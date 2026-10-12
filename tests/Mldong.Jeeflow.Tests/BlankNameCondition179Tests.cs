using System.Linq;
using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/179 R13 · getLastByName 空名／save 三腿缺 name：旧形状落仓储「空值当没填」通用放行
/// （MemoryRepository.ApplyConditions / MySqlRepository.BuildWhere）⇒ 条件整条丢弃——
/// getLastByName 退化全表返他行；deploy/designDeploy 版本号盗到别流程头上；designRedeploy 盗改他行内容。
///
/// 门面修复（与 Java 参考实现 598e0d4 同形）：空名 ⇒「缺少processDefineName参数」（对齐 rust/moon）；
/// deploy/designDeploy/designRedeploy 三腿缺 name ⇒「流程定义缺少 name」（对齐 go/py/node）。
/// </summary>
public class BlankNameCondition179Tests
{
    private const string NoNameFlow =
        """{"displayName": "缺名流程", "type": "approval", "nodes": [], "edges": []}""";

    private static (JeeflowFacade Facade, MemoryRepository Repo, ServiceContext Ctx) New()
    {
        var repo = new MemoryRepository();
        var ext = new MemoryExtRepository(repo, null);
        var ctx = new ServiceContext(repo, ext);
        ctx.Clock = new FixedClock(new DateTime(2026, 8, 1, 9, 0, 0));
        ctx.IdGenerator = new AtomicIdGenerator(1, ctx.Clock);
        ctx.UserProvider = new TestUserProvider();
        TestInfra.RegisterBuiltins(ctx);
        repo.Configure(ctx);
        return (new JeeflowFacade(ctx), repo, ctx);
    }

    /// <summary>判据①②③：缺键/空串/空白串三档一律错误信封；库里有他行时也不得返回任何行（后果档）。</summary>
    [Fact]
    public async Task GetLastByName_BlankName_Rejected_NotOtherRow()
    {
        var (facade, repo, _) = New();
        var other = await TestInfra.SaveFlowDefineAsync(repo, "other179", TestInfra.LoadFlow("01-simple"));

        var absent = await facade.FlowAsync("processDefine/getLastByName", new FlowData());
        var empty = await facade.FlowAsync("processDefine/getLastByName",
            new FlowData { ["processDefineName"] = "" });
        var blank = await facade.FlowAsync("processDefine/getLastByName",
            new FlowData { ["processDefineName"] = "   " });
        foreach (var r in new[] { absent, empty, blank })
        {
            Assert.Equal(99999999, Convert.ToInt32(r["code"]));
            Assert.Contains("缺少processDefineName参数", r["msg"]!.ToString());
            Assert.False(r.ContainsKey("data"), "空名档不得带 data（旧形状会把他行返回来）");
        }

        // 正常名回归：六键齐全、id 对上（挡住空名的同时不得误伤正常档）
        var ok = await facade.FlowAsync("processDefine/getLastByName",
            new FlowData { ["processDefineName"] = "other179" });
        Assert.Equal(0, Convert.ToInt32(ok["code"]));
        var data = (Dictionary<string, object?>)ok["data"]!;
        Assert.Equal(other, Convert.ToInt64(data["id"]));
        Assert.Equal("other179", data["name"]);
        Assert.NotNull(data["displayName"]);
        Assert.NotNull(data["type"]);
        Assert.NotNull(data["state"]);
        Assert.NotNull(data["version"]);
    }

    /// <summary>判据⑤（deploy 腿）：content 缺 name ⇒「流程定义缺少 name」，不得落新行。</summary>
    [Fact]
    public async Task Deploy_MissingName_Rejected_NoRow()
    {
        var (facade, _, _) = New();
        var before = await DefineCountAsync(facade);

        var r = await facade.FlowAsync("processDefine/deploy", new FlowData { ["content"] = NoNameFlow });
        Assert.Equal(99999999, Convert.ToInt32(r["code"]));
        Assert.Contains("流程定义缺少 name", r["msg"]!.ToString());
        Assert.Equal(before, await DefineCountAsync(facade));

        // 正常内容回归：合法 JSON 照常可发
        var ok = await facade.FlowAsync("processDefine/deploy",
            new FlowData { ["content"] = TestInfra.LoadFlow("01-simple") });
        Assert.Equal(0, Convert.ToInt32(ok["code"]));
        Assert.Equal(before + 1, await DefineCountAsync(facade));
    }

    /// <summary>判据⑤（designDeploy 腿）：设计内容缺 name ⇒ 报错，不得落行、不得置已部署。</summary>
    [Fact]
    public async Task DesignDeploy_MissingName_Rejected_NotDeployed()
    {
        var (facade, _, ctx) = New();
        var designId = await NewDesignAsync(facade, "nname179");
        var upd = await facade.FlowAsync("processDesign/updateDefine", new FlowData
        {
            ["processDesignId"] = designId,
            ["operator"] = "user1",
            ["content"] = NoNameFlow,
        });
        Assert.Equal(0, Convert.ToInt32(upd["code"]));

        var before = await DefineCountAsync(facade);
        var deployedBefore = (await ctx.ExtRepository!.FindDesignByIdAsync(designId))!.IsDeployed;

        var r = await facade.FlowAsync("processDesign/deploy",
            new FlowData { ["id"] = designId, ["operator"] = "user1" });
        Assert.Equal(99999999, Convert.ToInt32(r["code"]));
        Assert.Contains("流程定义缺少 name", r["msg"]!.ToString());
        Assert.Equal(before, await DefineCountAsync(facade));
        Assert.Equal(deployedBefore, (await ctx.ExtRepository!.FindDesignByIdAsync(designId))!.IsDeployed);
    }

    /// <summary>判据⑦（designRedeploy 腿，后果最重）：内容缺 name ⇒ 报错，且不得把缺名内容盗写到他流程定义行。</summary>
    [Fact]
    public async Task DesignRedeploy_MissingName_Rejected_NotHijackOtherRow()
    {
        var (facade, repo, ctx) = New();
        var victim = await TestInfra.SaveFlowDefineAsync(repo, "victim179", TestInfra.LoadFlow("01-simple"));
        var victimSnapshot = ((await repo.FindDefineByIdAsync(victim))!.Content!).ToArray();
        var victimVersion = (await repo.FindDefineByIdAsync(victim))!.Version;

        var designId = await NewDesignAsync(facade, "nnameRedeploy179");
        var upd = await facade.FlowAsync("processDesign/updateDefine", new FlowData
        {
            ["processDesignId"] = designId,
            ["operator"] = "user1",
            ["content"] = NoNameFlow,
        });
        Assert.Equal(0, Convert.ToInt32(upd["code"]));

        var before = await DefineCountAsync(facade);
        var r = await facade.FlowAsync("processDesign/redeploy",
            new FlowData { ["id"] = designId, ["operator"] = "user1" });
        Assert.Equal(99999999, Convert.ToInt32(r["code"]));
        Assert.Contains("流程定义缺少 name", r["msg"]!.ToString());
        var victimAfter = (await repo.FindDefineByIdAsync(victim))!.Content!;
        Assert.True(victimSnapshot.SequenceEqual(victimAfter), "他流程定义内容不得被盗改");
        Assert.Equal(victimVersion, (await repo.FindDefineByIdAsync(victim))!.Version);
        Assert.Equal(before, await DefineCountAsync(facade));
        Assert.Equal(0, (await ctx.ExtRepository!.FindDesignByIdAsync(designId))!.IsDeployed);
    }

    private static async Task<long> NewDesignAsync(JeeflowFacade facade, string name)
    {
        var r = await facade.FlowAsync("processDesign/save", new FlowData
        {
            ["name"] = name,
            ["displayName"] = "缺名" + name,
            ["type"] = "approval",
            ["operator"] = "user1",
        });
        Assert.Equal(0, Convert.ToInt32(r["code"]));
        return Convert.ToInt64(((Dictionary<string, object?>)r["data"]!)["id"]);
    }

    private static async Task<int> DefineCountAsync(JeeflowFacade facade)
    {
        var r = await facade.FlowAsync("processDefine/page",
            new FlowData { ["pageNum"] = 1, ["pageSize"] = 100 });
        Assert.Equal(0, Convert.ToInt32(r["code"]));
        var data = (Dictionary<string, object?>)r["data"]!;
        return Convert.ToInt32(data["recordCount"]);
    }
}

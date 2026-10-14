using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/213 · audit8 队列 R45（C99 §3.3 牙-4）——**写侧 operator 三档与审计列的判据钉**。
///
/// 为什么钉在本层：三档（缺键／空串／全空白）在 HTTP 面**结构性不可达**——14 个宿主面
/// （13 集成壳＋moon 插件）一律覆写式注入 `operator=当前登录人`，显式传参被丢弃
/// （boot4 `WfFlowController.java:54`；csharp 壳 `WfEndpoints.cs:71`；10-09 十三栈报告
/// readings.operator_injection 全为 shell-overwrite；audit8 假-11/R37 已把"14 宿主面同形"
/// 定为终局形状）。门禁侧按既有先例只登记明账（runner L2-15 `unreachable_cells`
/// 「只能由引擎栈内用例钉」＋ L2-56 同款指针），判据本体钉在**本层（T0）**。
///
/// 两落点口径（C5-L3 §2 现读表；对齐 Java 参考实现）：
/// ① 归一档 `OperatorArg`（processDesign/save 的 CreateUser/UpdateUser/快照 CreateUser）＝
///    `NormalizeActorValue`（trim＋空值折 null）`?? "user1"` ⇒ 缺键/空串/全空白 **同档回落 user1**；
///    数字按 InvariantCulture stringify（认数字＝java/go/py/node/php/csharp 六栈派；
///    rust/moon 不认数字是两栈派的反面——此处按本栈现行为钉，改口径须同步 spec 立法）。
/// ② `ToStr` 档（设计族 deploy/update/updateDefine/redeploy 与 define/redeploy 的审计列）＝
///    `ToStr(op, "system")` ⇒ **只在缺键/null 兜底 "system"**；显式 `""`/空白**原样落库**
///    （八栈同形、含 java，spec 未钉 ⇒ 本测试按现行为钉回归护栏，不做归一化"修正"）。
///
/// **不在本测试射程**：`processDefine/deploy`（含 design/deploy·design/redeploy 新建分支）
/// 生成定义的 create_user/update_user 落 **NULL**（php/csharp 两栈派，与 java 系六栈相反，
/// 方向未裁——C5-L3 §2 已记破口）⇒ 既不钉也不修，等立法。
/// </summary>
public class OperatorFormsWriteSide213Tests
{
    private static (JeeflowFacade Facade, MemoryRepository Repo, MemoryExtRepository Ext) New()
    {
        var repo = new MemoryRepository();
        var ext = new MemoryExtRepository(repo, null);
        var ctx = new ServiceContext(repo, ext);
        ctx.Clock = new FixedClock(new DateTime(2026, 8, 1, 9, 0, 0));
        ctx.IdGenerator = new AtomicIdGenerator(1, ctx.Clock);
        ctx.UserProvider = new TestUserProvider();
        TestInfra.RegisterBuiltins(ctx);
        repo.Configure(ctx);
        return (new JeeflowFacade(ctx), repo, ext);
    }

    private static async Task<long> SaveDesignAsync(
        JeeflowFacade facade, string name, params (string Key, object? Value)[] extra)
    {
        var args = new FlowData { ["name"] = name, ["displayName"] = name };
        foreach (var (k, v) in extra) args[k] = v;
        var r = await facade.FlowAsync("processDesign/save", args);
        Assert.Equal(0, Convert.ToInt32(r["code"]));
        return Convert.ToInt64(((Dictionary<string, object?>)r["data"]!)["id"]);
    }

    /// <summary>① 归一档：缺键/空串/全空白三档同落 user1；trim 与数字 stringify 两对照。</summary>
    [Fact]
    public async Task DesignSave_BlankForms_AllNormalizeToUser1()
    {
        var (facade, _, ext) = New();

        var absent = await SaveDesignAsync(facade, "o213-absent");
        var empty = await SaveDesignAsync(facade, "o213-empty", ("operator", ""));
        var blank = await SaveDesignAsync(facade, "o213-blank", ("operator", "   "));
        foreach (var id in new[] { absent, empty, blank })
        {
            var d = await ext.FindDesignByIdAsync(id);
            Assert.NotNull(d);
            Assert.Equal("user1", d!.CreateUser);
            Assert.Equal("user1", d.UpdateUser);
        }

        // 正向对照：显式值原样（trim 档）；数字按 InvariantCulture stringify（认数字派）
        var explicitId = await SaveDesignAsync(facade, "o213-explicit", ("operator", " zhangsan "));
        Assert.Equal("zhangsan", (await ext.FindDesignByIdAsync(explicitId))!.CreateUser);
        var numId = await SaveDesignAsync(facade, "o213-num", ("operator", 17301L));
        Assert.Equal("17301", (await ext.FindDesignByIdAsync(numId))!.CreateUser);
    }

    /// <summary>② 归一档的更新分支：无 content 的二次 save ⇒ UpdateUser 落归一值（不是 ToStr 档的 system）。</summary>
    [Fact]
    public async Task DesignSave_UpdateBranch_AlsoNormalizesToUser1()
    {
        var (facade, _, ext) = New();
        var id = await SaveDesignAsync(facade, "o213-upd", ("operator", "tester"));

        var r = await facade.FlowAsync("processDesign/save",
            new FlowData { ["id"] = id, ["displayName"] = "改后o213" });
        Assert.Equal(0, Convert.ToInt32(r["code"]));

        var d = await ext.FindDesignByIdAsync(id);
        Assert.Equal("tester", d!.CreateUser);       // 新建档不在本腿
        Assert.Equal("user1", d.UpdateUser);          // 更新档＝归一回落
    }

    /// <summary>③ 快照腿：his 行的 CreateUser 跟随归一后的 operator（缺键⇒user1；显式⇒原样）。</summary>
    [Fact]
    public async Task DesignSave_WithContent_SnapshotCreateUserFollowsNormalizedOperator()
    {
        var (facade, _, ext) = New();

        var absent = await SaveDesignAsync(facade, "o213-his-a", ("content", "{\"name\":\"o213-his-a\"}"));
        var hisA = await ext.ListDesignHisAsync(absent);
        Assert.NotEmpty(hisA);
        Assert.Equal("user1", hisA[0].CreateUser);

        var explicitUser = await SaveDesignAsync(facade, "o213-his-b",
            ("content", "{\"name\":\"o213-his-b\"}"), ("operator", "zhangsan"));
        var hisB = await ext.ListDesignHisAsync(explicitUser);
        Assert.NotEmpty(hisB);
        Assert.Equal("zhangsan", hisB[0].CreateUser);
    }

    /// <summary>④ ToStr 档（processDesign/update）：缺键⇒system；显式空串/空白原样；显式值原样。</summary>
    [Fact]
    public async Task DesignUpdate_SystemDefault_And_ExplicitValuesKeptAsIs()
    {
        var (facade, _, ext) = New();
        var id = await SaveDesignAsync(facade, "o213-sys");

        async Task UpdateWithAsync(params (string Key, object? Value)[] extra)
        {
            var args = new FlowData { ["id"] = id, ["displayName"] = "改后" };
            foreach (var (k, v) in extra) args[k] = v;
            var r = await facade.FlowAsync("processDesign/update", args);
            Assert.Equal(0, Convert.ToInt32(r["code"]));
        }

        await UpdateWithAsync();                                   // 缺键
        Assert.Equal("system", (await ext.FindDesignByIdAsync(id))!.UpdateUser);
        await UpdateWithAsync(("operator", ""));                   // 显式空串
        Assert.Equal("", (await ext.FindDesignByIdAsync(id))!.UpdateUser);
        await UpdateWithAsync(("operator", "   "));                // 空白串（ToStr 档不 trim）
        Assert.Equal("   ", (await ext.FindDesignByIdAsync(id))!.UpdateUser);
        await UpdateWithAsync(("operator", "zhangsan"));           // 显式值
        Assert.Equal("zhangsan", (await ext.FindDesignByIdAsync(id))!.UpdateUser);
    }
}

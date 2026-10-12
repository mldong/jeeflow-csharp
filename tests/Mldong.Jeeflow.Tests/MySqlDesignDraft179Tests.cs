using System.Text;
using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/179 R13-2（10-12 真机探针定案）· updateDefine 缺名草稿入库——SQL 腿判据：
/// 缺 name 时门面曾无条件 `design.Name = model.Name` ⇒ `name=NULL` 撞 wf_process_design.name
/// NOT NULL（MySql 1048），整个动作回滚、缺名快照永远存不进 ⇒ deploy/redeploy 拒收腿不可达。
/// 内存腿对 null 是 `?? stored.Name` 遮罩语义（变异不可观测）⇒ 判据必须落真库层：
/// ① 修后 updateDefine 缺名 content 返回 code=0（修前/摘守卫＝错误信封）；② 设计行 name 保持旧值；
/// ③ 缺名快照已入库且为最新一条（redeploy 读到它才会拒「流程定义缺少 name」——R13 轴⑤ 链路前提）。
/// </summary>
[Collection("mysql")]
[Trait("Category", "mysql-smoke")]
public class MySqlDesignDraft179Tests
{
    private const string NoNameFlow =
        """{"displayName": "缺名流程", "type": "approval", "nodes": [], "edges": []}""";

    private readonly MySqlFixture _fx;

    public MySqlDesignDraft179Tests(MySqlFixture fx) => _fx = fx;

    [Fact]
    public async Task R13UpdateDefine_MissingNameDraft_StoredNoCrash_NamePreserved()
    {
        var facade = new JeeflowFacade(_fx.Ctx);
        long? designId = null;
        try
        {
            var saved = await facade.FlowAsync("processDesign/save", new FlowData
            {
                ["name"] = "R13DraftSync179Mysql",
                ["displayName"] = "R13草稿入库",
                ["type"] = "6",
                ["operator"] = "user1",
            });
            Assert.Equal(0, Convert.ToInt32(saved["code"]));
            designId = Convert.ToInt64(((Dictionary<string, object?>)saved["data"]!)["id"]);

            var upd = await facade.FlowAsync("processDesign/updateDefine", new FlowData
            {
                ["processDesignId"] = designId.Value,
                ["operator"] = "user1",
                ["content"] = NoNameFlow,
            });
            Assert.Equal(0, Convert.ToInt32(upd["code"])); // 修前/摘守卫：99999999（SQL NOT NULL 崩）

            var design = await _fx.ExtRepo.FindDesignByIdAsync(designId.Value);
            Assert.Equal("R13DraftSync179Mysql", design!.Name); // name 保持旧值，不得被置 null

            var his = await _fx.ExtRepo.ListDesignHisAsync(designId.Value);
            Assert.True(his.Count >= 1, "缺名草稿快照应入库");
            Assert.Contains("缺名流程", Encoding.UTF8.GetString(his[0].Content!));
        }
        finally
        {
            if (designId.HasValue)
            {
                await _fx.ExtRepo.RemoveDesignAsync(designId.Value);
            }
        }
    }
}

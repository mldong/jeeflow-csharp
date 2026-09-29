using Xunit;
using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// 抄送分页归属条件必填（issues/141 G1 · C# 栈，内存仓储一路）。
///
/// <para>立法逐字依据＝spec 06-facade.md §2.5「抄送分页同一条尺子（owner 2026-09-29 拍）」：
/// <c>PageCcInstancesAsync</c> 这类"抄送我"取数入口，归属条件（<c>cc.actor_id</c>）<b>必填</b>——
/// 条件缺失或为空值时<b>返回空页</b>（<c>recordCount=0, rows=[]</c>），不得退化成"这条条件不加"
/// 而返回全部实例。</para>
///
/// <para>本案的原始症状正是"同一栈两个仓储两个答案"：内存仓储只放"有 cc 行的实例"（inner 语义），
/// 而 MySQL 仓储的 <c>LEFT JOIN wf_process_cc_instance</c> 不带条件时返回<b>全部实例</b>。
/// 判据同时钉在两仓上（issues/117 场景 27 那把尺子扩到 ccList）——MySQL 一路见
/// <see cref="MySqlCcOwnershipIdempotent141Tests"/>，两仓每一格读数必须相等，判据本体收口在
/// <see cref="PageQuery.HasEffectiveCondition"/>（一处定义、两仓共用）。</para>
///
/// <para>门面路径本身不受影响（<c>processInstance/ccList</c> 恒挂 <c>cc.actor_id EQ operator</c>，
/// 空串按 issues/129 第一层回落缺省 user1），这里打的是<b>直连仓储</b>那一档：调用方绕过门面、
/// 或下一版门面改动漏挂条件时，仓储这一层必须自己顶住。</para>
///
/// <para>"非归属列的空值放行不变"由 <see cref="BlankNonOwnershipConditionIsStillIgnored"/> 守：
/// <c>m_LIKE_*</c> 传空串仍按"没填"忽略，不许一并改成空页（issues/129 同一条边界）。</para>
/// </summary>
public class CcPageOwnership141Tests
{
    private const string MinimalFlow = """
        {"name": "cc-owner-141", "displayName": "抄送归属流程", "type": "approval",
         "nodes": [
           {"id": "start", "type": "snaker:start", "text": {"value": "开始"}},
           {"id": "approval", "type": "snaker:task", "text": {"value": "审批"},
            "properties": {"form": "f", "assignee": "leader", "taskType": 0, "performType": 0}},
           {"id": "end", "type": "snaker:end", "text": {"value": "结束"}}
         ],
         "edges": [
           {"id": "e1", "sourceNodeId": "start", "targetNodeId": "approval"},
           {"id": "e2", "sourceNodeId": "approval", "targetNodeId": "end"}
         ]}
        """;

    private sealed record Fixture(MemoryRepository Repo, long Mine, long Theirs);

    /// <summary>
    /// 一个内存仓里放两条实例：一条抄送给 user1（"我的"）、一条抄送给 user2（"别人的"）。
    /// 直写仓储不经引擎，把这一格钉在"分页判据"上而不是抄送流程上。
    /// </summary>
    private static async Task<Fixture> SeedAsync()
    {
        var (_, repo) = TestInfra.NewEngine();
        var define = new ProcessDefine
        {
            Name = "cc-owner-141",
            DisplayName = "抄送归属流程",
            Type = "approval",
            State = 1,
            Content = System.Text.Encoding.UTF8.GetBytes(MinimalFlow),
            Version = 1,
        };
        await repo.SaveDefineAsync(define);
        var mine = await NewInstanceCcToAsync(repo, define, "user1");
        var theirs = await NewInstanceCcToAsync(repo, define, "user2");
        return new Fixture(repo, mine, theirs);
    }

    private static async Task<long> NewInstanceCcToAsync(
        MemoryRepository repo, ProcessDefine define, string actorId)
    {
        // business_no 给非空值：空值列在任何条件上都是 SQL 三值逻辑的"恒不命中"档，
        // 会让"非归属条件空值仍被忽略"那一格的对照在两仓各说各话（既有形状，非本案范围）。
        var inst = ProcessInstance.Create(define, "zhangsan",
            new FlowData { [FlowConst.BusinessNo] = "CC141-" + actorId });
        await repo.SaveInstanceAsync(inst);
        await repo.CreateCcInstanceAsync(inst.InstanceId!.Value, "zhangsan", actorId);
        return inst.InstanceId.Value;
    }

    // ═══ 正向对照 ═══

    /// <summary>正向对照：带归属条件时照旧只出"我的"那一页。</summary>
    [Fact]
    public async Task CcPageWithOwnershipConditionReturnsOnlyMine()
    {
        var fx = await SeedAsync();

        var page = await fx.Repo.PageCcInstancesAsync(new PageQuery(1, 50).Add("cc.actor_id", "EQ", "user1"));

        Assert.Equal(1, page.RecordCount);                 // rows 数与 recordCount 同口径
        Assert.Single(page.Rows);
        Assert.Equal(fx.Mine, page.Rows[0].Id);            // 命中的应是我的实例
        Assert.NotEqual(fx.Theirs, page.Rows[0].Id);       // 别人的实例不该串进来
    }

    // ═══ 缺陷档：条件整条没给 ⇒ 空页 ═══

    /// <summary>
    /// 缺陷档：整条归属条件都不给 ⇒ <b>空页</b>。
    /// 改前这一格是红的——内存仓返回"所有有 cc 行的实例"（2 条），
    /// 而它的 MySQL 仓兄弟返回全部实例，同一栈两仓两个答案。
    /// </summary>
    [Fact]
    public async Task CcPageWithoutOwnershipConditionIsEmptyPage()
    {
        var fx = await SeedAsync();
        Assert.NotEqual(fx.Mine, fx.Theirs);

        var noCondition = await fx.Repo.PageCcInstancesAsync(new PageQuery(1, 50));
        Assert.Equal(0, noCondition.RecordCount);   // 缺归属条件必须返回空页，而不是所有有 cc 行的实例
        Assert.Empty(noCondition.Rows);             // 空页的 rows 也必须是空集合

        var bareQuery = await fx.Repo.PageCcInstancesAsync(new PageQuery());
        Assert.Equal(0, bareQuery.RecordCount);     // 默认分页参数同样缺归属条件 ⇒ 空页
    }

    /// <summary>空值四形（空串 / 全空白 / null / 空集合）与"条件整条缺失"同档。</summary>
    [Fact]
    public async Task BlankOwnershipConditionIsAlsoEmptyPage()
    {
        var fx = await SeedAsync();

        Assert.Equal(0, (await fx.Repo.PageCcInstancesAsync(
            new PageQuery(1, 50).Add("cc.actor_id", "EQ", ""))).RecordCount);
        Assert.Equal(0, (await fx.Repo.PageCcInstancesAsync(
            new PageQuery(1, 50).Add("cc.actor_id", "EQ", "   "))).RecordCount);
        Assert.Equal(0, (await fx.Repo.PageCcInstancesAsync(
            new PageQuery(1, 50).Add("cc.actor_id", "EQ", null))).RecordCount);
        Assert.Equal(0, (await fx.Repo.PageCcInstancesAsync(
            new PageQuery(1, 50).Add("cc.actor_id", "IN", new List<string>()))).RecordCount);
    }

    /// <summary>
    /// 改动面哨兵：只收归属谓词，不改 <c>PageQuery</c> 对可选过滤空值的通用放行
    /// （<c>m_LIKE_*</c> 传空串按"没填"处理是对的，issues/129 同一条边界）。
    /// </summary>
    [Fact]
    public async Task BlankNonOwnershipConditionIsStillIgnored()
    {
        var fx = await SeedAsync();
        Assert.True(fx.Mine > 0);

        var page = await fx.Repo.PageCcInstancesAsync(new PageQuery(1, 50)
            .Add("cc.actor_id", "EQ", "user1")
            .Add("t.business_no", "LIKE", ""));

        Assert.Equal(1, page.RecordCount);   // 空值非归属条件应被忽略，归属条件照常生效
    }

    /// <summary>
    /// 判据本体：内存仓与 MySQL 仓共用 <see cref="PageQuery.HasEffectiveCondition"/>——
    /// "有效条件"＝值非 null、字符串非全空白、集合非空。这一格钉判据本身，
    /// 两仓的分页读数都由它决定，判据红了则两仓同时红（不会只修一面）。
    /// </summary>
    [Fact]
    public void HasEffectiveConditionJudgesTheThreeBlankShapesAsMissing()
    {
        Assert.False(PageQuery.HasEffectiveCondition(null, "cc.actor_id"));
        Assert.False(PageQuery.HasEffectiveCondition(new PageQuery(1, 50), "cc.actor_id"));
        Assert.False(PageQuery.HasEffectiveCondition(
            new PageQuery(1, 50).Add("cc.actor_id", "EQ", null), "cc.actor_id"));
        Assert.False(PageQuery.HasEffectiveCondition(
            new PageQuery(1, 50).Add("cc.actor_id", "EQ", ""), "cc.actor_id"));
        Assert.False(PageQuery.HasEffectiveCondition(
            new PageQuery(1, 50).Add("cc.actor_id", "EQ", "  \t "), "cc.actor_id"));
        Assert.False(PageQuery.HasEffectiveCondition(
            new PageQuery(1, 50).Add("cc.actor_id", "IN", new List<string>()), "cc.actor_id"));

        Assert.True(PageQuery.HasEffectiveCondition(
            new PageQuery(1, 50).Add("cc.actor_id", "EQ", "user1"), "cc.actor_id"));
        Assert.True(PageQuery.HasEffectiveCondition(
            new PageQuery(1, 50).Add("cc.actor_id", "IN", new List<string> { "user1" }), "cc.actor_id"));
        // 别的列给了有效条件，不算这一列有条件
        Assert.False(PageQuery.HasEffectiveCondition(
            new PageQuery(1, 50).Add("t.operator", "EQ", "user1"), "cc.actor_id"));
    }
}

namespace Mldong.Jeeflow.Tests;

using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;
using Xunit;

/// <summary>
/// spec 04 保留键 × issues/82-3 回退（<b>内容级</b>判据，门面出口视角）。
///
/// 建单现在往每条任务变量写 <c>_branchCtx</c>（空分支也写 <c>[]</c>），两枚直返保留键必须算进
/// 「引擎控制键」，否则 ① 每条任务行从此不再回退实例变量（前端在任务行上拿不到
/// f_*/u_*/autoGenTitle），② 内部键直接从公开出口透出。本轮八栈齐漏——只有 php 的 PDO 仓有
/// 内容级断言才报红，其余七栈的既有 82-3 用例只断"容器存在"，照不出回退有没有真发生。
///
/// 但 <c>_reapplyContext</c> 是前端「重新提交后返回：X」的唯一取数口（jeeflow-ui helpers.ts 读
/// ext._reapplyContext）⇒ 回退后原样合回，且不得污染同一响应的 instanceExt 出口。
/// </summary>
public class TaskRowExtFallbackRtsTests
{
    private static FlowData Submit(int type) => new() { [FlowConst.SubmitType] = type };

    private static (JeeflowEngine Engine, MemoryRepository Repo, JeeflowFacade Facade) New()
    {
        var repo = new MemoryRepository();
        var ext = new MemoryExtRepository(repo, null);
        var ctx = new ServiceContext(repo, ext);
        ctx.Clock = new FixedClock(new DateTime(2026, 8, 1, 9, 0, 0));
        ctx.IdGenerator = new AtomicIdGenerator(1, ctx.Clock);
        ctx.UserProvider = new TestUserProvider();
        TestInfra.RegisterBuiltins(ctx);
        repo.Configure(ctx);
        return (new JeeflowEngine(ctx), repo, new JeeflowFacade(ctx));
    }

    private static async Task<List<Dictionary<string, object?>>> TodoRowsAsync(
        JeeflowFacade facade, string operatorId)
    {
        var resp = await facade.FlowAsync("processTask/todoList",
            new FlowData { ["operator"] = operatorId, ["pageSize"] = 100 });
        Assert.Equal(0, Convert.ToInt32(resp["code"]));
        var data = (Dictionary<string, object?>)resp["data"]!;
        return ((List<object?>)data["rows"]!)
            .Select(r => (Dictionary<string, object?>)r!).ToList();
    }

    private static Dictionary<string, object?> MapOf(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) && v is Dictionary<string, object?> m
            ? m
            : new Dictionary<string, object?>();

    [Fact]
    public async Task TaskRowExtFallsBackWithReapplyReservedKeys()
    {
        var (engine, repo, facade) = New();

        // ── ① 普通任务行：变量只剩 isFirstTaskNode + _branchCtx ⇒ ext 恰为实例变量 ──
        var simple = await TestInfra.SaveFlowDefineAsync(repo, "rtsExt14a", TestInfra.LoadFlow("01-simple"));
        var iid1 = await TestInfra.StartAndApplyAsync(engine, repo, simple,
            new FlowData { ["f_reason"] = "unit-test" });
        var rows1 = await TodoRowsAsync(facade, "leader");
        Assert.NotEmpty(rows1);
        var ext1 = MapOf(rows1[0], "ext");
        var iExt1 = MapOf(rows1[0], "instanceExt");
        Assert.NotEmpty(iExt1);   // 前置：非空才谈得上"回退有没有真发生"
        Assert.False(ext1.ContainsKey(ReapplySupport.VarBranchCtx),
            "内部键 _branchCtx 不得从任务行 ext 透出（spec 04 保留键）：" + string.Join(",", ext1.Keys));
        Assert.False(ext1.ContainsKey(ReapplySupport.VarReapplyCtx),
            "没有直返上下文时不得凭空多出 _reapplyContext");
        Assert.True(ext1.Keys.OrderBy(k => k, StringComparer.Ordinal)
            .SequenceEqual(iExt1.Keys.OrderBy(k => k, StringComparer.Ordinal)),
            $"任务变量只剩控制键 ⇒ ext 应恰为实例变量（issues/82-3 / 121 P1）：ext=[{string.Join(",", ext1.Keys)}] "
            + $"instanceExt=[{string.Join(",", iExt1.Keys)}]");

        // ── ② 直返申请待办：ext = 实例变量 + _reapplyContext，且不带 _branchCtx ──
        var rts = await TestInfra.SaveFlowDefineAsync(repo, "rtsExt14", TestInfra.LoadFlow("14-return-to-source"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, rts,
            new FlowData { ["f_reason"] = "unit-test" });
        var taskA = await TestInfra.FindDoingForAsync(repo, iid, "userA");
        Assert.Equal("taskA", taskA.TaskName);
        await engine.ExecuteAndJumpToFirstTaskNodeAsync(taskA.TaskId!.Value, "userA",
            Submit((int)WfSubmitType.RollbackToOperator));

        var rows2 = await TodoRowsAsync(facade, "applicant");
        var marked = rows2
            .Where(r => MapOf(r, "ext").ContainsKey(ReapplySupport.VarReapplyCtx))
            .ToList();
        Assert.Single(marked);   // 直返申请待办的 ext 必须带 _reapplyContext（前端提示的取数口）
        var ext2 = MapOf(marked[0], "ext");
        var iExt2 = MapOf(marked[0], "instanceExt");
        foreach (var k in iExt2.Keys)
            Assert.True(ext2.ContainsKey(k),
                $"回退仍要发生：实例变量键 {k} 应在 ext 上 → ext=[{string.Join(",", ext2.Keys)}]");
        Assert.False(ext2.ContainsKey(ReapplySupport.VarBranchCtx),
            "内部键 _branchCtx 不得从 ext 透出：" + string.Join(",", ext2.Keys));
        var ctx = Assert.IsType<Dictionary<string, object?>>(ext2[ReapplySupport.VarReapplyCtx]);
        Assert.Equal("taskA", ctx.TryGetValue("sourceNode", out var sn) ? sn?.ToString() : null);
        Assert.False(iExt2.ContainsKey(ReapplySupport.VarReapplyCtx),
            "合回不得污染同一响应里的 instanceExt 出口：" + string.Join(",", iExt2.Keys));
    }
}

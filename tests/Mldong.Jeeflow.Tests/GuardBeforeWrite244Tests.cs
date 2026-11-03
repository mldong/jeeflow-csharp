using Xunit;
using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/244 两处「先写后判」缺陷的判据（引擎状态变更必须排在判据之后）。
///
/// <para><b>病根（同一族）</b>：引擎先做状态变更（当前任务行办结 10→20、写实例、fire 事件），
/// 之后才检查前提条件；不满足就抛错。真库有外层事务整笔回滚，看不出差别；内存仓储无回滚
/// ⇒ 报错那次「当前任务已 DONE、新任务没建」⇒ 实例 DOING(10) 却零待办，流程失联。</para>
///
/// <list type="number">
/// <item><b>①</b> 节点后置拦截器的<b>名字解析</b>原先排在写之后：<c>RunInterceptorsAsync</c> 在
/// <c>node.ExecuteAsync</c>（post 档）里才按名解析，而引擎 <c>PrepareExecutionAsync</c> 早把当前行
/// 办结了。修法＝把「声明名可解析」抽成 <c>NodeModel.ValidateDeclaredInterceptors</c> 纯读校验，
/// 由引擎排在 <c>CompleteTask</c> 之前。</item>
/// <item><b>②</b> 串行会签零候选人：<c>actorIds[0]</c> 越界。跨栈已定语义（go/python/node/moon 现行）＝
/// <b>建一行、不挂任何参与者、引擎不报错</b>。</item>
/// </list>
///
/// <para>两条都咬「报错／成功后到底写了什么」，不看异常文案之外的东西——文案沿用原来那一句
/// （<c>无法实例化拦截器: {name}</c>），不新增错误码。</para>
/// </summary>
public class GuardBeforeWrite244Tests
{
    // ═══════════════════════════════════════════════════════════════════
    // ① 节点拦截器名字解析前置：报错即零写入
    // ═══════════════════════════════════════════════════════════════════

    private const string GhostName = "com.mldong.jeeflow.ghost.GhostInterceptor";

    /// <summary>start → apply(节点级 post 声明幽灵名) → approval → end。</summary>
    private static string GhostPostFlow() => """
        {"name": "__I244_GHOST__", "displayName": "244 幽灵后置拦截器", "type": "approval",
         "preInterceptors": "", "postInterceptors": "",
         "nodes": [
           {"id": "start", "type": "snaker:start", "text": {"value": "开始"}},
           {"id": "apply", "type": "snaker:task", "text": {"value": "申请"},
            "properties": {"assignee": "applicant", "taskType": 0, "performType": 0,
             "postInterceptors": "__GHOST__"}},
           {"id": "approval", "type": "snaker:task", "text": {"value": "审批"},
            "properties": {"assignee": "leader", "taskType": 0, "performType": 0}},
           {"id": "end", "type": "snaker:end", "text": {"value": "结束"}}
         ],
         "edges": [
           {"id": "e1", "sourceNodeId": "start", "targetNodeId": "apply"},
           {"id": "e2", "sourceNodeId": "apply", "targetNodeId": "approval"},
           {"id": "e3", "sourceNodeId": "approval", "targetNodeId": "end"}
         ]}
        """.Replace("__GHOST__", GhostName);

    [Fact]
    public async Task I244_01_GhostPostInterceptorRejectsWithZeroWrites()
    {
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        // 注册表里只有一个合法名；apply 声明的那个不在其中（"取不到 ⇒ 显式报错"那一档）
        ctx.NamedInterceptors.Clear();
        var defineId = await TestInfra.SaveFlowDefineAsync(repo, "i244-ghost-post", GhostPostFlow());

        var inst = await engine.StartProcessInstanceByIdAsync(defineId, "applicant", new FlowData());
        var iid = inst.InstanceId!.Value;
        var before = Assert.Single(await repo.FindDoingTasksAsync(iid, null));
        Assert.Equal("apply", before.TaskName);                      // 前置：待办确实建在 apply 上
        Assert.Equal((int)WfTaskState.Doing, before.TaskState);
        var originalTaskId = before.TaskId!.Value;
        var rowsBefore = (await repo.FindHistoryTasksAsync(iid)).Count;

        var ex = await Assert.ThrowsAsync<JeeflowException>(
            () => engine.ExecuteProcessTaskAsync(originalTaskId, "applicant", new FlowData()));
        Assert.Contains("无法实例化拦截器", ex.Message);
        Assert.Contains(GhostName, ex.Message);

        // ── 报错零写入 ──
        var afterTask = (await repo.FindTaskByIdAsync(originalTaskId))!;
        Assert.Equal((int)WfTaskState.Doing, afterTask.TaskState);   // 原任务仍 DOING（不是被静默办结）
        Assert.Equal(originalTaskId, afterTask.TaskId);              // id 未变
        var instAfter = (await repo.FindInstanceByIdAsync(iid))!;
        Assert.Equal((int)WfInstanceState.Doing, instAfter.State);   // 实例状态不变
        Assert.Equal(rowsBefore, (await repo.FindHistoryTasksAsync(iid)).Count); // 任务行总数不变
        // 失联形状的反面：实例 DOING 且仍有恰好一条待办（旧形状这里会变成 0 条）
        var doing = await repo.FindDoingTasksAsync(iid, null);
        Assert.Single(doing);
        Assert.Equal(originalTaskId, doing[0].TaskId);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ② 串行会签零候选人：建一行、不挂人、不报错
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>零候选人解析器（跨栈基准："建一行不挂人"）。</summary>
    private sealed class EmptyAssignmentHandler : IAssignmentHandler
    {
        public Task<string?> AssignAsync(Execution execution) => Task.FromResult<string?>(null);
    }

    /// <summary>start → apply(applicant) → cs(串行会签, assignmentHandler 返回空) → end。</summary>
    private static string ZeroCandidateSequentialFlow() => """
        {"name": "__I244_ZERO__", "displayName": "244 串行会签零候选人", "type": "approval",
         "nodes": [
           {"id": "start", "type": "snaker:start", "text": {"value": "开始"}},
           {"id": "apply", "type": "snaker:task", "text": {"value": "申请"},
            "properties": {"assignee": "applicant", "taskType": 0, "performType": 0}},
           {"id": "cs", "type": "snaker:task", "text": {"value": "串行会签"},
            "properties": {"taskType": 0, "performType": 1, "countersignType": "SEQUENTIAL",
             "assignmentHandler": "__I244_EMPTY__"}},
           {"id": "end", "type": "snaker:end", "text": {"value": "结束"}}
         ],
         "edges": [
           {"id": "e1", "sourceNodeId": "start", "targetNodeId": "apply"},
           {"id": "e2", "sourceNodeId": "apply", "targetNodeId": "cs"},
           {"id": "e3", "sourceNodeId": "cs", "targetNodeId": "end"}
         ]}
        """.Replace("__I244_EMPTY__", "i244.empty");

    [Fact]
    public async Task I244_02_SequentialCountersignZeroCandidatesCreatesOneBareRow()
    {
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        ctx.RegisterAssignmentHandler("i244.empty", new EmptyAssignmentHandler());
        var defineId = await TestInfra.SaveFlowDefineAsync(repo, "i244-zero", ZeroCandidateSequentialFlow());

        var inst = await engine.StartProcessInstanceByIdAsync(defineId, "applicant", new FlowData());
        var iid = inst.InstanceId!.Value;
        var apply = Assert.Single(await repo.FindDoingTasksAsync(iid, null));
        Assert.Equal("apply", apply.TaskName);

        // 旧形状：CreateCountersignTasks 的 actorIds[0] 越界 ⇒ 抛 ArgumentOutOfRangeException
        await engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData());

        var doing = await repo.FindDoingTasksAsync(iid, null);
        var row = Assert.Single(doing);                              // 恰好 1 行
        Assert.Equal("cs", row.TaskName);
        Assert.Equal((int)WfTaskState.Doing, row.TaskState);          // 是 DOING（不是办结也不是缺失）
        Assert.Equal(WfPerformType.Countersign, row.PerformType);     // PerformType 仍是会签
        Assert.Empty(row.ActorIds);                                   // 不挂任何参与者
        Assert.Empty(await repo.FindTaskActorsAsync(row.TaskId!.Value)); // task_actor 零行
        // 三枚会签任务变量照旧写：名册空、计数器 0、实例数 0
        var roster = row.Variables.GetObj($"{FlowConst.CountersignOperatorList}_cs");
        var rosterItems = roster is System.Collections.ICollection coll ? coll.Count : -1;
        Assert.Equal(0, rosterItems);
        Assert.Equal(0, row.Variables.GetInt($"{FlowConst.LoopCounter}_cs"));
        Assert.Equal(0, row.Variables.GetInt($"{FlowConst.NrOfInstances}_cs"));
    }
}

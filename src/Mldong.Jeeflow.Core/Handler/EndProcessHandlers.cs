namespace Mldong.Jeeflow.Core;

/// <summary>
/// 结束流程实例处理器（对齐 Java EndProcessHandler）：
/// submitType=2 REJECT → 实例 45，否则 FINISHED(20)；办结/拒绝两路都登记结束事件（码 2），
/// 由引擎在实例行落库之后统一 fire（C12/C13 ＋ spec §11.2 原则 3）；
/// 父流程挂载点：子流程结束后驱动父流程子流程节点，父实例的待播登记随任务一起上收。
/// </summary>
public class EndProcessHandler : IHandler
{
    private readonly EndModel _endModel;

    public EndProcessHandler(EndModel endModel) => _endModel = endModel;

    public async Task HandleAsync(Execution execution)
    {
        var submitType = execution.Args.GetInt(FlowConst.SubmitType, (int)WfSubmitType.Agree);
        var instance = execution.ProcessInstance!;
        if (submitType == (int)WfSubmitType.Reject)
        {
            instance.Reject(execution.Context.ClockOrDefault);
        }
        else
        {
            instance.Finish(execution.Context.ClockOrDefault);
        }

        // 实例终态事件（spec §11.3 码 2 PROCESS_INSTANCE_END）：办结 20 与拒绝 45 共用这一支，
        // 规范名不拆，靠载荷 state 分（§11.2 原则 2「码粗、载荷细」＋ §11.6 收口口径）。
        //
        // **只登记、不就地 fire**（§11.2 原则 3／08-compliance 场景 32「state 落库之后」）：
        // 上面 Finish()/Reject() 只改了内存聚合根，实例那一行要等调用方——
        // JeeflowEngine.PersistTasksAsync（或发起路径）的 UpdateInstanceAsync——才落库。
        // 在这里 fire 就是"先播后写"，监听器（站内信反查、待办角标、persist 回写）当下
        // 反查实例读到的是旧 state（本轮红基线实测：载荷承诺 20/45，那一行仍是 10），
        // issues/121／issues/126 两轮"回写序"教训的同一族。
        // 真正的 fire 收口在 JeeflowEngine.FlushInstanceEndEventsAsync；载荷键唯一形状见
        // ProcessPublisher.NotifyInstanceEndAsync（instanceId ＋ 落库后的 state）。
        execution.AddPendingEnd(new PendingInstanceEnd(
            execution.ProcessInstanceId, instance.State, instance));

        // 子流程：如果当前流程有父流程，则继续执行父流程的子流程节点
        if (instance.ParentId != null)
        {
            var repo = execution.Engine?.Repository;
            if (repo == null) return;
            var parentInstance = await repo.FindInstanceByIdAsync(instance.ParentId);
            if (parentInstance == null) return;
            var parentDefine = await repo.FindDefineByIdAsync(parentInstance.DefineId);
            if (parentDefine?.Content == null) return;
            var pm = ModelParser.Parse(parentDefine.Content, execution.Context);
            if (pm == null) return;
            if (pm.GetNode(instance.ParentNodeName) is not SubProcessModel spm) return;
            var newExec = new Execution
            {
                Engine = execution.Engine,
                ProcessModel = pm,
                ProcessInstance = parentInstance,
                ProcessInstanceId = parentInstance.InstanceId,
                Args = execution.Args,
                Operator = execution.Operator,
                Context = execution.Context,
            };
            await spm.ExecuteAsync(newExec);
            execution.AddTasks(newExec.ProcessTaskList);
            // 父实例若被这一支流转带到终态，**它的**子流程节点会再进一次本处理器，
            // 登记挂在 newExec 上；newExec 是这里的局部对象，随即丢弃 ⇒ 待播事件
            // 必须与任务一起上收到外层 execution（同 AddTasks 那条腿），漏一行
            // 就是"父实例终态事件整支丢掉"。
            execution.AddPendingEnds(newExec.PendingEnds);
        }
    }
}

/// <summary>
/// 合并分支处理器（对齐 Java MergeBranchHandler）：无进行中任务 → merged；
/// 否则检查本合并节点输入分支是否还有进行中任务。
/// </summary>
public class MergeBranchHandler : IHandler
{
    private readonly JoinModel _joinModel;

    public MergeBranchHandler(JoinModel joinModel) => _joinModel = joinModel;

    public Task HandleAsync(Execution execution)
    {
        var instance = execution.ProcessInstance!;
        var doingTasks = instance.GetDoingTasks();
        execution.IsMerged = doingTasks.Count == 0;

        if (!execution.IsMerged)
        {
            var hasActive = false;
            foreach (var input in _joinModel.Inputs)
            {
                var sourceName = input.Source?.Name;
                if (sourceName == null) continue;
                if (doingTasks.Any(t => sourceName == t.TaskName))
                {
                    hasActive = true;
                    break;
                }
            }
            execution.IsMerged = !hasActive;
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// 启动子流程处理器（对齐 Java StartSubProcessHandler）。
/// 注意：Java 参考实现此处传 null defineId（死路径，共享 15 flows 无子流程节点）——
/// C# 保持等价契约：按 nodeName 查定义失败时抛"没有流程定义"。
/// </summary>
public class StartSubProcessHandler : IHandler
{
    private readonly SubProcessModel _subProcessModel;

    public StartSubProcessHandler(SubProcessModel subProcessModel) => _subProcessModel = subProcessModel;

    public async Task HandleAsync(Execution execution)
    {
        var engine = execution.Engine;
        if (engine == null) return;
        var child = await engine.StartProcessInstanceByIdAsync(
            null,
            execution.Operator,
            execution.Args,
            execution.ProcessInstanceId,
            _subProcessModel.Name);
        if (child != null) execution.AddTasks(child.GetDoingTasks());
    }
}

namespace Mldong.Jeeflow.Core;

/// <summary>
/// 结束流程实例处理器（对齐 Java EndProcessHandler）：
/// submitType=2 REJECT → 实例 45，否则 FINISHED(20)；办结/拒绝两路都 fire 结束事件（C12/C13）；
/// 父流程挂载点：子流程结束后驱动父流程子流程节点。
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

        // 发布流程结束事件
        await ProcessPublisher.NotifyAsync(
            new ProcessEvent
            {
                EventType = ProcessEventType.ProcessInstanceEnd,
                SourceId = execution.ProcessInstanceId,
            },
            execution.Context.EventListeners);

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

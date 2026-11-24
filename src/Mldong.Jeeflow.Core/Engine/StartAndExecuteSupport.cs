namespace Mldong.Jeeflow.Core;

/// <summary>
/// 子流程与顶层 startAndExecute 共用的自办、参数清洗及定义定位逻辑（issues/257）。
/// 子流程命令在父命令的事务/命令闸内直接走引擎内部腿，不重入全局 SemaphoreSlim。
/// </summary>
public static class StartAndExecuteSupport
{
    public static string? RealOperator(string? op, ProcessInstance? parent)
    {
        if (parent != null
            && (string.Equals(FlowConst.AutoId, op, StringComparison.OrdinalIgnoreCase)
                || string.Equals(FlowConst.AdminId, op, StringComparison.OrdinalIgnoreCase)))
            return parent.Operator;
        return op;
    }

    /// <summary>复制父业务参数，剥掉父提交动作、预指派人及流程内部控制键。</summary>
    public static FlowData ChildArgs(FlowData? parentArgs)
    {
        var args = parentArgs?.Copy() ?? new FlowData();
        args.Remove(FlowConst.SubmitType);
        args.Remove(FlowConst.ProcessStartNextNodeOperator);
        args.Remove(FlowConst.NextNodeOperator);
        args.Remove(ReapplySupport.VarBranchCtx);
        args.Remove(ReapplySupport.VarReapplyCtx);
        foreach (var key in args.Keys.ToList())
            if (key.StartsWith(FlowConst.TaskFormDataPrefix, StringComparison.Ordinal))
                args.Remove(key);
        return args;
    }

    /// <summary>
    /// 自动办理子流程启动时产生的 DOING 快照：真实发起人加入参与者后，以 flow.auto + submitType=0 自办。
    /// 必须使用快照；自办推进会创建后继任务，后继不能在同一次循环里被继续自办。
    /// </summary>
    public static async Task<List<ProcessTask>> ApplyInitialTasksAsync(
        JeeflowEngine engine, long instanceId, string? applicant, FlowData args)
    {
        var doing = await engine.Repository.FindDoingTasksAsync(instanceId, null);
        var result = new List<ProcessTask>();
        foreach (var task in doing)
        {
            if (task.TaskId == null) continue;
            await engine.Repository.AddTaskActorAsync(task.TaskId.Value,
                applicant == null ? new List<string>() : new List<string> { applicant });
            var applyArgs = args.Copy();
            applyArgs[FlowConst.SubmitType] = (int)WfSubmitType.Apply;
            var next = PageQuery.NormalizeActors(applyArgs.GetObj(FlowConst.ProcessStartNextNodeOperator));
            if (next.Count > 0) applyArgs[FlowConst.NextNodeOperator] = next;
            result.AddRange(await engine.ExecuteProcessTaskInTxAsync(
                task.TaskId.Value, FlowConst.AutoId, applyArgs));
        }
        return result;
    }

    /// <summary>只读校验直接出边上的子流程定义，供办理写前置守卫调用。</summary>
    public static async Task ValidateSubProcessTargetAsync(
        ProcessModel model, NodeModel? current, IProcessRepository repository)
    {
        if (current == null) return;
        foreach (var transition in current.Outputs)
        {
            if (transition.Target is not SubProcessModel sub) continue;
            if (await repository.FindDefineByNameAndVersionAsync(sub.ProcessName, sub.Version) == null)
                throw new JeeflowException(WfErr.NotFoundProcessDefine,
                    "子流程" + sub.ProcessName + "定义不存在");
        }
    }
}

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
            // 同一条收口腿的 issues/142 档：父实例这一支若命中 custom（记录类）节点，
            // 它的历史行挂在 newExec.HistoryTasks 上 ⇒ 不并上来就是"父流程的留痕行永远进不了库"。
            // 这条与 AddTasks/AddPendingEnds 两腿平行，缺一不可（newExec 出了本方法就是孤儿对象）。
            execution.AddHistoryTasks(newExec.HistoryTasks);
        }
    }
}

/// <summary>
/// 合并分支处理器（对齐 Java MergeBranchHandler）——判据分两档：
/// <list type="bullet">
///   <item><b>有分支归属</b>（到达任务变量 <c>_branchCtx</c> 含本汇合点）：按本轮 round 统计全部分支根，
///       任一根下仍有进行中任务、或任一根有进行中的申请待办携带匹配的修改等待，都不放行。
///       申请人回到分叉之外修改时，那条申请任务不叫任何分支节点名，靠等待关系仍被算作
///       「该分支未完成」——这是旧的按节点名扫描做不到的。</item>
///   <item><b>无分支归属</b>（存量任务 / 未配 fork–join 的流）：回落旧判据——实例无进行中任务，
///       或输入源节点无进行中任务。</item>
/// </list>
/// <para>一次性放行由调用方在实例锁内完成（<see cref="InstanceLock"/>），本处理器只做判定。</para>
/// </summary>
public class MergeBranchHandler : IHandler
{
    private readonly JoinModel _joinModel;

    public MergeBranchHandler(JoinModel joinModel) => _joinModel = joinModel;

    public Task HandleAsync(Execution execution)
    {
        execution.IsMerged = ComputeMerged(execution);
        return Task.CompletedTask;
    }

    private bool ComputeMerged(Execution execution)
    {
        var instance = execution.ProcessInstance!;
        var arriving = execution.ProcessTask;
        var joinName = _joinModel.Name;
        var arrivingScope = arriving == null
            ? null
            : ReapplySupport.FindScope(ReapplySupport.ScopesOf(arriving), joinName);
        if (arrivingScope == null) return LegacyMerged(instance);
        var fork = ReapplySupport.AsStr(
            arrivingScope.TryGetValue(ReapplySupport.KeyFork, out var fv) ? fv : null);
        var round = ReapplySupport.AsLong(
            arrivingScope.TryGetValue(ReapplySupport.KeyRound, out var rv) ? rv : null);

        // root → 是否仍阻塞（true=未完成）
        var roots = new Dictionary<long, bool>();
        foreach (var task in instance.Tasks)
        {
            var scope = ReapplySupport.FindScope(ReapplySupport.ScopesOf(task), joinName);
            var inRound = scope != null
                && fork != null
                && fork == ReapplySupport.AsStr(scope.TryGetValue(ReapplySupport.KeyFork, out var sf) ? sf : null)
                && ReapplySupport.AsLong(scope.TryGetValue(ReapplySupport.KeyRound, out var sr) ? sr : null) == round;
            if (inRound)
            {
                var rootKey = ReapplySupport.AsLong(
                    scope!.TryGetValue(ReapplySupport.KeyRoot, out var srt) ? srt : null);
                roots[rootKey] = roots.TryGetValue(rootKey, out var prev) && prev;
            }
            if (!task.IsDoing()) continue;
            if (inRound)
            {
                roots[ReapplySupport.AsLong(
                    scope!.TryGetValue(ReapplySupport.KeyRoot, out var srt2) ? srt2 : null)] = true;
            }
            // 该分支正在申请人手里修改：申请任务本身不在分支节点名集合里，必须按等待关系计入
            foreach (var b in ReapplySupport.BranchOf(ReapplySupport.ReapplyOf(task)))
            {
                if (joinName == ReapplySupport.AsStr(b.TryGetValue(ReapplySupport.KeyJoin, out var bj) ? bj : null)
                    && fork != null
                    && fork == ReapplySupport.AsStr(b.TryGetValue(ReapplySupport.KeyFork, out var bf) ? bf : null)
                    && ReapplySupport.AsLong(b.TryGetValue(ReapplySupport.KeyRound, out var br) ? br : null) == round)
                {
                    roots[ReapplySupport.AsLong(
                        b.TryGetValue(ReapplySupport.KeyRoot, out var broot) ? broot : null)] = true;
                }
            }
        }
        if (roots.Count == 0) return LegacyMerged(instance);
        return !roots.Values.Any(v => v);
    }

    /// <summary>存量/无归属任务的旧判据：进行中任务里没有本汇合点输入源节点的任务即可合并。</summary>
    private bool LegacyMerged(ProcessInstance instance)
    {
        var doingTasks = instance.GetDoingTasks();
        if (doingTasks.Count == 0) return true;
        foreach (var input in _joinModel.Inputs)
        {
            var sourceName = input.Source?.Name;
            if (sourceName == null) continue;
            if (doingTasks.Any(t => sourceName == t.TaskName)) return false;
        }
        return true;
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

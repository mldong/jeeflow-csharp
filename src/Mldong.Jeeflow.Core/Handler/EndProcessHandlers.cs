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

        // SnakerFlow 主链（issues/257）：仍有未完成主办任务时不得结束；协办行可自动完成。
        // 当前聚合根优先：会签合并/拒绝刚把兄弟任务改成 FINISHED 或 ABANDON，内存仓与 JDBC
        // 都还没走到本次 UpdateInstance，只查仓储会把本次命令已清掉的行误判成进行中。
        var active = instance.GetDoingTasks().ToList();
        var knownTaskIds = instance.Tasks.Where(t => t.TaskId != null).Select(t => t.TaskId!.Value).ToHashSet();
        foreach (var task in await execution.Engine!.Repository.FindDoingTasksAsync(
                     instance.InstanceId!.Value, null))
            if (!knownTaskIds.Contains(task.TaskId!.Value)) active.Add(task);
        // 内部腿沿用当前命令的事务/命令闸，不重入公共 ExecuteProcessTaskAsync 的 SemaphoreSlim。
        if (active.Any(t => t.TaskType != WfTaskType.Secondary))
            throw new JeeflowException("存在未完成的主办任务,请确认.");
        var activeTaskIds = active.Select(t => t.TaskId!.Value).ToHashSet();
        foreach (var task in active)
        {
            var assistArgs = execution.Args.Copy();
            assistArgs[FlowConst.SubmitType] = (int)WfSubmitType.Agree;
            await execution.Engine.ExecuteProcessTaskInTxAsync(
                task.TaskId!.Value, FlowConst.AutoId, assistArgs);
        }
        if (active.Count > 0)
        {
            var refreshed = await execution.Engine.Repository.FindInstanceByIdAsync(instance.InstanceId);
            if (refreshed != null)
            {
                foreach (var task in refreshed.Tasks)
                {
                    var index = instance.Tasks.FindIndex(t => t.TaskId == task.TaskId);
                    if (activeTaskIds.Contains(task.TaskId!.Value) || index < 0)
                        instance.Tasks[index < 0 ? instance.Tasks.Count : index] = task;
                }
                foreach (var kv in refreshed.Variables) instance.Variables[kv.Key] = kv.Value;
            }
        }

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
                // 回父沿用本次子流程执行参数；父实例变量仍由父聚合根自己维护。
                Args = execution.Args.Copy(),
                Operator = StartAndExecuteSupport.RealOperator(execution.Operator, instance),
                Context = execution.Context,
            };
            await spm.ExecuteAsync(newExec);
            execution.AddContinuation(newExec);
            // 父实例的任务、历史行与终态事件随独立 continuation 持久化，避免借用子流程上下文。
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
/// 启动子流程处理器（issues/257）：按 processName/version 定位定义，起独立子实例并复用
/// startAndExecute 自办初始 DOING 快照。子启动/自办各自负责持久化与事件，本处理器不把子任务
/// 并入父 execution 重复保存或重复 fire TASK_START。
/// </summary>
public class StartSubProcessHandler : IHandler
{
    private readonly SubProcessModel _subProcessModel;

    public StartSubProcessHandler(SubProcessModel subProcessModel) => _subProcessModel = subProcessModel;

    public async Task HandleAsync(Execution execution)
    {
        var engine = execution.Engine;
        var parent = execution.ProcessInstance;
        if (engine == null || parent == null) return;

        var define = await engine.Repository.FindDefineByNameAndVersionAsync(
            _subProcessModel.ProcessName, _subProcessModel.Version);
        if (define == null)
            throw new JeeflowException(WfErr.NotFoundProcessDefine,
                "子流程" + _subProcessModel.ProcessName + "定义不存在");

        var op = StartAndExecuteSupport.RealOperator(execution.Operator, parent);
        var childArgs = StartAndExecuteSupport.ChildArgs(execution.Args);
        var parentTasksBefore = (await engine.Repository.FindDoingTasksAsync(parent.InstanceId!.Value, null))
            .Select(t => t.TaskId).ToHashSet();
        var child = await engine.StartInTxAsync(
            define.Id, op, childArgs, parent.InstanceId, _subProcessModel.Name);
        // 初始 DOING 快照由子 start 落库；这里逐任务补真实发起人后以 flow.auto 自办。
        // 内部腿不重入 _cmdGate，子任务已持久化，因此不再 AddTasks 到父 execution。
        var result = await StartAndExecuteSupport.ApplyInitialTasksAsync(
            engine, child.InstanceId!.Value, op, childArgs);
        result.AddRange(await engine.Repository.FindDoingTasksAsync(child.InstanceId.Value, null));
        foreach (var task in await engine.Repository.FindDoingTasksAsync(parent.InstanceId.Value, null))
            if (!parentTasksBefore.Contains(task.TaskId)) result.Add(task);
        execution.AddPersistedTasks(result.Where(t => t.TaskId != null)
            .GroupBy(t => t.TaskId).Select(g => g.First()));
    }
}

namespace Mldong.Jeeflow.Core;

/// <summary>
/// 工作流引擎——薄编排层（对齐 Java JeeflowEngineImpl，全异步，方案 §2.3）。
/// 引擎不直接操作持久层，通过 IProcessRepository SPI 与存储交互；
/// 所有状态变更委托给 ProcessInstance 聚合根。
/// </summary>
public class JeeflowEngine
{
    private readonly ServiceContext _context;

    /// <summary>
    /// 引擎命令级串行化（.NET 多线程下的"单线程事件循环"等价物，方案 §6.2）：
    /// 同一引擎实例的命令互斥执行，保证 prepare→complete→persist 读-改-写状态守卫
    /// 天然原子——并发办理同任务恰一次成功。
    /// </summary>
    private readonly System.Threading.SemaphoreSlim _cmdGate = new(1, 1);

    public JeeflowEngine(ServiceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public ServiceContext Context => _context;

    public IProcessRepository Repository => _context.Repository;

    // ═══ 启动流程 ═══

    public Task<ProcessInstance> StartProcessInstanceByIdAsync(long? defineId, string? op, FlowData args) =>
        StartProcessInstanceByIdAsync(defineId, op, args, null, null);

    public Task<ProcessInstance> StartProcessInstanceByIdAsync(
        long? defineId, string? op, FlowData args,
        long? parentId, string? parentNodeName)
    {
        return RunInGateAsync(() => StartInTxAsync(defineId, op, args, parentId, parentNodeName));
    }

    private async Task<ProcessInstance> StartInTxAsync(
        long? defineId, string? op, FlowData args,
        long? parentId, string? parentNodeName)
    {
        return await RunInTxAsync(async () =>
        {
            // 1. 查流程定义
            var define = await Repository.FindDefineByIdAsync(defineId);
            if (define == null)
                throw new JeeflowException(WfErr.NotFoundProcessDefine);
            // 2. 解析流程模型
            var model = ModelParser.Parse(define.Content, _context);
            // 3. 追加用户信息（C25：先 addUserInfo 再 autoGenTitle；u_* 仅 start 注入一次）
            await FlowUtil.AddUserInfoToArgsAsync(op, args, _context.UserProvider);
            FlowUtil.AddAutoGenTitle(define.DisplayName, args, _context.ClockOrDefault);
            // 4. 创建聚合根
            var instance = ProcessInstance.Create(define, op, args, parentId, parentNodeName,
                _context.ClockOrDefault);
            // 5. 计算到期时间
            var expireTime = model.ExpireTime;
            if (!string.IsNullOrEmpty(expireTime))
                instance.ExpireTime = FlowUtil.ProcessTime(expireTime, args, _context.ClockOrDefault);
            // 6. 持久化
            await Repository.SaveInstanceAsync(instance);
            // 7. 抄送（issues/47 E19：f_ccActors 发起时统一走 HandleCcActors）
            await HandleCcActorsAsync(instance.InstanceId.Value, op, args.GetObj(FlowConst.CcActorsStart));
            // 8. 构建 Execution 并执行开始节点
            var exec = BuildExecution(model, instance, args, op);
            if (model.GetStart() == null)
                throw new JeeflowException(WfErr.NotFoundNextNode);
            await model.GetStart()!.ExecuteAsync(exec);
            // 9. 持久化产生的任务，并更新实例
            //    TASK_START 事件须在 saveTask 落库（分配 taskId）之后 fire（spec §4.4，issues/13）
            await PersistTasksAsync(exec);
            await Repository.UpdateInstanceAsync(instance);
            // 实例终态事件（码 2）：发起即办结的短流（start→end、decision 直达结束）从这一支落库后播。
            // PersistTasksAsync 内部已 flush 过一次（exec 就是这里的 exec），此处是 java 同款的第二道
            // 显式收口点——队列已 drain 空时它是 no-op，但"发起路径有自己的 updateInstance"这件事
            // 要求 flush 必须排在它之后，不能只依赖前者。顺序判据同 persistTasks 的收口。
            await FlushInstanceEndEventsAsync(exec);
            return instance;
        });
    }

    // ═══ 执行任务 ═══

    public Task<List<ProcessTask>> ExecuteProcessTaskAsync(long taskId, string? op, FlowData args) =>
        RunInGateAsync(() => RunInInstanceLockAsync(taskId,
            () => ExecuteProcessTaskInTxAsync(taskId, op, args)));

    private async Task<List<ProcessTask>> ExecuteProcessTaskInTxAsync(long taskId, string? op, FlowData args)
    {
        return await RunInTxAsync(async () =>
        {
            // 「退回修改、重提直返」：申请待办带直返上下文 ⇒ 走直返腿（只接受 submitType=5）
            var snapshot = await Repository.FindTaskByIdAsync(taskId);
            if (snapshot != null && snapshot.IsDoing() && ReapplySupport.ReapplyOf(snapshot) != null)
                return await ExecuteReapplyAsync(snapshot, op, args);
            var exec = await PrepareExecutionAsync(taskId, op, args);
            if (exec == null) return new List<ProcessTask>();
            var model = exec.ProcessModel!;
            var node = model.GetNode(exec.ProcessTask!.TaskName);
            if (node != null) await node.ExecuteAsync(exec);
            // issues/47 E19：办理时抄送（tf_ccActors）创建 cc 实例
            await HandleCcActorsAsync(exec.ProcessInstance!.InstanceId.Value, op, args.GetObj(FlowConst.CcActors));
            await PersistTasksAsync(exec);
            return exec.ProcessTaskList;
        });
    }

    public Task<List<ProcessTask>> ExecuteAndJumpTaskAsync(
        long taskId, string? op, FlowData args, string? nodeName) =>
        RunInGateAsync(() => ExecuteAndJumpTaskInTxAsync(taskId, op, args, nodeName));

    private async Task<List<ProcessTask>> ExecuteAndJumpTaskInTxAsync(
        long taskId, string? op, FlowData args, string? nodeName)
    {
        return await RunInTxAsync(async () =>
        {
            // 带直返标记的申请待办只能按 submitType=5 重提：命名跳转与血缘回退（nodeName 空）
            // 两档都不得绕过（java `executeAndJumpTask` 同款守卫，覆盖整个入口而非只有命名支）。
            var snapshot = await Repository.FindTaskByIdAsync(taskId);
            ReapplySupport.RejectMarkedApplicant(snapshot, "跳转");
            if (string.IsNullOrEmpty(nodeName))
            {
                // issues/243 甲案：血缘守卫排在 PrepareExecutionAsync 之前——其内部 CompleteTask +
                // UpdateTaskAsync 会把当前行办结，内存仓储无回滚，守卫后置 ⇒ 报错那次待办凭空消失。
                await CheckRollbackPossibleAsync(snapshot);
            }
            else
            {
                // issues/243 §8-2：命名跳转的「找不到节点模型」同一族缺口，同规矩前置（对齐 java 35f0aca）。
                await CheckJumpTargetExistsAsync(snapshot, nodeName);
            }
            var exec = await PrepareExecutionAsync(taskId, op, args);
            if (exec == null) return new List<ProcessTask>();
            var model = exec.ProcessModel!;
            if (string.IsNullOrEmpty(nodeName))
            {
                // issues/121 P2：ROLLBACK 走血缘版——按当前行的 ParentTaskId 从仓储取出历史行，
                // 交给聚合根复活（取不到传 null，由聚合根报 20010007）。实例保持 DOING。
                var current = exec.ProcessTask!;
                var history = current.ParentTaskId is > 0
                    ? await Repository.FindTaskByIdAsync(current.ParentTaskId)
                    : null;
                exec.AddTask(exec.ProcessInstance!.RejectTask(model, current, history,
                    _context.ClockOrDefault)!);
            }
            else
            {
                var targetNode = model.GetNode(nodeName);
                if (targetNode == null)
                    throw new JeeflowException($"根据节点名称[{nodeName}]无法找到节点模型");
                if (targetNode is TaskModel tm && FlowUtil.IsFirstTaskName(model, tm.Name))
                    tm.Assignee = exec.ProcessInstance!.Operator;
                var transition = new TransitionModel
                {
                    Target = targetNode,
                    Enabled = true,
                };
                await transition.ExecuteAsync(exec);
            }
            await PersistTasksAsync(exec);
            return exec.ProcessTaskList;
        });
    }

    public Task<List<ProcessTask>> ExecuteAndJumpToEndAsync(long taskId, string? op, FlowData args) =>
        RunInGateAsync(() => ExecuteAndJumpToEndInTxAsync(taskId, op, args));

    private async Task<List<ProcessTask>> ExecuteAndJumpToEndInTxAsync(long taskId, string? op, FlowData args)
    {
        return await RunInTxAsync(async () =>
        {
            // 带直返标记的申请待办不得被"拒绝"直接推到结束（java `executeAndJumpToEnd` 同款守卫）
            ReapplySupport.RejectMarkedApplicant(await Repository.FindTaskByIdAsync(taskId), "拒绝");
            var exec = await PrepareExecutionAsync(taskId, op, args);
            if (exec == null) return new List<ProcessTask>();
            var model = exec.ProcessModel!;
            // 拒绝结束：本节点未完成的加签任务一并废弃，不留孤儿待办（规范 04 · 后加签「语义边界」）
            AddSignSupport.AbandonDoingOnNode(model, exec.ProcessInstance,
                exec.ProcessTask?.TaskName, op, _context.ClockOrDefault);
            foreach (var end in model.GetModels<EndModel>())
            {
                var transition = new TransitionModel
                {
                    Target = end,
                    Enabled = true,
                };
                await transition.ExecuteAsync(exec);
            }
            await PersistTasksAsync(exec);
            return exec.ProcessTaskList;
        });
    }

    public Task<List<ProcessTask>> ExecuteAndJumpToFirstTaskNodeAsync(
        long taskId, string? op, FlowData args) =>
        RunInGateAsync(() => RunInInstanceLockAsync(taskId,
            () => ExecuteAndJumpToFirstTaskNodeInTxAsync(taskId, op, args)));

    private async Task<List<ProcessTask>> ExecuteAndJumpToFirstTaskNodeInTxAsync(
        long taskId, string? op, FlowData args)
    {
        return await RunInTxAsync(async () =>
        {
            // 校验放在任何状态变更之前：来源节点配了 RETURN_TO_SOURCE 时，先读模型判准入
            // （非法配置/不支持的形状不得先完成任务、先发通知）。
            var snapshot = await Repository.FindTaskByIdAsync(taskId);
            // 带直返标记的申请待办不得被"再次退回"重建一条无标记申请（那会丢掉本轮分支等待）
            ReapplySupport.RejectMarkedApplicant(snapshot, "再次退回");
            TaskModel? sourceNode = null;
            ProcessModel? sourceModel = null;
            if (snapshot != null && snapshot.IsDoing())
            {
                var instance0 = await Repository.FindInstanceByIdAsync(snapshot.ProcessInstanceId);
                if (instance0 != null)
                {
                    var define0 = await Repository.FindDefineByIdAsync(instance0.DefineId);
                    if (define0 != null)
                    {
                        sourceModel = ModelParser.Parse(define0.Content, _context);
                        sourceNode = sourceModel.GetNode(snapshot.TaskName) as TaskModel;
                    }
                }
            }
            var direct = sourceNode != null && sourceNode.ReapplyMode == WfReapplyMode.ReturnToSource;
            if (direct) ValidateReturnToSource(sourceModel!, sourceNode!);

            var exec = await PrepareExecutionAsync(taskId, op, args);
            if (exec == null) return new List<ProcessTask>();
            var model = exec.ProcessModel!;
            // 退回发起人：本节点未完成的加签任务一并废弃，不留孤儿待办（规范 04 · 后加签「语义边界」）
            AddSignSupport.AbandonDoingOnNode(model, exec.ProcessInstance,
                exec.ProcessTask?.TaskName, op, _context.ClockOrDefault);
            if (direct)
            {
                CreateReturnToSourceApplication(exec, sourceNode!, snapshot!);
            }
            else
            {
                var start = model.GetStart();
                if (start != null)
                {
                    foreach (var tm in start.Outputs)
                    {
                        tm.Enabled = true;
                        if (tm.Target is TaskModel target)
                            target.Assignee = exec.ProcessInstance!.Operator;
                        await tm.ExecuteAsync(exec);
                    }
                }
            }
            await PersistTasksAsync(exec);
            return exec.ProcessTaskList;
        });
    }

    // ═══ 退回修改、重提直返 ═══

    /// <summary>来源节点配置了 RETURN_TO_SOURCE 时的准入校验（不改任何状态）。</summary>
    private static void ValidateReturnToSource(ProcessModel model, TaskModel sourceNode)
    {
        if (sourceNode.PerformType == WfPerformType.Countersign)
            throw new JeeflowException($"会签节点[{sourceNode.Name}]不支持退回修改直返");
        if (FlowUtil.IsFirstTaskName(model, sourceNode.Name))
            throw new JeeflowException($"申请节点[{sourceNode.Name}]不能配置重提直返");
        if (FirstTaskModel(model) == null)
            throw new JeeflowException("流程定义缺少申请节点，无法退回发起人");
    }

    /// <summary>
    /// submitType=6 的直返落点：完成来源任务（<see cref="PrepareExecutionAsync"/> 已做）后，
    /// 在原实例里给发起人建一条申请待办，并把本次退回绑定写进任务变量；
    /// <b>不重放来源出边、不重触发分叉</b>。
    /// </summary>
    private void CreateReturnToSourceApplication(Execution exec, TaskModel sourceNode, ProcessTask sourceTask)
    {
        var instance = exec.ProcessInstance!;
        var applyModel = FirstTaskModel(exec.ProcessModel!);
        if (applyModel == null)
            throw new JeeflowException("流程定义缺少申请节点，无法退回发起人");
        var operatorId = instance.Operator ?? sourceTask.CreateUser ?? "";
        var appTask = instance.CreateTask(applyModel, applyModel.DisplayName,
            new List<string> { operatorId },
            instance.Operator, sourceTask.TaskId, true, _context.ClockOrDefault);
        ReapplySupport.PutScopes(appTask, ReapplySupport.EmptyScopes());
        ReapplySupport.PutReapply(appTask, ReapplySupport.NewReapplyContext(
            ReapplySupport.ModeReturnToSource, sourceTask.TaskId, sourceNode.Name,
            ReapplySupport.ScopesOf(sourceTask)));
        exec.AddTask(appTask);
    }

    /// <summary>
    /// submitType=5 且申请待办带直返上下文：校验来源后，在来源节点直接建新待办。
    /// 不执行申请节点出边（不重算来源前条件、不重触发分叉），也不执行来源节点出边。
    /// </summary>
    private async Task<List<ProcessTask>> ExecuteReapplyAsync(
        ProcessTask applyTask, string? op, FlowData args)
    {
        args = ReapplySupport.StripReserved(args);
        var submitType = args.GetInt(FlowConst.SubmitType) ?? (int)WfSubmitType.ReApply;
        if (submitType != (int)WfSubmitType.ReApply)
            throw new JeeflowException("带重提直返标记的申请待办只接受重新提交(submitType=5)");

        var instance = await Repository.FindInstanceByIdAsync(applyTask.ProcessInstanceId);
        if (instance == null) throw new JeeflowException("流程实例不存在");
        var define = await Repository.FindDefineByIdAsync(instance.DefineId);
        if (define == null) throw new JeeflowException(WfErr.NotFoundProcessDefine);
        var model = ModelParser.Parse(define.Content, _context);

        var ctx = ReapplySupport.ReapplyOf(applyTask)!;
        var sourceTaskId = ReapplySupport.RequireSourceTaskId(ctx);
        ProcessTask? source = null;
        foreach (var t in instance.Tasks)
        {
            if (t.TaskId == sourceTaskId)
            {
                source = t;
                break;
            }
        }
        if (source == null || !source.IsFinished())
            throw new JeeflowException("重提直返来源任务无效或未完成");
        if (model.GetNode(source.TaskName) is not TaskModel sourceModel
            || sourceModel.ReapplyMode != WfReapplyMode.ReturnToSource)
        {
            throw new JeeflowException("重提直返来源节点配置已变更，拒绝执行");
        }
        if (!FlowUtil.IsFirstTaskName(model, applyTask.TaskName))
            throw new JeeflowException("重提直返必须从申请待办发起");

        var exec = await PrepareExecutionAsync(applyTask.TaskId!.Value, op, args);
        if (exec == null) return new List<ProcessTask>();
        // 遗留的下一节点指定人参数不得影响本次建单（历史记录不删）
        exec.Args.Remove(FlowConst.NextNodeOperator);
        var actors = await CreateTaskHandler.ResolveActorsAsync(sourceModel, model, exec);
        if (actors.Count == 0)
            throw new JeeflowException($"目标节点[{sourceModel.Name}]没有可用参与者，重提已回滚");
        var branch = ReapplySupport.BranchOf(ctx);
        var newTask = exec.ProcessInstance!.CreateTask(sourceModel, sourceModel.DisplayName, actors,
            op, applyTask.TaskId, false, _context.ClockOrDefault);
        ReapplySupport.PutScopes(newTask, branch);
        exec.AddTask(newTask);
        await PersistTasksAsync(exec);
        return exec.ProcessTaskList;
    }

    /// <summary>申请节点（开始节点的直接后继任务节点）。</summary>
    private static TaskModel? FirstTaskModel(ProcessModel model)
    {
        foreach (var t in model.Tasks)
        {
            if (FlowUtil.IsFirstTaskName(model, t.Name)) return t;
        }
        return null;
    }

    // ═══ 内部方法 ═══

    /// <summary>
    /// 退回上一步（<paramref name="current"/> 所在调用的 nodeName 空 ⇒ 血缘腿）的写前置守卫
    /// （issues/243 甲案，对齐 java <c>checkRollbackPossible</c>）。
    /// <para>只做一件事：把 <see cref="ProcessInstance.CheckRejectable"/> 的两道判据
    /// （无血缘／canRejected 不过）提到任何状态变更之前——它自己<b>纯读</b>
    /// （取 task→instance→define→解析模型→取血缘 parent 行），不写任务行、不写实例、不 fire 事件。</para>
    /// <para><b>不</b>在这里报"任务不在进行中／无权办理"，也不在这里发明第二套文案——那几档留给
    /// <see cref="PrepareExecutionAsync"/> 的既有固定错（它们也在 CompleteTask 之前，天然零写入）；
    /// 任务取不到/不在 DOING、取不到实例或定义时一律放行，由 PrepareExecutionAsync 走它的原形状。</para>
    /// </summary>
    private async Task CheckRollbackPossibleAsync(ProcessTask? current)
    {
        if (current == null || !current.IsDoing()) return;
        var instance = await Repository.FindInstanceByIdAsync(current.ProcessInstanceId);
        if (instance == null) return;
        var define = await Repository.FindDefineByIdAsync(instance.DefineId);
        if (define == null) return;
        var model = ModelParser.Parse(define.Content, _context);
        // history 由仓储取（本栈与 java 的结构差：java 聚合根自带 tasks 列表，本栈引擎取行传入；
        // 取不到传 null，判据就地报无血缘，与复活腿同一份实现）。
        var history = current.ParentTaskId is > 0
            ? await Repository.FindTaskByIdAsync(current.ParentTaskId)
            : null;
        instance.CheckRejectable(model, current, history);
    }

    /// <summary>
    /// 命名跳转（<paramref name="nodeName"/> 非空那一支）的节点存在性前置守卫
    /// （issues/243 §8-2，对齐 java <c>checkJumpTargetExists</c>）。
    /// <para>从前「根据节点名称[X]无法找到节点模型」排在 <see cref="PrepareExecutionAsync"/> 之后
    /// （其内部 CompleteTask + UpdateTaskAsync 已把当前行办结），内存仓储无回滚 ⇒ 节点名写错一次
    /// 就把办理人的待办弄丢。真库有动作级事务整笔回滚观察不到差别，这一档只有内存仓储照得出来。</para>
    /// <para>只做一件事：<b>纯读</b>取 task→instance→define→解析模型，节点名查不到即抛原来那一句——
    /// 不新增文案、不新增错误码；任务取不到/不在 DOING、取不到实例或定义时一律放行，
    /// 由 <see cref="PrepareExecutionAsync"/> 报它的既有固定错（那几档本就在办结之前）。</para>
    /// </summary>
    private async Task CheckJumpTargetExistsAsync(ProcessTask? current, string? nodeName)
    {
        if (current == null || !current.IsDoing()) return;
        var instance = await Repository.FindInstanceByIdAsync(current.ProcessInstanceId);
        if (instance == null) return;
        var define = await Repository.FindDefineByIdAsync(instance.DefineId);
        if (define == null) return;
        if (ModelParser.Parse(define.Content, _context).GetNode(nodeName) == null)
            throw new JeeflowException($"根据节点名称[{nodeName}]无法找到节点模型");
    }

    /// <summary>
    /// 办理前置：任务存在且 DOING、操作人有权；完成聚合任务；合并流程变量；构建 Execution。
    /// </summary>
    private async Task<Execution?> PrepareExecutionAsync(long taskId, string? op, FlowData args)
    {
        // 引擎控制键（_branchCtx / _reapplyContext）只能由引擎写：客户端传进来的一律就地剥掉
        // （伪造"申请待办"标记或伪造分支归属都不得改变路由/血缘），对齐 Java prepareExecution。
        args = ReapplySupport.StripReserved(args);
        var task = await Repository.FindTaskByIdAsync(taskId);
        if (task == null || !task.IsDoing())
            throw new JeeflowException(WfErr.NotFoundDoingProcessTask);
        if (!task.IsAllowed(op))
            throw new JeeflowException(WfErr.NotAllowedExecute);

        var instance = await Repository.FindInstanceByIdAsync(task.ProcessInstanceId);
        if (instance == null) return null;

        var define = await Repository.FindDefineByIdAsync(instance.DefineId);
        if (define == null) return null;

        var model = ModelParser.Parse(define.Content, _context);

        // issues/26：办理提交的 f_ 字段按任务节点字段权限过滤（只读/隐藏不入变量，C19）
        args = FlowUtil.FilterFieldByPerm(args, model, task.TaskName);

        // 后加签写入口校验（规范 04 · 后加签）：必须排在 CompleteTask 之前——内存仓储无回滚，
        // 报错时不得留下已完成的任务行（"报错且无写入"是这条链的硬要求）。
        AddSignSupport.ValidateSubmission(model, task, args, op);

        // 完成任务——聚合根内部修改了 instance 中的 task 状态
        instance.CompleteTask(taskId, op, args, _context.ClockOrDefault);

        // 将 instance 中的已完成 task 状态同步到 task 对象，并持久化
        ProcessTask? completedInInstance = null;
        foreach (var t in instance.Tasks)
        {
            if (taskId == t.TaskId)
            {
                completedInInstance = t;
                break;
            }
        }
        if (completedInInstance != null)
        {
            task.TaskState = completedInInstance.TaskState;
            task.ActorId = completedInInstance.ActorId;
            task.FinishTime = completedInInstance.FinishTime;
            task.Variables = completedInInstance.Variables;
            task.UpdateTime = completedInInstance.UpdateTime;
            task.UpdateUser = completedInInstance.UpdateUser;
        }
        await Repository.UpdateTaskAsync(task);

        // spec 11-events §11.3 码 5/6：任务被办掉/被退回，两支都在<b>任务行 state 落库之后</b> fire，
        // 且互斥（一次办理只发一支）——四条 execute 路径（execute/jump/jumpToEnd/rollbackToOperator）
        // 都经 PrepareExecutionAsync 这一处收口，不在各分支重复埋点。
        await NotifyTaskClosedAsync(instance, task, op, args);

        // 合并流程变量
        var mergedArgs = new FlowData();
        foreach (var kv in instance.Variables) mergedArgs[kv.Key] = kv.Value;
        if (args != null)
            foreach (var kv in args) mergedArgs[kv.Key] = kv.Value;

        var exec = BuildExecution(model, instance, mergedArgs, op);
        exec.ProcessTask = task;
        exec.ProcessTaskId = taskId;
        // 加签人取"本次请求"的值显式带上，不让处理器读合并后的 args
        // （判据与病因见 Execution.AddSignOperators 的注释——实例变量会把上一轮的 tf_ 键复活）。
        exec.AddSignOperators = AddSignSupport.Operators(args);
        return exec;
    }

    private Execution BuildExecution(ProcessModel model, ProcessInstance instance, FlowData args, string? op)
    {
        return new Execution
        {
            ProcessModel = model,
            ProcessInstance = instance,
            ProcessInstanceId = instance.InstanceId,
            Engine = this,
            Args = args,
            Operator = op,
            Context = _context,
        };
    }

    private async Task PersistTasksAsync(Execution exec)
    {
        foreach (var task in exec.ProcessTaskList)
        {
            await ApplySurrogateAsync(exec, task);
            await Repository.SaveTaskAsync(task);
            // 分支根任务建单时自身 taskId 尚未生成（SaveTaskAsync 才分配）⇒ _branchCtx.root 先落 0，
            // 落库后回填并补一次 UPDATE（对齐 Java saveNewTask 的两拍；不补就是"分支根的等待关系
            // 永远对不上"，汇合判据会把它当成另一档）。
            if (ReapplySupport.FillRootSelf(task))
                await Repository.UpdateTaskAsync(task);
            // TASK_START 在落库（分配 taskId）后 fire（spec §4.4 / issues/13）
            await NotifyTaskStartAsync(task);
        }
        // 记录类（custom）历史行的 INSERT 腿（issues/142 · spec 02 §6.2 第 1 条）——
        // **只 saveTask，不 notifyTaskStart**：码 3 表达"新待办产生"，而这一行出生即 FINISHED(20)
        // 、参与者恒等于当前操作人（留痕主体，不是收件人）⇒ 给它 fire 码 3 就是广播一条假待办。
        // 也不走 ApplySurrogateAsync：那一步会把代理人并进 ActorIds，等于在留痕行上凭空多挂一个
        // "能办的人"，与 §6.1「记录类不许兜底给别人」同族。
        // 落库通道之所以在这里而不是 CustomModel 内部直连仓储：本栈全部任务写都收口在引擎这一处
        // （模型/处理器层不碰仓储，见类头注释），且四条办理路径＋发起路径都过这里，一处覆盖全入口。
        // 紧随其后的 UpdateInstanceAsync 级联会按建单不变量覆写这些行（此刻 taskId 已由 saveTask 分配），
        // 与 DOING 行的处理完全同构，不需要额外的登记。
        foreach (var historyTask in exec.HistoryTasks)
        {
            await Repository.SaveTaskAsync(historyTask);
        }
        if (exec.ProcessTask?.TaskId != null)
        {
            await Repository.UpdateTaskAsync(exec.ProcessTask);
        }
        await Repository.UpdateInstanceAsync(exec.ProcessInstance!);
        // 实例终态事件（码 2）：紧跟上面那次 UpdateInstanceAsync —— 行的 state 已落库才允许播
        await FlushInstanceEndEventsAsync(exec);
    }

    /// <summary>
    /// 实例终态事件（码 2 <c>PROCESS_INSTANCE_END</c>）的统一收口——
    /// spec §11.2 原则 3「只在落库之后 fire」／§11.3 码 2「实例 state 落库为 20/45 这类
    /// "走到终点"的状态之后」／08-compliance 场景 32。
    ///
    /// <para>处理器（<c>EndProcessHandler</c>）只往 execution 挂 <see cref="PendingInstanceEnd"/>，
    /// 本方法在实例行<b>真正落库之后</b>把它们播出去。两条路径都要覆盖，缺一即丢事件：</para>
    /// <list type="bullet">
    ///   <item><b>正常路径</b>：登记的就是本次 execution 的实例，行已由调用方那次
    ///       <c>repository.UpdateInstanceAsync</c>（或发起路径的 <c>SaveInstanceAsync</c>＋
    ///       <c>UpdateInstanceAsync</c>）写好 ⇒ 这里只补播，不重复写；</item>
    ///   <item><b>子流程父实例路径</b>：子实例办结时处理器在<b>父实例</b>的 execution 上继续流转，
    ///       父实例<b>不走</b>子流程这次的 <c>UpdateInstanceAsync</c>（历史缺口：父实例终态只改内存，
    ///       那一行永远停在 10）⇒ 这里按登记带的聚合根补一次 <c>UpdateInstanceAsync</c>，再播。
    ///       先写后播的顺序对父实例同样成立。</item>
    /// </list>
    ///
    /// <para>载荷 state 取登记时刻的快照整数（＝刚落库那一行的值），不重读聚合根——登记之后
    /// 流转还可能继续触碰该对象，重读会播出一个没写过的中间值。</para>
    ///
    /// <para>本栈可到达的终态档位：码 2 只由结束节点产生（办结 20／拒绝 45，
    /// <c>EndProcessHandler</c> 是 <c>Finish()</c>／<c>Reject()</c> 的唯一调用者）。其余档位各自的
    /// 归宿——30 撤回走门面 <c>processInstance/withdraw</c>（先 <c>UpdateInstanceAsync</c> 后 fire 码 8，
    /// 写后播已满足；spec §11.3 码 8/9 明写"撤回只发 8 不补发 2"，故这里不扩火）；
    /// 40 终止、50 挂起、99 废弃在 main 源<b>没有生产者</b>（<c>Interrupt</c>／<c>Pending</c>／
    /// <c>AbandonTask</c> 生产路径零调用者）⇒ 对应门格按 unreachable 记账；将来出现写这些档位的
    /// 收口点时，须在该次落库后补 fire，不得在集成层主动补发（spec §11.1）。</para>
    ///
    /// <para>副作用（顺带收口）：修复前码 2 在 <c>node.ExecuteAsync</c> 里就地 fire，流转后续步骤
    /// 抛异常导致事务回滚时事件已经漏出去；现在事件排在写库之后，回滚的那次不再播。</para>
    /// </summary>
    private async Task FlushInstanceEndEventsAsync(Execution exec)
    {
        var pending = exec.DrainPendingEnds();
        if (pending.Count == 0) return;
        foreach (var end in pending)
        {
            var instance = end.Instance;
            var ownInstance = instance == null
                || (exec.ProcessInstanceId != null && exec.ProcessInstanceId.Equals(end.InstanceId));
            if (!ownInstance)
            {
                // 父实例（或更上层）被这一支流转连带办结：它的行不在本次 updateInstance 范围内，补写
                await Repository.UpdateInstanceAsync(instance!);
            }
            await ProcessPublisher.NotifyInstanceEndAsync(
                end.InstanceId, end.State, _context.EventListeners);
        }
    }

    /// <summary>
    /// 新任务落库唯一收口前的「委托自动生效」前置（issues/116 批次 D，契约 06 §4.5）——
    /// 引擎内置、默认开启。发起 / 办理推进 / <b>串行会签每一步推进</b> / 跳转（jump、jumpToEnd、
    /// rollback）<b>四条建任务路径全部经过这里</b>，只挂"发起"一处会漏掉流转中产生的新单。
    ///
    /// <para><b>顺序不能反</b>（条款 2 ⚠️）：此刻 <c>taskId</c> 可能尚未分配（由
    /// <c>SaveTaskAsync</c> 内的 <c>IdGen.NextId()</c> 才分配），走"事后 <c>AddTaskActorAsync</c>
    /// 补写"会打在空 id 上静默无效；故代理人并入<b>参与者集合本身</b>，再由 <c>SaveTaskAsync</c>
    /// 随任务全量写进 <c>wf_process_task_actor</c>。</para>
    ///
    /// <para><b>回写路径一致性</b>：紧随其后的 <c>UpdateInstanceAsync</c> 会按聚合根副本对每个
    /// 已落库任务<b>全量覆写参与者行</b>（C# 仓储既有语义）。因为 <c>instance.Tasks</c> 与
    /// <c>exec.ProcessTaskList</c> 持同一 <see cref="ProcessTask"/> 引用，集合就地并入后两条写路径
    /// 给出的参与者一致，代理人不会被覆写丢失——由用例钉住。</para>
    ///
    /// <para>开关：<see cref="ServiceContext.SurrogateAutoApply"/>（默认 true）；
    /// 未配置 <see cref="ServiceContext.ExtRepository"/> 时由 applier 静默跳过（条款 4）。
    /// 本方法另兜一层 try/catch：自定义 applier 报错也不得打断建单。</para>
    /// </summary>
    private async Task ApplySurrogateAsync(Execution exec, ProcessTask task)
    {
        if (!_context.SurrogateAutoApply) return;
        try
        {
            var processName = await ResolveSurrogateProcessNameAsync(exec);
            await _context.SurrogateApplierOrDefault.ApplyAsync(task, processName, _context.ClockOrDefault.Now);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(
                $"[jeeflow] surrogate apply error, keep original actors: task={task.TaskName}: {e.Message}");
        }
    }

    /// <summary>
    /// 解析当前流程名——契约 06 §4.5 <b>条款 1.1：流程模型 name 优先，模型未带 name 才回落
    /// <c>wf_process_define.name</c></b>（不是反过来）。
    ///
    /// <para>取值口径以<b>模型 name</b>（流程 JSON 的 <c>ProcessModel.Name</c>）为准，依据是迁移基线：
    /// 内置版 mldong-wf 的 <c>SurrogateInterceptor</c> 用的正是
    /// <c>execution.getProcessModel().getName()</c>，Java 参考实现与之同构；用户在内置版配的委托
    /// 迁到 jeeflow 后必须命中同一条。</para>
    ///
    /// <para><b>回落不是可省的兜底</b>：正常情况下 deploy 执行 <c>def.Name = model.Name</c> 让两者恒等，
    /// 只有"模型缺 name"这种异常形态才会走到这里。若此时直接给 null/空串，按判据① 只能命中
    /// 全流程兜底行，<b>该流程自己配的委托一条都查不到</b>（用户视角=委托静默失效）。</para>
    /// </summary>
    private async Task<string?> ResolveSurrogateProcessNameAsync(Execution exec)
    {
        var name = exec.ProcessModel?.Name?.Trim();
        if (!string.IsNullOrEmpty(name)) return name;

        var defineId = exec.ProcessInstance?.DefineId;
        if (defineId == null) return null;
        var define = await Repository.FindDefineByIdAsync(defineId);
        var defineName = define?.Name?.Trim();
        return string.IsNullOrEmpty(defineName) ? null : defineName;
    }

    /// <summary>fire「任务开始」事件（TASK_START）：落库后逐任务，sourceId=taskId 可被反查。</summary>
    private async Task NotifyTaskStartAsync(ProcessTask? task)
    {
        if (task == null || task.TaskId == null) return;
        // spec §11.3 码 3 直传载荷必备键：instanceId / taskId / actors。
        // actors 取<b>落库后的参与者行集合</b>（与 saveTask 写的 wf_process_task_actor 同源，
        // 委托自动生效 issues/116 已在 SaveTaskAsync 前并入集合 ⇒ 这里读到的就是最终收单人）；
        // 仓储读空才回落聚合副本（与 java notifyTaskStart 同口径）。
        var actors = await Repository.FindTaskActorsAsync(task.TaskId.Value);
        if (actors.Count == 0) actors = new List<string>(task.ActorIds);
        await ProcessPublisher.NotifyAsync(
            new ProcessEvent
            {
                EventType = ProcessEventType.ProcessTaskStart,
                SourceId = task.TaskId,
                Data = new FlowData
                {
                    ["instanceId"] = task.ProcessInstanceId,
                    ["taskId"] = task.TaskId,
                    ["actors"] = actors,
                },
            },
            _context.EventListeners);
    }

    /// <summary>退回族 submitType（spec §11.3 码 6 的「含退发起人、软拒绝、跳转回退」四档）：
    /// 2 REJECT／3 ROLLBACK／6 ROLLBACK_TO_OPERATOR／20 COUNTERSIGN_DISAGREE。
    /// 其余（0 APPLY／1 AGREE／4 JUMP 前跳／5 RE_APPLY）一律算「任务被办掉」＝码 5。</summary>
    private static readonly HashSet<int> RejectSubmitTypes = new()
    {
        (int)WfSubmitType.Reject,
        (int)WfSubmitType.Rollback,
        (int)WfSubmitType.RollbackToOperator,
        (int)WfSubmitType.CountersignDisagree,
    };

    /// <summary>
    /// fire「任务办结 5 / 任务退回 6」——<b>互斥</b>（spec §11.3 码 6 末注：同一动作走 reject
    /// 就不再 fire complete）。两支共用码粗载荷细：不为拒绝/跳转/退发起人各开一号，
    /// 靠载荷 <c>submitType</c> 区分（§11.2 原则 2）；缺省按 AGREE 处理，与
    /// <c>EndProcessHandler</c> 读 submitType 的缺省同口径。
    /// <para>调用点唯一：<see cref="PrepareExecutionAsync"/> 里 <c>UpdateTaskAsync</c> 之后——
    /// 四个办理入口（常规办理／跳转／退结束／退发起人）都汇过这一处，任务行的 state
    /// 就是在这次 updateTask 落库的（与 <see cref="NotifyTaskStartAsync"/> 之于 saveTask 同构）。</para>
    /// </summary>
    private async Task NotifyTaskClosedAsync(ProcessInstance instance, ProcessTask task,
        string? op, FlowData args)
    {
        if (task.TaskId == null) return;
        var submitType = args.GetInt(FlowConst.SubmitType, (int)WfSubmitType.Agree);
        await ProcessPublisher.NotifyAsync(
            new ProcessEvent
            {
                EventType = RejectSubmitTypes.Contains(submitType)
                    ? ProcessEventType.TaskReject
                    : ProcessEventType.TaskComplete,
                SourceId = task.TaskId,
                // spec §11.3 码 5/6 直传载荷必备键：instanceId / taskId / operator / submitType
                Data = new FlowData
                {
                    ["instanceId"] = instance.InstanceId ?? task.ProcessInstanceId,
                    ["taskId"] = task.TaskId,
                    ["operator"] = op,
                    ["submitType"] = submitType,
                },
            },
            _context.EventListeners);
    }

    /// <summary>抄送处理（issues/47 E19）：f_ccActors/tf_ccActors 统一走此逻辑（C11）。</summary>
    private async Task HandleCcActorsAsync(long instanceId, string? op, object? ccUserIds)
    {
        if (ccUserIds == null) return;
        // issues/141 G10「空不创建行」（spec 06 §2.10）＋ issues/142 B 批（§2.11 同一枚尺子搬到任务侧）：
        // 逗号串与数组<b>两形同判据</b>，走归属值判据单点的两形入口 PageQuery.NormalizeActors——
        // 逐元素 trim、空串/纯空白/null 丢弃、同次调用折叠，落库与比较取 trim 后的值；
        // 丢完为空 ⇒ 不建 cc 行、也不 fire 码 4。本栈旧形状与 java 同款：<c>"".Split(',')</c> 得到
        // <b>一个空元素</b> ⇒ 实测落出一条 ActorId='' 的 cc 行并 fire 一次码 4（issues/129 那族
        // "空归属值"的病根）。归一只放在漏斗这一层不够，写侧（两仓 + SPI 默认实现）还各有一层兜底。
        var actors = PageQuery.NormalizeActors(ccUserIds);
        if (actors.Count == 0) return;
        // issues/141 G2 写侧判重＝幂等空操作（spec 06 §4）：同一 (实例, 被抄送人) 已有 cc 行时
        // 跳过——不新增行、不重置未读、不更新原行时间；拿回的“实际新建子集”才拿去 fire。
        var created = await Repository.CreateCcInstanceIfAbsentAsync(instanceId, op ?? "user1", actors.ToArray());
        // CC_CREATE（issues/102）：cc 行落库之后逐抄送人 fire，ccActorId 直传事件体
        // 入参＝实际新建的子集而不是原始入参（issues/141 G2）：spec §11.2 原则 1「码=事实」
        // ⇒ 重复抄送没发生“创建”，就不该发这个事件；子集为空整支不 fire（不空转、也不照旧全量 fire）。
        if (created.Count > 0)
        {
            await ProcessPublisher.NotifyCcCreateAsync(instanceId, created, _context.EventListeners);
        }
    }

    private async Task<T> RunInGateAsync<T>(Func<Task<T>> action)
    {
        await _cmdGate.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            _cmdGate.Release();
        }
    }

    /// <summary>
    /// 同一实例串行化（<see cref="InstanceLock"/>）：两个"按任务办理"的入口——常规办理与
    /// 退发起人（jumpToFirstTaskNode）——都要在<b>同一临界区</b>里「重验状态 → 建后继任务」，
    /// 否则"汇合一次性放行""退回与另一支交错"会各读各的旧快照。
    /// <para>锁按任务所属实例取（先读任务行拿实例 id，任务不存在退化为实例 0），
    /// 排在引擎命令闸门之内，解锁后仍在外层命令闸门内完成事务提交。</para>
    /// <para><b>边界：仅单进程部署</b>成立；多进程需仓储侧分布式锁（见 <see cref="InstanceLock"/>）。</para>
    /// </summary>
    private Task<T> RunInInstanceLockAsync<T>(long taskId, Func<Task<T>> action) =>
        InstanceLock.RunAsync(Repository, taskId, action);

    private async Task<T> RunInTxAsync<T>(Func<Task<T>> action)
    {
        var tx = _context.TransactionTemplate;
        if (tx != null) return await tx.ExecuteInTxAsync(action);
        return await action();
    }

    /// <summary>
    /// 把一次完整动作并入当前事务作用域（audit8 R9，spec/12 §12.1 条 3：有则用、无则裸跑）。
    /// 门面（<c>JeeflowFacade.FlowAsync</c>）用它对整次 dispatch 包一层——复合动作
    /// （<c>startAndExecute</c> 三拍、<c>redeploy</c> 两拍等）此前各拍各事务、中途失败留半完成；
    /// 嵌套进入本方法时由事务模板的 join 支并入最外层，不重复 BEGIN。
    /// </summary>
    public Task<T> InTxAsync<T>(Func<Task<T>> action) => RunInTxAsync(action);

    /// <summary>
    /// 提交后腿通道（audit8 R77 读法一，spec/12 §12.3.1／issues/234 §4.3）：监听器/集成层用它把
    /// "必须看到已提交世界"的那一腿（反查、外发通知、二次写）挪到 COMMIT 之后，回滚整批丢弃。
    /// 模板没实现该能力或没有活动事务 ⇒ 立即执行（"落库后"与"提交后"等价，行为逐字不变）。
    /// ⚠ 引擎自身的 fire 不经此处：派发点与"监听器随事件拿到动作事务连接"由 145-3 钉住，
    /// 契约下限由监听器侧承担（java 形状＝事务内 fire＋监听器自取 afterCommit）。
    /// </summary>
    public async Task RunAfterCommitAsync(Func<Task> callback)
    {
        var tx = _context.TransactionTemplate;
        if (tx != null && await tx.TryAfterCommitAsync(callback)) return;
        await callback();
    }
}

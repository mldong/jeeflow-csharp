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
            return instance;
        });
    }

    // ═══ 执行任务 ═══

    public Task<List<ProcessTask>> ExecuteProcessTaskAsync(long taskId, string? op, FlowData args) =>
        RunInGateAsync(() => ExecuteProcessTaskInTxAsync(taskId, op, args));

    private async Task<List<ProcessTask>> ExecuteProcessTaskInTxAsync(long taskId, string? op, FlowData args)
    {
        return await RunInTxAsync(async () =>
        {
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
            var exec = await PrepareExecutionAsync(taskId, op, args);
            if (exec == null) return new List<ProcessTask>();
            var model = exec.ProcessModel!;
            if (string.IsNullOrEmpty(nodeName))
            {
                // ROLLBACK：沿首入边回退建新 todo（C28：实例保持 DOING，actor=上一节点操作人）
                var newTask = exec.ProcessInstance!.RejectTask(model, exec.ProcessTask!,
                    _context.ClockOrDefault);
                if (newTask != null) exec.AddTask(newTask);
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
            var exec = await PrepareExecutionAsync(taskId, op, args);
            if (exec == null) return new List<ProcessTask>();
            var model = exec.ProcessModel!;
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
        RunInGateAsync(() => ExecuteAndJumpToFirstTaskNodeInTxAsync(taskId, op, args));

    private async Task<List<ProcessTask>> ExecuteAndJumpToFirstTaskNodeInTxAsync(
        long taskId, string? op, FlowData args)
    {
        return await RunInTxAsync(async () =>
        {
            var exec = await PrepareExecutionAsync(taskId, op, args);
            if (exec == null) return new List<ProcessTask>();
            var model = exec.ProcessModel!;
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
            await PersistTasksAsync(exec);
            return exec.ProcessTaskList;
        });
    }

    // ═══ 内部方法 ═══

    /// <summary>
    /// 办理前置：任务存在且 DOING、操作人有权；完成聚合任务；合并流程变量；构建 Execution。
    /// </summary>
    private async Task<Execution?> PrepareExecutionAsync(long taskId, string? op, FlowData args)
    {
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

        // 合并流程变量
        var mergedArgs = new FlowData();
        foreach (var kv in instance.Variables) mergedArgs[kv.Key] = kv.Value;
        if (args != null)
            foreach (var kv in args) mergedArgs[kv.Key] = kv.Value;

        var exec = BuildExecution(model, instance, mergedArgs, op);
        exec.ProcessTask = task;
        exec.ProcessTaskId = taskId;
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
            await Repository.SaveTaskAsync(task);
            // TASK_START 在落库（分配 taskId）后 fire（spec §4.4 / issues/13）
            await NotifyTaskStartAsync(task);
        }
        if (exec.ProcessTask?.TaskId != null)
        {
            await Repository.UpdateTaskAsync(exec.ProcessTask);
        }
        await Repository.UpdateInstanceAsync(exec.ProcessInstance!);
    }

    /// <summary>fire「任务开始」事件（TASK_START）：落库后逐任务，sourceId=taskId 可被反查。</summary>
    private async Task NotifyTaskStartAsync(ProcessTask? task)
    {
        if (task == null || task.TaskId == null) return;
        await ProcessPublisher.NotifyAsync(
            new ProcessEvent
            {
                EventType = ProcessEventType.ProcessTaskStart,
                SourceId = task.TaskId,
            },
            _context.EventListeners);
    }

    /// <summary>抄送处理（issues/47 E19）：f_ccActors/tf_ccActors 统一走此逻辑（C11）。</summary>
    private async Task HandleCcActorsAsync(long instanceId, string? op, object? ccUserIds)
    {
        if (ccUserIds == null) return;
        List<string>? ccArr = null;
        if (ccUserIds is string s)
        {
            ccArr = s.Split(',').Select(x => x).ToList();
        }
        else if (ccUserIds is System.Collections.ICollection coll)
        {
            ccArr = new List<string>();
            foreach (var o in coll)
                if (o != null)
                    ccArr.Add(o.ToString()!);
        }
        if (ccArr is { Count: > 0 })
        {
            await Repository.CreateCcInstanceAsync(instanceId, op ?? "user1", ccArr.ToArray());
            // CC_CREATE（issues/102）：逐抄送人 fire，ccActorId 直传事件体
            await NotifyCcCreateAsync(instanceId, ccArr);
        }
    }

    private async Task NotifyCcCreateAsync(long instanceId, List<string> ccArr)
    {
        foreach (var ccActorId in ccArr)
        {
            await ProcessPublisher.NotifyAsync(
                new ProcessEvent
                {
                    EventType = ProcessEventType.CcCreate,
                    SourceId = instanceId,
                    CcActorId = ccActorId,
                },
                _context.EventListeners);
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

    private async Task<T> RunInTxAsync<T>(Func<Task<T>> action)
    {
        var tx = _context.TransactionTemplate;
        if (tx != null) return await tx.ExecuteInTxAsync(action);
        return await action();
    }
}

namespace Mldong.Jeeflow.Core;

/// <summary>
/// 流程实例——DDD 聚合根（充血模型，对齐 Java ProcessInstance）。
/// 流程实例是独立的事务边界，包含所有子任务；所有状态修改通过聚合根方法完成。
/// </summary>
public class ProcessInstance
{
    public long? InstanceId { get; set; }
    public long? ParentId { get; set; }
    public long? DefineId { get; set; }
    public int? State { get; set; }
    public string? ParentNodeName { get; set; }
    public string? BusinessNo { get; set; }
    /// <summary>发起人。</summary>
    public string? Operator { get; set; }
    public DateTime? ExpireTime { get; set; }
    public FlowData Variables { get; set; } = new();
    public List<ProcessTask> Tasks { get; set; } = new();
    public DateTime? CreateTime { get; set; }
    public string? CreateUser { get; set; }
    public DateTime? UpdateTime { get; set; }
    public string? UpdateUser { get; set; }

    public static ProcessInstance Create(
        ProcessDefine define, string? op, FlowData? args,
        long? parentId = null, string? parentNodeName = null, IClock? clock = null)
    {
        var now = (clock ?? SystemClock.Instance).Now;
        var instance = new ProcessInstance
        {
            ParentId = parentId,
            ParentNodeName = parentNodeName,
            DefineId = define.Id,
            Operator = op,
            State = (int)WfInstanceState.Doing,
            BusinessNo = args?.GetStr(FlowConst.BusinessNo),
            Variables = args != null ? args.Copy() : new FlowData(),
            Tasks = new List<ProcessTask>(),
            CreateTime = now,
            CreateUser = op,
            UpdateTime = now,
            UpdateUser = op,
        };
        return instance;
    }

    // ═══ 命令方法 ═══

    /// <summary>完成指定任务。</summary>
    public void CompleteTask(long taskId, string? op, FlowData? args, IClock? clock = null)
    {
        var task = FindDoingTask(taskId);
        task.Finish(op, args, clock);
        if (args != null)
        {
            foreach (var kv in args) Variables[kv.Key] = kv.Value;
            // 提取 f_ 前缀变量持久化到流程变量
            var formData = new FlowData();
            foreach (var key in args.Keys)
                if (key.StartsWith(FlowConst.FormDataPrefix, StringComparison.Ordinal))
                    formData[key] = args[key];
            if (formData.Count > 0) AddVariable(formData, clock);
        }
    }

    /// <summary>废弃指定任务 → 流程实例也废弃。</summary>
    public void AbandonTask(long taskId, string? op, IClock? clock = null)
    {
        var task = FindDoingTask(taskId);
        task.Abandon(op, clock);
        State = (int)WfInstanceState.Abandon;
        Touch(op, clock);
    }

    /// <summary>流程完成。</summary>
    public void Finish(IClock? clock = null)
    {
        State = (int)WfInstanceState.Finished;
        UpdateTime = (clock ?? SystemClock.Instance).Now;
    }

    /// <summary>流程拒绝。</summary>
    public void Reject(IClock? clock = null)
    {
        State = (int)WfInstanceState.Reject;
        UpdateTime = (clock ?? SystemClock.Instance).Now;
    }

    /// <summary>强行终止。</summary>
    public void Interrupt(string? op, IClock? clock = null)
    {
        foreach (var task in Tasks) task.Interrupt(op, clock);
        State = (int)WfInstanceState.Interrupt;
        Touch(op, clock);
    }

    /// <summary>唤醒。</summary>
    public void Resume(string? op, IClock? clock = null)
    {
        foreach (var task in Tasks) task.Resume(op, clock);
        State = (int)WfInstanceState.Doing;
        Touch(op, clock);
    }

    /// <summary>挂起。</summary>
    public void Pending(string? op, IClock? clock = null)
    {
        foreach (var task in Tasks) task.Pending(op, clock);
        State = (int)WfInstanceState.Pending;
        Touch(op, clock);
    }

    /// <summary>
    /// 撤回（C28：实例 30 WITHDRAW + 任务 30，非 45；任务态也不是 99）。
    /// issues/113/114：只改写<b>进行中</b>任务（已完成 20 / 已终止 40 行不得被撤回改写），
    /// 且作用于整单（同实例全部进行中任务，不是只撤操作人自己那一条）；
    /// 实例与被撤任务的 <c>update_user</c> 都回写为真实撤回人。
    /// </summary>
    public void Withdraw(string? op, IClock? clock = null)
    {
        foreach (var task in Tasks)
            if (task.IsDoing())
                task.Withdraw(op, clock);
        State = (int)WfInstanceState.Withdraw;
        Touch(op, clock);
    }

    /// <summary>追加变量。</summary>
    public void AddVariable(FlowData args, IClock? clock = null)
    {
        foreach (var kv in args) Variables[kv.Key] = kv.Value;
        UpdateTime = (clock ?? SystemClock.Instance).Now;
    }

    /// <summary>移除变量。</summary>
    public void RemoveVariable(IClock? clock = null, params string[] keys)
    {
        foreach (var key in keys) Variables.Remove(key);
        UpdateTime = (clock ?? SystemClock.Instance).Now;
    }

    /// <summary>创建普通任务。</summary>
    public ProcessTask CreateTask(
        TaskModel taskModel, string? displayName, List<string> actorIds,
        string? op, long? parentTaskId, bool isFirstTaskNode, IClock? clock = null)
    {
        var task = ProcessTask.Create(
            InstanceId, taskModel.Name, displayName,
            taskModel.TaskType, taskModel.PerformType,
            taskModel.Form, actorIds, op, parentTaskId, isFirstTaskNode, clock);
        Tasks.Add(task);
        return task;
    }

    /// <summary>
    /// 创建会签任务（C8–C10）：串行（SEQUENTIAL）仅创建第一位成员任务并把会签计数状态
    /// 写入该任务变量（operatorList_{node} 全量办理人 / loopCounter_{node} 当前序号 /
    /// nrOfInstances_{node} 总数，任务变量无 csv_ 前缀——C9 对齐 Java），由 CountersignHandler
    /// 在每位成员完成时推进创建下一位——任意时刻恰 1 个 DOING。PARALLEL / 未配置类型保持全员预创建。
    /// </summary>
    public List<ProcessTask> CreateCountersignTasks(
        TaskModel taskModel, List<string> actorIds, string? op,
        long? parentTaskId, bool isFirstTaskNode, IClock? clock = null)
    {
        var list = new List<ProcessTask>();
        if (taskModel.CountersignType == WfCountersignType.Sequential)
        {
            var node = taskModel.Name;
            var first = ProcessTask.Create(
                InstanceId, node, taskModel.DisplayName,
                taskModel.TaskType, taskModel.PerformType,
                taskModel.Form, new List<string> { actorIds[0] }, op,
                parentTaskId, isFirstTaskNode, clock);
            first.Variables[$"{FlowConst.CountersignOperatorList}_{node}"] = new List<object?>(actorIds);
            first.Variables[$"{FlowConst.LoopCounter}_{node}"] = 0;
            first.Variables[$"{FlowConst.NrOfInstances}_{node}"] = actorIds.Count;
            list.Add(first);
            Tasks.Add(first);
            return list;
        }
        foreach (var actorId in actorIds)
        {
            var task = ProcessTask.Create(
                InstanceId, taskModel.Name, taskModel.DisplayName,
                taskModel.TaskType, taskModel.PerformType,
                taskModel.Form, new List<string> { actorId }, op,
                parentTaskId, isFirstTaskNode, clock);
            list.Add(task);
            Tasks.Add(task);
        }
        return list;
    }

    /// <summary>驳回任务（退回上一步）：沿首入边回退建新 todo，actor=上一节点操作人（C28 ROLLBACK）。</summary>
    public ProcessTask? RejectTask(ProcessModel model, ProcessTask currentTask, IClock? clock = null)
    {
        var previousTaskName = GetPreviousTaskName(model, currentTask.TaskName);
        if (previousTaskName == null) return null;
        if (model.GetNode(previousTaskName) is not TaskModel prevModel) return null;
        var newTask = ProcessTask.Create(
            InstanceId, prevModel.Name, prevModel.DisplayName,
            prevModel.TaskType, prevModel.PerformType,
            prevModel.Form, new List<string> { currentTask.ActorId ?? "" },
            // 仍是拓扑版落点（P2 换血缘版）：parent＝被回退的那条任务
            currentTask.CreateUser, currentTask.TaskId,
            FlowUtil.IsFirstTaskName(model, prevModel.Name), clock);
        Tasks.Add(newTask);
        return newTask;
    }

    /// <summary>创建历史任务记录（自定义节点用，直接 FINISHED）。</summary>
    public ProcessTask CreateHistoryTask(CustomModel customModel, string? op, IClock? clock = null)
    {
        var task = ProcessTask.Create(
            InstanceId, customModel.Name, customModel.DisplayName,
            null, null, null, new List<string> { op ?? "" }, op,
            // 自定义节点历史行不是 start 直接后继，也没有「刚办结的任务」可言
            null, false, clock);
        task.TaskState = (int)WfTaskState.Finished;
        Tasks.Add(task);
        return task;
    }

    // ═══ 查询方法 ═══

    public List<ProcessTask> GetDoingTasks() => Tasks.Where(t => t.IsDoing()).ToList();

    public List<ProcessTask> GetDoingTasks(string[]? taskNames)
    {
        if (taskNames == null || taskNames.Length == 0) return GetDoingTasks();
        var nameList = taskNames.ToList();
        return Tasks.Where(t => t.IsDoing() && t.TaskName != null && nameList.Contains(t.TaskName)).ToList();
    }

    public List<ProcessTask> GetFinishedTasks() => Tasks.Where(t => t.IsFinished()).ToList();

    public List<ProcessTask> GetDoneTasks(string[]? taskNames)
    {
        if (taskNames == null || taskNames.Length == 0) return GetFinishedTasks();
        var nameList = taskNames.ToList();
        return Tasks.Where(t => t.IsFinished() && t.TaskName != null && nameList.Contains(t.TaskName)).ToList();
    }

    public bool IsAllTasksFinished() => Tasks.All(t => !t.IsDoing());

    public bool IsDoing() => State == (int)WfInstanceState.Doing;

    public bool IsFinished() => State == (int)WfInstanceState.Finished;

    private ProcessTask FindDoingTask(long taskId)
    {
        foreach (var task in Tasks)
        {
            if (taskId == task.TaskId)
            {
                if (!task.IsDoing())
                    throw new JeeflowException($"任务[{taskId}]不是进行中状态");
                return task;
            }
        }
        throw new JeeflowException($"未找到任务[{taskId}]或不在聚合根中");
    }

    private static string? GetPreviousTaskName(ProcessModel model, string? currentTaskName)
    {
        var node = model.GetNode(currentTaskName);
        return node?.Inputs.Count > 0 ? node.Inputs[0].Source?.Name : null;
    }

    private void Touch(string? op, IClock? clock)
    {
        UpdateTime = (clock ?? SystemClock.Instance).Now;
        UpdateUser = op;
    }
}

/// <summary>流程定义值对象（聚合内嵌，对齐 Java ProcessInstance.ProcessDefine）。</summary>
public class ProcessDefine
{
    public long? Id { get; set; }
    public string? Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Type { get; set; }
    public int? State { get; set; }
    /// <summary>流程模型定义（LogicFlow JSON 字节）。</summary>
    public byte[]? Content { get; set; }
    public int? Version { get; set; }
    public string? UpdateUser { get; set; }
    public DateTime? UpdateTime { get; set; }
    public DateTime? CreateTime { get; set; }
    public string? CreateUser { get; set; }
}

namespace Mldong.Jeeflow.Core;

/// <summary>
/// 流程任务——聚合根 ProcessInstance 的子实体（充血模型，对齐 Java ProcessTask）。
/// 任务自己知道如何完成、废弃、判断权限。
/// </summary>
public class ProcessTask
{
    public long? TaskId { get; set; }
    public long? ProcessInstanceId { get; set; }
    public string? TaskName { get; set; }
    public string? DisplayName { get; set; }
    public WfTaskType? TaskType { get; set; }
    public WfPerformType? PerformType { get; set; }
    public int? TaskState { get; set; }
    /// <summary>实际操作人。</summary>
    public string? ActorId { get; set; }
    /// <summary>参与者列表。</summary>
    public List<string> ActorIds { get; set; } = new();
    public DateTime? FinishTime { get; set; }
    public DateTime? ExpireTime { get; set; }
    public string? FormKey { get; set; }
    public long? ParentTaskId { get; set; }
    public FlowData Variables { get; set; } = new();
    public DateTime? CreateTime { get; set; }
    public string? CreateUser { get; set; }
    public DateTime? UpdateTime { get; set; }
    public string? UpdateUser { get; set; }

    /// <summary>
    /// 建单不变量（issues/121 P1）：本类唯一工厂，必写 ParentTaskId（发起 execution 无当前任务⇒0）
    /// 与行级 isFirstTaskNode。两参无默认值 ⇒ 任何建单路径漏传即编译不过，不留静默漏写。
    /// </summary>
    public static ProcessTask Create(
        long? instanceId, string? taskName, string? displayName,
        WfTaskType? taskType, WfPerformType? performType,
        string? formKey, List<string>? actorIds, string? op,
        long? parentTaskId, bool isFirstTaskNode, IClock? clock = null)
    {
        var now = (clock ?? SystemClock.Instance).Now;
        var task = new ProcessTask
        {
            ProcessInstanceId = instanceId,
            TaskName = taskName,
            DisplayName = displayName,
            TaskType = taskType,
            PerformType = performType,
            TaskState = (int)WfTaskState.Doing,
            FormKey = formKey,
            ActorIds = actorIds != null ? new List<string>(actorIds) : new List<string>(),
            Variables = new FlowData(),
            CreateTime = now,
            CreateUser = op,
            UpdateTime = now,
            UpdateUser = op,
            ParentTaskId = parentTaskId ?? 0,
        };
        // 门面出口现算版带「仅进行中」判定，已办结的历史行上恒 false，而血缘版回退
        // 要读那条历史行决定参与者 ⇒ 标记必须建单时落库。
        task.Variables[FlowConst.IsFirstTaskNode] = isFirstTaskNode;
        return task;
    }

    // ═══ 命令方法 ═══

    /// <summary>完成任务。</summary>
    public void Finish(string? op, FlowData? args, IClock? clock = null)
    {
        if (!IsDoing())
            throw new JeeflowException($"任务[{TaskName}]不是进行中状态，无法完成");
        if (!IsAllowed(op))
            throw new JeeflowException($"操作人[{op}]不在任务参与者列表中");
        TaskState = (int)WfTaskState.Finished;
        ActorId = op;
        if (args != null)
            foreach (var kv in args) Variables[kv.Key] = kv.Value;
        FinishTime = (clock ?? SystemClock.Instance).Now;
        UpdateTime = FinishTime;
        UpdateUser = op;
    }

    /// <summary>废弃任务。</summary>
    public void Abandon(string? op, IClock? clock = null)
    {
        if (!IsDoing())
            throw new JeeflowException($"任务[{TaskName}]不是进行中状态，无法废弃");
        TaskState = (int)WfTaskState.Abandon;
        UpdateTime = (clock ?? SystemClock.Instance).Now;
        UpdateUser = op;
    }

    /// <summary>
    /// 撤回任务（状态机 C28：任务置 30 WITHDRAW，不是 99——99 保留给"废弃"语义）。
    /// issues/114：<c>op</c> 是撤回人，须回写 <c>update_user</c>（审计链），不得保留建单人。
    /// 不碰 <c>actor_id</c>：进行中任务该列恒无值是既有不变量（spec 06 §transfer 留痕①）。
    /// </summary>
    public void Withdraw(string? op = null, IClock? clock = null)
    {
        TaskState = (int)WfTaskState.Withdraw;
        UpdateTime = (clock ?? SystemClock.Instance).Now;
        UpdateUser = op;
    }

    /// <summary>强行终止。</summary>
    public void Interrupt(string? op, IClock? clock = null)
    {
        if (IsDoing())
        {
            TaskState = (int)WfTaskState.Interrupt;
            UpdateTime = (clock ?? SystemClock.Instance).Now;
            UpdateUser = op;
        }
    }

    /// <summary>挂起。</summary>
    public void Pending(string? op, IClock? clock = null)
    {
        if (IsDoing())
        {
            TaskState = (int)WfTaskState.Pending;
            UpdateTime = (clock ?? SystemClock.Instance).Now;
            UpdateUser = op;
        }
    }

    /// <summary>唤醒（挂起恢复）。</summary>
    public void Resume(string? op, IClock? clock = null)
    {
        if (TaskState == (int)WfTaskState.Interrupt)
        {
            TaskState = (int)WfTaskState.Doing;
            UpdateTime = (clock ?? SystemClock.Instance).Now;
            UpdateUser = op;
        }
    }

    // ═══ 查询方法 ═══

    public bool IsDoing() => TaskState == (int)WfTaskState.Doing;

    public bool IsFinished() => TaskState == (int)WfTaskState.Finished;

    /// <summary>
    /// 权限判定：系统代执行（flow.auto）/超级管理员（flow.admin）放行（对齐 boot2/boot3 isAllowed）。
    /// </summary>
    public bool IsAllowed(string? op)
    {
        if (string.Equals(FlowConst.AutoId, op, StringComparison.OrdinalIgnoreCase)
            || string.Equals(FlowConst.AdminId, op, StringComparison.OrdinalIgnoreCase))
            return true;
        return IsDoing() && op != null && ActorIds.Contains(op);
    }

    /// <summary>克隆（拦截器/事件场景需要副本时用）。</summary>
    public ProcessTask Clone()
    {
        var t = (ProcessTask)MemberwiseClone();
        t.ActorIds = new List<string>(ActorIds);
        t.Variables = new FlowData();
        foreach (var kv in Variables) t.Variables[kv.Key] = kv.Value;
        return t;
    }
}

/// <summary>候选人值对象。</summary>
public class Candidate
{
    public string? ActorId { get; set; }
    public string? ActorName { get; set; }
    /// <summary>user / dept / role。</summary>
    public string? Type { get; set; }

    public Candidate() { }

    public Candidate(string? actorId, string? actorName, string? type)
    {
        ActorId = actorId;
        ActorName = actorName;
        Type = type;
    }

    public override bool Equals(object? obj) =>
        obj is Candidate c && ActorId != null && ActorId.Equals(c.ActorId);

    public override int GetHashCode() => ActorId?.GetHashCode() ?? 0;
}

/// <summary>流程设计（wf_process_design 行，对齐 Java ProcessDesign）。</summary>
public class ProcessDesign
{
    public long? Id { get; set; }
    public string? Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Type { get; set; }
    public string? Icon { get; set; }
    public int? IsDeployed { get; set; }
    public string? Remark { get; set; }
    public DateTime? CreateTime { get; set; }
    public string? CreateUser { get; set; }
    public DateTime? UpdateTime { get; set; }
    public string? UpdateUser { get; set; }
}

/// <summary>流程设计历史快照（wf_process_design_his 行）。</summary>
public class ProcessDesignHis
{
    public long? Id { get; set; }
    public long? ProcessDesignId { get; set; }
    public byte[]? Content { get; set; }
    public DateTime? CreateTime { get; set; }
    public string? CreateUser { get; set; }
}

/// <summary>委托代理（wf_process_surrogate 行）。</summary>
public class ProcessSurrogate
{
    public long? Id { get; set; }
    public string? ProcessName { get; set; }
    public string? Operator { get; set; }
    public string? Surrogate { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int? Enabled { get; set; } = 1;
    public DateTime? CreateTime { get; set; }
    public string? CreateUser { get; set; }
    public DateTime? UpdateTime { get; set; }
    public string? UpdateUser { get; set; }
}

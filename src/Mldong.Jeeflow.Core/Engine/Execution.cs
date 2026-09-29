namespace Mldong.Jeeflow.Core;

/// <summary>
/// 执行上下文——流转过程中携带的状态（对齐 Java Execution）。
/// Context 携带 SPI 注册表（C# 无静态 ServiceContext；模型/处理器经此取得）。
/// </summary>
public class Execution
{
    public long? ProcessInstanceId { get; set; }
    public long? ProcessTaskId { get; set; }
    public FlowData Args { get; set; } = new();
    public ProcessModel? ProcessModel { get; set; }
    public ProcessTask? ProcessTask { get; set; }
    public ProcessInstance? ProcessInstance { get; set; }
    public List<ProcessTask> ProcessTaskList { get; set; } = new();
    public bool IsMerged { get; set; }
    public JeeflowEngine? Engine { get; set; }
    public string? Operator { get; set; }
    public NodeModel? NodeModel { get; set; }
    public ServiceContext Context { get; set; } = null!;

    /// <summary>
    /// 实例终态待播队列（spec §11.2 原则 3／码 2 触发时机，见 <see cref="PendingInstanceEnd"/>）：
    /// 结束节点处理器只登记，真正的 fire 由引擎在 <c>repository.UpdateInstanceAsync</c>
    /// 成功返回后统一 flush。
    ///
    /// <para>随 execution 生死，不留静态登记表——并发流转互不串味。</para>
    /// </summary>
    private readonly List<PendingInstanceEnd> _pendingEnds = new();

    public void AddTask(ProcessTask task) => ProcessTaskList.Add(task);

    public void AddTasks(IEnumerable<ProcessTask> tasks) => ProcessTaskList.AddRange(tasks);

    /// <summary>登记一条待播的实例终态事件（不 fire，见 <see cref="DrainPendingEnds"/>）。</summary>
    public void AddPendingEnd(PendingInstanceEnd? pendingEnd)
    {
        if (pendingEnd != null) _pendingEnds.Add(pendingEnd);
    }

    /// <summary>
    /// 并入另一条 execution 的待播终态事件——子流程级联的收口姿势：处理器在<b>父实例</b>的
    /// 临时 execution 上办结父实例，登记要随任务一起上收到外层 execution，
    /// 与 <see cref="AddTasks"/> 同一条腿（漏了就是"父实例终态事件整支丢掉"）。
    /// </summary>
    public void AddPendingEnds(IEnumerable<PendingInstanceEnd>? pendingEnds)
    {
        if (pendingEnds == null) return;
        foreach (var e in pendingEnds) _pendingEnds.Add(e);
    }

    public IReadOnlyList<PendingInstanceEnd> PendingEnds => _pendingEnds;

    /// <summary>取走并清空（引擎 flush 用；清空保证同一条登记不会被播两次）。</summary>
    public List<PendingInstanceEnd> DrainPendingEnds()
    {
        var taken = new List<PendingInstanceEnd>(_pendingEnds);
        _pendingEnds.Clear();
        return taken;
    }

    public List<ProcessTask> GetDoingTaskList() =>
        ProcessTaskList.Where(t => t.TaskState == (int)WfTaskState.Doing).ToList();
}

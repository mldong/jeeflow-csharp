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

    /// <summary>
    /// 记录类节点（<c>snaker:custom</c>）产生的历史行待落库队列（issues/142 · spec 02 §6.2 第 1 条）。
    ///
    /// <para><b>为什么单独一条通道，而不是塞进 <see cref="ProcessTaskList"/></b>：那条列表在引擎里是
    /// 「新建待办」的落库收口点，<c>JeeflowEngine.PersistTasksAsync</c> 对它的每一步都是
    /// <c>saveTask</c> → <c>notifyTaskStart</c>（<b>码 3 TASK_START</b>）。码 3 表达的是
    /// "有一份新待办产生"，而记录类节点建出来的那一行<b>出生即 FINISHED(20)</b>、没有参与者可办
    /// （spec 02 §6.1：它本来就不该有待办）⇒ 混进去就是给一条查不到的历史行广播一条假待办事件，
    /// 集成层的站内信/角标会照着它去通知一个根本不该被通知的人。</para>
    ///
    /// <para>所以<b>「落库」与「fire 码 3」必须解耦</b>：本队列只走 <c>saveTask</c> 那半条腿，
    /// <c>notifyTaskStart</c> 与委托自动生效（<c>ApplySurrogate</c>，它会把代理人并进参与者集合，
    /// 等于在留痕行上凭空多挂一个"能办的人"）都不参与。基准侧 java 同样是两条腿（
    /// <c>persistTasks</c> 只吃 <c>exec.getProcessTaskList()</c>，历史行由聚合根携带），
    /// 缺的只是 INSERT 那一条——本栈与 java 本轮补的是同一条腿、同一个语义。</para>
    ///
    /// <para>随 execution 生死，与 <see cref="_pendingEnds"/> 同款（并发流转互不串味）。
    /// 聚合根 <c>instance.Tasks</c> 里是<b>同一个对象引用</b> ⇒ 落库分配 taskId 后，
    /// 紧随其后的 <c>UpdateInstanceAsync</c> 级联会照建单不变量把它当普通行覆写，不需要额外登记。</para>
    /// </summary>
    private readonly List<ProcessTask> _historyTasks = new();

    public void AddTask(ProcessTask task) => ProcessTaskList.Add(task);

    public void AddTasks(IEnumerable<ProcessTask> tasks) => ProcessTaskList.AddRange(tasks);

    /// <summary>登记一条记录类历史行（只落库、不 fire 码 3，见 <see cref="_historyTasks"/>）。</summary>
    public void AddHistoryTask(ProcessTask task) => _historyTasks.Add(task);

    /// <summary>
    /// 并入另一条 execution 的历史行——与 <see cref="AddTasks"/>／<see cref="AddPendingEnds"/>
    /// 同一条收口腿：子流程办结时处理器在<b>父实例</b>的临时 execution 上继续流转，
    /// 那个临时对象随即被丢弃 ⇒ 父实例这一支若命中 custom 节点，历史行不并上来就是"整支丢掉、
    /// 留痕查不到"。
    /// </summary>
    public void AddHistoryTasks(IEnumerable<ProcessTask>? tasks)
    {
        if (tasks == null) return;
        foreach (var t in tasks) _historyTasks.Add(t);
    }

    /// <summary>待落库的记录类历史行（引擎 <c>PersistTasksAsync</c> 消费）。</summary>
    public IReadOnlyList<ProcessTask> HistoryTasks => _historyTasks;

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

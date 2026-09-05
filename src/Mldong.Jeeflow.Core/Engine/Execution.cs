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

    public void AddTask(ProcessTask task) => ProcessTaskList.Add(task);

    public void AddTasks(IEnumerable<ProcessTask> tasks) => ProcessTaskList.AddRange(tasks);

    public List<ProcessTask> GetDoingTaskList() =>
        ProcessTaskList.Where(t => t.TaskState == (int)WfTaskState.Doing).ToList();
}

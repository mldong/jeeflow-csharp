namespace Mldong.Jeeflow.Core;

/// <summary>
/// 聚合仓储 SPI（对齐 Java IProcessRepository，全异步，方案 §2.3）。
/// 错误一律抛 <see cref="JeeflowException"/>；同事务内所有方法共用同一连接由
/// 实现侧闭包/连接模板保证（spec/05），签名不带事务参数。
/// </summary>
public interface IProcessRepository
{
    // ═══ 引擎运行时方法 ═══

    Task<ProcessDefine?> FindDefineByIdAsync(long? defineId);

    /// <summary>新增流程定义（id 为空时由实现生成）。</summary>
    Task SaveDefineAsync(ProcessDefine define);
    /// <summary>更新流程定义（name/displayName/type/state/content/version 等）。</summary>
    Task UpdateDefineAsync(ProcessDefine define);
    /// <summary>启用/禁用流程定义（state: 1 可用 / 0 不可用）。</summary>
    Task UpdateDefineStateAsync(long defineId, int state);
    /// <summary>删除流程定义。</summary>
    Task RemoveDefineAsync(long defineId);

    Task<ProcessInstance?> FindInstanceByIdAsync(long? instanceId);
    Task SaveInstanceAsync(ProcessInstance instance);
    /// <summary>更新实例并级联持久化聚合内任务状态（撤回/挂起/激活等变更随同落库，v1.0.1）。</summary>
    Task UpdateInstanceAsync(ProcessInstance instance);

    Task<ProcessTask?> FindTaskByIdAsync(long? taskId);
    Task SaveTaskAsync(ProcessTask task);
    Task UpdateTaskAsync(ProcessTask task);

    Task<List<ProcessTask>> FindDoingTasksAsync(long instanceId, string[]? taskNames);
    Task<List<ProcessTask>> FindDoneTasksAsync(long instanceId, string[]? taskNames);
    Task<List<ProcessTask>> FindHistoryTasksAsync(long instanceId);

    Task CreateCcInstanceAsync(long instanceId, string creator, params string[] actorIds);
    Task UpdateCcStatusAsync(long instanceId, string actorId);

    Task<List<string>> FindTaskActorsAsync(long taskId);
    Task AddTaskActorAsync(long taskId, List<string> actors);
    Task RemoveTaskActorAsync(long taskId, List<string> actors);

    // ═══ 前端分页查询方法 ═══

    /// <summary>我的待办。</summary>
    Task<PageResult<TaskRow>> PageTodoTasksAsync(PageQuery query);
    /// <summary>我的已办。</summary>
    Task<PageResult<TaskRow>> PageDoneTasksAsync(PageQuery query);
    /// <summary>我发起的流程实例。</summary>
    Task<PageResult<InstanceRow>> PageInstancesAsync(PageQuery query);
    /// <summary>我的抄送。</summary>
    Task<PageResult<InstanceRow>> PageCcInstancesAsync(PageQuery query);
    /// <summary>流程定义分页。</summary>
    Task<PageResult<DefineRow>> PageDefinesAsync(PageQuery query);
    /// <summary>我的待办计数。</summary>
    Task<int> CountTodoTasksAsync(long? userId);

    // ═══ 统计查询方法（issues/103，全纯列——C23 零方言分叉）═══

    /// <summary>查询实例列表（stats 用，轻量级）。stateIn/timeField+start/end 均可空。</summary>
    Task<List<InstanceStatsRow>> QueryInstancesForStatsAsync(
        List<int>? stateIn, string timeField, DateTime? start, DateTime? end);

    /// <summary>查询任务列表（stats 用）。state/start/end 均可空。</summary>
    Task<List<TaskStatsRow>> QueryTasksForStatsAsync(int? state, DateTime? start, DateTime? end);

    /// <summary>已完成实例平均时长（秒）：MAX(task.finish_time) - instance.create_time，state=20。</summary>
    Task<int> StatsAvgCompletedDurationSecondsAsync(DateTime? start, DateTime? end);

    /// <summary>进行中任务数 + 逾期未办任务数 → [pending, overdue]。</summary>
    Task<int[]> StatsPendingAndOverdueCountAsync();

    /// <summary>已完成任务聚合：[total, countersign(perform_type=1), onTime, onTimeDenom]。</summary>
    Task<int[]> StatsCompletedTaskAggregateAsync();

    /// <summary>当前积压节点（task_state=10 按 display_name 分组）→ [{key, count}] 降序 limit N。</summary>
    Task<List<Dictionary<string, object?>>> StatsStuckNodeGroupAsync(int limit);

    /// <summary>当前积压人（task_state=10 经 actor 表展开）→ [{key, count}] 降序 limit N。</summary>
    Task<List<Dictionary<string, object?>>> StatsStuckApproverGroupAsync(int limit);

    /// <summary>按流程定义分组统计实例数量 → [{key, label, count, avgDurationSeconds}] 降序 limit N。</summary>
    Task<List<Dictionary<string, object?>>> StatsDefineGroupAsync(DateTime? start, DateTime? end, int limit);

    /// <summary>已完成实例的耗时列表（秒），state=20，create_time 在 [start,end] 内。</summary>
    Task<List<int>> StatsCompletedInstanceDurationsAsync(DateTime? start, DateTime? end);

    // ═══ 行数据传输对象（字段契约对齐 Java 行输出转换）═══

    /// <summary>任务行数据。</summary>
    public class TaskRow
    {
        public long? Id { get; set; }
        public long? ProcessInstanceId { get; set; }
        public string? TaskName { get; set; }
        public string? DisplayName { get; set; }
        public int? TaskType { get; set; }
        public int? PerformType { get; set; }
        public int? TaskState { get; set; }
        public string? Operator { get; set; }
        public DateTime? FinishTime { get; set; }
        public DateTime? ExpireTime { get; set; }
        public string? FormKey { get; set; }
        public long? TaskParentId { get; set; }
        public string? Variable { get; set; }
        public DateTime? CreateTime { get; set; }
        public string? CreateUser { get; set; }
        public DateTime? UpdateTime { get; set; }
        public string? UpdateUser { get; set; }
        public string? ProcessDefineName { get; set; }
        public string? ProcessDefineDisplayName { get; set; }
        public int? ProcessDefineVersion { get; set; }
        public string? InstanceVariable { get; set; }
        public DateTime? InstanceCreateTime { get; set; }
    }

    /// <summary>实例行数据。</summary>
    public class InstanceRow
    {
        public long? Id { get; set; }
        public long? ParentId { get; set; }
        public long? ProcessDefineId { get; set; }
        public int? State { get; set; }
        public string? ParentNodeName { get; set; }
        public string? BusinessNo { get; set; }
        public string? Operator { get; set; }
        public DateTime? ExpireTime { get; set; }
        public string? Variable { get; set; }
        public DateTime? CreateTime { get; set; }
        public string? CreateUser { get; set; }
        public DateTime? UpdateTime { get; set; }
        public string? UpdateUser { get; set; }
        public string? ProcessDefineName { get; set; }
        public string? ProcessDefineDisplayName { get; set; }
        public int? ProcessDefineVersion { get; set; }
    }

    /// <summary>定义行数据。</summary>
    public class DefineRow
    {
        public long? Id { get; set; }
        public string? Name { get; set; }
        public string? DisplayName { get; set; }
        public string? Type { get; set; }
        public int? State { get; set; }
        public int? Version { get; set; }
        public DateTime? CreateTime { get; set; }
        public string? CreateUser { get; set; }
        public DateTime? UpdateTime { get; set; }
        public string? UpdateUser { get; set; }
    }

    /// <summary>实例统计行（轻量级，不加载关联任务）。</summary>
    public class InstanceStatsRow
    {
        public long? Id { get; set; }
        public int? State { get; set; }
        public DateTime? CreateTime { get; set; }
        public long? ProcessDefineId { get; set; }
        public string? Operator { get; set; }
    }

    /// <summary>任务统计行。</summary>
    public class TaskStatsRow
    {
        public long? Id { get; set; }
        public long? ProcessInstanceId { get; set; }
        public int? TaskState { get; set; }
        public int? PerformType { get; set; }
        public string? Operator { get; set; }
        public string? DisplayName { get; set; }
        public DateTime? CreateTime { get; set; }
        public DateTime? FinishTime { get; set; }
        public DateTime? ExpireTime { get; set; }
    }
}

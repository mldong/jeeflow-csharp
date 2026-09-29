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

    /// <summary>
    /// 建 cc 行的最底层写入口（<c>wf_process_cc_instance</c>）。
    /// <para><b>issues/141 G10「空不创建行」（spec 06 §2.10）的实现义务</b>：入参里的
    /// <b>空串、纯空白、<c>null</c> 一律丢弃</b>，落库值取 <c>Trim</c> 后的串
    /// （<c>" 123 "</c> 与 <c>"123"</c> 是同一个人）。判据要落在这一层而不只落在引擎漏斗里——
    /// 绕过 <c>HandleCcActorsAsync</c>／门面直连仓储的调用方（集成层、第三方仓储消费者）同样不得
    /// 把空归属值灌进 <c>actor_id</c>，那正是 issues/129 那族"空 operator 读全库"的病根。
    /// 本栈自带的两仓（内存 / MySQL）都按 <see cref="PageQuery.NormalizeCcActors"/> 实现这条义务。</para>
    /// </summary>
    Task CreateCcInstanceAsync(long instanceId, string creator, params string[] actorIds);
    Task UpdateCcStatusAsync(long instanceId, string actorId);

    /// <summary>
    /// issues/141 G2 写侧判重（spec 06 §4「抄送写侧判重＝幂等空操作」）的读侧：读某实例<b>已存在</b>
    /// 的 cc 行 actor id，供建 cc 的三条入口（发起 <c>f_ccActors</c>／办理 <c>tf_ccActors</c>／
    /// 门面手动 <c>createCCInstance</c>）判重用。
    /// <para>接口默认实现（DIM）返回空集＝不判重，未覆写的第三方仓储维持旧行为（全量建行、
    /// 全量 fire），SPI 源码兼容不破。jeeflow 自带的两仓（内存仓储 / MySQL 仓储）<b>必须</b>覆写：
    /// 否则 issues/141 G1 那条「同一栈 SQL 仓与内存仓两个答案」的分叉在写侧重演一遍。</para>
    /// </summary>
    Task<List<string>> FindCcActorIdsAsync(long instanceId) =>
        Task.FromResult(new List<string>());

    /// <summary>
    /// issues/141 G2：写侧幂等建 cc 行。同一 <c>(instanceId, actorId)</c> 已有 cc 行时<b>跳过</b>——
    /// ①不新增行、②不重置未读状态（<c>state</c>）、③不更新原行时间，重复抄送同一个人
    /// 在数据面上是 no-op（owner 2026-09-29 明确「不需要重置」，不产生“再提醒一次”语义）；
    /// 返回<b>实际新建</b>的 actor 子集（顺序与入参一致，同一次调用内的重复也折叠）。
    /// <para>为什么要返回子集而不是 void：spec 11.2 原则 1「码值表达发生了什么事实」⇒
    /// 没发生“创建”就不得 fire <c>CC_CREATE</c>（码 4）。三条入口逐人 fire 的入参一律换成这个子集，
    /// 子集为空则整支不 fire（见 <see cref="ProcessPublisher.NotifyCcCreateAsync"/> 的两个调用点）。</para>
    /// <para>未覆写 <see cref="FindCcActorIdsAsync"/> 的第三方仓储走本默认实现 ⇒ 读侧恒空集，
    /// 每次照旧把入参插进去（只折叠同一次调用内的重复），与旧 <see cref="CreateCcInstanceAsync"/>
    /// 一样"全量插入、全量返回"——源码兼容不破，但跨调用的判重与"子集才 fire"都吃不到；
    /// 自带两仓都已覆写，集成方自实现仓储要拿到本档语义也必须覆写。</para>
    /// </summary>
    async Task<List<string>> CreateCcInstanceIfAbsentAsync(
        long instanceId, string creator, params string[] actorIds)
    {
        var existing = await FindCcActorIdsAsync(instanceId);
        var fresh = new List<string>();
        // issues/141 G10「空不创建行」（spec 06 §2.10）：先过同一条归一腿——空串/纯空白/null 丢弃，
        // 值取 trim 后的串（" 123 " 与 "123" 是同一个人，也才与上面的判重咬合）。返回的子集直接
        // 拿去 fire CC_CREATE（码 4），所以子集里也不能留空值（旧形状实测会把 ""/"  " 原样返回并 fire）。
        foreach (var actorId in PageQuery.NormalizeCcActors(actorIds))
        {
            if (existing.Contains(actorId)) continue;
            if (!fresh.Contains(actorId)) fresh.Add(actorId);
        }
        if (fresh.Count > 0)
        {
            await CreateCcInstanceAsync(instanceId, creator, fresh.ToArray());
        }
        return fresh;
    }

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
    /// <summary>
    /// 我的抄送。
    /// <para><b>归属条件必填</b>（issues/141 G1 · spec 06 §2.5）：查询必须带 <c>cc.actor_id</c> 的
    /// 有效归属条件（判据＝<see cref="PageQuery.HasEffectiveCondition"/>：值非 null、字符串非全空白、
    /// 集合非空）；<b>条件缺失或为空值时返回空页</b>（<c>recordCount=0, rows=[]</c>），
    /// 严禁退化成“这条条件不加”而返回全部实例。<b>非归属列</b>的空值仍按“没填”忽略
    /// （<c>m_LIKE_*</c> 传空串照旧放行，issues/129 同一条边界）。</para>
    /// <para>SQL 仓储与内存仓储在同一条判据上必须给同一个答案（issues/117 场景 27 那把尺子
    /// 扩到 ccList）；门面 <c>processInstance/ccList</c> 恒挂这条条件，这里防的是绕过门面
    /// 直连仓储的调用方。</para>
    /// </summary>
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

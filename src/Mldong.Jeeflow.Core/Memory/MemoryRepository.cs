using System.Text;

namespace Mldong.Jeeflow.Core;

/// <summary>
/// 内存仓储（T0 基座，方案 §2.3）：普通内存结构 + 引擎单请求内串行，签名 async 内部零 await。
/// 行为语义逐条对齐 jeeflow-repository-jdbc（分页五键/白名单过滤/TsID 混排/级联更新/参与者全量覆盖），
/// 保证 M2 内存/MySQL 行为双跑无分叉。
/// </summary>
public class MemoryRepository : IProcessRepository
{
    protected readonly ServiceContext Context;
    protected readonly IClock Clock;
    protected readonly IIdGenerator IdGen;

    // ── 存储结构（表级模拟，含自增 cc/actor 行 id）──
    internal readonly Dictionary<long, ProcessDefine> Defines = new();
    internal readonly Dictionary<long, ProcessInstance> Instances = new();
    internal readonly Dictionary<long, ProcessTask> Tasks = new();
    internal readonly Dictionary<long, CcRow> CcInstances = new();
    internal readonly Dictionary<long, ActorRow> TaskActors = new();
    internal long _ccAutoId;
    internal long _actorAutoId;

    public MemoryRepository(ServiceContext context)
    {
        Context = context;
        Clock = context.ClockOrDefault;
        IdGen = context.IdGeneratorOrDefault;
    }

    public sealed class CcRow
    {
        public long Id;
        public long ProcessInstanceId;
        public string? ActorId;
        public int State;
        public DateTime? CreateTime;
        public string? CreateUser;
        public DateTime? UpdateTime;
        public string? UpdateUser;
    }

    public sealed class ActorRow
    {
        public long Id;
        public long ProcessTaskId;
        public string? ActorId;
        public DateTime? CreateTime;
        public string? CreateUser;
    }

    protected long NextId() => IdGen.NextId();

    // ═══ 流程定义 ═══

    public virtual Task<ProcessDefine?> FindDefineByIdAsync(long? defineId)
    {
        if (defineId == null) return Task.FromResult<ProcessDefine?>(null);
        Defines.TryGetValue(defineId.Value, out var d);
        return Task.FromResult(d != null ? CloneDefine(d) : null);
    }

    public virtual Task SaveDefineAsync(ProcessDefine define)
    {
        if (define.Id == null) define.Id = NextId();
        Defines[define.Id.Value] = CloneDefine(define);
        return Task.CompletedTask;
    }

    public virtual Task UpdateDefineAsync(ProcessDefine define)
    {
        if (define.Id == null || !Defines.TryGetValue(define.Id.Value, out var existing))
            return Task.CompletedTask;
        existing.Name = define.Name ?? existing.Name;
        existing.DisplayName = define.DisplayName ?? existing.DisplayName;
        existing.Type = define.Type ?? existing.Type;
        existing.State = define.State ?? existing.State;
        if (define.Content != null) existing.Content = define.Content;
        // version 替换语义（issues/59 designRedeploy）：显式给值保留原值，不递增
        if (define.Version != null) existing.Version = define.Version;
        existing.UpdateUser = define.UpdateUser;
        existing.UpdateTime = Clock.Now;
        return Task.CompletedTask;
    }

    public virtual Task UpdateDefineStateAsync(long defineId, int state)
    {
        if (Defines.TryGetValue(defineId, out var d))
        {
            d.State = state;
            d.UpdateTime = Clock.Now;
        }
        return Task.CompletedTask;
    }

    public virtual Task RemoveDefineAsync(long defineId)
    {
        Defines.Remove(defineId);
        return Task.CompletedTask;
    }

    // ═══ 流程实例 ═══

    public virtual Task<ProcessInstance?> FindInstanceByIdAsync(long? instanceId)
    {
        if (instanceId == null) return Task.FromResult<ProcessInstance?>(null);
        if (!Instances.TryGetValue(instanceId.Value, out var inst)) return Task.FromResult<ProcessInstance?>(null);
        var clone = CloneInstance(inst);
        // 加载关联任务（issues/89 水合：find_instance_by_id 后必须填充 tasks）
        clone.Tasks = FindTasksInternal(instanceId.Value, null, null);
        return Task.FromResult<ProcessInstance?>(clone);
    }

    public virtual Task SaveInstanceAsync(ProcessInstance instance)
    {
        if (instance.InstanceId == null) instance.InstanceId = NextId();
        Instances[instance.InstanceId.Value] = CloneInstance(instance);
        return Task.CompletedTask;
    }

    public virtual Task UpdateInstanceAsync(ProcessInstance instance)
    {
        if (instance.InstanceId == null || !Instances.TryGetValue(instance.InstanceId.Value, out var stored))
            return Task.CompletedTask;
        stored.State = instance.State;
        stored.ParentNodeName = instance.ParentNodeName;
        stored.ExpireTime = instance.ExpireTime;
        stored.Variables = new FlowData();
        foreach (var kv in instance.Variables) stored.Variables[kv.Key] = kv.Value;
        stored.UpdateTime = Clock.Now;
        stored.UpdateUser = instance.UpdateUser;
        // v1.0.1：级联持久化聚合内任务状态（撤回/挂起/激活等变更随同落库）
        foreach (var task in instance.Tasks)
        {
            if (task.TaskId != null) UpdateTaskInternal(task);
        }
        return Task.CompletedTask;
    }

    // ═══ 流程任务 ═══

    public virtual Task<ProcessTask?> FindTaskByIdAsync(long? taskId)
    {
        if (taskId == null) return Task.FromResult<ProcessTask?>(null);
        if (!Tasks.TryGetValue(taskId.Value, out var t)) return Task.FromResult<ProcessTask?>(null);
        var clone = CloneTask(t);
        clone.ActorIds = FindTaskActorsInternal(taskId.Value);
        return Task.FromResult<ProcessTask?>(clone);
    }

    public virtual Task SaveTaskAsync(ProcessTask task)
    {
        if (task.TaskId == null) task.TaskId = NextId();
        Tasks[task.TaskId.Value] = CloneTask(task);
        // 同步参与人（全量覆盖语义，对齐 JDBC saveTaskActors）
        SaveTaskActorsInternal(task.TaskId.Value, task.ActorIds, task.CreateUser);
        return Task.CompletedTask;
    }

    public virtual Task UpdateTaskAsync(ProcessTask task)
    {
        if (task.TaskId != null) UpdateTaskInternal(task);
        return Task.CompletedTask;
    }

    private void UpdateTaskInternal(ProcessTask task)
    {
        if (task.TaskId == null || !Tasks.TryGetValue(task.TaskId.Value, out var stored)) return;
        stored.TaskState = task.TaskState ?? (int)WfTaskState.Doing;
        stored.ActorId = task.ActorId;
        stored.FinishTime = task.FinishTime;
        stored.ExpireTime = task.ExpireTime;
        stored.Variables = new FlowData();
        foreach (var kv in task.Variables) stored.Variables[kv.Key] = kv.Value;
        stored.UpdateTime = Clock.Now;
        stored.UpdateUser = task.UpdateUser;
        SaveTaskActorsInternal(task.TaskId.Value, task.ActorIds, task.UpdateUser);
    }

    public virtual Task<List<ProcessTask>> FindDoingTasksAsync(long instanceId, string[]? taskNames) =>
        Task.FromResult(FindTasksInternal(instanceId, (int)WfTaskState.Doing, taskNames));

    public virtual Task<List<ProcessTask>> FindDoneTasksAsync(long instanceId, string[]? taskNames) =>
        Task.FromResult(FindTasksInternal(instanceId, null, taskNames, doneOnly: true));

    public virtual Task<List<ProcessTask>> FindHistoryTasksAsync(long instanceId) =>
        Task.FromResult(FindTasksInternal(instanceId, null, null));

    private List<ProcessTask> FindTasksInternal(
        long instanceId, int? state, string[]? taskNames, bool doneOnly = false)
    {
        var rows = Tasks.Values
            .Where(t => t.ProcessInstanceId == instanceId)
            .Where(t => state == null || t.TaskState == state)
            .Where(t => !doneOnly || t.TaskState != (int)WfTaskState.Doing)
            .Where(t => taskNames == null || taskNames.Length == 0 || taskNames.Contains(t.TaskName))
            .OrderBy(t => t.CreateTime)
            .ThenBy(t => t.TaskId)
            .ToList()
            .Select(CloneTask)
            .ToList();
        foreach (var row in rows) row.ActorIds = FindTaskActorsInternal(row.TaskId!.Value);
        return rows;
    }

    // ═══ 抄送 ═══

    public virtual Task CreateCcInstanceAsync(long instanceId, string creator, params string[] actorIds)
    {
        var now = Clock.Now;
        foreach (var actorId in actorIds)
        {
            CcInstances[++_ccAutoId] = new CcRow
            {
                Id = _ccAutoId,
                ProcessInstanceId = instanceId,
                ActorId = actorId,
                State = 0,
                CreateTime = now,
                CreateUser = creator,
                UpdateTime = now,
                UpdateUser = creator,
            };
        }
        return Task.CompletedTask;
    }

    public virtual Task UpdateCcStatusAsync(long instanceId, string actorId)
    {
        foreach (var row in CcInstances.Values)
        {
            if (row.ProcessInstanceId == instanceId && row.ActorId == actorId)
            {
                row.State = 1;
                row.UpdateTime = Clock.Now;
            }
        }
        return Task.CompletedTask;
    }

    // ═══ 参与人 ═══

    public virtual Task<List<string>> FindTaskActorsAsync(long taskId) =>
        Task.FromResult(FindTaskActorsInternal(taskId));

    private List<string> FindTaskActorsInternal(long taskId) =>
        TaskActors.Values
            .Where(a => a.ProcessTaskId == taskId)
            .OrderBy(a => a.Id)
            .Select(a => a.ActorId ?? "")
            .ToList();

    public virtual Task AddTaskActorAsync(long taskId, List<string> actors)
    {
        // 去重追加语义（对齐 JDBC addTaskActor：existing 差集插入）
        var existing = FindTaskActorsInternal(taskId);
        foreach (var actor in actors)
        {
            if (existing.Contains(actor)) continue;
            TaskActors[++_actorAutoId] = new ActorRow
            {
                Id = _actorAutoId,
                ProcessTaskId = taskId,
                ActorId = actor,
                CreateTime = Clock.Now,
            };
            existing.Add(actor);
        }
        return Task.CompletedTask;
    }

    public virtual Task RemoveTaskActorAsync(long taskId, List<string> actors)
    {
        var toRemove = TaskActors.Values
            .Where(a => a.ProcessTaskId == taskId && actors.Contains(a.ActorId ?? ""))
            .Select(a => a.Id)
            .ToList();
        foreach (var id in toRemove) TaskActors.Remove(id);
        return Task.CompletedTask;
    }

    private void SaveTaskActorsInternal(long taskId, List<string>? actors, string? createUser)
    {
        if (actors == null || actors.Count == 0) return;
        var toRemove = TaskActors.Values.Where(a => a.ProcessTaskId == taskId).Select(a => a.Id).ToList();
        foreach (var id in toRemove) TaskActors.Remove(id);
        foreach (var actor in actors)
        {
            TaskActors[++_actorAutoId] = new ActorRow
            {
                Id = _actorAutoId,
                ProcessTaskId = taskId,
                ActorId = actor,
                CreateTime = Clock.Now,
                CreateUser = createUser,
            };
        }
    }

    // ═══ 前端分页查询 ═══

    public virtual Task<PageResult<IProcessRepository.TaskRow>> PageTodoTasksAsync(PageQuery query) =>
        Task.FromResult(PageTasks(query, done: false));

    public virtual Task<PageResult<IProcessRepository.TaskRow>> PageDoneTasksAsync(PageQuery query) =>
        Task.FromResult(PageTasks(query, done: true));

    public virtual Task<PageResult<IProcessRepository.InstanceRow>> PageInstancesAsync(PageQuery query) =>
        Task.FromResult(PageInstances(query, cc: false));

    public virtual Task<PageResult<IProcessRepository.InstanceRow>> PageCcInstancesAsync(PageQuery query) =>
        Task.FromResult(PageInstances(query, cc: true));

    public virtual Task<PageResult<IProcessRepository.DefineRow>> PageDefinesAsync(PageQuery query) =>
        Task.FromResult(PageDefines(query));

    public virtual Task<int> CountTodoTasksAsync(long? userId)
    {
        var actorTaskIds = TaskActors.Values
            .Where(a => a.ActorId == userId?.ToString())
            .Select(a => a.ProcessTaskId)
            .ToHashSet();
        var count = Tasks.Values.Count(t => t.TaskState == (int)WfTaskState.Doing && actorTaskIds.Contains(t.TaskId!.Value));
        return Task.FromResult(count);
    }

    protected PageResult<IProcessRepository.DefineRow> PageDefines(PageQuery query)
    {
        var dicts = Defines.Values.Select(DefineToDict).ToList();
        var (pageRows, count) = ApplyConditions(dicts, DefineWhitelist, query, "t.id",
            list => list.Select(RowToDefine).ToList());
        return PageResult<IProcessRepository.DefineRow>.Of(query.PageNum, query.PageSize, count, pageRows);
    }

    protected PageResult<IProcessRepository.TaskRow> PageTasks(PageQuery query, bool done)
    {
        var whitelist = TaskWhitelist;
        var baseRows = Tasks.Values
            .Where(t => done ? t.TaskState != (int)WfTaskState.Doing : t.TaskState == (int)WfTaskState.Doing)
            .OrderBy(t => t.TaskId)
            .ToList();
        var dicts = new List<Dictionary<string, object?>>();
        foreach (var t in baseRows)
        {
            Instances.TryGetValue(t.ProcessInstanceId ?? 0, out var pi);
            Defines.TryGetValue(pi?.DefineId ?? 0, out var pd);
            // pta.actor_id 条件（todoList 契约）按 JOIN 语义展开：任务 × 参与人行
            var actorCond = query.Conditions.FirstOrDefault(c => c.Column == "pta.actor_id");
            if (actorCond != null)
            {
                var actorIds = FindTaskActorsInternal(t.TaskId!.Value);
                foreach (var actorId in actorIds)
                {
                    var dict = TaskToDict(t, pi, pd);
                    dict["pta.actor_id"] = actorId;
                    dicts.Add(dict);
                }
            }
            else
            {
                dicts.Add(TaskToDict(t, pi, pd));
            }
        }
        var (pageRows, count) = ApplyConditions(dicts, whitelist, query, "t.id",
            list => list.Select(RowToTask).ToList());
        return PageResult<IProcessRepository.TaskRow>.Of(query.PageNum, query.PageSize, count, pageRows);
    }

    protected PageResult<IProcessRepository.InstanceRow> PageInstances(PageQuery query, bool cc)
    {
        var whitelist = cc ? CcInstanceWhitelist : InstanceWhitelist;
        var baseRows = Instances.Values.ToList();
        var dicts = new List<Dictionary<string, object?>>();
        if (cc)
        {
            // cc.actor_id 条件按抄送行展开（同 JDBC JOIN 语义：一实例多抄送行）
            foreach (var t in baseRows)
            {
                Defines.TryGetValue(t.DefineId ?? 0, out var pd);
                var ccRows = CcInstances.Values.Where(c => c.ProcessInstanceId == t.InstanceId).ToList();
                if (ccRows.Count == 0) continue;
                foreach (var ccRow in ccRows)
                    dicts.Add(InstanceToDict(t, pd, ccRow));
            }
        }
        else
        {
            foreach (var t in baseRows)
            {
                Defines.TryGetValue(t.DefineId ?? 0, out var pd);
                dicts.Add(InstanceToDict(t, pd, null));
            }
        }
        var (pageRows, count) = ApplyConditions(dicts, whitelist, query, "t.id",
            list => list.Select(RowToInstance).ToList());
        return PageResult<IProcessRepository.InstanceRow>.Of(query.PageNum, query.PageSize, count, pageRows);
    }

    // ═══ 统计查询 ═══

    public virtual Task<List<IProcessRepository.InstanceStatsRow>> QueryInstancesForStatsAsync(
        List<int>? stateIn, string timeField, DateTime? start, DateTime? end)
    {
        var rows = Instances.Values.AsEnumerable();
        if (stateIn is { Count: > 0 }) rows = rows.Where(i => stateIn.Contains(i.State ?? 0));
        if (start != null) rows = rows.Where(i => (i.CreateTime ?? DateTime.MinValue) >= start);
        // JDBC 语义：end 边界 +1 秒（end < ?→实际 <end+1s，闭含当秒）
        if (end != null) rows = rows.Where(i => (i.CreateTime ?? DateTime.MinValue) < end.Value.AddSeconds(1));
        var list = rows.Select(i => new IProcessRepository.InstanceStatsRow
        {
            Id = i.InstanceId,
            State = i.State,
            CreateTime = i.CreateTime,
            ProcessDefineId = i.DefineId,
            Operator = i.Operator,
        }).ToList();
        return Task.FromResult(list);
    }

    public virtual Task<List<IProcessRepository.TaskStatsRow>> QueryTasksForStatsAsync(
        int? state, DateTime? start, DateTime? end)
    {
        var rows = Tasks.Values.AsEnumerable();
        if (state != null) rows = rows.Where(t => t.TaskState == state);
        // JDBC 语义：state=20 按 finish_time 过滤，否则 create_time
        var timeCol = state == (int)WfTaskState.Finished ? "finish" : "create";
        if (start != null)
            rows = rows.Where(t => (timeCol == "finish" ? t.FinishTime : t.CreateTime ?? DateTime.MinValue) >= start);
        if (end != null)
            rows = rows.Where(t => (timeCol == "finish" ? t.FinishTime : t.CreateTime ?? DateTime.MinValue) < end.Value.AddSeconds(1));
        var list = rows.Select(t => new IProcessRepository.TaskStatsRow
        {
            Id = t.TaskId,
            ProcessInstanceId = t.ProcessInstanceId,
            TaskState = t.TaskState,
            PerformType = t.PerformType == null ? null : (int)t.PerformType,
            Operator = t.ActorId,
            DisplayName = t.DisplayName,
            CreateTime = t.CreateTime,
            FinishTime = t.FinishTime,
            ExpireTime = t.ExpireTime,
        }).ToList();
        return Task.FromResult(list);
    }

    public virtual Task<int> StatsAvgCompletedDurationSecondsAsync(DateTime? start, DateTime? end)
    {
        var durs = CompletedInstanceDurations(start, end);
        if (durs.Count == 0) return Task.FromResult(0);
        return Task.FromResult((int)Math.Round(durs.Average()));
    }

    public virtual Task<int[]> StatsPendingAndOverdueCountAsync()
    {
        var doing = Tasks.Values.Where(t => t.TaskState == (int)WfTaskState.Doing).ToList();
        var pending = doing.Count;
        var overdue = doing.Count(t => t.ExpireTime != null && t.ExpireTime < Clock.Now);
        return Task.FromResult(new[] { pending, overdue });
    }

    public virtual Task<int[]> StatsCompletedTaskAggregateAsync()
    {
        var finished = Tasks.Values.Where(t => t.TaskState == (int)WfTaskState.Finished).ToList();
        var countersign = finished.Count(t => t.PerformType == WfPerformType.Countersign);
        var onTime = finished.Count(t => t.ExpireTime != null && t.FinishTime <= t.ExpireTime);
        var onTimeDenom = finished.Count(t => t.ExpireTime != null);
        return Task.FromResult(new[] { finished.Count, countersign, onTime, onTimeDenom });
    }

    public virtual Task<List<Dictionary<string, object?>>> StatsStuckNodeGroupAsync(int limit)
    {
        var rows = Tasks.Values
            .Where(t => t.TaskState == (int)WfTaskState.Doing)
            .GroupBy(t => t.DisplayName)
            .Select(g => new { key = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .ThenBy(x => x.key)
            .Take(limit)
            .Select(x => new Dictionary<string, object?> { ["key"] = x.key, ["count"] = x.count })
            .ToList();
        return Task.FromResult(rows);
    }

    public virtual Task<List<Dictionary<string, object?>>> StatsStuckApproverGroupAsync(int limit)
    {
        var doingIds = Tasks.Values
            .Where(t => t.TaskState == (int)WfTaskState.Doing)
            .Select(t => t.TaskId!.Value)
            .ToHashSet();
        var rows = TaskActors.Values
            .Where(a => doingIds.Contains(a.ProcessTaskId))
            .GroupBy(a => a.ActorId)
            .Select(g => new { key = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .ThenBy(x => x.key)
            .Take(limit)
            .Select(x => new Dictionary<string, object?> { ["key"] = x.key, ["count"] = x.count })
            .ToList();
        return Task.FromResult(rows);
    }

    public virtual Task<List<Dictionary<string, object?>>> StatsDefineGroupAsync(
        DateTime? start, DateTime? end, int limit)
    {
        var rows = Instances.Values.AsEnumerable();
        if (start != null) rows = rows.Where(i => (i.CreateTime ?? DateTime.MinValue) >= start);
        if (end != null) rows = rows.Where(i => (i.CreateTime ?? DateTime.MinValue) < end.Value.AddSeconds(1));
        var list = rows.ToList();
        var result = list
            .GroupBy(i => i.DefineId)
            .Select(g =>
            {
                Defines.TryGetValue(g.Key ?? 0, out var pd);
                // D 口径：count 全实例不过滤 state；avg 仅对 state=20 实例聚合（每实例 MAX(finish_time)）
                var finished = g.Where(i => i.State == (int)WfInstanceState.Finished)
                    .Select(i => i.Tasks.Count > 0 ? i.Tasks.Max(t => t.FinishTime) : null)
                    .Where(ft => ft != null)
                    .Select(ft => (DateTime)ft!)
                    .ToList();
                double? avg = finished.Count > 0
                    ? Math.Round(finished.Average(ft => (ft - (g.First().CreateTime ?? ft)).TotalSeconds))
                    : null;
                return new Dictionary<string, object?>
                {
                    ["key"] = pd?.Name,
                    ["label"] = pd?.DisplayName,
                    ["count"] = g.Count(),
                    ["avgDurationSeconds"] = avg == null ? null : (int)avg,
                };
            })
            .OrderByDescending(r => r["count"])
            .ThenBy(r => (string?)(r["key"] ?? ""))
            .Take(limit)
            .ToList();
        return Task.FromResult(result);
    }

    public virtual Task<List<int>> StatsCompletedInstanceDurationsAsync(DateTime? start, DateTime? end) =>
        Task.FromResult(CompletedInstanceDurations(start, end));

    private List<int> CompletedInstanceDurations(DateTime? start, DateTime? end)
    {
        var rows = Instances.Values.Where(i => i.State == (int)WfInstanceState.Finished);
        if (start != null) rows = rows.Where(i => (i.CreateTime ?? DateTime.MinValue) >= start);
        if (end != null) rows = rows.Where(i => (i.CreateTime ?? DateTime.MinValue) < end.Value.AddSeconds(1));
        var durs = new List<int>();
        foreach (var i in rows)
        {
            var maxFinish = Tasks.Values
                .Where(t => t.ProcessInstanceId == i.InstanceId && t.FinishTime != null)
                .Select(t => t.FinishTime!.Value)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
            if (maxFinish == DateTime.MinValue) continue;
            durs.Add((int)(maxFinish - (i.CreateTime ?? maxFinish)).TotalSeconds);
        }
        return durs;
    }

    // ═══ 通用条件过滤/排序/分页（白名单 + 默认 id DESC，对齐 buildWhere/buildOrder）═══

    protected (List<T> Rows, int RecordCount) ApplyConditions<T>(
        List<Dictionary<string, object?>> rows,
        HashSet<string> whitelist,
        PageQuery query,
        string defaultIdColumn,
        Func<List<Dictionary<string, object?>>, List<T>> mapper)
    {
        IEnumerable<Dictionary<string, object?>> filtered = rows;
        foreach (var cond in query.Conditions)
        {
            var col = cond.Column;
            if (!whitelist.Contains(col)) continue;
            var val = cond.Value;
            if (val == null || (val is string s && s.Length == 0)) continue;
            filtered = cond.Operator?.ToUpperInvariant() switch
            {
                "EQ" => filtered.Where(r => string.Equals(Str(r, col), StrOf(val), StringComparison.Ordinal)),
                "NE" => filtered.Where(r => !string.Equals(Str(r, col), StrOf(val), StringComparison.Ordinal)),
                "LIKE" => filtered.Where(r => LikeMatch(Str(r, col), WrapStr(val, "%", "%"))),
                "LLIKE" => filtered.Where(r => LikeMatch(Str(r, col), WrapStr(val, "%", ""))),
                "RLIKE" => filtered.Where(r => LikeMatch(Str(r, col), WrapStr(val, "", "%"))),
                "GT" => filtered.Where(r => CompareValues(Str(r, col), StrOf(val)) > 0),
                "GE" => filtered.Where(r => CompareValues(Str(r, col), StrOf(val)) >= 0),
                "LT" => filtered.Where(r => CompareValues(Str(r, col), StrOf(val)) < 0),
                "LE" => filtered.Where(r => CompareValues(Str(r, col), StrOf(val)) <= 0),
                "IN" => filtered.Where(r => InOf(col, val, r)),
                "NIN" => filtered.Where(r => !InOf(col, val, r)),
                "BT" => filtered.Where(r => BtOf(col, val, r)),
                _ => filtered,
            };
        }
        var list = filtered.ToList();
        // 排序（默认 id DESC；orderBy 多段白名单内生效）
        var ordered = ApplyOrder(list, whitelist, query, defaultIdColumn);
        // 分页
        var pageNum = Math.Max(query.PageNum, 1);
        var pageSize = Math.Max(query.PageSize, 1);
        var pageRows = ordered
            .Skip((pageNum - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        return (mapper(pageRows), ordered.Count);
    }

    private static string WrapStr(object val, string l, string r) => l + val + r;

    private static bool InOf(string col, object val, Dictionary<string, object?> r)
    {
        if (val is not System.Collections.IEnumerable en) return false;
        foreach (var item in en)
            if (string.Equals(Str(r, col), StrOf(item), StringComparison.Ordinal))
                return true;
        return false;
    }

    private static bool BtOf(string col, object val, Dictionary<string, object?> r)
    {
        if (val is not System.Collections.IEnumerable en) return false;
        var list = en.Cast<object?>().ToList();
        if (list.Count != 2) return false;
        var v = Str(r, col);
        return CompareValues(v, StrOf(list[0])) >= 0 && CompareValues(v, StrOf(list[1])) <= 0;
    }

    /// <summary>简单 LIKE 匹配（% 任意串，其余字面量）。</summary>
    private static bool LikeMatch(string? value, string pattern)
    {
        var v = value ?? "";
        var parts = pattern.Split('%');
        if (parts.Length == 1) return v.Contains(pattern, StringComparison.Ordinal);
        if (!v.StartsWith(parts[0], StringComparison.Ordinal)) return false;
        var idx = parts[0].Length;
        for (var i = 1; i < parts.Length - 1; i++)
        {
            var at = v.IndexOf(parts[i], idx, StringComparison.Ordinal);
            if (at < 0) return false;
            idx = at + parts[i].Length;
        }
        return v.EndsWith(parts[^1], StringComparison.Ordinal) && v.Length >= idx + parts[^1].Length;
    }

    private List<Dictionary<string, object?>> ApplyOrder(
        List<Dictionary<string, object?>> rows,
        HashSet<string> whitelist,
        PageQuery query,
        string defaultIdColumn)
    {
        var defaultCol = defaultIdColumn.Split('.').Last();
        IOrderedEnumerable<Dictionary<string, object?>>? ordered = null;
        var orderBy = query.OrderBy;
        if (!string.IsNullOrEmpty(orderBy))
        {
            foreach (var partRaw in orderBy.Split(','))
            {
                var part = partRaw.Trim().Split(' ');
                if (part.Length == 0 || part[0].Length == 0) continue;
                var col = part[0];
                var desc = part.Length > 1 && part[1].Equals("DESC", StringComparison.OrdinalIgnoreCase);
                // 白名单别名/裸名双匹配（对齐 buildOrder）
                if (!whitelist.Contains(col) && !whitelist.Contains(AliasOf(col) + col)) col = AliasOf(col) + col;
                if (!whitelist.Contains(col)) continue;
                var bare = col.Contains('.') ? col.Split('.')[^1] : col;
                if (ordered == null)
                    ordered = desc ? rows.OrderByDescending(r => SortKey(r, bare)) : rows.OrderBy(r => SortKey(r, bare));
                else
                    ordered = desc ? ordered.ThenByDescending(r => SortKey(r, bare)) : ordered.ThenBy(r => SortKey(r, bare));
            }
        }
        // 恒再按 id DESC 兜底（TsID 雪花混排现状，C22 不改动）
        return ordered == null
            ? rows.OrderByDescending(r => SortKey(r, defaultCol)).ToList()
            : ordered.ThenByDescending(r => SortKey(r, defaultCol)).ToList();
    }

    private static string AliasOf(string col) => col.Contains('.') ? col[..col.IndexOf('.')] + "." : "t.";

    private static object? SortKey(Dictionary<string, object?> r, string bare)
    {
        if (!r.TryGetValue(bare, out var v)) return null;
        if (v is long l) return l;
        if (v is int i) return (long)i;
        if (v is DateTime dt) return dt;
        return v?.ToString();
    }

    private static int CompareValues(string? a, string? b)
    {
        // 数值优先（列值数值化比较），字符串兜底
        if (double.TryParse(a, out var x) && double.TryParse(b, out var y)) return x.CompareTo(y);
        return string.CompareOrdinal(a, b);
    }

    private static string? Str(Dictionary<string, object?> r, string col)
    {
        if (r.TryGetValue(col, out var direct)) return direct?.ToString();
        var bare = col.Contains('.') ? col.Split('.')[^1] : col;
        return r.TryGetValue(bare, out var v) ? v?.ToString() : null;
    }

    private static string? StrOf(object? v) => v?.ToString();

    // ═══ 行 DTO ↔ 字典（排序/过滤统一走字典）═══

    protected virtual HashSet<string> DefineWhitelist { get; } = new()
    {
        "t.id", "t.name", "t.display_name", "t.type", "t.state", "t.version",
        "t.create_time", "t.update_time",
    };

    protected virtual HashSet<string> TaskWhitelist { get; } = new()
    {
        "t.id", "t.task_name", "t.display_name", "t.task_type", "t.perform_type", "t.task_state",
        "t.operator", "t.form_key", "t.create_time", "t.finish_time", "t.expire_time",
        "t.process_instance_id", "t.task_parent_id", "t.variable",
        "pi.id", "pi.business_no", "pi.operator", "pi.create_time", "pi.state",
        "pd.name", "pd.display_name", "pd.type",
        "pta.actor_id", "pta.process_task_id",
    };

    protected virtual HashSet<string> InstanceWhitelist { get; } = new()
    {
        "t.id", "t.parent_id", "t.process_define_id", "t.state", "t.business_no",
        "t.operator", "t.create_time", "t.expire_time", "t.variable",
        "pd.name", "pd.display_name", "pd.type", "pd.version",
    };

    protected virtual HashSet<string> CcInstanceWhitelist { get; } = new()
    {
        "t.id", "t.process_define_id", "t.state", "t.business_no", "t.operator",
        "t.create_time", "t.variable",
        "pd.name", "pd.display_name", "pd.type", "pd.version",
        "cc.actor_id", "cc.state",
    };

    protected static Dictionary<string, object?> DefineToDict(ProcessDefine d) => new()
    {
        ["id"] = d.Id,
        ["name"] = d.Name,
        ["display_name"] = d.DisplayName,
        ["type"] = d.Type,
        ["state"] = d.State,
        ["version"] = d.Version,
        ["create_time"] = d.CreateTime,
        ["create_user"] = d.CreateUser,
        ["update_time"] = d.UpdateTime,
        ["update_user"] = d.UpdateUser,
    };

    protected static IProcessRepository.DefineRow RowToDefine(Dictionary<string, object?> d) => new()
    {
        Id = AsLong(d, "id"),
        Name = AsStr(d, "name"),
        DisplayName = AsStr(d, "display_name"),
        Type = AsStr(d, "type"),
        State = AsInt(d, "state"),
        Version = AsInt(d, "version"),
        CreateTime = AsDate(d, "create_time"),
        CreateUser = AsStr(d, "create_user"),
        UpdateTime = AsDate(d, "update_time"),
        UpdateUser = AsStr(d, "update_user"),
    };

    protected static Dictionary<string, object?> TaskToDict(ProcessTask t, ProcessInstance? pi, ProcessDefine? pd) => new()
    {
        ["id"] = t.TaskId,
        ["process_instance_id"] = t.ProcessInstanceId,
        ["task_name"] = t.TaskName,
        ["display_name"] = t.DisplayName,
        ["task_type"] = t.TaskType == null ? null : (int)t.TaskType,
        ["perform_type"] = t.PerformType == null ? null : (int)t.PerformType,
        ["task_state"] = t.TaskState,
        ["operator"] = t.ActorId,
        ["finish_time"] = t.FinishTime,
        ["expire_time"] = t.ExpireTime,
        ["form_key"] = t.FormKey,
        ["task_parent_id"] = t.ParentTaskId,
        ["variable"] = ToJson(t.Variables),
        ["create_time"] = t.CreateTime,
        ["create_user"] = t.CreateUser,
        ["update_time"] = t.UpdateTime,
        ["update_user"] = t.UpdateUser,
        ["process_define_name"] = pd?.Name,
        ["process_define_display_name"] = pd?.DisplayName,
        ["process_define_version"] = pd?.Version,
        ["instance_variable"] = pi == null ? null : ToJson(pi.Variables),
        ["instance_create_time"] = pi?.CreateTime,
    };

    protected IProcessRepository.TaskRow RowToTask(Dictionary<string, object?> d) => new()
    {
        Id = AsLong(d, "id"),
        ProcessInstanceId = AsLong(d, "process_instance_id"),
        TaskName = AsStr(d, "task_name"),
        DisplayName = AsStr(d, "display_name"),
        TaskType = AsInt(d, "task_type"),
        PerformType = AsInt(d, "perform_type"),
        TaskState = AsInt(d, "task_state"),
        Operator = AsStr(d, "operator"),
        FinishTime = AsDate(d, "finish_time"),
        ExpireTime = AsDate(d, "expire_time"),
        FormKey = AsStr(d, "form_key"),
        TaskParentId = AsLong(d, "task_parent_id"),
        Variable = AsStr(d, "variable"),
        CreateTime = AsDate(d, "create_time"),
        CreateUser = AsStr(d, "create_user"),
        UpdateTime = AsDate(d, "update_time"),
        UpdateUser = AsStr(d, "update_user"),
        ProcessDefineName = AsStr(d, "process_define_name"),
        ProcessDefineDisplayName = AsStr(d, "process_define_display_name"),
        ProcessDefineVersion = AsInt(d, "process_define_version"),
        InstanceVariable = AsStr(d, "instance_variable"),
        InstanceCreateTime = AsDate(d, "instance_create_time"),
    };

    protected static Dictionary<string, object?> InstanceToDict(
        ProcessInstance t, ProcessDefine? pd, CcRow? cc) => new()
    {
        ["id"] = t.InstanceId,
        ["parent_id"] = t.ParentId,
        ["process_define_id"] = t.DefineId,
        ["state"] = t.State,
        ["parent_node_name"] = t.ParentNodeName,
        ["business_no"] = t.BusinessNo,
        ["operator"] = t.Operator,
        ["expire_time"] = t.ExpireTime,
        ["variable"] = ToJson(t.Variables),
        ["create_time"] = t.CreateTime,
        ["create_user"] = t.CreateUser,
        ["update_time"] = t.UpdateTime,
        ["update_user"] = t.UpdateUser,
        ["process_define_name"] = pd?.Name,
        ["process_define_display_name"] = pd?.DisplayName,
        ["process_define_version"] = pd?.Version,
        ["cc_actor_id"] = cc?.ActorId,
        ["cc_state"] = cc?.State,
    };

    protected IProcessRepository.InstanceRow RowToInstance(Dictionary<string, object?> d) => new()
    {
        Id = AsLong(d, "id"),
        ParentId = AsLong(d, "parent_id"),
        ProcessDefineId = AsLong(d, "process_define_id"),
        State = AsInt(d, "state"),
        ParentNodeName = AsStr(d, "parent_node_name"),
        BusinessNo = AsStr(d, "business_no"),
        Operator = AsStr(d, "operator"),
        ExpireTime = AsDate(d, "expire_time"),
        Variable = AsStr(d, "variable"),
        CreateTime = AsDate(d, "create_time"),
        CreateUser = AsStr(d, "create_user"),
        UpdateTime = AsDate(d, "update_time"),
        UpdateUser = AsStr(d, "update_user"),
        ProcessDefineName = AsStr(d, "process_define_name"),
        ProcessDefineDisplayName = AsStr(d, "process_define_display_name"),
        ProcessDefineVersion = AsInt(d, "process_define_version"),
    };

    // ═══ JSON / 类型工具 ═══

    protected static string? ToJson(FlowData data)
    {
        if (data == null || data.Count == 0) return null;
        return DefaultJsonProvider.Instance.ToJson(data);
    }

    protected static long? AsLong(Dictionary<string, object?> d, string k) =>
        d.TryGetValue(k, out var v) ? v as long? ?? (v as int?) ?? (long?)AsInt(d, k) : null;

    protected static int? AsInt(Dictionary<string, object?> d, string k)
    {
        if (!d.TryGetValue(k, out var v) || v == null) return null;
        if (v is int i) return i;
        if (v is long l) return (int)l;
        return int.TryParse(v.ToString(), out var p) ? p : null;
    }

    protected static string? AsStr(Dictionary<string, object?> d, string k) =>
        d.TryGetValue(k, out var v) ? v?.ToString() : null;

    protected static DateTime? AsDate(Dictionary<string, object?> d, string k) =>
        d.TryGetValue(k, out var v) ? v as DateTime? : null;

    // ═══ 克隆（隔离聚合副本，防测试/调用方改内部存储）═══

    protected static ProcessDefine CloneDefine(ProcessDefine d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        DisplayName = d.DisplayName,
        Type = d.Type,
        State = d.State,
        Content = d.Content,
        Version = d.Version,
        UpdateUser = d.UpdateUser,
    };

    protected static ProcessInstance CloneInstance(ProcessInstance i) => new()
    {
        InstanceId = i.InstanceId,
        ParentId = i.ParentId,
        DefineId = i.DefineId,
        State = i.State,
        ParentNodeName = i.ParentNodeName,
        BusinessNo = i.BusinessNo,
        Operator = i.Operator,
        ExpireTime = i.ExpireTime,
        Variables = i.Variables.Copy(),
        CreateTime = i.CreateTime,
        CreateUser = i.CreateUser,
        UpdateTime = i.UpdateTime,
        UpdateUser = i.UpdateUser,
    };

    protected static ProcessTask CloneTask(ProcessTask t) => new()
    {
        TaskId = t.TaskId,
        ProcessInstanceId = t.ProcessInstanceId,
        TaskName = t.TaskName,
        DisplayName = t.DisplayName,
        TaskType = t.TaskType,
        PerformType = t.PerformType,
        TaskState = t.TaskState,
        ActorId = t.ActorId,
        ActorIds = new List<string>(t.ActorIds),
        FinishTime = t.FinishTime,
        ExpireTime = t.ExpireTime,
        FormKey = t.FormKey,
        ParentTaskId = t.ParentTaskId,
        Variables = t.Variables.Copy(),
        CreateTime = t.CreateTime,
        CreateUser = t.CreateUser,
        UpdateTime = t.UpdateTime,
        UpdateUser = t.UpdateUser,
    };

    protected string BaseInfo() =>
        $"memory repo: defines={Defines.Count} instances={Instances.Count} tasks={Tasks.Count}";

    /// <summary>调试/断言辅助导出。</summary>
    public override string ToString() => new StringBuilder(BaseInfo()).ToString();
}

using System.Text.RegularExpressions;

namespace Mldong.Jeeflow.Core;
/// <summary>工具类（零依赖，对齐 Java StringUtils/FlowUtil）。</summary>
public static class FlowUtil
{
    public const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>追加用户信息到流程参数（C25：u_* 仅 start 注入一次；flow.auto/flow.admin 跳过）。</summary>
    public static async Task AddUserInfoToArgsAsync(
        string? op, FlowData args, IUserProvider? userProvider)
    {
        if (userProvider == null) return;
        if (string.Equals(FlowConst.AutoId, op, StringComparison.OrdinalIgnoreCase)
            || string.Equals(FlowConst.AdminId, op, StringComparison.OrdinalIgnoreCase))
            return;
        var u = await userProvider.GetUserAsync(op ?? "");
        if (u == null) return;
        args[FlowConst.UserUserId] = u.UserId ?? op;
        args[FlowConst.UserRealName] = u.RealName ?? op;
        args[FlowConst.UserDeptId] = u.DeptId;
        args[FlowConst.UserDeptName] = u.DeptName;
        args[FlowConst.UserPostId] = u.PostId;
        args[FlowConst.UserPostName] = u.PostName;
    }

    /// <summary>自动构造标题：{realName}的{displayName}-{yyyy-MM-dd HH:mm}（C25：HH:mm 非 HH:mm:ss）。</summary>
    public static void AddAutoGenTitle(string? displayName, FlowData args, IClock clock)
    {
        var realName = args.GetStr(FlowConst.UserRealName, "");
        var title = $"{realName}的{displayName}-{clock.Now:yyyy-MM-dd HH:mm}";
        args[FlowConst.AutoGenTitle] = title;
    }

    /// <summary>判断是否为第一个任务节点（开始节点的直接后继）。</summary>
    public static bool IsFirstTaskName(ProcessModel model, string? taskName)
    {
        var start = model.GetStart();
        if (start == null) return false;
        return start.Outputs.Any(tm =>
            tm.To != null && tm.To.Equals(taskName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 能否退回到 parent —— 照 mldong-boot2 NodeModel.canRejected：自 current 的入边回溯，命中 parent 放行；
    /// 入边来源是 fork/join/start 时**跳过该条入边、不再深入**（boot2 是 continue，不是穿越），其余来源递归。
    /// subprocess 在 boot2 里被注释掉，等同普通节点。
    /// </summary>
    public static bool CanRejected(NodeModel current, NodeModel parent)
    {
        foreach (var tm in current.Inputs)
        {
            var source = tm.Source;
            if (ReferenceEquals(source, parent)) return true;
            if (source is ForkModel or JoinModel or StartModel) continue;
            if (source != null && CanRejected(source, parent)) return true;
        }
        return false;
    }

    /// <summary>解析期待完成时间（变量引用 / 相对时间 5s/10m/24h/3d / 绝对时间）。</summary>
    public static DateTime? ProcessTime(string? expireTime, FlowData args, IClock clock)
    {
        if (string.IsNullOrEmpty(expireTime)) return null;
        if (args.ContainsKey(expireTime))
        {
            var obj = args[expireTime];
            switch (obj)
            {
                case DateTime dt: return dt;
                case long l: return DateTime.UnixEpoch.AddMilliseconds(l).ToLocalTime();
                case string s:
                    if (DateTime.TryParseExact(s, TimeFormat, null,
                            System.Globalization.DateTimeStyles.None, out var parsed)) return parsed;
                    return null;
            }
        }
        var now = clock.Now;
        if (expireTime.EndsWith("s") && int.TryParse(expireTime[..^1], out var seconds))
            return now.AddSeconds(seconds);
        if (expireTime.EndsWith("m") && int.TryParse(expireTime[..^1], out var minutes))
            return now.AddMinutes(minutes);
        if (expireTime.EndsWith("h") && int.TryParse(expireTime[..^1], out var hours))
            return now.AddHours(hours);
        if (expireTime.EndsWith("d") && int.TryParse(expireTime[..^1], out var days))
            return now.AddDays(days);
        if (DateTime.TryParseExact(expireTime, TimeFormat, null,
                System.Globalization.DateTimeStyles.None, out var abs)) return abs;
        return null;
    }

    /// <summary>表单字段前缀（f_）。</summary>
    public const string FormFieldPrefix = "f_";

    /// <summary>
    /// 办理提交按任务节点字段权限过滤（issues/26）：只读(1)/隐藏(3)的 f_ 字段不入变量。
    /// 键格式双兼容（issues/25）：PERMISSION_f_{全名} 优先，PERMISSION_{去前缀名} 兼容（C19）。
    /// </summary>
    public static FlowData FilterFieldByPerm(FlowData? args, ProcessModel? model, string? taskName)
    {
        if (args == null || model == null || taskName == null) return args ?? new FlowData();
        if (model.GetNode(taskName) is not TaskModel node) return args;
        var fieldPerm = node.Ext;
        if (fieldPerm.Count == 0) return args;
        var filtered = new FlowData();
        foreach (var kv in args)
        {
            var key = kv.Key;
            if (key.StartsWith(FormFieldPrefix, StringComparison.Ordinal)
                && key.Length > FormFieldPrefix.Length)
            {
                var fieldName = key[FormFieldPrefix.Length..];
                var has = fieldPerm.TryGetValue(FlowConst.FieldPermissionPrefix + FormFieldPrefix + fieldName, out var p);
                if (!has) has = fieldPerm.TryGetValue(FlowConst.FieldPermissionPrefix + fieldName, out p);
                if (has)
                {
                    var perm = ToInt(p);
                    if (perm is 1 or 3) continue; // 只读/隐藏：剔除（不入变量）
                }
            }
            filtered[key] = kv.Value;
        }
        return filtered;
    }

    private static int ToInt(object? value)
    {
        if (value is int i) return i;
        if (value is long l) return (int)l;
        if (value != null && int.TryParse(value.ToString(), out var v)) return v;
        return -1;
    }
}

/// <summary>
/// 创建任务处理器（对齐 Java CreateTaskHandler）：
/// 参与人解析（tf_nextNodeOperator → assignee 字面量/变量（applicant→发起人）→ assignmentHandler）
/// → 建任务（会签走 createCountersignTasks）→ 执行注册的 FlowInterceptor 池。
/// TASK_START 不在此 fire（taskId 尚未生成，issues/13）——引擎在落库后逐任务 fire。
/// </summary>
public class CreateTaskHandler : IHandler
{
    private readonly TaskModel _taskModel;

    public CreateTaskHandler(TaskModel taskModel) => _taskModel = taskModel;

    public async Task HandleAsync(Execution execution)
    {
        var instance = execution.ProcessInstance!;
        var model = execution.ProcessModel!;
        var op = execution.Operator;

        // 当前节点上下文（TransitionModel 直连 fire 时未走 NodeModel.Execute，需显式设置）
        execution.NodeModel = _taskModel;
        var actors = await ResolveActorsAsync(_taskModel, model, execution);

        List<ProcessTask> tasks;
        if (_taskModel.PerformType == WfPerformType.Countersign)
        {
            // 建单不变量：parent＝本 execution 刚办结的任务；发起时为 null ⇒ 工厂落 0
            tasks = instance.CreateCountersignTasks(_taskModel, actors, op,
                execution.ProcessTask?.TaskId,
                FlowUtil.IsFirstTaskName(model, _taskModel.Name),
                execution.Context.ClockOrDefault);
        }
        else
        {
            var task = instance.CreateTask(_taskModel, _taskModel.DisplayName, actors, op,
                execution.ProcessTask?.TaskId,
                FlowUtil.IsFirstTaskName(model, _taskModel.Name),
                execution.Context.ClockOrDefault);
            tasks = new List<ProcessTask> { task };
        }

        execution.AddTasks(tasks);

        // 执行注册的拦截器池（有序）
        foreach (var interceptor in execution.Context.Interceptors)
            await interceptor.Run(execution);
    }

    private static async Task<List<string>> ResolveActorsAsync(
        TaskModel taskModel, ProcessModel model, Execution execution)
    {
        var actors = new List<string>();
        var args = execution.Args;
        // 1. 动态指定下一节点处理人优先（v1.0.1：对齐 boot2/boot3 tf_nextNodeOperator）
        var nextNodeOperator = args.GetObj(FlowConst.NextNodeOperator);
        if (nextNodeOperator != null && !string.IsNullOrEmpty(nextNodeOperator.ToString()))
        {
            if (nextNodeOperator is System.Collections.ICollection coll)
            {
                foreach (var o in coll)
                {
                    var t = o?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(t) && !actors.Contains(t)) actors.Add(t);
                }
            }
            else
            {
                foreach (var a in nextNodeOperator.ToString()!.Split(','))
                {
                    var t = a.Trim();
                    if (t.Length > 0 && !actors.Contains(t)) actors.Add(t);
                }
            }
            return actors;
        }
        // 2. 固定指派 assignee——token 即变量 key，能替换就换，换不了就是字面量；
        //    applicant → 流程发起人（子串替换，对齐 Java contains/replace 语义）
        var assignee = taskModel.Assignee;
        if (!string.IsNullOrEmpty(assignee))
        {
            foreach (var raw in assignee.Split(','))
            {
                var token = raw.Trim();
                if (token.Length == 0) continue;
                if (token.Contains("applicant"))
                    token = token.Replace("applicant", execution.ProcessInstance?.Operator);
                if (args.TryGetValue(token, out var v) && v != null)
                {
                    if (v is System.Collections.ICollection coll)
                    {
                        foreach (var o in coll)
                        {
                            var t = o?.ToString()?.Trim();
                            if (!string.IsNullOrEmpty(t) && !actors.Contains(t)) actors.Add(t);
                        }
                    }
                    else
                    {
                        var t = v.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(t) && !actors.Contains(t)) actors.Add(t);
                    }
                }
                else if (!actors.Contains(token))
                {
                    actors.Add(token);
                }
            }
        }
        // 3. 动态指派处理器 assignmentHandler（assignee 为空时才生效）；声明名不可解析显式报错（C20）
        if (actors.Count == 0)
        {
            var handlerName = taskModel.AssignmentHandler;
            if (!string.IsNullOrEmpty(handlerName))
            {
                var handler = execution.Context.FindAssignmentHandler(handlerName.Trim());
                var result = await handler.AssignAsync(execution);
                if (!string.IsNullOrEmpty(result))
                {
                    foreach (var a in result.Split(','))
                    {
                        var t = a.Trim();
                        if (t.Length > 0 && !actors.Contains(t)) actors.Add(t);
                    }
                }
            }
        }
        return actors;
    }
}

/// <summary>
/// 会签任务处理器（对齐 Java CountersignHandler，C8–C10）：
/// 串行：每完成一人按任务变量 operatorList_{node}/loopCounter_{node} 推进创建下一位，
/// 最后一位完成才 merged；并行：全部完成或完成条件表达式求值 merged。
/// submitType=20 默认软拒绝（正常完成 + countersignDisagreeFlag=1）；仅
/// countersignCompletionCondition==ONE_VOTE_VETO 时一票否决提前 merged。
/// merged 后废弃本节点残留 DOING 任务（issues/91）。
/// </summary>
public class CountersignHandler : IHandler
{
    private readonly TaskModel _taskModel;

    public CountersignHandler(TaskModel taskModel) => _taskModel = taskModel;

    public Task HandleAsync(Execution execution)
    {
        var instance = execution.ProcessInstance!;
        var allTasks = instance.Tasks
            .Where(t => _taskModel.Name == t.TaskName)
            .ToList();
        var finishedCount = allTasks.Count(t => t.TaskState == (int)WfTaskState.Finished);

        var submitType = execution.Args.GetInt(FlowConst.SubmitType);
        if (submitType == (int)WfSubmitType.CountersignDisagree
            && IsOneVoteVeto(_taskModel.CountersignCompletionCondition))
        {
            execution.IsMerged = true;
            AbandonRemainingCountersignTasks(instance, execution.Operator,
                execution.Context.ClockOrDefault);
            return Task.CompletedTask;
        }

        var countersignType = _taskModel.CountersignType;
        bool merged;
        if (countersignType == WfCountersignType.Sequential)
        {
            // 串行逐个创建（issues/93）：任意时刻恰 1 个 DOING；计数状态在任务变量
            merged = false;
            var completed = execution.ProcessTask;
            List<string>? operatorList = null;
            var loopCounter = 0;
            if (completed?.Variables != null)
            {
                var ol = completed.Variables.GetObj(
                    $"{FlowConst.CountersignOperatorList}_{_taskModel.Name}");
                operatorList = ToStringList(ol);
                loopCounter = completed.Variables.GetInt(
                    $"{FlowConst.LoopCounter}_{_taskModel.Name}", 0);
            }
            if (operatorList is { Count: > 0 } && loopCounter + 1 < operatorList.Count)
            {
                CreateNextCountersignTask(instance, operatorList[loopCounter + 1],
                    loopCounter + 1, operatorList.Count, execution);
            }
            else
            {
                // 最后一位完成 → 流转（全部成员完成才推进，issues/44 E16）
                merged = true;
            }
        }
        else
        {
            // 并行会签：检查条件
            var cond = _taskModel.CountersignCompletionCondition;
            if (string.IsNullOrEmpty(cond))
            {
                merged = finishedCount >= allTasks.Count;
            }
            else
            {
            try
            {
                var vars = BuildCountersignVars(instance, allTasks, _taskModel.Name);
                foreach (var kv in execution.Args) vars[kv.Key] = kv.Value;
                    var result = execution.Context.ExpressionEvaluatorOrDefault.Eval(cond, vars);
                    merged = result is true;
                }
                catch (Exception)
                {
                    merged = false;
                }
            }
        }

        if (merged)
        {
            // 会签节点推进后废弃本节点剩余 DOING 会签任务（issues/91），不留孤儿待办
            AbandonRemainingCountersignTasks(instance, execution.Operator,
                execution.Context.ClockOrDefault);
        }
        execution.IsMerged = merged;
        return Task.CompletedTask;
    }

    /// <summary>一票否决开关：节点 countersignCompletionCondition == ONE_VOTE_VETO（忽略大小写）。</summary>
    private static bool IsOneVoteVeto(string? condition) =>
        condition != null && FlowConst.OneVoteVeto.Equals(condition.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>废弃本节点剩余 DOING 会签任务（刚完成的任务此刻已 FINISHED，不误伤）。</summary>
    private void AbandonRemainingCountersignTasks(ProcessInstance instance, string? op, IClock clock)
    {
        foreach (var task in instance.Tasks)
        {
            if (_taskModel.Name == task.TaskName && task.IsDoing())
                task.Abandon(op, clock);
        }
    }

    /// <summary>
    /// 串行会签推进：创建下一位成员任务（issues/93）。新任务同时加入聚合根（instance.tasks）
    /// 与 execution（persistTasks 经 saveTask 落库分配任务 id）。计数状态随任务变量持久化。
    /// </summary>
    private void CreateNextCountersignTask(
        ProcessInstance instance, string nextActor, int nextLoopCounter, int total, Execution execution)
    {
        var node = _taskModel.Name;
        var next = ProcessTask.Create(
            instance.InstanceId, node, _taskModel.DisplayName,
            _taskModel.TaskType, _taskModel.PerformType, _taskModel.Form,
            new List<string> { nextActor }, execution.Operator,
            // 建单不变量：串行会签下一位成员的 parent＝刚办结的那一位
            execution.ProcessTask?.TaskId,
            FlowUtil.IsFirstTaskName(execution.ProcessModel!, node),
            execution.Context.ClockOrDefault);
        next.Variables[$"{FlowConst.CountersignOperatorList}_{node}"] =
            new List<object?>(ReadOperatorList(execution, node));
        next.Variables[$"{FlowConst.LoopCounter}_{node}"] = nextLoopCounter;
        next.Variables[$"{FlowConst.NrOfInstances}_{node}"] = total;
        instance.Tasks.Add(next);
        execution.AddTask(next);
    }

    private static List<string> ReadOperatorList(Execution execution, string? node)
    {
        var completed = execution.ProcessTask;
        if (completed?.Variables != null)
            return ToStringList(completed.Variables.GetObj(
                $"{FlowConst.CountersignOperatorList}_{node}"));
        return new List<string>();
    }

    /// <summary>办理人列表取值兼容（JSON 反序列化后可能是 List / 标量）。</summary>
    internal static List<string> ToStringList(object? value)
    {
        var result = new List<string>();
        if (value == null) return result;
        if (value is System.Collections.ICollection coll)
        {
            foreach (var o in coll)
            {
                var s = o?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(s)) result.Add(s);
            }
        }
        else
        {
            var s = value.ToString()?.Trim();
            if (!string.IsNullOrEmpty(s)) result.Add(s);
        }
        return result;
    }

    private static FlowData BuildCountersignVars(
        ProcessInstance instance, List<ProcessTask> allTasks, string? nodeName)
    {
        var prefix = FlowConst.CountersignVariablePrefix + nodeName + "_";
        var vars = new FlowData();
        foreach (var kv in instance.Variables) vars[kv.Key] = kv.Value;
        vars[prefix + FlowConst.NrOfInstances] = allTasks.Count;
        vars[prefix + FlowConst.NrOfActivateInstances] = allTasks.Count(t => t.IsDoing());
        vars[prefix + FlowConst.NrOfCompletedInstances] = allTasks.Count(t => t.IsFinished());
        return vars;
    }
}

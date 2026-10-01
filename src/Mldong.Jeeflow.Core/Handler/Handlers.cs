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

    /// <summary>
    /// 主键类参数档位（issues/142 B 批 · spec 06 §2.11「主键类参数另判一档」）。
    /// <para>这与"归属值为空 ⇒ 丢弃"是<b>两件事</b>：归属值可有可无，主键没有就是调用方写错了，
    /// 静默接受会把 <c>process_task_id=0</c> 这种孤儿脏行钉进表里。缺失／空串／非正数一律响亮报错，
    /// 文案不带内部码（issues/121 口径：内部码不进出口 msg）。
    /// 门面腿另有一档：沿用该 action 既有的"缺参数"信封，不在这里造新文案。</para>
    /// </summary>
    public static long RequireTaskId(long? taskId)
    {
        if (taskId == null || taskId.Value <= 0)
            throw new JeeflowException("processTaskId 缺失或非法");
        return taskId.Value;
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

    /// <summary>
    /// 解析期待完成时间（变量引用 / 相对时间 5s/10m/24h/3d / 绝对时间）。三档<b>顺序不能变</b>：
    /// ① <paramref name="args"/> 里有与表达式同名的键 ⇒ 取该键的值（<c>DateTime</c> / 毫秒时间戳 /
    /// "yyyy-MM-dd HH:mm:ss" 串；<b>其它类型不落判，继续往下走</b>相对/绝对档）；
    /// ② 以 <c>s|m|h|d</c> 结尾 ⇒ <c>clock.Now</c> + 偏移（<c>d</c> 走日历加天）；
    /// ③ 把表达式本身当绝对时刻解析。
    /// <para>解析不出 ⇒ 返回 <b>null</b>（这一列留空），绝不退回 <c>now()</c>——那等于静默造一个
    /// "建单即逾期"的值，正是 issues/126 的病灶形状。</para>
    /// <para><b>八栈一致</b>（issues/137 C，java 参考实现 <c>6bdf41b</c> 已对齐本栈口径）：误配的相对档
    /// （前缀不是整数，如 <c>xh</c> / <c>2.5h</c> / <c>3hh</c> / 光一个后缀字母）<b>落穿</b>到绝对档、
    /// 最终 NULL，<b>不抛错</b>——配置写错不该让流程卡死，也不许退化成"取当前时间"。
    /// 旧注释里"Java 的 <c>Integer.parseInt</c> 会抛异常打断建单、本栈是故意差异"那句已过期：
    /// java 侧同样改成落穿，八栈这条不再有分叉，"前缀须整数"不再是任何栈的抛错理由。</para>
    /// <para><b>相对档前缀须非负</b>（issues/137 D，owner 2026-10-01 拍"判非负"，基准＝java
    /// <c>FlowUtil.parseIntOrNull</c> <c>1649955</c>）：四档解析出的整数 <c>&lt; 0</c> 一律算"解析不出来"，
    /// 与上面那些误配同路——落穿绝对档、仍算不出即 NULL。放行 <c>-5h</c> 会算出一个<b>过去</b>的时刻 ⇒
    /// 新建的行当场即逾期，比"没配到期时间"更难发现，也正与本卡"任何一档都不许退化成取当前时间"
    /// （issues/126 病灶形状）的精神冲突。<c>d</c> 档同判：它走 <c>AddDays</c> 日历加天，负数＝历日倒退，
    /// 不是乘 86400 秒。
    /// <b>只裁负、不裁加号</b>：<c>int.TryParse</c> 天然收 <c>-5</c> 也收 <c>+5</c>，这里只加比较、
    /// <b>不换成正则去禁符号</b>——各栈整数解析（python <c>[+-]?</c>、node <c>[-+]?\d+</c>、
    /// php <c>[+-]?\d{1,18}</c>）都收 '+'，裁掉加号等于新造一处跨栈分叉。</para>
    /// <para><b>相对档前缀允许两端空白</b>（issues/137 E，owner 2026-10-01 拍"统一 trim"，基准＝java
    /// <c>FlowUtil.parseIntOrNull</c> <c>bf1f401</c>）：<b>本栈不显式 trim，靠的是 <c>int.TryParse</c>
    /// 的默认空白容忍</b>——默认 <c>NumberStyles.Integer</c> 含 <c>AllowLeadingWhite|AllowTrailingWhite</c>，
    /// 于是 <c>" 2h"</c> 切出的前缀 <c>" 2"</c> 天然解成 2（实测 <c>int.TryParse(" 2")==true</c>），
    /// "同一份流程定义别家有到期时间、这一家没有"这件分叉在本栈不存在。
    /// ⚠️ 这是一处<b>隐式依赖</b>，别被"六栈统一写法"顺手改掉：<b>若将来把 <c>int.TryParse</c> 换成正则
    /// （如 <c>^[+-]?\d+$</c>）或 <c>ParseExact</c>，必须显式 trim 前缀</b>，否则
    /// <c>ExpireTime126Tests.PaddedRelativePrefixStillApplies</c> 的①当场红。
    /// 反过来也<b>不要</b>改成"整个表达式先去空白再判末位单位"——<c>"2h "</c> 的末位是空格、四档
    /// <c>EndsWith</c> 全不认，按契约②必须落穿成 NULL；裁的边界只到<b>前缀</b>，变量档的键名与
    /// 绝对档的串同样不 trim（⑥/绝对档格钉住）。</para>
    /// <para>钟一律由调用点注入（<c>clock ?? SystemClock.Instance</c>，issues/120）：本栈是八栈里唯一
    /// 域层带钟注入的栈，改成裸 <c>DateTime.Now</c> 会让"到期时间"绕过时钟出口、测试失去确定性。</para>
    /// </summary>
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
        // issues/137 D（owner 2026-10-01 拍"判非负"，基准＝java FlowUtil.parseIntOrNull 1649955）：
        // 四档各加 `>= 0` 守卫——负数前缀算"解析不出来"，条件不成立即天然落穿到下面的绝对档，
        // 仍失败则 NULL。放行 `-5h` 会算出一个过去的时刻 ⇒ 新建行当场即逾期（比"没配"更难发现），
        // 也不许退化成取当前时间（issues/126 病灶形状）。d 档走 AddDays 日历加天，负数＝历日倒退，同判。
        // 只裁负、不裁加号：int.TryParse 本身就收 `+5`，这里只加比较判断，不换成禁符号的正则——
        // python [+-]? / node [-+]?\d+ / php [+-]?\d{1,18} 都收 '+'，裁加号等于新造一处跨栈分叉。
        // issues/137 E（相对档前缀允许两端空白）：本栈**不显式 trim**——int.TryParse 的默认
        // NumberStyles.Integer 已含 AllowLeadingWhite|AllowTrailingWhite，`" 2h"` 的前缀 `" 2"` 天然解成 2。
        // 隐式依赖，改动前看方法注释那段 ⚠️：换成正则/ParseExact 就必须补 trim；而**别**在这里
        // `expireTime = expireTime.Trim()`（整串去空白）——那会让 `"2h "` 变成合法的 `2h`，契约②要的
        // 是"末位不是 s/m/h/d ⇒ 认不出单位 ⇒ 落穿 NULL"；变量档的键名与绝对档的串同样不 trim。
        if (expireTime.EndsWith("s") && int.TryParse(expireTime[..^1], out var seconds) && seconds >= 0)
            return now.AddSeconds(seconds);
        if (expireTime.EndsWith("m") && int.TryParse(expireTime[..^1], out var minutes) && minutes >= 0)
            return now.AddMinutes(minutes);
        if (expireTime.EndsWith("h") && int.TryParse(expireTime[..^1], out var hours) && hours >= 0)
            return now.AddHours(hours);
        if (expireTime.EndsWith("d") && int.TryParse(expireTime[..^1], out var days) && days >= 0)
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
        // issues/142 B 批 · spec 06 §2.11：逗号串与数组<b>两形同判据</b>——逐元素 trim、
        // 空串/纯空白/null 丢弃、同次调用折叠，数字元素收敛成字符串（旧形状里 null 会被串成
        // "null"、整条数组会被串成 .NET 类型名当一个人用）。
        // 归一后<b>为空 ⇒ 与"没填"同档</b>：继续回落 assignee／assignmentHandler。
        // 旧形状在这一档上是两形两样：串腿给 "" 走 `IsNullOrEmpty` 回落 assignee，
        // 数组腿给 [""] 却被当成"已指派"用 ⇒ 下一节点<b>零参与者</b>（§6.1 点名的死锁黑洞形状，
        // 实测读数 Expected ["leader"] / Actual []）。
        var nextOps = PageQuery.NormalizeActors(args.GetObj(FlowConst.NextNodeOperator));
        if (nextOps.Count > 0)
        {
            actors.AddRange(nextOps);
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
                    // 变量值同样过归属值判据单点（两形同判据：串／数组／标量一个答案）
                    actors.AddRange(ExcludeExisting(actors, PageQuery.NormalizeActors(v)));
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
                actors.AddRange(ExcludeExisting(actors, PageQuery.NormalizeActors(result)));
            }
        }
        return actors;
    }

    /// <summary>并入新参与者时跳过已有的（判据单点已折叠同次调用内的重复，这里只防与前一档重排）。</summary>
    private static List<string> ExcludeExisting(List<string> existing, List<string> incoming) =>
        incoming.Where(a => !existing.Contains(a)).ToList();
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
        // issues/126 A · 写点⑤：这一支绕过 ProcessInstance.CreateTask 直建任务行，
        // 聚合根为此开 ApplyNodeExpireTime 公开入口让它上同一把尺子——基准侧 boot2 的串行推进是回调
        // createCountersignTask（ProcessTaskServiceImpl:485，内含 :524 那处到期写），
        // 不补就是"首成员有到期时间、第二/第三位没有"。变量源＝实例变量，钟沿用本 execution 的注入钟。
        instance.ApplyNodeExpireTime(next, _taskModel, execution.Context.ClockOrDefault);
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

    /// <summary>
    /// 办理人列表取值兼容（JSON 反序列化后可能是 List / 标量）。
    /// issues/142 B 批：判据不再在本栈另抄一份——直接复用归属值单点
    /// <see cref="PageQuery.NormalizeActors(object)"/>（逗号串/数组两形同判据、trim、空值丢弃、
    /// 同次调用折叠）。旧副本与单点只差"折叠重复"和"串形态不拆逗号"两处，正是要防的分叉
    /// （名册里同一个人排两次会多出一次串行投票）。
    /// </summary>
    internal static List<string> ToStringList(object? value) => PageQuery.NormalizeActors(value);

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

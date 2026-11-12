namespace Mldong.Jeeflow.Core;

/// <summary>
/// 「后加签（可重复）」的配置解析、写入口校验、轮次与账本读写
/// （对齐 Java <c>core/AddSignSupport</c>，形状基准＝本栈 <see cref="ReapplySupport"/>）。
///
/// <para><b>为什么校验一律排在改动状态之前</b>：内存仓储没有事务回滚，一旦先 <c>CompleteTask</c>
/// 再判错，那条任务行就在聚合里变成已完成而无人能改回来——「报错且无写入」是这条链的硬要求，
/// 所以 <see cref="ValidateSubmission"/> 由引擎在 <c>PrepareExecutionAsync</c> 里、
/// <c>instance.CompleteTask(...)</c> 之前调用，而不是放进 <see cref="AddSignHandler"/>。</para>
///
/// <para>逐字文案与规范 04／门面 06「失败 msg 跨栈统一文案」表一致（八栈同词）。</para>
/// </summary>
public static class AddSignSupport
{
    /// <summary>节点属性键：后加签开关（<c>properties</c> 平铺层，缺省 false）。</summary>
    public const string PropAllowAddSign = "allowAddSign";

    /// <summary>节点属性键：加签轮次上限（0＝不限，缺省 0）。</summary>
    public const string PropAddSignMaxRounds = "addSignMaxRounds";

    /// <summary>与 <c>tf_nextNodeOperator</c> 同传（规范 06 §2.7，逐字）。</summary>
    public const string MsgMutex = "tf_addSignOperators 与 tf_nextNodeOperator 互斥，不能同时提交";

    /// <summary>节点未开开关却传键（规范 06 §2.7，逐字；<c>{0}</c>＝节点名）。</summary>
    public const string MsgSwitchOff = "节点[{0}]未开启 allowAddSign，不接受 tf_addSignOperators";

    /// <summary>加给自己（规范 06 §2.7，逐字）。</summary>
    public const string MsgSelf = "不能把任务加签给自己";

    /// <summary>超轮次上限（规范 06 §2.7，逐字；<c>{0}</c>＝节点名，<c>{1}</c>＝上限）。</summary>
    public const string MsgMaxRounds = "节点[{0}]的加签轮次已达上限 {1}，本次提交被拒绝";

    /// <summary>会签节点配开关（规范 02 §4.3，逐字）。</summary>
    public const string MsgCountersignNode =
        "会签节点[{0}]不允许配置 allowAddSign：会签池内加人与会签之后加人是两种语义，推进闸会打架";

    /// <summary>首任务节点配开关（规范 02 §4.3，逐字）。</summary>
    public const string MsgFirstTaskNode =
        "首任务节点[{0}]不允许配置 allowAddSign：申请节点的办理是发起/重提语义，与加签路由打架";

    // ═══ 解析期配置校验（规范 02 §4.3 · 纪律照 reapplyMode 先例：非法值不得静默当缺省）═══

    /// <summary>
    /// 解析布尔开关：缺省 false；非布尔档报配置错误，不得静默落回缺省
    /// （配错要在解析期就能诊断出来，静默降级＝配置者以为开关生效）。
    /// <para><c>keyName</c> 决定报错文案里报哪一枚键名（java <c>parseAllow(value, nodeName, keyName)</c>
    /// 同尺）：<c>allowAddSign</c>／<see cref="RecallSupport.PropAllowRecall"/>／
    /// <see cref="CollectSupport.PropDynamicCountersign"/> 三枚共用这一把解析尺子，
    /// 缺省仍按后加签那枚，既有调用点与既有一格文案逐字不变。</para>
    /// </summary>
    public static bool ParseAllow(object? value, string? nodeName, string? keyName = null)
    {
        var key = keyName ?? PropAllowAddSign;
        if (value == null) return false;
        if (value is bool b) return b;
        var v = AsText(value);
        if (v.Length == 0) return false;
        if ("true".Equals(v, StringComparison.OrdinalIgnoreCase)) return true;
        if ("false".Equals(v, StringComparison.OrdinalIgnoreCase)) return false;
        throw new JeeflowException($"节点[{nodeName}]的 {key} 配置非法: {v}");
    }

    /// <summary>
    /// 解析 <see cref="PropAddSignMaxRounds"/>：缺省 0＝不限；非整数或负数报配置错误。
    /// <para>两档整数文案分别是「（必须是整数）」（值本身是小数）与「（必须是整数，0 表示不限）」
    /// （串形解不出整数），与 java <c>AddSignSupport.parseMaxRounds</c> 逐字同尺；
    /// 负数一律「（不得为负，0 表示不限）」。</para>
    /// </summary>
    public static int ParseMaxRounds(object? value, string? nodeName)
    {
        if (value == null) return 0;
        // 数值档：整数值放行，小数与负数各报一档。bool 不算数值（走下面的串档，
        // True 解不出整数 ⇒ 报"必须是整数"），与 java 的 Number instanceof 同一判据。
        if (value is not bool && TryNumeric(value, out var numeric))
        {
            if (numeric != Math.Truncate(numeric))
                throw new JeeflowException(
                    $"节点[{nodeName}]的 {PropAddSignMaxRounds} 配置非法: {AsText(value)}（必须是整数）");
            var n = (int)numeric;
            if (n < 0)
                throw new JeeflowException(
                    $"节点[{nodeName}]的 {PropAddSignMaxRounds} 配置非法: {n}（不得为负，0 表示不限）");
            return n;
        }
        var v = AsText(value);
        if (v.Length == 0) return 0;
        // InvariantCulture：区域设置不参与整数解析（本栈其它栈用 Integer.parseInt／int()，都不认千分位与
        // 本机小数点，这里若用当前区域就会出现"同一份定义别家报错、这家静默收下"的分叉）。
        if (!int.TryParse(v, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            throw new JeeflowException(
                $"节点[{nodeName}]的 {PropAddSignMaxRounds} 配置非法: {v}（必须是整数，0 表示不限）");
        if (parsed < 0)
            throw new JeeflowException(
                $"节点[{nodeName}]的 {PropAddSignMaxRounds} 配置非法: {parsed}（不得为负，0 表示不限）");
        return parsed;
    }

    private static string AsText(object value) =>
        value is IFormattable f
            ? f.ToString(null, System.Globalization.CultureInfo.InvariantCulture)?.Trim() ?? ""
            : value.ToString()?.Trim() ?? "";

    private static bool TryNumeric(object value, out double number)
    {
        switch (value)
        {
            case int i: number = i; return true;
            case long l: number = l; return true;
            case double d: number = d; return true;
            case float ff: number = ff; return true;
            case decimal m: number = (double)m; return true;
            case short sh: number = sh; return true;
            default: number = 0; return false;
        }
    }

    // ═══ 本次请求参数 ═══

    /// <summary>
    /// 本次提交指定的加签人（规范 06 §2.7「逗号串与数组两形同判据」）：直接复用归属值判据单点
    /// <see cref="PageQuery.NormalizeActors(object)"/>——逐元素 trim、空串/纯空白/null 丢弃、
    /// 同次调用折叠；<b>不另抄第二份判据</b>（§2.11 尾注）。没点人返回空表。
    /// </summary>
    public static List<string> Operators(FlowData? args) =>
        args == null
            ? new List<string>()
            : PageQuery.NormalizeActors(args.GetObj(FlowConst.AddSignOperators));

    // ═══ 写入口校验（四判据，一律排在任何状态变更之前）═══

    /// <summary>
    /// 写入口校验（互斥／开关未开／加给自己／超上限）。<b>必须在改动任何状态之前调用</b>。
    ///
    /// <para>互斥这条的理由不是语义矛盾（一个管本节点再来一轮、一个管流出后交给谁，逻辑可共存），
    /// 而是<b>参数生命周期</b>：<c>tf_</c> 键只在当次请求的执行参数与已完成任务行变量里可读，
    /// 加签使令牌停留本节点、流出发生在后续请求，届时先传的 <see cref="FlowConst.NextNodeOperator"/>
    /// 已不可读——同传时它是死参数。静默择一＝用户选的"下一节点处理人"无声消失，报错是把它变响话。</para>
    ///
    /// <para>「开关没开却传键」必须报错而非静默忽略：静默忽略＝客户排障地狱（规范 06 §2.7）。</para>
    /// </summary>
    public static void ValidateSubmission(ProcessModel? model, ProcessTask? task,
                                          FlowData? args, string? operatorId)
    {
        if (args == null || task == null) return;
        var ops = Operators(args);
        var nextOp = args.GetObj(FlowConst.NextNodeOperator);
        var nextPresent = nextOp != null && AsText(nextOp).Length > 0;
        if (ops.Count > 0 && nextPresent) throw new JeeflowException(MsgMutex);
        if (ops.Count == 0)
        {
            // 没点人＝正常流转档，其余判据不涉及（也不要求节点开开关）
            return;
        }
        if (model?.GetNode(task.TaskName) is not TaskModel node || !node.AllowAddSign)
            throw new JeeflowException(string.Format(MsgSwitchOff, task.TaskName));
        var me = PageQuery.NormalizeActorValue(operatorId);
        foreach (var actor in ops)
            if (me != null && me == actor) throw new JeeflowException(MsgSelf);
        var max = node.AddSignMaxRounds;
        if (max > 0 && RoundOf(task, node.Name) + 1 > max)
            throw new JeeflowException(string.Format(MsgMaxRounds, node.Name, max));
    }

    // ═══ 推进闸与废弃 ═══

    /// <summary>本节点是否还有未完成任务（刚办结那条此刻已是 FINISHED，不计入）。
    /// issues/253：协办行（task_type=1）不算「兄弟待办」——它不驱动流程、不是接力链的一环，
    /// 算进来会让 waitCompletion=false 的主办在加签/征询节点上被协办行挡住推进（规范 04
    /// 「缺省 false 主办可随时推进」）。协办行的等待由 <see cref="AidantSupport.GateWait"/> 单独裁决。</summary>
    public static bool HasDoingOnNode(ProcessInstance? instance, string? nodeName)
    {
        if (instance?.Tasks == null || nodeName == null) return false;
        foreach (var t in instance.Tasks)
            if (nodeName.Equals(t.TaskName, StringComparison.Ordinal) && t.IsDoing()
                && !AidantSupport.IsAidantRow(t)) return true;
        return false;
    }

    /// <summary>
    /// 废弃本节点未完成的加签任务（拒绝／退回发起人时调用，不留孤儿待办——规范 04 · 后加签「语义边界」）。
    /// 只对开了 <see cref="PropAllowAddSign"/> <b>或</b> <see cref="CollectSupport.PropDynamicCountersign"/>
    /// 的节点动手（issues/247：征询成员行同属本节点 DOING，实例撤回/退回路径一并清场）。
    /// 普通节点的兄弟任务仍由既有会签腿管（<see cref="CountersignHandler"/>），不在此扩面。
    /// </summary>
    /// <returns>实际废弃的行数</returns>
    public static int AbandonDoingOnNode(ProcessModel? model, ProcessInstance? instance,
                                         string? nodeName, string? operatorId, IClock? clock = null)
    {
        if (model == null || instance?.Tasks == null || nodeName == null) return 0;
        if (model.GetNode(nodeName) is not TaskModel node
            || (!node.AllowAddSign && !node.DynamicCountersign)) return 0;
        var abandoned = 0;
        foreach (var t in instance.Tasks)
        {
            if (nodeName.Equals(t.TaskName, StringComparison.Ordinal) && t.IsDoing())
            {
                t.Abandon(operatorId, clock);
                abandoned++;
            }
        }
        return abandoned;
    }

    // ═══ 轮次与账本 ═══

    /// <summary>读轮次号：首圈（普通建单产生的那条）没有该键 ⇒ 0。</summary>
    public static int RoundOf(ProcessTask? task, string? nodeName)
    {
        if (task?.Variables == null || nodeName == null) return 0;
        return task.Variables.GetInt($"{FlowConst.AddSignRound}_{nodeName}", 0);
    }

    /// <summary>把轮次号写进<b>新任务</b>的行变量（键形 <c>addSignRound_{节点名}</c>）。</summary>
    public static void PutRound(ProcessTask? task, string? nodeName, int round)
    {
        if (task?.Variables == null || nodeName == null) return;
        task.Variables[$"{FlowConst.AddSignRound}_{nodeName}"] = round;
    }

    /// <summary>
    /// 账本追加（<b>只追加不覆盖</b>），落在<b>刚办结那条</b>的行变量上。
    /// 形状照本栈 <c>tf_transferHistory</c> 那族（<c>ActionsMain.AppendTransferRecord</c> 同尺）：
    /// 五键固定 camelCase 与固定键序，<c>time</c> 一律 <c>yyyy-MM-dd HH:mm:ss</c>
    /// （规范 06 §2.4 全生态时间格式，账本跨栈同形）。
    ///
    /// <para><b>为什么是"新建容器"而不是原地 <c>Add</c></b>：本栈内存仓储浅拷贝语义下
    /// <c>Variables</c> 的值在"聚合副本"与"存储行"之间<b>共享引用</b>，原地改会让同一份列表在
    /// 两个持有者之间串台——<see cref="ProcessTask.Clone"/> 那句注释与转办账本那条腿都为此立过规矩。
    /// java 侧是 <c>List</c> 原地 append（它的仓储不做这种共享引用拷贝），本栈照抄反而错，
    /// 落到的<b>可观察形状与 java 逐字一致</b>。</para>
    ///
    /// <para><c>tf_</c> 前缀使「退回上一步」的血缘复活腿天然剔掉它（<see cref="ProcessInstance.RejectTask"/>
    /// 的变量过滤），账本只留在已完成的历史行上、不污染新待办——正是审计想要的形状。</para>
    /// </summary>
    public static void AppendHistory(ProcessTask? completed, string? operatorId,
                                     List<string> toActors, int round, IClock? clock = null)
    {
        if (completed?.Variables == null) return;
        var vars = completed.Variables;
        var history = new List<object?>();
        if (vars.GetObj(FlowConst.AddSignHistory) is System.Collections.IEnumerable en and not string)
        {
            foreach (var item in en) history.Add(item);
        }
        var now = (clock ?? SystemClock.Instance).Now;
        history.Add(new Dictionary<string, object?>
        {
            // 键序与 java 的 LinkedHashMap 逐字同形：round / fromActor / toActors / time / operator
            ["round"] = round,
            ["fromActor"] = operatorId,
            ["toActors"] = new List<string>(toActors),
            ["time"] = now.ToString(FlowUtil.TimeFormat,
                System.Globalization.CultureInfo.InvariantCulture),
            ["operator"] = operatorId,
        });
        vars[FlowConst.AddSignHistory] = history;
    }

    /// <summary>读账本（测试与投影用）；缺省/形状不符 ⇒ 空表，不抛错。</summary>
    public static List<object?> HistoryOf(ProcessTask? task)
    {
        var result = new List<object?>();
        if (task?.Variables?.GetObj(FlowConst.AddSignHistory)
            is System.Collections.IEnumerable en and not string)
        {
            foreach (var item in en) result.Add(item);
        }
        return result;
    }
}

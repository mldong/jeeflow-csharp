namespace Mldong.Jeeflow.Core;

/// <summary>
/// 已办撤回（<c>allowRecall</c>，issues/246）的判据与账本：全部只读／纯函数，
/// 由引擎在聚合变更之前调用（对齐 Java <c>core/RecallSupport</c>，形状基准＝本栈
/// <see cref="AddSignSupport"/>）。
///
/// <para>契约正文：规范 02 §4.4（开关＋两条解析期硬校验）／规范 04「已办撤回」整节
/// （前置与窗口判据、动作三件、承诺边界）／规范 06 §2.7＋§<c>processTask/recall</c>
/// （五条逐字失败文案）／规范 11 §11.3 码 10 <c>TASK_RECALL</c>。</para>
///
/// <para><b>为什么判据一律排在任何状态变更之前</b>：内存仓储没有事务回滚，一旦先废弃下游行再判错，
/// 那些行就停在 99 而无人能改回来——「报错即零写入」与后加签写入口／退回上一步守卫同一规矩。</para>
///
/// <para><b>窗口取宽口径</b>（owner 2026-11-03 拍）：从目标行沿 <c>ParentTaskId</c> 血缘产出的
/// 全部下游行中<b>存在任何 FINISHED</b> ⇒ 报错；否则返回其中仍 DOING 的行供废弃。后加签行、
/// 动态会签成员行、并行分支多支都天然落在这条血缘闭包里，不必为它们各开一档判据；
/// 已废弃(99)／已撤回(30) 行既不拦也不重复废弃。</para>
///
/// <para>逐字文案与规范 06 §<c>processTask/recall</c> 失败文案表一致（八栈同词，一个字不许改）。</para>
/// </summary>
public static class RecallSupport
{
    /// <summary>节点属性键：已办撤回开关（<c>properties</c> 平铺层，缺省 false）。</summary>
    public const string PropAllowRecall = "allowRecall";

    /// <summary>下游存在已办结行（规范 06 §processTask/recall，逐字；<c>{0}</c>＝节点名）。</summary>
    public const string MsgWindow = "任务[{0}]的后续节点已办理，不能撤回";

    /// <summary>节点未开开关（规范 06 §processTask/recall，逐字；<c>{0}</c>＝节点名）。</summary>
    public const string MsgSwitchOff = "节点[{0}]未开启 allowRecall，不能撤回";

    /// <summary>目标行非 FINISHED（规范 06 §processTask/recall，逐字）。</summary>
    public const string MsgNotFinished = "任务非已完成状态，不可撤回";

    /// <summary>操作人非目标行参与者（规范 06 §processTask/recall，逐字）。</summary>
    public const string MsgNoPermission = "无权限撤回该任务";

    /// <summary>实例非 DOING(10)（规范 06 §processTask/recall，逐字）。</summary>
    public const string MsgInstanceNotDoing = "流程实例非进行中状态，不可撤回";

    /// <summary>会签节点配开关（规范 02 §4.4，逐字）——撤"谁的一票"语义不清。</summary>
    public const string MsgCountersignNode =
        "会签节点[{0}]不允许配置 allowRecall：撤\"谁的一票\"语义不清";

    /// <summary>首任务节点配开关（规范 02 §4.4，逐字）——申请节点走实例撤回＋重新发起。</summary>
    public const string MsgFirstTaskNode =
        "首任务节点[{0}]不允许配置 allowRecall：申请节点走实例撤回＋重新发起，不需要同实例复活";

    // ═══ 节点开关 ═══

    /// <summary>节点是<b>任务节点</b>且开了 <see cref="PropAllowRecall"/>（属性缺失／非任务节点 ⇒ false）。</summary>
    public static bool NodeEnabled(NodeModel? node) =>
        node is TaskModel task && task.AllowRecall;

    // ═══ 窗口判据（必须排在任何状态变更之前）═══

    /// <summary>
    /// 窗口判据（宽口径）：从 <paramref name="target"/> 沿 <c>ParentTaskId</c> 链产出的全部下游行中
    /// <b>存在任何 FINISHED</b> ⇒ 逐字报错；否则返回其中仍 DOING 的行（供废弃）。
    /// <para>纯函数：不写任何行、不碰实例状态，报错即调用方尚未做过任何变更。</para>
    /// </summary>
    public static List<ProcessTask> ValidateWindow(IEnumerable<ProcessTask>? tasks, ProcessTask? target)
    {
        var doing = new List<ProcessTask>();
        if (target == null) return doing;
        foreach (var t in CollectDescendants(tasks, target.TaskId))
        {
            if (t.IsFinished())
                throw new JeeflowException(string.Format(MsgWindow, target.TaskName));
            if (t.IsDoing()) doing.Add(t);
        }
        return doing;
    }

    /// <summary>
    /// 按 <c>ParentTaskId</c> 血缘收集 <paramref name="ancestorId"/> 的全部后代行（对齐 java
    /// <c>collectDescendants</c>：frontier 后进先出，收集的是<b>整棵子树</b>而非只下一层）。
    /// 血缘在建单不变量里必写（0＝无父，见 <see cref="ProcessTask.Create"/>），所以只沿 parent 走
    /// 就覆盖到"该行办结产出的一切"。
    /// </summary>
    private static List<ProcessTask> CollectDescendants(IEnumerable<ProcessTask>? tasks, long? ancestorId)
    {
        var result = new List<ProcessTask>();
        if (ancestorId == null || tasks == null) return result;
        var all = tasks.ToList();
        var frontier = new List<long> { ancestorId.Value };
        while (frontier.Count > 0)
        {
            var parent = frontier[frontier.Count - 1];
            frontier.RemoveAt(frontier.Count - 1);
            foreach (var t in all)
            {
                if (t.TaskId == null) continue;
                if (parent == t.ParentTaskId)
                {
                    result.Add(t);
                    frontier.Add(t.TaskId.Value);
                }
            }
        }
        return result;
    }

    // ═══ 账本 ═══

    /// <summary>
    /// 账本追加（<b>只追加不覆盖</b>），落在<b>被撤回的旧行</b>变量上；<c>recalledTaskId</c>
    /// 指向复活的新行（从历史记录可一步跳到唯一活待办）。三键固定 camelCase
    /// <c>{operator, time, recalledTaskId}</c>，<c>time</c> 一律 <c>yyyy-MM-dd HH:mm:ss</c>
    /// （规范 06 §2.4 全生态时间格式，账本跨栈同形，形状照 <see cref="AddSignSupport"/> 那族）。
    ///
    /// <para><b>原 FINISHED 行一律不改写</b>：jeeflow 无独立历史表，任务行即审批历史，
    /// 翻回 DOING＝改写已出证的记录；账本就是那一行上唯一的追加痕迹。</para>
    ///
    /// <para><b>为什么是"新建容器"而不是原地 <c>Add</c></b>：本栈内存仓储浅拷贝语义下
    /// <c>Variables</c> 的值在"聚合副本"与"存储行"之间共享引用，原地改会让同一份列表在两个持有者
    /// 之间串台——与 <see cref="AddSignSupport.AppendHistory"/> 同规矩，落到的可观察形状与 java 一致。</para>
    ///
    /// <para><c>tf_</c> 前缀使「退回上一步」的血缘复活腿天然剔掉它（<see cref="ProcessInstance.RejectTask"/>
    /// 的变量过滤），账本只留在已完成的历史行上、不污染新待办。</para>
    /// </summary>
    public static void AppendRecallHistory(ProcessTask? target, string? operatorId,
                                           long? resurrectedTaskId, IClock? clock = null)
    {
        if (target?.Variables == null) return;
        var vars = target.Variables;
        var history = new List<object?>();
        if (vars.GetObj(FlowConst.RecallHistory) is System.Collections.IEnumerable en and not string)
        {
            foreach (var item in en) history.Add(item);
        }
        var now = (clock ?? SystemClock.Instance).Now;
        history.Add(new Dictionary<string, object?>
        {
            // 键序与 java 的 LinkedHashMap 逐字同形：operator / time / recalledTaskId
            ["operator"] = operatorId,
            ["time"] = now.ToString(FlowUtil.TimeFormat,
                System.Globalization.CultureInfo.InvariantCulture),
            ["recalledTaskId"] = resurrectedTaskId,
        });
        vars[FlowConst.RecallHistory] = history;
    }

    /// <summary>读撤回账本（测试与出证用）；缺省/形状不符 ⇒ 空表，不抛错。</summary>
    public static List<object?> HistoryOf(ProcessTask? task)
    {
        var result = new List<object?>();
        if (task?.Variables?.GetObj(FlowConst.RecallHistory)
            is System.Collections.IEnumerable en and not string)
        {
            foreach (var item in en) result.Add(item);
        }
        return result;
    }
}

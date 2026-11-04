namespace Mldong.Jeeflow.Core;

/// <summary>
/// 动态会签（征询模式 <c>dynamicCountersign</c>，issues/247）的判据、账本与投影：
/// 全部只读／纯函数，由门面在聚合变更之前调用（对齐 Java <c>core/CollectSupport</c>，
/// 形状基准＝本栈 <see cref="AddSignSupport"/>／<see cref="RecallSupport"/>）。
///
/// <para>契约正文：规范 02 §4.4（开关＋三条解析期硬校验）／规范 04「动态会签（征询模式）」整节
/// （提交分流、成员行建单规则、撤回、语义边界）／规范 06 §2.7＋§<c>processTask/revokeDynamicCountersign</c>
/// （逐字失败文案表）。<b>不新增节点类型、不新增 submitType 档、无 DDL、不新增事件码</b>：
/// 成员行即普通任务行，意见物理隔离由「一行任务一条意见＋实例剥 <c>tf_*</c>」的既有不变量天然保证。</para>
///
/// <para>三条承重判据（与其余五腿同形，逐字照 java）：</para>
/// <list type="number">
/// <item><b>成员行识别＝parent 且同节点同名</b>。同名过滤不可省：主办办结推进后，下一节点行的
/// <c>ParentTaskId</c> 同样是主办行 id（建单不变量），只认 parent 会把下游行误当成员——
/// 撤回腿会误废弃它们。</item>
/// <item><b>闸门收齐＝无 DOING 成员行</b>；计数与投影一律跳过 99（ABANDON，撤回腿清场的那几行）。</item>
/// <item><b>撤回的参与人判断按名册</b>（<see cref="ProcessTask.ActorIds"/>），不能用只认 DOING 行的
/// <see cref="ProcessTask.IsAllowed"/>——主办已办结时那一判会假拒。</item>
/// </list>
/// </summary>
public static class CollectSupport
{
    /// <summary>节点属性键：动态会签（征询模式）开关（<c>properties</c> 平铺层，缺省 false）。</summary>
    public const string PropDynamicCountersign = "dynamicCountersign";

    /// <summary>未收齐时主办按任意 submitType 办结（规范 04／06，逐字；<c>{0}</c>＝节点名，
    /// <c>{1}</c>＝已提交数，<c>{2}</c>＝在册总数）。</summary>
    public const string MsgPending = "节点[{0}]动态会签意见未收齐（已提交 {1}/{2}），不能办理";

    /// <summary>重复拉同一未答的人（规范 04，逐字；<c>{0}</c>＝被拉的人，<c>{1}</c>＝节点名）。</summary>
    public const string MsgDuplicate = "操作人[{0}]已在节点[{1}]的动态会签中";

    /// <summary>成员行携带拉人参数（规范 04，逐字）——拉人只限主办行。</summary>
    public const string MsgMemberPull = "动态会签成员任务不能发起加签";

    /// <summary>无待答成员行却要撤回（规范 06 §revokeDynamicCountersign，逐字；<c>{0}</c>＝节点名）。</summary>
    public const string MsgNoPending = "节点[{0}]没有进行中的动态会签";

    /// <summary>撤回时操作人非主办行参与者（规范 06 §revokeDynamicCountersign，逐字）。</summary>
    public const string MsgNoPermissionRevoke = "无权限撤回该动态会签";

    /// <summary>与 allowAddSign 双开（规范 02 §4.4，逐字）——两档共用一个协议键＝行为歧义。</summary>
    public const string MsgMutexAddSign =
        "节点[{0}]不允许同时配置 allowAddSign 与 dynamicCountersign：两档共用 tf_addSignOperators，"
        + "行为靠节点属性分流，双开＝行为歧义";

    /// <summary>会签节点配开关（规范 02 §4.4，逐字）——池推进语义与征询挂起语义打架。</summary>
    public const string MsgCountersignNode =
        "会签节点[{0}]不允许配置 dynamicCountersign：会签池推进语义与征询挂起语义打架";

    /// <summary>申请节点配开关（规范 02 §4.4，逐字）——那手走实例撤回＋重新发起。</summary>
    public const string MsgApplyNode =
        "申请节点[{0}]不允许配置 dynamicCountersign：申请节点那手走实例撤回＋重新发起，不需要征询挂起";

    // ═══ 成员行判据（承重判据 1）═══

    /// <summary>
    /// 成员行＝<c>ParentTaskId</c> 指向主办行<b>且同节点同名</b>的行（成员不能再拉人 ⇒ 无更深层级）。
    /// 同名过滤不可省，见类头判据 1。
    /// </summary>
    public static List<ProcessTask> MemberRows(IEnumerable<ProcessTask>? tasks, ProcessTask? host)
    {
        var outList = new List<ProcessTask>();
        if (tasks == null || host?.TaskId == null || host.TaskName == null) return outList;
        foreach (var t in tasks)
        {
            if (host.TaskId.Equals(t.ParentTaskId) && host.TaskName.Equals(t.TaskName, StringComparison.Ordinal))
                outList.Add(t);
        }
        return outList;
    }

    /// <summary>
    /// 成员行判定：本行的 parent 指向<b>同节点同名</b>的另一行（issues/247 · 成员行禁带拉人参数的判据，
    /// 也是「成员行不得冒充主办发起拉人」的判据）。parent 空或 0 ⇒ 一定不是成员行。
    /// </summary>
    public static bool IsMemberRow(IEnumerable<ProcessTask>? tasks, ProcessTask? row)
    {
        if (tasks == null || row?.ParentTaskId == null || row.ParentTaskId == 0L) return false;
        foreach (var t in tasks)
        {
            if (row.ParentTaskId.Equals(t.TaskId)
                && row.TaskName != null && row.TaskName.Equals(t.TaskName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 闸门原料（承重判据 2）：<c>[0]</c>＝在册成员行总数、<c>[1]</c>＝已提交（FINISHED）数——
    /// 已废弃(99) 行（撤回腿清场的）<b>不算在册</b>；空集＝本行不是征询主办行。
    /// </summary>
    public static int[] CountMembers(IEnumerable<ProcessTask>? tasks, ProcessTask? host)
    {
        var total = 0;
        var submitted = 0;
        foreach (var t in MemberRows(tasks, host))
        {
            if (t.TaskState == (int)WfTaskState.Abandon) continue;
            total++;
            if (t.IsFinished()) submitted++;
        }
        return new[] { total, submitted };
    }

    /// <summary>未答成员行（DOING），即撤回接口（<c>revokeDynamicCountersign</c>）的废弃集合。</summary>
    public static List<ProcessTask> PendingMembers(IEnumerable<ProcessTask>? tasks, ProcessTask? host)
    {
        var outList = new List<ProcessTask>();
        foreach (var t in MemberRows(tasks, host))
            if (t.IsDoing()) outList.Add(t);
        return outList;
    }

    // ═══ 投影（规范 06 §2.7）═══

    /// <summary>
    /// 征询主办行的 <c>ext.dynamicCountersign</c> 投影＝
    /// <c>{active, round, total, submitted, actors:[{actor, taskId, state}]}</c>——
    /// 从 <c>ParentTaskId</c> <b>现算</b>、不落新存储；前端据此把执行按钮全部置灰。
    /// 无成员行 ⇒ null（＝本行不是征询主办行，出口不挂这个键）。
    /// </summary>
    public static Dictionary<string, object?>? BuildProjection(IEnumerable<ProcessTask>? tasks, ProcessTask? host)
    {
        var members = MemberRows(tasks, host);
        if (members.Count == 0) return null;
        var total = 0;
        var submitted = 0;
        var actors = new List<object?>();
        foreach (var m in members)
        {
            if (m.TaskState == (int)WfTaskState.Abandon) continue;   // 撤回清场的行不进投影
            total++;
            if (m.IsFinished()) submitted++;
            actors.Add(new Dictionary<string, object?>
            {
                // 键序与 java 的 LinkedHashMap 逐字同形：actor / taskId / state
                ["actor"] = m.ActorIds.Count == 0 ? null : m.ActorIds[0],
                ["taskId"] = m.TaskId,
                ["state"] = m.TaskState,
            });
        }
        return new Dictionary<string, object?>
        {
            // 键序与 java 逐字同形：active / round / total / submitted / actors
            ["active"] = submitted < total,
            ["round"] = LastRound(host),
            ["total"] = total,
            ["submitted"] = submitted,
            ["actors"] = actors,
        };
    }

    /// <summary>主办行当前轮次＝账本条数里最后一条的 <c>round</c>（每次拉人追加一条＝轮次推进；撤回不消耗）。</summary>
    public static int LastRound(ProcessTask? host)
    {
        var history = AddSignSupport.HistoryOf(host);
        if (history.Count == 0) return 0;
        if (history[history.Count - 1] is IDictionary<string, object?> last
            && last.TryGetValue("round", out var r))
        {
            return r switch
            {
                int i => i,
                long l => (int)l,
                _ => System.Convert.ToInt32(r,
                    System.Globalization.CultureInfo.InvariantCulture),
            };
        }
        return 0;
    }

    // ═══ 账本（落主办行）═══

    /// <summary>
    /// 发起会签账本追加（只追加不覆盖），落<b>主办行</b>——征询模式下主办行未办结，
    /// 下一轮读它时才读得到整条链；每条含 <c>reason</c>（选填，空则不落该键）。
    /// </summary>
    public static void AppendCollectHistory(ProcessTask? host, string? operatorId,
                                            List<string> toActors, int round, string? reason,
                                            IClock? clock = null)
    {
        var entry = BaseEntry(operatorId, toActors, round, clock);
        if (!string.IsNullOrEmpty(reason)) entry["reason"] = reason;
        Append(host, entry);
    }

    /// <summary>
    /// 撤回会签账本追加：<c>revoked＝true</c> 标记本条是撤回（未答废弃），<c>toActors</c>＝被撤的人。
    /// 轮次不消耗——本条沿用当前轮次号（java 同尺：传 <c>lastRound(host)</c>）。
    /// </summary>
    public static void AppendRevokeHistory(ProcessTask? host, string? operatorId,
                                            List<string?> revokedActors, int round, string? reason,
                                            IClock? clock = null)
    {
        var entry = BaseEntry(operatorId, revokedActors, round, clock);
        entry["revoked"] = true;
        if (!string.IsNullOrEmpty(reason)) entry["reason"] = reason;
        Append(host, entry);
    }

    private static Dictionary<string, object?> BaseEntry(string? operatorId,
        IEnumerable<string?> toActors, int round, IClock? clock)
    {
        var now = (clock ?? SystemClock.Instance).Now;
        var list = new List<string?>();
        foreach (var a in toActors) list.Add(a);
        return new Dictionary<string, object?>
        {
            // 键序与 java 的 LinkedHashMap 逐字同形：round / fromActor / toActors / time / operator
            // （revoked/reason 两键由调用侧在其后追加，同 java baseEntry + entry.put 的序）
            ["round"] = round,
            ["fromActor"] = operatorId,
            ["toActors"] = list,
            ["time"] = now.ToString(FlowUtil.TimeFormat,
                System.Globalization.CultureInfo.InvariantCulture),
            ["operator"] = operatorId,
        };
    }

    /// <summary>
    /// 追加到主办行的 <c>tf_addSignHistory</c>。与 <see cref="AddSignSupport.AppendHistory"/>
    /// 同一个「新建容器再覆写」的姿势（本栈内存仓储共享引用的串台问题同根）。
    /// </summary>
    private static void Append(ProcessTask? host, Dictionary<string, object?> entry)
    {
        if (host?.Variables == null) return;
        var vars = host.Variables;
        var history = new List<object?>();
        if (vars.GetObj(FlowConst.AddSignHistory) is System.Collections.IEnumerable en and not string)
        {
            foreach (var item in en) history.Add(item);
        }
        history.Add(entry);
        vars[FlowConst.AddSignHistory] = history;
    }
}

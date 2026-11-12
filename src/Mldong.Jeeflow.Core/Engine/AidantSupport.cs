namespace Mldong.Jeeflow.Core;

/// <summary>
/// 动态协办（aidant，issues/253）的判据、账本与投影：全部只读／纯函数（清场除外，它改聚合行），
/// 由引擎在聚合变更之前调用——「判据前置、报错即零写入」与后加签/征询/撤回写入口同一规矩
/// （对齐 Java <c>core/AidantSupport</c>，形状基准＝本栈 <see cref="CollectSupport"/>／<see cref="RecallSupport"/>）。
///
/// <para><b>与征询（<see cref="CollectSupport"/>)的分辨判据＝task_type</b>：两族子行都是 parent 指向
/// 主办行＋同节点同名，协办行 <c>task_type=1</c>（<see cref="WfTaskType.Secondary"/>）、征询成员行
/// <c>task_type=0</c>。等待闸只数协办行、征询收齐闸只数成员行，各数各的（规范 04「与征询允许同节点并存」）。</para>
///
/// <para><b>无节点开关</b>：协办档只由运行期 <c>processTask/aidant</c> 派生，节点级 <c>taskType=1</c>
/// 解析期禁配（issues/252，见 <c>ModelParser</c>）。契约正文：规范 04「动态协办（aidant）」／
/// 规范 06 §2.7（协议键＋投影）＋§<c>processTask/aidant</c>（协办六行逐字失败文案）。
/// <b>不新增 submitType 档、不新增事件码、无 DDL</b>。</para>
/// </summary>
public static class AidantSupport
{
    /// <summary>缺 actorIds 或归一后为空（规范 06 协办六行，逐字）。</summary>
    public const string MsgNoActors = "actorIds 必填";

    /// <summary>操作人非主办行名册且非 auto/admin（规范 06，逐字）。</summary>
    public const string MsgNoPermission = "无权限派生协办任务";

    /// <summary>主办行非进行中（规范 06，逐字）。</summary>
    public const string MsgNotDoing = "任务非进行中，不可派生协办";

    /// <summary>该人已有未办结协办行（规范 06，逐字）。</summary>
    public const string MsgDuplicate = "该人已有未办结的协办任务";

    /// <summary>协办行再派生协办（规范 06，逐字）。</summary>
    public const string MsgNoRecursion = "协办任务不能再派生协办";

    /// <summary>主办行读不到（规范 06，逐字；不借带 id 尾巴的内部码文案）。</summary>
    public const string MsgTaskNotFound = "任务不存在";

    /// <summary>等待闸未齐（规范 04／06，逐字；<c>{0}</c>＝节点名，<c>{1}</c>＝已办结数，<c>{2}</c>＝在册总数）。</summary>
    public const string MsgWaitPending = "节点[{0}]协办未办结（已办结 {1}/{2}），不能办理";

    // ═══ 协办行判据 ═══

    /// <summary>协办行判定＝<c>task_type=1</c>（Secondary）。这是两族子行唯一的分辨判据。</summary>
    public static bool IsAidantRow(ProcessTask? row) => row?.TaskType == WfTaskType.Secondary;

    /// <summary>
    /// 协办行＝parent 指向主办行<b>且同节点同名且 task_type=1</b> 的行。
    /// 同名过滤不可省（照 <see cref="CollectSupport.MemberRows"/> 的理由）：主办办结推进后，下一节点行的
    /// parent 同样是主办行 id（建单不变量），只认 parent 会把下游行误当协办行。task_type 过滤同样不可省：
    /// 征询成员行也满足 parent＋同名，混进来会让等待闸把「意见未收齐」误报成「协办未办结」。
    /// </summary>
    public static List<ProcessTask> AidantRows(IEnumerable<ProcessTask>? tasks, ProcessTask? host)
    {
        var outList = new List<ProcessTask>();
        if (tasks == null || host?.TaskId == null || host.TaskName == null) return outList;
        foreach (var t in tasks)
        {
            if (IsAidantRow(t)
                && host.TaskId.Equals(t.ParentTaskId)
                && host.TaskName.Equals(t.TaskName, StringComparison.Ordinal))
                outList.Add(t);
        }
        return outList;
    }

    /// <summary>
    /// 等待闸原料：<c>[0]</c>＝在册协办行总数、<c>[1]</c>＝已办结（FINISHED）数——
    /// 已废弃(99) 行（离开节点清场的）<b>不算在册</b>（规范 04「计数跳过已废弃(99) 行」）。
    /// </summary>
    public static int[] CountAidant(IEnumerable<ProcessTask>? tasks, ProcessTask? host)
    {
        var total = 0;
        var submitted = 0;
        foreach (var t in AidantRows(tasks, host))
        {
            if (t.TaskState == (int)WfTaskState.Abandon) continue;
            total++;
            if (t.IsFinished()) submitted++;
        }
        return new[] { total, submitted };
    }

    /// <summary>未办结协办行（DOING）——等待闸的判据集合，也是离开节点时的清场集合。</summary>
    public static List<ProcessTask> PendingAidant(IEnumerable<ProcessTask>? tasks, ProcessTask? host)
    {
        var outList = new List<ProcessTask>();
        foreach (var t in AidantRows(tasks, host))
            if (t.IsDoing()) outList.Add(t);
        return outList;
    }

    /// <summary>
    /// 等待闸开关（<c>waitCompletion</c>）：派生时盖在<b>主办行</b>变量上，缺省 false＝不等。
    /// 多轮派生时后一次的值生效（投影 <c>ext.aidant.wait</c> 同源）。非法值按缺省 false
    /// （＝java <c>Boolean.parseBoolean(String.valueOf(v))</c>：bool 与 "true" 字符串两形都认，其余 false）。
    /// </summary>
    public static bool WaitOf(ProcessTask? host)
    {
        var v = host?.Variables?.GetObj(FlowConst.AidantWait);
        return v switch
        {
            bool b => b,
            string s => bool.TryParse(s.Trim(), out var r) && r,
            _ => false,
        };
    }

    /// <summary>
    /// 等待闸（规范 04「推进闸与等待闸」）：<c>waitCompletion=true</c> 时主办行办结前判「不存在 DOING 协办行」，
    /// 未齐 ⇒ 逐字文案报错、零写入。协办行自身不受本闸约束（它办结本来就不驱动流程）；
    /// <c>waitCompletion=false</c>（缺省）原样放行。<b>必须由引擎排在 CompleteTask 之前</b>——
    /// 内存仓储无回滚，判据后置时「报错」那一次调用结束后主办待办已经消失（与后加签/退回守卫同一规矩）。
    /// </summary>
    public static void GateWait(IEnumerable<ProcessTask>? tasks, ProcessTask? host)
    {
        if (host == null || IsAidantRow(host) || !WaitOf(host)) return;
        if (PendingAidant(tasks, host).Count == 0) return;
        var count = CountAidant(tasks, host);
        throw new JeeflowException(string.Format(MsgWaitPending, host.TaskName, count[1], count[0]));
    }

    /// <summary>
    /// 离开节点时清场未办结协办行（规范 04：<c>waitCompletion=false</c> 时主办可随时推进，未办结协办行
    /// 随废弃腿清场、账本记废弃，不留孤儿待办）。<b>只对协办行（task_type=1）动手，且不要求节点开任何开关</b>
    /// ——协办无节点开关，普通节点上的协办行同样要清。按 parent 分组（首见序＝java LinkedHashMap 的行序），
    /// 废弃事实追加到各自主办行的账本上。幂等：已废弃(99)/已办结(20) 的行不再命中，重复调用不重复记账。
    /// 改的是聚合内的行，落库由调用方的 UpdateInstance 级联／逐条 UpdateTask 承担（与征询撤回腿同形）。
    /// </summary>
    /// <returns>实际废弃的行数</returns>
    public static int AbandonPendingOnNode(ProcessInstance? instance, string? nodeName,
                                           string? operatorId, IClock? clock = null)
    {
        if (instance?.Tasks == null || nodeName == null) return 0;
        // parent → 该主办行下待清场的协办行（首见序锁定记账顺序＝行序）
        var order = new List<long?>();
        var byHost = new Dictionary<long?, List<ProcessTask>>();
        foreach (var t in instance.Tasks)
        {
            if (nodeName.Equals(t.TaskName, StringComparison.Ordinal) && t.IsDoing() && IsAidantRow(t))
            {
                if (!byHost.TryGetValue(t.ParentTaskId, out var bucket))
                {
                    bucket = new List<ProcessTask>();
                    byHost[t.ParentTaskId] = bucket;
                    order.Add(t.ParentTaskId);
                }
                bucket.Add(t);
            }
        }
        var abandoned = 0;
        foreach (var parent in order)
        {
            var actors = new List<string?>();
            foreach (var t in byHost[parent])
            {
                t.Abandon(operatorId, clock);
                actors.Add(t.ActorIds.Count == 0 ? null : t.ActorIds[0]);
                abandoned++;
            }
            var host = instance.Tasks.FirstOrDefault(t => t.TaskId == parent);
            if (host != null)
                AppendAbandonHistory(host, operatorId, actors, LastRound(host), clock);
        }
        return abandoned;
    }

    // ═══ 投影（规范 06 §2.7）═══

    /// <summary>
    /// 主办行 <c>ext.aidant</c>＝<c>{active, round, total, submitted, actors:[{actor, taskId, state}], wait}</c>；
    /// 无协办行 ⇒ null。从 <c>task_parent_id</c>＋<c>task_type</c> 现算、不落新存储。
    /// 99（清场）行不进在册计数、也不进 actors 投影（与征询投影同口径）。
    /// </summary>
    public static Dictionary<string, object?>? BuildProjection(IEnumerable<ProcessTask>? tasks, ProcessTask? host)
    {
        var rows = AidantRows(tasks, host);
        if (rows.Count == 0) return null;
        var total = 0;
        var submitted = 0;
        var actors = new List<object?>();
        foreach (var t in rows)
        {
            if (t.TaskState == (int)WfTaskState.Abandon) continue;   // 清场的行不进投影
            total++;
            if (t.IsFinished()) submitted++;
            actors.Add(new Dictionary<string, object?>
            {
                // 键序与 java 的 LinkedHashMap 逐字同形：actor / taskId / state
                ["actor"] = t.ActorIds.Count == 0 ? null : t.ActorIds[0],
                ["taskId"] = t.TaskId,
                ["state"] = t.TaskState,
            });
        }
        return new Dictionary<string, object?>
        {
            // 键序与 java 逐字同形：active / round / total / submitted / actors / wait
            ["active"] = submitted < total,
            ["round"] = LastRound(host),
            ["total"] = total,
            ["submitted"] = submitted,
            ["actors"] = actors,
            ["wait"] = WaitOf(host),
        };
    }

    /// <summary>主办行当前轮次＝账本最后一条的 <c>round</c>（每次派生追加一条＝轮次推进；清场记账不消耗轮次）。</summary>
    public static int LastRound(ProcessTask? host)
    {
        var history = HistoryOf(host);
        if (history.Count == 0) return 0;
        if (history[history.Count - 1] is IDictionary<string, object?> last
            && last.TryGetValue("round", out var r))
        {
            return r switch
            {
                int i => i,
                long l => (int)l,
                _ => System.Convert.ToInt32(r, System.Globalization.CultureInfo.InvariantCulture),
            };
        }
        return 0;
    }

    // ═══ 账本（落主办行）═══

    /// <summary>
    /// 派生账本追加（只追加不覆盖），落<b>主办行</b>变量；键序锁定
    /// <c>{round, fromActor, toActors, reason, time, operator}</c>（规范 06 §2.7），
    /// <c>reason</c> 空串兜底、<c>time</c> 一律 §2.4 字符串。主办行在派生时不办结，
    /// CompleteTask 的合并序保证账本活到主办办结之后（与转办/征询账本同一条存活理由）。
    /// </summary>
    public static void AppendAidantHistory(ProcessTask? host, string? operatorId,
                                           List<string> toActors, int round, string? reason,
                                           IClock? clock = null)
    {
        Append(host, BaseEntry(operatorId, toActors, round, reason ?? "", clock));
    }

    /// <summary>
    /// 清场账本追加：<c>abandoned=true</c> 标记本条是「离开节点废弃」（形状照征询撤回条目的
    /// <c>revoked=true</c>），六键原序不动，<c>toActors</c>＝被废弃的人。
    /// </summary>
    public static void AppendAbandonHistory(ProcessTask? host, string? operatorId,
                                            List<string?> abandonedActors, int round,
                                            IClock? clock = null)
    {
        var entry = BaseEntry(operatorId, abandonedActors, round, "", clock);
        entry["abandoned"] = true;
        Append(host, entry);
    }

    /// <summary>读账本（测试与投影用）；缺省/形状不符 ⇒ 空表，不抛错。</summary>
    public static List<object?> HistoryOf(ProcessTask? task)
    {
        var result = new List<object?>();
        if (task?.Variables?.GetObj(FlowConst.AidantHistory)
            is System.Collections.IEnumerable en and not string)
        {
            foreach (var item in en) result.Add(item);
        }
        return result;
    }

    private static Dictionary<string, object?> BaseEntry(string? operatorId,
        IEnumerable<string?> toActors, int round, string reason, IClock? clock)
    {
        var now = (clock ?? SystemClock.Instance).Now;
        var list = new List<string?>();
        foreach (var a in toActors) list.Add(a);
        return new Dictionary<string, object?>
        {
            // 键序与 java 的 LinkedHashMap 逐字同形：round / fromActor / toActors / reason / time / operator
            // （abandoned 键由清场调用侧在其后追加，同 java baseEntry + entry.put 的序）
            ["round"] = round,
            ["fromActor"] = operatorId,
            ["toActors"] = list,
            ["reason"] = reason,
            ["time"] = now.ToString(FlowUtil.TimeFormat, System.Globalization.CultureInfo.InvariantCulture),
            ["operator"] = operatorId,
        };
    }

    /// <summary>
    /// 追加到主办行的 <c>tf_aidantHistory</c>。与 <see cref="CollectSupport"/> 同一个「新建容器再覆写」的姿势
    /// （本栈内存仓储共享引用的串台问题同根：原地改会让同一份列表在聚合副本与存储行之间串台）。
    /// </summary>
    private static void Append(ProcessTask? host, Dictionary<string, object?> entry)
    {
        if (host?.Variables == null) return;
        var vars = host.Variables;
        var history = new List<object?>();
        if (vars.GetObj(FlowConst.AidantHistory) is System.Collections.IEnumerable en and not string)
        {
            foreach (var item in en) history.Add(item);
        }
        history.Add(entry);
        vars[FlowConst.AidantHistory] = history;
    }
}
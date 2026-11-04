using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Facade;

/// <summary>
/// 已办撤回（issues/246）与动态会签·征询模式（issues/247）的门面腿
/// （对齐 Java <c>JeeflowFacade</c> 的 <c>taskRecall</c>／<c>tryDynamicCountersign</c>／
/// <c>gateDynamicCountersign</c>／<c>taskRevokeCountersign</c> 四个私有方法；
/// 形状基准＝本栈 <see cref="ActionsMain"/> 里的 <c>WithdrawAsync</c>／<c>ExecuteAsync</c>）。
///
/// <para><b>为什么分流与闸门都排在门面、且排在任何状态变更之前</b>：征询模式的发起腿
/// <b>不办结主办行</b>，它不是 <c>execute</c> 的某个 submitType 档（不新增档、不新增节点类型），
/// 而是同一入口上按<b>节点属性</b>分出的另一条语义；闸门同理——它挡的是<b>全部</b> submitType。
/// 两者都必须走在 <c>_engine.Execute*</c> 之前：内存仓储无回滚，先办结再判错就留下不可恢复的半状态
/// （与后加签写入口四判据、退回上一步守卫同一族规矩，规范 04「判据位序」纪律）。</para>
/// </summary>
public partial class JeeflowFacade
{
    // ═══ 已办撤回（processTask/recall）═══

    /// <summary>
    /// 已办撤回（规范 06 §<c>processTask/recall</c>）：办理人撤回自己刚办结的那一手，同实例内复活重办。
    /// 判据与落库全在引擎（<see cref="JeeflowEngine.RecallTaskAsync"/>），门面只做参数归一
    /// （<c>operator</c> 硬必填、主键档缺失响亮报错，绝不拿 <c>''</c>/<c>0</c> 当 id 落库）与信封包装。
    /// <para>位序照 java <c>taskRecall</c>：<c>operator</c> 空判在前、主键缺失判在后。</para>
    /// </summary>
    private async Task<Dictionary<string, object?>> TaskRecallAsync(FlowData args)
    {
        var taskId = ToLong(args.GetObj(FlowConst.ProcessTaskIdKey));
        var op = ToStr(args.GetObj("operator"))?.Trim();
        if (string.IsNullOrEmpty(op)) return Error("operator 必填");
        if (taskId == null) return Error("processTaskId 缺失");
        var resurrected = await _engine.RecallTaskAsync(taskId.Value, op);
        var data = new Dictionary<string, object?>
        {
            ["processTaskId"] = resurrected.TaskId?.ToString(),
            ["taskState"] = resurrected.TaskState,
        };
        return Ok(data);
    }

    // ═══ 动态会签：发起分流（execute 内）═══

    /// <summary>
    /// 动态会签发起分流（规范 04 · 动态会签／规范 06 §2.7）：征询节点带 <c>tf_addSignOperators</c>
    /// 的提交＝<b>只建成员行、不办结主办行</b>，响应带 <c>data.dynamicCountersign</c>
    /// <c>{round, total, submitted}</c> marker；<b>非征询提交返回 null</b>，让 <c>execute</c> 走普通流转。
    ///
    /// <para>判据全排前（互斥／参与人／加给自己／重复／上限），报错即零写入——建单是最后一步。
    /// 「返回 null 而非报错」的四档（行不存在、行不 DOING、本次没点人、节点不是征询档）都是
    /// "这压根不是动态会签发起"，不该由本腿裁决；<b>成员行冒充主办发起</b>也返回 null，
    /// 交给闸门腿报它那一句逐字文案（<see cref="GateDynamicCountersignAsync"/>）。</para>
    /// </summary>
    private async Task<Dictionary<string, object?>?> TryDynamicCountersignAsync(
        long? taskId, string? op, FlowData flowArgs)
    {
        var host = await _repository.FindTaskByIdAsync(taskId);
        if (host == null || !host.IsDoing()) return null;
        var operators = AddSignSupport.Operators(flowArgs);
        if (operators.Count == 0) return null;
        var inst = await _repository.FindInstanceByIdAsync(host.ProcessInstanceId);
        if (inst == null) return null;
        var def = await _repository.FindDefineByIdAsync(inst.DefineId);
        if (def == null) return null;
        var model = ModelParser.Parse(def.Content, _context);
        if (model.GetNode(host.TaskName) is not TaskModel collectNode
            || !collectNode.DynamicCountersign) return null;
        // 成员行不能冒充主办发起（拉人只限主办行）——放行给闸门腿报它的逐字文案
        if (CollectSupport.IsMemberRow(inst.Tasks, host)) return null;

        // 互斥（与 tf_nextNodeOperator 同传＝死参数，报错不静默）——与后加签共用同一枚文案
        var nextOp = flowArgs.GetObj(FlowConst.NextNodeOperator);
        if (nextOp != null && (nextOp.ToString() ?? "").Trim().Length > 0)
            throw new JeeflowException(AddSignSupport.MsgMutex);
        // 主办行此刻是 DOING，故 can-execute 判据仍走 IsAllowed（承重判据 3 只约束"已办结行"那一族）
        if (!host.IsAllowed(op)) throw new JeeflowException(WfErr.NotAllowedExecute);

        var round = CollectSupport.LastRound(host) + 1;
        var max = collectNode.AddSignMaxRounds;
        if (max > 0 && round > max)
            throw new JeeflowException(string.Format(AddSignSupport.MsgMaxRounds, collectNode.Name, max));

        var reason = flowArgs.GetStr(FlowConst.AddSignReason);
        var existing = CollectSupport.MemberRows(inst.Tasks, host);
        foreach (var target in operators)
        {
            if (target == op) throw new JeeflowException(AddSignSupport.MsgSelf);
            foreach (var m in existing)
            {
                if (m.IsDoing() && m.ActorIds.Contains(target))
                    throw new JeeflowException(
                        string.Format(CollectSupport.MsgDuplicate, target, collectNode.Name));
            }
        }

        // ── 判据全过：建成员行（同节点同名、parent＝主办行、轮次/原因/分支归属三戳）＋
        // 落库逐行 fire 码 3；主办行盖轮次戳＋追加账本后 updateTask（未办结＝征询挂起）──
        await _engine.CreateCollectMemberRowsAsync(inst, model, collectNode, host, op,
            operators, round, reason);
        CollectSupport.AppendCollectHistory(host, op, operators, round, reason, Clock);
        AddSignSupport.PutRound(host, collectNode.Name, round);
        await _repository.UpdateTaskAsync(host);

        var count = CollectSupport.CountMembers(inst.Tasks, host);
        return new Dictionary<string, object?>
        {
            // 与 java marker 键序同形：dynamicCountersign = {round, total, submitted}
            ["dynamicCountersign"] = new Dictionary<string, object?>
            {
                ["round"] = round,
                ["total"] = count[0],
                ["submitted"] = count[1],
            },
        };
    }

    // ═══ 动态会签：闸门（execute 内，紧接分流之后）═══

    /// <summary>
    /// 动态会签闸门（规范 04 · 动态会签）：征询节点上——主办行未收齐成员意见 ⇒ 逐字文案报错
    /// （<b>挡全部 submitType，含驳回</b>；唯一出口＝<c>processTask/revokeDynamicCountersign</c>）；
    /// 成员行携带拉人参数 ⇒ 报错（拉人只限主办行）。非征询节点／非本档提交原样放行。
    /// <para>收齐判据＝<b>还有无 DOING 成员行</b>（承重判据 2），计数跳过撤回清场的 99 行。</para>
    /// </summary>
    private async Task GateDynamicCountersignAsync(long? taskId, FlowData flowArgs)
    {
        var task = await _repository.FindTaskByIdAsync(taskId);
        if (task == null || !task.IsDoing()) return;
        var inst = await _repository.FindInstanceByIdAsync(task.ProcessInstanceId);
        if (inst == null) return;
        var def = await _repository.FindDefineByIdAsync(inst.DefineId);
        if (def == null) return;
        var model = ModelParser.Parse(def.Content, _context);
        if (model.GetNode(task.TaskName) is not TaskModel node || !node.DynamicCountersign) return;
        if (CollectSupport.IsMemberRow(inst.Tasks, task))
        {
            if (AddSignSupport.Operators(flowArgs).Count > 0)
                throw new JeeflowException(CollectSupport.MsgMemberPull);
            return;
        }
        var pending = CollectSupport.PendingMembers(inst.Tasks, task);
        if (pending.Count > 0)
        {
            var count = CollectSupport.CountMembers(inst.Tasks, task);
            throw new JeeflowException(string.Format(
                CollectSupport.MsgPending, task.TaskName, count[1], count[0]));
        }
    }

    // ═══ 撤回动态会签（processTask/revokeDynamicCountersign）═══

    /// <summary>
    /// 撤回动态会签（规范 06 §<c>processTask/revokeDynamicCountersign</c>）：废弃主办行下<b>全部未答
    /// 成员行</b>（复用废弃腿），账本追加撤回条目；<b>已提交意见行原样保留</b>（审计凭证不动）；
    /// 闸门自然恢复（判据＝还有无 DOING 成员行）；<b>轮次不消耗</b>。
    /// 独立 action，不进 <c>execute</c> 参数空间（撤回不是办结动作——任务操作族先例＝
    /// <c>transfer</c>/<c>removeTaskActor</c> 全是独立 action）。
    ///
    /// <para><b>参与人判断按名册、不走 <see cref="ProcessTask.IsAllowed"/></b>（承重判据 3）：
    /// 主办行可能已经办结（收齐后拍板前又想起撤一轮），而 <c>IsAllowed</c> 只认 DOING 行会假拒；
    /// "有没有待答成员"这一判据随后兜底，所以这里宽一档按名册判，与 java 同尺。</para>
    /// <para>位序照 java <c>taskRevokeCountersign</c>：<c>operator</c> 空判在前、主键缺失判在后。</para>
    /// </summary>
    private async Task<Dictionary<string, object?>> TaskRevokeCountersignAsync(FlowData args)
    {
        var taskId = ToLong(args.GetObj(FlowConst.ProcessTaskIdKey));
        var op = ToStr(args.GetObj("operator"))?.Trim();
        var reason = ToStr(args.GetObj("reason"));
        if (string.IsNullOrEmpty(op)) return Error("operator 必填");
        if (taskId == null) return Error("processTaskId 缺失");
        var host = await _repository.FindTaskByIdAsync(taskId);
        if (host == null) return Error("任务不存在");
        if (!IsPrivilegedOperator(op) && !host.ActorIds.Contains(op!))
            return Error(CollectSupport.MsgNoPermissionRevoke);
        var inst = await _repository.FindInstanceByIdAsync(host.ProcessInstanceId);
        if (inst == null) return Error("流程实例不存在");
        var def = await _repository.FindDefineByIdAsync(inst.DefineId);
        if (def == null) return Error("流程定义不存在");
        var model = ModelParser.Parse(def.Content, _context);
        if (model.GetNode(host.TaskName) is not TaskModel node || !node.DynamicCountersign)
            return Error(string.Format(CollectSupport.MsgNoPending, host.TaskName));
        var pending = CollectSupport.PendingMembers(inst.Tasks, host);
        if (pending.Count == 0)
            return Error(string.Format(CollectSupport.MsgNoPending, host.TaskName));

        var revokedActors = new List<string?>();
        foreach (var m in pending)
        {
            m.Abandon(op, Clock);   // update_user 回写真实撤回人（与实例撤回那族同尺）
            revokedActors.Add(m.ActorIds.Count == 0 ? null : m.ActorIds[0]);
            await _repository.UpdateTaskAsync(m);
        }
        CollectSupport.AppendRevokeHistory(host, op, revokedActors,
            CollectSupport.LastRound(host), reason, Clock);
        await _repository.UpdateTaskAsync(host);
        var data = new Dictionary<string, object?>
        {
            ["revoked"] = revokedActors.Count,
            ["actors"] = revokedActors,
        };
        return Ok(data);
    }
}

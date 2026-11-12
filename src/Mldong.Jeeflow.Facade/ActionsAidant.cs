using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Facade;

/// <summary>
/// 动态协办（issues/253）的门面腿（对齐 Java <c>JeeflowFacade.taskAidant</c>／<c>parseWaitCompletion</c>；
/// 形状基准＝本栈 <see cref="ActionsCollect"/> 里的 <c>TaskRecallAsync</c>）。
///
/// <para>门面只做参数归一与信封包装，判据/建单/账本全在引擎（<see cref="JeeflowEngine.AidantTaskAsync"/>，
/// 与 recall 同分工）。<c>operator</c> 硬必填（不接受缺省，与 transfer/withdraw 同口径）；
/// <c>actorIds</c> 逗号串与数组两形共用 §2.11 那枚归一单点（<see cref="PageQuery.NormalizeActors(object?)"/>），
/// 空 ⇒ 逐字「actorIds 必填」。<c>waitCompletion</c> 选填 bool，缺省 false＝主办不等协办办结即可推进。</para>
///
/// <para>成功 <c>data → null</c>（规范 06）；失败 msg 见跨栈统一文案表协办六行。
/// 权限码复用 <c>wf:processTask:execute</c>（OR 规则，照 246/247 B 案惯例，见 DefaultActionPermissionProvider）。</para>
/// </summary>
public partial class JeeflowFacade
{
    /// <summary>
    /// 动态协办（规范 06 §<c>processTask/aidant</c>）：主办把「这活」交给别人干——按人派生 task_type=1
    /// 协办子行，主办行原样保留 DOING，节点推进仍由主办行办结触发。
    /// <para>位序照 java <c>taskAidant</c>：<c>operator</c> 空判在前、主键缺失判在后、actorIds 归一后空判第三。</para>
    /// </summary>
    private async Task<Dictionary<string, object?>> TaskAidantAsync(FlowData args)
    {
        var taskId = ToLong(args.GetObj(FlowConst.ProcessTaskIdKey));
        var op = ToStr(args.GetObj("operator"))?.Trim();
        if (string.IsNullOrEmpty(op)) return Error("operator 必填");
        if (taskId == null) return Error("processTaskId 缺失");
        var actorIds = PageQuery.NormalizeActors(args.GetObj(FlowConst.ActorIdsKey));
        if (actorIds.Count == 0) return Error(AidantSupport.MsgNoActors);
        var reason = ToStr(args.GetObj("reason"));
        var waitCompletion = ParseWaitCompletion(args.GetObj("waitCompletion"));
        await _engine.AidantTaskAsync(taskId.Value, actorIds, reason, waitCompletion, op);
        return Ok();
    }

    /// <summary>
    /// <c>waitCompletion</c> 入参解析（＝java <c>parseWaitCompletion</c>）：缺省 false（规范 06「选填 bool，
    /// 缺省 false」）。布尔与 "true"/"false" 字符串两形都认（前端 JSON 与表单串两种来源）；其余值按缺省 false
    /// 处理——契约只定义了 bool 档，不为非法值新造文案（跨栈统一文案表协办六行之外不加第七行）。
    /// </summary>
    private static bool ParseWaitCompletion(object? value)
    {
        if (value == null) return false;
        if (value is bool b) return b;
        return bool.TryParse(value.ToString()?.Trim(), out var r) && r;
    }
}
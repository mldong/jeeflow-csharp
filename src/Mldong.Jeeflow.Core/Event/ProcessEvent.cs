namespace Mldong.Jeeflow.Core;

/// <summary>
/// 流程事件（对齐 Java ProcessEvent）：事件只带 id 不带业务上下文，副作用归集成层。
/// ccActorId 仅 CC_CREATE 用——逐抄送人 fire、直传事件体（issues/102）。
/// </summary>
public sealed class ProcessEvent
{
    public ProcessEventType EventType { get; init; }
    public long? SourceId { get; init; }
    public string? CcActorId { get; init; }
    public FlowData Data { get; init; } = new();
}

/// <summary>
/// 流程事件发布器（C12/C13）：引擎只 fire，监听器逐个隔离——
/// 单监听器异常只记日志，不中断后续监听器与主流程（issues/104 P2 统一口径）。
/// </summary>
public static class ProcessPublisher
{
    public static async Task NotifyAsync(
        ProcessEvent @event, IEnumerable<IProcessEventListener> listeners)
    {
        foreach (var listener in listeners)
        {
            try
            {
                await listener.OnEventAsync(@event);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(
                    $"[jeeflow] process event listener error: eventType={@event.EventType}, " +
                    $"sourceId={@event.SourceId}, listener={listener.GetType().Name}: {e.Message}");
            }
        }
    }

    /// <summary>
    /// CC_CREATE（码 4）唯一 fire 口：cc 行<b>落库之后</b>逐抄送人 fire 一次，ccActorId 直传事件体。
    /// <para>引擎自动路径（发起 <c>f_ccActors</c>／办理 <c>tf_ccActors</c>）与门面手动路径
    /// （<c>processInstance/createCCInstance</c>）<b>共用这一个实现</b>——spec §11.2 原则 1
    /// 「同一事实只发一次，路径不进事件名」，issues/132 §4.5 待拍① 据此定为「手动支也要 fire」。
    /// 行为基准取 Java <c>JeeflowEngineImpl</c> 的单一 <c>notifyCcCreate</c>（spec §11.7）。</para>
    /// <para><b>入参一律是“实际新建的 actor 子集”</b>（issues/141 G2 · spec 06 §4）：调用点先走
    /// <see cref="IProcessRepository.CreateCcInstanceIfAbsentAsync"/> 拿到子集，子集为空整支不 fire——
    /// §11.2 原则 1「码=事实」，重复抄送没发生“创建”就不该发码 4，严禁照旧按原始请求全量 fire。</para>
    /// </summary>
    public static async Task NotifyCcCreateAsync(
        long instanceId, IEnumerable<string> ccActorIds,
        IEnumerable<IProcessEventListener> listeners)
    {
        foreach (var ccActorId in ccActorIds)
        {
            await NotifyAsync(
                new ProcessEvent
                {
                    EventType = ProcessEventType.CcCreate,
                    SourceId = instanceId,
                    CcActorId = ccActorId,
                },
                listeners);
        }
    }

    /// <summary>
    /// 实例终态（<see cref="ProcessEventType.ProcessInstanceEnd"/>／码 2）fire 的<b>唯一</b>收口：
    /// <c>sourceId</c>＝instanceId，直传载荷键 <c>instanceId</c> ＋ <c>state</c>。
    /// 办结与拒绝共用这一支，规范名不拆，靠载荷 state 分（spec §11.3 码 2／§11.6 收口口径）。
    ///
    /// <para><b>调用前提</b>（§11.2 原则 3／08-compliance 场景 32）：实例那一行的 <c>State</c>
    /// <b>已经落库</b>。本栈唯一调用点＝<c>JeeflowEngine.FlushInstanceEndEventsAsync</c>，
    /// 排在 <c>repository.UpdateInstanceAsync</c> 成功返回之后；子流程级联里被连带办结的
    /// <b>父实例</b>不走那次 update，flush 先补写它自己的行再播。</para>
    /// </summary>
    /// <param name="state">落库后的实例状态整数（处理器落定时刻的快照，见 <see cref="PendingInstanceEnd"/>）</param>
    public static Task NotifyInstanceEndAsync(
        long? instanceId, int? state, IEnumerable<IProcessEventListener> listeners)
    {
        if (instanceId == null) return Task.CompletedTask;
        return NotifyAsync(
            new ProcessEvent
            {
                EventType = ProcessEventType.ProcessInstanceEnd,
                SourceId = instanceId,
                Data = new FlowData
                {
                    ["instanceId"] = instanceId,
                    ["state"] = state,
                },
            },
            listeners);
    }
}

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
}

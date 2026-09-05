namespace Mldong.Jeeflow.Core;

/// <summary>
/// 时钟 SPI（保留 MoonBit 决策）：引擎/门面所有"当前时间"经此收口。
/// 测试注入 FixedClock 让一致性快照天然确定（7 语言一致性比对强依赖此确定性）。
/// </summary>
public interface IClock
{
    /// <summary>当前时间（本地语义，格式化由出口层统一）。</summary>
    DateTime Now { get; }
}

/// <summary>默认系统时钟。</summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTime Now => DateTime.Now;
}

/// <summary>固定时钟（测试/一致性快照驱动用）。</summary>
public sealed class FixedClock : IClock
{
    public FixedClock(DateTime now) => Now = now;
    public DateTime Now { get; set; }
}

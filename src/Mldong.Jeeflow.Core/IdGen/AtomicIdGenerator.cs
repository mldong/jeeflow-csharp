namespace Mldong.Jeeflow.Core;

/// <summary>ID 生成器 SPI（对齐 Java IIdGenerator）。</summary>
public interface IIdGenerator
{
    /// <summary>生成下一个唯一 ID。</summary>
    long NextId();
}

/// <summary>
/// 雪花 ID 默认实现（EPOCH=1288834974657 对齐联邦 TsID 口径，方案 §3.2）。
/// 41 位时间戳 + 10 位机器 + 12 位序列，单实例线程安全。
/// </summary>
public sealed class AtomicIdGenerator : IIdGenerator
{
    public const long TwitterEpoch = 1288834974657L;
    private const int WorkerIdBits = 10;
    private const int SequenceBits = 12;
    private const long MaxWorkerId = -1L ^ (-1L << WorkerIdBits);
    private const long SequenceMask = -1L ^ (-1L << SequenceBits);
    private const int TimestampShift = WorkerIdBits + SequenceBits;

    private readonly object _lock = new();
    private readonly IClock _clock;
    private long _workerId;
    private long _lastTimestamp = -1L;
    private long _sequence;

    public AtomicIdGenerator(long workerId = 0L, IClock? clock = null)
    {
        if (workerId < 0 || workerId > MaxWorkerId)
            throw new ArgumentOutOfRangeException(nameof(workerId), $"workerId 必须在 [0,{MaxWorkerId}]");
        _workerId = workerId;
        _clock = clock ?? SystemClock.Instance;
    }

    public long NextId()
    {
        lock (_lock)
        {
            var timestamp = CurrentMillis();
            if (timestamp < _lastTimestamp)
                timestamp = _lastTimestamp; // 时钟回拨：沿用上次时间戳（序列仍递增，到 4096 抛错）
            if (timestamp == _lastTimestamp)
            {
                _sequence = (_sequence + 1) & SequenceMask;
                if (_sequence == 0)
                {
                    // 当前毫秒序列耗尽，自旋等待下一毫秒
                    do { timestamp = CurrentMillis(); } while (timestamp <= _lastTimestamp);
                }
            }
            else
            {
                _sequence = 0;
            }
            _lastTimestamp = timestamp;
            return ((timestamp - TwitterEpoch) << TimestampShift) | (_workerId << SequenceBits) | _sequence;
        }
    }

    private long CurrentMillis() =>
        (long)(_clock.Now.ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds;
}

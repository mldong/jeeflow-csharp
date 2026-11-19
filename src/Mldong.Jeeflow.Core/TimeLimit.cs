namespace Mldong.Jeeflow.Core;

/// <summary>
/// 任务时限动作凭证（spec 04 · 任务时限动作）：dueAt 用 UTC 毫秒，避免时区歧义。
/// </summary>
public sealed class TimeAction
{
    public const string TypeRemind = "REMIND";
    public const string TypeAutoComplete = "AUTO_COMPLETE";
    public const string TypeCallback = "CALLBACK";

    public string Type { get; set; } = "";
    public long DueAt { get; set; }
    public long InstanceId { get; set; }
    public long TaskId { get; set; }
    public string NodeKey { get; set; } = "";
    public int OccurrenceNo { get; set; }
    public string IdempotencyKey { get; set; } = "";
}

/// <summary>
/// 时限动作凭证仓储侧实现（集成层按框架执行队列实现，引擎在业务事务内回调）。
/// 未注册实现时引擎侧全部时限动作静默跳过。取消只清 REMIND/AUTO_COMPLETE，
/// CALLBACK 表达既成事实、不随失效取消。
/// </summary>
public interface ITimeActionSink
{
    Task InsertTimeActionAsync(TimeAction action);
    Task CancelTaskTimeActionsAsync(long taskId);
    Task CancelInstanceTimeActionsAsync(long instanceId);
}

/// <summary>仓储可选的实例级事务排他保护（spec 04 · 并发保护）。</summary>
public interface IInstanceLockRepository
{
    Task LockInstanceAsync(long instanceId);
}

/// <summary>任务时限四键的求值器（spec 02 §4.5 / spec 04）。</summary>
public static class TimeLimitSupport
{
    /// <summary>autoExecute 仅精确 "Y" 开启；N/未填/未知值一律关闭。</summary>
    public static bool AutoExecuteEnabled(TaskModel? node) => node != null && node.AutoExecute == "Y";

    /// <summary>callback 完成处理器注册名（空＝不调用；两端空白裁掉）。</summary>
    public static string CallbackName(TaskModel? node) => (node?.Callback ?? "").Trim();

    /// <summary>
    /// 首次提醒时刻（规范 02 §4.5）：变量 → 相对量（锚点＝任务创建时刻）→ 严格 HH:mm → 绝对。
    /// 任何一档都不得回落"当前时刻"或 expireTime。
    /// </summary>
    public static DateTime? FirstReminder(string? expr, FlowData? args, DateTime createTime)
    {
        if (string.IsNullOrWhiteSpace(expr)) return null;
        if (args != null && args.TryGetValue(expr, out var v) && v != null)
        {
            switch (v)
            {
                case DateTime d: return d;
                case long l: return DateTimeOffset.FromUnixTimeMilliseconds(l).LocalDateTime;
                case int i: return DateTimeOffset.FromUnixTimeMilliseconds(i).LocalDateTime;
                case string s:
                    return DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", null,
                        System.Globalization.DateTimeStyles.None, out var parsed) ? parsed : null;
            }
        }
        var rel = Relative(expr, createTime);
        if (rel != null) return rel;
        var daily = DailyHHmm(expr, createTime);
        if (daily != null) return daily;
        return DateTime.TryParseExact(expr, "yyyy-MM-dd HH:mm:ss", null,
            System.Globalization.DateTimeStyles.None, out var abs) ? abs : null;
    }

    private static DateTime? Relative(string expr, DateTime baseTime)
    {
        if (expr.Length < 2) return null;
        var unit = expr[^1];
        if (unit is not ('s' or 'm' or 'h' or 'd')) return null;
        var prefix = expr[..^1].Trim();
        if (prefix.StartsWith('+')) prefix = prefix[1..];
        if (prefix.Length == 0) return null;
        foreach (var c in prefix) if (c < '0' || c > '9') return null;
        if (!int.TryParse(prefix, out var n) || n < 0) return null;
        return unit switch
        {
            's' => baseTime.AddSeconds(n),
            'm' => baseTime.AddMinutes(n),
            'h' => baseTime.AddHours(n),
            _ => baseTime.AddDays(n),
        };
    }

    private static DateTime? DailyHHmm(string expr, DateTime baseTime)
    {
        if (expr.Length != 5 || expr[2] != ':') return null;
        for (var i = 0; i < 5; i++)
        {
            if (i == 2) continue;
            if (expr[i] < '0' || expr[i] > '9') return null;
        }
        var h = int.Parse(expr[..2]);
        var m = int.Parse(expr[3..]);
        if (h > 23 || m > 59) return null;
        var t = baseTime.Date.AddHours(h).AddMinutes(m);
        return t >= baseTime ? t : t.AddDays(1);
    }

    /// <summary>重复间隔（正整数 + s/m/h/d）；空/非法返回 null。</summary>
    public static (int N, char Unit)? ParseRepeat(string? raw)
    {
        if (raw == null) return null;
        var s = raw.Trim();
        if (s.Length < 2) return null;
        var unit = s[^1];
        if (unit is not ('s' or 'm' or 'h' or 'd')) return null;
        var prefix = s[..^1].Trim();
        if (prefix.Length == 0) return null;
        foreach (var c in prefix) if (c < '0' || c > '9') return null;
        return int.TryParse(prefix, out var n) && n > 0 ? (n, unit) : null;
    }

    public static long EpochMilli(DateTime t) =>
        new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Local)).ToUnixTimeMilliseconds();
}

using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Facade;

/// <summary>统计 3 动作（issues/103/C23）：全纯列不取 variable JSON；trend 缺参/非法显式错误；
/// data 本体裸数组；计数一律 int 出参（issues/105）。</summary>
public partial class JeeflowFacade
{
    private async Task<Dictionary<string, object?>> StatsOverviewAsync(FlowData args)
    {
        var start = ParseTime(args.GetObj("start"));
        var end = ParseTime(args.GetObj("end"));
        // B：stateIn 入参（int[]，缺省 DEFAULT_STATE_IN）
        var stateIn = ParseIntList(args.GetObj("stateIn")) ?? DefaultStateIn;

        // 1. 实例各状态计数
        var allInst = await _repository.QueryInstancesForStatsAsync(stateIn, "create_time", start, end);
        var instByState = new Dictionary<int, int>();
        foreach (var r in allInst)
        {
            var s = r.State ?? 0;
            instByState.TryAdd(s, 0);
            instByState[s]++;
        }
        int total = allInst.Count;
        int inProgress = instByState.GetValueOrDefault(10);
        int completed = instByState.GetValueOrDefault(20);
        int rejected = instByState.GetValueOrDefault(45);
        int withdrawn = instByState.GetValueOrDefault(30);
        int suspended = instByState.GetValueOrDefault(50);

        // 2. todayNew：当日创建实例数（恒按当天——经 Clock SPI，FixedClock 注入保持一致性快照确定）
        var today = Clock.Now.Date;
        var todayStart = today;
        var todayEnd = today.AddDays(1);
        var todayInst = await _repository.QueryInstancesForStatsAsync(null, "create_time", todayStart, todayEnd);
        int todayNew = todayInst.Count;

        // 3. 待办 / 逾期（全量，反映"当前"积压）
        var pendingOverdue = await _repository.StatsPendingAndOverdueCountAsync();
        int pendingTaskCount = pendingOverdue[0];
        int overdueTaskCount = pendingOverdue[1];

        // 4. 已完成任务聚合
        var taskAgg = await _repository.StatsCompletedTaskAggregateAsync();
        long taskTotal = taskAgg[0];
        long countersign = taskAgg[1];
        long onTime = taskAgg[2];
        long onTimeDenom = taskAgg[3];
        double countersignRate = taskTotal > 0 ? StatsRound4((double)countersign / taskTotal) : 0.0;
        double onTimeRate = onTimeDenom > 0 ? StatsRound4((double)onTime / onTimeDenom) : 0.0;

        // 5. 平均完成时长
        int avgDurationSeconds = await _repository.StatsAvgCompletedDurationSecondsAsync(start, end);

        // 6. rejectRate
        double rejectRate = StatsRound4((double)rejected / Math.Max(1, completed + rejected));

        return Ok(new Dictionary<string, object?>
        {
            ["total"] = total,
            ["inProgress"] = inProgress,
            ["completed"] = completed,
            ["rejected"] = rejected,
            ["withdrawn"] = withdrawn,
            ["suspended"] = suspended,
            ["todayNew"] = todayNew,
            ["avgDurationSeconds"] = avgDurationSeconds,
            ["rejectRate"] = rejectRate,
            ["pendingTaskCount"] = pendingTaskCount,
            ["overdueTaskCount"] = overdueTaskCount,
            ["countersignRate"] = countersignRate,
            ["onTimeRate"] = onTimeRate,
        });
    }

    /// <summary>statsTrend：data 本体裸数组（A 口径）；start/end/granularity 均必填（C）。</summary>
    private async Task<Dictionary<string, object?>> StatsTrendAsync(FlowData args)
    {
        var granularity = ToStr(args.GetObj("granularity"), "");
        var start = ParseTime(args.GetObj("start"));
        var end = ParseTime(args.GetObj("end"));
        if (start == null || end == null || granularity.Length == 0)
        {
            return Error("trend 缺少必填参数：start/end/granularity");
        }
        if (!ValidGranularity.Contains(granularity))
        {
            return Error("granularity 参数非法，允许值：hour/day/week/month");
        }

        // 查询时间范围内的实例（started，实例侧无 state 过滤）
        var insts = await _repository.QueryInstancesForStatsAsync(null, "create_time", start, end);
        // 查询时间范围内的已完成任务（finished，按 finish_time 分桶）
        var finishedTasks = await _repository.QueryTasksForStatsAsync(20, start, end);

        // 枚举连续桶
        var buckets = StatsEnumerateBuckets(start.Value, end.Value, granularity);
        var bucketMap = new Dictionary<string, int[]>();
        foreach (var b in buckets) bucketMap[b] = new int[2]; // [started, finished]
        foreach (var row in insts)
        {
            var bk = StatsBucketKey(row.CreateTime, granularity);
            if (bk == null || !bucketMap.ContainsKey(bk)) continue;
            bucketMap[bk][0]++;
        }
        foreach (var row in finishedTasks)
        {
            var bk = StatsBucketKey(row.FinishTime, granularity);
            if (bk == null || !bucketMap.ContainsKey(bk)) continue;
            bucketMap[bk][1]++;
        }

        var series = new List<object?>();
        foreach (var b in buckets)
        {
            var counts = bucketMap[b];
            series.Add(new Dictionary<string, object?>
            {
                ["bucket"] = b,
                ["started"] = counts[0],
                ["finished"] = counts[1],
            });
        }
        return Ok(series);
    }

    /// <summary>statsGroup：data 本体裸数组（A 口径）；dimension 非法显式错误（C23）。</summary>
    private async Task<Dictionary<string, object?>> StatsGroupAsync(FlowData args)
    {
        var start = ParseTime(args.GetObj("start"));
        var end = ParseTime(args.GetObj("end"));
        var dimension = ToStr(args.GetObj("dimension"), "define");
        var limit = ToInt(args.GetObj("limit"), DefaultStatsLimit);
        if (!ValidDimension.Contains(dimension))
        {
            return Error("dimension 参数非法，允许值：state/define/category/approver/applicant/node/stuckNode/stuckApprover/durationBucket");
        }

        List<Dictionary<string, object?>> rows;
        switch (dimension)
        {
            case "define":
                rows = await _repository.StatsDefineGroupAsync(start, end, limit);
                break;
            case "state":
            {
                var insts = await _repository.QueryInstancesForStatsAsync(null, "create_time", start, end);
                var grouped = new Dictionary<int, int>();
                foreach (var r in insts)
                {
                    var s = r.State ?? 0;
                    grouped.TryAdd(s, 0);
                    grouped[s]++;
                }
                rows = grouped.OrderByDescending(kv => kv.Value).Take(limit)
                    .Select(kv => GroupRow(kv.Key.ToString(), null, kv.Value)).ToList();
                break;
            }
            case "category":
            {
                var insts = await _repository.QueryInstancesForStatsAsync(null, "create_time", start, end);
                var instDefineTypes = new Dictionary<long, string>();
                foreach (var r in insts)
                {
                    var defId = r.ProcessDefineId;
                    if (defId != null && !instDefineTypes.ContainsKey(defId.Value))
                    {
                        var def = await _repository.FindDefineByIdAsync(defId);
                        instDefineTypes[defId.Value] = def?.Type ?? "";
                    }
                }
                var grouped = new Dictionary<string, int>();
                foreach (var r in insts)
                {
                    var type = r.ProcessDefineId != null && instDefineTypes.TryGetValue(r.ProcessDefineId.Value, out var t) ? t : "";
                    grouped.TryAdd(type, 0);
                    grouped[type]++;
                }
                rows = grouped.OrderByDescending(kv => kv.Value).Take(limit)
                    .Select(kv => GroupRow(kv.Key, null, kv.Value)).ToList();
                break;
            }
            case "approver":
            {
                var tasks = await _repository.QueryTasksForStatsAsync(20, start, end);
                var grouped = new Dictionary<string, int>();
                foreach (var r in tasks)
                {
                    var op = r.Operator;
                    if (string.IsNullOrEmpty(op)) continue;
                    grouped.TryAdd(op, 0);
                    grouped[op]++;
                }
                rows = grouped.OrderByDescending(kv => kv.Value).Take(limit)
                    .Select(kv => GroupRow(kv.Key, null, kv.Value)).ToList();
                break;
            }
            case "applicant":
            {
                var insts = await _repository.QueryInstancesForStatsAsync(null, "create_time", start, end);
                var grouped = new Dictionary<string, int>();
                foreach (var r in insts)
                {
                    var op = r.Operator;
                    if (string.IsNullOrEmpty(op)) continue;
                    grouped.TryAdd(op, 0);
                    grouped[op]++;
                }
                rows = grouped.OrderByDescending(kv => kv.Value).Take(limit)
                    .Select(kv => GroupRow(kv.Key, null, kv.Value)).ToList();
                break;
            }
            case "node":
            {
                var tasks = await _repository.QueryTasksForStatsAsync(20, start, end);
                var grouped = new Dictionary<string, (int Count, long Total)>();
                foreach (var r in tasks)
                {
                    var dn = r.DisplayName;
                    if (string.IsNullOrEmpty(dn)) continue;
                    long dur = 0;
                    if (r.FinishTime != null && r.CreateTime != null)
                        dur = (long)(r.FinishTime.Value - r.CreateTime.Value).TotalSeconds;
                    var cur = grouped.TryGetValue(dn, out var c) ? c : (0, 0);
                    grouped[dn] = (cur.Item1 + 1, cur.Item2 + dur);
                }
                rows = grouped.OrderByDescending(kv => kv.Value.Item1).Take(limit)
                    .Select(kv => new Dictionary<string, object?>
                    {
                        ["key"] = kv.Key,
                        ["label"] = null,
                        ["count"] = kv.Value.Item1,
                        ["avgDurationSeconds"] = kv.Value.Item1 > 0
                            ? (int)Math.Round((double)kv.Value.Item2 / kv.Value.Item1)
                            : (int?)null,
                    }).ToList();
                break;
            }
            case "stuckNode":
            {
                rows = await _repository.StatsStuckNodeGroupAsync(limit);
                foreach (var m in rows)
                {
                    m.TryAdd("label", null);
                    m.TryAdd("avgDurationSeconds", null);
                }
                break;
            }
            case "stuckApprover":
            {
                rows = await _repository.StatsStuckApproverGroupAsync(limit);
                foreach (var m in rows)
                {
                    m.TryAdd("label", null);
                    m.TryAdd("avgDurationSeconds", null);
                }
                break;
            }
            case "durationBucket":
            {
                var durations = await _repository.StatsCompletedInstanceDurationsAsync(start, end);
                int sameDay = 0, d1to3 = 0, d3to7 = 0, over7d = 0;
                foreach (var dur in durations)
                {
                    if (dur < 86400) sameDay++;
                    else if (dur < 259200) d1to3++;
                    else if (dur < 604800) d3to7++;
                    else over7d++;
                }
                rows = new List<Dictionary<string, object?>>();
                string[] keys = { "sameDay", "1to3d", "3to7d", "over7d" };
                int[] counts = { sameDay, d1to3, d3to7, over7d };
                for (var i = 0; i < keys.Length; i++)
                {
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["key"] = keys[i],
                        ["label"] = null,
                        ["count"] = counts[i],
                        ["avgDurationSeconds"] = null,
                    });
                }
                break;
            }
            default:
                rows = new List<Dictionary<string, object?>>();
                break;
        }
        return Ok(rows);
    }

    private static Dictionary<string, object?> GroupRow(string? key, string? label, int count) => new()
    {
        ["key"] = key,
        ["label"] = label,
        ["count"] = count,
        ["avgDurationSeconds"] = null,
    };

    // ── 统计 helper ──

    /// <summary>枚举时间桶标签列表（经 Clock 的 now 兜底，对齐 Java）。</summary>
    private List<string> StatsEnumerateBuckets(DateTime start, DateTime end, string granularity)
    {
        var buckets = new List<string>();
        switch (granularity)
        {
            case "hour":
            {
                var cursor = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0);
                while (cursor <= end)
                {
                    buckets.Add($"{cursor:yyyy-MM-dd HH}:00".Replace(" 00:", $" {cursor.Hour:00}:"));
                    cursor = cursor.AddHours(1);
                }
                break;
            }
            case "day":
            {
                var s = start.Date;
                while (s <= end.Date)
                {
                    buckets.Add(s.ToString("yyyy-MM-dd"));
                    s = s.AddDays(1);
                }
                break;
            }
            case "week":
            {
                // 对齐到周一（ISO 周，System.Globalization ISOWeek）
                var s = start.Date;
                var dow = (int)s.DayOfWeek == 0 ? 7 : (int)s.DayOfWeek;
                s = s.AddDays(1 - dow);
                while (s <= end.Date)
                {
                    buckets.Add(StatsWeekKey(s));
                    s = s.AddDays(7);
                }
                break;
            }
            case "month":
            {
                var s = new DateTime(start.Year, start.Month, 1);
                var ymEnd = end.Year * 12 + end.Month;
                var ym = s.Year * 12 + s.Month;
                while (ym <= ymEnd)
                {
                    buckets.Add($"{ym / 12:0000}-{ym % 12:00}");
                    ym++;
                }
                break;
            }
        }
        return buckets;
    }

    /// <summary>把时间按 granularity 转成桶标签。</summary>
    private static string? StatsBucketKey(DateTime? ldt, string granularity)
    {
        if (ldt == null) return null;
        var t = ldt.Value;
        switch (granularity)
        {
            case "hour":
                return $"{t:yyyy-MM-dd HH}:00";
            case "day":
                return t.ToString("yyyy-MM-dd");
            case "week":
                return StatsWeekKey(t);
            case "month":
                return $"{t.Year:0000}-{t.Month:00}";
            default:
                return null;
        }
    }

    /// <summary>ISO 周标签：YYYY-Www（对齐 Java IsoFields.WEEK_BASED_YEAR）。</summary>
    private static string StatsWeekKey(DateTime t)
    {
        var iso = System.Globalization.ISOWeek.GetYear(t);
        var week = System.Globalization.ISOWeek.GetWeekOfYear(t);
        return $"{iso}-W{week:00}";
    }

    private static double StatsRound4(double v) => Math.Round(v * 10000.0) / 10000.0;

    /// <summary>解析整数列表入参。</summary>
    private static List<int>? ParseIntList(object? val)
    {
        if (val is not List<object?> list) return null;
        var result = new List<int>();
        foreach (var o in list)
        {
            if (o is int i) result.Add(i);
            else if (o is long l) result.Add((int)l);
            else if (o is double d) result.Add((int)d);
            else if (int.TryParse(o?.ToString(), out var parsed)) result.Add(parsed);
        }
        return result.Count == 0 ? null : result;
    }
}

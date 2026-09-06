using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;

namespace Mldong.Jeeflow.Demo;

/// <summary>
/// T003：业务数据种子 driver——引擎真实启动（startAndExecute + execute），不直插 repo。
/// 矩阵 = 八语言共用 canonical（day-shift 已在 Rust demo 实测全绿，照 rust seed_business.rs 移植）：
/// 16 进行中(state=10) + 9 已完成(advance 推到 state=20) + 8 委托。
/// </summary>
public static class BusinessSeed
{
    private sealed record Row(long DefineId, string Operator, Dictionary<string, object?>? Extra, string[]? Cc);

    /// <summary>进行中 16 条：发起后停 state=10（I3/I15 冻结在决策/驳回前，I14 发起后再办两节点停 boss）</summary>
    private static readonly Row[] InProgress =
    {
        new(1, "user1", null, new[] { "userA", "userB" }),
        new(2, "user1", null, null),
        new(3, "userA", new Dictionary<string, object?> { ["amount"] = 500L }, null),
        new(4, "manager", null, new[] { "userC", "leader" }),
        new(5, "userB", null, null),
        new(6, "director", null, new[] { "manager", "boss" }),
        new(7, "userC", null, new[] { "user1" }),
        new(1, "boss", null, null),
        new(12, "user1", new Dictionary<string, object?> { ["deptLeader"] = "manager" }, null),
        new(12, "userC", new Dictionary<string, object?> { ["deptLeader"] = "director" }, null),
        new(12, "userB", new Dictionary<string, object?> { ["deptLeader"] = "user1" }, null),
        new(15, "userA", null, new[] { "boss" }),
        new(14, "leader", null, new[] { "director", "userC" }),
        new(2, "userA", null, null),
        new(10, "userB", null, null),
        new(8, "user1", null, null),
    };

    /// <summary>已完成 9 条：advance 推到 state=20（分支无关）</summary>
    private static readonly Row[] Finished =
    {
        new(1, "userA", null, new[] { "user1", "director" }),
        new(8, "userB", null, new[] { "boss", "manager" }),
        new(2, "manager", null, new[] { "boss" }),
        new(10, "director", null, null),
        new(12, "userC", new Dictionary<string, object?> { ["deptLeader"] = "leader" }, null),
        new(1, "director", null, null),
        new(5, "manager", null, null),
        new(12, "userA", new Dictionary<string, object?> { ["deptLeader"] = "director" }, null),
        new(12, "userB", new Dictionary<string, object?> { ["deptLeader"] = "user1" }, null),
    };

    /// <summary>委托 8 条：processSurrogate/page 无 operator 过滤 → 8 用户委托菜单全非空</summary>
    private static readonly string[][] Surrogates =
    {
        new[] { "user1", "userA" }, new[] { "userA", "userB" }, new[] { "userB", "userC" }, new[] { "userC", "leader" },
        new[] { "leader", "manager" }, new[] { "manager", "director" }, new[] { "director", "boss" }, new[] { "boss", "user1" },
    };

    /// <summary>种业务数据；失败逐条打日志不抛异常（demo 启动不被单条卡死）。</summary>
    public static async Task SeedAsync(JeeflowFacade facade)
    {
        var okIn = 0;
        var okFin = 0;
        var okSurr = 0;
        foreach (var row in InProgress)
        {
            var iid = await StartInstanceAsync(facade, row);
            if (iid is null) continue;
            // I14：发起后再办 leader、manager 两节点 → 停在 boss
            if (row.DefineId == 2 && row.Operator == "userA")
            {
                foreach (var actor in new[] { "leader", "manager" })
                {
                    var t = await TodoRowAsync(facade, actor, iid);
                    if (t is not null)
                    {
                        await facade.FlowAsync("processTask/execute", Args(
                            ("processTaskId", t["id"]), ("operator", actor), ("submitType", 1L)));
                    }
                    else
                    {
                        Console.Error.WriteLine($"[seed] I14 todoRow actor={actor} iid={iid} 未找到");
                    }
                }
            }
            if (row.Cc is { Length: > 0 })
            {
                await facade.FlowAsync("processInstance/createCCInstance", Args(
                    ("processInstanceId", iid), ("operator", row.Operator), ("actorIds", row.Cc)));
            }
            okIn++;
        }

        foreach (var row in Finished)
        {
            var iid = await StartInstanceAsync(facade, row);
            if (iid is null) continue;
            var state = await AdvanceAsync(facade, iid);
            if (state != 20)
                Console.Error.WriteLine($"[seed] FIN define={row.DefineId} op={row.Operator} iid={iid} 终态={state}（期望 20）");
            if (row.Cc is { Length: > 0 })
            {
                await facade.FlowAsync("processInstance/createCCInstance", Args(
                    ("processInstanceId", iid), ("operator", row.Operator), ("actorIds", row.Cc)));
            }
            okFin++;
        }

        foreach (var s in Surrogates)
        {
            var resp = await facade.FlowAsync("processSurrogate/save", Args(
                ("operator", s[0]), ("surrogate", s[1]), ("processName", ""),
                ("startTime", "2026-01-01 00:00:00"), ("endTime", "2027-12-31 23:59:59")));
            if (IsOk(resp)) okSurr++;
            else Console.Error.WriteLine($"[seed] surrogate {s[0]}->{s[1]} 失败");
        }
        Console.WriteLine($"[seedBusiness] done: in-progress {okIn}/16, finished {okFin}/9, surrogates {okSurr}/8");
    }

    private static async Task<object?> StartInstanceAsync(JeeflowFacade facade, Row row)
    {
        var args = new FlowData { ["processDefineId"] = row.DefineId, ["operator"] = row.Operator };
        if (row.Extra is not null)
            foreach (var kv in row.Extra)
                args[kv.Key] = kv.Value;
        var resp = await facade.FlowAsync("processDefine/startAndExecute", args);
        if (!IsOk(resp))
        {
            Console.Error.WriteLine($"[seed] startAndExecute define={row.DefineId} op={row.Operator} 失败");
            return null;
        }
        var data = resp.GetValueOrDefault("data") as Dictionary<string, object?>;
        return data is not null && data.TryGetValue(FlowConst.ProcessInstanceIdKey, out var iid) ? iid : null;
    }

    /// <summary>advance 原语：循环读 detail，对每个 doing 任务以其自身 actor execute(submitType=1)。</summary>
    private static async Task<long> AdvanceAsync(JeeflowFacade facade, object iid)
    {
        for (var i = 0; i < 30; i++)
        {
            var resp = await facade.FlowAsync("processInstance/detail", Args(("id", iid)));
            var data = resp.GetValueOrDefault("data") as Dictionary<string, object?>;
            if (data is null) return -1;
            var state = ToLong(data.GetValueOrDefault("state"), -1);
            if (state != 10) return state;
            var tasks = data.GetValueOrDefault("tasks") as IEnumerable<object?> ?? Array.Empty<object?>();
            var progress = false;
            foreach (var tObj in tasks)
            {
                var t = tObj as Dictionary<string, object?>;
                if (t is null || ToLong(t.GetValueOrDefault("taskState")) != 10) continue;
                var actor = t.GetValueOrDefault("operator") as string;
                if (string.IsNullOrEmpty(actor))
                {
                    var actors = t.GetValueOrDefault("taskActorIdList") as IEnumerable<object?>;
                    actor = actors?.FirstOrDefault(a => a is not null)?.ToString();
                }
                if (string.IsNullOrEmpty(actor)) continue;
                var r = await facade.FlowAsync("processTask/execute", Args(
                    ("processTaskId", t["id"]), ("operator", actor), ("submitType", 1L)));
                if (IsOk(r)) progress = true;
                else Console.Error.WriteLine($"[seed] advance execute iid={iid} actor={actor} 失败");
            }
            if (!progress) return state;
        }
        return -1;
    }

    /// <summary>仅 I14 用：在该实例里找 op 的 doing 任务行。</summary>
    private static async Task<Dictionary<string, object?>?> TodoRowAsync(JeeflowFacade facade, string op, object iid)
    {
        var resp = await facade.FlowAsync("processTask/todoList", Args(
            ("operator", op), ("pageNum", 1L), ("pageSize", 200L)));
        var data = resp.GetValueOrDefault("data") as Dictionary<string, object?>;
        var rows = data?.GetValueOrDefault("rows") as IEnumerable<object?>;
        foreach (var rObj in rows ?? Array.Empty<object?>())
        {
            if (rObj is not Dictionary<string, object?> r) continue;
            if (Convert.ToString(r.GetValueOrDefault("processInstanceId")) == Convert.ToString(iid)
                && ToLong(r.GetValueOrDefault("taskState")) == 10)
                return r;
        }
        return null;
    }

    private static FlowData Args(params (string Key, object? Value)[] pairs)
    {
        var fd = new FlowData();
        foreach (var (key, value) in pairs) fd[key] = value;
        return fd;
    }

    private static bool IsOk(Dictionary<string, object?>? resp)
        => resp is not null && resp.GetValueOrDefault("code") is not null && ToLong(resp.GetValueOrDefault("code")) == 0;

    private static long ToLong(object? v, long fallback = 0)
    {
        if (v is null) return fallback;
        try
        {
            return Convert.ToInt64(v);
        }
        catch
        {
            return fallback;
        }
    }
}

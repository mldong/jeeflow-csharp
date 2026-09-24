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

    /// <summary>
    /// 发起表单：f_ 前缀 = 实例变量，前端「申请信息」区读 formData/ext 回显（13 无 apply 节点故不列）。
    /// 八栈同表同值同序——勿改勿重排。
    /// 日期一律写死字面量，不按当前时钟算（八栈机器时区/系统时间各异，算出来会漂）。
    /// 字段名严禁 amount / finalAmount——它们是 03-decision-expr、10-mixed-mode 条件表达式的判定变量，撞上会改流程走向。
    /// </summary>
    private static readonly Dictionary<long, Dictionary<string, object?>> FormByDefine = new()
    {
        [1] = new()
        {
            ["f_reason"] = "家中有事需请假", ["f_days"] = 3, ["f_leaveType"] = "annual",
            ["f_startDate"] = "2026-09-01", ["f_endDate"] = "2026-09-03",
        },
        [2] = new()
        {
            ["f_reason"] = "项目上线后调休", ["f_days"] = 2, ["f_leaveType"] = "annual",
            ["f_startDate"] = "2026-09-07", ["f_endDate"] = "2026-09-08",
        },
        [3] = new()
        {
            ["f_reason"] = "出差报销申请", ["f_days"] = 1, ["f_leaveType"] = "personal",
            ["f_startDate"] = "2026-09-10", ["f_endDate"] = "2026-09-10",
        },
        [4] = new()
        {
            ["f_reason"] = "培训进修请假", ["f_days"] = 5, ["f_leaveType"] = "sick",
            ["f_startDate"] = "2026-09-14", ["f_endDate"] = "2026-09-18",
        },
        [5] = new()
        {
            ["f_reason"] = "年假出行", ["f_days"] = 4, ["f_leaveType"] = "annual",
            ["f_startDate"] = "2026-09-21", ["f_endDate"] = "2026-09-24",
        },
        [6] = new()
        {
            ["f_reason"] = "婚假申请", ["f_days"] = 10, ["f_leaveType"] = "personal",
            ["f_startDate"] = "2026-09-28", ["f_endDate"] = "2026-10-07",
        },
        [7] = new()
        {
            ["f_reason"] = "病假休养", ["f_days"] = 6, ["f_leaveType"] = "sick",
            ["f_startDate"] = "2026-10-12", ["f_endDate"] = "2026-10-17",
        },
        [8] = new()
        {
            ["f_reason"] = "产检假", ["f_days"] = 3, ["f_leaveType"] = "sick",
            ["f_startDate"] = "2026-10-19", ["f_endDate"] = "2026-10-21",
        },
        [9] = new()
        {
            ["f_reason"] = "陪产假", ["f_days"] = 5, ["f_leaveType"] = "personal",
            ["f_startDate"] = "2026-10-26", ["f_endDate"] = "2026-10-30",
        },
        [10] = new()
        {
            ["f_reason"] = "事假处理家务", ["f_days"] = 2, ["f_leaveType"] = "personal",
            ["f_startDate"] = "2026-11-02", ["f_endDate"] = "2026-11-03",
        },
        [11] = new()
        {
            ["f_bizType"] = "purchase", ["f_budget"] = 12000, ["f_urgency"] = "normal",
            ["f_desc"] = "采购一批开发板与传感器",
        },
        [12] = new()
        {
            ["f_reason"] = "部门例行调休", ["f_days"] = 1, ["f_leaveType"] = "annual",
            ["f_startDate"] = "2026-11-09", ["f_endDate"] = "2026-11-09",
        },
        [14] = new()
        {
            ["f_reason"] = "外派学习请假", ["f_days"] = 7, ["f_leaveType"] = "annual",
            ["f_startDate"] = "2026-11-16", ["f_endDate"] = "2026-11-22",
        },
        [15] = new()
        {
            ["f_reason"] = "丧假", ["f_days"] = 3, ["f_leaveType"] = "personal",
            ["f_startDate"] = "2026-11-23", ["f_endDate"] = "2026-11-25",
        },
    };

    /// <summary>
    /// 办理表单：tf_ 前缀 = 任务变量，前端「办理表单」区读 taskFormData 回显。
    /// 键 = 审批节点的 formKey；表里没有的 formKey 只落通用审批意见，不臆造字段。
    /// 八栈同表同值同序——勿改勿重排。
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, object?>> TfByForm = new()
    {
        ["leave-form"] = new()
        {
            ["tf_approvedDays"] = 3, ["tf_needExtra"] = "no", ["tf_remark"] = "按项目排期核准，注意工作交接",
        },
        ["review-form"] = new()
        {
            ["tf_riskLevel"] = "low", ["tf_needLegalDoc"] = "no", ["tf_reviewOpinion"] = "条款与预算均无风险",
        },
        ["boss-form"] = new()
        {
            ["tf_finalDecision"] = "agree", ["tf_finalAmount"] = 8000, ["tf_bossNote"] = "同意，走年度预算",
        },
        ["check-form"] = new()
        {
            ["tf_invoiceOk"] = "yes", ["tf_amountChecked"] = 8000, ["tf_checkNote"] = "票据齐全，计入差旅科目",
        },
        ["countersign-form"] = new()
        {
            ["tf_signVote"] = "support", ["tf_signAmount"] = 5000, ["tf_signOpinion"] = "本条线无异议",
        },
        ["seq-form"] = new()
        {
            ["tf_seqStage"] = "first", ["tf_seqVote"] = "pass", ["tf_seqOpinion"] = "初审通过，转下一人",
        },
        ["approve-form"] = new()
        {
            ["tf_approveResult"] = "ok", ["tf_approveAmount"] = 8000, ["tf_approveNote"] = "审批通过",
        },
        ["ratio-form"] = new()
        {
            ["tf_ratioVote"] = "agree", ["tf_ratioOpinion"] = "达到比例即可通过",
        },
        ["veto-form"] = new()
        {
            ["tf_vetoResult"] = "pass", ["tf_vetoReason"] = "无异议",
        },
        ["form-a"] = new()
        {
            ["tf_branchA"] = "a1", ["tf_branchANote"] = "A 分支选方案 A1",
        },
        ["form-b"] = new()
        {
            ["tf_branchB"] = "b1", ["tf_branchBNote"] = "B 分支选方案 B1",
        },
        ["field-form"] = new()
        {
            ["tf_ownerName"] = "张三", ["tf_field"] = "tech", ["tf_fieldNote"] = "技术域评估通过",
        },
        ["operator-form"] = new()
        {
            ["tf_selfCheck"] = "done", ["tf_operatorNote"] = "发起人自查无误",
        },
        ["dept-form"] = new()
        {
            ["tf_deptAgree"] = "yes", ["tf_deptQuota"] = 8000, ["tf_deptNote"] = "同意占用本部门额度",
        },
        ["role-form"] = new()
        {
            ["tf_roleResult"] = "pass", ["tf_roleNote"] = "角色审批通过",
        },
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
                        // todoList 行同样带 formKey（facade TaskRowToMap），与 advance 同口径填办理表单
                        var ex = Args(
                            ("processTaskId", t["id"]), ("operator", actor), ("submitType", 1L));
                        WithTaskForm(ex, t.GetValueOrDefault("formKey"));
                        await facade.FlowAsync("processTask/execute", ex);
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
        // f_* 先铺、extra 后铺：已有的流程变量 amount / deptLeader 优先，不被表单值盖掉
        if (FormByDefine.TryGetValue(row.DefineId, out var form))
            foreach (var kv in form)
                args[kv.Key] = kv.Value;
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
                // detail tasks 走 TaskVo，同样出口带 formKey；两处 execute 调用点必须同口径
                var ex = Args(
                    ("processTaskId", t["id"]), ("operator", actor), ("submitType", 1L));
                WithTaskForm(ex, t.GetValueOrDefault("formKey"));
                var r = await facade.FlowAsync("processTask/execute", ex);
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

    /// <summary>
    /// 办理表单落库：先给通用审批意见，再按该节点 formKey 覆盖专属字段。
    /// 抽成 helper 是因为两处 execute 调用点（I14 特例 / advance 循环）必须同口径，
    /// 否则八栈横评里同一节点会填出不一样的数据。
    /// </summary>
    private static void WithTaskForm(Dictionary<string, object?> ex, object? formKey)
    {
        ex["tf_approvalComment"] = "同意，情况已核实";
        if (formKey is null) return;
        if (TfByForm.TryGetValue(formKey.ToString() ?? string.Empty, out var fields))
            foreach (var kv in fields)
                ex[kv.Key] = kv.Value;
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

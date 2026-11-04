using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// 「动态会签（征询模式 <c>dynamicCountersign</c>）」验收（issues/247 · C# 腿）——移植基准＝
/// jeeflow-java <c>DynamicCountersignTest</c>（8 格，逐格语义对齐），本文件在 8 格之外<b>另加三格</b>：
/// 闸门报错的三面零写入、轮次上限那一拦、以及<b>废弃腿扩双开关</b>（离开本节点时 pending 成员行一并清场）。
///
/// <para>钉住的面（规范 02 §4.4／规范 04「动态会签（征询模式）」／规范 06 §2.7＋
/// §processTask/revokeDynamicCountersign）：发起<b>不办结主办行</b>、成员行三戳
/// （parent／轮次／reason）、账本含 reason、响应 marker、闸门逐字文案挡<b>全部</b> submitType、
/// 成员行禁拉人、重复／给自己／上限三拦、撤回恢复闸门且已交意见保留、多轮跨轮计数、
/// detail 投影、三条解析期硬校验、废弃腿扩双开关。</para>
///
/// <para><b>三条承重判据在本文件各有一格钉</b>：①成员行识别＝parent 且<b>同节点同名</b>
/// （见 <see cref="HostProceedsAfterAllSubmittedWithProjection"/> 后半段——主办办结推进后，
/// 下游 final 行的 parent 同样是主办行 id，只认 parent 会被误当成员）；②闸门收齐＝无 DOING 成员行、
/// 计数跳过 99（见 <see cref="RevokeRestoresGateAndKeepsSubmittedOpinions"/>）；
/// ③撤回的参与人判断按名册（见 <see cref="RevokePermissionByRosterNotDoingRows"/>）。</para>
///
/// <para>发起／闸门／撤回在<b>门面</b>分流（对齐 java：分流点不在节点执行闸），故这些格经
/// <c>facade.FlowAsync</c> 驱动信封；成员与主办办结走引擎原路径。夹具内联 JSON，不碰共享 <c>flows/</c>。</para>
/// </summary>
public class DynamicCountersign247Tests
{
    private const int Agree = (int)WfSubmitType.Agree;                 // 1
    private const int Reject = (int)WfSubmitType.Reject;               // 2
    private const int Apply = (int)WfSubmitType.Apply;                 // 0
    private const int Return = (int)WfSubmitType.RollbackToOperator;   // 6
    private const string CollectOn = "\"dynamicCountersign\":true";

    private static FlowData Submit(int type) => new() { [FlowConst.SubmitType] = type };

    // ═══ 夹具：start → apply → review → final → end（逐字照 java DynamicCountersignTest.chain）═══

    private static string Node(string id, string props, string text) =>
        "{\"id\":\"" + id + "\",\"type\":\"snaker:task\",\"x\":0,\"y\":0,"
        + "\"properties\":{" + props + "},\"text\":{\"value\":\"" + text + "\"}}";

    private static string Edge(string id, string from, string to) =>
        "{\"id\":\"" + id + "\",\"type\":\"snaker:transition\",\"sourceNodeId\":\"" + from
        + "\",\"targetNodeId\":\"" + to + "\",\"properties\":{}}";

    private const string ApplyNode =
        "\"form\":\"apply-form\",\"taskType\":0,\"performType\":0,\"assignee\":\"applicant\"";

    private static string Chain(string reviewProps, string name)
    {
        var r = reviewProps.Length == 0 ? "\"assignee\":\"userA\"" : "\"assignee\":\"userA\"," + reviewProps;
        return "{\"name\":\"" + name + "\",\"displayName\":\"动态会签夹具\",\"type\":\"approval\",\"nodes\":["
            + "{\"id\":\"start\",\"type\":\"snaker:start\",\"x\":0,\"y\":0,\"properties\":{},\"text\":{\"value\":\"开始\"}},"
            + Node("apply", ApplyNode, "发起申请") + ","
            + Node("review", "\"form\":\"form-r\",\"taskType\":0,\"performType\":0," + r, "审批") + ","
            + Node("final", "\"form\":\"form-f\",\"assignee\":\"userF\",\"taskType\":0,\"performType\":0", "终审") + ","
            + "{\"id\":\"end\",\"type\":\"snaker:end\",\"x\":0,\"y\":0,\"properties\":{},\"text\":{\"value\":\"结束\"}}"
            + "],\"edges\":["
            + Edge("e1", "start", "apply") + "," + Edge("e2", "apply", "review") + ","
            + Edge("e3", "review", "final") + "," + Edge("e4", "final", "end")
            + "]}";
    }

    private int _seq;
    private string NextName(string prefix) => $"{prefix}-{++_seq}";

    private sealed record Stack(JeeflowEngine Engine, MemoryRepository Repo, ServiceContext Ctx,
                                JeeflowFacade Facade, long Iid);

    /// <summary>发起并办结申请节点，令牌停在开了征询档的 review 主办行上。</summary>
    private async Task<Stack> StartToReviewAsync(string flowJson)
    {
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        var name = flowJson[(flowJson.IndexOf("\"name\":\"", StringComparison.Ordinal) + 8)..];
        name = name[..name.IndexOf('"', StringComparison.Ordinal)];
        var defineId = await TestInfra.SaveFlowDefineAsync(repo, name, flowJson);
        var inst = await engine.StartProcessInstanceByIdAsync(defineId, "applicant", new FlowData());
        foreach (var t in await repo.FindDoingTasksAsync(inst.InstanceId!.Value, null))
            await engine.ExecuteProcessTaskAsync(t.TaskId!.Value, "applicant", Submit(Apply));
        return new Stack(engine, repo, ctx, new JeeflowFacade(ctx), inst.InstanceId!.Value);
    }

    // ═══ 门面驱动的三个入口 ═══

    /// <summary>经门面 <c>processTask/execute</c> 发起动态会签（带人＋选填 reason）。</summary>
    private static Task<Dictionary<string, object?>> CollectAsync(JeeflowFacade facade, long hostTaskId,
        string op, List<string> operators, string? reason)
    {
        var a = new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = hostTaskId.ToString(),
            ["operator"] = op,
            [FlowConst.SubmitType] = Agree,
            [FlowConst.AddSignOperators] = new List<object?>(operators),
        };
        if (reason != null) a[FlowConst.AddSignReason] = reason;
        return facade.FlowAsync("processTask/execute", a);
    }

    private static Task<Dictionary<string, object?>> ExecAsync(JeeflowFacade facade, long hostTaskId,
        string op, int submitType, FlowData? extra = null)
    {
        var a = new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = hostTaskId.ToString(),
            ["operator"] = op,
            [FlowConst.SubmitType] = submitType,
        };
        if (extra != null) foreach (var kv in extra) a[kv.Key] = kv.Value;
        return facade.FlowAsync("processTask/execute", a);
    }

    private static Task<Dictionary<string, object?>> RevokeAsync(JeeflowFacade facade, long hostTaskId,
        string op, string? reason = null)
    {
        var a = new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = hostTaskId.ToString(),
            ["operator"] = op,
        };
        if (reason != null) a["reason"] = reason;
        return facade.FlowAsync("processTask/revokeDynamicCountersign", a);
    }

    private static Dictionary<string, object?>? DataOf(Dictionary<string, object?> resp) =>
        resp.TryGetValue("data", out var d) ? d as Dictionary<string, object?> : null;

    private static async Task<ProcessTask?> DoingAsync(MemoryRepository repo, long iid, string nodeName) =>
        (await repo.FindDoingTasksAsync(iid, null)).FirstOrDefault(t => t.TaskName == nodeName);

    private static async Task<int> DoingCountAsync(MemoryRepository repo, long iid, string nodeName) =>
        (await repo.FindDoingTasksAsync(iid, null)).Count(t => t.TaskName == nodeName);

    private static async Task<int> TotalRowsAsync(MemoryRepository repo, long iid) =>
        (await repo.FindHistoryTasksAsync(iid)).Count;

    private static async Task<List<ProcessTask>> MembersAsync(MemoryRepository repo, ProcessTask host)
    {
        var inst = await repo.FindInstanceByIdAsync(host.ProcessInstanceId!.Value);
        return CollectSupport.MemberRows(inst!.Tasks, host);
    }

    private static List<object?> Ledger(ProcessTask? row) => AddSignSupport.HistoryOf(row);

    private static IDictionary<string, object?> EntryAt(List<object?> ledger, int index) =>
        Assert.IsAssignableFrom<IDictionary<string, object?>>(ledger[index]);

    // ═══ ① 发起：主办不办结＋成员行三戳＋账本含 reason＋响应 marker（java 格 1）═══

    [Fact]
    public async Task CollectSubmitKeepsHostDoingAndStampsMemberRows()
    {
        var f = await StartToReviewAsync(Chain(CollectOn, NextName("collect")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        Assert.NotNull(host);
        var resp = await CollectAsync(f.Facade, host!.TaskId!.Value, "userA",
            new List<string> { "userX", "userY" }, "涉及地规划需两部门意见");
        Assert.Equal(0, resp["code"]);
        var dc = Assert.IsAssignableFrom<IDictionary<string, object?>>(
            DataOf(resp)!["dynamicCountersign"]);
        Assert.Equal(1, Convert.ToInt32(dc["round"]));
        Assert.Equal(2, Convert.ToInt32(dc["total"]));
        Assert.Equal(0, Convert.ToInt32(dc["submitted"]));

        // 主办行仍 DOING（征询挂起＝本案与后加签的本质差异）
        var hostRow = await f.Repo.FindTaskByIdAsync(host.TaskId);
        Assert.True(hostRow!.IsDoing());

        // 成员行三戳：parent＝主办行、轮次戳、reason 戳
        var members = await MembersAsync(f.Repo, hostRow);
        Assert.Equal(2, members.Count);
        foreach (var m in members)
        {
            Assert.True(m.IsDoing());
            Assert.Equal(host.TaskId, m.ParentTaskId);
            Assert.Equal("review", m.TaskName);                     // 同节点同名（承重判据 1 的另一半）
            Assert.Equal(1, m.Variables.GetInt($"{FlowConst.AddSignRound}_review", -1));
            Assert.Equal("涉及地规划需两部门意见", m.Variables.GetStr(FlowConst.AddSignReason));
        }
        // 账本含 reason、只一条
        var ledger = Ledger(hostRow);
        var e0 = EntryAt(ledger, 0);
        Assert.Single(ledger);
        Assert.Equal("涉及地规划需两部门意见", e0["reason"]);
        Assert.Equal("userA", e0["fromActor"]);
        Assert.Equal("userA", e0["operator"]);
        Assert.Equal(1, Convert.ToInt32(e0["round"]));
        // 令牌没流出本节点
        Assert.Null(await DoingAsync(f.Repo, f.Iid, "final"));
        Assert.Equal(3, await DoingCountAsync(f.Repo, f.Iid, "review"));   // 主办＋两成员
    }

    // ═══ ② 闸门：挡全部 submitType ＋ 三面零写入（java 格 2）═══

    [Fact]
    public async Task GateBlocksAllSubmitTypesUntilCollected()
    {
        var f = await StartToReviewAsync(Chain(CollectOn, NextName("collect")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        await CollectAsync(f.Facade, host!.TaskId!.Value, "userA",
            new List<string> { "userX", "userY" }, null);
        var before = await TotalRowsAsync(f.Repo, f.Iid);

        foreach (var submitType in new[] { Agree, Reject, Return })
        {
            var resp = await ExecAsync(f.Facade, host.TaskId!.Value, "userA", submitType);
            Assert.Equal(99999999, resp["code"]);
            Assert.Equal("节点[review]动态会签意见未收齐（已提交 0/2），不能办理", resp["msg"]);
        }
        // 零写入三面：主办仍 DOING、成员行一条没办结、任务行总数不变、实例状态不变
        Assert.True((await f.Repo.FindTaskByIdAsync(host.TaskId))!.IsDoing());
        Assert.Equal(3, await DoingCountAsync(f.Repo, f.Iid, "review"));
        Assert.Equal(before, await TotalRowsAsync(f.Repo, f.Iid));
        Assert.Equal((int)WfInstanceState.Doing, (await f.Repo.FindInstanceByIdAsync(f.Iid))!.State);
        // 账本没有因为三次被拦的提交多出条目（发起那一次是唯一一条）
        Assert.Single(Ledger(await f.Repo.FindTaskByIdAsync(host.TaskId)));
    }

    // ═══ ③ 收齐后主办拍板推进＋投影翻转（java 格 3）═══

    [Fact]
    public async Task HostProceedsAfterAllSubmittedWithProjection()
    {
        var f = await StartToReviewAsync(Chain(CollectOn, NextName("collect")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        await CollectAsync(f.Facade, host!.TaskId!.Value, "userA",
            new List<string> { "userX", "userY" }, null);
        var members = await MembersAsync(f.Repo, await f.Repo.FindTaskByIdAsync(host.TaskId) ?? host);
        // 逐个提交意见（意见落各自行的 tf_approvalComment——物理隔离由"一行一条"天然保证）
        var i = 0;
        foreach (var m in members)
            await f.Engine.ExecuteProcessTaskAsync(m.TaskId!.Value, m.ActorIds[0], new FlowData
            {
                [FlowConst.SubmitType] = Agree,
                [FlowConst.ApprovalComment] = "意见" + (++i),
            });

        // 投影：submitted=2、active=false（规范 06 §2.7 ext.dynamicCountersign）
        var detail = await f.Facade.FlowAsync("processTask/detail",
            new FlowData { ["id"] = host.TaskId!.ToString() });
        Assert.Equal(0, detail["code"]);
        var ext = Assert.IsAssignableFrom<IDictionary<string, object?>>(DataOf(detail)!["ext"]);
        var dc = Assert.IsAssignableFrom<IDictionary<string, object?>>(ext["dynamicCountersign"]);
        Assert.Equal(false, dc["active"]);
        Assert.Equal(2, Convert.ToInt32(dc["total"]));
        Assert.Equal(2, Convert.ToInt32(dc["submitted"]));
        Assert.Equal(1, Convert.ToInt32(dc["round"]));
        var actors = Assert.IsAssignableFrom<IEnumerable<object?>>(dc["actors"]);
        Assert.Equal(2, actors.Count());

        // 主办拍板 → 推进（征询档推进闸：主办办结且成员清零 ⇒ 放行）
        var resp = await ExecAsync(f.Facade, host.TaskId!.Value, "userA", Agree);
        Assert.Equal(0, resp["code"]);
        Assert.NotNull(await DoingAsync(f.Repo, f.Iid, "final"));

        // 承重判据 1 的反面：final 那行的 parent 同样是主办行 id（建单不变量），
        // 若成员行识别只认 parent 就会把下游行误当成员——这里用同名过滤把它排除掉。
        var afterMembers = await MembersAsync(f.Repo, await f.Repo.FindTaskByIdAsync(host.TaskId));
        Assert.All(afterMembers, m => Assert.Equal("review", m.TaskName));
        Assert.Equal(2, afterMembers.Count);
    }

    // ═══ ④ 成员行禁拉人／重复／给自己（java 格 4）═══

    [Fact]
    public async Task MemberPullDuplicateAndSelfGuards()
    {
        var f = await StartToReviewAsync(Chain(CollectOn, NextName("collect")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        await CollectAsync(f.Facade, host!.TaskId!.Value, "userA", new List<string> { "userX" }, null);
        var before = await TotalRowsAsync(f.Repo, f.Iid);
        var member = (await MembersAsync(f.Repo, await f.Repo.FindTaskByIdAsync(host.TaskId)))[0];

        // 成员行拉人 ⇒ 逐字文案（拉人只限主办行）
        var resp = await ExecAsync(f.Facade, member.TaskId!.Value, "userX", Agree, new FlowData
        {
            [FlowConst.AddSignOperators] = "userZ",
        });
        Assert.Equal("动态会签成员任务不能发起加签", resp["msg"]);

        // 重复拉 pending 中的人
        var resp2 = await CollectAsync(f.Facade, host.TaskId!.Value, "userA",
            new List<string> { "userX" }, null);
        Assert.Equal("操作人[userX]已在节点[review]的动态会签中", resp2["msg"]);

        // 加给自己
        var resp3 = await CollectAsync(f.Facade, host.TaskId!.Value, "userA",
            new List<string> { "userA" }, null);
        Assert.Equal("不能把任务加签给自己", resp3["msg"]);

        // 三拦都零写入：行总数不变、主办仍 DOING、成员仍只有那一条
        Assert.Equal(before, await TotalRowsAsync(f.Repo, f.Iid));
        Assert.Equal(2, await DoingCountAsync(f.Repo, f.Iid, "review"));
        Assert.Equal((int)WfInstanceState.Doing, (await f.Repo.FindInstanceByIdAsync(f.Iid))!.State);
    }

    /// <summary>与 <c>tf_nextNodeOperator</c> 同传＝死参数，报后加签那枚逐字互斥文案（不另造第二种）。</summary>
    [Fact]
    public async Task CollectMutexWithNextNodeOperator()
    {
        var f = await StartToReviewAsync(Chain(CollectOn, NextName("collect")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        var before = await TotalRowsAsync(f.Repo, f.Iid);
        var args = new FlowData
        {
            [FlowConst.AddSignOperators] = new List<object?> { "userX" },
            [FlowConst.NextNodeOperator] = "userF",
        };
        var resp = await ExecAsync(f.Facade, host!.TaskId!.Value, "userA", Agree, args);
        Assert.Equal("tf_addSignOperators 与 tf_nextNodeOperator 互斥，不能同时提交", resp["msg"]);
        Assert.Equal(before, await TotalRowsAsync(f.Repo, f.Iid));
    }

    /// <summary>超轮次上限那一拦（规范 06 §2.7 同尺，两档共用 <c>addSignMaxRounds</c>）。</summary>
    [Fact]
    public async Task CollectMaxRoundsRejected()
    {
        var f = await StartToReviewAsync(
            Chain(CollectOn + ",\"addSignMaxRounds\":1", NextName("collect")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        var r1 = await CollectAsync(f.Facade, host!.TaskId!.Value, "userA", new List<string> { "userX" }, null);
        Assert.Equal(0, r1["code"]);
        var xRow = (await MembersAsync(f.Repo, await f.Repo.FindTaskByIdAsync(host.TaskId)))[0];
        await f.Engine.ExecuteProcessTaskAsync(xRow.TaskId!.Value, "userX", Submit(Agree));

        var r2 = await CollectAsync(f.Facade, host.TaskId!.Value, "userA", new List<string> { "userY" }, null);
        Assert.Equal(99999999, r2["code"]);
        Assert.Equal("节点[review]的加签轮次已达上限 1，本次提交被拒绝", r2["msg"]);
    }

    // ═══ ⑤ 撤回：未答废弃／已交保留／账本记撤回／闸门恢复（java 格 5）═══

    [Fact]
    public async Task RevokeRestoresGateAndKeepsSubmittedOpinions()
    {
        var f = await StartToReviewAsync(Chain(CollectOn, NextName("collect")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        await CollectAsync(f.Facade, host!.TaskId!.Value, "userA",
            new List<string> { "userX", "userY" }, null);
        var members = await MembersAsync(f.Repo, await f.Repo.FindTaskByIdAsync(host.TaskId));
        var xRow = members[0].ActorIds.Contains("userX") ? members[0] : members[1];
        var yRow = members[0] == xRow ? members[1] : members[0];
        await f.Engine.ExecuteProcessTaskAsync(xRow.TaskId!.Value, "userX", new FlowData
        {
            [FlowConst.SubmitType] = Agree,
            [FlowConst.ApprovalComment] = "甲部门意见",
        });

        var resp = await RevokeAsync(f.Facade, host.TaskId!.Value, "userA", "部门选错重新拉");
        Assert.Equal(0, resp["code"]);
        var data = DataOf(resp)!;
        Assert.Equal(1, Convert.ToInt32(data["revoked"]));

        // 已交保留（FINISHED＋意见不动）、未答废弃（99）
        Assert.True((await f.Repo.FindTaskByIdAsync(xRow.TaskId))!.IsFinished());
        Assert.Equal("甲部门意见",
            (await f.Repo.FindTaskByIdAsync(xRow.TaskId))!.Variables.GetStr(FlowConst.ApprovalComment));
        Assert.Equal((int)WfTaskState.Abandon, (await f.Repo.FindTaskByIdAsync(yRow.TaskId))!.TaskState);

        // 账本两条（发起＋撤回，撤回条带 revoked 标记）；轮次不消耗（两条同 round）
        var ledger = Ledger(await f.Repo.FindTaskByIdAsync(host.TaskId));
        Assert.Equal(2, ledger.Count);
        var launch = EntryAt(ledger, 0);
        var revoke = EntryAt(ledger, 1);
        Assert.Equal(true, revoke["revoked"]);
        Assert.Equal("部门选错重新拉", revoke["reason"]);
        Assert.Equal(launch["round"], revoke["round"]);

        // 闸门自然恢复（判据＝还有无 DOING 成员行，撤完即无）；已废弃的 99 行不算在册
        var go = await ExecAsync(f.Facade, host.TaskId!.Value, "userA", Agree);
        Assert.Equal(0, go["code"]);
        Assert.NotNull(await DoingAsync(f.Repo, f.Iid, "final"));

        // 再撤一次 ⇒ 逐字"没有进行中的动态会签"
        var again = await RevokeAsync(f.Facade, host.TaskId!.Value, "userA");
        Assert.Equal("节点[review]没有进行中的动态会签", again["msg"]);
    }

    /// <summary>承重判据 3：撤回的参与人判断<b>按名册</b>，不能用只认 DOING 行的 IsAllowed——
    /// 主办已办结时那一判会假拒；非名册人仍逐字拦。</summary>
    [Fact]
    public async Task RevokePermissionByRosterNotDoingRows()
    {
        var f = await StartToReviewAsync(Chain(CollectOn, NextName("collect")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        await CollectAsync(f.Facade, host!.TaskId!.Value, "userA", new List<string> { "userX" }, null);
        var before = await TotalRowsAsync(f.Repo, f.Iid);

        var denied = await RevokeAsync(f.Facade, host.TaskId!.Value, "userZ");
        Assert.Equal(99999999, denied["code"]);
        Assert.Equal("无权限撤回该动态会签", denied["msg"]);
        Assert.Equal(before, await TotalRowsAsync(f.Repo, f.Iid));

        // 特权档放行（转办/实例撤回同口径）
        var ok = await RevokeAsync(f.Facade, host.TaskId!.Value, "flow.admin");
        Assert.Equal(0, ok["code"]);
        Assert.Equal(1, Convert.ToInt32(DataOf(ok)!["revoked"]));

        // 必填两档
        var noOp = await f.Facade.FlowAsync("processTask/revokeDynamicCountersign",
            new FlowData { [FlowConst.ProcessTaskIdKey] = host.TaskId!.ToString() });
        Assert.Equal("operator 必填", noOp["msg"]);
        var noId = await f.Facade.FlowAsync("processTask/revokeDynamicCountersign",
            new FlowData { ["operator"] = "userA" });
        Assert.Equal("processTaskId 缺失", noId["msg"]);
    }

    // ═══ ⑥ 多轮征询：跨轮计数（java 格 6）═══

    [Fact]
    public async Task MultiRoundCollectCountsAcrossRounds()
    {
        var f = await StartToReviewAsync(Chain(CollectOn, NextName("collect")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        var r1 = await CollectAsync(f.Facade, host!.TaskId!.Value, "userA", new List<string> { "userX" }, null);
        Assert.Equal(1, Convert.ToInt32(Assert.IsAssignableFrom<IDictionary<string, object?>>(
            DataOf(r1)!["dynamicCountersign"])["round"]));
        var xRow = (await MembersAsync(f.Repo, await f.Repo.FindTaskByIdAsync(host.TaskId)))[0];
        await f.Engine.ExecuteProcessTaskAsync(xRow.TaskId!.Value, "userX", Submit(Agree));

        // 闸门已开时仍可再拉一轮（round+1、闸门重闭）
        var r2 = await CollectAsync(f.Facade, host.TaskId!.Value, "userA", new List<string> { "userY" }, null);
        Assert.Equal(2, Convert.ToInt32(Assert.IsAssignableFrom<IDictionary<string, object?>>(
            DataOf(r2)!["dynamicCountersign"])["round"]));

        // 跨轮收齐判据：x 已交、y 未交 ⇒ 1/2
        var blocked = await ExecAsync(f.Facade, host.TaskId!.Value, "userA", Agree);
        Assert.Equal("节点[review]动态会签意见未收齐（已提交 1/2），不能办理", blocked["msg"]);

        var members = await MembersAsync(f.Repo, await f.Repo.FindTaskByIdAsync(host.TaskId));
        Assert.Equal(2, members.Count);
        foreach (var m in members.Where(m => m.IsDoing()))   // 第 1 轮已提交的行不再重复办结
            await f.Engine.ExecuteProcessTaskAsync(m.TaskId!.Value, m.ActorIds[0], Submit(Agree));

        var go = await ExecAsync(f.Facade, host.TaskId!.Value, "userA", Agree);
        Assert.Equal(0, go["code"]);
        Assert.NotNull(await DoingAsync(f.Repo, f.Iid, "final"));
        // 跨轮意见互不覆盖：两条账本、两轮 round
        var ledger = Ledger(await f.Repo.FindTaskByIdAsync(host.TaskId));
        Assert.Equal(2, ledger.Count);
        Assert.Equal(1, Convert.ToInt32(EntryAt(ledger, 0)["round"]));
        Assert.Equal(2, Convert.ToInt32(EntryAt(ledger, 1)["round"]));
    }

    // ═══ ⑦ 三条解析期硬校验（java 格 7）═══

    [Fact]
    public void ParseBansMutexCountersignAndApplyNodes()
    {
        var (_, _, ctx) = TestInfra.NewEngineWithCtx();
        ParseFails(ctx, Chain(CollectOn + ",\"allowAddSign\":true", "m"),
            "不允许同时配置 allowAddSign 与 dynamicCountersign");

        var countersign = Chain(CollectOn + ",\"countersignType\":\"PARALLEL\"", "c")
            .Replace("\"form\":\"form-r\",\"taskType\":0,\"performType\":0",
                     "\"form\":\"form-r\",\"taskType\":0,\"performType\":1");
        ParseFails(ctx, countersign, "会签节点[review]不允许配置 dynamicCountersign");

        var applyOn = Chain(CollectOn, "a").Replace(ApplyNode, ApplyNode + "," + CollectOn);
        ParseFails(ctx, applyOn, "申请节点[apply]不允许配置 dynamicCountersign");
    }

    private static void ParseFails(ServiceContext ctx, string flowJson, string expectedFragment)
    {
        var ex = Assert.Throws<JeeflowException>(() =>
            ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(flowJson), ctx));
        Assert.Contains(expectedFragment, ex.Message);
    }

    // ═══ ⑧ 逐字文案自证（java 格 8）═══

    [Fact]
    public void PinnedMessagesMatchSpec()
    {
        Assert.Equal("节点[{0}]动态会签意见未收齐（已提交 {1}/{2}），不能办理", CollectSupport.MsgPending);
        Assert.Equal("节点[{0}]没有进行中的动态会签", CollectSupport.MsgNoPending);
        Assert.Equal("无权限撤回该动态会签", CollectSupport.MsgNoPermissionRevoke);
        Assert.Equal("动态会签成员任务不能发起加签", CollectSupport.MsgMemberPull);
        Assert.Equal("操作人[{0}]已在节点[{1}]的动态会签中", CollectSupport.MsgDuplicate);
    }

    // ═══ ⑨ 废弃腿扩双开关（本栈补的一格；规范 04「语义边界」末行）═══

    /// <summary>成员行按 submitType=6 退回发起人 ⇒ <b>离开本节点</b>，废弃腿的条件从
    /// "只开 allowAddSign" 扩到"allowAddSign 或 dynamicCountersign"：主办行与未答成员行一并废弃，
    /// 不留孤儿待办（修前那一判会把征询档整个跳过，实例停在申请节点而 review 上挂着两条 DOING）。</summary>
    [Fact]
    public async Task LeavingNodeAbandonsPendingMembersViaDoubleSwitch()
    {
        var f = await StartToReviewAsync(Chain(CollectOn, NextName("collect")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        await CollectAsync(f.Facade, host!.TaskId!.Value, "userA",
            new List<string> { "userX", "userY" }, null);
        var members = await MembersAsync(f.Repo, await f.Repo.FindTaskByIdAsync(host.TaskId));

        // 成员行提交退回发起人：闸门对成员行只禁"带拉人参数"，退回档照普通办理放行
        var resp = await ExecAsync(f.Facade, members[0].TaskId!.Value, members[0].ActorIds[0], Return);
        Assert.Equal(0, resp["code"]);

        Assert.NotNull(await DoingAsync(f.Repo, f.Iid, "apply"));          // 申请待办生成
        Assert.Equal(0, await DoingCountAsync(f.Repo, f.Iid, "review"));   // 本节点全部清场
        var abandoned = (await f.Repo.FindHistoryTasksAsync(f.Iid))
            .Count(t => t.TaskName == "review" && t.TaskState == (int)WfTaskState.Abandon);
        Assert.Equal(2, abandoned);                                        // 主办＋未答那一位
        Assert.Null(await DoingAsync(f.Repo, f.Iid, "final"));
    }

    // ═══ ⑩ 回归：不开开关的节点行为逐字不变 ═══

    /// <summary>普通节点带 <c>tf_addSignOperators</c>（开关都没开）⇒ 走<b>后加签那一档的既有报错</b>，
    /// 分流腿不认领它（回归面：新增的两道判据不得改变没配开关的节点的行为）。</summary>
    [Fact]
    public async Task PlainNodeNotClaimedByCollectLeg()
    {
        var f = await StartToReviewAsync(Chain("", NextName("collect")));
        var review = await DoingAsync(f.Repo, f.Iid, "review");
        var before = await TotalRowsAsync(f.Repo, f.Iid);
        var resp = await CollectAsync(f.Facade, review!.TaskId!.Value, "userA",
            new List<string> { "userX" }, null);
        Assert.Equal("节点[review]未开启 allowAddSign，不接受 tf_addSignOperators", resp["msg"]);
        Assert.True((await f.Repo.FindTaskByIdAsync(review.TaskId))!.IsDoing());
        Assert.Equal(before, await TotalRowsAsync(f.Repo, f.Iid));
    }
}

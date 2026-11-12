using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// 「动态协办（aidant）」验收（issues/253 · C# 腿）——移植基准＝jeeflow-java
/// <c>jeeflow-core/src/test/java/com/mldong/jeeflow/test/AidantTest.java</c>（逐格语义对齐）。
///
/// <para>钉住的面（规范 04「动态协办」／规范 06 §2.7＋§processTask/aidant）：派生形状
/// （task_type=1／parent＝主办行／同节点同名／performType=NORMAL／expire_time 同尺／委托并入／码 3 既有腿）、
/// 推进闸四入口（协办办结不驱动）、等待闸两档（true 逐字文案＋零写入／缺省 false 随时推进＋离开节点清场记账）、
/// 两跳账本存活、detail 投影 ext.aidant、协办行能力边界（可加/摘人、可被转办、禁递归）、
/// 负向六文案＋越权＋非 DOING、回归（会签票齐即推进／与征询并存两闸各数／撤回清场／
/// transfer·surrogate·addCandidate 不变）。</para>
///
/// <para>夹具内联 JSON（照 <see cref="Recall246Tests"/>／<see cref="DynamicCountersign247Tests"/> 纪律），
/// <b>不碰共享 flows/ 目录</b>。既有测试期望值一字未改（permission 表条数属加法期望，另见 UnitTests）。</para>
/// </summary>
public class Aidant253Tests
{
    private const int Agree = (int)WfSubmitType.Agree;                 // 1
    private const int Reject = (int)WfSubmitType.Reject;               // 2
    private const int Apply = (int)WfSubmitType.Apply;                 // 0
    private const int Jump = (int)WfSubmitType.Jump;                   // 4
    private const int Return = (int)WfSubmitType.RollbackToOperator;   // 6

    private static FlowData Submit(int type) => new() { [FlowConst.SubmitType] = type };

    /// <summary>事件码录制器（码 3/5 既有腿取证；不新增事件码）。</summary>
    private sealed class CodeRecorder : IProcessEventListener
    {
        public List<int> Codes { get; } = new();
        public Task OnEventAsync(ProcessEvent @event)
        {
            Codes.Add((int)@event.EventType);
            return Task.CompletedTask;
        }
    }

    // ═══ 夹具（逐字照 java AidantTest 的 chain / countersignChain）═══

    private static string Node(string id, string props, string text) =>
        "{\"id\":\"" + id + "\",\"type\":\"snaker:task\",\"x\":0,\"y\":0,"
        + "\"properties\":{" + props + "},\"text\":{\"value\":\"" + text + "\"}}";

    private static string Edge(string id, string from, string to) =>
        "{\"id\":\"" + id + "\",\"type\":\"snaker:transition\",\"sourceNodeId\":\"" + from
        + "\",\"targetNodeId\":\"" + to + "\",\"properties\":{}}";

    private const string ApplyNode =
        "\"form\":\"apply-form\",\"taskType\":0,\"performType\":0,\"assignee\":\"applicant\"";

    private const string EndNode =
        "{\"id\":\"end\",\"type\":\"snaker:end\",\"x\":0,\"y\":0,\"properties\":{},\"text\":{\"value\":\"结束\"}}";

    private static string Head(string name, string displayName) =>
        "{\"name\":\"" + name + "\",\"displayName\":\"" + displayName + "\",\"type\":\"approval\",\"nodes\":["
        + "{\"id\":\"start\",\"type\":\"snaker:start\",\"x\":0,\"y\":0,\"properties\":{},\"text\":{\"value\":\"开始\"}},";

    /// <summary>五节点链 start→apply→review→final→end（java chain）。</summary>
    private static string Chain(string reviewProps, string name)
    {
        var r = reviewProps.Length == 0 ? "\"assignee\":\"userA\"" : "\"assignee\":\"userA\"," + reviewProps;
        return Head(name, "动态协办夹具")
            + Node("apply", ApplyNode, "发起申请") + ","
            + Node("review", "\"form\":\"form-r\",\"taskType\":0,\"performType\":0," + r, "审批") + ","
            + Node("final", "\"form\":\"form-f\",\"assignee\":\"userF\",\"taskType\":0,\"performType\":0", "终审") + ","
            + EndNode
            + "],\"edges\":["
            + Edge("e1", "start", "apply") + "," + Edge("e2", "apply", "review") + ","
            + Edge("e3", "review", "final") + "," + Edge("e4", "final", "end")
            + "]}";
    }

    /// <summary>并行会签链 start→apply→cs(userA,userB)→end（java countersignChain）。</summary>
    private static string CountersignChain(string name) =>
        Head(name, "协办×会签夹具")
        + Node("apply", ApplyNode, "发起申请") + ","
        + Node("cs", "\"form\":\"form-c\",\"taskType\":0,\"performType\":1,"
            + "\"assignee\":\"userA,userB\",\"countersignType\":\"PARALLEL\"", "会签审批") + ","
        + EndNode
        + "],\"edges\":["
        + Edge("e1", "start", "apply") + "," + Edge("e2", "apply", "cs") + ","
        + Edge("e3", "cs", "end")
        + "]}";

    private int _seq;
    private string NextName(string prefix) => $"{prefix}-{++_seq}";

    private sealed record Stack(JeeflowEngine Engine, MemoryRepository Repo, ServiceContext Ctx,
                                JeeflowFacade Facade, long Iid, CodeRecorder Events);

    private static string FlowName(string flowJson)
    {
        var i = flowJson.IndexOf("\"name\":\"", StringComparison.Ordinal);
        var s = flowJson[(i + 8)..];
        return s[..s.IndexOf('"', StringComparison.Ordinal)];
    }

    /// <summary>发起并办结申请节点，令牌停在 review（java startToReview）。</summary>
    private async Task<Stack> StartToReviewAsync(string flowJson)
    {
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        var recorder = new CodeRecorder();
        ctx.RegisterEventListener(recorder);
        var defineId = await TestInfra.SaveFlowDefineAsync(repo, FlowName(flowJson), flowJson);
        var inst = await engine.StartProcessInstanceByIdAsync(defineId, "applicant", new FlowData());
        foreach (var t in await repo.FindDoingTasksAsync(inst.InstanceId!.Value, null))
            await engine.ExecuteProcessTaskAsync(t.TaskId!.Value, "applicant", Submit(Apply));
        return new Stack(engine, repo, ctx, new JeeflowFacade(ctx), inst.InstanceId!.Value, recorder);
    }

    /// <summary>会签链装配（带事件捕获）。</summary>
    private async Task<Stack> StartCountersignAsync(string flowJson)
    {
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        var recorder = new CodeRecorder();
        ctx.RegisterEventListener(recorder);
        var defineId = await TestInfra.SaveFlowDefineAsync(repo, FlowName(flowJson), flowJson);
        var inst = await engine.StartProcessInstanceByIdAsync(defineId, "applicant", new FlowData());
        foreach (var t in await repo.FindDoingTasksAsync(inst.InstanceId!.Value, null))
            await engine.ExecuteProcessTaskAsync(t.TaskId!.Value, "applicant", Submit(Apply));
        return new Stack(engine, repo, ctx, new JeeflowFacade(ctx), inst.InstanceId!.Value, recorder);
    }

    // ═══ 驱动助手 ═══

    private static Task<Dictionary<string, object?>> AidantAsync(JeeflowFacade facade, long hostTaskId,
        string? op, object? actorIds, string? reason = null, object? wait = null)
    {
        var a = new FlowData { [FlowConst.ProcessTaskIdKey] = hostTaskId.ToString() };
        if (op != null) a["operator"] = op;
        if (actorIds != null) a[FlowConst.ActorIdsKey] = actorIds;
        if (reason != null) a["reason"] = reason;
        if (wait != null) a["waitCompletion"] = wait;
        return facade.FlowAsync("processTask/aidant", a);
    }

    private static Task<Dictionary<string, object?>> ExecAsync(JeeflowFacade facade, long taskId,
        string op, int submitType, FlowData? extra = null)
    {
        var a = new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = taskId.ToString(),
            ["operator"] = op,
            [FlowConst.SubmitType] = submitType,
        };
        if (extra != null) foreach (var kv in extra) a[kv.Key] = kv.Value;
        return facade.FlowAsync("processTask/execute", a);
    }

    private static Task<Dictionary<string, object?>> DetailAsync(JeeflowFacade facade, long taskId) =>
        facade.FlowAsync("processTask/detail", new FlowData { ["id"] = taskId.ToString(), ["operator"] = "userA" });

    private static Dictionary<string, object?>? DataOf(Dictionary<string, object?> resp) =>
        resp.TryGetValue("data", out var d) ? d as Dictionary<string, object?> : null;

    private static async Task<ProcessTask?> DoingAsync(MemoryRepository repo, long iid, string nodeName) =>
        (await repo.FindDoingTasksAsync(iid, null)).FirstOrDefault(t => t.TaskName == nodeName);

    private static async Task<int> DoingCountAsync(MemoryRepository repo, long iid, string nodeName) =>
        (await repo.FindDoingTasksAsync(iid, null)).Count(t => t.TaskName == nodeName);

    private static async Task<List<ProcessTask>> AidantRowsAsync(MemoryRepository repo, ProcessTask host)
    {
        var inst = await repo.FindInstanceByIdAsync(host.ProcessInstanceId!.Value);
        return AidantSupport.AidantRows(inst!.Tasks, host);
    }

    private static List<object?> Ledger(ProcessTask? row) => AidantSupport.HistoryOf(row);

    private static IDictionary<string, object?> EntryAt(List<object?> ledger, int index) =>
        Assert.IsAssignableFrom<IDictionary<string, object?>>(ledger[index]);

    private static void AssertMsg(Dictionary<string, object?> resp, string msg)
    {
        Assert.Equal(99999999, resp["code"]);
        Assert.Equal(msg, resp["msg"]);
    }

    private static List<object?> ActorsOf(object? actorIds) =>
        actorIds is List<object?> list ? list : new List<object?> { actorIds };

    // ═══ 正向：派生形状 ═══

    [Fact]
    public async Task DeriveShapesSecondaryRowsAndKeepsHostDoing()
    {
        var f = await StartToReviewAsync(Chain("", NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        Assert.NotNull(host);
        f.Events.Codes.Clear();

        var resp = await AidantAsync(f.Facade, host!.TaskId!.Value, "userA",
            new List<object?> { "userX", "userY" }, "需两部门协办");
        Assert.Equal(0, resp["code"]);
        Assert.Null(resp["data"]);   // 成功 data → null（规范 06）

        // 主办行原样保留 DOING，名册不动，actor_id 列恒无值
        var hostRow = await f.Repo.FindTaskByIdAsync(host.TaskId);
        Assert.True(hostRow!.IsDoing());
        Assert.Equal(new[] { "userA" }, hostRow.ActorIds);
        Assert.Null(hostRow.ActorId);   // 严禁覆写任务 actor_id 列

        var rows = await AidantRowsAsync(f.Repo, hostRow);
        Assert.Equal(2, rows.Count);   // 多人每人一行
        var actors = new List<string>();
        foreach (var r in rows)
        {
            Assert.True(r.IsDoing());
            Assert.Equal(WfTaskType.Secondary, r.TaskType);   // task_type=1
            Assert.Equal(host.TaskId, r.ParentTaskId);
            Assert.Equal(host.TaskName, r.TaskName);          // 同节点
            Assert.Equal(host.DisplayName, r.DisplayName);    // 同名
            Assert.Equal(WfPerformType.Normal, r.PerformType);
            Assert.Equal("需两部门协办", r.Variables.GetStr(FlowConst.AidantReason));
            Assert.Null(r.Variables.GetObj(FlowConst.ApprovalComment));   // 不占 tf_approvalComment
            Assert.Null(r.ActorId);   // 进行中任务 actor_id 列恒无值
            actors.Add(r.ActorIds[0]);
            Assert.Single(await f.Repo.FindTaskActorsAsync(r.TaskId!.Value));   // 参与者表真落库
        }
        Assert.Equal(new[] { "userX", "userY" }, actors);

        // 账本：一条、六键锁定键序、time 为 §2.4 字符串
        var lg = Ledger(await f.Repo.FindTaskByIdAsync(host.TaskId));
        Assert.Single(lg);
        var e0 = EntryAt(lg, 0);
        Assert.Equal(new[] { "round", "fromActor", "toActors", "reason", "time", "operator" }, e0.Keys);
        Assert.Equal(1, Convert.ToInt32(e0["round"]));
        Assert.Equal("userA", e0["fromActor"]);
        Assert.Equal("需两部门协办", e0["reason"]);
        Assert.Equal("userA", e0["operator"]);
        Assert.Equal(new List<string?> { "userX", "userY" }, (List<string?>)e0["toActors"]!);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", e0["time"]?.ToString());

        // 事件：只走既有码 3（逐协办行一次），不新增事件码
        Assert.Equal(new[] { 3, 3 }, f.Events.Codes);
    }

    [Fact]
    public async Task ReasonOptionalFallsBackToEmptyString()
    {
        var f = await StartToReviewAsync(Chain("", NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        Assert.Equal(0, (await AidantAsync(f.Facade, host!.TaskId!.Value, "userA", "userX"))["code"]);
        Assert.Equal("", EntryAt(Ledger(await f.Repo.FindTaskByIdAsync(host.TaskId)), 0)["reason"]);
        var rows = await AidantRowsAsync(f.Repo, await f.Repo.FindTaskByIdAsync(host.TaskId) ?? host);
        Assert.Null(rows[0].Variables.GetObj(FlowConst.AidantReason));
        Assert.Null((await f.Repo.FindTaskByIdAsync(host.TaskId))!.Variables.GetObj(FlowConst.ApprovalComment));
    }

    [Fact]
    public async Task ActorIdsAcceptsCommaStringAndArray()
    {
        var f = await StartToReviewAsync(Chain("", NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        Assert.Equal(0, (await AidantAsync(f.Facade, host!.TaskId!.Value, "userA", " userX , userY ,"))["code"]);
        Assert.Equal(2, (await AidantRowsAsync(f.Repo, host)).Count);
    }

    [Fact]
    public async Task ExpireTimeRecomputedAndSurrogateMerged()
    {
        // 带扩展仓储的装配（委托并入需 ExtRepository）；固定钟＝TestInfra 缺省 2026-08-01 09:00:00
        var repo = new MemoryRepository();
        var ext = new MemoryExtRepository(repo, null);
        var ctx = new ServiceContext(repo, ext);
        ctx.Clock = new FixedClock(new DateTime(2026, 8, 1, 9, 0, 0));
        ctx.IdGenerator = new AtomicIdGenerator(1, ctx.Clock);
        ctx.UserProvider = new TestUserProvider();
        TestInfra.RegisterBuiltins(ctx);
        repo.Configure(ctx);
        ext.Configure(ctx);
        var engine = new JeeflowEngine(ctx);
        var facade = new JeeflowFacade(ctx);

        await ext.SaveSurrogateAsync(new ProcessSurrogate
        {
            Id = 1,
            ProcessName = "",
            Operator = "userX",
            Surrogate = "userS",
            StartTime = new DateTime(2026, 7, 1),
            EndTime = new DateTime(2026, 9, 1),
            Enabled = 1,
            CreateUser = "tester",
        });

        var flowJson = Chain("\"expireTime\":\"2d\"", NextName("aidant"));
        var defineId = await TestInfra.SaveFlowDefineAsync(repo, FlowName(flowJson), flowJson);
        var inst = await engine.StartProcessInstanceByIdAsync(defineId, "applicant", new FlowData());
        foreach (var t in await repo.FindDoingTasksAsync(inst.InstanceId!.Value, null))
            await engine.ExecuteProcessTaskAsync(t.TaskId!.Value, "applicant", Submit(Apply));
        var host = await DoingAsync(repo, inst.InstanceId!.Value, "review");

        Assert.Equal(0, (await AidantAsync(facade, host!.TaskId!.Value, "userA", "userX"))["code"]);
        var child = (await AidantRowsAsync(repo, host))[0];
        Assert.Equal(new DateTime(2026, 8, 3, 9, 0, 0), child.ExpireTime);   // expire_time 按节点表达式同尺重算
        Assert.Contains("userX", child.ActorIds);   // 委托并入不摘原人
        Assert.Contains("userS", child.ActorIds);   // 委托照既有腿并入
        Assert.Equal(2, (await repo.FindTaskActorsAsync(child.TaskId!.Value)).Count);   // 落库参与者表也真带上
    }

    // ═══ 正向：推进闸 ═══

    [Fact]
    public async Task AidantCompletionDoesNotDrive()
    {
        var f = await StartToReviewAsync(Chain("", NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        await AidantAsync(f.Facade, host!.TaskId!.Value, "userA", "userX");
        var child = (await AidantRowsAsync(f.Repo, host))[0];
        f.Events.Codes.Clear();

        var created = await f.Engine.ExecuteProcessTaskAsync(child.TaskId!.Value, "userX", Submit(Agree));
        Assert.Empty(created);   // 协办办结不产生执行对象（无新任务）
        Assert.True((await f.Repo.FindTaskByIdAsync(child.TaskId))!.IsFinished());
        Assert.True((await f.Repo.FindTaskByIdAsync(host.TaskId))!.IsDoing());   // 主办行保留 DOING
        Assert.Null(await DoingAsync(f.Repo, f.Iid, "final"));   // 不跑输出变迁：下游节点无待办
        Assert.Equal(10, (await f.Repo.FindInstanceByIdAsync(f.Iid))!.State);
        Assert.Equal(new[] { 5 }, f.Events.Codes);   // 只走既有码 5，不新增事件码
    }

    [Fact]
    public async Task AidantJumpPathAlsoGated()
    {
        var f = await StartToReviewAsync(Chain("", NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        await AidantAsync(f.Facade, host!.TaskId!.Value, "userA", new List<object?> { "userX", "userY" });
        var rows = await AidantRowsAsync(f.Repo, host);

        // JUMP 路（门面 submitType=4）
        var resp = await ExecAsync(f.Facade, rows[0].TaskId!.Value, "userX", Jump,
            new FlowData { [FlowConst.TaskName] = "final" });
        Assert.Equal(0, resp["code"]);
        Assert.True((await f.Repo.FindTaskByIdAsync(rows[0].TaskId))!.IsFinished());
        Assert.Null(await DoingAsync(f.Repo, f.Iid, "final"));   // 跳转路同闸：不得凭空建出 final 待办

        // ROLLBACK 路（引擎直调，nodeName=null）
        var created = await f.Engine.ExecuteAndJumpTaskAsync(rows[1].TaskId!.Value, "userY",
            Submit((int)WfSubmitType.Rollback), null);
        Assert.Empty(created);
        Assert.True((await f.Repo.FindTaskByIdAsync(rows[1].TaskId))!.IsFinished());
        Assert.True((await f.Repo.FindTaskByIdAsync(host.TaskId))!.IsDoing());   // 主办行仍在
        Assert.Equal(1, await DoingCountAsync(f.Repo, f.Iid, "review"));
    }

    [Fact]
    public async Task AidantRejectAndReturnDoNotDrive()
    {
        var f = await StartToReviewAsync(Chain("", NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        await AidantAsync(f.Facade, host!.TaskId!.Value, "userA", new List<object?> { "userX", "userY" });
        var rows = await AidantRowsAsync(f.Repo, host);

        Assert.Equal(0, (await ExecAsync(f.Facade, rows[0].TaskId!.Value, "userX", Reject))["code"]);
        Assert.Equal(10, (await f.Repo.FindInstanceByIdAsync(f.Iid))!.State);   // 拒绝不得结束整单
        Assert.Equal(0, (await ExecAsync(f.Facade, rows[1].TaskId!.Value, "userY", Return))["code"]);
        Assert.Equal(10, (await f.Repo.FindInstanceByIdAsync(f.Iid))!.State);
        Assert.True((await f.Repo.FindTaskByIdAsync(host.TaskId))!.IsDoing());
        Assert.Null(await DoingAsync(f.Repo, f.Iid, "apply"));
    }

    [Fact]
    public async Task HostCompletionAfterAidantDrives()
    {
        var f = await StartToReviewAsync(Chain("", NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        await AidantAsync(f.Facade, host!.TaskId!.Value, "userA", "userX");
        var child = (await AidantRowsAsync(f.Repo, host))[0];
        await ExecAsync(f.Facade, child.TaskId!.Value, "userX", Agree);
        Assert.Equal(0, (await ExecAsync(f.Facade, host.TaskId!.Value, "userA", Agree))["code"]);
        Assert.NotNull(await DoingAsync(f.Repo, f.Iid, "final"));   // 主办办结才推进
        Assert.True((await f.Repo.FindTaskByIdAsync(host.TaskId))!.IsFinished());
    }

    // ═══ 正向／负向：等待闸两档 ═══

    [Fact]
    public async Task WaitCompletionTrueBlocksHostUntilAidantDone()
    {
        var f = await StartToReviewAsync(Chain("", NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        Assert.Equal(0, (await AidantAsync(f.Facade, host!.TaskId!.Value, "userA",
            new List<object?> { "userX", "userY" }, null, true))["code"]);
        var rows = await AidantRowsAsync(f.Repo, host);
        var ledgerBefore = Ledger(await f.Repo.FindTaskByIdAsync(host.TaskId)).Count;

        AssertMsg(await ExecAsync(f.Facade, host.TaskId!.Value, "userA", Agree),
            "节点[review]协办未办结（已办结 0/2），不能办理");
        // 零写入：主办仍 DOING、无下游待办、账本不增、协办行不动
        Assert.True((await f.Repo.FindTaskByIdAsync(host.TaskId))!.IsDoing());
        Assert.Null(await DoingAsync(f.Repo, f.Iid, "final"));
        Assert.Equal(ledgerBefore, Ledger(await f.Repo.FindTaskByIdAsync(host.TaskId)).Count);
        Assert.Equal(2, (await AidantRowsAsync(f.Repo, host)).Count);
        foreach (var r in await AidantRowsAsync(f.Repo, host)) Assert.True(r.IsDoing());

        // 办结一条 ⇒ 计数 1/2，仍拦
        await ExecAsync(f.Facade, rows[0].TaskId!.Value, "userX", Agree);
        AssertMsg(await ExecAsync(f.Facade, host.TaskId!.Value, "userA", Agree),
            "节点[review]协办未办结（已办结 1/2），不能办理");

        // 全办结 ⇒ 放行
        await ExecAsync(f.Facade, rows[1].TaskId!.Value, "userY", Agree);
        Assert.Equal(0, (await ExecAsync(f.Facade, host.TaskId!.Value, "userA", Agree))["code"]);
        Assert.NotNull(await DoingAsync(f.Repo, f.Iid, "final"));
    }

    [Fact]
    public async Task WaitGateBlocksEverySubmitType()
    {
        var f = await StartToReviewAsync(Chain("", NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        await AidantAsync(f.Facade, host!.TaskId!.Value, "userA", "userX", null, true);
        foreach (var st in new[] { Agree, Reject, Return })
        {
            AssertMsg(await ExecAsync(f.Facade, host.TaskId!.Value, "userA", st),
                "节点[review]协办未办结（已办结 0/1），不能办理");
            Assert.True((await f.Repo.FindTaskByIdAsync(host.TaskId))!.IsDoing());
        }
        Assert.Equal(10, (await f.Repo.FindInstanceByIdAsync(f.Iid))!.State);
    }

    [Fact]
    public async Task WaitDefaultFalseCleansPendingAidantOnLeave()
    {
        var f = await StartToReviewAsync(Chain("", NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        await AidantAsync(f.Facade, host!.TaskId!.Value, "userA", new List<object?> { "userX", "userY" });
        var rows = await AidantRowsAsync(f.Repo, host);
        await ExecAsync(f.Facade, rows[0].TaskId!.Value, "userX", Agree);   // 只办结一条

        Assert.Equal(0, (await ExecAsync(f.Facade, host.TaskId!.Value, "userA", Agree))["code"]);   // 缺省 false 主办可随时推进
        Assert.NotNull(await DoingAsync(f.Repo, f.Iid, "final"));
        Assert.Equal(99, (await f.Repo.FindTaskByIdAsync(rows[1].TaskId))!.TaskState);   // 未办结协办行清场为废弃(99)
        Assert.Equal(20, (await f.Repo.FindTaskByIdAsync(rows[0].TaskId))!.TaskState);   // 已办结那条不改写
        // 账本记废弃：末条带 abandoned 标记、toActors＝被废弃的人
        var lg = Ledger(await f.Repo.FindTaskByIdAsync(host.TaskId));
        Assert.Equal(2, lg.Count);
        var last = EntryAt(lg, 1);
        Assert.Equal(true, last["abandoned"]);
        Assert.Equal(new List<string?> { "userY" }, (List<string?>)last["toActors"]!);
        Assert.Equal(1, Convert.ToInt32(last["round"]));   // 清场不消耗轮次
        Assert.Equal(0, await DoingCountAsync(f.Repo, f.Iid, "review"));   // 不留孤儿待办
    }

    [Fact]
    public async Task WaitCompletionAcceptsStringForm()
    {
        var f = await StartToReviewAsync(Chain("", NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        Assert.Equal(0, (await AidantAsync(f.Facade, host!.TaskId!.Value, "userA", "userX", null, "true"))["code"]);
        AssertMsg(await ExecAsync(f.Facade, host.TaskId!.Value, "userA", Agree),
            "节点[review]协办未办结（已办结 0/1），不能办理");
    }

    [Fact]
    public async Task RejectAndReturnToOperatorCleanAidant()
    {
        var f1 = await StartToReviewAsync(Chain("", NextName("aidant")));
        var h1 = await DoingAsync(f1.Repo, f1.Iid, "review");
        await AidantAsync(f1.Facade, h1!.TaskId!.Value, "userA", "userX");
        var c1 = (await AidantRowsAsync(f1.Repo, h1))[0];
        Assert.Equal(0, (await ExecAsync(f1.Facade, h1.TaskId!.Value, "userA", Reject))["code"]);
        Assert.Equal(99, (await f1.Repo.FindTaskByIdAsync(c1.TaskId))!.TaskState);
        Assert.Equal(true, EntryAt(Ledger(await f1.Repo.FindTaskByIdAsync(h1.TaskId)), 1)["abandoned"]);

        var f2 = await StartToReviewAsync(Chain("", NextName("aidant")));
        var h2 = await DoingAsync(f2.Repo, f2.Iid, "review");
        await AidantAsync(f2.Facade, h2!.TaskId!.Value, "userA", "userX");
        var c2 = (await AidantRowsAsync(f2.Repo, h2))[0];
        Assert.Equal(0, (await ExecAsync(f2.Facade, h2.TaskId!.Value, "userA", Return))["code"]);
        Assert.Equal(99, (await f2.Repo.FindTaskByIdAsync(c2.TaskId))!.TaskState);
        Assert.NotNull(await DoingAsync(f2.Repo, f2.Iid, "apply"));   // 退回发起人后申请节点复活
    }

    // ═══ 正向：账本与投影 ═══

    [Fact]
    public async Task TwoRoundsLedgerSurvivesHostFinish()
    {
        var f = await StartToReviewAsync(Chain("", NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        await AidantAsync(f.Facade, host!.TaskId!.Value, "userA", "userX", "第一轮", false);
        var first = (await AidantRowsAsync(f.Repo, host))[0];
        await ExecAsync(f.Facade, first.TaskId!.Value, "userX", Agree);
        await AidantAsync(f.Facade, host.TaskId!.Value, "userA", "userY", "第二轮", false);

        var lg = Ledger(await f.Repo.FindTaskByIdAsync(host.TaskId));
        Assert.Equal(2, lg.Count);   // 两跳账本＝2
        Assert.Equal(1, Convert.ToInt32(EntryAt(lg, 0)["round"]));
        Assert.Equal(2, Convert.ToInt32(EntryAt(lg, 1)["round"]));
        Assert.Equal("第一轮", EntryAt(lg, 0)["reason"]);
        Assert.Equal("第二轮", EntryAt(lg, 1)["reason"]);
        Assert.Equal(new List<string?> { "userX" }, (List<string?>)EntryAt(lg, 0)["toActors"]!);
        Assert.Equal(new List<string?> { "userY" }, (List<string?>)EntryAt(lg, 1)["toActors"]!);

        var second = (await AidantRowsAsync(f.Repo, host))[1];
        await ExecAsync(f.Facade, second.TaskId!.Value, "userY", Agree);
        Assert.Equal(0, (await ExecAsync(f.Facade, host.TaskId!.Value, "userA", Agree))["code"]);
        Assert.True((await f.Repo.FindTaskByIdAsync(host.TaskId))!.IsFinished());
        Assert.Equal(2, Ledger(await f.Repo.FindTaskByIdAsync(host.TaskId)).Count);   // 办结后账本仍在
    }

    [Fact]
    public async Task DetailProjectionShape()
    {
        var f = await StartToReviewAsync(Chain("", NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        await AidantAsync(f.Facade, host!.TaskId!.Value, "userA",
            new List<object?> { "userX", "userY" }, "协办原因", true);
        var rows = await AidantRowsAsync(f.Repo, host);

        var detail = await DetailAsync(f.Facade, host.TaskId!.Value);
        Assert.Equal(0, detail["code"]);
        var ext = Assert.IsAssignableFrom<IDictionary<string, object?>>(DataOf(detail)!["ext"]);
        var p = Assert.IsAssignableFrom<IDictionary<string, object?>>(ext["aidant"]);
        Assert.Equal(new[] { "active", "round", "total", "submitted", "actors", "wait" }, p.Keys);
        Assert.Equal(true, p["active"]);
        Assert.Equal(1, Convert.ToInt32(p["round"]));
        Assert.Equal(2, Convert.ToInt32(p["total"]));
        Assert.Equal(0, Convert.ToInt32(p["submitted"]));
        Assert.Equal(true, p["wait"]);
        var actors = Assert.IsAssignableFrom<List<object?>>(p["actors"]);
        Assert.Equal(2, actors.Count);
        var a0 = Assert.IsAssignableFrom<IDictionary<string, object?>>(actors[0]);
        Assert.Equal(new[] { "actor", "taskId", "state" }, a0.Keys);
        Assert.Equal("userX", a0["actor"]);
        Assert.Equal(rows[0].TaskId, Convert.ToInt64(a0["taskId"]));
        Assert.Equal(10, Convert.ToInt32(a0["state"]));

        // 办结一条 ⇒ submitted 现算推进
        await ExecAsync(f.Facade, rows[0].TaskId!.Value, "userX", Agree);
        var d2 = await DetailAsync(f.Facade, host.TaskId!.Value);
        var p2 = Assert.IsAssignableFrom<IDictionary<string, object?>>(
            ((IDictionary<string, object?>)DataOf(d2)!["ext"])["aidant"]);
        Assert.Equal(1, Convert.ToInt32(p2["submitted"]));
        Assert.Equal(true, p2["active"]);

        // 无协办行的普通任务不输出该投影
        var other = await StartToReviewAsync(Chain("", NextName("aidant")));
        var plain = await DoingAsync(other.Repo, other.Iid, "review");
        var d3 = await DetailAsync(other.Facade, plain!.TaskId!.Value);
        var ext3 = Assert.IsAssignableFrom<IDictionary<string, object?>>(DataOf(d3)!["ext"]);
        Assert.False(ext3.ContainsKey("aidant"));
    }

    // ═══ 负向：六文案＋越权＋非 DOING ═══

    [Fact]
    public async Task NegativeActorIdsRequired()
    {
        var f = await StartToReviewAsync(Chain("", NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        AssertMsg(await AidantAsync(f.Facade, host!.TaskId!.Value, "userA", null), "actorIds 必填");
        AssertMsg(await AidantAsync(f.Facade, host.TaskId!.Value, "userA", new List<object?>()), "actorIds 必填");
        AssertMsg(await AidantAsync(f.Facade, host.TaskId!.Value, "userA", "  ,  "), "actorIds 必填");
        Assert.Empty(await AidantRowsAsync(f.Repo, host));   // 零写入
    }

    [Fact]
    public async Task NegativeOperatorRequired()
    {
        var f = await StartToReviewAsync(Chain("", NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        AssertMsg(await AidantAsync(f.Facade, host!.TaskId!.Value, null, "userX"), "operator 必填");
        AssertMsg(await AidantAsync(f.Facade, host.TaskId!.Value, "   ", "userX"), "operator 必填");
    }

    [Fact]
    public async Task NegativeTaskNotFound()
    {
        var f = await StartToReviewAsync(Chain("", NextName("aidant")));
        AssertMsg(await AidantAsync(f.Facade, 999999L, "userA", "userX"), "任务不存在");
    }

    [Fact]
    public async Task NegativeUnauthorizedOperator()
    {
        var f = await StartToReviewAsync(Chain("", NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        AssertMsg(await AidantAsync(f.Facade, host!.TaskId!.Value, "userZ", "userX"), "无权限派生协办任务");
        Assert.Empty(await AidantRowsAsync(f.Repo, host));   // 零写入
        Assert.True((await f.Repo.FindTaskByIdAsync(host.TaskId))!.IsDoing());
    }

    [Fact]
    public async Task RosterMemberAndPrivilegedAllowed()
    {
        var f = await StartToReviewAsync(Chain("", NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        // 加签进来的第二参与人同样在名册内
        await f.Facade.FlowAsync("processTask/addCandidate", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = host!.TaskId!.Value.ToString(),
            [FlowConst.ActorIdsKey] = new List<object?> { "userB" },
        });
        Assert.Equal(0, (await AidantAsync(f.Facade, host.TaskId!.Value, "userB", "userX"))["code"]);
        Assert.Equal(0, (await AidantAsync(f.Facade, host.TaskId!.Value, "flow.auto", "userY"))["code"]);
        Assert.Equal(0, (await AidantAsync(f.Facade, host.TaskId!.Value, "flow.admin", "userZ"))["code"]);
        Assert.Equal(3, (await AidantRowsAsync(f.Repo, host)).Count);
    }

    [Fact]
    public async Task NegativeHostNotDoing()
    {
        var f = await StartToReviewAsync(Chain("", NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        await ExecAsync(f.Facade, host!.TaskId!.Value, "userA", Agree);
        Assert.True((await f.Repo.FindTaskByIdAsync(host.TaskId))!.IsFinished());
        AssertMsg(await AidantAsync(f.Facade, host.TaskId!.Value, "userA", "userX"), "任务非进行中，不可派生协办");
    }

    [Fact]
    public async Task NegativeDuplicateActor()
    {
        var f = await StartToReviewAsync(Chain("", NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        await AidantAsync(f.Facade, host!.TaskId!.Value, "userA", "userX");
        AssertMsg(await AidantAsync(f.Facade, host.TaskId!.Value, "userA", "userX"), "该人已有未办结的协办任务");
        Assert.Single(await AidantRowsAsync(f.Repo, host));   // 零写入：不重复建行
        Assert.Single(Ledger(await f.Repo.FindTaskByIdAsync(host.TaskId)));

        // 已办结的那位可以再派生（去重只认未办结）
        var child = (await AidantRowsAsync(f.Repo, host))[0];
        await ExecAsync(f.Facade, child.TaskId!.Value, "userX", Agree);
        Assert.Equal(0, (await AidantAsync(f.Facade, host.TaskId!.Value, "userA", "userX"))["code"]);
        Assert.Equal(2, (await AidantRowsAsync(f.Repo, host)).Count);
    }

    [Fact]
    public async Task NegativeRecursionForbidden()
    {
        var f = await StartToReviewAsync(Chain("", NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        await AidantAsync(f.Facade, host!.TaskId!.Value, "userA", "userX");
        var child = (await AidantRowsAsync(f.Repo, host))[0];
        AssertMsg(await AidantAsync(f.Facade, child.TaskId!.Value, "userX", "userY"), "协办任务不能再派生协办");
        Assert.Single(await AidantRowsAsync(f.Repo, host));   // 零写入
        Assert.Empty(await AidantRowsAsync(f.Repo, child));
    }

    // ═══ 协办行能力边界 ═══

    [Fact]
    public async Task AidantRowSupportsActorWritesAndTransfer()
    {
        var f = await StartToReviewAsync(Chain("", NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        await AidantAsync(f.Facade, host!.TaskId!.Value, "userA", "userX");
        var child = (await AidantRowsAsync(f.Repo, host))[0];
        var cid = child.TaskId!.Value;

        // 加参与人（surrogate / addCandidate 同一腿）
        Assert.Equal(0, (await f.Facade.FlowAsync("processTask/surrogate", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = cid.ToString(),
            [FlowConst.ActorIdsKey] = new List<object?> { "userK" },
        }))["code"]);
        Assert.Contains("userK", await f.Repo.FindTaskActorsAsync(cid));
        Assert.Equal(0, (await f.Facade.FlowAsync("processTask/addCandidate", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = cid.ToString(),
            [FlowConst.ActorIdsKey] = new List<object?> { "userL" },
        }))["code"]);
        Assert.Contains("userL", await f.Repo.FindTaskActorsAsync(cid));

        // 摘参与人（只能摘自己那一票）
        Assert.Equal(0, (await f.Facade.FlowAsync("processTask/removeTaskActor", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = cid.ToString(),
            ["operator"] = "userK",
            [FlowConst.ActorIdsKey] = new List<object?> { "userK" },
        }))["code"]);
        Assert.DoesNotContain("userK", await f.Repo.FindTaskActorsAsync(cid));

        // 转办（摘原人＋换新参与人，同一行不新建）
        Assert.Equal(0, (await f.Facade.FlowAsync("processTask/transfer", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = cid.ToString(),
            ["operator"] = "userX",
            ["fromActor"] = "userX",
            ["toActor"] = "userT",
            ["reason"] = "协办转办",
        }))["code"]);
        Assert.DoesNotContain("userX", await f.Repo.FindTaskActorsAsync(cid));
        Assert.Contains("userT", await f.Repo.FindTaskActorsAsync(cid));
        var after = await f.Repo.FindTaskByIdAsync(cid);
        Assert.Equal(cid, after!.TaskId);   // 转办不新建任务行
        Assert.True(after.IsDoing());
        Assert.Equal(WfTaskType.Secondary, after.TaskType);
        // 转办后新办理人可办结，且仍不驱动流程
        Assert.Equal(0, (await ExecAsync(f.Facade, cid, "userT", Agree))["code"]);
        Assert.True((await f.Repo.FindTaskByIdAsync(host.TaskId))!.IsDoing());
        Assert.Null(await DoingAsync(f.Repo, f.Iid, "final"));
    }

    // ═══ 回归：会签合并 ═══

    [Fact]
    public async Task CountersignMergeNotBlockedByAidant()
    {
        var f = await StartCountersignAsync(CountersignChain(NextName("aidant-cs")));
        var members = await f.Repo.FindDoingTasksAsync(f.Iid, null);
        Assert.Equal(2, members.Count);
        var rowA = members.First(t => t.ActorIds.Contains("userA"));
        var rowB = members.First(t => t.ActorIds.Contains("userB"));
        // 在会签成员行上派生协办（协办行同节点同名，但 task_type=1）
        Assert.Equal(0, (await AidantAsync(f.Facade, rowA.TaskId!.Value, "userA", "userX"))["code"]);
        var child = (await AidantRowsAsync(f.Repo, rowA))[0];
        Assert.True(child.IsDoing());
        Assert.Equal("cs", child.TaskName);

        // 两票齐 ⇒ 推进（改前协办行会被当第三张票，永远合并不了）
        Assert.Equal(0, (await ExecAsync(f.Facade, rowA.TaskId!.Value, "userA", Agree))["code"]);
        Assert.Equal(0, (await ExecAsync(f.Facade, rowB.TaskId!.Value, "userB", Agree))["code"]);
        Assert.Equal(20, (await f.Repo.FindInstanceByIdAsync(f.Iid))!.State);   // 票齐即推进：实例办结
        Assert.Equal(99, (await f.Repo.FindTaskByIdAsync(child.TaskId))!.TaskState);   // 协办行随离开节点清场
        Assert.Empty(await f.Repo.FindDoingTasksAsync(f.Iid, null));
    }

    [Fact]
    public async Task AidantOnCountersignDoesNotAdvanceEarly()
    {
        var f = await StartCountersignAsync(CountersignChain(NextName("aidant-cs")));
        var members = await f.Repo.FindDoingTasksAsync(f.Iid, null);
        var rowA = members.First(t => t.ActorIds.Contains("userA"));
        await AidantAsync(f.Facade, rowA.TaskId!.Value, "userA", "userX");
        var child = (await AidantRowsAsync(f.Repo, rowA))[0];
        await ExecAsync(f.Facade, child.TaskId!.Value, "userX", Agree);
        Assert.Equal(10, (await f.Repo.FindInstanceByIdAsync(f.Iid))!.State);   // 协办办结不推进
        Assert.Equal(2, await DoingCountAsync(f.Repo, f.Iid, "cs"));
    }

    // ═══ 回归：与征询并存 ═══

    private const string CollectOn = "\"dynamicCountersign\":true";

    private static Task<Dictionary<string, object?>> CollectAsync(JeeflowFacade facade, long hostTaskId,
        string op, List<string> operators)
    {
        var a = new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = hostTaskId.ToString(),
            ["operator"] = op,
            [FlowConst.SubmitType] = Agree,
            [FlowConst.AddSignOperators] = new List<object?>(operators),
        };
        return facade.FlowAsync("processTask/execute", a);
    }

    [Fact]
    public async Task CoexistWithCollectTwoGatesCountOwnRows()
    {
        var f = await StartToReviewAsync(Chain(CollectOn, NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        // 征询拉一人（成员行 task_type=0）
        Assert.Equal(0, (await CollectAsync(f.Facade, host!.TaskId!.Value, "userA", new List<string> { "userC" }))["code"]);
        // 协办派生一人（协办行 task_type=1），等待闸开
        Assert.Equal(0, (await AidantAsync(f.Facade, host.TaskId!.Value, "userA", "userX", null, true))["code"]);

        var hostRow = await f.Repo.FindTaskByIdAsync(host.TaskId);
        var all = (await f.Repo.FindInstanceByIdAsync(f.Iid))!.Tasks;
        Assert.Equal(1, CollectSupport.CountMembers(all, hostRow)[0]);   // 征询收齐闸只数成员行
        Assert.Equal(1, AidantSupport.CountAidant(all, hostRow)[0]);     // 协办等待闸只数协办行
        Assert.Single(CollectSupport.MemberRows(all, hostRow));
        Assert.Single(AidantSupport.AidantRows(all, hostRow));
        Assert.False(CollectSupport.IsMemberRow(all, AidantSupport.AidantRows(all, hostRow)[0]));   // 协办行不是征询成员行

        // 协办先办结 ⇒ 征询闸仍拦（各数各的行），文案是征询那一族
        var aidantRow = AidantSupport.AidantRows(all, hostRow)[0];
        await ExecAsync(f.Facade, aidantRow.TaskId!.Value, "userX", Agree);
        AssertMsg(await ExecAsync(f.Facade, hostRow!.TaskId!.Value, "userA", Agree),
            "节点[review]动态会签意见未收齐（已提交 0/1），不能办理");

        // 征询成员交意见 ⇒ 两闸齐过，主办推进
        var member = CollectSupport.MemberRows((await f.Repo.FindInstanceByIdAsync(f.Iid))!.Tasks,
            await f.Repo.FindTaskByIdAsync(host.TaskId))[0];
        Assert.Equal(0, (await ExecAsync(f.Facade, member.TaskId!.Value, "userC", Agree))["code"]);
        Assert.Equal(0, (await ExecAsync(f.Facade, host.TaskId!.Value, "userA", Agree))["code"]);
        Assert.NotNull(await DoingAsync(f.Repo, f.Iid, "final"));
    }

    [Fact]
    public async Task CollectDoneButAidantPendingReportsAidantMessage()
    {
        var f = await StartToReviewAsync(Chain(CollectOn, NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        await CollectAsync(f.Facade, host!.TaskId!.Value, "userA", new List<string> { "userC" });
        await AidantAsync(f.Facade, host.TaskId!.Value, "userA", "userX", null, true);
        var member = CollectSupport.MemberRows((await f.Repo.FindInstanceByIdAsync(f.Iid))!.Tasks,
            await f.Repo.FindTaskByIdAsync(host.TaskId))[0];
        await ExecAsync(f.Facade, member.TaskId!.Value, "userC", Agree);
        AssertMsg(await ExecAsync(f.Facade, host.TaskId!.Value, "userA", Agree),
            "节点[review]协办未办结（已办结 0/1），不能办理");
    }

    [Fact]
    public async Task CollectNodeWaitFalseNotBlockedByAidant()
    {
        var f = await StartToReviewAsync(Chain(CollectOn, NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        await CollectAsync(f.Facade, host!.TaskId!.Value, "userA", new List<string> { "userC" });
        await AidantAsync(f.Facade, host.TaskId!.Value, "userA", "userX", null, false);
        var member = CollectSupport.MemberRows((await f.Repo.FindInstanceByIdAsync(f.Iid))!.Tasks,
            await f.Repo.FindTaskByIdAsync(host.TaskId))[0];
        await ExecAsync(f.Facade, member.TaskId!.Value, "userC", Agree);
        Assert.Equal(0, (await ExecAsync(f.Facade, host.TaskId!.Value, "userA", Agree))["code"]);   // 缺省不等协办：收齐即推进
        Assert.NotNull(await DoingAsync(f.Repo, f.Iid, "final"));
        var aidantRow = AidantSupport.AidantRows((await f.Repo.FindInstanceByIdAsync(f.Iid))!.Tasks,
            await f.Repo.FindTaskByIdAsync(host.TaskId))[0];
        Assert.Equal(99, (await f.Repo.FindTaskByIdAsync(aidantRow.TaskId))!.TaskState);
    }

    // ═══ 回归：撤回清场 ═══

    [Fact]
    public async Task RecallCleansAidantRows()
    {
        var f = await StartToReviewAsync(Chain("\"allowRecall\":true", NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        Assert.Equal(0, (await ExecAsync(f.Facade, host!.TaskId!.Value, "userA", Agree))["code"]);
        var finalRow = await DoingAsync(f.Repo, f.Iid, "final");
        Assert.NotNull(finalRow);
        // 在下游 final 行上派生协办 ⇒ 它是 review 行的血缘后代
        Assert.Equal(0, (await AidantAsync(f.Facade, finalRow!.TaskId!.Value, "userF", "userX"))["code"]);
        var child = (await AidantRowsAsync(f.Repo, finalRow))[0];
        Assert.True(child.IsDoing());

        var recall = await f.Facade.FlowAsync("processTask/recall", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = host.TaskId!.Value.ToString(),
            ["operator"] = "userA",
        });
        Assert.Equal(0, recall["code"]);
        Assert.Equal(99, (await f.Repo.FindTaskByIdAsync(child.TaskId))!.TaskState);   // 下游协办行废弃
        Assert.Equal(99, (await f.Repo.FindTaskByIdAsync(finalRow.TaskId))!.TaskState);   // 下游主办行废弃
        Assert.Single(await f.Repo.FindDoingTasksAsync(f.Iid, null));   // 唯一活待办
    }

    [Fact]
    public async Task InstanceWithdrawCleansAidantRows()
    {
        var f = await StartToReviewAsync(Chain("", NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        await AidantAsync(f.Facade, host!.TaskId!.Value, "userA", "userX");
        var child = (await AidantRowsAsync(f.Repo, host))[0];
        var w = await f.Facade.FlowAsync("processInstance/withdraw", new FlowData
        {
            ["id"] = f.Iid.ToString(),
            ["operator"] = "applicant",
        });
        Assert.Equal(0, w["code"]);
        Assert.Equal(30, (await f.Repo.FindTaskByIdAsync(child.TaskId))!.TaskState);
        Assert.Empty(await f.Repo.FindDoingTasksAsync(f.Iid, null));
    }

    // ═══ 回归：既有 actor 写腿不变 ═══

    [Fact]
    public async Task TransferSurrogateAddCandidateUnchanged()
    {
        var f = await StartToReviewAsync(Chain("", NextName("aidant")));
        var host = await DoingAsync(f.Repo, f.Iid, "review");
        var hid = host!.TaskId!.Value;
        await AidantAsync(f.Facade, hid, "userA", "userX");

        // surrogate：只追加，原人保留
        Assert.Equal(0, (await f.Facade.FlowAsync("processTask/surrogate", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = hid.ToString(),
            [FlowConst.ActorIdsKey] = new List<object?> { "userB" },
        }))["code"]);
        Assert.Equal(new[] { "userA", "userB" }, await f.Repo.FindTaskActorsAsync(hid));
        // addCandidate：同一腿
        Assert.Equal(0, (await f.Facade.FlowAsync("processTask/addCandidate", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = hid.ToString(),
            [FlowConst.ActorIdsKey] = new List<object?> { "userC" },
        }))["code"]);
        Assert.Equal(new[] { "userA", "userB", "userC" }, await f.Repo.FindTaskActorsAsync(hid));
        // transfer：摘原人换新参与人，同一行不新建，留痕三件照旧
        Assert.Equal(0, (await f.Facade.FlowAsync("processTask/transfer", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = hid.ToString(),
            ["operator"] = "userA",
            ["fromActor"] = "userA",
            ["toActor"] = "userD",
            ["reason"] = "转办原因",
        }))["code"]);
        Assert.DoesNotContain("userA", await f.Repo.FindTaskActorsAsync(hid));
        Assert.Contains("userD", await f.Repo.FindTaskActorsAsync(hid));
        var after = await f.Repo.FindTaskByIdAsync(hid);
        Assert.Equal(hid, after!.TaskId);
        Assert.Equal(7, after.Variables.GetInt(FlowConst.SubmitType));
        Assert.NotNull(after.Variables.GetObj(FlowConst.TransferHistory));
        Assert.Null(after.ActorId);   // 转办不写 actor_id 列
        // 协办行不受主办转办影响，仍 DOING 且不驱动
        Assert.Single(await AidantRowsAsync(f.Repo, after));
        Assert.True((await AidantRowsAsync(f.Repo, after))[0].IsDoing());
        // 新办理人办结照常推进
        Assert.Equal(0, (await ExecAsync(f.Facade, hid, "userD", Agree))["code"]);
        Assert.NotNull(await DoingAsync(f.Repo, f.Iid, "final"));
    }
}
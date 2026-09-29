using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/127 ＋ issues/132 事件代码腿——唯一权威契约 <c>jeeflow-doc/docs/spec/11-events.md</c>。
///
/// <para>钉四件事（正是 132 的两个病灶＋两条义务）：</para>
/// <list type="number">
/// <item><b>码表</b>（§11.3 A 套整型）：1..4 不变（4＝CC_CREATE，§11.6 号位让位史），
/// 新增 5 TASK_COMPLETE／6 TASK_REJECT／7 TASK_TRANSFER／8 TASK_WITHDRAW／9 INSTANCE_TERMINATED，
/// 10+ 只占号不发（§11.4 第 1 条）；</item>
/// <item><b>顺序</b>（§11.8 镜像格 L2-30）：一条流从发起到办结<b>按顺序</b>断
/// <c>[1,3,5,2]</c>＋抄送支 <c>[4]</c>——只断"出现过"不算过；</item>
/// <item><b>落库之后才 fire</b>（§11.2 原则 3）：监听器在回调里反查仓储，读到的必须已是新状态；</item>
/// <item><b>逐监听器异常隔离</b>（§11.5）：单监听器抛异常只记日志，不回滚主流程、不中断后续监听器。</item>
/// </list>
///
/// <para>ℹ️ 本文件对码 2 只断"发过＋载荷 state 正确"，<b>不</b>断"实例行已落库"——写序不是本文件的
/// 判据，它是 <see cref="InstanceEndTiming132Tests"/> 的唯一要点（R2-1 收口：处理器改为只登记，
/// 引擎在 <c>UpdateInstanceAsync</c> 之后统一 flush）。两边的分工要留着：本文件是<b>可见序</b>的
/// recorder，对写序<b>失明</b>（把 fire 挪回处理器就地，本文件仍然全绿）——这正是
/// <c>InstanceEndTiming132Tests</c> 必须独立存在、且不能被本文件"顺手补一条断言"替代的原因。</para>
///
/// <para>9 INSTANCE_TERMINATED 在本栈<b>无 fire 点</b>：门面 40+ action 没有"终止实例"这一支
/// （issues/134 门面级注释亦记"门面没有终止实例的 action"），聚合根 <c>Interrupt</c> 生产路径零调用者
/// ⇒ 只占号。此结论与 java 栈一致，见 <see cref="CodeTable_MatchesSpecASet_Codes1To9"/>。</para>
/// </summary>
public class EventCodes132Tests
{
    // ═══ 夹具 ═══

    private static (JeeflowEngine Engine, MemoryRepository Repo, ServiceContext Ctx,
        JeeflowFacade Facade, ProbeListener Probe) NewStack()
    {
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        var probe = new ProbeListener(repo);
        ctx.RegisterEventListener(probe);
        return (engine, repo, ctx, new JeeflowFacade(ctx), probe);
    }

    /// <summary>01-simple 走到底：发起 →（门面）办 apply →（门面）办 task1 同意 ⇒ 实例 20。</summary>
    private static async Task<(long InstanceId, long ApplyTaskId, long LeaderTaskId)> StartAndRunToLeaderAsync(
        JeeflowEngine engine, JeeflowFacade facade, MemoryRepository repo, string name)
    {
        var did = await TestInfra.SaveFlowDefineAsync(repo, name, TestInfra.LoadFlow("01-simple"));
        var inst = await engine.StartProcessInstanceByIdAsync(did, "user1", new FlowData());
        var apply = await TestInfra.FindDoingForAsync(repo, inst.InstanceId!.Value, "user1");
        var resp = await facade.FlowAsync("processTask/execute", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = apply.TaskId,
            ["operator"] = "user1",
        });
        Assert.True(Equals(0, resp["code"]), $"办 apply 应成功: {resp["msg"]}");
        var leader = await TestInfra.FindDoingForAsync(repo, inst.InstanceId!.Value, "leader");
        return (inst.InstanceId!.Value, apply.TaskId!.Value, leader.TaskId!.Value);
    }

    // ═══ ① 码表（spec §11.3 A 套）═══

    [Fact]
    public void CodeTable_MatchesSpecASet_Codes1To9()
    {
        // 规范名是权威，码值是本栈附带数值：A 套 1..4 不动（java 血缘，4 号位让位史见 §11.6），
        // 5..9 为 132 新增，10+ 本轮不发只占号（§11.4 第 1 条）。
        Assert.Equal(1, (int)ProcessEventType.ProcessInstanceStart);   // PROCESS_INSTANCE_START
        Assert.Equal(2, (int)ProcessEventType.ProcessInstanceEnd);     // PROCESS_INSTANCE_END
        Assert.Equal(3, (int)ProcessEventType.ProcessTaskStart);       // PROCESS_TASK_START
        Assert.Equal(4, (int)ProcessEventType.CcCreate);               // CC_CREATE
        Assert.Equal(5, (int)ProcessEventType.TaskComplete);           // TASK_COMPLETE
        Assert.Equal(6, (int)ProcessEventType.TaskReject);             // TASK_REJECT
        Assert.Equal(7, (int)ProcessEventType.TaskTransfer);           // TASK_TRANSFER
        Assert.Equal(8, (int)ProcessEventType.TaskWithdraw);           // TASK_WITHDRAW
        Assert.Equal(9, (int)ProcessEventType.InstanceTerminated);     // INSTANCE_TERMINATED

        // 不重排、不复用：1..9 恰各一次，且没有任何栈内码越到 10+（预留段不得被本轮占用）
        var codes = Enum.GetValues<ProcessEventType>().Select(e => (int)e).OrderBy(c => c).ToList();
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, codes);
    }

    // ═══ ② 顺序（镜像格 L2-30：只断"出现过"不算过）═══

    [Fact]
    public async Task Sequence_FullFlowFiresInOrder_AndDedupMatchesL2_30()
    {
        var (engine, repo, _, facade, probe) = NewStack();
        var (iid, _, leaderTaskId) = await StartAndRunToLeaderAsync(engine, facade, repo, "132-seq");
        await facade.FlowAsync("processTask/execute", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = leaderTaskId,
            ["operator"] = "leader",
        });

        // 逐事件原序：办 apply 出新待办 task1，故 3 与 5 各出现两次——
        // 5 紧跟其行的 updateTask 落库、新的 3 紧跟其行的 saveTask 落库。
        Assert.Equal(new[]
        {
            "ProcessInstanceStart", "ProcessTaskStart",
            "TaskComplete", "ProcessTaskStart",
            "TaskComplete", "ProcessInstanceEnd",
        }, probe.Names);

        // 规范名去重保序＝跨栈门禁格 L2-30 的 [1,3,5,2]（spec §11.8）
        var deduped = new List<string>();
        foreach (var n in probe.Names) if (!deduped.Contains(n)) deduped.Add(n);
        Assert.Equal(new[] { "ProcessInstanceStart", "ProcessTaskStart", "TaskComplete", "ProcessInstanceEnd" },
            deduped);

        // 抄送支独立成序（[4]，逐抄送人一次、按列表序、cc 行落库之后才发）
        var ccSeq = await CcCreateSequenceAsync();
        Assert.Equal(new List<string> { "u1", "u2" }, ccSeq.CcActorIds);

        // 载荷键按 §11.3 逐支齐全（直传键之外的字段允许监听器反查，故只钉必备键）
        AssertDataKeys(First(probe, ProcessEventType.ProcessInstanceStart), "instanceId");
        AssertDataKeys(First(probe, ProcessEventType.ProcessTaskStart), "instanceId", "taskId", "actors");
        AssertDataKeys(First(probe, ProcessEventType.TaskComplete),
            "instanceId", "taskId", "operator", "submitType");
        AssertDataKeys(First(probe, ProcessEventType.ProcessInstanceEnd), "instanceId", "state");

        // 载荷值语义：末支 InstanceEnd 的 state＝落库后的实例状态整数；taskId 即 sourceId
        var end = First(probe, ProcessEventType.ProcessInstanceEnd);
        Assert.Equal(iid, end.SourceId!.Value);
        Assert.Equal((int)WfInstanceState.Finished, Convert.ToInt32(end.Data["state"]));
        foreach (var ev in probe.Events.Where(e => e.EventType == ProcessEventType.TaskComplete))
            Assert.Equal(ev.SourceId!.Value, Convert.ToInt64(ev.Data["taskId"]!));

        // 10+ 预留段没被任何事件用上
        Assert.All(probe.Events, ev => Assert.True((int)ev.EventType <= 9));

        // 探针（在监听器回调里反查仓储）零失败＝本文件覆盖到的每一支都是"落库之后才 fire"
        //（码 2 的写序判据不在这里，见类注释与 InstanceEndTiming132Tests）
        Assert.Empty(probe.Failures);
    }

    /// <summary>抄送支：发起 f_ccActors 逐人 fire（issues/102 既有腿，本轮验载荷与落库序）。</summary>
    private static async Task<ProbeListener> CcCreateSequenceAsync()
    {
        var (engine, repo, _, _, probe) = NewStack();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "132-cc-start", TestInfra.LoadFlow("01-simple"));
        await engine.StartProcessInstanceByIdAsync(did, "user1", new FlowData
        {
            [FlowConst.CcActorsStart] = new List<object?> { "u1", "u2" },
        });
        Assert.Empty(probe.Failures);
        return probe;
    }

    // ═══ ③ 127 的抄送三条路径同判（§11.3 码 4 ＋ §11.7）═══

    [Fact]
    public async Task Cc_Leg_ExecuteTfCcActors_CreatesRowsAndFiresPerActor()
    {
        // issues/127 病灶：办理时 tf_ccActors 这条路必须"与任务更新同事务建 cc 行，落库后逐人 fire"
        var (engine, repo, _, facade, probe) = NewStack();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "132-cc-tf", TestInfra.LoadFlow("01-simple"));
        var inst = await engine.StartProcessInstanceByIdAsync(did, "user1", new FlowData());
        var iid = inst.InstanceId!.Value;
        var apply = await TestInfra.FindDoingForAsync(repo, iid, "user1");
        var resp = await facade.FlowAsync("processTask/execute", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = apply.TaskId,
            ["operator"] = "user1",
            [FlowConst.CcActors] = new List<object?> { "ccA", "ccB", "ccC" },
        });
        Assert.True(Equals(0, resp["code"]), $"办理＋抄送应成功: {resp["msg"]}");

        Assert.Equal(3, probe.Names.Count(n => n == "CcCreate"));
        Assert.Equal(new List<string> { "ccA", "ccB", "ccC" },
            probe.Events.Where(e => e.EventType == ProcessEventType.CcCreate)
                .Select(e => e.CcActorId!).ToList());
        Assert.All(probe.Events.Where(e => e.EventType == ProcessEventType.CcCreate),
            e => Assert.Equal(iid, e.SourceId!.Value));
        // 探针：每一次 CcCreate 回调里，三条 cc 行都已可反查（fire 在 createCcInstance 之后）
        Assert.Empty(probe.Failures);
        // 读法带归属条件（issues/141 G1：零条件 ⇒ 空页），断言一字未改
        var page = await repo.PageCcInstancesAsync(new PageQuery(1, 10)
            .Add("cc.actor_id", "IN", new List<string> { "ccA", "ccB", "ccC" }));
        Assert.Equal(3, page.RecordCount);
    }

    [Fact]
    public async Task Cc_Leg_ManualCreateCCInstance_AlsoFiresCcCreate()
    {
        // spec §11.2 原则 1／§11.6 待拍①：手动 createCCInstance <b>也要 fire</b>——
        // 码值表达"新增了一条抄送记录"这个事实，不表达"谁触发的"。
        // 这条腿此前本栈缺（issues/132 §4.5：csharp＝FlowConst 对齐 ＋ CreateCcInstanceAsync 补 fire）。
        var (engine, repo, _, facade, probe) = NewStack();
        var (iid, _, _) = await StartAndRunToLeaderAsync(engine, facade, repo, "132-cc-manual");
        var resp = await facade.FlowAsync("processInstance/createCCInstance", new FlowData
        {
            [FlowConst.ProcessInstanceIdKey] = iid,
            ["operator"] = "user1",
            ["actorIds"] = new List<object?> { "m1", "m2" },
        });
        Assert.True(Equals(0, resp["code"]), $"手动抄送应成功: {resp["msg"]}");

        Assert.Equal(new List<string> { "m1", "m2" },
            probe.Events.Where(e => e.EventType == ProcessEventType.CcCreate)
                .Select(e => e.CcActorId!).ToList());
        Assert.All(probe.Events.Where(e => e.EventType == ProcessEventType.CcCreate),
            e => Assert.Equal(iid, e.SourceId!.Value));
        Assert.Empty(probe.Failures);
        // 读法带归属条件（issues/141 G1：零条件 ⇒ 空页），断言一字未改
        Assert.Equal(2, (await repo.PageCcInstancesAsync(new PageQuery(1, 10)
            .Add("cc.actor_id", "IN", new List<string> { "m1", "m2" }))).RecordCount);

        // 负向不补发：actorIds 缺失／空集合 ⇒ 一行不建、一个事件不发（§11.2 原则 3 的另一面）
        var before = probe.Events.Count(e => e.EventType == ProcessEventType.CcCreate);
        var bad = await facade.FlowAsync("processInstance/createCCInstance", new FlowData
        {
            [FlowConst.ProcessInstanceIdKey] = iid,
            ["operator"] = "user1",
            ["actorIds"] = new List<object?>(),
        });
        Assert.Equal(99999999, bad["code"]);
        Assert.Equal(before, probe.Events.Count(e => e.EventType == ProcessEventType.CcCreate));
    }

    // ═══ ④ 5/6 互斥：一次办理只发一支，靠载荷 submitType 分（§11.2 原则 2）═══

    [Fact]
    public async Task TaskComplete_And_TaskReject_AreMutuallyExclusive_PerAction()
    {
        var (engine, repo, _, facade, probe) = NewStack();
        var (iid, _, leaderTaskId) = await StartAndRunToLeaderAsync(engine, facade, repo, "132-mutex");
        await facade.FlowAsync("processTask/execute", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = leaderTaskId,
            ["operator"] = "leader",
            [FlowConst.SubmitType] = (int)WfSubmitType.Reject,   // 2 拒绝 ⇒ 只发 6
        });

        // apply＝同意(5)，leader＝拒绝(6)：5 只一次，6 只一次，绝不并发
        Assert.Equal(1, probe.Names.Count(n => n == "TaskComplete"));
        Assert.Equal(1, probe.Names.Count(n => n == "TaskReject"));
        Assert.Equal(1, probe.Names.Count(n => n == "ProcessInstanceEnd"));

        var reject = First(probe, ProcessEventType.TaskReject);
        Assert.Equal(leaderTaskId, reject.SourceId);
        Assert.Equal((int)WfSubmitType.Reject, Convert.ToInt32(reject.Data[FlowConst.SubmitType]!));
        Assert.Equal("leader", reject.Data["operator"]);
        Assert.Equal(iid, Convert.ToInt64(reject.Data["instanceId"]!));
        AssertDataKeys(reject, "instanceId", "taskId", "operator", "submitType");

        // 拒绝使实例进 45，终态另发码 2（两支不互相替代，§11.3 码 6 末注）
        Assert.Equal((int)WfInstanceState.Reject,
            Convert.ToInt32(First(probe, ProcessEventType.ProcessInstanceEnd).Data["state"]!));
        Assert.Equal((int)WfInstanceState.Reject, (await repo.FindInstanceByIdAsync(iid))!.State);
        Assert.Empty(probe.Failures);
    }

    [Fact]
    public async Task Rollback_FiresTaskReject_AndRevivedRowFiresTaskStart()
    {
        // 3 退回上一步归 6；回退复活行按 §11.3 码 3「含回退复活行」补发一支 3。
        // 四条办理入口共用 PrepareExecutionAsync 一个 fire 点（§11.3 码 5「覆盖全部流转路径」）。
        var (engine, repo, _, facade, probe) = NewStack();
        var (iid, _, leaderTaskId) = await StartAndRunToLeaderAsync(engine, facade, repo, "132-rollback");
        var resp = await facade.FlowAsync("processTask/execute", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = leaderTaskId,
            ["operator"] = "leader",
            [FlowConst.SubmitType] = (int)WfSubmitType.Rollback,   // 3
        });
        Assert.True(Equals(0, resp["code"]), $"退回应成功: {resp["msg"]}");

        Assert.Equal(1, probe.Names.Count(n => n == "TaskReject"));
        Assert.Equal(1, probe.Names.Count(n => n == "TaskComplete"));   // apply 那一次，退回不并发
        Assert.Equal(3, probe.Names.Count(n => n == "ProcessTaskStart")); // apply、task1、复活回来的 apply
        var reject = First(probe, ProcessEventType.TaskReject);
        Assert.Equal((int)WfSubmitType.Rollback, Convert.ToInt32(reject.Data[FlowConst.SubmitType]!));
        // 回退不使实例进终态 ⇒ 不发码 2
        Assert.DoesNotContain("ProcessInstanceEnd", probe.Names);
        Assert.Equal((int)WfInstanceState.Doing, (await repo.FindInstanceByIdAsync(iid))!.State);
        Assert.Empty(probe.Failures);
    }

    [Fact]
    public async Task CountersignDisagree_FiresTaskReject_SoftRejectBranch()
    {
        // §11.3 码 6 的"软拒绝"档：submitType=20 会签拒绝共用 6（不另开号）
        var (engine, repo, _, facade, probe) = NewStack();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "132-soft-reject",
            TestInfra.LoadFlow("05-countersign-parallel"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var userA = await TestInfra.FindDoingForAsync(repo, iid, "userA");
        var resp = await facade.FlowAsync("processTask/execute", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = userA.TaskId,
            ["operator"] = "userA",
            [FlowConst.SubmitType] = (int)WfSubmitType.CountersignDisagree,   // 20
        });
        Assert.True(Equals(0, resp["code"]), $"软拒绝应成功: {resp["msg"]}");

        Assert.Equal(1, probe.Names.Count(n => n == "TaskReject"));
        Assert.Equal(1, probe.Names.Count(n => n == "TaskComplete"));   // 只有 apply 那一步；退回与它互斥
        var reject = First(probe, ProcessEventType.TaskReject);
        Assert.Equal((int)WfSubmitType.CountersignDisagree,
            Convert.ToInt32(reject.Data[FlowConst.SubmitType]!));
        Assert.Equal(reject.SourceId!.Value, Convert.ToInt64(reject.Data["taskId"]!));
        Assert.Empty(probe.Failures);
    }

    // ═══ ⑤ 7 转办 ／ 8 撤回（八栈此前零事件，132 新增）═══

    [Fact]
    public async Task Transfer_FiresTaskTransfer_AfterActorSwapAndWithoutNewTask()
    {
        var (engine, repo, _, facade, probe) = NewStack();
        var (_, _, leaderTaskId) = await StartAndRunToLeaderAsync(engine, facade, repo, "132-transfer");
        var resp = await facade.FlowAsync("processTask/transfer", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = leaderTaskId,
            ["operator"] = "leader",
            ["fromActor"] = "leader",
            ["toActor"] = "manager",
            ["reason"] = "出差",
        });
        Assert.True(Equals(0, resp["code"]), $"转办应成功: {resp["msg"]}");

        var transfer = First(probe, ProcessEventType.TaskTransfer);
        Assert.Equal(leaderTaskId, transfer.SourceId);
        AssertDataKeys(transfer, "instanceId", "taskId", "fromActor", "toActor", "operator");
        Assert.Equal("leader", transfer.Data["fromActor"]);
        Assert.Equal("manager", transfer.Data["toActor"]);
        Assert.Equal("leader", transfer.Data["operator"]);
        // 转办不新建任务行 ⇒ 不伴随码 3（§11.3 码 7 注）
        Assert.Equal(2, probe.Names.Count(n => n == "ProcessTaskStart"));
        // 探针：回调里参与者表已换成 manager（fromActor 已摘），即"参与者被替换并落库之后"
        Assert.Empty(probe.Failures);

        // 负向不发：原人不是参与人 ⇒ 一行不改、零事件
        var n0 = probe.Events.Count(e => e.EventType == ProcessEventType.TaskTransfer);
        var bad = await facade.FlowAsync("processTask/transfer", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = leaderTaskId,
            ["operator"] = "leader",
            ["fromActor"] = "notAParticipant",
            ["toActor"] = "director",
        });
        Assert.Equal(99999999, bad["code"]);
        Assert.Equal(n0, probe.Events.Count(e => e.EventType == ProcessEventType.TaskTransfer));
    }

    [Fact]
    public async Task Withdraw_FiresTaskWithdraw_OncePerRoundAfterState30()
    {
        var (engine, repo, _, facade, probe) = NewStack();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "132-withdraw",
            TestInfra.LoadFlow("05-countersign-parallel"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var doing = await repo.FindDoingTasksAsync(iid, null);
        Assert.True(doing.Count >= 2, "夹具需要多条进行中任务，才验得出「每轮只 fire 一次」");

        var resp = await facade.FlowAsync("processInstance/withdraw",
            new FlowData { ["id"] = iid, ["operator"] = "applicant" });
        Assert.True(Equals(0, resp["code"]), $"撤回应成功: {resp["msg"]}");

        // §11.3 码 8：每轮撤回只 fire 一次，不逐任务
        Assert.Equal(1, probe.Names.Count(n => n == "TaskWithdraw"));
        var ev = First(probe, ProcessEventType.TaskWithdraw);
        Assert.Equal(iid, ev.SourceId);
        AssertDataKeys(ev, "instanceId", "operator");
        Assert.Equal("applicant", ev.Data["operator"]);
        // 探针：回调里实例已是 30、进行中任务行也已 30（级联已落库）
        Assert.Empty(probe.Failures);
    }

    [Fact]
    public async Task Withdraw_RejectedByInstanceGuard_FiresNothing()
    {
        // issues/134 的守卫与本案的联动：被拒的那一轮不成立事实 ⇒ 不发码 8（§11.2 原则 3 的另一面）
        var (engine, repo, _, facade, probe) = NewStack();
        var (iid, _, leaderTaskId) = await StartAndRunToLeaderAsync(engine, facade, repo, "132-withdraw-neg");
        await facade.FlowAsync("processTask/execute", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = leaderTaskId,
            ["operator"] = "leader",
        });
        Assert.Equal((int)WfInstanceState.Finished, (await repo.FindInstanceByIdAsync(iid))!.State);

        var resp = await facade.FlowAsync("processInstance/withdraw",
            new FlowData { ["id"] = iid, ["operator"] = FlowConst.AdminId });
        Assert.Equal(99999999, resp["code"]);
        Assert.Equal(0, probe.Names.Count(n => n == "TaskWithdraw"));
    }

    // ═══ ⑥ 订阅形状与异常隔离（§11.5）═══

    [Fact]
    public async Task PerListenerIsolation_ThrowingListenerKeepsMainFlowAndLaterListeners()
    {
        // 注册顺序＝回调顺序；单监听器抛异常只记日志：① 不回滚主流程 ② 不中断后续监听器。
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        var boom = new ThrowingListener();          // 先注册：它抛，后面的必须照收
        var second = new ProbeListener(repo);
        var third = new ProbeListener(repo);
        ctx.RegisterEventListener(boom);
        ctx.RegisterEventListener(second);
        ctx.RegisterEventListener(third);
        var facade = new JeeflowFacade(ctx);

        var did = await TestInfra.SaveFlowDefineAsync(repo, "132-isolation", TestInfra.LoadFlow("01-simple"));
        var inst = await engine.StartProcessInstanceByIdAsync(did, "user1", new FlowData
        {
            [FlowConst.CcActorsStart] = new List<object?> { "u1" },
        });
        var iid = inst.InstanceId!.Value;
        var apply = await TestInfra.FindDoingForAsync(repo, iid, "user1");
        await facade.FlowAsync("processTask/execute", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = apply.TaskId, ["operator"] = "user1",
        });
        var leader = await TestInfra.FindDoingForAsync(repo, iid, "leader");
        var resp = await facade.FlowAsync("processTask/execute", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = leader.TaskId, ["operator"] = "leader",
        });

        // 主流程没被监听器的异常带跑：办结成功、实例落 20、cc 行落库
        Assert.True(Equals(0, resp["code"]), $"监听器抛异常不得回滚主流程: {resp["msg"]}");
        Assert.Equal((int)WfInstanceState.Finished, (await repo.FindInstanceByIdAsync(iid))!.State);
        // 读法带归属条件（issues/141 G1：零条件 ⇒ 空页），断言一字未改
        Assert.Equal(1, (await repo.PageCcInstancesAsync(new PageQuery(1, 10)
            .Add("cc.actor_id", "EQ", "u1"))).RecordCount);
        // 后续监听器一个不漏，两个监听器收到的序列完全相同（同栈内有序）。
        // CcCreate 排在最前是既有发起路形状：StartInTxAsync 第 7 步建 cc 行并 fire，第 8 步才跑开始节点
        // ——两条都各自满足"落库之后才 fire"（cc 行 insert 后、实例行 insert 后），spec 未钉跨支次序。
        Assert.Equal(second.Names, third.Names);
        Assert.Equal(new[]
        {
            "CcCreate", "ProcessInstanceStart", "ProcessTaskStart",
            "TaskComplete", "ProcessTaskStart",
            "TaskComplete", "ProcessInstanceEnd",
        }, second.Names);
        Assert.Empty(second.Failures);
        // 反查键可用：CC_CREATE 的 ccActorId 与 TASK_* 的 sourceId 都拿到了
        Assert.Equal("u1", second.Events.Single(e => e.EventType == ProcessEventType.CcCreate).CcActorId);
    }

    [Fact]
    public async Task ZeroListeners_AllNewFirePoints_ReturnSuccessAndDoNotThrow()
    {
        // §11.5「无监听器」义务：零注册时 fire 必须安全返回（不得空指针）——覆盖本轮新增的四支
        var (engine, repo) = TestInfra.NewEngine();   // 不注册任何监听器
        var facade = new JeeflowFacade(TestInfra.NewContext(repo));
        var did = await TestInfra.SaveFlowDefineAsync(repo, "132-no-listener", TestInfra.LoadFlow("01-simple"));
        var inst = await engine.StartProcessInstanceByIdAsync(did, "user1", new FlowData());
        var iid = inst.InstanceId!.Value;
        var apply = await TestInfra.FindDoingForAsync(repo, iid, "user1");

        // 办理＋抄送（码 5 + 码 4）
        Assert.Equal(0, (await facade.FlowAsync("processTask/execute", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = apply.TaskId, ["operator"] = "user1",
            [FlowConst.CcActors] = new List<object?> { "u1" },
        }))["code"]);
        // 转办（码 7）——task1 此刻才存在
        var after = await TestInfra.FindDoingForAsync(repo, iid, "leader");
        Assert.Equal(0, (await facade.FlowAsync("processTask/transfer", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = after.TaskId, ["operator"] = "leader",
            ["fromActor"] = "leader", ["toActor"] = "manager",
        }))["code"]);
        Assert.Equal(0, (await facade.FlowAsync("processInstance/createCCInstance", new FlowData
        {
            [FlowConst.ProcessInstanceIdKey] = iid, ["operator"] = "user1",
            ["actorIds"] = new List<object?> { "u2" },
        }))["code"]);
        Assert.Equal(0, (await facade.FlowAsync("processInstance/withdraw", new FlowData
        {
            ["id"] = iid, ["operator"] = "manager",
        }))["code"]);
        Assert.Equal((int)WfInstanceState.Withdraw, (await repo.FindInstanceByIdAsync(iid))!.State);
    }

    // ═══ 工具 ═══

    private static ProcessEvent First(ProbeListener probe, ProcessEventType type) =>
        probe.Events.FirstOrDefault(e => e.EventType == type)
        ?? throw new InvalidOperationException($"未收到 {type} 事件（实收：{string.Join(",", probe.Names)}）");

    /// <summary>§11.3「直传载荷键（必备）」——契约只钉"必须能拿到上表这些键"。</summary>
    private static void AssertDataKeys(ProcessEvent ev, params string[] keys)
    {
        foreach (var key in keys)
            Assert.True(ev.Data.ContainsKey(key),
                $"{ev.EventType}（码 {(int)ev.EventType}）载荷缺必备直传键 {key}，实有键：{string.Join(",", ev.Data.Keys)}");
    }

    /// <summary>
    /// 顺序录制＋落库探针监听器：在<b>回调当下</b>反查仓储，断言"事实已落库才 fire"（§11.2 原则 3）。
    /// 探针失败记进 <see cref="Failures"/> 由测试断言为空，而不是在这里抛——
    /// 抛出会被 <see cref="ProcessPublisher"/> 按 §11.5 隔离掉，反而测不出来。
    /// </summary>
    public sealed class ProbeListener : IProcessEventListener
    {
        private readonly MemoryRepository _repo;
        public List<string> Names { get; } = new();
        public List<ProcessEvent> Events { get; } = new();
        public List<string> CcActorIds { get; } = new();
        public List<string> Failures { get; } = new();

        public ProbeListener(MemoryRepository repo) => _repo = repo;

        public async Task OnEventAsync(ProcessEvent @event)
        {
            Names.Add(@event.EventType.ToString());
            Events.Add(@event);
            try
            {
                switch (@event.EventType)
                {
                    case ProcessEventType.ProcessInstanceStart:
                    {
                        var inst = await _repo.FindInstanceByIdAsync(@event.SourceId);
                        Fail(inst != null, "实例行 insert 之前就看到 PROCESS_INSTANCE_START");
                        break;
                    }
                    case ProcessEventType.ProcessTaskStart:
                    {
                        var task = await _repo.FindTaskByIdAsync(@event.SourceId);
                        Fail(task != null, "任务行还没落库（saveTask 前 taskId 不可反查）");
                        Fail(task?.TaskState == (int)WfTaskState.Doing, "新待办落库后 state 应为 10");
                        // actors 直传键＝落库后的参与者集合（与 wf_process_task_actor 同源）
                        var actors = (@event.Data["actors"] as List<string>) ?? new List<string>();
                        Fail(actors.Count > 0, "actors 直传键为空（应取落库后的参与者行）");
                        break;
                    }
                    case ProcessEventType.TaskComplete:
                    case ProcessEventType.TaskReject:
                    {
                        var task = await _repo.FindTaskByIdAsync(@event.SourceId);
                        Fail(task?.TaskState == (int)WfTaskState.Finished,
                            $"{@event.EventType}：任务行 state 尚未落库为 20（实为 {task?.TaskState}）");
                        break;
                    }
                    case ProcessEventType.CcCreate:
                    {
                        CcActorIds.Add(@event.CcActorId ?? "");
                        // 读法带归属条件（issues/141 G1：零条件 ⇒ 空页），断言语义一字未改；
                        // 按本支事件自己的 ccActorId 反查，反而比原来的"任意 cc 行存在"更严。
                        var page = await _repo.PageCcInstancesAsync(
                            new PageQuery(1, 10).Add("cc.actor_id", "EQ", @event.CcActorId));
                        Fail(page.RecordCount > 0, "cc 行未落库就 fire CC_CREATE");
                        break;
                    }
                    case ProcessEventType.TaskTransfer:
                    {
                        var actors = await _repo.FindTaskActorsAsync(@event.SourceId!.Value);
                        Fail(actors.Contains((string)@event.Data["toActor"]!),
                            $"转办落库后参与者应含 {@event.Data["toActor"]}，实有 {string.Join(",", actors)}");
                        Fail(!actors.Contains((string)@event.Data["fromActor"]!),
                            $"转办落库后参与者不应再有 {@event.Data["fromActor"]}");
                        break;
                    }
                    case ProcessEventType.TaskWithdraw:
                    {
                        var inst = await _repo.FindInstanceByIdAsync(@event.SourceId);
                        Fail(inst?.State == (int)WfInstanceState.Withdraw,
                            $"撤回：实例 state 落库应为 30（实为 {inst?.State}）");
                        Fail((await _repo.FindDoingTasksAsync(@event.SourceId!.Value, null)).Count == 0,
                            "撤回：进行中任务行未随级联落库为 30");
                        break;
                    }
                    case ProcessEventType.ProcessInstanceEnd:
                        // 有意<b>不</b>在这里断写序：本文件是"可见序＋载荷键"的 recorder，对码 2 的
                        // "state 是否已落库"保持失明——这条失明是变异对照的证据（把登记挪回处理器
                        // 就地 fire，本文件仍全绿，故 <c>InstanceEndTiming132Tests</c> 的写序探针
                        // 必须独立存在）。载荷 state 的正确性由上面 Sequence 用例的
                        // <c>Assert.Equal(Finished, end.Data["state"])</c> 钉住。
                        break;
                }
            }
            catch (Exception e)
            {
                Failures.Add($"{@event.EventType}: {e.GetType().Name} {e.Message}");
            }
        }

        private void Fail(bool ok, string why)
        {
            if (!ok) Failures.Add($"{Names[^1]}: {why}");
        }
    }
}

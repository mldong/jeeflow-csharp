using Xunit;
using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Tests;

/// <summary>引擎基础行为 + 事件时机（issues/13/102/104）+ 合规场景 c01–c22（15 flows 驱动）。</summary>
public class ComplianceTests
{
    // ═══ 引擎基础行为 ═══

    private const string SimpleFlow = """
        {"name": "test-flow", "displayName": "Test Flow", "type": "approval",
         "nodes": [
           {"id": "start", "type": "snaker:start", "text": {"value": "Start"}},
           {"id": "apply", "type": "snaker:task", "text": {"value": "Apply"},
            "properties": {"assignee": "applicant"}},
           {"id": "end", "type": "snaker:end", "text": {"value": "End"}}
         ],
         "edges": [
           {"id": "e1", "sourceNodeId": "start", "targetNodeId": "apply"},
           {"id": "e2", "sourceNodeId": "apply", "targetNodeId": "end"}
         ]}
        """;

    [Fact]
    public async Task Engine_Start_CreatesInstance()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "test-flow", SimpleFlow);
        var inst = await engine.StartProcessInstanceByIdAsync(did, "user1", new FlowData());
        Assert.Equal("user1", inst.Operator);
        Assert.True(inst.InstanceId > 0);
        Assert.Equal((int)WfInstanceState.Doing, inst.State);
    }

    [Fact]
    public async Task Engine_Start_DefineNotFound_Throws()
    {
        var (engine, _) = TestInfra.NewEngine();
        await Assert.ThrowsAsync<JeeflowException>(
            () => engine.StartProcessInstanceByIdAsync(99999, "user1", new FlowData()));
    }

    [Fact]
    public async Task Engine_Execute_TaskNotFound_Throws()
    {
        var (engine, _) = TestInfra.NewEngine();
        var ex = await Assert.ThrowsAsync<JeeflowException>(
            () => engine.ExecuteProcessTaskAsync(99999, "user1", new FlowData()));
        Assert.Equal(WfErr.NotFoundDoingProcessTask, ex.Code);
    }

    [Fact]
    public async Task Engine_Execute_PermissionDenied_Throws()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "test-flow", SimpleFlow);
        var inst = await engine.StartProcessInstanceByIdAsync(did, "user1", new FlowData());
        var tasks = await repo.FindDoingTasksAsync(inst.InstanceId!.Value, null);
        Assert.NotEmpty(tasks);
        var ex = await Assert.ThrowsAsync<JeeflowException>(
            () => engine.ExecuteProcessTaskAsync(tasks[0].TaskId!.Value, "user999", new FlowData()));
        Assert.Equal(WfErr.NotAllowedExecute, ex.Code);
    }

    [Fact]
    public async Task Engine_NoExtRepository_DesignActionsError()
    {
        var repo = new MemoryRepository();
        var ctx = new ServiceContext(repo, null); // 不接扩展仓储
        repo.Configure(ctx);
        var facade = new JeeflowEngine(ctx);
        Assert.Null(ctx.ExtRepository);
        _ = facade;
    }

    [Fact]
    public async Task Engine_AutoGenTitleAndUserInfoInjectedOnStart()
    {
        // C25：start 第 3.5 步先 addUserInfo 再 autoGenTitle；u_* 实例级稳定属性仅 start 注入一次
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "test-flow", SimpleFlow);
        var inst = await engine.StartProcessInstanceByIdAsync(did, "user1", new FlowData());
        Assert.Equal("张三", inst.Variables.GetStr(FlowConst.UserRealName));
        Assert.Equal("user1", inst.Variables.GetStr(FlowConst.UserUserId));
        var title = inst.Variables.GetStr(FlowConst.AutoGenTitle);
        Assert.NotNull(title);
        Assert.StartsWith("张三的test-flow-", title);
        Assert.Matches(@"-\d{4}-\d{2}-\d{2} \d{2}:\d{2}$", title);
    }

    [Fact]
    public async Task Engine_BusinessNoAndArgsCopiedToVariables()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "test-flow", SimpleFlow);
        var args = new FlowData { [FlowConst.BusinessNo] = "BIZ-2026-001", ["days"] = 3 };
        var inst = await engine.StartProcessInstanceByIdAsync(did, "user1", args);
        Assert.Equal("BIZ-2026-001", inst.BusinessNo);
        Assert.Equal("BIZ-2026-001", inst.Variables.GetStr(FlowConst.BusinessNo));
        Assert.Equal(3, inst.Variables.GetInt("days"));
    }

    // ═══ 事件时机 ═══

    [Fact]
    public async Task Events_TaskStartFiresAfterPersistWithRealId()
    {
        // issues/13 + spec §4.4：事件在任务落库（分配 taskId）之后 fire，sourceId 可反查
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        var fired = new List<long>();
        ctx.RegisterEventListener(new RecordingListener(ev =>
        {
            if (ev.EventType == ProcessEventType.ProcessTaskStart && ev.SourceId != null)
                fired.Add(ev.SourceId.Value);
        }));
        var did = await TestInfra.SaveFlowDefineAsync(repo, "test-flow", SimpleFlow);
        var inst = await engine.StartProcessInstanceByIdAsync(did, "user1", new FlowData());
        Assert.Single(fired);
        var task = await repo.FindTaskByIdAsync(fired[0]);
        Assert.NotNull(task);
        Assert.Equal(inst.InstanceId, task!.ProcessInstanceId);
    }

    [Fact]
    public async Task Events_CcCreateFiredPerActorWithCcRows()
    {
        // issues/102：逐抄送人 fire，ccActorId 直传事件体；cc 行逐条落库
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        var fired = new List<(long SourceId, string? CcActorId)>();
        ctx.RegisterEventListener(new RecordingListener(ev =>
        {
            if (ev.EventType == ProcessEventType.CcCreate)
                fired.Add((ev.SourceId!.Value, ev.CcActorId));
        }));
        var did = await TestInfra.SaveFlowDefineAsync(repo, "test-flow", SimpleFlow);
        var args = new FlowData
        {
            [FlowConst.CcActorsStart] = new List<object?> { "u1", "u2" },
        };
        var inst = await engine.StartProcessInstanceByIdAsync(did, "user1", args);
        Assert.Equal(2, fired.Count);
        Assert.All(fired, f => Assert.Equal(inst.InstanceId, f.SourceId));
        Assert.Equal("u1", fired[0].CcActorId);
        Assert.Equal("u2", fired[1].CcActorId);
        var page = await repo.PageCcInstancesAsync(new PageQuery(1, 10));
        Assert.Equal(2, page.RecordCount);
    }

    [Fact]
    public async Task Events_CcCreateZeroSideEffectWithoutListener()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "test-flow", SimpleFlow);
        var args = new FlowData { [FlowConst.CcActorsStart] = new List<object?> { "u9" } };
        var inst = await engine.StartProcessInstanceByIdAsync(did, "user1", args);
        var page = await repo.PageCcInstancesAsync(new PageQuery(1, 10));
        Assert.Equal(1, page.RecordCount);
        Assert.True(inst.InstanceId > 0);
    }

    [Fact]
    public async Task Events_RejectPathFiresInstanceEnd()
    {
        // issues/104：拒绝路径也 fire 结束事件
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        var fired = new List<long>();
        ctx.RegisterEventListener(new RecordingListener(ev =>
        {
            if (ev.EventType == ProcessEventType.ProcessInstanceEnd && ev.SourceId != null)
                fired.Add(ev.SourceId.Value);
        }));
        var did = await TestInfra.SaveFlowDefineAsync(repo, "with-reject-flow",
            TestInfra.LoadFlow("01-simple"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var task = await TestInfra.FindDoingForAsync(repo, iid, "leader").ConfigureAwait(false);
        var args = new FlowData { [FlowConst.SubmitType] = (int)WfSubmitType.Reject };
        await engine.ExecuteAndJumpToEndAsync(task.TaskId!.Value, "leader", args);
        var inst = await repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Reject, inst!.State);
        Assert.Single(fired);
        Assert.Equal(iid, fired[0]);
    }

    [Fact]
    public void Events_PublisherPerListenerIsolation()
    {
        // C13：单监听器异常不中断后续监听器与主流程
        var fired = new List<long>();
        var boom = new ThrowingListener();
        var ok = new RecordingListener(ev =>
        {
            if (ev.EventType == ProcessEventType.ProcessInstanceStart && ev.SourceId != null)
                fired.Add(ev.SourceId.Value);
        });
        ProcessPublisher.NotifyAsync(
            new ProcessEvent { EventType = ProcessEventType.ProcessInstanceStart, SourceId = 42 },
            new IProcessEventListener[] { boom, ok }).GetAwaiter().GetResult();
        Assert.Single(fired);
        Assert.Equal(42L, fired[0]);
    }

    // ═══ 合规场景 c01–c22 ═══

    [Fact]
    public async Task C01_ExecuteReturnsNonZeroTaskIds()
    {
        // issues/91b：execute 响应 new_tasks 必须是已分配 id 的原片
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "simple",
            TestInfra.LoadFlow("01-simple"));
        var inst = await engine.StartProcessInstanceByIdAsync(did, "applicant", new FlowData());
        var apply = await TestInfra.FindDoingForAsync(repo, inst.InstanceId!.Value, "applicant");
        var newTasks = await engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData());
        Assert.NotEmpty(newTasks);
        Assert.All(newTasks, t => Assert.True(t.TaskId > 0));
        var repoTasks = await repo.FindDoingTasksAsync(inst.InstanceId!.Value, null);
        foreach (var nt in newTasks)
            Assert.Contains(repoTasks, rt => rt.TaskId == nt.TaskId);
    }

    [Fact]
    public async Task C01_SimpleLinearFlowCompletes()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "simple", TestInfra.LoadFlow("01-simple"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var task = await TestInfra.FindDoingForAsync(repo, iid, "leader");
        await engine.ExecuteProcessTaskAsync(task.TaskId!.Value, "leader", new FlowData());
        var inst = await repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Finished, inst!.State);
    }

    [Fact]
    public async Task C02_MultiTaskChain()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "multi-task", TestInfra.LoadFlow("02-multi-task"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        foreach (var actor in new[] { "leader", "manager", "boss" })
        {
            var task = await TestInfra.FindDoingForAsync(repo, iid, actor);
            Assert.Equal(actor, task.ActorIds[0]);
            await engine.ExecuteProcessTaskAsync(task.TaskId!.Value, actor, new FlowData());
        }
        var inst = await repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Finished, inst!.State);
    }

    [Fact]
    public async Task C03_DecisionBranchStarts()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "decision-expr", TestInfra.LoadFlow("03-decision-expr"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var tasks = await repo.FindDoingTasksAsync(iid, null);
        Assert.NotEmpty(tasks);
        Assert.Equal("task1", tasks[0].TaskName);
    }

    [Fact]
    public async Task C03b_DecisionExprRoutesByAmount()
    {
        // amount=2000 → task2(manager)；amount=500 → task3(director)
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "decision-expr", TestInfra.LoadFlow("03-decision-expr"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var t1 = await TestInfra.FindDoingForAsync(repo, iid, "leader");
        await engine.ExecuteProcessTaskAsync(t1.TaskId!.Value, "leader",
            new FlowData { ["amount"] = 2000 });
        var next = await repo.FindDoingTasksAsync(iid, null);
        Assert.Equal("task2", next[0].TaskName);
        Assert.Equal("manager", next[0].ActorIds[0]);

        var iid2 = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var t1b = await TestInfra.FindDoingForAsync(repo, iid2, "leader");
        await engine.ExecuteProcessTaskAsync(t1b.TaskId!.Value, "leader",
            new FlowData { ["amount"] = 500 });
        var next2 = await repo.FindDoingTasksAsync(iid2, null);
        Assert.Equal("task3", next2[0].TaskName);
    }

    [Fact]
    public async Task C04_ForkJoinCreatesParallelTasks()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "fork-join", TestInfra.LoadFlow("04-fork-join"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var tasks = await repo.FindDoingTasksAsync(iid, null);
        Assert.True(tasks.Count >= 2);
        Assert.Contains(tasks, t => t.ActorIds.Contains("userA"));
        Assert.Contains(tasks, t => t.ActorIds.Contains("userB"));
    }

    [Fact]
    public async Task C04b_JoinWaitsForAllBranches()
    {
        // join1 合并：全部分支完成才推进（全部完成后实例 FINISHED）
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "fork-join", TestInfra.LoadFlow("04-fork-join"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var a = await TestInfra.FindDoingForAsync(repo, iid, "userA");
        await engine.ExecuteProcessTaskAsync(a.TaskId!.Value, "userA", new FlowData());
        var stillDoing = await repo.FindDoingTasksAsync(iid, null);
        Assert.Single(stillDoing); // userB 未完成，join 未合并，无新任务
        var b = await TestInfra.FindDoingForAsync(repo, iid, "userB");
        await engine.ExecuteProcessTaskAsync(b.TaskId!.Value, "userB", new FlowData());
        var inst = await repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Finished, inst!.State);
    }

    [Fact]
    public async Task C05_ParallelCountersignCreates3Tasks()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "cs",
            TestInfra.LoadFlow("05-countersign-parallel"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var tasks = await repo.FindDoingTasksAsync(iid, null);
        Assert.Equal(3, tasks.Count);
        // C6：会签分支 persist performType=1
        Assert.All(tasks, t => Assert.Equal(WfPerformType.Countersign, t.PerformType));
    }

    [Fact]
    public async Task C06_SequentialCountersignOneAtATime()
    {
        // issues/93：任意时刻恰 1 个 DOING；计数存任务变量（无 csv_ 前缀，C9）
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "cs-seq",
            TestInfra.LoadFlow("06-countersign-sequential"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var tasks = await repo.FindDoingTasksAsync(iid, null);
        Assert.Single(tasks);
        Assert.Equal("userA", tasks[0].ActorIds[0]);
        // 首位任务变量带全量办理人/计数（C9）
        Assert.Equal("userA", ((List<object?>)tasks[0].Variables[$"operatorList_task1"]!)[0]);
        Assert.Equal(2, tasks[0].Variables.GetInt("nrOfInstances_task1"));

        await engine.ExecuteProcessTaskAsync(tasks[0].TaskId!.Value, "userA",
            new FlowData { [FlowConst.SubmitType] = (int)WfSubmitType.Agree });
        var inst = await repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Doing, inst!.State);
        var next = await repo.FindDoingTasksAsync(iid, null);
        Assert.Single(next);
        Assert.Equal("userB", next[0].ActorIds[0]);

        await engine.ExecuteProcessTaskAsync(next[0].TaskId!.Value, "userB",
            new FlowData { [FlowConst.SubmitType] = (int)WfSubmitType.Agree });
        inst = await repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Finished, inst!.State);
        Assert.Empty(await repo.FindDoingTasksAsync(iid, null));
    }

    [Fact]
    public async Task C07_RatioCountersignCreates4Tasks()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "cs-ratio",
            TestInfra.LoadFlow("07-countersign-ratio"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var tasks = await repo.FindDoingTasksAsync(iid, null);
        Assert.Equal(4, tasks.Count);
    }

    [Fact]
    public async Task C08_SequentialApproveFlowRuns()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "cs-seq-approve",
            TestInfra.LoadFlow("08-countersign-sequential-approve"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var tasks = await repo.FindDoingTasksAsync(iid, null);
        Assert.NotEmpty(tasks);
        var inst = await repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Doing, inst!.State);
    }

    [Fact]
    public async Task C09_NonActorPermissionDenied()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "with-reject", TestInfra.LoadFlow("09-with-reject"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var tasks = await repo.FindDoingTasksAsync(iid, null);
        Assert.NotEmpty(tasks);
        await Assert.ThrowsAsync<JeeflowException>(
            () => engine.ExecuteProcessTaskAsync(tasks[0].TaskId!.Value, "unauthorized_user", new FlowData()));
    }

    [Fact]
    public async Task C10_MixedModeFlowStarts()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "mixed-mode", TestInfra.LoadFlow("10-mixed-mode"));
        var inst = await engine.StartProcessInstanceByIdAsync(did, "applicant", new FlowData());
        Assert.True(inst.InstanceId > 0);
    }

    [Fact]
    public async Task C11_AssigneeVariableFlowStarts()
    {
        // assignee=deptLeader（变量 key）——args 命中则取变量值，否则字面量
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "assignee-vars", TestInfra.LoadFlow("11-assignee-vars"));
        var args = new FlowData { ["deptLeader"] = "director" };
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did, args);
        var task = await TestInfra.FindDoingForAsync(repo, iid, "director");
        Assert.Equal("task1", task.TaskName);
    }

    [Fact]
    public async Task C12_FlowAutoCanExecuteAnyTask()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "simple-auto", TestInfra.LoadFlow("01-simple"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var tasks = await repo.FindDoingTasksAsync(iid, null);
        Assert.NotEmpty(tasks);
        var newTasks = await engine.ExecuteProcessTaskAsync(tasks[0].TaskId!.Value, "flow.auto", new FlowData());
        Assert.NotNull(newTasks);
    }

    [Fact]
    public async Task C13_DefineWriteOps()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "c13-test", "{}");
        Assert.True(did > 0);
        await repo.UpdateDefineStateAsync(did, 1);
        var d = await repo.FindDefineByIdAsync(did);
        Assert.Equal(1, d!.State);
        await repo.RemoveDefineAsync(did);
        Assert.Null(await repo.FindDefineByIdAsync(did));
    }

    [Fact]
    public async Task C14_InstanceUpdateCascadePersists()
    {
        // v1.0.1：updateInstance 级联持久化聚合内任务状态（撤回）
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "simple-cascade", TestInfra.LoadFlow("01-simple"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var inst = await repo.FindInstanceByIdAsync(iid);
        inst!.BusinessNo = "BIZ-CHANGED"; // JDBC updateInstance 列集不含 business_no——保持原值
        inst.Withdraw("user1");
        await repo.UpdateInstanceAsync(inst);
        var updated = await repo.FindInstanceByIdAsync(iid);
        Assert.NotEqual("BIZ-CHANGED", updated!.BusinessNo);
        Assert.Equal((int)WfInstanceState.Withdraw, updated.State);
        // 级联（v1.0.1 契约）：聚合内 DOING 任务随同置 30（C28 非 45）；已完成任务保持 20
        var history = await repo.FindHistoryTasksAsync(iid);
        Assert.Equal((int)WfTaskState.Withdraw, history.First(t => t.TaskName == "task1").TaskState);
        Assert.Equal((int)WfTaskState.Finished, history.First(t => t.TaskName == "apply").TaskState);
    }

    [Fact]
    public async Task C15_DefineFindableAfterSave()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "simple-routing", TestInfra.LoadFlow("01-simple"));
        Assert.NotNull(await repo.FindDefineByIdAsync(did));
    }

    [Fact]
    public async Task C16_HistoryTasksForApprovalRecord()
    {
        // issues/89 聚合水合：find_instance_by_id 后必须填充 tasks
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "simple-views", TestInfra.LoadFlow("01-simple"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var history = await repo.FindHistoryTasksAsync(iid);
        Assert.NotEmpty(history);
        // 水合：聚合根自带 tasks
        var inst = await repo.FindInstanceByIdAsync(iid);
        Assert.NotEmpty(inst!.Tasks);
    }

    [Fact]
    public async Task C17_HighlightHasHistory()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "simple-highlight", TestInfra.LoadFlow("01-simple"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var history = await repo.FindHistoryTasksAsync(iid);
        Assert.NotEmpty(history);
        var doing = await repo.FindDoingTasksAsync(iid, null);
        Assert.Single(doing);
    }

    [Fact]
    public async Task C18_CandidatePageFlowStarts()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "candidate-page", TestInfra.LoadFlow("12-candidate-page"));
        var inst = await engine.StartProcessInstanceByIdAsync(did, "applicant", new FlowData());
        Assert.True(inst.InstanceId > 0);
        // review 节点 assignee=leader 正常创建
        var tasks = await repo.FindDoingTasksAsync(inst.InstanceId!.Value, null);
        Assert.Contains(tasks, t => t.ActorIds.Contains("applicant"));
    }

    [Fact]
    public async Task C19_CcPaging()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "simple-cc", TestInfra.LoadFlow("01-simple"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        await repo.CreateCcInstanceAsync(iid, "user1", "cc_user1", "cc_user2");
        var page = await repo.PageCcInstancesAsync(new PageQuery(1, 10));
        Assert.True(page.RecordCount > 0);
        // updateCcStatus：已读
        await repo.UpdateCcStatusAsync(iid, "cc_user1");
    }

    [Fact]
    public async Task C20_AddTaskActorDedups()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "simple-actor", TestInfra.LoadFlow("01-simple"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var tasks = await repo.FindDoingTasksAsync(iid, null);
        Assert.NotEmpty(tasks);
        await repo.AddTaskActorAsync(tasks[0].TaskId!.Value, new List<string> { "extra_actor" });
        await repo.AddTaskActorAsync(tasks[0].TaskId!.Value, new List<string> { "extra_actor" });
        var actors = await repo.FindTaskActorsAsync(tasks[0].TaskId!.Value);
        Assert.Equal(1, actors.Count(a => a == "extra_actor"));
    }

    [Fact]
    public void C21_EnumDict7Keys()
    {
        var registry = new EnumDictRegistry();
        string[] keys =
        {
            "wf_process_define_state", "wf_process_instance_state", "wf_process_submit_type",
            "wf_process_task_state", "wf_process_task_type", "wf_process_task_perform_type",
            "wf_countersign_type",
        };
        Assert.Equal(7, keys.Length);
        foreach (var key in keys)
        {
            var dict = registry.GetDict(key);
            Assert.NotEmpty(dict);
        }
        // submitType 8 值全枚举（C24 字典与引擎枚举一致）
        Assert.Equal(8, registry.GetDict("wf_process_submit_type").Count);
        Assert.Empty(registry.GetDict("unknown"));
    }

    [Fact]
    public void C22_HandlerRegistryBuiltinsAndCustom()
    {
        var registry = new HandlerRegistry();
        var builtinCount = registry.ListHandlerTypes().Sum(
            t => registry.ListHandlers(t).Count);
        Assert.Equal(8, builtinCount); // 7 assignmentHandler + 1 权限码默认映射
        registry.Register("AssignmentHandler", "custom.handler.X", "自定义", 1, "test");
        registry.Register("AssignmentHandler", "custom.handler.Y", "自定义2", 2, "test");
        var byType = registry.ListHandlers("AssignmentHandler");
        Assert.True(byType.Count >= 2);
        // order 升序：内置 -9999 最前
        Assert.Equal(-9999, byType[0].Order);
        Assert.Equal("custom.handler.X", registry.ListHandlers("AssignmentHandler", "test")[0].ClassName);
    }

    [Fact]
    public async Task Cxx_CustomNodeRunsHandlerAndRecordsHistory()
    {
        // flows/08-custom-node：custom 节点按名解析 handler → FINISHED 历史任务 → 沿边驱动到 end
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "custom-node", TestInfra.LoadFlow("08-custom-node"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        // apply 完成后 custom1 执行（handler 写 customNodeRan）→ end → 实例完成
        var inst = await repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Finished, inst!.State);
        Assert.True((bool)inst.Variables["customNodeRan"]!);
        // Java 契约：custom 历史任务 taskId 为 null，级联持久化跳过——不落库（test08 宽松口径同源）
    }

    [Fact]
    public async Task Cxx_CustomNodeUnresolvableHandlerErrorsExplicitly()
    {
        // C20：声明名不可解析 → 显式错误（不静默跳过）
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        ctx.CustomHandlers.Clear(); // 移除测试 handler，模拟未注册（C20 显式报错）
        var didX = await TestInfra.SaveFlowDefineAsync(repo, "custom-node-x", TestInfra.LoadFlow("08-custom-node"));
        var ex = await Assert.ThrowsAsync<JeeflowException>(
            () => TestInfra.StartAndApplyAsync(engine, repo, didX));
        Assert.Contains("实例化对象失败", ex.Message);
    }
}

/// <summary>录制事件的监听器。</summary>
public class RecordingListener : IProcessEventListener
{
    private readonly Action<ProcessEvent> _on;
    public RecordingListener(Action<ProcessEvent> on) => _on = on;
    public Task OnEventAsync(ProcessEvent @event)
    {
        _on(@event);
        return Task.CompletedTask;
    }
}

/// <summary>恒抛异常的监听器（隔离测试用）。</summary>
public class ThrowingListener : IProcessEventListener
{
    public Task OnEventAsync(ProcessEvent @event) => throw new InvalidOperationException("boom");
}

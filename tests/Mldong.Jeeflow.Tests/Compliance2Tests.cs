using Xunit;
using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// 合规场景 c23–c31（会签门控/08 全链/字段权限/resume 表单分配）+ submitType 9 值矩阵。
/// 门控变量注入：facade execute() 在 submitType=20 时注入 countersignDisagreeFlag=1（Java 口径），
/// 引擎级测试直接在 args 携带（等价 facade 行为）。
/// </summary>
public class Compliance2Tests
{
    private static FlowData SoftRejectArgs() => new()
    {
        [FlowConst.SubmitType] = (int)WfSubmitType.CountersignDisagree,
        [FlowConst.CountersignDisagreeFlag] = 1,
    };

    [Fact]
    public async Task C23_ParallelSoftReject()
    {
        // C8：submitType=20 默认软拒绝——任务正常完成 + flag=1，流程不阻断
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "cs-parallel-soft",
            TestInfra.LoadFlow("05-countersign-parallel"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var userA = await TestInfra.FindDoingForAsync(repo, iid, "userA");
        await engine.ExecuteProcessTaskAsync(userA.TaskId!.Value, "userA", SoftRejectArgs());
        var inst = await repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Doing, inst!.State);
        var doing = await repo.FindDoingTasksAsync(iid, null);
        Assert.Equal(2, doing.Count);
        Assert.Equal("1", inst.Variables.GetStr(FlowConst.CountersignDisagreeFlag));
        var allTasks = await repo.FindHistoryTasksAsync(iid);
        foreach (var t in allTasks)
        {
            if (t.ActorIds.Contains("userA") && t.TaskState == (int)WfTaskState.Finished)
                Assert.Equal("1", t.Variables.GetStr(FlowConst.CountersignDisagreeFlag));
        }
    }

    [Fact]
    public async Task C24_OneVoteVetoFinishesInstance()
    {
        // C8：仅 countersignCompletionCondition==ONE_VOTE_VETO 时否决生效、提前 merged；
        // merged 后废弃本节点残留 DOING（99），流程 FINISHED(20)
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "cs-one-vote-veto",
            TestInfra.LoadFlow("13-countersign-one-vote-veto"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var userA = await TestInfra.FindDoingForAsync(repo, iid, "userA");
        await engine.ExecuteProcessTaskAsync(userA.TaskId!.Value, "userA", SoftRejectArgs());
        var inst = await repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Finished, inst!.State);
        var allTasks = await repo.FindHistoryTasksAsync(iid);
        foreach (var t in allTasks)
        {
            if (t.ActorIds.Contains("userB") || t.ActorIds.Contains("userC"))
                Assert.Equal((int)WfTaskState.Abandon, t.TaskState);
            if (t.ActorIds.Contains("userA") && t.TaskName == "task1")
                Assert.Equal((int)WfTaskState.Finished, t.TaskState);
        }
        Assert.Equal("1", inst.Variables.GetStr(FlowConst.CountersignDisagreeFlag));
    }

    [Fact]
    public async Task C25_RatioExpressionMergesAt2Of4()
    {
        // 07-flow：#nrOfCompletedInstances==2 → 2/4 完成即 merged，剩余 2 废弃
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "cs-ratio",
            TestInfra.LoadFlow("07-countersign-ratio"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var userA = await TestInfra.FindDoingForAsync(repo, iid, "userA");
        await engine.ExecuteProcessTaskAsync(userA.TaskId!.Value, "userA", new FlowData());
        var inst = await repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Doing, inst!.State);
        Assert.Equal(3, (await repo.FindDoingTasksAsync(iid, null)).Count);

        var userB = await TestInfra.FindDoingForAsync(repo, iid, "userB");
        await engine.ExecuteProcessTaskAsync(userB.TaskId!.Value, "userB", new FlowData());
        inst = await repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Finished, inst!.State);
        var allTasks = await repo.FindHistoryTasksAsync(iid);
        Assert.Equal(2, allTasks.Count(t => t.TaskState == (int)WfTaskState.Abandon));
    }

    [Fact]
    public async Task C26_SoftRejectFollowUpCompletesNormally()
    {
        // 软拒绝后其余成员照常办理，全部完成流程正常结束
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "cs-soft-followup",
            TestInfra.LoadFlow("05-countersign-parallel"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var a = await TestInfra.FindDoingForAsync(repo, iid, "userA");
        await engine.ExecuteProcessTaskAsync(a.TaskId!.Value, "userA", SoftRejectArgs());
        var b = await TestInfra.FindDoingForAsync(repo, iid, "userB");
        await engine.ExecuteProcessTaskAsync(b.TaskId!.Value, "userB", new FlowData());
        var c = await TestInfra.FindDoingForAsync(repo, iid, "userC");
        await engine.ExecuteProcessTaskAsync(c.TaskId!.Value, "userC", new FlowData());
        var inst = await repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Finished, inst!.State);
    }

    [Fact]
    public async Task C27_ExecutingAbandonedTaskFails()
    {
        // 负向：一票否决后废弃任务再办理 → 99999999（任务不存在或不进行中）
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "cs-abandon-neg",
            TestInfra.LoadFlow("13-countersign-one-vote-veto"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var userA = await TestInfra.FindDoingForAsync(repo, iid, "userA");
        var userB = await TestInfra.FindDoingForAsync(repo, iid, "userB");
        var userBId = userB.TaskId!.Value;
        await engine.ExecuteProcessTaskAsync(userA.TaskId!.Value, "userA", SoftRejectArgs());
        var ex = await Assert.ThrowsAsync<JeeflowException>(
            () => engine.ExecuteProcessTaskAsync(userBId, "userB", new FlowData()));
        Assert.Equal(WfErr.NotFoundDoingProcessTask, ex.Code);
    }

    [Fact]
    public async Task C28_Flow08FullChainHardAssertions()
    {
        // 08-cs-seq-approve 全链：apply → 串行会签(userA→userB) → approve(leader) → end
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "cs-seq-08",
            TestInfra.LoadFlow("08-countersign-sequential-approve"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);

        var tasks = await repo.FindDoingTasksAsync(iid, null);
        Assert.Single(tasks);
        Assert.Equal(new List<string> { "userA" }, tasks[0].ActorIds);
        Assert.Equal((int)WfInstanceState.Doing,
            (await repo.FindInstanceByIdAsync(iid))!.State);
        await engine.ExecuteProcessTaskAsync(tasks[0].TaskId!.Value, "userA", new FlowData());

        tasks = await repo.FindDoingTasksAsync(iid, null);
        Assert.Single(tasks);
        Assert.Equal(new List<string> { "userB" }, tasks[0].ActorIds);
        await engine.ExecuteProcessTaskAsync(tasks[0].TaskId!.Value, "userB", new FlowData());

        // 会签全通过才推进到 approve（leader）
        tasks = await repo.FindDoingTasksAsync(iid, null);
        Assert.Single(tasks);
        Assert.Equal(new List<string> { "leader" }, tasks[0].ActorIds);
        Assert.Equal("approve", tasks[0].TaskName);
        await engine.ExecuteProcessTaskAsync(tasks[0].TaskId!.Value, "leader", new FlowData());

        Assert.Empty(await repo.FindDoingTasksAsync(iid, null));
        Assert.Equal((int)WfInstanceState.Finished,
            (await repo.FindInstanceByIdAsync(iid))!.State);
    }

    [Fact]
    public async Task C29_JumpToEndWithRejectFinishes45()
    {
        // Java 口径：executeAndJumpToEnd 由 facade submitType=2 REJECT 路由，
        // EndProcessHandler 读 submitType=2 → 实例 45
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "cs-jump-flag",
            TestInfra.LoadFlow("13-countersign-one-vote-veto"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var userA = await TestInfra.FindDoingForAsync(repo, iid, "userA");
        var args = new FlowData
        {
            [FlowConst.SubmitType] = (int)WfSubmitType.Reject,
            [FlowConst.CountersignDisagreeFlag] = 1,
        };
        await engine.ExecuteAndJumpToEndAsync(userA.TaskId!.Value, "userA", args);
        var inst = await repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Reject, inst!.State);
        // flag 经 args 落实例变量
        Assert.Equal("1", inst.Variables.GetStr(FlowConst.CountersignDisagreeFlag));
        var allTasks = await repo.FindHistoryTasksAsync(iid);
        foreach (var t in allTasks)
        {
            if (t.TaskId == userA.TaskId)
                Assert.Equal("1", t.Variables.GetStr(FlowConst.CountersignDisagreeFlag));
        }
    }

    [Fact]
    public async Task C31_FormFieldAssigneeOnResume()
    {
        // L3 S11-B：FormFieldAssigneeHandler——f_approver= u0011 → approver 节点处理人 u0011
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        var flow = """
            {"name": "s11b", "displayName": "S11B", "type": "approval",
             "nodes": [
               {"id": "start", "type": "snaker:start", "text": {"value": "Start"}},
               {"id": "apply", "type": "snaker:task", "text": {"value": "Apply"},
                "properties": {"assignee": "applicant"}},
               {"id": "approver", "type": "snaker:task", "text": {"value": "Approver"},
                "properties": {"assignmentHandler": "com.mldong.jeeflow.interceptor.impl.FormFieldAssigneeHandler"}},
               {"id": "end", "type": "snaker:end", "text": {"value": "End"}}
             ],
             "edges": [
               {"id": "e1", "sourceNodeId": "start", "targetNodeId": "apply"},
               {"id": "e2", "sourceNodeId": "apply", "targetNodeId": "approver"},
               {"id": "e3", "sourceNodeId": "approver", "targetNodeId": "end"}
             ]}
            """;
        var did = await TestInfra.SaveFlowDefineAsync(repo, "s11b", flow);
        var args = new FlowData { ["f_approver"] = "u0011" };
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did, args);
        var after = await repo.FindDoingTasksAsync(iid, null);
        Assert.Contains(after, t => t.ActorIds.Contains("u0011"));
        _ = ctx;
    }

    // ═══ submitType 9 值矩阵（引擎路径级；facade 分发语义在 M3 契约测试复核）═══

    [Fact]
    public void SubmitTypeMatrix_FromCode9Values()
    {
        Assert.Equal(WfSubmitType.Apply, (WfSubmitType)0);
        Assert.Equal(WfSubmitType.Agree, (WfSubmitType)1);
        Assert.Equal(WfSubmitType.Reject, (WfSubmitType)2);
        Assert.Equal(WfSubmitType.Rollback, (WfSubmitType)3);
        Assert.Equal(WfSubmitType.Jump, (WfSubmitType)4);
        Assert.Equal(WfSubmitType.ReApply, (WfSubmitType)5);
        Assert.Equal(WfSubmitType.RollbackToOperator, (WfSubmitType)6);
        Assert.Equal(WfSubmitType.Transfer, (WfSubmitType)7);          // issues/115 转办留痕
        Assert.Equal(WfSubmitType.CountersignDisagree, (WfSubmitType)20);
        Assert.False(System.Enum.IsDefined(typeof(WfSubmitType), 99));
    }

    [Fact]
    public async Task SubmitType2_RejectJumpsToEnd_State45()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "st2", TestInfra.LoadFlow("01-simple"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var task = await TestInfra.FindDoingForAsync(repo, iid, "leader");
        await engine.ExecuteAndJumpToEndAsync(task.TaskId!.Value, "leader",
            new FlowData { [FlowConst.SubmitType] = (int)WfSubmitType.Reject });
        var inst = await repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Reject, inst!.State);
    }

    [Fact]
    public async Task SubmitType3_RollbackToPreviousStep_NewTodoForOperator()
    {
        // issues/121 P2 血缘版（对齐 Java 参考实现）：task2 退回 → 复活 task1 那条历史行，
        // actor＝该行原办结人 leader（不是执行回退的 manager），实例保持 DOING(10)
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "st3", TestInfra.LoadFlow("02-multi-task"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var leader = await TestInfra.FindDoingForAsync(repo, iid, "leader");
        await engine.ExecuteProcessTaskAsync(leader.TaskId!.Value, "leader", new FlowData());
        var manager = await TestInfra.FindDoingForAsync(repo, iid, "manager");
        await engine.ExecuteAndJumpTaskAsync(manager.TaskId!.Value, "manager", new FlowData(), null);
        var tasks = await repo.FindDoingTasksAsync(iid, null);
        Assert.NotEmpty(tasks);
        Assert.Equal("task1", tasks[0].TaskName);
        Assert.Contains("leader", tasks[0].ActorIds);
        Assert.DoesNotContain("manager", tasks[0].ActorIds);
        Assert.Equal((int)WfInstanceState.Doing,
            (await repo.FindInstanceByIdAsync(iid))!.State);
    }

    [Fact]
    public async Task SubmitType4_JumpToNamedTaskNode()
    {
        // JUMP task1 → 跳转目标节点新待办；首任务节点强制 assignee=发起人（C28）
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "st4", TestInfra.LoadFlow("02-multi-task"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var leader = await TestInfra.FindDoingForAsync(repo, iid, "leader");
        await engine.ExecuteAndJumpTaskAsync(leader.TaskId!.Value, "leader", new FlowData(), "task3");
        var tasks = await repo.FindDoingTasksAsync(iid, null);
        Assert.Single(tasks);
        Assert.Contains("boss", tasks[0].ActorIds);

        // 跳转到首任务节点 apply → assignee=发起人 applicant
        var iid2 = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var leader2 = await TestInfra.FindDoingForAsync(repo, iid2, "leader");
        await engine.ExecuteAndJumpTaskAsync(leader2.TaskId!.Value, "leader", new FlowData(), "apply");
        var tasks2 = await repo.FindDoingTasksAsync(iid2, null);
        Assert.Single(tasks2);
        Assert.Equal(new List<string> { "applicant" }, tasks2[0].ActorIds);
    }

    [Fact]
    public async Task SubmitType4_JumpInvalidNodeName_ExplicitError()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "st4n", TestInfra.LoadFlow("02-multi-task"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var leader = await TestInfra.FindDoingForAsync(repo, iid, "leader");
        var ex = await Assert.ThrowsAsync<JeeflowException>(
            () => engine.ExecuteAndJumpTaskAsync(leader.TaskId!.Value, "leader",
                new FlowData(), "no-such-node"));
        Assert.Contains("无法找到节点模型", ex.Message);
    }

    [Fact]
    public async Task SubmitType6_RollbackToOperator_FirstTaskNodeRecreatedForInitiator()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "st6", TestInfra.LoadFlow("02-multi-task"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        foreach (var actor in new[] { "leader", "manager" })
        {
            var t = await TestInfra.FindDoingForAsync(repo, iid, actor);
            await engine.ExecuteProcessTaskAsync(t.TaskId!.Value, actor, new FlowData());
        }
        var boss = await TestInfra.FindDoingForAsync(repo, iid, "boss");
        await engine.ExecuteAndJumpToFirstTaskNodeAsync(boss.TaskId!.Value, "boss", new FlowData());
        var tasks = await repo.FindDoingTasksAsync(iid, null);
        Assert.Single(tasks);
        Assert.Equal("apply", tasks[0].TaskName);
        Assert.Equal(new List<string> { "applicant" }, tasks[0].ActorIds);
        Assert.Equal((int)WfInstanceState.Doing,
            (await repo.FindInstanceByIdAsync(iid))!.State);
    }

    [Fact]
    public async Task SubmitType015_PlainExecuteDefaultPath()
    {
        // 0 APPLY / 1 AGREE / 5 RE_APPLY 都走普通完成
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "st105", TestInfra.LoadFlow("02-multi-task"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        foreach (var (actor, st) in new[] { ("leader", 0), ("manager", 1), ("boss", 5) })
        {
            var task = await TestInfra.FindDoingForAsync(repo, iid, actor);
            await engine.ExecuteProcessTaskAsync(task.TaskId!.Value, actor,
                new FlowData { [FlowConst.SubmitType] = st });
        }
        Assert.Equal((int)WfInstanceState.Finished,
            (await repo.FindInstanceByIdAsync(iid))!.State);
    }

    [Fact]
    public async Task SubmitType_Negative_NonActorRejected()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "stneg", TestInfra.LoadFlow("01-simple"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var task = await TestInfra.FindDoingForAsync(repo, iid, "leader");
        var ex = await Assert.ThrowsAsync<JeeflowException>(
            () => engine.ExecuteProcessTaskAsync(task.TaskId!.Value, "intruder", new FlowData()));
        Assert.Equal(WfErr.NotAllowedExecute, ex.Code);
    }

    [Fact]
    public async Task TfNextNodeOperatorOverridesAssignee()
    {
        // Java RE_APPLY 断言：tf_nextNodeOperator 覆盖下一节点处理人（C25 链路）
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "st-next-op", TestInfra.LoadFlow("02-multi-task"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var leader = await TestInfra.FindDoingForAsync(repo, iid, "leader");
        await engine.ExecuteProcessTaskAsync(leader.TaskId!.Value, "leader",
            new FlowData { [FlowConst.NextNodeOperator] = "director" });
        var tasks = await repo.FindDoingTasksAsync(iid, null);
        Assert.Equal("task2", tasks[0].TaskName);
        Assert.Equal(new List<string> { "director" }, tasks[0].ActorIds);
    }

    [Fact]
    public async Task Withdraw_Instance30Tasks30()
    {
        // C28：withdraw → 实例 30 + 任务 30（非 45）
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "wd", TestInfra.LoadFlow("01-simple"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var inst = await repo.FindInstanceByIdAsync(iid);
        inst!.Withdraw("user1");
        await repo.UpdateInstanceAsync(inst);
        var updated = await repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Withdraw, updated!.State);
        var history = await repo.FindHistoryTasksAsync(iid);
        Assert.Equal((int)WfTaskState.Withdraw, history.First(t => t.TaskName == "task1").TaskState);
        Assert.Equal((int)WfTaskState.Finished, history.First(t => t.TaskName == "apply").TaskState);
    }
}

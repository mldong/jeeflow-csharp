namespace Mldong.Jeeflow.Tests;

using Xunit;
using Mldong.Jeeflow.Core;

/// <summary>
/// 「退回修改、重提直返」（submitType=6 退发起人 → 申请待办带 <c>_reapplyContext</c> →
/// submitType=5 重提直接回到来源节点）验收，对齐 java ReturnToSourceTest 的用例面：
/// 普通直返、非法 submitType、条件分支、并行汇合等待（两种完成顺序）、多分支分别退回、
/// 重复退回、回归（未配置直返的流不变）、保留键伪造与非法配置。
/// </summary>
public class ReturnToSourceTests
{
    private const int Return = (int)WfSubmitType.RollbackToOperator; // 6 退发起人
    private const int Reapply = (int)WfSubmitType.ReApply;           // 5 重新提交
    private const int Agree = (int)WfSubmitType.Agree;               // 1 同意

    private static FlowData Submit(int type) => new() { [FlowConst.SubmitType] = type };

    private static async Task<List<ProcessTask>> DoingAsync(MemoryRepository repo, long iid) =>
        await repo.FindDoingTasksAsync(iid, null);

    private static async Task<List<ProcessTask>> HistoryOfAsync(
        MemoryRepository repo, long iid, string taskName) =>
        (await repo.FindHistoryTasksAsync(iid)).Where(t => t.TaskName == taskName).ToList();

    private static async Task<long> StartAsync(
        JeeflowEngine engine, MemoryRepository repo, string defineName, string fixture,
        FlowData? startArgs = null)
    {
        var did = await TestInfra.SaveFlowDefineAsync(repo, defineName, TestInfra.LoadFlow(fixture));
        return await TestInfra.StartAndApplyAsync(engine, repo, did, startArgs);
    }

    // ═══ 普通直返 + 重提 ═══

    [Fact]
    public async Task SimpleDirectReturnThenReapply()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var iid = await StartAsync(engine, repo, "rts14", "14-return-to-source");
        var taskA = await TestInfra.FindDoingForAsync(repo, iid, "userA");

        await engine.ExecuteAndJumpToFirstTaskNodeAsync(taskA.TaskId!.Value, "userA", Submit(Return));

        // 来源任务完成并留痕；实例仍是同一个且进行中
        var finishedA = await repo.FindTaskByIdAsync(taskA.TaskId);
        Assert.True(finishedA!.IsFinished());
        Assert.Equal(Return, finishedA.Variables.GetInt(FlowConst.SubmitType) ?? -1);
        Assert.Equal((int)WfInstanceState.Doing, (await repo.FindInstanceByIdAsync(iid))!.State);

        // 生成申请待办并携带直返上下文；不生成后续节点任务
        var apply = await TestInfra.FindDoingForAsync(repo, iid, "applicant");
        Assert.Equal("apply", apply.TaskName);
        var ctx = ReapplySupport.ReapplyOf(apply);
        Assert.NotNull(ctx);
        Assert.Equal(taskA.TaskId.Value.ToString(), ctx!["sourceTaskId"]?.ToString());
        Assert.Equal("taskA", ctx["sourceNode"]?.ToString());
        Assert.Equal("RETURN_TO_SOURCE", ctx["mode"]?.ToString());
        Assert.Empty(ReapplySupport.BranchOf(ctx));   // 无 fork–join 的流没有分支归属
        var doing = await DoingAsync(repo, iid);
        Assert.Single(doing);
        Assert.DoesNotContain(doing, t => t.TaskName == "taskB");

        // 重提：直接回到来源节点，不经过来源之前的节点
        await engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", Submit(Reapply));
        Assert.True((await repo.FindTaskByIdAsync(apply.TaskId))!.IsFinished());
        var newA = await TestInfra.FindDoingForAsync(repo, iid, "userA");
        Assert.Equal("taskA", newA.TaskName);
        Assert.Equal(apply.TaskId, newA.ParentTaskId);   // 新来源任务的血缘指向本轮申请任务
        doing = await DoingAsync(repo, iid);
        Assert.Single(doing);
        Assert.DoesNotContain(doing, t => t.TaskName == "taskB");
        Assert.Equal(2, (await HistoryOfAsync(repo, iid, "taskA")).Count);   // 旧来源行保留

        // 新来源任务办理通过后继续后续流程
        await engine.ExecuteProcessTaskAsync(newA.TaskId!.Value, "userA", Submit(Agree));
        var taskB = await TestInfra.FindDoingForAsync(repo, iid, "userB");
        await engine.ExecuteProcessTaskAsync(taskB.TaskId!.Value, "userB", Submit(Agree));
        Assert.Equal((int)WfInstanceState.Finished, (await repo.FindInstanceByIdAsync(iid))!.State);
    }

    [Fact]
    public async Task ReapplyTaskRejectsOtherSubmitTypes()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var iid = await StartAsync(engine, repo, "rts14b", "14-return-to-source");
        var taskA = await TestInfra.FindDoingForAsync(repo, iid, "userA");
        await engine.ExecuteAndJumpToFirstTaskNodeAsync(taskA.TaskId!.Value, "userA", Submit(Return));
        var apply = await TestInfra.FindDoingForAsync(repo, iid, "applicant");

        // submitType=1 不得绕过直返通道
        var ex = await Assert.ThrowsAsync<JeeflowException>(
            () => engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", Submit(Agree)));
        Assert.Contains("重新提交", ex.Message);

        // 拒绝后申请待办仍可处理，且未建出来源任务
        Assert.Contains(await DoingAsync(repo, iid), t => t.TaskName == "apply");
        Assert.DoesNotContain(await DoingAsync(repo, iid), t => t.TaskName == "taskA");
    }

    [Fact]
    public async Task ReapplyTaskRequiresActorPermission()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var iid = await StartAsync(engine, repo, "rts14c", "14-return-to-source");
        var taskA = await TestInfra.FindDoingForAsync(repo, iid, "userA");
        await engine.ExecuteAndJumpToFirstTaskNodeAsync(taskA.TaskId!.Value, "userA", Submit(Return));
        var apply = await TestInfra.FindDoingForAsync(repo, iid, "applicant");

        await Assert.ThrowsAsync<JeeflowException>(
            () => engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "userB", Submit(Reapply)));
        Assert.Contains(await DoingAsync(repo, iid), t => t.TaskName == "apply");
    }

    // ═══ 条件分支：不重算来源前条件 ═══

    [Fact]
    public async Task DecisionBeforeSourceIsNotReevaluated()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var iid = await StartAsync(engine, repo, "rts16", "16-return-to-source-decision",
            new FlowData { ["amount"] = 5000 });

        var high = await TestInfra.FindDoingForAsync(repo, iid, "userH");
        Assert.Equal("taskHigh", high.TaskName);
        await engine.ExecuteAndJumpToFirstTaskNodeAsync(high.TaskId!.Value, "userH", Submit(Return));

        // 申请人把条件字段改成 100，重提仍回 taskHigh（不重算来源前的条件）
        var apply = await TestInfra.FindDoingForAsync(repo, iid, "applicant");
        var reapplyArgs = Submit(Reapply);
        reapplyArgs["amount"] = 100;
        await engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", reapplyArgs);

        var again = await TestInfra.FindDoingForAsync(repo, iid, "userH");
        Assert.Equal("taskHigh", again.TaskName);
        Assert.DoesNotContain(await DoingAsync(repo, iid), t => t.TaskName == "taskLow");
        Assert.DoesNotContain(await DoingAsync(repo, iid), t => t.TaskName == "apply");

        // 来源之后的条件节点按正常数据求值：level>1 走 taskX
        var after = Submit(Agree);
        after["level"] = 2;
        await engine.ExecuteProcessTaskAsync(again.TaskId!.Value, "userH", after);
        Assert.Contains(await DoingAsync(repo, iid), t => t.TaskName == "taskX");
        Assert.DoesNotContain(await DoingAsync(repo, iid), t => t.TaskName == "taskY");
    }

    // ═══ 并行分支：汇合等待 ═══

    [Fact]
    public async Task ForkJoinBranchScopesComputedAndBackfilled()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var iid = await StartAsync(engine, repo, "rts15s", "15-return-to-source-fork");
        var taskA = await TestInfra.FindDoingForAsync(repo, iid, "userA");
        var taskB = await TestInfra.FindDoingForAsync(repo, iid, "userB");

        // 分支根：round=父任务（apply），root=自身（落库后回填，不是 0）
        var scopesA = ReapplySupport.ScopesOf(taskA);
        var scopesB = ReapplySupport.ScopesOf(taskB);
        Assert.Single(scopesA);
        Assert.Single(scopesB);
        Assert.Equal("fork1", scopesA[0]["fork"]);
        Assert.Equal("join1", scopesA[0]["join"]);
        Assert.Equal(taskA.ParentTaskId!.Value, ReapplySupport.AsLong(scopesA[0]["round"]));
        Assert.Equal(taskA.TaskId!.Value, ReapplySupport.AsLong(scopesA[0]["root"]));
        Assert.Equal(taskB.ParentTaskId!.Value, ReapplySupport.AsLong(scopesB[0]["round"]));
        Assert.Equal(taskB.TaskId!.Value, ReapplySupport.AsLong(scopesB[0]["root"]));
    }

    [Fact]
    public async Task JoinWaitsWhileApplicantEditing()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var iid = await StartAsync(engine, repo, "rts15a", "15-return-to-source-fork");
        var taskA = await TestInfra.FindDoingForAsync(repo, iid, "userA");
        var taskB = await TestInfra.FindDoingForAsync(repo, iid, "userB");

        await engine.ExecuteAndJumpToFirstTaskNodeAsync(taskA.TaskId!.Value, "userA", Submit(Return));
        Assert.Contains(await DoingAsync(repo, iid), t => t.TaskName == "apply");
        Assert.Contains(await DoingAsync(repo, iid), t => t.TaskName == "taskB");   // 另一分支可继续

        // 另一分支办完到汇合点：申请人还在修改，不得放行汇合
        await engine.ExecuteProcessTaskAsync(taskB.TaskId!.Value, "userB", Submit(Agree));
        Assert.DoesNotContain(await DoingAsync(repo, iid), t => t.TaskName == "taskC");
        Assert.Equal((int)WfInstanceState.Doing, (await repo.FindInstanceByIdAsync(iid))!.State);

        // 重提只创建新 taskA，不重建 taskB、不重触发分叉
        var apply = await TestInfra.FindDoingForAsync(repo, iid, "applicant");
        await engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", Submit(Reapply));
        var newA = await TestInfra.FindDoingForAsync(repo, iid, "userA");
        Assert.DoesNotContain(await DoingAsync(repo, iid), t => t.TaskName == "taskB");
        Assert.Single(await HistoryOfAsync(repo, iid, "taskB"));
        Assert.DoesNotContain(await DoingAsync(repo, iid), t => t.TaskName == "taskC");

        // 新来源任务办完才汇合，且只放行一次
        await engine.ExecuteProcessTaskAsync(newA.TaskId!.Value, "userA", Submit(Agree));
        var doing = await DoingAsync(repo, iid);
        Assert.Single(doing);
        Assert.Equal("taskC", doing[0].TaskName);
        Assert.Single(await HistoryOfAsync(repo, iid, "taskC"));
    }

    [Fact]
    public async Task JoinAfterSourceCompletesFirst()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var iid = await StartAsync(engine, repo, "rts15b", "15-return-to-source-fork");
        var taskA = await TestInfra.FindDoingForAsync(repo, iid, "userA");
        var taskB = await TestInfra.FindDoingForAsync(repo, iid, "userB");

        await engine.ExecuteAndJumpToFirstTaskNodeAsync(taskA.TaskId!.Value, "userA", Submit(Return));
        var apply = await TestInfra.FindDoingForAsync(repo, iid, "applicant");
        await engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", Submit(Reapply));
        var newA = await TestInfra.FindDoingForAsync(repo, iid, "userA");

        // 来源分支先办完：另一支未办完，不得先放行
        await engine.ExecuteProcessTaskAsync(newA.TaskId!.Value, "userA", Submit(Agree));
        Assert.DoesNotContain(await DoingAsync(repo, iid), t => t.TaskName == "taskC");

        await engine.ExecuteProcessTaskAsync(taskB.TaskId!.Value, "userB", Submit(Agree));
        var doing = await DoingAsync(repo, iid);
        Assert.Single(doing);
        Assert.Equal("taskC", doing[0].TaskName);
        Assert.Single(await HistoryOfAsync(repo, iid, "taskC"));
    }

    [Fact]
    public async Task TwoBranchesReturnIndependently()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var iid = await StartAsync(engine, repo, "rts15c", "15-return-to-source-fork");
        var taskA = await TestInfra.FindDoingForAsync(repo, iid, "userA");
        var taskB = await TestInfra.FindDoingForAsync(repo, iid, "userB");

        await engine.ExecuteAndJumpToFirstTaskNodeAsync(taskA.TaskId!.Value, "userA", Submit(Return));
        await engine.ExecuteAndJumpToFirstTaskNodeAsync(taskB.TaskId!.Value, "userB", Submit(Return));

        var applies = await repo.FindDoingTasksAsync(iid, new[] { "apply" });
        Assert.Equal(2, applies.Count);
        long? ApplyFor(string sourceNode) => applies.FirstOrDefault(t =>
            ReapplySupport.ReapplyOf(t)?["sourceNode"]?.ToString() == sourceNode)?.TaskId;
        var applyA = ApplyFor("taskA");
        var applyB = ApplyFor("taskB");
        Assert.NotNull(applyA);
        Assert.NotNull(applyB);

        // 只恢复 A 的来源：B 仍在等待
        await engine.ExecuteProcessTaskAsync(applyA!.Value, "applicant", Submit(Reapply));
        Assert.Single(await repo.FindDoingTasksAsync(iid, new[] { "apply" }));
        var newA = await TestInfra.FindDoingForAsync(repo, iid, "userA");
        await engine.ExecuteProcessTaskAsync(newA.TaskId!.Value, "userA", Submit(Agree));
        Assert.DoesNotContain(await DoingAsync(repo, iid), t => t.TaskName == "taskC");

        await engine.ExecuteProcessTaskAsync(applyB!.Value, "applicant", Submit(Reapply));
        var newB = await TestInfra.FindDoingForAsync(repo, iid, "userB");
        await engine.ExecuteProcessTaskAsync(newB.TaskId!.Value, "userB", Submit(Agree));
        var doing = await DoingAsync(repo, iid);
        Assert.Single(doing);
        Assert.Equal("taskC", doing[0].TaskName);
    }

    [Fact]
    public async Task ConcurrentReapplySucceedsOnce()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var iid = await StartAsync(engine, repo, "rts14e", "14-return-to-source");
        await engine.ExecuteAndJumpToFirstTaskNodeAsync(
            (await TestInfra.FindDoingForAsync(repo, iid, "userA")).TaskId!.Value, "userA", Submit(Return));
        var applyId = (await TestInfra.FindDoingForAsync(repo, iid, "applicant")).TaskId!.Value;

        var outcomes = await Task.WhenAll(
            AttemptReapplyAsync(engine, applyId),
            AttemptReapplyAsync(engine, applyId));

        // 同实例串行化（InstanceLock ＋ 命令闸门）：恰一次成功、一次看到"已不是进行中"
        Assert.Single(outcomes, ok => ok);
        var doing = await DoingAsync(repo, iid);
        Assert.Single(doing);
        Assert.Equal("taskA", doing[0].TaskName);
        Assert.Single(await repo.FindDoingTasksAsync(iid, new[] { "taskA" }));
    }

    private static async Task<bool> AttemptReapplyAsync(JeeflowEngine engine, long applyId)
    {
        try
        {
            await engine.ExecuteProcessTaskAsync(applyId, "applicant", Submit(Reapply));
            return true;
        }
        catch (JeeflowException)
        {
            return false;
        }
    }

    // ═══ 重复退回 / 回归 / 保留键 / 非法配置 ═══

    [Fact]
    public async Task RepeatedReturnBindsItsOwnSource()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var iid = await StartAsync(engine, repo, "rts14d", "14-return-to-source");
        await engine.ExecuteAndJumpToFirstTaskNodeAsync(
            (await TestInfra.FindDoingForAsync(repo, iid, "userA")).TaskId!.Value, "userA", Submit(Return));
        await engine.ExecuteProcessTaskAsync(
            (await TestInfra.FindDoingForAsync(repo, iid, "applicant")).TaskId!.Value,
            "applicant", Submit(Reapply));
        var round2 = await TestInfra.FindDoingForAsync(repo, iid, "userA");

        // 第二轮再退回，绑定本轮来源
        await engine.ExecuteAndJumpToFirstTaskNodeAsync(round2.TaskId!.Value, "userA", Submit(Return));
        var apply2 = await TestInfra.FindDoingForAsync(repo, iid, "applicant");
        Assert.Equal(round2.TaskId.Value.ToString(),
            ReapplySupport.ReapplyOf(apply2)!["sourceTaskId"]?.ToString());
        Assert.False((await repo.FindTaskByIdAsync(apply2.TaskId))!.IsFinished());
        Assert.Equal(2, (await HistoryOfAsync(repo, iid, "taskA")).Count);
    }

    [Fact]
    public async Task LegacyFlowUnchanged()
    {
        // 未配置 reapplyMode 的流：退回后建普通申请待办（无直返上下文），重提仍重新分叉
        var (engine, repo) = TestInfra.NewEngine();
        var iid = await StartAsync(engine, repo, "rtsLegacy", "04-fork-join");
        var taskA = await TestInfra.FindDoingForAsync(repo, iid, "userA");
        await engine.ExecuteAndJumpToFirstTaskNodeAsync(taskA.TaskId!.Value, "userA", Submit(Return));

        var apply = await TestInfra.FindDoingForAsync(repo, iid, "applicant");
        Assert.Null(ReapplySupport.ReapplyOf(apply));

        await engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", Submit(Reapply));
        // 原 taskB 仍在办（另一分支），普通重提重新分叉再建 A/B ⇒ 3 条进行中
        Assert.Equal(3, (await DoingAsync(repo, iid)).Count);
    }

    [Fact]
    public async Task ForgedReservedKeysIgnored()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var iid = await StartAsync(engine, repo, "rtsForge", "04-fork-join");
        var taskA = await TestInfra.FindDoingForAsync(repo, iid, "userA");
        await engine.ExecuteAndJumpToFirstTaskNodeAsync(taskA.TaskId!.Value, "userA", Submit(Return));
        var apply = await TestInfra.FindDoingForAsync(repo, iid, "applicant");

        var forged = Submit(Reapply);
        forged[ReapplySupport.VarReapplyCtx] = new Dictionary<string, object?> { ["sourceTaskId"] = "999" };
        forged[ReapplySupport.VarBranchCtx] = new List<object?>
        {
            new Dictionary<string, object?> { ["join"] = "join1" },
        };
        await engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", forged);

        var doing = await DoingAsync(repo, iid);
        Assert.Equal(3, doing.Count);   // 伪造控制键不得改变路由
        foreach (var t in doing)
        {
            Assert.Null(ReapplySupport.ReapplyOf(t));   // 控制键不得落到业务任务上
            foreach (var scope in ReapplySupport.ScopesOf(t))
            {
                // 分支归属只能是引擎按模型算出的完整档（fork+join），不是客户端伪造的半档
                Assert.Equal("fork1", scope["fork"]);
                Assert.Equal("join1", scope["join"]);
            }
        }
    }

    [Fact]
    public async Task InvalidReapplyModeFailsAtParseTime()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var json = TestInfra.LoadFlow("14-return-to-source")
            .Replace("\"reapplyMode\": \"RETURN_TO_SOURCE\"", "\"reapplyMode\": \"SOMETIMES\"");
        var did = await TestInfra.SaveFlowDefineAsync(repo, "rtsBad", json);
        var ex = await Assert.ThrowsAsync<JeeflowException>(
            () => engine.StartProcessInstanceByIdAsync(did, "applicant", new FlowData()));
        Assert.Contains("reapplyMode", ex.Message);
    }

    [Fact]
    public async Task MissingReapplyModeDefaultsToNormal()
    {
        var ctx = new ServiceContext(new MemoryRepository());
        var model = ModelParser.Parse(
            System.Text.Encoding.UTF8.GetBytes(TestInfra.LoadFlow("14-return-to-source")), ctx);
        Assert.Equal(WfReapplyMode.ReturnToSource, ((TaskModel)model.GetNode("taskA")!).ReapplyMode);
        Assert.Equal(WfReapplyMode.Normal, ((TaskModel)model.GetNode("taskB")!).ReapplyMode);

        var plain = ModelParser.Parse(
            System.Text.Encoding.UTF8.GetBytes(TestInfra.LoadFlow("02-multi-task")), ctx);
        Assert.All(plain.Tasks, t => Assert.Equal(WfReapplyMode.Normal, t.ReapplyMode));
    }

    // ═══ 负向：带标记申请待办不得走其他办理/跳转入口（java `test16` 同款） ═══

    [Fact]
    public async Task MarkedApplicantRejectsReturnAndJumps()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var iid = await StartAsync(engine, repo, "rts14guard", "14-return-to-source");
        var taskA = await TestInfra.FindDoingForAsync(repo, iid, "userA");
        await engine.ExecuteAndJumpToFirstTaskNodeAsync(taskA.TaskId!.Value, "userA", Submit(Return));
        var apply = await TestInfra.FindDoingForAsync(repo, iid, "applicant");
        var doingBefore = (await DoingAsync(repo, iid)).Count;
        var histBefore = (await repo.FindHistoryTasksAsync(iid)).Count;

        async Task AssertMarked(Func<Task> act)
        {
            var ex = await Assert.ThrowsAsync<JeeflowException>(act);
            Assert.Contains("只接受重新提交", ex.Message);
        }

        await AssertMarked(() => engine.ExecuteAndJumpToFirstTaskNodeAsync(
            apply.TaskId!.Value, "applicant", Submit(Return)));
        await AssertMarked(() => engine.ExecuteAndJumpTaskAsync(
            apply.TaskId!.Value, "applicant", Submit((int)WfSubmitType.Rollback), null));
        await AssertMarked(() => engine.ExecuteAndJumpTaskAsync(
            apply.TaskId!.Value, "applicant", Submit((int)WfSubmitType.Jump), "taskB"));
        await AssertMarked(() => engine.ExecuteAndJumpToEndAsync(
            apply.TaskId!.Value, "applicant", Submit((int)WfSubmitType.Reject)));

        // 四个入口都在状态变更之前拒绝：不新增待办、不新增历史行、原等待上下文与来源完成态保留
        Assert.Equal(doingBefore, (await DoingAsync(repo, iid)).Count);
        Assert.Equal(histBefore, (await repo.FindHistoryTasksAsync(iid)).Count);
        var still = await repo.FindTaskByIdAsync(apply.TaskId);
        Assert.True(still!.IsDoing());
        Assert.Equal(taskA.TaskId.Value.ToString(),
            ReapplySupport.ReapplyOf(still)!["sourceTaskId"]?.ToString());
        Assert.Single(await HistoryOfAsync(repo, iid, "taskA"));

        // 正常重提仍然可用
        await engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", Submit(Reapply));
        Assert.Contains(await DoingAsync(repo, iid), t => t.TaskName == "taskA");
    }
}

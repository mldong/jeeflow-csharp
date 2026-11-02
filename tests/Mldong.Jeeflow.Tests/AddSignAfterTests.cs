namespace Mldong.Jeeflow.Tests;

using Xunit;
using Mldong.Jeeflow.Core;

/// <summary>
/// 「后加签（可重复）」验收（移植基准＝jeeflow-java <c>AddSignAfterTest</c> 的 16 格，语义逐格对齐）：
/// 链式续办、多选等同圈兄弟等待、四类写入口报错且<b>零写入</b>、三条解析期硬校验、
/// 拒绝/退回发起人废弃未完成加签、回归（不开开关逐字不变）、并发只放行一次、
/// <c>tf_</c> 跨请求复活的根因守卫、共享夹具 <c>flows/19-add-sign-after.json</c> 真消费。
///
/// <para>夹具走内联 JSON（<see cref="Chain"/>），<b>不碰共享 flows 目录</b>——共享流程 JSON 的
/// 八仓镜像与漂移门禁是另一条腿，在这里加文件会把它提前拖进十仓同步；只有
/// <see cref="SharedFixtureChainRuns"/> 那一格读 flows/19 本身。</para>
/// </summary>
public class AddSignAfterTests
{
    private const int Agree = (int)WfSubmitType.Agree;                 // 1
    private const int Apply = (int)WfSubmitType.Apply;                 // 0
    private const int Reject = (int)WfSubmitType.Reject;               // 2
    private const int Return = (int)WfSubmitType.RollbackToOperator;   // 6

    private static readonly string RoundKey = $"{FlowConst.AddSignRound}_review";
    private const string AddSignOn = "\"allowAddSign\":true";

    private static FlowData Submit(int type) => new() { [FlowConst.SubmitType] = type };

    /// <summary>同意＋本次点名的加签人（逗号串档；数组档在多选那格直接用 List 形态）。</summary>
    private static FlowData AgreeWith(string operators) =>
        new()
        {
            [FlowConst.SubmitType] = Agree,
            [FlowConst.AddSignOperators] = operators,
        };

    // ═══ 夹具：start → apply → review → final → end ═══

    private static string Node(string id, string type, string props, string text) =>
        "{\"id\":\"" + id + "\",\"type\":\"" + type + "\",\"x\":0,\"y\":0,"
        + "\"properties\":{" + props + "},\"text\":{\"value\":\"" + text + "\"}}";

    private static string Edge(string id, string from, string to) =>
        "{\"id\":\"" + id + "\",\"type\":\"snaker:transition\",\"sourceNodeId\":\"" + from
        + "\",\"targetNodeId\":\"" + to + "\",\"properties\":{}}";

    /// <summary>
    /// 两参数分别控制申请节点与审批节点的 properties 片段。
    /// <para><b>与 java 夹具的一处形状差</b>：java 那边把追加片段直接拼在基线 properties 后面，
    /// 靠 Jackson「重复键后者覆盖」把 <c>performType</c> 从 0 改成 1；本栈的 System.Text.Json
    /// 在 <c>JsonNode.Parse</c> 默认档<b>拒绝重复键</b>（当场报"读取流程定义 JSON 失败"），
    /// 所以会签那一格改成用参数写 performType，不落重复键。</para>
    /// </summary>
    private static string Chain(string applyProps, string reviewProps, int reviewPerformType = 0)
    {
        var a = applyProps.Length == 0 ? "\"assignee\":\"applicant\"" : applyProps;
        var r = reviewProps.Length == 0 ? "" : "," + reviewProps;
        return "{\"name\":\"addsign\",\"displayName\":\"后加签夹具\",\"type\":\"approval\","
            + "\"nodes\":["
            + Node("start", "snaker:start", "", "开始") + ","
            + Node("apply", "snaker:task",
                "\"form\":\"apply-form\",\"taskType\":0,\"performType\":0," + a, "发起申请") + ","
            + Node("review", "snaker:task",
                "\"form\":\"form-r\",\"assignee\":\"userA\",\"taskType\":0,\"performType\":"
                + reviewPerformType + r, "审批") + ","
            + Node("final", "snaker:task",
                "\"form\":\"form-f\",\"assignee\":\"userF\",\"taskType\":0,\"performType\":0", "终审") + ","
            + Node("end", "snaker:end", "", "结束")
            + "],\"edges\":["
            + Edge("e1", "start", "apply") + ","
            + Edge("e2", "apply", "review") + ","
            + Edge("e3", "review", "final") + ","
            + Edge("e4", "final", "end")
            + "]}";
    }

    private int _seq;

    /// <summary>发起并办结申请节点，返回停在 review 的实例 id；参数是审批节点的 properties 片段。</summary>
    private async Task<(JeeflowEngine Engine, MemoryRepository Repo, long InstanceId)>
        StartToReviewAsync(string reviewProps)
    {
        var (engine, repo) = TestInfra.NewEngine();
        var defineId = await TestInfra.SaveFlowDefineAsync(repo,
            $"addsign-{++_seq}", Chain("\"assignee\":\"applicant\"", reviewProps));
        var inst = await engine.StartProcessInstanceByIdAsync(defineId, "applicant", new FlowData());
        foreach (var t in await repo.FindDoingTasksAsync(inst.InstanceId!.Value, null))
            await engine.ExecuteProcessTaskAsync(t.TaskId!.Value, "applicant", Submit(Apply));
        return (engine, repo, inst.InstanceId.Value);
    }

    private static async Task<ProcessTask?> DoingAsync(MemoryRepository repo, long iid, string nodeName) =>
        (await repo.FindDoingTasksAsync(iid, null)).FirstOrDefault(t => t.TaskName == nodeName);

    private static async Task<int> DoingCountAsync(MemoryRepository repo, long iid, string nodeName) =>
        (await repo.FindDoingTasksAsync(iid, null)).Count(t => t.TaskName == nodeName);

    private static async Task<int> RowsAsync(MemoryRepository repo, long iid, string nodeName) =>
        (await repo.FindHistoryTasksAsync(iid)).Count(t => t.TaskName == nodeName);

    /// <summary>进行中任务的 actor_id 列恒空是家族不变量（办结才写），取办理人要用参与者列表。</summary>
    private static string FirstActor(ProcessTask t) => t.ActorIds.First();

    // ═══ 正向 ═══

    /// <summary>链式续办：A→B→C→不点人即流出；血缘串成链、轮次递增、账本落在已完成行。</summary>
    [Fact]
    public async Task ChainThenStop()
    {
        var (engine, repo, iid) = await StartToReviewAsync(AddSignOn);
        var a = await DoingAsync(repo, iid, "review");
        Assert.NotNull(a);

        await engine.ExecuteProcessTaskAsync(a!.TaskId!.Value, "userA", AgreeWith("userB"));
        var b = await DoingAsync(repo, iid, "review");
        Assert.NotNull(b);                                  // 被加签人应在同一节点收到新待办
        Assert.Equal(a.TaskId, b!.ParentTaskId);            // 血缘 parent＝刚办结那条
        Assert.Equal(1, b.Variables.GetInt(RoundKey) ?? -1); // 轮次 1
        Assert.Null(await DoingAsync(repo, iid, "final"));   // 令牌不得流出本节点
        Assert.Equal(1, await DoingCountAsync(repo, iid, "review"));

        var histA = AddSignSupport.HistoryOf(await repo.FindTaskByIdAsync(a.TaskId));
        var entry = Assert.Single(histA);
        var e0 = Assert.IsAssignableFrom<IDictionary<string, object?>>(entry);
        Assert.Equal("userA", e0["fromActor"]);
        Assert.Equal(new List<object?> { "userB" },
            Assert.IsAssignableFrom<System.Collections.IEnumerable>(e0["toActors"])
                .Cast<object?>().ToList());
        Assert.Equal(1, Convert.ToInt32(e0["round"]));
        Assert.Equal("userA", e0["operator"]);
        Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}", e0["time"]?.ToString() ?? "");

        await engine.ExecuteProcessTaskAsync(b.TaskId!.Value, "userB", AgreeWith("userC"));
        var c = await DoingAsync(repo, iid, "review");
        Assert.NotNull(c);
        Assert.Equal(2, c!.Variables.GetInt(RoundKey) ?? -1);   // 轮次递增
        Assert.Equal(b.TaskId, c.ParentTaskId);

        // C 不再点人 → 停止条件成立，流出本节点
        await engine.ExecuteProcessTaskAsync(c.TaskId!.Value, "userC", Submit(Agree));
        Assert.Null(await DoingAsync(repo, iid, "review"));
        var fin = await DoingAsync(repo, iid, "final");
        Assert.NotNull(fin);                                   // 应流转到下一审批节点
        Assert.Equal(3, await RowsAsync(repo, iid, "review"));

        await engine.ExecuteProcessTaskAsync(fin!.TaskId!.Value, "userF", Submit(Agree));
        Assert.Equal((int)WfInstanceState.Finished,
            (await repo.FindInstanceByIdAsync(iid))!.State);   // 实例办结
    }

    /// <summary>数组两形同判据（规范 06 §2.11）：一次点两人 ⇒ 同圈建两行；最后一位办结且没点人才放行。</summary>
    [Fact]
    public async Task MultiSelectWaitsSiblings()
    {
        var (engine, repo, iid) = await StartToReviewAsync(AddSignOn);
        var a = await DoingAsync(repo, iid, "review");
        Assert.NotNull(a);

        await engine.ExecuteProcessTaskAsync(a!.TaskId!.Value, "userA", new FlowData
        {
            [FlowConst.SubmitType] = Agree,
            [FlowConst.AddSignOperators] = new List<object?> { "userB", "userC" },
        });
        Assert.Equal(2, await DoingCountAsync(repo, iid, "review"));   // 同圈应建两行待办
        Assert.Null(await DoingAsync(repo, iid, "final"));

        var b = await DoingAsync(repo, iid, "review");
        await engine.ExecuteProcessTaskAsync(b!.TaskId!.Value, FirstActor(b), Submit(Agree));
        Assert.Null(await DoingAsync(repo, iid, "final"));             // 兄弟待办未完成不得放行
        Assert.Equal(1, await DoingCountAsync(repo, iid, "review"));

        var c = await DoingAsync(repo, iid, "review");
        await engine.ExecuteProcessTaskAsync(c!.TaskId!.Value, FirstActor(c), Submit(Agree));
        Assert.NotNull(await DoingAsync(repo, iid, "final"));          // 最后一位办结且没点人才放行
        Assert.Equal(1, await DoingCountAsync(repo, iid, "final"));    // 放行只能发生一次
    }

    /// <summary>加给已办过的人合法（A→B→A 乒乓），轮次继续计数。</summary>
    [Fact]
    public async Task ReAddPreviouslyHandledIsLegal()
    {
        var (engine, repo, iid) = await StartToReviewAsync(AddSignOn);
        var a = await DoingAsync(repo, iid, "review");
        await engine.ExecuteProcessTaskAsync(a!.TaskId!.Value, "userA", AgreeWith("userB"));
        var b = await DoingAsync(repo, iid, "review");
        await engine.ExecuteProcessTaskAsync(b!.TaskId!.Value, "userB", AgreeWith("userA"));
        var back = await DoingAsync(repo, iid, "review");
        Assert.NotNull(back);
        Assert.Contains("userA", back!.ActorIds);
        Assert.Equal(2, back.Variables.GetInt(RoundKey) ?? -1);
    }

    // ═══ 负向：写入口报错且零写入 ═══

    /// <summary>与 tf_nextNodeOperator 同传 ⇒ 报错，且任务行/实例零改动。</summary>
    [Fact]
    public async Task MutexWithNextNodeOperatorRejected()
    {
        var (engine, repo, iid) = await StartToReviewAsync(AddSignOn);
        var a = await DoingAsync(repo, iid, "review");
        Assert.NotNull(a);
        var before = await RowsAsync(repo, iid, "review");

        var args = AgreeWith("userB");
        args[FlowConst.NextNodeOperator] = "userF";
        var ex = await Assert.ThrowsAsync<JeeflowException>(
            () => engine.ExecuteProcessTaskAsync(a!.TaskId!.Value, "userA", args));
        Assert.Equal(AddSignSupport.MsgMutex, ex.Message);

        Assert.True((await repo.FindTaskByIdAsync(a.TaskId))!.IsDoing());  // 报错时任务行不得被办结
        Assert.Equal(before, await RowsAsync(repo, iid, "review"));        // 不得新增行
        Assert.Equal((int)WfInstanceState.Doing, (await repo.FindInstanceByIdAsync(iid))!.State);
    }

    /// <summary>节点未开开关却传键 ⇒ 报错（不得静默忽略），零写入。</summary>
    [Fact]
    public async Task SwitchOffRejected()
    {
        var (engine, repo, iid) = await StartToReviewAsync("");   // review 不开 allowAddSign
        var a = await DoingAsync(repo, iid, "review");
        Assert.NotNull(a);

        var ex = await Assert.ThrowsAsync<JeeflowException>(
            () => engine.ExecuteProcessTaskAsync(a!.TaskId!.Value, "userA", AgreeWith("userB")));
        Assert.Contains("未开启 allowAddSign", ex.Message);

        Assert.True((await repo.FindTaskByIdAsync(a.TaskId))!.IsDoing());
        Assert.Null(await DoingAsync(repo, iid, "final"));
    }

    /// <summary>加给自己 ⇒ 报错（死循环入口），零写入。</summary>
    [Fact]
    public async Task SelfRejected()
    {
        var (engine, repo, iid) = await StartToReviewAsync(AddSignOn);
        var a = await DoingAsync(repo, iid, "review");
        Assert.NotNull(a);

        var ex = await Assert.ThrowsAsync<JeeflowException>(
            () => engine.ExecuteProcessTaskAsync(a!.TaskId!.Value, "userA", AgreeWith("userA")));
        Assert.Equal(AddSignSupport.MsgSelf, ex.Message);

        Assert.True((await repo.FindTaskByIdAsync(a!.TaskId))!.IsDoing());
        // 本节点仍是那一条待办（没多建、也没少建）
        Assert.Equal(1, await DoingCountAsync(repo, iid, "review"));
    }

    /// <summary>超上限 ⇒ 报错不强制放行（放行＝伪造"审批完成"），零写入。</summary>
    [Fact]
    public async Task MaxRoundsRejected()
    {
        var (engine, repo, iid) = await StartToReviewAsync(AddSignOn + ",\"addSignMaxRounds\":1");
        var a = await DoingAsync(repo, iid, "review");
        await engine.ExecuteProcessTaskAsync(a!.TaskId!.Value, "userA", AgreeWith("userB")); // 第 1 轮，合法
        var b = await DoingAsync(repo, iid, "review");
        Assert.NotNull(b);

        var ex = await Assert.ThrowsAsync<JeeflowException>(
            () => engine.ExecuteProcessTaskAsync(b!.TaskId!.Value, "userB", AgreeWith("userC")));
        Assert.Contains("已达上限 1", ex.Message);

        Assert.True((await repo.FindTaskByIdAsync(b.TaskId))!.IsDoing());   // 越界那次不得办结任务行
        Assert.Null(await DoingAsync(repo, iid, "final"));
        // 但第 1 轮那位不点人可以正常流出
        await engine.ExecuteProcessTaskAsync(b.TaskId!.Value, "userB", Submit(Agree));
        Assert.NotNull(await DoingAsync(repo, iid, "final"));
    }

    // ═══ 解析期硬校验（规范 02 §4.3）═══

    [Fact]
    public void CountersignNodeRejectedAtParse()
    {
        var (_, _, ctx) = TestInfra.NewEngineWithCtx();
        // performType 走参数写（1＝会签），不在 properties 里留重复键——见 Chain 的注释
        AssertParseFails(ctx, Chain("\"assignee\":\"applicant\"",
            AddSignOn + ",\"countersignType\":\"PARALLEL\"", reviewPerformType: 1), "会签节点");
    }

    [Fact]
    public void FirstTaskNodeRejectedAtParse()
    {
        var (_, _, ctx) = TestInfra.NewEngineWithCtx();
        AssertParseFails(ctx,
            Chain("\"assignee\":\"applicant\"," + AddSignOn, AddSignOn), "首任务节点");
    }

    [Fact]
    public void InvalidConfigValuesRejectedAtParse()
    {
        var (_, _, ctx) = TestInfra.NewEngineWithCtx();
        AssertParseFails(ctx, Chain("\"assignee\":\"applicant\"",
            "\"allowAddSign\":\"yes\""), "allowAddSign 配置非法");
        AssertParseFails(ctx, Chain("\"assignee\":\"applicant\"",
            AddSignOn + ",\"addSignMaxRounds\":-1"), "不得为负");
        AssertParseFails(ctx, Chain("\"assignee\":\"applicant\"",
            AddSignOn + ",\"addSignMaxRounds\":\"1.5\""), "必须是整数");
    }

    /// <summary>两开关声明成模型字段后不得再镜像进 ext（双源就此断掉，规范 02 §4.3）。</summary>
    [Fact]
    public void SwitchKeysDoNotMirrorIntoExt()
    {
        var (_, _, ctx) = TestInfra.NewEngineWithCtx();
        var model = ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(
            Chain("\"assignee\":\"applicant\"", AddSignOn + ",\"addSignMaxRounds\":2")), ctx);
        var review = Assert.IsAssignableFrom<TaskModel>(model.GetNode("review"));
        Assert.True(review.AllowAddSign);
        Assert.Equal(2, review.AddSignMaxRounds);
        Assert.False(review.Ext.ContainsKey(AddSignSupport.PropAllowAddSign));
        Assert.False(review.Ext.ContainsKey(AddSignSupport.PropAddSignMaxRounds));
    }

    private static void AssertParseFails(ServiceContext ctx, string flowJson, string expectedFragment)
    {
        var ex = Assert.Throws<JeeflowException>(() =>
            ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(flowJson), ctx));
        Assert.Contains(expectedFragment, ex.Message);
    }

    // ═══ 废弃未完成加签任务（规范 04 · 后加签「语义边界」）═══

    /// <summary>不同意(2)：实例结束，本节点未完成的兄弟加签任务全部废弃，不留孤儿待办。</summary>
    [Fact]
    public async Task RejectAbandonsSiblings()
    {
        var (engine, repo, iid) = await StartToReviewAsync(AddSignOn);
        var a = await DoingAsync(repo, iid, "review");
        await engine.ExecuteProcessTaskAsync(a!.TaskId!.Value, "userA", new FlowData
        {
            [FlowConst.SubmitType] = Agree,
            [FlowConst.AddSignOperators] = new List<object?> { "userB", "userC" },
        });
        Assert.Equal(2, await DoingCountAsync(repo, iid, "review"));

        var b = await DoingAsync(repo, iid, "review");
        await engine.ExecuteAndJumpToEndAsync(b!.TaskId!.Value, FirstActor(b), Submit(Reject));

        Assert.Equal((int)WfInstanceState.Reject, (await repo.FindInstanceByIdAsync(iid))!.State);
        Assert.Equal(0, await DoingCountAsync(repo, iid, "review"));   // 未完成的加签兄弟不得留待办
        var abandoned = (await repo.FindHistoryTasksAsync(iid))
            .Where(t => t.TaskName == "review")
            .Count(t => t.TaskState == (int)WfTaskState.Abandon);
        Assert.Equal(1, abandoned);                                    // 被废弃的那条应留废弃态
    }

    /// <summary>退回发起人(6)：申请待办生成，本节点未完成加签任务废弃。</summary>
    [Fact]
    public async Task ReturnToApplicantAbandonsSiblings()
    {
        var (engine, repo, iid) = await StartToReviewAsync(AddSignOn);
        var a = await DoingAsync(repo, iid, "review");
        await engine.ExecuteProcessTaskAsync(a!.TaskId!.Value, "userA", new FlowData
        {
            [FlowConst.SubmitType] = Agree,
            [FlowConst.AddSignOperators] = new List<object?> { "userB", "userC" },
        });
        var b = await DoingAsync(repo, iid, "review");
        await engine.ExecuteAndJumpToFirstTaskNodeAsync(b!.TaskId!.Value, FirstActor(b), Submit(Return));

        Assert.NotNull(await DoingAsync(repo, iid, "apply"));          // 应生成申请待办
        Assert.Equal(0, await DoingCountAsync(repo, iid, "review"));   // 本节点未完成加签任务应被废弃
        Assert.Null(await DoingAsync(repo, iid, "final"));
    }

    /// <summary>
    /// 加签链上「退回上一步」(3) <b>禁</b>（方案 §6 拍板①，照会签先例）：当前行与其血缘 parent
    /// 同节点，既有 <c>CanRejected</c> 守卫不过 ⇒ 走本栈既有等价错误出口（逐字文案＝规范 06 失败 msg 表
    /// 「canRejected 守卫不过」那一行，内部码 20010008 不进 msg；本栈异常无码位，文案即判据）。
    /// <para>守卫拦住的是"退到同节点的上一步"：既不生成新待办，也不流到下一节点。
    /// ⚠ 与 java 同形的一处既有排序：<b>退回族的报错发生在 prepare 之后</b>（那条待办已被本次
    /// 办理办结），本特性"零改动"照抄这个排序、不在本轮顺手改它——「四判据必须排在任何状态变更之前」
    /// 约束的是加签写入口（<see cref="AddSignSupport.ValidateSubmission"/>），不是这条旧腿。</para>
    /// </summary>
    [Fact]
    public async Task RollbackOnAddSignChainIsForbidden()
    {
        var (engine, repo, iid) = await StartToReviewAsync(AddSignOn);
        var a = await DoingAsync(repo, iid, "review");
        await engine.ExecuteProcessTaskAsync(a!.TaskId!.Value, "userA", AgreeWith("userB"));
        var b = await DoingAsync(repo, iid, "review");
        Assert.NotNull(b);

        var ex = await Assert.ThrowsAsync<JeeflowException>(
            () => engine.ExecuteAndJumpTaskAsync(b!.TaskId!.Value, "userB",
                Submit((int)WfSubmitType.Rollback), null));
        Assert.Contains("无法驳回至上一步处理", ex.Message);
        // 退同节点不成 ⇒ 本节点不再有活（B 那条已由 prepare 办结、没有复活的上一步），
        // 且令牌绝不流到下一节点
        Assert.Equal(0, await DoingCountAsync(repo, iid, "review"));
        Assert.Null(await DoingAsync(repo, iid, "final"));
        Assert.Equal((int)WfInstanceState.Doing, (await repo.FindInstanceByIdAsync(iid))!.State);
    }

    // ═══ 回归与并发 ═══

    /// <summary>不开开关的节点行为逐字不变：同意即流出，且不因新判据受影响。</summary>
    [Fact]
    public async Task PlainNodeUnchanged()
    {
        var (engine, repo, iid) = await StartToReviewAsync("");
        var a = await DoingAsync(repo, iid, "review");
        Assert.NotNull(a);
        await engine.ExecuteProcessTaskAsync(a!.TaskId!.Value, "userA", Submit(Agree));

        Assert.NotNull(await DoingAsync(repo, iid, "final"));
        Assert.Equal(1, await RowsAsync(repo, iid, "review"));
        Assert.Empty(AddSignSupport.HistoryOf(await repo.FindTaskByIdAsync(a.TaskId)));
    }

    /// <summary>两个同圈加签任务并发办结：只放行一次（后续节点恰好一条待办，不重复创建）。</summary>
    [Fact]
    public async Task ConcurrentFinishReleasesOnce()
    {
        var (engine, repo, iid) = await StartToReviewAsync(AddSignOn);
        var a = await DoingAsync(repo, iid, "review");
        await engine.ExecuteProcessTaskAsync(a!.TaskId!.Value, "userA", new FlowData
        {
            [FlowConst.SubmitType] = Agree,
            [FlowConst.AddSignOperators] = new List<object?> { "userB", "userC" },
        });

        var siblings = await repo.FindDoingTasksAsync(iid, null);
        Assert.Equal(2, siblings.Count);
        var tasks = siblings.Select(t =>
            engine.ExecuteProcessTaskAsync(t.TaskId!.Value, FirstActor(t), Submit(Agree))).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, await DoingCountAsync(repo, iid, "final"));   // 后续节点只应有一条待办
        Assert.Equal(0, await DoingCountAsync(repo, iid, "review"));
    }

    // ═══ 根因守卫 ═══

    /// <summary>
    /// 根因守卫：控制类 <c>tf_*</c> 参数不得进<b>实例变量</b>（跨请求复活的病根），
    /// 但任务行留痕必须完整——两者是同一枚判据的两面（规范 04「流程变量约定」末注）。
    /// 摘掉 <c>ProcessInstance.CompleteTask</c> 里的 tf_ 过滤 ⇒ 本格的"实例变量不得出现任何 tf_ 键"
    /// 那条断言当场变红（reapply 轮的坏样本纪律）。
    /// </summary>
    [Fact]
    public async Task ControlKeysNeverEnterInstanceVariables()
    {
        var (engine, repo, iid) = await StartToReviewAsync(AddSignOn);
        var a = await DoingAsync(repo, iid, "review");
        Assert.NotNull(a);
        var args = AgreeWith("userB");
        args[FlowConst.ApprovalComment] = "请 B 复核";
        await engine.ExecuteProcessTaskAsync(a!.TaskId!.Value, "userA", args);

        var instanceVars = (await repo.FindInstanceByIdAsync(iid))!.Variables;
        Assert.Null(instanceVars.GetObj(FlowConst.AddSignOperators));
        Assert.Null(instanceVars.GetObj(FlowConst.ApprovalComment));
        var leaked = instanceVars.Keys
            .Where(k => k.StartsWith(FlowConst.TaskFormDataPrefix, StringComparison.Ordinal))
            .ToList();
        Assert.True(leaked.Count == 0,
            $"实例变量里不应有任何 tf_ 键（跨请求复活的病根），实际出现: {string.Join(",", leaked)}");

        // 留痕仍在任务行上（修根因不得把审计信息一起改掉）
        var rowVars = (await repo.FindTaskByIdAsync(a.TaskId))!.Variables;
        Assert.Equal("请 B 复核", rowVars.GetStr(FlowConst.ApprovalComment));
        Assert.Equal("userB", rowVars.GetObj(FlowConst.AddSignOperators)?.ToString());
        Assert.Single(AddSignSupport.HistoryOf(await repo.FindTaskByIdAsync(a.TaskId)));
    }

    /// <summary>
    /// 复活的行为面：B 办结时<b>不再点人</b>，令牌必须流出本节点——上一轮的
    /// <c>tf_addSignOperators</c> 若随实例变量复活，这里会凭空多出一条 review 待办。
    /// </summary>
    [Fact]
    public async Task PreviousRoundOperatorsDoNotResurrect()
    {
        var (engine, repo, iid) = await StartToReviewAsync(AddSignOn);
        var a = await DoingAsync(repo, iid, "review");
        await engine.ExecuteProcessTaskAsync(a!.TaskId!.Value, "userA", AgreeWith("userB"));
        var b = await DoingAsync(repo, iid, "review");
        Assert.NotNull(b);

        await engine.ExecuteProcessTaskAsync(b!.TaskId!.Value, "userB", Submit(Agree));

        Assert.Null(await DoingAsync(repo, iid, "review"));    // 不多建本节点待办
        Assert.NotNull(await DoingAsync(repo, iid, "final"));  // 正常流出
        Assert.Equal(2, await RowsAsync(repo, iid, "review"));
    }

    // ═══ 共享夹具真消费 ═══

    /// <summary>
    /// 共享夹具 <c>flows/19-add-sign-after.json</c> 必须真被本栈消费——八语言仓 flows/ 是它的入库副本，
    /// 各语言移植后加签时读的就是这一份；没有生产者核对过的夹具会在传播轮里各语言各自"改一份能跑的"。
    /// </summary>
    [Fact]
    public async Task SharedFixtureChainRuns()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var defineId = await TestInfra.SaveFlowDefineAsync(repo,
            "add-sign-after", TestInfra.LoadFlow("19-add-sign-after"));
        var inst = await engine.StartProcessInstanceByIdAsync(defineId, "applicant", new FlowData());
        var iid = inst.InstanceId!.Value;
        foreach (var t in await repo.FindDoingTasksAsync(iid, null))
            await engine.ExecuteProcessTaskAsync(t.TaskId!.Value, "applicant", Submit(Apply));

        var a = await DoingAsync(repo, iid, "review");
        Assert.NotNull(a);                                     // 申请节点办结后应到审批节点
        await engine.ExecuteProcessTaskAsync(a!.TaskId!.Value, "userA", AgreeWith("userB,userC"));
        Assert.Equal(2, await DoingCountAsync(repo, iid, "review"));   // 逗号串两形应建两行
        Assert.Null(await DoingAsync(repo, iid, "final"));

        // 先取快照再逐条办结：办结会改写仓储里的进行中集合，边遍历边办是并发修改
        var siblings = await repo.FindDoingTasksAsync(iid, null);
        foreach (var sib in siblings)
            await engine.ExecuteProcessTaskAsync(sib.TaskId!.Value, FirstActor(sib), Submit(Agree));

        var fin = await DoingAsync(repo, iid, "final");
        Assert.NotNull(fin);                                   // 同圈兄弟全部办结后应放行
        Assert.Equal(1, await RowsAsync(repo, iid, "final"));
        await engine.ExecuteProcessTaskAsync(fin!.TaskId!.Value, "userF", Submit(Agree));
        Assert.Equal((int)WfInstanceState.Finished, (await repo.FindInstanceByIdAsync(iid))!.State);
    }
}

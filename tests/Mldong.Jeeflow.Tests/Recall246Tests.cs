using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// 「已办撤回（recall）」验收（issues/246 · C# 腿）——移植基准＝jeeflow-java
/// <c>jeeflow-core/src/test/java/com/mldong/jeeflow/test/RecallTest.java</c>（9 格，逐格语义对齐），
/// 本文件在 9 格之外<b>另加四格</b>：门面出口两键、必填两档、事件码 10 的"只发一次"与
/// "报错那次一个事件都不发"。
///
/// <para>钉住的面（规范 04「已办撤回」／规范 06 §processTask/recall／规范 11 码 10）：
/// 复活三件套（原行留 FINISHED＋<c>tf_recallHistory</c>、下游 DOING 废弃、同节点新行名册照旧）、
/// 窗口两档（宽口径放行／下游已办结拦）、五条逐字文案、后加签行连带废弃、并行分支全废弃、
/// 解析期两条禁配、实例非 DOING。</para>
///
/// <para><b>零写入三面断言</b>：每个报错格都真断<b>当前行状态不变＋任务行总数不变＋实例状态不变</b>
/// 三面，不是只断文案——判据必须排在任何状态变更之前；内存仓储无回滚，位序写错这三面里
/// 至少有一面会红（"只断文案"的测试对位序错误是失明的）。</para>
///
/// <para>夹具一律走<b>内联 JSON</b>（照 <see cref="AddSignAfterTests"/> 的纪律），
/// <b>不碰共享 <c>flows/</c> 目录</b>——那是八仓镜像＋漂移门禁的另一条腿。</para>
/// </summary>
public class Recall246Tests
{
    private const int Agree = (int)WfSubmitType.Agree;   // 1
    private const int Apply = (int)WfSubmitType.Apply;   // 0

    private static FlowData Submit(int type) => new() { [FlowConst.SubmitType] = type };

    // ═══ 夹具（逐字照 java RecallTest 的三个 chain）═══

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

    private static string ReviewNode(string reviewProps) =>
        Node("review", "\"form\":\"form-r\",\"taskType\":0,\"performType\":0,"
            + (reviewProps.Length == 0 ? "\"assignee\":\"userA\"" : "\"assignee\":\"userA\"," + reviewProps),
            "审批");

    /// <summary>五节点链 start→apply→review→final→end（java <c>chain</c>）。</summary>
    private static string Chain(string reviewProps, string name) =>
        Head(name, "已办撤回夹具")
        + Node("apply", ApplyNode, "发起申请") + ","
        + ReviewNode(reviewProps) + ","
        + Node("final", "\"form\":\"form-f\",\"assignee\":\"userF\",\"taskType\":0,\"performType\":0", "终审") + ","
        + EndNode
        + "],\"edges\":["
        + Edge("e1", "start", "apply") + "," + Edge("e2", "apply", "review") + ","
        + Edge("e3", "review", "final") + "," + Edge("e4", "final", "end")
        + "]}";

    /// <summary>六节点链（多一级 pay），供"下游已办结"窗口档（java <c>chainDeep</c>）。</summary>
    private static string ChainDeep(string reviewProps, string name) =>
        Head(name, "已办撤回深链")
        + Node("apply", ApplyNode, "发起申请") + ","
        + ReviewNode(reviewProps) + ","
        + Node("final", "\"form\":\"form-f\",\"assignee\":\"userF\",\"taskType\":0,\"performType\":0", "终审") + ","
        + Node("pay", "\"form\":\"form-p\",\"assignee\":\"userP\",\"taskType\":0,\"performType\":0", "支付") + ","
        + EndNode
        + "],\"edges\":["
        + Edge("e1", "start", "apply") + "," + Edge("e2", "apply", "review") + ","
        + Edge("e3", "review", "final") + "," + Edge("e4", "final", "pay") + "," + Edge("e5", "pay", "end")
        + "]}";

    /// <summary>并行双支：review 出两条边到 payA/payB，各自汇入 end（java <c>chainFork</c>）。</summary>
    private static string ChainFork(string reviewProps, string name) =>
        Head(name, "已办撤回并行")
        + Node("apply", ApplyNode, "发起申请") + ","
        + ReviewNode(reviewProps) + ","
        + Node("payA", "\"form\":\"form-a\",\"assignee\":\"userPA\",\"taskType\":0,\"performType\":0", "支付甲") + ","
        + Node("payB", "\"form\":\"form-b\",\"assignee\":\"userPB\",\"taskType\":0,\"performType\":0", "支付乙") + ","
        + EndNode
        + "],\"edges\":["
        + Edge("e1", "start", "apply") + "," + Edge("e2", "apply", "review") + ","
        + Edge("e3", "review", "payA") + "," + Edge("e4", "review", "payB") + ","
        + Edge("e5", "payA", "end") + "," + Edge("e6", "payB", "end")
        + "]}";

    private int _seq;
    private string NextName(string prefix) => $"{prefix}-{++_seq}";

    private static (JeeflowEngine Engine, MemoryRepository Repo, ServiceContext Ctx) NewStack()
        => TestInfra.NewEngineWithCtx();

    /// <summary>发起并办结申请节点，返回停在 review 的实例 id（java <c>startToReview</c>）。</summary>
    private async Task<(JeeflowEngine Engine, MemoryRepository Repo, ServiceContext Ctx, long Iid)>
        StartToReviewAsync(string flowJson)
    {
        var (engine, repo, ctx) = NewStack();
        var name = FlowName(flowJson);
        var defineId = await TestInfra.SaveFlowDefineAsync(repo, name, flowJson);
        var inst = await engine.StartProcessInstanceByIdAsync(defineId, "applicant", new FlowData());
        foreach (var t in await repo.FindDoingTasksAsync(inst.InstanceId!.Value, null))
            await engine.ExecuteProcessTaskAsync(t.TaskId!.Value, "applicant", Submit(Apply));
        return (engine, repo, ctx, inst.InstanceId!.Value);
    }

    /// <summary>从内联 JSON 里读回 name（夹具的 name 由 Chain* 生成，这里只回读，不再造第二份）。</summary>
    private static string FlowName(string flowJson)
    {
        var i = flowJson.IndexOf("\"name\":\"", StringComparison.Ordinal);
        var s = flowJson[(i + 8)..];
        return s[..s.IndexOf('"', StringComparison.Ordinal)];
    }

    private static async Task<ProcessTask?> DoingAsync(MemoryRepository repo, long iid, string nodeName) =>
        (await repo.FindDoingTasksAsync(iid, null)).FirstOrDefault(t => t.TaskName == nodeName);

    private static async Task<int> DoingCountAsync(MemoryRepository repo, long iid, string nodeName) =>
        (await repo.FindDoingTasksAsync(iid, null)).Count(t => t.TaskName == nodeName);

    /// <summary>不按状态滤——撤回后的下游行是 ABANDON(99)，不走 FINISHED（java <c>anyRow</c>）。</summary>
    private static async Task<ProcessTask?> AnyRowAsync(MemoryRepository repo, long iid, string nodeName) =>
        (await repo.FindHistoryTasksAsync(iid)).FirstOrDefault(t => t.TaskName == nodeName);

    private static async Task<ProcessTask?> FinishedRowAsync(MemoryRepository repo, long iid, string nodeName) =>
        (await repo.FindHistoryTasksAsync(iid))
            .FirstOrDefault(t => t.TaskName == nodeName && t.IsFinished());

    private static async Task<int> TotalRowsAsync(MemoryRepository repo, long iid) =>
        (await repo.FindHistoryTasksAsync(iid)).Count;

    // ═══ ① 正向：复活三件套（java 格 1）═══

    [Fact]
    public async Task RecallRevivesAbandonsDownstreamAndAllowsRedo()
    {
        var (engine, repo, _, iid) = await StartToReviewAsync(Chain("\"allowRecall\":true", NextName("recall")));
        var review = await DoingAsync(repo, iid, "review");
        Assert.NotNull(review);
        await engine.ExecuteProcessTaskAsync(review!.TaskId!.Value, "userA", Submit(Agree));
        var oldReview = await FinishedRowAsync(repo, iid, "review");
        Assert.NotNull(oldReview);
        Assert.Equal(1, await DoingCountAsync(repo, iid, "final"));

        var resurrected = await engine.RecallTaskAsync(oldReview.TaskId!.Value, "userA");

        Assert.Equal("review", resurrected.TaskName);
        Assert.True(resurrected.IsDoing());
        Assert.Contains("userA", resurrected.ActorIds);
        // 血缘同尺：复活行的 parent＝原行的 parent（不是原行 id——它要接在原行的上游位置上重办）
        Assert.Equal(oldReview.ParentTaskId, resurrected.ParentTaskId);
        // 下游废弃
        var finalRow = await AnyRowAsync(repo, iid, "final");
        Assert.NotNull(finalRow);
        Assert.Equal((int)WfTaskState.Abandon, finalRow!.TaskState);
        Assert.Equal(0, await DoingCountAsync(repo, iid, "final"));
        // 原行不改写＋账本指向复活行（三键齐）
        var stored = await repo.FindTaskByIdAsync(oldReview.TaskId);
        Assert.True(stored!.IsFinished());
        var entry = Assert.Single(RecallSupport.HistoryOf(stored));
        var e0 = Assert.IsAssignableFrom<IDictionary<string, object?>>(entry);
        Assert.Equal("userA", e0["operator"]);
        Assert.Equal(resurrected.TaskId, e0["recalledTaskId"]);
        Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}", e0["time"]?.ToString() ?? "");
        // 实例仍进行中；重办后正常推进
        Assert.Equal((int)WfInstanceState.Doing, (await repo.FindInstanceByIdAsync(iid))!.State);
        await engine.ExecuteProcessTaskAsync(resurrected.TaskId!.Value, "userA", Submit(Agree));
        Assert.NotNull(await DoingAsync(repo, iid, "final"));
        // final 两行：撤回前那条已被废弃成 99，重办后新建的这条是 DOING
        Assert.Equal(2, (await repo.FindHistoryTasksAsync(iid)).Count(t => t.TaskName == "final"));
    }

    // ═══ ② 窗口：下游已办结 ⇒ 逐字文案 ＋ 三面零写入（java 格 2）═══

    [Fact]
    public async Task RecallBlockedWhenDownstreamFinishedWithZeroWrite()
    {
        var (engine, repo, _, iid) = await StartToReviewAsync(ChainDeep("\"allowRecall\":true", NextName("recalldeep")));
        var review = await DoingAsync(repo, iid, "review");
        await engine.ExecuteProcessTaskAsync(review!.TaskId!.Value, "userA", Submit(Agree));
        var finalTask = await DoingAsync(repo, iid, "final");
        await engine.ExecuteProcessTaskAsync(finalTask!.TaskId!.Value, "userF", Submit(Agree));
        var oldReview = await FinishedRowAsync(repo, iid, "review");
        Assert.NotNull(oldReview);
        Assert.NotNull(await DoingAsync(repo, iid, "pay"));

        var before = await TotalRowsAsync(repo, iid);
        var ex = await Assert.ThrowsAsync<JeeflowException>(
            () => engine.RecallTaskAsync(oldReview!.TaskId!.Value, "userA"));
        Assert.Equal("任务[review]的后续节点已办理，不能撤回", ex.Message);

        // 零写入三面：① 任务行总数不变（没建复活行）② 原行/下游行状态原样 ③ 实例状态不变
        Assert.Equal(before, await TotalRowsAsync(repo, iid));
        Assert.Empty(RecallSupport.HistoryOf(await repo.FindTaskByIdAsync(oldReview.TaskId)));  // 无账本
        Assert.True((await FinishedRowAsync(repo, iid, "final"))!.IsFinished());               // 下游原样
        Assert.NotNull(await DoingAsync(repo, iid, "pay"));
        Assert.Equal(0, await DoingCountAsync(repo, iid, "review"));
        Assert.Equal((int)WfInstanceState.Doing, (await repo.FindInstanceByIdAsync(iid))!.State);
    }

    /// <summary>宽口径的另一面：下游<b>只有 DOING</b> ⇒ 放行（窄口径会拦这一档，写窄了本格报红）。</summary>
    [Fact]
    public async Task WideWindowAcceptsDoingOnlyDownstream()
    {
        var (engine, repo, _, iid) = await StartToReviewAsync(Chain("\"allowRecall\":true", NextName("recall")));
        var review = await DoingAsync(repo, iid, "review");
        await engine.ExecuteProcessTaskAsync(review!.TaskId!.Value, "userA", Submit(Agree));
        var oldReview = await FinishedRowAsync(repo, iid, "review");
        Assert.NotNull(await DoingAsync(repo, iid, "final"));   // 下游是 DOING、不是 FINISHED
        var resurrected = await engine.RecallTaskAsync(oldReview!.TaskId!.Value, "userA");
        Assert.True(resurrected.IsDoing());
    }

    // ═══ ③④⑤ 三条逐字文案档（java 格 3/4/5）═══

    [Fact]
    public async Task RecallBlockedWhenSwitchOff()
    {
        var (engine, repo, _, iid) = await StartToReviewAsync(Chain("", NextName("recall")));
        var review = await DoingAsync(repo, iid, "review");
        await engine.ExecuteProcessTaskAsync(review!.TaskId!.Value, "userA", Submit(Agree));
        var oldReview = await FinishedRowAsync(repo, iid, "review");
        var before = await TotalRowsAsync(repo, iid);

        var ex = await Assert.ThrowsAsync<JeeflowException>(
            () => engine.RecallTaskAsync(oldReview!.TaskId!.Value, "userA"));
        Assert.Equal("节点[review]未开启 allowRecall，不能撤回", ex.Message);
        Assert.Equal(before, await TotalRowsAsync(repo, iid));
        Assert.Empty(RecallSupport.HistoryOf(await repo.FindTaskByIdAsync(oldReview.TaskId)));
        Assert.Equal((int)WfInstanceState.Doing, (await repo.FindInstanceByIdAsync(iid))!.State);
    }

    /// <summary>非参与者拦（逐字）、<c>flow.admin</c> 特权放行、进行中行拦（逐字）。</summary>
    [Fact]
    public async Task RecallPermissionAndNotFinishedGuards()
    {
        var (engine, repo, _, iid) = await StartToReviewAsync(Chain("\"allowRecall\":true", NextName("recall")));
        var review = await DoingAsync(repo, iid, "review");
        await engine.ExecuteProcessTaskAsync(review!.TaskId!.Value, "userA", Submit(Agree));
        var oldReview = await FinishedRowAsync(repo, iid, "review");
        var before = await TotalRowsAsync(repo, iid);

        // 承重判据 3：目标行是 FINISHED，参与人判断按名册、不走只认 DOING 的 IsAllowed
        var ex = await Assert.ThrowsAsync<JeeflowException>(
            () => engine.RecallTaskAsync(oldReview!.TaskId!.Value, "userZ"));
        Assert.Equal("无权限撤回该任务", ex.Message);
        Assert.Equal(before, await TotalRowsAsync(repo, iid));

        // flow.admin 特权放行（与转办/实例撤回同口径）
        var resurrected = await engine.RecallTaskAsync(oldReview.TaskId!.Value, "flow.admin");
        Assert.True(resurrected.IsDoing());

        // 进行中行拦（复活行还没办结）
        var ex2 = await Assert.ThrowsAsync<JeeflowException>(
            () => engine.RecallTaskAsync(resurrected.TaskId!.Value, "userA"));
        Assert.Equal("任务非已完成状态，不可撤回", ex2.Message);
    }

    [Fact]
    public async Task RecallBlockedWhenInstanceFinished()
    {
        var (engine, repo, _, iid) = await StartToReviewAsync(Chain("\"allowRecall\":true", NextName("recall")));
        var review = await DoingAsync(repo, iid, "review");
        await engine.ExecuteProcessTaskAsync(review!.TaskId!.Value, "userA", Submit(Agree));
        var finalTask = await DoingAsync(repo, iid, "final");
        await engine.ExecuteProcessTaskAsync(finalTask!.TaskId!.Value, "userF", Submit(Agree));
        Assert.Equal((int)WfInstanceState.Finished, (await repo.FindInstanceByIdAsync(iid))!.State);
        var oldReview = await FinishedRowAsync(repo, iid, "review");
        var before = await TotalRowsAsync(repo, iid);

        var ex = await Assert.ThrowsAsync<JeeflowException>(
            () => engine.RecallTaskAsync(oldReview!.TaskId!.Value, "userA"));
        Assert.Equal("流程实例非进行中状态，不可撤回", ex.Message);
        Assert.Equal(before, await TotalRowsAsync(repo, iid));
        Assert.Equal((int)WfInstanceState.Finished, (await repo.FindInstanceByIdAsync(iid))!.State);
    }

    // ═══ ⑥⑦ 连带废弃两档（java 格 6/7）═══

    /// <summary>后加签行属下游 DOING ⇒ 撤回连带废弃，不留孤儿待办；复活行照常推进。</summary>
    [Fact]
    public async Task RecallAbandonsAddSignRowsToo()
    {
        var (engine, repo, _, iid) = await StartToReviewAsync(
            Chain("\"allowRecall\":true,\"allowAddSign\":true", NextName("recall")));
        var review = await DoingAsync(repo, iid, "review");
        await engine.ExecuteProcessTaskAsync(review!.TaskId!.Value, "userA", new FlowData
        {
            [FlowConst.SubmitType] = Agree,
            [FlowConst.AddSignOperators] = "userX",
        });
        var addSignRow = await DoingAsync(repo, iid, "review");
        Assert.NotNull(addSignRow);
        Assert.Contains("userX", addSignRow!.ActorIds);
        var oldReview = await FinishedRowAsync(repo, iid, "review");

        await engine.RecallTaskAsync(oldReview!.TaskId!.Value, "userA");

        Assert.Equal((int)WfTaskState.Abandon,
            (await repo.FindTaskByIdAsync(addSignRow.TaskId))!.TaskState);
        Assert.Equal(1, await DoingCountAsync(repo, iid, "review"));   // 只剩复活行这一条活待办
        var resurrected = await DoingAsync(repo, iid, "review");
        await engine.ExecuteProcessTaskAsync(resurrected!.TaskId!.Value, "userA", Submit(Agree));
        Assert.NotNull(await DoingAsync(repo, iid, "final"));          // 无兄弟待办 ⇒ 放行出边
    }

    [Fact]
    public async Task RecallAbandonsBothForkBranches()
    {
        var (engine, repo, _, iid) = await StartToReviewAsync(ChainFork("\"allowRecall\":true", NextName("recallfork")));
        var review = await DoingAsync(repo, iid, "review");
        await engine.ExecuteProcessTaskAsync(review!.TaskId!.Value, "userA", Submit(Agree));
        Assert.NotNull(await DoingAsync(repo, iid, "payA"));
        Assert.NotNull(await DoingAsync(repo, iid, "payB"));
        var oldReview = await FinishedRowAsync(repo, iid, "review");

        await engine.RecallTaskAsync(oldReview!.TaskId!.Value, "userA");

        Assert.Equal((int)WfTaskState.Abandon, (await AnyRowAsync(repo, iid, "payA"))!.TaskState);
        Assert.Equal((int)WfTaskState.Abandon, (await AnyRowAsync(repo, iid, "payB"))!.TaskState);
        Assert.Equal(1, await DoingCountAsync(repo, iid, "review"));
        Assert.Equal(0, await DoingCountAsync(repo, iid, "payA"));
        Assert.Equal(0, await DoingCountAsync(repo, iid, "payB"));
    }

    // ═══ ⑧ 解析期禁配（java 格 8）═══

    /// <summary>会签节点／申请节点配 <c>allowRecall</c> ⇒ 解析期直接报错（不做运行时降级）。</summary>
    [Fact]
    public void ParseBansAllowRecallOnCountersignAndApplyNodes()
    {
        var (_, _, ctx) = NewStack();
        // 会签那一格用字符串替换写 performType=1（本栈 System.Text.Json 拒绝重复键，
        // 见 AddSignAfterTests.Chain 的注释；java 那边靠 Jackson"重复键后者覆盖"）
        var countersign = Chain("\"allowRecall\":true,\"countersignType\":\"PARALLEL\"", "cs")
            .Replace("\"form\":\"form-r\",\"taskType\":0,\"performType\":0",
                     "\"form\":\"form-r\",\"taskType\":0,\"performType\":1");
        var ex1 = Assert.Throws<JeeflowException>(() =>
            ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(countersign), ctx));
        Assert.Contains("不允许配置 allowRecall", ex1.Message);
        Assert.Contains("会签节点", ex1.Message);

        var applyOn = Chain("", "ap").Replace(
            ApplyNode, ApplyNode + ",\"allowRecall\":true");
        var ex2 = Assert.Throws<JeeflowException>(() =>
            ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(applyOn), ctx));
        Assert.Contains("首任务节点", ex2.Message);
        Assert.Contains("allowRecall", ex2.Message);
    }

    /// <summary>开关声明成模型字段后不得再镜像进 ext（双源就此断掉），非法值解析期报错不静默当缺省。</summary>
    [Fact]
    public void AllowRecallKeyNotMirroredAndIllegalValueFailsAtParse()
    {
        var (_, _, ctx) = NewStack();
        var model = ModelParser.Parse(
            System.Text.Encoding.UTF8.GetBytes(Chain("\"allowRecall\":true", "x")), ctx);
        var review = Assert.IsAssignableFrom<TaskModel>(model.GetNode("review"));
        Assert.True(review.AllowRecall);
        Assert.False(review.Ext.ContainsKey(RecallSupport.PropAllowRecall));

        var ex = Assert.Throws<JeeflowException>(() => ModelParser.Parse(
            System.Text.Encoding.UTF8.GetBytes(Chain("\"allowRecall\":\"yes\"", "y")), ctx));
        Assert.Contains("allowRecall 配置非法", ex.Message);
    }

    // ═══ ⑨ 逐字文案自证（java 格 9）═══

    [Fact]
    public void PinnedMessagesMatchSpec()
    {
        Assert.Equal("任务[{0}]的后续节点已办理，不能撤回", RecallSupport.MsgWindow);
        Assert.Equal("节点[{0}]未开启 allowRecall，不能撤回", RecallSupport.MsgSwitchOff);
        Assert.Equal("任务非已完成状态，不可撤回", RecallSupport.MsgNotFinished);
        Assert.Equal("无权限撤回该任务", RecallSupport.MsgNoPermission);
        Assert.Equal("流程实例非进行中状态，不可撤回", RecallSupport.MsgInstanceNotDoing);
    }

    // ═══ ⑩ 门面出口（本栈补的两格之一）═══

    /// <summary>门面 <c>processTask/recall</c>：成功信封两键（id 字符串化）＋ <c>operator</c> 硬必填
    /// ＋主键缺失响亮报错，绝不拿 <c>''</c>/<c>0</c> 当 id 落库。</summary>
    [Fact]
    public async Task FacadeRecallActionEnvelopeAndRequiredArgs()
    {
        var (engine, repo, ctx, iid) = await StartToReviewAsync(Chain("\"allowRecall\":true", NextName("recall")));
        var facade = new JeeflowFacade(ctx);
        var review = await DoingAsync(repo, iid, "review");
        await engine.ExecuteProcessTaskAsync(review!.TaskId!.Value, "userA", Submit(Agree));
        var oldReview = await FinishedRowAsync(repo, iid, "review");

        var resp = await facade.FlowAsync("processTask/recall", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = oldReview!.TaskId,
            ["operator"] = "userA",
        });
        Assert.Equal(0, resp["code"]);
        var data = Assert.IsAssignableFrom<IDictionary<string, object?>>(resp["data"]!);
        var newId = long.Parse(data["processTaskId"]!.ToString()!);
        Assert.Equal((int)WfTaskState.Doing, Convert.ToInt32(data["taskState"]));
        Assert.True((await repo.FindTaskByIdAsync(newId))!.IsDoing());

        var noOp = await facade.FlowAsync("processTask/recall",
            new FlowData { [FlowConst.ProcessTaskIdKey] = newId });
        Assert.Equal(99999999, noOp["code"]);
        Assert.Equal("operator 必填", noOp["msg"]);

        var noId = await facade.FlowAsync("processTask/recall",
            new FlowData { ["operator"] = "userA" });
        Assert.Equal(99999999, noId["code"]);
        Assert.Equal("processTaskId 缺失", noId["msg"]);
    }

    // ═══ ⑪ 事件码（本栈补的两格之二）═══

    /// <summary>码 10 <c>TASK_RECALL</c>：落库之后 fire <b>一次</b>（不逐任务、不补发 5/6），
    /// 复活行自己的待办另走码 3；载荷三键齐、sourceId＝被撤回的旧行。</summary>
    [Fact]
    public async Task RecallFiresCode10OnceAndResurrectedRowFiresCode3()
    {
        var (engine, repo, ctx) = NewStack();
        var probe = new RecallProbe();
        ctx.RegisterEventListener(probe);
        var defineId = await TestInfra.SaveFlowDefineAsync(repo, NextName("recall-ev"),
            Chain("\"allowRecall\":true", "recall-ev-" + _seq));
        var inst = await engine.StartProcessInstanceByIdAsync(defineId, "applicant", new FlowData());
        var iid = inst.InstanceId!.Value;
        foreach (var t in await repo.FindDoingTasksAsync(iid, null))
            await engine.ExecuteProcessTaskAsync(t.TaskId!.Value, "applicant", Submit(Apply));
        var review = await DoingAsync(repo, iid, "review");
        await engine.ExecuteProcessTaskAsync(review!.TaskId!.Value, "userA", Submit(Agree));
        var oldReview = await FinishedRowAsync(repo, iid, "review");
        probe.Clear();   // 只裁撤回这一次调用

        var resurrected = await engine.RecallTaskAsync(oldReview!.TaskId!.Value, "userA");

        Assert.Equal(1, probe.Names.Count(n => n == "TaskRecall"));        // 每轮撤回只 fire 一次
        Assert.Equal(1, probe.Names.Count(n => n == "ProcessTaskStart"));  // 复活行一条码 3
        Assert.Equal(0, probe.Names.Count(n => n == "TaskComplete"));      // 不补发 5
        Assert.Equal(0, probe.Names.Count(n => n == "TaskReject"));        // 不补发 6
        var ev = probe.Events.First(e => e.EventType == ProcessEventType.TaskRecall);
        Assert.Equal(oldReview.TaskId, ev.SourceId);                       // sourceId＝被撤回的旧行
        Assert.Equal(iid, Convert.ToInt64(ev.Data["instanceId"]!));
        Assert.Equal(oldReview.TaskId, Convert.ToInt64(ev.Data["taskId"]!));
        Assert.Equal("userA", ev.Data["operator"]);
        Assert.Equal(10, (int)ProcessEventType.TaskRecall);
        var start = probe.Events.First(e => e.EventType == ProcessEventType.ProcessTaskStart);
        Assert.Equal(resurrected.TaskId, start.SourceId);                  // 码 3 钉在复活行上
    }

    /// <summary>报错那次一行不写 ⇒ 码 10 与码 3 都一次不发（§11.2 原则 3 的反面：不发未成立的事实）。</summary>
    [Fact]
    public async Task BlockedRecallFiresNoEvent()
    {
        var (engine, repo, ctx) = NewStack();
        var probe = new RecallProbe();
        ctx.RegisterEventListener(probe);
        var defineId = await TestInfra.SaveFlowDefineAsync(repo, NextName("recall-no"),
            Chain("\"allowRecall\":true", "recall-no-" + _seq));
        var inst = await engine.StartProcessInstanceByIdAsync(defineId, "applicant", new FlowData());
        var iid = inst.InstanceId!.Value;
        foreach (var t in await repo.FindDoingTasksAsync(iid, null))
            await engine.ExecuteProcessTaskAsync(t.TaskId!.Value, "applicant", Submit(Apply));
        var review = await DoingAsync(repo, iid, "review");
        await engine.ExecuteProcessTaskAsync(review!.TaskId!.Value, "userA", Submit(Agree));
        var oldReview = await FinishedRowAsync(repo, iid, "review");
        probe.Clear();

        await Assert.ThrowsAsync<JeeflowException>(
            () => engine.RecallTaskAsync(oldReview!.TaskId!.Value, "userZ"));

        Assert.Equal(0, probe.Names.Count(n => n == "TaskRecall"));
        Assert.Equal(0, probe.Names.Count(n => n == "ProcessTaskStart"));
    }

    /// <summary>
    /// 定档用例（owner 2026-11-04，规范 04 · 已办撤回第 3 条）：复活行**走建单收口** ⇒
    /// 名册＝原行名册 ＋ 当时生效委托并入（不摘原人），与普通建单同尺。
    ///
    /// <para>本栈首版逐字照 java 直调 <c>SaveTaskAsync</c>  ⇒ 不并，与 go／python／node／rust／moon
    /// 五栈相反（5-3 分裂）；旧注释的理由"再并一次代理人等于把'你自己重办'改成'别人替你办'"被定档推翻——
    /// 并入<b>不摘原人</b>，原办理人仍在名册里、照样能办，只是同时让受托人也看得见这一手。</para>
    ///
    /// <para><b>台账必须在 review 建单之后才登记</b>：不设这条前置自证，"复活行含 userB"可能被
    /// "建单时就已并入、原行本来就有 userB"顶包，这一格就照不到撤回这条腿（java 侧同口径用例
    /// 第一版正是这么红的：expected [userA] but was [userA, userB]）。</para>
    /// </summary>
    [Fact]
    public async Task R246_ResurrectedRowMergesEffectiveSurrogate()
    {
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

        var flowJson = Chain("\"allowRecall\":true", NextName("rc246sur"));
        var defineId = await TestInfra.SaveFlowDefineAsync(repo, FlowName(flowJson), flowJson);
        var inst = await engine.StartProcessInstanceByIdAsync(defineId, "applicant", new FlowData());
        foreach (var t in await repo.FindDoingTasksAsync(inst.InstanceId!.Value, null))
            await engine.ExecuteProcessTaskAsync(t.TaskId!.Value, "applicant", Submit(Apply));
        var review = await DoingAsync(repo, inst.InstanceId!.Value, "review");
        await engine.ExecuteProcessTaskAsync(review!.TaskId!.Value, "userA", Submit(Agree));

        var oldRow = await FinishedRowAsync(repo, inst.InstanceId!.Value, "review");
        var rosterBefore = oldRow!.ActorIds.ToList();
        Assert.Equal(new[] { "userA" }, rosterBefore);   // 前置：台账还没登记，原行只有办理人本人

        await ext.SaveSurrogateAsync(new ProcessSurrogate
        {
            ProcessName = "",          // 空＝全流程兜底台账
            Operator = "userA",
            Surrogate = "userB",
            Enabled = 1,
            CreateUser = "tester",
        });

        var revived = await engine.RecallTaskAsync(oldRow.TaskId!.Value, "userA");
        Assert.Contains("userB", revived.ActorIds);      // 复活行须并上当时生效的受托人
        Assert.Contains("userA", revived.ActorIds);      // 并入不摘原人
        Assert.Equal(2, (await repo.FindTaskActorsAsync(revived.TaskId!.Value)).Count);  // 真落参与者表
        Assert.Equal(rosterBefore, oldRow.ActorIds);     // 原 FINISHED 行的名册一字不动
    }

    /// <summary>只记事件名与事件体的探针（本格判据是"发没发、发几次、载荷对不对"，不需要反查仓储）。
    /// <see cref="Clear"/> 用来把"发起＋办理"那几拍已经产生的事件清空，只裁撤回这一次调用。</summary>
    private sealed class RecallProbe : IProcessEventListener
    {
        public List<string> Names { get; } = new();
        public List<ProcessEvent> Events { get; } = new();

        public void Clear()
        {
            Names.Clear();
            Events.Clear();
        }

        public Task OnEventAsync(ProcessEvent @event)
        {
            Names.Add(@event.EventType.ToString());
            Events.Add(@event);
            return Task.CompletedTask;
        }
    }
}

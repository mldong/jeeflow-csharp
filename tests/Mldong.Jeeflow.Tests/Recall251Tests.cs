using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// 「已办撤回 allowRecall 快照投影」（issues/251 · C# 腿）——移植基准＝jeeflow-java
/// <c>JeeflowFacadeTest.testDoneRowCarriesAllowRecallSnapshot</c> ／
/// <c>testAllowRecallSnapshotSemanticsAndMissingRow</c>；夹具纪律照 <see cref="Recall246Tests"/>
/// （一律内联 JSON，<b>不碰共享 flows/ 目录</b>——那是八仓镜像＋漂移门禁的另一条腿）。
///
/// <para>钉住定案形状四件：① 办理时把<b>当前节点</b>开关盖成 <c>tf_allowRecall</c>（缺省 false 也盖章，
/// 键恒在）；② <c>tf_</c> 键不进实例变量；③ 出口只搬<b>已办行</b>（DOING 不投、缺键不投、布尔走
/// <c>RowFirstIsTrue</c> 与 java <c>Boolean.parseBoolean</c> 同口径）；④ 撤回判据一行未动
/// （本文件只测快照投影，不测撤回本身）。</para>
///
/// <para>承重档：<b>缺省 false 也盖章</b>（格子2）——若把盖章守卫误写成「开关为真才盖」
/// （<c>RecallSupport.NodeEnabled</c> 语义），键就缺失、出口投不出 false，本格报红；
/// <b>DOING 行即便带键也不投</b>（格子5 直调内部投影）——若漏了 DOING 守卫，本格报红。</para>
/// </summary>
public class Recall251Tests
{
    private const int Agree = (int)WfSubmitType.Agree;    // 1
    private const int Apply = (int)WfSubmitType.Apply;    // 0
    private const int Reject = (int)WfSubmitType.Reject;  // 2

    private static FlowData Submit(int type) => new() { [FlowConst.SubmitType] = type };

    // ═══ 夹具（五节点链 start→apply→review→final→end，逐字照 Recall246Tests.Chain）═══

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

    /// <summary>五节点链：review 办理人 userA、final 办理人 userF、apply 办理人 applicant。</summary>
    private static string Chain(string reviewProps, string name) =>
        Head(name, "撤回开关快照夹具")
        + Node("apply", ApplyNode, "发起申请") + ","
        + ReviewNode(reviewProps) + ","
        + Node("final", "\"form\":\"form-f\",\"assignee\":\"userF\",\"taskType\":0,\"performType\":0", "终审") + ","
        + EndNode
        + "],\"edges\":["
        + Edge("e1", "start", "apply") + "," + Edge("e2", "apply", "review") + ","
        + Edge("e3", "review", "final") + "," + Edge("e4", "final", "end")
        + "]}";

    private int _seq;
    private string NextName(string prefix) => $"{prefix}-{++_seq}";

    private static (JeeflowEngine Engine, MemoryRepository Repo, ServiceContext Ctx) NewStack()
        => TestInfra.NewEngineWithCtx();

    /// <summary>发起并办结申请节点，返回停在 review 的实例 id（照 Recall246Tests.StartToReviewAsync）。</summary>
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

    private static string FlowName(string flowJson)
    {
        var i = flowJson.IndexOf("\"name\":\"", StringComparison.Ordinal);
        var s = flowJson[(i + 8)..];
        return s[..s.IndexOf('"', StringComparison.Ordinal)];
    }

    private static async Task<ProcessTask?> DoingAsync(MemoryRepository repo, long iid, string nodeName) =>
        (await repo.FindDoingTasksAsync(iid, null)).FirstOrDefault(t => t.TaskName == nodeName);

    private static async Task<ProcessTask?> FinishedRowAsync(MemoryRepository repo, long iid, string nodeName) =>
        (await repo.FindHistoryTasksAsync(iid))
            .FirstOrDefault(t => t.TaskName == nodeName && t.IsFinished());

    /// <summary>分页出口第一行（照 FacadeTests 的 rows 取数姿势）。</summary>
    private static Dictionary<string, object?> FirstRow(Dictionary<string, object?> resp)
    {
        var data = (Dictionary<string, object?>)resp["data"]!;
        return (Dictionary<string, object?>)((List<object?>)data["rows"]!)[0]!;
    }

    // ═══ 格子1：办理时盖章 true ＋ 不传播（实例变量零 tf_、办理前待办行无键）═══

    [Fact]
    public async Task R251_StampsTrueAtHandlingAndNeverPropagatesToInstance()
    {
        var (engine, repo, _, iid) = await StartToReviewAsync(Chain("\"allowRecall\":true", NextName("r251")));
        var review = await DoingAsync(repo, iid, "review");
        Assert.NotNull(review);
        // 办理前：待办行无快照键——键只在办理时刻盖上（java ③ 不传播·办理前）
        Assert.False(review!.Variables.ContainsKey(FlowConst.AllowRecall));

        await engine.ExecuteProcessTaskAsync(review.TaskId!.Value, "userA", Submit(Agree));

        var oldReview = await FinishedRowAsync(repo, iid, "review");
        Assert.NotNull(oldReview);
        // 办理时盖章：已完成行变量带 tf_allowRecall=true（原生 bool，未经 JSON 往返）
        Assert.True(oldReview!.Variables.ContainsKey(FlowConst.AllowRecall));
        Assert.Equal(true, oldReview.Variables[FlowConst.AllowRecall]);
        // 不传播：实例变量零 tf_ 键（CompleteTask 合并时剥 tf_，java ③）
        var inst = await repo.FindInstanceByIdAsync(iid);
        Assert.NotNull(inst);
        Assert.DoesNotContain(inst!.Variables.Keys,
            k => k.StartsWith(FlowConst.TaskFormDataPrefix, StringComparison.Ordinal));
    }

    // ═══ 格子2：缺省 false 也盖章（承重——区分「盖章 false」与「未盖章缺键」）═══

    [Fact]
    public async Task R251_StampsDefaultFalseIsLoadBearing()
    {
        var (engine, repo, _, iid) = await StartToReviewAsync(Chain("", NextName("r251")));
        var review = await DoingAsync(repo, iid, "review");
        Assert.NotNull(review);
        await engine.ExecuteProcessTaskAsync(review!.TaskId!.Value, "userA", Submit(Agree));

        var oldReview = await FinishedRowAsync(repo, iid, "review");
        Assert.NotNull(oldReview);
        // 节点未配开关 ⇒ 缺省 false，但键必须恒在、值为 false。
        // 若守卫误用 NodeEnabled（开关为真才盖），这里键缺失 ⇒ 报红。
        Assert.True(oldReview!.Variables.ContainsKey(FlowConst.AllowRecall));
        Assert.Equal(false, oldReview.Variables[FlowConst.AllowRecall]);
    }

    // ═══ 格子3：doneList 顶层投影（review true／final false）＋ todoList 不投 ＋ 同名隔离 ═══

    [Fact]
    public async Task R251_DoneListProjectsTopLevelAndTodoListDoesNot()
    {
        var (engine, repo, ctx, iid) = await StartToReviewAsync(Chain("\"allowRecall\":true", NextName("r251")));
        var facade = new JeeflowFacade(ctx);
        var review = await DoingAsync(repo, iid, "review");
        Assert.NotNull(review);
        // 同名隔离：办理 args 塞业务裸键 allowRecall=custom（与快照 tf_allowRecall 互不干扰，java ②）
        await engine.ExecuteProcessTaskAsync(review!.TaskId!.Value, "userA", new FlowData
        {
            [FlowConst.SubmitType] = Agree,
            ["allowRecall"] = "custom",
        });

        // final 现在是 DOING：待办行不投顶层 allowRecall
        var todo = await facade.FlowAsync("processTask/todoList", new FlowData { ["operator"] = "userF" });
        Assert.Equal(0, todo["code"]);
        var todoRow = FirstRow(todo);
        Assert.Equal("final", todoRow["taskName"]);
        Assert.False(todoRow.ContainsKey("allowRecall"));

        // 办结 final（节点未配开关 ⇒ 缺省 false 盖章）
        var final = await DoingAsync(repo, iid, "final");
        Assert.NotNull(final);
        await engine.ExecuteProcessTaskAsync(final!.TaskId!.Value, "userF", Submit(Agree));

        // 已办 review 行：顶层快照 true，ext 里业务同名键 custom 原样保留
        var doneA = await facade.FlowAsync("processTask/doneList", new FlowData { ["operator"] = "userA" });
        Assert.Equal(0, doneA["code"]);
        var reviewRow = FirstRow(doneA);
        Assert.Equal("review", reviewRow["taskName"]);
        Assert.Equal(true, reviewRow["allowRecall"]);
        var ext = (Dictionary<string, object?>)reviewRow["ext"]!;
        Assert.Equal("custom", ext["allowRecall"]);

        // 已办 final 行：顶层快照 false（缺省盖章，不是缺失）
        var doneF = await facade.FlowAsync("processTask/doneList", new FlowData { ["operator"] = "userF" });
        Assert.Equal(0, doneF["code"]);
        var finalRow = FirstRow(doneF);
        Assert.Equal("final", finalRow["taskName"]);
        Assert.Equal(false, finalRow["allowRecall"]);
    }

    // ═══ 格子4：instanceDetail 已办行投／DOING 行不投 ＋ processTask/detail 单任务出口不投 ═══

    [Fact]
    public async Task R251_InstanceDetailProjectsDoneNotDoingAndDetailEndpointDoesNot()
    {
        var (engine, repo, ctx, iid) = await StartToReviewAsync(Chain("\"allowRecall\":true", NextName("r251")));
        var facade = new JeeflowFacade(ctx);
        var review = await DoingAsync(repo, iid, "review");
        Assert.NotNull(review);
        await engine.ExecuteProcessTaskAsync(review!.TaskId!.Value, "userA", Submit(Agree));
        // 此刻 review FINISHED、final DOING、apply FINISHED

        var detail = await facade.FlowAsync("processInstance/detail", new FlowData { ["id"] = iid });
        Assert.Equal(0, detail["code"]);
        var data = (Dictionary<string, object?>)detail["data"]!;
        var tasks = (List<object?>)data["tasks"]!;
        Dictionary<string, object?>? reviewVo = null, finalVo = null, applyVo = null;
        foreach (var o in tasks)
        {
            var t = (Dictionary<string, object?>)o!;
            switch (t["taskName"])
            {
                case "review": reviewVo = t; break;
                case "final": finalVo = t; break;
                case "apply": applyVo = t; break;
            }
        }
        Assert.NotNull(reviewVo);
        Assert.NotNull(finalVo);
        Assert.NotNull(applyVo);
        // 已办行投顶层：review true、apply false（首任务节点禁配 ⇒ 缺省盖章 false）
        Assert.Equal(true, reviewVo!["allowRecall"]);
        Assert.Equal(false, applyVo!["allowRecall"]);
        // DOING 行不投（即便实例详情里它和已办行同列）
        Assert.Equal((int)WfTaskState.Doing, Convert.ToInt32(finalVo!["taskState"]));
        Assert.False(finalVo.ContainsKey("allowRecall"));

        // processTask/detail 单任务出口不投顶层（TaskVo 未动，java 同口径）
        var single = await facade.FlowAsync("processTask/detail",
            new FlowData { ["id"] = review.TaskId, ["operator"] = "userA" });
        Assert.Equal(0, single["code"]);
        var singleData = (Dictionary<string, object?>)single["data"]!;
        Assert.False(singleData.ContainsKey("allowRecall"));
    }

    // ═══ 格子5：直调内部投影——DOING 带键也不投／缺键不投／布尔语义（RowFirstIsTrue）═══

    [Fact]
    public void R251_DoingRowWithKeyNeverProjectsAndBooleanSemantics()
    {
        // DOING 行即便变量带 tf_allowRecall=true 也绝不投顶层（守卫直证）
        var doing = new IProcessRepository.TaskRow
        {
            Id = 1,
            ProcessInstanceId = 1,
            TaskName = "review",
            TaskState = (int)WfTaskState.Doing,
            Variable = "{\"tf_allowRecall\":true}",
        };
        Assert.False(JeeflowFacade.TaskRowToMap(doing).ContainsKey("allowRecall"));

        // FINISHED 带键 true → 投 true
        var finTrue = new IProcessRepository.TaskRow
        {
            TaskState = (int)WfTaskState.Finished,
            Variable = "{\"tf_allowRecall\":true}",
        };
        Assert.Equal(true, JeeflowFacade.TaskRowToMap(finTrue)["allowRecall"]);

        // FINISHED 带键 false → 投 false（不是缺失）
        var finFalse = new IProcessRepository.TaskRow
        {
            TaskState = (int)WfTaskState.Finished,
            Variable = "{\"tf_allowRecall\":false}",
        };
        Assert.Equal(false, JeeflowFacade.TaskRowToMap(finFalse)["allowRecall"]);

        // FINISHED 缺键（存量行/未盖章）→ 不投
        var finMissing = new IProcessRepository.TaskRow
        {
            TaskState = (int)WfTaskState.Finished,
            Variable = "{\"foo\":\"bar\"}",
        };
        Assert.False(JeeflowFacade.TaskRowToMap(finMissing).ContainsKey("allowRecall"));

        // 布尔语义：字符串 "true" → true；脏值 "1" → false（与 java Boolean.parseBoolean 同口径，
        // 不做「非空即真」，否则脏值会把行上的 false 翻成 true）
        var finStrTrue = new IProcessRepository.TaskRow
        {
            TaskState = (int)WfTaskState.Finished,
            Variable = "{\"tf_allowRecall\":\"true\"}",
        };
        Assert.Equal(true, JeeflowFacade.TaskRowToMap(finStrTrue)["allowRecall"]);
        var finDirty = new IProcessRepository.TaskRow
        {
            TaskState = (int)WfTaskState.Finished,
            Variable = "{\"tf_allowRecall\":\"1\"}",
        };
        Assert.Equal(false, JeeflowFacade.TaskRowToMap(finDirty)["allowRecall"]);
    }

    // ═══ 格子6：跳转腿／拒绝腿也盖章（四条 execute 路径同收口 PrepareExecutionAsync）═══

    [Fact]
    public async Task R251_JumpAndRejectLegsAlsoStamp()
    {
        // 跳转腿：命名跳转到 final，review 办结行仍盖章 true
        var (engine, repo, _, iid) = await StartToReviewAsync(Chain("\"allowRecall\":true", NextName("r251jump")));
        var review = await DoingAsync(repo, iid, "review");
        Assert.NotNull(review);
        await engine.ExecuteAndJumpTaskAsync(review!.TaskId!.Value, "userA", Submit(Agree), "final");
        var jumpedReview = await FinishedRowAsync(repo, iid, "review");
        Assert.NotNull(jumpedReview);
        Assert.Equal(true, jumpedReview!.Variables[FlowConst.AllowRecall]);

        // 拒绝腿：jumpToEnd（submitType=2 同路），review 办结行仍盖章 true，
        // 且实例已 REJECT(45) 时已办出口照投 true（快照与实例终态无关）
        var (engine2, repo2, ctx2, iid2) = await StartToReviewAsync(Chain("\"allowRecall\":true", NextName("r251rej")));
        var facade2 = new JeeflowFacade(ctx2);
        var review2 = await DoingAsync(repo2, iid2, "review");
        Assert.NotNull(review2);
        await engine2.ExecuteAndJumpToEndAsync(review2!.TaskId!.Value, "userA", Submit(Reject));
        Assert.Equal((int)WfInstanceState.Reject, (await repo2.FindInstanceByIdAsync(iid2))!.State);
        var rejReview = await FinishedRowAsync(repo2, iid2, "review");
        Assert.NotNull(rejReview);
        Assert.Equal(true, rejReview!.Variables[FlowConst.AllowRecall]);
        var done = await facade2.FlowAsync("processTask/doneList", new FlowData { ["operator"] = "userA" });
        Assert.Equal(0, done["code"]);
        Assert.Equal(true, FirstRow(done)["allowRecall"]);
    }

    // ═══ 格子7（照 java 漂移档）：办结后改定义开关，出口仍搬办理时刻快照（不重解析定义）═══

    [Fact]
    public async Task R251_SnapshotSurvivesDefinitionDrift()
    {
        var flowJson = Chain("\"allowRecall\":true", NextName("r251drift"));
        var (engine, repo, ctx, iid) = await StartToReviewAsync(flowJson);
        var facade = new JeeflowFacade(ctx);
        var review = await DoingAsync(repo, iid, "review");
        Assert.NotNull(review);
        await engine.ExecuteProcessTaskAsync(review!.TaskId!.Value, "userA", Submit(Agree));

        // 办结后把定义开关 true→false（漂移）：出口只搬运办理时刻快照，不重解析定义。
        // 撤回校验此时会按现读定义报「未开启」——预判与校验的分歧是设计内（后端权威），本档只钉投影。
        var inst = await repo.FindInstanceByIdAsync(iid);
        Assert.NotNull(inst);
        var def = await repo.FindDefineByIdAsync(inst!.DefineId);
        Assert.NotNull(def);
        var flipped = flowJson.Replace("\"allowRecall\":true", "\"allowRecall\":false");
        def!.Content = System.Text.Encoding.UTF8.GetBytes(flipped);
        await repo.UpdateDefineAsync(def);

        var done = await facade.FlowAsync("processTask/doneList", new FlowData { ["operator"] = "userA" });
        Assert.Equal(0, done["code"]);
        Assert.Equal(true, FirstRow(done)["allowRecall"]);   // 仍是办理时刻 true
    }
}
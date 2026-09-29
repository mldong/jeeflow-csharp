using Xunit;
using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// 「未知档节点在链路中间」的两条腿（issues/143 ＋ issues/141 G4 义务 2 的 c# 补票）。
///
/// <para><b>本栈的两处形状与另外两栈不同，但同一个病灶</b>：
/// ① <c>ModelParser.ParseNode</c> 认不出类型时 <c>_ =&gt; (NodeModel?)null</c> 直接丢节点，
///   而且<b>一条日志都不记</b>——spec 02「类型键的三条义务」第 2 条要求的是
///   「记一条可诊断日志（带节点 id 与实得类型串）再决定跳过」，java（<c>ModelParser.java:84-88</c>）
///   与 php（<c>ModelParser.php:112-113</c>）都已落，c# 一直没落 ⇒ 本类补票。
/// ② 被丢弃的节点仍在上游节点的 <c>Outputs</c> 里留着一<b>条 target 为 null 的悬边</b>
///   （<c>ModelParser.cs:100-110</c> 只在目标节点存在于模型里时才赋 <c>Target</c>）。
///   <c>TransitionModel.ExecuteAsync</c> 原本写的是 <c>else if (Target != null)</c> ⇒
///   <b>本栈不崩</b>（java 那边是 NPE、php 那边是 <c>Call to a member function execute() on null</c>，
///   两栈各自补 null 守卫，见 java <c>DanglingTransitionTest</c>／php <c>DanglingTransitionTest</c>），
///   但它是<b>无声停住</b>：日志里没有第二条线索，排查者只知道"令牌没往前走"，
///   不知道是哪条边、指向谁 ⇒ 本类补一条落穿 WARNING（带 from/to）。</para>
///
/// <para><b>为什么停住而不是越过它继续</b>：未知档没被解析成任何模型，越过它等于用一条臆造的
/// 通路把跑不通的定义跑成功，用户面更难发现。停住的可观测结果（实例留 DOING、库里不产生越过它的行）
/// 与按 id 现查目标的那几栈一致（go <c>findNode(..)!=nil</c>／python／node／moon 返回 option／
/// rust 把未知节点留在模型里标 <c>Unknown</c> 再由执行腿跳过）——八栈在这一格上原本分成
/// "崩／无声停住／可诊断停住"三派，本单统一到第三派。</para>
///
/// <para>顺带补 G4 义务 3 的 c# 一角：本栈类型表只有大写 <c>subProcess</c>／<c>wfSubProcess</c>，
/// 没有 spec 02 立的小写规范名 <c>subprocess</c> ⇒ 照文档写的定义在 c# 会被当未知档丢弃
/// （java 3d1fc98 已补小写别名并把大写标注保留一代），<see cref="LowercaseSubprocessAliasResolvesNode"/>
/// 钉住补票后的形状。</para>
/// </summary>
public class DanglingTransition143Tests
{
    private const string UnknownMidFlow = """
        {"name": "d143-unknown", "displayName": "未知档落穿", "type": "approval",
         "nodes": [
           {"id": "start", "type": "snaker:start", "text": {"value": "开始"}},
           {"id": "apply", "type": "snaker:task", "text": {"value": "发起申请"},
            "properties": {"assignee": "applicant", "taskType": 0, "performType": 0}},
           {"id": "odd", "type": "snaker:notRegisteredAtAll", "text": {"value": "没登记的类型"}},
           {"id": "end", "type": "snaker:end", "text": {"value": "结束"}}
         ],
         "edges": [
           {"id": "e1", "sourceNodeId": "start", "targetNodeId": "apply"},
           {"id": "e2", "sourceNodeId": "apply", "targetNodeId": "odd"},
           {"id": "e3", "sourceNodeId": "odd", "targetNodeId": "end"}
         ]}
        """;

    private const string KnownTailFlow = """
        {"name": "d143-known", "displayName": "已知档对照", "type": "approval",
         "nodes": [
           {"id": "start", "type": "snaker:start", "text": {"value": "开始"}},
           {"id": "apply", "type": "snaker:task", "text": {"value": "发起申请"},
            "properties": {"assignee": "applicant", "taskType": 0, "performType": 0}},
           {"id": "approve", "type": "snaker:task", "text": {"value": "审批"},
            "properties": {"assignee": "leader", "taskType": 0, "performType": 0}},
           {"id": "end", "type": "snaker:end", "text": {"value": "结束"}}
         ],
         "edges": [
           {"id": "e1", "sourceNodeId": "start", "targetNodeId": "apply"},
           {"id": "e2", "sourceNodeId": "apply", "targetNodeId": "approve"},
           {"id": "e3", "sourceNodeId": "approve", "targetNodeId": "end"}
         ]}
        """;

    private const string LowercaseSubprocessFlow = """
        {"name": "d143-sub", "displayName": "小写子流程别名", "type": "approval",
         "nodes": [
           {"id": "start", "type": "snaker:start", "text": {"value": "开始"}},
           {"id": "sub", "type": "snaker:subprocess", "text": {"value": "子流程"},
            "properties": {"form": "child-form", "version": 2}},
           {"id": "end", "type": "snaker:end", "text": {"value": "结束"}}
         ],
         "edges": [
           {"id": "e1", "sourceNodeId": "start", "targetNodeId": "sub"},
           {"id": "e2", "sourceNodeId": "sub", "targetNodeId": "end"}
         ]}
        """;

    private static int _defineSeq;

    private sealed class Stack
    {
        public JeeflowEngine Engine = null!;
        public MemoryRepository Repo = null!;
        public ServiceContext Ctx = null!;
        public List<string> Warnings { get; } = new();

        public static Stack New()
        {
            var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
            var s = new Stack { Engine = engine, Repo = repo, Ctx = ctx };
            // 诊断日志取证钩子（internal 成员，公开 API 面不扩）：不设时生产出口是 stderr。
            ctx.WarningSinkForTest = line => s.Warnings.Add(line);
            return s;
        }

        public async Task<long> DefineAsync(string content, string name) =>
            await TestInfra.SaveFlowDefineAsync(Repo, $"{name}-{_defineSeq++}", content);
    }

    // ═══ 1. 解析期：未知档必须留一条可诊断记录（G4 义务 2 的 c# 补票）═══

    /// <summary>
    /// 判据①：类型表查不到解析器时，节点仍不进模型（这是"跳过"），但<b>必须留下一条</b>
    /// 带 nodeId ＋ 实得类型串（含 <c>snaker:</c> 前缀原样）＋ 查表键的 WARNING。
    /// 改前会红：本栈这里是 <c>_ =&gt; null</c> ＋ 无声 <c>return null</c>，零条日志。
    /// </summary>
    [Fact]
    public void UnknownNodeTypeLogsDiagnosisAtParseTime()
    {
        var s = Stack.New();
        var model = ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(UnknownMidFlow), s.Ctx);

        Assert.Null(model.Nodes.FirstOrDefault(n => n.Name == "odd"));
        var hits = s.Warnings.Where(w => w.Contains("没有对应解析器")).ToList();
        Assert.Single(hits);
        Assert.Contains("nodeId=odd", hits[0]);
        Assert.Contains("snaker:notRegisteredAtAll", hits[0]);
        Assert.Contains("lookupKey=notRegisteredAtAll", hits[0]);
    }

    /// <summary>
    /// 现场形状取证（"为什么会停"）：未知节点没进模型，而 apply 的那条出边<b>仍在</b>，
    /// 其 <c>Target</c> 是 null ⇒ 执行腿若不守卫就是空引用；本栈守卫了，但要留线索。
    /// </summary>
    [Fact]
    public void DanglingEdgeShapeIsReproducedAtParseTime()
    {
        var s = Stack.New();
        var model = ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(UnknownMidFlow), s.Ctx);
        var apply = model.Nodes.First(n => n.Name == "apply");

        Assert.Single(apply.Outputs);
        Assert.Null(apply.Outputs[0].Target);
        Assert.Equal("odd", apply.Outputs[0].To);
    }

    // ═══ 2. 执行期：落穿要记日志并停住，严禁打断办理 ═══

    /// <summary>
    /// 判据②：发起后办掉 apply（令牌随即撞上那条悬边）⇒ 四件都要成立：
    /// ① 不抛任何异常（spec 02 §6.2 第 2 条「严禁抛错打断建单」／spec 04「节点属性配错不该把流程炸掉」）；
    /// ② 实例仍是 DOING（停住＝不办结，未知节点没被"越过"）；
    /// ③ 库里不为 <c>odd</c> 产生任何行（既不建待办也不建留痕）；
    /// ④ 落穿那一刻留一条带 from/to 的 WARNING（改前这里是无声 no-op，只有解析期一条日志）。
    /// </summary>
    [Fact]
    public async Task DanglingEdgeStopsTokenAndLogsInsteadOfSilentNoOp()
    {
        var s = Stack.New();
        var did = await s.DefineAsync(UnknownMidFlow, "d143-unknown");
        var inst = await s.Engine.StartProcessInstanceByIdAsync(did, "applicant", new FlowData());
        var iid = inst.InstanceId!.Value;

        var apply = (await s.Repo.FindDoingTasksAsync(iid, null)).First(t => t.TaskName == "apply");
        var ex = await Record.ExceptionAsync(async () =>
            await s.Engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData()));
        Assert.Null(ex);

        var fresh = await s.Repo.FindInstanceByIdAsync(iid);
        Assert.NotNull(fresh);
        Assert.Equal((int)WfInstanceState.Doing, fresh!.State);

        Assert.DoesNotContain("odd", s.Repo.Tasks.Values
            .Where(t => t.ProcessInstanceId == iid).Select(t => t.TaskName));

        var dangling = s.Warnings.Where(w => w.Contains("目标节点不在模型里")).ToList();
        Assert.Single(dangling);
        Assert.Contains("from=apply", dangling[0]);
        Assert.Contains("to=odd", dangling[0]);
    }

    // ═══ 3. 反向对照：已知档照旧跑得通，且不得冒出落穿记录 ═══

    /// <summary>
    /// 同一条流把未知档换成正常 task 链：办完两张待办后实例必须办结，
    /// 并且<b>一条</b>落穿/未知类型记录都不许有 ⇒ 防"把守卫做成什么都吞"。
    /// </summary>
    [Fact]
    public async Task KnownChainStillCompletesWithoutAnyDiagnosis()
    {
        var s = Stack.New();
        var did = await s.DefineAsync(KnownTailFlow, "d143-known");
        var inst = await s.Engine.StartProcessInstanceByIdAsync(did, "applicant", new FlowData());
        var iid = inst.InstanceId!.Value;

        var apply = (await s.Repo.FindDoingTasksAsync(iid, null)).First(t => t.TaskName == "apply");
        await s.Engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData());
        var approve = (await s.Repo.FindDoingTasksAsync(iid, null)).First(t => t.TaskName == "approve");
        await s.Engine.ExecuteProcessTaskAsync(approve.TaskId!.Value, "leader", new FlowData());

        var fresh = await s.Repo.FindInstanceByIdAsync(iid);
        Assert.NotNull(fresh);
        Assert.Equal((int)WfInstanceState.Finished, fresh!.State);
        Assert.Empty(s.Warnings.Where(w => w.Contains("目标节点不在模型里")));
        Assert.Empty(s.Warnings.Where(w => w.Contains("没有对应解析器")));
    }

    // ═══ 4. G4 义务 3：小写规范名 subprocess 在 c# 也要认（java 3d1fc98 已补，本栈缺）═══

    /// <summary>
    /// 判据③：spec 02 的表立的是小写 <c>snaker:subprocess</c>，本栈原先只认大写两档 ⇒
    /// 照文档写的定义在 c# 被当未知档<b>丢弃</b>（连带丢出边），属"按参考实现写却解析不出"。
    /// 补小写档后：解析出 <see cref="SubProcessModel"/>、form/version 到位、
    /// 且不再产生"没有对应解析器"的诊断。大写两档是 deprecate 别名，保留一代（同 java 姿势）。
    /// </summary>
    [Fact]
    public void LowercaseSubprocessAliasResolvesNode()
    {
        var s = Stack.New();
        var model = ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(LowercaseSubprocessFlow), s.Ctx);

        var sub = model.Nodes.FirstOrDefault(n => n.Name == "sub");
        Assert.NotNull(sub);
        Assert.IsType<SubProcessModel>(sub);
        Assert.Equal("child-form", ((SubProcessModel)sub!).Form);
        Assert.Equal(2, ((SubProcessModel)sub!).Version);
        Assert.Empty(s.Warnings.Where(w => w.Contains("没有对应解析器")));
    }

    /// <summary>大写两档（<c>subProcess</c>／<c>wfSubProcess</c>）仍是同值别名，不许被小写档换掉。</summary>
    [Fact]
    public void LegacyUppercaseAliasesAreKeptForOneGeneration()
    {
        var s = Stack.New();
        foreach (var rawType in new[] { "snaker:subProcess", "snaker:wfSubProcess" })
        {
            var json = LowercaseSubprocessFlow.Replace("snaker:subprocess", rawType);
            var model = ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(json), s.Ctx);
            var sub = model.Nodes.FirstOrDefault(n => n.Name == "sub");
            Assert.NotNull(sub);
            Assert.IsType<SubProcessModel>(sub);
        }
        Assert.Empty(s.Warnings.Where(w => w.Contains("没有对应解析器")));
    }
}

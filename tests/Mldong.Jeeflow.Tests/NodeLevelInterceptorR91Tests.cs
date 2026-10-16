using Xunit;
using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// audit8 R91 节点级 pre/postInterceptors 的**执行档**判据（issues/234 §4.2／spec02 §3 三条）。
///
/// <para>本栈实现早已齐（<c>Models.cs:87-99</c> 节点串空才取 <c>ProcessModel</c> 同名键，
/// <c>RunInterceptorsAsync</c> 按名解析、不可解析抛 <c>JeeflowException</c>），缺的是咬住它的判据：
/// 原先只有解析层用例（<c>TaskExtMirrorKeys231Tests</c>）＋根级执行用例（<c>PersistSmokeTests</c>），
/// "节点声明优先/回落/不叠加"这三件事在本栈没有任何一条断言 ⇒ 本轮补牙。测试件不进发布物，
/// 故这一腿零发版零镜像（对照交接卡 §2m 的射程拆分）。</para>
///
/// <para>反空转纪律：每条断言都先看 sink 里"目标节点真被调到"，再断形状；
/// 变异对照见 commit message（把回落腿改成"永远取流程根"时哪几档红）。</para>
/// </summary>
public class NodeLevelInterceptorR91Tests
{
    private sealed class Recorder : IFlowInterceptor
    {
        private readonly string _tag;
        private readonly List<string> _sink;
        public Recorder(string tag, List<string> sink) { _tag = tag; _sink = sink; }
        public Task InterceptAsync(Execution execution)
        {
            lock (_sink) _sink.Add($"{_tag}:{execution.NodeModel?.Name}");
            return Task.CompletedTask;
        }
    }

    /// <summary>start → apply(task) → end；四个占位符分别填根级 post／根级 pre／节点 post／节点 pre。</summary>
    private const string Template = """
        {"name": "__NAME__", "displayName": "R91", "type": "approval",
         "postInterceptors": "__ROOTPOST__", "preInterceptors": "__ROOTPRE__",
         "nodes": [
           {"id": "start", "type": "snaker:start", "text": {"value": "开始"}},
           {"id": "apply", "type": "snaker:task", "text": {"value": "申请"},
            "properties": {"assignee": "applicant", "taskType": 0, "performType": 0__NODEEXTRA__}},
           {"id": "end", "type": "snaker:end", "text": {"value": "结束"}}
         ],
         "edges": [
           {"id": "e1", "sourceNodeId": "start", "targetNodeId": "apply"},
           {"id": "e2", "sourceNodeId": "apply", "targetNodeId": "end"}
         ]}
        """;

    private static string Flow(string name, string rootPost = "", string rootPre = "",
        string nodePost = "", string nodePre = "")
    {
        var extra = "";
        if (nodePost.Length > 0) extra += ", \"postInterceptors\": \"" + nodePost + "\"";
        if (nodePre.Length > 0) extra += ", \"preInterceptors\": \"" + nodePre + "\"";
        return Template.Replace("__NAME__", name).Replace("__ROOTPOST__", rootPost)
            .Replace("__ROOTPRE__", rootPre).Replace("__NODEEXTRA__", extra);
    }

    private static int _seq;

    /// <summary>
    /// 发起并办结 apply ⇒ 返回拦截器调用序列。
    /// ⚠ 本栈（与 java 同形）发起时只执行 start 节点，任务节点是在**办结该任务**时才走
    /// <c>NodeModel.ExecuteAsync</c>（pre→exec→post）——第一版把断言挂在"发起之后"，
    /// 实测 sink 只有 <c>[A:start]</c>，节点档压根没被执行＝判据空转；故夹具必须推到目标节点。
    /// </summary>
    private static async Task<List<string>> RunAsync(ServiceContext ctx, JeeflowEngine engine,
        MemoryRepository repo, string json, string untilNode = "apply")
    {
        var sink = new List<string>();
        foreach (var tag in new[] { "A", "B", "C", "D" })
            ctx.NamedInterceptors["r91." + tag] = new Recorder(tag, sink);
        var defineId = await TestInfra.SaveFlowDefineAsync(repo, "r91-" + _seq++, json);
        var inst = await engine.StartProcessInstanceByIdAsync(defineId, "zhangsan", new FlowData());
        if (untilNode == "none") return sink;
        var todos = await repo.FindDoingTasksAsync(inst.InstanceId!.Value, null);
        Assert.NotEmpty(todos); // 前置：apply 的待办没建出来 ⇒ 后面所有断言都会空转
        await engine.ExecuteProcessTaskAsync(todos[0].TaskId!.Value, "zhangsan", new FlowData());
        return sink;
    }

    private static (JeeflowEngine Engine, MemoryRepository Repo, ServiceContext Ctx) Fresh()
        => TestInfra.NewEngineWithCtx();

    [Fact]
    public async Task R9101NodeDeclarationWinsNoStacking()
    {
        var (engine, repo, ctx) = Fresh();
        var sink = await RunAsync(ctx, engine, repo, Flow("r91-priority", rootPost: "r91.A", nodePost: "r91.B"));

        Assert.Contains("B:apply", sink);   // 先证节点档真被调到
        Assert.DoesNotContain("A:apply", sink); // 再证两档不叠加
    }

    [Fact]
    public async Task R9102FallsBackToProcessRoot()
    {
        var (engine, repo, ctx) = Fresh();
        var sink = await RunAsync(ctx, engine, repo, Flow("r91-fallback", rootPost: "r91.A"));

        Assert.Contains("A:apply", sink);
        Assert.Contains("A:start", sink);
    }

    [Fact]
    public async Task R9103RootAndNodePreUseSamePriority()
    {
        var (engine, repo, ctx) = Fresh();
        // 根级 pre 要读、节点级 pre 声明时不叠加
        var rootOnly = await RunAsync(ctx, engine, repo, Flow("r91-rootpre", rootPre: "r91.C"));
        Assert.Contains("C:apply", rootOnly);

        var (engine2, repo2, ctx2) = Fresh();
        var both = await RunAsync(ctx2, engine2, repo2, Flow("r91-nodepre", rootPre: "r91.C", nodePre: "r91.D"));
        Assert.Contains("D:apply", both);
        Assert.DoesNotContain("C:apply", both);
    }

    [Fact]
    public async Task R9104NodeLevelUnregisteredNameThrowsExplicitly()
    {
        var (engine, repo, ctx) = Fresh();
        ctx.NamedInterceptors.Clear();
        var defineId = await TestInfra.SaveFlowDefineAsync(repo, "r91-ghost",
            Flow("r91-ghost", nodePost: "com.mldong.jeeflow.ghost.GhostInterceptor"));

        // 幽灵名挂在 apply 节点上 ⇒ 发起不解析它（start 节点没声明就回落根级=空），
        // 必须推到"办结 apply"这一步才走得到那一串声明（见 RunAsync 的同一条前置说明）
        var inst = await engine.StartProcessInstanceByIdAsync(defineId, "zhangsan", new FlowData());
        var todos = await repo.FindDoingTasksAsync(inst.InstanceId!.Value, null);
        Assert.NotEmpty(todos);
        var ex = await Assert.ThrowsAsync<JeeflowException>(
            () => engine.ExecuteProcessTaskAsync(todos[0].TaskId!.Value, "zhangsan", new FlowData()));
        Assert.Contains("无法实例化拦截器", ex.Message);
        Assert.Contains("com.mldong.jeeflow.ghost.GhostInterceptor", ex.Message);
    }

    [Fact]
    public async Task R9105TwoNodesUseTheirOwnDeclarations()
    {
        // 本栈每次执行按名现解（无 defineId 单键缓存），这一档钉的是"节点各取各串"不被串成一串
        var (engine, repo, ctx) = Fresh();
        var sink = new List<string>();
        foreach (var tag in new[] { "A", "B" })
            ctx.NamedInterceptors["r91." + tag] = new Recorder(tag, sink);

        var json = """
            {"name": "r91-two", "displayName": "R91 两节点", "type": "approval",
             "postInterceptors": "", "preInterceptors": "",
             "nodes": [
               {"id": "start", "type": "snaker:start", "text": {"value": "开始"}},
               {"id": "apply", "type": "snaker:task", "text": {"value": "申请"},
                "properties": {"assignee": "applicant", "taskType": 0, "performType": 0,
                 "postInterceptors": "r91.A"}},
               {"id": "approval", "type": "snaker:task", "text": {"value": "审批"},
                "properties": {"assignee": "leader", "taskType": 0, "performType": 0,
                 "postInterceptors": "r91.B"}},
               {"id": "end", "type": "snaker:end", "text": {"value": "结束"}}
             ],
             "edges": [
               {"id": "e1", "sourceNodeId": "start", "targetNodeId": "apply"},
               {"id": "e2", "sourceNodeId": "apply", "targetNodeId": "approval"},
               {"id": "e3", "sourceNodeId": "approval", "targetNodeId": "end"}
             ]}
            """;
        var defineId = await TestInfra.SaveFlowDefineAsync(repo, "r91-two", json);
        var inst = await engine.StartProcessInstanceByIdAsync(defineId, "zhangsan", new FlowData());
        var apply = (await repo.FindDoingTasksAsync(inst.InstanceId!.Value, null))[0];
        await engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "zhangsan", new FlowData());
        var approval = (await repo.FindDoingTasksAsync(inst.InstanceId!.Value, null))[0];
        Assert.Equal("approval", approval.TaskName); // 前置：第二档要真推进到 approval 才不空转
        await engine.ExecuteProcessTaskAsync(approval.TaskId!.Value, "leader", new FlowData());

        Assert.Contains("A:apply", sink);
        Assert.Contains("B:approval", sink);
        Assert.DoesNotContain("B:apply", sink);
        Assert.DoesNotContain("A:approval", sink);
    }
}

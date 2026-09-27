using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/128 · 出口 <c>ext.isFirstTaskNode</c> 必须「行上值优先、缺键才现算」。
///
/// 判据基准逐字取 Java 参考实现：<c>JeeflowFacade.java:313-317</c>（instance detail 的 tasks[]）
/// 与 <c>:722</c>（processTask/detail），规范条文 <c>04-engine-ops.md:169</c> +
/// <c>06-facade.md:541/545</c>。C# 的建单侧本来就合规（<c>LineageColumnsTests</c> 钉着），
/// 违约的只有出口：两处都把行上值无条件盖掉。
///
/// 三格合起来才咬得住：只测"建单 true ⇒ 出口 true"的话，覆写实现同样能过
/// （覆写出来的值恰好也等于 true）——那正是本缺陷逃过 207 条既有测试的原因。
/// </summary>
public class FirstTaskNodeExitTests
{
    private const string ChainFlow = """
        {"name": "chain128", "displayName": "Chain128", "type": "approval",
         "nodes": [
           {"id": "start", "type": "snaker:start", "text": {"value": "Start"}},
           {"id": "apply", "type": "snaker:task", "text": {"value": "Apply"},
            "properties": {"assignee": "applicant"}},
           {"id": "t1", "type": "snaker:task", "text": {"value": "T1"},
            "properties": {"assignee": "leader"}},
           {"id": "end", "type": "snaker:end", "text": {"value": "End"}}
         ],
         "edges": [
           {"id": "e1", "sourceNodeId": "start", "targetNodeId": "apply"},
           {"id": "e2", "sourceNodeId": "apply", "targetNodeId": "t1"},
           {"id": "e3", "sourceNodeId": "t1", "targetNodeId": "end"}
         ]}
        """;

    private sealed record World(JeeflowFacade Facade, MemoryRepository Repo, long InstId, long ApplyTaskId);

    /// <summary>出口值读成布尔：布尔直取；字符串走大小写不敏感 "true"；其它（含缺失）为 false。</summary>
    private static bool Flag(object? v) => v switch
    {
        bool b => b,
        null => false,
        _ => string.Equals(Convert.ToString(v), "true", StringComparison.OrdinalIgnoreCase),
    };

    private static async Task<World> StartAsync()
    {
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "chain128", ChainFlow);
        var inst = await engine.StartProcessInstanceByIdAsync(did, "applicant", new FlowData());
        var iid = inst.InstanceId!.Value;
        // apply 是首任务节点、当前 DOING ⇒ 现算值本该是 true，行上值也本该被建单写成 true
        var apply = await TestInfra.FindDoingForAsync(repo, iid, "applicant");
        return new World(new JeeflowFacade(ctx), repo, iid, apply.TaskId!.Value);
    }

    private static async Task<Dictionary<string, object?>> InstanceRowExt(World w, long taskId)
    {
        var resp = await w.Facade.FlowAsync("processInstance/detail", new FlowData { ["id"] = w.InstId });
        Assert.Equal(0, Convert.ToInt32(resp["code"]));
        var data = (Dictionary<string, object?>)resp["data"]!;
        foreach (var o in (List<object?>)data["tasks"]!)
        {
            var row = (Dictionary<string, object?>)o!;
            if (Convert.ToInt64(row["id"]) == taskId)
                return (Dictionary<string, object?>)row["ext"]!;
        }
        throw new InvalidOperationException($"detail.tasks 里没找到任务 {taskId}");
    }

    private static async Task<Dictionary<string, object?>> TaskDetailExt(World w)
    {
        var resp = await w.Facade.FlowAsync("processTask/detail",
            new FlowData { ["id"] = w.ApplyTaskId, ["operator"] = "applicant" });
        Assert.Equal(0, Convert.ToInt32(resp["code"]));
        return (Dictionary<string, object?>)((Dictionary<string, object?>)resp["data"]!)["ext"]!;
    }

    private static async Task<ProcessTask> RowAsync(World w)
    {
        var tasks = await w.Repo.FindDoingTasksAsync(w.InstId, null);
        return tasks.First(t => t.TaskId == w.ApplyTaskId);
    }

    /// <summary>正向：建单写的 true 原样出口（这一格旧实现也能过，只当回归用）。</summary>
    [Fact]
    public async Task RowTrue_ExportsTrue()
    {
        var w = await StartAsync();
        var row = await RowAsync(w);
        Assert.True(Flag(row.Variables[FlowConst.IsFirstTaskNode]), "前置：建单把首任务行的键写成 true");

        Assert.True(Flag((await InstanceRowExt(w, w.ApplyTaskId))[FlowConst.IsFirstTaskNode]),
            "instance detail 的 tasks[] 行应原样出 true");
        Assert.True(Flag((await TaskDetailExt(w))[FlowConst.IsFirstTaskNode]),
            "processTask/detail 应原样出 true");
    }

    /// <summary>
    /// 负向（本案真缺陷）：行上被改成 false，而该节点恰是首任务节点且 DOING。
    /// 覆写实现会按拓扑现算成 true ⇒ 与其余七栈给出相反答案，前端"重新提交"开关错。
    /// </summary>
    [Fact]
    public async Task RowFalseOnFirstDoingNode_StillExportsFalse()
    {
        var w = await StartAsync();
        var row = await RowAsync(w);
        row.Variables[FlowConst.IsFirstTaskNode] = false;
        await w.Repo.UpdateTaskAsync(row);

        Assert.False(Flag((await InstanceRowExt(w, w.ApplyTaskId))[FlowConst.IsFirstTaskNode]),
            "instance detail 的 tasks[] 行：行上 false 必须原样出口，不许按拓扑翻回 true");
        Assert.False(Flag((await TaskDetailExt(w))[FlowConst.IsFirstTaskNode]),
            "processTask/detail：同口径（原先这里先置 false 再无条件现算，两道一起盖死行上值）");
    }

    /// <summary>缺键（存量行）才现算：删掉行上键 ⇒ 首任务 + DOING ⇒ 出口 true。</summary>
    [Fact]
    public async Task MissingRowKey_FallsBackToComputedTrue()
    {
        var w = await StartAsync();
        var row = await RowAsync(w);
        row.Variables.Remove(FlowConst.IsFirstTaskNode);
        await w.Repo.UpdateTaskAsync(row);

        Assert.True(Flag((await InstanceRowExt(w, w.ApplyTaskId))[FlowConst.IsFirstTaskNode]),
            "缺键时必须回退按拓扑现算（04-engine-ops.md:169 的存量行兜底）");
        Assert.True(Flag((await TaskDetailExt(w))[FlowConst.IsFirstTaskNode]),
            "processTask/detail 同样要回退现算");
    }

    /// <summary>
    /// 脏值口径：行上是 "1" 之类的非布尔值 ⇒ 与 Java <c>Boolean.parseBoolean</c> 同语义读作 false，
    /// 绝不能"非空即真"（那等于把行上的假翻成真，与本案要修的覆写同害）。
    /// </summary>
    [Fact]
    public async Task DirtyRowValue_DoesNotBecomeTrue()
    {
        var w = await StartAsync();
        var row = await RowAsync(w);
        row.Variables[FlowConst.IsFirstTaskNode] = "1";
        await w.Repo.UpdateTaskAsync(row);

        Assert.False(Flag((await InstanceRowExt(w, w.ApplyTaskId))[FlowConst.IsFirstTaskNode]),
            "脏值 '1' 不得被读成 true（Java Boolean.parseBoolean 同语义：只有字面 'true' 才是真）");
        Assert.False(Flag((await TaskDetailExt(w))[FlowConst.IsFirstTaskNode]),
            "processTask/detail 同口径");
    }
}

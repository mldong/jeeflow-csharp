namespace Mldong.Jeeflow.Tests;

using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;

/// <summary>
/// issues/241：highLight 重放腿在决策节点选边时，表达式结果与任务行佐证相矛盾要以任务行为准——
/// 直返单重提后实例变量已改，按当前值重放会把没走过的分支算进历史。
///
/// 夹具：start → apply → fork →(taskA / taskB)→ join → decision1{f_amount>1000 → taskHigh(直返) |
/// f_amount<=1000 → taskLow} → end。走单：6666 走 taskHigh → 退回发起人 → 改为 666 重提 →
/// 新待办仍落 taskHigh。高亮必须只点亮实际走过的 e7 侧，不得把 taskLow/e8/e10 算进历史。
/// 决策节点的入边来源刻意放成 join（网关不产任务行），免受「最老前置任务行」快照偶然救活。
/// </summary>
public class HighLightReapply241Tests
{
    private const string Flow = """
    {
      "name": "i241-return-highlight-join", "displayName": "直返重提高亮（汇合后分支）", "type": "approval",
      "nodes": [
        {"id": "start", "type": "snaker:start", "text": {"value": "开始"}},
        {"id": "apply", "type": "snaker:task", "text": {"value": "发起申请"},
         "properties": {"assignee": "applicant", "taskType": 0, "performType": 0}},
        {"id": "fork1", "type": "snaker:fork", "text": {"value": "分叉"}, "properties": {}},
        {"id": "taskA", "type": "snaker:task", "text": {"value": "分支A"},
         "properties": {"assignee": "userA", "taskType": 0, "performType": 0}},
        {"id": "taskB", "type": "snaker:task", "text": {"value": "分支B"},
         "properties": {"assignee": "userB", "taskType": 0, "performType": 0}},
        {"id": "join1", "type": "snaker:join", "text": {"value": "汇合"}, "properties": {}},
        {"id": "decision1", "type": "snaker:decision", "text": {"value": "金额分支"},
         "properties": {"expr": "f_amount > 1000"}},
        {"id": "taskHigh", "type": "snaker:task", "text": {"value": "高额审批"},
         "properties": {"assignee": "director", "taskType": 0, "performType": 0, "reapplyMode": "RETURN_TO_SOURCE"}},
        {"id": "taskLow", "type": "snaker:task", "text": {"value": "普通审批"},
         "properties": {"assignee": "manager", "taskType": 0, "performType": 0}},
        {"id": "end", "type": "snaker:end", "text": {"value": "结束"}}
      ],
      "edges": [
        {"id": "e0", "sourceNodeId": "start", "targetNodeId": "apply", "properties": {}},
        {"id": "e1", "sourceNodeId": "apply", "targetNodeId": "fork1", "properties": {}},
        {"id": "e2", "sourceNodeId": "fork1", "targetNodeId": "taskA", "properties": {}},
        {"id": "e3", "sourceNodeId": "fork1", "targetNodeId": "taskB", "properties": {}},
        {"id": "e4", "sourceNodeId": "taskA", "targetNodeId": "join1", "properties": {}},
        {"id": "e5", "sourceNodeId": "taskB", "targetNodeId": "join1", "properties": {}},
        {"id": "e6", "sourceNodeId": "join1", "targetNodeId": "decision1", "properties": {}},
        {"id": "e7", "sourceNodeId": "decision1", "targetNodeId": "taskHigh", "properties": {"expr": "f_amount > 1000"}},
        {"id": "e8", "sourceNodeId": "decision1", "targetNodeId": "taskLow", "properties": {"expr": "f_amount <= 1000"}},
        {"id": "e9", "sourceNodeId": "taskHigh", "targetNodeId": "end", "properties": {}},
        {"id": "e10", "sourceNodeId": "taskLow", "targetNodeId": "end", "properties": {}}
      ]
    }
    """;

    private static FlowData Args(params (string Key, object? Value)[] pairs)
    {
        var data = new FlowData();
        foreach (var (k, v) in pairs) data[k] = v;
        return data;
    }

    private static List<string> Strings(Dictionary<string, object?> data, string key) =>
        (data[key] as IEnumerable<object?> ?? new List<object?>()).Cast<string>().ToList();

    [Fact]
    public async Task ReapplyDoesNotLightUnvisitedBranch()
    {
        var repo = new MemoryRepository();
        var ext = new MemoryExtRepository(repo, null);
        var ctx = new ServiceContext(repo, ext);
        ctx.Clock = new FixedClock(new DateTime(2026, 11, 1, 9, 0, 0));
        ctx.IdGenerator = new AtomicIdGenerator(1, ctx.Clock);
        ctx.UserProvider = new TestUserProvider();
        TestInfra.RegisterBuiltins(ctx);
        repo.Configure(ctx);
        var engine = new JeeflowEngine(ctx);
        var facade = new JeeflowFacade(ctx);

        var did = await TestInfra.SaveFlowDefineAsync(repo, "i241-return-highlight-join", Flow);
        var start = await facade.FlowAsync("processInstance/startAndExecute", Args(
            ("processDefineId", did), ("operator", "zhangsan"), ("f_amount", 6666)));
        Assert.Equal(0L, Convert.ToInt64(start["code"]));
        var iid = Convert.ToInt64(((Dictionary<string, object?>)start["data"]!)["processInstanceId"]);

        foreach (var actor in new[] { "userA", "userB" })
        {
            var task = await TestInfra.FindDoingForAsync(repo, iid, actor);
            var resp = await facade.FlowAsync("processTask/execute", Args(
                ("processTaskId", task.TaskId), ("operator", actor), ("submitType", 1)));
            Assert.Equal(0L, Convert.ToInt64(resp["code"]));
        }
        var high = await TestInfra.FindDoingForAsync(repo, iid, "director");
        Assert.Equal("taskHigh", high.TaskName);
        var ret = await facade.FlowAsync("processTask/execute", Args(
            ("processTaskId", high.TaskId), ("operator", "director"), ("submitType", 6)));
        Assert.Equal(0L, Convert.ToInt64(ret["code"]));

        var apply = await TestInfra.FindDoingForAsync(repo, iid, "zhangsan");
        Assert.Equal("apply", apply.TaskName);
        var reapply = await facade.FlowAsync("processTask/execute", Args(
            ("processTaskId", apply.TaskId), ("operator", "zhangsan"), ("submitType", 5), ("f_amount", 666)));
        Assert.Equal(0L, Convert.ToInt64(reapply["code"]));
        Assert.Equal("taskHigh", (await TestInfra.FindDoingForAsync(repo, iid, "director")).TaskName);

        var respHl = await facade.FlowAsync("processInstance/highLight", Args(("id", iid)));
        Assert.Equal(0L, Convert.ToInt64(respHl["code"]));
        var data = (Dictionary<string, object?>)respHl["data"]!;
        var edges = Strings(data, "historyEdgeNames");
        var nodes = Strings(data, "historyNodeNames");
        Assert.Contains("e7", edges);
        Assert.DoesNotContain("e8", edges);
        Assert.DoesNotContain("e10", edges);
        Assert.DoesNotContain("taskLow", nodes);
        Assert.DoesNotContain("end", nodes);
    }
}

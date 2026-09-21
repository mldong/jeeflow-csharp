using Xunit;
using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/121 P1 建单不变量：每次建任务必写 ParentTaskId 与行级 isFirstTaskNode。
/// 夹具是 apply → t1 → t2 三级链——两步流里「上一节点」与「首任务节点」同格，
/// 断言恒真、抓不到缺陷，所以必须 ≥3 个任务节点。
/// </summary>
public class LineageColumnsTests
{
    private const string ChainFlow = """
        {"name": "chain121", "displayName": "Chain121", "type": "approval",
         "nodes": [
           {"id": "start", "type": "snaker:start", "text": {"value": "Start"}},
           {"id": "apply", "type": "snaker:task", "text": {"value": "Apply"},
            "properties": {"assignee": "applicant"}},
           {"id": "t1", "type": "snaker:task", "text": {"value": "T1"},
            "properties": {"assignee": "leader"}},
           {"id": "t2", "type": "snaker:task", "text": {"value": "T2"},
            "properties": {"assignee": "manager"}},
           {"id": "end", "type": "snaker:end", "text": {"value": "End"}}
         ],
         "edges": [
           {"id": "e1", "sourceNodeId": "start", "targetNodeId": "apply"},
           {"id": "e2", "sourceNodeId": "apply", "targetNodeId": "t1"},
           {"id": "e3", "sourceNodeId": "t1", "targetNodeId": "t2"},
           {"id": "e4", "sourceNodeId": "t2", "targetNodeId": "end"}
         ]}
        """;

    private static FlowData Agree() =>
        FlowData.Of(new Dictionary<string, object?> { [FlowConst.SubmitType] = 1 });

    [Fact]
    public async Task Create_WritesLineageColumns_AndFlagSurvivesOnHistoryRow()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "chain121", ChainFlow);
        var inst = await engine.StartProcessInstanceByIdAsync(did, "applicant", new FlowData());
        var iid = inst.InstanceId!.Value;

        var apply = (await repo.FindDoingTasksAsync(iid, null)).Single();
        Assert.Equal("apply", apply.TaskName);
        // 发起 execution 没有当前任务 ⇒ parent 落 0；apply 是 start 直接后继 ⇒ true
        Assert.Equal(0L, apply.ParentTaskId);
        Assert.Equal(true, apply.Variables[FlowConst.IsFirstTaskNode]);
        await engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", Agree());

        long prevId = apply.TaskId!.Value;
        foreach (var (name, who) in new[] { ("t1", "leader"), ("t2", "manager") })
        {
            var task = (await repo.FindDoingTasksAsync(iid, null)).Single();
            Assert.Equal(name, task.TaskName);
            Assert.Equal(prevId, task.ParentTaskId);   // 链式血缘：parent＝刚办结那条
            Assert.Equal(false, task.Variables[FlowConst.IsFirstTaskNode]);
            prevId = task.TaskId!.Value;
            await engine.ExecuteProcessTaskAsync(task.TaskId!.Value, who, Agree());
        }

        // 本案真正要的那格：血缘版回退读的是已办结的历史行，标记必须随行存活
        // （门面出口现算版带「仅进行中」判定，历史行上恒 false ⇒ 首节点回退会派错人）
        var his = await repo.FindTaskByIdAsync(apply.TaskId!.Value);
        Assert.NotNull(his);
        Assert.NotEqual((int)WfTaskState.Doing, his!.TaskState);
        Assert.Equal(true, his.Variables[FlowConst.IsFirstTaskNode]);
        Assert.Equal(0L, his.ParentTaskId);
    }
}

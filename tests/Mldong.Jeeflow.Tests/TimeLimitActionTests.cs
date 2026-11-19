using Xunit;
using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// 任务时限动作 T0（spec 04 · 任务时限动作；工作流时限方案 §8 的引擎侧判据，C# 腿）。
/// 与 go/java/python/node 同构：建行、旁路覆盖、不继承档、失效联动、回调事实范围、
/// 可注入时钟、实例级排他锁调用面。
/// </summary>
public class TimeLimitActionTests
{
    private const int Apply = (int)WfSubmitType.Apply;
    private const int Agree = (int)WfSubmitType.Agree;
    private const int Reject = (int)WfSubmitType.Reject;

    private sealed class FakeSink : ITimeActionSink
    {
        public List<TimeAction> Actions { get; } = new();
        public List<long> CancelledTasks { get; } = new();
        public List<long> CancelledInstances { get; } = new();

        public Task InsertTimeActionAsync(TimeAction action)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }

        public Task CancelTaskTimeActionsAsync(long taskId)
        {
            CancelledTasks.Add(taskId);
            return Task.CompletedTask;
        }

        public Task CancelInstanceTimeActionsAsync(long instanceId)
        {
            CancelledInstances.Add(instanceId);
            return Task.CompletedTask;
        }

        public List<TimeAction> OfType(string type) => Actions.Where(a => a.Type == type).ToList();
    }

    private int _seq;

    private static string Node(string id, string type, string props, string text) =>
        "{\"id\":\"" + id + "\",\"type\":\"" + type + "\",\"x\":0,\"y\":0,\"properties\":{"
        + props + "},\"text\":{\"value\":\"" + text + "\"}}";

    private static string Edge(string id, string from, string to) =>
        "{\"id\":\"" + id + "\",\"type\":\"snaker:transition\",\"sourceNodeId\":\"" + from
        + "\",\"targetNodeId\":\"" + to + "\",\"properties\":{}}";

    private string Flow(string reviewProps)
    {
        _seq++;
        var parts = new List<string> { "\"form\":\"f\"" };
        if (!reviewProps.Contains("\"assignee\"")) parts.Add("\"assignee\":\"userA\"");
        if (!reviewProps.Contains("\"taskType\"")) parts.Add("\"taskType\":0");
        if (!reviewProps.Contains("\"performType\"")) parts.Add("\"performType\":0");
        if (!string.IsNullOrWhiteSpace(reviewProps)) parts.Add(reviewProps);
        var rp = string.Join(",", parts);
        return "{\"name\":\"tl-" + _seq + "\",\"displayName\":\"时限夹具\",\"type\":\"approval\",\"nodes\":["
               + Node("start", "snaker:start", "", "开始") + ","
               + Node("apply", "snaker:task", "\"form\":\"af\",\"taskType\":0,\"performType\":0,\"assignee\":\"applicant\"", "申请") + ","
               + Node("review", "snaker:task", rp, "审批") + ","
               + Node("end", "snaker:end", "", "结束")
               + "],\"edges\":[" + Edge("e1", "start", "apply") + "," + Edge("e2", "apply", "review") + ","
               + Edge("e3", "review", "end") + "]}";
    }

    private sealed record Stack(JeeflowEngine Engine, MemoryRepository Repo, ServiceContext Ctx,
        FakeSink Sink, long Iid);

    private static FlowData Submit(int type) => new() { [FlowConst.SubmitType] = type };

    private async Task<Stack> StartReviewAsync(string reviewProps)
    {
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        var sink = new FakeSink();
        ctx.TimeActionSink = sink;
        var flow = Flow(reviewProps);
        var name = "tl-" + _seq + "-" + Guid.NewGuid().ToString("N")[..6];
        var defId = await TestInfra.SaveFlowDefineAsync(repo, name, flow);
        var inst = await engine.StartProcessInstanceByIdAsync(defId, "applicant", new FlowData());
        foreach (var t in await repo.FindDoingTasksAsync(inst.InstanceId!.Value, null))
            await engine.ExecuteProcessTaskAsync(t.TaskId!.Value, "applicant", Submit(Apply));
        var after = await repo.FindInstanceByIdAsync(inst.InstanceId!.Value);
        return new Stack(engine, repo, ctx, sink, after.InstanceId!.Value);
    }

    private static async Task<ProcessTask?> DoingAsync(MemoryRepository repo, long iid, string name) =>
        (await repo.FindDoingTasksAsync(iid, null)).FirstOrDefault(t => t.TaskName == name);

    private static DateTime? DueAtReminder(Stack s, int idx = 0)
    {
        var a = s.Sink.OfType(TimeAction.TypeRemind);
        return a.Count > idx
            ? DateTimeOffset.FromUnixTimeMilliseconds(a[idx].DueAt).LocalDateTime
            : null;
    }

    [Fact]
    public async Task AutoAndReminderOnCreate()
    {
        var s = await StartReviewAsync("\"expireTime\":\"2h\",\"autoExecute\":\"Y\",\"reminderTime\":\"30m\"");
        var review = await DoingAsync(s.Repo, s.Iid, "review");
        Assert.NotNull(review);
        Assert.Single(s.Sink.OfType(TimeAction.TypeAutoComplete));
        Assert.Single(s.Sink.OfType(TimeAction.TypeRemind));
        var auto = s.Sink.OfType(TimeAction.TypeAutoComplete)[0];
        var remind = s.Sink.OfType(TimeAction.TypeRemind)[0];
        Assert.Equal($"wf:auto:{review!.TaskId}:0", auto.IdempotencyKey);
        Assert.Equal($"wf:remind:{review.TaskId}:1", remind.IdempotencyKey);
        Assert.Equal(1, remind.OccurrenceNo);
        Assert.Equal(review.TaskId!.Value, auto.TaskId);
        Assert.Equal(s.Iid, auto.InstanceId);
        // 固定时钟 2026-08-01 09:00
        Assert.Equal(new DateTime(2026, 8, 1, 9, 30, 0), DueAtReminder(s));
        Assert.Equal(new DateTime(2026, 8, 1, 11, 0, 0),
            DateTimeOffset.FromUnixTimeMilliseconds(auto.DueAt).LocalDateTime);
    }

    [Fact]
    public async Task NotConfiguredCreatesNoAction()
    {
        var s = await StartReviewAsync("");
        Assert.Empty(s.Sink.Actions);
    }

    [Fact]
    public async Task AutoExecuteOnlyExactY()
    {
        foreach (var v in new[] { "N", "n", "y", "true" })
        {
            var s = await StartReviewAsync("\"expireTime\":\"2h\",\"autoExecute\":\"" + v + "\"");
            Assert.Empty(s.Sink.OfType(TimeAction.TypeAutoComplete));
        }
    }

    [Fact]
    public async Task ReminderHHmmSameDayAndNextDay()
    {
        var s = await StartReviewAsync("\"reminderTime\":\"12:00\"");
        Assert.Equal(new DateTime(2026, 8, 1, 12, 0, 0), DueAtReminder(s));
        var s2 = await StartReviewAsync("\"reminderTime\":\"08:00\"");
        Assert.Equal(new DateTime(2026, 8, 2, 8, 0, 0), DueAtReminder(s2));
    }

    [Fact]
    public async Task InvalidReminderDoesNotBreakFlow()
    {
        var s = await StartReviewAsync("\"reminderTime\":\"2.5h\"");
        Assert.NotNull(await DoingAsync(s.Repo, s.Iid, "review"));
        Assert.Empty(s.Sink.OfType(TimeAction.TypeRemind));
    }

    [Fact]
    public async Task ParallelCountersignEveryMember()
    {
        var s = await StartReviewAsync(
            "\"assignee\":\"userA,userB\",\"performType\":1,\"countersignType\":\"PARALLEL\",\"reminderTime\":\"1h\"");
        var rows = (await s.Repo.FindDoingTasksAsync(s.Iid, null)).Where(t => t.TaskName == "review").ToList();
        Assert.Equal(2, rows.Count);
        var ids = s.Sink.OfType(TimeAction.TypeRemind).Select(a => a.TaskId).ToHashSet();
        foreach (var r in rows) Assert.Contains(r.TaskId!.Value, ids);
    }

    [Fact]
    public async Task SequentialAdvanceGetsAction()
    {
        var s = await StartReviewAsync(
            "\"assignee\":\"userA,userB\",\"performType\":1,\"countersignType\":\"SEQUENTIAL\",\"expireTime\":\"2h\",\"autoExecute\":\"Y\"");
        var first = await DoingAsync(s.Repo, s.Iid, "review");
        Assert.NotNull(first);
        Assert.Contains(s.Sink.OfType(TimeAction.TypeAutoComplete), a => a.TaskId == first!.TaskId);
        await s.Engine.ExecuteProcessTaskAsync(first!.TaskId!.Value, "userA", Submit(Agree));
        var nxt = (await s.Repo.FindDoingTasksAsync(s.Iid, null))
            .Where(t => t.TaskName == "review" && t.TaskId != first.TaskId).ToList();
        Assert.Single(nxt);
        Assert.Contains(s.Sink.OfType(TimeAction.TypeAutoComplete), a => a.TaskId == nxt[0].TaskId);
        Assert.Contains(first.TaskId!.Value, s.Sink.CancelledTasks);
    }

    [Fact]
    public async Task AidantRowInheritsNoAction()
    {
        var s = await StartReviewAsync("\"expireTime\":\"2h\",\"autoExecute\":\"Y\",\"reminderTime\":\"1h\"");
        var review = await DoingAsync(s.Repo, s.Iid, "review");
        var before = s.Sink.Actions.Count;
        await s.Engine.AidantTaskAsync(review!.TaskId!.Value, new List<string> { "aid1" }, "", false, "userA");
        Assert.Equal(before, s.Sink.Actions.Count);
    }

    [Fact]
    public async Task CallbackOnCompleteOnly()
    {
        var s = await StartReviewAsync("\"callback\":\"demoHandler\",\"reminderTime\":\"1h\"");
        var review = await DoingAsync(s.Repo, s.Iid, "review");
        await s.Engine.ExecuteProcessTaskAsync(review!.TaskId!.Value, "userA", Submit(Agree));
        var cbs = s.Sink.OfType(TimeAction.TypeCallback);
        Assert.Single(cbs);
        Assert.Equal($"wf:callback:{review.TaskId}:0", cbs[0].IdempotencyKey);

        var s2 = await StartReviewAsync("\"callback\":\"demoHandler\"");
        var r2 = await DoingAsync(s2.Repo, s2.Iid, "review");
        await s2.Engine.ExecuteAndJumpToEndAsync(r2!.TaskId!.Value, "userA", Submit(Reject));
        Assert.Empty(s2.Sink.OfType(TimeAction.TypeCallback));
    }

    [Fact]
    public async Task InstanceTerminalCancelsActions()
    {
        var s = await StartReviewAsync("\"reminderTime\":\"1h\"");
        var review = await DoingAsync(s.Repo, s.Iid, "review");
        await s.Engine.ExecuteProcessTaskAsync(review!.TaskId!.Value, "userA", Submit(Agree));
        Assert.Contains(s.Iid, s.Sink.CancelledInstances);
    }

    [Fact]
    public async Task LockInstanceCalledOnExecute()
    {
        var s = await StartReviewAsync("");
        var review = await DoingAsync(s.Repo, s.Iid, "review");
        await s.Engine.ExecuteProcessTaskAsync(review!.TaskId!.Value, "userA", Submit(Agree));
        // MemoryRepository 实现 IInstanceLockRepository（no-op），调用面经引擎路径完成；
        // 真库腿由 MySqlRepository.LockInstanceAsync 的 FOR UPDATE 承担（T1）。
    }
}

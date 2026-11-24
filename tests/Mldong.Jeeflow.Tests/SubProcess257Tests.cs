using Mldong.Jeeflow.Core;
using Xunit;

namespace Mldong.Jeeflow.Tests;

/// <summary>issues/257 子流程定向 T0：定义定位、自办、回父、守卫、事件与零写入。</summary>
public class SubProcess257Tests
{
    [Theory]
    [InlineData("subprocess")]
    [InlineData("wfSubProcess")]
    [InlineData("subProcess")]
    public void ParserKeepsAllThreeAliasesAndProcessNamePriority(string alias)
    {
        var (_, _, ctx) = TestInfra.NewEngineWithCtx();
        var json = $$"""
            {"name":"parser257","nodes":[
              {"id":"sub257","type":"snaker:{{alias}}","properties":{
                "processName":"target257","name":"wrong257","form":"__wrong__","version":1} },
              {"id":"end257","type":"snaker:end","properties":{} }
             ],"edges":[{"id":"e257","sourceNodeId":"sub257","targetNodeId":"end257","properties":{} }
             ]}
            """;
        var node = Assert.IsType<SubProcessModel>(ModelParser.Parse(
            System.Text.Encoding.UTF8.GetBytes(json), ctx).GetNode("sub257"));
        Assert.Equal("target257", node.ProcessName);
        Assert.Equal("__wrong__", node.Form);
        Assert.Equal(1, node.Version);
    }

    [Theory]
    [InlineData("processName", "\"processName\":\"target257\",")]
    [InlineData("name", "\"name\":\"target257\",")]
    [InlineData("id", "")]
    public void ParserFallsBackProcessNameThenNameThenId(string key, string properties)
    {
        var (_, _, ctx) = TestInfra.NewEngineWithCtx();
        var id = key == "id" ? "target257" : "sub257";
        var json = $$"""
            {"name":"fallback257","nodes":[
              {"id":"{{id}}","type":"snaker:subProcess","properties":{{{properties}}"form":"__wrong__"} },
              {"id":"end257","type":"snaker:end","properties":{} }
             ],"edges":[{"id":"e257","sourceNodeId":"{{id}}","targetNodeId":"end257","properties":{} }
             ]}
            """;
        var node = Assert.IsType<SubProcessModel>(ModelParser.Parse(
            System.Text.Encoding.UTF8.GetBytes(json), ctx).GetNode(id));
        Assert.Equal("target257", node.ProcessName);
    }

    [Fact]
    public async Task DefinitionLookupExplicitLatestTieAndNoStateFilter()
    {
        var (_, repo) = TestInfra.NewEngine();
        var v1 = await AddDefineAsync(repo, "versioned257", 1, Chain("versioned257", "reviewV1"), 0);
        var v2LowId = await AddDefineAsync(repo, "versioned257", 2, Chain("versioned257", "reviewV2"), 0);
        var v2HighId = new ProcessDefine
        {
            Id = v2LowId + 10000,
            Name = "versioned257",
            DisplayName = "versioned257",
            Type = "approval",
            State = 0,
            Version = 2,
            Content = System.Text.Encoding.UTF8.GetBytes(Chain("versioned257", "reviewV2High")),
        };
        await repo.SaveDefineAsync(v2HighId);

        Assert.Equal(v2HighId.Id, (await repo.FindDefineByNameAndVersionAsync("versioned257", null))?.Id);
        Assert.Equal(v1, (await repo.FindDefineByNameAndVersionAsync("versioned257", 1))?.Id);
        Assert.Null(await repo.FindDefineByNameAndVersionAsync("versioned257", 99));
    }

    [Fact]
    public async Task ParentChildClosesLoopAndChildApplyIsAutoExecutedOnce()
    {
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        var events = new Recorder();
        ctx.RegisterEventListener(events);
        var childId = await AddDefineAsync(repo, "child257", 1, Chain("child257", "childReview"));
        var parentId = await AddDefineAsync(repo, "parent257", 1,
            Chain("parent257", "parentAfter", sub: "sub257"));

        var subModel = ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(
            Chain("parent257", "parentAfter", sub: "sub257")), engine.Context).GetNode("sub257");
        Assert.NotNull(subModel);
        var subNode = Assert.IsType<SubProcessModel>(subModel);
        Assert.Equal("child257", subNode.ProcessName);
        Assert.Null(subNode.Version);
        Assert.NotNull(subNode.Outputs[0].Target);
        Assert.Equal("parentAfter", subNode.Outputs[0].Target.Name);
        Assert.Equal(childId, (await repo.FindDefineByNameAndVersionAsync(subNode.ProcessName, subNode.Version))?.Id);
        Assert.Equal(childId, (await repo.FindDefineByIdAsync(childId))?.Id);
        Assert.Equal("child257", (await repo.FindDefineByIdAsync(childId))?.Name);
        Assert.Equal("child257", (await repo.PageDefinesAsync(new PageQuery(1, 100).Add("t.name", "EQ", "child257"))).Rows.Single().Name);

        var parent = await StartWithApplyAsync(engine, parentId, new FlowData { ["f_parent"] = "kept" });
        Assert.Empty(await repo.FindDoingTasksAsync(parent.InstanceId!.Value, null));
        Assert.Equal("childReview", (await OnlyDoingAsync(repo,
            (await OnlyChildAsync(repo, parent.InstanceId.Value)).InstanceId!.Value)).TaskName);
        var child = await OnlyChildAsync(repo, parent.InstanceId!.Value);
        Assert.Equal(parent.InstanceId, child.ParentId);
        Assert.Equal("sub257", child.ParentNodeName);
        Assert.Equal(childId, child.DefineId);
        Assert.Equal("applicant", child.Operator);
        Assert.Equal((int)WfInstanceState.Doing, child.State);
        Assert.Equal("kept", child.Variables.GetStr("f_parent"));

        var doneApply = Assert.Single(await repo.FindDoneTasksAsync(child.InstanceId!.Value, new[] { "apply" }));
        Assert.Equal((int)WfTaskState.Finished, doneApply.TaskState);
        Assert.Equal(FlowConst.AutoId, doneApply.ActorId);
        Assert.Contains("applicant", doneApply.ActorIds);
        var review = Assert.Single(await repo.FindDoingTasksAsync(child.InstanceId.Value, null));
        Assert.Equal("childReview", review.TaskName);
        Assert.Equal(1, events.Count(ProcessEventType.ProcessTaskStart, review.TaskId!.Value));
        Assert.DoesNotContain((await repo.FindDoingTasksAsync(parent.InstanceId.Value, null)),
            t => t.ProcessInstanceId == child.InstanceId);

        await engine.ExecuteProcessTaskAsync(review.TaskId!.Value, "leader",
            new FlowData { [FlowConst.SubmitType] = (int)WfSubmitType.Agree });
        Assert.Equal((int)WfInstanceState.Finished,
            (await repo.FindInstanceByIdAsync(child.InstanceId))?.State);
        var parentAfter = Assert.Single(await repo.FindDoingTasksAsync(parent.InstanceId.Value, null));
        Assert.Equal("parentAfter", parentAfter.TaskName);
        Assert.Equal((int)WfInstanceState.Doing,
            (await repo.FindInstanceByIdAsync(parent.InstanceId))?.State);

        await engine.ExecuteProcessTaskAsync(parentAfter.TaskId!.Value, "manager",
            new FlowData { [FlowConst.SubmitType] = (int)WfSubmitType.Agree });
        Assert.Equal((int)WfInstanceState.Finished,
            (await repo.FindInstanceByIdAsync(parent.InstanceId))?.State);
        Assert.Equal(1, events.Count(ProcessEventType.ProcessInstanceStart, child.InstanceId.Value));
        Assert.Equal(1, events.Count(ProcessEventType.ProcessInstanceEnd, child.InstanceId.Value));
        Assert.Equal(1, events.Count(ProcessEventType.ProcessInstanceStart, parent.InstanceId.Value));
        Assert.Equal(1, events.Count(ProcessEventType.ProcessInstanceEnd, parent.InstanceId.Value));
    }

    [Fact]
    public async Task ChildArgsAreCopiedAndControlsStrippedAndPrivilegedOperatorFallsBackToParent()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var childId = await AddDefineAsync(repo, "childArgs257", 1, Chain("childArgs257", "childReview"));
        var parentId = await AddDefineAsync(repo, "parentArgs257", 1,
            Chain("parentArgs257", "end", sub: "sub257", targetName: "childArgs257"));
        var parent = await engine.StartProcessInstanceByIdAsync(parentId, "applicant", new FlowData { ["f_parent"] = "parent" });
        var before = await OnlyDoingAsync(repo, parent.InstanceId!.Value);
        Assert.Equal("apply", before.TaskName);

        await engine.ExecuteProcessTaskAsync(before.TaskId!.Value, FlowConst.AdminId, new FlowData
        {
            [FlowConst.SubmitType] = (int)WfSubmitType.Apply,
            [FlowConst.TaskFormDataPrefix + "pollution"] = "no",
            ["tf_nextNodeOperator"] = "wrong",
            ["f_nextNodeOperator"] = "wrong",
            [ReapplySupport.VarBranchCtx] = "wrong",
            [ReapplySupport.VarReapplyCtx] = "wrong",
            ["f_business"] = "child",
        });

        var child = await OnlyChildAsync(repo, parent.InstanceId.Value);
        Assert.Equal("applicant", child.Operator);
        Assert.Equal("child", child.Variables.GetStr("f_business"));
        Assert.False(child.Variables.ContainsKey("tf_pollution"));
        Assert.False(child.Variables.ContainsKey(FlowConst.NextNodeOperator));
        Assert.False(child.Variables.ContainsKey(FlowConst.ProcessStartNextNodeOperator));
        Assert.False(child.Variables.ContainsKey(ReapplySupport.VarBranchCtx));
        Assert.False(child.Variables.ContainsKey(ReapplySupport.VarReapplyCtx));
        Assert.Equal("applicant", (await repo.FindInstanceByIdAsync(parent.InstanceId))?.Operator);
        Assert.Equal("childReview", (await OnlyDoingAsync(repo, child.InstanceId!.Value)).TaskName);
    }

    [Fact]
    public async Task ChildWithoutApplySelfHandlesFirstTask()
    {
        var (engine, repo) = TestInfra.NewEngine();
        await AddDefineAsync(repo, "noApply257", 1, Chain("noApply257", "first257", apply: false));
        var parentId = await AddDefineAsync(repo, "parentNoApply257", 1,
            Chain("parentNoApply257", "second257", sub: "subNoApply257", targetName: "noApply257"));
        var parent = await StartWithApplyAsync(engine, parentId, new FlowData());
        var child = await OnlyChildAsync(repo, parent.InstanceId!.Value);
        Assert.Equal((int)WfInstanceState.Finished, child.State);
        var first = Assert.Single(await repo.FindDoneTasksAsync(child.InstanceId.Value, new[] { "first257" }));
        Assert.Equal(FlowConst.AutoId, first.ActorId);
        Assert.Contains("applicant", first.ActorIds);
        Assert.Equal("second257", (await OnlyDoingAsync(repo, parent.InstanceId.Value)).TaskName);
    }

    [Fact]
    public async Task MissingDefinitionFailsBeforeCurrentTaskOrInstanceChanges()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var parentId = await AddDefineAsync(repo, "missingParent257", 1,
            Chain("missingParent257", "end", sub: "subMissing257", targetName: "missing257"));
        var parent = await engine.StartProcessInstanceByIdAsync(parentId, "applicant", new FlowData());
        var current = await OnlyDoingAsync(repo, parent.InstanceId!.Value);
        Assert.Equal("apply", current.TaskName);
        var instancesBefore = (await repo.PageInstancesAsync(new PageQuery(1, 100))).RecordCount;
        var tasksBefore = (await repo.FindHistoryTasksAsync(parent.InstanceId.Value)).Count;
        var events = new Recorder();
        engine.Context.RegisterEventListener(events);

        var ex = await Assert.ThrowsAsync<JeeflowException>(() =>
            engine.ExecuteProcessTaskAsync(current.TaskId!.Value, "applicant", new FlowData
            {
                [FlowConst.SubmitType] = (int)WfSubmitType.Apply,
            }));
        Assert.Equal(WfErr.NotFoundProcessDefine, ex.Code);
        Assert.Equal("子流程missing257定义不存在", ex.Message);
        Assert.Equal((int)WfTaskState.Doing, (await repo.FindTaskByIdAsync(current.TaskId))?.TaskState);
        Assert.Equal(tasksBefore, (await repo.FindHistoryTasksAsync(parent.InstanceId.Value)).Count);
        Assert.Equal(instancesBefore, (await repo.PageInstancesAsync(new PageQuery(1, 100))).RecordCount);
        Assert.Equal((int)WfInstanceState.Doing, (await repo.FindInstanceByIdAsync(parent.InstanceId))?.State);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task ExplicitVersionAndNullLatestDriveActualChildInstance()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var v1 = await AddDefineAsync(repo, "actualVersion257", 1, Chain("actualVersion257", "reviewV1"));
        var v2 = await AddDefineAsync(repo, "actualVersion257", 2, Chain("actualVersion257", "reviewV2"));
        var latestParent = await AddDefineAsync(repo, "latestParent257", 1,
            Chain("latestParent257", "end", sub: "subLatest257", targetName: "actualVersion257"));
        var latest = await StartWithApplyAsync(engine, latestParent, new FlowData());
        Assert.Equal(v2, (await OnlyChildAsync(repo, latest.InstanceId!.Value)).DefineId);

        var exactParent = await AddDefineAsync(repo, "exactParent257", 1,
            Chain("exactParent257", "end", sub: "subExact257", targetName: "actualVersion257", targetVersion: 1));
        var exact = await StartWithApplyAsync(engine, exactParent, new FlowData());
        Assert.Equal(v1, (await OnlyChildAsync(repo, exact.InstanceId!.Value)).DefineId);
    }

    [Fact]
    public async Task EndRejectsUnfinishedMajorWithoutAdvancing()
    {
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        var events = new Recorder();
        ctx.RegisterEventListener(events);
        var defineId = await AddDefineAsync(repo, "childMajorGuard257", 1, Chain("childMajorGuard257", "childReview"));
        var instance = await engine.StartProcessInstanceByIdAsync(defineId, "applicant", new FlowData());
        var apply = await OnlyDoingAsync(repo, instance.InstanceId!.Value);
        await engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData
        {
            [FlowConst.SubmitType] = (int)WfSubmitType.Apply,
        });
        var review = await OnlyDoingAsync(repo, instance.InstanceId.Value);
        var otherMajor = new ProcessTask
        {
            ProcessInstanceId = instance.InstanceId,
            TaskName = "otherMajor",
            DisplayName = "otherMajor",
            TaskType = WfTaskType.Major,
            PerformType = WfPerformType.Normal,
            TaskState = (int)WfTaskState.Doing,
            ActorIds = new List<string> { "manager" },
        };
        await repo.SaveTaskAsync(otherMajor);
        events.Events.Clear();
        var ex = await Assert.ThrowsAsync<JeeflowException>(() =>
            engine.ExecuteAndJumpToEndAsync(review.TaskId!.Value, "leader", new FlowData
            {
                [FlowConst.SubmitType] = (int)WfSubmitType.Agree,
            }));
        Assert.Equal("存在未完成的主办任务,请确认.", ex.Message);
        Assert.Equal((int)WfInstanceState.Doing, (await repo.FindInstanceByIdAsync(instance.InstanceId))?.State);
        Assert.Equal((int)WfTaskState.Doing, (await repo.FindTaskByIdAsync(otherMajor.TaskId))?.TaskState);
        var completed = Assert.Single(events.Events);
        Assert.Equal(ProcessEventType.TaskComplete, completed.EventType);
        Assert.Equal(review.TaskId, completed.SourceId);
    }

    [Fact]
    public async Task EndAutoCompletesRemainingAidant()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var defineId = await AddDefineAsync(repo, "childAidantOnly257", 1, Chain("childAidantOnly257", "childReview"));
        var instance = await engine.StartProcessInstanceByIdAsync(defineId, "applicant", new FlowData());
        var apply = await OnlyDoingAsync(repo, instance.InstanceId!.Value);
        await engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData
        {
            [FlowConst.SubmitType] = (int)WfSubmitType.Apply,
        });
        var review = await OnlyDoingAsync(repo, instance.InstanceId.Value);
        var aidants = await engine.AidantTaskAsync(review.TaskId!.Value, new List<string> { "manager" }, "assist", false, "leader");
        var aidant = Assert.Single(aidants);
        review.TaskState = (int)WfTaskState.Finished;
        await repo.UpdateTaskAsync(review);
        var execution = new Execution
        {
            Engine = engine,
            Context = engine.Context,
            ProcessModel = ModelParser.Parse((await repo.FindDefineByIdAsync(defineId))!.Content!, engine.Context),
            ProcessInstance = await repo.FindInstanceByIdAsync(instance.InstanceId),
            ProcessInstanceId = instance.InstanceId,
            Operator = "leader",
            Args = new FlowData { [FlowConst.SubmitType] = (int)WfSubmitType.Agree },
        };
        await new EndProcessHandler(new EndModel()).HandleAsync(execution);
        await repo.UpdateInstanceAsync(execution.ProcessInstance!);
        Assert.Equal((int)WfTaskState.Finished, (await repo.FindTaskByIdAsync(aidant.TaskId))?.TaskState);
        Assert.Equal((int)WfInstanceState.Finished, (await repo.FindInstanceByIdAsync(instance.InstanceId))?.State);
    }

    [Fact]
    public async Task ParentContinuationUsesParentVariablesAndEachInstanceEndIsPersistedBeforeEvent()
    {
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        var events = new EndOrderRecorder(repo);
        ctx.RegisterEventListener(events);
        var childId = await AddDefineAsync(repo, "childEnd257", 1, Chain("childEnd257", "childReview"));
        var parentId = await AddDefineAsync(repo, "parentEnd257", 1,
            Chain("parentEnd257", "end", sub: "sub257", targetName: "childEnd257"));
        // 该格专测「子结束 → 父终态落库后播」：直接造已到 sub 节点的父实例，避免先自动起一个真子实例干扰事件读数。
        var parent = await engine.StartProcessInstanceByIdAsync(parentId, "applicant", new FlowData { ["f_owner"] = "parentVar" });
        var parentApply = await OnlyDoingAsync(repo, parent.InstanceId!.Value);
        await engine.ExecuteProcessTaskAsync(parentApply.TaskId!.Value, "applicant", new FlowData
        {
            [FlowConst.SubmitType] = (int)WfSubmitType.Apply,
        });
        var child = await engine.StartProcessInstanceByIdAsync(childId, "applicant",
            new FlowData { ["f_childOnly"] = "childVar" }, parent.InstanceId, "sub257");
        var apply = await OnlyDoingAsync(repo, child.InstanceId!.Value);
        Assert.Equal("apply", apply.TaskName);
        await engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData
        {
            [FlowConst.SubmitType] = (int)WfSubmitType.Apply,
        });
        // 手工启动没有走 startAndExecute：先办 apply，再办 childReview，触发子结束与父级联。
        var review = await OnlyDoingAsync(repo, child.InstanceId.Value);
        events.Events.Clear();

        await engine.ExecuteProcessTaskAsync(review.TaskId!.Value, "leader", new FlowData
        {
            [FlowConst.SubmitType] = (int)WfSubmitType.Agree,
            ["f_childOnly"] = "changedInChild",
        });

        Assert.All(events.Events, e => Assert.Equal((int)WfInstanceState.Finished, e.StateAtEvent));
        Assert.Equal(new[] { child.InstanceId!.Value, parent.InstanceId!.Value },
            events.Events.Select(e => e.InstanceId).ToArray());
        var parentRow = await repo.FindInstanceByIdAsync(parent.InstanceId);
        Assert.Equal("parentVar", parentRow?.Variables.GetStr("f_owner"));
        Assert.False(parentRow!.Variables.ContainsKey("f_childOnly"));
    }

    [Fact]
    public void RollbackCannotCrossSubProcessOrCountersignParent()
    {
        var (_, _, ctx) = TestInfra.NewEngineWithCtx();
        var model = ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes("""
            {"name":"guard257","nodes":[
              {"id":"apply","type":"snaker:task","properties":{"assignee":"applicant","performType":0}},
              {"id":"sub257","type":"snaker:subProcess","properties":{"processName":"child257"}},
              {"id":"after257","type":"snaker:task","properties":{"assignee":"manager","performType":0}},
              {"id":"end257","type":"snaker:end","properties":{}}],
             "edges":[
              {"id":"e1","sourceNodeId":"apply","targetNodeId":"sub257"},
              {"id":"e2","sourceNodeId":"sub257","targetNodeId":"after257"},
              {"id":"e3","sourceNodeId":"after257","targetNodeId":"end257"}]}
            """), ctx);
        Assert.False(FlowUtil.CanRejected(model.GetNode("after257")!, model.GetNode("apply")!));

        var cs = ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes("""
            {"name":"guardCs257","nodes":[
              {"id":"apply","type":"snaker:task","properties":{"assignee":"applicant","performType":0}},
              {"id":"cs","type":"snaker:task","properties":{"assignee":"leader","performType":1}},
              {"id":"after","type":"snaker:task","properties":{"assignee":"manager","performType":0}}],
             "edges":[
              {"id":"e1","sourceNodeId":"apply","targetNodeId":"cs"},
              {"id":"e2","sourceNodeId":"cs","targetNodeId":"after"}]}
            """), ctx);
        Assert.False(FlowUtil.CanRejected(cs.GetNode("after")!, cs.GetNode("cs")!));
        Assert.True(FlowUtil.CanRejected(cs.GetNode("after")!, cs.GetNode("apply")!));
    }

    private static string Chain(string name, string review, string? sub = null,
        string? targetName = null, int? targetVersion = null, bool apply = true)
    {
        var target = targetName ?? name.Replace("parent", "child");
        var first = apply ? "apply" : "start";
        var nodes = new List<string> { Node("start", "snaker:start", "") };
        var edges = new List<string>();
        if (apply)
        {
            nodes.Add(Node("apply", "snaker:task", "\"assignee\":\"applicant\",\"performType\":0"));
            edges.Add(Edge("eApply", "start", "apply"));
        }
        var previous = first;
        if (sub != null)
        {
            var version = targetVersion.HasValue ? $",\"version\":{targetVersion.Value}" : "";
            nodes.Add(Node(sub, "snaker:subProcess",
                $"\"processName\":\"{target}\",\"form\":\"__wrong__\"{version}"));
            edges.Add(Edge("eSub", previous, sub));
            previous = sub;
        }
        nodes.Add(Node(review, "snaker:task",
            review.Contains("child", StringComparison.OrdinalIgnoreCase) || review.StartsWith("first")
                ? "\"assignee\":\"leader\",\"performType\":0"
                : "\"assignee\":\"manager\",\"performType\":0"));
        edges.Add(Edge("eReview", previous, review));
        nodes.Add(Node("end", "snaker:end", ""));
        edges.Add(Edge("eEnd", review, "end"));
        return $$"""
            {"name":"{{name}}","displayName":"{{name}}","type":"approval",
             "nodes":[{{string.Join(',', nodes)}}],"edges":[{{string.Join(',', edges)}}]
            }
            """;
    }

    private static string Node(string id, string type, string props) =>
        "{\"id\":\"" + id + "\",\"type\":\"" + type + "\",\"properties\":{" + props
        + "},\"text\":{\"value\":\"" + id + "\"}}";

    private static string Edge(string id, string source, string target) =>
        "{\"id\":\"" + id + "\",\"sourceNodeId\":\"" + source + "\",\"targetNodeId\":\"" + target
        + "\",\"properties\":{}}";

    private static async Task<long> AddDefineAsync(MemoryRepository repo, string name, int version,
        string json, int state = 1)
    {
        var define = new ProcessDefine
        {
            Name = name,
            DisplayName = name,
            Type = "approval",
            State = state,
            Version = version,
            Content = System.Text.Encoding.UTF8.GetBytes(json),
        };
        await repo.SaveDefineAsync(define);
        return define.Id!.Value;
    }

    private static async Task<ProcessInstance> StartWithApplyAsync(JeeflowEngine engine, long defineId, FlowData args)
    {
        var instance = await engine.StartProcessInstanceByIdAsync(defineId, "applicant", args);
        await StartAndExecuteSupport.ApplyInitialTasksAsync(engine, instance.InstanceId!.Value, "applicant", args);
        return instance;
    }

    private static async Task<ProcessInstance> OnlyChildAsync(MemoryRepository repo, long parentId)
    {
        var page = await repo.PageInstancesAsync(new PageQuery(1, 100));
        var children = new List<ProcessInstance>();
        foreach (var row in page.Rows)
        {
            var instance = await repo.FindInstanceByIdAsync(row.Id);
            if (instance?.ParentId == parentId) children.Add(instance);
        }
        return Assert.Single(children);
    }

    private static async Task<ProcessTask> OnlyDoingAsync(MemoryRepository repo, long instanceId) =>
        Assert.Single(await repo.FindDoingTasksAsync(instanceId, null));

    private sealed class Recorder : IProcessEventListener
    {
        public List<ProcessEvent> Events { get; } = new();
        public Task OnEventAsync(ProcessEvent @event)
        {
            Events.Add(@event);
            return Task.CompletedTask;
        }
        public int Count(ProcessEventType type, long sourceId) =>
            Events.Count(e => e.EventType == type && e.SourceId == sourceId);
    }

    private sealed record EndOrder(long InstanceId, int StateAtEvent);

    private sealed class EndOrderRecorder : IProcessEventListener
    {
        private readonly MemoryRepository _repo;
        public List<EndOrder> Events { get; } = new();
        public EndOrderRecorder(MemoryRepository repo) => _repo = repo;
        public async Task OnEventAsync(ProcessEvent @event)
        {
            if (@event.EventType != ProcessEventType.ProcessInstanceEnd) return;
            var id = @event.SourceId!.Value;
            Events.Add(new EndOrder(id, (await _repo.FindInstanceByIdAsync(id))!.State!.Value));
        }
        public void Clear() => Events.Clear();
    }
}

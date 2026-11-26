using Mldong.Jeeflow.Core;
using Xunit;

namespace Mldong.Jeeflow.Tests;

/// <summary>issues/258：表单指定 1–N 个部门的负责人／分管领导。</summary>
public class FormDeptAssignmentHandlerTests
{
    private const string FqLeader = "com.mldong.jeeflow.interceptor.impl.FormDeptLeaderAssignmentHandler";
    private const string FqMain = "com.mldong.jeeflow.interceptor.impl.FormDeptMainLeaderAssignmentHandler";

    private sealed class OrgProv : IOrgUserProvider
    {
        private static readonly Dictionary<string, List<string>?> Leaders = new()
        {
            ["deptA"] = new() { "leaderA", "shared" },
            ["deptB"] = new() { "leaderB", "shared" },
            ["deptC"] = new() { "leaderC1", "leaderC2" },
            ["0"] = new() { "zeroLeader" },
            ["dup"] = new() { "same", "same", " other " },
            ["blank"] = new() { null!, "", "   ", " padded ", "padded" },
            ["nullList"] = null!,
        };

        private static readonly Dictionary<string, List<string>?> MainLeaders = new()
        {
            ["deptA"] = new() { "mainA" },
            ["deptB"] = new() { "mainB" },
            ["deptC"] = new() { "mainC" },
            ["0"] = new() { "zeroMain" },
        };

        public List<string> LeaderCalls { get; } = new();
        public List<string> MainLeaderCalls { get; } = new();
        public HashSet<string> LeaderFailures { get; } = new();
        public HashSet<string> MainLeaderFailures { get; } = new();

        public Task<List<string>> FindDeptLeadersAsync(string deptId)
        {
            LeaderCalls.Add(deptId);
            if (LeaderFailures.Contains(deptId)) throw new InvalidOperationException("leader lookup failed");
            return Task.FromResult(Leaders.TryGetValue(deptId, out var ids) ? ids! : new List<string>());
        }

        public Task<List<string>> FindDeptMainLeadersAsync(string deptId)
        {
            MainLeaderCalls.Add(deptId);
            if (MainLeaderFailures.Contains(deptId)) throw new InvalidOperationException("main leader lookup failed");
            return Task.FromResult(MainLeaders.TryGetValue(deptId, out var ids) ? ids! : new List<string>());
        }

        public Task<List<string>> FindByRoleAsync(string roleCode) => Task.FromResult(new List<string>());
    }

    private static Execution Exec(string? nodeKey, FlowData? args, IOrgUserProvider? org = null)
    {
        var ctx = new ServiceContext(new MemoryRepository()) { OrgUserProvider = org };
        return new Execution
        {
            Context = ctx,
            NodeModel = nodeKey == null ? null : new TaskModel { Name = nodeKey },
            Args = args!,
        };
    }

    private static Task<string?> Assign(bool main, string? nodeKey, FlowData? args, IOrgUserProvider? org = null)
    {
        IAssignmentHandler handler = main
            ? new BuiltinAssignmentHandlers.FormDeptMainLeaderAssignmentHandler()
            : new BuiltinAssignmentHandlers.FormDeptLeaderAssignmentHandler();
        return handler.AssignAsync(Exec(nodeKey, args, org));
    }

    private static Task<string?> Leaders(string? nodeKey, FlowData? args, IOrgUserProvider? org = null) =>
        Assign(false, nodeKey, args, org);

    private static Task<string?> MainLeaders(string? nodeKey, FlowData? args, IOrgUserProvider? org = null) =>
        Assign(true, nodeKey, args, org);

    // ═══ 四档字段查找（顺序即优先级）══════════════════════════════════════════

    [Theory]
    [InlineData("depts", "f_depts", "deptA", "depts", "deptB", "leaderA,shared")]
    [InlineData("depts", "depts", "deptB", "other", "deptA", "leaderB,shared")]
    [InlineData("manager#deptIds", "deptIds", "deptA", "f_deptIds", "deptB", "leaderA,shared")]
    [InlineData("manager#deptIds", "f_deptIds", "deptB", "other", "deptA", "leaderB,shared")]
    [InlineData("depts_01", "f_depts", "deptA", "depts", "deptB", "leaderA,shared")]
    [InlineData("depts_01", "depts", "deptA", "other", "deptB", "leaderA,shared")]
    public async Task FieldLookupFourTiers(
        string nodeKey, string key1, string value1, string key2, string value2, string expected)
    {
        var args = new FlowData().Set(key1, value1).Set(key2, value2);
        Assert.Equal(expected, await Leaders(nodeKey, args, new OrgProv()));
    }

    [Fact]
    public async Task HashTierUsesLastSegmentAndPrefersBareField()
    {
        var args = new FlowData().Set("deptIds", "deptA").Set("f_deptIds", "deptB");
        Assert.Equal("leaderA,shared", await Leaders("a#b#deptIds", args, new OrgProv()));
    }

    [Fact]
    public async Task NumberSuffixTierUsesLazyBaseAndRejectsInvalidShapes()
    {
        var org = new OrgProv();
        var args = new FlowData().Set("a_b", "deptA").Set("a", "deptB");
        Assert.Equal("leaderA,shared", await Leaders("a_b_01", args, org));
        Assert.Null(await Leaders("_01", new FlowData().Set("f_", "deptA"), org));
        Assert.Null(await Leaders("depts_01x", new FlowData().Set("depts", "deptA"), org));
    }

    [Fact]
    public async Task PresentNullStopsFallback()
    {
        var org = new OrgProv();
        Assert.Null(await Leaders("depts",
            new FlowData().Set("f_depts", null).Set("depts", "deptB"), org));
        Assert.Null(await Leaders("manager#deptIds",
            new FlowData().Set("deptIds", null).Set("f_deptIds", "deptB"), org));
        Assert.Empty(org.LeaderCalls);
    }

    // ═══ 部门列表解析与合并顺序 ═══════════════════════════════════════════════

    [Fact]
    public async Task CommaStringResolvesAllDepartmentsInOrder()
    {
        var org = new OrgProv();
        Assert.Equal("leaderA,shared,leaderB",
            await Leaders("depts", new FlowData().Set("depts", "deptA, deptB"), org));
        Assert.Equal(new[] { "deptA", "deptB" }, org.LeaderCalls);
    }

    [Fact]
    public async Task ArrayResolvesAllDepartmentsInOrder()
    {
        var org = new OrgProv();
        Assert.Equal("leaderA,shared,leaderB",
            await Leaders("depts", new FlowData().Set("depts", new object?[] { "deptA", "deptB" }), org));
        Assert.Equal(new[] { "deptA", "deptB" }, org.LeaderCalls);
    }

    [Fact]
    public async Task ArrayElementIsNotResplit()
    {
        var org = new OrgProv();
        Assert.Null(await Leaders("depts",
            new FlowData().Set("depts", new[] { "deptA,deptB" }), org));
        Assert.Equal(new[] { "deptA,deptB" }, org.LeaderCalls);
    }

    [Fact]
    public async Task TrimsDropsEmptyAndDedupesDepartmentsWhilePreservingCallOrder()
    {
        var org = new OrgProv();
        Assert.Equal("leaderA,shared,leaderB", await Leaders("depts",
            new FlowData().Set("depts", " , deptA, ,deptB, deptA, "), org));
        Assert.Equal(new[] { "deptA", "deptB" }, org.LeaderCalls);
    }

    [Fact]
    public async Task DuplicateDepartmentsAndLeadersAreDeduped()
    {
        var org = new OrgProv();
        Assert.Equal("leaderA,shared,leaderB,same,other", await Leaders("depts",
            new FlowData().Set("depts", "deptA,deptB,deptA,dup"), org));
        Assert.Equal(new[] { "deptA", "deptB", "dup" }, org.LeaderCalls);
    }

    [Fact]
    public async Task InvalidDepartmentIsSkippedWithoutBlockingLaterDepartments()
    {
        var org = new OrgProv();
        Assert.Equal("leaderB,shared",
            await Leaders("depts", new FlowData().Set("depts", "ghost,deptB"), org));
        Assert.Equal(new[] { "ghost", "deptB" }, org.LeaderCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0L)]
    [InlineData("0")]
    public async Task ZeroDepartmentIdIsLegal(object value)
    {
        Assert.Equal("zeroLeader",
            await Leaders("depts", new FlowData().Set("depts", value), new OrgProv()));
    }

    [Fact]
    public async Task MainLeaderVariantUsesOnlyMainLeaderSpi()
    {
        var org = new OrgProv();
        Assert.Equal("mainA,mainB",
            await MainLeaders("depts", new FlowData().Set("depts", "deptA,deptB"), org));
        Assert.Equal(new[] { "deptA", "deptB" }, org.MainLeaderCalls);
        Assert.Empty(org.LeaderCalls);
    }

    [Fact]
    public async Task MultipleLeadersKeepSpiOrder()
    {
        Assert.Equal("leaderC1,leaderC2",
            await Leaders("depts", new FlowData().Set("depts", "deptC"), new OrgProv()));
    }

    [Fact]
    public async Task NullLeaderListIsSkipped()
    {
        var org = new OrgProv();
        Assert.Equal("leaderB,shared",
            await Leaders("depts", new FlowData().Set("depts", "nullList,deptB"), org));
        Assert.Equal(new[] { "nullList", "deptB" }, org.LeaderCalls);
    }

    [Fact]
    public async Task BlankLeaderIdsAreDroppedAndRemainingIdsTrimmedAndDeduped()
    {
        Assert.Equal("padded",
            await Leaders("depts", new FlowData().Set("depts", "blank"), new OrgProv()));
    }

    [Fact]
    public async Task SpiFailureSkipsDepartmentAndContinues()
    {
        var org = new OrgProv();
        org.LeaderFailures.Add("deptA");
        org.MainLeaderFailures.Add("deptA");
        var args = new FlowData().Set("depts", "deptA,deptB");
        Assert.Equal("leaderB,shared", await Leaders("depts", args, org));
        Assert.Equal("mainB", await MainLeaders("depts", args, org));
        Assert.Equal(new[] { "deptA", "deptB" }, org.LeaderCalls);
        Assert.Equal(new[] { "deptA", "deptB" }, org.MainLeaderCalls);
    }

    // ═══ 失败语义：空且不抛 ═══════════════════════════════════════════════════

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(", ,")]
    public async Task EmptyAndNullValuesAreEmpty(object? value)
    {
        Assert.Null(await Leaders("depts", new FlowData().Set("depts", value), new OrgProv()));
    }

    [Fact]
    public async Task EmptyCollectionAndMapAreEmpty()
    {
        var org = new OrgProv();
        Assert.Null(await Leaders("depts", new FlowData().Set("depts", Array.Empty<object?>()), org));
        Assert.Null(await Leaders("depts",
            new FlowData().Set("depts", new List<object?> { null, "", "   " }), org));
        Assert.Null(await Leaders("depts",
            new FlowData().Set("depts", new Dictionary<string, object?>()), org));
        Assert.Null(await Leaders("depts",
            new FlowData().Set("depts", new Dictionary<string, object?> { ["deptA"] = "ignored" }), org));
    }

    [Fact]
    public async Task MissingFieldIsEmpty()
    {
        Assert.Null(await Leaders("depts", new FlowData().Set("other", "deptA"), new OrgProv()));
    }

    [Fact]
    public async Task MissingOrgSpiIsEmptyForBothTiers()
    {
        var args = new FlowData().Set("depts", "deptA");
        Assert.Null(await Leaders("depts", args));
        Assert.Null(await MainLeaders("depts", args));
    }

    [Fact]
    public async Task NoNodeOrEmptyNodeIsEmptyForBothTiers()
    {
        var args = new FlowData().Set("f_", "deptA");
        Assert.Null(await Leaders(null, args, new OrgProv()));
        Assert.Null(await MainLeaders(null, args, new OrgProv()));
        Assert.Null(await Leaders("", args, new OrgProv()));
        Assert.Null(await MainLeaders("", args, new OrgProv()));
    }

    [Fact]
    public async Task MissingContextAndNullArgsDoNotThrow()
    {
        var args = new FlowData().Set("depts", "deptA");
        var noContext = new Execution
        {
            Context = null!,
            NodeModel = new TaskModel { Name = "depts" },
            Args = args,
        };
        Assert.Null(await new BuiltinAssignmentHandlers.FormDeptLeaderAssignmentHandler().AssignAsync(noContext));
        Assert.Null(await new BuiltinAssignmentHandlers.FormDeptMainLeaderAssignmentHandler().AssignAsync(noContext));
        Assert.Null(await Leaders("depts", null, new OrgProv()));
    }

    // ═══ 元数据、注册与引擎接线 ═══════════════════════════════════════════════

    [Fact]
    public void MetadataExposesTwoNewHandlersInOrder()
    {
        var metas = new HandlerRegistry().ListHandlers("AssignmentHandler");
        Assert.Equal(11, metas.Count);
        Assert.Equal(FqLeader, metas[^2].ClassName);
        Assert.Equal("表单指定部门负责人", metas[^2].DisplayName);
        Assert.Equal(90, metas[^2].Order);
        Assert.Equal(FqMain, metas[^1].ClassName);
        Assert.Equal("表单指定部门分管领导", metas[^1].DisplayName);
        Assert.Equal(100, metas[^1].Order);
    }

    [Fact]
    public void RegistrationNamesResolveToNewClasses()
    {
        var ctx = TestInfra.NewContext(new MemoryRepository());
        Assert.Equal(11, ctx.AssignmentHandlers.Count);
        Assert.IsType<BuiltinAssignmentHandlers.FormDeptLeaderAssignmentHandler>(
            ctx.FindAssignmentHandler(FqLeader));
        Assert.IsType<BuiltinAssignmentHandlers.FormDeptMainLeaderAssignmentHandler>(
            ctx.FindAssignmentHandler(FqMain));
    }

    private static string FlowJson(string name)
    {
        string Task(string id, string handler, int y) =>
            "{\"id\":\"" + id + "\",\"type\":\"snaker:task\",\"x\":480,\"y\":" + y +
            ",\"text\":{\"value\":\"" + id + "\"},\"properties\":{\"width\":120,\"height\":80," +
            "\"assignee\":\"\",\"taskType\":\"Major\",\"performType\":\"ANY\",\"autoExecute\":\"N\"," +
            "\"assignmentHandler\":\"" + handler + "\"}}";
        return "{\"nodes\":[" +
               "{\"id\":\"start\",\"type\":\"snaker:start\",\"x\":480,\"y\":200,\"text\":{\"value\":\"开始\"},\"properties\":{}}," +
               "{\"id\":\"apply\",\"type\":\"snaker:task\",\"x\":480,\"y\":350,\"text\":{\"value\":\"发起申请\"}," +
               "\"properties\":{\"assignee\":\"applicant\",\"taskType\":\"Major\",\"performType\":\"ANY\",\"autoExecute\":\"N\"}}," +
               Task("manager#f_deptIds", FqLeader, 500) + "," +
               Task("mainLeader#f_deptIds", FqMain, 650) + "," +
               "{\"id\":\"end\",\"type\":\"snaker:end\",\"x\":480,\"y\":800,\"text\":{\"value\":\"结束\"},\"properties\":{}}]," +
               "\"edges\":[" +
               "{\"id\":\"e0\",\"type\":\"snaker:transition\",\"sourceNodeId\":\"start\",\"targetNodeId\":\"apply\",\"properties\":{}}," +
               "{\"id\":\"e1\",\"type\":\"snaker:transition\",\"sourceNodeId\":\"apply\",\"targetNodeId\":\"manager#f_deptIds\",\"properties\":{}}," +
               "{\"id\":\"e2\",\"type\":\"snaker:transition\",\"sourceNodeId\":\"manager#f_deptIds\",\"targetNodeId\":\"mainLeader#f_deptIds\",\"properties\":{}}," +
               "{\"id\":\"e3\",\"type\":\"snaker:transition\",\"sourceNodeId\":\"mainLeader#f_deptIds\",\"targetNodeId\":\"end\",\"properties\":{}}" +
               "],\"name\":\"" + name + "\",\"displayName\":\"表单指定部门领导\"}";
    }

    [Fact]
    public async Task EngineFlowLandsOnFormDeptLeadersAndMainLeaders()
    {
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        var org = new OrgProv();
        ctx.OrgUserProvider = org;
        var defineId = await TestInfra.SaveFlowDefineAsync(repo, "fd-two-tiers", FlowJson("fd-two-tiers"));
        var instId = await TestInfra.StartAndApplyAsync(engine, repo, defineId,
            new FlowData().Set("f_deptIds", "deptA, deptB,deptA"));

        var manager = Assert.Single(await repo.FindDoingTasksAsync(instId, null));
        Assert.Equal("manager#f_deptIds", manager.TaskName);
        Assert.Equal(new[] { "leaderA", "shared", "leaderB" }, manager.ActorIds);

        await engine.ExecuteProcessTaskAsync(manager.TaskId!.Value, "leaderA", new FlowData());
        var main = Assert.Single(await repo.FindDoingTasksAsync(instId, null));
        Assert.Equal("mainLeader#f_deptIds", main.TaskName);
        Assert.Equal(new[] { "mainA", "mainB" }, main.ActorIds);
        Assert.Equal(new[] { "deptA", "deptB" }, org.LeaderCalls);
        Assert.Equal(new[] { "deptA", "deptB" }, org.MainLeaderCalls);
    }

    [Fact]
    public async Task EmptyActorsDoNotBreakInstance()
    {
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        ctx.OrgUserProvider = new OrgProv();
        var defineId = await TestInfra.SaveFlowDefineAsync(repo, "fd-empty", FlowJson("fd-empty"));
        var instId = await TestInfra.StartAndApplyAsync(engine, repo, defineId,
            new FlowData().Set("f_deptIds", "ghost"));

        var task = Assert.Single(await repo.FindDoingTasksAsync(instId, null));
        Assert.Empty(task.ActorIds);
        var inst = await repo.FindInstanceByIdAsync(instId);
        Assert.Equal((int)WfInstanceState.Doing, inst!.State);
    }
}

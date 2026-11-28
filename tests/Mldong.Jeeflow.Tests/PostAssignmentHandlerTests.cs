using Mldong.Jeeflow.Core;
using Xunit;

namespace Mldong.Jeeflow.Tests;

/// <summary>issues/259：任务节点关联岗位、表单指定 1–N 个岗位分配参与者。</summary>
public class PostAssignmentHandlerTests
{
    private const string FqTask =
        "com.mldong.jeeflow.interceptor.impl.OrgUserAssignmentHandlers$TaskPostAssigneeHandler";
    private const string FqForm = "com.mldong.jeeflow.interceptor.impl.FormPostAssignmentHandler";

    private sealed class PostProv : IPostUserProvider
    {
        private readonly Dictionary<string, List<string>?> _users = new()
        {
            ["postA"] = new() { "userA", "shared" },
            ["postB"] = new() { "userB", "shared" },
            ["postC"] = new() { "userC1", "userC2" },
            ["archive_clerk"] = new() { "userC1", "userC2" },
            ["0"] = new() { "zeroUser" },
            ["dup"] = new() { "same", "same", " other " },
            ["blank"] = new() { null!, "", "   ", " padded ", "padded" },
            ["empty"] = new(),
            ["nullList"] = null!,
        };

        public List<string> Calls { get; } = new();
        public HashSet<string> Failures { get; } = new();

        public Task<List<string>> FindByPostAsync(string postCode)
        {
            Calls.Add(postCode);
            if (Failures.Contains(postCode)) throw new InvalidOperationException("post lookup failed");
            return Task.FromResult(_users.TryGetValue(postCode, out var ids) ? ids! : new List<string>());
        }
    }

    private sealed class OrgProv : IOrgUserProvider
    {
        public Task<List<string>> FindDeptLeadersAsync(string deptId) =>
            Task.FromResult(new List<string> { "deptLeader" });
        public Task<List<string>> FindDeptMainLeadersAsync(string deptId) =>
            Task.FromResult(new List<string> { "deptMainLeader" });
        public Task<List<string>> FindByRoleAsync(string roleCode) =>
            Task.FromResult(new List<string> { "roleUser" });
    }

    private static Execution Exec(
        string? nodeKey, FlowData? args, IPostUserProvider? provider = null, bool withContext = true)
    {
        ServiceContext? ctx = withContext
            ? new ServiceContext(new MemoryRepository())
            {
                OrgUserProvider = new OrgProv(),
                PostUserProvider = provider,
            }
            : null;
        return new Execution
        {
            Context = ctx!,
            NodeModel = nodeKey == null ? null : new TaskModel { Name = nodeKey },
            Args = args!,
        };
    }

    private static Task<string?> AssignTask(
        string? nodeKey, IPostUserProvider? provider = null, bool withContext = true) =>
        new BuiltinAssignmentHandlers.TaskPostAssigneeHandler().AssignAsync(
            Exec(nodeKey, new FlowData(), provider, withContext));

    private static Task<string?> AssignForm(
        string? nodeKey, FlowData? args, IPostUserProvider? provider = null, bool withContext = true) =>
        new BuiltinAssignmentHandlers.FormPostAssignmentHandler().AssignAsync(
            Exec(nodeKey, args, provider, withContext));

    // ═══ TaskPost：节点唯一编码即岗位编码 ════════════════════════════════════

    [Theory]
    [InlineData("postA", "userA,shared")]
    [InlineData("postC", "userC1,userC2")]
    [InlineData("archive_clerk", "userC1,userC2")]
    [InlineData("0", "zeroUser")]
    public async Task NodeNameIsPostCodeAndProviderOrderIsPreserved(string nodeKey, string expected)
    {
        var provider = new PostProv();
        Assert.Equal(expected, await AssignTask(nodeKey, provider));
        Assert.Equal(new[] { nodeKey }, provider.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task MissingOrEmptyNodeIsEmpty(string? nodeKey)
    {
        var provider = new PostProv();
        Assert.Null(await AssignTask(nodeKey, provider));
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task MissingProviderIsEmpty()
    {
        Assert.Null(await AssignTask("postA"));
        Assert.Null(await AssignTask("postA", withContext: false));
    }

    [Theory]
    [InlineData("ghost")]
    [InlineData("empty")]
    [InlineData("nullList")]
    public async Task UnknownEmptyOrNullResultIsEmpty(string postCode)
    {
        var provider = new PostProv();
        Assert.Null(await AssignTask(postCode, provider));
        Assert.Equal(new[] { postCode }, provider.Calls);
    }

    [Fact]
    public async Task ProviderFailureIsEmptyAndDoesNotThrow()
    {
        var provider = new PostProv();
        provider.Failures.Add("postA");
        Assert.Null(await AssignTask("postA", provider));
        Assert.Equal(new[] { "postA" }, provider.Calls);
    }

    // ═══ FormPost：四档字段查找，键存在即命中 ════════════════════════════════

    [Theory]
    [InlineData("posts", "f_posts", "postA", "posts", "postB", "userA,shared")]
    [InlineData("posts", "posts", "postB", "other", "postA", "userB,shared")]
    [InlineData("manager#postIds", "postIds", "postA", "f_postIds", "postB", "userA,shared")]
    [InlineData("manager#postIds", "f_postIds", "postB", "other", "postA", "userB,shared")]
    [InlineData("posts_01", "f_posts", "postA", "posts", "postB", "userA,shared")]
    [InlineData("posts_01", "posts", "postB", "other", "postA", "userB,shared")]
    public async Task FieldLookupFourTiers(
        string nodeKey, string key1, string value1, string key2, string value2, string expected)
    {
        var args = new FlowData().Set(key1, value1).Set(key2, value2);
        Assert.Equal(expected, await AssignForm(nodeKey, args, new PostProv()));
    }

    [Fact]
    public async Task HashTierUsesLastSegmentAndPrefersBareField()
    {
        var args = new FlowData().Set("postIds", "postA").Set("f_postIds", "postB");
        Assert.Equal("userA,shared", await AssignForm("a#b#postIds", args, new PostProv()));
    }

    [Fact]
    public async Task NumberSuffixTierUsesLazyBaseAndRejectsInvalidShapes()
    {
        var provider = new PostProv();
        var args = new FlowData().Set("a_b", "postA").Set("a", "postB");
        Assert.Equal("userA,shared", await AssignForm("a_b_01", args, provider));
        Assert.Null(await AssignForm("_01", new FlowData().Set("f_", "postA"), provider));
        Assert.Null(await AssignForm("posts_01x", new FlowData().Set("posts", "postA"), provider));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PresentEmptyOrNullStopsFallback(string? value)
    {
        var provider = new PostProv();
        var args = new FlowData().Set("f_posts", value).Set("posts", "postA");
        Assert.Null(await AssignForm("posts", args, provider));
        Assert.Empty(provider.Calls);
    }

    [Theory]
    [InlineData("posts", "f_posts", "posts")]
    [InlineData("manager#postIds", "postIds", "f_postIds")]
    [InlineData("manager#postIds", "f_postIds", "other")]
    [InlineData("posts_01", "f_posts", "posts")]
    [InlineData("posts_01", "posts", "other")]
    public async Task PresentNullStopsFallback(string nodeKey, string nullKey, string fallbackKey)
    {
        var provider = new PostProv();
        var args = new FlowData().Set(nullKey, null).Set(fallbackKey, "postA");
        Assert.Null(await AssignForm(nodeKey, args, provider));
        Assert.Empty(provider.Calls);
    }

    // ═══ FormPost：岗位编码解析、逐岗位查询与用户合并 ════════════════════════

    [Fact]
    public async Task CommaStringTrimsDropsEmptyAndDedupesPostCodesInOrder()
    {
        var provider = new PostProv();
        Assert.Equal("userA,shared,userB", await AssignForm("posts",
            new FlowData().Set("posts", " , postA, ,postB, postA, "), provider));
        Assert.Equal(new[] { "postA", "postB" }, provider.Calls);
    }

    [Fact]
    public async Task ArrayAndCollectionReadElementsWithoutResplitting()
    {
        var arrayProvider = new PostProv();
        Assert.Equal("userA,shared,userB", await AssignForm("posts",
            new FlowData().Set("posts", new object?[] { "postA", "postB" }), arrayProvider));
        Assert.Equal(new[] { "postA", "postB" }, arrayProvider.Calls);

        var listProvider = new PostProv();
        Assert.Equal("userA,shared,userB", await AssignForm("posts",
            new FlowData().Set("posts", new List<object?> { "postA", "postB" }), listProvider));
        Assert.Equal(new[] { "postA", "postB" }, listProvider.Calls);
    }

    [Fact]
    public async Task ArrayElementContainingCommaIsOnePostCode()
    {
        var provider = new PostProv();
        Assert.Null(await AssignForm("posts",
            new FlowData().Set("posts", new[] { "postA,postB" }), provider));
        Assert.Equal(new[] { "postA,postB" }, provider.Calls);

        var listProvider = new PostProv();
        Assert.Null(await AssignForm("posts",
            new FlowData().Set("posts", new List<object?> { "postA,postB" }), listProvider));
        Assert.Equal(new[] { "postA,postB" }, listProvider.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0L)]
    [InlineData("0")]
    public async Task ZeroPostCodeIsLegal(object value)
    {
        var provider = new PostProv();
        Assert.Equal("zeroUser", await AssignForm("posts", new FlowData().Set("posts", value), provider));
        Assert.Equal(new[] { "0" }, provider.Calls);
    }

    [Fact]
    public async Task MultiplePostsKeepQueryAndProviderOrderWhileDedupingUsers()
    {
        var provider = new PostProv();
        Assert.Equal("userB,shared,userA", await AssignForm("posts",
            new FlowData().Set("posts", "postB,postA,postB"), provider));
        Assert.Equal(new[] { "postB", "postA" }, provider.Calls);
    }

    [Fact]
    public async Task UserIdsAreTrimmedBlankDroppedAndDedupedInFirstSeenOrder()
    {
        var provider = new PostProv();
        Assert.Equal("same,other,padded", await AssignForm("posts",
            new FlowData().Set("posts", "dup,blank,dup"), provider));
        Assert.Equal(new[] { "dup", "blank" }, provider.Calls);
    }

    [Fact]
    public async Task SinglePostFailureIsSkippedAndLaterPostsContinue()
    {
        var provider = new PostProv();
        provider.Failures.Add("postA");
        Assert.Equal("userB,shared", await AssignForm("posts",
            new FlowData().Set("posts", "postA,postB"), provider));
        Assert.Equal(new[] { "postA", "postB" }, provider.Calls);
    }

    [Fact]
    public async Task NullPostResultIsSkippedAndLaterPostsContinue()
    {
        var provider = new PostProv();
        Assert.Equal("userB,shared", await AssignForm("posts",
            new FlowData().Set("posts", "nullList,postB"), provider));
        Assert.Equal(new[] { "nullList", "postB" }, provider.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(", ,")]
    public async Task EmptyAndNullValuesAreEmpty(object? value)
    {
        var provider = new PostProv();
        Assert.Null(await AssignForm("posts", new FlowData().Set("posts", value), provider));
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task EmptyCollectionIsEmpty()
    {
        var provider = new PostProv();
        Assert.Null(await AssignForm("posts",
            new FlowData().Set("posts", Array.Empty<object?>()), provider));
        Assert.Null(await AssignForm("posts",
            new FlowData().Set("posts", new List<object?> { null, "", "   " }), provider));
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task AllPostsWithoutUsersAreEmpty()
    {
        var provider = new PostProv();
        Assert.Null(await AssignForm("posts",
            new FlowData().Set("posts", "ghost,empty,nullList"), provider));
        Assert.Equal(new[] { "ghost", "empty", "nullList" }, provider.Calls);
    }

    [Fact]
    public async Task MissingFieldProviderNodeOrContextIsEmpty()
    {
        var provider = new PostProv();
        Assert.Null(await AssignForm("posts", new FlowData().Set("other", "postA"), provider));
        Assert.Null(await AssignForm("posts", new FlowData().Set("posts", "postA")));
        Assert.Null(await AssignForm(null, new FlowData().Set("f_", "postA"), provider));
        Assert.Null(await AssignForm("", new FlowData().Set("f_", "postA"), provider));
        Assert.Null(await AssignForm("posts", null, provider));
        Assert.Null(await AssignForm("posts", new FlowData().Set("posts", "postA"), provider, false));
        Assert.Empty(provider.Calls);
    }

    // ═══ 元数据、运行时注册与旧 11 枚回归 ═══════════════════════════════════

    [Fact]
    public void MetadataExposesPostHandlersWithExactNamesDisplayNamesAndOrder()
    {
        var metas = new HandlerRegistry().ListHandlers("AssignmentHandler");
        Assert.Equal(13, metas.Count);
        Assert.Equal(metas.Select(m => m.Order).OrderBy(x => x), metas.Select(m => m.Order));

        var task = Assert.Single(metas, m => m.ClassName == FqTask);
        Assert.Equal("根据任务节点唯一编码关联岗位分配参与者", task.DisplayName);
        Assert.Equal(65, task.Order);
        var form = Assert.Single(metas, m => m.ClassName == FqForm);
        Assert.Equal("表单指定岗位分配参与者", form.DisplayName);
        Assert.Equal(110, form.Order);
    }

    [Fact]
    public void ExistingElevenHandlersKeepTheirMetadataOrder()
    {
        string[] existing =
        {
            "com.mldong.jeeflow.interceptor.impl.OperatorAssignmentHandler",
            "com.mldong.jeeflow.interceptor.impl.OrgUserAssignmentHandlers$ApplicantDeptLeaderAssignmentHandler",
            "com.mldong.jeeflow.interceptor.impl.OrgUserAssignmentHandlers$ApplicantDeptMainLeaderAssignmentHandler",
            "com.mldong.jeeflow.interceptor.impl.OrgUserAssignmentHandlers$DeptLeaderAssignmentHandler",
            "com.mldong.jeeflow.interceptor.impl.OrgUserAssignmentHandlers$DeptMainLeaderAssignmentHandler",
            "com.mldong.jeeflow.interceptor.impl.FormFieldAssigneeHandler",
            "com.mldong.jeeflow.interceptor.impl.OrgUserAssignmentHandlers$TaskRoleAssigneeHandler",
            "com.mldong.jeeflow.interceptor.impl.FormUserDeptLeaderAssignmentHandler",
            "com.mldong.jeeflow.interceptor.impl.FormUserDeptMainLeaderAssignmentHandler",
            "com.mldong.jeeflow.interceptor.impl.FormDeptLeaderAssignmentHandler",
            "com.mldong.jeeflow.interceptor.impl.FormDeptMainLeaderAssignmentHandler",
        };
        var actual = new HandlerRegistry().ListHandlers("AssignmentHandler")
            .Where(m => m.ClassName != FqTask && m.ClassName != FqForm)
            .Select(m => m.ClassName);
        Assert.Equal(existing, actual);
    }

    [Fact]
    public void RuntimeRegistrationNamesResolveToBothNewHandlers()
    {
        var ctx = TestInfra.NewContext(new MemoryRepository());
        Assert.Equal(13, ctx.AssignmentHandlers.Count);
        Assert.IsType<BuiltinAssignmentHandlers.TaskPostAssigneeHandler>(ctx.FindAssignmentHandler(FqTask));
        Assert.IsType<BuiltinAssignmentHandlers.FormPostAssignmentHandler>(ctx.FindAssignmentHandler(FqForm));
    }

    // ═══ 引擎级：两个 Java FQCN 从流程定义解析并真实取人 ═══════════════════════

    [Fact]
    public async Task EngineFlowResolvesTaskPostThenFormPost()
    {
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        var provider = new PostProv();
        ctx.PostUserProvider = provider;
        var defineId = await TestInfra.SaveFlowDefineAsync(repo, "post-two-tiers", FlowJson("post-two-tiers"));
        var instId = await TestInfra.StartAndApplyAsync(engine, repo, defineId,
            new FlowData().Set("f_postIds", "postA,postB,postA"));

        var archive = Assert.Single(await repo.FindDoingTasksAsync(instId, null));
        Assert.Equal("archive_clerk", archive.TaskName);
        Assert.Equal(new[] { "userC1", "userC2" }, archive.ActorIds);

        await engine.ExecuteProcessTaskAsync(archive.TaskId!.Value, "userC1", new FlowData());
        var form = Assert.Single(await repo.FindDoingTasksAsync(instId, null));
        Assert.Equal("manager#postIds", form.TaskName);
        Assert.Equal(new[] { "userA", "shared", "userB" }, form.ActorIds);
        Assert.Equal(new[] { "archive_clerk", "postA", "postB" }, provider.Calls);

        await engine.ExecuteProcessTaskAsync(form.TaskId!.Value, "userA", new FlowData());
        var instance = await repo.FindInstanceByIdAsync(instId);
        Assert.Equal((int)WfInstanceState.Finished, instance!.State);
    }

    [Fact]
    public async Task MissingPostProviderCreatesEmptyTaskWithoutBreakingInstance()
    {
        var (engine, repo, _) = TestInfra.NewEngineWithCtx();
        var defineId = await TestInfra.SaveFlowDefineAsync(repo, "post-no-provider", FlowJson("post-no-provider"));
        var instId = await TestInfra.StartAndApplyAsync(engine, repo, defineId,
            new FlowData().Set("f_postIds", "postA"));

        var archive = Assert.Single(await repo.FindDoingTasksAsync(instId, null));
        Assert.Empty(archive.ActorIds);
        var instance = await repo.FindInstanceByIdAsync(instId);
        Assert.Equal((int)WfInstanceState.Doing, instance!.State);
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
               Task("archive_clerk", FqTask, 500) + "," +
               Task("manager#postIds", FqForm, 650) + "," +
               "{\"id\":\"end\",\"type\":\"snaker:end\",\"x\":480,\"y\":800,\"text\":{\"value\":\"结束\"},\"properties\":{}}]," +
               "\"edges\":[" +
               "{\"id\":\"e0\",\"type\":\"snaker:transition\",\"sourceNodeId\":\"start\",\"targetNodeId\":\"apply\",\"properties\":{}}," +
               "{\"id\":\"e1\",\"type\":\"snaker:transition\",\"sourceNodeId\":\"apply\",\"targetNodeId\":\"archive_clerk\",\"properties\":{}}," +
               "{\"id\":\"e2\",\"type\":\"snaker:transition\",\"sourceNodeId\":\"archive_clerk\",\"targetNodeId\":\"manager#postIds\",\"properties\":{}}," +
               "{\"id\":\"e3\",\"type\":\"snaker:transition\",\"sourceNodeId\":\"manager#postIds\",\"targetNodeId\":\"end\",\"properties\":{}}" +
               "],\"name\":\"" + name + "\",\"displayName\":\"表单指定岗位分配参与者\"}";
    }
}

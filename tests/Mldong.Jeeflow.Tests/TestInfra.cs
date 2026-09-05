namespace Mldong.Jeeflow.Tests;

using Mldong.Jeeflow.Core;

/// <summary>
/// T0 测试基建：内存仓储 + 固定钟 + 8 具名用户 + 内置 handler 注册 + flows 加载。
/// </summary>
public static class TestInfra
{
    public static readonly Dictionary<string, (string RealName, string DeptId)> NamedUsers = new()
    {
        ["user1"] = ("张三", "dept1"),
        ["userA"] = ("孙倩", "dept1"),
        ["userB"] = ("周明", "dept1"),
        ["userC"] = ("吴婷", "dept1"),
        ["leader"] = ("李四", "dept1"),
        ["manager"] = ("王五", "dept2"),
        ["director"] = ("赵六", "dept2"),
        ["boss"] = ("钱七", "dept3"),
        ["applicant"] = ("发起人", "dept1"),
    };

    public static ServiceContext NewContext(MemoryRepository repo, bool withUserProvider = true)
    {
        var ctx = new ServiceContext(repo);
        ctx.Clock = new FixedClock(new DateTime(2026, 8, 1, 9, 0, 0));
        ctx.IdGenerator = new AtomicIdGenerator(1, ctx.Clock);
        if (withUserProvider)
        {
            ctx.UserProvider = new TestUserProvider();
        }
        RegisterBuiltins(ctx);
        return ctx;
    }

    /// <summary>注册 7 内置 assignmentHandler（键=Java FQCN，C29）+ custom 测试 handler。</summary>
    public static void RegisterBuiltins(ServiceContext ctx)
    {
        ctx.RegisterAssignmentHandler(
            "com.mldong.jeeflow.interceptor.impl.OperatorAssignmentHandler",
            new BuiltinAssignmentHandlers.OperatorAssignmentHandler());
        ctx.RegisterAssignmentHandler(
            "com.mldong.jeeflow.interceptor.impl.FormFieldAssigneeHandler",
            new BuiltinAssignmentHandlers.FormFieldAssigneeHandler());
        ctx.RegisterAssignmentHandler(
            "com.mldong.jeeflow.interceptor.impl.DeptLeaderAssignmentHandler",
            new BuiltinAssignmentHandlers.DeptLeaderAssignmentHandler());
        ctx.RegisterAssignmentHandler(
            "com.mldong.jeeflow.interceptor.impl.DeptMainLeaderAssignmentHandler",
            new BuiltinAssignmentHandlers.DeptMainLeaderAssignmentHandler());
        ctx.RegisterAssignmentHandler(
            "com.mldong.jeeflow.interceptor.impl.ApplicantDeptLeaderAssignmentHandler",
            new BuiltinAssignmentHandlers.ApplicantDeptLeaderAssignmentHandler());
        ctx.RegisterAssignmentHandler(
            "com.mldong.jeeflow.interceptor.impl.ApplicantDeptMainLeaderAssignmentHandler",
            new BuiltinAssignmentHandlers.ApplicantDeptMainLeaderAssignmentHandler());
        ctx.RegisterAssignmentHandler(
            "com.mldong.jeeflow.interceptor.impl.TaskRoleAssigneeHandler",
            new BuiltinAssignmentHandlers.TaskRoleAssigneeHandler());
        // custom 节点 handler（等价 Java 测试 classpath：flows/08-custom-node 声明的 clazz）
        ctx.CustomHandlers["com.mldong.jeeflow.test.TestCustomHandler"] = new TestCustomHandler();
    }

    public static (JeeflowEngine Engine, MemoryRepository Repo) NewEngine(bool withUserProvider = true)
    {
        var repo = new MemoryRepository();
        var ctx = NewContext(repo, withUserProvider);
        repo.Configure(ctx);
        return (new JeeflowEngine(ctx), repo);
    }

    public static (JeeflowEngine Engine, MemoryRepository Repo, ServiceContext Ctx) NewEngineWithCtx(
        bool withUserProvider = true)
    {
        var repo = new MemoryRepository();
        var ctx = NewContext(repo, withUserProvider);
        repo.Configure(ctx);
        return (new JeeflowEngine(ctx), repo, ctx);
    }

    /// <summary>加载共享 flows 副本（tests 输出目录 flows/）。</summary>
    public static string LoadFlow(string name)
    {
        foreach (var dir in new[] { "flows", "../flows", "../../flows", "../../../flows" })
        {
            var path = Path.Combine(dir, name + ".json");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException($"flows dir not found for {name}");
    }

    public static Dictionary<string, object?> ParseFlowJson(string json) =>
        (Dictionary<string, object?>?)DefaultJsonProvider.Instance.FromJson(json)
            ?? throw new InvalidOperationException("bad flow json");

    /// <summary>存流程定义（state=1, version=1）。</summary>
    public static async Task<long> SaveFlowDefineAsync(
        MemoryRepository repo, string name, string contentJson)
    {
        var define = new ProcessDefine
        {
            Name = name,
            DisplayName = name,
            Type = "approval",
            State = 1,
            Content = System.Text.Encoding.UTF8.GetBytes(contentJson),
            Version = 1,
            UpdateUser = "tester",
        };
        await repo.SaveDefineAsync(define);
        return define.Id!.Value;
    }

    /// <summary>发起并自动完成申请节点（operator=applicant）。</summary>
    public static async Task<long> StartAndApplyAsync(
        JeeflowEngine engine, MemoryRepository repo, long defineId, FlowData? args = null)
    {
        var inst = await engine.StartProcessInstanceByIdAsync(defineId, "applicant", args ?? new FlowData());
        var tasks = await repo.FindDoingTasksAsync(inst.InstanceId!.Value, null);
        foreach (var t in tasks)
        {
            if (t.ActorIds.Contains("applicant"))
            {
                await engine.ExecuteProcessTaskAsync(t.TaskId!.Value, "applicant", new FlowData());
                break;
            }
        }
        return inst.InstanceId.Value;
    }

    /// <summary>找指定参与人的 DOING 任务。</summary>
    public static async Task<ProcessTask> FindDoingForAsync(
        MemoryRepository repo, long instanceId, string actor)
    {
        var tasks = await repo.FindDoingTasksAsync(instanceId, null);
        foreach (var t in tasks)
            if (t.ActorIds.Contains(actor))
                return t;
        throw new InvalidOperationException($"no doing task for {actor} in {instanceId}");
    }

    public static long SaveFlowDefine(MemoryRepository repo, string name, string contentJson) =>
        SaveFlowDefineAsync(repo, name, contentJson).GetAwaiter().GetResult();

    public static long StartAndApply(JeeflowEngine engine, MemoryRepository repo, long defineId) =>
        StartAndApplyAsync(engine, repo, defineId).GetAwaiter().GetResult();

    public static ProcessTask FindDoingFor(MemoryRepository repo, long instanceId, string actor) =>
        FindDoingForAsync(repo, instanceId, actor).GetAwaiter().GetResult();
}

/// <summary>合规测试用户 provider：realName=具名用户表，dept/post 固定。</summary>
public class TestUserProvider : IUserProvider
{
    public Task<IUserProvider.UserInfo?> GetUserAsync(string userId)
    {
        if (TestInfra.NamedUsers.TryGetValue(userId, out var u))
        {
            return Task.FromResult<IUserProvider.UserInfo?>(new IUserProvider.UserInfo
            {
                UserId = userId,
                RealName = u.RealName,
                DeptId = u.DeptId,
                DeptName = "TestDept",
                PostId = "post1",
                PostName = "TestPost",
            });
        }
        return Task.FromResult<IUserProvider.UserInfo?>(new IUserProvider.UserInfo { UserId = userId, RealName = userId });
    }
}

/// <summary>custom 节点测试 handler（等价 Java TestCustomHandler）。</summary>
public class TestCustomHandler : IHandler
{
    public Task HandleAsync(Execution execution)
    {
        execution.Args[FlowConst.CustomReturnVal] = "customExecuted";
        execution.ProcessInstance!.AddVariable(new FlowData { ["customNodeRan"] = true });
        return Task.CompletedTask;
    }
}

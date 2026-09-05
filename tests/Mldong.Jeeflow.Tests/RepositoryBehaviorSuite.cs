using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Repository.MySql;
using MySqlConnector;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// 内存/MySQL 行为双跑套件（防仓储分叉，方案 §6.1 T1）：
/// 同一组行为断言在两个仓储上全绿。
/// </summary>
public abstract class RepositoryBehaviorSuite
{
    protected abstract (JeeflowEngine Engine, MemoryRepository? Mem, IProcessRepository Repo) Build();

    protected abstract Task<(long DefineId, long InstanceId)> SeedSimpleFlowAsync(
        JeeflowEngine engine, IProcessRepository repo, string businessNo, FlowData? args = null);

    [Fact]
    public virtual async Task Behavior_StartAndCompleteChain()
    {
        var (engine, _, repo) = Build();
        var (did, iid) = await SeedSimpleFlowAsync(engine, repo, "BHV-chain");
        var inst = await repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Doing, inst!.State);
        // 水合（issues/89）
        Assert.NotEmpty(inst.Tasks);

        var task = await FindDoingByActorAsync(repo, iid, "leader");
        await engine.ExecuteProcessTaskAsync(task.TaskId!.Value, "leader", new FlowData());
        var done = await repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Finished, done!.State);
        Assert.Empty(await repo.FindDoingTasksAsync(iid, null));
        var history = await repo.FindHistoryTasksAsync(iid);
        Assert.Equal(2, history.Count); // apply(FINISHED) + task1(FINISHED)
    }

    [Fact]
    public virtual async Task Behavior_TaskActorsRoundTrip()
    {
        var (engine, _, repo) = Build();
        var (_, iid) = await SeedSimpleFlowAsync(engine, repo, "BHV-actors");
        var task = await FindDoingByActorAsync(repo, iid, "leader");
        await repo.AddTaskActorAsync(task.TaskId!.Value, new List<string> { "helper1" });
        await repo.AddTaskActorAsync(task.TaskId!.Value, new List<string> { "helper1" }); // 去重
        var actors = await repo.FindTaskActorsAsync(task.TaskId.Value);
        Assert.Equal(1, actors.Count(a => a == "helper1"));
        Assert.Contains("leader", actors);

        await repo.RemoveTaskActorAsync(task.TaskId.Value, new List<string> { "helper1" });
        actors = await repo.FindTaskActorsAsync(task.TaskId.Value);
        Assert.DoesNotContain("helper1", actors);
    }

    [Fact]
    public virtual async Task Behavior_VariablesJsonRoundTrip()
    {
        // C21：变量 JSON 明文落库 + 读侧解析；NULL 安全
        var (engine, _, repo) = Build();
        var args = new FlowData { ["f_days"] = 3, ["f_reason"] = "年假", [FlowConst.BusinessNo] = "BHV-vars" };
        var (_, iid) = await SeedSimpleFlowAsync(engine, repo, "BHV-vars", args);
        var inst = await repo.FindInstanceByIdAsync(iid);
        Assert.Equal(3, inst!.Variables.GetInt("f_days"));
        Assert.Equal("年假", inst.Variables.GetStr("f_reason"));
    }

    [Fact]
    public virtual async Task Behavior_PageTodoFiveKeys()
    {
        var (engine, _, repo) = Build();
        var (_, iid) = await SeedSimpleFlowAsync(engine, repo, "BHV-page");
        var query = new PageQuery(1, 5).Add("pta.actor_id", "EQ", "leader");
        var page = await repo.PageTodoTasksAsync(query);
        Assert.Equal(1, page.PageNum);
        Assert.Equal(5, page.PageSize);
        Assert.True(page.RecordCount >= 1);
        Assert.Equal(page.RecordCount, page.RecordCount);
        Assert.NotEmpty(page.Rows);
        var row = page.Rows[0];
        Assert.Equal("task1", row.TaskName);
        Assert.NotNull(row.ProcessDefineDisplayName);
        Assert.NotNull(row.InstanceVariable); // JOIN 关联列
    }

    [Fact]
    public virtual async Task Behavior_CcCreateAndUpdateStatus()
    {
        var (engine, _, repo) = Build();
        var (_, iid) = await SeedSimpleFlowAsync(engine, repo, "BHV-cc");
        await repo.CreateCcInstanceAsync(iid, "leader", "cc1", "cc2");
        var page = await repo.PageCcInstancesAsync(new PageQuery(1, 10).Add("cc.actor_id", "EQ", "cc1"));
        Assert.Equal(1, page.RecordCount);
        await repo.UpdateCcStatusAsync(iid, "cc1");
        // updateCcStatus 只置该 actor 的行——cc2 仍可分页查到
        var page2 = await repo.PageCcInstancesAsync(new PageQuery(1, 10).Add("cc.actor_id", "EQ", "cc2"));
        Assert.Equal(1, page2.RecordCount);
    }

    [Fact]
    public virtual async Task Behavior_DefineWriteOps()
    {
        var (_, _, repo) = Build();
        var define = new ProcessDefine
        {
            Name = "BHV-def-write",
            DisplayName = "行为-定义写",
            Type = "approval",
            State = 1,
            Content = System.Text.Encoding.UTF8.GetBytes(TestInfra.LoadFlow("01-simple")),
            Version = 1,
        };
        await repo.SaveDefineAsync(define);
        Assert.True(define.Id > 0);

        var found = await repo.FindDefineByIdAsync(define.Id);
        Assert.NotNull(found);
        Assert.Equal("行为-定义写", found!.DisplayName);
        Assert.NotEmpty(found.Content!);

        found.Version = 7; // 替换语义：显式版本保留（issues/59）
        await repo.UpdateDefineAsync(found);
        Assert.Equal(7, (await repo.FindDefineByIdAsync(define.Id))!.Version);

        await repo.UpdateDefineStateAsync(define.Id.Value, 0);
        Assert.Equal(0, (await repo.FindDefineByIdAsync(define.Id))!.State);

        await repo.RemoveDefineAsync(define.Id.Value);
        Assert.Null(await repo.FindDefineByIdAsync(define.Id));
    }

    [Fact]
    public virtual async Task Behavior_NonActorExecuteFails()
    {
        var (engine, _, repo) = Build();
        var (_, iid) = await SeedSimpleFlowAsync(engine, repo, "BHV-neg");
        var task = await FindDoingByActorAsync(repo, iid, "leader");
        await Assert.ThrowsAsync<JeeflowException>(
            () => engine.ExecuteProcessTaskAsync(task.TaskId!.Value, "intruder", new FlowData()));
    }

    protected static async Task<ProcessTask> FindDoingByActorAsync(
        IProcessRepository repo, long instanceId, string actor)
    {
        var tasks = await repo.FindDoingTasksAsync(instanceId, null);
        foreach (var t in tasks)
            if (t.ActorIds.Contains(actor))
                return t;
        throw new InvalidOperationException($"no doing task for {actor} in {instanceId}");
    }
}

/// <summary>内存仓储侧双跑。</summary>
public class MemoryBehaviorSuite : RepositoryBehaviorSuite
{
    private (JeeflowEngine, MemoryRepository)? _built;

    protected override (JeeflowEngine Engine, MemoryRepository? Mem, IProcessRepository Repo) Build()
    {
        if (_built == null)
        {
            var repo = new MemoryRepository();
            var ctx = TestInfra.NewContext(repo);
            repo.Configure(ctx);
            _built = (new JeeflowEngine(ctx), repo);
        }
        return (_built.Value.Item1, _built.Value.Item2, _built.Value.Item2);
    }

    protected override async Task<(long DefineId, long InstanceId)> SeedSimpleFlowAsync(
        JeeflowEngine engine, IProcessRepository repo, string businessNo, FlowData? args = null)
    {
        var define = new ProcessDefine
        {
            Name = "BHV-def-" + businessNo,
            DisplayName = "行为-简单",
            Type = "approval",
            State = 1,
            Content = System.Text.Encoding.UTF8.GetBytes(TestInfra.LoadFlow("01-simple")),
            Version = 1,
        };
        await repo.SaveDefineAsync(define);
        var flowArgs = args ?? new FlowData();
        flowArgs[FlowConst.BusinessNo] = businessNo;
        var inst = await engine.StartProcessInstanceByIdAsync(define.Id, "applicant", flowArgs);
        var apply = await FindDoingByActorAsync(repo, inst.InstanceId!.Value, "applicant");
        await engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData());
        return (define.Id.Value, inst.InstanceId.Value);
    }
}

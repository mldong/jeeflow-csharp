using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;

// issues/103 §8 一致性驱动（C# 侧）——固定数据集：2 流程（leave/expense）/ 6 实例（含 1 个 99）/
// 5 任务，全部落 2026-08-01/02。数据集语义以 java.json 快照反推 + JeeflowStatsTest.seed() 为基准。
// 运行：dotnet run --project demo/Mldong.Jeeflow.Consistency -- <输出文件路径>
// 输出：{label:{code,msg,data}} 单行 JSON（对齐 jeeflow-hub/consistency/*.json 格式）。

var repo = new MemoryRepository();
var ctx = new ServiceContext(repo);
ctx.Clock = new FixedClock(new DateTime(2026, 8, 10, 12, 0, 0)); // 钟在数据集之外（快照口径 todayNew=0 不依赖数据日）
ctx.UserProvider = new StatsUserProvider();
repo.Configure(ctx);

Seed(repo);

var facade = new JeeflowFacade(ctx);
var output = new Dictionary<string, object?>();

// overview ×2
Call(output, "overview", () => facade.FlowAsync("processInstance/stats/overview", new FlowData()).GetAwaiter().GetResult());
Call(output, "overview_stateIn10", () => facade.FlowAsync("processInstance/stats/overview",
    Args(new() { ["stateIn"] = new List<object?> { 10 } })).GetAwaiter().GetResult());
// trend ×4
Call(output, "trend_day", () => facade.FlowAsync("processInstance/stats/trend",
    TrendArgs("2026-08-01 00:00:00", "2026-08-02 23:59:59", "day")).GetAwaiter().GetResult());
Call(output, "trend_hour", () => facade.FlowAsync("processInstance/stats/trend",
    TrendArgs("2026-08-01 10:00:00", "2026-08-01 12:00:00", "hour")).GetAwaiter().GetResult());
Call(output, "trend_week", () => facade.FlowAsync("processInstance/stats/trend",
    TrendArgs("2026-08-01 00:00:00", "2026-08-02 23:59:59", "week")).GetAwaiter().GetResult());
Call(output, "trend_month", () => facade.FlowAsync("processInstance/stats/trend",
    TrendArgs("2026-07-01 00:00:00", "2026-08-02 23:59:59", "month")).GetAwaiter().GetResult());
// group ×9
foreach (var dim in new[] { "state", "define", "category", "approver", "applicant", "node", "stuckNode", "stuckApprover", "durationBucket" })
{
    var dim1 = dim;
    Call(output, $"group_{dim1}", () => facade.FlowAsync("processInstance/stats/group",
        Args(new() { ["dimension"] = dim1 })).GetAwaiter().GetResult());
}

var json = Outbound.ToJson(output);
var outPath = args.Length > 0 ? args[0] : "csharp.json";
File.WriteAllText(outPath, json);
Console.WriteLine($"written: {Path.GetFullPath(outPath)}");

// ── helpers ──

static FlowData Args(Dictionary<string, object?> dict)
{
    var a = new FlowData();
    foreach (var kv in dict) a[kv.Key] = kv.Value;
    return a;
}

static FlowData TrendArgs(string start, string end, string granularity) => Args(new()
{
    ["start"] = start,
    ["end"] = end,
    ["granularity"] = granularity,
});

static void Call(Dictionary<string, object?> output, string label, Func<Dictionary<string, object?>> call)
{
    try
    {
        output[label] = call();
    }
    catch (Exception e)
    {
        output[label] = new Dictionary<string, object?>
        {
            ["code"] = 99999999,
            ["msg"] = e.Message,
            ["data"] = null,
        };
    }
}

static void Seed(MemoryRepository repo)
{
    // 2 流程
    repo.SaveDefineAsync(new ProcessDefine
    {
        Id = 1, Name = "leave", DisplayName = "请假流程", Type = "approval", State = 1,
        Content = Array.Empty<byte>(), Version = 1,
        CreateTime = new DateTime(2026, 8, 1, 9, 0, 0), CreateUser = "system",
    }).GetAwaiter().GetResult();
    repo.SaveDefineAsync(new ProcessDefine
    {
        Id = 2, Name = "expense", DisplayName = "报销流程", Type = "finance", State = 1,
        Content = Array.Empty<byte>(), Version = 1,
        CreateTime = new DateTime(2026, 8, 1, 9, 0, 0), CreateUser = "system",
    }).GetAwaiter().GetResult();

    Inst(repo, 100, 1, 10, "u1", "2026-08-01 10:00:00");
    Inst(repo, 101, 1, 20, "u1", "2026-08-01 12:00:00");
    Inst(repo, 102, 2, 20, "u2", "2026-08-02 09:00:00");
    Inst(repo, 103, 2, 30, "u2", "2026-08-02 11:00:00");
    Inst(repo, 104, 1, 45, "u1", "2026-08-02 15:00:00");
    Inst(repo, 105, 1, 99, "u3", "2026-08-02 16:00:00");

    var t200 = SeedTask(repo, 200, 100, 10, "部门经理审批", "u9", "2026-08-01 10:05:00", null, null, 0);
    var t201 = SeedTask(repo, 201, 100, 10, "人事确认", "u9", "2026-08-01 10:06:00", null, null, 1);
    t201.ActorIds = new List<string> { "u9", "u10" };
    var t202 = SeedTask(repo, 202, 101, 20, "部门经理审批", "u5", "2026-08-01 12:10:00",
        "2026-08-01 18:00:00", "2026-08-02 12:00:00", 0);
    var t203 = SeedTask(repo, 203, 102, 20, "财务审批", "u6", "2026-08-02 09:10:00",
        "2026-08-02 18:00:00", "2026-08-02 12:00:00", 0);
    var t204 = SeedTask(repo, 204, 105, 20, "部门经理审批", "u5", "2026-08-02 16:10:00",
        "2026-08-02 17:00:00", "2026-08-02 17:30:00", 0);
    repo.AddTaskActorAsync(201, new List<string> { "u9", "u10" }).GetAwaiter().GetResult();
    _ = new[] { t200, t201, t202, t203, t204 };
}

static void Inst(MemoryRepository repo, long id, long defineId, int state, string op, string createTime)
{
    var inst = new ProcessInstance
    {
        InstanceId = id,
        DefineId = defineId,
        State = state,
        Operator = op,
        CreateTime = DateTime.Parse(createTime),
        CreateUser = op,
    };
    repo.SaveInstanceAsync(inst).GetAwaiter().GetResult();
}

static ProcessTask SeedTask(MemoryRepository repo, long id, long instId, int state, string display,
    string actor, string createTime, string? finish, string? expire, int perform)
{
    var task = new ProcessTask
    {
        TaskId = id,
        ProcessInstanceId = instId,
        TaskName = display,
        DisplayName = display,
        TaskType = WfTaskType.Major,
        PerformType = (WfPerformType)perform,
        TaskState = state,
        ActorId = actor,
        ActorIds = new List<string> { actor },
        FinishTime = finish == null ? null : DateTime.Parse(finish),
        ExpireTime = expire == null ? null : DateTime.Parse(expire),
        CreateTime = DateTime.Parse(createTime),
    };
    repo.SaveTaskAsync(task).GetAwaiter().GetResult();
    return task;
}

/// <summary>快照用户 provider（realName=用户{uid}）。</summary>
internal class StatsUserProvider : IUserProvider
{
    public Task<IUserProvider.UserInfo?> GetUserAsync(string userId) =>
        Task.FromResult<IUserProvider.UserInfo?>(new IUserProvider.UserInfo
        {
            UserId = userId,
            RealName = $"用户{userId}",
        });
}

using Xunit;
using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Tests.Spike;

/// <summary>
/// M0 spike ①⑤：dotnet test async 用例绿 + 雪花 id/出口递归 stringifier 三态审计。
/// </summary>
public class SpikeTests
{
    [Fact]
    public async Task AsyncTestRunsGreen()
    {
        await Task.Delay(1);
        var sum = await Task.FromResult(1 + 1);
        Assert.Equal(2, sum);
    }

    // ── spike ⑤：雪花 id ──

    [Fact]
    public void SnowflakeIdGeneratesIncreasingUniqueIds()
    {
        var clock = new FixedClock(new DateTime(2026, 9, 6, 12, 0, 0));
        var gen = new AtomicIdGenerator(1, clock);
        var ids = new HashSet<long>();
        for (var i = 0; i < 1000; i++)
        {
            var id = gen.NextId();
            Assert.True(ids.Add(id), $"duplicate id {id}");
            Assert.True(id > 0);
        }
    }

    [Fact]
    public void SnowflakeIdUsesTwitterEpoch()
    {
        var clock = new FixedClock(new DateTime(2026, 9, 6, 12, 0, 0));
        var gen = new AtomicIdGenerator(0, clock);
        var id = gen.NextId();
        // 反解时间戳：id >> 22 + EPOCH ≈ 2026-09-06
        var ts = (id >> 22) + AtomicIdGenerator.TwitterEpoch;
        var dt = DateTime.UnixEpoch.AddMilliseconds(ts);
        Assert.Equal(2026, dt.Year);
    }

    // ── spike ⑤：出口递归 stringifier 三态审计（CS2：>2^53 三态全 string）──

    /// <summary>2^53+1（float64 丢精度边界）。</summary>
    private const long BigId = 9007199254740993L;

    [Fact]
    public void OutboundStringifiesSingleBigId()
    {
        var data = new Dictionary<string, object?> { ["id"] = BigId };
        var json = Outbound.ToJson(data);
        Assert.Contains($"\"id\":\"{BigId}\"", json);
        Assert.DoesNotContain($"\"id\":{BigId}", json);
    }

    [Fact]
    public void OutboundStringifiesPluralIdArrayElements()
    {
        var data = new Dictionary<string, object?>
        {
            ["taskIds"] = new List<object?> { BigId, 123L },
            ["ids"] = new List<object?> { BigId },
        };
        var json = Outbound.ToJson(data);
        Assert.Contains($"\"taskIds\":[\"{BigId}\",\"123\"]", json);
        Assert.Contains($"\"ids\":[\"{BigId}\"]", json);
    }

    [Fact]
    public void OutboundStringifiesNestedRowsWithoutHarmingObjects()
    {
        var data = new Dictionary<string, object?>
        {
            ["rows"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["id"] = BigId,
                    ["processInstanceId"] = BigId,
                    ["creator_id"] = BigId,
                    ["count"] = 42,          // stats 计数字段：int 出参非 string（issues/105）
                    ["createTime"] = new DateTime(2026, 8, 1, 9, 30, 0),
                },
            },
        };
        var json = Outbound.ToJson(data);
        Assert.Contains($"\"id\":\"{BigId}\"", json);
        Assert.Contains($"\"processInstanceId\":\"{BigId}\"", json);
        Assert.Contains($"\"creator_id\":\"{BigId}\"", json);
        Assert.Contains("\"count\":42", json);
        Assert.Contains("\"createTime\":\"2026-08-01 09:30:00\"", json);
        Assert.DoesNotContain($"\"count\":\"42\"", json);
    }

    [Fact]
    public void OutboundKeepsNullIdAsNull()
    {
        var data = new Dictionary<string, object?> { ["id"] = null!, ["parentId"] = null! };
        var json = Outbound.ToJson(data);
        Assert.Contains("\"id\":null", json);
        Assert.Contains("\"parentId\":null", json);
    }

    [Fact]
    public void OutboundHasNoNumberAbovePow53()
    {
        var data = new Dictionary<string, object?>
        {
            ["id"] = BigId,
            ["rows"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["processDefineId"] = BigId,
                    ["assigneeIds"] = new List<object?> { BigId, BigId + 1 },
                },
            },
        };
        var json = Outbound.ToJson(data);
        // 出口无 >2^53 number：全部大数以字符串出现
        Assert.DoesNotContain(BigId.ToString() + ",", json.Replace($"\"{BigId}\"", "SAFE"));
        Assert.Contains($"\"{BigId}\"", json);
    }

    // ── 引擎骨架可实例化（spike ④ 编译面）──

    [Fact]
    public void EngineSkeletonCompilesAndWires()
    {
        var ctx = new ServiceContext(new TestRepo());
        var engine = new JeeflowEngine(ctx);
        Assert.NotNull(engine);
        Assert.IsAssignableFrom<IProcessRepository>(ctx.Repository);
        Assert.Null(ctx.PermissionCodes("processInstance/stats/overview")); // 放行清单
        Assert.Equal(new[] { "wf:processDefine:page" }, ctx.PermissionCodes("processDefine/page"));
    }

    [Fact]
    public void EnumDictRegistryHas7Keys()
    {
        var reg = new EnumDictRegistry();
        Assert.Equal(7, reg.ListDictKeys().Count);
        Assert.Equal("10", reg.GetDict("wf_process_instance_state")[0].Value);
        Assert.Equal("进行中", reg.GetDict("wf_process_instance_state")[0].Label);
        Assert.Empty(reg.GetDict("unknown_key"));
    }

    private sealed class TestRepo : IProcessRepository
    {
        public Task<ProcessDefine?> FindDefineByIdAsync(long? defineId) => Task.FromResult<ProcessDefine?>(null);
        public Task SaveDefineAsync(ProcessDefine define) => Task.CompletedTask;
        public Task UpdateDefineAsync(ProcessDefine define) => Task.CompletedTask;
        public Task UpdateDefineStateAsync(long defineId, int state) => Task.CompletedTask;
        public Task RemoveDefineAsync(long defineId) => Task.CompletedTask;
        public Task<ProcessInstance?> FindInstanceByIdAsync(long? instanceId) => Task.FromResult<ProcessInstance?>(null);
        public Task SaveInstanceAsync(ProcessInstance instance) => Task.CompletedTask;
        public Task UpdateInstanceAsync(ProcessInstance instance) => Task.CompletedTask;
        public Task<ProcessTask?> FindTaskByIdAsync(long? taskId) => Task.FromResult<ProcessTask?>(null);
        public Task SaveTaskAsync(ProcessTask task) => Task.CompletedTask;
        public Task UpdateTaskAsync(ProcessTask task) => Task.CompletedTask;
        public Task<List<ProcessTask>> FindDoingTasksAsync(long instanceId, string[]? taskNames) => Task.FromResult(new List<ProcessTask>());
        public Task<List<ProcessTask>> FindDoneTasksAsync(long instanceId, string[]? taskNames) => Task.FromResult(new List<ProcessTask>());
        public Task<List<ProcessTask>> FindHistoryTasksAsync(long instanceId) => Task.FromResult(new List<ProcessTask>());
        public Task CreateCcInstanceAsync(long instanceId, string creator, params string[] actorIds) => Task.CompletedTask;
        public Task UpdateCcStatusAsync(long instanceId, string actorId) => Task.CompletedTask;
        public Task<List<string>> FindTaskActorsAsync(long taskId) => Task.FromResult(new List<string>());
        public Task AddTaskActorAsync(long taskId, List<string> actors) => Task.CompletedTask;
        public Task RemoveTaskActorAsync(long taskId, List<string> actors) => Task.CompletedTask;
        public Task<PageResult<IProcessRepository.TaskRow>> PageTodoTasksAsync(PageQuery query) =>
            Task.FromResult(PageResult<IProcessRepository.TaskRow>.Of(1, 10, 0, new List<IProcessRepository.TaskRow>()));
        public Task<PageResult<IProcessRepository.TaskRow>> PageDoneTasksAsync(PageQuery query) =>
            Task.FromResult(PageResult<IProcessRepository.TaskRow>.Of(1, 10, 0, new List<IProcessRepository.TaskRow>()));
        public Task<PageResult<IProcessRepository.InstanceRow>> PageInstancesAsync(PageQuery query) =>
            Task.FromResult(PageResult<IProcessRepository.InstanceRow>.Of(1, 10, 0, new List<IProcessRepository.InstanceRow>()));
        public Task<PageResult<IProcessRepository.InstanceRow>> PageCcInstancesAsync(PageQuery query) =>
            Task.FromResult(PageResult<IProcessRepository.InstanceRow>.Of(1, 10, 0, new List<IProcessRepository.InstanceRow>()));
        public Task<PageResult<IProcessRepository.DefineRow>> PageDefinesAsync(PageQuery query) =>
            Task.FromResult(PageResult<IProcessRepository.DefineRow>.Of(1, 10, 0, new List<IProcessRepository.DefineRow>()));
        public Task<int> CountTodoTasksAsync(long? userId) => Task.FromResult(0);
        public Task<List<IProcessRepository.InstanceStatsRow>> QueryInstancesForStatsAsync(
            List<int>? stateIn, string timeField, DateTime? start, DateTime? end) =>
            Task.FromResult(new List<IProcessRepository.InstanceStatsRow>());
        public Task<List<IProcessRepository.TaskStatsRow>> QueryTasksForStatsAsync(
            int? state, DateTime? start, DateTime? end) =>
            Task.FromResult(new List<IProcessRepository.TaskStatsRow>());
        public Task<int> StatsAvgCompletedDurationSecondsAsync(DateTime? start, DateTime? end) => Task.FromResult(0);
        public Task<int[]> StatsPendingAndOverdueCountAsync() => Task.FromResult(new[] { 0, 0 });
        public Task<int[]> StatsCompletedTaskAggregateAsync() => Task.FromResult(new[] { 0, 0, 0, 0 });
        public Task<List<Dictionary<string, object?>>> StatsStuckNodeGroupAsync(int limit) =>
            Task.FromResult(new List<Dictionary<string, object?>>());
        public Task<List<Dictionary<string, object?>>> StatsStuckApproverGroupAsync(int limit) =>
            Task.FromResult(new List<Dictionary<string, object?>>());
        public Task<List<Dictionary<string, object?>>> StatsDefineGroupAsync(DateTime? start, DateTime? end, int limit) =>
            Task.FromResult(new List<Dictionary<string, object?>>());
        public Task<List<int>> StatsCompletedInstanceDurationsAsync(DateTime? start, DateTime? end) =>
            Task.FromResult(new List<int>());
    }
}

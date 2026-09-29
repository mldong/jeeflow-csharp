using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Repository.MySql;
using MySqlConnector;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// 记录类（<c>snaker:custom</c>）历史行的 <b>MySQL 真库一路</b>（issues/142 · spec 02 §6.2 第 1/2 条 · C# 栈）。
///
/// <para>内存一路见 <see cref="CustomHistoryRow142Tests"/>。本文件只补一件内存档照不到的事：
/// <b>那条 FINISHED(20) 的行是否真能过 <c>wf_process_task</c> 的表约束插进去</b>——
/// 旧形状（没有 INSERT 腿）在真库里从没插过这一行，所以"能落内存"不等于"能落库"：
/// <c>display_name NOT NULL</c>／<c>task_parent_id</c>／<c>variable</c>／
/// <c>expire_time</c> 这些列在 DONE 行上的形状，必须由真库读数来判
/// （两仓同读数＝issues/117 场景 27 那把尺子，spec 02 §6.2 第 1 条「只在聚合内存里 append 一条不算做到」）。</para>
///
/// <para>连不上 160 算 fail 不算 skip（本仓 R6 发版机口径）；数据用 <c>T1CS-custom142-</c> 前缀的
/// business_no 标记、define 用 930000-939999 段，进出各清一次，不碰他语言数据。</para>
/// </summary>
[Collection("mysql")]
[Trait("Category", "mysql-smoke")]
public class MySqlCustomHistoryRow142Tests : IAsyncLifetime
{
    private const string MarkerPrefix = "T1CS-custom142-";
    private const string TestClazz = "com.mldong.jeeflow.test.TestCustomHandler";

    private readonly MySqlConnectionFactory _factory;
    private readonly MySqlRepository _repo;
    private readonly JeeflowEngine _engine;
    private readonly ServiceContext _ctx;
    private readonly List<ProcessEvent> _taskStarts = new();

    /// <summary>本用例专属 define id 段（夹具的 T1 段是 910000-919999，这里避开）。
    /// 计数器静态：xUnit 每条用例 new 一个类实例，段内自增才不会撞主键（进出各清整段）。</summary>
    private static int _defineCounter;

    public MySqlCustomHistoryRow142Tests(MySqlFixture fx)
    {
        _factory = fx.Factory;
        var clock = new FixedClock(new DateTime(2026, 8, 1, 9, 0, 0));
        _repo = new MySqlRepository(_factory);
        _ctx = new ServiceContext(_repo)
        {
            Clock = clock,
            IdGenerator = new AtomicIdGenerator(142L, clock),   // 独立 worker 段，避开夹具/他语言的 id
            UserProvider = new TestUserProvider(),
        };
        _repo.Configure(_ctx);
        TestInfra.RegisterBuiltins(_ctx);                     // 含 custom 测试 handler（键=JVM 类名）
        _ctx.RegisterEventListener(new TaskStartCapture(_taskStarts));
        _engine = new JeeflowEngine(_ctx);
    }

    private sealed class TaskStartCapture : IProcessEventListener
    {
        private readonly List<ProcessEvent> _sink;
        public TaskStartCapture(List<ProcessEvent> sink) => _sink = sink;
        public Task OnEventAsync(ProcessEvent @event)
        {
            if (@event.EventType == ProcessEventType.ProcessTaskStart) _sink.Add(@event);
            return Task.CompletedTask;
        }
    }

    public async Task InitializeAsync() => await PurgeAsync();
    public async Task DisposeAsync() => await PurgeAsync();

    private async Task PurgeAsync()
    {
        await using var conn = await _factory.OpenAsync();
        foreach (var sql in new[]
        {
            "DELETE a FROM wf_process_task_actor a " +
            "  JOIN wf_process_task t ON t.id = a.process_task_id " +
            "  JOIN wf_process_instance i ON i.id = t.process_instance_id " +
            "  WHERE i.business_no LIKE 'T1CS-custom142-%'",
            "DELETE t FROM wf_process_task t " +
            "  JOIN wf_process_instance i ON i.id = t.process_instance_id " +
            "  WHERE i.business_no LIKE 'T1CS-custom142-%'",
            "DELETE cc FROM wf_process_cc_instance cc " +
            "  JOIN wf_process_instance i ON i.id = cc.process_instance_id " +
            "  WHERE i.business_no LIKE 'T1CS-custom142-%'",
            "DELETE FROM wf_process_instance WHERE business_no LIKE 'T1CS-custom142-%'",
            "DELETE FROM wf_process_define WHERE id BETWEEN 930000 AND 939999",
        })
        {
            await using var cmd = new MySqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ── 夹具辅助 ──

    /// <summary>存一条真库 define（930000 段），返回 define id。</summary>
    private async Task<long> SaveDefineAsync(string contentJson)
    {
        var defineId = 930000 + Interlocked.Increment(ref _defineCounter);
        var define = new ProcessDefine
        {
            Id = defineId,
            Name = MarkerPrefix + defineId,
            DisplayName = "142 记录类真库档",
            Type = "approval",
            State = 1,
            Content = System.Text.Encoding.UTF8.GetBytes(contentJson),
            Version = 1,
            UpdateUser = "tester",
        };
        await _repo.SaveDefineAsync(define);
        return defineId;
    }

    /// <summary>发起（带本用例专属 business_no 标记，便于清理）。</summary>
    private async Task<long> StartAsync(long defineId)
    {
        var marker = MarkerPrefix + "bn-" + defineId + "-" + Environment.TickCount;
        var inst = await _engine.StartProcessInstanceByIdAsync(defineId, "applicant",
            new FlowData { [FlowConst.BusinessNo] = marker });
        return inst.InstanceId!.Value;
    }

    /// <summary>读回某实例的 task 真实行（不经任何仓储转换，直接 SELECT）。</summary>
    private async Task<List<(long Id, string? TaskName, int State, long? ParentId,
        DateTime? ExpireTime, string? Variable, string DisplayName)>> TaskRowsAsync(long instanceId)
    {
        var rows = new List<(long, string?, int, long?, DateTime?, string?, string?)>();
        await using var conn = await _factory.OpenAsync();
        await using var cmd = new MySqlCommand(
            "SELECT id, task_name, task_state, task_parent_id, expire_time, variable, display_name " +
            "FROM wf_process_task WHERE process_instance_id = @i ORDER BY id", conn);
        cmd.Parameters.AddWithValue("@i", instanceId);
        await using var rs = await cmd.ExecuteReaderAsync();
        while (await rs.ReadAsync())
        {
            rows.Add((
                rs.GetInt64("id"),
                rs.IsDBNull(rs.GetOrdinal("task_name")) ? null : rs.GetString("task_name"),
                rs.GetInt32("task_state"),
                rs.IsDBNull(rs.GetOrdinal("task_parent_id")) ? null : rs.GetInt64("task_parent_id"),
                rs.IsDBNull(rs.GetOrdinal("expire_time")) ? null : rs.GetDateTime("expire_time"),
                rs.IsDBNull(rs.GetOrdinal("variable")) ? null : rs.GetString("variable"),
                rs.GetString("display_name")));
        }
        return rows;
    }

    private async Task<List<string>> ActorIdsAsync(long taskId)
    {
        var actors = new List<string>();
        await using var conn = await _factory.OpenAsync();
        await using var cmd = new MySqlCommand(
            "SELECT actor_id FROM wf_process_task_actor WHERE process_task_id = @t ORDER BY id", conn);
        cmd.Parameters.AddWithValue("@t", taskId);
        await using var rs = await cmd.ExecuteReaderAsync();
        while (await rs.ReadAsync()) actors.Add(rs.GetString("actor_id"));
        return actors;
    }

    private async Task<int> DoingCountAsync(long instanceId)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = new MySqlCommand(
            "SELECT COUNT(*) FROM wf_process_task WHERE process_instance_id = @i AND task_state = 10", conn);
        cmd.Parameters.AddWithValue("@i", instanceId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    // ═══ 判据①：那条 DONE 行真进了 wf_process_task ═══

    /// <summary>
    /// 共享夹具 <c>flows/08-custom-node.json</c>（八栈同一份定义）在真库上跑一遍：
    /// 办掉 apply 后 <c>wf_process_task</c> 里查得到 custom1 那一行，<c>task_state=20</c>、
    /// <c>task_parent_id</c>＝刚办结的 apply、<c>variable</c> 带行级首节点标记、
    /// <c>expire_time</c> 保持 NULL，参与者表只有 applicant。
    /// 改前会红：摘掉 INSERT 腿 ⇒ 这条 SELECT 一行都查不到（旧形状只 append 进聚合的内存 Tasks）。
    /// </summary>
    [Fact]
    public async Task CustomHistoryRowReallyLandsInWfProcessTask()
    {
        var did = await SaveDefineAsync(TestInfra.LoadFlow("08-custom-node"));
        var iid = await StartAsync(did);
        var apply = (await _repo.FindDoingTasksAsync(iid, null))
            .First(t => t.TaskName == "apply");

        await _engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData());

        var custom = Assert.Single(await TaskRowsAsync(iid), r => r.TaskName == "custom1");
        Assert.Equal(20, custom.State);                                  // 出生即已完成
        Assert.Equal("通知外部系统", custom.DisplayName);                     // display_name NOT NULL 过约束
        Assert.Equal(apply.TaskId!.Value, custom.ParentId!.Value);           // 建单不变量：parent 真落列
        Assert.Null(custom.ExpireTime);                                  // issues/126：没配 ⇒ 该列一动不动
        Assert.Contains("isFirstTaskNode", custom.Variable ?? "");        // 行级首节点标记随 variable 落库
        Assert.Contains("false", custom.Variable ?? "");                  // 在真库里也读出这个形状
        Assert.Equal(new List<string> { "applicant" }, await ActorIdsAsync(custom.Id));

        // 判据②：待办数不因它增加（这条流走完只剩 0 张待办，多出来的那一行是 20 不是 10）
        Assert.Equal(0, await DoingCountAsync(iid));
        Assert.Equal(2, (await TaskRowsAsync(iid)).Count);               // apply(20) ＋ custom1(20)
        Assert.Equal((int)WfInstanceState.Finished,
            (await _repo.FindInstanceByIdAsync(iid))!.State);            // 判据③：照样走到终点
    }

    /// <summary>
    /// 判据④：历史行没有为它 fire 码 3（真库一路）。发起腿为 apply fire 一次，办理腿为 custom1
    /// 那一行 fire 零次（<c>sourceId</c> 里查不到它的 id）。
    /// </summary>
    [Fact]
    public async Task NoTaskStartEventForCustomRowOnRealDb()
    {
        var did = await SaveDefineAsync(TestInfra.LoadFlow("08-custom-node"));
        var iid = await StartAsync(did);
        var apply = (await _repo.FindDoingTasksAsync(iid, null)).First(t => t.TaskName == "apply");

        _taskStarts.Clear();
        await _engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData());

        var custom = Assert.Single(await TaskRowsAsync(iid), r => r.TaskName == "custom1");
        Assert.NotEqual(0L, custom.Id);                                   // 先确认它真落了库
        Assert.DoesNotContain(custom.Id, _taskStarts.Select(e => e.SourceId!.Value));
        Assert.Empty(_taskStarts);                                        // 这一支一条都不该有（无新待办）
    }

    // ═══ 判据⑤：clazz 未注册在真库上同样"记日志 + 落行 + 继续" ═══

    /// <summary>
    /// 未注册档在真库的形状：不抛异常、实例走到终点、custom1 那一行照样插进 <c>wf_process_task</c>。
    /// 改前会红：还原成 <c>throw</c> 时发起/办理直接报错，库里连 apply 之后的行都没有。
    /// </summary>
    [Fact]
    public async Task UnregisteredClazzStillPersistsHistoryRowOnRealDb()
    {
        _ctx.CustomHandlers.Clear();                                      // 模拟 clazz 不可解析
        var warnings = new List<string>();
        _ctx.WarningSinkForTest = w => warnings.Add(w);                   // internal 取证钩子

        var did = await SaveDefineAsync(TestInfra.LoadFlow("08-custom-node"));
        var iid = await StartAsync(did);
        var apply = (await _repo.FindDoingTasksAsync(iid, null)).First(t => t.TaskName == "apply");
        await _engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData());

        var custom = Assert.Single(await TaskRowsAsync(iid), r => r.TaskName == "custom1");
        Assert.Equal(20, custom.State);
        Assert.Equal(0, await DoingCountAsync(iid));
        Assert.Equal((int)WfInstanceState.Finished, (await _repo.FindInstanceByIdAsync(iid))!.State);
        Assert.Single(warnings);
        Assert.Contains("未注册处理器", warnings[0]);
        Assert.Contains(TestClazz, warnings[0]);

        _ctx.WarningSinkForTest = null;
    }

    /// <summary>
    /// 两仓同读数（内存/SQL 同一把尺子）：真库读回的 <c>task_state</c> 与仓储 SPI 读回的一致，
    /// 且 <c>FindDoneTasksAsync</c>（doneOnly）能看到这一行、<c>FindDoingTasksAsync</c> 看不到。
    /// </summary>
    [Fact]
    public async Task RepositoryReadApisSeeTheRowAsDoneNotDoing()
    {
        var did = await SaveDefineAsync(TestInfra.LoadFlow("08-custom-node"));
        var iid = await StartAsync(did);
        var apply = (await _repo.FindDoingTasksAsync(iid, null)).First(t => t.TaskName == "apply");
        await _engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData());

        var done = await _repo.FindDoneTasksAsync(iid, null);
        var doneRow = Assert.Single(done, t => t.TaskName == "custom1");
        Assert.Equal((int)WfTaskState.Finished, doneRow.TaskState!.Value);
        Assert.DoesNotContain(doneRow.TaskId, (await _repo.FindDoingTasksAsync(iid, null))
            .Select(t => t.TaskId));                                     // 待办列表里没有它
        var raw = Assert.Single(await TaskRowsAsync(iid), r => r.TaskName == "custom1");
        Assert.Equal(raw.State, doneRow.TaskState!.Value);               // SPI 读数 == 真库读数
    }
}

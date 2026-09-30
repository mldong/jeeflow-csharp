using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;
using Mldong.Jeeflow.Repository.MySql;
using MySqlConnector;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// 任务参与者写侧归属值归一的 <b>MySQL 真库一路</b>（issues/142 B 批 · spec 06 §2.11 · C# 栈）。
///
/// <para>内存一路见 <see cref="ActorWriteNormalization142Tests"/>；本文件只补内存档照不到的事：
/// <b>归一后的值真进了 <c>wf_process_task_actor.actor_id</c>，而不是只在内存对象里干净</b>
/// （issues/142 A 批的前车：rust/moon 的 save_task INSERT 不带列，内存绿 ≠ 落库）。
/// 断言一律直接 SELECT 真实行。</para>
///
/// <para><b>两仓同一枚判据</b>（§2.11 写侧兜底 ＋ issues/117 场景 27 那把尺子）：同一份入参，
/// 内存仓与 SQL 仓必须给同一个答案——本栈普查实读是"两仓都判重不判空不 trim"
/// （<c>MySqlRepository.cs:450-457</c>／<c>MemoryRepository.cs:282-299</c>），
/// 判据单点只有 <c>PageQuery.NormalizeActors</c> 一枚，两仓各调它、不各抄一份。</para>
///
/// <para><b>主键另判一档</b>：<c>process_task_id</c> 缺失/0/负数不得静默插孤儿行，两仓一律抛
/// <see cref="JeeflowException"/>（文案不带内部码，issues/121 口径）。</para>
///
/// <para><b>驱动坑位备注</b>：本栈 MySqlConnector 连接串已由
/// <see cref="MySqlConnectionFactory"/> 钉死 <c>TreatTinyAsBoolean=False</c>（issues/123，
/// 见 <see cref="MySqlConnectionFactoryTests"/>）——本组判据读的都是 <c>actor_id VARCHAR(64)</c>，
/// 不碰 tinyint 列，故不受该折 bool 影响；若后续把判据扩到 <c>state</c>／<c>enabled</c> 一类
/// tinyint 列，别再关这个开关（脏值 2 会被读成 true→1，恒红那一格就是那次踩坑）。</para>
///
/// <para>连不上 160 算 fail 不算 skip（本仓 R6 发版机口径）；实例以 <c>T1CS-actor142-</c> 前缀的
/// business_no 标记、define 用 943000-943999 段、合成任务 id 用 94148xxxx 段，进出各清一次，
/// 不碰他语言数据。</para>
/// </summary>
[Collection("mysql")]
[Trait("Category", "mysql-smoke")]
public class MySqlActorWriteNormalization142Tests : IAsyncLifetime
{
    private const string MarkerPrefix = "T1CS-actor142-";
    private const long DefineSegmentLo = 943000;
    private const long DefineSegmentHi = 943999;
    private const long SyntheticTaskLo = 941480000;
    private const long SyntheticTaskHi = 941489999;

    /// <summary>start → apply(applicant) → approval(leader) → end。</summary>
    private const string TwoTaskFlow = """
        {"name": "actor-142-sql", "displayName": "归属值写侧归一真库档", "type": "approval",
         "nodes": [
           {"id": "start", "type": "snaker:start", "text": {"value": "开始"}},
           {"id": "apply", "type": "snaker:task", "text": {"value": "发起申请"},
            "properties": {"form": "apply-form", "assignee": "applicant", "taskType": 0, "performType": 0}},
           {"id": "approval", "type": "snaker:task", "text": {"value": "审批"},
            "properties": {"form": "leave-form", "assignee": "leader", "taskType": 0, "performType": 0}},
           {"id": "end", "type": "snaker:end", "text": {"value": "结束"}}
         ],
         "edges": [
           {"id": "e1", "sourceNodeId": "start", "targetNodeId": "apply"},
           {"id": "e2", "sourceNodeId": "apply", "targetNodeId": "approval"},
           {"id": "e3", "sourceNodeId": "approval", "targetNodeId": "end"}
         ]}
        """;

    private readonly MySqlConnectionFactory _factory;
    private readonly MySqlRepository _repo;
    private readonly JeeflowEngine _engine;
    private readonly JeeflowFacade _facade;
    private readonly MemoryRepository _mem = new();

    /// <summary>define 段内自增（xUnit 每条用例 new 一个类实例，静态计数才不撞主键）。</summary>
    private static int _defineCounter;

    public MySqlActorWriteNormalization142Tests(MySqlFixture fx)
    {
        _factory = fx.Factory;
        var clock = new FixedClock(new DateTime(2026, 8, 1, 9, 0, 0));
        _repo = new MySqlRepository(_factory);
        var ctx = new ServiceContext(_repo)
        {
            Clock = clock,
            IdGenerator = new AtomicIdGenerator(148L, clock),   // 独立 worker 段，避开夹具/他语言 id
            UserProvider = new TestUserProvider(),
        };
        _repo.Configure(ctx);
        _engine = new JeeflowEngine(ctx);
        _facade = new JeeflowFacade(ctx);

        var memClock = new FixedClock(new DateTime(2026, 8, 1, 9, 0, 0));
        var memCtx = new ServiceContext(_mem)
        {
            Clock = memClock,
            IdGenerator = new AtomicIdGenerator(148L, memClock),
        };
        _mem.Configure(memCtx);
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
            $"  WHERE i.business_no LIKE '{MarkerPrefix}%'",
            "DELETE t FROM wf_process_task t " +
            "  JOIN wf_process_instance i ON i.id = t.process_instance_id " +
            $"  WHERE i.business_no LIKE '{MarkerPrefix}%'",
            $"DELETE FROM wf_process_task_actor WHERE process_task_id BETWEEN {SyntheticTaskLo} AND {SyntheticTaskHi}",
            "DELETE FROM wf_process_instance WHERE business_no LIKE '" + MarkerPrefix + "%'",
            $"DELETE FROM wf_process_define WHERE id BETWEEN {DefineSegmentLo} AND {DefineSegmentHi}",
        })
        {
            await using var cmd = new MySqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ── 夹具与取证辅助 ──

    private async Task<long> SaveDefineAsync()
    {
        var defineId = DefineSegmentLo + Interlocked.Increment(ref _defineCounter);
        var define = new ProcessDefine
        {
            Id = defineId,
            Name = MarkerPrefix + defineId,
            DisplayName = "142 归属值写侧真库档",
            Type = "approval",
            State = 1,
            Content = System.Text.Encoding.UTF8.GetBytes(TwoTaskFlow),
            Version = 1,
            UpdateUser = "tester",
        };
        await _repo.SaveDefineAsync(define);
        return defineId;
    }

    /// <summary>发起并办结申请节点，返回 approval 任务的真实 id（business_no 带本用例标记）。</summary>
    private async Task<long> ApprovalTaskIdAsync()
    {
        var inst = await _engine.StartProcessInstanceByIdAsync(await SaveDefineAsync(), "zhangsan",
            new FlowData { [FlowConst.BusinessNo] = MarkerPrefix + "bn-" + Guid.NewGuid().ToString("N")[..12] });
        var apply = (await _repo.FindDoingTasksAsync(inst.InstanceId!.Value, null))[0];
        await _engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "zhangsan", new FlowData());
        var approval = (await _repo.FindDoingTasksAsync(inst.InstanceId.Value, null))[0];
        return approval.TaskId!.Value;
    }

    /// <summary>真表取证：某任务的 actor_id 原样读数（按 id 升序＝插入序）。</summary>
    private async Task<List<string>> ActorIdsAsync(long taskId)
    {
        var rows = new List<string>();
        await using var conn = await _factory.OpenAsync();
        await using var cmd = new MySqlCommand(
            "SELECT actor_id FROM wf_process_task_actor WHERE process_task_id = @t ORDER BY id ASC", conn);
        cmd.Parameters.AddWithValue("@t", taskId);
        await using var rs = await cmd.ExecuteReaderAsync();
        while (await rs.ReadAsync()) rows.Add(rs.GetString(0));
        return rows;
    }

    private static List<object?> Col(params object?[] items) => items.ToList();

    // ═══ 写侧兜底（真库读数）═══

    /// <summary>
    /// 直连 SQL 仓储写侧：空串/纯空白/null 丢弃、落库值取 trim 后的串、同次调用折叠，
    /// 反向哨兵 "0"/"00"/"a" 三个人都在。
    /// 改前实测红样（真库）：入参 <c>[" 18001 ","18001","","  ","0"]</c> ⇒ 5 行，
    /// 其中 <c>''</c> 与 <c>'  '</c> 两条是空归属值、<c>' 18001 '</c> 与 <c>'18001'</c> 是同一人两行。
    /// </summary>
    [Fact]
    public async Task SqlRepoAddTaskActorWriteSideNormalizesRealRows()
    {
        var taskId = await ApprovalTaskIdAsync();

        await _repo.AddTaskActorAsync(taskId,
            new List<string> { " 18001 ", "18001", "", "  ", "0", "00", "a" });

        Assert.Equal(new List<string> { "leader", "18001", "0", "00", "a" }, await ActorIdsAsync(taskId));
    }

    /// <summary>trim 与判重咬合：先落 18101 再写 " 18101 " ⇒ 真库仍是同一人一行（不 trim 就两行）。</summary>
    [Fact]
    public async Task SqlRepoPaddedValueHitsTheDedupRule()
    {
        var taskId = await ApprovalTaskIdAsync();

        await _repo.AddTaskActorAsync(taskId, new List<string> { "18101" });
        await _repo.AddTaskActorAsync(taskId, new List<string> { " 18101 " });

        Assert.Equal(new List<string> { "leader", "18101" }, await ActorIdsAsync(taskId));
    }

    /// <summary>主键档：真库里也不得留下 process_task_id<=0 的孤儿行，两仓一律抛错。</summary>
    [Fact]
    public async Task SqlRepoAddTaskActorRejectsNonPositiveTaskId()
    {
        await Assert.ThrowsAsync<JeeflowException>(() =>
            _repo.AddTaskActorAsync(0, new List<string> { "18201" }));

        await using var conn = await _factory.OpenAsync();
        await using var cmd = new MySqlCommand(
            "SELECT COUNT(*) FROM wf_process_task_actor WHERE process_task_id <= 0", conn);
        Assert.Equal(0L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
    }

    // ═══ 门面加签腿（两形同判据）打到真库 ═══

    /// <summary>
    /// 集合腿与逗号串腿在真库上同答案（§2.11「两形同判据」）。
    /// 改前实测红样：数组腿落 <c>' 18301 '</c>／<c>'  '</c>，串腿落 <c>'18301'</c> ⇒ 两形两样。
    /// </summary>
    [Fact]
    public async Task SurrogateTwoFormsAgreeOnRealDb()
    {
        var byArray = await ApprovalTaskIdAsync();
        var byString = await ApprovalTaskIdAsync();

        Assert.Equal(0, (await _facade.FlowAsync("processTask/surrogate", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = byArray,
            ["actorIds"] = Col(" 18301 ", "18301", "", "  ", null, "18302"),
        }))["code"]);
        Assert.Equal(0, (await _facade.FlowAsync("processTask/surrogate", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = byString,
            ["actorIds"] = " 18301 ,, 18302 ,",
        }))["code"]);

        var arrayRows = await ActorIdsAsync(byArray);
        var stringRows = await ActorIdsAsync(byString);
        Assert.Equal((arrayRows.Skip(1)).ToList(), (stringRows.Skip(1)).ToList());
        Assert.Equal(new List<string> { "18301", "18302" }, arrayRows.Skip(1).ToList());
    }

    /// <summary>全空白集合 ⇒ 与"缺参数"档同判（不新造错误码/文案），真库零新行。</summary>
    [Fact]
    public async Task SurrogateAllBlankKeepsMissingParamArmOnRealDb()
    {
        var taskId = await ApprovalTaskIdAsync();
        var before = await ActorIdsAsync(taskId);

        var resp = await _facade.FlowAsync("processTask/surrogate", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = taskId,
            ["actorIds"] = Col("", "  ", null),
        });

        Assert.Equal(99999999, resp["code"]);
        Assert.Equal("processTaskId/actorIds 缺失", resp["msg"]);
        Assert.Equal(before, await ActorIdsAsync(taskId));
    }

    // ═══ 两仓同一枚判据（issues/117 场景 27 那把尺子）═══

    /// <summary>
    /// 同一份入参，内存仓与 SQL 仓必须给同一个答案——判据只有 <c>PageQuery.NormalizeActors</c>
    /// 一枚，两仓各调它而不是各抄一份（rust 那族"sqlx 仓盲插、内存仓判重"的两仓分叉就是反面）。
    /// </summary>
    [Fact]
    public async Task BothReposAgreeOnTheSameJudge()
    {
        var input = new List<string> { " 18501 ", "18501", "", "  ", "\t\n", null!, "0", "00", " 0 ", "a" };
        var sqlTaskId = await ApprovalTaskIdAsync();
        var memTaskId = SyntheticTaskLo + Interlocked.Increment(ref _defineCounter);
        // 内存侧先建同一位建单人，让两张任务的起跑线一致（真库那张由流程建单带来 leader）
        await _mem.AddTaskActorAsync(memTaskId, new List<string> { "leader" });

        await _repo.AddTaskActorAsync(sqlTaskId, input);
        await _mem.AddTaskActorAsync(memTaskId, input);

        Assert.Equal(await ActorIdsAsync(sqlTaskId), await _mem.FindTaskActorsAsync(memTaskId));
        Assert.Equal(new List<string> { "leader", "18501", "0", "00", "a" }, await ActorIdsAsync(sqlTaskId));
    }

    // ═══ 删除位（issues/142 §9.2 第二批）真库读数 ═══

    /// <summary>
    /// SQL 仓删除位同一条尺子：先落 trim 值、再拿「 18401 」删——改前实测红样（真库）：
    /// DELETE 拿未 trim 原值进 IN 列表 ⇒ 静默 no-op（报成功却没删）；归一后为空 ⇒
    /// <b>一条 DELETE 都不发</b>（真库插一条 actor_id='' 的历史脏行，空串入参不得批量误删）。
    /// </summary>
    [Fact]
    public async Task SqlRepoRemoveTaskActorTrimsMatchAndBlankListIsNoOp()
    {
        var taskId = await ApprovalTaskIdAsync();
        await _repo.AddTaskActorAsync(taskId, new List<string> { " 18401 " });
        Assert.Equal(new List<string> { "leader", "18401" }, await ActorIdsAsync(taskId));

        await _repo.RemoveTaskActorAsync(taskId, new List<string> { " 18401 " });
        Assert.Equal(new List<string> { "leader" }, await ActorIdsAsync(taskId));

        // 历史脏行（旧版本写进去的 actor_id=''）：空串/全空白入参一条都不许删
        await using (var conn = await _factory.OpenAsync())
        {
            await using var cmd = new MySqlCommand(
                "INSERT INTO wf_process_task_actor (id, process_task_id, actor_id, create_time) VALUES (@i, @t, '', NOW())",
                conn);
            cmd.Parameters.AddWithValue("@i", SyntheticTaskLo + 1);
            cmd.Parameters.AddWithValue("@t", taskId);
            await cmd.ExecuteNonQueryAsync();
        }
        await _repo.RemoveTaskActorAsync(taskId, new List<string> { "" });
        await _repo.RemoveTaskActorAsync(taskId, new List<string> { "  ", null! });
        await _repo.RemoveTaskActorAsync(taskId, new List<string>());
        // 脏行是合成小 id、leader 是雪花大 id，按 id 排序会排前面——这里只断成员（排序无契约）。
        Assert.Equal(new List<string> { "", "leader" }, (await ActorIdsAsync(taskId)).OrderBy(x => x).ToList());
    }
}

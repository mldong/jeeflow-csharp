using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;
using Mldong.Jeeflow.Repository.MySql;
using MySqlConnector;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// 抄送两格的 MySQL 仓储一路（issues/141 G1 归属条件必填 ＋ G2 写侧判重＝幂等空操作 · C# 栈）。
///
/// <para>与 <see cref="CcPageOwnership141Tests"/>（内存仓一路）/ <see cref="CcWriteIdempotent141Tests"/>
/// 跑同一条判据：<b>同一份数据，SQL 仓与内存仓必须给同一个答案</b>（spec 06 §2.5，
/// issues/117 场景 27 那把尺子扩到 ccList）。G1 的旧形状在这一侧是
/// <c>FROM wf_process_instance t LEFT JOIN wf_process_cc_instance cc</c> 不带条件时<b>返回全部实例</b>
/// （php PDO 仓的同款反面教材），而它自家内存仓只放"有 cc 行的实例"——两仓相反。
/// 判据本体是同一个 <see cref="PageQuery.HasEffectiveCondition"/>，两仓不可能各判各的。</para>
///
/// <para>G2 的四档逐字照 spec 06 §4：同一 <c>(实例, 人)</c> 已有 cc 行时<b>跳过</b>——
/// ①不新增行 ②不重置未读状态（state）③不更新原行时间（create_time/update_time 与原行 id 逐字不变）
/// ④不 fire CC_CREATE（码 4）。查询侧不引入 DISTINCT、历史重复行不清理（owner 拍为接受既成事实），
/// 所以这里只钉写侧。</para>
///
/// <para>断言直接查 <c>wf_process_cc_instance</c> 的真实行——只看返回值或只看内存对象都不作数。
/// 数据用 <c>T1CS-cc141-</c> 前缀的 business_no 标记，进出各清一次（R6 测后自清理，
/// 与 <see cref="MySqlFixture"/> 的 T1 段约定同姿势）；连接一律经
/// <see cref="MySqlFixture"/>/<see cref="TestDb"/> 的工厂，DSN 里
/// <c>TreatTinyAsBoolean=False</c> 是既有义务（issues/123），本用例不碰连接串。</para>
/// </summary>
[Collection("mysql")]
[Trait("Category", "mysql-smoke")]
public class MySqlCcOwnershipIdempotent141Tests : IAsyncLifetime
{
    private const string MarkerPrefix = "T1CS-cc141-";

    /// <summary>归属值取本用例专属串：160 库七语言共用，别拿 user1/user2 这种公共 id 抢读数。</summary>
    private const string Actor1 = "cc141u1";
    private const string Actor2 = "cc141u2";

    private readonly MySqlConnectionFactory _factory;
    private readonly MySqlRepository _repo;
    private readonly JeeflowFacade _facade;
    private readonly FixedClock _clock;

    /// <summary>实例 id 用独立 worker(141) 的雪花段，避开夹具与其它栈的 id 空间。</summary>
    private readonly AtomicIdGenerator _ids = new(141L, SystemClock.Instance);

    /// <summary>事件 sink：只收 CC_CREATE，"重复抄送没有新事件"断在这里。</summary>
    private readonly List<ProcessEvent> _ccEvents = new();

    private readonly int _tag = (int)((Environment.TickCount64 + Environment.CurrentManagedThreadId * 977) % 100000);

    public MySqlCcOwnershipIdempotent141Tests(MySqlFixture fx)
    {
        _factory = fx.Factory;
        // 本仓自带的 DSN 工厂（不动连接串），但用**独立 ServiceContext**：时钟要能被本用例拨走
        // （③档"没刷原行时间"在固定钟下分辨不出来），ID 段用 worker 141 避开夹具的 worker。
        _clock = new FixedClock(new DateTime(2026, 8, 1, 9, 0, 0));
        _repo = new MySqlRepository(_factory);
        var ctx = new ServiceContext(_repo)
        {
            Clock = _clock,
            IdGenerator = new AtomicIdGenerator(141L, _clock),
        };
        _repo.Configure(ctx);
        ctx.RegisterEventListener(new CcCapture(_ccEvents));
        _facade = new JeeflowFacade(ctx);
    }

    private sealed class CcCapture : IProcessEventListener
    {
        private readonly List<ProcessEvent> _sink;
        public CcCapture(List<ProcessEvent> sink) => _sink = sink;
        public Task OnEventAsync(ProcessEvent @event)
        {
            if (@event.EventType == ProcessEventType.CcCreate) _sink.Add(@event);
            return Task.CompletedTask;
        }
    }

    public async Task InitializeAsync() => await PurgeAsync();

    public async Task DisposeAsync() => await PurgeAsync();

    private async Task PurgeAsync()
    {
        await using var conn = await _factory.OpenAsync();
        await using (var cmd = new MySqlCommand(
            "DELETE cc FROM wf_process_cc_instance cc JOIN wf_process_instance i ON i.id = cc.process_instance_id " +
            $"WHERE i.business_no LIKE '{MarkerPrefix}%'", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = new MySqlCommand(
            $"DELETE FROM wf_process_instance WHERE business_no LIKE '{MarkerPrefix}%'", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ── 夹具与取证辅助 ──

    /// <summary>直插一条实例行（cc 的归属对象），返回实例 id。</summary>
    private async Task<long> NewInstanceAsync(string actorId)
    {
        var id = _ids.NextId();
        await using var conn = await _factory.OpenAsync();
        await using var cmd = new MySqlCommand(
            "INSERT INTO wf_process_instance (id, process_define_id, state, business_no, operator, create_time, create_user) " +
            "VALUES (@id, 1, 10, @bn, 'zhangsan', @now, 'zhangsan')", conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@bn", MarkerPrefix + actorId + "-" + _tag);
        cmd.Parameters.AddWithValue("@now", new DateTime(2026, 8, 1, 9, 0, 0));
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    /// <summary>cc 表取证：某 (实例, 人) 的真实行 [id, state, create_time, update_time]。</summary>
    private async Task<List<(long Id, int State, DateTime? CreateTime, DateTime? UpdateTime)>> CcRowsAsync(
        long instanceId, string actorId)
    {
        var rows = new List<(long, int, DateTime?, DateTime?)>();
        await using var conn = await _factory.OpenAsync();
        await using var cmd = new MySqlCommand(
            "SELECT id, state, create_time, update_time FROM wf_process_cc_instance " +
            "WHERE process_instance_id = @i AND actor_id = @a ORDER BY id", conn);
        cmd.Parameters.AddWithValue("@i", instanceId);
        cmd.Parameters.AddWithValue("@a", actorId);
        await using var rs = await cmd.ExecuteReaderAsync();
        while (await rs.ReadAsync())
        {
            rows.Add((rs.GetInt64("id"), rs.GetInt32("state"),
                rs.IsDBNull(rs.GetOrdinal("create_time")) ? null : rs.GetDateTime("create_time"),
                rs.IsDBNull(rs.GetOrdinal("update_time")) ? null : rs.GetDateTime("update_time")));
        }
        return rows;
    }

    /// <summary>cc 表全量计数（某实例的所有 cc 行，不带 actor 条件）。</summary>
    private async Task<int> CcRowCountAsync(long instanceId)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = new MySqlCommand(
            "SELECT COUNT(*) FROM wf_process_cc_instance WHERE process_instance_id = @i", conn);
        cmd.Parameters.AddWithValue("@i", instanceId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task ManualCcAsync(long instanceId, params string[] actorIds)
    {
        var resp = await _facade.FlowAsync("processInstance/createCCInstance", new FlowData
        {
            [FlowConst.ProcessInstanceIdKey] = instanceId,
            ["operator"] = "zhangsan",
            ["actorIds"] = actorIds.Select(a => (object?)a).ToList(),
        });
        Assert.True(Equals(0, resp["code"]), $"手动抄送应成功: {resp["msg"]}");
    }

    private List<string> CcActorIdsOfEvents() => _ccEvents.Select(e => e.CcActorId ?? "").ToList();

    /// <summary>让两次操作的时间戳必然可分（DATETIME(3) 精度到毫秒，这里直接拨钟）。</summary>
    private void Tick() => _clock.Now = _clock.Now.AddSeconds(5);

    // ═══ G1：归属条件必填，缺条件 ⇒ 空页（SQL 仓） ═══
    // 真库是七语言共用的 160 jeeflow 库，归属值取本用例专属串（cc141u1/cc141u2），
    // 免得别的栈留下的 cc 行把"只出我的那一页"读成多行（数据面隔离，判据不变）。

    /// <summary>正向对照：带 <c>cc.actor_id</c> 条件时只出"我的"。</summary>
    [Fact]
    public async Task CcPageWithOwnershipConditionReturnsOnlyMine()
    {
        var mine = await NewInstanceAsync("u1");
        var theirs = await NewInstanceAsync("u2");
        await _repo.CreateCcInstanceAsync(mine, "zhangsan", Actor1);
        await _repo.CreateCcInstanceAsync(theirs, "zhangsan", Actor2);

        var page = await _repo.PageCcInstancesAsync(new PageQuery(1, 50).Add("cc.actor_id", "EQ", Actor1));

        Assert.Equal(1, page.RecordCount);                 // 带条件应命中我的那 1 条
        Assert.Single(page.Rows);                          // rows 与 recordCount 同口径
        Assert.Equal(mine, page.Rows[0].Id);               // 命中的应是我的实例
        Assert.NotEqual(theirs, page.Rows[0].Id);          // 别人的实例不该串进来
    }

    /// <summary>
    /// 缺陷档：整条归属条件都不给 ⇒ 空页。改前这一格是红的——LEFT JOIN 不带条件时
    /// <c>WHERE 1=1</c> 直接放出<b>全部实例</b>（本用例库里就有 2 条实例），
    /// 与它自家内存仓"只放有 cc 行的实例"两个答案。
    /// </summary>
    [Fact]
    public async Task CcPageWithoutOwnershipConditionIsEmptyPage()
    {
        var mine = await NewInstanceAsync("u1");
        var theirs = await NewInstanceAsync("u2");
        await _repo.CreateCcInstanceAsync(mine, "zhangsan", Actor1);
        await _repo.CreateCcInstanceAsync(theirs, "zhangsan", Actor2);

        var noCondition = await _repo.PageCcInstancesAsync(new PageQuery(1, 50));
        Assert.Equal(0, noCondition.RecordCount);   // 缺归属条件必须返回空页，而不是全部实例
        Assert.Empty(noCondition.Rows);             // 空页的 rows 也必须是空集合

        var bareQuery = await _repo.PageCcInstancesAsync(new PageQuery());
        Assert.Equal(0, bareQuery.RecordCount);     // 默认分页参数同样缺归属条件 ⇒ 空页
    }

    /// <summary>空值四形与"条件整条缺失"同档（issues/129 那层已钉 EQ 空值，这里补齐 SQL 仓整套判据）。</summary>
    [Fact]
    public async Task BlankOwnershipConditionIsAlsoEmptyPage()
    {
        var mine = await NewInstanceAsync("u1");
        await _repo.CreateCcInstanceAsync(mine, "zhangsan", Actor1);

        Assert.Equal(0, (await _repo.PageCcInstancesAsync(
            new PageQuery(1, 50).Add("cc.actor_id", "EQ", ""))).RecordCount);
        Assert.Equal(0, (await _repo.PageCcInstancesAsync(
            new PageQuery(1, 50).Add("cc.actor_id", "EQ", "   "))).RecordCount);
        Assert.Equal(0, (await _repo.PageCcInstancesAsync(
            new PageQuery(1, 50).Add("cc.actor_id", "EQ", null))).RecordCount);
        Assert.Equal(0, (await _repo.PageCcInstancesAsync(
            new PageQuery(1, 50).Add("cc.actor_id", "IN", new List<string>()))).RecordCount);
    }

    /// <summary>改动面哨兵：只收归属谓词，非归属列的空值仍按"没填"忽略（可选过滤不许改成空页）。</summary>
    [Fact]
    public async Task BlankNonOwnershipConditionIsStillIgnored()
    {
        var mine = await NewInstanceAsync("u1");
        await _repo.CreateCcInstanceAsync(mine, "zhangsan", Actor1);

        var page = await _repo.PageCcInstancesAsync(new PageQuery(1, 50)
            .Add("cc.actor_id", "EQ", Actor1)
            .Add("t.business_no", "LIKE", ""));

        Assert.Equal(1, page.RecordCount);   // 空值非归属条件应被忽略，归属条件照常生效
    }

    /// <summary>
    /// 两仓同读数（issues/117 场景 27 那把尺子的 ccList 档）：同一份数据摆进内存仓与 SQL 仓，
    /// 四档条件的 <c>recordCount</c> 逐档相等。这条是"只改一面就自欺"的防线——
    /// 判据虽共用，分页展开形状两仓不同（内存 inner 语义 / SQL LEFT JOIN），必须实测对齐。
    /// </summary>
    [Fact]
    public async Task DualRepoGiveTheSamePageCountsOnTheSameJudge()
    {
        var mine = await NewInstanceAsync("u1");
        var theirs = await NewInstanceAsync("u2");
        await _repo.CreateCcInstanceAsync(mine, "zhangsan", Actor1);
        await _repo.CreateCcInstanceAsync(theirs, "zhangsan", Actor2);

        var (_, memRepo) = TestInfra.NewEngine();
        var define = new ProcessDefine
        {
            Name = "cc141-parity", DisplayName = "抄送归属流程", Type = "approval",
            State = 1, Version = 1,
            Content = System.Text.Encoding.UTF8.GetBytes("{}"),
        };
        await memRepo.SaveDefineAsync(define);
        foreach (var actor in new[] { Actor1, Actor2 })
        {
            var inst = ProcessInstance.Create(define, "zhangsan",
                new FlowData { [FlowConst.BusinessNo] = MarkerPrefix + actor });
            await memRepo.SaveInstanceAsync(inst);
            await memRepo.CreateCcInstanceAsync(inst.InstanceId!.Value, "zhangsan", actor);
        }

        foreach (var (label, cond) in new (string, Action<PageQuery>)[]
        {
            ("零条件", q => { }),
            ("空串条件", q => q.Add("cc.actor_id", "EQ", "")),
            ("我的条件", q => q.Add("cc.actor_id", "EQ", Actor1)),
            ("两人条件", q => q.Add("cc.actor_id", "IN", new List<string> { Actor1, Actor2 })),
        })
        {
            var sqlQuery = new PageQuery(1, 50);
            cond(sqlQuery);
            var memQuery = new PageQuery(1, 50);
            cond(memQuery);
            var memCount = (await memRepo.PageCcInstancesAsync(memQuery)).RecordCount;
            var sqlCount = (await _repo.PageCcInstancesAsync(sqlQuery)).RecordCount;
            Assert.True(memCount == sqlCount,
                $"档位「{label}」两仓读数必须相等：内存仓 {memCount} vs SQL 仓 {sqlCount}");
        }
    }

    // ═══ G2：写侧判重＝幂等空操作（SQL 仓） ═══

    /// <summary>正向对照：全新的一次抄送照旧建行＋逐人 fire 码 4。</summary>
    [Fact]
    public async Task FirstCcStillCreatesRowAndFiresPerActor()
    {
        var iid = await NewInstanceAsync("fire");
        _ccEvents.Clear();
        Tick();

        await ManualCcAsync(iid, "8101", "8102");

        Assert.Equal(2, await CcRowCountAsync(iid));                                     // 逐人建行
        Assert.Equal(2, _ccEvents.Count);                                                 // 逐人 fire（码 4）
        Assert.Equal(new List<string> { "8101", "8102" }, CcActorIdsOfEvents());          // 顺序与入参一致
        Assert.All(_ccEvents, e => Assert.Equal(iid, e.SourceId!.Value));
        Assert.Equal(0, (await CcRowsAsync(iid, "8101"))[0].State);                        // 首行未读 state=0
    }

    /// <summary>①不新增行 ＋ ④不 fire 码 4：门面手动腿连发两次同一个人（SQL 仓一路）。</summary>
    [Fact]
    public async Task RepeatCcAddsNoRowAndFiresNothing()
    {
        var iid = await NewInstanceAsync("repeat");
        await ManualCcAsync(iid, "8201");
        Assert.Equal(1, await CcRowCountAsync(iid));
        Assert.Equal(1, _ccEvents.Count);
        var rowId = (await CcRowsAsync(iid, "8201"))[0].Id;

        _ccEvents.Clear();
        Tick();
        await ManualCcAsync(iid, "8201");

        Assert.Equal(1, await CcRowCountAsync(iid));                    // ①重复抄送不得新增行
        Assert.Equal(rowId, (await CcRowsAsync(iid, "8201"))[0].Id);     // ①原行 id 不变（没有删旧插新）
        Assert.Empty(_ccEvents);   // ④没发生创建就不得发码 4（spec 11.2 原则 1「码=事实」）
    }

    /// <summary>②不重置未读：SQL 置已读（state=1）后重复抄送，state 必须仍是 1。</summary>
    [Fact]
    public async Task RepeatCcDoesNotResetUnreadState()
    {
        var iid = await NewInstanceAsync("read");
        await _repo.CreateCcInstanceAsync(iid, "zhangsan", "8301");
        await _repo.UpdateCcStatusAsync(iid, "8301");
        Assert.Equal(1, (await CcRowsAsync(iid, "8301"))[0].State);

        Tick();
        await _repo.CreateCcInstanceAsync(iid, "zhangsan", "8301");

        Assert.Equal(1, await CcRowCountAsync(iid));                     // ①重复抄送不得新增行
        Assert.Equal(1, (await CcRowsAsync(iid, "8301"))[0].State);       // ②不得把已读抹回未读
    }

    /// <summary>③不更新原行时间：create_time / update_time 逐字不变（跳过式判重不走 UPDATE，也不重插）。</summary>
    [Fact]
    public async Task RepeatCcDoesNotTouchOriginalRowTimes()
    {
        var iid = await NewInstanceAsync("time");
        await _repo.CreateCcInstanceAsync(iid, "zhangsan", "8401");
        var before = (await CcRowsAsync(iid, "8401"))[0];
        Assert.NotNull(before.CreateTime);

        Tick();   // 拨钟 5 秒：实现若刷时间，读数必然变
        await _repo.CreateCcInstanceAsync(iid, "zhangsan", "8401");

        var after = (await CcRowsAsync(iid, "8401"))[0];
        Assert.Equal(1, await CcRowCountAsync(iid));
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.CreateTime, after.CreateTime);      // ③不得刷新原行 create_time
        Assert.Equal(before.UpdateTime, after.UpdateTime);      // ③不得刷新原行 update_time
    }

    /// <summary>子集档：第二次给「已知人＋新人」⇒ 只为新人建行、只为新人 fire。</summary>
    [Fact]
    public async Task RepeatCcFiresOnlyForNewlyCreatedSubset()
    {
        var iid = await NewInstanceAsync("subset");
        await ManualCcAsync(iid, "8501", "8502");
        Assert.Equal(2, await CcRowCountAsync(iid));
        Assert.Equal(2, _ccEvents.Count);

        _ccEvents.Clear();
        Tick();
        await ManualCcAsync(iid, "8501", "8503");

        Assert.Equal(3, await CcRowCountAsync(iid));                                  // 只为实际新人建行
        Assert.Equal(new List<string> { "8503" }, CcActorIdsOfEvents());                // 入参＝实际新建的子集
        Assert.Single(_ccEvents);                                                      // 子集 1 人 ⇒ 只 fire 1 次
        Assert.Single(await CcRowsAsync(iid, "8503"));                                  // 新人 8503 的行真在库里
    }

    /// <summary>SPI 读侧：<c>FindCcActorIdsAsync</c> 反映真实行集（判重的依据不能是内存猜测）。</summary>
    [Fact]
    public async Task FindCcActorIdsReadsTheRealRows()
    {
        var iid = await NewInstanceAsync("readids");
        Assert.Empty(await _repo.FindCcActorIdsAsync(iid));                              // 空实例没有 cc 行

        await _repo.CreateCcInstanceAsync(iid, "zhangsan", "8601", "8602");
        Assert.Equal(new List<string> { "8601", "8602" }, await _repo.FindCcActorIdsAsync(iid));

        await _repo.CreateCcInstanceAsync(iid, "zhangsan", "8601", "8603");
        Assert.Equal(new List<string> { "8601", "8602", "8603" },
            await _repo.FindCcActorIdsAsync(iid));                                       // 重复的 8601 不新增
    }

    /// <summary>两腿共用判据（SQL 仓一路）：手动腿与仓储直发腿对同一个人各自只留一行。</summary>
    [Fact]
    public async Task ManualLegAndRepoLegShareTheSameDedupRule()
    {
        var iid = await NewInstanceAsync("twolegs");
        await _repo.CreateCcInstanceAsync(iid, "zhangsan", "8701");                      // 直发腿
        _ccEvents.Clear();
        Tick();

        await ManualCcAsync(iid, "8701");                                                 // 手动腿重抄同一个人

        Assert.Equal(1, await CcRowCountAsync(iid));
        Assert.Equal(new List<string> { "8701" }, await _repo.FindCcActorIdsAsync(iid));
        Assert.Empty(_ccEvents);   // 手动腿的子集为空 ⇒ 整支不 fire（不空转）
    }
}

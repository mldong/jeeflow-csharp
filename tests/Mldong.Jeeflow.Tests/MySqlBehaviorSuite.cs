using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Repository.MySql;
using MySqlConnector;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// T1 MySQL 冒烟（160 真库，R6 红线）：
/// - 行为双跑（与 Memory 同套件，防仓储分叉）
/// - M1 分页五键 / M2 hydrate 主键 / 事务回滚无半完成实例 / 并发办理只一次成功
/// - define 用 9xxxxx 段；实例以 BUSINESS_NO=T1CS- 前缀标记；测后自清理 + 清理验证
/// - SKIP_MYSQL=1 开发机跳过；凭据只走 JEFFLOW_DB_* env；连不上=fail 不是 skip（发版机口径）
/// </summary>
[Trait("Category", "mysql-smoke")]
public class MySqlBehaviorSuite : RepositoryBehaviorSuite, IClassFixture<MySqlFixture>
{
    private readonly MySqlFixture _fx;

    public MySqlBehaviorSuite(MySqlFixture fx) => _fx = fx;

    private static bool Skip =>
        Environment.GetEnvironmentVariable("SKIP_MYSQL") == "1";

    protected override (JeeflowEngine Engine, MemoryRepository? Mem, IProcessRepository Repo) Build() =>
        (_fx.Engine, null, _fx.Repo);

    // SKIP_MYSQL=1 开发机：vacuous pass（发版机不加 SKIP，连不上=fail，R6 口径）
    public override async Task Behavior_StartAndCompleteChain() { if (Skip) return; await base.Behavior_StartAndCompleteChain(); }
    public override async Task Behavior_TaskActorsRoundTrip() { if (Skip) return; await base.Behavior_TaskActorsRoundTrip(); }
    public override async Task Behavior_VariablesJsonRoundTrip() { if (Skip) return; await base.Behavior_VariablesJsonRoundTrip(); }
    public override async Task Behavior_PageTodoFiveKeys() { if (Skip) return; await base.Behavior_PageTodoFiveKeys(); }
    public override async Task Behavior_CcCreateAndUpdateStatus() { if (Skip) return; await base.Behavior_CcCreateAndUpdateStatus(); }
    public override async Task Behavior_DefineWriteOps() { if (Skip) return; await base.Behavior_DefineWriteOps(); }
    public override async Task Behavior_NonActorExecuteFails() { if (Skip) return; await base.Behavior_NonActorExecuteFails(); }

    protected override async Task<(long DefineId, long InstanceId)> SeedSimpleFlowAsync(
        JeeflowEngine engine, IProcessRepository repo, string businessNo, FlowData? args = null)
    {
        var defineId = await _fx.SaveT1DefineAsync("BHV-" + businessNo);
        var flowArgs = args ?? new FlowData();
        flowArgs[FlowConst.BusinessNo] = "T1CS-" + businessNo;
        var inst = await engine.StartProcessInstanceByIdAsync(defineId, "applicant", flowArgs);
        var apply = await FindDoingByActorAsync(repo, inst.InstanceId!.Value, "applicant");
        await engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData());
        return (defineId, inst.InstanceId.Value);
    }

    [Fact]
    public async Task T1M1_PagingFiveKeysOverRealDb()
    {
        if (Skip) return; // SKIP_MYSQL=1
        // M1：分页五键走真 SQL（LIMIT 内联 + 白名单 + COUNT）
        var (_, _, repo) = Build();
        var define = new ProcessDefine
        {
            Id = 910101,
            Name = "T1CS-page-def",
            DisplayName = "T1-分页",
            Type = "approval",
            State = 1,
            Content = System.Text.Encoding.UTF8.GetBytes(TestInfra.LoadFlow("01-simple")),
            Version = 1,
        };
        await repo.SaveDefineAsync(define);
        try
        {
            var query = new PageQuery(1, 2).Add("t.name", "LIKE", "T1CS-page-def");
            var page = await repo.PageDefinesAsync(query);
            Assert.Equal(1, page.PageNum);
            Assert.Equal(2, page.PageSize);
            Assert.Equal(1, page.RecordCount);
            Assert.Equal(1, page.TotalPage);
            Assert.NotEmpty(page.Rows);
            Assert.Equal("T1CS-page-def", page.Rows[0].Name);

            // m_ 三段式经仓储白名单生效
            var q2 = new JeeflowQueryParser().Parse(new Dictionary<string, object?>
            {
                ["pageSize"] = 5,
                ["m_t_EQ_name"] = "T1CS-page-def",
            });
            var page2 = await repo.PageDefinesAsync(q2);
            Assert.Equal(1, page2.RecordCount);
        }
        finally
        {
            await repo.RemoveDefineAsync(define.Id!.Value);
        }
    }

    [Fact]
    public async Task T1M2_HydratePrimaryKeyExact()
    {
        if (Skip) return; // SKIP_MYSQL=1
        // M2：手动 9xxxxx 主键落库 + 雪花(>2^53)主键读回均精确保真（hydrate 无截断/取整）
        var (_, _, repo) = Build();
        var defineId = await _fx.SaveT1DefineAsync("hydrate-pk");
        var inst = await _fx.Engine.StartProcessInstanceByIdAsync(defineId, "applicant",
            new FlowData { [FlowConst.BusinessNo] = "T1CS-hydrate-pk" });
        var applyH = await FindDoingByActorAsync(repo, inst.InstanceId!.Value, "applicant");
        await _fx.Engine.ExecuteProcessTaskAsync(applyH.TaskId!.Value, "applicant", new FlowData());
        try
        {
            // 雪花 id > 2^53 精确读回
            Assert.True(inst.InstanceId > 9007199254740993L);
            var hydrated = await repo.FindInstanceByIdAsync(inst.InstanceId);
            Assert.Equal(inst.InstanceId, hydrated!.InstanceId);
            Assert.NotEmpty(hydrated.Tasks); // 聚合水合
            Assert.All(hydrated.Tasks, t => Assert.True(t.TaskId > 0));

            // 手动 9xxxxx 主键（apply 操作人 task_actor 行）
            var task = await FindDoingByActorAsync(repo, inst.InstanceId.Value, "leader");
            Assert.True(task.TaskId > 0);
            var actors = await repo.FindTaskActorsAsync(task.TaskId.Value);
            Assert.Contains("leader", actors);
        }
        finally
        {
            await _fx.CleanupByMarkerAsync("T1CS-hydrate-pk");
        }
    }

    [Fact]
    public async Task T1_TxRollbackLeavesNoHalfInstance()
    {
        if (Skip) return; // SKIP_MYSQL=1
        // §6.2：ITransactionTemplate 回调抛错 → BEGIN 后所有写入回滚净，无半完成实例
        var defineId = await _fx.SaveT1DefineAsync("tx-rollback");
        var countBefore = await _fx.CountMarkerAsync("T1CS-tx-rollback");
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => _fx.Tx.ExecuteInTxAsync<object?>(async () =>
            {
                await _fx.Engine.StartProcessInstanceByIdAsync(defineId, "applicant",
                    new FlowData { [FlowConst.BusinessNo] = "T1CS-tx-rollback" });
                throw new InvalidOperationException("rollback-probe");
            }));
        // 探针异常（或其包装）必须可见——回滚由下一断言验证
        var msg = ex.Message + (ex.InnerException?.Message ?? "");
        Assert.Contains("rollback-probe", msg);
        var countAfter = await _fx.CountMarkerAsync("T1CS-tx-rollback");
        Assert.Equal(countBefore, countAfter); // 无半完成实例
    }

    [Fact]
    public async Task T1_ConcurrentCompleteOnlyOneWins()
    {
        if (Skip) return; // SKIP_MYSQL=1
        // §6.2：同任务两次 execute（并发）→ 恰一次成功，后者 99999999"任务已办理"
        var defineId = await _fx.SaveT1DefineAsync("concurrency");
        var inst = await _fx.Engine.StartProcessInstanceByIdAsync(defineId, "applicant",
            new FlowData { [FlowConst.BusinessNo] = "T1CS-concurrency" });
        var applyC = await FindDoingByActorAsync(_fx.Repo, inst.InstanceId!.Value, "applicant");
        await _fx.Engine.ExecuteProcessTaskAsync(applyC.TaskId!.Value, "applicant", new FlowData());
        var iid = inst.InstanceId.Value;
        try
        {
            var task = await FindDoingByActorAsync(_fx.Repo, iid, "leader");
            var taskId = task.TaskId!.Value;
            var done = 0;
            var failed = 0;
            var results = await Task.WhenAll(
                Probe(_fx.Engine, taskId, "leader"),
                Probe(_fx.Engine, taskId, "leader"));
            foreach (var ok in results) { if (ok) done++; else failed++; }
            Assert.Equal(1, done);
            Assert.Equal(1, failed);

            static async Task<bool> Probe(JeeflowEngine engine, long tid, string op)
            {
                try
                {
                    await engine.ExecuteProcessTaskAsync(tid, op, new FlowData());
                    return true;
                }
                catch (JeeflowException)
                {
                    return false;
                }
            }
        }
        finally
        {
            await _fx.CleanupByMarkerAsync("T1CS-concurrency");
        }
    }

    [Fact]
    public async Task T1_CleanupVerified()
    {
        if (Skip) return; // SKIP_MYSQL=1
        // R6：测后自清理 + 清理可验证
        var defineId = await _fx.SaveT1DefineAsync("cleanup-verify");
        var inst = await _fx.Engine.StartProcessInstanceByIdAsync(defineId, "applicant",
            new FlowData { [FlowConst.BusinessNo] = "T1CS-cleanup-verify" });
        Assert.True((await _fx.CountMarkerAsync("T1CS-cleanup-verify")) >= 1);
        await _fx.CleanupByMarkerAsync("T1CS-cleanup-verify");
        Assert.Equal(0, await _fx.CountMarkerAsync("T1CS-cleanup-verify"));
        _fx.RemoveDefine(defineId);
        Assert.Null(await _fx.Repo.FindDefineByIdAsync(defineId));
    }
}

/// <summary>
/// T1 共享夹具：连接 + 引擎 + T1 define（9xxxxx）+ schema 预置（IF NOT EXISTS 幂等）+ 清理。
/// </summary>
public sealed class MySqlFixture : IAsyncLifetime
{
    public MySqlConnectionFactory Factory { get; } = MySqlConnectionFactory.FromEnv();
    public MySqlRepository Repo { get; private set; } = null!;
    public MySqlExtRepository ExtRepo { get; private set; } = null!;
    public ServiceContext Ctx { get; private set; } = null!;
    public JeeflowEngine Engine { get; private set; } = null!;
    public MySqlTransactionTemplate Tx { get; private set; } = null!;

    private readonly List<long> _t1DefineIds = new();

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("SKIP_MYSQL") == "1") return;
        // 5 张 wf_* 表幂等确保（编辑源 schema-mysql.sql 副本；160 已建，IF NOT EXISTS 零副作用）
        await EnsureSchemaAsync();
        Repo = new MySqlRepository(Factory);
        ExtRepo = new MySqlExtRepository(Factory, Repo);
        Ctx = new ServiceContext(Repo, ExtRepo);
        Ctx.Clock = new FixedClock(new DateTime(2026, 8, 1, 9, 0, 0));
        Ctx.IdGenerator = new AtomicIdGenerator(1, Ctx.Clock);
        Ctx.UserProvider = new TestUserProvider();
        TestInfra.RegisterBuiltins(Ctx);
        Repo.Configure(Ctx);
        Tx = new MySqlTransactionTemplate(Factory, Repo, ExtRepo);
        Engine = new JeeflowEngine(Ctx);
    }

    public Task DisposeAsync() => CleanupAllAsync();

    private async Task EnsureSchemaAsync()
    {
        var schemaPath = FindSchemaPath();
        var statements = File.ReadAllText(schemaPath)
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        await using var conn = await Factory.OpenAsync();
        foreach (var st in statements)
        {
            if (!st.TrimStart().StartsWith("CREATE", StringComparison.OrdinalIgnoreCase)) continue;
            await using var cmd = new MySqlCommand(st, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static string FindSchemaPath()
    {
        foreach (var dir in new[] { "../../..", "../..", "..", "../../../../.." })
        {
            var p = Path.Combine(dir, "src", "Mldong.Jeeflow.Repository.MySql", "Schema", "schema-mysql.sql");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("schema-mysql.sql not found");
    }

    /// <summary>保存 T1 define（9xxxxx 段，自动递增编号）。</summary>
    public async Task<long> SaveT1DefineAsync(string name)
    {
        if (Environment.GetEnvironmentVariable("SKIP_MYSQL") == "1")
            throw new Xunit.Sdk.XunitException("SKIP_MYSQL=1");
        var id = NextT1Id();
        var define = new ProcessDefine
        {
            Id = id,
            Name = "T1CS-" + name,
            DisplayName = "T1-" + name,
            Type = "approval",
            State = 1,
            Content = System.Text.Encoding.UTF8.GetBytes(TestInfra.LoadFlow("01-simple")),
            Version = 1,
        };
        await Repo.SaveDefineAsync(define);
        _t1DefineIds.Add(id);
        return id;
    }

    private long _nextId = 910001;
    private long NextT1Id() => Interlocked.Increment(ref _nextId) - 1;

    public void RemoveDefine(long defineId) =>
        Repo.RemoveDefineAsync(defineId).GetAwaiter().GetResult();

    /// <summary>按 BUSINESS_NO 标记清理实例及任务/参与人/抄送行（R6 测后自清理）。</summary>
    public async Task CleanupByMarkerAsync(string marker)
    {
        var businessNo = marker.StartsWith("T1CS-", StringComparison.Ordinal) ? marker : "T1CS-" + marker;
        await using var conn = await Factory.OpenAsync();
        await Exec(conn, "DELETE a FROM wf_process_task_actor a " +
                         "JOIN wf_process_task t ON t.id = a.process_task_id " +
                         "JOIN wf_process_instance i ON i.id = t.process_instance_id " +
                         "WHERE i.business_no = {0}", businessNo);
        await Exec(conn, "DELETE t FROM wf_process_task t " +
                         "JOIN wf_process_instance i ON i.id = t.process_instance_id " +
                         "WHERE i.business_no = {0}", businessNo);
        await Exec(conn, "DELETE cc FROM wf_process_cc_instance cc " +
                         "JOIN wf_process_instance i ON i.id = cc.process_instance_id " +
                         "WHERE i.business_no = {0}", businessNo);
        await Exec(conn, "DELETE FROM wf_process_instance WHERE business_no = {0}", businessNo);
    }

    private async Task CleanupAllAsync()
    {
        if (Environment.GetEnvironmentVariable("SKIP_MYSQL") == "1") return;
        try
        {
            await using var conn = await Factory.OpenAsync();
            // 兜底清理：所有 T1CS 实例 + 910xxx define
            await Exec(conn, "DELETE a FROM wf_process_task_actor a " +
                             "JOIN wf_process_task t ON t.id = a.process_task_id " +
                             "JOIN wf_process_instance i ON i.id = t.process_instance_id " +
                             "WHERE i.business_no LIKE {0}", "T1CS-%");
            await Exec(conn, "DELETE t FROM wf_process_task t " +
                             "JOIN wf_process_instance i ON i.id = t.process_instance_id " +
                             "WHERE i.business_no LIKE {0}", "T1CS-%");
            await Exec(conn, "DELETE cc FROM wf_process_cc_instance cc " +
                             "JOIN wf_process_instance i ON i.id = cc.process_instance_id " +
                             "WHERE i.business_no LIKE {0}", "T1CS-%");
            await Exec(conn, "DELETE FROM wf_process_instance WHERE business_no LIKE {0}", "T1CS-%");
            await Exec(conn, "DELETE FROM wf_process_define WHERE id BETWEEN 910000 AND 919999");
        }
        catch
        {
            // 收尾清理失败不掩盖测试结果（前面各用例已自清理）
        }
    }

    public async Task<int> CountMarkerAsync(string marker)
    {
        var businessNo = marker.StartsWith("T1CS-", StringComparison.Ordinal) ? marker : "T1CS-" + marker;
        await using var conn = await Factory.OpenAsync();
        await using var cmd = new MySqlCommand(
            "SELECT COUNT(*) FROM wf_process_instance WHERE business_no = @bn", conn);
        cmd.Parameters.AddWithValue("@bn", businessNo);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task Exec(MySqlConnection conn, string sqlTemplate, string? arg = null)
    {
        if (sqlTemplate.Contains("{0}") && arg == null)
            throw new ArgumentException("placeholder requires arg");
        var sql = sqlTemplate.Replace("{0}", "@arg");
        await using var cmd = new MySqlCommand(sql, conn);
        if (arg != null) cmd.Parameters.AddWithValue("@arg", arg);
        await cmd.ExecuteNonQueryAsync();
    }
}

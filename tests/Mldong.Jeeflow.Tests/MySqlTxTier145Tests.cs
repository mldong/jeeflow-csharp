using System.Data.Common;
using Xunit;
using MySqlConnector;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;
using Mldong.Jeeflow.Persist;
using Mldong.Jeeflow.Repository.MySql;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// R9 145-4 · spec/12 §12.4 三档判据（csharp 腿，真库 160）＋ C99 ⑥ persist 写腿：
/// ① 空转档：事务内写入一行，提交前同连接读得到、另一独立连接读不到、回滚后不存在；
/// ② 归属档：嵌套并入最外层（不另开连接、不重复 BEGIN、随外层回滚）＋ 并发作用域互相隔离
///    （A 回滚不影响 B 的成功提交）；
/// ③ 原子档：门面复合动作（startAndExecute 三拍）中途失败 ⇒ 全部写入回滚净；同流程"未挂模板"
///    裸拼态同注入点留半完成 ⇒ 对照证明判据抓得住半完成（spec §12.2 原子面硬义务 1）。
/// ④ persist 写腿（C99 ⑥）：writer 接线 AmbientTxProvider 后并入动作事务（工厂被换成"一调即抛"
///    以证明不再自开连接）；同注入点未接线对照 ⇒ 独立提交留存（未接线＝允许运行态）。
/// ⑤ 门评估（145-4）：双引擎实例等效"命令级互斥失效"，真并发办同一任务取"无门时是否双成功"读数。
/// 标记/清理口径与 MySqlBehaviorSuite 同款：business_no = T1CS- 前缀、define 9xxxxx 段、测后自清理。
/// </summary>
[Collection("mysql")]
[Trait("Category", "mysql-smoke")]
public class MySqlTxTier145Tests
{
    private readonly MySqlFixture _fx;

    public MySqlTxTier145Tests(MySqlFixture fx) => _fx = fx;

    private const string ProbeTable = "tx_tier_145_probe";
    private const string ProbeMarkerLike = "T1CS-tx145-%";

    /// <summary>回滚探针专用异常类型：与被断言的回滚点一一对应，避免与其它 IO 异常混型假绿。</summary>
    private sealed class ProbeRollbackException(string message) : Exception(message);

    // ═══ ① 空转档（spec/12 §12.4 档 3）═══

    [Fact]
    public async Task T145_1_IdleLease_SameConnVisibleBeforeCommit()
    {
        await EnsureProbeAsync();
        await CleanProbeAsync(ProbeMarkerLike);
        var marker = "T1CS-tx145-idle";
        long sameConn = -1, otherConn = -1;
        try
        {
            await Assert.ThrowsAsync<ProbeRollbackException>(() =>
                _fx.Tx.ExecuteInTxAsync<object?>(async () =>
                {
                    var amb = _fx.Repo.CurrentAmbientTx;
                    Assert.NotNull(amb);
                    await InsertProbeAsync(amb!.Value.Connection, amb.Value.Transaction, 1, marker);
                    // 提交前：同一事务连接读得到（若仓储/模板其实每句自取新连接，此处即红）
                    sameConn = await CountProbeOnAsync(amb.Value.Connection, amb.Value.Transaction, marker);
                    // 提交前：另一条独立连接读不到（未提交行）
                    await using var other = await _fx.Factory.OpenAsync();
                    otherConn = await CountProbeOnAsync(other, null, marker);
                    throw new ProbeRollbackException("idle-rollback-probe");
                }));
            Assert.Equal(1L, sameConn);
            Assert.Equal(0L, otherConn);
            Assert.Equal(0L, await CountProbeAsync(marker)); // 回滚后不存在
        }
        finally
        {
            await CleanProbeAsync(marker);
        }
    }

    // ═══ ② 归属档：嵌套并入（spec/12 §12.2 归属面）═══

    [Fact]
    public async Task T145_2_Ownership_NestedJoinsOuter()
    {
        await EnsureProbeAsync();
        await CleanProbeAsync(ProbeMarkerLike);
        var marker = "T1CS-tx145-nested";
        DbConnection? outerConn = null, innerConn = null;
        DbTransaction? outerTx = null, innerTx = null;
        try
        {
            await Assert.ThrowsAsync<ProbeRollbackException>(() =>
                _fx.Tx.ExecuteInTxAsync<object?>(async () =>
                {
                    var outer = _fx.Repo.CurrentAmbientTx!.Value;
                    outerConn = outer.Connection;
                    outerTx = outer.Transaction;
                    // 内层再进一次（模拟门面级包裹 → 引擎入口包裹的嵌套）：必须并入最外层
                    await _fx.Tx.ExecuteInTxAsync<object?>(async () =>
                    {
                        var inner = _fx.Repo.CurrentAmbientTx!.Value;
                        innerConn = inner.Connection;
                        innerTx = inner.Transaction;
                        await InsertProbeAsync(inner.Connection, inner.Transaction, 2, marker);
                        return null;
                    });
                    // 内层回调返回（"提交"语义）后：并入判定 ⇒ 行仍在外层事务上可见（未提交）
                    Assert.Equal(1L, await CountProbeOnAsync(outer.Connection, outer.Transaction, marker));
                    throw new ProbeRollbackException("outer-rollback-probe");
                }));
            Assert.Same(outerConn, innerConn); // 嵌套不另开连接
            Assert.Same(outerTx, innerTx);     // 不重复 BEGIN（同一事务对象）
            Assert.Equal(0L, await CountProbeAsync(marker)); // 外层回滚 ⇒ 内层写同等消失（无独立提交）
        }
        finally
        {
            await CleanProbeAsync(marker);
        }
    }

    // ═══ ② 归属档：并发作用域互相隔离（spec/12 §12.2 归属面 / §12.4 档 2）═══

    [Fact]
    public async Task T145_3_Ownership_ConcurrentScopesIsolated()
    {
        await EnsureProbeAsync();
        await CleanProbeAsync(ProbeMarkerLike);
        var markerA = "T1CS-tx145-iso-a";
        var markerB = "T1CS-tx145-iso-b";
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        DbConnection? connA = null, connB = null;
        string? errA = null, errB = null;
        try
        {
            // A：写 → 让出 → 抛错回滚（独立 async 作用域）
            var ta = Task.Run(async () =>
            {
                try
                {
                    await _fx.Tx.ExecuteInTxAsync<object?>(async () =>
                    {
                        var amb = _fx.Repo.CurrentAmbientTx!.Value;
                        connA = amb.Connection;
                        await InsertProbeAsync(amb.Connection, amb.Transaction, 11, markerA);
                        entered.SetResult(true);
                        await release.Task; // 让出窗口
                        throw new ProbeRollbackException("A-rollback-probe");
                    });
                }
                catch (ProbeRollbackException e)
                {
                    errA = e.Message;
                }
            });
            await entered.Task; // A 已写、已让出

            // B：在 A 的让出窗口里写自己的行并成功提交
            var tb = Task.Run(async () =>
            {
                try
                {
                    await _fx.Tx.ExecuteInTxAsync<object?>(async () =>
                    {
                        var amb = _fx.Repo.CurrentAmbientTx!.Value;
                        connB = amb.Connection;
                        await InsertProbeAsync(amb.Connection, amb.Transaction, 12, markerB);
                        return null;
                    });
                }
                catch (Exception e)
                {
                    errB = e.Message;
                }
            });
            await tb;
            release.SetResult(true);
            await ta;

            Assert.Contains("A-rollback-probe", errA);
            Assert.Null(errB);
            Assert.NotSame(connA, connB); // 跨作用域各持各的连接（禁止进程级槽）
            Assert.Equal(0L, await CountProbeAsync(markerA)); // A 的行不在
            Assert.Equal(1L, await CountProbeAsync(markerB)); // B 的行在
        }
        finally
        {
            await CleanProbeAsync(markerA);
            await CleanProbeAsync(markerB);
        }
    }

    // ═══ ③ 原子档：门面复合动作中途失败整体回滚（＋裸拼对照）═══

    [Fact]
    public async Task T145_4_AtomicFacadeComposite_WithUnwiredControl()
    {
        var defineId = await _fx.SaveT1DefineAsync("tx145-atomic");
        var markerWired = "T1CS-tx145-atomic-wired";
        var markerNaked = "T1CS-tx145-atomic-naked";
        try
        {
            // (a) 挂模板：复合动作（发起三拍）在第二拍（第一拍已过、实例+任务已写）失败
            //     ⇒ 门面级包裹把整次 dispatch 收进事务 ⇒ 半完成不许留存。
            var repoA = new FlakyRepository(_fx.Factory, failAt: 2);
            var stackA = NewStack(repoA, wired: true);
            var res = await stackA.Facade.FlowAsync("processInstance/startAndExecute",
                StartArgs(defineId, markerWired));
            Assert.NotEqual("成功", res["msg"]?.ToString());          // 失败信封（信封在事务外生成）
            Assert.Equal(0L, await CountInstanceByMarkerAsync(markerWired)); // 全部写入回滚净

            // (b) 对照（未挂模板＝修前裸拼态）：同一注入点 ⇒ 第一拍已提交、第二拍回滚 ⇒ 半完成留存
            var repoB = new FlakyRepository(_fx.Factory, failAt: 2);
            var stackB = NewStack(repoB, wired: false);
            await stackB.Facade.FlowAsync("processInstance/startAndExecute",
                StartArgs(defineId, markerNaked));
            Assert.True(await CountInstanceByMarkerAsync(markerNaked) >= 1,
                "未挂模板的裸拼态应留半完成（若此处为 0，说明注入没生效，测试装置失效）");
        }
        finally
        {
            await _fx.CleanupByMarkerAsync(markerWired);
            await _fx.CleanupByMarkerAsync(markerNaked);
        }
    }

    // ═══ ④ persist 写腿（C99 ⑥）：writer 并入动作事务 ═══

    [Fact]
    public async Task T145_5_PersistWriterJoinsActionTx()
    {
        await EnsureProbeAsync();
        await CleanProbeAsync(ProbeMarkerLike);
        var markerWired = "T1CS-tx145-persist-wired";
        var markerNaked = "T1CS-tx145-persist-naked";
        try
        {
            // (a) 接线：工厂换成"一调即抛" ⇒ writer 若仍自开连接会抛 InvalidOperationException（≠探针异常 ⇒ 红）
            var writer = new DbDynamicTableWriter(new ThrowingFactory())
            {
                AmbientTxProvider = () => _fx.Repo.CurrentAmbientTx is { } a
                    ? new AmbientTxConnection(a.Connection, a.Transaction)
                    : null,
            };
            await Assert.ThrowsAsync<ProbeRollbackException>(() =>
                _fx.Tx.ExecuteInTxAsync<object?>(async () =>
                {
                    await writer.InsertAsync(ProbeTable,
                        new Dictionary<string, object?> { ["id"] = 21L, ["marker"] = markerWired });
                    throw new ProbeRollbackException("persist-rollback-probe");
                }));
            Assert.Equal(0L, await CountProbeAsync(markerWired)); // 随事务回滚

            // (b) 对照（未接线＝允许运行态，spec/12 §12.6）：独立连接自提交 ⇒ 事务回滚不影响它
            var writer2 = new DbDynamicTableWriter(new RealFactory(_fx.Factory));
            await Assert.ThrowsAsync<ProbeRollbackException>(() =>
                _fx.Tx.ExecuteInTxAsync<object?>(async () =>
                {
                    await writer2.InsertAsync(ProbeTable,
                        new Dictionary<string, object?> { ["id"] = 22L, ["marker"] = markerNaked });
                    throw new ProbeRollbackException("persist-naked-rollback");
                }));
            Assert.Equal(1L, await CountProbeAsync(markerNaked)); // 独立提交已落库（未接线形态）
        }
        finally
        {
            await CleanProbeAsync(markerWired);
            await CleanProbeAsync(markerNaked);
        }
    }

    // ═══ ⑤ 门评估（145-4）：无命令互斥时并发办理的读数 ═══

    [Fact]
    public async Task T145_6_GateEvaluation_Readout()
    {
        var defineId = await _fx.SaveT1DefineAsync("tx145-gate");
        // 第二个引擎实例 = 令命令门（SemaphoreSlim）互不生效的等效手段（不摘产品代码）
        var twin = new JeeflowEngine(_fx.Ctx);
        const int runs = 3;
        var doubleWins = 0;
        var perRound = new List<string>();
        try
        {
            for (var i = 1; i <= runs; i++)
            {
                var marker = $"T1CS-tx145-gate-{i}";
                var inst = await _fx.Engine.StartProcessInstanceByIdAsync(defineId, "applicant",
                    new FlowData { [FlowConst.BusinessNo] = marker });
                var applyC = await FindDoingAsync(inst.InstanceId!.Value, "applicant");
                await _fx.Engine.ExecuteProcessTaskAsync(applyC.TaskId!.Value, "applicant", new FlowData());
                var task = await FindDoingAsync(inst.InstanceId.Value, "leader");
                var results = await Task.WhenAll(
                    ProbeAsync(_fx.Engine, task.TaskId!.Value),
                    ProbeAsync(twin, task.TaskId!.Value));
                var done = results.Count(ok => ok);
                if (done >= 2) doubleWins++;
                perRound.Add($"r{i}:{done}/2");
                Assert.True(done >= 1, $"round {i}: 两拍全失败（并发把两边都搞坏，属异常读数）");
            }
        }
        finally
        {
            await _fx.CleanupByMarkerAsync("T1CS-tx145-gate");
        }
        Console.WriteLine($"[tx145-gate] runs={runs} doubleWins={doubleWins} :: {string.Join(",", perRound)}");
        Console.WriteLine(doubleWins > 0
            ? "[tx145-gate] 结论：无命令互斥时出现同一任务双成功 ⇒ 门（SemaphoreSlim）是并发恰一次的必要守卫，保留"
            : "[tx145-gate] 结论：本读数未观测到双成功（需人工复核门可否降级）");
    }

    // ═══ ③ 原子档：引擎入口自包（spec/12 §12.1 条 3 第一形态：宿主不外包时入口内包）═══

    [Fact]
    public async Task T145_7_EngineEntryWrapsWhenWired()
    {
        var defineId = await _fx.SaveT1DefineAsync("tx145-entry");
        var marker = "T1CS-tx145-entry";
        try
        {
            var repo = new FlakyRepository(_fx.Factory); // failAt=1：首拍内（任务已写、实例 update）失败
            var stack = NewStack(repo, wired: true);
            var engine = new JeeflowEngine(stack.Ctx);
            await Assert.ThrowsAsync<ProbeRollbackException>(() =>
                engine.StartProcessInstanceByIdAsync(defineId, "applicant",
                    new FlowData { [FlowConst.BusinessNo] = marker }));
            Assert.Equal(0L, await CountInstanceByMarkerAsync(marker)); // 入口自包 ⇒ 中途失败全回滚
        }
        finally
        {
            await _fx.CleanupByMarkerAsync(marker);
        }
    }

    // ═══ ② 归属档补充：环境事务下 ext 仓储命令必须绑定事务（R9-csharp 二轮根因 ①）═══

    [Fact]
    public async Task T145_8_ExtRepoCommandsBindAmbientTx_DesignRemove()
    {
        var stack = NewStack(new MySqlRepository(_fx.Factory), wired: true);
        long d1 = -1, d2 = -1;
        try
        {
            d1 = await SaveDesignAsync(stack.Facade, "T1CS-tx145-rm-1");
            d2 = await SaveDesignAsync(stack.Facade, "T1CS-tx145-rm-2");
            // 修前形状：RemoveDesignAsync 直接 `new MySqlCommand`（不绑 AmbientTx）⇒ 环境事务下
            // MySqlConnector 抛「The transaction associated with this command is not the
            // connection's active transaction」⇒ 门面转 99999999 失败信封（本用例红）。
            var rm = await stack.Facade.FlowAsync("processDesign/remove",
                new FlowData { ["ids"] = new List<object?> { d1 }, ["id"] = d2 });
            Assert.Equal(0, rm["code"]);
            Assert.Equal(0L, await CountDesignAsync(d1)); // ids 优先：点名那条真删掉
            Assert.Equal(1L, await CountDesignAsync(d2)); // id 那条不受牵连（ids 优先语义顺带守卫）
        }
        finally
        {
            if (d1 > 0) await _fx.ExtRepo.RemoveDesignAsync(d1);
            if (d2 > 0) await _fx.ExtRepo.RemoveDesignAsync(d2);
        }
    }

    private static async Task<long> SaveDesignAsync(JeeflowFacade facade, string name)
    {
        var save = await facade.FlowAsync("processDesign/save", new FlowData
        {
            ["name"] = name,
            ["displayName"] = name,
            ["type"] = "6",
            ["icon"] = "",
            ["remark"] = "r9-csharp tier",
        });
        Assert.Equal(0, save["code"]);
        return Convert.ToInt64(((Dictionary<string, object?>)save["data"]!)["id"]);
    }

    private async Task<long> CountDesignAsync(long id)
    {
        await using var conn = await _fx.Factory.OpenAsync();
        await using var cmd = new MySqlCommand("SELECT COUNT(*) FROM wf_process_design WHERE id=?", conn);
        cmd.Parameters.Add(new MySqlParameter { Value = id });
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    // ═══ 测试装置 ═══

    private static async Task<bool> ProbeAsync(JeeflowEngine engine, long taskId)
    {
        try
        {
            await engine.ExecuteProcessTaskAsync(taskId, "leader", new FlowData());
            return true;
        }
        catch (JeeflowException)
        {
            return false;
        }
    }

    private async Task<ProcessTask> FindDoingAsync(long instanceId, string actor)
    {
        foreach (var t in await _fx.Repo.FindDoingTasksAsync(instanceId, Array.Empty<string>()))
        {
            if (t.ActorIds?.Contains(actor) == true) return t;
        }
        throw new Xunit.Sdk.XunitException($"未找到 {actor} 的待办（instance={instanceId}）");
    }

    private static FlowData StartArgs(long defineId, string businessNo) => new()
    {
        [FlowConst.ProcessDefineIdKey] = defineId,
        ["operator"] = "applicant",
        [FlowConst.BusinessNo] = businessNo,
    };

    /// <summary>独立引擎栈（不动 fixture 的 Ctx 状态）；wired=false 即"未挂模板"的裸拼对照态。</summary>
    private (ServiceContext Ctx, JeeflowFacade Facade) NewStack(MySqlRepository repo, bool wired)
    {
        var ext = new MySqlExtRepository(_fx.Factory, repo);
        var ctx = new ServiceContext(repo, ext);
        ctx.Clock = new FixedClock(new DateTime(2026, 8, 1, 9, 0, 0));
        ctx.IdGenerator = new AtomicIdGenerator(2, ctx.Clock);
        ctx.UserProvider = new TestUserProvider();
        TestInfra.RegisterBuiltins(ctx);
        repo.Configure(ctx);
        if (wired) ctx.TransactionTemplate = new MySqlTransactionTemplate(_fx.Factory, repo, ext);
        return (ctx, new JeeflowFacade(ctx));
    }

    /// <summary>中途失败注入：第 <paramref name="failAt"/> 次 UpdateInstanceAsync 写完（半完成已发生）后抛探针异常。
    /// failAt=2 时注入点落在 startAndExecute 第二拍（第一拍已提交/已入外层事务）⇒ 复合动作跨拍原子性可观测。</summary>
    private sealed class FlakyRepository(MySqlConnectionFactory factory, int failAt = 1) : MySqlRepository(factory)
    {
        private int _remaining = failAt;

        public override async Task UpdateInstanceAsync(ProcessInstance instance)
        {
            await base.UpdateInstanceAsync(instance);
            if (Interlocked.Decrement(ref _remaining) == 0)
                throw new ProbeRollbackException("injected-mid-action-failure");
        }
    }

    /// <summary>一调即抛的连接工厂：接线正确时 writer 不应触碰它（写入走 ambient 连接）。</summary>
    private sealed class ThrowingFactory : IDbConnectionFactory
    {
        public Task<DbConnection> OpenAsync() =>
            throw new InvalidOperationException("writer 不应自开连接（ambient 已并入）");
    }

    private sealed class RealFactory(MySqlConnectionFactory inner) : IDbConnectionFactory
    {
        public async Task<DbConnection> OpenAsync() => await inner.OpenAsync();
    }

    // ═══ probe 表（非 sys_ 前缀；无二级索引避免间隙锁互扰；显式主键避免 AUTO-INC 争用）═══

    private async Task EnsureProbeAsync()
    {
        await using var conn = await _fx.Factory.OpenAsync();
        await using var cmd = (MySqlCommand)conn.CreateCommand();
        cmd.CommandText = $"CREATE TABLE IF NOT EXISTS {ProbeTable} " +
                          "(id BIGINT PRIMARY KEY, marker VARCHAR(64) NOT NULL) ENGINE=InnoDB";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertProbeAsync(DbConnection conn, DbTransaction? tx, long id, string marker)
    {
        await using var cmd = (MySqlCommand)conn.CreateCommand();
        if (tx != null) cmd.Transaction = (MySqlTransaction)tx;
        cmd.CommandText = $"INSERT INTO {ProbeTable} (id, marker) VALUES (@id, @m)";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@m", marker);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountProbeOnAsync(DbConnection conn, DbTransaction? tx, string marker)
    {
        await using var cmd = (MySqlCommand)conn.CreateCommand();
        if (tx != null) cmd.Transaction = (MySqlTransaction)tx;
        cmd.CommandText = $"SELECT COUNT(*) FROM {ProbeTable} WHERE marker = @m";
        cmd.Parameters.AddWithValue("@m", marker);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? 0L : Convert.ToInt64(v);
    }

    private async Task<long> CountProbeAsync(string marker)
    {
        await using var conn = await _fx.Factory.OpenAsync();
        return await CountProbeOnAsync(conn, null, marker);
    }

    private async Task CleanProbeAsync(string markerLike)
    {
        await using var conn = await _fx.Factory.OpenAsync();
        await using var cmd = (MySqlCommand)conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {ProbeTable} WHERE marker LIKE @m";
        cmd.Parameters.AddWithValue("@m", markerLike);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountInstanceByMarkerAsync(string businessNo)
    {
        await using var conn = await _fx.Factory.OpenAsync();
        await using var cmd = (MySqlCommand)conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM wf_process_instance WHERE business_no = @b";
        cmd.Parameters.AddWithValue("@b", businessNo);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? 0L : Convert.ToInt64(v);
    }
}

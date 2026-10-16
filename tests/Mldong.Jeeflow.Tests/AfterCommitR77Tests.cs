using System.Threading;
using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Repository.MySql;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// audit8 R77 读法一（csharp 腿·引擎侧通道）：可选"提交后腿"通道。
///
/// <para>契约下限＝"监听器看到的世界＝已提交"由<b>监听器侧</b>承担（owner 10-16 拍；
/// spec12 §12.3.1／issues/234 §4.3）。参考实现 java＝事务内 fire＋监听器自取 afterCommit，
/// php 已由 R25（issues/191）收口同形。⇒ <c>JeeflowEngine.RunAfterCommitAsync</c> 只在模板
/// 真接了通道且当前有活动事务时才登记，否则立即执行；<b>fire 的派发点一行没动</b>
/// （go 侧那一版把整批派发无条件挪到提交后，被 145-3 那条"监听器随事件拿动作事务连接"的
/// 既有判据当场照红，故本栈一律走"可选通道"）。</para>
///
/// <para>真库那一头（MySqlTransactionTemplate 的 scope 装配/提交后跑/回滚丢弃）在
/// <see cref="MySqlAfterCommitR77Tests"/>，同一个文件里分两个类，别让单测依赖 160。</para>
/// </summary>
public class AfterCommitR77Tests
{
    /// <summary>只实现 ExecuteInTxAsync 的旧形状模板 ⇒ DIM 默认返回 false ⇒ 引擎必须立即执行。</summary>
    private sealed class MinimalTemplate : ITransactionTemplate
    {
        public async Task<T> ExecuteInTxAsync<T>(Func<Task<T>> op) => await op();
    }

    /// <summary>带登记表的脚本化模板：与 MySqlTransactionTemplate 同一套 scope 语义，不碰数据库。</summary>
    private sealed class ScriptedTemplate : ITransactionTemplate
    {
        private readonly AsyncLocal<AfterCommitScope?> _ambient = new();
        public bool InTx;

        public async Task<T> ExecuteInTxAsync<T>(Func<Task<T>> op)
        {
            if (InTx) return await op(); // join 支：登记归最外层那张表
            InTx = true;
            var scope = new AfterCommitScope();
            var prev = _ambient.Value;
            _ambient.Value = scope;
            var ok = false;
            try
            {
                var r = await op();
                ok = true;
                return r;
            }
            catch
            {
                scope.Discard();
                throw;
            }
            finally
            {
                _ambient.Value = prev;
                InTx = false;
                if (ok) await scope.FireAsync();
            }
        }

        public Task<bool> TryAfterCommitAsync(Func<Task> callback)
            => Task.FromResult(_ambient.Value?.TryAdd(callback) ?? false);
    }

    private static JeeflowEngine EngineWith(ITransactionTemplate? tx)
    {
        var (engine, _, ctx) = TestInfra.NewEngineWithCtx();
        ctx.TransactionTemplate = tx;
        return engine;
    }

    [Fact]
    public async Task R7701TemplateWithoutChannelRunsImmediately()
    {
        var engine = EngineWith(new MinimalTemplate());
        var ran = new List<string>();
        await engine.InTxAsync(async () =>
        {
            await engine.RunAfterCommitAsync(() => { ran.Add("leg"); return Task.CompletedTask; });
            ran.Add("body");
            return 1;
        });
        // 模板没这条通道 ⇒ 腿在事务内就跑了＝上一版行为逐字不变
        Assert.Equal(new[] { "leg", "body" }, ran);
    }

    [Fact]
    public async Task R7702DeferredUntilCommitAndRunsOnce()
    {
        var engine = EngineWith(new ScriptedTemplate());
        var ran = new List<string>();
        await engine.InTxAsync(async () =>
        {
            await engine.RunAfterCommitAsync(() => { ran.Add("leg"); return Task.CompletedTask; });
            Assert.Empty(ran);      // 提交前不得跑
            ran.Add("body");
            return 1;
        });
        Assert.Equal(new[] { "body", "leg" }, ran);
    }

    [Fact]
    public async Task R7703RollbackDiscardsAllLegs()
    {
        var engine = EngineWith(new ScriptedTemplate());
        var ran = new List<string>();

        async Task<int> Body()
        {
            await engine.RunAfterCommitAsync(() => { ran.Add("a"); return Task.CompletedTask; });
            await engine.RunAfterCommitAsync(() => { ran.Add("b"); return Task.CompletedTask; });
            throw new InvalidOperationException("injected: R77 回滚探针");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.InTxAsync(Body));
        Assert.Empty(ran); // 回滚 ⇒ 整批丢弃（修前无通道＝这两条腿已经在事务内跑过了）
    }

    [Fact]
    public async Task R7704NoTemplateRunsImmediatelyAndNestedJoinsOuterScope()
    {
        // ① 门面级没接模板 ⇒ "落库后"与"提交后"等价 ⇒ 立即执行
        var bare = EngineWith(null);
        var ran = new List<string>();
        await bare.RunAfterCommitAsync(() => { ran.Add("bare"); return Task.CompletedTask; });
        Assert.Equal(new[] { "bare" }, ran);

        // ② 嵌套：内层 ExecuteInTxAsync 走 join 支 ⇒ 登记归最外层那张表，内层早退不触发
        var tx = new ScriptedTemplate();
        var engine = EngineWith(tx);
        var legs = new List<string>();
        await engine.InTxAsync(async () =>
        {
            await engine.InTxAsync(async () =>
            {
                await engine.RunAfterCommitAsync(() => { legs.Add("inner"); return Task.CompletedTask; });
                return 1;
            });
            Assert.Empty(legs);
            return 2;
        });
        Assert.Equal(new[] { "inner" }, legs);
    }
}

/// <summary>
/// R77 csharp 腿·真库一侧：<c>MySqlTransactionTemplate</c> 的 scope 装配——提交后腿在 COMMIT
/// 之后跑（跑在连接已解绑之后）、回滚整批丢弃。测的是 160 上的真事务，不需要流程定义。
/// </summary>
[Collection("mysql")]
[Trait("Category", "mysql-smoke")]
public class MySqlAfterCommitR77Tests
{
    private readonly MySqlFixture _fx;
    public MySqlAfterCommitR77Tests(MySqlFixture fx) => _fx = fx;

    [Fact]
    public async Task R7705MySqlTemplateFiresOnCommitAndDiscardsOnRollback()
    {
        var ran = new List<string>();

        // 回滚腿：登记两条 + 注入异常 ⇒ 整批丢弃
        await Assert.ThrowsAsync<InvalidOperationException>(() => _fx.Tx.ExecuteInTxAsync<int>(async () =>
        {
            Assert.True(await _fx.Tx.TryAfterCommitAsync(() => { ran.Add("a"); return Task.CompletedTask; }));
            Assert.True(await _fx.Tx.TryAfterCommitAsync(() => { ran.Add("b"); return Task.CompletedTask; }));
            throw new InvalidOperationException("injected: R77 真库回滚探针");
        }));
        Assert.Empty(ran);

        // 提交腿：同样形状正常返回 ⇒ COMMIT 后恰好各跑一次
        await _fx.Tx.ExecuteInTxAsync(async () =>
        {
            Assert.True(await _fx.Tx.TryAfterCommitAsync(() => { ran.Add("c"); return Task.CompletedTask; }));
            Assert.Empty(ran); // 提交前不得跑
            return 1;
        });
        Assert.Equal(new[] { "c" }, ran);

        // 不在事务中 ⇒ 必须报"没登记上"，由调用方自行立即执行（引擎那一侧就是这条支）
        Assert.False(await _fx.Tx.TryAfterCommitAsync(() => Task.CompletedTask));
    }

    [Fact]
    public async Task R7706EngineChannelOnMySqlTemplateRunsAfterCommit()
    {
        _fx.Ctx.TransactionTemplate = _fx.Tx;
        var ran = new List<string>();
        await _fx.Engine.InTxAsync(async () =>
        {
            await _fx.Engine.RunAfterCommitAsync(() => { ran.Add("leg"); return Task.CompletedTask; });
            Assert.Empty(ran);
            return 1;
        });
        Assert.Equal(new[] { "leg" }, ran);
    }
}

using Mldong.Jeeflow.Core;
using MySqlConnector;
using System.Threading;

namespace Mldong.Jeeflow.Repository.MySql;

/// <summary>
/// 事务模板真实现（方案 §2.3）：checkout 单连接 BeginTransactionAsync → 回调共用连接
/// （AsyncLocal 环境连接，仓储全部方法透明走同一连接）→ commit / rollback。
/// 契约不变量（spec/05）：同事务内所有仓储方法同一连接；仓储签名不带事务参数。
/// </summary>
public sealed class MySqlTransactionTemplate : ITransactionTemplate
{
    private readonly MySqlConnectionFactory _factory;
    private readonly MySqlRepository _repository;
    private readonly MySqlExtRepository? _extRepository;

    /// <summary>提交后腿登记表的 ambient（audit8 R77）：与连接绑定分离，最外层事务建一张表。</summary>
    private readonly AsyncLocal<AfterCommitScope?> _ambientScope = new();

    public MySqlTransactionTemplate(
        MySqlConnectionFactory factory, MySqlRepository repository, MySqlExtRepository? extRepository = null)
    {
        _factory = factory;
        _repository = repository;
        _extRepository = extRepository;
    }

    public async Task<T> ExecuteInTxAsync<T>(Func<Task<T>> op)
    {
        // audit8 R9（spec/12 §12.2 归属面）：嵌套必须并入最外层——已在事务作用域内（门面整次
        // dispatch 已包一层 + 引擎入口又包一层）直接执行回调，不另开连接、不重复 BEGIN；
        // 反向语义（required）同样成立：内层失败随外层一起回滚，而不是被"已有事务"拒绝。
        if (_repository.AmbientConn.Value != null) return await op();
        await using var conn = await _factory.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var prev = _repository.AmbientConn.Value;
        var prevTx = _repository.AmbientTx.Value;
        var prevScope = _ambientScope.Value;
        var scope = new AfterCommitScope();
        _repository.AmbientConn.Value = conn;
        _repository.AmbientTx.Value = (MySqlTransaction)tx;
        _ambientScope.Value = scope;
        var committed = false;
        T result;
        try
        {
            result = await op();
            await tx.CommitAsync();
            committed = true;
        }
        catch
        {
            await tx.RollbackAsync();
            scope.Discard(); // audit8 R77：回滚 ⇒ 提交后腿整批丢弃（监听器不得看到未提交的世界）
            throw;
        }
        finally
        {
            _repository.AmbientConn.Value = prev;
            _repository.AmbientTx.Value = prevTx;
            _ambientScope.Value = prevScope;
        }
        // 提交后腿跑在**连接已解绑**之后（反查走池连接，不踩在刚 COMMIT 的连接上）——
        // 放在 finally 之后而不是 try 内，就是为了这一条。
        if (committed) await scope.FireAsync();
        return result;
    }

    /// <summary>
    /// 提交后腿通道（audit8 R77 读法一）：有活动事务 ⇒ 登记并返回 true；
    /// 无活动事务（或表已 done）⇒ 返回 false，调用方须自行立即执行。
    /// 嵌套 ExecuteInTxAsync 走上面的 join 支早退 ⇒ 登记始终归最外层那张表。
    /// </summary>
    public Task<bool> TryAfterCommitAsync(Func<Task> callback)
    {
        var scope = _ambientScope.Value;
        return Task.FromResult(scope != null && scope.TryAdd(callback));
    }
}

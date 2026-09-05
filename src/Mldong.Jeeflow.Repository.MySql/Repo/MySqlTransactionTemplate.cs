using Mldong.Jeeflow.Core;
using MySqlConnector;

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

    public MySqlTransactionTemplate(
        MySqlConnectionFactory factory, MySqlRepository repository, MySqlExtRepository? extRepository = null)
    {
        _factory = factory;
        _repository = repository;
        _extRepository = extRepository;
    }

    public async Task<T> ExecuteInTxAsync<T>(Func<Task<T>> op)
    {
        await using var conn = await _factory.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var prev = _repository.AmbientConn.Value;
        var prevTx = _repository.AmbientTx.Value;
        _repository.AmbientConn.Value = conn;
        _repository.AmbientTx.Value = (MySqlTransaction)tx;
        try
        {
            var result = await op();
            await tx.CommitAsync();
            return result;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
        finally
        {
            _repository.AmbientConn.Value = prev;
            _repository.AmbientTx.Value = prevTx;
        }
    }
}

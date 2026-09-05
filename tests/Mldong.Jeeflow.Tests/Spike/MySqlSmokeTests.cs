using Xunit;
using Mldong.Jeeflow.Repository.MySql;
using MySqlConnector;

namespace Mldong.Jeeflow.Tests.Spike;

/// <summary>
/// M0 spike ②：MySqlConnector 连 160:3306——建表 + 1 查询 + 事务 begin/commit/rollback。
/// R6：只动 jeeflow 库 wf_* 表外的临时 spike 表（9xxxxx 段命名 + 测后自清理）；
/// SKIP_MYSQL=1 开发机跳过；凭据只走 JEFFLOW_DB_* env。
/// </summary>
[Trait("Category", "mysql-smoke")]
public class MySqlSmokeTests
{
    private static bool SkipMySql =>
        Environment.GetEnvironmentVariable("SKIP_MYSQL") == "1";

    private const string SpikeTable = "wf_csharp_spike_9xxxxx";

    [Fact]
    public async Task Connect_CreateTable_Query_Transaction()
    {
        if (SkipMySql) return; // SKIP_MYSQL=1：开发机跳过（发版机 REQUIRE_MYSQL 口径见 M2）
        var factory = MySqlConnectionFactory.FromEnv();

        // 1. 连接 + 建表
        await using var conn = await factory.OpenAsync();
        Assert.Equal(System.Data.ConnectionState.Open, conn.State);

        await using (var cmd = new MySqlCommand(
            $@"CREATE TABLE IF NOT EXISTS {SpikeTable} (
                 id BIGINT NOT NULL PRIMARY KEY,
                 name VARCHAR(64) NULL,
                 num INT NULL
               ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        // 2. 1 查询（插入后回读）
        var testId = 9000000000000001L;
        await using (var cmd = new MySqlCommand(
            $"INSERT INTO {SpikeTable} (id, name, num) VALUES (9000000000000001, 'spike', 42) " +
            "ON DUPLICATE KEY UPDATE name=VALUES(name), num=VALUES(num)", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = new MySqlCommand(
            $"SELECT name, num FROM {SpikeTable} WHERE id = {testId}", conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("spike", reader.GetString(0));
            Assert.Equal(42, reader.GetInt32(1));
        }

        // 3. 事务 begin/commit
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await using (var cmd = new MySqlCommand(
                $"UPDATE {SpikeTable} SET num = 100 WHERE id = {testId}", conn, (MySqlTransaction)tx))
            {
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }
        await using (var cmd = new MySqlCommand($"SELECT num FROM {SpikeTable} WHERE id = {testId}", conn))
        {
            Assert.Equal(100, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
        }

        // 4. 事务 rollback（半完成必须回滚净）
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await using (var cmd = new MySqlCommand(
                $"UPDATE {SpikeTable} SET num = 999 WHERE id = {testId}", conn, (MySqlTransaction)tx))
            {
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.RollbackAsync();
        }
        await using (var cmd = new MySqlCommand($"SELECT num FROM {SpikeTable} WHERE id = {testId}", conn))
        {
            Assert.Equal(100, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
        }

        // 5. 清理（R6 测后自清理）
        await using (var cmd = new MySqlCommand($"DELETE FROM {SpikeTable} WHERE id = {testId}", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = new MySqlCommand($"DROP TABLE IF EXISTS {SpikeTable}", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

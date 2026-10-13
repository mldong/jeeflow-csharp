using System.Data.Common;
using Xunit;
using MySqlConnector;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Persist;
using Mldong.Jeeflow.Repository.MySql;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/192（audit8 队列 R26）· csharp <c>StrictColumnMatch</c> 门控不完备——真库判据（160）。
/// spec09:96「显式开启后驼峰不再匹配」同时约束列名匹配与 data 键→列反查两腿；修前本栈
/// <c>FindDataKey</c> 恒走归一（只有 <c>FindColumn</c> 读门控）⇒ 严格档下 INSERT/UPDATE 仍把
/// <c>companyName</c> 写进 <c>company_name</c>，与 java/go/py/node/php 五栈反向。
/// 三个调用点（插列匹配／非自增主键缺值判定／UPDATE 的 SET 列）各有正反两档，共六枚。
/// </summary>
[Collection("mysql")]
[Trait("Category", "mysql-smoke")]
public sealed class MySqlStrictColumnMatch192Tests(MySqlFixture fx) : IAsyncLifetime
{
    private const string Table = "wf_csharp_r26_strict";
    private const string PkTable = "wf_csharp_r26_pk";

    private readonly MySqlConnectionFactory _factory = fx.Factory;

    public async Task InitializeAsync()
    {
        // 夹具表＝本卡自建、命名明确（wf_csharp_r26_*），DisposeAsync 整表 DROP
        await ExecAsync($"CREATE TABLE IF NOT EXISTS {Table} (id INT AUTO_INCREMENT PRIMARY KEY, " +
                        "company_name VARCHAR(64) NULL, remark VARCHAR(64) NULL)");
        await ExecAsync($"CREATE TABLE IF NOT EXISTS {PkTable} (user_id INT NOT NULL PRIMARY KEY, " +
                        "remark VARCHAR(64) NULL)");
    }

    public async Task DisposeAsync()
    {
        await ExecAsync($"DROP TABLE IF EXISTS {Table}");
        await ExecAsync($"DROP TABLE IF EXISTS {PkTable}");
    }

    private DbDynamicTableWriter NewWriter(bool strict) =>
        new(new ConnFactory(_factory)) { StrictColumnMatch = strict };

    private sealed class ConnFactory(MySqlConnectionFactory inner) : IDbConnectionFactory
    {
        public async Task<DbConnection> OpenAsync() => await inner.OpenAsync();
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(string? CompanyName, string? Remark)> ReadRowAsync(long id)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = new MySqlCommand(
            $"SELECT company_name, remark FROM {Table} WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        await using var rs = await cmd.ExecuteReaderAsync();
        Assert.True(await rs.ReadAsync(), $"业务行应存在（id={id}）");
        return (rs.IsDBNull(0) ? null : rs.GetString(0), rs.IsDBNull(1) ? null : rs.GetString(1));
    }

    private async Task<long> InsertAsync(DbDynamicTableWriter writer, IDictionary<string, object?> data)
    {
        await ExecAsync($"DELETE FROM {Table}");
        var id = await writer.InsertAsync(Table, data);
        Assert.NotNull(id);
        return Convert.ToInt64(id);
    }

    [Fact]
    public async Task R192_StrictInsert_CamelKey_UnderscoreColumnNotWritten()
    {
        var id = await InsertAsync(NewWriter(true), new Dictionary<string, object?>
        {
            ["companyName"] = "驼峰不该落",
            ["remark"] = "精确列照落",
        });

        var (companyName, remark) = await ReadRowAsync(id);
        Assert.Null(companyName);              // 修前＝"驼峰不该落"（门控漏收）
        Assert.Equal("精确列照落", remark);
    }

    [Fact]
    public async Task R192_LenientInsert_CamelKey_StillMatches()
    {
        var id = await InsertAsync(NewWriter(false), new Dictionary<string, object?>
        {
            ["companyName"] = "宽松档照落",
        });

        var (companyName, _) = await ReadRowAsync(id);
        Assert.Equal("宽松档照落", companyName); // 默认档零变化
    }

    [Fact]
    public async Task R192_StrictInsert_CaseInsensitiveExactKey_StillMatches()
    {
        var id = await InsertAsync(NewWriter(true), new Dictionary<string, object?>
        {
            ["COMPANY_NAME"] = "同名异大小写照落",
        });

        var (companyName, _) = await ReadRowAsync(id);
        Assert.Equal("同名异大小写照落", companyName); // 严格＝忽略大小写精确，不是全不匹配
    }

    [Fact]
    public async Task R192_StrictUpdate_CamelKey_NoSetColumnAndValuePreserved()
    {
        var id = await InsertAsync(NewWriter(false), new Dictionary<string, object?>
        {
            ["company_name"] = "原值",
        });

        var rows = await NewWriter(true).UpdateAsync(
            Table, new Dictionary<string, object?> { ["companyName"] = "驼峰不该改" }, "id", id);
        Assert.Equal(0, rows);                  // 修前＝1
        var (companyName, _) = await ReadRowAsync(id);
        Assert.Equal("原值", companyName);        // 修前＝"驼峰不该改"
    }

    [Fact]
    public async Task R192_LenientUpdate_CamelKey_StillUpdates()
    {
        var id = await InsertAsync(NewWriter(false), new Dictionary<string, object?>
        {
            ["company_name"] = "原值",
        });

        var rows = await NewWriter(false).UpdateAsync(
            Table, new Dictionary<string, object?> { ["companyName"] = "宽松档照改" }, "id", id);
        Assert.Equal(1, rows);
        var (companyName, _) = await ReadRowAsync(id);
        Assert.Equal("宽松档照改", companyName);
    }

    [Fact]
    public async Task R192_StrictInsert_NonAutoIncrementPk_CamelKey_FallsToGeneratorGuard()
    {
        await ExecAsync($"DELETE FROM {PkTable}");
        var ex = await Assert.ThrowsAsync<JeeflowException>(
            () => NewWriter(true).InsertAsync(PkTable, new Dictionary<string, object?>
            {
                ["userId"] = 777,
                ["remark"] = "非自增主键表",
            }));
        Assert.Contains("未配置主键生成器", ex.Message); // 修前＝驼峰命中主键、静默插入 777
        Assert.Equal(0, await CountAsync(PkTable));
    }

    private async Task<int> CountAsync(string table)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = new MySqlCommand($"SELECT COUNT(1) FROM {table}", conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}

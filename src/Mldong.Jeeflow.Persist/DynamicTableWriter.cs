using System.Data.Common;
using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Persist;

/// <summary>DB 连接工厂抽象（BCL System.Data.Common；MySqlConnector 连接即实现）。</summary>
public interface IDbConnectionFactory
{
    Task<DbConnection> OpenAsync();
}

/// <summary>表名安全（对齐 Java TableNames）：非空、非 sys_ 前缀、仅字母数字下划线。</summary>
public static class TableNames
{
    public const string SysPrefix = "sys_";

    public static void Validate(string? tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new JeeflowException("表名不能为空");
        var t = tableName.Trim();
        if (t.ToLowerInvariant().StartsWith(SysPrefix, StringComparison.Ordinal))
            throw new JeeflowException($"拒绝写入系统表: {t}");
        foreach (var c in t)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
                throw new JeeflowException($"表名含非法字符: {t}");
        }
    }
}

/// <summary>列元数据。</summary>
public sealed record ColumnMeta(string ColumnName, bool IsPrimaryKey, bool IsAutoIncrement);

/// <summary>
/// 动态表写入器接口（issues/18）：列过滤/参数化 INSERT/幂等检查/系统字段补充——引擎无关。
/// </summary>
public interface IDynamicTableWriter
{
    /// <summary>列过滤：返回目标表实际存在的列（输入字段 ∩ 表结构列，保序去重）。</summary>
    Task<List<string>> FilterColumnsAsync(string tableName, IEnumerable<string> columns);

    /// <summary>参数化 INSERT，返回主键（自增/生成器；无主键列返回 null）。</summary>
    Task<object?> InsertAsync(string tableName, IDictionary<string, object?> data);

    /// <summary>按条件列更新（SYNC 节点推进），条件列不参与 SET。返回更新行数。</summary>
    Task<int> UpdateAsync(string tableName, IDictionary<string, object?> data, string whereColumn, object? whereValue);

    /// <summary>幂等检查：bizKey 列 = bizKeyValue 的记录是否存在。</summary>
    Task<bool> ExistsAsync(string tableName, string bizKey, object? bizKeyValue);

    /// <summary>系统字段补充（未配置的列跳过；已存在的值不覆盖）。</summary>
    void FillSystemFields(IDictionary<string, object?> data, bool insert);
}

/// <summary>
/// ADO.NET 动态表写入默认实现（issues/18，零 ORM）：
/// information_schema.columns（schema 限定——C18 防多库同名表撞列）首次查询后缓存；
/// 参数化 INSERT 防注入；表名安全校验；列匹配宽松（驼峰↔下划线归一，issues/20）；
/// 主键：自增不生成、非自增且 data 无值且未配生成器 → 显式报错（C18，issues/21）；
/// 用户列默认值优先 data.apply_user_id（issues/19，C17 系统字段回落链）。
/// </summary>
public class DbDynamicTableWriter : IDynamicTableWriter
{
    private readonly IDbConnectionFactory _factory;
    private readonly Dictionary<string, List<ColumnMeta>> _schemaCache = new();
    private readonly object _cacheLock = new();

    private IClock? _clock;
    private IJsonProvider? _json;

    // 系统字段列约定（可配置；null = 不填充该列）
    public string? CreateTimeColumn { get; set; } = "create_time";
    public string? CreateUserColumn { get; set; } = "create_user";
    public string? UpdateTimeColumn { get; set; } = "update_time";
    public string? UpdateUserColumn { get; set; } = "update_user";
    public string? IsDeletedColumn { get; set; } = "is_deleted";
    /// <summary>用户列默认值（issues/19）；优先 data.apply_user_id。</summary>
    public object? DefaultUserValue { get; set; } = "system";
    /// <summary>列匹配（issues/20）：true=忽略大小写精确；false=驼峰↔下划线归一。</summary>
    public bool StrictColumnMatch { get; set; }
    /// <summary>主键生成器（issues/21）：非自增主键表插入时生成主键。</summary>
    public Func<string, object?>? PrimaryKeyGenerator { get; set; }

    public DbDynamicTableWriter(IDbConnectionFactory factory, IClock? clock = null, IJsonProvider? json = null)
    {
        _factory = factory;
        _clock = clock;
        _json = json;
    }

    private string Now => (_clock ?? SystemClock.Instance).Now.ToString("yyyy-MM-dd HH:mm:ss");

    // ── 写入器接口 ──

    public async Task<List<string>> FilterColumnsAsync(string tableName, IEnumerable<string> columns)
    {
        var meta = await TableMetaAsync(tableName);
        var result = new List<string>();
        foreach (var column in columns)
        {
            if (string.IsNullOrWhiteSpace(column)) continue;
            if (FindColumn(meta, column.Trim()) != null && !result.Contains(column.Trim()))
                result.Add(column.Trim());
        }
        return result;
    }

    public virtual async Task<object?> InsertAsync(string tableName, IDictionary<string, object?> data)
    {
        TableNames.Validate(tableName);
        var meta = await TableMetaAsync(tableName);
        var columns = new List<string>();
        var values = new List<object?>();
        foreach (var m in meta)
        {
            var col = m.ColumnName;
            var key = FindDataKey(data, col);
            if (key != null)
            {
                columns.Add(col);
                values.Add(data[key]);
            }
        }
        // 主键生成（issues/21/C18）：非自增主键且 data 无值 → 生成器；未配 → 显式报错
        object? generatedPk = null;
        foreach (var m in meta)
        {
            if (m.IsPrimaryKey && !m.IsAutoIncrement && FindDataKey(data, m.ColumnName) == null)
            {
                if (PrimaryKeyGenerator == null)
                    throw new JeeflowException(
                        $"表[{tableName}]主键[{m.ColumnName}]非自增且未配置主键生成器（请设置 PrimaryKeyGenerator，如雪花）");
                var pk = PrimaryKeyGenerator(tableName);
                columns.Add(m.ColumnName);
                values.Add(pk);
                if (generatedPk == null) generatedPk = pk;
            }
        }
        if (columns.Count == 0) return null;

        var sql = new System.Text.StringBuilder("INSERT INTO ").Append(tableName)
            .Append(" (").Append(string.Join(", ", columns)).Append(") VALUES (");
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0) sql.Append(", ");
            sql.Append('?');
        }
        sql.Append(')');

        await using var conn = await _factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql.ToString();
        foreach (var v in values) Bind(cmd, ToDbValue(v));
        await cmd.ExecuteNonQueryAsync();
        // 自增主键回取（MySQL LAST_INSERT_ID 语义按连接）
        var autoCol = meta.FirstOrDefault(m => m.IsPrimaryKey && m.IsAutoIncrement);
        if (autoCol != null)
        {
            await using var idCmd = conn.CreateCommand();
            idCmd.CommandText = "SELECT LAST_INSERT_ID()";
            var id = await idCmd.ExecuteScalarAsync();
            if (id != null && id != DBNull.Value) return id;
        }
        return generatedPk;
    }

    public virtual async Task<int> UpdateAsync(
        string tableName, IDictionary<string, object?> data, string whereColumn, object? whereValue)
    {
        TableNames.Validate(tableName);
        var meta = await TableMetaAsync(tableName);
        var setCols = new List<string>();
        var setVals = new List<object?>();
        foreach (var m in meta)
        {
            if (m.ColumnName.Equals(whereColumn, StringComparison.OrdinalIgnoreCase)) continue;
            var key = FindDataKey(data, m.ColumnName);
            if (key != null)
            {
                setCols.Add(m.ColumnName);
                setVals.Add(data[key]);
            }
        }
        if (setCols.Count == 0) return 0;
        var sql = new System.Text.StringBuilder("UPDATE ").Append(tableName).Append(" SET ");
        for (var i = 0; i < setCols.Count; i++)
        {
            if (i > 0) sql.Append(", ");
            sql.Append(setCols[i]).Append(" = ?");
        }
        sql.Append(" WHERE ").Append(whereColumn).Append(" = ?");
        await using var conn = await _factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql.ToString();
        foreach (var v in setVals) Bind(cmd, ToDbValue(v));
        Bind(cmd, ToDbValue(whereValue));
        return await cmd.ExecuteNonQueryAsync();
    }

    public virtual async Task<bool> ExistsAsync(string tableName, string bizKey, object? bizKeyValue)
    {
        TableNames.Validate(tableName);
        var sql = $"SELECT COUNT(1) FROM {tableName} WHERE {bizKey} = ?";
        await using var conn = await _factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, ToDbValue(bizKeyValue));
        var r = await cmd.ExecuteScalarAsync();
        return r != null && Convert.ToInt64(r) > 0;
    }

    public void FillSystemFields(IDictionary<string, object?> data, bool insert)
    {
        if (insert)
        {
            if (CreateTimeColumn != null) data.TryAdd(CreateTimeColumn, Now);
            if (CreateUserColumn != null) data.TryAdd(CreateUserColumn, ResolveDefaultUser(data));
            if (UpdateTimeColumn != null) data.TryAdd(UpdateTimeColumn, Now);
            if (UpdateUserColumn != null) data.TryAdd(UpdateUserColumn, ResolveDefaultUser(data));
            if (IsDeletedColumn != null) data.TryAdd(IsDeletedColumn, 0);
        }
        else
        {
            if (UpdateTimeColumn != null) data[UpdateTimeColumn] = Now;
            if (UpdateUserColumn != null) data.TryAdd(UpdateUserColumn, ResolveDefaultUser(data));
        }
    }

    /// <summary>默认用户值（issues/19/C17）：优先 data.apply_user_id（=流程 operator），否则配置默认值。</summary>
    protected object? ResolveDefaultUser(IDictionary<string, object?> data) =>
        data.TryGetValue("apply_user_id", out var op) && op != null ? op : DefaultUserValue;

    // ── 表结构 ──

    /// <summary>表结构（缓存）；表不存在返回空列表。C18：information_schema 必须 schema 限定（DATABASE()）。</summary>
    protected internal async Task<List<ColumnMeta>> TableMetaAsync(string tableName)
    {
        var key = tableName.ToLowerInvariant();
        lock (_cacheLock)
        {
            if (_schemaCache.TryGetValue(key, out var cached)) return cached;
        }
        var meta = await LoadTableMetaAsync(tableName);
        lock (_cacheLock)
        {
            _schemaCache[key] = meta;
        }
        return meta;
    }

    private async Task<List<ColumnMeta>> LoadTableMetaAsync(string tableName)
    {
        // C18：table_schema = DATABASE()（schema 限定，防多 schema 查表撞列）
        const string sql = "SELECT column_name, extra, column_key FROM information_schema.columns " +
                           "WHERE UPPER(table_name) = UPPER(?) AND table_schema = DATABASE() " +
                           "ORDER BY ordinal_position";
        var meta = new List<ColumnMeta>();
        await using var conn = await _factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, tableName);
        try
        {
            await using var rs = await cmd.ExecuteReaderAsync();
            while (await rs.ReadAsync())
            {
                var columnName = rs.GetString(0);
                var extra = rs.IsDBNull(1) ? null : rs.GetString(1);
                var columnKey = rs.IsDBNull(2) ? null : rs.GetString(2);
                var primaryKey = "PRI".Equals(columnKey, StringComparison.OrdinalIgnoreCase);
                var autoIncrement = extra != null && extra.ToLowerInvariant().Contains("auto_increment");
                meta.Add(new ColumnMeta(columnName, primaryKey, autoIncrement));
            }
        }
        catch (Exception e)
        {
            throw new JeeflowException($"读取表结构失败: {tableName} -> {e.Message}");
        }
        return meta;
    }

    // ── 列匹配（issues/20）──

    protected ColumnMeta? FindColumn(List<ColumnMeta> meta, string column)
    {
        foreach (var m in meta)
        {
            if (StrictColumnMatch)
            {
                if (m.ColumnName.Equals(column, StringComparison.OrdinalIgnoreCase)) return m;
            }
            else if (NormalizeColumn(m.ColumnName).Equals(NormalizeColumn(column), StringComparison.Ordinal))
            {
                return m;
            }
        }
        return null;
    }

    /// <summary>列名归一：转小写 + 去下划线（companyName / company_name 等价）。</summary>
    protected static string NormalizeColumn(string name) =>
        name.ToLowerInvariant().Replace("_", "");

    protected static string? FindDataKey(IDictionary<string, object?> data, string col)
    {
        foreach (var k in data.Keys)
        {
            if (k == null) continue;
            if (NormalizeColumn(col).Equals(NormalizeColumn(k), StringComparison.Ordinal)) return k;
        }
        return null;
    }

    // ── 工具 ──

    protected static void Bind(DbCommand cmd, object? value)
    {
        var p = cmd.CreateParameter();
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    protected object? ToDbValue(object? value)
    {
        if (value is DateTime dt) return dt.ToString("yyyy-MM-dd HH:mm:ss");
        if (value is string or byte[] or bool) return value;
        if (value is IDictionary<string, object?> || value is System.Collections.IEnumerable)
            return (_json ?? DefaultJsonProvider.Instance).ToJson(value);
        return value;
    }
}

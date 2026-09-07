using System.Data.Common;
using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Persist;

/// <summary>字段存储类型（issues/23，对齐 mldong dev_schema_field 1-5 语义）。</summary>
public enum StorageType
{
    Normal = 1,
    Expand = 2,
    Json = 3,
    One2One = 4,
    One2Many = 5,
}

/// <summary>字段元数据（issues/23）——表单字段 → 存储语义映射。</summary>
public class FieldMeta
{
    /// <summary>表单字段名（f_ 去前缀后的名，如 companyName）。</summary>
    public string? Name { get; set; }
    /// <summary>主表列名（缺省 = name 转下划线）。</summary>
    public string? ColumnName { get; set; }
    /// <summary>存储类型（默认 NORMAL）。</summary>
    public StorageType StorageType { get; set; } = StorageType.Normal;
    /// <summary>EXPAND：展开的子字段（表单字段名 → 表列名）。</summary>
    public Dictionary<string, string> ExpandFields { get; set; } = new();
    /// <summary>ONE2ONE / ONE2MANY：子表表名。</summary>
    public string? TargetTable { get; set; }
    /// <summary>ONE2ONE / ONE2MANY：子表外键列（缺省 = 主表主键列名）。</summary>
    public string? ForeignKey { get; set; }

    public string? GetColumnName()
    {
        if (string.IsNullOrEmpty(ColumnName)) return Name == null ? null : ToUnderline(Name);
        return ColumnName;
    }

    /// <summary>驼峰转下划线（companyName → company_name）。</summary>
    public static string ToUnderline(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>storageType 反序列化：支持名称（"EXPAND"）与数字（2）。</summary>
    public static StorageType? StorageTypeFromJson(object? v)
    {
        if (v == null) return null;
        if (v is long l) return l is >= 1 and <= 5 ? (StorageType)l : null;
        if (v is int i) return (StorageType)i;
        if (v is double d) return StorageTypeFromJson((long)d);
        var s = v.ToString();
        if (string.IsNullOrEmpty(s)) return null;
        foreach (StorageType t in System.Enum.GetValues(typeof(StorageType)))
            if (t.ToString().Equals(s, StringComparison.OrdinalIgnoreCase))
                return t;
        return null;
    }
}

/// <summary>表元数据。</summary>
public class TableMeta
{
    public string? TableName { get; set; }
    public string PrimaryKey { get; set; } = "id";
    public List<FieldMeta> Fields { get; set; } = new();

    public FieldMeta? FindField(string? name)
    {
        if (name == null) return null;
        foreach (var f in Fields)
            if (name.Equals(f.Name, StringComparison.OrdinalIgnoreCase))
                return f;
        return null;
    }

    public FieldMeta? FindFieldByColumn(string? columnName)
    {
        if (columnName == null) return null;
        foreach (var f in Fields)
            if (columnName.Equals(f.GetColumnName(), StringComparison.OrdinalIgnoreCase))
                return f;
        return null;
    }
}

/// <summary>元数据提供者 SPI。</summary>
public interface IDynamicMetaProvider
{
    TableMeta? LoadTableMeta(string tableName);
}

/// <summary>
/// 内置 JSON 配置加载器（issues/23）：目录下 文件名 = 表名（如 biz_leave.json），
/// 内容为 TableMeta JSON（storageType 支持名称或 1-5 数字）。表缓存。
/// </summary>
public class JsonMetaProvider : IDynamicMetaProvider
{
    private readonly string? _dir;
    private readonly Dictionary<string, TableMeta?> _cache = new();

    public JsonMetaProvider(string? dir) => _dir = dir;

    public TableMeta? LoadTableMeta(string tableName)
    {
        if (tableName == null) return null;
        lock (_cache)
        {
            if (_cache.TryGetValue(tableName, out var cached)) return cached;
        }
        TableMeta? meta = null;
        var path = Path.Combine(_dir ?? "", tableName + ".json");
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            if (DefaultJsonProvider.Instance.FromJson(json) is Dictionary<string, object?> root)
            {
                meta = new TableMeta
                {
                    TableName = root.TryGetValue("tableName", out var tn) ? tn?.ToString() : tableName,
                    PrimaryKey = root.TryGetValue("primaryKey", out var pk) && !string.IsNullOrEmpty(pk?.ToString())
                        ? pk.ToString()!
                        : "id",
                };
                if (root.TryGetValue("fields", out var f) && f is List<object?> fields)
                {
                    foreach (var item in fields)
                    {
                        if (item is not Dictionary<string, object?> fd) continue;
                        var fm = new FieldMeta
                        {
                            Name = fd.TryGetValue("name", out var n) ? n?.ToString() : null,
                            ColumnName = fd.TryGetValue("columnName", out var cn) ? cn?.ToString() : null,
                        };
                        if (fd.TryGetValue("storageType", out var st))
                            fm.StorageType = FieldMeta.StorageTypeFromJson(st) ?? StorageType.Normal;
                        if (fd.TryGetValue("targetTable", out var tt)) fm.TargetTable = tt?.ToString();
                        if (fd.TryGetValue("foreignKey", out var fk)) fm.ForeignKey = fk?.ToString();
                        if (fd.TryGetValue("expandFields", out var ef) && ef is Dictionary<string, object?> efd)
                        {
                            foreach (var kv in efd) fm.ExpandFields[kv.Key] = kv.Value?.ToString() ?? "";
                        }
                        meta.Fields.Add(fm);
                    }
                }
            }
        }
        lock (_cache)
        {
            _cache[tableName] = meta;
        }
        return meta;
    }
}

/// <summary>业务表查询器（issues/23 读侧底层）：按列等值查询原始行。</summary>
public class TableReader
{
    private readonly IDbConnectionFactory _factory;

    public TableReader(IDbConnectionFactory factory) => _factory = factory;

    /// <summary>查询首行（列名→值）。</summary>
    public async Task<Dictionary<string, object?>?> QueryFirstAsync(
        string tableName, string whereColumn, object? value)
    {
        var rows = await QueryListAsync(tableName, whereColumn, value, 1);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>查询列表（limit 0 = 不限制）。</summary>
    public async Task<List<Dictionary<string, object?>>> QueryListAsync(
        string tableName, string whereColumn, object? value, int limit)
    {
        TableNames.Validate(tableName);
        var sql = "SELECT * FROM " + tableName + " WHERE " + whereColumn + " = ?"
                  + (limit > 0 ? $" LIMIT {limit}" : "");
        var rows = new List<Dictionary<string, object?>>();
        await using var conn = await _factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var p = cmd.CreateParameter();
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
        await using var rs = await cmd.ExecuteReaderAsync();
        while (await rs.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < rs.FieldCount; i++)
            {
                row[rs.GetName(i)] = rs.IsDBNull(i) ? null : rs.GetValue(i);
            }
            rows.Add(row);
        }
        return rows;
    }
}

/// <summary>
/// 元数据驱动的动态读取引擎（issues/23 读侧）——按 relTableName + process_instance_id 回显业务数据，
/// 按 storageType 反序列化组装；无元数据回落原始行。
/// 实现 IBizDataReader：Facade bizData 读侧按该接口取 reader（签名本就一致，issues/109 漏声明）。
/// </summary>
public class MetaTableReader : IBizDataReader
{
    private readonly TableReader _reader;
    private readonly IDynamicMetaProvider _provider;

    public MetaTableReader(TableReader reader, IDynamicMetaProvider provider)
    {
        _reader = reader;
        _provider = provider;
    }

    /// <summary>按流程实例回显：无记录返回 null；无元数据回落原始行。</summary>
    public async Task<Dictionary<string, object?>?> ReadByProcessInstanceAsync(
        string tableName, object? processInstanceId)
    {
        var row = await _reader.QueryFirstAsync(tableName, "process_instance_id", processInstanceId);
        if (row == null) return null;
        var meta = _provider.LoadTableMeta(tableName);
        if (meta == null) return row;
        return await AssembleAsync(meta, row);
    }

    /// <summary>按元数据组装回显结果（字段名 → 值）。</summary>
    public async Task<Dictionary<string, object?>> AssembleAsync(TableMeta meta, Dictionary<string, object?> row)
    {
        var result = new Dictionary<string, object?>();
        foreach (var f in meta.Fields)
        {
            var v = FindRowValue(row, f.GetColumnName());
            switch (f.StorageType)
            {
                case StorageType.Json:
                    result[f.Name!] = FromJson(v);
                    break;
                case StorageType.Expand:
                    var obj = ExpandFrom(row, f);
                    if (obj != null) result[f.Name!] = obj;
                    break;
                case StorageType.One2One:
                case StorageType.One2Many:
                    var sub = await ReadSubTableAsync(meta, f, row);
                    if (sub != null) result[f.Name!] = sub;
                    break;
                default:
                    if (v != null) result[f.Name!] = v;
                    break;
            }
        }
        // 未在元数据中的列原样带出（key 统一小写）；EXPAND 展开列视为已消费（issues/24）
        foreach (var kv in row)
        {
            if (meta.FindFieldByColumn(kv.Key) == null && !IsExpandColumn(meta, kv.Key))
                result.TryAdd(kv.Key.ToLowerInvariant(), kv.Value);
        }
        return result;
    }

    private bool IsExpandColumn(TableMeta meta, string columnName)
    {
        foreach (var f in meta.Fields)
            foreach (var col in f.ExpandFields.Values)
                if (col.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    private static Dictionary<string, object?>? ExpandFrom(Dictionary<string, object?> row, FieldMeta f)
    {
        var obj = new Dictionary<string, object?>();
        foreach (var ef in f.ExpandFields)
        {
            var v = FindRowValue(row, ef.Value);
            if (v != null) obj[ef.Key] = v;
        }
        return obj.Count == 0 ? null : obj;
    }

    private async Task<object?> ReadSubTableAsync(TableMeta parentMeta, FieldMeta f, Dictionary<string, object?> row)
    {
        var parentPk = FindRowValue(row, parentMeta.PrimaryKey);
        if (parentPk == null) return null;
        var fk = f.ForeignKey ?? parentMeta.PrimaryKey;
        var subMeta = _provider.LoadTableMeta(f.TargetTable);
        if (f.StorageType == StorageType.One2One)
        {
            var sub = await _reader.QueryFirstAsync(f.TargetTable!, fk, parentPk);
            if (sub == null) return null;
            return subMeta != null ? await AssembleAsync(subMeta, sub) : sub;
        }
        var subs = await _reader.QueryListAsync(f.TargetTable!, fk, parentPk, 0);
        var result = new List<object?>();
        foreach (var sub in subs) result.Add(subMeta != null ? await AssembleAsync(subMeta, sub) : sub);
        return result;
    }

    internal static object? FindRowValue(Dictionary<string, object?>? row, string? columnName)
    {
        if (row == null || columnName == null) return null;
        foreach (var kv in row)
            if (kv.Key.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return null;
    }

    private static object? FromJson(object? v)
    {
        if (v == null) return null;
        try
        {
            return DefaultJsonProvider.Instance.FromJson(v.ToString()!);
        }
        catch
        {
            return v; // 非 JSON 串容错原样返回
        }
    }
}

/// <summary>业务数据读取器接口（facade bizData action 按名 "metaTableReader" 消费，免反射）。</summary>
public interface IBizDataReader
{
    Task<Dictionary<string, object?>?> ReadByProcessInstanceAsync(string tableName, object? processInstanceId);
}

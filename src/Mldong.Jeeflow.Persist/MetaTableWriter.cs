using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Persist;

/// <summary>
/// 元数据驱动的动态写入引擎（issues/23，纯写职责）——按 TableMeta storageType 语义执行
/// （NORMAL 直写 / JSON 序列化 / EXPAND 展开 / ONE2ONE·ONE2MANY 子表递归），
/// 系统字段/主键生成/幂等沿用基础 writer。无元数据时完全委托基础 writer（零破坏回落）。
/// </summary>
public class MetaTableWriter : IDynamicTableWriter
{
    private readonly IDynamicTableWriter _base;
    private readonly IDynamicMetaProvider _provider;

    public MetaTableWriter(IDynamicTableWriter baseWriter, IDynamicMetaProvider provider)
    {
        _base = baseWriter;
        _provider = provider;
    }

    public IDynamicMetaProvider Provider => _provider;

    public Task<List<string>> FilterColumnsAsync(string tableName, IEnumerable<string> columns) =>
        _base.FilterColumnsAsync(tableName, columns);

    public virtual async Task<object?> InsertAsync(string tableName, IDictionary<string, object?> data)
    {
        var meta = _provider.LoadTableMeta(tableName);
        if (meta == null) return await _base.InsertAsync(tableName, data); // 无元数据：回落
        var subData = new Dictionary<string, object?>();
        var row = new Dictionary<string, object?>();
        foreach (var f in meta.Fields)
        {
            if (!data.TryGetValue(f.Name ?? "", out var v) || v == null) continue;
            switch (f.StorageType)
            {
                case StorageType.Json:
                    row[f.GetColumnName()!] = DefaultJsonProvider.Instance.ToJson(v);
                    break;
                case StorageType.Expand:
                    ExpandInto(f, v, row);
                    break;
                case StorageType.One2One:
                case StorageType.One2Many:
                    subData[f.Name!] = v;
                    break;
                default:
                    row[f.GetColumnName()!] = v;
                    break;
            }
        }
        // 未消费字段（流程上下文 + 集成方自定义字段）直通基础 writer
        foreach (var kv in data)
        {
            if (meta.FindField(kv.Key) == null) row.TryAdd(kv.Key, kv.Value);
        }
        _base.FillSystemFields(row, insert: true);
        var pk = await _base.InsertAsync(tableName, row);
        if (pk == null) pk = FindRowValue(row, meta.PrimaryKey); // 兜底：data 显式主键
        // 子表递归插入（外键=主表主键，同事务语义由基础 writer 连接管理）
        foreach (var kv in subData)
        {
            await InsertSubTableAsync(meta, meta.FindField(kv.Key)!, kv.Value, pk, data);
        }
        return pk;
    }

    /// <summary>EXPAND：对象字段展开为多列（子字段名 → 表列名）。</summary>
    private static void ExpandInto(FieldMeta f, object? v, Dictionary<string, object?> row)
    {
        if (v is not IDictionary<string, object?> obj) return;
        foreach (var ef in f.ExpandFields)
        {
            if (obj.TryGetValue(ef.Key, out var fv) && fv != null) row[ef.Value] = fv;
        }
    }

    /// <summary>ONE2ONE/ONE2MANY 子表递归（外键=主表主键；C17：子表继承主表 apply_user_id，putIfAbsent）。</summary>
    private async Task InsertSubTableAsync(
        TableMeta parentMeta, FieldMeta f, object? v, object? parentPk, IDictionary<string, object?> parentData)
    {
        if (parentPk == null)
            throw new JeeflowException($"主表主键缺失，无法插入子表: {f.Name}");
        var fk = f.ForeignKey ?? parentMeta.PrimaryKey;
        // issues/24：继承主表操作人上下文（apply_user_id=流程 operator）
        parentData.TryGetValue("apply_user_id", out var op);
        if (f.StorageType == StorageType.One2One && v is IDictionary<string, object?> oneRow)
        {
            await InsertSubRowAsync(f, oneRow, fk, parentPk, op);
        }
        else if (f.StorageType == StorageType.One2Many && v is System.Collections.IEnumerable list)
        {
            foreach (var item in list)
            {
                if (item is IDictionary<string, object?> subRow)
                    await InsertSubRowAsync(f, subRow, fk, parentPk, op);
            }
        }
    }

    /// <summary>子表单条插入（外键注入 + 递归走子表自身元数据 + 操作人上下文继承，子表显式同名字段优先）。</summary>
    private async Task InsertSubRowAsync(
        FieldMeta f, IDictionary<string, object?> subData, string fk, object? parentPk, object? op)
    {
        var row = new Dictionary<string, object?>();
        foreach (var kv in subData) row[kv.Key] = kv.Value;
        row[fk] = parentPk;
        if (op != null) row.TryAdd("apply_user_id", op);
        await InsertAsync(f.TargetTable!, row);
    }

    public Task<bool> ExistsAsync(string tableName, string bizKey, object? bizKeyValue) =>
        _base.ExistsAsync(tableName, bizKey, bizKeyValue);

    /// <summary>按条件列更新（SYNC）：按元数据组装 SET 列（子表不参与中途更新），未消费字段直通。</summary>
    public virtual async Task<int> UpdateAsync(
        string tableName, IDictionary<string, object?> data, string whereColumn, object? whereValue)
    {
        var meta = _provider.LoadTableMeta(tableName);
        if (meta == null) return await _base.UpdateAsync(tableName, data, whereColumn, whereValue);
        var row = new Dictionary<string, object?>();
        foreach (var f in meta.Fields)
        {
            if (f.StorageType is StorageType.One2One or StorageType.One2Many) continue;
            if (!data.TryGetValue(f.Name ?? "", out var v) || v == null) continue;
            switch (f.StorageType)
            {
                case StorageType.Json:
                    row[f.GetColumnName()!] = DefaultJsonProvider.Instance.ToJson(v);
                    break;
                case StorageType.Expand:
                    ExpandInto(f, v, row);
                    break;
                default:
                    row[f.GetColumnName()!] = v;
                    break;
            }
        }
        foreach (var kv in data)
        {
            if (meta.FindField(kv.Key) == null) row.TryAdd(kv.Key, kv.Value);
        }
        return await _base.UpdateAsync(tableName, row, whereColumn, whereValue);
    }

    public void FillSystemFields(IDictionary<string, object?> data, bool insert) =>
        _base.FillSystemFields(data, insert);

    /// <summary>按列名取值（宽松：忽略大小写）。</summary>
    protected static object? FindRowValue(IDictionary<string, object?> row, string? columnName)
    {
        if (columnName == null) return null;
        foreach (var kv in row)
            if (kv.Key.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return null;
    }
}

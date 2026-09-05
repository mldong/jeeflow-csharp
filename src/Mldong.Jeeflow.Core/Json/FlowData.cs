namespace Mldong.Jeeflow.Core;

/// <summary>
/// 流程数据载体（对齐 Java FlowData extends LinkedHashMap）。
/// 引擎内对象图约定：Dictionary&lt;string,object?&gt; / List&lt;object?&gt; / string / bool /
/// long / int / double / DateTime / null。
/// </summary>
public class FlowData : Dictionary<string, object?>
{
    public FlowData() { }

    public FlowData(IDictionary<string, object?> map)
    {
        foreach (var kv in map) this[kv.Key] = kv.Value;
    }

    public static FlowData Create() => new();

    public static FlowData Of(IDictionary<string, object?> map) => new(map);

    /// <summary>浅拷贝（对齐 Java copy()：新建容器，值引用共享）。</summary>
    public FlowData Copy()
    {
        var data = new FlowData();
        foreach (var kv in this) data[kv.Key] = kv.Value;
        return data;
    }

    public FlowData Set(string key, object? value)
    {
        this[key] = value;
        return this;
    }

    public FlowData SetAll(IDictionary<string, object?> map)
    {
        foreach (var kv in map) this[kv.Key] = kv.Value;
        return this;
    }

    public void Put(string key, object? value) => this[key] = value;

    public string? GetStr(string key)
    {
        TryGetValue(key, out var v);
        return v == null ? null : v.ToString();
    }

    public string GetStr(string key, string defaultValue)
    {
        var v = GetStr(key);
        return v ?? defaultValue;
    }

    public long? GetLong(string key)
    {
        TryGetValue(key, out var v);
        if (v == null) return null;
        if (v is long l) return l;
        if (v is int i) return i;
        if (v is double d) return (long)d;
        return long.TryParse(v.ToString(), out var parsed) ? parsed : null;
    }

    public int? GetInt(string key)
    {
        TryGetValue(key, out var v);
        if (v == null) return null;
        if (v is int i) return i;
        if (v is long l) return (int)l;
        if (v is double d) return (int)d;
        return int.TryParse(v.ToString(), out var parsed) ? parsed : null;
    }

    public int GetInt(string key, int defaultValue) => GetInt(key) ?? defaultValue;

    public bool? GetBool(string key)
    {
        TryGetValue(key, out var v);
        if (v == null) return null;
        if (v is bool b) return b;
        var s = v.ToString()?.ToLowerInvariant();
        return s is "true" or "1" or "yes";
    }

    public object? GetObj(string key) => TryGetValue(key, out var v) ? v : null;

    public bool Contains(string key) => ContainsKey(key);

    /// <summary>字符串取值（键缺失返回 null，不抛错）。</summary>
    public string? TryGetStr(string key) => TryGetValue(key, out var v) ? v?.ToString() : null;
}

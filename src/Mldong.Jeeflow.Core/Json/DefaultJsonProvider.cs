using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mldong.Jeeflow.Core;

/// <summary>
/// JSON 序列化/反序列化 SPI（对齐 Java IJsonProvider）。
/// core 内置默认实现（System.Text.Json，BCL 内置件不算第三方），可替换。
/// </summary>
public interface IJsonProvider
{
    /// <summary>对象转 JSON 字符串。</summary>
    string ToJson(object? value);

    /// <summary>JSON 字符串转对象图（Dictionary/List/标量）。</summary>
    object? FromJson(string json);

    /// <summary>判断字符串是否为合法 JSON。</summary>
    bool IsJson(string json);
}

/// <summary>默认 IJsonProvider（System.Text.Json）。</summary>
public sealed class DefaultJsonProvider : IJsonProvider
{
    public static readonly DefaultJsonProvider Instance = new();

    // 出口序列化显式钉死选项（CS1）：键名原样、Unicode 全量直出、不缩进。
    // 引擎内部 toJson 仅用于流程定义 content / 变量列存储，与门面出口层（Outbound）不同。
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, // 不影响字典键；仅兜底强类型
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public string ToJson(object? value)
    {
        var node = ToNode(value);
        return node?.ToJsonString(WriteOptions) ?? "null";
    }

    public object? FromJson(string json)
    {
        var node = JsonNode.Parse(json);
        return FromNode(node);
    }

    public bool IsJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            _ = JsonNode.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>CLR 对象图 → JsonNode。</summary>
    public static JsonNode? ToNode(object? value)
    {
        if (value == null) return null;
        if (value is JsonNode jn) return jn;
        if (value is bool b) return b;
        if (value is string s) return s;
        if (value is byte[] bytes) return JsonValue.Create(Convert.ToBase64String(bytes));
        if (value is DateTime dt) return JsonValue.Create(dt.ToString("yyyy-MM-dd HH:mm:ss"));
        if (value is int i) return i;
        if (value is long l) return l;
        if (value is double d) return d;
        if (value is float f) return (double)f;
        if (value is decimal m) return (double)m;
        if (value is short sh) return (int)sh;
        if (value is IDictionary<string, object?> dict)
        {
            var obj = new JsonObject();
            foreach (var kv in dict) obj[kv.Key] = ToNode(kv.Value);
            return obj;
        }
        if (value is System.Collections.IDictionary dnr)
        {
            var obj = new JsonObject();
            foreach (System.Collections.DictionaryEntry kv in dnr)
                obj[kv.Key?.ToString() ?? ""] = ToNode(kv.Value);
            return obj;
        }
        if (value is System.Collections.IEnumerable en and not string)
        {
            var arr = new JsonArray();
            foreach (var item in en) arr.Add(ToNode(item));
            return arr;
        }
        return JsonValue.Create(value.ToString());
    }

    /// <summary>JsonNode → CLR 对象图。整数优先 long（>2^53 精度安全），否则 double。</summary>
    public static object? FromNode(JsonNode? node)
    {
        if (node == null) return null;
        if (node is JsonObject obj)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var kv in obj) dict[kv.Key] = FromNode(kv.Value);
            return dict;
        }
        if (node is JsonArray arr)
        {
            var list = new List<object?>();
            foreach (var item in arr) list.Add(FromNode(item));
            return list;
        }
        if (node is JsonValue val)
        {
            if (val.TryGetValue<bool>(out var b)) return b;
            if (val.TryGetValue<long>(out var l)) return l;
            if (val.TryGetValue<double>(out var d)) return d;
            if (val.TryGetValue<string>(out var s)) return s;
        }
        return node.ToJsonString();
    }
}

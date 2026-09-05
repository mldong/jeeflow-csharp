using System.Text.Encodings.Web;
using System.Text.Json;

namespace Mldong.Jeeflow.Core;

/// <summary>
/// 契约出口递归 stringifier（C1/C2/CS1/CS2，对齐 Moon/Rust 自研出口层）：
/// <list type="bullet">
/// <item>id 全字符串化：单数 <c>id</c> / <c>*Id</c> / <c>*_id</c> 与复数 <c>Ids</c>/<c>ids</c>
/// （数组逐元素）；对象数组（rows）不误伤——仅按键规则处理</item>
/// <item>时间一律 <c>yyyy-MM-dd HH:mm:ss</c>（CS4，DateTime 不直接出口）</item>
/// <item>统计计数字段（非 id 键的 int/long）保持 JSON number——id 字符串化规则不得误伤
/// stats 字段（issues/105 教训）</item>
/// <item>出口无 &gt;2^53 number（雪花 id 全部走 string）</item>
/// </list>
/// 门面统一经此出口，禁止依赖任何"序列化器全局魔法"。
/// </summary>
public static class Outbound
{
    public const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>出口转换 + JSON 序列化（facade 统一出口）。</summary>
    public static string ToJson(object? data) =>
        JsonSerializer.Serialize(Transform(data), Options);

    /// <summary>出口转换（对象图重写；分页/信封组装仍由门面完成）。</summary>
    public static object? Transform(object? value)
    {
        switch (value)
        {
            case null:
            case string:
            case bool:
                return value;
            case DateTime dt:
                return dt.ToString(TimeFormat);
            case IDictionary<string, object?> dict:
            {
                var outDict = new Dictionary<string, object?>();
                foreach (var kv in dict)
                    outDict[kv.Key] = IsIdKey(kv.Key) ? StringifyIdValue(kv.Value) : Transform(kv.Value);
                return outDict;
            }
            case System.Collections.IDictionary dnr:
            {
                var outDict = new Dictionary<string, object?>();
                foreach (System.Collections.DictionaryEntry kv in dnr)
                {
                    var k = kv.Key?.ToString() ?? "";
                    outDict[k] = IsIdKey(k) ? StringifyIdValue(kv.Value) : Transform(kv.Value);
                }
                return outDict;
            }
            case IEnumerable<object?> list:
            {
                var outList = new List<object?>();
                foreach (var item in list) outList.Add(Transform(item));
                return outList;
            }
            case System.Collections.IEnumerable en:
            {
                var outList = new List<object?>();
                foreach (var item in en) outList.Add(Transform(item));
                return outList;
            }
            default:
                return value;
        }
    }

    /// <summary>
    /// id 键判定：单数 id/*Id/*_id + 复数 Ids/ids（复数在取值侧逐元素字符串化）。
    /// </summary>
    public static bool IsIdKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (key is "id" or "ids") return true;
        return key.EndsWith("Id") || key.EndsWith("Ids") || key.EndsWith("_id");
    }

    /// <summary>id 值字符串化：数字 → 十进制串（null 保持 null、字符串保持、数组逐元素）。</summary>
    public static object? StringifyIdValue(object? value)
    {
        switch (value)
        {
            case null:
            case string:
                return value;
            case long l:
                return l.ToString();
            case int i:
                return i.ToString();
            case double d:
                return Math.Floor(d) == d && !double.IsInfinity(d)
                    ? ((long)d).ToString()
                    : d.ToString();
            case IEnumerable<object?> list:
            {
                var outList = new List<object?>();
                foreach (var item in list) outList.Add(StringifyIdValue(item));
                return outList;
            }
            default:
                return value.ToString();
        }
    }
}

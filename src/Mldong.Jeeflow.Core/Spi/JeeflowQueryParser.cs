using System.Globalization;

namespace Mldong.Jeeflow.Core;

/// <summary>
/// m_* 查询参数解析器（对齐 Java JeeflowQueryParser）：
/// 解析前端 {@code m_{alias}_{type}_{column}} 三段式 → PageQuery 条件；列名仓储层过白名单。
/// m_EQ_taskName → column="t.task_name"；m_t_LIKE_name → column="t.name"。
/// </summary>
public class JeeflowQueryParser
{
    private const string Prefix = "m_";

    public PageQuery Parse(IDictionary<string, object?> paramMap)
    {
        var query = new PageQuery
        {
            PageNum = ToInt(paramMap.TryGetValue("pageNum", out var pn) ? pn : null, 1),
            PageSize = ToInt(paramMap.TryGetValue("pageSize", out var ps) ? ps : null, 10),
            OrderBy = paramMap.TryGetValue("orderBy", out var ob) ? ob?.ToString() : null,
        };

        foreach (var kv in paramMap)
        {
            var key = kv.Key;
            var value = kv.Value;
            if (!key.StartsWith(Prefix, StringComparison.Ordinal) || IsEmpty(value)) continue;

            var suffix = key[Prefix.Length..];
            var parts = suffix.Split('_');
            if (parts.Length < 2) continue;

            string column;
            string op;
            if (parts.Length == 2)
            {
                // m_EQ_taskName → t.task_name（默认主表别名 t，issues/05-5）
                op = parts[0];
                column = "t." + ToUnderscore(parts[1]);
            }
            else
            {
                // m_t_EQ_taskName → t.task_name
                var alias = parts[0];
                op = parts[1];
                column = alias + "." + ToUnderscore(string.Join('_', parts[2..]));
            }
            query.Add(column, op.ToUpperInvariant(), value);
        }
        return query;
    }

    private static string ToUnderscore(string camel)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in camel)
        {
            if (char.IsUpper(c))
            {
                sb.Append('_').Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static bool IsEmpty(object? val) =>
        val == null || (val is string s && s.Length == 0)
                   || (val is System.Collections.ICollection c && c.Count == 0);

    private static int ToInt(object? val, int def)
    {
        if (val == null) return def;
        if (val is int i) return i;
        if (val is long l) return (int)l;
        return int.TryParse(val.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : def;
    }
}

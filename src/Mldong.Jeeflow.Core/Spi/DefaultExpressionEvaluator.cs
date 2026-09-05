using System.Globalization;

namespace Mldong.Jeeflow.Core;

/// <summary>
/// 内置默认表达式求值器（以 PHP WfExpressionEvaluator 为最小基准不超集，方案 §3.2）：
/// 变量名替换（#var 会签门控变量优先）+ 比较运算（&gt;= &lt;= == != &gt; &lt;，数值优先字符串兜底）
/// + 布尔字面量。决策边 expr / 会签完成条件 / DecisionModel.expr 全走此 SPI。
/// </summary>
public sealed class DefaultExpressionEvaluator : IExpressionEvaluator
{
    public static readonly DefaultExpressionEvaluator Instance = new();

    public object? Eval(string expression, IDictionary<string, object?> context)
    {
        var expr = (expression ?? "").Trim();
        // 变量替换：长键优先，避免短键误吃长键子串
        foreach (var key in context.Keys.OrderByDescending(k => k.Length))
        {
            if (string.IsNullOrEmpty(key)) continue;
            if (expr.Contains(key))
            {
                var val = context[key];
                expr = expr.Replace(key, val?.ToString() ?? "0");
            }
        }
        // ${var} 占位（未命中替换为 0）
        expr = ReplacePlaceholder(expr, "${", "}", context);
        return EvaluateComparison(expr);
    }

    private static string ReplacePlaceholder(
        string expr, string open, string close, IDictionary<string, object?> context)
    {
        while (true)
        {
            var s = expr.IndexOf(open, StringComparison.Ordinal);
            if (s < 0) return expr;
            var e = expr.IndexOf(close, s + open.Length, StringComparison.Ordinal);
            if (e < 0) return expr;
            var name = expr.Substring(s + open.Length, e - s - open.Length);
            var value = context.TryGetValue(name, out var v) && v != null ? v.ToString() : "0";
            expr = expr.Substring(0, s) + value + expr.Substring(e + close.Length);
        }
    }

    private static bool EvaluateComparison(string expr)
    {
        string[] ops = { ">=", "<=", "!=", "==", ">", "<" };
        foreach (var op in ops)
        {
            var pos = FindOperator(expr, op);
            if (pos < 0) continue;
            var left = expr.Substring(0, pos).Trim();
            var right = expr.Substring(pos + op.Length).Trim();
            var cmp = CompareValues(left, right);
            return op switch
            {
                ">" => cmp > 0,
                "<" => cmp < 0,
                ">=" => cmp >= 0,
                "<=" => cmp <= 0,
                "==" => cmp == 0,
                "!=" => cmp != 0,
                _ => false,
            };
        }
        // 布尔字面量
        var lower = expr.ToLowerInvariant();
        if (lower is "true" or "1" or "yes") return true;
        if (lower is "false" or "0" or "" or "null") return false;
        return true;
    }

    private static int FindOperator(string expr, string op)
    {
        // == 先于 = 之类由 ops 顺序保证；这里只找第一个出现位置
        return expr.IndexOf(op, StringComparison.Ordinal);
    }

    /// <summary>比较：数值优先，字符串兜底。</summary>
    private static int CompareValues(string left, string right)
    {
        var lNum = double.TryParse(left, NumberStyles.Any, CultureInfo.InvariantCulture, out var l);
        var rNum = double.TryParse(right, NumberStyles.Any, CultureInfo.InvariantCulture, out var r);
        if (lNum && rNum) return l.CompareTo(r);
        return string.CompareOrdinal(left, right);
    }
}

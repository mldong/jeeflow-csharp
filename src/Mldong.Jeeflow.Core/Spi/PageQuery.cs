namespace Mldong.Jeeflow.Core;

/// <summary>分页查询参数（对齐 Java PageQuery，条件列名在仓储层过白名单）。</summary>
public class PageQuery
{
    public int PageNum { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public string? OrderBy { get; set; }
    public List<Condition> Conditions { get; } = new();

    public PageQuery() { }

    public PageQuery(int pageNum, int pageSize)
    {
        PageNum = pageNum;
        PageSize = pageSize;
    }

    public PageQuery Add(string column, string op, object? value)
    {
        Conditions.Add(new Condition(column, op, value));
        return this;
    }

    /// <summary>
    /// 归属谓词列（issues/129 案 A）：这几列定义“这条记录属于谁”，空值绝不能等于“不过滤”。
    /// 与门面归属落点一一对齐：pageInstances/doneList→t.operator、todoList→pta.actor_id、
    /// ccList→cc.actor_id、任务分页 join 出的实例发起人→pi.operator。
    /// 内存仓储与 MySQL 仓储共用这一处定义，避免同栈两仓各判各的。
    /// </summary>
    public static readonly HashSet<string> OwnershipColumns = new()
    {
        "t.operator", "pi.operator", "pta.actor_id", "cc.actor_id",
    };

    /// <summary>
    /// 某一列是否给了<b>有效</b>条件（值非 null、字符串非全空白、集合非空）——
    /// issues/141 G1 归属谓词「必填」的判据。
    /// <para>与 <see cref="OwnershipColumns"/> 的空值档同一口径：<c>null</c> / <c>""</c> / 全空白 /
    /// 空集合都算「没填」。内存仓储与 MySQL 仓储共用这一处定义（同栈两仓在同一条判据上
    /// 必须给同一个答案，issues/117 场景 27 那把尺子），故写在 SPI 侧而不是两个仓储里各抄一份。</para>
    /// </summary>
    public static bool HasEffectiveCondition(PageQuery? query, string column)
    {
        if (query == null) return false;
        foreach (var cond in query.Conditions)
        {
            if (!string.Equals(column, cond.Column, StringComparison.Ordinal)) continue;
            var val = cond.Value;
            if (val == null) continue;
            if (val is string s && s.Trim().Length == 0) continue;
            if (val is System.Collections.ICollection coll && coll.Count == 0) continue;
            return true;
        }
        return false;
    }

    /// <summary>单个查询条件（列别名.列名 / 操作符 / 值）。</summary>
    public class Condition
    {
        public string Column { get; set; }
        public string Operator { get; set; }
        public object? Value { get; set; }

        public Condition() { Column = ""; Operator = "EQ"; }

        public Condition(string column, string op, object? value)
        {
            Column = column;
            Operator = op;
            Value = value;
        }
    }
}

/// <summary>分页查询结果（C22：出口恒五键 {pageNum,pageSize,recordCount,totalPage,rows}）。</summary>
public class PageResult<T>
{
    public int PageNum { get; set; }
    public int PageSize { get; set; }
    public int RecordCount { get; set; }
    public List<T> Rows { get; set; } = new();

    public PageResult() { }

    public PageResult(int pageNum, int pageSize, int recordCount, List<T> rows)
    {
        PageNum = pageNum;
        PageSize = pageSize;
        RecordCount = recordCount;
        Rows = rows;
    }

    public static PageResult<T> Of(int pageNum, int pageSize, int recordCount, List<T> rows) =>
        new(pageNum, pageSize, recordCount, rows);

    public int TotalPage => RecordCount == 0 ? 0 : (RecordCount + PageSize - 1) / PageSize;
}

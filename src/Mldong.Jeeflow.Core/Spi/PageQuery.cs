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

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

    /// <summary>
    /// 抄送人集合归一（issues/141 G10「空不创建行」· spec 06-facade.md §2.10）：
    /// 三条入口（发起 <c>f_ccActors</c>／办理 <c>tf_ccActors</c>／门面手动 <c>createCCInstance</c>）
    /// 解析出的<b>逗号串与数组两种形态</b>都先过这一支——逐元素 <c>Trim</c>，
    /// <b>空串与纯空白（含 <c>null</c>）丢弃</b>，同一次调用内的重复折叠（顺序保持）。
    /// <para>丢完为空 ⇒ 调用方<b>不得建 cc 行、也不得 fire CC_CREATE（码 4）</b>；
    /// 门面手动腿此时与本仓既有的"空集合"档同判（<c>actorIds 缺失</c> 错误信封），不新造错误码/文案。</para>
    /// <para><b>为什么这一栈也必须有它</b>：C# 的 <c>"".Split(',')</c> 与 Java 同款——得到
    /// <b>一个空元素</b>而不是零个 ⇒ 旧形状真落一条 <c>actor_id=''</c> 的 cc 行（实测：发起腿给
    /// <c>""</c> 时 cc 行数=1、码 4 fire 1 次）。空归属值正是 issues/129 那族"空 operator 读全库"
    /// 的病根，不能从抄送侧继续往里灌（G1 已把 <c>cc.actor_id</c> 的空值档判成"没填"⇒ 空页，
    /// 灌进来的空行连"被读到"的机会都没有，纯脏数据）。</para>
    /// <para><b>落库与比较一律取 trim 后的值</b>：<c>" 123 "</c> 与 <c>"123"</c> 是同一个人——
    /// 不 trim 就会绕开 issues/141 G2 的写侧判重（<see cref="IProcessRepository.FindCcActorIdsAsync"/>
    /// 拿回的已是 trim 后的串），同一人落两行（实测：先落 <c>" 9101 "</c> 再判重 <c>"9101"</c> ⇒ 2 行）。
    /// 判据单点放在这里（与 <see cref="HasEffectiveCondition"/> 同一处），漏斗层（引擎/门面）与
    /// 写侧层（内存仓储 / MySQL 仓储 / SPI 默认实现）共用同一支，不在两个仓储里各抄一份。</para>
    /// </summary>
    public static List<string> NormalizeCcActors(IEnumerable<string?>? raw)
    {
        var outList = new List<string>();
        if (raw == null) return outList;
        foreach (var actorId in raw)
        {
            if (actorId == null) continue;
            var trimmed = actorId.Trim();
            if (trimmed.Length == 0) continue;
            if (!outList.Contains(trimmed)) outList.Add(trimmed);
        }
        return outList;
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

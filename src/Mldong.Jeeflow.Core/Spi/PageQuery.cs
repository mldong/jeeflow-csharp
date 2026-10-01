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
    /// <b>归属值归一的判据单点</b>（issues/141 G10「空不创建行」· spec 06-facade.md §2.10 抄送侧
    /// ＋ issues/142 B 批 · spec 06 §2.11 任务侧——<b>同一条尺子两处用</b>，不抄第二份）。
    /// 三条抄送入口（发起 <c>f_ccActors</c>／办理 <c>tf_ccActors</c>／门面手动 <c>createCCInstance</c>）
    /// 与四条任务写点（<c>addCandidate</c>／<c>surrogate</c>／<c>transfer</c>／
    /// <c>f_nextNodeOperator</c>·<c>tf_nextNodeOperator</c>）解析出的<b>逗号串与数组两种形态</b>
    /// 都先过这一支——逐元素 <c>Trim</c>，
    /// <b>空串与纯空白（含 <c>null</c>）丢弃</b>，同一次调用内的重复折叠（顺序保持）。
    /// <para>丢完为空 ⇒ cc 侧<b>不建行、也不 fire CC_CREATE（码 4）</b>；任务侧由调用方决定
    /// 回落缺省（消费腿回落节点 assignee）还是走既有的"缺参数"错误信封——
    /// §2.10/§2.11 要求③：<b>不新造错误码/文案</b>。</para>
    /// <para><b>为什么这一栈也必须有它</b>：C# 的 <c>"".Split(',')</c> 与 Java 同款——得到
    /// <b>一个空元素</b>而不是零个 ⇒ 旧形状真落一条 <c>actor_id=''</c> 的行（实测：发起腿给
    /// <c>""</c> 时 cc 行数=1、码 4 fire 1 次；加签腿给 <c>["  "]</c> 时 code=0 且真落一行纯空白）。
    /// 空归属值正是 issues/129 那族"空 operator 读全库"的病根，
    /// 而且 G1 已把空值档判成"没填"⇒ 空页，灌进来的空行连"被读到"的机会都没有，纯脏数据。</para>
    /// <para><b>落库与比较一律取 trim 后的值</b>：<c>" 123 "</c> 与 <c>"123"</c> 是同一个人——
    /// 不 trim 就会绕开 issues/141 G2 的写侧判重（<see cref="IProcessRepository.FindCcActorIdsAsync"/>
    /// 拿回的已是 trim 后的串），同一人落两行（实测：先落 <c>" 9101 "</c> 再判重 <c>"9101"</c> ⇒ 2 行；
    /// 任务侧同款实测 <c>["leader","16601"," 16601 "]</c> 三行）。</para>
    /// <para><b>判空一律 trim 后判长</b>：严禁 <c>Where(t =&gt; t.Length &gt; 0)</c> 这种 trim <b>前</b>
    /// 判长（本栈 <c>ToStringList</c> 旧形状——它兜住了 <c>null</c> 转成的 <c>""</c>，兜不住 <c>"  "</c>）；
    /// 也严禁语言自带的假值判据：<c>"0"</c>／<c>"00"</c>／<c>"a"</c> 是三张不同的脸，
    /// 不是空值，也不得被松散比较折叠成同一个人（§2.10/§2.11 要求④反向哨兵，
    /// php 那把 <c>in_array</c> 松散尺子把 <c>'00'</c> 静默吃掉就是反面）。</para>
    /// <para>判据单点放在这里（与 <see cref="HasEffectiveCondition"/> 同一处），漏斗层（引擎/门面）与
    /// 写侧层（内存仓储 / MySQL 仓储 / SPI 默认实现）共用同一支，不在两个仓储里各抄一份（要求①）。</para>
    /// </summary>
    public static List<string> NormalizeActors(IEnumerable<string?>? raw)
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

    /// <summary>
    /// <b>归属值删除腿展开</b>（issues/137 §3-6 · spec 06-facade.md §processTask/removeTaskActor 语义 6，
    /// owner 2026-10-02 拍「两形并集」）：把待删列表展开成 <c>DELETE ... actor_id IN (...)</c> 真正要
    /// 绑定/匹配的值——<b>空值一律丢弃，非空值同时保留「原值」与「trim 值」两形</b>（保序、按字面去重）。
    /// <para><b>判据本体仍只有 <see cref="NormalizeActors(IEnumerable{string?})"/> 那一枚</b>：本方法把
    /// <b>单个元素</b>喂给它——返回空集合 ⇒ 该元素是空值（<c>null</c>/<c>""</c>/纯空白）⇒ 丢弃；
    /// 返回单元素 ⇒ 那个元素就是 trim 形。<b>不在这里抄第二份 trim/判空</b>
    /// （spec §2.11 尾注「不要再抄第二份，两份判据迟早分叉」明令；moon 腿 <c>actor_delete_forms</c> 同款做法）。</para>
    /// <para><b>为什么必须两形、只取一头各有一种假成功</b>（1.8.36 之前八栈正好分成这两派，没有一处两全）：</para>
    /// <list type="bullet">
    ///   <item><description>只取 <b>trim 形</b>（本栈 <c>MemoryRepository</c>／<c>MySqlRepository</c> 删除腿
    ///     与 php/rust/moon 的旧形状）⇒ 门面按语义 6 交出的历史脏行原值 <c>" 9101 "</c> 被削成 <c>9101</c>，
    ///     真库（MySQL NO PAD 排序规则）下那一行删不掉，门面却报成功——<b>被摘的人待办还在</b>；</description></item>
    ///   <item><description>只取 <b>原值形</b>（go/node/python/java 的旧形状）⇒ 第三方绕过门面直连仓储传
    ///     <c>" 8601 "</c> 时删不掉写侧归一后落库的规范行 <c>8601</c>（issues/142 §9.2 那一路）；
    ///     且空值照喂 <c>DELETE</c>，会把历史 <c>actor_id=''</c> 脏行批量误删（替脏数据做掉唯一痕迹）。</description></item>
    /// </list>
    /// <para>两形并集同时满足两侧：脏行按原值命中、规范行按 trim 形命中。按 §2.11 归一口径
    /// <c>" 9101 "</c> 与 <c>9101</c> 本就是<b>同一个人</b>，两行都删掉才是"摘掉这个人"的正确结果，不构成误删。
    /// 去重按<b>字面</b>做（<see cref="List{T}.Contains(T)"/> 默认 <c>StringComparer.Ordinal</c>），
    /// <b>不</b>按"trim 后相同"折叠原值形：<c>" 9101 "</c> 与 <c>"  9101  "</c> 是两种不同的原值形，都要保留
    /// （库里可能正是其中任一种脏法）。</para>
    /// <para><b>反向哨兵</b>同 <see cref="NormalizeActors(IEnumerable{string?})"/>：判空一律 trim 后判长，
    /// 严禁语言自带的假值判据——<c>"0"</c> 是合法 id 必须留下，且 <c>"0"</c> 与 <c>"00"</c> 是<b>两个人</b>
    /// （严禁松散比较把第二个静默折叠）。</para>
    /// <para><b>与写侧义务 <see cref="IProcessRepository.AddTaskActorAsync"/> 不同、别照抄</b>：写侧落库只取
    /// trim 形（同一人不得落两行）；删除腿多带一份原值，才删得掉修复前落下的未 trim 历史脏行。</para>
    /// </summary>
    /// <param name="raw">待删归属值集合，元素可为 <c>null</c>（<c>null</c> 丢弃，<b>不得</b>串化成 <c>"null"</c>）。</param>
    /// <returns>展开后的删除值列表（保序、按字面去重、无空值）；入参为 <c>null</c> 或全为空值时返回<b>空列表</b>
    /// ——调用方据此<b>早退，一条 <c>DELETE</c> 都不发</b>（空列表不得退化成"清空该任务全部参与者"）。</returns>
    public static List<string> ActorDeleteForms(IEnumerable<string?>? raw)
    {
        var outList = new List<string>();
        if (raw == null) return outList;
        foreach (var actorId in raw)
        {
            // 把单个元素喂给既有归一单点：返回空 ⇒ 空值（null/""/纯空白）⇒ 丢弃；返回 [t] ⇒ t 就是 trim 形。
            // 判据本体只有 NormalizeActors 一枚，不在这里抄第二份 trim/判空（spec §2.11 尾注）。
            var single = NormalizeActors(new[] { actorId });
            if (single.Count == 0) continue;                          // ① 空值丢弃，不喂 DELETE
            // 走到这里 actorId 必非 null（null 会被 NormalizeActors 丢成空集合），故下面的 ! 安全。
            if (!outList.Contains(actorId!)) outList.Add(actorId!);   // ② 原值形：保住未 trim 的历史脏行
            if (!outList.Contains(single[0])) outList.Add(single[0]); // ② trim 形：保住写侧归一后的规范行
        }
        return outList;
    }

    /// <summary>
    /// <b>旧名保留的转发</b>（issues/141 G10 落下的公开成员，已发布 NuGet 包 ⇒ 不删不改语义）：
    /// 判据本体已升格为通用的 <c>NormalizeActors</c>，
    /// cc 一支继续走同一枚（spec 06 §2.11「不要再抄第二份，两份判据迟早分叉」）。
    /// </summary>
    public static List<string> NormalizeCcActors(IEnumerable<string?>? raw) => NormalizeActors(raw);

    /// <summary>
    /// <b>两形入口</b>（spec 06 §2.11「逗号串与数组两形同判据」）：把调用方给的<b>原始入参</b>
    /// ——逗号串 <c>"a,,b"</c>／集合 <c>["a", null, "  "]</c>／标量 <c>123</c>——收敛成归一后的
    /// 归属值集合，元素判据完全走 <c>NormalizeActors</c> 那一枚，不另立尺子。
    /// <para><b>数组形态逐元素取值，绝不整条 <c>ToString()</c></b>：本栈普查（issues/142 §2 B 表）
    /// 按代码形状推定、本轮实机取证的反面形状——发起腿用 <c>GetStr</c>（＝<c>value.ToString()</c>）
    /// 读 <c>f_nextNodeOperator</c>，数组被串成
    /// <c>System.Collections.Generic.List`1[System.Object]</c> 当成<b>一个</b>参与者落进 <c>actor_id</c>。</para>
    /// <para>数字等非字符串标量按 <c>InvariantCulture</c> 收敛成字符串（<c>17301</c> ⇒ <c>"17301"</c>，
    /// 与其它栈 <c>String.valueOf</c> 同档，且不受机器区域设置影响）；嵌套集合展开后并入同一枚判据；
    /// 字典形态没有"一个归属值"可言（串化只会得到类型名/键值对串）⇒ 整体丢弃。</para>
    /// <para>归一后<b>为空 ⇒ 与"没填"同档</b>：调用方回落缺省或走既有缺参数信封。</para>
    /// </summary>
    public static List<string> NormalizeActors(object? raw)
    {
        var values = new List<string?>();
        CollectActorValues(values, raw);
        return NormalizeActors(values);
    }

    /// <summary>
    /// <b>单人归属参数</b>的归一（<c>processTask/transfer</c> 的 <c>fromActor</c>／<c>toActor</c>、
    /// <c>processInstance/updateCCStatus</c> 的 <c>operator</c> 等契约上"一个人"的参数）：
    /// 标量取 trim 后的串（<b>不拆逗号</b>——契约是单值，拆开等于静默丢掉一个人）；
    /// 集合形态（前端单选控件常给成 <c>["x"]</c>）过同一枚判据后<b>必须恰有一个</b>有效值，
    /// 0 个或多个都不是"一个人" ⇒ 返回 <c>null</c>，由调用方落进既有的"必填/缺参数"档。
    /// <para>旧形状：集合入参被 <c>ToString()</c> 成类型名 ⇒ 转办直接"原办理人不是该任务参与人"，
    /// 拼错时还会把类型名写进 <c>actor_id</c>。</para>
    /// </summary>
    public static string? NormalizeActorValue(object? raw) => raw switch
    {
        null => null,
        System.Collections.IDictionary => null,   // 字典形态没有归属值
        System.Collections.ICollection coll => SingleActor(coll),
        _ => SingleActorValue(ToActorString(raw)),
    };

    /// <summary>集合形态的单人档：过同一枚判据后必须恰有一个有效值。</summary>
    private static string? SingleActor(System.Collections.ICollection coll)
    {
        var values = new List<string?>();
        foreach (var o in coll) values.Add(ToActorString(o));
        var actors = NormalizeActors(values);
        return actors.Count == 1 ? actors[0] : null;
    }

    /// <summary>标量形态的单人档：trim 后为空 ⇒ null（与"没填"同档）。</summary>
    private static string? SingleActorValue(string? raw)
    {
        if (raw == null) return null;
        var trimmed = raw.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>两形入口的展开腿：串腿按逗号拆、集合腿逐元素递归、标量腿收敛成字符串。</summary>
    private static void CollectActorValues(List<string?> into, object? raw)
    {
        switch (raw)
        {
            case null:
                return;
            case string s:
                // C# 与 Java 同款："".Split(',') 得到【一个空元素】而不是零个 ⇒ 空值由判据那一腿丢掉
                foreach (var part in s.Split(',')) into.Add(part);
                return;
            case System.Collections.IDictionary:
                return;         // 字典形态没有归属值（串化只会得到类型名/键值对串）
            case System.Collections.IEnumerable en:
                foreach (var o in en) CollectActorValues(into, o);
                return;
            default:
                into.Add(ToActorString(raw));
                return;
        }
    }

    /// <summary>
    /// 单个元素 → 归属值串：数字/日期等 <see cref="System.IFormattable"/> 一律走
    /// <c>InvariantCulture</c>（不受区域设置影响，也不出现 <c>1,234</c> 这种分组分隔符），
    /// 集合/字典形态一律 <c>null</c>（<b>不允许类型名形状</b>），其余交给 <c>ToString()</c>
    /// （与其它栈 <c>String.valueOf</c> 同档；空元素由判据那一腿丢掉）。
    /// </summary>
    private static string? ToActorString(object? element) => element switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        System.Collections.IDictionary => null,
        System.Collections.IEnumerable => null,
        System.IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => element.ToString(),
    };

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

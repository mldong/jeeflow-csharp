using Xunit;
using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// 归属值<b>删除腿</b>「原值 ∪ trim 值」两形并集（issues/137 §3-6 · spec 06-facade.md
/// §processTask/removeTaskActor 语义 6 · owner 2026-10-02 拍「两形并集」· C# 栈单点纯函数＋内存仓一路）。
///
/// <para><b>为什么这一组存在</b>：1.8.36 之前 C# 删除腿（<c>MemoryRepository</c>／<c>MySqlRepository</c>）
/// 把待删实参再过一遍归一单点 <c>NormalizeActors</c> ⇒ <b>只留 trim 形</b>。门面按语义 6 交出的是
/// <b>行上的原值</b>（见 <c>ActionsMain.TaskRemoveActorAsync</c>：<c>toDelete.Add(row)</c> 加的是 row 本体），
/// 于是历史脏行 <c>" 9101"</c> 被削成 <c>9101</c>，内存里那一行按<b>字面</b>比不中 ⇒ 删不掉而门面报成功
/// （被摘的人待办还在）。这就是本栈改前的<b>假成功</b>，<see cref="MemoryRemoveDeletesUntrimmedDirtyRowByOriginalForm"/>
/// 与 <see cref="MemoryRemoveDeletesBothDirtyAndCanonicalAndKeepsOthers"/> 两格<b>改前必须是红的</b>。</para>
///
/// <para><b>两形并集</b>同时满足两侧：脏行按原值命中、规范行按 trim 形命中。反过来"只取原值形"会破
/// issues/142 §9.2 的既有判据（第三方直连仓储传 <c>" 8601 "</c> 删不掉写侧归一后的规范行 <c>8601</c>）——
/// <see cref="MemoryRemoveDeletesCanonicalRowByTrimForm"/> 就是那一路，<b>改前改后都要绿</b>。
/// 判据本体只有 <see cref="PageQuery.NormalizeActors"/> 一枚，删除腿变体 <see cref="PageQuery.ActorDeleteForms"/>
/// 把单个元素喂给它取 trim 形，<b>不抄第二份</b>（spec §2.11 尾注）。</para>
///
/// <para>MySQL 真库一路见 <see cref="MySqlRemoveTaskActorDeleteForms137Tests"/>：同一份入参，
/// 内存仓与 SQL 仓必须给同一个答案（issues/117 场景 27 那把尺子）。脏行夹具一律用<b>前导空格</b>
/// （MySQL 5.7 PAD SPACE 只忽略尾部、8.0 NO PAD 连尾部也算，前导空格在任何排序规则下都与规范行不等，
/// 判据不会漂）；内存仓按 <see cref="List{T}.Contains(T)"/> 的字面（Ordinal）比，前导空格同样稳妥。</para>
///
/// <para>种脏行<b>绕开写侧归一</b>：<see cref="MemoryRepository.AddTaskActorAsync"/> 会 trim＋丢空，
/// 正常路径建不出未 trim 的 <c>" 9101"</c>、也建不出 <c>actor_id=''</c> 行，故直接操作内存仓的
/// <c>TaskActors</c> 字典（<see cref="SeedDirtyRow"/>）。断言一律打在<b>仓储里真实存着的值</b>上
/// （<see cref="StoredActorIds"/> 读 <c>ActorRow.ActorId</c> 本体，不经任何转换），不只看返回的 Task 完成了。</para>
/// </summary>
public class RemoveTaskActorDeleteForms137Tests
{
    // ═══ 单点纯函数 PageQuery.ActorDeleteForms ═══

    /// <summary>空值全丢：null／""／纯空白／制表换行一个都不进并集（不喂 DELETE）。</summary>
    [Fact]
    public void DeleteFormsDropsEveryKindOfEmptyValue()
    {
        var forms = PageQuery.ActorDeleteForms(
            new List<string?> { null, "", " ", "   ", "\t", "\n", "\r", "\r\n", "\t\n", " \t\r\n " });
        Assert.Empty(forms);
    }

    /// <summary>入参 null ⇒ 空 List（不是 null、不抛异常）。</summary>
    [Fact]
    public void DeleteFormsNullInputYieldsEmptyList()
    {
        var forms = PageQuery.ActorDeleteForms(null);
        Assert.NotNull(forms);
        Assert.Empty(forms);
    }

    /// <summary>入参空集合 ⇒ 空 List。</summary>
    [Fact]
    public void DeleteFormsEmptyInputYieldsEmptyList()
    {
        Assert.Empty(PageQuery.ActorDeleteForms(new List<string?>()));
    }

    /// <summary>带空格值产出两形，且<b>原值在前、trim 形在后</b>（保序判据的第一层）。</summary>
    [Fact]
    public void DeleteFormsPaddedValueYieldsBothFormsOriginalFirst()
    {
        var forms = PageQuery.ActorDeleteForms(new List<string?> { " 9101 " });
        Assert.Equal(new List<string> { " 9101 ", "9101" }, forms);
    }

    /// <summary>已 trim 值只一份（原值==trim 值，按字面去重后只剩一条）。</summary>
    [Fact]
    public void DeleteFormsAlreadyTrimmedValueYieldsSingleForm()
    {
        var forms = PageQuery.ActorDeleteForms(new List<string?> { "9101" });
        Assert.Equal(new List<string> { "9101" }, forms);
    }

    /// <summary>跨元素去重：入参同时给 " 9101 " 与 "9101"，并集仍是两形、不重复。</summary>
    [Fact]
    public void DeleteFormsDeduplicatesAcrossElements()
    {
        var forms = PageQuery.ActorDeleteForms(new List<string?> { " 9101 ", "9101" });
        Assert.Equal(new List<string> { " 9101 ", "9101" }, forms);
    }

    /// <summary>
    /// 不同原值形各自保留：去重按<b>字面</b>做，<b>不</b>按"trim 后相同"折叠原值形——
    /// " 9101 " 与 "  9101  " 是两种不同的脏法（库里可能正是其中任一种），都要保留；trim 形只一份。
    /// </summary>
    [Fact]
    public void DeleteFormsKeepsDistinctOriginalFormsSeparately()
    {
        var forms = PageQuery.ActorDeleteForms(new List<string?> { " 9101 ", "  9101  " });
        Assert.Equal(new List<string> { " 9101 ", "9101", "  9101  " }, forms);
    }

    /// <summary>保序：按入参顺序，每个元素先原值形后 trim 形。</summary>
    [Fact]
    public void DeleteFormsPreservesInputOrder()
    {
        var forms = PageQuery.ActorDeleteForms(new List<string?> { " b ", "a", " c " });
        Assert.Equal(new List<string> { " b ", "b", "a", " c ", "c" }, forms);
    }

    /// <summary>反向哨兵："0" 是合法 id 必须留下，且 "0" 与 "00" 是<b>两个人</b>，不得被松散比较折叠。</summary>
    [Fact]
    public void DeleteFormsSentinelZeroAndDoubleZeroStayDistinct()
    {
        var forms = PageQuery.ActorDeleteForms(new List<string?> { "0", "00" });
        Assert.Equal(new List<string> { "0", "00" }, forms);
    }

    /// <summary>反向哨兵：带空格的 "0" 也产出两形（原值 " 0 " ＋ trim "0"），trim 后判长而非假值判据。</summary>
    [Fact]
    public void DeleteFormsSentinelPaddedZeroKeepsBothForms()
    {
        var forms = PageQuery.ActorDeleteForms(new List<string?> { " 0 " });
        Assert.Equal(new List<string> { " 0 ", "0" }, forms);
    }

    /// <summary>null 元素不得被串化成 "null"／类型名再去匹配（丢弃，不进并集）。</summary>
    [Fact]
    public void DeleteFormsNullElementIsNotStringified()
    {
        var forms = PageQuery.ActorDeleteForms(new List<string?> { null, "9101", null });
        Assert.Equal(new List<string> { "9101" }, forms);
        Assert.DoesNotContain("null", forms);
    }

    // ═══ 内存仓删除腿（RemoveTaskActorAsync）═══

    private readonly MemoryRepository _repo = new();

    /// <summary>某 taskId 的参与者行<b>原样读数</b>（归属列本体 ActorId，不经任何转换；按 id 升序＝插入序）。</summary>
    private List<string?> StoredActorIds(long taskId) =>
        _repo.TaskActors.Values
            .Where(a => a.ProcessTaskId == taskId)
            .OrderBy(a => a.Id)
            .Select(a => a.ActorId)
            .ToList();

    /// <summary>直接塞一行脏行（绕开写侧归一 AddTaskActorAsync——正常路径建不出未 trim／空串行）。</summary>
    private void SeedDirtyRow(long taskId, string? actorId, long syntheticRowId) =>
        _repo.TaskActors[syntheticRowId] = new MemoryRepository.ActorRow
        {
            Id = syntheticRowId,
            ProcessTaskId = taskId,
            ActorId = actorId,
            CreateTime = System.DateTime.Now,
        };

    /// <summary>
    /// N 档（<b>改前必红</b>）：未 trim 历史脏行 " 9101"（前导空格）＋ 写侧归一后的规范行 9101 并存，
    /// 删 [" 9101"]（门面交出的行原值）⇒ 脏行按原值形命中、规范行按 trim 形命中，<b>两行都真消失</b>。
    /// 改前 trim-only：实参被削成 9101，脏行 " 9101" 按字面比不中 ⇒ 留在库里而门面报成功（假成功）。
    /// </summary>
    [Fact]
    public async Task MemoryRemoveDeletesUntrimmedDirtyRowByOriginalForm()
    {
        const long taskId = 555001L;
        await _repo.AddTaskActorAsync(taskId, new List<string> { "zhangsan", "9101" });   // 规范行（写侧 trim）
        SeedDirtyRow(taskId, " 9101", 9_900_001);                                          // 历史未 trim 脏行（前导空格）
        Assert.Equal(new List<string?> { "zhangsan", "9101", " 9101" }, StoredActorIds(taskId));

        await _repo.RemoveTaskActorAsync(taskId, new List<string> { " 9101" });

        // 脏行与规范行都摘掉、zhangsan 一行不动
        Assert.Equal(new List<string?> { "zhangsan" }, StoredActorIds(taskId));
    }

    /// <summary>
    /// N 档（issues/142 §9.2 既有判据，<b>改前改后都要绿</b>）：删 [" 8601 "] 对写侧归一后的规范行 8601
    /// ⇒ trim 形 8601 命中，规范行消失。第三方绕过门面直连仓储传带空格值也删得掉规范行。
    /// </summary>
    [Fact]
    public async Task MemoryRemoveDeletesCanonicalRowByTrimForm()
    {
        const long taskId = 555002L;
        await _repo.AddTaskActorAsync(taskId, new List<string> { "leader", " 8601 " });   // 写侧 trim ⇒ 落 8601
        Assert.Equal(new List<string?> { "leader", "8601" }, StoredActorIds(taskId));

        await _repo.RemoveTaskActorAsync(taskId, new List<string> { " 8601 " });

        // 规范行 8601 被 trim 形命中删除、leader 不动
        Assert.Equal(new List<string?> { "leader" }, StoredActorIds(taskId));
    }

    /// <summary>
    /// N 档（<b>改前必红</b>）：脏行 " 9202" 与规范行 9202 并存，删 [" 9202"] ⇒ 两行都摘掉，
    /// 其余参与人（zhangsan／lisi）一行不动。改前 trim-only 只删得掉规范行、脏行留下。
    /// </summary>
    [Fact]
    public async Task MemoryRemoveDeletesBothDirtyAndCanonicalAndKeepsOthers()
    {
        const long taskId = 555003L;
        await _repo.AddTaskActorAsync(taskId, new List<string> { "zhangsan", "9202", "lisi" });
        SeedDirtyRow(taskId, " 9202", 9_900_003);

        await _repo.RemoveTaskActorAsync(taskId, new List<string> { " 9202" });

        // 脏行与规范行都摘掉，其余参与人原样保留（含顺序）
        Assert.Equal(new List<string?> { "zhangsan", "lisi" }, StoredActorIds(taskId));
    }

    /// <summary>
    /// P 档：空值入参（""／纯空白／null／空 List）一律<b>零删除</b>（并集为空则早退），
    /// 历史 actor_id=''／'   ' 脏行不得被批量误删，<b>更不得清空全部参与者</b>（issues/129 删除位对偶）。
    /// </summary>
    [Fact]
    public async Task MemoryRemoveEmptyInputsNeverDeleteAndNeverClearAll()
    {
        const long taskId = 555004L;
        await _repo.AddTaskActorAsync(taskId, new List<string> { "zhangsan", "9001" });
        SeedDirtyRow(taskId, "", 9_900_004);        // 历史 actor_id='' 脏行
        SeedDirtyRow(taskId, "   ", 9_900_005);     // 历史纯空白脏行
        var before = StoredActorIds(taskId);

        await _repo.RemoveTaskActorAsync(taskId, new List<string> { "" });
        await _repo.RemoveTaskActorAsync(taskId, new List<string> { "  ", null! });
        await _repo.RemoveTaskActorAsync(taskId, new List<string>());
        await _repo.RemoveTaskActorAsync(taskId, null!);

        // 四种空值入参一条都不删（早退），真人＋脏行原样还在
        Assert.Equal(before, StoredActorIds(taskId));
        Assert.Contains("", StoredActorIds(taskId));       // '' 脏行未被误删
        Assert.Contains("   ", StoredActorIds(taskId));    // 纯空白脏行未被误删
    }

    /// <summary>P 档：null 元素不得被串化成 ""／"null"／类型名再去匹配（并集为空 ⇒ 早退，脏行都在）。</summary>
    [Fact]
    public async Task MemoryRemoveNullElementIsNotStringified()
    {
        const long taskId = 555005L;
        await _repo.AddTaskActorAsync(taskId, new List<string> { "zhangsan" });
        SeedDirtyRow(taskId, "null", 9_900_006);   // 若 null 被串化成 "null" 就会误删这一行
        SeedDirtyRow(taskId, "", 9_900_007);       // 若 null 被兜成 "" 就会误删这一行

        await _repo.RemoveTaskActorAsync(taskId, new List<string> { null! });

        // null 元素既不串成 "null" 也不兜成 ""：并集为空 ⇒ 早退，两行脏行都在
        Assert.Equal(new List<string?> { "zhangsan", "null", "" }, StoredActorIds(taskId));
    }

    /// <summary>P 档：非参与者静默忽略；任务不存在 ⇒ 零操作、不抛异常。</summary>
    [Fact]
    public async Task MemoryRemoveNonParticipantIsIgnoredAndMissingTaskIsNoOp()
    {
        const long taskId = 555006L;
        await _repo.AddTaskActorAsync(taskId, new List<string> { "zhangsan", "9001" });

        await _repo.RemoveTaskActorAsync(taskId, new List<string> { "ghost" });   // 非参与者
        // 非参与者静默忽略、不误删真人
        Assert.Equal(new List<string?> { "zhangsan", "9001" }, StoredActorIds(taskId));

        await _repo.RemoveTaskActorAsync(424242L, new List<string> { "zhangsan" });   // 任务不存在
        // 任务不存在 ⇒ 零操作，原任务不受影响
        Assert.Equal(new List<string?> { "zhangsan", "9001" }, StoredActorIds(taskId));
        Assert.Empty(StoredActorIds(424242L));
    }

    /// <summary>反向哨兵：摘 "0" 不得连带摘 "00"（"0" 与 "00" 是两个人，字面比不折叠）。</summary>
    [Fact]
    public async Task MemoryRemoveZeroDoesNotRemoveDoubleZero()
    {
        const long taskId = 555007L;
        await _repo.AddTaskActorAsync(taskId, new List<string> { "0", "00", "zhangsan" });
        Assert.Equal(new List<string?> { "0", "00", "zhangsan" }, StoredActorIds(taskId));

        await _repo.RemoveTaskActorAsync(taskId, new List<string> { "0" });

        // 摘 "0" 只删 "0"，"00" 与 zhangsan 都在
        Assert.Equal(new List<string?> { "00", "zhangsan" }, StoredActorIds(taskId));
    }
}

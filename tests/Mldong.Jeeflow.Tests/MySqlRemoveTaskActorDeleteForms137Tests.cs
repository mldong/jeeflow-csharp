using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Repository.MySql;
using MySqlConnector;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// 归属值<b>删除腿</b>「原值 ∪ trim 值」两形并集的 <b>MySQL 真库一路</b>
/// （issues/137 §3-6 · spec 06-facade.md §processTask/removeTaskActor 语义 6 · C# 栈）。
///
/// <para>内存一路＋单点纯函数见 <see cref="RemoveTaskActorDeleteForms137Tests"/>；本文件只补内存档照不到的事：
/// <b>并集真进了 <c>DELETE ... actor_id IN (...)</c>，脏行/规范行按真库排序规则真被删掉</b>，
/// 而不是只在内存对象里干净。断言一律直接 <c>SELECT</c> 真实列值（<see cref="ActorIdsSortedAsync"/>）。</para>
///
/// <para><b>两仓同一枚判据</b>（spec §2.11 ＋ issues/117 场景 27 那把尺子）：同一份入参，内存仓与 SQL 仓
/// 必须给同一个答案——判据单点只有 <see cref="PageQuery.ActorDeleteForms"/> 一枚（trim/判空本体仍是
/// <see cref="PageQuery.NormalizeActors"/>），两仓各调它、不各抄一份。</para>
///
/// <para><b>脏行夹具一律用前导空格</b>（<c>" 9101"</c>）：MySQL 5.7 PAD SPACE 只忽略尾部、8.0 NO PAD 连尾部也算，
/// 前导空格在<b>任何</b>排序规则下都与规范行 <c>9101</c> 不等，改前 trim-only 的 <c>DELETE ... IN ('9101')</c>
/// 一定删不掉它（<b>改前必红</b>那格的判据不会漂）。种脏行<b>绕开写侧归一</b>：直接 <c>INSERT</c>
/// （<see cref="SeedDirtyRowAsync"/>），因为 <see cref="MySqlRepository.AddTaskActorAsync"/> 会 trim＋丢空，
/// 正常路径建不出未 trim／空串行。</para>
///
/// <para>连不上 160 算 fail 不算 skip（本仓 R6 发版机口径，与 <see cref="MySqlActorWriteNormalization142Tests"/>
/// 同批）；用 <c>941490000-941499999</c> 合成任务 id 段（无外键约束，直插参与者行即可，不建 define/instance），
/// 进出各清一次，worker 段用 <c>137</c> 避开夹具(1)/141/142/148/他语言。</para>
/// </summary>
[Collection("mysql")]
[Trait("Category", "mysql-smoke")]
public class MySqlRemoveTaskActorDeleteForms137Tests : IAsyncLifetime
{
    private const long SyntheticTaskLo = 941490000;
    private const long SyntheticTaskHi = 941499999;
    private const long DirtyRowIdBase = 941495000;

    private readonly MySqlConnectionFactory _factory;
    private readonly MySqlRepository _repo;

    private static int _taskSeq;
    private static int _rowSeq;

    public MySqlRemoveTaskActorDeleteForms137Tests(MySqlFixture fx)
    {
        _factory = fx.Factory;
        var clock = new FixedClock(new DateTime(2026, 8, 1, 9, 0, 0));
        _repo = new MySqlRepository(_factory);
        var ctx = new ServiceContext(_repo)
        {
            Clock = clock,
            IdGenerator = new AtomicIdGenerator(137L, clock),   // 独立 worker 段，避开夹具(1)/141/142/148/他语言
        };
        _repo.Configure(ctx);
    }

    public async Task InitializeAsync() => await PurgeAsync();
    public async Task DisposeAsync() => await PurgeAsync();

    private async Task PurgeAsync()
    {
        await using var conn = await _factory.OpenAsync();
        foreach (var sql in new[]
        {
            $"DELETE FROM wf_process_task_actor WHERE process_task_id BETWEEN {SyntheticTaskLo} AND {SyntheticTaskHi}",
            $"DELETE FROM wf_process_task_actor WHERE id BETWEEN {SyntheticTaskLo} AND {SyntheticTaskHi}",
        })
        {
            await using var cmd = new MySqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ── 夹具与取证辅助 ──

    private static long NextTaskId() => SyntheticTaskLo + 1 + Interlocked.Increment(ref _taskSeq);
    private static long NextDirtyRowId() => DirtyRowIdBase + Interlocked.Increment(ref _rowSeq);

    /// <summary>直接 INSERT 一行脏行（绕开写侧归一 AddTaskActorAsync——正常路径建不出未 trim／空串行）。</summary>
    private async Task SeedDirtyRowAsync(long taskId, string actorId)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = new MySqlCommand(
            "INSERT INTO wf_process_task_actor (id, process_task_id, actor_id, create_time) VALUES (@i, @t, @a, NOW())",
            conn);
        cmd.Parameters.AddWithValue("@i", NextDirtyRowId());
        cmd.Parameters.AddWithValue("@t", taskId);
        cmd.Parameters.AddWithValue("@a", actorId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>真表取证：某任务 actor_id 的全量读数（按 Ordinal 排序＝只断成员，行序无契约）。</summary>
    private async Task<List<string>> ActorIdsSortedAsync(long taskId)
    {
        var rows = new List<string>();
        await using var conn = await _factory.OpenAsync();
        await using var cmd = new MySqlCommand(
            "SELECT actor_id FROM wf_process_task_actor WHERE process_task_id = @t", conn);
        cmd.Parameters.AddWithValue("@t", taskId);
        await using var rs = await cmd.ExecuteReaderAsync();
        while (await rs.ReadAsync()) rows.Add(rs.GetString(0));
        return rows.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    // ═══ N 档：并集两形真删（真库列值读数）═══

    /// <summary>
    /// N 档（<b>改前必红</b>）：未 trim 历史脏行 " 9101"（前导空格）＋ 写侧归一后的规范行 9101 并存，
    /// 删 [" 9101"]（门面交出的行原值）⇒ 真库里两行都消失。改前 trim-only 的 <c>DELETE ... IN ('9101')</c>
    /// 在任何排序规则下都命不中 ' 9101'（前导空格）⇒ 脏行留在库里而门面报成功（假成功）。
    /// </summary>
    [Fact]
    public async Task SqlRemoveDeletesUntrimmedDirtyRowByOriginalForm()
    {
        var taskId = NextTaskId();
        await _repo.AddTaskActorAsync(taskId, new List<string> { "zhangsan", "9101" });   // 规范行（写侧 trim）
        await SeedDirtyRowAsync(taskId, " 9101");                                          // 历史未 trim 脏行（前导空格）
        Assert.Equal(new List<string> { " 9101", "9101", "zhangsan" }, await ActorIdsSortedAsync(taskId));

        await _repo.RemoveTaskActorAsync(taskId, new List<string> { " 9101" });

        // 脏行与规范行都摘掉、zhangsan 一行不动
        Assert.Equal(new List<string> { "zhangsan" }, await ActorIdsSortedAsync(taskId));
    }

    /// <summary>
    /// N 档（issues/142 §9.2 既有判据，<b>改前改后都要绿</b>）：删 [" 8601 "] 对写侧归一后的规范行 8601
    /// ⇒ trim 形 8601 进 IN 命中，规范行消失。第三方绕过门面直连仓储传带空格值也删得掉规范行。
    /// </summary>
    [Fact]
    public async Task SqlRemoveDeletesCanonicalRowByTrimForm()
    {
        var taskId = NextTaskId();
        await _repo.AddTaskActorAsync(taskId, new List<string> { "leader", " 8601 " });   // 写侧 trim ⇒ 落 8601
        Assert.Equal(new List<string> { "8601", "leader" }, await ActorIdsSortedAsync(taskId));

        await _repo.RemoveTaskActorAsync(taskId, new List<string> { " 8601 " });

        // 规范行 8601 被 trim 形命中删除、leader 不动
        Assert.Equal(new List<string> { "leader" }, await ActorIdsSortedAsync(taskId));
    }

    /// <summary>
    /// N 档（<b>改前必红</b>）：脏行 " 9202" 与规范行 9202 并存，删 [" 9202"] ⇒ 真库两行都摘掉，
    /// 其余参与人（zhangsan／lisi）一行不动。改前 trim-only 只删得掉规范行、脏行留下。
    /// </summary>
    [Fact]
    public async Task SqlRemoveDeletesBothDirtyAndCanonicalAndKeepsOthers()
    {
        var taskId = NextTaskId();
        await _repo.AddTaskActorAsync(taskId, new List<string> { "zhangsan", "9202", "lisi" });
        await SeedDirtyRowAsync(taskId, " 9202");

        await _repo.RemoveTaskActorAsync(taskId, new List<string> { " 9202" });

        // 脏行与规范行都摘掉，其余参与人原样保留
        Assert.Equal(new List<string> { "lisi", "zhangsan" }, await ActorIdsSortedAsync(taskId));
    }

    // ═══ P 档：空值零删除、绝不清空全部参与者 ═══

    /// <summary>
    /// P 档：空值入参（""／纯空白／null／空 List）一律<b>零删除</b>（并集为空则一条 DELETE 都不发），
    /// 历史 actor_id=''／'   ' 脏行不得被批量误删，<b>更不得清空全部参与者</b>（issues/129 删除位对偶）。
    /// </summary>
    [Fact]
    public async Task SqlRemoveEmptyInputsNeverDeleteAndNeverClearAll()
    {
        var taskId = NextTaskId();
        await _repo.AddTaskActorAsync(taskId, new List<string> { "zhangsan", "9001" });
        await SeedDirtyRowAsync(taskId, "");        // 历史 actor_id='' 脏行
        await SeedDirtyRowAsync(taskId, "   ");     // 历史纯空白脏行
        var before = await ActorIdsSortedAsync(taskId);

        await _repo.RemoveTaskActorAsync(taskId, new List<string> { "" });
        await _repo.RemoveTaskActorAsync(taskId, new List<string> { "  ", null! });
        await _repo.RemoveTaskActorAsync(taskId, new List<string>());
        await _repo.RemoveTaskActorAsync(taskId, null!);

        // 四种空值入参一条都不删（早退），真人＋脏行原样还在
        Assert.Equal(before, await ActorIdsSortedAsync(taskId));
        Assert.Equal(new List<string> { "", "   ", "9001", "zhangsan" }, await ActorIdsSortedAsync(taskId));
    }

    /// <summary>P 档：null 元素不得被串化成 ""／"null"／类型名再去匹配（并集为空 ⇒ 早退，脏行都在）。</summary>
    [Fact]
    public async Task SqlRemoveNullElementIsNotStringified()
    {
        var taskId = NextTaskId();
        await _repo.AddTaskActorAsync(taskId, new List<string> { "zhangsan" });
        await SeedDirtyRowAsync(taskId, "null");   // 若 null 被串化成 "null" 就会误删这一行
        await SeedDirtyRowAsync(taskId, "");       // 若 null 被兜成 "" 就会误删这一行

        await _repo.RemoveTaskActorAsync(taskId, new List<string> { null! });

        Assert.Equal(new List<string> { "", "null", "zhangsan" }, await ActorIdsSortedAsync(taskId));
    }

    /// <summary>P 档：非参与者静默忽略；任务不存在 ⇒ 零操作、不抛异常。</summary>
    [Fact]
    public async Task SqlRemoveNonParticipantIsIgnoredAndMissingTaskIsNoOp()
    {
        var taskId = NextTaskId();
        await _repo.AddTaskActorAsync(taskId, new List<string> { "zhangsan", "9001" });

        await _repo.RemoveTaskActorAsync(taskId, new List<string> { "ghost" });   // 非参与者
        Assert.Equal(new List<string> { "9001", "zhangsan" }, await ActorIdsSortedAsync(taskId));

        var emptyTaskId = NextTaskId();                                          // 没有任何参与者行的任务
        await _repo.RemoveTaskActorAsync(emptyTaskId, new List<string> { "zhangsan" });
        Assert.Equal(new List<string> { "9001", "zhangsan" }, await ActorIdsSortedAsync(taskId));   // 原任务不受影响
        Assert.Empty(await ActorIdsSortedAsync(emptyTaskId));
    }

    /// <summary>反向哨兵：摘 "0" 不得连带摘 "00"（"0" 与 "00" 是两个人，真库列值字面比不折叠）。</summary>
    [Fact]
    public async Task SqlRemoveZeroDoesNotRemoveDoubleZero()
    {
        var taskId = NextTaskId();
        await _repo.AddTaskActorAsync(taskId, new List<string> { "0", "00", "zhangsan" });
        Assert.Equal(new List<string> { "0", "00", "zhangsan" }, await ActorIdsSortedAsync(taskId));

        await _repo.RemoveTaskActorAsync(taskId, new List<string> { "0" });

        Assert.Equal(new List<string> { "00", "zhangsan" }, await ActorIdsSortedAsync(taskId));
    }
}

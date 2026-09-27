using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Repository.MySql;
using MySqlConnector;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/125 的行为判据：逾期计数的 now 必须来自**引擎时钟出口**（本栈是 <see cref="IClock"/>，
/// 也就是 <c>create_time/update_time</c> 所写的那把），不是 MySQL 的 <c>NOW()</c>（取 <c>@@session.time_zone</c> 的墙钟）。
///
/// 判据形状：
/// 1. **注入即变（有牙）**——把 <c>FixedClock</c> 前移 2 小时，逾期数必须跟着变。
///    旧实现里 SQL 写死 <c>NOW()</c>，注入钟挪不动 ⇒ 这一条当场红。与 jeeflow-java 的
///    <c>JdbcStatsOverdueClockTest</c> 同款（内存仓 <c>MemoryRepository</c> 早就用 <c>Clock.Now</c>，
///    只有 MySql 仓绕过了它，所以这条同时是"两仓同基准"的对拍）。
/// 2. **边界**——<c>expire_time IS NULL</c> 的行钟拨到 +30 天仍不计入。
/// 3. **回归**——pending 半边不受钟影响；同注入值重复调用读数稳定。
/// 4. **会话钟确有牙（危害见证）**——同一批夹具行用旧写法 <c>expire_time &lt; NOW()</c> 直查，
///    会话 <c>+08:00</c> 与 <c>+00:00</c> 下读数必须不同；相同就说明样本没落进两把钟的夹层，
///    本测试判失败而不是"绿"（issues/125 立案时踩过的恒绿死格）。
///    注：这一腿用测试自己的连接直查，是因为 MySqlConnector 的连接池不让外部把仓储钉在
///    指定时区的会话上；仓储侧的"与时区无关"由判据 1（绑的是引擎钟，SQL 里没有时间函数）保证，
///    跨栈全链的"换会话时区读数不变"实弹在 go 栈 <c>stats_clock_test.go</c> 上跑。
/// 5. **形状门禁**——方法体（剥掉整行注释后）不许再出现 <c>NOW()</c>，且必须是绑参 + <c>Clock.Now</c>。
///
/// 夹具只写自己的 id 段（931000–931099，含一条真实例行——本仓 <c>PurgeT1SegmentAsync</c> 会把
/// "实例不存在的孤儿任务"当垃圾清掉，不挂实例的夹具会被自己人扫走），进出都按 id 段删。
/// 统计口径是全表的，断言一律用"其余行 + 夹具"的差量 ⇒ 抗并发插入。
/// </summary>
[Collection("mysql")]
[Trait("Category", "mysql-smoke")]
public class StatsClockTests
{
    private const long PiId = 931000;
    private const long IdMin = 931001;
    private const long IdMax = 931099;

    private static readonly MySqlConnectionFactory F = TestDb.Factory();

    private static async Task<MySqlConnection> OpenAsync()
    {
        var conn = await F.OpenAsync();
        return conn;
    }

    private static async Task ExecAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        var v = await cmd.ExecuteScalarAsync();
        return v is null || v is DBNull ? 0L : Convert.ToInt64(v);
    }

    /// <summary>夹具四行：①无到期时间 ②早于 BASE 1 小时 ③晚于 BASE 1 小时 ④晚于 BASE 8 小时。</summary>
    private static async Task<DateTime> SeedFixtureAsync()
    {
        await PurgeAsync();
        // 秒级精度：列是 DATETIME(3)，但把毫秒削掉能让"注入钟 / 写库值 / 期望算法"三者逐位可核对
        var now = DateTime.Now;
        var baseNow = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second);
        // 实例行必须真存在：本仓的孤儿清理会把"实例不存在的任务"当垃圾扫掉
        await ExecAsync(
            "INSERT INTO wf_process_instance (id, business_no, process_define_id, state, create_time) " +
            "VALUES (@id, @bn, @did, 10, @ct)",
            ("@id", PiId), ("@bn", "CS125-clock"), ("@did", PiId), ("@ct", baseNow));
        var rows = new (long Id, DateTime? Expire)[]
        {
            (IdMin, null),
            (IdMin + 1, baseNow.AddHours(-1)),
            (IdMin + 2, baseNow.AddHours(1)),
            (IdMin + 3, baseNow.AddHours(8)),
        };
        foreach (var (id, expire) in rows)
        {
            object expArg = expire.HasValue ? expire.Value : DBNull.Value;
            await ExecAsync(
                "INSERT INTO wf_process_task (id, process_instance_id, task_name, display_name, task_state, expire_time, create_time) " +
                "VALUES (@id, @pi, @tn, @dn, 10, @exp, @ct)",
                ("@id", id), ("@pi", PiId), ("@tn", "t125"), ("@dn", "125 判据夹具"),
                ("@exp", expArg), ("@ct", baseNow));
        }
        return baseNow;
    }

    private static async Task PurgeAsync()
    {
        await ExecAsync("DELETE FROM wf_process_task WHERE id BETWEEN @a AND @b", ("@a", IdMin), ("@b", IdMax));
        await ExecAsync("DELETE FROM wf_process_instance WHERE id = @id", ("@id", PiId));
    }

    /// <summary>用注入钟算"其余行"的 (pending, overdue)——与仓储同口径，但不经被测实现。</summary>
    private static async Task<(long Pending, long Overdue)> OthersAsync(DateTime now)
    {
        var pending = await ScalarAsync(
            "SELECT COUNT(*) FROM wf_process_task WHERE task_state = 10 AND id NOT BETWEEN @a AND @b",
            ("@a", IdMin), ("@b", IdMax));
        var overdue = await ScalarAsync(
            "SELECT COALESCE(SUM(CASE WHEN expire_time IS NOT NULL AND expire_time < @now THEN 1 ELSE 0 END), 0) " +
            "FROM wf_process_task WHERE task_state = 10 AND id NOT BETWEEN @a AND @b",
            ("@now", now), ("@a", IdMin), ("@b", IdMax));
        return (pending, overdue);
    }

    /// <summary>期望的夹具逾期数：expire 非空且早于 (BASE+shift) 的行数。</summary>
    private static long ExpectedFixtureOverdue(DateTime @base, double shiftHours)
    {
        var cut = @base.AddHours(shiftHours);
        long n = 0;
        foreach (var h in new[] { -1.0, 1.0, 8.0 })
            if (@base.AddHours(h) < cut) n++;
        return n;
    }

    private static MySqlRepository RepoWithClock(DateTime now)
    {
        var repo = new MySqlRepository(F);
        var ext = new MySqlExtRepository(F, repo);
        var ctx = new ServiceContext(repo, ext) { Clock = new FixedClock(now) };
        repo.Configure(ctx);
        return repo;
    }

    [Fact]
    public async Task StatsOverdue_JudgedByEngineClock_NotDbSessionClock()
    {
        var baseNow = await SeedFixtureAsync();
        try
        {
            // 夹具落位自证：四行都在办
            Assert.Equal(4, await ScalarAsync(
                "SELECT COUNT(*) FROM wf_process_task WHERE task_state = 10 AND id BETWEEN @a AND @b",
                ("@a", IdMin), ("@b", IdMax)));

            // ── 判据 1：注入钟 BASE ⇒ 只有 -1h 那条逾期 ─────────────────────────
            var (othersP, othersO) = await OthersAsync(baseNow);
            var atBase = await RepoWithClock(baseNow).StatsPendingAndOverdueCountAsync();
            Assert.Equal(othersP + 4, atBase[0]);
            Assert.Equal(othersO + ExpectedFixtureOverdue(baseNow, 0), atBase[1]);
            Assert.Equal(othersO + 1, atBase[1]); // 期望值双写死：BASE 下恰好 1 条

            // ── 判据 1 的牙：钟前移 2 小时 ⇒ +1h 那条转为逾期（旧实现不动） ──────
            var moved = baseNow.AddHours(2);
            var (movedP, movedO) = await OthersAsync(moved);
            var atMoved = await RepoWithClock(moved).StatsPendingAndOverdueCountAsync();
            Assert.Equal(movedO + ExpectedFixtureOverdue(baseNow, 2), atMoved[1]);
            Assert.True(atMoved[1] > atBase[1],
                $"钟前移 2 小时后逾期数没跟着动（{atBase[1]} → {atMoved[1]}）⇒ now 不是引擎侧供给的");
            Assert.Equal(movedP + 4, atMoved[0]); // pending 半边不受钟影响

            // ── 判据 2（边界）：钟拨到 +30 天，NULL 到期时间的行仍不计入 ─────────
            var far = baseNow.AddDays(30);
            var (farP, farO) = await OthersAsync(far);
            var atFar = await RepoWithClock(far).StatsPendingAndOverdueCountAsync();
            Assert.Equal(farO + ExpectedFixtureOverdue(baseNow, 30 * 24), atFar[1]);
            Assert.Equal(farO + 3, atFar[1]); // 只有三条带到期时间的行
            Assert.Equal(farP + 4, atFar[0]);

            // ── 判据 3（回归）：同注入值重复调用必须稳定 ────────────────────────
            var again = await RepoWithClock(baseNow).StatsPendingAndOverdueCountAsync();
            Assert.Equal(atBase, again);
        }
        finally
        {
            await PurgeAsync();
        }
    }

    [Fact]
    public async Task StatsOverdue_SessionClockWouldHaveMovedTheAnswer_TeethWitness()
    {
        // 夹具落位（基准钟只用于摆放样本，本条判据比的是两把会话钟的读数差）
        await SeedFixtureAsync();
        try
        {
            // 旧写法（SQL 里 NOW()）在两个会话时区下各读一次：必须不同，否则夹具没牙、
            // 上面那组判据也只是"恒绿死格"。这里用测试自己的连接，会话变量随连接走。
            var east8 = await RawNowCountAsync("+08:00");
            var utc = await RawNowCountAsync("+00:00");
            Assert.True(east8 != utc,
                $"夹具在两把会话钟下读数相同（都 {east8}）⇒ 样本没落进时区夹层，这条判据抓不到本案病灶");
        }
        finally
        {
            await PurgeAsync();
        }
    }

    /// <summary>在受控会话时区下，用旧写法直查我那四行夹具的逾期数（并回读会话时区自证 SET 生效）。</summary>
    private static async Task<long> RawNowCountAsync(string tz)
    {
        await using var conn = await OpenAsync();
        await using (var set = new MySqlCommand($"SET time_zone = '{tz}'", conn))
        {
            await set.ExecuteNonQueryAsync();
        }
        await using var cmd = new MySqlCommand(
            "SELECT @@session.time_zone, " +
            "COALESCE(SUM(CASE WHEN expire_time IS NOT NULL AND expire_time < NOW() THEN 1 ELSE 0 END), 0) " +
            "FROM wf_process_task WHERE task_state = 10 AND id BETWEEN @a AND @b", conn);
        cmd.Parameters.AddWithValue("@a", IdMin);
        cmd.Parameters.AddWithValue("@b", IdMax);
        await using var rs = await cmd.ExecuteReaderAsync();
        Assert.True(await rs.ReadAsync(), "读不到会话时区与逾期计数");
        var applied = rs.GetString(0);
        var overdue = rs.GetInt32(1);
        Assert.Equal(tz, applied); // SET 没落到这条连接上 ⇒ 探针失效，宁可判失败也不给假绿
        return overdue;
    }

    [Fact]
    public void StatsSqlSourceMustNotHandTheClockToTheDatabase()
    {
        var path = FindRepoSourcePath();
        var src = File.ReadAllText(path).Replace("\r\n", "\n");
        const string head = "public virtual async Task<int[]> StatsPendingAndOverdueCountAsync";
        var i = src.IndexOf(head, StringComparison.Ordinal);
        Assert.True(i >= 0, $"在 {path} 里找不到 {head} —— 方法被改名/挪走，这条判据要先跟着调整");
        var rest = src.Substring(i + head.Length);
        var end = rest.IndexOf("\n    public ", StringComparison.Ordinal);
        Assert.True(end >= 0, "方法体切不出边界（下个成员分隔符没匹配上）");
        var body = rest.Substring(0, end);

        // 整行注释里写 NOW() 是在解释缺陷，不是病灶 ⇒ 先剥掉再数
        var code = string.Join("\n", body.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.True(!code.Contains("NOW()", StringComparison.Ordinal),
            "StatsPendingAndOverdueCountAsync 的代码里仍有 NOW() ⇒ 又把钟交给数据库了");
        Assert.True(code.Contains("expire_time < ?", StringComparison.Ordinal),
            "逾期判据不是绑参形状（没找到 `expire_time < ?`）");
        Assert.True(code.Contains("Clock.Now", StringComparison.Ordinal),
            "逾期判据没走 Clock 时钟出口（内存仓与 SQL 仓必须同一把）");
    }

    private static string FindRepoSourcePath()
    {
        // 从测试运行目录一路往上找仓根（bin/Debug/net10.0 距仓根的层数会随 dotnet 版本/配置变，
        // 写死 "../../.." 这类相对层数会静默找不到——本仓 schema 的查找就靠列了一串候选兜着）
        const string rel = "src/Mldong.Jeeflow.Repository.MySql/Repo/MySqlRepository.cs";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, rel);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException($"从 {AppContext.BaseDirectory} 往上 10 层找不到 {rel}");
    }
}

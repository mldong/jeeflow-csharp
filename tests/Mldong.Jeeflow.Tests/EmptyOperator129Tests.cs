using System.Text;
using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;
using Mldong.Jeeflow.Repository.MySql;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/129 · 空串 <c>operator</c> 与缺键同档，归属谓词空值不得读全库。
///
/// 判据基准：Java 参考实现 <c>JeeflowFacade.operatorArg</c> + <c>JdbcProcessRepository.buildWhere</c>，
/// 规范条文 spec <c>06-facade.md §2.5</c>（"空串与缺键同档" + "归属过滤是双层义务" +
/// "只收归属谓词，不是 PageQuery 通用放行"）。
///
/// C# 的两层落点与 java 同形：门面 <see cref="JeeflowFacade"/> 的 12 处缺省点、
/// <see cref="MySqlRepository.BuildWhere"/> 与 <see cref="MemoryRepository"/> 的
/// <c>ApplyConditions</c>（同栈两仓必须同判据，否则 T0 绿而生产红）。
///
/// 反空转设计：三档相等的前提是 user1 档<b>非空</b>——否则"空串 0 行 == 缺键 0 行"是自等假绿；
/// 另设"待办在 leader 手里"一格，挡住"把空值实现成不加条件"这种假修（正是本 issue 的生产症状）。
/// </summary>
public class EmptyOperator129Tests
{
    private const string ChainFlow = """
        {"name": "chain129", "displayName": "Chain129", "type": "approval",
         "nodes": [
           {"id": "start", "type": "snaker:start", "text": {"value": "Start"}},
           {"id": "apply", "type": "snaker:task", "text": {"value": "Apply"},
            "properties": {"assignee": "applicant"}},
           {"id": "t1", "type": "snaker:task", "text": {"value": "T1"},
            "properties": {"assignee": "leader"}},
           {"id": "end", "type": "snaker:end", "text": {"value": "End"}}
         ],
         "edges": [
           {"id": "e1", "sourceNodeId": "start", "targetNodeId": "apply"},
           {"id": "e2", "sourceNodeId": "apply", "targetNodeId": "t1"},
           {"id": "e3", "sourceNodeId": "t1", "targetNodeId": "end"}
         ]}
        """;

    private static readonly string[] Exits =
    {
        "processInstance/page", "processTask/todoList", "processTask/doneList", "processInstance/ccList",
    };

    /// <summary>造"user1 有自己的行 + zhangsan 也有自己的行"，并给 user1 抄送一条。</summary>
    private static async Task<JeeflowFacade> SeedAsync()
    {
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "chain129", ChainFlow);
        var facade = new JeeflowFacade(ctx);
        await facade.FlowAsync("processInstance/startAndExecute",
            new FlowData { ["processDefineId"] = did, ["operator"] = "user1" });
        var other = await facade.FlowAsync("processInstance/startAndExecute",
            new FlowData { ["processDefineId"] = did, ["operator"] = "zhangsan" });
        Assert.Equal(0, Convert.ToInt32(other["code"]));
        var otherInst = ((Dictionary<string, object?>)other["data"]!)["processInstanceId"];
        await facade.FlowAsync("processInstance/createCCInstance", new FlowData
        {
            ["processInstanceId"] = otherInst,
            ["operator"] = "zhangsan",
            ["actorIds"] = new List<object?> { "user1", "zhangsan" },
        });
        return facade;
    }

    private static async Task<int> RowsAsync(JeeflowFacade facade, string action, FlowData args)
    {
        var r = await facade.FlowAsync(action, args);
        Assert.Equal(0, Convert.ToInt32(r["code"]));
        var data = (Dictionary<string, object?>)r["data"]!;
        return ((List<object?>)data["rows"]!).Count;
    }

    // ═══ 第一层：门面归一化（空串/全空白 == 缺键 == 显式 user1）═══

    [Fact]
    public async Task EmptyOperatorFallsBackToDefaultLikeAbsentKey()
    {
        var facade = await SeedAsync();

        // 正向对照：user1 档非空（否则下面的三档相等是 0==0 自等假绿）
        Assert.Equal(1, await RowsAsync(facade, "processInstance/page", Of("user1")));
        Assert.Equal(1, await RowsAsync(facade, "processInstance/ccList", Of("user1")));
        Assert.True(await RowsAsync(facade, "processTask/doneList", Of("user1")) >= 1,
            "user1 办结的 apply 应进自己已办");
        Assert.True(await RowsAsync(facade, "processTask/todoList", Of("leader")) >= 1,
            "leader 手上应有待办（对照档，也是空串档的反面参照）");

        foreach (var action in Exits)
        {
            var empty = await RowsAsync(facade, action, Of(""));
            var blank = await RowsAsync(facade, action, Of("   "));
            var absent = await RowsAsync(facade, action, new FlowData());
            var asUser1 = await RowsAsync(facade, action, Of("user1"));
            Assert.Equal(absent, empty);      // 空串＝缺键同档
            Assert.Equal(asUser1, empty);     // 且都回落 demo 缺省 user1
            Assert.Equal(empty, blank);       // 全空白与空串同档
        }

        // 反向哨兵：待办在 leader 手里、user1 档 0 行。谁把"空值"实现成"不加条件"
        // （本 issue 的生产症状＝读全库），空串档就会读出 leader 那条 ⇒ 这两格挡的是假修。
        Assert.Equal(0, await RowsAsync(facade, "processTask/todoList", Of("")));
        Assert.True(await RowsAsync(facade, "processTask/todoList", Of("leader")) >= 1);
    }

    private static FlowData Of(string operatorValue) =>
        new() { ["operator"] = operatorValue };

    // ═══ 第二层 A：内存仓储 ApplyConditions 的归属列兜底（T0 走这条）═══

    [Fact]
    public async Task MemoryRepoBlankOwnershipYieldsEmptyPage()
    {
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "chain129", ChainFlow);
        var facade = new JeeflowFacade(ctx);
        await facade.FlowAsync("processInstance/startAndExecute",
            new FlowData { ["processDefineId"] = did, ["operator"] = "user1" });
        await facade.FlowAsync("processInstance/startAndExecute",
            new FlowData { ["processDefineId"] = did, ["operator"] = "zhangsan" });

        foreach (var blank in new[] { "", "   ", "\t" })
        {
            var inst = await repo.PageInstancesAsync(new PageQuery(1, 50).Add("t.operator", "EQ", blank));
            Assert.Equal(0, inst.RecordCount);
            var todo = await repo.PageTodoTasksAsync(new PageQuery(1, 50).Add("pta.actor_id", "EQ", blank));
            Assert.Equal(0, todo.RecordCount);
            var done = await repo.PageDoneTasksAsync(new PageQuery(1, 50).Add("t.operator", "EQ", blank));
            Assert.Equal(0, done.RecordCount);
            var cc = await repo.PageCcInstancesAsync(new PageQuery(1, 50).Add("cc.actor_id", "EQ", blank));
            Assert.Equal(0, cc.RecordCount);
        }

        // 正向对照：真值命中 ⇒ 上面那串 0 不是"查询恒空"
        var mine = await repo.PageInstancesAsync(new PageQuery(1, 50).Add("t.operator", "EQ", "user1"));
        Assert.Equal(1, mine.RecordCount);
        var theirs = await repo.PageInstancesAsync(new PageQuery(1, 50).Add("t.operator", "EQ", "zhangsan"));
        Assert.Equal(1, theirs.RecordCount);

        // 改动面哨兵：**非归属列**的空值仍走"当作没填"的通用放行（可选过滤不许改成空页）
        var optional = await repo.PageInstancesAsync(new PageQuery(1, 50)
            .Add("t.operator", "EQ", "user1").Add("t.business_no", "LIKE", ""));
        Assert.Equal(1, optional.RecordCount);
    }

    // ═══ 第二层 B：MySQL 仓储 BuildWhere 拼串层（只验 SQL 文本，不连库）═══

    [Fact]
    public void MySqlBuildWhereBlankOwnershipAppendsFalsePredicate()
    {
        var probe = new WhereProbe();
        var wl = new HashSet<string> { "t.operator", "t.business_no", "pd.name" };

        foreach (var blank in new object?[] { "", "   ", null })
        {
            var (sql, bind) = probe.Build(new PageQuery(1, 50).Add("t.operator", "EQ", blank), wl);
            Assert.Equal(" AND 1=0", sql);
            Assert.Empty(bind);
        }

        // 非归属列空值：整条不加（通用放行保持原样）
        Assert.Equal("", probe.Build(new PageQuery(1, 50).Add("t.business_no", "LIKE", ""), wl).Sql);
        // 归属列 + 非归属空值可选过滤：归属照常生效
        var (mixed, mixedBind) = probe.Build(new PageQuery(1, 50)
            .Add("t.operator", "EQ", "user1").Add("t.business_no", "LIKE", ""), wl);
        Assert.Equal(" AND t.operator = ?", mixed);
        Assert.Equal(new object?[] { "user1" }, mixedBind);
        // 归属列走非 EQ 操作符时不接管（本 issue 只收 EQ 归属谓词）
        Assert.Equal("", probe.Build(new PageQuery(1, 50).Add("t.operator", "LIKE", ""), wl).Sql);
    }

    // ═══ 第二层 B′（issues/152 ②）：委托分页那一腿的 SQL 文本层判据 ═══

    /// <summary>
    /// issues/152 ②：内存仓与 SQL 仓必须同答案（spec 06 §4.5 条款 6）——委托分页走的是同一支
    /// <see cref="MySqlRepository.BuildWhere"/>，但白名单是 <c>MySqlExtRepository</c> 自己那份：
    /// 归属列 <c>t.operator</c> 要是不在白名单里，门面注入的条件会被第一句"不在白名单，丢弃"吃掉，
    /// 光看门面那半永远照不出来。T0 不连库，判据打在拼串层（真库同形用例归 T1）。
    /// </summary>
    [Fact]
    public void S152_MySqlSurrogatePageBlankOwnershipAppendsFalsePredicate()
    {
        Assert.Contains("t.operator", MySqlExtRepository.SurrogateWhitelist);

        var probe = new WhereProbe();
        foreach (var blank in new object?[] { "", "   ", null })
        {
            var (sql, bind) = probe.Build(
                new PageQuery(1, 50).Add("t.operator", "EQ", blank), MySqlExtRepository.SurrogateWhitelist);
            Assert.Equal(" AND 1=0", sql);
            Assert.Empty(bind);
        }

        // 正向对照：真实归属值照常下推（证明上面那串 1=0 不是恒真）
        var (hitSql, hitBind) = probe.Build(
            new PageQuery(1, 50).Add("t.operator", "EQ", "op152"), MySqlExtRepository.SurrogateWhitelist);
        Assert.Equal(" AND t.operator = ?", hitSql);
        Assert.Equal(new object?[] { "op152" }, hitBind);

        // 改动面哨兵：委托表上的可选过滤（非归属列）空值仍走"当作没填"，不得被一起改成空页
        Assert.Equal("", probe.Build(new PageQuery(1, 50).Add("t.process_name", "LIKE", ""),
            MySqlExtRepository.SurrogateWhitelist).Sql);
    }

    /// <summary>只为把 protected 的 BuildWhere 暴露出来——不建连接、不发 SQL。</summary>
    private sealed class WhereProbe : MySqlRepository
    {
        public WhereProbe() : base(new MySqlConnectionFactory("127.0.0.1", 3306, "u", "p", "d")) { }

        public (string Sql, List<object?> Bind) Build(PageQuery query, HashSet<string> whitelist)
        {
            var sb = new StringBuilder();
            var bind = new List<object?>();
            BuildWhere(sb, bind, query, whitelist);
            return (sb.ToString(), bind);
        }
    }
}

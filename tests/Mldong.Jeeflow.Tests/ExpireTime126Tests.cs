using Xunit;
using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/126 案 A · 任务行 <c>expire_time</c> 由<b>建单路径</b>按节点到期表达式真算（基准＝boot2 内置版）。
///
/// <para>boot2 的三处写（<c>ProcessTaskServiceImpl</c> :213 普通建单 / :386 回退新建 / :524 会签建单）
/// 都是 <c>FlowUtil.processTime(node.getExpireTime(), args)</c>；本栈原形状是<b>只有回退那支真算</b>
/// （<c>ProcessInstance.RejectTask</c>），建单三支一处都不写 ⇒ "只有回退过的任务这一列有值"，
/// 常规流上逾期统计（<c>overdueTaskCount</c>）恒 0。跨栈判据在门禁 L2-27。</para>
///
/// <para>本栈写点共<b>五处</b>（issues/126 §1.8）：普通建单 / 串行会签首位 / 并行会签全员 / 回退新建 /
/// <b>串行会签推进出的下一位成员</b>（最后那处绕过聚合根建单 helper，直建于
/// <c>CountersignHandler.CreateNextCountersignTask</c>）。五处共用
/// <c>ProcessInstance.ApplyExpireTime</c> 一把尺子。</para>
///
/// <para>节点没配（null / 空串）⇒ 这一列保持 <b>NULL</b>，不造默认值（owner 2026-09-28 口径）。
/// 表达式解析不出 ⇒ 同样 NULL，<b>绝不退回 now()</b>——赋"建单那一刻"就是本案病灶的形状
/// （新建即逾期）。</para>
///
/// <para>反空转设计：正向格判据是<b>同一行</b>的 <c>expire − create</c> 落进 [2h−5s, 2h+60s]，
/// 不是"非空"——只判非空就会被 <c>now()</c> 占位蒙过；负向格把"行没读到"与"值为空"分开断，
/// 否则未配那档会拿 <c>{null, null}</c> 和"没读到行"混成一谈、恒真。</para>
/// </summary>
public class ExpireTime126Tests
{
    /// <summary>建单表达式为 "2h" 时的带宽：下界 2h−5s（秒级 floor 容差），上界 2h+60s。</summary>
    private const long MinDelta = 2 * 3600L - 5L;

    private const long MaxDelta = 2 * 3600L + 60L;

    private static ProcessInstance Instance() => new() { InstanceId = 9001 };

    private static TaskModel Node(string name, string? expireTime) =>
        new() { Name = name, DisplayName = name, ExpireTime = expireTime };

    private static List<string> One() => new() { "u1" };

    // ═══ T0 四格（域层：ProcessInstance 建单路径）═══

    /// <summary>
    /// 正向①：配 <c>"2h"</c> ⇒ 到期时间＝<b>建单那一刻</b> + 2 小时，用<b>同一行</b>的
    /// <c>create → expire</c> 差值判（不拿 now 当基准）。
    /// 注入固定钟是双判据：差值≈2h 证表达式真算了，差值基准落在 2026-08-01 那把钟上
    /// 证求值沿用的是<b>调用点注入的钟</b>而非裸 <c>DateTime.Now</c>（本栈是八栈里唯一域层带钟注入的，
    /// issues/120 的落点）。
    /// </summary>
    [Fact]
    public void RelativeExpressionAppliedAtCreationOnSameRow()
    {
        var clock = new FixedClock(new DateTime(2026, 8, 1, 9, 0, 0));
        var task = Instance().CreateTask(Node("approve", "2h"), "审批", One(), "op", 0, true, clock);

        Assert.NotNull(task.ExpireTime);
        Assert.NotNull(task.CreateTime);  // 对照：同一行有建单时刻，否则下面的差值判据是空判
        var delta = (long)(task.ExpireTime!.Value - task.CreateTime!.Value).TotalSeconds;
        Assert.True(delta >= MinDelta && delta <= MaxDelta,
            $"expire − create 应≈2h（实得 {delta}s）；赋 now() 占位会算出≈0 把新建任务判成已逾期，" +
            "改成裸 DateTime.Now 则会跑出几个量级");
        Assert.Equal(new DateTime(2026, 8, 1, 11, 0, 0), task.ExpireTime);
    }

    /// <summary>正向②：表达式是个<b>变量名</b> ⇒ 取该变量的值当到期时间（ProcessTime 第一档，
    /// 变量源＝实例变量，与 boot2 的 execution.getArgs() 同档）。</summary>
    [Fact]
    public void ExpressionNamingAVariableTakesItsValue()
    {
        var inst = Instance();
        inst.AddVariable(new FlowData { ["dueAt"] = "2026-12-31 10:00:00" });

        var task = inst.CreateTask(Node("approve", "dueAt"), "审批", One(), "op", 0, true,
            new FixedClock(new DateTime(2026, 8, 1, 9, 0, 0)));

        Assert.Equal(new DateTime(2026, 12, 31, 10, 0, 0), task.ExpireTime);
    }

    /// <summary>负向①：节点没配（null 与空串<b>两档</b>）⇒ 这一列必须留 NULL，不许造默认值（含不写 now()）。</summary>
    [Fact]
    public void UnconfiguredNodeKeepsColumnNull()
    {
        var task = Instance().CreateTask(Node("approve", null), "审批", One(), "op", 0, true);
        Assert.Null(task.ExpireTime);

        var blank = Instance().CreateTask(Node("approve", ""), "审批", One(), "op", 0, true);
        Assert.Null(blank.ExpireTime);
    }

    /// <summary>负向②：表达式解析不出来 ⇒ NULL，而不是退回成 now()
    /// （那等于静默造一个"建单即逾期"的值）。含 §1.9 条 3 的相对档前缀非整数档 <c>xh</c> ⇒ 落穿到绝对档后 NULL。</summary>
    [Fact]
    public void UnparsableExpressionStaysNullNotNow()
    {
        var clock = new FixedClock(new DateTime(2026, 8, 1, 9, 0, 0));
        Assert.Null(Instance().CreateTask(Node("approve", "not-a-time"), "审批", One(), "op", 0, true, clock)
            .ExpireTime);
        // 八栈一致（issues/137 C，java 6bdf41b 已对齐）：误配相对档前缀非整数 ⇒ 落穿到绝对档 ⇒ NULL，不抛错
        Assert.Null(Instance().CreateTask(Node("approve", "xh"), "审批", One(), "op", 0, true, clock)
            .ExpireTime);
    }

    // ═══ issues/137 D（相对档前缀须非负）═══

    /// <summary>
    /// 负向④（issues/137 D · owner 2026-10-01 拍"判非负"，基准＝java <c>FlowUtil.parseIntOrNull</c>
    /// 提交 <c>1649955</c>）：<c>s/m/h/d</c> 四档里前缀解析出的整数 <b>&lt; 0 一律算"解析不出来"</b>
    /// ⇒ 落穿绝对档 ⇒ 仍失败即 <b>NULL</b>。
    ///
    /// <para>为什么"照旧生效"不行：放行 <c>-5h</c> 算出的是一个<b>过去</b>的时刻 ⇒ 新建的行当场就是逾期，
    /// 比"没配到期时间"更难发现；而"退化成当前时间"正是 issues/126 的病灶形状（<c>expire == create</c>
    /// ＝建单即逾期）。两档都不许，唯一合法出口是空。</para>
    ///
    /// <para>本栈四档是同一段 <c>int.TryParse</c> 内联条件（<c>Handler/Handlers.cs</c> 的
    /// <c>FlowUtil.ProcessTime</c>，也是全栈到期档的唯一尺子——建单五处写点与实例级写点都汇到它），
    /// 所以<b>四档各钉一格</b>（<c>-30s / -5m / -5h / -5d</c>）：摘掉任何一档的 <c>&gt;= 0</c> 守卫，
    /// 本格都当场红。负数格用 <c>Assert.True(== null)</c> 带消息，红样里能直接看到那个过去时刻。</para>
    ///
    /// <para><b>只裁负、不裁加号</b>：<c>int.TryParse</c> 天然收 <c>-5</c> 也收 <c>+5</c>，判负只能加比较，
    /// 换成"正则禁符号"会把加号一起裁掉——python <c>[+-]?</c>、node <c>[-+]?\d+</c>、php
    /// <c>[+-]?\d{1,18}</c> 都收 <c>'+'</c>，那是新造一处跨栈分叉。加号档的正向对照格见下面
    /// <see cref="PlusSignAndOtherExpireTiersStillResolve"/>，它保证上面四判不是恒真。</para>
    ///
    /// <para><c>0h</c> 仍算合法偏移（基准侧 <c>parseIntOrNull</c> 也只判 <c>&lt; 0</c>），本栈不额外加码。</para>
    /// </summary>
    [Fact]
    public void NegativeRelativeExpressionStaysNullNotPastTime()
    {
        var clock = new FixedClock(new DateTime(2026, 8, 1, 9, 0, 0));
        var emptyArgs = new FlowData();

        // 尺子本身（ProcessTime 直调）：四档负前缀 ⇒ null，不落 now、不落回拨后的过去时刻
        foreach (var expr in new[] { "-30s", "-5m", "-5h", "-5d" })
            Assert.True(FlowUtil.ProcessTime(expr, emptyArgs, clock) == null,
                $"负数相对档 {expr} 必须算\"解析不出来\"⇒ NULL（实得 {FlowUtil.ProcessTime(expr, emptyArgs, clock)}）");

        // 建单路径（写点①，跨栈门禁 L2-27 的尺子：该行 expire_time 为空，不是"早于 create_time"）
        foreach (var expr in new[] { "-30s", "-5m", "-5h", "-5d" })
        {
            var task = Instance().CreateTask(Node("approve", expr), "审批", One(), "op", 0, true, clock);
            Assert.NotNull(task.CreateTime);   // 对照：这一行确实建过单，不是"没建行"造成的空
            Assert.True(task.ExpireTime == null,
                $"配 {expr} 的行 expire_time 必须留空；放行它会写进 {task.ExpireTime}" +
                "（＝当下 09:00 往前倒），新建即逾期，比\"没配\"更难发现");
        }
    }

    /// <summary>
    /// 正向对照（issues/137 D 的"有牙"那一半）：判负<b>只</b>裁掉负号，其余档位一律不动。
    ///
    /// <para>① 加号档四档全部照旧算得出且≈<c>now + 偏移</c>——这一格就是"没顺手裁加号"的证据；
    /// ② 裸数字档（<c>2h</c>/<c>2d</c>）不变；③ 变量档与绝对档不变（<c>"2026-12-31 10:00:00"</c>
    /// 照旧成功，绝对串末尾是数字不会被四档吃掉）；④ 坏前缀 <c>xh</c>/<c>2.5h</c> 行为不变
    /// （本来就走"落穿到绝对档 ⇒ NULL"，issues/137 C 那条）。</para>
    /// </summary>
    [Fact]
    public void PlusSignAndOtherExpireTiersStillResolve()
    {
        var clock = new FixedClock(new DateTime(2026, 8, 1, 9, 0, 0));

        // ① 加号档：四档都要照旧生效（摘掉守卫会多放行负数，加正则裁符号会把这四格一起打死）
        Assert.Equal(clock.Now.AddSeconds(30), FlowUtil.ProcessTime("+30s", new FlowData(), clock));
        Assert.Equal(clock.Now.AddMinutes(5), FlowUtil.ProcessTime("+5m", new FlowData(), clock));
        Assert.Equal(clock.Now.AddHours(2), FlowUtil.ProcessTime("+2h", new FlowData(), clock));
        Assert.Equal(clock.Now.AddDays(3), FlowUtil.ProcessTime("+3d", new FlowData(), clock));

        // 建单路径同样带值，且差值≈偏移（不是"非空"空判）
        var plusHours = Instance().CreateTask(Node("approve", "+2h"), "审批", One(), "op", 0, true, clock);
        Assert.NotNull(plusHours.ExpireTime);
        var delta = (long)(plusHours.ExpireTime!.Value - plusHours.CreateTime!.Value).TotalSeconds;
        Assert.True(delta >= MinDelta && delta <= MaxDelta,
            $"+2h 的 expire − create 应≈2h（实得 {delta}s）");

        // ② 裸数字档不变
        Assert.Equal(clock.Now.AddHours(2), FlowUtil.ProcessTime("2h", new FlowData(), clock));
        Assert.Equal(clock.Now.AddDays(2), FlowUtil.ProcessTime("2d", new FlowData(), clock));

        // ③ 变量档（第一档）与绝对档（第三档）不变
        var inst = Instance();
        inst.AddVariable(new FlowData { ["dueAt"] = "2026-12-31 10:00:00" });
        Assert.Equal(new DateTime(2026, 12, 31, 10, 0, 0),
            inst.CreateTask(Node("approve", "dueAt"), "审批", One(), "op", 0, true, clock).ExpireTime);
        Assert.Equal(new DateTime(2026, 12, 31, 10, 0, 0),
            FlowUtil.ProcessTime("2026-12-31 10:00:00", new FlowData(), clock));

        // ④ 坏前缀行为不变（issues/137 C：落穿到绝对档 ⇒ NULL，不抛错）
        Assert.Null(FlowUtil.ProcessTime("xh", new FlowData(), clock));
        Assert.Null(FlowUtil.ProcessTime("2.5h", new FlowData(), clock));
    }

    // ═══ §1.8 两格（引擎层：串行会签首成员 + 推进出的第二成员）═══

    /// <summary>夹具：串行会签节点 task1 只加 <c>expireTime:"2h"</c>，其余与原 06 夹具逐字同。</summary>
    private const string ExpireFlow = "06-countersign-sequential-expire";

    private const string PlainFlow = "06-countersign-sequential";

    /// <summary>
    /// 正向③＋写点⑤：串行会签<b>首成员</b>（<c>CreateCountersignTasks</c> SEQUENTIAL 支）与
    /// <b>推进出的第二成员</b>（<c>CountersignHandler.CreateNextCountersignTask</c>，绕过建单 helper
    /// 的第五条路径）<b>都</b>带≈2h 的到期时间。
    /// 基准侧 boot2 的串行推进是回调 <c>createCountersignTask</c>（ProcessTaskServiceImpl:485，
    /// 内含 :524 那处到期写）⇒ 基准形状里"推进新建的那一位"同样带到期时间；
    /// 只补首成员就是"首成员有、第二三位没有"，这一格正是摘掉第五处写点时会红的那格。
    /// </summary>
    [Fact]
    public async Task SerialCountersignFirstAndAdvancedMemberBothCarryExpire()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "cs-seq-126", TestInfra.LoadFlow(ExpireFlow));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);

        var first = await DoingRowAsync(repo, iid, "userA");
        Assert.NotNull(first);   // 先断"行读到了"
        AssertAbout2h(first!, "首成员（CreateCountersignTasks 串行支）");

        await engine.ExecuteProcessTaskAsync(first!.TaskId!.Value, "userA",
            new FlowData { [FlowConst.SubmitType] = (int)WfSubmitType.Agree });

        var second = await DoingRowAsync(repo, iid, "userB");
        Assert.NotNull(second);
        AssertAbout2h(second!, "推进新建的第二成员（第五处写点）");
    }

    /// <summary>
    /// 负向③：同一条会签节点的"未配"档（换回不带 expireTime 的原夹具）⇒ 首成员与推进出的第二成员
    /// <b>两行都留空</b>。判据把"行没读到"与"值为空"分开断：先断两行都读到、createTime 有值，
    /// 再断 ExpireTime 为空——否则这条恒真（拿"没读到行"当成"值为空"）。
    /// </summary>
    [Fact]
    public async Task SerialCountersignUnconfiguredKeepsBothRowsBlank()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "cs-seq-126-plain", TestInfra.LoadFlow(PlainFlow));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);

        var first = await DoingRowAsync(repo, iid, "userA");
        Assert.NotNull(first);                    // 行没读到 ⇒ 本格判失败，不许算过
        Assert.NotNull(first!.CreateTime);         // 对照：该行确实建过单
        Assert.Null(first.ExpireTime);             // 值为空

        await engine.ExecuteProcessTaskAsync(first.TaskId!.Value, "userA",
            new FlowData { [FlowConst.SubmitType] = (int)WfSubmitType.Agree });

        var second = await DoingRowAsync(repo, iid, "userB");
        Assert.NotNull(second);
        Assert.NotNull(second!.CreateTime);
        Assert.Null(second.ExpireTime);
    }

    /// <summary>取该实例里 actor 为某用户的那条 DOING 行本身（找不到返回 null——"行不在"与"值为空"
    /// 必须能分开，否则未配那档会退化成恒真）。</summary>
    private static async Task<ProcessTask?> DoingRowAsync(MemoryRepository repo, long instanceId, string actor)
    {
        var rows = await repo.FindDoingTasksAsync(instanceId, null);
        foreach (var t in rows)
            if (t.ActorIds.Contains(actor))
                return t;
        return null;
    }

    /// <summary>同一行 expire − create 落进 [2h−5s, 2h+60s]；非空只当"行/列齐不齐"的前置断言。</summary>
    private static void AssertAbout2h(ProcessTask row, string who)
    {
        Assert.NotNull(row.CreateTime);
        Assert.NotNull(row.ExpireTime);
        var delta = (long)(row.ExpireTime!.Value - row.CreateTime!.Value).TotalSeconds;
        Assert.True(delta >= MinDelta && delta <= MaxDelta,
            $"{who} 的 expire − create 应≈2h（实得 {delta}s）");
    }
}

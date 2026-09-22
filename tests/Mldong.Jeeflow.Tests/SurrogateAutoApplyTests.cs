using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/116 批次 D——「委托代理自动生效」引擎内置化的行为用例。
///
/// <para>契约依据：<c>jeeflow-doc docs/spec/06-facade.md</c> §4.5「运行期语义」条款 1~6 +
/// <c>05-spi.md</c>「SurrogateInterceptor」+ <c>08-compliance.md</c> 用例 26/27。</para>
///
/// <para>断言一律落在<b>持久值/读回值</b>（<c>MemoryRepository</c> 的 actor 行表等价
/// <c>wf_process_task_actor</c>，经 <c>FindTaskActorsAsync</c> / <c>FindTaskByIdAsync</c> 读回），
/// 不看内存集合；SQL 仓同判据见 <c>MySqlBehaviorSuite.T1_Surrogate*</c>（双仓一致，条款 6）。</para>
///
/// <para>三档：正向 S01~S04 / 负向 S05~S09（窗外、enabled=0、自委托、脏值、名不符）/
/// 回归 S10~S21（不级联、多条取 max id、未配扩展仓储不打断建单、仓储报错不打断建单、
/// 两条关闭路径后仅台账、加签仍只追加、串行会签票数不变、四条建任务路径全覆盖、幂等）。</para>
/// </summary>
public class SurrogateAutoApplyTests
{
    private static readonly DateTime Now = new(2026, 8, 1, 9, 0, 0);

    // ═══ 基建 ═══

    /// <summary>可观测扩展仓储：记录逐参与者查询轨迹（"查了几次 / 查了谁 / 带什么 processName"），
    /// 并可切换为"查询即报错"以验证条款 4「仓储报错也只记日志、不打断建单」。</summary>
    private class InstrumentedExtRepository : MemoryExtRepository
    {
        public List<(string? Op, string? ProcessName, DateTime Time)> Queries { get; } = new();
        public bool ThrowOnQuery { get; set; }

        public InstrumentedExtRepository(MemoryRepository repo, ServiceContext? ctx) : base(repo, ctx) { }

        public override Task<ProcessSurrogate?> GetSurrogateAsync(string? op, string? processName, DateTime time)
        {
            Queries.Add((op, processName, time));
            if (ThrowOnQuery) throw new InvalidOperationException("wf_process_surrogate 表不存在（模拟）");
            return base.GetSurrogateAsync(op, processName, time);
        }
    }

    private sealed record Harness(JeeflowEngine Engine, MemoryRepository Repo,
        InstrumentedExtRepository? Ext, ServiceContext Ctx);

    private static Harness NewHarness(bool withExt = true)
    {
        var repo = new MemoryRepository();
        InstrumentedExtRepository? ext = withExt ? new InstrumentedExtRepository(repo, null) : null;
        var ctx = new ServiceContext(repo, ext);
        ctx.Clock = new FixedClock(Now);
        ctx.IdGenerator = new AtomicIdGenerator(1, ctx.Clock);
        ctx.UserProvider = new TestUserProvider();
        TestInfra.RegisterBuiltins(ctx);
        repo.Configure(ctx);
        ext?.Configure(ctx);
        return new Harness(new JeeflowEngine(ctx), repo, ext, ctx);
    }

    /// <summary>台账：授权人 op → 代理人 agent（processName 空 = 全部流程兜底；时间窗 null = 该侧不限）。
    /// <paramref name="id"/> 显式给主键——打乱「插入序 vs id 序」用（条款 1.4 的夹具要求，见 S116_11）。</summary>
    private static async Task<long> LedgerAsync(Harness h, string op, string agent,
        string? processName = "simple", int enabled = 1,
        DateTime? start = null, DateTime? end = null, long? id = null)
    {
        var s = new ProcessSurrogate
        {
            Id = id,
            ProcessName = processName,
            Operator = op,
            Surrogate = agent,
            StartTime = start,
            EndTime = end,
            Enabled = enabled,
            CreateUser = "tester",
        };
        await h.Ext!.SaveSurrogateAsync(s);
        return s.Id!.Value;
    }

    /// <summary>发起指定 flows 并办结申请节点（推进到第一个审批节点的新任务）——"办理推进"建单路径。</summary>
    private static async Task<(long InstanceId, long TaskId)> AdvancePastApplyAsync(
        Harness h, long defineId, string applyActor = "applicant")
    {
        var inst = await h.Engine.StartProcessInstanceByIdAsync(defineId, applyActor, new FlowData());
        var apply = await TestInfra.FindDoingForAsync(h.Repo, inst.InstanceId!.Value, applyActor);
        await h.Engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, applyActor, new FlowData());
        var doing = await h.Repo.FindDoingTasksAsync(inst.InstanceId.Value, null);
        Assert.NotEmpty(doing);
        return (inst.InstanceId.Value, doing[0].TaskId!.Value);
    }

    /// <summary>01-simple（apply=applicant → task1=leader → end）推进到 task1。</summary>
    private static async Task<(long InstanceId, long TaskId)> StartToLeaderTaskAsync(Harness h)
    {
        var defineId = await TestInfra.SaveFlowDefineAsync(h.Repo, "simple", TestInfra.LoadFlow("01-simple"));
        var (iid, taskId) = await AdvancePastApplyAsync(h, defineId);
        Assert.Equal("task1", (await h.Repo.FindTaskByIdAsync(taskId))!.TaskName);
        return (iid, taskId);
    }

    /// <summary>落库后读回的参与者（= wf_process_task_actor 真行，非内存集合；两条读路径同答案）。</summary>
    private static async Task<List<string>> PersistedActorsAsync(Harness h, long taskId)
    {
        var viaActorTable = await h.Repo.FindTaskActorsAsync(taskId);
        var viaTask = await h.Repo.FindTaskByIdAsync(taskId);
        Assert.NotNull(viaTask);
        Assert.Equal(viaActorTable, viaTask!.ActorIds);
        return viaActorTable;
    }

    // ═══ 正向 ═══

    [Fact]
    public async Task S116_01_AgentMergedIntoPersistedActorRows_PrincipalKept()
    {
        // 条款 1/2：建单那一刻并入参与者集合 → 随任务落 wf_process_task_actor；授权人保留（任一可办）
        var h = NewHarness();
        await LedgerAsync(h, "leader", "deputy");
        var (_, taskId) = await StartToLeaderTaskAsync(h);

        Assert.Equal(new List<string> { "leader", "deputy" }, await PersistedActorsAsync(h, taskId));
        var task = (await h.Repo.FindTaskByIdAsync(taskId))!;
        Assert.True(task.IsAllowed("leader"), "授权人必须保留可办（委托不是转办）");
        Assert.True(task.IsAllowed("deputy"), "代理人必须可办，否则等于没代理出去");
        // 每条建单路径都逐参与者查一次（apply 节点 applicant + task1 节点 leader），
        // 带当前钟（可测性：引擎用 ServiceContext.Clock，不是 DateTime.Now）
        Assert.Equal(new[] { "applicant", "leader" }, h.Ext!.Queries.Select(q => q.Op).ToArray());
        Assert.All(h.Ext.Queries, q =>
        {
            Assert.Equal("simple", q.ProcessName);
            Assert.Equal(Now, q.Time);
        });
    }

    [Fact]
    public async Task S116_02_AgentActuallyExecutes_ActorsSurviveFullOverwriteWriteBack()
    {
        // C# 特有陷阱：SaveTask / UpdateTask / UpdateInstance 级联都按聚合副本【全量覆写】参与者行。
        // 代理人若只补写在副本之外，回写一次就丢。这里让代理人真的办结，再读回该行钉住不丢。
        var h = NewHarness();
        await LedgerAsync(h, "leader", "deputy");
        var (iid, taskId) = await StartToLeaderTaskAsync(h);

        await h.Engine.ExecuteProcessTaskAsync(taskId, "deputy", new FlowData()); // 代理人可办 = 落库真生效

        Assert.Equal((int)WfInstanceState.Finished, (await h.Repo.FindInstanceByIdAsync(iid))!.State);
        Assert.Equal(new List<string> { "leader", "deputy" }, await PersistedActorsAsync(h, taskId));
        Assert.Equal("deputy", (await h.Repo.FindTaskByIdAsync(taskId))!.ActorId);
    }

    [Fact]
    public async Task S116_03_ProcessNameIsDefineName_AfterDeploy()
    {
        // 条款 1.1：processName 认 wf_process_define.name（deploy 有 def.Name = model.Name 不变量），
        // 不得各自再从 content JSON 解析一遍——经门面 deploy 建定义，台账用「库内这一列」命中。
        var h = NewHarness();
        var facade = new JeeflowFacade(h.Ctx);
        var deployed = await facade.FlowAsync("processDefine/deploy",
            new FlowData { ["content"] = TestInfra.LoadFlow("01-simple") });
        Assert.Equal(0, deployed["code"]);
        var defineId = long.Parse(
            ((Dictionary<string, object?>)deployed["data"]!)["processDefineId"]!.ToString()!);
        var define = await h.Repo.FindDefineByIdAsync(defineId);
        Assert.NotNull(define);
        Assert.Equal("simple", define!.Name); // 与 content 顶层 name 同值（deploy 不变量）

        await LedgerAsync(h, "leader", "deputy", processName: define.Name);
        var (_, taskId) = await AdvancePastApplyAsync(h, defineId);

        Assert.Equal(new List<string> { "leader", "deputy" }, await PersistedActorsAsync(h, taskId));
        Assert.All(h.Ext!.Queries, q => Assert.Equal(define.Name, q.ProcessName));
    }

    [Fact]
    public async Task S116_04_BlankProcessName_IsGlobalFallback()
    {
        // 判据①：空 processName = 全部流程兜底（"" 与 null 两态同判）
        var h = NewHarness();
        await LedgerAsync(h, "leader", "deputy", processName: "");
        var (_, t1) = await StartToLeaderTaskAsync(h);
        Assert.Equal(new List<string> { "leader", "deputy" }, await PersistedActorsAsync(h, t1));

        var h2 = NewHarness();
        await LedgerAsync(h2, "leader", "deputy2", processName: null);
        var (_, t2) = await StartToLeaderTaskAsync(h2);
        Assert.Equal(new List<string> { "leader", "deputy2" }, await PersistedActorsAsync(h2, t2));
    }

    // ═══ 负向 ═══

    [Fact]
    public async Task S116_05_OutOfTimeWindow_NotApplied_BoundaryIsInclusive()
    {
        // 判据②：start_time <= now <= end_time（闭区间）；窗外不生效；NULL = 该侧不限
        Assert.Equal(new List<string> { "leader" },
            await ActorsWithWindowAsync(end: Now.AddSeconds(-1)));      // 已过期
        Assert.Equal(new List<string> { "leader" },
            await ActorsWithWindowAsync(start: Now.AddSeconds(1)));     // 未到期
        Assert.Equal(new List<string> { "leader", "deputy" },
            await ActorsWithWindowAsync(start: Now, end: Now));         // 边界含等号
        Assert.Equal(new List<string> { "leader", "deputy" },
            await ActorsWithWindowAsync(start: Now.AddMinutes(-1), end: null)); // 单侧不限
    }

    private static async Task<List<string>> ActorsWithWindowAsync(
        DateTime? start = null, DateTime? end = null)
    {
        var h = NewHarness();
        await LedgerAsync(h, "leader", "deputy", start: start, end: end);
        var (_, taskId) = await StartToLeaderTaskAsync(h);
        return await PersistedActorsAsync(h, taskId);
    }

    [Fact]
    public async Task S116_06_DisabledLedger_NotApplied()
    {
        // 判据④：enabled=0 停用——台账照存，建单不应用
        var h = NewHarness();
        var id = await LedgerAsync(h, "leader", "deputy", enabled: 0);
        var (_, taskId) = await StartToLeaderTaskAsync(h);
        Assert.Equal(new List<string> { "leader" }, await PersistedActorsAsync(h, taskId));
        var stored = await h.Ext!.FindSurrogateByIdAsync(id);
        Assert.Equal(0, stored!.Enabled);   // 持久值仍是 0（不被读侧改写）
    }

    [Fact]
    public async Task S116_07_SelfSurrogate_NotApplied()
    {
        // 判据③：surrogate <> operator（自己委托给自己不生效）
        var h = NewHarness();
        var id = await LedgerAsync(h, "leader", "leader");
        var (_, taskId) = await StartToLeaderTaskAsync(h);
        Assert.Equal(new List<string> { "leader" }, await PersistedActorsAsync(h, taskId));
        Assert.DoesNotContain("deputy", await PersistedActorsAsync(h, taskId)); // 没产生"自己代理自己"的追加
        Assert.Contains("leader", h.Ext!.Queries.Select(q => q.Op));            // 查了，但仓储按判据③过滤
        Assert.Null(await h.Ext.GetSurrogateAsync("leader", "simple", Now));
        Assert.NotNull(await h.Ext.FindSurrogateByIdAsync(id)); // 台账行本身还在
    }

    [Fact]
    public async Task S116_08_OtherProcessName_NotApplied()
    {
        // 判据①：精确名未命中且无兜底行 → 不生效
        var h = NewHarness();
        await LedgerAsync(h, "leader", "deputy", processName: "other-flow");
        var (_, taskId) = await StartToLeaderTaskAsync(h);
        Assert.Equal(new List<string> { "leader" }, await PersistedActorsAsync(h, taskId));
    }

    [Fact]
    public async Task S116_09_DirtyEnabledValue_PersistedAsZero_AndNotApplied()
    {
        // C# 本轮专项：脏值曾回落 1（=启用），与 PHP/Java 方向相反。
        // 判据④ + 05-spi：只有 1 生效、脏值不得当启用 → 落库 0（读回值），自动生效亦不发生；
        // 缺键仍是契约默认 1（06 §4.5 save 参数表「enabled 默认 1」），台账语义不变。
        Assert.Equal(0, await SaveEnabledAndReadBackAsync("abc"));
        // 空串必须显式钉住：它**不是**缺键，落成"缺键→1"就等于把脏值当启用（本栈曾经的相反方向）。
        // int.TryParse("") 为 false → 走脏值支落 0；一旦有人把判据写成 string.IsNullOrEmpty 即红。
        Assert.Equal(0, await SaveEnabledAndReadBackAsync(""));
        foreach (var dirty in new object?[] { "1x", "1abc", "true", " " })
            Assert.Equal(0, await SaveEnabledAndReadBackAsync(dirty));
        Assert.Equal(1, await SaveEnabledAndReadBackAsync(null));   // 缺键 = 契约默认
        Assert.Equal(1, await SaveEnabledAndReadBackAsync("1"));    // 字符串 "1" 同整数 1
        Assert.Equal(0, await SaveEnabledAndReadBackAsync(0));      // 显式 0 不得被 or 1 吞掉
        // 契约 06 §4.5 条款 5 写侧末句：布尔入参按 true→1 / false→0
        // （ToInt 走 ToString() 得到 "True" → TryParse 失败会落成 0，与契约相反，须显式分支）
        Assert.Equal(1, await SaveEnabledAndReadBackAsync(true));
        Assert.Equal(0, await SaveEnabledAndReadBackAsync(false));
        // 任何脏值都不得把门面打成异常/500（Node 首版 toInt 直接抛的坑）——上面每条已断言 code=0

        // 行为侧：脏值台账不产生任何代理
        var h = NewHarness();
        var facade = new JeeflowFacade(h.Ctx);
        await facade.FlowAsync("processSurrogate/save", new FlowData
        {
            ["operator"] = "leader", ["surrogate"] = "deputy",
            ["processName"] = "simple", ["enabled"] = "abc",
        });
        var (_, taskId) = await StartToLeaderTaskAsync(h);
        Assert.Equal(new List<string> { "leader" }, await PersistedActorsAsync(h, taskId));
    }

    /// <summary>经门面 save 写一条 enabled=&lt;value&gt; 的台账，再 detail 读回其持久值。</summary>
    private static async Task<int> SaveEnabledAndReadBackAsync(object? enabled)
    {
        var h = NewHarness();
        var facade = new JeeflowFacade(h.Ctx);
        var args = new FlowData
        {
            ["operator"] = "leader", ["surrogate"] = "deputy", ["processName"] = "simple",
        };
        if (enabled != null) args["enabled"] = enabled;
        var save = await facade.FlowAsync("processSurrogate/save", args);
        Assert.Equal(0, save["code"]);
        var id = ((Dictionary<string, object?>)save["data"]!)["id"];
        var detail = await facade.FlowAsync("processSurrogate/detail", new FlowData { ["id"] = id });
        var row = (Dictionary<string, object?>)detail["data"]!;
        return Convert.ToInt32(row["enabled"]);
    }

    // ═══ 回归 ═══

    [Fact]
    public async Task S116_10_NoCascade_AgentsOwnSurrogateIsNotExpanded()
    {
        // 条款 1.2：只查建单那一刻的【原始参与者快照】，代理人自身的委托不展开（A→B 且 B→C，C 收不到）
        var h = NewHarness();
        await LedgerAsync(h, "leader", "deputy");
        await LedgerAsync(h, "deputy", "superdeputy");
        var (_, taskId) = await StartToLeaderTaskAsync(h);

        var actors = await PersistedActorsAsync(h, taskId);
        Assert.Equal(new List<string> { "leader", "deputy" }, actors);
        Assert.DoesNotContain("superdeputy", actors);
        // 快照遍历的直接证据：task1 的参与者只有 leader 被查询过，
        // 本轮追加进来的代理人 deputy 根本没被查（否则 B→C 会继续展开成环）
        Assert.DoesNotContain("deputy", h.Ext!.Queries.Select(q => q.Op));
        Assert.Equal(1, h.Ext.Queries.Count(q => q.Op == "leader"));
    }

    [Fact]
    public async Task S116_11_MultipleHits_TakesMaxId_MemoryRepo()
    {
        // 条款 1.4：多条同时命中取主键 id 最大那条。
        // **插入序刻意与 id 序错开**（900 → 1000 → 950，最大那条卡在中间）：插入序与 id 序重合时，
        // "取遍历末条"与"取 id 最大"给同一个答案，用例钉不住（Node 上轮实测发现的空转）。
        // 这样排后三种错实现各红一头：取首条 → deputyLow、取末条 → deputyMid、取 max id → deputyHigh。
        var h = NewHarness();
        await LedgerAsync(h, "leader", "deputyLow", id: 900);    // 插入序第 1（id 最小）
        await LedgerAsync(h, "leader", "deputyHigh", id: 1000);  // 插入序第 2，id 最大 = 唯一正解
        await LedgerAsync(h, "leader", "deputyMid", id: 950);    // 插入序末条（"取末条"的诱饵）

        var hit = await h.Ext!.GetSurrogateAsync("leader", "simple", Now);
        Assert.Equal("deputyHigh", hit!.Surrogate);
        // 命中的必须是 id 最大那条，而不是插入序的首条(900)或末条(950)
        Assert.Equal(1000L, hit.Id);

        var (_, taskId) = await StartToLeaderTaskAsync(h);
        Assert.Equal(new List<string> { "leader", "deputyHigh" }, await PersistedActorsAsync(h, taskId));
    }

    [Fact]
    public async Task S116_12_NoExtRepository_BuildTaskUnbroken()
    {
        // 条款 4：未配置扩展仓储 → 静默跳过，不打断建单（缺仓储属正常部署形态）
        var h = NewHarness(withExt: false);
        Assert.Null(h.Ctx.ExtRepository);
        var (iid, taskId) = await StartToLeaderTaskAsync(h);
        Assert.Equal(new List<string> { "leader" }, await PersistedActorsAsync(h, taskId));
        Assert.Single(await h.Repo.FindDoingTasksAsync(iid, null));
    }

    [Fact]
    public async Task S116_13_ExtRepositoryThrows_BuildTaskUnbroken_OnlyLogs()
    {
        // 条款 4：仓储自身报错（表未建等）只记日志，参与者集合保持原样、建单继续
        var h = NewHarness();
        await LedgerAsync(h, "leader", "deputy");
        h.Ext!.ThrowOnQuery = true;
        var (iid, taskId) = await StartToLeaderTaskAsync(h);
        Assert.Equal(new List<string> { "leader" }, await PersistedActorsAsync(h, taskId));
        Assert.Single(await h.Repo.FindDoingTasksAsync(iid, null));
        Assert.NotEmpty(h.Ext.Queries);   // 确实查过并抛了
    }

    [Fact]
    public async Task S116_14_OffByOptionsFlag_LedgerOnly()
    {
        // 条款 3 关闭 API 主路径（.NET options 开关）：ctx.SurrogateAutoApply = false
        var h = NewHarness();
        await LedgerAsync(h, "leader", "deputy");
        Assert.True(h.Ctx.SurrogateAutoApply);      // 默认开启（契约条款 3）
        h.Ctx.SurrogateAutoApply = false;

        var (_, taskId) = await StartToLeaderTaskAsync(h);
        Assert.Equal(new List<string> { "leader" }, await PersistedActorsAsync(h, taskId));
        Assert.Empty(h.Ext!.Queries);               // 关闭后连查都不查
    }

    [Fact]
    public async Task S116_15_OffByNullObjectApplier_LedgerOnly()
    {
        // 条款 3 关闭 API 第二路径：注册空实现（DI 可替换 ISurrogateApplier）
        var h = NewHarness();
        await LedgerAsync(h, "leader", "deputy");
        h.Ctx.SurrogateApplier = NullSurrogateApplier.Instance;

        var (_, taskId) = await StartToLeaderTaskAsync(h);
        Assert.Equal(new List<string> { "leader" }, await PersistedActorsAsync(h, taskId));
        Assert.Empty(h.Ext!.Queries);
        Assert.Same(NullSurrogateApplier.Instance, h.Ctx.SurrogateApplierOrDefault);
    }

    [Fact]
    public async Task S116_16_LedgerStillWorksWhenAutoApplyOff()
    {
        // 关闭后回到「仅台账」：processSurrogate/* 五 action 照存照查
        var h = NewHarness();
        h.Ctx.SurrogateAutoApply = false;
        var facade = new JeeflowFacade(h.Ctx);
        var save = await facade.FlowAsync("processSurrogate/save", new FlowData
        {
            ["operator"] = "leader", ["surrogate"] = "deputy", ["processName"] = "simple",
        });
        Assert.Equal(0, save["code"]);
        var page = await facade.FlowAsync("processSurrogate/page", new FlowData { ["pageSize"] = 10 });
        var rows = (List<object?>)((Dictionary<string, object?>)page["data"]!)["rows"]!;
        Assert.Single(rows);
        var row = (Dictionary<string, object?>)rows[0]!;
        Assert.Equal("deputy", row["surrogate"]);
        Assert.Equal("leader", row["operator"]);
    }

    [Fact]
    public async Task S116_17_CountersignAddStillAppendOnly_AfterAutoApply()
    {
        // 回归：自动生效并入 + processTask/surrogate 加签都是"只追加、不摘原人"，且去重
        var h = NewHarness();
        await LedgerAsync(h, "leader", "deputy");
        var facade = new JeeflowFacade(h.Ctx);
        var (_, taskId) = await StartToLeaderTaskAsync(h);

        Assert.Equal(0, (await facade.FlowAsync("processTask/surrogate", new FlowData
        {
            ["processTaskId"] = taskId.ToString(),
            ["actorIds"] = new List<object?> { "helper1", "deputy" }, // deputy 已在，须去重
        }))["code"]);

        Assert.Equal(new List<string> { "leader", "deputy", "helper1" },
            await PersistedActorsAsync(h, taskId));
    }

    [Fact]
    public async Task S116_18_SequentialCountersign_AgentOnlyInCurrentStep_VoteRosterUnchanged()
    {
        // 条款 1.3：串行会签代理人只进【当一步任务】的参与者，不扩 operatorList 投票名册（否则改票数）
        var h = NewHarness();
        await LedgerAsync(h, "userA", "agentA", processName: "countersign-sequential");
        await LedgerAsync(h, "userB", "agentB", processName: "countersign-sequential");
        var defineId = await TestInfra.SaveFlowDefineAsync(h.Repo, "countersign-sequential",
            TestInfra.LoadFlow("06-countersign-sequential"));
        var (iid, step1Id) = await AdvancePastApplyAsync(h, defineId);

        var step1 = (await h.Repo.FindTaskByIdAsync(step1Id))!;
        Assert.Equal("task1", step1.TaskName);
        Assert.Equal(new List<string> { "userA", "agentA" }, await PersistedActorsAsync(h, step1Id));
        AssertRosterUnchanged(step1, 0);

        // 第 1 位办结 → 推进建第 2 步任务（"串行会签每一步推进"的建单路径）
        await h.Engine.ExecuteProcessTaskAsync(step1Id, "userA", new FlowData());
        var step2 = (await h.Repo.FindDoingTasksAsync(iid, null))[0];
        Assert.NotEqual(step1Id, step2.TaskId);
        Assert.Equal(new List<string> { "userB", "agentB" },
            await PersistedActorsAsync(h, step2.TaskId!.Value));
        AssertRosterUnchanged(step2, 1);   // 名册仍是 userA,userB 两票；agentA/agentB 未混入

        await h.Engine.ExecuteProcessTaskAsync(step2.TaskId.Value, "userB", new FlowData());
        Assert.Equal((int)WfInstanceState.Finished, (await h.Repo.FindInstanceByIdAsync(iid))!.State);
    }

    /// <summary>串行会签名册不变量：operatorList_{node} 恒为解析时的原始成员、nrOfInstances_{node} 恒 2。</summary>
    private static void AssertRosterUnchanged(ProcessTask step, int expectedLoopCounter)
    {
        var node = step.TaskName;
        var roster = AsStringList(step.Variables.GetObj($"{FlowConst.CountersignOperatorList}_{node}"));
        Assert.Equal(new[] { "userA", "userB" }, roster.ToArray());
        Assert.Equal(2, step.Variables.GetInt($"{FlowConst.NrOfInstances}_{node}"));
        Assert.Equal(expectedLoopCounter, step.Variables.GetInt($"{FlowConst.LoopCounter}_{node}"));
    }

    private static List<string> AsStringList(object? value)
    {
        var result = new List<string>();
        if (value is System.Collections.ICollection coll)
            foreach (var o in coll) result.Add(o?.ToString() ?? "");
        else if (value != null) result.Add(value.ToString() ?? "");
        return result;
    }

    [Fact]
    public async Task S116_19_RollbackPath_AlsoAppliesSurrogate()
    {
        // 条款 1：覆盖全部建任务路径——跳转/回退（ROLLBACK）建的新单同样要应用委托。
        // issues/121 P2 血缘版：回退复活的是 apply 那条历史行，且它是首任务节点行 ⇒
        // 参与者取该行 u_userId（发起人 applicant），不是执行回退的 leader。
        // 台账延后到 task1 建单之后再配，保住"起点没有代理人"的自证力。
        var h = NewHarness();
        var (iid, taskId) = await StartToLeaderTaskAsync(h);
        await LedgerAsync(h, "applicant", "deputy");

        await h.Engine.ExecuteAndJumpTaskAsync(taskId, "leader", new FlowData(), null);
        var back = (await h.Repo.FindDoingTasksAsync(iid, null))[0];
        Assert.Equal("apply", back.TaskName);
        Assert.Equal(new List<string> { "applicant", "deputy" },
            await PersistedActorsAsync(h, back.TaskId!.Value));
    }

    [Fact]
    public async Task S116_20_JumpToNamedNode_AlsoAppliesSurrogate()
    {
        // 条款 1：跳转指定节点（submitType=4 JUMP 走的 ExecuteAndJumpTaskAsync(nodeName) 建单路径）
        // 02-multi-task：apply(applicant) → task1(leader) → task2(manager) → task3(boss)
        var h = NewHarness();
        await LedgerAsync(h, "boss", "deputyBoss", processName: "multi-task");
        var defineId = await TestInfra.SaveFlowDefineAsync(h.Repo, "multi-task",
            TestInfra.LoadFlow("02-multi-task"));
        var (iid, task1Id) = await AdvancePastApplyAsync(h, defineId);
        Assert.Equal("task1", (await h.Repo.FindTaskByIdAsync(task1Id))!.TaskName);

        await h.Engine.ExecuteAndJumpTaskAsync(task1Id, "leader", new FlowData(), "task3");
        var jumped = (await h.Repo.FindDoingTasksAsync(iid, null)).First(t => t.TaskName == "task3");
        Assert.Equal(new List<string> { "boss", "deputyBoss" },
            await PersistedActorsAsync(h, jumped.TaskId!.Value));
    }

    [Fact]
    public async Task S116_21_ApplierIsIdempotent_AndWorksBeforeTaskIdAssigned()
    {
        // 条款 2 ⚠️ 的机理证明：taskId 尚未分配时"事后 AddTaskActorAsync 补写"会打在空 id 上静默无效；
        // 并入集合的做法在 taskId=null 时同样有效，且重复调用不重复追加（幂等）。
        var h = NewHarness();
        await LedgerAsync(h, "leader", "deputy");
        var applier = new ExtRepositorySurrogateApplier(h.Ctx);
        var task = ProcessTask.Create(1L, "task1", "上级审批", WfTaskType.Major,
            WfPerformType.Normal, null, new List<string> { "leader" }, "applicant", null, false, h.Ctx.Clock);
        Assert.Null(task.TaskId);                        // 建单那一刻 taskId 还没有

        await applier.ApplyAsync(task, "simple", Now);
        await applier.ApplyAsync(task, "simple", Now);   // 幂等
        Assert.Equal(new List<string> { "leader", "deputy" }, task.ActorIds);

        await h.Repo.SaveTaskAsync(task);                // 落库才分配 taskId
        Assert.NotNull(task.TaskId);
        Assert.Equal(new List<string> { "leader", "deputy" },
            await h.Repo.FindTaskActorsAsync(task.TaskId!.Value));

        // 非待办任务不追加（历史/已完成行不得被改写）
        var done = ProcessTask.Create(1L, "task9", "已办", WfTaskType.Major,
            WfPerformType.Normal, null, new List<string> { "leader" }, "applicant", null, false, h.Ctx.Clock);
        done.TaskState = (int)WfTaskState.Finished;
        await applier.ApplyAsync(done, "simple", Now);
        Assert.Equal(new List<string> { "leader" }, done.ActorIds);
    }

    // ═══ 条款 1.1：processName = 流程模型 name 优先，模型未带 name 才回落 wf_process_define.name ═══

    [Fact]
    public async Task S116_23_BlankModelName_FallsBackToProcessDefineName()
    {
        // 诱饵：content 顶层不带 name（模型 name 为空），而定义行 Name 有值。
        // 缺陷形态是引擎直接把 `exec.ProcessModel?.Name`（null/空串）交给 applier——按判据①
        // 空名只命中"全流程兜底"行，故台账这里**故意不配兜底行**：回落失效时本用例必红。
        var h = NewHarness();
        var content = FlowJsonWithModelName(TestInfra.LoadFlow("01-simple"), null);
        // 诱饵自检：模型 name 确实为空（诱饵不成立 = 用例空转，先证自己下的套）
        Assert.True(string.IsNullOrEmpty(
                ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(content), h.Ctx).Name),
            "诱饵前置：解析出的模型 name 必须为空，否则本用例等于没测回落");
        var defineId = await TestInfra.SaveFlowDefineAsync(h.Repo, "decoy-flow", content);
        await LedgerAsync(h, "leader", "decoyDeputy", processName: "decoy-flow");

        var (iid, taskId) = await AdvancePastApplyAsync(h, defineId);
        Assert.Equal("task1", (await h.Repo.FindTaskByIdAsync(taskId))!.TaskName);
        Assert.Equal(new List<string> { "leader", "decoyDeputy" }, await PersistedActorsAsync(h, taskId));
        Assert.All(h.Ext!.Queries, q => Assert.Equal("decoy-flow", q.ProcessName));
    }

    [Fact]
    public async Task S116_24_ModelNameWinsOverProcessDefineName()
    {
        // define.Name 与 content 顶层 name **不一致**的诱饵行（契约 06 §4.5 条款 1.1 点名各栈保留）。
        // 回落是"缺失才用"，正常情况下认的是**模型 name**（迁移基线：内置版 mldong-wf 的
        // SurrogateInterceptor 用的正是 execution.getProcessModel().getName()）——
        // 取错方向的实现会把 wrong-define-name 那条接走、把 model-only-name 那条漏掉。
        var h = NewHarness();
        var content = FlowJsonWithModelName(TestInfra.LoadFlow("01-simple"), "model-only-name");
        var defineId = await TestInfra.SaveFlowDefineAsync(h.Repo, "wrong-define-name", content);
        await LedgerAsync(h, "applicant", "rightDeputy", processName: "model-only-name");
        await LedgerAsync(h, "leader", "wrongDeputy", processName: "wrong-define-name");

        var (iid, taskId) = await AdvancePastApplyAsync(h, defineId);
        Assert.Equal("task1", (await h.Repo.FindTaskByIdAsync(taskId))!.TaskName);
        // task1 的参与者 leader 配的是"按定义行 name"的委托 → 不该命中
        Assert.Equal(new List<string> { "leader" }, await PersistedActorsAsync(h, taskId));
        // 引擎每次查台账带的都是模型 name，不是定义行 name
        Assert.All(h.Ext!.Queries, q => Assert.Equal("model-only-name", q.ProcessName));
    }

    // ═══ 条款 5 写侧：跨层对拍（门面 save → 内存台账 → 引擎建任务读侧）═══

    [Fact]
    public async Task S116_25_EnabledWriteSideAgreesWithReadSideAcrossLayers()
    {
        // 写读两侧必须同一套语义（条款 5 尾句）：写侧落 1 的行，建任务时代理人必须真的进
        // wf_process_task_actor；落 0 / 落 2 的一律不进。只测各自一侧会漏掉"门面归一了、
        // 读侧又按另一套判据放行"这类分叉。每轮重建基建，上一轮的生效行不串味。
        var cases = new (object? Arg, bool Omit, int Stored, bool Effective)[]
        {
            (null, true, 1, true),           // 缺键 → 契约默认 1
            (1, false, 1, true),
            ("1", false, 1, true),
            (true, false, 1, true),          // 布尔 true → 1
            (0, false, 0, false),            // 显式 0 不被默认值吞
            ("", false, 0, false),           // 空串不是缺键
            ("abc", false, 0, false),
            ("1abc", false, 0, false),
            (false, false, 0, false),        // 布尔 false → 0
            (2, false, 2, false),            // 可解析整数原样落库，读侧「只有 1 生效」
        };
        foreach (var (arg, omit, stored, effective) in cases)
        {
            var h = NewHarness();
            var facade = new JeeflowFacade(h.Ctx);
            var defineId = await TestInfra.SaveFlowDefineAsync(h.Repo, "simple",
                TestInfra.LoadFlow("01-simple"));
            var args = new FlowData
            {
                ["operator"] = "leader", ["surrogate"] = "dep", ["processName"] = "simple",
            };
            if (!omit) args["enabled"] = arg;
            var save = await facade.FlowAsync("processSurrogate/save", args);
            Assert.Equal(0, save["code"]);   // 脏值不得抛错变 500（Node 首版的坑）
            var id = ((Dictionary<string, object?>)save["data"]!)["id"];
            var detail = await facade.FlowAsync("processSurrogate/detail", new FlowData { ["id"] = id });
            var actualStored =
                Convert.ToInt32(((Dictionary<string, object?>)detail["data"]!)["enabled"]);
            Assert.True(actualStored == stored,
                $"写侧落值：enabled={Show(arg)} 期望 {stored} 实得 {actualStored}");

            var (_, taskId) = await AdvancePastApplyAsync(h, defineId);
            var actors = await PersistedActorsAsync(h, taskId);
            var wantActors = effective
                ? new List<string> { "leader", "dep" }
                : new List<string> { "leader" };
            Assert.True(actors.SequenceEqual(wantActors),
                $"跨层对拍：写侧落 {stored} → 读侧生效={effective}（enabled={Show(arg)}）" +
                $"期望 [{string.Join(",", wantActors)}] 实得 [{string.Join(",", actors)}]");
        }
    }

    // ═══ 条款 1：串行会签「每一步推进」单独成钉 ═══

    [Fact]
    public async Task S116_26_SerialCountersign_SecondStepIsItsOwnCreationPath()
    {
        // S116_18 同时断言首步与第二步（两条建单路径混在一条里，红了分不清是哪条）。
        // 本条把委托**只挂在第二步的参与者 userB 上**，第一步 userA 压根没配委托，
        // 于是唯一能让它红的就是"会签推进建的那张新单"漏挂点。
        var h = NewHarness();
        await LedgerAsync(h, "userB", "step2Deputy", processName: "countersign-sequential");
        var defineId = await TestInfra.SaveFlowDefineAsync(h.Repo, "countersign-sequential",
            TestInfra.LoadFlow("06-countersign-sequential"));
        var (iid, step1Id) = await AdvancePastApplyAsync(h, defineId);
        Assert.Equal("task1", (await h.Repo.FindTaskByIdAsync(step1Id))!.TaskName);
        Assert.Equal(new List<string> { "userA" }, await PersistedActorsAsync(h, step1Id));

        await h.Engine.ExecuteProcessTaskAsync(step1Id, "userA", new FlowData());
        var step2 = (await h.Repo.FindDoingTasksAsync(iid, null))[0];
        Assert.NotEqual(step1Id, step2.TaskId);
        Assert.Equal(new List<string> { "userB", "step2Deputy" },
            await PersistedActorsAsync(h, step2.TaskId!.Value));
        AssertRosterUnchanged(step2, 1);   // 名册仍两票，step2Deputy 没混进 operatorList
    }

    [Fact]
    public async Task S116_22_MultiplePrincipalsInOneTask_EachQueriedOnce()
    {
        // 一个任务多参与者（并行会签每人一条任务）：逐个各查一次，代理人各自并入、不重复
        var h = NewHarness();
        await LedgerAsync(h, "userA", "agentA", processName: "countersign-parallel");
        await LedgerAsync(h, "userB", "agentB", processName: "countersign-parallel");
        var defineId = await TestInfra.SaveFlowDefineAsync(h.Repo, "countersign-parallel",
            TestInfra.LoadFlow("05-countersign-parallel"));
        var (iid, _) = await AdvancePastApplyAsync(h, defineId);

        var doing = await h.Repo.FindDoingTasksAsync(iid, null);
        Assert.Equal(3, doing.Count);                    // 05 是 userA,userB,userC 全员并行会签
        foreach (var t in doing)
        {
            var actors = await PersistedActorsAsync(h, t.TaskId!.Value);
            switch (actors[0])
            {
                case "userA": Assert.Equal(new List<string> { "userA", "agentA" }, actors); break;
                case "userB": Assert.Equal(new List<string> { "userB", "agentB" }, actors); break;
                default: Assert.Equal(new List<string> { "userC" }, actors); break;
            }
        }
    }

    // ═══ issues/123 · 规范 06 §4.5 条款 1.4：多条并存时由「最新一条」裁决（内存仓侧）═══
    // SQL 仓同形用例见 MySqlBehaviorSuite.T1_Surrogate_i123_*。
    // 缺这几条，把实现改回"先滤生效、再从剩下的取最新"也不会红——而那个写法正是 13 栈
    // 在 L2-17/L2-18 上恒并入的根因（上一条窗内委托会把用户后续设置永久盖掉）。

    /// <summary>A 格：先配"窗内 + enabled=1"，再配一条更"新"的无效记录 ⇒ 建单不并入，
    /// 且**旧的那条有效记录不得复活**（四种无效形状各一格）。
    /// id 序刻意错开（旧 900 / 新 1000）——"更新"指的是主键最大，不是插入序末条。</summary>
    [Fact]
    public async Task S116_27_NewestInvalidBeatsOlderEffective_MemoryRepo()
    {
        var cases = new (string Why, string Agent, int Enabled, DateTime? Start, DateTime? End)[]
        {
            ("窗外（已过期）", "deputyExpired", 1, Now.AddDays(-10), Now.AddDays(-9)),
            ("窗外（未开始）", "deputyFuture", 1, Now.AddDays(1), Now.AddDays(2)),
            ("enabled=0", "deputyOff", 0, null, null),
            ("enabled 脏值 2（契约：只认 1）", "deputyDirty", 2, null, null),
            ("自委托（代理人就是授权人本人）", "leader", 1, null, null),
        };
        foreach (var (why, agent, enabled, start, end) in cases)
        {
            var h = NewHarness();
            await LedgerAsync(h, "leader", "deputyOldValid",
                start: Now.AddDays(-1), end: Now.AddDays(1), id: 900);      // 旧：窗内 + enabled=1
            await LedgerAsync(h, "leader", agent, enabled: enabled,
                start: start, end: end, id: 1000);                           // 新：由它裁决

            var (_, taskId) = await StartToLeaderTaskAsync(h);
            Assert.True(
                (await PersistedActorsAsync(h, taskId)).SequenceEqual(new List<string> { "leader" }),
                $"{why} ⇒ 最新一条不生效时不得并入代理人（旧的 deputyOldValid 更不得复活）");
            Assert.True(
                await h.Ext!.GetSurrogateAsync("leader", "simple", Now) == null,
                $"{why} ⇒ 仓储判据本身也必须判否，不得回落到旧的窗内有效行");
        }
    }

    /// <summary>B 格：作用域内**只有一条**"窗内 + enabled=1" ⇒ 必须并入。
    /// 防 A 格的修法被写成恒不并入。</summary>
    [Fact]
    public async Task S116_28_SoleEffectiveRowStillApplied_MemoryRepo()
    {
        var h = NewHarness();
        await LedgerAsync(h, "leader", "deputyOnly", start: Now.AddDays(-1), end: Now.AddDays(1));
        var (_, taskId) = await StartToLeaderTaskAsync(h);
        Assert.Equal(new List<string> { "leader", "deputyOnly" }, await PersistedActorsAsync(h, taskId));
        var hit = await h.Ext!.GetSurrogateAsync("leader", "simple", Now);
        Assert.Equal("deputyOnly", hit?.Surrogate);
    }

    /// <summary>精确作用域最新一条判否后**仍要看全流程作用域的最新一条**（不得判否即止）。
    /// 钉 Java <c>JdbcProcessExtRepositoryTest#testSurrogateCrudAndGet</c> 的同一条形：
    /// 精确那条已过期 ⇒ 兜底到全流程委托的代理人。</summary>
    [Fact]
    public async Task S116_29_ExactScopeInvalidStillFallsBackToGlobal_MemoryRepo()
    {
        var h = NewHarness();
        // 全流程委托（processName 空）：有效，但 id 更小（不是"最新一条"跨作用域通吃）
        await LedgerAsync(h, "leader", "deputyGlobal", processName: "",
            start: Now.AddDays(-1), end: Now.AddDays(1), id: 900);
        // 精确流程委托：id 最大但已过期 ⇒ 判否后仍要落到上面那条全流程委托
        await LedgerAsync(h, "leader", "deputyExpired", processName: "simple",
            start: Now.AddDays(-10), end: Now.AddDays(-9), id: 1000);

        var hit = await h.Ext!.GetSurrogateAsync("leader", "simple", Now);
        Assert.Equal("deputyGlobal", hit?.Surrogate);
        var (_, taskId) = await StartToLeaderTaskAsync(h);
        Assert.Equal(new List<string> { "leader", "deputyGlobal" }, await PersistedActorsAsync(h, taskId));
    }

    // ── 辅助 ──

    /// <summary>改流程 JSON 顶层 name（<c>null</c> = 删掉该键 → 模型未带 name），造条款 1.1 的诱饵。
    /// 必须绕开门面 deploy：deploy 执行 <c>def.Name = model.Name</c>，两者永远一致，也就永远测不出
    /// 实现到底取的哪一个。</summary>
    private static string FlowJsonWithModelName(string flowJson, string? modelName)
    {
        var root = TestInfra.ParseFlowJson(flowJson);
        if (modelName == null) root.Remove("name");
        else root["name"] = modelName;
        return DefaultJsonProvider.Instance.ToJson(root);
    }

    private static string Show(object? v) =>
        v == null ? "<缺键>" : (v is string s ? $"\"{s}\"" : v.ToString() ?? v.GetType().Name);
}

using Mldong.Jeeflow.Core;
using Xunit;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/137 A · 裁定 A · <b>实例级</b> <c>expire_time</c> ＝「定义<b>顶层</b>表达式的<b>求值结果</b>」
/// （批二 §3-4；基准＝java <c>JeeflowEngineImpl.java:93-96</c> ＝ boot2
/// <c>ProcessInstanceServiceImpl.java:157-160</c>；本栈引擎侧 <c>JeeflowEngine.cs:59-61</c>
/// 早就是这个形状，钟由调用点注入）。
///
/// <para><b>本栈的病灶和 java 逐字同一处</b>：引擎读 <c>model.ExpireTime</c>，而
/// <see cref="ModelParser"/> 的 <c>ProcessModel</c> 初始化器（<c>ModelParser.cs:81-92</c>）逐字段搬了
/// name/displayName/type/instanceUrl/instanceNoClass/post|preInterceptors/relTableName/persistMode
/// <b>九项、唯独没有 ExpireTime</b>（<c>ProcessModel.cs:8</c> 那个属性一直在，全仓无一处给它赋值）
/// ⇒ 守卫永远读到 null、<c>wf_process_instance.expire_time</c> 在本栈<b>恒 NULL</b>——
/// "形状是 A、链路断在解析这一跳"。这条断链由批二 §3-4 的 go 腿普查查出（java 同病，
/// 已随 <c>jeeflow-java d5d0395</c> 补上），故 §3-4 真正的整改面是<b>八栈</b>而不是案文写的六栈。
/// <see cref="ParserCarriesRootExpireTimeIntoProcessModel"/> 就是那一跳的取证格：摘掉新加的赋值它当场红。</para>
///
/// <para>四条口径与兄弟栈同尺：①进列的是<b>时刻</b>不是表达式原串；②变量源＝<b>发起参数</b>那份
/// （注入 <c>u_*</c> 与 <c>autoGenTitle</c> 之后，与引擎 <c>:59-61</c> 就地用的同一个 <c>args</c>）；
/// ③没配（缺键／空串／纯空白）⇒ NULL，不赋 <c>now()</c>、不赋空串；④配了但算不出
/// （误配／负数档，§3-2；带空白的边界，§3-3）⇒ NULL，任何一档都不许兜底当前时间。
/// ⑤尺子复用 <see cref="FlowUtil.ProcessTime"/>（任务行四处写点同一枚），不新造第二把。</para>
/// </summary>
public class InstanceExpireTime137Tests
{
    private readonly JeeflowEngine _engine;
    private readonly MemoryRepository _repo;
    private readonly ServiceContext _ctx;
    private static int _seq;

    public InstanceExpireTime137Tests()
    {
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        _engine = engine;
        _repo = repo;
        _ctx = ctx;
    }

    /// <summary>根上带不带 <c>expireTime</c> 由参数决定；<c>null</c> ＝压根没这个键（"没配"的正解形状）。</summary>
    /// <remarks>用**拼接**而不是 raw 内插字符串：JSON 里的连续右大括号会让 <c>$$"""</c> 解析器歧义
    /// （实测 CS9007/CS1733），拼接姿势与同仓 <c>ExpireTime126Tests</c> 的夹具一致。</remarks>
    private static string Flow(string? rootExpire, string? nodeExpire = null)
    {
        var root = rootExpire is null ? "" : "\"expireTime\": \"" + rootExpire + "\",";
        var nodeProps = nodeExpire is null
            ? "\"assignee\": \"leader\", \"taskType\": 0, \"performType\": 0"
            : "\"assignee\": \"leader\", \"taskType\": 0, \"performType\": 0, \"expireTime\": \"" + nodeExpire + "\"";
        return "{\"name\": \"i137a-instance\", " + root + " \"displayName\": \"实例级到期\", \"type\": \"approval\","
            + " \"nodes\": ["
            + "{\"id\": \"start\", \"type\": \"snaker:start\", \"text\": {\"value\": \"开始\"}, \"properties\": {}},"
            + "{\"id\": \"approve\", \"type\": \"snaker:task\", \"text\": {\"value\": \"审批\"}, \"properties\": {" + nodeProps + "}},"
            + "{\"id\": \"end\", \"type\": \"snaker:end\", \"text\": {\"value\": \"结束\"}, \"properties\": {}}"
            + "], \"edges\": ["
            + "{\"id\": \"e1\", \"sourceNodeId\": \"start\", \"targetNodeId\": \"approve\", \"properties\": {}},"
            + "{\"id\": \"e2\", \"sourceNodeId\": \"approve\", \"targetNodeId\": \"end\", \"properties\": {}}"
            + "]}";
    }

    private async Task<ProcessInstance> StartAsync(string? rootExpire, FlowData? args = null, string? nodeExpire = null)
    {
        var defId = await TestInfra.SaveFlowDefineAsync(_repo, "i137a-" + _seq++, Flow(rootExpire, nodeExpire));
        return await _engine.StartProcessInstanceByIdAsync(defId, "leader", args ?? new FlowData());
    }

    /// <summary>同一行 expire − create 落进 [want-5s, want+60s]（与任务行那族同带宽；占位 now() 算出的 ≈0 被夹在外面）。</summary>
    private static void AssertDelta(ProcessInstance inst, double wantSeconds, string why)
    {
        Assert.NotNull(inst.CreateTime);
        Assert.NotNull(inst.ExpireTime);
        var delta = (inst.ExpireTime!.Value - inst.CreateTime!.Value).TotalSeconds;
        Assert.True(delta >= wantSeconds - 5 && delta <= wantSeconds + 60,
            $"{why}：expire − create 应≈{wantSeconds}s（实得 {delta:F0}s）；算出≈0 就是兜底 now()、算不出就该是 NULL");
    }

    // ═══ 1. 断链那一跳：解析期必须把根上的表达式搬进 ProcessModel ═══

    /// <summary>
    /// 本案的改前红样就在这一格：引擎那句读的就是 <c>ProcessModel.ExpireTime</c>，解析期没搬＝恒 null。
    /// 同时钉"缺键 ⇒ null（不是空串占位）"与"定义级/节点级两层两个键互不污染"。
    /// </summary>
    [Fact]
    public void ParserCarriesRootExpireTimeIntoProcessModel()
    {
        var with = ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(Flow("2h")), _ctx);
        Assert.Equal("2h", with.ExpireTime);

        var absent = ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(Flow(null)), _ctx);
        Assert.Null(absent.ExpireTime);

        var twoLevels = ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(Flow("3h", "1d")), _ctx);
        Assert.Equal("3h", twoLevels.ExpireTime);
        var node = twoLevels.Tasks.OfType<TaskModel>().FirstOrDefault(t => t.Name == "approve");
        Assert.NotNull(node);
        Assert.Equal("1d", node!.ExpireTime);   // 节点级那一档仍是节点自己的 properties.expireTime
    }

    // ═══ 2. 口径①：进列的是时刻，且同一行 expire − create ≈ 表达式偏移 ═══

    [Fact]
    public async Task RelativeTierOnInstanceBecomesAMomentNotRawExpression()
    {
        var inst = await StartAsync("2h");
        Assert.NotNull(inst.ExpireTime);
        // 列上是**求值结果**：DateTime 类型本身就不可能是 "2h"，这一断钉住"值真算出来了"
        Assert.True(inst.ExpireTime!.Value > inst.CreateTime!.Value,
            "expire 必须晚于 create（早于或等于＝兜底 now()／回拨档放行的形状）");
        AssertDelta(inst, 7200, "定义配 2h");
    }

    /// <summary><c>d</c> 档走日历加天（<c>AddDays</c>），与 s/m/h 的毫秒加法不同形 ⇒ 单独一格。</summary>
    [Fact]
    public async Task DayTierOnInstanceIsCalendarBased()
    {
        var inst = await StartAsync("3d");
        AssertDelta(inst, 3 * 24 * 3600, "定义配 3d（日历加天）");
    }

    /// <summary>绝对档：表达式本身就是期望完成时刻 ⇒ 实例列等于那一刻（逐值，不留带宽）。</summary>
    [Fact]
    public async Task AbsoluteTierOnInstanceIsThatInstant()
    {
        var inst = await StartAsync("2027-03-04 05:06:07");
        Assert.Equal(new DateTime(2027, 3, 4, 5, 6, 7), inst.ExpireTime);
    }

    // ═══ 3. 口径②⑤：变量源＝发起参数那份；尺子与任务行同一枚 ═══

    [Fact]
    public async Task VariableTierTakesTheStartArgs()
    {
        var args = new FlowData { ["dueAt"] = "2026-12-31 10:00:00" };
        var inst = await StartAsync("dueAt", args);
        Assert.Equal(new DateTime(2026, 12, 31, 10, 0, 0), inst.ExpireTime);
    }

    /// <summary>变量档<b>优先于</b>相对档：根上表达式恰好是变量名、args 里真有那个键 ⇒ 取变量值。</summary>
    [Fact]
    public async Task VariableTierBeatsRelativeTier()
    {
        var args = new FlowData { ["2h"] = "2028-08-08 08:08:08" };
        var inst = await StartAsync("2h", args);
        Assert.Equal(new DateTime(2028, 8, 8, 8, 8, 8), inst.ExpireTime);
    }

    /// <summary>
    /// 口径⑤：实例行与 <see cref="FlowUtil.ProcessTime"/> 直调<b>同解</b>——同一枚尺子（任务行四处写点用的
    /// 就是它），谁给实例级另造一把（自己拼 <c>AddSeconds(90)</c>、或换一枚解析器）就红。
    /// </summary>
    /// <remarks>
    /// 直调必须<b>钉在同一块钟上</b>：本栈 <c>ProcessTime</c> 的基准由调用点注入（issues/120，
    /// 且它不做 null 兜底——传 null 直接 NRE，实测踩过），而 <c>TestInfra</c> 给引擎注的是固定钟。
    /// 姿势＝拿<b>本行自己的 create_time</b> 造一枚 <see cref="FixedClock"/> 去直调，
    /// 两值必须逐秒相同；用 <c>SystemClock.Instance</c> 去比会差出 61 天（那是两把不同的<b>钟</b>，
    /// 不是两把不同的<b>尺子</b>，判不出任何东西——本轮第一版就栽在这里）。
    /// </remarks>
    [Fact]
    public async Task InstanceAndEvaluatorGiveSameAnswer()
    {
        var inst = await StartAsync("90s");
        Assert.NotNull(inst.ExpireTime);
        Assert.NotNull(inst.CreateTime);
        var direct = FlowUtil.ProcessTime("90s", new FlowData(), new FixedClock(inst.CreateTime!.Value));
        Assert.NotNull(direct);
        Assert.Equal(direct!.Value, inst.ExpireTime!.Value);
    }

    // ═══ 4. 口径③④：没配 / 算不出 ⇒ NULL，任何一档都不许兜底当前时间 ═══

    /// <summary>缺键／空串／纯空白三档都算"没配"；每档都配一枚"同行 create_time 有值"的对照（防恒真）。</summary>
    [Fact]
    public async Task UnconfiguredDefinitionKeepsInstanceColumnNull()
    {
        foreach (var root in new string?[] { null, "", "   " })
        {
            var inst = await StartAsync(root);
            Assert.NotNull(inst.CreateTime);   // 对照：实例确实建出来了
            Assert.True(inst.ExpireTime == null,
                $"根上「{root}」算没配 ⇒ 这一列必须 NULL，不赋 now()、不赋空串（实得 {inst.ExpireTime}）");
        }
    }

    /// <summary>
    /// 配了但算不出：误配（<c>not-a-time</c>／<c>xh</c>／小数前缀 <c>2.5h</c>）、
    /// 负数档（<c>-5h</c>／<c>-3d</c>，§3-2）、带空白的负档（§3-3 裁完仍判负）、
    /// 单位符后带空白（§3-3 分界②）⇒ 一律 NULL。
    /// </summary>
    [Fact]
    public async Task UnparsableExpressionKeepsInstanceColumnNull()
    {
        foreach (var root in new[] { "not-a-time", "xh", "2.5h", "-5h", "-3d", " -5h", "2h " })
        {
            var inst = await StartAsync(root);
            Assert.NotNull(inst.CreateTime);
            Assert.True(inst.ExpireTime == null,
                $"表达式「{root}」算不出 ⇒ 这一列必须 NULL（不兜底 now、不写原串；实得 {inst.ExpireTime}）");
        }
    }
}

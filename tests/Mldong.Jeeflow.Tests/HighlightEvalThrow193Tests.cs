using System.Text;
using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/193（队列 R27）csharp 腿：highLight 模型补全腿里 **collectPath 那半腿**的异常面。
///
/// 缺陷形状＝<see cref="JeeflowFacade"/> 的 `CollectPath(...)` 调用不带 `await`，异步方法里抛出的
/// 异常一律收进返回的 Task（不会同步上抛），而该 Task 无人观察 ⇒ `:354` 的 catch 恒抓不到补全腿
/// 的失败 ⇒ issues/162 立的那句「保留降级面＋落可诊断 WARN」在本栈只对 parse/build 半腿生效，
/// collectPath 半腿仍是零日志静默中断。
///
/// 判据三枚：
/// ① 求值器抛 ⇒ 对外仍 code=0、决策分支腿不收录（降级面不变），nodeProgress 保留（已赋值在前），
///    且 stderr 真拿到 instanceId=/defineId=/err= 三件；
/// ② 正常求值器（正向对照）⇒ 决策 true 边收录、false 边不收录，且不得出现该 WARN；
/// ③ 坏定义 ⇒ 同 WARN（162 既有腿不回归）。
/// </summary>
[Collection("stderr")]
public class HighlightEvalThrow193Tests
{
    private readonly MemoryRepository _repo;
    private readonly MemoryExtRepository _ext;
    private readonly ServiceContext _ctx;
    private readonly JeeflowEngine _engine;
    private readonly JeeflowFacade _facade;

    public HighlightEvalThrow193Tests()
    {
        _repo = new MemoryRepository();
        _ext = new MemoryExtRepository(_repo, null);
        _ctx = new ServiceContext(_repo, _ext);
        _ctx.Clock = new FixedClock(new DateTime(2026, 8, 1, 9, 0, 0));
        _ctx.IdGenerator = new AtomicIdGenerator(1, _ctx.Clock);
        _ctx.UserProvider = new TestUserProvider();
        _ctx.UserSearchProvider = new TestUserSearchProvider();
        TestInfra.RegisterBuiltins(_ctx);
        _repo.Configure(_ctx);
        _engine = new JeeflowEngine(_ctx);
        _facade = new JeeflowFacade(_ctx);
    }

    /// <summary>求值器实现：一被调用就抛（＝宿主 SPI 自身失败档）。</summary>
    private sealed class ThrowingEvaluator : IExpressionEvaluator
    {
        public object? Eval(string expression, IDictionary<string, object?> context)
            => throw new InvalidOperationException("evaluator boom: " + expression);
    }

    /// <summary>线程安全的 stderr 捕获（形状照 HighlightDegradeLog162Tests）。</summary>
    private sealed class CaptureWriter : TextWriter
    {
        private readonly StringBuilder _sb = new();
        private readonly object _lock = new();
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { lock (_lock) _sb.Append(value); }
        public override void Write(string? value) { lock (_lock) _sb.Append(value); }
        public override void WriteLine(string? value) { lock (_lock) _sb.Append(value).Append('\n'); }
        public string Text() { lock (_lock) return _sb.ToString(); }
    }

    private static List<string> WarnLines(string captured) =>
        captured.Split('\n').Where(l => l.Contains("highLight 模型补全腿解码失败")).ToList();

    /// <summary>决策流实例：start→apply→task1→decision1，办理到 decision1 之后（活跃＝task2）。</summary>
    private async Task<(long InstanceId, long DefineId)> SeedDecisionInstanceAsync()
    {
        var did = await TestInfra.SaveFlowDefineAsync(_repo, "hl-193", TestInfra.LoadFlow("03-decision-expr"));
        var inst = await _engine.StartProcessInstanceByIdAsync(did, "applicant",
            new FlowData { ["amount"] = 2000 });
        var apply = await TestInfra.FindDoingForAsync(_repo, inst.InstanceId!.Value, "applicant");
        await _engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData());
        var t1 = await TestInfra.FindDoingForAsync(_repo, inst.InstanceId.Value, "leader");
        await _engine.ExecuteProcessTaskAsync(t1.TaskId!.Value, "leader",
            new FlowData { ["amount"] = 2000 }); // amount>1000 ⇒ e3 分支，活跃落 task2
        return (inst.InstanceId!.Value, did);
    }

    private async Task<Dictionary<string, object?>> HighLightAsync(long instanceId) =>
        await _facade.FlowAsync("processInstance/highLight", new FlowData { ["id"] = instanceId });

    [Fact]
    public async Task EvalThrow_LegDegradesWithWarnLog()
    {
        var (instanceId, defineId) = await SeedDecisionInstanceAsync();
        // 办理链跑完再换求值器：引擎运行期也要用同一枚 SPI，早换会把 start/execute 一起打断
        _ctx.ExpressionEvaluator = new ThrowingEvaluator();

        var capture = new CaptureWriter();
        var original = Console.Error;
        Console.SetError(capture);
        Dictionary<string, object?> resp;
        try
        {
            resp = await HighLightAsync(instanceId);
        }
        finally
        {
            Console.SetError(original);
        }

        // 降级面（修前后都不许变）：对外仍成功信封、决策两条出边都不收录、nodeProgress 已赋值在前故保留
        Assert.Equal(0L, Convert.ToInt64(resp["code"]));
        var data = (Dictionary<string, object?>)resp["data"]!;
        var edges = (List<string>)data["historyEdgeNames"]!;
        Assert.DoesNotContain("e3", edges);
        Assert.DoesNotContain("e4", edges);
        Assert.NotEmpty((Dictionary<string, object?>)data["nodeProgress"]!);

        // 日志面（本卡修点）：异常在未 await 的 Task 里 ⇒ catch 抓不到 ⇒ 修前零行
        var warns = WarnLines(capture.Text());
        Assert.True(warns.Count > 0, "求值器抛须落 WARN（实得零行捕获＝补全腿异常逃逸 catch）");
        var joined = string.Join("|", warns);
        Assert.Contains("instanceId=" + instanceId, joined);
        Assert.Contains("defineId=" + defineId, joined);
        Assert.Contains("err=", joined);
    }

    [Fact]
    public async Task NormalEvaluator_CollectsTrueEdgeWithoutWarn()
    {
        var (instanceId, _) = await SeedDecisionInstanceAsync();

        var capture = new CaptureWriter();
        var original = Console.Error;
        Console.SetError(capture);
        Dictionary<string, object?> resp;
        try
        {
            resp = await HighLightAsync(instanceId);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal(0L, Convert.ToInt64(resp["code"]));
        var data = (Dictionary<string, object?>)resp["data"]!;
        var edges = (List<string>)data["historyEdgeNames"]!;
        Assert.Contains("e3", edges);   // amount>1000 ⇒ 真边收录
        Assert.DoesNotContain("e4", edges); // 假边不收录（判真只认字面 true）
        Assert.True(WarnLines(capture.Text()).Count == 0,
            "正常求值不得出现降级 WARN：" + string.Join("|", WarnLines(capture.Text())));
    }
}

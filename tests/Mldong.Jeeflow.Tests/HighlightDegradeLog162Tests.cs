using System.Text;
using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/162 csharp 腿（队列 R5/R7 搭车）：highLight 块级 catch 保留降级面＋落可诊断 WARN。
///
/// 判据两枚（形状照 §6.1，php 156／python 160／go·node·rust 已同形收口）：
/// ① 坏定义 ⇒ 对外仍 code=0、边集空、nodeProgress 空、活跃行不受影响，且 stderr 真拿到
///    instanceId=／defineId=／err= 三件（进程内捕获 Console.Error，不接受"注释自称"）；
/// ② 好定义 ⇒ 非空边集且**不得出现**该 WARN。
/// </summary>
public class HighlightDegradeLog162Tests
{
    private readonly MemoryRepository _repo;
    private readonly MemoryExtRepository _ext;
    private readonly ServiceContext _ctx;
    private readonly JeeflowEngine _engine;
    private readonly JeeflowFacade _facade;

    public HighlightDegradeLog162Tests()
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

    /// <summary>线程安全的 stderr 捕获（并行测试类可能同时在写 Console.Error）。</summary>
    private sealed class CaptureWriter : TextWriter
    {
        private readonly StringBuilder _sb = new();
        private readonly object _lock = new();
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { lock (_lock) _sb.Append(value); }
        public override void Write(string? value) { lock (_lock) _sb.Append(value); }
        public override void WriteLine(string? value) { lock (_lock) _sb.Append(value).Append('\n'); }
        public string Text() { lock (_lock) return _sb.ToString(); }
        public void Clear() { lock (_lock) _sb.Clear(); }
    }

    private static List<string> WarnLines(string captured) =>
        captured.Split('\n').Where(l => l.Contains("highLight 模型补全腿解码失败")).ToList();

    private async Task<(long InstanceId, long DefineId)> SeedAsync()
    {
        var define = new ProcessDefine
        {
            Name = "hl-162",
            DisplayName = "162-高亮降级",
            Type = "approval",
            State = 1,
            Content = Encoding.UTF8.GetBytes(TestInfra.LoadFlow("01-simple")),
            Version = 1,
        };
        await _repo.SaveDefineAsync(define);
        var instanceId = await TestInfra.StartAndApplyAsync(_engine, _repo, define.Id!.Value,
            new FlowData { [FlowConst.BusinessNo] = "HL-162" });
        return (instanceId, define.Id.Value);
    }

    private async Task<Dictionary<string, object?>> HighLightAsync(long instanceId) =>
        await _facade.FlowAsync("processInstance/highLight", new FlowData { ["id"] = instanceId });

    [Fact]
    public async Task BadDefine_DegradesWithWarnLog()
    {
        var (instanceId, defineId) = await SeedAsync();
        var def = await _repo.FindDefineByIdAsync(defineId);
        // 换成坏内容（直改仓储，绕开解析层）→ highLight 补全腿必走降级支
        def!.Content = Encoding.UTF8.GetBytes("{ not-json");
        await _repo.UpdateDefineAsync(def);

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
        Assert.Empty((List<string>)data["historyEdgeNames"]!);
        Assert.Empty((Dictionary<string, object?>)data["nodeProgress"]!);
        Assert.Contains("task1", (List<string>)data["activeNodeNames"]!);

        var warns = WarnLines(capture.Text());
        Assert.True(warns.Count > 0, "坏定义须落 WARN（实得零行捕获）");
        var joined = string.Join("|", warns);
        Assert.Contains("instanceId=" + instanceId, joined);
        Assert.Contains("defineId=" + defineId, joined);
        Assert.Contains("err=", joined);
    }

    [Fact]
    public async Task GoodDefine_NoWarnLog()
    {
        var (instanceId, _) = await SeedAsync();

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
        Assert.NotEmpty((List<string>)data["historyEdgeNames"]!);
        Assert.True(WarnLines(capture.Text()).Count == 0,
            "好定义不得出现降级 WARN：" + string.Join("|", WarnLines(capture.Text())));
    }
}

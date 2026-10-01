using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;
using Mldong.Jeeflow.Persist;
using Xunit;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/137-G／139 的 c# 腿：门面出口 msg 不得携带内部异常原文（cause 分离——原文只进日志/错误对象）。
///
/// <para>改前形状两处：<c>ModelParser.Parse</c> 的 JSON 语法错那一档**根本没有 try/catch**，
/// System.Text.Json 带行列号的原文（如 "'{' is invalid after a value..."）一路冒到
/// <c>JeeflowFacade.cs</c> 的 <c>Error(e.Message)</c> 直接进用户面；bizData 腿则把
/// <c>InnerException.Message</c> 拼进 msg（与 java <c>JeeflowFacade</c> 那条逐字同形）。
/// 尺子＝java 逐字基准「读取流程定义 JSON 失败」＋固定文案「业务数据读取失败」。
/// 正向对照（同一条腿喂合法 JSON）保证判据不是恒真。</para>
/// </summary>
public class ErrorDetailNoLeak137Tests
{
    private const string BaselineParseFailed = "读取流程定义 JSON 失败";
    private const string BaselineBizFailed = "业务数据读取失败";

    private readonly MemoryRepository _repo = new();
    private readonly ServiceContext _ctx;
    private readonly JeeflowEngine _engine;
    private readonly JeeflowFacade _facade;

    public ErrorDetailNoLeak137Tests()
    {
        var ext = new MemoryExtRepository(_repo, null);
        _ctx = new ServiceContext(_repo, ext);
        _ctx.Clock = new FixedClock(new DateTime(2026, 10, 1, 9, 0, 0));
        _ctx.IdGenerator = new AtomicIdGenerator(1, _ctx.Clock);
        _ctx.UserProvider = new TestUserProvider();
        _ctx.UserSearchProvider = new TestUserSearchProvider();
        TestInfra.RegisterBuiltins(_ctx);
        _repo.Configure(_ctx);
        _engine = new JeeflowEngine(_ctx);
        _facade = new JeeflowFacade(_ctx);
    }

    /// <summary>负向（改前红）：主腿抛出的异常只带逐字基准文案，原文进 InnerException ＋ WARNING。</summary>
    [Fact]
    public void Parse_BrokenJson_ThrowsBaselineTextAndKeepsCause()
    {
        var lines = new List<string>();
        _ctx.WarningSinkForTest = lines.Add;

        var ex = Assert.Throws<JeeflowException>(() =>
            ModelParser.Parse(Encoding.UTF8.GetBytes("{\"name\": \"x\", 这不是合法的 json"), _ctx));

        Assert.Equal(BaselineParseFailed, ex.Message);
        Assert.NotNull(ex.InnerException);   // cause 分离：原文留在错误对象里，不进出口文案
        var line = Assert.Single(lines);
        Assert.Contains(BaselineParseFailed, line);
    }

    /// <summary>负向（改前红）：门面出口 msg ＝ 逐字基准文案，不含 System.Text.Json 的行列号原文。</summary>
    [Fact]
    public async Task FacadeExit_DeployBrokenJson_MsgCarriesNoInternals()
    {
        var resp = await _facade.FlowAsync("processDefine/deploy", new FlowData
        {
            ["operator"] = "user1",
            ["content"] = "{\"name\": \"x\", 这不是合法的 json",
        });

        Assert.Equal(99999999, resp["code"]);
        var msg = resp["msg"]!.ToString()!;
        Assert.Equal(BaselineParseFailed, msg);
        Assert.DoesNotContain("invalid", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("line", msg, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>正向对照：同一条腿喂合法 JSON 不得被判成解析失败（判据不恒真）。</summary>
    [Fact]
    public async Task FacadeExit_DeployLegalJson_IsNotReportedAsParseFailure()
    {
        var resp = await _facade.FlowAsync("processDefine/deploy", new FlowData
        {
            ["operator"] = "user1",
            ["content"] = TestInfra.LoadFlow("01-simple"),
        });

        Assert.DoesNotContain(BaselineParseFailed, resp["msg"]!.ToString()!);
    }

    /// <summary>负向（改前红）：bizData 腿 msg 只给固定文案，Reader 内部原文不得拼接进来。</summary>
    [Fact]
    public async Task BizData_ReaderThrows_MsgIsFixedTextWithoutCauseDetail()
    {
        var did = await TestInfra.SaveFlowDefineAsync(_repo, "biz-137g",
            TestInfra.LoadFlow("01-simple")
                .Replace("\"name\": \"simple\"",
                    "\"name\": \"biz_137g\"\n  ,\"relTableName\": \"biz_137g_tbl\"")
                .Replace("  ,\"relTableName\"", ",\"relTableName\""));
        var inst = await _engine.StartProcessInstanceByIdAsync(did, "user1", new FlowData());
        _ctx.BizDataReader = new BoomReader();

        var resp = await _facade.FlowAsync("processInstance/bizData",
            new FlowData { [FlowConst.ProcessInstanceIdKey] = inst.InstanceId });

        Assert.Equal(99999999, resp["code"]);
        var msg = resp["msg"]!.ToString()!;
        Assert.Equal(BaselineBizFailed, msg);
        Assert.DoesNotContain("12345", msg);   // 改前：msg = "业务数据读取失败: 内部细节 12345"
    }

    private sealed class BoomReader : IBizDataReader
    {
        public Task<Dictionary<string, object?>?> ReadByProcessInstanceAsync(
            string tableName, object? processInstanceId)
            => throw new InvalidOperationException("内部细节 12345");
    }
}

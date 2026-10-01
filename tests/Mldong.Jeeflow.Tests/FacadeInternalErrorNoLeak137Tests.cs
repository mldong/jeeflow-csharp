using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text.Json;
using System.Threading.Tasks;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;
using Xunit;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/137 §3-1（spec 06-facade.md §2.12）· C# 腿：<b>门面内部异常出口</b>——固定文案
/// <c>流程处理失败</c> ＋「谁写的这段文案」判别式。判据形状照 java 参考实现
/// <c>FacadeInternalErrorNoLeakTest</c> ＋ <c>JeeflowFacade.isForeignDetail(type, message, cause, trace)</c>。
///
/// <para>改前形状（本栈主泄漏点，启动词 §2-B 点名那条）＝<c>JeeflowFacade.cs:114</c>
/// <c>return Error(e.Message ?? e.ToString());</c>：门面顶层 catch 把任何异常的 <c>Message</c> 原样外透。
/// ⚠️ 顺带查清一件事：<c>?? e.ToString()</c> 这一腿在 .NET 里其实是<b>死码</b>——<c>Exception.Message</c>
/// 永不为 null（无 message 时框架回落成 <c>Exception of type 'System.Xxx' was thrown.</c>／本地化
/// 「发生类型为“System.Xxx”的异常。」），所以真正会外透的兜底文案<b>本身就带类型全名</b>；
/// 判别式第 1 条因此判两档：空白 ＋ 文案里带自己类型全名（java <c>message == null</c> 的对偶）。</para>
///
/// <para>两侧都要有牙：<b>负向</b>＝运行时／反射／IO／驱动／JSON 解析器／集成方 provider 写的原文
/// 不得进 <c>msg</c>，只能进日志与 <c>InnerException</c>；<b>正向／回归</b>＝引擎自己写的中文契约文案
/// 必须<b>逐字</b>留在 <c>msg</c>——这条不是"顺手保旧行为"，其余七栈、十三个集成壳与前端 toast 都按原文
/// 对齐，把判据收窄成"一律固定文案"就是静默改契约面（本文件 §C 那组就是为变异②准备的牙）。
/// 只断言 <c>msg</c> 等于固定文案是不够的：那样"把原文整个丢掉"也能绿，故每条负向都同时断言
/// 原文确实进了日志（<see cref="JeeflowFacade.InternalFailureSinkForTest"/> 取证钩子）。</para>
///
/// <para>本栈普查结论（2026-10-02 现读，<c>src/</c> 下 41 处 <c>throw new</c>）：<b>有</b>专门契约异常类型
/// <see cref="JeeflowException"/>（35 处），但**仍有裸异常腿**携带引擎自己写的中文文案——
/// <c>InvalidOperationException</c>「未支持的分页行类型: X」（门面 <c>PageResultOut</c>，抛出点就在引擎里）
/// 与 <c>ArgumentOutOfRangeException</c>「workerId 必须在 [0,1023]」（<c>AtomicIdGenerator</c> 构造守卫）。
/// ⇒ 与 java 同病，第 2 条（契约族）<b>不够用</b>，五条判据全要；见
/// <see cref="RealEngineThrowWithNonContractTypeIsNotForeign"/>（裸 ISE 腿的活证）。
/// 后一条（ArgumentOutOfRangeException）的取舍单独记在
/// <see cref="ArgumentValidationSubtypesAreForeignButTheBaseTypeIsNot"/>——它只在装配期触发、
/// 从 <c>FlowAsync</c> 不可达，故按第 4 条收进族里，代价写在明处。</para>
/// </summary>
public class FacadeInternalErrorNoLeak137Tests
{
    /// <summary>八栈逐字同一串（owner 2026-10-02 第 3 问拍 A）。这里写<b>字面量</b>而不是引用
    /// <c>JeeflowFacade.InternalFailureMsg</c>——常量本身写错字也要能报红。</summary>
    private const string FixedText = "流程处理失败";

    private const int ErrCode = 99999999;

    /// <summary>引擎里的栈帧（第 5 条判"在引擎内"的那一档；.NET 栈帧序＝内层→外层，同 java）。</summary>
    private const string EngineTrace =
        "   at Mldong.Jeeflow.Core.Engine.JeeflowEngine.ExecuteProcessTaskAsync(Int64 taskId, String op)\n"
        + "   at Mldong.Jeeflow.Facade.JeeflowFacade.ExecuteAsync(FlowData args)";

    /// <summary>集成方 provider 的栈帧（第 5 条判"不在引擎内"的那一档）。</summary>
    private const string ForeignTrace =
        "   at Acme.Flow.MyUserProvider.GetUserAsync(String userId)\n"
        + "   at Mldong.Jeeflow.Facade.JeeflowFacade.ExecuteAsync(FlowData args)";

    private readonly MemoryRepository _repo = new();
    private readonly ServiceContext _ctx;
    private readonly JeeflowEngine _engine;
    private readonly JeeflowFacade _facade;

    /// <summary>副作用取证：门面 <c>LogInternalFailure</c> 递交出来的「日志行 ＋ 带 inner 的包装件」。</summary>
    private readonly List<(string Line, Exception Wrapped)> _logs = new();

    private static int _defineSeq;

    public FacadeInternalErrorNoLeak137Tests()
    {
        // 不传扩展仓储 ⇒ 顺带能测「未配置 IProcessExtRepository（扩展仓储）」那条引擎裸文案
        _ctx = new ServiceContext(_repo);
        _ctx.Clock = new FixedClock(new DateTime(2026, 10, 2, 9, 0, 0));
        _ctx.IdGenerator = new AtomicIdGenerator(1, _ctx.Clock);
        _ctx.UserProvider = new TestUserProvider();
        _ctx.UserSearchProvider = new TestUserSearchProvider();
        TestInfra.RegisterBuiltins(_ctx);
        _repo.Configure(_ctx);
        _engine = new JeeflowEngine(_ctx);
        _facade = new JeeflowFacade(_ctx);
        _facade.InternalFailureSinkForTest = (line, wrapped) => _logs.Add((line, wrapped));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // A. 判别式五条（纯函数：文案判据那一半，各自可测、不碰异常对象、无副作用）
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>第 1 条：没有可用文案 ⇒ 内部。喂引擎栈帧，确保结论只可能来自第 1 条。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rule1_BlankMessageIsForeign(string? message) =>
        Assert.True(JeeflowFacade.IsForeignDetail(typeof(Exception), message, null, EngineTrace));

    /// <summary>
    /// 第 1 条的 .NET 专属档：<c>Exception.Message</c> 永不为 null，无 message 时框架兜底文案
    /// <b>带着类型全名</b>（java <c>message == null</c> ＋ 旧 <c>e.ToString()</c> 兜底的对偶）。
    /// 这一档不判，旧形状那句"死码" <c>?? e.ToString()</c> 想防的泄漏在 .NET 里换个姿势照样漏。
    /// </summary>
    [Fact]
    public void Rule1_FrameworkFallbackMessageNamingTheTypeIsForeign()
    {
        var e = new Exception();                       // 无 message
        Assert.Contains("System.Exception", e.Message); // 夹具前提：兜底文案确实吐类型全名
        Assert.True(JeeflowFacade.IsForeignDetail(e.GetType(), e.Message, e.InnerException, EngineTrace));
        Assert.True(JeeflowFacade.IsForeignDetail(e));  // 抽取层同结论

        var npe = new NullReferenceException();
        Assert.True(JeeflowFacade.IsForeignDetail(npe));
    }

    /// <summary>
    /// 第 2 条：契约异常族（<see cref="JeeflowException"/> 及其子类）⇒ <b>逐字透出</b>（返回 false）。
    /// 故意喂"外来栈帧"与"内部味很重的文案"，证明本条排在第 4/5 条之前（顺序即优先级）。
    /// </summary>
    [Fact]
    public void Rule2_ContractExceptionFamilyAlwaysPassesThrough()
    {
        Assert.False(JeeflowFacade.IsForeignDetail(typeof(JeeflowException), "operator 必填", null, ForeignTrace));
        Assert.False(JeeflowFacade.IsForeignDetail(typeof(JeeflowException), "任务不存在", null, null));
        Assert.False(JeeflowFacade.IsForeignDetail(typeof(SubContractException), "至少需保留一名参与人", null, ForeignTrace));
        // 第 1 条排在第 2 条之前：契约族但文案空白 ⇒ 仍判内部（空白文案透出去等于没给原因）
        Assert.True(JeeflowFacade.IsForeignDetail(typeof(JeeflowException), "  ", null, EngineTrace));
    }

    /// <summary>契约异常子类（验证第 2 条按"族"判，不是按精确类型判）。</summary>
    private sealed class SubContractException : JeeflowException
    {
        public SubContractException(string message) : base(message) { }
    }

    /// <summary>
    /// 第 3 条：裸包装（message 只是把下层原文搬上来）⇒ 内部。java 对偶＝
    /// <c>message.equals(String.valueOf(cause))</c>；.NET 的 <c>cause.ToString()</c> 还拖着栈，
    /// 故三种写法都认。<b>反面对照</b>：引擎自己写的文案 ＋ 带 cause ⇒ 不算裸包装（判据不恒真）——
    /// 这正是 <c>ModelParser</c>「读取流程定义 JSON 失败」＋ <c>DynamicTableWriter</c>「读取表结构失败」
    /// 改后的形状（契约文案进 message、原文进 InnerException）。
    /// </summary>
    [Fact]
    public void Rule3_BareWrapperIsForeignButOwnTextWithCauseIsNot()
    {
        var cause = new InvalidOperationException("内部驱动细节 12345");
        var byMessage = cause.Message;
        var byStringOf = cause.GetType().FullName + ": " + cause.Message;   // java String.valueOf(cause) 的对偶
        var byToString = cause.ToString();

        Assert.True(JeeflowFacade.IsForeignDetail(typeof(Exception), byMessage, cause, EngineTrace));
        Assert.True(JeeflowFacade.IsForeignDetail(typeof(Exception), byStringOf, cause, EngineTrace));
        Assert.True(JeeflowFacade.IsForeignDetail(typeof(Exception), byToString, cause, EngineTrace));

        Assert.False(JeeflowFacade.IsForeignDetail(typeof(Exception), "读取流程定义 JSON 失败", cause, EngineTrace));
        Assert.False(JeeflowFacade.IsForeignDetail(typeof(Exception), "读取表结构失败: biz_demo", cause, EngineTrace));
    }

    /// <summary>
    /// 第 4 条：运行时／反射／IO／驱动／JSON 解析器自己抛的族 ⇒ 内部。
    /// 每条都喂"引擎栈帧 ＋ 非空文案 ＋ 无 cause"，确保结论只可能来自第 4 条
    /// （第 4 条排在第 5 条之前 ⇒ 引擎自己调出来的运行时异常一样不外透）。
    /// <see cref="StackOverflowException"/> 在 .NET 实际捕不到（进程直接死），列上是为一族齐；
    /// <c>MySqlException</c> 的构造函数是 internal、测试里造不出实例 ⇒ 按 <see cref="Type"/> 判。
    /// </summary>
    [Theory]
    [MemberData(nameof(RuntimeFamily))]
    public void Rule4_RuntimeFamilyIsForeignEvenFromEngineFrame(Type type) =>
        Assert.True(JeeflowFacade.IsForeignDetail(type, "内部驱动细节 12345", null, EngineTrace));

    public static TheoryData<Type> RuntimeFamily => new()
    {
        // 空引用／转换／格式化／溢出／越界／算术
        typeof(NullReferenceException),
        typeof(InvalidCastException),
        typeof(FormatException),
        typeof(OverflowException),
        typeof(ArithmeticException),
        typeof(DivideByZeroException),
        typeof(IndexOutOfRangeException),
        typeof(ArgumentOutOfRangeException),
        // 进程级致命错
        typeof(StackOverflowException),
        typeof(OutOfMemoryException),
        typeof(InsufficientMemoryException),
        // 反射（java ReflectiveOperationException 族；.NET 无单一基类 ⇒ 逐个点名 ＋ 命名空间兜底）
        typeof(System.Reflection.TargetInvocationException),
        typeof(System.Reflection.ReflectionTypeLoadException),
        typeof(System.Reflection.TargetParameterCountException),
        typeof(System.Reflection.AmbiguousMatchException),
        // 程序集加载（java LinkageError 族）
        typeof(TypeLoadException),
        typeof(TypeInitializationException),
        typeof(MissingMethodException),
        typeof(MissingFieldException),
        typeof(BadImageFormatException),
        // IO／网络
        typeof(System.IO.IOException),
        typeof(System.IO.FileNotFoundException),
        typeof(System.IO.DirectoryNotFoundException),
        typeof(System.IO.EndOfStreamException),
        typeof(System.Net.Sockets.SocketException),
        // 驱动（java SQLException 族）
        typeof(DbException),
        typeof(MySqlConnector.MySqlException),
        typeof(FakeDriverException),
        // JSON 解析器
        typeof(JsonException),
        // 运行时包装（java UndeclaredThrowableException 的对偶）
        typeof(AggregateException),
    };

    /// <summary>驱动异常桩：<see cref="DbException"/> 的构造函数是 protected，只能派生才能造实例。</summary>
    private sealed class FakeDriverException : DbException
    {
        public FakeDriverException(string message) : base(message) { }
    }

    /// <summary>
    /// 真驱动类型确实在第 4 条的族里：<c>MySqlConnector.MySqlException : DbException</c>，
    /// 其原文（SQLSTATE／Access denied／表不存在）一律不外透。
    /// </summary>
    [Fact]
    public void DriverExceptionFamilyIsCovered()
    {
        Assert.True(typeof(DbException).IsAssignableFrom(typeof(MySqlConnector.MySqlException)));
        Assert.True(JeeflowFacade.IsForeignDetail(typeof(MySqlConnector.MySqlException),
            "SQLSTATE[42S02]: Base table or view not found: 1146 Table 'jeeflow.biz_137_3_1' doesn't exist",
            null, EngineTrace));
        Assert.True(JeeflowFacade.IsForeignDetail(typeof(MySqlConnector.MySqlException),
            "Access denied for user 'root'@'192.168.1.160' (using password: YES)", null, ForeignTrace));
    }

    /// <summary>
    /// 第 4 条的<b>排除面</b>（spec §2.12 那条 ⚠️）：引擎拿来当契约文案载体的类型不得进这一族。
    /// java 排除 <c>RuntimeException</c>/<c>ISE</c>/<c>IAE</c>，本栈对偶＝
    /// <see cref="InvalidOperationException"/>（门面 <c>PageResultOut</c> 的「未支持的分页行类型: X」，
    /// 抛出点就在引擎里、文案是引擎写的中文 ⇒ 必须逐字透出）与 <c>ArgumentException</c> <b>基类</b>。
    /// 把它们收进族里，就等于静默改写契约面。
    /// </summary>
    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(NotImplementedException))]
    [InlineData(typeof(Exception))]
    public void ContractCarrierTypesAreNotInTheRuntimeFamily(Type type) =>
        Assert.False(JeeflowFacade.IsForeignDetail(type, "未支持的分页行类型: DefineRow", null, EngineTrace));

    /// <summary>
    /// 本栈相对 java 清单的<b>唯一一处加严</b>（按普查结论定，不照抄 java）：
    /// <c>ArgumentException</c> 的两个具体子型 <see cref="ArgumentNullException"/>／
    /// <see cref="ArgumentOutOfRangeException"/> 进族，基类不进。
    ///
    /// <para>依据＝<c>src/</c> 41 处抛出点的普查：ArgumentNullException 4 处全是
    /// <c>?? throw new ArgumentNullException(nameof(x))</c> 构造守卫（文案由框架生成、英文、
    /// 装配期触发，从 <c>FlowAsync</c> 不可达），ArgumentOutOfRangeException 1 处是
    /// <c>AtomicIdGenerator</c> 的 workerId 守卫（同样只在装配期触发）。两者都<b>不是</b>契约文案载体
    /// ——这正是 java 把整个 IAE 族排除在外的理由在本栈不成立的地方；而 BCL 自己抛出来的这两型
    /// 文案永远是内部细节（<c>Value cannot be null. (Parameter 'x')</c>），按 §2.12 第 5 条的原则
    /// 「引擎没写过的文案一律不外透」，交给第 4 条兜住更稳（BCL 的 ThrowHelper 带
    /// <c>[StackTraceHidden]</c>，栈顶帧归属对这一族可能判不出来）。</para>
    /// </summary>
    [Fact]
    public void ArgumentValidationSubtypesAreForeignButTheBaseTypeIsNot()
    {
        Assert.True(JeeflowFacade.IsForeignDetail(typeof(ArgumentNullException),
            "Value cannot be null. (Parameter 'content')", null, EngineTrace));
        Assert.True(JeeflowFacade.IsForeignDetail(typeof(ArgumentOutOfRangeException),
            "Specified argument was out of the range of valid values. (Parameter 'workerId')", null, EngineTrace));
        // 基类不进族：它是引擎可能用来携带中文契约文案的形状（java 同口径排除 IAE）
        Assert.False(JeeflowFacade.IsForeignDetail(typeof(ArgumentException),
            "未支持的分页行类型: DefineRow", null, EngineTrace));
        // 唯一一处中文文案（AtomicIdGenerator 构造守卫）确实会被判成内部——它从 FlowAsync 不可达，
        // 属装配期错误（集成方 new 的时候就炸），这一格把代价写在明处而不是藏着
        Assert.True(JeeflowFacade.IsForeignDetail(typeof(ArgumentOutOfRangeException),
            "workerId 必须在 [0,1023]", null, EngineTrace));
    }

    /// <summary>第 5 条：抛出点不在引擎主命名空间（BCL／集成方 provider／测试桩）⇒ 内部。</summary>
    [Fact]
    public void Rule5_ThrowPointOutsideEngineIsForeign()
    {
        const string msg = "集成方 provider 内部细节 999";
        Assert.True(JeeflowFacade.IsForeignDetail(typeof(Exception), msg, null, ForeignTrace));
        // 测试桩同 java 排除 test 包：Mldong.Jeeflow.Tests.* 抛的不算引擎契约文案
        Assert.True(JeeflowFacade.IsForeignDetail(typeof(Exception), msg, null,
            "   at Mldong.Jeeflow.Tests.FakeRepo.PageInstancesAsync(PageQuery q)"));
        // 栈为空（异常只被构造、没真抛出过）⇒ 判不出归属 ⇒ 保守判内部（与 java 同口径）
        Assert.True(JeeflowFacade.IsForeignDetail(typeof(Exception), msg, null, null));
        Assert.True(JeeflowFacade.IsForeignDetail(typeof(Exception), msg, null, ""));
        // 反面对照：抛出点在引擎里 ⇒ 逐字透出
        Assert.False(JeeflowFacade.IsForeignDetail(typeof(Exception), msg, null, EngineTrace));
    }

    /// <summary>
    /// 第 5 条的栈文本解析要经得起 .NET 真实形状：异步状态机帧（<c>&lt;X&gt;d__1.MoveNext()</c>）
    /// 与重抛插进来的 <c>--- End of stack trace from previous location ---</c> 分隔行
    /// （它排在**后面**，归属只看第一条 <c>at</c> 帧）。
    /// </summary>
    [Fact]
    public void Rule5_ReadsTheInnermostFrameOnly()
    {
        const string msg = "引擎自己写的中文文案";
        Assert.False(JeeflowFacade.IsForeignDetail(typeof(Exception), msg, null,
            "   at Mldong.Jeeflow.Core.Engine.JeeflowEngine.<ExecuteAsync>d__12.MoveNext()"));
        Assert.False(JeeflowFacade.IsForeignDetail(typeof(Exception), msg, null,
            "   at Mldong.Jeeflow.Core.ProcessTask.Complete(String op)\n"
            + "--- End of stack trace from previous location ---\n"
            + "   at Acme.Flow.Caller.Run()"));
        Assert.True(JeeflowFacade.IsForeignDetail(typeof(Exception), msg, null,
            "   at Acme.Flow.Provider.Get()\n"
            + "--- End of stack trace from previous location ---\n"
            + "   at Mldong.Jeeflow.Core.Engine.JeeflowEngine.Run()"));
    }

    /// <summary>
    /// 用<b>真异常 ＋ 真栈</b>验证第 4/5 条：门面自己的 <c>PageResultOut</c> 遇到不认的行类型时抛的是
    /// <b>裸</b> <see cref="InvalidOperationException"/> 携带引擎写的中文文案（java 基准里 <c>ext()</c>
    /// 那条裸 ISE 的本栈对偶）⇒ 必须逐字透出；收窄成"只透 JeeflowException"这一格立刻红。
    /// </summary>
    [Fact]
    public void RealEngineThrowWithNonContractTypeIsNotForeign()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => JeeflowFacade.PageResultOut(
            new PageResult<object>(1, 10, 1, new List<object> { "不是已知行类型" })));

        Assert.Equal("未支持的分页行类型: String", ex.Message);          // 夹具前提：确是引擎写的中文文案
        Assert.Contains("Mldong.Jeeflow.Facade.JeeflowFacade", ex.StackTrace); // 夹具前提：抛出点真在引擎里
        Assert.False(JeeflowFacade.IsForeignDetail(ex));
        Assert.False(JeeflowFacade.IsForeignDetail(ex.GetType(), ex.Message, ex.InnerException, ex.StackTrace));
    }

    /// <summary>用<b>真异常 ＋ 真栈</b>验证第 2 条：Core 里真抛出来的契约异常一律逐字透出。</summary>
    [Fact]
    public void RealEngineContractThrowIsNotForeign()
    {
        var ctx = new ServiceContext(new MemoryRepository());
        var ex = Assert.Throws<JeeflowException>(() => ctx.FindAssignmentHandler("com.acme.NoSuchHandler"));

        Assert.Equal("无法解析 assignmentHandler: com.acme.NoSuchHandler", ex.Message);
        Assert.Contains("Mldong.Jeeflow.Core", ex.StackTrace);
        Assert.False(JeeflowFacade.IsForeignDetail(ex));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // B. 门面出口形状（副作用那一半）：内部原文 ⇒ msg 逐字＝固定文案，原文只进日志＋InnerException
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 负向①（改前红）：运行时 NPE 原文（java 137 案实测出口 <c>Cannot invoke …</c> 的本栈对偶）
    /// ⇒ 出口只有固定文案，原文连同类型与栈进日志，包装件的 <c>InnerException</c> 就是原异常对象。
    /// </summary>
    [Fact]
    public async Task Exit_NullReferenceDetail_KeepsInternalsOutOfMsg()
    {
        var boom = new NullReferenceException(
            "Cannot invoke \"String.Length\" because the return value of \"Acme.Raw.get_Value()\" is null");
        ThrowFromJsonProvider(boom);

        var resp = await DeployObjectContentAsync();

        Assert.Equal(ErrCode, resp["code"]);
        Assert.Equal(FixedText, resp["msg"]);
        var msg = resp["msg"]!.ToString()!;
        foreach (var marker in new[] { "Cannot invoke", "get_Value", "System.", "NullReference", "Acme" })
        {
            Assert.DoesNotContain(marker, msg);
        }

        var (line, wrapped) = Assert.Single(_logs);
        Assert.Contains("processDefine/deploy", line);          // 日志要指出是哪个 action
        Assert.Equal(FixedText, wrapped.Message);               // 对外文案
        Assert.Same(boom, wrapped.InnerException);              // cause 分离：原异常对象就在 inner 里
        Assert.Contains("Cannot invoke", wrapped.ToString());   // 原文＋类型＋栈都在日志载体里
    }

    /// <summary>
    /// 负向②（改前红）：<b>真</b> <c>int.Parse</c> 抛出来的 <see cref="FormatException"/>
    /// （java 137 案实测那条 <c>For input string: "x"</c> 的本栈对偶）。
    /// </summary>
    [Fact]
    public async Task Exit_RealFormatException_KeepsInternalsOutOfMsg()
    {
        var boom = RealFormatException();
        Assert.False(string.IsNullOrWhiteSpace(boom.Message));  // 夹具前提：解析器确实写了文案
        ThrowFromJsonProvider(boom);

        var resp = await DeployObjectContentAsync();

        Assert.Equal(FixedText, resp["msg"]);
        Assert.DoesNotContain(boom.Message, resp["msg"]!.ToString()!);
        Assert.Same(boom, Assert.Single(_logs).Wrapped.InnerException);
    }

    /// <summary>
    /// 负向③（改前红）：<b>真</b> System.Text.Json 的非法 UTF-8 首字节原文
    /// （spec §2.12 点名的 <c>'0xE8' is an invalid start of a property …</c> 那一族）。
    /// 批二 <c>dca1b33</c> 修的是 <c>ModelParser</c> 那条腿（解析失败 ⇒ 契约文案「读取流程定义 JSON 失败」），
    /// 本格走的是<b>另一条腿</b>：<c>ContentBytes</c> 的 <c>Json.ToJson(content)</c> 不在任何 try/catch 里
    /// ⇒ 解析器原文直达门面顶层 catch，只有判别式能挡。
    /// </summary>
    [Fact]
    public async Task Exit_RealSystemTextJsonDetail_KeepsInternalsOutOfMsg()
    {
        var boom = RealUtf8JsonExplosion();
        Assert.Contains("0xE8", boom.Message);                  // 夹具前提：确是那条非法首字节原文
        ThrowFromJsonProvider(boom);

        var resp = await DeployObjectContentAsync();

        Assert.Equal(FixedText, resp["msg"]);
        var msg = resp["msg"]!.ToString()!;
        Assert.DoesNotContain("0xE8", msg);
        Assert.DoesNotContain("invalid start", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LineNumber", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Same(boom, Assert.Single(_logs).Wrapped.InnerException);
    }

    /// <summary>
    /// 负向④（改前红）：裸包装 <c>new Exception(下层原文, inner)</c>（第 3 条）。
    /// 出口丢掉的是<b>文案</b>不是<b>信息</b>：<c>12345</c> 仍能从日志载体的 inner 链里读到。
    /// </summary>
    [Fact]
    public async Task Exit_BareWrapper_KeepsCauseTextOutOfMsg()
    {
        var inner = new InvalidOperationException("内部驱动细节 12345");
        var bare = new Exception(inner.GetType().FullName + ": " + inner.Message, inner);
        ThrowFromJsonProvider(bare);

        var resp = await DeployObjectContentAsync();

        Assert.Equal(FixedText, resp["msg"]);
        Assert.DoesNotContain("12345", resp["msg"]!.ToString()!);
        var wrapped = Assert.Single(_logs).Wrapped;
        Assert.Same(bare, wrapped.InnerException);
        Assert.Contains("12345", wrapped.ToString());
        Assert.Contains("内部驱动细节", wrapped.ToString());
    }

    /// <summary>负向⑤（改前红）：驱动族（<see cref="DbException"/>）的 SQLSTATE 原文。</summary>
    [Fact]
    public async Task Exit_DriverSqlStateDetail_KeepsInternalsOutOfMsg()
    {
        var boom = new FakeDriverException(
            "SQLSTATE[42S02]: Base table or view not found: 1146 Table 'jeeflow.biz_137_3_1' doesn't exist");
        ThrowFromJsonProvider(boom);

        var resp = await DeployObjectContentAsync();

        Assert.Equal(FixedText, resp["msg"]);
        var msg = resp["msg"]!.ToString()!;
        Assert.DoesNotContain("SQLSTATE", msg);
        Assert.DoesNotContain("1146", msg);
        Assert.Same(boom, Assert.Single(_logs).Wrapped.InnerException);
    }

    /// <summary>
    /// 负向⑥（改前红）：第三方 provider 抛的<b>普通</b> <see cref="Exception"/>——类型不在第 4 条的族里、
    /// 文案非空、也不是裸包装，全靠第 5 条（抛出点不在引擎主命名空间）判成内部。
    /// 这一格是"不能只按类型族判"的活证。
    /// </summary>
    [Fact]
    public async Task Exit_ThirdPartyProviderPlainException_KeepsInternalsOutOfMsg()
    {
        var boom = new Exception("集成方 provider 内部细节 999");
        ThrowFromJsonProvider(boom);

        var resp = await DeployObjectContentAsync();

        Assert.Equal(FixedText, resp["msg"]);
        Assert.DoesNotContain("999", resp["msg"]!.ToString()!);
        Assert.Contains("processDefine/deploy", Assert.Single(_logs).Line);
    }

    /// <summary>
    /// 负向⑦（改前红）：<c>Message</c> 为空那一档——旧形状想退到 <c>e.ToString()</c>
    /// （完整类型名＋栈，比 <c>Message</c> 更严重的泄漏）。现在：出口只有固定文案，
    /// <c>ToString()</c> 那些内容（类型名／栈）只在日志载体里。
    /// </summary>
    [Fact]
    public async Task Exit_EmptyMessage_KeepsToStringFallbackOutOfMsg()
    {
        var boom = new Exception(string.Empty);
        Assert.Equal(string.Empty, boom.Message);               // 夹具前提：真的是空文案
        ThrowFromJsonProvider(boom);

        var resp = await DeployObjectContentAsync();

        Assert.Equal(FixedText, resp["msg"]);
        var msg = resp["msg"]!.ToString()!;
        Assert.DoesNotContain("System.Exception", msg);
        Assert.DoesNotContain("at Mldong", msg);
        var wrapped = Assert.Single(_logs).Wrapped;
        Assert.Contains("System.Exception", wrapped.ToString());  // 类型名＋栈进了日志，信息没丢
        Assert.Contains("at Mldong", wrapped.ToString());
    }

    /// <summary>
    /// 负向⑧：门面出口不得把原文塞进<b>任何其它</b>对外字段（spec §2.12「不得拼进 msg，
    /// 也不得拼进 data／警告文本那族」）——整张响应表里都不许出现原文。
    /// </summary>
    [Fact]
    public async Task Exit_NoOtherOutboundFieldCarriesTheDetail()
    {
        var boom = new NullReferenceException("内部空引用细节 4242");
        ThrowFromJsonProvider(boom);

        var json = await _facade.FlowJsonAsync("processDefine/deploy", ObjectContentArgs());

        Assert.Contains(FixedText, json);
        Assert.DoesNotContain("4242", json);
        Assert.DoesNotContain("NullReference", json);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // C. 正向／回归：引擎自己写的中文契约文案必须仍<b>逐字</b>透出（变异②的牙）
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 契约异常族穿过顶层 catch 后仍逐字透出（java 基准
    /// <c>contractExceptionMessageStillPassesThroughVerbatim</c> 的本栈对偶）：本栈 <c>Ext()</c> 抛的是
    /// <see cref="JeeflowException"/>「未配置 IProcessExtRepository（扩展仓储）」（java 同一处抛的是裸 ISE）。
    /// 判据收窄成"一律固定文案"这一格立刻红，且引擎自己写的文案不该留下任何内部失败日志。
    /// ⚠️ 本栈"引擎抛<b>裸</b>非契约类型 ＋ 中文文案"那一档（java 基准的
    /// <c>engineIllegalStateOutsideContractTypeStillPassesThrough</c>）在 action 路径上<b>不可达</b>
    /// ——唯一一处是 <c>PageResultOut</c> 的「未支持的分页行类型: X」，行类型由内部 switch 兜死，
    /// 集成方喂不进来；故那一档改在纯函数层用<b>真异常＋真栈</b>钉住，见
    /// <see cref="RealEngineThrowWithNonContractTypeIsNotForeign"/>。
    /// </summary>
    [Fact]
    public async Task Exit_ContractExceptionTextStillPassesThrough()
    {
        var resp = await _facade.FlowAsync("processDesign/page", new FlowData { ["operator"] = "user1" });

        Assert.Equal(ErrCode, resp["code"]);
        Assert.Equal("未配置 IProcessExtRepository（扩展仓储）", resp["msg"]);
        Assert.Empty(_logs);   // 引擎自己写的文案不该被记成内部异常
    }

    /// <summary>
    /// spec 06 失败文案表里那条<b>抛出型</b>逐字文案：撤回已办结实例 ⇒ 聚合根抛
    /// <see cref="JeeflowException"/>(20010009)，穿过顶层 catch 后 msg 仍逐字、且不带码值。
    /// 批二 <c>ErrorDetailNoLeak137Tests</c> 已钉住「读取流程定义 JSON 失败」那条抛出型文案，
    /// 本格换一条不同来源（Core 聚合根）再钉一次。
    /// </summary>
    [Fact]
    public async Task Exit_WithdrawFinishedInstance_KeepsVerbatimContractText()
    {
        var iid = await StartAndFinishAsync("i137-3-1-withdraw");

        var resp = await _facade.FlowAsync("processInstance/withdraw",
            new FlowData { ["id"] = iid, ["operator"] = "flow.admin" });

        Assert.Equal(ErrCode, resp["code"]);
        Assert.Equal("流程实例非进行中，无法撤回", resp["msg"]);
        Assert.DoesNotContain("20010009", resp["msg"]!.ToString()!);
        Assert.Empty(_logs);
    }

    /// <summary>
    /// spec 06 失败文案表里那几条<b>直接返回型</b>逐字文案（不经过 catch，但同属出口面）：
    /// 判别式落地不得把它们改写成固定文案。逐字断言，不用"包含 必填"这种宽松判据。
    /// </summary>
    [Fact]
    public async Task Exit_VerbatimContractTextsStillPassThrough()
    {
        var (_, applyTaskId) = await StartAsync("i137-3-1-verbatim");

        // ① operator 必填（摘人：operator 守卫排在最前）
        Assert.Equal("operator 必填", (await RemoveActorAsync(applyTaskId, Col("user1"), null))["msg"]);
        // ② 任务不存在（摘人：正数主键但库里没有这一行）
        Assert.Equal("任务不存在", (await RemoveActorAsync(987654321L, Col("user1"), "user1"))["msg"]);
        // ③ 无权限转办该任务（转办：操作人非 fromActor 且非哨兵）
        Assert.Equal("无权限转办该任务", (await TransferAsync(applyTaskId, "leader", "user1", "userB"))["msg"]);
        // ④ 至少需保留一名参与人（摘掉唯一那一票）
        Assert.Equal("至少需保留一名参与人", (await RemoveActorAsync(applyTaskId, Col("user1"), "user1"))["msg"]);

        // ⑤ 任务非进行中，不可摘除参与人：先把 apply 办结，再摘历史行
        await _engine.ExecuteProcessTaskAsync(applyTaskId, "user1", new FlowData());
        Assert.Equal("任务非进行中，不可摘除参与人",
            (await RemoveActorAsync(applyTaskId, Col("user1"), "user1"))["msg"]);

        Assert.Empty(_logs);   // 全程没有内部异常
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 夹具
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 让门面的 JSON provider 抛指定异常。走的是 <c>processDefine/deploy</c> →
    /// <c>ContentBytes</c> → <c>Json.ToJson(content)</c> 这条**真实**路径：该腿不在任何 try/catch 里，
    /// 异常直达门面顶层 catch（<c>ModelParser</c> 那条腿批二已包成契约文案，走不到判别式）。
    /// provider 桩本身在测试程序集里 ⇒ 抛出点天然是"引擎外"，与"集成方 provider 炸了"同形状。
    /// </summary>
    private void ThrowFromJsonProvider(Exception boom) =>
        _ctx.JsonProvider = new ThrowingJsonProvider(() => boom);

    private static FlowData ObjectContentArgs() => new()
    {
        ["operator"] = "user1",
        // content 为对象图 ⇒ ContentBytes 走 Json.ToJson(content) 那一腿
        ["content"] = new Dictionary<string, object?>
        {
            ["name"] = "i137-3-1",
            ["displayName"] = "出口判别式",
        },
    };

    private Task<Dictionary<string, object?>> DeployObjectContentAsync() =>
        _facade.FlowAsync("processDefine/deploy", ObjectContentArgs());

    /// <summary>真 <c>int.Parse</c> 抛出来的 FormatException（java <c>For input string: "x"</c> 的对偶）。</summary>
    private static Exception RealFormatException()
    {
        try
        {
            _ = int.Parse("x");
            throw new InvalidOperationException("夹具失效：int.Parse(\"x\") 竟然没抛");
        }
        catch (FormatException e)
        {
            return e;
        }
    }

    /// <summary>
    /// 真 System.Text.Json 原文：<c>{</c> 后直接跟非法 UTF-8 首字节 <c>0xE8</c> ⇒
    /// <c>'0xE8' is an invalid start of a property name. Expected a '"'. LineNumber: 0 | BytePositionInLine: 1.</c>
    /// </summary>
    private static Exception RealUtf8JsonExplosion()
    {
        try
        {
            using var _ = JsonDocument.Parse(new byte[] { 0x7B, 0xE8, 0x22, 0x3A, 0x31, 0x7D });
            throw new InvalidOperationException("夹具失效：这段坏字节竟然解析成功了");
        }
        catch (JsonException e)
        {
            return e;
        }
    }

    /// <summary>起一条 <c>01-simple</c> 实例（发起人 user1）⇒ 停在 apply 节点（参与者 user1）。</summary>
    private async Task<(long InstanceId, long ApplyTaskId)> StartAsync(string name)
    {
        var did = await TestInfra.SaveFlowDefineAsync(_repo, name + "-" + _defineSeq++,
            TestInfra.LoadFlow("01-simple"));
        var inst = await _engine.StartProcessInstanceByIdAsync(did, "user1", new FlowData());
        var apply = await TestInfra.FindDoingForAsync(_repo, inst.InstanceId!.Value, "user1");
        return (inst.InstanceId!.Value, apply.TaskId!.Value);
    }

    /// <summary>再把 leader 那一步办结 ⇒ 实例自然到 state=20（不用 setState 造假形状）。</summary>
    private async Task<long> StartAndFinishAsync(string name)
    {
        var (iid, _) = await StartAsync(name);
        await _engine.ExecuteProcessTaskAsync(
            (await TestInfra.FindDoingForAsync(_repo, iid, "user1")).TaskId!.Value, "user1", new FlowData());
        var leader = await TestInfra.FindDoingForAsync(_repo, iid, "leader");
        var resp = await _facade.FlowAsync("processTask/execute",
            new FlowData { [FlowConst.ProcessTaskIdKey] = leader.TaskId, ["operator"] = "leader" });
        Assert.True(Equals(0, resp["code"]), $"办结应成功: {resp["msg"]}");
        var inst = await _repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Finished, inst!.State);   // 夹具前提：实例已办结
        return iid;
    }

    private Task<Dictionary<string, object?>> RemoveActorAsync(object? taskId, object? actorIds, object? op) =>
        _facade.FlowAsync("processTask/removeTaskActor", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = taskId,
            ["actorIds"] = actorIds,
            ["operator"] = op,
        });

    private Task<Dictionary<string, object?>> TransferAsync(
        object? taskId, string op, string fromActor, string toActor) =>
        _facade.FlowAsync("processTask/transfer", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = taskId,
            ["operator"] = op,
            ["fromActor"] = fromActor,
            ["toActor"] = toActor,
        });

    private static List<object?> Col(params object?[] items) => new(items);

    /// <summary>集成方 JSON provider 桩：<c>ToJson</c> 那一腿抛指定异常，其余照旧委托默认实现。</summary>
    private sealed class ThrowingJsonProvider : IJsonProvider
    {
        private readonly Func<Exception> _make;

        public ThrowingJsonProvider(Func<Exception> make) => _make = make;

        public string ToJson(object? value) => throw _make();

        public object? FromJson(string json) => DefaultJsonProvider.Instance.FromJson(json);

        public bool IsJson(string json) => DefaultJsonProvider.Instance.IsJson(json);
    }
}

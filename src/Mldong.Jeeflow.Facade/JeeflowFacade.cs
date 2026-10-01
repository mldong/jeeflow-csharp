using System.Text;
using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Facade;

/// <summary>
/// 统一门面（对齐 Java JeeflowFacade，40+ action）："接口即 POST + JSON body"风格单入口。
/// 恒 {code,msg,data} 信封；成功 code=0；失败只发明 99999999（不发明 9999xxxx）；
/// unknown/action 兜底同码。出口纪律（C1/C2/C5/C7/C22 + CS1/CS2/CS4）由 <see cref="Outbound"/>
/// 统一递归 stringifier 强制：id 全字符串化（含复数数组）、时间 yyyy-MM-dd HH:mm:ss、
/// 分页恒五键、统计计数 int 出参（issues/105）。
/// </summary>
public partial class JeeflowFacade
{
    private const int OkCode = 0;
    private const int ErrCode = 99999999;

    /// <summary>
    /// issues/137 §3-1（spec 06-facade.md §2.12）：门面捕获到**非引擎契约异常**时，对外只说这一句。
    /// owner 2026-10-02 第 3 问拍 A ⇒ 第八条逐字契约文本，**八栈同一串、不许改措辞**
    /// （java <c>INTERNAL_FAILURE_MSG</c>／php <c>INTERNAL_FAILURE_MSG</c>／python
    /// <c>INTERNAL_FAILURE_MSG</c>／node <c>INTERNAL_FAILURE_MSG</c> 同名同值，可跨栈 grep 互查）。
    /// </summary>
    internal const string InternalFailureMsg = "流程处理失败";

    /// <summary>引擎主命名空间前缀（判别式第 5 条「抛出点在不在引擎里」用；对齐 java <c>com.mldong.jeeflow.</c>）。</summary>
    private const string EngineNamespacePrefix = "Mldong.Jeeflow.";

    /// <summary>测试桩命名空间前缀（判别式第 5 条排除项；对齐 java 排除 <c>com.mldong.jeeflow.test.</c>）。</summary>
    private const string TestNamespacePrefix = "Mldong.Jeeflow.Tests.";

    private static readonly List<int> DefaultStateIn = new() { 10, 20, 30, 40, 45, 50 };
    private static readonly int DefaultStatsLimit = 10;
    private static readonly HashSet<string> ValidGranularity = new() { "hour", "day", "week", "month" };
    private static readonly HashSet<string> ValidDimension = new()
    {
        "state", "define", "category", "approver", "applicant",
        "node", "stuckNode", "stuckApprover", "durationBucket",
    };

    private readonly JeeflowEngine _engine;
    private readonly IProcessRepository _repository;
    private readonly IProcessExtRepository? _extRepository;
    private readonly ServiceContext _context;

    public JeeflowFacade(ServiceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _engine = new JeeflowEngine(context);
        _repository = context.Repository;
        _extRepository = context.ExtRepository;
    }

    public JeeflowEngine Engine => _engine;
    public ServiceContext Context => _context;

    private IClock Clock => _context.ClockOrDefault;
    private IJsonProvider Json => _context.JsonProviderOrDefault;

    /// <summary>统一入口（对象图信封；出口已按契约转换）。</summary>
    public async Task<Dictionary<string, object?>> FlowAsync(string? action, FlowData args)
    {
        if (args == null) args = new FlowData();
        try
        {
            var data = action switch
            {
                // ── 流程定义 ──
                "processDefine/page" => await DefinePageAsync(args),
                "processDefine/detail" => await DefineDetailAsync(args),
                "processDefine/startAndExecute" => await StartAndExecuteAsync(args),
                "processDefine/deploy" => await DeployAsync(args),
                "processDefine/redeploy" => await RedeployAsync(args),
                "processDefine/remove" => await DefineRemoveAsync(args),
                "processDefine/upAndDown" => await DefineUpAndDownAsync(args),
                // ── 流程实例 ──
                "processInstance/page" => await InstancePageAsync(args),
                "processInstance/detail" => await InstanceDetailAsync(args),
                "processInstance/startAndExecute" => await StartAndExecuteAsync(args),
                "processInstance/withdraw" => await WithdrawAsync(args),
                "processInstance/bizData" => await BizDataAsync(args),
                // ── 流程任务 ──
                "processTask/todoList" => await TodoListAsync(args),
                "processTask/doneList" => await DoneListAsync(args),
                "processTask/execute" => await ExecuteAsync(args),
                "processTask/detail" => await TaskDetailAsync(args),
                "processTask/jumpAbleTaskNameList" => await JumpAbleTaskNameListAsync(args),
                "processTask/candidatePage" => await CandidatePageAsync(args),
                "processTask/surrogate" => await TaskSurrogateAsync(args),
                "processTask/addCandidate" => await TaskSurrogateAsync(args),
                "processTask/transfer" => await TaskTransferAsync(args),
                "processTask/removeTaskActor" => await TaskRemoveActorAsync(args),  // issues/115 第 47 个 action
                "processTask/latest" => await TaskLatestAsync(args),
                // ── 流程设计 ──
                "processDesign/page" => await DesignPageAsync(args),
                "processDesign/detail" => await DesignDetailAsync(args),
                "processDesign/save" => await DesignSaveAsync(args),
                "processDesign/update" => await DesignUpdateAsync(args),
                "processDesign/updateDefine" => await DesignUpdateDefineAsync(args),
                "processDesign/remove" => await DesignRemoveAsync(args),
                "processDesign/deploy" => await DesignDeployAsync(args),
                "processDesign/redeploy" => await DesignRedeployAsync(args),
                "processDesign/listByType" => await DesignListByTypeAsync(args),
                // ── 视图端点 ──
                "processDefine/getLastByName" => await GetLastByNameAsync(args),
                "processInstance/highLight" => await HighLightAsync(args),
                "processInstance/approvalRecord" => await ApprovalRecordAsync(args),
                "processInstance/getAssigneeTextData" => await GetAssigneeTextDataAsync(args),
                "processInstance/createCCInstance" => await CreateCcInstanceAsync(args),
                "processInstance/updateCCStatus" => await UpdateCcStatusAsync(args),
                "processInstance/ccList" => await CcListAsync(args),
                // ── 委托代理 ──
                "processSurrogate/page" => await SurrogatePageAsync(args),
                "processSurrogate/save" => await SurrogateSaveAsync(args),
                "processSurrogate/update" => await SurrogateUpdateAsync(args),
                "processSurrogate/detail" => await SurrogateDetailAsync(args),
                "processSurrogate/remove" => await SurrogateRemoveAsync(args),
                // ── 统计 ──
                "processInstance/stats/overview" => await StatsOverviewAsync(args),
                "processInstance/stats/trend" => await StatsTrendAsync(args),
                "processInstance/stats/group" => await StatsGroupAsync(args),
                _ => Error($"未知 action: {action}"),
            };
            return data;
        }
        catch (Exception e)
        {
            // issues/137 §3-1（spec 06-facade.md §2.12）：判别规则与理由见 IsForeignDetail——引擎自己写的
            // 中文契约文案照旧**逐字**透出（其余七栈、十三个集成壳与前端 toast 都按原文对齐，在这条上收窄
            // 就是静默改契约面），只把运行时／反射／IO／驱动／JSON 解析器／集成方 provider 写的原文换成
            // 固定文案 InternalFailureMsg。原文只进**日志与 InnerException**（cause 分离，对齐 java
            // `log.log(Level.SEVERE, …, e)` 与 php `logInternalFailure(…, new JeeflowException(msg, code, $e))`），
            // 不得拼进 msg 或任何其它对外字段。
            //
            // 旧形状 `return Error(e.Message ?? e.ToString());` 是本栈主泄漏点（启动词 §2-B 点名那条）：
            // 任何异常的 Message 都原样外透。⚠️ 且 `?? e.ToString()` 这一腿在 .NET 里其实是**死码**——
            // `Exception.Message` 永不为 null（无 message 时框架回落成"Exception of type 'X' was thrown."／
            // 本地化"发生类型为“X”的异常。"），所以真正会外透的兜底文案**本身就带着类型全名**；
            // 判别式第 1 条因此除了判空白，还要判"文案里带自己类型全名"这一档（java `message == null` 的对偶）。
            if (IsForeignDetail(e))
            {
                LogInternalFailure(action, new Exception(InternalFailureMsg, e));
                return Error(InternalFailureMsg);
            }
            return Error(e.Message);
        }
    }

    // ═══ 内部异常出口判别式（issues/137 §3-1 · spec 06-facade.md §2.12）═══

    /// <summary>
    /// 判「这条异常的 message 能不能原样进出口 <c>msg</c>」——**纯静态函数**，四个入参全是已经抽好的值
    /// （类型／文案／cause／栈文本），不碰异常对象、不产生副作用 ⇒「文案判据」与「记日志那一半副作用」
    /// （<see cref="LogInternalFailure"/>）可以各自单测（spec §2.12「判据形状」硬要求）。
    /// 参考实现＝java <c>JeeflowFacade.isForeignDetail(type, message, cause, trace)</c>；
    /// 名字按 .NET PascalCase 直译（<c>isForeignDetail</c> ↔ <c>IsForeignDetail</c>，八栈可 grep 互查）。
    ///
    /// <para><b>不能简单收窄成「只透 <see cref="JeeflowException"/>」</b>——本栈 2026-10-02 现读普查
    /// （<c>src/</c> 下全部 41 处抛出点，按异常类型分类）：35 处走 <see cref="JeeflowException"/>
    /// （本栈**有**专门契约异常类型，<c>Core/Error/JeeflowException.cs</c>），但另有**裸异常腿**携带
    /// 引擎自己写的中文文案：<see cref="InvalidOperationException"/>「未支持的分页行类型: X」
    /// （本文件 <c>PageResultOut</c>）与 <see cref="ArgumentOutOfRangeException"/>
    /// 「workerId 必须在 [0,1023]」（<c>AtomicIdGenerator</c> 构造守卫）。
    /// 前者就在门面程序集里、抛出点是引擎 ⇒ 与 java 的 <c>ext()</c> 裸 ISE 同形状，收窄成"只透契约类型"
    /// 会把它改写成固定文案而没人报警（后者只在装配期触发、从 <c>FlowAsync</c> 不可达，其取舍见
    /// <see cref="RuntimeInternal"/> 的注释与测试 <c>ArgumentValidationSubtypesAreForeignButTheBaseTypeIsNot</c>）。
    /// 所以按「这段文案是谁写的」判，五条（顺序即优先级，
    /// 返回 <c>true</c> ⇒ 属内部信息 ⇒ 出口只给 <see cref="InternalFailureMsg"/>）：</para>
    /// <list type="number">
    ///   <item><description><c>message</c> 为 null／空白，<b>或</b>是框架兜底那句带类型全名的文案
    ///     ⇒ 内部（java 第 1 条 <c>message == null</c> 的 .NET 对偶：本栈 <c>Message</c> 永不为 null，
    ///     兜底会吐 <c>System.XxxException</c> 全名，一样是内部信息）；</description></item>
    ///   <item><description>属**契约异常族**（<see cref="JeeflowException"/> 及其子类）⇒ 引擎自己写的
    ///     契约文案，<b>逐字透出</b>（本条返回 <c>false</c>）；</description></item>
    ///   <item><description><c>message</c> 恰等于 cause 的原文／字符串化 ⇒ 内部（裸包装只是搬运下层原文，
    ///     引擎没写过它。java 对偶＝<c>message.equals(String.valueOf(cause))</c>；.NET 的
    ///     <c>cause.ToString()</c> 还拖着栈，故三种写法都认：<c>cause.Message</c>、
    ///     <c>"{FullName}: {Message}"</c>、<c>cause.ToString()</c>）；</description></item>
    ///   <item><description>属运行时／反射／IO／驱动／JSON 解析器自己抛的族 ⇒ 内部
    ///     （见 <see cref="RuntimeInternal"/>）；</description></item>
    ///   <item><description>抛出点不在引擎主命名空间（BCL、集成方 provider、测试桩）⇒ 内部
    ///     （见 <see cref="ThrownInsideEngine"/>；引擎没写过的文案一律不外透）。</description></item>
    /// </list>
    /// </summary>
    /// <param name="type">异常类型（<c>e.GetType()</c>）</param>
    /// <param name="message">异常文案（<c>e.Message</c>；.NET 下不为 null，但可为空白）</param>
    /// <param name="cause">下层异常（<c>e.InnerException</c>，可为 null）</param>
    /// <param name="stackTrace">栈文本（<c>e.StackTrace</c>，可为 null／空——未真抛出过时即空）</param>
    /// <returns>true ⇒ 属内部信息，出口只给固定文案</returns>
    internal static bool IsForeignDetail(Type type, string? message, Exception? cause, string? stackTrace)
    {
        // ① 没有可用文案，或文案就是"吐类型名"的框架兜底
        if (string.IsNullOrWhiteSpace(message))
        {
            return true;
        }
        if (type.FullName is { Length: > 0 } fullName && message!.IndexOf(fullName, StringComparison.Ordinal) >= 0)
        {
            return true;
        }

        // ② 契约异常族 ⇒ 逐字透出
        if (typeof(JeeflowException).IsAssignableFrom(type))
        {
            return false;
        }

        // ③ 裸包装：message 只是把下层原文搬上来
        if (cause is not null)
        {
            var causeText = cause.Message;
            var causeString = cause.GetType().FullName + ": " + causeText;   // java String.valueOf(cause) 的对偶
            if (message == causeText || message == causeString || message == cause.ToString())
            {
                return true;
            }
        }

        // ④ 运行时／反射／IO／驱动／解析器自己抛的族
        if (RuntimeInternal(type))
        {
            return true;
        }

        // ⑤ 抛出点不在引擎主命名空间
        return !ThrownInsideEngine(stackTrace);
    }

    /// <summary>
    /// 抽取层（**零判据逻辑**，对齐 python <c>foreign_detail_of</c>）：把活异常对象拆成四元组喂给
    /// <see cref="IsForeignDetail(Type, string?, Exception?, string?)"/>。.NET 没有 java
    /// <c>StackTraceElement[]</c> 那种结构化栈帧，第 5 条按 <c>e.StackTrace</c> 文本判归属
    /// （<c>e.TargetSite</c> 在异步/Release 内联下不可靠，故不用它）。
    /// </summary>
    internal static bool IsForeignDetail(Exception e) =>
        e is null || IsForeignDetail(e.GetType(), e.Message, e.InnerException, e.StackTrace);

    /// <summary>
    /// 由运行时／反射层／IO 层／驱动／JSON 解析器构造的异常族（其 message 一律是内部信息）——
    /// java <c>jvmInternal</c> 的本栈等价类型，按 spec §2.12 第 4 条族清单逐条对齐。
    /// ⚠️ 引擎拿来当**契约文案载体**的类型不在此族：<see cref="InvalidOperationException"/>
    /// （门面 <c>PageResultOut</c> 的「未支持的分页行类型: X」）与 <c>ArgumentException</c> <b>基类</b>
    /// （java 同样把 <c>RuntimeException</c>/<c>ISE</c>/<c>IAE</c> 排除在外）。
    /// 但它的两个**具体子类型**（<see cref="ArgumentNullException"/>／
    /// <see cref="ArgumentOutOfRangeException"/>）在族里——见下面那一行的理由。
    /// </summary>
    private static bool RuntimeInternal(Type type) =>
        // 空引用／类型转换／格式化／越界／算术（java NPE、ClassCastException、NumberFormatException、
        // IndexOutOfBounds、ArithmeticException 的对偶；OverflowException 与 DivideByZeroException
        // 都继承 ArithmeticException ⇒ 一并收在这一条）
        typeof(NullReferenceException).IsAssignableFrom(type)
        || typeof(InvalidCastException).IsAssignableFrom(type)
        || typeof(FormatException).IsAssignableFrom(type)
        || typeof(ArithmeticException).IsAssignableFrom(type)
        || typeof(IndexOutOfRangeException).IsAssignableFrom(type)
        // 参数校验两子型进族、ArgumentException 基类不进——按**本栈普查结论**定，不照抄 java 清单
        // （java 排除整个 IAE 族是因为它有约十处拿 IAE/ISE 携带中文契约文案；本栈 41 处抛出点里
        //  ArgumentNullException 4 处全是 `?? throw new ArgumentNullException(nameof(x))` 构造守卫、
        //  ArgumentOutOfRangeException 1 处是 AtomicIdGenerator 的 workerId 守卫，两者的文案
        //  都由框架生成或只在**装配期**触发，从 FlowAsync 不可达）⇒ 它们从来不是契约文案载体，
        //  而 BCL 自己抛出来的这两型文案永远是英文内部细节（"Value cannot be null. (Parameter 'x')"），
        //  按 §2.12 第 5 条的原则「引擎没写过的文案一律不外透」，交给第 4 条兜住更稳
        //  （BCL 的 ThrowHelper 有 [StackTraceHidden]，第 5 条的栈顶帧归属对这一族可能失效）。
        || typeof(ArgumentNullException).IsAssignableFrom(type)
        || typeof(ArgumentOutOfRangeException).IsAssignableFrom(type)
        // 进程级致命错（java StackOverflowError／VirtualMachineError 的对偶；StackOverflow 在 .NET
        // 实际捕不到——进程直接死，列上是为"一族齐"，OutOfMemory 捕得到）
        || typeof(StackOverflowException).IsAssignableFrom(type)
        || typeof(OutOfMemoryException).IsAssignableFrom(type)
        || typeof(InsufficientMemoryException).IsAssignableFrom(type)
        // 程序集加载／反射（java LinkageError／ReflectiveOperationException 的对偶。.NET 没有单一
        // 反射基类 ⇒ 逐个点名 ＋ 兜整个 System.Reflection 命名空间）
        || typeof(TypeLoadException).IsAssignableFrom(type)              // 含 EntryPointNotFoundException
        || typeof(TypeInitializationException).IsAssignableFrom(type)
        || typeof(MissingMemberException).IsAssignableFrom(type)         // 含 MissingMethod/MissingFieldException
        || typeof(BadImageFormatException).IsAssignableFrom(type)
        || typeof(System.Reflection.TargetInvocationException).IsAssignableFrom(type)
        || typeof(System.Reflection.ReflectionTypeLoadException).IsAssignableFrom(type)
        || typeof(System.Reflection.TargetParameterCountException).IsAssignableFrom(type)
        || typeof(System.Reflection.AmbiguousMatchException).IsAssignableFrom(type)
        || (type.Namespace?.StartsWith("System.Reflection", StringComparison.Ordinal) ?? false)
        // IO／网络（java IOException 的对偶；含 FileNotFoundException／DirectoryNotFoundException／
        // EndOfStreamException。java 的 SocketException 继承 IOException，.NET 不继承 ⇒ 单列）
        || typeof(IOException).IsAssignableFrom(type)
        || typeof(System.Net.Sockets.SocketException).IsAssignableFrom(type)
        // 驱动（java SQLException 的对偶；MySqlConnector 的 MySqlException 继承 DbException ⇒ 一族全收，
        // 断言见 FacadeInternalErrorNoLeak137Tests.DriverExceptionFamilyIsCovered）
        || typeof(System.Data.Common.DbException).IsAssignableFrom(type)
        // JSON 解析器（spec §2.12 点名的 `'0xE8' is an invalid start of a property …` 那一族）
        || typeof(System.Text.Json.JsonException).IsAssignableFrom(type)
        // 运行时包装（java UndeclaredThrowableException 的对偶；.NET 的 AggregateException 文案里
        // 直接嵌着各 inner 的原文，同样是引擎没写过的搬运文案）
        || typeof(AggregateException).IsAssignableFrom(type);

    /// <summary>
    /// 栈顶帧是否落在引擎主命名空间（排除测试桩：<c>Mldong.Jeeflow.Tests.</c> 抛的不算引擎契约文案，
    /// 对齐 java <c>thrownInsideEngine</c> 排除 <c>com.mldong.jeeflow.test.</c>）。
    /// .NET 栈帧序与 java <c>getStackTrace()</c> 同序（内层→外层），故取第一条 <c>at</c> 帧即抛出点；
    /// 异步重抛插进来的 <c>--- End of stack trace from previous location ---</c> 分隔行在**后面**，不影响。
    /// 空／null 栈（异常只是被构造、没真抛出过）⇒ 判不出归属 ⇒ 按"不在引擎里"（与 java 同口径，偏保守）。
    /// </summary>
    private static bool ThrownInsideEngine(string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace))
        {
            return false;
        }
        foreach (var raw in stackTrace!.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var symbol = line.StartsWith("at ", StringComparison.Ordinal) ? line[3..] : line;
            var paren = symbol.IndexOf('(');          // 去掉参数列表；异步状态机形态 `<X>d__1.MoveNext` 同样成立
            if (paren >= 0) symbol = symbol[..paren];
            return symbol.StartsWith(EngineNamespacePrefix, StringComparison.Ordinal)
                && !symbol.StartsWith(TestNamespacePrefix, StringComparison.Ordinal);
        }
        return false;
    }

    /// <summary>
    /// 内部失败的**唯一副作用出口**：原文只进日志与 <paramref name="wrapped"/> 的
    /// <see cref="Exception.InnerException"/>，绝不进 <c>msg</c> 或任何其它对外字段。
    /// 生产出口＝stderr（本栈 Core/Facade 零第三方依赖、没有 ILogger，既有惯例是
    /// <c>Console.Error.WriteLine("[jeeflow] …")</c>，见 <c>ActionsExt.BizDataAsync</c>／
    /// <c>ServiceContext.LogWarning</c>／<c>SurrogateApplier</c>）。
    /// </summary>
    /// <param name="action">出事的 action（java 日志同款：<c>"jeeflow action 执行失败: action=" + action</c>）</param>
    /// <param name="wrapped">固定文案 ＋ 原异常作 inner 的包装件（<c>new Exception(InternalFailureMsg, e)</c>）；
    /// <c>ToString()</c> 里带着原文＋类型＋内外两层栈，等价 java <c>log.log(SEVERE, msg, e)</c> 的 cause 分离</param>
    private void LogInternalFailure(string? action, Exception wrapped)
    {
        var line = $"[jeeflow] ERROR action 执行失败: action={action}";
        if (InternalFailureSinkForTest is { } sink)
        {
            sink(line, wrapped);
            return;
        }
        Console.Error.WriteLine(line + Environment.NewLine + wrapped);
    }

    /// <summary>
    /// <b>internal 测试取证钩子</b>（对齐本仓 <c>ServiceContext.WarningSinkForTest</c> 的一贯姿势：
    /// 公开 API 面不因此扩成员，靠 <c>InternalsVisibleTo</c> 给测试）。设了它，
    /// <see cref="LogInternalFailure"/> 只把「日志行 ＋ 带 inner 的包装件」递交给钩子、不再打 stderr。
    /// 存在的理由＝spec §2.12 要求"文案判据与副作用各自可测"：只断言 <c>msg</c> 等于固定文案的话，
    /// "把原文整个丢掉"也能绿，判据没有牙。实例级（非静态）⇒ xunit 跨类并行零串扰。
    /// </summary>
    internal Action<string, Exception>? InternalFailureSinkForTest { get; set; }

    /// <summary>统一入口（契约 JSON 出口——经 Outbound 递归 stringifier）。</summary>
    public async Task<string> FlowJsonAsync(string? action, FlowData args) =>
        Outbound.ToJson(await FlowAsync(action, args));

    // ═══ 响应工具（boot2 CommonResult 契约）═══

    internal static Dictionary<string, object?> Ok() => Ok(null);

    internal static Dictionary<string, object?> Ok(object? data) => new()
    {
        ["code"] = OkCode,
        ["msg"] = "成功",
        ["data"] = data,
    };

    internal static Dictionary<string, object?> Error(string msg) => new()
    {
        ["code"] = ErrCode,
        ["msg"] = msg,
    };

    /// <summary>分页出口：恒五键（C22）。</summary>
    internal static Dictionary<string, object?> PageResultOut<T>(PageResult<T> page)
    {
        var rows = new List<object?>();
        foreach (var row in page.Rows)
        {
            rows.Add(row switch
            {
                IProcessRepository.TaskRow t => TaskRowToMap(t),
                IProcessRepository.InstanceRow i => InstanceRowToMap(i),
                IProcessRepository.DefineRow d => DefineRowToMap(d),
                ProcessSurrogate s => SurrogateRowToMap(s),
                ProcessDesign pd => DesignRowToMap(pd),
                Dictionary<string, object?> dict => dict, // 已转换的行（candidatePage 候选）直接透传
                _ => throw new InvalidOperationException("未支持的分页行类型: " + row?.GetType().Name),
            });
        }
        return Ok(new Dictionary<string, object?>
        {
            ["pageNum"] = page.PageNum,
            ["pageSize"] = page.PageSize,
            ["recordCount"] = page.RecordCount,
            ["totalPage"] = page.TotalPage,
            ["rows"] = rows,
        });
    }

    // ═══ 行输出转换（issues/05 字段契约 + 时间格式）═══

    internal static string? FmtTime(DateTime? t) =>
        t?.ToString("yyyy-MM-dd HH:mm:ss");

    internal static Dictionary<string, object?> InstanceRowToMap(IProcessRepository.InstanceRow r) => new()
    {
        ["id"] = r.Id,
        ["parentId"] = r.ParentId,
        ["processDefineId"] = r.ProcessDefineId,
        ["state"] = r.State,
        ["parentNodeName"] = r.ParentNodeName,
        ["businessNo"] = r.BusinessNo,
        ["operator"] = r.Operator,
        ["expireTime"] = FmtTime(r.ExpireTime),
        ["createTime"] = FmtTime(r.CreateTime),
        ["createUser"] = r.CreateUser,
        ["updateTime"] = FmtTime(r.UpdateTime),
        ["updateUser"] = r.UpdateUser,
        ["processDefineName"] = r.ProcessDefineName,
        ["processDefineDisplayName"] = r.ProcessDefineDisplayName,
        ["processDefineVersion"] = r.ProcessDefineVersion,
        ["ext"] = ParseJsonMap(r.Variable),
        ["displayName"] = r.ProcessDefineDisplayName,
        ["version"] = r.ProcessDefineVersion,
    };

    internal static Dictionary<string, object?> TaskRowToMap(IProcessRepository.TaskRow r)
    {
        var instanceExt = ParseJsonMap(r.InstanceVariable);
        var ext = ParseJsonMap(r.Variable);
        // issues/121 P1：引擎建单必写的控制键不算「任务变量非空」，否则新建任务的 ext
        // 永远不再回退实例变量（issues/82-3 既有契约）。
        if (ext.Count == 0 || (ext.Count == 1 && ext.ContainsKey("isFirstTaskNode"))) ext = instanceExt;
        return new Dictionary<string, object?>
        {
            ["id"] = r.Id,
            ["processInstanceId"] = r.ProcessInstanceId,
            ["taskName"] = r.TaskName,
            ["displayName"] = r.DisplayName,
            ["taskType"] = r.TaskType,
            ["performType"] = r.PerformType,
            ["taskState"] = r.TaskState,
            ["operator"] = r.Operator,
            ["finishTime"] = FmtTime(r.FinishTime),
            ["expireTime"] = FmtTime(r.ExpireTime),
            ["formKey"] = r.FormKey,
            ["taskParentId"] = r.TaskParentId,
            ["createTime"] = FmtTime(r.CreateTime),
            ["createUser"] = r.CreateUser,
            ["updateTime"] = FmtTime(r.UpdateTime),
            ["updateUser"] = r.UpdateUser,
            ["processDefineName"] = r.ProcessDefineName,
            ["processDefineDisplayName"] = r.ProcessDefineDisplayName,
            ["instanceCreateTime"] = FmtTime(r.InstanceCreateTime),
            ["ext"] = ext,
            ["instanceExt"] = instanceExt,
            ["version"] = r.ProcessDefineVersion,
            ["taskFormData"] = FormDataOf(ParseJsonMap(r.Variable), FlowConst.TaskFormDataPrefix),
        };
    }

    internal static Dictionary<string, object?> DefineRowToMap(IProcessRepository.DefineRow r) => new()
    {
        ["id"] = r.Id,
        ["name"] = r.Name,
        ["displayName"] = r.DisplayName,
        ["type"] = r.Type,
        ["state"] = r.State,
        ["version"] = r.Version,
        ["createTime"] = FmtTime(r.CreateTime),
        ["createUser"] = r.CreateUser,
        ["updateTime"] = FmtTime(r.UpdateTime),
        ["updateUser"] = r.UpdateUser,
    };

    internal static Dictionary<string, object?> DesignRowToMap(ProcessDesign d) => new()
    {
        ["id"] = d.Id,
        ["name"] = d.Name,
        ["displayName"] = d.DisplayName,
        ["type"] = d.Type,
        ["icon"] = d.Icon,
        ["isDeployed"] = d.IsDeployed,
        ["remark"] = d.Remark,
        ["createTime"] = FmtTime(d.CreateTime),
        ["createUser"] = d.CreateUser,
        ["updateTime"] = FmtTime(d.UpdateTime),
        ["updateUser"] = d.UpdateUser,
    };

    internal static Dictionary<string, object?> SurrogateRowToMap(ProcessSurrogate s) => new()
    {
        ["id"] = s.Id,
        ["processName"] = s.ProcessName,
        ["operator"] = s.Operator,
        ["surrogate"] = s.Surrogate,
        ["startTime"] = FmtTime(s.StartTime),
        ["endTime"] = FmtTime(s.EndTime),
        ["enabled"] = s.Enabled,
        ["createTime"] = FmtTime(s.CreateTime),
        ["createUser"] = s.CreateUser,
        ["updateTime"] = FmtTime(s.UpdateTime),
        ["updateUser"] = s.UpdateUser,
    };

    /// <summary>任务 VO（processTask/detail、taskLatest、instance detail 共用）。</summary>
    internal Dictionary<string, object?> TaskVo(ProcessTask t)
    {
        var vo = new Dictionary<string, object?>
        {
            ["id"] = t.TaskId,
            ["processInstanceId"] = t.ProcessInstanceId,
            ["taskName"] = t.TaskName,
            ["displayName"] = t.DisplayName,
            ["taskType"] = t.TaskType == null ? null : (int)t.TaskType, // C5：出口数字 code
            ["performType"] = t.PerformType == null ? null : (int)t.PerformType,
            ["taskState"] = t.TaskState,
            ["operator"] = t.ActorId,
            ["formKey"] = t.FormKey,
            ["taskParentId"] = t.ParentTaskId,
            ["taskActorIdList"] = new List<object?>(t.ActorIds),
            ["taskFormData"] = FormDataOf(t.Variables, FlowConst.TaskFormDataPrefix),
        };
        return vo;
    }

    // ═══ 内部工具 ═══

    private IProcessExtRepository Ext()
    {
        if (_extRepository == null)
            throw new JeeflowException("未配置 IProcessExtRepository（扩展仓储）");
        return _extRepository;
    }

    /// <summary>deploy 版本管理：按 name 查最新定义，存在 version+1 插新记录，否则从 0 起。</summary>
    private async Task<long> SaveDeployedDefineAsync(ProcessModel model, byte[] bytes)
    {
        var query = new PageQuery(1, 1).Add("t.name", "EQ", model.Name).Add("t.state", "GT", -1);
        var page = await _repository.PageDefinesAsync(query);
        var def = new ProcessDefine();
        var version = 0;
        if (page.Rows.Count > 0)
            version = (page.Rows[0].Version ?? 0) + 1;
        def.Name = model.Name;
        def.DisplayName = model.DisplayName;
        def.Type = model.Type;
        def.State = 1;
        def.Content = bytes;
        def.Version = version;
        await _repository.SaveDefineAsync(def);
        return def.Id!.Value;
    }

    /// <summary>content 归一：字符串/对象/顶层 JSON（issues/27）三态兼容 → UTF-8 字节。</summary>
    private byte[]? ContentBytes(FlowData args)
    {
        if (!args.TryGetValue("content", out var content) || content == null)
        {
            // issues/27：非保留字段（除 processDesignId/operator）整体序列化为 content
            var copy = new FlowData();
            foreach (var kv in args)
            {
                if (kv.Key != FlowConst.ProcessDesignIdKey && kv.Key != "operator") copy[kv.Key] = kv.Value;
            }
            if (copy.Count == 0) return null;
            content = Json.ToJson(copy);
        }
        if (content is IDictionary<string, object?> || content is System.Collections.IEnumerable and not string and not byte[])
        {
            return Encoding.UTF8.GetBytes(Json.ToJson(content)); // content 为对象：序列化
        }
        if (content is byte[] bytes) return bytes;
        return Encoding.UTF8.GetBytes(content.ToString() ?? "");
    }

    private Dictionary<string, object?>? ParseGraph(byte[]? content)
    {
        if (content == null) return null;
        try
        {
            if (Json.FromJson(Encoding.UTF8.GetString(content)) is Dictionary<string, object?> m)
                return m;
        }
        catch
        {
            // 坏 JSON：null（对齐 Java 吞异常返回 null）
        }
        return null;
    }

    /// <summary>JSON 字符串 → Map（坏 JSON 返回空 Map）。</summary>
    internal static Dictionary<string, object?> ParseJsonMap(string? json)
    {
        var result = new Dictionary<string, object?>();
        if (string.IsNullOrEmpty(json)) return result;
        try
        {
            if (DefaultJsonProvider.Instance.FromJson(json) is Dictionary<string, object?> m)
            {
                foreach (var kv in m) result[kv.Key] = kv.Value;
            }
        }
        catch
        {
            // 空 Map
        }
        return result;
    }

    /// <summary>issues/15：f_/tf_ 前缀派生——「带前缀 + 去前缀副本」。</summary>
    internal static Dictionary<string, object?> FormDataOf(IDictionary<string, object?>? vars, string prefix)
    {
        var result = new Dictionary<string, object?>();
        if (vars == null) return result;
        foreach (var kv in vars)
        {
            if (kv.Key != null && kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                result[kv.Key] = kv.Value;
                result[kv.Key[prefix.Length..]] = kv.Value;
            }
        }
        return result;
    }

    /// <summary>id 入参（C3：string/number 双收；不可解析 → null 由调用方报错；&gt;2^53 double 显式报错）。</summary>
    internal static long? ToLong(object? val)
    {
        if (val == null) return null;
        if (val is long l) return l;
        if (val is int i) return i;
        if (val is double d)
        {
            if (Math.Abs(d) > 9007199254740992.0)
                throw new JeeflowException($"id {d} 超出 float64 精确范围（2^53），请以字符串传递");
            return (long)d;
        }
        return long.TryParse(val.ToString(), out var parsed) ? parsed : null;
    }

    /// <summary>批量主键（C15/issues/95）：{ids} 优先、单 {id} 兜底；空/含非法值显式报错。</summary>
    internal static List<long> IdListArgs(FlowData args)
    {
        var ids = args.GetObj("ids");
        var result = new List<long>();
        if (ids is string)
        {
            // 字符串形态 ids：非法（契约要求 ids 为数组）——走单 id 兜底
            var v1 = ToLong(args.GetObj("id"));
            if (v1 != null) result.Add(v1.Value);
        }
        else if (ids is System.Collections.ICollection coll)
        {
            foreach (var id in coll)
            {
                var v = ToLong(id);
                if (v == null) throw new JeeflowException("id 缺失或非法");
                result.Add(v.Value);
            }
        }
        else
        {
            var v = ToLong(args.GetObj("id"));
            if (v != null) result.Add(v.Value);
        }
        if (result.Count == 0) throw new JeeflowException("id 缺失或非法");
        return result;
    }

    internal static int ToInt(object? val, int def)
    {
        if (val == null) return def;
        if (val is int i) return i;
        if (val is long l) return (int)l;
        return int.TryParse(val.ToString(), out var parsed) ? parsed : def;
    }

    internal static string? ToStr(object? val) => val?.ToString();

    internal static string ToStr(object? val, string def) => val?.ToString() ?? def;

    /// <summary>
    /// 归属/操作人入参归一化（issues/129 案 A · spec 06-facade.md:100 补句 ＋ §2.11「落库与比较取
    /// trim 后的值」）。
    ///
    /// 空串与<b>缺键同档</b>：传 ""（或全空白）视同未传，一并回落 demo 缺省 user1。
    /// 修前是 <c>ToStr(args.GetObj("operator"), "user1")</c>——只在 null 时兜缺省，显式空串
    /// 原样落进 <c>query.Add("t.operator", "EQ", "")</c>，再被仓储“空值当作没填”那句通用放行
    /// 丢掉 ⇒“我的实例/我的已办”读出<b>全库</b>（160 实测 25 行 vs user1 的 4 行，行上是别人的 operator）。
    /// 门面归一化是第一层，仓储的归属兜底是第二层（MySqlRepository.BuildWhere /
    /// MemoryRepository.ApplyConditions），两层都要在。
    /// <b>本轮补 trim</b>（issues/142 B 批 · §2.11）：写侧归属值已统一落 trim 后的串，
    /// 比较侧若还拿 <c>" user1 "</c> 去等就是两把尺子（<c>updateCCStatus</c> 的 operator 尤其——
    /// 带空格的 operator 会把已读状态静默打到"谁也匹配不上"，历史 <c>actor_id=''</c> 的脏档则再也不会被覆写）。
    /// 判据本体仍只有一枚：<see cref="PageQuery.NormalizeActorValue"/>。
    /// </summary>
    internal static string OperatorArg(FlowData args) =>
        PageQuery.NormalizeActorValue(args.GetObj("operator")) ?? "user1";

    /// <summary>系统代执行（flow.auto）/ 超级管理员（flow.admin）放行——<c>IsAllowed</c> 既有约定，
    /// 撤回（issues/114）与转办（issues/115）共用同一判据。</summary>
    internal static bool IsPrivilegedOperator(string? op) =>
        string.Equals(FlowConst.AutoId, op, StringComparison.OrdinalIgnoreCase)
        || string.Equals(FlowConst.AdminId, op, StringComparison.OrdinalIgnoreCase);

    /// <summary>时间入参：`yyyy-MM-dd HH:mm:ss` 与 ISO T 双格式（C26/issues/77）。</summary>
    internal static DateTime? ParseTime(object? val)
    {
        if (val == null) return null;
        var s = val.ToString()?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", null,
                System.Globalization.DateTimeStyles.None, out var t1)) return t1;
        if (DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.None, out var t2)) return t2;
        return null;
    }

    /// <summary>
    /// issues/128：行上 <c>isFirstTaskNode</c> 的读取口径——与 Java
    /// <c>Boolean.parseBoolean(String.valueOf(rowFirst))</c> 同语义：
    /// 布尔直取；字符串走大小写不敏感的 "true"；其它（"1"、脏值）一律 false。
    /// 不做"非空即真"，否则脏值会把行上的 false 翻成 true（那正是本案要修的覆写等价物）。
    /// </summary>
    private static bool RowFirstIsTrue(object? v) => v switch
    {
        null => false,
        bool b => b,
        _ => bool.TryParse(Convert.ToString(v), out var p) && p,
    };

    /// <summary>
    /// issues/121 建单不变量 + issues/128 出口口径：<b>行上值优先</b>，只有存量行缺该键才按拓扑现算。
    /// 现算带"仅进行中"判定，只够展示用，不能当引擎判据（Java 同注释，JeeflowFacade.java:313-316）。
    /// </summary>
    private static bool RowFirstOrCompute(IDictionary<string, object?> ext, bool doing,
                                           string? taskName, string? firstTaskNodeId)
    {
        var rowFirst = ext.TryGetValue(FlowConst.IsFirstTaskNode, out var v) ? v : null;
        return rowFirst != null ? RowFirstIsTrue(rowFirst) : doing && taskName == firstTaskNodeId;
    }

    private static string? FirstTaskNodeId(Dictionary<string, object?>? jsonObject)
    {
        if (jsonObject != null &&
            jsonObject.TryGetValue("nodes", out var n) && n is List<object?> nodes)
        {
            foreach (var node in nodes)
            {
                if (node is Dictionary<string, object?> nd &&
                    "snaker:task".Equals(nd.TryGetValue("type", out var t) ? t?.ToString() : null, StringComparison.Ordinal))
                {
                    return nd.TryGetValue("id", out var id) ? id?.ToString() : null;
                }
            }
        }
        return null;
    }
}

namespace Mldong.Jeeflow.Core;

/// <summary>
/// 服务上下文——SPI 注册表 POCO（方案 §2.4：core 无 DI 依赖，构造注入普通对象，
/// demo/facade 宿主侧随意用 DI 装配）。启动期构建、运行期只读（小 SPI 可后置注册）。
/// </summary>
public sealed class ServiceContext
{
    /// <summary>聚合仓储（必需；未注册引擎 configure 抛 SPI_NOT_REGISTERED）。</summary>
    public IProcessRepository Repository { get; }

    /// <summary>扩展仓储（可选：设计/委托 action 未接入时报错）。</summary>
    public IProcessExtRepository? ExtRepository { get; }

    public IUserProvider? UserProvider { get; set; }
    public IOrgUserProvider? OrgUserProvider { get; set; }
    public IUserSearchProvider? UserSearchProvider { get; set; }

    /// <summary>JSON SPI；未注入用内置 DefaultJsonProvider。</summary>
    public IJsonProvider? JsonProvider { get; set; }

    /// <summary>表达式 SPI；未注入用内置 DefaultExpressionEvaluator。</summary>
    public IExpressionEvaluator? ExpressionEvaluator { get; set; }

    /// <summary>事务模板（可选；demo 默认不注入 = 语句级 autocommit）。</summary>
    public ITransactionTemplate? TransactionTemplate { get; set; }

    /// <summary>ID 生成器；未注入回退默认雪花（worker 0）。</summary>
    public IIdGenerator? IdGenerator { get; set; }

    /// <summary>action 权限码映射；未注入用默认 wf:{action /→:}（issues/29）。</summary>
    public IActionPermissionProvider? ActionPermissionProvider { get; set; }

    /// <summary>时钟；未注入 SystemClock，测试注 FixedClock。</summary>
    public IClock? Clock { get; set; }

    /// <summary>业务数据读取器（bizData action，按名 "metaTableReader" 查找，issues/23/28）。</summary>
    public object? BizDataReader { get; set; }

    /// <summary>
    /// 委托代理自动生效开关（issues/116 批次 D，契约 06 §4.5 条款 3）——<b>引擎内置、默认开启</b>。
    /// <para><b>关闭 API（.NET options 开关，主路径）</b>：<c>ctx.SurrogateAutoApply = false;</c></para>
    /// <para>等价第二路径（null-object 注册）：<c>ctx.SurrogateApplier = NullSurrogateApplier.Instance;</c></para>
    /// 关闭后回到「仅台账」行为：<c>processSurrogate/*</c> 五 action 照存照查，建任务不再应用委托。
    /// </summary>
    public bool SurrogateAutoApply { get; set; } = true;

    /// <summary>
    /// 委托应用扩展点（issues/116）；null = 内置 <see cref="ExtRepositorySurrogateApplier"/>
    /// （逐个参与者查 <see cref="IProcessExtRepository.GetSurrogateAsync"/>）。
    /// 注册 <see cref="NullSurrogateApplier"/> 或自定义实现即可覆盖默认行为。
    /// </summary>
    public ISurrogateApplier? SurrogateApplier { get; set; }

    /// <summary>解析委托应用器：未注册即用内置默认（首次解析后缓存）。</summary>
    public ISurrogateApplier SurrogateApplierOrDefault =>
        SurrogateApplier ??= new ExtRepositorySurrogateApplier(this);

    /// <summary>assignmentHandler 按名注册表（键=Java FQCN，注册名=Java 口径，方案 §3.1）。</summary>
    public Dictionary<string, IAssignmentHandler> AssignmentHandlers { get; } = new();

    /// <summary>decisionHandler 按名注册表。</summary>
    public Dictionary<string, IDecisionHandler> DecisionHandlers { get; } = new();

    /// <summary>custom 节点 clazz 按名注册表（键=Java FQCN 口径）。</summary>
    public Dictionary<string, IHandler> CustomHandlers { get; } = new();

    /// <summary>节点 pre/postInterceptors 声明名注册表（C20：声明名不可解析显式报错）。</summary>
    public Dictionary<string, IFlowInterceptor> NamedInterceptors { get; } = new();

    /// <summary>candidateHandler 注册池（Java findList 语义：全部命中后合并去重）。</summary>
    public Dictionary<string, ICandidateHandler> CandidateHandlers { get; } = new();

    /// <summary>有序拦截器（order 升序；pre/post 共用注册池）。</summary>
    public List<InterceptorEntry> Interceptors { get; } = new();

    /// <summary>事件监听器（逐个隔离：单监听器异常不中断其余与主流程，C13）。</summary>
    public List<IProcessEventListener> EventListeners { get; } = new();

    public ServiceContext(IProcessRepository repository, IProcessExtRepository? extRepository = null)
    {
        Repository = repository ?? throw new ArgumentNullException(nameof(repository));
        ExtRepository = extRepository;
    }

    public IJsonProvider JsonProviderOrDefault => JsonProvider ?? DefaultJsonProvider.Instance;

    public IExpressionEvaluator ExpressionEvaluatorOrDefault =>
        ExpressionEvaluator ?? DefaultExpressionEvaluator.Instance;

    public IClock ClockOrDefault => Clock ?? SystemClock.Instance;

    public IIdGenerator IdGeneratorOrDefault => IdGenerator ?? new AtomicIdGenerator(0L, Clock);

    public IActionPermissionProvider ActionPermissionProviderOrDefault =>
        ActionPermissionProvider ?? DefaultActionPermissionProvider.Instance;

    /// <summary>注册 assignmentHandler（运行时按节点 properties.assignmentHandler 名解析）。</summary>
    public ServiceContext RegisterAssignmentHandler(string name, IAssignmentHandler handler)
    {
        AssignmentHandlers[name] = handler;
        return this;
    }

    public ServiceContext RegisterDecisionHandler(string name, IDecisionHandler handler)
    {
        DecisionHandlers[name] = handler;
        return this;
    }

    public ServiceContext RegisterInterceptor(int order, Func<Execution, Task> run, string? group = null)
    {
        Interceptors.Add(new InterceptorEntry(order, run, group));
        Interceptors.Sort((a, b) => a.Order.CompareTo(b.Order));
        return this;
    }

    public ServiceContext RegisterEventListener(IProcessEventListener listener)
    {
        EventListeners.Add(listener);
        return this;
    }

    /// <summary>按名解析 assignmentHandler；声明名不可解析 → 显式错误（C20，不静默跳过）。</summary>
    public IAssignmentHandler FindAssignmentHandler(string name) =>
        AssignmentHandlers.TryGetValue(name, out var h)
            ? h
            : throw new JeeflowException($"无法解析 assignmentHandler: {name}");

    public IDecisionHandler FindDecisionHandler(string name) =>
        DecisionHandlers.TryGetValue(name, out var h)
            ? h
            : throw new JeeflowException($"无法解析 decisionHandler: {name}");

    public ICandidateHandler FindCandidateHandler(string name) =>
        CandidateHandlers.TryGetValue(name, out var h)
            ? h
            : throw new JeeflowException($"无法解析 candidateHandler: {name}");

    /// <summary>action → 权限码（供码不鉴权，C24）。</summary>
    public string[]? PermissionCodes(string action) =>
        ActionPermissionProviderOrDefault.PermissionCodes(action);
}

/// <summary>有序拦截器条目。</summary>
public sealed record InterceptorEntry(int Order, Func<Execution, Task> Run, string? Group = null);

/// <summary>参与者处理接口（内置 7 个，注册名=Java FQCN）。</summary>
public interface IAssignmentHandler
{
    Task<string?> AssignAsync(Execution execution);
}

/// <summary>决策处理器接口。</summary>
public interface IDecisionHandler
{
    Task<string?> DecideAsync(Execution execution);
}

/// <summary>流程拦截器接口（引擎内建拦截器 + 集成方 persist 拦截器）。</summary>
public interface IFlowInterceptor
{
    Task InterceptAsync(Execution execution);
}

/// <summary>候选人处理接口。</summary>
public interface ICandidateHandler
{
    Task<List<Candidate>?> HandleAsync(TaskModel taskModel);
}

/// <summary>流程事件监听器接口。</summary>
public interface IProcessEventListener
{
    Task OnEventAsync(ProcessEvent @event);
}

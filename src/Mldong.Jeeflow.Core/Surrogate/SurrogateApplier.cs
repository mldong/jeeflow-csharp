namespace Mldong.Jeeflow.Core;

/// <summary>
/// 委托代理「运行期自动生效」扩展点（issues/116 批次 D）——引擎内置、默认开启。
///
/// <para>契约依据：<c>jeeflow-doc docs/spec/06-facade.md</c> §4.5「运行期语义」六条 +
/// <c>docs/spec/05-spi.md</c>「SurrogateInterceptor」+ <c>docs/spec/08-compliance.md</c> 用例 26/27。</para>
///
/// <para>引擎在<b>参与者解析完成后、任务落库前</b>（即"建单那一刻"）对每个新建任务调用一次
/// <see cref="ApplyAsync"/>，命中生效委托则把被委托人<b>并入参与者集合本身</b>，随后由
/// <c>IProcessRepository.SaveTaskAsync</c> 随任务一起全量写入 <c>wf_process_task_actor</c>。</para>
///
/// <para><b>为什么不是"事后 <c>AddTaskActorAsync</c> 补写"</b>（条款 2 的 ⚠️）：Java 首版
/// <c>SurrogateInterceptor</c> 正走补写路，而它挂在 taskId 分配前，那次补写打在空 id 上
/// <b>静默无效</b>——能力看起来实现了，实际一单都没代理出去。本实现只改集合，不补写。</para>
/// </summary>
public interface ISurrogateApplier
{
    /// <summary>
    /// 对单个待办任务应用生效委托：<paramref name="task"/> 的 <c>ActorIds</c> 就地更新，
    /// 供紧随其后的 <c>SaveTaskAsync</c> 全量落库。实现必须幂等（已在集合里的代理人不重复追加）。
    /// </summary>
    /// <param name="task">待落库任务（参与者集合就地更新）</param>
    /// <param name="processName">
    /// 当前流程名——契约 06 §4.5 条款 1.1 定的是<b>流程模型 name 优先</b>
    /// （<c>ProcessModel.Name</c>，即流程 JSON 顶层 <c>name</c>），<b>模型未带 name 时</b>才回落
    /// <c>wf_process_define.name</c>（引擎 <c>ResolveSurrogateProcessNameAsync</c> 负责解析）。
    /// 正常部署下两者恒等（deploy 有 <c>def.Name = model.Name</c> 不变量）；仍为 null/空串时
    /// 只命中"全流程委托"兜底行
    /// </param>
    /// <param name="now">判定时间（取 <c>ServiceContext.ClockOrDefault.Now</c>，测试可注入固定钟）</param>
    Task ApplyAsync(ProcessTask task, string? processName, DateTime now);
}

/// <summary>
/// 空实现（null-object）——<b>显式关闭</b>委托自动生效的第二条路（条款 3）：
/// <c>ctx.SurrogateApplier = NullSurrogateApplier.Instance;</c>。
/// 注册后引擎不再应用委托，回到「仅台账」行为（<c>processSurrogate/*</c> 五 action 照存照查）。
/// DI 装配场景等价写法：把 <see cref="ISurrogateApplier"/> 注册成本实例后透传给
/// <c>ServiceContext.SurrogateApplier</c>。
/// </summary>
public sealed class NullSurrogateApplier : ISurrogateApplier
{
    /// <summary>共享单例（无状态）。</summary>
    public static readonly NullSurrogateApplier Instance = new();

    public Task ApplyAsync(ProcessTask task, string? processName, DateTime now) => Task.CompletedTask;
}

/// <summary>
/// 内置默认实现：逐个参与者查 <see cref="IProcessExtRepository.GetSurrogateAsync"/>
/// （四判据由仓储保证，内存仓与 SQL 仓必须同答案——条款 5/6）。
///
/// <para>四条不变量（各栈同形状）：</para>
/// <list type="number">
///   <item><description><b>不级联</b>（条款 1.2）：只对<b>建单那一刻的原始参与者快照</b>逐个查一次，
///   代理人自身的委托不展开（A→B 且 B→C 时 C 收不到该单）。实现上先取快照再遍历，
///   绝不边遍历边往同一集合追加。</description></item>
///   <item><description><b>授权人保留</b>（条款 2）：只追加、只去重，不摘原人
///   （与 <c>processTask/surrogate</c> 加签同语义——委托不是转办，任一可办）。</description></item>
///   <item><description><b>只对待办生效</b>：已完成/废弃/撤回的历史任务不追加代理人。</description></item>
///   <item><description><b>未配置扩展仓储时静默跳过</b>（条款 4）：委托是增强能力，缺仓储属正常部署形态，
///   不得抛"未配置扩展仓储"打断建单；仓储自身报错（如表未建）同样只记日志、继续建单。</description></item>
/// </list>
///
/// <para>串行会签（条款 1.3）：本实现只改 <c>ProcessTask.ActorIds</c>，不碰
/// <c>operatorList_{node}</c> / <c>nrOfInstances_{node}</c> 一类投票名册变量，票数不变。</para>
/// </summary>
public sealed class ExtRepositorySurrogateApplier : ISurrogateApplier
{
    private readonly ServiceContext _context;

    public ExtRepositorySurrogateApplier(ServiceContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task ApplyAsync(ProcessTask task, string? processName, DateTime now)
    {
        if (task == null || !task.IsDoing()) return;             // 只给待办追加代理人
        var repo = _context.ExtRepository;
        if (repo == null) return;                                 // 未配置扩展仓储：静默跳过（不打断建单）
        var actors = task.ActorIds;
        if (actors == null || actors.Count == 0) return;

        var additions = new List<string>();
        // 快照原始参与者：本轮追加的代理人不再触发查询（条款 1.2 不级联，避免环状委托死循环）
        foreach (var actor in actors.ToArray())
        {
            ProcessSurrogate? hit;
            try
            {
                hit = await repo.GetSurrogateAsync(actor, processName, now);
            }
            catch (Exception e)
            {
                // 委托是增强能力：查询报错不得打断建单（同 ProcessPublisher 监听器隔离口径）
                Console.Error.WriteLine(
                    $"[jeeflow] getSurrogate(actor={actor}, processName={processName}) failed, " +
                    $"surrogate skipped: {e.Message}");
                continue;
            }
            var agent = hit?.Surrogate?.Trim();
            if (string.IsNullOrEmpty(agent)) continue;
            if (actors.Contains(agent) || additions.Contains(agent)) continue;
            additions.Add(agent);
        }
        if (additions.Count == 0) return;

        // 落在参与者集合本身（随后随任务一起 saveTask 全量落库；换引用而非原地改，
        // 以便建单期已按引用捕获快照的调用方——如串行会签投票名册——不被扩写）
        var merged = new List<string>(actors);
        merged.AddRange(additions);
        task.ActorIds = merged;
    }
}

namespace Mldong.Jeeflow.Core;

/// <summary>
/// 内存扩展仓储（设计/设计历史/委托代理），行为对齐 JdbcProcessExtRepository。
/// </summary>
public class MemoryExtRepository : IProcessExtRepository
{
    private readonly MemoryRepository _repo;
    private ServiceContext? _ctx;
    private IClock Clock => _ctx?.ClockOrDefault ?? SystemClock.Instance;
    private IIdGenerator IdGen => _ctx?.IdGeneratorOrDefault ?? new AtomicIdGenerator(0L, Clock);

    internal readonly Dictionary<long, ProcessDesign> Designs = new();
    internal readonly Dictionary<long, ProcessDesignHis> DesignHis = new();
    internal readonly Dictionary<long, ProcessSurrogate> Surrogates = new();
    internal long _hisAutoId;

    public MemoryExtRepository(MemoryRepository repo, ServiceContext? context = null)
    {
        _repo = repo;
        _ctx = context;
    }

    /// <summary>两阶段接线：ServiceContext 构造后回填。</summary>
    public void Configure(ServiceContext context) => _ctx = context;

    // ═══ 流程设计 ═══

    public virtual Task<ProcessDesign?> FindDesignByIdAsync(long? designId)
    {
        Designs.TryGetValue(designId ?? 0, out var d);
        return Task.FromResult(d != null ? CloneDesign(d) : null);
    }

    public virtual Task SaveDesignAsync(ProcessDesign design)
    {
        if (design.Id == null) design.Id = IdGen.NextId();
        Designs[design.Id.Value] = CloneDesign(design);
        return Task.CompletedTask;
    }

    public virtual Task UpdateDesignAsync(ProcessDesign design)
    {
        if (design.Id == null || !Designs.TryGetValue(design.Id.Value, out var stored))
            return Task.CompletedTask;
        stored.Name = design.Name ?? stored.Name;
        stored.DisplayName = design.DisplayName ?? stored.DisplayName;
        stored.Type = design.Type ?? stored.Type;
        stored.Icon = design.Icon ?? stored.Icon;
        stored.IsDeployed = design.IsDeployed ?? stored.IsDeployed;
        stored.Remark = design.Remark ?? stored.Remark;
        stored.UpdateUser = design.UpdateUser;
        stored.UpdateTime = Clock.Now;
        return Task.CompletedTask;
    }

    public virtual Task RemoveDesignAsync(long designId)
    {
        Designs.Remove(designId);
        var hisIds = DesignHis.Values.Where(h => h.ProcessDesignId == designId).Select(h => h.Id!.Value).ToList();
        foreach (var id in hisIds) DesignHis.Remove(id);
        return Task.CompletedTask;
    }

    public virtual Task<PageResult<ProcessDesign>> PageDesignsAsync(PageQuery query)
    {
        // 简易分页（设计量小；条件白名单同 define 口径）
        var rows = Designs.Values
            .OrderByDescending(d => d.Id)
            .Select(CloneDesign)
            .ToList();
        var pageNum = Math.Max(query.PageNum, 1);
        var pageSize = Math.Max(query.PageSize, 1);
        var page = rows.Skip((pageNum - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(PageResult<ProcessDesign>.Of(pageNum, pageSize, rows.Count, page));
    }

    // ═══ 设计历史 ═══

    public virtual Task SaveDesignHisAsync(ProcessDesignHis his)
    {
        if (his.Id == null) his.Id = ++_hisAutoId;
        DesignHis[his.Id.Value] = new ProcessDesignHis
        {
            Id = his.Id,
            ProcessDesignId = his.ProcessDesignId,
            Content = his.Content,
            CreateTime = Clock.Now,
            CreateUser = his.CreateUser,
        };
        return Task.CompletedTask;
    }

    public virtual Task<List<ProcessDesignHis>> ListDesignHisAsync(long designId)
    {
        // 最新在前（对齐 JDBC ORDER BY id DESC——首页为最新快照）
        var rows = DesignHis.Values
            .Where(h => h.ProcessDesignId == designId)
            .OrderByDescending(h => h.Id)
            .Select(h => new ProcessDesignHis
            {
                Id = h.Id,
                ProcessDesignId = h.ProcessDesignId,
                Content = h.Content,
                CreateTime = h.CreateTime,
                CreateUser = h.CreateUser,
            })
            .ToList();
        return Task.FromResult(rows);
    }

    // ═══ 委托代理 ═══

    public virtual Task<ProcessSurrogate?> FindSurrogateByIdAsync(long? surrogateId)
    {
        Surrogates.TryGetValue(surrogateId ?? 0, out var s);
        return Task.FromResult(s != null ? CloneSurrogate(s) : null);
    }

    public virtual Task SaveSurrogateAsync(ProcessSurrogate surrogate)
    {
        if (surrogate.Id == null) surrogate.Id = IdGen.NextId();
        Surrogates[surrogate.Id.Value] = CloneSurrogate(surrogate);
        return Task.CompletedTask;
    }

    public virtual Task UpdateSurrogateAsync(ProcessSurrogate surrogate)
    {
        if (surrogate.Id == null || !Surrogates.TryGetValue(surrogate.Id.Value, out var stored))
            return Task.CompletedTask;
        stored.ProcessName = surrogate.ProcessName;
        stored.Operator = surrogate.Operator ?? stored.Operator;
        stored.Surrogate = surrogate.Surrogate;
        stored.StartTime = surrogate.StartTime;
        stored.EndTime = surrogate.EndTime;
        stored.Enabled = surrogate.Enabled ?? 1;
        stored.UpdateUser = surrogate.UpdateUser;
        stored.UpdateTime = Clock.Now;
        return Task.CompletedTask;
    }

    public virtual Task RemoveSurrogateAsync(long surrogateId)
    {
        Surrogates.Remove(surrogateId);
        return Task.CompletedTask;
    }

    public virtual Task<PageResult<ProcessSurrogate>> PageSurrogatesAsync(PageQuery query)
    {
        var rows = Surrogates.Values
            .OrderByDescending(s => s.Id)
            .Select(CloneSurrogate)
            .ToList();
        var pageNum = Math.Max(query.PageNum, 1);
        var pageSize = Math.Max(query.PageSize, 1);
        var page = rows.Skip((pageNum - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(PageResult<ProcessSurrogate>.Of(pageNum, pageSize, rows.Count, page));
    }

    public virtual Task<ProcessSurrogate?> GetSurrogateAsync(string? op, string? processName, DateTime time)
    {
        // 规范 06 §4.5 条款 1.4（issues/123 修正后的判序）：**每个作用域各自先按主键 id 取最新一条**
        // （不带任何生效判据过滤），再交 ProcessSurrogate.IsEffective 裁决那一条。
        // 反过来写——先按 enabled=1 / 时间窗 / surrogate<>operator 把候选滤掉、剩下的才排序——
        // 等价于"上一条窗内委托把用户后续改停用/改到未来的设置永久盖掉"，
        // 正是 13 栈在 L2-17/L2-18 上恒并入的成因。
        // 精确作用域那条判否后**仍要看全流程作用域的最新一条**（不得判否即止），
        // 与 SQL 仓 MySqlExtRepository#getSurrogateAsync 同形（条款 6 双仓同答案）。
        var exact = NewestInScope(op, processName, scopeGlobal: false);
        if (exact != null && exact.IsEffective(op, time)) return Task.FromResult(exact);
        var global = NewestInScope(op, processName, scopeGlobal: true);
        return Task.FromResult(global != null && global.IsEffective(op, time) ? global : null);
    }

    /// <summary>
    /// 取该授权人在指定流程作用域内**最新的一条**委托（id 最大）；只择优，不判生效。
    /// <paramref name="scopeGlobal"/>=false ⇒ processName 精确匹配；true ⇒ 全流程委托（processName 为空）。
    /// </summary>
    private ProcessSurrogate? NewestInScope(string? op, string? processName, bool scopeGlobal)
    {
        if (op == null) return null;
        ProcessSurrogate? best = null;
        long bestId = long.MinValue;
        foreach (var s in Surrogates.Values)
        {
            if (s.Operator == null || !s.Operator.Equals(op, StringComparison.Ordinal)) continue;
            var inScope = scopeGlobal
                ? string.IsNullOrEmpty(s.ProcessName)
                : processName != null && string.Equals(s.ProcessName, processName, StringComparison.Ordinal);
            if (!inScope) continue;
            var id = s.Id ?? 0;
            if (best == null || id > bestId)
            {
                best = s;
                bestId = id;
            }
        }
        return best == null ? null : CloneSurrogate(best);
    }

    private static ProcessDesign CloneDesign(ProcessDesign d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        DisplayName = d.DisplayName,
        Type = d.Type,
        Icon = d.Icon,
        IsDeployed = d.IsDeployed,
        Remark = d.Remark,
        CreateTime = d.CreateTime,
        CreateUser = d.CreateUser,
        UpdateTime = d.UpdateTime,
        UpdateUser = d.UpdateUser,
    };

    private static ProcessSurrogate CloneSurrogate(ProcessSurrogate s) => new()
    {
        Id = s.Id,
        ProcessName = s.ProcessName,
        Operator = s.Operator,
        Surrogate = s.Surrogate,
        StartTime = s.StartTime,
        EndTime = s.EndTime,
        Enabled = s.Enabled,
        CreateTime = s.CreateTime,
        CreateUser = s.CreateUser,
        UpdateTime = s.UpdateTime,
        UpdateUser = s.UpdateUser,
    };
}

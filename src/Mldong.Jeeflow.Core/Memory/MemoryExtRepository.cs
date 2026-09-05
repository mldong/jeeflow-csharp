namespace Mldong.Jeeflow.Core;

/// <summary>
/// 内存扩展仓储（设计/设计历史/委托代理），行为对齐 JdbcProcessExtRepository。
/// </summary>
public class MemoryExtRepository : IProcessExtRepository
{
    private readonly MemoryRepository _repo;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGen;

    internal readonly Dictionary<long, ProcessDesign> Designs = new();
    internal readonly Dictionary<long, ProcessDesignHis> DesignHis = new();
    internal readonly Dictionary<long, ProcessSurrogate> Surrogates = new();
    internal long _hisAutoId;

    public MemoryExtRepository(MemoryRepository repo, ServiceContext context)
    {
        _repo = repo;
        _clock = context.ClockOrDefault;
        _idGen = context.IdGeneratorOrDefault;
    }

    // ═══ 流程设计 ═══

    public virtual Task<ProcessDesign?> FindDesignByIdAsync(long? designId)
    {
        Designs.TryGetValue(designId ?? 0, out var d);
        return Task.FromResult(d != null ? CloneDesign(d) : null);
    }

    public virtual Task SaveDesignAsync(ProcessDesign design)
    {
        if (design.Id == null) design.Id = _idGen.NextId();
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
        stored.UpdateTime = _clock.Now;
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
            CreateTime = _clock.Now,
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
        if (surrogate.Id == null) surrogate.Id = _idGen.NextId();
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
        stored.UpdateTime = _clock.Now;
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
        // 对齐 JDBC querySurrogate：operator=授权人 且 enabled=1 且 surrogate<>operator；
        // 时间窗 start<=t<=end（起止为空表示不限）；优先 processName 精确匹配（id DESC 首行），
        // 其次 process_name 为空的"全流程委托"兜底
        ProcessSurrogate? exact = null;
        ProcessSurrogate? wildcard = null;
        foreach (var s in Surrogates.Values.OrderByDescending(x => x.Id))
        {
            if (s.Enabled != 1) continue;
            if (s.Operator == null || !s.Operator.Equals(op, StringComparison.Ordinal)) continue;
            if (s.Surrogate == null || s.Surrogate.Equals(op, StringComparison.Ordinal)) continue;
            if (s.StartTime != null && time < s.StartTime) continue;
            if (s.EndTime != null && time > s.EndTime) continue;
            if (s.ProcessName != null && processName != null &&
                s.ProcessName.Equals(processName, StringComparison.Ordinal) && exact == null)
                exact = CloneSurrogate(s);
            if (string.IsNullOrEmpty(s.ProcessName) && wildcard == null)
                wildcard = CloneSurrogate(s);
        }
        return Task.FromResult(exact ?? wildcard);
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

namespace Mldong.Jeeflow.Core;

/// <summary>
/// 扩展仓储 SPI（可选）——流程设计 / 设计历史 / 委托代理（对齐 Java IProcessExtRepository，全异步）。
/// </summary>
public interface IProcessExtRepository
{
    // ═══ 流程设计（wf_process_design）═══

    Task<ProcessDesign?> FindDesignByIdAsync(long? designId);
    Task SaveDesignAsync(ProcessDesign design);
    Task UpdateDesignAsync(ProcessDesign design);
    Task RemoveDesignAsync(long designId);
    Task<PageResult<ProcessDesign>> PageDesignsAsync(PageQuery query);

    // ═══ 设计历史（wf_process_design_his）═══

    Task SaveDesignHisAsync(ProcessDesignHis his);
    Task<List<ProcessDesignHis>> ListDesignHisAsync(long designId);

    // ═══ 委托代理（wf_process_surrogate）═══

    Task<ProcessSurrogate?> FindSurrogateByIdAsync(long? surrogateId);
    Task SaveSurrogateAsync(ProcessSurrogate surrogate);
    Task UpdateSurrogateAsync(ProcessSurrogate surrogate);
    Task RemoveSurrogateAsync(long surrogateId);
    Task<PageResult<ProcessSurrogate>> PageSurrogatesAsync(PageQuery query);

    /// <summary>
    /// 查询指定时间生效中的委托：enabled=1 且时间窗内（起止为空表示不限）。
    /// 优先 processName 精确匹配，其次 processName 为空的"全流程委托"兜底。
    /// </summary>
    Task<ProcessSurrogate?> GetSurrogateAsync(string? operatorId, string? processName, DateTime time);
}

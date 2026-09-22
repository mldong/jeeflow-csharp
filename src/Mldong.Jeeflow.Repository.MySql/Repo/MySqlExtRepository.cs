using System.Data;
using System.Text;
using Mldong.Jeeflow.Core;
using MySqlConnector;

namespace Mldong.Jeeflow.Repository.MySql;

/// <summary>
/// MySQL 扩展仓储（设计/设计历史/委托代理，14 方法，对齐 JdbcProcessExtRepository）。
/// removeDesign 级联删历史（同连接）；listDesignHis 最新在前（id DESC）。
/// </summary>
public class MySqlExtRepository : IProcessExtRepository
{
    private readonly MySqlConnectionFactory _factory;
    private readonly ServiceContext? _ctx;
    private readonly MySqlRepository _owner;

    public MySqlExtRepository(MySqlConnectionFactory factory, MySqlRepository owner, ServiceContext? ctx = null)
    {
        _factory = factory;
        _owner = owner;
        _ctx = ctx;
    }

    private IClock Clock => _ctx?.ClockOrDefault ?? SystemClock.Instance;
    private IIdGenerator IdGen => _ctx?.IdGeneratorOrDefault ?? new AtomicIdGenerator(0L);

    private MySqlCommand NewCmd(string sql, MySqlConnection conn)
    {
        var cmd = new MySqlCommand(sql, conn);
        var tx = _owner.AmbientTx.Value;
        if (tx != null) cmd.Transaction = tx;
        return cmd;
    }

    /// <summary>优先复用主仓储的环境连接（事务内同连接）。</summary>
    private async Task<MySqlRepository.ConnLease> RentAsync()
    {
        var ambient = _owner.AmbientConn.Value;
        if (ambient != null) return new MySqlRepository.ConnLease(ambient, owned: false);
        return new MySqlRepository.ConnLease(await _factory.OpenAsync(), owned: true);
    }

    // ═══ 流程设计 ═══

    public virtual async Task<ProcessDesign?> FindDesignByIdAsync(long? designId)
    {
        if (designId == null) return null;
        await using var lease = await RentAsync();
        const string sql = "SELECT id, name, display_name, type, icon, is_deployed, remark, " +
                           "create_time, create_user, update_time, update_user FROM wf_process_design WHERE id = ?";
        await using var cmd = NewCmd(sql, lease.Conn);
        cmd.Parameters.Add(new MySqlParameter { Value = designId.Value });
        await using var rs = await cmd.ExecuteReaderAsync();
        if (!await rs.ReadAsync()) return null;
        return MapDesign(rs);
    }

    public virtual async Task SaveDesignAsync(ProcessDesign design)
    {
        if (design.Id == null) design.Id = IdGen.NextId();
        await using var lease = await RentAsync();
        const string sql = "INSERT INTO wf_process_design (id, name, display_name, type, icon, is_deployed, remark, " +
                           "create_time, create_user, update_time, update_user) VALUES (?,?,?,?,?,?,?,?,?,?,?)";
        await using var cmd = NewCmd(sql, lease.Conn);
        var now = MySqlRepository.ToDb(design.CreateTime ?? Clock.Now);
        cmd.Parameters.Add(new MySqlParameter { Value = design.Id.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)design.Name ?? DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)design.DisplayName ?? DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)design.Type ?? DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)design.Icon ?? DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = design.IsDeployed ?? 0 });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)design.Remark ?? DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = now });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)design.CreateUser ?? DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = now });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)design.UpdateUser ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync();
    }

    public virtual async Task UpdateDesignAsync(ProcessDesign design)
    {
        if (design.Id == null) return;
        await using var lease = await RentAsync();
        const string sql = "UPDATE wf_process_design SET name=?, display_name=?, type=?, icon=?, is_deployed=?, " +
                           "remark=?, update_time=?, update_user=? WHERE id=?";
        await using var cmd = NewCmd(sql, lease.Conn);
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)design.Name ?? DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)design.DisplayName ?? DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)design.Type ?? DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)design.Icon ?? DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = design.IsDeployed ?? 0 });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)design.Remark ?? DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = MySqlRepository.ToDb(Clock.Now) });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)design.UpdateUser ?? DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = design.Id.Value });
        await cmd.ExecuteNonQueryAsync();
    }

    public virtual async Task RemoveDesignAsync(long designId)
    {
        await using var lease = await RentAsync();
        await using (var cmd = new MySqlCommand("DELETE FROM wf_process_design WHERE id=?", lease.Conn))
        {
            cmd.Parameters.Add(new MySqlParameter { Value = designId });
            await cmd.ExecuteNonQueryAsync();
        }
        // 级联删历史（同连接，对齐 JDBC）
        await using (var cmd = new MySqlCommand("DELETE FROM wf_process_design_his WHERE process_design_id=?", lease.Conn))
        {
            cmd.Parameters.Add(new MySqlParameter { Value = designId });
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public virtual async Task<PageResult<ProcessDesign>> PageDesignsAsync(PageQuery query)
    {
        await using var lease = await RentAsync();
        var where = new System.Text.StringBuilder(" FROM wf_process_design t WHERE 1=1");
        var bind = new List<object?>();
        _owner.BuildWhere(where, bind, query, DesignWhitelist);
        var totalRs = await ScalarAsync(lease.Conn, "SELECT COUNT(*)" + where, bind);
        var total = Convert.ToInt32(totalRs ?? 0);
        var order = _owner.BuildOrder(query, "t.", DesignWhitelist);
        var sql = "SELECT t.*" + where + order +
                  $" LIMIT {MySqlRepository.SafeLimit(query.PageSize)} OFFSET {MySqlRepository.SafeOffset(query.PageNum, query.PageSize)}";
        var rows = new List<ProcessDesign>();
        await using var cmd = NewCmd(sql, lease.Conn);
        foreach (var b in bind) cmd.Parameters.Add(new MySqlParameter { Value = b ?? DBNull.Value });
        await using var rs = await cmd.ExecuteReaderAsync();
        while (await rs.ReadAsync()) rows.Add(MapDesign(rs));
        return PageResult<ProcessDesign>.Of(query.PageNum, query.PageSize, total, rows);
    }

    // ═══ 设计历史 ═══

    public virtual async Task SaveDesignHisAsync(ProcessDesignHis his)
    {
        if (his.Id == null) his.Id = IdGen.NextId();
        await using var lease = await RentAsync();
        const string sql = "INSERT INTO wf_process_design_his (id, process_design_id, content, create_time, create_user) " +
                           "VALUES (?,?,?,?,?)";
        await using var cmd = NewCmd(sql, lease.Conn);
        cmd.Parameters.Add(new MySqlParameter { Value = his.Id.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = his.ProcessDesignId.HasValue ? his.ProcessDesignId.Value : DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)his.Content ?? DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = MySqlRepository.ToDb(his.CreateTime ?? Clock.Now) });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)his.CreateUser ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync();
    }

    public virtual async Task<List<ProcessDesignHis>> ListDesignHisAsync(long designId)
    {
        await using var lease = await RentAsync();
        const string sql = "SELECT id, process_design_id, content, create_time, create_user " +
                           "FROM wf_process_design_his WHERE process_design_id = ? ORDER BY id DESC";
        var rows = new List<ProcessDesignHis>();
        await using var cmd = NewCmd(sql, lease.Conn);
        cmd.Parameters.Add(new MySqlParameter { Value = designId });
        await using var rs = await cmd.ExecuteReaderAsync();
        while (await rs.ReadAsync())
        {
            rows.Add(new ProcessDesignHis
            {
                Id = MySqlRepository.GetLong(rs, "id"),
                ProcessDesignId = MySqlRepository.GetLong(rs, "process_design_id"),
                Content = MySqlRepository.GetBytes(rs, "content"),
                CreateTime = MySqlRepository.GetDateTime(rs, "create_time"),
                CreateUser = MySqlRepository.GetStr(rs, "create_user"),
            });
        }
        return rows;
    }

    // ═══ 委托代理 ═══

    public virtual async Task<ProcessSurrogate?> FindSurrogateByIdAsync(long? surrogateId)
    {
        if (surrogateId == null) return null;
        await using var lease = await RentAsync();
        const string sql = "SELECT id, process_name, operator, surrogate, start_time, end_time, enabled, " +
                           "create_time, create_user, update_time, update_user FROM wf_process_surrogate WHERE id = ?";
        await using var cmd = NewCmd(sql, lease.Conn);
        cmd.Parameters.Add(new MySqlParameter { Value = surrogateId.Value });
        await using var rs = await cmd.ExecuteReaderAsync();
        if (!await rs.ReadAsync()) return null;
        return MapSurrogate(rs);
    }

    public virtual async Task SaveSurrogateAsync(ProcessSurrogate surrogate)
    {
        if (surrogate.Id == null) surrogate.Id = IdGen.NextId();
        await using var lease = await RentAsync();
        const string sql = "INSERT INTO wf_process_surrogate (id, process_name, operator, surrogate, start_time, " +
                           "end_time, enabled, create_time, create_user, update_time, update_user) VALUES (?,?,?,?,?,?,?,?,?,?,?)";
        await using var cmd = NewCmd(sql, lease.Conn);
        BindSurrogate(cmd, surrogate, insert: true);
        await cmd.ExecuteNonQueryAsync();
    }

    public virtual async Task UpdateSurrogateAsync(ProcessSurrogate surrogate)
    {
        if (surrogate.Id == null) return;
        await using var lease = await RentAsync();
        const string sql = "UPDATE wf_process_surrogate SET process_name=?, operator=?, surrogate=?, start_time=?, " +
                           "end_time=?, enabled=?, update_time=?, update_user=? WHERE id=?";
        await using var cmd = NewCmd(sql, lease.Conn);
        BindSurrogate(cmd, surrogate, insert: false);
        await cmd.ExecuteNonQueryAsync();
    }

    public virtual async Task RemoveSurrogateAsync(long surrogateId)
    {
        await using var lease = await RentAsync();
        await using var cmd = NewCmd("DELETE FROM wf_process_surrogate WHERE id=?", lease.Conn);
        cmd.Parameters.Add(new MySqlParameter { Value = surrogateId });
        await cmd.ExecuteNonQueryAsync();
    }

    public virtual async Task<PageResult<ProcessSurrogate>> PageSurrogatesAsync(PageQuery query)
    {
        await using var lease = await RentAsync();
        var where = new System.Text.StringBuilder(" FROM wf_process_surrogate t WHERE 1=1");
        var bind = new List<object?>();
        _owner.BuildWhere(where, bind, query, SurrogateWhitelist);
        var totalRs = await ScalarAsync(lease.Conn, "SELECT COUNT(*)" + where, bind);
        var total = Convert.ToInt32(totalRs ?? 0);
        var order = _owner.BuildOrder(query, "t.", SurrogateWhitelist);
        var sql = "SELECT t.*" + where + order +
                  $" LIMIT {MySqlRepository.SafeLimit(query.PageSize)} OFFSET {MySqlRepository.SafeOffset(query.PageNum, query.PageSize)}";
        var rows = new List<ProcessSurrogate>();
        await using var cmd = NewCmd(sql, lease.Conn);
        foreach (var b in bind) cmd.Parameters.Add(new MySqlParameter { Value = b ?? DBNull.Value });
        await using var rs = await cmd.ExecuteReaderAsync();
        while (await rs.ReadAsync()) rows.Add(MapSurrogate(rs));
        return PageResult<ProcessSurrogate>.Of(query.PageNum, query.PageSize, total, rows);
    }

    public virtual async Task<ProcessSurrogate?> GetSurrogateAsync(string? op, string? processName, DateTime time)
    {
        if (op == null) return null;
        await using var lease = await RentAsync();
        // 规范 06 §4.5 条款 1.4（issues/123 修正后的判序）：**每个作用域各自先按主键 id 取最新一条**
        // （SQL 不带任何生效判据过滤），再交 ProcessSurrogate.IsEffective 裁决那一条。
        // 反过来写（SQL 先 `AND enabled = 1 AND surrogate <> ?` + 窗口条件，剩下的才 ORDER BY id DESC）
        // 等价于"历史上出现过一条窗内委托就永久生效"——用户随后改停用、改到未来都不算数，
        // 这就是 issues/123 里 13 栈"窗外 / enabled=0 / 脏值一律并入"的成因。
        // 两个作用域各取自己最新的一条、各自裁决：精确作用域那条判否时**仍要看全流程作用域的最新一条**
        // （不得判否即止）。内存仓 MemoryExtRepository#getSurrogateAsync 同形（条款 6 双仓同答案）。
        var exact = await QueryNewestSurrogateAsync(lease.Conn, op, processName);
        if (exact != null && exact.IsEffective(op, time)) return exact;
        var global = await QueryNewestSurrogateAsync(lease.Conn, op, "");
        return global != null && global.IsEffective(op, time) ? global : null;
    }

    /// <summary>取该授权人在指定流程作用域内**最新的一条**委托；只 ORDER BY id DESC LIMIT 1，
    /// 不带任何生效判据（enabled / 时间窗 / 自委托）过滤。</summary>
    private async Task<ProcessSurrogate?> QueryNewestSurrogateAsync(
        MySqlConnection conn, string op, string? processName)
    {
        var sql = new StringBuilder(
            "SELECT id, process_name, operator, surrogate, start_time, end_time, enabled, " +
            "create_time, create_user, update_time, update_user FROM wf_process_surrogate " +
            "WHERE operator = ?");
        var bind = new List<object?> { op };
        if (string.IsNullOrEmpty(processName))
        {
            sql.Append(" AND (process_name IS NULL OR process_name = '')");
        }
        else
        {
            sql.Append(" AND process_name = ?");
            bind.Add(processName);
        }
        sql.Append(" ORDER BY id DESC ");
        sql.Append(" LIMIT 1");
        await using var cmd = NewCmd(sql.ToString(), conn);
        foreach (var b in bind) cmd.Parameters.Add(new MySqlParameter { Value = b ?? DBNull.Value });
        await using var rs = await cmd.ExecuteReaderAsync();
        return await rs.ReadAsync() ? MapSurrogate(rs) : null;
    }

    // ═══ 白名单 / 映射 / 工具 ═══

    private static readonly HashSet<string> DesignWhitelist = new()
    {
        "t.id", "t.name", "t.display_name", "t.type", "t.state", "t.version",
        "t.create_time", "t.update_time", "t.is_deployed",
    };

    private static readonly HashSet<string> SurrogateWhitelist = new()
    {
        "t.id", "t.process_name", "t.operator", "t.surrogate", "t.start_time", "t.end_time",
        "t.enabled", "t.create_time", "t.update_time",
    };

    private static ProcessDesign MapDesign(IDataRecord rs) => new()
    {
        Id = MySqlRepository.GetLong(rs, "id"),
        Name = MySqlRepository.GetStr(rs, "name"),
        DisplayName = MySqlRepository.GetStr(rs, "display_name"),
        Type = MySqlRepository.GetStr(rs, "type"),
        Icon = MySqlRepository.GetStr(rs, "icon"),
        IsDeployed = MySqlRepository.GetInt(rs, "is_deployed"),
        Remark = MySqlRepository.GetStr(rs, "remark"),
        CreateTime = MySqlRepository.GetDateTime(rs, "create_time"),
        CreateUser = MySqlRepository.GetStr(rs, "create_user"),
        UpdateTime = MySqlRepository.GetDateTime(rs, "update_time"),
        UpdateUser = MySqlRepository.GetStr(rs, "update_user"),
    };

    private void BindSurrogate(MySqlCommand cmd, ProcessSurrogate s, bool insert)
    {
        if (insert)
        {
            cmd.Parameters.Add(new MySqlParameter { Value = s.Id!.Value });
        }
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)s.ProcessName ?? DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)s.Operator ?? DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)s.Surrogate ?? DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = MySqlRepository.ToDb(s.StartTime) });
        cmd.Parameters.Add(new MySqlParameter { Value = MySqlRepository.ToDb(s.EndTime) });
        cmd.Parameters.Add(new MySqlParameter { Value = s.Enabled ?? 1 });
        if (insert)
        {
            var now = MySqlRepository.ToDb(s.CreateTime ?? Clock.Now);
            cmd.Parameters.Add(new MySqlParameter { Value = now });
            cmd.Parameters.Add(new MySqlParameter { Value = (object?)s.CreateUser ?? DBNull.Value });
            // INSERT 列序含 update_time（11 列 11 占位符），漏绑会让 update_user 错位到
            // update_time、末位占位符无参数可绑（issues/109 "Parameter index 10"）
            cmd.Parameters.Add(new MySqlParameter { Value = MySqlRepository.ToDb(s.UpdateTime ?? s.CreateTime ?? Clock.Now) });
        }
        else
        {
            cmd.Parameters.Add(new MySqlParameter { Value = MySqlRepository.ToDb(Clock.Now) });
        }
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)s.UpdateUser ?? DBNull.Value });
        if (!insert)
        {
            cmd.Parameters.Add(new MySqlParameter { Value = s.Id!.Value });
        }
    }

    private static ProcessSurrogate MapSurrogate(IDataRecord rs) => new()
    {
        Id = MySqlRepository.GetLong(rs, "id"),
        ProcessName = MySqlRepository.GetStr(rs, "process_name"),
        Operator = MySqlRepository.GetStr(rs, "operator"),
        Surrogate = MySqlRepository.GetStr(rs, "surrogate"),
        StartTime = MySqlRepository.GetDateTime(rs, "start_time"),
        EndTime = MySqlRepository.GetDateTime(rs, "end_time"),
        Enabled = MySqlRepository.GetInt(rs, "enabled"),
        CreateTime = MySqlRepository.GetDateTime(rs, "create_time"),
        CreateUser = MySqlRepository.GetStr(rs, "create_user"),
        UpdateTime = MySqlRepository.GetDateTime(rs, "update_time"),
        UpdateUser = MySqlRepository.GetStr(rs, "update_user"),
    };

    private async Task<object?> ScalarAsync(MySqlConnection conn, string sql, List<object?> bind)
    {
        await using var cmd = NewCmd(sql, conn);
        foreach (var b in bind) cmd.Parameters.Add(new MySqlParameter { Value = b ?? DBNull.Value });
        var r = await cmd.ExecuteScalarAsync();
        return r is DBNull ? null : r;
    }
}

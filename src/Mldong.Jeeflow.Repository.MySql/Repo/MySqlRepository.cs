using System.Data;
using System.Text;
using Mldong.Jeeflow.Core;
using MySqlConnector;

namespace Mldong.Jeeflow.Repository.MySql;

/// <summary>
/// MySQL 聚合仓储（对齐 jeeflow-repository-jdbc JdbcProcessRepository 的 SQL 与行为，全 async）。
/// 连接即取即用（autocommit，联邦现状）；ITransactionTemplate 注入时走环境连接（AsyncLocal）
/// ——同事务内所有仓储方法共用同一连接（spec/05）。
/// NULL 读侧显式 DBNull 处理（T10/C21）；LIMIT/OFFSET 内联非负整数（C21）。
/// </summary>
public class MySqlRepository : IProcessRepository
{
    private readonly MySqlConnectionFactory _factory;
    private ServiceContext? _ctx;
    internal readonly AsyncLocal<MySqlConnection?> AmbientConn = new();

    /// <summary>环境事务（ITransactionTemplate 注入时设置；命令必须绑定活动事务——MySqlConnector 强制）。</summary>
    internal readonly AsyncLocal<MySqlTransaction?> AmbientTx = new();

    private MySqlCommand NewCmd(string sql, MySqlConnection conn)
    {
        var cmd = new MySqlCommand(sql, conn);
        var tx = AmbientTx.Value;
        if (tx != null) cmd.Transaction = tx;
        return cmd;
    }

    public MySqlRepository(MySqlConnectionFactory factory, ServiceContext? ctx = null)
    {
        _factory = factory;
        _ctx = ctx;
    }

    /// <summary>两阶段接线：ServiceContext 构造后回填（打破 ctx↔repo 循环）。</summary>
    public void Configure(ServiceContext ctx) => _ctx = ctx;

    // ── JSON（引擎 SPI 收口；仓储侧默认内置 provider）──
    private IJsonProvider Json => _ctx?.JsonProvider ?? DefaultJsonProvider.Instance;

    private IClock Clock => _ctx?.ClockOrDefault ?? SystemClock.Instance;

    private IIdGenerator IdGen => _ctx?.IdGeneratorOrDefault ?? new AtomicIdGenerator(0L);

    // ═══ 连接租用 ═══

    private async Task<ConnLease> RentAsync(CancellationToken ct = default)
    {
        var ambient = AmbientConn.Value;
        if (ambient != null) return new ConnLease(ambient, owned: false);
        var conn = await _factory.OpenAsync(ct);
        return new ConnLease(conn, owned: true);
    }

    internal sealed class ConnLease : IAsyncDisposable
    {
        public MySqlConnection Conn { get; }
        private readonly bool _owned;

        public ConnLease(MySqlConnection conn, bool owned)
        {
            Conn = conn;
            _owned = owned;
        }

        public async ValueTask DisposeAsync()
        {
            if (_owned) await Conn.DisposeAsync();
        }
    }

    private async Task<int> ExecAsync(MySqlConnection conn, string sql, Action<MySqlCommand>? bind)
    {
        await using var cmd = NewCmd(sql, conn);
        bind?.Invoke(cmd);
        return await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(MySqlConnection conn, string sql, Action<MySqlCommand>? bind)
    {
        await using var cmd = NewCmd(sql, conn);
        bind?.Invoke(cmd);
        var r = await cmd.ExecuteScalarAsync();
        return r is DBNull ? null : r;
    }

    // ═══ 流程定义 ═══

    public virtual async Task<ProcessDefine?> FindDefineByIdAsync(long? defineId)
    {
        if (defineId == null) return null;
        await using var lease = await RentAsync();
        const string sql = "SELECT id, name, display_name, type, state, content, version, " +
                           "create_time, create_user, update_time, update_user FROM wf_process_define WHERE id = ?";
        await using var cmd = NewCmd(sql, lease.Conn);
        cmd.Parameters.Add(new MySqlParameter { Value = defineId.Value });
        await using var rs = await cmd.ExecuteReaderAsync();
        if (!await rs.ReadAsync()) return null;
        return new ProcessDefine
        {
            Id = rs.GetInt64("id"),
            Name = rs.GetString("name"),
            DisplayName = GetStr(rs, "display_name"),
            Type = GetStr(rs, "type"),
            State = GetInt(rs, "state"),
            Content = GetBytes(rs, "content"),
            Version = GetInt(rs, "version"),
            CreateTime = GetDateTime(rs, "create_time"),
            CreateUser = GetStr(rs, "create_user"),
            UpdateTime = GetDateTime(rs, "update_time"),
            UpdateUser = GetStr(rs, "update_user"),
        };
    }

    public virtual async Task SaveDefineAsync(ProcessDefine define)
    {
        if (define.Id == null) define.Id = IdGen.NextId();
        await using var lease = await RentAsync();
        const string sql = "INSERT INTO wf_process_define " +
                           "(id, name, display_name, type, state, content, version, create_time, create_user, update_time, update_user) " +
                           "VALUES (?,?,?,?,?,?,?,?,?,?,?)";
        await ExecAsync(lease.Conn, sql, cmd =>
        {
            cmd.Parameters.Add(new MySqlParameter { Value = define.Id!.Value });
            cmd.Parameters.Add(new MySqlParameter { Value = define.Name ?? "" });
            cmd.Parameters.Add(new MySqlParameter { Value = (object?)define.DisplayName ?? DBNull.Value });
            cmd.Parameters.Add(new MySqlParameter { Value = (object?)define.Type ?? DBNull.Value });
            cmd.Parameters.Add(new MySqlParameter { Value = define.State ?? 1 });
            cmd.Parameters.Add(new MySqlParameter { Value = (object?)define.Content ?? DBNull.Value });
            cmd.Parameters.Add(new MySqlParameter { Value = define.Version ?? 1 });
            cmd.Parameters.Add(new MySqlParameter { Value = ToDb(define.CreateTime ?? Clock.Now) });
            cmd.Parameters.Add(new MySqlParameter { Value = (object?)define.CreateUser ?? DBNull.Value });
            cmd.Parameters.Add(new MySqlParameter { Value = ToDb(define.UpdateTime ?? Clock.Now) });
            cmd.Parameters.Add(new MySqlParameter { Value = (object?)define.UpdateUser ?? DBNull.Value });
        });
    }

    public virtual async Task UpdateDefineAsync(ProcessDefine define)
    {
        if (define.Id == null) return;
        await using var lease = await RentAsync();
        // 替换语义 version 不递增（issues/59 designRedeploy；version 列 NOT NULL 兜底 1）
        const string sql = "UPDATE wf_process_define SET name=?, display_name=?, type=?, state=?, content=?, " +
                           "version=?, update_time=?, update_user=? WHERE id=?";
        await ExecAsync(lease.Conn, sql, cmd =>
        {
            cmd.Parameters.Add(new MySqlParameter { Value = define.Name ?? "" });
            cmd.Parameters.Add(new MySqlParameter { Value = (object?)define.DisplayName ?? DBNull.Value });
            cmd.Parameters.Add(new MySqlParameter { Value = (object?)define.Type ?? DBNull.Value });
            cmd.Parameters.Add(new MySqlParameter { Value = define.State ?? 1 });
            cmd.Parameters.Add(new MySqlParameter { Value = (object?)define.Content ?? DBNull.Value });
            cmd.Parameters.Add(new MySqlParameter { Value = define.Version ?? 1 });
            cmd.Parameters.Add(new MySqlParameter { Value = ToDb(Clock.Now) });
            cmd.Parameters.Add(new MySqlParameter { Value = (object?)define.UpdateUser ?? DBNull.Value });
            cmd.Parameters.Add(new MySqlParameter { Value = define.Id!.Value });
        });
    }

    public virtual async Task UpdateDefineStateAsync(long defineId, int state)
    {
        await using var lease = await RentAsync();
        await ExecAsync(lease.Conn,
            "UPDATE wf_process_define SET state=?, update_time=? WHERE id=?",
            cmd =>
            {
                cmd.Parameters.Add(new MySqlParameter { Value = state });
                cmd.Parameters.Add(new MySqlParameter { Value = ToDb(Clock.Now) });
                cmd.Parameters.Add(new MySqlParameter { Value = defineId });
            });
    }

    public virtual async Task RemoveDefineAsync(long defineId)
    {
        await using var lease = await RentAsync();
        await ExecAsync(lease.Conn, "DELETE FROM wf_process_define WHERE id=?",
            cmd => cmd.Parameters.Add(new MySqlParameter { Value = defineId }));
    }

    // ═══ 流程实例 ═══

    public virtual async Task<ProcessInstance?> FindInstanceByIdAsync(long? instanceId)
    {
        if (instanceId == null) return null;
        await using var lease = await RentAsync();
        const string sql = "SELECT id, parent_id, process_define_id, state, parent_node_name, business_no, " +
                           "operator, expire_time, variable, create_time, create_user, update_time, update_user " +
                           "FROM wf_process_instance WHERE id = ?";
        await using var cmd = NewCmd(sql, lease.Conn);
        cmd.Parameters.Add(new MySqlParameter { Value = instanceId.Value });
        await using var rs = await cmd.ExecuteReaderAsync();
        if (!await rs.ReadAsync()) return null;
        var inst = MapInstance(rs);
        await rs.DisposeAsync();
        // issues/89 聚合水合：加载关联任务（ORDER BY create_time ASC，与 JDBC 同）
        inst.Tasks = await FindTasksInternalAsync(lease.Conn, instanceId.Value, null, null);
        return inst;
    }

    public virtual async Task SaveInstanceAsync(ProcessInstance instance)
    {
        if (instance.InstanceId == null) instance.InstanceId = IdGen.NextId();
        await using var lease = await RentAsync();
        const string sql = "INSERT INTO wf_process_instance " +
                           "(id, parent_id, process_define_id, state, parent_node_name, business_no, operator, " +
                           "expire_time, variable, create_time, create_user, update_time, update_user) " +
                           "VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)";
        await ExecAsync(lease.Conn, sql, cmd => BindInstance(cmd, instance));
    }

    public virtual async Task UpdateInstanceAsync(ProcessInstance instance)
    {
        if (instance.InstanceId == null) return;
        await using var lease = await RentAsync();
        var conn = lease.Conn;
        const string sql = "UPDATE wf_process_instance SET state=?, parent_node_name=?, expire_time=?, " +
                           "variable=?, update_time=?, update_user=? WHERE id=?";
        await ExecAsync(conn, sql, cmd =>
        {
            cmd.Parameters.Add(new MySqlParameter { Value = instance.State ?? 10 });
            cmd.Parameters.Add(new MySqlParameter { Value = (object?)instance.ParentNodeName ?? DBNull.Value });
            cmd.Parameters.Add(new MySqlParameter { Value = ToDb(instance.ExpireTime) });
            cmd.Parameters.Add(new MySqlParameter { Value = ToJson(instance.Variables) });
            cmd.Parameters.Add(new MySqlParameter { Value = ToDb(Clock.Now) });
            cmd.Parameters.Add(new MySqlParameter { Value = (object?)instance.UpdateUser ?? DBNull.Value });
            cmd.Parameters.Add(new MySqlParameter { Value = instance.InstanceId.Value });
        });
        // v1.0.1：级联持久化聚合内任务状态（同连接保证一致）
        if (instance.Tasks != null)
        {
            foreach (var task in instance.Tasks)
            {
                if (task.TaskId != null) await UpdateTaskWithConnAsync(conn, task);
            }
        }
    }

    // ═══ 流程任务 ═══

    public virtual async Task<ProcessTask?> FindTaskByIdAsync(long? taskId)
    {
        if (taskId == null) return null;
        await using var lease = await RentAsync();
        const string sql = "SELECT * FROM wf_process_task WHERE id = ?";
        await using var cmd = NewCmd(sql, lease.Conn);
        cmd.Parameters.Add(new MySqlParameter { Value = taskId.Value });
        await using var rs = await cmd.ExecuteReaderAsync();
        if (!await rs.ReadAsync()) return null;
        var task = MapTask(rs);
        await rs.DisposeAsync();
        task.ActorIds = await FindTaskActorsInternalAsync(lease.Conn, taskId.Value);
        return task;
    }

    public virtual async Task SaveTaskAsync(ProcessTask task)
    {
        if (task.TaskId == null) task.TaskId = IdGen.NextId();
        await using var lease = await RentAsync();
        const string sql = "INSERT INTO wf_process_task " +
                           "(id, process_instance_id, task_name, display_name, task_type, perform_type, task_state, " +
                           "operator, finish_time, expire_time, form_key, task_parent_id, variable, " +
                           "create_time, create_user, update_time, update_user) " +
                           "VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)";
        await ExecAsync(lease.Conn, sql, cmd =>
        {
            BindTask(cmd, task);
        });
        // 同步参与人（全量覆盖语义）
        await SaveTaskActorsAsync(lease.Conn, task.TaskId.Value, task.ActorIds, task.CreateUser);
    }

    public virtual async Task UpdateTaskAsync(ProcessTask task)
    {
        if (task.TaskId == null) return;
        await using var lease = await RentAsync();
        await UpdateTaskWithConnAsync(lease.Conn, task);
    }

    private async Task UpdateTaskWithConnAsync(MySqlConnection conn, ProcessTask task)
    {
        if (task.TaskId == null) return;
        const string sql = "UPDATE wf_process_task SET task_state=?, operator=?, finish_time=?, expire_time=?, " +
                           "variable=?, update_time=?, update_user=? WHERE id=?";
        await ExecAsync(conn, sql, cmd =>
        {
            cmd.Parameters.Add(new MySqlParameter { Value = task.TaskState ?? 10 });
            cmd.Parameters.Add(new MySqlParameter { Value = (object?)task.ActorId ?? DBNull.Value });
            cmd.Parameters.Add(new MySqlParameter { Value = ToDb(task.FinishTime) });
            cmd.Parameters.Add(new MySqlParameter { Value = ToDb(task.ExpireTime) });
            cmd.Parameters.Add(new MySqlParameter { Value = ToJson(task.Variables) });
            cmd.Parameters.Add(new MySqlParameter { Value = ToDb(Clock.Now) });
            cmd.Parameters.Add(new MySqlParameter { Value = (object?)task.UpdateUser ?? DBNull.Value });
            cmd.Parameters.Add(new MySqlParameter { Value = task.TaskId.Value });
        });
        await SaveTaskActorsAsync(conn, task.TaskId.Value, task.ActorIds, task.UpdateUser);
    }

    public virtual async Task<List<ProcessTask>> FindDoingTasksAsync(long instanceId, string[]? taskNames) =>
        await QueryInstanceTasksAsync(instanceId, (int)WfTaskState.Doing, taskNames);

    public virtual async Task<List<ProcessTask>> FindDoneTasksAsync(long instanceId, string[]? taskNames) =>
        await QueryInstanceTasksAsync(instanceId, null, taskNames, doneOnly: true);

    public virtual async Task<List<ProcessTask>> FindHistoryTasksAsync(long instanceId) =>
        await QueryInstanceTasksAsync(instanceId, null, null);

    private async Task<List<ProcessTask>> QueryInstanceTasksAsync(
        long instanceId, int? state, string[]? taskNames, bool doneOnly = false)
    {
        await using var lease = await RentAsync();
        var tasks = await FindTasksInternalAsync(lease.Conn, instanceId, state, taskNames, doneOnly);
        return tasks;
    }

    private async Task<List<ProcessTask>> FindTasksInternalAsync(
        MySqlConnection conn, long instanceId, int? state, string[]? taskNames, bool doneOnly = false)
    {
        var sql = new StringBuilder("SELECT * FROM wf_process_task WHERE process_instance_id = ?");
        var bind = new List<object?> { instanceId };
        if (state != null)
        {
            sql.Append(" AND task_state = ?");
            bind.Add(state.Value);
        }
        if (doneOnly)
        {
            sql.Append(" AND task_state <> 10");
        }
        if (taskNames is { Length: > 0 })
        {
            sql.Append(" AND task_name IN (");
            for (var i = 0; i < taskNames.Length; i++) sql.Append(i == 0 ? "?" : ",?");
            sql.Append(')');
            bind.AddRange(taskNames);
        }
        sql.Append(" ORDER BY create_time ASC");
        var tasks = new List<ProcessTask>();
        await using var cmd = NewCmd(sql.ToString(), conn);
        foreach (var b in bind) cmd.Parameters.Add(new MySqlParameter { Value = b ?? DBNull.Value });
        await using var rs = await cmd.ExecuteReaderAsync();
        while (await rs.ReadAsync()) tasks.Add(MapTask(rs));
        await rs.DisposeAsync();
        foreach (var t in tasks)
        {
            if (t.TaskId != null) t.ActorIds = await FindTaskActorsInternalAsync(conn, t.TaskId.Value);
        }
        return tasks;
    }

    // ═══ 抄送 ═══

    public virtual async Task CreateCcInstanceAsync(long instanceId, string creator, params string[] actorIds)
    {
        await using var lease = await RentAsync();
        const string sql = "INSERT INTO wf_process_cc_instance " +
                           "(id, process_instance_id, actor_id, state, create_time, create_user, update_time, update_user) " +
                           "VALUES (?,?,?,0,?,?,?,?)";
        var now = ToDb(Clock.Now);
        foreach (var actorId in actorIds)
        {
            var id = IdGen.NextId();
            await ExecAsync(lease.Conn, sql, cmd =>
            {
                cmd.Parameters.Add(new MySqlParameter { Value = id });
                cmd.Parameters.Add(new MySqlParameter { Value = instanceId });
                cmd.Parameters.Add(new MySqlParameter { Value = actorId });
                cmd.Parameters.Add(new MySqlParameter { Value = now });
                cmd.Parameters.Add(new MySqlParameter { Value = creator });
                cmd.Parameters.Add(new MySqlParameter { Value = now });
                cmd.Parameters.Add(new MySqlParameter { Value = creator });
            });
        }
    }

    public virtual async Task UpdateCcStatusAsync(long instanceId, string actorId)
    {
        await using var lease = await RentAsync();
        await ExecAsync(lease.Conn,
            "UPDATE wf_process_cc_instance SET state=1, update_time=? WHERE process_instance_id=? AND actor_id=?",
            cmd =>
            {
                cmd.Parameters.Add(new MySqlParameter { Value = ToDb(Clock.Now) });
                cmd.Parameters.Add(new MySqlParameter { Value = instanceId });
                cmd.Parameters.Add(new MySqlParameter { Value = actorId });
            });
    }

    // ═══ 参与人 ═══

    public virtual async Task<List<string>> FindTaskActorsAsync(long taskId)
    {
        await using var lease = await RentAsync();
        return await FindTaskActorsInternalAsync(lease.Conn, taskId);
    }

    private async Task<List<string>> FindTaskActorsInternalAsync(MySqlConnection conn, long taskId)
    {
        const string sql = "SELECT actor_id FROM wf_process_task_actor WHERE process_task_id = ? ORDER BY id ASC";
        var actors = new List<string>();
        await using var cmd = NewCmd(sql, conn);
        cmd.Parameters.Add(new MySqlParameter { Value = taskId });
        await using var rs = await cmd.ExecuteReaderAsync();
        while (await rs.ReadAsync())
        {
            var s = GetStr(rs, "actor_id");
            if (s != null) actors.Add(s);
        }
        return actors;
    }

    public virtual async Task AddTaskActorAsync(long taskId, List<string> actors)
    {
        await using var lease = await RentAsync();
        // 去重追加（对齐 JDBC addTaskActor：existing 差集插入）
        var existing = await FindTaskActorsInternalAsync(lease.Conn, taskId);
        var toAdd = actors.Where(a => !existing.Contains(a)).ToList();
        await InsertTaskActorsAsync(lease.Conn, taskId, toAdd, null);
    }

    public virtual async Task RemoveTaskActorAsync(long taskId, List<string> actors)
    {
        await using var lease = await RentAsync();
        var inList = string.Join(",", actors.Select((_, i) => $"@a{i}"));
        await ExecAsync(lease.Conn,
            $"DELETE FROM wf_process_task_actor WHERE process_task_id = ? AND actor_id IN ({inList})",
            cmd =>
            {
                cmd.Parameters.Add(new MySqlParameter { Value = taskId });
                for (var i = 0; i < actors.Count; i++)
                    cmd.Parameters.Add(new MySqlParameter($"@a{i}", actors[i]));
            });
    }

    /// <summary>全量覆盖语义（saveTask/updateTask 同步"任务聚合副本"的参与者）。</summary>
    private async Task SaveTaskActorsAsync(MySqlConnection conn, long taskId, List<string>? actors, string? createUser)
    {
        if (actors == null || actors.Count == 0) return;
        await ExecAsync(conn, "DELETE FROM wf_process_task_actor WHERE process_task_id = ?",
            cmd => cmd.Parameters.Add(new MySqlParameter { Value = taskId }));
        await InsertTaskActorsAsync(conn, taskId, actors, createUser);
    }

    private async Task InsertTaskActorsAsync(MySqlConnection conn, long taskId, List<string>? actors, string? createUser)
    {
        if (actors == null || actors.Count == 0) return;
        const string sql = "INSERT INTO wf_process_task_actor (id, process_task_id, actor_id, create_time, create_user) VALUES (?,?,?,?,?)";
        foreach (var actorId in actors)
        {
            var id = IdGen.NextId();
            var now = ToDb(Clock.Now);
            await ExecAsync(conn, sql, cmd =>
            {
                cmd.Parameters.Add(new MySqlParameter { Value = id });
                cmd.Parameters.Add(new MySqlParameter { Value = taskId });
                cmd.Parameters.Add(new MySqlParameter { Value = actorId });
                cmd.Parameters.Add(new MySqlParameter { Value = now });
                cmd.Parameters.Add(new MySqlParameter { Value = (object?)createUser ?? DBNull.Value });
            });
        }
    }

    // ═══ 前端分页查询（五键 + 白名单 + 内联 LIMIT/OFFSET）═══

    public virtual Task<PageResult<IProcessRepository.TaskRow>> PageTodoTasksAsync(PageQuery query) =>
        PageTasksAsync(query, done: false);

    public virtual Task<PageResult<IProcessRepository.TaskRow>> PageDoneTasksAsync(PageQuery query) =>
        PageTasksAsync(query, done: true);

    public virtual Task<PageResult<IProcessRepository.InstanceRow>> PageInstancesAsync(PageQuery query) =>
        PageInstancesAsync(query, cc: false);

    public virtual Task<PageResult<IProcessRepository.InstanceRow>> PageCcInstancesAsync(PageQuery query) =>
        PageInstancesAsync(query, cc: true);

    public virtual async Task<PageResult<IProcessRepository.DefineRow>> PageDefinesAsync(PageQuery query)
    {
        await using var lease = await RentAsync();
        var conn = lease.Conn;
        var where = new StringBuilder(" FROM wf_process_define t WHERE 1=1");
        var bind = new List<object?>();
        BuildWhere(where, bind, query, DefineWhitelist);

        var total = Convert.ToInt32(
            await ScalarAsync(conn, "SELECT COUNT(*)" + where,
                cmd => { foreach (var b in bind) cmd.Parameters.Add(new MySqlParameter { Value = b ?? DBNull.Value }); }));

        var order = BuildOrder(query, "t.", DefineWhitelist);
        var limitSql = where + order + $" LIMIT {SafeLimit(query.PageSize)} OFFSET {SafeOffset(query.PageNum, query.PageSize)}";
        var dataBind = new List<object?>(bind);
        const string dataSelect = "SELECT t.*";
        var rows = new List<IProcessRepository.DefineRow>();
        await using var cmd = NewCmd(dataSelect + limitSql, conn);
        foreach (var b in dataBind) cmd.Parameters.Add(new MySqlParameter { Value = b ?? DBNull.Value });
        await using var rs = await cmd.ExecuteReaderAsync();
        while (await rs.ReadAsync())
        {
            rows.Add(new IProcessRepository.DefineRow
            {
                Id = GetLong(rs, "id"),
                Name = GetStr(rs, "name"),
                DisplayName = GetStr(rs, "display_name"),
                Type = GetStr(rs, "type"),
                State = GetInt(rs, "state"),
                Version = GetInt(rs, "version"),
                CreateTime = GetDateTime(rs, "create_time"),
                CreateUser = GetStr(rs, "create_user"),
                UpdateTime = GetDateTime(rs, "update_time"),
                UpdateUser = GetStr(rs, "update_user"),
            });
        }
        return PageResult<IProcessRepository.DefineRow>.Of(query.PageNum, query.PageSize, total, rows);
    }

    private async Task<PageResult<IProcessRepository.TaskRow>> PageTasksAsync(PageQuery query, bool done)
    {
        await using var lease = await RentAsync();
        var conn = lease.Conn;
        var where = new StringBuilder(
            " FROM wf_process_task t " +
            "LEFT JOIN wf_process_instance pi ON t.process_instance_id = pi.id " +
            "LEFT JOIN wf_process_define pd ON pi.process_define_id = pd.id " +
            "LEFT JOIN wf_process_task_actor pta ON t.id = pta.process_task_id " +
            "WHERE 1=1");
        var bind = new List<object?>();
        where.Append(done ? " AND t.task_state <> 10" : " AND t.task_state = 10");
        BuildWhere(where, bind, query, TaskWhitelist);

        var total = Convert.ToInt32(
            await ScalarAsync(conn, "SELECT COUNT(DISTINCT t.id)" + where,
                cmd => { foreach (var b in bind) cmd.Parameters.Add(new MySqlParameter { Value = b ?? DBNull.Value }); }));

        var order = BuildOrder(query, "t.", TaskWhitelist);
        var limitSql = where + order + $" LIMIT {SafeLimit(query.PageSize)} OFFSET {SafeOffset(query.PageNum, query.PageSize)}";
        const string dataSelect =
            "SELECT DISTINCT t.*, pd.name AS process_define_name, pd.display_name AS process_define_display_name, " +
            "pd.version AS process_define_version, pi.variable AS instance_variable, pi.create_time AS instance_create_time";
        var rows = new List<IProcessRepository.TaskRow>();
        await using var cmd = NewCmd(dataSelect + limitSql, conn);
        foreach (var b in bind) cmd.Parameters.Add(new MySqlParameter { Value = b ?? DBNull.Value });
        await using var rs = await cmd.ExecuteReaderAsync();
        while (await rs.ReadAsync())
        {
            rows.Add(new IProcessRepository.TaskRow
            {
                Id = GetLong(rs, "id"),
                ProcessInstanceId = GetLong(rs, "process_instance_id"),
                TaskName = GetStr(rs, "task_name"),
                DisplayName = GetStr(rs, "display_name"),
                TaskType = GetInt(rs, "task_type"),
                PerformType = GetInt(rs, "perform_type"),
                TaskState = GetInt(rs, "task_state"),
                Operator = GetStr(rs, "operator"),
                FinishTime = GetDateTime(rs, "finish_time"),
                ExpireTime = GetDateTime(rs, "expire_time"),
                FormKey = GetStr(rs, "form_key"),
                TaskParentId = GetLong(rs, "task_parent_id"),
                Variable = GetStr(rs, "variable"),
                CreateTime = GetDateTime(rs, "create_time"),
                CreateUser = GetStr(rs, "create_user"),
                UpdateTime = GetDateTime(rs, "update_time"),
                UpdateUser = GetStr(rs, "update_user"),
                ProcessDefineName = GetStr(rs, "process_define_name"),
                ProcessDefineDisplayName = GetStr(rs, "process_define_display_name"),
                ProcessDefineVersion = GetInt(rs, "process_define_version"),
                InstanceVariable = GetStr(rs, "instance_variable"),
                InstanceCreateTime = GetDateTime(rs, "instance_create_time"),
            });
        }
        return PageResult<IProcessRepository.TaskRow>.Of(query.PageNum, query.PageSize, total, rows);
    }

    private async Task<PageResult<IProcessRepository.InstanceRow>> PageInstancesAsync(PageQuery query, bool cc)
    {
        await using var lease = await RentAsync();
        var conn = lease.Conn;
        var where = new StringBuilder(
            " FROM wf_process_instance t LEFT JOIN wf_process_define pd ON t.process_define_id = pd.id ");
        if (cc) where.Append("LEFT JOIN wf_process_cc_instance cc ON t.id = cc.process_instance_id ");
        where.Append("WHERE 1=1");
        var bind = new List<object?>();
        BuildWhere(where, bind, query, cc ? CcInstanceWhitelist : InstanceWhitelist);

        var total = Convert.ToInt32(
            await ScalarAsync(conn, "SELECT COUNT(*)" + where,
                cmd => { foreach (var b in bind) cmd.Parameters.Add(new MySqlParameter { Value = b ?? DBNull.Value }); }));

        var order = BuildOrder(query, "t.", cc ? CcInstanceWhitelist : InstanceWhitelist);
        var limitSql = where + order + $" LIMIT {SafeLimit(query.PageSize)} OFFSET {SafeOffset(query.PageNum, query.PageSize)}";
        // C29：跨表 SELECT 列加别名（pd_ 前缀）防后列覆盖
        const string dataSelect =
            "SELECT pd.name AS pd_name, pd.display_name AS pd_display_name, pd.version AS pd_version, t.*";
        var rows = new List<IProcessRepository.InstanceRow>();
        await using var cmd = NewCmd(dataSelect + limitSql, conn);
        foreach (var b in bind) cmd.Parameters.Add(new MySqlParameter { Value = b ?? DBNull.Value });
        await using var rs = await cmd.ExecuteReaderAsync();
        while (await rs.ReadAsync())
        {
            rows.Add(new IProcessRepository.InstanceRow
            {
                Id = GetLong(rs, "id"),
                ParentId = GetLong(rs, "parent_id"),
                ProcessDefineId = GetLong(rs, "process_define_id"),
                State = GetInt(rs, "state"),
                ParentNodeName = GetStr(rs, "parent_node_name"),
                BusinessNo = GetStr(rs, "business_no"),
                Operator = GetStr(rs, "operator"),
                ExpireTime = GetDateTime(rs, "expire_time"),
                Variable = GetStr(rs, "variable"),
                CreateTime = GetDateTime(rs, "create_time"),
                CreateUser = GetStr(rs, "create_user"),
                UpdateTime = GetDateTime(rs, "update_time"),
                UpdateUser = GetStr(rs, "update_user"),
                ProcessDefineName = GetStr(rs, "pd_name"),
                ProcessDefineDisplayName = GetStr(rs, "pd_display_name"),
                ProcessDefineVersion = GetInt(rs, "pd_version"),
            });
        }
        return PageResult<IProcessRepository.InstanceRow>.Of(query.PageNum, query.PageSize, total, rows);
    }

    public virtual async Task<int> CountTodoTasksAsync(long? userId)
    {
        await using var lease = await RentAsync();
        const string sql = "SELECT COUNT(*) FROM wf_process_task t " +
                           "LEFT JOIN wf_process_task_actor pta ON t.id = pta.process_task_id " +
                           "WHERE t.task_state = 10 AND pta.actor_id = ?";
        var r = await ScalarAsync(lease.Conn, sql,
            cmd => cmd.Parameters.Add(new MySqlParameter { Value = userId?.ToString() ?? "" }));
        return Convert.ToInt32(r ?? 0);
    }

    // ═══ 统计查询（全纯列，C23）═══

    private const string InstanceTimeFieldWhitelist = "create_time";

    public virtual async Task<List<IProcessRepository.InstanceStatsRow>> QueryInstancesForStatsAsync(
        List<int>? stateIn, string timeField, DateTime? start, DateTime? end)
    {
        await using var lease = await RentAsync();
        var sql = new StringBuilder("SELECT id, state, create_time, process_define_id, operator FROM wf_process_instance WHERE 1=1");
        var bind = new List<object?>();
        if (stateIn is { Count: > 0 })
        {
            sql.Append(" AND state IN (").Append(string.Join(",", stateIn.Select(_ => "?"))).Append(')');
            bind.AddRange(stateIn.Cast<object?>());
        }
        var tf = timeField == InstanceTimeFieldWhitelist ? timeField : "create_time"; // 白名单
        if (start != null)
        {
            sql.Append(" AND ").Append(tf).Append(" >= ?");
            bind.Add(ToDb(start));
        }
        if (end != null)
        {
            sql.Append(" AND ").Append(tf).Append(" < ?");
            bind.Add(ToDb(end.Value.AddSeconds(1))); // JDBC 语义：end+1s 开区间
        }
        var rows = new List<IProcessRepository.InstanceStatsRow>();
        await using var cmd = NewCmd(sql.ToString(), lease.Conn);
        foreach (var b in bind) cmd.Parameters.Add(new MySqlParameter { Value = b ?? DBNull.Value });
        await using var rs = await cmd.ExecuteReaderAsync();
        while (await rs.ReadAsync())
        {
            rows.Add(new IProcessRepository.InstanceStatsRow
            {
                Id = GetLong(rs, "id"),
                State = GetInt(rs, "state"),
                CreateTime = GetDateTime(rs, "create_time"),
                ProcessDefineId = GetLong(rs, "process_define_id"),
                Operator = GetStr(rs, "operator"),
            });
        }
        return rows;
    }

    public virtual async Task<List<IProcessRepository.TaskStatsRow>> QueryTasksForStatsAsync(
        int? state, DateTime? start, DateTime? end)
    {
        await using var lease = await RentAsync();
        var sql = new StringBuilder(
            "SELECT id, process_instance_id, task_state, perform_type, operator, display_name, " +
            "create_time, finish_time, expire_time FROM wf_process_task WHERE 1=1");
        var bind = new List<object?>();
        if (state != null)
        {
            sql.Append(" AND task_state = ?");
            bind.Add(state.Value);
        }
        var timeCol = state == (int)WfTaskState.Finished ? "finish_time" : "create_time";
        if (start != null)
        {
            sql.Append(" AND ").Append(timeCol).Append(" >= ?");
            bind.Add(ToDb(start));
        }
        if (end != null)
        {
            sql.Append(" AND ").Append(timeCol).Append(" < ?");
            bind.Add(ToDb(end.Value.AddSeconds(1)));
        }
        var rows = new List<IProcessRepository.TaskStatsRow>();
        await using var cmd = NewCmd(sql.ToString(), lease.Conn);
        foreach (var b in bind) cmd.Parameters.Add(new MySqlParameter { Value = b ?? DBNull.Value });
        await using var rs = await cmd.ExecuteReaderAsync();
        while (await rs.ReadAsync())
        {
            rows.Add(new IProcessRepository.TaskStatsRow
            {
                Id = GetLong(rs, "id"),
                ProcessInstanceId = GetLong(rs, "process_instance_id"),
                TaskState = GetInt(rs, "task_state"),
                PerformType = GetInt(rs, "perform_type"),
                Operator = GetStr(rs, "operator"),
                DisplayName = GetStr(rs, "display_name"),
                CreateTime = GetDateTime(rs, "create_time"),
                FinishTime = GetDateTime(rs, "finish_time"),
                ExpireTime = GetDateTime(rs, "expire_time"),
            });
        }
        return rows;
    }

    public virtual async Task<int> StatsAvgCompletedDurationSecondsAsync(DateTime? start, DateTime? end)
    {
        await using var lease = await RentAsync();
        var sql = new StringBuilder(
            "SELECT ROUND(AVG(dur)) FROM (" +
            " SELECT TIMESTAMPDIFF(SECOND, i.create_time, MAX(t.finish_time)) AS dur" +
            " FROM wf_process_instance i" +
            " JOIN wf_process_task t ON t.process_instance_id = i.id" +
            " WHERE i.state = 20");
        var bind = new List<object?>();
        if (start != null)
        {
            sql.Append(" AND i.create_time >= ?");
            bind.Add(ToDb(start));
        }
        if (end != null)
        {
            sql.Append(" AND i.create_time < DATE_ADD(?, INTERVAL 1 SECOND)");
            bind.Add(ToDb(end));
        }
        sql.Append(" GROUP BY i.id) x WHERE dur IS NOT NULL");
        var r = await ScalarAsync(lease.Conn, sql.ToString(),
            cmd => { foreach (var b in bind) cmd.Parameters.Add(new MySqlParameter { Value = b ?? DBNull.Value }); });
        return r == null ? 0 : Convert.ToInt32(r);
    }

    public virtual async Task<int[]> StatsPendingAndOverdueCountAsync()
    {
        await using var lease = await RentAsync();
        const string sql = "SELECT COUNT(*) AS pending, " +
                           "SUM(CASE WHEN expire_time IS NOT NULL AND expire_time < NOW() THEN 1 ELSE 0 END) AS overdue " +
                           "FROM wf_process_task WHERE task_state = 10";
        await using var cmd = NewCmd(sql, lease.Conn);
        await using var rs = await cmd.ExecuteReaderAsync();
        if (!await rs.ReadAsync()) return new[] { 0, 0 };
        var pending = rs.GetInt32("pending");
        var overdue = rs.IsDBNull(rs.GetOrdinal("overdue")) ? 0 : rs.GetInt32("overdue");
        return new[] { pending, overdue };
    }

    public virtual async Task<int[]> StatsCompletedTaskAggregateAsync()
    {
        await using var lease = await RentAsync();
        const string sql = "SELECT COUNT(*) AS total, " +
                           "SUM(CASE WHEN perform_type = 1 THEN 1 ELSE 0 END) AS countersign, " +
                           "SUM(CASE WHEN expire_time IS NOT NULL AND finish_time <= expire_time THEN 1 ELSE 0 END) AS ontime, " +
                           "SUM(CASE WHEN expire_time IS NOT NULL THEN 1 ELSE 0 END) AS ontimedenom " +
                           "FROM wf_process_task WHERE task_state = 20";
        await using var cmd = NewCmd(sql, lease.Conn);
        await using var rs = await cmd.ExecuteReaderAsync();
        if (!await rs.ReadAsync()) return new[] { 0, 0, 0, 0 };
        int Get(string col) => rs.IsDBNull(rs.GetOrdinal(col)) ? 0 : rs.GetInt32(col);
        return new[] { Get("total"), Get("countersign"), Get("ontime"), Get("ontimedenom") };
    }

    public virtual async Task<List<Dictionary<string, object?>>> StatsStuckNodeGroupAsync(int limit)
    {
        await using var lease = await RentAsync();
        const string sql = "SELECT display_name AS `key`, COUNT(*) AS `count` " +
                           "FROM wf_process_task WHERE task_state = 10 " +
                           "GROUP BY display_name ORDER BY `count` DESC ";
        var rows = new List<Dictionary<string, object?>>();
        await using var cmd = NewCmd(sql + $"LIMIT {SafeLimit(limit)}", lease.Conn);
        await using var rs = await cmd.ExecuteReaderAsync();
        while (await rs.ReadAsync())
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["key"] = GetStr(rs, "key"),
                ["count"] = rs.GetInt32("count"),
            });
        }
        return rows;
    }

    public virtual async Task<List<Dictionary<string, object?>>> StatsStuckApproverGroupAsync(int limit)
    {
        await using var lease = await RentAsync();
        const string sql = "SELECT a.actor_id AS `key`, COUNT(*) AS `count` " +
                           "FROM wf_process_task_actor a " +
                           "JOIN wf_process_task t ON t.id = a.process_task_id " +
                           "WHERE t.task_state = 10 " +
                           "GROUP BY a.actor_id ORDER BY `count` DESC ";
        var rows = new List<Dictionary<string, object?>>();
        await using var cmd = NewCmd(sql + $"LIMIT {SafeLimit(limit)}", lease.Conn);
        await using var rs = await cmd.ExecuteReaderAsync();
        while (await rs.ReadAsync())
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["key"] = GetStr(rs, "key"),
                ["count"] = rs.GetInt32("count"),
            });
        }
        return rows;
    }

    public virtual async Task<List<Dictionary<string, object?>>> StatsDefineGroupAsync(
        DateTime? start, DateTime? end, int limit)
    {
        await using var lease = await RentAsync();
        // D 口径：count 全实例不过滤 state、avg 仅对 state=20 实例聚合、inner join define、子查询取每实例 MAX(finish_time)
        var sql = new StringBuilder(
            "SELECT pd.name AS `key`, pd.display_name AS label, COUNT(*) AS `count`, " +
            "ROUND(AVG(CASE WHEN i.state = 20 AND sub.maxft IS NOT NULL " +
            "THEN TIMESTAMPDIFF(SECOND, i.create_time, sub.maxft) END)) AS avgDurationSeconds " +
            "FROM wf_process_instance i " +
            "JOIN wf_process_define pd ON i.process_define_id = pd.id " +
            "LEFT JOIN (SELECT process_instance_id, MAX(finish_time) AS maxft " +
            "FROM wf_process_task GROUP BY process_instance_id) sub ON sub.process_instance_id = i.id");
        var bind = new List<object?>();
        if (start != null)
        {
            sql.Append(" WHERE i.create_time >= ?");
            bind.Add(ToDb(start));
        }
        if (end != null)
        {
            sql.Append(start != null ? " AND " : " WHERE ").Append("i.create_time < DATE_ADD(?, INTERVAL 1 SECOND)");
            bind.Add(ToDb(end));
        }
        sql.Append(" GROUP BY pd.id, pd.name, pd.display_name ORDER BY `count` DESC ");
        var rows = new List<Dictionary<string, object?>>();
        await using var cmd = NewCmd(sql + $"LIMIT {SafeLimit(limit)}", lease.Conn);
        foreach (var b in bind) cmd.Parameters.Add(new MySqlParameter { Value = b ?? DBNull.Value });
        await using var rs = await cmd.ExecuteReaderAsync();
        while (await rs.ReadAsync())
        {
            var avg = GetLong(rs, "avgDurationSeconds");
            // issues/105：avg 出参 int
            rows.Add(new Dictionary<string, object?>
            {
                ["key"] = GetStr(rs, "key"),
                ["label"] = GetStr(rs, "label"),
                ["count"] = rs.GetInt32("count"),
                ["avgDurationSeconds"] = avg == null ? null : (int)avg,
            });
        }
        return rows;
    }

    public virtual async Task<List<int>> StatsCompletedInstanceDurationsAsync(DateTime? start, DateTime? end)
    {
        await using var lease = await RentAsync();
        var sql = new StringBuilder(
            "SELECT TIMESTAMPDIFF(SECOND, i.create_time, " +
            "(SELECT MAX(t2.finish_time) FROM wf_process_task t2 WHERE t2.process_instance_id = i.id)) AS dur " +
            "FROM wf_process_instance i " +
            "WHERE i.state = 20");
        var bind = new List<object?>();
        if (start != null)
        {
            sql.Append(" AND i.create_time >= ?");
            bind.Add(ToDb(start));
        }
        if (end != null)
        {
            sql.Append(" AND i.create_time < DATE_ADD(?, INTERVAL 1 SECOND)");
            bind.Add(ToDb(end));
        }
        var durations = new List<int>();
        await using var cmd = NewCmd(sql.ToString(), lease.Conn);
        foreach (var b in bind) cmd.Parameters.Add(new MySqlParameter { Value = b ?? DBNull.Value });
        await using var rs = await cmd.ExecuteReaderAsync();
        while (await rs.ReadAsync())
        {
            var dur = GetLong(rs, "dur");
            if (dur != null) durations.Add((int)dur);
        }
        return durations;
    }

    // ═══ buildWhere / buildOrder（白名单 + 操作符，对齐 JDBC）═══

    protected internal virtual void BuildWhere(
        StringBuilder sql, List<object?> bind, PageQuery query, HashSet<string> whitelist)
    {
        foreach (var cond in query.Conditions)
        {
            var col = cond.Column;
            if (!whitelist.Contains(col)) continue; // 不在白名单，丢弃
            var val = cond.Value;
            if (val == null || (val is string s && s.Length == 0)) continue;

            switch (cond.Operator?.ToUpperInvariant())
            {
                case "EQ":
                    sql.Append(" AND ").Append(col).Append(" = ?");
                    bind.Add(val);
                    break;
                case "NE":
                    sql.Append(" AND ").Append(col).Append(" <> ?");
                    bind.Add(val);
                    break;
                case "LIKE":
                case "LLIKE":
                case "RLIKE":
                    sql.Append(" AND ").Append(col).Append(" LIKE ?");
                    var v = val.ToString() ?? "";
                    bind.Add(cond.Operator?.ToUpperInvariant() switch
                    {
                        "LLIKE" => "%" + v,
                        "RLIKE" => v + "%",
                        _ => "%" + v + "%",
                    });
                    break;
                case "GT":
                    sql.Append(" AND ").Append(col).Append(" > ?");
                    bind.Add(val);
                    break;
                case "GE":
                    sql.Append(" AND ").Append(col).Append(" >= ?");
                    bind.Add(val);
                    break;
                case "LT":
                    sql.Append(" AND ").Append(col).Append(" < ?");
                    bind.Add(val);
                    break;
                case "LE":
                    sql.Append(" AND ").Append(col).Append(" <= ?");
                    bind.Add(val);
                    break;
                case "IN":
                case "NIN":
                    if (val is System.Collections.IEnumerable en and not string)
                    {
                        var list = en.Cast<object?>().ToList();
                        if (list.Count == 0) continue;
                        sql.Append(" AND ").Append(col)
                           .Append("IN".Equals(cond.Operator, StringComparison.OrdinalIgnoreCase) ? " IN (" : " NOT IN (");
                        for (var i = 0; i < list.Count; i++)
                        {
                            sql.Append(i == 0 ? "?" : ",?");
                            bind.Add(list[i]);
                        }
                        sql.Append(')');
                    }
                    break;
                case "BT":
                    if (val is System.Collections.IEnumerable en2 and not string)
                    {
                        var list2 = en2.Cast<object?>().ToList();
                        if (list2.Count == 2)
                        {
                            sql.Append(" AND ").Append(col).Append(" BETWEEN ? AND ?");
                            bind.Add(list2[0]);
                            bind.Add(list2[1]);
                        }
                    }
                    break;
            }
        }
    }

    protected internal virtual string BuildOrder(PageQuery query, string defaultAlias, HashSet<string> whitelist)
    {
        var orderBy = query.OrderBy;
        if (string.IsNullOrEmpty(orderBy))
            return $" ORDER BY {defaultAlias}id DESC";
        var order = new StringBuilder();
        foreach (var partRaw in orderBy.Split(','))
        {
            var part = partRaw.Trim().Split(' ');
            if (part.Length == 0 || part[0].Length == 0) continue;
            var col = part[0];
            var dir = part.Length > 1 ? part[1].ToUpperInvariant() : "ASC";
            if (!whitelist.Contains(col) && !whitelist.Contains(defaultAlias + col))
            {
                col = defaultAlias + col;
                if (!whitelist.Contains(col)) continue;
            }
            if (order.Length > 0) order.Append(", ");
            order.Append(col).Append(' ').Append(dir);
        }
        return order.Length > 0 ? $" ORDER BY {order}" : $" ORDER BY {defaultAlias}id DESC";
    }

    // C21：LIMIT/offset 内联非负整数（白名单化，非占位符路径）
    protected internal static int SafeLimit(int pageSize) => Math.Clamp(pageSize <= 0 ? 10 : pageSize, 1, 1_000_000);
    protected internal static int SafeOffset(int pageNum, int pageSize) =>
        Math.Max(pageNum <= 0 ? 0 : (pageNum - 1) * SafeLimit(pageSize), 0);

    // ═══ 白名单（对齐 JDBC 常量）═══

    protected internal static readonly HashSet<string> TaskWhitelist = new()
    {
        "t.id", "t.task_name", "t.display_name", "t.task_type", "t.perform_type", "t.task_state",
        "t.operator", "t.form_key", "t.create_time", "t.finish_time", "t.expire_time",
        "t.process_instance_id", "t.task_parent_id", "t.variable",
        "pi.id", "pi.business_no", "pi.operator", "pi.create_time", "pi.state",
        "pd.name", "pd.display_name", "pd.type",
        "pta.actor_id", "pta.process_task_id",
    };

    protected internal static readonly HashSet<string> InstanceWhitelist = new()
    {
        "t.id", "t.parent_id", "t.process_define_id", "t.state", "t.business_no",
        "t.operator", "t.create_time", "t.expire_time", "t.variable",
        "pd.name", "pd.display_name", "pd.type", "pd.version",
    };

    protected internal static readonly HashSet<string> CcInstanceWhitelist = new()
    {
        "t.id", "t.process_define_id", "t.state", "t.business_no", "t.operator",
        "t.create_time", "t.variable",
        "pd.name", "pd.display_name", "pd.type", "pd.version",
        "cc.actor_id", "cc.state",
    };

    protected internal static readonly HashSet<string> DefineWhitelist = new()
    {
        "t.id", "t.name", "t.display_name", "t.type", "t.state", "t.version",
        "t.create_time", "t.update_time",
    };

    // ═══ 绑定 / 映射 / NULL 安全读（T10：NULL→零值；DATETIME 独立解析不依赖 DSN）═══

    private void BindInstance(MySqlCommand cmd, ProcessInstance instance)
    {
        cmd.Parameters.Add(new MySqlParameter { Value = instance.InstanceId!.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = instance.ParentId.HasValue ? instance.ParentId.Value : DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = instance.DefineId.HasValue ? instance.DefineId.Value : DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = instance.State ?? 10 });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)instance.ParentNodeName ?? DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)instance.BusinessNo ?? DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)instance.Operator ?? DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = ToDb(instance.ExpireTime) });
        cmd.Parameters.Add(new MySqlParameter { Value = ToJson(instance.Variables) });
        cmd.Parameters.Add(new MySqlParameter { Value = ToDb(instance.CreateTime ?? Clock.Now) });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)instance.CreateUser ?? DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = ToDb(instance.UpdateTime ?? Clock.Now) });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)instance.UpdateUser ?? DBNull.Value });
    }

    private void BindTask(MySqlCommand cmd, ProcessTask task)
    {
        cmd.Parameters.Add(new MySqlParameter { Value = task.TaskId!.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = task.ProcessInstanceId.HasValue ? task.ProcessInstanceId.Value : DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)task.TaskName ?? DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)task.DisplayName ?? DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = task.TaskType.HasValue ? (int)task.TaskType.Value : DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = task.PerformType.HasValue ? (int)task.PerformType.Value : DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = task.TaskState ?? 10 });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)task.ActorId ?? DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = ToDb(task.FinishTime) });
        cmd.Parameters.Add(new MySqlParameter { Value = ToDb(task.ExpireTime) });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)task.FormKey ?? DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = task.ParentTaskId.HasValue ? task.ParentTaskId.Value : DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = ToJson(task.Variables) });
        cmd.Parameters.Add(new MySqlParameter { Value = ToDb(task.CreateTime ?? Clock.Now) });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)task.CreateUser ?? DBNull.Value });
        cmd.Parameters.Add(new MySqlParameter { Value = ToDb(task.UpdateTime ?? Clock.Now) });
        cmd.Parameters.Add(new MySqlParameter { Value = (object?)task.UpdateUser ?? DBNull.Value });
    }

    private static ProcessInstance MapInstance(IDataRecord rs)
    {
        var variables = new FlowData();
        var variableJson = GetStr(rs, "variable");
        if (!string.IsNullOrEmpty(variableJson) && DefaultJsonProvider.Instance.FromJson(variableJson) is Dictionary<string, object?> dict)
        {
            foreach (var kv in dict) variables[kv.Key] = kv.Value;
        }
        return new ProcessInstance
        {
            InstanceId = GetLong(rs, "id"),
            ParentId = GetLong(rs, "parent_id"),
            DefineId = GetLong(rs, "process_define_id"),
            State = GetInt(rs, "state"),
            ParentNodeName = GetStr(rs, "parent_node_name"),
            BusinessNo = GetStr(rs, "business_no"),
            Operator = GetStr(rs, "operator"),
            ExpireTime = GetDateTime(rs, "expire_time"),
            Variables = variables,
            CreateTime = GetDateTime(rs, "create_time"),
            CreateUser = GetStr(rs, "create_user"),
            UpdateTime = GetDateTime(rs, "update_time"),
            UpdateUser = GetStr(rs, "update_user"),
        };
    }

    private static ProcessTask MapTask(IDataRecord rs)
    {
        var variables = new FlowData();
        var variableJson = GetStr(rs, "variable");
        if (!string.IsNullOrEmpty(variableJson) && DefaultJsonProvider.Instance.FromJson(variableJson) is Dictionary<string, object?> dict)
        {
            foreach (var kv in dict) variables[kv.Key] = kv.Value;
        }
        var taskType = GetInt(rs, "task_type");
        var performType = GetInt(rs, "perform_type");
        return new ProcessTask
        {
            TaskId = GetLong(rs, "id"),
            ProcessInstanceId = GetLong(rs, "process_instance_id"),
            TaskName = GetStr(rs, "task_name"),
            DisplayName = GetStr(rs, "display_name"),
            TaskType = taskType == null ? null : (WfTaskType)taskType,
            PerformType = performType == null ? null : (WfPerformType)performType,
            TaskState = GetInt(rs, "task_state"),
            ActorId = GetStr(rs, "operator"),
            FinishTime = GetDateTime(rs, "finish_time"),
            ExpireTime = GetDateTime(rs, "expire_time"),
            FormKey = GetStr(rs, "form_key"),
            ParentTaskId = GetLong(rs, "task_parent_id"),
            Variables = variables,
            CreateTime = GetDateTime(rs, "create_time"),
            CreateUser = GetStr(rs, "create_user"),
            UpdateTime = GetDateTime(rs, "update_time"),
            UpdateUser = GetStr(rs, "update_user"),
        };
    }

    private string? ToJson(FlowData? data)
    {
        if (data == null || data.Count == 0) return null;
        return Json.ToJson(data);
    }

    // ── NULL 安全读 ──

    protected internal static string? GetStr(IDataRecord rs, string col)
    {
        var i = rs.GetOrdinal(col);
        return rs.IsDBNull(i) ? null : rs.GetString(i);
    }

    protected internal static long? GetLong(IDataRecord rs, string col)
    {
        var i = rs.GetOrdinal(col);
        return rs.IsDBNull(i) ? null : rs.GetInt64(i);
    }

    protected internal static int? GetInt(IDataRecord rs, string col)
    {
        var i = rs.GetOrdinal(col);
        return rs.IsDBNull(i) ? null : rs.GetInt32(i);
    }

    protected internal static DateTime? GetDateTime(IDataRecord rs, string col)
    {
        var i = rs.GetOrdinal(col);
        return rs.IsDBNull(i) ? null : rs.GetDateTime(i);
    }

    protected internal static byte[]? GetBytes(IDataRecord rs, string col)
    {
        var i = rs.GetOrdinal(col);
        return rs.IsDBNull(i) ? null : (byte[])rs.GetValue(i);
    }

    protected internal static object ToDb(DateTime? t) =>
        t.HasValue ? t.Value : DBNull.Value;
}

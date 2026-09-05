using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;
using Mldong.Jeeflow.Persist;
using Mldong.Jeeflow.Repository.MySql;
using MySqlConnector;
using System.Data.Common;

namespace Mldong.Jeeflow.Tests;

/// <summary>MySQL 测试集合：共享夹具 + 串行执行（防并行 id 冲突/互踩）。</summary>
[CollectionDefinition("mysql")]
public class MySqlTestCollection : Xunit.ICollectionFixture<MySqlFixture>
{
}

/// <summary>
/// T1 复跑（M3/M4 落库，R6 红线）：真库 ARCHIVE 归档 / SYNC+字段权限不写穿 / bizData 回显。
/// 业务表 wf_csharp_t1_*（9xxxxx 段命名）测后 DROP。
/// </summary>
[Collection("mysql")]
[Trait("Category", "mysql-smoke")]
public class PersistSmokeTests
{
    private readonly MySqlFixture _fx;

    public PersistSmokeTests(MySqlFixture fx) => _fx = fx;

    private static bool Skip =>
        Environment.GetEnvironmentVariable("SKIP_MYSQL") == "1";

    /// <summary>构造带 persist 拦截器的 facade（postInterceptors 声明名挂载，模型级）。</summary>
    private JeeflowFacade NewPersistFacade(string bizTable)
    {
        var factory = _fx.Factory;
        var writer = new DbDynamicTableWriter(new PersistConnFactory(factory), _fx.Ctx.Clock);
        var reader = new TableReader(new PersistConnFactory(factory));
        var interceptor = new PersistPostInterceptor().SetWriter(writer);
        _fx.Ctx.NamedInterceptors[PersistPostInterceptor.MetaClassName] = interceptor;
        _fx.Ctx.BizDataReader = new BizReaderAdapter(new MetaTableReader(reader, new JsonMetaProvider(null)));
        return new JeeflowFacade(_fx.Ctx);
    }

    private sealed class PersistConnFactory : IDbConnectionFactory
    {
        private readonly MySqlConnectionFactory _inner;
        public PersistConnFactory(MySqlConnectionFactory inner) => _inner = inner;
        public async Task<DbConnection> OpenAsync() => await _inner.OpenAsync();
    }

    private sealed class BizReaderAdapter : IBizDataReader
    {
        private readonly MetaTableReader _reader;
        public BizReaderAdapter(MetaTableReader reader) => _reader = reader;
        public Task<Dictionary<string, object?>?> ReadByProcessInstanceAsync(string tableName, object? processInstanceId) =>
            _reader.ReadByProcessInstanceAsync(tableName, processInstanceId);
    }

    /// <summary>建业务表 + 带 relTableName/persistMode/postInterceptors 的流程定义。</summary>
    private async Task<long> SavePersistFlowAsync(
        string flowName, string bizTable, string persistMode, string columns, string fieldPermJson)
    {
        await using var conn = await _fx.Factory.OpenAsync();
        await using (var cmd = new MySqlCommand(
            $@"CREATE TABLE IF NOT EXISTS {bizTable} (
                 id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                 process_instance_id BIGINT NULL,
                 apply_user_id VARCHAR(64) NULL,
                 days INT NULL,
                 reason VARCHAR(200) NULL,
                 approval_comment VARCHAR(200) NULL,
                 state INT NULL,
                 task1_10 INT NULL, task1_20 INT NULL,
                 create_time VARCHAR(32) NULL, create_user VARCHAR(64) NULL,
                 update_time VARCHAR(32) NULL, update_user VARCHAR(64) NULL,
                 INDEX idx_t1pi (process_instance_id)
               ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        var fieldJson = string.IsNullOrEmpty(fieldPermJson)
            ? ""
            : $",\"field\":{{{fieldPermJson}}}";
        var flow = $@"
{{""name"": ""{flowName}"", ""displayName"": ""T1-{flowName}"", ""type"": ""approval"",
  ""relTableName"": ""{bizTable}"", ""persistMode"": ""{persistMode}"",
  ""postInterceptors"": ""{PersistPostInterceptor.MetaClassName}"",
  ""nodes"": [
    {{""id"": ""start"", ""type"": ""snaker:start"", ""text"": {{""value"": ""Start""}}}},
    {{""id"": ""apply"", ""type"": ""snaker:task"", ""text"": {{""value"": ""Apply""}},
     ""properties"": {{""assignee"": ""applicant""}}}},
    {{""id"": ""task1"", ""type"": ""snaker:task"", ""text"": {{""value"": ""审批""}},
     ""properties"": {{""assignee"": ""leader""{fieldJson}}}}},
    {{""id"": ""end"", ""type"": ""snaker:end"", ""text"": {{""value"": ""End""}}}}
  ],
  ""edges"": [
    {{""id"": ""e1"", ""sourceNodeId"": ""start"", ""targetNodeId"": ""apply""}},
    {{""id"": ""e2"", ""sourceNodeId"": ""apply"", ""targetNodeId"": ""task1""}},
    {{""id"": ""e3"", ""sourceNodeId"": ""task1"", ""targetNodeId"": ""end""}}
  ]}}";
        var define = new ProcessDefine
        {
            Id = NextPersistDefineId(),
            Name = flowName,
            DisplayName = "T1-" + flowName,
            Type = "approval",
            State = 1,
            Content = System.Text.Encoding.UTF8.GetBytes(flow),
            Version = 1,
        };
        await _fx.Repo.SaveDefineAsync(define);
        return define.Id!.Value;
    }

    private long _persistDefineId = 911001;
    private long NextPersistDefineId() => Interlocked.Increment(ref _persistDefineId) - 1;

    [Fact]
    public async Task T1M3_ArchivePersistToBizTable()
    {
        if (Skip) return;
        var bizTable = $"wf_csharp_t1_arc_910001";
        var defineId = await SavePersistFlowAsync("t1-archive-probe", bizTable, "", "days INT", "");
        try
        {
            var facade = NewPersistFacade(bizTable);
            // 发起（f_ 表单）→ apply 完成 → leader 完成（AGREE）→ end → ARCHIVE INSERT
            var startResp = await facade.FlowAsync("processInstance/startAndExecute",
                new FlowData
                {
                    [FlowConst.ProcessDefineIdKey] = defineId,
                    ["operator"] = "user1",
                    ["f_days"] = 5,
                    ["f_reason"] = "年假归档",
                });
            Assert.Equal(0, startResp["code"]);
            // leader 办理（AGREE）→ end → FINISHED+AGREE → ARCHIVE INSERT
            var iidArc = Convert.ToInt64(((Dictionary<string, object?>)startResp["data"]!)["processInstanceId"]);
            var doingTasks = await _fx.Repo.FindDoingTasksAsync(iidArc, null);
            var taskArc = doingTasks.First(t => t.ActorIds.Contains("leader"));
            var execResp = await facade.FlowAsync("processTask/execute",
                new FlowData { [FlowConst.ProcessTaskIdKey] = taskArc.TaskId, ["operator"] = "leader" });
            Assert.True(Equals(0, execResp["code"]), $"exec msg={execResp["msg"]}");

            var count = await CountBizRowsAsync(bizTable);
            Assert.Equal(1, count);
            var row = await ReadBizRowAsync(bizTable);
            // 明文落库断言（T1M3 契约）
            Assert.Equal(5, Convert.ToInt32(row["days"]));
            Assert.Equal("年假归档", row["reason"]);
            Assert.Equal("user1", row["apply_user_id"]?.ToString());
        }
        finally
        {
            await DropBizTableAsync(bizTable);
            await _fx.Repo.RemoveDefineAsync(defineId);
        }
    }

    [Fact]
    public async Task T1M4_SyncFieldPermissionNotWrittenThrough()
    {
        if (Skip) return;
        var bizTable = "wf_csharp_t1_sync_910002";
        // task1 节点声明 days 只读（PERMISSION_days=1）
        var defineId = await SavePersistFlowAsync("t1-sync-probe", bizTable, "SYNC", "days INT",
            "\"PERMISSION_days\":1");
        try
        {
            var facade = NewPersistFacade(bizTable);
            // 发起（SYNC：INSERT 行）
            var startResp = await facade.FlowAsync("processInstance/startAndExecute",
                new FlowData
                {
                    [FlowConst.ProcessDefineIdKey] = defineId,
                    ["operator"] = "user1",
                    ["f_days"] = 3,
                    ["f_reason"] = "发起原因",
                });
            Assert.True(Equals(0, startResp["code"]), "start msg=" + startResp["msg"]);
            Assert.Equal(1, await CountBizRowsAsync(bizTable));

            // leader 办理：提交 days=999（只读——必须被过滤不写穿）+ reason 可编辑
            var iid = await LatestInstanceIdAsync(bizTable);
            var doingTasks = await _fx.Repo.FindDoingTasksAsync(iid, null);
            var task = doingTasks.First(t => t.ActorIds.Contains("leader"));
            await _fx.Repo.AddTaskActorAsync(task.TaskId!.Value, new List<string> { "leader" });
            var execResp = await facade.FlowAsync("processTask/execute",
                new FlowData
                {
                    [FlowConst.ProcessTaskIdKey] = task.TaskId,
                    ["operator"] = "leader",
                    ["f_days"] = 999,           // 只读 → 不入变量/不入库
                    ["f_reason"] = "办理修改",   // 可编辑 → 更新
                    ["tf_approvalComment"] = "同意备注",
                });
            Assert.True(Equals(0, execResp["code"]), $"exec msg={execResp["msg"]}");

            var row = await ReadBizRowAsync(bizTable);
            Assert.Equal(3, Convert.ToInt32(row["days"]));           // 只读不写穿（C19）
            Assert.Equal("办理修改", row["reason"]);                  // 可编辑已更新
            Assert.Equal("同意备注", row["approval_comment"]);         // tf_ 冗余落库
            Assert.Equal(10, Convert.ToInt32(row["task1_10"]));       // 状态字段 {节点ID}_{10}=DOING
        }
        finally
        {
            await DropBizTableAsync(bizTable);
            await _fx.Repo.RemoveDefineAsync(defineId);
        }
    }

    private async Task<int> CountBizRowsAsync(string table)
    {
        await using var conn = await _fx.Factory.OpenAsync();
        await using var cmd = new MySqlCommand($"SELECT COUNT(*) FROM {table}", conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task<Dictionary<string, object?>> ReadBizRowAsync(string table)
    {
        await using var conn = await _fx.Factory.OpenAsync();
        await using var cmd = new MySqlCommand($"SELECT * FROM {table} LIMIT 1", conn);
        await using var rs = await cmd.ExecuteReaderAsync();
        Assert.True(await rs.ReadAsync());
        var row = new Dictionary<string, object?>();
        for (var i = 0; i < rs.FieldCount; i++)
            row[rs.GetName(i)] = rs.IsDBNull(i) ? null : rs.GetValue(i);
        return row;
    }

    private async Task<long> LatestInstanceIdAsync(string table)
    {
        await using var conn = await _fx.Factory.OpenAsync();
        await using var cmd = new MySqlCommand(
            $"SELECT process_instance_id FROM {table} ORDER BY id DESC LIMIT 1", conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task DropBizTableAsync(string table)
    {
        try
        {
            await using var conn = await _fx.Factory.OpenAsync();
            await using var cmd = new MySqlCommand($"DROP TABLE IF EXISTS {table}", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // 清理失败不掩盖断言
        }
    }
}

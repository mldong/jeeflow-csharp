using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Persist;

namespace Mldong.Jeeflow.Tests;

/// <summary>Persist T9 全规则单测（fake writer 录制调用；真库 ARCHIVE/SYNC 落库在 T1 复跑）。</summary>
public class PersistTests
{
    /// <summary>录制型 fake writer。</summary>
    private class RecordingWriter : IDynamicTableWriter
    {
        public List<string> Calls = new();
        public List<string> Existing = new(); // ExistsAsync 命中的 (table, key, value)
        public List<string> Columns = new();  // filterColumns 白名单（列探测结果）

        public Task<List<string>> FilterColumnsAsync(string tableName, IEnumerable<string> columns)
        {
            Calls.Add($"filter:{tableName}");
            return Task.FromResult(columns.Where(Columns.Contains).ToList());
        }

        public Task<object?> InsertAsync(string tableName, IDictionary<string, object?> data)
        {
            Calls.Add($"insert:{tableName}:{string.Join(",", data.Keys.OrderBy(k => k))}");
            return Task.FromResult<object?>(1);
        }

        public Task<int> UpdateAsync(string tableName, IDictionary<string, object?> data, string whereColumn, object? whereValue)
        {
            Calls.Add($"update:{tableName}:{string.Join(",", data.Keys.OrderBy(k => k))}");
            return Task.FromResult(1);
        }

        public Task<bool> ExistsAsync(string tableName, string bizKey, object? bizKeyValue)
        {
            return Task.FromResult(Existing.Contains($"{tableName}:{bizKey}:{bizKeyValue}"));
        }

        public void FillSystemFields(IDictionary<string, object?> data, bool insert)
        {
            Calls.Add($"sys:{(insert ? "insert" : "update")}");
        }
    }

    private static (Execution Exec, RecordingWriter Writer) MakeArchiveExec(int instanceState, int? submitType)
    {
        var ctx = NewCtx();
        var inst = new ProcessInstance
        {
            InstanceId = 900001,
            Operator = "9001",
            State = instanceState,
            Variables = new FlowData { ["f_days"] = 3, ["f_reason"] = "年假", ["u_deptId"] = "d1" },
        };
        var exec = new Execution
        {
            Context = ctx,
            ProcessInstance = inst,
            Args = new FlowData(),
        };
        if (submitType != null) exec.Args[FlowConst.SubmitType] = submitType.Value;
        var writer = new RecordingWriter();
        return (exec, writer);
    }

    private static ServiceContext NewCtx()
    {
        var ctx = new ServiceContext(new MemoryRepository());
        ctx.Clock = new FixedClock(new DateTime(2026, 8, 1, 9, 0, 0));
        ctx.IdGenerator = new AtomicIdGenerator(1, ctx.Clock);
        return ctx;
    }

    [Fact]
    public async Task Archive_OnlyOnFinishedAndAgree()
    {
        // ARCHIVE：仅 FINISHED + submitType=AGREE 触发 INSERT
        var (exec, writer) = MakeArchiveExec((int)WfInstanceState.Finished, (int)WfSubmitType.Agree);
        exec.ProcessModel = new ProcessModel { Name = "biz_leave" };
        var interceptor = new PersistPostInterceptor().SetWriter(writer);
        await interceptor.InterceptAsync(exec);
        Assert.Contains(writer.Calls, c => c.StartsWith("insert:biz_leave"));

        // 非 FINISHED（DOING）不触发
        var (exec2, writer2) = MakeArchiveExec((int)WfInstanceState.Doing, (int)WfSubmitType.Agree);
        exec2.ProcessModel = new ProcessModel { Name = "biz_leave" };
        await new PersistPostInterceptor().SetWriter(writer2).InterceptAsync(exec2);
        Assert.DoesNotContain(writer2.Calls, c => c.StartsWith("insert:"));

        // FINISHED + REJECT 不触发（拒绝不归档）
        var (exec3, writer3) = MakeArchiveExec((int)WfInstanceState.Reject, (int)WfSubmitType.Reject);
        exec3.ProcessModel = new ProcessModel { Name = "biz_leave" };
        await new PersistPostInterceptor().SetWriter(writer3).InterceptAsync(exec3);
        Assert.DoesNotContain(writer3.Calls, c => c.StartsWith("insert:"));
    }

    [Fact]
    public async Task Archive_IdempotentByInstanceExists()
    {
        // C16 幂等：exists 命中 → 不再 INSERT
        var (exec, writer) = MakeArchiveExec((int)WfInstanceState.Finished, (int)WfSubmitType.Agree);
        exec.ProcessModel = new ProcessModel { Name = "biz_leave" };
        writer.Existing.Add("biz_leave:process_instance_id:900001");
        await new PersistPostInterceptor().SetWriter(writer).InterceptAsync(exec);
        Assert.DoesNotContain(writer.Calls, c => c.StartsWith("insert:"));
    }

    [Fact]
    public async Task Archive_ContextFieldsAndSystemFields()
    {
        // C17：process_instance_id/apply_user_id 上下文 + fillSystemFields(insert) 调用
        var (exec, writer) = MakeArchiveExec((int)WfInstanceState.Finished, (int)WfSubmitType.Agree);
        exec.ProcessModel = new ProcessModel { Name = "biz_leave" };
        await new PersistPostInterceptor().SetWriter(writer).InterceptAsync(exec);
        var insertCall = writer.Calls.First(c => c.StartsWith("insert:biz_leave"));
        Assert.Contains("process_instance_id", insertCall);
        Assert.Contains("apply_user_id", insertCall);
        Assert.Contains("sys:insert", writer.Calls);
    }

    [Fact]
    public async Task Sync_InsertThenUpdateWithPermFilter()
    {
        // SYNC：任务节点 UPDATE 按字段权限过滤（只读/隐藏不写穿——C19）；状态字段 {节点ID}_{状态码}
        var ctx = NewCtx();
        var inst = new ProcessInstance
        {
            InstanceId = 900002,
            Operator = "9001",
            State = (int)WfInstanceState.Doing,
            Variables = new FlowData
            {
                ["f_days"] = 3,        // PERMISSION_days=1 只读 → 不更新
                ["f_reason"] = "事假", // 无声明 → 可更新
                ["tf_approvalComment"] = "同意", // tf_ 冗余
            },
        };
        var taskNode = new TaskModel
        {
            Name = "task1",
            Ext = new FlowData { ["PERMISSION_days"] = 1 },
        };
        var exec = new Execution
        {
            Context = ctx,
            ProcessInstance = inst,
            NodeModel = taskNode,
            Args = new FlowData(),
            ProcessModel = new ProcessModel
            {
                Name = "biz_sync",
                PersistMode = "SYNC",
            },
        };
        var writer = new RecordingWriter { Columns = { "task1_10", "task1_20" } };
        // 首次（无行）→ INSERT；列探测含 task1_10（状态列）
        await new PersistPostInterceptor().SetWriter(writer).InterceptAsync(exec);
        Assert.Contains(writer.Calls, c => c.StartsWith("insert:biz_sync"));
        var insertCall = writer.Calls.First(c => c.StartsWith("insert:biz_sync"));
        Assert.Contains("days", insertCall);       // 首次 INSERT f_ 全量
        Assert.Contains("task1_10", insertCall);   // 状态列（列探测通过）

        // 第二次（新执行链 + 行已存在）→ UPDATE；f_days 只读不写穿
        var exec2 = new Execution
        {
            Context = ctx,
            ProcessInstance = inst,
            NodeModel = taskNode,
            Args = new FlowData(), // 新链：内存标记清零（同节点跨请求靠 exists 兜底，C16）
            ProcessModel = exec.ProcessModel,
        };
        writer.Calls.Clear();
        writer.Existing.Add("biz_sync:process_instance_id:900002");
        await new PersistPostInterceptor().SetWriter(writer).InterceptAsync(exec2);
        var updateCall = writer.Calls.FirstOrDefault(c => c.StartsWith("update:biz_sync"));
        Assert.NotNull(updateCall);
        Assert.DoesNotContain("days", updateCall);            // 只读不写穿（C19）
        Assert.Contains("reason", updateCall);                 // 可编辑正常更新
        Assert.Contains("approvalComment", updateCall);        // tf_ 冗余
    }

    [Fact]
    public async Task Sync_EndNodeFinalizesState()
    {
        // SYNC：结束节点（非 TaskModel）只定稿最终状态（FINISHED=20），不覆盖业务字段
        var ctx = NewCtx();
        var inst = new ProcessInstance
        {
            InstanceId = 900003,
            Operator = "9001",
            State = (int)WfInstanceState.Finished,
            Variables = new FlowData { ["f_days"] = 3 },
        };
        var endNode = new EndModel { Name = "end" };
        var exec = new Execution
        {
            Context = ctx,
            ProcessInstance = inst,
            NodeModel = endNode,
            Args = new FlowData(),
            ProcessModel = new ProcessModel { Name = "biz_sync2", PersistMode = "SYNC" },
        };
        var writer = new RecordingWriter { Columns = { "end_20" } };
        writer.Existing.Add("biz_sync2:process_instance_id:900003");
        await new PersistPostInterceptor().SetWriter(writer).InterceptAsync(exec);
        var updateCall = writer.Calls.First(c => c.StartsWith("update:biz_sync2"));
        Assert.Contains("end_20", updateCall);   // 最终状态定稿
        Assert.DoesNotContain("days", updateCall); // 非任务节点不带业务字段（避免覆盖只读限制）
    }

    [Fact]
    public async Task SameNodeNotFiredTwiceInChain()
    {
        // C16：同链同节点只触发一次（内存标记）
        var (exec, writer) = MakeArchiveExec((int)WfInstanceState.Finished, (int)WfSubmitType.Agree);
        exec.ProcessModel = new ProcessModel { Name = "biz_leave" };
        var interceptor = new PersistPostInterceptor().SetWriter(writer);
        await interceptor.InterceptAsync(exec);
        var insertCount = writer.Calls.Count(c => c.StartsWith("insert:"));
        await interceptor.InterceptAsync(exec); // 同链重放
        Assert.Equal(insertCount, writer.Calls.Count(c => c.StartsWith("insert:")));
    }

    [Fact]
    public void TableNameValidation()
    {
        // 表名安全：sys_ 前缀 / 非法字符 / 空
        Assert.Throws<JeeflowException>(() => TableNames.Validate("sys_user"));
        Assert.Throws<JeeflowException>(() => TableNames.Validate("biz;drop"));
        Assert.Throws<JeeflowException>(() => TableNames.Validate(""));
        TableNames.Validate("biz_leave"); // 合法不抛
    }

    [Fact]
    public void FieldMeta_ColumnNameDefaultsToUnderline()
    {
        var f = new FieldMeta { Name = "companyName" };
        Assert.Equal("company_name", f.GetColumnName());
        var f2 = new FieldMeta { Name = "days", ColumnName = "leave_days" };
        Assert.Equal("leave_days", f2.GetColumnName());
    }

    [Fact]
    public void MetaTableWriter_SubTableInheritsApplyUser()
    {
        // C17/issues/24：子表继承主表 apply_user_id（putIfAbsent——子表显式同名字段优先）
        var baseWriter = new RecordingWriter();
        var provider = new JsonMetaProvider("TestPersistMeta");
        var writer = new MetaTableWriter(baseWriter, provider);
        // 用内嵌元数据验证子表递归：直接构造 provider 返回
        var meta = new TableMeta
        {
            TableName = "biz_main",
            PrimaryKey = "id",
            Fields =
            {
                new FieldMeta { Name = "days", StorageType = StorageType.Normal },
                new FieldMeta
                {
                    Name = "items", StorageType = StorageType.One2Many,
                    TargetTable = "biz_item", ForeignKey = "main_id",
                },
            },
        };
        var stubProvider = new StubMetaProvider(meta);
        var writer2 = new MetaTableWriter(baseWriter, stubProvider);
        var data = new Dictionary<string, object?>
        {
            ["days"] = 3,
            ["apply_user_id"] = 9001,
            ["items"] = new List<object?>
            {
                new Dictionary<string, object?> { ["name"] = "a" },
                new Dictionary<string, object?> { ["name"] = "b", ["apply_user_id"] = 8888 },
            },
        };
        writer2.InsertAsync("biz_main", data).GetAwaiter().GetResult();
        // 主表 + 2 子表行
        Assert.Contains(baseWriter.Calls, c => c.StartsWith("insert:biz_main"));
        Assert.Equal(2, baseWriter.Calls.Count(c => c.StartsWith("insert:biz_item")));
        // 子表外键注入 + 继承/显式优先
        Assert.Contains(baseWriter.Calls, c => c.Contains("main_id") && c.Contains("name"));
    }

    private sealed class StubMetaProvider : IDynamicMetaProvider
    {
        private readonly TableMeta _meta;
        public StubMetaProvider(TableMeta meta) => _meta = meta;
        public TableMeta? LoadTableMeta(string tableName) =>
            tableName == _meta.TableName ? _meta : null;
    }
}

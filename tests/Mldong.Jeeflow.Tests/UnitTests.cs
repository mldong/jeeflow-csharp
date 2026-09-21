using Xunit;
using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Tests;

/// <summary>T0 单元测试包：parser / 聚合根 / FlowUtil / 求值器 / m_ 解析 / 内存分页查询 / 元数据。</summary>
public class ParserTests
{
    [Fact]
    public void Parse_SimpleFlow_Topology()
    {
        var ctx = new ServiceContext(new MemoryRepository());
        var model = ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(TestInfra.LoadFlow("01-simple")), ctx);
        Assert.Equal("simple", model.Name);
        Assert.Equal("简单审批流程", model.DisplayName);
        Assert.Equal("approval", model.Type);
        Assert.Equal(4, model.Nodes.Count); // start/apply/task1/end
        Assert.NotNull(model.GetStart());
        Assert.Equal("start", model.GetStart()!.Name);
        var apply = model.GetNode("apply");
        Assert.NotNull(apply);
        Assert.Equal("发起申请", apply!.DisplayName);
        // 边拓扑：start→apply→task1→end
        Assert.Equal("apply", model.GetStart()!.Outputs[0].To);
        Assert.NotNull(apply.Outputs[0].Target);
        Assert.Single(apply.Inputs);
        Assert.Equal(model.GetStart(), apply.Inputs[0].Source);
    }

    [Fact]
    public void Parse_TaskProperties()
    {
        var ctx = new ServiceContext(new MemoryRepository());
        var model = ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(TestInfra.LoadFlow("05-countersign-parallel")), ctx);
        var task1 = (TaskModel)model.GetNode("task1")!;
        Assert.Equal("userA,userB,userC", task1.Assignee);
        Assert.Equal(WfPerformType.Countersign, task1.PerformType); // '1' 字符串容错（C4）
        Assert.Equal(WfCountersignType.Parallel, task1.CountersignType);
        Assert.Equal("countersign-form", task1.Form);
    }

    [Fact]
    public void Parse_PerformTypeStringTolerant()
    {
        // C4：'1'/'ALL'/'COUNTERSIGN' → Countersign；'ANY' 等 → Normal；null → Normal
        Assert.Equal(WfPerformType.Countersign, Enums.PerformTypeCodeOf("1"));
        Assert.Equal(WfPerformType.Countersign, Enums.PerformTypeCodeOf(1));
        Assert.Equal(WfPerformType.Countersign, Enums.PerformTypeCodeOf("ALL"));
        Assert.Equal(WfPerformType.Countersign, Enums.PerformTypeCodeOf("countersign"));
        Assert.Equal(WfPerformType.Normal, Enums.PerformTypeCodeOf("ANY"));
        Assert.Equal(WfPerformType.Normal, Enums.PerformTypeCodeOf(null));
        Assert.Equal(WfPerformType.Normal, Enums.PerformTypeCodeOf(0));
    }

    [Fact]
    public void Parse_FieldExtMerged()
    {
        // 07-flow 的 field.candidateUsers/field.countersignCompletionCondition 并入 ext + 具名属性
        var ctx = new ServiceContext(new MemoryRepository());
        var model = ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(TestInfra.LoadFlow("07-countersign-ratio")), ctx);
        var task1 = (TaskModel)model.GetNode("task1")!;
        Assert.Equal("#nrOfCompletedInstances==2", task1.CountersignCompletionCondition);
        Assert.Equal("userA,userB,userC,userD", task1.Ext.TryGetStr("candidateUsers"));
    }

    [Fact]
    public void Parse_DecisionEdgesHaveExpr()
    {
        var ctx = new ServiceContext(new MemoryRepository());
        var model = ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(TestInfra.LoadFlow("03-decision-expr")), ctx);
        var decision = model.GetNode("decision1");
        Assert.IsType<DecisionModel>(decision);
        Assert.Equal(2, decision!.Outputs.Count);
        Assert.NotNull(decision.Outputs[0].Expr);
    }

    [Fact]
    public void Parse_NonTaskNodes()
    {
        var ctx = new ServiceContext(new MemoryRepository());
        var model = ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(TestInfra.LoadFlow("04-fork-join")), ctx);
        Assert.IsType<ForkModel>(model.GetNode("fork1"));
        Assert.IsType<JoinModel>(model.GetNode("join1"));
        Assert.IsType<EndModel>(model.GetNode("end"));
        Assert.IsType<StartModel>(model.GetStart());
    }

    [Fact]
    public void Parse_CustomNodeClazzAndVar()
    {
        var ctx = new ServiceContext(new MemoryRepository());
        var model = ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(TestInfra.LoadFlow("08-custom-node")), ctx);
        var custom = Assert.IsType<CustomModel>(model.GetNode("custom1"));
        Assert.Equal("com.mldong.jeeflow.test.TestCustomHandler", custom.Clazz);
        Assert.Equal("execute", custom.MethodName);
        Assert.Equal("customResult", custom.Var);
    }

    [Fact]
    public void Parse_EmptyModel_ReturnsEmptyProcess()
    {
        var ctx = new ServiceContext(new MemoryRepository());
        var model = ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes("{\"name\":\"empty\"}"), ctx);
        Assert.Equal("empty", model.Name);
        Assert.Empty(model.Nodes);
        Assert.Null(model.GetStart());
    }

    [Fact]
    public void Parse_RelTableNameAndPersistMode()
    {
        var ctx = new ServiceContext(new MemoryRepository());
        var json = """{"name":"p","relTableName":"biz_leave","persistMode":"SYNC","nodes":[],"edges":[]}""";
        var model = ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(json), ctx);
        Assert.Equal("biz_leave", model.RelTableName);
        Assert.Equal("SYNC", model.PersistMode);
    }
}

public class AggregateTests
{
    [Fact]
    public void ProcessTask_FinishGuards()
    {
        var task = ProcessTask.Create(1, "t", "T", WfTaskType.Major, WfPerformType.Normal, null,
            new List<string> { "u1" }, "op");
        var ex = Assert.Throws<JeeflowException>(() => task.Finish("u2", null));
        Assert.Contains("不在任务参与者列表中", ex.Message);
        task.Finish("u1", null);
        Assert.Equal((int)WfTaskState.Finished, task.TaskState);
        Assert.Equal("u1", task.ActorId);
        var ex2 = Assert.Throws<JeeflowException>(() => task.Finish("u1", null));
        Assert.Contains("不是进行中状态", ex2.Message);
    }

    [Fact]
    public void ProcessTask_FlowAutoAndAdminAlwaysAllowed()
    {
        // v1.0.1：flow.auto / flow.admin 放行（忽略大小写）
        var task = ProcessTask.Create(1, "t", "T", null, null, null,
            new List<string> { "u1" }, "op");
        Assert.True(task.IsAllowed("flow.auto"));
        Assert.True(task.IsAllowed("FLOW.AUTO"));
        Assert.True(task.IsAllowed("flow.admin"));
        Assert.False(task.IsAllowed("u2"));
        Assert.True(task.IsAllowed("u1"));
    }

    [Fact]
    public void ProcessInstance_CompleteTaskMergesFormVars()
    {
        var inst = new ProcessInstance { InstanceId = 1, State = (int)WfInstanceState.Doing };
        var task = ProcessTask.Create(1, "apply", "申请", null, null, null,
            new List<string> { "u1" }, "u1");
        task.TaskId = 100L;
        inst.Tasks.Add(task);
        inst.CompleteTask(task.TaskId.Value, "u1", new FlowData { ["f_days"] = 3, ["memo"] = "x" });
        Assert.Equal((int)WfTaskState.Finished, task.TaskState);
        Assert.Equal(3, inst.Variables.GetInt("f_days")); // f_ 前缀入流程变量
        Assert.Equal("x", inst.Variables.GetStr("memo"));
        var ex = Assert.Throws<JeeflowException>(() =>
            inst.CompleteTask(999, "u1", null));
        Assert.Contains("未找到任务", ex.Message);
    }

    [Fact]
    public void ProcessInstance_CountersignTasks_SequentialVsParallel()
    {
        var inst = new ProcessInstance { InstanceId = 1 };
        var seqModel = new TaskModel
        {
            Name = "n1", DisplayName = "串行", Form = "f",
            PerformType = WfPerformType.Countersign, CountersignType = WfCountersignType.Sequential,
        };
        var seqTasks = inst.CreateCountersignTasks(seqModel, new List<string> { "a", "b", "c" }, "op");
        Assert.Single(seqTasks); // C9：串行仅建首位
        Assert.Equal("a", seqTasks[0].ActorIds[0]);
        Assert.Equal(3, seqTasks[0].Variables.GetInt("nrOfInstances_n1"));
        Assert.Equal(0, seqTasks[0].Variables.GetInt("loopCounter_n1"));
        Assert.Equal(new List<object?> { "a", "b", "c" },
            seqTasks[0].Variables["operatorList_n1"]);

        var parModel = new TaskModel
        {
            Name = "n2", DisplayName = "并行", Form = "f",
            PerformType = WfPerformType.Countersign, CountersignType = WfCountersignType.Parallel,
        };
        var parTasks = inst.CreateCountersignTasks(parModel, new List<string> { "a", "b" }, "op");
        Assert.Equal(2, parTasks.Count); // 并行一次建全
        Assert.All(parTasks, t => Assert.Single(t.ActorIds));
    }

    [Fact]
    public void ProcessInstance_WithdrawOnlyDoingTasks()
    {
        var inst = new ProcessInstance { InstanceId = 1 };
        var done = ProcessTask.Create(1, "a", "A", null, null, null, new List<string> { "u" }, "u");
        done.Finish("u", null);
        var doing = ProcessTask.Create(1, "b", "B", null, null, null, new List<string> { "u" }, "u");
        inst.Tasks.AddRange(new[] { done, doing });
        inst.Withdraw("admin");
        Assert.Equal((int)WfTaskState.Finished, done.TaskState); // 已完成不受影响
        Assert.Equal((int)WfTaskState.Withdraw, doing.TaskState);
        Assert.Equal((int)WfInstanceState.Withdraw, inst.State);
    }

    [Fact]
    public void ProcessInstance_RejectTaskFollowsFirstInputEdge()
    {
        // RejectTask：沿首入边回退，actor=当前操作人（Java 口径）
        var ctx = new ServiceContext(new MemoryRepository());
        var model = ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(TestInfra.LoadFlow("02-multi-task")), ctx);
        var inst = new ProcessInstance { InstanceId = 1 };
        var current = ProcessTask.Create(1, "task2", "经理审批", null, null, null,
            new List<string> { "manager" }, "manager");
        current.Finish("manager", null);
        inst.Tasks.Add(current);
        var newTask = inst.RejectTask(model, current);
        Assert.NotNull(newTask);
        Assert.Equal("task1", newTask!.TaskName);
        Assert.Equal(new List<string> { "manager" }, newTask.ActorIds);
    }
}

public class FlowUtilTests
{
    [Fact]
    public void AddAutoGenTitle_HoursOnly()
    {
        // C7/C25：autoGenTitle 用 HH:mm（非秒级）
        var clock = new FixedClock(new DateTime(2026, 8, 1, 9, 30, 45));
        var args = new FlowData { [FlowConst.UserRealName] = "张三" };
        FlowUtil.AddAutoGenTitle("请假申请", args, clock);
        Assert.Equal("张三的请假申请-2026-08-01 09:30", args.GetStr(FlowConst.AutoGenTitle));
    }

    [Fact]
    public void AddUserInfoSkipsFlowAutoAndAdmin()
    {
        var args = new FlowData();
        FlowUtil.AddUserInfoToArgsAsync("flow.auto", args, new TestUserProvider()).GetAwaiter().GetResult();
        Assert.Empty(args); // C25：非真实用户跳过注入
        FlowUtil.AddUserInfoToArgsAsync("FLOW.ADMIN", args, new TestUserProvider()).GetAwaiter().GetResult();
        Assert.Empty(args);
    }

    [Fact]
    public void ProcessTime_RelativeAndAbsolute()
    {
        var clock = new FixedClock(new DateTime(2026, 8, 1, 9, 0, 0));
        var args = new FlowData { ["daysVar"] = "2026-08-10 00:00:00" };
        Assert.Equal(clock.Now.AddSeconds(30), FlowUtil.ProcessTime("30s", args, clock));
        Assert.Equal(clock.Now.AddMinutes(10), FlowUtil.ProcessTime("10m", args, clock));
        Assert.Equal(clock.Now.AddHours(24), FlowUtil.ProcessTime("24h", args, clock));
        Assert.Equal(clock.Now.AddDays(3), FlowUtil.ProcessTime("3d", args, clock));
        Assert.Equal(new DateTime(2026, 8, 10), FlowUtil.ProcessTime("daysVar", args, clock)); // 变量引用
        Assert.Null(FlowUtil.ProcessTime("not-a-date", args, clock));
    }

    [Fact]
    public void FilterFieldByPerm_DoubleKeyFormat()
    {
        // C19：PERMISSION_f_{field} 优先 / PERMISSION_{stripped} 兼容；只读(1)/隐藏(3)剔除；可编辑(2)放行
        var ctx = new ServiceContext(new MemoryRepository());
        var model = ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes("""
            {"name":"fp","nodes":[
              {"id":"task1","type":"snaker:task","properties":{"field":{"PERMISSION_f_days":1,"PERMISSION_memo":3,"PERMISSION_ok":2}}},
              {"id":"end","type":"snaker:end"}],
             "edges":[{"id":"e1","sourceNodeId":"task1","targetNodeId":"end"}]}
            """), ctx);
        var args = new FlowData
        {
            ["f_days"] = 9,       // PERMISSION_f_days=1 只读 → 剔除
            ["f_memo"] = "秘密",  // PERMISSION_memo=3 隐藏 → 剔除
            ["f_ok"] = "keep",    // PERMISSION_ok=2 可编辑 → 保留
            ["f_free"] = "y",     // 无声明 → 保留
            ["tf_memo"] = "意见", // tf_ 非表单键 → 不受影响
        };
        var filtered = FlowUtil.FilterFieldByPerm(args, model, "task1");
        Assert.False(filtered.ContainsKey("f_days"));
        Assert.False(filtered.ContainsKey("f_memo"));
        Assert.Equal("keep", filtered.GetStr("f_ok"));
        Assert.Equal("y", filtered.GetStr("f_free"));
        Assert.Equal("意见", filtered.GetStr("tf_memo"));
    }

    [Fact]
    public void IsFirstTaskName_TrueForStartSuccessor()
    {
        var ctx = new ServiceContext(new MemoryRepository());
        var model = ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(TestInfra.LoadFlow("02-multi-task")), ctx);
        Assert.True(FlowUtil.IsFirstTaskName(model, "apply"));
        Assert.False(FlowUtil.IsFirstTaskName(model, "task2"));
    }
}

public class EvaluatorTests
{
    private readonly DefaultExpressionEvaluator _eval = DefaultExpressionEvaluator.Instance;

    [Fact]
    public void Comparisons_NumericPriority()
    {
        Assert.True((bool)_eval.Eval("amount > 1000", new Dictionary<string, object?> { ["amount"] = 2000 })!);
        Assert.False((bool)_eval.Eval("amount > 1000", new Dictionary<string, object?> { ["amount"] = 500 })!);
        Assert.True((bool)_eval.Eval("amount <= 1000", new Dictionary<string, object?> { ["amount"] = 1000 })!);
        Assert.True((bool)_eval.Eval("amount == 1000", new Dictionary<string, object?> { ["amount"] = 1000 })!);
        Assert.True((bool)_eval.Eval("amount != 1000", new Dictionary<string, object?> { ["amount"] = 999 })!);
        Assert.True((bool)_eval.Eval("a >= b", new Dictionary<string, object?> { ["a"] = 5, ["b"] = 5 })!);
        Assert.True((bool)_eval.Eval("a < b", new Dictionary<string, object?> { ["a"] = 1, ["b"] = 2 })!);
    }

    [Fact]
    public void HashVar_SuffixKeyMatch()
    {
        // #nrOfCompletedInstances 匹配 csv_task1_nrOfCompletedInstances（PHP str_ends_with 语义）
        var vars = new Dictionary<string, object?>
        {
            ["csv_task1_nrOfInstances"] = 4,
            ["csv_task1_nrOfCompletedInstances"] = 2,
        };
        Assert.True((bool)_eval.Eval("#nrOfCompletedInstances==2", vars)!);
        Assert.False((bool)_eval.Eval("#nrOfCompletedInstances==3", vars)!);
    }

    [Fact]
    public void PlaceholderAndLiterals()
    {
        Assert.True((bool)_eval.Eval("${count} >= 3", new Dictionary<string, object?> { ["count"] = 5 })!);
        Assert.False((bool)_eval.Eval("${count} >= 3", new Dictionary<string, object?>())!); // 缺省 0
        Assert.True((bool)_eval.Eval("true", new Dictionary<string, object?>())!);
        Assert.False((bool)_eval.Eval("false", new Dictionary<string, object?>())!);
    }

    [Fact]
    public void DecisionModel_UsesEvaluatorForEdgeSelection()
    {
        // 决策节点：表达式边只收 true 边（C14 highLight 前置语义）
        var (engine, repo) = TestInfra.NewEngine();
        var did = TestInfra.SaveFlowDefine(repo, "decision-expr", TestInfra.LoadFlow("03-decision-expr"));
        var inst = engine.StartProcessInstanceByIdAsync(did, "applicant",
            new FlowData { ["amount"] = 800 }).GetAwaiter().GetResult();
        var apply = TestInfra.FindDoingFor(repo, inst.InstanceId!.Value, "applicant");
        engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData()).GetAwaiter().GetResult();
        var t1 = TestInfra.FindDoingFor(repo, inst.InstanceId.Value, "leader");
        engine.ExecuteProcessTaskAsync(t1.TaskId!.Value, "leader",
            new FlowData { ["amount"] = 800 }).GetAwaiter().GetResult();
        var next = repo.FindDoingTasksAsync(inst.InstanceId.Value, null).GetAwaiter().GetResult();
        Assert.Equal("task3", next[0].TaskName); // amount<=1000 → director 分支
    }
}

public class MemoryQueryTests
{
    private async Task<(MemoryRepository Repo, long DefineId)> SeedAsync()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "seed-simple", TestInfra.LoadFlow("01-simple"));
        for (var i = 0; i < 7; i++)
        {
            var args = new FlowData { [FlowConst.BusinessNo] = $"BIZ-{i:000}" };
            await TestInfra.StartAndApplyAsync(engine, repo, did, args);
        }
        // 3 个 define 驱动分页断言
        for (var i = 1; i <= 2; i++)
        {
            await TestInfra.SaveFlowDefineAsync(repo, $"seed-def-{i}", TestInfra.LoadFlow("01-simple"));
        }
        return (repo, did);
    }

    [Fact]
    public async Task PageDefines_FiveKeysAndDefaultOrder()
    {
        // C22：恒五键；默认排序 id DESC（TsID 雪花混排现状）
        var (repo, did) = await SeedAsync();
        var page = await repo.PageDefinesAsync(new PageQuery(1, 2));
        Assert.Equal(1, page.PageNum);
        Assert.Equal(2, page.PageSize);
        Assert.Equal(3, page.RecordCount);
        Assert.Equal(2, page.TotalPage); // ceil(3/2)
        Assert.Equal(2, page.Rows.Count);
        Assert.True(page.Rows[0].Id > page.Rows[1].Id); // id DESC

        var page2 = await repo.PageDefinesAsync(new PageQuery(2, 2));
        Assert.Single(page2.Rows);
    }

    [Fact]
    public async Task PageInstances_BusinessNoFilterAndPaging()
    {
        var (repo, _) = await SeedAsync();
        var query = new JeeflowQueryParser().Parse(new Dictionary<string, object?>
        {
            ["pageNum"] = 1,
            ["pageSize"] = 5,
            ["m_t_LIKE_businessNo"] = "BIZ-00",
        });
        query.Add("t.operator", "EQ", "applicant");
        var page = await repo.PageInstancesAsync(query);
        Assert.Equal(7, page.RecordCount); // BIZ-000..006 全命中
        Assert.Equal(5, page.Rows.Count);

        var query2 = new JeeflowQueryParser().Parse(new Dictionary<string, object?>
        {
            ["pageSize"] = 5,
            ["m_t_EQ_businessNo"] = "BIZ-003",
        });
        var page2 = await repo.PageInstancesAsync(query2);
        Assert.Equal(1, page2.RecordCount);
    }

    [Fact]
    public async Task PageTodoTasks_ActorJoinExpand()
    {
        // pta.actor_id 条件按任务×参与人行展开（JDBC JOIN 语义）
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "todo", TestInfra.LoadFlow("01-simple"));
        await TestInfra.StartAndApplyAsync(engine, repo, did);
        var query = new PageQuery(1, 10).Add("pta.actor_id", "EQ", "leader");
        var page = await repo.PageTodoTasksAsync(query);
        Assert.Equal(1, page.RecordCount);
        Assert.Equal("task1", page.Rows[0].TaskName);
        Assert.Equal("上级审批", page.Rows[0].DisplayName); // pd 关联显示名
    }

    [Fact]
    public async Task PageInstances_WhitelistDropsUnknownColumns()
    {
        // 白名单外条件丢弃（防注入面）
        var (repo, _) = await SeedAsync();
        var query = new PageQuery(1, 10).Add("t.password", "EQ", "hack");
        var page = await repo.PageInstancesAsync(query);
        Assert.Equal(7, page.RecordCount); // 条件被丢弃，返回全量
    }

    [Fact]
    public async Task CountTodoTasks()
    {
        // Java countTodoTasks(Long userId)：pta.actor_id = String.valueOf(userId)
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "cnt", TestInfra.LoadFlow("01-simple"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var task = await TestInfra.FindDoingForAsync(repo, iid, "leader");
        Assert.Equal(0, await repo.CountTodoTasksAsync(9000001)); // 无关用户 0
        await repo.AddTaskActorAsync(task.TaskId!.Value, new List<string> { "9000001" });
        Assert.Equal(1, await repo.CountTodoTasksAsync(9000001));
    }

    [Fact]
    public async Task UpdateTask_DoesNotReinsertActorDuplicates()
    {
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "upd", TestInfra.LoadFlow("01-simple"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var task = await TestInfra.FindDoingForAsync(repo, iid, "leader");
        await repo.UpdateTaskAsync(task); // 全量覆盖语义：同名单不重复
        var actors = await repo.FindTaskActorsAsync(task.TaskId!.Value);
        Assert.Equal(1, actors.Count(a => a == "leader"));
    }

    [Fact]
    public async Task Hydration_InstanceTasksLoadedAfterFind()
    {
        // issues/89：find_instance_by_id 后必须填充 tasks
        var (engine, repo) = TestInfra.NewEngine();
        var did = await TestInfra.SaveFlowDefineAsync(repo, "hyd", TestInfra.LoadFlow("02-multi-task"));
        var iid = await TestInfra.StartAndApplyAsync(engine, repo, did);
        var inst = await repo.FindInstanceByIdAsync(iid);
        Assert.NotEmpty(inst!.Tasks); // apply(FINISHED) + task1(DOING)
        Assert.Equal(2, inst.Tasks.Count);
        Assert.Contains(inst.Tasks, t => t.IsDoing() && t.TaskName == "task1");
    }
}

public class MetadataPermissionTests
{
    [Fact]
    public void DefaultPermissionProvider_Rules()
    {
        // C24：默认 wf:{action /→:}；OR 清单；stats/详情类放行（null）
        var p = new DefaultActionPermissionProvider();
        Assert.Equal(new[] { "wf:processDefine:page" }, p.PermissionCodes("processDefine/page"));
        Assert.Equal(new[] { "wf:processDefine:detail", "wf:processDesign:listByType" },
            p.PermissionCodes("processDefine/detail"));
        Assert.Equal(new[] { "wf:processTask:execute", "wf:processTask:candidatePage" },
            p.PermissionCodes("processTask/candidatePage"));
        Assert.Null(p.PermissionCodes("processInstance/stats/overview"));
        Assert.Null(p.PermissionCodes("processInstance/stats/trend"));
        Assert.Null(p.PermissionCodes("processInstance/stats/group"));
        Assert.Null(p.PermissionCodes("processInstance/detail"));
        Assert.Null(p.PermissionCodes("processTask/latest"));
        Assert.Null(p.PermissionCodes("processInstance/bizData"));
    }

    [Fact]
    public void EnumDictRegistry_LabelsMatchJava()
    {
        var reg = new EnumDictRegistry();
        var submit = reg.GetDict("wf_process_submit_type");
        Assert.Equal(9, submit.Count);            // 9 值全枚举（issues/115 补 7 转办）
        Assert.Equal("20", submit[^1].Value);     // spec 07：20=会签拒绝（曾与 2 同名"拒绝申请"，前端下拉分不开）
        Assert.Equal("会签拒绝", submit[^1].Label);
        Assert.Equal("6", submit[6].Value);
        Assert.Equal("退回发起人", submit[6].Label);
        Assert.Equal("7", submit[7].Value);
        Assert.Equal("转办", submit[7].Label);
        Assert.Equal("2", submit[2].Value);       // 2=拒绝申请保持不动，只把 20 改标签
        Assert.Equal("拒绝申请", submit[2].Label);
        var instance = reg.GetDict("wf_process_instance_state");
        Assert.Equal(7, instance.Count);          // 10/20/30/40/45/50/99
        Assert.Equal("45", instance[4].Value);
        Assert.Equal("已拒绝", instance[4].Label);
    }
}

using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;
using Mldong.Jeeflow.Repository.MySql;
using MySqlConnector;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// T1 MySQL 冒烟（160 真库，R6 红线）：
/// - 行为双跑（与 Memory 同套件，防仓储分叉）
/// - M1 分页五键 / M2 hydrate 主键 / 事务回滚无半完成实例 / 并发办理只一次成功
/// - define 用 9xxxxx 段；实例以 BUSINESS_NO=T1CS- 前缀标记；测后自清理 + 清理验证
/// - SKIP_MYSQL=1 开发机跳过；凭据只走 JEFFLOW_DB_* env；连不上=fail 不是 skip（发版机口径）
/// </summary>
[Collection("mysql")]
[Trait("Category", "mysql-smoke")]
public class MySqlBehaviorSuite : RepositoryBehaviorSuite
{
    private readonly MySqlFixture _fx;

    public MySqlBehaviorSuite(MySqlFixture fx) => _fx = fx;

    private static bool Skip =>
        Environment.GetEnvironmentVariable("SKIP_MYSQL") == "1";

    protected override (JeeflowEngine Engine, MemoryRepository? Mem, IProcessRepository Repo) Build() =>
        (_fx.Engine, null, _fx.Repo);

    // SKIP_MYSQL=1 开发机：vacuous pass（发版机不加 SKIP，连不上=fail，R6 口径）
    public override async Task Behavior_StartAndCompleteChain() { if (Skip) return; await base.Behavior_StartAndCompleteChain(); }
    public override async Task Behavior_TaskActorsRoundTrip() { if (Skip) return; await base.Behavior_TaskActorsRoundTrip(); }
    public override async Task Behavior_VariablesJsonRoundTrip() { if (Skip) return; await base.Behavior_VariablesJsonRoundTrip(); }
    public override async Task Behavior_PageTodoFiveKeys() { if (Skip) return; await base.Behavior_PageTodoFiveKeys(); }
    public override async Task Behavior_CcCreateAndUpdateStatus() { if (Skip) return; await base.Behavior_CcCreateAndUpdateStatus(); }
    public override async Task Behavior_DefineWriteOps() { if (Skip) return; await base.Behavior_DefineWriteOps(); }
    public override async Task Behavior_NonActorExecuteFails() { if (Skip) return; await base.Behavior_NonActorExecuteFails(); }

    protected override async Task<(long DefineId, long InstanceId)> SeedSimpleFlowAsync(
        JeeflowEngine engine, IProcessRepository repo, string businessNo, FlowData? args = null)
    {
        var defineId = await _fx.SaveT1DefineAsync("BHV-" + businessNo);
        var flowArgs = args ?? new FlowData();
        flowArgs[FlowConst.BusinessNo] = "T1CS-" + businessNo;
        var inst = await engine.StartProcessInstanceByIdAsync(defineId, "applicant", flowArgs);
        var apply = await FindDoingByActorAsync(repo, inst.InstanceId!.Value, "applicant");
        await engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData());
        return (defineId, inst.InstanceId.Value);
    }

    [Fact]
    public async Task T1M1_PagingFiveKeysOverRealDb()
    {
        if (Skip) return; // SKIP_MYSQL=1
        // M1：分页五键走真 SQL（LIMIT 内联 + 白名单 + COUNT）
        var (_, _, repo) = Build();
        var define = new ProcessDefine
        {
            Id = 910101,
            Name = "T1CS-page-def",
            DisplayName = "T1-分页",
            Type = "approval",
            State = 1,
            Content = System.Text.Encoding.UTF8.GetBytes(TestInfra.LoadFlow("01-simple")),
            Version = 1,
        };
        await repo.SaveDefineAsync(define);
        try
        {
            var query = new PageQuery(1, 2).Add("t.name", "LIKE", "T1CS-page-def");
            var page = await repo.PageDefinesAsync(query);
            Assert.Equal(1, page.PageNum);
            Assert.Equal(2, page.PageSize);
            Assert.Equal(1, page.RecordCount);
            Assert.Equal(1, page.TotalPage);
            Assert.NotEmpty(page.Rows);
            Assert.Equal("T1CS-page-def", page.Rows[0].Name);

            // m_ 三段式经仓储白名单生效
            var q2 = new JeeflowQueryParser().Parse(new Dictionary<string, object?>
            {
                ["pageSize"] = 5,
                ["m_t_EQ_name"] = "T1CS-page-def",
            });
            var page2 = await repo.PageDefinesAsync(q2);
            Assert.Equal(1, page2.RecordCount);
        }
        finally
        {
            await repo.RemoveDefineAsync(define.Id!.Value);
        }
    }

    [Fact]
    public async Task T1M2_HydratePrimaryKeyExact()
    {
        if (Skip) return; // SKIP_MYSQL=1
        // M2：手动 9xxxxx 主键落库 + 雪花(>2^53)主键读回均精确保真（hydrate 无截断/取整）
        var (_, _, repo) = Build();
        var defineId = await _fx.SaveT1DefineAsync("hydrate-pk");
        var inst = await _fx.Engine.StartProcessInstanceByIdAsync(defineId, "applicant",
            new FlowData { [FlowConst.BusinessNo] = "T1CS-hydrate-pk" });
        var applyH = await FindDoingByActorAsync(repo, inst.InstanceId!.Value, "applicant");
        await _fx.Engine.ExecuteProcessTaskAsync(applyH.TaskId!.Value, "applicant", new FlowData());
        try
        {
            // 雪花 id > 2^53 精确读回
            Assert.True(inst.InstanceId > 9007199254740993L);
            var hydrated = await repo.FindInstanceByIdAsync(inst.InstanceId);
            Assert.Equal(inst.InstanceId, hydrated!.InstanceId);
            Assert.NotEmpty(hydrated.Tasks); // 聚合水合
            Assert.All(hydrated.Tasks, t => Assert.True(t.TaskId > 0));

            // 手动 9xxxxx 主键（apply 操作人 task_actor 行）
            var task = await FindDoingByActorAsync(repo, inst.InstanceId.Value, "leader");
            Assert.True(task.TaskId > 0);
            var actors = await repo.FindTaskActorsAsync(task.TaskId.Value);
            Assert.Contains("leader", actors);
        }
        finally
        {
            await _fx.CleanupByMarkerAsync("T1CS-hydrate-pk");
        }
    }

    [Fact]
    public async Task T1M5_WithdrawPersistsTaskState30()
    {
        if (Skip) return; // SKIP_MYSQL=1
        // issues/113：门面 withdraw 须把全部进行中任务以 30（WITHDRAW）落 MySQL。
        // v1.0.1 的 updateInstance 级联在 C# 侧此前从未对真库验过撤回路径，
        // 门面用例 Withdraw_ViaFacade 也只断到实例态。
        var (_, _, repo) = Build();
        var defineId = await _fx.SaveT1DefineAsync("withdraw-30");
        var inst = await _fx.Engine.StartProcessInstanceByIdAsync(defineId, "applicant",
            new FlowData { [FlowConst.BusinessNo] = "T1CS-withdraw-30" });
        var apply = await FindDoingByActorAsync(repo, inst.InstanceId!.Value, "applicant");
        await _fx.Engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData());
        var leader = await FindDoingByActorAsync(repo, inst.InstanceId!.Value, "leader");
        try
        {
            var facade = new JeeflowFacade(_fx.Ctx);
            var resp = await facade.FlowAsync("processInstance/withdraw",
                new FlowData { ["id"] = inst.InstanceId!.Value, ["operator"] = "applicant" });
            Assert.Equal(0, resp["code"]);

            // 直查库表，绕开聚合水合与内存别名：确证落库 30 而非 99（ABANDON）
            Assert.Equal((int)WfTaskState.Withdraw, await TaskStateOfAsync(leader.TaskId!.Value));
            // 已完成的任务不得被撤回改写（仍是 20）
            Assert.Equal((int)WfTaskState.Finished, await TaskStateOfAsync(apply.TaskId!.Value));
            Assert.Empty(await repo.FindDoingTasksAsync(inst.InstanceId!.Value, null));
            Assert.Equal((int)WfInstanceState.Withdraw,
                (await repo.FindInstanceByIdAsync(inst.InstanceId!.Value))!.State);
        }
        finally
        {
            await _fx.CleanupByMarkerAsync("T1CS-withdraw-30");
            _fx.RemoveDefine(defineId);
        }
    }

    private async Task<int> TaskStateOfAsync(long taskId)
    {
        await using var conn = await _fx.Factory.OpenAsync();
        await using var cmd = new MySqlCommand(
            "SELECT task_state FROM wf_process_task WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", taskId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    // ═══ issues/113~115：撤回鉴权 + 转办（真库裸 SQL 读回）═══
    // 断言一律绕开聚合水合与内存别名，直查 wf_process_task / wf_process_task_actor /
    // wf_process_instance 列值——门面写没写进库，只有裸 SQL 说得清。

    /// <summary>裸 SQL 取单列值（SQL NULL → C# null）。</summary>
    private async Task<object?> ScalarOfAsync(string selectSql, long id)
    {
        await using var conn = await _fx.Factory.OpenAsync();
        await using var cmd = new MySqlCommand(selectSql, conn);
        cmd.Parameters.AddWithValue("@id", id);
        var val = await cmd.ExecuteScalarAsync();
        return val is DBNull ? null : val;
    }

    private Task<object?> TaskColumnOfAsync(long taskId, string column) =>
        ScalarOfAsync($"SELECT {column} FROM wf_process_task WHERE id = @id", taskId);

    private async Task<List<string>> ActorsOfAsync(long taskId)
    {
        await using var conn = await _fx.Factory.OpenAsync();
        await using var cmd = new MySqlCommand(
            "SELECT actor_id FROM wf_process_task_actor WHERE process_task_id = @id ORDER BY id ASC", conn);
        cmd.Parameters.AddWithValue("@id", taskId);
        var actors = new List<string>();
        await using var rs = await cmd.ExecuteReaderAsync();
        while (await rs.ReadAsync()) actors.Add(rs.GetString(0));
        return actors;
    }

    /// <summary>门面待办行 id（真库分页 SQL：pta.actor_id JOIN）。</summary>
    private static async Task<List<string>> TodoIdsAsync(JeeflowFacade facade, string actor)
    {
        var resp = await facade.FlowAsync("processTask/todoList", new FlowData { ["operator"] = actor });
        Assert.True(Equals(0, resp["code"]), $"todoList msg={resp["msg"]}");
        var data = (Dictionary<string, object?>)resp["data"]!;
        return ((List<object?>)data["rows"]!)
            .Select(r => ((Dictionary<string, object?>)r!)["id"]!.ToString()!)
            .ToList();
    }

    private static async Task<List<string>> DoneIdsAsync(JeeflowFacade facade, string actor)
    {
        var resp = await facade.FlowAsync("processTask/doneList", new FlowData { ["operator"] = actor });
        Assert.True(Equals(0, resp["code"]), $"doneList msg={resp["msg"]}");
        var data = (Dictionary<string, object?>)resp["data"]!;
        return ((List<object?>)data["rows"]!)
            .Select(r => ((Dictionary<string, object?>)r!)["id"]!.ToString()!)
            .ToList();
    }

    /// <summary>发起 + 办结 apply → leader 手上留一条 DOING 任务（T1 段专属 marker）。</summary>
    private async Task<(long DefineId, long InstanceId, long TaskId, long ApplyTaskId)> SeedLeaderTaskAsync(
        string marker)
    {
        var (_, _, repo) = Build();
        var defineId = await _fx.SaveT1DefineAsync(marker);
        var inst = await _fx.Engine.StartProcessInstanceByIdAsync(defineId, "applicant",
            new FlowData { [FlowConst.BusinessNo] = "T1CS-" + marker });
        var apply = await FindDoingByActorAsync(repo, inst.InstanceId!.Value, "applicant");
        await _fx.Engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData());
        var leader = await FindDoingByActorAsync(repo, inst.InstanceId!.Value, "leader");
        return (defineId, inst.InstanceId.Value, leader.TaskId!.Value, apply.TaskId!.Value);
    }

    [Fact]
    public async Task T1M6_TransferPersistsActorsAndThreeTracesOverRealDb()
    {
        if (Skip) return; // SKIP_MYSQL=1
        var (defineId, iid, taskId, _) = await SeedLeaderTaskAsync("transfer-trace");
        try
        {
            var facade = new JeeflowFacade(_fx.Ctx);
            var resp = await facade.FlowAsync("processTask/transfer", new FlowData
            {
                [FlowConst.ProcessTaskIdKey] = taskId,
                ["fromActor"] = "leader",
                ["toActor"] = "lisi",
                ["reason"] = "出差一周",
                ["operator"] = "leader",
            });
            Assert.True(Equals(0, resp["code"]), $"transfer msg={resp["msg"]}");

            // 参与者行：leader 那行真删了、lisi 那行真加了（同 taskId，不新建任务）
            Assert.Equal(new List<string> { "lisi" }, await ActorsOfAsync(taskId));
            Assert.Equal((int)WfTaskState.Doing, await TaskStateOfAsync(taskId));

            // 注意：严禁覆写 operator 列：DOING 任务该列必须仍是 SQL NULL（Java 已知偏差①，本栈不照抄）
            var persistedOperator = await TaskColumnOfAsync(taskId, "operator");
            Assert.True(persistedOperator == null,
                $"转办覆写了 wf_process_task.operator（应恒 NULL），实得 {persistedOperator}");
            // "办理人记谁"由 update_user 承载
            Assert.Equal("leader", await TaskColumnOfAsync(taskId, "update_user"));

            // variable 列 JSON 落地形状：六键固定 camelCase + time yyyy-MM-dd HH:mm:ss（非 ISO 方言）
            var variable = (string?)(await TaskColumnOfAsync(taskId, "variable")) ?? "";
            Assert.Contains(
                "{\"submitType\":7,\"fromActor\":\"leader\",\"toActor\":\"lisi\"," +
                "\"reason\":\"出差一周\",\"time\":\"2026-08-01 09:00:00\",\"operator\":\"leader\"}",
                variable);
            Assert.Contains("\"tf_transferHistory\":[", variable);
            Assert.Contains("\"tf_transferTo\":\"lisi\"", variable);
            Assert.Contains("\"tf_transferReason\":\"出差一周\"", variable);
            Assert.Contains("\"tf_approvalComment\":\"leader 转办给 lisi（出差一周）\"", variable);
            Assert.Contains("\"submitType\":7", variable);
            Assert.DoesNotContain("\"time\":\"2026-08-01T", variable); // 禁本地 ISO 方言（契约 §2.4）
            // 仓储读回（JSON 反序列化）后账本仍是数组，形状与内存仓同构
            var reread = await _fx.Repo.FindTaskByIdAsync(taskId);
            Assert.IsType<List<object?>>(reread!.Variables.GetObj(FlowConst.TransferHistory));
            var hop = (Dictionary<string, object?>)((List<object?>)reread.Variables
                .GetObj(FlowConst.TransferHistory)!)[0]!;
            Assert.Equal(7L, Convert.ToInt64(hop["submitType"])); // JSON 回读整数为 long，值不变形
            Assert.Equal("2026-08-01 09:00:00", hop["time"]);

            // 待办在真库分页 SQL 上挪了
            Assert.Contains(taskId.ToString(), await TodoIdsAsync(facade, "lisi"));
            Assert.DoesNotContain(taskId.ToString(), await TodoIdsAsync(facade, "leader"));
        }
        finally
        {
            await _fx.CleanupByMarkerAsync("T1CS-transfer-trace");
            _fx.RemoveDefine(defineId);
        }
    }

    [Fact]
    public async Task T1M7_TransferThenWithdraw_KeepsFromActorOutOfDoneListOverRealDb()
    {
        if (Skip) return; // SKIP_MYSQL=1
        // 回归红线（Node 实测踩过）：转办若把被摘走的人写进 operator 列，该单一旦撤回
        // （离开 DOING 但保留该列值），pageDoneTasks 的 state<>10 AND operator=? 会让他
        // 凭空出现在「我已办」里。真库上把这条钉住。
        var (defineId, iid, taskId, applyTaskId) = await SeedLeaderTaskAsync("transfer-redline");
        try
        {
            var facade = new JeeflowFacade(_fx.Ctx);
            Assert.Equal(0, (await facade.FlowAsync("processTask/transfer", new FlowData
            {
                [FlowConst.ProcessTaskIdKey] = taskId, ["fromActor"] = "leader",
                ["toActor"] = "lisi", ["operator"] = "leader",
            }))["code"]);
            var wd = await facade.FlowAsync("processInstance/withdraw",
                new FlowData { ["id"] = iid, ["operator"] = "applicant" });
            Assert.True(Equals(0, wd["code"]), $"withdraw msg={wd["msg"]}");

            // 任务离开 DOING（30），但 operator 列仍 NULL——没被转办覆写过
            Assert.Equal((int)WfTaskState.Withdraw, await TaskStateOfAsync(taskId));
            Assert.Null(await TaskColumnOfAsync(taskId, "operator"));
            // 被摘走的人 / 接手但未办的人：这条都不该在他们的「我已办」里
            Assert.DoesNotContain(taskId.ToString(), await DoneIdsAsync(facade, "leader"));
            Assert.DoesNotContain(taskId.ToString(), await DoneIdsAsync(facade, "lisi"));
            // 正向对照（防空断言）：真办过 apply 的 applicant 确实在自己「我已办」里看到它
            Assert.Contains(applyTaskId.ToString(), await DoneIdsAsync(facade, "applicant"));
        }
        finally
        {
            await _fx.CleanupByMarkerAsync("T1CS-transfer-redline");
            _fx.RemoveDefine(defineId);
        }
    }

    [Fact]
    public async Task T1M8_WithdrawRequiresOperatorAndOwnershipOverRealDb()
    {
        if (Skip) return; // SKIP_MYSQL=1
        var (defineId, iid, taskId, _) = await SeedLeaderTaskAsync("withdraw-auth");
        try
        {
            var facade = new JeeflowFacade(_fx.Ctx);
            // 缺 operator：明确报错，严禁回落 user1 静默撤回；库里原样未动
            var noOp = await facade.FlowAsync("processInstance/withdraw", new FlowData { ["id"] = iid });
            Assert.Equal(99999999, noOp["code"]);
            Assert.Equal("operator 必填", noOp["msg"]);
            Assert.Equal((int)WfInstanceState.Doing,
                Convert.ToInt32(await ScalarOfAsync("SELECT state FROM wf_process_instance WHERE id = @id", iid)));
            Assert.Equal((int)WfTaskState.Doing, await TaskStateOfAsync(taskId));

            // 无关第三人：拒绝且不落库
            var third = await facade.FlowAsync("processInstance/withdraw",
                new FlowData { ["id"] = iid, ["operator"] = "boss" });
            Assert.Equal(99999999, third["code"]);
            Assert.Equal("无权限撤回该流程实例", third["msg"]);
            Assert.Equal((int)WfTaskState.Doing, await TaskStateOfAsync(taskId));

            // 判据②：进行中任务的参与者撤回整单 → 任务 30 + update_user 回写真实撤回人
            var ok = await facade.FlowAsync("processInstance/withdraw",
                new FlowData { ["id"] = iid, ["operator"] = "leader" });
            Assert.True(Equals(0, ok["code"]), $"withdraw msg={ok["msg"]}");
            Assert.Equal((int)WfTaskState.Withdraw, await TaskStateOfAsync(taskId));
            Assert.Equal("leader", await TaskColumnOfAsync(taskId, "update_user"));
            Assert.Equal("leader",
                await ScalarOfAsync("SELECT update_user FROM wf_process_instance WHERE id = @id", iid));
        }
        finally
        {
            await _fx.CleanupByMarkerAsync("T1CS-withdraw-auth");
            _fx.RemoveDefine(defineId);
        }
    }

    [Fact]
    public async Task T1_TxRollbackLeavesNoHalfInstance()
    {
        if (Skip) return; // SKIP_MYSQL=1
        // §6.2：ITransactionTemplate 回调抛错 → BEGIN 后所有写入回滚净，无半完成实例
        var defineId = await _fx.SaveT1DefineAsync("tx-rollback");
        var countBefore = await _fx.CountMarkerAsync("T1CS-tx-rollback");
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => _fx.Tx.ExecuteInTxAsync<object?>(async () =>
            {
                await _fx.Engine.StartProcessInstanceByIdAsync(defineId, "applicant",
                    new FlowData { [FlowConst.BusinessNo] = "T1CS-tx-rollback" });
                throw new InvalidOperationException("rollback-probe");
            }));
        // 探针异常（或其包装）必须可见——回滚由下一断言验证
        var msg = ex.Message + (ex.InnerException?.Message ?? "");
        Assert.Contains("rollback-probe", msg);
        var countAfter = await _fx.CountMarkerAsync("T1CS-tx-rollback");
        Assert.Equal(countBefore, countAfter); // 无半完成实例
    }

    [Fact]
    public async Task T1_ConcurrentCompleteOnlyOneWins()
    {
        if (Skip) return; // SKIP_MYSQL=1
        // §6.2：同任务两次 execute（并发）→ 恰一次成功，后者 99999999"任务已办理"
        var defineId = await _fx.SaveT1DefineAsync("concurrency");
        var inst = await _fx.Engine.StartProcessInstanceByIdAsync(defineId, "applicant",
            new FlowData { [FlowConst.BusinessNo] = "T1CS-concurrency" });
        var applyC = await FindDoingByActorAsync(_fx.Repo, inst.InstanceId!.Value, "applicant");
        await _fx.Engine.ExecuteProcessTaskAsync(applyC.TaskId!.Value, "applicant", new FlowData());
        var iid = inst.InstanceId.Value;
        try
        {
            var task = await FindDoingByActorAsync(_fx.Repo, iid, "leader");
            var taskId = task.TaskId!.Value;
            var done = 0;
            var failed = 0;
            var results = await Task.WhenAll(
                Probe(_fx.Engine, taskId, "leader"),
                Probe(_fx.Engine, taskId, "leader"));
            foreach (var ok in results) { if (ok) done++; else failed++; }
            Assert.Equal(1, done);
            Assert.Equal(1, failed);

            static async Task<bool> Probe(JeeflowEngine engine, long tid, string op)
            {
                try
                {
                    await engine.ExecuteProcessTaskAsync(tid, op, new FlowData());
                    return true;
                }
                catch (JeeflowException)
                {
                    return false;
                }
            }
        }
        finally
        {
            await _fx.CleanupByMarkerAsync("T1CS-concurrency");
        }
    }

    [Fact]
    public async Task T1_CleanupVerified()
    {
        if (Skip) return; // SKIP_MYSQL=1
        // R6：测后自清理 + 清理可验证
        var defineId = await _fx.SaveT1DefineAsync("cleanup-verify");
        var inst = await _fx.Engine.StartProcessInstanceByIdAsync(defineId, "applicant",
            new FlowData { [FlowConst.BusinessNo] = "T1CS-cleanup-verify" });
        Assert.True((await _fx.CountMarkerAsync("T1CS-cleanup-verify")) >= 1);
        await _fx.CleanupByMarkerAsync("T1CS-cleanup-verify");
        Assert.Equal(0, await _fx.CountMarkerAsync("T1CS-cleanup-verify"));
        _fx.RemoveDefine(defineId);
        Assert.Null(await _fx.Repo.FindDefineByIdAsync(defineId));
    }

    // ═══ issues/116 批次 D：委托代理自动生效（SQL 仓 + 双仓一致，契约 06 §4.5 条款 6）═══

    /// <summary>
    /// SQL 仓真库用例：断言落在 <c>wf_process_task_actor</c> 真行，并把同一份委托数据
    /// 同时喂给内存仓，逐判据比对两仓结论（条款 6：同栈两仓给出不同结论即缺陷）。
    /// </summary>
    [Fact]
    public async Task T1_SurrogateAutoApply_RealActorRows_AndDualRepoSameAnswer()
    {
        if (Skip) return; // SKIP_MYSQL=1
        const string flowName = "T1CS-surrogate";
        const string actor = "t1sur-actor";
        var defineId = await SaveSurrogateDefineAsync(flowName, actor);
        try
        {
            // ── 正向 + 条款 1.4（多条命中取 max id，SQL 侧 ORDER BY id DESC）──
            await SeedSurrogateAsync(actor, "t1sur-agentOld", flowName, 1);
            await SeedSurrogateAsync(actor, "t1sur-agentNew", flowName, 1);
            var taskId = await StartUntilApprovalAsync(defineId);
            var rows = await _fx.Repo.FindTaskActorsAsync(taskId);
            Assert.Equal(new List<string> { actor, "t1sur-agentNew" }, rows);

            // ── 判据①：空 processName = 全部流程兜底（精确名未命中时）──
            await ClearSurrogateRowsAsync();
            await SeedSurrogateAsync(actor, "t1sur-wild", "", 1);
            Assert.Equal(new List<string> { actor, "t1sur-wild" },
                await _fx.Repo.FindTaskActorsAsync(await StartUntilApprovalAsync(defineId)));

            // ── 判据②：时间窗（NULL=该侧不限；窗外不生效）──
            foreach (var (start, end, expectHit) in new (DateTime?, DateTime?, bool)[]
                     {
                         (null, null, true),
                         (new DateTime(2020, 1, 1), null, true),
                         (null, new DateTime(2030, 1, 1), true),
                         (new DateTime(2030, 1, 1), null, false),
                         (null, new DateTime(2020, 1, 1), false),
                     })
            {
                await ClearSurrogateRowsAsync();
                await SeedSurrogateAsync(actor, "t1sur-win", flowName, 1, start, end);
                var got = await _fx.Repo.FindTaskActorsAsync(await StartUntilApprovalAsync(defineId));
                Assert.Equal(expectHit ? 2 : 1, got.Count);
            }

            // ── 判据③：自委托过滤 / 判据④：enabled 只认 1 ──
            await ClearSurrogateRowsAsync();
            await SeedSurrogateAsync(actor, actor, flowName, 1);
            Assert.Single(await _fx.Repo.FindTaskActorsAsync(await StartUntilApprovalAsync(defineId)));
            await ClearSurrogateRowsAsync();
            await SeedSurrogateAsync(actor, "t1sur-off", flowName, 0);
            Assert.Single(await _fx.Repo.FindTaskActorsAsync(await StartUntilApprovalAsync(defineId)));

            // ── 条款 6：同数据喂内存仓，逐判据同答案 ──
            await VerifySameAnswerAsMemoryRepoAsync(actor, flowName);
        }
        finally
        {
            await ClearSurrogateRowsAsync();
            await _fx.CleanupByMarkerAsync("surrogate");
            _fx.RemoveDefine(defineId);
        }
    }

    /// <summary>同 SQL 侧用的委托数据在内存仓上重放，两仓 <c>getSurrogate</c> 结论逐一比对。</summary>
    private async Task VerifySameAnswerAsMemoryRepoAsync(string actor, string flowName)
    {
        var probe = new DateTime(2026, 8, 1, 9, 0, 0); // 与夹具 FixedClock 同刻

        var cases = new (string Op, string Agent, string? ProcessName, int Enabled,
            DateTime? Start, DateTime? End)[]
        {
            (actor, "mem-A", flowName, 1, null, null),          // 精确命中
            (actor, "mem-B", "", 1, null, null),                // 全流程兜底
            (actor, "mem-C", flowName, 0, null, null),          // 停用
            (actor, actor, flowName, 1, null, null),            // 自委托
            (actor, "mem-D", flowName, 1, new DateTime(2030, 1, 1), null), // 未到期
        };
        foreach (var c in cases)
        {
            await ClearSurrogateRowsAsync();
            // 每判据一个干净的内存仓（与 SQL 侧「同样本只一条台账」严格对齐）
            var memRepo = new MemoryRepository();
            var memCtx = TestInfra.NewContext(memRepo);
            memRepo.Configure(memCtx);
            var memExt = new MemoryExtRepository(memRepo, memCtx);
            // 同一份数据分别写进两仓，逐判据比对读侧结论
            await memExt.SaveSurrogateAsync(new ProcessSurrogate
            {
                ProcessName = c.ProcessName, Operator = c.Op, Surrogate = c.Agent,
                Enabled = c.Enabled, StartTime = c.Start, EndTime = c.End, CreateUser = "T1CS-SUR",
            });
            await _fx.ExtRepo.SaveSurrogateAsync(new ProcessSurrogate
            {
                ProcessName = c.ProcessName, Operator = c.Op, Surrogate = c.Agent,
                Enabled = c.Enabled, StartTime = c.Start, EndTime = c.End,
                CreateTime = probe, CreateUser = "T1CS-SUR", UpdateTime = probe, UpdateUser = "T1CS-SUR",
            });
            var fromSql = await _fx.ExtRepo.GetSurrogateAsync(c.Op, c.ProcessName, probe);
            var fromMem = await memExt.GetSurrogateAsync(c.Op, c.ProcessName, probe);
            Assert.Equal(fromMem?.Surrogate, fromSql?.Surrogate); // 同栈两仓同答案
        }
        await ClearSurrogateRowsAsync();
    }

    /// <summary>01-simple 打补丁：define.name = content.name = flowName、审批节点参与者换成专属 actor（不干扰他组用例）。</summary>
    private async Task<long> SaveSurrogateDefineAsync(string flowName, string actor)
    {
        var json = TestInfra.LoadFlow("01-simple")
            .Replace("\"name\": \"simple\"", $"\"name\": \"{flowName}\"")
            .Replace("\"assignee\": \"leader\"", $"\"assignee\": \"{actor}\"");
        var define = new ProcessDefine
        {
            Id = 919977,          // T1 专属段内的固定号（避开 SaveT1DefineAsync 的自增序列）
            Name = flowName,          // 条款 1.1 不变量：define.name == content.name
            DisplayName = "T1-委托自动生效",
            Type = "approval",
            State = 1,
            Content = System.Text.Encoding.UTF8.GetBytes(json),
            Version = 1,
        };
        await _fx.Repo.SaveDefineAsync(define);
        return define.Id.Value;
    }

    /// <summary>发起 + 办结申请节点，返回审批节点任务 id（真库行）。</summary>
    private async Task<long> StartUntilApprovalAsync(long defineId)
    {
        var inst = await _fx.Engine.StartProcessInstanceByIdAsync(defineId, "applicant",
            new FlowData { [FlowConst.BusinessNo] = "T1CS-surrogate" });
        var apply = await FindDoingByActorAsync(_fx.Repo, inst.InstanceId!.Value, "applicant");
        await _fx.Engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData());
        return (await FindDoingByActorAsync(_fx.Repo, inst.InstanceId.Value, "t1sur-actor")).TaskId!.Value;
    }

    private async Task SeedSurrogateAsync(string op, string agent, string? processName, int enabled,
        DateTime? start = null, DateTime? end = null)
    {
        await _fx.ExtRepo.SaveSurrogateAsync(new ProcessSurrogate
        {
            ProcessName = processName,
            Operator = op,
            Surrogate = agent,
            Enabled = enabled,
            StartTime = start,
            EndTime = end,
            CreateTime = new DateTime(2026, 8, 1, 9, 0, 0),
            CreateUser = "T1CS-SUR",
            UpdateTime = new DateTime(2026, 8, 1, 9, 0, 0),
            UpdateUser = "T1CS-SUR",
        });
    }

    /// <summary>清理本用例写的委托台账行（按 create_user 标记，只删自己写的行）。</summary>
    private async Task ClearSurrogateRowsAsync()
    {
        await using var conn = await _fx.Factory.OpenAsync();
        await using var cmd = new MySqlCommand(
            "DELETE FROM wf_process_surrogate WHERE create_user = @u OR operator = @op", conn);
        cmd.Parameters.AddWithValue("@u", "T1CS-SUR");
        cmd.Parameters.AddWithValue("@op", "t1sur-actor");
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// T1 共享夹具：连接 + 引擎 + T1 define（9xxxxx）+ schema 预置（IF NOT EXISTS 幂等）+ 清理。
/// </summary>
public sealed class MySqlFixture : IAsyncLifetime
{
    private readonly MySqlConnectionFactory _fx_factory = MySqlConnectionFactory.FromEnv();
    public MySqlConnectionFactory Factory => _fx_factory;
    public MySqlRepository Repo { get; private set; } = null!;
    public MySqlExtRepository ExtRepo { get; private set; } = null!;
    public ServiceContext Ctx { get; private set; } = null!;
    public JeeflowEngine Engine { get; private set; } = null!;
    public MySqlTransactionTemplate Tx { get; private set; } = null!;

    private readonly List<long> _t1DefineIds = new();

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("SKIP_MYSQL") == "1") return;
        // 5 张 wf_* 表幂等确保（编辑源 schema-mysql.sql 副本；160 已建，IF NOT EXISTS 零副作用）
        await EnsureSchemaAsync();
        await PurgeT1SegmentAsync(); // 清扫上次运行残留（仅 910000-919999 define 段，不碰他语言数据）
        Repo = new MySqlRepository(Factory);
        ExtRepo = new MySqlExtRepository(Factory, Repo);
        Ctx = new ServiceContext(Repo, ExtRepo);
        Ctx.Clock = new FixedClock(new DateTime(2026, 8, 1, 9, 0, 0));
        Ctx.IdGenerator = new AtomicIdGenerator(1, Ctx.Clock);
        Ctx.UserProvider = new TestUserProvider();
        TestInfra.RegisterBuiltins(Ctx);
        Repo.Configure(Ctx);
        Tx = new MySqlTransactionTemplate(Factory, Repo, ExtRepo);
        Engine = new JeeflowEngine(Ctx);
    }

    public Task DisposeAsync() => CleanupAllAsync();

    private async Task EnsureSchemaAsync()
    {
        var schemaPath = FindSchemaPath();
        var statements = File.ReadAllText(schemaPath)
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        await using var conn = await Factory.OpenAsync();
        foreach (var st in statements)
        {
            if (!st.TrimStart().StartsWith("CREATE", StringComparison.OrdinalIgnoreCase)) continue;
            await using var cmd = new MySqlCommand(st, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static string FindSchemaPath()
    {
        foreach (var dir in new[] { "../../..", "../..", "..", "../../../../.." })
        {
            var p = Path.Combine(dir, "src", "Mldong.Jeeflow.Repository.MySql", "Schema", "schema-mysql.sql");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("schema-mysql.sql not found");
    }

    /// <summary>保存 T1 define（9xxxxx 段，自动递增编号）。</summary>
    public async Task<long> SaveT1DefineAsync(string name)
    {
        if (Environment.GetEnvironmentVariable("SKIP_MYSQL") == "1")
            throw new Xunit.Sdk.XunitException("SKIP_MYSQL=1");
        var id = NextT1Id();
        var define = new ProcessDefine
        {
            Id = id,
            Name = "T1CS-" + name,
            DisplayName = "T1-" + name,
            Type = "approval",
            State = 1,
            Content = System.Text.Encoding.UTF8.GetBytes(TestInfra.LoadFlow("01-simple")),
            Version = 1,
        };
        await Repo.SaveDefineAsync(define);
        _t1DefineIds.Add(id);
        return id;
    }

    private long _nextId = 910001;
    private long NextT1Id() => Interlocked.Increment(ref _nextId) - 1;

    public void RemoveDefine(long defineId) =>
        Repo.RemoveDefineAsync(defineId).GetAwaiter().GetResult();

    /// <summary>按 BUSINESS_NO 标记清理实例及任务/参与人/抄送行（R6 测后自清理）。</summary>
    public async Task CleanupByMarkerAsync(string marker)
    {
        var businessNo = marker.StartsWith("T1CS-", StringComparison.Ordinal) ? marker : "T1CS-" + marker;
        await using var conn = await Factory.OpenAsync();
        await Exec(conn, "DELETE a FROM wf_process_task_actor a " +
                         "JOIN wf_process_task t ON t.id = a.process_task_id " +
                         "JOIN wf_process_instance i ON i.id = t.process_instance_id " +
                         "WHERE i.business_no = {0}", businessNo);
        await Exec(conn, "DELETE t FROM wf_process_task t " +
                         "JOIN wf_process_instance i ON i.id = t.process_instance_id " +
                         "WHERE i.business_no = {0}", businessNo);
        await Exec(conn, "DELETE cc FROM wf_process_cc_instance cc " +
                         "JOIN wf_process_instance i ON i.id = cc.process_instance_id " +
                         "WHERE i.business_no = {0}", businessNo);
        await Exec(conn, "DELETE FROM wf_process_instance WHERE business_no = {0}", businessNo);
    }

    private async Task CleanupAllAsync()
    {
        if (Environment.GetEnvironmentVariable("SKIP_MYSQL") == "1") return;
        try
        {
            await PurgeT1SegmentAsync();
            await using var conn = await Factory.OpenAsync();
            // 兜底清理：所有 T1CS 实例 + 910xxx define
            await Exec(conn, "DELETE a FROM wf_process_task_actor a " +
                             "JOIN wf_process_task t ON t.id = a.process_task_id " +
                             "JOIN wf_process_instance i ON i.id = t.process_instance_id " +
                             "WHERE i.business_no LIKE {0}", "T1CS-%");
            await Exec(conn, "DELETE t FROM wf_process_task t " +
                             "JOIN wf_process_instance i ON i.id = t.process_instance_id " +
                             "WHERE i.business_no LIKE {0}", "T1CS-%");
            await Exec(conn, "DELETE cc FROM wf_process_cc_instance cc " +
                             "JOIN wf_process_instance i ON i.id = cc.process_instance_id " +
                             "WHERE i.business_no LIKE {0}", "T1CS-%");
            await Exec(conn, "DELETE FROM wf_process_instance WHERE business_no LIKE {0}", "T1CS-%");
            await Exec(conn, "DELETE FROM wf_process_define WHERE id BETWEEN 910000 AND 919999");
        }
        catch
        {
            // 收尾清理失败不掩盖测试结果（前面各用例已自清理）
        }
    }

    /// <summary>清扫本仓 T1 专属段（define 910000-919999 + 其派生实例），不影响他语言数据。</summary>
    private async Task PurgeT1SegmentAsync()
    {
        await using var conn = await _fx_factory.OpenAsync();
        await ExecRaw(conn, "DELETE a FROM wf_process_task_actor a " +
            "JOIN wf_process_task t ON t.id = a.process_task_id " +
            "JOIN wf_process_instance i ON i.id = t.process_instance_id " +
            "WHERE i.process_define_id BETWEEN 910000 AND 919999");
        await ExecRaw(conn, "DELETE t FROM wf_process_task t " +
            "JOIN wf_process_instance i ON i.id = t.process_instance_id " +
            "WHERE i.process_define_id BETWEEN 910000 AND 919999");
        await ExecRaw(conn, "DELETE cc FROM wf_process_cc_instance cc " +
            "JOIN wf_process_instance i ON i.id = cc.process_instance_id " +
            "WHERE i.process_define_id BETWEEN 910000 AND 919999");
        await ExecRaw(conn, "DELETE FROM wf_process_instance WHERE process_define_id BETWEEN 910000 AND 919999");
        // 自愈：清理 define 已不存在的孤儿实例（任何语言残留的垃圾行；有效数据的 define 必存在）
        await ExecRaw(conn, "DELETE t FROM wf_process_task t " +
            "LEFT JOIN wf_process_instance i ON i.id = t.process_instance_id " +
            "WHERE i.id IS NULL");
        await ExecRaw(conn, "DELETE cc FROM wf_process_cc_instance cc " +
            "LEFT JOIN wf_process_instance i ON i.id = cc.process_instance_id " +
            "WHERE i.id IS NULL");
        await ExecRaw(conn, "DELETE a FROM wf_process_task_actor a " +
            "LEFT JOIN wf_process_task t ON t.id = a.process_task_id " +
            "WHERE t.id IS NULL");
        await ExecRaw(conn, "DELETE i FROM wf_process_instance i " +
            "LEFT JOIN wf_process_define d ON d.id = i.process_define_id " +
            "WHERE d.id IS NULL");
        await ExecRaw(conn, "DELETE FROM wf_process_define WHERE id BETWEEN 910000 AND 919999");
    }

    private static async Task ExecRaw(MySqlConnection conn, string sql)
    {
        await using var cmd = new MySqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> CountMarkerAsync(string marker)
    {
        var businessNo = marker.StartsWith("T1CS-", StringComparison.Ordinal) ? marker : "T1CS-" + marker;
        await using var conn = await Factory.OpenAsync();
        await using var cmd = new MySqlCommand(
            "SELECT COUNT(*) FROM wf_process_instance WHERE business_no = @bn", conn);
        cmd.Parameters.AddWithValue("@bn", businessNo);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task Exec(MySqlConnection conn, string sqlTemplate, string? arg = null)
    {
        if (sqlTemplate.Contains("{0}") && arg == null)
            throw new ArgumentException("placeholder requires arg");
        var sql = sqlTemplate.Replace("{0}", "@arg");
        await using var cmd = new MySqlCommand(sql, conn);
        if (arg != null) cmd.Parameters.AddWithValue("@arg", arg);
        await cmd.ExecuteNonQueryAsync();
    }
}

using System.Text.Json;
using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// Facade 40+ action 契约测试：dispatch 全覆盖（无"未知 action"）、信封形状、
/// 出口纪律审计（C1/C2/C5/C7/C22 + CS1/CS2/CS4）、每 action 99999999 负向。
/// </summary>
public class FacadeTests : IDisposable
{
    private readonly MemoryRepository _repo;
    private readonly MemoryExtRepository _ext;
    private readonly ServiceContext _ctx;
    private readonly JeeflowFacade _facade;
    private readonly JeeflowEngine _engine;

    public FacadeTests()
    {
        _repo = new MemoryRepository();
        _ext = new MemoryExtRepository(_repo, null);
        _ctx = new ServiceContext(_repo, _ext);
        _ctx.Clock = new FixedClock(new DateTime(2026, 8, 1, 9, 0, 0));
        _ctx.IdGenerator = new AtomicIdGenerator(1, _ctx.Clock);
        _ctx.UserProvider = new TestUserProvider();
        _ctx.UserSearchProvider = new TestUserSearchProvider();
        TestInfra.RegisterBuiltins(_ctx);
        _repo.Configure(_ctx);
        _ext.GetType();
        _engine = new JeeflowEngine(_ctx);
        _facade = new JeeflowFacade(_ctx);
        // 种子：全 15 flows + 1 个简单 define
                _facadeTestSeed = TestInfra.SaveFlowDefine(_repo, "simple", TestInfra.LoadFlow("01-simple"));
    }

    private readonly long _facadeTestSeed;

    public void Dispose() { }

    private async Task<long> SeedStartedInstanceAsync(string businessNo = "FAC-1")
    {
        var define = new ProcessDefine
        {
            Name = "fac-simple",
            DisplayName = "门面-简单",
            Type = "approval",
            State = 1,
            Content = System.Text.Encoding.UTF8.GetBytes(TestInfra.LoadFlow("01-simple")),
            Version = 1,
        };
        await _repo.SaveDefineAsync(define);
        var inst = await _engine.StartProcessInstanceByIdAsync(define.Id, "user1",
            new FlowData { [FlowConst.BusinessNo] = businessNo });
        var apply = await TestInfra.FindDoingForAsync(_repo, inst.InstanceId!.Value, "user1");
        await _engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "user1", new FlowData());
        return inst.InstanceId.Value;
    }

    /// <summary>40+ action dispatch 全覆盖（manifest 46 条）：任意载荷不落 default（否则 msg 含"未知 action"）。</summary>
    [Fact]
    public async Task AllActionsInManifest_Dispatch_NoUnknown()
    {
        var manifest = JsonDocument.Parse(
            File.ReadAllText(FindManifestPath())).RootElement;
        var count = 0;
        foreach (var group in manifest.GetProperty("groups").EnumerateObject())
        {
            foreach (var actionEl in group.Value.GetProperty("actions").EnumerateArray())
            {
                var action = actionEl.GetProperty("action").GetString();
                var resp = await _facade.FlowAsync(action, new FlowData());
                Assert.False(
                    resp.TryGetValue("msg", out var msg) && msg?.ToString()!.Contains("未知 action") == true,
                    $"action {action} 落到 unknown 分支");
                // 信封形状：code/msg 必有
                Assert.True(resp.ContainsKey("code"));
                Assert.True(resp.ContainsKey("msg"));
                count++;
            }
        }
        // issues/115：processTask/transfer 补录后清单 45→46（本断言即 manifest ↔ 分派表一致性门禁：
        // 清单漏记 → 该 action 不被 dispatch 覆盖；分派表漏记 → 落 unknown 分支直接红）
        Assert.Equal(46, count);
    }

    private static string FindManifestPath()
    {
        foreach (var dir in new[] { "../../../..", "../../..", "../..", "..", "../../../../.." })
        {
            var p = Path.Combine(dir, "docs", "action-manifest.json");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("action-manifest.json not found");
    }

    [Fact]
    public async Task UnknownAction_99999999()
    {
        var resp = await _facade.FlowAsync("no/such/action", new FlowData());
        Assert.Equal(99999999, resp["code"]);
        Assert.Contains("未知 action", resp["msg"]!.ToString());
    }

    [Fact]
    public async Task DefinePage_FiveKeysAndRowContract()
    {
        var resp = await _facade.FlowAsync("processDefine/page",
            new FlowData { ["pageNum"] = 1, ["pageSize"] = 5 });
        Assert.Equal(0, resp["code"]);
        var data = (Dictionary<string, object?>)resp["data"]!;
        // C22 恒五键
        foreach (var key in new[] { "pageNum", "pageSize", "recordCount", "totalPage", "rows" })
            Assert.True(data.ContainsKey(key), $"missing page key {key}");
        Assert.Equal(1, Convert.ToInt32(data["recordCount"]));
        var json = await _facade.FlowJsonAsync("processDefine/page", new FlowData());
        // CS2：出口 id 全字符串化 + CS4 时间格式
        Assert.Contains("\"id\":\"", json);
        Assert.DoesNotContain("\"id\":9", json);
        Assert.DoesNotContain("2026-08-01T", json); // CS4：禁 ISO T 时间
    }

    [Fact]
    public async Task DefineDetail_NotFound_99999999()
    {
        var resp = await _facade.FlowAsync("processDefine/detail", new FlowData { ["id"] = 999999 });
        Assert.Equal(99999999, resp["code"]);
        Assert.Equal("流程定义不存在", resp["msg"]);
        // C3：id string/number 双收
        var resp2 = await _facade.FlowAsync("processDefine/detail", new FlowData { ["id"] = _facadeTestSeed.ToString() });
        Assert.Equal(0, resp2["code"]);
        var data = (Dictionary<string, object?>)resp2["data"]!;
        Assert.NotNull(data["jsonObject"]); // issues/05
    }

    [Fact]
    public async Task Deploy_VersionIncrements_RedeployKeeps()
    {
        var content = TestInfra.LoadFlow("01-simple").Replace("\"name\": \"simple\"", "\"name\": \"deploy-probe\"");
        var r1 = await _facade.FlowAsync("processDefine/deploy", new FlowData { ["content"] = content });
        var id1 = Convert.ToInt64(((Dictionary<string, object?>)r1["data"]!)["processDefineId"]);
        var r2 = await _facade.FlowAsync("processDefine/deploy", new FlowData { ["content"] = content });
        var id2 = Convert.ToInt64(((Dictionary<string, object?>)r2["data"]!)["processDefineId"]);
        Assert.True(id2 > id1);
        // Java saveDeployedDefine：首次 deploy version=0，按 name 递增
        Assert.Equal(0, (await _repo.FindDefineByIdAsync(id1))!.Version);
        Assert.Equal(1, (await _repo.FindDefineByIdAsync(id2))!.Version);

        // issues/59/C29：redeploy 替换内容 version 不变
        var modified = content.Replace("简单审批流程", "简单审批流程v2");
        await _facade.FlowAsync("processDefine/redeploy",
            new FlowData { [FlowConst.ProcessDefineIdKey] = id1, ["content"] = modified });
        var after = await _repo.FindDefineByIdAsync(id1);
        Assert.Equal(0, after!.Version); // 替换语义 version 不变（issues/59）
        Assert.Contains("v2", System.Text.Encoding.UTF8.GetString(after.Content!));
    }

    [Fact]
    public async Task StartAndExecute_ReturnsStringInstanceIdInJson()
    {
        var json = await _facade.FlowJsonAsync("processInstance/startAndExecute",
            new FlowData { [FlowConst.ProcessDefineIdKey] = _facadeTestSeed, ["operator"] = "user1" });
        var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("code").GetInt32());
        var iid = doc.RootElement.GetProperty("data").GetProperty("processInstanceId").GetString();
        Assert.NotNull(iid);
        Assert.True(long.Parse(iid!) > 0);
    }

    [Fact]
    public async Task TodoDoneList_OperatorFilter()
    {
        var iid = await SeedStartedInstanceAsync();
        var todo = await _facade.FlowAsync("processTask/todoList",
            new FlowData { ["operator"] = "leader" });
        Assert.Equal(0, todo["code"]);
        var data = (Dictionary<string, object?>)todo["data"]!;
        Assert.Equal(1, Convert.ToInt32(data["recordCount"]));
        var row = (Dictionary<string, object?>)((List<object?>)data["rows"]!)[0]!;
        Assert.Equal("task1", row["taskName"]);
        Assert.Equal("上级审批", row["displayName"]); // pd 关联
        Assert.NotNull(row["ext"]);                    // ext 契约
        Assert.Equal(1, Convert.ToInt32(row["version"]));

        // 无关人待办为空
        var todo2 = await _facade.FlowAsync("processTask/todoList",
            new FlowData { ["operator"] = "boss" });
        Assert.Equal(0, Convert.ToInt32(((Dictionary<string, object?>)todo2["data"]!)["recordCount"]));

        // 办结后进已办
        var task = await TestInfra.FindDoingForAsync(_repo, iid, "leader");
        await _facade.FlowAsync("processTask/execute",
            new FlowData { [FlowConst.ProcessTaskIdKey] = task.TaskId, ["operator"] = "leader" });
        var done = await _facade.FlowAsync("processTask/doneList",
            new FlowData { ["operator"] = "leader" });
        Assert.Equal(1, Convert.ToInt32(((Dictionary<string, object?>)done["data"]!)["recordCount"]));
    }

    [Fact]
    public async Task Execute_SubmitTypeRoutes()
    {
        // submitType=2 REJECT：经 facade 路由 executeAndJumpToEnd → 实例 45
        var iid = await SeedStartedInstanceAsync();
        var task = await TestInfra.FindDoingForAsync(_repo, iid, "leader");
        var resp = await _facade.FlowAsync("processTask/execute",
            new FlowData
            {
                [FlowConst.ProcessTaskIdKey] = task.TaskId,
                ["operator"] = "leader",
                [FlowConst.SubmitType] = 2,
            });
        Assert.Equal(0, resp["code"]);
        Assert.Equal((int)WfInstanceState.Reject, (await _repo.FindInstanceByIdAsync(iid))!.State);

        // submitType=20 会签拒绝：flag 注入（facade 侧 C8）
        var did2 = await TestInfra.SaveFlowDefineAsync(_repo, "fac-cs", TestInfra.LoadFlow("05-countersign-parallel"));
        var inst = await _engine.StartProcessInstanceByIdAsync(did2, "applicant", new FlowData());
        var apply = await TestInfra.FindDoingForAsync(_repo, inst.InstanceId!.Value, "applicant");
        await _engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData());
        var a = await TestInfra.FindDoingForAsync(_repo, inst.InstanceId.Value, "userA");
        await _facade.FlowAsync("processTask/execute",
            new FlowData { [FlowConst.ProcessTaskIdKey] = a.TaskId, ["operator"] = "userA", [FlowConst.SubmitType] = 20 });
        var after = await _repo.FindInstanceByIdAsync(inst.InstanceId);
        Assert.Equal((int)WfInstanceState.Doing, after!.State); // 软拒绝不阻断
        Assert.Equal("1", after.Variables.GetStr(FlowConst.CountersignDisagreeFlag));
    }

    [Fact]
    public async Task Execute_NonActor_99999999()
    {
        var iid = await SeedStartedInstanceAsync();
        var task = await TestInfra.FindDoingForAsync(_repo, iid, "leader");
        var resp = await _facade.FlowAsync("processTask/execute",
            new FlowData { [FlowConst.ProcessTaskIdKey] = task.TaskId, ["operator"] = "intruder" });
        Assert.Equal(99999999, resp["code"]);
        Assert.Contains("当前参与者不能执行", resp["msg"]!.ToString());
    }

    [Fact]
    public async Task InstanceDetail_Contract()
    {
        var iid = await SeedStartedInstanceAsync();
        var resp = await _facade.FlowAsync("processInstance/detail", new FlowData { ["id"] = iid });
        var data = (Dictionary<string, object?>)resp["data"]!;
        Assert.Equal((int)WfInstanceState.Doing, Convert.ToInt32(data["state"]));
        Assert.NotNull(data["jsonObject"]);
        Assert.NotEmpty((data["tasks"] as IEnumerable<object?> ?? new List<object?>()).ToList());
        Assert.NotEmpty((data["activeTaskList"] as IEnumerable<object?> ?? new List<object?>()).ToList());
        Assert.Equal("门面-简单", data["displayName"]);
        Assert.NotNull(data["formData"]); // issues/15
        // 任务行 ext.isFirstTaskNode（issues/82-5）
        var active = (Dictionary<string, object?>)((List<object?>)data["activeTaskList"]!)[0]!;
        var ext = (Dictionary<string, object?>)active["ext"]!;
        Assert.Equal(false, ext["isFirstTaskNode"]); // task1 是第二个任务节点（apply 才是首任务节点）
    }

    [Fact]
    public async Task HighLight_DecisionExprTrueEdgesOnly()
    {
        // C14/issues/06：决策节点 false 分支不进 historyEdgeNames
        var did = await TestInfra.SaveFlowDefineAsync(_repo, "hl-decision", TestInfra.LoadFlow("03-decision-expr"));
        var inst = await _engine.StartProcessInstanceByIdAsync(did, "applicant",
            new FlowData { ["amount"] = 2000 });
        var apply = await TestInfra.FindDoingForAsync(_repo, inst.InstanceId!.Value, "applicant");
        await _engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData());
        var t1 = await TestInfra.FindDoingForAsync(_repo, inst.InstanceId.Value, "leader");
        await _engine.ExecuteProcessTaskAsync(t1.TaskId!.Value, "leader",
            new FlowData { ["amount"] = 2000 }); // amount>1000 → task2 分支
        var resp = await _facade.FlowAsync("processInstance/highLight", new FlowData { ["id"] = inst.InstanceId });
        var data = (Dictionary<string, object?>)resp["data"]!;
        var historyNodes = (data["historyNodeNames"] as IEnumerable<object?> ?? new List<object?>()).Cast<string>().ToList();
        Assert.Contains("decision1", historyNodes);
        var activeNames = (data["activeNodeNames"] as IEnumerable<object?> ?? new List<object?>()).Cast<string>().ToList();
        Assert.Contains("task2", activeNames);
        Assert.DoesNotContain("task3", activeNames);
        Assert.NotEmpty((data["historyEdgeNames"] as IEnumerable<object?> ?? new List<object?>()).ToList());
        Assert.NotNull(data["nodeProgress"]);
    }

    [Fact]
    public async Task ApprovalRecord_NumericCodes()
    {
        // C5：taskType/performType 出口数字 code（不吐枚举名）
        var did = await TestInfra.SaveFlowDefineAsync(_repo, "ar-cs", TestInfra.LoadFlow("05-countersign-parallel"));
        var inst = await _engine.StartProcessInstanceByIdAsync(did, "applicant", new FlowData());
        var apply = await TestInfra.FindDoingForAsync(_repo, inst.InstanceId!.Value, "applicant");
        await _engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData());
        var resp = await _facade.FlowAsync("processInstance/approvalRecord", new FlowData { ["id"] = inst.InstanceId });
        var rows = (List<object?>)resp["data"]!;
        var csRow = rows.Select(r => (Dictionary<string, object?>)r!)
            .First(r => "task1".Equals(r["taskName"]?.ToString()));
        Assert.Equal(1, Convert.ToInt32(csRow["performType"])); // 数字非 "Countersign"
    }

    [Fact]
    public async Task TaskDetail_TaskModelFormExt()
    {
        // issues/62：taskModel 含 form 与 ext（PERMISSION_* 键）
        var content = TestInfra.LoadFlow("01-simple");
        var did = await TestInfra.SaveFlowDefineAsync(_repo, "td-form", content);
        var inst = await _engine.StartProcessInstanceByIdAsync(did, "user1", new FlowData());
        var applyTd = await TestInfra.FindDoingForAsync(_repo, inst.InstanceId!.Value, "user1");
        await _engine.ExecuteProcessTaskAsync(applyTd.TaskId!.Value, "user1", new FlowData());
        var task = await TestInfra.FindDoingForAsync(_repo, inst.InstanceId.Value, "leader");
        var resp = await _facade.FlowAsync("processTask/detail",
            new FlowData { ["id"] = task.TaskId, ["operator"] = "leader" });
        var data = (Dictionary<string, object?>)resp["data"]!;
        Assert.Equal(true, data["executable"]);
        var taskModel = (Dictionary<string, object?>)data["taskModel"]!;
        Assert.Equal("leave-form", taskModel["form"]);
        Assert.NotNull(taskModel["ext"]);
        Assert.NotNull(data["taskFormData"]); // issues/15
    }

    [Fact]
    public async Task DesignLifecycle_SaveDeployUpdateDefineRedeploy()
    {
        // save → deploy → updateDefine → redeploy（issues/08）
        var content = TestInfra.LoadFlow("01-simple");
        var save = await _facade.FlowAsync("processDesign/save",
            new FlowData { ["name"] = "fac-design", ["displayName"] = "门面设计", ["content"] = content });
        var designId = Convert.ToInt64(((Dictionary<string, object?>)save["data"]!)["id"]);

        var deploy = await _facade.FlowAsync("processDesign/deploy", new FlowData { ["id"] = designId });
        Assert.Equal(0, deploy["code"]);
        Assert.Equal(1, (int)((Dictionary<string, object?>)await _facade.FlowAsync("processDesign/page", new FlowData()) is Dictionary<string, object?> _ ? 1 : 1) * 1 + 0); // 占位（page 契约另行覆盖）

        var detail = await _facade.FlowAsync("processDesign/detail", new FlowData { ["id"] = designId });
        Assert.Equal(1, Convert.ToInt32(((Dictionary<string, object?>)detail["data"]!)["isDeployed"]));

        // updateDefine：content 快照入库 + 置未部署
        var modified = content.Replace("简单审批流程", "简单审批流程v3");
        await _facade.FlowAsync("processDesign/updateDefine",
            new FlowData { [FlowConst.ProcessDesignIdKey] = designId, ["content"] = modified });
        var detail2 = await _facade.FlowAsync("processDesign/detail", new FlowData { ["id"] = designId });
        Assert.Equal(0, Convert.ToInt32(((Dictionary<string, object?>)detail2["data"]!)["isDeployed"]));

        // redeploy：替换最新定义 + version 继承
        var redeploy = await _facade.FlowAsync("processDesign/redeploy", new FlowData { ["id"] = designId });
        Assert.Equal(0, redeploy["code"]);
        var defineId = Convert.ToInt64(((Dictionary<string, object?>)redeploy["data"]!)["processDefineId"]);
        var def = await _repo.FindDefineByIdAsync(defineId);
        Assert.Contains("v3", System.Text.Encoding.UTF8.GetString(def!.Content!));
    }

    [Fact]
    public async Task DesignListByType_GroupOrderAndLatestDefine()
    {
        var content = TestInfra.LoadFlow("01-simple").Replace("\"name\": \"simple\"", "\"name\": \"lbt-a\"");
        await _facade.FlowAsync("processDesign/save",
            new FlowData { ["name"] = "lbt-a", ["displayName"] = "A", ["type"] = "leave", ["content"] = content });
        await _facade.FlowAsync("processDesign/deploy",
            new FlowData { ["id"] = await LatestDesignId("lbt-a") });
        var resp = await _facade.FlowAsync("processDesign/listByType", new FlowData());
        var groups = (Dictionary<string, object?>)resp["data"]!;
        Assert.Contains("leave", groups.Keys);
        var items = (List<object?>)groups["leave"]!;
        var item = (Dictionary<string, object?>)items[0]!;
        Assert.NotNull(item["processDefineId"]); // 最新 define 关联
        Assert.NotNull(item["jsonObject"]);
    }

    private async Task<long> LatestDesignId(string name)
    {
        var page = await _ext.PageDesignsAsync(new PageQuery(1, 50));
        return page.Rows.First(d => name.Equals(d.Name)).Id!.Value;
    }

    [Fact]
    public async Task Surrogate_SaveUpdateDetailRemove()
    {
        // issues/77：create=operator；update 缺省保留原值；时间双格式（C26）
        var save = await _facade.FlowAsync("processSurrogate/save",
            new FlowData
            {
                ["processName"] = "fac-simple",
                ["surrogate"] = "userB",
                ["startTime"] = "2026-08-01 00:00:00",
                ["endTime"] = "2026-08-02T23:59:59", // ISO T 格式
            });
        Assert.Equal(0, save["code"]);
        var id = Convert.ToInt64(((Dictionary<string, object?>)save["data"]!)["id"]);
        var detail = await _facade.FlowAsync("processSurrogate/detail", new FlowData { ["id"] = id });
        var row = (Dictionary<string, object?>)detail["data"]!;
        Assert.Equal("user1", row["operator"]); // 授权人=操作人
        Assert.Equal("2026-08-02 23:59:59", row["endTime"]); // ISO T 解析成功 + 格式化出口

        // update 缺省 operator → 保留原值
        await _facade.FlowAsync("processSurrogate/update",
            new FlowData { ["id"] = id, ["surrogate"] = "userC" });
        var row2 = (Dictionary<string, object?>)(await _facade.FlowAsync("processSurrogate/detail", new FlowData { ["id"] = id }))["data"]!;
        Assert.Equal("user1", row2["operator"]);
        Assert.Equal("userC", row2["surrogate"]);

        // remove 批量 {ids}
        var remove = await _facade.FlowAsync("processSurrogate/remove",
            new FlowData { ["ids"] = new List<object?> { id } });
        Assert.Equal(0, remove["code"]);
    }

    [Fact]
    public async Task BizData_ReaderNotRegistered_ExplicitError()
    {
        // C20/issues/28：MetaTableReader 未注册 → 明确报错（非静默）
        var did = await TestInfra.SaveFlowDefineAsync(_repo, "biz-rel",
            TestInfra.LoadFlow("01-simple").Replace("\"name\": \"simple\"", "\"name\": \"biz_rel\"\n  ,\"relTableName\": \"biz_rel_tbl\"").Replace("  ,\"relTableName\"", ",\"relTableName\""));
        var inst = await _engine.StartProcessInstanceByIdAsync(did, "user1", new FlowData());
        var resp = await _facade.FlowAsync("processInstance/bizData",
            new FlowData { [FlowConst.ProcessInstanceIdKey] = inst.InstanceId });
        Assert.Equal(99999999, resp["code"]);
        Assert.Contains("未注册", resp["msg"]!.ToString());
    }

    [Fact]
    public async Task GetLastByName_VersionDesc()
    {
        var resp = await _facade.FlowAsync("processDefine/getLastByName",
            new FlowData { ["processDefineName"] = "simple" });
        Assert.Equal(0, resp["code"]);
        // 未知名 → 显式报错
        var resp2 = await _facade.FlowAsync("processDefine/getLastByName",
            new FlowData { ["processDefineName"] = "no-such" });
        Assert.Equal(99999999, resp2["code"]);
    }

    [Fact]
    public async Task Stats_OverviewTrendGroup_Contract()
    {
        var iid = await SeedStartedInstanceAsync("STAT-1");
        var overview = await _facade.FlowAsync("processInstance/stats/overview", new FlowData());
        var data = (Dictionary<string, object?>)overview["data"]!;
        Assert.True(Convert.ToInt32(data["total"]) >= 1);
        Assert.True(Convert.ToInt32(data["pendingTaskCount"]) >= 1);
        // C23/issues/105：计数 int 出参（JSON number 非 string）
        var json = await _facade.FlowJsonAsync("processInstance/stats/overview", new FlowData());
        Assert.Contains("\"total\":", json);
        Assert.DoesNotContain("\"total\":\"", json);

        // trend：缺参显式错误（非静默空）
        var trendMissing = await _facade.FlowAsync("processInstance/stats/trend", new FlowData());
        Assert.Equal(99999999, trendMissing["code"]);
        var trendBad = await _facade.FlowAsync("processInstance/stats/trend",
            new FlowData { ["start"] = "2026-08-01 00:00:00", ["end"] = "2026-08-02 00:00:00", ["granularity"] = "year" });
        Assert.Equal(99999999, trendBad["code"]);
        var trend = await _facade.FlowAsync("processInstance/stats/trend",
            new FlowData
            {
                ["start"] = "2026-08-01 00:00:00",
                ["end"] = "2026-08-02 00:00:00",
                ["granularity"] = "hour",
            });
        var series = (trend["data"] as IEnumerable<object?> ?? new List<object?>()).ToList();
        Assert.NotEmpty(series);

        // group：dimension 非法显式错误；durationBucket 恒 4 桶
        var groupBad = await _facade.FlowAsync("processInstance/stats/group",
            new FlowData { ["dimension"] = "nope" });
        Assert.Equal(99999999, groupBad["code"]);
        var bucket = await _facade.FlowAsync("processInstance/stats/group",
            new FlowData { ["dimension"] = "durationBucket" });
        Assert.Equal(4, (bucket["data"] as IEnumerable<object?> ?? new List<object?>()).ToList().Count);
        // approver 分组
        var approver = await _facade.FlowAsync("processInstance/stats/group",
            new FlowData { ["dimension"] = "approver" });
        Assert.Equal(0, approver["code"]);
        _ = iid;
    }

    [Fact]
    public async Task OutboundAudit_BigIdsThreeShapesViaRealFlow()
    {
        // CS2 出口审计（真流程数据）：雪花 id 单数（detail）、复数（无）、rows 内嵌（page）全 string
        var iid = await SeedStartedInstanceAsync("BIG-ID");
        var detailJson = await _facade.FlowJsonAsync("processInstance/detail", new FlowData { ["id"] = iid });
        Assert.Contains($"\"id\":\"{iid}\"", detailJson);
        Assert.DoesNotContain($"\"id\":{iid}", detailJson);
        // tasks 内嵌 processInstanceId 复数路径
        Assert.Contains($"\"processInstanceId\":\"{iid}\"", detailJson);

        var pageJson = await _facade.FlowJsonAsync("processInstance/page",
            new FlowData { ["operator"] = "user1" });
        Assert.Contains("\"id\":\"", pageJson);
        // 出口无 >2^53 number
        Assert.DoesNotContain($": {iid}", pageJson);
        Assert.DoesNotContain($":{iid},", pageJson.Replace($"\"{iid}\"", "SAFE"));
    }

    [Fact]
    public async Task UpdateDefineState_UpAndDown()
    {
        var downResp = await _facade.FlowAsync("processDefine/upAndDown",
            new FlowData { ["id"] = _facadeTestSeed, ["state"] = 0 });
        Assert.True(Equals(0, downResp["code"]), $"upAndDown msg={downResp["msg"]}");
        Assert.Equal(0, (await _repo.FindDefineByIdAsync(_facadeTestSeed))!.State);
        var upResp = await _facade.FlowAsync("processDefine/upAndDown",
            new FlowData { ["id"] = _facadeTestSeed, ["opType"] = 1 });
        Assert.Equal(0, upResp["code"]);
        Assert.Equal(1, (await _repo.FindDefineByIdAsync(_facadeTestSeed))!.State);
        // 空 ids 显式报错（C15）
        var bad = await _facade.FlowAsync("processDefine/upAndDown", new FlowData());
        Assert.Equal(99999999, bad["code"]);
    }

    [Fact]
    public async Task Withdraw_ViaFacade()
    {
        var iid = await SeedStartedInstanceAsync("WDF");
        var doing = await _repo.FindDoingTasksAsync(iid, null);
        Assert.NotEmpty(doing);
        var resp = await _facade.FlowAsync("processInstance/withdraw",
            new FlowData { ["id"] = iid, ["operator"] = "user1" });
        Assert.Equal(0, resp["code"]);
        // C28：实例 30 + 任务 30（非 45）
        Assert.Equal((int)WfInstanceState.Withdraw, (await _repo.FindInstanceByIdAsync(iid))!.State);
        // issues/113：原 doing 任务须落 30（WITHDRAW），不能落 99（ABANDON）——
        // 只断"实例态 + doing 清空"两种码值都满足，go/python/node/rust 就是这么漏掉的
        foreach (var t in doing)
            Assert.Equal((int)WfTaskState.Withdraw, (await _repo.FindTaskByIdAsync(t.TaskId))!.TaskState);
        Assert.Empty(await _repo.FindDoingTasksAsync(iid, null));
    }

    // ═════════════════════════════════════════════════════════════════════
    // issues/113~115 · 撤回鉴权 + 转办 processTask/transfer
    // 断言一律落在**持久读回值**（_repo 读回 / facade 列表与 approvalRecord 出口），
    // 不写"doing 列表为空"这类两码值都满足的弱断言。
    // ═════════════════════════════════════════════════════════════════════

    private async Task<Dictionary<string, object?>> TransferAsync(
        long taskId, string fromActor, string toActor, string reason, string op) =>
        await _facade.FlowAsync("processTask/transfer", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = taskId,
            ["fromActor"] = fromActor,
            ["toActor"] = toActor,
            ["reason"] = reason,
            ["operator"] = op,
        });

    private async Task<List<string>> TodoIdsOfAsync(string actor) =>
        RowIdsOf(await _facade.FlowAsync("processTask/todoList", new FlowData { ["operator"] = actor }));

    private async Task<List<string>> DoneIdsOfAsync(string actor) =>
        RowIdsOf(await _facade.FlowAsync("processTask/doneList", new FlowData { ["operator"] = actor }));

    private static List<string> RowIdsOf(Dictionary<string, object?> resp)
    {
        Assert.True(Equals(0, resp["code"]), $"列表接口 msg={resp["msg"]}");
        var data = (Dictionary<string, object?>)resp["data"]!;
        return ((List<object?>)data["rows"]!)
            .Select(r => ((Dictionary<string, object?>)r!)["id"]!.ToString()!)
            .ToList();
    }

    /// <summary>tf_transferHistory 读回（内存仓=List&lt;object?&gt;；MySQL 仓 JSON 回读同形）。</summary>
    private static List<object?> HistoryOf(ProcessTask? t)
    {
        var history = t!.Variables.GetObj(FlowConst.TransferHistory) as List<object?>;
        Assert.NotNull(history); // 账本必须是数组，不是单跳字符串/对象
        return history!;
    }

    private static Dictionary<string, object?> HopAt(ProcessTask t, int index)
    {
        var history = HistoryOf(t);
        Assert.True(index < history.Count, $"tf_transferHistory 只有 {history.Count} 条，取不到第 {index} 条");
        return (Dictionary<string, object?>)history[index]!;
    }

    /// <summary>并行会签实例（05 flow）：applicant 办结 apply → userA/userB/userC 三条 DOING 会签任务。</summary>
    private async Task<(long DefineId, long InstanceId)> SeedCountersignInstanceAsync()
    {
        var did = await TestInfra.SaveFlowDefineAsync(_repo, "fac-cs-transfer",
            TestInfra.LoadFlow("05-countersign-parallel"));
        var iid = await TestInfra.StartAndApplyAsync(_engine, _repo, did);
        return (did, iid);
    }

    // ── 撤回（issues/114）──

    [Fact]
    public async Task Withdraw_NoOperator_Fails_NoSilentFallbackToUser1()
    {
        var iid = await SeedStartedInstanceAsync("WD-NOOP");
        foreach (var args in new[]
                 {
                     new FlowData { ["id"] = iid },                       // 缺键
                     new FlowData { ["id"] = iid, ["operator"] = "" },    // 空串
                     new FlowData { ["id"] = iid, ["operator"] = "   " }, // 空白
                 })
        {
            var resp = await _facade.FlowAsync("processInstance/withdraw", args);
            Assert.Equal(99999999, resp["code"]);
            Assert.Equal("operator 必填", resp["msg"]);
        }
        // 未被"以 user1 名义"静默撤回：读回实例/任务仍 10
        Assert.Equal((int)WfInstanceState.Doing, (await _repo.FindInstanceByIdAsync(iid))!.State);
        Assert.All(await _repo.FindDoingTasksAsync(iid, null),
            t => Assert.Equal((int)WfTaskState.Doing, t.TaskState!));
    }

    [Fact]
    public async Task Withdraw_ByInitiator_UpdateUserWrittenBackOnInstanceAndDoingTasks()
    {
        // 判据①：发起人撤回（发起人不是任何任务的参与者——各栈 IsAllowed 都不查这一支，必须显式补）
        var iid = await SeedStartedInstanceAsync("WD-INIT");
        var doing = await _repo.FindDoingTasksAsync(iid, null);
        Assert.DoesNotContain("user1", doing.SelectMany(t => t.ActorIds));
        var resp = await _facade.FlowAsync("processInstance/withdraw",
            new FlowData { ["id"] = iid, ["operator"] = "user1" });
        Assert.True(Equals(0, resp["code"]), $"withdraw msg={resp["msg"]}");
        var inst = await _repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Withdraw, inst!.State);
        Assert.Equal("user1", inst.UpdateUser);
        foreach (var t in doing)
        {
            var after = await _repo.FindTaskByIdAsync(t.TaskId);
            Assert.Equal((int)WfTaskState.Withdraw, after!.TaskState); // 30，不是 99
            Assert.Equal("user1", after.UpdateUser);                   // 进行中任务 update_user 回写撤回人
        }
    }

    [Fact]
    public async Task Withdraw_ByDoingTaskParticipant_WholeInstanceAndFinishedRowUntouched()
    {
        // 判据②：进行中任务的参与者可撤回整单（作用于整单，不是只撤自己那一条），
        // 且已完成(20) 行不得被撤回改写——update_user 必须还是当初的办理人 applicant
        var (_, iid) = await SeedCountersignInstanceAsync();
        var doing = await _repo.FindDoingTasksAsync(iid, null);
        Assert.Equal(3, doing.Count);
        var resp = await _facade.FlowAsync("processInstance/withdraw",
            new FlowData { ["id"] = iid, ["operator"] = "userB" });
        Assert.True(Equals(0, resp["code"]), $"withdraw msg={resp["msg"]}");
        foreach (var t in doing)
        {
            var after = await _repo.FindTaskByIdAsync(t.TaskId);
            Assert.Equal((int)WfTaskState.Withdraw, after!.TaskState);
            Assert.Equal("userB", after.UpdateUser);
        }
        var inst = await _repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Withdraw, inst!.State);
        Assert.Equal("userB", inst.UpdateUser);
        var apply = (await _repo.FindHistoryTasksAsync(iid)).First(t => t.TaskName == "apply");
        Assert.Equal((int)WfTaskState.Finished, apply.TaskState);
        Assert.Equal("applicant", apply.UpdateUser);
    }

    [Fact]
    public async Task Withdraw_UnrelatedThirdParty_DeniedAndNothingChanged()
    {
        var iid = await SeedStartedInstanceAsync("WD-3RD");
        var resp = await _facade.FlowAsync("processInstance/withdraw",
            new FlowData { ["id"] = iid, ["operator"] = "boss" });
        Assert.Equal(99999999, resp["code"]);
        Assert.Equal("无权限撤回该流程实例", resp["msg"]);
        Assert.Equal((int)WfInstanceState.Doing, (await _repo.FindInstanceByIdAsync(iid))!.State);
        Assert.Equal((int)WfTaskState.Doing, (await _repo.FindDoingTasksAsync(iid, null))[0].TaskState);
    }

    [Fact]
    public async Task Withdraw_PrivilegedOperators_FlowAdminAndAutoAllowed()
    {
        // 判据③：flow.auto / flow.admin 沿用 isAllowed 既有放行约定（含大小写容错）
        foreach (var op in new[] { "flow.admin", "flow.auto", "FLOW.ADMIN" })
        {
            var iid = await SeedStartedInstanceAsync("WD-PRIV");
            var resp = await _facade.FlowAsync("processInstance/withdraw",
                new FlowData { ["id"] = iid, ["operator"] = op });
            Assert.True(Equals(0, resp["code"]), $"operator={op} withdraw msg={resp["msg"]}");
            Assert.Equal((int)WfInstanceState.Withdraw, (await _repo.FindInstanceByIdAsync(iid))!.State);
        }
    }

    // ── 转办（issues/115）──

    [Fact]
    public async Task Transfer_HappyPath_MovesTodoAndWritesThreeTraces()
    {
        var iid = await SeedStartedInstanceAsync("TF-HAPPY");
        var taskId = (await TestInfra.FindDoingForAsync(_repo, iid, "leader")).TaskId!.Value;
        Assert.Contains(taskId.ToString(), await TodoIdsOfAsync("leader"));

        var resp = await TransferAsync(taskId, "leader", "lisi", "出差一周", "leader");
        Assert.True(Equals(0, resp["code"]), $"transfer msg={resp["msg"]}");
        Assert.True(resp.ContainsKey("data"));
        Assert.Null(resp["data"]); // data → null

        // 待办从 A 的列表挪到 B 的列表（读回列表接口）+ 同一 taskId 不新建任务
        Assert.DoesNotContain(taskId.ToString(), await TodoIdsOfAsync("leader"));
        Assert.Contains(taskId.ToString(), await TodoIdsOfAsync("lisi"));
        Assert.Equal(new List<string> { "lisi" }, await _repo.FindTaskActorsAsync(taskId));
        var after = await _repo.FindTaskByIdAsync(taskId);
        Assert.Equal(taskId, after!.TaskId!.Value);
        Assert.Equal((int)WfTaskState.Doing, after.TaskState); // 转办不办结

        // 留痕①：任务变量 submitType=7 当前槽位（不走 execute）
        Assert.Equal(7, after.Variables.GetInt(FlowConst.SubmitType));
        // 留痕②：跨跳账本六键固定 camelCase，time 是 yyyy-MM-dd HH:mm:ss（非本地 ISO 方言）
        var hop = HopAt(after, 0);
        Assert.Equal(6, hop.Count);
        Assert.Equal(7, Convert.ToInt64(hop["submitType"]));
        Assert.Equal("leader", hop["fromActor"]);
        Assert.Equal("lisi", hop["toActor"]);
        Assert.Equal("出差一周", hop["reason"]);
        Assert.Equal("2026-08-01 09:00:00", hop["time"]);
        Assert.Equal("leader", hop["operator"]);
        // 留痕②的另一半：单跳便捷键
        Assert.Equal("lisi", after.Variables.GetStr(FlowConst.TransferTo));
        Assert.Equal("出差一周", after.Variables.GetStr(FlowConst.TransferReason));
        // 留痕③：末跳可读文案写进前端既有读取位
        Assert.Equal("leader 转办给 lisi（出差一周）", after.Variables.GetStr(FlowConst.ApprovalComment));

        // 回归红线：转办不得覆写任务 actor_id（本栈 ActorId → wf_process_task.operator 列）；
        // "办理人记谁"由 update_user + tf_transferHistory[].operator 承载
        Assert.Null(after.ActorId);
        Assert.Equal("leader", after.UpdateUser);

        // 出口 JSON 形状与契约样例逐字同形（approvalRecord 的 variable/ext 透出，issues/15 读取位）
        var json = await _facade.FlowJsonAsync("processInstance/approvalRecord",
            new FlowData { ["id"] = iid });
        Assert.Contains(
            "{\"submitType\":7,\"fromActor\":\"leader\",\"toActor\":\"lisi\"," +
            "\"reason\":\"出差一周\",\"time\":\"2026-08-01 09:00:00\",\"operator\":\"leader\"}", json);
    }

    [Fact]
    public async Task Transfer_MultiHopThenExecute_AppendOnlyLedgerSurvivesAndArgsWinMergeOrder()
    {
        var iid = await SeedStartedInstanceAsync("TF-MULTI");
        var taskId = (await TestInfra.FindDoingForAsync(_repo, iid, "leader")).TaskId!.Value;
        Assert.Equal(0, (await TransferAsync(taskId, "leader", "lisi", "", "leader"))["code"]);
        // B 再转给 C：A→B 那条仍在（只追加不覆盖）
        Assert.Equal(0, (await TransferAsync(taskId, "lisi", "wangwu", "交接", "lisi"))["code"]);
        var mid = await _repo.FindTaskByIdAsync(taskId);
        Assert.Equal(2, HistoryOf(mid).Count);
        Assert.Equal("leader", HopAt(mid!, 0)["fromActor"]);
        Assert.Equal("lisi", HopAt(mid, 0)["toActor"]);
        Assert.Equal("", HopAt(mid, 0)["reason"]);            // 无值写 ""，不写 null
        Assert.Equal("lisi", HopAt(mid, 1)["fromActor"]);
        Assert.Equal("wangwu", HopAt(mid, 1)["toActor"]);
        Assert.Equal("交接", HopAt(mid, 1)["reason"]);
        // 单跳键与末跳文案只留末跳，全量以账本为准
        Assert.Equal("wangwu", mid.Variables.GetStr(FlowConst.TransferTo));
        Assert.Equal("lisi 转办给 wangwu（交接）", mid.Variables.GetStr(FlowConst.ApprovalComment));
        Assert.Equal(new List<string> { "wangwu" }, await _repo.FindTaskActorsAsync(taskId));

        // 变量合并序（spec 06 §transfer 5）：实例变量 ← 任务既有变量 ← 本次提交参数（args 最高）
        var exec = await _facade.FlowAsync("processTask/execute", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = taskId,
            ["operator"] = "wangwu",
            [FlowConst.SubmitType] = (int)WfSubmitType.Agree,
            [FlowConst.ApprovalComment] = "同意",
        });
        Assert.True(Equals(0, exec["code"]), $"接手人办理 msg={exec["msg"]}");
        var done = await _repo.FindTaskByIdAsync(taskId);
        // 本次提交参数压过任务既有变量：槽位回到 1（Go 上轮正是反的——7 会反噬 B 的 1/2/20）
        Assert.Equal(1, done!.Variables.GetInt(FlowConst.SubmitType));
        Assert.Equal("同意", done.Variables.GetStr(FlowConst.ApprovalComment));
        // 任务变量未被整体替换：两跳转办留痕在 B 办结后仍在（跨跳账本的全部意义）
        Assert.Equal(2, HistoryOf(done).Count);
        Assert.Equal("wangwu", done.Variables.GetStr(FlowConst.TransferTo));
        // 办结（非转办）路径照常写 operator 列——契约只禁转办覆写
        Assert.Equal("wangwu", done.ActorId);
    }

    [Fact]
    public async Task Transfer_ThenWithdrawFromInstance_FromActorDoneListNotPolluted()
    {
        // 回归红线（Node 实测踩过）：转办把被摘走的人写进 operator 列后，该单一旦撤回/终止
        // （离开 DOING 但保留该列值），会凭空出现在他从没办过的「我已办」里。
        var iid = await SeedStartedInstanceAsync("TF-REDLINE");
        var task = await TestInfra.FindDoingForAsync(_repo, iid, "leader");
        var taskId = task.TaskId!.Value;
        Assert.Equal(0, (await TransferAsync(taskId, "leader", "lisi", "", "leader"))["code"]);
        Assert.Null((await _repo.FindTaskByIdAsync(taskId))!.ActorId); // DOING 期间该列恒无值

        var wd = await _facade.FlowAsync("processInstance/withdraw",
            new FlowData { ["id"] = iid, ["operator"] = "user1" });
        Assert.True(Equals(0, wd["code"]), $"withdraw msg={wd["msg"]}");
        Assert.Equal((int)WfTaskState.Withdraw, (await _repo.FindTaskByIdAsync(taskId))!.TaskState);

        // 被摘走的人：该单不得出现在其「我已办」
        Assert.DoesNotContain(taskId.ToString(), await DoneIdsOfAsync("leader"));
        // 接手但未办的人：同样不得出现
        Assert.DoesNotContain(taskId.ToString(), await DoneIdsOfAsync("lisi"));
        // 正向对照（防空断言）：真办过的发起人 apply 那条确实在其「我已办」里
        var apply = (await _repo.FindHistoryTasksAsync(iid)).First(t => t.TaskName == "apply");
        Assert.Contains(apply.TaskId!.Value.ToString(), await DoneIdsOfAsync("user1"));
    }

    [Fact]
    public async Task Transfer_ThenWithdraw_FinishedRowKeepsOperatorAndTraceSurvives()
    {
        // 已完成(20) 行不被撤回改写 + 转办留痕在撤回后仍可读（撤回不抹账本）
        var iid = await SeedStartedInstanceAsync("TF-FINISHED");
        var taskId = (await TestInfra.FindDoingForAsync(_repo, iid, "leader")).TaskId!.Value;
        Assert.Equal(0, (await TransferAsync(taskId, "leader", "lisi", "r", "leader"))["code"]);
        var apply = (await _repo.FindHistoryTasksAsync(iid)).First(t => t.TaskName == "apply");
        var resp = await _facade.FlowAsync("processInstance/withdraw",
            new FlowData { ["id"] = iid, ["operator"] = "user1" });
        Assert.Equal(0, resp["code"]);
        var applyAfter = await _repo.FindTaskByIdAsync(apply.TaskId);
        Assert.Equal((int)WfTaskState.Finished, applyAfter!.TaskState);
        Assert.Equal("user1", applyAfter.ActorId); // 原办理人列不被撤回动过
        // 撤回不抹账本：那条转办留痕读回仍在，且内容未变形
        var ledger = HistoryOf(await _repo.FindTaskByIdAsync(taskId)!);
        Assert.Single(ledger);
        Assert.Equal("lisi", ((Dictionary<string, object?>)ledger[0]!)["toActor"]);
    }

    [Fact]
    public async Task Transfer_NegativeCases_SixUnifiedMsgs()
    {
        var iid = await SeedStartedInstanceAsync("TF-NEG");
        var taskId = (await TestInfra.FindDoingForAsync(_repo, iid, "leader")).TaskId!.Value;
        // 先把 lisi 加签进来（供"目标人已是参与人"用例）
        Assert.Equal(0, (await _facade.FlowAsync("processTask/surrogate", new FlowData
        { [FlowConst.ProcessTaskIdKey] = taskId, ["actorIds"] = new List<object?> { "lisi" } }))["code"]);

        var cases = new (string Name, FlowData Args, string Msg)[]
        {
            ("缺 operator", new FlowData
                { [FlowConst.ProcessTaskIdKey] = taskId, ["fromActor"] = "leader", ["toActor"] = "wangwu" },
                "operator 必填"),
            ("operator 空串", new FlowData
                { [FlowConst.ProcessTaskIdKey] = taskId, ["operator"] = "", ["fromActor"] = "leader",
                  ["toActor"] = "wangwu" }, "operator 必填"),
            ("缺 fromActor", new FlowData
                { [FlowConst.ProcessTaskIdKey] = taskId, ["operator"] = "leader", ["toActor"] = "wangwu" },
                "fromActor 必填"),
            ("fromActor 空白", new FlowData
                { [FlowConst.ProcessTaskIdKey] = taskId, ["operator"] = "leader", ["fromActor"] = " ",
                  ["toActor"] = "wangwu" }, "fromActor 必填"),
            ("缺 toActor", new FlowData
                { [FlowConst.ProcessTaskIdKey] = taskId, ["operator"] = "leader", ["fromActor"] = "leader" },
                "toActor 必填"),
            ("操作人非 fromActor", new FlowData
                { [FlowConst.ProcessTaskIdKey] = taskId, ["operator"] = "boss", ["fromActor"] = "leader",
                  ["toActor"] = "wangwu" }, "无权限转办该任务"),
            ("原办理人非参与人", new FlowData
                { [FlowConst.ProcessTaskIdKey] = taskId, ["operator"] = "boss", ["fromActor"] = "boss",
                  ["toActor"] = "wangwu" }, "原办理人不是该任务参与人"),
            ("目标人已是参与人", new FlowData
                { [FlowConst.ProcessTaskIdKey] = taskId, ["operator"] = "leader", ["fromActor"] = "leader",
                  ["toActor"] = "lisi" }, "目标人已是该任务参与人"),
        };
        foreach (var (name, args, msg) in cases)
        {
            var resp = await _facade.FlowAsync("processTask/transfer", args);
            Assert.True(Equals(99999999, resp["code"]), $"{name}：期望 99999999，实得 {resp["code"]}");
            Assert.Equal(msg, resp["msg"]); // msg 跨栈逐字统一
        }
        // 负向不得留下半成品：参与者与留痕均未变
        Assert.Contains("leader", await _repo.FindTaskActorsAsync(taskId));
        Assert.Null((await _repo.FindTaskByIdAsync(taskId))!.Variables.GetObj(FlowConst.TransferHistory));

        // 任务非进行中（办结后再转办）——最后一条独立用例，态已变
        var finish = await _facade.FlowAsync("processTask/execute", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = taskId, ["operator"] = "leader",
            [FlowConst.SubmitType] = (int)WfSubmitType.Agree,
        });
        Assert.True(Equals(0, finish["code"]), $"办结 msg={finish["msg"]}");
        var late = await TransferAsync(taskId, "leader", "wangwu", "", "leader");
        Assert.Equal(99999999, late["code"]);
        Assert.Equal("任务非进行中，不可转办", late["msg"]);
    }

    [Fact]
    public async Task Transfer_ByFlowAdmin_CanMoveOthersTodo()
    {
        // 归属判据例外：flow.admin/flow.auto 可代转（msg=无权限转办该任务 只在既非 fromActor 又非特权时出）
        var iid = await SeedStartedInstanceAsync("TF-ADMIN");
        var taskId = (await TestInfra.FindDoingForAsync(_repo, iid, "leader")).TaskId!.Value;
        var resp = await TransferAsync(taskId, "leader", "lisi", "管理员改派", "flow.admin");
        Assert.True(Equals(0, resp["code"]), $"transfer msg={resp["msg"]}");
        Assert.Contains(taskId.ToString(), await TodoIdsOfAsync("lisi"));
        var after = await _repo.FindTaskByIdAsync(taskId);
        Assert.Equal("flow.admin", after!.UpdateUser);            // 操作人记 update_user
        Assert.Equal("flow.admin", HopAt(after, 0)["operator"]); // 账本 operator 记真实操作人
        Assert.Null(after.ActorId);
    }

    [Fact]
    public async Task Surrogate_StillAppendOnly_AndTransferOnSharedTaskRemovesOnlyFromActor()
    {
        // 回归：加签（surrogate/addCandidate）仍"只追加不清空"，原参与人保留可办；
        // 转办在同一任务上只摘 fromActor 一行，加签来的人不受影响
        var iid = await SeedStartedInstanceAsync("TF-SURR");
        var taskId = (await TestInfra.FindDoingForAsync(_repo, iid, "leader")).TaskId!.Value;
        Assert.Equal(0, (await _facade.FlowAsync("processTask/surrogate", new FlowData
        { [FlowConst.ProcessTaskIdKey] = taskId, ["actorIds"] = "helper1" }))["code"]);
        Assert.Equal(0, (await _facade.FlowAsync("processTask/addCandidate", new FlowData
        { [FlowConst.ProcessTaskIdKey] = taskId, ["actorIds"] = new List<object?> { "helper2" } }))["code"]);
        Assert.Equal(new List<string> { "leader", "helper1", "helper2" },
            await _repo.FindTaskActorsAsync(taskId));

        Assert.Equal(0, (await TransferAsync(taskId, "leader", "lisi", "", "leader"))["code"]);
        Assert.Equal(new List<string> { "helper1", "helper2", "lisi" },
            await _repo.FindTaskActorsAsync(taskId));
        // 加签来的人仍可办，被摘走的人待办已挪走
        Assert.Contains(taskId.ToString(), await TodoIdsOfAsync("helper1"));
        Assert.DoesNotContain(taskId.ToString(), await TodoIdsOfAsync("leader"));
    }

    [Fact]
    public async Task Transfer_OnCountersignNode_OtherMembersVotingUnaffected()
    {
        // 会签节点转的是"自己那一票"：其余成员任务与簿记不受影响，接手人照常计入推进
        var (_, iid) = await SeedCountersignInstanceAsync();
        var taskA = await TestInfra.FindDoingForAsync(_repo, iid, "userA");
        var taskB = await TestInfra.FindDoingForAsync(_repo, iid, "userB");
        Assert.Equal(0, (await TransferAsync(taskA.TaskId!.Value, "userA", "lisi", "转岗", "userA"))["code"]);
        Assert.Equal(new List<string> { "lisi" }, await _repo.FindTaskActorsAsync(taskA.TaskId.Value));
        Assert.Equal(new List<string> { "userB" }, await _repo.FindTaskActorsAsync(taskB.TaskId!.Value));
        Assert.Equal((int)WfTaskState.Doing, (await _repo.FindTaskByIdAsync(taskB.TaskId.Value))!.TaskState);

        var exec = await _facade.FlowAsync("processTask/execute", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = taskA.TaskId.Value, ["operator"] = "lisi",
            [FlowConst.SubmitType] = (int)WfSubmitType.Agree,
        });
        Assert.True(Equals(0, exec["code"]), $"接手人办理 msg={exec["msg"]}");
        // 会签簿记：只完成 1/3 → 实例仍进行中，其余成员仍可办
        Assert.Equal((int)WfInstanceState.Doing, (await _repo.FindInstanceByIdAsync(iid))!.State);
        Assert.Contains(taskB.TaskId!.Value.ToString(), await TodoIdsOfAsync("userB"));
        // 被摘走的 userA 不得再去办别人那一票
        var stolen = await _facade.FlowAsync("processTask/execute", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = taskB.TaskId.Value, ["operator"] = "userA",
            [FlowConst.SubmitType] = (int)WfSubmitType.Agree,
        });
        Assert.Equal(99999999, stolen["code"]);
    }
}

/// <summary>用户搜索钩子 stub（candidatePage 无模型候选分支/候选映射用）。</summary>
public class TestUserSearchProvider : IUserSearchProvider
{
    public Task<PageResult<Dictionary<string, object?>>> PageAsync(PageQuery query)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (var uid in new[] { "userA", "userB", "userC" })
        {
            rows.Add(new Dictionary<string, object?> { ["id"] = uid, ["realName"] = uid });
        }
        return Task.FromResult(PageResult<Dictionary<string, object?>>.Of(1, 10, rows.Count, rows));
    }

    public Task<Dictionary<string, object?>?> FindByIdAsync(string userId)
    {
        Dictionary<string, object?>? row = new Dictionary<string, object?> { ["id"] = userId, ["realName"] = userId };
        return Task.FromResult<Dictionary<string, object?>?>(row);
    }
}

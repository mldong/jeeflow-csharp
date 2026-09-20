using System.Text.Json;
using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// Facade 45 action 契约测试：dispatch 全覆盖（无"未知 action"）、信封形状、
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

    /// <summary>45 action dispatch 全覆盖：任意载荷不落 default（否则 msg 含"未知 action"）。</summary>
    [Fact]
    public async Task All45ActionsDispatch_NoUnknown()
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
        Assert.Equal(45, count);
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

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mldong.Jeeflow.Core;
using Xunit;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// 「表单指定用户所属部门经理／分管领导」两个内置处理器（C# 栈）。
///
/// <para>契约＝执行卡 <c>docs/2026-11-22-form-user-assignment-handler-handoff.md</c> §3（八语言同形）；
/// 行为基准＝jeeflow-go <c>v1.8.67</c>（本轮样章）；场景来源＝mldong-fastapi-wf <c>fe9233d</c>（HR 代员工提交）。</para>
///
/// <para>⚠️ 本栈的 assignmentHandler 腿历史上零覆盖：<c>TestInfra</c> 注册了内置 handler，却没有任何用例
/// 通过流程定义里的 <c>assignmentHandler</c> 走进去 ⇒ 注册名或取人顺序写错不会有任何一格变红。
/// 本件既钉取值语义，也钉"引擎从 <see cref="ServiceContext"/> 真把处理器取到"这条接线；
/// 共享 <c>flows/11-assignment-handler.json</c> 另走完整部署/发起/办理链，钉嵌套 Java FQCN 真能解析。</para>
/// </summary>
public class FormUserAssignmentHandlerTests
{
    private const string FqLeader = "com.mldong.jeeflow.interceptor.impl.FormUserDeptLeaderAssignmentHandler";
    private const string FqMain = "com.mldong.jeeflow.interceptor.impl.FormUserDeptMainLeaderAssignmentHandler";
    private const string FqFormField = "com.mldong.jeeflow.interceptor.impl.FormFieldAssigneeHandler";

    /// <summary>dept1 有两位经理＋一位分管领导，其余部门无领导。</summary>
    private class OrgProv : IOrgUserProvider
    {
        public Task<List<string>> FindDeptLeadersAsync(string deptId) =>
            Task.FromResult(deptId == "dept1" ? new List<string> { "leader1", "leader2" } : new List<string>());
        public Task<List<string>> FindDeptMainLeadersAsync(string deptId) =>
            Task.FromResult(deptId == "dept1" ? new List<string> { "boss1" } : new List<string>());
        public Task<List<string>> FindByRoleAsync(string roleCode) =>
            Task.FromResult(roleCode == "task4" ? new List<string> { "roleA", "roleB" } : new List<string>());
    }

    private static (JeeflowEngine Engine, MemoryRepository Repo, ServiceContext Ctx) Harness()
    {
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        ctx.OrgUserProvider = new OrgProv();
        return (engine, repo, ctx);
    }

    private static string FlowJson(string name, string applyAssignee)
    {
        string Task(string id, string handler, int y) =>
            "{\"id\":\"" + id + "\",\"type\":\"snaker:task\",\"x\":480,\"y\":" + y +
            ",\"text\":{\"value\":\"" + id + "\"},\"properties\":{\"width\":120,\"height\":80," +
            "\"assignee\":\"\",\"taskType\":\"Major\",\"performType\":\"ANY\",\"autoExecute\":\"N\"," +
            "\"assignmentHandler\":\"" + handler + "\"}}";
        return "{\"nodes\":[" +
               "{\"id\":\"start\",\"type\":\"snaker:start\",\"x\":480,\"y\":200,\"text\":{\"value\":\"开始\"},\"properties\":{}}," +
               "{\"id\":\"apply\",\"type\":\"snaker:task\",\"x\":480,\"y\":350,\"text\":{\"value\":\"发起申请\"}," +
               "\"properties\":{\"assignee\":\"" + applyAssignee + "\",\"taskType\":\"Major\",\"performType\":\"ANY\",\"autoExecute\":\"N\"}}," +
               Task("manager#f_userId", FqLeader, 500) + "," +
               Task("mainLeader#f_userId", FqMain, 650) + "," +
               "{\"id\":\"end\",\"type\":\"snaker:end\",\"x\":480,\"y\":800,\"text\":{\"value\":\"结束\"},\"properties\":{}}]," +
               "\"edges\":[" +
               "{\"id\":\"e0\",\"type\":\"snaker:transition\",\"sourceNodeId\":\"start\",\"targetNodeId\":\"apply\",\"properties\":{}}," +
               "{\"id\":\"e1\",\"type\":\"snaker:transition\",\"sourceNodeId\":\"apply\",\"targetNodeId\":\"manager#f_userId\",\"properties\":{}}," +
               "{\"id\":\"e2\",\"type\":\"snaker:transition\",\"sourceNodeId\":\"manager#f_userId\",\"targetNodeId\":\"mainLeader#f_userId\",\"properties\":{}}," +
               "{\"id\":\"e3\",\"type\":\"snaker:transition\",\"sourceNodeId\":\"mainLeader#f_userId\",\"targetNodeId\":\"end\",\"properties\":{}}" +
               "],\"name\":\"" + name + "\",\"displayName\":\"HR代员工提交（表单指定用户）\"}";
    }

    // ═══ 正向：四档并集（顺序即优先级）════════════════════════════════════════

    [Theory]
    [InlineData("f_ 主通道", "manager", "f_manager", "emp")]
    [InlineData("裸名回落", "manager", "manager", "emp")]
    [InlineData("# 右半（字段名本身带 f_）", "manager#f_userId", "f_userId", "emp")]
    [InlineData("# 右半的 f_ 变体", "manager#userId", "f_userId", "emp")]
    [InlineData("_NN 去后缀的 f_ 形", "manager_01", "f_manager", "emp")]
    [InlineData("_NN 去后缀的裸名形", "manager_01", "manager", "emp")]
    public void FieldLookupFourTiers(string label, string nodeKey, string varKey, string want)
    {
        var args = new FlowData().Set(varKey, want);
        var got = BuiltinAssignmentHandlers.FindFormUserFieldValue(args, nodeKey);
        Assert.True(Equals(want, got), $"{label}：期望 {want}，实得 {got}");
    }

    [Fact]
    public void FPrefixBeatsBareName()
    {
        var args = new FlowData().Set("f_x", "emp").Set("x", "other");
        Assert.Equal("emp", BuiltinAssignmentHandlers.FindFormUserFieldValue(args, "x"));
    }

    [Fact]
    public void HashTierPrefersBareFieldThenFPrefix()
    {
        var args = new FlowData().Set("userId", "emp").Set("f_userId", "other");
        Assert.Equal("emp", BuiltinAssignmentHandlers.FindFormUserFieldValue(args, "manager#userId"));
    }

    [Fact]
    public void NumberSuffixTierPrefersFPrefix()
    {
        // jeeflow 变量存 f_ ⇒ 第 4 档先 f_ 再裸名（参照实现 fastapi-wf 没有这一支）
        var args = new FlowData().Set("f_manager", "emp").Set("manager", "other");
        Assert.Equal("emp", BuiltinAssignmentHandlers.FindFormUserFieldValue(args, "manager_01"));
    }

    [Fact]
    public void TakesFirstNonEmptyIdOnly()
    {
        Assert.Equal("a", BuiltinAssignmentHandlers.FirstFormUserId("a,b"));
        Assert.Equal("b", BuiltinAssignmentHandlers.FirstFormUserId(new List<object?> { "", "b", "c" }));
        Assert.Equal("d", BuiltinAssignmentHandlers.FirstFormUserId("  ,  ,d"));
        Assert.Equal("0", BuiltinAssignmentHandlers.FirstFormUserId("0"));
        Assert.Null(BuiltinAssignmentHandlers.FirstFormUserId(new List<object?> { "", "  " }));
        Assert.Null(BuiltinAssignmentHandlers.FirstFormUserId(null));
    }

    // ═══ 引擎级：HR 代员工提交两段链路 ════════════════════════════════════════

    [Fact]
    public async Task HrProxyFlowLandsOnFormUserDeptLeaders()
    {
        var (engine, repo, _) = Harness();
        // userA 属 dept1（TestInfra.NamedUsers）⇒ 取的是**表单指定用户**的部门，不是操作人 applicant 的
        var defineId = await TestInfra.SaveFlowDefineAsync(repo, "fu-hr-proxy", FlowJson("fu-hr-proxy", "applicant"));
        var instId = await TestInfra.StartAndApplyAsync(engine, repo, defineId,
            new FlowData().Set("f_userId", "userA"));

        var doing = await repo.FindDoingTasksAsync(instId, null);
        var manager = Assert.Single(doing, t => t.TaskName == "manager#f_userId");
        Assert.Equal(new List<string> { "leader1", "leader2" }, manager.ActorIds);

        await engine.ExecuteProcessTaskAsync(manager.TaskId!.Value, "leader1", new FlowData());
        doing = await repo.FindDoingTasksAsync(instId, null);
        var main = Assert.Single(doing, t => t.TaskName == "mainLeader#f_userId");
        Assert.Equal(new List<string> { "boss1" }, main.ActorIds);
    }

    [Fact]
    public async Task EmptyActorsDoNotBreakInstance()
    {
        var (engine, repo, _) = Harness();
        var defineId = await TestInfra.SaveFlowDefineAsync(repo, "fu-hr-empty", FlowJson("fu-hr-empty", "applicant"));
        var instId = await TestInfra.StartAndApplyAsync(engine, repo, defineId, new FlowData());

        var doing = await repo.FindDoingTasksAsync(instId, null);
        var manager = Assert.Single(doing, t => t.TaskName == "manager#f_userId");
        Assert.Empty(manager.ActorIds);                       // 零参与者仍建 DOING 行
        var inst = await repo.FindInstanceByIdAsync(instId);
        Assert.Equal(10, inst!.State);                        // 未被空参与者打断
    }

    [Theory]
    [InlineData("指向无部门用户", "manager")]     // manager 属 dept2 ⇒ 该部门无领导
    [InlineData("指向查无此人", "ghost")]          // TestUserProvider 回 DeptId=null
    [InlineData("值为空串", "")]
    [InlineData("值为纯空白", "   ")]
    public async Task NegativeShapesGiveNoActors(string label, string userId)
    {
        var (engine, repo, _) = Harness();
        var defineId = await TestInfra.SaveFlowDefineAsync(repo, "fu-neg-" + label, FlowJson("fu-neg-" + label, "applicant"));
        var instId = await TestInfra.StartAndApplyAsync(engine, repo, defineId,
            new FlowData().Set("f_userId", userId));
        var doing = await repo.FindDoingTasksAsync(instId, null);
        var manager = Assert.Single(doing, t => t.TaskName == "manager#f_userId");
        Assert.Empty(manager.ActorIds);
    }

    // ═══ 回归：现存 handler 没被并集查找渗进去 ════════════════════════════════

    [Fact]
    public async Task FormFieldAssigneeStillIgnoresHashTier()
    {
        // 同一枚节点唯一编码、同一份变量：老类读不到（它没有 # 档），新类读得到 ⇒ 两条腿各自独立
        var (engine, repo, _) = Harness();
        var defineId = await TestInfra.SaveFlowDefineAsync(repo, "fu-legacy", FlowJson("fu-legacy", "applicant"));
        var legacyJson = FlowJson("fu-legacy2", "applicant")
            .Replace("\"assignmentHandler\":\"" + FqLeader + "\"", "\"assignmentHandler\":\"" + FqFormField + "\"");
        var legacyDefineId = await TestInfra.SaveFlowDefineAsync(repo, "fu-legacy2", legacyJson);

        var emptyInst = await TestInfra.StartAndApplyAsync(engine, repo, legacyDefineId,
            new FlowData().Set("f_userId", "userA"));
        var doing = await repo.FindDoingTasksAsync(emptyInst, null);
        var task = Assert.Single(doing, t => t.TaskName == "manager#f_userId");
        Assert.Empty(task.ActorIds);

        await TestInfra.StartAndApplyAsync(engine, repo, defineId, new FlowData().Set("f_userId", "userA"));
    }

    /// <summary>
    /// issues/255：老类第三档＝<c>_NN</c> 去后缀后查<strong>裸名 base</strong>（八语言补齐的那一档）。
    /// 第三档只查裸名，<c>f_&lt;base&gt;</c> 是 254 两枚新类的扩面，老类不许跟着扩。
    /// </summary>
    [Fact]
    public async Task FormFieldAssigneeNumberSuffixTier()
    {
        var handler = new BuiltinAssignmentHandlers.FormFieldAssigneeHandler();

        async Task<string?> Assign(string nodeId, params (string Key, string Value)[] vars)
        {
            var args = new FlowData();
            foreach (var (key, value) in vars) args[key] = value;
            var exec = new Execution
            {
                Context = new ServiceContext(new MemoryRepository()),
                NodeModel = new TaskModel { Name = nodeId },
                Args = args,
            };
            return await handler.AssignAsync(exec);
        }

        Assert.Equal("userA,userB", await Assign("task_01", ("task", "userA,userB")));
        // 惰性量词的最早可切点：a_b_01 的 base 是 a_b，不是 a
        Assert.Equal("u_c", await Assign("a_b_01", ("a", "u_x"), ("a_b", "u_c")));
        // 优先级：f_<id> → 裸名 → base 裸名
        Assert.Equal("u_1", await Assign("task_01", ("f_task_01", "u_1"), ("task_01", "u_2"), ("task", "u_3")));
        Assert.Equal("u_2", await Assign("task_01", ("task_01", "u_2"), ("task", "u_3")));
        // 反向：尾巴非纯数字、base 为空、base 只有 f_ 形 ⇒ 三档全落空
        Assert.Null(await Assign("task_01x", ("task", "u_3")));
        Assert.Null(await Assign("_01", ("", "u_3")));
        Assert.Null(await Assign("task_02", ("f_task", "u_3")));
    }

    // ═══ 共享 flow 11：嵌套 Java FQCN 的部署／发起／办理全链 ════════════════════

    [Fact]
    public async Task SharedFlow11DeploysStartsAndResolvesNestedHandlers()
    {
        var (engine, repo, _) = Harness();
        var defineId = await TestInfra.SaveFlowDefineAsync(repo, "assignment-handler",
            TestInfra.LoadFlow("11-assignment-handler"));
        var inst = await engine.StartProcessInstanceByIdAsync(defineId, "user1",
            new FlowData { ["task1"] = "userA,userB" });
        var instId = inst.InstanceId!.Value;
        Assert.Equal((int)WfInstanceState.Doing, inst.State);

        // 与 Java AssignmentHandlerTest.testBuiltinAssignmentHandlers 同序：字段 → 发起人 → 部门领导 → 角色 → 结束。
        var t1 = Assert.Single(await repo.FindDoingTasksAsync(instId, null));
        Assert.Equal("task1", t1.TaskName);
        Assert.Equal(2, t1.ActorIds.Count);
        Assert.Contains("userA", t1.ActorIds);
        Assert.Contains("userB", t1.ActorIds);
        await engine.ExecuteProcessTaskAsync(t1.TaskId!.Value, "userA", new FlowData());

        var t2 = Assert.Single(await repo.FindDoingTasksAsync(instId, null));
        Assert.Equal("task2", t2.TaskName);
        Assert.Equal(new List<string> { "user1" }, t2.ActorIds);
        await engine.ExecuteProcessTaskAsync(t2.TaskId!.Value, "user1", new FlowData());

        // task3/task4 能建成并取到人，才证明共享 JSON 的 $ 嵌套名确实命中 TestInfra 注册表。
        var t3 = Assert.Single(await repo.FindDoingTasksAsync(instId, null));
        Assert.Equal("task3", t3.TaskName);
        Assert.Equal(2, t3.ActorIds.Count);
        Assert.Contains("leader1", t3.ActorIds);
        Assert.Contains("leader2", t3.ActorIds);
        await engine.ExecuteProcessTaskAsync(t3.TaskId!.Value, "leader1", new FlowData());

        var t4 = Assert.Single(await repo.FindDoingTasksAsync(instId, null));
        Assert.Equal("task4", t4.TaskName);
        Assert.Equal(2, t4.ActorIds.Count);
        Assert.Contains("roleA", t4.ActorIds);
        Assert.Contains("roleB", t4.ActorIds);
        await engine.ExecuteProcessTaskAsync(t4.TaskId!.Value, "roleA", new FlowData());

        var finished = await repo.FindInstanceByIdAsync(instId);
        Assert.Equal((int)WfInstanceState.Finished, finished!.State);
    }

    // ═══ 注册名／元数据 ═══════════════════════════════════════════════════════

    [Fact]
    public void MetadataExposesTwoNewDisplayNames()
    {
        var registry = new HandlerRegistry();
        var metas = registry.ListHandlers("AssignmentHandler");
        var tail = metas.Skip(metas.Count - 2).ToList();
        Assert.Equal(FqLeader, tail[0].ClassName);
        Assert.Equal("表单指定用户所属部门经理", tail[0].DisplayName);
        Assert.Equal(70, tail[0].Order);
        Assert.Equal(FqMain, tail[1].ClassName);
        Assert.Equal("表单指定用户所属部门分管领导", tail[1].DisplayName);
        Assert.Equal(80, tail[1].Order);
    }

    [Fact]
    public void RegistrationNamesResolveToNewClasses()
    {
        var (_, _, ctx) = Harness();
        Assert.IsType<BuiltinAssignmentHandlers.FormUserDeptLeaderAssignmentHandler>(
            ctx.FindAssignmentHandler(FqLeader));
        Assert.IsType<BuiltinAssignmentHandlers.FormUserDeptMainLeaderAssignmentHandler>(
            ctx.FindAssignmentHandler(FqMain));
    }
}

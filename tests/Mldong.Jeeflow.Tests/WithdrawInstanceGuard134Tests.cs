using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/134 案 A · 撤回的<b>实例状态守卫</b>（owner 2026-09-28 拍板）。
///
/// <para>缺陷：issues/113 只落了<b>任务行</b>层面的保护（已完成 20 / 已终止 40 的行不被撤回改写），
/// <b>实例</b>层面谁都没判状态——对已办结(20)/已终止(40) 的实例调撤回，会把实例静默改写成 30（已撤回），
/// 已办列表与按状态聚合的统计就此凭空改历史，而调用方看不到任何报错。</para>
///
/// <para>判据（八栈逐字统一）：聚合根 <see cref="ProcessInstance.Withdraw"/> 时实例
/// <c>state != 10(进行中)</c> ⇒ 抛内部码 <b>20010009</b>（<see cref="WfErr.WithdrawInstanceNotDoing"/>），
/// 文案固定 <see cref="ExpectedMsg"/>，<b>不改写任何行、不落库</b>；守卫排在任务行循环之前。
/// 门面沿用 issues/121 口径把内部码吞掉 ⇒ 出口 <c>code=99999999</c> ＋ msg 就是那句原文
/// （<b>文案里不带码值</b>）。</para>
///
/// <para>跨栈门禁格 L2-28 只钉得住 20 档与正向 10 档（壳侧造不出 state=40 的行，issues/134 §5.2 已注明），
/// 40 / 30 两档在本栈栈内单测钉住。</para>
///
/// <para>本栈专属：域层是八栈里唯一带 <see cref="IClock"/> 注入的（issues/120），守卫不取时、
/// 正向路径的取时仍一律 <c>clock ?? SystemClock.Instance</c>——①那格注入 FixedClock 并断精确值，
/// 写成裸 <c>DateTime.Now</c> 就会因量级差报红。</para>
/// </summary>
public class WithdrawInstanceGuard134Tests
{
    /// <summary>出口文案逐字固定（八栈一致）；门禁按逐字断言，不许用"包含 撤回"这种宽松判据。</summary>
    private const string ExpectedMsg = "流程实例非进行中，无法撤回";

    /// <summary>内部错误码：20010001–20010008 已占，本案取 20010009。</summary>
    private const int ExpectedInternalCode = 20010009;

    /// <summary>负向档的撤回人：与夹具里的原始 update_user 不同，才照得出"一行都不改"。</summary>
    private const string LateWithdrawer = "lateWithdrawer";

    // ═══ 聚合根级夹具（对齐 Java WithdrawInstanceStateGuardTest 的 doingInstance/finishedInstance）═══

    /// <summary>一个进行中(10) 的实例：一行进行中任务 task1，参与者 leader，发起人 zhangsan。</summary>
    private static ProcessInstance DoingInstance()
    {
        var define = new ProcessDefine { Id = 9527, Name = "134-guard" };
        var inst = ProcessInstance.Create(define, "zhangsan", new FlowData());
        inst.InstanceId = 9001;
        inst.CreateTask(new TaskModel { Name = "task1", DisplayName = "审批" },
            "审批", new List<string> { "leader" }, "zhangsan", 0, false);
        return inst;
    }

    /// <summary>把实例<b>自然</b>办到 state=20（行真办结 + 聚合根 Finish），不用 setState 造假形状。</summary>
    private static ProcessInstance FinishedInstance()
    {
        var inst = DoingInstance();
        inst.Tasks[0].Finish("leader", null);
        inst.Finish();
        Assert.Equal((int)WfInstanceState.Finished, inst.State); // 夹具前提：实例已办结
        return inst;
    }

    /// <summary>
    /// 断负向抛出：异常类型 ＋ 内部码 20010009 ＋ 文案逐字相等（不含码值）＋
    /// 实例状态/撤回人/取时/任务行 一行未改。
    /// </summary>
    private static void AssertRejectedWithoutTouchingRows(ProcessInstance inst, int? originalState)
    {
        var updateUserBefore = inst.UpdateUser;
        var updateTimeBefore = inst.UpdateTime;
        var rowStatesBefore = inst.Tasks.Select(t => t.TaskState).ToList();

        var ex = Assert.Throws<JeeflowException>(() => inst.Withdraw(LateWithdrawer));
        Assert.Equal(ExpectedInternalCode, ex.Code);
        Assert.Equal(ExpectedMsg, ex.Message);
        Assert.DoesNotContain("20010009", ex.Message);

        Assert.Equal(originalState, inst.State);
        Assert.NotEqual((int)WfInstanceState.Withdraw, inst.State);
        Assert.Equal(updateUserBefore, inst.UpdateUser);
        Assert.Equal(updateTimeBefore, inst.UpdateTime);
        for (var i = 0; i < inst.Tasks.Count; i++)
            Assert.Equal(rowStatesBefore[i], inst.Tasks[i].TaskState);
    }

    // ── T0 聚合根级四格 ──

    /// <summary>负向①：已完成(20) 的实例调撤回 ⇒ 20010009 ＋ 固定文案，实例仍 20、行仍 20。</summary>
    [Fact]
    public void WithdrawOnFinishedInstanceIsRejectedAndKeepsState()
    {
        var inst = FinishedInstance();
        Assert.Equal((int)WfTaskState.Finished, inst.Tasks[0].TaskState);
        AssertRejectedWithoutTouchingRows(inst, (int)WfInstanceState.Finished);
    }

    /// <summary>负向②：强行终止(40) 的实例调撤回 ⇒ 同样拒绝，实例仍 40、行仍 40。</summary>
    [Fact]
    public void WithdrawOnInterruptedInstanceIsRejectedAndKeepsState()
    {
        var inst = DoingInstance();
        inst.Interrupt("boss");
        Assert.Equal((int)WfInstanceState.Interrupt, inst.State); // 夹具前提：实例已终止
        Assert.Equal((int)WfTaskState.Interrupt, inst.Tasks[0].TaskState);
        AssertRejectedWithoutTouchingRows(inst, (int)WfInstanceState.Interrupt);
    }

    /// <summary>负向③：已撤回(30) 的实例二次撤回同样被拒——重复撤不得把 update_user 改成第二次操作人。</summary>
    [Fact]
    public void WithdrawOnAlreadyWithdrawnInstanceIsRejectedOnSecondCall()
    {
        var inst = DoingInstance();
        inst.Withdraw("zhangsan");   // 首次：进行中，照旧成功
        Assert.Equal((int)WfInstanceState.Withdraw, inst.State);
        Assert.Equal("zhangsan", inst.UpdateUser);

        var ex = Assert.Throws<JeeflowException>(() => inst.Withdraw(LateWithdrawer));
        Assert.Equal(ExpectedInternalCode, ex.Code);
        Assert.Equal(ExpectedMsg, ex.Message);
        Assert.Equal((int)WfInstanceState.Withdraw, inst.State);
        Assert.Equal("zhangsan", inst.UpdateUser); // 被拒的那次不得把撤回人改成第二次操作人
    }

    /// <summary>
    /// 正向对照（聚合根级）：进行中(10) 的实例撤回照旧成功，实例与进行中任务都落 30。
    /// 判据带上注入钟的<b>精确值</b>：守卫排在取时之前但不吃钟，正向路径的 <c>update_time</c>
    /// 仍须落在调用点注入的那把钟上（issues/120 路 1）——退化成裸 <c>DateTime.Now</c> 这一格即红。
    /// </summary>
    [Fact]
    public void WithdrawOnDoingInstanceStillSucceedsAndKeepsInjectedClock()
    {
        var clock = new FixedClock(new DateTime(2026, 8, 1, 9, 0, 0));
        var inst = DoingInstance();

        inst.Withdraw("zhangsan", clock);

        Assert.Equal((int)WfInstanceState.Withdraw, inst.State);
        Assert.Equal((int)WfTaskState.Withdraw, inst.Tasks[0].TaskState);
        Assert.Equal("zhangsan", inst.UpdateUser);
        Assert.Equal(new DateTime(2026, 8, 1, 9, 0, 0), inst.UpdateTime);
        Assert.Equal(new DateTime(2026, 8, 1, 9, 0, 0), inst.Tasks[0].UpdateTime);
    }
}

/// <summary>
/// issues/134 案 A · 门面级三格：出口只发明 99999999 ＋ 逐字文案（不带码值）、被拒不落库、
/// 正向 10 档照旧 code=0，且既有失败文案（operator 必填／无权限）仍排在状态守卫之前。
/// </summary>
public class WithdrawInstanceGuard134FacadeTests
{
    private const string ExpectedMsg = "流程实例非进行中，无法撤回";
    private const int ErrCode = 99999999;

    private readonly MemoryRepository _repo;
    private readonly JeeflowEngine _engine;
    private readonly JeeflowFacade _facade;

    public WithdrawInstanceGuard134FacadeTests()
    {
        (_engine, _repo) = TestInfra.NewEngine();
        _facade = new JeeflowFacade(TestInfra.NewContext(_repo));
    }

    /// <summary>起一条实例并完成申请节点（发起人 user1）⇒ 实例 10、task1(leader) 进行中。</summary>
    private async Task<long> StartAndApplyAsync(string businessNo)
    {
        var did = await TestInfra.SaveFlowDefineAsync(_repo, "134-" + businessNo,
            TestInfra.LoadFlow("01-simple"));
        var inst = await _engine.StartProcessInstanceByIdAsync(did, "user1",
            new FlowData { [FlowConst.BusinessNo] = businessNo });
        var apply = await TestInfra.FindDoingForAsync(_repo, inst.InstanceId!.Value, "user1");
        await _engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "user1", new FlowData());
        return inst.InstanceId!.Value;
    }

    /// <summary>再走门面把 leader 那一步办结 ⇒ 实例自然到 state=20（不用 setState 造假形状）。</summary>
    private async Task<long> StartAndFinishAsync(string businessNo)
    {
        var iid = await StartAndApplyAsync(businessNo);
        var leader = await TestInfra.FindDoingForAsync(_repo, iid, "leader");
        var resp = await _facade.FlowAsync("processTask/execute",
            new FlowData { [FlowConst.ProcessTaskIdKey] = leader.TaskId, ["operator"] = "leader" });
        Assert.True(Equals(0, resp["code"]), $"办结应成功: {resp["msg"]}");
        var inst = await _repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Finished, inst!.State); // 夹具前提：实例已办结
        return iid;
    }

    private async Task<Dictionary<string, object?>> WithdrawAsync(long iid, object? op) =>
        await _facade.FlowAsync("processInstance/withdraw",
            new FlowData { ["id"] = iid, ["operator"] = op! });

    /// <summary>
    /// 负向①（对应跨栈门禁格 L2-28）：真把实例办到 state=20 再调撤回
    /// ⇒ 出口 <c>code=99999999</c> ＋ msg 逐字 ＝ 固定文案（内部码不进 msg）；
    /// <b>再读一次实例</b> state 仍是 20、update_user 与任务行都没被改写（本案病灶）。
    /// 撤回人用 <c>flow.admin</c> 哨兵（归属判据③放行），确保报错来自状态守卫，
    /// 而不是被鉴权分支的「无权限撤回该流程实例」抢先命中。
    /// </summary>
    [Fact]
    public async Task FacadeWithdrawOnFinishedInstanceReturnsVerbatimMsgAndDoesNotPersist()
    {
        var iid = await StartAndFinishAsync("WD134-FIN");
        var before = await _repo.FindInstanceByIdAsync(iid);
        var updateUserBefore = before!.UpdateUser;
        var rowStatesBefore = (await _repo.FindHistoryTasksAsync(iid))
            .ToDictionary(t => t.TaskName!, t => t.TaskState);
        Assert.NotEmpty(rowStatesBefore);

        var resp = await WithdrawAsync(iid, FlowConst.AdminId);
        Assert.Equal(ErrCode, resp["code"]);
        Assert.Equal(ExpectedMsg, resp["msg"]);
        Assert.DoesNotContain("20010009", resp["msg"]!.ToString());

        // JSON 出口（跨栈门禁 L2-28 就是在这一层逐字比 msg）：文案原样、不拼码、不加前缀
        var json = await _facade.FlowJsonAsync("processInstance/withdraw",
            new FlowData { ["id"] = iid, ["operator"] = FlowConst.AdminId });
        Assert.Contains("{\"code\":99999999,\"msg\":\"流程实例非进行中，无法撤回\"}", json);

        var reread = await _repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Finished, reread!.State);
        Assert.Equal(updateUserBefore, reread.UpdateUser);
        foreach (var t in await _repo.FindHistoryTasksAsync(iid))
            Assert.Equal(rowStatesBefore[t.TaskName!], t.TaskState);
    }

    /// <summary>
    /// 负向②：state=40（强行终止）档。门面没有"终止实例"的 action，壳侧同样造不出这一档
    /// （issues/134 §5.2 因此把 L2-28 限定在 20 ＋ 正向 10），故本栈用聚合根自己的
    /// <see cref="ProcessInstance.Interrupt"/> 命令把存储里的实例自然推到 40，再走门面撤回。
    /// </summary>
    [Fact]
    public async Task FacadeWithdrawOnInterruptedInstanceIsRejectedAndKeepsRowStates()
    {
        var iid = await StartAndApplyAsync("WD134-INT");
        var inst = await _repo.FindInstanceByIdAsync(iid);
        inst!.Interrupt("boss");
        await _repo.UpdateInstanceAsync(inst);
        Assert.Equal((int)WfInstanceState.Interrupt,
            (await _repo.FindInstanceByIdAsync(iid))!.State); // 夹具前提：实例已终止

        var resp = await WithdrawAsync(iid, FlowConst.AdminId);
        Assert.Equal(ErrCode, resp["code"]);
        Assert.Equal(ExpectedMsg, resp["msg"]);

        var reread = await _repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Interrupt, reread!.State);
        Assert.Equal("boss", reread.UpdateUser); // 被拒的那次不落库，update_user 仍是终止人
        foreach (var t in await _repo.FindHistoryTasksAsync(iid))
            Assert.NotEqual((int)WfTaskState.Withdraw, t.TaskState); // 行不得被改成 30
    }

    /// <summary>
    /// 正向对照（门面级）：进行中(10) 的实例撤回照旧 code=0，实例落 30、进行中任务落 30，
    /// 已完成(20) 行仍不被改写（issues/113 的既有保护保持原样）。
    /// </summary>
    [Fact]
    public async Task FacadeWithdrawOnDoingInstanceStillSucceedsAndPersists30()
    {
        var iid = await StartAndApplyAsync("WD134-DOING");
        var doing = await _repo.FindDoingTasksAsync(iid, null);
        Assert.NotEmpty(doing);

        var resp = await WithdrawAsync(iid, "user1");
        Assert.True(Equals(0, resp["code"]), $"进行中实例撤回应照旧成功（守卫没写反）: {resp["msg"]}");

        var after = await _repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Withdraw, after!.State);
        Assert.Equal("user1", after.UpdateUser);
        Assert.Empty(await _repo.FindDoingTasksAsync(iid, null));
        var history = await _repo.FindHistoryTasksAsync(iid);
        foreach (var t in history.Where(t => t.TaskName != "apply"))
            Assert.Equal((int)WfTaskState.Withdraw, t.TaskState);
        Assert.Equal((int)WfTaskState.Finished,
            history.First(t => t.TaskName == "apply").TaskState); // 20 行不改写
    }

    /// <summary>
    /// 回归：既有失败文案不被本案污染——缺 operator／越权两条仍排在状态守卫之前，
    /// 且两条负向都不改状态，其后的正向撤回仍可用。
    /// </summary>
    [Fact]
    public async Task ExistingWithdrawFailureMessagesAndOrderUnchanged()
    {
        var iid = await StartAndApplyAsync("WD134-ORDER");

        var noOperator = await _facade.FlowAsync("processInstance/withdraw", new FlowData { ["id"] = iid });
        Assert.Equal(ErrCode, noOperator["code"]);
        Assert.Equal("operator 必填", noOperator["msg"]);

        var stranger = await WithdrawAsync(iid, "boss");
        Assert.Equal(ErrCode, stranger["code"]);
        Assert.Equal("无权限撤回该流程实例", stranger["msg"]);

        Assert.Equal((int)WfInstanceState.Doing, (await _repo.FindInstanceByIdAsync(iid))!.State);
        Assert.True(Equals(0, (await WithdrawAsync(iid, "user1"))["code"]));
    }
}

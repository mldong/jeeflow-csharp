using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// 任务参与者写侧归属值归一（issues/142 B 批 · spec 06-facade.md §2.11 · C# 栈内存一路）。
///
/// <para>§2.10（抄送侧，issues/141 G10 已落）的同一条尺子换到<b>任务侧</b>：
/// <c>wf_process_task_actor.actor_id</c> 是归属列（<see cref="PageQuery.OwnershipColumns"/> 之一），
/// 空串／纯空白／null 一旦落进去就是 issues/129 那族"空归属值读全库"的进水口。
/// owner 2026-09-30 拍：「八栈一起收：两形同判据＋写侧兜底＋trim＋哨兵」。</para>
///
/// <para>本栈普查实读（issues/142 §2 B 表 csharp 行）三条腿各有洞：
/// ① <c>ToStringList</c>（门面）集合腿<b>不 trim</b>，末尾 <c>Where(t =&gt; t.Length &gt; 0)</c>
/// 只兜住 null（转成的 ""），兜不住 <c>"  "</c>（trim 前长度 &gt; 0）⇒ 纯空白真落一行；
/// ② 发起腿 <c>ActionsMain.cs</c> 读 <c>f_nextNodeOperator</c> 用的是 <c>GetStr</c>＝<c>v.ToString()</c>
/// ⇒ 数组形态整条被串化成 <b>.NET 类型名</b>当成<b>一个</b>参与者落进归属列（普查时按代码形状推得，
/// 本文件的 <see cref="StartLegArrayNextNodeOperatorIsNotStringifiedToTypeName"/> 就是那条的实机取证）；
/// ③ 两仓 <c>AddTaskActorAsync</c> 判重不判空不 trim。
/// 判据一律复用 §2.10 已落地的单点（<see cref="PageQuery.NormalizeActors"/>，
/// cc 腿继续走同一枚的旧名 <see cref="PageQuery.NormalizeCcActors"/>），<b>不抄第二份</b>。</para>
///
/// <para>MySQL 一路（真库 wf_process_task_actor 读数）见 <see cref="MySqlActorWriteNormalization142Tests"/>：
/// 同一份数据，SQL 仓与内存仓必须给同一个答案（issues/117 场景 27 那把尺子）。</para>
/// </summary>
public class ActorWriteNormalization142Tests
{
    /// <summary>start → apply(applicant) → approval(leader) → end：发起腿与消费腿各有下一节点可指。</summary>
    private const string TwoTaskFlow = """
        {"name": "actor-142", "displayName": "归属值写侧归一", "type": "approval",
         "nodes": [
           {"id": "start", "type": "snaker:start", "text": {"value": "开始"}},
           {"id": "apply", "type": "snaker:task", "text": {"value": "发起申请"},
            "properties": {"form": "apply-form", "assignee": "applicant", "taskType": 0, "performType": 0}},
           {"id": "approval", "type": "snaker:task", "text": {"value": "审批"},
            "properties": {"form": "leave-form", "assignee": "leader", "taskType": 0, "performType": 0}},
           {"id": "end", "type": "snaker:end", "text": {"value": "结束"}}
         ],
         "edges": [
           {"id": "e1", "sourceNodeId": "start", "targetNodeId": "apply"},
           {"id": "e2", "sourceNodeId": "apply", "targetNodeId": "approval"},
           {"id": "e3", "sourceNodeId": "approval", "targetNodeId": "end"}
         ]}
        """;

    private readonly JeeflowEngine _engine;
    private readonly MemoryRepository _repo;
    private readonly JeeflowFacade _facade;
    private static int _defineSeq;

    public ActorWriteNormalization142Tests()
    {
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        _engine = engine;
        _repo = repo;
        _facade = new JeeflowFacade(ctx);
    }

    // ── 夹具辅助 ──

    private async Task<long> DefineAsync() =>
        await TestInfra.SaveFlowDefineAsync(_repo, "actor-142-" + _defineSeq++, TwoTaskFlow);

    /// <summary>发起并办结申请节点，停在 approval（参与人＝leader）。</summary>
    private async Task<long> StartToApprovalAsync()
    {
        var inst = await _engine.StartProcessInstanceByIdAsync(await DefineAsync(), "zhangsan", new FlowData());
        var apply = (await _repo.FindDoingTasksAsync(inst.InstanceId!.Value, null))[0];
        await _engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "zhangsan", new FlowData());
        return inst.InstanceId.Value;
    }

    private async Task<long> DoingTaskIdOfAsync(long instanceId, string actor)
    {
        foreach (var t in await _repo.FindDoingTasksAsync(instanceId, null))
            if (t.ActorIds.Contains(actor)) return t.TaskId!.Value;
        throw new InvalidOperationException($"实例 {instanceId} 没有 {actor} 的待办");
    }

    /// <summary>加签腿原始返回（负向档要看 code/msg，不能假定成功）。</summary>
    private Task<Dictionary<string, object?>> SurrogateAsync(object? processTaskId, object? actorIds) =>
        _facade.FlowAsync("processTask/surrogate", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = processTaskId,
            ["actorIds"] = actorIds,
        });

    private static List<object?> Col(params object?[] items) => items.ToList();

    /// <summary>某任务的参与者行原样读数（归属列本体，不经任何转换）。</summary>
    private async Task<List<string>> ActorsAsync(long taskId) => await _repo.FindTaskActorsAsync(taskId);

    /// <summary>内存 actor 表里某 taskId 的行数（判"空值有没有真落进去"）。</summary>
    private int ActorRows(long taskId) => _repo.TaskActors.Values.Count(a => a.ProcessTaskId == taskId);

    // ═══ 加签腿（addCandidate／surrogate 同体）：两形同判据 ═══

    /// <summary>
    /// 集合腿必须与逗号串腿同一条尺子：逐元素 trim、空串/纯空白/null 丢弃、同次调用折叠。
    /// 改前实测红样：<c>[" 16001 ","16001","","  ",null,"16002"]</c> ⇒ 落
    /// <c>leader, " 16001 ", "16001", "  ", "16002"</c>（trim 前判长兜不住纯空白，同一人两行）。
    /// </summary>
    [Fact]
    public async Task SurrogateArrayLegTrimsDropsBlankAndFoldsDuplicates()
    {
        var taskId = await DoingTaskIdOfAsync(await StartToApprovalAsync(), "leader");

        var resp = await SurrogateAsync(taskId, Col(" 16001 ", "16001", "", "  ", null, "16002"));

        Assert.True(Equals(0, resp["code"]), $"加签应成功: {resp["msg"]}");
        Assert.Equal(new List<string> { "leader", "16001", "16002" }, await ActorsAsync(taskId));
        Assert.Equal(3, ActorRows(taskId));   // 不得有 actor_id=''／'   '／' 16001 ' 的行
    }

    /// <summary>
    /// 「逗号串与数组两种形态在这条上必须同判据，别只修一条腿」（§2.11 逐字搬 §2.10）：
    /// 同一批人给成串形态与数组形态，落库归属值逐字相同。
    /// 改前实测红样：串腿 <c>["16101","16102"]</c>（split 后 trim），数组腿 <c>[" 16101 ",""," 16102 "]</c>
    /// （不 trim、空元素照收）⇒ 两形两个答案。
    /// </summary>
    [Fact]
    public async Task SurrogateStringLegAndArrayLegGiveTheSameAnswer()
    {
        // 两条腿各打一张任务（同一张会被写侧判重互相吃掉，观测不到"两形是否同判据"）
        var byStringTask = await DoingTaskIdOfAsync(await StartToApprovalAsync(), "leader");
        var byArrayTask = await DoingTaskIdOfAsync(await StartToApprovalAsync(), "leader");

        var s = await SurrogateAsync(byStringTask, " 16101 ,, 16102 ,");
        Assert.True(Equals(0, s["code"]), $"串腿应成功: {s["msg"]}");
        var stringActors = (await ActorsAsync(byStringTask)).Skip(1).ToList();

        var a = await SurrogateAsync(byArrayTask, Col(" 16101 ", "", "   ", "16102"));
        Assert.True(Equals(0, a["code"]), $"数组腿应成功: {a["msg"]}");
        var arrayActors = (await ActorsAsync(byArrayTask)).Skip(1).ToList();

        Assert.Equal(stringActors, arrayActors);                                  // 两形同判据
        Assert.Equal(new List<string> { "16101", "16102" }, stringActors);        // 且都是 trim＋丢空后的值
    }

    /// <summary>
    /// 全空白集合 ⇒ 与既有的"缺参数"档同判（§2.11 要求③：沿用 <c>processTaskId/actorIds 缺失</c>
    /// 错误信封，不新造错误码/文案）＋零新行。
    /// 改前实测红样：code=<b>0</b>（当成功）且真落一行 <c>actor_id='   '</c>。
    /// </summary>
    [Fact]
    public async Task SurrogateAllBlankIsTheSameArmAsMissingActorIds()
    {
        var taskId = await DoingTaskIdOfAsync(await StartToApprovalAsync(), "leader");
        var before = ActorRows(taskId);

        var allBlank = await SurrogateAsync(taskId, Col("", "  ", null));
        var missing = await SurrogateAsync(taskId, null);

        Assert.Equal(99999999, allBlank["code"]);
        Assert.Equal(missing["code"], allBlank["code"]);
        Assert.Equal(missing["msg"], allBlank["msg"]);
        Assert.Equal("processTaskId/actorIds 缺失", allBlank["msg"]);
        Assert.Equal(before, ActorRows(taskId));          // 一行都不许多
        Assert.All(await ActorsAsync(taskId), a => Assert.NotEmpty(a.Trim()));  // 归属列不得有空/纯空白值
    }

    /// <summary>
    /// 反向哨兵（§2.11 要求④）：<c>"0"</c>／<c>"00"</c>／<c>"a"</c> 是三张不同的脸，
    /// 不得被"看起来像空/像同一个数"的判据吃掉或折叠（php 那把松散 in_array 尺子的反面）。
    /// 纯空白的 <c>" "</c> 与空串同档 ⇒ 丢弃（trim 后为空）。
    /// </summary>
    [Fact]
    public async Task ZeroLikeActorsAreDistinctPeopleAndOnlyBlankIsDropped()
    {
        var taskId = await DoingTaskIdOfAsync(await StartToApprovalAsync(), "leader");

        var resp = await SurrogateAsync(taskId, Col("0", "00", " ", "a"));

        Assert.True(Equals(0, resp["code"]), $"加签应成功: {resp["msg"]}");
        Assert.Equal(new List<string> { "leader", "0", "00", "a" }, await ActorsAsync(taskId));
        Assert.Equal(4, ActorRows(taskId));   // "0"/"00"/"a" 三个人都在，只有 " " 被丢
    }

    // ═══ 主键档：processTaskId 缺失/空串/0 必须响亮报错，不得拿 ''/0 落库 ═══

    /// <summary>
    /// 归属值可有可无（丢了就行），主键没有就是调用方写错了（§2.11「主键类参数另判一档」）。
    /// 改前实测红样：<c>processTaskId=0</c> ⇒ code=0 且真往 <c>process_task_id=0</c> 插行（孤儿脏数据）；
    /// 缺失/空串两档本仓已报错（沿用既有信封，本轮不动文案）。
    /// </summary>
    [Fact]
    public async Task SurrogateRejectsMissingEmptyAndZeroTaskId()
    {
        // 先建一张真实任务，保证"报错"不是因为没数据
        var iid = await StartToApprovalAsync();
        Assert.NotEqual(0, iid);

        foreach (var (name, id) in new (string, object?)[]
                 { ("缺失", null), ("空串", ""), ("零", 0L), ("负数", -1L), ("非数字", "abc") })
        {
            var resp = await SurrogateAsync(id, Col("16301"));
            Assert.True(Equals(99999999, resp["code"]), $"{name} 档应报错，实得 code={resp["code"]}");
            Assert.Equal("processTaskId/actorIds 缺失", resp["msg"]);   // 不新造错误码/文案
        }
        Assert.DoesNotContain(_repo.TaskActors.Values, a => a.ProcessTaskId <= 0);  // 一行都没许落
    }

    /// <summary>绕过门面直连仓储的主键档：同样不得静默插入。</summary>
    [Fact]
    public async Task MemoryRepoAddTaskActorRejectsNonPositiveTaskId()
    {
        var taskId = await DoingTaskIdOfAsync(await StartToApprovalAsync(), "leader");

        await Assert.ThrowsAsync<JeeflowException>(
            () => _repo.AddTaskActorAsync(0, new List<string> { "16401" }));
        Assert.Equal(1, ActorRows(taskId));   // 既有任务行没被牵连
    }

    // ═══ 写侧兜底：绕过门面/引擎直连仓储也灌不进空值（§2.11 要求①第二层）═══

    /// <summary>
    /// 改前实测红样：内存仓直连 <c>[" 16501 ","16501","","  ","0"]</c> ⇒ 5 行
    /// （<c>' 16501 '</c> 与 <c>'16501'</c> 同一人两行、<c>''</c> 与 <c>'  '</c> 两行空归属值）。
    /// </summary>
    [Fact]
    public async Task MemoryRepoWritePathTrimsDropsBlankAndDedups()
    {
        var taskId = await DoingTaskIdOfAsync(await StartToApprovalAsync(), "leader");

        await _repo.AddTaskActorAsync(taskId, new List<string> { " 16501 ", "16501", "", "  ", "0" });

        Assert.Equal(new List<string> { "leader", "16501", "0" }, await ActorsAsync(taskId));
        Assert.Equal(3, ActorRows(taskId));
    }

    /// <summary>写侧 trim 与判重咬合：先落 16601 再写 " 16601 " ⇒ 仍是同一人一行（不 trim 就两行）。</summary>
    [Fact]
    public async Task MemoryRepoPaddedValueHitsTheDedupRule()
    {
        var taskId = await DoingTaskIdOfAsync(await StartToApprovalAsync(), "leader");

        await _repo.AddTaskActorAsync(taskId, new List<string> { "16601" });
        await _repo.AddTaskActorAsync(taskId, new List<string> { " 16601 " });

        Assert.Equal(new List<string> { "leader", "16601" }, await ActorsAsync(taskId));
        Assert.Equal(2, ActorRows(taskId));
    }

    // ═══ 删除位（issues/142 §9.2 第二批，与 php 第二遍同一把尺子）═══

    /// <summary>
    /// 删除位必须按归一后的值比较：「 8601 」删得掉库里的 8601。
    /// 改前实测红样：AddTaskActorAsync 落 trim 值后，RemoveTaskActorAsync 拿未 trim 原值比
    /// ⇒ 静默 no-op（报成功却没删）。空列表/全空白 ⇒ 什么都不删（早退），
    /// 历史 actor_id='' 的脏行不得被批量误删（issues/129 删除位对偶）。
    /// </summary>
    [Fact]
    public async Task MemoryRepoRemoveTaskActorTrimsMatchAndBlankListIsNoOp()
    {
        var taskId = await DoingTaskIdOfAsync(await StartToApprovalAsync(), "leader");
        await _repo.AddTaskActorAsync(taskId, new List<string> { " 8601 " });
        Assert.Equal(new List<string> { "leader", "8601" }, await ActorsAsync(taskId));

        await _repo.RemoveTaskActorAsync(taskId, new List<string> { " 8601 " });
        Assert.Equal(new List<string> { "leader" }, await ActorsAsync(taskId));

        // 历史脏行：库里有 actor_id=空串 的行（旧版本写进去的）。空串入参绝不能把它当"要删的人"。
        _repo.TaskActors.Add(9_999_999, new MemoryRepository.ActorRow
        {
            Id = 9_999_999, ProcessTaskId = taskId, ActorId = "", CreateTime = System.DateTime.Now,
        });
        await _repo.RemoveTaskActorAsync(taskId, new List<string> { "" });
        await _repo.RemoveTaskActorAsync(taskId, new List<string> { "  ", null! });
        await _repo.RemoveTaskActorAsync(taskId, new List<string>());
        Assert.Equal(2, ActorRows(taskId));
        Assert.Contains("", _repo.TaskActors.Values
            .Where(a => a.ProcessTaskId == taskId).Select(a => a.ActorId));
    }

    // ═══ 发起腿 f_nextNodeOperator ═══

    /// <summary>发起（facade startAndExecute），返回 approval 节点任务 id。</summary>
    private async Task<long> StartAndExecuteToApprovalAsync(object? nextNodeOperator)
    {
        var resp = await _facade.FlowAsync("processDefine/startAndExecute", new FlowData
        {
            [FlowConst.ProcessDefineIdKey] = await DefineAsync(),
            ["operator"] = "zhangsan",
            [FlowConst.ProcessStartNextNodeOperator] = nextNodeOperator,
        });
        Assert.True(Equals(0, resp["code"]), $"startAndExecute 应成功: {resp["msg"]}");
        var iid = Convert.ToInt64(((Dictionary<string, object?>)resp["data"]!)[FlowConst.ProcessInstanceIdKey]);
        foreach (var t in await _repo.FindDoingTasksAsync(iid, null))
            return t.TaskId!.Value;
        throw new InvalidOperationException("发起后没有待办");
    }

    /// <summary>本栈发起腿的取证格（普查 §6 未验清单第 1 条）：数组形态绝不得整条串化成类型名。</summary>
    [Fact]
    public async Task StartLegArrayNextNodeOperatorIsNotStringifiedToTypeName()
    {
        var taskId = await StartAndExecuteToApprovalAsync(Col("17001", "17002"));

        var actors = await ActorsAsync(taskId);
        Assert.DoesNotContain(actors, a => a.Contains("System.Collections"));   // 类型名形状
        Assert.Equal(new List<string> { "17001", "17002" }, actors);
    }

    /// <summary>发起腿数组形态的归一义务与串腿同一条：trim、丢空、折叠。</summary>
    [Fact]
    public async Task StartLegArrayNextNodeOperatorTrimsDropsBlankAndFoldsDuplicates()
    {
        var taskId = await StartAndExecuteToApprovalAsync(Col(" 17101 ", "17101", "", "  ", null, "17102"));

        Assert.Equal(new List<string> { "17101", "17102" }, await ActorsAsync(taskId));
        Assert.Equal(2, ActorRows(taskId));
    }

    /// <summary>发起腿两形同判据：串形态与数组形态指向同一批人 ⇒ 归属值逐字相同。</summary>
    [Fact]
    public async Task StartLegStringAndArrayFormsAgree()
    {
        var byString = await StartAndExecuteToApprovalAsync(" 17201 ,, 17202 ,");
        var byArray = await StartAndExecuteToApprovalAsync(Col(" 17201 ", "", "   ", "17202"));

        Assert.Equal(await ActorsAsync(byString), await ActorsAsync(byArray));
        Assert.Equal(new List<string> { "17201", "17202" }, await ActorsAsync(byString));
    }

    /// <summary>数字元素收敛成字符串（不变体串化），不得整条串成类型名。</summary>
    [Fact]
    public async Task StartLegNumericElementsBecomeStrings()
    {
        var taskId = await StartAndExecuteToApprovalAsync(Col(17301L, 17302));

        Assert.Equal(new List<string> { "17301", "17302" }, await ActorsAsync(taskId));
    }

    /// <summary>
    /// 丢完为空 ⇒ 与"没填"同档：回落节点 assignee（串腿给 "" 时就是这个行为，数组腿必须同答案）。
    /// 改前实测红样：数组腿给 <c>["", "  "]</c> 时被 ToString 成类型名 ⇒ 落一个"类型名参与者"，
    /// 该单永远没人能办。
    /// </summary>
    [Fact]
    public async Task StartLegBlankOnlyArrayFallsBackToAssignee()
    {
        var taskId = await StartAndExecuteToApprovalAsync(Col("", "  "));

        Assert.Equal(new List<string> { "leader" }, await ActorsAsync(taskId));
    }

    // ═══ 消费腿 tf_nextNodeOperator（processTask/execute）═══

    /// <summary>办理并提交 tf_nextNodeOperator，返回下一节点任务 id。</summary>
    private async Task<long> ExecuteWithNextOperatorAsync(long instanceId, object? nextNodeOperator)
    {
        var task = (await _repo.FindDoingTasksAsync(instanceId, null))[0];
        var resp = await _facade.FlowAsync("processTask/execute", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = task.TaskId,
            ["operator"] = (await ActorsAsync(task.TaskId!.Value))[0],
            [FlowConst.NextNodeOperator] = nextNodeOperator,
        });
        Assert.True(Equals(0, resp["code"]), $"办理应成功: {resp["msg"]}");
        var next = await _repo.FindDoingTasksAsync(instanceId, null);
        if (next.Count == 0) return -1;   // 没待办 ⇒ 用哨兵 id，断言由调用方判
        return next[0].TaskId!.Value;
    }

    /// <summary>
    /// 消费腿两形同判据：数组里全是空值 ⇒ 归一后为空 ⇒ 与串腿给 <c>""</c> 同档（回落 assignee）。
    /// 改前实测红样：数组腿 <c>[""]</c> 归一判据没跑，直接当"已指派"用 ⇒ 下一节点<b>零参与者</b>，
    /// 落一条谁也办不了的待办（§6.1 点名过的死锁黑洞形状）。
    /// </summary>
    [Fact]
    public async Task ExecuteLegBlankOnlyArrayFallsBackToAssignee()
    {
        var inst = await _engine.StartProcessInstanceByIdAsync(
            await DefineAsync(), "zhangsan", new FlowData());

        var nextId = await ExecuteWithNextOperatorAsync(inst.InstanceId!.Value, Col(""));

        Assert.True(nextId > 0, "空集合指派的下一节点必须仍然存在");
        Assert.Equal(new List<string> { "leader" }, await ActorsAsync(nextId));   // 与串腿给 "" 同档
        Assert.NotEmpty(await ActorsAsync(nextId));                               // 不得落零参与者的死锁黑洞
    }

    /// <summary>消费腿数组形态 trim／丢空／折叠，与串形态逐字同答案。</summary>
    [Fact]
    public async Task ExecuteLegArrayAndStringFormsAgree()
    {
        var byArrayInst = await _engine.StartProcessInstanceByIdAsync(await DefineAsync(), "zhangsan", new FlowData());
        var byArray = await ExecuteWithNextOperatorAsync(
            byArrayInst.InstanceId!.Value, Col(" 17501 ", "", "  ", "17502", "17501"));

        var byStringInst = await _engine.StartProcessInstanceByIdAsync(await DefineAsync(), "zhangsan", new FlowData());
        var byString = await ExecuteWithNextOperatorAsync(byStringInst.InstanceId!.Value, " 17501 ,, 17502 ,");

        Assert.Equal(new List<string> { "17501", "17502" }, await ActorsAsync(byArray));
        Assert.Equal(await ActorsAsync(byString), await ActorsAsync(byArray));
    }

    /// <summary>消费腿数字元素收敛成字符串（与发起腿同一条）。</summary>
    [Fact]
    public async Task ExecuteLegNumericElementsBecomeStrings()
    {
        var inst = await _engine.StartProcessInstanceByIdAsync(await DefineAsync(), "zhangsan", new FlowData());
        var nextId = await ExecuteWithNextOperatorAsync(inst.InstanceId!.Value, Col(17601L, 17602));

        Assert.Equal(new List<string> { "17601", "17602" }, await ActorsAsync(nextId));
    }

    // ═══ 转办腿 fromActor/toActor ═══

    private Task<Dictionary<string, object?>> TransferAsync(object? taskId, object? from, object? to,
                                                             string? op = "leader") =>
        _facade.FlowAsync("processTask/transfer", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = taskId,
            ["operator"] = op,
            ["fromActor"] = from,
            ["toActor"] = to,
        });

    /// <summary>
    /// 转办入参也吃同一枚判据：数组形态（前端单选控件常给成 <c>["x"]</c>）不得整条串成类型名。
    /// 改前实测红样：<c>fromActor=["leader"]</c> ⇒ 串成 <c>System.Collections.Generic.List`1[System.Object]</c>
    /// ⇒ "原办理人不是该任务参与人"，转办直接失败（且一旦拼错还会把类型名写进 actor_id）。
    /// </summary>
    [Fact]
    public async Task TransferArrayFormActorsAreNotStringifiedToTypeName()
    {
        var taskId = await DoingTaskIdOfAsync(await StartToApprovalAsync(), "leader");

        var resp = await TransferAsync(taskId, Col("leader"), Col("17701"));

        Assert.True(Equals(0, resp["code"]), $"转办应成功: {resp["msg"]}");
        Assert.Equal(new List<string> { "17701" }, await ActorsAsync(taskId));
        Assert.DoesNotContain(await ActorsAsync(taskId), a => a.Contains("System.Collections"));
    }

    /// <summary>转办入参 trim：带空格的人与不带空格的是同一个人，落库与比较都取 trim 后的值。</summary>
    [Fact]
    public async Task TransferPaddedActorsUseTrimmedValues()
    {
        var taskId = await DoingTaskIdOfAsync(await StartToApprovalAsync(), "leader");

        var resp = await TransferAsync(taskId, " leader ", " 17801 ");

        Assert.True(Equals(0, resp["code"]), $"转办应成功: {resp["msg"]}");
        Assert.Equal(new List<string> { "17801" }, await ActorsAsync(taskId));
        var vars = (await _repo.FindTaskByIdAsync(taskId))!.Variables;
        Assert.Equal("17801", vars.GetStr(FlowConst.TransferTo));   // 留痕同样记 trim 后的值
    }

    /// <summary>转办的空值档沿用既有"必填"信封（不新造错误码/文案），且不得留半成品。</summary>
    [Fact]
    public async Task TransferBlankArmKeepsExistingEnvelope()
    {
        var taskId = await DoingTaskIdOfAsync(await StartToApprovalAsync(), "leader");

        foreach (var (name, from, to) in new (string, object?, object?)[]
                 { ("fromActor 全空白", "   ", "17901"), ("toActor 全空白", "leader", "  "),
                   ("fromActor 空集合", Col(""), "17901"), ("toActor 空集合", "leader", Col("", "  ")) })
        {
            var resp = await TransferAsync(taskId, from, to);
            Assert.True(Equals(99999999, resp["code"]), $"{name} 应报错，实得 {resp["code"]}");
            Assert.Contains("必填", resp["msg"]!.ToString());
        }
        Assert.Contains("leader", await ActorsAsync(taskId));   // 参与者没被动过
    }

    // ═══ updateCCStatus 的 operator（§2.11 表第 4 行：入参归一后再比）═══

    /// <summary>
    /// 抄送已读腿的 operator 必须先归一再比：cc 行落的是 trim 后的值，
    /// 带空格的 operator 若不等值换算就会静默打不中（用户看到"点了已读没反应"）。
    /// 同时钉反面：空 operator 回落 demo 缺省（issues/129 案 A 的既有档），
    /// 不得把 <c>state=1</c> 打到历史 <c>actor_id=''</c> 的脏行上。
    /// </summary>
    [Fact]
    public async Task UpdateCcStatusComparesNormalizedOperator()
    {
        var iid = await StartToApprovalAsync();
        // 先落一条 trim 后的 cc 行（写侧归一后的形状就是 "18001"，不带空格）
        Assert.Equal(0, (await _facade.FlowAsync("processInstance/createCCInstance", new FlowData
        {
            [FlowConst.ProcessInstanceIdKey] = iid,
            ["operator"] = "zhangsan",
            ["actorIds"] = Col(" 18001 "),
        }))["code"]);
        // 历史脏行：actor_id=''（本轮写侧已挡，这里手工造出来只为钉"空 operator 不得覆写它"）
        _repo.CcInstances[999001] = new MemoryRepository.CcRow
        {
            Id = 999001, ProcessInstanceId = iid, ActorId = "", State = 0,
        };

        var padded = await _facade.FlowAsync("processInstance/updateCCStatus", new FlowData
        {
            [FlowConst.ProcessInstanceIdKey] = iid,
            ["operator"] = " 18001 ",
        });
        Assert.True(Equals(0, padded["code"]), $"已读应成功: {padded["msg"]}");
        Assert.Equal(1, _repo.CcInstances.Values.Single(c => c.ActorId == "18001").State);
        Assert.Equal(0, _repo.CcInstances[999001].State);   // 脏行没被空/带空格的 operator 顺手动过

        var blank = await _facade.FlowAsync("processInstance/updateCCStatus", new FlowData
        {
            [FlowConst.ProcessInstanceIdKey] = iid,
            ["operator"] = "   ",
        });
        Assert.Equal(0, blank["code"]);                     // 回落缺省 user1（issues/129 既有档）
        Assert.Equal(0, _repo.CcInstances[999001].State);   // 仍不得覆写 actor_id='' 的脏行
    }
}

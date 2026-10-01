using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// 门面第 <b>47</b> 个 action <c>processTask/removeTaskActor</c>（issues/115 残留 · C# 腿，八栈同批）。
/// 契约逐字依据＝jeeflow-doc <b>spec 06-facade.md §processTask/removeTaskActor</b>（七条语义＋守卫次序）
/// 与 §2.11（归属值归一单点）；行为基准＝java 腿
/// <c>jeeflow-core/src/test/java/com/mldong/jeeflow/test/RemoveTaskActorActionTest.java</c>（17 格同判据）。
///
/// <para>它填的是 SPI 与门面之间那段空档：<see cref="IProcessRepository.RemoveTaskActorAsync"/> 从一开始
/// 就是<b>必选</b>方法、本栈两仓（内存 / MySQL）都实现，但门面没有对应 action，摘人只能靠
/// <c>processTask/transfer</c>（摘 A <b>并</b>加 B）。owner 拍「做吧，要不然后面又扫到这个」
/// ⇒ 契约先行补齐，八栈同批。</para>
///
/// <para>三个兄弟 action 的分工写在本文件的判据主线里：<c>surrogate</c>/<c>addCandidate</c> 只加、
/// <c>transfer</c> 换人＋留痕、本 action <b>只摘不加零留痕</b>（不写任务变量、不覆写任务
/// <c>actor_id</c>/<c>operator</c> 列、<b>不 fire 事件</b>——issues/132 §11.3 定稿事件集没有"摘人"码，
/// 码 7 的语义是"参与者被替换"）。每条负向都同时断言"参与者一动不动"，
/// 因为摘人是删除操作，报错却删了一半比报错更糟。</para>
///
/// <para><see cref="WhitespacePaddedIdsAreRemovedAndDirtyRowsSurvive"/> 与
/// <see cref="UntrimmedHistoricalRowIsMatchedAndDeletedByRowValue"/> 打在<b>喂给 DELETE 的那一份实参</b>上：
/// 内存仓写侧归一后建不出空串行、也建不出未 trim 的 " leader " 行，故用 <see cref="DirtyRowSpyRepo"/>
/// 复刻历史脏行与 <c>DELETE ... WHERE actor_id IN (?)</c> 的<b>字面相等</b>语义（与 java 腿同名 spy 同形）——
/// 反面形状（拿归一值去删）＝判成同一人却一条没删、门面报成功而被摘的人待办还在（假成功）。</para>
/// </summary>
public class RemoveTaskActor115Tests
{
    /// <summary>start → apply(assignee=applicant) → approval(assignee=leader) → end。</summary>
    private const string TwoTaskFlow = """
        {"name": "remove-actor-115", "displayName": "摘除参与人", "type": "approval",
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
    private readonly DirtyRowSpyRepo _repo;
    private readonly JeeflowFacade _facade;
    private readonly AllEventsCapture _cap;
    private static int _defineSeq;

    public RemoveTaskActor115Tests()
    {
        _repo = new DirtyRowSpyRepo();
        var ctx = TestInfra.NewContext(_repo);
        _cap = new AllEventsCapture();
        ctx.RegisterEventListener(_cap);
        _repo.Configure(ctx);
        _engine = new JeeflowEngine(ctx);
        _facade = new JeeflowFacade(ctx);
    }

    // ── 夹具与取证辅助 ──

    private Task<long> DefineAsync() =>
        TestInfra.SaveFlowDefineAsync(_repo, "remove-actor-115-" + _defineSeq++, TwoTaskFlow);

    /// <summary>发起一条实例，停在 apply 节点（参与者＝发起人 zhangsan）。</summary>
    private async Task<long> StartInstanceAsync()
    {
        var inst = await _engine.StartProcessInstanceByIdAsync(await DefineAsync(), "zhangsan", new FlowData());
        return inst.InstanceId!.Value;
    }

    private async Task<long> DoingTaskIdAsync(long instanceId, string taskName)
    {
        foreach (var t in await _repo.FindDoingTasksAsync(instanceId, null))
            if (taskName == t.TaskName) return t.TaskId!.Value;
        throw new InvalidOperationException($"夹具里没有进行中任务: {taskName}");
    }

    /// <summary>加签成人手（用兄弟 action 造多参与者现场，不直接塞仓储）。</summary>
    private async Task AddActorsAsync(long taskId, params object?[] actors)
    {
        var resp = await _facade.FlowAsync("processTask/addCandidate", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = taskId,
            ["actorIds"] = Col(actors),
        });
        Assert.True(Equals(0, resp["code"]), $"加签夹具应成功: {resp["msg"]}");
    }

    /// <summary>办结 apply ⇒ 该任务离开 DOING（历史任务那一档的夹具）。</summary>
    private async Task FinishApplyAsync(long instanceId)
    {
        await _engine.ExecuteProcessTaskAsync(
            await DoingTaskIdAsync(instanceId, "apply"), "zhangsan",
            new FlowData { [FlowConst.SubmitType] = (int)WfSubmitType.Apply });
    }

    /// <summary>参与者取证走仓储而不是返回值——判据必须打在"落库的值"上。</summary>
    private async Task<List<string>> ActorsAsync(long taskId) => await _repo.FindTaskActorsAsync(taskId);

    private static List<object?> Col(params object?[] items) => items.ToList();

    private Task<Dictionary<string, object?>> RemoveAsync(object? taskId, object? actorIds, object? op) =>
        _facade.FlowAsync("processTask/removeTaskActor", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = taskId,
            ["actorIds"] = actorIds,
            ["operator"] = op,
        });

    private static void AssertOk(Dictionary<string, object?> resp) =>
        Assert.True(Equals(0, resp["code"]), $"应成功: {resp["msg"]}");

    /// <summary>带判据说明的等值断言：xunit 2.9 的 <c>Assert.Equal</c> 没有 message-first 重载，
    /// 说明与实际读数一并拼进失败文案（集合按元素序逐位比，参与者顺序本身就是判据）。</summary>
    private static void AssertEq<T>(string why, T expected, T actual) =>
        Assert.True(SameValue(expected, actual),
            $"{why}\n  期望: {Show(expected)}\n  实得: {Show(actual)}");

    private static bool SameValue<T>(T expected, T actual) =>
        (expected, actual) switch
        {
            (System.Collections.IEnumerable e, System.Collections.IEnumerable a)
                when expected is not string => e.Cast<object?>().SequenceEqual(a.Cast<object?>()),
            _ => Equals(expected, actual),
        };

    private static string Show<T>(T v) => v switch
    {
        string s => $"\"{s}\"",
        System.Collections.IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Select(Show)) + "]",
        _ => Convert.ToString(v) ?? "null",
    };

    // ═══ 语义 1「只摘不加」＋ 正向核心：摘掉点名的人，其余参与人一动不动 ═══

    [Fact]
    public async Task RemovesOnlyTheNamedActorAndKeepsTheRest()
    {
        var instanceId = await StartInstanceAsync();
        var taskId = await DoingTaskIdAsync(instanceId, "apply");
        await AddActorsAsync(taskId, "9001", "9002");
        AssertEq("夹具：加签后三个人都在",
            new List<string> { "zhangsan", "9001", "9002" }, await ActorsAsync(taskId));

        var resp = await RemoveAsync(taskId, Col("9001"), "flow.admin");

        AssertOk(resp);
        AssertEq("只删点名的 9001，其余参与人原样保留（含顺序）",
            new List<string> { "zhangsan", "9002" }, await ActorsAsync(taskId));
        Assert.True(resp.ContainsKey("data") && resp["data"] == null,
            $"data 出 null（spec 同节：前端消费面不读 data），实得 {Show(resp["data"])}");
    }

    /// <summary>多支一起摘（集合语义，不是"一次只能摘一个人"）。</summary>
    [Fact]
    public async Task RemovesSeveralActorsInOneCall()
    {
        var instanceId = await StartInstanceAsync();
        var taskId = await DoingTaskIdAsync(instanceId, "apply");
        await AddActorsAsync(taskId, "9001", "9002", "9003");

        AssertOk(await RemoveAsync(taskId, Col("9001", "9002"), "flow.admin"));

        AssertEq("一次摘两人", new List<string> { "zhangsan", "9003" }, await ActorsAsync(taskId));
    }

    /// <summary>逗号串腿与数组腿同判据（§2.11 第 1 行"两形一把尺子"，摘人腿不得另抄一份）。</summary>
    [Fact]
    public async Task CommaStringShapeRemovesTheSamePeople()
    {
        var instanceId = await StartInstanceAsync();
        var taskId = await DoingTaskIdAsync(instanceId, "apply");
        await AddActorsAsync(taskId, "9001", "9002");

        AssertOk(await RemoveAsync(taskId, "9001, 9002 ", "flow.admin"));

        AssertEq("逗号串带空格照样命中", new List<string> { "zhangsan" }, await ActorsAsync(taskId));
    }

    // ═══ 语义 3「归属判据同 transfer」：只能摘自己那一票，auto/admin 例外 ═══

    /// <summary>本人摘自己的那一票：无需特权。</summary>
    [Fact]
    public async Task SelfRemovalNeedsNoPrivilege()
    {
        var instanceId = await StartInstanceAsync();
        var taskId = await DoingTaskIdAsync(instanceId, "apply");
        await AddActorsAsync(taskId, "9001");

        AssertOk(await RemoveAsync(taskId, Col("9001"), "9001"));

        AssertEq("本人摘自己那一票，另一票不动",
            new List<string> { "zhangsan" }, await ActorsAsync(taskId));
    }

    /// <summary>借道摘他人必须拦下（transfer 能"摘 A 加 B"是因为 A＝操作人本人，本 action 同理）。</summary>
    [Fact]
    public async Task RemovingSomeoneElseWithoutPrivilegeIsRejected()
    {
        var instanceId = await StartInstanceAsync();
        var taskId = await DoingTaskIdAsync(instanceId, "apply");
        await AddActorsAsync(taskId, "9001");

        var resp = await RemoveAsync(taskId, Col("9001"), "zhangsan");

        AssertEq("逐字文案", "无权限摘除该任务参与人", resp["msg"]);
        AssertEq("报错后一条都不许删",
            new List<string> { "zhangsan", "9001" }, await ActorsAsync(taskId));
    }

    /// <summary><c>flow.auto</c> 与 <c>flow.admin</c> 同档放行（<c>IsPrivilegedOperator</c> 既有口径）。</summary>
    [Fact]
    public async Task AutoSystemOperatorIsAlsoPrivileged()
    {
        var instanceId = await StartInstanceAsync();
        var taskId = await DoingTaskIdAsync(instanceId, "apply");
        await AddActorsAsync(taskId, "9001");

        AssertOk(await RemoveAsync(taskId, Col("9001"), "flow.auto"));

        AssertEq("flow.auto 与 flow.admin 同档",
            new List<string> { "zhangsan" }, await ActorsAsync(taskId));
    }

    // ═══ 语义 5「不得摘空」：判据是集合差，不是入参条数 ═══

    [Fact]
    public async Task NeverEmptiesTheTask()
    {
        var instanceId = await StartInstanceAsync();
        var taskId = await DoingTaskIdAsync(instanceId, "apply");
        AssertEq("夹具：单人任务", new List<string> { "zhangsan" }, await ActorsAsync(taskId));

        var resp = await RemoveAsync(taskId, Col("zhangsan"), "zhangsan");

        AssertEq("摘空会造出无人可办又无法重派的死单", "至少需保留一名参与人", resp["msg"]);
        AssertEq("人还在", new List<string> { "zhangsan" }, await ActorsAsync(taskId));
    }

    /// <summary>
    /// 绕过档：<c>actorIds</c> 里混进非参与者 id，"入参条数 &lt; 参与人数"这种判据会放过去，
    /// 集合差判据必须照样拦下（spec 语义 5 的第二句）。
    /// </summary>
    [Fact]
    public async Task MixedNonParticipantIdCannotBypassTheKeepOneFloor()
    {
        var instanceId = await StartInstanceAsync();
        var taskId = await DoingTaskIdAsync(instanceId, "apply");
        await AddActorsAsync(taskId, "9001");

        var resp = await RemoveAsync(taskId, Col("zhangsan", "9001", "ghost"), "zhangsan");

        AssertEq("混入非参与者 id 也绕不过下限", "至少需保留一名参与人", resp["msg"]);
        AssertEq("报错后一动不动",
            new List<string> { "zhangsan", "9001" }, await ActorsAsync(taskId));
    }

    // ═══ 语义 4「只作用于进行中任务」：历史参与人行是审批链的取证依据 ═══

    [Fact]
    public async Task FinishedTaskIsProtected()
    {
        var instanceId = await StartInstanceAsync();
        var taskId = await DoingTaskIdAsync(instanceId, "apply");
        await FinishApplyAsync(instanceId);              // apply 转 FINISHED，approval 起新任务

        var resp = await RemoveAsync(taskId, Col("zhangsan"), "flow.admin");

        AssertEq("历史参与人行是审批链的取证依据，非 DOING 一律拦下",
            "任务非进行中，不可摘除参与人", resp["msg"]);
        AssertEq("已办结任务的参与人行不得被改写历史",
            new List<string> { "zhangsan" }, await ActorsAsync(taskId));
    }

    // ═══ 语义 2「不留痕、不 fire 事件」═══

    [Fact]
    public async Task LeavesNoTraceAndFiresNoEvent()
    {
        var instanceId = await StartInstanceAsync();
        var taskId = await DoingTaskIdAsync(instanceId, "apply");
        await AddActorsAsync(taskId, "9001");
        // 任务行留痕列的"改前读数"：建单路径本来就会写 update_user/update_time（ProcessTask.Create
        // 里 op + now），判据只能是"摘人这一步没动它"，不能假定它本来是 null。
        var before = await _repo.FindTaskByIdAsync(taskId);
        var varsBefore = new Dictionary<string, object?>(before!.Variables);
        var updateUserBefore = before.UpdateUser;
        var updateTimeBefore = before.UpdateTime;
        _cap.Events.Clear();

        AssertOk(await RemoveAsync(taskId, Col("9001"), "flow.admin"));

        Assert.True(_cap.Events.Count == 0,
            "摘人不在 132 定稿事件集里，一律不 fire（码 7 的语义是「参与者被替换」），实得事件: "
            + string.Join(",", _cap.Events.Select(e => e.EventType.ToString())));
        var after = await _repo.FindTaskByIdAsync(taskId);
        var vars = after!.Variables;
        Assert.True(!vars.ContainsKey(FlowConst.TransferHistory), "不写 tf_transferHistory");
        Assert.True(!vars.ContainsKey(FlowConst.TransferTo), "不写 tf_transferTo");
        Assert.True(!vars.ContainsKey(FlowConst.SubmitType), "不置 submitType");
        AssertEq("任务变量整张表一动不动（只摘不加，不新建、不覆写）",
            varsBefore.Keys.OrderBy(k => k, StringComparer.Ordinal),
            vars.Keys.OrderBy(k => k, StringComparer.Ordinal));
        AssertEq("不覆写任务留痕列 update_user", updateUserBefore, after.UpdateUser);
        AssertEq("不覆写任务留痕列 update_time", updateTimeBefore, after.UpdateTime);
        Assert.True(after.ActorId == null,
            "不覆写任务 actor_id 列（进行中任务该列恒无值是既有不变量）");
    }

    // ═══ 语义 7「幂等」：非参与者静默忽略，重放第二次仍成功 ═══

    [Fact]
    public async Task RemovingANonParticipantIsIdempotent()
    {
        var instanceId = await StartInstanceAsync();
        var taskId = await DoingTaskIdAsync(instanceId, "apply");
        await AddActorsAsync(taskId, "9001");

        AssertOk(await RemoveAsync(taskId, Col("9001"), "flow.admin"));
        AssertEq("第一次真摘", new List<string> { "zhangsan" }, await ActorsAsync(taskId));

        AssertOk(await RemoveAsync(taskId, Col("9001"), "flow.admin"));   // 重放第二次
        AssertEq("重放后集合不变（一个都没命中 ⇒ 空操作）",
            new List<string> { "zhangsan" }, await ActorsAsync(taskId));
    }

    // ═══ 必填档与守卫次序（spec 同节钉死，八栈不接受自行排序）═══

    /// <summary>
    /// 三档逐字文案：operator 缺省/纯空白 ⇒ <c>operator 必填</c>（严禁回落 user1）；
    /// 主键缺失或 <c>actorIds</c> 丢完为空 ⇒ 与 <c>surrogate</c> 同一文案
    /// <c>processTaskId/actorIds 缺失</c>；任务不存在 ⇒ <c>任务不存在</c>。
    /// </summary>
    [Fact]
    public async Task MissingArmsReuseTheExistingErrorEnvelope()
    {
        var instanceId = await StartInstanceAsync();
        var taskId = await DoingTaskIdAsync(instanceId, "apply");
        var before = new List<string>(await ActorsAsync(taskId));

        AssertEq("缺省 operator ⇒ 必填档（严禁回落 user1）",
            "operator 必填", (await RemoveAsync(taskId, Col("zhangsan"), null))["msg"]);
        AssertEq("纯空白 operator 也不给过",
            "operator 必填", (await RemoveAsync(taskId, Col("zhangsan"), "   "))["msg"]);
        AssertEq("主键空串 ⇒ 兄弟 action 同文案",
            "processTaskId/actorIds 缺失", (await RemoveAsync("", Col("9001"), "flow.admin"))["msg"]);
        AssertEq("actorIds 丢完为空 ⇒ 兄弟 action 同文案", "processTaskId/actorIds 缺失",
            (await RemoveAsync(taskId, Col("", "  ", null), "flow.admin"))["msg"]);
        AssertEq("任务不存在", "任务不存在", (await RemoveAsync(424242L, Col("9001"), "flow.admin"))["msg"]);

        AssertEq("五个报错档一条都不许删", before, await ActorsAsync(taskId));
    }

    /// <summary>
    /// 守卫次序（spec 同节末尾那段）：<c>operator 必填</c> 排在缺参数之前——
    /// 否则"参数全缺"会先报主键缺失，把鉴权缺口藏进参数报错里；
    /// 权限档排在 DOING 档之前。
    /// </summary>
    [Fact]
    public async Task GuardOrderIsFixedAcrossStacks()
    {
        var instanceId = await StartInstanceAsync();
        var taskId = await DoingTaskIdAsync(instanceId, "apply");

        AssertEq("operator 必填排在主键缺失档之前（否则鉴权缺口会被参数报错藏起来）",
            "operator 必填", (await RemoveAsync("", Col("9001"), null))["msg"]);

        await FinishApplyAsync(instanceId);   // apply 已非 DOING，operator 又不是参与者
        AssertEq("权限档先于非进行中档（否则外人可以靠「任务已完成」探到别人的任务状态）",
            "无权限摘除该任务参与人", (await RemoveAsync(taskId, Col("zhangsan"), "outsider"))["msg"]);
    }

    // ═══ 门禁新格：带空格入参可删 ∧ 空值不误删 actor_id='' 脏行 ═══

    /// <summary>
    /// <c>" 9001 "</c> 必须命中库里的人（硬要求②"落库与比较一律取 trim 后的值"）；
    /// 同时喂进 DELETE 的实参永不能含空串/<c>null</c>——历史 <c>actor_id=''</c> 脏行
    /// 是 <c>DELETE ... actor_id IN (?)</c> 的受害者，判据打在实参与脏行存活两处。
    /// </summary>
    [Fact]
    public async Task WhitespacePaddedIdsAreRemovedAndDirtyRowsSurvive()
    {
        var instanceId = await StartInstanceAsync();
        var taskId = await DoingTaskIdAsync(instanceId, "apply");
        await AddActorsAsync(taskId, "9001", "9002");
        _repo.SeedDirtyRow(taskId, "");                  // 复刻历史脏行（内存仓写侧归一后建不出来）
        _repo.SeedDirtyRow(taskId, "   ");               // 纯空白那一支也算脏行

        AssertOk(await RemoveAsync(taskId, Col(" 9001 ", "", null, "   ", "9002"), "flow.admin"));

        AssertEq("带空格的入参删得掉真人，其余参与人不动",
            new List<string> { "zhangsan" }, await _repo.FindRealActorsAsync(taskId));
        AssertEq("空串/纯空白绝不能喂进 DELETE ⇒ 历史脏行必须原样还在",
            new List<string> { "", "   " }, _repo.DirtyRemaining(taskId));
        foreach (var call in _repo.RemoveCalls)
            foreach (var actor in call)
                Assert.True(actor.Trim().Length > 0, $"喂给 DELETE 的实参不得含空串/纯空白: {Show(call)}");
    }

    // ═══ 语义 6「匹配取归一值、DELETE 取行上的原值」＋语义 5「脏行不算一个人」═══

    /// <summary>
    /// 库里的行是修复前落下的未 trim 原值 <c>" 9101 "</c>，入参给 <c>"9101"</c>：
    /// 判据必须把它当成同一个人<b>并真删掉</b>，且喂进 DELETE 的实参是<b>那一行的原值</b>。
    /// 反面形状＝拿归一值去删：判成同一人却一条没删，门面报成功而被摘的人待办还在（假成功）。
    /// </summary>
    [Fact]
    public async Task UntrimmedHistoricalRowIsMatchedAndDeletedByRowValue()
    {
        var instanceId = await StartInstanceAsync();
        var taskId = await DoingTaskIdAsync(instanceId, "apply");
        _repo.SeedDirtyRow(taskId, " 9101 ");            // 历史未 trim 行（写侧归一后正常路径造不出来）

        AssertOk(await RemoveAsync(taskId, Col("9101"), "flow.admin"));

        AssertEq("未 trim 的历史行被归一匹配命中并删除",
            new List<string> { "zhangsan" }, await _repo.FindRealActorsAsync(taskId));
        AssertEq("脏行清单里那一行确实没了", new List<string>(), _repo.DirtyRemaining(taskId));
        AssertEq("DELETE 的实参是行上的原值，不是归一后的值（否则删不掉）",
            new List<string> { " 9101 " }, _repo.RemoveCalls[^1]);
    }

    /// <summary>
    /// 「至少剩一人」的下限按<b>能办单的人数</b>算：库里只剩 <c>actor_id=''</c> 脏行时，
    /// 摘走最后一个真人必须报错——脏行谁也办不了，拿它撑住下限等于让"摘空"伪装成成功。
    /// </summary>
    [Fact]
    public async Task DirtyRowsDoNotPropUpTheKeepOneFloor()
    {
        var instanceId = await StartInstanceAsync();
        var taskId = await DoingTaskIdAsync(instanceId, "apply");
        _repo.SeedDirtyRow(taskId, "");
        _repo.SeedDirtyRow(taskId, "   ");

        var resp = await RemoveAsync(taskId, Col("zhangsan"), "flow.admin");

        AssertEq("脏行不算一个人", "至少需保留一名参与人", resp["msg"]);
        AssertEq("报错后真人那行还在",
            new List<string> { "zhangsan" }, await _repo.FindRealActorsAsync(taskId));
    }

    // ═══ 兄弟 action 回归：只加／换人语义不被本 action 污染 ═══

    [Fact]
    public async Task SiblingActionsKeepTheirOwnSemantics()
    {
        var instanceId = await StartInstanceAsync();
        var taskId = await DoingTaskIdAsync(instanceId, "apply");

        AssertOk(await _facade.FlowAsync("processTask/surrogate", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = taskId, ["actorIds"] = Col("9101"),
        }));
        AssertEq("surrogate 仍旧只加不摘",
            new List<string> { "zhangsan", "9101" }, await ActorsAsync(taskId));

        AssertOk(await RemoveAsync(taskId, Col("9101"), "flow.admin"));
        AssertEq("摘人不带加人", new List<string> { "zhangsan" }, await ActorsAsync(taskId));

        await FinishApplyAsync(instanceId);              // 推进出 approval 节点（参与者＝leader）
        var approvalTask = await DoingTaskIdAsync(instanceId, "approval");
        AssertOk(await _facade.FlowAsync("processTask/transfer", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = approvalTask, ["operator"] = "leader",
            ["fromActor"] = "leader", ["toActor"] = "boss",
        }));
        AssertEq("transfer 换人语义不变",
            new List<string> { "boss" }, await ActorsAsync(approvalTask));
        AssertEq("transfer 仍写 submitType=7 留痕", 7, Convert.ToInt32(
            (await _repo.FindTaskByIdAsync(approvalTask))!.Variables.GetObj(FlowConst.SubmitType)));
    }

    // ── 辅助 ──

    /// <summary>事件 sink：全量录制（不收筛）。"摘人不 fire 任何事件"的判据要求看见<b>所有</b>码，
    /// 所以不能像 141/132 那些夹具一样只收自己关心的码。</summary>
    private sealed class AllEventsCapture : IProcessEventListener
    {
        public List<ProcessEvent> Events { get; } = new();

        public Task OnEventAsync(ProcessEvent @event)
        {
            Events.Add(@event);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 复刻"库里已经存在的历史脏行"与 <c>DELETE ... WHERE actor_id IN (?)</c> 的逐字语义
    /// （与 java 基准腿同名 <c>DirtyRowSpyRepo</c> 同形：继承内存仓而不是另写一份
    /// <see cref="IProcessRepository"/> 实现——本栈门面只依赖仓储接口，继承既有内存仓即可零行为分叉）：
    /// <para>① 脏行只能从外部塞进来：内存仓写侧 <c>AddTaskActorAsync</c> 已过归一单点
    /// （issues/142 B 批），正常路径建不出 <c>actor_id=''</c>、也建不出未 trim 的 <c>" 9101 "</c> 行，
    /// 而库里恰恰可能有这种修复前落下的行；</para>
    /// <para>② <see cref="FindTaskActorsAsync"/> 把真人那一半与脏行并起来返回（与 JDBC 一条裸
    /// <c>SELECT</c> 同形——脏行本来就会被读出来）；<see cref="RemoveTaskActorAsync"/> 记录每一次喂进
    /// DELETE 的实参，并按 <c>IN</c> 的<b>字面相等</b>命中删除（不做任何归一——这正是"门面必须给行上的
    /// 原值"的判据来源：只给归一值就一条也删不掉，门面却报成功）。</para>
    /// <para>③ <see cref="DirtyRowsDoNotPropUpTheKeepOneFloor"/> 把脏行当"算不算一个人"的判据用；
    /// 其余格里脏行只当被保护的对象用（发起人 zhangsan 全程在场，下限不靠脏行撑起）。</para>
    /// </summary>
    private sealed class DirtyRowSpyRepo : MemoryRepository
    {
        private readonly Dictionary<long, List<string>> _dirty = new();
        public List<List<string>> RemoveCalls { get; } = new();

        public void SeedDirtyRow(long taskId, string actorId)
        {
            if (!_dirty.TryGetValue(taskId, out var rows)) _dirty[taskId] = rows = new List<string>();
            rows.Add(actorId);
        }

        public List<string> DirtyRemaining(long taskId) =>
            new List<string>(_dirty.TryGetValue(taskId, out var rows) ? rows : new List<string>());

        /// <summary>只取"真人"那一半（脏行并进返回值会干扰其它判据 ⇒ 分离取证）。</summary>
        public Task<List<string>> FindRealActorsAsync(long taskId) => base.FindTaskActorsAsync(taskId);

        public override async Task<List<string>> FindTaskActorsAsync(long taskId)
        {
            var outList = new List<string>(await base.FindTaskActorsAsync(taskId));
            if (_dirty.TryGetValue(taskId, out var rows)) outList.AddRange(rows);
            return outList;
        }

        public override async Task RemoveTaskActorAsync(long taskId, List<string> actors)
        {
            RemoveCalls.Add(new List<string>(actors));
            if (_dirty.TryGetValue(taskId, out var rows))
                rows.RemoveAll(r => actors.Contains(r, StringComparer.Ordinal));   // DELETE ... IN 的字面语义
            await base.RemoveTaskActorAsync(taskId, actors);
        }
    }
}

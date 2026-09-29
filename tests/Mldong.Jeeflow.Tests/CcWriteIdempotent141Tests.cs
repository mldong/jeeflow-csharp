using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// 抄送写侧判重＝幂等空操作（issues/141 G2 · C# 栈，内存仓储一路 ＋ 引擎/门面三条入口腿）。
///
/// <para>立法逐字依据＝spec 06-facade.md §4「写侧判重＝幂等空操作（owner 2026-09-29 拍）」＋
/// spec 11.2 原则 1「码值表达发生了什么事实」。同一 <c>(实例, 被抄送人)</c> 已存在 cc 行时，
/// 再次抄送必须：①不新增行 ②不重置未读状态 ③不更新原行时间 ④<b>不 fire CC_CREATE（码 4）</b>。
/// 逐人 fire 的入参换成<b>实际新建的子集</b>（<see cref="IProcessRepository.CreateCcInstanceIfAbsentAsync"/>
/// 的返回值），子集为空整支不 fire（不空转、也不照旧全量 fire）。
/// 查询侧不引入 DISTINCT、历史重复行不清理（owner 明确接受既成事实），故这里只钉写侧。</para>
///
/// <para>判重义务覆盖三条入口（spec §11.7「同一支」）：发起 <c>f_ccActors</c>、办理
/// <c>tf_ccActors</c>（同走 <see cref="JeeflowEngine"/> 的 <c>HandleCcActorsAsync</c>）、门面手动
/// <c>processInstance/createCCInstance</c>。MySQL 一路见
/// <see cref="MySqlCcOwnershipIdempotent141Tests"/>，两仓必须给同一个答案
/// （issues/117 场景 27 那把尺子）。</para>
///
/// <para>取证强度：内存仓储的 cc 行带 <c>State/CreateTime/UpdateTime</c>（形状对齐
/// <c>wf_process_cc_instance</c> 表），②③两档直接读<b>行本体</b>而不是只读 actor id 集合——
/// 只看 id 照不出"重复抄送把已读抹回未读 / 把原行时间刷成 now"这两种假修。
/// 时间档另配一个会走的钟（<see cref="FixedClock.Now"/> 可写）：固定钟下两次写入取值相同，
/// "刷没刷时间"在断言上分不开，那是自欺。</para>
/// </summary>
public class CcWriteIdempotent141Tests
{
    /// <summary>start → approval(assignee=leader) → end：一条待办就够办理腿用。</summary>
    private const string OneTaskFlow = """
        {"name": "cc-dedup-141", "displayName": "抄送判重流程", "type": "approval",
         "nodes": [
           {"id": "start", "type": "snaker:start", "text": {"value": "开始"}},
           {"id": "approval", "type": "snaker:task", "text": {"value": "审批"},
            "properties": {"form": "leave-form", "assignee": "leader", "taskType": 0, "performType": 0}},
           {"id": "end", "type": "snaker:end", "text": {"value": "结束"}}
         ],
         "edges": [
           {"id": "e1", "sourceNodeId": "start", "targetNodeId": "approval"},
           {"id": "e2", "sourceNodeId": "approval", "targetNodeId": "end"}
         ]}
        """;

    private readonly Fixture _fx;

    public CcWriteIdempotent141Tests()
    {
        _fx = Fixture.New();
    }

    private sealed class Fixture
    {
        public JeeflowEngine Engine = null!;
        public MemoryRepository Repo = null!;
        public IProcessRepository SPI = null!;   // DIM 只能经接口调用（C# 语言规则），统一走这个口
        public ServiceContext Ctx = null!;
        public JeeflowFacade Facade = null!;
        public FixedClock Clock = null!;
        public CcCapture Capture = null!;

        public static Fixture New()
        {
            var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
            var clock = (FixedClock)ctx.Clock!;
            var capture = new CcCapture();
            ctx.RegisterEventListener(capture);
            return new Fixture
            {
                Engine = engine,
                Repo = repo,
                SPI = repo,
                Ctx = ctx,
                Facade = new JeeflowFacade(ctx),
                Clock = clock,
                Capture = capture,
            };
        }

        /// <summary>让钟走起来（②③档要能分辨"刷了时间"与"没刷时间"）。</summary>
        public void Tick(int seconds = 5) => Clock.Now = Clock.Now.AddSeconds(seconds);
    }

    /// <summary>事件 sink：只收 CC_CREATE，"第二次抄送没有新事件"就断在这里。</summary>
    private sealed class CcCapture : IProcessEventListener
    {
        public List<ProcessEvent> Events { get; } = new();
        public List<string> ActorIds => Events.Select(e => e.CcActorId ?? "").ToList();

        public Task OnEventAsync(ProcessEvent @event)
        {
            if (@event.EventType == ProcessEventType.CcCreate) Events.Add(@event);
            return Task.CompletedTask;
        }
    }

    // ── 夹具辅助 ──

    private async Task<long> StartInstanceAsync()
    {
        var did = await TestInfra.SaveFlowDefineAsync(_fx.Repo, "cc-dedup-141-" + _defineSeq++, OneTaskFlow);
        var inst = await _fx.Engine.StartProcessInstanceByIdAsync(did, "zhangsan", new FlowData());
        Assert.NotNull(inst.InstanceId);
        return inst.InstanceId!.Value;
    }

    private static int _defineSeq;

    private async Task ManualCcAsync(long instanceId, params string[] actorIds)
    {
        var resp = await _fx.Facade.FlowAsync("processInstance/createCCInstance", new FlowData
        {
            [FlowConst.ProcessInstanceIdKey] = instanceId,
            ["operator"] = "zhangsan",
            ["actorIds"] = actorIds.Select(a => (object?)a).ToList(),
        });
        Assert.True(Equals(0, resp["code"]), $"手动抄送应成功: {resp["msg"]}");
    }

    /// <summary>cc 行 actor id 列表（SPI 读侧，顺序＝建行序）。</summary>
    private Task<List<string>> CcActorIdsAsync(long instanceId) => _fx.Repo.FindCcActorIdsAsync(instanceId);

    /// <summary>内存仓储的 cc 行本体（②③档的取证面，不经任何转换）。</summary>
    private List<MemoryRepository.CcRow> CcRows(long instanceId) =>
        _fx.Repo.CcInstances.Values
            .Where(c => c.ProcessInstanceId == instanceId)
            .OrderBy(c => c.Id)
            .ToList();

    private MemoryRepository.CcRow? CcRow(long instanceId, string actorId) =>
        CcRows(instanceId).FirstOrDefault(r => r.ActorId == actorId);

    // ═══ 正向对照：全新的一次抄送照旧建行＋逐人 fire ═══

    [Fact]
    public async Task FirstCcStillCreatesRowsAndFiresPerActor()
    {
        var iid = await StartInstanceAsync();
        _fx.Capture.Events.Clear();
        _fx.Tick();

        await ManualCcAsync(iid, "6101", "6102");

        Assert.Equal(new List<string> { "6101", "6102" }, await CcActorIdsAsync(iid));  // 全新抄送逐人落行
        Assert.Equal(2, _fx.Capture.Events.Count);                                      // 逐人 fire CC_CREATE（码 4）
        Assert.Equal(new List<string> { "6101", "6102" }, _fx.Capture.ActorIds);        // ccActorId 顺序与入参一致
        Assert.All(_fx.Capture.Events, e => Assert.Equal(iid, e.SourceId!.Value));      // sourceId＝instanceId
        Assert.All(CcRows(iid), r => Assert.Equal(0, r.State));                         // 新行应是未读（state=0）
    }

    // ═══ 四档：重复抄送是幂等空操作 ═══

    /// <summary>①不新增行 ＋ ④不 fire 码 4：手动腿连发两次同一个人。</summary>
    [Fact]
    public async Task RepeatCcForSameActorAddsNoRowAndFiresNothing()
    {
        var iid = await StartInstanceAsync();
        await ManualCcAsync(iid, "6201");
        Assert.Equal(new List<string> { "6201" }, await CcActorIdsAsync(iid));   // 首次抄送落 1 行
        Assert.Equal(1, _fx.Capture.Events.Count);                               // 首次抄送 fire 1 次

        _fx.Capture.Events.Clear();
        _fx.Tick();
        await ManualCcAsync(iid, "6201");

        Assert.Equal(new List<string> { "6201" }, await CcActorIdsAsync(iid));   // ①重复抄送不得新增行
        Assert.Single(CcRows(iid));                                               // ①重复抄送后行数仍是 1
        Assert.Empty(_fx.Capture.Events);   // ④没发生创建就不得发码 4（spec 11.2 原则 1「码=事实」）
    }

    /// <summary>②不重置未读：先置已读，再重复抄送，state 必须仍是已读（也不许新插一条未读行）。</summary>
    [Fact]
    public async Task RepeatCcDoesNotResetUnreadState()
    {
        var iid = await StartInstanceAsync();
        await ManualCcAsync(iid, "6301");
        var read = await _fx.Facade.FlowAsync("processInstance/updateCCStatus", new FlowData
        {
            [FlowConst.ProcessInstanceIdKey] = iid,
            ["operator"] = "6301",
        });
        Assert.True(Equals(0, read["code"]), $"已读应成功: {read["msg"]}");
        Assert.Equal(1, CcRow(iid, "6301")!.State);                              // 置读后 state 应为 1

        _fx.Tick();
        await ManualCcAsync(iid, "6301");

        Assert.Single(CcRows(iid));                                               // ②的形态前提：没多插行
        Assert.Equal(1, CcRow(iid, "6301")!.State);                               // ②重复抄送不得把已读抹回未读
        Assert.DoesNotContain(CcRows(iid), r => r.State == 0);                    // 也不得以"新的未读行"绕过
    }

    /// <summary>③不更新原行时间：createTime 与 updateTime 逐字不变，且仍是同一行（没有删旧插新）。</summary>
    [Fact]
    public async Task RepeatCcDoesNotTouchOriginalRowTimes()
    {
        var iid = await StartInstanceAsync();
        await ManualCcAsync(iid, "6401");
        var before = CcRow(iid, "6401")!;
        Assert.NotNull(before.CreateTime);
        var (rowId, createTime, updateTime) = (before.Id, before.CreateTime, before.UpdateTime);

        _fx.Tick();   // 钟走 5 秒：若实现刷时间，读数必然与上面不同
        await ManualCcAsync(iid, "6401");

        var after = CcRow(iid, "6401")!;
        Assert.Single(CcRows(iid));                        // ③的形态前提：仍是那一行，没删旧插新
        Assert.Equal(rowId, after.Id);
        Assert.Equal(createTime, after.CreateTime);        // ③重复抄送不得刷新原行 CreateTime
        Assert.Equal(updateTime, after.UpdateTime);        // ③重复抄送不得刷新原行 UpdateTime
    }

    /// <summary>④的子集档：第二次同时给「已知人＋新人」⇒ 只为新人建行、只为新人 fire 一次。</summary>
    [Fact]
    public async Task RepeatCcFiresOnlyForNewlyCreatedSubset()
    {
        var iid = await StartInstanceAsync();
        await ManualCcAsync(iid, "6501", "6502");
        Assert.Equal(new List<string> { "6501", "6502" }, await CcActorIdsAsync(iid));   // 首轮 2 行
        Assert.Equal(2, _fx.Capture.Events.Count);                                        // 首轮 fire 2 次

        _fx.Capture.Events.Clear();
        _fx.Tick();
        await ManualCcAsync(iid, "6501", "6503");

        Assert.Equal(new List<string> { "6503" }, _fx.Capture.ActorIds);   // 逐人 fire 的入参应是实际新建的子集
        Assert.Single(_fx.Capture.Events);                                 // 子集只有 1 人 ⇒ 只 fire 1 次
        Assert.Equal(new List<string> { "6501", "6502", "6503" }, await CcActorIdsAsync(iid));
    }

    /// <summary>同一次调用里重复给同一个人 ⇒ 也按幂等处理（一行一次提醒）。</summary>
    [Fact]
    public async Task DuplicateWithinOneCallCollapses()
    {
        var iid = await StartInstanceAsync();
        _fx.Capture.Events.Clear();

        await ManualCcAsync(iid, "6601", "6601");

        Assert.Equal(new List<string> { "6601" }, await CcActorIdsAsync(iid));   // 不新增第二行
        Assert.Single(CcRows(iid));
        Assert.Equal(1, _fx.Capture.Events.Count);                                // 只 fire 一次
    }

    // ═══ 引擎腿（f_ccActors ／ tf_ccActors）同一条判据 ═══

    /// <summary>办理腿与发起腿重叠的那个人不得再建行、不得再 fire；新人照旧。</summary>
    [Fact]
    public async Task EngineCcLegsShareTheSameDedupRule()
    {
        var did = await TestInfra.SaveFlowDefineAsync(_fx.Repo, "cc-dedup-141-leg", OneTaskFlow);
        var inst = await _fx.Engine.StartProcessInstanceByIdAsync(did, "zhangsan",
            new FlowData { [FlowConst.CcActorsStart] = "7001" });
        var iid = inst.InstanceId!.Value;
        Assert.Equal(1, _fx.Capture.Events.Count);                                  // 发起腿 fire 1 次
        Assert.Equal(new List<string> { "7001" }, await CcActorIdsAsync(iid));      // 发起腿落 1 行

        _fx.Capture.Events.Clear();
        _fx.Tick();
        var approval = (await _fx.Repo.FindDoingTasksAsync(iid, null))[0];
        var resp = await _fx.Facade.FlowAsync("processTask/execute", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = approval.TaskId,
            ["operator"] = "leader",
            [FlowConst.CcActors] = "7001,7002",
        });
        Assert.True(Equals(0, resp["code"]), $"办理＋抄送应成功: {resp["msg"]}");

        Assert.Equal(new List<string> { "7001", "7002" }, await CcActorIdsAsync(iid));  // 只为新人 7002 建行
        Assert.Equal(new List<string> { "7002" }, _fx.Capture.ActorIds);                // 只 fire 实际新建的子集
    }

    /// <summary>
    /// 两形态入参（<c>Collection</c> 逐元素 / 逗号串 <c>string</c>）共用同一条判重腿：
    /// 发起腿给集合、办理腿给逗号串，重叠的人仍只有一行、只 fire 一次。
    /// </summary>
    [Fact]
    public async Task StringAndCollectionFormsShareTheDedupRule()
    {
        var did = await TestInfra.SaveFlowDefineAsync(_fx.Repo, "cc-dedup-141-forms", OneTaskFlow);
        var inst = await _fx.Engine.StartProcessInstanceByIdAsync(did, "zhangsan",
            new FlowData { [FlowConst.CcActorsStart] = new List<object?> { "7101", "7102" } });
        var iid = inst.InstanceId!.Value;
        Assert.Equal(new List<string> { "7101", "7102" }, await CcActorIdsAsync(iid));   // 集合形态逐人建行
        Assert.Equal(2, _fx.Capture.Events.Count);                                        // 集合形态逐人 fire

        _fx.Capture.Events.Clear();
        _fx.Tick();
        var approval = (await _fx.Repo.FindDoingTasksAsync(iid, null))[0];
        await _fx.Facade.FlowAsync("processTask/execute", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = approval.TaskId,
            ["operator"] = "leader",
            [FlowConst.CcActors] = "7101,7103",
        });

        Assert.Equal(new List<string> { "7101", "7102", "7103" }, await CcActorIdsAsync(iid));
        Assert.Equal(new List<string> { "7103" }, _fx.Capture.ActorIds);   // 两形态混用也只为新人 fire
    }

    /// <summary>
    /// 反向哨兵：判重不得把"没抄送过的人"也吃掉——不同实例上的同一个人各自建行。
    /// </summary>
    [Fact]
    public async Task DedupIsScopedToInstanceNotGlobal()
    {
        var first = await StartInstanceAsync();
        var second = await StartInstanceAsync();
        _fx.Capture.Events.Clear();

        await ManualCcAsync(first, "6701");
        _fx.Tick();
        await ManualCcAsync(second, "6701");

        Assert.Equal(new List<string> { "6701" }, await CcActorIdsAsync(first));    // 实例一有自己的 cc 行
        Assert.Equal(new List<string> { "6701" }, await CcActorIdsAsync(second));   // 实例二照样建行
        Assert.Equal(2, _fx.Capture.Events.Count);                                   // 两个实例各 fire 一次
        Assert.NotEqual(first, second);                                              // 判重作用域是按实例
    }

    // ═══ SPI 形状（对外影响的两条腿都要有证据）═══

    /// <summary>
    /// SPI 默认实现档（对外影响）：第三方仓储<b>不覆写</b> <see cref="IProcessRepository.FindCcActorIdsAsync"/>
    /// ⇒ 读侧恒空集 ⇒ <see cref="IProcessRepository.CreateCcInstanceIfAbsentAsync"/> 的默认实现退化成
    /// "每次都插"（只折叠同一次调用内的重复）——跨调用判重<b>吃不到</b>，码 4 也照旧全量 fire。
    /// 源码兼容不破（既有集成方不改一行也能编译），但 changelog 必须写明
    /// "自实现仓储要覆写 <c>FindCcActorIdsAsync</c> 才能得到 issues/141 G2 的幂等语义"，
    /// 本栈自带的两仓（内存 / MySQL）都已覆写。
    /// </summary>
    [Fact]
    public async Task ThirdPartyRepoWithoutOverrideKeepsLegacyBehavior()
    {
        var legacy = new LegacyCcRepo();
        IProcessRepository repo = legacy;

        var first = await repo.CreateCcInstanceIfAbsentAsync(4242L, "zhangsan", "8001", "8001", "8002");
        Assert.Equal(new List<string> { "8001", "8002" }, first);       // 只折叠同一次调用内的重复
        Assert.Empty(await repo.FindCcActorIdsAsync(4242L));            // default 读侧＝空集＝"谁都没抄送过"

        var second = await repo.CreateCcInstanceIfAbsentAsync(4242L, "zhangsan", "8001");
        Assert.Equal(new List<string> { "8001" }, second);              // 跨调用判重没生效
        Assert.Equal(new List<string> { "8001", "8002", "8001" }, legacy.Inserted);
    }

    /// <summary>SPI 读侧：自带内存仓储的 <c>FindCcActorIdsAsync</c> 反映真实行集（判重依据不能是内存猜测）。</summary>
    [Fact]
    public async Task FindCcActorIdsReadsTheRealRows()
    {
        var iid = await StartInstanceAsync();
        Assert.Empty(await _fx.SPI.FindCcActorIdsAsync(iid));                         // 空实例没有 cc 行

        await _fx.SPI.CreateCcInstanceIfAbsentAsync(iid, "zhangsan", "8601", "8602");
        Assert.Equal(new List<string> { "8601", "8602" }, await _fx.SPI.FindCcActorIdsAsync(iid));

        await _fx.SPI.CreateCcInstanceIfAbsentAsync(iid, "zhangsan", "8601", "8603");
        Assert.Equal(new List<string> { "8601", "8602", "8603" }, await _fx.SPI.FindCcActorIdsAsync(iid));
    }

    /// <summary>
    /// 门面手动腿的"子集为空整支不 fire"档：全部重复 ⇒ 一次事件都不发（不空转）。
    /// </summary>
    [Fact]
    public async Task AllDuplicateCallFiresNothingAtAll()
    {
        var iid = await StartInstanceAsync();
        await ManualCcAsync(iid, "6801", "6802");
        _fx.Capture.Events.Clear();
        _fx.Tick();

        await ManualCcAsync(iid, "6801", "6802");

        Assert.Empty(_fx.Capture.Events);                        // 子集为空 ⇒ 整支不发
        Assert.Equal(2, CcRows(iid).Count);                       // 行数也没动
        Assert.Equal(2, (await CcActorIdsAsync(iid)).Count);
    }

    /// <summary>只实现旧 <c>CreateCcInstanceAsync</c>（不覆写两个新方法）的第三方仓储桩。</summary>
    private sealed class LegacyCcRepo : IProcessRepository
    {
        public List<string> Inserted { get; } = new();

        public Task CreateCcInstanceAsync(long instanceId, string creator, params string[] actorIds)
        {
            foreach (var a in actorIds) Inserted.Add(a);
            return Task.CompletedTask;
        }

        public Task<ProcessDefine?> FindDefineByIdAsync(long? defineId) => Task.FromResult<ProcessDefine?>(null);
        public Task SaveDefineAsync(ProcessDefine define) => Task.CompletedTask;
        public Task UpdateDefineAsync(ProcessDefine define) => Task.CompletedTask;
        public Task UpdateDefineStateAsync(long defineId, int state) => Task.CompletedTask;
        public Task RemoveDefineAsync(long defineId) => Task.CompletedTask;
        public Task<ProcessInstance?> FindInstanceByIdAsync(long? instanceId) => Task.FromResult<ProcessInstance?>(null);
        public Task SaveInstanceAsync(ProcessInstance instance) => Task.CompletedTask;
        public Task UpdateInstanceAsync(ProcessInstance instance) => Task.CompletedTask;
        public Task<ProcessTask?> FindTaskByIdAsync(long? taskId) => Task.FromResult<ProcessTask?>(null);
        public Task SaveTaskAsync(ProcessTask task) => Task.CompletedTask;
        public Task UpdateTaskAsync(ProcessTask task) => Task.CompletedTask;
        public Task<List<ProcessTask>> FindDoingTasksAsync(long instanceId, string[]? taskNames) => Task.FromResult(new List<ProcessTask>());
        public Task<List<ProcessTask>> FindDoneTasksAsync(long instanceId, string[]? taskNames) => Task.FromResult(new List<ProcessTask>());
        public Task<List<ProcessTask>> FindHistoryTasksAsync(long instanceId) => Task.FromResult(new List<ProcessTask>());
        public Task UpdateCcStatusAsync(long instanceId, string actorId) => Task.CompletedTask;
        public Task<List<string>> FindTaskActorsAsync(long taskId) => Task.FromResult(new List<string>());
        public Task AddTaskActorAsync(long taskId, List<string> actors) => Task.CompletedTask;
        public Task RemoveTaskActorAsync(long taskId, List<string> actors) => Task.CompletedTask;
        public Task<PageResult<IProcessRepository.TaskRow>> PageTodoTasksAsync(PageQuery query) =>
            Task.FromResult(PageResult<IProcessRepository.TaskRow>.Of(1, 10, 0, new List<IProcessRepository.TaskRow>()));
        public Task<PageResult<IProcessRepository.TaskRow>> PageDoneTasksAsync(PageQuery query) =>
            Task.FromResult(PageResult<IProcessRepository.TaskRow>.Of(1, 10, 0, new List<IProcessRepository.TaskRow>()));
        public Task<PageResult<IProcessRepository.InstanceRow>> PageInstancesAsync(PageQuery query) =>
            Task.FromResult(PageResult<IProcessRepository.InstanceRow>.Of(1, 10, 0, new List<IProcessRepository.InstanceRow>()));
        public Task<PageResult<IProcessRepository.InstanceRow>> PageCcInstancesAsync(PageQuery query) =>
            Task.FromResult(PageResult<IProcessRepository.InstanceRow>.Of(1, 10, 0, new List<IProcessRepository.InstanceRow>()));
        public Task<PageResult<IProcessRepository.DefineRow>> PageDefinesAsync(PageQuery query) =>
            Task.FromResult(PageResult<IProcessRepository.DefineRow>.Of(1, 10, 0, new List<IProcessRepository.DefineRow>()));
        public Task<int> CountTodoTasksAsync(long? userId) => Task.FromResult(0);
        public Task<List<IProcessRepository.InstanceStatsRow>> QueryInstancesForStatsAsync(
            List<int>? stateIn, string timeField, DateTime? start, DateTime? end) =>
            Task.FromResult(new List<IProcessRepository.InstanceStatsRow>());
        public Task<List<IProcessRepository.TaskStatsRow>> QueryTasksForStatsAsync(
            int? state, DateTime? start, DateTime? end) =>
            Task.FromResult(new List<IProcessRepository.TaskStatsRow>());
        public Task<int> StatsAvgCompletedDurationSecondsAsync(DateTime? start, DateTime? end) => Task.FromResult(0);
        public Task<int[]> StatsPendingAndOverdueCountAsync() => Task.FromResult(new[] { 0, 0 });
        public Task<int[]> StatsCompletedTaskAggregateAsync() => Task.FromResult(new[] { 0, 0, 0, 0 });
        public Task<List<Dictionary<string, object?>>> StatsStuckNodeGroupAsync(int limit) =>
            Task.FromResult(new List<Dictionary<string, object?>>());
        public Task<List<Dictionary<string, object?>>> StatsStuckApproverGroupAsync(int limit) =>
            Task.FromResult(new List<Dictionary<string, object?>>());
        public Task<List<Dictionary<string, object?>>> StatsDefineGroupAsync(DateTime? start, DateTime? end, int limit) =>
            Task.FromResult(new List<Dictionary<string, object?>>());
        public Task<List<int>> StatsCompletedInstanceDurationsAsync(DateTime? start, DateTime? end) =>
            Task.FromResult(new List<int>());
    }
}

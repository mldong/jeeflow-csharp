using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// 空抄送人不建 cc 行（issues/141 G10 · owner 2026-09-29 拍「空不创建行」· C# 栈内存一路）。
///
/// <para>立法逐字依据＝spec 06-facade.md §2.10「空抄送人不建 cc 行」＋基准实现＝java <c>5fbd5ac</c>：
/// 三条入口（发起 <c>f_ccActors</c>／办理 <c>tf_ccActors</c>／门面手动 <c>createCCInstance</c>）
/// 解析抄送人集合时，<b>空串、纯空白、数组里的空元素一律丢弃</b>；丢完为空 ⇒
/// <b>不建任何 cc 行、也不 fire CC_CREATE（码 4）</b>。手动腿丢完为空时与本仓既有的
/// "空集合"档同判（<c>actorIds 缺失</c> 错误信封，实测 code=99999999），不新造错误码/文案。</para>
///
/// <para><b>本栈的旧形状与 java 是同一种病</b>（普查实测，见下方各格的"改前读数"注释）：
/// <c>"".Split(',')</c> 在 C# 里得到<b>一个空元素</b>而不是零个，且
/// <c>HandleCcActorsAsync</c> 用的是不带 <c>RemoveEmptyEntries</c> 的那一档 ⇒
/// 发起腿给 <c>""</c> 时 cc 行数=1、<c>ActorId=''</c>、码 4 fire 1 次；
/// <c>"a,,b"</c> ⇒ 3 行、fire 3 次。空归属值正是 issues/129 那族"空 operator 读全库"的病根，
/// 而且 G1 已把 <c>cc.actor_id</c> 的空值档判成"没填"⇒ 空页，这些空行连被查到的机会都没有，
/// 是纯脏数据。</para>
///
/// <para><b>两层都挡</b>（spec §2.10 四点要求①）：漏斗层（<see cref="JeeflowEngine"/> 的
/// <c>HandleCcActorsAsync</c> ＋ 门面 <c>ActionsMain.CreateCcInstanceAsync</c>）归一后判空；
/// 写侧层（<see cref="MemoryRepository.CreateCcInstanceAsync"/>／
/// <see cref="Mldong.Jeeflow.Repository.MySql.MySqlRepository.CreateCcInstanceAsync"/>／
/// <see cref="IProcessRepository.CreateCcInstanceIfAbsentAsync"/> 的接口默认实现）各自再挡一次——
/// 只修漏斗，绕过引擎/门面直连仓储的调用方照样灌空值。判据单点复用
/// <see cref="PageQuery.NormalizeCcActors"/>（G1 的 <see cref="PageQuery.HasEffectiveCondition"/>
/// 同一处、同一形状），两仓不各抄一份。</para>
///
/// <para><b>落库与比较一律取 trim 后的值</b>（要求②，与 G2 写侧判重咬合）：<c>" 123 "</c> 与
/// <c>"123"</c> 是同一个人。不 trim 会把 99485e5 那条判重打穿——普查实测先落 <c>" 9101 "</c>
/// 再抄 <c>"9101"</c> ⇒ 2 行（同一人两行、码 4 两次）。</para>
///
/// <para><b>反向哨兵</b>（要求④）：<c>"0"</c> 这类"看起来像空"的正常 id 不得被当空值丢掉。
/// MySQL 一路见 <see cref="MySqlCcOwnershipIdempotent141Tests"/> 的 G10 段，两仓必须同答案
/// （issues/117 场景 27 那把尺子）。</para>
/// </summary>
public class CcBlankActorDropped141Tests
{
    /// <summary>start → approval(assignee=leader) → end：一条待办就够办理腿用。</summary>
    private const string OneTaskFlow = """
        {"name": "cc-blank-141", "displayName": "空抄送人不建行", "type": "approval",
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

    private readonly JeeflowEngine _engine;
    private readonly MemoryRepository _repo;
    private readonly IProcessRepository _spi;   // 接口调度 ⇒ 打到 CreateCcInstanceIfAbsentAsync 的默认实现
    private readonly JeeflowFacade _facade;
    private readonly CcCapture _cap;
    private static int _defineSeq;

    public CcBlankActorDropped141Tests()
    {
        var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
        _cap = new CcCapture();
        ctx.RegisterEventListener(_cap);
        _engine = engine;
        _repo = repo;
        _spi = repo;
        _facade = new JeeflowFacade(ctx);
    }

    /// <summary>事件 sink：只收 CC_CREATE（码 4），"空白抄送人不 fire"就断在这里。</summary>
    private sealed class CcCapture : IProcessEventListener
    {
        public List<ProcessEvent> Events { get; } = new();
        public List<ProcessEvent> Cc => Events.Where(e => e.EventType == ProcessEventType.CcCreate).ToList();
        public List<string?> ActorIds => Cc.Select(e => e.CcActorId).ToList();

        public Task OnEventAsync(ProcessEvent @event)
        {
            if (@event.EventType == ProcessEventType.CcCreate) Events.Add(@event);
            return Task.CompletedTask;
        }
    }

    // ── 夹具辅助 ──

    private async Task<long> DefineAsync() =>
        await TestInfra.SaveFlowDefineAsync(_repo, "cc-blank-141-" + _defineSeq++, OneTaskFlow);

    /// <summary>发起腿：带 f_ccActors 发起，返回实例 id（过程中事件计数即发起腿的 fire 数）。</summary>
    private async Task<long> StartWithCcAsync(object? ccActors)
    {
        _cap.Events.Clear();
        var inst = await _engine.StartProcessInstanceByIdAsync(
            await DefineAsync(), "zhangsan", new FlowData { [FlowConst.CcActorsStart] = ccActors });
        return inst.InstanceId!.Value;
    }

    /// <summary>无抄送的干净实例（办理腿/手动腿用）。</summary>
    private async Task<long> StartPlainAsync() => await StartWithCcAsync(null);

    /// <summary>办理腿：给唯一待办办理并带 tf_ccActors。</summary>
    private async Task ExecuteWithCcAsync(long instanceId, object? ccActors)
    {
        var task = (await _repo.FindDoingTasksAsync(instanceId, null))[0];
        var resp = await _facade.FlowAsync("processTask/execute", new FlowData
        {
            [FlowConst.ProcessTaskIdKey] = task.TaskId,
            ["operator"] = "leader",
            [FlowConst.CcActors] = ccActors,
        });
        Assert.True(Equals(0, resp["code"]), $"办理＋抄送应成功: {resp["msg"]}");
    }

    /// <summary>手动腿原始返回（全空白档要看它是不是与"空集合"同档，不能假定成功）。</summary>
    private async Task<Dictionary<string, object?>> ManualCcRawAsync(long instanceId, params string[] actorIds) =>
        await _facade.FlowAsync("processInstance/createCCInstance", new FlowData
        {
            [FlowConst.ProcessInstanceIdKey] = instanceId,
            ["operator"] = "zhangsan",
            ["actorIds"] = actorIds.Select(a => (object?)a).ToList(),
        });

    private async Task ManualCcAsync(long instanceId, params string[] actorIds)
    {
        var resp = await ManualCcRawAsync(instanceId, actorIds);
        Assert.True(Equals(0, resp["code"]), $"手动抄送应成功: {resp["msg"]}");
    }

    private async Task<Dictionary<string, object?>> ManualCcStringAsync(long instanceId, string actorIds) =>
        await _facade.FlowAsync("processInstance/createCCInstance", new FlowData
        {
            [FlowConst.ProcessInstanceIdKey] = instanceId,
            ["operator"] = "zhangsan",
            ["actorIds"] = actorIds,
        });

    private Task<List<string>> CcActorIdsAsync(long instanceId) => _repo.FindCcActorIdsAsync(instanceId);

    /// <summary>cc 行本体计数（不经任何转换，直接数内存仓储的行）。</summary>
    private int CcRowCount(long instanceId) =>
        _repo.CcInstances.Values.Count(c => c.ProcessInstanceId == instanceId);

    // ═══ 正向对照：非空抄送人照旧建行＋逐人 fire（按设计改前改后都不红）═══

    [Fact]
    public async Task NonBlankCcActorsStillCreateRowsAndFire()
    {
        var iid = await StartPlainAsync();
        _cap.Events.Clear();

        await ManualCcAsync(iid, "7501", "7502");

        Assert.Equal(new List<string> { "7501", "7502" }, await CcActorIdsAsync(iid));   // 照旧逐人落行
        Assert.Equal(2, CcRowCount(iid));
        Assert.Equal(new List<string?> { "7501", "7502" }, _cap.ActorIds);               // 照旧逐人 fire 码 4
    }

    // ═══ 发起腿 f_ccActors ═══

    /// <summary>
    /// java 旧形状在这一栈的正面对手戏：C# 的 <c>"".Split(',')</c> 也得到一个空元素 ⇒
    /// 改前实测 cc 行数=1（<c>ActorId=''</c>）＋ fire 码 4 一次。按 G10 必须"丢完为空 ⇒ 不建行、不 fire"。
    /// </summary>
    [Fact]
    public async Task StartLegEmptyStringCreatesNoRow()
    {
        var iid = await StartWithCcAsync("");

        Assert.Equal(0, CcRowCount(iid));                    // G10：f_ccActors 给空串不得建 cc 行
        Assert.Empty(await CcActorIdsAsync(iid));            // 连空 actor id 的行都不能有
        Assert.Empty(_cap.Cc);                               // G10：也不得 fire 码 4
    }

    /// <summary>纯空白串与空串同档（改前实测落 1 行 <c>ActorId='   '</c>＋fire 一次）。</summary>
    [Fact]
    public async Task StartLegWhitespaceOnlyCreatesNoRow()
    {
        var iid = await StartWithCcAsync("   ");

        Assert.Equal(0, CcRowCount(iid));
        Assert.Empty(await CcActorIdsAsync(iid));
        Assert.Empty(_cap.Cc);
    }

    /// <summary>逗号串里的空元素（<c>"a,,b"</c>）丢弃，两个人照旧（改前实测 3 行、fire 3 次）。</summary>
    [Fact]
    public async Task StartLegCommaStringDropsEmptyElement()
    {
        var iid = await StartWithCcAsync("7701,,7702");

        Assert.Equal(new List<string> { "7701", "7702" }, await CcActorIdsAsync(iid));
        Assert.Equal(2, CcRowCount(iid));                                     // 只有两行
        Assert.Equal(new List<string?> { "7701", "7702" }, _cap.ActorIds);    // 逐有效人 fire
    }

    /// <summary>尾随逗号（<c>"7751,"</c>）不得建空行（改前实测 2 行，第二行 <c>ActorId=''</c>）。</summary>
    [Fact]
    public async Task StartLegTrailingCommaDropsEmptyElement()
    {
        var iid = await StartWithCcAsync("7751,");

        Assert.Equal(new List<string> { "7751" }, await CcActorIdsAsync(iid));
        Assert.Equal(1, CcRowCount(iid));
        Assert.Equal(new List<string?> { "7751" }, _cap.ActorIds);
    }

    /// <summary>数组形态给空元素 ⇒ 与逗号串同判据（只修串腿漏修数组腿＝本条要抓的形状）。</summary>
    [Fact]
    public async Task StartLegCollectionDropsBlankElements()
    {
        var iid = await StartWithCcAsync(new List<object?> { "7801", "", "  " });

        Assert.Equal(new List<string> { "7801" }, await CcActorIdsAsync(iid));
        Assert.Equal(1, CcRowCount(iid));
        Assert.Equal(new List<string?> { "7801" }, _cap.ActorIds);
    }

    /// <summary>
    /// spec §2.10「逗号串与数组两种形态在这条上必须同判据，别只修一条腿」：
    /// 同一批人给成串形态与数组形态，落库行与 fire 序列逐字相同。
    /// </summary>
    [Fact]
    public async Task StringAndCollectionFormsAgreeOnBlankElements()
    {
        var byString = await StartWithCcAsync("7851,,7852");
        var stringIds = await CcActorIdsAsync(byString);
        var stringFires = _cap.ActorIds;

        var byCollection = await StartWithCcAsync(new List<object?> { "7851", "", "   ", "7852" });

        Assert.Equal(stringIds, await CcActorIdsAsync(byCollection));   // 两形态落库行同答案
        Assert.Equal(stringFires, _cap.ActorIds);                        // 两形态 fire 序列同答案
        Assert.Equal(new List<string?> { "7851", "7852" }, _cap.ActorIds);
        Assert.Equal(CcRowCount(byString), CcRowCount(byCollection));
    }

    // ═══ 办理腿 tf_ccActors ═══

    /// <summary>办理腿给纯空白串 ⇒ 不建行、不 fire（改前实测 1 行 <c>ActorId='   '</c>＋fire 一次）。</summary>
    [Fact]
    public async Task ExecuteLegBlankStringCreatesNoRow()
    {
        var iid = await StartPlainAsync();
        _cap.Events.Clear();

        await ExecuteWithCcAsync(iid, "   ");

        Assert.Equal(0, CcRowCount(iid));
        Assert.Empty(await CcActorIdsAsync(iid));
        Assert.Empty(_cap.Cc);
    }

    /// <summary>办理腿尾随逗号 ⇒ 只丢空的，有效的人照旧（改前实测多落一行 <c>ActorId=''</c> 且多 fire 一次）。</summary>
    [Fact]
    public async Task ExecuteLegTrailingCommaDropsEmptyElement()
    {
        var iid = await StartPlainAsync();
        _cap.Events.Clear();

        await ExecuteWithCcAsync(iid, "8201,");

        Assert.Equal(new List<string> { "8201" }, await CcActorIdsAsync(iid));
        Assert.Equal(1, CcRowCount(iid));
        Assert.Equal(new List<string?> { "8201" }, _cap.ActorIds);
    }

    // ═══ 门面手动腿 createCCInstance ═══

    /// <summary>
    /// 全空白集合 ⇒ 与既有的"空集合"档同判（spec §2.10 要求③：沿用 <c>actorIds 缺失</c>
    /// 错误信封，不新造错误码/文案）＋ 零行 ＋ 零 fire。
    /// 改前实测：code=0（当成功）、真落两行（<c>''</c> 与 <c>'   '</c>）、fire 码 4 两次。
    /// </summary>
    [Fact]
    public async Task ManualLegAllBlankIsTheSameArmAsEmptyCollection()
    {
        var iid = await StartPlainAsync();
        _cap.Events.Clear();

        var allBlank = await ManualCcRawAsync(iid, "", "   ");
        var emptyCollection = await _facade.FlowAsync("processInstance/createCCInstance", new FlowData
        {
            [FlowConst.ProcessInstanceIdKey] = iid,
            ["operator"] = "zhangsan",
            ["actorIds"] = new List<object?>(),
        });

        Assert.Equal(99999999, allBlank["code"]);                       // 与"空集合"同档 ⇒ 非 0
        Assert.Equal(emptyCollection["code"], allBlank["code"]);        // 同一条错误码
        Assert.Equal(emptyCollection["msg"], allBlank["msg"]);          // 同一条文案（actorIds 缺失）
        Assert.Equal("actorIds 缺失", allBlank["msg"]);
        Assert.Equal(0, CcRowCount(iid));                               // 一行都不许有
        Assert.Empty(await CcActorIdsAsync(iid));
        Assert.Empty(_cap.Cc);                                          // 也不 fire 码 4
    }

    /// <summary>
    /// 手动腿的逗号串形态：本仓（与 java 基准 <c>5fbd5ac</c> 同形状）对非集合入参一律走
    /// "actorIds 缺失"档 ⇒ 空串同样不建行。这条钉的是"两形态同判据"里串那条腿不被漏掉。
    /// </summary>
    [Fact]
    public async Task ManualLegBlankCommaStringAlsoCreatesNoRow()
    {
        var iid = await StartPlainAsync();
        _cap.Events.Clear();

        var resp = await ManualCcStringAsync(iid, "");

        Assert.Equal(99999999, resp["code"]);
        Assert.Equal("actorIds 缺失", resp["msg"]);
        Assert.Equal(0, CcRowCount(iid));
        Assert.Empty(_cap.Cc);
    }

    /// <summary>混着给 ⇒ 只丢空元素，有效的人照旧建行＋fire（改前实测 4 行、fire 4 次）。</summary>
    [Fact]
    public async Task ManualLegDropsBlankElementsKeepsValidOnes()
    {
        var iid = await StartPlainAsync();
        _cap.Events.Clear();

        await ManualCcAsync(iid, "7601", "", "  ", "7602");

        Assert.Equal(new List<string> { "7601", "7602" }, await CcActorIdsAsync(iid));
        Assert.Equal(2, CcRowCount(iid));                                     // 不得落出 ActorId='' 的行
        Assert.Equal(new List<string?> { "7601", "7602" }, _cap.ActorIds);     // fire 入参只含有效的人
    }

    // ═══ trim 后同值＝同一个人（与 G2 判重咬合）═══

    /// <summary>落库值取 trim 后的串：带空格的人与不带空格的人是同一个人（改前实测原样落 <c>' 8301 '</c>）。</summary>
    [Fact]
    public async Task CcActorValuesAreTrimmed()
    {
        var iid = await StartPlainAsync();

        await ManualCcAsync(iid, " 8301 ", "8302");

        Assert.Equal(new List<string> { "8301", "8302" }, await CcActorIdsAsync(iid));
        Assert.Equal(2, CcRowCount(iid));
    }

    /// <summary>
    /// 先抄 <c>"8401"</c> 再抄 <c>" 8401 "</c> ⇒ 判重命中，仍是 1 行、0 新 fire。
    /// 这一格专门钉"trim 与 99485e5 的写侧判重咬合"：不 trim ⇒ 同一人落两行（普查实测 2 行）。
    /// </summary>
    [Fact]
    public async Task PaddedValueHitsTheDedupRule()
    {
        var iid = await StartPlainAsync();
        await ManualCcAsync(iid, "8401");
        _cap.Events.Clear();

        await ManualCcAsync(iid, " 8401 ");

        Assert.Equal(new List<string> { "8401" }, await CcActorIdsAsync(iid));   // 不得再建第二行
        Assert.Equal(1, CcRowCount(iid));
        Assert.Empty(_cap.Cc);                                                    // 判重命中 ⇒ 不 fire 码 4
    }

    // ═══ 写侧兜底：绕过引擎/门面直连仓储也建不出空行 ═══

    /// <summary>
    /// 漏斗修了但写侧没修 ⇒ 直连仓储照样灌空值；本条钉 spec §2.10 要求①里的第二层。
    /// 改前实测：内存仓直连 <c>("", "   ", null, "8501")</c> 落 3 行（含 <c>''</c> 与 <c>'   '</c>）。
    /// </summary>
    [Fact]
    public async Task MemoryRepoWritePathAlsoDropsBlankActors()
    {
        var iid = await StartPlainAsync();

        await _repo.CreateCcInstanceAsync(iid, "zhangsan",
            new string?[] { "", "   ", null, "8501" }.Select(x => x!).ToArray());

        Assert.Equal(new List<string> { "8501" }, await CcActorIdsAsync(iid));
        Assert.Equal(1, CcRowCount(iid));                       // 只落那一行
    }

    /// <summary>MySQL 一路的同判据哨兵：内存仓与 SQL 仓在"直连写侧"上必须同答案（这里钉内存侧）。</summary>
    [Fact]
    public async Task MemoryRepoWritePathTrimsTheStoredValue()
    {
        var iid = await StartPlainAsync();

        await _repo.CreateCcInstanceAsync(iid, "zhangsan", " 8551 ");

        Assert.Equal(new List<string> { "8551" }, await CcActorIdsAsync(iid));   // 落库值＝trim 后的串
        Assert.Equal(1, _repo.CcInstances.Values.Count(c => c.ProcessInstanceId == iid && c.ActorId == "8551"));
    }

    /// <summary>
    /// SPI 接口默认实现档（第三方仓储不覆写 <c>CreateCcInstanceIfAbsentAsync</c> 时打的就是这一支）：
    /// 返回的子集也不得含空值——子集是直接拿去 fire 码 4 的。
    /// 改前实测：入参 <c>("", "8601", "  ", " 8602 ")</c> ⇒ 子集原样带回 4 个（含空串与纯空白）。
    /// </summary>
    [Fact]
    public async Task IfAbsentSubsetExcludesBlankActors()
    {
        var iid = await StartPlainAsync();

        var created = await _spi.CreateCcInstanceIfAbsentAsync(iid, "zhangsan",
            new string?[] { "", "8601", "  ", " 8602 " }.Select(x => x!).ToArray());

        Assert.Equal(new List<string> { "8601", "8602" }, created);              // 子集只含有效且 trim 后的人
        Assert.Equal(new List<string> { "8601", "8602" }, await CcActorIdsAsync(iid));   // 子集与落库行一致
        Assert.Equal(2, CcRowCount(iid));
    }

    // ═══ 反向哨兵（spec §2.10 要求④）：判据只吃空值，不吃"看起来像空"的正常 id ═══

    [Fact]
    public async Task NormalActorIdsAreNotMistakenForBlank()
    {
        var iid = await StartPlainAsync();
        _cap.Events.Clear();

        await ManualCcAsync(iid, "0", "user-1");

        Assert.Equal(new List<string> { "0", "user-1" }, await CcActorIdsAsync(iid));   // "0" 不得被吃掉
        Assert.Equal(2, CcRowCount(iid));
        Assert.Equal(new List<string?> { "0", "user-1" }, _cap.ActorIds);               // 照旧逐人 fire
    }

    /// <summary>发起腿的反向哨兵：逗号串里的 <c>"0"</c> 也照常落行（三形态各一枚）。</summary>
    [Fact]
    public async Task ZeroValuedActorSurvivesTheCommaStringLeg()
    {
        var iid = await StartWithCcAsync("0,,0 ");

        Assert.Equal(new List<string> { "0" }, await CcActorIdsAsync(iid));
        Assert.Equal(1, CcRowCount(iid));
        Assert.Single(_cap.Cc);
    }

    // ═══ 漏斗层单独可观测档（spec §2.10 要求①的第一层）═══
    // 自带两仓的写侧都归一 ⇒ 光还原漏斗会被写侧兜住，观测面上分不开。这两格换一枚
    // "第三方自实现 CreateCcInstanceIfAbsentAsync、自己不做归一"的仓储（照旧全量插，
    // 等价 java 侧未覆写 default 的集成方），此时<b>漏斗是唯一防线</b>——
    // 断言的是"引擎/门面递到仓储手里的东西就已经是干净且 trim 过的"。

    /// <summary>
    /// 第三方风格仓储：自实现 IfAbsent、不归一、把拿到的原始入参录下来。
    /// （基类声明过接口时 DIM 仍归基类映射，所以这里<b>重新声明 </b><c>: IProcessRepository</c>，
    /// 让本类的成员参与接口映射——C# DIM 的语言规则，等价 java 侧"实现类自己覆盖 default"。）
    /// </summary>
    private sealed class SelfImplementedIfAbsentRepo : MemoryRepository, IProcessRepository
    {
        public List<string?> HandedByFunnel { get; } = new();

        public SelfImplementedIfAbsentRepo() : base(null) { }

        public Task<List<string>> CreateCcInstanceIfAbsentAsync(
            long instanceId, string creator, params string[] actorIds)
        {
            HandedByFunnel.AddRange(actorIds);
            return Task.FromResult(actorIds.ToList());   // 旧式"照旧全量插、全量返回"，不做 G10 归一
        }
    }

    /// <summary>发起/办理两腿经引擎漏斗递到仓储的入参已归一（空元素不进仓储）。</summary>
    [Fact]
    public async Task EngineFunnelNormalizesBeforeAskingTheRepository()
    {
        var repo = new SelfImplementedIfAbsentRepo();
        var ctx = TestInfra.NewContext(repo);
        repo.Configure(ctx);
        var engine = new JeeflowEngine(ctx);
        var did = await TestInfra.SaveFlowDefineAsync(repo, "cc-blank-141-funnel", OneTaskFlow);

        var inst = await engine.StartProcessInstanceByIdAsync(did, "zhangsan",
            new FlowData { [FlowConst.CcActorsStart] = "9001, ,9002," });
        var iid = inst.InstanceId!.Value;
        Assert.Equal(new List<string?> { "9001", "9002" }, repo.HandedByFunnel);   // 漏斗给仓储的就是干净值

        repo.HandedByFunnel.Clear();
        var task = (await repo.FindDoingTasksAsync(iid, null))[0];
        await engine.ExecuteProcessTaskAsync(task.TaskId!.Value, "leader",
            new FlowData { [FlowConst.CcActors] = new List<object?> { "9003", "", "  " } });
        Assert.Equal(new List<string?> { "9003" }, repo.HandedByFunnel);           // 数组形态同一条腿
    }

    /// <summary>门面手动腿经漏斗递到仓储的入参已归一（同上，另一条入口腿）。</summary>
    [Fact]
    public async Task FacadeManualLegNormalizesBeforeAskingTheRepository()
    {
        var repo = new SelfImplementedIfAbsentRepo();
        var ctx = TestInfra.NewContext(repo);
        repo.Configure(ctx);
        var engine = new JeeflowEngine(ctx);
        var facade = new JeeflowFacade(ctx);
        var did = await TestInfra.SaveFlowDefineAsync(repo, "cc-blank-141-manual-funnel", OneTaskFlow);
        var iid = (await engine.StartProcessInstanceByIdAsync(did, "zhangsan", new FlowData())).InstanceId!.Value;

        var resp = await facade.FlowAsync("processInstance/createCCInstance", new FlowData
        {
            [FlowConst.ProcessInstanceIdKey] = iid,
            ["operator"] = "zhangsan",
            ["actorIds"] = new List<object?> { "9051", "", "  ", " 9052 " },
        });

        Assert.True(Equals(0, resp["code"]), $"手动抄送应成功: {resp["msg"]}");
        Assert.Equal(new List<string?> { "9051", "9052" }, repo.HandedByFunnel);   // trim 也在漏斗这一层做完
    }
}

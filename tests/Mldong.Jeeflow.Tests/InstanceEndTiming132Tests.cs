using Xunit;
using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// 码 2 <c>PROCESS_INSTANCE_END</c> 的<b>落库时机</b>回归（R2-1；spec §11.2 原则 3／§11.3 码 2
/// 「实例 state 落库为 20/45 这类"走到终点"的状态<b>之后</b>」／08-compliance 场景 32）。
/// 形状逐字移植 Java 参考实现的 <c>InstanceEndEventTimingTest</c>。
///
/// <para>要还的债：<c>EndProcessHandler</c> 曾在 <c>instance.Finish()/Reject()</c> 之后<b>立刻</b> fire
/// ——那一刻只是内存聚合根改了 state，实例那一行要等引擎 <c>PersistTasksAsync</c>／发起路径的
/// <c>UpdateInstanceAsync</c> 才落库。监听器（站内信反查、待办角标、persist 回写）在回调当下反查
/// 实例读到的是旧 state，与 issues/121／issues/126 两轮"回写序"同族。</para>
///
/// <para><b>判据形状</b>：recorder 在<b>回调那一刻</b>用仓储反查实例，读到的 <c>state</c> 必须已经
/// 等于事件载荷里的 <c>state</c>（fire-before-persist 的旧形状在这条断言下必红：那一刻那一行还是 10）。</para>
///
/// <para><b>为什么自带 <see cref="WriteOrderRepository"/> 而不是裸用 <see cref="MemoryRepository"/></b>：
/// 时机判据要的是"SELECT 语义"——读到的是<b>最后一次写进去的值</b>，不是内存聚合根正在被改成什么。
/// 本栈内存仓的 <c>FindInstanceByIdAsync</c> 已经是这个形状（返回落库行的 <c>CloneInstance</c> 副本，
/// 与 java 侧 <c>MemoryProcessRepository</c> 的<b>活引用</b>语义相反，故 java 必须新造探针仓）；
/// 这里再包一层是为了把<b>写库次数</b>也记下来——"那一行一次都没被写过就播了"是另一种红法，
/// 光比 state 值照不出（父实例那支的历史缺口正是"state 从没写过去"）。</para>
///
/// <para>⚠️ 与 <see cref="EventCodes132Tests"/> 的分工：那边是<b>序</b>的 recorder（码表、可见序
/// [1,3,5,2]、逐支载荷键、异常隔离），对码 2 的写序<b>失明</b>——它的探针在 ProcessInstanceEnd 一支
/// 不做反查断言。本案的时机判据只在本文件钉，两处各管一件事，不要合并。</para>
/// </summary>
public class InstanceEndTiming132Tests
{
    // ═══ 夹具：三张流程图（与 java InstanceEndEventTimingTest 同形）═══

    /// <summary>单任务流：start → approval(leader) → end。</summary>
    private const string OneTaskFlow = """
        {"name":"one-task","displayName":"终态时机流程","type":"approval","nodes":[
          {"id":"start","type":"snaker:start","x":100,"y":200,"properties":{},"text":{"value":"开始"}},
          {"id":"approval","type":"snaker:task","x":300,"y":200,"properties":{"form":"leave-form",
            "assignee":"leader","taskType":0,"performType":0},"text":{"value":"审批"}},
          {"id":"end","type":"snaker:end","x":500,"y":200,"properties":{},"text":{"value":"结束"}}],
         "edges":[
          {"id":"e1","sourceNodeId":"start","targetNodeId":"approval","properties":{}},
          {"id":"e2","sourceNodeId":"approval","targetNodeId":"end","properties":{}}]}
        """;

    /// <summary>无待办短流：start → end —— 覆盖<b>发起路径</b>的 flush 收口。</summary>
    private const string DirectEndFlow = """
        {"name":"direct-end","displayName":"发起即办结","type":"approval","nodes":[
          {"id":"start","type":"snaker:start","x":100,"y":200,"properties":{},"text":{"value":"开始"}},
          {"id":"end","type":"snaker:end","x":300,"y":200,"properties":{},"text":{"value":"结束"}}],
         "edges":[{"id":"e1","sourceNodeId":"start","targetNodeId":"end","properties":{}}]}
        """;

    /// <summary>父流程：start → approval(leader) → <b>subprocess</b> → end —— 子办结后级联走这一段。</summary>
    private const string ParentFlowWithSubprocess = """
        {"name":"parent-sub","displayName":"父流程(含子流程)","type":"approval","nodes":[
          {"id":"start","type":"snaker:start","x":100,"y":200,"properties":{},"text":{"value":"开始"}},
          {"id":"approval","type":"snaker:task","x":260,"y":200,"properties":{"assignee":"leader",
            "taskType":0,"performType":0},"text":{"value":"父审批"}},
          {"id":"subprocess","type":"snaker:subProcess","x":420,"y":200,"properties":{},"text":{"value":"子流程"}},
          {"id":"end","type":"snaker:end","x":580,"y":200,"properties":{},"text":{"value":"结束"}}],
         "edges":[
          {"id":"e1","sourceNodeId":"start","targetNodeId":"approval","properties":{}},
          {"id":"e2","sourceNodeId":"approval","targetNodeId":"subprocess","properties":{}},
          {"id":"e3","sourceNodeId":"subprocess","targetNodeId":"end","properties":{}}]}
        """;

    /// <summary>子流程：start → childTask(leader) → end —— 它办结时把父实例一路推到 end。</summary>
    private const string ChildFlow = """
        {"name":"child","displayName":"子流程","type":"approval","nodes":[
          {"id":"start","type":"snaker:start","x":100,"y":200,"properties":{},"text":{"value":"开始"}},
          {"id":"childTask","type":"snaker:task","x":300,"y":200,"properties":{"assignee":"leader",
            "taskType":0,"performType":0},"text":{"value":"子审批"}},
          {"id":"end","type":"snaker:end","x":500,"y":200,"properties":{},"text":{"value":"结束"}}],
         "edges":[
          {"id":"e1","sourceNodeId":"childTask","targetNodeId":"end","properties":{}},
          {"id":"e0","sourceNodeId":"start","targetNodeId":"childTask","properties":{}}]}
        """;

    private static (JeeflowEngine Engine, WriteOrderRepository Repo, TimingRecorder Rec) NewStack()
    {
        var (engine, repo, rec, _) = NewTxStack();
        return (engine, repo, rec);
    }

    /// <summary>同 <see cref="NewStack"/>，额外挂一个可观测的事务模板（③ 组「回滚不播」判据用）。</summary>
    private static (JeeflowEngine Engine, WriteOrderRepository Repo, TimingRecorder Rec, AbortTrackingTx Tx) NewTxStack()
    {
        var repo = new WriteOrderRepository();
        var ctx = TestInfra.NewContext(repo);
        repo.Configure(ctx);
        var rec = new TimingRecorder(repo);
        ctx.RegisterEventListener(rec);
        var tx = new AbortTrackingTx();
        ctx.TransactionTemplate = tx;
        return (new JeeflowEngine(ctx), repo, rec, tx);
    }

    private static Task<long> AddDefineAsync(WriteOrderRepository repo, string json) =>
        TestInfra.SaveFlowDefineAsync(repo, "end-timing-" + Guid.NewGuid().ToString("N"), json);

    /// <summary>取实例行<b>落库后</b>的 state（独立证据，不经聚合根内存值）。</summary>
    private static async Task<int?> RowStateAsync(WriteOrderRepository repo, long id) =>
        (await repo.FindInstanceByIdAsync(id))?.State;

    private static async Task<long> DoingTaskIdAsync(WriteOrderRepository repo, long instanceId)
    {
        var doing = await repo.FindDoingTasksAsync(instanceId, null);
        Assert.NotEmpty(doing);   // 夹具前提：应有进行中任务
        return doing[0].TaskId!.Value;
    }

    // ═══════════════════════════════════════════════════════════════════
    // ① 正常办理路径：办结 20 ／ 拒绝 45
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>办结：码 2 排在实例行 <c>UpdateInstanceAsync(20)</c> <b>之后</b>，且可见序不被打散。</summary>
    [Fact]
    public async Task Finish_EndEvent_Fires_After_InstanceRowIsWritten()
    {
        var (engine, repo, rec) = NewStack();
        var did = await AddDefineAsync(repo, OneTaskFlow);
        var inst = await engine.StartProcessInstanceByIdAsync(did, "user1", new FlowData());
        var iid = inst.InstanceId!.Value;
        var taskId = await DoingTaskIdAsync(repo, iid);
        rec.Clear();

        await engine.ExecuteProcessTaskAsync(taskId, "leader",
            new FlowData { [FlowConst.SubmitType] = (int)WfSubmitType.Agree });

        Assert.Equal(new[] { "TaskComplete", "ProcessInstanceEnd" }, rec.Names);
        Assert.Equal((int)WfInstanceState.Finished, await RowStateAsync(repo, iid));
        AssertEndFiresAfterRowWritten(rec.FirstOf("ProcessInstanceEnd"), iid, (int)WfInstanceState.Finished);
    }

    /// <summary>拒绝：同一支码 2（规范名不拆，§11.6），载荷 45 ＋ 回调时那一行也已是 45。</summary>
    [Fact]
    public async Task Reject_EndEvent_Fires_After_RejectedRowIsWritten()
    {
        var (engine, repo, rec) = NewStack();
        var did = await AddDefineAsync(repo, OneTaskFlow);
        var inst = await engine.StartProcessInstanceByIdAsync(did, "user1", new FlowData());
        var iid = inst.InstanceId!.Value;
        var taskId = await DoingTaskIdAsync(repo, iid);
        rec.Clear();

        await engine.ExecuteProcessTaskAsync(taskId, "leader",
            new FlowData { [FlowConst.SubmitType] = (int)WfSubmitType.Reject });

        Assert.Equal(new[] { "TaskReject", "ProcessInstanceEnd" }, rec.Names);
        Assert.Equal((int)WfInstanceState.Reject, await RowStateAsync(repo, iid));
        AssertEndFiresAfterRowWritten(rec.FirstOf("ProcessInstanceEnd"), iid, (int)WfInstanceState.Reject);
    }

    /// <summary>发起即办结的短流：码 2 排在<b>发起路径</b>那次 <c>UpdateInstanceAsync</c> 之后（第二个 flush 点）。</summary>
    [Fact]
    public async Task StartStraightToEnd_EndEvent_Fires_After_RowWritten()
    {
        var (engine, repo, rec) = NewStack();
        var did = await AddDefineAsync(repo, DirectEndFlow);

        var inst = await engine.StartProcessInstanceByIdAsync(did, "user1", new FlowData());
        var iid = inst.InstanceId!.Value;

        Assert.Equal(new[] { "ProcessInstanceStart", "ProcessInstanceEnd" }, rec.Names);
        Assert.Equal((int)WfInstanceState.Finished, await RowStateAsync(repo, iid));
        AssertEndFiresAfterRowWritten(rec.FirstOf("ProcessInstanceEnd"), iid, (int)WfInstanceState.Finished);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ② 子流程父实例路径（第一轮点名"不能简单挪位"的那一支）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 子实例办结 → 级联把父实例也推到 end ⇒ <b>两支</b>码 2，且父实例那一支同样排在
    /// <b>父实例行落库之后</b>。
    ///
    /// <para>历史缺口有两处，本用例一并钉住：
    /// ① 父实例的终态曾"顺路"在级联里就地 fire，那一刻父实例那一行根本没被写过——本栈
    ///    <c>EndProcessHandler</c> 的 parent 分支只把父实例的新任务 <c>AddTasks</c> 上收给子 execution，
    ///    <c>PersistTasksAsync</c> 的 <c>UpdateInstanceAsync</c> 只写<b>子</b>实例那一行
    ///    ⇒ <b>父实例终态永远停在 10</b>（java 同缺陷，本轮一并修）；
    /// ② 把 fire 简单挪到子流程那次 updateInstance 之后，会把父实例这一支<b>整支丢掉</b>
    ///    （登记挂在级联的临时 execution 上，随对象丢弃）。
    /// 现在 flush 对"不是本次 execution own 的实例"先补写再播，登记随 <c>AddPendingEnds</c> 上收。</para>
    ///
    /// <para>夹具说明：本栈 <c>StartSubProcessHandler</c> 以 <c>defineId=null</c> 调引擎（源码里就标着
    /// "简化处理"，与 java 同），正向"父 → 子"起不来 ⇒ 本用例按级联的反方向直造子实例
    /// （<c>StartProcessInstanceByIdAsync(childDid, op, args, parentId, "subprocess")</c>，
    /// 正是那个处理器本该做的事），要照的"子办结 → 父级联"这一段与生产同形。</para>
    /// </summary>
    [Fact]
    public async Task SubProcess_ParentEndEvent_FiresOnce_After_ParentRowIsWritten()
    {
        var (engine, repo, rec) = NewStack();
        var parentDid = await AddDefineAsync(repo, ParentFlowWithSubprocess);
        var childDid = await AddDefineAsync(repo, ChildFlow);

        var parent = await engine.StartProcessInstanceByIdAsync(parentDid, "user1", new FlowData());
        var parentId = parent.InstanceId!.Value;
        var child = await engine.StartProcessInstanceByIdAsync(
            childDid, "user1", new FlowData(), parentId, "subprocess");
        var childId = child.InstanceId!.Value;
        var childTaskId = await DoingTaskIdAsync(repo, childId);
        rec.Clear();

        await engine.ExecuteProcessTaskAsync(childTaskId, "leader",
            new FlowData { [FlowConst.SubmitType] = (int)WfSubmitType.Agree });

        Assert.Equal(new[] { "TaskComplete", "ProcessInstanceEnd", "ProcessInstanceEnd" }, rec.Names);

        var ends = rec.AllOf("ProcessInstanceEnd");
        Assert.Equal(2, ends.Count);
        Assert.Equal(new[] { childId, parentId },
            ends.Select(e => e.Event.SourceId!.Value).ToArray());   // 先子后父（登记顺序＝级联顺序）

        AssertEndFiresAfterRowWritten(ends[0], childId, (int)WfInstanceState.Finished);
        AssertEndFiresAfterRowWritten(ends[1], parentId, (int)WfInstanceState.Finished);

        // 父实例的终态不再"只改内存"：行真的落到 20（此前 SQL 语义下永远停在 10）
        Assert.Equal((int)WfInstanceState.Finished, await RowStateAsync(repo, parentId));
        Assert.Single(ends, e => e.Event.SourceId == parentId);   // 父实例一支只播一次，不重复
        Assert.Single(ends, e => e.Event.SourceId == childId);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ③ 落库失败 / 事务回滚 ⇒ 一支都不播（§11.2 原则 3 的另一面：不发未成立事实的事件）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 实例那一行<b>落库失败</b>（等价于事务回滚 / 流转后续步骤抛异常）⇒ 码 2 <b>一支都不许漏出去</b>。
    ///
    /// <para>判据机理：修复前码 2 在 <c>EndProcessHandler</c> 里就地处 fire，<c>instance.Finish()</c>
    /// 一执行事件就已经出去了；随后 <c>PersistTasksAsync</c> 的 <c>UpdateInstanceAsync</c> 抛错、
    /// 整笔事务回滚 ⇒ 监听器收到一条"流程已办结"，而那一行根本从没落到 20（幽灵事件）。
    /// 现在 fire 收口在 flush，且 flush 排在写库之后 ⇒ 写失败即异常穿出，flush 不可达，事件为零。</para>
    ///
    /// <para>⚠️ 与真库回滚的分工：本栈内存仓没有回滚语义（写即生效），故这里的"回滚"实现为
    /// <b>「实例写库这一步抛错 ⇒ 那一行仍是 10 且 flush 走不到」</b>——时机判据完全等价
    /// （旧形状在本断言下必红：回调列表非空）。真实的 <c>ITransactionTemplate</c> BEGIN 后
    /// 全量回滚由 <c>MySqlBehaviorSuite</c> §6.2 在真库上钉（<c>MySqlTransactionTemplate</c>），
    /// 两者不重复。此处注入 <see cref="AbortTrackingTx"/> 只为证明 flush 确实<b>在事务体内</b>
    /// （Aborted==1 即事务确实带着这次写一起废了），不改变内存仓的落库形状。</para>
    /// </summary>
    [Fact]
    public async Task InstanceWriteFails_TransactionAborts_EndEventNeverFires()
    {
        var (engine, repo, rec, tx) = NewTxStack();
        var did = await AddDefineAsync(repo, OneTaskFlow);
        var inst = await engine.StartProcessInstanceByIdAsync(did, "user1", new FlowData());
        var iid = inst.InstanceId!.Value;
        var taskId = await DoingTaskIdAsync(repo, iid);
        rec.Clear();

        repo.AbortNextInstanceUpdate = true;   // 下一次 UpdateInstanceAsync 在写之前抛错
        var threw = false;
        try
        {
            await engine.ExecuteProcessTaskAsync(taskId, "leader",
                new FlowData { [FlowConst.SubmitType] = (int)WfSubmitType.Agree });
        }
        catch
        {
            threw = true;   // 探针抛错原样穿出（引擎不吞），异常类型非本案判据
        }

        // 夹具前提：这一笔确实失败回滚了（写没成 + 事务确实 abort），否则下面的"零事件"是假绿
        Assert.True(threw, "夹具前提：实例落库这一步应抛错");
        Assert.Equal(1, tx.Aborted);
        Assert.Equal((int)WfInstanceState.Doing, await RowStateAsync(repo, iid));

        // 本案判据：落库没成 ⇒ 码 2 一支都不播（修复前这里必有一条幽灵 ProcessInstanceEnd）
        Assert.Empty(rec.Ends);
        Assert.DoesNotContain("ProcessInstanceEnd", rec.Names);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 时机判据（本类的全部要点）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 回调那一刻反查实例那一行的 state，必须<b>已经等于</b>载荷里承诺的 state，且那一行至少被写过一次。
    /// 旧形状（处理器就地 fire）在第一条断言上必红：<c>expected 20 but was 10</c>。
    /// </summary>
    private static void AssertEndFiresAfterRowWritten(TimingRecorder.EndRecord end,
        long expectedInstanceId, int expectedState)
    {
        Assert.Equal(expectedInstanceId, end.Event.SourceId!.Value);
        Assert.Equal(expectedInstanceId, Convert.ToInt64(end.Event.Data["instanceId"]!));
        Assert.Equal(expectedState, Convert.ToInt32(end.Event.Data["state"]!));
        Assert.True(end.WritesAtCallback >= 1,
            $"码 2 回调时实例行 {expectedInstanceId} 必须已被写过（写库次数={end.WritesAtCallback}）" +
            "——写在播之后 ⇒ 次数至少 1");
        Assert.NotNull(end.RowStateAtCallback);
        Assert.Equal(Convert.ToInt32(end.Event.Data["state"]!), end.RowStateAtCallback!.Value);
        Assert.Equal(expectedState, end.RowStateAtCallback!.Value);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 写序探针仓
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 只认"写库"的仓储探针（本类判据的支点），在 <see cref="MemoryRepository"/> 之上加写次数台账：
    /// <list type="bullet">
    /// <item><c>SaveInstanceAsync</c>／<c>UpdateInstanceAsync</c> ⇒ 累加该实例的写次数；</item>
    /// <item><c>FindInstanceByIdAsync</c> 沿用基类的 <b>CloneInstance 语义</b>（真 SQL 仓的 SELECT 语义：
    /// 读到的是最后一次写进去的值，改内存聚合根不会泄漏进读结果）——java 那边
    /// <c>MemoryProcessRepository.findInstanceById</c> 返回<b>活引用</b>，照不出写序，
    /// 故它必须新造快照副本；本栈基类已是副本，这里只补"写过几次"这一维。</item>
    /// </list>
    /// </summary>
    public sealed class WriteOrderRepository : MemoryRepository
    {
        private readonly Dictionary<long, int> _instanceWrites = new();

        /// <summary>置位 ⇒ 下一次 <c>UpdateInstanceAsync</c> <b>在写之前</b>抛错（消费后自动复位）：
        /// 模拟实例行落库失败／事务后续步骤异常。判据用得上"写之前"——那一行必须仍是旧值。</summary>
        public bool AbortNextInstanceUpdate { get; set; }

        public override Task SaveInstanceAsync(ProcessInstance instance)
        {
            var ret = base.SaveInstanceAsync(instance);
            RecordWrite(instance);
            return ret;
        }

        public override Task UpdateInstanceAsync(ProcessInstance instance)
        {
            if (AbortNextInstanceUpdate)
            {
                AbortNextInstanceUpdate = false;
                throw new InvalidOperationException(
                    "探针：实例行落库失败（模拟事务回滚，见 InstanceWriteFails_TransactionAborts_EndEventNeverFires）");
            }
            var ret = base.UpdateInstanceAsync(instance);
            RecordWrite(instance);
            return ret;
        }

        private void RecordWrite(ProcessInstance? instance)
        {
            if (instance?.InstanceId == null) return;
            _instanceWrites[instance.InstanceId.Value] =
                _instanceWrites.TryGetValue(instance.InstanceId.Value, out var n) ? n + 1 : 1;
        }

        /// <summary>该实例行到此刻为止被写过几次（0 ⇒ 一次都没落库）。</summary>
        public int WriteCount(long? instanceId) =>
            instanceId == null ? 0 : (_instanceWrites.TryGetValue(instanceId.Value, out var n) ? n : 0);
    }

    /// <summary>
    /// 只记账不改语义的事务模板探针：进入即 <see cref="Entered"/>＋1，回调抛错（＝这笔要废）
    /// 则 <see cref="Aborted"/>＋1 后原样 rethrow。用于坐实"flush 点在事务体内"，
    /// 从而让③ 组的"回滚 ⇒ 零事件"不是假绿（Aborted==1 才说明这一笔真的没走完）。
    /// </summary>
    public sealed class AbortTrackingTx : ITransactionTemplate
    {
        public int Entered { get; private set; }
        public int Aborted { get; private set; }

        public async Task<T> ExecuteInTxAsync<T>(Func<Task<T>> op)
        {
            Entered++;
            try
            {
                return await op();
            }
            catch
            {
                Aborted++;
                throw;
            }
        }
    }

    /// <summary>
    /// 回调那一刻的仓储读值录档。探针失败一律<b>记进列表</b>而不是就地抛——抛出会被
    /// <see cref="ProcessPublisher"/> 按 §11.5 隔离掉，反而测不出形状（与 EventCodes132 的 probe 同口径）。
    /// </summary>
    public sealed class TimingRecorder : IProcessEventListener
    {
        private readonly WriteOrderRepository _repo;

        public List<string> Names { get; } = new();
        public List<EndRecord> Ends { get; } = new();
        public List<string> Failures { get; } = new();

        public sealed record EndRecord(ProcessEvent Event, int? RowStateAtCallback, int WritesAtCallback);

        public TimingRecorder(WriteOrderRepository repo) => _repo = repo;

        public void Clear()
        {
            Names.Clear();
            Ends.Clear();
            Failures.Clear();
        }

        public async Task OnEventAsync(ProcessEvent @event)
        {
            Names.Add(@event.EventType.ToString());
            if (@event.EventType != ProcessEventType.ProcessInstanceEnd) return;

            var instanceId = @event.Data.GetLong("instanceId") ?? @event.SourceId;
            int? rowState = null;
            var writes = 0;
            if (instanceId != null)
            {
                // 「用仓储反查实例」——探针仓这里给的是落库快照的副本（SELECT 语义），
                // 所以读到的是"这一刻那一行是什么"，不是"内存聚合根正在被改成什么"
                rowState = (await _repo.FindInstanceByIdAsync(instanceId))?.State;
                writes = _repo.WriteCount(instanceId);
            }
            Ends.Add(new EndRecord(@event, rowState, writes));
        }

        public EndRecord FirstOf(string name)
        {
            Assert.Equal("ProcessInstanceEnd", name);
            Assert.NotEmpty(Ends);
            return Ends[0];
        }

        public List<EndRecord> AllOf(string name)
        {
            Assert.Equal("ProcessInstanceEnd", name);
            return Ends;
        }
    }
}

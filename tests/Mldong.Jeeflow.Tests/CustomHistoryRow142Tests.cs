using Xunit;
using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// 记录类（<c>snaker:custom</c>）节点的两条腿（issues/142 A 段 · spec 02 §6.1＋§6.2 · C# 栈内存一路）。
///
/// <para><b>立法原文（逐字依据，owner 2026-09-30 逐条拍）</b>：<c>jeeflow-doc/docs/spec/02-flow-definition.md</c>
/// §6.2「记录类历史行的三条硬要求」第 1／2 条 ＋ §6.1 表第二行（记录类：执行 clazz、落历史行、
/// 令牌继续；<b>它本来就不该有参与者，也不该有待办</b>）。</para>
///
/// <para><b>缺陷 1（本栈与 java 同形，A 段事实 1）</b>：历史行只有聚合内存 append、没有 INSERT 腿。
/// 旧形状 <c>CustomModel.ExecAsync</c> 把 <c>CreateHistoryTask(...)</c> 的返回值当垃圾丢掉 ⇒
/// ① <c>JeeflowEngine.PersistTasksAsync</c> 只保存 <c>exec.ProcessTaskList</c>（历史行不在里面）；
/// ② <c>UpdateInstanceAsync</c> 的级联只对 <c>TaskId != null</c> 的行发 UPDATE，而
/// <c>ProcessTask.Create</c> 从不赋 taskId（内存仓 <c>MemoryRepository.cs:135-138</c>／
/// SQL 仓 <c>MySqlRepository.cs:230-236</c>）⇒ 那条 <c>TaskState=20</c> 的行<b>永远进不了库</b>。
/// 本栈自家 <c>ComplianceTests.Cxx_CustomNodeRunsHandlerAndRecordsHistory</c> 对历史行零断言
/// （与 java <c>test08CustomNode</c> 同款失明）⇒ 这个洞测试照不出来。SQL 一路见
/// <see cref="MySqlCustomHistoryRow142Tests"/>。</para>
///
/// <para><b>缺陷 2</b>：<c>clazz</c> 不可解析时抛 <c>JeeflowException("自定义模型[class=…]实例化对象失败")</c>
/// 打断整条建单，且把「clazz 空串」与「未注册」<b>合成同一个异常</b>（覆盖面比 java 还宽）。
/// 按 §6.2 第 2 条改成记日志＋照常落历史行＋令牌继续，两档文案分别可诊断；
/// 处理器<b>自身</b>抛异常不在豁免内 ⇒ 照旧外抛（<see cref="ThrowingHandlerStillPropagates"/>
/// 是防"什么都吞"的负向对照）。</para>
///
/// <para><b>两条实现约束（做错了会造新缺陷）</b>：
/// ① 历史行落库但<b>不为它 fire 码 3（TASK_START）</b>——码 3 表达"新待办产生"，记录类出生即 20，
/// 给它 fire 就是广播一条假待办。所以走 <see cref="Execution.HistoryTasks"/> 这条与
/// <c>ProcessTaskList</c> 分离的通道，<b>落库与 fire 解耦</b>；顺带也不经过
/// <c>ApplySurrogateAsync</c>（那一步会把代理人并进 <c>ActorIds</c>＝在留痕行上凭空多挂一个
/// "能办的人"，spec §6.1 硬结论 1 禁的那族），见 <see cref="HistoryRowDoesNotReceiveSurrogateActor"/>。
/// ② 建单不变量照旧带上：<c>task_parent_id</c> 与行级 <c>isFirstTaskNode</c> 都打在
/// <b>仓储读回来的行</b>上（<see cref="FirstPositionCustomRowKeepsLineageInvariants"/>／
/// <see cref="MidPositionCustomRowPointsParentAtTheFinishedTask"/>）。</para>
///
/// <para><b>ExpireTime（issues/126 普查把 java 的 createHistoryTask 列为写点之一）实读结论</b>：
/// 本支<b>没写，也不该写</b>——<see cref="CustomModel"/> 没有到期表达式属性
/// （<c>ModelParser</c> 的 custom 档只解析 clazz/methodName/args/val，<c>ExpireTime</c> 只在
/// TaskModel 档解析），按 issues/126「节点没配 ⇒ 这一列保持 NULL，赋一个建单时刻是病灶」
/// 正是应有形状；java 基准同形（<c>ProcessInstance.createHistoryTask</c> 也不调 applyExpireTime）。
/// 由 <see cref="HistoryRowKeepsExpireTimeNull"/> 钉住，防止将来"顺手补个 now()"。</para>
///
/// <para>断言一律打在<b>仓储读回来的行</b>（<c>FindTaskByIdAsync</c>／<c>FindHistoryTasksAsync</c>／
/// <c>FindDoingTasksAsync</c>）上，不看流转过程中的内存对象——"只在聚合里 append 一条"正是本条
/// 要判的红，用内存对象断言等于自己给自己放水。</para>
/// </summary>
public class CustomHistoryRow142Tests
{
    /// <summary>八栈共享夹具：start → apply(applicant) → custom1 → end。</summary>
    private const string SharedFixture = "08-custom-node";

    private const string TestClazz = "com.mldong.jeeflow.test.TestCustomHandler";

    /// <summary>记录类节点在中间、后面还有一张真待办：待办数与码 3 计数的对照组。</summary>
    private const string CustomMiddleFlow = """
        {"name": "c142-mid", "displayName": "记录类在中间", "type": "approval",
         "nodes": [
           {"id": "start", "type": "snaker:start", "text": {"value": "开始"}},
           {"id": "apply", "type": "snaker:task", "text": {"value": "发起申请"},
            "properties": {"form": "apply-form", "assignee": "applicant", "taskType": 0, "performType": 0}},
           {"id": "custom1", "type": "snaker:custom", "text": {"value": "通知外部系统"},
            "properties": {"clazz": "com.mldong.jeeflow.test.TestCustomHandler",
                           "methodName": "execute", "args": "param1", "val": "customResult"}},
           {"id": "approve", "type": "snaker:task", "text": {"value": "审批"},
            "properties": {"form": "approve-form", "assignee": "leader", "taskType": 0, "performType": 0}},
           {"id": "end", "type": "snaker:end", "text": {"value": "结束"}}
         ],
         "edges": [
           {"id": "e1", "sourceNodeId": "start", "targetNodeId": "apply"},
           {"id": "e2", "sourceNodeId": "apply", "targetNodeId": "custom1"},
           {"id": "e3", "sourceNodeId": "custom1", "targetNodeId": "approve"},
           {"id": "e4", "sourceNodeId": "approve", "targetNodeId": "end"}
         ]}
        """;

    /// <summary>记录类节点直接跟在开始节点后（首任务节点档：isFirstTaskNode=true、parent=0）。</summary>
    private const string CustomFirstFlow = """
        {"name": "c142-first", "displayName": "记录类打头", "type": "approval",
         "nodes": [
           {"id": "start", "type": "snaker:start", "text": {"value": "开始"}},
           {"id": "customFirst", "type": "snaker:custom", "text": {"value": "发起留痕"},
            "properties": {"clazz": "com.mldong.jeeflow.test.TestCustomHandler"}},
           {"id": "end", "type": "snaker:end", "text": {"value": "结束"}}
         ],
         "edges": [
           {"id": "e1", "sourceNodeId": "start", "targetNodeId": "customFirst"},
           {"id": "e2", "sourceNodeId": "customFirst", "targetNodeId": "end"}
         ]}
        """;

    /// <summary>clazz 配成空串（档①「没配」）。</summary>
    private const string BlankClazzFlow = """
        {"name": "c142-blank", "displayName": "clazz 空串", "type": "approval",
         "nodes": [
           {"id": "start", "type": "snaker:start", "text": {"value": "开始"}},
           {"id": "customBlank", "type": "snaker:custom", "text": {"value": "空 clazz 留痕"},
            "properties": {"clazz": ""}},
           {"id": "end", "type": "snaker:end", "text": {"value": "结束"}}
         ],
         "edges": [
           {"id": "e1", "sourceNodeId": "start", "targetNodeId": "customBlank"},
           {"id": "e2", "sourceNodeId": "customBlank", "targetNodeId": "end"}
         ]}
        """;

    /// <summary>clazz 指向一个会自己抛异常的处理器（档③负向对照）。</summary>
    private const string ThrowingClazzFlow = """
        {"name": "c142-throw", "displayName": "处理器自身抛", "type": "approval",
         "nodes": [
           {"id": "start", "type": "snaker:start", "text": {"value": "开始"}},
           {"id": "customBoom", "type": "snaker:custom", "text": {"value": "外部系统调用失败"},
            "properties": {"clazz": "c142.throwingHandler"}},
           {"id": "end", "type": "snaker:end", "text": {"value": "结束"}}
         ],
         "edges": [
           {"id": "e1", "sourceNodeId": "start", "targetNodeId": "customBoom"},
           {"id": "e2", "sourceNodeId": "customBoom", "targetNodeId": "end"}
         ]}
        """;

    private static int _defineSeq;

    // ═══ 基建 ═══

    /// <summary>一套栈＋两个取证口：事件（只留码 3）与 WARNING 日志。</summary>
    private sealed class Stack
    {
        public JeeflowEngine Engine = null!;
        public MemoryRepository Repo = null!;
        public ServiceContext Ctx = null!;
        public List<ProcessEvent> TaskStarts { get; } = new();
        public List<string> Warnings { get; } = new();

        public static Stack New()
        {
            var (engine, repo, ctx) = TestInfra.NewEngineWithCtx();
            var s = new Stack { Engine = engine, Repo = repo, Ctx = ctx };
            ctx.RegisterEventListener(new TaskStartCapture(s.TaskStarts));
            // 诊断日志取证钩子（internal 成员，公开 API 面不扩）：不设时生产出口是 stderr。
            ctx.WarningSinkForTest = line => s.Warnings.Add(line);
            return s;
        }

        public async Task<long> DefineAsync(string content, string name = "c142") =>
            await TestInfra.SaveFlowDefineAsync(Repo, $"{name}-{_defineSeq++}", content);

        public async Task<long> StartDefineAsync(long defineId, string op)
        {
            var inst = await Engine.StartProcessInstanceByIdAsync(defineId, op, new FlowData());
            return inst.InstanceId!.Value;
        }

        /// <summary>
        /// 仓储读回历史行（找不到就 fail——"这一行只在聚合内存里"正是缺陷 1 的形状，
        /// 不许用"内存对象里有"糊过去）。taskId 已分配且 <c>FindTaskByIdAsync</c> 查得到 ⇒ INSERT 腿真落库。
        /// </summary>
        public async Task<ProcessTask> HistoryRowAsync(long instanceId, string taskName)
        {
            var rows = (await Repo.FindHistoryTasksAsync(instanceId))
                .Where(t => t.TaskName == taskName)
                .ToList();
            Assert.True(rows.Count == 1,
                $"wf_process_task 读回应恰有一条 task_name={taskName}（实得 {rows.Count} 条）");
            var byId = await Repo.FindTaskByIdAsync(rows[0].TaskId!.Value);
            Assert.NotNull(byId);
            return byId!;
        }

        public Task<int> DoingCountAsync(long instanceId) =>
            Task.FromResult((Repo.Tasks.Values
                .Count(t => t.ProcessInstanceId == instanceId
                    && t.TaskState == (int)WfTaskState.Doing)));

        public Task<int> RowCountAsync(long instanceId) =>
            Task.FromResult(Repo.Tasks.Values.Count(t => t.ProcessInstanceId == instanceId));

        /// <summary>落库行的 taskId（没落库就是 0，用来把"有 id 才谈 fire 不 fire"钉死）。</summary>
        public long RowIdOf(long instanceId, string taskName) =>
            Repo.Tasks.Values
                .Where(t => t.ProcessInstanceId == instanceId && t.TaskName == taskName)
                .Select(t => t.TaskId ?? 0L)
                .FirstOrDefault();
    }

    private sealed class TaskStartCapture : IProcessEventListener
    {
        private readonly List<ProcessEvent> _sink;
        public TaskStartCapture(List<ProcessEvent> sink) => _sink = sink;
        public Task OnEventAsync(ProcessEvent @event)
        {
            if (@event.EventType == ProcessEventType.ProcessTaskStart) _sink.Add(@event);
            return Task.CompletedTask;
        }
    }

    /// <summary>会自己炸的业务处理器（档③：解析到了、跑炸了 ⇒ 不在豁免内）。</summary>
    private sealed class ThrowingCustomHandler : IHandler
    {
        public Task HandleAsync(Execution execution) =>
            throw new InvalidOperationException("外部系统调用失败（模拟业务错误）");
    }

    // ═══ 缺陷 1：历史行的 INSERT 腿 ═══

    /// <summary>
    /// 判据①：发起带 custom 的定义并办掉申请节点后，<b>库里查得到</b>那条 TaskState=20 的行。
    /// 改前会红：INSERT 腿摘掉时这一格读不到行（仓储里根本没有 custom1，更没有 taskId）。
    /// </summary>
    [Fact]
    public async Task HistoryRowIsPersistedAndReadableFromRepository()
    {
        var s = Stack.New();
        var did = await s.DefineAsync(TestInfra.LoadFlow(SharedFixture), "custom-node");
        var iid = await s.StartDefineAsync(did, "applicant");

        var apply = await TestInfra.FindDoingForAsync(s.Repo, iid, "applicant");
        await s.Engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData());

        var row = await s.HistoryRowAsync(iid, "custom1");
        Assert.Equal((int)WfTaskState.Finished, row.TaskState);            // 20，不是 10
        Assert.Equal("通知外部系统", row.DisplayName);
        Assert.Equal(new List<string> { "applicant" }, row.ActorIds);       // 参与者＝当前操作人（留痕主体）
        Assert.Null(row.ExpireTime);                                        // 见类头 ExpireTime 结论
        // spec 02 §6.2 第 1bis 条（八栈对表补的细则）：留痕行必须写处理人与完成时间——
        // doneList 走 `state<>10 AND operator=?`、审批记录也读这两列，
        // 只写 task_state=20 的留痕在用户面上等于没落过。
        Assert.Equal("applicant", row.ActorId);
        Assert.NotNull(row.FinishTime);
    }

    /// <summary>
    /// 判据③：令牌继续走到终点——实例 state=20，历史行没把流转卡住。
    /// 这一格改前也是绿的（旧形状确实能走完），留着是让"落库"与"能走完"各有独立判据，
    /// 防止 INSERT 腿接歪成"流程不走了"。
    /// </summary>
    [Fact]
    public async Task InstanceStillReachesEndAfterCustomNode()
    {
        var s = Stack.New();
        var did = await s.DefineAsync(TestInfra.LoadFlow(SharedFixture), "custom-node");
        var iid = await s.StartDefineAsync(did, "applicant");
        var apply = await TestInfra.FindDoingForAsync(s.Repo, iid, "applicant");

        await s.Engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData());

        var inst = await s.Repo.FindInstanceByIdAsync(iid);
        Assert.Equal((int)WfInstanceState.Finished, inst!.State);
        Assert.True((bool)inst.Variables["customNodeRan"]!);                // 处理器确实跑了
        Assert.Equal(0, await s.DoingCountAsync(iid));
    }

    /// <summary>
    /// 判据②：待办数不因历史行增加。custom 在中间、后面还有一张真待办 ⇒
    /// 落库行共 3 条（apply DONE ＋ custom1 DONE ＋ approve DOING），可办的只有 1 张。
    /// "摘掉 INSERT 腿"这一格的行数读数会掉到 2 ⇒ 落库与待办两件事同时钉死。
    /// </summary>
    [Fact]
    public async Task HistoryRowAddsNoTodoAndOnlyOneDoingRowRemains()
    {
        var s = Stack.New();
        var did = await s.DefineAsync(CustomMiddleFlow);
        var iid = await s.StartDefineAsync(did, "applicant");
        var apply = await TestInfra.FindDoingForAsync(s.Repo, iid, "applicant");

        await s.Engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData());

        Assert.Equal(1, await s.DoingCountAsync(iid));                                  // 只有 approve
        var doing = await s.Repo.FindDoingTasksAsync(iid, null);
        Assert.Equal(new[] { "approve" }, doing.Select(t => t.TaskName).ToArray());     // 历史行不在待办里
        Assert.Equal(3, await s.RowCountAsync(iid));                                    // apply＋custom1＋approve
    }

    /// <summary>
    /// 判据④：没有为历史行出现码 3（TASK_START）。发起腿 1 次（apply）＋办理腿 1 次（approve），
    /// custom1 那一行<b>一次都不许有</b>。这条是"落库与 fire 解耦"的反面哨兵：
    /// 把历史行塞进 <c>ProcessTaskList</c> 走既有 save→notifyTaskStart 那条路，这一格立刻红。
    /// </summary>
    [Fact]
    public async Task NoTaskStartEventIsFiredForHistoryRow()
    {
        var s = Stack.New();
        var did = await s.DefineAsync(CustomMiddleFlow);
        var iid = await s.StartDefineAsync(did, "applicant");
        var apply = await TestInfra.FindDoingForAsync(s.Repo, iid, "applicant");

        await s.Engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData());

        var customRowId = s.RowIdOf(iid, "custom1");
        Assert.NotEqual(0L, customRowId);                        // 先确认它真落了库（有 id 才谈 fire 不 fire）
        Assert.DoesNotContain(customRowId, s.TaskStarts.Select(e => e.SourceId!.Value));

        var fired = s.TaskStarts.Select(e => e.SourceId!.Value).ToList();
        Assert.Equal(2, fired.Count);                            // apply ＋ approve，一条不多
        Assert.Contains(apply.TaskId!.Value, fired);
        Assert.Contains(s.RowIdOf(iid, "approve"), fired);
    }

    /// <summary>
    /// 建单不变量（首任务节点档）：custom 直接跟在 start 后 ⇒ 读回来的行上
    /// <c>task_parent_id=0</c>（发起 execution 没有刚办结的任务）＋行级 <c>isFirstTaskNode=true</c>。
    /// 这两个值过去由 <c>CreateHistoryTask</c> 的入参保证但<b>从没进过库</b>；
    /// 补上落库腿后必须能在仓储读数里看到，否则血缘回退（issues/121 P2）在留痕行上被剪断。
    /// </summary>
    [Fact]
    public async Task FirstPositionCustomRowKeepsLineageInvariants()
    {
        var s = Stack.New();
        var did = await s.DefineAsync(CustomFirstFlow);
        var iid = await s.StartDefineAsync(did, "applicant");   // 发起即走完（start→custom→end）

        var row = await s.HistoryRowAsync(iid, "customFirst");
        Assert.Equal((int)WfTaskState.Finished, row.TaskState);
        Assert.Equal(0L, row.ParentTaskId);                       // 工厂把 null 落 0，不是 null
        Assert.True((bool)row.Variables[FlowConst.IsFirstTaskNode]!);   // 行级首节点标记随变量落库
        Assert.Equal((int)WfInstanceState.Finished, (await s.Repo.FindInstanceByIdAsync(iid))!.State);
    }

    /// <summary>建单不变量（非首位档）：parent 指向刚办结的那张任务行，首节点标记为 false。</summary>
    [Fact]
    public async Task MidPositionCustomRowPointsParentAtTheFinishedTask()
    {
        var s = Stack.New();
        var did = await s.DefineAsync(CustomMiddleFlow);
        var iid = await s.StartDefineAsync(did, "applicant");
        var apply = await TestInfra.FindDoingForAsync(s.Repo, iid, "applicant");

        await s.Engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData());

        var row = await s.HistoryRowAsync(iid, "custom1");
        Assert.Equal(apply.TaskId, row.ParentTaskId);             // 血缘链不剪断
        Assert.False((bool)row.Variables[FlowConst.IsFirstTaskNode]!);   // 是 false，不是"没写"
    }

    /// <summary>
    /// ExpireTime 哨兵：历史行那一列保持 NULL（issues/126「节点没配 ⇒ 这一列一动不动」）。
    /// 本栈 CustomModel 没有到期表达式可取，java 基准的 createHistoryTask 同样不写。
    /// </summary>
    [Fact]
    public async Task HistoryRowKeepsExpireTimeNull()
    {
        var s = Stack.New();
        var did = await s.DefineAsync(CustomFirstFlow);
        var iid = await s.StartDefineAsync(did, "applicant");

        var row = await s.HistoryRowAsync(iid, "customFirst");
        Assert.Null(row.ExpireTime);                    // 赋"建单时刻"＝新建即逾期，跨栈逾期统计全失真
        Assert.Null(s.Repo.Tasks[row.TaskId!.Value].ExpireTime);   // 仓储行本体也没被级联写过
    }

    /// <summary>
    /// 落库通道选择的代价核算：历史行<b>不</b>走 <c>ApplySurrogateAsync</c>。
    /// 委托自动生效给 DOING 待办挂代理人是对的；给一条 FINISHED 留痕行挂就是"凭空多一个能办的人"
    /// （spec §6.1 硬结论 1「不许兜底把行挂给别人」同族）。
    /// 对照组：同一次发起里 apply 那张待办行照旧并入代理人。
    /// </summary>
    [Fact]
    public async Task HistoryRowDoesNotReceiveSurrogateActor()
    {
        var repo = new MemoryRepository();
        var ext = new MemoryExtRepository(repo, null);
        var clock = new FixedClock(new DateTime(2026, 8, 1, 9, 0, 0));
        var ctx = new ServiceContext(repo, ext)
        {
            Clock = clock,
            IdGenerator = new AtomicIdGenerator(1, clock),
            UserProvider = new TestUserProvider(),
        };
        repo.Configure(ctx);
        ext.Configure(ctx);
        TestInfra.RegisterBuiltins(ctx);
        var s = new Stack { Engine = new JeeflowEngine(ctx), Repo = repo, Ctx = ctx };
        await ext.SaveSurrogateAsync(new ProcessSurrogate
        {
            ProcessName = null,                    // 全流程兜底
            Operator = "applicant",
            Surrogate = "leader",
            Enabled = 1,
            CreateUser = "tester",
        });

        var did = await s.DefineAsync(TestInfra.LoadFlow(SharedFixture), "custom-node");
        var iid = await s.StartDefineAsync(did, "applicant");
        var apply = await TestInfra.FindDoingForAsync(repo, iid, "applicant");
        Assert.Contains("leader", apply.ActorIds);              // 待办行：代理人照旧并入

        await s.Engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData());

        var row = await s.HistoryRowAsync(iid, "custom1");
        Assert.Equal(new List<string> { "applicant" }, row.ActorIds);   // 留痕行：一个都不许多
    }

    // ═══ 缺陷 2：clazz 不可解析的两档 ═══

    /// <summary>
    /// 判据⑤-未注册档：clazz 配了但注册表没有 ⇒ <b>不抛</b>、记一条可诊断 WARNING（含节点名＋clazz 原样串）、
    /// 历史行照落、实例走到终点、待办不增。
    /// 改前会红：把"记日志继续"还原成 <c>throw</c> 时这一格炸在
    /// <c>JeeflowException("自定义模型[class=…]实例化对象失败")</c> 上，且一行都不落。
    /// </summary>
    [Fact]
    public async Task UnregisteredClazzLogsAndStillPersistsHistoryRow()
    {
        var s = Stack.New();
        s.Ctx.CustomHandlers.Clear();                            // 模拟未注册（共享夹具用的是 JVM 类名）
        var did = await s.DefineAsync(TestInfra.LoadFlow(SharedFixture), "custom-node");
        var iid = await s.StartDefineAsync(did, "applicant");

        var apply = await TestInfra.FindDoingForAsync(s.Repo, iid, "applicant");
        await s.Engine.ExecuteProcessTaskAsync(apply.TaskId!.Value, "applicant", new FlowData());

        var row = await s.HistoryRowAsync(iid, "custom1");       // 不抛＋照常落库
        Assert.Equal((int)WfTaskState.Finished, row.TaskState);
        Assert.Equal(0, await s.DoingCountAsync(iid));           // 待办不因它增加
        Assert.Equal((int)WfInstanceState.Finished, (await s.Repo.FindInstanceByIdAsync(iid))!.State);

        Assert.Single(s.Warnings);
        Assert.Contains("未注册处理器", s.Warnings[0]);
        Assert.Contains("custom1", s.Warnings[0]);               // 定位得到节点
        Assert.Contains(TestClazz, s.Warnings[0]);               // 定位得到 clazz 原样串
    }

    /// <summary>
    /// 判据⑤-空串档：clazz 配成空串 ⇒ 同样不抛、同样落历史行，日志走"未配置 clazz"这一支。
    /// </summary>
    [Fact]
    public async Task BlankClazzLogsAndStillPersistsHistoryRow()
    {
        var s = Stack.New();
        var did = await s.DefineAsync(BlankClazzFlow);
        var iid = await s.StartDefineAsync(did, "applicant");

        var row = await s.HistoryRowAsync(iid, "customBlank");
        Assert.Equal((int)WfTaskState.Finished, row.TaskState);
        Assert.Equal(new List<string> { "applicant" }, row.ActorIds);
        Assert.Equal(0, await s.DoingCountAsync(iid));
        Assert.Equal((int)WfInstanceState.Finished, (await s.Repo.FindInstanceByIdAsync(iid))!.State);

        Assert.Single(s.Warnings);
        Assert.Contains("未配置 clazz", s.Warnings[0]);
        Assert.Contains("customBlank", s.Warnings[0]);
    }

    /// <summary>
    /// 判据⑤的第二半：<b>两档日志分别可诊断</b>（spec §6.2 第 2 条点名"c# 现在把两者合成同一个异常，
    /// 覆盖面比 java 宽"）。两档各跑一次，文案必须不同且各自带自己的判据词。
    /// </summary>
    [Fact]
    public async Task BlankAndUnregisteredArmsHaveDistinctDiagnosticMessages()
    {
        var blank = Stack.New();
        await blank.StartDefineAsync(await blank.DefineAsync(BlankClazzFlow), "applicant");

        var unregistered = Stack.New();
        unregistered.Ctx.CustomHandlers.Clear();
        await unregistered.StartDefineAsync(await unregistered.DefineAsync(CustomFirstFlow), "applicant");

        Assert.Single(blank.Warnings);
        Assert.Single(unregistered.Warnings);
        Assert.NotEqual(blank.Warnings[0], unregistered.Warnings[0]);
        Assert.Contains("未配置", blank.Warnings[0]);
        Assert.DoesNotContain("未注册", blank.Warnings[0]);
        Assert.Contains("未注册", unregistered.Warnings[0]);
        Assert.DoesNotContain("未配置", unregistered.Warnings[0]);
    }

    /// <summary>
    /// 判据⑥（负向对照，防"什么都吞"）：处理器<b>解析到了、自己抛异常</b> ⇒ 照旧外抛，
    /// 且实例没被当成走通（内存仓储里那一行还停在 10，没落 20）。
    /// 这一格是"豁免只覆盖配置形状错、不覆盖业务错误"的唯一哨兵——档③若也被 try/catch 吞掉，立刻红。
    /// </summary>
    [Fact]
    public async Task ThrowingHandlerStillPropagates()
    {
        var s = Stack.New();
        s.Ctx.CustomHandlers["c142.throwingHandler"] = new ThrowingCustomHandler();
        var did = await s.DefineAsync(ThrowingClazzFlow);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => s.StartDefineAsync(did, "applicant"));
        Assert.Contains("外部系统调用失败", ex.Message);

        Assert.True(s.Repo.Tasks.Values.All(t => t.TaskName != "customBoom"));   // 业务错误不该留下"已完成"的假留痕
        var inst = Assert.Single(s.Repo.Instances.Values);
        Assert.NotEqual((int)WfInstanceState.Finished, inst.State);              // 没被吞成成功
    }

    /// <summary>
    /// 回归：解析成功时处理器照旧执行、实例变量照旧写、历史行照落，且<b>一条 WARNING 都不该有</b>
    /// （防止"什么都记日志"把正常档也污染成告警噪声）。
    /// </summary>
    [Fact]
    public async Task RegisteredHandlerStillRunsAndRowStillLandsWithoutWarning()
    {
        var s = Stack.New();
        var did = await s.DefineAsync(CustomFirstFlow);
        var iid = await s.StartDefineAsync(did, "applicant");

        var inst = await s.Repo.FindInstanceByIdAsync(iid);
        Assert.True((bool)inst!.Variables["customNodeRan"]!);
        Assert.Equal((int)WfInstanceState.Finished, inst.State);
        Assert.Empty(s.Warnings);
        var row = await s.HistoryRowAsync(iid, "customFirst");
        Assert.Equal((int)WfTaskState.Finished, row.TaskState);
    }
}

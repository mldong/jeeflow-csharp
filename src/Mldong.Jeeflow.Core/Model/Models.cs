namespace Mldong.Jeeflow.Core;

/// <summary>模型基类（对齐 Java BaseModel）。</summary>
public abstract class BaseModel
{
    public string? Name { get; set; }
    public string? DisplayName { get; set; }
}

/// <summary>边/转移模型——连接两个节点的有向边（对齐 Java TransitionModel）。</summary>
public class TransitionModel : BaseModel
{
    public NodeModel? Source { get; set; }
    public NodeModel? Target { get; set; }
    public string? To { get; set; }
    public string? Expr { get; set; }
    public string? G { get; set; }
    public bool Enabled { get; set; }

    public async Task ExecuteAsync(Execution execution)
    {
        if (!Enabled) return;
        if (Target is TaskModel taskTarget)
        {
            await new CreateTaskHandler(taskTarget).HandleAsync(execution);
        }
        else if (Target is SubProcessModel subTarget)
        {
            await new StartSubProcessHandler(subTarget).HandleAsync(execution);
        }
        else if (Target != null)
        {
            await Target.ExecuteAsync(execution);
        }
        else
        {
            // issues/143：出边的目标节点不在模型里 ⇒ 这条边**落穿**。
            // 成因：解析期该类型未建档、节点被跳过（ModelParser.ParseNode 返回 null），
            // 但**指向它的边**仍挂在上游节点的 Outputs 上，而 Target 只在目标节点存在于
            // 模型里时才赋值 ⇒ 这里为 null。本栈原本静默 no-op：不崩（比 java 的 NPE、
            // php 的 Call to a member function execute() on null 好），
            // 但 spec 02 类型键义务 2 的「可诊断」也没兑现 ⇒ 补一条 WARNING 后**停住**，
            // 不越过未知节点继续跑（未知档没被解析成任何模型，越过它等于用一条臆造的通路
            // 把跑不通的定义跑成功）。与 rust/moon/go/python/node 的可观测结果一致。
            execution.Context.LogWarning(
                "转移出边的目标节点不在模型里（该节点类型未建档，解析期已跳过），本次流转停在这条边: "
                + $"from={Source?.Name ?? "null"}, to={To}");
        }
    }
}

/// <summary>
/// 节点模型抽象基类（对齐 Java NodeModel）：execute 模板方法 = pre 拦截器 → exec → 重设当前节点 → post 拦截器。
/// </summary>
public abstract class NodeModel : BaseModel
{
    public string? Layout { get; set; }
    public List<TransitionModel> Inputs { get; set; } = new();
    public List<TransitionModel> Outputs { get; set; } = new();
    public string? PreInterceptors { get; set; }
    public string? PostInterceptors { get; set; }

    /// <summary>子类实现具体执行逻辑。</summary>
    internal abstract Task ExecAsync(Execution execution);

    public async Task ExecuteAsync(Execution execution)
    {
        execution.NodeModel = this;
        await ExecPreInterceptorsAsync(execution);
        await ExecAsync(execution);
        // 流转链中 CreateTaskHandler 等会改写 NodeModel（标记"当前创建的任务节点"），
        // post 拦截器必须看到的是本节点——重新设置（1.8.0：字段权限/状态字段按节点判定）
        execution.NodeModel = this;
        await ExecPostInterceptorsAsync(execution);
    }

    /// <summary>执行所有输出边。</summary>
    protected async Task RunOutTransitionAsync(Execution execution)
    {
        foreach (var tr in Outputs)
        {
            tr.Enabled = true;
            await tr.ExecuteAsync(execution);
        }
    }

    private Task ExecPreInterceptorsAsync(Execution execution)
        => RunInterceptorsAsync(ResolvePreInterceptors(execution.ProcessModel), execution);

    private Task ExecPostInterceptorsAsync(Execution execution)
        => RunInterceptorsAsync(ResolvePostInterceptors(execution.ProcessModel), execution);

    /// <summary>
    /// 本节点实际生效的 pre 拦截器声明串：<b>节点级非空优先，空则回落流程级</b>
    /// （对齐 Java "节点配置了拦截器 → 流程的不生效"）。
    /// <para>执行期（<see cref="ExecPreInterceptorsAsync"/>）与办理前的纯读校验
    /// （<see cref="ValidateDeclaredInterceptors"/>）共用这一处判据，不许有第二套取串规则。</para>
    /// </summary>
    public string? ResolvePreInterceptors(ProcessModel? processModel) =>
        !string.IsNullOrEmpty(PreInterceptors) ? PreInterceptors : processModel?.PreInterceptors;

    /// <summary>本节点实际生效的 post 拦截器声明串（回落判据同 <see cref="ResolvePreInterceptors"/>）。</summary>
    public string? ResolvePostInterceptors(ProcessModel? processModel) =>
        !string.IsNullOrEmpty(PostInterceptors) ? PostInterceptors : processModel?.PostInterceptors;

    /// <summary>
    /// <b>纯读</b>校验：本节点实际生效的 pre／post 两串里每个名字都必须在
    /// <paramref name="namedInterceptors"/> 里可解析；解析不了抛与执行期<b>同一句</b>
    /// <c>无法实例化拦截器: {name}</c>。
    ///
    /// <para><b>为什么不是「先跑一遍拦截器」</b>：<see cref="IFlowInterceptor.InterceptAsync"/> 是
    /// 有副作用的（写 args／发事件／改聚合根），预跑一遍等于把副作用做两次；这里只查名字，
    /// 名字切分走 <see cref="SplitInterceptorNames"/>、取串走 <see cref="ResolvePreInterceptors"/>／
    /// <see cref="ResolvePostInterceptors"/>，与执行期逐字同判据，只是不做 <c>InterceptAsync</c>。</para>
    ///
    /// <para><b>为什么必须由引擎排在写之前</b>：post 串是在 <c>node.ExecuteAsync</c> 里才解析的，
    /// 而引擎 <c>PrepareExecutionAsync</c> 早已 <c>CompleteTask</c> + <c>UpdateTaskAsync</c>。
    /// 真库靠外层事务整笔回滚看不出差别；内存仓储无回滚 ⇒ 报错那次「当前任务已 DONE、新任务没建」
    /// ⇒ 实例 DOING(10) 却零待办（issues/244 ①）。</para>
    /// </summary>
    public void ValidateDeclaredInterceptors(
        ProcessModel? processModel, IReadOnlyDictionary<string, IFlowInterceptor> namedInterceptors)
    {
        ValidateInterceptorSpec(ResolvePreInterceptors(processModel), namedInterceptors);
        ValidateInterceptorSpec(ResolvePostInterceptors(processModel), namedInterceptors);
    }

    private static void ValidateInterceptorSpec(
        string? interceptors, IReadOnlyDictionary<string, IFlowInterceptor> namedInterceptors)
    {
        foreach (var name in SplitInterceptorNames(interceptors))
            if (!namedInterceptors.ContainsKey(name))
                throw new JeeflowException($"无法实例化拦截器: {name}");
    }

    /// <summary>声明串 → 名字序列（英文逗号分隔、trim、空段丢弃）；执行期与校验期共用。</summary>
    private static IEnumerable<string> SplitInterceptorNames(string? interceptors)
    {
        if (string.IsNullOrEmpty(interceptors)) yield break;
        foreach (var raw in interceptors.Split(','))
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            yield return name;
        }
    }

    /// <summary>按名解析拦截器并执行；声明名不可解析 → 显式错误（C20，不静默跳过）。</summary>
    private static async Task RunInterceptorsAsync(string? interceptors, Execution execution)
    {
        foreach (var name in SplitInterceptorNames(interceptors))
        {
            if (!execution.Context.NamedInterceptors.TryGetValue(name, out var interceptor))
                throw new JeeflowException($"无法实例化拦截器: {name}");
            await interceptor.InterceptAsync(execution);
        }
    }

    /// <summary>获取后续指定类型的所有节点模型（防环遍历）。</summary>
    public List<T> GetNextModels<T>() where T : NodeModel
    {
        var models = new List<T>();
        var visited = new HashSet<string>();
        foreach (var tm in Outputs)
            AddNextModels(models, tm, visited);
        return models;
    }

    private void AddNextModels<T>(List<T> models, TransitionModel tm, HashSet<string> visited)
        where T : NodeModel
    {
        if (tm.To == null || !visited.Add(tm.To)) return;
        if (tm.Target is T hit)
        {
            models.Add(hit);
        }
        else if (tm.Target != null)
        {
            foreach (var tm2 in tm.Target.Outputs) AddNextModels(models, tm2, visited);
        }
    }

    /// <summary>判断 current 是否可以退回到 parent（驳回校验）。</summary>
    public static bool CanRejected(NodeModel current, NodeModel parent)
    {
        var result = false;
        foreach (var tm in current.Inputs)
        {
            var source = tm.Source;
            if (source == parent) return true;
            if (source is ForkModel or JoinModel or StartModel) continue;
            if (source != null) result = result || CanRejected(source, parent);
        }
        return result;
    }
}

/// <summary>开始节点模型（fire PROCESS_INSTANCE_START 后沿输出边驱动）。</summary>
public class StartModel : NodeModel
{
    internal override async Task ExecAsync(Execution execution)
    {
        await ProcessPublisher.NotifyAsync(
            new ProcessEvent
            {
                EventType = ProcessEventType.ProcessInstanceStart,
                SourceId = execution.ProcessInstanceId,
                // spec §11.3 码 1 直传载荷必备键：instanceId（实例行已由 SaveInstanceAsync 落库）
                Data = new FlowData { ["instanceId"] = execution.ProcessInstanceId },
            },
            execution.Context.EventListeners);
        await RunOutTransitionAsync(execution);
    }
}

/// <summary>结束节点模型（EndProcessHandler：submitType=2 → reject(45)，否则 finish(20)）。</summary>
public class EndModel : NodeModel
{
    internal override Task ExecAsync(Execution execution) =>
        new EndProcessHandler(this).HandleAsync(execution);
}

/// <summary>
/// 任务节点模型（对齐 Java TaskModel）：执行闸三档（规范 04 · 后加签「推进闸」）——
/// 会签 performType 走 <see cref="CountersignHandler"/>、开了 <see cref="AllowAddSign"/> 的普通节点走
/// <see cref="AddSignHandler"/>，两档都<b>只有 merged 才沿边驱动</b>；其余档无条件流出（普通档不变）。
/// </summary>
public class TaskModel : NodeModel
{
    public string? Form { get; set; }
    public string? Assignee { get; set; }
    public string? AssignmentHandler { get; set; }
    public WfTaskType? TaskType { get; set; }
    public WfPerformType? PerformType { get; set; }
    public string? ReminderTime { get; set; }
    public string? ReminderRepeat { get; set; }
    public string? ExpireTime { get; set; }
    public string? AutoExecute { get; set; }
    public string? Callback { get; set; }
    /// <summary>扩展属性（candidateUsers/candidateGroups/字段权限 PERMISSION_* 等）。</summary>
    public FlowData Ext { get; set; } = new();
    public string? CandidateHandler { get; set; }
    public WfCountersignType? CountersignType { get; set; }
    public string? CountersignCompletionCondition { get; set; }

    /// <summary>
    /// 退回发起人后的重提方式（properties.reapplyMode；缺省 <see cref="WfReapplyMode.Normal"/>）。
    /// <see cref="WfReapplyMode.ReturnToSource"/> ⇒ 退回时建的申请待办在重提后直接回到本节点，
    /// 不重放申请节点出边、不重算来源前条件（「退回修改、重提直返」）。
    /// </summary>
    public WfReapplyMode ReapplyMode { get; set; } = WfReapplyMode.Normal;

    /// <summary>
    /// 后加签开关（<c>properties.allowAddSign</c>，平铺层；缺省 false ⇒ 节点不可加签，
    /// 行为与既有普通任务<b>逐字一致</b>）。非法值在解析期报错，不得静默落回缺省
    /// （规范 02 §4.3，纪律照 <see cref="ReapplyMode"/> 先例）。
    /// </summary>
    public bool AllowAddSign { get; set; }

    /// <summary>
    /// 后加签轮次上限（<c>properties.addSignMaxRounds</c>；0＝不限，缺省 0）。
    /// 超上限<b>报错不强制放行</b>——放行等于伪造"审批完成"（规范 04 · 后加签「语义边界」）。
    /// </summary>
    public int AddSignMaxRounds { get; set; }

    /// <summary>
    /// 已办撤回开关（<c>properties.allowRecall</c>，平铺层；缺省 false ⇒ 该节点办结后不可撤回）。
    /// 非法值在解析期报错，不得静默落回缺省（规范 02 §4.4，纪律照 <see cref="AllowAddSign"/> 先例）。
    /// 会签节点／申请节点配它＝解析期禁配（<see cref="ModelParser"/>）——前者撤"谁的一票"语义不清，
    /// 后者走实例撤回＋重新发起那条既有通道。
    /// </summary>
    public bool AllowRecall { get; set; }

    /// <summary>
    /// 动态会签（征询模式）开关（<c>properties.dynamicCountersign</c>，平铺层；缺省 false）。
    /// 办理人运行期拉人收意见：主办提交带 <c>tf_addSignOperators</c> 时<b>不办结主办行</b>、
    /// 按人建成员行，收齐后闸门才放开（规范 04 · 动态会签）。
    /// 与 <see cref="AllowAddSign"/> 互斥、会签节点／申请节点禁配（都在解析期报错，规范 02 §4.4）；
    /// 两档共用协议键 <c>tf_addSignOperators</c>，行为靠节点属性分流。
    /// </summary>
    public bool DynamicCountersign { get; set; }

    internal override async Task ExecAsync(Execution execution)
    {
        if (PerformType == WfPerformType.Countersign)
        {
            await new CountersignHandler(this).HandleAsync(execution);
            if (execution.IsMerged) await RunOutTransitionAsync(execution);
        }
        else if (AllowAddSign)
        {
            // 后加签档：推进闸交给 AddSignHandler 裁决（本次点人→同节点续建且不流转；
            // 没点人但本节点还有兄弟待办→不流转；两者皆否→放行出边）。
            await new AddSignHandler(this).HandleAsync(execution);
            if (execution.IsMerged) await RunOutTransitionAsync(execution);
        }
        else if (DynamicCountersign)
        {
            // 征询档（issues/247）：发起腿在门面（带 tf_addSignOperators 的提交不办结主办行，
            // 到不了这里）；能走进执行闸的只有成员行办结与主办行收齐后的拍板——两者复用
            // AddSignHandler 推进闸：主办还 DOING ⇒ merged=false 节点不动；主办办结且成员清零 ⇒ 放行。
            await new AddSignHandler(this).HandleAsync(execution);
            if (execution.IsMerged) await RunOutTransitionAsync(execution);
        }
        else
        {
            await RunOutTransitionAsync(execution);
        }
    }

    // ── 便捷字段（从 ext 中读取）──

    public string? CandidateUsers => Ext.TryGetStr("candidateUsers");

    public string? CandidateGroups => Ext.TryGetStr("candidateGroups");

    public string? CandidateHandlerName
    {
        get
        {
            if (CandidateHandler != null) return CandidateHandler;
            return Ext.TryGetStr("candidateHandler");
        }
    }
}

/// <summary>决策节点模型（对齐 Java DecisionModel：expr 求值/decisionHandler 命名边 + 表达式边）。</summary>
public class DecisionModel : NodeModel
{
    public string? Expr { get; set; }
    public string? HandleClass { get; set; }

    internal override async Task ExecAsync(Execution execution)
    {
        var found = false;
        string? nextNodeName = null;

        if (!string.IsNullOrEmpty(Expr))
        {
            var result = execution.Context.ExpressionEvaluatorOrDefault.Eval(Expr, execution.Args);
            nextNodeName = result?.ToString();
        }
        else if (!string.IsNullOrEmpty(HandleClass))
        {
            try
            {
                var handler = execution.Context.FindDecisionHandler(HandleClass.Trim());
                nextNodeName = await handler.DecideAsync(execution);
            }
            catch (Exception)
            {
                throw new JeeflowException(WfErr.NotFoundNextNode);
            }
        }

        foreach (var tm in Outputs)
        {
            if (!string.IsNullOrEmpty(tm.Expr))
            {
                var evaluator = execution.Context.ExpressionEvaluatorOrDefault;
                if (evaluator.Eval(tm.Expr, execution.Args) is true)
                {
                    found = true;
                    tm.Enabled = true;
                    await tm.ExecuteAsync(execution);
                    // issues/234 丙-9：首条真边获胜——命中即走（多真边并行流转非 spec 语义）
                    break;
                }
            }
            else if (tm.To != null && tm.To.Equals(nextNodeName, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                tm.Enabled = true;
                await tm.ExecuteAsync(execution);
                break;
            }
        }

        if (!found) throw new JeeflowException(WfErr.NotFoundNextNode);
    }
}

/// <summary>
/// 自定义节点模型（记录类节点，对齐 Java CustomModel）：clazz 按名解析 IHandler 执行。
/// <para><b>issues/142 · spec 02 §6.2 两条改判</b>（owner 2026-09-30，与 java 同步跟改）：</para>
/// <list type="number">
/// <item>历史行要<b>真落库</b>——过去只 append 进聚合根 <c>Tasks</c> 就丢弃返回值，
/// 而引擎 <c>PersistTasksAsync</c> 只保存 <c>ProcessTaskList</c>、
/// <c>UpdateInstanceAsync</c> 级联只对 <c>TaskId != null</c> 的行发 UPDATE ⇒ 那条 FINISHED(20)
/// 的行永远进不了 <c>wf_process_task</c>。现经 <see cref="Execution.HistoryTasks"/> 这条
/// <b>与码 3 解耦</b>的通道落库（记录类不该有待办，不许 fire TASK_START）。</item>
/// <item><c>clazz</c> 解析不了（空串／未注册）⇒ <b>记日志 + 照常落历史行 + 令牌继续</b>，
/// 不再抛错打断建单（旧形状同 java 的"自定义模型[class=…]实例化对象失败"，
/// 且本栈把两档合成同一个异常，覆盖面比 java 还宽）。
/// 处理器<b>自身</b>抛异常不在豁免内：照旧外抛，那是业务错误不是配置形状错。</item>
/// </list>
/// 反射方法调用路径为 Java 特有，C# 以注册表等价承载。
/// </summary>
public class CustomModel : NodeModel
{
    public string? Clazz { get; set; }
    public string? MethodName { get; set; }
    public string? Args { get; set; }
    public string? Var { get; set; }

    internal override async Task ExecAsync(Execution execution)
    {
        var clazz = Clazz?.Trim();
        if (string.IsNullOrEmpty(clazz))
        {
            // 档①：properties.clazz 压根没配（缺失／空串／纯空白）——配置形状问题，不打断建单。
            // 文案带节点 name ⇒ 与档②可分别诊断（spec §6.2 第 2 条明确要求两档分开）。
            execution.Context.LogWarning(
                $"自定义节点[name={Name}] 未配置 clazz（properties.clazz 缺失/空串/纯空白）：" +
                "跳过处理器执行，照常落记录类历史行，令牌继续流转（issues/142 · spec 02 §6.2 第 2 条）");
        }
        else if (!execution.Context.CustomHandlers.TryGetValue(clazz, out var handler))
        {
            // 档②：clazz 配了但注册表里没有（本栈按名注册，不像 java 反射 FQCN，
            // 沿用共享夹具的 com.mldong.* 类名时这一档最容易命中）——同样记日志继续。
            execution.Context.LogWarning(
                $"自定义节点[name={Name}] clazz=[{clazz}] 未注册处理器（本栈注册表＝" +
                "ServiceContext.CustomHandlers，按名解析）：跳过处理器执行，" +
                "照常落记录类历史行，令牌继续流转（issues/142 · spec 02 §6.2 第 2 条）");
        }
        else
        {
            // 档③：处理器解析到了 ⇒ 执行。它**自身**抛的异常不在 §6.2 豁免内（照旧外抛）：
            // 外部系统调用失败是业务错误，吞掉就等于把失败报成成功。
            await handler.HandleAsync(execution);
        }

        // 记录历史任务（建单不变量：parent＝刚办结的那个任务，发起 execution 没有则为 null⇒落 0；
        // 行级"首任务节点"标记随建单落 variable，血缘回退要用）。返回的历史行必须登记到
        // HistoryTasks 这条腿——只 append 进聚合根 Tasks 不算落库（issues/142 缺陷 1）。
        // ExpireTime 不在这里写：CustomModel 没有到期表达式可取（ModelParser 的 custom 档只解析
        // clazz/methodName/args/val），按 issues/126「节点没配 ⇒ 该列保持 NULL」正是应有形状，java 基准同形。
        var historyTask = execution.ProcessInstance!.CreateHistoryTask(this, execution.Operator,
            execution.ProcessTask?.TaskId,
            FlowUtil.IsFirstTaskName(execution.ProcessModel!, Name),
            execution.Context.ClockOrDefault);
        execution.AddHistoryTask(historyTask);

        await RunOutTransitionAsync(execution);
    }
}

/// <summary>分支节点模型（并行驱动全部输出边）。</summary>
public class ForkModel : NodeModel
{
    internal override Task ExecAsync(Execution execution) => RunOutTransitionAsync(execution);
}

/// <summary>合并节点模型（MergeBranchHandler：全部输入分支完成才 merged 沿边驱动）。</summary>
public class JoinModel : NodeModel
{
    internal override async Task ExecAsync(Execution execution)
    {
        await new MergeBranchHandler(this).HandleAsync(execution);
        if (execution.IsMerged) await RunOutTransitionAsync(execution);
    }
}

/// <summary>子流程节点模型（占位：启动子流程走 StartSubProcessHandler）。</summary>
public class SubProcessModel : NodeModel
{
    public string? Form { get; set; }
    public int? Version { get; set; }

    internal override Task ExecAsync(Execution execution) => RunOutTransitionAsync(execution);
}

/// <summary>自定义处理器接口（CustomModel.clazz 解析目标）。</summary>
public interface IHandler
{
    Task HandleAsync(Execution execution);
}

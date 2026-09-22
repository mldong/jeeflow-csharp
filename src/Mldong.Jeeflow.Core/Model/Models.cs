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
    {
        var interceptors = PreInterceptors;
        if (string.IsNullOrEmpty(interceptors))
            interceptors = execution.ProcessModel?.PreInterceptors;
        return RunInterceptorsAsync(interceptors, execution);
    }

    private Task ExecPostInterceptorsAsync(Execution execution)
    {
        var interceptors = PostInterceptors;
        if (string.IsNullOrEmpty(interceptors))
            interceptors = execution.ProcessModel?.PostInterceptors;
        return RunInterceptorsAsync(interceptors, execution);
    }

    /// <summary>按名解析拦截器并执行；声明名不可解析 → 显式错误（C20，不静默跳过）。</summary>
    private static async Task RunInterceptorsAsync(string? interceptors, Execution execution)
    {
        if (string.IsNullOrEmpty(interceptors)) return;
        foreach (var raw in interceptors.Split(','))
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
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
/// 任务节点模型（对齐 Java TaskModel）：会签 performType 走 CountersignHandler（merged 才沿边驱动）。
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

    internal override async Task ExecAsync(Execution execution)
    {
        if (PerformType == WfPerformType.Countersign)
        {
            await new CountersignHandler(this).HandleAsync(execution);
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
                }
            }
            else if (tm.To != null && tm.To.Equals(nextNodeName, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                tm.Enabled = true;
                await tm.ExecuteAsync(execution);
            }
        }

        if (!found) throw new JeeflowException(WfErr.NotFoundNextNode);
    }
}

/// <summary>
/// 自定义节点模型（对齐 Java CustomModel）：clazz 按名解析 IHandler 执行（C20 不可解析显式报错），
/// 记录 FINISHED 历史任务后沿输出边驱动。反射方法调用路径为 Java 特有，C# 以注册表等价承载。
/// </summary>
public class CustomModel : NodeModel
{
    public string? Clazz { get; set; }
    public string? MethodName { get; set; }
    public string? Args { get; set; }
    public string? Var { get; set; }

    internal override async Task ExecAsync(Execution execution)
    {
        var name = Clazz?.Trim();
        if (string.IsNullOrEmpty(name) ||
            !execution.Context.CustomHandlers.TryGetValue(name, out var handler))
            throw new JeeflowException($"自定义模型[class={Clazz}]实例化对象失败");
        await handler.HandleAsync(execution);
        // 记录历史任务（建单不变量：parent＝刚办结的那个任务，发起 execution 没有则为 null⇒落 0）
        execution.ProcessInstance!.CreateHistoryTask(this, execution.Operator,
            execution.ProcessTask?.TaskId,
            FlowUtil.IsFirstTaskName(execution.ProcessModel!, Name),
            execution.Context.ClockOrDefault);
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

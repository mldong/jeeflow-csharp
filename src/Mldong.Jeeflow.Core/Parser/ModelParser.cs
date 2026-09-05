namespace Mldong.Jeeflow.Core;

/// <summary>
/// 模型解析器——将 LogicFlow JSON 流程定义解析为 ProcessModel（对齐 Java ModelParser + 各 NodeParser）。
/// 注册表形态承载 Java 的 NodeParser SPI：node.type（snaker:*）→ 模型构造，零反射。
/// </summary>
public static class ModelParser
{
    public const string NodeNamePrefix = "snaker:";

    // 节点 properties 键（对齐 Java NodeParser 常量）
    public const string TextValueKey = "value";
    public const string WidthKey = "width";
    public const string HeightKey = "height";
    public const string PreInterceptorsKey = "preInterceptors";
    public const string PostInterceptorsKey = "postInterceptors";
    public const string ExprKey = "expr";
    public const string HandleClassKey = "handleClass";
    public const string FormKey = "form";
    public const string AssigneeKey = "assignee";
    public const string AssignmentHandleKey = "assignmentHandler";
    public const string TaskTypeKey = "taskType";
    public const string PerformTypeKey = "performType";
    public const string ReminderTimeKey = "reminderTime";
    public const string ReminderRepeatKey = "reminderRepeat";
    public const string ExpireTimeKey = "expireTime";
    public const string AuthExecuteKey = "autoExecute";
    public const string CallbackKey = "callback";
    public const string ExtFieldKey = "field";
    public const string CandidateUsersKey = "candidateUsers";
    public const string CandidateGroupsKey = "candidateGroups";
    public const string CandidateHandlerKey = "candidateHandler";
    public const string CountersignTypeKey = "countersignType";
    public const string CountersignCompletionConditionKey = "countersignCompletionCondition";
    public const string ClassKey = "clazz";
    public const string MethodNameKey = "methodName";
    public const string ArgsKey = "args";
    public const string ReturnValKey = "val";
    public const string VersionKey = "version";

    /// <summary>将流程定义 JSON 字节解析为流程模型。</summary>
    public static ProcessModel Parse(byte[]? bytes, ServiceContext context)
    {
        if (bytes == null || bytes.Length == 0)
            throw new JeeflowException("读取流程定义 JSON 失败");
        var json = context.JsonProviderOrDefault;
        string jsonStr;
        try
        {
            jsonStr = System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (Exception)
        {
            throw new JeeflowException("读取流程定义 JSON 失败");
        }
        if (json.FromJson(jsonStr) is not Dictionary<string, object?> root)
            return new ProcessModel();
        return Parse(root, context);
    }

    /// <summary>将流程定义对象图解析为流程模型。</summary>
    public static ProcessModel Parse(Dictionary<string, object?> root, ServiceContext context)
    {
        var processModel = new ProcessModel
        {
            Name = Str(root, "name"),
            DisplayName = Str(root, "displayName"),
            Type = Str(root, "type"),
            InstanceUrl = Str(root, "instanceUrl"),
            InstanceNoClass = Str(root, "instanceNoClass"),
            PostInterceptors = Str(root, "postInterceptors"),
            PreInterceptors = Str(root, "preInterceptors"),
            RelTableName = Str(root, "relTableName"),
            PersistMode = Str(root, "persistMode"),
        };
        if (root.TryGetValue("ext", out var extObj) && extObj is Dictionary<string, object?> extDict)
        {
            foreach (var kv in extDict) processModel.Ext[kv.Key] = kv.Value;
        }

        var nodes = root.TryGetValue("nodes", out var n) && n is List<object?> nl ? nl : null;
        var edges = root.TryGetValue("edges", out var e) && e is List<object?> el ? el : null;

        if (nodes == null || nodes.Count == 0 || edges == null || edges.Count == 0)
            return processModel;

        // 解析各节点（type 去掉 snaker: 前缀 → 分发）
        foreach (var nodeObj in nodes)
        {
            if (nodeObj is not Dictionary<string, object?> node) continue;
            var nodeModel = ParseNode(node, edges, context);
            if (nodeModel == null) continue;
            processModel.Nodes.Add(nodeModel);
            if (nodeModel is TaskModel taskModel) processModel.Tasks.Add(taskModel);
        }

        // 构造输入边、输出边的 source/target 引用
        foreach (var node in processModel.Nodes)
        {
            foreach (var transition in node.Outputs)
            {
                var to = transition.To;
                foreach (var node2 in processModel.Nodes)
                {
                    if (to != null && to.Equals(node2.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        node2.Inputs.Add(transition);
                        transition.Target = node2;
                    }
                }
            }
        }

        return processModel;
    }

    private static NodeModel? ParseNode(
        Dictionary<string, object?> node, List<object?> edges, ServiceContext context)
    {
        var id = Str(node, "id");
        var type = (Str(node, "type") ?? "").Replace(NodeNamePrefix, "");
        var nodeModel = type switch
        {
            "start" => new StartModel(),
            "end" => new EndModel(),
            "task" => new TaskModel(),
            "decision" => new DecisionModel(),
            "fork" => new ForkModel(),
            "join" => new JoinModel(),
            "custom" => new CustomModel(),
            "wfSubProcess" or "subProcess" => new SubProcessModel(),
            _ => (NodeModel?)null, // 未知类型：与 Java findByName=null 跳过一致
        };
        if (nodeModel == null) return null;

        // 基本属性
        nodeModel.Name = id;
        if (node.TryGetValue("text", out var textObj) && textObj is Dictionary<string, object?> text)
            nodeModel.DisplayName = Str(text, TextValueKey);

        var properties = node.TryGetValue("properties", out var p) && p is Dictionary<string, object?> pd
            ? pd
            : new Dictionary<string, object?>();

        // 布局属性
        var x = IntOf(node, "x");
        var y = IntOf(node, "y");
        var w = IntOf(properties, WidthKey);
        var h = IntOf(properties, HeightKey);
        nodeModel.Layout = $"{x},{y},{w},{h}";
        nodeModel.PreInterceptors = Str(properties, PreInterceptorsKey);
        nodeModel.PostInterceptors = Str(properties, PostInterceptorsKey);

        // 输出边
        foreach (var edgeObj in edges)
        {
            if (edgeObj is not Dictionary<string, object?> edge) continue;
            if (!string.Equals(Str(edge, "sourceNodeId"), id, StringComparison.Ordinal)) continue;
            var tm = new TransitionModel
            {
                Name = Str(edge, "id"),
                To = Str(edge, "targetNodeId"),
                Source = nodeModel,
            };
            if (edge.TryGetValue("properties", out var ep) && ep is Dictionary<string, object?> edgeProps)
                tm.Expr = Str(edgeProps, ExprKey);
            if (edge.TryGetValue("pointsList", out var pl) && pl is List<object?> points && points.Count > 0)
            {
                var parts = new List<string>();
                foreach (var pt in points)
                    if (pt is Dictionary<string, object?> point)
                        parts.Add($"{IntOf(point, "x")},{IntOf(point, "y")}");
                tm.G = string.Join(";", parts);
            }
            else if (edge.TryGetValue("startPoint", out var sp) && sp is Dictionary<string, object?> startPoint
                     && edge.TryGetValue("endPoint", out var ep2) && ep2 is Dictionary<string, object?> endPoint)
            {
                tm.G = $"{IntOf(startPoint, "x")},{IntOf(startPoint, "y")};{IntOf(endPoint, "x")},{IntOf(endPoint, "y")}";
            }
            nodeModel.Outputs.Add(tm);
        }

        // 类型特定属性
        switch (nodeModel)
        {
            case TaskModel task:
                task.Form = Str(properties, FormKey);
                task.Assignee = Str(properties, AssigneeKey);
                task.AssignmentHandler = Str(properties, AssignmentHandleKey);
                task.TaskType = Enums.TaskTypeCodeOf(Obj(properties, TaskTypeKey));
                task.PerformType = Enums.PerformTypeCodeOf(Obj(properties, PerformTypeKey));
                task.ReminderTime = Str(properties, ReminderTimeKey);
                task.ReminderRepeat = Str(properties, ReminderRepeatKey);
                task.ExpireTime = Str(properties, ExpireTimeKey);
                task.AutoExecute = Str(properties, AuthExecuteKey);
                task.Callback = Str(properties, CallbackKey);
                task.CandidateHandler = Str(properties, CandidateHandlerKey);
                task.CountersignType = Enums.CountersignTypeCodeOf(Str(properties, CountersignTypeKey));
                task.CountersignCompletionCondition = Str(properties, CountersignCompletionConditionKey);
                // 候选人（v1.6.0 对齐 Go/Python/Node：顶层 candidateUsers/candidateGroups）
                var candUsers = Str(properties, CandidateUsersKey);
                var candGroups = Str(properties, CandidateGroupsKey);
                if (candUsers != null) task.Ext[CandidateUsersKey] = candUsers;
                if (candGroups != null) task.Ext[CandidateGroupsKey] = candGroups;
                // field 扩展（字段权限/会签属性优先从 ext 取）
                if (properties.TryGetValue(ExtFieldKey, out var fieldObj))
                {
                    if (fieldObj is Dictionary<string, object?> field)
                    {
                        foreach (var kv in field) task.Ext[kv.Key] = kv.Value;
                        if (field.TryGetValue(CandidateHandlerKey, out var ch) && ch != null)
                            task.CandidateHandler = ch.ToString();
                        if (field.TryGetValue(CountersignTypeKey, out var cst) && cst != null)
                            task.CountersignType = Enums.CountersignTypeCodeOf(cst);
                        if (field.TryGetValue(CountersignCompletionConditionKey, out var csc) && csc != null)
                            task.CountersignCompletionCondition = csc.ToString();
                    }
                }
                // 其余 properties（非 TaskModel 字段）进 ext
                foreach (var key in properties.Keys)
                {
                    if (!TaskModelPropertyKeys.Contains(key)) task.Ext[key] = Obj(properties, key);
                }
                break;
            case DecisionModel decision:
                decision.Expr = Str(properties, ExprKey);
                decision.HandleClass = Str(properties, HandleClassKey);
                break;
            case CustomModel custom:
                custom.Clazz = Str(properties, ClassKey);
                custom.MethodName = Str(properties, MethodNameKey);
                custom.Args = Str(properties, ArgsKey);
                custom.Var = Str(properties, ReturnValKey) ?? FlowConst.CustomReturnVal;
                break;
            case SubProcessModel sub:
                sub.Form = Str(properties, FormKey);
                sub.Version = IntOf(properties, VersionKey);
                break;
        }
        return nodeModel;
    }

    /// <summary>TaskModel 具名属性键（其余 properties 进 ext——对齐 Java hasField 语义）。</summary>
    private static readonly HashSet<string> TaskModelPropertyKeys = new()
    {
        FormKey, AssigneeKey, AssignmentHandleKey, TaskTypeKey, PerformTypeKey,
        ReminderTimeKey, ReminderRepeatKey, ExpireTimeKey, AuthExecuteKey, CallbackKey,
        CandidateHandlerKey, CandidateUsersKey, CandidateGroupsKey,
        CountersignTypeKey, CountersignCompletionConditionKey, ExtFieldKey,
    };

    private static string? Str(Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static object? Obj(Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) ? v : null;

    private static int IntOf(Dictionary<string, object?> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || v == null) return 0;
        if (v is int i) return i;
        if (v is long l) return (int)l;
        if (v is double db) return (int)db;
        return int.TryParse(v.ToString(), out var parsed) ? parsed : 0;
    }
}

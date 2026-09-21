namespace Mldong.Jeeflow.Core;

/// <summary>单个字典项。</summary>
public sealed record DictItem(string Value, string Label);

/// <summary>
/// 引擎内置枚举字典注册表（v1.4.0，C24）：7 键 wf_+枚举下划线，
/// value=code（数字），label=message；未知 key 返回空列表。
/// </summary>
public class EnumDictRegistry
{
    public const string DictDefineState = "wf_process_define_state";
    public const string DictInstanceState = "wf_process_instance_state";
    public const string DictSubmitType = "wf_process_submit_type";
    public const string DictTaskState = "wf_process_task_state";
    public const string DictTaskType = "wf_process_task_type";
    public const string DictTaskPerformType = "wf_process_task_perform_type";
    public const string DictCountersignType = "wf_countersign_type";

    private static readonly Dictionary<string, List<DictItem>> Dicts = new()
    {
        [DictDefineState] = FromEnum(
            (0, "禁用"), (1, "启用")),
        [DictInstanceState] = FromEnum(
            (10, "进行中"), (20, "已完成"), (30, "已撤回"), (40, "强行终止"),
            (45, "已拒绝"), (50, "挂起"), (99, "已废弃")),
        [DictSubmitType] = FromEnum(
            (0, "发起申请"), (1, "同意申请"), (2, "拒绝申请"), (3, "退回上一步"),
            (4, "跳转"), (5, "重新提交"), (6, "退回发起人"), (7, "转办"), (20, "会签拒绝")),
        [DictTaskState] = FromEnum(
            (10, "进行中"), (20, "已完成"), (30, "已撤回"), (40, "强行终止"),
            (50, "挂起"), (99, "已废弃")),
        [DictTaskType] = FromEnum(
            (0, "主办"), (1, "协办"), (2, "记录")),
        [DictTaskPerformType] = FromEnum(
            (0, "普通参与"), (1, "会签参与")),
        [DictCountersignType] = FromEnum(
            (0, "并行会签"), (1, "串行会签")),
    };

    private static List<DictItem> FromEnum(params (int Code, string Message)[] items) =>
        items.Select(i => new DictItem(i.Code.ToString(), i.Message)).ToList();

    /// <summary>内置枚举字典 key 清单。</summary>
    public List<string> ListDictKeys() => Dicts.Keys.ToList();

    /// <summary>按 key 取字典（[{value, label}]），未知 key 返回空列表。</summary>
    public List<DictItem> GetDict(string key) =>
        Dicts.TryGetValue(key, out var items) ? items : new List<DictItem>();
}

/// <summary>处理器元数据（SPI 实现清单字典源）。</summary>
public sealed record HandlerMeta(
    string Type,
    string ClassName,
    string DisplayName,
    int Order,
    string? Group = null)
{
    public string TypeName => Type;
}

/// <summary>
/// 处理器注册中心（v1.4.0，C24）：注册名=Java FQCN（四语言一致），构造即注册内置通用 handler。
/// </summary>
public class HandlerRegistry
{
    private readonly Dictionary<string, List<HandlerMeta>> _handlers = new();

    public HandlerRegistry() => RegisterBuiltins();

    /// <summary>内置通用参与者 handler 元数据（注册名 = 内置类全限定名，四语言一致）。</summary>
    private void RegisterBuiltins()
    {
        const string assignment = "AssignmentHandler";
        Register(assignment, "com.mldong.jeeflow.interceptor.impl.OperatorAssignmentHandler",
            "流程发起人", -9999);
        Register(assignment, "com.mldong.jeeflow.interceptor.impl.OrgUserAssignmentHandlers$ApplicantDeptLeaderAssignmentHandler",
            "发起人所属部门经理", 10);
        Register(assignment, "com.mldong.jeeflow.interceptor.impl.OrgUserAssignmentHandlers$ApplicantDeptMainLeaderAssignmentHandler",
            "发起人所属部门分管领导", 20);
        Register(assignment, "com.mldong.jeeflow.interceptor.impl.OrgUserAssignmentHandlers$DeptLeaderAssignmentHandler",
            "当前用户所属部门经理", 30);
        Register(assignment, "com.mldong.jeeflow.interceptor.impl.OrgUserAssignmentHandlers$DeptMainLeaderAssignmentHandler",
            "当前用户所属部门分管领导", 40);
        Register(assignment, "com.mldong.jeeflow.interceptor.impl.FormFieldAssigneeHandler",
            "根据表单字段值分配参与者", 50);
        Register(assignment, "com.mldong.jeeflow.interceptor.impl.OrgUserAssignmentHandlers$TaskRoleAssigneeHandler",
            "根据任务节点唯一编码关联角色分配参与者", 60);
        // issues/29：action 权限码映射 SPI 清单
        Register("IActionPermissionProvider", "com.mldong.jeeflow.spi.DefaultActionPermissionProvider",
            "action 权限码默认映射（boot3 注解语义归纳）", 0);
    }

    public void Register(HandlerMeta meta)
    {
        if (!_handlers.TryGetValue(meta.Type, out var list))
        {
            list = new List<HandlerMeta>();
            _handlers[meta.Type] = list;
        }
        list.Add(meta);
    }

    public void Register(string type, string className, string displayName, int order, string? group = null) =>
        Register(new HandlerMeta(type, className, displayName, order, group));

    public void RegisterAll(IEnumerable<HandlerMeta> metas)
    {
        foreach (var meta in metas) Register(meta);
    }

    /// <summary>按处理器类型列出可用实现（按 order 升序）。</summary>
    public List<HandlerMeta> ListHandlers(string type)
    {
        var list = _handlers.TryGetValue(type, out var l) ? new List<HandlerMeta>(l) : new List<HandlerMeta>();
        list.Sort((a, b) => a.Order.CompareTo(b.Order));
        return list;
    }

    /// <summary>按处理器类型 + 分组列出。</summary>
    public List<HandlerMeta> ListHandlers(string type, string? group)
    {
        var result = _handlers.TryGetValue(type, out var l)
            ? l.Where(m => group == null || group.Equals(m.Group)).ToList()
            : new List<HandlerMeta>();
        result.Sort((a, b) => a.Order.CompareTo(b.Order));
        return result;
    }

    /// <summary>已注册的处理器接口类型清单。</summary>
    public List<string> ListHandlerTypes() => _handlers.Keys.ToList();
}

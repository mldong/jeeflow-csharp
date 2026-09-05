using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Persist;

/// <summary>
/// 工作流业务数据入库适配拦截器（issues/18，1.8.0 SYNC 同步演进，T9 全规则）：
/// <list type="bullet">
/// <item><b>ARCHIVE（缺省）</b>：结束归档——FINISHED + submitType=AGREE 时 INSERT（幂等=按
/// process_instance_id 先 exists；C16 同链双触发=节点级标记+exists 兜底）</item>
/// <item><b>SYNC</b>：发起 INSERT → 任务节点 UPDATE（f_ 按字段权限过滤 + tf_ 冗余 + 状态字段
/// 优先 {节点ID}_{状态码} 列）→ 结束 UPDATE 定稿（同意/驳回都入库）</item>
/// </list>
/// 字段权限双格式键（C19/issues/25）：PERMISSION_f_{field} 优先 / PERMISSION_{stripped} 兼容，
/// 只读(1)/隐藏(3)不参与写入。writer 未注入：静默跳过（未配置 ≠ 错误）。
/// </summary>
public class PersistPostInterceptor : IFlowInterceptor
{
    public const string FieldPrefix = "f_";
    public const string TaskFieldPrefix = "tf_";
    public const string PersistModeSync = "SYNC";
    public const string PermissionPrefix = "PERMISSION_";
    public const int PermReadOnly = 1;
    public const int PermEdit = 2;
    public const int PermHidden = 3;

    /// <summary>SPI 清单字典显示名（wf_flow_interceptor_post_process）。</summary>
    public const string MetaDisplayName = "业务数据自动入库";
    public const int MetaOrder = 0;
    public const string MetaGroup = "post";
    /// <summary>注册名（=Java 类全名口径）。</summary>
    public const string MetaClassName = "com.mldong.jeeflow.persist.interceptor.PersistPostInterceptor";

    private IDynamicTableWriter? _writer;

    public PersistPostInterceptor SetWriter(IDynamicTableWriter writer)
    {
        _writer = writer;
        return this;
    }

    /// <summary>注册助手（issues/60）：元数据注册中心一行登记，保证"字典有 ⟺ 实例有"。</summary>
    public static void RegisterMeta(HandlerRegistry registry) =>
        registry.Register("FlowInterceptor", MetaClassName, MetaDisplayName, MetaOrder, MetaGroup);

    public Task InterceptAsync(Execution execution)
    {
        if (_writer == null) return Task.CompletedTask; // 未注入 writer：静默跳过
        var instance = execution.ProcessInstance;
        if (instance == null) return Task.CompletedTask;
        var mode = execution.ProcessModel?.PersistMode;
        if (PersistModeSync.Equals(mode, StringComparison.OrdinalIgnoreCase))
        {
            return InterceptSyncAsync(execution, instance);
        }
        return InterceptArchiveAsync(execution, instance);
    }

    // ─── ARCHIVE（缺省：结束归档）───

    private async Task InterceptArchiveAsync(Execution execution, ProcessInstance instance)
    {
        // 时机：仅流程正常结束（FINISHED）且同意
        if (instance.State != (int)WfInstanceState.Finished) return;
        var submitType = ToInt(execution.Args.GetObj(FlowConst.SubmitType));
        if (submitType == null || submitType != (int)WfSubmitType.Agree) return;
        var tableName = ResolveTableName(execution);
        if (tableName == null) return;
        if (!MarkChain(execution, instance)) return; // 同链重复触发防护（节点级）
        // 幂等：以 process_instance_id 为键，先查后插（C16）
        if (await _writer!.ExistsAsync(tableName, "process_instance_id", instance.InstanceId)) return;

        var data = ExtractFields(instance, null, includeTaskFields: false, includeFormFields: true);
        FillContext(data, instance);
        _writer.FillSystemFields(data, insert: true);
        await _writer.InsertAsync(tableName, data);
    }

    // ─── SYNC（发起 INSERT → 节点 UPDATE → 结束定稿）───

    private async Task InterceptSyncAsync(Execution execution, ProcessInstance instance)
    {
        var tableName = ResolveTableName(execution);
        if (tableName == null) return;
        if (!MarkChain(execution, instance)) return;
        var exists = await _writer!.ExistsAsync(tableName, "process_instance_id", instance.InstanceId);

        // 任务节点才更新业务字段：f_ 按字段权限过滤；非任务节点（如结束）只定稿状态
        var taskNode = execution.NodeModel is TaskModel;
        var fieldPerm = taskNode ? ResolveFieldPermission(execution) : null;
        var data = ExtractFields(instance,
            !exists ? null : fieldPerm,
            includeTaskFields: !exists || taskNode,
            includeFormFields: !exists || taskNode);

        // 状态字段：优先 {节点ID}_{状态码} 列，无则 {节点ID} 列。
        // 任务节点写 DOING(10)；结束节点写最终状态（FINISHED/REJECT）。
        var nodeId = execution.NodeModel?.Name;
        var stateCode = taskNode ? (int)WfInstanceState.Doing : instance.State;
        await PutStateFieldAsync(tableName, data, nodeId, stateCode);

        FillContext(data, instance);
        if (!exists)
        {
            _writer.FillSystemFields(data, insert: true);
            await _writer.InsertAsync(tableName, data);
        }
        else
        {
            _writer.FillSystemFields(data, insert: false);
            await _writer.UpdateAsync(tableName, data, "process_instance_id", instance.InstanceId);
        }
    }

    // ─── 公共 ───

    /// <summary>表名：relTableName 缺省回落流程 name。</summary>
    private static string? ResolveTableName(Execution execution)
    {
        var model = execution.ProcessModel;
        if (model == null) return null;
        var tableName = model.RelTableName;
        if (string.IsNullOrWhiteSpace(tableName)) tableName = model.Name;
        return string.IsNullOrWhiteSpace(tableName) ? null : tableName.Trim();
    }

    /// <summary>同链重复触发防护（issues/19，节点级）：同节点不重复；exists 兜底跨请求（C16）。</summary>
    private static bool MarkChain(Execution execution, ProcessInstance instance)
    {
        var node = execution.NodeModel;
        var chainKey = "__persist_executed_" + instance.InstanceId + "_"
                       + (node?.Name ?? "");
        if (true.Equals(execution.Args.GetObj(chainKey))) return false;
        execution.Args[chainKey] = true;
        return true;
    }

    /// <summary>字段权限（任务节点 properties.field 的 PERMISSION_x；缺省 null=全部可编辑）。</summary>
    private static FlowData? ResolveFieldPermission(Execution execution)
    {
        if (execution.NodeModel is TaskModel tm)
        {
            var ext = tm.Ext;
            if (ext != null && ext.Count > 0) return ext;
        }
        return null;
    }

    /// <summary>提取字段：f_ 去前缀（SYNC 按权限过滤）；tf_ 去前缀冗余（有列则写）。</summary>
    private static Dictionary<string, object?> ExtractFields(
        ProcessInstance instance, FlowData? fieldPerm, bool includeTaskFields, bool includeFormFields)
    {
        var data = new Dictionary<string, object?>();
        var variables = instance.Variables;
        if (variables != null)
        {
            foreach (var kv in variables)
            {
                var key = kv.Key;
                if (key == null) continue;
                if (includeFormFields && key.StartsWith(FieldPrefix, StringComparison.Ordinal)
                                       && key.Length > FieldPrefix.Length)
                {
                    var fieldName = key[FieldPrefix.Length..];
                    if (!IsEditable(fieldPerm, fieldName)) continue; // C19：只读/隐藏不入库
                    data[fieldName] = kv.Value;
                }
                else if (includeTaskFields && key.StartsWith(TaskFieldPrefix, StringComparison.Ordinal)
                                             && key.Length > TaskFieldPrefix.Length)
                {
                    data[key[TaskFieldPrefix.Length..]] = kv.Value;
                }
            }
        }
        return data;
    }

    /// <summary>字段可编辑判定：无声明或 EDIT(2) 可更新；READ_ONLY(1)/HIDDEN(3) 不更新。双格式键（issues/25）。</summary>
    private static bool IsEditable(FlowData? fieldPerm, string fieldName)
    {
        if (fieldPerm == null || fieldPerm.Count == 0) return true;
        if (!fieldPerm.TryGetValue(PermissionPrefix + FieldPrefix + fieldName, out var p))
            fieldPerm.TryGetValue(PermissionPrefix + fieldName, out p);
        if (p == null) return true;
        var perm = ToInt(p);
        return perm == PermEdit;
    }

    /// <summary>状态字段写入：优先 {节点ID}_{状态码} 列，无则 {节点ID} 列（列探测过滤）。</summary>
    private async Task PutStateFieldAsync(string tableName, Dictionary<string, object?> data, string? nodeId, int? stateCode)
    {
        if (nodeId == null || stateCode == null) return;
        var kept = await _writer!.FilterColumnsAsync(tableName,
            new[] { $"{nodeId}_{stateCode}", nodeId });
        if (kept.Count > 0) data[kept[0]] = stateCode;
    }

    /// <summary>流程上下文字段（蛇形列名约定）。</summary>
    private static void FillContext(Dictionary<string, object?> data, ProcessInstance instance)
    {
        data.TryAdd("process_instance_id", instance.InstanceId);
        data.TryAdd("apply_user_id", instance.Operator); // C17：系统字段优先 apply_user_id
        data.TryAdd("apply_dept_id", instance.Variables?.GetObj("u_deptId"));
    }

    private static int? ToInt(object? v)
    {
        if (v == null) return null;
        if (v is int i) return i;
        if (v is long l) return (int)l;
        return int.TryParse(v.ToString(), out var parsed) ? parsed : null;
    }
}

using System.Text.RegularExpressions;

namespace Mldong.Jeeflow.Core;

/// <summary>
/// 内置参与者处理器（issues/16，C29）：注册名=Java FQCN（HandlerRegistry 同清单）。
/// 数据来源 IOrgUserProvider / IUserProvider SPI——未注册时相关 handler 返回 null（静默返空）。
/// </summary>
public static class BuiltinAssignmentHandlers
{
    /// <summary>流程发起人（对齐 com.mldong.jeeflow.interceptor.impl.OperatorAssignmentHandler）。</summary>
    public sealed class OperatorAssignmentHandler : IAssignmentHandler
    {
        public Task<string?> AssignAsync(Execution execution)
        {
            var op = execution.ProcessInstance?.Operator;
            return Task.FromResult<string?>(string.IsNullOrEmpty(op) ? "apply.operator" : op);
        }
    }

    /// <summary>按表单字段值分配参与者（对齐 FormFieldAssigneeHandler，issues/16/48）。</summary>
    public sealed class FormFieldAssigneeHandler : IAssignmentHandler
    {
        private static readonly Regex NumberSuffixPattern = new("^(.+?)_(\\d+)$", RegexOptions.Compiled);

        public Task<string?> AssignAsync(Execution execution)
        {
            var ids = new List<string>();
            var currentTaskName = execution.NodeModel?.Name;
            if (currentTaskName == null) return Task.FromResult<string?>(null);
            var args = execution.Args;
            if (args.Count == 0) return Task.FromResult<string?>(null);
            var fieldValue = FindFieldValue(args, currentTaskName);
            if (fieldValue == null) return Task.FromResult<string?>(null);
            Collect(fieldValue, ids);
            return Task.FromResult<string?>(ids.Count == 0 ? null : string.Join(",", ids));
        }

        private static object? FindFieldValue(FlowData args, string taskName)
        {
            // issues/48 E20：f_ 前缀优先，再回落裸名，再 _数字 后缀剥离
            if (args.TryGetValue("f_" + taskName, out var v)) return v;
            if (args.TryGetValue(taskName, out var v2)) return v2;
            var m = NumberSuffixPattern.Match(taskName);
            if (m.Success && args.TryGetValue(m.Groups[1].Value, out var v3)) return v3;
            return null;
        }

        private static void Collect(object fieldValue, List<string> ids)
        {
            if (fieldValue is System.Collections.ICollection coll)
            {
                foreach (var item in coll) Add(ids, item);
            }
            else
            {
                Add(ids, fieldValue);
            }
        }

        private static void Add(List<string> ids, object? v)
        {
            if (v == null) return;
            foreach (var token in v.ToString()!.Split(','))
            {
                var t = token.Trim();
                if (t.Length > 0 && !ids.Contains(t)) ids.Add(t);
            }
        }
    }

    /// <summary>当前用户（任务操作人）部门领导。</summary>
    public sealed class DeptLeaderAssignmentHandler : IAssignmentHandler
    {
        public async Task<string?> AssignAsync(Execution execution)
        {
            var deptId = await DeptIdOfAsync(execution.Operator, execution);
            return await ByDeptAsync(deptId, false, execution.Context.OrgUserProvider);
        }
    }

    /// <summary>当前用户（任务操作人）部门分管领导。</summary>
    public sealed class DeptMainLeaderAssignmentHandler : IAssignmentHandler
    {
        public async Task<string?> AssignAsync(Execution execution)
        {
            var deptId = await DeptIdOfAsync(execution.Operator, execution);
            return await ByDeptAsync(deptId, true, execution.Context.OrgUserProvider);
        }
    }

    /// <summary>发起人部门领导。</summary>
    public sealed class ApplicantDeptLeaderAssignmentHandler : IAssignmentHandler
    {
        public async Task<string?> AssignAsync(Execution execution)
        {
            var deptId = await DeptIdOfAsync(execution.ProcessInstance?.Operator, execution);
            return await ByDeptAsync(deptId, false, execution.Context.OrgUserProvider);
        }
    }

    /// <summary>发起人部门分管领导。</summary>
    public sealed class ApplicantDeptMainLeaderAssignmentHandler : IAssignmentHandler
    {
        public async Task<string?> AssignAsync(Execution execution)
        {
            var deptId = await DeptIdOfAsync(execution.ProcessInstance?.Operator, execution);
            return await ByDeptAsync(deptId, true, execution.Context.OrgUserProvider);
        }
    }

    /// <summary>任务节点唯一编码关联角色（roleCode = 节点 name）。</summary>
    public sealed class TaskRoleAssigneeHandler : IAssignmentHandler
    {
        public async Task<string?> AssignAsync(Execution execution)
        {
            var roleCode = execution.NodeModel?.Name;
            if (roleCode == null) return null;
            var org = execution.Context.OrgUserProvider;
            if (org == null) return null;
            var ids = await org.FindByRoleAsync(roleCode);
            return ids == null || ids.Count == 0 ? null : string.Join(",", ids);
        }
    }

    /// <summary>用户 deptId：operator → IUserProvider.getUser。</summary>
    private static async Task<string?> DeptIdOfAsync(string? userId, Execution execution)
    {
        if (string.IsNullOrEmpty(userId)) return null;
        var userProvider = execution.Context.UserProvider;
        if (userProvider == null) return null;
        var u = await userProvider.GetUserAsync(userId);
        return u?.DeptId;
    }

    /// <summary>按部门取领导/分管领导并集串（org SPI 未注册/部门为空时返 null）。</summary>
    private static async Task<string?> ByDeptAsync(string? deptId, bool main, IOrgUserProvider? org)
    {
        if (string.IsNullOrEmpty(deptId) || org == null) return null;
        var ids = main ? await org.FindDeptMainLeadersAsync(deptId) : await org.FindDeptLeadersAsync(deptId);
        return ids == null || ids.Count == 0 ? null : string.Join(",", ids);
    }
}

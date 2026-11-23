using System.Text.RegularExpressions;

namespace Mldong.Jeeflow.Core;

/// <summary>
/// 内置参与者处理器（issues/16，C29）：注册名=Java FQCN（HandlerRegistry 同清单）。
/// 数据来源 IOrgUserProvider / IUserProvider SPI——未注册时相关 handler 返回 null（静默返空）。
/// </summary>
public static class BuiltinAssignmentHandlers
{
    /// <summary>表单字段编号后缀（task_01 → task），供「表单指定用户」两档并集查找复用。</summary>
    internal static readonly Regex NumberSuffix = new("^(.+?)_(\\d+)$", RegexOptions.Compiled);
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

    /// <summary>
    /// 表单指定用户所属部门的经理。场景（参照 mldong-fastapi-wf 提交 fe9233d）：HR 代员工提交、
    /// 代理申请——表单里给出被代理人的用户 id，取**该员工**所属部门的经理，与当前操作人无关。
    /// </summary>
    public sealed class FormUserDeptLeaderAssignmentHandler : IAssignmentHandler
    {
        public async Task<string?> AssignAsync(Execution execution)
        {
            var userId = FirstFormUserId(FindFormUserFieldValue(execution.Args, execution.NodeModel?.Name));
            return await ByDeptAsync(await DeptIdOfAsync(userId, execution), false, execution.Context.OrgUserProvider);
        }
    }

    /// <summary>表单指定用户所属部门的分管领导（取人口径同上，走 mainLeaders 出口）。</summary>
    public sealed class FormUserDeptMainLeaderAssignmentHandler : IAssignmentHandler
    {
        public async Task<string?> AssignAsync(Execution execution)
        {
            var userId = FirstFormUserId(FindFormUserFieldValue(execution.Args, execution.NodeModel?.Name));
            return await ByDeptAsync(await DeptIdOfAsync(userId, execution), true, execution.Context.OrgUserProvider);
        }
    }

    /// <summary>
    /// 两个「表单指定用户」handler 专用的四档并集查找（顺序即优先级）：
    /// 1. <c>f_&lt;nodeKey&gt;</c> 2. <c>&lt;nodeKey&gt;</c>
    /// 3. 含 <c>#</c> 时取最后一个 # 之后的 field：先裸名、再 f_ 变体
    /// 4. 匹配 <c>^(.+?)_(\d+)$</c> 时取 base：先 f_ 变体、再裸名
    ///
    /// 与 <see cref="FormFieldAssigneeHandler"/> 的 <c>FindFieldValue</c> 的差别只在 3、4 两档各补了 f_ 变体
    /// （参照实现没有 f_ 分支，jeeflow 家族变量存 f_ 必须有）。现存 handler 仍走它们自己的查找。
    /// </summary>
    internal static object? FindFormUserFieldValue(FlowData args, string? nodeKey)
    {
        if (string.IsNullOrEmpty(nodeKey) || args.Count == 0) return null;
        if (args.TryGetValue("f_" + nodeKey, out var prefixed)) return prefixed;
        if (args.TryGetValue(nodeKey, out var bare)) return bare;
        var hash = nodeKey.LastIndexOf('#');
        if (hash >= 0)
        {
            var field = nodeKey[(hash + 1)..];
            if (field.Length > 0 && args.TryGetValue(field, out var rightBare)) return rightBare;
            if (field.Length > 0 && args.TryGetValue("f_" + field, out var rightPrefixed)) return rightPrefixed;
        }
        var m = NumberSuffix.Match(nodeKey);
        if (m.Success)
        {
            var baseName = m.Groups[1].Value;
            if (args.TryGetValue("f_" + baseName, out var basePrefixed)) return basePrefixed;
            if (args.TryGetValue(baseName, out var baseBare)) return baseBare;
        }
        return null;
    }

    /// <summary>
    /// 取**第一个**非空用户 id（逗号串／集合同样只取第一个，多值语义不在本轮承诺）。
    /// 判空只比空串 ⇒ "0" 是合法 id（不被真值判据吃掉）。
    /// </summary>
    internal static string? FirstFormUserId(object? value)
    {
        if (value == null) return null;
        var tokens = value is System.Collections.ICollection coll
            ? coll.Cast<object?>().Select(x => x?.ToString())
            : value.ToString()!.Split(',');
        foreach (var token in tokens)
        {
            var id = (token ?? "").Trim();
            if (id.Length > 0) return id;
        }
        return null;
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

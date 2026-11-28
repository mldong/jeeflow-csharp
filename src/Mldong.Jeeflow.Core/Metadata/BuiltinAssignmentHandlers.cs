using System.Text.RegularExpressions;

namespace Mldong.Jeeflow.Core;

/// <summary>
/// 内置参与者处理器（issues/16，C29）：注册名=Java FQCN（HandlerRegistry 同清单）。
/// 数据来源 IOrgUserProvider / IPostUserProvider / IUserProvider SPI——未注册时相关 handler 返回 null（静默返空）。
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

    /// <summary>任务节点唯一编码关联岗位（postCode = 节点 name，issues/259）。</summary>
    public sealed class TaskPostAssigneeHandler : IAssignmentHandler
    {
        public async Task<string?> AssignAsync(Execution execution)
        {
            var postCode = execution.NodeModel?.Name;
            if (string.IsNullOrEmpty(postCode)) return null;
            var provider = execution.Context?.PostUserProvider;
            if (provider == null) return null;
            List<string>? ids;
            try
            {
                ids = await provider.FindByPostAsync(postCode);
            }
            catch
            {
                return null;
            }
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

    /// <summary>表单指定 1–N 个部门，取这些部门的负责人。</summary>
    public sealed class FormDeptLeaderAssignmentHandler : IAssignmentHandler
    {
        public Task<string?> AssignAsync(Execution execution) => ResolveFormDeptAsync(execution, false);
    }

    /// <summary>表单指定 1–N 个部门，取这些部门的分管领导。</summary>
    public sealed class FormDeptMainLeaderAssignmentHandler : IAssignmentHandler
    {
        public Task<string?> AssignAsync(Execution execution) => ResolveFormDeptAsync(execution, true);
    }

    /// <summary>表单指定 1–N 个岗位，取这些岗位的成员（issues/259）。</summary>
    public sealed class FormPostAssignmentHandler : IAssignmentHandler
    {
        public Task<string?> AssignAsync(Execution execution) => ResolveFormPostAsync(execution);
    }

    /// <summary>
    /// issues/258：字段值 → 1–N 个部门 ID → 各部门负责人/分管领导。
    /// 部门按首次出现顺序逐个查询，SPI 返回顺序追加，最终 userId 去重；
    /// 缺节点、缺字段、空值、缺 SPI、部门无领导均为空且不抛。
    /// </summary>
    private static async Task<string?> ResolveFormDeptAsync(Execution execution, bool main)
    {
        var nodeKey = execution.NodeModel?.Name;
        if (string.IsNullOrEmpty(nodeKey) || execution.Args == null) return null;
        var deptIds = ParseFormDeptIds(FindFormUserFieldValue(execution.Args, nodeKey));
        if (deptIds.Count == 0) return null;
        var org = execution.Context?.OrgUserProvider;
        if (org == null) return null;

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var deptId in deptIds)
        {
            List<string>? leaders;
            try
            {
                leaders = main
                    ? await org.FindDeptMainLeadersAsync(deptId)
                    : await org.FindDeptLeadersAsync(deptId);
            }
            catch
            {
                continue;
            }
            if (leaders == null) continue;
            foreach (var leader in leaders)
            {
                if (leader == null) continue;
                var id = leader.Trim();
                if (id.Length > 0 && seen.Add(id)) result.Add(id);
            }
        }
        return result.Count == 0 ? null : string.Join(",", result);
    }

    /// <summary>
    /// issues/259：字段值 → 1–N 个岗位编码 → 各岗位成员。
    /// 岗位按首次出现顺序逐个查询，SPI 返回顺序追加，最终 userId trim 后去重；
    /// 缺节点、缺字段、空值、缺 SPI、岗位无成员均为空且不抛，单岗位异常只跳过该岗位。
    /// </summary>
    private static async Task<string?> ResolveFormPostAsync(Execution execution)
    {
        var nodeKey = execution.NodeModel?.Name;
        if (string.IsNullOrEmpty(nodeKey) || execution.Args == null) return null;
        var postCodes = ParseFormPostCodes(FindFormUserFieldValue(execution.Args, nodeKey));
        if (postCodes.Count == 0) return null;
        var provider = execution.Context?.PostUserProvider;
        if (provider == null) return null;

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var postCode in postCodes)
        {
            List<string>? userIds;
            try
            {
                userIds = await provider.FindByPostAsync(postCode);
            }
            catch
            {
                continue;
            }
            if (userIds == null) continue;
            foreach (var userId in userIds)
            {
                if (userId == null) continue;
                var id = userId.Trim();
                if (id.Length > 0 && seen.Add(id)) result.Add(id);
            }
        }
        return result.Count == 0 ? null : string.Join(",", result);
    }

    /// <summary>
    /// 岗位编码解析：字符串拆英文逗号；数组/集合逐元素读取且不二次拆；
    /// trim 后丢空，保留 "0"，按首次出现顺序去重。Map 不按集合键展开。
    /// </summary>
    private static List<string> ParseFormPostCodes(object? value)
    {
        var result = new List<string>();
        if (value == null) return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(object? token)
        {
            if (token == null) return;
            var code = token.ToString()?.Trim() ?? "";
            if (code.Length > 0 && seen.Add(code)) result.Add(code);
        }

        if (value is string text)
        {
            foreach (var item in text.Split(',')) Add(item);
        }
        else if (value is System.Collections.ICollection collection &&
                 value is not System.Collections.IDictionary)
        {
            foreach (var item in collection) Add(item);
        }
        else
        {
            Add(value);
        }
        return result;
    }

    /// <summary>
    /// 部门 ID 解析：字符串拆英文逗号；数组/集合逐元素读取且不二次拆；
    /// trim 后丢空，保留 "0"，按首次出现顺序去重。Map 不按集合键展开。
    /// </summary>
    private static List<string> ParseFormDeptIds(object? value)
    {
        var result = new List<string>();
        if (value == null) return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(object? token)
        {
            if (token == null) return;
            var id = token.ToString()?.Trim() ?? "";
            if (id.Length > 0 && seen.Add(id)) result.Add(id);
        }

        if (value is string text)
        {
            foreach (var item in text.Split(',')) Add(item);
        }
        else if (value is System.Collections.ICollection collection &&
                 value is not System.Collections.IDictionary)
        {
            foreach (var item in collection) Add(item);
        }
        else
        {
            Add(value);
        }
        return result;
    }

    /// <summary>
    /// 「表单指定用户」与「表单指定部门」handler 共用的四档并集查找（顺序即优先级）：
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

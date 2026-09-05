namespace Mldong.Jeeflow.Core;

/// <summary>
/// 用户信息提供者 SPI——一次查询返回完整用户信息（对齐 Java IUserProvider.getUser 单方法口径）。
/// </summary>
public interface IUserProvider
{
    /// <summary>一次返回用户全部信息（为空时返回 null）。</summary>
    Task<UserInfo?> GetUserAsync(string userId);

    /// <summary>用户信息 DTO。</summary>
    public class UserInfo
    {
        public string? UserId { get; set; }
        public string? RealName { get; set; }
        public string? DeptId { get; set; }
        public string? DeptName { get; set; }
        public string? PostId { get; set; }
        public string? PostName { get; set; }

        public static UserInfo Of(string userId) => new() { UserId = userId, RealName = userId };
    }
}

/// <summary>
/// 组织维度用户提供者 SPI（可选，issues/16）。
/// 未注册时相关内置 handler 返回空（参与者为空）——C29：必须装配进上下文，null provider → 静默返空。
/// </summary>
public interface IOrgUserProvider
{
    Task<List<string>> FindDeptLeadersAsync(string deptId);
    Task<List<string>> FindDeptMainLeadersAsync(string deptId);
    Task<List<string>> FindByRoleAsync(string roleCode);
}

/// <summary>
/// 用户搜索钩子（可选）——candidatePage 无模型候选时的用户分页搜索。
/// </summary>
public interface IUserSearchProvider
{
    /// <summary>用户分页搜索（query 透传 pageNum/pageSize/m_*）。</summary>
    Task<PageResult<Dictionary<string, object?>>> PageAsync(PageQuery query);

    /// <summary>单用户信息（候选映射用）。</summary>
    Task<Dictionary<string, object?>?> FindByIdAsync(string userId);
}

/// <summary>
/// 表达式求值提供者 SPI（可选）。
/// core 内置默认实现（IExpressionEvaluator 以 PHP WfExpressionEvaluator 为最小基准不超集）。
/// </summary>
public interface IExpressionEvaluator
{
    /// <summary>在指定上下文中求值表达式，返回结果。</summary>
    object? Eval(string expression, IDictionary<string, object?> context);
}

/// <summary>
/// 事务模板 SPI（可选；demo 默认不注入 = 语句级 autocommit，对齐联邦现状）。
/// 契约不变量（spec/05）：回调内所有仓储调用走同一连接。
/// </summary>
public interface ITransactionTemplate
{
    Task<T> ExecuteInTxAsync<T>(Func<Task<T>> op);
}

/// <summary>
/// action → 权限码映射提供者 SPI（issues/29）。引擎不鉴权只供码（C24）。
/// null/空数组 = 放行（仅登录）；stats 3 action 登录放行不配权限码。
/// </summary>
public interface IActionPermissionProvider
{
    /// <summary>返回 action 的权限码集合（OR 语义）。</summary>
    string[]? PermissionCodes(string action);
}

/// <summary>
/// 默认权限码映射（issues/29，boot3 注解语义归纳）：
/// 默认 wf:{action /→:}；OR 清单 + 放行清单与 Java DefaultActionPermissionProvider 逐条一致。
/// </summary>
public sealed class DefaultActionPermissionProvider : IActionPermissionProvider
{
    public static readonly DefaultActionPermissionProvider Instance = new();

    private static readonly Dictionary<string, string[]> OrRules = new()
    {
        ["processDefine/detail"] = new[] { "wf:processDefine:detail", "wf:processDesign:listByType" },
        ["processDefine/startAndExecute"] = new[] { "wf:processDefine:startAndExecute", "wf:processDesign:listByType" },
        ["processDefine/getLastByName"] = new[] { "wf:processDefine:detail", "wf:processDesign:listByType", "wf:processDefine:getLastByName" },
        ["processTask/candidatePage"] = new[] { "wf:processTask:execute", "wf:processTask:candidatePage" },
        ["processTask/jumpAbleTaskNameList"] = new[] { "wf:processTask:execute" },
    };

    private static readonly HashSet<string> NoPermActions = new()
    {
        "processInstance/detail", "processInstance/highLight", "processInstance/approvalRecord",
        "processInstance/getAssigneeTextData", "processInstance/bizData",
        "processTask/detail", "processTask/addCandidate", "processTask/latest",
        "processInstance/stats/overview", "processInstance/stats/trend", "processInstance/stats/group",
    };

    public string[]? PermissionCodes(string? action)
    {
        if (action == null) return null;
        if (NoPermActions.Contains(action)) return null;
        if (OrRules.TryGetValue(action, out var or)) return or;
        return new[] { "wf:" + action.Replace('/', ':') };
    }
}

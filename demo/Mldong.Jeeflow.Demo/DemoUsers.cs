using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Demo;

/// <summary>8 具名用户（对齐 rust demo-salvo / moon demo）。</summary>
public record DemoUser(
    string UserId, string RealName, string DeptId, string DeptName,
    string PostId, string PostName)
{
    public static readonly List<DemoUser> All = new()
    {
        new("user1", "张三", "dept1", "研发部", "post1", "工程师"),
        new("userA", "孙倩", "dept2", "产品部", "post1", "工程师"),
        new("userB", "周明", "dept2", "产品部", "post2", "经理"),
        new("userC", "吴婷", "dept3", "财务部", "post1", "工程师"),
        new("leader", "李四", "dept1", "研发部", "post3", "总监"),
        new("manager", "王五", "dept3", "财务部", "post2", "经理"),
        new("director", "赵六", "dept1", "研发部", "post4", "分管领导"),
        new("boss", "钱七", "dept0", "总经理办公室", "post5", "总经理"),
    };

    public static DemoUser? Find(string uid) =>
        All.FirstOrDefault(u => u.UserId == uid);
}

/// <summary>IUserProvider demo 实现。</summary>
public class DemoUserProvider : IUserProvider
{
    public Task<IUserProvider.UserInfo?> GetUserAsync(string userId)
    {
        var u = DemoUser.Find(userId);
        if (u == null) return Task.FromResult<IUserProvider.UserInfo?>(null);
        return Task.FromResult<IUserProvider.UserInfo?>(new IUserProvider.UserInfo
        {
            UserId = u.UserId,
            RealName = u.RealName,
            DeptId = u.DeptId,
            DeptName = u.DeptName,
            PostId = u.PostId,
            PostName = u.PostName,
        });
    }
}

/// <summary>
/// IOrgUserProvider demo 规则：同部门 post2+ 为经理层；post4/post5 为分管层（boss 兜底）；
/// 角色码命中 userId 或岗位名包含。
/// </summary>
public class DemoOrgUserProvider : IOrgUserProvider
{
    public Task<List<string>> FindDeptLeadersAsync(string deptId) =>
        Task.FromResult(DemoUser.All
            .Where(u => u.DeptId == deptId && (u.PostId is "post2" or "post3"))
            .Select(u => u.UserId).ToList());

    public Task<List<string>> FindDeptMainLeadersAsync(string deptId)
    {
        var ids = DemoUser.All
            .Where(u => u.DeptId == deptId && (u.PostId is "post4" or "post5"))
            .Select(u => u.UserId).ToList();
        if (ids.Count == 0) ids.Add("boss");
        return Task.FromResult(ids);
    }

    public Task<List<string>> FindByRoleAsync(string roleCode) =>
        Task.FromResult(DemoUser.All
            .Where(u => u.UserId == roleCode || u.PostName.Contains(roleCode))
            .Select(u => u.UserId).ToList());
}

/// <summary>IUserSearchProvider demo：关键词分页（realName/userId contains）。</summary>
public class DemoUserSearchProvider : IUserSearchProvider
{
    public Task<PageResult<Dictionary<string, object?>>> PageAsync(PageQuery query)
    {
        // 关键词：取 query 中首个非条件参数 keyword（兼容前端 keyword/userName 参数）
        var keyword = query.Conditions.FirstOrDefault(c => c.Column is "keyword" or "t.keyword")?.Value?.ToString() ?? "";
        var matched = DemoUser.All
            .Where(u => keyword.Length == 0 || u.RealName.Contains(keyword) || u.UserId.Contains(keyword))
            .ToList();
        var rows = new List<Dictionary<string, object?>>();
        foreach (var u in matched)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["id"] = u.UserId,
                ["userId"] = u.UserId,
                ["realName"] = u.RealName,
                ["deptName"] = u.DeptName,
            });
        }
        var pageNum = Math.Max(query.PageNum, 1);
        var pageSize = Math.Max(query.PageSize, 1);
        var page = rows.Skip((pageNum - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(PageResult<Dictionary<string, object?>>.Of(pageNum, pageSize, rows.Count, page));
    }

    public Task<Dictionary<string, object?>?> FindByIdAsync(string userId)
    {
        var u = DemoUser.Find(userId);
        if (u == null) return Task.FromResult<Dictionary<string, object?>?>(null);
        return Task.FromResult<Dictionary<string, object?>?>(new Dictionary<string, object?>
        {
            ["id"] = u.UserId,
            ["userId"] = u.UserId,
            ["realName"] = u.RealName,
            ["deptName"] = u.DeptName,
        });
    }
}

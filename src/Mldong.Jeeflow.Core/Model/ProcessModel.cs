namespace Mldong.Jeeflow.Core;

/// <summary>流程模型——整个流程定义的顶层对象（对齐 Java ProcessModel）。</summary>
public class ProcessModel : BaseModel
{
    public string? Type { get; set; }
    public string? InstanceUrl { get; set; }
    public string? ExpireTime { get; set; }
    public string? InstanceNoClass { get; set; }
    public string? PreInterceptors { get; set; }
    public string? PostInterceptors { get; set; }
    public string? RelTableName { get; set; }
    /// <summary>持久化模式：缺省 ARCHIVE（结束归档）/ SYNC（发起入库→节点推进→结束定稿）。</summary>
    public string? PersistMode { get; set; }
    public FlowData Ext { get; set; } = new();
    public List<NodeModel> Nodes { get; set; } = new();
    public List<TaskModel> Tasks { get; set; } = new();

    /// <summary>获取开始节点。</summary>
    public StartModel? GetStart()
    {
        foreach (var node in Nodes)
            if (node is StartModel start)
                return start;
        return null;
    }

    /// <summary>根据名称获取节点。</summary>
    public NodeModel? GetNode(string? nodeName)
    {
        foreach (var node in Nodes)
            if (nodeName != null && nodeName.Equals(node.Name, StringComparison.Ordinal))
                return node;
        return null;
    }

    /// <summary>获取下一个任务节点模型集合。</summary>
    public List<TaskModel> GetNextTaskModels(string? nodeName)
    {
        var res = new List<TaskModel>();
        var node = GetNode(nodeName);
        if (node == null) return res;
        foreach (var tm in node.Outputs)
            if (tm.Target is TaskModel direct)
                res.Add(direct);
        if (res.Count == 0)
        {
            foreach (var tm in node.Outputs)
                if (tm.Target != null)
                    res.AddRange(GetNextTaskModels(tm.Target.Name));
        }
        return res;
    }

    /// <summary>获取下一个任务节点的候选人（CandidateHandler 注册池 + 声明 handler + 内置双源，C20）。</summary>
    public async Task<List<Candidate>> GetNextTaskModelCandidatesAsync(string? nodeName, ServiceContext context)
    {
        var res = new List<Candidate>();
        foreach (var tm in GetNextTaskModels(nodeName))
            res.AddRange(await GetCandidatesAsync(tm, context));
        return res;
    }

    /// <summary>根据任务模型获取候选人（去重）。</summary>
    public async Task<List<Candidate>> GetCandidatesAsync(TaskModel taskModel, ServiceContext context)
    {
        var res = new List<Candidate>();
        // ① CandidateHandler 注册池
        foreach (var handlerEntry in context.CandidateHandlers)
        {
            var candidates = await handlerEntry.Value.HandleAsync(taskModel);
            if (candidates != null) res.AddRange(candidates);
        }
        // ② 节点声明的 candidateHandler（声明名不可解析 → 显式错误，C20）
        var handlerName = taskModel.CandidateHandlerName;
        if (!string.IsNullOrEmpty(handlerName))
        {
            var handler = context.FindCandidateHandler(handlerName.Trim());
            var declared = await handler.HandleAsync(taskModel);
            if (declared != null) res.AddRange(declared);
        }
        // ③ 内置解析（v1.6.0，对齐 boot4 GlobalCandidateHandler 双源语义）：
        //    candidateUsers 逗号分隔 userId 直接作为候选人
        var candidateUsers = taskModel.CandidateUsers;
        if (!string.IsNullOrEmpty(candidateUsers))
        {
            foreach (var userId in candidateUsers.Split(','))
            {
                var uid = userId.Trim();
                if (uid.Length > 0) res.Add(new Candidate(uid, uid, "user"));
            }
        }
        // ④ candidateGroups 逗号分隔角色标识，IOrgUserProvider.findByRole 取人
        var candidateGroups = taskModel.CandidateGroups;
        if (!string.IsNullOrEmpty(candidateGroups))
        {
            var orgProvider = context.OrgUserProvider;
            if (orgProvider != null)
            {
                foreach (var roleCode in candidateGroups.Split(','))
                {
                    var rc = roleCode.Trim();
                    if (rc.Length == 0) continue;
                    var userIds = await orgProvider.FindByRoleAsync(rc);
                    if (userIds != null)
                        foreach (var uid in userIds)
                            if (!string.IsNullOrEmpty(uid))
                                res.Add(new Candidate(uid, uid, "user"));
                }
            }
        }
        return res.Distinct().ToList();
    }

    /// <summary>获取指定类型的所有节点（从 start 沿图遍历）。</summary>
    public List<T> GetModels<T>() where T : NodeModel
    {
        var models = new List<T>();
        var start = GetStart();
        if (start != null)
            BuildModels(models, start.GetNextModels<T>());
        return models;
    }

    private void BuildModels<T>(List<T> models, List<T> nextModels) where T : NodeModel
    {
        foreach (var nextModel in nextModels)
        {
            if (!models.Contains(nextModel))
            {
                models.Add(nextModel);
                BuildModels(models, nextModel.GetNextModels<T>());
            }
        }
    }
}

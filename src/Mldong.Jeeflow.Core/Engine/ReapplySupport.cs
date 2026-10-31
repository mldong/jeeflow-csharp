using System.Collections;

namespace Mldong.Jeeflow.Core;

/// <summary>
/// 退回发起人后的重提方式（流程节点 properties.reapplyMode，对齐 Java ReapplyModeEnum）。
/// </summary>
public enum WfReapplyMode
{
    /// <summary>退回发起人后按申请节点的正常出边重提（缺省，保持既有行为）。</summary>
    Normal = 0,

    /// <summary>退回发起人后，重提直接回到本来源节点建待办。</summary>
    ReturnToSource = 1,
}

/// <summary>
/// 「退回修改、重提直返」的引擎内部状态读写与分支归属解析（对齐 Java ReapplySupport /
/// Python jeeflow/reapply.py）。
///
/// <para>状态落点：不新增表/列，全部寄存在任务变量 JSON 的保留键里——<see cref="VarBranchCtx"/>
/// （分支归属栈，外层→内层）与 <see cref="VarReapplyCtx"/>（本次退回绑定，只出现在申请待办上）。
/// 保留键由引擎写入，对外办理入口必须先剥掉客户端传入的同名键（<see cref="StripReserved"/>）。</para>
/// </summary>
public static class ReapplySupport
{
    /// <summary>节点属性键：退回发起人后的重提方式。</summary>
    public const string PropReapplyMode = "reapplyMode";

    /// <summary>任务变量保留键：分支归属栈（外层→内层）。</summary>
    public const string VarBranchCtx = "_branchCtx";

    /// <summary>任务变量保留键：本次退回绑定（只出现在申请待办上）。</summary>
    public const string VarReapplyCtx = "_reapplyContext";

    public const string KeyMode = "mode";
    /// <summary>直返上下文的 mode 取值（JSON 形状与 Java/Python 逐字一致，全大写）。</summary>
    public const string ModeNormal = "NORMAL";
    /// <summary>直返上下文的 mode 取值（JSON 形状与 Java/Python 逐字一致，全大写）。</summary>
    public const string ModeReturnToSource = "RETURN_TO_SOURCE";    public const string KeySourceTask = "sourceTaskId";
    public const string KeySourceNode = "sourceNode";
    public const string KeyBranch = "branch";
    public const string KeyFork = "fork";
    public const string KeyJoin = "join";
    public const string KeyRound = "round";
    public const string KeyRoot = "root";

    /// <summary>
    /// 解析配置值（对齐 Java ReapplyModeEnum#parse）：缺省/空串 ⇒ NORMAL；
    /// <b>非法值必须报配置错误</b>，不得静默当缺省（配错要在解析期就能诊断出来）。
    /// </summary>
    public static WfReapplyMode ParseMode(object? value, string? nodeName)
    {
        if (value == null) return WfReapplyMode.Normal;
        var v = value.ToString()?.Trim() ?? "";
        if (v.Length == 0 || v == ModeNormal) return WfReapplyMode.Normal;
        if (v == ModeReturnToSource) return WfReapplyMode.ReturnToSource;
        throw new JeeflowException($"节点[{nodeName}]的 reapplyMode 配置非法: {v}");
    }

    // ═══ 模型静态分析 ═══

    /// <summary>
    /// 节点所在的全部分支范围（外层→内层）。含该节点的汇合点按集合大小降序排列即为外→内。
    /// </summary>
    public static List<Dictionary<string, object?>> StaticScopes(ProcessModel? model, string? nodeName)
    {
        var result = new List<Dictionary<string, object?>>();
        if (model == null || nodeName == null) return result;
        var matched = new List<(int Size, string Fork, string Join)>();
        foreach (var node in model.Nodes)
        {
            if (node is not JoinModel join || join.Name == null) continue;
            var fork = FindForkName(model, join);
            if (fork == null) continue;
            var inside = BackwardTaskNames(join, fork);
            if (!inside.Contains(nodeName)) continue;
            matched.Add((inside.Count, fork, join.Name));
        }
        // OrderByDescending 是稳定排序（对齐 Java List.sort），同层宽度相同的两档保持节点表顺序
        foreach (var m in matched.OrderByDescending(m => m.Size))
            result.Add(Scope(m.Fork, m.Join, 0L, 0L));
        return result;
    }

    /// <summary>从节点回溯到指定分叉之间的任务节点名集合；可穿过嵌套的内层 fork，遇到 stopFork 即止。</summary>
    private static HashSet<string> BackwardTaskNames(NodeModel node, string stopFork)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var stack = new List<NodeModel>();
        foreach (var tm in node.Inputs)
            if (tm.Source != null) stack.Add(tm.Source);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (stack.Count > 0)
        {
            var cur = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (cur == null) continue;
            if (cur.Name == null || !visited.Add(cur.Name)) continue;
            if (cur is ForkModel && cur.Name == stopFork) continue;
            if (cur is TaskModel) names.Add(cur.Name);
            foreach (var tm in cur.Inputs)
                if (tm.Source != null) stack.Add(tm.Source);
        }
        return names;
    }

    private static List<NodeModel> Outgoing(NodeModel node)
    {
        var list = new List<NodeModel>();
        foreach (var tm in node.Outputs)
            if (tm.Target != null) list.Add(tm.Target);
        return list;
    }

    /// <summary>
    /// 汇合点对应的分叉：取「最近后支配汇合点 == 本 join」的 fork，多候选取最外层。
    /// 不能取"回溯遇到的第一个 fork"——嵌套并行时外层汇合点沿内层分支回溯会先撞到内层 fork。
    /// </summary>
    private static string? FindForkName(ProcessModel model, JoinModel join)
    {
        if (join.Name == null) return null;
        string? best = null;
        var bestDepth = -1;
        foreach (var node in model.Nodes)
        {
            if (node is not ForkModel fork || fork.Name == null) continue;
            if (NearestPostDominatingJoin(model, fork) != join.Name) continue;
            var d = MaxDistanceToJoin(fork, join.Name);
            if (d > bestDepth)
            {
                bestDepth = d;
                best = fork.Name;
            }
        }
        return best;
    }

    private static string? NearestPostDominatingJoin(ProcessModel model, NodeModel fork)
    {
        string? best = null;
        int? bestMax = null;
        foreach (var node in model.Nodes)
        {
            if (node is not JoinModel join || join.Name == null) continue;
            if (!PostDominates(fork, join.Name)) continue;
            var d = MaxDistanceToJoin(fork, join.Name);
            if (d >= 0 && (bestMax == null || d < bestMax))
            {
                bestMax = d;
                best = join.Name;
            }
        }
        return best;
    }

    /// <summary>屏蔽该汇合点后，fork 的任一分支仍能走到末端即返回 false。</summary>
    private static bool PostDominates(NodeModel fork, string joinName)
    {
        var stack = Outgoing(fork);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (stack.Count > 0)
        {
            var cur = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (cur.Name == null || cur.Name == joinName || !visited.Add(cur.Name)) continue;
            var outs = Outgoing(cur);
            if (outs.Count == 0) return false;
            stack.AddRange(outs);
        }
        return true;
    }

    /// <summary>分支走到汇合点的最大跳数（外层程度）；不可达返回 -1。</summary>
    private static int MaxDistanceToJoin(NodeModel fork, string joinName)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new List<NodeModel>();
        foreach (var t in Outgoing(fork))
            if (t.Name != null && visited.Add(t.Name)) frontier.Add(t);
        var d = 1;
        var found = -1;
        while (frontier.Count > 0)
        {
            var next = new List<NodeModel>();
            foreach (var cur in frontier)
            {
                if (cur.Name == joinName) found = d;
                foreach (var t in Outgoing(cur))
                    if (t.Name != null && visited.Add(t.Name)) next.Add(t);
            }
            frontier = next;
            d++;
        }
        return found;
    }

    // ═══ 任务变量的读写 ═══

    /// <summary>读任务的分支归属栈（缺省/形状不符 ⇒ 空列表，不抛错）。</summary>
    public static List<Dictionary<string, object?>> ScopesOf(ProcessTask? task) =>
        ScopesOf(task?.Variables);

    /// <summary>读变量的分支归属栈（缺省/形状不符 ⇒ 空列表，不抛错）。</summary>
    public static List<Dictionary<string, object?>> ScopesOf(FlowData? vars)
    {
        if (vars == null) return new List<Dictionary<string, object?>>();
        return ToScopeList(vars.TryGetValue(VarBranchCtx, out var raw) ? raw : null);
    }

    private static List<Dictionary<string, object?>> ToScopeList(object? raw)
    {
        var result = new List<Dictionary<string, object?>>();
        if (raw is not IEnumerable en || raw is string) return result;
        foreach (var item in en)
        {
            if (item is IDictionary<string, object?> map)
                result.Add(new Dictionary<string, object?>(map));
        }
        return result;
    }

    /// <summary>写分支归属栈（null ⇒ 空列表，保留键恒存在）。</summary>
    public static void PutScopes(ProcessTask? task, List<Dictionary<string, object?>>? scopes)
    {
        if (task == null) return;
        task.Variables[VarBranchCtx] = scopes ?? new List<Dictionary<string, object?>>();
    }

    /// <summary>读直返上下文；缺省/空对象 ⇒ null（＝本任务不是「退回修改」的申请待办）。</summary>
    public static Dictionary<string, object?>? ReapplyOf(ProcessTask? task)
    {
        if (task?.Variables == null) return null;
        if (!task.Variables.TryGetValue(VarReapplyCtx, out var raw)) return null;
        if (raw is not IDictionary<string, object?> map) return null;
        var ctx = new Dictionary<string, object?>(map);
        return ctx.Count == 0 ? null : ctx;
    }

    public static void PutReapply(ProcessTask? task, Dictionary<string, object?>? ctx)
    {
        if (task == null || ctx == null) return;
        task.Variables[VarReapplyCtx] = ctx;
    }

    /// <summary>直返上下文里的分支归属（申请待办写的是来源任务当时的那一档）。</summary>
    public static List<Dictionary<string, object?>> BranchOf(Dictionary<string, object?>? reapply)
    {
        if (reapply == null) return new List<Dictionary<string, object?>>();
        return ToScopeList(reapply.TryGetValue(KeyBranch, out var raw) ? raw : null);
    }

    /// <summary>在范围内找指定汇合点的那一档。</summary>
    public static Dictionary<string, object?>? FindScope(
        List<Dictionary<string, object?>>? scopes, string? joinName)
    {
        if (scopes == null || joinName == null) return null;
        foreach (var s in scopes)
            if (joinName == AsStr(s.TryGetValue(KeyJoin, out var v) ? v : null))
                return s;
        return null;
    }

    // ═══ 建单时计算分支归属 ═══

    /// <summary>
    /// 新任务的分支归属：静态范围逐档继承父任务同档的 round/root；父任务不在该档
    /// （本任务是该分叉的分支根）时 round=父任务ID、root=本任务ID。
    /// <para><b>本仓 taskId 由 <c>SaveTaskAsync</c> 分配</b> ⇒ 建单时 self 恒 0，root 落 0，
    /// 落库后由引擎调 <see cref="FillRootSelf"/> 回填并补一次 UPDATE（对齐 Java 同款两拍）。</para>
    /// </summary>
    public static List<Dictionary<string, object?>> BuildScopes(
        ProcessTask? parent, ProcessModel? model, string? nodeName, long? selfTaskId)
    {
        var statics = StaticScopes(model, nodeName);
        if (statics.Count == 0) return statics;
        var parentScopes = ScopesOf(parent);
        var parentId = parent?.TaskId ?? 0L;
        var selfId = selfTaskId ?? 0L;
        var result = new List<Dictionary<string, object?>>();
        foreach (var s in statics)
        {
            var fork = AsStr(s.TryGetValue(KeyFork, out var f) ? f : null);
            var join = AsStr(s.TryGetValue(KeyJoin, out var j) ? j : null);
            Dictionary<string, object?>? inherited = null;
            foreach (var p in parentScopes)
            {
                if (fork == AsStr(p.TryGetValue(KeyFork, out var pf) ? pf : null)
                    && join == AsStr(p.TryGetValue(KeyJoin, out var pj) ? pj : null))
                {
                    inherited = p;
                    break;
                }
            }
            if (inherited != null)
            {
                result.Add(Scope(fork!, join!,
                    AsLong(inherited.TryGetValue(KeyRound, out var r) ? r : null),
                    AsLong(inherited.TryGetValue(KeyRoot, out var rt) ? rt : null)));
            }
            else
            {
                result.Add(Scope(fork!, join!, parentId, selfId));
            }
        }
        return result;
    }

    /// <summary>
    /// 分支根任务落库拿到 taskId 后回填自身 root（建单时 ID 尚未生成）；返回是否发生回填。
    /// </summary>
    public static bool FillRootSelf(ProcessTask? task)
    {
        if (task?.TaskId == null) return false;
        var scopes = ScopesOf(task);
        var changed = false;
        foreach (var s in scopes)
        {
            if (AsLong(s.TryGetValue(KeyRoot, out var v) ? v : null) == 0L)
            {
                s[KeyRoot] = task.TaskId.Value;
                changed = true;
            }
        }
        if (changed) PutScopes(task, scopes);
        return changed;
    }

    // ═══ 直返上下文 ═══

    /// <summary>
    /// 新建直返上下文（JSON 形状与 Java/Python 一致：<c>sourceTaskId</c> 是<b>字符串</b>，
    /// 其余三键 mode/sourceNode/branch）。</summary>
    public static Dictionary<string, object?> NewReapplyContext(
        string? mode, long? sourceTaskId, string? sourceNode, List<Dictionary<string, object?>>? branch) =>
        new()
        {
            [KeyMode] = mode,
            [KeySourceTask] = sourceTaskId?.ToString(),
            [KeySourceNode] = sourceNode,
            [KeyBranch] = branch ?? new List<Dictionary<string, object?>>(),
        };

    /// <summary>客户端不得伪造/覆盖引擎控制键（就地剥离，对齐 Java stripReserved）。</summary>
    public static FlowData StripReserved(FlowData? args)
    {
        if (args == null) return new FlowData();
        args.Remove(VarBranchCtx);
        args.Remove(VarReapplyCtx);
        return args;
    }

    /// <summary>结构损坏的直返上下文明确失败，不按无标记处理。</summary>
    public static long RequireSourceTaskId(Dictionary<string, object?> ctx)
    {
        var id = AsLongObj(ctx.TryGetValue(KeySourceTask, out var v) ? v : null);
        if (id == null || id <= 0L)
            throw new JeeflowException("重提直返上下文缺少有效来源任务");
        return id.Value;
    }

    /// <summary>
    /// 带直返标记的进行中申请待办只接受重新提交（submitType=5）：跳转/血缘回退/拒绝/
    /// 再次退回四个入口一律在<b>任何状态变更之前</b>拒绝（对齐 java <c>rejectMarkedApplicant</c>）。
    /// 少了这道守卫，对标记申请待办提交 6 会完成旧申请、建一条无标记新申请 ⇒
    /// 旧申请的分支等待随之消失，另一支汇合提前放行。
    /// </summary>
    public static void RejectMarkedApplicant(ProcessTask? snapshot, string action)
    {
        if (snapshot != null && snapshot.IsDoing() && ReapplyOf(snapshot) != null)
            throw new JeeflowException($"带重提直返标记的申请待办不允许{action}，只接受重新提交(submitType=5)");
    }

    // ═══ 基础转换（JSON 往返后数字可能是 int/long/double/string） ═══

    public static long AsLong(object? v) => AsLongObj(v) ?? 0L;

    public static long? AsLongObj(object? v)
    {
        switch (v)
        {
            case null: return null;
            case long l: return l;
            case int i: return i;
            case double d: return (long)d;
            case decimal m: return (long)m;
        }
        var s = v.ToString()?.Trim() ?? "";
        if (s.Length == 0) return null;
        return long.TryParse(s, out var parsed) ? parsed : null;
    }

    public static string? AsStr(object? v) => v?.ToString();

    private static Dictionary<string, object?> Scope(string fork, string join, long round, long root) =>
        new()
        {
            [KeyFork] = fork,
            [KeyJoin] = join,
            [KeyRound] = round,
            [KeyRoot] = root,
        };

    public static List<Dictionary<string, object?>> EmptyScopes() => new();
}

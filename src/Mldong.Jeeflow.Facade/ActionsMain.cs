using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Facade;

/// <summary>40+ action 实现：定义/实例/任务/视图端点（对齐 Java JeeflowFacade 私有方法）。</summary>
public partial class JeeflowFacade
{
    // ═══ 流程定义 ═══

    private async Task<Dictionary<string, object?>> DefinePageAsync(FlowData args)
    {
        var query = new JeeflowQueryParser().Parse(args);
        var page = await _repository.PageDefinesAsync(query);
        return PageResultOut(page);
    }

    private async Task<Dictionary<string, object?>> DefineDetailAsync(FlowData args)
    {
        var id = ToLong(args.GetObj("id"));
        var def = await _repository.FindDefineByIdAsync(id);
        if (def == null) return Error("流程定义不存在");
        var data = new Dictionary<string, object?>
        {
            ["id"] = def.Id,
            ["name"] = def.Name,
            ["displayName"] = def.DisplayName,
            ["type"] = def.Type,
            ["state"] = def.State,
            ["version"] = def.Version,
            ["jsonObject"] = ParseGraph(def.Content), // 前端表单渲染/流程图依赖（issues/05）
        };
        return Ok(data);
    }

    private async Task<Dictionary<string, object?>> StartAndExecuteAsync(FlowData args)
    {
        var defineId = ToLong(args.GetObj(FlowConst.ProcessDefineIdKey));
        var op = OperatorArg(args);
        var flowArgs = new FlowData();
        foreach (var kv in args)
        {
            if (kv.Key != FlowConst.ProcessDefineIdKey && kv.Key != "operator") flowArgs[kv.Key] = kv.Value;
        }
        var inst = await _engine.StartProcessInstanceByIdAsync(defineId, op, flowArgs);
        // boot2 startAndExecute：自动完成申请节点（assignee="applicant" → 发起人）
        var doingTasks = await _repository.FindDoingTasksAsync(inst.InstanceId!.Value, new string[] { });
        foreach (var task in doingTasks)
        {
            await _repository.AddTaskActorAsync(task.TaskId!.Value, new List<string> { op });
            flowArgs[FlowConst.SubmitType] = (int)WfSubmitType.Apply;
            // f_nextNodeOperator（发起时预指派人）→ tf_nextNodeOperator（引擎执行参数）
            // issues/142 B 批 · spec 06 §2.11「两形同判据」：这里原先用 GetStr（＝value.ToString()）读值，
            // 数组形态被整条串成 <b>.NET 类型名</b>当成一个参与者落进 actor_id——实机取证读数
            // Expected ["17001","17002"] / Actual ["System.Collections.Generic.List`1[System.Object]"]。
            // 现在过归属值判据单点（逗号串/数组/标量同一枚：逐元素 trim、空值丢弃、折叠、数字收敛成字符串），
            // 归一后为空 ⇒ 与"没填"同档（不写 tf_nextNodeOperator，由引擎回落节点 assignee）。
            var startNextOp = PageQuery.NormalizeActors(flowArgs.GetObj(FlowConst.ProcessStartNextNodeOperator));
            if (startNextOp.Count > 0)
            {
                flowArgs[FlowConst.NextNodeOperator] = startNextOp;
            }
            await _engine.ExecuteProcessTaskAsync(task.TaskId.Value, op, flowArgs);
        }
        return Ok(new Dictionary<string, object?>
        {
            [FlowConst.ProcessInstanceIdKey] = inst.InstanceId,
        });
    }

    private async Task<Dictionary<string, object?>> DeployAsync(FlowData args)
    {
        var bytes = ContentBytes(args);
        var model = ModelParser.Parse(bytes, _context);
        var defineId = await SaveDeployedDefineAsync(model, bytes!);
        return Ok(new Dictionary<string, object?> { [FlowConst.ProcessDefineIdKey] = defineId });
    }

    private async Task<Dictionary<string, object?>> RedeployAsync(FlowData args)
    {
        var defineId = ToLong(args.GetObj(FlowConst.ProcessDefineIdKey));
        var bytes = ContentBytes(args);
        var model = ModelParser.Parse(bytes, _context);
        var def = new ProcessDefine
        {
            Id = defineId,
            Name = model.Name,
            DisplayName = model.DisplayName,
            Type = model.Type,
            Content = bytes,
            UpdateUser = ToStr(args.GetObj("operator"), "system"),
        };
        await _repository.UpdateDefineAsync(def);
        return Ok();
    }

    private async Task<Dictionary<string, object?>> DefineRemoveAsync(FlowData args)
    {
        foreach (var id in IdListArgs(args))
        {
            await _repository.RemoveDefineAsync(id);
        }
        return Ok();
    }

    private async Task<Dictionary<string, object?>> DefineUpAndDownAsync(FlowData args)
    {
        var stateObj = args.GetObj("opType") ?? args.GetObj("state");
        if (stateObj == null) throw new JeeflowException("state 缺失");
        var state = int.Parse(stateObj.ToString()!);
        foreach (var id in IdListArgs(args))
        {
            await _repository.UpdateDefineStateAsync(id, state);
        }
        return Ok();
    }

    // ═══ 流程实例 ═══

    private async Task<Dictionary<string, object?>> InstancePageAsync(FlowData args)
    {
        var query = new JeeflowQueryParser().Parse(args);
        var userId = OperatorArg(args);
        query.Add("t.operator", "EQ", userId);
        var page = await _repository.PageInstancesAsync(query);
        return PageResultOut(page);
    }

    private async Task<Dictionary<string, object?>> InstanceDetailAsync(FlowData args)
    {
        var id = ToLong(args.GetObj("id"));
        var inst = await _repository.FindInstanceByIdAsync(id);
        if (inst == null) return Error("流程实例不存在");
        var data = new Dictionary<string, object?>
        {
            ["id"] = inst.InstanceId,
            ["parentId"] = inst.ParentId,
            ["processDefineId"] = inst.DefineId,
            ["state"] = inst.State,
            ["parentNodeName"] = inst.ParentNodeName,
            ["businessNo"] = inst.BusinessNo,
            ["operator"] = inst.Operator,
            ["ext"] = inst.Variables ?? new FlowData(), // issues/124：变量唯一对外出口，空变量出 {} 而非 null
            ["formData"] = FormDataOf(inst.Variables, FlowConst.FormDataPrefix), // issues/15
            ["createTime"] = inst.CreateTime?.ToString("yyyy-MM-dd HH:mm:ss"),
            ["createUser"] = inst.CreateUser,
        };
        var def0 = await _repository.FindDefineByIdAsync(inst.DefineId);
        if (def0 != null)
        {
            data["displayName"] = def0.DisplayName;
            data["name"] = def0.Name;
            data["version"] = def0.Version;
        }
        data["jsonObject"] = def0 != null ? ParseGraph(def0.Content) : null;
        // 任务列表（issues/05-4）：全量 tasks + activeTaskList（仅 DOING）+ ext/isFirstTaskNode
        var firstTaskNodeId = FirstTaskNodeId(data.TryGetValue("jsonObject", out var jo)
            ? jo as Dictionary<string, object?> : null);
        var tasks = new List<object?>();
        var activeTaskList = new List<object?>();
        foreach (var t in inst.Tasks)
        {
            var vo = TaskVo(t);
            var ext = new Dictionary<string, object?>();
            foreach (var kv in t.Variables) ext[kv.Key] = kv.Value;
            var doing = t.TaskState == (int)WfTaskState.Doing;
            // issues/128：这里曾无条件按拓扑覆写，把建单时写在行上的值盖掉 ⇒ 与其余七栈相反
            ext[FlowConst.IsFirstTaskNode] = RowFirstOrCompute(ext, doing, t.TaskName, firstTaskNodeId);
            vo["ext"] = ext;
            tasks.Add(vo);
            if (doing) activeTaskList.Add(vo);
        }
        data["tasks"] = tasks;
        data["activeTaskList"] = activeTaskList;
        return Ok(data);
    }

    private async Task<Dictionary<string, object?>> WithdrawAsync(FlowData args)
    {
        var instanceId = ToLong(args.GetObj("id"));
        // issues/114：operator 硬必填——严禁缺省回落 user1 等固定账号。回落会把撤回人静默记成
        // 别人（实例与任务的 update_user 一起失真），审计链坏掉且不报错。msg 跨栈统一「operator 必填」。
        var op = ToStr(args.GetObj("operator"))?.Trim();
        if (string.IsNullOrEmpty(op)) return Error("operator 必填");
        var inst = await _repository.FindInstanceByIdAsync(instanceId);
        if (inst == null) return Error("流程实例不存在");
        if (!await CanWithdrawAsync(inst, op!)) return Error("无权限撤回该流程实例");
        inst.Withdraw(op);
        await _repository.UpdateInstanceAsync(inst); // v1.0.1：级联持久化任务状态
        // TASK_WITHDRAW（码 8）：撤回把实例 state 写 30 <b>落库之后</b> fire，
        // 每轮撤回只 fire 一次、不逐任务（spec §11.3 码 8；上游 issues/134 的实例态守卫抛异常时
        // 走不到这里，被撤的轮次不发事件——不发未成立事实的事件是 §11.2 原则 3 的另一面）。
        await NotifyAsync(ProcessEventType.TaskWithdraw, inst.InstanceId, new FlowData
        {
            ["instanceId"] = inst.InstanceId,
            ["operator"] = op,
        });
        return Ok();
    }

    /// <summary>
    /// 撤回归属判据（issues/114，命中任一即放行，全不命中拒绝）：
    /// <list type="number">
    /// <item><c>operator</c> = 实例发起人（<c>wf_process_instance.operator</c>）——
    /// <b>不可复用 <see cref="ProcessTask.IsAllowed"/></b>：各语言引擎的 isAllowed 只判
    /// "operator 在不在该任务 actorIds"+ auto/admin 放行，从不查实例发起人，这一支必须显式补；</item>
    /// <item><c>operator</c> 是该实例任一<b>进行中</b>任务的参与者
    /// （<c>wf_process_task_actor.actor_id</c>，以参与者表为准，不用聚合副本——副本可能滞后于
    /// 加签/转办的增量写入）；</item>
    /// <item><c>operator</c> ∈ {flow.auto, flow.admin}（沿用 isAllowed 既有放行约定）。</item>
    /// </list>
    /// </summary>
    private async Task<bool> CanWithdrawAsync(ProcessInstance inst, string op)
    {
        if (IsPrivilegedOperator(op)) return true;
        if (op == inst.Operator) return true;
        var doingTasks = await _repository.FindDoingTasksAsync(inst.InstanceId!.Value, new string[] { });
        foreach (var task in doingTasks)
        {
            if ((await _repository.FindTaskActorsAsync(task.TaskId!.Value)).Contains(op)) return true;
        }
        return false;
    }

    // ═══ 流程任务 ═══

    private async Task<Dictionary<string, object?>> TodoListAsync(FlowData args)
    {
        var query = new JeeflowQueryParser().Parse(args);
        var userId = OperatorArg(args);
        query.Add("pta.actor_id", "EQ", userId);
        var page = await _repository.PageTodoTasksAsync(query);
        return PageResultOut(page);
    }

    private async Task<Dictionary<string, object?>> DoneListAsync(FlowData args)
    {
        var query = new JeeflowQueryParser().Parse(args);
        var userId = OperatorArg(args);
        query.Add("t.operator", "EQ", userId);
        var page = await _repository.PageDoneTasksAsync(query);
        return PageResultOut(page);
    }

    private async Task<Dictionary<string, object?>> ExecuteAsync(FlowData args)
    {
        var taskId = ToLong(args.GetObj(FlowConst.ProcessTaskIdKey));
        var op = OperatorArg(args);
        var submitType = ToInt(args.GetObj(FlowConst.SubmitType), (int)WfSubmitType.Agree);
        var flowArgs = new FlowData();
        foreach (var kv in args)
        {
            if (kv.Key != FlowConst.ProcessTaskIdKey && kv.Key != "operator") flowArgs[kv.Key] = kv.Value;
        }
        flowArgs[FlowConst.SubmitType] = submitType;
        // boot3 execute 分发（spec §11.2）
        if (submitType == (int)WfSubmitType.Reject)
        {
            await _engine.ExecuteAndJumpToEndAsync(taskId!.Value, op, flowArgs);
        }
        else if (submitType == (int)WfSubmitType.Rollback)
        {
            await _engine.ExecuteAndJumpTaskAsync(taskId!.Value, op, flowArgs, null);
        }
        else if (submitType == (int)WfSubmitType.Jump)
        {
            var taskName = ToStr(args.GetObj(FlowConst.TaskName));
            await _engine.ExecuteAndJumpTaskAsync(taskId!.Value, op, flowArgs, taskName);
        }
        else if (submitType == (int)WfSubmitType.RollbackToOperator)
        {
            await _engine.ExecuteAndJumpToFirstTaskNodeAsync(taskId!.Value, op, flowArgs);
        }
        else if (submitType == (int)WfSubmitType.CountersignDisagree)
        {
            flowArgs[FlowConst.CountersignDisagreeFlag] = 1; // 软拒绝 flag（C8）
            await _engine.ExecuteProcessTaskAsync(taskId!.Value, op, flowArgs);
        }
        else
        {
            // 默认执行（0 APPLY / 1 AGREE / 5 重新提交）
            await _engine.ExecuteProcessTaskAsync(taskId!.Value, op, flowArgs);
        }
        return Ok();
    }

    // ═══ 视图端点（v1.2.0）═══

    private async Task<Dictionary<string, object?>> GetLastByNameAsync(FlowData args)
    {
        var name = ToStr(args.GetObj("processDefineName"));
        var query = new PageQuery(1, 1).Add("t.name", "EQ", name);
        query.OrderBy = "t.version desc";
        var page = await _repository.PageDefinesAsync(query);
        if (page.Rows.Count == 0) return Error("流程定义不存在: " + name);
        var def = page.Rows[0];
        return Ok(new Dictionary<string, object?>
        {
            ["id"] = def.Id,
            ["name"] = def.Name,
            ["displayName"] = def.DisplayName,
            ["type"] = def.Type,
            ["state"] = def.State,
            ["version"] = def.Version,
        });
    }

    private async Task<Dictionary<string, object?>> HighLightAsync(FlowData args)
    {
        var instanceId = ToLong(args.GetObj("id"));
        var inst = await _repository.FindInstanceByIdAsync(instanceId);
        if (inst == null) return Error("流程实例不存在");
        var activeNodeNames = new List<string>();
        var historyNodeNames = new List<string>();
        var historyEdgeNames = new List<string>();
        // 活跃节点 = 进行中任务
        var doing = await _repository.FindDoingTasksAsync(instanceId!.Value, null);
        foreach (var t in doing)
        {
            if (!activeNodeNames.Contains(t.TaskName!)) activeNodeNames.Add(t.TaskName!);
        }
        // 历史节点 = 已完成任务 + 模型路径补全（start 沿 outputs 递归，遇活跃节点停止）
        var history = await _repository.FindHistoryTasksAsync(instanceId.Value);
        foreach (var t in history)
        {
            if (!activeNodeNames.Contains(t.TaskName!) && !historyNodeNames.Contains(t.TaskName!))
                historyNodeNames.Add(t.TaskName!);
        }
        var def = await _repository.FindDefineByIdAsync(inst.DefineId);
        var nodeProgress = new Dictionary<string, object?>();
        if (def != null)
        {
            try
            {
                var model = ModelParser.Parse(def.Content, _context);
                nodeProgress = await BuildNodeProgressAsync(model, history);
                CollectPath(model.GetStart(), activeNodeNames, historyNodeNames, historyEdgeNames,
                    new HashSet<string>(), inst.Variables, history);
            }
            catch
            {
                // 高亮容错（对齐 Java 吞异常）
            }
        }
        return Ok(new Dictionary<string, object?>
        {
            ["activeNodeNames"] = activeNodeNames,
            ["historyNodeNames"] = historyNodeNames,
            ["historyEdgeNames"] = historyEdgeNames,
            ["nodeProgress"] = nodeProgress,
        });
    }

    /// <summary>节点成员进度（issue 41/C9）：成员列表优先从会签任务变量 operatorList_{node} 还原。</summary>
    private async Task<Dictionary<string, object?>> BuildNodeProgressAsync(ProcessModel model, List<ProcessTask> history)
    {
        var progress = new Dictionary<string, object?>();
        var names = new List<string>();
        var seen = new HashSet<string>();
        foreach (var t in history)
        {
            if (seen.Add(t.TaskName!)) names.Add(t.TaskName!);
        }
        var userProvider = _context.UserProvider;
        foreach (var name in names)
        {
            var ts = history.Where(t => name == t.TaskName).ToList();
            if (ts.Count == 0) continue;
            // 完整成员列表：会签串行任务变量 operatorList_{node} 优先（C9），否则任务 actorIds 并集
            var csMembers = ReadCountersignOperatorList(ts, name);
            List<string> members;
            if (csMembers.Count > 0)
            {
                members = csMembers;
            }
            else
            {
                var memberSet = new LinkedHashSet();
                foreach (var t in ts)
                    foreach (var a in t.ActorIds) memberSet.Add(a);
                members = memberSet.ToList();
            }
            if (members.Count == 0) continue; // 动态参与人：无静态成员，不返回
            var doneSet = new HashSet<string>();
            foreach (var t in ts)
            {
                if (t.TaskState == (int)WfTaskState.Finished)
                    foreach (var a in t.ActorIds) doneSet.Add(a);
            }
            string? activeActor = null;
            foreach (var t in ts)
            {
                if (t.TaskState == (int)WfTaskState.Doing && t.ActorIds.Count > 0)
                {
                    activeActor = t.ActorIds[0];
                    break;
                }
            }
            // 会签判定：模型节点属性（TaskParser codeOf 已兼容 'ALL' 字符串，issue 42）
            var isCs = false;
            string? csType = null;
            if (model.GetNode(name) is TaskModel tm)
            {
                isCs = tm.PerformType == WfPerformType.Countersign;
                if (tm.CountersignType != null)
                    csType = tm.CountersignType.ToString();
            }
            var memberList = new List<object?>();
            foreach (var id in members)
            {
                var m = new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["name"] = await ResolveUserNameAsync(userProvider, id),
                };
                if (doneSet.Contains(id)) m["done"] = true;
                else if (id == activeActor) m["active"] = true;
                memberList.Add(m);
            }
            var item = new Dictionary<string, object?> { ["members"] = memberList };
            if (isCs && csType != null) item["type"] = csType;
            progress[name] = item;
        }
        return progress;
    }

    /// <summary>会签全量办理人：从任务变量 operatorList_{node} 还原（C9，无 csv_ 前缀）。</summary>
    private static List<string> ReadCountersignOperatorList(List<ProcessTask> ts, string name)
    {
        var key = $"{FlowConst.CountersignOperatorList}_{name}";
        foreach (var t in ts)
        {
            if (t.Variables == null) continue;
            if (!t.Variables.TryGetValue(key, out var value)) continue;
            // issues/142 B 批：出口侧读这把名册也过同一枚判据单点（逗号串/数组/标量两形同判据、
            // trim、空值丢弃、折叠），不再在本方法里判第三份尺子；整条为空时继续找下一个任务变量（旧语义）。
            var actors = PageQuery.NormalizeActors(value);
            if (actors.Count > 0) return actors;
        }
        return new List<string>();
    }

    /// <summary>成员姓名解析（issue 43/E15）：IUserProvider SPI 解析 realName，查不到缺省空串。</summary>
    private async Task<string> ResolveUserNameAsync(IUserProvider? userProvider, string userId)
    {
        if (userProvider == null) return "";
        try
        {
            var info = await userProvider.GetUserAsync(userId);
            return !string.IsNullOrEmpty(info?.RealName) ? info.RealName : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>高亮路径收集：决策节点出边须表达式求值只收集 true 边（C14/issues/06）。</summary>
    private async Task CollectPathAsync(
        NodeModel? node, List<string> active, List<string> history, List<string> edges,
        HashSet<string> visited, FlowData instanceVars, List<ProcessTask> historyTasks)
    {
        if (node == null || visited.Contains(node.Name!)) return;
        visited.Add(node.Name!);
        foreach (var tm in node.Outputs)
        {
            // 决策节点：输出边表达式求值过滤——false 分支未实际执行，不收集进高亮路径
            if (node is DecisionModel && !string.IsNullOrEmpty(tm.Expr)
                                    && !await EvalDecisionExprAsync((DecisionModel)node, tm, instanceVars, historyTasks))
            {
                continue;
            }
            var edgeName = tm.Name;
            if (edgeName != null && !edges.Contains(edgeName)) edges.Add(edgeName);
            var next = tm.Target;
            if (next == null) continue;
            if (!active.Contains(next.Name!) && !history.Contains(next.Name!))
                history.Add(next.Name!);
            if (active.Contains(next.Name!)) continue; // 遇活跃节点停止深入
            await CollectPathAsync(next, active, history, edges, visited, instanceVars, historyTasks);
        }
    }

    /// <summary>决策输出边表达式求值：args = 实例变量 + 决策前置任务变量（与引擎运行时同源）。</summary>
    private async Task<bool> EvalDecisionExprAsync(
        DecisionModel decision, TransitionModel tm, FlowData instanceVars, List<ProcessTask> historyTasks)
    {
        var evaluator = _context.ExpressionEvaluatorOrDefault;
        var args = new Dictionary<string, object?>();
        foreach (var kv in instanceVars) args[kv.Key] = kv.Value;
        if (decision.Inputs.Count > 0)
        {
            var src = decision.Inputs[0].Source;
            if (src?.Name != null)
            {
                foreach (var t in historyTasks)
                {
                    if (src.Name == t.TaskName)
                    {
                        foreach (var kv in t.Variables) args[kv.Key] = kv.Value;
                        break;
                    }
                }
            }
        }
        var result = evaluator.Eval(tm.Expr!, args);
        return result is true;
    }

    private Task CollectPath(
        NodeModel? node, List<string> active, List<string> history, List<string> edges,
        HashSet<string> visited, FlowData instanceVars, List<ProcessTask> historyTasks) =>
        CollectPathAsync(node, active, history, edges, visited, instanceVars, historyTasks);

    private async Task<Dictionary<string, object?>> ApprovalRecordAsync(FlowData args)
    {
        var instanceId = ToLong(args.GetObj("id"));
        var history = await _repository.FindHistoryTasksAsync(instanceId!.Value);
        var rows = new List<object?>();
        foreach (var t in history)
        {
            var vo = new Dictionary<string, object?>
            {
                ["taskName"] = t.TaskName,
                ["displayName"] = t.DisplayName,
                ["taskType"] = t.TaskType == null ? null : (int)t.TaskType, // C5 数字 code
                ["performType"] = t.PerformType == null ? null : (int)t.PerformType,
                ["taskState"] = t.TaskState,
                ["operator"] = t.ActorId,
                ["finishTime"] = FmtTime(t.FinishTime),
                ["ext"] = t.Variables, // issues/124：variable 原串出口下线
            };
            rows.Add(vo);
        }
        return Ok(rows);
    }

    private async Task<Dictionary<string, object?>> GetAssigneeTextDataAsync(FlowData args)
    {
        var instanceId = ToLong(args.GetObj("id"));
        var includeNodeName = !false.Equals(args.GetObj("includeNodeName"));
        var rows = new List<object?>();
        var doing = await _repository.FindDoingTasksAsync(instanceId!.Value, null);
        foreach (var t in doing)
        {
            var actors = await _repository.FindTaskActorsAsync(t.TaskId!.Value);
            foreach (var actor in actors)
            {
                rows.Add(new Dictionary<string, object?>
                {
                    ["value"] = actor,
                    ["label"] = includeNodeName ? t.DisplayName + ":" + actor : actor,
                });
            }
        }
        return Ok(rows);
    }

    private async Task<Dictionary<string, object?>> CreateCcInstanceAsync(FlowData args)
    {
        var instanceId = ToLong(args.GetObj(FlowConst.ProcessInstanceIdKey));
        var op = OperatorArg(args);
        var actorIds = args.GetObj("actorIds");
        if (actorIds is string || actorIds is not System.Collections.ICollection coll || coll.Count == 0)
        {
            return Error("actorIds 缺失");
        }
        // issues/141 G10「空不创建行」（spec 06 §2.10）＋ issues/142 B 批（§2.11 同一枚尺子搬到任务侧）：
        // 手动腿与引擎腿、任务侧走的是<b>同一个判据单点</b> PageQuery.NormalizeActors——
        // 空串/纯空白/null 丢弃、值取 trim 后的串；丢完为空 ⇒ 与上面那条"空集合＝actorIds 缺失"
        // 同档（沿用既有错误信封与文案，不新造错误语义）。
        // 旧形状实测：actorIds={"", "   "} 时 code=0 且真落两行 cc（ActorId='' 与 '   '）、fire 码 4 两次。
        var actors = PageQuery.NormalizeActors(actorIds);
        if (actors.Count == 0) return Error("actorIds 缺失");
        // issues/141 G2 写侧判重＝幂等空操作（spec 06 §4）：手动腿与引擎腿同一条判据
        // （spec §11.7「三条入口共用一支」）——已有 cc 行的 (实例, 人) 跳过，不新增行、
        // 不重置未读、不更新原行时间；只有实际新建的子集拿去 fire。
        var created = await _repository.CreateCcInstanceIfAbsentAsync(instanceId!.Value, op, actors.ToArray());
        // CC_CREATE（码 4）：<b>手动抄送支也要 fire</b>——issues/132 §4.5 待拍① 按 spec §11.2 原则 1
        // 定稿：码值表达"发生了什么事实"（新增了一条抄送记录），不表达"谁触发的"，
        // 故引擎自动路径（f_ccActors／tf_ccActors）与门面手动路径共用同一个 fire 口
        // ProcessPublisher.NotifyCcCreateAsync（行为基准＝Java 单一 notifyCcCreate，spec §11.7）。
        // 集成层严禁再自行补发（§11.1，历史 PHP issues/101 就是这条降级路）。
        // 入参＝实际新建子集（issues/141 G2）：重复抄送没发生"创建"⇒ 不发码 4，子集为空整支不 fire。
        if (created.Count > 0)
        {
            await ProcessPublisher.NotifyCcCreateAsync(instanceId.Value, created, _context.EventListeners);
        }
        return Ok();
    }

    private async Task<Dictionary<string, object?>> UpdateCcStatusAsync(FlowData args)
    {
        var instanceId = ToLong(args.GetObj(FlowConst.ProcessInstanceIdKey));
        var op = OperatorArg(args);
        await _repository.UpdateCcStatusAsync(instanceId!.Value, op);
        return Ok();
    }

    private async Task<Dictionary<string, object?>> CcListAsync(FlowData args)
    {
        var query = new JeeflowQueryParser().Parse(args);
        var userId = OperatorArg(args);
        query.Add("cc.actor_id", "EQ", userId);
        var page = await _repository.PageCcInstancesAsync(query);
        return PageResultOut(page);
    }

    private async Task<Dictionary<string, object?>> TaskDetailAsync(FlowData args)
    {
        var taskId = ToLong(args.GetObj("id"));
        var op = OperatorArg(args);
        var task = await _repository.FindTaskByIdAsync(taskId);
        if (task == null) return Error("任务不存在");
        var vo = TaskVo(task);
        vo["taskActorIdList"] = await _repository.FindTaskActorsAsync(taskId!.Value);
        vo["executable"] = task.IsAllowed(op);
        var doing = task.TaskState == (int)WfTaskState.Doing;
        var tExt = new Dictionary<string, object?>();
        foreach (var kv in task.Variables) tExt[kv.Key] = kv.Value;
        // issues/128：原写法先无条件 false、下面再无条件现算，两道合起来把行上值盖死。
        // 先把行上值另存（缺键=null），两处出口都以它优先；def 取不到时也不掉回 false。
        var rowFirstVal = tExt.TryGetValue(FlowConst.IsFirstTaskNode, out var rowFirstRaw) ? rowFirstRaw : null;
        tExt[FlowConst.IsFirstTaskNode] = rowFirstVal != null && RowFirstIsTrue(rowFirstVal);
        vo["ext"] = tExt;
        // taskModel：流程定义中对应节点（显示名/表单/issues/62 form+ext）
        var inst = await _repository.FindInstanceByIdAsync(task.ProcessInstanceId);
        if (inst != null)
        {
            var def = await _repository.FindDefineByIdAsync(inst.DefineId);
            var jsonObject = def != null ? ParseGraph(def.Content) : null;
            vo["jsonObject"] = jsonObject;
            if (def != null)
            {
                tExt[FlowConst.IsFirstTaskNode] = rowFirstVal != null
                    ? RowFirstIsTrue(rowFirstVal)
                    : doing && task.TaskName == FirstTaskNodeId(jsonObject);
                try
                {
                    var model = ModelParser.Parse(def.Content, _context);
                    foreach (var node in model.Nodes)
                    {
                        if (task.TaskName == node.Name)
                        {
                            var tm = new Dictionary<string, object?>
                            {
                                ["name"] = node.Name,
                                ["displayName"] = node.DisplayName,
                                ["type"] = node.GetType().Name.Replace("Model", "").ToLowerInvariant(),
                            };
                            if (node is TaskModel taskNode)
                            {
                                tm["form"] = taskNode.Form;
                                var ext = new Dictionary<string, object?>();
                                foreach (var kv in taskNode.Ext) ext[kv.Key] = kv.Value;
                                tm["ext"] = ext; // issues/62：taskModel 补 form/ext（字段权限键）
                            }
                            vo["taskModel"] = tm;
                            break;
                        }
                    }
                }
                catch
                {
                    // 解析容错
                }
            }
        }
        return Ok(vo);
    }

    private async Task<Dictionary<string, object?>> JumpAbleTaskNameListAsync(FlowData args)
    {
        var instanceId = ToLong(args.GetObj(FlowConst.ProcessInstanceIdKey));
        var rows = new List<object?>();
        var seen = new HashSet<string>();
        var done = await _repository.FindDoneTasksAsync(instanceId!.Value, null);
        foreach (var t in done)
        {
            if (t.PerformType == WfPerformType.Countersign) continue; // 会签任务不可跳转目标
            if (seen.Add(t.TaskName!))
            {
                rows.Add(new Dictionary<string, object?>
                {
                    ["label"] = t.DisplayName,
                    ["value"] = t.TaskName,
                });
            }
        }
        return Ok(rows);
    }

    private async Task<Dictionary<string, object?>> CandidatePageAsync(FlowData args)
    {
        var taskId = ToLong(args.GetObj(FlowConst.ProcessTaskIdKey));
        if (taskId == null) taskId = ToLong(args.GetObj("id"));
        if (taskId == null) return Error("processTaskId 缺失");
        var task = await _repository.FindTaskByIdAsync(taskId);
        if (task == null) return Error("任务不存在");
        var inst = await _repository.FindInstanceByIdAsync(task.ProcessInstanceId);
        if (inst == null) return Error("流程实例不存在");
        var def = await _repository.FindDefineByIdAsync(inst.DefineId);
        if (def == null) return Error("流程定义不存在");
        List<Candidate>? candidateList = null;
        try
        {
            var model = ModelParser.Parse(def.Content, _context);
            candidateList = await model.GetNextTaskModelCandidatesAsync(task.TaskName, _context);
        }
        catch
        {
            // 候选解析容错
        }
        if (candidateList is { Count: > 0 })
        {
            // 候选配置命中 → 用户信息映射（issues/80 行键归一：{id, realName, userName?, deptName?}）
            var rows = new List<object?>();
            foreach (var c in candidateList)
            {
                Dictionary<string, object?>? u = null;
                if (_context.UserSearchProvider != null)
                {
                    u = await _context.UserSearchProvider.FindByIdAsync(c.ActorId!);
                }
                if (u == null && _context.UserProvider != null)
                {
                    var info = await _context.UserProvider.GetUserAsync(c.ActorId!);
                    if (info != null)
                    {
                        u = new Dictionary<string, object?> { ["userId"] = info.UserId, ["realName"] = info.RealName };
                        if (info.DeptName != null) u["deptName"] = info.DeptName;
                    }
                }
                if (u == null)
                {
                    u = new Dictionary<string, object?> { ["userId"] = c.ActorId, ["realName"] = c.ActorId };
                }
                rows.Add(CandidateRow(c.ActorId!, u));
            }
            return PageResultOut(PageResult<Dictionary<string, object?>>.Of(1, 10, rows.Count,
                rows.Cast<Dictionary<string, object?>>().ToList()));
        }
        // 无模型候选 → 用户分页搜索（依赖 IUserSearchProvider）
        if (_context.UserSearchProvider == null)
        {
            return Error("未配置 IUserSearchProvider（用户搜索钩子）");
        }
        return PageResultOut(await _context.UserSearchProvider.PageAsync(new JeeflowQueryParser().Parse(args)));
    }

    /// <summary>candidatePage 模型候选行键归一（issues/80）：主键收敛为 id，realName 缺失回落 id。</summary>
    private static Dictionary<string, object?> CandidateRow(string actorId, Dictionary<string, object?> src)
    {
        var row = new Dictionary<string, object?>();
        var id = src.TryGetValue("id", out var idVal) && idVal != null ? idVal
            : src.TryGetValue("userId", out var uidVal) && uidVal != null ? uidVal : (object?)actorId;
        row["id"] = id.ToString();
        row["realName"] = src.TryGetValue("realName", out var rn) && rn != null ? rn : row["id"];
        if (src.TryGetValue("userId", out var uid) && uid != null) row["userId"] = uid;
        if (src.TryGetValue("userName", out var un) && un != null) row["userName"] = un;
        if (src.TryGetValue("deptName", out var dn) && dn != null) row["deptName"] = dn;
        return row;
    }

    private async Task<Dictionary<string, object?>> TaskSurrogateAsync(FlowData args)
    {
        var taskId = ToLong(args.GetObj(FlowConst.ProcessTaskIdKey));
        // issues/142 B 批 · spec 06 §2.11：actorIds 的逗号串与数组两形过同一枚判据单点
        // （PageQuery.NormalizeActors）——逐元素 trim、空串/纯空白/null 丢弃、同次调用折叠。
        // 旧形状：集合腿不 trim，末尾 Where(t => t.Length > 0) 只兜住 null 转成的 ""，兜不住 "  "
        //（trim 前长度 > 0）⇒ 实测加签 [" 16001 ","16001","","  ",null,"16002"] 落
        // ["leader"," 16001 ","16001","  ","16002"]：同一人两行 + 一条空归属值。
        var actors = PageQuery.NormalizeActors(args.GetObj("actorIds"));
        // 主键类参数另判一档（§2.11）：缺失/空串/0/负数都是调用方写错了，不得拿 ''/0 当 id 落库；
        // 沿用既有"缺参数"信封与文案（要求③不新造错误码/文案）。
        if (taskId == null || taskId.Value <= 0 || actors.Count == 0)
            return Error("processTaskId/actorIds 缺失");
        // C15/issues/28：addTaskActor=去重追加非全删全插
        await _repository.AddTaskActorAsync(taskId!.Value, actors);
        return Ok();
    }

    /// <summary>
    /// 转办（issues/115）：摘原办理人 + 换新参与人，区别于 <c>processTask/surrogate</c> 加签的
    /// "只追加"（加签后原人保留可办，本 action 把待办从 A 挪到 B）。契约七条语义（spec 06
    /// §processTask/transfer）逐条落地：
    /// <list type="number">
    /// <item><b>摘原人</b>：只删 <c>fromActor</c> 在该任务的 <c>wf_process_task_actor</c> 行，
    /// 会签节点转的是"自己那一票"，其余成员不受影响；</item>
    /// <item><b>加新人</b>：<c>toActor</c> 追加为该任务参与人，办理规则不变；</item>
    /// <item><b>任务不新建</b>：沿用同一 <c>processTaskId</c>，高亮图/节点进度不变；</item>
    /// <item><b>留痕三件</b>：任务变量 <c>submitType=7</c>（当前槽位，B 办结前审批历史直接读作"转办"，
    /// 办结后由 B 的 1/2/20 覆盖，属预期）+ 跨跳追加账本 <c>tf_transferHistory</c>（每跳 append，
    /// 六键 camelCase，time 一律 <c>yyyy-MM-dd HH:mm:ss</c>）+ 单跳便捷键 <c>tf_transferTo</c>/
    /// <c>tf_transferReason</c> + 末跳可读文案 <c>tf_approvalComment</c>；</item>
    /// <item><b>变量合并序</b>（留痕存活的前置条件）：办理提交走"实例变量 ← 任务既有变量 ← 本次提交
    /// 参数"，见 <see cref="ProcessTask.Finish"/>（args 覆盖式合并，不整体替换任务变量）；</item>
    /// <item><b>去重</b>：<c>toActor</c> 已是参与者 / <c>fromActor</c> 不在参与者里 → 明确报错；</item>
    /// <item><b>前置态</b>：任务非进行中（<c>taskState != 10</c>）→ 明确报错。</item>
    /// </list>
    /// <b>注意：严禁覆写任务 <c>actor_id</c>（本栈 <see cref="ProcessTask.ActorId"/> → <c>operator</c> 列）</b>：
    /// 进行中任务该列恒无值是既有不变量，而 <c>PageDoneTasks</c> 按 <c>task_state&lt;&gt;10 AND
    /// t.operator=?</c> 过滤——把被摘走的人写进这一列，该单一旦撤回/终止（离开 DOING 但保留该列值），
    /// 会凭空出现在他从没办过的「我已办」列表里（Node 实测踩到，spec 06 §transfer 留痕①）。
    /// "办理人记谁"由 <c>update_user</c> = 转办操作人 + <c>tf_transferHistory[].operator</c> 承载。
    /// </summary>
    private async Task<Dictionary<string, object?>> TaskTransferAsync(FlowData args)
    {
        var taskId = ToLong(args.GetObj(FlowConst.ProcessTaskIdKey));
        // 参数必填序与 msg 逐字对齐 spec 06「失败 msg 跨栈统一文案」（失败码一律 99999999）
        // issues/142 B 批 · spec 06 §2.11：fromActor/toActor/operator 先过归属值判据单点再用
        // （NormalizeActorValue＝单人档：trim＋丢空，集合形态 ["x"] 收敛成那一个人）。
        // 旧形状用 ToStr（＝value.ToString()）读单人参数，数组形态整条串成 .NET 类型名 ⇒
        // 实测 fromActor=["leader"] 直接落进"原办理人不是该任务参与人"，拼错时还会把类型名写进 actor_id。
        var op = PageQuery.NormalizeActorValue(args.GetObj("operator"));
        if (op == null) return Error("operator 必填");
        var fromActor = PageQuery.NormalizeActorValue(args.GetObj("fromActor"));
        if (fromActor == null) return Error("fromActor 必填");
        var toActor = PageQuery.NormalizeActorValue(args.GetObj("toActor"));
        if (toActor == null) return Error("toActor 必填");
        var reason = ToStr(args.GetObj("reason")) ?? "";
        var task = taskId == null ? null : await _repository.FindTaskByIdAsync(taskId.Value);
        if (task == null) return Error("任务不存在");
        // 归属判据：只能转自己那一条待办（flow.auto / flow.admin 例外），与撤回同口径
        if (!IsPrivilegedOperator(op) && op != fromActor) return Error("无权限转办该任务");
        // 前置态：仅进行中（DOING=10）任务可转办
        if (!task.IsDoing()) return Error("任务非进行中，不可转办");
        // 参与者以关系表为判据（聚合副本可能滞后于加签/转办的增量写入）；副本并入仅作仓储不水合时兜底
        var actors = await _repository.FindTaskActorsAsync(taskId!.Value);
        var participants = DedupKeepOrder(actors.Concat(task.ActorIds));
        if (!participants.Contains(fromActor)) return Error("原办理人不是该任务参与人");
        if (participants.Contains(toActor)) return Error("目标人已是该任务参与人");
        // ① 摘原人（仅 fromActor 一行）+ ② 加新人（同一 taskId，不新建任务）
        await _repository.RemoveTaskActorAsync(taskId.Value, new List<string> { fromActor });
        await _repository.AddTaskActorAsync(taskId.Value, new List<string> { toActor });
        // ④ 留痕三件（写进任务变量，与办理提交同一槽位）
        var vars = task.Variables ?? new FlowData();
        var now = Clock.Now;
        // 账本为何必须是追加式列表而非单跳键：本栈审批记录的槽位就是任务行本身，B 办结时
        // submitType 会被 B 的办理参数覆盖；没有追加式账本，多跳转办只剩末跳、办结后转办事实整体消失。
        // 新建容器而非原地 Add：内存仓浅拷贝下变量值在副本与存储间共享引用，原地改会串台。
        vars[FlowConst.TransferHistory] = AppendTransferRecord(
            vars.GetObj(FlowConst.TransferHistory),
            new Dictionary<string, object?>
            {
                // 六键固定 camelCase + 固定键序（与契约/其他栈 JSON 同形；Dictionary 无删除时按插入序枚举）
                [FlowConst.SubmitType] = (int)WfSubmitType.Transfer,
                ["fromActor"] = fromActor,
                ["toActor"] = toActor,
                ["reason"] = reason,          // 无值写 ""，不写 null
                ["time"] = FmtTime(now)!,     // 一律 yyyy-MM-dd HH:mm:ss，不得用本地 ISO 方言
                ["operator"] = op,
            });
        vars[FlowConst.SubmitType] = (int)WfSubmitType.Transfer; // 当前槽位（B 办结后由其覆盖，预期）
        vars[FlowConst.TransferTo] = toActor;
        vars[FlowConst.TransferReason] = reason;
        vars[FlowConst.ApprovalComment] = TransferComment(fromActor!, toActor!, reason); // 末跳可读文案
        task.Variables = vars;
        task.UpdateTime = now;
        task.UpdateUser = op; // "办理人记谁"落这列（actor_id 不碰）
        // 仓储 updateTask 会用任务副本的 actorIds 全量覆写参与者行（内存/JDBC 同语义），
        // 必须同步为摘/加之后的最新集合，否则留痕落库时把旧参与者原样写回
        task.ActorIds = DedupKeepOrder(
            participants.Where(a => a != fromActor).Append(toActor!));
        await _repository.UpdateTaskAsync(task);
        // TASK_TRANSFER（码 7）：任务参与者被替换<b>并落库之后</b> fire（spec §11.3 码 7）。
        // 前面任一负向判据（必填／越权／非进行中／原人不是参与人／目标人已在）都 return 在这一行之前
        // ⇒ 转办没成立就不发事件，不存在"补发"。
        await NotifyAsync(ProcessEventType.TaskTransfer, task.TaskId, new FlowData
        {
            ["instanceId"] = task.ProcessInstanceId,
            ["taskId"] = task.TaskId,
            ["fromActor"] = fromActor,
            ["toActor"] = toActor,
            ["operator"] = op,
        });
        return Ok();
    }

    /// <summary>tf_transferHistory 追加（只追加不覆盖，spec 06 §transfer 留痕②）。
    /// 既有值三种来源都容错：本栈内存写入的 <c>List&lt;object?&gt;</c>、直接构造的
    /// <c>List&lt;Dictionary&lt;string,object?&gt;&gt;</c>、MySQL 仓 variable 列 JSON 回读的
    /// <c>List&lt;object?&gt;</c>（元素为 Dictionary）。统一归一为 <c>List&lt;object?&gt;</c>
    /// ——与 JSON 落地形态同构，便于八栈读到同一形状。</summary>
    private static List<object?> AppendTransferRecord(object? existing, Dictionary<string, object?> record)
    {
        var outList = new List<object?>();
        if (existing is System.Collections.IEnumerable en and not string)
        {
            foreach (var item in en) outList.Add(item);
        }
        outList.Add(record);
        return outList;
    }

    /// <summary>转办末跳可读文案（tf_approvalComment 槽位，前端审批意见既有读取位 issues/15）：
    /// 形如「A 转办给 B（原因…）」，无原因时「A 转办给 B」。label 与
    /// <c>wf_process_submit_type</c> 的 7 同词。</summary>
    private static string TransferComment(string fromActor, string toActor, string reason)
    {
        var r = reason.Trim();
        return r.Length == 0
            ? $"{fromActor} 转办给 {toActor}"
            : $"{fromActor} 转办给 {toActor}（{r}）";
    }

    /// <summary>去重保序（空串剔除）。</summary>
    private static List<string> DedupKeepOrder(IEnumerable<string> items)
    {
        var seen = new HashSet<string>();
        var list = new List<string>();
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item)) continue;
            if (seen.Add(item)) list.Add(item);
        }
        return list;
    }

    /// <summary>门面侧 fire 流程事件：与引擎共用 <see cref="ProcessPublisher"/>
    /// （逐监听器隔离、零监听器安全返回，spec §11.5）。只在事实已落库之后由调用点触发。</summary>
    private Task NotifyAsync(ProcessEventType eventType, long? sourceId, FlowData data) =>
        ProcessPublisher.NotifyAsync(
            new ProcessEvent { EventType = eventType, SourceId = sourceId, Data = data },
            _context.EventListeners);

    /// <summary>
    /// 摘除参与人（issues/115 残留 · 门面第 <b>47</b> 个 action，spec 06 §processTask/removeTaskActor）：
    /// SPI 侧 <see cref="IProcessRepository.RemoveTaskActorAsync"/> 从第一天起就是<b>必选</b>方法、
    /// 本栈两仓（内存 / MySQL）都实现，只是没上门面——摘人只能靠 <c>transfer</c>（摘 A <b>并</b>加 B）。
    /// 本 action 补的就是这一段（八栈同批）。
    /// <para>三个兄弟 action 的分工写清楚，免得后来人把三条混用：
    /// <c>processTask/surrogate</c>／<c>addCandidate</c>＝<b>只加</b>（加签，原人保留可办）；
    /// <c>processTask/transfer</c>＝<b>换人</b>（摘 A 加 B，写 submitType=7 ＋ tf_transferHistory 留痕）；
    /// 本 action＝<b>只摘不加、零留痕</b>：删掉 <c>actorIds</c> 在本任务的参与者行，不新建任务、
    /// 不写任何任务变量、不覆写任务 <c>actor_id</c>/<c>operator</c> 列、<b>不 fire 事件</b>
    /// （issues/132 §11.3 定稿的事件集里没有"摘除参与人"这一码，码 7 <c>TASK_TRANSFER</c> 的语义是
    /// "参与者被替换"，只摘不加却发码 7 等于凭空造出一条没发生的转办事实——要立法先开 issue）。</para>
    /// <para>六档守卫（次序逐栈一致，spec 同节钉死，不接受各栈自行排序）：operator 硬必填 →
    /// 主键/集合缺失 → 任务存在 → 归属判据 → 仅 DOING → 不得摘空 → 落库。归属判据沿用
    /// <c>transfer</c> 口径（只能摘自己那一票，<c>flow.auto</c>/<c>flow.admin</c> 例外，
    /// <see cref="IsPrivilegedOperator"/> 大小写不敏感既有档）；"不得摘空"是本 action 独有的下限——
    /// 摘空会造出<b>无人可办又无法撤回重派的死单</b>，比"配错表达式落 NULL"更难恢复。</para>
    /// <para>幂等：非参与者静默忽略，重放第二次仍得成功信封（本 action 是"清理/收回"用途）。</para>
    /// </summary>
    private async Task<Dictionary<string, object?>> TaskRemoveActorAsync(FlowData args)
    {
        // operator 先判必填：参数全缺时若先报缺参数，会把鉴权缺口藏进"缺参数"报错里（spec 同节守卫次序）。
        // 归一在入口就做（NormalizeActorValue＝trim＋丢空），不沿用 transfer 现状那支未 trim 的形状——
        // 新增代码不该重犯 issues/142 §2.11 已立法的毛病（transfer 的 operator trim 随批二 §3-6 单独收）。
        var op = PageQuery.NormalizeActorValue(args.GetObj("operator"));
        if (op == null) return Error("operator 必填");
        // 主键档与归属值档分得很清楚（§2.11「主键类参数另判一档」），文案与判据都复用兄弟 action
        // surrogate 那一支（不另造，spec 同节第 8 条「同族同文案」）：processTaskId 缺失/空串/非正数
        // ⇒ 响亮报错；actorIds 归一后为空 ⇒ 同一逐字文案。两条都不落库，空串元素也绝不会被喂进
        // DELETE（历史 actor_id='' 脏行因此安全）。
        var taskId = ToLong(args.GetObj(FlowConst.ProcessTaskIdKey));
        var actors = PageQuery.NormalizeActors(args.GetObj("actorIds"));
        if (taskId == null || taskId.Value <= 0 || actors.Count == 0)
            return Error("processTaskId/actorIds 缺失");
        var task = await _repository.FindTaskByIdAsync(taskId!.Value);
        if (task == null) return Error("任务不存在");
        // 归属判据同 transfer：被摘集合必须含操作人本人（入参两半边都取归一后的串，比较才咬得上），
        // flow.auto / flow.admin 例外。transfer 能"摘 A 加 B"是因为 A 就是操作人本人，
        // 本 action 不得成为借道摘他人的口子。
        if (!IsPrivilegedOperator(op) && !actors.Contains(op)) return Error("无权限摘除该任务参与人");
        // 前置态：仅进行中（DOING=10）任务可摘人。已办结/撤回/废弃任务的历史参与人行是
        // approvalRecord 的取证依据（它读全状态任务行），摘它等于改写审批历史。
        if (!task.IsDoing()) return Error("任务非进行中，不可摘除参与人");
        // 以参与者表为判据（聚合副本可能滞后于加签/转办的增量写入，与 transfer 同源）
        var current = await _repository.FindTaskActorsAsync(taskId!.Value);
        var targets = new HashSet<string>(actors);
        // 【语义 6】匹配取归一值、DELETE 取行上的原值（§2.11 硬要求②「落库与比较取 trim 后的值」的
        // <b>删除腿</b>）：库里的行可能是修复前落下的未 trim 原值 " leader "，入参 "leader" 必须判成
        // 同一个人<b>并真删掉它</b>——所以匹配用归一形，喂给仓储的删除值是<b>那一行的原值</b>。
        // 只拿归一值去 DELETE 会"判成同一人却一条没删"：门面报成功而被摘的人待办还在，是<b>假成功</b>
        // （go 栈 transfer 腿实测到并已这样修）。
        // 【语义 5】"至少剩一人"的下限按<b>能办单的人数</b>算：归一后为空的行（actor_id=''/纯空白脏行）
        // 既不匹配也不算"一个人"——它谁也办不了，拿它撑住下限等于让"摘空"伪装成成功。
        // 判据是<b>集合差</b>（当前参与者 − 归一后入参），不是入参条数，否则混入非参与者 id 就能绕过。
        var toDelete = new List<string>();
        var remaining = 0;
        foreach (var row in current)
        {
            var normalized = PageQuery.NormalizeActorValue(row);
            if (normalized == null) continue;   // 历史脏行：既不匹配也不算"一个人"
            if (targets.Contains(normalized)) toDelete.Add(row);
            else remaining++;
        }
        if (toDelete.Count > 0 && remaining == 0) return Error("至少需保留一名参与人");
        // 【语义 7】幂等：actors 里不属于本任务参与者的人静默忽略（不报错），一个都没命中 ⇒ 空操作、
        // 成功信封（前端双点、集成层重放第二次不再报错）。要"人不在任务里就报错"请用 transfer。
        if (toDelete.Count > 0) await _repository.RemoveTaskActorAsync(taskId!.Value, toDelete);
        return Ok();
    }

    private async Task<Dictionary<string, object?>> TaskLatestAsync(FlowData args)
    {
        var instanceId = ToLong(args.GetObj(FlowConst.ProcessInstanceIdKey));
        var doing = await _repository.FindDoingTasksAsync(instanceId!.Value, null);
        if (doing.Count == 0) return Ok(null);
        return Ok(TaskVo(doing[0]));
    }
}

/// <summary>保序去重集合（对齐 Java LinkedHashSet 用途）。</summary>
internal class LinkedHashSet
{
    private readonly Dictionary<string, byte> _inner = new();
    public void Add(string item) => _inner.TryAdd(item, 0);
    public List<string> ToList() => _inner.Keys.ToList();
}

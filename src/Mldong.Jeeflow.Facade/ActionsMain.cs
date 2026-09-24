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
        var op = ToStr(args.GetObj("operator"), "user1");
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
            var startNextOp = flowArgs.GetStr(FlowConst.ProcessStartNextNodeOperator);
            if (!string.IsNullOrEmpty(startNextOp))
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
        var userId = ToStr(args.GetObj("operator"), "user1");
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
            ext["isFirstTaskNode"] = doing && t.TaskName == firstTaskNodeId;
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
        var userId = ToStr(args.GetObj("operator"), "user1");
        query.Add("pta.actor_id", "EQ", userId);
        var page = await _repository.PageTodoTasksAsync(query);
        return PageResultOut(page);
    }

    private async Task<Dictionary<string, object?>> DoneListAsync(FlowData args)
    {
        var query = new JeeflowQueryParser().Parse(args);
        var userId = ToStr(args.GetObj("operator"), "user1");
        query.Add("t.operator", "EQ", userId);
        var page = await _repository.PageDoneTasksAsync(query);
        return PageResultOut(page);
    }

    private async Task<Dictionary<string, object?>> ExecuteAsync(FlowData args)
    {
        var taskId = ToLong(args.GetObj(FlowConst.ProcessTaskIdKey));
        var op = ToStr(args.GetObj("operator"), "user1");
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
            if (value is string)
            {
                // 标量：走 else 分支
            }
            else if (value is System.Collections.ICollection coll)
            {
                var list = new List<string>();
                foreach (var o in coll)
                {
                    var s = o?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                }
                if (list.Count > 0) return list;
            }
            else if (value != null)
            {
                var s = value.ToString()?.Trim();
                if (!string.IsNullOrEmpty(s)) return new List<string> { s };
            }
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
        var op = ToStr(args.GetObj("operator"), "user1");
        var actorIds = args.GetObj("actorIds");
        if (actorIds is string || actorIds is not System.Collections.ICollection coll || coll.Count == 0)
        {
            return Error("actorIds 缺失");
        }
        var list = new List<string>();
        foreach (var o in coll) list.Add(o?.ToString() ?? "");
        await _repository.CreateCcInstanceAsync(instanceId!.Value, op, list.ToArray());
        return Ok();
    }

    private async Task<Dictionary<string, object?>> UpdateCcStatusAsync(FlowData args)
    {
        var instanceId = ToLong(args.GetObj(FlowConst.ProcessInstanceIdKey));
        var op = ToStr(args.GetObj("operator"), "user1");
        await _repository.UpdateCcStatusAsync(instanceId!.Value, op);
        return Ok();
    }

    private async Task<Dictionary<string, object?>> CcListAsync(FlowData args)
    {
        var query = new JeeflowQueryParser().Parse(args);
        var userId = ToStr(args.GetObj("operator"), "user1");
        query.Add("cc.actor_id", "EQ", userId);
        var page = await _repository.PageCcInstancesAsync(query);
        return PageResultOut(page);
    }

    private async Task<Dictionary<string, object?>> TaskDetailAsync(FlowData args)
    {
        var taskId = ToLong(args.GetObj("id"));
        var op = ToStr(args.GetObj("operator"), "user1");
        var task = await _repository.FindTaskByIdAsync(taskId);
        if (task == null) return Error("任务不存在");
        var vo = TaskVo(task);
        vo["taskActorIdList"] = await _repository.FindTaskActorsAsync(taskId!.Value);
        vo["executable"] = task.IsAllowed(op);
        var doing = task.TaskState == (int)WfTaskState.Doing;
        var tExt = new Dictionary<string, object?>();
        foreach (var kv in task.Variables) tExt[kv.Key] = kv.Value;
        tExt["isFirstTaskNode"] = false;
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
                tExt["isFirstTaskNode"] = doing && task.TaskName == FirstTaskNodeId(jsonObject);
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
        var actors = ToStringList(args.GetObj("actorIds"));
        if (taskId == null || actors.Count == 0) return Error("processTaskId/actorIds 缺失");
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
        var op = ToStr(args.GetObj("operator"))?.Trim();
        if (string.IsNullOrEmpty(op)) return Error("operator 必填");
        var fromActor = ToStr(args.GetObj("fromActor"))?.Trim();
        if (string.IsNullOrEmpty(fromActor)) return Error("fromActor 必填");
        var toActor = ToStr(args.GetObj("toActor"))?.Trim();
        if (string.IsNullOrEmpty(toActor)) return Error("toActor 必填");
        var reason = ToStr(args.GetObj("reason")) ?? "";
        var task = taskId == null ? null : await _repository.FindTaskByIdAsync(taskId.Value);
        if (task == null) return Error("任务不存在");
        // 归属判据：只能转自己那一条待办（flow.auto / flow.admin 例外），与撤回同口径
        if (!IsPrivilegedOperator(op!) && op != fromActor) return Error("无权限转办该任务");
        // 前置态：仅进行中（DOING=10）任务可转办
        if (!task.IsDoing()) return Error("任务非进行中，不可转办");
        // 参与者以关系表为判据（聚合副本可能滞后于加签/转办的增量写入）；副本并入仅作仓储不水合时兜底
        var actors = await _repository.FindTaskActorsAsync(taskId!.Value);
        var participants = DedupKeepOrder(actors.Concat(task.ActorIds));
        if (!participants.Contains(fromActor!)) return Error("原办理人不是该任务参与人");
        if (participants.Contains(toActor!)) return Error("目标人已是该任务参与人");
        // ① 摘原人（仅 fromActor 一行）+ ② 加新人（同一 taskId，不新建任务）
        await _repository.RemoveTaskActorAsync(taskId.Value, new List<string> { fromActor! });
        await _repository.AddTaskActorAsync(taskId.Value, new List<string> { toActor! });
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

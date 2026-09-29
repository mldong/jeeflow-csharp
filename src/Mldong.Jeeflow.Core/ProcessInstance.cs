namespace Mldong.Jeeflow.Core;

/// <summary>
/// 流程实例——DDD 聚合根（充血模型，对齐 Java ProcessInstance）。
/// 流程实例是独立的事务边界，包含所有子任务；所有状态修改通过聚合根方法完成。
/// </summary>
public class ProcessInstance
{
    public long? InstanceId { get; set; }
    public long? ParentId { get; set; }
    public long? DefineId { get; set; }
    public int? State { get; set; }
    public string? ParentNodeName { get; set; }
    public string? BusinessNo { get; set; }
    /// <summary>发起人。</summary>
    public string? Operator { get; set; }
    public DateTime? ExpireTime { get; set; }
    public FlowData Variables { get; set; } = new();
    public List<ProcessTask> Tasks { get; set; } = new();
    public DateTime? CreateTime { get; set; }
    public string? CreateUser { get; set; }
    public DateTime? UpdateTime { get; set; }
    public string? UpdateUser { get; set; }

    public static ProcessInstance Create(
        ProcessDefine define, string? op, FlowData? args,
        long? parentId = null, string? parentNodeName = null, IClock? clock = null)
    {
        var now = (clock ?? SystemClock.Instance).Now;
        var instance = new ProcessInstance
        {
            ParentId = parentId,
            ParentNodeName = parentNodeName,
            DefineId = define.Id,
            Operator = op,
            State = (int)WfInstanceState.Doing,
            BusinessNo = args?.GetStr(FlowConst.BusinessNo),
            Variables = args != null ? args.Copy() : new FlowData(),
            Tasks = new List<ProcessTask>(),
            CreateTime = now,
            CreateUser = op,
            UpdateTime = now,
            UpdateUser = op,
        };
        return instance;
    }

    // ═══ 命令方法 ═══

    /// <summary>完成指定任务。</summary>
    public void CompleteTask(long taskId, string? op, FlowData? args, IClock? clock = null)
    {
        var task = FindDoingTask(taskId);
        task.Finish(op, args, clock);
        if (args != null)
        {
            foreach (var kv in args) Variables[kv.Key] = kv.Value;
            // 提取 f_ 前缀变量持久化到流程变量
            var formData = new FlowData();
            foreach (var key in args.Keys)
                if (key.StartsWith(FlowConst.FormDataPrefix, StringComparison.Ordinal))
                    formData[key] = args[key];
            if (formData.Count > 0) AddVariable(formData, clock);
        }
    }

    /// <summary>废弃指定任务 → 流程实例也废弃。</summary>
    public void AbandonTask(long taskId, string? op, IClock? clock = null)
    {
        var task = FindDoingTask(taskId);
        task.Abandon(op, clock);
        State = (int)WfInstanceState.Abandon;
        Touch(op, clock);
    }

    /// <summary>流程完成。</summary>
    public void Finish(IClock? clock = null)
    {
        State = (int)WfInstanceState.Finished;
        UpdateTime = (clock ?? SystemClock.Instance).Now;
    }

    /// <summary>流程拒绝。</summary>
    public void Reject(IClock? clock = null)
    {
        State = (int)WfInstanceState.Reject;
        UpdateTime = (clock ?? SystemClock.Instance).Now;
    }

    /// <summary>强行终止。</summary>
    public void Interrupt(string? op, IClock? clock = null)
    {
        foreach (var task in Tasks) task.Interrupt(op, clock);
        State = (int)WfInstanceState.Interrupt;
        Touch(op, clock);
    }

    /// <summary>唤醒。</summary>
    public void Resume(string? op, IClock? clock = null)
    {
        foreach (var task in Tasks) task.Resume(op, clock);
        State = (int)WfInstanceState.Doing;
        Touch(op, clock);
    }

    /// <summary>挂起。</summary>
    public void Pending(string? op, IClock? clock = null)
    {
        foreach (var task in Tasks) task.Pending(op, clock);
        State = (int)WfInstanceState.Pending;
        Touch(op, clock);
    }

    /// <summary>
    /// 撤回（C28：实例 30 WITHDRAW + 任务 30，非 45；任务态也不是 99）。
    /// issues/113/114：只改写<b>进行中</b>任务（已完成 20 / 已终止 40 行不得被撤回改写），
    /// 且作用于整单（同实例全部进行中任务，不是只撤操作人自己那一条）；
    /// 实例与被撤任务的 <c>update_user</c> 都回写为真实撤回人。
    ///
    /// <para>issues/134 案 A（owner 2026-09-28 拍板）：撤回只允许<b>进行中(10)</b> 的实例。
    /// 实例不是 10（已完成 20 / 已撤回 30 / 强行终止 40 / 已拒绝 45 / 挂起 50 / 已废弃 99，
    /// 含 <c>State</c> 为 null 的未初始化形状——真实实例永远有状态，那一档同样落拒绝支）
    /// ⇒ 抛内部码 <b>20010009</b>（<see cref="WfErr.WithdrawInstanceNotDoing"/>），
    /// <b>一行都不改、不落库</b>。守卫必须排在下面的任务行循环<b>之前</b>：否则已办结实例会被
    /// 静默改写成 30（凭空改历史、调用方看不到任何报错），那正是本案病灶。
    /// 任务行层面那句"已完成/已终止行不改写"的既有保护保持原样。
    /// 出口沿用 issues/121 口径：门面吞掉内部码 ⇒ <c>code=99999999</c> ＋ 逐字文案
    /// 「流程实例非进行中，无法撤回」，文案不含码值。
    /// 取时仍一律 <c>clock ?? SystemClock.Instance</c>（issues/120 域层钟注入，不退化成裸
    /// <c>DateTime.Now</c>），守卫本身不取时。</para>
    /// </summary>
    public void Withdraw(string? op, IClock? clock = null)
    {
        // 守卫排在任务行循环之前（issues/134 案 A）
        if (!IsDoing())
            throw new JeeflowException(WfErr.WithdrawInstanceNotDoing);
        foreach (var task in Tasks)
            if (task.IsDoing())
                task.Withdraw(op, clock);
        State = (int)WfInstanceState.Withdraw;
        Touch(op, clock);
    }

    /// <summary>追加变量。</summary>
    public void AddVariable(FlowData args, IClock? clock = null)
    {
        foreach (var kv in args) Variables[kv.Key] = kv.Value;
        UpdateTime = (clock ?? SystemClock.Instance).Now;
    }

    /// <summary>移除变量。</summary>
    public void RemoveVariable(IClock? clock = null, params string[] keys)
    {
        foreach (var key in keys) Variables.Remove(key);
        UpdateTime = (clock ?? SystemClock.Instance).Now;
    }

    // ═══ 建单到期时间（issues/126 案 A · 基准＝boot2 内置版）═══

    /// <summary>
    /// 任务行的 <c>expire_time</c> 唯一尺子：在<b>建单那一刻</b>按节点到期表达式真算。
    /// 表达式语义逐字取 <see cref="FlowUtil.ProcessTime"/>（变量名档 → 相对档 → 绝对档，顺序不变）。
    /// <b>节点没配（null / 空串）⇒ 这一列保持 NULL</b>——不写 now()、不写 ''、不写 0（owner 2026-09-28）。
    /// 赋一个"建单时刻"是病灶：那一行新建即逾期，跨栈逾期统计（<c>overdueTaskCount</c>）全失真。
    /// </summary>
    /// <param name="expr">节点上配的到期表达式（设计器 JSON 的 properties.expireTime）</param>
    /// <param name="args">变量源：建单三处＝<b>实例变量</b>（boot2 的 execution.getArgs()）；
    /// 回退新建＝<b>随行拷贝那份变量</b>（boot2 的 hisVariable）。两档搞混会让"表达式是个变量名"这一档跨栈得到不同答案。</param>
    /// <param name="clock">本栈是八栈里唯一域层带钟注入的（issues/120）——一律沿调用点的钟，
    /// 缺省才落 <see cref="SystemClock.Instance"/>；<b>不许</b>换成裸 <c>DateTime.Now</c>。</param>
    private static void ApplyExpireTime(
        ProcessTask task, string? expr, FlowData args, IClock? clock)
    {
        if (string.IsNullOrEmpty(expr)) return;   // 没配 ⇒ 这一列一动不动（保持 NULL）
        task.ExpireTime = FlowUtil.ProcessTime(expr, args, clock ?? SystemClock.Instance);
    }

    /// <summary>建单三处的默认变量源＝实例变量（与 boot2 <c>execution.getArgs()</c> 同档）。</summary>
    private void ApplyExpireTime(ProcessTask task, string? expr, IClock? clock) =>
        ApplyExpireTime(task, expr, Variables, clock);

    /// <summary>
    /// 供处理器在"绕过 <see cref="CreateTask"/> 直建任务"的路径上补同一把尺子——issues/126：
    /// 串行会签推进出的<b>下一位成员</b>就是这种路径。基准侧 boot2 的串行推进是回调
    /// <c>createCountersignTask</c>（<c>ProcessTaskServiceImpl:485</c>，内含 <c>:524</c> 那处到期写），
    /// 所以基准形状里"推进新建的那一位"同样带到期时间；不补就是"首成员有、后续没有"。
    /// </summary>
    public void ApplyNodeExpireTime(ProcessTask task, TaskModel taskModel, IClock? clock = null) =>
        ApplyExpireTime(task, taskModel.ExpireTime, Variables, clock);

    /// <summary>创建普通任务。</summary>
    public ProcessTask CreateTask(
        TaskModel taskModel, string? displayName, List<string> actorIds,
        string? op, long? parentTaskId, bool isFirstTaskNode, IClock? clock = null)
    {
        var task = ProcessTask.Create(
            InstanceId, taskModel.Name, displayName,
            taskModel.TaskType, taskModel.PerformType,
            taskModel.Form, actorIds, op, parentTaskId, isFirstTaskNode, clock);
        ApplyExpireTime(task, taskModel.ExpireTime, clock);   // issues/126 A · 写点①普通建单
        Tasks.Add(task);
        return task;
    }

    /// <summary>
    /// 创建会签任务（C8–C10）：串行（SEQUENTIAL）仅创建第一位成员任务并把会签计数状态
    /// 写入该任务变量（operatorList_{node} 全量办理人 / loopCounter_{node} 当前序号 /
    /// nrOfInstances_{node} 总数，任务变量无 csv_ 前缀——C9 对齐 Java），由 CountersignHandler
    /// 在每位成员完成时推进创建下一位——任意时刻恰 1 个 DOING。PARALLEL / 未配置类型保持全员预创建。
    /// </summary>
    public List<ProcessTask> CreateCountersignTasks(
        TaskModel taskModel, List<string> actorIds, string? op,
        long? parentTaskId, bool isFirstTaskNode, IClock? clock = null)
    {
        var list = new List<ProcessTask>();
        if (taskModel.CountersignType == WfCountersignType.Sequential)
        {
            var node = taskModel.Name;
            var first = ProcessTask.Create(
                InstanceId, node, taskModel.DisplayName,
                taskModel.TaskType, taskModel.PerformType,
                taskModel.Form, new List<string> { actorIds[0] }, op,
                parentTaskId, isFirstTaskNode, clock);
            first.Variables[$"{FlowConst.CountersignOperatorList}_{node}"] = new List<object?>(actorIds);
            first.Variables[$"{FlowConst.LoopCounter}_{node}"] = 0;
            first.Variables[$"{FlowConst.NrOfInstances}_{node}"] = actorIds.Count;
            ApplyExpireTime(first, taskModel.ExpireTime, clock);   // issues/126 A · 写点②串行会签首位成员
            list.Add(first);
            Tasks.Add(first);
            return list;
        }
        foreach (var actorId in actorIds)
        {
            var task = ProcessTask.Create(
                InstanceId, taskModel.Name, taskModel.DisplayName,
                taskModel.TaskType, taskModel.PerformType,
                taskModel.Form, new List<string> { actorId }, op,
                parentTaskId, isFirstTaskNode, clock);
            ApplyExpireTime(task, taskModel.ExpireTime, clock);   // issues/126 A · 写点③并行会签全员
            list.Add(task);
            Tasks.Add(task);
        }
        return list;
    }

    /// <summary>
    /// 驳回任务（退回上一步）——血缘版（规范 04 · 退回上一步）：上一步来源＝当前行的 ParentTaskId，
    /// 复活调用方取出的那条历史行；不按模型入边拓扑推（拓扑版会回到本实例没走过的节点，且会
    /// "静默返回 null 不建单"）。history 为 null ⇒ 无血缘文案；canRejected 守卫不过 ⇒ 守卫文案（引擎内部码 20010007、20010008 不进 msg）。
    /// 本栈异常无码位、出口统一 99999999；对外 msg 用固定中文文案、不含引擎内部码（两格语义由文案区分）。
    /// </summary>
    public ProcessTask? RejectTask(ProcessModel model, ProcessTask currentTask, ProcessTask? history, IClock? clock = null)
    {
        const string NoLineage = "上一步任务ID为空，无法驳回至上一步处理";
        if (history == null) throw new JeeflowException(NoLineage);
        var current = model.GetNode(currentTask.TaskName);
        var parent = model.GetNode(history.TaskName);
        if (current == null || parent == null || !FlowUtil.CanRejected(current, parent))
        {
            throw new JeeflowException("无法驳回至上一步处理，请确认上一步骤并非fork、join、suprocess以及会签任务");
        }

        // 复活行的变量只带数据类键（tf_ 与 csv_/会签簿记都是"上次提交"的残留）
        var vars = new FlowData();
        foreach (var kv in history.Variables)
        {
            var k = kv.Key;
            if (k == FlowConst.SubmitType || k == "taskName"
                || k.StartsWith("tf_") || k.StartsWith("csv_")
                || k.StartsWith("loopCounter") || k.StartsWith("nrOfInstances")
                || k.StartsWith("operatorList")) continue;
            vars[k] = kv.Value;
        }
        // 首任务节点那条由发起人提交 ⇒ 参与者取该行 u_userId；其余取该行办结人。
        // 老行没这个键 ⇒ 按 false 处理（宁可派给该行 ActorId，也不用带"仅进行中"判定的现算值）。
        var isFirstRow = vars.TryGetValue(FlowConst.IsFirstTaskNode, out var flag) && flag is true;
        vars[FlowConst.IsFirstTaskNode] = isFirstRow;
        var actor = "";
        if (isFirstRow)
        {
            actor = vars.TryGetValue(FlowConst.UserUserId, out var uid) ? uid?.ToString() ?? "" : "";
            if (string.IsNullOrEmpty(actor)) actor = Operator ?? "";
        }
        else actor = history.ActorId ?? "";
        if (string.IsNullOrEmpty(actor)) throw new JeeflowException(NoLineage);

        var newTask = ProcessTask.Create(
            InstanceId, history.TaskName, history.DisplayName,
            history.TaskType, history.PerformType, history.FormKey,
            new List<string> { actor },
            history.CreateUser,
            history.ParentTaskId,   // 随行拷贝＝"上一步的上一步"，与 mldong-boot2 一致
            isFirstRow, clock);
        newTask.Variables = vars;
        // issues/126 A · 写点④退回/回退新建：并入同一把尺子。变量源仍是随行拷贝那份 vars
        // （＝boot2 的 hisVariable），非空判据与钟的取法（clock ?? SystemClock.Instance）与改前逐字等价。
        if (current is TaskModel curTask)
            ApplyExpireTime(newTask, curTask.ExpireTime, vars, clock);
        Tasks.Add(newTask);
        return newTask;
    }

    /// <summary>创建历史任务记录（自定义节点用，直接 FINISHED）。
    /// 建单不变量同其它路径：parent 与首节点标记由调用点给真值（对齐 Java
    /// <c>ProcessInstance.createHistoryTask</c> 与 <c>CustomModel</c> 的实参），
    /// 不在这里写死 null/false——否则自定义节点把血缘链剪断，且是静默的。</summary>
    public ProcessTask CreateHistoryTask(CustomModel customModel, string? op,
                                         long? parentTaskId, bool isFirstTaskNode,
                                         IClock? clock = null)
    {
        var task = ProcessTask.Create(
            InstanceId, customModel.Name, customModel.DisplayName,
            null, null, null,
            // 参与者＝当前操作人（留痕主体，不是待办收单人）。op 为空时给**空集合**而不是 `[""]`——
            // 往 actor_id 灌空串正是 issues/129／142 B 表那族"空归属值读全库"的进水口。
            op is null or { Length: 0 } ? new List<string>() : new List<string> { op }, op,
            parentTaskId, isFirstTaskNode, clock);
        task.TaskState = (int)WfTaskState.Finished;
        // spec 02 §6.2 第 1bis 条（issues/142 A 批收口，八栈对表判掉的分歧）：
        // 这条 DONE 行必须写 `operator`（落库时绑的是 ActorId）与 `FinishTime`——
        // doneList 走 `state<>10 AND operator=?`、审批记录也按这两列取数，
        // 只写 task_state=20 的留痕在用户面上等于没落过（与第 1 条"查不到的留痕＝没留痕"同一把尺子）。
        // 反过来 ExpireTime 保持 null：CustomModel 只有 clazz/methodName/args/val 四个属性，
        // 记录类没有到期表达式可算，补它就是造默认值（issues/126 owner 口径"没配就留 NULL"）。
        task.ActorId = op;
        task.FinishTime = (clock ?? SystemClock.Instance).Now;
        Tasks.Add(task);
        return task;
    }

    // ═══ 查询方法 ═══

    public List<ProcessTask> GetDoingTasks() => Tasks.Where(t => t.IsDoing()).ToList();

    public List<ProcessTask> GetDoingTasks(string[]? taskNames)
    {
        if (taskNames == null || taskNames.Length == 0) return GetDoingTasks();
        var nameList = taskNames.ToList();
        return Tasks.Where(t => t.IsDoing() && t.TaskName != null && nameList.Contains(t.TaskName)).ToList();
    }

    public List<ProcessTask> GetFinishedTasks() => Tasks.Where(t => t.IsFinished()).ToList();

    public List<ProcessTask> GetDoneTasks(string[]? taskNames)
    {
        if (taskNames == null || taskNames.Length == 0) return GetFinishedTasks();
        var nameList = taskNames.ToList();
        return Tasks.Where(t => t.IsFinished() && t.TaskName != null && nameList.Contains(t.TaskName)).ToList();
    }

    public bool IsAllTasksFinished() => Tasks.All(t => !t.IsDoing());

    public bool IsDoing() => State == (int)WfInstanceState.Doing;

    public bool IsFinished() => State == (int)WfInstanceState.Finished;

    private ProcessTask FindDoingTask(long taskId)
    {
        foreach (var task in Tasks)
        {
            if (taskId == task.TaskId)
            {
                if (!task.IsDoing())
                    throw new JeeflowException($"任务[{taskId}]不是进行中状态");
                return task;
            }
        }
        throw new JeeflowException($"未找到任务[{taskId}]或不在聚合根中");
    }

    private void Touch(string? op, IClock? clock)
    {
        UpdateTime = (clock ?? SystemClock.Instance).Now;
        UpdateUser = op;
    }
}

/// <summary>流程定义值对象（聚合内嵌，对齐 Java ProcessInstance.ProcessDefine）。</summary>
public class ProcessDefine
{
    public long? Id { get; set; }
    public string? Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Type { get; set; }
    public int? State { get; set; }
    /// <summary>流程模型定义（LogicFlow JSON 字节）。</summary>
    public byte[]? Content { get; set; }
    public int? Version { get; set; }
    public string? UpdateUser { get; set; }
    public DateTime? UpdateTime { get; set; }
    public DateTime? CreateTime { get; set; }
    public string? CreateUser { get; set; }
}

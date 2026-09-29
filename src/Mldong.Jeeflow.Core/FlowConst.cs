namespace Mldong.Jeeflow.Core;

/// <summary>流程常量定义（对齐 Java FlowConst，键名逐字一致）。</summary>
public static class FlowConst
{
    public const string BusinessNo = "BUSINESS_NO";
    public const string AdminId = "flow.admin";
    public const string AutoId = "flow.auto";

    public const string ProcessNameKey = "name";
    public const string ProcessDisplayNameKey = "displayName";
    public const string ProcessType = "type";

    public const string ProcessDefineIdKey = "processDefineId";
    public const string ProcessDesignIdKey = "processDesignId";
    public const string ProcessTaskIdKey = "processTaskId";
    public const string ProcessInstanceIdKey = "processInstanceId";

    public const string FormDataPrefix = "f_";
    public const string TaskFormDataPrefix = "tf_";

    public const string ApprovalComment = "tf_approvalComment";
    public const string ApprovalAttachment = "tf_approvalAttachment";
    /// <summary>下一节点执行人（办理时）</summary>
    public const string NextNodeOperator = "tf_nextNodeOperator";
    /// <summary>流程启动时下一步节点执行人</summary>
    public const string ProcessStartNextNodeOperator = "f_nextNodeOperator";
    public const string CcActors = "tf_ccActors";
    public const string CcActorsStart = "f_ccActors";

    /// <summary>转办单跳便捷键：目标人（<c>processTask/transfer</c> 留痕，末跳值）。</summary>
    public const string TransferTo = "tf_transferTo";
    /// <summary>转办单跳便捷键：原因（无值为 <c>""</c>，不写 null）。</summary>
    public const string TransferReason = "tf_transferReason";
    /// <summary>转办跨跳追加式账本：每跳 append 一条
    /// <c>{submitType,fromActor,toActor,reason,time,operator}</c>（只追加不覆盖，六键固定 camelCase，
    /// time 一律 <c>yyyy-MM-dd HH:mm:ss</c>）。</summary>
    public const string TransferHistory = "tf_transferHistory";

    public const string UserUserId = "u_userId";
    public const string UserRealName = "u_realName";
    public const string UserDeptId = "u_deptId";
    public const string UserDeptName = "u_deptName";
    public const string UserPostId = "u_postId";
    public const string UserPostName = "u_postName";

    public const string SubmitType = "submitType";
    public const string AutoGenTitle = "autoGenTitle";
    public const string TaskName = "taskName";
    public const string IsFirstTaskNode = "isFirstTaskNode";

    public const string CountersignVariablePrefix = "csv_";
    public const string NrOfActivateInstances = "nrOfActivateInstances";
    public const string LoopCounter = "loopCounter";
    public const string NrOfInstances = "nrOfInstances";
    public const string NrOfCompletedInstances = "nrOfCompletedInstances";
    /// <summary>会签操作人列表变量键前缀（任务变量，无 csv_ 前缀——C9 对齐 Java）</summary>
    public const string CountersignOperatorList = "operatorList";
    public const string CountersignType = "countersignType";
    public const string CountersignDisagreeFlag = "countersignDisagreeFlag";
    public const string OneVoteVeto = "ONE_VOTE_VETO";

    public const string ActorIdsKey = "actorIds";
    public const string CustomReturnVal = "custom_return_val";

    /// <summary>字段权限键前缀（issues/25/26）</summary>
    public const string FieldPermissionPrefix = "PERMISSION_";
}

/// <summary>流程实例状态（对齐 Java ProcessInstanceStateEnum）。</summary>
public enum WfInstanceState
{
    Doing = 10,
    Finished = 20,
    Withdraw = 30,
    Interrupt = 40,
    Reject = 45,
    Pending = 50,
    Abandon = 99,
}

/// <summary>任务状态（对齐 Java ProcessTaskStateEnum；全集语义见 spec/03）。</summary>
public enum WfTaskState
{
    Doing = 10,
    Finished = 20,
    Withdraw = 30,
    Interrupt = 40,
    Pending = 50,
    Abandon = 99,
}

/// <summary>提交类型（对齐 Java ProcessSubmitTypeEnum，9 值全枚举）。
/// 7 TRANSFER 仅供 <c>processTask/transfer</c> 写留痕，不走 <c>processTask/execute</c> 分发。</summary>
public enum WfSubmitType
{
    Apply = 0,
    Agree = 1,
    Reject = 2,
    Rollback = 3,
    Jump = 4,
    ReApply = 5,
    RollbackToOperator = 6,
    /// <summary>转办（摘原人 + 换新人）。</summary>
    Transfer = 7,
    CountersignDisagree = 20,
}

/// <summary>任务类型（对齐 Java ProcessTaskTypeEnum）。</summary>
public enum WfTaskType
{
    Major = 0,
    Secondary = 1,
    Record = 2,
}

/// <summary>任务参与类型（对齐 Java ProcessTaskPerformTypeEnum）。</summary>
public enum WfPerformType
{
    Normal = 0,
    Countersign = 1,
}

/// <summary>会签类型（对齐 Java CountersignTypeEnum）。</summary>
public enum WfCountersignType
{
    Parallel = 0,
    Sequential = 1,
}

/// <summary>流程定义状态。</summary>
public enum WfDefineState
{
    Disable = 0,
    Enable = 1,
}

/// <summary>流程事件类型——权威码表见 jeeflow-doc <c>docs/spec/11-events.md</c> §11.3（A 套整型）。
/// <para><b>规范名是权威，码值只是本栈内部的附带数值</b>：集成层跨语言判据一律用规范名，
/// 不得拿数字码当判据（各栈码值曾三套并存，132 §11.6 才收口）。成员名按 C# PascalCase 承载，
/// 与规范名的逐字对应见各项注释；4 号位＝CC_CREATE（issues/102 码值不重排 + spec §11.6：
/// 那一格由 Java 死码 PROCESS_TASK_END 让位，1/2/3 不变）。</para>
/// <para>「同一事实只发一次」：5 TASK_COMPLETE 与 6 TASK_REJECT <b>互斥</b>——同一动作走 reject
/// 就不再 fire complete（判据是载荷 submitType，见 <c>JeeflowEngine</c>）。</para></summary>
public enum ProcessEventType
{
    /// <summary>1 <c>PROCESS_INSTANCE_START</c> 实例发起成功（实例行 insert 之后）。sourceId=instanceId。</summary>
    ProcessInstanceStart = 1,
    /// <summary>2 <c>PROCESS_INSTANCE_END</c> 实例进入终态（办结/拒绝共用，靠载荷 state 分）。
    /// 实例 state 更新为 20/30/40/45/50/99 之一并落库之后。sourceId=instanceId。</summary>
    ProcessInstanceEnd = 2,
    /// <summary>3 <c>PROCESS_TASK_START</c> 新待办生成（含会签逐人、回退复活行、子流程任务）。
    /// 每个任务行 saveTask 落库（分到 taskId）之后逐任务 fire。sourceId=taskId。</summary>
    ProcessTaskStart = 3,
    /// <summary>4 <c>CC_CREATE</c> 新增一条抄送记录。cc 行落库之后<b>逐抄送人 fire 一次</b>；
    /// 发起 <c>f_ccActors</c>／办理 <c>tf_ccActors</c>／手动 <c>createCCInstance</c> 三条路径同判
    /// （spec §11.2 原则 1：事实是"存在一条新抄送记录"，与谁触发无关）。sourceId=instanceId。</summary>
    CcCreate = 4,
    /// <summary>5 <c>TASK_COMPLETE</c> 任务被办掉（同意/跳转/会签办理）。
    /// 任务行 state 更新为已完成并落库之后。sourceId=taskId。</summary>
    TaskComplete = 5,
    /// <summary>6 <c>TASK_REJECT</c> 任务被退回/拒绝（含退发起人、软拒绝、跳转回退）。
    /// 退回动作使任务/实例落库之后；与 5 互斥。sourceId=taskId。</summary>
    TaskReject = 6,
    /// <summary>7 <c>TASK_TRANSFER</c> 转办发生。任务参与者被替换并落库之后。sourceId=taskId。</summary>
    TaskTransfer = 7,
    /// <summary>8 <c>TASK_WITHDRAW</c> 撤回发生（实例进入 30）。撤回把实例 state 写 30 落库之后
    /// fire <b>一次</b>（每轮撤回只 fire 一次，不逐任务）。sourceId=instanceId。</summary>
    TaskWithdraw = 8,
    /// <summary>9 <c>INSTANCE_TERMINATED</c> 实例被终止（40 落库之后）。sourceId=instanceId。
    /// <b>本栈暂无 fire 点</b>：门面 40+ action 里没有"终止实例"这一支（spec 06 动作清单同无，
    /// issues/134 门面级注释已注明"门面没有终止实例的 action"），聚合根
    /// <c>ProcessInstance.Interrupt</c> 只有测试调用。补 action 时必须在 UpdateInstanceAsync
    /// <b>落库之后</b> fire 本码，不得先 fire 后落库。</summary>
    InstanceTerminated = 9,
    // 10+ 预留（超时催办／超时自动通过 …）：**本轮不发，仅占号防分叉**（spec §11.3 + §11.4 第 1 条：
    // 八栈都没有时钟扫描器，发了没有触发源）。号一旦发出去不许改语义、不许复用（§11.2 原则 2）。
}

/// <summary>任务类型 codeOf（C4：code/'CODE'/中文 message 容错，兜底 Major）。</summary>
public static class Enums
{
    public static WfTaskType TaskTypeCodeOf(object? code)
    {
        if (code == null) return WfTaskType.Major;
        var s = (code.ToString() ?? "").Trim();
        (WfTaskType e, string name, string message, int codeVal)[] all =
        {
            (WfTaskType.Major, "Major", "主办", 0),
            (WfTaskType.Secondary, "Secondary", "协办", 1),
            (WfTaskType.Record, "Record", "记录", 2),
        };
        var n = CodeOfNumeric(code);
        foreach (var (e, name, message, codeVal) in all)
            if (codeVal == n || name.Equals(s, StringComparison.OrdinalIgnoreCase) || message.Equals(s))
                return e;
        return WfTaskType.Major;
    }

    /// <summary>performType codeOf（C4：1/'1'/'ALL'/'COUNTERSIGN' → Countersign；'ANY' 等 → Normal；兜底 Normal）。</summary>
    public static WfPerformType PerformTypeCodeOf(object? code)
    {
        if (code == null) return WfPerformType.Normal;
        var s = (code.ToString() ?? "").Trim();
        if ("ALL".Equals(s, StringComparison.OrdinalIgnoreCase)
            || "COUNTERSIGN".Equals(s, StringComparison.OrdinalIgnoreCase)
            || "会签参与".Equals(s, StringComparison.Ordinal))
            return WfPerformType.Countersign;
        var n = CodeOfNumeric(code);
        return n == 1 ? WfPerformType.Countersign : WfPerformType.Normal;
    }

    public static WfCountersignType CountersignTypeCodeOf(object? code)
    {
        if (code == null) return WfCountersignType.Parallel;
        var s = (code.ToString() ?? "").Trim();
        foreach (var e in new[] { WfCountersignType.Parallel, WfCountersignType.Sequential })
        {
            var name = Enum.GetName(e) ?? "";
            if (name.Equals(s, StringComparison.OrdinalIgnoreCase)) return e;
        }
        return System.Enum.IsDefined(typeof(WfCountersignType), (int)(CodeOfNumeric(code) ?? -1))
            ? (WfCountersignType)(CodeOfNumeric(code) ?? 0)
            : WfCountersignType.Parallel;
    }

    private static long? CodeOfNumeric(object? code)
    {
        if (code == null) return null;
        if (code is long l) return l;
        if (code is int i) return i;
        if (code is double d) return (long)d;
        return long.TryParse(code.ToString(), out var v) ? v : null;
    }
}

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

/// <summary>流程事件类型（4 号位 = CC_CREATE，issues/102 码值不重排）。</summary>
public enum ProcessEventType
{
    ProcessInstanceStart = 1,
    ProcessInstanceEnd = 2,
    ProcessTaskStart = 3,
    CcCreate = 4,
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

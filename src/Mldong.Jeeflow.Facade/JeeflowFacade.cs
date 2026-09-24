using System.Text;
using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Facade;

/// <summary>
/// 统一门面（对齐 Java JeeflowFacade，40+ action）："接口即 POST + JSON body"风格单入口。
/// 恒 {code,msg,data} 信封；成功 code=0；失败只发明 99999999（不发明 9999xxxx）；
/// unknown/action 兜底同码。出口纪律（C1/C2/C5/C7/C22 + CS1/CS2/CS4）由 <see cref="Outbound"/>
/// 统一递归 stringifier 强制：id 全字符串化（含复数数组）、时间 yyyy-MM-dd HH:mm:ss、
/// 分页恒五键、统计计数 int 出参（issues/105）。
/// </summary>
public partial class JeeflowFacade
{
    private const int OkCode = 0;
    private const int ErrCode = 99999999;

    private static readonly List<int> DefaultStateIn = new() { 10, 20, 30, 40, 45, 50 };
    private static readonly int DefaultStatsLimit = 10;
    private static readonly HashSet<string> ValidGranularity = new() { "hour", "day", "week", "month" };
    private static readonly HashSet<string> ValidDimension = new()
    {
        "state", "define", "category", "approver", "applicant",
        "node", "stuckNode", "stuckApprover", "durationBucket",
    };

    private readonly JeeflowEngine _engine;
    private readonly IProcessRepository _repository;
    private readonly IProcessExtRepository? _extRepository;
    private readonly ServiceContext _context;

    public JeeflowFacade(ServiceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _engine = new JeeflowEngine(context);
        _repository = context.Repository;
        _extRepository = context.ExtRepository;
    }

    public JeeflowEngine Engine => _engine;
    public ServiceContext Context => _context;

    private IClock Clock => _context.ClockOrDefault;
    private IJsonProvider Json => _context.JsonProviderOrDefault;

    /// <summary>统一入口（对象图信封；出口已按契约转换）。</summary>
    public async Task<Dictionary<string, object?>> FlowAsync(string? action, FlowData args)
    {
        if (args == null) args = new FlowData();
        try
        {
            var data = action switch
            {
                // ── 流程定义 ──
                "processDefine/page" => await DefinePageAsync(args),
                "processDefine/detail" => await DefineDetailAsync(args),
                "processDefine/startAndExecute" => await StartAndExecuteAsync(args),
                "processDefine/deploy" => await DeployAsync(args),
                "processDefine/redeploy" => await RedeployAsync(args),
                "processDefine/remove" => await DefineRemoveAsync(args),
                "processDefine/upAndDown" => await DefineUpAndDownAsync(args),
                // ── 流程实例 ──
                "processInstance/page" => await InstancePageAsync(args),
                "processInstance/detail" => await InstanceDetailAsync(args),
                "processInstance/startAndExecute" => await StartAndExecuteAsync(args),
                "processInstance/withdraw" => await WithdrawAsync(args),
                "processInstance/bizData" => await BizDataAsync(args),
                // ── 流程任务 ──
                "processTask/todoList" => await TodoListAsync(args),
                "processTask/doneList" => await DoneListAsync(args),
                "processTask/execute" => await ExecuteAsync(args),
                "processTask/detail" => await TaskDetailAsync(args),
                "processTask/jumpAbleTaskNameList" => await JumpAbleTaskNameListAsync(args),
                "processTask/candidatePage" => await CandidatePageAsync(args),
                "processTask/surrogate" => await TaskSurrogateAsync(args),
                "processTask/addCandidate" => await TaskSurrogateAsync(args),
                "processTask/transfer" => await TaskTransferAsync(args),
                "processTask/latest" => await TaskLatestAsync(args),
                // ── 流程设计 ──
                "processDesign/page" => await DesignPageAsync(args),
                "processDesign/detail" => await DesignDetailAsync(args),
                "processDesign/save" => await DesignSaveAsync(args),
                "processDesign/update" => await DesignUpdateAsync(args),
                "processDesign/updateDefine" => await DesignUpdateDefineAsync(args),
                "processDesign/remove" => await DesignRemoveAsync(args),
                "processDesign/deploy" => await DesignDeployAsync(args),
                "processDesign/redeploy" => await DesignRedeployAsync(args),
                "processDesign/listByType" => await DesignListByTypeAsync(args),
                // ── 视图端点 ──
                "processDefine/getLastByName" => await GetLastByNameAsync(args),
                "processInstance/highLight" => await HighLightAsync(args),
                "processInstance/approvalRecord" => await ApprovalRecordAsync(args),
                "processInstance/getAssigneeTextData" => await GetAssigneeTextDataAsync(args),
                "processInstance/createCCInstance" => await CreateCcInstanceAsync(args),
                "processInstance/updateCCStatus" => await UpdateCcStatusAsync(args),
                "processInstance/ccList" => await CcListAsync(args),
                // ── 委托代理 ──
                "processSurrogate/page" => await SurrogatePageAsync(args),
                "processSurrogate/save" => await SurrogateSaveAsync(args),
                "processSurrogate/update" => await SurrogateUpdateAsync(args),
                "processSurrogate/detail" => await SurrogateDetailAsync(args),
                "processSurrogate/remove" => await SurrogateRemoveAsync(args),
                // ── 统计 ──
                "processInstance/stats/overview" => await StatsOverviewAsync(args),
                "processInstance/stats/trend" => await StatsTrendAsync(args),
                "processInstance/stats/group" => await StatsGroupAsync(args),
                _ => Error($"未知 action: {action}"),
            };
            return data;
        }
        catch (Exception e)
        {
            return Error(e.Message ?? e.ToString());
        }
    }

    /// <summary>统一入口（契约 JSON 出口——经 Outbound 递归 stringifier）。</summary>
    public async Task<string> FlowJsonAsync(string? action, FlowData args) =>
        Outbound.ToJson(await FlowAsync(action, args));

    // ═══ 响应工具（boot2 CommonResult 契约）═══

    internal static Dictionary<string, object?> Ok() => Ok(null);

    internal static Dictionary<string, object?> Ok(object? data) => new()
    {
        ["code"] = OkCode,
        ["msg"] = "成功",
        ["data"] = data,
    };

    internal static Dictionary<string, object?> Error(string msg) => new()
    {
        ["code"] = ErrCode,
        ["msg"] = msg,
    };

    /// <summary>分页出口：恒五键（C22）。</summary>
    internal static Dictionary<string, object?> PageResultOut<T>(PageResult<T> page)
    {
        var rows = new List<object?>();
        foreach (var row in page.Rows)
        {
            rows.Add(row switch
            {
                IProcessRepository.TaskRow t => TaskRowToMap(t),
                IProcessRepository.InstanceRow i => InstanceRowToMap(i),
                IProcessRepository.DefineRow d => DefineRowToMap(d),
                ProcessSurrogate s => SurrogateRowToMap(s),
                ProcessDesign pd => DesignRowToMap(pd),
                Dictionary<string, object?> dict => dict, // 已转换的行（candidatePage 候选）直接透传
                _ => throw new InvalidOperationException("未支持的分页行类型: " + row?.GetType().Name),
            });
        }
        return Ok(new Dictionary<string, object?>
        {
            ["pageNum"] = page.PageNum,
            ["pageSize"] = page.PageSize,
            ["recordCount"] = page.RecordCount,
            ["totalPage"] = page.TotalPage,
            ["rows"] = rows,
        });
    }

    // ═══ 行输出转换（issues/05 字段契约 + 时间格式）═══

    internal static string? FmtTime(DateTime? t) =>
        t?.ToString("yyyy-MM-dd HH:mm:ss");

    internal static Dictionary<string, object?> InstanceRowToMap(IProcessRepository.InstanceRow r) => new()
    {
        ["id"] = r.Id,
        ["parentId"] = r.ParentId,
        ["processDefineId"] = r.ProcessDefineId,
        ["state"] = r.State,
        ["parentNodeName"] = r.ParentNodeName,
        ["businessNo"] = r.BusinessNo,
        ["operator"] = r.Operator,
        ["expireTime"] = FmtTime(r.ExpireTime),
        ["createTime"] = FmtTime(r.CreateTime),
        ["createUser"] = r.CreateUser,
        ["updateTime"] = FmtTime(r.UpdateTime),
        ["updateUser"] = r.UpdateUser,
        ["processDefineName"] = r.ProcessDefineName,
        ["processDefineDisplayName"] = r.ProcessDefineDisplayName,
        ["processDefineVersion"] = r.ProcessDefineVersion,
        ["ext"] = ParseJsonMap(r.Variable),
        ["displayName"] = r.ProcessDefineDisplayName,
        ["version"] = r.ProcessDefineVersion,
    };

    internal static Dictionary<string, object?> TaskRowToMap(IProcessRepository.TaskRow r)
    {
        var instanceExt = ParseJsonMap(r.InstanceVariable);
        var ext = ParseJsonMap(r.Variable);
        // issues/121 P1：引擎建单必写的控制键不算「任务变量非空」，否则新建任务的 ext
        // 永远不再回退实例变量（issues/82-3 既有契约）。
        if (ext.Count == 0 || (ext.Count == 1 && ext.ContainsKey("isFirstTaskNode"))) ext = instanceExt;
        return new Dictionary<string, object?>
        {
            ["id"] = r.Id,
            ["processInstanceId"] = r.ProcessInstanceId,
            ["taskName"] = r.TaskName,
            ["displayName"] = r.DisplayName,
            ["taskType"] = r.TaskType,
            ["performType"] = r.PerformType,
            ["taskState"] = r.TaskState,
            ["operator"] = r.Operator,
            ["finishTime"] = FmtTime(r.FinishTime),
            ["expireTime"] = FmtTime(r.ExpireTime),
            ["formKey"] = r.FormKey,
            ["taskParentId"] = r.TaskParentId,
            ["createTime"] = FmtTime(r.CreateTime),
            ["createUser"] = r.CreateUser,
            ["updateTime"] = FmtTime(r.UpdateTime),
            ["updateUser"] = r.UpdateUser,
            ["processDefineName"] = r.ProcessDefineName,
            ["processDefineDisplayName"] = r.ProcessDefineDisplayName,
            ["instanceCreateTime"] = FmtTime(r.InstanceCreateTime),
            ["ext"] = ext,
            ["instanceExt"] = instanceExt,
            ["version"] = r.ProcessDefineVersion,
            ["taskFormData"] = FormDataOf(ParseJsonMap(r.Variable), FlowConst.TaskFormDataPrefix),
        };
    }

    internal static Dictionary<string, object?> DefineRowToMap(IProcessRepository.DefineRow r) => new()
    {
        ["id"] = r.Id,
        ["name"] = r.Name,
        ["displayName"] = r.DisplayName,
        ["type"] = r.Type,
        ["state"] = r.State,
        ["version"] = r.Version,
        ["createTime"] = FmtTime(r.CreateTime),
        ["createUser"] = r.CreateUser,
        ["updateTime"] = FmtTime(r.UpdateTime),
        ["updateUser"] = r.UpdateUser,
    };

    internal static Dictionary<string, object?> DesignRowToMap(ProcessDesign d) => new()
    {
        ["id"] = d.Id,
        ["name"] = d.Name,
        ["displayName"] = d.DisplayName,
        ["type"] = d.Type,
        ["icon"] = d.Icon,
        ["isDeployed"] = d.IsDeployed,
        ["remark"] = d.Remark,
        ["createTime"] = FmtTime(d.CreateTime),
        ["createUser"] = d.CreateUser,
        ["updateTime"] = FmtTime(d.UpdateTime),
        ["updateUser"] = d.UpdateUser,
    };

    internal static Dictionary<string, object?> SurrogateRowToMap(ProcessSurrogate s) => new()
    {
        ["id"] = s.Id,
        ["processName"] = s.ProcessName,
        ["operator"] = s.Operator,
        ["surrogate"] = s.Surrogate,
        ["startTime"] = FmtTime(s.StartTime),
        ["endTime"] = FmtTime(s.EndTime),
        ["enabled"] = s.Enabled,
        ["createTime"] = FmtTime(s.CreateTime),
        ["createUser"] = s.CreateUser,
        ["updateTime"] = FmtTime(s.UpdateTime),
        ["updateUser"] = s.UpdateUser,
    };

    /// <summary>任务 VO（processTask/detail、taskLatest、instance detail 共用）。</summary>
    internal Dictionary<string, object?> TaskVo(ProcessTask t)
    {
        var vo = new Dictionary<string, object?>
        {
            ["id"] = t.TaskId,
            ["processInstanceId"] = t.ProcessInstanceId,
            ["taskName"] = t.TaskName,
            ["displayName"] = t.DisplayName,
            ["taskType"] = t.TaskType == null ? null : (int)t.TaskType, // C5：出口数字 code
            ["performType"] = t.PerformType == null ? null : (int)t.PerformType,
            ["taskState"] = t.TaskState,
            ["operator"] = t.ActorId,
            ["formKey"] = t.FormKey,
            ["taskParentId"] = t.ParentTaskId,
            ["taskActorIdList"] = new List<object?>(t.ActorIds),
            ["taskFormData"] = FormDataOf(t.Variables, FlowConst.TaskFormDataPrefix),
        };
        return vo;
    }

    // ═══ 内部工具 ═══

    private IProcessExtRepository Ext()
    {
        if (_extRepository == null)
            throw new JeeflowException("未配置 IProcessExtRepository（扩展仓储）");
        return _extRepository;
    }

    /// <summary>deploy 版本管理：按 name 查最新定义，存在 version+1 插新记录，否则从 0 起。</summary>
    private async Task<long> SaveDeployedDefineAsync(ProcessModel model, byte[] bytes)
    {
        var query = new PageQuery(1, 1).Add("t.name", "EQ", model.Name).Add("t.state", "GT", -1);
        var page = await _repository.PageDefinesAsync(query);
        var def = new ProcessDefine();
        var version = 0;
        if (page.Rows.Count > 0)
            version = (page.Rows[0].Version ?? 0) + 1;
        def.Name = model.Name;
        def.DisplayName = model.DisplayName;
        def.Type = model.Type;
        def.State = 1;
        def.Content = bytes;
        def.Version = version;
        await _repository.SaveDefineAsync(def);
        return def.Id!.Value;
    }

    /// <summary>content 归一：字符串/对象/顶层 JSON（issues/27）三态兼容 → UTF-8 字节。</summary>
    private byte[]? ContentBytes(FlowData args)
    {
        if (!args.TryGetValue("content", out var content) || content == null)
        {
            // issues/27：非保留字段（除 processDesignId/operator）整体序列化为 content
            var copy = new FlowData();
            foreach (var kv in args)
            {
                if (kv.Key != FlowConst.ProcessDesignIdKey && kv.Key != "operator") copy[kv.Key] = kv.Value;
            }
            if (copy.Count == 0) return null;
            content = Json.ToJson(copy);
        }
        if (content is IDictionary<string, object?> || content is System.Collections.IEnumerable and not string and not byte[])
        {
            return Encoding.UTF8.GetBytes(Json.ToJson(content)); // content 为对象：序列化
        }
        if (content is byte[] bytes) return bytes;
        return Encoding.UTF8.GetBytes(content.ToString() ?? "");
    }

    private Dictionary<string, object?>? ParseGraph(byte[]? content)
    {
        if (content == null) return null;
        try
        {
            if (Json.FromJson(Encoding.UTF8.GetString(content)) is Dictionary<string, object?> m)
                return m;
        }
        catch
        {
            // 坏 JSON：null（对齐 Java 吞异常返回 null）
        }
        return null;
    }

    /// <summary>JSON 字符串 → Map（坏 JSON 返回空 Map）。</summary>
    internal static Dictionary<string, object?> ParseJsonMap(string? json)
    {
        var result = new Dictionary<string, object?>();
        if (string.IsNullOrEmpty(json)) return result;
        try
        {
            if (DefaultJsonProvider.Instance.FromJson(json) is Dictionary<string, object?> m)
            {
                foreach (var kv in m) result[kv.Key] = kv.Value;
            }
        }
        catch
        {
            // 空 Map
        }
        return result;
    }

    /// <summary>issues/15：f_/tf_ 前缀派生——「带前缀 + 去前缀副本」。</summary>
    internal static Dictionary<string, object?> FormDataOf(IDictionary<string, object?>? vars, string prefix)
    {
        var result = new Dictionary<string, object?>();
        if (vars == null) return result;
        foreach (var kv in vars)
        {
            if (kv.Key != null && kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                result[kv.Key] = kv.Value;
                result[kv.Key[prefix.Length..]] = kv.Value;
            }
        }
        return result;
    }

    /// <summary>id 入参（C3：string/number 双收；不可解析 → null 由调用方报错；&gt;2^53 double 显式报错）。</summary>
    internal static long? ToLong(object? val)
    {
        if (val == null) return null;
        if (val is long l) return l;
        if (val is int i) return i;
        if (val is double d)
        {
            if (Math.Abs(d) > 9007199254740992.0)
                throw new JeeflowException($"id {d} 超出 float64 精确范围（2^53），请以字符串传递");
            return (long)d;
        }
        return long.TryParse(val.ToString(), out var parsed) ? parsed : null;
    }

    /// <summary>批量主键（C15/issues/95）：{ids} 优先、单 {id} 兜底；空/含非法值显式报错。</summary>
    internal static List<long> IdListArgs(FlowData args)
    {
        var ids = args.GetObj("ids");
        var result = new List<long>();
        if (ids is string)
        {
            // 字符串形态 ids：非法（契约要求 ids 为数组）——走单 id 兜底
            var v1 = ToLong(args.GetObj("id"));
            if (v1 != null) result.Add(v1.Value);
        }
        else if (ids is System.Collections.ICollection coll)
        {
            foreach (var id in coll)
            {
                var v = ToLong(id);
                if (v == null) throw new JeeflowException("id 缺失或非法");
                result.Add(v.Value);
            }
        }
        else
        {
            var v = ToLong(args.GetObj("id"));
            if (v != null) result.Add(v.Value);
        }
        if (result.Count == 0) throw new JeeflowException("id 缺失或非法");
        return result;
    }

    internal static int ToInt(object? val, int def)
    {
        if (val == null) return def;
        if (val is int i) return i;
        if (val is long l) return (int)l;
        return int.TryParse(val.ToString(), out var parsed) ? parsed : def;
    }

    internal static string? ToStr(object? val) => val?.ToString();

    internal static string ToStr(object? val, string def) => val?.ToString() ?? def;

    /// <summary>系统代执行（flow.auto）/ 超级管理员（flow.admin）放行——<c>IsAllowed</c> 既有约定，
    /// 撤回（issues/114）与转办（issues/115）共用同一判据。</summary>
    internal static bool IsPrivilegedOperator(string? op) =>
        string.Equals(FlowConst.AutoId, op, StringComparison.OrdinalIgnoreCase)
        || string.Equals(FlowConst.AdminId, op, StringComparison.OrdinalIgnoreCase);

    /// <summary>时间入参：`yyyy-MM-dd HH:mm:ss` 与 ISO T 双格式（C26/issues/77）。</summary>
    internal static DateTime? ParseTime(object? val)
    {
        if (val == null) return null;
        var s = val.ToString()?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", null,
                System.Globalization.DateTimeStyles.None, out var t1)) return t1;
        if (DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.None, out var t2)) return t2;
        return null;
    }

    internal static List<string> ToStringList(object? val)
    {
        var list = new List<string>();
        if (val is string sVal)
        {
            if (sVal.Length > 0)
                foreach (var part in sVal.Split(',')) list.Add(part.Trim());
        }
        else if (val is System.Collections.ICollection coll)
        {
            foreach (var o in coll) list.Add(o?.ToString() ?? "");
        }
        return list.Where(t => t.Length > 0).ToList();
    }

    /// <summary>流程 JSON 中第一个任务节点 id（isFirstTaskNode 用）。</summary>
    private static string? FirstTaskNodeId(Dictionary<string, object?>? jsonObject)
    {
        if (jsonObject != null &&
            jsonObject.TryGetValue("nodes", out var n) && n is List<object?> nodes)
        {
            foreach (var node in nodes)
            {
                if (node is Dictionary<string, object?> nd &&
                    "snaker:task".Equals(nd.TryGetValue("type", out var t) ? t?.ToString() : null, StringComparison.Ordinal))
                {
                    return nd.TryGetValue("id", out var id) ? id?.ToString() : null;
                }
            }
        }
        return null;
    }
}

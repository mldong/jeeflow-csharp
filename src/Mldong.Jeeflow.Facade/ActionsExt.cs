using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Persist;

namespace Mldong.Jeeflow.Facade;

/// <summary>40+ action 实现：流程设计 / 委托代理 / bizData / 统计（对齐 Java JeeflowFacade）。</summary>
public partial class JeeflowFacade
{
    // ═══ 流程设计（需扩展仓储）═══

    private async Task<Dictionary<string, object?>> DesignPageAsync(FlowData args)
    {
        var query = new JeeflowQueryParser().Parse(args);
        var page = await Ext().PageDesignsAsync(query);
        return PageResultOut(page);
    }

    private async Task<Dictionary<string, object?>> DesignDetailAsync(FlowData args)
    {
        var id = ToLong(args.GetObj("id"));
        var design = await Ext().FindDesignByIdAsync(id);
        if (design == null) return Error("流程设计不存在");
        var data = new Dictionary<string, object?>
        {
            ["id"] = design.Id,
            ["name"] = design.Name,
            ["displayName"] = design.DisplayName,
            ["type"] = design.Type,
            ["icon"] = design.Icon,
            ["isDeployed"] = design.IsDeployed,
            ["remark"] = design.Remark,
        };
        // 最新设计稿内容 + 历史列表
        var hisList = await Ext().ListDesignHisAsync(design.Id!.Value);
        Dictionary<string, object?>? jsonObject = null;
        if (hisList.Count > 0) jsonObject = ParseGraph(hisList[0].Content);
        // issues/07：jsonObject 缺失基本信息时从设计表补齐
        jsonObject ??= new Dictionary<string, object?>();
        if (!jsonObject.ContainsKey("name")) jsonObject["name"] = design.Name;
        if (!jsonObject.ContainsKey("displayName")) jsonObject["displayName"] = design.DisplayName;
        if (!jsonObject.ContainsKey("type")) jsonObject["type"] = design.Type;
        if (!jsonObject.ContainsKey("processDesignId")) jsonObject["processDesignId"] = design.Id;
        data["jsonObject"] = jsonObject;
        data["his"] = hisList.Select(h => (object?)new Dictionary<string, object?>
        {
            ["id"] = h.Id,
            ["processDesignId"] = h.ProcessDesignId,
            ["createTime"] = FmtTime(h.CreateTime),
            ["createUser"] = h.CreateUser,
        }).ToList();
        return Ok(data);
    }

    private async Task<Dictionary<string, object?>> DesignSaveAsync(FlowData args)
    {
        var ext = Ext();
        var op = ToStr(args.GetObj("operator"), "user1");
        var id = ToLong(args.GetObj("id"));
        ProcessDesign design;
        if (id == null)
        {
            design = new ProcessDesign
            {
                Name = ToStr(args.GetObj("name")),
                DisplayName = ToStr(args.GetObj("displayName")),
                Type = ToStr(args.GetObj("type"), "approval"),
                Icon = ToStr(args.GetObj("icon")),
                Remark = ToStr(args.GetObj("remark")),
                IsDeployed = 0,
                CreateUser = op,
                UpdateUser = op,
            };
            await ext.SaveDesignAsync(design);
        }
        else
        {
            design = await ext.FindDesignByIdAsync(id);
            if (design == null) return Error("流程设计不存在");
            if (args.GetObj("displayName") != null) design.DisplayName = ToStr(args.GetObj("displayName"));
            if (args.GetObj("type") != null) design.Type = ToStr(args.GetObj("type"));
            if (args.GetObj("icon") != null) design.Icon = ToStr(args.GetObj("icon"));
            if (args.GetObj("remark") != null) design.Remark = ToStr(args.GetObj("remark"));
            design.UpdateUser = op;
            // 内容快照变更 → 置为未部署（issues/08）
            if (ContentBytes(args) != null) design.IsDeployed = 0;
            await ext.UpdateDesignAsync(design);
        }
        // 内容快照（设计稿内容存历史表）
        var content = ContentBytes(args);
        if (content != null)
        {
            var his = new ProcessDesignHis
            {
                ProcessDesignId = design.Id,
                Content = content,
                CreateUser = op,
            };
            await ext.SaveDesignHisAsync(his);
        }
        return Ok(new Dictionary<string, object?> { ["id"] = design.Id });
    }

    private async Task<Dictionary<string, object?>> DesignRemoveAsync(FlowData args)
    {
        foreach (var id in IdListArgs(args))
        {
            await Ext().RemoveDesignAsync(id);
        }
        return Ok();
    }

    private async Task<Dictionary<string, object?>> DesignDeployAsync(FlowData args)
    {
        var ext = Ext();
        var designId = ToLong(args.GetObj("id"));
        var design = await ext.FindDesignByIdAsync(designId);
        if (design == null) return Error("流程设计不存在");
        var hisList = await ext.ListDesignHisAsync(designId!.Value);
        if (hisList.Count == 0) return Error("流程设计没有内容，无法发布");
        var bytes = hisList[0].Content;
        var model = ModelParser.Parse(bytes, _context);
        var defineId = await SaveDeployedDefineAsync(model, bytes!);
        design.IsDeployed = 1;
        design.UpdateUser = ToStr(args.GetObj("operator"), "system");
        await ext.UpdateDesignAsync(design);
        return Ok(new Dictionary<string, object?> { [FlowConst.ProcessDefineIdKey] = defineId });
    }

    /// <summary>修改流程设计基本信息（对齐 boot3 ProcessDesignController.update，不写设计稿快照）。</summary>
    private async Task<Dictionary<string, object?>> DesignUpdateAsync(FlowData args)
    {
        var ext = Ext();
        var id = ToLong(args.GetObj("id"));
        var design = await ext.FindDesignByIdAsync(id);
        if (design == null) return Error("流程设计不存在");
        if (args.GetObj("name") != null) design.Name = ToStr(args.GetObj("name"));
        if (args.GetObj("displayName") != null) design.DisplayName = ToStr(args.GetObj("displayName"));
        if (args.GetObj("type") != null) design.Type = ToStr(args.GetObj("type"));
        if (args.GetObj("icon") != null) design.Icon = ToStr(args.GetObj("icon"));
        if (args.GetObj("remark") != null) design.Remark = ToStr(args.GetObj("remark"));
        design.UpdateUser = ToStr(args.GetObj("operator"), "system");
        await ext.UpdateDesignAsync(design);
        return Ok();
    }

    /// <summary>更新流程设计定义（issues/08）：content 快照入库 + 同步 name/displayName/type + 置未部署。</summary>
    private async Task<Dictionary<string, object?>> DesignUpdateDefineAsync(FlowData args)
    {
        var ext = Ext();
        var designId = ToLong(args.GetObj(FlowConst.ProcessDesignIdKey));
        var design = await ext.FindDesignByIdAsync(designId);
        if (design == null) return Error("流程设计不存在");
        var bytes = ContentBytes(args);
        if (bytes == null) return Error("content 缺失");
        // 与最新一条相同则不重复入库（对齐 boot3 updateDefine）
        var hisList = await ext.ListDesignHisAsync(designId!.Value);
        if (hisList.Count == 0 || !hisList[0].Content.AsSpan().SequenceEqual(bytes))
        {
            var his = new ProcessDesignHis
            {
                ProcessDesignId = designId,
                Content = bytes,
                CreateUser = ToStr(args.GetObj("operator"), "system"),
            };
            await ext.SaveDesignHisAsync(his);
        }
        // 同步设计基本信息 + 内容变更 → 未部署
        try
        {
            var model = ModelParser.Parse(bytes, _context);
            design.Name = model.Name;
            design.DisplayName = model.DisplayName;
            design.Type = model.Type;
        }
        catch
        {
            // 解析容错
        }
        design.IsDeployed = 0;
        design.UpdateUser = ToStr(args.GetObj("operator"), "system");
        await ext.UpdateDesignAsync(design);
        return Ok();
    }

    /// <summary>重新部署（issues/08）：替换最新定义内容 + 置已部署；替换分支继承原 version（C29/issues/59，禁 null→1 漂移）。</summary>
    private async Task<Dictionary<string, object?>> DesignRedeployAsync(FlowData args)
    {
        var ext = Ext();
        var designId = ToLong(args.GetObj("id"));
        var design = await ext.FindDesignByIdAsync(designId);
        if (design == null) return Error("流程设计不存在");
        var hisList = await ext.ListDesignHisAsync(designId!.Value);
        if (hisList.Count == 0) return Error("流程设计没有内容，无法发布");
        var bytes = hisList[0].Content;
        var model = ModelParser.Parse(bytes, _context);
        // 按 name 取最新定义：有则替换内容（version 不变），无则新建
        var query = new PageQuery(1, 1).Add("t.name", "EQ", model.Name);
        query.OrderBy = "t.version desc";
        var page = await _repository.PageDefinesAsync(query);
        long defineId;
        if (page.Rows.Count == 0)
        {
            defineId = await SaveDeployedDefineAsync(model, bytes!);
        }
        else
        {
            var last = page.Rows[0];
            var def = new ProcessDefine
            {
                Id = last.Id,
                Name = model.Name,
                DisplayName = model.DisplayName,
                Type = model.Type,
                Content = bytes,
                // issues/59：保留原 version（替换语义，不递增）；缺失时兜底会误写 1
                Version = last.Version,
                UpdateUser = ToStr(args.GetObj("operator"), "system"),
            };
            await _repository.UpdateDefineAsync(def);
            defineId = last.Id!.Value;
        }
        design.IsDeployed = 1;
        design.UpdateUser = ToStr(args.GetObj("operator"), "system");
        await ext.UpdateDesignAsync(design);
        return Ok(new Dictionary<string, object?> { [FlowConst.ProcessDefineIdKey] = defineId });
    }

    /// <summary>按类型分组列出流程设计（issues/28）：组序=type 数值升序、组内 id DESC（C14 moon T7 listByType 有序）。</summary>
    private async Task<Dictionary<string, object?>> DesignListByTypeAsync(FlowData args)
    {
        var ext = Ext();
        var query = new JeeflowQueryParser().Parse(args);
        query.PageNum = 1;
        query.PageSize = int.MaxValue - 1;
        var page = await ext.PageDesignsAsync(query);
        // 每 name 最新 define（version 最大）
        var defQuery = new PageQuery(1, int.MaxValue - 1);
        var defPage = await _repository.PageDefinesAsync(defQuery);
        var latestByName = new Dictionary<string, IProcessRepository.DefineRow>();
        foreach (var row in defPage.Rows)
        {
            if (row.Name == null) continue;
            if (!latestByName.TryGetValue(row.Name, out var prev) || (row.Version ?? 0) > (prev.Version ?? 0))
                latestByName[row.Name] = row;
        }
        var groups = new Dictionary<string, object?>();
        foreach (var d in page.Rows)
        {
            var type = d.Type ?? "";
            var items = groups.TryGetValue(type, out var g) && g is List<object?> l
                ? l
                : new List<object?>();
            if (!groups.ContainsKey(type)) groups[type] = items;
            var item = new Dictionary<string, object?>
            {
                ["processDesignId"] = d.Id,
                ["name"] = d.Name,
                ["displayName"] = d.DisplayName,
                ["icon"] = d.Icon,
                ["remark"] = d.Remark,
                ["processDefineId"] = latestByName.TryGetValue(d.Name ?? "", out var latest) ? latest.Id : null,
                ["processDefineState"] = latestByName.TryGetValue(d.Name ?? "", out var latest2) ? latest2.State : null,
            };
            // jsonObject：最新设计稿内容（设计器回显）
            var his = await ext.ListDesignHisAsync(d.Id!.Value);
            if (his.Count > 0) item["jsonObject"] = ParseGraph(his[0].Content);
            items.Add(item);
        }
        return Ok(groups);
    }

    /// <summary>按流程实例回显业务数据（issues/28）：MetaTableReader 未注册明确报错。</summary>
    private async Task<Dictionary<string, object?>> BizDataAsync(FlowData args)
    {
        var instanceIdRaw = args.GetObj(FlowConst.ProcessInstanceIdKey) ?? args.GetObj("id");
        var processInstanceId = ToLong(instanceIdRaw);
        if (processInstanceId == null) return Error("processInstanceId 缺失");
        // 表名：实例 → 定义 relTableName（回落流程 name）
        var inst = await _repository.FindInstanceByIdAsync(processInstanceId);
        if (inst == null) return Error("流程实例不存在");
        var define = await _repository.FindDefineByIdAsync(inst.DefineId);
        if (define == null) return Error("流程定义不存在");
        var tableName = ResolveRelTableName(define.Content);
        if (tableName == null) return Error("流程定义未配置 relTableName");
        // issues/23/28：BizDataReader 由集成方注册（装配点 ctx.BizDataReader），core 不依赖 persist
        if (_context.BizDataReader is not IBizDataReader reader)
        {
            return Error("业务数据读取器未注册（ctx.BizDataReader = new MetaTableReader(...)，需引入 Mldong.Jeeflow.Persist）");
        }
        try
        {
            var result = await reader.ReadByProcessInstanceAsync(tableName, processInstanceId.Value);
            return result == null ? Ok() : Ok(result);
        }
        catch (Exception e)
        {
            return Error("业务数据读取失败: " + (e.InnerException?.Message ?? e.Message));
        }
    }

    /// <summary>从流程定义 content 顶层解析 relTableName（缺省回落 name）。</summary>
    private static string? ResolveRelTableName(byte[]? content)
    {
        if (content == null) return null;
        try
        {
            if (DefaultJsonProvider.Instance.FromJson(System.Text.Encoding.UTF8.GetString(content))
                is not Dictionary<string, object?> meta) return null;
            var tableName = meta.TryGetValue("relTableName", out var rt) ? rt?.ToString()?.Trim() : null;
            if (string.IsNullOrEmpty(tableName))
                tableName = meta.TryGetValue("name", out var n) ? n?.ToString()?.Trim() : null;
            return string.IsNullOrEmpty(tableName) ? null : tableName;
        }
        catch
        {
            return null;
        }
    }

    // ═══ 委托代理（需扩展仓储）═══

    private async Task<Dictionary<string, object?>> SurrogatePageAsync(FlowData args)
    {
        var query = new JeeflowQueryParser().Parse(args);
        var page = await Ext().PageSurrogatesAsync(query);
        return PageResultOut(page);
    }

    private async Task<Dictionary<string, object?>> SurrogateSaveAsync(FlowData args)
    {
        var ext = Ext();
        var op = ToStr(args.GetObj("operator"), "user1");
        var id = ToLong(args.GetObj("id"));
        ProcessSurrogate surrogate;
        if (id == null)
        {
            surrogate = new ProcessSurrogate
            {
                CreateUser = op,
                CreateTime = Clock.Now,
                Operator = op, // 授权人 = 操作人（新建必有）
            };
        }
        else
        {
            surrogate = await ext.FindSurrogateByIdAsync(id);
            if (surrogate == null) return Error("委托记录不存在");
        }
        ApplySurrogateFields(surrogate, args, op);
        if (id == null)
        {
            await ext.SaveSurrogateAsync(surrogate);
        }
        else
        {
            await ext.UpdateSurrogateAsync(surrogate);
        }
        return Ok(new Dictionary<string, object?> { ["id"] = surrogate.Id });
    }

    /// <summary>委托更新（issues/77）：按 id 全字段更新，授权人缺省时保留原值。</summary>
    private async Task<Dictionary<string, object?>> SurrogateUpdateAsync(FlowData args)
    {
        var ext = Ext();
        var op = ToStr(args.GetObj("operator"), "user1");
        var id = ToLong(args.GetObj("id"));
        if (id == null) return Error("id 缺失");
        var surrogate = await ext.FindSurrogateByIdAsync(id);
        if (surrogate == null) return Error("委托记录不存在");
        ApplySurrogateFields(surrogate, args, op);
        await ext.UpdateSurrogateAsync(surrogate);
        return Ok(new Dictionary<string, object?> { ["id"] = surrogate.Id });
    }

    /// <summary>委托详情（issues/77）：按 id 查单条，返回行结构（时间格式化）。</summary>
    private async Task<Dictionary<string, object?>> SurrogateDetailAsync(FlowData args)
    {
        var id = ToLong(args.GetObj("id"));
        var surrogate = await Ext().FindSurrogateByIdAsync(id);
        if (surrogate == null) return Error("委托记录不存在");
        return Ok(SurrogateRowToMap(surrogate));
    }

    /// <summary>委托写入公共字段。授权人仅在显式传入时覆盖（避免 update 清空原授权人）。</summary>
    private static void ApplySurrogateFields(ProcessSurrogate s, FlowData args, string op)
    {
        s.ProcessName = ToStr(args.GetObj("processName"));
        if (args.ContainsKey("operator")) s.Operator = ToStr(args.GetObj("operator"));
        s.Surrogate = ToStr(args.GetObj("surrogate"));
        s.StartTime = ParseTime(args.GetObj("startTime"));
        s.EndTime = ParseTime(args.GetObj("endTime"));
        s.Enabled = ToInt(args.GetObj("enabled"), 1); // C26：enabled=0 不得折叠成 1
        s.UpdateUser = op;
    }

    private async Task<Dictionary<string, object?>> SurrogateRemoveAsync(FlowData args)
    {
        foreach (var id in IdListArgs(args))
        {
            await Ext().RemoveSurrogateAsync(id);
        }
        return Ok();
    }
}

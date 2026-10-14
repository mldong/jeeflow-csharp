using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/214 · audit8 队列 R46（C99 §3.3 牙-5）——**出口契约变换（id 串化／时间格式化）
/// 与壳调用点的判据钉（T0）**。
///
/// 为什么钉在本层：csharp 的出口变换 `Outbound.Transform`（`Core/Json/Outbound.cs:33-58`）
/// 在 HTTP 面不是引擎自交付——引擎 `FlowAsync`（`Facade/JeeflowFacade.cs:61`）返回**裸对象图**
/// （id 为 long），生产壳显式再调 `Outbound.ToJson(result)`（`mldong-csharp-jeeflow/
/// src/Mldong.WfCore/WfEndpoints.cs:95`）；引擎内便利腿 `FlowJsonAsync` 同为
/// `Outbound.ToJson(await FlowAsync(...))`（`JeeflowFacade.cs:356-357`）。⇒
/// ・L1-03 拾取的 `processDefine/page.rows[0].id` 在"壳去 pass"时**必红**（裸 long 出
///   number，runner `is_str_id` 只收 str）——T1 的**无 pass 序列化负向对照**把该因果链
///   钉成可复跑判据（C99 牙-5 行"门禁不红"的现读判定：对 L1-03 **不成立**）；
/// ・**嵌套 id 行**（`his[].id`／`his[].processDesignId`／`tasks[].id`／实例顶部
///   `id/processDefineId`）此前 HTTP 零断言（C5-L3 §6「runner 只取顶层第一行」）——T2/T3 本层补钉；
/// ・**时间半不经 pass**：L2-25 六枚观测出口的时间列全部构建期 `FmtTime` 成形
///   （`JeeflowFacade.cs:405-406` 等）⇒ L2-25 对"壳去 pass"变异无感、不属壳依赖面
///   （T4 钉此，纠正牙-5 行"L1-03/L2-25 测的是壳的自觉"的 L2-25 半边）。
///
/// 两半口径（本轮现读，逐行核过）：
/// ① id 半＝**壳依赖**：define/instance/task/design/surrogate 页行与 TaskVo、his 行 id 键
///    是裸 `long?`（`DefineRowToMap`／`InstanceRowToMap`／`TaskRowToMap`／`TaskVo`／
///    `ActionsExt.cs:47-48`），依赖出口 pass 串化；approvalRecord 行是**例外**（构建期显式
///    `t.TaskId?.ToString()`，`ActionsMain.cs:543`——该处注释自称"本栈出口没有 id 兜底 pass"，
///    与 `Outbound.cs` 现读相反，audit8 C5-L3 §10-5 已记，本卡不改 src）。
/// ② 时间半＝**引擎自交付**：时间列出口串不经 pass、不依赖调用方姿势。
///
/// 不在射程：壳本身（`WfEndpoints.cs` 的 `Results.Json` 变异属集成仓，HTTP 全链探针
/// 未跑，因果由本层负向对照钉）；`Outbound.Transform` 的 DateTime 分支单测（SpikeTests
/// 五枚已有）；approvalRecord 构建期串化档（既有 FacadeTests 已断）。
/// </summary>
public class ExitPassShellPattern214Tests
{
    private static readonly Regex FmtRe = new(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$");
    private static readonly DateTime ClockBase = new(2026, 8, 1, 9, 0, 0);

    private static (JeeflowFacade Facade, MemoryRepository Repo, MemoryExtRepository Ext, JeeflowEngine Engine) New()
    {
        var repo = new MemoryRepository();
        var ext = new MemoryExtRepository(repo, null);
        var ctx = new ServiceContext(repo, ext);
        ctx.Clock = new FixedClock(ClockBase);
        ctx.IdGenerator = new AtomicIdGenerator(1, ctx.Clock);
        ctx.UserProvider = new TestUserProvider();
        TestInfra.RegisterBuiltins(ctx);
        repo.Configure(ctx);
        return (new JeeflowFacade(ctx), repo, ext, new JeeflowEngine(ctx));
    }

    /// <summary>壳序列化姿势（WfEndpoints.cs:95 同款）＝对 FlowAsync 裸输出补 `Outbound.ToJson`。</summary>
    private static JsonElement ShellPatternJson(Dictionary<string, object?> raw) =>
        JsonDocument.Parse(Outbound.ToJson(raw)).RootElement;

    /// <summary>无 pass 的默认序列化（"壳改回 `Results.Json(result)`"的实质：缺出口变换那一腿）。</summary>
    private static JsonElement NoPassJson(Dictionary<string, object?> raw) =>
        JsonDocument.Parse(JsonSerializer.Serialize(raw)).RootElement;

    private static long SeedDefineWithTime(MemoryRepository repo, string name)
    {
        var define = new ProcessDefine
        {
            Name = name,
            DisplayName = name,
            Type = "approval",
            State = 1,
            Version = 1,
            Content = System.Text.Encoding.UTF8.GetBytes(TestInfra.LoadFlow("01-simple")),
            CreateTime = ClockBase,
            UpdateTime = ClockBase,
        };
        repo.SaveDefineAsync(define).GetAwaiter().GetResult();
        return define.Id!.Value;
    }

    /// <summary>T1（L1-03 拾取链）：define 页行 id＝壳依赖——壳姿势出 String；无 pass 出 Number。</summary>
    [Fact]
    public async Task T1_DefinePageIdCell_ShellPatternString_NoPassNumber()
    {
        var (facade, repo, _, _) = New();
        SeedDefineWithTime(repo, "r46-t1");

        var raw = await facade.FlowAsync("processDefine/page",
            new FlowData { ["pageNum"] = 1, ["pageSize"] = 5 });
        Assert.Equal(0, Convert.ToInt32(raw["code"]));

        // 引擎边界事实：FlowAsync 裸输出里 id 是 long（未串化）
        var data = (Dictionary<string, object?>)raw["data"]!;
        var rows = (List<object?>)data["rows"]!;
        Assert.NotEmpty(rows);
        var row0 = (Dictionary<string, object?>)rows[0]!;
        Assert.IsType<long>(row0["id"]);

        // 壳姿势（WfEndpoints.cs:95）：String——runner L1-03 现读拾取面的通过形状
        var shellRoot = ShellPatternJson(raw);
        var shellId = shellRoot.GetProperty("data").GetProperty("rows")[0].GetProperty("id");
        Assert.Equal(JsonValueKind.String, shellId.ValueKind);
        Assert.Equal(row0["id"]!.ToString(), shellId.GetString());

        // 引擎便利腿（FlowJsonAsync）同形（同一 pass，两挂载点不漂）
        var fj = JsonDocument.Parse(await facade.FlowJsonAsync("processDefine/page",
            new FlowData { ["pageNum"] = 1 })).RootElement;
        Assert.Equal(JsonValueKind.String,
            fj.GetProperty("data").GetProperty("rows")[0].GetProperty("id").ValueKind);

        // 负向对照（"壳改回 Results.Json(result)"的实质）：无 pass ⇒ Number ⇒ is_str_id 拒收 ⇒ L1-03 必红
        var noPass = NoPassJson(raw);
        Assert.Equal(JsonValueKind.Number,
            noPass.GetProperty("data").GetProperty("rows")[0].GetProperty("id").ValueKind);
    }

    /// <summary>T2：实例详情嵌套 id（顶部 id/processDefineId 与 tasks[].id/processInstanceId）＝壳依赖。</summary>
    [Fact]
    public async Task T2_InstanceDetailNestedIds_ShellPatternString_NoPassNumber()
    {
        var (facade, repo, _, engine) = New();
        var defineId = SeedDefineWithTime(repo, "r46-t2");
        var inst = await engine.StartProcessInstanceByIdAsync(defineId, "user1",
            new FlowData { [FlowConst.BusinessNo] = "R46-T2" });
        var iid = inst.InstanceId!.Value;

        var raw = await facade.FlowAsync("processInstance/detail", new FlowData { ["id"] = iid });
        Assert.Equal(0, Convert.ToInt32(raw["code"]));
        var data = (Dictionary<string, object?>)raw["data"]!;
        Assert.IsType<long>(data["id"]);
        Assert.IsType<long>(data["processDefineId"]);
        var tasks = (List<object?>)data["tasks"]!;
        Assert.NotEmpty(tasks);
        var task0 = (Dictionary<string, object?>)tasks[0]!;
        Assert.IsType<long>(task0["id"]);

        var sd = ShellPatternJson(raw).GetProperty("data");
        Assert.Equal(JsonValueKind.String, sd.GetProperty("id").ValueKind);
        Assert.Equal(JsonValueKind.String, sd.GetProperty("processDefineId").ValueKind);
        foreach (var t in sd.GetProperty("tasks").EnumerateArray())
        {
            Assert.Equal(JsonValueKind.String, t.GetProperty("id").ValueKind);
            Assert.Equal(JsonValueKind.String, t.GetProperty("processInstanceId").ValueKind);
        }

        var noPassTask0 = NoPassJson(raw).GetProperty("data").GetProperty("tasks")[0];
        Assert.Equal(JsonValueKind.Number, noPassTask0.GetProperty("id").ValueKind);
    }

    /// <summary>T3：设计详情嵌套 id（his[].id／his[].processDesignId＋顶层 id）＝壳依赖（HTTP 零断言面）。</summary>
    [Fact]
    public async Task T3_DesignDetailHisNestedIds_ShellPatternString_NoPassNumber()
    {
        var (facade, _, _, _) = New();
        var r = await facade.FlowAsync("processDesign/save", new FlowData
        {
            ["name"] = "r46-t3", ["displayName"] = "r46-t3",
            ["content"] = "{\"name\":\"r46-t3\"}",
        });
        Assert.Equal(0, Convert.ToInt32(r["code"]));
        var did = Convert.ToInt64(((Dictionary<string, object?>)r["data"]!)["id"]);

        var raw = await facade.FlowAsync("processDesign/detail", new FlowData { ["id"] = did });
        Assert.Equal(0, Convert.ToInt32(raw["code"]));
        var data = (Dictionary<string, object?>)raw["data"]!;
        var his = (List<object?>)data["his"]!;
        Assert.NotEmpty(his);
        var his0 = (Dictionary<string, object?>)his[0]!;
        Assert.IsType<long>(his0["id"]);
        Assert.IsType<long>(his0["processDesignId"]);

        var sd = ShellPatternJson(raw).GetProperty("data");
        Assert.Equal(JsonValueKind.String, sd.GetProperty("id").ValueKind);
        foreach (var h in sd.GetProperty("his").EnumerateArray())
        {
            Assert.Equal(JsonValueKind.String, h.GetProperty("id").ValueKind);
            Assert.Equal(JsonValueKind.String, h.GetProperty("processDesignId").ValueKind);
        }

        var noPassHis0 = NoPassJson(raw).GetProperty("data").GetProperty("his")[0];
        Assert.Equal(JsonValueKind.Number, noPassHis0.GetProperty("id").ValueKind);
    }

    /// <summary>T4（L2-25 半边纠正）：时间列在裸 FlowAsync 输出里已是格式化串——不经 pass、不依赖壳。</summary>
    [Fact]
    public async Task T4_TimeCells_FormattedInRawOutput_NotShellDependent()
    {
        var (facade, repo, _, _) = New();
        SeedDefineWithTime(repo, "r46-t4");

        var raw = await facade.FlowAsync("processDefine/page",
            new FlowData { ["pageNum"] = 1, ["pageSize"] = 5 });
        var data = (Dictionary<string, object?>)raw["data"]!;
        var row0 = (Dictionary<string, object?>)((List<object?>)data["rows"]!)[0]!;
        var createTime = Assert.IsType<string>(row0["createTime"]);
        Assert.Equal("2026-08-01 09:00:00", createTime);
        Assert.Matches(FmtRe, createTime);
        Assert.Matches(FmtRe, Assert.IsType<string>(row0["updateTime"]));

        // 设计 his 行同理（L2-25 的另一观测出口）
        var r = await facade.FlowAsync("processDesign/save", new FlowData
        {
            ["name"] = "r46-t4d", ["displayName"] = "r46-t4d",
            ["content"] = "{\"name\":\"r46-t4d\"}",
        });
        var did = Convert.ToInt64(((Dictionary<string, object?>)r["data"]!)["id"]);
        var dRaw = await facade.FlowAsync("processDesign/detail", new FlowData { ["id"] = did });
        var dHis = (List<object?>)((Dictionary<string, object?>)dRaw["data"]!)["his"]!;
        Assert.Matches(FmtRe, Assert.IsType<string>(((Dictionary<string, object?>)dHis[0]!)["createTime"]));

        // 无 pass 序列化同样出格式化串（该半"壳变不红"的现读事实——与 id 半形成对照）
        Assert.Equal(JsonValueKind.String,
            NoPassJson(raw).GetProperty("data").GetProperty("rows")[0].GetProperty("createTime").ValueKind);
    }
}

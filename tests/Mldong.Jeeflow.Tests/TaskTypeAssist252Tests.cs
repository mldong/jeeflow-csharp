using Xunit;
using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/252 · 节点级 <c>taskType=1</c>（协办）**解析期禁配**（C# 栈 T0）。
///
/// <para>契约出处：spec 02 §4.4 末条 bullet ＋ §4 字段表 <c>taskType</c> 行。协办档今后只由运行期
/// <c>processTask/aidant</c> 派生（issues/253，本轮不做）；节点上配协办在 snaker 原义下是死路——
/// 协办任务办结不产生执行对象、跳转吃同一道闸 ⇒ 实例仍运行中却零待办无后继（流程悬挂）。
/// 所以这一档在**解析期**就拦，不是运行时降级。</para>
///
/// <para><b>本栈容错集合现读</b>（与 php/rust/moon 不同，判据按本栈实际折值写）：
/// <c>Enums.TaskTypeCodeOf</c> 的内联容错表＝<c>(Major,"Major","主办",0)</c>／
/// <c>(Secondary,"Secondary","协办",1)</c>／<c>(Record,"Record","记录",2)</c>，
/// 命中判据是 <c>codeVal == CodeOfNumeric(code) || name.Equals(s, OrdinalIgnoreCase) || message.Equals(s)</c>
/// （s＝<c>code.ToString().Trim()</c>）。⇒ 能折成协办(1) 的输入形＝数字 <c>1</c>、串 <c>"1"</c>、
/// <c>"Secondary"</c> 的**任意大小写**（SECONDARY／secondary／Secondary）、中文 <c>"协办"</c>。
/// 本栈**不认** <c>"ASSIST"</c>（那是 php 的常量名）也不认 <c>"Aidant"</c>（设计器现状值）⇒
/// 两者按 issues/252 §5-3 边界**维持既有静默回落 Major**，不得扩成 fail-fast（issues/245 口径）。</para>
///
/// <para>夹具内联 JSON，**不碰共享 <c>flows/</c>**（该目录有镜像与漂移门禁，唯一编辑源在 jeeflow-java）。</para>
/// </summary>
public class TaskTypeAssist252Tests
{
    /// <summary>逐字契约文案：八栈同尺；改字面量要走 issue，不要顺手改。</summary>
    private const string Verbatim = "节点[review]不允许配置协办任务类型（taskType=1）";

    // ═══ 夹具：start → apply → review → end ═══

    private static string Node(string id, string type, string props, string text) =>
        "{\"id\":\"" + id + "\",\"type\":\"" + type + "\",\"x\":0,\"y\":0,"
        + "\"properties\":{" + props + "},\"text\":{\"value\":\"" + text + "\"}}";

    private static string Edge(string id, string from, string to) =>
        "{\"id\":\"" + id + "\",\"type\":\"snaker:transition\",\"sourceNodeId\":\"" + from
        + "\",\"targetNodeId\":\"" + to + "\",\"properties\":{}}";

    /// <summary>review 节点的 taskType 用**原文片段**注入（数字/串/null 各形）。</summary>
    private static string Chain(string taskTypeRaw) =>
        "{\"name\":\"tt252\",\"displayName\":\"协办禁配夹具\",\"type\":\"approval\",\"nodes\":["
        + Node("start", "snaker:start", "", "开始") + ","
        + Node("apply", "snaker:task",
            "\"form\":\"apply-form\",\"taskType\":0,\"performType\":0,\"assignee\":\"applicant\"", "发起申请") + ","
        + Node("review", "snaker:task",
            "\"form\":\"form-r\",\"assignee\":\"userA\",\"performType\":0,\"taskType\":" + taskTypeRaw, "审批") + ","
        + Node("end", "snaker:end", "", "结束")
        + "],\"edges\":["
        + Edge("e1", "start", "apply") + "," + Edge("e2", "apply", "review") + ","
        + Edge("e3", "review", "end") + "]}";

    /// <summary>review 节点**真正不写** taskType 键（缺省档）。</summary>
    private static string ChainAbsent() =>
        Chain("0").Replace("\"performType\":0,\"taskType\":0", "\"performType\":0");

    /// <summary>start→apply→rec(custom)→end：记录类节点上写 taskType，供"custom 不受影响"那格。</summary>
    private static string ChainCustom(string taskTypeRaw) =>
        "{\"name\":\"tt252-custom\",\"displayName\":\"协办禁配夹具\",\"type\":\"approval\",\"nodes\":["
        + Node("start", "snaker:start", "", "开始") + ","
        + Node("apply", "snaker:task",
            "\"form\":\"apply-form\",\"taskType\":0,\"performType\":0,\"assignee\":\"applicant\"", "发起申请") + ","
        + Node("rec", "snaker:custom", "\"clazz\":\"noopHandler\",\"taskType\":" + taskTypeRaw, "记录") + ","
        + Node("end", "snaker:end", "", "结束")
        + "],\"edges\":["
        + Edge("e1", "start", "apply") + "," + Edge("e2", "apply", "rec") + ","
        + Edge("e3", "rec", "end") + "]}";

    private static ProcessModel Parse(string flowJson)
    {
        var (_, _, ctx) = TestInfra.NewEngineWithCtx();
        return ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(flowJson), ctx);
    }

    private static JeeflowException ParseFails(string flowJson)
    {
        var (_, _, ctx) = TestInfra.NewEngineWithCtx();
        return Assert.Throws<JeeflowException>(() =>
            ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(flowJson), ctx));
    }

    private static TaskModel ReviewOf(ProcessModel model) =>
        Assert.IsAssignableFrom<TaskModel>(model.GetNode("review"));

    // ═══ 正向：凡折成协办(1) 的输入形 ⇒ 解析期报错、零副作用 ═══

    /// <summary>本栈容错表能折成 Secondary(1) 的全部输入形（JSON 原文片段）。</summary>
    public static TheoryData<string, string> AssistForms => new()
    {
        { "int 1", "1" },
        { "string \"1\"", "\"1\"" },
        { "string \"Secondary\"（枚举名原样）", "\"Secondary\"" },
        { "string \"SECONDARY\"（忽略大小写）", "\"SECONDARY\"" },
        { "string \"secondary\"（忽略大小写）", "\"secondary\"" },
        { "string \"协办\"（中文 message）", "\"协办\"" },
        { "string \" 1 \"（ToString 后 Trim）", "\" 1 \"" },
        { "float 1.9（CodeOfNumeric 的 double 档取整）", "1.9" },
    };

    [Theory]
    [MemberData(nameof(AssistForms))]
    public void AssistFormsAreRejectedAtParseTime(string label, string raw)
    {
        var ex = ParseFails(Chain(raw));
        Assert.True(Verbatim == ex.Message, $"{label}：期望「{Verbatim}」，实际「{ex.Message}」");
        Assert.DoesNotContain("2001", ex.Message);   // 内部码不得外泄
        Assert.DoesNotContain("审批", ex.Message);   // 取节点 id，不取 display_name
    }

    /// <summary>报错发生在**解析**阶段：拿不到模型 ⇒ 后续建单/落库无从发生（零副作用）。</summary>
    [Fact]
    public void RejectionYieldsNoModel()
    {
        var (_, _, ctx) = TestInfra.NewEngineWithCtx();
        Assert.Throws<JeeflowException>(() =>
            ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(Chain("1")), ctx));
    }

    /// <summary>申请节点（首任务节点）配协办同样拦——禁配不看节点位置，只看折值。</summary>
    [Fact]
    public void ApplyNodeAssistIsAlsoRejected()
    {
        var flow = Chain("0").Replace(
            "\"form\":\"apply-form\",\"taskType\":0",
            "\"form\":\"apply-form\",\"taskType\":1");
        var ex = ParseFails(flow);
        Assert.Equal("节点[apply]不允许配置协办任务类型（taskType=1）", ex.Message);
    }

    /// <summary>逐字文案常量自证（摘掉禁配判据 ⇒ 上面几格全红，本格钉住文案本身）。</summary>
    [Fact]
    public void PinnedMessageMatchesSpec()
    {
        Assert.Equal("节点[{0}]不允许配置协办任务类型（taskType=1）", Enums.MsgTaskTypeAssistForbidden);
        Assert.Equal(Verbatim, string.Format(Enums.MsgTaskTypeAssistForbidden, "review"));
    }

    // ═══ 回归：其余输入形维持既有折值，不扩成 fail-fast ═══

    /// <summary>不可识别值（含设计器现状写的 Aidant）与主办档 ⇒ 解析成功且仍落 Major。</summary>
    public static TheoryData<string, string> ToleratedForms => new()
    {
        { "int 0", "0" },
        { "string \"0\"", "\"0\"" },
        { "string \"Major\"", "\"Major\"" },
        { "string \"MAJOR\"（忽略大小写）", "\"MAJOR\"" },
        { "string \"主办\"", "\"主办\"" },
        { "string \"Aidant\"（设计器现状值，八栈都不认）", "\"Aidant\"" },
        { "string \"ASSIST\"（php 常量名，本栈容错表不认）", "\"ASSIST\"" },
        { "空串", "\"\"" },
        { "null", "null" },
    };

    [Theory]
    [MemberData(nameof(ToleratedForms))]
    public void NonAssistFormsStillParseAndFoldToMajor(string label, string raw)
    {
        var model = Parse(Chain(raw));
        Assert.True(WfTaskType.Major == ReviewOf(model).TaskType,
            $"{label}：既有静默回落主办口径不得被本轮改动");
    }

    /// <summary>缺省不写键 ⇒ 仍落 Major（与改前逐字同形）。</summary>
    [Fact]
    public void AbsentKeyFoldsToMajor()
    {
        Assert.Equal(WfTaskType.Major, ReviewOf(Parse(ChainAbsent())).TaskType);
    }

    /// <summary>RECORD(2) 档不受影响：数字、数字串、枚举名、中文 message 四形都原样落 Record。</summary>
    [Theory]
    [InlineData("2")]
    [InlineData("\"2\"")]
    [InlineData("\"Record\"")]
    [InlineData("\"记录\"")]
    public void RecordTypeIsUntouched(string raw)
    {
        Assert.Equal(WfTaskType.Record, ReviewOf(Parse(Chain(raw))).TaskType);
    }

    /// <summary>
    /// custom（记录类）节点不受影响：本刀只在 <c>TaskModel</c> 那一支，
    /// <c>CustomModel</c> 根本不读 taskType ⇒ 节点上写 1 也放行。
    /// </summary>
    [Fact]
    public void CustomNodeWithAssistValueStillParses()
    {
        var model = Parse(ChainCustom("1"));
        Assert.IsAssignableFrom<CustomModel>(model.GetNode("rec"));
    }
}
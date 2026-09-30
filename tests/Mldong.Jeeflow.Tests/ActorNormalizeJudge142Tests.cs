using Xunit;
using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// 归属值判据单点本体（issues/142 B 批 · spec 06 §2.11 ＋ §2.10 同一枚 · C# 栈）。
///
/// <para>§2.11 末句要求「复用 §2.10 已落地的那一枚单点……<b>不要再抄第二份</b>，两份判据迟早分叉」：
/// 本栈把 cc 侧的 <see cref="PageQuery.NormalizeCcActors"/> 升格为通用的
/// <c>PageQuery.NormalizeActors</c>（判据本体），旧名<b>保留为转发</b>（已发布 NuGet 公开成员不删），
/// 再补两形入口（逗号串／集合／标量）与单人档（<c>transfer</c> 的 fromActor/toActor、
/// <c>updateCCStatus</c> 的 operator）。本文件钉的就是这一枚判据本身的四条硬要求：</para>
/// <list type="number">
/// <item>两形同判据（串腿与集合腿一个答案，含嵌套集合）；</item>
/// <item>逐元素 trim、空串/纯空白/null 丢弃、同次调用折叠；</item>
/// <item>标量元素按 <c>InvariantCulture</c> 收敛成字符串，绝不出现类型名；</item>
/// <item>反向哨兵：<c>"0"</c>／<c>"00"</c>／<c>"a"</c> 是三张不同的脸，都不是空值。</item>
/// </list>
/// </summary>
public class ActorNormalizeJudge142Tests
{
    private static List<object?> Col(params object?[] items) => items.ToList();

    // ═══ ① 两形同判据 ═══

    [Fact]
    public void StringFormAndCollectionFormGiveTheSameAnswer()
    {
        var byString = PageQuery.NormalizeActors((object?)" 20001 ,, 20002 ,");
        var byCollection = PageQuery.NormalizeActors(Col(" 20001 ", "", "   ", "20002"));

        Assert.Equal(new List<string> { "20001", "20002" }, byString);
        Assert.Equal(byString, byCollection);
    }

    [Fact]
    public void EmptyCollectionIsTheSameArmAsEmptyString()
    {
        Assert.Empty(PageQuery.NormalizeActors(Col("", "  ", null)));
        Assert.Empty(PageQuery.NormalizeActors((object?)null));
        Assert.Empty(PageQuery.NormalizeActors(Col()));
        Assert.Empty(PageQuery.NormalizeActors((object?)""));       // "".Split(',') 的那一个空元素被丢掉
        Assert.Empty(PageQuery.NormalizeActors((object?)"   "));
    }

    [Fact]
    public void NestedCollectionIsFlattenedNotStringified()
    {
        var actors = PageQuery.NormalizeActors(Col(Col("20101", "  "), "20102"));

        Assert.Equal(new List<string> { "20101", "20102" }, actors);
        Assert.DoesNotContain(actors, a => a.Contains("System.Collections"));   // 类型名形状绝不允许
    }

    [Fact]
    public void DictionaryFormYieldsNoActorInsteadOfTypeNameOrKeyValuePair()
    {
        var actors = PageQuery.NormalizeActors(
            new Dictionary<string, object?> { ["a"] = "20201" });

        Assert.Empty(actors);
    }

    // ═══ ② trim ＋ 丢空 ＋ 折叠 ═══

    [Fact]
    public void ValuesAreTrimmedAndDuplicatesFoldedInOrder()
    {
        var actors = PageQuery.NormalizeActors(Col(" 20301 ", "20301", "\t20302\n", "20303", " 20301"));

        Assert.Equal(new List<string> { "20301", "20302", "20303" }, actors);
    }

    // ═══ ③ 标量元素收敛成字符串（InvariantCulture，不吃区域设置）═══

    [Fact]
    public void NumericElementsBecomeInvariantStrings()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // 德区设置下 1234.5 的小数点是逗号；判据必须钉 InvariantCulture，否则同一份数据
            // 在两台机器上落两个不同的归属值（跨栈对表时那一格恒红的经典形状）
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("de-DE");
            var actors = PageQuery.NormalizeActors(Col(20401L, 1234.5, (short)7));
            Assert.Equal(new List<string> { "20401", "1234.5", "7" }, actors);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void BooleanElementFollowsCrossStackValueOfSemantics()
    {
        Assert.Equal(new List<string> { "true" }, PageQuery.NormalizeActors(Col(true)));
    }

    // ═══ ④ 反向哨兵：判据只吃空值，不吃"看起来像空/像同一个数" ═══

    [Fact]
    public void ZeroLikeValuesAreDistinctPeople()
    {
        var actors = PageQuery.NormalizeActors(Col("0", "00", "000", "a", " 0 "));

        // "0"/"00"/"000"/"a" 四个人都在（松散比较把 '0'=='00' 折成同一个人就是 php 那把尺子的反面）；
        // " 0 " trim 后与 "0" 是同一个人 ⇒ 折叠
        Assert.Equal(new List<string> { "0", "00", "000", "a" }, actors);
    }

    [Fact]
    public void OnlyBlankValuesAreDroppedNotFalsyLookingOnes()
    {
        var actors = PageQuery.NormalizeActors("0,,false, ,a");

        Assert.Equal(new List<string> { "0", "false", "a" }, actors);
    }

    // ═══ 旧名转发：cc 一支与任务一支必须走同一枚（严禁第二份判据）═══

    [Fact]
    public void CcNameForwardsToTheSameSingleJudge()
    {
        var raw = new string?[] { " 20501 ", "20501", "", "  ", null, "0", "00", "a" }
            .Select(x => x!).ToList();

        Assert.Equal(PageQuery.NormalizeActors(raw), PageQuery.NormalizeCcActors(raw));
        Assert.Equal(new List<string> { "20501", "0", "00", "a" }, PageQuery.NormalizeCcActors(raw));
    }

    // ═══ 单人档（transfer 的 fromActor/toActor、updateCCStatus 的 operator）═══

    [Fact]
    public void SingleValueArmTakesExactlyOneActor()
    {
        Assert.Equal("20601", PageQuery.NormalizeActorValue(Col(" 20601 ")));   // 集合形态收敛
        Assert.Equal("20602", PageQuery.NormalizeActorValue(" 20602 "));         // 标量 trim
        Assert.Equal("20603", PageQuery.NormalizeActorValue(20603L));            // 数字标量
        Assert.Null(PageQuery.NormalizeActorValue(Col("20604", "20605")));       // 多人不是"一个人"
        Assert.Null(PageQuery.NormalizeActorValue(Col("", "  ")));               // 全空＝没填
        Assert.Null(PageQuery.NormalizeActorValue("   "));
        Assert.Null(PageQuery.NormalizeActorValue(null));
        Assert.Null(PageQuery.NormalizeActorValue(Col()));
        Assert.Null(PageQuery.NormalizeActorValue(
            new Dictionary<string, object?> { ["a"] = "20606" }));               // 字典形态没有归属值
    }

    [Fact]
    public void SingleValueArmDoesNotSplitCommas()
    {
        // 契约上 fromActor/toActor 是"一个人"的标量：拆开等于静默丢掉其余人，
        // 所以单人档只做 trim 不做逗号拆分（与集合档的分工写在这里钉住）
        Assert.Equal("20701,20702", PageQuery.NormalizeActorValue(" 20701,20702 "));
    }

    // ═══ 主键档与归属档是两件事 ═══

    [Fact]
    public void TaskIdArmIsStrictWhileActorArmIsLenient()
    {
        // 归属值可有可无（丢了就行）
        Assert.Empty(PageQuery.NormalizeActors(Col("", "  ", null)));
        // 主键没有就是调用方写错了 ⇒ 响亮报错，不得拿 0 往下落库
        Assert.Equal(20801L, FlowUtil.RequireTaskId(20801L));
        Assert.Throws<JeeflowException>(() => FlowUtil.RequireTaskId(null));
        Assert.Throws<JeeflowException>(() => FlowUtil.RequireTaskId(0));
        Assert.Throws<JeeflowException>(() => FlowUtil.RequireTaskId(-1));
    }

    [Fact]
    public void TaskIdErrorMessageCarriesNoInternalCode()
    {
        // issues/121 口径：内部码不进出口 msg
        var ex = Assert.Throws<JeeflowException>(() => FlowUtil.RequireTaskId(0));
        Assert.DoesNotContain("2001", ex.Message);
        Assert.Equal("processTaskId 缺失或非法", ex.Message);
    }
}

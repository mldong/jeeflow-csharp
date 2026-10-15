using Xunit;
using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/234 丙-9（R90 拍 A）：决策节点「首条真边获胜」——命中即 break（c# 半边）。
/// 改前 foreach 无 break，多真边全部执行＝多路并行流转。变异＝摘 break，判据复红。
/// </summary>
public class DecisionFirstTrueEdge234Tests
{
    private sealed class RecordingNode : NodeModel
    {
        public string Tag { get; }
        public List<string> Hits { get; }
        public RecordingNode(string tag, List<string> hits) { Tag = tag; Hits = hits; }
        internal override Task ExecAsync(Execution execution)
        {
            Hits.Add(Tag);
            return Task.CompletedTask;
        }
    }

    private sealed class StubTrueEvaluator : IExpressionEvaluator
    {
        public object? Eval(string expression, IDictionary<string, object?> context) => true;
    }

    private static (DecisionModel Decision, Execution Execution) NewDecision()
    {
        var repo = new MemoryRepository();
        var ctx = new ServiceContext(repo) { ExpressionEvaluator = new StubTrueEvaluator() };
        return (new DecisionModel(), new Execution { Context = ctx });
    }

    private static TransitionModel Edge(DecisionModel from, string to, string expr, NodeModel target)
    {
        var tm = new TransitionModel { Source = from, To = to, Expr = expr, Target = target };
        from.Outputs.Add(tm);
        return tm;
    }

    [Fact]
    public async Task FirstTrueExprEdgeWins()
    {
        var hits = new List<string>();
        var (decision, execution) = NewDecision();
        var e1 = Edge(decision, "t1", "1==1", new RecordingNode("t1", hits));
        Edge(decision, "t2", "2==2", new RecordingNode("t2", hits));

        await decision.ExecAsync(execution);

        Assert.Equal(new[] { "t1" }, hits);
        Assert.True(e1.Enabled);
    }
}

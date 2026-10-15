using Xunit;
using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/231 R63：TaskModelPropertyKeys 黑名单补 width/height/preInterceptors/postInterceptors 四枚——
/// 已解析进模型的键不得再镜像一份进 ext（java hasField 沿父类链上溯同案 c# 半边）。
/// 变异＝黑名单摘回四枚，<see cref="ParsedModelKeysNotMirroredIntoExt"/> 复红。
/// </summary>
public class TaskExtMirrorKeys231Tests
{
    private const string Flow = """
        {"name": "ext-231", "displayName": "ext镜像判据", "type": "approval",
         "nodes": [
           {"id": "start", "type": "snaker:start", "text": {"value": "开始"}},
           {"id": "apply", "type": "snaker:task", "text": {"value": "申请"},
            "properties": {"width": 100, "height": 50,
              "preInterceptors": "a.b.PreOne", "postInterceptors": "a.b.PostOne",
              "form": "apply-form", "bizTag": "keep-me"}},
           {"id": "end", "type": "snaker:end", "text": {"value": "结束"}}
         ],
         "edges": [
           {"id": "e1", "sourceNodeId": "start", "targetNodeId": "apply"},
           {"id": "e2", "sourceNodeId": "apply", "targetNodeId": "end"}
         ]}
        """;

    private static TaskModel ParseApply()
    {
        var (_, _, ctx) = TestInfra.NewEngineWithCtx();
        var model = ModelParser.Parse(System.Text.Encoding.UTF8.GetBytes(Flow), ctx);
        return Assert.IsType<TaskModel>(model.Nodes.First(n => n.Name == "apply"));
    }

    [Fact]
    public void ParsedModelKeysNotMirroredIntoExt()
    {
        var task = ParseApply();
        Assert.Equal("0,0,100,50", task.Layout);
        Assert.Equal("a.b.PreOne", task.PreInterceptors);
        Assert.Equal("a.b.PostOne", task.PostInterceptors);
        Assert.False(task.Ext.ContainsKey("width"), "width 已解析进 Layout，不得再镜像进 ext");
        Assert.False(task.Ext.ContainsKey("height"), "height 已解析进 Layout，不得再镜像进 ext");
        Assert.False(task.Ext.ContainsKey("preInterceptors"), "preInterceptors 已解析进模型，不得再镜像进 ext");
        Assert.False(task.Ext.ContainsKey("postInterceptors"), "postInterceptors 已解析进模型，不得再镜像进 ext");
    }

    [Fact]
    public void UnknownKeysStillMirroredDeclaredKeysExcluded()
    {
        var task = ParseApply();
        Assert.Equal("keep-me", task.Ext["bizTag"]?.ToString());
        Assert.False(task.Ext.ContainsKey("form"), "form 是 TaskModel 声明键：不进 ext（回归）");
        Assert.Equal("apply-form", task.Form);
    }
}

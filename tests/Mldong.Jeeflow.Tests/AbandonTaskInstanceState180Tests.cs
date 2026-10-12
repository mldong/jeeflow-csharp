using Xunit;
using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/180 · abandonTask 摘副作用（owner 2026-10-13 拍板 A）——聚合根级。
///
/// <para>缺陷：AbandonTask 把「任务废弃」连带写成「流程实例也置 99(Abandon)」，spec/04:329 只写
/// 「废弃单个任务」，go/py/node 三栈实例状态不动（对照形状），boot2/boot3 原版全仓零命中
/// （非产品血统、五栈自创）。修复＝删实例 state 写点；任务级废弃与实例元数据（Touch）照旧。</para>
///
/// <para>判据：① 正向——任务落 99 且实例 state 仍 10；② 负向——非进行中任务照旧抛；
/// ③ 回归——其余任务行不动。</para>
/// </summary>
public class AbandonTaskInstanceState180Tests
{
    private static ProcessInstance Instance() =>
        new() { InstanceId = 9001, State = (int)WfInstanceState.Doing };

    private static TaskModel Node(string name) => new() { Name = name, DisplayName = name };

    private static List<string> One() => new() { "u1" };

    /// <summary>① 正向：废弃后任务=99、实例 state 仍 10（改前被连带置 99——issues/180 病灶）。</summary>
    [Fact]
    public void AbandonTaskKeepsInstanceState()
    {
        var inst = Instance();
        var task = inst.CreateTask(Node("task1"), "审批", One(), "op", 0, true);
        task.TaskId = 9527;   // 域层不分配 id（落库才分配），显式设置以走 findDoingTask
        Assert.Equal((int)WfInstanceState.Doing, inst.State);   // 夹具前提
        inst.AbandonTask(9527, "user9");
        Assert.Equal((int)WfTaskState.Abandon, task.TaskState);
        Assert.Equal((int)WfInstanceState.Doing, inst.State);
    }

    /// <summary>② 负向：非进行中任务照旧拒（夹具先办结），实例态不动。</summary>
    [Fact]
    public void AbandonTaskRejectsNonDoingTask()
    {
        var inst = Instance();
        var task = inst.CreateTask(Node("task1"), "审批", One(), "op", 0, true);
        task.TaskId = 9527;
        task.Finish("u1", new FlowData());
        Assert.Throws<JeeflowException>(() => inst.AbandonTask(9527, "user9"));
        Assert.Equal((int)WfInstanceState.Doing, inst.State);
    }

    /// <summary>③ 回归：只废目标任务，其余行不动。</summary>
    [Fact]
    public void AbandonTaskLeavesOtherRowsUntouched()
    {
        var inst = Instance();
        var t1 = inst.CreateTask(Node("task1"), "审批", One(), "op", 0, true);
        t1.TaskId = 9527;
        var t2 = inst.CreateTask(Node("task2"), "审批2", One(), "op", 0, false);
        t2.TaskId = 9528;
        inst.AbandonTask(9527, "user9");
        Assert.Equal((int)WfTaskState.Abandon, t1.TaskState);
        Assert.Equal((int)WfTaskState.Doing, t2.TaskState);
        Assert.Equal((int)WfInstanceState.Doing, inst.State);
    }
}

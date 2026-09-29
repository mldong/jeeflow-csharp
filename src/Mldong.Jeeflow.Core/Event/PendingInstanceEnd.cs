namespace Mldong.Jeeflow.Core;

/// <summary>
/// 待播的「实例终态」登记（spec §11.2 原则 3「只在落库之后 fire」／§11.3 码 2／08-compliance 场景 32）。
/// 形状逐字移植 Java 参考实现的 <c>com.mldong.jeeflow.event.PendingInstanceEnd</c>。
///
/// <para><b>为什么要登记而不是就地 fire</b>：结束节点处理器（<c>EndProcessHandler</c>）跑到时，
/// 实例的 <c>State</c> 只在<b>内存聚合根</b>里落定，真正写库的那次
/// <c>IProcessRepository.UpdateInstanceAsync</c> 发生在处理器<b>之后</b>
/// （引擎 <c>PersistTasksAsync</c>／发起路径收口）。就地 fire 会让监听器（站内信、待办角标、
/// persist 回写）反查实例读到旧 state——正是 issues/121、issues/126 两轮"回写序"教训的同一族。</para>
///
/// <para>形状：处理器把「谁（InstanceId）＋ 落库后该是什么（State）＋ 那一行的聚合根（Instance）」
/// 挂到本次流转的 <see cref="Execution"/> 上，由引擎在 <c>UpdateInstanceAsync</c> 成功返回后统一
/// flush（<c>JeeflowEngine.FlushInstanceEndEventsAsync</c>）。<see cref="Instance"/> 一起带上是
/// 必需的——子流程级联里被连带办结的是<b>父实例</b>，它不走子流程这次的 <c>UpdateInstanceAsync</c>，
/// flush 时要拿这个对象去补那次写。</para>
/// </summary>
public sealed class PendingInstanceEnd
{
    public PendingInstanceEnd(long? instanceId, int? state, ProcessInstance instance)
    {
        InstanceId = instanceId;
        State = state;
        Instance = instance;
    }

    /// <summary>事件 <c>sourceId</c> 与载荷 <c>instanceId</c> 用哪个实例的终态。</summary>
    public long? InstanceId { get; }

    /// <summary>处理器落定那一刻的实例状态整数（＝随后写库那一行的 State）。</summary>
    public int? State { get; }

    /// <summary>终态所属的聚合根（引擎按它把父实例那一行补写落库）。</summary>
    public ProcessInstance Instance { get; }
}

using System.Collections.Concurrent;

namespace Mldong.Jeeflow.Core;

/// <summary>
/// 同实例串行化锁（对齐 Java InstanceLock）。
///
/// <para>汇合一次性放行、同一任务重复办理、退回与另一支汇合交错，都必须在同一临界区里
/// 「重验状态 → 建后继任务」。仓储每次调用各走一条连接（事务模板下亦然），行级
/// <c>FOR UPDATE</c> 无法跨调用持有 ⇒ 引擎侧用<b>按实例的进程内互斥</b>兜住。</para>
///
/// <para><b>边界：仅单进程部署（以及全部本地/测试场景）成立</b>。多进程/多节点部署需要仓储侧
/// 提供真正的分布式锁（同 Java <c>InstanceLock</c> 的注释口径）。本栈另有引擎命令级
/// <c>SemaphoreSlim</c>（<c>JeeflowEngine._cmdGate</c>）兜并发办理，本锁负责"同一实例的
/// 两条办理入口（execute / jumpToFirstTaskNode）互斥"，二者不互相替代。</para>
/// </summary>
public static class InstanceLock
{
    private static readonly ConcurrentDictionary<long, SemaphoreSlim> Locks = new();

    /// <summary>按任务所属实例取锁；任务不存在时退化为实例 0 的锁（无副作用）。</summary>
    public static async Task<SemaphoreSlim> ForTaskAsync(IProcessRepository? repository, long? taskId)
    {
        long instanceId = 0L;
        if (repository != null && taskId != null)
        {
            var task = await repository.FindTaskByIdAsync(taskId);
            if (task?.ProcessInstanceId != null) instanceId = task.ProcessInstanceId.Value;
        }
        return Locks.GetOrAdd(instanceId, _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>在指定任务所属实例的锁内执行 <paramref name="action"/>。</summary>
    public static async Task<T> RunAsync<T>(
        IProcessRepository? repository, long? taskId, Func<Task<T>> action)
    {
        var gate = await ForTaskAsync(repository, taskId);
        await gate.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            gate.Release();
        }
    }
}

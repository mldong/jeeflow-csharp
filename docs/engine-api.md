# C# 引擎 API

引擎五方法（全 async，`Mldong.Jeeflow.Core.JeeflowEngine`）——语义与 Java 参考实现一一对应：

| 方法 | 说明 |
|---|---|
| `StartProcessInstanceByIdAsync(defineId, operator, args[, parentId, parentNodeName])` | 发起流程：查定义 → 解析模型 → 注入 u_* 用户信息（仅 start 一次）→ autoGenTitle → 创建聚合根 → 执行开始节点 |
| `ExecuteProcessTaskAsync(taskId, operator, args)` | 办理任务：DOING 校验 → isAllowed（flow.auto/flow.admin 放行）→ 完成聚合任务 → 沿输出边驱动 |
| `ExecuteAndJumpTaskAsync(taskId, operator, args, nodeName)` | 跳转（JUMP=4）/ 退回上一步（ROLLBACK=3，nodeName 传空）。**本栈已是血缘版**（issues/121 P2）：上一步取当前行的 `task_parent_id` 那条历史行并复活它，新 todo 参与者＝该行办结人（该行 `isFirstTaskNode=true` 时取该行 `u_userId`＝发起人）；无血缘报 20010007、`FlowUtil.CanRejected` 守卫不过报 20010008（守卫照 mldong-boot2：入边来源是 fork/join/start 时跳过该条边不再深入，故分支任务退不到 fork 之前的节点）。见规范「引擎操作 04 · 退回上一步（血缘版）」 |
| `ExecuteAndJumpToEndAsync(taskId, operator, args)` | 跳结束：submitType=2 → 实例 45，否则 20 |
| `ExecuteAndJumpToFirstTaskNodeAsync(taskId, operator, args)` | 退回发起人：首任务节点重执行、assignee=发起人 |

## submitType 分发（facade 层，spec §11.2）

| 值 | 语义 | 引擎路径 |
|---|---|---|
| 0/1/5 | 申请/同意/重新提交 | `ExecuteProcessTaskAsync` |
| 2 | 拒绝 | `ExecuteAndJumpToEndAsync` → 45 |
| 3 | 退回上一步 | `ExecuteAndJumpTaskAsync(null)` |
| 4 | 跳转 | `ExecuteAndJumpTaskAsync(taskName)` |
| 6 | 退回发起人 | `ExecuteAndJumpToFirstTaskNodeAsync` |
| 7 | 转办（**不走 execute 分发**，`processTask/transfer` 写留痕用） | `RemoveTaskActorAsync(fromActor)` + `AddTaskActorAsync(toActor)` |
| 20 | 会签拒绝 | `ExecuteProcessTaskAsync` + `countersignDisagreeFlag=1` |

## 聚合根（DDD 充血模型）

- `ProcessInstance.CompleteTask/Withdraw/Finish/Reject/AbandonTask...`：状态只经聚合根方法修改；
  `Withdraw` → 实例 30 + 任务 30（非 45）；`updateInstance` 级联持久化聚合内任务状态。
- `ProcessTask.Finish/Abandon/IsAllowed`：完成校验 DOING + 参与人。
- 会签：串行逐个推进（任务变量 `operatorList_{node}/loopCounter_{node}/nrOfInstances_{node}`）；
  并行全量/表达式完成条件；`ONE_VOTE_VETO` 一票否决；merged 后废弃本节点残留 DOING。

## 事件（引擎只 fire，副作用归集成层）

| 事件 | 时机 |
|---|---|
| `ProcessInstanceStart` | 开始节点执行 |
| `ProcessInstanceEnd` | 办结/拒绝（终态两路都 fire） |
| `ProcessTaskStart` | 任务**落库后**逐任务（sourceId=taskId 可反查） |
| `CcCreate` | 逐抄送人（ccActorId 直传事件体） |

监听器逐个隔离：单监听器异常只记 stderr，不中断其余与主流程。

## 错误与信封

- 引擎错误抛 `JeeflowException`（code：20010001~20010006）。
- 门面出口恒 `{code, msg, data}`：成功 0；失败只发明 `99999999`；未知 action 同码。

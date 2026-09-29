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
  **实例级守卫**（issues/134 案 A，owner 2026-09-28）：`Withdraw` 只允许**进行中(10)** 的实例，
  非 10（20/30/40/45/50/99，含 `State` 为 null）⇒ 抛内部码 20010009，**一行都不改、不落库**，
  门面出口 `code=99999999` ＋ 逐字文案 `流程实例非进行中，无法撤回`；任务行层面"已完成/已终止行不改写"保持原样。
- `ProcessTask.Finish/Abandon/IsAllowed`：完成校验 DOING + 参与人。
- 会签：串行逐个推进（任务变量 `operatorList_{node}/loopCounter_{node}/nrOfInstances_{node}`）；
  并行全量/表达式完成条件；`ONE_VOTE_VETO` 一票否决；merged 后废弃本节点残留 DOING。

## 事件（引擎只 fire，副作用归集成层）

码表唯一权威＝`jeeflow-doc/docs/spec/11-events.md` §11.3（A 套整型，issues/127＋132 立章）。
**规范名是权威，数字码只是本栈附带数值**——集成层跨语言判据一律用规范名（§11.6）。

| 码 | 本栈成员名 | 规范名 | 时机（一律"落库之后"，§11.2 原则 3） | 直传载荷键 |
|---|---|---|---|---|
| 1 | `ProcessInstanceStart` | `PROCESS_INSTANCE_START` | 实例行 insert 之后、开始节点执行 | instanceId |
| 2 | `ProcessInstanceEnd` | `PROCESS_INSTANCE_END` | 办结/拒绝两路都发；处理器**只登记不就地 fire**，由引擎在实例行 `UpdateInstanceAsync` 落库**之后**统一 flush（`FlushInstanceEndEventsAsync`，两个收口点：`PersistTasksAsync` 之后＋发起路径之后）；子流程级联里被连带办结的**父实例**不走子流程那次 update ⇒ flush 先按登记带的聚合根补写父实例那一行再播 | instanceId, state |
| 3 | `ProcessTaskStart` | `PROCESS_TASK_START` | 任务**落库后**逐任务（sourceId=taskId 可反查；含会签逐人、回退复活行） | instanceId, taskId, actors |
| 4 | `CcCreate` | `CC_CREATE` | cc 行落库后**逐抄送人**一次；发起 `f_ccActors`／办理 `tf_ccActors`／手动 `createCCInstance` **三条路径同判**（共用 `ProcessPublisher.NotifyCcCreateAsync`） | ccActorId（直传事件体） |
| 5 | `TaskComplete` | `TASK_COMPLETE` | 任务行 state 落库之后（`PrepareExecutionAsync` 单点，四条办理入口全覆盖） | instanceId, taskId, operator, submitType |
| 6 | `TaskReject` | `TASK_REJECT` | 同上；与 5 **互斥**——submitType ∈ {2,3,6,20} 只发 6 | instanceId, taskId, operator, submitType |
| 7 | `TaskTransfer` | `TASK_TRANSFER` | 参与者被替换**并落库之后**（门面 `processTask/transfer`）；不伴随码 3 | instanceId, taskId, fromActor, toActor, operator |
| 8 | `TaskWithdraw` | `TASK_WITHDRAW` | 实例 state 写 30 落库之后，**每轮只发一次**（不逐任务）；被 issues/134 守卫拒掉的那轮不发 | instanceId, operator |
| 9 | `InstanceTerminated` | `INSTANCE_TERMINATED` | **本栈无 fire 点**：门面没有"终止实例"action，聚合根 `Interrupt` 生产路径零调用者 ⇒ 只占号 | instanceId, operator, reason |
| 10+ | — | *预留* | 超时催办／超时自动通过等，**本轮不发**（八栈无时钟扫描器） | — |

监听器为**列表**且按注册顺序回调；单个监听器抛异常只记日志，不回滚主流程、不中断后续监听器（C13/§11.5）；
零注册时 fire 安全返回。集成层**严禁**在门面/业务侧主动补发事件（§11.1，PHP issues/101 的降级路已作废）。

监听器逐个隔离：单监听器异常只记 stderr，不中断其余与主流程。

## 错误与信封

- 引擎错误抛 `JeeflowException`（code：20010001~20010006、20010009 撤回实例非进行中）。
  退回上一步的 20010007/20010008 与 20010009 同守 issues/121 口径：**内部码不进出口 msg**，
  对外只有 `99999999` ＋ 固定中文文案。
- 门面出口恒 `{code, msg, data}`：成功 0；失败只发明 `99999999`；未知 action 同码。

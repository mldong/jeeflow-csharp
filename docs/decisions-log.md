# jeeflow-csharp 决策台账（decisions-log）

> 方案 §9 开放决策 O1–O8 全部按推荐项执行，逐条留痕；表中未覆盖的新决策点同格式追加（D-M0-* 编号）。

## O1 首发版本号

- 问题：NuGet 首发版本号取值。
- 候选项：`v1.0.0` 独立线 / 跟 1.8.x。
- 所选：**`v1.0.0` 起，独立 1.0.x 线**（Directory.Build.props `Version=1.0.0`）。
- 理由：方案推荐项；NuGet 无 mooncakes 式 0.x 强制；不跟 1.8.x（R3 版本独立）。

## O2 发版执行与账号

- 问题：本次任务是否执行对外发版。
- 所选：**不执行**。本地全部弄完 + 发版材料就绪即收口；nuget.org 账号/API key、github 远端仓（仓名 `jeeflow-csharp`）由用户后补，届时按清单执行（R4）。

## O3 jeeflow-ui `/csharp-api` 代理 + `?lang=csharp` 分段

- 所选：**做**（进 M4 交付，对齐 `/moon-api` 先例）。

## O4 语言标识

- 所选：**`csharp`**（仓名 `jeeflow-csharp`、issue 后缀 `-csharp-`、UI 段 `?lang=csharp`、快照 `csharp.json`）。

## O5 NuGet 包 ID 前缀

- 所选：**`Mldong.Jeeflow.*`**（Core / Repository.MySql / Persist / Facade 四包；发布前 nuget.org 查重）。

## O6 TFM

- 所选：**类库 `net8.0;net10.0` 双目标，demo/test `net10.0`**（`<TargetFrameworks>` 一行成本）。

## O7 demo 公网上线（160 宿主 16087→8093）

- 所选：**后置**，留用户定时机；本机联调不阻塞。

## O8 `bump-version.sh --all` 是否纳入 csharp

- 所选：**纳入**（C# 无 mooncakes 式平台限制；M5 在脚本层落地，不执行）。

---

## D-M0-1 环境变量命名：`JEFFLOW_DB_*` 为主、`JEEFLOW_DB_*` 别名

- 问题：方案 §0.2 R6 写 `JEEFLOW_DB_*`、§2.5 写 `JEFFLOW_DB_*`，两拼写并存。
- 候选项：只认 `JEFFLOW_DB_*`（联邦七语言现状口径）/ 只认 `JEEFLOW_DB_*` / 双认。
- 所选：**主 `JEFFLOW_DB_HOST/PORT/USER/PWD/NAME`（对齐 moon/golang 等兄弟仓 from_env 口径），兼容 `JEEFLOW_DB_*` 别名**（MySqlConnectionFactory.FromEnv）。
- 理由：联邦既有脚本/文档全部用 `JEFFLOW_DB_*`；别名兜底 R6 原文字形，零成本。

## D-M0-2 引擎结构：语义抄 Java（模型树执行）而非 moon（图遍历解释器）

- 问题：moon 引擎为图遍历解释器（NodeModel/EdgeModel 扁平 + engine.execute_node 递归），Java 为模型树（NodeModel.Execute 模板方法 + TransitionModel/Handler 群）。
- 所选：**C# 按 Java 结构 1:1 移植**（BaseModel/NodeModel/TaskModel/TransitionModel/DecisionModel/CustomModel/SubProcessModel/Fork/Join/Start/End + CreateTaskHandler/CountersignHandler/EndProcessHandler/MergeBranchHandler/StartSubProcessHandler），全 async 化。
- 理由：方案 §2.1"行为语义层 Java 是唯一参考"+ C# 语法最接近 Java；checklist C1–C29 作为"第一天写对"护栏。会签计数状态按 **C9 口径存任务变量**（`operatorList_{node}/loopCounter_{node}/nrOfInstances_{node}`，无 csv_ 前缀）——moon 实现存实例变量 `csv_{node}_*` 与其自家 checklist 相悖，不跟。

## D-M0-3 ServiceContext 形态：POCO 构造注入（无静态上下文）

- 问题：Java 用静态 ServiceContext + Context 接口；moon 用 Ctx 泛型结构 + 闭包字段。
- 所选：**POCO 构造注入**（`new ServiceContext(repository, extRepository)` + 小 SPI 可空属性 + 按名注册表：AssignmentHandlers/DecisionHandlers/CustomHandlers/NamedInterceptors/CandidateHandlers + 有序 Interceptors/EventListeners）。模型/处理器经 `Execution.Context` 取用。
- 理由：方案 §2.4 明示"core 无 Microsoft.Extensions.* 依赖，DI 不进 core；SPI 组装用普通构造注入的 ServiceContext POCO"；消除 Java 静态上下文的全局态问题。

## D-M0-4 反射扩展点 → 按名注册表（注册名=Java FQCN）

- 问题：Java 拦截器/assignmentHandler/decisionHandler/candidateHandler/自定义节点 clazz 用 `Class.forName` 反射实例化，C# 无等价机制。
- 所选：**全部走 ServiceContext 按名注册表**；键=Java FQCN（`com.mldong.jeeflow.interceptor.impl.OperatorAssignmentHandler` 等，HandlerRegistry 内置 7+1 条与 Java 逐条一致）；**声明名不可解析 → 显式 JeeflowException**（C20，不静默跳过）。flows 声明的 `com.mldong.jeeflow.test.TestCustomHandler` 在测试中注册同名 handler 承载（等价 Java 测试 classpath）。
- 理由：moon 同款决策（方案 §4 C20）；"注册名=Java FQCN"由方案 §3.1 handler 行明示。

## D-M0-5 子流程保持 Java 现状（死路径不扩展）

- 问题：Java StartSubProcessHandler 传 null defineId（内部死路径，15 共享 flows 无子流程节点）；moon 简化为占位。
- 所选：**保持等价契约**——StartSubProcessHandler 调 engine.StartProcessInstanceByIdAsync(null, …)，null → findDefineById(null)=null → 抛"没有流程定义"；Shared flows 覆盖不到该路径。
- 理由：不自行发明行为（R1）；将来联邦若把子流程做实，全语言同代演进（§4 升级传播流）。

## D-M0-6 TFM 测试矩阵：测试工程只 net10.0

- 问题：类库双目标 net8.0;net10.0，测试是否双跑。
- 所选：**测试工程仅 net10.0**（net8.0 兼容面由类库编译期保证）。
- 理由：测试矩阵翻倍收益低；T0/T1 语义测试与 TFM 无关。

## D-M0-7 会签推进与 C6/C9 细节口径

- 问题：C6"仅会签分支 persist perform_type=1，普通分支保持 0"与 C9"计数存任务变量（无 csv_ 前缀）"。
- 所选：**按 checklist 原文执行**：ProcessTask.PerformType 枚举持久化为 int（会签=1/普通=0/null 不写列）；串行会签计数写**任务变量** `operatorList_{node}`/`loopCounter_{node}`/`nrOfInstances_{node}`；nodeProgress 从任务变量还原全量办理人（M3 facade 侧）。
- 理由：Java 参考实现 + moon 方案 §4 checklist 一致；moon 实现偏差不采。

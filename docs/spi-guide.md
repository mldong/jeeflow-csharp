# SPI 实现指南

引擎 DI 为 POCO `ServiceContext`（构造注入，无静态上下文、无框架依赖）：

```csharp
var ctx = new ServiceContext(repository, extRepository /* 可选 */);
ctx.UserProvider        = new MyUserProvider();      // 必需（引擎运行时）
ctx.JsonProvider        = null;                       // 可空 → 内置 DefaultJsonProvider
ctx.ExpressionEvaluator = null;                       // 可空 → 内置 DefaultExpressionEvaluator
ctx.IdGenerator         = null;                       // 可空 → 内置雪花（EPOCH=1288834974657）
ctx.Clock               = null;                       // 可空 → SystemClock（测试注 FixedClock）
ctx.TransactionTemplate = null;                       // 可空 → 语句级 autocommit（联邦现状）
ctx.ActionPermissionProvider = null;                  // 可空 → 默认 wf:{action /→:}
```

## IProcessRepository（必需，全 async）

21+ 方法：定义/实例/任务/参与人/抄送 CRUD + 分页五键 + stats 纯列查询。
内存实现 `MemoryRepository` 可直接用于测试；生产用 `Mldong.Jeeflow.Repository.MySql`。
要点：id 为空时由实现经 `IIdGenerator` 生成；`FindInstanceByIdAsync` 必须水合 tasks；
`UpdateInstanceAsync` 级联持久化聚合内任务状态；`SaveTask` 参与人全量覆盖、`AddTaskActor` 去重追加。

### 抄送两格（issues/141 G1／G2）

```csharp
// 写侧判重（G2，接口默认实现：不覆写＝维持旧行为）
Task<List<string>> FindCcActorIdsAsync(long instanceId);
Task<List<string>> CreateCcInstanceIfAbsentAsync(long instanceId, string creator, params string[] actorIds);
```

> **`PageCcInstancesAsync` 的归属条件必填**（G1 · spec 06 §2.5）：查询必须带 `cc.actor_id` 的
> 有效条件（非 null、非空串/全空白、非空集合，判据＝`PageQuery.HasEffectiveCondition`）。
> **条件缺失或为空值 ⇒ 返回空页**，严禁退化成"这条条件不加"而放出全部实例；非归属列的空值
> 仍按"没填"忽略（`m_LIKE_*` 传空串照旧放行）。内存仓储与 MySQL 仓储必须给同一个答案——
> 门面 `processInstance/ccList` 恒挂这条条件，这里防的是绕过门面直连仓储的调用方。

> **抄送写侧判重＝幂等空操作**（G2 · spec 06 §4）：同一 `(实例, 被抄送人)` 已有 cc 行时跳过——
> 不新增行、不重置未读、不更新原行时间，也**不 fire CC_CREATE（码 4）**。建 cc 的三条入口
> （发起 `f_ccActors`／办理 `tf_ccActors`／手动 `createCCInstance`）统一调 `CreateCcInstanceIfAbsentAsync`，
> 逐人 fire 的入参＝它返回的**实际新建子集**，子集为空整支不发。查询侧不引入 `DISTINCT`、
> 历史重复行不清理。自带两仓都覆写了 `FindCcActorIdsAsync`；**集成方自实现仓储若不覆写它，
> 判重与"子集才 fire"都吃不到**（默认实现读侧恒空集＝每次照旧全量插入），本方法为接口默认实现，
> 不改既有集成方源码也能编译。

> **空抄送人不建 cc 行**（G10 · spec 06 §2.10）：三条入口解析抄送人集合时，**空串、纯空白、
> 数组里的空元素一律丢弃，落库与比较的值取 `Trim` 后的串**；丢完为空 ⇒ 不建任何 cc 行、
> 也**不 fire CC_CREATE（码 4）**，手动腿此时与"空集合"同档（`actorIds 缺失` 错误信封，
> 不是新错误码）。判据单点＝`PageQuery.NormalizeActors`（旧名 `PageQuery.NormalizeCcActors`
> 保留为它的转发，两者同一枚尺子），**两层都挡**：
> ① 漏斗层＝引擎 `HandleCcActorsAsync` ＋门面 `createCCInstance`；
> ② 写侧层＝`CreateCcInstanceAsync` 的实现义务（自带两仓已按它实现）＋ 接口默认实现
> `CreateCcInstanceIfAbsentAsync`。集成方自实现仓储时**必须自己在最底层写入口丢空值并 trim**——
> 只修漏斗的话，绕过引擎/门面直连仓储的调用方照样能把空归属值灌进 `actor_id`
> （issues/129 那族"空 operator 读全库"的病根）。`"".Split(',')` 在 C# 与 Java 一样得到
> **一个空元素**而不是零个，所以逗号串那条腿也必须过归一，别只判 `Length > 0`。

### 任务参与者写侧归一（issues/142 B 批 · spec 06 §2.11）

同一条尺子从抄送侧搬到任务侧（`wf_process_task_actor.actor_id` 也是归属列）。判据单点只有
`PageQuery.NormalizeActors` 一枚，四条腿＋两仓都调它，**不另抄第二份**：

| 入口 | 归一档 |
|---|---|
| `processTask/addCandidate`·`processTask/surrogate` 的 `actorIds` | `NormalizeActors(object)`＝两形入口：逗号串与数组同一判据，逐元素 trim、空串/纯空白/null 丢弃、同次调用折叠 |
| `f_nextNodeOperator`／`tf_nextNodeOperator`（发起腿／消费腿） | 同上；数组元素**逐元素取值**，绝不整条 `ToString()`（那会落一个 `.NET` 类型名当参与者）；数字元素按 `InvariantCulture` 收敛成字符串；归一后为空 ⇒ 与"没填"同档（回落节点 assignee，不落零参与者待办） |
| `processTask/transfer` 的 `fromActor`／`toActor`、`updateCCStatus` 的 `operator` | `NormalizeActorValue(object)`＝单人档：trim＋丢空；集合形态 `["x"]` 收敛为那一个人，0 个或多个 ⇒ 返回 `null` 走既有"必填"信封（契约上是单人，**不拆逗号**，拆了等于静默丢掉其余人） |
| 两仓 `AddTaskActorAsync` | 写侧兜底：入参先过 `NormalizeActors`，落库与判重都取 trim 后的值 |

三条硬要求与 §2.10 同构：① **两层都挡**（只修门面腿 ⇒ 直连仓储照样灌空值）；
② **落库与比较取 trim 后的值**（`" 123 "` 与 `"123"` 是同一个人，不 trim 就打穿写侧判重）；
③ **空入参沿用既有"缺参数"信封**（`processTaskId/actorIds 缺失`／`fromActor 必填`，不新造错误码/文案）；
④ 反向哨兵：**判空一律 trim 后判长**，严禁 `Where(t => t.Length > 0)` 这种 trim 前判长
（兜不住 `"  "`），`"0"`／`"00"`／`"a"` 是三张不同的脸，都不是空值，也不得被松散比较折叠。

> **主键类参数另判一档**（§2.11）：`processTaskId` 缺失/空串/`0`/负数是调用方写错了，
> 必须响亮报错，不得拿 `''`/`0` 当 id 落孤儿行——门面腿走既有缺参数信封，仓储腿一律抛
> `JeeflowException`（文案不带内部码，issues/121 口径；单点＝`FlowUtil.RequireTaskId`）。

## 用户 SPI

| SPI | 方法 | 说明 |
|---|---|---|
| `IUserProvider` | `GetUserAsync(userId)` | 单方法一次返回 UserInfo（userId/realName/deptId/deptName/postId/postName） |
| `IOrgUserProvider` | `FindDeptLeadersAsync / FindDeptMainLeadersAsync / FindByRoleAsync` | 内置组织类 assignmentHandler 数据源；未注册→静默返空 |
| `IUserSearchProvider` | `PageAsync / FindByIdAsync` | candidatePage 无模型候选时的用户分页搜索 |

## 表达式（可选，缺省内置）

`IExpressionEvaluator.Eval(expr, context)`：内置 `DefaultExpressionEvaluator` 支持
比较运算（>= <= == != > <，数值优先）、`#var` 会签门控变量（键后缀匹配）、`${var}` 占位、布尔字面量。

## 按名注册表（键 = Java FQCN 口径）

```csharp
ctx.RegisterAssignmentHandler("com.mldong.jeeflow.interceptor.impl.FormFieldAssigneeHandler", new ...);
ctx.RegisterDecisionHandler(...);           // IDecisionHandler
ctx.CustomHandlers["com.mldong.jeeflow.test.TestCustomHandler"] = customHandler;  // custom 节点
ctx.NamedInterceptors[name] = flowInterceptor;   // 节点 pre/postInterceptors 声明名
ctx.CandidateHandlers[name] = candidateHandler;
ctx.RegisterInterceptor(order, exec => ...);     // 任务创建时的拦截器池（有序）
ctx.RegisterEventListener(listener);             // 事件监听器（逐个隔离）
```

**声明名不可解析 → 显式 JeeflowException（不静默跳过）**。内置 assignmentHandler 清单
（7 个，注册名与 Java 全限定名一致）见 `HandlerRegistry` 构造。

## 业务数据读取（bizData action）

```csharp
ctx.BizDataReader = new MetaTableReader(new TableReader(factory), new JsonMetaProvider("persist-meta"));
```

未注册时 `processInstance/bizData` 显式报错（不静默）。

## 权限码（引擎只供码不鉴权）

`ctx.PermissionCodes(action)`：默认 `wf:{action /→:}`；OR 清单与放行清单
（stats 3 action、detail 类）与 Java DefaultActionPermissionProvider 逐条一致。

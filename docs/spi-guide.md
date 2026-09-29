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

# jeeflow-csharp 执行日志（execution-log）

> 每阶段收尾追加：产物 / 用例数 / gate 结果 / 基线漂移检查。

## M0 仓骨架 + 契约固化 + 去险 spike（2026-09-06）

**产物**

- `jeeflow-hub/jeeflow-csharp/` 独立 git 仓（仅本地 `git init`，remote 后补，R4）。
- 4+1 solution（`Jeeflow.sln`）：Core（零第三方依赖）/ Repository.MySql（MySqlConnector 2.5.0）/ Persist / Facade + Demo；类库 `net8.0;net10.0` 双目标，demo/test `net10.0`。
- `Directory.Build.props`（Nullable enable / 版本 1.0.0）+ `Directory.Packages.props`（中央包管理，版本全钉精确）。
- `flows/`：15 份共享 LogicFlow 副本（自 `jeeflow-java/jeeflow-core/src/test/resources/flows/` 复制，`diff -q` 与 java 源逐字一致）。
- `src/Mldong.Jeeflow.Repository.MySql/Schema/schema-mysql.sql`：DDL 副本（编辑源=java，永不改副本源）。
- `docs/action-manifest.json`：45 action（processDefine 8 + processInstance 14 含 stats 3 + processSurrogate 5 + processTask 9），java↔moon↔csharp 三方 diff **无差集**；spec 基线锚定写入 manifest 头（java@b7ef670 v1.8.27 / doc@4661554 / hub@e032e2a / issue 起 106 `-csharp-` / toolchain dotnet 10.0.400 / 版本线 v1.0.0）。
- `docs/decisions-log.md`：O1–O8 按推荐项 + D-M0-1..7 代决策留痕。
- Core 引擎骨架全量就位（供 spike ④）：模型树（NodeModel 群 + async 模板方法）、处理器群（CreateTask/Countersign/EndProcess/MergeBranch/StartSubProcess）、解析器（LogicFlow→ProcessModel）、SPI（全 async 接口 + POCO ServiceContext）、事件（逐监听器隔离）、元数据（EnumDictRegistry 7 键 + HandlerRegistry FQCN 清单）、雪花 IdGenerator（EPOCH=1288834974657）、出口递归 stringifier（Outbound）、MemoryRepository + MemoryExtRepository（行为对齐 JDBC，M1 用）、MySqlConnectionFactory。

**5 个 spike（gate，全绿）**

| # | spike | 结果 |
|---|---|---|
| ① | `dotnet test` async 用例（Git Bash env，`/g/` 盘符） | ✅ 10/10 绿 |
| ② | MySqlConnector 连 160:3306 建表+1 查询+事务 begin/commit/rollback（`JEFFLOW_DB_*` env，spike 表 9xxxxx 段测后 DROP） | ✅ 1/1 绿 |
| ③ | minimal API :8093 起 + curl：`/health` 200 / `POST /wf/*` 未知 action → `{code:99999999}` / OPTIONS 预检 204 | ✅ |
| ④ | ServiceContext + 全 async 引擎骨架编译 | ✅ solution 0 警告 0 错误（net8.0+net10.0×4 库 + demo + tests） |
| ⑤ | 雪花 id（唯一性/EPOCH 反解）+ 出口递归 stringifier 三态审计（>2^53 单数/复数数组/rows 内嵌全 string；count 字段不被误伤保持 int；null 保持 null） | ✅ 10/10 绿 |

**45 action manifest 双 diff**

- Java `JeeflowFacade.java` 实查 45 action（flow() switch L100–L155）↔ moon manifest 45 action：**无差集**（复用 moon M0 结论 + 本次复核 groups.action 集合断言相等）。
- csharp manifest 以 moon manifest 结构镜像 + csharp 基线元数据；`count=45` 断言过。

**gate 结论**：M0 通过，直入 M1。

**漂移检查**：java@b7ef670 / doc@4661554 与 manifest 基线一致（本阶段新建，无漂移）。

## M1 Core + 内存仓储（T0）（2026-09-06）

**产物**

- Core 全模块（方案 §3.1 行 1）：model（聚合根/PageQuery/PageResult/UserInfo/FlowData）、spi（全 async 接口 + POCO ServiceContext + JeeflowQueryParser + DefaultActionPermissionProvider + DefaultExpressionEvaluator）、engine（JeeflowEngine 全 async 五方法 + Execution）、parser（ModelParser LogicFlow→模型树）、handler（CreateTask/Countersign/EndProcess/MergeBranch/StartSubProcess）、event（ProcessEvent + 逐监听器隔离 Publisher）、metadata（EnumDictRegistry 7 键 + HandlerRegistry FQCN 清单 + 内置 7 assignmentHandler）、memory（MemoryRepository + MemoryExtRepository，行为对齐 JDBC）、json（FlowData + DefaultJsonProvider + Outbound 出口层）、error、id_gen、IClock。
- MemoryRepository 关键语义：id 由仓储 saveXxx 分配（IIdGenerator SPI，对齐 Java JDBC）；findInstanceById 水合 tasks；updateInstance 级联任务状态（v1.0.1，不含 business_no 列——JDBC SQL 口径）；saveTask 参与人全量覆盖 / addTaskActor 去重追加；分页白名单 + 默认 id DESC + 五键；stats 纯列查询 9 方法。

**用例数**：`dotnet test`（memory）**100 用例全绿**，构成：
- spike 10（async/雪花/出口三态审计/骨架/元数据）
- 引擎基础 7（start/def 不存在/任务不存在/权限拒绝/无扩展仓储/autoGenTitle+u_* 注入/BusinessNo）
- 事件 5（TASK_START 落库后 fire 可反查 / CC_CREATE 逐人直传 + cc 行 / 无监听器零副作用 / 拒绝路径 fire INSTANCE_END / 监听器逐个隔离）
- 合规 c01–c22（22 个具名场景，15 flows 驱动）+ 扩展 c23–c31（软拒绝/一票否决/比例表达式/软拒后续/废弃任务负向/08 全链硬断言/jump-end-45/字段权限 resume）+ custom 节点 2（handler 执行 + C20 不可解析显式报错）
- submitType 矩阵 11（8 值枚举/2→45/3 退回操作人/4 跳转含首任务节点强制发起人/非法节点名负向/6 退回发起人/015 普通完成/非处理人负向/tf_nextNodeOperator 覆盖/withdraw 30-30）
- 单元测试包 33（parser 9/聚合根 5/FlowUtil 5/求值器 4/m_ 解析+内存分页 7/元数据权限 2）

**gate**

- 全绿 ✅；负向变异：串行会签 `merged=true` 变异 → C06/C28 红（2 fail）→ 还原复绿（100 pass）✅
- CS3 门禁 grep `.Result|.Wait()|GetAwaiter().GetResult()` src/ 零命中 ✅
- CS7 审计 `dotnet list package --include-transitive`：Core 双 TFM 零包引用（零第三方）✅
- 量级对标 Moon T0 117：100（inventories 全覆盖，数量级一致）

**gate 结论**：T0 过，直入 M2。

**漂移检查**：flows/ 与 java@b7ef670 逐字一致（`diff -q` 无输出）✅

## M2 Repository.MySql（T1）（2026-09-06）

**产物**

- `MySqlRepository`（IProcessRepository 全 21+ 方法，SQL 逐条对齐 JdbcProcessRepository）：定义/实例/任务/参与人/抄送写读、分页五键（白名单 buildWhere/buildOrder + LIMIT/OFFSET 内联非负整数 C21）、NULL 安全读（GetStr/GetLong/GetInt/GetDateTime/GetBytes 显式 DBNull）、聚合水合（findInstanceById 级联 tasks）、updateInstance 级联任务状态（v1.0.1）、saveTask 参与人全量覆盖 / addTaskActor 去重追加、stats 纯列 9 方法（C23，avg int 出参）。
- `MySqlExtRepository`（14 方法）：design/his/surrogate 全 CRUD+分页；removeDesign 级联删历史；getSurrogate operator/enabled/surrogate<>operator/时间窗/精确优先全流程兜底（id DESC LIMIT 1）。
- `MySqlTransactionTemplate` 真实现：单连接 BeginTransactionAsync → AsyncLocal 环境连接+环境事务（MySqlConnector 强制命令绑 Transaction）→ commit/rollback；仓储命令统一经 NewCmd 自动绑定活动事务。
- `JeeflowEngine` 命令级信号量串行化（.NET 多线程下"单线程事件循环"等价物）——并发办理同任务读-改-写守卫天然原子。
- T1 冒烟（mysql-smoke 分组 + MySqlFixture 夹具：schema 幂等确保 + 9xxxxx define + BUSINESS_NO=T1CS-* 标记 + 测后自清理与清理验证）。

**用例数**：全套件 120 用例
- T0（SKIP_MYSQL=1，本机 memory）：**120/120 绿**（MySQL 侧 13 个 vacuous pass，开发机跳过口径）
- T1（连 160 真库）：**120/120 绿**，其中 MySQL 实跑 13：行为双跑 7（与 Memory 同断言套件，防仓储分叉）+ T1 专项 5（M1 分页五键真 SQL/m_ 过白名单、M2 hydrate 雪花>2^53 主键+9xxxxx 手动主键精确保真、事务回滚无半完成实例、并发办理恰一次成功、自清理可验证）+ spike 连通 1
- 修复过程抓出两个真 bug：MySqlConnector 事务内命令必须绑 Transaction（环境事务通道）；多线程下引擎读-改-写需命令级串行化

**gate**

- T1 过（本机→160，JEFFLOW_DB_* env；9xxxxx 段 + T1CS-* 标记自清理，清理验证断言过）✅
- 内存/MySQL 行为测试双跑无分叉（同套件断言 7/7 双绿）✅
- 事务回滚无半完成实例 ✅；并发办理只一次成功（1 成功 1 拒绝）✅
- SKIP_MYSQL=1 开发机全绿；无 SKIP 凭据缺失=fail（发版机 REQUIRE_MYSQL 口径）✅

**gate 结论**：T1 过，直入 M3。

**漂移检查**：flows/ 与 java 源逐字一致 ✅（本阶段未改上游仓）

## M3 Persist + Facade 全量（2026-09-06）

**产物**

- `Mldong.Jeeflow.Persist`（依赖仅 Core + BCL System.Data.Common，零第三方）：
  - `IDynamicTableWriter`/`DbDynamicTableWriter`（T9）：information_schema 列探测 schema 限定 DATABASE()（C18）、宽松列匹配驼峰↔下划线、主键非自增无生成器显式报错（C18）、参数化 INSERT、幂等 exists、系统字段补齐 apply_user_id 优先（C17）、表名安全（sys_ 拒绝）、值转换（LocalDateTime→串、容器→JSON）。
  - `MetaTableWriter`（NORMAL/JSON/EXPAND/ONE2ONE·ONE2MANY 子表递归；子表继承 apply_user_id putIfAbsent——C17/；无元数据回落基础 writer）。
  - `MetaTableReader`+`TableReader`（bizData 回显：storageType 反序列化组装 + 无元数据回落原始行）。
  - `PersistPostInterceptor`（ARCHIVE 缺省结束归档 FINISHED+AGREE INSERT / SYNC 发起 INSERT→任务节点 UPDATE→结束定稿；幂等 exists + 节点级 markChain 防同链双触发（C16）；字段权限双格式键只读/隐藏不写穿（C19）；状态字段 {节点ID}_{状态码} 列探测；relTableName 回落 name；writer 未注入静默跳过）。
- `Mldong.Jeeflow.Facade`：`FlowAsync/FlowJsonAsync` 45 action 全量（dispatch switch + unknown 兜底 99999999）；契约出口层经 Core Outbound（id 字符串化递归含复数、时间格式化、恒五键、stats int 出参）；入口 C3 双收/C15 ids 优先空报错/C26 时间双格式；stats 3 action 全纯列（C23/，todayNew 经 Clock）；bizData 经 ctx.BizDataReader（IBizDataReader，未注册显式报错）。

**用例数**：全套件 **153/153 全绿**（SKIP_MYSQL=1 时 MySQL 13 个 vacuous pass）
- Persist T0 9（fake writer 录制：ARCHIVE 时机/幂等/上下文字段、SYNC 权限过滤不写穿/结束定稿、同链防重、表名安全、FieldMeta、子表继承 apply_user）
- Facade 22（45 action dispatch 全覆盖无 unknown（读 action-manifest.json 断言 45）、未知 action、五键+出口 id string、detail 不存在/双收、deploy 递增/redeploy 保 version、startAndExecute 出口 string id、todo/doneList operator 过滤、execute submitType 路由（2→45/20 软拒绝 flag 注入）、非处理人负向、instanceDetail 契约（formData/ext/isFirstTaskNode）、highLight 决策 true 边（C14）、approvalRecord 数字 code（C5）、taskDetail form+ext、设计生命周期、listByType 分组、委托 save/update/detail/remove（C26）、bizData 未注册显式错、getLastByName、stats overview/trend/group 契约（缺参显式错/durationBucket 4 桶）、出口大数三态真流程审计、upAndDown 双键、withdraw）
- PersistSmoke（160 真库）2：T1M3 ARCHIVE 明文落库（days/reason/apply_user_id）+ T1M4 SYNC 字段权限不写穿（days 只读保持 3/999 被拒、reason 可编辑更新、tf_ 冗余落库、task1_10=10 状态列）
- 抓出 2 个真 bug 并修：MySqlConnector 事务内命令必须绑 Transaction（M2 已修）+ DbDynamicTableWriter.ToDbValue 误把 string 当 IEnumerable 序列化成 JSON

**gate**

- 45 action 契约测试（请求形态+信封+负向）✅；spec/09 persist 关键路径（ARCHIVE/SYNC/幂等/权限/状态字段）✅；出口审计 ✅；T1 复跑 M3/M4 落库 ✅
- MySQL 测试类收编 [Collection("mysql")] 共享夹具串行执行（消并行 id 冲突）

**gate 结论**：M3 过，直入 M4。

## M4 轻量 demo + jeeflow-ui 段位 + T2（2026-09-06）

**产物**

- demo（ASP.NET Core minimal API，:8093，零业务层）：`POST /wf/{**action}` 全转发 facade（FlowJsonAsync 契约出口）、`GET /health`、`GET /api/stats`、`POST /api/reset`（memory 重建+重载种子；mysql 回 ok）、CORS 全开 + OPTIONS 204。
- 双存储：`JEEFLOW_DEMO_STORE=memory`（默认，种子 15 流程 define(id=1..N)+design+design_his）/ `mysql`（JEFFLOW_DB_* 共享库）。
- 8 具名用户 SPI：IUserProvider / IOrgUserProvider（dept 领导 post2+/分管 post4+ boss 兜底/角色码匹配）/ IUserSearchProvider（关键词分页）。
- flows resolver：JEEFLOW_FLOWS_DIR → 候选探测；java 兄弟目录存在则精确镜像（全量复制+删孤儿）。
- 负向：body 解析失败 → 99999999 + stderr 日志（口径）；未知 action 同码。
- `demo/smoke_test.sh`：20 项端到端冒烟（入库）。
- jeeflow-ui（独立仓 f1d889b）：`/csharp-api` 代理（→ :8093）+ `.env` VITE_BACKEND_CSHARP + `?lang=csharp` 分段（LANG_MAP + backends 数组）。

**gate**

- curl 全路由 ✅（health/listByType/page/startAndExecute/stats/reset）
- 负向全中 ✅（非法 body→99999999+stderr、未知 action→99999999）
- smoke_test.sh 20/20 ✅：发起(07 比例会签)→userA/userB 办理→2/4 达成 merged→废弃残留→实例 FINISHED(20)；高亮 nodeProgress 会签成员 done ✓；抄送 createCCInstance/ccList ✓
- jeeflow-ui 代理链路 ✅：vite dev :5173 `/csharp-api/health`、`/csharp-api/wf/processDefine/page` 转发通（vite 热载新配置验证）
- 期间修复：路由 `/wf/{action}` → `/wf/{**action}`（两级 action 段）、种子 design 写入临时 ext 实例、CloneDefine 丢 CreateTime/CreateUser

**gate 结论**：T2 过，直入 M5。

## M5 发版准备 + 登记 + 一致性（2026-09-06）

**产物**

- 一致性快照：`demo/Mldong.Jeeflow.Consistency`（驱动：2 流程/6 实例含 99/5 任务固定数据集，钟 2026-08-10 在数据集外）
  → `jeeflow-hub/consistency/csharp.json` 15 key 落盘；**与 moon.json 逐字段全等**（其余语言差异均为已知格式化/并列序产物：
  java 0.0 vs 0 浮点格式化、node/php/python stuckNode 并列序文化差异——C# 侧排序统一 StringComparer.Ordinal 码点序对齐 MySQL）。
  驱动期修复 2 处：group_define avg 错锚（g.First()→各实例自身）、stuck 序文化敏感→Ordinal。
- 版本定版：`Directory.Build.props` Version=1.0.0（4 包 + demo 继承）；后续 1.0.x 递增。
- 联邦脚本增补（已验证）：`sync-schema.sh` 分发加 jeeflow-csharp；`release-checklist.sh` git 仓列表 + flows 漂移门禁加 csharp（实测 ✅）；`bump-version.sh --lang csharp`（改 Directory.Build.props + commit + tag，`--all` 纳入 csharp=O8，dry-run 验证过）；`stats-7lang-type-verify.py` 扩 8 语言（csharp 公网 demo 就绪后生效）。
- VERSIONS.md：jeeflow-integrations 新增「jeeflow-csharp（C#/.NET 第 8 语言 · 发版就绪待用户执行）」节。
- jeeflow-doc：`release.yml` checkout jeeflow-csharp、`sync-langs.js` 聚合 csharp 映射、`config.ts` 语言侧栏 + changelog 页 + 仓链接（🎯 C#/.NET · NuGet 1.0.x）。
- 交付文档：README + getting-started/engine-api/flow-definition/spi-guide/persist/demo/index/CHANGELOG/contract-notes（契约对照 21 条）/testing + PUBLISH.md（发版执行清单）。
- AGENTS 登记：jeeflow-hub §1/§2/§3.5/§4 + mldong-hub §3.5 + docs/jeeflow-demo-deploy-plan.md :8093 端口。
- issues 台账：无 -csharp- issue（全程无 R1 契约硬停；编号 106 起保留给后续）。
- CS3 复扫清零：BuildNodeProgress 异步化、MetaTableReader Assemble/QueryFirstAsync 异步化。

**gate（发版就绪门票）**

- T0 ✅ 153/153（memory，SKIP_MYSQL=1）　T1 ✅ 153/153（160 真库全量）　T2 ✅ smoke 20/20 + jeeflow-ui 代理链路
- 漂移门禁 ✅ flows/ 与 java 源逐字一致（7 语言仓逐一 diff 验证，csharp 在列）
- 一致性 ✅ 15 key；manifest ✅ 45 action 无差集
- 就绪 ≠ 发版：NuGet publish / tag / push / 远端建仓全部留待用户（R4）

**gate 结论**：M5 收口——**发版就绪**。

## 发布执行留痕（2026-09-06，R4 解除：用户已确认 push/publish）

- GitHub 远端建仓 `mldong/jeeflow-csharp`（用户提供 SSH），master 推送 3 个发布期 commit：
  f746a38（release.yml + PUBLISH §A-§D）→ 414f1a4（Push 可观测化）→ b2cf5d8（NUGET_API_KEY 大写修复）。
- NuGet Trusted Publishing policy 建好（用户操作）：owner=mldong / repo=mldong/jeeflow-csharp /
  workflow=release.yml / glob=Mldong.Jeeflow* / scope=Push new packages and package versions。
- tag `v1.0.0` 三推两修：run 34004326509 fail（Push exit 1，原因未知期）→ 34004675880 fail（实证
  401 No API Key——NuGet/login@v1 输出名为大写 NUGET_API_KEY，小写引用取空）→ **34004792139 全绿**，
  四包拓扑序 push 成功。
- nuget.org 实证：registration/search 双端点查四包 1.0.0 全部可查（新包 validation 有约 10 分钟索引滞后，
  flat-container/registration 滞后于 search 端点，排障时以 azuresearch query 为准）。
- 回拉验证通过：/tmp/nuget-verify 临时工程 `dotnet add package Mldong.Jeeflow.Facade/Repository.MySql -v 1.0.0`
  （nuget.org 真实解析 + 传递依赖 Core/Persist/MySqlConnector 齐全）四程序集 typeof 加载 + dotnet run 全通，测后已清理。
- 排障方法论留档：Actions 日志 API 需 admin 权限；公开仓匿名排障走 check-runs annotations
  （`::error::` 行会被抓取）+ Job Summary（仅网页渲染，API 不透出）。
- 剩余未办（等用户）：公网 demo 16087→8093 上线；jeeflow-doc Release workflow 部署文档站（csharp 侧栏已配）。

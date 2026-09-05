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

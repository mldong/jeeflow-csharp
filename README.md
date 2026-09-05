# jeeflow-csharp · jeeflow 工作流引擎 C#/.NET 实现（第 8 语言）

> NuGet 独立版本线 `v1.0.0` 起；引擎语义以 jeeflow-java 参考实现为准（R1 契约优先）。
> 移植模板 jeeflow-moon；契约 checklist C1–C29 + C# 增补 CS1–CS7 见
> `../docs/moonbit-engine-implementation-plan.md` §4 与 `../docs/jeeflow-csharp-implementation-plan.md`。

## 模块（4+1）

| 项目 | 依赖 | 说明 |
|---|---|---|
| `Mldong.Jeeflow.Core` | 零第三方 | 模型/SPI/引擎/parser/handler/event/metadata/内存仓储/雪花 id/出口 stringifier |
| `Mldong.Jeeflow.Repository.MySql` | Core、MySqlConnector | MySQL 仓储（全 async）、分页五键、`m_` 解析、T1 冒烟 |
| `Mldong.Jeeflow.Persist` | Core | DynamicTableWriter + PersistPostInterceptor + MetaTableReader |
| `Mldong.Jeeflow.Facade` | Core、Persist | `FlowAsync(action, args)` 45 action + 契约出口层 |
| `Mldong.Jeeflow.Demo` | Facade、Repository.MySql | 轻量 demo :8093（不发布） |

## 构建 / 测试（Git Bash）

```bash
export DOTNET_ROOT=/g/dev-tools/dotnet PATH=/g/dev-tools/dotnet:$PATH \
       DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build Jeeflow.sln
dotnet test tests/Mldong.Jeeflow.Tests          # T0（内存）
JEFFLOW_DB_PWD=... dotnet test tests/Mldong.Jeeflow.Tests --filter "Category=mysql-smoke"  # T1（160）
```

- MySQL 凭据只走 `JEFFLOW_DB_HOST/PORT/USER/PWD/NAME` env（`JEEFLOW_DB_*` 别名兼容），不入仓（R6）。
- 开发机 `SKIP_MYSQL=1` 跳过 T1；发版机连不上 = fail（REQUIRE_MYSQL 口径，M2 落地）。

## 契约速览

- 恒 `{code,msg,data}` 信封；成功 code=0；失败只发明 `99999999`；未知 action 同码。
- 出口：id 全字符串化（递归含复数数组）、时间 `yyyy-MM-dd HH:mm:ss`、分页恒五键、统计计数 int 出参（issues/105）。
- 入口：id string/number 双收、批量 `{ids}` 与单 `{id}` 双收（ids 优先、空显式报错）、`m_` 三段式过滤。
- 全链 async（无 sync-over-async）；`IClock` 注入固定钟做一致性快照。

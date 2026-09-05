# jeeflow-csharp demo（:8093）

轻量 demo（对齐 rust demo-salvo / moon demo 量级）：`POST /wf/{action}` 全量转发 facade + `/health` + `/api/stats` + `/api/reset` + CORS。demo 层零业务。

## 启动

```bash
export DOTNET_ROOT=/g/dev-tools/dotnet PATH=/g/dev-tools/dotnet:$PATH
dotnet run --project demo/Mldong.Jeeflow.Demo
# listening on 0.0.0.0:8093, store=memory
```

## 环境变量

| 变量 | 默认 | 说明 |
|---|---|---|
| `JEEFLOW_DEMO_STORE` | `memory` | `memory`（内置仓储+种子）/ `mysql`（JEFFLOW_DB_* 连共享库，不种子） |
| `JEFFLOW_DB_HOST/PORT/USER/PWD/NAME` | 160/root/jeeflow | mysql 存储凭据（凭据不入仓 R6） |
| `JEEFLOW_FLOWS_DIR` | 自动探测 | flows 目录覆盖 |

## 路由

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/wf/{action}` | 45 action 全转发（body JSON → args；出口走契约 stringifier） |
| GET | `/health` | `{status, engine, store}` |
| GET | `/api/stats?operator=` | `{todoCount, instanceCount}` |
| POST | `/api/reset` | memory：重建+重载种子；mysql：回 ok |

负向契约：body 解析失败 → `{code:99999999}` + stderr 日志；未知 action → 同码。

## 种子

memory 模式启动时从 `flows/`（15 共享流程）按文件名序载 define(id=1..N) + design + design_his。
resolver：java 兄弟目录（`../jeeflow-java/.../flows`）存在则精确镜像（全量复制+删孤儿）后再读。

## 8 具名用户

user1 张三 / userA 孙倩 / userB 周明 / userC 吴婷 / leader 李四 / manager 王五 / director 赵六 / boss 钱七。
IUserProvider / IOrgUserProvider（dept 领导/分管/角色）/ IUserSearchProvider（关键词分页）已装配。

## 冒烟

```bash
bash demo/smoke_test.sh   # 20 项：路由/种子/负向/主链路(发起→会签比例→完成)/抄送/视图/reset
```

## jeeflow-ui 接入

`jeeflow-ui` 已配 `/csharp-api` 代理（→ :8093）与 `?lang=csharp` 分段；启动 UI 后顶栏选 C# 或带 `?lang=csharp` 访问即直连本 demo。

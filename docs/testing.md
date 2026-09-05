# 测试指南

```bash
export DOTNET_ROOT=/g/dev-tools/dotnet PATH=/g/dev-tools/dotnet:$PATH

dotnet test tests/Mldong.Jeeflow.Tests                       # T0 全量（SKIP_MYSQL=1 自动跳过 MySQL 段）
SKIP_MYSQL=1 dotnet test tests/Mldong.Jeeflow.Tests          # 同上（显式）
JEFFLOW_DB_HOST=... JEFFLOW_DB_PWD=... dotnet test tests/Mldong.Jeeflow.Tests   # T1（160 真库）
bash demo/smoke_test.sh                                      # T2（demo 启动后）
```

| 层 | 规模 | 内容 |
|---|---|---|
| T0 | 153 用例 | 合规 c01–c31、submitType 8 值矩阵、事件时机、出口审计、parser/聚合/求值器/内存分页/persist 规则 |
| T1 | 真库 15 | 行为双跑（内存/MySQL 同断言）、分页五键、hydrate 主键、事务回滚无半成品、并发办理恰一次、ARCHIVE/SYNC 落库+字段权限 |
| T2 | 20 项 | demo 全路由、负向（非法 body/未知 action/缺参）、主链路（发起→会签比例→完成）、抄送/视图/reset |

发版机口径：`SKIP_MYSQL` 不设——连不上 MySQL = fail 不是 skip。
三场景纪律：commit 前正向/负向（变异红→还原）/回归；禁止 --no-verify。

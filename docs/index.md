# C#/.NET 引擎（第 8 语言 · NuGet 1.0.x）

jeeflow 工作流引擎 C# 实现：引擎核心零第三方依赖、全链 async、MySQL 仓储（MySqlConnector）、
40+ action 统一门面 + 契约出口层、业务数据动态入库（persist）。

- 四包：`Mldong.Jeeflow.Core` / `Repository.MySql` / `Persist` / `Facade`（net8.0;net10.0 双目标）
- 契约基线：40+ action manifest 与 java 实查无差集；T0 153 用例 + T1（160 真库）+ T2 全链路
- stats 一致性 15/15 逐字段一致（口径）
- 本机 demo :8093；jeeflow-ui `?lang=csharp` 段位已接

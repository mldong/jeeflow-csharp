# C# 引擎 Changelog

## v1.0.1（2026-09-06 · 包元数据完善）

- 四包内嵌 README（nuget.org 包页直接展示，消除 "missing a README" 警告）+ `PackageLicenseExpression`
  MIT → **Apache-2.0**（对齐联邦七兄弟仓 LICENSE；1.0.0 已发 MIT 属首发笔误）
- 仓库 README 全面扩写：架构图/包矩阵/快速开始（内存与 MySQL 双装配）/契约速览/测试矩阵/文档索引/发版通道
- 补 LICENSE 文件（Apache-2.0，与 java/php/rust/moon 同文本）
- 发版通道顺带修复留档：NuGet/login@v1 输出名为大写 `NUGET_API_KEY`（1.0.0 首发时实证）

## v1.0.0（2026-09-06 · 首发就绪）

- 引擎全量移植（第 8 语言）：start/execute/jump/jumpToEnd/rollbackToOperator 全 async 五方法；
  聚合根充血模型（实例 10/20/30/40/45/50/99 + 任务 10/20/30/40/50/99）
- 会签：串行逐个推进（任务变量 operatorList_{node}/loopCounter_{node}/nrOfInstances_{node}）、
  并行全量/表达式门控（#nrOfCompletedInstances 等）、ONE_VOTE_VETO 一票否决、软拒绝 flag、残留废弃
- 事件：INSTANCE_START/END（终态两路）/TASK_START（落库后 fire）/CC_CREATE（逐抄送人直传），逐监听器隔离
- 45 action 统一门面 + 契约出口层（id 全字符串化递归含复数、时间 yyyy-MM-dd HH:mm:ss、恒五键、stats int 出参）
- MySQL 仓储全量（分页五键/m_ 解析/NULL 安全/级联更新/参与者全量覆盖+去重追加/纯列 stats）
- persist：ARCHIVE/SYNC 双模式、字段权限双格式键、状态字段列探测、元数据子表递归
- 一致性：15 key 与联邦快照逐字段一致；45 action manifest 与 java 无差集

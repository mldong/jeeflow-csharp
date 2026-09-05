# 流程定义格式（LogicFlow JSON）

15 个共享流程定义在仓 `flows/` 目录（id=1..N 文件名序）。结构：

```json
{
  "name": "leave",
  "displayName": "请假流程",
  "type": "approval",
  "relTableName": "biz_leave",
  "persistMode": "ARCHIVE",
  "nodes": [
    { "id": "start", "type": "snaker:start", "text": { "value": "开始" } },
    { "id": "apply", "type": "snaker:task", "text": { "value": "发起申请" },
      "properties": { "assignee": "applicant", "form": "apply-form" } },
    { "id": "task1", "type": "snaker:task", "text": { "value": "审批" },
      "properties": { "assignee": "leader", "performType": 1,
                      "countersignType": "PARALLEL",
                      "countersignCompletionCondition": "#nrOfCompletedInstances==2",
                      "field": { "PERMISSION_days": 1 } } },
    { "id": "end", "type": "snaker:end", "text": { "value": "结束" } }
  ],
  "edges": [
    { "id": "e1", "sourceNodeId": "start", "targetNodeId": "apply" },
    { "id": "e2", "sourceNodeId": "apply", "targetNodeId": "task1", "properties": { "expr": "amount > 1000" } }
  ]
}
```

## 节点类型（snaker: 前缀）

| type | 模型 | 行为 |
|---|---|---|
| start | StartModel | fire 实例开始事件 → 驱动输出边 |
| task | TaskModel | 建任务；会签走 CountersignHandler（merged 才驱动） |
| decision | DecisionModel | expr 求值 / decisionHandler 命名边；边表达式只收 true |
| fork / join | ForkModel / JoinModel | 并行 / 全分支完成合并 |
| custom | CustomModel | clazz 按名解析 IHandler → FINISHED 历史任务 |
| end | EndModel | submitType=2 → 45，否则 20 |
| wfSubProcess / subProcess | SubProcessModel | 子流程占位 |

## 任务节点 properties

| 键 | 说明 |
|---|---|
| `assignee` | 逗号分隔；token 即变量 key（applicant→发起人子串替换）；tf_nextNodeOperator 优先 |
| `assignmentHandler` | 处理器注册名（Java FQCN 口径，见 SPI 指南） |
| `performType` | 0 普通 / 1 会签（接受 'ALL'/'COUNTERSIGN' 字符串） |
| `countersignType` | PARALLEL / SEQUENTIAL |
| `countersignCompletionCondition` | 表达式（#nrOfCompletedInstances 等）或 ONE_VOTE_VETO |
| `candidateUsers` / `candidateGroups` | 候选（顶层或 field 内） |
| `field` | 字段权限 PERMISSION_* 键 + 会签属性扩展 |

## 顶层属性

`relTableName`（业务表名，缺省回落 name）/ `persistMode`（ARCHIVE 缺省 / SYNC）/
`preInterceptors` / `postInterceptors`（逗号分隔拦截器注册名）。

## 共享 flows 纪律

唯一编辑源在 jeeflow-java 仓 `jeeflow-core/src/test/resources/flows/`；各语言仓 `flows/` 为入库副本——
改后在任一语言仓跑一次 demo/test 触发 resolver 精确镜像（全量复制+删孤儿），再逐仓 commit。

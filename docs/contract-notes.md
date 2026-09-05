# C# 契约对照表（与 Java 参考实现逐条）

| 契约 | C# 落点 |
|---|---|
| 信封 {code,msg,data}；失败只发明 99999999 | JeeflowFacade.Ok/Error + FlowJsonAsync |
| id 全字符串化（id/*Id/*_id/Ids/ids 逐元素，rows 不误伤） | Core Outbound.Transform |
| 时间 yyyy-MM-dd HH:mm:ss / autoGenTitle HH:mm | Outbound + FlowUtil.AddAutoGenTitle |
| 分页恒五键 | PageResultOut |
| 统计计数 int 出参（issues/105） | stats 3 action 全 int，avg int |
| id 入参 string/number 双收（C3） | ToLong |
| 批量 {ids} 与单 {id} 双收、空显式报错（C15） | IdListArgs |
| performType codeOf 容错 1/'1'/'ALL'（C4） | Enums.PerformTypeCodeOf |
| 出口枚举数字 code（C5） | TaskVo/ApprovalRecord |
| 串行会签逐个推进 + 任务变量无 csv_ 前缀（C9） | CountersignHandler + CreateCountersignTasks |
| 一票否决 + 残留废弃（C8） | CountersignHandler |
| f_ccActors/tf_ccActors 两路抄送（C11） | HandleCcActorsAsync |
| TASK_START 落库后 fire（C12/issues/13） | PersistTasksAsync.NotifyTaskStartAsync |
| 驳回路径 fire 结束事件 + 监听器隔离（C13） | EndProcessHandler + ProcessPublisher |
| 状态字段权限 PERMISSION_ 双格式（C19） | FlowUtil.FilterFieldByPerm + PersistPostInterceptor.IsEditable |
| withdraw 30/30 非 45（C28） | ProcessInstance.Withdraw |
| updateDefine 顶层 JSON 兼容（issues/27） | ContentBytes |
| designRedeploy 继承原 version（C29/issues/59） | DesignRedeployAsync |
| addTaskActor 去重追加（C15） | AddTaskActorAsync |
| 跨表列别名 + NULL 安全（C29/T10） | MySqlRepository 页查询/Get* 读取 |
| stats 全纯列 + 缺参显式错误（C23） | Stats* |
| 声明名不可解析显式报错（C20） | FindAssignmentHandler/NamedInterceptors/CustomModel |

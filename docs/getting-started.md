# C# 快速开始（NuGet 1.0.x）

jeeflow 工作流引擎的 C#/.NET 实现（第 8 语言）。引擎核心零第三方依赖；MySQL 走 MySqlConnector；全链 async。

## 安装

```bash
dotnet add package Mldong.Jeeflow.Core        # 引擎核心（零依赖）
dotnet add package Mldong.Jeeflow.Repository.MySql   # MySQL 仓储
dotnet add package Mldong.Jeeflow.Persist     # 业务数据动态入库（可选）
dotnet add package Mldong.Jeeflow.Facade      # 40+ action 统一门面
```

类库目标 `net8.0;net10.0` 双 TFM。

## 5 分钟上手

```csharp
using Mldong.Jeeflow.Core;

// 1. 实现仓储 SPI（示例用内置内存仓储；生产用 Mldong.Jeeflow.Repository.MySql）
var repo = new MemoryRepository();
var ctx  = new ServiceContext(repo);
ctx.UserProvider = new MyUserProvider();     // IUserProvider：getUser 单方法
ctx.JsonProvider = new DefaultJsonProvider(); // 缺省已内置，可不设

// 2. 组装引擎
var engine = new JeeflowEngine(ctx);

// 3. 部署流程（LogicFlow JSON）并发起
var content = File.ReadAllText("leave.json");
var define = new ProcessDefine { Name = "leave", DisplayName = "请假", Type = "approval",
                                 State = 1, Version = 1,
                                 Content = Encoding.UTF8.GetBytes(content) };
await repo.SaveDefineAsync(define);

var inst = await engine.StartProcessInstanceByIdAsync(define.Id, "user1",
    new FlowData { ["f_days"] = 3, ["f_reason"] = "年假" });
```

## MySQL 仓储

```csharp
var factory  = new MySqlConnectionFactory("192.168.1.160", 3306, "root", pwd, "jeeflow");
// 或 MySqlConnectionFactory.FromEnv()：读 JEFFLOW_DB_HOST/PORT/USER/PWD/NAME
var repo = new MySqlRepository(factory);
var ctx  = new ServiceContext(repo);
repo.Configure(ctx);
// 可选真事务：ctx.TransactionTemplate = new MySqlTransactionTemplate(factory, repo);
```

- 语句级 autocommit 为联邦现状；注入 `ITransactionTemplate` 后同事务内所有仓储方法共用同一连接。
- 建表 SQL 见包内 `schema/schema-mysql.sql`（5 张 `wf_*` 表，无自增，主键应用层生成）。

## 统一门面（40+ action）

```csharp
var facade = new JeeflowFacade(ctx);
var json = await facade.FlowJsonAsync("processInstance/startAndExecute", new FlowData
{
    ["processDefineId"] = "1",
    ["operator"] = "user1",
    ["f_days"] = 3,
});
// 恒 {code,msg,data} 信封；成功 code=0；失败只发明 99999999
```

集成方只需一个转发 controller：把 HTTP body JSON 转成 `FlowData` 传 `FlowAsync(action, args)`。

## 下一步

- [SPI 实现指南](./spi-guide)：IUserProvider / 表达式 / assignmentHandler
- [引擎 API](./engine-api)：五方法 + 聚合根行为
- [演示站（Demo）](./demo)：:8093 + jeeflow-ui `?lang=csharp`

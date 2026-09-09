# 业务数据入库（persist）

`Mldong.Jeeflow.Persist`：流程变量按元数据写入业务表（契约全覆盖）。
依赖仅 Core + BCL（System.Data.Common），适配任意 ADO.NET 驱动。

## 组装（引擎零改动，postInterceptors 声明名挂载）

```csharp
var factory = new MySqlConnectionFactory(host, port, user, pwd, db);
var writer = new MetaTableWriter(
    new DbDynamicTableWriter(new MyConnFactory(factory)),
    new JsonMetaProvider("/etc/jeeflow/persist-meta"));   // 元数据目录：表名.json
var interceptor = new PersistPostInterceptor().SetWriter(writer);

ctx.NamedInterceptors[PersistPostInterceptor.MetaClassName] = interceptor;
ctx.BizDataReader = new MetaTableReader(new TableReader(new MyConnFactory(factory)),
                                        new JsonMetaProvider("/etc/jeeflow/persist-meta"));
```

流程定义配置：顶层 `"persistMode": "ARCHIVE|SYNC"` + `"postInterceptors": "com.mldong.jeeflow.persist.interceptor.PersistPostInterceptor"` + `"relTableName": "biz_leave"`。

## 两种模式

| 模式 | 时机 |
|---|---|
| **ARCHIVE**（缺省） | 结束归档：FINISHED + submitType=AGREE 时 INSERT；幂等=按 process_instance_id 先 exists |
| **SYNC** | 发起 INSERT → 任务节点 UPDATE（f_ 按字段权限过滤 + tf_ 冗余 + 状态字段 {节点ID}_{状态码}）→ 结束 UPDATE 定稿（同意/驳回都入库） |

## 元数据（表名.json）

```json
{ "tableName": "biz_leave", "primaryKey": "id",
  "fields": [
    { "name": "days" },
    { "name": "contact", "storageType": "JSON" },
    { "name": "company", "storageType": "EXPAND",
      "expandFields": { "companyName": "company_name", "companyId": "company_id" } },
    { "name": "items", "storageType": "ONE2MANY", "targetTable": "biz_leave_item", "foreignKey": "leave_id" }
  ] }
```

storageType：1 NORMAL 直写 / 2 EXPAND 展开 / 3 JSON 序列化 / 4 ONE2ONE / 5 ONE2MANY（子表递归，
外键=主表主键，继承 apply_user_id——子表显式同名字段优先）。

## 关键规则

- **字段权限**：节点 `field.PERMISSION_days=1 只读/3 隐藏` 不参与 UPDATE（`PERMISSION_f_days` 优先、`PERMISSION_days` 兼容）；引擎入口同步过滤（f_ 不入变量）。
- **主键**：自增列自动回取（LAST_INSERT_ID）；非自增无值且未设 `PrimaryKeyGenerator` → 显式报错。
- **系统字段**：create_*/update_*/is_deleted 按配置补齐；用户列优先 data.apply_user_id（=流程发起人），否则 DefaultUserValue。
- **安全**：表名白名单（拒绝 sys_ 前缀/非法字符）；information_schema 查询 schema 限定（DATABASE()）；列匹配宽松（companyName ↔ company_name）。
- **读回**：`MetaTableReader.ReadByProcessInstanceAsync(table, instanceId)` 按 storageType 反组装（JSON 反序列化/EXPAND 反展开/子表数组）。

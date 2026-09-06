using System.Text.Json;
using Mldong.Jeeflow.Core;
using Mldong.Jeeflow.Facade;
using Mldong.Jeeflow.Demo;
using Mldong.Jeeflow.Repository.MySql;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8093");
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("*")));

var store = Environment.GetEnvironmentVariable("JEEFLOW_DEMO_STORE")
         ?? Environment.GetEnvironmentVariable("JEFFLOW_DEMO_STORE") ?? "memory";

// ── 存储装配（demo 层零业务；memory 默认种子，mysql 连共享库不种子）──
ServiceContext BuildContext()
{
    if (store == "mysql")
    {
        var factory = MySqlConnectionFactory.FromEnv();
        var repo = new MySqlRepository(factory);
        var ext = new MySqlExtRepository(factory, repo);
        var ctx = FlowsSeed.NewDemoContext(repo, ext);
        repo.Configure(ctx);
        return ctx;
    }
    var mem = new MemoryRepository();
    var memExt = new MemoryExtRepository(mem, null);
    var memCtx = FlowsSeed.NewDemoContext(mem, memExt);
    mem.Configure(memCtx);
    memExt.Configure(memCtx);
    FlowsSeed.Seed(mem, memExt, memCtx);
    return memCtx;
}

var context = BuildContext();
var facade = new JeeflowFacade(context);

// T003：业务数据种子（引擎真实启动 16 进行中 + 9 已完成 + 8 委托），memory 库才有意义
if (store == "memory")
{
    await BusinessSeed.SeedAsync(facade);
}

var app = builder.Build();
app.UseCors();

// GET /health：探活 + 引擎/存储标识
app.MapGet("/health", () => Results.Json(new
{
    status = "ok",
    engine = "jeeflow-csharp",
    store,
}));

// POST /api/reset：memory 重建状态 + 重载种子 + 复跑业务种子；mysql 共享库仅回 ok（对齐 moon 口径）
app.MapPost("/api/reset", async () =>
{
    if (store == "memory")
    {
        context = BuildContext();
        facade = new JeeflowFacade(context);
        await BusinessSeed.SeedAsync(facade);
    }
    return Results.Json(new { code = 0, msg = "成功" });
});

// GET /api/stats：轻量统计（todoCount + instanceCount，对齐七 demo）
app.MapGet("/api/stats", async (HttpRequest req) =>
{
    var operatorId = req.Query["operator"].ToString();
    if (string.IsNullOrEmpty(operatorId)) operatorId = "user1";
    var query = new PageQuery(1, 1);
    query.Add("pta.actor_id", "EQ", operatorId);
    var todo = await context.Repository.PageTodoTasksAsync(query);
    var instQuery = new PageQuery(1, 1).Add("t.operator", "EQ", operatorId);
    var instances = await context.Repository.PageInstancesAsync(instQuery);
    return Results.Json(new
    {
        todoCount = todo.RecordCount,
        instanceCount = instances.RecordCount,
    });
});

// OPTIONS 预检（CORS 中间件已处理 204，此路由兜底显式应答）
app.MapMethods("/wf/{**action}", new[] { "OPTIONS" },
    (string action) => Results.StatusCode(204));

// POST /wf/{action}：facade 全转发（契约出口 FlowJsonAsync；demo 层零业务）
app.MapPost("/wf/{**action}", async (string action, HttpRequest req) =>
{
    string body;
    using (var reader = new StreamReader(req.Body))
    {
        body = await reader.ReadToEndAsync();
    }
    FlowData args;
    try
    {
        // body 解析失败 → 99999999 + stderr 日志（Rust issue 88 教训：不静默吞）
        args = ParseArgs(body);
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"[jeeflow-csharp demo] body 解析失败: {e.Message} body={Truncate(body, 500)}");
        return Results.Json(new { code = 99999999, msg = "请求体解析失败: " + e.Message });
    }
    try
    {
        var json = await facade.FlowJsonAsync(action, args);
        return Results.Content(json, "application/json; charset=utf-8");
    }
    catch (Exception e)
    {
        // 门面外异常兜底：契约信封不裸奔
        Console.Error.WriteLine($"[jeeflow-csharp demo] action={action} 异常: {e}");
        return Results.Json(new { code = 99999999, msg = e.Message });
    }
});

app.Run();

static FlowData ParseArgs(string body)
{
    var args = new FlowData();
    if (string.IsNullOrWhiteSpace(body)) return args; // 空 body = 空 args（退化为缺省 operator）
    if (DefaultJsonProvider.Instance.FromJson(body) is Dictionary<string, object?> dict)
    {
        foreach (var kv in dict) args[kv.Key] = kv.Value;
        return args;
    }
    throw new InvalidOperationException("body 不是 JSON 对象");
}

static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

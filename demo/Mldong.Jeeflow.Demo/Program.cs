var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8093");
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

app.MapGet("/health", () => Results.Json(new
{
    status = "ok",
    engine = "jeeflow-csharp",
    store = Environment.GetEnvironmentVariable("JEFFLOW_DEMO_STORE") ?? "memory",
}));

// spike ③：POST /wf/{action} 骨架（M3 接 facade 全转发）
app.MapPost("/wf/{action}", (string action, HttpRequest req) =>
    Results.Json(new { code = 99999999, msg = $"未知 action: {action}" }));

app.Run();

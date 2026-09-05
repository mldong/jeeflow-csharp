using Mldong.Jeeflow.Core;

namespace Mldong.Jeeflow.Demo;

/// <summary>
/// flows 目录 resolver + 种子（对齐七语言 resolver 口径）：
/// 读本仓副本；java 兄弟目录存在则精确镜像（全量复制+删孤儿）进本仓再读——
/// 维护者执行即自动同步，单语言用户下载即用。id=1..N 文件名序，禁止只增不删。
/// </summary>
public static class FlowsSeed
{
    /// <summary>定位 flows 目录：JEEFLOW_FLOWS_DIR → 候选探测。</summary>
    public static string ResolveFlowsDir()
    {
        var env = Environment.GetEnvironmentVariable("JEEFLOW_FLOWS_DIR")
               ?? Environment.GetEnvironmentVariable("JEFFLOW_FLOWS_DIR");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
        foreach (var dir in new[] { "flows", "../flows", "../../flows", "../../../flows" })
        {
            if (File.Exists(Path.Combine(dir, "01-simple.json"))) return dir;
        }
        throw new InvalidOperationException("flows dir not found（可用 JEEFLOW_FLOWS_DIR 指定）");
    }

    /// <summary>resolver：java 兄弟目录存在 → 精确镜像（全量复制+删孤儿）；返回生效目录。</summary>
    public static string MirrorIfNeeded(string flowsDir)
    {
        // 兄弟仓相对本仓 demo 输出目录的探测：向上找 jeeflow-csharp 同级 jeeflow-java
        var candidates = new[]
        {
            Path.Combine(flowsDir, "..", "..", "..", "..", "jeeflow-java", "jeeflow-core", "src", "test", "resources", "flows"),
            Path.Combine("../../../jeeflow-java/jeeflow-core/src/test/resources/flows"),
            Path.Combine("../../../jeeflow-hub/jeeflow-java/jeeflow-core/src/test/resources/flows"),
        };
        foreach (var c in candidates)
        {
            var javaDir = Path.GetFullPath(c);
            if (!Directory.Exists(javaDir)) continue;
            if (!Directory.Exists(javaDir) || !Directory.EnumerateFiles(javaDir, "*.json").Any()) continue;
            var target = Path.GetFullPath(flowsDir);
            // 全量复制
            foreach (var srcFile in Directory.EnumerateFiles(javaDir, "*.json"))
            {
                var dst = Path.Combine(target, Path.GetFileName(srcFile));
                if (!File.Exists(dst) || File.ReadAllText(srcFile) != File.ReadAllText(dst))
                    File.Copy(srcFile, dst, overwrite: true);
            }
            // 删孤儿
            var javaNames = Directory.EnumerateFiles(javaDir, "*.json").Select(Path.GetFileName).ToHashSet();
            foreach (var dstFile in Directory.EnumerateFiles(target, "*.json"))
            {
                if (!javaNames.Contains(Path.GetFileName(dstFile))) File.Delete(dstFile);
            }
            return target;
        }
        return flowsDir;
    }

    /// <summary>种子：按文件名序载 define(id=1..N) + design + design_his（listByType 契约完整）。</summary>
    public static void Seed(MemoryRepository repo, MemoryExtRepository ext, ServiceContext ctx)
    {
        var flowsDir = MirrorIfNeeded(ResolveFlowsDir());
        var files = Directory.EnumerateFiles(flowsDir, "*.json")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        long id = 1;
        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            var name = Path.GetFileNameWithoutExtension(file);
            var define = new ProcessDefine
            {
                Id = id,
                Name = name,
                DisplayName = DisplayNameOf(content) ?? name,
                Type = "approval",
                State = 1,
                Content = System.Text.Encoding.UTF8.GetBytes(content),
                Version = 1,
                CreateTime = ctx.ClockOrDefault.Now,
                CreateUser = "system",
                UpdateTime = ctx.ClockOrDefault.Now,
                UpdateUser = "system",
            };
            repo.SaveDefineAsync(define).GetAwaiter().GetResult();
            // design + his（发起页 listByType 契约）
            ext.SaveDesignAsync(new ProcessDesign
            {
                Id = id,
                Name = define.Name,
                DisplayName = define.DisplayName,
                Type = "approval",
                IsDeployed = 1,
                CreateTime = ctx.ClockOrDefault.Now,
                CreateUser = "system",
            }).GetAwaiter().GetResult();
            ext.SaveDesignHisAsync(new ProcessDesignHis
            {
                Id = id,
                ProcessDesignId = id,
                Content = define.Content,
                CreateTime = ctx.ClockOrDefault.Now,
                CreateUser = "system",
            }).GetAwaiter().GetResult();
            id++;
        }
    }

    private static string? DisplayNameOf(string content)
    {
        try
        {
            if (DefaultJsonProvider.Instance.FromJson(content) is Dictionary<string, object?> root &&
                root.TryGetValue("displayName", out var dn))
                return dn?.ToString();
        }
        catch
        {
            // 容错回落文件名
        }
        return null;
    }

    /// <summary>组装 demo ServiceContext（8 用户 SPI + 内置 handler + worker 2 雪花）。</summary>
    public static ServiceContext NewDemoContext(IProcessRepository repo, IProcessExtRepository? ext)
    {
        var ctx = new ServiceContext(repo, ext);
        ctx.Clock = SystemClock.Instance;
        ctx.IdGenerator = new AtomicIdGenerator(2, ctx.Clock);
        ctx.UserProvider = new DemoUserProvider();
        ctx.OrgUserProvider = new DemoOrgUserProvider();
        ctx.UserSearchProvider = new DemoUserSearchProvider();
        TestInfraLike.RegisterBuiltins(ctx);
        return ctx;
    }

    /// <summary>内置 handler 注册（与 tests TestInfra 同清单；demo 独立持有避免引用测试程序集）。</summary>
    internal static class TestInfraLike
    {
        public static void RegisterBuiltins(ServiceContext ctx)
        {
            ctx.RegisterAssignmentHandler("com.mldong.jeeflow.interceptor.impl.OperatorAssignmentHandler",
                new BuiltinAssignmentHandlers.OperatorAssignmentHandler());
            ctx.RegisterAssignmentHandler("com.mldong.jeeflow.interceptor.impl.FormFieldAssigneeHandler",
                new BuiltinAssignmentHandlers.FormFieldAssigneeHandler());
            ctx.RegisterAssignmentHandler("com.mldong.jeeflow.interceptor.impl.DeptLeaderAssignmentHandler",
                new BuiltinAssignmentHandlers.DeptLeaderAssignmentHandler());
            ctx.RegisterAssignmentHandler("com.mldong.jeeflow.interceptor.impl.DeptMainLeaderAssignmentHandler",
                new BuiltinAssignmentHandlers.DeptMainLeaderAssignmentHandler());
            ctx.RegisterAssignmentHandler("com.mldong.jeeflow.interceptor.impl.ApplicantDeptLeaderAssignmentHandler",
                new BuiltinAssignmentHandlers.ApplicantDeptLeaderAssignmentHandler());
            ctx.RegisterAssignmentHandler("com.mldong.jeeflow.interceptor.impl.ApplicantDeptMainLeaderAssignmentHandler",
                new BuiltinAssignmentHandlers.ApplicantDeptMainLeaderAssignmentHandler());
            ctx.RegisterAssignmentHandler("com.mldong.jeeflow.interceptor.impl.TaskRoleAssigneeHandler",
                new BuiltinAssignmentHandlers.TaskRoleAssigneeHandler());
        }
    }
}

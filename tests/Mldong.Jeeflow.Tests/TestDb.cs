using Mldong.Jeeflow.Repository.MySql;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// 测试侧 DSN 工厂：env 优先，未设时兜底开发机测试库（与 go/python/node/php/rust 五栈同姿势，
/// 那五栈的兜底值也都在测试代码里）。兜底口令只出现在本测试工程，不进 src——
/// 发布的 NuGet 包维持 MySqlConnectionFactory「凭据只走 env」的口径。
/// </summary>
internal static class TestDb
{
    private static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    public static MySqlConnectionFactory Factory() => new(
        Env("JEFFLOW_DB_HOST", "192.168.1.160"),
        uint.Parse(Env("JEFFLOW_DB_PORT", "3306")),
        Env("JEFFLOW_DB_USER", "root"),
        Env("JEFFLOW_DB_PWD", "8Eli#gr#AUk"),
        Env("JEFFLOW_DB_NAME", "jeeflow"));
}

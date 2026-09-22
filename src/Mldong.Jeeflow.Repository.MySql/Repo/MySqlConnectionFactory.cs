using MySqlConnector;

namespace Mldong.Jeeflow.Repository.MySql;

/// <summary>
/// MySQL 连接工厂（CS5：连接串集中一处构建，参数注释钉死；凭据只走 JEFFLOW_DB_* env，R6）。
/// 连接即取即用（OpenAsync → dispose），语句级 autocommit——行为等同联邦现状。
/// </summary>
public sealed class MySqlConnectionFactory
{
    public string Host { get; }
    public uint Port { get; }
    public string User { get; }
    public string Password { get; }
    public string Database { get; }

    public MySqlConnectionFactory(string host, uint port, string user, string password, string database)
    {
        Host = host;
        Port = port;
        User = user;
        Password = password;
        Database = database;
    }

    /// <summary>从环境变量构建（JEFFLOW_DB_HOST/PORT/USER/PWD/NAME；JEEFLOW_DB_* 为别名）。</summary>
    public static MySqlConnectionFactory FromEnv()
    {
        return new MySqlConnectionFactory(
            Env("JEFFLOW_DB_HOST", "JEEFLOW_DB_HOST", "192.168.1.160"),
            ParsePort(Env("JEFFLOW_DB_PORT", "JEEFLOW_DB_PORT", "3306")),
            Env("JEFFLOW_DB_USER", "JEEFLOW_DB_USER", "root"),
            Env("JEFFLOW_DB_PWD", "JEEFLOW_DB_PWD", ""),
            Env("JEFFLOW_DB_NAME", "JEEFLOW_DB_NAME", "jeeflow"));
    }

    /// <summary>
    /// 连接串（集中一处，CS5）：
    /// - Pooling=true：.NET 侧连接池（每请求即取即用）
    /// - DefaultCommandTimeout=30：防悬挂
    /// - AllowUserVariables=false：只允许 ? 占位符（LIMIT/offset 内联非负整数，C21）
    /// - TreatTinyAsBoolean=False：<b>必需</b>。`wf_process_surrogate.enabled` 在共享 DDL 里是
    ///   `tinyint(1)`，MySqlConnector 默认把它当 bool ⇒ 脏值 2 读回来变 true→1，
    ///   「enabled 只认整数 1」的读侧判据被驱动静默改写（issues/123 的 ④ 判据在 csharp 栈恒红）。
    /// DATETIME 读侧一律显式 GetDateTime/DBNull 处理（issues/37 教训，不依赖 DSN 魔法）。
    /// </summary>
    public string ConnectionString =>
        $"Server={Host};Port={Port};User ID={User};Password={Password};Database={Database};" +
        "Pooling=true;DefaultCommandTimeout=30;AllowUserVariables=false;Character Set=utf8mb4;" +
        "TreatTinyAsBoolean=False";

    public async Task<MySqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new MySqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static string Env(params string[] names)
    {
        foreach (var n in names)
        {
            var v = Environment.GetEnvironmentVariable(n);
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return names[^1];
    }

    private static uint ParsePort(string s) => uint.TryParse(s, out var p) ? p : 3306;
}

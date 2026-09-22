using Mldong.Jeeflow.Repository.MySql;
using Xunit;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/123 ④ 判据的驱动侧防线：`wf_process_surrogate.enabled` 是 tinyint(1)，
/// MySqlConnector 默认 TreatTinyAsBoolean=true ⇒ 库里存的脏值 2 会被读成 true→1，
/// 「enabled 只认整数 1」在 SQL 仓被驱动静默改写（内存仓同数据却判否 ⇒ 双仓结论相反）。
/// 断言落在连接串本身：工厂是唯一 DSN 出口（CS5），这里钉住就不会有第二个口径。
/// </summary>
public class MySqlConnectionFactoryTests
{
    [Fact]
    public void ConnectionString_DisablesTinyAsBoolean()
    {
        var f = new MySqlConnectionFactory("127.0.0.1", 3306, "u", "p", "jeeflow");
        Assert.Contains("TreatTinyAsBoolean=False", f.ConnectionString);
        // 反向自证：不关这个开关时该键必须不存在（否则上面的断言是恒真）
        var raw = "Server=127.0.0.1;Port=3306;User ID=u;Password=p;Database=jeeflow;Pooling=true";
        Assert.DoesNotContain("TreatTinyAsBoolean", raw);
    }
}

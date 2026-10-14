using Xunit;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/215 · audit8 队列 R47（C99 §3.3 层位 1 / C1-L3 §1e）——**建表 SQL 分句器回归判据（T0，无 DB 依赖）**。
/// 钉住：编辑源副本经 <see cref="MySqlFixture.SplitSchemaStatements"/> 恰产 8 句、每句以
/// `CREATE TABLE IF NOT EXISTS` 开头、首句为 `wf_process_define`。历史坑（rust
/// `test_init_schema_statement_split` 同款、本栈 2026-10-15 修复）：按 `;` 分句后判段首 `--`
/// 会把文件头注释紧跟的首句 CREATE 误吞 ⇒ `wf_process_define` 建表静默缺失（空库 T1 首错
/// `Table '…wf_process_define' doesn't exist`；160 已建八表把缺陷掩盖成环境，非被测判据）。
/// </summary>
public class SchemaSplitFirstCreate215Tests
{
    [Fact]
    public void SchemaSplit_EightCreateStatements_FirstIsDefine()
    {
        var statements = MySqlFixture.SplitSchemaStatements(
            File.ReadAllText(MySqlFixture.FindSchemaPath()));
        Assert.Equal(8, statements.Length);
        Assert.All(statements, st =>
            Assert.StartsWith("CREATE TABLE IF NOT EXISTS", st, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("wf_process_define", statements[0]);
    }
}

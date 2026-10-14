using Xunit;

namespace Mldong.Jeeflow.Tests;

/// <summary>
/// issues/217 · audit8 队列 R49（C99 §3.3 层位 3 / C1-L3 §1d）——建表 SQL 表集合断言（T0，无 DB 依赖）。
/// "谁盯着表集合"此前四栈四答案（php 无／rust test_schema_* 群／moon 零枚／本栈仅静态形状）：
/// 本件补全名单腿，与 rust `test_schema_contains_8_tables`/`test_schema_table_names` 及
/// php/moon 同轮新增件同形——剥 `--` 行首注释 → count(CREATE TABLE) == 8 → 8 名单逐字在场
/// （canonical 前缀形式，防子串误判：`wf_process_task` 是 `wf_process_task_actor` 的前缀）。
/// 盯的对象＝随包分发副本 src/Mldong.Jeeflow.Repository.MySql/Schema/schema-mysql.sql
/// （issues/215 R47 修好的夹具经 SplitSchemaStatements 消费的同一份；R47 件只钉句数与首句，本件补全名单）。
/// </summary>
public class SchemaTableSet217Tests
{
    private static readonly string[] ExpectedTables = new[]
    {
        "wf_process_define",
        "wf_process_instance",
        "wf_process_task",
        "wf_process_task_actor",
        "wf_process_cc_instance",
        "wf_process_design",
        "wf_process_design_his",
        "wf_process_surrogate",
    };

    private static string StrippedSchema()
    {
        var ddl = File.ReadAllText(MySqlFixture.FindSchemaPath());
        return string.Join('\n', ddl.Split('\n').Where(l => !l.TrimStart().StartsWith("--")));
    }

    [Fact]
    public void SchemaTableSet_ExactlyEightCreateTableStatements()
    {
        var count = StrippedSchema().Split("CREATE TABLE").Length - 1;
        Assert.Equal(8, count);
    }

    [Fact]
    public void SchemaTableSet_AllEightTableNamesPresent()
    {
        var schema = StrippedSchema();
        foreach (var table in ExpectedTables)
        {
            Assert.Contains($"CREATE TABLE IF NOT EXISTS {table} (", schema, StringComparison.Ordinal);
        }
    }
}

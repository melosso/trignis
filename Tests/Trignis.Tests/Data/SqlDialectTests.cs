using System;
using System.Data.Common;
using Trignis.Data;
using Xunit;

namespace Trignis.Tests.Data;

public class SqlDialectTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mssql")]
    [InlineData("MSSQL")]
    [InlineData("SqlServer")]
    public void Parse_defaults_to_mssql(string? provider)
    {
        Assert.Same(SqlDialect.Mssql, SqlDialect.Parse(provider));
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("PostgreSQL")]
    [InlineData(" pgsql ")]
    public void Parse_resolves_postgres(string provider)
    {
        Assert.Same(SqlDialect.Postgres, SqlDialect.Parse(provider));
    }

    [Fact]
    public void Parse_rejects_unknown_provider_and_names_the_alternatives()
    {
        var ex = Assert.Throws<ArgumentException>(() => SqlDialect.Parse("oracle"));
        Assert.Contains("oracle", ex.Message);
        Assert.Contains("mssql", ex.Message);
    }

    [Fact]
    public void TryParse_reports_failure_without_throwing()
    {
        Assert.False(SqlDialect.TryParse("cassandra", out _));
    }

    /// <summary>
    /// Only SQL Server can be seeded from the server. Postgres has to ask the function,
    /// which is what drives <c>mode: "seed"</c> in the payload.
    /// </summary>
    [Fact]
    public void Only_mssql_reports_a_server_side_watermark()
    {
        Assert.NotNull(SqlDialect.Mssql.CurrentVersionSql);
        Assert.Null(SqlDialect.Postgres.CurrentVersionSql);
    }

    [Fact]
    public void CallProcedure_binds_the_shared_parameter_name()
    {
        foreach (var dialect in new[] { SqlDialect.Mssql, SqlDialect.Postgres })
        {
            var sql = string.Format(dialect.CallProcedure, "dbo.sp_GetChanges");
            Assert.Contains("dbo.sp_GetChanges", sql);
            Assert.Contains($"@{SqlDialect.JsonParameter}", sql);
        }
    }

    [Fact]
    public void Defaults_are_applied_when_the_user_left_them_out()
    {
        var connectionString = ApplyDefaults(SqlDialect.Mssql, "Server=localhost;Database=Db1");
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };

        Assert.Equal("Trignis", builder["Application Name"]);
        Assert.Equal("32768", builder["Packet Size"]);
    }

    [Fact]
    public void Defaults_never_override_what_the_user_set()
    {
        var connectionString = ApplyDefaults(
            SqlDialect.Mssql, "Server=localhost;Database=Db1;Application Name=Custom;Packet Size=4096");
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };

        Assert.Equal("Custom", builder["Application Name"]);
        Assert.Equal("4096", builder["Packet Size"]);
    }

    /// <summary>Mirrors the merge inside <see cref="SqlDialect.OpenAsync"/>, which needs a live server.</summary>
    private static string ApplyDefaults(SqlDialect dialect, string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        foreach (var (key, value) in dialect.ConnectionDefaults)
            if (!builder.ContainsKey(key))
                builder[key] = value;
        return builder.ConnectionString;
    }
}

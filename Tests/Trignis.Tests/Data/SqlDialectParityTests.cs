using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using Trignis.Data;
using Xunit;

namespace Trignis.Tests.Data;

/// <summary>
/// Holds every dialect to the same contract, so a provider added later has to earn its place
/// rather than quietly skipping half of what the pipeline assumes. These run against
/// <see cref="SqlDialect.All"/> on purpose: adding a dialect adds cases here for free.
/// </summary>
public class SqlDialectParityTests
{
    public static TheoryData<string> AllDialects()
    {
        var data = new TheoryData<string>();
        foreach (var dialect in SqlDialect.All)
            data.Add(dialect.Name);
        return data;
    }

    // xUnit needs serialisable theory data, so the cases carry the name and look the dialect back up.
    private static SqlDialect Get(string name) => SqlDialect.Parse(name);

    [Fact]
    public void More_than_one_dialect_is_registered()
    {
        // Otherwise every test below is trivially true and parity means nothing.
        Assert.True(SqlDialect.All.Count > 1, "parity is meaningless with a single dialect");
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void Every_dialect_is_fully_populated(string name)
    {
        var dialect = Get(name);

        Assert.False(string.IsNullOrWhiteSpace(dialect.Name));
        Assert.NotNull(dialect.Factory);
        Assert.False(string.IsNullOrWhiteSpace(dialect.CallProcedure));
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void Every_dialect_resolves_by_its_own_name(string name)
    {
        Assert.Same(Get(name), SqlDialect.Parse(Get(name).Name));
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void Every_dialect_formats_the_procedure_call_without_leftover_placeholders(string name)
    {
        var sql = string.Format(Get(name).CallProcedure, "schema.my_proc");

        Assert.Contains("schema.my_proc", sql);
        Assert.DoesNotContain("{0}", sql);
        // A stray {1} would have thrown above; this pins the reason the format takes one argument.
        Assert.DoesNotContain("{1}", sql);
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void Every_dialect_binds_the_one_shared_parameter(string name)
    {
        var sql = string.Format(Get(name).CallProcedure, "p");

        var occurrences = sql.Split($"@{SqlDialect.JsonParameter}").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void Every_dialect_can_build_a_command_carrying_that_parameter(string name)
    {
        var dialect = Get(name);

        using var connection = dialect.Factory.CreateConnection();
        Assert.NotNull(connection);

        using var command = connection!.CreateCommand();
        command.CommandText = string.Format(dialect.CallProcedure, "p");

        var parameter = command.CreateParameter();
        parameter.ParameterName = SqlDialect.JsonParameter;
        parameter.Value = """{"fromVersion":0,"mode":"sync"}""";
        command.Parameters.Add(parameter);

        Assert.Single(command.Parameters);
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void Every_dialect_accepts_its_own_connection_defaults(string name)
    {
        var dialect = Get(name);

        // The defaults are merged through a provider-neutral builder, but the provider's own
        // builder has to accept the result or opening the connection throws at runtime.
        var builder = dialect.Factory.CreateConnectionStringBuilder();
        Assert.NotNull(builder);

        foreach (var (key, value) in dialect.ConnectionDefaults)
            builder![key] = value;

        Assert.NotEmpty(builder!.ConnectionString);
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void Every_dialect_identifies_itself_to_the_server(string name)
    {
        // Operators grep for this when working out which connections are Trignis.
        var defaults = Get(name).ConnectionDefaults;

        Assert.Contains(defaults, d => d.Key.Contains("Application", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void Connection_defaults_never_override_the_user(string name)
    {
        var dialect = Get(name);
        if (dialect.ConnectionDefaults.Count == 0)
            return;

        var (key, defaultValue) = dialect.ConnectionDefaults.First();

        var builder = new DbConnectionStringBuilder { [key] = "user-chosen" };
        foreach (var (k, v) in dialect.ConnectionDefaults)
            if (!builder.ContainsKey(k))
                builder[k] = v;

        Assert.Equal("user-chosen", builder[key]);
        Assert.NotEqual(defaultValue, (string)builder[key]);
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void A_dialect_without_a_server_watermark_must_be_seedable_through_the_procedure(string name)
    {
        var dialect = Get(name);

        // Seeding sends mode: "seed" in the same JSON parameter, so there is nothing extra to
        // declare - but the call has to carry a parameter at all, or seeding is impossible.
        if (dialect.CurrentVersionSql is null)
            Assert.Contains($"@{SqlDialect.JsonParameter}", string.Format(dialect.CallProcedure, "p"));
        else
            Assert.False(string.IsNullOrWhiteSpace(dialect.CurrentVersionSql));
    }

    [Fact]
    public void Every_alias_resolves_to_a_registered_dialect()
    {
        foreach (var alias in SqlDialect.Aliases)
            Assert.Contains(SqlDialect.Parse(alias), SqlDialect.All);
    }

    [Fact]
    public void Aliases_are_matched_without_regard_to_case_or_surrounding_space()
    {
        foreach (var alias in SqlDialect.Aliases)
        {
            var expected = SqlDialect.Parse(alias);
            Assert.Same(expected, SqlDialect.Parse($"  {alias.ToUpperInvariant()}  "));
            Assert.Same(expected, SqlDialect.Parse(alias.ToLowerInvariant()));
        }
    }

    [Fact]
    public void Parse_and_TryParse_agree_on_every_alias()
    {
        foreach (var alias in SqlDialect.Aliases)
        {
            Assert.True(SqlDialect.TryParse(alias, out var viaTry));
            Assert.Same(SqlDialect.Parse(alias), viaTry);
        }
    }

    [Fact]
    public void Dialect_names_are_distinct()
    {
        var names = SqlDialect.All.Select(d => d.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Supported_lists_every_alias_so_the_error_message_stays_truthful()
    {
        foreach (var alias in SqlDialect.Aliases)
            Assert.Contains(alias, SqlDialect.Supported);
    }
}

using System;
using System.Linq;
using System.Text;
using Trignis.Helpers;
using Trignis.Models;
using Xunit;

namespace Trignis.Tests;

public class YamlEnvironmentTests
{
    private const string Yaml = """
        # production
        provider: postgres

        databases:
          main: "Host=db;Password=p\"w#1: x"

        polling:
          interval: 1m

        retry:
          count: 3
          delay: 5s

        tables:
          - name: items
            database: main
            table: public.items
            procedure: web.get_items
            initial_sync: full

        destinations:
          - name: files
            type: file
            path: exports/{object}.json
          - name: hook # webhook
            type: http
            url: https://example.com/hook
            compression: true
            auth: { type: basic, username: u, password: pw }
            headers:
              X-Api-Key: header-secret
          - name: orders
            type: rabbitmq
            host: mq
            port: 5673
            password: mqpw
            queue: orders
          - name: paused
            type: kafka
            enabled: false
            bootstrap_servers: localhost:9092
            topic: t
        """;

    private static EnvironmentConfig Load(string yaml) => YamlEnvironment.ToModel(yaml, "prod", text => text);

    [Fact]
    public void ToModel_MapsEveryKey()
    {
        Assert.Empty(YamlEnvironment.Validate(Yaml));
        var env = Load(Yaml);
        var ct = env.ChangeTracking;

        Assert.Equal("postgres", env.Provider);
        Assert.Equal("Host=db;Password=p\"w#1: x", env.ConnectionStrings["main"]);
        Assert.Equal((60, 3, 5), (ct.PollingIntervalSeconds, ct.RetryCount, ct.RetryDelaySeconds));
        Assert.Equal(("items", "main", "public.items", "web.get_items", "Full", "prod"),
            (ct.TrackingObjects[0].Name, ct.TrackingObjects[0].Database, ct.TrackingObjects[0].TableName, ct.TrackingObjects[0].StoredProcedureName, ct.TrackingObjects[0].InitialSyncMode, ct.TrackingObjects[0].EnvironmentFile));
        Assert.Equal((true, "exports/{object}.json", true), (ct.ExportToFile, ct.FilePath, ct.ExportToApi));

        Assert.Equal(["hook", "orders"], ct.ApiEndpoints.Select(e => e.Key)); // disabled destination left out
        var hook = ct.ApiEndpoints[0];
        Assert.Equal(("https://example.com/hook", true, "Basic", "pw", "header-secret"), (hook.Url, hook.EnableCompression, hook.Auth!.Type, hook.Auth.Password, hook.CustomHeaders!["X-Api-Key"]));
        var orders = ct.ApiEndpoints[1];
        Assert.Equal(("RabbitMQ", "mq", 5673, "orders", "/"), (orders.MessageQueueType, orders.MessageQueue!.HostName, orders.MessageQueue.Port, orders.MessageQueue.QueueName, orders.MessageQueue.VirtualHost));
    }

    [Fact]
    public void Write_RoundTripsThroughToModel()
    {
        var written = YamlEnvironment.Write(Load(Yaml));
        Assert.Empty(YamlEnvironment.Validate(written));
        Assert.Equal(written, YamlEnvironment.Write(Load(written)));
    }

    [Fact]
    public void EncryptSecrets_EncryptsSecrets_AndKeepsComments()
    {
        var encrypted = YamlEnvironment.EncryptSecrets(Yaml, text => $"PWENC:{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}", text => text.StartsWith("PWENC:"))!;
        var env = Load(encrypted);

        Assert.StartsWith("# production\n", encrypted);
        Assert.Contains("# webhook", encrypted);
        Assert.StartsWith("PWENC:", env.ConnectionStrings["main"]);
        Assert.StartsWith("PWENC:", env.ChangeTracking.ApiEndpoints[0].Auth!.Password);
        Assert.StartsWith("PWENC:", env.ChangeTracking.ApiEndpoints[0].CustomHeaders!["X-Api-Key"]);
        Assert.StartsWith("PWENC:", env.ChangeTracking.ApiEndpoints[1].MessageQueue!.Password);
        Assert.Equal("u", env.ChangeTracking.ApiEndpoints[0].Auth!.Username);
        Assert.Null(YamlEnvironment.EncryptSecrets(encrypted, text => text, text => text.StartsWith("PWENC:")));
    }

    [Theory]
    [InlineData("polling:\n  interval: [30\n", 3, "")]
    [InlineData("polling:\n  interval: 30s\n  interval: 60s\n", 3, "Duplicate key 'interval'")]
    [InlineData("polling:\n  interval: often\n", 2, "expected a duration")]
    [InlineData("retry:\n  count: three\n", 2, "expected a whole number")]
    [InlineData("tables:\n  name: a\n", 2, "expected a list of items")]
    [InlineData("provider: oracle\n", 1, "expected one of mssql")]
    [InlineData("tables:\n  - name: a\n    database: missing\n    table: t\n    procedure: p\n", 3, "Database 'missing' is not defined")]
    [InlineData("tables:\n  - name: a\n    table: t\n    procedure: p\n", 2, "'database' is required")]
    [InlineData("destinations:\n  - name: a\n    type: ftp\n", 3, "expected one of file, http")]
    [InlineData("destinations:\n  - name: a\n    type: http\n", 2, "'url' is required")]
    [InlineData("destinations:\n  - name: a\n    type: http\n    url: x\n  - name: a\n    type: http\n    url: y\n", 5, "Duplicate destination name 'a'")]
    [InlineData("destinations:\n  - name: a\n    type: file\n    path: x\n  - name: b\n    type: file\n    path: y\n", 5, "Only one enabled file destination")]
    [InlineData("base: &b { name: a }\ntables: [*b]\n", 2, "Aliases")]
    public void Validate_ReportsErrorsWithTheirLine(string yaml, int line, string message)
    {
        var error = Assert.Single(YamlEnvironment.Validate(yaml), i => i.IsError);
        Assert.Equal(line, error.Line);
        Assert.Contains(message, error.Message);
        Assert.Throws<FormatException>(() => Load(yaml));
    }

    [Fact]
    public void Validate_WarnsAboutUnknownKeys_WithASuggestion()
    {
        var warning = Assert.Single(YamlEnvironment.Validate("retry:\n  Count: 3\n"));
        Assert.False(warning.IsError);
        Assert.Equal((2, 3), (warning.Line, warning.Column));
        Assert.Contains("did you mean 'count'", warning.Message);
    }

    [Fact]
    public void Validate_WarnsWhenACommentFollowsAnUnquotedSecret()
    {
        var warning = Assert.Single(YamlEnvironment.Validate("databases:\n  main: Server=db;PWD=abc #123\n"));
        Assert.False(warning.IsError);
        Assert.Contains("double quotes", warning.Message);
        Assert.Empty(YamlEnvironment.Validate("databases:\n  main: \"Server=db;PWD=abc #123\" # quoted\nretry:\n  count: 3 # fine\n"));
    }

    [Fact]
    public void FromJson_ResolvesGlobalExportDefaults()
    {
        const string json = """
            { "ChangeTracking": { "ApiEndpoints": [ { "Url": "http://a/" }, { "Url": "http://b/" } ] } }
            """;
        var yaml = EnvironmentMigration.FromJson(json, new GlobalSettings { ExportToFile = true, ExportToApi = false });
        var env = Load(yaml);

        Assert.Contains("enabled: false", yaml);
        Assert.Equal((true, new GlobalSettings().FilePath, false), (env.ChangeTracking.ExportToFile, env.ChangeTracking.FilePath, env.ChangeTracking.ExportToApi));
        Assert.Contains("name: unnamed\n", yaml);
        Assert.Contains("name: unnamed-2\n", yaml);
    }
}

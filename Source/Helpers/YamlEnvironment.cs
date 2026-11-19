using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SharpYaml;
using SharpYaml.Events;
using Trignis.Models;

namespace Trignis.Helpers;

/// <summary>A problem in an environment file. Line and column are 1-based.</summary>
public sealed record YamlIssue(int Line, int Column, string Message, bool IsError)
{
    public override string ToString() => $"line {Line}, column {Column}: {Message}";
}

/// <summary>
/// Environment files in YAML: schema validation, mapping to <see cref="EnvironmentConfig"/>, secret encryption and writing.
/// </summary>
public static partial class YamlEnvironment
{
    private const string FileTarget = "file";

    private static readonly Dictionary<string, string> AuthTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bearer"] = "Bearer", ["basic"] = "Basic", ["apikey"] = "ApiKey", ["oauth2"] = "OAuth2ClientCredentials",
    };

    private static readonly Dictionary<string, string> QueueTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rabbitmq"] = "RabbitMQ", ["azureservicebus"] = "AzureServiceBus", ["awssqs"] = "AWSSQS",
        ["azureeventhubs"] = "AzureEventHubs", ["kafka"] = "Kafka",
    };

    private static readonly string[] Providers = ["mssql", "sqlserver", "postgres", "postgresql", "pgsql"];
    private static readonly string[] SyncModes = ["incremental", "full"];

    // ---- Schema ----

    private abstract record Shape;
    private sealed record TextShape : Shape;
    private sealed record WholeShape : Shape;
    private sealed record FlagShape : Shape;
    private sealed record DurationShape : Shape;
    private sealed record ChoiceShape(string[] Values) : Shape;
    private sealed record MapShape(Shape Value) : Shape;
    private sealed record ListShape(Shape Item) : Shape;
    private sealed record ObjectShape(Dictionary<string, Shape> Fields, string[] Required, string[]? Secret = null) : Shape;

    // Fields common to every destination, then fields per type.
    private sealed record DestinationShape(ObjectShape Common, Dictionary<string, ObjectShape> Types) : Shape;

    private static readonly TextShape Text = new();

    private static ObjectShape Obj(string[] required, string[]? secret, params (string Name, Shape Shape)[] fields) =>
        new(fields.ToDictionary(f => f.Name, f => f.Shape, StringComparer.Ordinal), required, secret);

    private static readonly ObjectShape Auth = Obj(["type"], ["token", "password", "api_key", "client_id", "client_secret"],
        ("type", new ChoiceShape([.. AuthTypes.Keys])), ("token", Text), ("username", Text), ("password", Text), ("api_key", Text),
        ("header_name", Text), ("token_endpoint", Text), ("client_id", Text), ("client_secret", Text), ("scope", Text),
        ("token_expiration", new DurationShape()));

    private static readonly DestinationShape Destination = new(
        Obj(["name", "type"], null, ("name", Text), ("type", new ChoiceShape([FileTarget, "http", .. QueueTypes.Keys])), ("enabled", new FlagShape())),
        new()
        {
            [FileTarget] = Obj(["path"], null, ("path", Text)),
            ["http"] = Obj(["url"], null, ("url", Text), ("auth", Auth), ("headers", new MapShape(Text)), ("compression", new FlagShape())),
            ["rabbitmq"] = Obj(["host"], ["password"], ("host", Text), ("port", new WholeShape()), ("virtual_host", Text), ("username", Text),
                ("password", Text), ("queue", Text), ("exchange", Text), ("routing_key", Text)),
            ["azureservicebus"] = Obj(["connection_string"], ["connection_string"], ("connection_string", Text), ("queue", Text), ("topic", Text)),
            ["awssqs"] = Obj(["queue_url"], ["access_key_id", "secret_access_key"], ("queue_url", Text), ("region", Text),
                ("access_key_id", Text), ("secret_access_key", Text)),
            ["azureeventhubs"] = Obj(["connection_string", "event_hub"], ["connection_string"], ("connection_string", Text), ("event_hub", Text)),
            ["kafka"] = Obj(["bootstrap_servers", "topic"], ["password"], ("bootstrap_servers", Text), ("topic", Text), ("username", Text),
                ("password", Text), ("security_protocol", Text), ("sasl_mechanism", Text)),
        });

    private static readonly ObjectShape Root = Obj([], null,
        ("provider", new ChoiceShape(Providers)),
        ("databases", new MapShape(Text)),
        ("polling", Obj([], null, ("interval", new DurationShape()))),
        ("retry", Obj([], null, ("count", new WholeShape()), ("delay", new DurationShape()))),
        ("tables", new ListShape(Obj(["name", "database", "table", "procedure"], null, ("name", Text), ("database", Text), ("table", Text),
            ("procedure", Text), ("initial_sync", new ChoiceShape(SyncModes))))),
        ("destinations", new ListShape(Destination)));

    // ---- Syntax tree with positions ----

    private abstract record Node(Mark Start);

    private sealed record ScalarNode(string Value, ScalarStyle Style, Mark Start, Mark End) : Node(Start)
    {
        public bool IsNull => Style is ScalarStyle.Plain or ScalarStyle.Any
            && (Value is "" or "~" || Value.Equals("null", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record MapNode(List<(ScalarNode Key, Node Value)> Items, Mark Start) : Node(Start)
    {
        public Node? this[string key] => Items.FirstOrDefault(kv => kv.Key.Value == key).Value;
        public string? Text(string key) => this[key] is ScalarNode { IsNull: false } s ? s.Value : null;
    }

    private sealed record SeqNode(List<Node> Items, Mark Start) : Node(Start);

    // ---- Public API ----

    /// <summary>Syntax errors, schema errors and references (errors); unknown keys and comment pitfalls (warnings).</summary>
    public static IReadOnlyList<YamlIssue> Validate(string yaml)
    {
        var issues = new List<YamlIssue>();
        var root = Read(yaml, issues);
        if (issues.Any(i => i.IsError)) return issues;

        if (root is MapNode map)
        {
            Check(map, Root, "", issues);
            CheckReferences(map, issues);
        }
        else if (root is not null and not ScalarNode { IsNull: true })
            issues.Add(Issue(root.Start, "The file must be a mapping of keys, such as tables:", true));

        // " #" ends a plain value: "PWD=abc #123" reads as "PWD=abc".
        foreach (var secret in SecretScalars(root).Where(s => s.Style == ScalarStyle.Plain))
        {
            if (CommentAfter().IsMatch(yaml.AsSpan(secret.End.Index)))
                issues.Add(Issue(secret.Start, "A comment follows this unquoted value. If '#' is part of the value, put the value in double quotes", false));
        }
        return issues;
    }

    /// <summary>Maps a valid file to the model; <paramref name="decrypt"/> receives every string. Throws <see cref="FormatException"/> with the first error.</summary>
    public static EnvironmentConfig ToModel(string yaml, string name, Func<string, string> decrypt)
    {
        if (Validate(yaml).FirstOrDefault(i => i.IsError) is { } error) throw new FormatException(error.ToString());
        var root = Read(yaml, []) as MapNode ?? new MapNode([], default);

        string? Get(MapNode? map, string key) => map?.Text(key) is { } text ? decrypt(text) : null;

        var databases = (root["databases"] as MapNode)?.Items
            .Where(kv => kv.Value is ScalarNode { IsNull: false })
            .ToDictionary(kv => kv.Key.Value, kv => decrypt(((ScalarNode)kv.Value).Value), StringComparer.Ordinal)
            ?? new Dictionary<string, string>();

        var tables = Items(root["tables"]).Select(t => new TrackingObject
        {
            Name = t.Text("name")!,
            Database = t.Text("database")!,
            TableName = t.Text("table")!,
            StoredProcedureName = t.Text("procedure")!,
            InitialSyncMode = t.Text("initial_sync")?.ToLowerInvariant() == "full" ? "Full" : "Incremental",
            EnvironmentFile = name,
        }).ToArray();

        var destinations = Items(root["destinations"]).Where(d => !bool.TryParse(d.Text("enabled"), out var on) || on).ToList();
        var file = destinations.FirstOrDefault(d => d.Text("type")!.Equals(FileTarget, StringComparison.OrdinalIgnoreCase));

        var endpoints = destinations.Where(d => d != file).Select(d =>
        {
            var type = d.Text("type")!.ToLowerInvariant();
            var auth = d["auth"] as MapNode;
            return new ApiEndpoint
            {
                Key = d.Text("name"),
                EnvironmentFile = name,
                Url = type == "http" ? Get(d, "url") : null,
                EnableCompression = bool.TryParse(d.Text("compression"), out var compress) && compress,
                CustomHeaders = (d["headers"] as MapNode)?.Items.Where(kv => kv.Value is ScalarNode { IsNull: false })
                    .ToDictionary(kv => kv.Key.Value, kv => decrypt(((ScalarNode)kv.Value).Value)),
                Auth = auth is null ? null : new ApiAuth
                {
                    Type = AuthTypes[auth.Text("type")!],
                    Token = Get(auth, "token"), Username = Get(auth, "username"), Password = Get(auth, "password"),
                    ApiKey = Get(auth, "api_key"), HeaderName = Get(auth, "header_name"), TokenEndpoint = Get(auth, "token_endpoint"),
                    ClientId = Get(auth, "client_id"), ClientSecret = Get(auth, "client_secret"), Scope = Get(auth, "scope"),
                    TokenExpirationSeconds = Seconds(auth.Text("token_expiration")),
                },
                MessageQueueType = type == "http" ? null : QueueTypes[type],
                MessageQueue = type == "http" ? null : new MessageQueueConfig
                {
                    HostName = Get(d, "host"),
                    Port = int.TryParse(d.Text("port"), out var port) ? port : 5672,
                    VirtualHost = Get(d, "virtual_host") ?? "/",
                    Username = Get(d, "username"), Password = Get(d, "password"),
                    QueueName = Get(d, "queue"), Exchange = Get(d, "exchange"), RoutingKey = Get(d, "routing_key"),
                    ConnectionString = Get(d, "connection_string"), TopicName = type == "azureservicebus" ? Get(d, "topic") : null,
                    QueueUrl = Get(d, "queue_url"), Region = Get(d, "region"),
                    AccessKeyId = Get(d, "access_key_id"), SecretAccessKey = Get(d, "secret_access_key"),
                    EventHubName = Get(d, "event_hub"),
                    BootstrapServers = Get(d, "bootstrap_servers"), Topic = type == "kafka" ? Get(d, "topic") : null,
                    SecurityProtocol = Get(d, "security_protocol"), SaslMechanism = Get(d, "sasl_mechanism"),
                },
            };
        }).ToArray();

        return new EnvironmentConfig
        {
            Name = name,
            Provider = root.Text("provider") ?? "mssql",
            ConnectionStrings = databases,
            ChangeTracking = new EnvironmentChangeTracking
            {
                TrackingObjects = tables,
                ApiEndpoints = endpoints,
                PollingIntervalSeconds = Seconds((root["polling"] as MapNode)?.Text("interval")),
                RetryCount = int.TryParse((root["retry"] as MapNode)?.Text("count"), out var retries) ? retries : null,
                RetryDelaySeconds = Seconds((root["retry"] as MapNode)?.Text("delay")),
                ExportToFile = file is not null,
                FilePath = file is null ? null : Get(file, "path"),
                ExportToApi = endpoints.Length > 0,
            },
        };
    }

    /// <summary>Strings at secret paths: databases, credentials and HTTP headers.</summary>
    public static IReadOnlySet<string> Secrets(string yaml) =>
        SecretScalars(Read(yaml, [])).Select(s => s.Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>Encrypts plaintext secrets in place, keeping comments and layout. Returns null if nothing changed or the file is invalid.</summary>
    public static string? EncryptSecrets(string yaml, Func<string, string> encrypt, Func<string, bool> isEncrypted)
    {
        var issues = new List<YamlIssue>();
        var root = Read(yaml, issues);
        if (issues.Any(i => i.IsError)) return null;

        var plain = SecretScalars(root).Where(s => !isEncrypted(s.Value)).OrderByDescending(s => s.Start.Index).ToList();
        if (plain.Count == 0) return null;

        var text = new StringBuilder(yaml);
        foreach (var scalar in plain)
            text.Remove(scalar.Start.Index, scalar.End.Index - scalar.Start.Index).Insert(scalar.Start.Index, Quote(encrypt(scalar.Value)));
        return text.ToString();
    }

    /// <summary>Writes <paramref name="model"/> in the YAML layout. A disabled export gets <c>enabled: false</c> on its destinations.</summary>
    public static string Write(EnvironmentConfig model, bool fileEnabled = true, bool endpointsEnabled = true)
    {
        var text = new StringBuilder();
        var ct = model.ChangeTracking;

        void Line(int indent, string key, string? value)
        {
            if (value is not null) text.Append(' ', indent).Append(Key(key)).Append(": ").Append(value).Append('\n');
        }

        void Section(string key, IEnumerable<(string Key, string? Value)> entries)
        {
            var present = entries.Where(e => e.Value is not null).ToList();
            if (present.Count == 0) return;
            text.Append('\n').Append(key).Append(":\n");
            foreach (var (k, v) in present) Line(2, k, v);
        }

        text.Append("provider: ").Append(ScalarText(model.Provider)).Append('\n');
        Section("databases", model.ConnectionStrings.Select(kv => (kv.Key, (string?)Quote(kv.Value))));
        Section("polling", [("interval", ct.PollingIntervalSeconds is { } p ? $"{p}s" : null)]);
        Section("retry", [("count", ct.RetryCount?.ToString(CultureInfo.InvariantCulture)), ("delay", ct.RetryDelaySeconds is { } d ? $"{d}s" : null)]);

        if (ct.TrackingObjects.Length > 0)
        {
            text.Append("\ntables:\n");
            foreach (var t in ct.TrackingObjects)
            {
                text.Append("  - name: ").Append(ScalarText(t.Name)).Append('\n');
                Line(4, "database", ScalarText(t.Database));
                Line(4, "table", ScalarText(t.TableName));
                Line(4, "procedure", ScalarText(t.StoredProcedureName));
                Line(4, "initial_sync", ScalarText(t.InitialSyncMode.ToLowerInvariant()));
            }
        }

        var destinations = new List<Action>();
        if (ct.FilePath is not null || ct.ExportToFile == true)
            destinations.Add(() =>
            {
                text.Append("  - name: files\n");
                Line(4, "type", FileTarget);
                Line(4, "path", ScalarText(ct.FilePath ?? new GlobalSettings().FilePath));
                if (!fileEnabled) Line(4, "enabled", "false");
            });

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "files" };
        foreach (var e in ct.ApiEndpoints)
        {
            var name = e.Key ?? "unnamed";
            for (var n = 2; !names.Add(name); n++) name = $"{e.Key ?? "unnamed"}-{n}";
            destinations.Add(() => WriteDestination(text, e, name, endpointsEnabled, Line));
        }

        if (destinations.Count > 0)
        {
            text.Append("\ndestinations:\n");
            foreach (var write in destinations) write();
        }
        return text.ToString();
    }

    private static void WriteDestination(StringBuilder text, ApiEndpoint e, string name, bool enabled, Action<int, string, string?> line)
    {
        var type = e.MessageQueueType is { } queue
            ? QueueTypes.FirstOrDefault(kv => kv.Value.Equals(queue, StringComparison.OrdinalIgnoreCase)).Key ?? queue.ToLowerInvariant()
            : "http";
        text.Append("  - name: ").Append(ScalarText(name)).Append('\n');
        line(4, "type", ScalarText(type));
        if (!enabled) line(4, "enabled", "false");

        string? Q(string? value) => value is null ? null : Quote(value);
        string? T(string? value) => value is null ? null : ScalarText(value);
        var mq = e.MessageQueue ?? new MessageQueueConfig();
        switch (type)
        {
            case "http":
                line(4, "url", Q(e.Url));
                if (e.EnableCompression) line(4, "compression", "true");
                if (e.Auth is { } a)
                {
                    text.Append("    auth:\n");
                    line(6, "type", T(AuthTypes.FirstOrDefault(kv => kv.Value.Equals(a.Type, StringComparison.OrdinalIgnoreCase)).Key ?? a.Type?.ToLowerInvariant()));
                    line(6, "token", Q(a.Token)); line(6, "username", T(a.Username)); line(6, "password", Q(a.Password));
                    line(6, "api_key", Q(a.ApiKey)); line(6, "header_name", T(a.HeaderName)); line(6, "token_endpoint", Q(a.TokenEndpoint));
                    line(6, "client_id", Q(a.ClientId)); line(6, "client_secret", Q(a.ClientSecret)); line(6, "scope", Q(a.Scope));
                    line(6, "token_expiration", a.TokenExpirationSeconds is { } s ? $"{s}s" : null);
                }
                if (e.CustomHeaders is { Count: > 0 } headers)
                {
                    text.Append("    headers:\n");
                    foreach (var (k, v) in headers) line(6, k, Quote(v));
                }
                break;
            case "rabbitmq":
                line(4, "host", T(mq.HostName));
                if (mq.Port != 5672) line(4, "port", mq.Port.ToString(CultureInfo.InvariantCulture));
                if (mq.VirtualHost is not (null or "/")) line(4, "virtual_host", Q(mq.VirtualHost));
                line(4, "username", T(mq.Username)); line(4, "password", Q(mq.Password));
                line(4, "queue", T(mq.QueueName)); line(4, "exchange", T(mq.Exchange)); line(4, "routing_key", Q(mq.RoutingKey));
                break;
            case "azureservicebus":
                line(4, "connection_string", Q(mq.ConnectionString)); line(4, "queue", T(mq.QueueName)); line(4, "topic", T(mq.TopicName));
                break;
            case "awssqs":
                line(4, "queue_url", Q(mq.QueueUrl)); line(4, "region", T(mq.Region));
                line(4, "access_key_id", Q(mq.AccessKeyId)); line(4, "secret_access_key", Q(mq.SecretAccessKey));
                break;
            case "azureeventhubs":
                line(4, "connection_string", Q(mq.ConnectionString)); line(4, "event_hub", T(mq.EventHubName));
                break;
            case "kafka":
                line(4, "bootstrap_servers", Q(mq.BootstrapServers)); line(4, "topic", T(mq.Topic));
                line(4, "username", T(mq.Username)); line(4, "password", Q(mq.Password));
                line(4, "security_protocol", T(mq.SecurityProtocol)); line(4, "sasl_mechanism", T(mq.SaslMechanism));
                break;
        }
    }

    // ---- Validation ----

    private static void Check(Node node, Shape shape, string path, List<YamlIssue> issues)
    {
        if (node is ScalarNode { IsNull: true }) return;
        switch (shape)
        {
            case TextShape when node is not ScalarNode:
                issues.Add(Issue(node.Start, $"{path}: expected a single value", true));
                break;
            case WholeShape when node is not ScalarNode s || !int.TryParse(s.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _):
                issues.Add(Issue(node.Start, $"{path}: expected a whole number", true));
                break;
            case FlagShape when node is not ScalarNode s || !bool.TryParse(s.Value, out _):
                issues.Add(Issue(node.Start, $"{path}: expected true or false", true));
                break;
            case DurationShape when node is not ScalarNode s || Seconds(s.Value) is null:
                issues.Add(Issue(node.Start, $"{path}: expected a duration such as 30s, 5m or 1h", true));
                break;
            case ChoiceShape choice when node is not ScalarNode s || !choice.Values.Contains(s.Value, StringComparer.OrdinalIgnoreCase):
                issues.Add(Issue(node.Start, $"{path}: expected one of {string.Join(", ", choice.Values)}", true));
                break;
            case MapShape map:
                if (node is not MapNode m) { issues.Add(Issue(node.Start, $"{path}: expected key: value pairs", true)); break; }
                foreach (var (key, value) in m.Items) Check(value, map.Value, $"{path}.{key.Value}", issues);
                break;
            case ListShape list:
                if (node is not SeqNode seq) { issues.Add(Issue(node.Start, $"{path}: expected a list of items, each starting with '- '", true)); break; }
                for (var i = 0; i < seq.Items.Count; i++) Check(seq.Items[i], list.Item, $"{path}[{i}]", issues);
                break;
            case ObjectShape obj:
                CheckObject(node, [obj], path, issues);
                break;
            case DestinationShape destination:
                var type = (node as MapNode)?.Text("type");
                CheckObject(node, type is not null && destination.Types.TryGetValue(type.ToLowerInvariant(), out var typed)
                    ? [destination.Common, typed] : [destination.Common], path, issues);
                break;
        }
    }

    private static void CheckObject(Node node, ObjectShape[] shapes, string path, List<YamlIssue> issues)
    {
        if (node is not MapNode map) { issues.Add(Issue(node.Start, $"{path}: expected key: value pairs", true)); return; }
        foreach (var (key, value) in map.Items)
        {
            if (shapes.Select(s => s.Fields.GetValueOrDefault(key.Value)).FirstOrDefault(f => f is not null) is { } field)
                Check(value, field, path.Length == 0 ? key.Value : $"{path}.{key.Value}", issues);
            else
                issues.Add(Issue(key.Start, $"Unknown key '{key.Value}' is ignored{Suggestion(key.Value, shapes)}", false));
        }
        foreach (var required in shapes.SelectMany(s => s.Required))
            if (map[required] is null or ScalarNode { IsNull: true })
                issues.Add(Issue(map.Start, $"{(path.Length == 0 ? "" : path + ": ")}'{required}' is required", true));
    }

    private static string Suggestion(string key, ObjectShape[] shapes)
    {
        var match = shapes.SelectMany(s => s.Fields.Keys).FirstOrDefault(f =>
            f.Replace("_", "").Equals(key.Replace("_", ""), StringComparison.OrdinalIgnoreCase));
        return match is null ? "" : $"; did you mean '{match}'?";
    }

    private static void CheckReferences(MapNode root, List<YamlIssue> issues)
    {
        var databases = (root["databases"] as MapNode)?.Items.Select(kv => kv.Key.Value).ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var table in Items(root["tables"]))
            if (table["database"] is ScalarNode { IsNull: false } db && !databases.Contains(db.Value))
                issues.Add(Issue(db.Start, $"Database '{db.Value}' is not defined under databases:", true));

        Unique(Items(root["tables"]), "table name", issues);
        var destinations = Items(root["destinations"]).ToList();
        Unique(destinations, "destination name", issues);

        var files = destinations.Where(d => d.Text("type")?.Equals(FileTarget, StringComparison.OrdinalIgnoreCase) == true
            && (!bool.TryParse(d.Text("enabled"), out var on) || on)).Skip(1);
        foreach (var extra in files)
            issues.Add(Issue(extra.Start, "Only one enabled file destination is supported per environment", true));
    }

    private static void Unique(IEnumerable<MapNode> items, string what, List<YamlIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
            if (item["name"] is ScalarNode { IsNull: false } name && !seen.Add(name.Value))
                issues.Add(Issue(name.Start, $"Duplicate {what} '{name.Value}'", true));
    }

    private static IEnumerable<MapNode> Items(Node? node) => node is SeqNode seq ? seq.Items.OfType<MapNode>() : [];

    /// <summary>Seconds for <c>30</c>, <c>30s</c>, <c>5m</c> or <c>1h</c>; null otherwise.</summary>
    private static int? Seconds(string? value)
    {
        if (value is null) return null;
        var match = DurationText().Match(value);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return null;
        return match.Groups[2].Value switch { "m" => n * 60, "h" => n * 3600, _ => n };
    }

    private static IEnumerable<ScalarNode> SecretScalars(Node? root)
    {
        static IEnumerable<ScalarNode> Strings(Node? node, string[]? names) =>
            node is MapNode map
                ? map.Items.Where(kv => names is null || names.Contains(kv.Key.Value, StringComparer.Ordinal))
                    .Select(kv => kv.Value).OfType<ScalarNode>().Where(s => !s.IsNull)
                : [];

        if (root is not MapNode map) yield break;
        foreach (var s in Strings(map["databases"], null)) yield return s;
        foreach (var d in Items(map["destinations"]))
        {
            if (d.Text("type") is { } type && Destination.Types.TryGetValue(type.ToLowerInvariant(), out var shape) && shape.Secret is { } secret)
                foreach (var s in Strings(d, secret)) yield return s;
            foreach (var s in Strings(d["auth"], Auth.Secret)) yield return s;
            foreach (var s in Strings(d["headers"], null)) yield return s;
        }
    }

    // ---- Parsing ----

    private static Node? Read(string yaml, List<YamlIssue> issues)
    {
        var parser = Parser.CreateParser(new StringReader(yaml));
        try
        {
            Node? root = null;
            var documents = 0;
            while (parser.MoveNext())
            {
                switch (parser.Current)
                {
                    case DocumentStart start when ++documents > 1:
                        issues.Add(Issue(start.Start, "The file must contain one YAML document", true));
                        return root;
                    case MappingStart or SequenceStart or Scalar or AnchorAlias:
                        root = ReadNode(parser, issues);
                        break;
                }
            }
            return root;
        }
        catch (YamlException ex)
        {
            issues.Add(new YamlIssue(ex.Start.Line + 1, ex.Start.Column + 1, PositionPrefix().Replace(ex.Message, ""), true));
            return null;
        }
    }

    private static Node? ReadNode(IParser parser, List<YamlIssue> issues)
    {
        switch (parser.Current)
        {
            case Scalar scalar:
                return new ScalarNode(scalar.Value, scalar.Style, scalar.Start, scalar.End);
            case AnchorAlias alias:
                issues.Add(Issue(alias.Start, "Aliases (*name) are not supported in environment files", true));
                return null;
            case MappingStart start:
            {
                var map = new MapNode([], start.Start);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                while (parser.MoveNext() && parser.Current is not MappingEnd)
                {
                    var keyNode = ReadNode(parser, issues);
                    parser.MoveNext();
                    var value = ReadNode(parser, issues);
                    if (keyNode is not ScalarNode key)
                    {
                        if (keyNode is not null) issues.Add(Issue(keyNode.Start, "Keys must be plain text", true));
                        continue;
                    }
                    if (!seen.Add(key.Value)) issues.Add(Issue(key.Start, $"Duplicate key '{key.Value}'", true));
                    if (value is not null) map.Items.Add((key, value));
                }
                return map;
            }
            case SequenceStart start:
            {
                var seq = new SeqNode([], start.Start);
                while (parser.MoveNext() && parser.Current is not SequenceEnd)
                    if (ReadNode(parser, issues) is { } item) seq.Items.Add(item);
                return seq;
            }
            default:
                return null;
        }
    }

    private static YamlIssue Issue(Mark at, string message, bool isError) => new(at.Line + 1, at.Column + 1, message, isError);

    // ---- Writing ----

    private static string Key(string key) => PlainKey().IsMatch(key) ? key : Quote(key);

    // Plain only when YAML cannot read it as a number, boolean or null.
    private static string ScalarText(string value) =>
        PlainText().IsMatch(value) && !Reserved().IsMatch(value) ? value : Quote(value);

    private static string Quote(string value)
    {
        var text = new StringBuilder("\"");
        foreach (var c in value)
        {
            text.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => $"\\u{(int)c:X4}",
                _ => c.ToString(),
            });
        }
        return text.Append('"').ToString();
    }

    [GeneratedRegex(@"^\(.*?\)\s*-\s*\(.*?\):\s*")]
    private static partial Regex PositionPrefix();

    [GeneratedRegex(@"^(\d+)(s|m|h)?$")]
    private static partial Regex DurationText();

    [GeneratedRegex(@"^[ \t]+#")]
    private static partial Regex CommentAfter();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_\-]*$")]
    private static partial Regex PlainKey();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.\-/]*$")]
    private static partial Regex PlainText();

    [GeneratedRegex(@"^(true|false|yes|no|on|off|y|n|null)$", RegexOptions.IgnoreCase)]
    private static partial Regex Reserved();
}

using Trignis.MicrosoftSQL.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.IO;
using Serilog;
using Serilog.Sinks.EventLog;
using Trignis.MicrosoftSQL.Helpers;
using System;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using System.Linq;
using Trignis.MicrosoftSQL.Models;
using System.Collections.Generic;
using System.Text.Json;
using System.Text;
using System.Reflection;
using Microsoft.AspNetCore.Http;

Environment.CurrentDirectory = AppContext.BaseDirectory;

// Load .env file if it exists
var envPath = Path.Combine(AppContext.BaseDirectory, ".env");
if (File.Exists(envPath))
{
    foreach (var line in File.ReadAllLines(envPath))
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("#") || string.IsNullOrWhiteSpace(trimmed))
            continue;

        var parts = trimmed.Split('=', 2);
        if (parts.Length == 2)
        {
            var key = parts[0].Trim();
            var value = parts[1].Trim().Trim('"', '\'');
            Environment.SetEnvironmentVariable(key, value, EnvironmentVariableTarget.Process);
        }
    }
}

var tempConfig = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true)
    .Build();

bool useEventLog = tempConfig.GetValue<bool>("Windows:UseEventLog", false);

var logDirectory = Path.Combine(AppContext.BaseDirectory, "log");
if (!Directory.Exists(logDirectory))
{
    Directory.CreateDirectory(logDirectory);
}

// Configure initial logger (for `appsettings.json`)
var loggerConfig = new LoggerConfiguration()
    .ReadFrom.Configuration(tempConfig);

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && useEventLog)
{
    loggerConfig.WriteTo.EventLog(source: "Trignis", logName: "Application");
}

Log.Logger = loggerConfig.CreateLogger();

Console.WriteLine("");
Console.WriteLine("████████╗██████╗ ██╗ ██████╗ ███╗   ██╗██╗███████╗");
Console.WriteLine("╚══██╔══╝██╔══██╗██║██╔════╝ ████╗  ██║██║██╔════╝");
Console.WriteLine("   ██║   ██████╔╝██║██║  ███╗██╔██╗ ██║██║███████╗");
Console.WriteLine("   ██║   ██╔══██╗██║██║   ██║██║╚██╗██║██║╚════██║");
Console.WriteLine("   ██║   ██║  ██║██║╚██████╔╝██║ ╚████║██║███████║");
Console.WriteLine("   ╚═╝   ╚═╝  ╚═╝╚═╝ ╚═════╝ ╚═╝  ╚═══╝╚═╝╚══════╝");
Console.WriteLine("");

// Initialize encryption service
var encryptionService = new EncryptionService(AppContext.BaseDirectory);

// Encrypt config files if plain
encryptionService.EncryptConfigFiles();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Initialize Serilog globally 
    builder.Host.UseSerilog();

    // Load global settings from appsettings.json first
    var globalSettings = builder.Configuration.GetSection("ChangeTracking:GlobalSettings").Get<GlobalSettings>() ?? new GlobalSettings();

    // Determine which environment file(s) to load
    var envDir = Directory.GetDirectories(".").FirstOrDefault(d => d.Equals("environments", StringComparison.OrdinalIgnoreCase)) ?? "environments";
    var selectedEnvironment = Environment.GetEnvironmentVariable("TRIGNIS_ENVIRONMENT") 
        ?? builder.Configuration.GetValue<string>("SelectedEnvironment");
    
    List<EnvironmentConfig> environments = new();
    
    if (Directory.Exists(envDir))
    {
        var jsonFiles = Directory.GetFiles(envDir, "*.json").OrderBy(f => Path.GetFileName(f)).ToList();
        
        // Filter files based on selected environment
        if (!string.IsNullOrEmpty(selectedEnvironment))
        {
            var targetFile = jsonFiles.FirstOrDefault(f => 
                Path.GetFileNameWithoutExtension(f).Equals(selectedEnvironment, StringComparison.OrdinalIgnoreCase));
            
            if (targetFile != null)
            {
                jsonFiles = new List<string> { targetFile };
                Log.Information($"Loading specific environment: {selectedEnvironment}");
            }
            else
            {
                Log.Warning($"Environment '{selectedEnvironment}' not found. Available: {string.Join(", ", jsonFiles.Select(f => Path.GetFileNameWithoutExtension(f)))}");
                Log.Information("Loading all environment files...");
            }
        }
        else
        {
            Log.Debug("No specific environment selected. Loading all environment files...");
        }

        foreach (var file in jsonFiles)
        {
            var relativePath = Path.GetRelativePath(".", file);
            var environmentName = Path.GetFileNameWithoutExtension(file);
            
            // Load configuration
            var tempBuilder = new ConfigurationBuilder();
            tempBuilder.AddEncryptedJsonFile(relativePath, encryptionService, optional: true);
            var tempCfg = tempBuilder.Build();
            
            // Collect connection strings
            var connStrings = tempCfg.GetSection("ConnectionStrings").GetChildren();
            var connectionStrings = new Dictionary<string, string>();
            foreach (var connString in connStrings)
            {
                var key = connString.Key;
                var value = connString.Value;
                if (!string.IsNullOrEmpty(value))
                {
                    connectionStrings[key] = value;
                }
            }

            // Collect ChangeTracking settings
            var ct = tempCfg.GetSection("ChangeTracking");

            // Load tracking objects (stamp EnvironmentFile at construction via `with`)
            var trackingObjects = (ct.GetSection("TrackingObjects").Get<TrackingObject[]>() ?? Array.Empty<TrackingObject>())
                .Select(obj => obj with { EnvironmentFile = environmentName })
                .ToArray();

            // Load API endpoints (stamp EnvironmentFile at construction via `with`)
            var apiEndpoints = (ct.GetSection("ApiEndpoints").Get<ApiEndpoint[]>() ?? Array.Empty<ApiEndpoint>())
                .Select(endpoint => endpoint with { EnvironmentFile = environmentName })
                .ToArray();

            // Build environment config
            var envConfig = new EnvironmentConfig
            {
                Name = environmentName,
                ConnectionStrings = connectionStrings,
                ChangeTracking = new EnvironmentChangeTracking
                {
                    TrackingObjects = trackingObjects,
                    ApiEndpoints = apiEndpoints,
                    // Load environment-specific overrides (if present)
                    PollingIntervalSeconds = ct.GetValue<int?>("PollingIntervalSeconds"),
                    ExportToFile = ct.GetValue<bool?>("ExportToFile"),
                    FilePath = ct.GetValue<string?>("FilePath"),
                    ExportToApi = ct.GetValue<bool?>("ExportToApi"),
                    RetryCount = ct.GetValue<int?>("RetryCount"),
                    RetryDelaySeconds = ct.GetValue<int?>("RetryDelaySeconds")
                }
            };

            environments.Add(envConfig);
            
            Log.Debug($"Loaded environment: {environmentName} ({trackingObjects.Length} objects, {apiEndpoints.Length} endpoints)");
        }
    }
    else
    {
        Log.Warning($"Environments directory '{envDir}' does not exist. Please create it and add environment configuration files.");
    }

    // Add environments to configuration
    if (environments.Any())
    {
        var combinedObj = new 
        { 
            ChangeTracking = new 
            { 
                GlobalSettings = globalSettings,
                Environments = environments 
            } 
        };
        var options = new JsonSerializerOptions 
        { 
            WriteIndented = true,
            PropertyNamingPolicy = null // Preserve property names as-is
        };
        var combinedJson = JsonSerializer.Serialize(combinedObj, options);
        
        Log.Debug("Environments configuration loaded successfully");
        
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(combinedJson));
        builder.Configuration.AddJsonStream(stream);
    }
    else
    {
        Log.Warning("No environments were loaded from the environments folder");
    }

    // Validate configuration
    ConfigurationValidator.ValidateConfiguration(builder.Configuration);

    // Log configuration status
    ConfigurationLogger.LogConfigurationStatus(builder.Configuration);

    // Use Windows Service hosting
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "Trignis (Agent)";
    });

    // Configure shutdown timeout
    builder.Services.Configure<HostOptions>(options =>
    {
        options.ShutdownTimeout = TimeSpan.FromSeconds(30);
    });

    // Register services
    builder.Services.AddHostedService<ChangeTrackingBackgroundService>();
    builder.Services.AddSingleton<DeadLetterQueueMonitor>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<DeadLetterQueueMonitor>());
    builder.Services.AddSingleton<ConnectionHealthCheckService>();
    builder.Services.AddHostedService<ConnectionHealthCheckService>();
    builder.Services.AddSingleton<DeadLetterService>();
    builder.Services.AddSingleton<HealthCheckService>();
    builder.Services.AddSingleton<MessageQueueService>();
    builder.Services.AddSingleton<OAuth2TokenService>();
    builder.Services.AddSingleton(encryptionService);
    builder.Services.AddSingleton<EnvironmentConfigService>();
    builder.Services.AddHttpClient();

    var app = builder.Build();

    // Initialize environment config service with the already-loaded environments and start file watcher
    var envConfigService = app.Services.GetRequiredService<EnvironmentConfigService>();
    envConfigService.Initialize(environments, envDir, selectedEnvironment);
    envConfigService.StartWatching();

    // Eagerly resolve HealthCheckService so its _startTime reflects actual application start
    app.Services.GetRequiredService<HealthCheckService>();

    // Read web config early so auth middleware can be registered before static files
    var healthEnabled = builder.Configuration.GetValue<bool>("Health:Enabled", false);
    var healthPort = builder.Configuration.GetValue<int>("Health:Port", 2455);
    var healthHost = builder.Configuration.GetValue<string>("Health:Host", "*");
    var webHostEnabled = builder.Configuration.GetValue<bool>("WebHost:Enabled", false);
    var webHostHost = builder.Configuration.GetValue<string>("WebHost:Host", "*");
    var adminApiKey = builder.Configuration.GetValue<string>("Trignis:AdminApiKey", "");
    var authEnabled = webHostEnabled && !string.IsNullOrEmpty(adminApiKey);

    // Auth token helpers — stateless HMAC-SHA256 signed tokens
    const string AuthCookieName = "trignis_auth";
    const int AuthTokenExpiryHours = 24;

    byte[] GetSigningKey() =>
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(adminApiKey));

    string GenerateAuthToken()
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(AuthTokenExpiryHours).ToUnixTimeSeconds();
        using var hmac = new System.Security.Cryptography.HMACSHA256(GetSigningKey());
        return $"{expiry}.{Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(expiry.ToString())))}";
    }

    bool ValidateAuthToken(string token)
    {
        var dot = token.IndexOf('.');
        if (dot < 0) return false;
        var expiryStr = token[..dot];
        if (!long.TryParse(expiryStr, out var expiry)) return false;
        if (DateTimeOffset.FromUnixTimeSeconds(expiry) < DateTimeOffset.UtcNow) return false;
        using var hmac = new System.Security.Cryptography.HMACSHA256(GetSigningKey());
        var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(expiryStr)));
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(token[(dot + 1)..]));
    }

    // Auth middleware — registered before static files so .html paths are also protected
    if (authEnabled)
    {
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (!path.StartsWithSegments("/ui") ||
                path.StartsWithSegments("/ui/login") ||
                path.StartsWithSegments("/ui/api/auth"))
            {
                await next(); return;
            }
            if (!context.Request.Cookies.TryGetValue(AuthCookieName, out var cookie) ||
                !ValidateAuthToken(cookie))
            {
                context.Response.Redirect("/ui/login");
                return;
            }
            await next();
        });
    }

    var defaultFilesOptions = new DefaultFilesOptions { RequestPath = "/ui" };
    defaultFilesOptions.DefaultFileNames.Clear();
    defaultFilesOptions.DefaultFileNames.Add("dashboard.html");
    app.UseDefaultFiles(defaultFilesOptions);
    app.UseStaticFiles();

    if (healthEnabled || webHostEnabled)
    {
        app.Urls.Add($"http://{healthHost}:{healthPort}");

        // Restrict /ui paths to loopback connections when WebHost:Host is localhost
        if (webHostEnabled && (webHostHost == "localhost" || webHostHost == "127.0.0.1"))
        {
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/ui"))
                {
                    var remote = context.Connection.RemoteIpAddress;
                    if (remote == null || !System.Net.IPAddress.IsLoopback(remote))
                    {
                        context.Response.StatusCode = 403;
                        await context.Response.WriteAsync("Web UI is restricted to localhost.");
                        return;
                    }
                }
                await next();
            });
        }

        // ── Trignis UI ───────────────────────────────────────────────────────────
        if (webHostEnabled)
        {
        // Auth routes
        var loginFilePath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "ui", "login.html");
        app.MapGet("/ui/login", () =>
            authEnabled ? Results.File(loginFilePath, "text/html") : Results.Redirect("/ui/dashboard"));
        app.MapGet("/ui/login.html", () => Results.Redirect("/ui/login"));

        app.MapPost("/ui/api/auth", async (HttpContext context) =>
        {
            var body = await context.Request.ReadFromJsonAsync<JsonElement>();
            var provided = body.TryGetProperty("apiKey", out var kp) ? kp.GetString() ?? "" : "";
            var provHash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(provided));
            var expHash  = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(adminApiKey));
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(provHash, expHash))
                return Results.Json(new { error = "Invalid API key" }, statusCode: 401);
            var token = GenerateAuthToken();
            context.Response.Cookies.Append(AuthCookieName, token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                Expires = DateTimeOffset.UtcNow.AddHours(AuthTokenExpiryHours)
            });
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/ui/api/auth/logout", (HttpContext context) =>
        {
            context.Response.Cookies.Append(AuthCookieName, "", new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                Expires = DateTimeOffset.UnixEpoch
            });
            return Results.Ok();
        });

        app.MapGet("/ui", () => Results.Redirect("/ui/dashboard"));

        // Clean URLs: serve pages at /ui/{page} and redirect /ui/{page}.html → /ui/{page}
        foreach (var page in new[] { "dashboard", "environments", "settings", "deadletters", "logs" })
        {
            var p = page;
            var filePath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "ui", $"{p}.html");
            app.MapGet($"/ui/{p}", () => Results.File(filePath, "text/html"));
            app.MapGet($"/ui/{p}.html", () => Results.Redirect($"/ui/{p}"));
        }

        app.MapGet("/ui/api/overview", async (DeadLetterQueueMonitor dlqMonitor) =>
        {
            long dlTotal = 0, dlLast24h = 0, dlLastHour = 0;
            try
            {
                var stats = await dlqMonitor.GetStatsAsync();
                dlTotal = stats.TotalCount;
                dlLast24h = stats.Last24HoursCount;
                dlLastHour = stats.LastHourCount;
            }
            catch { /* sinkhole.db may not exist yet */ }

            return Results.Json(new
            {
                version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0",
                environment_count = envConfigService.Environments.Count,
                tracking_object_count = envConfigService.Environments.Sum(e => e.ChangeTracking.TrackingObjects.Length),
                endpoint_count = envConfigService.Environments.Sum(e => e.ChangeTracking.ApiEndpoints.Length),
                dead_letters = new { total = dlTotal, last_24h = dlLast24h, last_hour = dlLastHour }
            });
        });

        app.MapGet("/ui/api/environments", () =>
        {
            var result = envConfigService.Environments.Select(env => new
            {
                name = env.Name,
                connection_string_keys = env.ConnectionStrings.Keys.ToArray(),
                settings = new
                {
                    polling_interval_seconds = env.ChangeTracking.PollingIntervalSeconds,
                    export_to_file = env.ChangeTracking.ExportToFile,
                    file_path = env.ChangeTracking.FilePath,
                    export_to_api = env.ChangeTracking.ExportToApi,
                    retry_count = env.ChangeTracking.RetryCount,
                    retry_delay_seconds = env.ChangeTracking.RetryDelaySeconds
                },
                tracking_objects = env.ChangeTracking.TrackingObjects.Select(t => new
                {
                    name = t.Name,
                    database = t.Database,
                    table_name = t.TableName,
                    stored_procedure_name = t.StoredProcedureName,
                    initial_sync_mode = t.InitialSyncMode
                }),
                api_endpoints = env.ChangeTracking.ApiEndpoints.Select(e => new
                {
                    key = e.Key,
                    url = e.Url,
                    auth_type = e.Auth?.Type,
                    auth_username = e.Auth?.Username,
                    auth_client_id = e.Auth?.ClientId,
                    auth_token_endpoint = e.Auth?.TokenEndpoint,
                    auth_scope = e.Auth?.Scope,
                    auth_header_name = e.Auth?.HeaderName,
                    // auth credentials (Token, Password, ApiKey, ClientSecret) are intentionally omitted
                    mq_type = e.MessageQueueType,
                    enable_compression = e.EnableCompression,
                    mq = e.MessageQueue == null ? null : new
                    {
                        host_name = e.MessageQueue.HostName,
                        port = e.MessageQueue.Port,
                        virtual_host = e.MessageQueue.VirtualHost,
                        username = e.MessageQueue.Username,
                        queue_name = e.MessageQueue.QueueName,
                        exchange = e.MessageQueue.Exchange,
                        routing_key = e.MessageQueue.RoutingKey,
                        topic_name = e.MessageQueue.TopicName,
                        queue_url = e.MessageQueue.QueueUrl,
                        region = e.MessageQueue.Region,
                        event_hub_name = e.MessageQueue.EventHubName,
                        bootstrap_servers = e.MessageQueue.BootstrapServers,
                        topic = e.MessageQueue.Topic,
                        security_protocol = e.MessageQueue.SecurityProtocol,
                        sasl_mechanism = e.MessageQueue.SaslMechanism
                        // Password, AccessKeyId, SecretAccessKey, ConnectionString intentionally omitted
                    }
                })
            });
            return Results.Json(result);
        });

        app.MapGet("/ui/api/settings", () =>
        {
            var logMinLevel = builder.Configuration.GetValue<string>("Serilog:MinimumLevel:Default", "Information");
            var logSinks = builder.Configuration.GetSection("Serilog:WriteTo").GetChildren()
                .Select(s => s.GetValue<string>("Name"))
                .Where(n => !string.IsNullOrEmpty(n))
                .ToArray();

            return Results.Json(new
            {
                global = globalSettings,
                server = new
                {
                    host = builder.Configuration.GetValue<string>("Health:Host", "*"),
                    port = builder.Configuration.GetValue<int>("Health:Port", 2455),
                    cache_duration_seconds = builder.Configuration.GetValue<int>("Health:CacheDurationSeconds", 120)
                },
                logging = new { min_level = logMinLevel, sinks = logSinks }
            });
        });

        app.MapGet("/ui/api/deadletters", async (int page = 1, int pageSize = 50, string? search = null, string? objectFilter = null) =>
        {
            if (!File.Exists("sinkhole.db"))
                return Results.Json(new { total = 0, page = 1, page_size = pageSize, total_pages = 0, data = Array.Empty<object>() });

            try
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=sinkhole.db");
                await conn.OpenAsync();

                var conditions = new List<string>();
                if (!string.IsNullOrEmpty(search))
                    conditions.Add("(TrackingObjectName LIKE @search OR ErrorMessage LIKE @search OR DatabaseName LIKE @search)");
                if (!string.IsNullOrEmpty(objectFilter))
                    conditions.Add("TrackingObjectName = @objectFilter");
                var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

                var countCmd = conn.CreateCommand();
                countCmd.CommandText = $"SELECT COUNT(*) FROM DeadLetters {where}";
                if (!string.IsNullOrEmpty(search)) countCmd.Parameters.AddWithValue("@search", $"%{search}%");
                if (!string.IsNullOrEmpty(objectFilter)) countCmd.Parameters.AddWithValue("@objectFilter", objectFilter);
                var totalResult = await countCmd.ExecuteScalarAsync();
                var total = (totalResult != null && totalResult != DBNull.Value) ? Convert.ToInt64(totalResult) : 0;

                var offset = (page - 1) * pageSize;
                var dataCmd = conn.CreateCommand();
                dataCmd.CommandText = $@"
                    SELECT Id, SourceKey, TrackingObjectName, DatabaseName, DataHash, Data, ErrorMessage, Timestamp
                    FROM DeadLetters {where}
                    ORDER BY Timestamp DESC
                    LIMIT @pageSize OFFSET @offset";
                if (!string.IsNullOrEmpty(search)) dataCmd.Parameters.AddWithValue("@search", $"%{search}%");
                if (!string.IsNullOrEmpty(objectFilter)) dataCmd.Parameters.AddWithValue("@objectFilter", objectFilter);
                dataCmd.Parameters.AddWithValue("@pageSize", pageSize);
                dataCmd.Parameters.AddWithValue("@offset", offset);

                var items = new List<object>();
                using var reader = await dataCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    items.Add(new
                    {
                        id = reader.GetInt64(0),
                        source_key = reader.GetString(1),
                        tracking_object_name = reader.GetString(2),
                        database_name = reader.GetString(3),
                        data_hash = reader.GetString(4),
                        data = reader.GetString(5),
                        error_message = reader.GetString(6),
                        timestamp = reader.IsDBNull(7) ? null : reader.GetString(7)
                    });
                }

                return Results.Json(new
                {
                    total,
                    page,
                    page_size = pageSize,
                    total_pages = (int)Math.Ceiling((double)total / pageSize),
                    data = items
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message, total = 0, page = 1, page_size = pageSize, total_pages = 0, data = Array.Empty<object>() });
            }
        });

        app.MapGet("/ui/api/logs", async (int limit = 200, int offset = 0, string? level = null) =>
        {
            var logDir = Path.Combine(AppContext.BaseDirectory, "log");
            if (!Directory.Exists(logDir))
                return Results.Json(new { file = (string?)null, total = 0, lines = Array.Empty<object>(), has_more = false });

            var files = Directory.GetFiles(logDir, "log-*.txt")
                .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                .Take(3)
                .ToList();

            if (!files.Any())
                return Results.Json(new { file = (string?)null, total = 0, lines = Array.Empty<object>(), has_more = false });

            var logPattern = new System.Text.RegularExpressions.Regex(
                @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) \[(\w{3})\] (.*)$");

            var allEntries = new List<(string Timestamp, string Level, string Message)>();
            string? lastFile = null;

            foreach (var file in files)
            {
                try
                {
                    string content;
                    using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(stream))
                    {
                        content = await sr.ReadToEndAsync();
                    }

                    var rawLines = content.Split('\n');
                    string? ts = null, lvl = null;
                    var msgParts = new List<string>();

                    foreach (var rawLine in rawLines)
                    {
                        var m = logPattern.Match(rawLine);
                        if (m.Success)
                        {
                            if (ts != null)
                                allEntries.Add((ts, lvl ?? "INF", string.Join("\n", msgParts).TrimEnd()));
                            ts = m.Groups[1].Value;
                            lvl = m.Groups[2].Value.ToUpper();
                            msgParts = new List<string> { m.Groups[3].Value };
                        }
                        else if (ts != null && !string.IsNullOrWhiteSpace(rawLine))
                        {
                            msgParts.Add(rawLine.TrimEnd());
                        }
                    }
                    if (ts != null)
                        allEntries.Add((ts, lvl ?? "INF", string.Join("\n", msgParts).TrimEnd()));

                    lastFile ??= file;
                    if (allEntries.Count >= limit * 5) break;
                }
                catch { /* skip if file is inaccessible */ }
            }

            // Newest first — sort by timestamp string (ISO-like format is lexicographically comparable)
            allEntries.Sort((a, b) => string.CompareOrdinal(b.Timestamp, a.Timestamp));

            var filtered = string.IsNullOrEmpty(level) || level.Equals("ALL", StringComparison.OrdinalIgnoreCase)
                ? allEntries
                : allEntries.Where(e => e.Level.Equals(level, StringComparison.OrdinalIgnoreCase)).ToList();

            var hasMore = offset + limit < filtered.Count;
            var paged = filtered.Skip(offset).Take(limit).ToList();

            var returned = paged
                .Select(e => new { timestamp = e.Timestamp, level = e.Level, message = e.Message });

            return Results.Json(new
            {
                file = lastFile != null ? Path.GetFileName(lastFile) : null,
                total = filtered.Count,
                has_more = hasMore,
                lines = returned
            });
        });

        // ── End Trignis UI ───────────────────────────────────────────────────────
        }
        else
        {
            var webUiUnavailable = Results.Json(new { status = "unavailable", reason = "Web UI is disabled (WebHost:Enabled is false)" }, statusCode: 503);
            app.MapGet("/ui", () => webUiUnavailable);
        }

        // Root redirect / discovery
        app.MapGet("/", (HttpContext context) =>
        {
            if (webHostEnabled)
                return Results.Redirect("/ui");

            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            var response = new
            {
                service = "trignis-service",
                version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0",
                endpoints = new
                {
                    health_url = $"{baseUrl}/health",
                    deadletters_url = $"{baseUrl}/health/deadletters",
                    connections_url = $"{baseUrl}/health/connections",
                    state_url = $"{baseUrl}/health/state",
                    state_environment_url = $"{baseUrl}/health/state/{{environmentName}}"
                }
            };
            return Results.Json(response);
        });

        if (healthEnabled)
        {

        app.MapGet("/health", async (HealthCheckService healthService) =>
        {
            var health = await healthService.GetHealthStatusAsync();
            return Results.Content(health, "application/json");
        });

        app.MapGet("/health/deadletters", async (DeadLetterQueueMonitor dlqMonitor) =>
        {
            var stats = await dlqMonitor.GetStatsAsync();
            return Results.Json(stats);
        });

        app.MapGet("/health/connections", (ConnectionHealthCheckService connHealth) =>
        {
            var status = connHealth.GetHealthStatus();
            var result = status.Details.ToDictionary(
                kvp => kvp.Key,
                kvp => new
                {
                    is_healthy = kvp.Value.IsHealthy,
                    last_error = kvp.Value.IsHealthy
                        ? null
                        : kvp.Value.ConsecutiveFailures > 0
                            ? $"{kvp.Value.ConsecutiveFailures} consecutive failure(s)"
                            : "Unhealthy"
                });
            return Results.Json(result);
        });

        // State database endpoint showing tracking versions per environment
        app.MapGet("/health/state", async () =>
        {
            // Build a lookup: envName -> objectName -> stored procedure
            var spLookup = envConfigService.Environments
                .ToDictionary(
                    e => e.Name,
                    e => e.ChangeTracking.TrackingObjects
                        .ToDictionary(t => t.Name, t => t.StoredProcedureName ?? string.Empty,
                            StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);

            try
            {
                var stateDbPath = builder.Configuration.GetValue<string>("ChangeTracking:StateDbPath", "state.db");
                var connectionString = $"Data Source={stateDbPath}";

                using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
                await conn.OpenAsync();

                // Get all state grouped by environment
                var command = conn.CreateCommand();
                command.CommandText = @"
                    SELECT 
                        EnvironmentName,
                        COUNT(*) as ObjectCount
                    FROM LastVersions
                    GROUP BY EnvironmentName
                    ORDER BY EnvironmentName
                ";

                var environments = new List<object>();

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var envName = reader.GetString(0);
                    var objectCount = reader.GetInt32(1);

                    // Get objects for this environment
                    var objectCommand = conn.CreateCommand();
                    objectCommand.CommandText = @"
                        SELECT 
                            ObjectName,
                            LastVersion,
                            LastUpdated
                        FROM LastVersions
                        WHERE EnvironmentName = @environmentName
                        ORDER BY ObjectName
                    ";
                    objectCommand.Parameters.AddWithValue("@environmentName", envName);

                    var envSpLookup = spLookup.TryGetValue(envName, out var envSps) ? envSps : null;
                    var objects = new List<object>();
                    using var objectReader = await objectCommand.ExecuteReaderAsync();
                    while (await objectReader.ReadAsync())
                    {
                        var objName = objectReader.GetString(0);
                        var sp = envSpLookup != null && envSpLookup.TryGetValue(objName, out var spName) ? spName : null;
                        objects.Add(new
                        {
                            object_name = objName,
                            stored_procedure_name = sp,
                            last_version = objectReader.GetInt32(1),
                            last_updated = objectReader.GetDateTime(2).ToString("yyyy-MM-ddTHH:mm:ssZ")
                        });
                    }

                    environments.Add(new
                    {
                        name = envName,
                        object_count = objectCount,
                        objects = objects
                    });
                }

                var response = new
                {
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    total_environments = environments.Count,
                    environments = environments
                };

                return Results.Json(response);
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    error = "Failed to read state database",
                    message = ex.Message
                });
            }
        });

        // Endpoint to query specific environment state
        app.MapGet("/health/state/{environmentName}", async (string environmentName) =>
        {
            try
            {
                var stateDbPath = builder.Configuration.GetValue<string>("ChangeTracking:StateDbPath", "state.db");
                var connectionString = $"Data Source={stateDbPath}";

                using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
                await conn.OpenAsync();

                var command = conn.CreateCommand();
                command.CommandText = @"
                    SELECT 
                        ObjectName,
                        LastVersion,
                        LastUpdated
                    FROM LastVersions
                    WHERE EnvironmentName = @environmentName
                    ORDER BY ObjectName
                ";
                command.Parameters.AddWithValue("@environmentName", environmentName);

                var objects = new List<object>();

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    objects.Add(new
                    {
                        object_name = reader.GetString(0),
                        last_version = reader.GetInt32(1),
                        last_updated = reader.GetDateTime(2).ToString("yyyy-MM-ddTHH:mm:ssZ")
                    });
                }

                if (objects.Count == 0)
                {
                    return Results.NotFound(new
                    {
                        error = "Environment not found",
                        environment = environmentName
                    });
                }

                var response = new
                {
                    environment = environmentName,
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    object_count = objects.Count,
                    objects = objects
                };

                return Results.Json(response);
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    error = "Failed to read state database",
                    message = ex.Message
                });
            }
        });

        } // end if (healthEnabled)

        // 404 handler for all other routes
        app.MapFallback((HttpContext context) =>
        {
            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            return Results.NotFound(new
            {
                error = "Not Found",
                message = $"The requested endpoint '{context.Request.Path}' does not exist"
            });
        });
    }
    
    // Register shutdown handlers
    var lifetime = app.Lifetime;
    
    lifetime.ApplicationStarted.Register(() =>
    {
        Log.Information("✓ Application started successfully");
        Log.Information("");
    });

    lifetime.ApplicationStopping.Register(() =>
    {
        Log.Information("");
        Log.Information("Exit: Application is stopping...");
    });

    lifetime.ApplicationStopped.Register(() =>
    {
        Log.Information("Application stopped");
    });

    // Run the application
    app.Run();
    
    Log.Information("Exit: Application shutdown complete");
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    
    // Show error if Serilog fails
    Console.WriteLine("");
    Console.WriteLine("Fatal error during application startup:");
    Console.WriteLine(ex.ToString());
    
    // If running as console, wait for user input
    if (!OperatingSystem.IsWindows() || Environment.UserInteractive)
    {
        Console.WriteLine("\nPress any key to exit...");
        Console.ReadKey();
    }
    
    Environment.Exit(1);
}
finally
{
    Log.CloseAndFlush();
}
using Trignis.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.IO;
using Serilog;
using Serilog.Sinks.EventLog;
using Trignis.Helpers;
using System;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using System.Linq;
using Trignis.Models;
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
    const string envDir = "environments";
    var selectedEnvironment = Environment.GetEnvironmentVariable("TRIGNIS_ENVIRONMENT") 
        ?? builder.Configuration.GetValue<string>("SelectedEnvironment");
    
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
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, ".core", "dp-keys")));
    builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(globalSettings));
    builder.Services.AddSingleton<WebUiAuth>();
    builder.Services.AddHostedService<ChangeTrackingBackgroundService>();
    builder.Services.AddSingleton<DeadLetterQueueMonitor>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<DeadLetterQueueMonitor>());
    builder.Services.AddSingleton<ConnectionHealthCheckService>();
    builder.Services.AddHostedService<ConnectionHealthCheckService>();
    builder.Services.AddSingleton<DeadLetterService>();
    builder.Services.AddSingleton<PauseService>();
    builder.Services.AddSingleton<DeadLetterReplayer>();
    builder.Services.AddHostedService<DeadLetterReplayService>();
    builder.Services.AddSingleton<RetryPolicies>();
    builder.Services.AddSingleton<ExportService>();
    builder.Services.AddSingleton<HealthCheckService>();
    builder.Services.AddSingleton<MessageQueueService>();
    builder.Services.AddSingleton<OAuth2TokenService>();
    builder.Services.AddSingleton(encryptionService);
    builder.Services.AddSingleton<EnvironmentConfigService>();
    builder.Services.AddHttpClient();

    var app = builder.Build();

    var envConfigService = app.Services.GetRequiredService<EnvironmentConfigService>();

    List<EnvironmentConfig> environments = [];

    if (Directory.Exists(envDir))
    {
        var jsonFiles = Directory.GetFiles(envDir, "*.json").OrderBy(Path.GetFileName).ToList();

        if (!string.IsNullOrEmpty(selectedEnvironment))
        {
            var targetFile = jsonFiles.FirstOrDefault(f =>
                Path.GetFileNameWithoutExtension(f).Equals(selectedEnvironment, StringComparison.OrdinalIgnoreCase));

            if (targetFile != null)
            {
                jsonFiles = [targetFile];
                Log.Information($"Loading specific environment: {selectedEnvironment}");
            }
            else
            {
                Log.Warning($"Environment '{selectedEnvironment}' not found. Available: {string.Join(", ", jsonFiles.Select(Path.GetFileNameWithoutExtension))}");
                Log.Information("Loading all environment files...");
            }
        }
        else
        {
            Log.Debug("No specific environment selected. Loading all environment files...");
        }

        foreach (var file in jsonFiles)
        {
            var envConfig = envConfigService.LoadFile(file);
            if (envConfig == null) continue;

            environments.Add(envConfig);
            Log.Debug($"Loaded environment: {envConfig.Name} ({envConfig.ChangeTracking.TrackingObjects.Length} objects, {envConfig.ChangeTracking.ApiEndpoints.Length} endpoints)");
        }
    }
    else
    {
        Log.Warning($"Environments directory '{envDir}' does not exist. Please create it and add environment configuration files.");
    }

    if (environments.Count == 0)
    {
        Log.Warning("No environments were loaded from the environments folder");
    }

    ConfigurationValidator.ValidateConfiguration(environments, globalSettings,
        builder.Configuration.GetValue<bool>("Health:Enabled", false)
            ? builder.Configuration.GetValue<int>("Health:Port", 2455)
            : null);

    ConfigurationLogger.LogConfigurationStatus(builder.Configuration, environments, globalSettings);

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

    const string AuthCookieName = "trignis_auth";
    const string CsrfCookieName = "trignis_csrf";
    const int AuthTokenExpiryHours = 24;

    var webUiAuth = app.Services.GetRequiredService<WebUiAuth>();

    // Explicit config wins; otherwise follow the scheme of the request being answered, so a
    // plain-HTTP dev run still works while a TLS deployment gets the flag automatically.
    var configuredSecureCookies = builder.Configuration.GetValue<bool?>("WebHost:SecureCookies");
    bool UseSecureCookies(HttpContext context) => configuredSecureCookies ?? context.Request.IsHttps;

    CookieOptions SessionCookie(HttpContext context, bool httpOnly, DateTimeOffset expires) => new()
    {
        HttpOnly = httpOnly,
        Secure = UseSecureCookies(context),
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Expires = expires
    };

    var authProtector = app.Services
        .GetRequiredService<IDataProtectionProvider>()
        .CreateProtector("Trignis.WebUi.Auth")
        .ToTimeLimitedDataProtector();

    string GenerateAuthToken() =>
        authProtector.Protect("authenticated", TimeSpan.FromHours(AuthTokenExpiryHours));

    bool ValidateAuthToken(string token)
    {
        try
        {
            authProtector.Unprotect(token);
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    // Auth middleware: registered before static files so .html paths are also protected
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

        // Trignis UI
        if (webHostEnabled)
        {
        // Auth routes
        var uiRoot = Path.Combine(AppContext.BaseDirectory, "ui");
        var uiVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        app.MapGet("/ui/login", () =>
            authEnabled ? WebUiPages.ServePage(Path.Combine(uiRoot, "login.html"), uiVersion) : Results.Redirect("/ui/dashboard"));
        app.MapGet("/ui/login.html", () => Results.Redirect("/ui/login"));

        // One-time token the login form must echo back
        app.MapGet("/ui/api/auth/csrf", () => Results.Json(new { csrf = webUiAuth.GenerateCsrfToken() }));

        app.MapPost("/ui/api/auth", async (HttpContext context) =>
        {
            var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            if (webUiAuth.CheckAccess(clientIp) is { } blockReason)
                return Results.Json(new { error = blockReason }, statusCode: 429);

            var body = await context.Request.ReadFromJsonAsync<JsonElement>();

            var csrfToken = body.TryGetProperty("csrf", out var csrf) ? csrf.GetString() : null;
            if (!webUiAuth.ValidateCsrfToken(csrfToken))
            {
                webUiAuth.RecordFailedAttempt(clientIp);
                return Results.Json(new { error = "Invalid or expired CSRF token" }, statusCode: 403);
            }

            var provided = body.TryGetProperty("apiKey", out var kp) ? kp.GetString() ?? "" : "";
            if (!PassphraseMatches(provided, adminApiKey))
            {
                webUiAuth.RecordFailedAttempt(clientIp);
                return Results.Json(new { error = "Invalid API key" }, statusCode: 401);
            }

            webUiAuth.ClearFailedAttempts(clientIp);
            webUiAuth.ConsumeCsrfToken(csrfToken!);

            var expires = DateTimeOffset.UtcNow.AddHours(AuthTokenExpiryHours);
            context.Response.Cookies.Append(AuthCookieName, GenerateAuthToken(),
                SessionCookie(context, httpOnly: true, expires));

            // Readable by page JS so mutating fetches can echo it in X-CSRF-Token
            context.Response.Cookies.Append(CsrfCookieName, WebUiAuth.NewSessionCsrf(),
                SessionCookie(context, httpOnly: false, expires));

            return Results.Ok(new { ok = true });
        });

        app.MapPost("/ui/api/auth/logout", (HttpContext context) =>
        {
            context.Response.Cookies.Append(AuthCookieName, "",
                SessionCookie(context, httpOnly: true, DateTimeOffset.UnixEpoch));
            context.Response.Cookies.Append(CsrfCookieName, "",
                SessionCookie(context, httpOnly: false, DateTimeOffset.UnixEpoch));
            return Results.Ok();
        });

        // Double-submit gate for every mutating UI endpoint. Null means the request may proceed.
        // Skipped when auth is off, since there is no session to forge against.
        IResult? RejectIfCsrfInvalid(HttpContext context)
        {
            if (!authEnabled)
                return null;

            context.Request.Cookies.TryGetValue(CsrfCookieName, out var cookie);
            var header = context.Request.Headers["X-CSRF-Token"].ToString();

            return WebUiAuth.IsDoubleSubmitValid(header, cookie)
                ? null
                : Results.Json(new { error = "Missing or invalid CSRF token" }, statusCode: 403);
        }

        static bool PassphraseMatches(string provided, string expected)
        {
            var provHash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(provided));
            var expHash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(expected));
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(provHash, expHash);
        }

        // Step-up gate for actions that require the admin passphrase.
        IResult? RejectIfPassphraseInvalid(HttpContext context, JsonElement body)
        {
            // Nothing to step up from: with auth off the whole UI is already open.
            if (!authEnabled)
                return null;

            var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            if (webUiAuth.CheckAccess(clientIp) is { } blockReason)
                return Results.Json(new { error = blockReason }, statusCode: 429);

            var provided = body.TryGetProperty("passphrase", out var p) ? p.GetString() ?? "" : "";

            if (!PassphraseMatches(provided, adminApiKey))
            {
                webUiAuth.RecordFailedAttempt(clientIp);
                Log.Warning("Rejected a pause request from {Ip}: wrong passphrase", clientIp);
                return Results.Json(new { error = "Incorrect passphrase" }, statusCode: 401);
            }

            webUiAuth.ClearFailedAttempts(clientIp);
            return null;
        }

        // Clearing the row makes the next cycle re-initialise the object per its InitialSyncMode.
        // Without this the only recovery from a bad sync is stopping the service and editing state.db.
        app.MapPost("/ui/api/state/{environmentName}/{objectName}/reset", async (HttpContext context, string environmentName, string objectName) =>
        {
            if (RejectIfCsrfInvalid(context) is { } rejected) return rejected;

            try
            {
                var stateDbPath = builder.Configuration.GetValue<string>("ChangeTracking:StateDbPath", "state.db");

                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={stateDbPath}");
                await conn.OpenAsync();

                var command = conn.CreateCommand();
                command.CommandText = "DELETE FROM LastVersions WHERE EnvironmentName = @environmentName AND ObjectName = @objectName";
                command.Parameters.AddWithValue("@environmentName", environmentName);
                command.Parameters.AddWithValue("@objectName", objectName);

                if (await command.ExecuteNonQueryAsync() == 0)
                    return Results.NotFound(new { error = "No sync state stored for that environment and object" });

                Log.Warning("Sync state for {Environment}/{Object} reset from the web UI; the next cycle will re-initialise it",
                    environmentName, objectName);

                return Results.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to reset sync state for {Environment}/{Object}", environmentName, objectName);
                return Results.Json(new { error = "Failed to reset sync state", message = ex.Message }, statusCode: 500);
            }
        });

        // Resend a dead letter to its environment's destinations. The row is only removed once
        // every destination succeeds, so a partial failure stays queued for another attempt.
        app.MapPost("/ui/api/deadletters/{id:long}/replay", async (
            HttpContext context, long id, DeadLetterService deadLetters, DeadLetterReplayer replayer) =>
        {
            if (RejectIfCsrfInvalid(context) is { } rejected) return rejected;

            var record = await deadLetters.GetAsync(id, context.RequestAborted);
            if (record == null)
                return Results.NotFound(new { error = "Dead letter not found" });

            try
            {
                var result = await replayer.ReplayAsync(record, context.RequestAborted);

                switch (result.Outcome)
                {
                    case DeadLetterReplayer.Outcome.Replayed:
                        Log.Information("Dead letter {Id} replayed from the web UI and removed", id);
                        return Results.Ok(new { ok = true });

                    case DeadLetterReplayer.Outcome.Unroutable:
                        return Results.Json(new { error = result.Reason }, statusCode: 409);

                    default:
                        // A human asking for a replay is a signal the downstream problem was
                        // addressed, so put the row back in the automatic rotation either way.
                        await deadLetters.ResetAttemptsAsync(id, context.RequestAborted);
                        return Results.Json(new
                        {
                            error = "Replay failed; the dead letter was kept",
                            failures = result.Failures.Select(f => new { target = f.Target, message = f.Error.Message })
                        }, statusCode: 502);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Replay of dead letter {Id} failed", id);
                return Results.Json(new { error = "Replay failed", message = ex.Message }, statusCode: 500);
            }
        });

        app.MapPost("/ui/api/deadletters/{id:long}/discard", async (
            HttpContext context, long id, DeadLetterService deadLetters) =>
        {
            if (RejectIfCsrfInvalid(context) is { } rejected) return rejected;

            if (!await deadLetters.DeleteAsync(id, context.RequestAborted))
                return Results.NotFound(new { error = "Dead letter not found" });

            Log.Warning("Dead letter {Id} discarded from the web UI", id);
            return Results.Ok(new { ok = true });
        });

        // Purges exactly what the current filter selects, so the UI cannot delete more than it shows
        app.MapPost("/ui/api/deadletters/purge", async (
            HttpContext context, DeadLetterService deadLetters, string? search = null, string? objectFilter = null) =>
        {
            if (RejectIfCsrfInvalid(context) is { } rejected) return rejected;

            var deleted = await deadLetters.PurgeAsync(search, objectFilter, context.RequestAborted);
            return Results.Ok(new { ok = true, deleted });
        });

        // Pausing stops changes being read and exported, but the source database keeps recording
        // them, so this is a "hold", not an "off". Guarded by the admin passphrase because the
        // failure mode is silent: nothing errors, data simply stops moving.
        app.MapPost("/ui/api/pause", async (HttpContext context, PauseService pauseService) =>
        {
            if (RejectIfCsrfInvalid(context) is { } rejected) return rejected;

            var body = await context.Request.ReadFromJsonAsync<JsonElement>(context.RequestAborted);
            if (RejectIfPassphraseInvalid(context, body) is { } denied) return denied;

            if (ResolveScope(body) is not { } resolved)
                return Results.BadRequest(new { error = "Specify an environment, and an object when pausing a single tracking object" });

            var reason = body.TryGetProperty("reason", out var r) ? r.GetString() : null;
            var by = context.Connection.RemoteIpAddress?.ToString();

            await pauseService.PauseAsync(resolved.Scope, reason, by, context.RequestAborted);
            Log.Warning("Paused {Label} from the web UI ({Reason})", resolved.Label, reason ?? "no reason given");

            return Results.Ok(new { ok = true, scope = resolved.Scope, label = resolved.Label });
        });

        // Resuming is the safe direction, so it needs no passphrase. Making operators re-authenticate
        // to restore service is how an incident gets longer.
        app.MapPost("/ui/api/resume", async (HttpContext context, PauseService pauseService) =>
        {
            if (RejectIfCsrfInvalid(context) is { } rejected) return rejected;

            var body = await context.Request.ReadFromJsonAsync<JsonElement>(context.RequestAborted);

            if (ResolveScope(body) is not { } resolved)
                return Results.BadRequest(new { error = "Specify an environment, and an object when resuming a single tracking object" });

            var resumed = await pauseService.ResumeAsync(resolved.Scope, context.RequestAborted);
            if (resumed)
                Log.Information("Resumed {Label} from the web UI", resolved.Label);

            return Results.Ok(new { ok = true, resumed, scope = resolved.Scope, label = resolved.Label });
        });

        app.MapGet("/ui/api/pauses", async (PauseService pauseService, HttpContext context) =>
            Results.Json(await pauseService.ListAsync(context.RequestAborted)));

        // Shared by pause and resume so the two can never disagree on what a scope string means.
        static (string Scope, string Label)? ResolveScope(JsonElement body)
        {
            var environment = body.TryGetProperty("environment", out var e) ? e.GetString() : null;
            if (string.IsNullOrWhiteSpace(environment))
                return null;

            var objectName = body.TryGetProperty("object", out var o) ? o.GetString() : null;

            return string.IsNullOrWhiteSpace(objectName)
                ? (PauseService.EnvironmentScope(environment), $"environment '{environment}'")
                : (PauseService.ObjectScope(environment, objectName), $"'{objectName}' in '{environment}'");
        }

        app.MapGet("/ui", () => Results.Redirect("/ui/dashboard"));

        // Clean URLs: compose pages at /ui/{page} and redirect /ui/{page}.html → /ui/{page}
        foreach (var (page, title) in WebUiPages.Titles)
        {
            var p = page;
            var t = title;
            app.MapGet($"/ui/{p}", () => WebUiPages.Compose(uiRoot, p, t, uiVersion));
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
            catch (Exception ex)
            {
                // sinkhole.db is created on first dead letter, so this is expected on a fresh install
                Log.Debug(ex, "Dead letter stats unavailable for the overview card");
            }

            return Results.Json(new
            {
                version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0",
                environment_count = envConfigService.Environments.Count,
                tracking_object_count = envConfigService.Environments.Sum(e => e.ChangeTracking.TrackingObjects.Length),
                endpoint_count = envConfigService.Environments.Sum(e => e.ChangeTracking.ApiEndpoints.Length),
                // Lets the pause dialog know whether to ask for the passphrase; says nothing secret.
                auth_enabled = authEnabled,
                dead_letters = new { total = dlTotal, last_24h = dlLast24h, last_hour = dlLastHour }
            });
        });

        app.MapGet("/ui/api/environments", async (PauseService pauseService, HttpContext context) =>
        {
            var paused = await pauseService.GetPausedScopesAsync(context.RequestAborted);

            var result = envConfigService.Environments.Select(env => new
            {
                name = env.Name,
                provider = env.Provider,
                paused = paused.Contains(PauseService.EnvironmentScope(env.Name)),
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
                    initial_sync_mode = t.InitialSyncMode,
                    paused = paused.Contains(PauseService.ObjectScope(env.Name, t.Name))
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
                var pragmaCmd = conn.CreateCommand();
                pragmaCmd.CommandText = "PRAGMA busy_timeout = 3000;";
                await pragmaCmd.ExecuteNonQueryAsync();

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
                catch (Exception ex)
                {
                    // A log file being rolled or held open should not blank the whole log view
                    Log.Debug(ex, "Skipped unreadable log file {File}", file);
                }
            }

            // Newest first, sort by timestamp string (ISO-like format is lexicographically comparable)
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

        // End Trignis UI //
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
            var result = connHealth.GetHealthStatus().ToDictionary(
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

        // Tracking versions per environment; the whole set, or one named environment
        app.MapGet("/health/state", () => ReadStateAsync(null));
        app.MapGet("/health/state/{environmentName}", (string environmentName) => ReadStateAsync(environmentName));

        async Task<IResult> ReadStateAsync(string? environmentName)
        {
            // envName -> objectName -> stored procedure
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

                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={stateDbPath}");
                await conn.OpenAsync();

                var command = conn.CreateCommand();
                command.CommandText = @"
                    SELECT EnvironmentName, ObjectName, LastVersion, LastUpdated
                    FROM LastVersions
                    WHERE @environmentName IS NULL OR EnvironmentName = @environmentName
                    ORDER BY EnvironmentName, ObjectName
                ";
                command.Parameters.AddWithValue("@environmentName", (object?)environmentName ?? DBNull.Value);

                var byEnvironment = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var envName = reader.GetString(0);
                        var objName = reader.GetString(1);

                        if (!byEnvironment.TryGetValue(envName, out var objects))
                            byEnvironment[envName] = objects = new List<object>();

                        objects.Add(new
                        {
                            object_name = objName,
                            stored_procedure_name = spLookup.TryGetValue(envName, out var sps)
                                && sps.TryGetValue(objName, out var sp) ? sp : null,
                            last_version = reader.GetInt64(2),
                            last_updated = reader.GetDateTime(3).ToString("yyyy-MM-ddTHH:mm:ssZ")
                        });
                    }
                }

                var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

                if (environmentName != null)
                {
                    if (!byEnvironment.TryGetValue(environmentName, out var objects))
                        return Results.NotFound(new { error = "Environment not found", environment = environmentName });

                    return Results.Json(new
                    {
                        environment = environmentName,
                        timestamp,
                        object_count = objects.Count,
                        objects
                    });
                }

                var environments = byEnvironment
                    .Select(kv => new { name = kv.Key, object_count = kv.Value.Count, objects = kv.Value })
                    .ToList();

                return Results.Json(new
                {
                    timestamp,
                    total_environments = environments.Count,
                    environments
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = "Failed to read state database", message = ex.Message });
            }
        }

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
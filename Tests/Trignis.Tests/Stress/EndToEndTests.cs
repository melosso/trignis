using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Trignis.Tests.Stress;

/// <summary>Runs if TRIGNIS_E2E is set and the parity databases are up (see Tests/e2e/run.sh).</summary>
public sealed class EndToEndFactAttribute : FactAttribute
{
    public EndToEndFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TRIGNIS_E2E") is null || ParitySource.ConnectionString("mssql") is null || ParitySource.ConnectionString("postgres") is null)
            Skip = "Set TRIGNIS_E2E and the parity connection strings (see Tests/e2e/run.sh)";
    }
}

/// <summary>The built Trignis.dll as a process: every HTTP route, the change pipeline, the JSON migration and the web UI pages.</summary>
[Collection("SqliteTests")]
public sealed class EndToEndTests(ITestOutputHelper output)
{
    private const string AdminKey = "e2e-admin-key";
    private static readonly string AppBuild = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../Source/bin/Debug/net10.0"));

    [EndToEndFact]
    public async Task EveryRoute_AndThePipeline_WorkOnTheBuiltApp()
    {
        await using var mssql = await ParitySource.CreateAsync("mssql", transactional: true);
        await using var postgres = await ParitySource.CreateAsync("postgres", transactional: true);
        await using var sink = new HttpSink();
        await using var app = await App.StartAsync(AdminKey, dir =>
        {
            File.WriteAllText(Path.Combine(dir, "environments", "e2e.yaml"), $$"""
                provider: mssql
                databases:
                  src: "{{mssql.Connection}}"
                polling:
                  interval: 1s
                retry:
                  count: 0
                tables:
                  - name: items
                    database: src
                    table: {{mssql.Table}}
                    procedure: {{mssql.Procedure}}
                    initial_sync: full
                destinations:
                  - name: files
                    type: file
                    path: "{{Path.Combine(dir, "exports").Replace("\\", "/")}}/{object}/changes-{timestamp}.json"
                  - name: hook
                    type: http
                    url: {{sink.Url}}
                """);
            // v0.4 file: migrated at startup.
            File.WriteAllText(Path.Combine(dir, "environments", "legacy.json"), $$"""
                { "Provider": "postgres", "ConnectionStrings": { "pg": "{{postgres.Connection}}" },
                  "ChangeTracking": { "PollingIntervalSeconds": 1, "ExportToApi": true,
                    "TrackingObjects": [ { "Name": "rows", "Database": "pg", "TableName": "{{postgres.Table}}", "StoredProcedureName": "{{postgres.Procedure}}", "InitialSyncMode": "Full" } ],
                    "ApiEndpoints": [ { "Key": "legacy-hook", "Url": "{{sink.Url}}" } ] } }
                """);
        });

        var anonymous = app.Client(cookies: false);
        var admin = app.Client(cookies: true);

        // Startup migration
        Assert.True(File.Exists(Path.Combine(app.Dir, "environments", "legacy.yaml")));
        Assert.True(File.Exists(Path.Combine(app.Dir, "environments", "legacy.json.migrated")));
        Assert.DoesNotContain(postgres.Connection.Split("Password=")[1].Split(';')[0], File.ReadAllText(Path.Combine(app.Dir, "environments", "legacy.yaml")));
        Assert.Contains("Migrating environments to new format", app.Log);

        // Anonymous access
        await Expect(anonymous, HttpMethod.Get, "/", HttpStatusCode.Redirect);
        await Expect(anonymous, HttpMethod.Get, "/ui", HttpStatusCode.Redirect);
        await Expect(anonymous, HttpMethod.Get, "/ui/login", HttpStatusCode.OK);
        await Expect(anonymous, HttpMethod.Get, "/ui/login.html", HttpStatusCode.Redirect);
        await Expect(anonymous, HttpMethod.Get, "/ui/dashboard", HttpStatusCode.Redirect);
        foreach (var api in new[] { "overview", "environments", "settings", "deadletters", "logs", "pauses" })
            await Expect(anonymous, HttpMethod.Get, $"/ui/api/{api}", HttpStatusCode.Unauthorized);
        foreach (var health in new[] { "/health", "/health/deadletters", "/health/connections", "/health/state" })
            await Expect(anonymous, HttpMethod.Get, health, HttpStatusCode.OK);

        var headers = (await anonymous.GetAsync("/ui/login")).Headers;
        Assert.Contains("frame-ancestors 'none'", headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", headers.GetValues("X-Content-Type-Options").Single());

        // Login
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(admin, "wrong-key"));
        Assert.Equal(HttpStatusCode.OK, await LoginAsync(admin, AdminKey));
        foreach (var page in new[] { "dashboard", "environments", "deadletters", "logs", "settings" })
            await Expect(admin, HttpMethod.Get, $"/ui/{page}", HttpStatusCode.OK);
        foreach (var api in new[] { "overview", "environments", "settings", "deadletters", "logs", "pauses" })
            await Expect(admin, HttpMethod.Get, $"/ui/api/{api}", HttpStatusCode.OK);

        var environments = await admin.GetFromJsonAsync<JsonElement>("/ui/api/environments");
        Assert.Equal(["e2e", "legacy"], environments.EnumerateArray().Select(e => e.GetProperty("name").GetString()).Order());

        // CSRF is required for every mutation
        await Expect(admin, HttpMethod.Post, "/ui/api/resume", HttpStatusCode.Forbidden, new { environment = "e2e" }, csrf: false);
        await Expect(admin, HttpMethod.Post, "/ui/api/client-errors", HttpStatusCode.Forbidden, new { message = "x" }, csrf: false);

        // Pipeline: both environments deliver
        await mssql.InsertAsync(5);
        await postgres.InsertAsync(4);
        await WaitUntil(() => sink.Accepted.Count >= 9, "first delivery");
        await Expect(anonymous, HttpMethod.Get, "/health/state/e2e", HttpStatusCode.OK);
        await Expect(anonymous, HttpMethod.Get, "/health/state/missing", HttpStatusCode.NotFound);
        await WaitUntil(() => Directory.Exists(Path.Combine(app.Dir, "exports")) && Directory.GetFiles(Path.Combine(app.Dir, "exports"), "*.json", SearchOption.AllDirectories).Length > 0, "file export");

        // Pause and resume
        await Expect(admin, HttpMethod.Post, "/ui/api/pause", HttpStatusCode.Unauthorized, new { environment = "e2e", passphrase = "wrong" });
        await Expect(admin, HttpMethod.Post, "/ui/api/pause", HttpStatusCode.OK, new { environment = "e2e", passphrase = AdminKey, reason = "e2e" });
        Assert.Contains("env:e2e", (await admin.GetStringAsync("/ui/api/pauses")));
        var beforePause = sink.Accepted.Count;
        await mssql.InsertAsync(2);
        await Task.Delay(3000);
        Assert.Equal(beforePause, sink.Accepted.Count);
        await Expect(admin, HttpMethod.Post, "/ui/api/resume", HttpStatusCode.OK, new { environment = "e2e" });
        await WaitUntil(() => sink.Accepted.Count >= beforePause + 2, "delivery after resume");

        // Failure, dead letter, replay
        sink.Status = 503;
        await mssql.InsertAsync(3);
        var letter = await WaitForDeadLetterAsync(admin);
        sink.Status = 200;
        await Expect(admin, HttpMethod.Post, $"/ui/api/deadletters/{letter}/replay", HttpStatusCode.OK);
        Assert.Equal(0, (await admin.GetFromJsonAsync<JsonElement>("/ui/api/deadletters")).GetProperty("total").GetInt32());
        await Expect(admin, HttpMethod.Post, "/ui/api/deadletters/999999/replay", HttpStatusCode.NotFound);

        // Discard and purge
        sink.Status = 503;
        await mssql.InsertAsync(1);
        letter = await WaitForDeadLetterAsync(admin);
        await Expect(admin, HttpMethod.Post, $"/ui/api/deadletters/{letter}/discard", HttpStatusCode.OK);
        await mssql.InsertAsync(1);
        await WaitForDeadLetterAsync(admin);
        await Expect(admin, HttpMethod.Post, "/ui/api/deadletters/purge", HttpStatusCode.OK);
        sink.Status = 200;

        // Reset, client errors, paging bounds
        await Expect(admin, HttpMethod.Post, "/ui/api/state/e2e/items/reset", HttpStatusCode.OK);
        await Expect(admin, HttpMethod.Post, "/ui/api/state/e2e/nothing/reset", HttpStatusCode.NotFound);
        await Expect(admin, HttpMethod.Post, "/ui/api/client-errors", HttpStatusCode.NoContent, new { message = "e2e probe", page = "/ui/dashboard" });
        await Expect(admin, HttpMethod.Get, "/ui/api/deadletters?pageSize=0&page=-1", HttpStatusCode.OK);

        // Hot reload: an invalid edit is rejected; the previous configuration runs
        var envFile = Path.Combine(app.Dir, "environments", "e2e.yaml");
        var valid = File.ReadAllText(envFile);
        File.WriteAllText(envFile, valid.Replace("interval: 1s", "interval: often"));
        await WaitUntil(async () => (await admin.GetStringAsync("/ui/api/environments")).Contains("expected a duration"), "rejected edit badge");
        File.WriteAllText(envFile, valid);
        await WaitUntil(async () => !(await admin.GetStringAsync("/ui/api/environments")).Contains("expected a duration"), "badge cleared");

        // Logout revokes the session
        await Expect(admin, HttpMethod.Post, "/ui/api/auth/logout", HttpStatusCode.OK);
        await Expect(admin, HttpMethod.Get, "/ui/api/overview", HttpStatusCode.Unauthorized);

        Assert.DoesNotContain("[ERR] Unhandled", app.Log);
        output.WriteLine($"{sink.Accepted.Count} rows delivered over HTTP");
    }

    [EndToEndFact]
    public async Task EveryPage_RendersInABrowser_WithoutErrors()
    {
        var chrome = Chrome() ?? throw new InvalidOperationException("Headless Chromium not found. Set TRIGNIS_CHROME or install Playwright's chromium_headless_shell.");
        await using var mssql = await ParitySource.CreateAsync("mssql", transactional: true);
        await using var sink = new HttpSink { Status = 503 };
        await using var app = await App.StartAsync(adminKey: "", dir => File.WriteAllText(Path.Combine(dir, "environments", "shop's \"<e2e>\".yaml"), $$"""
            databases:
              src: "{{mssql.Connection}}"
            polling:
              interval: 1s
            retry:
              count: 0
            tables:
              - name: "items <i>x</i> O'Brien"
                database: src
                table: {{mssql.Table}}
                procedure: {{mssql.Procedure}}
                initial_sync: full
            destinations:
              - name: "hook'1"
                type: http
                url: {{sink.Url}}
            """));

        await mssql.InsertAsync(3);
        var client = app.Client(cookies: false);
        await WaitUntil(async () => (await client.GetFromJsonAsync<JsonElement>("/ui/api/deadletters")).GetProperty("total").GetInt32() > 0, "dead letter for the screens");

        var screens = Path.Combine(AppContext.BaseDirectory, "screens");
        Directory.CreateDirectory(screens);
        foreach (var page in new[] { "dashboard", "environments", "deadletters", "logs", "settings" })
        {
            var (dom, console) = await RenderAsync(chrome, $"{app.Url}/ui/{page}", Path.Combine(screens, $"{page}.png"));
            Assert.DoesNotContain("Uncaught", console);
            Assert.DoesNotContain("\"<e2e>\"", dom);
            Assert.DoesNotContain("<i>x</i>", dom);
            output.WriteLine($"{page}: {dom.Length} bytes of DOM, screenshot {page}.png");
        }

        Assert.Contains("items &lt;i&gt;x&lt;/i&gt; O'Brien", (await RenderAsync(chrome, $"{app.Url}/ui/environments", null)).Dom);
        await Task.Delay(1000);
        Assert.DoesNotContain("Web UI error", app.Log);
    }

    [EndToEndFact]
    public async Task ReadOnlyEnvironments_LoadJsonInMemory_AndUnmigratableJson_StopsStartup()
    {
        await using var mssql = await ParitySource.CreateAsync("mssql", transactional: true);
        var json = $$"""
            { "ConnectionStrings": { "src": "{{mssql.Connection}}" },
              "ChangeTracking": { "TrackingObjects": [ { "Name": "items", "Database": "src", "TableName": "{{mssql.Table}}", "StoredProcedureName": "{{mssql.Procedure}}" } ] } }
            """;

        // Read-only folder (ConfigMap, :ro mount): warning, JSON loaded, nothing written.
        string? readOnly = null;
        await using (var app = await App.StartAsync("", dir =>
        {
            readOnly = Path.Combine(dir, "environments");
            File.WriteAllText(Path.Combine(readOnly, "mounted.json"), json);
            File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }))
        {
            try
            {
                Assert.Contains("is not writable", app.Log);
                var environments = await app.Client(cookies: false).GetFromJsonAsync<JsonElement>("/ui/api/environments");
                Assert.Equal("mounted", Assert.Single(environments.EnumerateArray()).GetProperty("name").GetString());
                Assert.Equal(["mounted.json"], Directory.GetFiles(readOnly!).Select(Path.GetFileName));
            }
            finally
            {
                File.SetUnixFileMode(readOnly!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        // Writable folder, unconvertible JSON: startup stops with the reason.
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => App.StartAsync("", dir =>
            File.WriteAllText(Path.Combine(dir, "environments", "broken.json"), "{ not json")));
        Assert.Contains("broken.json: could not be migrated to YAML", failure.Message);
    }

    private static async Task<HttpStatusCode> LoginAsync(HttpClient client, string key)
    {
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/ui/api/auth/csrf")).GetProperty("csrf").GetString();
        return (await client.PostAsJsonAsync("/ui/api/auth", new { csrf, apiKey = key })).StatusCode;
    }

    private static async Task Expect(HttpClient client, HttpMethod method, string path, HttpStatusCode status, object? body = null, bool csrf = true)
    {
        using var request = new HttpRequestMessage(method, path);
        if (method == HttpMethod.Post)
        {
            request.Content = JsonContent.Create(body ?? new { });
            if (csrf && App.CsrfOf(client) is { } token) request.Headers.Add("X-CSRF-Token", token);
        }
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == status, $"{method} {path}: expected {(int)status}, got {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<long> WaitForDeadLetterAsync(HttpClient client)
    {
        long id = 0;
        await WaitUntil(async () =>
        {
            var data = (await client.GetFromJsonAsync<JsonElement>("/ui/api/deadletters")).GetProperty("data");
            if (data.GetArrayLength() == 0) return false;
            id = data[0].GetProperty("id").GetInt64();
            return true;
        }, "dead letter");
        return id;
    }

    private static Task WaitUntil(Func<bool> condition, string what) => WaitUntil(() => Task.FromResult(condition()), what);

    private static async Task WaitUntil(Func<Task<bool>> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!await condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(60)) Assert.Fail($"timed out waiting for {what}");
            await Task.Delay(250);
        }
    }

    private static string? Chrome()
    {
        if (Environment.GetEnvironmentVariable("TRIGNIS_CHROME") is { } configured) return configured;
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "ms-playwright");
        return Directory.Exists(cache)
            ? Directory.GetFiles(cache, "chrome-headless-shell", SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }

    private static async Task<(string Dom, string Console)> RenderAsync(string chrome, string url, string? screenshot)
    {
        async Task<(string, string)> Run(params string[] extra)
        {
            var start = new ProcessStartInfo(chrome) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "--no-sandbox", "--disable-gpu", "--virtual-time-budget=6000", "--enable-logging=stderr", "--v=0" }.Concat(extra).Append(url))
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            return (await stdout, await stderr);
        }

        var (dom, console) = await Run("--dump-dom");
        if (screenshot is not null) await Run($"--screenshot={screenshot}", "--window-size=1440,900");
        return (dom, string.Join('\n', console.Split('\n').Where(l => l.Contains("CONSOLE"))));
    }

    /// <summary>A copy of the build output, started as its own process.</summary>
    private sealed class App : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _log = new();
        private readonly List<(HttpClient Client, CookieContainer? Cookies)> _clients = [];
        private static readonly Dictionary<HttpClient, CookieContainer> Jars = new();

        public string Dir { get; }
        public string Url { get; }
        public string Log { get { lock (_log) return _log.ToString(); } }

        private App(Process process, string dir, string url)
        {
            _process = process;
            Dir = dir;
            Url = url;
        }

        public static async Task<App> StartAsync(string adminKey, Action<string> prepare)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"trignis-e2e-{Guid.NewGuid():N}");
            CopyDirectory(AppBuild, dir);
            foreach (var stale in new[] { "state.db", "sinkhole.db", ".core", "log", "environments", "exports" }.Select(p => Path.Combine(dir, p)))
            {
                if (File.Exists(stale)) File.Delete(stale);
                if (Directory.Exists(stale)) Directory.Delete(stale, recursive: true);
            }
            Directory.CreateDirectory(Path.Combine(dir, "environments"));
            prepare(dir);

            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var start = new ProcessStartInfo("dotnet", "Trignis.dll") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            start.Environment["WebHost__Enabled"] = "true";
            start.Environment["Health__Enabled"] = "true";
            start.Environment["Health__Host"] = "127.0.0.1";
            start.Environment["Health__Port"] = port.ToString();
            start.Environment["Trignis__AdminApiKey"] = adminKey;
            start.Environment["TRIGNIS_ENCRYPTION_KEY"] = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
            start.Environment["ChangeTracking__GlobalSettings__DeadLetterAutoReplayEnabled"] = "false";
            start.Environment["ChangeTracking__GlobalSettings__RetryCount"] = "0";

            var app = new App(Process.Start(start)!, dir, $"http://127.0.0.1:{port}");
            app._process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (app._log) app._log.AppendLine(e.Data); };
            app._process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (app._log) app._log.AppendLine(e.Data); };
            app._process.BeginOutputReadLine();
            app._process.BeginErrorReadLine();

            using var health = new HttpClient();
            var clock = Stopwatch.StartNew();
            while (true)
            {
                if (app._process.HasExited)
                {
                    var log = app.Log;
                    try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
                    throw new InvalidOperationException($"Trignis exited during startup:\n{log}");
                }
                try { if ((await health.GetAsync($"{app.Url}/health")).IsSuccessStatusCode) break; } catch (HttpRequestException) { }
                if (clock.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException($"Trignis did not start:\n{app.Log}");
                await Task.Delay(250);
            }
            return app;
        }

        public HttpClient Client(bool cookies)
        {
            var jar = new CookieContainer();
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = cookies, CookieContainer = jar }) { BaseAddress = new Uri(Url) };
            lock (Jars) Jars[client] = jar;
            _clients.Add((client, cookies ? jar : null));
            return client;
        }

        public static string? CsrfOf(HttpClient client)
        {
            lock (Jars)
                return Jars.TryGetValue(client, out var jar)
                    ? jar.GetAllCookies().FirstOrDefault(c => c.Name == "trignis_csrf") is { } cookie ? Uri.UnescapeDataString(cookie.Value) : null
                    : null;
        }

        private static void CopyDirectory(string from, string to)
        {
            foreach (var directory in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(directory.Replace(from, to));
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
                File.Copy(file, file.Replace(from, to), overwrite: true);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var (client, _) in _clients) client.Dispose();
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            _process.Dispose();
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
        }
    }
}

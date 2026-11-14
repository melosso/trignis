using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Trignis.Tests.Stress;

/// <summary>
/// A real HTTP destination on a loopback port. Records the row ids of every request it
/// accepted, and can be told to refuse, so a test can check what was delivered where
/// rather than what the service believes it sent.
/// </summary>
internal sealed class HttpSink : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly ConcurrentQueue<long> _accepted = new();
    private int _requests;

    public string Url { get; }

    /// <summary>Status to answer with. Anything but 2xx means the body is not recorded.</summary>
    public volatile int Status = 200;

    /// <summary>Awaited before answering, to hold a request open while the test does something else.</summary>
    public volatile Func<Task>? BeforeRespond;

    public int Requests => Volatile.Read(ref _requests);

    public IReadOnlyList<long> Accepted => _accepted.ToArray();

    public HttpSink()
    {
        // Ask the OS for a free port, then hand it to the listener
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        Url = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(Url);
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) when (_stop.IsCancellationRequested || !_listener.IsListening) { return; }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            Interlocked.Increment(ref _requests);

            if (BeforeRespond is { } hold) await hold();

            var status = Status;
            if (status is >= 200 and < 300)
            {
                using var doc = JsonDocument.Parse(body);
                foreach (var row in doc.RootElement.EnumerateArray())
                    _accepted.Enqueue(row.GetProperty("id").GetInt64());
            }

            context.Response.StatusCode = status;
            context.Response.Close();
        }
        catch (Exception)
        {
            // The client went away mid-request (a cancelled cycle); nothing was accepted
            try { context.Response.Abort(); } catch (Exception) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        _listener.Close();
        _stop.Dispose();
    }
}

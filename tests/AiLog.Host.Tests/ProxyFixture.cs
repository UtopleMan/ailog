using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using AiLog.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;

namespace AiLog.Host.Tests;

/// <summary>A fake upstream provider plus an ailog instance routing "/fake" to it.</summary>
public sealed class ProxyFixture : IAsyncDisposable
{
    public string LogsPath { get; private init; } = Path.Combine(Path.GetTempPath(), "ailog-tests", Guid.NewGuid().ToString("N"));
    public TaskCompletionSource StreamGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource UpstreamCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public HttpClient Client { get; private set; } = null!;

    private WebApplication _upstream = null!;
    private WebApplication _proxy = null!;

    public static async Task<ProxyFixture> StartAsync(string? logsPath = null)
    {
        var fixture = logsPath is null ? new ProxyFixture() : new ProxyFixture { LogsPath = logsPath };
        await fixture.InitializeAsync();
        return fixture;
    }

    private async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        _upstream = builder.Build();

        _upstream.MapPost("/v1/messages", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            ctx.Response.Headers["x-upstream-saw-key-sha256"] = Sha256(ctx.Request.Headers["x-api-key"].ToString());
            ctx.Response.Headers["x-upstream-saw-query"] = ctx.Request.QueryString.ToString();
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync($$"""{"echo":{{body}}}""");
        });

        _upstream.MapPost("/v1/stream", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync("event: message_start\ndata: {\"n\":1}\n\n");
            await ctx.Response.Body.FlushAsync();
            await StreamGate.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await ctx.Response.WriteAsync("event: message_stop\ndata: {\"n\":2}\n\n");
        });

        _upstream.MapGet("/v1/gzip", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/json";
            ctx.Response.Headers.ContentEncoding = "gzip";
            await ctx.Response.Body.WriteAsync(Gzip("""{"compressed":true}"""));
        });

        _upstream.MapGet("/v1/hang", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync("data: partial\n\n");
            await ctx.Response.Body.FlushAsync();
            try
            {
                await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                UpstreamCancelled.TrySetResult();
            }
        });

        await _upstream.StartAsync();
        var upstreamUrl = AiLogApp.GetListeningAddresses(_upstream).Single();

        _proxy = AiLogApp.Build(["--port", "0", "--logs", LogsPath, $"--AiLog:Routes:fake={upstreamUrl}", "--AiLog:Routes:dead=http://127.0.0.1:1"]);
        await _proxy.StartAsync();

        var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None };
        Client = new HttpClient(handler) { BaseAddress = new Uri(AiLogApp.GetListeningAddresses(_proxy).Single()) };
    }

    public static string Sha256(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }

        return output.ToArray();
    }

    /// <summary>The log file is written just after the response completes, so poll for it.</summary>
    public async Task<(string FileName, ExchangeLog Log)[]> WaitForLogsAsync(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var files = Directory.Exists(LogsPath)
                ? Directory.GetFiles(LogsPath, "*.json").Order(StringComparer.Ordinal).ToArray()
                : [];
            if (files.Length >= count)
            {
                return files.Select(f => (Path.GetFileName(f), JsonSerializer.Deserialize(File.ReadAllText(f), AiLogJsonContext.Default.ExchangeLog)!)).ToArray();
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Expected {count} log files in {LogsPath}, found {files.Length}.");
            }

            await Task.Delay(25);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _proxy.DisposeAsync();
        await _upstream.DisposeAsync();
        if (Directory.Exists(LogsPath))
        {
            Directory.Delete(LogsPath, recursive: true);
        }
    }
}

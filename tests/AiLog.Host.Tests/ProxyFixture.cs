using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiLog.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace AiLog.Host.Tests;

/// <summary>A fake upstream provider plus an ailog instance routing "/fake" to it.</summary>
public sealed class ProxyFixture : IAsyncDisposable
{
    /// <summary>Upper bound for anything a single test waits on.</summary>
    public static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    public const string SeenKeyHashHeader = "x-upstream-saw-key-sha256";
    public const string SeenQueryHeader = "x-upstream-saw-query";
    public const string CompressedJson = """{"compressed":true}""";

    public const string FirstStreamEvent = """
        event: message_start
        data: {"n":1}


        """;

    public const string LastStreamEvent = """
        event: message_stop
        data: {"n":2}


        """;

    public const string PartialStreamEvent = """
        data: partial


        """;

    private static readonly TimeSpan LogPollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan LogWaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StreamGateTimeout = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource timeout = new(TestTimeout);
    private WebApplication upstream = null!;
    private WebApplication proxy = null!;

    private ProxyFixture(string logsPath)
    {
        LogsPath = logsPath;
    }

    public string LogsPath { get; }

    public TaskCompletionSource StreamGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource UpstreamCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public HttpClient Client { get; private set; } = null!;

    /// <summary>Cancelled when the test has run for longer than <see cref="TestTimeout"/>.</summary>
    public CancellationToken CancellationToken => timeout.Token;

    public static async Task<ProxyFixture> StartAsync(string? logsPath = null)
    {
        ProxyFixture fixture = new(logsPath ?? NewLogsPath());
        await fixture.InitializeAsync();
        return fixture;
    }

    public static string NewLogsPath() => Path.Combine(Path.GetTempPath(), "ailog-tests", Guid.NewGuid().ToString("N"));

    public static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static byte[] Gzip(string text)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.Fastest))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }

        return output.ToArray();
    }

    /// <summary>Posts a JSON body to the fake upstream's messages endpoint through the proxy.</summary>
    public async Task PostMessageAsync(string json = "{}")
    {
        using StringContent content = new(json, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await Client.PostAsync("/fake/v1/messages", content, CancellationToken);
    }

    /// <summary>The log file is written just after the response completes, so poll for it.</summary>
    public async Task<LogFile[]> WaitForLogsAsync(int count)
    {
        DateTime deadline = DateTime.UtcNow + LogWaitTimeout;
        while (true)
        {
            string[] files = Directory.Exists(LogsPath)
                ? Directory.GetFiles(LogsPath, "*.json").Order(StringComparer.Ordinal).ToArray()
                : [];
            if (files.Length >= count)
            {
                return files.Select(ReadLogFile).ToArray();
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Expected {count} log files in {LogsPath}, found {files.Length}.");
            }

            await Task.Delay(LogPollInterval, CancellationToken);
        }
    }

    public async Task<ExchangeLog> WaitForFirstLogAsync()
    {
        LogFile[] files = await WaitForLogsAsync(1);
        return files[0].Log;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await proxy.DisposeAsync();
        await upstream.DisposeAsync();
        timeout.Dispose();
        if (Directory.Exists(LogsPath))
        {
            Directory.Delete(LogsPath, recursive: true);
        }
    }

    private async Task InitializeAsync()
    {
        upstream = BuildUpstream();
        await upstream.StartAsync(CancellationToken);
        string upstreamUrl = AiLogApp.GetListeningAddresses(upstream).Single();

        proxy = AiLogApp.Build(["--port", "0", "--logs", LogsPath, $"--AiLog:Routes:fake={upstreamUrl}", "--AiLog:Routes:dead=http://127.0.0.1:1"]);
        await proxy.StartAsync(CancellationToken);

        SocketsHttpHandler handler = new() { AutomaticDecompression = DecompressionMethods.None };
        Client = new HttpClient(handler) { BaseAddress = new Uri(AiLogApp.GetListeningAddresses(proxy).Single()) };
    }

    private WebApplication BuildUpstream()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        WebApplication app = builder.Build();
        app.MapPost("/v1/messages", EchoAsync);
        app.MapPost("/v1/stream", StreamUntilGateOpensAsync);
        app.MapGet("/v1/gzip", WriteCompressedAsync);
        app.MapGet("/v1/hang", HangUntilCancelledAsync);
        return app;
    }

    private static async Task EchoAsync(HttpContext context)
    {
        using StreamReader reader = new(context.Request.Body);
        string body = await reader.ReadToEndAsync(context.RequestAborted);
        context.Response.Headers[SeenKeyHashHeader] = Sha256(context.Request.Headers["x-api-key"].ToString());
        context.Response.Headers[SeenQueryHeader] = context.Request.QueryString.ToString();
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync($$"""{"echo":{{body}}}""", context.RequestAborted);
    }

    private async Task StreamUntilGateOpensAsync(HttpContext context)
    {
        context.Response.ContentType = "text/event-stream";
        await context.Response.WriteAsync(FirstStreamEvent, context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
        await StreamGate.Task.WaitAsync(StreamGateTimeout, context.RequestAborted);
        await context.Response.WriteAsync(LastStreamEvent, context.RequestAborted);
    }

    private static async Task WriteCompressedAsync(HttpContext context)
    {
        context.Response.ContentType = "application/json";
        context.Response.Headers.ContentEncoding = "gzip";
        await context.Response.Body.WriteAsync(Gzip(CompressedJson), context.RequestAborted);
    }

    private async Task HangUntilCancelledAsync(HttpContext context)
    {
        context.Response.ContentType = "text/event-stream";
        await context.Response.WriteAsync(PartialStreamEvent, context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
        try
        {
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            UpstreamCancelled.TrySetResult();
        }
    }

    private static LogFile ReadLogFile(string path) =>
        new(Path.GetFileName(path), JsonSerializer.Deserialize(File.ReadAllText(path), AiLogJsonContext.Default.ExchangeLog)!);
}

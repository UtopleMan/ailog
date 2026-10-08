using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using AiLog.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace AiLog.Host;

/// <summary>The Blazor UI and its API, all under the reserved /_ailog prefix so they never collide with proxy routes.</summary>
internal static class UiEndpoints
{
    public const string Prefix = "_ailog";

    private const string ReadyEvent = "ready";
    private const string ExchangeEvent = "exchange";

    private static readonly PathString Root = "/" + Prefix;
    private static readonly PathString PackageContent = "/_content";
    private static readonly PathString Api = Root + "/api";
    private static readonly PathString AppShell = Root + "/index.html";

    /// <summary>Client route of the detail page. Log ids contain a '.' (milliseconds), so they look like file names.</summary>
    private static readonly PathString ExchangePages = Root + "/exchanges";

    extension(WebApplication app)
    {
        /// <summary>Must run before routing.</summary>
        public void UseUiPaths() => app.Use(RewriteUiPath);

        public void MapUi()
        {
            RouteGroupBuilder api = app.MapGroup("/" + Prefix + "/api");
            api.MapGet("/logs", async (LogIndex index, CancellationToken aborted) =>
                TypedResults.Json(await index.GetAllAsync(aborted), AiLogApiJsonContext.Default.ListExchangeSummary));
            api.MapGet("/logs/events", (LogIndex index, IHostApplicationLifetime lifetime, CancellationToken aborted) =>
                TypedResults.ServerSentEvents(StreamEvents(index, lifetime.ApplicationStopping, aborted)));
            api.MapGet("/logs/{id}", GetLog);

            // Static web assets of AiLog.Web are published under the _ailog base path.
            if (HasAssets(app))
            {
                app.MapStaticAssets();
            }
        }
    }

    /// <summary>Null unless the id is a plain file name that resolves directly inside the logs folder.</summary>
    internal static string? ResolveLogFile(string logsPath, string id)
    {
        if (!IsPlainFileName(id))
        {
            return null;
        }

        string directory = Path.GetFullPath(logsPath);
        string path = Path.GetFullPath(Path.Combine(directory, id + ".json"));
        return Path.GetDirectoryName(path) == directory.TrimEnd(Path.DirectorySeparatorChar) ? path : null;
    }

    public static bool HasAssets(WebApplication app) =>
        File.Exists(Path.Combine(app.Environment.ContentRootPath, $"{app.Environment.ApplicationName}.staticwebassets.endpoints.json"));

    private static Task RewriteUiPath(HttpContext context, RequestDelegate next)
    {
        PathString path = context.Request.Path;
        if (path == Root)
        {
            context.Response.Redirect(Root + "/");
            return Task.CompletedTask;
        }

        // StaticWebAssetBasePath only moves AiLog.Web's own files under /_ailog; NuGet package assets
        // (BlazorBlueprint's CSS and JS) stay at /_content, but the app requests them relative to its base href.
        if (path.StartsWithSegments(Root + PackageContent, out PathString rest))
        {
            context.Request.Path = PackageContent + rest;
        }
        else if (IsClientRoute(path))
        {
            context.Request.Path = AppShell;
        }

        return next(context);
    }

    /// <summary>
    /// Client-side routes (anything without a file extension) load the app shell. Rewritten in middleware rather than
    /// with MapFallbackToFile because fallback endpoints lose to the proxy's catch-all.
    /// </summary>
    private static bool IsClientRoute(PathString path) =>
        path.StartsWithSegments(ExchangePages)
        || (path.StartsWithSegments(Root) && !path.StartsWithSegments(Api) && !Path.HasExtension(path.Value));

    /// <summary>The log file as written, streamed from disk without re-serialising (files can be several MB).</summary>
    private static IResult GetLog(string id, [FromServices] AiLogOptions options)
    {
        if (ResolveLogFile(options.LogsPath, id) is not { } path)
        {
            return TypedResults.BadRequest();
        }

        return File.Exists(path) ? TypedResults.PhysicalFile(path, "application/json") : TypedResults.NotFound();
    }

    private static bool IsPlainFileName(string id) =>
        id.Length > 0 && !id.StartsWith('.') && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    private static async IAsyncEnumerable<SseItem<string>> StreamEvents(
        LogIndex index, CancellationToken stopping, [EnumeratorCancellation] CancellationToken aborted)
    {
        using LogIndex.Subscription subscription = index.Subscribe();

        // End the stream cleanly on shutdown; otherwise an open UI tab holds Ctrl+C for the 30s shutdown timeout.
        using CancellationTokenSource done = CancellationTokenSource.CreateLinkedTokenSource(stopping, aborted);

        // Sent immediately so the client knows it is subscribed before it fetches the list.
        yield return new SseItem<string>("{}", ReadyEvent);

        while (await WaitForMoreAsync(subscription.Reader, done.Token))
        {
            while (subscription.Reader.TryRead(out ExchangeSummary? summary))
            {
                yield return ToExchangeEvent(summary);
            }
        }
    }

    /// <summary>False once the channel completes or the stream is cancelled.</summary>
    private static async Task<bool> WaitForMoreAsync(ChannelReader<ExchangeSummary> reader, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.WaitToReadAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static SseItem<string> ToExchangeEvent(ExchangeSummary summary)
    {
        string data = JsonSerializer.Serialize(summary, AiLogApiJsonContext.Default.ExchangeSummary);
        return new SseItem<string>(data, ExchangeEvent) { EventId = summary.Id };
    }
}

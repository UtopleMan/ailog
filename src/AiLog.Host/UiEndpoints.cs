using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AiLog.Contracts;

namespace AiLog.Host;

/// <summary>The Blazor UI and its API, all under the reserved /_ailog prefix so they never collide with proxy routes.</summary>
internal static class UiEndpoints
{
    public const string Prefix = "_ailog";

    private static readonly PathString Root = "/" + Prefix;
    private static readonly PathString PackageContent = "/_content";
    private static readonly PathString Api = Root + "/api";

    /// <summary>Must run before routing.</summary>
    public static void UseUiPaths(this WebApplication app) => app.Use((context, next) =>
    {
        var path = context.Request.Path;
        if (path == Root)
        {
            context.Response.Redirect(Root + "/");
            return Task.CompletedTask;
        }

        // StaticWebAssetBasePath only moves AiLog.Web's own files under /_ailog; NuGet package assets
        // (BlazorBlueprint's CSS and JS) stay at /_content, but the app requests them relative to its base href.
        if (path.StartsWithSegments(Root + PackageContent, out var rest))
        {
            context.Request.Path = PackageContent + rest;
        }
        // Client-side routes (anything without a file extension) load the app shell. Done here rather than with
        // MapFallbackToFile because fallback endpoints lose to the proxy's catch-all.
        else if (path.StartsWithSegments(Root) && !path.StartsWithSegments(Api) && !Path.HasExtension(path.Value))
        {
            context.Request.Path = Root + "/index.html";
        }

        return next(context);
    });

    public static void MapUi(this WebApplication app)
    {
        var api = app.MapGroup("/" + Prefix + "/api");
        api.MapGet("/logs", async (LogIndex index) =>
            TypedResults.Json(await index.GetAllAsync(), AiLogApiJsonContext.Default.ListExchangeSummary));
        api.MapGet("/logs/events", (LogIndex index, IHostApplicationLifetime lifetime, CancellationToken aborted) =>
            TypedResults.ServerSentEvents(StreamEvents(index, lifetime.ApplicationStopping, aborted)));

        // Static web assets of AiLog.Web are published under the _ailog base path.
        if (HasAssets(app))
        {
            app.MapStaticAssets();
        }
    }

    public static bool HasAssets(WebApplication app) =>
        File.Exists(Path.Combine(app.Environment.ContentRootPath, $"{app.Environment.ApplicationName}.staticwebassets.endpoints.json"));

    private static async IAsyncEnumerable<SseItem<string>> StreamEvents(
        LogIndex index, CancellationToken stopping, [EnumeratorCancellation] CancellationToken aborted)
    {
        using var subscription = index.Subscribe();

        // End the stream cleanly on shutdown; otherwise an open UI tab holds Ctrl+C for the 30s shutdown timeout.
        using var done = CancellationTokenSource.CreateLinkedTokenSource(stopping, aborted);

        // Sent immediately so the client knows it is subscribed before it fetches the list.
        yield return new SseItem<string>("{}", "ready");

        while (true)
        {
            bool more;
            try
            {
                more = await subscription.Reader.WaitToReadAsync(done.Token);
            }
            catch (OperationCanceledException)
            {
                more = false;
            }

            if (!more)
            {
                yield break;
            }

            while (subscription.Reader.TryRead(out var summary))
            {
                yield return new SseItem<string>(JsonSerializer.Serialize(summary, AiLogApiJsonContext.Default.ExchangeSummary), "exchange")
                {
                    EventId = summary.Id,
                };
            }
        }
    }
}

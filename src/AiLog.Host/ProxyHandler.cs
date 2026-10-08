using System.Buffers;
using System.Diagnostics;
using AiLog.Contracts;
using Microsoft.AspNetCore.Http.Features;

namespace AiLog.Host;

/// <summary>Forwards a harness request to its upstream unchanged, streams the response back, and logs both.</summary>
internal sealed class ProxyHandler(
    HttpClient client,
    AiLogOptions options,
    HeaderRedactor redactor,
    ExchangeLogWriter writer,
    TimeProvider time)
{
    private const int BufferSize = 16 * 1024;
    private const string ClientAbortedError = "The harness disconnected before the response completed.";
    private const string PlainTextUtf8 = "text/plain; charset=utf-8";

    private static readonly char[] RouteTerminators = ['/', '?'];

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade",
    };

    public async Task HandleAsync(HttpContext context)
    {
        DateTimeOffset startedAt = time.GetUtcNow();
        long startedTicks = Stopwatch.GetTimestamp();
        string target = ReadRawTarget(context);
        if (ResolveRoute(target) is not { } route)
        {
            await RespondNoRouteAsync(context, target);
            return;
        }

        Exchange exchange = new()
        {
            Context = context,
            Target = target,
            Route = route,
            Sequence = writer.NextSequence(),
            StartedAt = startedAt,
            StartedTicks = startedTicks,
        };

        using HttpRequestMessage upstreamRequest = CreateUpstreamRequest(exchange);
        ForwardResult result = await ForwardAsync(exchange, upstreamRequest);
        using HttpResponseMessage? upstream = result.Upstream;
        await TryWriteLogAsync(exchange, result);
    }

    private static string ReadRawTarget(HttpContext context)
    {
        HttpRequest request = context.Request;
        return context.Features.Get<IHttpRequestFeature>()?.RawTarget
            ?? (request.PathBase + request.Path + request.QueryString).ToString();
    }

    /// <summary>"/anthropic/v1/messages?x=1" resolves to route "anthropic" and upstream path "/v1/messages?x=1".</summary>
    private RouteMatch? ResolveRoute(string target)
    {
        if (!target.StartsWith('/'))
        {
            return null;
        }

        int end = target.IndexOfAny(RouteTerminators, 1);
        string name = end < 0 ? target[1..] : target[1..end];
        string upstreamPath = end < 0 ? "" : target[end..];
        return options.Routes.TryGetValue(name, out string? upstreamBase)
            ? new RouteMatch(name, upstreamBase, upstreamPath)
            : null;
    }

    private async Task RespondNoRouteAsync(HttpContext context, string target)
    {
        string knownPrefixes = string.Join(", ", options.Routes.Keys.Select(name => "/" + name));
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync(
            $"ailog: no route for '{target}'. Known prefixes: {knownPrefixes}\n",
            context.RequestAborted);
    }

    private static HttpRequestMessage CreateUpstreamRequest(Exchange exchange)
    {
        HttpRequest request = exchange.Context.Request;
        HttpRequestMessage upstreamRequest = new(new HttpMethod(request.Method), exchange.Route.UpstreamUrl);
        if (CanHaveBody(exchange.Context))
        {
            upstreamRequest.Content = new StreamContent(new CapturingStream(request.Body, exchange.RequestCapture), BufferSize);
        }

        CopyRequestHeaders(request.Headers, upstreamRequest);
        return upstreamRequest;
    }

    private static bool CanHaveBody(HttpContext context)
    {
        HttpRequest request = context.Request;
        bool canHaveBody = context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody ?? request.ContentLength > 0;
        return canHaveBody || request.ContentLength is not null;
    }

    private static void CopyRequestHeaders(IHeaderDictionary source, HttpRequestMessage destination)
    {
        HashSet<string> dropped = ConnectionListedHeaders(source.Connection);
        foreach (var (name, values) in source)
        {
            if (!IsForwardableRequestHeader(name, dropped))
            {
                continue;
            }

            if (!destination.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values))
            {
                destination.Content?.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
            }
        }
    }

    private static bool IsForwardableRequestHeader(string name, HashSet<string> dropped) =>
        !HopByHopHeaders.Contains(name)
        && !dropped.Contains(name)
        && !name.StartsWith(':')
        && !name.Equals("Host", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The proxy boundary: any upstream failure becomes a 502, or an aborted response once streaming has started,
    /// so this deliberately catches every exception type.
    /// </summary>
    private async Task<ForwardResult> ForwardAsync(Exchange exchange, HttpRequestMessage upstreamRequest)
    {
        CancellationToken aborted = exchange.Aborted;
        HttpResponseMessage? upstream = null;
        try
        {
            upstream = await client.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, aborted);
            await RelayResponseAsync(exchange, upstream);
            return new ForwardResult(upstream, ExchangeOutcome.Completed, Error: null);
        }
        catch (Exception) when (aborted.IsCancellationRequested)
        {
            return new ForwardResult(upstream, ExchangeOutcome.ClientAborted, ClientAbortedError);
        }
        catch (Exception ex)
        {
            await RespondUpstreamFailureAsync(exchange, ex);
            return new ForwardResult(upstream, ExchangeOutcome.UpstreamError, ex.Message);
        }
    }

    private static async Task RelayResponseAsync(Exchange exchange, HttpResponseMessage upstream)
    {
        HttpResponse response = exchange.Context.Response;
        response.StatusCode = (int)upstream.StatusCode;
        CopyResponseHeaders(upstream, response.Headers);
        exchange.Context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await response.StartAsync(exchange.Aborted);

        await using Stream upstreamBody = await upstream.Content.ReadAsStreamAsync(exchange.Aborted);
        await StreamBodyAsync(upstreamBody, exchange);
    }

    private static void CopyResponseHeaders(HttpResponseMessage source, IHeaderDictionary destination)
    {
        HashSet<string> dropped = ConnectionListedHeaders(string.Join(", ", source.Headers.Connection));
        foreach (var (name, values) in source.Headers.Concat(source.Content.Headers))
        {
            if (!HopByHopHeaders.Contains(name) && !dropped.Contains(name))
            {
                destination[name] = values.ToArray();
            }
        }
    }

    private static HashSet<string> ConnectionListedHeaders(string? connection) =>
        new(
            (connection ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Copies the upstream body to the harness chunk by chunk, flushing each so streaming stays live.</summary>
    private static async Task StreamBodyAsync(Stream upstreamBody, Exchange exchange)
    {
        Stream harnessBody = exchange.Context.Response.Body;
        CancellationToken aborted = exchange.Aborted;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await upstreamBody.ReadAsync(buffer, aborted)) > 0)
            {
                exchange.ResponseCapture.Write(buffer, 0, read);
                await harnessBody.WriteAsync(buffer.AsMemory(0, read), aborted);
                await harnessBody.FlushAsync(aborted);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task RespondUpstreamFailureAsync(Exchange exchange, Exception failure)
    {
        HttpContext context = exchange.Context;
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }

        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        context.Response.ContentType = PlainTextUtf8;

        // Not cancellable: the exchange must still be logged even if the harness has gone.
        await context.Response.WriteAsync(
            $"ailog: upstream request to {exchange.Route.UpstreamUrl} failed: {failure.Message}\n",
            CancellationToken.None);
    }

    /// <summary>A failed log write is reported on stderr and never fails the proxied exchange.</summary>
    private async Task TryWriteLogAsync(Exchange exchange, ForwardResult result)
    {
        try
        {
            // Not cancellable: aborted exchanges are logged too.
            await writer.WriteAsync(BuildLog(exchange, result), CancellationToken.None);
        }
        catch (Exception ex)
        {
            HttpRequest request = exchange.Context.Request;
            Console.Error.WriteLine($"ailog: failed to write log for {request.Method} {exchange.Target}: {ex.Message}");
        }
    }

    private ExchangeLog BuildLog(Exchange exchange, ForwardResult result)
    {
        return new ExchangeLog
        {
            Id = ExchangeLogWriter.BuildId(
                exchange.StartedAt, exchange.Sequence, exchange.Context.Request.Method, exchange.Route.UpstreamPath),
            Sequence = exchange.Sequence,
            StartedAt = exchange.StartedAt,
            CompletedAt = time.GetUtcNow(),
            DurationMs = Stopwatch.GetElapsedTime(exchange.StartedTicks).TotalMilliseconds,
            Route = exchange.Route.Name,
            UpstreamUrl = exchange.Route.UpstreamUrl,
            Outcome = result.Outcome,
            Error = result.Error,
            Request = BuildLoggedRequest(exchange),
            Response = result.Upstream is { } upstream ? BuildLoggedResponse(upstream, exchange.ResponseCapture) : null,
        };
    }

    private LoggedRequest BuildLoggedRequest(Exchange exchange)
    {
        HttpRequest request = exchange.Context.Request;
        return new LoggedRequest
        {
            Method = request.Method,
            Target = exchange.Target,
            Headers = CaptureHeaders(request.Headers),
            Body = BodyDecoder.Decode(
                new CapturedBody(CapturedBytes(exchange.RequestCapture), request.ContentType, request.Headers.ContentEncoding)),
        };
    }

    private LoggedResponse BuildLoggedResponse(HttpResponseMessage upstream, MemoryStream capture)
    {
        ICollection<string> contentEncoding = upstream.Content.Headers.ContentEncoding;
        return new LoggedResponse
        {
            StatusCode = (int)upstream.StatusCode,
            Headers = CaptureHeaders(upstream),
            Body = BodyDecoder.Decode(new CapturedBody(
                CapturedBytes(capture),
                upstream.Content.Headers.ContentType?.ToString(),
                contentEncoding.Count == 0 ? null : string.Join(", ", contentEncoding))),
        };
    }

    private static ReadOnlyMemory<byte> CapturedBytes(MemoryStream capture) =>
        capture.GetBuffer().AsMemory(0, (int)capture.Length);

    private Dictionary<string, string> CaptureHeaders(IHeaderDictionary headers)
    {
        Dictionary<string, string> captured = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in headers)
        {
            redactor.Add(captured, name, values);
        }

        return captured;
    }

    private Dictionary<string, string> CaptureHeaders(HttpResponseMessage response)
    {
        Dictionary<string, string> captured = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            redactor.Add(captured, name, values);
        }

        return captured;
    }

    /// <summary>The route prefix of the request target and the upstream it maps to.</summary>
    private sealed record RouteMatch(string Name, string UpstreamBase, string UpstreamPath)
    {
        public string UpstreamUrl { get; } = UpstreamBase.TrimEnd('/') + UpstreamPath;
    }

    /// <summary>How forwarding ended; <see cref="Upstream"/> is null when no response headers arrived.</summary>
    private sealed record ForwardResult(HttpResponseMessage? Upstream, ExchangeOutcome Outcome, string? Error);

    /// <summary>One proxied request in flight, with copies of both bodies as they stream through.</summary>
    private sealed class Exchange
    {
        public required HttpContext Context { get; init; }

        public required string Target { get; init; }

        public required RouteMatch Route { get; init; }

        public required long Sequence { get; init; }

        public required DateTimeOffset StartedAt { get; init; }

        public required long StartedTicks { get; init; }

        public MemoryStream RequestCapture { get; } = new();

        public MemoryStream ResponseCapture { get; } = new();

        public CancellationToken Aborted => Context.RequestAborted;
    }
}

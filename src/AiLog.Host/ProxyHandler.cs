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

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade",
    };

    public async Task HandleAsync(HttpContext context)
    {
        var startedAt = time.GetUtcNow();
        var startedTicks = Stopwatch.GetTimestamp();
        var request = context.Request;
        var target = context.Features.Get<IHttpRequestFeature>()?.RawTarget
            ?? (request.PathBase + request.Path + request.QueryString).ToString();

        if (!TryResolveRoute(target, out var route, out var upstreamBase, out var upstreamPath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync(
                $"ailog: no route for '{target}'. Known prefixes: {string.Join(", ", options.Routes.Keys.Select(k => "/" + k))}\n");
            return;
        }

        var sequence = writer.NextSequence();
        var upstreamUrl = upstreamBase.TrimEnd('/') + upstreamPath;
        var aborted = context.RequestAborted;

        var requestCapture = new MemoryStream();
        var responseCapture = new MemoryStream();
        HttpResponseMessage? upstream = null;
        var outcome = ExchangeOutcome.Completed;
        string? error = null;

        using var upstreamRequest = new HttpRequestMessage(new HttpMethod(request.Method), upstreamUrl);
        var canHaveBody = context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody ?? request.ContentLength > 0;
        if (canHaveBody || request.ContentLength is not null)
        {
            upstreamRequest.Content = new StreamContent(new CapturingStream(request.Body, requestCapture), BufferSize);
        }

        CopyRequestHeaders(request.Headers, upstreamRequest);

        try
        {
            upstream = await client.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, aborted);

            var response = context.Response;
            response.StatusCode = (int)upstream.StatusCode;
            CopyResponseHeaders(upstream, response.Headers);
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await response.StartAsync(aborted);

            await using var upstreamBody = await upstream.Content.ReadAsStreamAsync(aborted);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                int read;
                while ((read = await upstreamBody.ReadAsync(buffer, aborted)) > 0)
                {
                    responseCapture.Write(buffer, 0, read);
                    await response.Body.WriteAsync(buffer.AsMemory(0, read), aborted);
                    await response.Body.FlushAsync(aborted);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception) when (aborted.IsCancellationRequested)
        {
            outcome = ExchangeOutcome.ClientAborted;
            error = "The harness disconnected before the response completed.";
        }
        catch (Exception ex)
        {
            outcome = ExchangeOutcome.UpstreamError;
            error = ex.Message;
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync($"ailog: upstream request to {upstreamUrl} failed: {ex.Message}\n");
            }
            else
            {
                context.Abort();
            }
        }

        try
        {
            var entry = new ExchangeLog
            {
                Id = ExchangeLogWriter.BuildId(startedAt, sequence, request.Method, upstreamPath),
                Sequence = sequence,
                StartedAt = startedAt,
                CompletedAt = time.GetUtcNow(),
                DurationMs = Stopwatch.GetElapsedTime(startedTicks).TotalMilliseconds,
                Route = route,
                UpstreamUrl = upstreamUrl,
                Outcome = outcome,
                Error = error,
                Request = new LoggedRequest
                {
                    Method = request.Method,
                    Target = target,
                    Headers = CaptureHeaders(request.Headers),
                    Body = BodyDecoder.Decode(requestCapture.GetBuffer().AsSpan(0, (int)requestCapture.Length),
                        request.ContentType, request.Headers.ContentEncoding),
                },
                Response = upstream is null ? null : new LoggedResponse
                {
                    StatusCode = (int)upstream.StatusCode,
                    Headers = CaptureHeaders(upstream),
                    Body = BodyDecoder.Decode(responseCapture.GetBuffer().AsSpan(0, (int)responseCapture.Length),
                        upstream.Content.Headers.ContentType?.ToString(),
                        upstream.Content.Headers.ContentEncoding.Count == 0 ? null : string.Join(", ", upstream.Content.Headers.ContentEncoding)),
                },
            };

            await writer.WriteAsync(entry);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ailog: failed to write log for {request.Method} {target}: {ex.Message}");
        }
        finally
        {
            upstream?.Dispose();
        }
    }

    /// <summary>"/anthropic/v1/messages?x=1" resolves to route "anthropic" and upstream path "/v1/messages?x=1".</summary>
    private bool TryResolveRoute(string target, out string route, out string upstreamBase, out string upstreamPath)
    {
        route = upstreamBase = upstreamPath = "";
        if (!target.StartsWith('/'))
        {
            return false;
        }

        var end = target.IndexOfAny(['/', '?'], 1);
        route = end < 0 ? target[1..] : target[1..end];
        upstreamPath = end < 0 ? "" : target[end..];
        return options.Routes.TryGetValue(route, out upstreamBase!);
    }

    private static void CopyRequestHeaders(IHeaderDictionary source, HttpRequestMessage destination)
    {
        var dropped = ConnectionListedHeaders(source.Connection);
        foreach (var (name, values) in source)
        {
            if (HopByHopHeaders.Contains(name) || dropped.Contains(name) || name.StartsWith(':')
                || name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!destination.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values))
            {
                destination.Content?.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
            }
        }
    }

    private static void CopyResponseHeaders(HttpResponseMessage source, IHeaderDictionary destination)
    {
        var dropped = ConnectionListedHeaders(string.Join(", ", source.Headers.Connection));
        foreach (var (name, values) in source.Headers.Concat(source.Content.Headers))
        {
            if (!HopByHopHeaders.Contains(name) && !dropped.Contains(name))
            {
                destination[name] = values.ToArray();
            }
        }
    }

    private static HashSet<string> ConnectionListedHeaders(string? connection) =>
        new((connection ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, string> CaptureHeaders(IHeaderDictionary headers)
    {
        var captured = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in headers)
        {
            redactor.Add(captured, name, values);
        }

        return captured;
    }

    private Dictionary<string, string> CaptureHeaders(HttpResponseMessage response)
    {
        var captured = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            redactor.Add(captured, name, values);
        }

        return captured;
    }
}

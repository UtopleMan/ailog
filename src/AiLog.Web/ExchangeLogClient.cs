using System.Net;
using System.Text.Json;
using AiLog.Contracts;

namespace AiLog.Web;

/// <summary>How loading one exchange log ended.</summary>
public enum LoadOutcome
{
    /// <summary>The log was read and parsed.</summary>
    Loaded,

    /// <summary>No log file has that id.</summary>
    NotFound,

    /// <summary>The request or parsing failed.</summary>
    Failed,
}

/// <summary>The result of loading one exchange log.</summary>
/// <param name="Outcome">How the load ended.</param>
/// <param name="Log">The log, when <see cref="LoadOutcome.Loaded"/>.</param>
/// <param name="Error">The failure message, when <see cref="LoadOutcome.Failed"/>.</param>
public sealed record LoadResult(LoadOutcome Outcome, ExchangeLog? Log = null, string? Error = null);

/// <summary>Reads full exchange logs from the server.</summary>
public sealed class ExchangeLogClient(HttpClient http)
{
    /// <summary>Loads the log with the given id; failures are reported in the result, not thrown.</summary>
    public async Task<LoadResult> LoadAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            return await FetchAsync(id, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            return new LoadResult(LoadOutcome.Failed, Error: ex.Message);
        }
    }

    private async Task<LoadResult> FetchAsync(string id, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync($"api/logs/{Uri.EscapeDataString(id)}", cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return new LoadResult(LoadOutcome.NotFound);
        }

        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        ExchangeLog log = await JsonSerializer.DeserializeAsync(stream, AiLogJsonContext.Default.ExchangeLog, cancellationToken)
            ?? throw new InvalidOperationException("empty log file");
        return new LoadResult(LoadOutcome.Loaded, log);
    }
}

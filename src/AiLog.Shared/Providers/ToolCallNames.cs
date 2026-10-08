namespace AiLog.Shared.Providers;

/// <summary>Remembers the tool behind each call id, so a result can be labelled with the tool that produced it.</summary>
internal sealed class ToolCallNames
{
    private readonly Dictionary<string, string> names = new(StringComparer.Ordinal);

    public void Remember(string? callId, string toolName)
    {
        if (callId is not null)
        {
            names[callId] = toolName;
        }
    }

    public string? Find(string? callId) =>
        callId is not null && names.TryGetValue(callId, out string? toolName) ? toolName : null;
}

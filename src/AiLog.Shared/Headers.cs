namespace AiLog.Shared;

/// <summary>Lookups on logged header dictionaries.</summary>
public static class Headers
{
    /// <summary>Deserialised header dictionaries are case-sensitive, so match the name by hand.</summary>
    public static string? Get(Dictionary<string, string> headers, string name)
    {
        foreach ((string key, string value) in headers)
        {
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }
}

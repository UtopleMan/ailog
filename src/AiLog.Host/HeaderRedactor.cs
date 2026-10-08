namespace AiLog.Host;

/// <summary>Builds the header dictionaries written to log files, masking secrets. Never used for forwarding.</summary>
internal sealed class HeaderRedactor(IEnumerable<string> redactedNames)
{
    private readonly HashSet<string> _redacted = new(redactedNames, StringComparer.OrdinalIgnoreCase);

    public void Add(Dictionary<string, string> target, string name, IEnumerable<string?> values)
    {
        var redact = _redacted.Contains(name);
        var joined = string.Join(", ", values.Select(v => redact ? Mask(v ?? "") : v));
        target[name] = target.TryGetValue(name, out var existing) ? existing + ", " + joined : joined;
    }

    /// <summary>"Bearer sk-ant-api03-abcdef...wxyz" becomes "Bearer sk-ant…wxyz".</summary>
    internal static string Mask(string value)
    {
        var space = value.IndexOf(' ');
        if (space > 0 && space < value.Length - 1 && value.AsSpan(0, space).ContainsOnlyLetters())
        {
            return value[..space] + " " + Mask(value[(space + 1)..]);
        }

        return value.Length > 12 ? $"{value[..6]}…{value[^4..]}" : "…";
    }
}

file static class SpanExtensions
{
    public static bool ContainsOnlyLetters(this ReadOnlySpan<char> span)
    {
        foreach (var c in span)
        {
            if (!char.IsAsciiLetter(c))
            {
                return false;
            }
        }

        return true;
    }
}

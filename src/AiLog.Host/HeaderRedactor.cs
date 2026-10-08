namespace AiLog.Host;

/// <summary>Builds the header dictionaries written to log files, masking secrets. Never used for forwarding.</summary>
internal sealed class HeaderRedactor(IEnumerable<string> redactedNames)
{
    private const string ValueSeparator = ", ";
    private const string Ellipsis = "…";
    private const int MaxFullyMaskedLength = 12;
    private const int RevealedPrefixLength = 6;
    private const int RevealedSuffixLength = 4;

    private readonly HashSet<string> redacted = new(redactedNames, StringComparer.OrdinalIgnoreCase);

    /// <summary>Copies the headers into a log-ready dictionary, joining repeated names and masking redacted values.</summary>
    public Dictionary<string, string> Capture(IEnumerable<(string Name, IEnumerable<string?> Values)> headers)
    {
        Dictionary<string, string> captured = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, IEnumerable<string?> values) in headers)
        {
            string joined = JoinValues(name, values);
            captured[name] = captured.TryGetValue(name, out string? existing) ? existing + ValueSeparator + joined : joined;
        }

        return captured;
    }

    private string JoinValues(string name, IEnumerable<string?> values)
    {
        bool isRedacted = redacted.Contains(name);
        return string.Join(ValueSeparator, values.Select(value => isRedacted ? Mask(value ?? "") : value));
    }

    /// <summary>"Bearer sk-ant-api03-abcdef...wxyz" becomes "Bearer sk-ant…wxyz".</summary>
    internal static string Mask(string value)
    {
        int space = value.IndexOf(' ');
        if (space > 0 && space < value.Length - 1 && value.AsSpan(0, space).ContainsOnlyAsciiLetters())
        {
            return value[..space] + " " + Mask(value[(space + 1)..]);
        }

        if (value.Length <= MaxFullyMaskedLength)
        {
            return Ellipsis;
        }

        return $"{value[..RevealedPrefixLength]}{Ellipsis}{value[^RevealedSuffixLength..]}";
    }
}

file static class SpanExtensions
{
    extension(ReadOnlySpan<char> span)
    {
        public bool ContainsOnlyAsciiLetters()
        {
            foreach (char c in span)
            {
                if (!char.IsAsciiLetter(c))
                {
                    return false;
                }
            }

            return true;
        }
    }
}

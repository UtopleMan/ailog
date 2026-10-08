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

    /// <summary>Adds the header to <paramref name="target"/>, appending to any values already captured under that name.</summary>
    public void Add(Dictionary<string, string> target, string name, IEnumerable<string?> values)
    {
        bool isRedacted = redacted.Contains(name);
        string joined = string.Join(ValueSeparator, values.Select(value => isRedacted ? Mask(value ?? "") : value));
        target[name] = target.TryGetValue(name, out string? existing) ? existing + ValueSeparator + joined : joined;
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

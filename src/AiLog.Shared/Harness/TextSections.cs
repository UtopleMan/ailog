using System.Text;

namespace AiLog.Shared.Harness;

public sealed record TextSection(string FirstLine, string Text);

/// <summary>Splits prose into sections at boundary lines, never inside fenced code blocks.</summary>
public static class TextSections
{
    /// <param name="isBoundary">Called with a trimmed line; true starts a new section there.</param>
    /// <param name="needsBlankLineBefore">Only lines that start a paragraph can be boundaries.</param>
    public static List<TextSection> Split(string text, Func<string, bool> isBoundary, bool needsBlankLineBefore)
    {
        var sections = new List<TextSection>();
        var current = new StringBuilder();
        string? firstLine = null;
        var inFence = false;
        var previousBlank = true;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();
            if (!inFence && (previousBlank || !needsBlankLineBefore) && trimmed.Length > 0 && isBoundary(trimmed) && current.Length > 0)
            {
                Flush();
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }

            if (firstLine is null && trimmed.Length > 0)
            {
                firstLine = trimmed;
            }

            current.Append(line).Append('\n');
            previousBlank = trimmed.Length == 0;
        }

        Flush();
        return sections;

        void Flush()
        {
            var sectionText = current.ToString().Trim('\n');
            if (sectionText.Trim().Length > 0)
            {
                sections.Add(new TextSection(firstLine ?? "", sectionText));
            }

            current.Clear();
            firstLine = null;
        }
    }

    public static bool IsHeading(string line) => line.StartsWith("# ", StringComparison.Ordinal);

    public static string HeadingText(string line) => line.TrimStart('#').Trim();
}

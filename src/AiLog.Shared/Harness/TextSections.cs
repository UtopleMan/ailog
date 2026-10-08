using System.Text;

namespace AiLog.Shared.Harness;

/// <summary>A run of lines starting at a boundary; <see cref="FirstLine"/> is its first non-blank line, trimmed.</summary>
public sealed record TextSection(string FirstLine, string Text);

/// <summary>Splits prose into sections at boundary lines, never inside fenced code blocks.</summary>
public static class TextSections
{
    private const string CodeFence = "```";
    private const string HeadingMarker = "# ";

    /// <summary>Splits <paramref name="text"/> before every boundary line.</summary>
    /// <param name="text">The prose to split.</param>
    /// <param name="isBoundary">Called with a trimmed line; true starts a new section there.</param>
    /// <param name="needsBlankLineBefore">Only lines that start a paragraph can be boundaries.</param>
    public static List<TextSection> Split(string text, Func<string, bool> isBoundary, bool needsBlankLineBefore)
    {
        var splitter = new Splitter();
        bool isFenced = false;
        bool previousBlank = true;
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            string trimmed = line.Trim();
            bool canStartSection = !isFenced && (previousBlank || !needsBlankLineBefore) && trimmed.Length > 0;
            if (canStartSection && isBoundary(trimmed))
            {
                splitter.StartSection();
            }

            if (trimmed.StartsWith(CodeFence, StringComparison.Ordinal))
            {
                isFenced = !isFenced;
            }

            splitter.Append(line, trimmed);
            previousBlank = trimmed.Length == 0;
        }

        return splitter.Finish();
    }

    /// <summary>True for a top-level markdown heading.</summary>
    public static bool IsHeading(string line) => line.StartsWith(HeadingMarker, StringComparison.Ordinal);

    /// <summary>The heading without its leading hashes.</summary>
    public static string HeadingText(string line) => line.TrimStart('#').Trim();

    /// <summary>Accumulates lines into the current section and closes sections on request.</summary>
    private sealed class Splitter
    {
        private readonly List<TextSection> sections = [];
        private readonly StringBuilder current = new();
        private string? firstLine;

        public void Append(string line, string trimmed)
        {
            if (firstLine is null && trimmed.Length > 0)
            {
                firstLine = trimmed;
            }

            current.Append(line).Append('\n');
        }

        public void StartSection()
        {
            if (current.Length > 0)
            {
                Flush();
            }
        }

        public List<TextSection> Finish()
        {
            Flush();
            return sections;
        }

        /// <summary>Closes the current section, dropping it when it holds only whitespace.</summary>
        private void Flush()
        {
            string sectionText = current.ToString().Trim('\n');
            if (sectionText.Trim().Length > 0)
            {
                sections.Add(new TextSection(firstLine ?? "", sectionText));
            }

            current.Clear();
            firstLine = null;
        }
    }
}

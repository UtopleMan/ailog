using System.Text.Json;

namespace AiLog.Shared.Context;

public enum SegmentKind
{
    /// <summary>A structural container: System, Tools, Messages, Output or a classifier grouping.</summary>
    Group,
    Message,
    Text,
    Thinking,
    ToolDefinition,
    ToolCall,
    ToolResult,
    Image,
    Document,
    Other,
}

/// <summary>
/// A provider-neutral piece of a request or response. Leaves carry content and a size weight; parents sum their
/// children. Produced by an <see cref="Providers.IProviderAdapter"/>, refined by an <see cref="Harness.IHarnessClassifier"/>.
/// </summary>
public sealed class ContextSegment
{
    public required SegmentKind Kind { get; init; }

    /// <summary>Shown in the conversation view, e.g. "#3 user", "Bash", "# Environment".</summary>
    public required string Label { get; set; }

    /// <summary>user, assistant, system, developer, tool. Set on messages and inherited by their blocks.</summary>
    public string? Role { get; set; }

    /// <summary>Top level of the token breakdown, e.g. "Built-in tools", "Skills", "Tool results".</summary>
    public string? Category { get; set; }

    /// <summary>Second level of the breakdown; leaves with the same item are summed. Defaults to <see cref="Label"/>.</summary>
    public string? Item { get; set; }

    /// <summary>Prose, rendered as markdown.</summary>
    public string? Text { get; set; }

    /// <summary>Indented JSON for structured content (tool definitions, tool inputs, unknown blocks).</summary>
    public string? Json { get; set; }

    /// <summary>Characters that stand for this leaf's tokens; compact JSON length for structured content.</summary>
    public long Weight { get; set; }

    /// <summary>Set for images and documents, whose cost is not proportional to their encoded size.</summary>
    public long? FixedTokens { get; set; }

    public string? Note { get; set; }

    /// <summary>The block carries a prompt-cache breakpoint (Anthropic <c>cache_control</c>).</summary>
    public bool CacheBreakpoint { get; set; }

    /// <summary>Part of the prefix up to the last cache breakpoint.</summary>
    public bool Cached { get; set; }

    /// <summary>Estimated tokens; for parents the sum of their children.</summary>
    public long Tokens { get; set; }

    /// <summary>Unique within the analysis and stable for the same file; ancestors' ids are its '-'-separated prefixes.</summary>
    public string Id { get; set; } = "";

    public List<ContextSegment> Children { get; set; } = [];

    public bool IsLeaf => Children.Count == 0;

    public string BreakdownItem => Item ?? Label;

    public static ContextSegment Group(string label, string? category = null) =>
        new() { Kind = SegmentKind.Group, Label = label, Category = category };

    public static ContextSegment FromText(SegmentKind kind, string label, string? text) =>
        new() { Kind = kind, Label = label, Text = text ?? "", Weight = text?.Length ?? 0 };

    public static ContextSegment FromJson(SegmentKind kind, string label, JsonElement json)
    {
        var compact = AiLog.Shared.Json.Format(json, indented: false);
        return new() { Kind = kind, Label = label, Json = AiLog.Shared.Json.Format(json), Weight = compact.Length };
    }

    public IEnumerable<ContextSegment> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var descendant in child.Descendants())
            {
                yield return descendant;
            }
        }
    }

    public IEnumerable<ContextSegment> Leaves() => IsLeaf ? [this] : Descendants().Where(d => d.IsLeaf);

    /// <summary>The first line of content, for collapsed rows.</summary>
    public string Preview(int max = 160)
    {
        var source = Text ?? Json ?? Leaves().Select(l => l.Text ?? l.Json).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "";
        // Skips lines like "{" and "<system-reminder>" so JSON and tagged text show something meaningful.
        var line = source.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.Any(char.IsLetterOrDigit) && !(l.StartsWith('<') && l.EndsWith('>'))) ?? "";
        return line.Length <= max ? line : line[..max] + "…";
    }
}

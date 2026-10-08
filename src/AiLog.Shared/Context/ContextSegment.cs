using System.Text.Json;

namespace AiLog.Shared.Context;

/// <summary>What a <see cref="ContextSegment"/> holds.</summary>
public enum SegmentKind
{
    /// <summary>A structural container: System, Tools, Messages, Output or a classifier grouping.</summary>
    Group,

    /// <summary>One conversation turn; its children are the blocks.</summary>
    Message,

    /// <summary>Prose.</summary>
    Text,

    /// <summary>Reasoning, visible or encrypted.</summary>
    Thinking,

    /// <summary>A tool's schema as offered to the model.</summary>
    ToolDefinition,

    /// <summary>A tool invocation by the model.</summary>
    ToolCall,

    /// <summary>The answer to a tool call.</summary>
    ToolResult,

    /// <summary>An image attachment.</summary>
    Image,

    /// <summary>A document attachment.</summary>
    Document,

    /// <summary>A block of a type the adapter does not know.</summary>
    Other,
}

/// <summary>
/// A provider-neutral piece of a request or response. Leaves carry content and a size weight; parents sum their
/// children. Produced by an <see cref="Providers.IProviderAdapter"/>, refined by an <see cref="Harness.IHarnessClassifier"/>.
/// </summary>
public sealed class ContextSegment
{
    private const int DefaultPreviewLength = 160;

    /// <summary>What the segment holds.</summary>
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

    /// <summary>A short remark shown next to the segment, e.g. how its size was estimated.</summary>
    public string? Note { get; set; }

    /// <summary>The block carries a prompt-cache breakpoint (Anthropic <c>cache_control</c>).</summary>
    public bool CacheBreakpoint { get; set; }

    /// <summary>Part of the prefix up to the last cache breakpoint.</summary>
    public bool Cached { get; set; }

    /// <summary>Estimated tokens; for parents the sum of their children.</summary>
    public long Tokens { get; set; }

    /// <summary>Unique within the analysis and stable for the same file; ancestors' ids are its '-'-separated prefixes.</summary>
    public string Id { get; set; } = "";

    /// <summary>The nested segments; empty for leaves.</summary>
    public List<ContextSegment> Children { get; set; } = [];

    /// <summary>True when the segment has no children.</summary>
    public bool IsLeaf => Children.Count == 0;

    /// <summary>The item this segment is summed under in the breakdown.</summary>
    public string BreakdownItem => Item ?? Label;

    /// <summary>A structural container.</summary>
    public static ContextSegment Group(string label, string? category = null) =>
        new() { Kind = SegmentKind.Group, Label = label, Category = category };

    /// <summary>A leaf holding prose, weighted by its length.</summary>
    public static ContextSegment FromText(SegmentKind kind, string label, string? text) =>
        new() { Kind = kind, Label = label, Text = text ?? "", Weight = text?.Length ?? 0 };

    /// <summary>A leaf holding structured content, weighted by its compact JSON length.</summary>
    public static ContextSegment FromJson(SegmentKind kind, string label, JsonElement json)
    {
        string compact = AiLog.Shared.Json.Format(json, indented: false);
        return new() { Kind = kind, Label = label, Json = AiLog.Shared.Json.Format(json), Weight = compact.Length };
    }

    /// <summary>Every segment below this one, depth first.</summary>
    public IEnumerable<ContextSegment> Descendants()
    {
        foreach (ContextSegment child in Children)
        {
            yield return child;
            foreach (ContextSegment descendant in child.Descendants())
            {
                yield return descendant;
            }
        }
    }

    /// <summary>The leaves below this segment, or the segment itself when it is a leaf.</summary>
    public IEnumerable<ContextSegment> Leaves() => IsLeaf ? [this] : Descendants().Where(d => d.IsLeaf);

    /// <summary>The first line of content, for collapsed rows.</summary>
    public string Preview(int max = DefaultPreviewLength)
    {
        string source = Text ?? Json ?? Leaves().Select(l => l.Text ?? l.Json).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "";
        string line = source.Split('\n').Select(l => l.Trim()).FirstOrDefault(IsMeaningful) ?? "";
        return line.Shorten(max);
    }

    /// <summary>Skips lines like "{" and "&lt;system-reminder&gt;" so JSON and tagged text show something meaningful.</summary>
    private static bool IsMeaningful(string line) =>
        line.Any(char.IsLetterOrDigit) && !(line.StartsWith('<') && line.EndsWith('>'));
}

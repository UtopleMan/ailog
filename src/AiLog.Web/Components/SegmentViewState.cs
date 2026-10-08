using AiLog.Shared.Context;
using Microsoft.AspNetCore.Components;

namespace AiLog.Web.Components;

/// <summary>Expansion and display state of one exchange's segment trees, shared by all segment rows.</summary>
public sealed class SegmentViewState
{
    private readonly Dictionary<string, MarkupString> _rendered = new(StringComparer.Ordinal);

    public HashSet<string> Expanded { get; } = new(StringComparer.Ordinal);

    /// <summary>Text segments shown as markdown source instead of rendered.</summary>
    public HashSet<string> ShowSource { get; } = new(StringComparer.Ordinal);

    /// <summary>The segment last jumped to from the breakdown.</summary>
    public string? Highlighted { get; set; }

    public bool IsExpanded(ContextSegment segment) => Expanded.Contains(segment.Id);

    public void Toggle(ContextSegment segment)
    {
        if (!Expanded.Remove(segment.Id))
        {
            Expanded.Add(segment.Id);
        }
    }

    /// <summary>Expands a segment and all its ancestors (whose ids are its '-'-separated prefixes).</summary>
    public void Reveal(ContextSegment segment)
    {
        var parts = segment.Id.Split('-');
        for (var i = 1; i <= parts.Length; i++)
        {
            Expanded.Add(string.Join('-', parts[..i]));
        }

        Highlighted = segment.Id;
    }

    public void ExpandAll(ContextSegment segment)
    {
        Expanded.Add(segment.Id);
        foreach (var descendant in segment.Descendants())
        {
            Expanded.Add(descendant.Id);
        }
    }

    /// <summary>Markdown is rendered once per segment, and only when it is first shown.</summary>
    public MarkupString Rendered(ContextSegment segment)
    {
        if (!_rendered.TryGetValue(segment.Id, out var html))
        {
            _rendered[segment.Id] = html = Markdown.Render(segment.Text ?? "");
        }

        return html;
    }
}

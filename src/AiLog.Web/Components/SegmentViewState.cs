using AiLog.Shared;
using AiLog.Shared.Context;
using Microsoft.AspNetCore.Components;

namespace AiLog.Web.Components;

/// <summary>Expansion and display state of one exchange's segment trees, shared by all segment rows.</summary>
public sealed class SegmentViewState
{
    private const string UserRole = "user";

    private readonly Dictionary<string, MarkupString> rendered = new(StringComparer.Ordinal);

    /// <summary>Ids of the expanded segments.</summary>
    public HashSet<string> Expanded { get; } = new(StringComparer.Ordinal);

    /// <summary>Text segments shown as markdown source instead of rendered.</summary>
    public HashSet<string> ShowSource { get; } = new(StringComparer.Ordinal);

    /// <summary>The segment last jumped to from the breakdown.</summary>
    public string? Highlighted { get; set; }

    /// <summary>
    /// The initial view of an exchange: top-level groups open, the last user message and the model's output fully
    /// open, everything else collapsed.
    /// </summary>
    public static SegmentViewState For(ExchangeAnalysis analysis)
    {
        SegmentViewState state = new();
        if (analysis.Request is { } request)
        {
            state.ExpandTopLevel(request);
        }

        if (analysis.Response is { } output)
        {
            state.ExpandAll(output);
        }

        return state;
    }

    /// <summary>Whether the segment's children or content are shown.</summary>
    public bool IsExpanded(ContextSegment segment) => Expanded.Contains(segment.Id);

    /// <summary>Expands a collapsed segment or collapses an expanded one.</summary>
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
        string[] parts = segment.Id.Split('-');
        for (int i = 1; i <= parts.Length; i++)
        {
            Expanded.Add(string.Join('-', parts[..i]));
        }

        Highlighted = segment.Id;
    }

    /// <summary>Expands a segment and every segment below it.</summary>
    public void ExpandAll(ContextSegment segment)
    {
        Expanded.Add(segment.Id);
        foreach (ContextSegment descendant in segment.Descendants())
        {
            Expanded.Add(descendant.Id);
        }
    }

    /// <summary>Markdown is rendered once per segment, and only when it is first shown.</summary>
    public MarkupString Rendered(ContextSegment segment)
    {
        if (!rendered.TryGetValue(segment.Id, out MarkupString html))
        {
            html = Markdown.Render(segment.Text ?? "");
            rendered[segment.Id] = html;
        }

        return html;
    }

    private void ExpandTopLevel(ContextSegment request)
    {
        foreach (ContextSegment top in request.Children)
        {
            Expanded.Add(top.Id);
        }

        ContextSegment? lastUser = request.Children.LastOrDefault()?.Children
            .LastOrDefault(m => m.Kind == SegmentKind.Message && m.Role == UserRole);
        if (lastUser is not null)
        {
            ExpandAll(lastUser);
        }
    }
}

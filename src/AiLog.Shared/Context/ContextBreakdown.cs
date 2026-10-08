namespace AiLog.Shared.Context;

/// <summary>The second breakdown level: the leaves sharing one item within a category.</summary>
public sealed record BreakdownItem(string Label, long Tokens, IReadOnlyList<ContextSegment> Segments);

/// <summary>The first breakdown level: one category with its items, largest first.</summary>
public sealed record BreakdownCategory(string Name, long Tokens, IReadOnlyList<BreakdownItem> Items);

/// <summary>Groups the leaves of a segment tree by category, then by item, largest first.</summary>
public static class ContextBreakdown
{
    /// <summary>Category of leaves nothing classified.</summary>
    public const string Uncategorized = "Other";

    /// <summary>The breakdown of a tree; empty when there is no tree.</summary>
    public static List<BreakdownCategory> Build(ContextSegment? root)
    {
        if (root is null)
        {
            return [];
        }

        return root.Leaves()
            .Where(l => l.Kind is not (SegmentKind.Group or SegmentKind.Message))
            .GroupBy(l => l.Category ?? Uncategorized)
            .Select(ToCategory)
            .OrderByDescending(c => c.Tokens)
            .ToList();
    }

    private static BreakdownCategory ToCategory(IGrouping<string, ContextSegment> leaves)
    {
        List<BreakdownItem> items = leaves
            .GroupBy(l => l.BreakdownItem)
            .Select(ToItem)
            .OrderByDescending(i => i.Tokens)
            .ToList();
        return new BreakdownCategory(leaves.Key, leaves.Sum(l => l.Tokens), items);
    }

    private static BreakdownItem ToItem(IGrouping<string, ContextSegment> leaves) =>
        new(leaves.Key, leaves.Sum(l => l.Tokens), leaves.OrderByDescending(l => l.Tokens).ToList());

    /// <summary>
    /// Marks everything up to and including the last cache breakpoint, walking in the provider's cache order
    /// (for Anthropic: tools, then system, then messages).
    /// </summary>
    public static void MarkCachedPrefix(IEnumerable<ContextSegment> cacheOrder)
    {
        List<ContextSegment> flat = Flatten(cacheOrder);
        int lastBreakpoint = flat.FindLastIndex(s => s.CacheBreakpoint);
        if (lastBreakpoint < 0)
        {
            return;
        }

        int end = lastBreakpoint + flat[lastBreakpoint].Descendants().Count();
        for (int i = 0; i <= end; i++)
        {
            flat[i].Cached = true;
        }
    }

    private static List<ContextSegment> Flatten(IEnumerable<ContextSegment> roots)
    {
        var flat = new List<ContextSegment>();
        foreach (ContextSegment root in roots)
        {
            flat.Add(root);
            flat.AddRange(root.Descendants());
        }

        return flat;
    }

    /// <summary>Gives every segment an id made of its path, so ancestors are found by prefix.</summary>
    public static void AssignIds(ContextSegment root, string prefix)
    {
        root.Id = prefix;
        for (int i = 0; i < root.Children.Count; i++)
        {
            AssignIds(root.Children[i], $"{prefix}-{i}");
        }
    }
}

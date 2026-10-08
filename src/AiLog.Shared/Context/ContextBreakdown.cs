namespace AiLog.Shared.Context;

public sealed record BreakdownItem(string Label, long Tokens, IReadOnlyList<ContextSegment> Segments);

public sealed record BreakdownCategory(string Name, long Tokens, IReadOnlyList<BreakdownItem> Items);

/// <summary>Groups the leaves of a segment tree by category, then by item, largest first.</summary>
public static class ContextBreakdown
{
    public const string Uncategorized = "Other";

    public static List<BreakdownCategory> Build(ContextSegment? root)
    {
        if (root is null)
        {
            return [];
        }

        return root.Leaves()
            .Where(l => l.Kind is not (SegmentKind.Group or SegmentKind.Message))
            .GroupBy(l => l.Category ?? Uncategorized)
            .Select(category => new BreakdownCategory(
                category.Key,
                category.Sum(l => l.Tokens),
                category.GroupBy(l => l.BreakdownItem)
                    .Select(item => new BreakdownItem(item.Key, item.Sum(l => l.Tokens), item.OrderByDescending(l => l.Tokens).ToList()))
                    .OrderByDescending(i => i.Tokens)
                    .ToList()))
            .OrderByDescending(c => c.Tokens)
            .ToList();
    }

    /// <summary>
    /// Marks everything up to and including the last cache breakpoint, walking in the provider's cache order
    /// (for Anthropic: tools, then system, then messages).
    /// </summary>
    public static void MarkCachedPrefix(IEnumerable<ContextSegment> cacheOrder)
    {
        var flat = new List<ContextSegment>();
        foreach (var top in cacheOrder)
        {
            flat.Add(top);
            flat.AddRange(top.Descendants());
        }

        var last = flat.FindLastIndex(s => s.CacheBreakpoint);
        if (last < 0)
        {
            return;
        }

        var end = last + flat[last].Descendants().Count();
        for (var i = 0; i <= end; i++)
        {
            flat[i].Cached = true;
        }
    }

    /// <summary>Gives every segment an id made of its path, so ancestors are found by prefix.</summary>
    public static void AssignIds(ContextSegment root, string prefix)
    {
        root.Id = prefix;
        for (var i = 0; i < root.Children.Count; i++)
        {
            AssignIds(root.Children[i], $"{prefix}-{i}");
        }
    }
}

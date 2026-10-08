namespace AiLog.Shared.Context;

/// <summary>
/// Spreads a known token total over a segment tree. Text-like leaves share what is left after the fixed media
/// estimates, in proportion to their character weight, so the parts always add up to the provider's real count.
/// Without a real count every leaf falls back to characters / 4.
/// </summary>
public static class TokenEstimator
{
    public const double CharsPerToken = 4;

    /// <returns>True when the tree was calibrated against <paramref name="actualTotal"/>.</returns>
    public static bool Estimate(ContextSegment root, long? actualTotal)
    {
        var leaves = root.Leaves().ToList();
        var raw = leaves.Select(l => l.FixedTokens is { } fixedTokens ? (double)fixedTokens : l.Weight / CharsPerToken).ToArray();

        var calibrated = actualTotal is > 0 && raw.Sum() > 0;
        double[] shares;
        if (!calibrated)
        {
            shares = raw;
        }
        else
        {
            var total = (double)actualTotal!.Value;
            var fixedSum = leaves.Where(l => l.FixedTokens is not null).Sum(l => (double)l.FixedTokens!.Value);
            var textWeight = leaves.Where(l => l.FixedTokens is null).Sum(l => (double)l.Weight);
            if (fixedSum <= total && textWeight > 0)
            {
                var perChar = (total - fixedSum) / textWeight;
                shares = leaves.Select(l => l.FixedTokens is { } f ? f : l.Weight * perChar).ToArray();
            }
            else
            {
                // Media alone exceeds the real count (or there is no text): scale everything down together.
                var factor = total / raw.Sum();
                shares = raw.Select(r => r * factor).ToArray();
            }
        }

        var rounded = calibrated ? LargestRemainder(shares, actualTotal!.Value) : shares.Select(s => (long)Math.Ceiling(s)).ToArray();
        for (var i = 0; i < leaves.Count; i++)
        {
            leaves[i].Tokens = rounded[i];
        }

        SumParents(root);
        return calibrated;
    }

    private static long SumParents(ContextSegment segment)
    {
        if (!segment.IsLeaf)
        {
            segment.Tokens = segment.Children.Sum(SumParents);
        }

        return segment.Tokens;
    }

    /// <summary>Rounds so the integers add up exactly to <paramref name="total"/>.</summary>
    private static long[] LargestRemainder(double[] shares, long total)
    {
        var result = shares.Select(s => (long)Math.Floor(s)).ToArray();
        var missing = total - result.Sum();
        foreach (var i in Enumerable.Range(0, shares.Length).OrderByDescending(i => shares[i] - Math.Floor(shares[i])).Take((int)Math.Max(0, missing)))
        {
            result[i]++;
        }

        return result;
    }
}

namespace AiLog.Shared.Context;

/// <summary>
/// Spreads a known token total over a segment tree. Text-like leaves share what is left after the fixed media
/// estimates, in proportion to their character weight, so the parts always add up to the provider's real count.
/// Without a real count every leaf falls back to characters / 4.
/// </summary>
public static class TokenEstimator
{
    /// <summary>Characters per token assumed when there is no real count to calibrate against.</summary>
    public const double CharsPerToken = 4;

    /// <summary>Sets <see cref="ContextSegment.Tokens"/> on every segment of the tree.</summary>
    /// <returns>True when the tree was calibrated against <paramref name="actualTotal"/>.</returns>
    public static bool Estimate(ContextSegment root, long? actualTotal)
    {
        List<ContextSegment> leaves = root.Leaves().ToList();
        double[] raw = leaves.Select(RawEstimate).ToArray();
        long total = actualTotal ?? 0;
        bool isCalibrated = total > 0 && raw.Sum() > 0;

        long[] tokens = isCalibrated
            ? LargestRemainder(CalibratedShares(leaves, total), total)
            : raw.Select(r => (long)Math.Ceiling(r)).ToArray();
        for (int i = 0; i < leaves.Count; i++)
        {
            leaves[i].Tokens = tokens[i];
        }

        SumParents(root);
        return isCalibrated;
    }

    private static double RawEstimate(ContextSegment leaf) =>
        leaf.FixedTokens is { } fixedTokens ? fixedTokens : leaf.Weight / CharsPerToken;

    private static double[] CalibratedShares(List<ContextSegment> leaves, long total)
    {
        double fixedSum = leaves.Where(l => l.FixedTokens is not null).Sum(l => (double)l.FixedTokens!.Value);
        double textWeight = leaves.Where(l => l.FixedTokens is null).Sum(l => (double)l.Weight);
        if (fixedSum <= total && textWeight > 0)
        {
            double perChar = (total - fixedSum) / textWeight;
            return leaves.Select(l => l.FixedTokens is { } fixedTokens ? fixedTokens : l.Weight * perChar).ToArray();
        }

        // Media alone exceeds the real count (or there is no text): scale everything down together.
        double[] raw = leaves.Select(RawEstimate).ToArray();
        double factor = total / raw.Sum();
        return raw.Select(r => r * factor).ToArray();
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
        long[] result = shares.Select(s => (long)Math.Floor(s)).ToArray();
        long missing = total - result.Sum();
        IEnumerable<int> largestRemainders = Enumerable.Range(0, shares.Length)
            .OrderByDescending(i => shares[i] - Math.Floor(shares[i]))
            .Take((int)Math.Max(0, missing));
        foreach (int i in largestRemainders)
        {
            result[i]++;
        }

        return result;
    }
}

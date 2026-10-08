using AiLog.Contracts;

namespace AiLog.Shared.Harness;

/// <summary>The known harnesses.</summary>
public static class HarnessRegistry
{
    /// <summary>Every classifier, tried in order.</summary>
    public static IReadOnlyList<IHarnessClassifier> Classifiers { get; } = [new ClaudeCodeClassifier()];

    /// <summary>The first classifier recognising the exchange; null when none does.</summary>
    public static IHarnessClassifier? Match(ExchangeLog log) => Classifiers.FirstOrDefault(c => c.Matches(log));
}

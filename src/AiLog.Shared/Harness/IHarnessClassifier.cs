using AiLog.Contracts;
using AiLog.Shared.Context;

namespace AiLog.Shared.Harness;

/// <summary>
/// Knows how one harness (Claude Code, Codex, ...) lays out its prompts, and refines the generic categories into
/// the parts it is made of: skills, memory files, reminders and so on. Runs after the generic categories are set.
/// </summary>
public interface IHarnessClassifier
{
    string Name { get; }

    bool Matches(ExchangeLog log);

    /// <summary>Re-categorises and may split or regroup segments of a parsed request.</summary>
    void Classify(ContextSegment request);
}

public static class HarnessRegistry
{
    public static IReadOnlyList<IHarnessClassifier> Classifiers { get; } = [new ClaudeCodeClassifier()];

    public static IHarnessClassifier? Match(ExchangeLog log) => Classifiers.FirstOrDefault(c => c.Matches(log));
}

using AiLog.Contracts;
using AiLog.Shared.Context;

namespace AiLog.Shared.Harness;

/// <summary>
/// Knows how one harness (Claude Code, Codex, ...) lays out its prompts, and refines the generic categories into
/// the parts it is made of: skills, memory files, reminders and so on. Runs after the generic categories are set.
/// </summary>
public interface IHarnessClassifier
{
    /// <summary>Display name, e.g. "Claude Code".</summary>
    string Name { get; }

    /// <summary>Recognises the harness that sent the exchange, typically by its User-Agent.</summary>
    bool Matches(ExchangeLog log);

    /// <summary>Re-categorises and may split or regroup segments of a parsed request.</summary>
    void Classify(ContextSegment request);
}

using AiLog.Shared.Context;

namespace AiLog.Web.Components;

/// <summary>Stable colours for breakdown categories.</summary>
public static class CategoryColors
{
    private const int HashSeed = 17;
    private const int HashMultiplier = 31;

    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        [Categories.SystemPrompt] = "#6366f1",
        [Categories.Tools] = "#0ea5e9",
        [Categories.BuiltInTools] = "#0ea5e9",
        [Categories.McpTools] = "#06b6d4",
        [Categories.McpInstructions] = "#14b8a6",
        [Categories.Skills] = "#f59e0b",
        [Categories.Memory] = "#ec4899",
        [Categories.Reminders] = "#a855f7",
        [Categories.SessionContext] = "#c084fc",
        [Categories.Environment] = "#8b5cf6",
        [Categories.Agents] = "#64748b",
        [Categories.UserMessages] = "#22c55e",
        [Categories.AssistantMessages] = "#84cc16",
        [Categories.OutputText] = "#84cc16",
        [Categories.Thinking] = "#eab308",
        [Categories.ToolCalls] = "#f97316",
        [Categories.ToolResults] = "#ef4444",
        [Categories.Images] = "#d946ef",
        [Categories.Documents] = "#fb7185",
        [Categories.Other] = "#a1a1aa",
    };

    private static readonly string[] Fallback = ["#78716c", "#0d9488", "#7c3aed", "#be123c", "#4d7c0f"];

    /// <summary>The CSS colour for a category; unknown categories get a stable fallback.</summary>
    public static string For(string category) =>
        Known.TryGetValue(category, out string? color) ? color : Fallback[(int)((uint)StableHash(category) % Fallback.Length)];

    private static int StableHash(string text)
    {
        int hash = HashSeed;
        foreach (char character in text)
        {
            hash = hash * HashMultiplier + character;
        }

        return hash;
    }
}

using AiLog.Shared.Context;

namespace AiLog.Web.Components;

public static class CategoryColors
{
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

    public static string For(string category) =>
        Known.TryGetValue(category, out var color) ? color : Fallback[(int)((uint)StableHash(category) % Fallback.Length)];

    private static int StableHash(string text)
    {
        var hash = 17;
        foreach (var c in text)
        {
            hash = hash * 31 + c;
        }

        return hash;
    }
}

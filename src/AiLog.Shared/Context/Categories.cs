using AiLog.Shared.Providers;

namespace AiLog.Shared.Context;

/// <summary>Breakdown categories. The generic ones come from <see cref="Apply"/>; harness classifiers add finer ones.</summary>
public static class Categories
{
    public const string SystemPrompt = "System prompt";
    public const string Tools = "Tools";
    public const string BuiltInTools = "Built-in tools";
    public const string McpTools = "MCP tools";
    public const string McpInstructions = "MCP instructions";
    public const string UserMessages = "User messages";
    public const string AssistantMessages = "Assistant messages";
    public const string OutputText = "Output text";
    public const string Thinking = "Thinking";
    public const string ToolCalls = "Tool calls";
    public const string ToolResults = "Tool results";
    public const string Images = "Images";
    public const string Documents = "Documents";
    public const string Skills = "Skills";
    public const string Memory = "CLAUDE.md / memory";
    public const string Reminders = "System reminders";
    public const string SessionContext = "Session context";
    public const string Environment = "Environment";
    public const string Agents = "Agents";
    public const string Other = ContextBreakdown.Uncategorized;

    /// <summary>Assigns provider-neutral categories and breakdown items to every leaf.</summary>
    public static void Apply(ContextSegment root, bool isOutput)
    {
        foreach (var top in root.Children)
        {
            var category = top.Label switch
            {
                ProviderAdapter.SystemGroup => SystemPrompt,
                ProviderAdapter.ToolsGroup => Tools,
                _ => null,
            };
            Walk(top, category, item: null, isOutput, messageLabel: null);
        }
    }

    private static void Walk(ContextSegment node, string? category, string? item, bool isOutput, string? messageLabel)
    {
        switch (node.Kind)
        {
            case SegmentKind.Message:
                messageLabel = node.Label;
                if (node.Role is "system" or "developer")
                {
                    category = SystemPrompt;
                    item = node.Label;
                }

                break;
            case SegmentKind.ToolResult:
                category ??= ToolResults;
                item ??= node.Label;
                break;
            case SegmentKind.Document when !node.IsLeaf:
                category ??= Documents;
                item ??= node.Label;
                break;
        }

        if (node.IsLeaf)
        {
            node.Category ??= category ?? node.Kind switch
            {
                SegmentKind.Text when isOutput => OutputText,
                SegmentKind.Text => node.Role == "assistant" ? AssistantMessages : UserMessages,
                SegmentKind.Thinking => Thinking,
                SegmentKind.ToolCall => ToolCalls,
                SegmentKind.ToolResult => ToolResults,
                SegmentKind.Image => Images,
                SegmentKind.Document => Documents,
                SegmentKind.ToolDefinition => Tools,
                _ => Other,
            };

            node.Item ??= item ?? node.Kind switch
            {
                SegmentKind.ToolDefinition or SegmentKind.ToolCall or SegmentKind.ToolResult or SegmentKind.Other => node.Label,
                _ when category == SystemPrompt && messageLabel is null => node.Preview(60),
                _ => messageLabel ?? node.Label,
            };
            return;
        }

        foreach (var child in node.Children)
        {
            Walk(child, category, item, isOutput, messageLabel);
        }
    }
}

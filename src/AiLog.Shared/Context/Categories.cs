using AiLog.Shared.Providers;

namespace AiLog.Shared.Context;

/// <summary>Breakdown categories. The generic ones come from <see cref="Apply"/>; harness classifiers add finer ones.</summary>
public static class Categories
{
    /// <summary>System blocks and system or developer messages.</summary>
    public const string SystemPrompt = "System prompt";

    /// <summary>Tool definitions sent with the request.</summary>
    public const string Tools = "Tools";

    /// <summary>Tool definitions the harness ships itself.</summary>
    public const string BuiltInTools = "Built-in tools";

    /// <summary>Tool definitions contributed by MCP servers.</summary>
    public const string McpTools = "MCP tools";

    /// <summary>Usage instructions contributed by MCP servers.</summary>
    public const string McpInstructions = "MCP instructions";

    /// <summary>Text written by the user.</summary>
    public const string UserMessages = "User messages";

    /// <summary>Earlier assistant turns replayed in the request.</summary>
    public const string AssistantMessages = "Assistant messages";

    /// <summary>Text the model produced in the response.</summary>
    public const string OutputText = "Output text";

    /// <summary>Reasoning blocks, visible or encrypted.</summary>
    public const string Thinking = "Thinking";

    /// <summary>Tool invocations made by the model.</summary>
    public const string ToolCalls = "Tool calls";

    /// <summary>Results returned to the model by tools.</summary>
    public const string ToolResults = "Tool results";

    /// <summary>Image attachments.</summary>
    public const string Images = "Images";

    /// <summary>Document attachments such as PDFs.</summary>
    public const string Documents = "Documents";

    /// <summary>Skill listings, skill calls and injected skill bodies.</summary>
    public const string Skills = "Skills";

    /// <summary>CLAUDE.md files and auto memory.</summary>
    public const string Memory = "CLAUDE.md / memory";

    /// <summary>Injected system reminders.</summary>
    public const string Reminders = "System reminders";

    /// <summary>Session facts such as git status, date and user email.</summary>
    public const string SessionContext = "Session context";

    /// <summary>Working directory, platform, model and date.</summary>
    public const string Environment = "Environment";

    /// <summary>Sub-agent type listings.</summary>
    public const string Agents = "Agents";

    /// <summary>Anything no rule matched.</summary>
    public const string Other = ContextBreakdown.Uncategorized;

    private const int SystemItemPreviewLength = 60;

    /// <summary>Assigns provider-neutral categories and breakdown items to every leaf.</summary>
    public static void Apply(ContextSegment root, bool isOutput)
    {
        foreach (ContextSegment group in root.Children)
        {
            var scope = new Scope(GroupCategory(group), Item: null, MessageLabel: null, isOutput);
            Walk(group, scope);
        }
    }

    private static string? GroupCategory(ContextSegment group) => group.Label switch
    {
        ProviderAdapter.SystemGroup => SystemPrompt,
        ProviderAdapter.ToolsGroup => Tools,
        _ => null,
    };

    private static void Walk(ContextSegment node, Scope inherited)
    {
        Scope scope = Enter(node, inherited);
        if (node.IsLeaf)
        {
            AssignLeaf(node, scope);
            return;
        }

        foreach (ContextSegment child in node.Children)
        {
            Walk(child, scope);
        }
    }

    /// <summary>The scope a node passes on to its leaves; the outermost tool result or document wins.</summary>
    private static Scope Enter(ContextSegment node, Scope inherited) => node.Kind switch
    {
        SegmentKind.Message when node.Role is "system" or "developer" =>
            inherited with { Category = SystemPrompt, Item = node.Label, MessageLabel = node.Label },
        SegmentKind.Message => inherited with { MessageLabel = node.Label },
        SegmentKind.ToolResult => inherited.WithFallback(ToolResults, node.Label),
        SegmentKind.Document when !node.IsLeaf => inherited.WithFallback(Documents, node.Label),
        _ => inherited,
    };

    private static void AssignLeaf(ContextSegment leaf, Scope scope)
    {
        leaf.Category ??= scope.Category ?? DefaultCategory(leaf, scope.IsOutput);
        leaf.Item ??= scope.Item ?? DefaultItem(leaf, scope);
    }

    private static string DefaultCategory(ContextSegment leaf, bool isOutput) => leaf.Kind switch
    {
        SegmentKind.Text when isOutput => OutputText,
        SegmentKind.Text => leaf.Role == "assistant" ? AssistantMessages : UserMessages,
        SegmentKind.Thinking => Thinking,
        SegmentKind.ToolCall => ToolCalls,
        SegmentKind.ToolResult => ToolResults,
        SegmentKind.Image => Images,
        SegmentKind.Document => Documents,
        SegmentKind.ToolDefinition => Tools,
        _ => Other,
    };

    private static string DefaultItem(ContextSegment leaf, Scope scope) => leaf.Kind switch
    {
        SegmentKind.ToolDefinition or SegmentKind.ToolCall or SegmentKind.ToolResult or SegmentKind.Other => leaf.Label,
        _ when scope.Category == SystemPrompt && scope.MessageLabel is null => leaf.Preview(SystemItemPreviewLength),
        _ => scope.MessageLabel ?? leaf.Label,
    };

    /// <summary>What a node hands down to its descendants while walking the tree.</summary>
    private sealed record Scope(string? Category, string? Item, string? MessageLabel, bool IsOutput)
    {
        public Scope WithFallback(string category, string item) =>
            this with { Category = Category ?? category, Item = Item ?? item };
    }
}

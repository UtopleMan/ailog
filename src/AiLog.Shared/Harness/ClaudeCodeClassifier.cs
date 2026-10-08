using System.Text.RegularExpressions;
using AiLog.Contracts;
using AiLog.Shared.Context;
using AiLog.Shared.Providers;

namespace AiLog.Shared.Harness;

/// <summary>
/// Claude Code (User-Agent "claude-cli/..."). Its context is spread over the system blocks, the tool list and
/// injected text in messages: &lt;system-reminder&gt; blocks, a mid-conversation system message with the
/// environment, agent types and skills list, and skill bodies sent as user text. The markers below are
/// heuristics taken from real requests; anything unmatched keeps its generic category.
/// </summary>
public sealed partial class ClaudeCodeClassifier : IHarnessClassifier
{
    private const string CliUserAgentPrefix = "claude-cli/";
    private const string UserAgentMarker = "claude-code";
    private const string ContextReminderLead = "As you answer the user's questions, you can use the following context";
    private const string SkillBaseDirectory = "Base directory for this skill:";
    private const string SkillTool = "Skill";
    private const string SkillCallsItem = "Skill calls";
    private const string SkillResultsItem = "Skill results";
    private const string McpPrefix = "mcp__";
    private const string McpSeparator = "__";
    private const string ClaudeMdSection = "claudeMd";
    private const string ClaudeMdItem = "CLAUDE.md";
    private const string UntitledContextSection = "context";
    private const string PlainTextLabel = "text";
    private const int ItemPreviewLength = 60;

    /// <summary>Section openers. A null item means "use the heading".</summary>
    private static readonly LeadIn[] leadIns =
    [
        new("x-anthropic-billing-header", Categories.SystemPrompt, "Billing header"),
        new("You are Claude Code", Categories.SystemPrompt, "Identity"),
        new("# Environment", Categories.Environment, "Environment"),
        new("You are powered by the model", Categories.Environment, "Model"),
        new("Today's date is", Categories.Environment, "Date"),
        new("Available agent types", Categories.Agents, "Agent types"),
        new("The following skills are available", Categories.Skills, "Skills list"),
        new("# MCP Server Instructions", Categories.McpInstructions, "MCP server instructions"),
        new("# Memory", Categories.Memory, "Memory"),
        new("# auto memory", Categories.Memory, "Memory"),
        new("# claudeMd", Categories.Memory, ClaudeMdItem),
        new("While auto mode is active", Categories.SystemPrompt, "Auto mode"),
    ];

    /// <inheritdoc/>
    public string Name => "Claude Code";

    /// <inheritdoc/>
    public bool Matches(ExchangeLog log) =>
        Headers.Get(log.Request.Headers, "user-agent") is { } agent
        && (agent.StartsWith(CliUserAgentPrefix, StringComparison.OrdinalIgnoreCase)
            || agent.Contains(UserAgentMarker, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc/>
    public void Classify(ContextSegment request)
    {
        foreach (ContextSegment group in request.Children)
        {
            switch (group.Label)
            {
                case ProviderAdapter.SystemGroup:
                    ClassifySystem(group);
                    break;
                case ProviderAdapter.ToolsGroup:
                    GroupTools(group);
                    break;
                case ProviderAdapter.MessagesGroup:
                    ClassifyMessages(group);
                    break;
            }
        }
    }

    private static void ClassifySystem(ContextSegment system)
    {
        foreach (ContextSegment leaf in system.Leaves().Where(l => l.Kind == SegmentKind.Text).ToList())
        {
            SplitPrompt(leaf);
        }
    }

    private static void ClassifyMessages(ContextSegment messages)
    {
        foreach (ContextSegment message in messages.Children)
        {
            ClassifyMessage(message);
        }
    }

    private static void ClassifyMessage(ContextSegment message)
    {
        foreach (ContextSegment leaf in message.Leaves().ToList())
        {
            ClassifyLeaf(leaf, message);
        }

        // Results of the Skill tool with structured content.
        foreach (ContextSegment result in message.Children.Where(c => c.Kind == SegmentKind.ToolResult && c.Label == SkillTool))
        {
            foreach (ContextSegment leaf in result.Leaves())
            {
                MarkSkill(leaf, SkillResultsItem);
            }
        }
    }

    private static void ClassifyLeaf(ContextSegment leaf, ContextSegment message)
    {
        switch (leaf.Kind)
        {
            case SegmentKind.Text when message.Role == "system":
                SplitPrompt(leaf);
                break;
            case SegmentKind.Text when message.Role == "user" && leaf.Category == Categories.UserMessages:
                SplitUserText(leaf, message.Label);
                break;
            case SegmentKind.ToolCall when leaf.Label == SkillTool:
                MarkSkill(leaf, SkillName(leaf) ?? SkillCallsItem);
                break;
            case SegmentKind.ToolResult when leaf.Label == SkillTool:
                MarkSkill(leaf, SkillResultsItem);
                break;
        }
    }

    private static void MarkSkill(ContextSegment leaf, string item)
    {
        leaf.Category = Categories.Skills;
        leaf.Item = item;
    }

    /// <summary>System text: sections start at "# " headings and known lead-in paragraphs.</summary>
    private static void SplitPrompt(ContextSegment leaf)
    {
        List<TextSection> sections = TextSections.Split(leaf.Text ?? "", IsPromptBoundary, needsBlankLineBefore: true);
        Replace(leaf, sections.Select(PromptSection).ToList());
    }

    private static bool IsPromptBoundary(string line) => TextSections.IsHeading(line) || FindLeadIn(line) is not null;

    private static ContextSegment PromptSection(TextSection section)
    {
        string firstLine = section.FirstLine;
        Placement placement = FindLeadIn(firstLine) is { } leadIn
            ? leadIn.PlacementFor(firstLine)
            : new Placement(Categories.SystemPrompt, TextSections.IsHeading(firstLine) ? TextSections.HeadingText(firstLine) : firstLine.Shorten(ItemPreviewLength));
        return Section(section.Text, placement.Item, placement);
    }

    /// <summary>User text: separates &lt;system-reminder&gt; blocks and injected skill bodies from what the user typed.</summary>
    private static void SplitUserText(ContextSegment leaf, string messageLabel)
    {
        string text = leaf.Text ?? "";
        var parts = new List<ContextSegment>();
        int position = 0;
        foreach (Match reminder in Reminder().Matches(text))
        {
            parts.AddRange(TypedSections(text[position..reminder.Index], messageLabel));
            parts.AddRange(ClassifyReminder(reminder.Value, reminder.Groups[1].Value));
            position = reminder.Index + reminder.Length;
        }

        parts.AddRange(TypedSections(text[position..], messageLabel));
        Replace(leaf, parts);
    }

    /// <summary>What the user typed between reminders, unless it is blank; skill bodies count as skills.</summary>
    private static IEnumerable<ContextSegment> TypedSections(string typed, string messageLabel)
    {
        typed = typed.Trim('\n');
        if (typed.Trim().Length == 0)
        {
            yield break;
        }

        if (typed.StartsWith(SkillBaseDirectory, StringComparison.Ordinal))
        {
            string skill = SkillFromBaseDirectory(typed);
            yield return Section(typed, $"skill: {skill}", new Placement(Categories.Skills, skill));
        }
        else
        {
            yield return Section(typed, PlainTextLabel, new Placement(Categories.UserMessages, messageLabel));
        }
    }

    private static string SkillFromBaseDirectory(string skillBody)
    {
        string path = skillBody[SkillBaseDirectory.Length..].Split('\n')[0].Trim().TrimEnd('/');
        return path[(path.LastIndexOf('/') + 1)..];
    }

    private static IEnumerable<ContextSegment> ClassifyReminder(string whole, string inner) =>
        inner.Contains(ContextReminderLead, StringComparison.Ordinal)
            ? ContextReminderSections(whole)
            : [SingleReminder(whole, inner)];

    /// <summary>The context reminder holds "# name" sections: claudeMd, userEmail, gitStatus, currentDate...</summary>
    private static IEnumerable<ContextSegment> ContextReminderSections(string reminder)
    {
        foreach (TextSection section in TextSections.Split(reminder, TextSections.IsHeading, needsBlankLineBefore: false))
        {
            string name = TextSections.IsHeading(section.FirstLine) ? TextSections.HeadingText(section.FirstLine) : UntitledContextSection;
            bool isMemory = name.Contains(ClaudeMdSection, StringComparison.OrdinalIgnoreCase) || name.Contains("memory", StringComparison.OrdinalIgnoreCase);
            string category = isMemory ? Categories.Memory : Categories.SessionContext;
            string item = name.Equals(ClaudeMdSection, StringComparison.OrdinalIgnoreCase) ? ClaudeMdItem : name;
            yield return Section(section.Text, $"reminder: {name}", new Placement(category, item));
        }
    }

    private static ContextSegment SingleReminder(string whole, string inner)
    {
        string firstLine = inner.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        Placement placement = FindLeadIn(firstLine) is { } leadIn
            ? leadIn.PlacementFor(firstLine)
            : new Placement(Categories.Reminders, firstLine.Shorten(ItemPreviewLength));
        return Section(whole, $"reminder: {placement.Item}", placement);
    }

    /// <summary>Built-in tools stay together; MCP tools ("mcp__server__tool") are grouped per server.</summary>
    private static void GroupTools(ContextSegment tools)
    {
        ContextSegment builtIn = ContextSegment.Group("Built-in");
        var servers = new Dictionary<string, ContextSegment>(StringComparer.Ordinal);
        foreach (ContextSegment tool in tools.Children)
        {
            if (McpServer(tool.Label) is { } server)
            {
                AddToServerGroup(servers, tool, server);
            }
            else
            {
                tool.Category = Categories.BuiltInTools;
                builtIn.Children.Add(tool);
            }
        }

        if (servers.Count == 0)
        {
            return;
        }

        tools.Children = [builtIn, .. servers.Values];
        foreach (ContextSegment group in tools.Children)
        {
            group.Label += $" ({group.Children.Count})";
        }
    }

    private static string? McpServer(string toolName)
    {
        if (!toolName.StartsWith(McpPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        string rest = toolName[McpPrefix.Length..];
        int separator = rest.IndexOf(McpSeparator, StringComparison.Ordinal);
        return separator < 0 ? rest : rest[..separator];
    }

    private static void AddToServerGroup(Dictionary<string, ContextSegment> servers, ContextSegment tool, string server)
    {
        if (!servers.TryGetValue(server, out ContextSegment? group))
        {
            servers[server] = group = ContextSegment.Group($"MCP: {server}");
        }

        tool.Category = Categories.McpTools;
        tool.Item = server;
        group.Children.Add(tool);
    }

    private static LeadIn? FindLeadIn(string line) =>
        leadIns.FirstOrDefault(leadIn => line.StartsWith(leadIn.Prefix, StringComparison.Ordinal));

    /// <summary>Turns a text leaf into a parent of its sections; a single section just relabels the leaf.</summary>
    private static void Replace(ContextSegment leaf, List<ContextSegment> sections)
    {
        if (sections.Count == 0)
        {
            return;
        }

        if (sections.Count == 1)
        {
            Relabel(leaf, sections[0]);
            return;
        }

        foreach (ContextSegment section in sections)
        {
            section.Role = leaf.Role;
        }

        leaf.Children = sections;
        leaf.Text = null;
        leaf.Weight = 0;
    }

    private static void Relabel(ContextSegment leaf, ContextSegment onlySection)
    {
        leaf.Category = onlySection.Category;
        leaf.Item = onlySection.Item;
        if (leaf.Label == PlainTextLabel)
        {
            leaf.Label = onlySection.Label;
        }
    }

    private static ContextSegment Section(string text, string label, Placement placement)
    {
        ContextSegment section = ContextSegment.FromText(SegmentKind.Text, label, text);
        section.Category = placement.Category;
        section.Item = placement.Item;
        return section;
    }

    private static string? SkillName(ContextSegment call) =>
        call.Json is { } json && Json.TryParse(json) is { } input ? Json.String(input, "skill") : null;

    [GeneratedRegex(@"<system-reminder>\s*(.*?)\s*</system-reminder>", RegexOptions.Singleline)]
    private static partial Regex Reminder();

    /// <summary>Where a section lands in the breakdown.</summary>
    private sealed record Placement(string Category, string Item);

    /// <summary>A line prefix that opens a known section. A null item means "use the heading".</summary>
    private sealed record LeadIn(string Prefix, string Category, string? Item)
    {
        public Placement PlacementFor(string firstLine) => new(Category, Item ?? TextSections.HeadingText(firstLine));
    }
}

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
    private const string ContextReminderLead = "As you answer the user's questions, you can use the following context";
    private const string SkillBaseDirectory = "Base directory for this skill:";
    private const string McpPrefix = "mcp__";

    /// <summary>Section openers: (line prefix, category, breakdown item). Item null means "use the heading".</summary>
    private static readonly (string Prefix, string Category, string? Item)[] LeadIns =
    [
        ("x-anthropic-billing-header", Categories.SystemPrompt, "Billing header"),
        ("You are Claude Code", Categories.SystemPrompt, "Identity"),
        ("# Environment", Categories.Environment, "Environment"),
        ("You are powered by the model", Categories.Environment, "Model"),
        ("Today's date is", Categories.Environment, "Date"),
        ("Available agent types", Categories.Agents, "Agent types"),
        ("The following skills are available", Categories.Skills, "Skills list"),
        ("# MCP Server Instructions", Categories.McpInstructions, "MCP server instructions"),
        ("# Memory", Categories.Memory, "Memory"),
        ("# auto memory", Categories.Memory, "Memory"),
        ("# claudeMd", Categories.Memory, "CLAUDE.md"),
        ("While auto mode is active", Categories.SystemPrompt, "Auto mode"),
    ];

    public string Name => "Claude Code";

    public bool Matches(ExchangeLog log) =>
        Headers.Get(log.Request.Headers, "user-agent") is { } agent
        && (agent.StartsWith("claude-cli/", StringComparison.OrdinalIgnoreCase) || agent.Contains("claude-code", StringComparison.OrdinalIgnoreCase));

    public void Classify(ContextSegment request)
    {
        foreach (var top in request.Children)
        {
            switch (top.Label)
            {
                case ProviderAdapter.SystemGroup:
                    foreach (var leaf in top.Leaves().Where(l => l.Kind == SegmentKind.Text).ToList())
                    {
                        SplitPrompt(leaf, Categories.SystemPrompt);
                    }

                    break;
                case ProviderAdapter.ToolsGroup:
                    GroupTools(top);
                    break;
                case ProviderAdapter.MessagesGroup:
                    foreach (var message in top.Children)
                    {
                        ClassifyMessage(message);
                    }

                    break;
            }
        }
    }

    private static void ClassifyMessage(ContextSegment message)
    {
        foreach (var leaf in message.Leaves().ToList())
        {
            switch (leaf.Kind)
            {
                case SegmentKind.Text when message.Role == "system":
                    SplitPrompt(leaf, Categories.SystemPrompt);
                    break;
                case SegmentKind.Text when message.Role == "user" && leaf.Category == Categories.UserMessages:
                    SplitUserText(leaf, message.Label);
                    break;
                case SegmentKind.ToolCall when leaf.Label == "Skill":
                    leaf.Category = Categories.Skills;
                    leaf.Item = SkillName(leaf) ?? "Skill calls";
                    break;
                case SegmentKind.ToolResult when leaf.Label == "Skill":
                    leaf.Category = Categories.Skills;
                    leaf.Item = "Skill results";
                    break;
            }
        }

        // Results of the Skill tool with structured content.
        foreach (var result in message.Children.Where(c => c.Kind == SegmentKind.ToolResult && c.Label == "Skill"))
        {
            foreach (var leaf in result.Leaves())
            {
                leaf.Category = Categories.Skills;
                leaf.Item = "Skill results";
            }
        }
    }

    /// <summary>System text: sections start at "# " headings and known lead-in paragraphs.</summary>
    private static void SplitPrompt(ContextSegment leaf, string fallbackCategory)
    {
        var sections = TextSections.Split(leaf.Text ?? "", line => TextSections.IsHeading(line) || LeadIn(line) is not null, needsBlankLineBefore: true);
        Replace(leaf, sections.Select(s =>
        {
            var (category, item) = LeadIn(s.FirstLine) is { } match
                ? (match.Category, match.Item ?? TextSections.HeadingText(s.FirstLine))
                : (fallbackCategory, TextSections.IsHeading(s.FirstLine) ? TextSections.HeadingText(s.FirstLine) : Shorten(s.FirstLine, 60));
            return Section(s.Text, item, category, item);
        }).ToList());
    }

    /// <summary>User text: separates &lt;system-reminder&gt; blocks and injected skill bodies from what the user typed.</summary>
    private static void SplitUserText(ContextSegment leaf, string messageLabel)
    {
        var text = leaf.Text ?? "";
        var parts = new List<ContextSegment>();
        var position = 0;
        foreach (Match reminder in Reminder().Matches(text))
        {
            AddTyped(text[position..reminder.Index]);
            parts.AddRange(ClassifyReminder(reminder.Value, reminder.Groups[1].Value));
            position = reminder.Index + reminder.Length;
        }

        AddTyped(text[position..]);
        Replace(leaf, parts);

        void AddTyped(string typed)
        {
            typed = typed.Trim('\n');
            if (typed.Trim().Length == 0)
            {
                return;
            }

            if (typed.StartsWith(SkillBaseDirectory, StringComparison.Ordinal))
            {
                var path = typed[SkillBaseDirectory.Length..].Split('\n')[0].Trim().TrimEnd('/');
                var skill = path[(path.LastIndexOf('/') + 1)..];
                parts.Add(Section(typed, $"skill: {skill}", Categories.Skills, skill));
            }
            else
            {
                parts.Add(Section(typed, "text", Categories.UserMessages, messageLabel));
            }
        }
    }

    private static IEnumerable<ContextSegment> ClassifyReminder(string whole, string inner)
    {
        // The context reminder holds "# name" sections: claudeMd, userEmail, gitStatus, currentDate...
        if (inner.Contains(ContextReminderLead, StringComparison.Ordinal))
        {
            foreach (var section in TextSections.Split(whole, TextSections.IsHeading, needsBlankLineBefore: false))
            {
                var name = TextSections.IsHeading(section.FirstLine) ? TextSections.HeadingText(section.FirstLine) : "context";
                var isMemory = name.Contains("claudeMd", StringComparison.OrdinalIgnoreCase) || name.Contains("memory", StringComparison.OrdinalIgnoreCase);
                yield return Section(section.Text, $"reminder: {name}",
                    isMemory ? Categories.Memory : Categories.SessionContext,
                    name.Equals("claudeMd", StringComparison.OrdinalIgnoreCase) ? "CLAUDE.md" : name);
            }

            yield break;
        }

        var firstLine = inner.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        var (category, item) = LeadIn(firstLine) is { } match
            ? (match.Category, match.Item ?? TextSections.HeadingText(firstLine))
            : (Categories.Reminders, Shorten(firstLine, 60));
        yield return Section(whole, $"reminder: {item}", category, item);
    }

    /// <summary>Built-in tools stay together; MCP tools ("mcp__server__tool") are grouped per server.</summary>
    private static void GroupTools(ContextSegment tools)
    {
        var builtIn = ContextSegment.Group("Built-in");
        var servers = new Dictionary<string, ContextSegment>(StringComparer.Ordinal);
        foreach (var tool in tools.Children)
        {
            if (tool.Label.StartsWith(McpPrefix, StringComparison.Ordinal))
            {
                var rest = tool.Label[McpPrefix.Length..];
                var separator = rest.IndexOf("__", StringComparison.Ordinal);
                var server = separator < 0 ? rest : rest[..separator];
                if (!servers.TryGetValue(server, out var group))
                {
                    servers[server] = group = ContextSegment.Group($"MCP: {server}");
                }

                tool.Category = Categories.McpTools;
                tool.Item = server;
                group.Children.Add(tool);
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
        foreach (var group in tools.Children)
        {
            group.Label += $" ({group.Children.Count})";
        }
    }

    private static (string Category, string? Item)? LeadIn(string line)
    {
        foreach (var (prefix, category, item) in LeadIns)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return (category, item);
            }
        }

        return null;
    }

    /// <summary>Turns a text leaf into a parent of its sections; a single section just relabels the leaf.</summary>
    private static void Replace(ContextSegment leaf, List<ContextSegment> sections)
    {
        if (sections.Count == 0)
        {
            return;
        }

        if (sections.Count == 1)
        {
            leaf.Category = sections[0].Category;
            leaf.Item = sections[0].Item;
            if (leaf.Label == "text")
            {
                leaf.Label = sections[0].Label;
            }

            return;
        }

        foreach (var section in sections)
        {
            section.Role = leaf.Role;
        }

        leaf.Children = sections;
        leaf.Text = null;
        leaf.Weight = 0;
    }

    private static ContextSegment Section(string text, string label, string category, string item)
    {
        var section = ContextSegment.FromText(SegmentKind.Text, label, text);
        section.Category = category;
        section.Item = item;
        return section;
    }

    private static string? SkillName(ContextSegment call) =>
        call.Json is { } json && AiLog.Shared.Json.TryParse(json) is { } input ? AiLog.Shared.Json.String(input, "skill") : null;

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    [GeneratedRegex(@"<system-reminder>\s*(.*?)\s*</system-reminder>", RegexOptions.Singleline)]
    private static partial Regex Reminder();
}

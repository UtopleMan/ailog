using Markdig;
using Microsoft.AspNetCore.Components;

namespace AiLog.Web;

/// <summary>Renders logged markdown to safe HTML.</summary>
public static class Markdown
{
    // Raw HTML is escaped, not passed through: prompts are full of tags like <system-reminder> that must show
    // as text, and logged content must never inject markup into the page.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    /// <summary>Converts markdown to HTML with raw HTML escaped.</summary>
    public static MarkupString Render(string text) => new(Markdig.Markdown.ToHtml(text, Pipeline));
}

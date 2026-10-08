using Markdig;
using Microsoft.AspNetCore.Components;

namespace AiLog.Web;

public static class Markdown
{
    // Raw HTML is escaped, not passed through: prompts are full of tags like <system-reminder> that must show
    // as text, and logged content must never inject markup into the page.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static MarkupString Render(string text) => new(Markdig.Markdown.ToHtml(text, Pipeline));
}

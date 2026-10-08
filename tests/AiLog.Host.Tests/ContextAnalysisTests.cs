using System.Buffers.Binary;
using AiLog.Contracts;
using AiLog.Shared;
using AiLog.Shared.Context;
using AiLog.Shared.Providers;

namespace AiLog.Host.Tests;

public sealed class ContextAnalysisTests
{
    // A sanitised Claude Code request with the same layout as a real one.
    private static readonly string ClaudeCodeRequest = $$$"""
        {
          "model": "claude-test",
          "system": [
            {"type": "text", "text": "x-anthropic-billing-header: cc_version=1.0;"},
            {"type": "text", "text": "You are Claude Code, Anthropic's official CLI for Claude.", "cache_control": {"type": "ephemeral"}},
            {"type": "text", "text": "You are an agent.\n\n# Harness\nRules about the harness.\n\n```sh\n# not a heading\n```\n\n# Memory\nYou have a persistent memory.\n\n## Types\nuser, feedback.", "cache_control": {"type": "ephemeral"}}
          ],
          "tools": [
            {"name": "Bash", "description": "Runs a command", "input_schema": {"type": "object"}},
            {"name": "Skill", "description": "Invokes a skill", "input_schema": {"type": "object"}},
            {"name": "mcp__chrome__navigate", "description": "Navigates", "input_schema": {"type": "object"}},
            {"name": "mcp__chrome__click", "description": "Clicks", "input_schema": {"type": "object"}}
          ],
          "messages": [
            {"role": "user", "content": [
              {"type": "text", "text": "<system-reminder>\nAs you answer the user's questions, you can use the following context:\n# claudeMd\nContents of /repo/CLAUDE.md:\nUse tabs.\n# gitStatus\nOn branch main\n</system-reminder>"},
              {"type": "text", "text": "<system-reminder>\nAttribution for git commits: add a trailer.\n</system-reminder>\n\nPlease list the files"}
            ]},
            {"role": "system", "content": [
              {"type": "text", "text": "# Environment\nWorking directory: /repo\n\nAvailable agent types for the Agent tool:\n- general: anything\n\nThe following skills are available for use with the Skill tool:\n\n- grilling: grills you\n\nToday's date is 2026-01-01."}
            ]},
            {"role": "assistant", "content": [
              {"type": "thinking", "thinking": "I should run ls.", "signature": "c2ln"},
              {"type": "tool_use", "id": "toolu_1", "name": "Bash", "input": {"command": "ls"}},
              {"type": "tool_use", "id": "toolu_2", "name": "Skill", "input": {"skill": "grilling"}}
            ]},
            {"role": "user", "content": [
              {"type": "tool_result", "tool_use_id": "toolu_1", "content": "a.txt\nb.txt"},
              {"type": "tool_result", "tool_use_id": "toolu_2", "content": "Launching skill: grilling"},
              {"type": "text", "text": "Base directory for this skill: /repo/.claude/skills/grilling\n\nInterview the user."},
              {"type": "image", "source": {"type": "base64", "media_type": "image/png", "data": "{{{Png(200, 100)}}}"}}
            ]}
          ]
        }
        """;

    private const string ClaudeCodeUsage = """{"input_tokens":100,"cache_read_input_tokens":5000,"cache_creation_input_tokens":0,"output_tokens":50}""";
    private const long ClaudeCodeInputTokens = 100 + 5000;
    private const string AnthropicTarget = "/anthropic/v1/messages?beta=true";
    private const string ClaudeCliUserAgent = "claude-cli/2.1.0 (external, cli)";

    [Fact]
    public void Claude_code_request_is_recognised()
    {
        ExchangeAnalysis analysis = AnalyzeClaudeCodeRequest();

        Assert.Equal("Anthropic Messages", analysis.Provider!.Name);
        Assert.Equal("Claude Code", analysis.Harness!.Name);
        Assert.Equal("claude-test", analysis.Model);
        Assert.Null(analysis.Error);
    }

    [Fact]
    public void Claude_code_request_is_split_into_its_parts()
    {
        ExchangeAnalysis analysis = AnalyzeClaudeCodeRequest();

        HashSet<(string Category, string Label)> items = analysis.InputBreakdown
            .SelectMany(category => category.Items.Select(item => (category.Name, item.Label)))
            .ToHashSet();

        Assert.Contains((Categories.SystemPrompt, "Billing header"), items);
        Assert.Contains((Categories.SystemPrompt, "Identity"), items);
        Assert.Contains((Categories.SystemPrompt, "Harness"), items);
        Assert.Contains((Categories.Memory, "Memory"), items);
        Assert.Contains((Categories.BuiltInTools, "Bash"), items);
        Assert.Contains((Categories.McpTools, "chrome"), items);
        Assert.Contains((Categories.Memory, "CLAUDE.md"), items);
        Assert.Contains((Categories.SessionContext, "gitStatus"), items);
        Assert.Contains((Categories.Reminders, "Attribution for git commits: add a trailer."), items);
        Assert.Contains((Categories.UserMessages, "#1 user"), items);
        Assert.Contains((Categories.Environment, "Environment"), items);
        Assert.Contains((Categories.Environment, "Date"), items);
        Assert.Contains((Categories.Agents, "Agent types"), items);
        Assert.Contains((Categories.Skills, "Skills list"), items);
        Assert.Contains((Categories.Skills, "grilling"), items);
        Assert.Contains((Categories.Skills, "Skill results"), items);
        Assert.Contains((Categories.Thinking, "#3 assistant"), items);
        Assert.Contains((Categories.ToolCalls, "Bash"), items);
        Assert.Contains((Categories.ToolResults, "Bash"), items);
        Assert.Contains((Categories.Images, "#4 user"), items);
    }

    [Fact]
    public void Headings_inside_code_fences_do_not_start_a_section()
    {
        ExchangeAnalysis analysis = AnalyzeClaudeCodeRequest();

        ContextSegment harness = analysis.Request!.Descendants().Single(segment => segment.Item == "Harness");

        Assert.Contains("# not a heading", harness.Text);
    }

    [Fact]
    public void Mcp_tools_are_grouped_per_server()
    {
        ExchangeAnalysis analysis = AnalyzeClaudeCodeRequest();

        ContextSegment tools = ToolsOf(analysis.Request!);

        Assert.Equal(["Built-in (2)", "MCP: chrome (2)"], tools.Children.Select(group => group.Label));
    }

    [Fact]
    public void Estimates_add_up_to_the_real_input_count()
    {
        ExchangeAnalysis analysis = AnalyzeClaudeCodeRequest();

        Assert.True(analysis.InputCalibrated);
        Assert.Equal(ClaudeCodeInputTokens, analysis.Request!.Tokens);
        Assert.Equal(ClaudeCodeInputTokens, analysis.InputBreakdown.Sum(category => category.Tokens));
    }

    [Fact]
    public void Images_keep_their_own_pixel_based_estimate()
    {
        ExchangeAnalysis analysis = AnalyzeClaudeCodeRequest();

        ContextSegment image = analysis.Request!.Descendants().Single(segment => segment.Kind == SegmentKind.Image);

        // 200×100 px / 750.
        Assert.Equal(27, image.Tokens);
        Assert.Equal("200×100 px", image.Note);
    }

    [Fact]
    public void Without_usage_tokens_fall_back_to_characters_per_token()
    {
        ExchangeAnalysis analysis = ExchangeAnalysis.Analyze(Log(new("""{"model":"m","messages":[{"role":"user","content":"12345678"}]}""")));

        Assert.False(analysis.InputCalibrated);
        Assert.Equal(2, analysis.Request!.Tokens);
    }

    [Fact]
    public void Cached_prefix_runs_tools_then_system_up_to_the_last_breakpoint()
    {
        ContextSegment request = AnalyzeClaudeCodeRequest(usage: null).Request!;

        Assert.All(ToolsOf(request).Leaves(), tool => Assert.True(tool.Cached));
        Assert.All(SystemOf(request).Children, block => Assert.True(block.Cached));
        Assert.All(MessagesOf(request).Descendants(), segment => Assert.False(segment.Cached));
        Assert.Equal(2, SystemOf(request).Children.Count(block => block.CacheBreakpoint));
    }

    [Fact]
    public void Streamed_response_is_reassembled_and_its_output_estimated()
    {
        ExchangeLog log = Log(new("""{"model":"m","messages":[{"role":"user","content":"hi"}]}"""));
        ExchangeAnalysis analysis = ExchangeAnalysis.Analyze(WithSseResponse(log, """
            event: message_start
            data: {"type":"message_start","message":{"id":"m1","content":[],"usage":{"input_tokens":12,"output_tokens":1}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello there"}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}

            """));

        Assert.Equal(4, analysis.Events!.Count);
        Assert.Equal(12, analysis.Request!.Tokens);
        Assert.True(analysis.OutputCalibrated);
        ContextSegment output = Assert.Single(analysis.Response!.Children);
        Assert.Equal("Hello there", output.Text);
        Assert.Equal(3, output.Tokens);
        Assert.Equal(Categories.OutputText, output.Category);
    }

    [Fact]
    public void OpenAI_chat_request_maps_tool_results_to_their_calls()
    {
        ExchangeAnalysis analysis = ExchangeAnalysis.Analyze(Log(new("""
            {"model":"gpt","messages":[
              {"role":"system","content":"Be brief."},
              {"role":"user","content":[{"type":"text","text":"Weather?"}]},
              {"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Oslo\"}"}}]},
              {"role":"tool","tool_call_id":"call_1","content":"Sunny"}
            ],"tools":[{"type":"function","function":{"name":"get_weather","parameters":{}}}]}
            """, "/openai/v1/chat/completions", "test-agent")));

        Assert.Equal("OpenAI Chat Completions", analysis.Provider!.Name);
        Dictionary<string, List<string>> categories = LabelsByCategory(analysis);
        Assert.Equal(["#1 system"], categories[Categories.SystemPrompt]);
        Assert.Equal(["get_weather"], categories[Categories.ToolResults]);
        Assert.Equal(["get_weather"], categories[Categories.ToolCalls]);
        Assert.Equal(["get_weather"], categories[Categories.Tools]);
    }

    [Fact]
    public void OpenAI_responses_request_reads_instructions_and_items()
    {
        ExchangeAnalysis analysis = ExchangeAnalysis.Analyze(Log(new("""
            {"model":"gpt","instructions":"You are Codex.","input":[
              {"type":"message","role":"developer","content":[{"type":"input_text","text":"Sandbox rules"}]},
              {"type":"message","role":"user","content":[{"type":"input_text","text":"fix it"}]},
              {"type":"reasoning","summary":[{"type":"summary_text","text":"Thinking"}],"encrypted_content":"AAAAAAAAAAAAAAAAAAAA"},
              {"type":"function_call","name":"shell","call_id":"c1","arguments":"{\"cmd\":[\"ls\"]}"},
              {"type":"function_call_output","call_id":"c1","output":"a.txt"}
            ]}
            """, "/openai/v1/responses", "codex_cli_rs/0.1")));

        Assert.Equal("OpenAI Responses", analysis.Provider!.Name);
        Dictionary<string, List<string>> categories = LabelsByCategory(analysis);
        Assert.Contains("#1 developer", categories[Categories.SystemPrompt]);
        Assert.Equal(["shell"], categories[Categories.ToolResults]);
        Assert.Equal(["#3 reasoning"], categories[Categories.Thinking]);
    }

    [Fact]
    public void Unknown_exchanges_get_no_provider_view()
    {
        ExchangeAnalysis analysis = ExchangeAnalysis.Analyze(Log(new("""{"hello":1}""", "/anthropic/api/hello")));

        Assert.Null(analysis.Provider);
        Assert.Null(analysis.Request);
        Assert.Empty(analysis.InputBreakdown);
    }

    // 1568×1568 is scaled down to ~1.15 MP; 4000×1000 has its long edge scaled to 1568.
    [Theory]
    [InlineData(200, 100, 27)]
    [InlineData(1568, 1568, 1534)]
    [InlineData(4000, 1000, 820)]
    public void Image_tokens_follow_the_pixel_formula(int width, int height, long expected) =>
        Assert.Equal(expected, MediaEstimator.ImageTokens(width, height));

    [Fact]
    public void Image_size_is_read_from_a_png_header()
    {
        Assert.Equal(new ImageSize(640, 480), MediaEstimator.ReadSize(Convert.FromBase64String(Png(640, 480))));
    }

    [Fact]
    public void Image_size_is_read_from_a_gif_header()
    {
        byte[] gif = [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 0x20, 0x00, 0x10, 0x00];

        Assert.Equal(new ImageSize(32, 16), MediaEstimator.ReadSize(gif));
    }

    [Fact]
    public void Image_size_is_read_from_a_jpeg_header()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x01, 0x2C, 0x02, 0x58, 0x03];

        Assert.Equal(new ImageSize(600, 300), MediaEstimator.ReadSize(jpeg));
    }

    private static ExchangeAnalysis AnalyzeClaudeCodeRequest(string? usage = ClaudeCodeUsage) =>
        ExchangeAnalysis.Analyze(Log(new(ClaudeCodeRequest), usage));

    private static ContextSegment SystemOf(ContextSegment request) => request.Children[0];

    private static ContextSegment ToolsOf(ContextSegment request) => request.Children[1];

    private static ContextSegment MessagesOf(ContextSegment request) => request.Children[2];

    private static Dictionary<string, List<string>> LabelsByCategory(ExchangeAnalysis analysis) =>
        analysis.InputBreakdown.ToDictionary(category => category.Name, category => category.Items.Select(item => item.Label).ToList());

    /// <summary>A base64 PNG holding just the signature and the IHDR size fields.</summary>
    private static string Png(int width, int height)
    {
        byte[] bytes = new byte[24];
        byte[] signatureAndIhdr = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];
        signatureAndIhdr.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        return Convert.ToBase64String(bytes);
    }

    private static ExchangeLog Log(CapturedRequest request, string? usage = null) => new()
    {
        Id = "2026-01-01T00-00-00.000Z_0001_POST_v1-messages",
        Sequence = 1,
        StartedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        CompletedAt = DateTimeOffset.Parse("2026-01-01T00:00:01Z"),
        DurationMs = 1000,
        Route = request.Target.Split('/')[1],
        UpstreamUrl = "https://upstream.example" + request.Target,
        Outcome = ExchangeOutcome.Completed,
        Request = new LoggedRequest
        {
            Method = "POST",
            Target = request.Target,
            Headers = new() { ["User-Agent"] = request.UserAgent },
            Body = LoggedBodies.Json(request.Json),
        },
        Response = new LoggedResponse
        {
            StatusCode = 200,
            Headers = new() { ["Content-Type"] = "application/json" },
            Body = LoggedBodies.Json(usage is null ? """{"content":[]}""" : $$"""{"type":"message","content":[],"usage":{{usage}}}"""),
        },
    };

    private static ExchangeLog WithSseResponse(ExchangeLog log, string sse) => new()
    {
        Id = log.Id,
        Sequence = log.Sequence,
        StartedAt = log.StartedAt,
        CompletedAt = log.CompletedAt,
        DurationMs = log.DurationMs,
        Route = log.Route,
        UpstreamUrl = log.UpstreamUrl,
        Outcome = log.Outcome,
        Request = log.Request,
        Response = new LoggedResponse
        {
            StatusCode = 200,
            Headers = new() { ["content-type"] = "text/event-stream" },
            Body = LoggedBodies.Text(sse),
        },
    };

    /// <summary>The request a harness sent: its JSON body, target and user agent.</summary>
    private sealed record CapturedRequest(string Json, string Target = AnthropicTarget, string UserAgent = ClaudeCliUserAgent);
}

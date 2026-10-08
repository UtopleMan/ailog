using System.Text.Json;
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

    [Fact]
    public void Claude_code_request_is_split_into_its_parts()
    {
        var analysis = ExchangeAnalysis.Analyze(Log(ClaudeCodeRequest, usage: """{"input_tokens":100,"cache_read_input_tokens":5000,"cache_creation_input_tokens":0,"output_tokens":50}"""));

        Assert.Equal("Anthropic Messages", analysis.Provider!.Name);
        Assert.Equal("Claude Code", analysis.Harness!.Name);
        Assert.Equal("claude-test", analysis.Model);
        Assert.Null(analysis.Error);

        var items = analysis.InputBreakdown
            .SelectMany(c => c.Items.Select(i => (c.Name, i.Label)))
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

        // The fenced "# not a heading" stays inside the Harness section.
        var harness = analysis.Request!.Descendants().Single(s => s.Item == "Harness");
        Assert.Contains("# not a heading", harness.Text);

        // MCP tools are grouped per server.
        var tools = analysis.Request.Children[1];
        Assert.Equal(["Built-in (2)", "MCP: chrome (2)"], tools.Children.Select(c => c.Label));
    }

    [Fact]
    public void Estimates_add_up_to_the_real_input_count()
    {
        var analysis = ExchangeAnalysis.Analyze(Log(ClaudeCodeRequest, usage: """{"input_tokens":100,"cache_read_input_tokens":5000,"cache_creation_input_tokens":0,"output_tokens":50}"""));

        Assert.True(analysis.InputCalibrated);
        Assert.Equal(5100, analysis.Request!.Tokens);
        Assert.Equal(5100, analysis.InputBreakdown.Sum(c => c.Tokens));

        // Images keep their own estimate: 200×100 px / 750.
        var image = analysis.Request.Descendants().Single(s => s.Kind == SegmentKind.Image);
        Assert.Equal(27, image.Tokens);
        Assert.Equal("200×100 px", image.Note);
    }

    [Fact]
    public void Without_usage_tokens_fall_back_to_characters_per_token()
    {
        var analysis = ExchangeAnalysis.Analyze(Log("""{"model":"m","messages":[{"role":"user","content":"12345678"}]}""", usage: null));

        Assert.False(analysis.InputCalibrated);
        Assert.Equal(2, analysis.Request!.Tokens);
    }

    [Fact]
    public void Cached_prefix_runs_tools_then_system_up_to_the_last_breakpoint()
    {
        var analysis = ExchangeAnalysis.Analyze(Log(ClaudeCodeRequest, usage: null));
        var request = analysis.Request!;

        Assert.All(request.Children[1].Leaves(), tool => Assert.True(tool.Cached));
        Assert.All(request.Children[0].Children, block => Assert.True(block.Cached));
        Assert.All(request.Children[2].Descendants(), segment => Assert.False(segment.Cached));
        Assert.Equal(2, request.Children[0].Children.Count(b => b.CacheBreakpoint));
    }

    [Fact]
    public void Streamed_response_is_reassembled_and_its_output_estimated()
    {
        var log = Log("""{"model":"m","messages":[{"role":"user","content":"hi"}]}""", usage: null);
        var analysis = ExchangeAnalysis.Analyze(WithSseResponse(log, """
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
        var output = Assert.Single(analysis.Response!.Children);
        Assert.Equal("Hello there", output.Text);
        Assert.Equal(3, output.Tokens);
        Assert.Equal(Categories.OutputText, output.Category);
    }

    [Fact]
    public void OpenAI_chat_request_maps_tool_results_to_their_calls()
    {
        var analysis = ExchangeAnalysis.Analyze(Log("""
            {"model":"gpt","messages":[
              {"role":"system","content":"Be brief."},
              {"role":"user","content":[{"type":"text","text":"Weather?"}]},
              {"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Oslo\"}"}}]},
              {"role":"tool","tool_call_id":"call_1","content":"Sunny"}
            ],"tools":[{"type":"function","function":{"name":"get_weather","parameters":{}}}]}
            """, usage: null, target: "/openai/v1/chat/completions", userAgent: "test-agent"));

        Assert.Equal("OpenAI Chat Completions", analysis.Provider!.Name);
        var categories = analysis.InputBreakdown.ToDictionary(c => c.Name, c => c.Items.Select(i => i.Label).ToList());
        Assert.Equal(["#1 system"], categories[Categories.SystemPrompt]);
        Assert.Equal(["get_weather"], categories[Categories.ToolResults]);
        Assert.Equal(["get_weather"], categories[Categories.ToolCalls]);
        Assert.Equal(["get_weather"], categories[Categories.Tools]);
    }

    [Fact]
    public void OpenAI_responses_request_reads_instructions_and_items()
    {
        var analysis = ExchangeAnalysis.Analyze(Log("""
            {"model":"gpt","instructions":"You are Codex.","input":[
              {"type":"message","role":"developer","content":[{"type":"input_text","text":"Sandbox rules"}]},
              {"type":"message","role":"user","content":[{"type":"input_text","text":"fix it"}]},
              {"type":"reasoning","summary":[{"type":"summary_text","text":"Thinking"}],"encrypted_content":"AAAAAAAAAAAAAAAAAAAA"},
              {"type":"function_call","name":"shell","call_id":"c1","arguments":"{\"cmd\":[\"ls\"]}"},
              {"type":"function_call_output","call_id":"c1","output":"a.txt"}
            ]}
            """, usage: null, target: "/openai/v1/responses", userAgent: "codex_cli_rs/0.1"));

        Assert.Equal("OpenAI Responses", analysis.Provider!.Name);
        var categories = analysis.InputBreakdown.ToDictionary(c => c.Name, c => c.Items.Select(i => i.Label).ToList());
        Assert.Contains("#1 developer", categories[Categories.SystemPrompt]);
        Assert.Equal(["shell"], categories[Categories.ToolResults]);
        Assert.Equal(["#3 reasoning"], categories[Categories.Thinking]);
    }

    [Fact]
    public void Unknown_exchanges_get_no_provider_view()
    {
        var analysis = ExchangeAnalysis.Analyze(Log("""{"hello":1}""", usage: null, target: "/anthropic/api/hello"));

        Assert.Null(analysis.Provider);
        Assert.Null(analysis.Request);
        Assert.Empty(analysis.InputBreakdown);
    }

    [Theory]
    [InlineData(200, 100, 27)]
    [InlineData(1568, 1568, 1534)] // scaled to ~1.15 MP
    [InlineData(4000, 1000, 820)] // long edge scaled to 1568
    public void Image_tokens_follow_the_pixel_formula(int width, int height, long expected) =>
        Assert.Equal(expected, MediaEstimator.ImageTokens(width, height));

    [Fact]
    public void Image_size_is_read_from_png_gif_and_jpeg_headers()
    {
        Assert.True(MediaEstimator.TryReadSize(Convert.FromBase64String(Png(640, 480)), out var w, out var h));
        Assert.Equal((640, 480), (w, h));

        byte[] gif = [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 0x20, 0x00, 0x10, 0x00];
        Assert.True(MediaEstimator.TryReadSize(gif, out w, out h));
        Assert.Equal((32, 16), (w, h));

        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x01, 0x2C, 0x02, 0x58, 0x03];
        Assert.True(MediaEstimator.TryReadSize(jpeg, out w, out h));
        Assert.Equal((600, 300), (w, h));
    }

    private static string Png(int width, int height)
    {
        var bytes = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        return Convert.ToBase64String(bytes);
    }

    private static ExchangeLog Log(string requestJson, string? usage, string target = "/anthropic/v1/messages?beta=true", string userAgent = "claude-cli/2.1.0 (external, cli)") => new()
    {
        Id = "2026-01-01T00-00-00.000Z_0001_POST_v1-messages",
        Sequence = 1,
        StartedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        CompletedAt = DateTimeOffset.Parse("2026-01-01T00:00:01Z"),
        DurationMs = 1000,
        Route = target.Split('/')[1],
        UpstreamUrl = "https://upstream.example" + target,
        Outcome = ExchangeOutcome.Completed,
        Request = new LoggedRequest
        {
            Method = "POST",
            Target = target,
            Headers = new() { ["User-Agent"] = userAgent },
            Body = new LoggedBody { Format = BodyFormat.Json, SizeBytes = requestJson.Length, Content = Parse(requestJson) },
        },
        Response = new LoggedResponse
        {
            StatusCode = 200,
            Headers = new() { ["Content-Type"] = "application/json" },
            Body = new LoggedBody
            {
                Format = BodyFormat.Json,
                SizeBytes = 10,
                Content = Parse(usage is null ? """{"content":[]}""" : $$"""{"type":"message","content":[],"usage":{{usage}}}"""),
            },
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
            Body = new LoggedBody { Format = BodyFormat.Text, SizeBytes = sse.Length, Content = JsonSerializer.SerializeToElement(sse, AiLogJsonContext.Default.String) },
        },
    };

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}

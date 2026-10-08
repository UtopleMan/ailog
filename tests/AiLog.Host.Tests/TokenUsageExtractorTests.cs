using System.Text.Json;
using AiLog.Contracts;

namespace AiLog.Host.Tests;

public sealed class TokenUsageExtractorTests
{
    [Fact]
    public void Anthropic_json_totals_input_including_cache_reads_and_writes()
    {
        var usage = Extract(Json("""{"type":"message","usage":{"input_tokens":1204,"cache_creation_input_tokens":100,"cache_read_input_tokens":48000,"output_tokens":356}}"""));

        Assert.Equal(49304, usage!.InputTokens);
        Assert.Equal(356, usage.OutputTokens);
        Assert.Equal(48000, usage.CacheReadTokens);
        Assert.Equal(100, usage.CacheWriteTokens);
    }

    [Fact]
    public void Anthropic_sse_takes_input_from_message_start_and_output_from_the_last_message_delta()
    {
        var usage = Extract(Sse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":10,"cache_creation_input_tokens":0,"cache_read_input_tokens":5000,"output_tokens":1}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hi \"usage\""}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":42}}

            event: message_stop
            data: {"type":"message_stop"}

            """));

        Assert.Equal(5010, usage!.InputTokens);
        Assert.Equal(42, usage.OutputTokens);
        Assert.Equal(5000, usage.CacheReadTokens);
    }

    [Fact]
    public void Aborted_anthropic_stream_reports_what_arrived()
    {
        var usage = Extract(Sse("""
            event: message_start
            data: {"type":"message_start","message":{"usage":{"input_tokens":20,"output_tokens":1}}}

            """));

        Assert.Equal(20, usage!.InputTokens);
        Assert.Equal(1, usage.OutputTokens);
    }

    [Fact]
    public void OpenAI_chat_json_prompt_tokens_already_include_cached_tokens()
    {
        var usage = Extract(Json("""{"object":"chat.completion","usage":{"prompt_tokens":523,"completion_tokens":88,"total_tokens":611,"prompt_tokens_details":{"cached_tokens":500}}}"""));

        Assert.Equal(523, usage!.InputTokens);
        Assert.Equal(88, usage.OutputTokens);
        Assert.Equal(500, usage.CacheReadTokens);
        Assert.Null(usage.CacheWriteTokens);
    }

    [Fact]
    public void OpenAI_chat_sse_reads_the_final_usage_chunk_and_ignores_done()
    {
        var usage = Extract(Sse("""
            data: {"object":"chat.completion.chunk","choices":[{"delta":{"content":"hi"}}],"usage":null}

            data: {"object":"chat.completion.chunk","choices":[],"usage":{"prompt_tokens":7,"completion_tokens":3}}

            data: [DONE]

            """));

        Assert.Equal(7, usage!.InputTokens);
        Assert.Equal(3, usage.OutputTokens);
    }

    [Fact]
    public void OpenAI_chat_sse_without_include_usage_has_no_usage() =>
        Assert.Null(Extract(Sse("""
            data: {"object":"chat.completion.chunk","choices":[{"delta":{"content":"hi"}}]}

            data: [DONE]

            """)));

    [Fact]
    public void OpenAI_responses_json()
    {
        var usage = Extract(Json("""{"object":"response","usage":{"input_tokens":152000,"input_tokens_details":{"cached_tokens":150000},"output_tokens":12034,"output_tokens_details":{"reasoning_tokens":9000}}}"""));

        Assert.Equal(152000, usage!.InputTokens);
        Assert.Equal(12034, usage.OutputTokens);
        Assert.Equal(150000, usage.CacheReadTokens);
    }

    [Fact]
    public void OpenAI_responses_sse_reads_response_completed()
    {
        var usage = Extract(Sse("""
            event: response.created
            data: {"type":"response.created","response":{"usage":null}}

            event: response.completed
            data: {"type":"response.completed","response":{"usage":{"input_tokens":30,"input_tokens_details":{"cached_tokens":0},"output_tokens":12,"output_tokens_details":{"reasoning_tokens":0}}}}

            """));

        Assert.Equal(30, usage!.InputTokens);
        Assert.Equal(12, usage.OutputTokens);
    }

    [Fact]
    public void Responses_without_usage_have_none()
    {
        Assert.Null(Extract(Json("""{"error":{"type":"not_found"}}""")));
        Assert.Null(TokenUsageExtractor.Extract(null));
        Assert.Null(TokenUsageExtractor.Extract(new LoggedResponse { StatusCode = 204, Headers = [] }));
    }

    [Fact]
    public void Text_that_is_not_an_event_stream_is_ignored()
    {
        var response = new LoggedResponse
        {
            StatusCode = 200,
            Headers = new() { ["Content-Type"] = "text/plain" },
            Body = new LoggedBody { Format = BodyFormat.Text, SizeBytes = 1, Content = JsonSerializer.SerializeToElement("""data: {"usage":{"input_tokens":1}}""", AiLogJsonContext.Default.String) },
        };

        Assert.Null(TokenUsageExtractor.Extract(response));
    }

    private static TokenUsage? Extract(LoggedResponse response) => TokenUsageExtractor.Extract(response);

    private static LoggedResponse Json(string json) => new()
    {
        StatusCode = 200,
        Headers = new() { ["Content-Type"] = "application/json" },
        Body = new LoggedBody { Format = BodyFormat.Json, SizeBytes = json.Length, Content = JsonDocument.Parse(json).RootElement.Clone() },
    };

    private static LoggedResponse Sse(string text) => new()
    {
        StatusCode = 200,
        Headers = new() { ["Content-Type"] = "text/event-stream; charset=utf-8" },
        Body = new LoggedBody { Format = BodyFormat.Text, SizeBytes = text.Length, Content = JsonSerializer.SerializeToElement(text, AiLogJsonContext.Default.String) },
    };
}

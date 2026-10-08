using System.Text.Json;
using AiLog.Shared;
using AiLog.Shared.Providers;
using Xunit.Sdk;

namespace AiLog.Host.Tests;

public sealed class ReassemblyTests
{
    [Fact]
    public void Anthropic_stream_folds_into_the_final_message()
    {
        JsonElement message = Reassemble(new AnthropicMessagesAdapter(), """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-x","content":[],"stop_reason":null,"usage":{"input_tokens":10,"output_tokens":1}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Let me "}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"think."}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

            event: ping
            data: {"type": "ping"}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Hello "}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"world"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":1}

            event: content_block_start
            data: {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_1","name":"Bash","input":{}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"command\": "}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"\"ls\"}"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":2}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":42}}

            event: message_stop
            data: {"type":"message_stop"}

            """);

        Assert.Equal("msg_1", message.GetProperty("id").GetString());
        Assert.Equal("tool_use", message.GetProperty("stop_reason").GetString());
        Assert.Equal(42, message.GetProperty("usage").GetProperty("output_tokens").GetInt32());
        Assert.Equal(10, message.GetProperty("usage").GetProperty("input_tokens").GetInt32());

        JsonElement content = message.GetProperty("content");
        Assert.Equal(3, content.GetArrayLength());
        Assert.Equal("Let me think.", content[0].GetProperty("thinking").GetString());
        Assert.Equal("sig", content[0].GetProperty("signature").GetString());
        Assert.Equal("Hello world", content[1].GetProperty("text").GetString());
        Assert.Equal("ls", content[2].GetProperty("input").GetProperty("command").GetString());
    }

    [Fact]
    public void Aborted_anthropic_stream_keeps_what_arrived()
    {
        JsonElement message = Reassemble(new AnthropicMessagesAdapter(), """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_2","content":[],"usage":{"input_tokens":5}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Partial"}}
            """);

        Assert.Equal("Partial", message.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void OpenAI_chat_stream_folds_into_a_chat_completion()
    {
        JsonElement completion = Reassemble(new OpenAiChatAdapter(), """
            data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"gpt-x","choices":[{"index":0,"delta":{"role":"assistant","content":""}}]}

            data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"gpt-x","choices":[{"index":0,"delta":{"content":"Hi "}}]}

            data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"gpt-x","choices":[{"index":0,"delta":{"content":"there"}}]}

            data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"gpt-x","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"get_weather","arguments":""}}]}}]}

            data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"gpt-x","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"city\":"}}]}}]}

            data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"gpt-x","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"Oslo\"}"}}]}}]}

            data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"gpt-x","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

            data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"gpt-x","choices":[],"usage":{"prompt_tokens":7,"completion_tokens":3}}

            data: [DONE]

            """);

        Assert.Equal("chat.completion", completion.GetProperty("object").GetString());
        Assert.Equal(7, completion.GetProperty("usage").GetProperty("prompt_tokens").GetInt32());
        JsonElement choice = completion.GetProperty("choices")[0];
        Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());
        JsonElement message = choice.GetProperty("message");
        Assert.Equal("assistant", message.GetProperty("role").GetString());
        Assert.Equal("Hi there", message.GetProperty("content").GetString());
        JsonElement call = message.GetProperty("tool_calls")[0];
        Assert.Equal("call_1", call.GetProperty("id").GetString());
        Assert.Equal("get_weather", call.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("""{"city":"Oslo"}""", call.GetProperty("function").GetProperty("arguments").GetString());
    }

    [Fact]
    public void OpenAI_responses_stream_uses_the_completed_response()
    {
        JsonElement response = Reassemble(new OpenAiResponsesAdapter(), """
            event: response.created
            data: {"type":"response.created","response":{"id":"resp_1","status":"in_progress","output":[]}}

            event: response.output_text.delta
            data: {"type":"response.output_text.delta","output_index":0,"content_index":0,"delta":"ignored"}

            event: response.completed
            data: {"type":"response.completed","response":{"id":"resp_1","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Done"}]}]}}

            """);

        Assert.Equal("completed", response.GetProperty("status").GetString());
        Assert.Equal("Done", response.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Aborted_openai_responses_stream_is_rebuilt_from_items_and_deltas()
    {
        JsonElement response = Reassemble(new OpenAiResponsesAdapter(), """
            event: response.created
            data: {"type":"response.created","response":{"id":"resp_2","status":"in_progress","output":[]}}

            event: response.output_item.added
            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","name":"shell","call_id":"c1","arguments":""}}

            event: response.function_call_arguments.delta
            data: {"type":"response.function_call_arguments.delta","output_index":0,"delta":"{\"cmd\":\"ls\"}"}

            event: response.output_item.done
            data: {"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","name":"shell","call_id":"c1","arguments":"{\"cmd\":\"ls\"}"}}

            event: response.output_item.added
            data: {"type":"response.output_item.added","output_index":1,"item":{"type":"message","role":"assistant","content":[]}}

            event: response.output_text.delta
            data: {"type":"response.output_text.delta","output_index":1,"content_index":0,"delta":"Half a "}

            event: response.output_text.delta
            data: {"type":"response.output_text.delta","output_index":1,"content_index":0,"delta":"sentence"}
            """);

        JsonElement output = response.GetProperty("output");
        Assert.Equal("""{"cmd":"ls"}""", output[0].GetProperty("arguments").GetString());
        Assert.Equal("Half a sentence", output[1].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Streams_in_another_format_are_not_reassembled() =>
        Assert.Null(new AnthropicMessagesAdapter().Reassemble(Sse.Parse("""
            data: [DONE]


            """)));

    private static JsonElement Reassemble(IProviderAdapter adapter, string sse) =>
        adapter.Reassemble(Sse.Parse(sse)) ?? throw new XunitException("nothing reassembled");
}

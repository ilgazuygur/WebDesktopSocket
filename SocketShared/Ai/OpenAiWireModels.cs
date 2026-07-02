using System.Text.Json.Serialization;

namespace SocketShared.Ai;

// These classes mirror the JSON shape of the OpenAI "chat completions" API
// (https://platform.openai.com/docs/api-reference/chat), which is also
// what most OpenAI-compatible providers (OpenRouter, local Ollama/LM
// Studio "OpenAI compatible" servers, etc.) accept and return. They are
// intentionally minimal - only the fields OpenAiCompatibleClient actually
// needs - and are not meant to be used anywhere outside this file.

internal sealed class OpenAiChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<OpenAiChatRequestMessage> Messages { get; set; } = new();
}

internal sealed class OpenAiChatRequestMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

internal sealed class OpenAiChatResponse
{
    [JsonPropertyName("choices")]
    public List<OpenAiChatChoice>? Choices { get; set; }
}

internal sealed class OpenAiChatChoice
{
    [JsonPropertyName("message")]
    public OpenAiChatResponseMessage? Message { get; set; }
}

internal sealed class OpenAiChatResponseMessage
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

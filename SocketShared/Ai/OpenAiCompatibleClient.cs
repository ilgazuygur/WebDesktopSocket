using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SocketShared.Protocol;

namespace SocketShared.Ai;

// A single IAiClient implementation that works with any provider exposing
// an OpenAI-compatible POST {BaseUrl}/chat/completions endpoint (OpenAI
// itself, OpenRouter, a local Ollama/LM Studio server, etc.). Which
// provider it actually talks to is entirely controlled by AiOptions
// (BaseUrl/Model/ApiKey), so switching providers is a configuration
// change, not a code change.
//
// This class never logs anything - in particular it never writes
// AiOptions.ApiKey anywhere, including in exception messages.
public sealed class OpenAiCompatibleClient : IAiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly AiOptions _options;

    // The HttpClient is injected (e.g. from IHttpClientFactory in the WPF
    // host) rather than created here, so its lifetime, timeout and any
    // handler pipeline (retries, proxies, ...) stay under the host's
    // control instead of being hidden inside this class.
    public OpenAiCompatibleClient(HttpClient httpClient, AiOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<string> CompleteAsync(IReadOnlyList<ConversationTurn> messages, CancellationToken cancellationToken = default)
    {
        var requestBody = new OpenAiChatRequest
        {
            Model = _options.Model,
            Messages = messages
                .Select(turn => new OpenAiChatRequestMessage { Role = turn.Role, Content = turn.Content })
                .ToList()
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpointUri())
        {
            // Built by hand with StringContent (rather than the
            // System.Net.Http.Json helpers) so SocketShared doesn't need
            // any extra NuGet package beyond the base class library.
            Content = new StringContent(JsonSerializer.Serialize(requestBody, JsonOptions), Encoding.UTF8, "application/json")
        };

        // Set per-request rather than on the shared HttpClient, since a
        // client from IHttpClientFactory may be reused across requests
        // with different options.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        using var response = await SendAsync(request, cancellationToken);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AiAuthenticationException("The AI API rejected the configured API key.");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new AiRequestException((int)response.StatusCode, $"AI API returned {(int)response.StatusCode}: {Truncate(body)}");
        }

        return ExtractReplyText(body);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout expired. In that case HttpClient throws
            // OperationCanceledException even though the caller's own
            // token was never cancelled - that's how we tell "we gave up
            // waiting" apart from "the caller asked us to stop".
            throw new AiTimeoutException("The AI API did not respond within the configured timeout.", ex);
        }
        catch (OperationCanceledException ex)
        {
            throw new AiTimeoutException("The AI request was cancelled before it completed.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AiRequestException(0, $"Could not reach the AI API: {ex.Message}");
        }
    }

    private Uri BuildEndpointUri()
    {
        var baseUrl = _options.BaseUrl.TrimEnd('/');
        return new Uri($"{baseUrl}/chat/completions");
    }

    private static string ExtractReplyText(string body)
    {
        OpenAiChatResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<OpenAiChatResponse>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new AiInvalidResponseException("The AI API response was not valid JSON.", ex);
        }

        var content = parsed?.Choices?.FirstOrDefault()?.Message?.Content;

        if (string.IsNullOrEmpty(content))
        {
            throw new AiInvalidResponseException("The AI API response did not contain any reply text.");
        }

        return content;
    }

    private static string Truncate(string text, int maxLength = 500)
    {
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }
}

namespace SocketShared.Ai;

// Configuration for talking to an AI API. All three values are meant to
// be configurable rather than hardcoded, so the exact provider can change
// without touching code - only these settings.
//
// BaseUrl and Model are not secret and can live in an appsettings.json
// file. ApiKey IS secret and must come from an environment variable or
// another local secret store (e.g. `dotnet user-secrets`) - never from a
// committed file. See README.md / .env.example for setup instructions.
public class AiOptions
{
    // e.g. "https://api.openai.com/v1" - the API is expected to expose an
    // OpenAI-compatible POST {BaseUrl}/chat/completions endpoint.
    public string BaseUrl { get; set; } = string.Empty;

    // e.g. "gpt-4o-mini" - left blank here; set via configuration.
    public string Model { get; set; } = string.Empty;

    // Sent as "Authorization: Bearer {ApiKey}". Never log this value.
    public string ApiKey { get; set; } = string.Empty;
}

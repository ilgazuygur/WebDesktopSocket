namespace SocketShared.Ai;

// Base type for every error an IAiClient can throw. Callers (the desktop
// client's AiRequest handler) can catch this one type to know "something
// went wrong talking to the AI", and inspect the concrete subtype for detail.
//
// None of these exceptions ever include the API key in their message -
// only the ApiKey field on AiOptions holds that value, and it is never
// read by any of the code that builds these messages.
public abstract class AiClientException : Exception
{
    protected AiClientException(string message, Exception? inner = null) : base(message, inner) { }
}

// The AI API rejected our API key (HTTP 401/403).
public sealed class AiAuthenticationException : AiClientException
{
    public AiAuthenticationException(string message) : base(message) { }
}

// The request did not complete before the configured timeout, or the
// caller's CancellationToken was triggered.
public sealed class AiTimeoutException : AiClientException
{
    public AiTimeoutException(string message, Exception? inner = null) : base(message, inner) { }
}

// The AI API returned a non-success status code that wasn't an auth
// failure (e.g. 400, 429, 500).
public sealed class AiRequestException : AiClientException
{
    public int StatusCode { get; }

    public AiRequestException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}

// The AI API returned a 2xx response, but the body wasn't valid JSON, or
// didn't contain the fields we expected (e.g. no choices/content).
public sealed class AiInvalidResponseException : AiClientException
{
    public AiInvalidResponseException(string message, Exception? inner = null) : base(message, inner) { }
}

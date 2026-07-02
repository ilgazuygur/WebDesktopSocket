namespace Socket.Tests.Ai;

// A minimal HttpMessageHandler double so OpenAiCompatibleClientTests can
// control exactly what "the AI API" returns (or how it fails) without any
// real network call. The last request sent is captured so tests can also
// assert on what OpenAiCompatibleClient sent (e.g. the Authorization
// header, or the request body).
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        _respond = respond;
    }

    public static StubHttpMessageHandler ReturningJson(System.Net.HttpStatusCode statusCode, string json)
    {
        return new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        }));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return await _respond(request, cancellationToken);
    }
}

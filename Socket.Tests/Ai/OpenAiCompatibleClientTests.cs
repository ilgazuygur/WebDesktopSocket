using System.Net;
using SocketShared.Ai;
using SocketShared.Protocol;

namespace Socket.Tests.Ai;

// Exercises OpenAiCompatibleClient against a stubbed HTTP handler, so no
// real AI API or network access is needed to verify: success, auth
// failure, timeout, other non-success statuses, and invalid JSON bodies -
// the full set of failure modes the client is expected to handle.
public class OpenAiCompatibleClientTests
{
    private const string DummyApiKey = "test-api-key-do-not-log";

    private static readonly List<ConversationTurn> SampleHistory = new()
    {
        new ConversationTurn { Role = "user", Content = "Hello" }
    };

    [Fact]
    public async Task CompleteAsync_OnSuccess_ReturnsAssistantReplyText()
    {
        var handler = StubHttpMessageHandler.ReturningJson(HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"Hi there!"}}]}""");
        var client = CreateClient(handler);

        var reply = await client.CompleteAsync(SampleHistory);

        Assert.Equal("Hi there!", reply);
    }

    [Fact]
    public async Task CompleteAsync_SendsBearerAuthorizationHeader()
    {
        var handler = StubHttpMessageHandler.ReturningJson(HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""");
        var client = CreateClient(handler);

        await client.CompleteAsync(SampleHistory);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization?.Scheme);
        Assert.Equal(DummyApiKey, handler.LastRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task CompleteAsync_PostsToBaseUrlPlusChatCompletions()
    {
        var handler = StubHttpMessageHandler.ReturningJson(HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""");
        var client = CreateClient(handler);

        await client.CompleteAsync(SampleHistory);

        Assert.Equal("https://example-ai.test/v1/chat/completions", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task CompleteAsync_SendsModelAndHistoryInBody()
    {
        var handler = StubHttpMessageHandler.ReturningJson(HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""");
        var client = CreateClient(handler);

        await client.CompleteAsync(SampleHistory);

        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("\"model\":\"test-model\"", handler.LastRequestBody);
        Assert.Contains("\"content\":\"Hello\"", handler.LastRequestBody);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task CompleteAsync_OnAuthFailure_ThrowsAiAuthenticationException(HttpStatusCode statusCode)
    {
        var handler = StubHttpMessageHandler.ReturningJson(statusCode, """{"error":"invalid api key"}""");
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<AiAuthenticationException>(() => client.CompleteAsync(SampleHistory));

        Assert.DoesNotContain(DummyApiKey, ex.Message);
    }

    [Fact]
    public async Task CompleteAsync_OnServerError_ThrowsAiRequestExceptionWithStatusCode()
    {
        var handler = StubHttpMessageHandler.ReturningJson(HttpStatusCode.InternalServerError, """{"error":"boom"}""");
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<AiRequestException>(() => client.CompleteAsync(SampleHistory));

        Assert.Equal(500, ex.StatusCode);
    }

    [Fact]
    public async Task CompleteAsync_OnMalformedJsonBody_ThrowsAiInvalidResponseException()
    {
        var handler = StubHttpMessageHandler.ReturningJson(HttpStatusCode.OK, "this is not json");
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<AiInvalidResponseException>(() => client.CompleteAsync(SampleHistory));
    }

    [Fact]
    public async Task CompleteAsync_OnEmptyChoices_ThrowsAiInvalidResponseException()
    {
        var handler = StubHttpMessageHandler.ReturningJson(HttpStatusCode.OK, """{"choices":[]}""");
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<AiInvalidResponseException>(() => client.CompleteAsync(SampleHistory));
    }

    [Fact]
    public async Task CompleteAsync_WhenHttpClientTimesOut_ThrowsAiTimeoutException()
    {
        var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            // Wait far longer than the HttpClient timeout below, so the
            // client gives up and throws before this ever completes.
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) };
        var client = new OpenAiCompatibleClient(httpClient, SampleOptions());

        await Assert.ThrowsAsync<AiTimeoutException>(() => client.CompleteAsync(SampleHistory));
    }

    [Fact]
    public async Task CompleteAsync_WhenCallerCancels_ThrowsAiTimeoutException()
    {
        var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var client = CreateClient(handler);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<AiTimeoutException>(() => client.CompleteAsync(SampleHistory, cts.Token));
    }

    private static OpenAiCompatibleClient CreateClient(StubHttpMessageHandler handler)
    {
        return new OpenAiCompatibleClient(new HttpClient(handler), SampleOptions());
    }

    private static AiOptions SampleOptions() => new()
    {
        BaseUrl = "https://example-ai.test/v1",
        Model = "test-model",
        ApiKey = DummyApiKey
    };
}

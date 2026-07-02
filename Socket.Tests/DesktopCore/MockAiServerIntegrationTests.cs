using MockAiServer;
using SocketShared.Ai;
using SocketShared.Protocol;

namespace Socket.Tests.DesktopCore;

// Points the REAL OpenAiCompatibleClient (the production AI client) at the
// deterministic MockOpenAiServer, over a real in-process HTTP connection.
// This proves the actual client-to-provider path works end to end without
// any real API key or external network - the same mock the local/CI
// end-to-end runs use.
public class MockAiServerIntegrationTests
{
    [Fact]
    public async Task OpenAiCompatibleClient_AgainstMockServer_ReturnsTheDeterministicReply()
    {
        const string reply = "MOCK_AI_REPLY: hello from the mock";
        using var server = MockOpenAiServer.Start(MockOpenAiServer.FindFreePort(), reply);

        var client = new OpenAiCompatibleClient(new HttpClient(), new AiOptions
        {
            BaseUrl = server.BaseUrl,
            Model = "mock-model",
            ApiKey = "any-nonempty-value" // ignored by the mock; never a real key
        });

        var result = await client.CompleteAsync(new List<ConversationTurn>
        {
            new() { Role = "user", Content = "Hello" }
        });

        Assert.Equal(reply, result);
        Assert.Equal(1, server.RequestCount);
    }
}

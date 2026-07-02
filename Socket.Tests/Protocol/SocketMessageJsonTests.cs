using System.Text.Json;
using SocketShared.Protocol;

namespace Socket.Tests.Protocol;

// SocketMessage is the one contract SocketWeb and SocketDesktop agree on
// over the wire. These tests lock down that JSON.Serialize/Deserialize
// round-trips every field correctly for each MessageType, and that the
// enums serialize as readable strings (not numbers), which matters for
// debugging raw WebSocket traffic.
public class SocketMessageJsonTests
{
    [Fact]
    public void ClientHello_RoundTrips_RoleAndType()
    {
        var original = new SocketMessage
        {
            Type = MessageType.ClientHello,
            Role = ClientRole.Desktop
        };

        var roundTripped = RoundTrip(original);

        Assert.Equal(MessageType.ClientHello, roundTripped.Type);
        Assert.Equal(ClientRole.Desktop, roundTripped.Role);
    }

    [Fact]
    public void UserPrompt_RoundTrips_SessionRequestAndContent()
    {
        var original = new SocketMessage
        {
            Type = MessageType.UserPrompt,
            SessionId = "session-123",
            RequestId = "request-456",
            ConnectionId = "conn-789",
            Content = "Hello, AI!",
            MessageRole = "user"
        };

        var roundTripped = RoundTrip(original);

        Assert.Equal(MessageType.UserPrompt, roundTripped.Type);
        Assert.Equal("session-123", roundTripped.SessionId);
        Assert.Equal("request-456", roundTripped.RequestId);
        Assert.Equal("conn-789", roundTripped.ConnectionId);
        Assert.Equal("Hello, AI!", roundTripped.Content);
        Assert.Equal("user", roundTripped.MessageRole);
    }

    [Fact]
    public void AiRequest_RoundTrips_ConversationHistory()
    {
        var original = new SocketMessage
        {
            Type = MessageType.AiRequest,
            SessionId = "session-123",
            RequestId = "request-456",
            History = new List<ConversationTurn>
            {
                new() { Role = "user", Content = "What is 2+2?" },
                new() { Role = "assistant", Content = "4" },
                new() { Role = "user", Content = "And 3+3?" }
            }
        };

        var roundTripped = RoundTrip(original);

        Assert.Equal(MessageType.AiRequest, roundTripped.Type);
        Assert.NotNull(roundTripped.History);
        Assert.Equal(3, roundTripped.History!.Count);
        Assert.Equal("user", roundTripped.History[0].Role);
        Assert.Equal("What is 2+2?", roundTripped.History[0].Content);
        Assert.Equal("assistant", roundTripped.History[1].Role);
        Assert.Equal("And 3+3?", roundTripped.History[2].Content);
    }

    [Fact]
    public void AiResponse_RoundTrips_Content()
    {
        var original = new SocketMessage
        {
            Type = MessageType.AiResponse,
            SessionId = "session-123",
            RequestId = "request-456",
            Content = "The answer is 4.",
            MessageRole = "assistant"
        };

        var roundTripped = RoundTrip(original);

        Assert.Equal(MessageType.AiResponse, roundTripped.Type);
        Assert.Equal("The answer is 4.", roundTripped.Content);
        Assert.Equal("assistant", roundTripped.MessageRole);
    }

    [Fact]
    public void Status_RoundTrips_Content()
    {
        var original = new SocketMessage
        {
            Type = MessageType.Status,
            SessionId = "session-123",
            Content = "AI is thinking"
        };

        var roundTripped = RoundTrip(original);

        Assert.Equal(MessageType.Status, roundTripped.Type);
        Assert.Equal("AI is thinking", roundTripped.Content);
    }

    [Fact]
    public void Error_RoundTrips_RequestIdAndErrorDetail()
    {
        var original = new SocketMessage
        {
            Type = MessageType.Error,
            RequestId = "request-456",
            Error = "Desktop AI client offline"
        };

        var roundTripped = RoundTrip(original);

        Assert.Equal(MessageType.Error, roundTripped.Type);
        Assert.Equal("request-456", roundTripped.RequestId);
        Assert.Equal("Desktop AI client offline", roundTripped.Error);
    }

    [Theory]
    [InlineData(MessageType.ClientHello, "\"ClientHello\"")]
    [InlineData(MessageType.HelloAck, "\"HelloAck\"")]
    [InlineData(MessageType.UserPrompt, "\"UserPrompt\"")]
    [InlineData(MessageType.AiRequest, "\"AiRequest\"")]
    [InlineData(MessageType.AiResponse, "\"AiResponse\"")]
    [InlineData(MessageType.Status, "\"Status\"")]
    [InlineData(MessageType.Error, "\"Error\"")]
    public void MessageType_SerializesAsReadableString(MessageType type, string expectedJsonFragment)
    {
        var json = JsonSerializer.Serialize(new SocketMessage { Type = type });

        Assert.Contains($"\"Type\":{expectedJsonFragment}", json);
    }

    [Fact]
    public void ClientRole_SerializesAsReadableString()
    {
        var json = JsonSerializer.Serialize(new SocketMessage { Type = MessageType.ClientHello, Role = ClientRole.Browser });

        Assert.Contains("\"Role\":\"Browser\"", json);
    }

    private static SocketMessage RoundTrip(SocketMessage message)
    {
        var json = JsonSerializer.Serialize(message);
        var result = JsonSerializer.Deserialize<SocketMessage>(json);
        Assert.NotNull(result);
        return result!;
    }
}

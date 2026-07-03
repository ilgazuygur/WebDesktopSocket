using SocketDesktop.Core;
using SocketShared.Ai;

namespace Socket.Tests.DesktopCore;

public class DesktopClientOptionsTests
{
    private static DesktopClientOptions FullyConfigured() => new()
    {
        SocketUrl = "ws://localhost:5080/ws",
        Ai = new AiOptions { BaseUrl = "https://api.example.test/v1", Model = "test-model", ApiKey = "test-key" }
    };

    [Fact]
    public void DefaultSocketUrl_IsTheLocalSocketWebEndpoint()
    {
        Assert.Equal("ws://localhost:5080/ws", new DesktopClientOptions().SocketUrl);
    }

    [Theory]
    [InlineData("ws://localhost:5080/ws", true)]
    [InlineData("wss://example.com/ws", true)]
    [InlineData("http://localhost:5080/ws", false)]
    [InlineData("not-a-url", false)]
    [InlineData("", false)]
    public void IsSocketUrlValid_OnlyAcceptsWsAndWss(string url, bool expected)
    {
        var options = new DesktopClientOptions { SocketUrl = url };
        Assert.Equal(expected, options.IsSocketUrlValid);
    }

    [Fact]
    public void IsFullyConfigured_TrueWhenUrlValidAndAiComplete()
    {
        Assert.True(FullyConfigured().IsFullyConfigured);
    }

    [Fact]
    public void IsFullyConfigured_FalseWhenApiKeyMissing()
    {
        var options = FullyConfigured();
        options.Ai.ApiKey = "";
        Assert.False(options.IsFullyConfigured);
    }

    [Fact]
    public void DescribeMissingConfiguration_ListsEachMissingPiece_ButNeverTheKeyValue()
    {
        var options = new DesktopClientOptions
        {
            SocketUrl = "http://bad",
            Ai = new AiOptions { BaseUrl = "", Model = "", ApiKey = "" }
        };

        var missing = options.DescribeMissingConfiguration();

        Assert.Contains(missing, m => m.Contains("Socket__Url"));
        Assert.Contains(missing, m => m.Contains("Ai__BaseUrl"));
        Assert.Contains(missing, m => m.Contains("Ai__Model"));
        Assert.Contains(missing, m => m.Contains("AI_API_KEY"));
    }

    [Fact]
    public void DescribeMissingConfiguration_EmptyWhenFullyConfigured()
    {
        Assert.Empty(FullyConfigured().DescribeMissingConfiguration());
    }
}

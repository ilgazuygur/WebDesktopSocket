using SocketShared.Ai;

namespace Socket.Tests.Ai;

public class AiOptionsTests
{
    [Fact]
    public void IsComplete_AllValuesSet_ReturnsTrue()
    {
        var options = new AiOptions { BaseUrl = "https://api.openai.com/v1", Model = "gpt-4o-mini", ApiKey = "sk-test" };

        Assert.True(options.IsComplete);
    }

    [Theory]
    [InlineData("", "gpt-4o-mini", "sk-test")]
    [InlineData("https://api.openai.com/v1", "", "sk-test")]
    [InlineData("https://api.openai.com/v1", "gpt-4o-mini", "")]
    [InlineData("   ", "gpt-4o-mini", "sk-test")]
    public void IsComplete_AnyValueMissing_ReturnsFalse(string baseUrl, string model, string apiKey)
    {
        var options = new AiOptions { BaseUrl = baseUrl, Model = model, ApiKey = apiKey };

        Assert.False(options.IsComplete);
    }

    [Fact]
    public void IsComplete_DefaultOptions_ReturnsFalse()
    {
        var options = new AiOptions();

        Assert.False(options.IsComplete);
    }
}

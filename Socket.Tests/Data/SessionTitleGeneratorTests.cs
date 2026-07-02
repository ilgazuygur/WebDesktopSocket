using SocketWeb.Data;

namespace Socket.Tests.Data;

public class SessionTitleGeneratorTests
{
    [Fact]
    public void FromFirstMessage_ShortMessage_ReturnsItUnchanged()
    {
        var title = SessionTitleGenerator.FromFirstMessage("Hello there");

        Assert.Equal("Hello there", title);
    }

    [Fact]
    public void FromFirstMessage_LongMessage_TruncatesWithEllipsis()
    {
        var longMessage = new string('a', 100);

        var title = SessionTitleGenerator.FromFirstMessage(longMessage);

        Assert.EndsWith("...", title);
        Assert.True(title.Length <= 63); // 60 chars + "..."
    }

    [Fact]
    public void FromFirstMessage_MultiLineMessage_UsesOnlyFirstNonEmptyLine()
    {
        var title = SessionTitleGenerator.FromFirstMessage("\n\n  First real line  \nSecond line");

        Assert.Equal("First real line", title);
    }

    [Fact]
    public void FromFirstMessage_EmptyMessage_ReturnsFallback()
    {
        var title = SessionTitleGenerator.FromFirstMessage("   \n  ");

        Assert.Equal("New chat", title);
    }
}

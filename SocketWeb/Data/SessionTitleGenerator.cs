namespace SocketWeb.Data;

// Turns the first user message of a session into a short, readable title
// for the sidebar - similar to how ChatGPT names a new chat after your
// first message, when you haven't renamed it yourself.
public static class SessionTitleGenerator
{
    private const int MaxLength = 60;

    public static string FromFirstMessage(string content)
    {
        var firstLine = content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0) ?? string.Empty;

        if (firstLine.Length == 0)
        {
            return "New chat";
        }

        return firstLine.Length <= MaxLength
            ? firstLine
            : firstLine[..MaxLength].TrimEnd() + "...";
    }
}

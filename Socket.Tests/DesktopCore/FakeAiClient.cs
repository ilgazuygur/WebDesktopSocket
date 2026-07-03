using SocketShared.Ai;
using SocketShared.Protocol;

namespace Socket.Tests.DesktopCore;

// A stand-in IAiClient for DesktopSocketClient tests. By default returns a
// fixed reply; a test can swap in its own handler (e.g. to throw a
// specific AiClientException, or to record the history it was given).
internal sealed class FakeAiClient : IAiClient
{
    private int _callCount;
    private readonly object _gate = new();
    private readonly List<IReadOnlyList<ConversationTurn>> _histories = new();

    public Func<IReadOnlyList<ConversationTurn>, CancellationToken, Task<string>> Handler { get; set; }
        = (_, _) => Task.FromResult("Fixed test reply");

    public int CallCount => Volatile.Read(ref _callCount);

    public IReadOnlyList<IReadOnlyList<ConversationTurn>> ReceivedHistories
    {
        get { lock (_gate) { return _histories.ToList(); } }
    }

    public Task<string> CompleteAsync(IReadOnlyList<ConversationTurn> messages, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);
        lock (_gate)
        {
            _histories.Add(messages);
        }
        return Handler(messages, cancellationToken);
    }
}

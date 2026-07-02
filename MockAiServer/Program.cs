using MockAiServer;

// Standalone entrypoint so the mock can be run as its own process during
// local/CI end-to-end runs. Configure via args or environment:
//   MOCK_PORT  (or first CLI arg)  - port to listen on (default 5099)
//   MOCK_REPLY                       - the fixed assistant reply to return
//
// The desktop app is then pointed at it with, e.g.:
//   Ai__BaseUrl = http://localhost:5099/v1
//   Ai__Model   = mock-model
//   AI_API_KEY  = anything-nonempty  (ignored by the mock)

var port = 5099;
if (args.Length > 0 && int.TryParse(args[0], out var argPort))
{
    port = argPort;
}
else if (int.TryParse(Environment.GetEnvironmentVariable("MOCK_PORT"), out var envPort))
{
    port = envPort;
}

var reply = Environment.GetEnvironmentVariable("MOCK_REPLY")
    ?? "MOCK_AI_REPLY: the deterministic mock assistant is working.";

using var server = MockOpenAiServer.Start(port, reply);
Console.WriteLine($"Mock OpenAI-compatible server listening on {server.BaseUrl}");
Console.WriteLine("POST /v1/chat/completions returns a fixed reply. Press Ctrl+C to stop.");

var stopped = new TaskCompletionSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stopped.TrySetResult(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stopped.TrySetResult();
await stopped.Task;

using System.Text;
using AiHarnessDemo.Core.Reasoning;

namespace AiHarnessDemo.Services;

/// <summary>Incrementally tails only the bound session; never replays pre-resume activity.</summary>
internal sealed class CopilotActivityJournal(
    string home,
    Guid requestedSessionId,
    string workspace,
    AgentExecutionMonitor monitor,
    ILogger logger) : IAsyncDisposable
{
    private readonly DateTimeOffset launchedAt = DateTimeOffset.UtcNow;
    private readonly long initialOffset = ExistingLength(home, requestedSessionId);
    private readonly StringBuilder pending = new();
    private StreamReader? reader;
    private Guid? openedSessionId;
    private bool unavailableReported;

    public async Task PollAsync(CancellationToken cancellationToken)
    {
        var sessionId = monitor.ConfirmedSessionId ?? requestedSessionId;
        try
        {
            if (reader is null || openedSessionId != sessionId)
            {
                var directory = Path.Combine(home, "session-state", sessionId.ToString("D"));
                var path = Path.Combine(directory, "events.jsonl");
                if (!File.Exists(path)) { return; }
                var binding = await CopilotSessionJournal.ReadWorkspaceBindingAsync(
                    directory, requireStartedAt: false, cancellationToken);
                if (!CopilotSessionJournal.PathEquals(binding.WorkspacePath, workspace))
                {
                    return;
                }
                reader?.Dispose();
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (sessionId == requestedSessionId)
                {
                    stream.Seek(initialOffset, SeekOrigin.Begin);
                }
                reader = new StreamReader(stream);
                openedSessionId = sessionId;
                pending.Clear();
            }
            var buffer = new char[8_192];
            // Bound each poll so a busy journal cannot delay the absolute-budget check.
            for (var batch = 0; batch < 16; batch++)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (count == 0) { break; }
                for (var index = 0; index < count; index++)
                {
                    if (buffer[index] == '\n')
                    {
                        var line = pending.ToString();
                        pending.Clear();
                        if (CopilotJsonlParser.TryPayload(
                                line, out _, out var document, out _))
                        {
                            using (document)
                            {
                                var timestamp = CopilotJsonlParser.ReadString(
                                    document.RootElement, "timestamp");
                                if (DateTimeOffset.TryParse(timestamp, out var eventAt) &&
                                    eventAt < launchedAt)
                                {
                                    continue;
                                }
                            }
                        }
                        monitor.Observe(line);
                    }
                    else
                    {
                        pending.Append(buffer[index]);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!unavailableReported)
            {
                unavailableReported = true;
                logger.LogWarning(exception,
                    "Live Copilot journal activity is unavailable; stdout lifecycle signals and watchdog remain active.");
            }
        }
    }

    private static long ExistingLength(string home, Guid sessionId)
    {
        var path = Path.Combine(home, "session-state", sessionId.ToString("D"), "events.jsonl");
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    public ValueTask DisposeAsync()
    {
        reader?.Dispose();
        return ValueTask.CompletedTask;
    }
}

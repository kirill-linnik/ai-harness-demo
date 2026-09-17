using System.Diagnostics;
using System.Text.Json;

namespace AiHarnessDemo.Services;

internal sealed class CopilotHandoffWatchdog : IDisposable
{
    internal static readonly TimeSpan ShutdownGracePeriod = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly Func<string, bool> _isCompleteHandoff;
    private readonly TimeSpan _gracePeriod;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationTokenSource _linked;
    private readonly Timer _timer;
    private readonly HashSet<string> _pendingTools = new(StringComparer.Ordinal);
    private bool _turnOpen;
    private bool _hasHandoff;
    private bool _sessionFailed;
    private bool _unidentifiedToolSeen;
    private bool _expired;
    private bool _disposed;
    private string? _turnId;
    private long? _completedTimestamp;

    internal CopilotHandoffWatchdog(
        Func<string, bool> isCompleteHandoff,
        CancellationToken cancellationToken,
        TimeSpan? gracePeriod = null)
    {
        _isCompleteHandoff = isCompleteHandoff;
        _gracePeriod = gracePeriod ?? ShutdownGracePeriod;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_gracePeriod, TimeSpan.Zero);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _shutdown.Token);
        _timer = new Timer(
            _ => Expire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal CancellationToken Token => _linked.Token;

    internal bool Expired
    {
        get { lock (_gate) { return _expired; } }
    }

    internal bool WaitingForExit
    {
        get { lock (_gate) { return _completedTimestamp is not null; } }
    }

    internal void Observe(string line)
    {
        if (!CopilotJsonlParser.TryPayload(
                line, out var type, out var document, out var payload))
        {
            return;
        }

        using (document)
        {
            var subagent = CopilotJsonlParser.IsSubAgentEvent(document.RootElement);
            lock (_gate)
            {
                if (_disposed || _expired)
                {
                    return;
                }

                switch (type)
                {
                    case "session.start":
                    case "session.resume":
                        Disarm();
                        _turnOpen = false;
                        _turnId = null;
                        _sessionFailed = false;
                        _unidentifiedToolSeen = false;
                        _pendingTools.Clear();
                        break;
                    case "user.message":
                        Disarm();
                        break;
                    case "assistant.turn_start":
                        Disarm();
                        if (!subagent)
                        {
                            _turnOpen = true;
                            _turnId = CopilotJsonlParser.ReadString(payload, "turnId");
                        }
                        break;
                    case "assistant.message_start":
                    case "assistant.message_delta":
                        Disarm();
                        break;
                    case "assistant.message":
                        Disarm();
                        if (subagent)
                        {
                            break;
                        }
                        var content = CopilotJsonlParser.ReadString(payload, "content") ??
                                      CopilotJsonlParser.ReadString(payload, "message");
                        var noTools = !payload.TryGetProperty("toolRequests", out var tools) ||
                                      (tools.ValueKind == JsonValueKind.Array &&
                                       tools.GetArrayLength() == 0);
                        _hasHandoff = _turnOpen && !_sessionFailed && noTools &&
                                      !string.IsNullOrWhiteSpace(content) &&
                                      _isCompleteHandoff(content);
                        break;
                    case "tool.execution_start":
                        Disarm();
                        var callId = ToolCallId(payload);
                        if (string.IsNullOrWhiteSpace(callId))
                        {
                            _unidentifiedToolSeen = true;
                        }
                        else
                        {
                            _pendingTools.Add(callId);
                        }
                        break;
                    case "tool.execution_complete":
                        Disarm();
                        _pendingTools.Remove(ToolCallId(payload));
                        break;
                    case "assistant.turn_end" when !subagent:
                        if (!_turnOpen ||
                            !string.Equals(
                                _turnId,
                                CopilotJsonlParser.ReadString(payload, "turnId"),
                                StringComparison.Ordinal))
                        {
                            break;
                        }
                        _turnOpen = false;
                        if (_hasHandoff && _pendingTools.Count == 0 &&
                            !_unidentifiedToolSeen && !_sessionFailed)
                        {
                            _completedTimestamp = Stopwatch.GetTimestamp();
                            _timer.Change(_gracePeriod, Timeout.InfiniteTimeSpan);
                        }
                        break;
                    case "assistant.turn_end" when subagent:
                        Disarm();
                        break;
                    case "abort":
                    case "session.error":
                        _sessionFailed = true;
                        Disarm();
                        break;
                    case "session.shutdown" when !string.Equals(
                        CopilotJsonlParser.ReadString(payload, "shutdownType"),
                        "routine", StringComparison.OrdinalIgnoreCase):
                        _sessionFailed = true;
                        Disarm();
                        break;
                }
            }
        }
    }

    private static string ToolCallId(JsonElement payload) =>
        CopilotJsonlParser.ReadString(payload, "toolCallId") ??
        CopilotJsonlParser.ReadString(payload, "callId") ??
        string.Empty;

    private void Disarm()
    {
        _hasHandoff = false;
        _completedTimestamp = null;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void Expire()
    {
        lock (_gate)
        {
            if (_disposed || _completedTimestamp is not { } completed)
            {
                return;
            }
            var remaining = _gracePeriod - Stopwatch.GetElapsedTime(completed);
            if (remaining > TimeSpan.Zero)
            {
                _timer.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }
            _expired = true;
            _completedTimestamp = null;
            _shutdown.Cancel();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
            _linked.Dispose();
            _shutdown.Dispose();
        }
    }
}

using System.Text.Json;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Reasoning;

namespace AiHarnessDemo.Services;

public sealed class ProcessBudgetExhaustedException(string message) : TimeoutException(message);

/// <summary>Only recognized lifecycle events reset inactivity; diagnostics are tracked separately.</summary>
public sealed class AgentExecutionMonitor
{
    private readonly Lock sync = new();
    private readonly TimeProvider clock;
    private readonly AgentExecutionPolicy policy;
    private readonly DateTimeOffset assignmentStartedAt;
    private readonly DateTimeOffset deadlineAt;
    private readonly DateTimeOffset invocationStartedAt;
    private readonly long invocationTimestamp;
    private DateTimeOffset watchdogStartedAt;
    private readonly Dictionary<string, (string Name, DateTimeOffset StartedAt)> tools = [];
    private readonly HashSet<string> completedTools = [];
    private readonly HashSet<string> observedIds = [];
    private DateTimeOffset? rawOutputAt;
    private DateTimeOffset? structuredAt;
    private string? structuredEvent;
    private DateTimeOffset? publishedAt;
    private bool toolLifecycleAvailable;
    private bool softWarning;
    private string? terminationReason;

    public AgentExecutionMonitor(
        AgentExecutionPolicy policy,
        DateTimeOffset assignmentStartedAt,
        DateTimeOffset deadlineAt,
        bool softWarningAlreadyEmitted = false,
        TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        this.policy = policy;
        this.assignmentStartedAt = assignmentStartedAt;
        this.deadlineAt = deadlineAt;
        invocationStartedAt = this.clock.GetUtcNow();
        invocationTimestamp = this.clock.GetTimestamp();
        watchdogStartedAt = invocationStartedAt;
        softWarning = softWarningAlreadyEmitted;
    }

    internal AgentExecutionMonitor(AgentExecutionBudget budget, TimeProvider? clock = null)
        : this(AssignmentExecutionBudget.ReadPolicy(budget), budget.StartedAt, budget.DeadlineAt,
            budget.SoftWarningAt.HasValue, clock) { }

    public Guid? ConfirmedSessionId { get; private set; }

    internal Func<CancellationToken, Task>? PollActivityAsync { get; set; }

    internal Action<AgentRuntimeActivity>? Report { get; set; }

    internal void BeginProcess()
    {
        lock (sync) { watchdogStartedAt = Now(); }
    }

    public void ObserveRawOutput()
    {
        lock (sync) { rawOutputAt = Now(); }
    }

    public void Observe(string line)
    {
        if (!CopilotJsonlParser.TryPayload(line, out var type, out var document, out var data))
        {
            return;
        }
        using (document)
        {
            lock (sync)
            {
                var now = Now();
                var eventId = CopilotJsonlParser.ReadString(document.RootElement, "id");
                if (eventId is not null && !observedIds.Add(eventId))
                {
                    return;
                }
                var callId = CopilotJsonlParser.ReadString(data, "toolCallId") ??
                             CopilotJsonlParser.ReadString(data, "callId");
                switch (type)
                {
                    case "session.start":
                    case "session.resume":
                        if (Guid.TryParse(CopilotJsonlParser.ReadString(data, "sessionId"), out var id))
                        {
                            ConfirmedSessionId = id;
                        }
                        break;
                    case "tool.execution_start":
                        var name = CopilotJsonlParser.ReadString(data, "toolName");
                        if (string.IsNullOrWhiteSpace(callId) || !SafeToolName(name) ||
                            completedTools.Contains(callId))
                        {
                            return;
                        }
                        toolLifecycleAvailable = true;
                        // A duplicate start must not renew a silent tool's allowance.
                        if (!tools.TryAdd(callId, (name!, now))) { return; }
                        break;
                    case "tool.execution_complete":
                        if (string.IsNullOrWhiteSpace(callId) || !completedTools.Add(callId))
                        {
                            return;
                        }
                        toolLifecycleAvailable = true;
                        tools.Remove(callId);
                        break;
                    case "tool.execution_progress":
                    case "tool.execution_partial_result":
                        if (callId is null || !tools.ContainsKey(callId))
                        {
                            return;
                        }
                        break;
                    case "assistant.turn_start":
                    case "assistant.message_start":
                    case "assistant.message":
                    case "assistant.message_delta":
                    case "assistant.reasoning":
                    case "assistant.reasoning_delta":
                    case "assistant.turn_end":
                    case "session.compaction_start":
                    case "session.compaction_complete":
                    case "session.error":
                    case "session.shutdown":
                    case "result":
                        break;
                    default:
                        return;
                }
                structuredAt = now;
                structuredEvent = type;
            }
        }
    }

    public AgentRuntimeActivity Snapshot()
    {
        lock (sync) { return SnapshotCore(Now()); }
    }

    public void Terminate(string reason)
    {
        lock (sync) { terminationReason ??= reason; }
    }

    public AgentRuntimeActivity? Tick(bool force = false)
    {
        lock (sync)
        {
            var now = Now();
            var warningChanged = !softWarning &&
                (now - assignmentStartedAt).TotalMilliseconds >= policy.SoftWarningMs;
            softWarning |= warningChanged;
            if (now >= deadlineAt)
            {
                terminationReason = nameof(AgentRunFailureKind.BudgetExhausted);
            }
            else
            {
                var inactivityDeadline = (structuredAt ?? watchdogStartedAt)
                    .AddMilliseconds(policy.InactivityTimeoutMs);
                // Use the oldest still-active tool: overlapping or duplicate starts cannot keep
                // an abandoned tool alive indefinitely.
                if (tools.Count > 0)
                {
                    var allowance = tools.Values.Min(tool => tool.StartedAt)
                        .AddMilliseconds(policy.SilentToolTimeoutMs);
                    if (allowance > inactivityDeadline) { inactivityDeadline = allowance; }
                }
                if (now >= inactivityDeadline)
                {
                    terminationReason = nameof(AgentRunFailureKind.Stalled);
                }
            }
            if (!force && !warningChanged && terminationReason is null &&
                publishedAt is { } published &&
                now - published < TimeSpan.FromSeconds(10))
            {
                return null;
            }
            publishedAt = now;
            return SnapshotCore(now);
        }
    }

    public void ThrowIfTerminated()
    {
        lock (sync)
        {
            if (terminationReason == nameof(AgentRunFailureKind.BudgetExhausted))
            {
                throw new ProcessBudgetExhaustedException(AssignmentExecutionBudget.ExhaustedReason);
            }
            if (terminationReason == nameof(AgentRunFailureKind.Stalled))
            {
                throw new ProcessStalledException(
                    "Copilot CLI produced no supported structured activity within its inactivity " +
                    "watchdog or bounded silent-tool allowance. Diagnostic output does not renew this watchdog.");
            }
        }
    }

    private DateTimeOffset Now()
    {
        var monotonicNow = invocationStartedAt + clock.GetElapsedTime(invocationTimestamp);
        var utcNow = clock.GetUtcNow();
        return utcNow > monotonicNow ? utcNow : monotonicNow;
    }

    private AgentRuntimeActivity SnapshotCore(DateTimeOffset now)
    {
        var active = tools.Count > 0
            ? tools.Values.MinBy(tool => tool.StartedAt)
            : ((string Name, DateTimeOffset StartedAt)?)null;
        return new(now, rawOutputAt, structuredEvent, structuredAt,
            active?.Name, active?.StartedAt,
            active is { } tool ? Math.Max(0, (long)(now - tool.StartedAt).TotalMilliseconds) : null,
            toolLifecycleAvailable ? completedTools.Count : null,
            Math.Max(0, (long)(now - assignmentStartedAt).TotalMilliseconds),
            Math.Max(0, (long)(deadlineAt - now).TotalMilliseconds), deadlineAt,
            softWarning || (now - assignmentStartedAt).TotalMilliseconds >= policy.SoftWarningMs,
            terminationReason);
    }

    private static bool SafeToolName(string? value) =>
        value is { Length: > 0 and <= 120 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
                               character is '_' or '-' or '.' or ':' or '/');
}

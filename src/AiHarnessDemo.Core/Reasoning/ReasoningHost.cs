namespace AiHarnessDemo.Core.Reasoning;

/// <summary>One invocation of an agent, routed through a pluggable reasoning host.</summary>
public sealed class AgentRunRequest
{
    public required string AgentId { get; init; }

    public required string Model { get; init; }

    public required string CorrelationId { get; init; }

    public required Guid CopilotSessionId { get; init; }

    public required string WorkingDirectory { get; init; }

    public Dictionary<string, object?> InputContext { get; init; } = new();

    public Action<AgentRunProgress>? Progress { get; init; }
}

public enum AgentRunPhase
{
    PreparingWorkspace,
    BuildingPrompt,
    LaunchingAgentProcess,
    InitializingSession,
    StreamingTurn,
    Finishing,
    Succeeded,
    Failed,
    TimedOut,
    Stalled,
    CanceledByReconciliation
}

public sealed record AgentRunProgress(
    AgentRunPhase Phase,
    string Activity,
    string? ExecutionPrompt = null,
    Guid? CopilotSessionId = null,
    string? CopilotSessionHome = null);

public sealed class AgentRunResult
{
    public required bool Success { get; init; }

    public required string OutputSummary { get; init; }

    public List<ToolCallRecord> ToolCalls { get; init; } = [];

    public string? Error { get; init; }

    public AgentRunFailureKind? FailureKind { get; init; }

    public string? FailedDependency { get; init; }

    public bool ProcessTerminationUnconfirmed { get; init; }
}

/// <summary>A scrubbed tool invocation. Raw tool arguments and results are not part of the audit record.</summary>
public sealed record ToolCallRecord(
    string ToolName,
    string ArgumentsSummary,
    bool Succeeded);

public readonly record struct ReasoningHostReadiness(
    bool IsAvailable,
    string Detail,
    DateTimeOffset ObservedAt)
{
    public static ReasoningHostReadiness CreateAvailable(string detail = "Ready.") =>
        new(true, detail, DateTimeOffset.UtcNow);

    public static ReasoningHostReadiness CreateUnavailable(string detail) =>
        new(false, detail, DateTimeOffset.UtcNow);
}

public enum AgentRunFailureKind
{
    Transient,
    DependencyUnavailable,
    TimedOut,
    Stalled,
    InvalidOutput,
    Cancelled,
    AmbiguousCrash
}

public sealed record ReasoningHostConfig(string RuntimeName);

/// <summary>
/// Contract for the Copilot CLI reasoning host. Reasoning only produces an output; the caller
/// separately submits that output to the gate engine.
/// </summary>
public abstract class ReasoningHost(ReasoningHostConfig config)
{
    public ReasoningHostConfig Config { get; } = config;

    public abstract Task<AgentRunResult> RunAgentAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default);

    public virtual ReasoningHostReadiness CheckReadiness(
        CancellationToken cancellationToken = default) =>
        ReasoningHostReadiness.CreateAvailable("Reasoning host is ready.");
}

public sealed class AgentRunException(
    string message,
    AgentRunFailureKind failureKind,
    string? failedDependency = null,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public AgentRunFailureKind FailureKind { get; } = failureKind;

    public string? FailedDependency { get; } = failedDependency;
}

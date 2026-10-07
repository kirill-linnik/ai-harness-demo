namespace AiHarnessDemo.Core.Reasoning;

/// <summary>One invocation of an agent, routed through a pluggable reasoning host.</summary>
public sealed class AgentRunRequest
{
    public required string AgentId { get; init; }

    public required string Model { get; init; }

    public required string Effort { get; init; }

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
    Retrying,
    Succeeded,
    Failed,
    TimedOut,
    Stalled,
    CanceledByReconciliation,
    BudgetExhausted
}

public sealed record AgentRunProgress(
    AgentRunPhase Phase,
    string Activity,
    string? ExecutionPrompt = null,
    Guid? CopilotSessionId = null,
    string? CopilotSessionHome = null,
    AgentRuntimeActivity? RuntimeActivity = null);

public sealed record AgentExecutionPolicy(
    int InactivityTimeoutMs,
    int SilentToolTimeoutMs,
    int SoftWarningMs,
    int ExecutionBudgetMs,
    string WorkflowRevision);

public sealed record AgentRuntimeActivity(
    DateTimeOffset ObservedAt,
    DateTimeOffset? LastRawOutputAt,
    string? LastStructuredEvent,
    DateTimeOffset? LastStructuredActivityAt,
    string? ActiveTool,
    DateTimeOffset? ActiveToolStartedAt,
    long? ActiveToolElapsedMilliseconds,
    int? CompletedToolCount,
    long TotalElapsedMilliseconds,
    long RemainingBudgetMilliseconds,
    DateTimeOffset BudgetDeadlineAt,
    bool SoftWarning,
    string? TerminationReason);

public sealed class AgentRunResult
{
    public required bool Success { get; init; }

    public required string OutputSummary { get; init; }

    public List<ToolCallRecord> ToolCalls { get; init; } = [];

    public string? Error { get; init; }

    public AgentRunFailureKind? FailureKind { get; init; }

    public string? FailedDependency { get; init; }

    public bool ProcessTerminationUnconfirmed { get; init; }

    public bool CanResumeSession { get; init; }
}

/// <summary>
/// Host-observed tool evidence. Sensitive argument fields remain redacted, while normalized
/// verification inputs and bounded result evidence are retained so QA claims can be checked
/// against what the host actually executed and observed.
/// </summary>
public sealed record ToolCallRecord(
    string ToolName,
    string ArgumentsSummary,
    bool Succeeded,
    string ToolType = "Unknown",
    string NormalizedCommand = "",
    string NormalizedArguments = "",
    string WorkingDirectory = "",
    int? ExitCode = null,
    string ResultDigest = "",
    string ResultSummary = "");

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
    ModelUnavailable,
    TimedOut,
    Stalled,
    InvalidOutput,
    Cancelled,
    AmbiguousCrash,
    BudgetExhausted
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
    bool canResumeSession = false,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public AgentRunFailureKind FailureKind { get; } = failureKind;

    public string? FailedDependency { get; } = failedDependency;

    public bool CanResumeSession { get; } = canResumeSession;

    public int ExecutionAttempts { get; set; } = 1;

    public IReadOnlyList<ToolCallRecord> ToolCalls { get; init; } = [];

    public bool ProcessTerminationUnconfirmed { get; init; }
}

using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Reasoning;

namespace AiHarnessDemo.Core.Domain;

public enum OutcomeType
{
    Commit,
    PullRequest
}

public enum ModelSelectionStrategy
{
    MaximumQuality,
    FastestResponse,
    LowestCost
}

public enum TaskRisk
{
    Low,
    Medium,
    High,
    Critical
}

public enum TaskTypeTag
{
    CustomerDialogue,
    Planning,
    Architecture,
    Design,
    Data,
    Implementation,
    Security,
    Quality,
    Documentation,
    Release,
    Feedback,
    CrossCutting
}

public enum FlowStatus
{
    Intake,
    Queued,
    Running,
    WaitingForFeedback,
    Reworking,
    Approved,
    Failed
}

public enum StepStatus
{
    Pending,
    Running,
    Completed,
    Pushback,
    Failed,
    Skipped
}

public enum ConversationRole
{
    Customer,
    AccountManager,
    ProductManager,
    Harness
}

public sealed class HarnessSettings
{
    public int Id { get; set; } = 1;

    public string RepositoryPath { get; set; } = string.Empty;

    public string RepositoryKnowledge { get; set; } = string.Empty;

    public OutcomeType Outcome { get; set; } = OutcomeType.PullRequest;

    public int MaxHandoffRetries { get; set; } = 2;

    public ModelSelectionStrategy ModelSelectionStrategy { get; set; } =
        ModelSelectionStrategy.MaximumQuality;

    // Retains compatibility with databases created before Copilot CLI became the only host.
    public string RuntimeMarker { get; set; } = "LiveCopilot";

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AgentRecord
{
    public required string Id { get; set; }

    public required string Name { get; set; }

    public required string Description { get; set; }

    public required string Role { get; set; }

    public required string SourcePath { get; set; }

    public string Accent { get; set; } = "violet";

    public bool Enabled { get; set; } = true;

    public int SortOrder { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FlowRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Title { get; set; }

    public required string OriginalRequest { get; set; }

    public string ConsolidatedRequest { get; set; } = string.Empty;

    public FlowStatus Status { get; set; } = FlowStatus.Intake;

    public int Iteration { get; set; } = 1;

    public string RepositoryPath { get; set; } = string.Empty;

    public string RepositoryKnowledge { get; set; } = string.Empty;

    public OutcomeType Outcome { get; set; } = OutcomeType.PullRequest;

    public ModelSelectionStrategy ModelSelectionStrategy { get; set; } =
        ModelSelectionStrategy.MaximumQuality;

    // Retains compatibility with databases created before Copilot CLI became the only host.
    public string RuntimeMarker { get; set; } = "LiveCopilot";

    public string WorkspacePath { get; set; } = string.Empty;

    public string BranchName { get; set; } = string.Empty;

    public string OutcomeUrl { get; set; } = string.Empty;

    public string OutcomeLabel { get; set; } = string.Empty;

    public string FailureReason { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }

    public List<FlowStep> Steps { get; set; } = [];

    public List<FlowMessage> Messages { get; set; } = [];

    public List<FlowEvent> Events { get; set; } = [];

    public List<HandoffGateRecord> GateRecords { get; set; } = [];

    public List<TaskProfile> TaskProfiles { get; set; } = [];
}

public sealed class FlowStep
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FlowRunId { get; set; }

    public FlowRun? FlowRun { get; set; }

    public int Iteration { get; set; }

    public int Sequence { get; set; }

    public required string AgentId { get; set; }

    public required string AgentName { get; set; }

    public required string AgentRole { get; set; }

    public string Label { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string ModelEffort { get; set; } = string.Empty;

    public string ModelReason { get; set; } = string.Empty;

    public StepStatus Status { get; set; } = StepStatus.Pending;

    public AgentRunPhase Phase { get; set; } = AgentRunPhase.PreparingWorkspace;

    public int Attempt { get; set; } = 1;

    public int ExecutionAttempts { get; set; }

    public string InputSummary { get; set; } = string.Empty;

    public string ExecutionPrompt { get; set; } = string.Empty;

    public Guid? CopilotSessionId { get; set; }

    public string CopilotSessionHome { get; set; } = string.Empty;

    public string OutputSummary { get; set; } = string.Empty;

    public string PushbackReason { get; set; } = string.Empty;

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public long DurationMilliseconds { get; set; }

    public List<AgentToolCall> ToolCalls { get; set; } = [];

    public List<RoutingDecision> RoutingDecisions { get; set; } = [];
}

/// <summary>
/// Versioned normalized routing input. Task text and repository content are deliberately excluded.
/// </summary>
public sealed class TaskProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FlowRunId { get; set; }

    public int Iteration { get; set; }

    public Guid? FlowStepId { get; set; }

    public string Version { get; set; } = "task-profile-v1";

    public required string Role { get; set; }

    public int Complexity { get; set; }

    public int ReasoningDepth { get; set; }

    public int ContextDemand { get; set; }

    public int ToolIntensity { get; set; }

    /// <summary>JSON array of <see cref="TaskTypeTag"/> names.</summary>
    public string TaskTypeTagsJson { get; set; } = "[]";

    public TaskRisk Risk { get; set; }

    public required string RiskReason { get; set; }

    public double Confidence { get; set; }

    /// <summary>JSON array of bounded rationale strings.</summary>
    public string RationalesJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ModelCatalogSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string CatalogVersion { get; set; }

    public string CliVersion { get; set; } = string.Empty;

    public DateTimeOffset DiscoveredAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsCurrent { get; set; }

    public List<ModelCatalogCandidate> Candidates { get; set; } = [];
}

public sealed class ModelCatalogCandidate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ModelCatalogSnapshotId { get; set; }

    public ModelCatalogSnapshot? Snapshot { get; set; }

    public required string Model { get; set; }

    public required string Effort { get; set; }

    public int ModelOrder { get; set; }

    public int EffortOrder { get; set; }

    public bool IsDefaultModel { get; set; }

    public bool IsDefaultEffort { get; set; }

    public double? PremiumMultiplier { get; set; }

    public string Description { get; set; } = string.Empty;

    public string MetadataConfidence { get; set; } = "low";

    public bool Enabled { get; set; } = true;
}

public sealed class RoutingDecision
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FlowStepId { get; set; }

    public Guid TaskProfileId { get; set; }

    public TaskProfile? TaskProfile { get; set; }

    public Guid ModelCatalogSnapshotId { get; set; }

    public Guid? SupersedesRoutingDecisionId { get; set; }

    public bool Superseded { get; set; }

    public int RerouteCount { get; set; }

    public required string SelectedModel { get; set; }

    public required string SelectedEffort { get; set; }

    public ModelSelectionStrategy Strategy { get; set; }

    public double PredictedQuality { get; set; }

    public double PredictedAcceptedTimeSeconds { get; set; }

    public double PredictedPremiumRequests { get; set; }

    public bool PremiumUseEstimated { get; set; } = true;

    public double Confidence { get; set; }

    public double Uncertainty { get; set; }

    public bool Exploration { get; set; }

    public required string Reason { get; set; }

    public string AlgorithmVersion { get; set; } = "router-v1";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<RoutingAlternative> Alternatives { get; set; } = [];

    public FlowStep? FlowStep { get; set; }
}

public sealed class RoutingAlternative
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RoutingDecisionId { get; set; }

    public RoutingDecision? RoutingDecision { get; set; }

    public required string Model { get; set; }

    public required string Effort { get; set; }

    public int Rank { get; set; }

    public double PredictedQuality { get; set; }

    public double PredictedAcceptedTimeSeconds { get; set; }

    public double PredictedPremiumRequests { get; set; }

    public double Confidence { get; set; }

    public required string Reason { get; set; }
}

/// <summary>Normalized outcome evidence. It intentionally contains no prompt, task, or repository text.</summary>
public sealed class RoutingObservation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RoutingDecisionId { get; set; }

    public Guid FlowStepId { get; set; }

    public required string Role { get; set; }

    public string TaskTypeTagsJson { get; set; } = "[]";

    public int Complexity { get; set; }

    public int ReasoningDepth { get; set; }

    public int ContextDemand { get; set; }

    public int ToolIntensity { get; set; }

    public TaskRisk Risk { get; set; }

    public bool? Accepted { get; set; }

    public bool AvailabilityFailure { get; set; }

    public double EvidenceWeight { get; set; } = 1;

    public long DurationMilliseconds { get; set; }

    public int ExecutionAttempts { get; set; } = 1;

    public double EstimatedPremiumRequests { get; set; }

    public string OutcomeKind { get; set; } = string.Empty;

    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Scrubbed tool trace. Raw tool arguments and results are deliberately not persisted.</summary>
public sealed class AgentToolCall
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FlowStepId { get; set; }

    public FlowStep? FlowStep { get; set; }

    public required string ToolName { get; set; }

    public required string ArgumentsSummary { get; set; }

    public bool Succeeded { get; set; }
}

public sealed class FlowMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FlowRunId { get; set; }

    public FlowRun? FlowRun { get; set; }

    public ConversationRole Role { get; set; }

    public required string Content { get; set; }

    public bool IsQuestion { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FlowEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FlowRunId { get; set; }

    public FlowRun? FlowRun { get; set; }

    public Guid? FlowStepId { get; set; }

    public required string Type { get; set; }

    public required string Message { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class HarnessLearning
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? SourceFlowId { get; set; }

    public string AgentId { get; set; } = string.Empty;

    public required string Category { get; set; }

    public required string Trigger { get; set; }

    public required string Lesson { get; set; }

    public required string PromptRefinement { get; set; }

    public int TimesApplied { get; set; }

    public int TimesObserved { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

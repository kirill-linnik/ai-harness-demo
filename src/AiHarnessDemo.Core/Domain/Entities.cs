using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Reasoning;

namespace AiHarnessDemo.Core.Domain;

public enum OutcomeType
{
    Commit,
    PullRequest,
    None
}

public static class OutcomeTypeRules
{
    public static OutcomeType RequireDelivery(
        OutcomeType outcome,
        string parameterName = "outcome")
    {
        if (!Enum.IsDefined(outcome) || outcome == OutcomeType.None)
        {
            throw new ArgumentException(
                "A persisted Delivery outcome must be Commit or PullRequest.",
                parameterName);
        }
        return outcome;
    }
}

public enum FlowKind
{
    Advisory,
    Delivery
}

public enum FlowLinkKind
{
    AdvisoryPromotion,
    QualificationRosterRepair,
    QualificationScopeRevision
}

public enum PlanDuty
{
    Analyze,
    Design,
    Implement,
    Verify,
    PrepareOutcome,
    Publish
}

public enum PlanStage
{
    BeforeReview,
    AfterApproval
}

public enum ExecutionPermissionProfile
{
    ReadOnlySource,
    WorkspaceWrite,
    Publish,
    PreMortemReadOnly
}

/// <summary>
/// Host-owned lifecycle purpose for an agent turn. This value is assigned by orchestration code,
/// never by an agent manifest or plan document, and is the authorization input used alongside
/// flow kind, stage, and duties.
/// </summary>
public enum ExecutionInvocationKind
{
    Intake,
    Planning,
    Worker,
    PreMortem,
    BlockerExplanation,
    Publication
}

public enum ReviewDecision
{
    Accepted,
    RefinementRequested,
    PromotedToDelivery
}

public enum AgentDefinitionStatus
{
    Valid,
    Invalid
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
    Abandoning,
    Approved,
    Abandoned,
    Failed,
    Blocked
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

public enum DemoInstanceState
{
    Stopped,
    Starting,
    Running,
    Unhealthy,
    Failed
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

    public AgentDefinitionStatus DefinitionStatus { get; set; } = AgentDefinitionStatus.Valid;

    public string ValidationError { get; set; } = string.Empty;

    public bool Required { get; set; }

    public bool Switchable { get; set; } = true;

    public string DefinitionHash { get; set; } = string.Empty;

    public DateTimeOffset LoadedAt { get; set; } = DateTimeOffset.UtcNow;

    public int SortOrder { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FlowRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Title { get; set; }

    public required string OriginalRequest { get; set; }

    public string ConsolidatedRequest { get; set; } = string.Empty;

    public FlowKind Kind { get; set; } = FlowKind.Delivery;

    public Guid? ParentFlowRunId { get; set; }

    public FlowRun? ParentFlowRun { get; set; }

    public int? ParentIteration { get; set; }

    public FlowLinkKind? LinkKind { get; set; }

    public string AgentCatalogRevision { get; set; } = string.Empty;

    public string? OutcomeOwnerPlanStepKey { get; set; }

    public string? PublicationPlanStepKey { get; set; }

    public string OutcomeContractJson { get; set; } = string.Empty;

    public string? CurrentBlockerCode { get; set; }

    public string? CurrentBlockerSummary { get; set; }

    public string? CurrentBlockerDataJson { get; set; }

    public string? CustomerBlockerMessage { get; set; }

    public FlowStatus Status { get; set; } = FlowStatus.Intake;

    public int Iteration { get; set; } = 1;

    public string RepositoryPath { get; set; } = string.Empty;

    public string RepositoryKnowledge { get; set; } = string.Empty;

    public OutcomeType Outcome { get; set; } = OutcomeType.PullRequest;

    public ModelSelectionStrategy ModelSelectionStrategy { get; set; } =
        ModelSelectionStrategy.MaximumQuality;

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

    public List<FlowRun> LinkedFlowRuns { get; set; } = [];

    public List<FlowAgentSnapshot> AgentSnapshots { get; set; } = [];

    public List<FlowPlanDocument> PlanDocuments { get; set; } = [];

    public List<DemoInstanceRecord> DemoInstances { get; set; } = [];
}

/// <summary>
/// Durable ownership and lifecycle record for a non-authoritative live demo. Candidate and manifest
/// identities are immutable for the row; mutable process fields describe only the currently owned
/// attempt for that binding.
/// </summary>
public sealed class DemoInstanceRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FlowRunId { get; set; }

    public FlowRun? FlowRun { get; set; }

    public string ArtifactId { get; set; } = string.Empty;

    public string CandidateFingerprint { get; set; } = string.Empty;

    public string ManifestHash { get; set; } = string.Empty;

    public string ManifestRelativePath { get; set; } = string.Empty;

    public DemoInstanceState State { get; set; } = DemoInstanceState.Stopped;

    public int? ProcessId { get; set; }

    public long? ProcessStartIdentity { get; set; }

    public string ProcessName { get; set; } = string.Empty;

    public int? AssignedPort { get; set; }

    public string WorkspacePath { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    public string LaunchProfile { get; set; } = string.Empty;

    public string LaunchIdentity { get; set; } = string.Empty;

    public string FailureDetail { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? LastHealthCheckAt { get; set; }

    public DateTimeOffset? StoppedAt { get; set; }
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

    public string PlanStepKey { get; set; } = string.Empty;

    public string PlanDutiesJson { get; set; } = "[]";

    public PlanStage PlanStage { get; set; } = PlanStage.BeforeReview;

    public bool IsOutcomeOwner { get; set; }

    public ExecutionPermissionProfile PermissionProfile { get; set; } =
        ExecutionPermissionProfile.WorkspaceWrite;

    public ExecutionInvocationKind InvocationKind { get; set; } =
        ExecutionInvocationKind.Worker;

    public string EffectivePermissionJson { get; set; } = string.Empty;

    public string WorkflowRevision { get; set; } = string.Empty;

    public Guid? StableSemanticRootId { get; set; }

    public string Model { get; set; } = string.Empty;

    public string ModelEffort { get; set; } = string.Empty;

    public string ModelReason { get; set; } = string.Empty;

    public bool RemotePublicationAllowed { get; set; }

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

    public Guid? RetryOfStepId { get; set; }

    public Guid? DependsOnStepId { get; set; }

    public Guid? PushbackRootStepId { get; set; }

    public Guid? PreMortemOriginStepId { get; set; }

    public Guid? PreMortemTargetStepId { get; set; }

    public Guid? PreMortemReviewStepId { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public long DurationMilliseconds { get; set; }

    public List<AgentToolCall> ToolCalls { get; set; } = [];

    public List<RoutingDecision> RoutingDecisions { get; set; } = [];
}

/// <summary>Normalized routing input. Task text and repository content are deliberately excluded.</summary>
public sealed class TaskProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FlowRunId { get; set; }

    public int Iteration { get; set; }

    public Guid? FlowStepId { get; set; }

    public string PlanStepKey { get; set; } = string.Empty;

    public string AgentId { get; set; } = string.Empty;

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

    public bool PreMortemAfter { get; set; }

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

/// <summary>
/// Structured host-observed tool trace. Sensitive argument values are redacted and result text is
/// bounded; the normalized verification inputs and result digest make QA evidence auditable.
/// </summary>
public sealed class AgentToolCall
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FlowStepId { get; set; }

    public FlowStep? FlowStep { get; set; }

    public required string ToolName { get; set; }

    public required string ArgumentsSummary { get; set; }

    public bool Succeeded { get; set; }

    public string ToolType { get; set; } = "Unknown";

    public string NormalizedCommand { get; set; } = string.Empty;

    public string NormalizedArguments { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    public int? ExitCode { get; set; }

    public string ResultDigest { get; set; } = string.Empty;

    public string ResultSummary { get; set; } = string.Empty;
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

    public string? DataJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FlowAgentSnapshot
{
    public Guid FlowRunId { get; set; }

    public FlowRun? FlowRun { get; set; }

    public required string AgentId { get; set; }

    public required string Name { get; set; }

    public required string Description { get; set; }

    public required string Role { get; set; }

    public required string Instructions { get; set; }

    public required string DefinitionHash { get; set; }

    public bool EnabledAtSnapshot { get; set; }

    public bool Required { get; set; }

    public bool Switchable { get; set; }

    public required string SourceFileName { get; set; }

    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FlowPlanDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FlowRunId { get; set; }

    public FlowRun? FlowRun { get; set; }

    public int Iteration { get; set; }

    public required string Disposition { get; set; }

    public required string RawJson { get; set; }

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

using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Reasoning;

namespace AiHarnessDemo.Core.Domain;

public enum OutcomeType
{
    Commit,
    PullRequest
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

    public string ModelReason { get; set; } = string.Empty;

    public StepStatus Status { get; set; } = StepStatus.Pending;

    public AgentRunPhase Phase { get; set; } = AgentRunPhase.PreparingWorkspace;

    public int Attempt { get; set; } = 1;

    public int ExecutionAttempts { get; set; }

    public string InputSummary { get; set; } = string.Empty;

    public string OutputSummary { get; set; } = string.Empty;

    public string PushbackReason { get; set; } = string.Empty;

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public long DurationMilliseconds { get; set; }

    public List<AgentToolCall> ToolCalls { get; set; } = [];
}

/// <summary>Scrubbed tool trace. Raw prompts and tool results are deliberately not persisted.</summary>
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

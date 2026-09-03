using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Reasoning;

namespace AiHarnessDemo.Contracts;

public sealed record SettingsDto(
    string RepositoryPath,
    string RepositoryKnowledge,
    OutcomeType Outcome,
    int MaxHandoffRetries,
    DateTimeOffset UpdatedAt);

public sealed record AgentDto(
    string Id,
    string Name,
    string Description,
    string Role,
    string Accent,
    bool Enabled,
    int SortOrder);

public sealed record AgentToolCallDto(
    Guid Id,
    string ToolName,
    string ArgumentsSummary,
    bool Succeeded);

public sealed record FlowSummaryDto(
    Guid Id,
    string Title,
    FlowStatus Status,
    int Iteration,
    string RepositoryPath,
    string OutcomeLabel,
    string OutcomeUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record FlowStepDto(
    Guid Id,
    int Iteration,
    int Sequence,
    string AgentId,
    string AgentName,
    string AgentRole,
    string Label,
    string Model,
    string ModelReason,
    StepStatus Status,
    AgentRunPhase Phase,
    int Attempt,
    int ExecutionAttempts,
    string InputSummary,
    string ExecutionPrompt,
    string CopilotSessionId,
    string OutputSummary,
    string PushbackReason,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    long DurationMilliseconds,
    IReadOnlyList<AgentToolCallDto> ToolCalls);

public sealed record FlowMessageDto(
    Guid Id,
    ConversationRole Role,
    string Content,
    bool IsQuestion,
    DateTimeOffset CreatedAt);

public sealed record FlowEventDto(
    Guid Id,
    Guid? FlowStepId,
    string Type,
    string Message,
    DateTimeOffset CreatedAt);

public sealed record HandoffGateRecordDto(
    Guid Id,
    Guid FlowStepId,
    HandoffActionType ActionType,
    HandoffGateDecision Decision,
    HandoffTrustLevel TrustLevelAtDecision,
    string Summary,
    string Evidence,
    string Reason,
    DateTimeOffset DecidedAt,
    bool Resolved,
    bool? Approved,
    string? ResolvedBy,
    string? ResolutionNote,
    DateTimeOffset? ResolvedAt);

public sealed record FlowDetailDto(
    Guid Id,
    string Title,
    string OriginalRequest,
    string ConsolidatedRequest,
    FlowStatus Status,
    int Iteration,
    string RepositoryPath,
    string RepositoryKnowledge,
    OutcomeType Outcome,
    string WorkspacePath,
    string BranchName,
    string OutcomeUrl,
    string OutcomeLabel,
    string FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<FlowStepDto> Steps,
    IReadOnlyList<FlowMessageDto> Messages,
    IReadOnlyList<FlowEventDto> Events,
    IReadOnlyList<HandoffGateRecordDto> GateRecords);

public sealed record LearningDto(
    Guid Id,
    Guid? SourceFlowId,
    string AgentId,
    string Category,
    string Trigger,
    string Lesson,
    string PromptRefinement,
    int TimesApplied,
    int TimesObserved,
    DateTimeOffset CreatedAt);

public sealed record HistoryItemDto(
    Guid FlowId,
    string FlowTitle,
    int Iteration,
    string AgentName,
    string AgentRole,
    string Model,
    StepStatus Status,
    long DurationMilliseconds,
    DateTimeOffset? StartedAt,
    string PushbackReason);

public sealed record HarnessStatsDto(
    int TotalFlows,
    int ActiveFlows,
    int CompletedFlows,
    int LearnedRefinements,
    long TotalAgentMinutes);

public sealed record WorkflowStatusDto(
    bool Ready,
    string SourcePath,
    DateTimeOffset? LoadedAt,
    string? LastError,
    int? MaxConcurrentAgents,
    int? MaxAttempts,
    string? WorkspaceRoot);

public sealed record CopilotCliStatusDto(
    bool Ready,
    string Command,
    string ResolvedPath,
    string Version,
    string Detail,
    DateTimeOffset CheckedAt);

public sealed record BootstrapDto(
    SettingsDto Settings,
    IReadOnlyList<AgentDto> Agents,
    IReadOnlyList<FlowSummaryDto> Flows,
    HarnessStatsDto Stats,
    bool CopilotCliAvailable,
    CopilotCliStatusDto CopilotCli,
    WorkflowStatusDto Workflow,
    bool FactoryEnabled,
    string FactoryDisabledReason);

public sealed record SaveSettingsRequest(
    string? RepositoryPath,
    string? RepositoryKnowledge,
    OutcomeType Outcome,
    int? MaxHandoffRetries);

public sealed record ToggleAgentRequest(bool Enabled);

public sealed record AnalyzeRepositoryRequest(string Path, bool RunCopilotInit = true);

public sealed record AnalyzeRepositoryResponse(
    string RepositoryPath,
    string Knowledge,
    bool CopilotInitSucceeded,
    string CopilotInitMessage);

public sealed record DirectoryEntryDto(string Name, string Path);

public sealed record DirectoryListingDto(
    string CurrentPath,
    string? ParentPath,
    IReadOnlyList<DirectoryEntryDto> Directories,
    IReadOnlyList<DirectoryEntryDto> Locations)
{
    public IReadOnlyList<DirectoryEntryDto> Drives => Locations;
}

public sealed record IntakeRequest(Guid? FlowId, string Message);

public sealed record IntakeResponse(
    FlowDetailDto Flow,
    string Reply,
    bool ReadyToStart,
    bool ShouldSpeak);

public sealed record FeedbackRequest(string Message);

public sealed record FeedbackResponse(FlowDetailDto Flow, string Reply, bool ShouldSpeak);

public sealed record FlowDecisionRequest(bool Approve);

public sealed record PreviewDto(
    Guid FlowId,
    string Title,
    string Request,
    string RepositoryName,
    int Iteration,
    string OutcomeLabel,
    IReadOnlyList<FlowStepDto> DeliveredBy,
    DateTimeOffset GeneratedAt);

public static class ApiMappings
{
    public static SettingsDto ToDto(this HarnessSettings settings) =>
        new(
            settings.RepositoryPath,
            settings.RepositoryKnowledge,
            settings.Outcome,
            settings.MaxHandoffRetries,
            settings.UpdatedAt);

    public static AgentDto ToDto(this AgentRecord agent) =>
        new(
            agent.Id,
            agent.Name,
            agent.Description,
            agent.Role,
            agent.Accent,
            agent.Enabled,
            agent.SortOrder);

    public static FlowSummaryDto ToSummaryDto(this FlowRun flow) =>
        new(
            flow.Id,
            flow.Title,
            flow.Status,
            flow.Iteration,
            flow.RepositoryPath,
            flow.OutcomeLabel,
            flow.OutcomeUrl,
            flow.CreatedAt,
            flow.UpdatedAt);

    public static FlowDetailDto ToDetailDto(this FlowRun flow) =>
        new(
            flow.Id,
            flow.Title,
            flow.OriginalRequest,
            flow.ConsolidatedRequest,
            flow.Status,
            flow.Iteration,
            flow.RepositoryPath,
            flow.RepositoryKnowledge,
            flow.Outcome,
            flow.WorkspacePath,
            flow.BranchName,
            flow.OutcomeUrl,
            flow.OutcomeLabel,
            flow.FailureReason,
            flow.CreatedAt,
            flow.UpdatedAt,
            flow.CompletedAt,
            flow.Steps
                .OrderBy(step => step.Iteration)
                .ThenBy(step => step.Sequence)
                .Select(step => step.ToDto())
                .ToList(),
            flow.Messages
                .OrderBy(message => message.CreatedAt)
                .Select(message => new FlowMessageDto(
                    message.Id,
                    message.Role,
                    message.Content,
                    message.IsQuestion,
                    message.CreatedAt))
                .ToList(),
            flow.Events
                .OrderByDescending(item => item.CreatedAt)
                .Select(item => new FlowEventDto(
                    item.Id,
                    item.FlowStepId,
                    item.Type,
                    item.Message,
                    item.CreatedAt))
                .ToList(),
            flow.GateRecords
                .OrderBy(item => item.DecidedAt)
                .Select(item => new HandoffGateRecordDto(
                    item.Id,
                    item.FlowStepId,
                    item.ActionType,
                    item.Decision,
                    item.TrustLevelAtDecision,
                    item.Summary,
                    item.Evidence,
                    item.Reason,
                    item.DecidedAt,
                    item.Resolved,
                    item.Approved,
                    item.ResolvedBy,
                    item.ResolutionNote,
                    item.ResolvedAt))
                .ToList());

    public static FlowStepDto ToDto(this FlowStep step) =>
        new(
            step.Id,
            step.Iteration,
            step.Sequence,
            step.AgentId,
            step.AgentName,
            step.AgentRole,
            step.Label,
            step.Model,
            step.ModelReason,
            step.Status,
            step.Phase,
            step.Attempt,
            step.ExecutionAttempts,
            step.InputSummary,
            step.ExecutionPrompt,
            step.CopilotSessionId?.ToString("D") ?? string.Empty,
            step.OutputSummary,
            step.PushbackReason,
            step.StartedAt,
            step.CompletedAt,
            step.DurationMilliseconds,
            step.ToolCalls
                .Select(item => new AgentToolCallDto(
                    item.Id,
                    item.ToolName,
                    item.ArgumentsSummary,
                    item.Succeeded))
                .ToList());

    public static LearningDto ToDto(this HarnessLearning learning) =>
        new(
            learning.Id,
            learning.SourceFlowId,
            learning.AgentId,
            learning.Category,
            learning.Trigger,
            learning.Lesson,
            learning.PromptRefinement,
            learning.TimesApplied,
            learning.TimesObserved,
            learning.CreatedAt);
}

using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Services;

namespace AiHarnessDemo.Contracts;

public sealed record SettingsDto(
    string RepositoryPath,
    string RepositoryKnowledge,
    OutcomeType Outcome,
    int MaxHandoffRetries,
    ModelSelectionStrategy ModelSelectionStrategy,
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
    string ModelEffort,
    string ModelReason,
    TaskProfileDto? TaskProfile,
    RoutingDecisionDto? Routing,
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
    IReadOnlyList<AgentToolCallDto> ToolCalls,
    IReadOnlyList<string> AssignedCriterionIds,
    int? OutcomeQaRound);

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
    ModelSelectionStrategy ModelSelectionStrategy,
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
    IReadOnlyList<HandoffGateRecordDto> GateRecords,
    OutcomeVerificationDto OutcomeVerification);

public sealed record OutcomeVerificationDto(
    string Status,
    bool LegacyUnverified,
    int CurrentRound,
    int MaxRounds,
    string PlanHashPrefix,
    string CandidateFingerprintPrefix,
    string CandidateFingerprint,
    Guid? ReleaseGateId,
    IReadOnlyList<OutcomeCriterionDto> Criteria,
    IReadOnlyList<OutcomeEvidenceDto> Evidence,
    IReadOnlyList<OutcomeCriterionResultDto> LatestResults,
    IReadOnlyList<string> FailedCriterionIds,
    IReadOnlyList<string> PendingOwnerRoles,
    bool Stale,
    bool PreviewRequired,
    bool ReleaseReady,
    DateTimeOffset? VerifiedAt,
    OutcomeResolutionGateDto? HumanResolutionGate);

public sealed record OutcomeCriterionDto(
    string Id,
    string Requirement,
    string Verification,
    IReadOnlyList<string> OwnerRoles,
    IReadOnlyList<OutcomeEvidenceKind> EvidenceKinds,
    bool CustomerVisible);

public sealed record OutcomeEvidenceDto(
    string EvidenceId,
    string CriterionId,
    OutcomeEvidenceDisposition Disposition,
    OutcomeEvidenceKind Kind,
    string Locator,
    string ObservedResult,
    int? ExitCode,
    string ProducerRole,
    Guid ProducerStepId,
    DateTimeOffset ProducedAt);

public sealed record OutcomeCriterionResultDto(
    string CriterionId,
    OutcomeCriterionStatus Status,
    string Rationale,
    IReadOnlyList<string> ResponsibleRoles,
    string? Remediation);

public sealed record OutcomeResolutionGateDto(
    Guid GateId,
    HandoffGateDecision Decision,
    string Summary,
    DateTimeOffset CreatedAt);

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
    string? WorkspaceRoot,
    bool? OutcomeVerificationEnabled,
    int? OutcomeVerificationMaxRounds);

public sealed record CopilotCliStatusDto(
    bool Ready,
    string Command,
    string ResolvedPath,
    string Version,
    string Detail,
    DateTimeOffset CheckedAt);

public sealed record ModelCatalogStatusDto(
    bool Ready,
    string CatalogVersion,
    int CandidateCount,
    string Detail,
    DateTimeOffset CheckedAt);

public sealed record TaskProfileDto(
    string Version,
    string Role,
    int Complexity,
    int ReasoningDepth,
    int ContextDemand,
    int ToolIntensity,
    IReadOnlyList<string> TaskTypeTags,
    TaskRisk Risk,
    string RiskReason,
    double Confidence,
    IReadOnlyList<string> Rationales);

public sealed record RoutingAlternativeDto(
    string Model,
    string Effort,
    int Rank,
    double PredictedQuality,
    double PredictedAcceptedTimeSeconds,
    double PredictedPremiumRequests,
    double Confidence,
    string Reason);

public sealed record RoutingDecisionDto(
    ModelSelectionStrategy Strategy,
    string SelectedModel,
    string SelectedEffort,
    double PredictedQuality,
    double PredictedAcceptedTimeSeconds,
    double PredictedPremiumRequests,
    bool PremiumUseEstimated,
    double Confidence,
    double Uncertainty,
    bool Exploration,
    string Reason,
    string AlgorithmVersion,
    int RerouteCount,
    IReadOnlyList<RoutingAlternativeDto> Alternatives);

public sealed record BootstrapDto(
    SettingsDto Settings,
    IReadOnlyList<AgentDto> Agents,
    IReadOnlyList<FlowSummaryDto> Flows,
    HarnessStatsDto Stats,
    bool CopilotCliAvailable,
    CopilotCliStatusDto CopilotCli,
    ModelCatalogStatusDto ModelCatalog,
    WorkflowStatusDto Workflow,
    bool FactoryEnabled,
    string FactoryDisabledReason);

public sealed record SaveSettingsRequest(
    string? RepositoryPath,
    string? RepositoryKnowledge,
    OutcomeType Outcome,
    int? MaxHandoffRetries,
    ModelSelectionStrategy ModelSelectionStrategy);

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

public enum ReleaseDecisionOutcome
{
    Approved,
    Rejected,
    RefreshQueued,
    Conflict
}

public sealed record FlowDecisionRequest(
    bool Approve,
    Guid GateId,
    string CandidateFingerprint,
    string Feedback);

public sealed record FlowDecisionResponse(
    ReleaseDecisionOutcome Outcome,
    FlowDetailDto Flow,
    string Message);

public sealed record OutcomeResolutionRequest(
    Guid GateId,
    OutcomeResolutionAction Action,
    string Reason);

public sealed record AbandonFlowResponse(
    Guid FlowId,
    FlowStatus Status,
    int ProcessesStopped,
    IReadOnlyList<int> ListeningPortsReleased,
    int CopilotSessionsDeleted,
    int WorktreesRemoved,
    int LocalBranchesDeleted,
    int RemoteBranchesDeleted);

public sealed record PreviewDto(
    Guid FlowId,
    string Title,
    string Request,
    string RepositoryName,
    int Iteration,
    FlowStatus Status,
    string OutcomeLabel,
    IReadOnlyList<PreviewArtifactDto> Artifacts,
    IReadOnlyList<FlowStepDto> DeliveredBy,
    OutcomeVerificationDto OutcomeVerification,
    DateTimeOffset GeneratedAt);

public sealed record PreviewArtifactDto(
    string Id,
    string Label,
    string Url,
    string OpenUrl);

public static class ApiMappings
{
    public static SettingsDto ToDto(this HarnessSettings settings) =>
        new(
            settings.RepositoryPath,
            settings.RepositoryKnowledge,
            settings.Outcome,
            settings.MaxHandoffRetries,
            settings.ModelSelectionStrategy,
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

    public static FlowDetailDto ToDetailDto(this FlowRun flow)
    {
        var outcomeState = string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson)
            ? null
            : OutcomeVerificationRules.DeserializeAggregate(
                flow.OutcomeVerificationJson);
        return new(
            flow.Id,
            flow.Title,
            flow.OriginalRequest,
            flow.ConsolidatedRequest,
            flow.Status,
            flow.Iteration,
            flow.RepositoryPath,
            flow.RepositoryKnowledge,
            flow.Outcome,
            flow.ModelSelectionStrategy,
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
                .Select(step => step.ToDto(outcomeState))
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
                .ToList(),
            ToOutcomeVerificationDto(flow));
    }

    public static FlowStepDto ToDto(
        this FlowStep step,
        OutcomeVerificationState? outcomeState = null) =>
        new(
            step.Id,
            step.Iteration,
            step.Sequence,
            step.AgentId,
            step.AgentName,
            step.AgentRole,
            step.Label,
            step.Model,
            step.ModelEffort,
            step.ModelReason,
            step.RoutingDecisions
                .Where(item => !item.Superseded)
                .OrderByDescending(item => item.CreatedAt)
                .Select(item => item.TaskProfile?.ToDto())
                .FirstOrDefault(),
            step.RoutingDecisions
                .Where(item => !item.Superseded)
                .OrderByDescending(item => item.CreatedAt)
                .Select(item => item.ToDto())
                .FirstOrDefault(),
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
                .ToList(),
            AssignedCriteria(step, outcomeState),
            OutcomeQaRound(step, outcomeState));

    public static TaskProfileDto ToDto(this TaskProfile profile) =>
        new(
            profile.Version,
            profile.Role,
            profile.Complexity,
            profile.ReasoningDepth,
            profile.ContextDemand,
            profile.ToolIntensity,
            TaskProfileRules.ReadTags(profile).Select(item => item.ToString()).ToList(),
            profile.Risk,
            profile.RiskReason,
            profile.Confidence,
            TaskProfileRules.ReadRationales(profile));

    public static RoutingDecisionDto ToDto(this RoutingDecision decision) =>
        new(
            decision.Strategy,
            decision.SelectedModel,
            decision.SelectedEffort,
            decision.PredictedQuality,
            decision.PredictedAcceptedTimeSeconds,
            decision.PredictedPremiumRequests,
            decision.PremiumUseEstimated,
            decision.Confidence,
            decision.Uncertainty,
            decision.Exploration,
            decision.Reason,
            decision.AlgorithmVersion,
            decision.RerouteCount,
            decision.Alternatives
                .OrderBy(item => item.Rank)
                .Select(item => new RoutingAlternativeDto(
                    item.Model,
                    item.Effort,
                    item.Rank,
                    item.PredictedQuality,
                    item.PredictedAcceptedTimeSeconds,
                    item.PredictedPremiumRequests,
                    item.Confidence,
                    item.Reason))
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

    public static OutcomeVerificationDto ToOutcomeVerificationDto(this FlowRun flow)
    {
        var currentStepIds = flow.Steps
            .Where(step => step.Iteration == flow.Iteration)
            .Select(step => step.Id)
            .ToHashSet();
        var releaseGate = flow.GateRecords
            .Where(gate =>
                gate.ActionType == HandoffActionType.Release &&
                !gate.Resolved &&
                currentStepIds.Contains(gate.FlowStepId))
            .OrderByDescending(gate => gate.DecidedAt)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson))
        {
            return new OutcomeVerificationDto(
                "LegacyUnverified",
                true,
                0,
                0,
                string.Empty,
                string.Empty,
                string.Empty,
                releaseGate?.Id,
                [],
                [],
                [],
                [],
                [],
                false,
                false,
                false,
                null,
                null);
        }

        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        var latestRound = state.Rounds
            .OrderByDescending(item => item.Round)
            .FirstOrDefault();
        var latestResults = latestRound?.Result?.Criteria ?? [];
        var resolutionGate = flow.GateRecords
            .Where(item =>
                item.ActionType == HandoffActionType.OutcomeResolution &&
                !item.Resolved)
            .OrderByDescending(item => item.DecidedAt)
            .FirstOrDefault();
        var currentFingerprint = state.CurrentCandidate?.Fingerprint;
        var releaseReady =
            state.Status == OutcomeVerificationStatus.Passed &&
            !state.Stale &&
            !string.IsNullOrWhiteSpace(currentFingerprint) &&
            string.Equals(
                currentFingerprint,
                state.VerifiedCandidateFingerprint,
                StringComparison.Ordinal);

        return new OutcomeVerificationDto(
            state.Status.ToString(),
            false,
            state.ActiveQaRound ??
            state.Rounds.OrderByDescending(item => item.Round)
                .Select(item => (int?)item.Round)
                .FirstOrDefault() ??
            0,
            state.MaxRounds,
            Prefix(state.AcceptancePlan?.Hash),
            Prefix(currentFingerprint),
            releaseReady
                ? state.VerifiedCandidateFingerprint ?? string.Empty
                : string.Empty,
            releaseReady
                ? releaseGate?.Id
                : null,
            state.AcceptancePlan?.Criteria.Select(criterion =>
                new OutcomeCriterionDto(
                    criterion.Id,
                    criterion.Requirement,
                    criterion.Verification,
                    criterion.OwnerRoles,
                    criterion.EvidenceKinds,
                    criterion.CustomerVisible)).ToList() ?? [],
            state.Evidence.Select(item => new OutcomeEvidenceDto(
                item.EvidenceId,
                item.CriterionId,
                item.Disposition,
                item.Kind,
                item.Locator,
                item.ObservedResult,
                item.ExitCode,
                item.ProducerRole,
                item.ProducerStepId,
                item.ProducedAt)).ToList(),
            latestResults.Select(item => new OutcomeCriterionResultDto(
                item.CriterionId,
                item.Status,
                item.Rationale,
                item.ResponsibleRoles,
                item.Remediation)).ToList(),
            latestResults
                .Where(item => item.Status != OutcomeCriterionStatus.PASS)
                .Select(item => item.CriterionId)
                .ToList(),
            state.PendingOwnerRoles,
            state.Stale,
            CandidateFingerprintService.RequiresPreview(
                state.AcceptancePlan),
            releaseReady,
            state.VerifiedAt,
            resolutionGate is null
                ? null
                : new OutcomeResolutionGateDto(
                    resolutionGate.Id,
                    resolutionGate.Decision,
                    resolutionGate.Summary,
                    resolutionGate.DecidedAt));
    }

    private static string Prefix(string? digest) =>
        string.IsNullOrWhiteSpace(digest)
            ? string.Empty
            : digest[..Math.Min(digest.Length, 19)];

    private static IReadOnlyList<string> AssignedCriteria(
        FlowStep step,
        OutcomeVerificationState? state)
    {
        if (state?.AcceptancePlan is null)
        {
            return [];
        }
        if (step.Kind == FlowStepKind.OutcomeQa)
        {
            return state.AcceptancePlan.Criteria.Select(item => item.Id).ToList();
        }
        if (step.AgentRole == "team-lead")
        {
            return state.AcceptancePlan.Criteria.Select(item => item.Id).ToList();
        }
        return state.AcceptancePlan.Criteria
            .Where(item => item.OwnerRoles.Contains(
                step.AgentRole,
                StringComparer.Ordinal))
            .Select(item => item.Id)
            .ToList();
    }

    private static int? OutcomeQaRound(
        FlowStep step,
        OutcomeVerificationState? state) =>
        step.OutcomeQaRound ??
        state?.Rounds.FirstOrDefault(item => item.QaStepId == step.Id)?.Round ??
        (state?.ActiveQaStepId == step.Id ? state.ActiveQaRound : null);
}

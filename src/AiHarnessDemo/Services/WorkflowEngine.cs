using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using AiHarnessDemo.Data;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Security;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Core.Workflow;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

public sealed class WorkflowEngine(
    IDbContextFactory<HarnessDbContext> databaseFactory,
    AgentCatalog agentCatalog,
    IModelRouter modelRouter,
    BootstrapTaskProfileFactory profileFactory,
    RoutingObservationRecorder observationRecorder,
    IWorkspaceManager workspaceManager,
    IAgentRunner agentRunner,
    HandoffGateEngine handoffGate,
    CopilotSessionJournal sessionJournal,
    WorkflowDefinitionProvider workflowProvider,
    ILogger<WorkflowEngine> logger,
    IPublishedOutcomeVerifier? publicationVerifier = null,
    CandidateFingerprintService? candidateFingerprintService = null,
    IVerifiedCandidatePublisher? candidatePublisher = null,
    FlowAgentSnapshotService? flowAgentSnapshotService = null,
    TeamPlanValidator? teamPlanValidator = null,
    AdvisoryArtifactCatalog? advisoryArtifactCatalog = null,
    MissingQualificationCoordinator? missingQualificationCoordinator = null,
    FlowLifecycleCoordinator? lifecycleCoordinator = null,
    PermissionProfileResolver? permissionProfileResolver = null,
    IReviewedCandidateService? reviewedCandidateService = null,
    LinkedFlowCoordinator? linkedFlowCoordinator = null,
    AgentManifestStager? manifestStager = null,
    DeliveryReadinessService? deliveryReadinessService = null,
    IServer? server = null)
{
    internal const string ApprovedPublicationLabel = "Publish customer-approved outcome";
    internal const string PreMortemRole = "pre-mortem-sceptic";
    internal const string TeamLeadPlanStepKey = "team-plan";
    internal const string RefinementIntakePlanStepKey =
        "account-manager:refinement";
    internal const int MaximumFailedOutputCharacters = 32_000;
    internal const string RepositoryKnowledgeUnchangedEventType =
        "repository.knowledge-unchanged";
    internal const string RepositoryKnowledgeRefreshSkippedEventType =
        "repository.knowledge-refresh-skipped";
    private const string ManualRestartLabelPrefix = "Manual restart of ";
    private const string StudioContractCorrectionLabelPrefix =
        "Correct invalid response from ";
    internal static string ApprovedPublicationAssignment(OutcomeType outcome) =>
        OutcomeTypeRules.RequireDelivery(outcome) switch
        {
            OutcomeType.PullRequest =>
                "Customer approval is recorded. Publish exactly the already-reviewed outcome. " +
                "Do not change any product or source file. Report the sealed candidate identity " +
                "and the resulting pull request.",
            OutcomeType.Commit =>
                "Customer approval is recorded. Finalize the already-verified local commit outcome " +
                "without changing any product or source file, pushing a branch, or creating a pull " +
                "request. Report the exact sealed commit SHA.",
            _ => throw new UnreachableException()
        };
    internal const string HostControlledPublicationAssignment =
        "Customer approval is recorded. Inspect the already-verified candidate and prepare the " +
        "final release narrative, but do not modify product files, push, or create a pull request. " +
        "The harness will publish only the immutable verified commit and tree identities after " +
        "this Copilot CLI turn completes.";

    private readonly Lock _concurrencyLock = new();
    private readonly SemaphoreSlim _learningGate = new(1, 1);
    private readonly SemaphoreSlim _manualRestartGate = new(1, 1);
    private readonly FlowLifecycleCoordinator _lifecycle =
        lifecycleCoordinator ?? new FlowLifecycleCoordinator();
    private readonly PermissionProfileResolver _permissionResolver =
        permissionProfileResolver ?? new PermissionProfileResolver();
    private readonly FlowAgentSnapshotService _flowAgentSnapshots =
        flowAgentSnapshotService ?? new FlowAgentSnapshotService(databaseFactory, agentCatalog);
    private readonly TeamPlanValidator _teamPlanValidator =
        teamPlanValidator ?? new TeamPlanValidator();
    private readonly AdvisoryArtifactCatalog _advisoryArtifacts =
        advisoryArtifactCatalog ?? new AdvisoryArtifactCatalog(workflowProvider);
    private readonly MissingQualificationCoordinator _missingQualifications =
        missingQualificationCoordinator ?? new MissingQualificationCoordinator(
            databaseFactory,
            profileFactory,
            lifecycleCoordinator ?? new FlowLifecycleCoordinator());
    private readonly IReviewedCandidateService? _reviewedCandidates =
        reviewedCandidateService ??
        (candidateFingerprintService is null
            ? null
            : new ReviewedCandidateService(candidateFingerprintService));
    private readonly DeliveryReadinessService _readiness =
        deliveryReadinessService ?? new DeliveryReadinessService();
    private readonly LinkedFlowCoordinator? _linkedFlows =
        linkedFlowCoordinator;
    private readonly AgentManifestStager _manifestStager =
        manifestStager ?? new AgentManifestStager();
    private readonly ConcurrentDictionary<Guid, byte> _executionClaims = new();
    private int _activeFlows;

    private sealed record PreparedPublicationCompletion(
        string VerificationOutput,
        RepositoryKnowledgeRecap? KnowledgeRecap);

    internal int ActiveFlowCount
    {
        get
        {
            lock (_concurrencyLock)
            {
                return _activeFlows;
            }
        }
    }

    private (ValidatedTeamPlan Plan, string RawJson)
        ParseValidatedStudioTeamPlanOutput(
            string output,
            TeamPlanValidationContext validationContext)
    {
        try
        {
            var handoff = AgentHandoffInspector.ParseDynamic(output);
            if (handoff.IsPushback)
            {
                throw new InvalidOperationException(
                    "Team Lead planning cannot return pushback; return Planned or MissingQualification.");
            }
        }
        catch (InvalidOperationException exception)
        {
            throw new TeamPlanContractException([exception.Message]);
        }

        var parsed = TeamPlanParser.Parse(output);
        return (
            _teamPlanValidator.Validate(parsed.Document, validationContext),
            parsed.RawJson);
    }

    internal Task<bool> IsRunnableAsync(
        Guid flowId,
        CancellationToken cancellationToken) =>
        IsRunnableCoreAsync(flowId, cancellationToken);

    private async Task<bool> IsRunnableCoreAsync(
        Guid flowId,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        return await database.Flows
            .AsNoTracking()
            .AnyAsync(
                flow =>
                    flow.Id == flowId &&
                    (flow.Status == FlowStatus.Queued ||
                     flow.Status == FlowStatus.Running ||
                     flow.Status == FlowStatus.Reworking),
                cancellationToken);
    }

    public async Task RunAsync(Guid flowId, CancellationToken cancellationToken)
    {
        if (!_executionClaims.TryAdd(flowId, 0))
        {
            logger.LogInformation(
                "Ignored duplicate execution claim for factory flow {FlowId}.",
                flowId);
            return;
        }
        try
        {
            await AcquireConcurrencySlotAsync(cancellationToken);
            logger.LogInformation(
                "Flow {FlowId} entered execution with {ActiveFlowCount} active flow(s).",
                flowId,
                ActiveFlowCount);
            try
            {
                using var executionSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    handoffGate.KillSwitchToken);
                await RunCoreAsync(flowId, executionSource.Token);
                logger.LogInformation(
                    "Flow {FlowId} completed its current execution cycle.",
                    flowId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Factory flow {FlowId} failed", flowId);
                await MarkFailedAsync(flowId, exception.Message, cancellationToken);
            }
            finally
            {
                int activeFlowCount;
                lock (_concurrencyLock)
                {
                    _activeFlows--;
                    activeFlowCount = _activeFlows;
                }
                logger.LogInformation(
                    "Flow {FlowId} released its execution slot; {ActiveFlowCount} active flow(s) remain.",
                    flowId,
                    activeFlowCount);
            }
        }
        finally
        {
            _executionClaims.TryRemove(flowId, out _);
        }
    }

    private async Task AcquireConcurrencySlotAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var limit = Math.Max(
                1,
                workflowProvider.GetValidated().Config.Agent.MaxConcurrentAgents);
            lock (_concurrencyLock)
            {
                if (_activeFlows < limit)
                {
                    _activeFlows++;
                    return;
                }
            }

            await Task.Delay(100, cancellationToken);
        }
    }

    private async Task RunCoreAsync(Guid flowId, CancellationToken cancellationToken)
    {
        FlowRun flow;

        await using (var lifecycleLease =
                     await _lifecycle.EnterAsync(flowId, cancellationToken))
        await using (var database = await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            flow = await database.Flows.SingleOrDefaultAsync(
                       item => item.Id == flowId,
                       cancellationToken)
                   ?? throw new KeyNotFoundException($"Factory flow '{flowId}' was not found.");

            if (flow.Status is not (
                    FlowStatus.Queued or
                    FlowStatus.Running or
                    FlowStatus.Reworking))
            {
                return;
            }
            _lifecycle.Transition(flow, FlowStatus.Running);
            flow.FailureReason = string.Empty;
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                Type = "flow.started",
                Message = $"Factory iteration {flow.Iteration} started."
            });
            await database.SaveChangesAsync(cancellationToken);
        }

        await using var invocationDatabase =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var workspaceInvocation = await invocationDatabase.FlowSteps
            .AsNoTracking()
            .Where(step =>
                step.FlowRunId == flow.Id &&
                step.Iteration == flow.Iteration &&
                step.Status == StepStatus.Pending)
            .OrderBy(step => step.Sequence)
            .ThenBy(step => step.Attempt)
            .Select(step =>
                (ExecutionInvocationKind?)step.InvocationKind)
            .FirstOrDefaultAsync(cancellationToken) ??
            ExecutionInvocationKind.Worker;
        var workspace = await workspaceManager.PrepareForInvocationAsync(
            flow,
            workspaceInvocation,
            cancellationToken);
        await UpdateWorkspaceAsync(flowId, workspace, cancellationToken);
        await RunFlowAsync(
            flow,
            workspace.Path,
            cancellationToken);
    }

    private async Task RunFlowAsync(
        FlowRun flow,
        string workspacePath,
        CancellationToken cancellationToken)
    {
        var workflow = workflowProvider.GetEffective();
        if (flow.Kind == FlowKind.Advisory)
        {
            var baseline = await _advisoryArtifacts.EnsureBaselineAsync(
                flow,
                workspacePath,
                WorkspaceMode.AdvisoryReadOnly,
                cancellationToken);
            await RecordAdvisoryWorkspacePolicyAsync(
                flow.Id,
                baseline,
                cancellationToken);
        }
        List<FlowAgentSnapshot> snapshots;
        FlowPlanDocument? persistedDocument;
        await using (var database =
                     await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            snapshots = await database.FlowAgentSnapshots
                .AsNoTracking()
                .Where(item => item.FlowRunId == flow.Id)
                .OrderBy(item => item.AgentId)
                .ToListAsync(cancellationToken);
            persistedDocument = await database.FlowPlanDocuments
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item =>
                        item.FlowRunId == flow.Id &&
                        item.Iteration == flow.Iteration,
                    cancellationToken);
        }
        if (snapshots.Count == 0)
        {
            throw new InvalidOperationException(
                "Studio planning requires an immutable flow agent snapshot.");
        }

        var teamLeadSnapshot = snapshots.SingleOrDefault(snapshot =>
            snapshot.EnabledAtSnapshot &&
            string.Equals(snapshot.AgentId, "team-lead", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "Studio planning requires the enabled Team Lead flow snapshot.");
        var maximumPreMortemRounds = await GetMaxHandoffRetriesAsync(cancellationToken);
        var preMortemSnapshot = snapshots.SingleOrDefault(snapshot =>
            snapshot.EnabledAtSnapshot &&
            string.Equals(
                snapshot.AgentId,
                PreMortemRole,
                StringComparison.Ordinal));
        var preMortemAvailableInSnapshot = preMortemSnapshot is not null;
        var preMortemAvailableForNewPlan =
            preMortemAvailableInSnapshot && maximumPreMortemRounds > 0;
        var validationContext = persistedDocument is null
            ? TeamPlanValidationContext.FromWorkflow(
                flow.Kind,
                snapshots,
                preMortemAvailableForNewPlan,
                workflow)
            : TeamPlanValidationContext.ForPersistedPlan(
                flow.Kind,
                snapshots,
                preMortemAvailableInSnapshot);
        Guid? refinementIntakeStepId = null;
        if (persistedDocument is null)
        {
            refinementIntakeStepId = await EnsureStudioRefinementIntakeAsync(
                flow,
                workspacePath,
                snapshots,
                workflow.Revision,
                cancellationToken);
        }

        ValidatedTeamPlan plan;
        FlowStep? contractSource;
        string rawJson;
        if (persistedDocument is not null)
        {
            var parsed = TeamPlanParser.ParseJson(persistedDocument.RawJson);
            plan = _teamPlanValidator.Validate(parsed.Document, validationContext);
            rawJson = parsed.RawJson;
            await using var database =
                await databaseFactory.CreateDbContextAsync(cancellationToken);
            contractSource = await database.FlowSteps
                .AsNoTracking()
                .Where(step =>
                    step.FlowRunId == flow.Id &&
                    step.Iteration == flow.Iteration &&
                    step.PlanStepKey == TeamLeadPlanStepKey &&
                    step.Status == StepStatus.Completed)
                .OrderByDescending(step => step.Sequence)
                .ThenByDescending(step => step.Attempt)
                .FirstOrDefaultAsync(cancellationToken);
        }
        else
        {
            (plan, rawJson, contractSource) =
                await ExecuteStudioTeamLeadPlanAsync(
                    flow,
                    workspacePath,
                    teamLeadSnapshot,
                    snapshots,
                    validationContext,
                    preMortemAvailableForNewPlan,
                    maximumPreMortemRounds,
                    workflow.Revision,
                    refinementIntakeStepId,
                    cancellationToken);
        }

        await PersistAndMaterializeStudioPlanAsync(
            flow,
            plan,
            rawJson,
            contractSource,
            snapshots,
            preMortemSnapshot,
            workflow,
            cancellationToken);
        if (plan.IsMissingQualification)
        {
            var qualification = plan.Document.MissingQualification
                ?? throw new InvalidOperationException(
                    "A validated missing-qualification plan has no qualification data.");
            await _missingQualifications.BlockAsync(
                flow.Id,
                flow.Iteration,
                qualification,
                contractSource?.Id,
                workflow.Revision,
                (stepId, token) => ExecuteStepAsync(
                    flow.Id,
                    stepId,
                    workspacePath,
                    "Customer-safe missing qualification explanation",
                    complexity: 3,
                    token),
                cancellationToken);
            return;
        }

        var planSummary = string.Join(
            " -> ",
            plan.OrderedSteps
                .Where(step => step.Stage == PlanStage.BeforeReview)
                .Select(step => $"{step.Id} ({step.AgentId})"));
        var complexity = plan.OrderedSteps
            .Where(step => step.Stage == PlanStage.BeforeReview)
            .Select(step => step.TaskProfile?.Complexity ?? 1)
            .DefaultIfEmpty(1)
            .Max();
        var upstreamOwners =
            new Dictionary<string, AgentRecord>(StringComparer.Ordinal);

        await ExecutePendingCausalRetriesAsync(
            flow,
            workspacePath,
            planSummary,
            complexity,
            upstreamOwners,
            cancellationToken);
        await RecoverUnresolvedPushbacksAsync(
            flow,
            workspacePath,
            planSummary,
            complexity,
            upstreamOwners,
            cancellationToken);
        await ThrowIfUnresolvedFailureAsync(
            flow.Id,
            flow.Iteration,
            cancellationToken);
        await RecoverUnresolvedPreMortemsAsync(flow, cancellationToken);

        while (await GetNextPendingStepIdAsync(
                   flow.Id,
                   flow.Iteration,
                   cancellationToken) is { } stepId)
        {
            if (!await ShouldExecuteStepAsync(stepId, cancellationToken))
            {
                continue;
            }
            await HydrateRetryAssignmentAsync(stepId, cancellationToken);
            await ExecutePendingStepAsync(
                flow,
                stepId,
                workspacePath,
                planSummary,
                complexity,
                upstreamOwners,
                cancellationToken);
        }

        await ThrowIfUnresolvedFailureAsync(
            flow.Id,
            flow.Iteration,
            cancellationToken);
        await MarkWaitingForReviewAsync(flow.Id, cancellationToken);
    }

    private async Task<Guid?> EnsureStudioRefinementIntakeAsync(
        FlowRun flow,
        string workspacePath,
        IReadOnlyCollection<FlowAgentSnapshot> snapshots,
        string workflowRevision,
        CancellationToken cancellationToken)
    {
        if (flow.Iteration <= 1)
        {
            return null;
        }

        bool isRefinement;
        string? alreadyNormalizedBrief;
        Guid? existingStepId;
        await using (var database =
                     await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            isRefinement = await (
                    from gate in database.GateRecords.AsNoTracking()
                    join step in database.FlowSteps.AsNoTracking()
                        on gate.FlowStepId equals step.Id
                    where gate.FlowRunId == flow.Id &&
                          gate.ActionType == HandoffActionType.CustomerReview &&
                          gate.Resolved &&
                          gate.ReviewDecision == ReviewDecision.RefinementRequested &&
                          step.Iteration == flow.Iteration - 1
                    select gate.Id)
                .AnyAsync(cancellationToken);
            alreadyNormalizedBrief = await (
                    from flowEvent in database.FlowEvents.AsNoTracking()
                    join step in database.FlowSteps.AsNoTracking()
                        on flowEvent.FlowStepId equals step.Id
                    where flowEvent.FlowRunId == flow.Id &&
                          flowEvent.Type == "flow.refinement-normalized" &&
                          step.Iteration == flow.Iteration
                    orderby flowEvent.CreatedAt descending
                    select flowEvent.DataJson)
                .FirstOrDefaultAsync(cancellationToken);
            existingStepId = await database.FlowSteps
                .AsNoTracking()
                .Where(step =>
                    step.FlowRunId == flow.Id &&
                    step.Iteration == flow.Iteration &&
                    step.PlanStepKey == RefinementIntakePlanStepKey)
                .OrderBy(step => step.Sequence)
                .Select(step => (Guid?)step.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }
        if (!isRefinement)
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(alreadyNormalizedBrief))
        {
            var normalized = IntakeParser.ParseJson(alreadyNormalizedBrief);
            flow.ConsolidatedRequest = normalized.NormalizedBriefJson;
            return existingStepId;
        }

        var accountManagerSnapshot = snapshots.SingleOrDefault(snapshot =>
            snapshot.EnabledAtSnapshot &&
            string.Equals(
                snapshot.AgentId,
                "account-manager",
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "Studio refinement requires the enabled Account Manager flow snapshot.");
        var accountManager = SnapshotAgent(accountManagerSnapshot);
        var assignment = $$"""
            Normalize the customer's explicit refinement request into the complete brief for the
            next iteration. Preserve the flow kind {{flow.Kind}} and all still-applicable accepted
            scope. The customer has explicitly requested this refinement, so return Confirmed;
            this is not acceptance of the final outcome. Do not ask another question unless the
            supplied request has no actionable customer outcome.

            The current refinement is intentionally the first section of the supplied customer
            task. Treat it as authoritative over the reviewed outcome and previous confirmed brief
            that follow it. Never replace it with an older refinement or omit its requested changes.

            Return exactly one intake document. Use the existing task title, FlowKind
            {{flow.Kind}}, and a complete normalized Brief containing the updated goal, details,
            success criteria, constraints, and assumptions.
            """;
        var stepId = await AddStepAsync(
            flow,
            accountManager,
            sequence: 0,
            label: "Normalize customer refinement",
            cancellationToken,
            inputSummary: assignment,
            planStepKey: RefinementIntakePlanStepKey,
            planDuties: [PlanDuty.Analyze],
            invocationKind: ExecutionInvocationKind.Intake,
            workflowRevision: workflowRevision);
        stepId = await ResolveEffectiveManualRetryStepIdAsync(
            stepId,
            cancellationToken);
        await EnsureBootstrapProfileAsync(
            flow,
            accountManager.Role,
            stepId,
            cancellationToken,
            RefinementIntakePlanStepKey,
            accountManager.Id);

        var normalizedStep = await LoadOrExecuteStepAsync(
            flow,
            stepId,
            workspacePath,
            "Account Manager refinement normalization",
            complexity: 4,
            new Dictionary<string, AgentRecord>(StringComparer.Ordinal),
            cancellationToken);
        if (normalizedStep.Status != StepStatus.Completed)
        {
            throw new InvalidOperationException(
                "Account Manager did not complete refinement normalization.");
        }
        var response = IntakeParser.Parse(normalizedStep.OutputSummary);
        if (response.Document.Status != IntakeStatus.Confirmed ||
            response.Document.FlowKind != flow.Kind)
        {
            throw new InvalidOperationException(
                "Account Manager refinement normalization must preserve the flow kind and return Confirmed.");
        }

        await using (var database =
                     await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction =
                await database.Database.BeginTransactionAsync(cancellationToken);
            var stored = await database.Flows
                .Include(item => item.Events)
                .Include(item => item.Messages)
                .SingleAsync(item => item.Id == flow.Id, cancellationToken);
            var existing = stored.Events.SingleOrDefault(item =>
                item.Type == "flow.refinement-normalized" &&
                item.FlowStepId == normalizedStep.Id);
            if (existing is null)
            {
                stored.ConsolidatedRequest = response.NormalizedBriefJson;
                stored.Messages.Add(new FlowMessage
                {
                    FlowRunId = stored.Id,
                    Role = ConversationRole.AccountManager,
                    Content = response.Document.CustomerReply,
                    IsQuestion = false
                });
                stored.Events.Add(new FlowEvent
                {
                    FlowRunId = stored.Id,
                    FlowStepId = normalizedStep.Id,
                    Type = "flow.refinement-normalized",
                    Message =
                        "Account Manager normalized the customer refinement before Team Lead replanning.",
                    DataJson = IntakeParser.Serialize(response.Document)
                });
                stored.UpdatedAt = DateTimeOffset.UtcNow;
                await database.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        flow.ConsolidatedRequest = response.NormalizedBriefJson;
        return normalizedStep.Id;
    }

    private async Task<(ValidatedTeamPlan Plan, string RawJson, FlowStep Source)>
        ExecuteStudioTeamLeadPlanAsync(
            FlowRun flow,
            string workspacePath,
            FlowAgentSnapshot teamLeadSnapshot,
            IReadOnlyCollection<FlowAgentSnapshot> snapshots,
            TeamPlanValidationContext validationContext,
            bool preMortemAvailable,
            int maximumPreMortemRounds,
            string workflowRevision,
            Guid? dependsOnStepId,
            CancellationToken cancellationToken)
    {
        var lead = SnapshotAgent(teamLeadSnapshot);
        var assignment = BuildStudioTeamLeadAssignment(
            flow,
            snapshots,
            validationContext,
            preMortemAvailable,
            maximumPreMortemRounds);
        var leadStepId = await AddStepAsync(
            flow,
            lead,
            sequence: 10,
            label: "Plan the dynamic team",
            cancellationToken,
            inputSummary: assignment,
            planStepKey: TeamLeadPlanStepKey,
            planDuties: [PlanDuty.Analyze, PlanDuty.Design],
            invocationKind: ExecutionInvocationKind.Planning,
            workflowRevision: workflowRevision,
            dependsOnStepId: dependsOnStepId);
        leadStepId = await ResolveEffectiveManualRetryStepIdAsync(
            leadStepId,
            cancellationToken);
        await EnsureBootstrapProfileAsync(
            flow,
            lead.Role,
            leadStepId,
            cancellationToken,
            TeamLeadPlanStepKey,
            lead.Id);

        var upstreamOwners =
            new Dictionary<string, AgentRecord>(StringComparer.Ordinal);
        var leadResult = await LoadOrExecuteStepAsync(
            flow,
            leadStepId,
            workspacePath,
            "Dynamic Team Lead planning",
            complexity: 8,
            upstreamOwners,
            cancellationToken);
        try
        {
            var parsed = ParseValidatedStudioTeamPlanOutput(
                leadResult.OutputSummary,
                validationContext);
            return (
                parsed.Plan,
                parsed.RawJson,
                leadResult);
        }
        catch (TeamPlanContractException firstFailure)
        {
            await RecordPlanningRejectionAsync(
                leadResult,
                cancellationToken);
            var validationErrors = string.Join(
                Environment.NewLine,
                firstFailure.Errors.Select(error => $"- {error}"));
            var correctionStepId = await AddStepAsync(
                flow,
                lead,
                sequence: FirstCorrectionSequence(leadResult.Sequence),
                label: "Correct dynamic Team Lead plan",
                cancellationToken,
                attempt: 2,
                inputSummary:
                    "Your previous team plan result was invalid. Resume the same Team Lead " +
                    "session and return a complete replacement under 10,000 characters. Start with " +
                    "exactly one HANDOFF_STATUS: COMPLETE line, followed by the complete document " +
                    "between the exact TEAM_PLAN " +
                    $"sentinels. Validation errors:{Environment.NewLine}{validationErrors}",
                retryOfStepId: GetRetryRootId(leadResult),
                stableSemanticRootId: GetStableSemanticRootId(leadResult),
                planStepKey: TeamLeadPlanStepKey,
                planDuties: [PlanDuty.Analyze, PlanDuty.Design],
                invocationKind: ExecutionInvocationKind.Planning,
                workflowRevision: workflowRevision);
            correctionStepId = await ResolveEffectiveManualRetryStepIdAsync(
                correctionStepId,
                cancellationToken);
            await EnsureBootstrapProfileAsync(
                flow,
                lead.Role,
                correctionStepId,
                cancellationToken,
                TeamLeadPlanStepKey,
                lead.Id);
            await AddEventOnceAsync(
                flow.Id,
                correctionStepId,
                "plan.validation-correction",
                "Team Lead returned an invalid team plan result. One bounded correction turn was scheduled with the exact validation errors.",
                cancellationToken);
            var correction = await LoadOrExecuteStepAsync(
                flow,
                correctionStepId,
                workspacePath,
                "Dynamic Team Lead planning correction",
                complexity: 8,
                upstreamOwners,
                cancellationToken);
            try
            {
                var parsed = ParseValidatedStudioTeamPlanOutput(
                    correction.OutputSummary,
                    validationContext);
                return (
                    parsed.Plan,
                    parsed.RawJson,
                    correction);
            }
            catch (TeamPlanContractException secondFailure)
            {
                await RecordPlanningRejectionAsync(
                    correction,
                    cancellationToken);
                await MarkContractValidationFailedAsync(
                    correction.Id,
                    "Team Lead returned an invalid corrected team plan result.",
                    cancellationToken);
                await AddEventOnceAsync(
                    flow.Id,
                    correction.Id,
                    "plan.validation-failed",
                    "Team Lead returned an invalid team plan result on the correction turn: " +
                    string.Join("; ", secondFailure.Errors),
                    cancellationToken);
                throw new InvalidOperationException(
                    "Team Lead plan remained invalid after one correction: " +
                    string.Join("; ", secondFailure.Errors),
                    secondFailure);
            }
        }
    }

    private async Task<FlowStep> LoadOrExecuteStepAsync(
        FlowRun flow,
        Guid stepId,
        string workspacePath,
        string planSummary,
        int complexity,
        IReadOnlyDictionary<string, AgentRecord> upstreamOwners,
        CancellationToken cancellationToken)
    {
        if (await ShouldExecuteStepAsync(stepId, cancellationToken))
        {
            return await ExecuteWithPushbackRecoveryAsync(
                flow,
                flow.Id,
                stepId,
                workspacePath,
                planSummary,
                complexity,
                upstreamOwners,
                cancellationToken);
        }

        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        return await database.FlowSteps
            .AsNoTracking()
            .SingleAsync(item => item.Id == stepId, cancellationToken);
    }

    private async Task RecordPlanningRejectionAsync(
        FlowStep step,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var alreadyRecorded = await database.RoutingObservations
            .AsNoTracking()
            .AnyAsync(
                observation =>
                    observation.FlowStepId == step.Id &&
                    observation.OutcomeKind == "invalid-team-plan",
                cancellationToken);
        if (alreadyRecorded)
        {
            return;
        }
        await observationRecorder.RecordCompletionAsync(
            step.Id,
            accepted: false,
            step.DurationMilliseconds,
            Math.Max(1, step.ExecutionAttempts),
            "invalid-team-plan",
            cancellationToken);
    }

    private async Task PersistAndMaterializeStudioPlanAsync(
        FlowRun flow,
        ValidatedTeamPlan plan,
        string rawJson,
        FlowStep? contractSource,
        IReadOnlyCollection<FlowAgentSnapshot> snapshots,
        FlowAgentSnapshot? preMortemSnapshot,
        WorkflowDefinition workflow,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);
        var storedFlow = await database.Flows.SingleAsync(
            item => item.Id == flow.Id,
            cancellationToken);
        var document = await database.FlowPlanDocuments.SingleOrDefaultAsync(
            item =>
                item.FlowRunId == flow.Id &&
                item.Iteration == flow.Iteration,
            cancellationToken);
        if (document is null)
        {
            document = new FlowPlanDocument
            {
                FlowRunId = flow.Id,
                Iteration = flow.Iteration,
                Disposition = plan.Document.Disposition!.Value.ToString(),
                RawJson = rawJson
            };
            database.FlowPlanDocuments.Add(document);
        }
        else if (!string.Equals(document.RawJson, rawJson, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The accepted Team Lead plan is immutable for this flow iteration.");
        }

        storedFlow.OutcomeOwnerPlanStepKey = plan.OutcomeOwner?.Id;
        storedFlow.PublicationPlanStepKey = plan.PublicationStep?.Id;
        storedFlow.UpdatedAt = DateTimeOffset.UtcNow;
        if (plan.IsMissingQualification)
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        if (DeliveryReadinessService.AppliesTo(storedFlow) &&
            plan.Document.AcceptanceCriteria is { Count: > 0 } acceptanceCriteria)
        {
            var acceptancePlan = new DeliveryAcceptancePlan(
                acceptanceCriteria);
            var acceptanceData = DeliveryReadinessService.SerializeAcceptancePlan(
                acceptancePlan,
                flow.Iteration,
                contractSource?.Id ?? Guid.Empty);
            if (!await database.FlowEvents.AnyAsync(
                    item =>
                        item.FlowRunId == flow.Id &&
                        item.Type == DeliveryReadinessService.AcceptancePlanEventType &&
                        item.DataJson == acceptanceData,
                    cancellationToken))
            {
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = contractSource?.Id,
                    Type = DeliveryReadinessService.AcceptancePlanEventType,
                    Message =
                        $"Recorded {acceptanceCriteria.Count} planned Delivery acceptance criteria for iteration {flow.Iteration}.",
                    DataJson = acceptanceData
                });
            }
        }

        var snapshotById = snapshots.ToDictionary(
            item => item.AgentId,
            StringComparer.Ordinal);
        var existingSteps = await database.FlowSteps
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.Iteration == flow.Iteration)
            .ToListAsync(cancellationToken);
        var dependencyStepId = contractSource?.Id ??
            existingSteps
                .Where(step =>
                    step.PlanStepKey == TeamLeadPlanStepKey &&
                    step.Status == StepStatus.Completed)
                .OrderByDescending(step => step.Sequence)
                .Select(step => (Guid?)step.Id)
                .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "An accepted Studio plan has no completed Team Lead source step.");
        var sequence = Math.Max(
            20,
            existingSteps
                .Where(step => step.PlanStepKey == TeamLeadPlanStepKey)
                .Select(step => step.Sequence)
                .DefaultIfEmpty(10)
                .Max() + 10);

        foreach (var planned in plan.OrderedSteps.Where(
                     step => step.Stage == PlanStage.BeforeReview))
        {
            var snapshot = snapshotById[planned.AgentId];
            var step = existingSteps
                .Where(item =>
                    item.PlanStepKey == planned.Id &&
                    item.InvocationKind == ExecutionInvocationKind.Worker &&
                    item.RetryOfStepId == null &&
                    item.PreMortemReviewStepId == null)
                .OrderBy(item => item.Sequence)
                .FirstOrDefault();
            if (step is null)
            {
                step = new FlowStep
                {
                    FlowRunId = flow.Id,
                    Iteration = flow.Iteration,
                    Sequence = sequence,
                    AgentId = snapshot.AgentId,
                    AgentName = snapshot.Name,
                    AgentRole = snapshot.Role,
                    Label = planned.Assignment,
                    PlanStepKey = planned.Id,
                    PlanDutiesJson = SerializePlanDuties(planned.Duties ?? []),
                    PlanStage = planned.Stage,
                    InvocationKind = ExecutionInvocationKind.Worker,
                    IsOutcomeOwner = planned.OutcomeOwner,
                    PermissionProfile = InitialPermissionProfile(
                        flow.Kind,
                        planned.Stage,
                        planned.Duties ?? []),
                    WorkflowRevision = workflow.Revision,
                    Status = StepStatus.Pending,
                    InputSummary =
                        $"{planned.Assignment}{Environment.NewLine}{Environment.NewLine}" +
                        $"Selection rationale: {planned.Justification}",
                    DependsOnStepId = dependencyStepId
                };
                step.StableSemanticRootId = step.Id;
                database.FlowSteps.Add(step);
                existingSteps.Add(step);
            }
            else
            {
                EnsureMaterializedStepMatches(step, planned, snapshot);
            }

            if (!await database.TaskProfiles.AnyAsync(
                    item => item.FlowStepId == step.Id,
                    cancellationToken))
            {
                database.TaskProfiles.Add(CreateStudioTaskProfile(
                    planned,
                    snapshot,
                    flow.Id,
                    flow.Iteration,
                    step.Id,
                    plan.Document.PreMortemCheckpoints?.Contains(
                        planned.Id,
                        StringComparer.Ordinal) == true));
            }
            dependencyStepId = step.Id;
            sequence = Math.Max(sequence + 10, step.Sequence + 10);

            if (plan.Document.PreMortemCheckpoints?.Contains(
                    planned.Id,
                    StringComparer.Ordinal) != true)
            {
                continue;
            }
            if (preMortemSnapshot is null)
            {
                throw new InvalidOperationException(
                    "A validated pre-mortem checkpoint has no enabled snapshot agent.");
            }
            var reviewKey = PreMortemPlanStepKey(planned.Id, round: 1);
            var reviewAttempts = existingSteps
                .Where(item => string.Equals(
                    item.PlanStepKey,
                    reviewKey,
                    StringComparison.Ordinal))
                .ToList();
            var reviewRoots = reviewAttempts
                .Where(item =>
                    item.RetryOfStepId is null &&
                    item.PreMortemReviewStepId is null)
                .ToList();
            if (reviewRoots.Count > 1)
            {
                throw new InvalidOperationException(
                    $"The materialized pre-mortem checkpoint '{reviewKey}' has duplicate canonical roots.");
            }
            var review = reviewRoots.SingleOrDefault();
            if (review is null)
            {
                if (reviewAttempts.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"The materialized pre-mortem checkpoint '{reviewKey}' has attempts without a canonical root.");
                }
                review = new FlowStep
                {
                    FlowRunId = flow.Id,
                    Iteration = flow.Iteration,
                    Sequence = sequence,
                    AgentId = preMortemSnapshot.AgentId,
                    AgentName = preMortemSnapshot.Name,
                    AgentRole = preMortemSnapshot.Role,
                    Label = $"Pre-mortem review of {snapshot.Name} (round 1)",
                    PlanStepKey = reviewKey,
                    PlanDutiesJson = SerializePlanDuties(
                        [PlanDuty.Analyze, PlanDuty.Verify]),
                    PlanStage = PlanStage.BeforeReview,
                    InvocationKind = ExecutionInvocationKind.PreMortem,
                    IsOutcomeOwner = false,
                    PermissionProfile = ExecutionPermissionProfile.PreMortemReadOnly,
                    WorkflowRevision = workflow.Revision,
                    Status = StepStatus.Pending,
                    Attempt = 1,
                    InputSummary = BuildPreMortemAssignment(step),
                    DependsOnStepId = step.Id,
                    PreMortemOriginStepId = step.Id,
                    PreMortemTargetStepId = step.Id
                };
                review.StableSemanticRootId = review.Id;
                database.FlowSteps.Add(review);
                existingSteps.Add(review);
                var sourceProfile = CreateStudioTaskProfile(
                    planned,
                    snapshot,
                    flow.Id,
                    flow.Iteration,
                    step.Id,
                    preMortemAfter: true);
                database.TaskProfiles.Add(TaskProfileRules.CreatePreMortem(
                    sourceProfile,
                    review.Id,
                    reviewKey));
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = review.Id,
                    Type = "premortem.review-scheduled",
                    Message =
                        $"{preMortemSnapshot.Name} will independently evaluate plan step " +
                        $"'{planned.Id}' (round 1)."
                });
            }
            else
            {
                ValidateMaterializedPreMortemLineage(
                    review,
                    reviewAttempts,
                    preMortemSnapshot,
                    step,
                    existingSteps,
                    reviewKey);
            }
            var effectiveReview = reviewAttempts
                .Append(review)
                .DistinctBy(item => item.Id)
                .Where(item =>
                    item.Id == review.Id ||
                    item.RetryOfStepId == review.Id)
                .OrderByDescending(item => item.Sequence)
                .ThenByDescending(item => item.StartedAt)
                .First();
            dependencyStepId = effectiveReview.Id;
            sequence = Math.Max(
                sequence + 10,
                reviewAttempts
                    .Append(review)
                    .Max(item => item.Sequence) + 10);
        }

        foreach (var planned in plan.OrderedSteps.Where(
                     step => step.Stage == PlanStage.AfterApproval))
        {
            var outcomeOwnerStepId = existingSteps
                .Where(step =>
                    step.IsOutcomeOwner &&
                    step.RetryOfStepId is null &&
                    step.PreMortemReviewStepId is null &&
                    string.Equals(
                        step.PlanStepKey,
                        plan.OutcomeOwner?.Id,
                        StringComparison.Ordinal))
                .Select(step => step.Id)
                .Single();
            if (!await database.FlowEvents.AnyAsync(
                    item =>
                        item.FlowRunId == flow.Id &&
                        item.FlowStepId == outcomeOwnerStepId &&
                        item.Type ==
                        "plan.publication-permission-snapshotted",
                    cancellationToken))
            {
                var plannedPermission = _permissionResolver.Resolve(
                    new PermissionResolutionRequest(
                        flow.Kind,
                        ExecutionInvocationKind.Publication,
                        planned.Stage,
                        (planned.Duties ?? []).ToImmutableArray(),
                        DurableReviewDecision: ReviewDecision.Accepted,
                        DurableApproval: true,
                        IsOnlyPlannedPublishStep: true),
                    PermissionProfileResolver.FromWorkflow(workflow));
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = outcomeOwnerStepId,
                    Type = "plan.publication-permission-snapshotted",
                    Message =
                        "Persisted the planned post-approval permission ceiling before customer review.",
                    DataJson = JsonSerializer.Serialize(
                        new DeferredPermissionSnapshot(
                            flow.Iteration,
                            planned.Id,
                            workflow.Revision,
                            plannedPermission))
                });
            }
            if (!await database.TaskProfiles.AnyAsync(
                    item =>
                        item.FlowRunId == flow.Id &&
                        item.Iteration == flow.Iteration &&
                        item.PlanStepKey == planned.Id &&
                        item.AgentId == planned.AgentId,
                    cancellationToken))
            {
                database.TaskProfiles.Add(CreateStudioTaskProfile(
                    planned,
                    snapshotById[planned.AgentId],
                    flow.Id,
                    flow.Iteration,
                    stepId: null,
                    preMortemAfter: false));
            }
        }

        if (!await database.FlowEvents.AnyAsync(
                item =>
                    item.FlowRunId == flow.Id &&
                    item.Type == "plan.accepted" &&
                    item.FlowStepId == contractSource!.Id,
                cancellationToken))
        {
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = contractSource!.Id,
                Type = "plan.accepted",
                Message =
                    $"Accepted {plan.OrderedSteps.Count} dynamic plan step(s); " +
                    $"{plan.OrderedSteps.Count(step => step.Stage == PlanStage.BeforeReview)} " +
                    "pre-review worker step(s) were selected."
            });
        }
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static TaskProfile CreateStudioTaskProfile(
        TeamPlanStep planned,
        FlowAgentSnapshot snapshot,
        Guid flowId,
        int iteration,
        Guid? stepId,
        bool preMortemAfter)
    {
        var profile = planned.TaskProfile
            ?? throw new InvalidOperationException(
                $"Validated plan step '{planned.Id}' has no task profile.");
        var input = profile.IsEmpty
            ? new TaskProfileInput(
                snapshot.Role,
                3,
                4,
                5,
                5,
                [TaskTypeTag.Release.ToString()],
                TaskRisk.Medium.ToString(),
                "Publication must preserve the already-approved result.",
                0.8,
                ["The planned publication is isolated until durable customer approval."])
            : new TaskProfileInput(
                snapshot.Role,
                profile.Complexity!.Value,
                profile.ReasoningDepth!.Value,
                profile.ContextDemand!.Value,
                profile.ToolIntensity!.Value,
                profile.TaskTypeTags!.Select(tag => tag.ToString()).ToArray(),
                profile.Risk!.Value.ToString(),
                profile.RiskReason!,
                profile.Confidence!.Value,
                profile.Rationales!);
        var result = TaskProfileRules.Create(
            input,
            flowId,
            iteration,
            stepId,
            planned.Id,
            snapshot.AgentId);
        result.PreMortemAfter = preMortemAfter;
        return result;
    }

    private static void EnsureMaterializedStepMatches(
        FlowStep step,
        TeamPlanStep planned,
        FlowAgentSnapshot snapshot)
    {
        if (!string.Equals(step.AgentId, snapshot.AgentId, StringComparison.Ordinal) ||
            !string.Equals(step.AgentName, snapshot.Name, StringComparison.Ordinal) ||
            !string.Equals(step.AgentRole, snapshot.Role, StringComparison.Ordinal) ||
            !string.Equals(
                step.PlanDutiesJson,
                SerializePlanDuties(planned.Duties ?? []),
                StringComparison.Ordinal) ||
            step.PlanStage != planned.Stage ||
            step.IsOutcomeOwner != planned.OutcomeOwner)
        {
            throw new InvalidOperationException(
                $"Materialized plan step '{planned.Id}' does not match its immutable accepted plan.");
        }
    }

    private static void ValidateMaterializedPreMortemLineage(
        FlowStep root,
        IReadOnlyCollection<FlowStep> attempts,
        FlowAgentSnapshot preMortemSnapshot,
        FlowStep target,
        IReadOnlyCollection<FlowStep> materializedSteps,
        string reviewKey)
    {
        var expectedRoot = root.StableSemanticRootId ?? root.Id;
        var targetRootId =
            target.StableSemanticRootId ??
            target.RetryOfStepId ??
            target.Id;
        bool IsTargetLineage(Guid? targetStepId)
        {
            if (targetStepId is null)
            {
                return false;
            }
            var matches = materializedSteps
                .Where(item => item.Id == targetStepId.Value)
                .ToList();
            if (matches.Count != 1)
            {
                return false;
            }
            var candidate = matches[0];
            return (candidate.StableSemanticRootId ??
                    candidate.RetryOfStepId ??
                    candidate.Id) == targetRootId;
        }
        if (root.AgentId != preMortemSnapshot.AgentId ||
            root.AgentName != preMortemSnapshot.Name ||
            root.AgentRole != preMortemSnapshot.Role ||
            root.InvocationKind != ExecutionInvocationKind.PreMortem ||
            root.PreMortemReviewStepId is not null ||
            root.PreMortemOriginStepId != target.Id ||
            !IsTargetLineage(root.PreMortemTargetStepId) ||
            root.Attempt != 1 ||
            expectedRoot != root.Id)
        {
            throw new InvalidOperationException(
                $"The canonical pre-mortem checkpoint '{reviewKey}' does not match its immutable materialized identity.");
        }

        foreach (var attempt in attempts.Where(item => item.Id != root.Id))
        {
            if (attempt.RetryOfStepId != root.Id ||
                (attempt.StableSemanticRootId ?? attempt.RetryOfStepId) != root.Id ||
                attempt.AgentId != root.AgentId ||
                attempt.AgentName != root.AgentName ||
                attempt.AgentRole != root.AgentRole ||
                attempt.InvocationKind != ExecutionInvocationKind.PreMortem ||
                attempt.PreMortemReviewStepId is not null ||
                attempt.PreMortemOriginStepId != root.PreMortemOriginStepId ||
                !IsTargetLineage(attempt.PreMortemTargetStepId) ||
                attempt.Attempt != root.Attempt)
            {
                throw new InvalidOperationException(
                    $"The pre-mortem checkpoint '{reviewKey}' contains an attempt outside its canonical retry lineage.");
            }
        }
    }

    private static ExecutionPermissionProfile InitialPermissionProfile(
        FlowKind flowKind,
        PlanStage stage,
        IReadOnlyCollection<PlanDuty> duties) =>
        stage == PlanStage.AfterApproval
            ? ExecutionPermissionProfile.Publish
            : flowKind == FlowKind.Advisory ||
              !duties.Any(duty =>
                  duty is PlanDuty.Implement or PlanDuty.Verify or PlanDuty.PrepareOutcome)
                ? ExecutionPermissionProfile.ReadOnlySource
                : ExecutionPermissionProfile.WorkspaceWrite;

    private static ExecutionPermissionProfile
        InitialInvocationPermissionProfile(
            FlowRun flow,
            ExecutionInvocationKind invocationKind,
            PlanStage stage,
            IReadOnlyCollection<PlanDuty> duties) =>
        invocationKind switch
        {
            ExecutionInvocationKind.PreMortem =>
                ExecutionPermissionProfile.PreMortemReadOnly,
            ExecutionInvocationKind.Intake or
                ExecutionInvocationKind.Planning or
                ExecutionInvocationKind.BlockerExplanation =>
                ExecutionPermissionProfile.ReadOnlySource,
            _ => InitialPermissionProfile(flow.Kind, stage, duties)
        };

    private static string SerializePlanDuties(IEnumerable<PlanDuty> duties) =>
        JsonSerializer.Serialize(duties.Select(duty => duty.ToString()).ToArray());

    private static string PreMortemPlanStepKey(string targetPlanStepKey, int round) =>
        $"pre-mortem:{targetPlanStepKey}:{round}";

    private static AgentRecord SnapshotAgent(FlowAgentSnapshot snapshot) =>
        new()
        {
            Id = snapshot.AgentId,
            Name = snapshot.Name,
            Description = snapshot.Description,
            Role = snapshot.Role,
            SourcePath = snapshot.SourceFileName,
            Enabled = snapshot.EnabledAtSnapshot,
            Required = snapshot.Required,
            Switchable = snapshot.Switchable,
            DefinitionHash = snapshot.DefinitionHash,
            LoadedAt = snapshot.CapturedAt,
            UpdatedAt = snapshot.CapturedAt
        };

    internal static string BuildStudioTeamLeadAssignment(
        FlowRun flow,
        IReadOnlyCollection<FlowAgentSnapshot> snapshots,
        TeamPlanValidationContext validationContext,
        bool preMortemAvailable,
        int maximumPreMortemRounds)
    {
        if (flow.ConsolidatedRequest.Length >
            CopilotReasoningHost.MaximumPlanningBriefCharacters)
        {
            throw new InvalidOperationException(
                $"The confirmed brief exceeds the {CopilotReasoningHost.MaximumPlanningBriefCharacters}-character intake/promotion bound. Team Lead planning will not truncate it.");
        }
        var roster = snapshots
            .Where(snapshot =>
                snapshot.EnabledAtSnapshot &&
                snapshot.AgentId is not (
                    "account-manager" or "team-lead" or "pre-mortem-sceptic"))
            .OrderBy(snapshot => snapshot.AgentId, StringComparer.Ordinal)
            .Select(snapshot => new
            {
                Id = snapshot.AgentId,
                Name = snapshot.Name,
                Description = snapshot.Description
            })
            .ToArray();
        var rosterJson = JsonSerializer.Serialize(
            roster,
            new JsonSerializerOptions { WriteIndented = true });
        if (rosterJson.Length >
            CopilotReasoningHost.MaximumPlanningRosterCharacters)
        {
            throw new InvalidOperationException(
                $"The enabled snapshot roster exceeds the {CopilotReasoningHost.MaximumPlanningRosterCharacters}-character team-plan-derived bound. Team Lead planning will not truncate it.");
        }
        var requiredDuties = string.Join(
            ", ",
            validationContext.RequiredDuties.Select(duty => duty.ToString()));
        const string plannedShape =
            """{"Disposition":"Planned","Steps":[{"Id":"inspect-current-product","AgentId":"exact-roster-id","Order":1,"Stage":"BeforeReview","Assignment":"Complete, bounded assignment including the expected handoff.","Justification":"Why this exact agent and step are needed.","DependsOn":[],"Duties":["Analyze"],"OutcomeOwner":false,"TaskProfile":{"Complexity":5,"ReasoningDepth":5,"ContextDemand":5,"ToolIntensity":3,"TaskTypeTags":["Design"],"Risk":"Low","RiskReason":"Nonempty bounded reason.","Confidence":0.8,"Rationales":["Nonempty bounded rationale."]}}],"PreMortemCheckpoints":[],"MissingQualification":null}""";
        var assignment = $$"""
            Create the dynamic downstream plan for this flow.

            Flow kind: {{flow.Kind}}
            Confirmed brief:

            {{AssignmentBriefFormatter.Format(flow.ConsolidatedRequest)}}

            Exact enabled optional snapshot roster (Id, Name, Description):
            {{rosterJson}}

            Select the smallest suitable team. Use only exact roster Id values. An AgentId may be
            selected in multiple distinct plan steps, but every step Id must be unique. If the
            enabled roster cannot safely satisfy the brief, return MissingQualification instead
            of inventing an agent. Workers receive the confirmed brief plus only their declared
            current-iteration dependencies and ancestors; never assign a worker to reconstruct
            an earlier iteration or inspect a full execution ledger.

            Return exactly one strict JSON object between TEAM_PLAN_BEGIN and
            TEAM_PLAN_END. Disposition is Planned or
            MissingQualification. Unknown properties, enum aliases, and extra sentinels are
            rejected. A Planned result contains Steps, PreMortemCheckpoints, and null
            MissingQualification. A MissingQualification result contains empty Steps and
            PreMortemCheckpoints plus Summary, Missing, WhyRequired, and SuggestedAgent.

            A Planned result uses exactly this shape (replace the sample values):
            {{plannedShape}}
            Every BeforeReview TaskProfile contains exactly Complexity, ReasoningDepth,
            ContextDemand, ToolIntensity, TaskTypeTags, Risk, RiskReason, Confidence, and
            Rationales. Allowed TaskTypeTags are exactly CustomerDialogue, Planning,
            Architecture, Design, Data, Implementation, Security, Quality, Documentation,
            Release, Feedback, and CrossCutting. Do not emit a Handoff property or any other
            step/profile property; put handoff expectations in Assignment. PreMortemCheckpoints
            is an array of step ID strings, never checkpoint objects.

            Plan limits: at most {{validationContext.MaximumSteps}} steps, at most
            {{validationContext.MaximumDependenciesPerStep}} dependencies per step, and at most
            {{validationContext.MaximumAssignmentCharacters}} characters in each Assignment.
            Keep the complete response under 10,000 characters so the standalone HANDOFF_STATUS
            and TEAM_PLAN delimiters plus the entire JSON document are never transport-truncated.
            Assignment and Justification must be nonempty and bounded. Order values are positive
            and dependencies name only lower-order steps. Duties use exact values Analyze, Design,
            Implement, Verify, PrepareOutcome, and Publish. Stage is BeforeReview or AfterApproval.
            TaskProfile uses 1-10 integer metrics, 1-6 exact TaskTypeTags, exact Low/Medium/High/
            Critical Risk, bounded RiskReason and Rationales, and Confidence from 0 through 1.

            Exactly one final BeforeReview worker is OutcomeOwner and has PrepareOutcome.
            Required configured duties: {{requiredDuties}}.
            {{(flow.Kind == FlowKind.Advisory
                ? "Advisory requires at least one worker and PrepareOutcome, and forbids Implement, Publish, and AfterApproval."
                : "Delivery requires exactly one Verify-duty step: the final BeforeReview OutcomeOwner, which also has PrepareOutcome. All implementation, packaging, and preview creation it verifies must be completed by earlier dependencies. Delivery also requires exactly one AfterApproval Publish-only step. Never Publish before review.")}}
            The AfterApproval publication step remains planned only and will not run before durable
            customer acceptance. Its TaskProfile may be an empty object.
            Never tell a pre-review worker to stage, commit, branch, push, or publish changes.
            The host seals the working-tree bytes through its own temporary Git index after the
            outcome owner completes. For a browser-visible Delivery, assign creation of static
            review artifacts under .customer-preview\<variant>\index.html before customer review.
            Studio serves those artifacts in a sandbox with connect-src 'none'; each preview must
            boot and render representative product content without any network request. Bundle or
            inline the required preview configuration, data, images, fonts, and other assets.
            `.customer-preview` is the only generated top-level directory allowed to remain outside
            the registered repositories. Every assignment that creates `_release`, `.previous`,
            packaging-helper, browser-cache, report, test-result, or other temporary output must
            explicitly remove it before handoff. Preserve every pre-existing non-repository project
            scaffold file byte-for-byte; cleanup must never delete a trusted root file merely
            because it resembles generated package-manager output.

            {{(preMortemAvailable
                ? $"The Pre-mortem Sceptic snapshot is enabled. PreMortemCheckpoints may name justified BeforeReview step IDs; each has at most {maximumPreMortemRounds} round(s)."
                : "The Pre-mortem Sceptic is unavailable. PreMortemCheckpoints must be empty.")}}
            """;
        if (assignment.Length >
            CopilotReasoningHost.MaximumPlanningTaskCharacters)
        {
            throw new InvalidOperationException(
                $"The Team Lead assignment exceeds the {CopilotReasoningHost.MaximumPlanningTaskCharacters}-character contract-derived bound. Confirmed brief and roster data will not be truncated.");
        }
        return assignment;
    }

    private async Task AddEventOnceAsync(
        Guid flowId,
        Guid? stepId,
        string type,
        string message,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        if (await database.FlowEvents.AnyAsync(
                item =>
                    item.FlowRunId == flowId &&
                    item.FlowStepId == stepId &&
                    item.Type == type,
                cancellationToken))
        {
            return;
        }
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flowId,
            FlowStepId = stepId,
            Type = type,
            Message = message
        });
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordAdvisoryWorkspacePolicyAsync(
        Guid flowId,
        AdvisoryWorkspaceEvidence baseline,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        if (!await database.FlowEvents.AnyAsync(
                item =>
                    item.FlowRunId == flowId &&
                    item.Type == "workspace.advisory-policy",
                cancellationToken))
        {
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flowId,
                Type = "workspace.advisory-policy",
                Message =
                    "Advisory execution uses a guarded source snapshot with hooks, source writes, shell access, and publication disabled.",
                DataJson = JsonSerializer.Serialize(new
                {
                    WorkspaceMode = WorkspaceMode.AdvisoryReadOnly.ToString(),
                    PermissionProfile =
                        ExecutionPermissionProfile.ReadOnlySource.ToString(),
                    HooksEnabled = false,
                    PublicationAllowed = false,
                    baseline.BaselineDigest,
                    baseline.FileCount,
                    baseline.TotalBytes,
                    baseline.CapturedAt
                })
            });
            await database.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task ExecutePendingCausalRetriesAsync(
        FlowRun flow,
        string workspacePath,
        string planSummary,
        int complexity,
        IReadOnlyDictionary<string, AgentRecord> upstreamOwners,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Guid? retryStepId;
            await using (var database =
                         await databaseFactory.CreateDbContextAsync(cancellationToken))
            {
                retryStepId = await database.FlowSteps
                    .AsNoTracking()
                    .Where(step =>
                        step.FlowRunId == flow.Id &&
                        step.Iteration == flow.Iteration &&
                        step.Status == StepStatus.Pending &&
                        step.RetryOfStepId != null)
                    .OrderBy(step => step.Sequence)
                    .ThenBy(step => step.Attempt)
                    .Select(step => (Guid?)step.Id)
                    .FirstOrDefaultAsync(cancellationToken);
            }
            if (retryStepId is null)
            {
                return;
            }
            if (!await IsDependencyCompleteAsync(
                    retryStepId.Value,
                    cancellationToken))
            {
                return;
            }

            await HydrateRetryAssignmentAsync(
                retryStepId.Value,
                cancellationToken);
            await ExecutePendingStepAsync(
                flow,
                retryStepId.Value,
                workspacePath,
                planSummary,
                complexity,
                upstreamOwners,
                cancellationToken);
        }
    }

    private async Task ThrowIfUnresolvedFailureAsync(
        Guid flowId,
        int iteration,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var steps = await database.FlowSteps
            .AsNoTracking()
            .Where(step =>
                step.FlowRunId == flowId &&
                step.Iteration == iteration)
            .ToListAsync(cancellationToken);
        var unresolved = FindUnresolvedFailure(steps);
        if (unresolved is not null)
        {
            throw new InvalidOperationException(
                $"{unresolved.AgentName} still has an unresolved failed step. " +
                "Restart that step before downstream execution continues.");
        }
    }

    private async Task<int> GetLatestCompletedAgentSequenceAsync(
        Guid flowId,
        int iteration,
        string agentId,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        return await database.FlowSteps
            .Where(step =>
                step.FlowRunId == flowId &&
                step.Iteration == iteration &&
                step.AgentId == agentId &&
                step.Status == StepStatus.Completed)
            .Select(step => (int?)step.Sequence)
            .MaxAsync(cancellationToken) ?? 10;
    }

    private async Task<Guid> AddStepAsync(
        FlowRun flow,
        AgentRecord agent,
        int sequence,
        string label,
        CancellationToken cancellationToken,
        int attempt = 1,
        string? inputSummary = null,
        Guid? retryOfStepId = null,
        Guid? stableSemanticRootId = null,
        Guid? dependsOnStepId = null,
        string planStepKey = "",
        IReadOnlyCollection<PlanDuty>? planDuties = null,
        PlanStage planStage = PlanStage.BeforeReview,
        ExecutionInvocationKind invocationKind = ExecutionInvocationKind.Worker,
        bool isOutcomeOwner = false,
        string? workflowRevision = null)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var existingQuery = database.FlowSteps
            .AsNoTracking()
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.Iteration == flow.Iteration &&
                item.AgentId == agent.Id &&
                item.Attempt == attempt &&
                (planStepKey == string.Empty ||
                 item.PlanStepKey == planStepKey));
        var existing = retryOfStepId is null
            ? await existingQuery.SingleOrDefaultAsync(
                item => item.Label == label,
                cancellationToken)
            : await existingQuery.SingleOrDefaultAsync(
                item => item.RetryOfStepId == retryOfStepId,
                cancellationToken);
        if (existing is not null)
        {
            return existing.Id;
        }

        var step = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Sequence = sequence,
            AgentId = agent.Id,
            AgentName = agent.Name,
            AgentRole = agent.Role,
            Label = label,
            PlanStepKey = planStepKey,
            PlanDutiesJson = SerializePlanDuties(planDuties ?? []),
            PlanStage = planStage,
            InvocationKind = invocationKind,
            IsOutcomeOwner = isOutcomeOwner,
            PermissionProfile = InitialInvocationPermissionProfile(
                flow,
                invocationKind,
                planStage,
                planDuties ?? []),
            WorkflowRevision = workflowRevision ?? string.Empty,
            Status = StepStatus.Pending,
            Attempt = attempt,
            InputSummary = inputSummary ?? label,
            RetryOfStepId = retryOfStepId,
            DependsOnStepId = dependsOnStepId,
            StableSemanticRootId = stableSemanticRootId
        };
        step.StableSemanticRootId ??= retryOfStepId ?? step.Id;
        database.FlowSteps.Add(step);
        await database.SaveChangesAsync(cancellationToken);
        return step.Id;
    }

    private async Task<Guid> AddPreMortemReviewStepAsync(
        FlowRun flow,
        AgentRecord sceptic,
        Guid targetStepId,
        Guid originStepId,
        int sequence,
        int round,
        CancellationToken cancellationToken,
        bool shiftLaterSteps = false)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var existing = await database.FlowSteps
            .AsNoTracking()
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.Iteration == flow.Iteration &&
                (item.InvocationKind == ExecutionInvocationKind.PreMortem ||
                 item.AgentId == PreMortemRole) &&
                item.PreMortemOriginStepId == originStepId &&
                item.Attempt == round)
            .OrderByDescending(item => item.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing.Id;
        }

        var target = await database.FlowSteps
            .AsNoTracking()
            .SingleAsync(item => item.Id == targetStepId, cancellationToken);
        var sourceProfile = await database.TaskProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.FlowStepId == targetStepId,
                cancellationToken)
            ?? await database.TaskProfiles
                .AsNoTracking()
                .Where(item =>
                    item.FlowRunId == flow.Id &&
                    item.Iteration == flow.Iteration &&
                    item.FlowStepId == null &&
                    item.Role == target.AgentRole &&
                    item.PlanStepKey == target.PlanStepKey)
                .OrderByDescending(item => item.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        if (sourceProfile is null)
        {
            throw new InvalidOperationException(
                $"No validated task profile exists for pre-mortem target '{target.AgentRole}'.");
        }

        if (shiftLaterSteps)
        {
            var laterSteps = await database.FlowSteps
                .Where(item =>
                    item.FlowRunId == flow.Id &&
                    item.Iteration == flow.Iteration &&
                    item.Sequence >= sequence)
                .ToListAsync(cancellationToken);
            foreach (var laterStep in laterSteps)
            {
                laterStep.Sequence += 10;
            }
        }

        var step = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Sequence = sequence,
            AgentId = sceptic.Id,
            AgentName = sceptic.Name,
            AgentRole = sceptic.Role,
            Label = $"Pre-mortem review of {target.AgentName} (round {round})",
            PlanStepKey = PreMortemPlanStepKey(target.PlanStepKey, round),
            PlanDutiesJson = SerializePlanDuties(
                [PlanDuty.Analyze, PlanDuty.Verify]),
            PlanStage = PlanStage.BeforeReview,
            InvocationKind = ExecutionInvocationKind.PreMortem,
            IsOutcomeOwner = false,
            PermissionProfile = ExecutionPermissionProfile.PreMortemReadOnly,
            WorkflowRevision = workflowProvider.GetEffective().Revision,
            Status = StepStatus.Pending,
            Attempt = round,
            InputSummary = BuildPreMortemAssignment(target),
            DependsOnStepId = targetStepId,
            PreMortemOriginStepId = originStepId,
            PreMortemTargetStepId = targetStepId
        };
        step.StableSemanticRootId = step.Id;
        database.FlowSteps.Add(step);
        database.TaskProfiles.Add(TaskProfileRules.CreatePreMortem(
            sourceProfile,
            step.Id,
            step.PlanStepKey));
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = step.Id,
            Type = "premortem.review-scheduled",
            Message =
                $"{sceptic.Name} will independently evaluate {target.AgentName}'s result " +
                $"(round {round})."
        });
        await database.SaveChangesAsync(cancellationToken);
        return step.Id;
    }

    private async Task EnsureBootstrapProfileAsync(
        FlowRun flow,
        string role,
        Guid stepId,
        CancellationToken cancellationToken,
        string planStepKey = "",
        string agentId = "")
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        if (await database.TaskProfiles.AnyAsync(
                item =>
                    item.FlowStepId == stepId,
                cancellationToken))
        {
            return;
        }
        database.TaskProfiles.Add(profileFactory.Create(
            role,
            flow.ConsolidatedRequest,
            flow.Id,
            flow.Iteration,
            stepId,
            planStepKey,
            agentId));
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> ShouldExecuteStepAsync(
        Guid stepId,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        return await database.FlowSteps.AnyAsync(
            item =>
                item.Id == stepId &&
                item.Status == StepStatus.Pending,
            cancellationToken);
    }

    private async Task<Guid> ResolveEffectiveManualRetryStepIdAsync(
        Guid stepId,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var source = await database.FlowSteps
            .AsNoTracking()
            .SingleAsync(item => item.Id == stepId, cancellationToken);
        var retryRootId = GetStableSemanticRootId(source);
        var candidates = await database.FlowSteps
            .AsNoTracking()
            .Where(item =>
                item.FlowRunId == source.FlowRunId &&
                item.Iteration == source.Iteration &&
                item.AgentId == source.AgentId &&
                (item.Id == source.Id ||
                 item.StableSemanticRootId == retryRootId))
            .ToListAsync(cancellationToken);
        return SelectEffectiveManualRetryStep(source, candidates).Id;
    }

    private async Task<Guid?> GetNextPendingStepIdAsync(
        Guid flowId,
        int iteration,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var next = await database.FlowSteps
            .AsNoTracking()
            .Where(item =>
                item.FlowRunId == flowId &&
                item.Iteration == iteration &&
                item.Status == StepStatus.Pending)
            .OrderBy(item => item.Sequence)
            .ThenBy(item => item.Attempt)
            .Select(item => new
            {
                item.Id,
                item.DependsOnStepId
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (next is null)
        {
            return null;
        }
        if (next.DependsOnStepId is null ||
            await database.FlowSteps.AnyAsync(
                dependency =>
                    dependency.Id == next.DependsOnStepId &&
                    dependency.Status == StepStatus.Completed,
                cancellationToken))
        {
            return next.Id;
        }

        throw new InvalidOperationException(
            $"Flow step '{next.Id}' cannot run before dependency " +
            $"'{next.DependsOnStepId}' completes.");
    }

    private async Task<bool> IsDependencyCompleteAsync(
        Guid stepId,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var dependencyStepId = await database.FlowSteps
            .AsNoTracking()
            .Where(step => step.Id == stepId)
            .Select(step => step.DependsOnStepId)
            .SingleAsync(cancellationToken);
        return dependencyStepId is null ||
               await database.FlowSteps.AnyAsync(
                   dependency =>
                       dependency.Id == dependencyStepId &&
                       dependency.Status == StepStatus.Completed,
                   cancellationToken);
    }

    private async Task BindStudioPermissionAtFirstLaunchAsync(
        HarnessDbContext database,
        FlowRun flow,
        FlowStep step,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(step.EffectivePermissionJson))
        {
            return;
        }
        if (step.Status != StepStatus.Pending ||
            step.StartedAt is not null)
        {
            throw new InvalidOperationException(
                "A started attempt has no persisted effective permission policy; execution failed closed.");
        }

        // A materialized pending step is not yet bound to an execution attempt. Its planning
        // revision is provenance only: resolve the effective last-known-good workflow at the
        // launch boundary, then persist policy/revision in the same save as Running/StartedAt.
        var workflow = workflowProvider.GetEffective();
        var duties = ReadPlanDuties(step.PlanDutiesJson).ToImmutableArray();
        var publicationStep =
            ReviewCoordinator.IsPublicationStep(flow, step);
        var approved = publicationStep &&
                       await HasDurablePublicationApprovalAsync(
                           database,
                           flow,
                           step,
                           cancellationToken);
        var request = new PermissionResolutionRequest(
            flow.Kind,
            step.InvocationKind,
            step.PlanStage,
            duties,
            approved ? ReviewDecision.Accepted : null,
            approved,
            publicationStep);
        var permission = _permissionResolver.Resolve(
            request,
            PermissionProfileResolver.FromWorkflow(workflow));
        PermissionProfileResolver.ValidatePersisted(
            permission,
            permission.Profile,
            request);
        step.PermissionProfile = permission.Profile;
        step.EffectivePermissionJson =
            JsonSerializer.Serialize(permission);
        step.WorkflowRevision = workflow.Revision;
    }

    internal async Task<FlowStep> ExecuteStepAsync(
        Guid flowId,
        Guid stepId,
        string workspacePath,
        string planSummary,
        int complexity,
        CancellationToken cancellationToken)
    {
        AgentExecutionContext executionContext;
        var stopwatch = Stopwatch.StartNew();
        var remotePublicationAuthorized = false;
        ReviewedCandidateIdentity? reviewedIdentity = null;
        var isStudioContractCorrection = false;

        try
        {
            await using (var database =
                         await databaseFactory.CreateDbContextAsync(cancellationToken))
            {
                var flow = await database.Flows
                    .Include(item => item.Events)
                    .SingleAsync(
                        item => item.Id == flowId,
                        cancellationToken);
                var step = await database.FlowSteps.SingleAsync(
                    item => item.Id == stepId,
                    cancellationToken);
                isStudioContractCorrection = step.Label.StartsWith(
                    StudioContractCorrectionLabelPrefix,
                    StringComparison.Ordinal);
                var publicationShape =
                    ReviewCoordinator.IsPublicationStep(flow, step);
                var requestsPublication =
                    publicationShape ||
                    step.RemotePublicationAllowed ||
                    step.PlanStage == PlanStage.AfterApproval ||
                    string.Equals(
                        step.PlanStepKey,
                        flow.PublicationPlanStepKey,
                        StringComparison.Ordinal) ||
                    ReadPlanDuties(step.PlanDutiesJson)
                        .Contains(PlanDuty.Publish);
                if (requestsPublication && !publicationShape)
                {
                    throw new InvalidOperationException(
                        "Publication authority must match the sole planned AfterApproval Publish step.");
                }
                if (publicationShape)
                {
                    _ = OutcomeTypeRules.RequireDelivery(
                        flow.Outcome,
                        nameof(flow.Outcome));
                    remotePublicationAuthorized =
                        await HasDurablePublicationApprovalAsync(
                            database,
                            flow,
                            step,
                            cancellationToken);
                    if (!remotePublicationAuthorized)
                    {
                        throw new InvalidOperationException(
                            "Publication cannot execute before durable customer acceptance.");
                    }
                    _ = await RefreshAndRequirePublicationAuthorityAsync(
                        database,
                        flow,
                        step,
                        cancellationToken);
                    reviewedIdentity = ReviewedCandidateLedger.Read(flow);
                    _ = await (_reviewedCandidates
                            ?? throw new InvalidOperationException(
                                "No reviewed candidate verification service is configured."))
                        .VerifyAsync(
                            flow,
                            reviewedIdentity,
                            cancellationToken);
                }
                string? excludedModelFamily = null;
                string? evaluatedModel = null;
                if (IsPreMortemStep(step))
                {
                    var targetStepId = step.PreMortemTargetStepId
                        ?? throw new InvalidOperationException(
                            "A pre-mortem review has no persisted evaluation target.");
                    var targetStep = await database.FlowSteps
                        .AsNoTracking()
                        .SingleAsync(item => item.Id == targetStepId, cancellationToken);
                    if (targetStep.Status != StepStatus.Completed ||
                        string.IsNullOrWhiteSpace(targetStep.Model))
                    {
                        throw new InvalidOperationException(
                            "A pre-mortem review cannot start before its evaluated step completes with a routed model.");
                    }
                    evaluatedModel = targetStep.Model;
                    excludedModelFamily = ModelFamilyClassifier.Classify(targetStep.Model);
                }
                var decision = await modelRouter.SelectAsync(
                    new RoutingRequest(
                        step.Id,
                        flow.ModelSelectionStrategy,
                        ExcludedModelFamilies: excludedModelFamily is null
                            ? null
                            : [excludedModelFamily]),
                    cancellationToken);
                if (excludedModelFamily is not null &&
                    ModelFamilyClassifier.Classify(decision.SelectedModel) ==
                    excludedModelFamily)
                {
                    throw new InvalidOperationException(
                        $"The pre-mortem route '{decision.SelectedModel}' belongs to the evaluated " +
                        $"model family '{excludedModelFamily}'. A different family is required.");
                }
                await BindStudioPermissionAtFirstLaunchAsync(
                    database,
                    flow,
                    step,
                    cancellationToken);
                if (isStudioContractCorrection)
                {
                    RestrictResponseCorrectionPermission(step);
                }
                var persistedSessionId = step.CopilotSessionId;
                var interruptedDurableAttempt =
                    step.Phase ==
                        AgentRunPhase.CanceledByReconciliation &&
                    step.StartedAt is not null;
                if (interruptedDurableAttempt &&
                    persistedSessionId is null)
                {
                    throw new InvalidOperationException(
                        "The interrupted durable attempt has no recoverable Copilot session; an explicit retry attempt is required.");
                }
                var priorSession = persistedSessionId is null &&
                                   !IsPreMortemStep(step) &&
                                   !(isStudioContractCorrection &&
                                     IsDeliveryVerificationStep(step))
                    ? await database.FlowSteps
                        .AsNoTracking()
                        .Where(item =>
                            item.FlowRunId == flow.Id &&
                            item.Iteration == flow.Iteration &&
                            item.AgentId == step.AgentId &&
                            item.Id != step.Id &&
                            item.CopilotSessionId != null &&
                            item.PlanStepKey == step.PlanStepKey &&
                            (item.Status == StepStatus.Completed ||
                             item.Status == StepStatus.Pushback))
                        .OrderByDescending(item => item.StartedAt)
                        .Select(item => new
                        {
                            item.CopilotSessionId,
                            item.CopilotSessionHome
                        })
                        .FirstOrDefaultAsync(cancellationToken)
                    : null;
                var copilotSessionId =
                    persistedSessionId ??
                    priorSession?.CopilotSessionId ??
                    AgentSessionIdentity.Create(
                        flow.Id,
                        flow.Iteration,
                        isStudioContractCorrection
                            ? $"{step.AgentId}:response-correction:{step.Id:N}"
                        : IsPreMortemStep(step)
                            ? $"{step.AgentId}:{step.PreMortemOriginStepId:D}:{step.Attempt}"
                            : step.RetryOfStepId is not null &&
                              step.Attempt > 1
                                ? $"{step.AgentId}:attempt:{step.Attempt}"
                            : step.AgentId,
                        step.PlanStepKey);
                var resumesPersistedSession =
                    step.Phase == AgentRunPhase.CanceledByReconciliation &&
                    persistedSessionId is not null;
                var recoversInterruptedSession =
                    resumesPersistedSession &&
                    interruptedDurableAttempt;
                var resumesSession =
                    resumesPersistedSession ||
                    priorSession is not null;
                var copilotSessionHome = !string.IsNullOrWhiteSpace(step.CopilotSessionHome)
                    ? step.CopilotSessionHome
                    : !string.IsNullOrWhiteSpace(priorSession?.CopilotSessionHome)
                        ? priorSession.CopilotSessionHome
                        : sessionJournal.ExpectedHome();
                step.Model = decision.SelectedModel;
                step.ModelEffort = decision.SelectedEffort;
                step.ModelReason = decision.Reason;
                if (string.IsNullOrWhiteSpace(step.WorkflowRevision))
                {
                    step.WorkflowRevision = workflowProvider.GetEffective().Revision;
                }
                step.Status = StepStatus.Running;
                step.Phase = AgentRunPhase.BuildingPrompt;
                step.StartedAt ??= DateTimeOffset.UtcNow;
                step.CopilotSessionId = copilotSessionId;
                step.CopilotSessionHome = copilotSessionHome;
                flow.UpdatedAt = DateTimeOffset.UtcNow;
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flowId,
                    FlowStepId = stepId,
                    Type = "step.started",
                    Message =
                        $"{step.AgentName} started with {decision.SelectedModel}/{decision.SelectedEffort} " +
                        $"under {flow.ModelSelectionStrategy}."
                });
                if (excludedModelFamily is not null)
                {
                    database.FlowEvents.Add(new FlowEvent
                    {
                        FlowRunId = flowId,
                        FlowStepId = stepId,
                        Type = "premortem.model-family-separated",
                        Message =
                            $"{step.AgentName} uses {ModelFamilyClassifier.Classify(decision.SelectedModel)} " +
                            $"instead of the evaluated {excludedModelFamily} family ({evaluatedModel})."
                    });
                }
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flowId,
                    FlowStepId = stepId,
                    Type = resumesSession
                        ? "agent.session-resumed"
                        : "agent.session-created",
                    Message =
                        $"{step.AgentName} {(resumesSession ? "requested continuation of" : "requested")} " +
                        $"Copilot session {copilotSessionId:D}."
                });

                IReadOnlyList<StudioDependencyOutput>? studioDependencyOutputs =
                    null;
                List<string> previousOutputs;
                if (step.InvocationKind == ExecutionInvocationKind.Worker)
                {
                    studioDependencyOutputs =
                        await ResolveStudioDependencyOutputsAsync(
                            database,
                            flow,
                            step,
                            cancellationToken);
                    previousOutputs = [];
                }
                else
                {
                    var previousSteps = await database.FlowSteps
                        .AsNoTracking()
                        .Where(item =>
                            item.FlowRunId == flowId &&
                            item.Iteration == step.Iteration &&
                            item.Sequence < step.Sequence &&
                            item.Status == StepStatus.Completed &&
                            item.OutputSummary != string.Empty)
                        .OrderByDescending(item => item.Sequence)
                        .ThenByDescending(item => item.Attempt)
                        .Select(item => new
                        {
                            item.AgentName,
                            item.AgentRole,
                            item.OutputSummary
                        })
                        .Take(2)
                        .ToListAsync(cancellationToken);
                    previousSteps.Reverse();
                    previousOutputs = previousSteps
                        .Select(item =>
                            $"{item.AgentName} ({item.AgentRole}){Environment.NewLine}" +
                            item.OutputSummary)
                        .ToList();
                }
                var learnings = await database.Learnings
                    .Where(item =>
                        item.AgentId == string.Empty ||
                        item.AgentId == step.AgentId ||
                        item.AgentId == step.AgentRole)
                    .OrderByDescending(item => item.CreatedAt)
                    .Take(12)
                    .ToListAsync(cancellationToken);
                learnings.Reverse();
                foreach (var learning in learnings)
                {
                    learning.TimesApplied++;
                }

                var readinessAssignment =
                    await PrepareDeliveryVerificationAssignmentAsync(
                        database,
                        flow,
                        step,
                        cancellationToken);
                var (outcomeContext, outcomeContract) =
                    BuildOutcomePrompt(step, readinessAssignment);
                await database.SaveChangesAsync(cancellationToken);
                executionContext = new AgentExecutionContext(
                    flow.Id,
                    flow.Iteration,
                    step.AgentId,
                    step.AgentName,
                    step.AgentRole,
                    step.Model,
                    step.ModelEffort,
                    step.Attempt,
                    IsPreMortemStep(step)
                        ? BuildPreMortemContextTask(
                            flow.ConsolidatedRequest,
                            step.InputSummary)
                        : step.PreMortemReviewStepId is not null
                        ? BuildPreMortemContextTask(
                            flow.ConsolidatedRequest,
                            step.InputSummary)
                        : BuildStepTask(flow.ConsolidatedRequest, step.InputSummary),
                    flow.RepositoryKnowledge,
                    flow.RepositoryPath,
                    workspacePath,
                    copilotSessionId,
                    flow.Outcome,
                    planSummary,
                    previousOutputs,
                    learnings,
                    ModelSelectionStrategy: flow.ModelSelectionStrategy,
                    ExpectedAcceptedTimeSeconds: decision.PredictedAcceptedTimeSeconds,
                    AllowRemotePublication: false,
                    ResumeSession: resumesSession,
                    RecoverInterruptedSession: recoversInterruptedSession,
                    Progress: progress =>
                        RecordProgressAsync(
                                flow.Id,
                                step.Id,
                                progress,
                                CancellationToken.None)
                            .GetAwaiter()
                            .GetResult(),
                    IsPreMortemRevision: step.PreMortemReviewStepId is not null,
                    InvocationStartedAt: step.StartedAt,
                    OutcomeContext: outcomeContext,
                    OutcomeContract: outcomeContract,
                    DirectPrompt: string.Empty,
                    IsHostControlledPublication:
                        reviewedIdentity is not null,
                    GovernedRepositoryRelativePaths:
                        reviewedIdentity is not null
                            ? reviewedIdentity.Repositories
                                .Select(repository => repository.RelativePath)
                                .ToArray()
                            : null,
                    FlowStepId: step.Id,
                    InvocationKind: step.InvocationKind,
                    StudioDependencyOutputs: studioDependencyOutputs,
                    IsOutcomeOwner: step.IsOutcomeOwner,
                    PlanStepKey: step.PlanStepKey,
                    FlowKind: flow.Kind,
                    RequiresDeliveryReadinessQa:
                        DeliveryReadinessService.AppliesTo(flow) &&
                        IsDeliveryVerificationStep(step),
                    ContextDocuments: readinessAssignment is null
                        ? null
                        :
                        [
                            new AgentContextDocument(
                                "evidence.jsonl",
                                DeliveryReadinessService.SerializeEvidenceDocument(
                                    readinessAssignment.Evidence))
                        ]);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            await MarkStepFailedAsync(
                stepId,
                exception,
                stopwatch.ElapsedMilliseconds,
                cancellationToken);
            throw;
        }

        AgentExecutionResult? attemptedResult = null;
        var correctionScheduled = false;
        try
        {
            logger.LogInformation(
                "Starting agent step {StepId} for flow {FlowId}: {AgentId} attempt {Attempt}, " +
                "{InvocationKind}, session {CopilotSessionId}, model {Model}/{Effort}.",
                stepId,
                flowId,
                executionContext.AgentId,
                executionContext.Attempt,
                executionContext.InvocationKind,
                executionContext.CopilotSessionId,
                executionContext.Model,
                executionContext.ModelEffort);
            attemptedResult = await agentRunner.ExecuteAsync(
                executionContext,
                cancellationToken);
            var contractError = GetStudioContractCorrectionReason(
                executionContext,
                attemptedResult.Output);
            if (contractError is null && executionContext.RequiresDeliveryReadinessQa)
            {
                await using var database =
                    await databaseFactory.CreateDbContextAsync(cancellationToken);
                var flow = await database.Flows
                    .Include(item => item.Events)
                    .SingleAsync(item => item.Id == flowId, cancellationToken);
                var step = await database.FlowSteps
                    .SingleAsync(item => item.Id == stepId, cancellationToken);
                contractError = GetDeliveryQaCorrectionReason(
                    flow,
                    step,
                    attemptedResult);
            }
            if (contractError is not null)
            {
                if (isStudioContractCorrection)
                {
                    throw new InvalidOperationException(
                        "The bounded Studio response-contract correction remained invalid: " +
                        contractError);
                }
                var correctionStepId =
                    await ScheduleStudioContractCorrectionAsync(
                        flowId,
                        stepId,
                        attemptedResult,
                        contractError,
                        DateTimeOffset.UtcNow,
                        stopwatch.ElapsedMilliseconds,
                        cancellationToken);
                stopwatch.Stop();
                correctionScheduled = true;
                return await ExecuteStepAsync(
                    flowId,
                    correctionStepId,
                    workspacePath,
                    planSummary,
                    complexity,
                    cancellationToken);
            }
            stopwatch.Stop();
            var completedStep = await CompleteStepAsync(
                flowId,
                stepId,
                attemptedResult,
                DateTimeOffset.UtcNow,
                stopwatch.ElapsedMilliseconds,
                recoveredSessionId: null,
                cancellationToken);
            logger.LogInformation(
                "Agent step {StepId} for flow {FlowId} finished with {StepStatus}/{StepPhase} " +
                "after {ElapsedMilliseconds} ms and {ToolCallCount} tool call(s).",
                stepId,
                flowId,
                completedStep.Status,
                completedStep.Phase,
                stopwatch.ElapsedMilliseconds,
                attemptedResult.ToolCalls.Count);
            return completedStep;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            if (!correctionScheduled)
            {
                await MarkStepInterruptedAsync(
                    flowId,
                    stepId,
                    stopwatch.ElapsedMilliseconds,
                    CancellationToken.None);
            }
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            if (!correctionScheduled)
            {
                await MarkStepFailedAsync(
                    stepId,
                    exception,
                    stopwatch.ElapsedMilliseconds,
                    cancellationToken,
                    attemptedResult?.Output,
                    attemptedResult);
            }
            throw;
        }
    }

    internal static string? GetStudioContractCorrectionReason(
        AgentExecutionContext context,
        string output)
    {
        if (context.InvocationKind == ExecutionInvocationKind.PreMortem)
        {
            try
            {
                _ = PreMortemRules.ParseReview(output);
                return null;
            }
            catch (InvalidOperationException exception)
            {
                return exception.Message;
            }
        }
        if (context.InvocationKind is not (
                ExecutionInvocationKind.Worker or
                ExecutionInvocationKind.Publication))
        {
            return null;
        }

        DynamicHandoffStatus handoff;
        try
        {
            handoff = AgentHandoffInspector.ParseDynamic(output);
            if (context.IsPreMortemRevision)
            {
                _ = ValidatePreMortemRevisionOutput(output);
            }
            if (context.IsOutcomeOwner && !handoff.IsPushback)
            {
                _ = FlowOutcomeParser.Parse(output);
            }
            if (context.InvocationKind == ExecutionInvocationKind.Publication &&
                !handoff.IsPushback)
            {
                _ = RepositoryKnowledgeSynthesizer.ParseRecapEnvelope(output);
            }
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message;
        }

        if (!handoff.IsPushback &&
            context.RequiresDeliveryReadinessQa &&
            !DeliveryReadinessPolicy.ContainsQaContract(output))
        {
            return
                "The Delivery verification turn must return exactly one strict verification " +
                $"block between {DeliveryReadinessPolicy.QaBeginMarker} and {DeliveryReadinessPolicy.QaEndMarker}.";
        }
        return null;
    }

    /// <summary>
    /// A Delivery verification step is the planned owner of the strict verification contract.
    /// It is identified from the persisted plan duties, never from an agent role name or prose.
    /// </summary>
    internal static bool IsDeliveryVerificationStep(FlowStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.PlanStage == PlanStage.BeforeReview &&
               step.InvocationKind == ExecutionInvocationKind.Worker &&
               ReadPlanDuties(step.PlanDutiesJson).Contains(PlanDuty.Verify);
    }

    internal static string? GetDeliveryQaCorrectionReason(
        FlowRun flow,
        FlowStep step,
        AgentExecutionResult result)
    {
        if (!DeliveryReadinessService.AppliesTo(flow) ||
            !IsDeliveryVerificationStep(step) ||
            AgentHandoffInspector.ParseDynamic(result.Output).IsPushback)
        {
            return null;
        }
        var (plan, planHash, planErrors) =
            DeliveryReadinessService.TryReadAcceptancePlan(flow);
        if (plan is null)
        {
            throw new InvalidOperationException(
                "A Delivery verification turn has no valid acceptance plan: " +
                string.Join("; ", planErrors));
        }
        var issued = DeliveryReadinessService.ReadStepEvidence(
            flow.Events,
            step.Iteration,
            step.Id);
        var observed = DeliveryReadinessService.BuildEvidence(
            step,
            ToObservedToolCalls(step.Id, result),
            issued?.Sequence);
        var currentIds = observed.Items
            .Select(item => item.EvidenceId)
            .ToHashSet(StringComparer.Ordinal);
        var evidence = DeliveryReadinessService.ReadEvidence(
                flow.Events,
                flow.Iteration)
            .Where(item => !currentIds.Contains(item.EvidenceId))
            .Concat(observed.Items)
            .ToArray();
        try
        {
            _ = DeliveryReadinessPolicy.ParseQaOutput(
                result.Output,
                plan,
                planHash,
                evidence);
            return null;
        }
        catch (DeliveryReadinessContractException exception)
        {
            return exception.Message;
        }
    }

    /// <summary>
    /// Validates and durably records the strict verification contract emitted by a Delivery
    /// verification turn. Only a fully validated contract is accepted; invalid responses are
    /// routed through the bounded correction turn before reaching this commit boundary.
    /// </summary>
    private static async Task RecordDeliveryQaContractAsync(
        HarnessDbContext database,
        FlowRun flow,
        FlowStep step,
        string output,
        CancellationToken cancellationToken)
    {
        var events = await database.FlowEvents
            .Where(item =>
                item.FlowRunId == flow.Id &&
                (item.Type == DeliveryReadinessService.AcceptancePlanEventType ||
                 item.Type == DeliveryReadinessService.EvidenceEventType))
            .ToListAsync(cancellationToken);
        var (plan, planHash, planErrors) =
            DeliveryReadinessService.TryReadAcceptancePlan(events, flow.Iteration);
        if (plan is null)
        {
            throw new InvalidOperationException(
                "A Delivery verification turn cannot be accepted without planned acceptance " +
                "criteria: " + string.Join("; ", planErrors));
        }

        // Membership in the host-owned registry is mandatory here, so a fabricated identifier
        // fails the turn instead of quietly authorizing a verified criterion.
        var parsed = DeliveryReadinessPolicy.ParseQaOutput(
            output,
            plan,
            planHash,
            DeliveryReadinessService.ReadEvidence(events, flow.Iteration));
        var data = DeliveryReadinessService.SerializeQa(
            parsed,
            flow.Iteration,
            step.Id,
            step.AgentRole);
        if (await database.FlowEvents.AnyAsync(
                item =>
                    item.FlowRunId == flow.Id &&
                    item.FlowStepId == step.Id &&
                    item.Type == DeliveryReadinessService.QaEventType &&
                    item.DataJson == data,
                cancellationToken))
        {
            return;
        }
        var derived = DeliveryReadinessPolicy.DeriveVerdict(parsed.Document);
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = step.Id,
            Type = DeliveryReadinessService.QaEventType,
            Message =
                $"Recorded a strict verification result with a host-derived verdict of {derived}.",
            DataJson = data
        });
    }

    /// <summary>
    /// Non-authoritative prose diagnostics. These strings are recorded for operators only; they can
    /// never fail, pass, or gate a candidate, because authorization comes from typed criterion
    /// results in the readiness snapshot.
    /// </summary>
    internal static bool ContainsContradictoryQualityProse(string output) =>
        output.ReplaceLineEndings("\n")
            .Split('\n')
            .Select(line => line.Trim().Trim('*', '_', '`').Trim())
            .Any(line =>
                line.StartsWith(
                    "NOT release-ready",
                    StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith(
                    "NOT release ready",
                    StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith(
                    "VERDICT: FAIL",
                    StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith(
                    "VERDICT: BLOCKED",
                    StringComparison.OrdinalIgnoreCase));

    private async Task<Guid> ScheduleStudioContractCorrectionAsync(
        Guid flowId,
        Guid stepId,
        AgentExecutionResult result,
        string contractError,
        DateTimeOffset completedAt,
        long durationMilliseconds,
        CancellationToken cancellationToken,
        bool recoveringFailedAttempt = false)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);
        var flow = await database.Flows
            .AsSplitQuery()
            .Include(item => item.Steps)
            .Include(item => item.TaskProfiles)
            .SingleAsync(item => item.Id == flowId, cancellationToken);
        var source = flow.Steps.Single(item => item.Id == stepId);
        if (source.Status != StepStatus.Running &&
            !(recoveringFailedAttempt &&
              source.Status == StepStatus.Failed &&
              flow.Status == FlowStatus.Failed))
        {
            throw new InvalidOperationException(
                "A Studio response-contract correction can be scheduled only for its running source attempt.");
        }

        foreach (var later in flow.Steps.Where(item =>
                     item.Iteration == source.Iteration &&
                     item.Sequence > source.Sequence))
        {
            later.Sequence += 10;
            if (recoveringFailedAttempt && later.Status == StepStatus.Skipped)
            {
                ResetSkippedStep(later);
            }
        }

        var observedToolCalls = ToObservedToolCalls(source.Id, result);
        if (!await database.AgentToolCalls.AnyAsync(
                item => item.FlowStepId == source.Id,
                cancellationToken))
        {
            database.AgentToolCalls.AddRange(observedToolCalls);
        }
        if (DeliveryReadinessService.AppliesTo(flow) &&
            source.PlanStage == PlanStage.BeforeReview &&
            source.InvocationKind == ExecutionInvocationKind.Worker)
        {
            await RecordDeliveryEvidenceAsync(
                database,
                flow,
                source,
                observedToolCalls,
                cancellationToken);
        }
        source.Status = StepStatus.Completed;
        source.Phase = AgentRunPhase.Succeeded;
        source.OutputSummary = result.Output;
        source.PushbackReason = string.Empty;
        source.ExecutionAttempts = Math.Max(
            source.ExecutionAttempts,
            result.ExecutionAttempts);
        source.CompletedAt = completedAt;
        source.DurationMilliseconds = Math.Max(1, durationMilliseconds);

        var correction = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = source.Iteration,
            Sequence = source.Sequence + 10,
            AgentId = source.AgentId,
            AgentName = source.AgentName,
            AgentRole = source.AgentRole,
            Label = StudioContractCorrectionLabelPrefix + source.AgentName,
            PlanStepKey = source.PlanStepKey,
            PlanDutiesJson = source.PlanDutiesJson,
            PlanStage = source.PlanStage,
            InvocationKind = source.InvocationKind,
            IsOutcomeOwner = source.IsOutcomeOwner,
            PermissionProfile = source.PermissionProfile,
            EffectivePermissionJson = source.EffectivePermissionJson,
            WorkflowRevision = source.WorkflowRevision,
            Status = StepStatus.Pending,
            Phase = AgentRunPhase.PreparingWorkspace,
            Attempt = IsPreMortemStep(source)
                ? source.Attempt
                : flow.Steps
                    .Where(item =>
                        item.Iteration == source.Iteration &&
                        item.AgentId == source.AgentId)
                    .Select(item => item.Attempt)
                    .DefaultIfEmpty()
                    .Max() + 1,
            InputSummary = BuildStudioContractCorrectionAssignment(
                source,
                contractError),
            RemotePublicationAllowed = source.RemotePublicationAllowed,
            RetryOfStepId = GetRetryRootId(source),
            DependsOnStepId = source.DependsOnStepId,
            PushbackRootStepId = source.PushbackRootStepId,
            StableSemanticRootId = GetStableSemanticRootId(source),
            PreMortemOriginStepId = source.PreMortemOriginStepId,
            PreMortemTargetStepId = source.PreMortemTargetStepId,
            PreMortemReviewStepId = source.PreMortemReviewStepId
        };
        flow.Steps.Add(correction);
        database.Entry(correction).State = EntityState.Added;
        PreserveOrTightenRetryPermission(flow, source, correction);
        if (!string.IsNullOrWhiteSpace(correction.EffectivePermissionJson))
        {
            RestrictResponseCorrectionPermission(correction);
        }
        foreach (var dependent in flow.Steps.Where(item =>
                     item.Id != correction.Id &&
                     item.Status == StepStatus.Pending &&
                     item.DependsOnStepId == source.Id))
        {
            dependent.DependsOnStepId = correction.Id;
        }
        foreach (var review in flow.Steps.Where(item =>
                     IsPreMortemStep(item) &&
                     item.Status == StepStatus.Pending &&
                     item.PreMortemTargetStepId == source.Id))
        {
            review.PreMortemTargetStepId = correction.Id;
        }
        var sourceProfile = flow.TaskProfiles.SingleOrDefault(
            item => item.FlowStepId == source.Id);
        if (sourceProfile is not null)
        {
            database.TaskProfiles.Add(
                TaskProfileRules.CopyForStep(sourceProfile, correction.Id));
        }
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = correction.Id,
            Type = "agent.contract-correction-scheduled",
            Message =
                $"{source.AgentName} must correct one invalid Studio response contract before the flow can advance.",
            DataJson = JsonSerializer.Serialize(new
            {
                SourceStepId = source.Id,
                CorrectionStepId = correction.Id,
                Error = ClipText(contractError, 2_000)
            })
        });
        if (recoveringFailedAttempt)
        {
            _lifecycle.Transition(flow, FlowStatus.Queued);
            flow.FailureReason = string.Empty;
            flow.CompletedAt = null;
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = correction.Id,
                Type = "flow.contract-correction-queued",
                Message =
                    $"Recovered {source.AgentName}'s completed execution and its host-observed " +
                    "evidence. Only the invalid response will be corrected; completed work will not be rerun."
            });
        }
        flow.UpdatedAt = completedAt;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return correction.Id;
    }

    internal static string BuildStudioContractCorrectionAssignment(
        FlowStep source,
        string contractError)
    {
        if (IsPreMortemStep(source))
        {
            return
                "Correct only the format of your completed pre-mortem review. Do not repeat " +
                "research or modify the workspace. Preserve the evidence-backed findings and " +
                "return the complete review, not a delta. The first non-empty line must be " +
                "exactly PRE_MORTEM_STATUS: CLEAR or PRE_MORTEM_STATUS: FINDINGS. For FINDINGS, " +
                "include exactly one valid PRE_MORTEM_FINDINGS_BEGIN/END JSON envelope. Do not " +
                "emit a HANDOFF_STATUS marker, introductory commentary, or Markdown fences. " +
                "Keep the response under 9,000 characters and each finding field under 800. " +
                $"Validation error: {ClipText(contractError, 2_000)}" +
                $"{Environment.NewLine}{Environment.NewLine}" +
                $"Previous review to correct:{Environment.NewLine}{source.OutputSummary}";
        }
        var assignment =
            "Your previous Studio response contract was invalid. Do not rerun tools or modify " +
            "the workspace; rewrite the complete response from your existing evidence. Start with " +
            "exactly one standalone HANDOFF_STATUS: COMPLETE or HANDOFF_STATUS: PUSHBACK line. " +
            "For PUSHBACK, use one exact allowed current-iteration owner from the prompt plus one " +
            "bounded PUSHBACK_REASON. Keep the entire replacement under 12,000 characters. " +
            $"Validation error: {ClipText(contractError, 2_000)}";
        if (source.IsOutcomeOwner)
        {
            assignment +=
                " Return exactly one complete flow-outcome document with 1-24 concise, " +
                "consolidated ImplementationDetails between the exact standalone sentinels.";
        }
        if (IsDeliveryVerificationStep(source))
        {
            assignment +=
                " Also replace the complete OUTCOME_QA document. The host has now recorded the " +
                "actual tool observations from your previous turn with their exact identifiers, " +
                "kinds, and success values in the verification context. Do not count or invent " +
                "identifiers. Select the observations that actually prove each criterion and " +
                "whose kind is allowed by its unchanged acceptance plan. Never cite a failed " +
                "call as successful evidence. If the evidence is insufficient, report the honest " +
                "Failed or Blocked result with remediation rather than claiming PASS." +
                $"{Environment.NewLine}{Environment.NewLine}" +
                $"Previous response to correct:{Environment.NewLine}" +
                BoundFailedOutput(source.OutputSummary);
        }
        if (source.PreMortemReviewStepId is not null)
        {
            assignment +=
                " Preserve exactly one PRE_MORTEM_DISPOSITION marker required by this revision.";
        }
        return assignment;
    }

    private static void RestrictResponseCorrectionPermission(FlowStep step)
    {
        var permission = JsonSerializer.Deserialize<EffectiveExecutionPermission>(
                step.EffectivePermissionJson)
            ?? throw new InvalidOperationException(
                "A response correction requires its persisted permission ceiling.");
        step.EffectivePermissionJson = JsonSerializer.Serialize(
            PermissionProfileResolver.ForResponseCorrection(permission));
    }

    internal static async Task<FlowStep> ResolveTaskPermissionSourceAsync(
        HarnessDbContext database,
        FlowStep step,
        CancellationToken cancellationToken)
    {
        var source = step;
        var visited = new HashSet<Guid>();
        while (source.Label.StartsWith(
                   StudioContractCorrectionLabelPrefix,
                   StringComparison.Ordinal))
        {
            if (!visited.Add(source.Id))
            {
                throw new InvalidOperationException(
                    "The response-correction permission lineage contains a cycle.");
            }
            var binding = await database.FlowEvents.AsNoTracking()
                .SingleAsync(item =>
                    item.FlowRunId == step.FlowRunId &&
                    item.FlowStepId == source.Id &&
                    item.Type == "agent.contract-correction-scheduled",
                    cancellationToken);
            using var document = JsonDocument.Parse(
                binding.DataJson ?? throw new InvalidOperationException(
                    "The response-correction permission binding is missing."));
            if (!document.RootElement.TryGetProperty("SourceStepId", out var sourceValue) ||
                !sourceValue.TryGetGuid(out var sourceId))
            {
                throw new InvalidOperationException(
                    "The response correction has no durable task-permission source.");
            }
            source = await database.FlowSteps.AsNoTracking()
                .SingleAsync(item =>
                    item.Id == sourceId &&
                    item.FlowRunId == step.FlowRunId &&
                    item.Iteration == step.Iteration &&
                    item.AgentId == step.AgentId &&
                    item.PlanStepKey == step.PlanStepKey,
                    cancellationToken);
        }
        return source;
    }

    private static async Task<IReadOnlyList<StudioDependencyOutput>>
        ResolveStudioDependencyOutputsAsync(
            HarnessDbContext database,
            FlowRun flow,
            FlowStep currentStep,
            CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentStep.PlanStepKey))
        {
            throw new InvalidOperationException(
                "A Studio worker has no immutable plan-step identity.");
        }

        var document = await database.FlowPlanDocuments
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item =>
                    item.FlowRunId == flow.Id &&
                    item.Iteration == currentStep.Iteration,
                cancellationToken)
            ?? throw new InvalidOperationException(
                "A Studio worker has no accepted plan document for its iteration.");
        var parsed = TeamPlanParser.ParseJson(document.RawJson).Document;
        var planSteps = parsed.Steps ??
                        throw new InvalidOperationException(
                            "The accepted Studio plan has no steps.");
        var byKey = planSteps.ToDictionary(
            item => item.Id,
            StringComparer.Ordinal);
        if (!byKey.TryGetValue(
                currentStep.PlanStepKey,
                out var currentPlanStep))
        {
            throw new InvalidOperationException(
                $"Studio worker '{currentStep.PlanStepKey}' is not present in the accepted iteration plan.");
        }

        var directKeys = currentPlanStep.DependsOn?.ToList() ?? [];
        if (directKeys.Count == 0)
        {
            return [];
        }

        var distanceByKey = new Dictionary<string, int>(
            StringComparer.Ordinal);
        var pending = new Queue<(string Key, int Distance)>();
        foreach (var key in directKeys)
        {
            pending.Enqueue((key, 1));
        }
        while (pending.TryDequeue(out var candidate))
        {
            if (distanceByKey.TryGetValue(
                    candidate.Key,
                    out var existingDistance) &&
                existingDistance <= candidate.Distance)
            {
                continue;
            }
            if (!byKey.TryGetValue(candidate.Key, out var dependency))
            {
                throw new InvalidOperationException(
                    $"Studio worker '{currentStep.PlanStepKey}' references missing dependency '{candidate.Key}'.");
            }
            distanceByKey[candidate.Key] = candidate.Distance;
            foreach (var ancestor in dependency.DependsOn ?? [])
            {
                pending.Enqueue((ancestor, candidate.Distance + 1));
            }
        }

        var relevantKeys = distanceByKey.Keys.ToArray();
        var completed = await database.FlowSteps
            .AsNoTracking()
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.Iteration == currentStep.Iteration &&
                item.Status == StepStatus.Completed &&
                item.OutputSummary != string.Empty &&
                relevantKeys.Contains(item.PlanStepKey))
            .ToListAsync(cancellationToken);
        var effectiveByKey = completed
            .Where(item =>
                byKey.TryGetValue(item.PlanStepKey, out var planned) &&
                string.Equals(
                    item.AgentId,
                    planned.AgentId,
                    StringComparison.Ordinal))
            .GroupBy(item => item.PlanStepKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(item => item.Sequence)
                    .ThenByDescending(item => item.Attempt)
                    .ThenByDescending(item => item.CompletedAt)
                    .ThenBy(item => item.Id)
                    .First(),
                StringComparer.Ordinal);

        var result = new List<StudioDependencyOutput>();
        foreach (var key in directKeys)
        {
            if (!effectiveByKey.TryGetValue(key, out var effective))
            {
                throw new InvalidOperationException(
                    $"Studio worker '{currentStep.PlanStepKey}' cannot start because direct dependency '{key}' has no effective completed output.");
            }
            result.Add(ToStudioDependencyOutput(
                effective,
                StudioDependencyKind.Direct,
                distance: 1));
        }

        var directSet = directKeys.ToHashSet(StringComparer.Ordinal);
        foreach (var key in distanceByKey
                     .Where(item => !directSet.Contains(item.Key))
                     .OrderBy(item => item.Value)
                     .ThenBy(item => byKey[item.Key].Order)
                     .ThenBy(item => item.Key, StringComparer.Ordinal)
                     .Select(item => item.Key))
        {
            if (effectiveByKey.TryGetValue(key, out var effective))
            {
                result.Add(ToStudioDependencyOutput(
                    effective,
                    StudioDependencyKind.Ancestor,
                    distanceByKey[key]));
            }
        }
        return result;
    }

    private static StudioDependencyOutput ToStudioDependencyOutput(
        FlowStep step,
        StudioDependencyKind kind,
        int distance) =>
        new(
            step.PlanStepKey,
            step.AgentId,
            kind,
            distance,
            step.Attempt,
            step.Sequence,
            step.OutputSummary);

    private async Task<FlowStep> ExecuteWithPushbackRecoveryAsync(
        FlowRun flow,
        Guid flowId,
        Guid stepId,
        string workspacePath,
        string planSummary,
        int complexity,
        IReadOnlyDictionary<string, AgentRecord> upstreamOwners,
        CancellationToken cancellationToken)
    {
        var step = await ExecuteStepAsync(
            flow.Id,
            stepId,
            workspacePath,
            planSummary,
            complexity,
            cancellationToken);
        if (step.Status != StepStatus.Pushback)
        {
            return step;
        }

        return await RecoverPushbackAsync(
            flow,
            step,
            workspacePath,
            planSummary,
            complexity,
            upstreamOwners,
            cancellationToken);
    }

    private async Task ExecutePendingStepAsync(
        FlowRun flow,
        Guid stepId,
        string workspacePath,
        string planSummary,
        int complexity,
        IReadOnlyDictionary<string, AgentRecord> upstreamOwners,
        CancellationToken cancellationToken)
    {
        FlowStep pendingStep;
        await using (var database =
                     await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            pendingStep = await database.FlowSteps
                .AsNoTracking()
                .SingleAsync(item => item.Id == stepId, cancellationToken);
        }

        if (IsPreMortemStep(pendingStep))
        {
            var maximumRounds = await GetMaxHandoffRetriesAsync(
                cancellationToken);
            var scepticEnabled = await IsAgentEnabledAsync(
                pendingStep.FlowRunId,
                PreMortemRole,
                cancellationToken);
            if (!ShouldRunPreMortemRound(
                    pendingStep.Attempt,
                    maximumRounds) ||
                !scepticEnabled)
            {
                await SkipDisabledPreMortemReviewAsync(
                    pendingStep.Id,
                    maximumRounds,
                    scepticEnabled,
                    cancellationToken);
                return;
            }
            await HydratePreMortemAssignmentAsync(stepId, cancellationToken);
        }
        else if (pendingStep.PreMortemReviewStepId is not null)
        {
            await HydratePreMortemRevisionAssignmentAsync(
                stepId,
                cancellationToken);
        }

        var completedStep = await ExecuteWithPushbackRecoveryAsync(
            flow,
            flow.Id,
            stepId,
            workspacePath,
            planSummary,
            complexity,
            upstreamOwners,
            cancellationToken);
        await RetargetPendingDependenciesAsync(
            stepId,
            completedStep,
            cancellationToken);
        if (IsPreMortemStep(completedStep))
        {
            await HandleCompletedPreMortemReviewAsync(
                flow,
                completedStep.Id,
                cancellationToken);
            return;
        }
        if (completedStep.PreMortemReviewStepId is not null)
        {
            await HandleCompletedPreMortemRevisionAsync(
                flow,
                completedStep.Id,
                cancellationToken);
            return;
        }

        await RetargetPendingPreMortemAsync(
            stepId,
            completedStep.Id,
            cancellationToken);
    }

    private async Task HydratePreMortemAssignmentAsync(
        Guid reviewStepId,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var review = await database.FlowSteps.SingleAsync(
            item => item.Id == reviewStepId,
            cancellationToken);
        if (!IsPreMortemStep(review) ||
            review.Label.StartsWith(
                StudioContractCorrectionLabelPrefix,
                StringComparison.Ordinal))
        {
            return;
        }

        var targetStepId = review.PreMortemTargetStepId
            ?? throw new InvalidOperationException(
                "A pre-mortem review has no persisted evaluation target.");
        var target = await database.FlowSteps.SingleAsync(
            item => item.Id == targetStepId,
            cancellationToken);
        if (target.Status != StepStatus.Completed)
        {
            var completedRetry = await database.FlowSteps
                .AsNoTracking()
                .Where(item =>
                    item.FlowRunId == review.FlowRunId &&
                    item.Iteration == review.Iteration &&
                    item.AgentId == target.AgentId &&
                    (target.PlanStepKey == string.Empty ||
                     item.PlanStepKey == target.PlanStepKey) &&
                    item.Sequence >= target.Sequence &&
                    item.Sequence < review.Sequence &&
                    item.Status == StepStatus.Completed)
                .OrderByDescending(item => item.Sequence)
                .ThenByDescending(item => item.Attempt)
                .FirstOrDefaultAsync(cancellationToken);
            target = completedRetry
                ?? throw new InvalidOperationException(
                    $"{review.AgentName} cannot run before {target.AgentName} completes.");
            review.PreMortemTargetStepId = target.Id;
        }

        review.InputSummary = BuildPreMortemAssignment(target);
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task SkipDisabledPreMortemReviewAsync(
        Guid stepId,
        int maximumRounds,
        bool scepticEnabled,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);
        var step = await database.FlowSteps.SingleAsync(
            item => item.Id == stepId,
            cancellationToken);
        if (step.Status != StepStatus.Pending)
        {
            return;
        }
        if (!IsPreMortemStep(step))
        {
            throw new InvalidOperationException(
                "Only a durable pre-mortem checkpoint can be intentionally skipped.");
        }

        var completedTargetId = step.DependsOnStepId ??
                                step.PreMortemTargetStepId ??
                                throw new InvalidOperationException(
                                    "The skipped pre-mortem checkpoint has no durable completed target.");
        var completedTarget = await database.FlowSteps.SingleOrDefaultAsync(
                                  item => item.Id == completedTargetId,
                                  cancellationToken)
                              ?? throw new InvalidOperationException(
                                  "The skipped pre-mortem checkpoint target no longer exists.");
        if (completedTarget.FlowRunId != step.FlowRunId ||
            completedTarget.Iteration != step.Iteration ||
            completedTarget.Status != StepStatus.Completed)
        {
            throw new InvalidOperationException(
                "The skipped pre-mortem checkpoint target is not a completed step in the same flow iteration.");
        }
        if (step.PreMortemTargetStepId is { } reviewTargetId &&
            reviewTargetId != completedTarget.Id)
        {
            var reviewTarget = await database.FlowSteps
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.Id == reviewTargetId,
                    cancellationToken)
                ?? throw new InvalidOperationException(
                    "The skipped pre-mortem review target no longer exists.");
            var completedRoot = completedTarget.StableSemanticRootId ??
                                completedTarget.RetryOfStepId ??
                                completedTarget.Id;
            var reviewRoot = reviewTarget.StableSemanticRootId ??
                             reviewTarget.RetryOfStepId ??
                             reviewTarget.Id;
            if (reviewTarget.FlowRunId != step.FlowRunId ||
                reviewTarget.Iteration != step.Iteration ||
                completedRoot != reviewRoot)
            {
                throw new InvalidOperationException(
                    "The skipped pre-mortem checkpoint has conflicting durable targets.");
            }
        }

        var dependents = await database.FlowSteps
            .Where(item =>
                item.FlowRunId == step.FlowRunId &&
                item.Iteration == step.Iteration &&
                item.Status == StepStatus.Pending &&
                item.DependsOnStepId == step.Id)
            .ToListAsync(cancellationToken);
        foreach (var dependent in dependents)
        {
            dependent.DependsOnStepId = completedTarget.Id;
        }
        step.PreMortemTargetStepId = completedTarget.Id;

        step.Status = StepStatus.Skipped;
        step.Phase = AgentRunPhase.Succeeded;
        step.CompletedAt = DateTimeOffset.UtcNow;
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = step.FlowRunId,
            FlowStepId = step.Id,
            Type = "premortem.review-disabled",
            Message =
                $"{step.AgentName} round {step.Attempt} was skipped because " +
                (scepticEnabled
                    ? $"the current per-checkpoint limit is {maximumRounds}."
                    : "the agent is currently disabled or unavailable."),
            DataJson = JsonSerializer.Serialize(new
            {
                CompletedTargetStepId = completedTarget.Id,
                RetargetedDependents = dependents.Count,
                MaximumRounds = maximumRounds,
                ScepticEnabled = scepticEnabled
            })
        });
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task RetargetPendingPreMortemAsync(
        Guid plannedStepId,
        Guid completedStepId,
        CancellationToken cancellationToken)
    {
        if (plannedStepId == completedStepId)
        {
            return;
        }

        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var reviews = await database.FlowSteps
            .Where(item =>
                (item.InvocationKind ==
                     ExecutionInvocationKind.PreMortem ||
                 item.AgentId == PreMortemRole) &&
                item.Status == StepStatus.Pending &&
                (item.PreMortemTargetStepId == plannedStepId ||
                 item.PreMortemOriginStepId == plannedStepId))
            .ToListAsync(cancellationToken);
        foreach (var review in reviews)
        {
            review.PreMortemTargetStepId = completedStepId;
        }
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task RetargetPendingDependenciesAsync(
        Guid plannedStepId,
        FlowStep completedStep,
        CancellationToken cancellationToken)
    {
        var priorDependencyIds = new HashSet<Guid> { plannedStepId };
        if (completedStep.RetryOfStepId is { } retryRootStepId)
        {
            priorDependencyIds.Add(retryRootStepId);
        }
        priorDependencyIds.Remove(completedStep.Id);
        if (priorDependencyIds.Count == 0)
        {
            return;
        }

        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var dependents = await database.FlowSteps
            .Where(step =>
                step.FlowRunId == completedStep.FlowRunId &&
                step.Iteration == completedStep.Iteration &&
                step.Status == StepStatus.Pending &&
                step.DependsOnStepId != null &&
                priorDependencyIds.Contains(step.DependsOnStepId.Value))
            .ToListAsync(cancellationToken);
        if (dependents.Count == 0)
        {
            return;
        }
        foreach (var dependent in dependents)
        {
            dependent.DependsOnStepId = completedStep.Id;
        }
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = completedStep.FlowRunId,
            FlowStepId = completedStep.Id,
            Type = "handoff.dependency-retargeted",
            Message =
                $"{dependents.Count} pending step dependency link(s) now reference the " +
                $"effective {completedStep.AgentName} result."
        });
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task HydratePreMortemRevisionAssignmentAsync(
        Guid revisionStepId,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var revision = await database.FlowSteps.SingleAsync(
            item => item.Id == revisionStepId,
            cancellationToken);
        var reviewStepId = revision.PreMortemReviewStepId
            ?? throw new InvalidOperationException(
                "A pre-mortem revision has no persisted review source.");
        var review = await database.FlowSteps
            .AsNoTracking()
            .SingleAsync(item => item.Id == reviewStepId, cancellationToken);
        if (review.Status != StepStatus.Completed)
        {
            throw new InvalidOperationException(
                $"{revision.AgentName} cannot revise before the pre-mortem review completes.");
        }

        revision.InputSummary = BuildPreMortemRevisionAssignment(
            review.OutputSummary,
            revision.PlanDutiesJson);
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task HandleCompletedPreMortemReviewAsync(
        FlowRun flow,
        Guid reviewStepId,
        CancellationToken cancellationToken)
    {
        FlowStep review;
        PreMortemReview result;
        bool alreadyResolved;
        await using (var database =
                     await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            review = await database.FlowSteps
                .AsNoTracking()
                .SingleAsync(item => item.Id == reviewStepId, cancellationToken);
            alreadyResolved = await database.FlowEvents.AnyAsync(
                                  item =>
                                      item.FlowStepId == reviewStepId &&
                                      (item.Type == "premortem.review-cleared" ||
                                       item.Type == "premortem.revision-scheduled"),
                                  cancellationToken) ||
                              await database.FlowSteps.AnyAsync(
                                  item => item.PreMortemReviewStepId == reviewStepId,
                                  cancellationToken) ||
                              await database.FlowSteps.AnyAsync(
                                  item =>
                                      item.FlowRunId == review.FlowRunId &&
                                      item.Iteration == review.Iteration &&
                                      item.RetryOfStepId == (review.RetryOfStepId ?? review.Id) &&
                                      item.Sequence > review.Sequence &&
                                      item.Label.StartsWith(StudioContractCorrectionLabelPrefix),
                                  cancellationToken);
        }
        if (alreadyResolved)
        {
            return;
        }
        result = PreMortemRules.ParseReview(review.OutputSummary);
        if (!result.HasFindings)
        {
            await AddEventAsync(
                flow.Id,
                review.Id,
                "premortem.review-cleared",
                $"{review.AgentName} found no remaining evidence-backed failure case.",
                cancellationToken);
            return;
        }

        await SchedulePreMortemRevisionAsync(
            flow,
            review,
            result,
            cancellationToken);
    }

    private async Task SchedulePreMortemRevisionAsync(
        FlowRun flow,
        FlowStep review,
        PreMortemReview result,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        if (await database.FlowSteps.AnyAsync(
                item => item.PreMortemReviewStepId == review.Id,
                cancellationToken))
        {
            return;
        }

        var targetStepId = review.PreMortemTargetStepId
            ?? throw new InvalidOperationException(
                "A completed pre-mortem review has no evaluation target.");
        var target = await database.FlowSteps
            .AsNoTracking()
            .SingleAsync(item => item.Id == targetStepId, cancellationToken);
        var permissionSource = await ResolveTaskPermissionSourceAsync(
            database, target, cancellationToken);
        var laterSteps = await database.FlowSteps
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.Iteration == flow.Iteration &&
                item.Sequence > review.Sequence)
            .ToListAsync(cancellationToken);
        foreach (var laterStep in laterSteps)
        {
            laterStep.Sequence += 10;
        }

        var revisionAttempt = (await database.FlowSteps
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.Iteration == flow.Iteration &&
                item.AgentId == target.AgentId &&
                item.PlanStepKey == target.PlanStepKey)
            .Select(item => (int?)item.Attempt)
            .MaxAsync(cancellationToken) ?? 0) + 1;
        var revision = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Sequence = review.Sequence + 10,
            AgentId = target.AgentId,
            AgentName = target.AgentName,
            AgentRole = target.AgentRole,
            Label = $"Revision after pre-mortem findings (round {review.Attempt})",
            PlanStepKey = target.PlanStepKey,
            PlanDutiesJson = target.PlanDutiesJson,
            PlanStage = target.PlanStage,
            InvocationKind = target.InvocationKind,
            IsOutcomeOwner = target.IsOutcomeOwner,
            PermissionProfile = permissionSource.PermissionProfile,
            EffectivePermissionJson = permissionSource.EffectivePermissionJson,
            WorkflowRevision = target.WorkflowRevision,
            RemotePublicationAllowed = target.RemotePublicationAllowed,
            Status = StepStatus.Pending,
            Attempt = revisionAttempt,
            InputSummary = BuildPreMortemRevisionAssignment(
                review.OutputSummary,
                target.PlanDutiesJson),
            PreMortemOriginStepId = review.PreMortemOriginStepId,
            PreMortemReviewStepId = review.Id
        };
        revision.StableSemanticRootId = target.StableSemanticRootId ?? target.Id;
        var permissionTightened =
            PreserveOrTightenRetryPermission(
                flow,
                permissionSource,
                revision);
        database.FlowSteps.Add(revision);
        var targetProfile = await database.TaskProfiles
            .AsNoTracking()
            .SingleAsync(
                item => item.FlowStepId == target.Id,
                cancellationToken);
        database.TaskProfiles.Add(
            TaskProfileRules.CopyForStep(targetProfile, revision.Id));
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = review.Id,
            Type = "premortem.findings",
            Message =
                $"{review.AgentName} reported {result.Findings.Count} evidence-backed " +
                $"finding(s) for {target.AgentName}."
        });
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = revision.Id,
            Type = "premortem.revision-scheduled",
            Message =
                $"{target.AgentName} will resume its original Copilot session and return a " +
                "complete result after evaluating the pre-mortem findings."
        });
        if (permissionTightened)
        {
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = revision.Id,
                Type = "step.permission-policy-tightened",
                Message =
                    "The retry retained its original permission ceiling and incorporated only stricter current WORKFLOW.md restrictions."
            });
        }
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task HandleCompletedPreMortemRevisionAsync(
        FlowRun flow,
        Guid revisionStepId,
        CancellationToken cancellationToken)
    {
        FlowStep revision;
        bool alreadyResolved;
        await using (var database =
                     await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            revision = await database.FlowSteps
                .AsNoTracking()
                .SingleAsync(item => item.Id == revisionStepId, cancellationToken);
            alreadyResolved = await database.FlowEvents.AnyAsync(
                                  item =>
                                      item.FlowStepId == revisionStepId &&
                                      (item.Type == "premortem.revision-unchanged" ||
                                       item.Type == "premortem.round-limit-exhausted" ||
                                       item.Type == "premortem.review-disabled"),
                                  cancellationToken) ||
                              await database.FlowSteps.AnyAsync(
                                  item =>
                                      (item.InvocationKind ==
                                           ExecutionInvocationKind.PreMortem ||
                                       item.AgentId == PreMortemRole) &&
                                      item.PreMortemTargetStepId == revisionStepId,
                                  cancellationToken);
        }
        if (alreadyResolved)
        {
            return;
        }

        var disposition = PreMortemRules.ParseDisposition(revision.OutputSummary);
        if (disposition == PreMortemDisposition.Unchanged)
        {
            await AddEventAsync(
                flow.Id,
                revision.Id,
                "premortem.revision-unchanged",
                $"{revision.AgentName} rejected or absorbed the findings without changing the " +
                "complete handoff; the flow will advance.",
                cancellationToken);
            return;
        }

        var originStepId = revision.PreMortemOriginStepId
            ?? throw new InvalidOperationException(
                "A pre-mortem revision has no persisted checkpoint origin.");
        int completedRounds;
        AgentRecord? sceptic;
        await using (var database =
                     await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            var persistedRounds = await database.FlowSteps
                .Where(item =>
                    item.FlowRunId == flow.Id &&
                    item.Iteration == flow.Iteration &&
                    (item.InvocationKind ==
                         ExecutionInvocationKind.PreMortem ||
                     item.AgentId == PreMortemRole) &&
                    item.PreMortemOriginStepId == originStepId)
                .Select(item => item.Attempt)
                .ToListAsync(cancellationToken);
            completedRounds = CountPreMortemRounds(persistedRounds);
        }
        var maxRounds = await GetMaxHandoffRetriesAsync(cancellationToken);
        if (completedRounds >= maxRounds)
        {
            await AddEventAsync(
                flow.Id,
                revision.Id,
                "premortem.round-limit-exhausted",
                $"{revision.AgentName} returned a complete adjusted result after " +
                $"{completedRounds} of {maxRounds} allowed sceptic round(s); the flow will advance.",
                cancellationToken);
            return;
        }
        sceptic = (await _flowAgentSnapshots.GetAgentsAsync(
                flow.Id,
                cancellationToken))
            .SingleOrDefault(item =>
                item.Id == PreMortemRole &&
                item.Enabled);
        if (sceptic is null)
        {
            await AddEventAsync(
                flow.Id,
                revision.Id,
                "premortem.review-disabled",
                $"{revision.AgentName} returned a complete adjusted result, but the next " +
                "sceptic round is unavailable; the flow will advance.",
                cancellationToken);
            return;
        }

        await AddPreMortemReviewStepAsync(
            flow,
            sceptic,
            revision.Id,
            originStepId,
            revision.Sequence + 10,
            completedRounds + 1,
            cancellationToken,
            shiftLaterSteps: true);
    }

    private async Task<bool> IsAgentEnabledAsync(
        Guid flowId,
        string agentId,
        CancellationToken cancellationToken)
        => (await _flowAgentSnapshots.GetAgentsAsync(
                flowId,
                cancellationToken))
            .Any(agent =>
                agent.Id == agentId &&
                agent.Enabled);

    private async Task RecoverUnresolvedPreMortemsAsync(
        FlowRun flow,
        CancellationToken cancellationToken)
    {
        List<(Guid Id, bool IsReview)> candidates;
        await using (var database =
                     await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            candidates = await database.FlowSteps
                .AsNoTracking()
                .Where(item =>
                    item.FlowRunId == flow.Id &&
                    item.Iteration == flow.Iteration &&
                    item.Status == StepStatus.Completed &&
                    (item.InvocationKind ==
                         ExecutionInvocationKind.PreMortem ||
                     item.AgentId == PreMortemRole ||
                     item.PreMortemReviewStepId != null))
                .OrderBy(item => item.Sequence)
                .Select(item => new ValueTuple<Guid, bool>(
                    item.Id,
                    item.InvocationKind ==
                        ExecutionInvocationKind.PreMortem ||
                    item.AgentId == PreMortemRole))
                .ToListAsync(cancellationToken);
        }

        foreach (var candidate in candidates)
        {
            if (candidate.IsReview)
            {
                await HandleCompletedPreMortemReviewAsync(
                    flow,
                    candidate.Id,
                    cancellationToken);
            }
            else
            {
                await HandleCompletedPreMortemRevisionAsync(
                    flow,
                    candidate.Id,
                    cancellationToken);
            }
        }
    }

    internal async Task RecoverCompletedStepAsync(
        Guid flowId,
        Guid stepId,
        Guid sessionId,
        AgentRunResult recoveredResult,
        DateTimeOffset? completedAt,
        CancellationToken cancellationToken)
    {
        var result = new AgentExecutionResult(
            recoveredResult.OutputSummary,
            $"Recovered from completed Copilot session {sessionId:D}.",
            1,
            recoveredResult.ToolCalls);
        try
        {
            if (await TryScheduleRecoveredQaCorrectionAsync(
                    flowId,
                    stepId,
                    result,
                    completedAt ?? DateTimeOffset.UtcNow,
                    recoveringFailedAttempt: false,
                    cancellationToken))
            {
                return;
            }
            await CompleteStepAsync(
                flowId,
                stepId,
                result,
                completedAt ?? DateTimeOffset.UtcNow,
                durationMilliseconds: null,
                sessionId,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await MarkStepFailedAsync(
                stepId,
                exception,
                durationMilliseconds: null,
                cancellationToken,
                result.Output,
                result);
            throw;
        }
    }

    private async Task<bool> TryScheduleRecoveredQaCorrectionAsync(
        Guid flowId,
        Guid stepId,
        AgentExecutionResult result,
        DateTimeOffset completedAt,
        bool recoveringFailedAttempt,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var flow = await database.Flows
            .Include(item => item.Events)
            .SingleAsync(item => item.Id == flowId, cancellationToken);
        var step = await database.FlowSteps
            .SingleAsync(item => item.Id == stepId, cancellationToken);
        if (step.Label.StartsWith(
                StudioContractCorrectionLabelPrefix,
                StringComparison.Ordinal))
        {
            return false;
        }
        var error = GetDeliveryQaCorrectionReason(flow, step, result);
        if (error is null)
        {
            return false;
        }
        await ScheduleStudioContractCorrectionAsync(
            flowId,
            stepId,
            result,
            error,
            completedAt,
            step.DurationMilliseconds > 0
                ? step.DurationMilliseconds
                : Math.Max(1, (long)(completedAt -
                    (step.StartedAt ?? completedAt)).TotalMilliseconds),
            cancellationToken,
            recoveringFailedAttempt);
        return true;
    }

    private async Task<FlowStep> CompleteStepAsync(
        Guid flowId,
        Guid stepId,
        AgentExecutionResult result,
        DateTimeOffset completedAt,
        long? durationMilliseconds,
        Guid? recoveredSessionId,
        CancellationToken cancellationToken)
    {
        var preparedPublication = await PrepareGovernedPublicationIfNeededAsync(
            flowId,
            stepId,
            result,
            cancellationToken);
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);
        var completion = await StageCompletedStepAsync(
            database,
            flowId,
            stepId,
            result,
            completedAt,
            durationMilliseconds,
            recoveredSessionId,
            preparedPublication,
            cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (completion.PreparedGateUpdates.Count > 0)
        {
            handoffGate.RestoreHistory(completion.PreparedGateUpdates);
        }
        try
        {
            await RecordCompletionObservationAsync(
                completion,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Completion telemetry failed after durable commit for step {StepId}",
                completion.Step.Id);
        }
        ThrowIfCompletionBlocked(completion.GateRecord);
        return completion.Step;
    }

    private async Task<PreparedPublicationCompletion?>
        PrepareGovernedPublicationIfNeededAsync(
        Guid flowId,
        Guid stepId,
        AgentExecutionResult result,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var publication = await (
                from step in database.FlowSteps
                join flow in database.Flows
                    on step.FlowRunId equals flow.Id
                where step.Id == stepId && flow.Id == flowId
                select new
                {
                    step.RemotePublicationAllowed,
                    Step = step,
                    flow
                })
            .SingleAsync(cancellationToken);
        var reviewedPublication =
            ReviewCoordinator.IsPublicationStep(
                publication.flow,
                publication.Step);
        if (!reviewedPublication)
        {
            return null;
        }
        RepositoryKnowledgeRecap? knowledgeRecap = null;
        var handoff = AgentHandoffInspector.ParseDynamic(result.Output);
        if (handoff.IsPushback)
        {
            return null;
        }
        _ = await RefreshAndRequirePublicationAuthorityAsync(
            database,
            publication.flow,
            publication.Step,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(publication.flow.WorkspacePath) ||
            !Directory.Exists(publication.flow.WorkspacePath))
        {
            throw new InvalidOperationException(
                "The accepted Delivery workspace is unavailable for the post-implementation repository knowledge recap.");
        }
        var repositories = RepositoryAnalyzer.FindGitRepositories(
            publication.flow.WorkspacePath);
        if (repositories.Count == 0)
        {
            throw new InvalidOperationException(
                "The accepted Delivery workspace contains no Git repositories for the post-implementation repository knowledge recap.");
        }
        var inventory = await RepositoryAnalyzer.BuildStudyInventoryAsync(
            publication.flow.WorkspacePath,
            repositories,
            cancellationToken);
        knowledgeRecap =
            RepositoryKnowledgeSynthesizer.ParseAndRenderRecap(
                result.Output,
                publication.flow.WorkspacePath,
                inventory,
                RepositoryKnowledgeSynthesizer.ResolveProjectName(
                    publication.flow.RepositoryKnowledge,
                    publication.flow.RepositoryPath));
        if (knowledgeRecap.Changed &&
            string.Equals(
                knowledgeRecap.Knowledge,
                publication.flow.RepositoryKnowledge,
                StringComparison.Ordinal))
        {
            throw new RepositoryKnowledgeContractException(
                ["Changed cannot be true when the synthesized knowledge is identical to the flow baseline"]);
        }

        var verificationOutput = await (candidatePublisher
            ?? throw new InvalidOperationException(
                "No verified candidate publisher is configured."))
            .PublishAsync(
                publication.flow,
                stepId,
                cancellationToken);
        return new PreparedPublicationCompletion(
            verificationOutput,
            knowledgeRecap);
    }

    private async Task<EffectiveExecutionPermission>
        RefreshAndRequirePublicationAuthorityAsync(
            HarnessDbContext database,
            FlowRun flow,
            FlowStep publication,
            CancellationToken cancellationToken)
    {
        if (!ReviewCoordinator.IsPublicationStep(
                flow,
                publication))
        {
            throw new InvalidOperationException(
                "Remote publication authority is available only to the sole planned publication step.");
        }
        if (!await HasDurablePublicationApprovalAsync(
                database,
                flow,
                publication,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "Publication cannot execute before durable customer acceptance.");
        }
        if (string.IsNullOrWhiteSpace(
                publication.EffectivePermissionJson))
        {
            throw new InvalidOperationException(
                "The publication attempt has no persisted effective permission document.");
        }

        EffectiveExecutionPermission persisted;
        try
        {
            persisted =
                JsonSerializer.Deserialize<EffectiveExecutionPermission>(
                    publication.EffectivePermissionJson)
                ?? throw new InvalidOperationException(
                    "The publication attempt has an empty effective permission document.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The publication attempt has an invalid effective permission document.",
                exception);
        }
        PermissionProfileResolver.ValidatePersisted(
            persisted,
            publication.PermissionProfile);
        var request = new PermissionResolutionRequest(
            flow.Kind,
            publication.InvocationKind,
            publication.PlanStage,
            ReadPlanDuties(
                    publication.PlanDutiesJson)
                .ToImmutableArray(),
            DurableReviewDecision: ReviewDecision.Accepted,
            DurableApproval: true,
            IsOnlyPlannedPublishStep: true);
        var effectiveWorkflow =
            workflowProvider.GetEffective();
        var current = _permissionResolver.Resolve(
            request,
            PermissionProfileResolver.FromWorkflow(
                effectiveWorkflow));
        var effective = PermissionProfileResolver.Tighten(
            persisted,
            current);
        PermissionProfileResolver.ValidatePersisted(
            effective,
            effective.Profile,
            request);
        var remotePublicationAllowed =
            publication.RemotePublicationAllowed &&
            PermissionProfileResolver.GrantsRemotePublication(
                effective);
        if (!PermissionProfileResolver.Equivalent(
                persisted,
                effective) ||
            publication.RemotePublicationAllowed !=
            remotePublicationAllowed)
        {
            publication.PermissionProfile = effective.Profile;
            publication.EffectivePermissionJson =
                JsonSerializer.Serialize(effective);
            publication.RemotePublicationAllowed =
                remotePublicationAllowed;
            publication.WorkflowRevision =
                effectiveWorkflow.Revision;
            await database.SaveChangesAsync(cancellationToken);
        }
        return ReviewCoordinator.RequireRemotePublicationAuthority(
            flow,
            publication);
    }

    private static bool IsPreMortemStep(FlowStep step) =>
        step.InvocationKind == ExecutionInvocationKind.PreMortem ||
        string.Equals(
            step.AgentId,
            PreMortemRole,
            StringComparison.Ordinal);

    private static Guid GetStableSemanticRootId(FlowStep step) =>
        step.StableSemanticRootId ?? step.RetryOfStepId ?? step.Id;

    private static Guid GetRetryRootId(FlowStep step) =>
        step.RetryOfStepId ?? GetStableSemanticRootId(step);

    private async Task<StagedStepCompletion> StageCompletedStepAsync(
        HarnessDbContext database,
        Guid flowId,
        Guid stepId,
        AgentExecutionResult result,
        DateTimeOffset completedAt,
        long? durationMilliseconds,
        Guid? recoveredSessionId,
        PreparedPublicationCompletion? preparedPublication,
        CancellationToken cancellationToken)
    {
        var step = await database.FlowSteps.SingleAsync(
            item => item.Id == stepId,
            cancellationToken);
        var flow = await database.Flows.SingleAsync(
            item => item.Id == flowId,
            cancellationToken);
        var observedToolCalls = ToObservedToolCalls(stepId, result);
        if (!await database.AgentToolCalls.AnyAsync(
                item => item.FlowStepId == stepId,
                cancellationToken))
        {
            database.AgentToolCalls.AddRange(observedToolCalls);
        }
        if (step.InvocationKind == ExecutionInvocationKind.Intake)
        {
            ValidateStudioIntakeCompletion(flow, step, result.Output);
        }
        if (IsPreMortemStep(step))
        {
            _ = PreMortemRules.ParseReview(result.Output);
        }
        if (step.PreMortemReviewStepId is not null)
        {
            _ = ValidatePreMortemRevisionOutput(result.Output);
        }
        DynamicHandoffStatus? dynamicHandoff = null;
        if (step.InvocationKind is
                ExecutionInvocationKind.Worker or
                ExecutionInvocationKind.Publication or
                ExecutionInvocationKind.Planning)
        {
            try
            {
                dynamicHandoff = AgentHandoffInspector.ParseDynamic(result.Output);
            }
            catch (InvalidOperationException) when (
                step.InvocationKind == ExecutionInvocationKind.Planning)
            {
                // Planning contract errors are persisted and routed through the bounded
                // Team Lead correction turn after this execution completes.
            }
            if (dynamicHandoff?.IsPushback == true)
            {
                await ValidateDynamicPushbackTargetAsync(
                    database,
                    flow,
                    step,
                    dynamicHandoff,
                    cancellationToken);
            }
        }
        var pushbackReason =
            IsPreMortemStep(step)
            ? null
            : dynamicHandoff?.Reason;
        var pushedBack = pushbackReason is not null;
        ParsedFlowOutcome? normalizedOutcome = null;
        if (!pushedBack && step.IsOutcomeOwner)
        {
            var advisoryConfig =
                workflowProvider.GetEffective().Config.Studio.Advisory;
            normalizedOutcome = flow.Kind == FlowKind.Advisory
                ? FlowOutcomeParser.Parse(
                    result.Output,
                    advisoryConfig.MaxArtifactCount,
                    advisoryConfig.MaxTotalArtifactBytes)
                : FlowOutcomeParser.Parse(result.Output);
            if (flow.Kind == FlowKind.Advisory)
            {
                flow.Outcome = OutcomeType.None;
                flow.OutcomeUrl = string.Empty;
                flow.OutcomeLabel = string.Empty;
            }
            flow.OutcomeContractJson = normalizedOutcome.RawJson;
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = step.Id,
                Type = "outcome.contract-accepted",
                Message =
                    "Accepted the required flow outcome from the planned outcome owner.",
                DataJson = JsonSerializer.Serialize(new
                {
                    normalizedOutcome.Document.Goal,
                    normalizedOutcome.Document.Summary,
                    ImplementationDetailCount =
                        normalizedOutcome.Document.ImplementationDetails!.Count,
                    ArtifactCount =
                        normalizedOutcome.Document.Artifacts!.Count
                })
            });
        }
        var publicationStep =
            ReviewCoordinator.IsPublicationStep(flow, step);
        if (!pushedBack && DeliveryReadinessService.AppliesTo(flow) &&
            step.PlanStage == PlanStage.BeforeReview &&
            step.InvocationKind == ExecutionInvocationKind.Worker)
        {
            // The host mints evidence identifiers from its own execution records, so a later
            // verification turn can only cite observations the host actually made.
            await RecordDeliveryEvidenceAsync(
                database,
                flow,
                step,
                observedToolCalls,
                cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
        }
        if (!pushedBack && DeliveryReadinessService.AppliesTo(flow) &&
            IsDeliveryVerificationStep(step))
        {
            await RecordDeliveryQaContractAsync(
                database,
                flow,
                step,
                result.Output,
                cancellationToken);
        }
        if ((step.RemotePublicationAllowed ||
             step.PlanStage == PlanStage.AfterApproval ||
             string.Equals(
                 step.PlanStepKey,
                 flow.PublicationPlanStepKey,
                 StringComparison.Ordinal) ||
             ReadPlanDuties(step.PlanDutiesJson).Contains(PlanDuty.Publish)) &&
            !publicationStep)
        {
            throw new InvalidOperationException(
                "Publication completion does not match its durable planned authority.");
        }
        if (publicationStep &&
            !await HasDurablePublicationApprovalAsync(
                database,
                flow,
                step,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "Publication completion has no durable accepted CustomerReview.");
        }
        if (publicationStep && !pushedBack)
        {
            var verificationOutput = preparedPublication?.VerificationOutput
                ?? throw new InvalidOperationException(
                    "Publication requires a host-controlled sealed-candidate publication record.");
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = step.Id,
                Type = "delivery.reviewed-candidate-published",
                Message =
                    "The harness published only the candidate identity sealed before customer review."
            });
            var published = await (publicationVerifier
                ?? throw new InvalidOperationException(
                    "No published outcome verifier is configured."))
                .VerifyAsync(flow, verificationOutput, cancellationToken);
            flow.OutcomeUrl = published.Url;
            flow.OutcomeLabel = published.Label;
            await ApplyRepositoryKnowledgeRecapAsync(
                database,
                flow,
                step,
                preparedPublication?.KnowledgeRecap
                ?? throw new InvalidOperationException(
                    "Publication completion has no validated repository knowledge recap."),
                cancellationToken);
        }
        IReadOnlyList<HandoffGateRecord> preparedGateUpdates = [];
        var actionType = pushedBack
            ? HandoffActionType.RequestRevision
            : HandoffActionType.Advance;
        var gateRecord = handoffGate.SubmitProposal(new HandoffProposal
        {
            FlowRunId = flowId,
            FlowStepId = stepId,
            ActionType = actionType,
            Summary = pushbackReason ?? result.Output,
            Evidence = pushedBack ? result.Output : result.Evidence,
            BlastRadius = pushedBack
                ? HandoffBlastRadius.Low
                : HandoffBlastRadius.Medium
        });
        database.GateRecords.Add(gateRecord);
        var elapsedMilliseconds = durationMilliseconds ?? Math.Max(
            1,
            (long)(completedAt - (step.StartedAt ?? completedAt)).TotalMilliseconds);
        step.Status = pushedBack ? StepStatus.Pushback : StepStatus.Completed;
        step.Phase = pushedBack
            ? AgentRunPhase.Failed
            : AgentRunPhase.Succeeded;
        step.OutputSummary = result.Output;
        step.PushbackReason = pushbackReason ?? string.Empty;
        step.ExecutionAttempts = Math.Max(step.ExecutionAttempts, result.ExecutionAttempts);
        step.CompletedAt = completedAt;
        step.DurationMilliseconds = elapsedMilliseconds;
        if (!pushedBack && step.RetryOfStepId is { } retryRootStepId)
        {
            var dependents = await database.FlowSteps
                .Where(dependent =>
                    dependent.FlowRunId == step.FlowRunId &&
                    dependent.Iteration == step.Iteration &&
                    (dependent.Status == StepStatus.Pending ||
                     dependent.Status == StepStatus.Skipped) &&
                    (dependent.DependsOnStepId == retryRootStepId ||
                     dependent.DependsOnStepId == step.Id))
                .ToListAsync(cancellationToken);
            var retargeted = RetargetDependentSteps(step, dependents);
            if (retargeted > 0)
            {
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flowId,
                    FlowStepId = step.Id,
                    Type = "handoff.dependency-retargeted",
                    Message =
                        $"{retargeted} pending step dependency link(s) now reference the " +
                        $"effective {step.AgentName} result."
                });
            }
        }
        if (recoveredSessionId is not null)
        {
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flowId,
                FlowStepId = stepId,
                Type = "step.session-output-recovered",
                Message =
                    $"{step.AgentName} output was recovered from completed Copilot session " +
                    $"{recoveredSessionId:D} after restart."
            });
        }
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flowId,
            FlowStepId = stepId,
            Type = pushedBack ? "handoff.pushback" : "step.completed",
            Message = pushedBack
                ? $"{step.AgentName} requested an upstream revision: {pushbackReason}"
                : $"{step.AgentName} completed in " +
                  $"{FormatDuration(TimeSpan.FromMilliseconds(elapsedMilliseconds))}."
        });
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flowId,
            FlowStepId = stepId,
            Type = "gate.decision",
            Message =
                $"{(pushedBack ? "Revision request" : "Handoff gate")}: " +
                $"{gateRecord.Decision} at {gateRecord.TrustLevelAtDecision} trust."
        });
        if (result.ExecutionAttempts > 1)
        {
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flowId,
                FlowStepId = stepId,
                Type = "step.retried",
                Message =
                    $"{step.AgentName} recovered after {result.ExecutionAttempts} runtime attempts."
            });
        }

        return new StagedStepCompletion(
            step,
            gateRecord,
            pushedBack,
            ContractInvalid: false,
            elapsedMilliseconds,
            step.ExecutionAttempts,
            preparedGateUpdates);
    }

    internal static async Task ApplyRepositoryKnowledgeRecapAsync(
        HarnessDbContext database,
        FlowRun flow,
        FlowStep publication,
        RepositoryKnowledgeRecap recap,
        CancellationToken cancellationToken)
    {
        var recapEventTypes = new[]
        {
            RepositoryKnowledgeUnchangedEventType,
            RepositoryKnowledgeRefreshSkippedEventType
        };
        if (await database.FlowEvents.AnyAsync(
                item =>
                    item.FlowRunId == flow.Id &&
                    item.FlowStepId == publication.Id &&
                    recapEventTypes.Contains(item.Type),
                cancellationToken))
        {
            return;
        }

        if (!recap.Changed)
        {
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = publication.Id,
                Type = RepositoryKnowledgeUnchangedEventType,
                Message =
                    "Post-implementation recap found no durable Repository Knowledge change.",
                DataJson = JsonSerializer.Serialize(new
                {
                    recap.Reason
                })
            });
            return;
        }

        var knowledge = recap.Knowledge
            ?? throw new InvalidOperationException(
                "A changed repository knowledge recap has no synthesized replacement.");
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = publication.Id,
            Type = RepositoryKnowledgeRefreshSkippedEventType,
            Message =
                "Post-implementation recap remained flow-scoped because the configured source checkout has not integrated the isolated publication. Re-analyze the source after integration.",
            DataJson = JsonSerializer.Serialize(new
            {
                recap.Reason,
                PreviousSha256 =
                    OutcomeVerificationRules.ComputeSha256(
                        flow.RepositoryKnowledge),
                NewSha256 =
                    OutcomeVerificationRules.ComputeSha256(
                        knowledge)
            })
        });
    }

    internal static ParsedIntake ValidateStudioIntakeCompletion(
        FlowRun flow,
        FlowStep step,
        string output)
    {
        var parsed = IntakeParser.Parse(output);
        if (string.Equals(
                step.PlanStepKey,
                RefinementIntakePlanStepKey,
                StringComparison.Ordinal) &&
            (parsed.Document.Status != IntakeStatus.Confirmed ||
             parsed.Document.FlowKind != flow.Kind))
        {
            throw new InvalidOperationException(
                "Account Manager refinement normalization must preserve the flow kind and return Confirmed.");
        }
        return parsed;
    }

    internal static string BoundFailedOutput(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Length <= MaximumFailedOutputCharacters)
        {
            return output;
        }

        var suffix =
            $"{Environment.NewLine}...[output bounded from {output.Length} characters; " +
            $"{OutcomeVerificationRules.ComputeSha256(output)}]...";
        var available = MaximumFailedOutputCharacters - suffix.Length;
        if (available <= 1)
        {
            return suffix[^MaximumFailedOutputCharacters..];
        }
        var prefixLength = available / 2;
        var tailLength = available - prefixLength;
        if (char.IsHighSurrogate(output[prefixLength - 1]))
        {
            prefixLength--;
            tailLength++;
        }
        var tailStart = output.Length - tailLength;
        if (tailStart < output.Length &&
            char.IsLowSurrogate(output[tailStart]))
        {
            tailStart++;
        }
        return output[..prefixLength] +
               suffix +
               output[tailStart..];
    }

    private static async Task ValidateDynamicPushbackTargetAsync(
        HarnessDbContext database,
        FlowRun flow,
        FlowStep blockedStep,
        DynamicHandoffStatus handoff,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(blockedStep.PlanStepKey) ||
            string.IsNullOrWhiteSpace(handoff.OwnerPlanStepKey))
        {
            throw new InvalidOperationException(
                "Studio pushback requires both blocked and owner plan-step identities.");
        }
        var planJson = await database.FlowPlanDocuments
            .AsNoTracking()
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.Iteration == flow.Iteration)
            .Select(item => item.RawJson)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "Studio pushback cannot be validated without the accepted plan document.");
        var document = TeamPlanParser.ParseJson(planJson).Document;
        var ownerIsPlannedAncestor = TeamPlanValidator.IsDependencyAncestor(
            document,
            handoff.OwnerPlanStepKey,
            blockedStep.PlanStepKey);
        if (!ownerIsPlannedAncestor)
        {
            throw new InvalidOperationException(
                $"Pushback owner '{handoff.OwnerPlanStepKey}' must be an earlier dependency " +
                $"or ancestor of plan step '{blockedStep.PlanStepKey}'.");
        }
        var ownerExists = await database.FlowSteps
            .AsNoTracking()
            .AnyAsync(
                item =>
                    item.FlowRunId == flow.Id &&
                    item.Iteration == flow.Iteration &&
                    item.PlanStepKey == handoff.OwnerPlanStepKey &&
                    item.Sequence < blockedStep.Sequence &&
                    item.Status == StepStatus.Completed,
                cancellationToken);
        if (!ownerExists)
        {
            throw new InvalidOperationException(
                $"Pushback owner '{handoff.OwnerPlanStepKey}' has no completed earlier step.");
        }
    }

    private async Task RecordCompletionObservationAsync(
        StagedStepCompletion completion,
        CancellationToken cancellationToken)
    {
        if (completion.PushedBack || completion.ContractInvalid)
        {
            if (completion.PushedBack)
            {
                await observationRecorder.RecordPushbackDetectionAsync(
                    completion.Step.Id,
                    completion.ElapsedMilliseconds,
                    completion.ExecutionAttempts,
                    cancellationToken);
            }
            else
            {
                await observationRecorder.RecordCompletionAsync(
                    completion.Step.Id,
                    accepted: false,
                    completion.ElapsedMilliseconds,
                    completion.ExecutionAttempts,
                    "invalid-outcome-qa",
                    cancellationToken);
            }
        }
        else
        {
            await observationRecorder.RecordCompletionAsync(
            completion.Step.Id,
            accepted: true,
            completion.ElapsedMilliseconds,
            completion.ExecutionAttempts,
            "accepted-handoff",
            cancellationToken);
        }
    }

    private static void ThrowIfCompletionBlocked(HandoffGateRecord gateRecord)
    {
        if (gateRecord.Decision is
            HandoffGateDecision.BlockedKillSwitch or
            HandoffGateDecision.LoggedShadow)
        {
            throw new InvalidOperationException(gateRecord.Reason);
        }
    }

    private async Task MarkStepInterruptedAsync(
        Guid flowId,
        Guid stepId,
        long durationMilliseconds,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var step = await database.FlowSteps.SingleAsync(
            item => item.Id == stepId,
            cancellationToken);
        step.Status = StepStatus.Running;
        step.Phase = AgentRunPhase.CanceledByReconciliation;
        step.DurationMilliseconds = durationMilliseconds;
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flowId,
            FlowStepId = stepId,
            Type = "step.interrupted",
            Message =
                $"{step.AgentName} was interrupted before completion; its persisted Copilot session will be reconciled before execution continues."
        });
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkStepFailedAsync(
        Guid stepId,
        Exception exception,
        long? durationMilliseconds,
        CancellationToken cancellationToken,
        string? diagnosticOutput = null,
        AgentExecutionResult? executionResult = null)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var step = await database.FlowSteps.SingleAsync(
            item => item.Id == stepId,
            cancellationToken);
        if (executionResult is not null)
        {
            var observedToolCalls = ToObservedToolCalls(stepId, executionResult);
            if (!await database.AgentToolCalls.AnyAsync(
                    item => item.FlowStepId == stepId,
                    cancellationToken))
            {
                database.AgentToolCalls.AddRange(observedToolCalls);
            }
            var flow = await database.Flows.SingleAsync(
                item => item.Id == step.FlowRunId,
                cancellationToken);
            if (DeliveryReadinessService.AppliesTo(flow) &&
                step.PlanStage == PlanStage.BeforeReview &&
                step.InvocationKind == ExecutionInvocationKind.Worker)
            {
                await RecordDeliveryEvidenceAsync(
                    database,
                    flow,
                    step,
                    observedToolCalls,
                    cancellationToken);
            }
            step.ExecutionAttempts = Math.Max(
                step.ExecutionAttempts,
                executionResult.ExecutionAttempts);
        }
        var completedAt = DateTimeOffset.UtcNow;
        step.Status = StepStatus.Failed;
        step.Phase = exception is AgentRunException
        {
            FailureKind: AgentRunFailureKind.TimedOut
        }
            ? AgentRunPhase.TimedOut
            : exception is AgentRunException
            {
                FailureKind: AgentRunFailureKind.Stalled
            }
            ? AgentRunPhase.Stalled
            : AgentRunPhase.Failed;
        step.CompletedAt = completedAt;
        if (exception is AgentRunException failedRun)
        {
            step.ExecutionAttempts = Math.Max(
                step.ExecutionAttempts,
                failedRun.ExecutionAttempts);
        }
        if (!string.IsNullOrWhiteSpace(diagnosticOutput))
        {
            step.OutputSummary = BoundFailedOutput(diagnosticOutput);
        }
        step.PushbackReason = ClipText(
            exception.GetBaseException().Message,
            4_000);
        step.DurationMilliseconds = durationMilliseconds ?? Math.Max(
            1,
            (long)(completedAt - (step.StartedAt ?? completedAt)).TotalMilliseconds);
        var failureKind = exception is AgentRunException agentException
            ? agentException.FailureKind
            : AgentRunFailureKind.InvalidOutput;
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = step.FlowRunId,
            FlowStepId = step.Id,
            Type = failureKind == AgentRunFailureKind.ModelUnavailable
                ? "routing.model-unavailable"
                : "step.failed",
            Message = failureKind == AgentRunFailureKind.ModelUnavailable
                ? "The selected model/effort was classified unavailable. The run failed closed because the CLI result does not prove that no session or tool activity occurred; unsafe automatic rerouting was not attempted."
                : ClipText(
                    $"{step.AgentName} failed: {exception.GetBaseException().Message}",
                    4_000),
            DataJson = JsonSerializer.Serialize(new
            {
                FailureKind = failureKind.ToString(),
                Reason = step.PushbackReason,
                OutputCharacters = diagnosticOutput?.Length ?? 0,
                OutputSha256 = string.IsNullOrWhiteSpace(diagnosticOutput)
                    ? null
                    : OutcomeVerificationRules.ComputeSha256(
                        diagnosticOutput)
            })
        });
        await database.SaveChangesAsync(cancellationToken);
        await observationRecorder.RecordFailureAsync(
            stepId,
            failureKind,
            step.DurationMilliseconds,
            Math.Max(1, step.ExecutionAttempts),
            cancellationToken);
    }

    private async Task MarkContractValidationFailedAsync(
        Guid stepId,
        string reason,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var step = await database.FlowSteps.SingleAsync(
            item => item.Id == stepId,
            cancellationToken);
        ApplyContractValidationFailure(step);
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = step.FlowRunId,
            FlowStepId = step.Id,
            Type = "step.contract-invalid",
            Message = reason
        });
        await database.SaveChangesAsync(cancellationToken);
    }

    internal static void ApplyContractValidationFailure(FlowStep step)
    {
        step.Status = StepStatus.Failed;
        step.Phase = AgentRunPhase.Failed;
        step.CompletedAt ??= DateTimeOffset.UtcNow;
    }

    private async Task ReconcileDurableStateAsync(
        CancellationToken cancellationToken)
    {
        List<Guid> flowIds;
        await using (var database =
                     await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            flowIds = await database.Flows
                .AsNoTracking()
                .Where(flow =>
                    flow.Status != FlowStatus.Abandoned &&
                    flow.Status != FlowStatus.Abandoning)
                .Select(flow => flow.Id)
                .ToListAsync(cancellationToken);
        }

        foreach (var flowId in flowIds)
        {
            await using var lifecycleLease =
                await _lifecycle.EnterAsync(flowId, cancellationToken);
            try
            {
                await using var database =
                    await databaseFactory.CreateDbContextAsync(cancellationToken);
                await using var transaction =
                    await database.Database.BeginTransactionAsync(cancellationToken);
                var flow = await database.Flows
                    .AsSplitQuery()
                    .Include(item => item.Steps)
                    .Include(item => item.GateRecords)
                    .Include(item => item.Events)
                    .Include(item => item.AgentSnapshots)
                    .Include(item => item.PlanDocuments)
                    .Include(item => item.TaskProfiles)
                    .SingleAsync(item => item.Id == flowId, cancellationToken);

                if (flow.Status == FlowStatus.Blocked)
                {
                    AddRecoveryEventOnce(
                        flow,
                        null,
                        "flow.recovery-blocker-retained",
                        "Restart reconciliation retained the durable qualification blocker without scheduling a retry.");
                    await database.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    continue;
                }

                var currentStepIds = flow.Steps
                    .Where(step => step.Iteration == flow.Iteration)
                    .Select(step => step.Id)
                    .ToHashSet();
                var reviews = flow.GateRecords
                    .Where(gate =>
                        gate.ActionType == HandoffActionType.CustomerReview &&
                        currentStepIds.Contains(gate.FlowStepId))
                    .OrderBy(gate => gate.DecidedAt)
                    .ToList();
                if (reviews.Count(gate => !gate.Resolved) > 1 ||
                    reviews.Count(gate =>
                        gate.Resolved &&
                        gate.Approved == true &&
                        gate.ReviewDecision is
                            ReviewDecision.Accepted or
                            ReviewDecision.PromotedToDelivery) > 1)
                {
                    throw new InvalidOperationException(
                        "Restart reconciliation found duplicate customer-review state.");
                }

                var accepted = reviews.SingleOrDefault(gate =>
                    gate.Resolved &&
                    gate.Approved == true &&
                    gate.ReviewDecision is
                        ReviewDecision.Accepted or
                        ReviewDecision.PromotedToDelivery);
                if (flow.Kind == FlowKind.Advisory)
                {
                    if (accepted is not null &&
                        flow.Status == FlowStatus.WaitingForFeedback)
                    {
                        _lifecycle.Transition(flow, FlowStatus.Approved);
                        flow.CompletedAt ??= accepted.ResolvedAt ??
                            DateTimeOffset.UtcNow;
                        flow.OutcomeLabel =
                            accepted.ReviewDecision ==
                            ReviewDecision.PromotedToDelivery
                                ? "Advisory result accepted and promoted to Delivery"
                                : "Advisory result accepted";
                        AddRecoveryEventOnce(
                            flow,
                            accepted.FlowStepId,
                            "flow.recovery-advisory-accepted",
                            "Restart reconciliation restored the accepted Advisory as idle, with later promotion still available.");
                    }
                    else if (accepted is not null &&
                             flow.Status == FlowStatus.Approved)
                    {
                        AddRecoveryEventOnce(
                            flow,
                            accepted.FlowStepId,
                            "flow.recovery-advisory-idle",
                            "Restart reconciliation retained the accepted Advisory without scheduling execution.");
                    }
                    else if (flow.Status == FlowStatus.WaitingForFeedback &&
                             reviews.SingleOrDefault(gate => !gate.Resolved) is
                             { } openReview)
                    {
                        AddRecoveryEventOnce(
                            flow,
                            openReview.FlowStepId,
                            "flow.recovery-review-retained",
                            "Restart reconciliation retained the open customer review without scheduling execution.");
                    }

                    await database.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    continue;
                }

                if (accepted is null)
                {
                    if (flow.Status == FlowStatus.WaitingForFeedback &&
                        reviews.SingleOrDefault(gate => !gate.Resolved) is
                        { } openReview)
                    {
                        AddRecoveryEventOnce(
                            flow,
                            openReview.FlowStepId,
                            "flow.recovery-review-retained",
                            "Restart reconciliation retained the open customer review without scheduling execution.");
                    }
                    else if (flow.Status == FlowStatus.Failed)
                    {
                        AddRecoveryEventOnce(
                            flow,
                            null,
                            "flow.recovery-manual-restart-required",
                            "Restart reconciliation retained the failed attempt for an explicit manual restart.");
                    }

                    await database.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    continue;
                }

                _ = OutcomeTypeRules.RequireDelivery(
                    flow.Outcome,
                    nameof(flow.Outcome));
                var reviewedStep = flow.Steps.SingleOrDefault(step =>
                    step.Id == accepted.FlowStepId &&
                    step.Iteration == flow.Iteration &&
                    step.IsOutcomeOwner &&
                    step.Status == StepStatus.Completed &&
                    string.Equals(
                        step.PlanStepKey,
                        flow.OutcomeOwnerPlanStepKey,
                        StringComparison.Ordinal))
                    ?? throw new InvalidOperationException(
                        "The accepted Delivery review is not bound to its completed outcome owner.");
                var publicationAttempts = flow.Steps
                    .Where(step =>
                        step.Iteration == flow.Iteration &&
                        ReviewCoordinator.IsPublicationStep(flow, step))
                    .OrderBy(step => step.Sequence)
                    .ThenBy(step => step.Attempt)
                    .ToList();
                var semanticRoots = publicationAttempts
                    .Select(step =>
                        step.StableSemanticRootId ??
                        step.RetryOfStepId ??
                        step.Id)
                    .Distinct()
                    .ToList();
                if (semanticRoots.Count > 1)
                {
                    throw new InvalidOperationException(
                        "Restart reconciliation found duplicate Delivery publication semantic roots.");
                }

                if (publicationAttempts.Count == 0)
                {
                    var workflow = workflowProvider.GetEffective();
                    var publication = ReviewCoordinator.MaterializePublicationStep(
                        flow,
                        reviewedStep,
                        workflow,
                        _permissionResolver);
                    publicationAttempts.Add(publication);
                    AddRecoveryEventOnce(
                        flow,
                        publication.Id,
                        "flow.recovery-publication-materialized",
                        "Restart reconciliation materialized the sole planned publication from durable customer acceptance.");
                }

                var latestPublication = publicationAttempts.LastOrDefault();
                if (latestPublication is not null &&
                    latestPublication.Status is
                        StepStatus.Pending or StepStatus.Running)
                {
                    var document = flow.PlanDocuments.Single(item =>
                        item.Iteration == flow.Iteration);
                    var planned = TeamPlanParser.ParseJson(document.RawJson)
                        .Document.Steps!
                        .Single(step =>
                            string.Equals(
                                step.Id,
                                flow.PublicationPlanStepKey,
                                StringComparison.Ordinal));
                    var workflow = workflowProvider.GetEffective();
                    if (ReviewCoordinator.EnsurePublicationPermission(
                            flow,
                            reviewedStep,
                            planned,
                            latestPublication,
                            workflow,
                            _permissionResolver))
                    {
                        AddRecoveryEventOnce(
                            flow,
                            latestPublication.Id,
                            "step.permission-policy-tightened",
                            "Restart reconciliation retained the original publication ceiling and incorporated only stricter current WORKFLOW.md restrictions.");
                    }
                }
                if (latestPublication?.Status == StepStatus.Completed)
                {
                    if (flow.Status != FlowStatus.Approved)
                    {
                        // Crash-window recovery must reauthorize from the durable readiness,
                        // review, and publication-journal binding instead of transitioning a
                        // Delivery flow to Approved directly.
                        var authorized = await TryCompleteRecoveredDeliveryAsync(
                            database,
                            flow,
                            accepted,
                            latestPublication,
                            cancellationToken);
                        if (!authorized)
                        {
                            await database.SaveChangesAsync(cancellationToken);
                            await transaction.CommitAsync(cancellationToken);
                            continue;
                        }
                        flow.CompletedAt ??=
                            latestPublication.CompletedAt ??
                            DateTimeOffset.UtcNow;
                    }
                    AddRecoveryEventOnce(
                        flow,
                        latestPublication.Id,
                        "flow.recovery-publication-completed",
                        "Restart reconciliation retained the verified customer-approved publication.");
                }
                else if (latestPublication?.Status == StepStatus.Failed)
                {
                    if (flow.Status != FlowStatus.Failed)
                    {
                        _lifecycle.Transition(flow, FlowStatus.Failed);
                        flow.FailureReason =
                            "The customer-approved publication failed and requires manual restart.";
                    }
                    AddRecoveryEventOnce(
                        flow,
                        latestPublication.Id,
                        "flow.recovery-publication-restart-required",
                        "Durable customer acceptance was retained; failed publication remains manual-restartable.");
                }
                else if (latestPublication is not null)
                {
                    if (flow.Status == FlowStatus.WaitingForFeedback)
                    {
                        _lifecycle.Transition(flow, FlowStatus.Queued);
                    }
                    else if (flow.Status is
                             FlowStatus.Running or FlowStatus.Reworking)
                    {
                        _lifecycle.RequeueAfterRecovery(flow);
                    }
                    else if (flow.Status == FlowStatus.Failed)
                    {
                        _lifecycle.Transition(flow, FlowStatus.Queued);
                        flow.FailureReason = string.Empty;
                        flow.CompletedAt = null;
                    }
                    else if (flow.Status != FlowStatus.Queued)
                    {
                        throw new FlowLifecycleException(
                            flow.Id,
                            flow.Status,
                            FlowStatus.Queued,
                            "accepted Delivery publication can resume only from a recoverable active state");
                    }
                    AddRecoveryEventOnce(
                        flow,
                        latestPublication.Id,
                        "flow.recovery-publication-queued",
                        "Restart reconciliation queued the single customer-approved publication attempt.");
                }

                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Could not reconcile durable flow state for flow {FlowId}.",
                    flowId);
                await using var failureDatabase =
                    await databaseFactory.CreateDbContextAsync(
                        CancellationToken.None);
                var failed = await failureDatabase.Flows
                    .Include(item => item.Events)
                    .SingleAsync(item => item.Id == flowId);
                if (failed.Status is
                    FlowStatus.Queued or
                    FlowStatus.Running or
                    FlowStatus.Reworking or
                    FlowStatus.WaitingForFeedback)
                {
                    _lifecycle.Transition(failed, FlowStatus.Failed);
                    failed.FailureReason =
                        "Restart reconciliation failed: " +
                        exception.GetBaseException().Message;
                }
                AddRecoveryEventOnce(
                    failed,
                    null,
                    "flow.recovery-failed",
                    "Restart reconciliation failed and requires operator attention.",
                    JsonSerializer.Serialize(new
                    {
                        Error = ClipText(
                            exception.GetBaseException().Message,
                            4_000)
                    }));
                await failureDatabase.SaveChangesAsync(CancellationToken.None);
            }
        }
    }

    private static void AddRecoveryEventOnce(
        FlowRun flow,
        Guid? stepId,
        string type,
        string message,
        string? dataJson = null)
    {
        if (flow.Events.Any(item =>
                item.FlowStepId == stepId &&
                string.Equals(item.Type, type, StringComparison.Ordinal)))
        {
            return;
        }
        flow.Events.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = stepId,
            Type = type,
            Message = message,
            DataJson = dataJson
        });
    }

    private static IQueryable<InterruptedStepCandidate> QueryInterruptedSteps(
        HarnessDbContext database,
        Guid? flowId = null) =>
        from step in database.FlowSteps.AsNoTracking()
        join flow in database.Flows.AsNoTracking()
            on step.FlowRunId equals flow.Id
        where (!flowId.HasValue || step.FlowRunId == flowId.Value) &&
              step.Status == StepStatus.Running &&
              (flow.Status == FlowStatus.Queued ||
               flow.Status == FlowStatus.Running ||
               flow.Status == FlowStatus.Reworking)
        select new InterruptedStepCandidate(
            step.Id,
            step.FlowRunId,
            step.Iteration,
            step.AgentId,
            step.AgentName,
            step.AgentRole,
            step.PlanStepKey,
            flow.WorkspacePath,
            step.StartedAt,
            step.CopilotSessionId,
            step.CopilotSessionHome,
            step.PreMortemReviewStepId != null,
            step.ExecutionPrompt,
            step.WorkflowRevision,
            step.InvocationKind,
            step.IsOutcomeOwner,
            flow.Kind);

    internal async Task<IReadOnlyList<Guid>> RecoverInterruptedFlowsAsync(
        CancellationToken cancellationToken)
    {
        List<InterruptedStepCandidate> interruptedSteps;
        List<InterruptedStepCandidate> failedStalledSteps;
        await using (var database = await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            interruptedSteps = await QueryInterruptedSteps(database)
                .ToListAsync(cancellationToken);
            failedStalledSteps = await (
                    from step in database.FlowSteps.AsNoTracking()
                    join flow in database.Flows.AsNoTracking()
                        on step.FlowRunId equals flow.Id
                    where step.Status == StepStatus.Failed &&
                          (step.Phase == AgentRunPhase.Stalled ||
                           step.Phase == AgentRunPhase.TimedOut) &&
                          flow.Status == FlowStatus.Failed &&
                          !database.FlowSteps.Any(retry =>
                              retry.FlowRunId == step.FlowRunId &&
                              retry.Iteration == step.Iteration &&
                              retry.AgentId == step.AgentId &&
                              retry.Sequence > step.Sequence &&
                              retry.StableSemanticRootId ==
                              (step.StableSemanticRootId ??
                               step.RetryOfStepId ??
                               step.Id) &&
                              retry.Status == StepStatus.Completed &&
                              ((step.InvocationKind !=
                                    ExecutionInvocationKind.PreMortem &&
                                step.AgentId != PreMortemRole) ||
                               retry.PreMortemOriginStepId ==
                               step.PreMortemOriginStepId))
                    select new InterruptedStepCandidate(
                        step.Id,
                        step.FlowRunId,
                        step.Iteration,
                        step.AgentId,
                        step.AgentName,
                        step.AgentRole,
                        step.PlanStepKey,
                        flow.WorkspacePath,
                        step.StartedAt,
                        step.CopilotSessionId,
                        step.CopilotSessionHome,
                        step.PreMortemReviewStepId != null,
                        step.ExecutionPrompt,
                        step.WorkflowRevision,
                        step.InvocationKind,
                        step.IsOutcomeOwner,
                        flow.Kind))
                .ToListAsync(cancellationToken);
        }

        foreach (var candidate in interruptedSteps)
        {
            try
            {
                await RecoverInterruptedStepAsync(candidate, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Could not reconcile interrupted step {StepId} in flow {FlowId}.",
                    candidate.StepId,
                    candidate.FlowId);
                await AddEventAsync(
                    candidate.FlowId,
                    candidate.StepId,
                    "step.recovery-failed",
                    $"Interrupted Copilot session recovery failed: {exception.Message}",
                    CancellationToken.None);
                await MarkStepFailedAsync(
                    candidate.StepId,
                    exception,
                    durationMilliseconds: null,
                    CancellationToken.None,
                    (exception as CompletedJournalContractException)
                    ?.Output);
                await MarkFailedAsync(
                    candidate.FlowId,
                    exception.Message,
                    CancellationToken.None);
            }
        }

        foreach (var candidate in failedStalledSteps
                     .GroupBy(item => item.FlowId)
                     .Select(group => group
                         .OrderByDescending(item => item.StartedAt)
                         .First()))
        {
            try
            {
                await TryRecoverCompletedFailedStepAsync(candidate, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Could not recover completed output for failed step {StepId} in flow {FlowId}.",
                    candidate.StepId,
                    candidate.FlowId);
                await AddEventAsync(
                    candidate.FlowId,
                    candidate.StepId,
                    "step.completed-output-recovery-failed",
                    $"Completed-output recovery failed: {exception.Message}",
                    CancellationToken.None);
            }
        }

        if (_linkedFlows is not null)
        {
            try
            {
                _ = await _linkedFlows
                    .RecoverUnstartedIntakesAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Could not complete startup intake reconciliation; continuing with other recovery work.");
            }
        }
        await ReconcileDurableStateAsync(cancellationToken);

        await using var flowDatabase = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var interruptedFlows = await flowDatabase.Flows
            .Where(flow =>
                flow.Status == FlowStatus.Queued ||
                flow.Status == FlowStatus.Running ||
                flow.Status == FlowStatus.Reworking)
            .ToListAsync(cancellationToken);
        foreach (var flow in interruptedFlows)
        {
            if (_lifecycle.RequeueAfterRecovery(flow))
            {
                flowDatabase.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    Type = "flow.recovery-queued",
                    Message =
                        "Restart reconciliation returned interrupted flow execution to the queue."
                });
            }
        }
        await flowDatabase.SaveChangesAsync(cancellationToken);

        var resumedFlowIds = interruptedFlows
            .Select(flow => flow.Id)
            .ToHashSet();
        await CleanupConclusiveStagedSessionsAsync(cancellationToken);

        logger.LogInformation(
            "Reconciled {StepCount} interrupted step(s) across {FlowCount} flow(s).",
            interruptedSteps.Count + failedStalledSteps.Count,
            resumedFlowIds.Count);
        return resumedFlowIds.ToList();
    }

    internal async Task RecoverActiveFlowAsync(
        Guid flowId,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "Operator requested recovery for active flow {FlowId}.",
            flowId);
        await _manualRestartGate.WaitAsync(cancellationToken);
        try
        {
            List<InterruptedStepCandidate> interruptedSteps;
            await using (var database =
                         await databaseFactory.CreateDbContextAsync(cancellationToken))
            {
                interruptedSteps = await QueryInterruptedSteps(database, flowId)
                    .ToListAsync(cancellationToken);
            }

            foreach (var candidate in interruptedSteps)
            {
                try
                {
                    await RecoverInterruptedStepAsync(candidate, cancellationToken);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "Manual recovery could not reconcile interrupted step {StepId} in flow {FlowId}.",
                        candidate.StepId,
                        candidate.FlowId);
                    await AddEventAsync(
                        candidate.FlowId,
                        candidate.StepId,
                        "step.recovery-failed",
                        $"Manual interrupted-session recovery failed: {exception.Message}",
                        CancellationToken.None);
                    await MarkStepFailedAsync(
                        candidate.StepId,
                        exception,
                        durationMilliseconds: null,
                        CancellationToken.None,
                        (exception as CompletedJournalContractException)?.Output);
                    await MarkFailedAsync(
                        candidate.FlowId,
                        exception.Message,
                        CancellationToken.None);
                    throw new InvalidOperationException(
                        "The interrupted execution could not be recovered and the flow was moved to Failed.",
                        exception);
                }
            }

            await using var lifecycleLease =
                await _lifecycle.EnterAsync(flowId, cancellationToken);
            await using var flowDatabase =
                await databaseFactory.CreateDbContextAsync(cancellationToken);
            var flow = await flowDatabase.Flows
                           .Include(item => item.Events)
                           .SingleOrDefaultAsync(
                               item => item.Id == flowId,
                               cancellationToken)
                       ?? throw new KeyNotFoundException(
                           $"Factory flow '{flowId}' was not found.");
            if (flow.Status is not (
                    FlowStatus.Queued or
                    FlowStatus.Running or
                    FlowStatus.Reworking))
            {
                throw new FlowLifecycleException(
                    flow.Id,
                    flow.Status,
                    FlowStatus.Queued,
                    "manual recovery applies only to queued, running, or reworking flows");
            }

            _lifecycle.RequeueAfterRecovery(flow);
            flow.FailureReason = string.Empty;
            flow.CompletedAt = null;
            flow.UpdatedAt = DateTimeOffset.UtcNow;
            flowDatabase.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                Type = "flow.manual-recovery-queued",
                Message = interruptedSteps.Count == 0
                    ? "Operator recovery re-queued the persisted flow after execution stopped progressing."
                    : "Operator recovery reconciled the interrupted Copilot session and returned the flow to the queue."
            });
            await flowDatabase.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Recovered active flow {FlowId}; reconciled {InterruptedStepCount} interrupted step(s) and queued durable continuation.",
                flowId,
                interruptedSteps.Count);
        }
        finally
        {
            _manualRestartGate.Release();
        }
    }

    private async Task<bool> TryRecoverCompletedFailedStepAsync(
        InterruptedStepCandidate candidate,
        CancellationToken cancellationToken)
    {
        var copilotHome = string.IsNullOrWhiteSpace(candidate.CopilotSessionHome)
            ? sessionJournal.ExpectedHome()
            : candidate.CopilotSessionHome;
        var snapshot = candidate.CopilotSessionId is { } sessionId
            ? await sessionJournal.InspectAsync(
                copilotHome,
                sessionId,
                cancellationToken)
            : null;
        if (!candidate.IsPreMortemRevision &&
            (snapshot is null ||
             snapshot.State == CopilotSessionJournalState.Missing))
        {
            var deterministicSessionId = AgentSessionIdentity.Create(
                candidate.FlowId,
                candidate.Iteration,
                candidate.AgentId,
                candidate.PlanStepKey);
            snapshot = await sessionJournal.InspectAsync(
                copilotHome,
                deterministicSessionId,
                cancellationToken);
        }
        if (snapshot is not
            {
                State: CopilotSessionJournalState.Completed,
                Result: { Success: true } recoveredResult
            } ||
            !CopilotReasoningHost.IsRecoverableCompletedOutput(
                  candidate.AgentRole,
                  recoveredResult.OutputSummary,
                  candidate.IsPreMortemRevision,
                  invocationKind: candidate.InvocationKind,
                  isOutcomeOwner: candidate.IsOutcomeOwner,
                  planStepKey: candidate.PlanStepKey,
                  expectedFlowKind: candidate.FlowKind) ||
            !CopilotReasoningHost.IsRecoveryCurrent(
                candidate.StartedAt,
                snapshot.CompletedAt))
        {
            return false;
        }

        var recoveredExecution = ToRecoveredExecutionResult(
            recoveredResult,
            snapshot.SessionId);
        if (await TryScheduleRecoveredQaCorrectionAsync(
                candidate.FlowId,
                candidate.StepId,
                recoveredExecution,
                snapshot.CompletedAt ?? DateTimeOffset.UtcNow,
                recoveringFailedAttempt: true,
                cancellationToken))
        {
            return true;
        }
        var governedPublicationOutput = await PrepareGovernedPublicationIfNeededAsync(
            candidate.FlowId,
            candidate.StepId,
            recoveredExecution,
            cancellationToken);
        StagedStepCompletion completion;
        await using (var database =
                     await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction =
                await database.Database.BeginTransactionAsync(cancellationToken);
            var flow = await database.Flows
                .Include(item => item.Steps)
                .SingleAsync(item => item.Id == candidate.FlowId, cancellationToken);
            var failedStep = flow.Steps.Single(item => item.Id == candidate.StepId);
            if (flow.Status != FlowStatus.Failed ||
                failedStep.Status != StepStatus.Failed)
            {
                return false;
            }

            completion = await StageCompletedStepAsync(
                database,
                flow.Id,
                failedStep.Id,
                recoveredExecution,
                snapshot.CompletedAt ?? DateTimeOffset.UtcNow,
                durationMilliseconds: null,
                snapshot.SessionId,
                governedPublicationOutput,
                cancellationToken);
            ThrowIfCompletionBlocked(completion.GateRecord);
            RetargetSupersededRetryLinks(flow.Steps, failedStep);
            foreach (var laterStep in flow.Steps.Where(
                         step =>
                             step.Iteration == flow.Iteration &&
                             step.Sequence > failedStep.Sequence &&
                             step.Status == StepStatus.Skipped &&
                             !IsSupersededRetry(step, failedStep)))
            {
                ResetSkippedStep(laterStep);
            }
            failedStep.CopilotSessionId = snapshot.SessionId;
            failedStep.CopilotSessionHome = snapshot.CopilotHome;
            _lifecycle.Transition(flow, FlowStatus.Queued);
            flow.FailureReason = string.Empty;
            flow.CompletedAt = null;
            flow.UpdatedAt = DateTimeOffset.UtcNow;
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = failedStep.Id,
                Type = "flow.completed-output-auto-recovered",
                Message =
                    $"Recovered {failedStep.AgentName}'s completed handoff from Copilot session " +
                    $"{snapshot.SessionId:D}; downstream execution will continue automatically."
            });
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await RecordCompletionObservationAsync(completion, cancellationToken);
        CleanupRecoveredSessionRoot(snapshot);
        return true;
    }

    internal async Task<FlowRun> RestartFailedFlowAsync(
        Guid flowId,
        CancellationToken cancellationToken)
    {
        await _manualRestartGate.WaitAsync(cancellationToken);
        try
        {
            await using var lifecycleLease =
                await _lifecycle.EnterAsync(flowId, cancellationToken);
            await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
            var flow = await database.Flows
                           .AsSplitQuery()
                           .Include(item => item.Steps)
                           .ThenInclude(step => step.ToolCalls)
                           .Include(item => item.Messages)
                           .Include(item => item.Events)
                           .Include(item => item.GateRecords)
                           .Include(item => item.AgentSnapshots)
                           .SingleOrDefaultAsync(item => item.Id == flowId, cancellationToken)
                       ?? throw new KeyNotFoundException($"Factory flow '{flowId}' was not found.");
            if (flow.Status != FlowStatus.Failed)
            {
                throw new InvalidOperationException("Only a failed flow can be restarted.");
            }
            var iterationSteps = flow.Steps
                .Where(step => step.Iteration == flow.Iteration)
                .ToList();
            var failedStep =
                FindUnresolvedFailure(iterationSteps) ??
                FindUnresolvedPushback(iterationSteps);
            var finalizationStep = FindRetryableStudioFinalizationStep(flow);
            if (finalizationStep is not null &&
                (failedStep is null || failedStep.Id == finalizationStep.Id))
            {
                finalizationStep.Status = StepStatus.Completed;
                finalizationStep.Phase = AgentRunPhase.Succeeded;
                finalizationStep.PushbackReason = string.Empty;
                finalizationStep.CompletedAt ??= DateTimeOffset.UtcNow;
                var finalizationFailureReason = flow.FailureReason;
                _lifecycle.Transition(flow, FlowStatus.Queued);
                flow.FailureReason = string.Empty;
                flow.CompletedAt = null;
                flow.OutcomeUrl = string.Empty;
                flow.OutcomeLabel = string.Empty;
                flow.UpdatedAt = DateTimeOffset.UtcNow;
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = finalizationStep.Id,
                    Type = "flow.finalization-retry-queued",
                    Message =
                        $"Manual restart queued host finalization for the already-accepted outcome: {finalizationFailureReason}"
                });
                await database.SaveChangesAsync(cancellationToken);
                return flow;
            }
            if (failedStep is null)
            {
                throw new InvalidOperationException(
                    "The failed flow has no unresolved agent step to restart.");
            }
            if (flow.AgentSnapshots.Count > 0 &&
                !flow.AgentSnapshots.Any(snapshot =>
                    string.Equals(
                        snapshot.AgentId,
                        failedStep.AgentId,
                        StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"The flow cannot be restarted because its agent snapshot has no definition for '{failedStep.AgentId}'.");
            }
            var copilotHome = string.IsNullOrWhiteSpace(failedStep.CopilotSessionHome)
                ? sessionJournal.ExpectedHome()
                : failedStep.CopilotSessionHome;
            var snapshot = failedStep.CopilotSessionId is { } sessionId
                ? await sessionJournal.InspectAsync(
                    copilotHome,
                    sessionId,
                    cancellationToken)
                : null;
            var discovered = false;
            var canHaveRecoverableJournal =
                failedStep.Phase is
                    AgentRunPhase.Stalled or
                    AgentRunPhase.TimedOut or
                    AgentRunPhase.CanceledByReconciliation;
            var isVerification = IsDeliveryVerificationStep(failedStep);
            if (failedStep.PreMortemReviewStepId is null &&
                (snapshot is null ||
                 snapshot.State == CopilotSessionJournalState.Missing) &&
                canHaveRecoverableJournal)
            {
                var deterministicSessionId = AgentSessionIdentity.Create(
                    flow.Id,
                    failedStep.Iteration,
                    failedStep.AgentId,
                    failedStep.PlanStepKey);
                snapshot = await sessionJournal.InspectAsync(
                    copilotHome,
                    deterministicSessionId,
                    cancellationToken);
                discovered =
                    snapshot.State != CopilotSessionJournalState.Missing;
            }
            else if (!isVerification &&
                (snapshot is null || snapshot.State == CopilotSessionJournalState.Missing) &&
                canHaveRecoverableJournal)
            {
                snapshot = await sessionJournal.DiscoverLatestAsync(
                    copilotHome,
                    flow.WorkspacePath,
                    failedStep.AgentName,
                    failedStep.StartedAt,
                    cancellationToken);
                discovered = snapshot is not null;
            }

            var stoppedActiveSession = false;
            if (snapshot?.State == CopilotSessionJournalState.Active)
            {
                stoppedActiveSession = sessionJournal.TryStopActiveSession(snapshot);
                if (!stoppedActiveSession)
                {
                    throw new InvalidOperationException(
                        $"Copilot session {snapshot.SessionId:D} is still active and could not be stopped safely.");
                }
            }

            if (isVerification &&
                snapshot is
                {
                    State: CopilotSessionJournalState.Completed,
                    Result: { Success: true } completedVerification
                } &&
                CopilotReasoningHost.IsRecoveryCurrent(
                    failedStep.StartedAt,
                    snapshot.CompletedAt) &&
                await TryScheduleRecoveredQaCorrectionAsync(
                    flow.Id,
                    failedStep.Id,
                    ToRecoveredExecutionResult(
                        completedVerification,
                        snapshot.SessionId),
                    snapshot.CompletedAt ?? DateTimeOffset.UtcNow,
                    recoveringFailedAttempt: true,
                    cancellationToken))
            {
                database.ChangeTracker.Clear();
                return await database.Flows
                    .AsSplitQuery()
                    .Include(item => item.Steps)
                    .ThenInclude(step => step.ToolCalls)
                    .Include(item => item.Messages)
                    .Include(item => item.Events)
                    .Include(item => item.GateRecords)
                    .Include(item => item.AgentSnapshots)
                    .SingleAsync(item => item.Id == flowId, cancellationToken);
            }

            if (snapshot is
                {
                    State: CopilotSessionJournalState.Completed,
                    Result: { Success: true } recoveredResult
                } &&
                failedStep.Status == StepStatus.Failed &&
                (IsPreMortemStep(failedStep) ||
                 failedStep.PreMortemReviewStepId is not null ||
                 failedStep.Phase is AgentRunPhase.Stalled or AgentRunPhase.TimedOut) &&
                (isVerification ||
                 CopilotReasoningHost.IsRecoverableCompletedOutput(
                     failedStep.AgentRole,
                     recoveredResult.OutputSummary,
                     failedStep.PreMortemReviewStepId is not null,
                     invocationKind: failedStep.InvocationKind,
                     isOutcomeOwner: failedStep.IsOutcomeOwner,
                     planStepKey: failedStep.PlanStepKey,
                     expectedFlowKind: flow.Kind)) &&
                CopilotReasoningHost.IsRecoveryCurrent(
                    failedStep.StartedAt,
                    snapshot.CompletedAt))
            {
                var recoveredExecution = ToRecoveredExecutionResult(
                    recoveredResult,
                    snapshot.SessionId);
                var governedPublicationOutput =
                    await PrepareGovernedPublicationIfNeededAsync(
                        flow.Id,
                        failedStep.Id,
                        recoveredExecution,
                        cancellationToken);
                await using var transaction =
                    await database.Database.BeginTransactionAsync(cancellationToken);
                var completion = await StageCompletedStepAsync(
                    database,
                    flow.Id,
                    failedStep.Id,
                    recoveredExecution,
                    snapshot.CompletedAt ?? DateTimeOffset.UtcNow,
                    durationMilliseconds: null,
                    snapshot.SessionId,
                    governedPublicationOutput,
                    cancellationToken);
                ThrowIfCompletionBlocked(completion.GateRecord);
                RetargetSupersededRetryLinks(flow.Steps, failedStep);
                foreach (var laterStep in flow.Steps.Where(
                             step =>
                                 step.Iteration == flow.Iteration &&
                                 step.Sequence > failedStep.Sequence &&
                                 step.Status == StepStatus.Skipped &&
                                 !IsSupersededRetry(step, failedStep)))
                {
                    ResetSkippedStep(laterStep);
                }

                var recoveredFailureReason = flow.FailureReason;
                failedStep.CopilotSessionId = snapshot.SessionId;
                failedStep.CopilotSessionHome = snapshot.CopilotHome;
                _lifecycle.Transition(flow, FlowStatus.Queued);
                flow.FailureReason = string.Empty;
                flow.CompletedAt = null;
                flow.UpdatedAt = DateTimeOffset.UtcNow;
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = failedStep.Id,
                    Type = "flow.completed-output-recovered",
                    Message =
                        $"Recovered {failedStep.AgentName}'s completed handoff from Copilot session " +
                        $"{snapshot.SessionId:D} after the runtime stopped: {recoveredFailureReason}"
                });
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                await RecordCompletionObservationAsync(completion, cancellationToken);
                CleanupRecoveredSessionRoot(snapshot);
                return flow;
            }

            var canResume = snapshot?.State is
                CopilotSessionJournalState.Interrupted or
                CopilotSessionJournalState.Active;
            var priorAssignment = failedStep.InputSummary.Trim();
            if (string.IsNullOrWhiteSpace(priorAssignment) ||
                priorAssignment.StartsWith(
                    "Manual restart after the prior attempt failed:",
                    StringComparison.Ordinal))
            {
                priorAssignment =
                    "Complete the role-specific assignment and produce the required handoff.";
            }
            if (!canResume)
            {
                priorAssignment +=
                    $"{Environment.NewLine}{Environment.NewLine}" +
                    "Host recovery context: inspect the preserved workspace state before " +
                    "continuing. This retry retains the step's persisted permission ceiling.";
            }
            var retryStep = FindReusableCausalRetry(
                failedStep,
                flow.Steps);
            if (retryStep is null)
            {
                foreach (var laterStep in flow.Steps.Where(
                             step =>
                                 step.Iteration == flow.Iteration &&
                                 step.Sequence > failedStep.Sequence))
                {
                    laterStep.Sequence += 10;
                    if (laterStep.Status == StepStatus.Skipped)
                    {
                        ResetSkippedStep(laterStep);
                    }
                }

                retryStep = new FlowStep
                {
                    FlowRunId = flow.Id,
                    Iteration = flow.Iteration,
                    Sequence = failedStep.Sequence + 10,
                    AgentId = failedStep.AgentId,
                    AgentName = failedStep.AgentName,
                    AgentRole = failedStep.AgentRole,
                    Label = $"{ManualRestartLabelPrefix}{failedStep.AgentName}",
                    PlanStepKey = failedStep.PlanStepKey,
                    PlanDutiesJson = failedStep.PlanDutiesJson,
                    PlanStage = failedStep.PlanStage,
                    InvocationKind = failedStep.InvocationKind,
                    IsOutcomeOwner = failedStep.IsOutcomeOwner,
                    PermissionProfile = failedStep.PermissionProfile,
                    EffectivePermissionJson =
                        failedStep.EffectivePermissionJson,
                    WorkflowRevision = failedStep.WorkflowRevision,
                    Status = StepStatus.Pending,
                    Phase = canResume
                        ? AgentRunPhase.CanceledByReconciliation
                        : AgentRunPhase.PreparingWorkspace,
                    Attempt = IsPreMortemStep(failedStep)
                        ? failedStep.Attempt
                        : flow.Steps
                            .Where(step =>
                                step.Iteration == flow.Iteration &&
                                step.AgentId == failedStep.AgentId)
                            .Select(step => step.Attempt)
                            .DefaultIfEmpty()
                            .Max() + 1,
                    InputSummary = priorAssignment,
                    CopilotSessionId = canResume ? snapshot!.SessionId : null,
                    CopilotSessionHome = canResume
                        ? snapshot!.CopilotHome
                        : string.Empty,
                    RemotePublicationAllowed = failedStep.RemotePublicationAllowed,
                    RetryOfStepId = GetRetryRootId(failedStep),
                    DependsOnStepId = failedStep.DependsOnStepId,
                    PushbackRootStepId = failedStep.PushbackRootStepId,
                    StableSemanticRootId = GetStableSemanticRootId(failedStep),
                    PreMortemOriginStepId = failedStep.PreMortemOriginStepId,
                    PreMortemTargetStepId = failedStep.PreMortemTargetStepId,
                    PreMortemReviewStepId = failedStep.PreMortemReviewStepId
                };
                flow.Steps.Add(retryStep);
                database.Entry(retryStep).State = EntityState.Added;
            }
            else
            {
                foreach (var laterStep in flow.Steps.Where(
                             step =>
                                 step.Iteration == flow.Iteration &&
                                 step.Sequence > failedStep.Sequence &&
                                 step.Id != retryStep.Id &&
                                 step.Status == StepStatus.Skipped))
                {
                    ResetSkippedStep(laterStep);
                }
                ResetSkippedStep(retryStep);
                retryStep.Phase = canResume
                    ? AgentRunPhase.CanceledByReconciliation
                    : AgentRunPhase.PreparingWorkspace;
                retryStep.PlanStepKey = failedStep.PlanStepKey;
                retryStep.PlanDutiesJson = failedStep.PlanDutiesJson;
                retryStep.PlanStage = failedStep.PlanStage;
                retryStep.InvocationKind = failedStep.InvocationKind;
                retryStep.IsOutcomeOwner = failedStep.IsOutcomeOwner;
                retryStep.StableSemanticRootId = GetStableSemanticRootId(failedStep);
                if (retryStep.DependsOnStepId is null)
                {
                    retryStep.InputSummary = priorAssignment;
                }
                retryStep.CopilotSessionId = canResume
                    ? snapshot!.SessionId
                    : null;
                retryStep.CopilotSessionHome = canResume
                    ? snapshot!.CopilotHome
                    : string.Empty;
            }
            if (!await database.TaskProfiles.AnyAsync(
                    profile => profile.FlowStepId == retryStep.Id,
                    cancellationToken))
            {
                var sourceProfile = await database.TaskProfiles
                                        .AsNoTracking()
                                        .Where(profile =>
                                            profile.FlowRunId == flow.Id &&
                                            profile.Iteration == flow.Iteration &&
                                            profile.PlanStepKey ==
                                            failedStep.PlanStepKey &&
                                            profile.AgentId == failedStep.AgentId)
                                        .OrderByDescending(profile =>
                                            profile.FlowStepId == failedStep.Id)
                                        .ThenByDescending(profile =>
                                            profile.CreatedAt)
                                        .FirstOrDefaultAsync(cancellationToken)
                                    ?? throw new InvalidOperationException(
                                        $"No durable task profile exists for failed plan step '{failedStep.PlanStepKey}'.");
                database.TaskProfiles.Add(
                    TaskProfileRules.CopyForStep(sourceProfile, retryStep.Id));
            }
            foreach (var dependent in flow.Steps.Where(step =>
                         step.Status == StepStatus.Pending &&
                         step.DependsOnStepId == failedStep.Id &&
                         step.Id != retryStep.Id))
            {
                dependent.DependsOnStepId = retryStep.Id;
            }
            foreach (var review in flow.Steps.Where(step =>
                         IsPreMortemStep(step) &&
                         step.PreMortemTargetStepId == failedStep.Id &&
                         step.Status is StepStatus.Pending or StepStatus.Skipped))
            {
                review.PreMortemTargetStepId = retryStep.Id;
            }
            var permissionTightened =
                PreserveOrTightenRetryPermission(
                    flow,
                    failedStep,
                    retryStep);
            if (ReviewCoordinator.IsPublicationStep(
                    flow,
                    retryStep))
            {
                _ = ReviewCoordinator.RequireRemotePublicationAuthority(
                    flow,
                    retryStep);
            }

            var failureReason = flow.FailureReason;
            _lifecycle.Transition(flow, FlowStatus.Queued);
            flow.FailureReason = string.Empty;
            flow.CompletedAt = null;
            flow.OutcomeUrl = string.Empty;
            flow.OutcomeLabel = string.Empty;
            flow.UpdatedAt = DateTimeOffset.UtcNow;
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = failedStep.Id,
                Type = "flow.manual-restart",
                Message =
                    $"Manual restart requested after {failedStep.AgentName} failed: {failureReason}"
            });
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = retryStep.Id,
                Type = "step.manual-retry-scheduled",
                Message = canResume
                    ? $"{failedStep.AgentName} will resume Copilot session {snapshot!.SessionId:D}."
                    : $"{failedStep.AgentName} will continue in a new Copilot session using the preserved workspace."
            });
            if (permissionTightened)
            {
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = retryStep.Id,
                    Type = "step.permission-policy-tightened",
                    Message =
                        "The retry retained its original permission ceiling and incorporated only stricter current WORKFLOW.md restrictions."
                });
            }
            if (discovered)
            {
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = retryStep.Id,
                    Type = "agent.session-discovered",
                    Message =
                        $"Found Copilot session {snapshot!.SessionId:D} for the failed attempt."
                });
            }
            if (stoppedActiveSession)
            {
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = retryStep.Id,
                    Type = "agent.orphan-stopped",
                    Message =
                        $"Stopped Copilot session {snapshot!.SessionId:D} before its manual restart."
                });
            }

            await database.SaveChangesAsync(cancellationToken);
            return flow;
        }
        finally
        {
            _manualRestartGate.Release();
        }
    }

    internal static bool CanRetryStudioFinalization(FlowRun flow)
        => FindRetryableStudioFinalizationStep(flow) is not null;

    private static FlowStep? FindRetryableStudioFinalizationStep(FlowRun flow)
    {
        if (flow.Status != FlowStatus.Failed ||
            string.IsNullOrWhiteSpace(flow.OutcomeOwnerPlanStepKey) ||
            string.IsNullOrWhiteSpace(flow.OutcomeContractJson) ||
            flow.GateRecords.Any(gate =>
                gate.ActionType == HandoffActionType.CustomerReview &&
                gate.Resolved &&
                gate.ReviewDecision == ReviewDecision.Accepted))
        {
            return null;
        }

        var acceptedStepIds = flow.Events
            .Where(flowEvent =>
                flowEvent.Type == "outcome.contract-accepted" &&
                flowEvent.FlowStepId is not null)
            .Select(flowEvent => flowEvent.FlowStepId!.Value)
            .ToHashSet();
        return flow.Steps
            .Where(step =>
                step.Iteration == flow.Iteration &&
                step.IsOutcomeOwner &&
                acceptedStepIds.Contains(step.Id) &&
                !string.IsNullOrWhiteSpace(step.OutputSummary) &&
                string.Equals(
                    step.PlanStepKey,
                    flow.OutcomeOwnerPlanStepKey,
                    StringComparison.Ordinal))
            .OrderByDescending(step => step.Sequence)
            .ThenByDescending(step => step.Attempt)
            .FirstOrDefault();
    }

    private static void ResetSkippedStep(FlowStep step)
    {
        step.Status = StepStatus.Pending;
        step.Phase = AgentRunPhase.PreparingWorkspace;
        step.Model = string.Empty;
        step.ModelReason = string.Empty;
        step.ExecutionAttempts = 0;
        step.ExecutionPrompt = string.Empty;
        step.CopilotSessionId = null;
        step.CopilotSessionHome = string.Empty;
        step.OutputSummary = string.Empty;
        step.PushbackReason = string.Empty;
        step.StartedAt = null;
        step.CompletedAt = null;
        step.DurationMilliseconds = 0;
    }

    private async Task RecoverInterruptedStepAsync(
        InterruptedStepCandidate candidate,
        CancellationToken cancellationToken)
    {
        var copilotHome = string.IsNullOrWhiteSpace(candidate.CopilotSessionHome)
            ? sessionJournal.ExpectedHome()
            : candidate.CopilotSessionHome;
        var snapshot = candidate.CopilotSessionId is { } sessionId
            ? await sessionJournal.InspectAsync(
                copilotHome,
                sessionId,
                cancellationToken)
            : null;
        var discovered = false;
        if (!candidate.IsPreMortemRevision &&
            (snapshot is null ||
             snapshot.State == CopilotSessionJournalState.Missing))
        {
            var deterministicSessionId = AgentSessionIdentity.Create(
                candidate.FlowId,
                candidate.Iteration,
                candidate.AgentId,
                candidate.PlanStepKey);
            snapshot = await sessionJournal.InspectAsync(
                copilotHome,
                deterministicSessionId,
                cancellationToken);
            discovered =
                snapshot.State != CopilotSessionJournalState.Missing;
        }
        if (snapshot?.State == CopilotSessionJournalState.Completed &&
            snapshot.Result is { Success: true } recoveredExplanation &&
            candidate.InvocationKind ==
                ExecutionInvocationKind.BlockerExplanation &&
            await HasPersistedMissingQualificationAsync(
                candidate.FlowId,
                cancellationToken) &&
            CopilotReasoningHost.IsRecoveryCurrent(
                candidate.StartedAt,
                snapshot.CompletedAt))
        {
            await PersistRecoveredSessionIdentityAsync(
                candidate.StepId,
                snapshot,
                discovered,
                cancellationToken);
            await _missingQualifications.RecoverCompletedExplanationAsync(
                candidate.FlowId,
                candidate.StepId,
                ToRecoveredExecutionResult(
                    recoveredExplanation,
                    snapshot.SessionId),
                snapshot.SessionId,
                snapshot.CopilotHome,
                snapshot.CompletedAt ?? DateTimeOffset.UtcNow,
                cancellationToken);
            CleanupRecoveredSessionRoot(snapshot);
            return;
        }

        if (snapshot?.State == CopilotSessionJournalState.Completed &&
            snapshot.Result is { Success: true } recoveredResult &&
            CopilotReasoningHost.IsRecoverableCompletedOutput(
                 candidate.AgentRole,
                 recoveredResult.OutputSummary,
                 candidate.IsPreMortemRevision,
                 invocationKind: candidate.InvocationKind,
                 isOutcomeOwner: candidate.IsOutcomeOwner,
                 planStepKey: candidate.PlanStepKey,
                 expectedFlowKind: candidate.FlowKind) &&
            CopilotReasoningHost.IsRecoveryCurrent(
                candidate.StartedAt,
                snapshot.CompletedAt))
        {
            await PersistRecoveredSessionIdentityAsync(
                candidate.StepId,
                snapshot,
                discovered,
                cancellationToken);
            await RecoverCompletedStepAsync(
                candidate.FlowId,
                candidate.StepId,
                snapshot.SessionId,
                recoveredResult,
                snapshot.CompletedAt,
                cancellationToken);
            CleanupRecoveredSessionRoot(snapshot);
            return;
        }
        if (snapshot?.State == CopilotSessionJournalState.Completed)
        {
            throw new CompletedJournalContractException(
                $"Completed Copilot session {snapshot.SessionId:D} is stale or does not satisfy the persisted {candidate.InvocationKind} contract. It requires manual restart.",
                snapshot.Result?.OutputSummary ?? string.Empty);
        }

        var stoppedActiveSession = false;
        if (snapshot?.State == CopilotSessionJournalState.Active)
        {
            stoppedActiveSession = sessionJournal.TryStopActiveSession(snapshot);
            if (!stoppedActiveSession)
            {
                throw new InvalidOperationException(
                    $"Copilot session {snapshot.SessionId:D} is still active and could not be stopped safely.");
            }
        }

        await QueueInterruptedStepAsync(
            candidate,
            snapshot,
            discovered,
            stoppedActiveSession,
            cancellationToken);
    }

    private async Task<bool> HasPersistedMissingQualificationAsync(
        Guid flowId,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        return await database.Flows
            .AsNoTracking()
            .AnyAsync(
                flow =>
                    flow.Id == flowId &&
                    flow.CurrentBlockerCode ==
                        MissingQualificationCoordinator.BlockerCode &&
                    flow.CurrentBlockerDataJson != null &&
                    flow.CurrentBlockerDataJson != string.Empty,
                cancellationToken);
    }

    private void CleanupRecoveredSessionRoot(
        CopilotSessionSnapshot snapshot)
    {
        try
        {
            _manifestStager.CleanupSessionRoot(
                snapshot.CopilotHome,
                snapshot.SessionId);
        }
        catch (Exception exception)
        {
            // A recovered lifecycle result is already durable. Cleanup failure is observable,
            // but must not turn a conclusive customer outcome back into a failed execution.
            logger.LogWarning(
                exception,
                "Could not remove staged context for recovered Copilot session {SessionId}.",
                snapshot.SessionId);
        }
    }

    private async Task CleanupConclusiveStagedSessionsAsync(
        CancellationToken cancellationToken)
    {
        List<StagedSessionCleanupCandidate> candidates;
        await using (var database =
                     await databaseFactory.CreateDbContextAsync(
                         cancellationToken))
        {
            candidates = await (
                    from step in database.FlowSteps.AsNoTracking()
                    join flow in database.Flows.AsNoTracking()
                        on step.FlowRunId equals flow.Id
                    where step.CopilotSessionId != null
                    select new StagedSessionCleanupCandidate(
                        step.CopilotSessionId!.Value,
                        step.CopilotSessionHome,
                        step.Status,
                        step.Phase,
                        flow.Status))
                .ToListAsync(cancellationToken);
        }

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        foreach (var group in candidates.GroupBy(
                     item =>
                         $"{(string.IsNullOrWhiteSpace(item.CopilotHome)
                             ? sessionJournal.ExpectedHome()
                             : item.CopilotHome)}|{item.SessionId:D}",
                     comparer))
        {
            var entries = group.ToArray();
            var durableActiveAttempt = entries.Any(item =>
                (item.StepStatus is
                    StepStatus.Pending or StepStatus.Running) &&
                item.FlowStatus is not (
                    FlowStatus.Approved or
                    FlowStatus.Abandoned or
                    FlowStatus.Blocked));
            if (durableActiveAttempt)
            {
                continue;
            }
            var failedResumeCandidate = entries.Any(item =>
                item.StepStatus == StepStatus.Failed &&
                item.FlowStatus == FlowStatus.Failed &&
                item.Phase is
                    AgentRunPhase.Stalled or
                    AgentRunPhase.TimedOut or
                    AgentRunPhase.CanceledByReconciliation);
            var conclusive = entries.Any(item =>
                item.StepStatus is
                    StepStatus.Completed or
                    StepStatus.Pushback or
                    StepStatus.Failed or
                    StepStatus.Skipped ||
                item.FlowStatus is
                    FlowStatus.Approved or
                    FlowStatus.Abandoned or
                    FlowStatus.Blocked or
                    FlowStatus.Failed);
            if (!conclusive)
            {
                continue;
            }

            var candidate = entries[0];
            var home = string.IsNullOrWhiteSpace(candidate.CopilotHome)
                ? sessionJournal.ExpectedHome()
                : candidate.CopilotHome;
            try
            {
                var snapshot = await sessionJournal.InspectAsync(
                    home,
                    candidate.SessionId,
                    cancellationToken);
                if (failedResumeCandidate &&
                    snapshot.State is
                        CopilotSessionJournalState.Interrupted or
                        CopilotSessionJournalState.Active)
                {
                    continue;
                }
                if (snapshot.State ==
                    CopilotSessionJournalState.Active)
                {
                    if (!sessionJournal.TryStopActiveSession(snapshot))
                    {
                        continue;
                    }
                    snapshot = await sessionJournal.InspectAsync(
                        home,
                        candidate.SessionId,
                        cancellationToken);
                    if (snapshot.State ==
                        CopilotSessionJournalState.Active)
                    {
                        continue;
                    }
                }
                _manifestStager.CleanupSessionRoot(
                    home,
                    candidate.SessionId);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Could not reconcile staged context for Copilot session {SessionId}.",
                    candidate.SessionId);
            }
        }
    }

    private async Task PersistRecoveredSessionIdentityAsync(
        Guid stepId,
        CopilotSessionSnapshot snapshot,
        bool discovered,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var step = await database.FlowSteps.SingleAsync(
            item => item.Id == stepId,
            cancellationToken);
        step.CopilotSessionId = snapshot.SessionId;
        step.CopilotSessionHome = snapshot.CopilotHome;
        if (discovered)
        {
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = step.FlowRunId,
                FlowStepId = step.Id,
                Type = "agent.session-discovered",
                Message =
                    $"Recovered Copilot session {snapshot.SessionId:D} from its persisted journal."
            });
        }
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task QueueInterruptedStepAsync(
        InterruptedStepCandidate candidate,
        CopilotSessionSnapshot? snapshot,
        bool discovered,
        bool stoppedActiveSession,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var step = await database.FlowSteps.SingleAsync(
            item => item.Id == candidate.StepId,
            cancellationToken);
        var canResume = snapshot?.State is
            CopilotSessionJournalState.Interrupted or
            CopilotSessionJournalState.Active;
        if (!canResume)
        {
            throw new InvalidOperationException(
                "The interrupted durable attempt has no recoverable Copilot session; an explicit retry attempt is required.");
        }
        CopilotReasoningHost.ValidatePersistedExecutionInstructions(
            candidate.ExecutionPrompt,
            candidate.WorkflowRevision);
        step.Status = StepStatus.Pending;
        step.Phase = AgentRunPhase.CanceledByReconciliation;
        step.CompletedAt = null;
        step.CopilotSessionId = canResume ? snapshot!.SessionId : null;
        step.CopilotSessionHome = canResume ? snapshot!.CopilotHome : string.Empty;
        if (discovered)
        {
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = candidate.FlowId,
                FlowStepId = candidate.StepId,
                Type = "agent.session-discovered",
                Message =
                    $"Found Copilot session {snapshot!.SessionId:D} for the interrupted step."
            });
        }
        if (stoppedActiveSession)
        {
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = candidate.FlowId,
                FlowStepId = candidate.StepId,
                Type = "agent.orphan-stopped",
                Message =
                    $"Stopped orphaned Copilot session {snapshot!.SessionId:D} before resuming it."
            });
        }
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = candidate.FlowId,
            FlowStepId = candidate.StepId,
            Type = canResume ? "step.resume-queued" : "step.restart-queued",
            Message = canResume
                ? $"{candidate.AgentName} will resume Copilot session {snapshot!.SessionId:D} after restart."
                : $"{candidate.AgentName} has no recoverable Copilot session; " +
                  "a new session will continue from the persisted workspace."
        });
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task<(AgentRecord? Agent, FlowStep? Step)>
        ResolveDynamicPushbackOwnerAsync(
            FlowRun flow,
            FlowStep blockedStep,
            CancellationToken cancellationToken)
    {
        var handoff = AgentHandoffInspector.ParseDynamic(blockedStep.OutputSummary);
        if (!handoff.IsPushback ||
            string.IsNullOrWhiteSpace(handoff.OwnerPlanStepKey))
        {
            return (null, null);
        }

        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        await ValidateDynamicPushbackTargetAsync(
            database,
            flow,
            blockedStep,
            handoff,
            cancellationToken);
        var ownerStep = await database.FlowSteps
            .AsNoTracking()
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.Iteration == flow.Iteration &&
                item.PlanStepKey == handoff.OwnerPlanStepKey &&
                item.Sequence < blockedStep.Sequence &&
                item.Status == StepStatus.Completed)
            .OrderByDescending(item => item.Sequence)
            .ThenByDescending(item => item.Attempt)
            .FirstAsync(cancellationToken);
        var snapshot = await database.FlowAgentSnapshots
            .AsNoTracking()
            .SingleAsync(
                item =>
                    item.FlowRunId == flow.Id &&
                    item.AgentId == ownerStep.AgentId,
                cancellationToken);
        return (SnapshotAgent(snapshot), ownerStep);
    }

    private async Task<FlowStep> RecoverPushbackAsync(
        FlowRun flow,
        FlowStep step,
        string workspacePath,
        string planSummary,
        int complexity,
        IReadOnlyDictionary<string, AgentRecord> upstreamOwners,
        CancellationToken cancellationToken)
    {
        var (upstreamOwner, upstreamOwnerStep) =
            await ResolveDynamicPushbackOwnerAsync(
                flow,
                step,
                cancellationToken);
        if (upstreamOwner is null)
        {
            await AddEventAsync(
                flow.Id,
                step.Id,
                "handoff.unroutable",
                $"{step.AgentName} pushed back, but the plan has no upstream owner to revise the handoff.",
                cancellationToken);
            throw new InvalidOperationException(
                $"{step.AgentName} pushed back without an upstream owner: {step.PushbackReason}");
        }

        await RecordPushbackLearningAsync(
            flow,
            step,
            upstreamOwner,
            cancellationToken);
        await RecordAttributedPushbackAsync(
            flow,
            step,
            upstreamOwner,
            upstreamOwnerStep?.PlanStepKey,
            cancellationToken);

        int pushbackCount;
        int maxHandoffRetries;
        await using (var database = await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            var pushbackRootStepId = step.PushbackRootStepId ?? step.Id;
            pushbackCount = true
                ? await database.FlowSteps.CountAsync(
                    item =>
                        item.FlowRunId == flow.Id &&
                        item.Iteration == flow.Iteration &&
                        item.PlanStepKey == step.PlanStepKey &&
                        item.Status == StepStatus.Pushback &&
                        (item.Id == pushbackRootStepId ||
                         item.PushbackRootStepId == pushbackRootStepId),
                    cancellationToken)
                : await database.FlowSteps.CountAsync(
                    item =>
                        item.FlowRunId == flow.Id &&
                        item.Iteration == flow.Iteration &&
                        item.AgentId == step.AgentId &&
                        item.Status == StepStatus.Pushback &&
                        (item.Id == pushbackRootStepId ||
                         item.PushbackRootStepId == pushbackRootStepId),
                    cancellationToken);
            maxHandoffRetries = await database.Settings
                .AsNoTracking()
                .Select(item => item.MaxHandoffRetries)
                .SingleAsync(cancellationToken);
        }
        if (maxHandoffRetries is < 0 or > 10)
        {
            throw new InvalidOperationException(
                $"The configured handoff retry limit {maxHandoffRetries} is outside the supported range 0-10.");
        }

        if (!HasHandoffRetryAvailable(pushbackCount, maxHandoffRetries))
        {
            await AddEventAsync(
                flow.Id,
                step.Id,
                "handoff.retry-limit-exhausted",
                $"{step.AgentName} exhausted the configured limit of " +
                $"{maxHandoffRetries} handoff retries.",
                cancellationToken);
            throw new InvalidOperationException(
                $"{step.AgentName} still cannot continue after {maxHandoffRetries} " +
                $"handoff retries: {step.PushbackReason}");
        }

        var (revisionStepId, retryStepId) = await SchedulePushbackRecoveryAsync(
            flow,
            step,
            upstreamOwner,
            upstreamOwnerStep,
            pushbackCount,
            maxHandoffRetries,
            cancellationToken);
        var revision = await ExecuteWithPushbackRecoveryAsync(
            flow,
            flow.Id,
            revisionStepId,
            workspacePath,
            planSummary,
            complexity,
            upstreamOwners,
            cancellationToken);
        await SetRetryAssignmentAsync(
            retryStepId,
            revision,
            cancellationToken);
        return await ExecuteWithPushbackRecoveryAsync(
            flow,
            flow.Id,
            retryStepId,
            workspacePath,
            planSummary,
            complexity,
            upstreamOwners,
            cancellationToken);
    }

    private async Task RecoverUnresolvedPushbacksAsync(
        FlowRun flow,
        string workspacePath,
        string planSummary,
        int complexity,
        IReadOnlyDictionary<string, AgentRecord> upstreamOwners,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            FlowStep? unresolved;
            await using (var database = await databaseFactory.CreateDbContextAsync(cancellationToken))
            {
                unresolved = await database.FlowSteps
                    .AsNoTracking()
                    .Where(step =>
                        step.FlowRunId == flow.Id &&
                        step.Iteration == flow.Iteration &&
                        step.Status == StepStatus.Pushback &&
                        !database.FlowSteps.Any(retry =>
                            retry.FlowRunId == step.FlowRunId &&
                            retry.Iteration == step.Iteration &&
                            retry.AgentId == step.AgentId &&
                            retry.StableSemanticRootId ==
                            (step.StableSemanticRootId ??
                             step.RetryOfStepId ??
                             step.Id) &&
                            retry.Attempt > step.Attempt &&
                            retry.Sequence > step.Sequence &&
                            retry.Status != StepStatus.Skipped))
                    .OrderBy(step => step.Sequence)
                    .FirstOrDefaultAsync(cancellationToken);
            }

            if (unresolved is null)
            {
                return;
            }

            await AddEventAsync(
                flow.Id,
                unresolved.Id,
                "handoff.recovery-resumed",
                $"Recovered an interrupted revision loop for {unresolved.AgentName}.",
                cancellationToken);
            await RecoverPushbackAsync(
                flow,
                unresolved,
                workspacePath,
                planSummary,
                complexity,
                upstreamOwners,
                cancellationToken);
        }
    }

    private async Task<(Guid RevisionStepId, Guid RetryStepId)>
        SchedulePushbackRecoveryAsync(
            FlowRun flow,
            FlowStep blockedStep,
            AgentRecord upstreamOwner,
            FlowStep? upstreamOwnerStep,
            int retryNumber,
            int maxHandoffRetries,
            CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var laterSteps = await database.FlowSteps
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.Iteration == flow.Iteration &&
                item.Sequence > blockedStep.Sequence)
            .ToListAsync(cancellationToken);
        foreach (var laterStep in laterSteps)
        {
            laterStep.Sequence += 20;
        }

        var revisionAttempt = (await database.FlowSteps
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.Iteration == flow.Iteration &&
                item.AgentId == upstreamOwner.Id &&
                item.PlanStepKey == upstreamOwnerStep!.PlanStepKey)
            .Select(item => (int?)item.Attempt)
            .MaxAsync(cancellationToken) ?? 0) + 1;
        var (revisionStep, retryStep) = CreateRecoverySteps(
            flow,
            blockedStep,
            upstreamOwner,
            revisionAttempt,
            upstreamOwnerStep);
        var revisionPolicyTightened = false;
        var retryPolicyTightened = false;
        revisionPolicyTightened = PreserveOrTightenRetryPermission(
            flow,
            upstreamOwnerStep!,
            revisionStep);
        retryPolicyTightened = PreserveOrTightenRetryPermission(
            flow,
            blockedStep,
            retryStep);
        database.FlowSteps.AddRange(revisionStep, retryStep);
        var ownerProfile = await database.TaskProfiles
                .AsNoTracking()
                .Where(item =>
                    item.FlowRunId == flow.Id &&
                    item.Iteration == flow.Iteration &&
                    item.PlanStepKey == revisionStep.PlanStepKey)
                .OrderByDescending(item => item.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    $"No task profile exists for pushback owner step '{revisionStep.PlanStepKey}'.");
        var blockedProfile = await database.TaskProfiles
            .AsNoTracking()
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.Iteration == flow.Iteration &&
                item.PlanStepKey == retryStep.PlanStepKey)
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                $"No task profile exists for blocked step '{retryStep.PlanStepKey}'.");
        database.TaskProfiles.AddRange(
            TaskProfileRules.CopyForStep(ownerProfile, revisionStep.Id),
            TaskProfileRules.CopyForStep(blockedProfile, retryStep.Id));
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = blockedStep.Id,
            Type = "handoff.revision-scheduled",
            Message =
                $"{upstreamOwner.Name} will resume its Copilot session to unblock " +
                $"{blockedStep.AgentName} (retry {retryNumber} of {maxHandoffRetries})."
        });
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = retryStep.Id,
            Type = "handoff.retry-scheduled",
            Message =
                $"{blockedStep.AgentName} will resume its Copilot session after " +
                $"{upstreamOwner.Name} responds."
        });
        foreach (var tightenedStep in new[]
                 {
                     revisionPolicyTightened ? revisionStep : null,
                     retryPolicyTightened ? retryStep : null
                 }.OfType<FlowStep>())
        {
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = tightenedStep.Id,
                Type = "step.permission-policy-tightened",
                Message =
                    "The retry retained its original permission ceiling and incorporated only stricter current WORKFLOW.md restrictions."
            });
        }
        await database.SaveChangesAsync(cancellationToken);
        return (revisionStep.Id, retryStep.Id);
    }

    internal static (FlowStep RevisionStep, FlowStep RetryStep) CreateRecoverySteps(
        FlowRun flow,
        FlowStep blockedStep,
        AgentRecord upstreamOwner,
        int revisionAttempt,
        FlowStep? upstreamOwnerStep = null)
    {
        var ownerStep = upstreamOwnerStep ??
            throw new InvalidOperationException(
                "Pushback recovery requires the owner's immutable plan step.");
        var revisionStep = new FlowStep
        {
            FlowRunId = flow.Id,
            WorkflowRevision = ownerStep.WorkflowRevision,
            Iteration = flow.Iteration,
            Sequence = blockedStep.Sequence + 10,
            AgentId = upstreamOwner.Id,
            AgentName = upstreamOwner.Name,
            AgentRole = upstreamOwner.Role,
            Label = $"Revision after {blockedStep.AgentName} pushback",
            PlanStepKey = ownerStep.PlanStepKey,
            PlanDutiesJson = ownerStep.PlanDutiesJson,
            PlanStage = ownerStep.PlanStage,
            InvocationKind = ownerStep.InvocationKind,
            IsOutcomeOwner = ownerStep.IsOutcomeOwner,
            PermissionProfile = ownerStep.PermissionProfile,
            EffectivePermissionJson = ownerStep.EffectivePermissionJson,
            Status = StepStatus.Pending,
            Attempt = revisionAttempt,
            InputSummary =
                $"{blockedStep.AgentName} cannot continue because: " +
                $"{blockedStep.PushbackReason}{Environment.NewLine}{Environment.NewLine}" +
                "Resume your prior work, correct the missing handoff or implementation detail, " +
                $"and explicitly unblock {blockedStep.AgentName}.{Environment.NewLine}{Environment.NewLine}" +
                $"Blocked agent output:{Environment.NewLine}" +
                blockedStep.OutputSummary
        };
        var retryStep = new FlowStep
        {
            FlowRunId = flow.Id,
            WorkflowRevision = blockedStep.WorkflowRevision,
            Iteration = flow.Iteration,
            Sequence = blockedStep.Sequence + 20,
            AgentId = blockedStep.AgentId,
            AgentName = blockedStep.AgentName,
            AgentRole = blockedStep.AgentRole,
            Label = $"Retry after {upstreamOwner.Name} revision",
            PlanStepKey = blockedStep.PlanStepKey,
            PlanDutiesJson = blockedStep.PlanDutiesJson,
            PlanStage = blockedStep.PlanStage,
            InvocationKind = blockedStep.InvocationKind,
            IsOutcomeOwner = blockedStep.IsOutcomeOwner,
            PermissionProfile = blockedStep.PermissionProfile,
            EffectivePermissionJson = blockedStep.EffectivePermissionJson,
            RemotePublicationAllowed = blockedStep.RemotePublicationAllowed,
            Status = StepStatus.Pending,
            Attempt = blockedStep.Attempt + 1,
            InputSummary =
                $"{upstreamOwner.Name} is revising the rejected handoff. " +
                "Resume this role after the corrected handoff is attached to the retry.",
            RetryOfStepId =
                GetRetryRootId(blockedStep),
            DependsOnStepId = revisionStep.Id,
            PushbackRootStepId =
                blockedStep.PushbackRootStepId ?? blockedStep.Id,
            StableSemanticRootId = GetStableSemanticRootId(blockedStep),
            PreMortemOriginStepId = blockedStep.PreMortemOriginStepId,
            PreMortemTargetStepId = blockedStep.PreMortemTargetStepId,
            PreMortemReviewStepId = blockedStep.PreMortemReviewStepId
        };
        return (revisionStep, retryStep);
    }

    private async Task SetRetryAssignmentAsync(
        Guid retryStepId,
        FlowStep revision,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var retryStep = await database.FlowSteps.SingleAsync(
            item => item.Id == retryStepId,
            cancellationToken);
        retryStep.DependsOnStepId = revision.Id;
        retryStep.InputSummary =
            $"{revision.AgentName} responded to your pushback. Resume your role and re-attempt " +
            $"the blocked work using this corrected handoff:{Environment.NewLine}{Environment.NewLine}" +
            revision.OutputSummary;
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task HydrateRetryAssignmentAsync(
        Guid retryStepId,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var retryStep = await database.FlowSteps.SingleAsync(
            item => item.Id == retryStepId,
            cancellationToken);
        if (!retryStep.Label.StartsWith("Retry after ", StringComparison.Ordinal) ||
            retryStep.DependsOnStepId is null &&
            !retryStep.InputSummary.Contains(
                "is revising the rejected handoff",
                StringComparison.Ordinal))
        {
            return;
        }

        var revisionStep = retryStep.DependsOnStepId is { } dependencyStepId
            ? await database.FlowSteps
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item =>
                        item.Id == dependencyStepId &&
                        item.Status == StepStatus.Completed,
                    cancellationToken)
            : await database.FlowSteps
                .AsNoTracking()
                .Where(item =>
                    item.FlowRunId == retryStep.FlowRunId &&
                    item.Iteration == retryStep.Iteration &&
                    item.Sequence < retryStep.Sequence &&
                    item.Status == StepStatus.Completed)
                .OrderByDescending(item => item.Sequence)
                .FirstOrDefaultAsync(cancellationToken);
        if (revisionStep is null)
        {
            throw new InvalidOperationException(
                $"{retryStep.AgentName} cannot retry before the upstream revision completes.");
        }

        retryStep.InputSummary =
            $"{revisionStep.AgentName} responded to your pushback. Resume your role and re-attempt " +
            $"the blocked work using this corrected handoff:{Environment.NewLine}{Environment.NewLine}" +
            revisionStep.OutputSummary;
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordPushbackLearningAsync(
        FlowRun flow,
        FlowStep blockedStep,
        AgentRecord upstreamOwner,
        CancellationToken cancellationToken)
    {
        var candidate = CreatePushbackLearning(flow, blockedStep, upstreamOwner);
        await _learningGate.WaitAsync(cancellationToken);
        try
        {
            await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
            var alreadyRecorded = await database.FlowEvents.AnyAsync(
                item =>
                    item.FlowRunId == flow.Id &&
                    item.FlowStepId == blockedStep.Id &&
                    item.Type == "learning.pushback-recorded",
                cancellationToken);
            if (alreadyRecorded)
            {
                return;
            }

            var learning = await database.Learnings.SingleOrDefaultAsync(
                item =>
                    item.Category == candidate.Category &&
                    item.AgentId == candidate.AgentId &&
                    item.Trigger == candidate.Trigger,
                cancellationToken);
            if (learning is null)
            {
                database.Learnings.Add(candidate);
            }
            else
            {
                learning.TimesObserved++;
            }

            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = blockedStep.Id,
                Type = "learning.pushback-recorded",
                Message =
                    $"The harness refined {upstreamOwner.Name}'s future prompts from " +
                    $"{blockedStep.AgentName}'s pushback."
            });
            await database.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _learningGate.Release();
        }
    }

    private async Task RecordAttributedPushbackAsync(
        FlowRun flow,
        FlowStep blockedStep,
        AgentRecord upstreamOwner,
        string? upstreamPlanStepKey,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var producingStep = await database.FlowSteps
            .AsNoTracking()
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.Iteration == flow.Iteration &&
                item.AgentId == upstreamOwner.Id &&
                item.PlanStepKey == upstreamPlanStepKey &&
                item.Sequence < blockedStep.Sequence &&
                item.Status == StepStatus.Completed)
            .OrderByDescending(item => item.Sequence)
            .ThenByDescending(item => item.Attempt)
            .FirstOrDefaultAsync(cancellationToken);
        if (producingStep is null)
        {
            return;
        }
        await observationRecorder.RecordCompletionAsync(
            producingStep.Id,
            accepted: false,
            producingStep.DurationMilliseconds,
            Math.Max(1, producingStep.ExecutionAttempts),
            "downstream-pushback",
            cancellationToken);
    }

    internal static HarnessLearning CreatePushbackLearning(
        FlowRun flow,
        FlowStep blockedStep,
        AgentRecord upstreamOwner)
    {
        var reason = string.IsNullOrWhiteSpace(blockedStep.PushbackReason)
            ? "The downstream agent could not use the handoff."
            : blockedStep.PushbackReason.Trim();
        return new HarnessLearning
        {
            SourceFlowId = flow.Id,
            AgentId = upstreamOwner.Id,
            Category = "Handoff pushback",
            Trigger = ClipText(
                $"{blockedStep.AgentRole} rejected {upstreamOwner.Role}: {reason}",
                700),
            Lesson = ClipText(
                $"{blockedStep.AgentName} could not continue because {reason}",
                1_000),
            PromptRefinement = ClipText(
                $"When handing off to {blockedStep.AgentName}, explicitly resolve this gap: " +
                $"{reason} Provide concrete artifacts, decisions, and evidence that let the " +
                "downstream role start without guessing.",
                1_500)
        };
    }

    private async Task UpdateWorkspaceAsync(
        Guid flowId,
        WorkspaceInfo workspace,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var flow = await database.Flows.SingleAsync(item => item.Id == flowId, cancellationToken);
        flow.WorkspacePath = workspace.Path;
        flow.BranchName = workspace.BranchName;
        if (flow.Kind == FlowKind.Delivery &&
            workspace.TrustedRepositories is { Count: > 0 })
        {
            var repositoryMap = StudioWorkspaceRepositoryMapLedger.Serialize(
                StudioWorkspaceRepositoryMapLedger.Create(
                    flow,
                    workspace.Path,
                    workspace.TrustedRepositories));
            var existingMaps = await database.FlowEvents
                .Where(item =>
                    item.FlowRunId == flow.Id &&
                    item.Type == StudioWorkspaceRepositoryMapLedger.EventType)
                .ToListAsync(cancellationToken);
            if (existingMaps.Count > 1 ||
                existingMaps.Count == 1 &&
                !string.Equals(
                    existingMaps[0].DataJson,
                    repositoryMap,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The durable studio workspace repository map does not match the recovered workspace.");
            }
            if (existingMaps.Count == 0)
            {
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    Type = StudioWorkspaceRepositoryMapLedger.EventType,
                    Message =
                        $"Recorded {workspace.TrustedRepositories.Count} trusted repository publication target(s) for the isolated Delivery workspace.",
                    DataJson = repositoryMap
                });
            }
        }
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
    }

    private static string AppendOutcomeAssignment(
        string assignment,
        string outcomeAssignment) =>
        string.IsNullOrWhiteSpace(outcomeAssignment)
            ? assignment
            : $"{assignment}{Environment.NewLine}{Environment.NewLine}" +
              outcomeAssignment;

    private static (string Context, string Contract) BuildOutcomePrompt(
        FlowStep step,
        DeliveryVerificationAssignment? readiness = null)
    {
        var ownerContext = step.IsOutcomeOwner
            ? "You are the final outcome owner. Consolidate the confirmed goal and the completed plan-step evidence into the customer-review result."
            : "The accepted dynamic team plan defines this step's duties and outcome ownership.";
        var ownerContract = step.IsOutcomeOwner
            ? FlowOutcomeResponseContract()
            : "No flow-outcome document is required because this is not the final outcome-owner step.";
        if (readiness is null)
        {
            return (ownerContext, ownerContract);
        }
        return (
            ownerContext +
            Environment.NewLine + Environment.NewLine +
            BuildDeliveryVerificationContext(readiness),
            ownerContract +
            Environment.NewLine + Environment.NewLine +
            DeliveryQaResponseContract(readiness.PlanHash));
    }

    /// <summary>
    /// The exact host-computed inputs a Delivery verification turn must work from: the planned
    /// criteria, the acceptance plan hash it must echo verbatim, and the complete set of evidence
    /// identifiers the host issued. Nothing outside this set is a valid evidence reference.
    /// </summary>
    internal static string BuildDeliveryVerificationContext(
        DeliveryVerificationAssignment readiness)
    {
        var criteria = string.Join(
            Environment.NewLine,
            readiness.Plan.Criteria.Select(criterion =>
                $"- {criterion.Id} (customerVisible={criterion.CustomerVisible.ToString().ToLowerInvariant()}; " +
                $"owners={string.Join('/', criterion.OwnerRoles ?? [])}; " +
                $"evidenceKinds={string.Join('/', (criterion.EvidenceKinds ?? []).Select(kind => kind.ToString()))}): " +
                $"{criterion.Requirement} | verification: {criterion.Verification}"));
        var evidence = BuildDeliveryEvidenceContext(readiness.Evidence);
        var preview = readiness.VerificationPreviewUrl is null
            ? string.Empty
            : $"""
              Active verification preview metadata: {readiness.VerificationPreviewUrl}
              For ordinary verification, not response-only correction, read this endpoint to
              discover every generated variant. Open each returned openUrl
              on the same Studio origin; it uses the actual customer-preview sandbox, CSP, and
              bootstrap. Allow only this local preview origin while blocking external network
              access. Exercise the iframe content at representative desktop and mobile sizes.
              These unreviewed URLs are available only while this verification task is running.
              Ordinary customer-preview URLs require the later reviewed seal; do not use them
              before review or treat a direct-file check as proof of the harness security layer.
              This endpoint grants no customer approval or publication authority.
              """;
        return $"""
            You additionally own the Delivery verification duty for this iteration.

            AcceptancePlanHash (copy this value verbatim into the verification document):
            {readiness.PlanHash}

            Planned acceptance criteria ({readiness.Plan.Criteria.Count}); report exactly one result for each:
            {criteria}

            Host-issued evidence identifiers you may cite ({readiness.Evidence.Count}); any other identifier is rejected:
            {evidence}

            Current verification step evidence prefix: {readiness.CurrentStepEvidencePrefix}
            Tool calls from this turn receive that prefix followed by a one-based, three-digit
            index in host-observed completion order, including context reads and failed calls.
            Do not infer an ID from a command label, shell session number, or a count of only the
            important checks. The host validates every citation against the actual observations.
            If references are invalid, the host preserves your work and supplies the recorded
            identifiers for one response-only correction, without rerunning successful checks.

            {preview}
            """;
    }

    internal static string BuildDeliveryEvidenceContext(
        IReadOnlyList<DeliveryEvidenceItem> evidence)
    {
        if (evidence.Count == 0)
        {
            return "- (none: the host issued no evidence identifiers, so no criterion can be verified)";
        }
        var index = string.Join(
            Environment.NewLine,
            evidence.GroupBy(item => new
            {
                item.Kind,
                item.SupportsVerification,
                item.ExitCode
            }).Select(group =>
                $"[{group.Key.Kind}; supportsVerification=" +
                $"{group.Key.SupportsVerification.ToString().ToLowerInvariant()}; " +
                $"exitCode={group.Key.ExitCode?.ToString() ?? "n/a"}]" +
                Environment.NewLine +
                string.Join(
                    Environment.NewLine,
                    group.Chunk(12).Select(chunk =>
                        string.Join(", ", chunk.Select(item => item.EvidenceId))))));
        const int detailBudget = 16_000;
        var details = new List<string>();
        var used = 0;
        foreach (var item in evidence.Reverse())
        {
            if (item.EvidenceId.EndsWith("-000", StringComparison.Ordinal))
            {
                continue;
            }
            var detail =
                $"- {item.EvidenceId} [{item.Kind}; supportsVerification=" +
                $"{item.SupportsVerification.ToString().ToLowerInvariant()}; " +
                $"exitCode={item.ExitCode?.ToString() ?? "n/a"}] " +
                $"{ClipText(item.Locator, 160)}: {ClipText(item.Summary, 240)}";
            if (used + detail.Length + Environment.NewLine.Length > detailBudget)
            {
                break;
            }
            details.Add(detail);
            used += detail.Length + Environment.NewLine.Length;
        }
        return
            "Complete identifier index (kind and success are authoritative):" +
            Environment.NewLine + index +
            Environment.NewLine + Environment.NewLine +
            "Recent observation details (newest first; summaries are abbreviated, not new evidence):" +
            Environment.NewLine + string.Join(Environment.NewLine, details);
    }

    internal static string DeliveryQaResponseContract(string acceptancePlanHash) => $$"""
        Also output exactly one strict JSON document between these standalone sentinels:
        {{DeliveryReadinessPolicy.QaBeginMarker}}
        {"AcceptancePlanHash":"{{acceptancePlanHash}}","Verdict":"PASS|FAIL|BLOCKED","Criteria":[{"CriterionId":"AC-001","Outcome":"Verified|Failed|Blocked","EvidenceIds":["EV-S000-001"],"Rationale":"what the successful host-observed evidence shows","Remediation":null,"ResponsibleRoles":[]}],"ResidualRisks":[],"PlanGaps":[]}
        {{DeliveryReadinessPolicy.QaEndMarker}}
        Property names and enum casing are exact. Provide exactly one Criteria entry per planned
        criterion identifier, referencing only host-issued evidence whose successful kind is allowed
        by that criterion. The execution-record identifier ending in -000 is context only and cannot
        prove a Verified result. Outcome is
        Verified, Failed, or Blocked. Verified has no responsible roles or remediation. Failed names
        at least one responsible role and includes remediation. Blocked includes remediation and may
        omit responsible roles only for a clearly external blocker. Each ResidualRisks entry needs RiskId matching RR-000,
        Classification NonBlockingDisclosure, WaiverRequired, or Blocking, Severity Low, Medium,
        High, or Critical, Statement, Impact, EvidenceIds, CriterionIds, and PreMortemFindingId
        (null or matching PM-000). Never reclassify a failed or blocked criterion as a residual
        risk. PlanGaps is required; report every confirmed requirement omitted from the acceptance
        plan with Requirement, Verification, OwnerRoles, and Rationale. Verdict must be BLOCKED when
        any criterion is Blocked or any risk is Blocking, PASS only when every criterion is Verified
        and PlanGaps is empty, and FAIL otherwise; the host derives the same value and rejects a
        mismatch. A COMPLETE handoff marker or a confident summary never makes a candidate releasable.
        """;

    internal static string FlowOutcomeResponseContract() => $$"""
        After the HANDOFF_STATUS marker and concise handoff, output exactly one strict JSON document between these standalone sentinels:
        {{FlowOutcomeParser.BeginSentinel}}
        {"Goal":"the confirmed customer goal","Summary":"the concise customer-review result","ImplementationDetails":["specific evidence, recommendation, or delivered behavior"],"Artifacts":[]}
        {{FlowOutcomeParser.EndSentinel}}
        Keep the complete response under 12,000 characters. ImplementationDetails must contain
        1-24 concise consolidated entries; merge overlapping evidence instead of appending a
        transcript. Put the standalone HANDOFF_STATUS line and opening sentinel near the beginning
        so no required envelope marker can be displaced by a long response.
        Property names and casing are exact. Goal, Summary, and every ImplementationDetails item are required and customer-facing. Include 1-24 implementation details. Artifacts is always required. Advisory may declare only bounded text/plain, text/markdown, text/csv, or application/json artifacts with relative non-traversing paths; the host writes validated artifacts after proving the guarded source snapshot is unchanged. Do not write Advisory artifacts or source files yourself. Delivery normally uses an empty Artifacts list. Do not emit either sentinel more than once or inside a Markdown fence.
        """;

    private static string BuildOutcomeReviewEvidence(
        FlowOutcomeDocument document) =>
        $"Goal: {document.Goal}{Environment.NewLine}{Environment.NewLine}" +
        "Implementation details:" +
        Environment.NewLine +
        string.Join(
            Environment.NewLine,
            document.ImplementationDetails!.Select(detail => $"- {detail}"));

    internal static string BuildStepTask(string customerTask, string inputSummary)
    {
        var brief = AssignmentBriefFormatter.Format(customerTask);
        if (string.IsNullOrWhiteSpace(inputSummary))
        {
            return brief;
        }

        return
            $"{brief}{Environment.NewLine}{Environment.NewLine}" +
            $"## Role-specific assignment{Environment.NewLine}{inputSummary}";
    }

    internal static string BuildPreMortemAssignment(FlowStep target) =>
        "Assume this result was adopted and, six months later, became a disaster. " +
        "Independently reconstruct what failed, what the result missed, and the precise prevention. " +
        "Research the isolated workspace and authoritative sources as needed. Report no more than " +
        "five findings, and report CLEAR when no evidence-backed failure case remains. Keep the " +
        "entire response under 9,000 characters and each finding field under 800 characters. " +
        "Evaluate the correctness and completeness of this role's handoff, not whether its " +
        "acknowledged downstream corrections have already been implemented. An honest failed or " +
        "blocked QA assessment is a valid handoff to refinement, not an approval. Do not repeat " +
        "acknowledged failures as new findings or turn optional improvements into requirements " +
        "outside the confirmed customer brief." +
        $"{Environment.NewLine}{Environment.NewLine}" +
        $"Evaluated agent: {target.AgentName} ({target.AgentRole})" +
        $"{Environment.NewLine}Evaluated model: " +
        $"{(string.IsNullOrWhiteSpace(target.Model) ? "pending" : target.Model)}" +
        $"{Environment.NewLine}{Environment.NewLine}" +
        $"Evaluated result:{Environment.NewLine}" +
        (string.IsNullOrWhiteSpace(target.OutputSummary)
            ? "The completed result will be attached immediately before this review runs."
            : target.OutputSummary);

    internal static string BuildPreMortemRevisionAssignment(
        string reviewOutput,
        string planDutiesJson = "[]")
    {
        var duties = ReadPlanDuties(planDutiesJson);
        var ownsImplementation = duties.Contains(PlanDuty.Implement);
        var preparesOutcome = duties.Contains(PlanDuty.PrepareOutcome);
        var safeReviewOutput = reviewOutput
            .Replace(
                TeamPlanParser.BeginSentinel,
                "[team plan begin marker]",
                StringComparison.Ordinal)
            .Replace(
                TeamPlanParser.EndSentinel,
                "[team plan end marker]",
                StringComparison.Ordinal)
            .Replace(
                FlowOutcomeParser.BeginSentinel,
                "[flow outcome begin marker]",
                StringComparison.Ordinal)
            .Replace(
                FlowOutcomeParser.EndSentinel,
                "[flow outcome end marker]",
                StringComparison.Ordinal);
        return
        "The Pre-mortem Sceptic found evidence that this result could fail within six months. " +
        "Resume your original work and investigate every finding. Accept, reject, or narrow each " +
        "item based on facts. This is a task revision, not a response-only format correction; " +
        "use this attempt's recorded task permissions. " +
        (ownsImplementation
            ? "If a finding is justified, make the focused corrections owned by this role. "
            : "Do not implement downstream product corrections in this turn; revise this role's " +
              "complete plan, design, or review handoff and assign justified corrections to the " +
              "responsible downstream owner. Stop tool use once that handoff is evidence-based. ") +
        "Return the complete current deliverable or plan, not a delta. " +
        "The next agent must be able to rely on this response alone. " +
        (preparesOutcome
            ? "outcome contract: return exactly 1-24 consolidated ImplementationDetails in the " +
              "complete flow-outcome document. Replace the previous document; merge overlapping " +
              "old and new findings instead of appending entries, and never exceed 24 details. "
            : string.Empty) +
        "Preserve the normal role " +
        "completion contract and end with exactly one disposition marker: " +
        $"{PreMortemRules.AdjustedDisposition} when the complete result materially changed, or " +
        $"{PreMortemRules.UnchangedDisposition} when no material change was justified." +
        $"{Environment.NewLine}{Environment.NewLine}" +
        $"Sceptic output:{Environment.NewLine}" +
        safeReviewOutput;
    }

    private static IReadOnlySet<PlanDuty> ReadPlanDuties(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<string[]>(json) ?? [])
                .Select(value => Enum.Parse<PlanDuty>(value, ignoreCase: false))
                .ToHashSet();
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException)
        {
            throw new InvalidOperationException(
                "The persisted plan duties are invalid.",
                exception);
        }
    }

    private bool PreserveOrTightenRetryPermission(
        FlowRun flow,
        FlowStep source,
        FlowStep retry)
    {
        if (string.IsNullOrWhiteSpace(retry.EffectivePermissionJson))
        {
            retry.PermissionProfile = source.PermissionProfile;
            retry.EffectivePermissionJson = source.EffectivePermissionJson;
            retry.WorkflowRevision = source.WorkflowRevision;
        }
        if (string.IsNullOrWhiteSpace(retry.EffectivePermissionJson))
        {
            return false;
        }

        EffectiveExecutionPermission persisted;
        try
        {
            persisted =
                JsonSerializer.Deserialize<EffectiveExecutionPermission>(
                    retry.EffectivePermissionJson)
                ?? throw new InvalidOperationException(
                    "The retry attempt has an empty effective permission document.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The source attempt has an invalid effective permission document; retry policy failed closed.",
                exception);
        }
        PermissionProfileResolver.ValidatePersisted(
            persisted,
            retry.PermissionProfile);

        var workflow = workflowProvider.GetEffective();
        var accepted = ReviewCoordinator.HasAcceptedCustomerReview(flow);
        var current = _permissionResolver.Resolve(
            new PermissionResolutionRequest(
                flow.Kind,
                retry.InvocationKind,
                retry.PlanStage,
                ReadPlanDuties(retry.PlanDutiesJson).ToImmutableArray(),
                accepted ? ReviewDecision.Accepted : null,
                accepted,
                !string.IsNullOrWhiteSpace(flow.PublicationPlanStepKey) &&
                string.Equals(
                    flow.PublicationPlanStepKey,
                    retry.PlanStepKey,
                    StringComparison.Ordinal)),
            PermissionProfileResolver.FromWorkflow(workflow));
        var tightened = PermissionProfileResolver.Tighten(
            persisted,
            current);
        var remotePublicationAllowed =
            retry.RemotePublicationAllowed &&
            PermissionProfileResolver.GrantsRemotePublication(
                tightened);
        if (PermissionProfileResolver.Equivalent(persisted, tightened) &&
            retry.RemotePublicationAllowed ==
            remotePublicationAllowed)
        {
            return false;
        }

        retry.PermissionProfile = tightened.Profile;
        retry.EffectivePermissionJson = JsonSerializer.Serialize(tightened);
        retry.WorkflowRevision = workflow.Revision;
        retry.RemotePublicationAllowed =
            remotePublicationAllowed;
        return true;
    }

    private static Task<bool> HasDurablePublicationApprovalAsync(
        HarnessDbContext database,
        FlowRun flow,
        FlowStep publicationStep,
        CancellationToken cancellationToken)
    {
        if (publicationStep.Iteration != flow.Iteration ||
            !ReviewCoordinator.IsPublicationStep(flow, publicationStep) ||
            string.IsNullOrWhiteSpace(flow.OutcomeOwnerPlanStepKey))
        {
            return Task.FromResult(false);
        }

        return database.GateRecords
            .AsNoTracking()
            .AnyAsync(
                gate =>
                    gate.FlowRunId == flow.Id &&
                    gate.ActionType == HandoffActionType.CustomerReview &&
                    gate.Resolved &&
                    gate.Approved == true &&
                    gate.ReviewDecision == ReviewDecision.Accepted &&
                    database.FlowSteps.Any(owner =>
                        owner.Id == gate.FlowStepId &&
                        owner.FlowRunId == flow.Id &&
                        owner.Iteration == publicationStep.Iteration &&
                        owner.IsOutcomeOwner &&
                        owner.PlanStepKey == flow.OutcomeOwnerPlanStepKey),
                cancellationToken);
    }

    internal static string BuildPreMortemContextTask(
        string customerTask,
        string assignment) =>
        $"## Role-specific assignment{Environment.NewLine}" +
        assignment +
        $"{Environment.NewLine}{Environment.NewLine}" +
        $"## Original customer outcome{Environment.NewLine}" +
        AssignmentBriefFormatter.Format(customerTask);

    internal static bool HasHandoffRetryAvailable(
        int observedPushbacks,
        int maxHandoffRetries) =>
        observedPushbacks <= maxHandoffRetries;

    internal static FlowStep? FindUnresolvedFailure(
        IReadOnlyCollection<FlowStep> steps) =>
        steps
            .Where(step =>
                step.Status == StepStatus.Failed &&
                !steps.Any(retry =>
                    retry.FlowRunId == step.FlowRunId &&
                    retry.Iteration == step.Iteration &&
                    retry.AgentId == step.AgentId &&
                    retry.Sequence > step.Sequence &&
                    GetStableSemanticRootId(retry) ==
                    GetStableSemanticRootId(step) &&
                    (retry.Status is
                        StepStatus.Pending or
                        StepStatus.Running or
                        StepStatus.Pushback or
                        StepStatus.Completed ||
                     retry.Status == StepStatus.Skipped &&
                     retry.Phase == AgentRunPhase.Succeeded) &&
                    (!IsPreMortemStep(step) ||
                     retry.PreMortemOriginStepId ==
                     step.PreMortemOriginStepId)))
            .OrderByDescending(step => step.Sequence)
            .ThenByDescending(step => step.Attempt)
            .FirstOrDefault();

    internal static FlowStep? FindUnresolvedPushback(
        IReadOnlyCollection<FlowStep> steps) =>
        steps
            .Where(step =>
                step.Status == StepStatus.Pushback &&
                !steps.Any(retry =>
                    retry.FlowRunId == step.FlowRunId &&
                    retry.Iteration == step.Iteration &&
                    retry.AgentId == step.AgentId &&
                    GetStableSemanticRootId(retry) ==
                    GetStableSemanticRootId(step) &&
                    retry.Sequence > step.Sequence &&
                    retry.Attempt > step.Attempt &&
                    retry.Status != StepStatus.Skipped))
            .OrderByDescending(step => step.Sequence)
            .ThenByDescending(step => step.Attempt)
            .FirstOrDefault();

    internal static FlowStep? FindReusableCausalRetry(
        FlowStep failedStep,
        IEnumerable<FlowStep> steps)
    {
        var retryRootStepId = GetStableSemanticRootId(failedStep);
        return steps
            .Where(step =>
                step.FlowRunId == failedStep.FlowRunId &&
                step.Iteration == failedStep.Iteration &&
                step.AgentId == failedStep.AgentId &&
                GetStableSemanticRootId(step) == retryRootStepId &&
                step.Sequence > failedStep.Sequence &&
                step.Status == StepStatus.Skipped &&
                step.Phase != AgentRunPhase.Succeeded)
            .OrderByDescending(step => step.Sequence)
            .ThenByDescending(step => step.Attempt)
            .FirstOrDefault();
    }

    internal static int RetargetDependentSteps(
        FlowStep completedStep,
        IEnumerable<FlowStep> dependents)
    {
        var count = 0;
        foreach (var dependent in dependents)
        {
            if (dependent.Status is not (
                    StepStatus.Pending or StepStatus.Skipped) ||
                dependent.Id == completedStep.Id)
            {
                continue;
            }
            dependent.DependsOnStepId = completedStep.Id;
            count++;
        }
        return count;
    }

    internal static bool IsSupersededRetry(
        FlowStep candidate,
        FlowStep recoveredStep) =>
        candidate.Id != recoveredStep.Id &&
        GetStableSemanticRootId(candidate) ==
        GetStableSemanticRootId(recoveredStep);

    internal static int RetargetSupersededRetryLinks(
        IEnumerable<FlowStep> steps,
        FlowStep recoveredStep)
    {
        var supersededIds = steps
            .Where(step => IsSupersededRetry(step, recoveredStep))
            .Select(step => step.Id)
            .ToHashSet();
        if (supersededIds.Count == 0)
        {
            return 0;
        }

        var count = 0;
        foreach (var step in steps)
        {
            if (step.DependsOnStepId is { } dependencyId &&
                supersededIds.Contains(dependencyId))
            {
                step.DependsOnStepId = recoveredStep.Id;
                count++;
            }
            if (step.PreMortemTargetStepId is { } targetId &&
                supersededIds.Contains(targetId))
            {
                step.PreMortemTargetStepId = recoveredStep.Id;
                count++;
            }
        }
        return count;
    }

    internal static FlowStep SelectEffectiveManualRetryStep(
        FlowStep source,
        IEnumerable<FlowStep> candidates) =>
        candidates
            .Where(item =>
                item.FlowRunId == source.FlowRunId &&
                item.Iteration == source.Iteration &&
                item.AgentId == source.AgentId &&
                item.Status != StepStatus.Skipped &&
                (item.Id == source.Id ||
                 GetStableSemanticRootId(item) == GetStableSemanticRootId(source)))
            .OrderByDescending(item => item.Sequence)
            .ThenByDescending(item => item.StartedAt)
            .FirstOrDefault()
        ?? throw new InvalidOperationException(
            $"No persisted execution step exists for '{source.AgentName}'.");

    internal static int CountPreMortemRounds(IEnumerable<int> attempts) =>
        attempts.Distinct().Count();

    internal static int FirstDeliverySequence(
        int latestTeamLeadSequence) =>
        Math.Max(20, latestTeamLeadSequence + 10);

    internal static int FirstCorrectionSequence(
        int effectiveTeamLeadSequence) =>
        Math.Max(15, effectiveTeamLeadSequence + 5);

    internal static IReadOnlySet<string> SelectEnabledPreMortemCheckpoints(
        IEnumerable<TaskProfile> profiles,
        bool preMortemAvailable) =>
        preMortemAvailable
            ? profiles
                .Where(profile => profile.PreMortemAfter)
                .Select(profile => profile.Role)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    internal static bool ShouldRunPreMortemRound(
        int round,
        int maximumRounds) =>
        maximumRounds > 0 &&
        round > 0 &&
        round <= maximumRounds;

    internal static PreMortemDisposition ValidatePreMortemRevisionOutput(
        string output)
    {
        if (!AgentHandoffInspector.HasCompleteStatus(output))
        {
            throw new PreMortemValidationException(
                ["a pre-mortem revision must return exactly one HANDOFF_STATUS: COMPLETE marker"]);
        }
        return PreMortemRules.ParseDisposition(output);
    }

    private async Task<int> GetMaxHandoffRetriesAsync(
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var maximum = await database.Settings
            .AsNoTracking()
            .Select(item => item.MaxHandoffRetries)
            .SingleAsync(cancellationToken);
        if (maximum is < 0 or > 10)
        {
            throw new InvalidOperationException(
                $"The configured handoff retry limit {maximum} is outside the supported range 0-10.");
        }
        return maximum;
    }

    private static string ClipText(string value, int maxCharacters) =>
        value.Length <= maxCharacters
            ? value
            : value[..maxCharacters] + "...";

    private static string PrefixDigest(string digest) =>
        digest[..Math.Min(digest.Length, 19)];

    internal static bool IsPublishedPullRequestLabel(string label) =>
        label.StartsWith(
            "Published pull request #",
            StringComparison.Ordinal) ||
        label.StartsWith(
            "Published pull requests · ",
            StringComparison.Ordinal);

    private async Task AddEventAsync(
        Guid flowId,
        Guid? stepId,
        string type,
        string message,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flowId,
            FlowStepId = stepId,
            Type = type,
            Message = message
        });
        await database.SaveChangesAsync(cancellationToken);
    }

    private Task RecordProgressAsync(
        Guid flowId,
        Guid stepId,
        AgentRunProgress progress,
        CancellationToken cancellationToken) =>
        RecordProgressCoreAsync(flowId, stepId, progress, cancellationToken);

    private async Task RecordProgressCoreAsync(
        Guid flowId,
        Guid stepId,
        AgentRunProgress progress,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var step = await database.FlowSteps.SingleAsync(
            item => item.Id == stepId,
            cancellationToken);
        var update = AgentProgressPersistence.Apply(step, progress);
        if (update.PhaseChanged)
        {
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flowId,
                FlowStepId = stepId,
                Type = $"agent.{progress.Phase}",
                Message = progress.Activity
            });
        }
        if (!update.StateChanged)
        {
            return;
        }
        await database.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Handles a Delivery result whose host-derived readiness is not
    /// <see cref="DeliveryReadinessState.ReadyToApprove"/>. It opens the separate waiver gate for
    /// <see cref="DeliveryReadinessState.NeedsCustomerWaiver"/> and otherwise opens no customer gate
    /// at all, so neither acceptance nor publication can be reached from a non-ready assessment.
    /// </summary>
    private async Task OpenNonReadyDeliveryStateAsync(
        HarnessDbContext database,
        FlowRun flow,
        FlowStep owner,
        DeliveryReadinessBinding readiness,
        ParsedFlowOutcome normalizedOutcome,
        CancellationToken cancellationToken)
    {
        var currentStepIds = flow.Steps
            .Where(step => step.Iteration == flow.Iteration)
            .Select(step => step.Id)
            .ToHashSet();
        foreach (var stale in flow.GateRecords.Where(gate =>
                     !gate.Resolved &&
                     gate.ActionType is HandoffActionType.CustomerReview
                         or HandoffActionType.CustomerWaiver &&
                     currentStepIds.Contains(gate.FlowStepId) &&
                     (gate.ActionType != HandoffActionType.CustomerWaiver ||
                      readiness.State != DeliveryReadinessState.NeedsCustomerWaiver))
                     .ToList())
        {
            var superseded = handoffGate.PrepareSupersession(
                stale,
                "harness",
                $"Superseded because host-derived readiness is '{readiness.State}'.");
            ApplyGateResolution(superseded, stale);
            handoffGate.RestoreHistory([superseded]);
        }

        if (readiness.State == DeliveryReadinessState.NeedsCustomerWaiver)
        {
            var existing = flow.GateRecords.SingleOrDefault(gate =>
                !gate.Resolved &&
                gate.ActionType == HandoffActionType.CustomerWaiver &&
                currentStepIds.Contains(gate.FlowStepId));
            if (existing is null)
            {
                var waiverGate = handoffGate.SubmitProposal(new HandoffProposal
                {
                    FlowRunId = flow.Id,
                    FlowStepId = owner.Id,
                    ActionType = HandoffActionType.CustomerWaiver,
                    Summary = normalizedOutcome.Document.Summary,
                    Evidence = string.Join(
                        Environment.NewLine,
                        readiness.Contract.Risks
                            .Where(risk =>
                                risk.Classification ==
                                DeliveryRiskClassification.WaiverRequired)
                            .Select(risk =>
                                $"{risk.RiskId} [{risk.Severity}] {risk.Statement} -> {risk.Impact}")),
                    BlastRadius = HandoffBlastRadius.High
                });
                if (waiverGate.Decision != HandoffGateDecision.AwaitingHumanApproval)
                {
                    throw new InvalidOperationException(
                        "CustomerWaiver must always produce a human-gated decision.");
                }
                flow.GateRecords.Add(waiverGate);
                database.Entry(waiverGate).State = EntityState.Added;
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = owner.Id,
                    Type = "gate.customer-waiver-created",
                    Message =
                        $"Harness opened the separate customer waiver gate for {readiness.Contract.RequiredWaiverRiskIds.Count} disclosed risk(s).",
                    DataJson = JsonSerializer.Serialize(new
                    {
                        SnapshotId = readiness.Record.Id,
                        readiness.Revision,
                        readiness.ContractHash,
                        ReviewedCandidateId = readiness.Candidate.Id,
                        readiness.Contract.RequiredWaiverRiskIds
                    })
                });
            }
            _lifecycle.OpenWaiverReview(
                flow,
                readiness.State,
                readiness.Record.CandidateFingerprint,
                readiness.Candidate.CandidateFingerprint);
            flow.OutcomeLabel = "Needs customer waiver";
        }
        else
        {
            var blocked = readiness.State == DeliveryReadinessState.Blocked;
            _lifecycle.Transition(
                flow,
                blocked ? FlowStatus.Blocked : FlowStatus.WaitingForFeedback);
            flow.OutcomeLabel = blocked ? "Blocked" : "Needs refinement";
            flow.CurrentBlockerCode = blocked
                ? "delivery.readiness-blocked"
                : "delivery.readiness-needs-refinement";
            flow.CurrentBlockerSummary =
                $"Host-derived Delivery readiness is '{readiness.State}' at revision {readiness.Revision}.";
            flow.CustomerBlockerMessage = blocked
                ? "This result is blocked: at least one acceptance criterion or residual risk blocks release."
                : "This result needs refinement before it can be reviewed for acceptance.";
            flow.CurrentBlockerDataJson = JsonSerializer.Serialize(new
            {
                State = readiness.State.ToString(),
                readiness.Revision,
                readiness.ContractHash,
                FailedCriteria = readiness.Contract.Criteria
                    .Where(item => item.Outcome == DeliveryCriterionOutcome.Failed)
                    .Select(item => item.CriterionId)
                    .ToArray(),
                BlockedCriteria = readiness.Contract.Criteria
                    .Where(item => item.Outcome == DeliveryCriterionOutcome.Blocked)
                    .Select(item => item.CriterionId)
                    .ToArray(),
                readiness.Contract.Diagnostics
            });
        }

        flow.OutcomeUrl = string.Empty;
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        if (!await database.FlowEvents.AnyAsync(
                item =>
                    item.FlowRunId == flow.Id &&
                    item.Type == "flow.readiness-not-ready" &&
                    item.FlowStepId == owner.Id,
                cancellationToken))
        {
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = owner.Id,
                Type = "flow.readiness-not-ready",
                Message =
                    $"No ordinary customer review was created because host-derived readiness is '{readiness.State}'.",
                DataJson = JsonSerializer.Serialize(new
                {
                    State = readiness.State.ToString(),
                    readiness.Revision,
                    readiness.ContractHash
                })
            });
        }
        await database.SaveChangesAsync(cancellationToken);
    }

    private static void ApplyGateResolution(
        HandoffGateRecord source,
        HandoffGateRecord destination)
    {
        destination.Resolved = source.Resolved;
        destination.Approved = source.Approved;
        destination.ResolvedBy = source.ResolvedBy;
        destination.ResolutionNote = source.ResolutionNote;
        destination.ResolvedAt = source.ResolvedAt;
        destination.ReviewDecision = source.ReviewDecision;
    }

    /// <summary>
    /// The host-owned inputs a Delivery verification turn needs: the exact acceptance plan hash it
    /// must echo, the criteria it must cover, and the evidence identifiers it may cite.
    /// </summary>
    internal sealed record DeliveryVerificationAssignment(
        string PlanHash,
        DeliveryAcceptancePlan Plan,
        IReadOnlyList<DeliveryEvidenceItem> Evidence,
        string CurrentStepEvidencePrefix,
        string? VerificationPreviewUrl = null);

    /// <summary>
    /// Durably records the host-issued evidence identifiers for one plan step. It is idempotent, so
    /// a restart or a retry never mints a second registry entry for the same observations.
    /// </summary>
    private static async Task RecordDeliveryEvidenceAsync(
        HarnessDbContext database,
        FlowRun flow,
        FlowStep step,
        IReadOnlyList<AgentToolCall> toolCalls,
        CancellationToken cancellationToken)
    {
        var events = await database.FlowEvents
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.FlowStepId == step.Id &&
                item.Type == DeliveryReadinessService.EvidenceEventType)
            .ToListAsync(cancellationToken);
        var issued = DeliveryReadinessService.ReadStepEvidence(
            events,
            step.Iteration,
            step.Id);
        if (issued is not null && toolCalls.Count == 0)
        {
            return;
        }
        var entry = DeliveryReadinessService.BuildEvidence(
            step,
            toolCalls,
            issued?.Sequence);
        var data = DeliveryReadinessService.SerializeEvidence(entry);
        if (issued is not null)
        {
            if (string.Equals(
                    DeliveryReadinessService.SerializeEvidence(issued),
                    data,
                    StringComparison.Ordinal))
            {
                return;
            }
            if (issued.Items.Count > 1)
            {
                throw new InvalidOperationException(
                    "Host-issued evidence is immutable. The recorded observations for this " +
                    "attempt cannot be replaced or reordered.");
            }
        }
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = step.Id,
            Type = DeliveryReadinessService.EvidenceEventType,
            Message =
                $"Issued {entry.Items.Count} host-owned evidence identifier(s) for plan step '{step.PlanStepKey}'.",
            DataJson = data
        });
    }

    private static AgentToolCall[] ToObservedToolCalls(
        Guid stepId,
        AgentExecutionResult result) =>
        [.. result.ToolCalls.Select(toolCall => new AgentToolCall
        {
            FlowStepId = stepId,
            ToolName = toolCall.ToolName,
            ArgumentsSummary = toolCall.ArgumentsSummary,
            Succeeded = toolCall.Succeeded,
            ToolType = toolCall.ToolType,
            NormalizedCommand = toolCall.NormalizedCommand,
            NormalizedArguments = toolCall.NormalizedArguments,
            WorkingDirectory = toolCall.WorkingDirectory,
            ExitCode = toolCall.ExitCode,
            ResultDigest = toolCall.ResultDigest,
            ResultSummary = toolCall.ResultSummary
        })];

    /// <summary>
    /// Prepares the verification turn: pre-issues the step's own evidence identifier before
    /// dispatch and loads the durable acceptance plan and evidence registry that the prompt must
    /// carry. A Delivery verification turn is never dispatched without a planned criterion set.
    /// </summary>
    private async Task<DeliveryVerificationAssignment?>
        PrepareDeliveryVerificationAssignmentAsync(
            HarnessDbContext database,
            FlowRun flow,
            FlowStep step,
            CancellationToken cancellationToken)
    {
        if (!DeliveryReadinessService.AppliesTo(flow) ||
            !IsDeliveryVerificationStep(step))
        {
            return null;
        }
        var completedSteps = await database.FlowSteps
            .AsNoTracking()
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.Iteration == flow.Iteration &&
                item.PlanStage == PlanStage.BeforeReview &&
                item.InvocationKind == ExecutionInvocationKind.Worker &&
                item.Status == StepStatus.Completed)
            .OrderBy(item => item.Sequence)
            .ToListAsync(cancellationToken);
        foreach (var completedStep in completedSteps)
        {
            if (await database.FlowEvents.AnyAsync(
                    item =>
                        item.FlowRunId == flow.Id &&
                        item.FlowStepId == completedStep.Id &&
                        item.Type == DeliveryReadinessService.EvidenceEventType,
                    cancellationToken))
            {
                continue;
            }
            var toolCalls = await database.AgentToolCalls
                .AsNoTracking()
                .Where(item => item.FlowStepId == completedStep.Id)
                .OrderBy(item => item.Id)
                .ToListAsync(cancellationToken);
            await RecordDeliveryEvidenceAsync(
                database,
                flow,
                completedStep,
                toolCalls,
                cancellationToken);
        }
        await RecordDeliveryEvidenceAsync(database, flow, step, [], cancellationToken);
        await database.SaveChangesAsync(cancellationToken);

        var events = await database.FlowEvents
            .Where(item =>
                item.FlowRunId == flow.Id &&
                (item.Type == DeliveryReadinessService.AcceptancePlanEventType ||
                 item.Type == DeliveryReadinessService.EvidenceEventType))
            .ToListAsync(cancellationToken);
        var (plan, planHash, planErrors) =
            DeliveryReadinessService.TryReadAcceptancePlan(events, flow.Iteration);
        if (plan is null)
        {
            throw new InvalidOperationException(
                "A Delivery verification turn cannot be dispatched without planned acceptance " +
                "criteria: " + string.Join("; ", planErrors));
        }
        return new DeliveryVerificationAssignment(
            planHash,
            plan,
            DeliveryReadinessService.ReadEvidence(events, flow.Iteration),
            $"EV-S{Math.Max(
                DeliveryReadinessService.ReadStepEvidence(
                    events,
                    step.Iteration,
                    step.Id)?.Sequence ?? step.Sequence, 0):000}-",
            server is null
                ? null
                : PreviewArtifactCatalog.VerificationBaseUrl(
                    flow.Id,
                    server.Features.Get<IServerAddressesFeature>()?.Addresses
                        .OrderBy(address =>
                            !address.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                        .FirstOrDefault()
                    ?? throw new InvalidOperationException(
                        "Studio must be listening before dispatching Delivery verification.")));
    }

    /// <summary>
    /// Idempotently completes a Delivery flow whose publication finished before the crash. It
    /// reauthorizes the current readiness, the accepted review, and the publication journal
    /// binding, and denies safely (leaving the flow unapproved) when any of them no longer agree.
    /// </summary>
    private async Task<bool> TryCompleteRecoveredDeliveryAsync(
        HarnessDbContext database,
        FlowRun flow,
        HandoffGateRecord accepted,
        FlowStep publication,
        CancellationToken cancellationToken)
    {
        if (!DeliveryReadinessService.AppliesTo(flow))
        {
            _lifecycle.Transition(flow, FlowStatus.Approved);
            return true;
        }

        DeliveryReadinessBinding? binding;
        string? loadFailure = null;
        try
        {
            binding = await _readiness.LoadCurrentAsync(
                database,
                flow.Id,
                cancellationToken);
        }
        catch (DeliveryReadinessConflictException exception)
        {
            binding = null;
            loadFailure = exception.Message;
        }
        var journal = await database.ReviewedPublicationRecords
            .Where(item => item.FlowRunId == flow.Id)
            .ToListAsync(cancellationToken);
        var denial =
            loadFailure ??
            (binding is null
                ? "no current readiness assessment exists"
                : binding.State != DeliveryReadinessState.ReadyToApprove
                    ? $"the current readiness state is '{binding.State}'"
                    : journal.Any(item =>
                        item.Stage != ReviewedPublicationStage.Completed ||
                        item.ReviewedCandidateId != binding.Candidate.Id ||
                        item.ReadinessSnapshotId != binding.Record.Id ||
                        item.CustomerReviewGateId != accepted.Id ||
                        !string.Equals(
                            item.ReadinessContractHash,
                            binding.ContractHash,
                            StringComparison.Ordinal))
                        ? "the publication journal is not bound to the current readiness assessment"
                        : null);
        if (denial is not null)
        {
            database.FlowEvents.Add(DeliveryReadinessService.DenialEvent(
                flow.Id,
                publication.Id,
                DeliveryReadinessConflicts.PublicationNotAuthorized,
                "Restart reconciliation refused final approval because " + denial + ".",
                binding));
            return false;
        }

        _lifecycle.CompletePublishedDelivery(
            flow,
            binding!.State,
            binding.Record.CandidateFingerprint,
            binding.Candidate.CandidateFingerprint,
            publicationVerified: true);
        return true;
    }

    private async Task MarkWaitingForReviewAsync(
        Guid flowId,
        CancellationToken cancellationToken)
    {
        await using var lifecycleLease =
            await _lifecycle.EnterAsync(flowId, cancellationToken);
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var flow = await database.Flows
            .AsSplitQuery()
            .Include(item => item.Steps)
            .Include(item => item.GateRecords)
            .Include(item => item.Events)
            .SingleAsync(item => item.Id == flowId, cancellationToken);

        if (string.IsNullOrWhiteSpace(flow.OutcomeOwnerPlanStepKey))
        {
            throw new InvalidOperationException(
                "A planned Studio flow has no outcome-owner plan step.");
        }
        var owner = flow.Steps
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.Iteration == flow.Iteration &&
                item.PlanStepKey == flow.OutcomeOwnerPlanStepKey &&
                item.IsOutcomeOwner &&
                item.Status == StepStatus.Completed)
            .OrderByDescending(item => item.Sequence)
            .ThenByDescending(item => item.Attempt)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "A Studio flow cannot enter review before its outcome owner completes.");
        if (string.IsNullOrWhiteSpace(flow.OutcomeContractJson))
        {
            throw new InvalidOperationException(
                "The final outcome owner must return a flow outcome before customer review.");
        }
        var advisoryConfig =
            workflowProvider.GetEffective().Config.Studio.Advisory;
        if (flow.Kind == FlowKind.Delivery)
        {
            _ = OutcomeTypeRules.RequireDelivery(
                flow.Outcome,
                nameof(flow.Outcome));
        }
        var normalizedOutcome = flow.Kind switch
        {
            FlowKind.Advisory => FlowOutcomeParser.ParseJson(
                flow.OutcomeContractJson,
                advisoryConfig.MaxArtifactCount,
                advisoryConfig.MaxTotalArtifactBytes),
            FlowKind.Delivery => FlowOutcomeParser.ParseJson(
                flow.OutcomeContractJson),
            _ => throw new InvalidOperationException(
                $"Unknown flow kind '{flow.Kind}'.")
        };
        ReviewedCandidateIdentity? sealedIdentity = null;
        if (flow.Kind == FlowKind.Delivery)
        {
            try
            {
                var reviewedIdentity =
                    ReviewedCandidateLedger.TryRead(flow, owner.Id);
                if (reviewedIdentity is null)
                {
                    reviewedIdentity = await (_reviewedCandidates
                            ?? throw new InvalidOperationException(
                                "No reviewed candidate sealing service is configured."))
                        .SealAsync(
                            flow,
                            owner.Id,
                            owner.PlanStepKey,
                            normalizedOutcome.RawJson,
                            cancellationToken);
                    database.FlowEvents.Add(new FlowEvent
                    {
                        FlowRunId = flow.Id,
                        FlowStepId = owner.Id,
                        Type = ReviewedCandidateLedger.EventType,
                        Message =
                            $"Sealed the exact Delivery candidate {reviewedIdentity.Fingerprint} before customer review.",
                        DataJson = ReviewedCandidateLedger.Serialize(
                            reviewedIdentity)
                    });
                }
                else
                {
                    _ = await (_reviewedCandidates
                            ?? throw new InvalidOperationException(
                                "No reviewed candidate verification service is configured."))
                        .VerifyAsync(
                            flow,
                            reviewedIdentity,
                            cancellationToken);
                }
                sealedIdentity = reviewedIdentity;
            }
            catch (Exception exception) when (
                exception is CandidateValidationException or
                    InvalidOperationException or IOException or
                    UnauthorizedAccessException)
            {
                owner.Status = StepStatus.Failed;
                owner.Phase = AgentRunPhase.Failed;
                owner.PushbackReason = ClipText(
                    exception.GetBaseException().Message,
                    4_000);
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = owner.Id,
                    Type = "delivery.review-candidate-seal-failed",
                    Message =
                        "The Delivery result was rejected before customer review because its exact product identity could not be sealed.",
                    DataJson = JsonSerializer.Serialize(new
                    {
                        Error = ClipText(
                            exception.GetBaseException().Message,
                            4_000)
                    })
                });
                await database.SaveChangesAsync(cancellationToken);
                throw;
            }
        }
        AdvisoryOutcomeMaterialization? advisoryMaterialization = null;
        if (flow.Kind == FlowKind.Advisory)
        {
            try
            {
                var persistedMaterialization =
                    _advisoryArtifacts.TryReadCurrentMaterialization(flow);
                if (persistedMaterialization is null)
                {
                    advisoryMaterialization =
                        await _advisoryArtifacts.VerifyAndWriteAsync(
                            flow,
                            normalizedOutcome,
                            cancellationToken);
                }
                else
                {
                    advisoryMaterialization =
                        new AdvisoryOutcomeMaterialization(
                            await _advisoryArtifacts.VerifyBaselineAsync(
                                flow,
                                cancellationToken),
                            _advisoryArtifacts.Discover(flow),
                            persistedMaterialization);
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                    IOException or UnauthorizedAccessException)
            {
                owner.Status = StepStatus.Failed;
                owner.Phase = AgentRunPhase.Failed;
                owner.PushbackReason = ClipText(
                    exception.GetBaseException().Message,
                    4_000);
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = owner.Id,
                    Type = "advisory.source-verification-failed",
                    Message =
                        "The Advisory result was rejected before customer review because the guarded source or artifact boundary changed.",
                    DataJson = JsonSerializer.Serialize(new
                    {
                        Error = ClipText(
                            exception.GetBaseException().Message,
                            4_000)
                    })
                });
                await database.SaveChangesAsync(cancellationToken);
                throw;
            }
            if (_advisoryArtifacts.TryReadCurrentMaterialization(flow) is null)
            {
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = owner.Id,
                    Type = "advisory.source-verified",
                    Message =
                        "Verified the guarded Advisory source snapshot byte-for-byte before opening customer review.",
                    DataJson = JsonSerializer.Serialize(new
                    {
                        advisoryMaterialization.Verification.BaselineDigest,
                        advisoryMaterialization.Verification.VerifiedDigest,
                        advisoryMaterialization.Verification.FileCount,
                        advisoryMaterialization.Verification.TotalBytes,
                        advisoryMaterialization.Verification.VerifiedAt
                    })
                });
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = owner.Id,
                    Type = AdvisoryArtifactCatalog.MaterializationEventType,
                    Message =
                        $"Materialized {advisoryMaterialization.Artifacts.Count} validated Advisory artifact(s) for iteration {flow.Iteration}.",
                    DataJson =
                        _advisoryArtifacts.SerializeMaterialization(
                            advisoryMaterialization.Policy)
                });
            }
        }
        var currentStepIds = flow.Steps
            .Where(step => step.Iteration == flow.Iteration)
            .Select(step => step.Id)
            .ToHashSet();
        DeliveryReadinessBinding? readiness = null;
        if (flow.Kind == FlowKind.Delivery)
        {
            readiness = await _readiness.DeriveAndPersistAsync(
                database,
                flow,
                sealedIdentity
                ?? throw new InvalidOperationException(
                    "A Delivery review requires a sealed reviewed candidate."),
                cancellationToken);
        }
        var reviews = flow.GateRecords
            .Where(gate =>
                gate.ActionType == HandoffActionType.CustomerReview &&
                currentStepIds.Contains(gate.FlowStepId))
            .OrderBy(gate => gate.DecidedAt)
            .ToList();
        if (reviews.Count(gate => !gate.Resolved) > 1)
        {
            throw new InvalidOperationException(
                "The current Studio iteration has duplicate unresolved customer reviews.");
        }
        var accepted = reviews.LastOrDefault(gate =>
            gate.Resolved &&
            gate.Approved == true &&
            gate.ReviewDecision == ReviewDecision.Accepted);
        if (accepted is not null)
        {
            FlowStep? publication = null;
            if (flow.Kind == FlowKind.Delivery)
            {
                publication = flow.Steps
                    .Where(step =>
                        step.Iteration == flow.Iteration &&
                        step.Status == StepStatus.Completed &&
                        ReviewCoordinator.IsPublicationStep(flow, step))
                    .OrderByDescending(step => step.Sequence)
                    .ThenByDescending(step => step.Attempt)
                    .FirstOrDefault()
                    ?? throw new InvalidOperationException(
                        "Delivery acceptance is durable, but its planned publication has not completed verification.");
            }
            if (flow.Kind == FlowKind.Delivery)
            {
                var current = readiness
                    ?? throw new InvalidOperationException(
                        "Delivery approval requires a current readiness assessment.");
                // The durable publication journal is the host's own record of remote side
                // effects. Whenever rows exist they must all be completed and bound to this
                // exact readiness, candidate, and accepted-review identity; a mismatch denies
                // final approval instead of silently trusting the accepted gate.
                var journal = await database
                    .ReviewedPublicationRecords
                    .Where(item => item.FlowRunId == flow.Id)
                    .ToListAsync(cancellationToken);
                var verified =
                    journal.Count == 0 ||
                    journal.All(item =>
                        item.Stage == ReviewedPublicationStage.Completed &&
                        item.ReviewedCandidateId == current.Candidate.Id &&
                        item.ReadinessSnapshotId == current.Record.Id &&
                        item.CustomerReviewGateId == accepted.Id &&
                        string.Equals(
                            item.ReadinessContractHash,
                            current.ContractHash,
                            StringComparison.Ordinal));
                if (!verified)
                {
                    database.FlowEvents.Add(DeliveryReadinessService.DenialEvent(
                        flow.Id,
                        publication?.Id ?? owner.Id,
                        DeliveryReadinessConflicts.PublicationNotAuthorized,
                        "Final approval was refused because the reviewed publication journal is not bound to the current readiness assessment.",
                        current));
                    await database.SaveChangesAsync(cancellationToken);
                    throw new DeliveryReadinessConflictException(
                        DeliveryReadinessConflicts.PublicationNotAuthorized,
                        "The reviewed publication journal is not bound to the current readiness assessment.",
                        current.State,
                        current.Revision,
                        current.ContractHash);
                }
                _lifecycle.CompletePublishedDelivery(
                    flow,
                    current.State,
                    current.Record.CandidateFingerprint,
                    current.Candidate.CandidateFingerprint,
                    publicationVerified: true);
            }
            else
            {
                _lifecycle.Transition(flow, FlowStatus.Approved);
            }
            flow.CompletedAt = DateTimeOffset.UtcNow;
            flow.UpdatedAt = DateTimeOffset.UtcNow;
            var approvalStepId = publication?.Id ?? owner.Id;
            if (!await database.FlowEvents.AnyAsync(
                    item =>
                        item.FlowRunId == flow.Id &&
                        item.Type == "flow.approved" &&
                        item.FlowStepId == approvalStepId,
                    cancellationToken))
            {
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = approvalStepId,
                    Type = "flow.approved",
                    Message = publication is null
                        ? "Customer accepted the Advisory result."
                        : $"Customer-approved Delivery publication was verified: {flow.OutcomeLabel}."
                });
            }
            await database.SaveChangesAsync(cancellationToken);
            return;
        }

        if (readiness is not null &&
            readiness.State is not DeliveryReadinessState.ReadyToApprove)
        {
            await OpenNonReadyDeliveryStateAsync(
                database,
                flow,
                owner,
                readiness,
                normalizedOutcome,
                cancellationToken);
            return;
        }

        var unresolved = reviews.SingleOrDefault(gate => !gate.Resolved);
        if (unresolved is null)
        {
            var review = handoffGate.SubmitProposal(new HandoffProposal
            {
                FlowRunId = flow.Id,
                FlowStepId = owner.Id,
                ActionType = HandoffActionType.CustomerReview,
                Summary = normalizedOutcome.Document.Summary,
                Evidence = BuildOutcomeReviewEvidence(
                    normalizedOutcome.Document),
                BlastRadius = HandoffBlastRadius.High
            });
            if (review.Decision !=
                HandoffGateDecision.AwaitingHumanApproval)
            {
                throw new InvalidOperationException(
                    "CustomerReview must always produce a human-gated decision.");
            }
            flow.GateRecords.Add(review);
            database.Entry(review).State = EntityState.Added;
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = owner.Id,
                Type = "gate.customer-review-created",
                Message = readiness is null
                    ? "Harness created the durable generic customer-review gate."
                    : $"Harness opened the ordinary customer review from readiness revision {readiness.Revision} (ReadyToApprove).",
                DataJson = readiness is null
                    ? null
                    : JsonSerializer.Serialize(new
                    {
                        SnapshotId = readiness.Record.Id,
                        readiness.Revision,
                        readiness.ContractHash,
                        ReviewedCandidateId = readiness.Candidate.Id,
                        readiness.Record.CandidateFingerprint
                    })
            });
        }
        if (readiness is null)
        {
            _lifecycle.Transition(flow, FlowStatus.WaitingForFeedback);
        }
        else
        {
            _lifecycle.OpenCustomerReview(
                flow,
                readiness.State,
                readiness.Record.CandidateFingerprint,
                readiness.Candidate.CandidateFingerprint);
        }
        flow.OutcomeUrl = flow.Kind == FlowKind.Advisory
            ? $"#/preview/{flow.Id}"
            : string.Empty;
        flow.OutcomeLabel = flow.Kind == FlowKind.Advisory
            ? "Advisory result ready"
            : "Customer review ready";
        if (flow.Kind == FlowKind.Advisory)
        {
            flow.Outcome = OutcomeType.None;
        }
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        if (!await database.FlowEvents.AnyAsync(
                item =>
                    item.FlowRunId == flow.Id &&
                    item.Type == "flow.review-ready" &&
                    item.FlowStepId == owner.Id,
                cancellationToken))
        {
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = owner.Id,
                Type = "flow.review-ready",
                Message = "The dynamic outcome owner completed; the result is ready for customer review."
            });
        }
        await database.SaveChangesAsync(cancellationToken);
        return;
    }


    private async Task MarkFailedAsync(
        Guid flowId,
        string failureReason,
        CancellationToken cancellationToken)
    {
        await using var lifecycleLease =
            await _lifecycle.EnterAsync(flowId, cancellationToken);
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var flow = await database.Flows.SingleOrDefaultAsync(
            item => item.Id == flowId,
            cancellationToken);
        if (flow is null)
        {
            return;
        }
        if (flow.Status is
            FlowStatus.Blocked or
            FlowStatus.Abandoning or
            FlowStatus.Abandoned or
            FlowStatus.Approved)
        {
            return;
        }

        var pendingSteps = await database.FlowSteps
            .Where(step =>
                step.FlowRunId == flowId &&
                step.Status == StepStatus.Pending)
            .ToListAsync(cancellationToken);
        database.FlowEvents.AddRange(
            ApplyFlowFailure(flow, pendingSteps, failureReason, _lifecycle));
        await database.SaveChangesAsync(cancellationToken);
    }

    internal static IReadOnlyList<FlowEvent> ApplyFlowFailure(
        FlowRun flow,
        IReadOnlyList<FlowStep> pendingSteps,
        string failureReason,
        FlowLifecycleCoordinator? lifecycle = null)
    {
        var stoppedAt = DateTimeOffset.UtcNow;
        var events = new List<FlowEvent>(pendingSteps.Count + 1);
        (lifecycle ?? new FlowLifecycleCoordinator())
            .Transition(flow, FlowStatus.Failed);
        flow.FailureReason = failureReason;
        flow.UpdatedAt = stoppedAt;
        foreach (var pendingStep in pendingSteps)
        {
            pendingStep.Status = StepStatus.Skipped;
            pendingStep.Phase = AgentRunPhase.Failed;
            pendingStep.CompletedAt = stoppedAt;
            events.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = pendingStep.Id,
                Type = "step.skipped",
                Message = $"{pendingStep.AgentName} was skipped because the flow stopped."
            });
        }
        events.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            Type = "flow.failed",
            Message = failureReason
        });
        return events;
    }

    private static AgentExecutionResult ToRecoveredExecutionResult(
        AgentRunResult result,
        Guid sessionId) =>
        new(
            result.OutputSummary,
            $"Recovered from completed Copilot session {sessionId:D}.",
            1,
            result.ToolCalls);

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalMinutes >= 1
            ? $"{duration.TotalMinutes:0.0} min"
            : $"{Math.Max(1, duration.TotalSeconds):0.0} sec";

    private sealed record InterruptedStepCandidate(
        Guid StepId,
        Guid FlowId,
        int Iteration,
        string AgentId,
        string AgentName,
        string AgentRole,
        string PlanStepKey,
        string WorkspacePath,
        DateTimeOffset? StartedAt,
        Guid? CopilotSessionId,
        string CopilotSessionHome,
        bool IsPreMortemRevision,
        string ExecutionPrompt,
        string WorkflowRevision,
        ExecutionInvocationKind InvocationKind,
        bool IsOutcomeOwner,
        FlowKind FlowKind);

    private sealed record StagedSessionCleanupCandidate(
        Guid SessionId,
        string CopilotHome,
        StepStatus StepStatus,
        AgentRunPhase Phase,
        FlowStatus FlowStatus);

    private sealed class CompletedJournalContractException(
        string message,
        string output) : InvalidOperationException(message)
    {
        public string Output { get; } = output;
    }

    private sealed record StagedStepCompletion(
        FlowStep Step,
        HandoffGateRecord GateRecord,
        bool PushedBack,
        bool ContractInvalid,
        long ElapsedMilliseconds,
        int ExecutionAttempts,
        IReadOnlyList<HandoffGateRecord> PreparedGateUpdates);

}

public interface IFlowExecutionController
{
    Task<bool> CancelAsync(
        Guid flowId,
        CancellationToken cancellationToken = default);
}

public interface IFlowRecoveryController
{
    Task RecoverAsync(
        Guid flowId,
        CancellationToken cancellationToken = default);
}

public sealed class FlowWorker :
    BackgroundService,
    IFlowExecutionController,
    IFlowRecoveryController
{
    private readonly FlowQueue _queue;
    private readonly Func<Guid, CancellationToken, Task> _runFlowAsync;
    private readonly Func<Guid, CancellationToken, Task>? _prepareFlowAsync;
    private readonly Func<Guid, CancellationToken, Task>? _recoverFlowAsync;
    private readonly Func<CancellationToken, Task<IReadOnlyList<Guid>>>
        _recoverFlowsAsync;
    private readonly Func<Guid, CancellationToken, Task<bool>> _isRunnableAsync;
    private readonly ILogger<FlowWorker> _logger;
    private readonly Lock _stateLock = new();
    private readonly Dictionary<Guid, FlowDispatchState> _states = [];
    private readonly HashSet<Guid> _blocked = [];
    private readonly HashSet<Guid> _recovering = [];
    private bool _stopping;

    public FlowWorker(
        FlowQueue queue,
        WorkflowEngine engine,
        ILogger<FlowWorker> logger,
        IDemoRuntimeRevoker? demoRuntimeRevoker = null)
        : this(
            queue,
            engine.RunAsync,
            engine.RecoverInterruptedFlowsAsync,
            engine.IsRunnableAsync,
            logger,
            demoRuntimeRevoker is null
                ? null
                : (flowId, cancellationToken) =>
                    demoRuntimeRevoker.RevokeFlowAsync(
                        flowId,
                        "Live demos were revoked by the execution preflight.",
                        cancellationToken),
            engine.RecoverActiveFlowAsync)
    {
    }

    internal FlowWorker(
        FlowQueue queue,
        Func<Guid, CancellationToken, Task> runFlowAsync,
        Func<CancellationToken, Task<IReadOnlyList<Guid>>> recoverFlowsAsync,
        Func<Guid, CancellationToken, Task<bool>> isRunnableAsync,
        ILogger<FlowWorker> logger,
        Func<Guid, CancellationToken, Task>? prepareFlowAsync = null,
        Func<Guid, CancellationToken, Task>? recoverFlowAsync = null)
    {
        _queue = queue;
        _runFlowAsync = runFlowAsync;
        _recoverFlowsAsync = recoverFlowsAsync;
        _isRunnableAsync = isRunnableAsync;
        _logger = logger;
        _prepareFlowAsync = prepareFlowAsync;
        _recoverFlowAsync = recoverFlowAsync;
    }

    internal bool HasPendingRerun(Guid flowId)
    {
        lock (_stateLock)
        {
            return _states.TryGetValue(flowId, out var state) &&
                   state.PendingRerun;
        }
    }

    internal bool IsRegistered(Guid flowId)
    {
        lock (_stateLock)
        {
            return _states.ContainsKey(flowId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var recoveredFlowIds = await _recoverFlowsAsync(stoppingToken);
        foreach (var flowId in recoveredFlowIds)
        {
            _queue.Queue(flowId);
        }

        await foreach (var flowId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            _queue.MarkDequeued();
            DispatchOrMarkPending(flowId, stoppingToken);
        }
    }

    public async Task<bool> CancelAsync(
        Guid flowId,
        CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? source;
        Task? task;
        lock (_stateLock)
        {
            _blocked.Add(flowId);
            if (!_states.TryGetValue(flowId, out var state))
            {
                return false;
            }

            source = state.Cancellation;
            task = state.ActiveTask;
        }

        source?.Cancel();
        if (task is not null)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (OperationCanceledException) when (
                source?.IsCancellationRequested == true)
            {
                // Expected when abandonment cancels a running flow.
            }
            catch (TimeoutException exception)
            {
                throw new InvalidOperationException(
                    $"Flow {flowId} did not stop within the abandonment deadline.",
                    exception);
            }
        }
        return source is not null;
    }

    public async Task RecoverAsync(
        Guid flowId,
        CancellationToken cancellationToken = default)
    {
        if (!await _isRunnableAsync(flowId, cancellationToken))
        {
            throw new InvalidOperationException(
                "Only a queued, running, or reworking flow can be recovered.");
        }

        CancellationTokenSource? source;
        Task? task;
        lock (_stateLock)
        {
            if (_stopping)
            {
                throw new InvalidOperationException(
                    "Flow recovery is unavailable while the worker is stopping.");
            }
            if (_blocked.Contains(flowId))
            {
                throw new InvalidOperationException(
                    "A flow being abandoned cannot be recovered.");
            }
            if (!_recovering.Add(flowId))
            {
                throw new InvalidOperationException(
                    $"Flow {flowId} recovery is already in progress.");
            }

            if (_states.TryGetValue(flowId, out var state))
            {
                source = state.Cancellation;
                task = state.ActiveTask;
            }
            else
            {
                source = null;
                task = null;
            }
        }

        try
        {
            source?.Cancel();
            if (task is not null)
            {
                try
                {
                    await task.WaitAsync(
                        TimeSpan.FromSeconds(30),
                        CancellationToken.None);
                }
                catch (OperationCanceledException) when (
                    source?.IsCancellationRequested == true)
                {
                    // The interrupted attempt remains durable and is reconciled below.
                }
                catch (TimeoutException exception)
                {
                    throw new InvalidOperationException(
                        $"Flow {flowId} did not stop within the recovery deadline.",
                        exception);
                }
            }

            await (_recoverFlowAsync
                   ?? throw new InvalidOperationException(
                       "No active-flow recovery operation is configured."))(
                flowId,
                CancellationToken.None);
        }
        finally
        {
            lock (_stateLock)
            {
                _recovering.Remove(flowId);
            }
        }

        if (!_queue.Queue(flowId))
        {
            throw new InvalidOperationException(
                $"Flow {flowId} was recovered but could not be queued.");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource[] sources;
        Task[] tasks;
        lock (_stateLock)
        {
            _stopping = true;
            sources = _states.Values
                .Select(state => state.Cancellation)
                .Where(source => source is not null)
                .Cast<CancellationTokenSource>()
                .ToArray();
            tasks = _states.Values
                .Select(state => state.ActiveTask)
                .Where(task => task is not null)
                .Cast<Task>()
                .ToArray();
        }
        foreach (var source in sources)
        {
            source.Cancel();
        }
        await base.StopAsync(cancellationToken);
        try
        {
            await Task.WhenAll(tasks).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Shutdown deadline elapsed while factory flows were stopping.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "One or more in-flight factory flows did not stop cleanly.");
        }
    }

    private void DispatchOrMarkPending(
        Guid flowId,
        CancellationToken stoppingToken)
    {
        Task? task = null;
        CancellationTokenSource? source = null;
        FlowDispatchState? state = null;
        lock (_stateLock)
        {
            if (_stopping ||
                _blocked.Contains(flowId) ||
                _recovering.Contains(flowId))
            {
                return;
            }
            if (_states.TryGetValue(flowId, out state))
            {
                state.PendingRerun = true;
                return;
            }

            state = new FlowDispatchState();
            _states.Add(flowId, state);
            (task, source) = StartExecutionLocked(
                flowId,
                state,
                stoppingToken);
        }

        _logger.LogInformation(
            "Dispatched flow {FlowId} from the durable queue.",
            flowId);
        _ = ObserveAsync(flowId, state, task, source, stoppingToken);
    }

    private (Task Task, CancellationTokenSource Source) StartExecutionLocked(
        Guid flowId,
        FlowDispatchState state,
        CancellationToken stoppingToken)
    {
        var source =
            CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task task;
        try
        {
            task = RunPreparedAsync(flowId, source.Token);
        }
        catch (Exception exception)
        {
            task = Task.FromException(exception);
        }
        state.Cancellation = source;
        state.ActiveTask = task;
        return (task, source);
    }

    private async Task RunPreparedAsync(
        Guid flowId,
        CancellationToken cancellationToken)
    {
        if (_prepareFlowAsync is not null)
        {
            try
            {
                await _prepareFlowAsync(flowId, cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Flow {FlowId} execution preflight failed; durable work will be retried.",
                    flowId);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                if (!_queue.Queue(flowId))
                {
                    throw new InvalidOperationException(
                        $"Flow {flowId} execution preflight failed and could not be re-queued.",
                        exception);
                }
                return;
            }
        }

        await _runFlowAsync(flowId, cancellationToken);
    }

    private async Task ObserveAsync(
        Guid flowId,
        FlowDispatchState state,
        Task task,
        CancellationTokenSource source,
        CancellationToken stoppingToken)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "Factory flow {FlowId} execution was canceled.",
                flowId);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Factory flow {FlowId} escaped the execution boundary.",
                flowId);
        }
        finally
        {
            var recheck = false;
            lock (_stateLock)
            {
                if (_states.TryGetValue(flowId, out var current) &&
                    ReferenceEquals(current, state))
                {
                    state.ActiveTask = null;
                    state.Cancellation = null;
                    if (_stopping ||
                        stoppingToken.IsCancellationRequested ||
                        _blocked.Contains(flowId) ||
                        _recovering.Contains(flowId))
                    {
                        _states.Remove(flowId);
                    }
                    else if (state.PendingRerun)
                    {
                        state.PendingRerun = false;
                        recheck = true;
                    }
                    else
                    {
                        _states.Remove(flowId);
                    }
                }
            }
            source.Dispose();
            if (recheck)
            {
                await RecheckAndDispatchAsync(
                    flowId,
                    state,
                    stoppingToken);
            }
        }
    }

    private async Task RecheckAndDispatchAsync(
        Guid flowId,
        FlowDispatchState state,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool runnable;
            try
            {
                runnable = await _isRunnableAsync(
                    flowId,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (
                stoppingToken.IsCancellationRequested)
            {
                RemoveRecheckingState(flowId, state);
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Could not re-check durable state for pending factory flow {FlowId}.",
                    flowId);
                try
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(250),
                        stoppingToken);
                    continue;
                }
                catch (OperationCanceledException) when (
                    stoppingToken.IsCancellationRequested)
                {
                    RemoveRecheckingState(flowId, state);
                    return;
                }
            }

            Task? task = null;
            CancellationTokenSource? source = null;
            var checkAgain = false;
            lock (_stateLock)
            {
                if (!_states.TryGetValue(flowId, out var current) ||
                    !ReferenceEquals(current, state))
                {
                    return;
                }
                if (_stopping ||
                    stoppingToken.IsCancellationRequested ||
                    _blocked.Contains(flowId) ||
                    _recovering.Contains(flowId))
                {
                    _states.Remove(flowId);
                    return;
                }
                if (runnable)
                {
                    // Every queue item observed while the prior execution was active or
                    // while this durable-state check was in flight is represented by this
                    // single dispatch.
                    state.PendingRerun = false;
                    (task, source) = StartExecutionLocked(
                        flowId,
                        state,
                        stoppingToken);
                }
                else if (state.PendingRerun)
                {
                    // A later queue item may correspond to a durable transition committed
                    // after the preceding query. Re-read instead of dropping that signal.
                    state.PendingRerun = false;
                    checkAgain = true;
                }
                else
                {
                    _states.Remove(flowId);
                }
            }

            if (task is not null && source is not null)
            {
                _ = ObserveAsync(
                    flowId,
                    state,
                    task,
                    source,
                    stoppingToken);
                return;
            }
            if (!checkAgain)
            {
                return;
            }
        }

        RemoveRecheckingState(flowId, state);
    }

    private void RemoveRecheckingState(
        Guid flowId,
        FlowDispatchState state)
    {
        lock (_stateLock)
        {
            if (_states.TryGetValue(flowId, out var current) &&
                ReferenceEquals(current, state) &&
                current.ActiveTask is null)
            {
                _states.Remove(flowId);
            }
        }
    }

    private sealed class FlowDispatchState
    {
        public Task? ActiveTask { get; set; }

        public CancellationTokenSource? Cancellation { get; set; }

        public bool PendingRerun { get; set; }
    }
}

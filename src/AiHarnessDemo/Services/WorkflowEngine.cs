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
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

public sealed class WorkflowEngine(
    IDbContextFactory<HarnessDbContext> databaseFactory,
    AgentCatalog agentCatalog,
    FlowPlanner planner,
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
    OutcomeVerificationContextBuilder? outcomeContextBuilder = null,
    IVerifiedCandidatePublisher? candidatePublisher = null,
    FlowAgentSnapshotService? flowAgentSnapshotService = null,
    TeamPlanValidator? teamPlanValidator = null,
    AdvisoryArtifactCatalog? advisoryArtifactCatalog = null,
    MissingQualificationCoordinator? missingQualificationCoordinator = null,
    FlowLifecycleCoordinator? lifecycleCoordinator = null,
    PermissionProfileResolver? permissionProfileResolver = null,
    IReviewedCandidateService? reviewedCandidateService = null,
    ReviewCoordinator? reviewCoordinator = null,
    LinkedFlowCoordinator? linkedFlowCoordinator = null,
    AgentManifestStager? manifestStager = null)
{
    internal const string ReleaseCandidateLabel = "Prepare customer release candidate";
    internal const string ApprovedPublicationLabel = "Publish customer-approved outcome";
    internal const string OutcomeCorrectionLabelPrefix = "Correct outcome after QA round ";
    internal const string OutcomePlanCorrectionLabelPrefix =
        "Correct outcome acceptance plan after QA round ";
    internal const string OutcomeCandidateRefreshLabelPrefix =
        "Refresh outcome candidate after QA round ";
    internal const string OutcomeQaLabelPrefix = "Verify outcome candidate (round ";
    internal const string PreMortemRole = "pre-mortem-sceptic";
    internal const string TeamLeadPlanStepKey = "team-plan";
    internal const string RefinementIntakePlanStepKey =
        "account-manager:refinement";
    internal const int MaximumQaContractErrorCharacters = 4_000;
    internal const int MaximumFailedOutputCharacters = 32_000;
    private const string ManualRestartLabelPrefix = "Manual restart of ";
    private const string StudioContractCorrectionLabelPrefix =
        "Correct invalid response from ";
    internal const string ReleaseCandidateAssignment =
        "Prepare the local outcome candidate for independent QA. Generate every browser artifact " +
        "under .customer-preview, leave every repository ready for host sealing from the actual " +
        "working-tree product bytes, and do not create commits, branches, tags, remotes, pushes, " +
        "or pull requests before explicit customer approval. Exclude " +
        ".ai-harness\\outcome-verification from product commits, rerun release-critical checks, " +
        "and report the repository scope plus release evidence. The harness records the final " +
        "sealed HEAD and tree identity for every repository.";
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
    private readonly bool _legacyCatalogCompatibility =
        flowAgentSnapshotService is null;
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
    private readonly ReviewCoordinator? _reviewCoordinator =
        reviewCoordinator;
    private readonly LinkedFlowCoordinator? _linkedFlows =
        linkedFlowCoordinator;
    private readonly AgentManifestStager _manifestStager =
        manifestStager ?? new AgentManifestStager();
    private readonly ConcurrentDictionary<Guid, byte> _executionClaims = new();
    private int _activeFlows;

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
            try
            {
                using var executionSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    handoffGate.KillSwitchToken);
                await RunCoreAsync(flowId, executionSource.Token);
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
                lock (_concurrencyLock)
                {
                    _activeFlows--;
                }
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
            if (flow.ContractVersion == "legacy-v1" &&
                !string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson))
            {
                var outcome = OutcomeVerificationRules.DeserializeAggregate(
                    flow.OutcomeVerificationJson);
                if (outcome.Iteration != flow.Iteration)
                {
                    throw new InvalidOperationException(
                        "The outcome-verification aggregate does not match the active flow iteration.");
                }
                if (outcome.Status == OutcomeVerificationStatus.NotStarted)
                {
                    outcome.Status = OutcomeVerificationStatus.Planning;
                    outcome.UpdatedAt = DateTimeOffset.UtcNow;
                    flow.OutcomeVerificationJson =
                        OutcomeVerificationRules.SerializeAggregate(outcome);
                    database.FlowEvents.Add(new FlowEvent
                    {
                        FlowRunId = flow.Id,
                        Type = "outcome.planning",
                        Message =
                            "Team Lead is converting the confirmed brief into independently verifiable acceptance criteria."
                    });
                }
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

        var workspaceInvocation = ExecutionInvocationKind.Worker;
        if (flow.ContractVersion == "studio-v2")
        {
            await using var invocationDatabase =
                await databaseFactory.CreateDbContextAsync(cancellationToken);
            workspaceInvocation = await invocationDatabase.FlowSteps
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
        }
        var workspace = await workspaceManager.PrepareForInvocationAsync(
            flow,
            workspaceInvocation,
            cancellationToken);
        await UpdateWorkspaceAsync(flowId, workspace, cancellationToken);

        if (flow.ContractVersion == "studio-v2")
        {
            await RunStudioV2Async(
                flow,
                workspace.Path,
                cancellationToken);
            return;
        }
        if (!UsesStaticPlanner(flow.ContractVersion))
        {
            throw new InvalidOperationException(
                $"Unsupported flow contract version '{flow.ContractVersion}'.");
        }

        IReadOnlyList<AgentRecord> availableAgents;
        if (_legacyCatalogCompatibility)
        {
            await agentCatalog.LoadAsync(cancellationToken);
            availableAgents = agentCatalog.List()
                .Where(item =>
                    item.DefinitionStatus == AgentDefinitionStatus.Valid)
                .ToList();
        }
        else
        {
            availableAgents = await _flowAgentSnapshots.GetAgentsAsync(
                flowId,
                cancellationToken);
        }
        var plan = planner.Plan(flow.ConsolidatedRequest, availableAgents);
        var complexity = planner.CalculateComplexity(flow.ConsolidatedRequest);
        var planSummary = string.Join(
            " -> ",
            plan.Select(item => item.Agent.Name));
        if (!string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson))
        {
            foreach (var requiredRole in new[]
                     {
                         "team-lead",
                         "release-engineer",
                         "quality-engineer"
                     })
            {
                if (!plan.Any(item =>
                        string.Equals(
                            item.Agent.Role,
                            requiredRole,
                            StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"Outcome verification requires an enabled {requiredRole} agent.");
                }
            }
        }
        var upstreamOwners = BuildUpstreamOwners(plan);
        var maxPreMortemRounds = await GetMaxHandoffRetriesAsync(cancellationToken);
        var preMortemAgent = availableAgents.SingleOrDefault(
            item => item.Enabled && item.Id == PreMortemRole);
        var preMortemAvailable = preMortemAgent is not null && maxPreMortemRounds > 0;

        var lead = plan.FirstOrDefault(item => item.Agent.Id == "team-lead");
        if (lead is null)
        {
            throw new InvalidOperationException(
                "Team Lead must be enabled because downstream task profiles cannot be silently synthesized.");
        }
        var materializeInitialGraph =
            string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson) ||
            !await HasCompleteInitialOutcomeGraphAsync(
                flow.Id,
                flow.Iteration,
                cancellationToken);
        if (materializeInitialGraph)
        {
            var initialDeliveryStepIds = new List<Guid>();
            var leadStepId = await AddStepAsync(
                flow,
                lead,
                sequence: 10,
                label: string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson)
                    ? "Plan the delivery system"
                    : "Define acceptance plan and delivery system",
                cancellationToken,
                inputSummary:
                    "Plan delivery for the fixed downstream role sequence and emit one validated " +
                    "task profile for each role: " +
                    string.Join(
                        ", ",
                        plan.Where(item => item.Agent.Role != "team-lead")
                            .Select(item => item.Agent.Role)) +
                    $".{Environment.NewLine}{Environment.NewLine}" +
                    (preMortemAvailable
                        ? $"The Pre-mortem Sceptic is available. Select any justified checkpoints " +
                          $"for {flow.ModelSelectionStrategy}; each selected checkpoint permits at most " +
                          $"{maxPreMortemRounds} total sceptic round(s)."
                        : "The Pre-mortem Sceptic is unavailable because it is disabled or the configured round limit is zero. Return an empty AfterRoles array."),
                kind: string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson)
                    ? FlowStepKind.Standard
                    : FlowStepKind.OutcomePlan,
                planDuties: [PlanDuty.Analyze, PlanDuty.Design],
                invocationKind: ExecutionInvocationKind.Planning,
                workflowRevision: workflowProvider.GetEffective().Revision);
            leadStepId = await ResolveEffectiveManualRetryStepIdAsync(
                leadStepId,
                cancellationToken);
            await EnsureBootstrapProfileAsync(
                flow,
                lead.Agent.Role,
                leadStepId,
                cancellationToken);
            FlowStep leadResult;
            if (await ShouldExecuteStepAsync(leadStepId, cancellationToken))
            {
                leadResult = await ExecuteWithPushbackRecoveryAsync(
                    flow,
                    flowId,
                    leadStepId,
                    workspace.Path,
                    planSummary,
                    complexity,
                    upstreamOwners,
                    cancellationToken);
            }
            else
            {
                await using var leadDatabase =
                    await databaseFactory.CreateDbContextAsync(cancellationToken);
                leadResult = await leadDatabase.FlowSteps
                    .AsNoTracking()
                    .SingleAsync(item => item.Id == leadStepId, cancellationToken);
            }
            var preMortemCheckpoints = await EnsureDownstreamProfilesAsync(
                flow,
                lead,
                leadResult,
                plan.Where(item => item.Agent.Role != "team-lead")
                    .Select(item => item.Agent.Role)
                    .ToArray(),
                workspace.Path,
                planSummary,
                complexity,
                upstreamOwners,
                preMortemAvailable,
                cancellationToken);

            var orderedDeliveryPlan = plan
                .Where(item => item.Agent.Role != "team-lead")
                .ToList();
            var activeOutcomePlanHash = string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson)
                ? null
                : await GetActiveOutcomePlanHashAsync(flow.Id, cancellationToken);
            var latestTeamLeadSequence =
                await GetLatestCompletedAgentSequenceAsync(
                    flow.Id,
                    flow.Iteration,
                    lead.Agent.Id,
                    cancellationToken);
            var sequence = FirstDeliverySequence(latestTeamLeadSequence);
            var dependencyStepId = leadResult.Id;
            foreach (var planned in orderedDeliveryPlan)
            {
                var preparesReleaseCandidate =
                    planned.Agent.Role == "release-engineer";
                if (preparesReleaseCandidate &&
                    await HasCompletedReleaseCandidateAsync(
                        flow.Id,
                        flow.Iteration,
                        cancellationToken))
                {
                    sequence += 10;
                    continue;
                }
                var deliveryStepId = await AddStepAsync(
                    flow,
                    planned,
                    sequence,
                    preparesReleaseCandidate
                        ? ReleaseCandidateLabel
                        : planned.Agent.Role == "quality-engineer" &&
                          !string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson)
                        ? "Verify outcome candidate (round 1)"
                        : $"Execute {planned.Agent.Name} contract",
                    cancellationToken,
                    inputSummary: AppendOutcomeAssignment(
                        preparesReleaseCandidate
                            ? ReleaseCandidateAssignment
                            : planned.Reason,
                        await BuildOutcomeAssignmentAsync(
                            flow.Id,
                            planned.Agent.Role,
                            cancellationToken)),
                    kind: !string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson)
                        ? preparesReleaseCandidate
                            ? FlowStepKind.OutcomeLocalReleaseCandidate
                            : planned.Agent.Role == "quality-engineer"
                                ? FlowStepKind.OutcomeQa
                                : FlowStepKind.OutcomeDelivery
                        : FlowStepKind.Standard,
                    outcomeQaRound: !string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson) &&
                        planned.Agent.Role == "quality-engineer"
                        ? 1
                        : null,
                    outcomePlanHash: activeOutcomePlanHash,
                    dependsOnStepId: dependencyStepId);
                initialDeliveryStepIds.Add(deliveryStepId);
                dependencyStepId = deliveryStepId;
                sequence += 10;

                if (!preMortemCheckpoints.Contains(planned.Agent.Role))
                {
                    continue;
                }
                if (preMortemAgent is null)
                {
                    throw new InvalidOperationException(
                        "Team Lead selected a pre-mortem checkpoint without an enabled Pre-mortem Sceptic.");
                }
                dependencyStepId = await AddPreMortemReviewStepAsync(
                    flow,
                    preMortemAgent,
                    deliveryStepId,
                    deliveryStepId,
                    sequence,
                    round: 1,
                    cancellationToken);
                sequence += 10;
            }
            if (!string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson))
            {
                await RecordInitialOutcomeGraphAsync(
                    flow.Id,
                    leadResult,
                    initialDeliveryStepIds,
                    cancellationToken);
            }
        }

        await AddEventAsync(
            flowId,
            null,
            "plan.selected",
            $"Team sequence selected: {planSummary}.",
            cancellationToken);
        await ExecutePendingCausalRetriesAsync(
            flow,
            workspace.Path,
            planSummary,
            complexity,
            upstreamOwners,
            cancellationToken);
        await RecoverUnresolvedPushbacksAsync(
            flow,
            workspace.Path,
            planSummary,
            complexity,
            upstreamOwners,
            cancellationToken);
        await ThrowIfUnresolvedFailureAsync(
            flow.Id,
            flow.Iteration,
            cancellationToken);
        await RecoverUnresolvedPreMortemsAsync(
            flow,
            cancellationToken);
        await ReconcileCompletedOutcomeArtifactsAsync(
            flow.Id,
            cancellationToken);

        while (true)
        {
            while (await GetNextPendingStepIdAsync(
                       flowId,
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
                    workspace.Path,
                    planSummary,
                    complexity,
                    upstreamOwners,
                    cancellationToken);
            }

            await ThrowIfUnresolvedFailureAsync(
                flow.Id,
                flow.Iteration,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson) ||
                !await ReconcileOutcomeVerificationAsync(
                    flow,
                    plan,
                    cancellationToken))
            {
                break;
            }
        }

        await MarkWaitingForReviewAsync(flowId, cancellationToken);
    }

    internal static bool UsesStaticPlanner(string contractVersion) =>
        string.Equals(
            contractVersion,
            "legacy-v1",
            StringComparison.Ordinal);

    private async Task RunStudioV2Async(
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
                "studio-v2 planning requires an immutable flow agent snapshot.");
        }

        var teamLeadSnapshot = snapshots.SingleOrDefault(snapshot =>
            snapshot.EnabledAtSnapshot &&
            string.Equals(snapshot.AgentId, "team-lead", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "studio-v2 planning requires the enabled Team Lead flow snapshot.");
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
            if (!string.Equals(
                    persistedDocument.Version,
                    TeamPlanParser.Version,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The persisted studio-v2 plan version '{persistedDocument.Version}' is unsupported.");
            }
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
        var legacyOwners =
            new Dictionary<string, AgentRecord>(StringComparer.Ordinal);

        await ExecutePendingCausalRetriesAsync(
            flow,
            workspacePath,
            planSummary,
            complexity,
            legacyOwners,
            cancellationToken);
        await RecoverUnresolvedPushbacksAsync(
            flow,
            workspacePath,
            planSummary,
            complexity,
            legacyOwners,
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
                legacyOwners,
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
            var normalized = IntakeV2Parser.ParseJson(alreadyNormalizedBrief);
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
                "studio-v2 refinement requires the enabled Account Manager flow snapshot.");
        var accountManager = new PlannedAgent(
            SnapshotAgent(accountManagerSnapshot),
            "Normalize the customer's requested refinement before replanning.");
        var assignment = $$"""
            Normalize the customer's explicit refinement request into the complete brief for the
            next iteration. Preserve the flow kind {{flow.Kind}} and all still-applicable accepted
            scope. The customer has explicitly requested this refinement, so return Confirmed;
            this is not acceptance of the final outcome. Do not ask another question unless the
            supplied request has no actionable customer outcome.

            The current refinement is intentionally the first section of the supplied customer
            task. Treat it as authoritative over the reviewed outcome and previous confirmed brief
            that follow it. Never replace it with an older refinement or omit its requested changes.

            Return exactly one intake-v2 document. Use the existing task title, FlowKind
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
            accountManager.Agent.Role,
            stepId,
            cancellationToken,
            RefinementIntakePlanStepKey,
            accountManager.Agent.Id);

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
        var response = IntakeV2Parser.Parse(normalizedStep.OutputSummary);
        if (response.Document.Status != IntakeV2Status.Confirmed ||
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
                    DataJson = IntakeV2Parser.Serialize(response.Document)
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
        var lead = new PlannedAgent(
            SnapshotAgent(teamLeadSnapshot),
            "Select the smallest suitable downstream team.");
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
            lead.Agent.Role,
            leadStepId,
            cancellationToken,
            TeamLeadPlanStepKey,
            lead.Agent.Id);

        var noLegacyOwners =
            new Dictionary<string, AgentRecord>(StringComparer.Ordinal);
        var leadResult = await LoadOrExecuteStepAsync(
            flow,
            leadStepId,
            workspacePath,
            "Dynamic Team Lead planning",
            complexity: 8,
            noLegacyOwners,
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
                    "Your previous team-plan-v1 result was invalid. Resume the same Team Lead " +
                    "session and return a complete replacement under 10,000 characters. Start with " +
                    "exactly one HANDOFF_STATUS: COMPLETE line, followed by the complete document " +
                    "between the exact TEAM_PLAN_V1 " +
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
                lead.Agent.Role,
                correctionStepId,
                cancellationToken,
                TeamLeadPlanStepKey,
                lead.Agent.Id);
            await AddEventOnceAsync(
                flow.Id,
                correctionStepId,
                "plan.validation-correction",
                "Team Lead returned an invalid team-plan-v1 result. One bounded correction turn was scheduled with the exact validation errors.",
                cancellationToken);
            var correction = await LoadOrExecuteStepAsync(
                flow,
                correctionStepId,
                workspacePath,
                "Dynamic Team Lead planning correction",
                complexity: 8,
                noLegacyOwners,
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
                    "Team Lead returned an invalid corrected team-plan-v1 result.",
                    cancellationToken);
                await AddEventOnceAsync(
                    flow.Id,
                    correction.Id,
                    "plan.validation-failed",
                    "Team Lead returned an invalid team-plan-v1 result on the correction turn: " +
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
                Version = TeamPlanParser.Version,
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
                "An accepted studio-v2 plan has no completed Team Lead source step.");
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
                        IsOnlyPlannedPublishStep: true,
                        ContractVersion: flow.ContractVersion,
                        LegacyPublicationAuthorized: false,
                        IsGovernedOutcomeVerification: false),
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
                            DeferredPermissionSnapshot.CurrentVersion,
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
                ExecutionInvocationKind.ReviewClassification or
                ExecutionInvocationKind.BlockerExplanation =>
                ExecutionPermissionProfile.ReadOnlySource,
            _ when flow.ContractVersion == "studio-v2" =>
                InitialPermissionProfile(flow.Kind, stage, duties),
            _ => ExecutionPermissionProfile.WorkspaceWrite
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
                $"The confirmed brief exceeds the {CopilotReasoningHost.MaximumPlanningBriefCharacters}-character intake-v2/promotion bound. Team Lead planning will not truncate it.");
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
            """{"Version":"team-plan-v1","Disposition":"Planned","Steps":[{"Id":"inspect-current-product","AgentId":"exact-roster-id","Order":1,"Stage":"BeforeReview","Assignment":"Complete, bounded assignment including the expected handoff.","Justification":"Why this exact agent and step are needed.","DependsOn":[],"Duties":["Analyze"],"OutcomeOwner":false,"TaskProfile":{"Complexity":5,"ReasoningDepth":5,"ContextDemand":5,"ToolIntensity":3,"TaskTypeTags":["Design"],"Risk":"Low","RiskReason":"Nonempty bounded reason.","Confidence":0.8,"Rationales":["Nonempty bounded rationale."]}}],"PreMortemCheckpoints":[],"MissingQualification":null}""";
        var assignment = $$"""
            Create the dynamic downstream plan for this flow.

            Flow kind: {{flow.Kind}}
            Confirmed brief:
            {{flow.ConsolidatedRequest}}

            Exact enabled optional snapshot roster (Id, Name, Description):
            {{rosterJson}}

            Select the smallest suitable team. Use only exact roster Id values. An AgentId may be
            selected in multiple distinct plan steps, but every step Id must be unique. If the
            enabled roster cannot safely satisfy the brief, return MissingQualification instead
            of inventing an agent. Workers receive the confirmed brief plus only their declared
            current-iteration dependencies and ancestors; never assign a worker to reconstruct
            an earlier iteration or inspect a full execution ledger.

            Return exactly one strict JSON object between TEAM_PLAN_V1_BEGIN and
            TEAM_PLAN_V1_END. Version is exactly team-plan-v1. Disposition is Planned or
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
            and TEAM_PLAN_V1 delimiters plus the entire JSON document are never transport-truncated.
            Assignment and Justification must be nonempty and bounded. Order values are positive
            and dependencies name only lower-order steps. Duties use exact values Analyze, Design,
            Implement, Verify, PrepareOutcome, and Publish. Stage is BeforeReview or AfterApproval.
            TaskProfile uses 1-10 integer metrics, 1-6 exact TaskTypeTags, exact Low/Medium/High/
            Critical Risk, bounded RiskReason and Rationales, and Confidence from 0 through 1.

            Exactly one final BeforeReview worker is OutcomeOwner and has PrepareOutcome.
            Required configured duties: {{requiredDuties}}.
            {{(flow.Kind == FlowKind.Advisory
                ? "Advisory requires at least one worker and PrepareOutcome, and forbids Implement, Publish, and AfterApproval."
                : "Delivery requires Implement, Verify, and PrepareOutcome before review plus exactly one AfterApproval Publish-only step. Never Publish before review.")}}
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
                    Version = "advisory-policy-v1",
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

    internal async Task ReconcileCompletedOutcomeArtifactsAsync(
        Guid flowId,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var flow = await database.Flows
            .SingleAsync(item => item.Id == flowId, cancellationToken);
        if (string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson))
        {
            return;
        }

        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        var steps = await database.FlowSteps
            .Include(item => item.ToolCalls)
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.Iteration == flow.Iteration)
            .OrderBy(item => item.Sequence)
            .ThenBy(item => item.Attempt)
            .ToListAsync(cancellationToken);
        foreach (var step in steps.Where(item =>
                     item.Status == StepStatus.Completed &&
                     IsGovernedOutcomeEvidenceStep(item)))
        {
            try
            {
                CollectOutcomeEvidence(
                    database,
                    flow,
                    step,
                    step.OutputSummary,
                    step.CompletedAt ?? DateTimeOffset.UtcNow);
                CompleteOutcomeCorrection(
                    database,
                    flow,
                    step,
                    step.CompletedAt ?? DateTimeOffset.UtcNow);
            }
            catch (OutcomeVerificationValidationException exception)
            {
                ApplyContractValidationFailure(step);
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = step.Id,
                    Type = "outcome.evidence.recovery-failed",
                    Message =
                        "Could not recover strict delivery evidence: " +
                        string.Join("; ", exception.Errors)
                });
                await database.SaveChangesAsync(cancellationToken);
                throw;
            }
        }

        state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        var expectedPlanGapRound = state.Status ==
                                   OutcomeVerificationStatus.Correcting
            ? state.Rounds
                .Where(round =>
                    !round.Stale &&
                    round.Result?.PlanGaps.Count > 0 &&
                    string.Equals(
                        round.AcceptancePlanHash,
                        state.AcceptancePlan?.Hash,
                        StringComparison.Ordinal))
                .OrderByDescending(round => round.Round)
                .FirstOrDefault()
            : null;
        var planGapCorrection = expectedPlanGapRound is null
            ? null
            : steps
                .Where(item =>
                    item.Status == StepStatus.Completed &&
                    IsOutcomePlanCorrectionStep(item) &&
                    item.OutcomeQaRound == expectedPlanGapRound.Round &&
                    string.Equals(
                        item.OutcomePlanHash,
                        expectedPlanGapRound.AcceptancePlanHash,
                        StringComparison.Ordinal) &&
                    !state.ProcessedSemanticRootIds.Contains(
                        GetStableSemanticRootId(item)))
                .OrderBy(item => item.Sequence)
                .ThenBy(item => item.Attempt)
                .FirstOrDefault();
        if (planGapCorrection is not null)
        {
            await ApplyAcceptancePlanReplacementAsync(
                database,
                flow,
                planGapCorrection,
                planGapCorrection.OutputSummary,
                planGapCorrection.CompletedAt ?? DateTimeOffset.UtcNow,
                cancellationToken);
        }

        state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        if (state.Status is
                OutcomeVerificationStatus.CollectingEvidence or
                OutcomeVerificationStatus.PreparingCandidate or
                OutcomeVerificationStatus.AwaitingCandidateRefresh)
        {
            var expectedCandidateKind =
                state.Status == OutcomeVerificationStatus.AwaitingCandidateRefresh
                    ? FlowStepKind.OutcomeCandidateRefresh
                    : FlowStepKind.OutcomeLocalReleaseCandidate;
            int? expectedRound = state.Status ==
                                 OutcomeVerificationStatus.AwaitingCandidateRefresh
                ? state.Rounds.OrderByDescending(round => round.Round)
                    .Select(round => (int?)round.Round)
                    .FirstOrDefault() ?? 0
                : null;
            var release = steps
                .Where(item =>
                    item.Status == StepStatus.Completed &&
                    item.Kind == expectedCandidateKind &&
                    item.OutcomeQaRound == expectedRound &&
                    string.Equals(
                        item.OutcomePlanHash,
                        state.AcceptancePlan?.Hash,
                        StringComparison.Ordinal) &&
                    !state.ProcessedSemanticRootIds.Contains(
                        GetStableSemanticRootId(item)))
                .OrderByDescending(item => item.Sequence)
                .ThenByDescending(item => item.Attempt)
                .FirstOrDefault();
            if (release is not null)
            {
                await PrepareOutcomeCandidateAsync(
                    database,
                    flow,
                    release,
                    cancellationToken);
            }
        }

        state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        if (state.ActiveQaStepId is { } activeQaStepId &&
            !state.Rounds.Any(round => round.QaStepId == activeQaStepId))
        {
            var completedQa = steps.SingleOrDefault(item =>
                item.Id == activeQaStepId &&
                item.Status == StepStatus.Completed);
            if (completedQa is not null)
            {
                await ProcessOutcomeQaCompletionAsync(
                    database,
                    flow,
                    completedQa,
                    completedQa.OutputSummary,
                    completedQa.CompletedAt ?? DateTimeOffset.UtcNow,
                    completedQa.ToolCalls.Select(call => new ToolCallRecord(
                        call.ToolName,
                        call.ArgumentsSummary,
                        call.Succeeded,
                        call.ToolType,
                        call.NormalizedCommand,
                        call.NormalizedArguments,
                        call.WorkingDirectory,
                        call.ExitCode,
                        call.ResultDigest,
                        call.ResultSummary)).ToArray(),
                    cancellationToken);
            }
        }

        await database.SaveChangesAsync(cancellationToken);
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

    private async Task<bool> HasCompletedReleaseCandidateAsync(
        Guid flowId,
        int iteration,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        return await database.FlowSteps.AnyAsync(
            item =>
                item.FlowRunId == flowId &&
                item.Iteration == iteration &&
                item.Kind == FlowStepKind.OutcomeLocalReleaseCandidate &&
                item.Status == StepStatus.Completed,
            cancellationToken);
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
        PlannedAgent planned,
        int sequence,
        string label,
        CancellationToken cancellationToken,
        int attempt = 1,
        string? inputSummary = null,
        FlowStepKind kind = FlowStepKind.Standard,
        int? outcomeQaRound = null,
        string? outcomePlanHash = null,
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
                item.AgentId == planned.Agent.Id &&
                item.Attempt == attempt &&
                (planStepKey == string.Empty ||
                 item.PlanStepKey == planStepKey));
        var existing = kind == FlowStepKind.Standard &&
                       outcomeQaRound is null &&
                       string.IsNullOrWhiteSpace(outcomePlanHash) &&
                       retryOfStepId is null
            ? await existingQuery.SingleOrDefaultAsync(
                item => item.Label == label,
                cancellationToken)
            : await existingQuery.SingleOrDefaultAsync(
                item =>
                    item.Kind == kind &&
                    item.OutcomeQaRound == outcomeQaRound &&
                    item.RetryOfStepId == retryOfStepId &&
                    item.OutcomePlanHash ==
                    (string.IsNullOrWhiteSpace(outcomePlanHash)
                        ? string.Empty
                        : outcomePlanHash),
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
            AgentId = planned.Agent.Id,
            AgentName = planned.Agent.Name,
            AgentRole = planned.Agent.Role,
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
            Kind = kind,
            Status = StepStatus.Pending,
            Attempt = attempt,
            InputSummary = inputSummary ?? planned.Reason,
            RetryOfStepId = retryOfStepId,
            DependsOnStepId = dependsOnStepId,
            OutcomeQaRound = outcomeQaRound,
            OutcomePlanHash = outcomePlanHash ?? string.Empty,
            StableSemanticRootId = stableSemanticRootId
        };
        step.StableSemanticRootId ??= retryOfStepId ?? step.Id;
        database.FlowSteps.Add(step);
        await database.SaveChangesAsync(cancellationToken);
        return step.Id;
    }

    private async Task<string?> GetActiveOutcomePlanHashAsync(
        Guid flowId,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var json = await database.Flows
            .AsNoTracking()
            .Where(item => item.Id == flowId)
            .Select(item => item.OutcomeVerificationJson)
            .SingleAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return OutcomeVerificationRules.DeserializeAggregate(json)
            .AcceptancePlan?.Hash;
    }

    private async Task<bool> HasCompleteInitialOutcomeGraphAsync(
        Guid flowId,
        int iteration,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var json = await database.Flows
            .AsNoTracking()
            .Where(item => item.Id == flowId)
            .Select(item => item.OutcomeVerificationJson)
            .SingleAsync(cancellationToken);
        var state = OutcomeVerificationRules.DeserializeAggregate(json);
        if (state.AcceptancePlan is null ||
            state.InitialPlanSemanticRootId is null ||
            state.PlannedRoles.Count == 0 ||
            state.InitialDeliverySemanticRootIds.Count != state.PlannedRoles.Count)
        {
            return false;
        }

        var expectedRoots = state.InitialDeliverySemanticRootIds
            .Append(state.InitialPlanSemanticRootId.Value)
            .ToHashSet();
        var persistedRoots = await database.FlowSteps
            .AsNoTracking()
            .Where(step =>
                step.FlowRunId == flowId &&
                step.Iteration == iteration &&
                step.StableSemanticRootId != null &&
                expectedRoots.Contains(step.StableSemanticRootId.Value))
            .Select(step => step.StableSemanticRootId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        return persistedRoots.Count == expectedRoots.Count;
    }

    private async Task RecordInitialOutcomeGraphAsync(
        Guid flowId,
        FlowStep leadStep,
        IReadOnlyCollection<Guid> deliveryStepIds,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var flow = await database.Flows.SingleAsync(
            item => item.Id == flowId,
            cancellationToken);
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        var requestedIds = deliveryStepIds.ToHashSet();
        var deliveryRoots = await database.FlowSteps
            .AsNoTracking()
            .Where(step => requestedIds.Contains(step.Id))
            .OrderBy(step => step.Sequence)
            .Select(step => step.StableSemanticRootId ?? step.Id)
            .ToListAsync(cancellationToken);
        var leadRoot = leadStep.StableSemanticRootId ??
                       leadStep.RetryOfStepId ??
                       leadStep.Id;
        if (state.InitialPlanSemanticRootId is { } existingLeadRoot &&
            existingLeadRoot != leadRoot)
        {
            throw new InvalidOperationException(
                "The initial outcome graph cannot change its Team Lead semantic root.");
        }
        if (deliveryRoots.Count != state.PlannedRoles.Count)
        {
            throw new InvalidOperationException(
                "The initial outcome graph does not contain one immutable delivery root per planned role.");
        }

        state.InitialPlanSemanticRootId = leadRoot;
        state.InitialDeliverySemanticRootIds = deliveryRoots
            .Distinct()
            .ToList();
        MarkSemanticRootProcessed(state, leadRoot);
        state.UpdatedAt = DateTimeOffset.UtcNow;
        flow.OutcomeVerificationJson =
            OutcomeVerificationRules.SerializeAggregate(state);
        await database.SaveChangesAsync(cancellationToken);
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
                    (item.InvocationKind ==
                         ExecutionInvocationKind.PreMortem ||
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
                cancellationToken);
        sourceProfile ??= await database.TaskProfiles
            .AsNoTracking()
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.Iteration == flow.Iteration &&
                item.FlowStepId == null &&
                item.Role == target.AgentRole &&
                (flow.ContractVersion == "legacy-v1" ||
                 item.PlanStepKey == target.PlanStepKey))
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
            PlanStepKey = flow.ContractVersion == "studio-v2"
                ? PreMortemPlanStepKey(target.PlanStepKey, round)
                : string.Empty,
            PlanDutiesJson = SerializePlanDuties(
                [PlanDuty.Analyze, PlanDuty.Verify]),
            PlanStage = PlanStage.BeforeReview,
            InvocationKind = ExecutionInvocationKind.PreMortem,
            IsOutcomeOwner = false,
            PermissionProfile =
                ExecutionPermissionProfile.PreMortemReadOnly,
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

    private async Task<IReadOnlySet<string>> EnsureDownstreamProfilesAsync(
        FlowRun flow,
        PlannedAgent lead,
        FlowStep leadResult,
        IReadOnlyList<string> expectedRoles,
        string workspacePath,
        string planSummary,
        int complexity,
        IReadOnlyDictionary<string, AgentRecord> upstreamOwners,
        bool preMortemAvailable,
        CancellationToken cancellationToken)
    {
        var governed = !string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson);
        await using (var check =
                     await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            var existingProfiles = await check.TaskProfiles
                .AsNoTracking()
                .Where(item =>
                    item.FlowRunId == flow.Id &&
                    item.Iteration == flow.Iteration &&
                    expectedRoles.Contains(item.Role))
                .ToListAsync(cancellationToken);
            var existingRoleSet = existingProfiles
                .Select(item => item.Role)
                .ToHashSet(StringComparer.Ordinal);
            var hasAcceptancePlan = !governed;
            if (governed)
            {
                var storedOutcomeJson = await check.Flows
                    .AsNoTracking()
                    .Where(item => item.Id == flow.Id)
                    .Select(item => item.OutcomeVerificationJson)
                    .SingleAsync(cancellationToken);
                hasAcceptancePlan = OutcomeVerificationRules
                    .DeserializeAggregate(storedOutcomeJson)
                    .AcceptancePlan is not null;
            }
            if (existingRoleSet.SetEquals(expectedRoles) && hasAcceptancePlan)
            {
                return SelectEnabledPreMortemCheckpoints(
                    existingProfiles,
                    preMortemAvailable);
            }
        }

        TeamLeadContract contract;
        var contractSource = leadResult;
        try
        {
            contract = ParseTeamLeadContract(
                leadResult.OutputSummary,
                expectedRoles,
                flow.Id,
                flow.Iteration,
                preMortemAvailable,
                governed);
        }
        catch (TaskProfileValidationException firstFailure)
        {
            await observationRecorder.RecordCompletionAsync(
                leadResult.Id,
                accepted: false,
                leadResult.DurationMilliseconds,
                Math.Max(1, leadResult.ExecutionAttempts),
                "invalid-task-profile",
                cancellationToken);
            var validationErrors = string.Join(
                Environment.NewLine,
                firstFailure.Errors.Select(error => $"- {error}"));
            var correctionStepId = await AddStepAsync(
                flow,
                lead,
                sequence: FirstCorrectionSequence(leadResult.Sequence),
                label: "Correct Team Lead task profiles",
                cancellationToken,
                attempt: 2,
                inputSummary:
                    "Your previous Team Lead contract was invalid. Resume the same session and " +
                    "return every corrected sentinel-delimited JSON document. Exact validation errors:" +
                    Environment.NewLine +
                    validationErrors,
                kind: governed
                    ? FlowStepKind.OutcomePlan
                    : FlowStepKind.Standard,
                retryOfStepId: governed
                    ? GetRetryRootId(leadResult)
                    : null,
                stableSemanticRootId: governed
                    ? GetStableSemanticRootId(leadResult)
                    : null,
                planDuties: [PlanDuty.Analyze, PlanDuty.Design],
                invocationKind: ExecutionInvocationKind.Planning,
                workflowRevision: workflowProvider.GetEffective().Revision);
            correctionStepId = await ResolveEffectiveManualRetryStepIdAsync(
                correctionStepId,
                cancellationToken);
            await AddEventAsync(
                flow.Id,
                correctionStepId,
                "profile.validation-correction",
                "Team Lead profiles or pre-mortem checkpoints were invalid. One correction turn was scheduled with the exact validation errors.",
                cancellationToken);
            FlowStep correction;
            if (await ShouldExecuteStepAsync(correctionStepId, cancellationToken))
            {
                correction = await ExecuteWithPushbackRecoveryAsync(
                    flow,
                    flow.Id,
                    correctionStepId,
                    workspacePath,
                    planSummary,
                    complexity,
                    upstreamOwners,
                    cancellationToken);
            }
            else
            {
                await using var correctionDatabase =
                    await databaseFactory.CreateDbContextAsync(cancellationToken);
                correction = await correctionDatabase.FlowSteps
                    .AsNoTracking()
                    .SingleAsync(item => item.Id == correctionStepId, cancellationToken);
            }

            try
            {
                contract = ParseTeamLeadContract(
                    correction.OutputSummary,
                    expectedRoles,
                    flow.Id,
                    flow.Iteration,
                    preMortemAvailable,
                    governed);
                contractSource = correction;
            }
            catch (TaskProfileValidationException secondFailure)
            {
                await observationRecorder.RecordCompletionAsync(
                    correction.Id,
                    accepted: false,
                    correction.DurationMilliseconds,
                    Math.Max(1, correction.ExecutionAttempts),
                    "invalid-task-profile",
                    cancellationToken);
                await MarkContractValidationFailedAsync(
                    correction.Id,
                    "Team Lead returned an invalid corrected profile, pre-mortem, or acceptance contract.",
                    cancellationToken);
                await AddEventAsync(
                    flow.Id,
                    correctionStepId,
                    "profile.validation-failed",
                    "Team Lead returned invalid task profiles on the correction turn: " +
                    string.Join("; ", secondFailure.Errors),
                    cancellationToken);
                throw new InvalidOperationException(
                    "Team Lead contract remained invalid after one correction: " +
                    string.Join("; ", secondFailure.Errors),
                    secondFailure);
            }
        }

        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var existingProfileRoles = await database.TaskProfiles
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.Iteration == flow.Iteration &&
                expectedRoles.Contains(item.Role))
            .Select(item => item.Role)
            .ToListAsync(cancellationToken);
        database.TaskProfiles.AddRange(contract.Profiles.Where(
            profile => !existingProfileRoles.Contains(
                profile.Role,
                StringComparer.Ordinal)));
        if (governed)
        {
            var storedFlow = await database.Flows.SingleAsync(
                item => item.Id == flow.Id,
                cancellationToken);
            var contractStep = await database.FlowSteps.SingleAsync(
                item => item.Id == contractSource.Id,
                cancellationToken);
            var state = OutcomeVerificationRules.DeserializeAggregate(
                storedFlow.OutcomeVerificationJson);
            state.MaxRounds = workflowProvider
                .GetValidated()
                .Config
                .OutcomeVerification
                .MaxRounds;
            state.PlannedRoles = expectedRoles.ToList();
            state.AcceptancePlan = OutcomeVerificationRules.CreateAcceptanceSnapshot(
                contract.AcceptancePlan ??
                throw new InvalidOperationException(
                    "A governed Team Lead contract has no acceptance plan."),
                contractSource.Id);
            state.Status = OutcomeVerificationStatus.CollectingEvidence;
            state.Evidence.Clear();
            state.EvidenceProcessing.Clear();
            state.Rounds.Clear();
            state.CurrentCandidate = null;
            state.Publication = null;
            state.VerifiedCandidateFingerprint = null;
            state.VerifiedAt = null;
            state.PendingOwnerRoles.Clear();
            state.Stale = false;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            storedFlow.OutcomeVerificationJson =
                OutcomeVerificationRules.SerializeAggregate(state);
            contractStep.Kind = FlowStepKind.OutcomePlan;
            contractStep.OutcomePlanHash = state.AcceptancePlan.Hash;
            contractStep.StableSemanticRootId ??=
                GetStableSemanticRootId(contractSource);
            var initialPlanRoot = GetStableSemanticRootId(contractStep);
            if (state.InitialPlanSemanticRootId is { } existingInitialRoot &&
                existingInitialRoot != initialPlanRoot)
            {
                throw new InvalidOperationException(
                    "The initial acceptance-plan semantic root cannot change.");
            }
            state.InitialPlanSemanticRootId = initialPlanRoot;
            MarkSemanticRootProcessed(state, initialPlanRoot);
            storedFlow.OutcomeVerificationJson =
                OutcomeVerificationRules.SerializeAggregate(state);
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = contractSource.Id,
                Type = "outcome.plan.accepted",
                Message =
                    $"Accepted {state.AcceptancePlan.Criteria.Count} outcome criteria " +
                    $"with plan hash {PrefixDigest(state.AcceptancePlan.Hash)} and a " +
                    $"{state.MaxRounds}-round QA budget."
            });
        }
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = contractSource.Id,
            Type = "profile.validated",
            Message =
                $"Validated router-v1 task profiles for {contract.Profiles.Count} downstream roles " +
                $"and {contract.PreMortemAfterRoles.Count} pre-mortem checkpoint(s)."
        });
        await database.SaveChangesAsync(cancellationToken);
        return contract.PreMortemAfterRoles;
    }

    internal static TeamLeadContract ParseTeamLeadContract(
        string output,
        IReadOnlyList<string> expectedRoles,
        Guid flowId,
        int iteration,
        bool preMortemAvailable,
        bool outcomeVerificationEnabled)
    {
        IReadOnlyList<TaskProfile>? profiles = null;
        IReadOnlySet<string>? checkpoints = null;
        OutcomeAcceptancePlan? acceptancePlan = null;
        var errors = new List<string>();
        try
        {
            profiles = TaskProfileRules.ParseTeamLeadOutput(
                output,
                expectedRoles,
                flowId,
                iteration);
        }
        catch (TaskProfileValidationException exception)
        {
            errors.AddRange(exception.Errors.Select(error => $"task profiles: {error}"));
        }
        try
        {
            checkpoints = PreMortemRules.ParsePlan(
                output,
                expectedRoles,
                preMortemAvailable);
        }
        catch (PreMortemValidationException exception)
        {
            errors.AddRange(exception.Errors.Select(error => $"pre-mortem plan: {error}"));
        }
        if (outcomeVerificationEnabled &&
            checkpoints?.Contains("quality-engineer") == true)
        {
            errors.Add(
                "pre-mortem plan: quality-engineer cannot be a checkpoint because outcome QA is already independent and authoritative");
        }
        if (outcomeVerificationEnabled)
        {
            try
            {
                acceptancePlan = OutcomeVerificationRules.ParseAcceptancePlan(
                    output,
                    expectedRoles);
            }
            catch (OutcomeVerificationValidationException exception)
            {
                errors.AddRange(exception.Errors.Select(
                    error => $"acceptance plan: {error}"));
            }
        }
        if (errors.Count > 0)
        {
            throw new TaskProfileValidationException(errors);
        }

        var validatedProfiles = profiles
            ?? throw new TaskProfileValidationException(["task profiles are required"]);
        var validatedCheckpoints = checkpoints
            ?? throw new TaskProfileValidationException(["pre-mortem plan is required"]);
        foreach (var profile in validatedProfiles)
        {
            profile.PreMortemAfter = validatedCheckpoints.Contains(profile.Role);
        }
        return new TeamLeadContract(
            validatedProfiles,
            validatedCheckpoints,
            acceptancePlan);
    }

    private async Task<bool> ShouldExecuteStepAsync(
        Guid stepId,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        return await database.FlowSteps.AnyAsync(
            item =>
                item.Id == stepId &&
                item.Status == StepStatus.Pending &&
                (item.InvocationKind !=
                     ExecutionInvocationKind.ReviewClassification ||
                 !database.FlowEvents.Any(flowEvent =>
                     flowEvent.FlowStepId == item.Id &&
                     flowEvent.Type ==
                     ReviewCoordinator.FeedbackRequestEventType)),
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
                item.Status == StepStatus.Pending &&
                (item.InvocationKind !=
                     ExecutionInvocationKind.ReviewClassification ||
                 !database.FlowEvents.Any(flowEvent =>
                     flowEvent.FlowStepId == item.Id &&
                     flowEvent.Type ==
                     ReviewCoordinator.FeedbackRequestEventType)))
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

    private async Task<bool> ReconcileOutcomeVerificationAsync(
        FlowRun flow,
        IReadOnlyList<PlannedAgent> plan,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var storedFlow = await database.Flows.SingleAsync(
            item => item.Id == flow.Id,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(storedFlow.OutcomeVerificationJson))
        {
            return false;
        }

        var state = OutcomeVerificationRules.DeserializeAggregate(
            storedFlow.OutcomeVerificationJson);
        var steps = await database.FlowSteps
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.Iteration == flow.Iteration)
            .OrderBy(item => item.Sequence)
            .ThenBy(item => item.Attempt)
            .ToListAsync(cancellationToken);
        var latestRound = state.Rounds
            .OrderByDescending(item => item.Round)
            .FirstOrDefault();

        if (state.Status == OutcomeVerificationStatus.Correcting)
        {
            if (latestRound?.Result?.PlanGaps.Count > 0 &&
                string.Equals(
                    latestRound.AcceptancePlanHash,
                    state.AcceptancePlan?.Hash,
                    StringComparison.Ordinal))
            {
                return await EnsurePlanGapCorrectionAsync(
                    database,
                    storedFlow,
                    state,
                    steps,
                    plan,
                    latestRound,
                    cancellationToken);
            }

            if (latestRound?.Result is null &&
                state.PendingOwnerRoles.Count == 0)
            {
                state.Status = OutcomeVerificationStatus.AwaitingQa;
                state.UpdatedAt = DateTimeOffset.UtcNow;
                storedFlow.OutcomeVerificationJson =
                    OutcomeVerificationRules.SerializeAggregate(state);
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = latestRound?.QaStepId,
                    Type = "outcome.qa.retry-required",
                    Message =
                        "The invalid QA contract consumed a round; Quality Engineer must return a fresh strict result."
                });
                await database.SaveChangesAsync(cancellationToken);
                return await EnsureQaRoundAsync(
                    database,
                    storedFlow,
                    state,
                    steps,
                    plan,
                    cancellationToken);
            }

            if (state.PendingOwnerRoles.Count > 0)
            {
                var correctionRound = latestRound
                ?? throw new InvalidOperationException(
                    "Outcome correction requires a persisted QA round.");
                var correctionPlanHash = state.AcceptancePlan?.Hash
                ?? throw new InvalidOperationException(
                    "Outcome correction requires an active acceptance plan.");
                var orderedRoles = OrderRolesByPlan(
                plan.Select(item => item.Agent.Role),
                state.PendingOwnerRoles)
                .ToArray();
                if (orderedRoles.Length != state.PendingOwnerRoles.Count)
                {
                throw new InvalidOperationException(
                    "A responsible QA role is not present in the original Team Lead plan.");
                }

                var dependencyId = correctionRound.QaStepId;
                var sequence = steps.Select(item => item.Sequence).DefaultIfEmpty(0).Max() + 10;
                var created = false;
                var expectedCorrections = new List<FlowStep>(orderedRoles.Length);
                foreach (var role in orderedRoles)
                {
                    var agent = plan
                        .Select(item => item.Agent)
                        .Single(item => string.Equals(
                            item.Role,
                            role,
                            StringComparison.Ordinal));
                    var label =
                        $"{OutcomeCorrectionLabelPrefix}{correctionRound.Round}: {agent.Name}";
                    var correction = steps
                        .Where(item =>
                            item.AgentRole == role &&
                            IsOutcomeOwnerCorrectionStep(item) &&
                            item.OutcomeQaRound == correctionRound.Round &&
                            string.Equals(
                                item.OutcomePlanHash,
                                correctionPlanHash,
                                StringComparison.Ordinal))
                        .OrderByDescending(item => item.Sequence)
                        .ThenByDescending(item => item.Attempt)
                        .FirstOrDefault();
                    if (correction is null)
                    {
                        correction = new FlowStep
                        {
                            FlowRunId = flow.Id,
                            Iteration = flow.Iteration,
                            Sequence = sequence,
                            AgentId = agent.Id,
                            AgentName = agent.Name,
                            AgentRole = agent.Role,
                            Label = label,
                            Kind = FlowStepKind.OutcomeOwnerCorrection,
                            Status = StepStatus.Pending,
                            Attempt = steps
                                .Where(item => item.AgentId == agent.Id)
                                .Select(item => item.Attempt)
                                .DefaultIfEmpty(0)
                                .Max() + 1,
                            DependsOnStepId = dependencyId,
                            OutcomeQaRound = correctionRound.Round,
                            OutcomePlanHash = correctionPlanHash,
                            InputSummary = BuildOutcomeCorrectionAssignment(
                                state,
                                correctionRound,
                                role)
                        };
                        correction.StableSemanticRootId = correction.Id;
                        database.FlowSteps.Add(correction);
                        steps.Add(correction);
                        database.FlowEvents.Add(new FlowEvent
                        {
                            FlowRunId = flow.Id,
                            FlowStepId = correction.Id,
                            Type = "outcome.correction.scheduled",
                            Message =
                                $"{agent.Name} must correct its failed outcome criteria from QA round {correctionRound.Round}."
                        });
                        created = true;
                        sequence += 10;
                    }
                    EnsureCorrectionRegistration(
                        correctionRound,
                        correction,
                        correctionPlanHash);
                    expectedCorrections.Add(correction);
                    dependencyId = correction.Id;
                    if (!correctionRound.CorrectionStepIds.Contains(correction.Id))
                    {
                        correctionRound.CorrectionStepIds.Add(correction.Id);
                    }
                }

                foreach (var completed in expectedCorrections.Where(
                             step => step.Status == StepStatus.Completed))
                {
                    ApplyOutcomeCorrection(
                        database,
                        state,
                        completed,
                        completed.CompletedAt ?? DateTimeOffset.UtcNow);
                }

                state.UpdatedAt = DateTimeOffset.UtcNow;
                storedFlow.OutcomeVerificationJson =
                    OutcomeVerificationRules.SerializeAggregate(state);
                await database.SaveChangesAsync(cancellationToken);
                if (state.PendingOwnerRoles.Count > 0)
                {
                    return created || expectedCorrections.Any(item =>
                        item.Status == StepStatus.Pending);
                }
            }

            state.Status = OutcomeVerificationStatus.AwaitingCandidateRefresh;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            storedFlow.OutcomeVerificationJson =
                OutcomeVerificationRules.SerializeAggregate(state);
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = latestRound?.QaStepId,
                Type = "outcome.corrections.completed",
                Message =
                    "All responsible roles completed their corrections; the local candidate must be refreshed."
            });
            await database.SaveChangesAsync(cancellationToken);
        }

        if (state.Status == OutcomeVerificationStatus.AwaitingCandidateRefresh)
        {
            return await EnsureCandidateRefreshAsync(
                database,
                storedFlow,
                state,
                steps,
                plan,
                cancellationToken);
        }
        if (state.Status == OutcomeVerificationStatus.AwaitingQa)
        {
            return await EnsureQaRoundAsync(
                database,
                storedFlow,
                state,
                steps,
                plan,
                cancellationToken);
        }
        if (state.Status == OutcomeVerificationStatus.AwaitingHumanResolution)
        {
            await EnsureOutcomeResolutionGateAsync(
                database,
                storedFlow,
                state,
                steps,
                cancellationToken);
            return false;
        }
        if (state.Status == OutcomeVerificationStatus.NotStarted)
        {
            state.Status = OutcomeVerificationStatus.Planning;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            storedFlow.OutcomeVerificationJson =
                OutcomeVerificationRules.SerializeAggregate(state);
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                Type = "outcome.planning",
                Message =
                    "Restart reconciliation resumed acceptance-plan creation."
            });
            await database.SaveChangesAsync(cancellationToken);
            return false;
        }
        if (state.Status == OutcomeVerificationStatus.Passed)
        {
            if (!await EnsurePassedReleaseGateAsync(
                database,
                storedFlow,
                state,
                steps,
                cancellationToken))
            {
                return await EnsureCandidateRefreshAsync(
                    database,
                    storedFlow,
                    state,
                    steps,
                    plan,
                    cancellationToken);
            }
            return false;
        }
        if (state.Status is
            OutcomeVerificationStatus.CollectingEvidence or
            OutcomeVerificationStatus.PreparingCandidate or
            OutcomeVerificationStatus.Planning)
        {
            if (state.Status == OutcomeVerificationStatus.Planning)
            {
                var lead = steps
                    .Where(item =>
                        item.InvocationKind ==
                            ExecutionInvocationKind.Planning &&
                        item.Status == StepStatus.Completed)
                    .OrderByDescending(item => item.Sequence)
                    .ThenByDescending(item => item.Attempt)
                    .FirstOrDefault();
                if (lead is not null)
                {
                    throw new InvalidOperationException(
                        "Completed Team Lead planning has no persisted acceptance plan.");
                }
                return false;
            }
            throw new InvalidOperationException(
                $"Outcome verification stopped in '{state.Status}' without pending work.");
        }
        return false;
    }

    private async Task<bool> EnsurePlanGapCorrectionAsync(
        HarnessDbContext database,
        FlowRun flow,
        OutcomeVerificationState state,
        List<FlowStep> steps,
        IReadOnlyList<PlannedAgent> plan,
        OutcomeQaRound round,
        CancellationToken cancellationToken)
    {
        var lead = plan.Single(item =>
            string.Equals(
                item.Agent.Id,
                "team-lead",
                StringComparison.Ordinal));
        var label = $"{OutcomePlanCorrectionLabelPrefix}{round.Round}";
        var existing = steps
            .Where(item =>
                IsOutcomePlanCorrectionStep(item) &&
                item.OutcomeQaRound == round.Round &&
                string.Equals(
                    item.OutcomePlanHash,
                    round.AcceptancePlanHash,
                    StringComparison.Ordinal))
            .OrderByDescending(item => item.Sequence)
            .ThenByDescending(item => item.Attempt)
            .FirstOrDefault();
        if (existing is not null)
        {
            if (existing.Status == StepStatus.Skipped &&
                existing.CopilotSessionId is null)
            {
                ResetSkippedStep(existing);
                existing.DependsOnStepId = state.CurrentCandidate?.PreparedByStepId;
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = existing.Id,
                    Type = "outcome.qa.rescheduled",
                    Message =
                        $"QA round {round} was rescheduled after candidate refresh."
                });
                await database.SaveChangesAsync(cancellationToken);
                return true;
            }
            return existing.Status == StepStatus.Pending;
        }

        var sequence = steps.Select(item => item.Sequence).DefaultIfEmpty(0).Max() + 10;
        var gapSummary = string.Join(
            Environment.NewLine,
            round.Result!.PlanGaps.Select(gap =>
                $"- {gap.Requirement} ({gap.Rationale})"));
        var step = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Sequence = sequence,
            AgentId = lead.Agent.Id,
            AgentName = lead.Agent.Name,
            AgentRole = lead.Agent.Role,
            Label = label,
            PlanDutiesJson = SerializePlanDuties(
                [PlanDuty.Analyze, PlanDuty.Design]),
            PlanStage = PlanStage.BeforeReview,
            InvocationKind = ExecutionInvocationKind.Planning,
            PermissionProfile =
                ExecutionPermissionProfile.ReadOnlySource,
            WorkflowRevision = workflowProvider.GetEffective().Revision,
            Kind = FlowStepKind.OutcomePlanCorrection,
            Status = StepStatus.Pending,
            Attempt = steps
                .Where(item =>
                    item.InvocationKind ==
                    ExecutionInvocationKind.Planning)
                .Select(item => item.Attempt)
                .DefaultIfEmpty(0)
                .Max() + 1,
            DependsOnStepId = round.QaStepId,
            OutcomeQaRound = round.Round,
            OutcomePlanHash = round.AcceptancePlanHash,
            InputSummary =
                "QA found confirmed requirements missing from the acceptance plan. Resume the " +
                "original Team Lead session and return a complete replacement acceptance plan. " +
                "Preserve every existing criterion ID and append sequential IDs for new criteria." +
                $"{Environment.NewLine}{Environment.NewLine}{gapSummary}"
        };
        step.StableSemanticRootId = step.Id;
        database.FlowSteps.Add(step);
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = step.Id,
            Type = "outcome.plan-correction.scheduled",
            Message =
                $"Team Lead must correct {round.Result.PlanGaps.Count} acceptance-plan gap(s)."
        });
        state.UpdatedAt = DateTimeOffset.UtcNow;
        flow.OutcomeVerificationJson =
            OutcomeVerificationRules.SerializeAggregate(state);
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> EnsureCandidateRefreshAsync(
        HarnessDbContext database,
        FlowRun flow,
        OutcomeVerificationState state,
        List<FlowStep> steps,
        IReadOnlyList<PlannedAgent> plan,
        CancellationToken cancellationToken)
    {
        var round = state.Rounds
            .OrderByDescending(item => item.Round)
            .FirstOrDefault();
        var roundNumber = round?.Round ?? 0;
        if (!state.Stale &&
            state.CurrentCandidate is not null &&
            state.CurrentCandidate.PreparedAt >= (round?.CompletedAt ?? DateTimeOffset.MinValue) &&
            state.CurrentCandidate.PreparedByStepId != round?.QaStepId)
        {
            state.Status = OutcomeVerificationStatus.AwaitingQa;
            state.Stale = false;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            flow.OutcomeVerificationJson =
                OutcomeVerificationRules.SerializeAggregate(state);
            await database.SaveChangesAsync(cancellationToken);
            return await EnsureQaRoundAsync(
                database,
                flow,
                state,
                steps,
                plan,
                cancellationToken);
        }

        var release = plan.Single(item =>
            string.Equals(
                item.Agent.Role,
                "release-engineer",
                StringComparison.Ordinal));
        var baseLabel = $"{OutcomeCandidateRefreshLabelPrefix}{roundNumber}";
        var existingRefreshes = steps
            .Where(item =>
                IsOutcomeCandidateRefreshStep(item) &&
                item.OutcomeQaRound == roundNumber &&
                string.Equals(
                    item.OutcomePlanHash,
                    state.AcceptancePlan?.Hash,
                    StringComparison.Ordinal))
            .OrderBy(item => item.Sequence)
            .ToArray();
        var existing = existingRefreshes.LastOrDefault();
        if (existing?.Status is StepStatus.Pending or StepStatus.Running)
        {
            return true;
        }
        var label = existingRefreshes.Length == 0
            ? baseLabel
            : $"{baseLabel} (attempt {existingRefreshes.Length + 1})";

        var dependency = round?.CorrectionStepIds
            .Select(id => steps.SingleOrDefault(step => step.Id == id))
            .Where(step => step is not null)
            .OrderBy(step => step!.Sequence)
            .LastOrDefault()?.Id ?? round?.QaStepId;
        var step = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Sequence = steps.Select(item => item.Sequence).DefaultIfEmpty(0).Max() + 10,
            AgentId = release.Agent.Id,
            AgentName = release.Agent.Name,
            AgentRole = release.Agent.Role,
            Label = label,
            Kind = FlowStepKind.OutcomeCandidateRefresh,
            Status = StepStatus.Pending,
            Attempt = steps
                .Where(item => item.AgentRole == "release-engineer")
                .Select(item => item.Attempt)
                .DefaultIfEmpty(0)
                .Max() + 1,
            RetryOfStepId = existing is null
                ? null
                : GetRetryRootId(existing),
            DependsOnStepId = dependency,
            OutcomeQaRound = roundNumber,
            OutcomePlanHash = state.AcceptancePlan?.Hash ?? string.Empty,
            InputSummary = AppendOutcomeAssignment(
                ReleaseCandidateAssignment +
                " Refresh the complete local candidate after the latest corrections.",
                BuildCriterionAssignment(state, release.Agent.Role))
        };
        step.StableSemanticRootId = existing is null
            ? step.Id
            : GetStableSemanticRootId(existing);
        database.FlowSteps.Add(step);
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = step.Id,
            Type = "outcome.candidate-refresh.scheduled",
            Message =
                $"Release Engineer must refresh the local candidate after QA round {roundNumber}."
        });
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> EnsureQaRoundAsync(
        HarnessDbContext database,
        FlowRun flow,
        OutcomeVerificationState state,
        List<FlowStep> steps,
        IReadOnlyList<PlannedAgent> plan,
        CancellationToken cancellationToken)
    {
        if (state.Rounds.Count >= state.MaxRounds)
        {
            state.Status = OutcomeVerificationStatus.AwaitingHumanResolution;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            flow.OutcomeVerificationJson =
                OutcomeVerificationRules.SerializeAggregate(state);
            await database.SaveChangesAsync(cancellationToken);
            await EnsureOutcomeResolutionGateAsync(
                database,
                flow,
                state,
                steps,
                cancellationToken);
            return false;
        }
        if (state.ActiveQaStepId is { } activeStepId)
        {
            var active = steps.SingleOrDefault(item => item.Id == activeStepId);
            return active?.Status is StepStatus.Pending or StepStatus.Running;
        }

        var round = state.Rounds.Count + 1;
        var label = $"{OutcomeQaLabelPrefix}{round})";
        var existing = steps
            .Where(item =>
                IsOutcomeQaStep(item) &&
                item.OutcomeQaRound == round &&
                string.Equals(
                    item.OutcomePlanHash,
                    state.AcceptancePlan?.Hash,
                    StringComparison.Ordinal))
            .OrderByDescending(item => item.Sequence)
            .ThenByDescending(item => item.Attempt)
            .FirstOrDefault();
        if (existing is not null)
        {
            if (existing.Status == StepStatus.Skipped &&
                !state.Rounds.Any(item => item.QaStepId == existing.Id))
            {
                ResetSkippedStep(existing);
                existing.DependsOnStepId =
                    state.CurrentCandidate?.PreparedByStepId;
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = existing.Id,
                    Type = "outcome.qa.rescheduled",
                    Message =
                        $"QA round {round} was rescheduled after the stale candidate was refreshed."
                });
                await database.SaveChangesAsync(cancellationToken);
                return true;
            }
            if (existing.Status == StepStatus.Completed &&
                !state.Rounds.Any(item => item.QaStepId == existing.Id))
            {
                throw new InvalidOperationException(
                    $"Completed QA step '{existing.Id}' has no durable round result.");
            }
            return existing.Status is StepStatus.Pending or StepStatus.Running;
        }
        var qa = plan.Single(item =>
            string.Equals(
                item.Agent.Role,
                "quality-engineer",
                StringComparison.Ordinal));
        var dependency = state.CurrentCandidate?.PreparedByStepId;
        var step = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Sequence = steps.Select(item => item.Sequence).DefaultIfEmpty(0).Max() + 10,
            AgentId = qa.Agent.Id,
            AgentName = qa.Agent.Name,
            AgentRole = qa.Agent.Role,
            Label = label,
            Kind = FlowStepKind.OutcomeQa,
            Status = StepStatus.Pending,
            Attempt = round,
            DependsOnStepId = dependency,
            OutcomeQaRound = round,
            OutcomePlanHash = state.AcceptancePlan?.Hash ?? string.Empty,
            InputSummary =
                $"Independently verify every criterion against candidate " +
                $"{state.CurrentCandidate?.Fingerprint}. This is QA round {round} of " +
                $"{state.MaxRounds}."
        };
        step.StableSemanticRootId = step.Id;
        database.FlowSteps.Add(step);
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = step.Id,
            Type = "outcome.qa.scheduled",
            Message = $"Quality Engineer QA round {round}/{state.MaxRounds} was scheduled."
        });
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task EnsureOutcomeResolutionGateAsync(
        HarnessDbContext database,
        FlowRun flow,
        OutcomeVerificationState state,
        IReadOnlyList<FlowStep> steps,
        CancellationToken cancellationToken)
    {
        var existing = await database.GateRecords.AnyAsync(
            item =>
                item.FlowRunId == flow.Id &&
                item.ActionType == HandoffActionType.OutcomeResolution &&
                !item.Resolved,
            cancellationToken);
        if (!existing)
        {
            var source = state.Rounds
                .OrderByDescending(item => item.Round)
                .Select(round => steps.SingleOrDefault(step =>
                    step.Id == round.QaStepId))
                .FirstOrDefault(step => step is not null)
                ?? steps.LastOrDefault(step => step.Status == StepStatus.Completed)
                ?? throw new InvalidOperationException(
                    "Outcome resolution requires a completed semantic step.");
            var gate = handoffGate.SubmitProposal(new HandoffProposal
            {
                FlowRunId = flow.Id,
                FlowStepId = source.Id,
                ActionType = HandoffActionType.OutcomeResolution,
                Summary =
                    $"Outcome verification did not pass after {state.Rounds.Count} round(s).",
                Evidence = state.Rounds.LastOrDefault()?.ContractError ??
                           state.Rounds.LastOrDefault()?.Result?.Verdict.ToString() ??
                           "No valid QA result.",
                BlastRadius = HandoffBlastRadius.High
            });
            database.GateRecords.Add(gate);
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = source.Id,
                Type = "gate.outcome-resolution-created",
                Message =
                    "Outcome verification requires Continue, Replan, or Abandon; no release bypass is available."
            });
        }
        _lifecycle.Transition(flow, FlowStatus.WaitingForFeedback);
        flow.OutcomeUrl = string.Empty;
        flow.OutcomeLabel = "Outcome verification needs resolution";
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> EnsurePassedReleaseGateAsync(
        HarnessDbContext database,
        FlowRun flow,
        OutcomeVerificationState state,
        IReadOnlyList<FlowStep> steps,
        CancellationToken cancellationToken)
    {
        if (state.CurrentCandidate is null ||
            !string.Equals(
                state.CurrentCandidate.Fingerprint,
                state.VerifiedCandidateFingerprint,
                StringComparison.Ordinal) ||
            state.Stale)
        {
            throw new InvalidOperationException(
                "A release gate cannot be created for a stale or unverified candidate.");
        }
        try
        {
            if (!await (candidateFingerprintService
                    ?? throw new InvalidOperationException(
                        "No candidate fingerprint service is configured."))
                .IsCurrentAsync(
                    flow,
                    state.CurrentCandidate,
                    CandidateFingerprintService.RequiresPreview(
                        state.AcceptancePlan),
                    cancellationToken))
            {
                await MarkCandidateStaleAsync(
                    database,
                    flow,
                    state,
                    null,
                    "Candidate content changed before release-gate reconciliation.",
                    cancellationToken);
                return false;
            }
        }
        catch (CandidateValidationException exception)
        {
            await MarkCandidateStaleAsync(
                database,
                flow,
                state,
                null,
                "Candidate validation failed before release-gate reconciliation: " +
                exception.Message,
                cancellationToken);
            return false;
        }
        var qaStep = state.Rounds
            .Where(round =>
                round.Result?.Verdict == OutcomeQaVerdict.PASS &&
                !round.Stale &&
                string.Equals(
                    round.CandidateFingerprint,
                    state.VerifiedCandidateFingerprint,
                    StringComparison.Ordinal))
            .OrderByDescending(round => round.Round)
            .Select(round => steps.Single(step => step.Id == round.QaStepId))
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "A current PASS has no persisted Quality Engineer step.");
        var hasApprovedGate = await database.GateRecords.AnyAsync(
            gate =>
                gate.FlowRunId == flow.Id &&
                gate.ActionType == HandoffActionType.Release &&
                gate.Resolved &&
                gate.Approved == true &&
                gate.FlowStepId == qaStep.Id,
            cancellationToken);
        if (hasApprovedGate)
        {
            return true;
        }
        var hasGate = await database.GateRecords.AnyAsync(
            gate =>
                gate.FlowRunId == flow.Id &&
                gate.ActionType == HandoffActionType.Release &&
                !gate.Resolved &&
                gate.FlowStepId == qaStep.Id,
            cancellationToken);
        if (hasGate)
        {
            return true;
        }

        var gate = handoffGate.SubmitProposal(new HandoffProposal
        {
            FlowRunId = flow.Id,
            FlowStepId = qaStep.Id,
            ActionType = HandoffActionType.Release,
            Summary =
                $"Independent QA passed every criterion for candidate " +
                $"{PrefixDigest(state.CurrentCandidate.Fingerprint)}.",
            Evidence = qaStep.OutputSummary,
            BlastRadius = HandoffBlastRadius.High
        });
        database.GateRecords.Add(gate);
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = qaStep.Id,
            Type = "gate.release-created",
            Message =
                "Current all-criteria QA PASS created the customer release gate."
        });
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string BuildOutcomeCorrectionAssignment(
        OutcomeVerificationState state,
        OutcomeQaRound round,
        string role)
    {
        var ownedCriterionIds = state.AcceptancePlan?.Criteria
            .Where(item => item.OwnerRoles.Contains(role, StringComparer.Ordinal))
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var failures = round.Result?.Criteria
            .Where(item =>
                item.Status != OutcomeCriterionStatus.PASS &&
                ownedCriterionIds.Contains(item.CriterionId))
            .Select(item =>
                $"- {item.CriterionId}: {item.Rationale}{Environment.NewLine}" +
                $"  Required remediation: {item.Remediation}")
            .ToArray() ?? [];
        return
            $"Resume the original {role} session. Correct every assigned failed criterion and " +
            $"return a complete replacement handoff with updated evidence.{Environment.NewLine}" +
            string.Join(Environment.NewLine, failures) +
            $"{Environment.NewLine}{Environment.NewLine}" +
            BuildCriterionAssignment(state, role);
    }

    private async Task BindStudioPermissionAtFirstLaunchAsync(
        HarnessDbContext database,
        FlowRun flow,
        FlowStep step,
        CancellationToken cancellationToken)
    {
        if (flow.ContractVersion != "studio-v2" ||
            !string.IsNullOrWhiteSpace(step.EffectivePermissionJson))
        {
            return;
        }
        if (step.Status != StepStatus.Pending ||
            step.StartedAt is not null)
        {
            throw new InvalidOperationException(
                "A started studio-v2 attempt has no persisted effective permission policy; execution failed closed.");
        }

        // A materialized pending step is not yet bound to an execution attempt. Its planning
        // revision is provenance only: resolve the effective last-known-good workflow at the
        // launch boundary, then persist policy/revision in the same save as Running/StartedAt.
        var workflow = workflowProvider.GetEffective();
        var duties = ReadPlanDuties(step.PlanDutiesJson).ToImmutableArray();
        var publicationStep =
            ReviewCoordinator.IsStudioPublicationStep(flow, step);
        var approved = publicationStep &&
                       await HasDurableStudioPublicationApprovalAsync(
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
            publicationStep,
            flow.ContractVersion,
            LegacyPublicationAuthorized: false,
            IsGovernedOutcomeVerification: false);
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
                if (flow.ContractVersion == "studio-v2")
                {
                    var publicationShape =
                        ReviewCoordinator.IsStudioPublicationStep(flow, step);
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
                            "studio-v2 publication authority must match the sole planned AfterApproval Publish step.");
                    }
                    if (publicationShape)
                    {
                        _ = OutcomeTypeRules.RequireDelivery(
                            flow.Outcome,
                            nameof(flow.Outcome));
                        remotePublicationAuthorized =
                            await HasDurableStudioPublicationApprovalAsync(
                                database,
                                flow,
                                step,
                                cancellationToken);
                        if (!remotePublicationAuthorized)
                        {
                            throw new InvalidOperationException(
                                "studio-v2 publication cannot execute before durable customer acceptance.");
                        }
                        _ = await RefreshAndRequireStudioPublicationAuthorityAsync(
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
                }
                else
                {
                    remotePublicationAuthorized =
                        step.RemotePublicationAllowed;
                }
                if (flow.ContractVersion == "legacy-v1" &&
                    step.RemotePublicationAllowed &&
                    !string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson) &&
                    !await ValidatePublicationCandidateAsync(
                        database,
                        flow,
                        step,
                        cancellationToken))
                {
                    stopwatch.Stop();
                    return step;
                }
                OutcomeQaContext? qaContext = null;
                if (IsOutcomeQaStep(step))
                {
                    qaContext = await PrepareOutcomeQaDispatchAsync(
                        database,
                        flow,
                        step,
                        cancellationToken);
                    if (qaContext is null)
                    {
                        stopwatch.Stop();
                        return step;
                    }
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
                                   !IsPreMortemStep(step)
                    ? await database.FlowSteps
                        .AsNoTracking()
                        .Where(item =>
                            item.FlowRunId == flow.Id &&
                            item.Iteration == flow.Iteration &&
                            item.AgentId == step.AgentId &&
                            item.Id != step.Id &&
                            item.CopilotSessionId != null &&
                            (flow.ContractVersion == "legacy-v1" ||
                             item.PlanStepKey == step.PlanStepKey) &&
                            (item.Status == StepStatus.Completed ||
                             item.Status == StepStatus.Pushback ||
                             (flow.ContractVersion == "legacy-v1" &&
                              step.AgentRole == "quality-engineer" &&
                              item.Status == StepStatus.Failed)))
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
                        IsPreMortemStep(step)
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
                if (flow.ContractVersion == "legacy-v1")
                {
                    MarkOutcomeStepStarted(database, flow, step);
                }
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
                if (string.Equals(
                        flow.ContractVersion,
                        "studio-v2",
                        StringComparison.Ordinal) &&
                    step.InvocationKind == ExecutionInvocationKind.Worker)
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

                var (outcomeContext, outcomeContract) =
                    BuildOutcomePrompt(flow, step);
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
                    AllowRemotePublication:
                        flow.ContractVersion == "legacy-v1" &&
                        remotePublicationAuthorized &&
                        string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson),
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
                    DirectPrompt: qaContext is null
                        ? string.Empty
                        : $"{qaContext.PromptSummary}{Environment.NewLine}{Environment.NewLine}" +
                          QaOutcomeContract(),
                    IsOutcomeQa: qaContext is not null,
                    IsHostControlledPublication:
                        flow.ContractVersion == "studio-v2" &&
                        reviewedIdentity is not null ||
                        flow.ContractVersion == "legacy-v1" &&
                        step.RemotePublicationAllowed &&
                        !string.IsNullOrWhiteSpace(
                            flow.OutcomeVerificationJson),
                    IsGovernedOutcomeVerification:
                        flow.ContractVersion == "legacy-v1" &&
                        !string.IsNullOrWhiteSpace(
                            flow.OutcomeVerificationJson),
                    GovernedRepositoryRelativePaths:
                        reviewedIdentity is not null
                            ? reviewedIdentity.Repositories
                                .Select(repository => repository.RelativePath)
                                .ToArray()
                            : flow.ContractVersion != "legacy-v1" ||
                        string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson)
                            ? null
                            : OutcomeVerificationRules.DeserializeAggregate(
                                    flow.OutcomeVerificationJson)
                                .TrustedRepositories
                                .Select(repository => repository.RelativePath)
                                .ToArray(),
                    FlowStepId: step.Id,
                    ContractVersion: flow.ContractVersion,
                    InvocationKind: step.InvocationKind,
                    StudioDependencyOutputs: studioDependencyOutputs,
                    IsOutcomeOwner: step.IsOutcomeOwner,
                    PlanStepKey: step.PlanStepKey,
                    FlowKind: flow.Kind);
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
        try
        {
            attemptedResult = await agentRunner.ExecuteAsync(
                executionContext,
                cancellationToken);
            var contractError = GetStudioContractCorrectionReason(
                executionContext,
                attemptedResult.Output);
            if (contractError is not null)
            {
                if (isStudioContractCorrection)
                {
                    throw new InvalidOperationException(
                        "The bounded studio-v2 response-contract correction remained invalid: " +
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
                return await ExecuteStepAsync(
                    flowId,
                    correctionStepId,
                    workspacePath,
                    planSummary,
                    complexity,
                    cancellationToken);
            }
            stopwatch.Stop();
            return await CompleteStepAsync(
                flowId,
                stepId,
                attemptedResult,
                DateTimeOffset.UtcNow,
                stopwatch.ElapsedMilliseconds,
                recoveredSessionId: null,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            await MarkStepInterruptedAsync(
                flowId,
                stepId,
                stopwatch.ElapsedMilliseconds,
                CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            await MarkStepFailedAsync(
                stepId,
                exception,
                stopwatch.ElapsedMilliseconds,
                cancellationToken,
                attemptedResult?.Output);
            throw;
        }
    }

    internal static string? GetStudioContractCorrectionReason(
        AgentExecutionContext context,
        string output)
    {
        if (!string.Equals(
                context.ContractVersion,
                "studio-v2",
                StringComparison.Ordinal) ||
            context.InvocationKind is not (
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
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message;
        }

        if (!handoff.IsPushback &&
            string.Equals(
                context.AgentRole,
                "quality-engineer",
                StringComparison.Ordinal) &&
            ContainsExplicitFailedQualityVerdict(output))
        {
            return
                "Quality Engineer paired HANDOFF_STATUS: COMPLETE with an explicit failing or not-release-ready verdict.";
        }
        return null;
    }

    private static bool ContainsExplicitFailedQualityVerdict(string output) =>
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
        CancellationToken cancellationToken)
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
        if (source.Status != StepStatus.Running ||
            !string.Equals(
                flow.ContractVersion,
                "studio-v2",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A studio-v2 response-contract correction can be scheduled only for its running source attempt.");
        }

        foreach (var later in flow.Steps.Where(item =>
                     item.Iteration == source.Iteration &&
                     item.Sequence > source.Sequence))
        {
            later.Sequence += 10;
        }

        foreach (var toolCall in result.ToolCalls)
        {
            database.AgentToolCalls.Add(new AgentToolCall
            {
                FlowStepId = source.Id,
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
            });
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
            Kind = source.Kind,
            Status = StepStatus.Pending,
            Phase = AgentRunPhase.PreparingWorkspace,
            Attempt = flow.Steps
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
            OutcomeQaRound = source.OutcomeQaRound,
            OutcomePlanHash = source.OutcomePlanHash,
            StableSemanticRootId = GetStableSemanticRootId(source),
            PreMortemOriginStepId = source.PreMortemOriginStepId,
            PreMortemTargetStepId = source.PreMortemTargetStepId,
            PreMortemReviewStepId = source.PreMortemReviewStepId
        };
        flow.Steps.Add(correction);
        database.Entry(correction).State = EntityState.Added;
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
                $"{source.AgentName} must correct one invalid studio-v2 response contract before the flow can advance.",
            DataJson = JsonSerializer.Serialize(new
            {
                Version = "studio-contract-correction-v1",
                SourceStepId = source.Id,
                CorrectionStepId = correction.Id,
                Error = ClipText(contractError, 2_000)
            })
        });
        flow.UpdatedAt = completedAt;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return correction.Id;
    }

    internal static string BuildStudioContractCorrectionAssignment(
        FlowStep source,
        string contractError)
    {
        var assignment =
            "Your previous studio-v2 response contract was invalid. Do not rerun tools or modify " +
            "the workspace; rewrite the complete response from your existing evidence. Start with " +
            "exactly one standalone HANDOFF_STATUS: COMPLETE or HANDOFF_STATUS: PUSHBACK line. " +
            "For PUSHBACK, use one exact allowed current-iteration owner from the prompt plus one " +
            "bounded PUSHBACK_REASON. Keep the entire replacement under 12,000 characters. " +
            $"Validation error: {ClipText(contractError, 2_000)}";
        if (source.IsOutcomeOwner)
        {
            assignment +=
                " Return exactly one complete flow-outcome-v1 document with 1-24 concise, " +
                "consolidated ImplementationDetails between the exact standalone sentinels.";
        }
        if (source.PreMortemReviewStepId is not null)
        {
            assignment +=
                " Preserve exactly one PRE_MORTEM_DISPOSITION marker required by this revision.";
        }
        return assignment;
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
                "A studio-v2 worker has no immutable plan-step identity.");
        }

        var document = await database.FlowPlanDocuments
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item =>
                    item.FlowRunId == flow.Id &&
                    item.Iteration == currentStep.Iteration,
                cancellationToken)
            ?? throw new InvalidOperationException(
                "A studio-v2 worker has no accepted plan document for its iteration.");
        var parsed = TeamPlanParser.ParseJson(document.RawJson).Document;
        var planSteps = parsed.Steps ??
                        throw new InvalidOperationException(
                            "The accepted studio-v2 plan has no steps.");
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
        if (!IsPreMortemStep(review))
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
                Version = "premortem-disabled-v1",
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

        var contractVersion = await database.Flows
            .AsNoTracking()
            .Where(flow => flow.Id == revision.FlowRunId)
            .Select(flow => flow.ContractVersion)
            .SingleAsync(cancellationToken);
        revision.InputSummary = BuildPreMortemRevisionAssignment(
            review.OutputSummary,
            revision.AgentRole,
            revision.PlanDutiesJson,
            contractVersion);
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
            result = PreMortemRules.ParseReview(review.OutputSummary);
            alreadyResolved = await database.FlowEvents.AnyAsync(
                                  item =>
                                      item.FlowStepId == reviewStepId &&
                                      (item.Type == "premortem.review-cleared" ||
                                       item.Type == "premortem.revision-scheduled"),
                                  cancellationToken) ||
                              await database.FlowSteps.AnyAsync(
                                  item => item.PreMortemReviewStepId == reviewStepId,
                                  cancellationToken);
        }
        if (alreadyResolved)
        {
            return;
        }
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
                (flow.ContractVersion == "legacy-v1" ||
                 item.PlanStepKey == target.PlanStepKey))
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
            PlanStepKey = flow.ContractVersion == "studio-v2"
                ? target.PlanStepKey
                : string.Empty,
            PlanDutiesJson = flow.ContractVersion == "studio-v2"
                ? target.PlanDutiesJson
                : "[]",
            PlanStage = flow.ContractVersion == "studio-v2"
                ? target.PlanStage
                : PlanStage.BeforeReview,
            InvocationKind = flow.ContractVersion == "studio-v2"
                ? target.InvocationKind
                : ExecutionInvocationKind.Worker,
            IsOutcomeOwner = flow.ContractVersion == "studio-v2" &&
                target.IsOutcomeOwner,
            PermissionProfile = flow.ContractVersion == "studio-v2"
                ? target.PermissionProfile
                : ExecutionPermissionProfile.WorkspaceWrite,
            EffectivePermissionJson = flow.ContractVersion == "studio-v2"
                ? target.EffectivePermissionJson
                : string.Empty,
            WorkflowRevision = flow.ContractVersion == "studio-v2"
                ? target.WorkflowRevision
                : workflowProvider.GetEffective().Revision,
            RemotePublicationAllowed = target.RemotePublicationAllowed,
            Status = StepStatus.Pending,
            Attempt = revisionAttempt,
            InputSummary = BuildPreMortemRevisionAssignment(
                review.OutputSummary,
                target.AgentRole,
                target.PlanDutiesJson,
                flow.ContractVersion),
            PreMortemOriginStepId = review.PreMortemOriginStepId,
            PreMortemReviewStepId = review.Id
        };
        revision.StableSemanticRootId = target.StableSemanticRootId ?? target.Id;
        var permissionTightened =
            flow.ContractVersion == "studio-v2" &&
            PreserveOrTightenRetryPermission(
                flow,
                target,
                revision);
        database.FlowSteps.Add(revision);
        if (flow.ContractVersion == "studio-v2")
        {
            var targetProfile = await database.TaskProfiles
                .AsNoTracking()
                .SingleAsync(
                    item => item.FlowStepId == target.Id,
                    cancellationToken);
            database.TaskProfiles.Add(
                TaskProfileRules.CopyForStep(targetProfile, revision.Id));
        }
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
                result.Output);
            throw;
        }
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
        var governedPublicationOutput = await PrepareGovernedPublicationIfNeededAsync(
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
            governedPublicationOutput,
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

    private async Task<string?> PrepareGovernedPublicationIfNeededAsync(
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
                    flow.ContractVersion,
                    flow
                })
            .SingleAsync(cancellationToken);
        var legacyGoverned =
            publication.ContractVersion == "legacy-v1" &&
            publication.RemotePublicationAllowed &&
            !string.IsNullOrWhiteSpace(
                publication.flow.OutcomeVerificationJson);
        var studioReviewed =
            publication.ContractVersion == "studio-v2" &&
            ReviewCoordinator.IsStudioPublicationStep(
                publication.flow,
                publication.Step);
        if (!legacyGoverned && !studioReviewed)
        {
            return null;
        }
        if (studioReviewed)
        {
            // Remote publication is an irreversible host side effect. Validate the exact,
            // invocation-specific studio-v2 handoff before token lookup, publication events,
            // or any Git/GitHub command can run.
            var handoff = AgentHandoffInspector.ParseDynamic(result.Output);
            if (handoff.IsPushback)
            {
                return null;
            }
            _ = await RefreshAndRequireStudioPublicationAuthorityAsync(
                database,
                publication.flow,
                publication.Step,
                cancellationToken);
        }
        else if (AgentHandoffInspector.GetPushbackReason(result.Output) is not null)
        {
            // Preserve the permissive legacy handoff compatibility path without weakening
            // studio-v2's exact contract boundary above.
            return null;
        }

        return await (candidatePublisher
            ?? throw new InvalidOperationException(
                "No verified candidate publisher is configured."))
            .PublishAsync(
                publication.flow,
                stepId,
                cancellationToken);
    }

    private async Task<EffectiveExecutionPermission>
        RefreshAndRequireStudioPublicationAuthorityAsync(
            HarnessDbContext database,
            FlowRun flow,
            FlowStep publication,
            CancellationToken cancellationToken)
    {
        if (!ReviewCoordinator.IsStudioPublicationStep(
                flow,
                publication))
        {
            throw new InvalidOperationException(
                "Remote publication authority is available only to the sole planned studio-v2 publication step.");
        }
        if (!await HasDurableStudioPublicationApprovalAsync(
                database,
                flow,
                publication,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "studio-v2 publication cannot execute before durable customer acceptance.");
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
            IsOnlyPlannedPublishStep: true,
            ContractVersion: flow.ContractVersion,
            LegacyPublicationAuthorized: false,
            IsGovernedOutcomeVerification: false);
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

    private async Task<IReadOnlyList<HandoffGateRecord>>
        SupersedePendingReleaseGatesAsync(
        HarnessDbContext database,
        Guid flowId,
        Guid effectiveStepId,
        int iteration,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken)
    {
        var pending = await database.GateRecords
            .Where(gate =>
                gate.FlowRunId == flowId &&
                gate.FlowStepId != effectiveStepId &&
                gate.ActionType == HandoffActionType.Release &&
                !gate.Resolved &&
                database.FlowSteps.Any(step =>
                    step.Id == gate.FlowStepId &&
                    step.Iteration == iteration))
            .ToListAsync(cancellationToken);
        var preparedUpdates = new List<HandoffGateRecord>(pending.Count);
        foreach (var gate in pending)
        {
            var resolved = handoffGate.PrepareSupersession(
                gate,
                "harness",
                "Superseded by a revised release candidate.",
                resolvedAt);
            gate.Resolved = resolved.Resolved;
            gate.Approved = resolved.Approved;
            gate.ResolvedBy = resolved.ResolvedBy;
            gate.ResolutionNote = resolved.ResolutionNote;
            gate.ResolvedAt = resolved.ResolvedAt ?? resolvedAt;
            preparedUpdates.Add(resolved);
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flowId,
                FlowStepId = gate.FlowStepId,
                Type = "gate.release-superseded",
                Message = "An earlier release gate was superseded by a revised candidate."
            });
        }
        return preparedUpdates;
    }

    private static bool IsOutcomeQaStep(FlowStep step) =>
        step.Kind == FlowStepKind.OutcomeQa;

    private static bool IsPreMortemStep(FlowStep step) =>
        step.InvocationKind == ExecutionInvocationKind.PreMortem ||
        string.Equals(
            step.AgentId,
            PreMortemRole,
            StringComparison.Ordinal);

    private static bool IsOutcomePlanCorrectionStep(FlowStep step) =>
        step.Kind == FlowStepKind.OutcomePlanCorrection;

    private static bool IsOutcomeOwnerCorrectionStep(FlowStep step) =>
        step.Kind == FlowStepKind.OutcomeOwnerCorrection;

    private static bool IsOutcomeCandidateRefreshStep(FlowStep step) =>
        step.Kind == FlowStepKind.OutcomeCandidateRefresh;

    private static bool IsOutcomeLocalReleaseCandidateStep(FlowStep step) =>
        step.Kind == FlowStepKind.OutcomeLocalReleaseCandidate;

    private static bool IsOutcomeApprovedPublicationStep(FlowStep step) =>
        step.Kind == FlowStepKind.OutcomeApprovedPublication;

    private static bool IsGovernedOutcomeEvidenceStep(FlowStep step) =>
        step.Kind is FlowStepKind.OutcomeDelivery or FlowStepKind.OutcomeOwnerCorrection ||
        (!step.RemotePublicationAllowed &&
         step.AgentRole is not (
             "team-lead" or
             "quality-engineer" or
             "product-manager" or
             PreMortemRole));

    private static Guid GetStableSemanticRootId(FlowStep step) =>
        step.StableSemanticRootId ?? step.RetryOfStepId ?? step.Id;

    private static Guid GetRetryRootId(FlowStep step) =>
        step.RetryOfStepId ?? GetStableSemanticRootId(step);

    private static void MarkSemanticRootProcessed(
        OutcomeVerificationState state,
        Guid semanticRootId)
    {
        if (semanticRootId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "A processed outcome semantic root cannot be empty.");
        }
        if (!state.ProcessedSemanticRootIds.Contains(semanticRootId))
        {
            state.ProcessedSemanticRootIds.Add(semanticRootId);
        }
    }

    private static void RebindActiveQaRetry(
        FlowRun flow,
        FlowStep priorStep,
        FlowStep retryStep)
    {
        if (string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson) ||
            !IsOutcomeQaStep(priorStep) ||
            !IsOutcomeQaStep(retryStep))
        {
            return;
        }

        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        if (state.ActiveQaStepId != priorStep.Id)
        {
            return;
        }

        var expectedRound = priorStep.OutcomeQaRound ?? retryStep.OutcomeQaRound;
        if (state.ActiveQaRound != expectedRound)
        {
            return;
        }

        state.ActiveQaStepId = retryStep.Id;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        flow.OutcomeVerificationJson =
            OutcomeVerificationRules.SerializeAggregate(state);
    }

    private static List<string> OrderRolesByPlan(
        IEnumerable<string> plannedRoles,
        IEnumerable<string> roles)
    {
        var requested = roles
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        return plannedRoles
            .Where(role => requested.Remove(role))
            .Distinct(StringComparer.Ordinal)
            .Concat(requested.Order(StringComparer.Ordinal))
            .ToList();
    }

    private static List<string> DerivePendingOwnerRoles(
        OutcomeVerificationState state,
        IEnumerable<string> criterionIds) =>
        state.AcceptancePlan is null
            ? []
            : OrderRolesByPlan(
                state.PlannedRoles,
                state.AcceptancePlan.Criteria
                    .Where(item =>
                        criterionIds.Contains(
                            item.Id,
                            StringComparer.Ordinal))
                    .SelectMany(item => item.OwnerRoles));

    private async Task<bool> ValidatePublicationCandidateAsync(
        HarnessDbContext database,
        FlowRun flow,
        FlowStep step,
        CancellationToken cancellationToken)
    {
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        if (state.Status != OutcomeVerificationStatus.Passed ||
            state.CurrentCandidate is null ||
            state.Stale ||
            !string.Equals(
                state.CurrentCandidate.Fingerprint,
                state.VerifiedCandidateFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Publication cannot start without a current authoritative QA PASS.");
        }
        var current = false;
        string? reason = null;
        try
        {
            current = await (candidateFingerprintService
                ?? throw new InvalidOperationException(
                    "No candidate fingerprint service is configured."))
                .IsCurrentAsync(
                    flow,
                    state.CurrentCandidate,
                    CandidateFingerprintService.RequiresPreview(
                        state.AcceptancePlan),
                    cancellationToken);
        }
        catch (CandidateValidationException exception)
        {
            reason = exception.Message;
        }
        if (current)
        {
            return true;
        }

        step.Status = StepStatus.Skipped;
        step.Phase = AgentRunPhase.Succeeded;
        step.CompletedAt = DateTimeOffset.UtcNow;
        await MarkCandidateStaleAsync(
            database,
            flow,
            state,
            step.Id,
            "Candidate changed before publication; nothing was published. " +
            (reason ?? string.Empty),
            cancellationToken);
        return false;
    }

    private async Task<OutcomeQaContext?> PrepareOutcomeQaDispatchAsync(
        HarnessDbContext database,
        FlowRun flow,
        FlowStep step,
        CancellationToken cancellationToken)
    {
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        var candidate = state.CurrentCandidate
            ?? throw new InvalidOperationException(
                "Quality Engineer cannot run before local candidate preparation.");
        var fingerprintService = candidateFingerprintService
            ?? throw new InvalidOperationException(
                "No candidate fingerprint service is configured.");
        OutcomeCandidateSnapshot current;
        try
        {
            current = await fingerprintService.PrepareAsync(
                flow,
                state.AcceptancePlan?.Hash ??
                throw new InvalidOperationException(
                    "Quality Engineer cannot run without an acceptance plan."),
                candidate.PreparedByStepId,
                CandidateFingerprintService.RequiresPreview(
                    state.AcceptancePlan),
                cancellationToken);
        }
        catch (CandidateValidationException exception)
        {
            step.Status = StepStatus.Skipped;
            step.Phase = AgentRunPhase.Succeeded;
            step.CompletedAt = DateTimeOffset.UtcNow;
            await MarkCandidateStaleAsync(
                database,
                flow,
                state,
                step.Id,
                exception.Message,
                cancellationToken);
            return null;
        }
        if (!string.Equals(
                current.Fingerprint,
                candidate.Fingerprint,
                StringComparison.Ordinal))
        {
            step.Status = StepStatus.Skipped;
            step.Phase = AgentRunPhase.Succeeded;
            step.CompletedAt = DateTimeOffset.UtcNow;
            await MarkCandidateStaleAsync(
                database,
                flow,
                state,
                step.Id,
                "Candidate content changed after local preparation.",
                cancellationToken);
            return null;
        }

        var round = state.ActiveQaStepId == step.Id
            ? state.ActiveQaRound!.Value
            : step.OutcomeQaRound ?? state.ActiveQaRound ?? state.Rounds.Count + 1;
        if (round > state.MaxRounds)
        {
            throw new InvalidOperationException(
                "The outcome-verification QA round budget is exhausted.");
        }
        if (state.ActiveQaStepId is not null &&
            state.ActiveQaStepId != step.Id)
        {
            var activeStep = await database.FlowSteps
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.Id == state.ActiveQaStepId,
                    cancellationToken);
            var canReplaceActiveStep = activeStep is not null &&
                                       IsOutcomeQaStep(activeStep) &&
                                       GetStableSemanticRootId(activeStep) ==
                                       GetStableSemanticRootId(step) &&
                                       activeStep.OutcomeQaRound == round &&
                                       string.Equals(
                                           activeStep.OutcomePlanHash,
                                           step.OutcomePlanHash,
                                           StringComparison.Ordinal);
            if (!canReplaceActiveStep ||
                state.ActiveQaRound != round)
            {
                throw new InvalidOperationException(
                    "A different QA step already owns the active verification round.");
            }
        }

        var context = await (outcomeContextBuilder
            ?? throw new InvalidOperationException(
                "No outcome verification context builder is configured."))
            .BuildAsync(flow.Id, round, cancellationToken);
        step.Kind = FlowStepKind.OutcomeQa;
        step.OutcomeQaRound = round;
        step.OutcomePlanHash = state.AcceptancePlan?.Hash ?? string.Empty;
        step.StableSemanticRootId ??= GetStableSemanticRootId(step);
        state.ActiveQaRound = round;
        state.ActiveQaStepId = step.Id;
        state.ActiveQaContextPath = context.Path;
        state.ActiveQaContextHash = context.Hash;
        state.Status = OutcomeVerificationStatus.AwaitingQa;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        flow.OutcomeVerificationJson =
            OutcomeVerificationRules.SerializeAggregate(state);
        if (!await database.FlowEvents.AnyAsync(
                item =>
                    item.FlowRunId == flow.Id &&
                    item.FlowStepId == step.Id &&
                    item.Type == "outcome.qa.dispatched",
                cancellationToken))
        {
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = step.Id,
                Type = "outcome.qa.dispatched",
                Message =
                    $"Quality Engineer received round {round}/{state.MaxRounds} for " +
                    $"{PrefixDigest(candidate.Fingerprint)} with context " +
                    $"{PrefixDigest(context.Hash)}."
            });
        }
        await database.SaveChangesAsync(cancellationToken);
        return context;
    }

    private async Task MarkCandidateStaleAsync(
        HarnessDbContext database,
        FlowRun flow,
        OutcomeVerificationState state,
        Guid? stepId,
        string reason,
        CancellationToken cancellationToken)
    {
        state.Stale = true;
        state.VerifiedCandidateFingerprint = null;
        state.VerifiedAt = null;
        state.Status = OutcomeVerificationStatus.AwaitingCandidateRefresh;
        state.ActiveQaRound = null;
        state.ActiveQaStepId = null;
        state.ActiveQaContextPath = null;
        state.ActiveQaContextHash = null;
        foreach (var round in state.Rounds.Where(round =>
                     string.Equals(
                         round.CandidateFingerprint,
                         state.CurrentCandidate?.Fingerprint,
                         StringComparison.Ordinal)))
        {
            round.Stale = true;
        }
        var staleQaStepIds = state.Rounds
            .Where(round => round.Stale)
            .Select(round => round.QaStepId)
            .ToArray();
        var staleApprovedGates = await database.GateRecords
            .Where(gate =>
                gate.FlowRunId == flow.Id &&
                gate.ActionType == HandoffActionType.Release &&
                gate.Resolved &&
                gate.Approved == true &&
                staleQaStepIds.Contains(gate.FlowStepId))
            .ToListAsync(cancellationToken);
        foreach (var gate in staleApprovedGates)
        {
            var alreadyRecorded = await database.FlowEvents.AnyAsync(
                item =>
                    item.FlowRunId == flow.Id &&
                    item.FlowStepId == gate.FlowStepId &&
                    item.Type == "gate.release-approval-stale",
                cancellationToken);
            if (!alreadyRecorded)
            {
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = gate.FlowStepId,
                    Type = "gate.release-approval-stale",
                    Message =
                        "The historical customer approval remains in the audit record but does not authorize a refreshed candidate."
                });
            }
        }
        state.UpdatedAt = DateTimeOffset.UtcNow;
        flow.OutcomeVerificationJson =
            OutcomeVerificationRules.SerializeAggregate(state);
        var preparedGateUpdates = await SupersedePendingReleaseGatesAsync(
            database,
            flow.Id,
            stepId ?? Guid.Empty,
            flow.Iteration,
            DateTimeOffset.UtcNow,
            cancellationToken);
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = stepId,
            Type = "outcome.candidate.stale",
            Message = reason
        });
        await database.SaveChangesAsync(cancellationToken);
        if (preparedGateUpdates.Count > 0)
        {
            handoffGate.RestoreHistory(preparedGateUpdates);
        }
    }

    private async Task<StagedStepCompletion> StageCompletedStepAsync(
        HarnessDbContext database,
        Guid flowId,
        Guid stepId,
        AgentExecutionResult result,
        DateTimeOffset completedAt,
        long? durationMilliseconds,
        Guid? recoveredSessionId,
        string? governedPublicationOutput,
        CancellationToken cancellationToken)
    {
        var step = await database.FlowSteps.SingleAsync(
            item => item.Id == stepId,
            cancellationToken);
        var flow = await database.Flows.SingleAsync(
            item => item.Id == flowId,
            cancellationToken);
        if (flow.ContractVersion == "studio-v2" &&
            step.InvocationKind == ExecutionInvocationKind.Intake)
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
        if (flow.ContractVersion == "studio-v2" &&
            step.InvocationKind is
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
            : flow.ContractVersion == "studio-v2"
                ? dynamicHandoff?.Reason
                : AgentHandoffInspector.GetPushbackReason(result.Output);
        var pushedBack = pushbackReason is not null;
        ParsedFlowOutcome? normalizedOutcome = null;
        if (!pushedBack &&
            flow.ContractVersion == "studio-v2" &&
            step.IsOutcomeOwner)
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
                    $"Accepted the required {FlowOutcomeParser.Version} result from the planned outcome owner.",
                DataJson = JsonSerializer.Serialize(new
                {
                    Version = FlowOutcomeParser.Version,
                    normalizedOutcome.Document.Goal,
                    normalizedOutcome.Document.Summary,
                    ImplementationDetailCount =
                        normalizedOutcome.Document.ImplementationDetails!.Count,
                    ArtifactCount =
                        normalizedOutcome.Document.Artifacts!.Count
                })
            });
        }
        var studioPublication =
            ReviewCoordinator.IsStudioPublicationStep(flow, step);
        var legacyPublication =
            flow.ContractVersion == "legacy-v1" &&
            step.RemotePublicationAllowed;
        if (flow.ContractVersion == "studio-v2" &&
            (step.RemotePublicationAllowed ||
             step.PlanStage == PlanStage.AfterApproval ||
             string.Equals(
                 step.PlanStepKey,
                 flow.PublicationPlanStepKey,
                 StringComparison.Ordinal) ||
             ReadPlanDuties(step.PlanDutiesJson).Contains(PlanDuty.Publish)) &&
            !studioPublication)
        {
            throw new InvalidOperationException(
                "studio-v2 publication completion does not match its durable planned authority.");
        }
        if (studioPublication &&
            !await HasDurableStudioPublicationApprovalAsync(
                database,
                flow,
                step,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "studio-v2 publication completion has no durable accepted CustomerReview.");
        }
        if ((studioPublication || legacyPublication) && !pushedBack)
        {
            var verificationOutput = result.Output;
            if (studioPublication)
            {
                verificationOutput = governedPublicationOutput
                    ?? throw new InvalidOperationException(
                        "studio-v2 publication requires a host-controlled sealed-candidate publication record.");
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = step.Id,
                    Type = "delivery.reviewed-candidate-published",
                    Message =
                        "The harness published only the candidate identity sealed before customer review."
                });
            }
            else if (legacyPublication &&
                     !string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson))
            {
                verificationOutput = governedPublicationOutput
                    ?? throw new InvalidOperationException(
                        "Governed publication finalization requires a durable host publication record.");
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = step.Id,
                    Type = "outcome.candidate.published",
                    Message =
                        "The harness published the immutable verified commit/tree identities after the guarded Release Engineer turn."
                });
            }
            var published = await (publicationVerifier
                ?? throw new InvalidOperationException(
                    "No published outcome verifier is configured."))
                .VerifyAsync(flow, verificationOutput, cancellationToken);
            if (legacyPublication &&
                !string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson))
            {
                MarkPublicationVerified(flow, step, completedAt);
            }
            flow.OutcomeUrl = published.Url;
            flow.OutcomeLabel = published.Label;
        }
        if (!pushedBack && flow.ContractVersion == "legacy-v1")
        {
            if (step.InvocationKind == ExecutionInvocationKind.Planning &&
                IsOutcomePlanCorrectionStep(step))
            {
                await ApplyAcceptancePlanReplacementAsync(
                    database,
                    flow,
                    step,
                    result.Output,
                    completedAt,
                    cancellationToken);
            }
            CollectOutcomeEvidence(
                database,
                flow,
                step,
                result.Output,
                completedAt);
            CompleteOutcomeCorrection(database, flow, step, completedAt);
            if ((IsOutcomeLocalReleaseCandidateStep(step) ||
                 IsOutcomeCandidateRefreshStep(step)) &&
                !string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson))
            {
                await PrepareOutcomeCandidateAsync(
                    database,
                    flow,
                    step,
                    cancellationToken);
            }
        }
        QaCompletion? qaCompletion = null;
        if (!pushedBack && IsOutcomeQaStep(step))
        {
            qaCompletion = await ProcessOutcomeQaCompletionAsync(
                database,
                flow,
                step,
                result.Output,
                completedAt,
                result.ToolCalls,
                cancellationToken);
        }
        var publishesApprovedOutcome =
            flow.ContractVersion == "legacy-v1" &&
            step.AgentRole == "release-engineer" &&
            step.RemotePublicationAllowed;
        IReadOnlyList<HandoffGateRecord> preparedGateUpdates = [];
        if (flow.ContractVersion == "legacy-v1" &&
            step.AgentRole == "release-engineer" &&
            !publishesApprovedOutcome)
        {
            preparedGateUpdates = await SupersedePendingReleaseGatesAsync(
                database,
                flowId,
                step.Id,
                step.Iteration,
                completedAt,
                cancellationToken);
        }
        var actionType = pushedBack
            ? HandoffActionType.RequestRevision
            : qaCompletion?.ReleaseReady == true
            ? HandoffActionType.Release
            : flow.ContractVersion == "legacy-v1" &&
              step.AgentRole == "release-engineer" &&
              !publishesApprovedOutcome &&
              string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson)
            ? HandoffActionType.Release
            : HandoffActionType.Advance;
        var gateRecord = handoffGate.SubmitProposal(new HandoffProposal
        {
            FlowRunId = flowId,
            FlowStepId = stepId,
            ActionType = actionType,
            Summary = pushbackReason ?? result.Output,
            Evidence = pushedBack ? result.Output : result.Evidence,
            BlastRadius = actionType == HandoffActionType.Release
                ? HandoffBlastRadius.High
                : pushedBack
                ? HandoffBlastRadius.Low
                : HandoffBlastRadius.Medium
        });
        database.GateRecords.Add(gateRecord);
        foreach (var toolCall in result.ToolCalls)
        {
            database.AgentToolCalls.Add(new AgentToolCall
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
            });
        }

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
            qaCompletion?.ContractInvalid == true,
            elapsedMilliseconds,
            step.ExecutionAttempts,
            preparedGateUpdates);
    }

    internal static ParsedIntakeV2 ValidateStudioIntakeCompletion(
        FlowRun flow,
        FlowStep step,
        string output)
    {
        var parsed = IntakeV2Parser.Parse(output);
        if (string.Equals(
                step.PlanStepKey,
                RefinementIntakePlanStepKey,
                StringComparison.Ordinal) &&
            (parsed.Document.Status != IntakeV2Status.Confirmed ||
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
                "studio-v2 pushback requires both blocked and owner plan-step identities.");
        }
        var planJson = await database.FlowPlanDocuments
            .AsNoTracking()
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.Iteration == flow.Iteration)
            .Select(item => item.RawJson)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "studio-v2 pushback cannot be validated without the accepted plan document.");
        var document = TeamPlanParser.ParseJson(planJson).Document;
        if (!TeamPlanValidator.IsDependencyAncestor(
                document,
                handoff.OwnerPlanStepKey,
                blockedStep.PlanStepKey))
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

    private static void MarkPublicationVerified(
        FlowRun flow,
        FlowStep publicationStep,
        DateTimeOffset verifiedAt)
    {
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        var journal = state.Publication
            ?? throw new InvalidOperationException(
                "Published outcome verification has no durable publication journal.");
        if (journal.StepId != GetStableSemanticRootId(publicationStep) ||
            journal.Status != OutcomePublicationStatus.Published)
        {
            throw new InvalidOperationException(
                "Published outcome verification does not match a completed publication journal.");
        }
        journal.Status = OutcomePublicationStatus.Verified;
        journal.UpdatedAt = verifiedAt;
        state.UpdatedAt = verifiedAt;
        flow.OutcomeVerificationJson =
            OutcomeVerificationRules.SerializeAggregate(state);
    }

    private async Task<QaCompletion> ProcessOutcomeQaCompletionAsync(
        HarnessDbContext database,
        FlowRun flow,
        FlowStep step,
        string output,
        DateTimeOffset completedAt,
        IReadOnlyCollection<ToolCallRecord> hostToolCalls,
        CancellationToken cancellationToken)
    {
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        var plan = state.AcceptancePlan
            ?? throw new InvalidOperationException(
                "QA cannot complete without an acceptance plan.");
        var candidate = state.CurrentCandidate
            ?? throw new InvalidOperationException(
                "QA cannot complete without a candidate.");
        if (state.Rounds.Any(round => round.QaStepId == step.Id))
        {
            var existing = state.Rounds.Single(round => round.QaStepId == step.Id);
            return new QaCompletion(
                !string.IsNullOrWhiteSpace(existing.ContractError),
                existing.Result?.Verdict == OutcomeQaVerdict.PASS &&
                !existing.Stale);
        }
        if (state.ActiveQaStepId != step.Id ||
            state.ActiveQaRound is null ||
            string.IsNullOrWhiteSpace(state.ActiveQaContextHash))
        {
            throw new InvalidOperationException(
                "QA completion does not match the persisted active round.");
        }
        step.Kind = FlowStepKind.OutcomeQa;
        step.OutcomeQaRound = state.ActiveQaRound.Value;
        step.OutcomePlanHash = plan.Hash;
        step.StableSemanticRootId ??= GetStableSemanticRootId(step);
        OutcomeQaResult? qaResult = null;
        string contractError = string.Empty;
        try
        {
            if (!await OutcomeVerificationContextBuilder.MatchesPersistedHashAsync(
                    state.ActiveQaContextPath!,
                    state.ActiveQaContextHash!,
                    cancellationToken))
            {
                throw new OutcomeVerificationValidationException(
                    [
                        "the persisted QA context no longer matches its host-recorded dispatch hash"
                    ]);
            }
            qaResult = OutcomeVerificationRules.ParseQaResult(
                output,
                plan,
                candidate.Fingerprint,
                state.Evidence.Select(item => item.EvidenceId).ToArray(),
                state.PlannedRoles);
            var hostObservationErrors =
                HostObservedQaEvidence.ValidatePassChecks(
                    qaResult,
                    hostToolCalls,
                    string.IsNullOrWhiteSpace(flow.WorkspacePath)
                        ? flow.RepositoryPath
                        : flow.WorkspacePath,
                    candidate.Manifest.Repositories
                        .Select(repository => repository.RelativePath)
                        .ToArray());
            if (hostObservationErrors.Count > 0)
            {
                throw new OutcomeVerificationValidationException(
                    hostObservationErrors);
            }
        }
        catch (OutcomeVerificationValidationException exception)
        {
            qaResult = null;
            contractError = SummarizeQaContractErrors(exception.Errors);
        }

        var stale = false;
        string? staleReason = null;
        try
        {
            var current = await (candidateFingerprintService
                ?? throw new InvalidOperationException(
                    "No candidate fingerprint service is configured."))
                .PrepareAsync(
                    flow,
                    plan.Hash,
                    candidate.PreparedByStepId,
                    CandidateFingerprintService.RequiresPreview(plan),
                    cancellationToken);
            stale = !string.Equals(
                current.Fingerprint,
                candidate.Fingerprint,
                StringComparison.Ordinal);
            if (stale)
            {
                staleReason = "Quality Engineer changed candidate content during verification.";
            }
        }
        catch (CandidateValidationException exception)
        {
            stale = true;
            staleReason =
                "The candidate became invalid during QA: " + exception.Message;
        }

        var round = new OutcomeQaRound
        {
            Round = state.ActiveQaRound.Value,
            QaStepId = step.Id,
            AcceptancePlanHash = plan.Hash,
            CandidateFingerprint = candidate.Fingerprint,
            ContextHash = state.ActiveQaContextHash!,
            Verdict = qaResult?.Verdict ?? OutcomeQaVerdict.FAIL,
            Result = qaResult,
            ContractError = contractError,
            Stale = stale,
            CompletedAt = completedAt
        };
        state.Rounds.Add(round);
        MarkSemanticRootProcessed(state, GetStableSemanticRootId(step));
        state.ActiveQaRound = null;
        state.ActiveQaStepId = null;
        state.ActiveQaContextPath = null;
        state.ActiveQaContextHash = null;
        var correctableCriterionIds = qaResult?.Criteria
            .Where(item =>
                item.Status != OutcomeCriterionStatus.PASS &&
                item.ResponsibleRoles.Count > 0)
            .Select(item => item.CriterionId)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        state.PendingOwnerRoles = DerivePendingOwnerRoles(
            state,
            correctableCriterionIds);
        var releaseReady =
            qaResult?.Verdict == OutcomeQaVerdict.PASS &&
            !stale &&
            qaResult.PlanGaps.Count == 0;
        if (releaseReady)
        {
            state.Status = OutcomeVerificationStatus.Passed;
            state.VerifiedCandidateFingerprint = candidate.Fingerprint;
            state.VerifiedAt = completedAt;
            state.PendingOwnerRoles.Clear();
            state.Stale = false;
        }
        else if (stale)
        {
            state.Status = OutcomeVerificationStatus.AwaitingCandidateRefresh;
            state.VerifiedCandidateFingerprint = null;
            state.VerifiedAt = null;
            state.Stale = true;
        }
        else
        {
            var hasPlanGaps = qaResult?.PlanGaps.Count > 0;
            var externalBlocker =
                !hasPlanGaps &&
                qaResult?.Verdict == OutcomeQaVerdict.BLOCKED &&
                qaResult.Criteria
                    .Where(item => item.Status != OutcomeCriterionStatus.PASS)
                    .All(item => item.ResponsibleRoles.Count == 0);
            state.Status = externalBlocker ||
                           state.Rounds.Count >= state.MaxRounds
                ? OutcomeVerificationStatus.AwaitingHumanResolution
                : OutcomeVerificationStatus.Correcting;
            state.VerifiedCandidateFingerprint = null;
            state.VerifiedAt = null;
            state.Stale = false;
        }
        state.UpdatedAt = completedAt;
        flow.OutcomeVerificationJson =
            OutcomeVerificationRules.SerializeAggregate(state);
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = step.Id,
            Type = stale
                ? "outcome.candidate.stale"
                : !string.IsNullOrWhiteSpace(contractError)
                    ? "outcome.qa.contract-invalid"
                    : releaseReady
                        ? "outcome.qa.passed"
                        : qaResult?.Verdict == OutcomeQaVerdict.BLOCKED
                            ? "outcome.qa.blocked"
                            : "outcome.qa.failed",
            Message = stale
                ? staleReason!
                : !string.IsNullOrWhiteSpace(contractError)
                    ? $"QA round {round.Round} returned an invalid strict contract: {contractError}"
                    : $"QA round {round.Round} completed with {qaResult!.Verdict}."
        });
        return new QaCompletion(
            !string.IsNullOrWhiteSpace(contractError),
            releaseReady);
    }

    internal static string SummarizeQaContractErrors(
        IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var normalized = errors
            .Select(error => error?.Trim() ?? string.Empty)
            .Where(error => error.Length > 0)
            .ToArray();
        var summary = normalized.Length == 0
            ? "The QA result violated the strict contract."
            : string.Join("; ", normalized);
        if (summary.Length <= MaximumQaContractErrorCharacters)
        {
            return summary;
        }

        var suffix =
            $" … [{normalized.Length} diagnostics; {OutcomeVerificationRules.ComputeSha256(summary)}]";
        var prefixLength = MaximumQaContractErrorCharacters - suffix.Length;
        if (prefixLength < 1)
        {
            return suffix[^MaximumQaContractErrorCharacters..];
        }
        if (prefixLength < summary.Length &&
            prefixLength > 0 &&
            char.IsHighSurrogate(summary[prefixLength - 1]))
        {
            prefixLength--;
        }
        return summary[..prefixLength] + suffix;
    }

    private static void CompleteOutcomeCorrection(
        HarnessDbContext database,
        FlowRun flow,
        FlowStep step,
        DateTimeOffset completedAt)
    {
        if (string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson) ||
            !IsOutcomeOwnerCorrectionStep(step))
        {
            return;
        }
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        if (!ApplyOutcomeCorrection(database, state, step, completedAt))
        {
            return;
        }
        flow.OutcomeVerificationJson =
            OutcomeVerificationRules.SerializeAggregate(state);
    }

    private static bool ApplyOutcomeCorrection(
        HarnessDbContext database,
        OutcomeVerificationState state,
        FlowStep step,
        DateTimeOffset completedAt)
    {
        if (step.OutcomeQaRound is not { } qaRound ||
            string.IsNullOrWhiteSpace(step.OutcomePlanHash) ||
            step.StableSemanticRootId is not { } semanticRootId ||
            semanticRootId == Guid.Empty)
        {
            return false;
        }
        var round = state.Rounds.SingleOrDefault(item =>
            item.Round == qaRound);
        if (round is null)
        {
            return false;
        }
        var registration = round.CorrectionRegistrations.SingleOrDefault(item =>
            item.QaRound == qaRound &&
            string.Equals(
                item.OutcomePlanHash,
                step.OutcomePlanHash,
                StringComparison.Ordinal) &&
            string.Equals(item.Role, step.AgentRole, StringComparison.Ordinal) &&
            item.SemanticRootId == semanticRootId);
        if (registration is null ||
            round.ProcessedCorrectionRootIds.Contains(semanticRootId))
        {
            return false;
        }
        if (!state.PendingOwnerRoles.Remove(step.AgentRole))
        {
            throw new InvalidOperationException(
                $"Correction root '{semanticRootId:D}' was registered for QA round {qaRound}, " +
                $"but role '{step.AgentRole}' is not pending.");
        }

        round.ProcessedCorrectionRootIds.Add(semanticRootId);
        if (!round.CorrectionStepIds.Contains(step.Id))
        {
            round.CorrectionStepIds.Add(step.Id);
        }
        MarkSemanticRootProcessed(state, semanticRootId);
        if (state.PendingOwnerRoles.Count == 0)
        {
            state.Status = OutcomeVerificationStatus.AwaitingCandidateRefresh;
        }
        state.UpdatedAt = completedAt;
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = step.FlowRunId,
            FlowStepId = step.Id,
            Type = "outcome.correction.completed",
            Message =
                $"{step.AgentName} completed its assigned outcome corrections."
        });
        return true;
    }

    private static void EnsureCorrectionRegistration(
        OutcomeQaRound round,
        FlowStep correction,
        string outcomePlanHash)
    {
        if (correction.OutcomeQaRound != round.Round ||
            !string.Equals(
                correction.OutcomePlanHash,
                outcomePlanHash,
                StringComparison.Ordinal) ||
            correction.StableSemanticRootId is not { } semanticRootId ||
            semanticRootId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "A correction step does not carry the expected typed QA round identity.");
        }
        var existing = round.CorrectionRegistrations.SingleOrDefault(item =>
            string.Equals(item.Role, correction.AgentRole, StringComparison.Ordinal));
        if (existing is not null)
        {
            if (existing.QaRound != round.Round ||
                !string.Equals(
                    existing.OutcomePlanHash,
                    outcomePlanHash,
                    StringComparison.Ordinal) ||
                existing.SemanticRootId != semanticRootId)
            {
                throw new InvalidOperationException(
                    $"QA round {round.Round} has a conflicting correction registration for '{correction.AgentRole}'.");
            }
            return;
        }
        round.CorrectionRegistrations.Add(new OutcomeCorrectionRegistration(
            round.Round,
            outcomePlanHash,
            correction.AgentRole,
            correction.Id,
            semanticRootId));
    }

    private static async Task ApplyAcceptancePlanReplacementAsync(
        HarnessDbContext database,
        FlowRun flow,
        FlowStep step,
        string output,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        if (!IsOutcomePlanCorrectionStep(step))
        {
            throw new InvalidOperationException(
                "Only an outcome plan-correction step can replace the acceptance plan.");
        }
        var prior = state.AcceptancePlan
            ?? throw new InvalidOperationException(
                "An acceptance-plan correction requires the prior plan.");
        var plannedRoles = await database.FlowSteps
            .AsNoTracking()
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.Iteration == flow.Iteration &&
                item.InvocationKind !=
                    ExecutionInvocationKind.Planning &&
                item.InvocationKind !=
                    ExecutionInvocationKind.PreMortem)
            .OrderBy(item => item.Sequence)
            .Select(item => item.AgentRole)
            .ToListAsync(cancellationToken);
        plannedRoles = plannedRoles
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var replacement = OutcomeVerificationRules.ParseAcceptancePlan(
            output,
            plannedRoles);
        var replacementIds = replacement.Criteria
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var omittedIds = prior.Criteria
            .Select(item => item.Id)
            .Where(id => !replacementIds.Contains(id))
            .ToArray();
        if (omittedIds.Length > 0)
        {
            throw new OutcomeVerificationValidationException(
                [
                    "A replacement acceptance plan must preserve existing criterion IDs: " +
                    string.Join(", ", omittedIds)
                ]);
        }

        var priorById = prior.Criteria.ToDictionary(
            item => item.Id,
            StringComparer.Ordinal);
        var changedCriteria = replacement.Criteria
            .Where(criterion =>
                !priorById.TryGetValue(criterion.Id, out var oldCriterion) ||
                !string.Equals(
                    OutcomeVerificationRules.SerializeCanonical(oldCriterion),
                    OutcomeVerificationRules.SerializeCanonical(criterion),
                    StringComparison.Ordinal))
            .ToArray();
        if (changedCriteria.Length == 0)
        {
            throw new OutcomeVerificationValidationException(
                ["A plan-gap correction must add or change at least one criterion"]);
        }

        var changedIds = changedCriteria
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var triggeringRound = step.OutcomeQaRound is { } roundNumber
            ? state.Rounds.FirstOrDefault(item =>
                item.Round == roundNumber &&
                string.Equals(
                    item.AcceptancePlanHash,
                    step.OutcomePlanHash,
                    StringComparison.Ordinal))
            : state.Rounds
                .OrderByDescending(item => item.Round)
                .FirstOrDefault();
        var unresolvedExistingCriterionIds = triggeringRound?.Result?.Criteria
            .Where(item => item.Status != OutcomeCriterionStatus.PASS)
            .Select(item => item.CriterionId)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var pendingCriterionIds = unresolvedExistingCriterionIds
            .Union(changedIds, StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        state.AcceptancePlan =
            OutcomeVerificationRules.CreateAcceptanceSnapshot(
                replacement,
                step.Id);
        state.PlannedRoles = plannedRoles;
        foreach (var round in state.Rounds)
        {
            round.Stale = true;
        }
        state.Evidence = state.Evidence
            .Where(item => !changedIds.Contains(item.CriterionId))
            .ToList();
        state.CurrentCandidate = null;
        state.Publication = null;
        state.VerifiedCandidateFingerprint = null;
        state.VerifiedAt = null;
        state.Stale = true;
        state.PendingOwnerRoles = OrderRolesByPlan(
            plannedRoles,
            replacement.Criteria
                .Where(item => pendingCriterionIds.Contains(item.Id))
                .SelectMany(item => item.OwnerRoles));
        state.Status = OutcomeVerificationStatus.Correcting;
        MarkSemanticRootProcessed(state, GetStableSemanticRootId(step));
        state.UpdatedAt = completedAt;
        flow.OutcomeVerificationJson =
            OutcomeVerificationRules.SerializeAggregate(state);
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = step.Id,
            Type = "outcome.plan.replaced",
            Message =
                $"Team Lead replaced the acceptance plan after QA identified a gap. " +
                $"The new hash is {PrefixDigest(state.AcceptancePlan.Hash)}."
        });
    }

    private async Task PrepareOutcomeCandidateAsync(
        HarnessDbContext database,
        FlowRun flow,
        FlowStep releaseStep,
        CancellationToken cancellationToken)
    {
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        var plan = state.AcceptancePlan
            ?? throw new InvalidOperationException(
                "A release candidate cannot be prepared before the acceptance plan.");
        if (state.CurrentCandidate?.PreparedByStepId == releaseStep.Id &&
            state.Status == OutcomeVerificationStatus.AwaitingQa)
        {
            return;
        }
        var previousFingerprint = state.CurrentCandidate?.Fingerprint;
        var missingEvidence = plan.Criteria
            .Where(criterion => !state.Evidence.Any(evidence =>
                string.Equals(
                    evidence.CriterionId,
                    criterion.Id,
                    StringComparison.Ordinal)))
            .Select(criterion => criterion.Id)
            .ToArray();
        if (missingEvidence.Length > 0)
        {
            throw new OutcomeVerificationValidationException(
                [
                    "Every criterion requires concrete delivery evidence before candidate " +
                    $"preparation. Missing: {string.Join(", ", missingEvidence)}"
                ]);
        }

        var fingerprintService = candidateFingerprintService
            ?? throw new InvalidOperationException(
                "No candidate fingerprint service is configured.");
        _ = await fingerprintService.SealAsync(flow, cancellationToken);
        var candidate = await fingerprintService.PrepareAsync(
            flow,
            plan.Hash,
            releaseStep.Id,
            CandidateFingerprintService.RequiresPreview(plan),
            cancellationToken);
        releaseStep.OutcomePlanHash = plan.Hash;
        releaseStep.StableSemanticRootId ??= GetStableSemanticRootId(releaseStep);
        if (releaseStep.Kind == FlowStepKind.Standard)
        {
            releaseStep.Kind = state.CurrentCandidate is null
                ? FlowStepKind.OutcomeLocalReleaseCandidate
                : FlowStepKind.OutcomeCandidateRefresh;
        }
        state.CurrentCandidate = candidate;
        state.Publication = null;
        state.VerifiedCandidateFingerprint = null;
        state.VerifiedAt = null;
        state.Stale = false;
        state.Status = OutcomeVerificationStatus.AwaitingQa;
        MarkSemanticRootProcessed(
            state,
            GetStableSemanticRootId(releaseStep));
        state.UpdatedAt = DateTimeOffset.UtcNow;
        flow.OutcomeVerificationJson =
            OutcomeVerificationRules.SerializeAggregate(state);
        var pendingQaSteps = await database.FlowSteps
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.Iteration == flow.Iteration &&
                item.Kind == FlowStepKind.OutcomeQa &&
                item.Status == StepStatus.Pending)
            .ToListAsync(cancellationToken);
        foreach (var qaStep in pendingQaSteps)
        {
            qaStep.DependsOnStepId = releaseStep.Id;
        }
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = releaseStep.Id,
            Type = "outcome.candidate.prepared",
            Message =
                $"Release Engineer {(previousFingerprint is null ? "prepared" : "refreshed")} " +
                $"unpublished candidate " +
                $"{PrefixDigest(candidate.Fingerprint)} across " +
                $"{candidate.Manifest.Repositories.Count} repository/repositories."
        });
    }

    private static void MarkOutcomeStepStarted(
        HarnessDbContext database,
        FlowRun flow,
        FlowStep step)
    {
        if (string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson) ||
            step.RemotePublicationAllowed)
        {
            return;
        }
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        if ((IsOutcomeLocalReleaseCandidateStep(step) ||
             IsOutcomeCandidateRefreshStep(step)) &&
            state.Status is
                OutcomeVerificationStatus.CollectingEvidence or
                OutcomeVerificationStatus.AwaitingCandidateRefresh)
        {
            state.Status = OutcomeVerificationStatus.PreparingCandidate;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            flow.OutcomeVerificationJson =
                OutcomeVerificationRules.SerializeAggregate(state);
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = step.Id,
                Type = "outcome.candidate.preparing",
                Message =
                    "Release Engineer started local candidate preparation; remote publication remains disabled."
            });
        }
    }

    internal static void CollectOutcomeEvidence(
        HarnessDbContext database,
        FlowRun flow,
        FlowStep step,
        string output,
        DateTimeOffset completedAt)
    {
        if (string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson) ||
            step.RemotePublicationAllowed ||
            !IsGovernedOutcomeEvidenceStep(step))
        {
            return;
        }

        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        if (state.AcceptancePlan is null)
        {
            throw new OutcomeVerificationValidationException(
                ["delivery evidence cannot be accepted before the acceptance plan"]);
        }
        if (state.EvidenceProcessing.Any(item =>
                item.ProducerStepId == step.Id))
        {
            return;
        }

        var assignedCriteria = state.AcceptancePlan.Criteria
            .Where(criterion =>
                criterion.OwnerRoles.Contains(
                    step.AgentRole,
                    StringComparer.Ordinal))
            .Select(criterion => criterion.Id)
            .ToArray();
        if (assignedCriteria.Length == 0)
        {
            return;
        }

        var evidence = OutcomeVerificationRules.ParseDeliveryEvidence(
            output,
            state.AcceptancePlan,
            step.AgentRole,
            step.Id,
            completedAt);
        var evidencedCriteria = evidence
            .Select(item => item.CriterionId)
            .ToHashSet(StringComparer.Ordinal);
        var missing = assignedCriteria
            .Where(criterionId => !evidencedCriteria.Contains(criterionId))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new OutcomeVerificationValidationException(
                [
                    $"{step.AgentRole} must provide concrete evidence for assigned criteria: " +
                    string.Join(", ", missing)
                ]);
        }

        state.EvidenceProcessing.Add(new OutcomeEvidenceProcessing(
            step.Id,
            state.AcceptancePlan.Hash,
            step.AgentRole,
            evidencedCriteria.Order(StringComparer.Ordinal).ToArray(),
            completedAt));
        MarkSemanticRootProcessed(state, GetStableSemanticRootId(step));
        OutcomeVerificationRules.MergeEvidence(state, evidence);
        flow.OutcomeVerificationJson =
            OutcomeVerificationRules.SerializeAggregate(state);
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = step.Id,
            Type = "outcome.evidence.collected",
            Message =
                $"Recorded {evidence.Count} validated evidence item(s) from " +
                $"{step.AgentName} for {string.Join(", ", evidencedCriteria.Order())}."
        });
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
                $"{step.AgentName} was interrupted by host shutdown; its Copilot session will be reconciled on restart."
        });
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkStepFailedAsync(
        Guid stepId,
        Exception exception,
        long? durationMilliseconds,
        CancellationToken cancellationToken,
        string? diagnosticOutput = null)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var step = await database.FlowSteps.SingleAsync(
            item => item.Id == stepId,
            cancellationToken);
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
                Version = "step-failure-v1",
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

    private async Task ReconcileStudioV2DurableStateAsync(
        CancellationToken cancellationToken)
    {
        List<Guid> flowIds;
        await using (var database =
                     await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            flowIds = await database.Flows
                .AsNoTracking()
                .Where(flow =>
                    flow.ContractVersion == "studio-v2" &&
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
                        ReviewCoordinator.IsStudioPublicationStep(flow, step))
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
                        _lifecycle.Transition(flow, FlowStatus.Approved);
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
                    "Could not reconcile durable studio-v2 state for flow {FlowId}.",
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
                        "studio-v2 restart reconciliation failed: " +
                        exception.GetBaseException().Message;
                }
                AddRecoveryEventOnce(
                    failed,
                    null,
                    "flow.recovery-failed",
                    "studio-v2 restart reconciliation failed and requires operator attention.",
                    JsonSerializer.Serialize(new
                    {
                        Version = "flow-recovery-v1",
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

    internal async Task<IReadOnlyList<Guid>> RecoverInterruptedFlowsAsync(
        CancellationToken cancellationToken)
    {
        List<InterruptedStepCandidate> interruptedSteps;
        List<InterruptedStepCandidate> failedStalledSteps;
        await using (var database = await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            interruptedSteps = await (
                    from step in database.FlowSteps.AsNoTracking()
                    join flow in database.Flows.AsNoTracking()
                        on step.FlowRunId equals flow.Id
                    where step.Status == StepStatus.Running &&
                          (step.InvocationKind !=
                               ExecutionInvocationKind.ReviewClassification ||
                           !database.FlowEvents.Any(flowEvent =>
                               flowEvent.FlowStepId == step.Id &&
                               flowEvent.Type ==
                               ReviewCoordinator
                                   .FeedbackRequestEventType)) &&
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
                        step.Kind == FlowStepKind.OutcomeQa,
                        step.ExecutionPrompt,
                        step.WorkflowRevision,
                        flow.ContractVersion,
                        step.InvocationKind,
                        step.IsOutcomeOwner,
                        flow.Kind))
                .ToListAsync(cancellationToken);
            failedStalledSteps = await (
                    from step in database.FlowSteps.AsNoTracking()
                    join flow in database.Flows.AsNoTracking()
                        on step.FlowRunId equals flow.Id
                    where step.Status == StepStatus.Failed &&
                          (step.InvocationKind !=
                               ExecutionInvocationKind.ReviewClassification ||
                           !database.FlowEvents.Any(flowEvent =>
                               flowEvent.FlowStepId == step.Id &&
                               flowEvent.Type ==
                               ReviewCoordinator
                                   .FeedbackRequestEventType)) &&
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
                        step.Kind == FlowStepKind.OutcomeQa,
                        step.ExecutionPrompt,
                        step.WorkflowRevision,
                        flow.ContractVersion,
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

        if (_reviewCoordinator is not null)
        {
            try
            {
                _ = await _reviewCoordinator
                    .RecoverFeedbackClassificationsAsync(cancellationToken);
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
                    "Could not complete startup review-classification reconciliation; continuing with other recovery work.");
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
        await ReconcileStudioV2DurableStateAsync(cancellationToken);

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
        var waitingGovernedFlows = await flowDatabase.Flows
            .Where(flow =>
                flow.ContractVersion == "legacy-v1" &&
                flow.Status == FlowStatus.WaitingForFeedback &&
                flow.OutcomeVerificationJson != string.Empty)
            .ToListAsync(cancellationToken);
        foreach (var waitingFlow in waitingGovernedFlows)
        {
            try
            {
                var state = OutcomeVerificationRules.DeserializeAggregate(
                    waitingFlow.OutcomeVerificationJson);
                var steps = await flowDatabase.FlowSteps
                    .Where(step =>
                        step.FlowRunId == waitingFlow.Id &&
                        step.Iteration == waitingFlow.Iteration)
                    .ToListAsync(cancellationToken);
                if (state.Status == OutcomeVerificationStatus.Passed)
                {
                    var current = await EnsurePassedReleaseGateAsync(
                        flowDatabase,
                        waitingFlow,
                        state,
                        steps,
                        cancellationToken);
                    if (!current)
                    {
                        _lifecycle.Transition(
                            waitingFlow,
                            FlowStatus.Queued);
                        resumedFlowIds.Add(waitingFlow.Id);
                    }
                }
                else if (state.Status ==
                         OutcomeVerificationStatus.AwaitingHumanResolution)
                {
                    await EnsureOutcomeResolutionGateAsync(
                        flowDatabase,
                        waitingFlow,
                        state,
                        steps,
                        cancellationToken);
                }
                else
                {
                    _lifecycle.Transition(
                        waitingFlow,
                        FlowStatus.Queued);
                    resumedFlowIds.Add(waitingFlow.Id);
                }
            }
            catch (Exception exception) when (
                exception is
                    OutcomeVerificationValidationException or
                    CandidateValidationException or
                    InvalidOperationException)
            {
                _lifecycle.Transition(
                    waitingFlow,
                    FlowStatus.Failed);
                waitingFlow.FailureReason =
                    "Outcome-verification restart reconciliation failed: " +
                    exception.Message;
                waitingFlow.UpdatedAt = DateTimeOffset.UtcNow;
                flowDatabase.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = waitingFlow.Id,
                    Type = "outcome.reconciliation.failed",
                    Message = waitingFlow.FailureReason
                });
            }
        }
        await flowDatabase.SaveChangesAsync(cancellationToken);
        await CleanupConclusiveStagedSessionsAsync(cancellationToken);

        logger.LogInformation(
            "Reconciled {StepCount} interrupted step(s) across {FlowCount} flow(s).",
            interruptedSteps.Count + failedStalledSteps.Count,
            resumedFlowIds.Count);
        return resumedFlowIds.ToList();
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
        if (candidate.ContractVersion == "studio-v2" &&
            !candidate.IsPreMortemRevision &&
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
        else if (!candidate.IsOutcomeQa &&
            (snapshot is null || snapshot.State == CopilotSessionJournalState.Missing))
        {
            snapshot = await sessionJournal.DiscoverLatestAsync(
                copilotHome,
                candidate.WorkspacePath,
                candidate.AgentName,
                candidate.StartedAt,
                cancellationToken);
        }
        if (snapshot is not
            {
                State: CopilotSessionJournalState.Completed,
                Result: { Success: true } recoveredResult
            } ||
            !(candidate.IsOutcomeQa ||
              CopilotReasoningHost.IsRecoverableCompletedOutput(
                  candidate.AgentRole,
                  recoveredResult.OutputSummary,
                  candidate.IsPreMortemRevision,
                  contractVersion: candidate.ContractVersion,
                  invocationKind: candidate.InvocationKind,
                  isOutcomeOwner: candidate.IsOutcomeOwner,
                  planStepKey: candidate.PlanStepKey,
                  expectedFlowKind: candidate.FlowKind)) ||
            !CopilotReasoningHost.IsRecoveryCurrent(
                candidate.StartedAt,
                snapshot.CompletedAt))
        {
            return false;
        }

        var recoveredExecution = ToRecoveredExecutionResult(
            recoveredResult,
            snapshot.SessionId);
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

    internal async Task<FlowRun> ResolveOutcomeAsync(
        Guid flowId,
        Guid gateId,
        OutcomeResolutionAction action,
        string reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1_000)
        {
            throw new ArgumentException(
                "Outcome resolution requires a reason containing 1-1000 characters.",
                nameof(reason));
        }

        await _manualRestartGate.WaitAsync(cancellationToken);
        try
        {
            await using var lifecycleLease =
                await _lifecycle.EnterAsync(flowId, cancellationToken);
            await using var database =
                await databaseFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction =
                await database.Database.BeginTransactionAsync(cancellationToken);
            var flow = await database.Flows
                .AsSplitQuery()
                .Include(item => item.Steps)
                .ThenInclude(step => step.ToolCalls)
                .Include(item => item.Steps)
                .ThenInclude(step => step.RoutingDecisions)
                .ThenInclude(decision => decision.TaskProfile)
                .Include(item => item.Messages)
                .Include(item => item.Events)
                .Include(item => item.GateRecords)
                .Include(item => item.AgentSnapshots)
                .SingleOrDefaultAsync(item => item.Id == flowId, cancellationToken)
                ?? throw new KeyNotFoundException(
                    $"Factory flow '{flowId}' was not found.");
            if (flow.Status != FlowStatus.WaitingForFeedback ||
                string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson))
            {
                throw new InvalidOperationException(
                    "Only a governed flow awaiting outcome resolution can be continued or replanned.");
            }
            var state = OutcomeVerificationRules.DeserializeAggregate(
                flow.OutcomeVerificationJson);
            if (state.Status != OutcomeVerificationStatus.AwaitingHumanResolution)
            {
                throw new InvalidOperationException(
                    "The flow is not awaiting an outcome-resolution decision.");
            }
            var gate = flow.GateRecords.SingleOrDefault(item =>
                item.Id == gateId &&
                item.ActionType == HandoffActionType.OutcomeResolution &&
                !item.Resolved)
                ?? throw new InvalidOperationException(
                    "The requested outcome-resolution gate is not current.");
            if (action == OutcomeResolutionAction.Continue &&
                state.ManualRoundsGranted >= 10)
            {
                throw new InvalidOperationException(
                    "This iteration already used the maximum ten manually added QA rounds.");
            }
            if (!Enum.IsDefined(action))
            {
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
            }
            var resolved = handoffGate.PrepareResolution(
                gate,
                approved: true,
                "operator",
                $"{action}: {reason.Trim()}");

            if (action == OutcomeResolutionAction.Continue)
            {
                state.ManualRoundsGranted++;
                state.MaxRounds++;
                var latest = state.Rounds
                    .OrderByDescending(item => item.Round)
                    .FirstOrDefault();
                if (state.Stale)
                {
                    state.Status = OutcomeVerificationStatus.AwaitingCandidateRefresh;
                }
                else if (latest?.Result?.PlanGaps.Count > 0 &&
                         string.Equals(
                             latest.AcceptancePlanHash,
                             state.AcceptancePlan?.Hash,
                             StringComparison.Ordinal))
                {
                    state.Status = OutcomeVerificationStatus.Correcting;
                }
                else if (latest?.Result is not null)
                {
                    var correctableIds = latest.Result.Criteria
                        .Where(item => item.Status != OutcomeCriterionStatus.PASS)
                        .Where(item => item.ResponsibleRoles.Count > 0)
                        .Select(item => item.CriterionId)
                        .ToHashSet(StringComparer.Ordinal);
                    state.PendingOwnerRoles = DerivePendingOwnerRoles(
                        state,
                        correctableIds);
                    state.Status = state.PendingOwnerRoles.Count > 0
                        ? OutcomeVerificationStatus.Correcting
                        : OutcomeVerificationStatus.AwaitingQa;
                }
                else
                {
                    state.Status = OutcomeVerificationStatus.AwaitingQa;
                }
                state.UpdatedAt = DateTimeOffset.UtcNow;
                flow.OutcomeVerificationJson =
                    OutcomeVerificationRules.SerializeAggregate(state);
                _lifecycle.Transition(flow, FlowStatus.Queued);
                flow.FailureReason = string.Empty;
                flow.OutcomeLabel = string.Empty;
                flow.OutcomeUrl = string.Empty;
                flow.UpdatedAt = DateTimeOffset.UtcNow;
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = gate.FlowStepId,
                    Type = "outcome.resolution.continued",
                    Message =
                        $"Operator granted exactly one additional QA round. Reason: {reason.Trim()}"
                });
            }
            else if (action == OutcomeResolutionAction.Replan)
            {
                flow.Iteration++;
                state = OutcomeVerificationRules.StartNextIteration(
                    state,
                    flow.Iteration,
                    workflowProvider.GetValidated()
                        .Config.OutcomeVerification.MaxRounds);
                flow.OutcomeVerificationJson =
                    OutcomeVerificationRules.SerializeAggregate(state);
                _lifecycle.Transition(flow, FlowStatus.Queued);
                flow.FailureReason = string.Empty;
                flow.CompletedAt = null;
                flow.OutcomeLabel = string.Empty;
                flow.OutcomeUrl = string.Empty;
                flow.UpdatedAt = DateTimeOffset.UtcNow;
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = gate.FlowStepId,
                    Type = "outcome.resolution.replanned",
                    Message =
                        $"Operator began iteration {flow.Iteration} with a new acceptance plan. " +
                        $"Reason: {reason.Trim()}"
                });
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
            }

            gate.Resolved = resolved.Resolved;
            gate.Approved = resolved.Approved;
            gate.ResolvedBy = resolved.ResolvedBy;
            gate.ResolutionNote = resolved.ResolutionNote;
            gate.ResolvedAt = resolved.ResolvedAt;
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            handoffGate.RestoreHistory([resolved]);
            return flow;
        }
        finally
        {
            _manualRestartGate.Release();
        }
    }

    internal async Task<bool> EnsureVerifiedCandidateCurrentAsync(
        Guid flowId,
        CancellationToken cancellationToken)
    {
        await _manualRestartGate.WaitAsync(cancellationToken);
        try
        {
            await using var lifecycleLease =
                await _lifecycle.EnterAsync(flowId, cancellationToken);
            await using var database =
                await databaseFactory.CreateDbContextAsync(cancellationToken);
            var flow = await database.Flows.SingleOrDefaultAsync(
                item => item.Id == flowId,
                cancellationToken)
                ?? throw new KeyNotFoundException(
                    $"Factory flow '{flowId}' was not found.");
            if (string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson))
            {
                return true;
            }
            var state = OutcomeVerificationRules.DeserializeAggregate(
                flow.OutcomeVerificationJson);
            if (state.Status != OutcomeVerificationStatus.Passed ||
                state.CurrentCandidate is null ||
                state.Stale ||
                !string.Equals(
                    state.CurrentCandidate.Fingerprint,
                    state.VerifiedCandidateFingerprint,
                    StringComparison.Ordinal))
            {
                return false;
            }

            var current = false;
            string? staleReason = null;
            try
            {
                current = await (candidateFingerprintService
                    ?? throw new InvalidOperationException(
                        "No candidate fingerprint service is configured."))
                    .IsCurrentAsync(
                        flow,
                        state.CurrentCandidate,
                        CandidateFingerprintService.RequiresPreview(
                            state.AcceptancePlan),
                        cancellationToken);
            }
            catch (CandidateValidationException exception)
            {
                staleReason = exception.Message;
            }
            if (current)
            {
                return true;
            }
            if (flow.Status == FlowStatus.Approved)
            {
                return false;
            }

            await using var transaction =
                await database.Database.BeginTransactionAsync(cancellationToken);
            await MarkCandidateStaleAsync(
                database,
                flow,
                state,
                null,
                "Candidate changed before customer preview access; unverified content was not served. " +
                (staleReason ?? string.Empty),
                cancellationToken);
            _lifecycle.Transition(flow, FlowStatus.Queued);
            flow.OutcomeUrl = string.Empty;
            flow.OutcomeLabel = string.Empty;
            flow.UpdatedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return false;
        }
        finally
        {
            _manualRestartGate.Release();
        }
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
            if (flow.ContractVersion == "legacy-v1" &&
                !_legacyCatalogCompatibility &&
                flow.AgentSnapshots.Count == 0)
            {
                _flowAgentSnapshots.CaptureForLegacyReactivation(
                    database,
                    flow);
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
            if (flow.ContractVersion == "legacy-v1" &&
                flow.AgentSnapshots.Count > 0 &&
                !flow.AgentSnapshots.Any(snapshot =>
                    string.Equals(
                        snapshot.AgentId,
                        failedStep.AgentId,
                        StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"The legacy flow cannot be restarted because no durable definition exists for historical agent '{failedStep.AgentId}'.");
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
            var isOutcomeQa = IsOutcomeQaStep(failedStep);
            if (flow.ContractVersion == "studio-v2" &&
                failedStep.PreMortemReviewStepId is null &&
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
            else if (!isOutcomeQa &&
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

            if (snapshot is
                {
                    State: CopilotSessionJournalState.Completed,
                    Result: { Success: true } recoveredResult
                } &&
                failedStep.Status == StepStatus.Failed &&
                (IsPreMortemStep(failedStep) ||
                 failedStep.PreMortemReviewStepId is not null ||
                 failedStep.Phase is AgentRunPhase.Stalled or AgentRunPhase.TimedOut) &&
                (isOutcomeQa ||
                 CopilotReasoningHost.IsRecoverableCompletedOutput(
                     failedStep.AgentRole,
                     recoveredResult.OutputSummary,
                     failedStep.PreMortemReviewStepId is not null,
                     contractVersion: flow.ContractVersion,
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
                    $"{Environment.NewLine}Inspect existing workspace changes before editing.";
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
                    Kind = failedStep.Kind,
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
                    OutcomeQaRound = failedStep.OutcomeQaRound,
                    OutcomePlanHash = failedStep.OutcomePlanHash,
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
                retryStep.Kind = failedStep.Kind;
                retryStep.PlanStepKey = failedStep.PlanStepKey;
                retryStep.PlanDutiesJson = failedStep.PlanDutiesJson;
                retryStep.PlanStage = failedStep.PlanStage;
                retryStep.InvocationKind = failedStep.InvocationKind;
                retryStep.IsOutcomeOwner = failedStep.IsOutcomeOwner;
                retryStep.OutcomeQaRound = failedStep.OutcomeQaRound;
                retryStep.OutcomePlanHash = failedStep.OutcomePlanHash;
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
            if (flow.ContractVersion == "studio-v2" &&
                !await database.TaskProfiles.AnyAsync(
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
            RebindActiveQaRetry(flow, failedStep, retryStep);
            var permissionTightened =
                PreserveOrTightenRetryPermission(
                    flow,
                    failedStep,
                    retryStep);
            if (flow.ContractVersion == "studio-v2" &&
                ReviewCoordinator.IsStudioPublicationStep(
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
        if (!string.Equals(
                flow.ContractVersion,
                "studio-v2",
                StringComparison.Ordinal) ||
            flow.Status != FlowStatus.Failed ||
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
        if (candidate.ContractVersion == "studio-v2" &&
            !candidate.IsPreMortemRevision &&
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
        else if (!candidate.IsOutcomeQa &&
            (snapshot is null || snapshot.State == CopilotSessionJournalState.Missing))
        {
            snapshot = await sessionJournal.DiscoverLatestAsync(
                copilotHome,
                candidate.WorkspacePath,
                candidate.AgentName,
                candidate.StartedAt,
                cancellationToken);
            discovered = snapshot is not null;
        }

        if (snapshot?.State == CopilotSessionJournalState.Completed &&
            snapshot.Result is { Success: true } recoveredExplanation &&
            candidate.ContractVersion == "studio-v2" &&
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
            (candidate.IsOutcomeQa ||
             CopilotReasoningHost.IsRecoverableCompletedOutput(
                 candidate.AgentRole,
                 recoveredResult.OutputSummary,
                 candidate.IsPreMortemRevision,
                 contractVersion: candidate.ContractVersion,
                 invocationKind: candidate.InvocationKind,
                 isOutcomeOwner: candidate.IsOutcomeOwner,
                 planStepKey: candidate.PlanStepKey,
                 expectedFlowKind: candidate.FlowKind)) &&
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
        if (candidate.ContractVersion == "studio-v2")
        {
            if (!canResume)
            {
                throw new InvalidOperationException(
                    "The interrupted durable attempt has no recoverable Copilot session; an explicit retry attempt is required.");
            }
            CopilotReasoningHost.ValidatePersistedExecutionInstructions(
                candidate.ExecutionPrompt,
                candidate.WorkflowRevision);
        }
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
        AgentRecord? upstreamOwner;
        FlowStep? upstreamOwnerStep = null;
        if (flow.ContractVersion == "studio-v2")
        {
            (upstreamOwner, upstreamOwnerStep) =
                await ResolveDynamicPushbackOwnerAsync(
                    flow,
                    step,
                    cancellationToken);
        }
        else
        {
            upstreamOwners.TryGetValue(step.AgentId, out upstreamOwner);
        }
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
            pushbackCount = flow.ContractVersion == "studio-v2"
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
                (flow.ContractVersion == "legacy-v1" ||
                 item.PlanStepKey == upstreamOwnerStep!.PlanStepKey))
            .Select(item => (int?)item.Attempt)
            .MaxAsync(cancellationToken) ?? 0) + 1;
        var (revisionStep, retryStep) = CreateRecoverySteps(
            flow,
            blockedStep,
            upstreamOwner,
            revisionAttempt,
            workflowProvider.GetEffective().Revision,
            upstreamOwnerStep);
        var revisionPolicyTightened = false;
        var retryPolicyTightened = false;
        if (flow.ContractVersion == "studio-v2")
        {
            revisionPolicyTightened = PreserveOrTightenRetryPermission(
                flow,
                upstreamOwnerStep!,
                revisionStep);
            retryPolicyTightened = PreserveOrTightenRetryPermission(
                flow,
                blockedStep,
                retryStep);
        }
        database.FlowSteps.AddRange(revisionStep, retryStep);
        if (flow.ContractVersion == "studio-v2")
        {
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
        }
        if (IsOutcomeQaStep(blockedStep))
        {
            var storedFlow = await database.Flows.SingleAsync(
                item => item.Id == flow.Id,
                cancellationToken);
            RebindActiveQaRetry(storedFlow, blockedStep, retryStep);
        }
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
        string? workflowRevision = null,
        FlowStep? upstreamOwnerStep = null)
    {
        var dynamicPlan = flow.ContractVersion == "studio-v2";
        var legacyPlanningRevision =
            !dynamicPlan &&
            string.Equals(
                upstreamOwner.Role,
                "team-lead",
                StringComparison.Ordinal);
        var legacyPlanningRetry =
            !dynamicPlan &&
            string.Equals(
                blockedStep.AgentRole,
                "team-lead",
                StringComparison.Ordinal);
        var revisionStep = new FlowStep
        {
            FlowRunId = flow.Id,
            WorkflowRevision = dynamicPlan
                ? upstreamOwnerStep?.WorkflowRevision ??
                  throw new InvalidOperationException(
                      "studio-v2 pushback recovery requires the owner's workflow revision.")
                : workflowRevision ?? blockedStep.WorkflowRevision,
            Iteration = flow.Iteration,
            Sequence = blockedStep.Sequence + 10,
            AgentId = upstreamOwner.Id,
            AgentName = upstreamOwner.Name,
            AgentRole = upstreamOwner.Role,
            Label = $"Revision after {blockedStep.AgentName} pushback",
            PlanStepKey = dynamicPlan
                ? upstreamOwnerStep?.PlanStepKey ??
                  throw new InvalidOperationException(
                      "studio-v2 pushback recovery requires an owner plan-step identity.")
                : string.Empty,
            PlanDutiesJson = dynamicPlan
                ? upstreamOwnerStep?.PlanDutiesJson ?? "[]"
                : legacyPlanningRevision
                    ? SerializePlanDuties(
                        [PlanDuty.Analyze, PlanDuty.Design])
                    : "[]",
            PlanStage = dynamicPlan
                ? upstreamOwnerStep?.PlanStage ?? PlanStage.BeforeReview
                : PlanStage.BeforeReview,
            InvocationKind = dynamicPlan
                ? upstreamOwnerStep?.InvocationKind ??
                  ExecutionInvocationKind.Worker
                : legacyPlanningRevision
                    ? ExecutionInvocationKind.Planning
                    : ExecutionInvocationKind.Worker,
            IsOutcomeOwner = dynamicPlan &&
                upstreamOwnerStep?.IsOutcomeOwner == true,
            PermissionProfile = dynamicPlan
                ? upstreamOwnerStep?.PermissionProfile ??
                  ExecutionPermissionProfile.ReadOnlySource
                : legacyPlanningRevision
                    ? ExecutionPermissionProfile.ReadOnlySource
                    : ExecutionPermissionProfile.WorkspaceWrite,
            EffectivePermissionJson = dynamicPlan
                ? upstreamOwnerStep?.EffectivePermissionJson ?? string.Empty
                : string.Empty,
            Status = StepStatus.Pending,
            Attempt = revisionAttempt,
            InputSummary =
                $"{blockedStep.AgentName} cannot continue because: " +
                $"{blockedStep.PushbackReason}{Environment.NewLine}{Environment.NewLine}" +
                "Resume your prior work, correct the missing handoff or implementation detail, " +
                $"and explicitly unblock {blockedStep.AgentName}.{Environment.NewLine}{Environment.NewLine}" +
                $"Blocked agent output:{Environment.NewLine}" +
                ClipText(blockedStep.OutputSummary, 2_000)
        };
        var retryStep = new FlowStep
        {
            FlowRunId = flow.Id,
            WorkflowRevision = dynamicPlan
                ? blockedStep.WorkflowRevision
                : workflowRevision ?? blockedStep.WorkflowRevision,
            Iteration = flow.Iteration,
            Sequence = blockedStep.Sequence + 20,
            AgentId = blockedStep.AgentId,
            AgentName = blockedStep.AgentName,
            AgentRole = blockedStep.AgentRole,
            Label = $"Retry after {upstreamOwner.Name} revision",
            PlanStepKey = dynamicPlan ? blockedStep.PlanStepKey : string.Empty,
            PlanDutiesJson = dynamicPlan
                ? blockedStep.PlanDutiesJson
                : legacyPlanningRetry
                    ? SerializePlanDuties(
                        [PlanDuty.Analyze, PlanDuty.Design])
                    : "[]",
            PlanStage = dynamicPlan
                ? blockedStep.PlanStage
                : PlanStage.BeforeReview,
            InvocationKind = dynamicPlan
                ? blockedStep.InvocationKind
                : legacyPlanningRetry
                    ? ExecutionInvocationKind.Planning
                    : ExecutionInvocationKind.Worker,
            IsOutcomeOwner = dynamicPlan && blockedStep.IsOutcomeOwner,
            PermissionProfile = dynamicPlan
                ? blockedStep.PermissionProfile
                : legacyPlanningRetry
                    ? ExecutionPermissionProfile.ReadOnlySource
                    : ExecutionPermissionProfile.WorkspaceWrite,
            EffectivePermissionJson = dynamicPlan
                ? blockedStep.EffectivePermissionJson
                : string.Empty,
            Kind = blockedStep.Kind,
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
            OutcomeQaRound = blockedStep.OutcomeQaRound,
            OutcomePlanHash = blockedStep.OutcomePlanHash,
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
            ClipText(revision.OutputSummary, 3_000);
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
            ClipText(revisionStep.OutputSummary, 3_000);
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
                (flow.ContractVersion == "legacy-v1" ||
                 item.PlanStepKey == upstreamPlanStepKey) &&
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
        if (flow.ContractVersion == "studio-v2" &&
            flow.Kind == FlowKind.Delivery &&
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
        if (!string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson) &&
            workspace.TrustedRepositories is { Count: > 0 })
        {
            var state = OutcomeVerificationRules.DeserializeAggregate(
                flow.OutcomeVerificationJson);
            state.TrustedRepositories = workspace.TrustedRepositories
                .Select(item => new OutcomeTrustedRepository(
                    item.RelativePath,
                    item.RemoteRepository))
                .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
                .ToList();
            state.UpdatedAt = DateTimeOffset.UtcNow;
            flow.OutcomeVerificationJson =
                OutcomeVerificationRules.SerializeAggregate(state);
        }
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
    }

    internal static IReadOnlyDictionary<string, AgentRecord> BuildUpstreamOwners(
        IReadOnlyList<PlannedAgent> plan)
    {
        var orderedPlan = FlowPlanner.OrderGovernedRoles(plan);
        var owners = new Dictionary<string, AgentRecord>(StringComparer.Ordinal);
        for (var index = 1; index < orderedPlan.Count; index++)
        {
            owners[orderedPlan[index].Agent.Id] =
                orderedPlan[index - 1].Agent;
        }
        return owners;
    }

    private async Task<string> BuildOutcomeAssignmentAsync(
        Guid flowId,
        string role,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var json = await database.Flows
            .AsNoTracking()
            .Where(item => item.Id == flowId)
            .Select(item => item.OutcomeVerificationJson)
            .SingleAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        var state = OutcomeVerificationRules.DeserializeAggregate(json);
        return BuildCriterionAssignment(state, role);
    }

    private static string AppendOutcomeAssignment(
        string assignment,
        string outcomeAssignment) =>
        string.IsNullOrWhiteSpace(outcomeAssignment)
            ? assignment
            : $"{assignment}{Environment.NewLine}{Environment.NewLine}" +
              outcomeAssignment;

    private static (string Context, string Contract) BuildOutcomePrompt(
        FlowRun flow,
        FlowStep step)
    {
        if (flow.ContractVersion == "studio-v2")
        {
            return step.IsOutcomeOwner
                ? (
                    "You are the final outcome owner. Consolidate the confirmed goal and the completed plan-step evidence into the customer-review result.",
                    FlowOutcomeResponseContract())
                : (
                    "The accepted dynamic team plan defines this step's duties and outcome ownership.",
                    "No flow-outcome document is required because this is not the final outcome-owner step.");
        }
        if (string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson))
        {
            return (
                "This is a legacy flow without an outcome-verification cycle.",
                "No legacy outcome-verification machine document is required for this flow.");
        }

        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        var context = BuildCriterionAssignment(state, step.AgentRole);
        if (step.InvocationKind == ExecutionInvocationKind.Planning)
        {
            return (context, TeamLeadOutcomeContract());
        }
        if (IsOutcomeQaStep(step))
        {
            return (context, QaOutcomeContract());
        }
        if (step.AgentRole is "pre-mortem-sceptic" or "product-manager")
        {
            return (context, "No outcome-verification machine document is required in this turn.");
        }
        if (step.RemotePublicationAllowed)
        {
            return (
                context,
                "Publish only the already verified candidate. Do not change product files. " +
                "Report the exact published commit and tree identities.");
        }

        var assigned = state.AcceptancePlan?.Criteria.Any(criterion =>
            criterion.OwnerRoles.Contains(step.AgentRole, StringComparer.Ordinal)) == true;
        return (
            context,
            assigned
                ? DeliveryEvidenceOutcomeContract()
                : "No acceptance criterion is assigned to this role; no outcome-evidence document is required.");
    }

    internal static string FlowOutcomeResponseContract() => $$"""
        After the HANDOFF_STATUS marker and concise handoff, output exactly one strict JSON document between these standalone sentinels:
        {{FlowOutcomeParser.BeginSentinel}}
        {"Version":"flow-outcome-v1","Goal":"the confirmed customer goal","Summary":"the concise customer-review result","ImplementationDetails":["specific evidence, recommendation, or delivered behavior"],"Artifacts":[]}
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

    internal static string BuildCriterionAssignment(
        OutcomeVerificationState state,
        string role)
    {
        if (state.AcceptancePlan is null)
        {
            var previous = state.PriorIterations
                .OrderByDescending(item => item.Iteration)
                .FirstOrDefault();
            return previous is null
                ?
                $"Outcome verification status: {state.Status}. " +
                "Create the acceptance plan for the confirmed brief before delivery begins."
                :
                $"Outcome verification status: {state.Status}. Create a complete acceptance " +
                $"plan for iteration {state.Iteration}. Preserve criterion IDs for unchanged " +
                $"requirement lineage and append IDs for new requirements.{Environment.NewLine}" +
                $"Previous plan:{Environment.NewLine}" +
                OutcomeVerificationRules.SerializeCanonical(new
                {
                    previous.AcceptancePlan,
                    previous.Rounds
                });
        }

        var criteria = string.Equals(role, "quality-engineer", StringComparison.Ordinal)
            ? state.AcceptancePlan.Criteria
            : state.AcceptancePlan.Criteria
                .Where(criterion =>
                    criterion.OwnerRoles.Contains(role, StringComparer.Ordinal))
                .ToArray();
        var lines = new List<string>
        {
            $"Acceptance plan: {state.AcceptancePlan.Hash}",
            $"Outcome status: {state.Status}",
            $"QA rounds used: {state.Rounds.Count}/{state.MaxRounds}"
        };
        if (criteria.Count == 0)
        {
            lines.Add($"Assigned criteria for {role}: none.");
        }
        else
        {
            lines.Add($"Assigned criteria for {role}:");
            lines.AddRange(criteria.Select(criterion =>
                $"- {criterion.Id}: {criterion.Requirement}{Environment.NewLine}" +
                $"  Verification: {criterion.Verification}{Environment.NewLine}" +
                $"  Allowed evidence: {string.Join(", ", criterion.EvidenceKinds)}"));
        }
        return string.Join(Environment.NewLine, lines);
    }

    internal static string TeamLeadOutcomeContract() => $$"""
        After the task-profile and pre-mortem documents, output exactly one acceptance plan between these standalone markers:
        {{OutcomeVerificationRules.AcceptanceBeginMarker}}
        {"Version":"outcome-acceptance-v1","Criteria":[{"Id":"AC-001","Requirement":"observable customer or system outcome","Verification":"Run or inspect a concrete check and state the observable expected result.","OwnerRoles":["software-engineer"],"EvidenceKinds":["Test"],"CustomerVisible":true}]}
        {{OutcomeVerificationRules.AcceptanceEndMarker}}
        Property names and enum casing are exact. Define 1-12 criteria with IDs sequentially from AC-001. Each criterion must be an observable outcome, not an activity; name 1-3 unique owners from the supplied downstream plan; never assign quality-engineer; assign release-engineer only to packaging, preview, publication, or release outcomes; and select 1-4 evidence kinds from Test, Command, Artifact, Observation, and SourceInspection. Set CustomerVisible true only when the customer must inspect a generated preview; every such criterion requires a .customer-preview artifact. File existence alone is not verification. Do not emit any marker more than once or inside a Markdown fence.
        Do not select quality-engineer as a pre-mortem checkpoint in a governed flow; the outcome QA round is the independent authoritative verification.
        """;

    internal static string DeliveryEvidenceOutcomeContract() => $$"""
        After the normal handoff, output exactly one evidence document between these standalone markers:
        {{OutcomeVerificationRules.EvidenceBeginMarker}}
        {"Version":"outcome-evidence-v1","Items":[{"CriterionId":"AC-001","Disposition":"Supports","Kind":"Test","Locator":"exact command, artifact, observation, or source location","ObservedResult":"concrete observed result","ExitCode":0,"ContentDigest":null}]}
        {{OutcomeVerificationRules.EvidenceEndMarker}}
        Report evidence only for criterion IDs assigned to this role. Include at least one concrete item for every assigned criterion. Disposition is Supports, Contradicts, or Inconclusive. Kind must be allowed by that criterion. ContentDigest is null or sha256 followed by 64 lowercase hexadecimal characters. Evidence is a claim for independent QA to verify; it never marks a criterion PASS.
        """;

    internal static string QaOutcomeContract() => $$"""
        Inspect the actual candidate and independently verify every acceptance criterion. Upstream evidence is context, never proof by itself. Output exactly one result document between these standalone markers:
        {{OutcomeVerificationRules.QaBeginMarker}}
        {"Version":"outcome-qa-v1","AcceptancePlanHash":"sha256:...","CandidateFingerprint":"sha256:...","Verdict":"FAIL","Criteria":[{"CriterionId":"AC-001","Status":"FAIL","EvidenceIds":[],"ChecksPerformed":[{"Kind":"Command","Locator":"exact check","ObservedResult":"observed result","ExitCode":1}],"Rationale":"bounded evidence-based rationale","ResponsibleRoles":["software-engineer"],"Remediation":"specific correction"}],"PlanGaps":[]}
        {{OutcomeVerificationRules.QaEndMarker}}
        Return exactly one criterion result in plan order. PASS has checks, no responsible roles, and no remediation. FAIL names only criterion owners and requires remediation. BLOCKED clearly identifies the blocker. Report up to three confirmed-requirement omissions in PlanGaps. Never infer PASS from prose or artifact existence.
        """;

    internal static string BuildStepTask(string customerTask, string inputSummary)
    {
        if (string.IsNullOrWhiteSpace(inputSummary))
        {
            return customerTask;
        }

        return
            $"{customerTask}{Environment.NewLine}{Environment.NewLine}" +
            $"## Role-specific assignment{Environment.NewLine}{inputSummary}";
    }

    internal static string BuildPreMortemAssignment(FlowStep target) =>
        "Assume this result was adopted and, six months later, became a disaster. " +
        "Independently reconstruct what failed, what the result missed, and the precise prevention. " +
        "Research the isolated workspace and authoritative sources as needed. Report no more than " +
        "five findings, and report CLEAR when no evidence-backed failure case remains. Keep the " +
        "entire response under 9,000 characters and each finding field under 800 characters." +
        $"{Environment.NewLine}{Environment.NewLine}" +
        $"Evaluated agent: {target.AgentName} ({target.AgentRole})" +
        $"{Environment.NewLine}Evaluated model: " +
        $"{(string.IsNullOrWhiteSpace(target.Model) ? "pending" : target.Model)}" +
        $"{Environment.NewLine}{Environment.NewLine}" +
        $"Evaluated result:{Environment.NewLine}" +
        (string.IsNullOrWhiteSpace(target.OutputSummary)
            ? "The completed result will be attached immediately before this review runs."
            : ClipText(target.OutputSummary, 8_000));

    internal static string BuildPreMortemRevisionAssignment(
        string reviewOutput,
        string agentRole,
        string planDutiesJson = "[]",
        string contractVersion = "legacy-v1")
    {
        var ownsImplementation = contractVersion == "studio-v2"
            ? ReadPlanDuties(planDutiesJson).Contains(PlanDuty.Implement)
            : agentRole is "software-engineer" or "data-engineer" or "release-engineer";
        var preparesOutcome = contractVersion == "studio-v2" &&
                              ReadPlanDuties(planDutiesJson).Contains(
                                  PlanDuty.PrepareOutcome);
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
        "item based on facts. " +
        (ownsImplementation
            ? "If a finding is justified, make the focused corrections owned by this role. "
            : "Do not implement downstream product corrections in this turn; revise this role's " +
              "complete plan, design, or review handoff and assign justified corrections to the " +
              "responsible downstream owner. Stop tool use once that handoff is evidence-based. ") +
        "Return the complete current deliverable or plan, not a delta. " +
        "The next agent must be able to rely on this response alone. " +
        (preparesOutcome
            ? "outcome contract: return exactly 1-24 consolidated ImplementationDetails in the " +
              "complete flow-outcome-v1 document. Replace the previous document; merge overlapping " +
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
        if (flow.ContractVersion != "studio-v2" ||
            string.IsNullOrWhiteSpace(retry.EffectivePermissionJson))
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
                    StringComparison.Ordinal),
                flow.ContractVersion,
                LegacyPublicationAuthorized: false,
                IsGovernedOutcomeVerification: false),
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

    private static Task<bool> HasDurableStudioPublicationApprovalAsync(
        HarnessDbContext database,
        FlowRun flow,
        FlowStep publicationStep,
        CancellationToken cancellationToken)
    {
        if (publicationStep.Iteration != flow.Iteration ||
            !ReviewCoordinator.IsStudioPublicationStep(flow, publicationStep) ||
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
        ClipText(customerTask, 700);

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
        step.Phase = progress.Phase;
        if (progress.ExecutionPrompt is not null)
        {
            step.ExecutionPrompt = progress.ExecutionPrompt;
        }
        if (progress.CopilotSessionId is not null)
        {
            step.CopilotSessionId = progress.CopilotSessionId;
            step.CopilotSessionHome = progress.CopilotSessionHome ?? string.Empty;
        }
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flowId,
            FlowStepId = stepId,
            Type = $"agent.{progress.Phase}",
            Message = progress.Activity
        });
        await database.SaveChangesAsync(cancellationToken);
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
        if (flow.ContractVersion == "studio-v2")
        {
            if (string.IsNullOrWhiteSpace(flow.OutcomeOwnerPlanStepKey))
            {
                throw new InvalidOperationException(
                    "A planned studio-v2 flow has no outcome-owner plan step.");
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
                    "A studio-v2 flow cannot enter review before its outcome owner completes.");
            if (string.IsNullOrWhiteSpace(flow.OutcomeContractJson))
            {
                throw new InvalidOperationException(
                    $"The final outcome owner must return {FlowOutcomeParser.Version} before customer review.");
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
                            Version = "reviewed-candidate-error-v1",
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
                            Version = "advisory-source-verification-v1",
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
                            Version = "advisory-source-verification-v1",
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
            var reviews = flow.GateRecords
                .Where(gate =>
                    gate.ActionType == HandoffActionType.CustomerReview &&
                    currentStepIds.Contains(gate.FlowStepId))
                .OrderBy(gate => gate.DecidedAt)
                .ToList();
            if (reviews.Count(gate => !gate.Resolved) > 1)
            {
                throw new InvalidOperationException(
                    "The current studio-v2 iteration has duplicate unresolved customer reviews.");
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
                            ReviewCoordinator.IsStudioPublicationStep(flow, step))
                        .OrderByDescending(step => step.Sequence)
                        .ThenByDescending(step => step.Attempt)
                        .FirstOrDefault()
                        ?? throw new InvalidOperationException(
                            "Delivery acceptance is durable, but its planned publication has not completed verification.");
                }
                _lifecycle.Transition(flow, FlowStatus.Approved);
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
                    Message =
                        "Harness created the durable generic customer-review gate."
                });
            }
            _lifecycle.Transition(flow, FlowStatus.WaitingForFeedback);
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
        var deliveryOutcome = OutcomeTypeRules.RequireDelivery(
            flow.Outcome,
            nameof(flow.Outcome));
        var governed = !string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson);
        var outcome = governed
            ? OutcomeVerificationRules.DeserializeAggregate(
                flow.OutcomeVerificationJson)
            : null;
        if (outcome?.Status == OutcomeVerificationStatus.AwaitingHumanResolution)
        {
            var hasResolutionGate = await database.GateRecords.AnyAsync(
                item =>
                    item.FlowRunId == flowId &&
                    item.ActionType == HandoffActionType.OutcomeResolution &&
                    !item.Resolved,
                cancellationToken);
            if (!hasResolutionGate)
            {
                throw new InvalidOperationException(
                    "Outcome verification requires a persisted human-resolution gate.");
            }
            _lifecycle.Transition(flow, FlowStatus.WaitingForFeedback);
            flow.OutcomeUrl = string.Empty;
            flow.OutcomeLabel = "Outcome verification needs resolution";
            flow.UpdatedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync(cancellationToken);
            return;
        }
        if (governed && outcome?.Status != OutcomeVerificationStatus.Passed)
        {
            throw new InvalidOperationException(
                $"A governed flow cannot enter customer review while outcome verification is '{outcome?.Status}'.");
        }
        var latestReleaseGate = await database.GateRecords
            .Where(item =>
                item.FlowRunId == flowId &&
                item.ActionType == HandoffActionType.Release &&
                database.FlowSteps.Any(step =>
                    step.Id == item.FlowStepId &&
                    step.Iteration == flow.Iteration))
            .OrderByDescending(item => item.DecidedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (latestReleaseGate is
            {
                Resolved: true,
                Approved: true
            })
        {
            var publicationStep = await database.FlowSteps
                .Where(item =>
                    item.FlowRunId == flowId &&
                    item.Iteration == flow.Iteration &&
                    item.AgentRole == "release-engineer" &&
                    item.RemotePublicationAllowed &&
                    item.Status == StepStatus.Completed)
                .OrderByDescending(item => item.Sequence)
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    "Customer approval was recorded, but the approved release publication did not complete.");
            if (deliveryOutcome == OutcomeType.PullRequest)
            {
                var publicationVerified =
                    string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson) ||
                    OutcomeVerificationRules
                        .DeserializeAggregate(flow.OutcomeVerificationJson)
                        .Publication?.Status ==
                    OutcomePublicationStatus.Verified;
                if (string.IsNullOrWhiteSpace(flow.OutcomeUrl) ||
                    !publicationVerified ||
                    !IsPublishedPullRequestLabel(flow.OutcomeLabel))
                {
                    throw new InvalidOperationException(
                        "The customer-approved pull request publication was not verified.");
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(flow.OutcomeLabel) ||
                    !flow.OutcomeLabel.StartsWith(
                        "Approved commit · ",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The customer-approved commit publication was not verified.");
                }
            }
            _lifecycle.Transition(flow, FlowStatus.Approved);
            flow.CompletedAt = DateTimeOffset.UtcNow;
            flow.UpdatedAt = DateTimeOffset.UtcNow;
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flowId,
                FlowStepId = publicationStep.Id,
                Type = "flow.approved",
                Message =
                    $"Customer-approved outcome was published: {flow.OutcomeLabel}."
            });
            await database.SaveChangesAsync(cancellationToken);
            return;
        }

        var hasReleaseGate = latestReleaseGate is
        {
            Resolved: false
        };
        if (!hasReleaseGate)
        {
            hasReleaseGate = await database.GateRecords.AnyAsync(
            item =>
                item.FlowRunId == flowId &&
                item.ActionType == HandoffActionType.Release &&
                !item.Resolved &&
                database.FlowSteps.Any(step =>
                    step.Id == item.FlowStepId &&
                    step.Iteration == flow.Iteration),
            cancellationToken);
        }
        if (!hasReleaseGate)
        {
            if (governed)
            {
                throw new InvalidOperationException(
                    "A governed flow has a current PASS but no authoritative QA release gate.");
            }
            var lastCompletedStep = await database.FlowSteps
                .Where(item =>
                    item.FlowRunId == flowId &&
                    item.Iteration == flow.Iteration &&
                    item.Status == StepStatus.Completed)
                .OrderByDescending(item => item.Sequence)
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    "The factory cannot create a customer gate without a completed handoff.");
            var releaseGate = handoffGate.SubmitProposal(new HandoffProposal
            {
                FlowRunId = flowId,
                FlowStepId = lastCompletedStep.Id,
                ActionType = HandoffActionType.Release,
                Summary =
                    "The harness created the required customer acceptance checkpoint because no enabled release role produced one.",
                Evidence = lastCompletedStep.OutputSummary,
                BlastRadius = HandoffBlastRadius.High
            });
            database.GateRecords.Add(releaseGate);
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flowId,
                FlowStepId = lastCompletedStep.Id,
                Type = "gate.release-created",
                Message = "Harness created the mandatory customer release gate."
            });
        }

        _lifecycle.Transition(flow, FlowStatus.WaitingForFeedback);
        flow.OutcomeUrl = $"#/preview/{flow.Id}";
        flow.OutcomeLabel = deliveryOutcome == OutcomeType.PullRequest
            ? $"Pull request candidate(s) · {flow.BranchName}"
            : $"Commit candidate(s) · {flow.BranchName}";
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flowId,
            Type = "flow.preview-ready",
            Message = "Customer preview is ready; Product Manager is waiting for feedback."
        });
        await database.SaveChangesAsync(cancellationToken);
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
        bool IsOutcomeQa,
        string ExecutionPrompt,
        string WorkflowRevision,
        string ContractVersion,
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

    private sealed record QaCompletion(
        bool ContractInvalid,
        bool ReleaseReady);

    internal sealed record TeamLeadContract(
        IReadOnlyList<TaskProfile> Profiles,
        IReadOnlySet<string> PreMortemAfterRoles,
        OutcomeAcceptancePlan? AcceptancePlan);
}

public interface IFlowExecutionController
{
    Task<bool> CancelAsync(
        Guid flowId,
        CancellationToken cancellationToken = default);
}

public sealed class FlowWorker : BackgroundService, IFlowExecutionController
{
    private readonly FlowQueue _queue;
    private readonly Func<Guid, CancellationToken, Task> _runFlowAsync;
    private readonly Func<CancellationToken, Task<IReadOnlyList<Guid>>>
        _recoverFlowsAsync;
    private readonly Func<Guid, CancellationToken, Task<bool>> _isRunnableAsync;
    private readonly ILogger<FlowWorker> _logger;
    private readonly Lock _stateLock = new();
    private readonly Dictionary<Guid, FlowDispatchState> _states = [];
    private readonly HashSet<Guid> _blocked = [];
    private bool _stopping;

    public FlowWorker(
        FlowQueue queue,
        WorkflowEngine engine,
        ILogger<FlowWorker> logger)
        : this(
            queue,
            engine.RunAsync,
            engine.RecoverInterruptedFlowsAsync,
            engine.IsRunnableAsync,
            logger)
    {
    }

    internal FlowWorker(
        FlowQueue queue,
        Func<Guid, CancellationToken, Task> runFlowAsync,
        Func<CancellationToken, Task<IReadOnlyList<Guid>>> recoverFlowsAsync,
        Func<Guid, CancellationToken, Task<bool>> isRunnableAsync,
        ILogger<FlowWorker> logger)
    {
        _queue = queue;
        _runFlowAsync = runFlowAsync;
        _recoverFlowsAsync = recoverFlowsAsync;
        _isRunnableAsync = isRunnableAsync;
        _logger = logger;
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
            if (_stopping || _blocked.Contains(flowId))
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
            task = _runFlowAsync(flowId, source.Token);
        }
        catch (Exception exception)
        {
            task = Task.FromException(exception);
        }
        state.Cancellation = source;
        state.ActiveTask = task;
        return (task, source);
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
                "Factory flow {FlowId} was canceled during shutdown",
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
                        _blocked.Contains(flowId))
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
                    _blocked.Contains(flowId))
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

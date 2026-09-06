using System.Collections.Concurrent;
using System.Diagnostics;
using AiHarnessDemo.Data;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Verification;
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
    IVerifiedCandidatePublisher? candidatePublisher = null)
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
    internal const int MaximumQaContractErrorCharacters = 4_000;
    private const string ManualRestartLabelPrefix = "Manual restart of ";
    internal const string ReleaseCandidateAssignment =
        "Prepare the local outcome candidate for independent QA. Generate every browser artifact " +
        "under .customer-preview, leave every repository ready for host sealing from the actual " +
        "working-tree product bytes, and do not create commits, branches, tags, remotes, pushes, " +
        "or pull requests before explicit customer approval. Exclude " +
        ".ai-harness\\outcome-verification from product commits, rerun release-critical checks, " +
        "and report the repository scope plus release evidence. The harness records the final " +
        "sealed HEAD and tree identity for every repository.";
    internal static string ApprovedPublicationAssignment(OutcomeType outcome) =>
        outcome == OutcomeType.PullRequest
            ? "Customer approval is recorded. Publish the already-verified outcome now: push the " +
              "prepared branch and create or reopen the configured pull request. Do not change " +
              "product behavior unless publication itself requires a narrowly scoped correction."
            : "Customer approval is recorded. Finalize the already-verified local commit outcome " +
              "without pushing a branch or creating a pull request. Report the exact commit SHA.";
    internal const string HostControlledPublicationAssignment =
        "Customer approval is recorded. Inspect the already-verified candidate and prepare the " +
        "final release narrative, but do not modify product files, push, or create a pull request. " +
        "The harness will publish only the immutable verified commit and tree identities after " +
        "this Copilot CLI turn completes.";

    private readonly Lock _concurrencyLock = new();
    private readonly SemaphoreSlim _learningGate = new(1, 1);
    private readonly SemaphoreSlim _manualRestartGate = new(1, 1);
    private int _activeFlows;

    public async Task RunAsync(Guid flowId, CancellationToken cancellationToken)
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
        var availableAgents = await agentCatalog.SyncAsync(cancellationToken);
        FlowRun flow;

        await using (var database = await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            flow = await database.Flows.SingleOrDefaultAsync(
                       item => item.Id == flowId,
                       cancellationToken)
                   ?? throw new KeyNotFoundException($"Factory flow '{flowId}' was not found.");

            if (flow.Status is
                FlowStatus.Approved or
                FlowStatus.Abandoned or
                FlowStatus.Abandoning or
                FlowStatus.WaitingForFeedback)
            {
                return;
            }
            if (!string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson))
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
            flow.Status = FlowStatus.Running;
            flow.FailureReason = string.Empty;
            flow.UpdatedAt = DateTimeOffset.UtcNow;
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                Type = "flow.started",
                Message = $"Factory iteration {flow.Iteration} started."
            });
            await database.SaveChangesAsync(cancellationToken);
        }

        var workspace = await workspaceManager.PrepareAsync(flow, cancellationToken);
        await UpdateWorkspaceAsync(flowId, workspace, cancellationToken);

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
            item => item.Enabled && item.Role == PreMortemRole);
        var preMortemAvailable = preMortemAgent is not null && maxPreMortemRounds > 0;

        var lead = plan.FirstOrDefault(item => item.Agent.Role == "team-lead");
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
                    : FlowStepKind.OutcomePlan);
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

        await MarkWaitingForFeedbackAsync(flowId, cancellationToken);
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
        Guid? dependsOnStepId = null)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var existingQuery = database.FlowSteps
            .AsNoTracking()
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.Iteration == flow.Iteration &&
                item.AgentId == planned.Agent.Id &&
                item.Attempt == attempt);
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
                    item.AgentRole == PreMortemRole &&
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
                item.Role == target.AgentRole)
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
            Status = StepStatus.Pending,
            Attempt = round,
            InputSummary = BuildPreMortemAssignment(target),
            PreMortemOriginStepId = originStepId,
            PreMortemTargetStepId = targetStepId
        };
        database.FlowSteps.Add(step);
        database.TaskProfiles.Add(TaskProfileRules.CreatePreMortem(
            sourceProfile,
            step.Id));
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
        CancellationToken cancellationToken)
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
            stepId));
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
                    : null);
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
                        item.AgentRole == "team-lead" &&
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
                item.Agent.Role,
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
            Kind = FlowStepKind.OutcomePlanCorrection,
            Status = StepStatus.Pending,
            Attempt = steps
                .Where(item => item.AgentRole == "team-lead")
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
        flow.Status = FlowStatus.WaitingForFeedback;
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

        try
        {
            await using (var database =
                         await databaseFactory.CreateDbContextAsync(cancellationToken))
            {
                var flow = await database.Flows.SingleAsync(
                    item => item.Id == flowId,
                    cancellationToken);
                var step = await database.FlowSteps.SingleAsync(
                    item => item.Id == stepId,
                    cancellationToken);
                if (step.RemotePublicationAllowed &&
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
                if (step.AgentRole == PreMortemRole)
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
                var persistedSessionId = step.CopilotSessionId;
                var priorSession = persistedSessionId is null &&
                                   step.AgentRole != PreMortemRole
                    ? await database.FlowSteps
                        .AsNoTracking()
                        .Where(item =>
                            item.FlowRunId == flow.Id &&
                            item.Iteration == flow.Iteration &&
                            item.AgentId == step.AgentId &&
                            item.Id != step.Id &&
                            item.CopilotSessionId != null &&
                            (item.Status == StepStatus.Completed ||
                             item.Status == StepStatus.Pushback ||
                             (step.AgentRole == "quality-engineer" &&
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
                        step.AgentRole == PreMortemRole
                            ? $"{step.AgentId}:{step.PreMortemOriginStepId:D}:{step.Attempt}"
                            : step.AgentId);
                var recoversInterruptedSession =
                    step.Phase == AgentRunPhase.CanceledByReconciliation &&
                    persistedSessionId is not null;
                var resumesSession = recoversInterruptedSession || priorSession is not null;
                var copilotSessionHome = !string.IsNullOrWhiteSpace(step.CopilotSessionHome)
                    ? step.CopilotSessionHome
                    : !string.IsNullOrWhiteSpace(priorSession?.CopilotSessionHome)
                        ? priorSession.CopilotSessionHome
                        : sessionJournal.ExpectedHome();
                step.Model = decision.SelectedModel;
                step.ModelEffort = decision.SelectedEffort;
                step.ModelReason = decision.Reason;
                step.Status = StepStatus.Running;
                step.Phase = AgentRunPhase.BuildingPrompt;
                step.StartedAt ??= DateTimeOffset.UtcNow;
                step.CopilotSessionId = copilotSessionId;
                step.CopilotSessionHome = copilotSessionHome;
                flow.UpdatedAt = DateTimeOffset.UtcNow;
                MarkOutcomeStepStarted(database, flow, step);
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
                var previousOutputs = previousSteps
                    .Select(item =>
                        $"{item.AgentName} ({item.AgentRole}){Environment.NewLine}" +
                        item.OutputSummary)
                    .ToList();
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
                    step.AgentRole == PreMortemRole
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
                        step.RemotePublicationAllowed &&
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
                        step.RemotePublicationAllowed &&
                        !string.IsNullOrWhiteSpace(
                            flow.OutcomeVerificationJson),
                    IsGovernedOutcomeVerification:
                        !string.IsNullOrWhiteSpace(
                            flow.OutcomeVerificationJson),
                    GovernedRepositoryRelativePaths:
                        string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson)
                            ? null
                            : OutcomeVerificationRules.DeserializeAggregate(
                                    flow.OutcomeVerificationJson)
                                .TrustedRepositories
                                .Select(repository => repository.RelativePath)
                                .ToArray());
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

        try
        {
            var result = await agentRunner.ExecuteAsync(executionContext, cancellationToken);
            stopwatch.Stop();
            return await CompleteStepAsync(
                flowId,
                stepId,
                result,
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
                cancellationToken);
            throw;
        }
    }

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

        if (pendingStep.AgentRole == PreMortemRole)
        {
            var maximumRounds = await GetMaxHandoffRetriesAsync(
                cancellationToken);
            var scepticEnabled = await IsAgentEnabledAsync(
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
        if (completedStep.AgentRole == PreMortemRole)
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
        if (review.AgentRole != PreMortemRole)
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
        var step = await database.FlowSteps.SingleAsync(
            item => item.Id == stepId,
            cancellationToken);
        if (step.Status != StepStatus.Pending)
        {
            return;
        }

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
                    : "the agent is currently disabled or unavailable.")
        });
        await database.SaveChangesAsync(cancellationToken);
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
                item.AgentRole == PreMortemRole &&
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
            revision.AgentRole);
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
                item.AgentId == target.AgentId)
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
            RemotePublicationAllowed = target.RemotePublicationAllowed,
            Status = StepStatus.Pending,
            Attempt = revisionAttempt,
            InputSummary = BuildPreMortemRevisionAssignment(
                review.OutputSummary,
                target.AgentRole),
            PreMortemOriginStepId = review.PreMortemOriginStepId,
            PreMortemReviewStepId = review.Id
        };
        database.FlowSteps.Add(revision);
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
                                      item.AgentRole == PreMortemRole &&
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
                    item.AgentRole == PreMortemRole &&
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
        await using (var database =
                     await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            sceptic = await database.Agents
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item =>
                        item.Role == PreMortemRole &&
                        item.Enabled,
                    cancellationToken);
        }
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
        string role,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        return await database.Agents
            .AsNoTracking()
            .AnyAsync(
                agent => agent.Role == role && agent.Enabled,
                cancellationToken);
    }

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
                    (item.AgentRole == PreMortemRole ||
                     item.PreMortemReviewStepId != null))
                .OrderBy(item => item.Sequence)
                .Select(item => new ValueTuple<Guid, bool>(
                    item.Id,
                    item.AgentRole == PreMortemRole))
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
                cancellationToken);
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
        if (AgentHandoffInspector.GetPushbackReason(result.Output) is not null)
        {
            return null;
        }

        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var publication = await (
                from step in database.FlowSteps.AsNoTracking()
                join flow in database.Flows.AsNoTracking()
                    on step.FlowRunId equals flow.Id
                where step.Id == stepId && flow.Id == flowId
                select new
                {
                    step.RemotePublicationAllowed,
                    flow
                })
            .SingleAsync(cancellationToken);
        if (!publication.RemotePublicationAllowed ||
            string.IsNullOrWhiteSpace(publication.flow.OutcomeVerificationJson))
        {
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
        if (step.AgentRole == PreMortemRole)
        {
            _ = PreMortemRules.ParseReview(result.Output);
        }
        if (step.PreMortemReviewStepId is not null)
        {
            _ = ValidatePreMortemRevisionOutput(result.Output);
        }
        var pushbackReason = step.AgentRole == PreMortemRole
            ? null
            : AgentHandoffInspector.GetPushbackReason(result.Output);
        var pushedBack = pushbackReason is not null;
        if (step.RemotePublicationAllowed && !pushedBack)
        {
            var verificationOutput = result.Output;
            if (!string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson))
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
            if (!string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson))
            {
                MarkPublicationVerified(flow, step, completedAt);
            }
            flow.OutcomeUrl = published.Url;
            flow.OutcomeLabel = published.Label;
        }
        if (!pushedBack)
        {
            if (step.AgentRole == "team-lead" &&
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
            step.AgentRole == "release-engineer" &&
            step.RemotePublicationAllowed;
        IReadOnlyList<HandoffGateRecord> preparedGateUpdates = [];
        if (step.AgentRole == "release-engineer" &&
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
            : step.AgentRole == "release-engineer" &&
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
                item.AgentRole != "team-lead" &&
                item.AgentRole != PreMortemRole)
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
        CancellationToken cancellationToken)
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
                : $"{step.AgentName} failed: {exception.GetBaseException().Message}"
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
                          (flow.Status == FlowStatus.Queued ||
                           flow.Status == FlowStatus.Running ||
                           flow.Status == FlowStatus.Reworking)
                    select new InterruptedStepCandidate(
                        step.Id,
                        step.FlowRunId,
                        step.AgentName,
                        step.AgentRole,
                        flow.WorkspacePath,
                        step.StartedAt,
                        step.CopilotSessionId,
                        step.CopilotSessionHome,
                        step.PreMortemReviewStepId != null,
                        step.Kind == FlowStepKind.OutcomeQa))
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
                              (step.AgentRole != PreMortemRole ||
                               retry.PreMortemOriginStepId ==
                               step.PreMortemOriginStepId))
                    select new InterruptedStepCandidate(
                        step.Id,
                        step.FlowRunId,
                        step.AgentName,
                        step.AgentRole,
                        flow.WorkspacePath,
                        step.StartedAt,
                        step.CopilotSessionId,
                        step.CopilotSessionHome,
                        step.PreMortemReviewStepId != null,
                        step.Kind == FlowStepKind.OutcomeQa))
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
                    CancellationToken.None);
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

        await using var flowDatabase = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var interruptedFlows = await flowDatabase.Flows
            .Where(flow =>
                flow.Status == FlowStatus.Queued ||
                flow.Status == FlowStatus.Running ||
                flow.Status == FlowStatus.Reworking)
            .ToListAsync(cancellationToken);
        foreach (var flow in interruptedFlows)
        {
            flow.Status = FlowStatus.Queued;
            flow.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await flowDatabase.SaveChangesAsync(cancellationToken);

        var resumedFlowIds = interruptedFlows
            .Select(flow => flow.Id)
            .ToHashSet();
        var waitingGovernedFlows = await flowDatabase.Flows
            .Where(flow =>
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
                        waitingFlow.Status = FlowStatus.Queued;
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
                    waitingFlow.Status = FlowStatus.Queued;
                    waitingFlow.UpdatedAt = DateTimeOffset.UtcNow;
                    resumedFlowIds.Add(waitingFlow.Id);
                }
            }
            catch (Exception exception) when (
                exception is
                    OutcomeVerificationValidationException or
                    CandidateValidationException or
                    InvalidOperationException)
            {
                waitingFlow.Status = FlowStatus.Failed;
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
        if (!candidate.IsOutcomeQa &&
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
                  candidate.IsPreMortemRevision)) ||
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
            flow.Status = FlowStatus.Queued;
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
                flow.Status = FlowStatus.Queued;
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
                flow.Status = FlowStatus.Queued;
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
            flow.Status = FlowStatus.Queued;
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
            await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
            var flow = await database.Flows
                           .AsSplitQuery()
                           .Include(item => item.Steps)
                           .ThenInclude(step => step.ToolCalls)
                           .Include(item => item.Messages)
                           .Include(item => item.Events)
                           .Include(item => item.GateRecords)
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
                FindUnresolvedPushback(iterationSteps) ??
                throw new InvalidOperationException(
                    "The failed flow has no unresolved agent step to restart.");
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
            if (!isOutcomeQa &&
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
                (failedStep.AgentRole == PreMortemRole ||
                 failedStep.Phase is AgentRunPhase.Stalled or AgentRunPhase.TimedOut) &&
                (isOutcomeQa ||
                 CopilotReasoningHost.IsRecoverableCompletedOutput(
                     failedStep.AgentRole,
                     recoveredResult.OutputSummary,
                     failedStep.PreMortemReviewStepId is not null)) &&
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
                flow.Status = FlowStatus.Queued;
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
                return flow;
            }

            var canResume = snapshot is not null &&
                            snapshot.State != CopilotSessionJournalState.Missing;
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
                    Kind = failedStep.Kind,
                    Status = StepStatus.Pending,
                    Phase = canResume
                        ? AgentRunPhase.CanceledByReconciliation
                        : AgentRunPhase.PreparingWorkspace,
                    Attempt = failedStep.AgentRole == PreMortemRole
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
            foreach (var dependent in flow.Steps.Where(step =>
                         step.Status == StepStatus.Pending &&
                         step.DependsOnStepId == failedStep.Id &&
                         step.Id != retryStep.Id))
            {
                dependent.DependsOnStepId = retryStep.Id;
            }
            foreach (var review in flow.Steps.Where(step =>
                         step.AgentRole == PreMortemRole &&
                         step.PreMortemTargetStepId == failedStep.Id &&
                         step.Status is StepStatus.Pending or StepStatus.Skipped))
            {
                review.PreMortemTargetStepId = retryStep.Id;
            }
            RebindActiveQaRetry(flow, failedStep, retryStep);

            var failureReason = flow.FailureReason;
            flow.Status = FlowStatus.Queued;
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
        if (!candidate.IsOutcomeQa &&
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
            snapshot.Result is { Success: true } recoveredResult &&
            (candidate.IsOutcomeQa ||
             CopilotReasoningHost.IsRecoverableCompletedOutput(
                 candidate.AgentRole,
                 recoveredResult.OutputSummary,
                 candidate.IsPreMortemRevision)) &&
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
            return;
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
        var canResume = snapshot is not null &&
                        snapshot.State != CopilotSessionJournalState.Missing;
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

    private async Task<FlowStep> RecoverPushbackAsync(
        FlowRun flow,
        FlowStep step,
        string workspacePath,
        string planSummary,
        int complexity,
        IReadOnlyDictionary<string, AgentRecord> upstreamOwners,
        CancellationToken cancellationToken)
    {
        if (!upstreamOwners.TryGetValue(step.AgentId, out var upstreamOwner))
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
            cancellationToken);

        int pushbackCount;
        int maxHandoffRetries;
        await using (var database = await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            var pushbackRootStepId = step.PushbackRootStepId ?? step.Id;
            pushbackCount = await database.FlowSteps.CountAsync(
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
                item.AgentId == upstreamOwner.Id)
            .Select(item => (int?)item.Attempt)
            .MaxAsync(cancellationToken) ?? 0) + 1;
        var (revisionStep, retryStep) = CreateRecoverySteps(
            flow,
            blockedStep,
            upstreamOwner,
            revisionAttempt);
        database.FlowSteps.AddRange(revisionStep, retryStep);
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
        await database.SaveChangesAsync(cancellationToken);
        return (revisionStep.Id, retryStep.Id);
    }

    internal static (FlowStep RevisionStep, FlowStep RetryStep) CreateRecoverySteps(
        FlowRun flow,
        FlowStep blockedStep,
        AgentRecord upstreamOwner,
        int revisionAttempt)
    {
        var revisionStep = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Sequence = blockedStep.Sequence + 10,
            AgentId = upstreamOwner.Id,
            AgentName = upstreamOwner.Name,
            AgentRole = upstreamOwner.Role,
            Label = $"Revision after {blockedStep.AgentName} pushback",
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
            Iteration = flow.Iteration,
            Sequence = blockedStep.Sequence + 20,
            AgentId = blockedStep.AgentId,
            AgentName = blockedStep.AgentName,
            AgentRole = blockedStep.AgentRole,
            Label = $"Retry after {upstreamOwner.Name} revision",
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
        if (string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson))
        {
            return (
                "This is a legacy flow without an outcome-verification cycle.",
                "No outcome-verification machine document is required for this legacy flow.");
        }

        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        var context = BuildCriterionAssignment(state, step.AgentRole);
        if (step.AgentRole == "team-lead")
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
        "five findings, and report CLEAR when no evidence-backed failure case remains." +
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
        string agentRole) =>
        "The Pre-mortem Sceptic found evidence that this result could fail within six months. " +
        "Resume your original work and investigate every finding. Accept, reject, or narrow each " +
        "item based on facts. " +
        (agentRole is "software-engineer" or "data-engineer" or "release-engineer"
            ? "If a finding is justified, make the focused corrections owned by this role. "
            : "Do not implement downstream product corrections in this turn; revise this role's " +
              "complete plan, design, or review handoff and assign justified corrections to the " +
              "responsible downstream owner. Stop tool use once that handoff is evidence-based. ") +
        "Return the complete current deliverable or plan, not a delta. " +
        "The next agent must be able to rely on this response alone. Preserve the normal role " +
        "completion contract and end with exactly one disposition marker: " +
        $"{PreMortemRules.AdjustedDisposition} when the complete result materially changed, or " +
        $"{PreMortemRules.UnchangedDisposition} when no material change was justified." +
        $"{Environment.NewLine}{Environment.NewLine}" +
        $"Sceptic output:{Environment.NewLine}" +
        reviewOutput;

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
                    (step.AgentRole != PreMortemRole ||
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

    private async Task MarkWaitingForFeedbackAsync(
        Guid flowId,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var flow = await database.Flows.SingleAsync(item => item.Id == flowId, cancellationToken);
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
            flow.Status = FlowStatus.WaitingForFeedback;
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
            if (flow.Outcome == OutcomeType.PullRequest)
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
            flow.Status = FlowStatus.Approved;
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

        flow.Status = FlowStatus.WaitingForFeedback;
        flow.OutcomeUrl = $"#/preview/{flow.Id}";
        flow.OutcomeLabel = flow.Outcome == OutcomeType.PullRequest
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
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var flow = await database.Flows.SingleOrDefaultAsync(
            item => item.Id == flowId,
            cancellationToken);
        if (flow is null)
        {
            return;
        }

        var pendingSteps = await database.FlowSteps
            .Where(step =>
                step.FlowRunId == flowId &&
                step.Status == StepStatus.Pending)
            .ToListAsync(cancellationToken);
        database.FlowEvents.AddRange(
            ApplyFlowFailure(flow, pendingSteps, failureReason));
        await database.SaveChangesAsync(cancellationToken);
    }

    internal static IReadOnlyList<FlowEvent> ApplyFlowFailure(
        FlowRun flow,
        IReadOnlyList<FlowStep> pendingSteps,
        string failureReason)
    {
        var stoppedAt = DateTimeOffset.UtcNow;
        var events = new List<FlowEvent>(pendingSteps.Count + 1);
        flow.Status = FlowStatus.Failed;
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
        string AgentName,
        string AgentRole,
        string WorkspacePath,
        DateTimeOffset? StartedAt,
        Guid? CopilotSessionId,
        string CopilotSessionHome,
        bool IsPreMortemRevision,
        bool IsOutcomeQa);

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

public sealed class FlowWorker(
    FlowQueue queue,
    WorkflowEngine engine,
    ILogger<FlowWorker> logger)
    : BackgroundService, IFlowExecutionController
{
    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellations = new();
    private readonly ConcurrentDictionary<Guid, byte> _blocked = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var recoveredFlowIds = await engine.RecoverInterruptedFlowsAsync(stoppingToken);
        foreach (var flowId in recoveredFlowIds)
        {
            queue.Queue(flowId);
        }

        await foreach (var flowId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (_blocked.ContainsKey(flowId))
            {
                continue;
            }
            var flowSource =
                CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            if (!_cancellations.TryAdd(flowId, flowSource))
            {
                flowSource.Dispose();
                continue;
            }

            var task = engine.RunAsync(flowId, flowSource.Token);
            if (!_running.TryAdd(flowId, task))
            {
                _cancellations.TryRemove(flowId, out _);
                flowSource.Dispose();
                continue;
            }

            _ = ObserveAsync(flowId, task, flowSource);
        }
    }

    public async Task<bool> CancelAsync(
        Guid flowId,
        CancellationToken cancellationToken = default)
    {
        _blocked.TryAdd(flowId, 0);
        if (!_cancellations.TryGetValue(flowId, out var source))
        {
            return false;
        }

        source.Cancel();
        if (_running.TryGetValue(flowId, out var task))
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (OperationCanceledException) when (source.IsCancellationRequested)
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
        return true;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var source in _cancellations.Values)
        {
            source.Cancel();
        }
        await base.StopAsync(cancellationToken);
        try
        {
            await Task.WhenAll(_running.Values).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Shutdown deadline elapsed while factory flows were stopping.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "One or more in-flight factory flows did not stop cleanly.");
        }
    }

    private async Task ObserveAsync(
        Guid flowId,
        Task task,
        CancellationTokenSource source)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Factory flow {FlowId} was canceled during shutdown", flowId);
        }
        finally
        {
            _running.TryRemove(flowId, out _);
            _cancellations.TryRemove(flowId, out _);
            source.Dispose();
        }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using AiHarnessDemo.Data;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
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
    IPublishedOutcomeVerifier? publicationVerifier = null)
{
    internal const string ReleaseCandidateLabel = "Prepare customer release candidate";
    internal const string ApprovedPublicationLabel = "Publish customer-approved outcome";
    internal const string PreMortemRole = "pre-mortem-sceptic";
    private const string ManualRestartLabelPrefix = "Manual restart of ";
    internal const string ReleaseCandidateAssignment =
        "Prepare the verified outcome for customer review. Commit intended changes locally and " +
        "generate every browser artifact under .customer-preview, but do not push a branch or " +
        "create a pull request before explicit customer approval.";
    internal static string ApprovedPublicationAssignment(OutcomeType outcome) =>
        outcome == OutcomeType.PullRequest
            ? "Customer approval is recorded. Publish the already-verified outcome now: push the " +
              "prepared branch and create or reopen the configured pull request. Do not change " +
              "product behavior unless publication itself requires a narrowly scoped correction."
            : "Customer approval is recorded. Finalize the already-verified local commit outcome " +
              "without pushing a branch or creating a pull request. Report the exact commit SHA.";

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
        var upstreamOwners = BuildUpstreamOwners(plan);
        var maxPreMortemRounds = await GetMaxHandoffRetriesAsync(cancellationToken);
        var preMortemAgent = availableAgents.SingleOrDefault(
            item => item.Enabled && item.Role == PreMortemRole);
        var preMortemAvailable = preMortemAgent is not null && maxPreMortemRounds > 0;

        var lead = plan.FirstOrDefault(item => item.Agent.Role == "team-lead");
        if (lead is not null)
        {
            var leadStepId = await AddStepAsync(
                flow,
                lead,
                sequence: 10,
                label: "Plan the delivery system",
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
                        : "The Pre-mortem Sceptic is unavailable because it is disabled or the configured round limit is zero. Return an empty AfterRoles array."));
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

            var deliveryPlan = plan.Where(item => item.Agent.Role != "team-lead").ToList();
            var latestTeamLeadSequence =
                await GetLatestCompletedAgentSequenceAsync(
                    flow.Id,
                    flow.Iteration,
                    lead.Agent.Id,
                    cancellationToken);
            var sequence = FirstDeliverySequence(latestTeamLeadSequence);
            foreach (var planned in deliveryPlan)
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
                        : $"Execute {planned.Agent.Name} contract",
                    cancellationToken,
                    inputSummary: preparesReleaseCandidate
                        ? ReleaseCandidateAssignment
                        : null);
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
                await AddPreMortemReviewStepAsync(
                    flow,
                    preMortemAgent,
                    deliveryStepId,
                    deliveryStepId,
                    sequence,
                    round: 1,
                    cancellationToken);
                sequence += 10;
            }
        }
        else
        {
            throw new InvalidOperationException(
                "Team Lead must be enabled because downstream task profiles cannot be silently synthesized.");
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

        await MarkWaitingForFeedbackAsync(flowId, cancellationToken);
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
                item.AgentRole == "release-engineer" &&
                item.Label != ApprovedPublicationLabel &&
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
        string? inputSummary = null)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var existing = await database.FlowSteps
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item =>
                    item.FlowRunId == flow.Id &&
                    item.Iteration == flow.Iteration &&
                    item.AgentId == planned.Agent.Id &&
                    item.Attempt == attempt &&
                    item.Label == label,
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
            Status = StepStatus.Pending,
            Attempt = attempt,
            InputSummary = inputSummary ?? planned.Reason
        };
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
        IReadOnlyCollection<string> expectedRoles,
        string workspacePath,
        string planSummary,
        int complexity,
        IReadOnlyDictionary<string, AgentRecord> upstreamOwners,
        bool preMortemAvailable,
        CancellationToken cancellationToken)
    {
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
            if (existingRoleSet.SetEquals(expectedRoles))
            {
                return SelectEnabledPreMortemCheckpoints(
                    existingProfiles,
                    preMortemAvailable);
            }
        }

        TeamLeadContract contract;
        try
        {
            contract = ParseTeamLeadContract(
                leadResult.OutputSummary,
                expectedRoles,
                flow.Id,
                flow.Iteration,
                preMortemAvailable);
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
                    "return both corrected sentinel-delimited JSON documents. Exact validation errors:" +
                    Environment.NewLine +
                    validationErrors);
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
                    preMortemAvailable);
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
                    "Team Lead returned an invalid corrected profile or pre-mortem contract.",
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
        database.TaskProfiles.AddRange(contract.Profiles);
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = leadResult.Id,
            Type = "profile.validated",
            Message =
                $"Validated router-v1 task profiles for {contract.Profiles.Count} downstream roles " +
                $"and {contract.PreMortemAfterRoles.Count} pre-mortem checkpoint(s)."
        });
        await database.SaveChangesAsync(cancellationToken);
        return contract.PreMortemAfterRoles;
    }

    private static TeamLeadContract ParseTeamLeadContract(
        string output,
        IReadOnlyCollection<string> expectedRoles,
        Guid flowId,
        int iteration,
        bool preMortemAvailable)
    {
        IReadOnlyList<TaskProfile>? profiles = null;
        IReadOnlySet<string>? checkpoints = null;
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
        return new TeamLeadContract(validatedProfiles, validatedCheckpoints);
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
        var retryRootId = source.RetryOfStepId ?? source.Id;
        var candidates = await database.FlowSteps
            .AsNoTracking()
            .Where(item =>
                item.FlowRunId == source.FlowRunId &&
                item.Iteration == source.Iteration &&
                item.AgentId == source.AgentId &&
                (item.Id == source.Id ||
                 item.RetryOfStepId == retryRootId))
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
                    AllowRemotePublication: step.RemotePublicationAllowed,
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
                    InvocationStartedAt: step.StartedAt);
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
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var completion = await StageCompletedStepAsync(
            database,
            flowId,
            stepId,
            result,
            completedAt,
            durationMilliseconds,
            recoveredSessionId,
            cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        await RecordCompletionObservationAsync(completion, cancellationToken);
        ThrowIfCompletionBlocked(completion.GateRecord);
        return completion.Step;
    }

    private async Task SupersedePendingReleaseGatesAsync(
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
        foreach (var gate in pending)
        {
            var resolved = handoffGate.SupersedeProposal(
                gate.Id,
                "harness",
                "Superseded by a revised release candidate.");
            gate.Resolved = true;
            gate.Approved = false;
            gate.ResolvedBy = resolved.ResolvedBy;
            gate.ResolutionNote = resolved.ResolutionNote;
            gate.ResolvedAt = resolved.ResolvedAt ?? resolvedAt;
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flowId,
                FlowStepId = gate.FlowStepId,
                Type = "gate.release-superseded",
                Message = "An earlier release gate was superseded by a revised candidate."
            });
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
        CancellationToken cancellationToken)
    {
        var step = await database.FlowSteps.SingleAsync(
            item => item.Id == stepId,
            cancellationToken);
        if (step.RemotePublicationAllowed)
        {
            var flow = await database.Flows.SingleAsync(
                item => item.Id == flowId,
                cancellationToken);
            var published = await (publicationVerifier
                ?? throw new InvalidOperationException(
                    "No published outcome verifier is configured."))
                .VerifyAsync(flow, result.Output, cancellationToken);
            flow.OutcomeUrl = published.Url;
            flow.OutcomeLabel = published.Label;
        }
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
        var publishesApprovedOutcome =
            step.AgentRole == "release-engineer" &&
            step.RemotePublicationAllowed;
        if (step.AgentRole == "release-engineer" &&
            !publishesApprovedOutcome)
        {
            await SupersedePendingReleaseGatesAsync(
                database,
                flowId,
                step.Id,
                step.Iteration,
                completedAt,
                cancellationToken);
        }
        var actionType = pushedBack
            ? HandoffActionType.RequestRevision
            : step.AgentRole == "release-engineer" && !publishesApprovedOutcome
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
                Succeeded = toolCall.Succeeded
            });
        }

        var elapsedMilliseconds = durationMilliseconds ?? Math.Max(
            1,
            (long)(completedAt - (step.StartedAt ?? completedAt)).TotalMilliseconds);
        step.Status = pushedBack ? StepStatus.Pushback : StepStatus.Completed;
        step.Phase = pushedBack ? AgentRunPhase.Failed : AgentRunPhase.Succeeded;
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
            elapsedMilliseconds,
            step.ExecutionAttempts);
    }

    private async Task RecordCompletionObservationAsync(
        StagedStepCompletion completion,
        CancellationToken cancellationToken)
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
                        step.PreMortemReviewStepId != null))
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
                              retry.RetryOfStepId ==
                              (step.RetryOfStepId ?? step.Id) &&
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
                        step.PreMortemReviewStepId != null))
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

        logger.LogInformation(
            "Reconciled {StepCount} interrupted step(s) across {FlowCount} flow(s).",
            interruptedSteps.Count + failedStalledSteps.Count,
            interruptedFlows.Count);
        return interruptedFlows.Select(flow => flow.Id).ToList();
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
        if (snapshot is null || snapshot.State == CopilotSessionJournalState.Missing)
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
            !CopilotReasoningHost.IsRecoverableCompletedOutput(
                candidate.AgentRole,
                recoveredResult.OutputSummary,
                candidate.IsPreMortemRevision) ||
            !CopilotReasoningHost.IsRecoveryCurrent(
                candidate.StartedAt,
                snapshot.CompletedAt))
        {
            return false;
        }

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
                ToRecoveredExecutionResult(recoveredResult, snapshot.SessionId),
                snapshot.CompletedAt ?? DateTimeOffset.UtcNow,
                durationMilliseconds: null,
                snapshot.SessionId,
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
            if ((snapshot is null || snapshot.State == CopilotSessionJournalState.Missing) &&
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
                CopilotReasoningHost.IsRecoverableCompletedOutput(
                    failedStep.AgentRole,
                    recoveredResult.OutputSummary,
                    failedStep.PreMortemReviewStepId is not null) &&
                CopilotReasoningHost.IsRecoveryCurrent(
                    failedStep.StartedAt,
                    snapshot.CompletedAt))
            {
                await using var transaction =
                    await database.Database.BeginTransactionAsync(cancellationToken);
                var completion = await StageCompletedStepAsync(
                    database,
                    flow.Id,
                    failedStep.Id,
                    ToRecoveredExecutionResult(recoveredResult, snapshot.SessionId),
                    snapshot.CompletedAt ?? DateTimeOffset.UtcNow,
                    durationMilliseconds: null,
                    snapshot.SessionId,
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
                    RetryOfStepId = failedStep.RetryOfStepId ?? failedStep.Id,
                    DependsOnStepId = failedStep.DependsOnStepId,
                    PushbackRootStepId = failedStep.PushbackRootStepId,
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
        if (snapshot is null || snapshot.State == CopilotSessionJournalState.Missing)
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
            CopilotReasoningHost.IsRecoverableCompletedOutput(
                candidate.AgentRole,
                recoveredResult.OutputSummary,
                candidate.IsPreMortemRevision) &&
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
            RemotePublicationAllowed = blockedStep.RemotePublicationAllowed,
            Status = StepStatus.Pending,
            Attempt = blockedStep.Attempt + 1,
            InputSummary =
                $"{upstreamOwner.Name} is revising the rejected handoff. " +
                "Resume this role after the corrected handoff is attached to the retry.",
            RetryOfStepId =
                blockedStep.RetryOfStepId ?? blockedStep.Id,
            DependsOnStepId = revisionStep.Id,
            PushbackRootStepId =
                blockedStep.PushbackRootStepId ?? blockedStep.Id,
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
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
    }

    internal static IReadOnlyDictionary<string, AgentRecord> BuildUpstreamOwners(
        IReadOnlyList<PlannedAgent> plan)
    {
        var owners = new Dictionary<string, AgentRecord>(StringComparer.Ordinal);
        for (var index = 1; index < plan.Count; index++)
        {
            owners[plan[index].Agent.Id] = plan[index - 1].Agent;
        }
        return owners;
    }

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
                    retry.RetryOfStepId ==
                    (step.RetryOfStepId ?? step.Id) &&
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
        var retryRootStepId =
            failedStep.RetryOfStepId ?? failedStep.Id;
        return steps
            .Where(step =>
                step.FlowRunId == failedStep.FlowRunId &&
                step.Iteration == failedStep.Iteration &&
                step.AgentId == failedStep.AgentId &&
                step.RetryOfStepId == retryRootStepId &&
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
        candidate.RetryOfStepId ==
        (recoveredStep.RetryOfStepId ?? recoveredStep.Id);

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
                 item.RetryOfStepId == (source.RetryOfStepId ?? source.Id)))
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
                if (string.IsNullOrWhiteSpace(flow.OutcomeUrl) ||
                    !flow.OutcomeLabel.StartsWith(
                        "Published pull request #",
                        StringComparison.Ordinal))
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
        bool IsPreMortemRevision);

    private sealed record StagedStepCompletion(
        FlowStep Step,
        HandoffGateRecord GateRecord,
        bool PushedBack,
        long ElapsedMilliseconds,
        int ExecutionAttempts);

    private sealed record TeamLeadContract(
        IReadOnlyList<TaskProfile> Profiles,
        IReadOnlySet<string> PreMortemAfterRoles);
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

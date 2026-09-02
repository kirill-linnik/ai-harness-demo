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
    ModelSelector modelSelector,
    IWorkspaceManager workspaceManager,
    IAgentRunner agentRunner,
    HandoffGateEngine handoffGate,
    WorkflowDefinitionProvider workflowProvider,
    ILogger<WorkflowEngine> logger)
{
    private readonly Lock _concurrencyLock = new();
    private readonly SemaphoreSlim _learningGate = new(1, 1);
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

            if (flow.Status is FlowStatus.Approved or FlowStatus.WaitingForFeedback)
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

        var lead = plan.FirstOrDefault(item => item.Agent.Role == "team-lead");
        if (lead is not null)
        {
            var leadStepId = await AddStepAsync(
                flow,
                lead,
                sequence: 10,
                label: "Plan the delivery system",
                cancellationToken);
            if (await ShouldExecuteStepAsync(leadStepId, cancellationToken))
            {
                await ExecuteWithPushbackRecoveryAsync(
                    flow,
                    flowId,
                    leadStepId,
                    workspace.Path,
                    planSummary,
                    complexity,
                    upstreamOwners,
                    cancellationToken);
            }
        }
        else
        {
            await AddEventAsync(
                flow.Id,
                null,
                "plan.fallback",
                "Team Lead is disabled; the harness applied its deterministic default sequence.",
                cancellationToken);
        }

        var deliveryPlan = plan.Where(item => item.Agent.Role != "team-lead").ToList();
        var sequence = 20;
        foreach (var planned in deliveryPlan)
        {
            await AddStepAsync(
                flow,
                planned,
                sequence,
                $"Execute {planned.Agent.Name} contract",
                cancellationToken);
            sequence += 10;
        }

        await AddEventAsync(
            flowId,
            null,
            "plan.selected",
            $"Team sequence selected: {planSummary}.",
            cancellationToken);
        await RecoverUnresolvedPushbacksAsync(
            flow,
            workspace.Path,
            planSummary,
            complexity,
            upstreamOwners,
            cancellationToken);

        var stepIds = await GetPendingStepIdsAsync(
            flowId,
            flow.Iteration,
            cancellationToken);
        foreach (var stepId in stepIds)
        {
            if (!await ShouldExecuteStepAsync(stepId, cancellationToken))
            {
                continue;
            }

            await HydrateRetryAssignmentAsync(stepId, cancellationToken);
            await ExecuteWithPushbackRecoveryAsync(
                flow,
                flowId,
                stepId,
                workspace.Path,
                planSummary,
                complexity,
                upstreamOwners,
                cancellationToken);
        }

        await MarkWaitingForFeedbackAsync(flowId, cancellationToken);
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

    private async Task<List<Guid>> GetPendingStepIdsAsync(
        Guid flowId,
        int iteration,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        return await database.FlowSteps
            .AsNoTracking()
            .Where(item =>
                item.FlowRunId == flowId &&
                item.Iteration == iteration &&
                item.Status == StepStatus.Pending)
            .OrderBy(item => item.Sequence)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
    }

    private async Task<FlowStep> ExecuteStepAsync(
        Guid flowId,
        Guid stepId,
        string workspacePath,
        string planSummary,
        int complexity,
        CancellationToken cancellationToken)
    {
        AgentExecutionContext executionContext;
        var stopwatch = Stopwatch.StartNew();

        await using (var database = await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            var flow = await database.Flows.SingleAsync(
                item => item.Id == flowId,
                cancellationToken);
            var step = await database.FlowSteps.SingleAsync(
                item => item.Id == stepId,
                cancellationToken);
            var history = await database.FlowSteps
                .AsNoTracking()
                .Where(item =>
                    item.AgentRole == step.AgentRole &&
                    item.Id != step.Id &&
                    item.Status != StepStatus.Pending &&
                    item.Status != StepStatus.Running)
                .Select(item => item.Status)
                .ToListAsync(cancellationToken);
            var failureRate = history.Count == 0
                ? 0
                : history.Count(status => status is StepStatus.Failed or StepStatus.Pushback) /
                  (double)history.Count;
            var model = modelSelector.Select(step.AgentRole, complexity, failureRate);
            var copilotSessionId = AgentSessionIdentity.Create(
                flow.Id,
                flow.Iteration,
                step.AgentId);
            var resumesSession = await database.FlowSteps
                .AsNoTracking()
                .AnyAsync(
                    item =>
                        item.FlowRunId == flow.Id &&
                        item.Iteration == flow.Iteration &&
                        item.AgentId == step.AgentId &&
                        item.Id != step.Id &&
                        item.StartedAt != null,
                    cancellationToken);

            step.Model = model.Model;
            step.ModelReason = model.Reason;
            step.Status = StepStatus.Running;
            step.Phase = AgentRunPhase.BuildingPrompt;
            step.StartedAt = DateTimeOffset.UtcNow;
            flow.UpdatedAt = DateTimeOffset.UtcNow;
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flowId,
                FlowStepId = stepId,
                Type = "step.started",
                Message = $"{step.AgentName} started with {model.Model}."
            });
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flowId,
                FlowStepId = stepId,
                Type = resumesSession
                    ? "agent.session-resumed"
                    : "agent.session-created",
                Message =
                    $"{step.AgentName} {(resumesSession ? "resumed" : "created")} " +
                    $"Copilot session {copilotSessionId:D}."
            });

            var previousOutputs = await database.FlowSteps
                .AsNoTracking()
                .Where(item =>
                    item.FlowRunId == flowId &&
                    item.Status == StepStatus.Completed &&
                    item.OutputSummary != string.Empty)
                .OrderBy(item => item.Iteration)
                .ThenBy(item => item.Sequence)
                .Select(item => item.OutputSummary)
                .ToListAsync(cancellationToken);
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
                step.Attempt,
                BuildStepTask(flow.ConsolidatedRequest, step.InputSummary),
                flow.RepositoryKnowledge,
                flow.RepositoryPath,
                workspacePath,
                copilotSessionId,
                flow.Outcome,
                planSummary,
                previousOutputs,
                learnings,
                Progress: progress =>
                    RecordProgressAsync(
                            flow.Id,
                            step.Id,
                            progress,
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult());
        }

        try
        {
            var result = await agentRunner.ExecuteAsync(executionContext, cancellationToken);
            stopwatch.Stop();
            await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
            var step = await database.FlowSteps.SingleAsync(
                item => item.Id == stepId,
                cancellationToken);
            var pushbackReason = AgentHandoffInspector.GetPushbackReason(result.Output);
            var pushedBack = pushbackReason is not null;
            var actionType = pushedBack
                ? HandoffActionType.RequestRevision
                : step.AgentRole == "release-engineer"
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
            step.Status = pushedBack ? StepStatus.Pushback : StepStatus.Completed;
            step.Phase = pushedBack ? AgentRunPhase.Failed : AgentRunPhase.Succeeded;
            step.OutputSummary = result.Output;
            step.PushbackReason = pushbackReason ?? string.Empty;
            step.ExecutionAttempts = result.ExecutionAttempts;
            step.CompletedAt = DateTimeOffset.UtcNow;
            step.DurationMilliseconds = stopwatch.ElapsedMilliseconds;
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flowId,
                FlowStepId = stepId,
                Type = pushedBack ? "handoff.pushback" : "step.completed",
                Message = pushedBack
                    ? $"{step.AgentName} requested an upstream revision: {pushbackReason}"
                    : $"{step.AgentName} completed in {FormatDuration(stopwatch.Elapsed)}."
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
            await database.SaveChangesAsync(cancellationToken);

            if (gateRecord.Decision is
                HandoffGateDecision.BlockedKillSwitch or
                HandoffGateDecision.LoggedShadow)
            {
                throw new InvalidOperationException(gateRecord.Reason);
            }

            return step;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
            var step = await database.FlowSteps.SingleAsync(
                item => item.Id == stepId,
                cancellationToken);
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
            step.CompletedAt = DateTimeOffset.UtcNow;
            step.DurationMilliseconds = stopwatch.ElapsedMilliseconds;
            await database.SaveChangesAsync(cancellationToken);
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

        int pushbackCount;
        int maxHandoffRetries;
        await using (var database = await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            pushbackCount = await database.FlowSteps.CountAsync(
                item =>
                    item.FlowRunId == flow.Id &&
                    item.Iteration == flow.Iteration &&
                    item.AgentId == step.AgentId &&
                    item.Status == StepStatus.Pushback,
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
            upstreamOwner.Name,
            revision.OutputSummary,
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
                            retry.Sequence > step.Sequence))
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
            Status = StepStatus.Pending,
            Attempt = blockedStep.Attempt + 1,
            InputSummary =
                $"{upstreamOwner.Name} is revising the rejected handoff. " +
                "Resume this role after the corrected handoff appears in Prior handoffs."
        };
        return (revisionStep, retryStep);
    }

    private async Task SetRetryAssignmentAsync(
        Guid retryStepId,
        string upstreamOwnerName,
        string revisionOutput,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var retryStep = await database.FlowSteps.SingleAsync(
            item => item.Id == retryStepId,
            cancellationToken);
        retryStep.InputSummary =
            $"{upstreamOwnerName} responded to your pushback. Resume your role and re-attempt " +
            $"the blocked work using this corrected handoff:{Environment.NewLine}{Environment.NewLine}" +
            ClipText(revisionOutput, 3_000);
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
            !retryStep.InputSummary.Contains(
                "is revising the rejected handoff",
                StringComparison.Ordinal))
        {
            return;
        }

        var revisionStep = await database.FlowSteps
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
            $"## Assignment for this turn{Environment.NewLine}{inputSummary}";
    }

    internal static bool HasHandoffRetryAvailable(
        int observedPushbacks,
        int maxHandoffRetries) =>
        observedPushbacks <= maxHandoffRetries;

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
        var hasReleaseGate = await database.GateRecords.AnyAsync(
            item =>
                item.FlowRunId == flowId &&
                item.ActionType == HandoffActionType.Release &&
                !item.Resolved,
            cancellationToken);
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

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalMinutes >= 1
            ? $"{duration.TotalMinutes:0.0} min"
            : $"{Math.Max(1, duration.TotalSeconds):0.0} sec";
}

public sealed class FlowWorker(
    FlowQueue queue,
    WorkflowEngine engine,
    IDbContextFactory<HarnessDbContext> databaseFactory,
    ILogger<FlowWorker> logger)
    : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverInterruptedFlowsAsync(stoppingToken);

        await foreach (var flowId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (_running.ContainsKey(flowId))
            {
                continue;
            }

            var task = engine.RunAsync(flowId, stoppingToken);
            if (!_running.TryAdd(flowId, task))
            {
                continue;
            }

            _ = ObserveAsync(flowId, task);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
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

    private async Task RecoverInterruptedFlowsAsync(CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var interrupted = await database.Flows
            .Where(flow =>
                flow.Status == FlowStatus.Queued ||
                flow.Status == FlowStatus.Running ||
                flow.Status == FlowStatus.Reworking)
            .ToListAsync(cancellationToken);
        var interruptedIds = interrupted.Select(flow => flow.Id).ToList();
        var interruptedSteps = interruptedIds.Count == 0
            ? []
            : await database.FlowSteps
                .Where(step =>
                    interruptedIds.Contains(step.FlowRunId) &&
                    step.Status == StepStatus.Running)
                .ToListAsync(cancellationToken);
        foreach (var step in interruptedSteps)
        {
            step.Status = StepStatus.Pending;
            step.Phase = AgentRunPhase.CanceledByReconciliation;
            step.StartedAt = null;
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = step.FlowRunId,
                FlowStepId = step.Id,
                Type = "step.reconciled",
                Message = $"{step.AgentName} was interrupted by restart and queued for workspace-aware continuation."
            });
        }

        foreach (var flow in interrupted)
        {
            flow.Status = FlowStatus.Queued;
            flow.UpdatedAt = DateTimeOffset.UtcNow;
            queue.Queue(flow.Id);
        }

        if (interrupted.Count > 0)
        {
            await database.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Recovered {Count} interrupted factory flows", interrupted.Count);
        }
    }

    private async Task ObserveAsync(Guid flowId, Task task)
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
        }
    }
}

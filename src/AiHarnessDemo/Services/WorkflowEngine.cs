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
    AgentRunner agentRunner,
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
                await ExecuteStepAsync(
                    flowId,
                    leadStepId,
                    workspace.Path,
                    planSummary,
                    complexity,
                    cancellationToken);
            }
        }
        else
        {
            await AddEventAsync(
                flowId,
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

        var pushbackHandled =
            await HasPushbackAsync(flowId, flow.Iteration, cancellationToken) ||
            await HasEngineeringHandoffLearningAsync(cancellationToken);
        var stepIds = await GetPendingStepIdsAsync(
            flowId,
            flow.Iteration,
            cancellationToken);
        foreach (var stepId in stepIds)
        {
            var completed = await ExecuteStepAsync(
                flowId,
                stepId,
                workspace.Path,
                planSummary,
                complexity,
                cancellationToken);

            if (completed.AgentRole == "quality-engineer" && !pushbackHandled)
            {
                var engineer = await GetLatestEngineerAsync(
                    flowId,
                    flow.Iteration,
                    cancellationToken);
                if (engineer is not null &&
                    ShouldPushBack(completed.OutputSummary, engineer.OutputSummary))
                {
                    await RecordPushbackAsync(
                        flow,
                        completed,
                        engineer,
                        cancellationToken);
                    await MakeSpaceAfterStepAsync(
                        flowId,
                        flow.Iteration,
                        completed.Sequence,
                        cancellationToken);

                    var engineerAgent = availableAgents.Single(item => item.Id == engineer.AgentId);
                    var revisionId = await AddStepAsync(
                        flow,
                        new PlannedAgent(
                            engineerAgent,
                            "QA requested acceptance-to-evidence traceability."),
                        completed.Sequence + 10,
                        "Revision after QA pushback",
                        cancellationToken,
                        attempt: engineer.Attempt + 1);

                    var revision = await ExecuteStepAsync(
                        flowId,
                        revisionId,
                        workspace.Path,
                        planSummary,
                        complexity,
                        cancellationToken);

                    var qaAgent = availableAgents.Single(item => item.Id == completed.AgentId);
                    var retryId = await AddStepAsync(
                        flow,
                        new PlannedAgent(
                            qaAgent,
                            "Re-run the quality gate against the corrected handoff."),
                        completed.Sequence + 20,
                        "Re-validate corrected handoff",
                        cancellationToken,
                        attempt: completed.Attempt + 1,
                        inputSummary: revision.OutputSummary);

                    await ExecuteStepAsync(
                        flowId,
                        retryId,
                        workspace.Path,
                        planSummary,
                        complexity,
                        cancellationToken);
                    pushbackHandled = true;
                }
            }
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
                .OrderBy(item => item.CreatedAt)
                .Take(12)
                .ToListAsync(cancellationToken);
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
                flow.ConsolidatedRequest,
                flow.RepositoryKnowledge,
                workspacePath,
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
            var actionType = step.AgentRole == "release-engineer"
                ? HandoffActionType.Release
                : HandoffActionType.Advance;
            var gateRecord = handoffGate.SubmitProposal(new HandoffProposal
            {
                FlowRunId = flowId,
                FlowStepId = stepId,
                ActionType = actionType,
                Summary = result.Output,
                Evidence = result.Evidence,
                BlastRadius = actionType == HandoffActionType.Release
                    ? HandoffBlastRadius.High
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
            step.Status = StepStatus.Completed;
            step.Phase = AgentRunPhase.Succeeded;
            step.OutputSummary = result.Output;
            step.ExecutionAttempts = result.ExecutionAttempts;
            step.CompletedAt = DateTimeOffset.UtcNow;
            step.DurationMilliseconds = stopwatch.ElapsedMilliseconds;
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flowId,
                FlowStepId = stepId,
                Type = "step.completed",
                Message = $"{step.AgentName} completed in {FormatDuration(stopwatch.Elapsed)}."
            });
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flowId,
                FlowStepId = stepId,
                Type = "gate.decision",
                Message =
                    $"Handoff gate: {gateRecord.Decision} at {gateRecord.TrustLevelAtDecision} trust."
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

    private async Task RecordPushbackAsync(
        FlowRun flow,
        FlowStep qualityStep,
        FlowStep engineerStep,
        CancellationToken cancellationToken)
    {
        const string reason =
            "QA could not trace every acceptance statement to a concrete validation result.";
        await _learningGate.WaitAsync(cancellationToken);
        try
        {
            await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
            var storedQualityStep = await database.FlowSteps.SingleAsync(
                item => item.Id == qualityStep.Id,
                cancellationToken);
            storedQualityStep.Status = StepStatus.Pushback;
            storedQualityStep.PushbackReason = reason;
            var gateRecord = handoffGate.SubmitProposal(new HandoffProposal
            {
                FlowRunId = flow.Id,
                FlowStepId = qualityStep.Id,
                ActionType = HandoffActionType.RequestRevision,
                Summary = reason,
                Evidence = qualityStep.OutputSummary,
                BlastRadius = HandoffBlastRadius.Low
            });
            database.GateRecords.Add(gateRecord);

            var learning = await database.Learnings.SingleOrDefaultAsync(
                item =>
                    item.Category == "Handoff quality" &&
                    item.AgentId == engineerStep.AgentId,
                cancellationToken);
            if (learning is null)
            {
                database.Learnings.Add(new HarnessLearning
                {
                    SourceFlowId = flow.Id,
                    AgentId = engineerStep.AgentId,
                    Category = "Handoff quality",
                    Trigger = "Quality Engineer pushed an implementation handoff back.",
                    Lesson = "A completion claim is not enough; downstream agents need traceable evidence.",
                    PromptRefinement =
                        "End the engineering handoff with changed surfaces, acceptance-to-test mapping, " +
                        "exact validation commands, and observed results."
                });
            }
            else
            {
                learning.TimesObserved++;
            }

            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = qualityStep.Id,
                Type = "handoff.pushback",
                Message = $"Quality Engineer pushed work back to Software Engineer: {reason}"
            });
            await database.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _learningGate.Release();
        }
    }

    private async Task MakeSpaceAfterStepAsync(
        Guid flowId,
        int iteration,
        int sequence,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var laterSteps = await database.FlowSteps
            .Where(item =>
                item.FlowRunId == flowId &&
                item.Iteration == iteration &&
                item.Sequence > sequence)
            .ToListAsync(cancellationToken);
        foreach (var laterStep in laterSteps)
        {
            laterStep.Sequence += 20;
        }
        await database.SaveChangesAsync(cancellationToken);
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

    private async Task<FlowStep?> GetLatestEngineerAsync(
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
                item.AgentRole == "software-engineer" &&
                item.Status == StepStatus.Completed)
            .OrderByDescending(item => item.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<bool> HasPushbackAsync(
        Guid flowId,
        int iteration,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        return await database.FlowSteps.AnyAsync(
            item =>
                item.FlowRunId == flowId &&
                item.Iteration == iteration &&
                item.Status == StepStatus.Pushback,
            cancellationToken);
    }

    private async Task<bool> HasEngineeringHandoffLearningAsync(
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        return await database.Learnings.AnyAsync(
            item => item.Category == "Handoff quality",
            cancellationToken);
    }

    private static bool ShouldPushBack(string qualityOutput, string engineerOutput) =>
        qualityOutput.Contains("PUSHBACK", StringComparison.OrdinalIgnoreCase) ||
        (!engineerOutput.Contains("Evidence", StringComparison.OrdinalIgnoreCase) &&
         !engineerOutput.Contains("Validation", StringComparison.OrdinalIgnoreCase));

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

        flow.Status = FlowStatus.Failed;
        flow.FailureReason = failureReason;
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flowId,
            Type = "flow.failed",
            Message = failureReason
        });
        await database.SaveChangesAsync(cancellationToken);
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

using AiHarnessDemo.Contracts;
using AiHarnessDemo.Data;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Reasoning;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

public sealed class FeedbackCoordinator(
    IDbContextFactory<HarnessDbContext> databaseFactory,
    ModelSelector modelSelector,
    AgentRunner agentRunner,
    HandoffGateEngine gateEngine,
    FlowQueue flowQueue)
{
    public async Task<FeedbackResponse> RespondAsync(
        Guid flowId,
        string feedback,
        CancellationToken cancellationToken = default)
    {
        var message = feedback.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Customer feedback cannot be empty.");
        }

        FlowRun flow;
        AgentRecord? productManager;
        FlowStep? step = null;
        await using (var database = await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            flow = await database.Flows
                       .Include(item => item.Steps)
                       .ThenInclude(step => step.ToolCalls)
                       .Include(item => item.Messages)
                       .Include(item => item.Events)
                       .Include(item => item.GateRecords)
                       .SingleOrDefaultAsync(item => item.Id == flowId, cancellationToken)
                   ?? throw new KeyNotFoundException($"Factory flow '{flowId}' was not found.");
            if (flow.Status != FlowStatus.WaitingForFeedback)
            {
                throw new InvalidOperationException(
                    "Customer feedback is accepted only when a preview is ready.");
            }

            var customerMessage = new FlowMessage
            {
                FlowRunId = flow.Id,
                Role = ConversationRole.Customer,
                Content = message
            };
            flow.Messages.Add(customerMessage);
            database.Entry(customerMessage).State = EntityState.Added;
            productManager = await database.Agents
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.Role == "product-manager" && item.Enabled,
                    cancellationToken);

            if (productManager is not null)
            {
                var model = modelSelector.Select("product-manager", complexity: 2);
                step = new FlowStep
                {
                    FlowRunId = flow.Id,
                    Iteration = flow.Iteration,
                    Sequence = flow.Steps
                        .Where(item => item.Iteration == flow.Iteration)
                        .Select(item => item.Sequence)
                        .DefaultIfEmpty()
                        .Max() + 10,
                    AgentId = productManager.Id,
                    AgentName = productManager.Name,
                    AgentRole = productManager.Role,
                    Label = "Interpret customer feedback",
                    Model = model.Model,
                    ModelReason = model.Reason,
                    Status = StepStatus.Running,
                    StartedAt = DateTimeOffset.UtcNow,
                    InputSummary = message
                };
                database.FlowSteps.Add(step);
            }

            flow.UpdatedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync(cancellationToken);
        }

        string reply;
        if (step is null || productManager is null)
        {
            reply =
                "Feedback is recorded in the execution ledger. Enable Product Manager for a contextual spoken response; " +
                "you can approve this result or start a revised iteration.";
        }
        else
        {
            var previousOutputs = flow.Steps
                .Where(item => item.Status is StepStatus.Completed or StepStatus.Pushback)
                .OrderBy(item => item.Sequence)
                .Select(item => item.OutputSummary)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToList();
            AgentExecutionResult result;
            try
            {
                result = await agentRunner.ExecuteAsync(
                    new AgentExecutionContext(
                        flow.Id,
                        flow.Iteration,
                        productManager.Id,
                        productManager.Name,
                        productManager.Role,
                        step.Model,
                        step.Attempt,
                        flow.ConsolidatedRequest,
                        flow.RepositoryKnowledge,
                        string.IsNullOrWhiteSpace(flow.WorkspacePath)
                            ? flow.RepositoryPath
                            : flow.WorkspacePath,
                        flow.Outcome,
                        "Review the execution ledger and help the customer decide.",
                        previousOutputs,
                        [],
                        message),
                    cancellationToken);
            }
            catch (Exception exception)
            {
                await PersistFeedbackFailureAsync(
                    flow.Id,
                    step.Id,
                    exception,
                    CancellationToken.None);
                throw;
            }
            reply = result.Output;

            await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
            var storedStep = await database.FlowSteps.SingleAsync(
                item => item.Id == step.Id,
                cancellationToken);
            var gateRecord = gateEngine.SubmitProposal(new HandoffProposal
            {
                FlowRunId = flow.Id,
                FlowStepId = storedStep.Id,
                ActionType = HandoffActionType.Advance,
                Summary = result.Output,
                Evidence = result.Evidence,
                BlastRadius = HandoffBlastRadius.Low
            });
            database.GateRecords.Add(gateRecord);
            foreach (var toolCall in result.ToolCalls)
            {
                database.AgentToolCalls.Add(new AgentToolCall
                {
                    FlowStepId = storedStep.Id,
                    ToolName = toolCall.ToolName,
                    ArgumentsSummary = toolCall.ArgumentsSummary,
                    Succeeded = toolCall.Succeeded
                });
            }
            storedStep.Status = StepStatus.Completed;
            storedStep.OutputSummary = reply;
            storedStep.ExecutionAttempts = result.ExecutionAttempts;
            storedStep.CompletedAt = DateTimeOffset.UtcNow;
            storedStep.DurationMilliseconds = Math.Max(
                1,
                (long)(storedStep.CompletedAt.Value - storedStep.StartedAt!.Value).TotalMilliseconds);
            await database.SaveChangesAsync(cancellationToken);
        }

        await using (var database = await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            var storedFlow = await database.Flows
                .Include(item => item.Steps)
                .ThenInclude(step => step.ToolCalls)
                .Include(item => item.Messages)
                .Include(item => item.Events)
                .Include(item => item.GateRecords)
                .SingleAsync(item => item.Id == flowId, cancellationToken);
            var responseMessage = new FlowMessage
            {
                FlowRunId = storedFlow.Id,
                Role = productManager is null
                    ? ConversationRole.Harness
                    : ConversationRole.ProductManager,
                Content = reply
            };
            storedFlow.Messages.Add(responseMessage);
            database.Entry(responseMessage).State = EntityState.Added;
            var feedbackEvent = new FlowEvent
            {
                FlowRunId = storedFlow.Id,
                Type = "feedback.reviewed",
                Message = "Customer feedback was grounded in the full execution ledger."
            };
            storedFlow.Events.Add(feedbackEvent);
            database.Entry(feedbackEvent).State = EntityState.Added;
            storedFlow.UpdatedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync(cancellationToken);
            return new FeedbackResponse(storedFlow.ToDetailDto(), reply, ShouldSpeak: true);
        }
    }

    public async Task<FlowDetailDto> DecideAsync(
        Guid flowId,
        bool approve,
        CancellationToken cancellationToken = default)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var flow = await database.Flows
                       .Include(item => item.Steps)
                       .ThenInclude(step => step.ToolCalls)
                       .Include(item => item.Messages)
                       .Include(item => item.Events)
                       .Include(item => item.GateRecords)
                       .SingleOrDefaultAsync(item => item.Id == flowId, cancellationToken)
                   ?? throw new KeyNotFoundException($"Factory flow '{flowId}' was not found.");

        if (flow.Status != FlowStatus.WaitingForFeedback)
        {
            throw new InvalidOperationException("This flow is not waiting for a customer decision.");
        }

        var pendingReleaseGate = flow.GateRecords
            .Where(item =>
                item.ActionType == HandoffActionType.Release &&
                !item.Resolved)
            .OrderByDescending(item => item.DecidedAt)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The flow has no unresolved release gate for the customer to decide.");
        var resolvedGate = gateEngine.ResolveProposal(
            pendingReleaseGate.Id,
            approve,
            "customer",
            approve ? "Customer accepted the outcome." : "Customer requested another iteration.");
        pendingReleaseGate.Resolved = resolvedGate.Resolved;
        pendingReleaseGate.Approved = resolvedGate.Approved;
        pendingReleaseGate.ResolvedBy = resolvedGate.ResolvedBy;
        pendingReleaseGate.ResolutionNote = resolvedGate.ResolutionNote;
        pendingReleaseGate.ResolvedAt = resolvedGate.ResolvedAt;

        if (approve)
        {
            flow.Status = FlowStatus.Approved;
            flow.CompletedAt = DateTimeOffset.UtcNow;
            var approvedEvent = new FlowEvent
            {
                FlowRunId = flow.Id,
                Type = "flow.approved",
                Message = "Customer approved the delivered outcome. Factory flow closed."
            };
            flow.Events.Add(approvedEvent);
            database.Entry(approvedEvent).State = EntityState.Added;
        }
        else
        {
            var latestFeedback = flow.Messages
                .Where(item => item.Role == ConversationRole.Customer)
                .OrderByDescending(item => item.CreatedAt)
                .FirstOrDefault()?.Content
                ?? "Customer requested another iteration.";
            flow.ConsolidatedRequest +=
                $"{Environment.NewLine}Customer feedback after iteration {flow.Iteration}: {latestFeedback}";
            flow.Iteration++;
            flow.Status = FlowStatus.Queued;
            flow.OutcomeUrl = string.Empty;
            flow.OutcomeLabel = string.Empty;
            var reworkEvent = new FlowEvent
            {
                FlowRunId = flow.Id,
                Type = "flow.rework-queued",
                Message = $"Customer requested iteration {flow.Iteration}; all prior context is retained."
            };
            flow.Events.Add(reworkEvent);
            database.Entry(reworkEvent).State = EntityState.Added;
        }

        flow.UpdatedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);

        if (!approve && !flowQueue.Queue(flow.Id))
        {
            throw new InvalidOperationException("Unable to queue the revised factory flow.");
        }

        return flow.ToDetailDto();
    }

    private async Task PersistFeedbackFailureAsync(
        Guid flowId,
        Guid stepId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var storedStep = await database.FlowSteps.SingleAsync(
            item => item.Id == stepId,
            cancellationToken);
        storedStep.Status = StepStatus.Failed;
        storedStep.Phase = exception is AgentRunException
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
        storedStep.CompletedAt = DateTimeOffset.UtcNow;
        storedStep.DurationMilliseconds = Math.Max(
            1,
            (long)(storedStep.CompletedAt.Value - storedStep.StartedAt!.Value).TotalMilliseconds);
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flowId,
            FlowStepId = stepId,
            Type = "feedback.failed",
            Message = $"Product Manager could not process feedback: {exception.GetBaseException().Message}"
        });
        await database.SaveChangesAsync(cancellationToken);
    }
}

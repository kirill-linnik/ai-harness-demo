using System.Text.Json;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Data;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

public sealed class MissingQualificationCoordinator(
    IDbContextFactory<HarnessDbContext> databaseFactory,
    BootstrapTaskProfileFactory profileFactory,
    FlowLifecycleCoordinator lifecycle)
{
    internal const string BlockerCode = "MissingQualification";
    internal const string AccountManagerPlanStepKey =
        "account-manager:qualification-blocker";
    internal const string SafeFallbackMessage =
        "The current team cannot safely complete this request yet. You can revise the scope, " +
        "or retry after the available expertise has been updated.";

    public async Task<FlowRun> BlockAsync(
        Guid flowId,
        int iteration,
        MissingQualification qualification,
        Guid? sourceStepId,
        string workflowRevision,
        Func<Guid, CancellationToken, Task<FlowStep>> executeAccountManager,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(qualification);
        ArgumentNullException.ThrowIfNull(executeAccountManager);

        await using var lifecycleLease =
            await lifecycle.EnterAsync(flowId, cancellationToken);
        var prepared = await PrepareAsync(
            flowId,
            iteration,
            qualification,
            sourceStepId,
            workflowRevision,
            cancellationToken);
        if (prepared.AlreadyBlocked)
        {
            return await LoadAsync(flowId, cancellationToken);
        }

        string customerMessage;
        string? explanationFailure = null;
        var step = prepared.Step;
        if (step.Status == StepStatus.Pending)
        {
            try
            {
                step = await executeAccountManager(step.Id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                explanationFailure = Clip(
                    exception.GetBaseException().Message,
                    4_000);
            }
        }

        if (explanationFailure is null)
        {
            if (step.Status != StepStatus.Completed)
            {
                explanationFailure = string.IsNullOrWhiteSpace(step.PushbackReason)
                    ? "The Account Manager explanation step did not complete."
                    : Clip(step.PushbackReason, 4_000);
            }
            else
            {
                try
                {
                    customerMessage = ParseCustomerMessage(
                        step.OutputSummary,
                        qualification);
                    return await FinalizeBlockedAsync(
                        flowId,
                        step.Id,
                        customerMessage,
                        explanationFailure: null,
                        cancellationToken);
                }
                catch (Exception exception) when (
                    exception is IntakeContractException or
                        InvalidOperationException)
                {
                    explanationFailure = Clip(
                        exception.GetBaseException().Message,
                        4_000);
                }
            }
        }

        return await FinalizeBlockedAsync(
            flowId,
            step.Id,
            SafeFallbackMessage,
            explanationFailure,
            cancellationToken);
    }

    internal async Task<FlowRun> RecoverCompletedExplanationAsync(
        Guid flowId,
        Guid stepId,
        AgentExecutionResult recoveredResult,
        Guid sessionId,
        string copilotHome,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recoveredResult);
        await using var lifecycleLease =
            await lifecycle.EnterAsync(flowId, cancellationToken);

        MissingQualification qualification;
        string customerMessage;
        string? explanationFailure = null;
        await using (var database =
                     await databaseFactory.CreateDbContextAsync(
                         cancellationToken))
        {
            var flow = await database.Flows.SingleAsync(
                item => item.Id == flowId,
                cancellationToken);
            if (flow.Status == FlowStatus.Blocked)
            {
                return flow;
            }
            if (flow.Status is not (FlowStatus.Running or FlowStatus.Reworking))
            {
                throw new InvalidOperationException(
                    $"A recovered qualification explanation cannot complete while the flow is {flow.Status}.");
            }
            qualification = DeserializeBlocker(flow.CurrentBlockerDataJson);
            var step = await database.FlowSteps
                .Include(item => item.ToolCalls)
                .SingleAsync(
                    item =>
                        item.Id == stepId &&
                        item.FlowRunId == flowId,
                    cancellationToken);
            if (step.InvocationKind !=
                    ExecutionInvocationKind.BlockerExplanation ||
                !string.Equals(
                    step.PlanStepKey,
                    AccountManagerPlanStepKey,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The recovered step is not the canonical qualification explanation.");
            }

            step.CopilotSessionId = sessionId;
            step.CopilotSessionHome = copilotHome;
            step.OutputSummary =
                WorkflowEngine.BoundFailedOutput(recoveredResult.Output);
            step.ExecutionAttempts = Math.Max(
                step.ExecutionAttempts,
                recoveredResult.ExecutionAttempts);
            step.CompletedAt = completedAt;
            try
            {
                customerMessage = ParseCustomerMessage(
                    recoveredResult.Output,
                    qualification);
                step.Status = StepStatus.Completed;
                step.Phase = AgentRunPhase.Succeeded;
                step.PushbackReason = string.Empty;
            }
            catch (Exception exception) when (
                exception is IntakeContractException or
                    InvalidOperationException)
            {
                customerMessage = SafeFallbackMessage;
                explanationFailure = Clip(
                    exception.GetBaseException().Message,
                    4_000);
            }
            await database.SaveChangesAsync(cancellationToken);
        }

        return await FinalizeBlockedAsync(
            flowId,
            stepId,
            customerMessage,
            explanationFailure,
            cancellationToken);
    }

    private async Task<PreparedBlocker> PrepareAsync(
        Guid flowId,
        int iteration,
        MissingQualification qualification,
        Guid? sourceStepId,
        string workflowRevision,
        CancellationToken cancellationToken)
    {
        var blockerJson = SerializeBlocker(qualification);
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);
        var flow = await database.Flows
                       .Include(item => item.Steps)
                       .Include(item => item.Events)
                       .Include(item => item.AgentSnapshots)
                       .Include(item => item.TaskProfiles)
                       .SingleOrDefaultAsync(
                           item => item.Id == flowId,
                           cancellationToken)
                   ?? throw new KeyNotFoundException(
                       $"Factory flow '{flowId}' was not found.");
        if (flow.Iteration != iteration)
        {
            throw new InvalidOperationException(
                "The missing qualification does not belong to the active flow iteration.");
        }
        if (flow.Status == FlowStatus.Blocked)
        {
            var existingBlockedStep = flow.Steps.SingleOrDefault(step =>
                step.Iteration == iteration &&
                step.PlanStepKey == AccountManagerPlanStepKey);
            await transaction.CommitAsync(cancellationToken);
            return new PreparedBlocker(
                existingBlockedStep ?? new FlowStep
                {
                    FlowRunId = flow.Id,
                    Iteration = iteration,
                    AgentId = "account-manager",
                    AgentName = "Account Manager",
                    AgentRole = "account-manager"
                },
                AlreadyBlocked: true);
        }
        if (flow.Status is not (FlowStatus.Running or FlowStatus.Reworking))
        {
            throw new InvalidOperationException(
                $"Missing qualification cannot block a flow in {flow.Status}.");
        }

        EnsureImmutableBlocker(flow, qualification.Summary, blockerJson);
        if (!flow.Events.Any(item =>
                item.Type == "plan.missing-qualification"))
        {
            flow.Events.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = sourceStepId,
                Type = "plan.missing-qualification",
                Message =
                    "Team Lead reported an exact operator-visible qualification gap.",
                DataJson = blockerJson
            });
        }

        var accountManager = flow.AgentSnapshots.SingleOrDefault(snapshot =>
            snapshot.EnabledAtSnapshot &&
            string.Equals(
                snapshot.AgentId,
                "account-manager",
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "The blocked flow has no enabled immutable Account Manager snapshot.");
        var step = flow.Steps.SingleOrDefault(item =>
            item.Iteration == iteration &&
            item.PlanStepKey == AccountManagerPlanStepKey &&
            item.RetryOfStepId is null);
        if (step is null)
        {
            step = new FlowStep
            {
                FlowRunId = flow.Id,
                Iteration = iteration,
                Sequence = flow.Steps
                    .Where(item => item.Iteration == iteration)
                    .Select(item => item.Sequence)
                    .DefaultIfEmpty(10)
                    .Max() + 10,
                AgentId = accountManager.AgentId,
                AgentName = accountManager.Name,
                AgentRole = accountManager.Role,
                Label = "Explain missing qualification",
                PlanStepKey = AccountManagerPlanStepKey,
                PlanDutiesJson = JsonSerializer.Serialize(
                    new[] { PlanDuty.Analyze.ToString() }),
                PlanStage = PlanStage.BeforeReview,
                InvocationKind = ExecutionInvocationKind.BlockerExplanation,
                PermissionProfile = ExecutionPermissionProfile.ReadOnlySource,
                WorkflowRevision = workflowRevision,
                Status = StepStatus.Pending,
                Phase = AgentRunPhase.PreparingWorkspace,
                Attempt = 1,
                InputSummary = BuildAccountManagerAssignment(
                    flow.Title,
                    blockerJson)
            };
            step.StableSemanticRootId = step.Id;
            flow.Steps.Add(step);
            database.TaskProfiles.Add(profileFactory.Create(
                accountManager.Role,
                flow.ConsolidatedRequest,
                flow.Id,
                flow.Iteration,
                step.Id,
                AccountManagerPlanStepKey,
                accountManager.AgentId));
            flow.Events.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = step.Id,
                Type = "qualification.customer-explanation-scheduled",
                Message =
                    "Account Manager will translate the operator qualification gap into a customer-safe explanation."
            });
        }

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PreparedBlocker(step, AlreadyBlocked: false);
    }

    private async Task<FlowRun> FinalizeBlockedAsync(
        Guid flowId,
        Guid stepId,
        string customerMessage,
        string? explanationFailure,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);
        var flow = await database.Flows
            .Include(item => item.Steps)
            .Include(item => item.Events)
            .Include(item => item.Messages)
            .SingleAsync(item => item.Id == flowId, cancellationToken);
        if (flow.Status == FlowStatus.Blocked)
        {
            await transaction.CommitAsync(cancellationToken);
            return flow;
        }

        var step = flow.Steps.Single(item => item.Id == stepId);
        if (explanationFailure is not null &&
            step.Status != StepStatus.Failed)
        {
            step.Status = StepStatus.Failed;
            step.Phase = AgentRunPhase.Failed;
            step.PushbackReason = explanationFailure;
            step.CompletedAt ??= DateTimeOffset.UtcNow;
        }
        flow.CustomerBlockerMessage = customerMessage;
        flow.FailureReason = string.Empty;
        flow.OutcomeUrl = string.Empty;
        flow.OutcomeLabel = "Qualification needed";
        lifecycle.Transition(flow, FlowStatus.Blocked);

        if (!flow.Messages.Any(message =>
                message.Role == ConversationRole.AccountManager &&
                string.Equals(
                    message.Content,
                    customerMessage,
                    StringComparison.Ordinal)))
        {
            flow.Messages.Add(new FlowMessage
            {
                FlowRunId = flow.Id,
                Role = ConversationRole.AccountManager,
                Content = customerMessage,
                IsQuestion = true
            });
        }
        var eventType = explanationFailure is null
            ? "qualification.customer-explanation-completed"
            : "qualification.customer-explanation-failed";
        if (!flow.Events.Any(item =>
                item.FlowStepId == stepId &&
                item.Type == eventType))
        {
            flow.Events.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = stepId,
                Type = eventType,
                Message = explanationFailure is null
                    ? "Account Manager produced the customer-safe qualification explanation."
                    : "Account Manager could not produce a valid customer-safe explanation; the host supplied a safe fallback.",
                DataJson = explanationFailure is null
                    ? JsonSerializer.Serialize(new
                    {
                        UsedFallback = false
                    })
                    : JsonSerializer.Serialize(new
                    {
                        UsedFallback = true,
                        Error = explanationFailure
                    })
            });
        }
        if (!flow.Events.Any(item => item.Type == "flow.blocked"))
        {
            flow.Events.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = stepId,
                Type = "flow.blocked",
                Message =
                    "Flow is blocked on missing qualification and has no automatic retry."
            });
        }

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return flow;
    }

    private static void EnsureImmutableBlocker(
        FlowRun flow,
        string summary,
        string blockerJson)
    {
        if (flow.CurrentBlockerCode is not null &&
            (!string.Equals(
                 flow.CurrentBlockerCode,
                 BlockerCode,
                 StringComparison.Ordinal) ||
             !string.Equals(
                 flow.CurrentBlockerSummary,
                 summary,
                 StringComparison.Ordinal) ||
             !string.Equals(
                 flow.CurrentBlockerDataJson,
                 blockerJson,
                 StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The persisted qualification blocker is immutable for this flow.");
        }
        flow.CurrentBlockerCode = BlockerCode;
        flow.CurrentBlockerSummary = summary;
        flow.CurrentBlockerDataJson = blockerJson;
        flow.UpdatedAt = DateTimeOffset.UtcNow;
    }

    internal static string SerializeBlocker(MissingQualification qualification)
    {
        var json = JsonSerializer.Serialize(new
        {
            Code = BlockerCode,
            qualification.Summary,
            Missing = qualification.Missing ?? [],
            qualification.WhyRequired,
            qualification.SuggestedAgent
        });
        if (json.Length > 65_536)
        {
            throw new InvalidOperationException(
                "The validated missing-qualification data exceeds the event bound.");
        }
        return json;
    }

    internal static MissingQualification DeserializeBlocker(string? blockerJson)
    {
        if (string.IsNullOrWhiteSpace(blockerJson) ||
            blockerJson.Length > 65_536)
        {
            throw new InvalidOperationException(
                "The persisted missing-qualification document is absent or exceeds its bound.");
        }
        PersistedBlocker? persisted;
        try
        {
            persisted = JsonSerializer.Deserialize<PersistedBlocker>(
                blockerJson,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = false,
                    UnmappedMemberHandling =
                        System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
                });
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The persisted missing-qualification document is invalid.",
                exception);
        }
        if (persisted is null ||
            persisted.Code != BlockerCode ||
            string.IsNullOrWhiteSpace(persisted.Summary) ||
            persisted.Missing is null ||
            persisted.Missing.Count == 0 ||
            string.IsNullOrWhiteSpace(persisted.WhyRequired))
        {
            throw new InvalidOperationException(
                "The persisted missing-qualification document does not satisfy its host contract.");
        }
        return new MissingQualification
        {
            Summary = persisted.Summary,
            Missing = persisted.Missing,
            WhyRequired = persisted.WhyRequired,
            SuggestedAgent = persisted.SuggestedAgent
        };
    }

    internal static string BuildAccountManagerAssignment(
        string taskTitle,
        string blockerJson) => $$"""
        The Team Lead determined that the current team cannot safely complete this request.
        Produce one short customer-safe explanation and offer exactly these next choices in plain
        language: revise the scope, or retry after the available team expertise is repaired.
        Never repeat internal agent IDs, roster details, diagnostics, policy language, or the
        operator wording below. Do not claim the request failed and do not promise an automatic
        retry.

        Return exactly one intake document. Set Status to NeedsClarification, FlowKind to null,
        TaskTitle to "{{taskTitle}}", CustomerReply to only the safe explanation, and Brief to
        empty Goal/Details/SuccessCriteria/Constraints/Assumptions values.

        Private operator qualification data:
        {{blockerJson}}
        """;

    internal static string ParseCustomerMessage(
        string output,
        MissingQualification qualification)
    {
        var parsed = IntakeParser.Parse(output);
        if (parsed.Document.Status != IntakeStatus.NeedsClarification ||
            parsed.Document.FlowKind is not null)
        {
            throw new InvalidOperationException(
                "The Account Manager blocker response must be a NeedsClarification intake document.");
        }
        var message = parsed.Document.CustomerReply.Trim();
        if (string.Equals(
                message,
                qualification.Summary,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The Account Manager response repeated the operator blocker summary.");
        }
        var diagnostics = (qualification.Missing ?? [])
            .Append(qualification.Summary)
            .Append(qualification.WhyRequired)
            .Concat(qualification.SuggestedAgent is null
                ? []
                :
                [
                    qualification.SuggestedAgent.Id,
                    qualification.SuggestedAgent.Name,
                    qualification.SuggestedAgent.Description
                ])
            .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length >= 8);
        if (diagnostics.Any(value =>
                message.Contains(value, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "The Account Manager response exposed raw operator qualification data.");
        }
        return message;
    }

    private async Task<FlowRun> LoadAsync(
        Guid flowId,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        return await database.Flows
            .AsNoTracking()
            .SingleAsync(item => item.Id == flowId, cancellationToken);
    }

    private static string Clip(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    private sealed record PreparedBlocker(
        FlowStep Step,
        bool AlreadyBlocked);

    private sealed class PersistedBlocker
    {
        public string Code { get; init; } = string.Empty;

        public string Summary { get; init; } = string.Empty;

        public IReadOnlyList<string>? Missing { get; init; }

        public string WhyRequired { get; init; } = string.Empty;

        public SuggestedAgent? SuggestedAgent { get; init; }
    }
}

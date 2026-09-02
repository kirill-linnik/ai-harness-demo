using System.Diagnostics;
using System.Text.RegularExpressions;
using AiHarnessDemo.Contracts;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Data;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

public sealed partial class IntakeCoordinator(
    IDbContextFactory<HarnessDbContext> databaseFactory,
    AgentCatalog agentCatalog,
    ModelSelector modelSelector,
    AgentRunner agentRunner,
    IWorkspaceManager workspaceManager,
    HandoffGateEngine handoffGate,
    RepositoryContextGate contextGate,
    FlowQueue flowQueue)
{
    [GeneratedRegex(
        @"(?im)^\s*(?:\*\*)?INTAKE_STATUS(?:\*\*)?\s*:\s*(NEEDS_CLARIFICATION|AWAITING_CONFIRMATION|CONFIRMED)\s*$")]
    private static partial Regex IntakeStatusPattern();

    [GeneratedRegex(
        @"(?im)^\s*(?:\*\*)?CUSTOMER_REPLY(?:\*\*)?\s*:\s*(.+?)\s*$")]
    private static partial Regex CustomerReplyPattern();

    [GeneratedRegex(
        @"(?ims)^\s*(?:\*\*)?TASK_BRIEF(?:\*\*)?\s*:\s*(.+?)\s*\z")]
    private static partial Regex TaskBriefPattern();

    public async Task<IntakeResponse> ContinueAsync(
        IntakeRequest request,
        CancellationToken cancellationToken = default)
    {
        using var contextLease = await contextGate.EnterReadAsync(cancellationToken);
        var message = request.Message.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Tell the account manager what you want to build.");
        }

        await agentCatalog.SyncAsync(cancellationToken);
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var accountManager = await database.Agents
            .AsNoTracking()
            .SingleOrDefaultAsync(
                agent => agent.Role == "account-manager" && agent.Enabled,
                cancellationToken)
            ?? throw new InvalidOperationException(
                "Enable the Account Manager agent before starting voice intake.");
        var settings = await database.Settings.AsNoTracking().SingleAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.RepositoryPath) ||
            string.IsNullOrWhiteSpace(settings.RepositoryKnowledge) ||
            !RepositoryAnalyzer.IsProjectDirectory(settings.RepositoryPath))
        {
            throw new InvalidOperationException(
                "Add and study a project folder containing at least one Git repository before starting a factory flow.");
        }

        var flow = request.FlowId is null
            ? CreateFlow(message, settings)
            : await LoadFlowAsync(database, request.FlowId.Value, cancellationToken);
        if (request.FlowId is null)
        {
            database.Flows.Add(flow);
        }
        else if (flow.Status != FlowStatus.Intake)
        {
            throw new InvalidOperationException("This factory flow has already left intake.");
        }
        var pendingConfirmationBrief = GetPendingConfirmationBrief(flow);

        var customerMessage = new FlowMessage
        {
            FlowRunId = flow.Id,
            Role = ConversationRole.Customer,
            Content = message
        };
        flow.Messages.Add(customerMessage);
        database.Entry(customerMessage).State = EntityState.Added;
        var customerMessages = flow.Messages
            .Where(item => item.Role == ConversationRole.Customer)
            .Select(item => item.Content)
            .ToList();
        if (string.IsNullOrWhiteSpace(pendingConfirmationBrief))
        {
            flow.ConsolidatedRequest = FormatCustomerInputs(customerMessages);
        }
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);

        var workspace = string.IsNullOrWhiteSpace(flow.WorkspacePath)
            ? await workspaceManager.PrepareAsync(flow, cancellationToken)
            : new WorkspaceInfo(flow.WorkspacePath, flow.BranchName, CreatedNow: false);
        if (string.IsNullOrWhiteSpace(flow.WorkspacePath))
        {
            flow.WorkspacePath = workspace.Path;
            flow.BranchName = workspace.BranchName;
            await database.SaveChangesAsync(cancellationToken);
        }

        var model = modelSelector.Select("account-manager", complexity: 1);
        var intakeStep = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Sequence = -100 + customerMessages.Count,
            AgentId = accountManager.Id,
            AgentName = accountManager.Name,
            AgentRole = accountManager.Role,
            Label = "Review customer intake",
            Model = model.Model,
            ModelReason = model.Reason,
            Status = StepStatus.Running,
            Phase = AgentRunPhase.BuildingPrompt,
            Attempt = customerMessages.Count,
            StartedAt = DateTimeOffset.UtcNow,
            InputSummary = message
        };
        flow.Steps.Add(intakeStep);
        database.Entry(intakeStep).State = EntityState.Added;
        await database.SaveChangesAsync(cancellationToken);

        var stopwatch = Stopwatch.StartNew();
        AgentExecutionResult result;
        try
        {
            var learnings = await database.Learnings
                .AsNoTracking()
                .OrderBy(item => item.CreatedAt)
                .Take(12)
                .ToListAsync(cancellationToken);
            var priorReplies = flow.Messages
                .Where(item => item.Role == ConversationRole.AccountManager)
                .OrderBy(item => item.CreatedAt)
                .Select(item => item.Content)
                .ToList();
            result = await agentRunner.ExecuteAsync(
                new AgentExecutionContext(
                    flow.Id,
                    flow.Iteration,
                    accountManager.Id,
                    accountManager.Name,
                    accountManager.Role,
                    model.Model,
                    intakeStep.Attempt,
                    BuildDialogueTask(
                        flow.Messages,
                        flow.Outcome,
                        pendingConfirmationBrief),
                    flow.RepositoryKnowledge,
                    workspace.Path,
                    flow.Outcome,
                    $"Create a task-ready brief with sensible defaults. Delivery is already configured as {flow.Outcome}.",
                    priorReplies,
                    learnings,
                    Progress: progress =>
                        RecordProgressAsync(
                                flow.Id,
                                intakeStep.Id,
                                progress,
                                CancellationToken.None)
                            .GetAwaiter()
                            .GetResult()),
                cancellationToken);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            intakeStep.Status = StepStatus.Failed;
            intakeStep.Phase = exception is AgentRunException
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
            intakeStep.CompletedAt = DateTimeOffset.UtcNow;
            intakeStep.DurationMilliseconds = stopwatch.ElapsedMilliseconds;
            await database.SaveChangesAsync(cancellationToken);
            throw;
        }

        stopwatch.Stop();
        var response = ApplyConfirmationGate(
            ParseResponse(result.Output),
            pendingConfirmationBrief);
        flow.ConsolidatedRequest =
            response.Status == AccountManagerIntakeStatus.NeedsClarification
                ? FormatCustomerInputs(customerMessages)
                : response.TaskBrief;
        var accountManagerMessage = new FlowMessage
        {
            FlowRunId = flow.Id,
            Role = ConversationRole.AccountManager,
            Content = response.Reply,
            IsQuestion = response.Status != AccountManagerIntakeStatus.Confirmed
        };
        flow.Messages.Add(accountManagerMessage);
        database.Entry(accountManagerMessage).State = EntityState.Added;

        intakeStep.Label = response.Status switch
        {
            AccountManagerIntakeStatus.NeedsClarification => "Clarification requested",
            AccountManagerIntakeStatus.AwaitingConfirmation => "Customer confirmation requested",
            AccountManagerIntakeStatus.Confirmed => "Customer confirmed brief",
            _ => throw new InvalidOperationException("Unsupported Account Manager intake status.")
        };
        intakeStep.Status = StepStatus.Completed;
        intakeStep.Phase = AgentRunPhase.Succeeded;
        intakeStep.ExecutionAttempts = result.ExecutionAttempts;
        intakeStep.OutputSummary = result.Output;
        intakeStep.CompletedAt = DateTimeOffset.UtcNow;
        intakeStep.DurationMilliseconds = stopwatch.ElapsedMilliseconds;
        foreach (var toolCall in result.ToolCalls)
        {
            var storedCall = new AgentToolCall
            {
                FlowStepId = intakeStep.Id,
                ToolName = toolCall.ToolName,
                ArgumentsSummary = toolCall.ArgumentsSummary,
                Succeeded = toolCall.Succeeded
            };
            intakeStep.ToolCalls.Add(storedCall);
            database.Entry(storedCall).State = EntityState.Added;
        }

        var intakeGate = handoffGate.SubmitProposal(new HandoffProposal
        {
            FlowRunId = flow.Id,
            FlowStepId = intakeStep.Id,
            ActionType = response.Ready
                ? HandoffActionType.Advance
                : HandoffActionType.RequestRevision,
            Summary = string.IsNullOrWhiteSpace(response.TaskBrief)
                ? result.Output
                : response.TaskBrief,
            Evidence = $"Copilot Account Manager reviewed {customerMessages.Count} customer turn(s).",
            BlastRadius = HandoffBlastRadius.Low
        });
        flow.GateRecords.Add(intakeGate);
        database.Entry(intakeGate).State = EntityState.Added;
        var (intakeEventType, intakeEventMessage) = response.Status switch
        {
            AccountManagerIntakeStatus.NeedsClarification => (
                "intake.clarification",
                "Copilot Account Manager requested one material clarification."),
            AccountManagerIntakeStatus.AwaitingConfirmation => (
                "intake.confirmation_requested",
                "Copilot Account Manager presented its understanding for customer confirmation."),
            AccountManagerIntakeStatus.Confirmed => (
                "intake.confirmed",
                "Customer explicitly confirmed the Account Manager brief."),
            _ => throw new InvalidOperationException("Unsupported Account Manager intake status.")
        };
        var intakeEvent = new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = intakeStep.Id,
            Type = intakeEventType,
            Message = intakeEventMessage
        };
        flow.Events.Add(intakeEvent);
        database.Entry(intakeEvent).State = EntityState.Added;
        var queuedEvent = PrepareConfirmedHandoff(flow, response);
        if (queuedEvent is not null)
        {
            database.Entry(queuedEvent).State = EntityState.Added;
        }
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        if (queuedEvent is not null && !flowQueue.Queue(flow.Id))
        {
            throw new InvalidOperationException("Unable to queue the customer-confirmed flow.");
        }

        return new IntakeResponse(
            flow.ToDetailDto(),
            response.Reply,
            response.Ready,
            ShouldSpeak: true);
    }

    public static AccountManagerResponse ParseResponse(string output)
    {
        var status = IntakeStatusPattern().Match(output);
        var reply = CustomerReplyPattern().Match(output);
        var brief = TaskBriefPattern().Match(output);
        if (!status.Success || !reply.Success || !brief.Success)
        {
            throw new InvalidOperationException(
                "Copilot Account Manager returned an invalid intake contract. " +
                "Expected INTAKE_STATUS, CUSTOMER_REPLY, and TASK_BRIEF markers.");
        }

        var intakeStatus = status.Groups[1].Value.ToUpperInvariant() switch
        {
            "NEEDS_CLARIFICATION" => AccountManagerIntakeStatus.NeedsClarification,
            "AWAITING_CONFIRMATION" => AccountManagerIntakeStatus.AwaitingConfirmation,
            "CONFIRMED" => AccountManagerIntakeStatus.Confirmed,
            _ => throw new InvalidOperationException(
                "Copilot Account Manager returned an unsupported intake status.")
        };
        var taskBrief = brief.Groups[1].Value.Trim();
        var requiresBrief = intakeStatus is
            AccountManagerIntakeStatus.AwaitingConfirmation or
            AccountManagerIntakeStatus.Confirmed;
        if (requiresBrief &&
            (string.IsNullOrWhiteSpace(taskBrief) ||
             string.Equals(taskBrief, "NONE", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Copilot Account Manager requested or recorded confirmation without a TASK_BRIEF.");
        }

        return new AccountManagerResponse(
            intakeStatus,
            reply.Groups[1].Value.Trim(),
            requiresBrief ? taskBrief : string.Empty);
    }

    internal static AccountManagerResponse ApplyConfirmationGate(
        AccountManagerResponse response,
        string? pendingConfirmationBrief)
    {
        if (!response.Ready)
        {
            return response;
        }
        if (string.IsNullOrWhiteSpace(pendingConfirmationBrief))
        {
            throw new InvalidOperationException(
                "Copilot Account Manager cannot confirm a brief that the customer has not reviewed.");
        }

        return response with { TaskBrief = pendingConfirmationBrief.Trim() };
    }

    internal static FlowEvent? PrepareConfirmedHandoff(
        FlowRun flow,
        AccountManagerResponse response)
    {
        if (!response.Ready)
        {
            return null;
        }

        flow.Status = FlowStatus.Queued;
        var queuedEvent = new FlowEvent
        {
            FlowRunId = flow.Id,
            Type = "flow.queued",
            Message = "Customer-confirmed brief entered the AI factory queue."
        };
        flow.Events.Add(queuedEvent);
        return queuedEvent;
    }

    private static FlowRun CreateFlow(string message, HarnessSettings settings) =>
        new()
        {
            Title = BuildTitle(message),
            OriginalRequest = message,
            ConsolidatedRequest = message,
            RepositoryPath = settings.RepositoryPath,
            RepositoryKnowledge = settings.RepositoryKnowledge,
            Outcome = settings.Outcome,
            RuntimeMarker = "LiveCopilot"
        };

    private static async Task<FlowRun> LoadFlowAsync(
        HarnessDbContext database,
        Guid flowId,
        CancellationToken cancellationToken) =>
        await database.Flows
            .AsSplitQuery()
            .Include(item => item.Messages)
            .Include(item => item.Steps)
            .ThenInclude(step => step.ToolCalls)
            .Include(item => item.Events)
            .Include(item => item.GateRecords)
            .SingleOrDefaultAsync(item => item.Id == flowId, cancellationToken)
        ?? throw new KeyNotFoundException($"Factory flow '{flowId}' was not found.");

    private static string FormatCustomerInputs(IReadOnlyList<string> customerMessages) =>
        string.Join(
            Environment.NewLine,
            customerMessages.Select((item, index) => $"Customer input {index + 1}: {item}"));

    private static string GetPendingConfirmationBrief(FlowRun flow)
    {
        var latestIntakeEvent = flow.Events
            .Where(item => item.Type.StartsWith("intake.", StringComparison.Ordinal))
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefault();
        return latestIntakeEvent?.Type is "intake.confirmation_requested" or "intake.ready"
            ? flow.ConsolidatedRequest.Trim()
            : string.Empty;
    }

    internal static string BuildDialogueTask(
        IEnumerable<FlowMessage> messages,
        OutcomeType outcome,
        string? pendingConfirmationBrief = null)
    {
        var orderedMessages = messages
            .OrderBy(item => item.CreatedAt)
            .ToList();
        var dialogue = string.Join(
            Environment.NewLine,
            orderedMessages.Select(item => $"{item.Role}: {item.Content}"));
        var confirmationPolicy = string.IsNullOrWhiteSpace(pendingConfirmationBrief)
            ? "No proposed brief is awaiting customer approval, so this turn must not return " +
              "CONFIRMED. If the request is actionable, return AWAITING_CONFIRMATION with a complete " +
              "brief and ask the customer to validate your concise understanding. "
            : "The customer is replying to the unconfirmed brief below. Return CONFIRMED only if " +
              "their latest message clearly approves that brief without a correction. If they " +
              "correct it, incorporate the correction and request confirmation of the revised brief; " +
              "if they only reject it, ask one focused clarification question. ";
        var pendingBriefContext = string.IsNullOrWhiteSpace(pendingConfirmationBrief)
            ? string.Empty
            : Environment.NewLine +
              Environment.NewLine +
              "UNCONFIRMED_TASK_BRIEF:" +
              Environment.NewLine +
              pendingConfirmationBrief.Trim();

        return
            "Turn this complete customer dialogue into a brief the delivery team can act on. " +
            "Default to AWAITING_CONFIRMATION once meaningful work can begin; downstream details do " +
            "not need to be settled during intake. Treat all prior answers as settled and do not ask " +
            "for the same detail twice. Ask at most one focused clarification question in this turn. " +
            "A request for a design the customer can click is actionable and requires an interactive " +
            "result, not another prototype, implementation, or deployment choice. " +
            $"The configured delivery outcome is {outcome}; do not ask the customer how the work " +
            "should be packaged, released, or deployed. " +
            confirmationPolicy +
            pendingBriefContext +
            Environment.NewLine +
            Environment.NewLine +
            dialogue;
    }

    private async Task RecordProgressAsync(
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

    private static string BuildTitle(string message)
    {
        var title = message
            .ReplaceLineEndings(" ")
            .Trim()
            .TrimEnd('.', '!', '?');
        return title.Length <= 64 ? title : $"{title[..61]}...";
    }
}

public enum AccountManagerIntakeStatus
{
    NeedsClarification,
    AwaitingConfirmation,
    Confirmed
}

public sealed record AccountManagerResponse(
    AccountManagerIntakeStatus Status,
    string Reply,
    string TaskBrief)
{
    public bool Ready => Status == AccountManagerIntakeStatus.Confirmed;

    public bool AwaitingConfirmation =>
        Status == AccountManagerIntakeStatus.AwaitingConfirmation;
}

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
    RepositoryContextGate contextGate)
{
    [GeneratedRegex(
        @"(?im)^\s*(?:\*\*)?INTAKE_STATUS(?:\*\*)?\s*:\s*(READY|NEEDS_CLARIFICATION)\s*$")]
    private static partial Regex IntakeStatusPattern();

    [GeneratedRegex(
        @"(?im)^\s*(?:\*\*)?CUSTOMER_REPLY(?:\*\*)?\s*:\s*(.+?)\s*$")]
    private static partial Regex CustomerReplyPattern();

    [GeneratedRegex(
        @"(?im)^\s*(?:\*\*)?TASK_BRIEF(?:\*\*)?\s*:\s*(.+?)\s*$")]
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
            !RepositoryAnalyzer.IsGitRepository(settings.RepositoryPath))
        {
            throw new InvalidOperationException(
                "Add and study a valid source repository in Settings before starting a factory flow.");
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
        flow.ConsolidatedRequest = FormatCustomerInputs(customerMessages);
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
            Label = "Clarify customer request",
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
                    BuildDialogueTask(flow.Messages),
                    flow.RepositoryKnowledge,
                    workspace.Path,
                    flow.Outcome,
                    "Determine whether the customer request is implementation-ready.",
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
        var response = ParseResponse(result.Output);
        flow.ConsolidatedRequest = response.Ready
            ? response.TaskBrief
            : FormatCustomerInputs(customerMessages);
        var accountManagerMessage = new FlowMessage
        {
            FlowRunId = flow.Id,
            Role = ConversationRole.AccountManager,
            Content = response.Reply,
            IsQuestion = !response.Ready
        };
        flow.Messages.Add(accountManagerMessage);
        database.Entry(accountManagerMessage).State = EntityState.Added;

        intakeStep.Label = response.Ready ? "Request aligned" : "Clarification requested";
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
            Summary = result.Output,
            Evidence = $"Copilot Account Manager reviewed {customerMessages.Count} customer turn(s).",
            BlastRadius = HandoffBlastRadius.Low
        });
        flow.GateRecords.Add(intakeGate);
        database.Entry(intakeGate).State = EntityState.Added;
        var intakeEvent = new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = intakeStep.Id,
            Type = response.Ready ? "intake.ready" : "intake.clarification",
            Message = response.Ready
                ? "Copilot Account Manager confirmed a task-ready brief."
                : "Copilot Account Manager requested one material clarification."
        };
        flow.Events.Add(intakeEvent);
        database.Entry(intakeEvent).State = EntityState.Added;
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);

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

        var ready = string.Equals(
            status.Groups[1].Value,
            "READY",
            StringComparison.OrdinalIgnoreCase);
        var taskBrief = brief.Groups[1].Value.Trim();
        if (ready &&
            (string.IsNullOrWhiteSpace(taskBrief) ||
             string.Equals(taskBrief, "NONE", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Copilot Account Manager marked the request ready without a TASK_BRIEF.");
        }

        return new AccountManagerResponse(
            ready,
            reply.Groups[1].Value.Trim(),
            ready ? taskBrief : string.Empty);
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

    private static string BuildDialogueTask(IEnumerable<FlowMessage> messages)
    {
        var dialogue = string.Join(
            Environment.NewLine,
            messages
                .OrderBy(item => item.CreatedAt)
                .Select(item => $"{item.Role}: {item.Content}"));
        return
            "Review this customer dialogue against the repository knowledge. Decide whether the " +
            "request is implementation-ready; otherwise ask exactly one material clarification." +
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

public sealed record AccountManagerResponse(bool Ready, string Reply, string TaskBrief);

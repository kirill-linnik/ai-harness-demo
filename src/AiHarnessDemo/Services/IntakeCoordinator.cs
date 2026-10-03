using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiHarnessDemo.Contracts;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Security;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Data;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

public sealed partial class IntakeCoordinator(
    IDbContextFactory<HarnessDbContext> databaseFactory,
    IModelRouter modelRouter,
    BootstrapTaskProfileFactory profileFactory,
    RoutingObservationRecorder observationRecorder,
    IAgentRunner agentRunner,
    IWorkspaceManager workspaceManager,
    HandoffGateEngine handoffGate,
    RepositoryContextGate contextGate,
    FlowQueue flowQueue,
    WorkflowDefinitionProvider workflowProvider,
    INewWorkAdmissionService admissionService,
    FlowAgentSnapshotService snapshotService,
    FlowLifecycleCoordinator? lifecycleCoordinator = null,
    CopilotSessionJournal? sessionJournal = null,
    AgentManifestStager? manifestStager = null,
    PermissionProfileResolver? permissionProfileResolver = null,
    ILogger<IntakeCoordinator>? logger = null)
{
    internal const string InitialLinkedIntakePlanStepKey =
        "account-manager:initial-linked-intake";
    private readonly FlowLifecycleCoordinator _lifecycle =
        lifecycleCoordinator ?? new FlowLifecycleCoordinator();
    private readonly CopilotSessionJournal _sessionJournal =
        sessionJournal ?? new CopilotSessionJournal();
    private readonly AgentManifestStager _manifestStager =
        manifestStager ?? new AgentManifestStager();
    private readonly PermissionProfileResolver _permissionResolver =
        permissionProfileResolver ?? new PermissionProfileResolver();
    private readonly ILogger<IntakeCoordinator> _logger =
        logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<IntakeCoordinator>.Instance;
    internal Func<bool>? GitHubCliAvailableOverride { get; set; }
    internal Func<CancellationToken, Task<bool>>?
        GitHubAuthenticationAvailableOverride { get; set; }
    internal Func<string, Guid, CancellationToken, Task<CopilotSessionSnapshot>>?
        SessionInspectorOverride
    { get; set; }
    internal Func<CopilotSessionSnapshot, bool>?
        ActiveSessionStopperOverride
    { get; set; }
    private readonly ConcurrentDictionary<Guid, byte> _intakeClaims = new();
    private const int MaximumIntakeFailureReasonCharacters = 4_000;
    internal const string SafeIntakeRetryMessage =
        "The Account Manager could not validate the first intake turn. " +
        "The flow was saved; open it and retry the same intake conversation.";

    [GeneratedRegex(
        @"(?im)^\s*(?:\*\*)?INTAKE_STATUS(?:\*\*)?\s*:\s*(NEEDS_CLARIFICATION|AWAITING_CONFIRMATION|CONFIRMED)\s*$")]
    private static partial Regex IntakeStatusPattern();

    [GeneratedRegex(
        @"(?im)^\s*(?:\*\*)?TASK_TITLE(?:\*\*)?\s*:\s*(.+?)\s*$")]
    private static partial Regex TaskTitlePattern();

    [GeneratedRegex(
        @"(?im)^\s*(?:\*\*)?CUSTOMER_REPLY(?:\*\*)?\s*:\s*(.+?)\s*$")]
    private static partial Regex CustomerReplyPattern();

    [GeneratedRegex(
        @"(?ims)^\s*(?:\*\*)?TASK_BRIEF(?:\*\*)?\s*:\s*(.+?)\s*\z")]
    private static partial Regex TaskBriefPattern();

    public Task<IntakeResponse> ContinueAsync(
        IntakeRequest request,
        CancellationToken cancellationToken = default) =>
        request.FlowId is { } flowId
            ? ContinueWithLifecycleAsync(
                flowId,
                request,
                cancellationToken)
            : ContinueCoreAsync(
                request,
                linkedInitialTurn: false,
                cancellationToken);

    private async Task<IntakeResponse> ContinueWithLifecycleAsync(
        Guid flowId,
        IntakeRequest request,
        CancellationToken cancellationToken)
    {
        if (!_intakeClaims.TryAdd(flowId, 0))
        {
            throw new InvalidOperationException(
                "An Account Manager intake attempt is already pending or running for this flow.");
        }
        try
        {
            await using var lifecycleLease =
                await _lifecycle.EnterAsync(flowId, cancellationToken);
            return await ContinueCoreAsync(
                request,
                linkedInitialTurn: false,
                cancellationToken);
        }
        finally
        {
            _intakeClaims.TryRemove(flowId, out _);
        }
    }

    internal async Task<IntakeResponse?> ContinueInitialLinkedAsync(
        Guid flowId,
        string durableSeed,
        CancellationToken cancellationToken,
        bool queueReadyFlow = true)
    {
        if (!_intakeClaims.TryAdd(flowId, 0))
        {
            return null;
        }
        try
        {
            await using var lifecycleLease =
                await _lifecycle.EnterAsync(flowId, cancellationToken);
            Guid? existingStepId = null;
            AgentExecutionResult? recoveredResult = null;
            CopilotSessionSnapshot? completedJournal = null;
            var resumeSession = false;
            var recoverInterruptedSession = false;
            await using (var database =
                         await databaseFactory.CreateDbContextAsync(cancellationToken))
            {
                var candidate = await database.Flows
                    .AsNoTracking()
                    .AsSplitQuery()
                    .Include(flow => flow.Steps)
                    .Include(flow => flow.AgentSnapshots)
                    .SingleOrDefaultAsync(flow => flow.Id == flowId, cancellationToken)
                    ?? throw new KeyNotFoundException(
                        $"Factory flow '{flowId}' was not found.");
                if (candidate.Status != FlowStatus.Intake)
                {
                    return null;
                }

                var intakeAttempts = candidate.Steps
                    .Where(step =>
                        step.Iteration == candidate.Iteration &&
                        step.InvocationKind ==
                            ExecutionInvocationKind.Intake)
                    .ToList();
                if (intakeAttempts.Count == 0)
                {
                    // The initial linked turn was not yet durably materialized.
                }
                else
                {
                    var canonical = intakeAttempts
                        .Where(step => string.Equals(
                            step.PlanStepKey,
                            InitialLinkedIntakePlanStepKey,
                            StringComparison.Ordinal))
                        .ToList();
                    if (intakeAttempts.Count != 1 ||
                        canonical.Count != 1)
                    {
                        throw new InvalidOperationException(
                            "Linked intake recovery found duplicate or noncanonical Account Manager attempts.");
                    }

                    var step = canonical[0];
                    if (step.Status is StepStatus.Completed or StepStatus.Failed)
                    {
                        return null;
                    }
                    if (step.Status is not (StepStatus.Pending or StepStatus.Running))
                    {
                        throw new InvalidOperationException(
                            $"Linked intake recovery cannot continue a {step.Status} Account Manager attempt.");
                    }
                    var accountManager = candidate.AgentSnapshots
                        .SingleOrDefault(item =>
                            item.AgentId == "account-manager" &&
                            item.EnabledAtSnapshot)
                        ?? throw new InvalidOperationException(
                            "Linked intake recovery found no enabled Account Manager snapshot.");
                    ValidateCanonicalLinkedStep(
                        candidate,
                        step,
                        accountManager,
                        durableSeed);
                    existingStepId = step.Id;

                    var canonicalSessionId = AgentSessionIdentity.Create(
                        candidate.Id,
                        candidate.Iteration,
                        accountManager.AgentId,
                        InitialLinkedIntakePlanStepKey);
                    if (step.CopilotSessionId is { } persistedSessionId &&
                        persistedSessionId != canonicalSessionId)
                    {
                        throw new InvalidOperationException(
                            "Linked intake recovery found a noncanonical Copilot session identity.");
                    }

                    if (step.Status == StepStatus.Running)
                    {
                        if (step.CopilotSessionId is null)
                        {
                            throw new InvalidOperationException(
                                "The running linked intake attempt has no durable Copilot session identity.");
                        }
                        var copilotHome =
                            string.IsNullOrWhiteSpace(step.CopilotSessionHome)
                                ? _sessionJournal.ExpectedHome()
                                : step.CopilotSessionHome;
                        var journal = await InspectSessionAsync(
                            copilotHome,
                            canonicalSessionId,
                            cancellationToken);
                        if (journal.State == CopilotSessionJournalState.Active)
                        {
                            if (!TryStopActiveSession(journal))
                            {
                                return null;
                            }
                            journal = await InspectSessionAsync(
                                copilotHome,
                                canonicalSessionId,
                                cancellationToken);
                            if (journal.State ==
                                CopilotSessionJournalState.Active)
                            {
                                return null;
                            }
                        }
                        ValidateLinkedJournalBinding(
                            candidate,
                            accountManager,
                            canonicalSessionId,
                            journal);
                        switch (journal.State)
                        {
                            case CopilotSessionJournalState.Completed
                                when journal.Result is { Success: true } result &&
                                     CopilotReasoningHost.IsRecoverableCompletedOutput(
                                         accountManager.Role,
                                         result.OutputSummary,
                                         invocationKind:
                                             ExecutionInvocationKind.Intake,
                                         planStepKey:
                                             step.PlanStepKey,
                                         expectedFlowKind:
                                             candidate.Kind) &&
                                     CopilotReasoningHost.IsRecoveryCurrent(
                                         step.StartedAt,
                                         journal.CompletedAt):
                                recoveredResult = new AgentExecutionResult(
                                    result.OutputSummary,
                                    $"Recovered from completed Copilot session {canonicalSessionId:D}.",
                                    1,
                                    result.ToolCalls);
                                completedJournal = journal;
                                break;

                            case CopilotSessionJournalState.Interrupted:
                                resumeSession = true;
                                recoverInterruptedSession = true;
                                break;

                            case CopilotSessionJournalState.Completed:
                                throw new InvalidOperationException(
                                    "The completed linked intake journal is stale or does not contain a valid intake result.");

                            case CopilotSessionJournalState.Missing:
                                throw new InvalidOperationException(
                                    "The running linked intake has no deterministic Copilot session journal; manual retry is required.");

                            case CopilotSessionJournalState.Active:
                                throw new InvalidOperationException(
                                    "The linked intake Copilot session remained active after reconciliation.");

                            default:
                                throw new InvalidOperationException(
                                    "The linked intake journal state is unsupported.");
                        }
                    }
                }
            }

            IntakeResponse response;
            try
            {
                response = await ContinueCoreAsync(
                    new IntakeRequest(flowId, durableSeed),
                    linkedInitialTurn: true,
                    cancellationToken,
                    existingStepId,
                    recoveredResult,
                    resumeSession,
                    recoverInterruptedSession,
                    queueReadyFlow);
            }
            catch
            {
                if (completedJournal is not null)
                {
                    await CleanupRecoveredIntakeSessionAsync(
                        flowId,
                        existingStepId!.Value,
                        completedJournal,
                        CancellationToken.None);
                }
                throw;
            }
            if (completedJournal is not null)
            {
                await CleanupRecoveredIntakeSessionAsync(
                    flowId,
                    existingStepId!.Value,
                    completedJournal,
                    CancellationToken.None);
            }
            return response;
        }
        finally
        {
            _intakeClaims.TryRemove(flowId, out _);
        }
    }

    internal async Task<IReadOnlyList<Guid>> RecoverOrdinaryIntakesAsync(
        CancellationToken cancellationToken)
    {
        List<Guid> candidates;
        await using (var database =
                     await databaseFactory.CreateDbContextAsync(
                         cancellationToken))
        {
            candidates = await database.Flows
                .AsNoTracking()
                .Where(flow =>
                    flow.Status == FlowStatus.Intake &&
                    database.FlowSteps.Any(step =>
                        step.FlowRunId == flow.Id &&
                        step.Iteration == flow.Iteration &&
                        step.InvocationKind ==
                            ExecutionInvocationKind.Intake &&
                        step.PlanStepKey !=
                            InitialLinkedIntakePlanStepKey &&
                        (step.Status == StepStatus.Pending ||
                         step.Status == StepStatus.Running)))
                .Select(flow => flow.Id)
                .ToListAsync(cancellationToken);
        }

        var recovered = new List<Guid>();
        foreach (var flowId in candidates)
        {
            try
            {
                if (await RecoverOrdinaryIntakeAsync(
                        flowId,
                        cancellationToken))
                {
                    recovered.Add(flowId);
                }
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await RecordIntakeRecoveryFailureBestEffortAsync(
                    flowId,
                    "intake.ordinary-recovery-failed",
                    exception,
                    CancellationToken.None);
            }
        }
        return recovered;
    }

    private async Task<bool> RecoverOrdinaryIntakeAsync(
        Guid flowId,
        CancellationToken cancellationToken)
    {
        if (!_intakeClaims.TryAdd(flowId, 0))
        {
            return false;
        }

        CopilotSessionSnapshot? completedJournal = null;
        Guid? stepId = null;
        AgentExecutionResult? recoveredResult = null;
        try
        {
            await using var lifecycleLease =
                await _lifecycle.EnterAsync(flowId, cancellationToken);
            string durableInput;
            var resumeSession = false;
            var recoverInterruptedSession = false;
            await using (var database =
                         await databaseFactory.CreateDbContextAsync(
                             cancellationToken))
            {
                var flow = await database.Flows
                    .AsNoTracking()
                    .AsSplitQuery()
                    .Include(item => item.Steps)
                    .Include(item => item.AgentSnapshots)
                    .SingleOrDefaultAsync(
                        item => item.Id == flowId,
                        cancellationToken)
                    ?? throw new KeyNotFoundException(
                        $"Factory flow '{flowId}' was not found.");
                if (flow.Status != FlowStatus.Intake)
                {
                    return false;
                }
                var active = flow.Steps
                    .Where(step =>
                        step.Iteration == flow.Iteration &&
                        step.InvocationKind ==
                            ExecutionInvocationKind.Intake &&
                        step.Status is
                            StepStatus.Pending or StepStatus.Running)
                    .OrderBy(step => step.Sequence)
                    .ThenBy(step => step.Attempt)
                    .ToList();
                if (active.Count == 0)
                {
                    return false;
                }
                if (active.Count != 1)
                {
                    throw new InvalidOperationException(
                        "Intake recovery found multiple pending or running Account Manager attempts.");
                }

                var step = active[0];
                stepId = step.Id;
                durableInput = step.InputSummary;
                if (string.IsNullOrWhiteSpace(durableInput))
                {
                    throw new InvalidOperationException(
                        "The recoverable intake attempt has no durable customer input.");
                }
                var accountManager = flow.AgentSnapshots
                    .SingleOrDefault(item =>
                        item.AgentId == "account-manager" &&
                        item.EnabledAtSnapshot)
                    ?? throw new InvalidOperationException(
                        "Intake recovery found no enabled Account Manager snapshot.");
                ValidateCanonicalOrdinaryStep(
                    flow,
                    step,
                    accountManager,
                    durableInput);
                if (step.Status == StepStatus.Running)
                {
                    var expectedSessionId = AgentSessionIdentity.Create(
                        flow.Id,
                        flow.Iteration,
                        accountManager.AgentId,
                        step.PlanStepKey);
                    if (step.CopilotSessionId is not { } persistedSessionId ||
                        persistedSessionId != expectedSessionId)
                    {
                        throw new InvalidOperationException(
                            "The running intake attempt has no canonical durable Copilot session identity.");
                    }
                    var copilotHome =
                        string.IsNullOrWhiteSpace(step.CopilotSessionHome)
                            ? _sessionJournal.ExpectedHome()
                            : step.CopilotSessionHome;
                    var journal = await InspectSessionAsync(
                        copilotHome,
                        expectedSessionId,
                        cancellationToken);
                    if (journal.State ==
                        CopilotSessionJournalState.Active)
                    {
                        if (!TryStopActiveSession(journal))
                        {
                            // The journal still has verified live ownership. Defer instead of
                            // launching a second process or failing a potentially valid turn.
                            return false;
                        }
                        journal = await InspectSessionAsync(
                            copilotHome,
                            expectedSessionId,
                            cancellationToken);
                        if (journal.State ==
                            CopilotSessionJournalState.Active)
                        {
                            return false;
                        }
                    }
                    ValidateLinkedJournalBinding(
                        flow,
                        accountManager,
                        expectedSessionId,
                        journal);
                    switch (journal.State)
                    {
                        case CopilotSessionJournalState.Completed
                            when journal.Result is { Success: true } result &&
                                 CopilotReasoningHost.IsRecoveryCurrent(
                                     step.StartedAt,
                                     journal.CompletedAt):
                            completedJournal = journal;
                            recoveredResult = new AgentExecutionResult(
                                result.OutputSummary,
                                $"Recovered from completed Copilot session {expectedSessionId:D}.",
                                1,
                                result.ToolCalls);
                            break;

                        case CopilotSessionJournalState.Interrupted:
                            resumeSession = true;
                            recoverInterruptedSession = true;
                            break;

                        case CopilotSessionJournalState.Completed:
                            throw new InvalidOperationException(
                                "The completed intake journal is stale or did not finish successfully.");

                        case CopilotSessionJournalState.Missing:
                            throw new InvalidOperationException(
                                "The running intake attempt has no deterministic Copilot session journal and is ready for a customer retry.");

                        default:
                            throw new InvalidOperationException(
                                "The intake journal state is unsupported.");
                    }
                }
            }

            _ = await ContinueCoreAsync(
                new IntakeRequest(flowId, durableInput),
                linkedInitialTurn: false,
                cancellationToken,
                stepId,
                recoveredResult,
                resumeSession,
                recoverInterruptedSession,
                queueReadyFlow: false);
            if (completedJournal is not null)
            {
                await CleanupRecoveredIntakeSessionAsync(
                    flowId,
                    stepId!.Value,
                    completedJournal,
                    CancellationToken.None);
            }
            return true;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (stepId is { } failedStepId)
            {
                await FailRecoveredIntakeAsync(
                    flowId,
                    failedStepId,
                    exception,
                    recoveredResult,
                    CancellationToken.None);
            }
            if (completedJournal is not null)
            {
                await CleanupRecoveredIntakeSessionAsync(
                    flowId,
                    stepId!.Value,
                    completedJournal,
                    CancellationToken.None);
            }
            return true;
        }
        finally
        {
            _intakeClaims.TryRemove(flowId, out _);
        }
    }

    private async Task FailRecoveredIntakeAsync(
        Guid flowId,
        Guid stepId,
        Exception exception,
        AgentExecutionResult? result,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var flow = await LoadFlowAsync(
            database,
            flowId,
            cancellationToken);
        var step = flow.Steps.Single(item => item.Id == stepId);
        if (step.Status is
            StepStatus.Failed or StepStatus.Completed)
        {
            return;
        }
        await FailIntakeAttemptAsync(
            database,
            flow,
            step,
            exception,
            result,
            durationMilliseconds: 1,
            cancellationToken);
    }

    /// <summary>
    /// Recovers parentless Studio flows that crashed after their durable customer
    /// message was saved but before their canonical Account Manager step, workspace,
    /// and <see cref="TaskProfile" /> were ever materialized. Startup reconciliation
    /// must find these; ordinary recovery cannot, because it only reconciles flows
    /// that already have an Intake <see cref="FlowStep" />.
    /// </summary>
    internal async Task<IReadOnlyList<Guid>> RecoverUnmaterializedIntakesAsync(
        CancellationToken cancellationToken)
    {
        List<Guid> candidates;
        await using (var database =
                     await databaseFactory.CreateDbContextAsync(
                         cancellationToken))
        {
            candidates = await database.Flows
                .AsNoTracking()
                .Where(flow =>
                    flow.Status == FlowStatus.Intake &&
                    flow.ParentFlowRunId == null &&
                    database.FlowMessages.Any(message =>
                        message.FlowRunId == flow.Id &&
                        message.Role == ConversationRole.Customer) &&
                    !database.FlowEvents.Any(item =>
                        item.FlowRunId == flow.Id &&
                        item.Type ==
                            "intake.unmaterialized-recovery-failed") &&
                    !database.FlowSteps.Any(step =>
                        step.FlowRunId == flow.Id &&
                        step.Iteration == flow.Iteration &&
                        step.InvocationKind ==
                            ExecutionInvocationKind.Intake))
                .Select(flow => flow.Id)
                .ToListAsync(cancellationToken);
        }

        var recovered = new List<Guid>();
        foreach (var flowId in candidates)
        {
            try
            {
                if (await RecoverUnmaterializedIntakeAsync(
                        flowId,
                        cancellationToken))
                {
                    recovered.Add(flowId);
                }
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await RecordIntakeRecoveryFailureBestEffortAsync(
                    flowId,
                    "intake.unmaterialized-recovery-failed",
                    exception,
                    CancellationToken.None);
            }
        }
        return recovered;
    }

    private async Task<bool> RecoverUnmaterializedIntakeAsync(
        Guid flowId,
        CancellationToken cancellationToken)
    {
        if (!_intakeClaims.TryAdd(flowId, 0))
        {
            return false;
        }

        string? durableInput = null;
        try
        {
            await using var lifecycleLease =
                await _lifecycle.EnterAsync(flowId, cancellationToken);
            await using (var database =
                         await databaseFactory.CreateDbContextAsync(
                             cancellationToken))
            {
                var flow = await database.Flows
                    .AsNoTracking()
                    .AsSplitQuery()
                    .Include(item => item.Messages)
                    .Include(item => item.Steps)
                    .SingleOrDefaultAsync(
                        item => item.Id == flowId,
                        cancellationToken)
                    ?? throw new KeyNotFoundException(
                        $"Factory flow '{flowId}' was not found.");
                if (flow.Status != FlowStatus.Intake ||
                    flow.ParentFlowRunId is not null)
                {
                    // Already progressed (or claimed by a concurrent turn), or this is
                    // a linked flow handled by LinkedFlowCoordinator: nothing to do.
                    return false;
                }
                if (flow.Steps.Any(step =>
                        step.Iteration == flow.Iteration &&
                        step.InvocationKind ==
                            ExecutionInvocationKind.Intake))
                {
                    // A canonical step already exists: a prior recovery pass, or a
                    // live customer turn, already materialized this flow.
                    return false;
                }
                var customerMessages = flow.Messages
                    .Where(item => item.Role == ConversationRole.Customer)
                    .ToList();
                if (customerMessages.Count == 0)
                {
                    // Nothing durable to recover from.
                    return false;
                }
                if (customerMessages.Count > 1)
                {
                    throw new InvalidOperationException(
                        "The unmaterialized intake flow has more than one durable " +
                        "customer message and no canonical Account Manager step; " +
                        "manual review is required before recovery can continue.");
                }
                durableInput = customerMessages[0].Content;
                if (string.IsNullOrWhiteSpace(durableInput))
                {
                    throw new InvalidOperationException(
                        "The recoverable unmaterialized intake flow has no durable " +
                        "customer input.");
                }
            }

            _ = await ContinueCoreAsync(
                new IntakeRequest(flowId, durableInput),
                linkedInitialTurn: false,
                cancellationToken,
                existingIntakeStepId: null,
                recoveredExecutionResult: null,
                resumeSession: false,
                recoverInterruptedSession: false,
                queueReadyFlow: false,
                reuseDurableCustomerMessage: true);
            return true;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Fail closed: the flow keeps its durable customer message and stays in
            // Intake, ready for a customer retry, instead of retrying silently forever
            // or leaving the collision unrecorded.
            await PersistNewIntakeFailureAsync(
                flowId,
                durableInput ?? string.Empty,
                exception,
                CancellationToken.None);
            return true;
        }
        finally
        {
            _intakeClaims.TryRemove(flowId, out _);
        }
    }

    private async Task RecordIntakeRecoveryFailureBestEffortAsync(
        Guid flowId,
        string eventType,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var database =
                await databaseFactory.CreateDbContextAsync(cancellationToken);
            if (!await database.Flows
                    .AsNoTracking()
                    .AnyAsync(item => item.Id == flowId, cancellationToken) ||
                await database.FlowEvents.AnyAsync(
                    item =>
                        item.FlowRunId == flowId &&
                        item.Type == eventType,
                    cancellationToken))
            {
                return;
            }
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flowId,
                Type = eventType,
                Message =
                    "Startup intake reconciliation skipped this flow after a durable-state error: " +
                    ClipFailureText(
                        exception.GetBaseException().Message,
                        MaximumIntakeFailureReasonCharacters)
            });
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Recovery diagnostics are best-effort. A poisoned row must not prevent
            // startup reconciliation from continuing with unrelated flows.
        }
    }

    private async Task CleanupRecoveredIntakeSessionAsync(
        Guid flowId,
        Guid stepId,
        CopilotSessionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            _manifestStager.CleanupSessionRoot(
                snapshot.CopilotHome,
                snapshot.SessionId);
        }
        catch (Exception exception)
        {
            await using var database =
                await databaseFactory.CreateDbContextAsync(
                    cancellationToken);
            if (!await database.FlowEvents.AnyAsync(
                    item =>
                        item.FlowRunId == flowId &&
                        item.FlowStepId == stepId &&
                        item.Type ==
                        "agent.staged-context-cleanup-failed",
                    cancellationToken))
            {
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flowId,
                    FlowStepId = stepId,
                    Type =
                        "agent.staged-context-cleanup-failed",
                    Message =
                        "The intake result is durable, but its exact session-owned staged context still requires cleanup.",
                    DataJson = JsonSerializer.Serialize(new
                    {
                        Error = ClipFailureText(
                            exception.GetBaseException().Message,
                            1_000)
                    })
                });
                await database.SaveChangesAsync(
                    cancellationToken);
            }
        }
    }

    private Task<CopilotSessionSnapshot> InspectSessionAsync(
        string copilotHome,
        Guid sessionId,
        CancellationToken cancellationToken) =>
        SessionInspectorOverride is null
            ? _sessionJournal.InspectAsync(
                copilotHome,
                sessionId,
                cancellationToken)
            : SessionInspectorOverride(
                copilotHome,
                sessionId,
                cancellationToken);

    private bool TryStopActiveSession(
        CopilotSessionSnapshot snapshot) =>
        ActiveSessionStopperOverride?.Invoke(snapshot) ??
        _sessionJournal.TryStopActiveSession(snapshot);

    private async Task<IntakeResponse> ContinueCoreAsync(
        IntakeRequest request,
        bool linkedInitialTurn,
        CancellationToken cancellationToken,
        Guid? existingIntakeStepId = null,
        AgentExecutionResult? recoveredExecutionResult = null,
        bool resumeSession = false,
        bool recoverInterruptedSession = false,
        bool queueReadyFlow = true,
        bool reuseDurableCustomerMessage = false)
    {
        using var contextLease = await contextGate.EnterReadAsync(cancellationToken);
        var message = request.Message?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Tell the account manager what you want to build.");
        }

        if (request.FlowId is null)
        {
            await admissionService.EnsureReadyAsync(cancellationToken);
        }
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var settings = await database.Settings.AsNoTracking().SingleAsync(cancellationToken);
        var configuredDeliveryOutcome = ResolveDeliveryOutcome(settings);

        var flow = request.FlowId is null
            ? CreateFlow(message, settings)
            : await LoadFlowAsync(database, request.FlowId.Value, cancellationToken);
        if (request.FlowId is null)
        {
            database.Flows.Add(flow);
            snapshotService.CaptureForNewFlow(database, flow);
        }
        else if (flow.Status != FlowStatus.Intake)
        {
            throw new InvalidOperationException("This factory flow has already left intake.");
        }
        if (existingIntakeStepId is null &&
            flow.Steps.Any(step =>
                step.Iteration == flow.Iteration &&
                step.InvocationKind == ExecutionInvocationKind.Intake &&
                step.Status is StepStatus.Pending or StepStatus.Running))
        {
            throw new InvalidOperationException(
                "An Account Manager intake attempt is already pending or running for this flow.");
        }
        var accountManager = flow.AgentSnapshots
            .SingleOrDefault(agent =>
                agent.AgentId == "account-manager" &&
                agent.EnabledAtSnapshot)
            ?? throw new InvalidOperationException(
                "This flow has no enabled Account Manager snapshot.");
        var pendingConfirmation = GetPendingConfirmation(flow);
        var linkedSeed = flow.ParentFlowRunId is not null &&
                         flow.LinkKind is not null
            ? LinkedFlowCoordinator.ReadLinkedIntakeSeed(flow)
            : null;
        if (configuredDeliveryOutcome == OutcomeType.PullRequest &&
            (pendingConfirmation?.Document?.FlowKind == FlowKind.Delivery ||
             linkedInitialTurn &&
             flow.LinkKind == FlowLinkKind.AdvisoryPromotion))
        {
            await GitHubPublicationPrerequisites.RequireAsync(
                GitHubCliAvailableOverride,
                GitHubAuthenticationAvailableOverride,
                cancellationToken);
        }
        if (linkedInitialTurn &&
            !string.Equals(
                message,
                linkedSeed,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The initial linked intake request does not match its durable seed.");
        }
        var promotionSeed = TryAuthorizeFirstPromotionTurn(
            flow,
            message,
            pendingConfirmation,
            linkedSeed,
            existingIntakeStepId);
        var failedAttemptToRetry = linkedInitialTurn
            ? null
            : flow.Steps
                .Where(step =>
                    step.InvocationKind == ExecutionInvocationKind.Intake &&
                    step.Status == StepStatus.Failed &&
                    string.Equals(
                        step.InputSummary,
                        message,
                        StringComparison.Ordinal))
                .OrderByDescending(step => step.StartedAt)
                .ThenByDescending(step => step.Sequence)
                .ThenByDescending(step => step.Attempt)
                .FirstOrDefault();
        if (failedAttemptToRetry is not null &&
            flow.Steps.Any(step =>
                step.InvocationKind == ExecutionInvocationKind.Intake &&
                string.Equals(
                    step.InputSummary,
                    message,
                    StringComparison.Ordinal) &&
                (step.StartedAt > failedAttemptToRetry.StartedAt ||
                 step.StartedAt == failedAttemptToRetry.StartedAt &&
                 (step.Sequence > failedAttemptToRetry.Sequence ||
                  step.Sequence == failedAttemptToRetry.Sequence &&
                  step.Attempt > failedAttemptToRetry.Attempt))))
        {
            failedAttemptToRetry = null;
        }

        var existingLinkedMessages = linkedInitialTurn
            ? flow.Messages
                .Where(item => item.Role == ConversationRole.Customer)
                .ToArray()
            : [];
        if (existingLinkedMessages.Length > 1 ||
            existingLinkedMessages.Length == 1 &&
            !string.Equals(
                existingLinkedMessages[0].Content,
                message,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The linked intake message history does not match its durable seed.");
        }
        var repeatsFailedCustomerTurn =
            failedAttemptToRetry is not null &&
            flow.Messages
                .Where(item => item.Role == ConversationRole.Customer)
                .OrderBy(item => item.CreatedAt)
                .LastOrDefault() is { } latestCustomer &&
            string.Equals(
                latestCustomer.Content,
                message,
                StringComparison.Ordinal);
        if (reuseDurableCustomerMessage &&
            (existingIntakeStepId is not null ||
             linkedInitialTurn ||
             flow.Steps.Any(step =>
                 step.Iteration == flow.Iteration &&
                 step.InvocationKind == ExecutionInvocationKind.Intake) ||
             flow.Messages.Count(item =>
                 item.Role == ConversationRole.Customer) != 1 ||
             !string.Equals(
                 flow.Messages.Single(item =>
                     item.Role == ConversationRole.Customer).Content,
                 message,
                 StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Unmaterialized intake recovery requires exactly one durable " +
                "customer message and no existing Account Manager step or " +
                "session; the durable state is invalid or colliding.");
        }
        if (existingIntakeStepId is null &&
            existingLinkedMessages.Length == 0 &&
            !repeatsFailedCustomerTurn &&
            !reuseDurableCustomerMessage)
        {
            var customerMessage = new FlowMessage
            {
                FlowRunId = flow.Id,
                Role = ConversationRole.Customer,
                Content = message
            };
            flow.Messages.Add(customerMessage);
            database.Entry(customerMessage).State = EntityState.Added;
            var existingAttachmentBytes = flow.Messages
                .Where(item => item.Id != customerMessage.Id)
                .SelectMany(item => item.Attachments)
                .Sum(item => item.Length);
            var existingAttachmentCount = flow.Messages
                .Where(item => item.Id != customerMessage.Id)
                .Sum(item => item.Attachments.Count);
            var attachments = CustomerAttachmentStore.Prepare(
                flow.Id,
                customerMessage.Id,
                request.Attachments,
                existingAttachmentBytes,
                existingAttachmentCount);
            customerMessage.Attachments.AddRange(attachments);
            database.FlowAttachments.AddRange(attachments);
            if (attachments.Count > 0)
            {
                var receipt = new FlowEvent
                {
                    FlowRunId = flow.Id,
                    Type = "customer.attachments-received",
                    Message =
                        $"Stored {attachments.Count} customer-uploaded file(s) for this flow.",
                    DataJson = JsonSerializer.Serialize(attachments.Select(item => new
                    {
                        item.Id,
                        item.FlowMessageId,
                        item.FileName,
                        item.Length,
                        item.Digest
                    }))
                };
                flow.Events.Add(receipt);
                database.FlowEvents.Add(receipt);
            }
        }
        else if (request.Attachments is { Count: > 0 })
        {
            var latest = flow.Messages
                .Where(item => item.Role == ConversationRole.Customer)
                .OrderBy(item => item.CreatedAt)
                .LastOrDefault();
            if (latest is null ||
                !string.Equals(latest.Content, message, StringComparison.Ordinal) ||
                !CustomerAttachmentStore.MatchesRetriedMessage(
                    flow.Id, latest, request.Attachments))
            {
                throw new ArgumentException(
                    "A retried intake turn cannot replace its saved attachments. Send a new customer message instead.");
            }
        }
        var customerMessages = flow.Messages
            .Where(item => item.Role == ConversationRole.Customer)
            .Select(item => item.Content)
            .ToList();
        if (pendingConfirmation is null)
        {
            flow.ConsolidatedRequest = FormatCustomerInputs(customerMessages);
        }
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);

        try
        {
            var workspace = await workspaceManager.PrepareAsync(
                flow,
                cancellationToken);
            var workspaceStateChanged = false;
            if (string.IsNullOrWhiteSpace(flow.WorkspacePath))
            {
                flow.WorkspacePath = workspace.Path;
                flow.BranchName = workspace.BranchName;
                workspaceStateChanged = true;
            }
            if (workspace.Mode is
                    WorkspaceMode.ProvisionalReadOnly or WorkspaceMode.AdvisoryReadOnly &&
                !flow.Events.Any(item =>
                    item.Type == "workspace.guarded-snapshot-created"))
            {
                var workspaceEvent = new FlowEvent
                {
                    FlowRunId = flow.Id,
                    Type = "workspace.guarded-snapshot-created",
                    Message =
                        "Created a guarded per-flow source snapshot from the latest available Git base, or local files when unversioned, without changing source checkout files or leaving temporary worktrees.",
                    DataJson = JsonSerializer.Serialize(new
                    {
                        Mode = workspace.Mode.ToString(),
                        workspace.BaselineDigest,
                        FileCount = workspace.BaselineFileCount,
                        TotalBytes = workspace.BaselineTotalBytes
                    })
                };
                flow.Events.Add(workspaceEvent);
                database.Entry(workspaceEvent).State = EntityState.Added;
                workspaceStateChanged = true;
            }
            if (workspaceStateChanged)
            {
                await database.SaveChangesAsync(cancellationToken);
            }

            FlowStep intakeStep;
            if (existingIntakeStepId is { } persistedStepId)
            {
                intakeStep = flow.Steps.Single(step => step.Id == persistedStepId);
                if (linkedInitialTurn)
                {
                    ValidateCanonicalLinkedStep(
                        flow,
                        intakeStep,
                        accountManager,
                        message);
                }
                else
                {
                    ValidateCanonicalOrdinaryStep(
                        flow,
                        intakeStep,
                        accountManager,
                        message);
                }
                if (intakeStep.Status is not (
                        StepStatus.Pending or StepStatus.Running))
                {
                    throw new InvalidOperationException(
                        "The durable intake attempt is no longer recoverable.");
                }
            }
            else
            {
                var retryAttempt = failedAttemptToRetry is null
                    ? customerMessages.Count
                    : flow.Steps
                        .Where(step =>
                            step.InvocationKind ==
                                ExecutionInvocationKind.Intake &&
                            string.Equals(
                                step.InputSummary,
                                message,
                                StringComparison.Ordinal))
                        .Select(step => step.Attempt)
                        .DefaultIfEmpty()
                        .Max() + 1;
                intakeStep = new FlowStep
                {
                    FlowRunId = flow.Id,
                    Iteration = flow.Iteration,
                    Sequence = failedAttemptToRetry is null
                        ? -100 + customerMessages.Count
                        : flow.Steps
                            .Where(step =>
                                step.InvocationKind ==
                                ExecutionInvocationKind.Intake)
                            .Select(step => step.Sequence)
                            .DefaultIfEmpty(-100)
                            .Max() + 1,
                    AgentId = accountManager.AgentId,
                    AgentName = accountManager.Name,
                    AgentRole = accountManager.Role,
                    Label = "Review customer intake",
                    PlanStepKey = linkedInitialTurn
                        ? InitialLinkedIntakePlanStepKey
                        : failedAttemptToRetry is null
                            ? string.Empty
                            : $"account-manager:intake-retry:" +
                              $"{(failedAttemptToRetry.StableSemanticRootId ??
                                 failedAttemptToRetry.Id):N}:{retryAttempt}",
                    PlanDutiesJson = """["Analyze"]""",
                    PlanStage = PlanStage.BeforeReview,
                    InvocationKind = ExecutionInvocationKind.Intake,
                    PermissionProfile =
                        ExecutionPermissionProfile.ReadOnlySource,
                    Status = StepStatus.Pending,
                    Phase = AgentRunPhase.BuildingPrompt,
                    WorkflowRevision =
                        workflowProvider.GetEffective().Revision,
                    Attempt = retryAttempt,
                    InputSummary = message,
                    RetryOfStepId =
                        failedAttemptToRetry?.StableSemanticRootId ??
                        failedAttemptToRetry?.Id
                };
                intakeStep.StableSemanticRootId =
                    failedAttemptToRetry?.StableSemanticRootId ??
                    failedAttemptToRetry?.Id ??
                    intakeStep.Id;
                flow.Steps.Add(intakeStep);
                database.Entry(intakeStep).State = EntityState.Added;
                if (failedAttemptToRetry is not null)
                {
                    var retryEvent = new FlowEvent
                    {
                        FlowRunId = flow.Id,
                        FlowStepId = intakeStep.Id,
                        Type = "intake.retry-started",
                        Message =
                            "Retrying the failed Account Manager intake turn in the same flow, snapshot, and workspace without duplicating the customer message."
                    };
                    flow.Events.Add(retryEvent);
                    database.Entry(retryEvent).State = EntityState.Added;
                }
            }
            if (!flow.TaskProfiles.Any(profile =>
                    profile.FlowStepId == intakeStep.Id))
            {
                var profile = profileFactory.Create(
                    accountManager.Role,
                    FormatCustomerInputs(customerMessages),
                    flow.Id,
                    flow.Iteration,
                    intakeStep.Id,
                    intakeStep.PlanStepKey,
                    accountManager.AgentId);
                flow.TaskProfiles.Add(profile);
                database.TaskProfiles.Add(profile);
            }
            await database.SaveChangesAsync(cancellationToken);
            RoutingDecision? routing = null;
            if (recoveredExecutionResult is null)
            {
                routing = await modelRouter.SelectAsync(
                    new RoutingRequest(
                        intakeStep.Id,
                        settings.ModelSelectionStrategy),
                    cancellationToken);
                intakeStep.Model = routing.SelectedModel;
                intakeStep.ModelEffort = routing.SelectedEffort;
                intakeStep.ModelReason = routing.Reason;
            }
            else if (string.IsNullOrWhiteSpace(intakeStep.Model) ||
                     string.IsNullOrWhiteSpace(intakeStep.ModelEffort))
            {
                throw new InvalidOperationException(
                    "The completed linked intake journal has no persisted routing identity.");
            }
            BindIntakePermissionAtFirstLaunch(
                flow,
                intakeStep);
            intakeStep.Status = StepStatus.Running;
            intakeStep.Phase = AgentRunPhase.BuildingPrompt;
            intakeStep.StartedAt ??= DateTimeOffset.UtcNow;
            var intakeSessionId =
                AgentSessionIdentity.Create(
                    flow.Id,
                    flow.Iteration,
                    accountManager.AgentId,
                    intakeStep.PlanStepKey);
            if (intakeStep.CopilotSessionId is { } existingSessionId &&
                existingSessionId != intakeSessionId)
            {
                throw new InvalidOperationException(
                    "The durable intake attempt has a noncanonical Copilot session identity.");
            }
            intakeStep.CopilotSessionId = intakeSessionId;
            if (string.IsNullOrWhiteSpace(intakeStep.CopilotSessionHome))
            {
                intakeStep.CopilotSessionHome =
                    _sessionJournal.ExpectedHome();
            }
            if (existingIntakeStepId is not null)
            {
                AddLinkedRecoveryEventOnce(
                    flow,
                    intakeStep,
                    linkedInitialTurn
                        ? recoveredExecutionResult is not null
                            ? "linked.intake-journal-completed"
                            : resumeSession
                                ? "linked.intake-session-resumed"
                                : "linked.intake-pending-recovered"
                        : recoveredExecutionResult is not null
                            ? "intake.recovery-journal-completed"
                            : resumeSession
                                ? "intake.recovery-session-resumed"
                                : "intake.recovery-pending",
                    recoveredExecutionResult is not null
                        ? "Recovered the completed initial Account Manager result from its deterministic Copilot session journal."
                        : resumeSession
                            ? "Resuming the interrupted initial Account Manager Copilot session."
                            : "Continued the existing pending initial Account Manager attempt without creating a duplicate.");
            }
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
                result = recoveredExecutionResult ??
                    await agentRunner.ExecuteAsync(
                    new AgentExecutionContext(
                        flow.Id,
                        flow.Iteration,
                        accountManager.AgentId,
                        accountManager.Name,
                        accountManager.Role,
                        intakeStep.Model,
                        intakeStep.ModelEffort,
                        intakeStep.Attempt,
                        BuildDialogueTask(
                            flow.Messages,
                            configuredDeliveryOutcome,
                            pendingConfirmation?.NormalizedBriefJson,
                            pendingConfirmation?.Document?.FlowKind,
                            promotionSeed),
                        flow.RepositoryKnowledge,
                        flow.RepositoryPath,
                        workspace.Path,
                        intakeSessionId,
                        configuredDeliveryOutcome,
                        $"Create a task-ready brief with sensible defaults. If classified as Delivery, packaging is already configured as {configuredDeliveryOutcome}; Advisory never publishes.",
                        priorReplies,
                        learnings,
                        ModelSelectionStrategy: settings.ModelSelectionStrategy,
                        ExpectedAcceptedTimeSeconds:
                            routing?.PredictedAcceptedTimeSeconds ?? 0,
                        ResumeSession: resumeSession,
                        RecoverInterruptedSession:
                            recoverInterruptedSession,
                        GovernedRepositoryRelativePaths: null,
                        Progress: progress =>
                        {
                            if (progress.ExecutionPrompt is not null)
                            {
                                intakeStep.ExecutionPrompt = progress.ExecutionPrompt;
                            }
                            if (progress.CopilotSessionId is not null)
                            {
                                intakeStep.CopilotSessionId = progress.CopilotSessionId;
                                intakeStep.CopilotSessionHome =
                                    progress.CopilotSessionHome ?? string.Empty;
                            }
                            RecordProgressAsync(
                                    flow.Id,
                                    intakeStep.Id,
                                    progress,
                                    CancellationToken.None)
                                .GetAwaiter()
                                .GetResult();
                        },
                        InvocationStartedAt: intakeStep.StartedAt,
                        FlowStepId: intakeStep.Id,
                        InvocationKind: intakeStep.InvocationKind,
                        PromotionContext: promotionSeed is null
                            ? null
                            : new AdvisoryPromotionContext(
                                linkedSeed!,
                                AdvisoryPromotionSeedParser.ComputeHash(
                                    linkedSeed!)),
                        PlanStepKey: intakeStep.PlanStepKey,
                        FlowKind: flow.Kind),
                    cancellationToken);
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                await FailIntakeAttemptAsync(
                    database,
                    flow,
                    intakeStep,
                    exception,
                    result: null,
                    stopwatch.ElapsedMilliseconds,
                    cancellationToken);
                if (request.FlowId is null)
                {
                    throw new IntakeAttemptException(
                        flow.Id,
                        flow.Status,
                        SafeIntakeRetryMessage,
                        exception);
                }
                throw;
            }

            stopwatch.Stop();
            AccountManagerResponse response;
            try
            {
                response = ParseResponse(result.Output);
                if (flow.LinkKind == FlowLinkKind.AdvisoryPromotion &&
                    response.FlowKind is not (null or FlowKind.Delivery))
                {
                    throw new InvalidOperationException(
                        "An Advisory-promotion intake must remain a Delivery flow.");
                }
                response = promotionSeed is not null && response.Ready
                    ? ApplyPromotionConfirmationGate(
                        response,
                        promotionSeed,
                        flow.Title)
                    : ApplyConfirmationGate(
                        response,
                        pendingConfirmation?.Document);
                if (response.Status != AccountManagerIntakeStatus.NeedsClarification &&
                    flow.LinkKind is null &&
                    CustomerExternalFileGate.MissingFiles(flow) is { Count: > 0 } missing)
                {
                    response = RequireCustomerUploads(response, missing);
                    var inputEvent = new FlowEvent
                    {
                        FlowRunId = flow.Id,
                        FlowStepId = intakeStep.Id,
                        Type = "intake.external-upload-required",
                        Message =
                            "The Account Manager cannot confirm an outside-project file reference without a customer upload.",
                        DataJson = JsonSerializer.Serialize(new { MissingFiles = missing })
                    };
                    flow.Events.Add(inputEvent);
                    database.FlowEvents.Add(inputEvent);
                }
                ValidateIntakeCompletion(
                    flow,
                    response,
                    configuredDeliveryOutcome);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await FailIntakeAttemptAsync(
                    database,
                    flow,
                    intakeStep,
                    exception,
                    result,
                    stopwatch.ElapsedMilliseconds,
                    cancellationToken);
                if (request.FlowId is null)
                {
                    throw new IntakeAttemptException(
                        flow.Id,
                        flow.Status,
                        SafeIntakeRetryMessage,
                        exception);
                }
                throw;
            }
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
            await observationRecorder.RecordCompletionAsync(
                intakeStep.Id,
                accepted: response.Status != AccountManagerIntakeStatus.NeedsClarification,
                intakeStep.DurationMilliseconds,
                result.ExecutionAttempts,
                response.Ready ? "confirmed-intake" : "intake-turn",
                cancellationToken);
            foreach (var toolCall in result.ToolCalls)
            {
                var storedCall = new AgentToolCall
                {
                    FlowStepId = intakeStep.Id,
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
                Message = intakeEventMessage,
                DataJson = response.RawContractJson
            };
            flow.Events.Add(intakeEvent);
            database.Entry(intakeEvent).State = EntityState.Added;
            if (response.Ready)
            {
                flow.ModelSelectionStrategy = settings.ModelSelectionStrategy;
            }
            var queuedEvent = ApplyIntakeOutcome(
                flow,
                response,
                configuredDeliveryOutcome,
                _lifecycle);
            if (queuedEvent is not null)
            {
                database.Entry(queuedEvent).State = EntityState.Added;
            }
            if (response.FlowKind == FlowKind.Advisory &&
                !flow.Events.Any(item => item.Type == "workspace.advisory-policy"))
            {
                var policyEvent = new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = intakeStep.Id,
                    Type = "workspace.advisory-policy",
                    Message =
                        "Advisory execution uses the guarded source snapshot with repository hooks, source writes, shell access, and publication disabled.",
                    DataJson = JsonSerializer.Serialize(new
                    {
                        WorkspaceMode = WorkspaceMode.AdvisoryReadOnly.ToString(),
                        PermissionProfile =
                            ExecutionPermissionProfile.ReadOnlySource.ToString(),
                        HooksEnabled = false,
                        PublicationAllowed = false
                    })
                };
                flow.Events.Add(policyEvent);
                database.Entry(policyEvent).State = EntityState.Added;
            }
            flow.UpdatedAt = DateTimeOffset.UtcNow;
            flow.FailureReason = string.Empty;
            await database.SaveChangesAsync(cancellationToken);
            if (queuedEvent is not null &&
                queueReadyFlow &&
                !flowQueue.Queue(flow.Id))
            {
                throw new InvalidOperationException("Unable to queue the customer-confirmed flow.");
            }

            var detailFlow = await database.Flows
                .AsNoTracking()
                .AsSplitQuery()
                .Include(item => item.Steps)
                .ThenInclude(step => step.ToolCalls)
                .Include(item => item.Steps)
                .ThenInclude(step => step.RoutingDecisions)
                .ThenInclude(decision => decision.TaskProfile)
                .Include(item => item.Steps)
                .ThenInclude(step => step.RoutingDecisions)
                .ThenInclude(decision => decision.Alternatives)
                .Include(item => item.Messages)
                .ThenInclude(message => message.Attachments)
                .Include(item => item.Events)
                .Include(item => item.GateRecords)
                .SingleAsync(item => item.Id == flow.Id, cancellationToken);
            return new IntakeResponse(
                detailFlow.ToDetailDto(),
                response.Reply,
                response.Ready,
                ShouldSpeak: true);
        }
        catch (IntakeAttemptException) when (request.FlowId is null)
        {
            throw;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (request.FlowId is null)
        {
            var status = await PersistNewIntakeFailureAsync(
                flow.Id,
                message,
                exception,
                CancellationToken.None);
            throw new IntakeAttemptException(
                flow.Id,
                status,
                SafeIntakeRetryMessage,
                exception);
        }
    }

    private async Task<FlowStatus> PersistNewIntakeFailureAsync(
        Guid flowId,
        string input,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var flow = await database.Flows
            .Include(item => item.Steps)
            .Include(item => item.Events)
            .Include(item => item.AgentSnapshots)
            .SingleAsync(item => item.Id == flowId, cancellationToken);
        if (flow.Status != FlowStatus.Intake)
        {
            return flow.Status;
        }
        var step = flow.Steps
            .Where(item =>
                item.Iteration == flow.Iteration &&
                item.InvocationKind ==
                    ExecutionInvocationKind.Intake &&
                item.Status is
                    StepStatus.Pending or StepStatus.Running)
            .OrderByDescending(item => item.Sequence)
            .ThenByDescending(item => item.Attempt)
            .FirstOrDefault();
        step ??= flow.Steps
            .Where(item =>
                item.Iteration == flow.Iteration &&
                item.InvocationKind ==
                    ExecutionInvocationKind.Intake &&
                string.Equals(
                    item.InputSummary,
                    input,
                    StringComparison.Ordinal))
            .OrderByDescending(item => item.Sequence)
            .ThenByDescending(item => item.Attempt)
            .FirstOrDefault();
        if (step?.Status == StepStatus.Completed)
        {
            return flow.Status;
        }
        if (step is null)
        {
            var accountManager = flow.AgentSnapshots
                .Single(item =>
                    item.AgentId == "account-manager" &&
                    item.EnabledAtSnapshot);
            step = new FlowStep
            {
                FlowRunId = flow.Id,
                Iteration = flow.Iteration,
                Sequence = flow.Steps
                    .Where(item => item.Iteration == flow.Iteration)
                    .Select(item => item.Sequence)
                    .DefaultIfEmpty(-100)
                    .Max() + 1,
                AgentId = accountManager.AgentId,
                AgentName = accountManager.Name,
                AgentRole = accountManager.Role,
                Label = "Account Manager intake failed",
                PlanDutiesJson = """["Analyze"]""",
                PlanStage = PlanStage.BeforeReview,
                InvocationKind =
                    ExecutionInvocationKind.Intake,
                PermissionProfile =
                    ExecutionPermissionProfile.ReadOnlySource,
                WorkflowRevision =
                    workflowProvider.GetEffective().Revision,
                Status = StepStatus.Failed,
                Phase = AgentRunPhase.Failed,
                Attempt = 1,
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                InputSummary = input
            };
            step.StableSemanticRootId = step.Id;
            flow.Steps.Add(step);
        }
        else
        {
            step.Status = StepStatus.Failed;
            step.Phase = AgentRunPhase.Failed;
            step.CompletedAt = DateTimeOffset.UtcNow;
            step.Label = "Account Manager intake failed";
        }
        var reason = ClipFailureText(
            exception.GetBaseException().Message,
            MaximumIntakeFailureReasonCharacters);
        step.PushbackReason = reason;
        flow.FailureReason = reason;
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        if (!flow.Events.Any(item =>
                item.FlowStepId == step.Id &&
                item.Type == "intake.attempt-failed"))
        {
            flow.Events.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = step.Id,
                Type = "intake.attempt-failed",
                Message =
                    "The Account Manager intake setup failed after the new flow was saved and remains available for retry.",
                DataJson = JsonSerializer.Serialize(new
                {
                    FailureKind = "SetupFailure",
                    Reason = reason
                })
            });
        }
        await database.SaveChangesAsync(cancellationToken);
        return flow.Status;
    }

    private async Task FailIntakeAttemptAsync(
        HarnessDbContext database,
        FlowRun flow,
        FlowStep intakeStep,
        Exception exception,
        AgentExecutionResult? result,
        long durationMilliseconds,
        CancellationToken cancellationToken)
    {
        var failureKind = exception is AgentRunException runException
            ? runException.FailureKind
            : AgentRunFailureKind.InvalidOutput;
        var reason = ClipFailureText(
            exception.GetBaseException().Message,
            MaximumIntakeFailureReasonCharacters);
        intakeStep.Label = result is null
            ? "Account Manager intake failed"
            : "Account Manager intake contract rejected";
        intakeStep.Status = StepStatus.Failed;
        intakeStep.Phase = exception is AgentRunException
        {
            FailureKind: AgentRunFailureKind.BudgetExhausted
        }
            ? AgentRunPhase.BudgetExhausted
            : exception is AgentRunException
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
        intakeStep.ExecutionAttempts = Math.Max(
            intakeStep.ExecutionAttempts,
            result?.ExecutionAttempts ??
            (exception as AgentRunException)?.ExecutionAttempts ??
            1);
        intakeStep.DurationMilliseconds = Math.Max(
            1,
            durationMilliseconds);
        intakeStep.PushbackReason = reason;
        if (result is not null)
        {
            intakeStep.OutputSummary =
                WorkflowEngine.BoundFailedOutput(result.Output);
            foreach (var toolCall in result.ToolCalls)
            {
                var storedCall = new AgentToolCall
                {
                    FlowStepId = intakeStep.Id,
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
                };
                intakeStep.ToolCalls.Add(storedCall);
                database.Entry(storedCall).State = EntityState.Added;
            }
        }
        flow.FailureReason = reason;
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        if (!flow.Events.Any(item =>
                item.FlowStepId == intakeStep.Id &&
                item.Type == "intake.attempt-failed"))
        {
            var failureEvent = new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = intakeStep.Id,
                Type = "intake.attempt-failed",
                Message = result is null
                    ? "The Account Manager intake execution failed and remains available for retry."
                    : "The Account Manager output failed intake contract or lifecycle validation and remains available for retry.",
                DataJson = JsonSerializer.Serialize(new
                {
                    FailureKind = failureKind.ToString(),
                    Reason = reason,
                    OutputCharacters = result?.Output.Length ?? 0,
                    OutputSha256 = result is null
                        ? null
                        : OutcomeVerificationRules.ComputeSha256(
                            result.Output)
                })
            };
            flow.Events.Add(failureEvent);
            database.Entry(failureEvent).State = EntityState.Added;
        }
        await database.SaveChangesAsync(cancellationToken);
        await observationRecorder.RecordFailureAsync(
            intakeStep.Id,
            failureKind,
            intakeStep.DurationMilliseconds,
            Math.Max(1, intakeStep.ExecutionAttempts),
            cancellationToken);
    }

    private void BindIntakePermissionAtFirstLaunch(
        FlowRun flow,
        FlowStep step)
    {
        if (!string.IsNullOrWhiteSpace(step.EffectivePermissionJson))
        {
            return;
        }
        var workflow = workflowProvider.GetEffective();
        if (step.Status == StepStatus.Running &&
            !string.IsNullOrWhiteSpace(step.WorkflowRevision) &&
            !string.Equals(
                step.WorkflowRevision,
                workflow.Revision,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A started intake attempt has no persisted permission policy for its workflow revision.");
        }
        if (step.Status is not (
                StepStatus.Pending or StepStatus.Running))
        {
            throw new InvalidOperationException(
                "Intake permission can be bound only before an active attempt launches.");
        }
        var request = new PermissionResolutionRequest(
            flow.Kind,
            ExecutionInvocationKind.Intake,
            PlanStage.BeforeReview,
            ImmutableArray.Create(PlanDuty.Analyze),
            DurableReviewDecision: null,
            DurableApproval: false,
            IsOnlyPlannedPublishStep: false);
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

    internal static void ValidateIntakeCompletion(
        FlowRun flow,
        AccountManagerResponse response,
        OutcomeType configuredDeliveryOutcome)
    {
        if (flow.Status != FlowStatus.Intake)
        {
            throw new InvalidOperationException(
                "An intake result can be completed only while the flow remains in Intake.");
        }
        if ((response.Status is
                AccountManagerIntakeStatus.AwaitingConfirmation or
                AccountManagerIntakeStatus.Confirmed) &&
            response.FlowKind is null)
        {
            throw new InvalidOperationException(
                "An actionable intake result must include its flow kind.");
        }
        if (flow.LinkKind == FlowLinkKind.AdvisoryPromotion &&
            response.FlowKind is not (null or FlowKind.Delivery))
        {
            throw new InvalidOperationException(
                "An Advisory-promotion intake must remain a Delivery flow.");
        }
        if (response.Ready &&
            string.IsNullOrWhiteSpace(response.TaskBrief))
        {
            throw new InvalidOperationException(
                "A confirmed intake result must contain the reviewed task brief.");
        }
        if (response.FlowKind == FlowKind.Delivery)
        {
            _ = OutcomeTypeRules.RequireDelivery(
                configuredDeliveryOutcome,
                nameof(configuredDeliveryOutcome));
        }
        if (response.Ready &&
            !FlowLifecycleCoordinator.IsAllowed(
                flow.Status,
                FlowStatus.Queued))
        {
            throw new InvalidOperationException(
                "The confirmed intake cannot enter the execution queue from its current lifecycle state.");
        }
    }

    private static string ClipFailureText(string value, int maximum)
    {
        if (value.Length <= maximum)
        {
            return value;
        }
        var suffix =
            $" … [{OutcomeVerificationRules.ComputeSha256(value)}]";
        return value[..(maximum - suffix.Length)] + suffix;
    }

    public static AccountManagerResponse ParseResponse(string output)
    {
        var parsed = IntakeParser.Parse(output);
        var intakeStatus = parsed.Document.Status!.Value switch
        {
            IntakeStatus.NeedsClarification =>
                AccountManagerIntakeStatus.NeedsClarification,
            IntakeStatus.AwaitingConfirmation =>
                AccountManagerIntakeStatus.AwaitingConfirmation,
            IntakeStatus.Confirmed =>
                AccountManagerIntakeStatus.Confirmed,
            _ => throw new InvalidOperationException(
                "Copilot Account Manager returned an unsupported intake status.")
        };
        return new AccountManagerResponse(
            intakeStatus,
            parsed.Document.CustomerReply,
            parsed.Document.TaskTitle,
            intakeStatus == AccountManagerIntakeStatus.NeedsClarification
                ? string.Empty
                : parsed.NormalizedBriefJson,
            parsed.Document.FlowKind,
            parsed.Document.Brief,
            IntakeParser.Serialize(parsed.Document));
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

    private static AdvisoryPromotionSeed? TryAuthorizeFirstPromotionTurn(
        FlowRun flow,
        string message,
        PendingIntakeConfirmation? pendingConfirmation,
        string? linkedSeed,
        Guid? recoveringStepId = null)
    {
        if (flow.LinkKind != FlowLinkKind.AdvisoryPromotion ||
            flow.Kind != FlowKind.Delivery ||
            flow.Status != FlowStatus.Intake ||
            flow.ParentFlowRunId is null ||
            flow.ParentIteration is null or < 1 ||
            pendingConfirmation is not null ||
            linkedSeed is null ||
            !string.Equals(message, linkedSeed, StringComparison.Ordinal) ||
            flow.Steps.Any(step =>
                step.Id != recoveringStepId &&
                step.Iteration == flow.Iteration &&
                step.InvocationKind == ExecutionInvocationKind.Intake) ||
            flow.Events.Any(item =>
                item.Type.StartsWith("intake.", StringComparison.Ordinal)) ||
            flow.Messages.Count(item =>
                item.Role == ConversationRole.Customer) > 1 ||
            flow.Messages.Any(item =>
                item.Role == ConversationRole.Customer &&
                !string.Equals(
                    item.Content,
                    linkedSeed,
                    StringComparison.Ordinal)))
        {
            return null;
        }
        return AdvisoryPromotionSeedParser.Parse(linkedSeed);
    }

    internal static AccountManagerResponse ApplyPromotionConfirmationGate(
        AccountManagerResponse response,
        AdvisoryPromotionSeed seed,
        string durableTaskTitle)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (!response.Ready)
        {
            return response;
        }
        var brief = response.NormalizedBrief
                    ?? throw new InvalidOperationException(
                        "A directly confirmed Advisory promotion requires a complete intake brief.");
        var details = brief.Details ??
                      throw new InvalidOperationException(
                          "A directly confirmed Advisory promotion requires implementation details.");
        if (response.FlowKind != FlowKind.Delivery ||
            !string.Equals(
                AdvisoryPromotionSeedParser.NormalizeText(brief.Goal),
                seed.Goal,
                StringComparison.Ordinal) ||
            !details
                .Select(AdvisoryPromotionSeedParser.NormalizeText)
                .SequenceEqual(
                    seed.ImplementationDetails ?? [],
                    StringComparer.Ordinal) ||
            brief.SuccessCriteria is not { Count: 0 } ||
            brief.Constraints is not { Count: 0 } ||
            brief.Assumptions is not { Count: 0 })
        {
            throw new InvalidOperationException(
                "The directly confirmed promotion brief drifted from the durable accepted goal or implementation details.");
        }

        var confirmed = new IntakeDocument
        {
            Status = IntakeStatus.Confirmed,
            FlowKind = FlowKind.Delivery,
            TaskTitle = durableTaskTitle,
            CustomerReply = response.Reply,
            Brief = new IntakeBrief
            {
                Goal = seed.Goal,
                Details = seed.ImplementationDetails,
                SuccessCriteria = [],
                Constraints = [],
                Assumptions = []
            }
        };
        var normalized = IntakeParser.ParseJson(
            IntakeParser.Serialize(confirmed));
        return response with
        {
            TaskTitle = normalized.Document.TaskTitle,
            TaskBrief = normalized.NormalizedBriefJson,
            FlowKind = normalized.Document.FlowKind,
            NormalizedBrief = normalized.Document.Brief,
            RawContractJson = IntakeParser.Serialize(normalized.Document)
        };
    }

    private static void ValidateCanonicalLinkedStep(
        FlowRun flow,
        FlowStep step,
        FlowAgentSnapshot accountManager,
        string durableSeed)
    {
        if (step.FlowRunId != flow.Id ||
            step.Iteration != flow.Iteration ||
            !string.Equals(
                step.PlanStepKey,
                InitialLinkedIntakePlanStepKey,
                StringComparison.Ordinal) ||
            step.InvocationKind != ExecutionInvocationKind.Intake ||
            step.PlanStage != PlanStage.BeforeReview ||
            step.PermissionProfile !=
                ExecutionPermissionProfile.ReadOnlySource ||
            !string.Equals(
                step.AgentId,
                accountManager.AgentId,
                StringComparison.Ordinal) ||
            !string.Equals(
                step.AgentName,
                accountManager.Name,
                StringComparison.Ordinal) ||
            !string.Equals(
                step.AgentRole,
                accountManager.Role,
                StringComparison.Ordinal) ||
            !string.Equals(
                step.InputSummary,
                durableSeed,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The durable linked intake attempt does not match the canonical Account Manager step identity and seed.");
        }
    }

    private static void ValidateCanonicalOrdinaryStep(
        FlowRun flow,
        FlowStep step,
        FlowAgentSnapshot accountManager,
        string durableInput)
    {
        if (step.FlowRunId != flow.Id ||
            step.Iteration != flow.Iteration ||
            step.InvocationKind != ExecutionInvocationKind.Intake ||
            step.PlanStage != PlanStage.BeforeReview ||
            step.PermissionProfile !=
                ExecutionPermissionProfile.ReadOnlySource ||
            !string.Equals(
                step.AgentId,
                accountManager.AgentId,
                StringComparison.Ordinal) ||
            !string.Equals(
                step.AgentName,
                accountManager.Name,
                StringComparison.Ordinal) ||
            !string.Equals(
                step.AgentRole,
                accountManager.Role,
                StringComparison.Ordinal) ||
            !string.Equals(
                step.InputSummary,
                durableInput,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The durable intake attempt does not match its canonical Account Manager identity and input.");
        }
    }

    private static void ValidateLinkedJournalBinding(
        FlowRun flow,
        FlowAgentSnapshot accountManager,
        Guid expectedSessionId,
        CopilotSessionSnapshot journal)
    {
        if (journal.State == CopilotSessionJournalState.Missing)
        {
            return;
        }
        if (journal.SessionId != expectedSessionId ||
            !PathEquals(journal.WorkspacePath, flow.WorkspacePath) ||
            !string.Equals(
                journal.AgentName,
                accountManager.Name,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The linked intake journal is not bound to the canonical flow workspace, session, and Account Manager snapshot.");
        }
    }

    private static bool PathEquals(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) ||
            string.IsNullOrWhiteSpace(right))
        {
            return false;
        }
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private static void AddLinkedRecoveryEventOnce(
        FlowRun flow,
        FlowStep step,
        string type,
        string message)
    {
        if (flow.Events.Any(item =>
                item.FlowStepId == step.Id &&
                string.Equals(item.Type, type, StringComparison.Ordinal)))
        {
            return;
        }
        flow.Events.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = step.Id,
            Type = type,
            Message = message
        });
    }

    internal static AccountManagerResponse ApplyConfirmationGate(
        AccountManagerResponse response,
        IntakeDocument? pendingConfirmation)
    {
        if (!response.Ready)
        {
            return response;
        }
        if (pendingConfirmation?.Status != IntakeStatus.AwaitingConfirmation ||
            pendingConfirmation.FlowKind is null ||
            pendingConfirmation.Brief is null)
        {
            throw new InvalidOperationException(
                "Copilot Account Manager cannot confirm an intake brief that the customer has not reviewed.");
        }

        var confirmed = new IntakeDocument
        {
            Status = IntakeStatus.Confirmed,
            FlowKind = pendingConfirmation.FlowKind,
            TaskTitle = pendingConfirmation.TaskTitle,
            CustomerReply = response.Reply,
            Brief = pendingConfirmation.Brief
        };
        var normalized = IntakeParser.ParseJson(
            IntakeParser.Serialize(confirmed));
        return response with
        {
            TaskTitle = normalized.Document.TaskTitle,
            TaskBrief = normalized.NormalizedBriefJson,
            FlowKind = normalized.Document.FlowKind,
            NormalizedBrief = normalized.Document.Brief,
            RawContractJson = IntakeParser.Serialize(normalized.Document)
        };
    }

    internal static AccountManagerResponse RequireCustomerUploads(
        AccountManagerResponse response,
        IReadOnlyList<string> missingFiles)
    {
        if (missingFiles.Count == 0)
        {
            return response;
        }
        var brief = response.NormalizedBrief ?? new IntakeBrief
        {
            Details = [],
            SuccessCriteria = [],
            Constraints = [],
            Assumptions = []
        };
        var document = new IntakeDocument
        {
            Status = IntakeStatus.NeedsClarification,
            FlowKind = null,
            TaskTitle = response.TaskTitle,
            CustomerReply =
                $"Please attach {string.Join(", ", missingFiles.Take(8))} to the conversation. " +
                "It is outside the selected project; uploading the file lets the team use it " +
                "without accessing your other folders. Then I can confirm the brief.",
            Brief = brief
        };
        var normalized = IntakeParser.ParseJson(IntakeParser.Serialize(document));
        return response with
        {
            Status = AccountManagerIntakeStatus.NeedsClarification,
            Reply = normalized.Document.CustomerReply,
            TaskBrief = string.Empty,
            FlowKind = null,
            NormalizedBrief = normalized.Document.Brief,
            RawContractJson = IntakeParser.Serialize(normalized.Document)
        };
    }

    internal static FlowEvent? ApplyIntakeOutcome(
        FlowRun flow,
        AccountManagerResponse response,
        OutcomeType? configuredDeliveryOutcome = null,
        FlowLifecycleCoordinator? lifecycle = null)
    {
        flow.Title = response.TaskTitle;
        if (response.Status != AccountManagerIntakeStatus.NeedsClarification &&
            response.FlowKind is { } proposedKind)
        {
            flow.Kind = proposedKind;
            flow.Outcome = proposedKind == FlowKind.Advisory
                ? OutcomeType.None
                : OutcomeTypeRules.RequireDelivery(
                    configuredDeliveryOutcome ?? flow.Outcome,
                    nameof(configuredDeliveryOutcome));
        }
        if (!response.Ready)
        {
            return null;
        }

        (lifecycle ?? new FlowLifecycleCoordinator())
            .Transition(flow, FlowStatus.Queued);
        var queuedEvent = new FlowEvent
        {
            FlowRunId = flow.Id,
            Type = "flow.queued",
            Message = "Customer-confirmed brief entered the AI factory queue."
        };
        flow.Events.Add(queuedEvent);
        return queuedEvent;
    }

    internal static FlowRun CreateFlow(string message, HarnessSettings settings)
    {
        return
        new()
        {
            Title = BuildTitle(message),
            OriginalRequest = message,
            ConsolidatedRequest = message,
            RepositoryPath = settings.RepositoryPath,
            RepositoryKnowledge = settings.RepositoryKnowledge,
            Outcome = ResolveDeliveryOutcome(settings),
            ModelSelectionStrategy = settings.ModelSelectionStrategy
        };
    }

    private static OutcomeType ResolveDeliveryOutcome(HarnessSettings settings) =>
        Directory.Exists(settings.RepositoryPath) &&
        RepositoryAnalyzer.FindGitRepositories(settings.RepositoryPath).Count == 0 &&
        RepositoryAnalyzer.FindContainingGitRepository(settings.RepositoryPath) is null
            ? OutcomeType.Commit
            : OutcomeTypeRules.RequireDelivery(
                settings.Outcome,
                nameof(settings.Outcome));

    private static async Task<FlowRun> LoadFlowAsync(
        HarnessDbContext database,
        Guid flowId,
        CancellationToken cancellationToken) =>
        await database.Flows
            .AsSplitQuery()
            .Include(item => item.Messages)
            .ThenInclude(message => message.Attachments)
            .Include(item => item.Steps)
            .ThenInclude(step => step.ToolCalls)
            .Include(item => item.Events)
            .Include(item => item.GateRecords)
            .Include(item => item.AgentSnapshots)
            .Include(item => item.TaskProfiles)
            .SingleOrDefaultAsync(item => item.Id == flowId, cancellationToken)
        ?? throw new KeyNotFoundException($"Factory flow '{flowId}' was not found.");

    private static string FormatCustomerInputs(IReadOnlyList<string> customerMessages) =>
        string.Join(
            Environment.NewLine,
            customerMessages.Select((item, index) => $"Customer input {index + 1}: {item}"));

    /// <summary>
    /// Intake event types that represent an actual, successfully completed Account
    /// Manager proposal turn (clarification, confirmation request, or confirmation).
    /// Operational noise -- failed attempts, retry starts, and recovery/progress
    /// reconciliation events -- must never shadow the latest real proposal, so
    /// <see cref="GetPendingConfirmation" /> only ever considers these types.
    /// </summary>
    private static readonly ImmutableHashSet<string> SubstantiveIntakeProposalEventTypes =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "intake.clarification",
            "intake.confirmation_requested",
            "intake.confirmed",
            "intake.ready");

    private static PendingIntakeConfirmation? GetPendingConfirmation(FlowRun flow)
    {
        var latestIntakeEvent = flow.Events
            .Where(item =>
                SubstantiveIntakeProposalEventTypes.Contains(item.Type))
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefault();
        if (latestIntakeEvent?.Type is not (
                "intake.confirmation_requested" or "intake.ready"))
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(latestIntakeEvent.DataJson))
        {
            throw new InvalidOperationException(
                "The pending intake confirmation has no durable normalized proposal.");
        }
        var parsed = IntakeParser.ParseJson(latestIntakeEvent.DataJson);
        if (parsed.Document.Status != IntakeStatus.AwaitingConfirmation)
        {
            throw new InvalidOperationException(
                "The pending intake proposal is not awaiting confirmation.");
        }
        return new PendingIntakeConfirmation(
            parsed.Document,
            parsed.NormalizedBriefJson);
    }

    internal static string BuildDialogueTask(
        IEnumerable<FlowMessage> messages,
        OutcomeType outcome,
        string? pendingConfirmationBrief = null,
        FlowKind? pendingFlowKind = null,
        AdvisoryPromotionSeed? promotionSeed = null)
    {
        var orderedMessages = messages
            .OrderBy(item => item.CreatedAt)
            .ToList();
        var dialogue = promotionSeed is null
            ? string.Join(
                Environment.NewLine,
                orderedMessages.Select(item =>
                    $"{item.Role}: {item.Content}" +
                    (item.Attachments.Count == 0
                        ? string.Empty
                        : Environment.NewLine +
                          "Customer-uploaded files: " +
                          string.Join(", ", item.Attachments
                              .Select(attachment => attachment.FileName)))))
            : "Customer: The customer accepted the Advisory result and explicitly requested " +
              "the linked Delivery represented by the host-controlled promotion context.";
        var confirmationPolicy = promotionSeed is not null
            ? "DURABLE_ADVISORY_PROMOTION_AUTHORIZATION: the customer already accepted the " +
              "Advisory result and explicitly requested this linked Delivery. You may return " +
              "Confirmed on this first turn only when FlowKind is Delivery, Brief.Goal exactly " +
              "matches the durable goal, Brief.Details contains exactly the durable implementation " +
              "details, and every other Brief list is empty. Otherwise return AwaitingConfirmation. "
            : string.IsNullOrWhiteSpace(pendingConfirmationBrief)
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
              $"UNCONFIRMED_FLOW_KIND: {pendingFlowKind}{Environment.NewLine}" +
              "UNCONFIRMED_NORMALIZED_BRIEF:" +
              Environment.NewLine +
              pendingConfirmationBrief.Trim();
        return
            "Classify the repository-grounded customer intent as Advisory or Delivery. " +
            "Advisory means inspect, recommend, or explain without source changes or publication. " +
            "Delivery means the customer is asking the team to implement or change the product. " +
            "Describe customer outcomes, not tools or implementation mechanics. " +
            "Use only supplied facts needed for the requested outcome in the brief. The full " +
            "input remains available to downstream agents; do not copy unrelated fields into " +
            "requirements or treat a restated brief as authorization to disclose sensitive " +
            "information. " +
            "Default to AwaitingConfirmation once meaningful work can begin; downstream details " +
            "do not need to be settled during intake. Treat all prior answers as settled and do " +
            "not ask for the same detail twice. Ask at most one focused clarification question. " +
            (promotionSeed is null
                ? "No proposed brief is customer-approved merely because it is clear. "
                : "Only the host-marked durable Advisory promotion is already customer-authorized. ") +
            confirmationPolicy
                .Replace("CONFIRMED", "Confirmed", StringComparison.Ordinal)
                .Replace("AWAITING_CONFIRMATION", "AwaitingConfirmation", StringComparison.Ordinal) +
            "A correction is not confirmation. A confirmation must preserve the exact pending " +
            "FlowKind and normalized Brief; never silently change either during confirmation. " +
            $"The configured Delivery packaging preference is {outcome}; do not discuss it with " +
            "the customer, and do not apply it to Advisory work. " +
            pendingBriefContext +
            Environment.NewLine +
            Environment.NewLine +
            dialogue;
    }

    private sealed record PendingIntakeConfirmation(
        IntakeDocument? Document,
        string NormalizedBriefJson);

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
        var update = AgentProgressPersistence.Apply(step, progress);
        if (update.PhaseChanged)
        {
            _logger.LogInformation(
                "Intake step {StepId} for flow {FlowId} entered {Phase}; session {CopilotSessionId}. {Activity}",
                stepId,
                flowId,
                progress.Phase,
                progress.CopilotSessionId,
                progress.Activity);
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
    string TaskTitle,
    string TaskBrief,
    FlowKind? FlowKind = null,
    IntakeBrief? NormalizedBrief = null,
    string RawContractJson = "")
{
    public bool Ready => Status == AccountManagerIntakeStatus.Confirmed;

    public bool AwaitingConfirmation =>
        Status == AccountManagerIntakeStatus.AwaitingConfirmation;
}

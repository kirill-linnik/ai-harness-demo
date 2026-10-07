using System.Text.Json;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Security;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Data;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

internal sealed record QaOwnerRepairProposal(
    Guid SourceStepId,
    string AcceptancePlanHash,
    DeliveryAcceptancePlan AcceptancePlan,
    IReadOnlyList<DeliveryQaCriterionDocument> Criteria);

internal sealed class HostHandoffRepairRequiredException(
    Guid stepId, string message, string? ownerPlanStepKey = null,
    QaOwnerRepairProposal? proposal = null, bool validatedReport = false)
    : InvalidOperationException(message)
{
    public Guid StepId { get; } = stepId;

    public string? OwnerPlanStepKey { get; } = ownerPlanStepKey;

    public QaOwnerRepairProposal? Proposal { get; } = proposal;
    public bool ValidatedReport { get; } = validatedReport;
}

internal sealed class OwnerRepairBlockedException(Guid stepId, string message)
    : InvalidOperationException(message)
{
    public Guid StepId { get; } = stepId;
}

public sealed partial class WorkflowEngine
{
    internal const string AutomaticContinuationEventType = "agent.continuation-scheduled";
    internal const string RecoveryExhaustedEventType = "agent.recovery-exhausted";
    internal const string HostRepairEventType = "handoff.host-repair-required";
    internal const string RemainingWorkAssignmentEventType = "flow.remaining-work-assignment";
    internal const string OwnerRepairProposalEventType = "handoff.owner-repair-proposal-bound";
    internal const string OwnerRepairBlockedEventType = "handoff.owner-repair-blocked";

    private static async Task<string?> GetQaReworkTargetErrorAsync(
        HarnessDbContext database, FlowRun flow, FlowStep step, string output,
        CancellationToken cancellationToken)
    {
        DynamicHandoffStatus handoff;
        try
        {
            handoff = AgentHandoffInspector.ParseDynamic(output);
            if (!handoff.IsPushback)
            {
                return null;
            }
            await ValidateDynamicPushbackTargetAsync(database, flow, step, handoff, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message;
        }
        var owner = await FindQaImplementationOwnerAsync(database, flow, step, cancellationToken);
        return owner is not null && handoff.OwnerPlanStepKey == owner.PlanStepKey
            ? null
            : $"QA rework must name the accepted implementation-owner plan step '{owner?.PlanStepKey}', " +
              "not a different or inferred owner.";
    }

    private static async Task<(AgentExecutionResult Result, string? Error)> PrepareQaAssessmentAsync(
        HarnessDbContext database, FlowRun flow, FlowStep step, AgentExecutionResult result,
        CancellationToken cancellationToken)
    {
        var error = GetDeliveryQaCorrectionReason(flow, step, result);
        if (error is not null || !AgentHandoffInspector.ParseDynamic(result.Output).IsPushback)
        {
            return (result, error);
        }
        error = await GetQaReworkTargetErrorAsync(database, flow, step, result.Output, cancellationToken);
        if (error is not null)
        {
            return (result, error);
        }
        var (acceptance, hash, _) = DeliveryReadinessService.TryReadAcceptancePlan(flow);
        var assessment = DeliveryReadinessPolicy.ParseQaOutput(result.Output,
            acceptance ?? throw new InvalidOperationException("The validated QA acceptance plan is missing."), hash);
        if (!assessment.Document.Criteria!.Any(item =>
            item.Outcome is DeliveryCriterionOutcome.Failed or DeliveryCriterionOutcome.Blocked))
        {
            return (result, "A QA rework request must identify the actual Failed or Blocked criteria " +
                "and concrete owner remediation. If every criterion is Verified, return the completed " +
                "assessment and disclose any external risk honestly instead of inventing a defect.");
        }
        var normalized = AgentHandoffInspector.CompleteQaAssessment(result.Output);
        var data = JsonSerializer.Serialize(new { OriginalOutput = result.Output, NormalizedOutput = normalized });
        var existing = await database.FlowEvents.SingleOrDefaultAsync(item =>
            item.FlowStepId == step.Id && item.Type == "handoff.qa-rework-assessment-recorded", cancellationToken);
        if (existing is not null && existing.DataJson != data)
        {
            throw new InvalidOperationException("The recovered QA rework assessment differs from its durable normalization.");
        }
        if (existing is null)
        {
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id, FlowStepId = step.Id, Type = "handoff.qa-rework-assessment-recorded",
                Message = "Recorded QA's implementation rework request as a completed negative assessment. " +
                    "The host will route owner repair or bounded replanning; no approval is authorized.",
                DataJson = data
            });
        }
        return (result with { Output = normalized }, null);
    }

    private static async Task<FlowStep?> FindQaImplementationOwnerAsync(
        HarnessDbContext database, FlowRun flow, FlowStep verification, CancellationToken cancellationToken)
    {
        var planJson = await database.FlowPlanDocuments
            .Where(item => item.FlowRunId == flow.Id && item.Iteration == verification.Iteration)
            .Select(item => item.RawJson).SingleOrDefaultAsync(cancellationToken);
        if (planJson is null)
        {
            return null;
        }
        var plan = AiHarnessDemo.Core.Orchestration.TeamPlanParser.ParseJson(planJson).Document;
        var owner = plan.Steps!
            .Where(item => item.Duties!.Contains(PlanDuty.Implement) &&
                AiHarnessDemo.Core.Orchestration.TeamPlanValidator.IsDependencyAncestor(
                    plan, item.Id, verification.PlanStepKey))
            .OrderByDescending(item => item.Order).FirstOrDefault();
        return owner is null ? null : await database.FlowSteps.AsNoTracking()
            .Where(item => item.FlowRunId == flow.Id && item.Iteration == verification.Iteration &&
                item.PlanStepKey == owner.Id && item.Status == StepStatus.Completed)
            .OrderByDescending(item => item.Sequence).FirstOrDefaultAsync(cancellationToken);
    }

    private static async Task<string> DescribeQaImplementationOwnerAsync(
        HarnessDbContext database, FlowRun flow, FlowStep verification, CancellationToken cancellationToken)
    {
        var owner = await FindQaImplementationOwnerAsync(database, flow, verification, cancellationToken);
        return owner is null ? string.Empty :
            $"{owner.AgentName} (agent ID: {owner.AgentId}; role: {owner.AgentRole}; plan-step ID: {owner.PlanStepKey})";
    }

    private static bool IsActionableOwnerCriterion(
        DeliveryCriterionOutcome outcome, string? remediation, IEnumerable<string>? responsibleRoles,
        FlowStep owner) =>
        outcome is DeliveryCriterionOutcome.Failed or DeliveryCriterionOutcome.Blocked &&
        !string.IsNullOrWhiteSpace(remediation) &&
        responsibleRoles?.Any(role =>
            role == owner.AgentRole || role == owner.AgentId || role == owner.AgentName) == true;

    private static async Task<bool> HasOwnerActionableBlockedCriteriaAsync(
        HarnessDbContext database, FlowRun flow, DeliveryReadinessBinding readiness,
        CancellationToken cancellationToken)
    {
        var steps = await database.FlowSteps.AsNoTracking()
            .Where(item => item.FlowRunId == flow.Id && item.Iteration == flow.Iteration &&
                item.Status == StepStatus.Completed).ToListAsync(cancellationToken);
        var owners = steps.Where(item => ReadPlanDuties(item.PlanDutiesJson).Contains(PlanDuty.Implement));
        return readiness.Contract.Criteria.Any(criterion =>
            criterion.Outcome == DeliveryCriterionOutcome.Blocked &&
            owners.Any(owner => IsActionableOwnerCriterion(criterion.Outcome, criterion.Remediation,
                criterion.ResponsibleRoles, owner)));
    }

    internal static bool RequiresOwnerRepairStatus(AgentExecutionContext context) =>
        context.ContextDocuments?.Any(item => item.Name == "qa-repair-proposal.json") == true;

    internal static string? ReadOwnerRepairStatus(string output)
    {
        var lines = output.Split('\n').Select(line => line.TrimEnd('\r'))
            .Where(line => line.TrimStart().StartsWith("REPAIR_STATUS:", StringComparison.Ordinal)).ToArray();
        return lines.Length == 1 && lines[0] is
            "REPAIR_STATUS: REPAIRED" or "REPAIR_STATUS: NO_CHANGE_NEEDED" or "REPAIR_STATUS: BLOCKED"
            ? lines[0]["REPAIR_STATUS: ".Length..]
            : null;
    }

    private async Task RequireOwnerRepairCapabilitiesAsync(
        AgentExecutionContext context, CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var step = await database.FlowSteps.AsNoTracking().SingleAsync(item =>
            item.Id == context.FlowStepId && item.FlowRunId == context.FlowId &&
            item.Iteration == context.Iteration, cancellationToken);
        var permission = JsonSerializer.Deserialize<EffectiveExecutionPermission>(
            step.EffectivePermissionJson)
            ?? throw new InvalidOperationException("The owner repair permission document is empty.");
        var canEdit = !permission.DeniedTools.Contains("write") &&
            permission.AllowedTools.Any(tool =>
                tool is "edit" or "create" && !permission.DeniedTools.Contains(tool));
        var canUseShell = !permission.DeniedTools.Contains("shell") &&
            permission.AllowedTools.Any(tool =>
                tool is "powershell" or "bash" && !permission.DeniedTools.Contains(tool));
        if (!canEdit && !canUseShell)
        {
            throw new OwnerRepairBlockedException(context.FlowStepId,
                "The scoped implementation repair has no authorized editing capability under the " +
                "original task ceiling and current policy. The host did not dispatch an investigation " +
                "as a repair or broaden permissions.");
        }
    }

    private async Task BlockOwnerRepairAsync(
        Guid flowId, OwnerRepairBlockedException failure, CancellationToken cancellationToken)
    {
        await using var lease = await _lifecycle.EnterAsync(flowId, cancellationToken);
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var flow = await database.Flows.SingleAsync(item => item.Id == flowId, cancellationToken);
        if (flow.Status is not (FlowStatus.Running or FlowStatus.Reworking))
        {
            logger.LogInformation(
                "Preserved flow {FlowId} in {Status} instead of applying a stale owner-repair blocker.",
                flowId, flow.Status);
            return;
        }
        _lifecycle.Transition(flow, FlowStatus.Blocked);
        flow.CurrentBlockerCode = OwnerRepairBlockedEventType;
        flow.CurrentBlockerSummary = failure.Message;
        flow.CurrentBlockerDataJson = null;
        flow.CustomerBlockerMessage = failure.Message;
        flow.OutcomeLabel = "Implementation repair blocked";
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flowId, FlowStepId = failure.StepId, Type = OwnerRepairBlockedEventType,
            Message = failure.Message
        });
        await database.SaveChangesAsync(cancellationToken);
    }

    private static async Task<bool> HasFocusedQaRepairAvailableAsync(
        HarnessDbContext database, FlowRun flow, FlowStep step, CancellationToken cancellationToken)
    {
        var root = step.PushbackRootStepId ?? step.Id;
        var count = await database.FlowSteps.CountAsync(item =>
            item.FlowRunId == flow.Id && item.Iteration == flow.Iteration &&
            item.PlanStepKey == step.PlanStepKey && item.Status == StepStatus.Pushback &&
            (item.Id == root || item.PushbackRootStepId == root), cancellationToken);
        var limit = await database.Settings.Select(item => item.MaxHandoffRetries)
            .SingleAsync(cancellationToken);
        if (limit is < 0 or > 10)
        {
            throw new InvalidOperationException("The configured handoff retry limit is outside 0-10.");
        }
        return HasHandoffRetryAvailable(count + 1, limit);
    }

    internal async Task<FlowRun> ContinueRemainingWorkAsync(
        Guid flowId, string assignment, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(assignment) || assignment.Length > 2_000)
        {
            throw new ArgumentException("A remaining-work assignment of 1-2000 characters is required.", nameof(assignment));
        }
        await _manualRestartGate.WaitAsync(cancellationToken);
        try
        {
            await using var lease = await _lifecycle.EnterAsync(flowId, cancellationToken);
            await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
            var flow = await database.Flows.AsSplitQuery()
                .Include(item => item.Steps).Include(item => item.Events)
                .Include(item => item.GateRecords).Include(item => item.AgentSnapshots)
                .Include(item => item.Messages)
                .SingleAsync(item => item.Id == flowId, cancellationToken);
            var source = FindUnresolvedFailure(flow.Steps.Where(item => item.Iteration == flow.Iteration).ToArray());
            if (flow.Status != FlowStatus.Failed || source is null ||
                source.Phase != AgentRunPhase.BudgetExhausted ||
                source.PlanStage != PlanStage.BeforeReview ||
                source.InvocationKind != ExecutionInvocationKind.Worker ||
                source.RemotePublicationAllowed || IsPreMortemStep(source) ||
                source.PreMortemReviewStepId is not null ||
                ReviewCoordinator.HasAcceptedCustomerReview(flow))
            {
                throw new FlowLifecycleException(flow.Id, flow.Status, FlowStatus.Queued,
                    "Scoped remaining work requires an exhausted pre-review worker and no accepted customer review.");
            }
            var root = GetStableSemanticRootId(source);
            var failureWitness = flow.Events.Where(item => item.FlowStepId == source.Id &&
                    item.Type == "step.failed").OrderByDescending(item => item.CreatedAt).FirstOrDefault();
            if (failureWitness?.DataJson is { } witnessJson)
            {
                using var witness = JsonDocument.Parse(witnessJson);
                if (witness.RootElement.TryGetProperty("ProcessTerminationUnconfirmed", out var unconfirmed) &&
                    unconfirmed.GetBoolean())
                {
                    throw new FlowLifecycleException(flow.Id, flow.Status, FlowStatus.Queued,
                        "The previous process termination remains unconfirmed.");
                }
            }
            var oldBudget = await database.AgentExecutionBudgets.SingleAsync(item =>
                item.RootStepId == (source.ExecutionBudgetRootId ?? root) &&
                item.AgentId == source.AgentId && item.FlowRunId == flow.Id, cancellationToken);
            if (oldBudget.DeadlineAt > DateTimeOffset.UtcNow)
            {
                throw new FlowLifecycleException(flow.Id, flow.Status, FlowStatus.Queued,
                    "The prior assignment has not exhausted its recorded deadline.");
            }
            var scope = assignment.Trim();
            var scopeHash = OutcomeVerificationRules.ComputeSha256(scope);
            var prior = flow.Events.Where(item => item.Type == RemainingWorkAssignmentEventType)
                .Select(item => JsonSerializer.Deserialize<RemainingWorkAssignment>(item.DataJson!) ??
                    throw new InvalidOperationException("The remaining-work assignment ledger is empty."))
                .Where(item => item.SemanticRootId == root).ToArray();
            var limit = await GetMaxHandoffRetriesAsync(cancellationToken);
            if (prior.Length >= limit || prior.Any(item => item.ScopeHash == scopeHash) ||
                scope == source.InputSummary.Trim())
            {
                throw new FlowLifecycleException(flow.Id, flow.Status, FlowStatus.Queued,
                    "The scoped assignment limit is exhausted or this scope has already been assigned.");
            }
            if (source.CopilotSessionId is { } sessionId)
            {
                var journal = await sessionJournal.InspectAsync(
                    string.IsNullOrWhiteSpace(source.CopilotSessionHome)
                        ? sessionJournal.ExpectedHome() : source.CopilotSessionHome,
                    sessionId, cancellationToken);
                if (journal.State == CopilotSessionJournalState.Active &&
                    !sessionJournal.TryStopActiveSession(journal))
                {
                    throw new FlowLifecycleException(flow.Id, flow.Status, FlowStatus.Queued,
                        "The previous Copilot process has not been confirmed stopped.");
                }
            }
            var workflow = workflowProvider.GetValidated();
            var original = AssignmentExecutionBudget.ReadPolicy(oldBudget);
            var current = AssignmentExecutionBudget.Policy(workflow.Config.Copilot, workflow.Revision);
            var budgetMs = Math.Min(45 * 60 * 1_000, Math.Min(original.ExecutionBudgetMs, current.ExecutionBudgetMs));
            var policy = new AgentExecutionPolicy(
                Math.Min(original.InactivityTimeoutMs, current.InactivityTimeoutMs),
                Math.Min(original.SilentToolTimeoutMs, current.SilentToolTimeoutMs),
                Math.Min(budgetMs / 2, Math.Min(original.SoftWarningMs, current.SoftWarningMs)),
                budgetMs, workflow.Revision);
            var next = CreateContinuationStep(flow, source, $"Scoped remaining work for {source.AgentName}",
                "This is a distinct, operator-scoped remaining-work assignment, not a restart of the exhausted task. " +
                "Inspect preserved work and existing reports first. Do not repeat completed work or expand the confirmed outcome. " +
                "Complete only the following scope and return the normal complete role handoff. Independent verification and " +
                "customer approval remain mandatory; do not claim checks that have not passed.\n\n" + scope);
            foreach (var later in flow.Steps.Where(item =>
                item.Iteration == source.Iteration && item.Sequence > source.Sequence))
            {
                later.Sequence += 10;
                if (later.Status == StepStatus.Skipped)
                {
                    ResetSkippedStep(later);
                }
            }
            next.ExecutionBudgetRootId = next.Id;
            next.ExecutionPolicyJson = JsonSerializer.Serialize(policy);
            next.CopilotSessionId = AgentSessionIdentity.Create(
                flow.Id, flow.Iteration, $"{source.AgentId}:remaining:{next.Id:N}", source.PlanStepKey);
            next.CopilotSessionHome = sessionJournal.ExpectedHome();
            PreserveOrTightenRetryPermission(flow, source, next);
            flow.Steps.Add(next);
            database.Entry(next).State = EntityState.Added;
            database.TaskProfiles.Add(TaskProfileRules.CopyForStep(
                await database.TaskProfiles.SingleAsync(item => item.FlowStepId == source.Id, cancellationToken), next.Id));
            var now = DateTimeOffset.UtcNow;
            database.AgentExecutionBudgets.Add(new AgentExecutionBudget
            {
                FlowRunId = flow.Id, RootStepId = next.Id, AgentId = next.AgentId,
                StartedAt = now, DeadlineAt = now.AddMilliseconds(budgetMs),
                PolicyJson = next.ExecutionPolicyJson
            });
            foreach (var dependent in flow.Steps.Where(item =>
                item.Id != next.Id && item.Status == StepStatus.Pending && item.DependsOnStepId == source.Id))
            {
                dependent.DependsOnStepId = next.Id;
            }
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id, FlowStepId = next.Id, Type = RemainingWorkAssignmentEventType,
                Message = "An operator scoped distinct remaining work with a finite, non-escalating budget. " +
                    "The exhausted attempt, its deadline, and all preserved work remain unchanged.",
                DataJson = JsonSerializer.Serialize(new RemainingWorkAssignment(
                    root, source.Id, next.Id, scope, scopeHash))
            });
            if (flow.Kind == FlowKind.Delivery && ReadPlanDuties(next.PlanDutiesJson).Contains(PlanDuty.Implement))
            {
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id, FlowStepId = next.Id,
                    Type = DeliveryReadinessService.EvidenceEpochEventType,
                    Message = "Invalidated earlier verification evidence before scoped completion of preserved implementation.",
                    DataJson = DeliveryReadinessService.SerializeEvidenceEpoch(
                        new DeliveryEvidenceEpoch(flow.Iteration, next.Id, next.Sequence))
                });
            }
            _lifecycle.Transition(flow, FlowStatus.Queued);
            flow.FailureReason = string.Empty;
            flow.CompletedAt = null;
            flow.UpdatedAt = now;
            await database.SaveChangesAsync(cancellationToken);
            return flow;
        }
        finally
        {
            _manualRestartGate.Release();
        }
    }

    private sealed record RemainingWorkAssignment(
        Guid SemanticRootId, Guid SourceStepId, Guid AssignmentStepId, string Scope, string ScopeHash);

    internal sealed record AutomaticContinuation(
        Guid SourceStepId,
        Guid RetryStepId,
        Guid BudgetRootId,
        string FailureFingerprint,
        int RecoveryAttempt,
        DateTimeOffset DueAt);

    internal async Task<FlowStep> ExecuteStepAsync(
        Guid flowId,
        Guid stepId,
        string workspacePath,
        string planSummary,
        int complexity,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await WaitForAutomaticContinuationAsync(stepId, cancellationToken);
            try
            {
                return await ExecuteStepAttemptAsync(
                    flowId, stepId, workspacePath, planSummary, complexity, cancellationToken);
            }
            catch (AgentRunException exception) when (!cancellationToken.IsCancellationRequested)
            {
                var continuation = await TryScheduleAutomaticContinuationAsync(
                    flowId, stepId, exception, cancellationToken);
                if (continuation is null)
                {
                    throw;
                }
                stepId = continuation.RetryStepId;
            }
        }
    }

    internal static bool CanAutomaticallyContinue(AgentRunException failure) =>
        !failure.ProcessTerminationUnconfirmed &&
        (failure.FailureKind == AgentRunFailureKind.Transient ||
         failure.CanResumeSession &&
         failure.FailureKind is AgentRunFailureKind.TimedOut or AgentRunFailureKind.Stalled);

    internal async Task<AutomaticContinuation?> TryScheduleAutomaticContinuationAsync(
        Guid flowId,
        Guid sourceId,
        AgentRunException failure,
        CancellationToken cancellationToken)
    {
        if (!CanAutomaticallyContinue(failure))
        {
            return null;
        }
        await using var lease = await _lifecycle.EnterAsync(flowId, cancellationToken);
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var flow = await database.Flows
            .AsSplitQuery()
            .Include(item => item.Steps)
            .ThenInclude(step => step.ToolCalls)
            .Include(item => item.Events)
            .SingleAsync(item => item.Id == flowId, cancellationToken);
        if (flow.Status is not (FlowStatus.Running or FlowStatus.Reworking))
        {
            return null;
        }
        var source = flow.Steps.Single(item => item.Id == sourceId);
        if (source.Status != StepStatus.Failed || source.StartedAt is null ||
            source.PlanStage != PlanStage.BeforeReview ||
            source.InvocationKind == ExecutionInvocationKind.Publication ||
            source.RemotePublicationAllowed ||
            (failure.FailureKind is AgentRunFailureKind.TimedOut or AgentRunFailureKind.Stalled &&
             source.CopilotSessionId is null) ||
            source.Label.StartsWith(StudioContractCorrectionLabelPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var prior = flow.Events.Where(item => item.Type == AutomaticContinuationEventType)
            .Select(item => JsonSerializer.Deserialize<AutomaticContinuation>(item.DataJson!)
                ?? throw new InvalidOperationException("The persisted continuation is empty."))
            .ToArray();
        if (prior.SingleOrDefault(item => item.SourceStepId == source.Id) is { } existing)
        {
            return existing;
        }
        var root = source.ExecutionBudgetRootId ?? GetStableSemanticRootId(source);
        var attempts = prior.Where(item => item.BudgetRootId == root).ToArray();
        var limit = await GetMaxHandoffRetriesAsync(cancellationToken);
        var config = workflowProvider.GetValidated();
        var policy = string.IsNullOrWhiteSpace(source.ExecutionPolicyJson)
            ? AssignmentExecutionBudget.Policy(config.Config.Copilot, source.WorkflowRevision)
            : JsonSerializer.Deserialize<AgentExecutionPolicy>(source.ExecutionPolicyJson)
                ?? throw new InvalidOperationException("The persisted execution policy is empty.");
        var budget = await database.AgentExecutionBudgets.SingleOrDefaultAsync(
            item => item.RootStepId == root && item.AgentId == source.AgentId, cancellationToken);
        if (budget is null)
        {
            // Real CLI launches already bind this row; this also covers failures before launch.
            var started = flow.Steps
                .Where(item => item.AgentId == source.AgentId &&
                    (item.ExecutionBudgetRootId ?? GetStableSemanticRootId(item)) == root &&
                    item.StartedAt is not null)
                .Min(item => item.StartedAt!.Value);
            budget = new AgentExecutionBudget
            {
                FlowRunId = flow.Id,
                RootStepId = root,
                AgentId = source.AgentId,
                StartedAt = started,
                DeadlineAt = started.AddMilliseconds(policy.ExecutionBudgetMs),
                PolicyJson = JsonSerializer.Serialize(policy)
            };
            database.AgentExecutionBudgets.Add(budget);
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = source.Id,
                Type = "agent.execution-budget-bound",
                Message = $"Continuation bound the original assignment deadline {budget.DeadlineAt:O} " +
                          "to its earliest persisted start; no additional time was granted."
            });
        }
        if (budget.FlowRunId != flow.Id)
        {
            throw new InvalidOperationException("The continuation budget belongs to another flow.");
        }
        source.ExecutionBudgetRootId = root;
        source.ExecutionPolicyJson = budget.PolicyJson;
        var fingerprint = OutcomeVerificationRules.ComputeSha256(JsonSerializer.Serialize(new
        {
            failure.FailureKind,
            failure.Message,
            source.OutputSummary,
            Evidence = source.ToolCalls.Where(call => call.Succeeded)
                .Select(call => new
                {
                    call.ToolName, call.NormalizedCommand, call.NormalizedArguments,
                    call.WorkingDirectory, call.ResultDigest, call.ResultSummary
                })
                .OrderBy(call => call.ToolName, StringComparer.Ordinal)
                .ThenBy(call => call.NormalizedCommand, StringComparer.Ordinal)
                .ThenBy(call => call.NormalizedArguments, StringComparer.Ordinal)
                .ThenBy(call => call.WorkingDirectory, StringComparer.Ordinal)
                .ThenBy(call => call.ResultDigest, StringComparer.Ordinal)
                .ThenBy(call => call.ResultSummary, StringComparer.Ordinal)
                .ToArray()
        }));
        var recoveryAttempt = attempts.Length + 1;
        var delayMs = Math.Min(
            config.Config.Agent.RetryBaseDelayMs * Math.Pow(2, attempts.Length),
            config.Config.Agent.MaxRetryBackoffMs);
        var dueAt = DateTimeOffset.UtcNow.AddMilliseconds(delayMs);
        var reason = recoveryAttempt > limit
            ? "The configured automatic continuation limit is exhausted."
            : attempts.Any(item => item.FailureFingerprint == fingerprint)
                ? "The same failure repeated without new host-observed evidence."
                : dueAt >= budget.DeadlineAt
                    ? AssignmentExecutionBudget.ExhaustedReason
                    : null;
        if (reason is not null)
        {
            flow.CurrentBlockerCode = "factory.recovery-exhausted";
            flow.CurrentBlockerSummary = reason;
            flow.CurrentBlockerDataJson = JsonSerializer.Serialize(
                new { BudgetRootId = root, RecoveryAttempt = recoveryAttempt, Limit = limit });
            flow.CustomerBlockerMessage =
                "Your work is preserved, but the factory could not finish within its safe " +
                "recovery limits. The remaining work needs a scoped repair before it can be reviewed.";
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = source.Id,
                Type = RecoveryExhaustedEventType,
                Message = reason,
                DataJson = JsonSerializer.Serialize(new { BudgetRootId = root, RecoveryAttempt = recoveryAttempt, Limit = limit })
            });
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var retry = CreateContinuationStep(flow, source,
            $"Automatic continuation of {source.AgentName}",
            source.InputSummary + Environment.NewLine + Environment.NewLine +
            "Continue from the preserved workspace and durable handoffs. Inspect existing work " +
            "before making edits; do not repeat completed implementation or validation unless " +
            "the candidate changed. Correct the interrupted execution, not the confirmed customer " +
            "goal. The original absolute deadline and permission ceiling still apply.");
        if (failure.CanResumeSession && source.CopilotSessionId is not null)
        {
            // This is a new durable turn in the same session, not replay of the failed turn's prompt.
            retry.Phase = AgentRunPhase.CanceledByReconciliation;
            retry.CopilotSessionId = source.CopilotSessionId;
            retry.CopilotSessionHome = source.CopilotSessionHome;
        }
        foreach (var later in flow.Steps.Where(item =>
                     item.Iteration == source.Iteration && item.Sequence > source.Sequence))
        {
            later.Sequence += 10;
        }
        PreserveOrTightenRetryPermission(flow, source, retry);
        database.FlowSteps.Add(retry);
        if (flow.Kind == FlowKind.Delivery &&
            ReadPlanDuties(source.PlanDutiesJson).Contains(PlanDuty.Implement))
        {
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = retry.Id,
                Type = DeliveryReadinessService.EvidenceEpochEventType,
                Message = "Invalidated earlier evidence before the implementation continues preserved work.",
                DataJson = DeliveryReadinessService.SerializeEvidenceEpoch(
                    new DeliveryEvidenceEpoch(flow.Iteration, retry.Id, retry.Sequence))
            });
        }
        var profile = await database.TaskProfiles.SingleAsync(
            item => item.FlowStepId == source.Id, cancellationToken);
        database.TaskProfiles.Add(TaskProfileRules.CopyForStep(profile, retry.Id));
        foreach (var dependent in flow.Steps.Where(item =>
                     item.Status == StepStatus.Pending && item.DependsOnStepId == source.Id))
        {
            dependent.DependsOnStepId = retry.Id;
        }
        foreach (var review in flow.Steps.Where(item =>
                     item.Status == StepStatus.Pending && item.PreMortemTargetStepId == source.Id))
        {
            review.PreMortemTargetStepId = retry.Id;
        }
        var continuation = new AutomaticContinuation(
            source.Id, retry.Id, root, fingerprint, recoveryAttempt, dueAt);
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = retry.Id,
            Type = AutomaticContinuationEventType,
            Message = $"{source.AgentName} will continue preserved work after bounded backoff " +
                      $"(recovery {recoveryAttempt} of {limit}); no customer restart is required.",
            DataJson = JsonSerializer.Serialize(continuation)
        });
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return continuation;
    }

    private async Task WaitForAutomaticContinuationAsync(Guid stepId, CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var entry = await database.FlowEvents.AsNoTracking().SingleOrDefaultAsync(
            item => item.FlowStepId == stepId && item.Type == AutomaticContinuationEventType,
            cancellationToken);
        if (entry is null)
        {
            return;
        }
        var continuation = JsonSerializer.Deserialize<AutomaticContinuation>(entry.DataJson!)
            ?? throw new InvalidOperationException("The persisted continuation is empty.");
        var step = await database.FlowSteps.AsNoTracking().SingleAsync(
            item => item.Id == stepId, cancellationToken);
        var budget = await database.AgentExecutionBudgets.AsNoTracking().SingleAsync(
            item => item.RootStepId == continuation.BudgetRootId && item.AgentId == step.AgentId,
            cancellationToken);
        if (budget.DeadlineAt <= DateTimeOffset.UtcNow || continuation.DueAt >= budget.DeadlineAt)
        {
            var failure = new AgentRunException(
                AssignmentExecutionBudget.ExhaustedReason, AgentRunFailureKind.BudgetExhausted);
            await MarkStepFailedAsync(stepId, failure, null, cancellationToken);
            throw failure;
        }
        var delay = continuation.DueAt - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }
        if (budget.DeadlineAt <= DateTimeOffset.UtcNow)
        {
            var failure = new AgentRunException(
                AssignmentExecutionBudget.ExhaustedReason, AgentRunFailureKind.BudgetExhausted);
            await MarkStepFailedAsync(stepId, failure, null, cancellationToken);
            throw failure;
        }
    }

    private static FlowStep CreateContinuationStep(
        FlowRun flow, FlowStep source, string label, string assignment) => new()
    {
        FlowRunId = flow.Id,
        Iteration = source.Iteration,
        Sequence = source.Sequence + 10,
        AgentId = source.AgentId,
        AgentName = source.AgentName,
        AgentRole = source.AgentRole,
        Label = label,
        PlanStepKey = source.PlanStepKey,
        PlanDutiesJson = source.PlanDutiesJson,
        PlanStage = source.PlanStage,
        InvocationKind = source.InvocationKind,
        IsOutcomeOwner = source.IsOutcomeOwner,
        PermissionProfile = source.PermissionProfile,
        EffectivePermissionJson = source.EffectivePermissionJson,
        WorkflowRevision = source.WorkflowRevision,
        ExecutionBudgetRootId = source.ExecutionBudgetRootId,
        ExecutionPolicyJson = source.ExecutionPolicyJson,
        Status = StepStatus.Pending,
        Phase = AgentRunPhase.PreparingWorkspace,
        Attempt = IsPreMortemStep(source) ? source.Attempt : flow.Steps
            .Where(item => item.Iteration == source.Iteration && item.AgentId == source.AgentId)
            .Max(item => item.Attempt) + 1,
        InputSummary = assignment,
        RemotePublicationAllowed = source.RemotePublicationAllowed,
        RetryOfStepId = GetRetryRootId(source),
        DependsOnStepId = source.DependsOnStepId,
        PushbackRootStepId = source.PushbackRootStepId,
        StableSemanticRootId = GetStableSemanticRootId(source),
        PreMortemOriginStepId = source.PreMortemOriginStepId,
        PreMortemTargetStepId = source.PreMortemTargetStepId,
        PreMortemReviewStepId = source.PreMortemReviewStepId
    };

    private async Task<FlowStep> RequireHostHandoffRepairAsync(
        Guid flowId, HostHandoffRepairRequiredException failure, CancellationToken cancellationToken)
    {
        await using var lease = await _lifecycle.EnterAsync(flowId, cancellationToken);
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var step = await database.FlowSteps.SingleAsync(
            item => item.Id == failure.StepId && item.FlowRunId == flowId, cancellationToken);
        var flow = await database.Flows.SingleAsync(item => item.Id == flowId, cancellationToken);
        if (flow.Status is not (FlowStatus.Running or FlowStatus.Reworking))
        {
            throw failure;
        }
        if (await database.FlowEvents.AnyAsync(
                item => item.FlowStepId == step.Id && item.Type == HostRepairEventType, cancellationToken))
        {
            return step;
        }
        if (step.Status != StepStatus.Failed)
        {
            throw new InvalidOperationException("Host repair requires the failed worker attempt.");
        }
        await StageHostHandoffRepairAsync(database, flow, step, failure, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return step;
    }

    private static async Task StageHostHandoffRepairAsync(
        HarnessDbContext database, FlowRun flow, FlowStep step,
        HostHandoffRepairRequiredException failure, CancellationToken cancellationToken)
    {
        var owner = failure.OwnerPlanStepKey;
        if (owner is null)
        {
            var blocker = await database.FlowEvents.SingleAsync(
                item => item.FlowStepId == step.Id && item.Type == "workspace.preview-preparation-blocked",
                cancellationToken);
            using var data = JsonDocument.Parse(blocker.DataJson!);
            owner = data.RootElement.GetProperty("OwnerPlanStepKey").GetString()
                ?? throw new InvalidOperationException("The host repair has no accepted owner.");
        }
        var handoff = new DynamicHandoffStatus(true, owner, failure.Message);
        await ValidateDynamicPushbackTargetAsync(database, flow, step, handoff, cancellationToken);
        var existing = await database.FlowEvents.SingleOrDefaultAsync(
            item => item.FlowRunId == flow.Id && item.FlowStepId == step.Id &&
                item.Type == HostRepairEventType, cancellationToken);
        if (existing is not null)
        {
            using var directive = JsonDocument.Parse(existing.DataJson!);
            if (directive.RootElement.GetProperty("OwnerPlanStepKey").GetString() != owner)
            {
                throw new InvalidOperationException("The existing host repair is bound to a different owner.");
            }
            step.Status = StepStatus.Pushback;
            step.PushbackReason = directive.RootElement.GetProperty("Reason").GetString()
                ?? throw new InvalidOperationException("The existing host repair has no reason.");
            return;
        }
        step.Status = StepStatus.Pushback;
        step.PushbackReason = ClipText(failure.Message, 2_000);
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = step.Id,
            Type = HostRepairEventType,
            Message = failure.OwnerPlanStepKey is null
                ? "The host routed its unresolved preflight to the accepted implementation owner; " +
                  "an invalid COMPLETE response cannot stop internal repair or open customer review."
                : failure.ValidatedReport
                    ? "The host routed actionable failed QA criteria to the accepted implementation " +
                      "owner within the current iteration; fresh QA is required before readiness."
                    : "The host rejected the QA response but routed its scoped defect reports to the " +
                  "accepted implementation owner. No verification claims were accepted; fresh QA is required.",
            DataJson = JsonSerializer.Serialize(new
            {
                OwnerPlanStepKey = owner, Reason = step.PushbackReason,
                Kind = failure.OwnerPlanStepKey is null ? "preview-preflight" : "qa-remediation-proposal",
                failure.ValidatedReport,
                Proposal = failure.Proposal
            })
        });
    }

    internal sealed record FramedPreMortemResponse(Guid SourceStepId, string Output);

    internal static string? TryFramePreMortemResponse(string output)
    {
        var normalized = output.ReplaceLineEndings("\n");
        if (normalized.Length > PreMortemRules.MaximumReviewOutputCharacters)
        {
            return null;
        }
        var lines = output.Split('\n');
        var statuses = lines.Select((line, index) => (Text: line.Trim(), Index: index))
            .Where(item => item.Text.StartsWith("PRE_MORTEM_STATUS:", StringComparison.Ordinal))
            .ToArray();
        if (statuses.Length != 1 ||
            statuses[0].Text is not (PreMortemRules.ClearStatus or PreMortemRules.FindingsStatus))
        {
            return null;
        }
        var begins = lines.Select((line, index) => (Text: line.Trim(), Index: index))
            .Where(item => item.Text == PreMortemRules.FindingsBeginSentinel).ToArray();
        var ends = lines.Select((line, index) => (Text: line.Trim(), Index: index))
            .Where(item => item.Text == PreMortemRules.FindingsEndSentinel).ToArray();
        if (begins.Length != 1 || ends.Length != 1 || ends[0].Index <= begins[0].Index ||
            (statuses[0].Index > begins[0].Index && statuses[0].Index < ends[0].Index))
        {
            return null;
        }
        var framed = statuses[0].Text + "\n" +
            string.Join("\n", lines.Where((_, index) => index != statuses[0].Index));
        try
        {
            _ = PreMortemRules.ParseReview(framed);
            return framed;
        }
        catch (PreMortemValidationException)
        {
            return null;
        }
    }

    internal static async Task<FramedPreMortemResponse?> TryRecoverPreMortemResponseAsync(
        HarnessDbContext database, FlowRun flow, FlowStep step, string output,
        CancellationToken cancellationToken)
    {
        if (step.FlowRunId != flow.Id || step.Iteration != flow.Iteration ||
            step.InvocationKind != ExecutionInvocationKind.PreMortem ||
            step.PlanStage != PlanStage.BeforeReview || step.RemotePublicationAllowed ||
            step.PreMortemTargetStepId is null || ReviewCoordinator.HasAcceptedCustomerReview(flow))
        {
            return null;
        }
        if (!await database.FlowSteps.AsNoTracking().AnyAsync(
                item => item.FlowRunId == flow.Id && item.Id == step.PreMortemTargetStepId &&
                    item.Iteration == flow.Iteration && item.Status == StepStatus.Completed &&
                    item.Sequence < step.Sequence, cancellationToken))
        {
            return null;
        }
        try
        {
            _ = PreMortemRules.ParseReview(output);
            return null;
        }
        catch (PreMortemValidationException)
        {
            // Only framing may be repaired; the full strict findings contract still has to pass.
        }
        if (TryFramePreMortemResponse(output) is { } framed)
        {
            return new FramedPreMortemResponse(step.Id, framed);
        }
        var correctionEvent = await database.FlowEvents.AsNoTracking().SingleOrDefaultAsync(
            item => item.FlowRunId == flow.Id && item.FlowStepId == step.Id &&
                item.Type == "agent.contract-correction-scheduled", cancellationToken);
        if (correctionEvent is null)
        {
            return null;
        }
        using var correction = JsonDocument.Parse(correctionEvent.DataJson!);
        if (correction.RootElement.GetProperty("CorrectionStepId").GetGuid() != step.Id)
        {
            throw new InvalidOperationException("The response correction ledger names a different attempt.");
        }
        var sourceId = correction.RootElement.GetProperty("SourceStepId").GetGuid();
        var source = await database.FlowSteps.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == sourceId && item.FlowRunId == flow.Id, cancellationToken);
        if (source is null || source.Status != StepStatus.Completed ||
            source.Iteration != step.Iteration || source.Sequence >= step.Sequence ||
            source.AgentId != step.AgentId || source.PlanStepKey != step.PlanStepKey ||
            source.InvocationKind != step.InvocationKind || source.PlanStage != step.PlanStage ||
            source.RemotePublicationAllowed ||
            source.PreMortemTargetStepId != step.PreMortemTargetStepId ||
            source.PreMortemOriginStepId != step.PreMortemOriginStepId ||
            GetStableSemanticRootId(source) != GetStableSemanticRootId(step))
        {
            return null;
        }
        return TryFramePreMortemResponse(source.OutputSummary) is { } sourceOutput
            ? new FramedPreMortemResponse(source.Id, sourceOutput) : null;
    }

    private static void RecordPreMortemFramingRecovery(
        HarnessDbContext database, FlowRun flow, FlowStep step,
        string originalOutput, FramedPreMortemResponse response)
    {
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = step.Id,
            Type = "agent.pre-mortem-framing-recovered",
            Message = "Recovered a complete strict pre-mortem report by placing its exact status " +
                "first. No findings, JSON escapes, requirements or verification claims were repaired.",
            DataJson = JsonSerializer.Serialize(new
            {
                response.SourceStepId,
                OriginalOutput = originalOutput,
                FramedOutput = response.Output
            })
        });
    }

    internal static async Task<HostHandoffRepairRequiredException?> TryBuildQaOwnerRepairAsync(
        HarnessDbContext database, FlowRun flow, FlowStep step, string output,
        CancellationToken cancellationToken, bool validatedReport = false)
    {
        if (flow.Kind != FlowKind.Delivery || !IsDeliveryVerificationStep(step) ||
            step.Iteration != flow.Iteration || step.PlanStage != PlanStage.BeforeReview ||
            step.InvocationKind != ExecutionInvocationKind.Worker || step.RemotePublicationAllowed ||
            ReviewCoordinator.HasAcceptedCustomerReview(flow))
        {
            return null;
        }
        var (acceptance, hash, _) = DeliveryReadinessService.TryReadAcceptancePlan(flow);
        if (acceptance is null)
        {
            return null;
        }
        var framed = AgentHandoffInspector.HasTerminalStatus(output);
        if (framed && await GetQaReworkTargetErrorAsync(database, flow, step, output, cancellationToken) is not null)
        {
            return null;
        }
        if (!validatedReport && !framed)
        {
            var binding = await database.FlowEvents.AsNoTracking().SingleOrDefaultAsync(item =>
                item.FlowRunId == flow.Id && item.FlowStepId == step.Id &&
                item.Type == "agent.contract-correction-scheduled", cancellationToken);
            if (binding is not null)
            {
                using var directive = JsonDocument.Parse(binding.DataJson!);
                if (directive.RootElement.GetProperty("CorrectionStepId").GetGuid() != step.Id)
                {
                    throw new InvalidOperationException("The response correction ledger names a different attempt.");
                }
                var boundId = directive.RootElement.GetProperty("SourceStepId").GetGuid();
                var boundSource = await database.FlowSteps.AsNoTracking().SingleOrDefaultAsync(item =>
                    item.Id == boundId && item.FlowRunId == flow.Id &&
                    item.Iteration == step.Iteration && item.AgentId == step.AgentId &&
                    item.PlanStepKey == step.PlanStepKey, cancellationToken);
                if (boundSource is not null && AgentHandoffInspector.HasTerminalStatus(boundSource.OutputSummary) &&
                    await GetQaReworkTargetErrorAsync(database, flow, boundSource, boundSource.OutputSummary,
                        cancellationToken) is not null)
                {
                    return null;
                }
            }
        }
        var proposalSourceId = step.Id;
        var proposal = ParseQaRepairProposal(output, acceptance, hash);
        if (proposal is null && !validatedReport)
        {
            var correctionEvent = await database.FlowEvents.AsNoTracking().SingleOrDefaultAsync(
                item => item.FlowRunId == flow.Id && item.FlowStepId == step.Id &&
                    item.Type == "agent.contract-correction-scheduled", cancellationToken);
            if (correctionEvent is null)
            {
                return null;
            }
            using var correction = JsonDocument.Parse(correctionEvent.DataJson!);
            if (correction.RootElement.GetProperty("CorrectionStepId").GetGuid() != step.Id)
            {
                throw new InvalidOperationException("The response correction ledger names a different attempt.");
            }
            var sourceId = correction.RootElement.GetProperty("SourceStepId").GetGuid();
            var source = await database.FlowSteps.AsNoTracking().SingleOrDefaultAsync(
                item => item.Id == sourceId && item.FlowRunId == flow.Id, cancellationToken);
            if (source is null || source.Iteration != flow.Iteration || step.Iteration != flow.Iteration ||
                source.Sequence >= step.Sequence || source.Status != StepStatus.Completed ||
                source.AgentId != step.AgentId || source.PlanStepKey != step.PlanStepKey ||
                source.InvocationKind != ExecutionInvocationKind.Worker ||
                source.PlanStage != PlanStage.BeforeReview || !IsDeliveryVerificationStep(source) ||
                GetStableSemanticRootId(source) != GetStableSemanticRootId(step))
            {
                return null;
            }
            if (AgentHandoffInspector.HasTerminalStatus(source.OutputSummary) &&
                await GetQaReworkTargetErrorAsync(database, flow, source, source.OutputSummary,
                    cancellationToken) is not null)
            {
                return null;
            }
            proposal = ParseQaRepairProposal(source.OutputSummary, acceptance, hash);
            if (proposal is null)
            {
                return null;
            }
            proposalSourceId = source.Id;
        }
        if (proposal is null)
        {
            return null;
        }
        var ownerStep = await FindQaImplementationOwnerAsync(database, flow, step, cancellationToken);
        if (ownerStep is null)
        {
            return null;
        }
        var unmet = proposal.Document.Criteria!
            .Where(item => item.Outcome is DeliveryCriterionOutcome.Failed or DeliveryCriterionOutcome.Blocked &&
                item.ResponsibleRoles!.Any(role =>
                    role == ownerStep.AgentRole || role == ownerStep.AgentId || role == ownerStep.AgentName))
            .ToArray();
        if (unmet.Length == 0 ||
            validatedReport && (unmet.Any(item => string.IsNullOrWhiteSpace(item.Remediation)) ||
                proposal.Document.Criteria!.Any(item =>
                    item.Outcome is DeliveryCriterionOutcome.Failed or DeliveryCriterionOutcome.Blocked &&
                    !unmet.Contains(item))))
        {
            return null;
        }
        var reason = (validatedReport
            ? "Independent QA requests implementation rework for failed or blocked accepted criteria. "
            : "The QA response failed the host contract; none of its verification claims were accepted. ") +
            "Investigate these reported defects against the unchanged accepted " +
            "criteria, repair confirmed defects in your owned workspace, and return for fresh QA. " +
            string.Join(" ", unmet.Select(item => $"{item.CriterionId}: {item.Remediation}"));
        return new HostHandoffRepairRequiredException(step.Id, reason, ownerStep.PlanStepKey,
            new QaOwnerRepairProposal(proposalSourceId, hash,
                new DeliveryAcceptancePlan(acceptance.Criteria
                    .Where(item => unmet.Any(result => result.CriterionId == item.Id)).ToArray()),
                unmet), validatedReport);
    }

    private static ParsedDeliveryQaDocument? ParseQaRepairProposal(
        string output, DeliveryAcceptancePlan acceptance, string hash)
    {
        try
        {
            // Syntax and accepted scope are checked, but these claims are not accepted as evidence.
            return DeliveryReadinessPolicy.ParseQaOutput(output, acceptance, hash);
        }
        catch (DeliveryReadinessContractException)
        {
            return null;
        }
    }

    internal static async Task<AgentContextDocument?> ResolveOwnerRepairProposalAsync(
        HarnessDbContext database, FlowRun flow, FlowStep step, CancellationToken cancellationToken)
    {
        if (step.FlowRunId != flow.Id || step.Iteration != flow.Iteration ||
            step.InvocationKind != ExecutionInvocationKind.Worker ||
            step.PlanStage != PlanStage.BeforeReview ||
            !ReadPlanDuties(step.PlanDutiesJson).Contains(PlanDuty.Implement))
        {
            return null;
        }
        var rootId = GetStableSemanticRootId(step);
        var source = rootId == step.Id ? step : await database.FlowSteps.AsNoTracking()
            .SingleOrDefaultAsync(item => item.FlowRunId == flow.Id && item.Id == rootId, cancellationToken);
        if (source is null || GetStableSemanticRootId(source) != rootId ||
            source.Sequence > step.Sequence || source.Iteration != step.Iteration ||
            source.AgentId != step.AgentId || source.PlanStepKey != step.PlanStepKey ||
            source.InvocationKind != step.InvocationKind || source.PlanStage != step.PlanStage ||
            !ReadPlanDuties(source.PlanDutiesJson).Contains(PlanDuty.Implement))
        {
            return null;
        }
        var binding = flow.Events.SingleOrDefault(item =>
            item.FlowStepId == source.Id && item.Type == OwnerRepairProposalEventType);
        return binding is null ? null : new AgentContextDocument(
            "qa-repair-proposal.json", binding.DataJson ??
            throw new InvalidOperationException("The owner repair proposal is missing."));
    }

    internal const string RepairedCandidateAssignmentEventType = "verification.repaired-candidate-assignment";

    private static async Task<FlowStep?> FindUnassignedRepairedCandidateAsync(
        HarnessDbContext database, FlowRun flow, FlowStep verification,
        CancellationToken cancellationToken)
    {
        if (flow.Kind != FlowKind.Delivery || !IsDeliveryVerificationStep(verification) ||
            verification.PushbackRootStepId is not { } sourceId ||
            verification.DependsOnStepId is not { } revisionId)
        {
            return null;
        }
        var directive = await database.FlowEvents.AsNoTracking().SingleOrDefaultAsync(
            item => item.FlowRunId == flow.Id && item.FlowStepId == sourceId &&
                item.Type == HostRepairEventType, cancellationToken);
        var revision = await database.FlowSteps.SingleOrDefaultAsync(
            item => item.Id == revisionId && item.FlowRunId == flow.Id &&
                item.Iteration == verification.Iteration && item.Status == StepStatus.Completed,
            cancellationToken);
        if (directive is null || revision is null ||
            !ReadPlanDuties(revision.PlanDutiesJson).Contains(PlanDuty.Implement))
        {
            return null;
        }
        using var data = JsonDocument.Parse(directive.DataJson!);
        if (revision.PlanStepKey != data.RootElement.GetProperty("OwnerPlanStepKey").GetString())
        {
            return null;
        }
        var assignments = await database.FlowEvents.AsNoTracking()
            .Where(item => item.FlowRunId == flow.Id &&
                item.Type == RepairedCandidateAssignmentEventType)
            .Select(item => item.DataJson).ToListAsync(cancellationToken);
        return assignments.Any(json =>
        {
            using var assignment = JsonDocument.Parse(json!);
            return assignment.RootElement.GetProperty("RevisionStepId").GetGuid() ==
                (revision.ExecutionBudgetRootId ?? GetStableSemanticRootId(revision));
        }) ? null : revision;
    }

    private static void RecordRepairedCandidateAssignment(
        HarnessDbContext database, FlowRun flow, FlowStep verification, FlowStep revision,
        Guid sourceId)
    {
        verification.ExecutionBudgetRootId = verification.Id;
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = verification.Id,
            Type = RepairedCandidateAssignmentEventType,
            Message = "Queued a distinct bounded verification assignment for the repaired candidate. " +
                "Earlier deadlines remain unchanged; permissions, repair limits, and customer approval remain enforced.",
            DataJson = JsonSerializer.Serialize(new
            {
                RevisionStepId = revision.ExecutionBudgetRootId ?? GetStableSemanticRootId(revision),
                SourceStepId = sourceId,
                BudgetRootStepId = verification.Id
            })
        });
    }

    private async Task<DynamicHandoffStatus> ReadRecoveryHandoffAsync(
        FlowStep step, CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var entry = await database.FlowEvents.AsNoTracking().SingleOrDefaultAsync(
            item => item.FlowStepId == step.Id && item.Type == HostRepairEventType, cancellationToken);
        if (entry is null)
        {
            return AgentHandoffInspector.ParseDynamic(step.OutputSummary);
        }
        using var data = JsonDocument.Parse(entry.DataJson!);
        return new DynamicHandoffStatus(true,
            data.RootElement.GetProperty("OwnerPlanStepKey").GetString(),
            data.RootElement.GetProperty("Reason").GetString());
    }
}

using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Data;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

public sealed class RoutingObservationRecorder(
    IDbContextFactory<HarnessDbContext> databaseFactory,
    TimeProvider timeProvider)
{
    public Task RecordCompletionAsync(
        Guid stepId,
        bool accepted,
        long durationMilliseconds,
        int executionAttempts,
        string outcomeKind,
        CancellationToken cancellationToken = default) =>
        RecordAsync(
            stepId,
            accepted,
            availabilityFailure: false,
            durationMilliseconds,
            executionAttempts,
            outcomeKind,
            cancellationToken);

    public Task RecordFailureAsync(
        Guid stepId,
        AgentRunFailureKind failureKind,
        long durationMilliseconds,
        int executionAttempts,
        CancellationToken cancellationToken = default)
    {
        var operationalFailure = failureKind is
            AgentRunFailureKind.DependencyUnavailable or
            AgentRunFailureKind.ModelUnavailable or
            AgentRunFailureKind.TimedOut or
            AgentRunFailureKind.Stalled or
            AgentRunFailureKind.BudgetExhausted or
            AgentRunFailureKind.AmbiguousCrash;
        return RecordAsync(
            stepId,
            operationalFailure ? null : false,
            operationalFailure,
            durationMilliseconds,
            executionAttempts,
            failureKind switch
            {
                AgentRunFailureKind.ModelUnavailable => "model-unavailable",
                AgentRunFailureKind.DependencyUnavailable => "dependency-unavailable",
                AgentRunFailureKind.TimedOut => "runtime-timeout",
                AgentRunFailureKind.Stalled => "runtime-stalled",
                AgentRunFailureKind.BudgetExhausted => "execution-budget-exhausted",
                AgentRunFailureKind.AmbiguousCrash => "runtime-ambiguous-crash",
                _ => "execution-failure"
            },
            cancellationToken);
    }

    public Task RecordPushbackDetectionAsync(
        Guid stepId,
        long durationMilliseconds,
        int executionAttempts,
        CancellationToken cancellationToken = default) =>
        RecordAsync(
            stepId,
            accepted: null,
            availabilityFailure: false,
            durationMilliseconds,
            executionAttempts,
            "valid-pushback-detection",
            cancellationToken);

    public async Task RecordFinalApprovalAsync(
        Guid flowId,
        CancellationToken cancellationToken = default)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var flow = await database.Flows
            .AsNoTracking()
            .SingleAsync(item => item.Id == flowId, cancellationToken);
        var stepIds = await database.FlowSteps
            .AsNoTracking()
            .Where(item =>
                item.FlowRunId == flowId &&
                item.Iteration == flow.Iteration &&
                item.Status == StepStatus.Completed)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        foreach (var stepId in stepIds)
        {
            await RecordAsync(
                stepId,
                true,
                availabilityFailure: false,
                0,
                1,
                "final-approval-weak",
                cancellationToken,
                evidenceWeight: 0.15 / Math.Max(1, stepIds.Count),
                includeExecutionMetrics: false);
        }
    }

    public async Task RecordTargetedReworkAsync(
        Guid flowId,
        int iteration,
        IReadOnlyCollection<string> targetRoles,
        CancellationToken cancellationToken = default)
    {
        if (targetRoles.Count == 0)
        {
            return;
        }

        List<Guid> stepIds;
        await using (var database =
                     await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            var candidates = await database.FlowSteps
                .AsNoTracking()
                .Where(item =>
                    item.FlowRunId == flowId &&
                    item.Iteration == iteration &&
                    targetRoles.Contains(item.AgentRole) &&
                    item.Status == StepStatus.Completed)
                .OrderByDescending(item => item.Sequence)
                .ThenByDescending(item => item.Attempt)
                .Select(item => new
                {
                    item.Id,
                    item.AgentRole
                })
                .ToListAsync(cancellationToken);
            stepIds = candidates
                .GroupBy(item => item.AgentRole, StringComparer.Ordinal)
                .Select(group => group.First().Id)
                .ToList();
        }

        foreach (var stepId in stepIds)
        {
            await RecordAsync(
                stepId,
                accepted: false,
                availabilityFailure: false,
                0,
                1,
                "customer-rework-targeted",
                cancellationToken,
                evidenceWeight: 0.75,
                includeExecutionMetrics: false);
        }
    }

    private async Task RecordAsync(
        Guid stepId,
        bool? accepted,
        bool availabilityFailure,
        long durationMilliseconds,
        int executionAttempts,
        string outcomeKind,
        CancellationToken cancellationToken,
        double evidenceWeight = 1,
        bool includeExecutionMetrics = true)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var decision = await database.RoutingDecisions
            .AsNoTracking()
            .Where(item => item.FlowStepId == stepId && !item.Superseded)
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (decision is null)
        {
            return;
        }
        var existingObservation = await database.RoutingObservations.FirstOrDefaultAsync(
            item =>
                item.RoutingDecisionId == decision.Id &&
                item.OutcomeKind == outcomeKind,
            cancellationToken);
        if (existingObservation is not null)
        {
            return;
        }
        if (outcomeKind is
            "downstream-pushback" or
            "customer-rework-targeted" or
            "invalid-task-profile")
        {
            var provisionalAcceptance = await database.RoutingObservations.FirstOrDefaultAsync(
                item =>
                    item.RoutingDecisionId == decision.Id &&
                    item.OutcomeKind == "accepted-handoff",
                cancellationToken);
            if (provisionalAcceptance is not null)
            {
                provisionalAcceptance.Accepted = false;
                provisionalAcceptance.EvidenceWeight = Math.Clamp(evidenceWeight, 0, 1);
                provisionalAcceptance.OutcomeKind = outcomeKind;
                provisionalAcceptance.ObservedAt = timeProvider.GetUtcNow();
                await database.SaveChangesAsync(cancellationToken);
                return;
            }
        }
        var profile = await database.TaskProfiles
            .AsNoTracking()
            .SingleAsync(item => item.Id == decision.TaskProfileId, cancellationToken);
        var candidate = await database.ModelCatalogCandidates
            .AsNoTracking()
            .SingleAsync(
                item =>
                    item.ModelCatalogSnapshotId == decision.ModelCatalogSnapshotId &&
                    item.Model == decision.SelectedModel &&
                    item.Effort == decision.SelectedEffort,
                cancellationToken);
        database.RoutingObservations.Add(new RoutingObservation
        {
            RoutingDecisionId = decision.Id,
            FlowStepId = stepId,
            Role = profile.Role,
            TaskTypeTagsJson = profile.TaskTypeTagsJson,
            Complexity = profile.Complexity,
            ReasoningDepth = profile.ReasoningDepth,
            ContextDemand = profile.ContextDemand,
            ToolIntensity = profile.ToolIntensity,
            Risk = profile.Risk,
            Accepted = accepted,
            AvailabilityFailure = availabilityFailure,
            EvidenceWeight = Math.Clamp(evidenceWeight, 0, 1),
            DurationMilliseconds = includeExecutionMetrics
                ? Math.Max(0, durationMilliseconds)
                : 0,
            ExecutionAttempts = Math.Max(1, executionAttempts),
            EstimatedPremiumRequests = includeExecutionMetrics
                ? (candidate.PremiumMultiplier ?? 1) * Math.Max(1, executionAttempts)
                : -1,
            OutcomeKind = outcomeKind,
            ObservedAt = timeProvider.GetUtcNow()
        });
        await database.SaveChangesAsync(cancellationToken);
    }
}

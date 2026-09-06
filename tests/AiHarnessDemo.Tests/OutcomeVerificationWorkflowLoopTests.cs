using System.Diagnostics;
using AiHarnessDemo.Api;
using AiHarnessDemo.Contracts;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Data;
using AiHarnessDemo.Infrastructure;
using AiHarnessDemo.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiHarnessDemo.Tests;

public sealed class OutcomeVerificationWorkflowLoopTests
{
    [Fact]
    public async Task Pass_PreparesCandidateBeforeQaAndCreatesOneReleaseGate()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.PASS],
            maxRounds: 3);

        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        await using var database = await fixture.Factory.CreateDbContextAsync();
        var flow = await database.Flows
            .Include(item => item.Steps)
            .ThenInclude(step => step.ToolCalls)
            .Include(item => item.GateRecords)
            .SingleAsync(item => item.Id == fixture.FlowId);
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        var release = flow.Steps.Single(item =>
            item.Label == WorkflowEngine.ReleaseCandidateLabel);
        var qa = flow.Steps.Single(item =>
            item.Label == $"{WorkflowEngine.OutcomeQaLabelPrefix}1)");

        Assert.True(release.Sequence < qa.Sequence);
        Assert.Equal(release.Id, qa.DependsOnStepId);
        Assert.Equal(OutcomeVerificationStatus.Passed, state.Status);
        Assert.Equal(state.CurrentCandidate?.Fingerprint, state.VerifiedCandidateFingerprint);
        Assert.Equal(FlowStatus.WaitingForFeedback, flow.Status);
        var gate = Assert.Single(flow.GateRecords, item =>
            item.ActionType == HandoffActionType.Release &&
            !item.Resolved);
        Assert.Equal(qa.Id, gate.FlowStepId);
        var observedTool = Assert.Single(qa.ToolCalls);
        Assert.True(observedTool.Succeeded);
        Assert.Equal("Command", observedTool.ToolType);
        Assert.Equal(fixture.Workspace, observedTool.WorkingDirectory);
        Assert.Equal(0, observedTool.ExitCode);
        Assert.StartsWith("sha256:", observedTool.ResultDigest);
        Assert.Contains("Focused test passed", observedTool.ResultSummary);
        Assert.True(HostObservedToolLocator.MatchesCommand(
            observedTool.ArgumentsSummary,
            "dotnet test"));
        Assert.DoesNotContain(
            flow.GateRecords,
            item =>
                item.ActionType == HandoffActionType.Release &&
                item.FlowStepId == release.Id);
    }

    [Fact]
    public async Task Fail_RoutesOriginalOwnerThenRefreshesCandidateAndContinuesQaSession()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.FAIL, OutcomeQaVerdict.PASS],
            maxRounds: 3);

        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        await using var database = await fixture.Factory.CreateDbContextAsync();
        var flow = await database.Flows
            .Include(item => item.Steps)
            .Include(item => item.GateRecords)
            .SingleAsync(item => item.Id == fixture.FlowId);
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        var correction = Assert.Single(flow.Steps, item =>
            item.Label.StartsWith(
                WorkflowEngine.OutcomeCorrectionLabelPrefix,
                StringComparison.Ordinal));
        var refresh = Assert.Single(flow.Steps, item =>
            item.Label.StartsWith(
                WorkflowEngine.OutcomeCandidateRefreshLabelPrefix,
                StringComparison.Ordinal));
        var qaSteps = flow.Steps
            .Where(item => item.Label.StartsWith(
                WorkflowEngine.OutcomeQaLabelPrefix,
                StringComparison.Ordinal))
            .OrderBy(item => item.Sequence)
            .ToArray();

        Assert.Equal("software-engineer", correction.AgentRole);
        Assert.True(correction.Sequence < refresh.Sequence);
        Assert.True(refresh.Sequence < qaSteps[1].Sequence);
        Assert.Equal(2, state.Rounds.Count);
        Assert.Equal(OutcomeVerificationStatus.Passed, state.Status);
        Assert.Equal(qaSteps[0].CopilotSessionId, qaSteps[1].CopilotSessionId);
        Assert.Equal(
            flow.Steps
                .First(item => item.AgentRole == "software-engineer")
                .CopilotSessionId,
            correction.CopilotSessionId);
        Assert.Single(flow.GateRecords, item =>
            item.ActionType == HandoffActionType.Release &&
            !item.Resolved);
    }

    [Fact]
    public async Task RestartBeforeSecondRoundCorrection_DoesNotReuseFirstRoundCorrection()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts:
            [
                OutcomeQaVerdict.FAIL,
                OutcomeQaVerdict.FAIL,
                OutcomeQaVerdict.PASS
            ],
            maxRounds: 3);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        Guid firstCorrectionRoot;
        Guid secondQaStepId;
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows
                .Include(item => item.Steps)
                .Include(item => item.GateRecords)
                .SingleAsync(item => item.Id == fixture.FlowId);
            var firstCorrection = flow.Steps.Single(step =>
                step.Kind == FlowStepKind.OutcomeOwnerCorrection &&
                step.OutcomeQaRound == 1);
            firstCorrectionRoot =
                firstCorrection.StableSemanticRootId ?? firstCorrection.Id;
            var secondQa = flow.Steps.Single(step =>
                step.Kind == FlowStepKind.OutcomeQa &&
                step.OutcomeQaRound == 2);
            secondQaStepId = secondQa.Id;
            var removedSteps = flow.Steps
                .Where(step => step.Sequence > secondQa.Sequence)
                .ToArray();
            var removedIds = removedSteps.Select(step => step.Id).ToHashSet();
            var removedRoots = removedSteps
                .Select(step => step.StableSemanticRootId ?? step.Id)
                .ToHashSet();

            var state = OutcomeVerificationRules.DeserializeAggregate(
                flow.OutcomeVerificationJson);
            state.Rounds = state.Rounds.Take(2).ToList();
            var secondRound = state.Rounds[1];
            secondRound.CorrectionStepIds.Clear();
            secondRound.CorrectionRegistrations.Clear();
            secondRound.ProcessedCorrectionRootIds.Clear();
            state.ProcessedSemanticRootIds.RemoveAll(removedRoots.Contains);
            state.Evidence = state.Evidence
                .Where(item => !removedIds.Contains(item.ProducerStepId))
                .ToList();
            state.EvidenceProcessing = state.EvidenceProcessing
                .Where(item => !removedIds.Contains(item.ProducerStepId))
                .ToList();
            state.PendingOwnerRoles = ["software-engineer"];
            state.CurrentCandidate = null;
            state.Publication = null;
            state.VerifiedCandidateFingerprint = null;
            state.VerifiedAt = null;
            state.ActiveQaRound = null;
            state.ActiveQaStepId = null;
            state.ActiveQaContextPath = null;
            state.ActiveQaContextHash = null;
            state.Status = OutcomeVerificationStatus.Correcting;
            state.Stale = false;
            flow.OutcomeVerificationJson =
                OutcomeVerificationRules.SerializeAggregate(state);
            flow.Status = FlowStatus.Queued;
            database.GateRecords.RemoveRange(
                flow.GateRecords.Where(gate =>
                    removedIds.Contains(gate.FlowStepId)));
            database.FlowSteps.RemoveRange(removedSteps);
            await database.SaveChangesAsync();
        }

        await fixture.Engine.ReconcileCompletedOutcomeArtifactsAsync(
            fixture.FlowId,
            CancellationToken.None);
        var afterRecovery = OutcomeVerificationRules.DeserializeAggregate(
            (await ReadFlowAsync(fixture)).OutcomeVerificationJson);
        Assert.Equal(["software-engineer"], afterRecovery.PendingOwnerRoles);
        Assert.DoesNotContain(
            firstCorrectionRoot,
            afterRecovery.Rounds[1].ProcessedCorrectionRootIds);

        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        await using var verify = await fixture.Factory.CreateDbContextAsync();
        var steps = await verify.FlowSteps
            .Where(step => step.FlowRunId == fixture.FlowId)
            .ToListAsync();
        var finalState = OutcomeVerificationRules.DeserializeAggregate(
            (await verify.Flows.SingleAsync(flow => flow.Id == fixture.FlowId))
            .OutcomeVerificationJson);
        var secondCorrection = Assert.Single(steps, step =>
            step.Kind == FlowStepKind.OutcomeOwnerCorrection &&
            step.OutcomeQaRound == 2);
        var secondCorrectionRoot =
            secondCorrection.StableSemanticRootId ?? secondCorrection.Id;
        Assert.NotEqual(firstCorrectionRoot, secondCorrectionRoot);
        Assert.Equal(secondQaStepId, finalState.Rounds[1].QaStepId);
        Assert.Contains(
            finalState.Rounds[1].CorrectionRegistrations,
            registration =>
                registration.QaRound == 2 &&
                registration.OutcomePlanHash == secondCorrection.OutcomePlanHash &&
                registration.SemanticRootId == secondCorrectionRoot);
        Assert.Equal(
            1,
            finalState.Rounds[1].ProcessedCorrectionRootIds.Count(root =>
                root == secondCorrectionRoot));
        Assert.DoesNotContain(
            firstCorrectionRoot,
            finalState.Rounds[1].ProcessedCorrectionRootIds);
    }

    [Fact]
    public async Task Exhaustion_CreatesOutcomeResolutionAndNeverRelease()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.FAIL, OutcomeQaVerdict.FAIL],
            maxRounds: 2,
            qaExecutionAttempts: 3);

        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        await using var database = await fixture.Factory.CreateDbContextAsync();
        var flow = await database.Flows
            .Include(item => item.GateRecords)
            .SingleAsync(item => item.Id == fixture.FlowId);
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);

        Assert.Equal(2, state.Rounds.Count);
        Assert.Equal(OutcomeVerificationStatus.AwaitingHumanResolution, state.Status);
        Assert.Equal(FlowStatus.WaitingForFeedback, flow.Status);
        Assert.Single(flow.GateRecords, item =>
            item.ActionType == HandoffActionType.OutcomeResolution &&
            !item.Resolved);
        Assert.DoesNotContain(
            flow.GateRecords,
            item => item.ActionType == HandoffActionType.Release);
        Assert.All(state.Rounds, round => Assert.NotNull(round.Result));
        Assert.All(
            await database.FlowSteps
                .Where(item => item.AgentRole == "quality-engineer")
                .ToListAsync(),
            step => Assert.Equal(3, step.ExecutionAttempts));
    }

    [Fact]
    public async Task Continue_GrantsExactlyOneRoundAndCanReachPass()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts:
            [
                OutcomeQaVerdict.FAIL,
                OutcomeQaVerdict.FAIL,
                OutcomeQaVerdict.PASS
            ],
            maxRounds: 2);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        Guid gateId;
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            gateId = await database.GateRecords
                .Where(item =>
                    item.ActionType == HandoffActionType.OutcomeResolution &&
                    !item.Resolved)
                .Select(item => item.Id)
                .SingleAsync();
        }

        var resumed = await fixture.Engine.ResolveOutcomeAsync(
            fixture.FlowId,
            gateId,
            OutcomeResolutionAction.Continue,
            "The operator confirmed that the local dependency is available.",
            CancellationToken.None);
        Assert.Equal(FlowStatus.Queued, resumed.Status);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        await using var verify = await fixture.Factory.CreateDbContextAsync();
        var flow = await verify.Flows
            .Include(item => item.GateRecords)
            .SingleAsync(item => item.Id == fixture.FlowId);
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        Assert.Equal(3, state.MaxRounds);
        Assert.Equal(1, state.ManualRoundsGranted);
        Assert.Equal(3, state.Rounds.Count);
        Assert.Equal(OutcomeVerificationStatus.Passed, state.Status);
        Assert.Single(flow.GateRecords, item =>
            item.ActionType == HandoffActionType.Release &&
            !item.Resolved);
    }

    [Fact]
    public async Task OutcomeResolution_SaveFailureLeavesGateRetryable()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.FAIL],
            maxRounds: 1);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        Guid gateId;
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            gateId = await database.GateRecords
                .Where(item =>
                    item.ActionType == HandoffActionType.OutcomeResolution &&
                    !item.Resolved)
                .Select(item => item.Id)
                .SingleAsync();
            await database.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER fail_outcome_resolution
                BEFORE UPDATE OF OutcomeVerificationJson ON Flows
                BEGIN
                    SELECT RAISE(FAIL, 'forced outcome resolution failure');
                END;
                """);
        }

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            fixture.Engine.ResolveOutcomeAsync(
                fixture.FlowId,
                gateId,
                OutcomeResolutionAction.Continue,
                "Exercise the durable retry path.",
                CancellationToken.None));
        await using (var verifyFailure = await fixture.Factory.CreateDbContextAsync())
        {
            Assert.False((await verifyFailure.GateRecords.SingleAsync(
                item => item.Id == gateId)).Resolved);
            await verifyFailure.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER fail_outcome_resolution;");
        }
        Assert.False(fixture.GateHistory.Single(item => item.Id == gateId).Resolved);

        var resumed = await fixture.Engine.ResolveOutcomeAsync(
            fixture.FlowId,
            gateId,
            OutcomeResolutionAction.Continue,
            "Exercise the durable retry path.",
            CancellationToken.None);

        Assert.Equal(FlowStatus.Queued, resumed.Status);
        Assert.True(fixture.GateHistory.Single(item => item.Id == gateId).Resolved);
        await using var verifySuccess = await fixture.Factory.CreateDbContextAsync();
        Assert.True((await verifySuccess.GateRecords.SingleAsync(
            item => item.Id == gateId)).Resolved);
    }

    [Fact]
    public async Task Replan_StartsNewIterationAndPreservesSupersededLedger()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.FAIL],
            maxRounds: 1);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        Guid gateId;
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            gateId = await database.GateRecords
                .Where(item =>
                    item.ActionType == HandoffActionType.OutcomeResolution &&
                    !item.Resolved)
                .Select(item => item.Id)
                .SingleAsync();
        }

        var replanned = await fixture.Engine.ResolveOutcomeAsync(
            fixture.FlowId,
            gateId,
            OutcomeResolutionAction.Replan,
            "The acceptance criteria need a material reset.",
            CancellationToken.None);
        var state = OutcomeVerificationRules.DeserializeAggregate(
            replanned.OutcomeVerificationJson);

        Assert.Equal(2, replanned.Iteration);
        Assert.Equal(FlowStatus.Queued, replanned.Status);
        Assert.Equal(OutcomeVerificationStatus.Planning, state.Status);
        var archived = Assert.Single(state.PriorIterations);
        Assert.Equal(1, archived.Iteration);
        Assert.Equal(OutcomeVerificationStatus.Superseded, archived.Status);
        Assert.Single(archived.Rounds);
    }

    [Fact]
    public async Task MultiOwnerFailure_SchedulesSerialCorrectionsInPlanOrder()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.FAIL, OutcomeQaVerdict.PASS],
            maxRounds: 3,
            multiOwner: true);

        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        await using var database = await fixture.Factory.CreateDbContextAsync();
        var corrections = await database.FlowSteps
            .Where(item => item.Label.StartsWith(
                WorkflowEngine.OutcomeCorrectionLabelPrefix))
            .OrderBy(item => item.Sequence)
            .ToListAsync();
        Assert.Equal(
            ["architect", "software-engineer"],
            corrections.Select(item => item.AgentRole));
        Assert.Equal(corrections[0].Id, corrections[1].DependsOnStepId);
        Assert.All(corrections, step => Assert.Equal(StepStatus.Completed, step.Status));
    }

    [Fact]
    public async Task PlanGap_ResumesTeamLeadAndInvalidatesPriorPlanResults()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.FAIL, OutcomeQaVerdict.PASS],
            maxRounds: 3,
            planGapFirst: true);

        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        await using var database = await fixture.Factory.CreateDbContextAsync();
        var flow = await database.Flows
            .Include(item => item.Steps)
            .SingleAsync(item => item.Id == fixture.FlowId);
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        var teamLeadSteps = flow.Steps
            .Where(item => item.AgentRole == "team-lead")
            .OrderBy(item => item.Sequence)
            .ToArray();

        Assert.Equal(2, teamLeadSteps.Length);
        Assert.Equal(teamLeadSteps[0].CopilotSessionId, teamLeadSteps[1].CopilotSessionId);
        Assert.Equal(["AC-001", "AC-002"], state.AcceptancePlan!.Criteria.Select(item => item.Id));
        Assert.True(state.Rounds[0].Stale);
        Assert.Equal(OutcomeVerificationStatus.Passed, state.Status);

        var softwareSteps = flow.Steps
            .Where(item => item.AgentRole == "software-engineer")
            .OrderBy(item => item.Sequence)
            .ToArray();
        Assert.Equal(2, softwareSteps.Length);
        var originalProcessing = state.EvidenceProcessing.Single(item =>
            item.ProducerStepId == softwareSteps[0].Id);
        var correctionProcessing = state.EvidenceProcessing.Single(item =>
            item.ProducerStepId == softwareSteps[1].Id);
        Assert.NotEqual(state.AcceptancePlan.Hash, originalProcessing.AcceptancePlanHash);
        Assert.Equal(state.AcceptancePlan.Hash, correctionProcessing.AcceptancePlanHash);
        Assert.Equal(
            softwareSteps[1].Id,
            state.Evidence.Single(item => item.CriterionId == "AC-002").ProducerStepId);

        var processingCount = state.EvidenceProcessing.Count;
        await fixture.Engine.ReconcileCompletedOutcomeArtifactsAsync(
            fixture.FlowId,
            CancellationToken.None);
        await fixture.Engine.ReconcileCompletedOutcomeArtifactsAsync(
            fixture.FlowId,
            CancellationToken.None);
        var reconciled = OutcomeVerificationRules.DeserializeAggregate(
            (await ReadFlowAsync(fixture)).OutcomeVerificationJson);
        Assert.Equal(processingCount, reconciled.EvidenceProcessing.Count);
    }

    [Fact]
    public async Task SameIterationRerunAfterPlanReplacement_DoesNotRecreateInitialGraph()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.FAIL, OutcomeQaVerdict.PASS],
            maxRounds: 3,
            planGapFirst: true);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        int stepCount;
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows
                .Include(item => item.Steps)
                .SingleAsync(item => item.Id == fixture.FlowId);
            stepCount = flow.Steps.Count;
            flow.Status = FlowStatus.Queued;
            await database.SaveChangesAsync();
        }

        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        await using var verify = await fixture.Factory.CreateDbContextAsync();
        var steps = await verify.FlowSteps
            .Where(step => step.FlowRunId == fixture.FlowId)
            .ToListAsync();
        var state = OutcomeVerificationRules.DeserializeAggregate(
            (await verify.Flows.SingleAsync(flow => flow.Id == fixture.FlowId))
            .OutcomeVerificationJson);
        Assert.Equal(stepCount, steps.Count);
        Assert.Single(steps, step => step.Kind == FlowStepKind.OutcomePlan);
        Assert.Single(steps, step =>
            step.Kind == FlowStepKind.OutcomeLocalReleaseCandidate);
        Assert.Single(steps, step =>
            step.Kind == FlowStepKind.OutcomeQa &&
            step.OutcomeQaRound == 1);
        Assert.Equal(state.PlannedRoles.Count, state.InitialDeliverySemanticRootIds.Count);
        Assert.NotNull(state.InitialPlanSemanticRootId);
    }

    [Fact]
    public async Task SameIterationRerunAfterInitialPlanAcceptance_IsIdempotent()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.PASS],
            maxRounds: 3);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        int stepCount;
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows
                .Include(item => item.Steps)
                .SingleAsync(item => item.Id == fixture.FlowId);
            stepCount = flow.Steps.Count;
            flow.Status = FlowStatus.Queued;
            await database.SaveChangesAsync();
        }

        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        await using var verify = await fixture.Factory.CreateDbContextAsync();
        var steps = await verify.FlowSteps
            .Where(step => step.FlowRunId == fixture.FlowId)
            .ToListAsync();
        Assert.Equal(stepCount, steps.Count);
        Assert.Single(steps, step => step.Kind == FlowStepKind.OutcomePlan);
        Assert.Single(steps, step =>
            step.Kind == FlowStepKind.OutcomeQa &&
            step.OutcomeQaRound == 1);
    }

    [Fact]
    public async Task CrashAfterFirstPlanReplacement_ReconcilesCorrectionOnce()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.FAIL, OutcomeQaVerdict.PASS],
            maxRounds: 3,
            planGapFirst: true);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        Guid correctionRoot;
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows
                .Include(item => item.Steps)
                .Include(item => item.GateRecords)
                .SingleAsync(item => item.Id == fixture.FlowId);
            var initialPlanStep = flow.Steps.Single(step =>
                step.Kind == FlowStepKind.OutcomePlan);
            var correction = flow.Steps.Single(step =>
                step.Kind == FlowStepKind.OutcomePlanCorrection);
            correctionRoot = correction.StableSemanticRootId ?? correction.Id;
            var state = OutcomeVerificationRules.DeserializeAggregate(
                flow.OutcomeVerificationJson);
            var initialPlan = OutcomeVerificationRules.ParseAcceptancePlan(
                initialPlanStep.OutputSummary,
                state.PlannedRoles);
            state.AcceptancePlan =
                OutcomeVerificationRules.CreateAcceptanceSnapshot(
                    initialPlan,
                    initialPlanStep.Id);
            state.Rounds = state.Rounds.Take(1).ToList();
            state.Rounds[0].Stale = false;
            var removedSteps = flow.Steps
                .Where(step => step.Sequence > correction.Sequence)
                .ToArray();
            var removedIds = removedSteps.Select(step => step.Id).ToHashSet();
            var removedRoots = removedSteps
                .Select(step => step.StableSemanticRootId ?? step.Id)
                .Append(correctionRoot)
                .ToHashSet();
            state.Evidence = state.Evidence
                .Where(item =>
                    item.CriterionId == "AC-001" &&
                    !removedIds.Contains(item.ProducerStepId))
                .ToList();
            state.EvidenceProcessing = state.EvidenceProcessing
                .Where(item => !removedIds.Contains(item.ProducerStepId))
                .Select(item => new OutcomeEvidenceProcessing(
                    item.ProducerStepId,
                    item.AcceptancePlanHash,
                    item.ProducerRole,
                    item.CriterionIds
                        .Where(id => id == "AC-001")
                        .ToArray(),
                    item.ProcessedAt))
                .Where(item => item.CriterionIds.Count > 0)
                .ToList();
            state.ProcessedSemanticRootIds.RemoveAll(removedRoots.Contains);
            state.CurrentCandidate = null;
            state.Publication = null;
            state.VerifiedCandidateFingerprint = null;
            state.VerifiedAt = null;
            state.PendingOwnerRoles.Clear();
            state.ActiveQaRound = null;
            state.ActiveQaStepId = null;
            state.ActiveQaContextPath = null;
            state.ActiveQaContextHash = null;
            state.Status = OutcomeVerificationStatus.Correcting;
            state.Stale = true;
            flow.OutcomeVerificationJson =
                OutcomeVerificationRules.SerializeAggregate(state);
            flow.Status = FlowStatus.Queued;
            database.GateRecords.RemoveRange(
                flow.GateRecords.Where(gate => removedIds.Contains(gate.FlowStepId)));
            database.FlowSteps.RemoveRange(removedSteps);
            await database.SaveChangesAsync();
        }

        await fixture.Engine.ReconcileCompletedOutcomeArtifactsAsync(
            fixture.FlowId,
            CancellationToken.None);
        await fixture.Engine.ReconcileCompletedOutcomeArtifactsAsync(
            fixture.FlowId,
            CancellationToken.None);

        var reconciled = OutcomeVerificationRules.DeserializeAggregate(
            (await ReadFlowAsync(fixture)).OutcomeVerificationJson);
        Assert.Equal(2, reconciled.AcceptancePlan!.Criteria.Count);
        Assert.Equal(correctionRoot, reconciled.AcceptancePlan.SourceStepId);
        Assert.Equal(
            1,
            reconciled.ProcessedSemanticRootIds.Count(id => id == correctionRoot));

        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        var steps = await verify.FlowSteps
            .Where(step => step.FlowRunId == fixture.FlowId)
            .ToListAsync();
        Assert.Single(steps, step =>
            step.Kind == FlowStepKind.OutcomePlanCorrection);
        Assert.Single(steps, step =>
            step.Kind == FlowStepKind.OutcomeQa &&
            step.OutcomeQaRound == 1);
    }

    [Fact]
    public async Task CrashAfterSecondPlanReplacement_ReconcilesLatestCorrectionOnce()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.FAIL, OutcomeQaVerdict.FAIL, OutcomeQaVerdict.PASS],
            maxRounds: 4,
            doublePlanGap: true);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        Guid secondCorrectionRoot;
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows
                .Include(item => item.Steps)
                .Include(item => item.GateRecords)
                .SingleAsync(item => item.Id == fixture.FlowId);
            var corrections = flow.Steps
                .Where(step => step.Kind == FlowStepKind.OutcomePlanCorrection)
                .OrderBy(step => step.Sequence)
                .ToArray();
            Assert.Equal(2, corrections.Length);
            var firstCorrection = corrections[0];
            var secondCorrection = corrections[1];
            secondCorrectionRoot = secondCorrection.StableSemanticRootId ??
                                   secondCorrection.Id;
            var state = OutcomeVerificationRules.DeserializeAggregate(
                flow.OutcomeVerificationJson);
            var firstPlan = OutcomeVerificationRules.ParseAcceptancePlan(
                firstCorrection.OutputSummary,
                state.PlannedRoles);
            state.AcceptancePlan =
                OutcomeVerificationRules.CreateAcceptanceSnapshot(
                    firstPlan,
                    firstCorrection.Id);
            state.Rounds = state.Rounds.Take(2).ToList();
            state.Rounds[0].Stale = true;
            state.Rounds[1].Stale = false;
            state.Evidence = state.Evidence
                .Where(item => item.CriterionId is "AC-001" or "AC-002")
                .ToList();
            state.EvidenceProcessing = state.EvidenceProcessing
                .Select(item => new OutcomeEvidenceProcessing(
                    item.ProducerStepId,
                    item.AcceptancePlanHash,
                    item.ProducerRole,
                    item.CriterionIds
                        .Where(id => id is "AC-001" or "AC-002")
                        .ToArray(),
                    item.ProcessedAt))
                .Where(item => item.CriterionIds.Count > 0)
                .ToList();
            var removedSteps = flow.Steps
                .Where(step => step.Sequence > secondCorrection.Sequence)
                .ToArray();
            var removedIds = removedSteps.Select(step => step.Id).ToHashSet();
            var removedRoots = removedSteps
                .Select(step => step.StableSemanticRootId ?? step.Id)
                .Append(secondCorrectionRoot)
                .ToHashSet();
            state.ProcessedSemanticRootIds.RemoveAll(removedRoots.Contains);
            state.CurrentCandidate = null;
            state.Publication = null;
            state.VerifiedCandidateFingerprint = null;
            state.VerifiedAt = null;
            state.PendingOwnerRoles.Clear();
            state.ActiveQaRound = null;
            state.ActiveQaStepId = null;
            state.ActiveQaContextPath = null;
            state.ActiveQaContextHash = null;
            state.Status = OutcomeVerificationStatus.Correcting;
            state.Stale = true;
            flow.OutcomeVerificationJson =
                OutcomeVerificationRules.SerializeAggregate(state);
            flow.Status = FlowStatus.Queued;
            database.GateRecords.RemoveRange(
                flow.GateRecords.Where(gate => removedIds.Contains(gate.FlowStepId)));
            database.FlowSteps.RemoveRange(removedSteps);
            await database.SaveChangesAsync();
        }

        await fixture.Engine.ReconcileCompletedOutcomeArtifactsAsync(
            fixture.FlowId,
            CancellationToken.None);
        await fixture.Engine.ReconcileCompletedOutcomeArtifactsAsync(
            fixture.FlowId,
            CancellationToken.None);

        var reconciled = OutcomeVerificationRules.DeserializeAggregate(
            (await ReadFlowAsync(fixture)).OutcomeVerificationJson);
        Assert.Equal(3, reconciled.AcceptancePlan!.Criteria.Count);
        Assert.Equal(
            secondCorrectionRoot,
            reconciled.AcceptancePlan.SourceStepId);
        Assert.Equal(
            1,
            reconciled.ProcessedSemanticRootIds.Count(id =>
                id == secondCorrectionRoot));

        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        var finalSteps = await verify.FlowSteps
            .Where(step => step.FlowRunId == fixture.FlowId)
            .ToListAsync();
        Assert.Equal(2, finalSteps.Count(step =>
            step.Kind == FlowStepKind.OutcomePlanCorrection));
        Assert.Single(finalSteps, step =>
            step.Kind == FlowStepKind.OutcomeQa &&
            step.OutcomeQaRound == 1);
    }

    [Fact]
    public async Task CrashAfterLaterCandidateRefresh_RecoversExpectedRefreshOnce()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts:
            [
                OutcomeQaVerdict.FAIL,
                OutcomeQaVerdict.FAIL,
                OutcomeQaVerdict.PASS
            ],
            maxRounds: 4);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        Guid refreshRoot;
        Guid refreshStepId;
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows
                .Include(item => item.Steps)
                .Include(item => item.GateRecords)
                .SingleAsync(item => item.Id == fixture.FlowId);
            var refresh = flow.Steps.Single(step =>
                step.Kind == FlowStepKind.OutcomeCandidateRefresh &&
                step.OutcomeQaRound == 2);
            var qa3 = flow.Steps.Single(step =>
                step.Kind == FlowStepKind.OutcomeQa &&
                step.OutcomeQaRound == 3);
            refreshStepId = refresh.Id;
            refreshRoot = refresh.StableSemanticRootId ?? refresh.Id;
            var qa3Root = qa3.StableSemanticRootId ?? qa3.Id;
            var state = OutcomeVerificationRules.DeserializeAggregate(
                flow.OutcomeVerificationJson);
            state.Rounds = state.Rounds.Take(2).ToList();
            state.ProcessedSemanticRootIds.RemoveAll(id =>
                id == refreshRoot || id == qa3Root);
            state.CurrentCandidate = null;
            state.Publication = null;
            state.VerifiedCandidateFingerprint = null;
            state.VerifiedAt = null;
            state.PendingOwnerRoles.Clear();
            state.ActiveQaRound = null;
            state.ActiveQaStepId = null;
            state.ActiveQaContextPath = null;
            state.ActiveQaContextHash = null;
            state.Status = OutcomeVerificationStatus.AwaitingCandidateRefresh;
            state.Stale = true;
            flow.OutcomeVerificationJson =
                OutcomeVerificationRules.SerializeAggregate(state);
            flow.Status = FlowStatus.Queued;
            database.GateRecords.RemoveRange(
                flow.GateRecords.Where(gate => gate.FlowStepId == qa3.Id));
            database.FlowSteps.Remove(qa3);
            await database.SaveChangesAsync();
        }

        await fixture.Engine.ReconcileCompletedOutcomeArtifactsAsync(
            fixture.FlowId,
            CancellationToken.None);
        await fixture.Engine.ReconcileCompletedOutcomeArtifactsAsync(
            fixture.FlowId,
            CancellationToken.None);

        var recovered = OutcomeVerificationRules.DeserializeAggregate(
            (await ReadFlowAsync(fixture)).OutcomeVerificationJson);
        Assert.Equal(refreshStepId, recovered.CurrentCandidate?.PreparedByStepId);
        Assert.Equal(OutcomeVerificationStatus.AwaitingQa, recovered.Status);
        Assert.Equal(
            1,
            recovered.ProcessedSemanticRootIds.Count(id => id == refreshRoot));

        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        var steps = await verify.FlowSteps
            .Where(step => step.FlowRunId == fixture.FlowId)
            .ToListAsync();
        Assert.Single(steps, step =>
            step.Kind == FlowStepKind.OutcomeCandidateRefresh &&
            step.OutcomeQaRound == 2);
        Assert.Single(steps, step =>
            step.Kind == FlowStepKind.OutcomeQa &&
            step.OutcomeQaRound == 3);
    }

    [Fact]
    public async Task PlanReplacement_UsesDeterministicOwnerUnionForUnresolvedExistingAndNewCriteria()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.FAIL],
            maxRounds: 3,
            multiFindingPlanGapFirst: true,
            failFirstReplacementCorrection: true);

        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        await using var database = await fixture.Factory.CreateDbContextAsync();
        var flow = await database.Flows
            .Include(item => item.Steps)
            .SingleAsync(item => item.Id == fixture.FlowId);
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        var latestRound = Assert.Single(state.Rounds);

        Assert.Equal(FlowStatus.Failed, flow.Status);
        Assert.Equal(OutcomeVerificationStatus.Correcting, state.Status);
        Assert.Empty(latestRound.Result!.Criteria.Single().ResponsibleRoles);
        Assert.Equal(
            ["architect", "data-engineer", "software-engineer"],
            state.PendingOwnerRoles);
        Assert.Contains(
            state.Evidence,
            item => item.CriterionId == "AC-001");
        Assert.Equal(
            ["architect", "data-engineer", "software-engineer"],
            flow.Steps
                .Where(item => item.Kind == FlowStepKind.OutcomeOwnerCorrection)
                .OrderBy(item => item.Sequence)
                .Select(item => item.AgentRole));
    }

    [Fact]
    public async Task InvalidQaContract_ConsumesRoundWithoutConsumingRuntimeAttempts()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.FAIL, OutcomeQaVerdict.PASS],
            maxRounds: 3,
            qaExecutionAttempts: 2,
            invalidQaFirst: true);

        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        await using var database = await fixture.Factory.CreateDbContextAsync();
        var flow = await database.Flows
            .Include(item => item.Steps)
            .SingleAsync(item => item.Id == fixture.FlowId);
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        var qaSteps = flow.Steps
            .Where(item => item.AgentRole == "quality-engineer")
            .OrderBy(item => item.Sequence)
            .ToArray();

        Assert.Equal(2, state.Rounds.Count);
        Assert.Null(state.Rounds[0].Result);
        Assert.NotEmpty(state.Rounds[0].ContractError);
        Assert.Equal(OutcomeQaVerdict.PASS, state.Rounds[1].Result?.Verdict);
        Assert.All(qaSteps, step => Assert.Equal(2, step.ExecutionAttempts));
        Assert.Equal(qaSteps[0].CopilotSessionId, qaSteps[1].CopilotSessionId);
        Assert.Single(flow.Steps, item =>
            item.Label == WorkflowEngine.ReleaseCandidateLabel);
    }

    [Fact]
    public async Task PassWithNonzeroCommandExit_ConsumesRoundAndCreatesNoReleaseGate()
    {
        await AssertRejectedHostObservationAsync(
            QaObservationMode.NonzeroExit,
            "must report ExitCode 0");
    }

    [Fact]
    public async Task PassWithNoHostTools_ConsumesRoundAndCreatesNoReleaseGate()
    {
        await AssertRejectedHostObservationAsync(
            QaObservationMode.Empty,
            "does not match a successful host-observed command");
    }

    [Fact]
    public async Task PassWithMismatchedToolLocator_ConsumesRoundAndCreatesNoReleaseGate()
    {
        await AssertRejectedHostObservationAsync(
            QaObservationMode.LocatorMismatch,
            "does not match a successful host-observed command");
    }

    [Fact]
    public async Task QaContextMutation_ConsumesRoundAndCreatesNoReleaseGate()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.PASS],
            maxRounds: 1,
            mutateQaContextDuringFirstQa: true);

        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        await using var database = await fixture.Factory.CreateDbContextAsync();
        var flow = await database.Flows
            .Include(item => item.GateRecords)
            .SingleAsync(item => item.Id == fixture.FlowId);
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        var round = Assert.Single(state.Rounds);
        Assert.Null(round.Result);
        Assert.Contains(
            "context no longer matches",
            round.ContractError,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            flow.GateRecords,
            gate => gate.ActionType == HandoffActionType.Release);
    }

    [Fact]
    public async Task MissingQaVerdict_ConsumesRoundAndCannotCreateReleaseGate()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.FAIL],
            maxRounds: 1,
            invalidQaFirst: true);

        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        await using var database = await fixture.Factory.CreateDbContextAsync();
        var flow = await database.Flows
            .Include(item => item.GateRecords)
            .SingleAsync(item => item.Id == fixture.FlowId);
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        Assert.Single(state.Rounds);
        Assert.Null(state.Rounds[0].Result);
        Assert.Contains("Verdict is required", state.Rounds[0].ContractError);
        Assert.Equal(
            OutcomeVerificationStatus.AwaitingHumanResolution,
            state.Status);
        Assert.DoesNotContain(
            flow.GateRecords,
            item => item.ActionType == HandoffActionType.Release);
    }

    [Fact]
    public async Task NullQaCriterionResult_ConsumesRoundAndCannotCreateReleaseGate()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.FAIL],
            maxRounds: 1,
            nullQaCriterionFirst: true);

        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        await using var database = await fixture.Factory.CreateDbContextAsync();
        var flow = await database.Flows
            .Include(item => item.GateRecords)
            .SingleAsync(item => item.Id == fixture.FlowId);
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        Assert.Single(state.Rounds);
        Assert.Null(state.Rounds[0].Result);
        Assert.Contains(
            "criterion result 1 is null",
            state.Rounds[0].ContractError);
        Assert.Equal(
            OutcomeVerificationStatus.AwaitingHumanResolution,
            state.Status);
        Assert.DoesNotContain(
            flow.GateRecords,
            item => item.ActionType == HandoffActionType.Release);
    }

    [Fact]
    public async Task InvalidQaDiagnostics_AreBoundedAndDurablyConsumeRound()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.FAIL],
            maxRounds: 1,
            manyInvalidQaDiagnosticsFirst: true);

        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        await using var database = await fixture.Factory.CreateDbContextAsync();
        var flow = await database.Flows
            .Include(item => item.GateRecords)
            .SingleAsync(item => item.Id == fixture.FlowId);
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        Assert.True(
            state.Rounds.Count == 1,
            $"Expected one consumed QA round, found {state.Rounds.Count}. " +
            $"Flow={flow.Status}; outcome={state.Status}; failure={flow.FailureReason}");
        var round = state.Rounds.Single();
        Assert.Null(round.Result);
        Assert.InRange(
            round.ContractError.Length,
            1,
            WorkflowEngine.MaximumQaContractErrorCharacters);
        Assert.Contains("diagnostics; sha256:", round.ContractError);
        Assert.Equal(
            OutcomeVerificationStatus.AwaitingHumanResolution,
            state.Status);
        Assert.DoesNotContain(
            flow.GateRecords,
            item => item.ActionType == HandoffActionType.Release);
    }

    [Fact]
    public async Task CustomerApproval_RechecksFingerprintAndPublishesNothingWhenStale()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.PASS],
            maxRounds: 3);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Workspace, "tracked.txt"),
            "changed after QA");

        var result = await fixture.DecideCurrentAsync(approve: true);

        Assert.Equal(ReleaseDecisionOutcome.RefreshQueued, result.Outcome);
        Assert.Equal(FlowStatus.Queued, result.Flow.Status);
        Assert.Equal(
            "AwaitingCandidateRefresh",
            result.Flow.OutcomeVerification.Status);
        Assert.DoesNotContain(
            result.Flow.Steps,
            step => step.Label == WorkflowEngine.ApprovedPublicationLabel);
        Assert.Contains(
            result.Flow.Events,
            item => item.Type == "outcome.candidate.stale");
    }

    [Fact]
    public async Task CustomerApproval_SaveFailureLeavesExactGateDecisionRetryable()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.PASS],
            maxRounds: 3);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        var binding = await ReadDecisionBindingAsync(fixture);
        await InstallGateFailureTriggerAsync(fixture);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            fixture.CreateFeedbackCoordinator().DecideAsync(
                fixture.FlowId,
                approve: true,
                binding.GateId,
                binding.CandidateFingerprint,
                string.Empty,
                CancellationToken.None));
        await AssertDecisionStillRetryableAsync(
            fixture,
            binding,
            expectedIteration: 1);

        await DropGateFailureTriggerAsync(fixture);
        var retry = await fixture.CreateFeedbackCoordinator().DecideAsync(
            fixture.FlowId,
            approve: true,
            binding.GateId,
            binding.CandidateFingerprint,
            string.Empty,
            CancellationToken.None);
        Assert.Equal(ReleaseDecisionOutcome.Approved, retry.Outcome);
    }

    [Fact]
    public async Task CustomerRejection_SaveFailureLeavesExactGateDecisionRetryable()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.PASS],
            maxRounds: 3);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        var binding = await ReadDecisionBindingAsync(fixture);
        const string feedback = "Keep the same durable rejection for retry.";
        await InstallGateFailureTriggerAsync(fixture);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            fixture.CreateFeedbackCoordinator().DecideAsync(
                fixture.FlowId,
                approve: false,
                binding.GateId,
                binding.CandidateFingerprint,
                feedback,
                CancellationToken.None));
        await AssertDecisionStillRetryableAsync(
            fixture,
            binding,
            expectedIteration: 1,
            absentMessage: feedback);

        await DropGateFailureTriggerAsync(fixture);
        var retry = await fixture.CreateFeedbackCoordinator().DecideAsync(
            fixture.FlowId,
            approve: false,
            binding.GateId,
            binding.CandidateFingerprint,
            feedback,
            CancellationToken.None);
        Assert.Equal(ReleaseDecisionOutcome.Rejected, retry.Outcome);
        Assert.Equal(2, retry.Flow.Iteration);
    }

    [Fact]
    public async Task StaleSupersession_SaveFailureLeavesExactGateDecisionRetryable()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.PASS],
            maxRounds: 3);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        var binding = await ReadDecisionBindingAsync(fixture);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Workspace, "tracked.txt"),
            "changed after QA");
        await InstallGateFailureTriggerAsync(fixture);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            fixture.CreateFeedbackCoordinator().DecideAsync(
                fixture.FlowId,
                approve: true,
                binding.GateId,
                binding.CandidateFingerprint,
                string.Empty,
                CancellationToken.None));
        await AssertDecisionStillRetryableAsync(
            fixture,
            binding,
            expectedIteration: 1);

        await DropGateFailureTriggerAsync(fixture);
        var retry = await fixture.CreateFeedbackCoordinator().DecideAsync(
            fixture.FlowId,
            approve: true,
            binding.GateId,
            binding.CandidateFingerprint,
            string.Empty,
            CancellationToken.None);
        Assert.Equal(ReleaseDecisionOutcome.RefreshQueued, retry.Outcome);
    }

    [Fact]
    public async Task StaleTabCannotApproveNewlyVerifiedGateAndApiReturnsConflict()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.PASS, OutcomeQaVerdict.PASS],
            maxRounds: 3);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        FlowDecisionBinding reviewedA;
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows
                .Include(item => item.Steps)
                .Include(item => item.GateRecords)
                .SingleAsync(item => item.Id == fixture.FlowId);
            var outcome = flow.ToOutcomeVerificationDto();
            reviewedA = new FlowDecisionBinding(
                outcome.ReleaseGateId!.Value,
                outcome.CandidateFingerprint);
        }
        var fingerprintConflict = await fixture.DecideCurrentAsync(
            approve: true,
            gateId: reviewedA.GateId,
            candidateFingerprint: $"sha256:{new string('f', 64)}");
        Assert.Equal(
            ReleaseDecisionOutcome.Conflict,
            fingerprintConflict.Outcome);
        var gateConflict = await fixture.DecideCurrentAsync(
            approve: true,
            gateId: Guid.NewGuid(),
            candidateFingerprint: reviewedA.CandidateFingerprint);
        Assert.Equal(ReleaseDecisionOutcome.Conflict, gateConflict.Outcome);
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            Assert.False((await database.GateRecords.SingleAsync(gate =>
                gate.Id == reviewedA.GateId)).Resolved);
            Assert.DoesNotContain(
                await database.FlowSteps.ToListAsync(),
                step => step.Label == WorkflowEngine.ApprovedPublicationLabel);
        }
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Workspace, "tracked.txt"),
            "changed after tab A rendered");
        var refresh = await fixture.DecideCurrentAsync(
            approve: true,
            gateId: reviewedA.GateId,
            candidateFingerprint: reviewedA.CandidateFingerprint);
        Assert.Equal(ReleaseDecisionOutcome.RefreshQueued, refresh.Outcome);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        FlowDecisionBinding reviewedB;
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows
                .Include(item => item.Steps)
                .Include(item => item.GateRecords)
                .SingleAsync(item => item.Id == fixture.FlowId);
            var outcome = flow.ToOutcomeVerificationDto();
            reviewedB = new FlowDecisionBinding(
                outcome.ReleaseGateId!.Value,
                outcome.CandidateFingerprint);
        }
        Assert.NotEqual(reviewedA, reviewedB);

        var staleDecision = await fixture.DecideCurrentAsync(
            approve: true,
            gateId: reviewedA.GateId,
            candidateFingerprint: reviewedA.CandidateFingerprint);

        Assert.Equal(ReleaseDecisionOutcome.Conflict, staleDecision.Outcome);
        Assert.Equal(FlowStatus.WaitingForFeedback, staleDecision.Flow.Status);
        var staleRejection = await fixture.DecideCurrentAsync(
            approve: false,
            gateId: reviewedA.GateId,
            candidateFingerprint: reviewedA.CandidateFingerprint,
            feedback: "This stale feedback must not be persisted.");
        Assert.Equal(ReleaseDecisionOutcome.Conflict, staleRejection.Outcome);
        var apiResult = DemoApi.ToFlowDecisionResult(staleDecision);
        Assert.Equal(
            StatusCodes.Status409Conflict,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(apiResult).StatusCode);
        Assert.Same(
            staleDecision,
            Assert.IsAssignableFrom<IValueHttpResult>(apiResult).Value);
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        var currentGate = await verify.GateRecords.SingleAsync(gate =>
            gate.Id == reviewedB.GateId);
        Assert.False(currentGate.Resolved);
        Assert.DoesNotContain(
            await verify.FlowSteps.ToListAsync(),
            step => step.Label == WorkflowEngine.ApprovedPublicationLabel);
        Assert.DoesNotContain(
            await verify.FlowMessages.ToListAsync(),
            message => message.Content.Contains(
                "stale feedback",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task CustomerRejection_StartsFreshVerifiedIterationWithAuditHistory()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.PASS],
            maxRounds: 3);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        var result = await fixture.DecideCurrentAsync(approve: false);
        var state = OutcomeVerificationRules.DeserializeAggregate(
            (await ReadFlowAsync(fixture)).OutcomeVerificationJson);

        Assert.Equal(ReleaseDecisionOutcome.Rejected, result.Outcome);
        Assert.Equal(2, result.Flow.Iteration);
        Assert.Equal(OutcomeVerificationStatus.Planning, state.Status);
        Assert.Equal(2, state.Iteration);
        Assert.Single(state.PriorIterations);
        Assert.Equal(
            OutcomeVerificationStatus.Superseded,
            state.PriorIterations[0].Status);
    }

    [Fact]
    public async Task QaMutation_InvalidatesResultAndForcesCandidateRefresh()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.PASS, OutcomeQaVerdict.PASS],
            maxRounds: 3,
            mutateDuringFirstQa: true);

        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        await using var database = await fixture.Factory.CreateDbContextAsync();
        var flow = await database.Flows
            .Include(item => item.Steps)
            .Include(item => item.GateRecords)
            .SingleAsync(item => item.Id == fixture.FlowId);
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);

        Assert.Equal(2, state.Rounds.Count);
        Assert.True(state.Rounds[0].Stale);
        Assert.False(state.Rounds[1].Stale);
        Assert.Single(flow.Steps, item =>
            item.Label.StartsWith(
                WorkflowEngine.OutcomeCandidateRefreshLabelPrefix,
                StringComparison.Ordinal));
        Assert.Single(flow.GateRecords, item =>
            item.ActionType == HandoffActionType.Release &&
            !item.Resolved);
    }

    [Fact]
    public async Task PreQaStaleness_RefreshesCandidateAndReschedulesSkippedRound()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.PASS, OutcomeQaVerdict.PASS],
            maxRounds: 3);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows
                .Include(item => item.Steps)
                .Include(item => item.GateRecords)
                .SingleAsync(item => item.Id == fixture.FlowId);
            var state = OutcomeVerificationRules.DeserializeAggregate(
                flow.OutcomeVerificationJson);
            state.Rounds.Clear();
            state.ActiveQaRound = null;
            state.ActiveQaStepId = null;
            state.ActiveQaContextPath = null;
            state.ActiveQaContextHash = null;
            state.VerifiedCandidateFingerprint = null;
            state.VerifiedAt = null;
            state.Publication = null;
            state.Stale = false;
            state.Status = OutcomeVerificationStatus.AwaitingQa;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            flow.OutcomeVerificationJson =
                OutcomeVerificationRules.SerializeAggregate(state);
            flow.Status = FlowStatus.Queued;
            flow.OutcomeLabel = string.Empty;
            flow.OutcomeUrl = string.Empty;
            var qa = flow.Steps.Single(item =>
                item.Label == $"{WorkflowEngine.OutcomeQaLabelPrefix}1)");
            qa.Status = StepStatus.Pending;
            qa.Phase = AgentRunPhase.PreparingWorkspace;
            qa.OutputSummary = string.Empty;
            qa.PushbackReason = string.Empty;
            qa.StartedAt = null;
            qa.CompletedAt = null;
            database.GateRecords.RemoveRange(flow.GateRecords);
            await database.SaveChangesAsync();
        }
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Workspace, "tracked.txt"),
            "changed before QA dispatch");

        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        await using var verify = await fixture.Factory.CreateDbContextAsync();
        var recovered = await verify.Flows
            .Include(item => item.Steps)
            .Include(item => item.Events)
            .SingleAsync(item => item.Id == fixture.FlowId);
        var recoveredState = OutcomeVerificationRules.DeserializeAggregate(
            recovered.OutcomeVerificationJson);
        Assert.Equal(OutcomeVerificationStatus.Passed, recoveredState.Status);
        Assert.Single(recoveredState.Rounds);
        Assert.Single(recovered.Steps, item =>
            item.Label.StartsWith(
                WorkflowEngine.OutcomeCandidateRefreshLabelPrefix,
                StringComparison.Ordinal));
        Assert.Single(recovered.Steps, item =>
            item.Label == $"{WorkflowEngine.OutcomeQaLabelPrefix}1)");
        Assert.Contains(
            recovered.Events,
            item => item.Type == "outcome.qa.rescheduled");
    }

    [Fact]
    public async Task RestartReconciliation_RecreatesMissingPassGateExactlyOnce()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.PASS],
            maxRounds: 3);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var gates = await database.GateRecords
                .Where(item => item.ActionType == HandoffActionType.Release)
                .ToListAsync();
            database.GateRecords.RemoveRange(gates);
            await database.SaveChangesAsync();
        }

        await fixture.Engine.RecoverInterruptedFlowsAsync(CancellationToken.None);
        await fixture.Engine.RecoverInterruptedFlowsAsync(CancellationToken.None);

        await using var verify = await fixture.Factory.CreateDbContextAsync();
        Assert.Single(
            await verify.GateRecords
                .Where(item => item.ActionType == HandoffActionType.Release)
                .ToListAsync());
    }

    [Fact]
    public async Task RestartReconciliation_InvalidatesStalePassAndQueuesRefresh()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.PASS, OutcomeQaVerdict.PASS],
            maxRounds: 3);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Workspace, "tracked.txt"),
            "changed while stopped");

        var recovered = await fixture.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);

        Assert.Contains(fixture.FlowId, recovered);
        await using var database = await fixture.Factory.CreateDbContextAsync();
        var flow = await database.Flows
            .Include(item => item.GateRecords)
            .SingleAsync(item => item.Id == fixture.FlowId);
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        Assert.Equal(FlowStatus.Queued, flow.Status);
        Assert.Equal(
            OutcomeVerificationStatus.AwaitingCandidateRefresh,
            state.Status);
        Assert.Null(state.VerifiedCandidateFingerprint);
        Assert.All(
            flow.GateRecords.Where(item =>
                item.ActionType == HandoffActionType.Release),
            gate => Assert.True(gate.Resolved));
    }

    [Fact]
    public async Task PreviewFreshnessCheck_InvalidatesStalePassBeforeServingContent()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.PASS, OutcomeQaVerdict.PASS],
            maxRounds: 3);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Workspace, "tracked.txt"),
            "changed before preview access");

        var current = await fixture.Engine.EnsureVerifiedCandidateCurrentAsync(
            fixture.FlowId,
            CancellationToken.None);

        Assert.False(current);
        await using var database = await fixture.Factory.CreateDbContextAsync();
        var flow = await database.Flows
            .Include(item => item.GateRecords)
            .SingleAsync(item => item.Id == fixture.FlowId);
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        Assert.Equal(FlowStatus.Queued, flow.Status);
        Assert.Equal(
            OutcomeVerificationStatus.AwaitingCandidateRefresh,
            state.Status);
        Assert.All(
            flow.GateRecords.Where(item =>
                item.ActionType == HandoffActionType.Release),
            gate => Assert.True(gate.Resolved));
    }

    [Fact]
    public async Task ApprovedPreviewDrift_DoesNotReopenPublishedFlow()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.PASS],
            maxRounds: 3);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows.SingleAsync(item => item.Id == fixture.FlowId);
            flow.Status = FlowStatus.Approved;
            flow.OutcomeUrl = "https://github.com/example/repo/pull/1";
            flow.OutcomeLabel = "Published pull request #1";
            await database.SaveChangesAsync();
        }
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Workspace, "tracked.txt"),
            "local drift after publication");

        var current = await fixture.Engine.EnsureVerifiedCandidateCurrentAsync(
            fixture.FlowId,
            CancellationToken.None);

        Assert.False(current);
        var flowAfter = await ReadFlowAsync(fixture);
        Assert.Equal(FlowStatus.Approved, flowAfter.Status);
        Assert.Equal(
            "https://github.com/example/repo/pull/1",
            flowAfter.OutcomeUrl);
        Assert.Equal(
            "Published pull request #1",
            flowAfter.OutcomeLabel);
    }

    [Fact]
    public async Task RestartReconciliation_MergesEvidenceAndCandidateWithoutDuplicates()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.PASS],
            maxRounds: 3);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows.SingleAsync(item => item.Id == fixture.FlowId);
            var prior = OutcomeVerificationRules.DeserializeAggregate(
                flow.OutcomeVerificationJson);
            var reset = OutcomeVerificationRules.CreateInitialState(1, 3);
            reset.Status = OutcomeVerificationStatus.CollectingEvidence;
            reset.TrustedRepositories = prior.TrustedRepositories.ToList();
            reset.PlannedRoles = prior.PlannedRoles.ToList();
            reset.AcceptancePlan = prior.AcceptancePlan;
            flow.OutcomeVerificationJson =
                OutcomeVerificationRules.SerializeAggregate(reset);
            var qa = await database.FlowSteps.SingleAsync(item =>
                item.AgentRole == "quality-engineer");
            qa.Status = StepStatus.Pending;
            qa.OutputSummary = string.Empty;
            database.GateRecords.RemoveRange(await database.GateRecords
                .Where(item => item.ActionType == HandoffActionType.Release)
                .ToListAsync());
            await database.SaveChangesAsync();
        }

        await fixture.Engine.ReconcileCompletedOutcomeArtifactsAsync(
            fixture.FlowId,
            CancellationToken.None);
        await fixture.Engine.ReconcileCompletedOutcomeArtifactsAsync(
            fixture.FlowId,
            CancellationToken.None);

        var recovered = await ReadFlowAsync(fixture);
        var state = OutcomeVerificationRules.DeserializeAggregate(
            recovered.OutcomeVerificationJson);
        Assert.Single(state.Evidence);
        Assert.NotNull(state.CurrentCandidate);
        Assert.Equal(OutcomeVerificationStatus.AwaitingQa, state.Status);
    }

    [Fact]
    public async Task RestartReconciliation_ParsesCompletedQaExactlyOnce()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.PASS],
            maxRounds: 3);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows.SingleAsync(item => item.Id == fixture.FlowId);
            var state = OutcomeVerificationRules.DeserializeAggregate(
                flow.OutcomeVerificationJson);
            var priorRound = Assert.Single(state.Rounds);
            state.Rounds.Clear();
            state.Status = OutcomeVerificationStatus.AwaitingQa;
            state.VerifiedCandidateFingerprint = null;
            state.VerifiedAt = null;
            state.ActiveQaRound = 1;
            state.ActiveQaStepId = priorRound.QaStepId;
            state.ActiveQaContextHash = priorRound.ContextHash;
            state.ActiveQaContextPath = Path.Combine(
                OutcomeVerificationContextBuilder.ResolveContextDirectory(
                    fixture.Workspace,
                    state.CurrentCandidate!.Fingerprint),
                "qa-context.json");
            flow.OutcomeVerificationJson =
                OutcomeVerificationRules.SerializeAggregate(state);
            flow.Status = FlowStatus.Queued;
            var qa = await database.FlowSteps.SingleAsync(item =>
                item.Id == priorRound.QaStepId);
            qa.Label = "Recovered QA turn with migrated metadata";
            qa.Kind = FlowStepKind.OutcomeQa;
            database.GateRecords.RemoveRange(await database.GateRecords
                .Where(item => item.ActionType == HandoffActionType.Release)
                .ToListAsync());
            await database.SaveChangesAsync();
        }

        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        await using var verify = await fixture.Factory.CreateDbContextAsync();
        var flowAfter = await verify.Flows
            .Include(item => item.GateRecords)
            .SingleAsync(item => item.Id == fixture.FlowId);
        var stateAfter = OutcomeVerificationRules.DeserializeAggregate(
            flowAfter.OutcomeVerificationJson);
        Assert.Single(stateAfter.Rounds);
        Assert.Single(flowAfter.GateRecords, item =>
            item.ActionType == HandoffActionType.Release &&
            !item.Resolved);
    }

    [Fact]
    public async Task ManualQaRestart_RebindsActiveRoundWithoutConsumingAnExtraRound()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.PASS],
            maxRounds: 3);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        Guid originalQaStepId;
        Guid stableRootId;
        string planHash;
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows
                .Include(item => item.GateRecords)
                .SingleAsync(item => item.Id == fixture.FlowId);
            var state = OutcomeVerificationRules.DeserializeAggregate(
                flow.OutcomeVerificationJson);
            var priorRound = Assert.Single(state.Rounds);
            var qa = await database.FlowSteps.SingleAsync(item =>
                item.Id == priorRound.QaStepId);
            originalQaStepId = qa.Id;
            stableRootId = qa.StableSemanticRootId ?? qa.Id;
            planHash = qa.OutcomePlanHash;

            qa.Label = "Failed QA turn with renamed label";
            qa.Kind = FlowStepKind.OutcomeQa;
            qa.Status = StepStatus.Failed;
            qa.Phase = AgentRunPhase.Stalled;
            qa.OutputSummary = string.Empty;
            qa.CompletedAt = DateTimeOffset.UtcNow;
            qa.CopilotSessionId = null;
            qa.CopilotSessionHome = string.Empty;
            flow.Status = FlowStatus.Failed;
            flow.FailureReason = "QA runtime failed after dispatch.";
            flow.OutcomeLabel = string.Empty;
            flow.OutcomeUrl = string.Empty;
            state.Rounds.Clear();
            state.Status = OutcomeVerificationStatus.AwaitingQa;
            state.VerifiedCandidateFingerprint = null;
            state.VerifiedAt = null;
            state.ActiveQaRound = priorRound.Round;
            state.ActiveQaStepId = qa.Id;
            state.ActiveQaContextHash = priorRound.ContextHash;
            state.ActiveQaContextPath = Path.Combine(
                fixture.Workspace,
                ".ai-harness",
                "outcome-verification",
                "manual-retry",
                "qa-context.json");
            flow.OutcomeVerificationJson =
                OutcomeVerificationRules.SerializeAggregate(state);
            database.GateRecords.RemoveRange(flow.GateRecords);
            await database.SaveChangesAsync();
        }

        var restarted = await fixture.Engine.RestartFailedFlowAsync(
            fixture.FlowId,
            CancellationToken.None);
        var retry = restarted.Steps
            .Where(step =>
                step.Kind == FlowStepKind.OutcomeQa &&
                step.Id != originalQaStepId)
            .Single();
        var restartedState = OutcomeVerificationRules.DeserializeAggregate(
            restarted.OutcomeVerificationJson);

        Assert.Equal(1, restartedState.ActiveQaRound);
        Assert.Equal(retry.Id, restartedState.ActiveQaStepId);
        Assert.Equal(1, retry.OutcomeQaRound);
        Assert.Equal(planHash, retry.OutcomePlanHash);
        Assert.Equal(stableRootId, retry.StableSemanticRootId);

        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        await using var verify = await fixture.Factory.CreateDbContextAsync();
        var flowAfter = await verify.Flows
            .Include(item => item.GateRecords)
            .SingleAsync(item => item.Id == fixture.FlowId);
        var stateAfter = OutcomeVerificationRules.DeserializeAggregate(
            flowAfter.OutcomeVerificationJson);
        Assert.Single(stateAfter.Rounds);
        Assert.Equal(1, stateAfter.Rounds[0].Round);
        Assert.Equal(retry.Id, stateAfter.Rounds[0].QaStepId);
        Assert.Single(flowAfter.GateRecords, item =>
            item.ActionType == HandoffActionType.Release &&
            !item.Resolved);
    }

    [Fact]
    public async Task Continue_RequiresReasonAndNeverForcePasses()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.FAIL],
            maxRounds: 1);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        Guid gateId;
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            gateId = await database.GateRecords
                .Where(item =>
                    item.ActionType == HandoffActionType.OutcomeResolution &&
                    !item.Resolved)
                .Select(item => item.Id)
                .SingleAsync();
        }

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Engine.ResolveOutcomeAsync(
                fixture.FlowId,
                gateId,
                OutcomeResolutionAction.Continue,
                " ",
                CancellationToken.None));

        var flow = await ReadFlowAsync(fixture);
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        Assert.Equal(OutcomeVerificationStatus.AwaitingHumanResolution, state.Status);
        Assert.Null(state.VerifiedCandidateFingerprint);
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        Assert.DoesNotContain(
            await verify.GateRecords.ToListAsync(),
            gate => gate.ActionType == HandoffActionType.Release);
    }

    [Fact]
    public async Task PublicationStep_IsQueuedOnlyForCurrentPassWithReleaseIdentity()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.PASS],
            maxRounds: 3);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        var result = await fixture.DecideCurrentAsync(approve: true);
        await using var database = await fixture.Factory.CreateDbContextAsync();
        var steps = await database.FlowSteps
            .Where(item => item.FlowRunId == fixture.FlowId)
            .ToListAsync();
        var localRelease = steps.Single(item =>
            item.Label == WorkflowEngine.ReleaseCandidateLabel);
        var publication = steps.Single(item =>
            item.Label == WorkflowEngine.ApprovedPublicationLabel);

        Assert.Equal(ReleaseDecisionOutcome.Approved, result.Outcome);
        Assert.Equal(FlowStatus.Queued, result.Flow.Status);
        Assert.True(publication.RemotePublicationAllowed);
        Assert.Null(publication.StartedAt);
        Assert.Equal(localRelease.AgentId, publication.AgentId);
        Assert.DoesNotContain(
            fixture.RunnerContexts,
            context => context.AllowRemotePublication);
        Assert.All(
            fixture.RunnerContexts,
            context => Assert.True(context.IsGovernedOutcomeVerification));
    }

    [Fact]
    public async Task GovernedPublication_UsesGuardedAgentThenHostPublisher()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.PASS],
            maxRounds: 3,
            enablePublication: true);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        _ = await fixture.DecideCurrentAsync(approve: true);

        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        var flow = await ReadFlowAsync(fixture);
        Assert.Equal(FlowStatus.Approved, flow.Status);
        Assert.Equal(1, fixture.PublisherCalls);
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        Assert.Equal(
            OutcomePublicationStatus.Verified,
            state.Publication?.Status);
        var publicationContext = fixture.RunnerContexts.Last(context =>
            context.AgentRole == "release-engineer");
        Assert.False(publicationContext.AllowRemotePublication);
        Assert.True(publicationContext.IsHostControlledPublication);
        await using var database = await fixture.Factory.CreateDbContextAsync();
        Assert.Contains(
            await database.FlowEvents.ToListAsync(),
            item => item.Type == "outcome.candidate.published");
    }

    [Fact]
    public async Task GovernedPublication_CompletedOutputRecovery_ReusesPublishedJournal()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.PASS],
            maxRounds: 3,
            enablePublication: true,
            failPublicationVerificationOnce: true);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        var approved = await fixture.DecideCurrentAsync(approve: true);
        var publicationStepId = approved.Flow.Steps.Single(item =>
            item.Label == WorkflowEngine.ApprovedPublicationLabel).Id;
        var copilotHome = Path.Combine(fixture.Workspace, ".copilot-home-auto-recovery");
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var step = await database.FlowSteps.SingleAsync(item => item.Id == publicationStepId);
            step.CopilotSessionHome = copilotHome;
            await database.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Engine.ExecuteStepAsync(
                fixture.FlowId,
                publicationStepId,
                fixture.Workspace,
                "Release Engineer",
                1,
                CancellationToken.None));

        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows
                .Include(item => item.Steps)
                .SingleAsync(item => item.Id == fixture.FlowId);
            var step = flow.Steps.Single(item => item.Id == publicationStepId);
            Assert.Equal(1, fixture.PublisherCalls);
            flow.Status = FlowStatus.Failed;
            flow.FailureReason = "Publication finalization failed after the journal persisted.";
            step.StartedAt = DateTimeOffset.UtcNow.AddMinutes(-3);
            step.Phase = AgentRunPhase.Stalled;
            await WriteCompletedSessionJournalAsync(
                step.CopilotSessionHome,
                step.CopilotSessionId!.Value,
                fixture.Workspace,
                step.AgentName,
                "HANDOFF_STATUS: COMPLETE\nPrepared the local candidate without publishing.");
            await database.SaveChangesAsync();
        }

        var recovered = await fixture.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);
        Assert.Contains(fixture.FlowId, recovered);

        await using var verify = await fixture.Factory.CreateDbContextAsync();
        var flowAfter = await verify.Flows
            .Include(item => item.Steps)
            .SingleAsync(item => item.Id == fixture.FlowId);
        var stateAfter = OutcomeVerificationRules.DeserializeAggregate(
            flowAfter.OutcomeVerificationJson);
        Assert.Equal(FlowStatus.Queued, flowAfter.Status);
        Assert.Equal(OutcomePublicationStatus.Verified, stateAfter.Publication?.Status);
        Assert.Equal(
            StepStatus.Completed,
            flowAfter.Steps.Single(item => item.Id == publicationStepId).Status);
        Assert.Equal(1, fixture.PublisherCalls);
    }

    [Fact]
    public async Task GovernedPublication_ManualRecovery_ReusesPublishedJournal()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.PASS],
            maxRounds: 3,
            enablePublication: true,
            failPublicationVerificationOnce: true);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        var approved = await fixture.DecideCurrentAsync(approve: true);
        var publicationStepId = approved.Flow.Steps.Single(item =>
            item.Label == WorkflowEngine.ApprovedPublicationLabel).Id;
        var copilotHome = Path.Combine(fixture.Workspace, ".copilot-home-manual-recovery");
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var step = await database.FlowSteps.SingleAsync(item => item.Id == publicationStepId);
            step.CopilotSessionHome = copilotHome;
            await database.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Engine.ExecuteStepAsync(
                fixture.FlowId,
                publicationStepId,
                fixture.Workspace,
                "Release Engineer",
                1,
                CancellationToken.None));

        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows
                .Include(item => item.Steps)
                .SingleAsync(item => item.Id == fixture.FlowId);
            var step = flow.Steps.Single(item => item.Id == publicationStepId);
            Assert.Equal(1, fixture.PublisherCalls);
            flow.Status = FlowStatus.Failed;
            flow.FailureReason = "Publication finalization failed after the journal persisted.";
            step.StartedAt = DateTimeOffset.UtcNow.AddMinutes(-3);
            step.Phase = AgentRunPhase.Stalled;
            await WriteCompletedSessionJournalAsync(
                step.CopilotSessionHome,
                step.CopilotSessionId!.Value,
                fixture.Workspace,
                step.AgentName,
                "HANDOFF_STATUS: COMPLETE\nPrepared the local candidate without publishing.");
            await database.SaveChangesAsync();
        }

        var restarted = await fixture.Engine.RestartFailedFlowAsync(
            fixture.FlowId,
            CancellationToken.None);
        Assert.Equal(FlowStatus.Queued, restarted.Status);
        Assert.Equal(1, fixture.PublisherCalls);

        await using var verify = await fixture.Factory.CreateDbContextAsync();
        var flowAfter = await verify.Flows
            .Include(item => item.Steps)
            .SingleAsync(item => item.Id == fixture.FlowId);
        var stateAfter = OutcomeVerificationRules.DeserializeAggregate(
            flowAfter.OutcomeVerificationJson);
        Assert.Equal(FlowStatus.Queued, flowAfter.Status);
        Assert.Equal(OutcomePublicationStatus.Verified, stateAfter.Publication?.Status);
        Assert.Equal(
            StepStatus.Completed,
            flowAfter.Steps.Single(item => item.Id == publicationStepId).Status);
        Assert.Equal(1, fixture.PublisherCalls);
    }

    [Fact]
    public async Task PublicationPushback_CreatesNoRemoteSideEffect()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.PASS],
            maxRounds: 3,
            enablePublication: true,
            publicationPushback: true);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        var approved = await fixture.DecideCurrentAsync(approve: true);
        var publicationStep = approved.Flow.Steps.Single(item =>
            item.Label == WorkflowEngine.ApprovedPublicationLabel);

        var completed = await fixture.Engine.ExecuteStepAsync(
            fixture.FlowId,
            publicationStep.Id,
            fixture.Workspace,
            "Release Engineer",
            1,
            CancellationToken.None);

        Assert.Equal(StepStatus.Pushback, completed.Status);
        Assert.Equal(0, fixture.PublisherCalls);
    }

    [Fact]
    public async Task ApprovedCandidateDrift_RequiresFreshQaAndCustomerApproval()
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.PASS, OutcomeQaVerdict.PASS],
            maxRounds: 3,
            enablePublication: true);
        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);
        _ = await fixture.DecideCurrentAsync(approve: true);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Workspace, "tracked.txt"),
            "changed after customer approval");

        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        await using var database = await fixture.Factory.CreateDbContextAsync();
        var flow = await database.Flows
            .Include(item => item.GateRecords)
            .Include(item => item.Events)
            .SingleAsync(item => item.Id == fixture.FlowId);
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        Assert.Equal(FlowStatus.WaitingForFeedback, flow.Status);
        Assert.Equal(OutcomeVerificationStatus.Passed, state.Status);
        Assert.Equal(0, fixture.PublisherCalls);
        Assert.Single(flow.GateRecords, gate =>
            gate.ActionType == HandoffActionType.Release &&
            !gate.Resolved);
        Assert.False(FlowAbandonmentService.HasEffectiveApprovedRelease(flow));
        Assert.Contains(
            flow.Events,
            item => item.Type == "gate.release-approval-stale");
    }

    private static async Task<FlowRun> ReadFlowAsync(OutcomeFixture fixture)
    {
        await using var database = await fixture.Factory.CreateDbContextAsync();
        return await database.Flows.SingleAsync(item => item.Id == fixture.FlowId);
    }

    private static async Task<FlowDecisionBinding> ReadDecisionBindingAsync(
        OutcomeFixture fixture)
    {
        await using var database = await fixture.Factory.CreateDbContextAsync();
        var flow = await database.Flows
            .Include(item => item.Steps)
            .Include(item => item.GateRecords)
            .SingleAsync(item => item.Id == fixture.FlowId);
        var outcome = flow.ToOutcomeVerificationDto();
        return new FlowDecisionBinding(
            outcome.ReleaseGateId ??
            throw new InvalidOperationException(
                "The fixture has no unresolved release gate."),
            outcome.CandidateFingerprint);
    }

    private static async Task InstallGateFailureTriggerAsync(
        OutcomeFixture fixture)
    {
        await using var database = await fixture.Factory.CreateDbContextAsync();
        await database.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_release_gate_decision
            BEFORE UPDATE OF Resolved ON GateRecords
            WHEN NEW.Resolved = 1
            BEGIN
                SELECT RAISE(FAIL, 'forced release gate decision failure');
            END;
            """);
    }

    private static async Task DropGateFailureTriggerAsync(
        OutcomeFixture fixture)
    {
        await using var database = await fixture.Factory.CreateDbContextAsync();
        await database.Database.ExecuteSqlRawAsync(
            "DROP TRIGGER fail_release_gate_decision;");
    }

    private static async Task AssertDecisionStillRetryableAsync(
        OutcomeFixture fixture,
        FlowDecisionBinding binding,
        int expectedIteration,
        string? absentMessage = null)
    {
        await using var database = await fixture.Factory.CreateDbContextAsync();
        var flow = await database.Flows
            .Include(item => item.Steps)
            .Include(item => item.Messages)
            .Include(item => item.GateRecords)
            .SingleAsync(item => item.Id == fixture.FlowId);
        var gate = flow.GateRecords.Single(item =>
            item.Id == binding.GateId);
        Assert.False(gate.Resolved);
        Assert.Equal(expectedIteration, flow.Iteration);
        Assert.Equal(FlowStatus.WaitingForFeedback, flow.Status);
        Assert.DoesNotContain(
            flow.Steps,
            step => step.Label == WorkflowEngine.ApprovedPublicationLabel);
        if (absentMessage is not null)
        {
            Assert.DoesNotContain(
                flow.Messages,
                message => message.Content == absentMessage);
        }
        Assert.False(
            fixture.GateHistory.Single(item => item.Id == binding.GateId)
                .Resolved);
    }

    private sealed record FlowDecisionBinding(
        Guid GateId,
        string CandidateFingerprint);

    private static async Task AssertRejectedHostObservationAsync(
        QaObservationMode mode,
        string expectedError)
    {
        await using var fixture = await OutcomeFixture.CreateAsync(
            qaVerdicts: [OutcomeQaVerdict.PASS],
            maxRounds: 1,
            qaObservationMode: mode);

        await fixture.Engine.RunAsync(fixture.FlowId, CancellationToken.None);

        await using var database = await fixture.Factory.CreateDbContextAsync();
        var flow = await database.Flows
            .Include(item => item.GateRecords)
            .SingleAsync(item => item.Id == fixture.FlowId);
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        Assert.True(
            state.Rounds.Count == 1,
            $"Expected one consumed QA round, found {state.Rounds.Count}. " +
            $"Flow={flow.Status}; outcome={state.Status}; failure={flow.FailureReason}");
        var round = state.Rounds.Single();
        Assert.Null(round.Result);
        Assert.Contains(expectedError, round.ContractError);
        Assert.Equal(
            OutcomeVerificationStatus.AwaitingHumanResolution,
            state.Status);
        Assert.DoesNotContain(
            flow.GateRecords,
            gate => gate.ActionType == HandoffActionType.Release);
    }

    private static async Task WriteCompletedSessionJournalAsync(
        string copilotHome,
        Guid sessionId,
        string workspacePath,
        string agentName,
        string output)
    {
        var sessionDirectory = Path.Combine(
            copilotHome,
            "session-state",
            sessionId.ToString("D"));
        Directory.CreateDirectory(sessionDirectory);
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var events = new[]
        {
            RecoveryFixture.Serialize(
                "session.start",
                startedAt,
                new
                {
                    sessionId,
                    context = new
                    {
                        cwd = workspacePath
                    }
                }),
            RecoveryFixture.Serialize(
                "subagent.selected",
                startedAt.AddSeconds(1),
                new
                {
                    agentName,
                    agentDisplayName = agentName
                }),
            RecoveryFixture.Serialize(
                "assistant.turn_start",
                startedAt.AddSeconds(2),
                new
                {
                    turnId = "0"
                }),
            RecoveryFixture.Serialize(
                "assistant.message",
                startedAt.AddSeconds(3),
                new
                {
                    turnId = "0",
                    content = output,
                    toolRequests = Array.Empty<object>()
                }),
            RecoveryFixture.Serialize(
                "assistant.turn_end",
                startedAt.AddSeconds(4),
                new
                {
                    turnId = "0"
                }),
            RecoveryFixture.Serialize(
                "session.shutdown",
                startedAt.AddSeconds(5),
                new
                {
                    shutdownType = "routine"
                })
        };
        await File.WriteAllTextAsync(
            Path.Combine(sessionDirectory, "events.jsonl"),
            string.Join(Environment.NewLine, events) + Environment.NewLine);
    }

    private sealed class OutcomeFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly WorkflowDefinitionProvider _workflowProvider;
        private readonly HandoffGateEngine _gate;
        private readonly CandidateFingerprintService _candidate;
        private readonly IAgentRunner _runner;
        private readonly StubCandidatePublisher? _publisher;

        private OutcomeFixture(
            string root,
            Guid flowId,
            OutcomeDbContextFactory factory,
            WorkflowDefinitionProvider workflowProvider,
            HandoffGateEngine gate,
            WorkflowEngine engine,
            CandidateFingerprintService candidate,
            IAgentRunner runner,
            string workspace,
            StubCandidatePublisher? publisher)
        {
            _root = root;
            FlowId = flowId;
            Factory = factory;
            _workflowProvider = workflowProvider;
            _gate = gate;
            _candidate = candidate;
            _runner = runner;
            _publisher = publisher;
            Engine = engine;
            Workspace = workspace;
        }

        public Guid FlowId { get; }

        public OutcomeDbContextFactory Factory { get; }

        public WorkflowEngine Engine { get; }

        public string Workspace { get; }

        public FeedbackCoordinator CreateFeedbackCoordinator() =>
            new(
                Factory,
                new FixedModelRouter(),
                new BootstrapTaskProfileFactory(),
                TestRoutingSupport.Recorder(Factory),
                _runner,
                _gate,
                new FlowQueue(),
                new FlowLifecycleCoordinator(),
                _candidate,
                _workflowProvider);

        public async Task<FlowDecisionResponse> DecideCurrentAsync(
            bool approve,
            Guid? gateId = null,
            string? candidateFingerprint = null,
            string? feedback = null)
        {
            await using var database = await Factory.CreateDbContextAsync();
            var flow = await database.Flows
                .Include(item => item.Steps)
                .Include(item => item.GateRecords)
                .SingleAsync(item => item.Id == FlowId);
            var outcome = flow.ToOutcomeVerificationDto();
            return await CreateFeedbackCoordinator().DecideAsync(
                FlowId,
                approve,
                gateId ?? outcome.ReleaseGateId ??
                throw new InvalidOperationException(
                    "The fixture has no unresolved release gate."),
                candidateFingerprint ?? outcome.CandidateFingerprint,
                feedback ?? (approve ? string.Empty : "Please revise this outcome."),
                CancellationToken.None);
        }

        public IReadOnlyList<AgentExecutionContext> RunnerContexts =>
            ((OutcomeAgentRunner)_runner).Contexts;

        public IReadOnlyList<HandoffGateRecord> GateHistory => _gate.History();

        public int PublisherCalls => _publisher?.Calls ?? 0;

        public static async Task<OutcomeFixture> CreateAsync(
            IReadOnlyList<OutcomeQaVerdict> qaVerdicts,
            int maxRounds,
            int qaExecutionAttempts = 1,
            bool multiOwner = false,
            bool planGapFirst = false,
            bool invalidQaFirst = false,
            bool nullQaCriterionFirst = false,
            bool manyInvalidQaDiagnosticsFirst = false,
            bool mutateDuringFirstQa = false,
            bool enablePublication = false,
            bool publicationPushback = false,
            bool multiFindingPlanGapFirst = false,
            bool failFirstReplacementCorrection = false,
            bool failPublicationVerificationOnce = false,
            bool doublePlanGap = false,
            bool mutateQaContextDuringFirstQa = false,
            QaObservationMode qaObservationMode =
                QaObservationMode.CorrelatedSuccess)
        {
            var root = Path.Combine(
                AppContext.BaseDirectory,
                "outcome-loop-tests",
                Guid.NewGuid().ToString("N"));
            var workspace = Path.Combine(root, "workspace");
            var agents = Path.Combine(root, ".github", "agents");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(agents);
            InitializeRepository(workspace);
            var configuredAgents = new List<(string Id, string Name)>
            {
                ("team-lead", "Team Lead"),
                ("architect", "Architect"),
                ("software-engineer", "Software Engineer"),
                ("quality-engineer", "Quality Engineer"),
                ("release-engineer", "Release Engineer")
            };
            if (multiFindingPlanGapFirst)
            {
                configuredAgents.Insert(2, ("data-engineer", "Data Engineer"));
            }
            foreach (var (id, name) in configuredAgents)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(agents, $"{id}.agent.md"),
                    $"---{Environment.NewLine}name: {name}{Environment.NewLine}" +
                    $"description: Test role.{Environment.NewLine}---{Environment.NewLine}" +
                    "Complete the assigned work.");
            }
            await File.WriteAllTextAsync(
                Path.Combine(root, "WORKFLOW.md"),
                $$"""
                  ---
                  workspace:
                    root: workspace
                  agent:
                    max_concurrent_agents: 1
                    max_attempts: 1
                  outcome_verification:
                    enabled: true
                    max_rounds: {{maxRounds}}
                  ---
                  {{ "{{ agent.name }}" }}
                  {{ "{{ task }}" }}
                  {{ "{{ role.context }}" }}
                  {{ "{{ outcome.context }}" }}
                  {{ "{{ outcome.contract }}" }}
                  {{ "{{ response.contract }}" }}
                  """);
            var options = new DbContextOptionsBuilder<HarnessDbContext>()
                .UseSqlite(
                    $"Data Source={Path.Combine(root, "harness.db")};Pooling=False")
                .Options;
            var factory = new OutcomeDbContextFactory(options);
            var state = OutcomeVerificationRules.CreateInitialState(1, maxRounds);
            state.TrustedRepositories =
                [new OutcomeTrustedRepository(".", "example/repository")];
            var flow = new FlowRun
            {
                Title = "Implement feature",
                OriginalRequest = multiFindingPlanGapFirst
                    ? "Implement an architecture, database, and product feature."
                    : multiOwner
                    ? "Implement an architecture and product feature."
                    : "Implement a focused product feature.",
                ConsolidatedRequest = multiFindingPlanGapFirst
                    ? "Implement an architecture, database, and product feature."
                    : multiOwner
                    ? "Implement an architecture and product feature."
                    : "Implement a focused product feature.",
                Status = FlowStatus.Queued,
                RepositoryPath = workspace,
                RepositoryKnowledge = "One test repository.",
                WorkspacePath = workspace,
                BranchName = "test/outcome",
                OutcomeVerificationJson =
                    OutcomeVerificationRules.SerializeAggregate(state)
            };
            await using (var database = await factory.CreateDbContextAsync())
            {
                await database.Database.EnsureCreatedAsync();
                database.Settings.Add(new HarnessSettings
                {
                    RepositoryPath = workspace,
                    RepositoryKnowledge = "One test repository.",
                    MaxHandoffRetries = 0
                });
                database.Flows.Add(flow);
                await database.SaveChangesAsync();
            }

            var paths = new HarnessPaths(
                root,
                agents,
                Path.Combine(root, "harness.db"));
            var workflowProvider = new WorkflowDefinitionProvider(
                paths,
                new WorkflowLoader(),
                NullLogger<WorkflowDefinitionProvider>.Instance);
            await workflowProvider.StartAsync(CancellationToken.None);
            var gate = new HandoffGateEngine();
            gate.SetTrustLevel(HandoffActionType.Advance, HandoffTrustLevel.Auto);
            gate.SetTrustLevel(HandoffActionType.RequestRevision, HandoffTrustLevel.Auto);
            gate.SetTrustLevel(HandoffActionType.Release, HandoffTrustLevel.Gated);
            gate.SetTrustLevel(
                HandoffActionType.OutcomeResolution,
                HandoffTrustLevel.Gated);
            var candidate = new CandidateFingerprintService(
                new ProcessRunner(),
                TimeProvider.System);
            var runner = new OutcomeAgentRunner(
                factory,
                workspace,
                qaVerdicts,
                qaExecutionAttempts,
                multiOwner,
                planGapFirst,
                invalidQaFirst,
                nullQaCriterionFirst,
                manyInvalidQaDiagnosticsFirst,
                mutateDuringFirstQa,
                publicationPushback,
                multiFindingPlanGapFirst,
                failFirstReplacementCorrection,
                doublePlanGap,
                mutateQaContextDuringFirstQa,
                qaObservationMode);
            var publisher = enablePublication
                ? new StubCandidatePublisher(factory)
                : null;
            var engine = new WorkflowEngine(
                factory,
                new AgentCatalog(paths, factory),
                new FlowPlanner(),
                new FixedModelRouter(),
                new BootstrapTaskProfileFactory(),
                TestRoutingSupport.Recorder(factory),
                new FixedWorkspaceManager(workspace),
                runner,
                gate,
                new CopilotSessionJournal(),
                workflowProvider,
                NullLogger<WorkflowEngine>.Instance,
                publicationVerifier: enablePublication
                    ? new StubPublishedOutcomeVerifier(
                        failPublicationVerificationOnce)
                    : null,
                candidate,
                new OutcomeVerificationContextBuilder(factory),
                publisher);
            return new OutcomeFixture(
                root,
                flow.Id,
                factory,
                workflowProvider,
                gate,
                engine,
                candidate,
                runner,
                workspace,
                publisher);
        }

        public ValueTask DisposeAsync()
        {
            _workflowProvider.Dispose();
            _gate.Dispose();
            try
            {
                foreach (var file in Directory.EnumerateFiles(
                             _root,
                             "*",
                             SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Test cleanup only.
            }
            return ValueTask.CompletedTask;
        }

        private static void InitializeRepository(string path)
        {
            Run(path, "init", "--quiet");
            Run(path, "config", "user.email", "tests@example.invalid");
            Run(path, "config", "user.name", "Outcome Tests");
            Run(
                path,
                "remote",
                "add",
                "origin",
                "https://github.com/example/repository.git");
            File.WriteAllText(Path.Combine(path, "tracked.txt"), "initial");
            Run(path, "add", "tracked.txt");
            Run(path, "commit", "--quiet", "-m", "initial");
            Run(path, "checkout", "--quiet", "-b", "test/outcome");
        }

        private static void Run(string path, params string[] arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = path,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }
            process.Start();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(process.StandardError.ReadToEnd());
            }
        }
    }

    private sealed class OutcomeAgentRunner(
        IDbContextFactory<HarnessDbContext> factory,
        string workspace,
        IReadOnlyList<OutcomeQaVerdict> qaVerdicts,
        int qaExecutionAttempts,
        bool multiOwner,
        bool planGapFirst,
        bool invalidQaFirst,
        bool nullQaCriterionFirst,
        bool manyInvalidQaDiagnosticsFirst,
        bool mutateDuringFirstQa,
        bool publicationPushback,
        bool multiFindingPlanGapFirst,
        bool failFirstReplacementCorrection,
        bool doublePlanGap,
        bool mutateQaContextDuringFirstQa,
        QaObservationMode qaObservationMode) : IAgentRunner
    {
        private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);

        public List<AgentExecutionContext> Contexts { get; } = [];

        public async Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            _calls[context.AgentRole] =
                _calls.GetValueOrDefault(context.AgentRole) + 1;
            var output = context.AgentRole switch
            {
                "team-lead" =>
                    doublePlanGap && _calls["team-lead"] > 2
                        ? SecondReplacementPlanOutput()
                        : doublePlanGap && _calls["team-lead"] > 1
                            ? ReplacementPlanOutput()
                    : multiFindingPlanGapFirst && _calls["team-lead"] > 1
                        ? ReplacementPlanOutput(includeDataEngineer: true)
                        : planGapFirst && _calls["team-lead"] > 1
                            ? ReplacementPlanOutput()
                            : TeamLeadOutput(
                                includeArchitect: multiOwner || multiFindingPlanGapFirst,
                                includeDataEngineer: multiFindingPlanGapFirst),
                "architect" => failFirstReplacementCorrection &&
                               multiFindingPlanGapFirst &&
                               _calls["architect"] > 1
                    ? "HANDOFF_STATUS: COMPLETE"
                    : ArchitectOutput(),
                "data-engineer" => multiFindingPlanGapFirst && context.Attempt > 1
                    ? DataOutput()
                    : "HANDOFF_STATUS: COMPLETE",
                "software-engineer" =>
                    await SoftwareOutputAsync(context, cancellationToken),
                "release-engineer" =>
                    publicationPushback && _calls["release-engineer"] > 1
                        ? """
                          HANDOFF_STATUS: PUSHBACK
                          PUSHBACK_REASON: Final release checks are not safe to publish.
                          """
                        : ReleaseOutput(),
                "quality-engineer" => await QaOutputAsync(cancellationToken),
                _ => "HANDOFF_STATUS: COMPLETE"
            };
            IReadOnlyList<ToolCallRecord> toolCalls =
                context.AgentRole == "quality-engineer" &&
                qaObservationMode != QaObservationMode.Empty
                ?
                [
                    new ToolCallRecord(
                        "powershell",
                        HostObservedToolLocator.CreateCommandSummary(
                            qaObservationMode == QaObservationMode.LocatorMismatch
                                ? "dotnet test --filter DifferentTest"
                                : "dotnet test"),
                        Succeeded: true,
                        ToolType: "Command",
                        NormalizedCommand:
                            qaObservationMode == QaObservationMode.LocatorMismatch
                                ? "dotnet test --filter DifferentTest"
                                : "dotnet test",
                        NormalizedArguments:
                            qaObservationMode == QaObservationMode.LocatorMismatch
                                ? """{"command":"dotnet test --filter DifferentTest"}"""
                                : """{"command":"dotnet test"}""",
                        WorkingDirectory: context.WorkspacePath,
                        ExitCode: 0,
                        ResultDigest: $"sha256:{new string('f', 64)}",
                        ResultSummary:
                            "Focused test passed. Architecture behavior passed. " +
                            "Stored projection update test passed.")
                ]
                : [];
            return new AgentExecutionResult(
                output,
                "fixture evidence",
                context.AgentRole == "quality-engineer"
                    ? qaExecutionAttempts
                    : 1,
                toolCalls);
        }

        private async Task<string> SoftwareOutputAsync(
            AgentExecutionContext context,
            CancellationToken cancellationToken)
        {
            var call = _calls["software-engineer"];
            File.WriteAllText(
                Path.Combine(workspace, "tracked.txt"),
                call == 1 ? "implemented" : $"corrected-{call}");
            await using var database =
                await factory.CreateDbContextAsync(cancellationToken);
            var state = OutcomeVerificationRules.DeserializeAggregate(
                await database.Flows
                    .Select(item => item.OutcomeVerificationJson)
                    .SingleAsync(cancellationToken));
            var items = string.Join(
                ",",
                state.AcceptancePlan!.Criteria
                    .Where(criterion => criterion.OwnerRoles.Contains(
                        context.AgentRole,
                        StringComparer.Ordinal))
                    .Select(criterion =>
                        $$"""{"CriterionId":"{{criterion.Id}}","Disposition":"Supports","Kind":"Test","Locator":"dotnet test","ObservedResult":"Focused test passed","ExitCode":0,"ContentDigest":null}"""));
            return $$"""
                HANDOFF_STATUS: COMPLETE
                Decision
                Implemented.
                OUTCOME_EVIDENCE_V1_BEGIN
                {"Version":"outcome-evidence-v1","Items":[{{items}}]}
                OUTCOME_EVIDENCE_V1_END
                """;
        }

        private string ReleaseOutput()
        {
            return "HANDOFF_STATUS: COMPLETE\nPrepared the local candidate without publishing.";
        }

        private async Task<string> QaOutputAsync(CancellationToken cancellationToken)
        {
            await using var database = await factory.CreateDbContextAsync(cancellationToken);
            var json = await database.Flows
                .Select(item => item.OutcomeVerificationJson)
                .SingleAsync(cancellationToken);
            var state = OutcomeVerificationRules.DeserializeAggregate(json);
            if (mutateQaContextDuringFirstQa &&
                _calls["quality-engineer"] == 1)
            {
                var contextPath = state.ActiveQaContextPath!;
                File.SetAttributes(
                    contextPath,
                    File.GetAttributes(contextPath) & ~FileAttributes.ReadOnly);
                await File.WriteAllTextAsync(
                    contextPath,
                    """{"tampered":true}""",
                    cancellationToken);
            }
            if (mutateDuringFirstQa && _calls["quality-engineer"] == 1)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(workspace, "tracked.txt"),
                    "QA must never mutate this file",
                    cancellationToken);
            }
            if (invalidQaFirst && _calls["quality-engineer"] == 1)
            {
                return """
                    OUTCOME_QA_RESULT_V1_BEGIN
                    {"Version":"outcome-qa-v1"}
                    OUTCOME_QA_RESULT_V1_END
                    """;
            }
            if (nullQaCriterionFirst && _calls["quality-engineer"] == 1)
            {
                return $$"""
                    OUTCOME_QA_RESULT_V1_BEGIN
                    {"Version":"outcome-qa-v1","AcceptancePlanHash":"{{state.AcceptancePlan!.Hash}}","CandidateFingerprint":"{{state.CurrentCandidate!.Fingerprint}}","Verdict":"FAIL","Criteria":[null],"PlanGaps":[]}
                    OUTCOME_QA_RESULT_V1_END
                    """;
            }
            if (manyInvalidQaDiagnosticsFirst &&
                _calls["quality-engineer"] == 1)
            {
                var gaps = string.Join(
                    ",",
                    Enumerable.Repeat("{}", 700));
                return $$"""
                    OUTCOME_QA_RESULT_V1_BEGIN
                    {"Version":"outcome-qa-v1","AcceptancePlanHash":"{{state.AcceptancePlan!.Hash}}","CandidateFingerprint":"{{state.CurrentCandidate!.Fingerprint}}","Verdict":"FAIL","Criteria":[],"PlanGaps":[{{gaps}}]}
                    OUTCOME_QA_RESULT_V1_END
                    """;
            }
            var verdict = qaVerdicts[
                Math.Min(_calls["quality-engineer"] - 1, qaVerdicts.Count - 1)];
            if (multiFindingPlanGapFirst && _calls["quality-engineer"] == 1)
            {
                return $$"""
                    OUTCOME_QA_RESULT_V1_BEGIN
                    {"Version":"outcome-qa-v1","AcceptancePlanHash":"{{state.AcceptancePlan!.Hash}}","CandidateFingerprint":"{{state.CurrentCandidate!.Fingerprint}}","Verdict":"BLOCKED","Criteria":[{"CriterionId":"AC-001","Status":"BLOCKED","EvidenceIds":[],"ChecksPerformed":[{"Kind":"Test","Locator":"dotnet test","ObservedResult":"Architecture behavior remains blocked","ExitCode":1}],"Rationale":"An external dependency still blocks the original multi-owner outcome.","ResponsibleRoles":[],"Remediation":null}],"PlanGaps":[{"Requirement":"The stored projection is refreshed.","Verification":"Run the storage update test and observe that the stored projection updates.","OwnerRoles":["data-engineer"],"Rationale":"The confirmed brief includes a persisted outcome that the acceptance plan omitted."}]}
                    OUTCOME_QA_RESULT_V1_END
                    """;
            }
            if (planGapFirst && _calls["quality-engineer"] == 1)
            {
                return $$"""
                    OUTCOME_QA_RESULT_V1_BEGIN
                    {"Version":"outcome-qa-v1","AcceptancePlanHash":"{{state.AcceptancePlan!.Hash}}","CandidateFingerprint":"{{state.CurrentCandidate!.Fingerprint}}","Verdict":"FAIL","Criteria":[{"CriterionId":"AC-001","Status":"PASS","EvidenceIds":[],"ChecksPerformed":[{"Kind":"Test","Locator":"dotnet test","ObservedResult":"Focused test passed","ExitCode":0}],"Rationale":"The represented criterion passed.","ResponsibleRoles":[],"Remediation":null}],"PlanGaps":[{"Requirement":"The missing outcome is implemented.","Verification":"Run the missing outcome test and observe that the missing outcome behavior passes.","OwnerRoles":["software-engineer"],"Rationale":"The confirmed brief contains an uncovered outcome."}]}
                    OUTCOME_QA_RESULT_V1_END
                    """;
            }
            if (doublePlanGap && _calls["quality-engineer"] <= 2)
            {
                var nextId = $"AC-{state.AcceptancePlan!.Criteria.Count + 1:000}";
                var coveredCriteria = string.Join(
                    ",",
                    state.AcceptancePlan.Criteria.Select(criterion =>
                        $$"""{"CriterionId":"{{criterion.Id}}","Status":"PASS","EvidenceIds":[],"ChecksPerformed":[{"Kind":"Test","Locator":"dotnet test","ObservedResult":"Focused test passed","ExitCode":0}],"Rationale":"The represented criterion passed.","ResponsibleRoles":[],"Remediation":null}"""));
                return $$"""
                    OUTCOME_QA_RESULT_V1_BEGIN
                    {"Version":"outcome-qa-v1","AcceptancePlanHash":"{{state.AcceptancePlan.Hash}}","CandidateFingerprint":"{{state.CurrentCandidate!.Fingerprint}}","Verdict":"FAIL","Criteria":[{{coveredCriteria}}],"PlanGaps":[{"Requirement":"Missing outcome {{nextId}} is implemented.","Verification":"Run the focused test and observe that missing outcome {{nextId}} passes.","OwnerRoles":["software-engineer"],"Rationale":"The confirmed brief contains another uncovered outcome."}]}
                    OUTCOME_QA_RESULT_V1_END
                    """;
            }
            var status = verdict switch
            {
                OutcomeQaVerdict.PASS => "PASS",
                OutcomeQaVerdict.BLOCKED => "BLOCKED",
                _ => "FAIL"
            };
            var roles = verdict == OutcomeQaVerdict.PASS
                ? "[]"
                : multiOwner
                    ? """["architect"]"""
                    : """["software-engineer"]""";
            var remediation = verdict == OutcomeQaVerdict.PASS
                ? "null"
                : "\"Correct the implementation and rerun the focused test.\"";
            var observed = verdict == OutcomeQaVerdict.PASS
                ? "Focused test passed"
                : "Focused test failed";
            var exitCode = verdict == OutcomeQaVerdict.PASS &&
                           qaObservationMode != QaObservationMode.NonzeroExit
                ? 0
                : 1;
            var criteria = string.Join(
                ",",
                state.AcceptancePlan!.Criteria.Select(criterion =>
                    $$"""{"CriterionId":"{{criterion.Id}}","Status":"{{status}}","EvidenceIds":[],"ChecksPerformed":[{"Kind":"Test","Locator":"dotnet test","ObservedResult":"{{observed}}","ExitCode":{{exitCode}}}],"Rationale":"The focused behavior was independently checked.","ResponsibleRoles":{{roles}},"Remediation":{{remediation}}}"""));
            return $$"""
                OUTCOME_QA_RESULT_V1_BEGIN
                {"Version":"outcome-qa-v1","AcceptancePlanHash":"{{state.AcceptancePlan.Hash}}","CandidateFingerprint":"{{state.CurrentCandidate!.Fingerprint}}","Verdict":"{{verdict}}","Criteria":[{{criteria}}],"PlanGaps":[]}
                OUTCOME_QA_RESULT_V1_END
                """;
        }

        private static string TeamLeadOutput(
            bool includeArchitect,
            bool includeDataEngineer = false) =>
            includeDataEngineer ? """
            HANDOFF_STATUS: COMPLETE
            Decision
            Use the fixed plan.
            TEAM_TASK_PROFILES_V1_BEGIN
            {"Version":"task-profile-v1","Profiles":[{"Role":"architect","Complexity":6,"ReasoningDepth":7,"ContextDemand":6,"ToolIntensity":4,"TaskTypeTags":["Architecture"],"Risk":"Medium","RiskReason":"Architecture affects implementation.","Confidence":0.9,"Rationales":["Boundaries require review."]},{"Role":"data-engineer","Complexity":5,"ReasoningDepth":5,"ContextDemand":5,"ToolIntensity":4,"TaskTypeTags":["Data"],"Risk":"Medium","RiskReason":"Persisted state changes behavior.","Confidence":0.9,"Rationales":["Data lineage is required."]},{"Role":"software-engineer","Complexity":5,"ReasoningDepth":5,"ContextDemand":5,"ToolIntensity":5,"TaskTypeTags":["Implementation"],"Risk":"Medium","RiskReason":"Implementation changes behavior.","Confidence":0.9,"Rationales":["Implementation is required."]},{"Role":"quality-engineer","Complexity":5,"ReasoningDepth":6,"ContextDemand":6,"ToolIntensity":5,"TaskTypeTags":["Quality"],"Risk":"Medium","RiskReason":"Independent verification is required.","Confidence":0.9,"Rationales":["Candidate checks are required."]},{"Role":"release-engineer","Complexity":4,"ReasoningDepth":4,"ContextDemand":4,"ToolIntensity":5,"TaskTypeTags":["Release"],"Risk":"Medium","RiskReason":"Local candidate packaging is required.","Confidence":0.9,"Rationales":["Commit identity is required."]}]}
            TEAM_TASK_PROFILES_V1_END
            PRE_MORTEM_PLAN_V1_BEGIN
            {"Version":"pre-mortem-plan-v1","AfterRoles":[]}
            PRE_MORTEM_PLAN_V1_END
            OUTCOME_ACCEPTANCE_PLAN_V1_BEGIN
            {"Version":"outcome-acceptance-v1","Criteria":[{"Id":"AC-001","Requirement":"The architecture and focused behavior work together.","Verification":"Run the focused integration test and observe that the architecture and focused behavior pass together.","OwnerRoles":["architect","software-engineer"],"EvidenceKinds":["Test"],"CustomerVisible":false}]}
            OUTCOME_ACCEPTANCE_PLAN_V1_END
            """
            : includeArchitect ? """
            HANDOFF_STATUS: COMPLETE
            Decision
            Use the fixed plan.
            TEAM_TASK_PROFILES_V1_BEGIN
            {"Version":"task-profile-v1","Profiles":[{"Role":"architect","Complexity":6,"ReasoningDepth":7,"ContextDemand":6,"ToolIntensity":4,"TaskTypeTags":["Architecture"],"Risk":"Medium","RiskReason":"Architecture affects implementation.","Confidence":0.9,"Rationales":["Boundaries require review."]},{"Role":"software-engineer","Complexity":5,"ReasoningDepth":5,"ContextDemand":5,"ToolIntensity":5,"TaskTypeTags":["Implementation"],"Risk":"Medium","RiskReason":"Implementation changes behavior.","Confidence":0.9,"Rationales":["Implementation is required."]},{"Role":"quality-engineer","Complexity":5,"ReasoningDepth":6,"ContextDemand":6,"ToolIntensity":5,"TaskTypeTags":["Quality"],"Risk":"Medium","RiskReason":"Independent verification is required.","Confidence":0.9,"Rationales":["Candidate checks are required."]},{"Role":"release-engineer","Complexity":4,"ReasoningDepth":4,"ContextDemand":4,"ToolIntensity":5,"TaskTypeTags":["Release"],"Risk":"Medium","RiskReason":"Local candidate packaging is required.","Confidence":0.9,"Rationales":["Commit identity is required."]}]}
            TEAM_TASK_PROFILES_V1_END
            PRE_MORTEM_PLAN_V1_BEGIN
            {"Version":"pre-mortem-plan-v1","AfterRoles":[]}
            PRE_MORTEM_PLAN_V1_END
            OUTCOME_ACCEPTANCE_PLAN_V1_BEGIN
            {"Version":"outcome-acceptance-v1","Criteria":[{"Id":"AC-001","Requirement":"The architecture and focused behavior work together.","Verification":"Run the focused integration test and observe that the architecture and focused behavior pass together.","OwnerRoles":["architect","software-engineer"],"EvidenceKinds":["Test"],"CustomerVisible":false}]}
            OUTCOME_ACCEPTANCE_PLAN_V1_END
            """
            : """
            HANDOFF_STATUS: COMPLETE
            Decision
            Use the fixed plan.
            TEAM_TASK_PROFILES_V1_BEGIN
            {"Version":"task-profile-v1","Profiles":[{"Role":"software-engineer","Complexity":5,"ReasoningDepth":5,"ContextDemand":5,"ToolIntensity":5,"TaskTypeTags":["Implementation"],"Risk":"Medium","RiskReason":"Implementation changes behavior.","Confidence":0.9,"Rationales":["Implementation is required."]},{"Role":"quality-engineer","Complexity":5,"ReasoningDepth":6,"ContextDemand":6,"ToolIntensity":5,"TaskTypeTags":["Quality"],"Risk":"Medium","RiskReason":"Independent verification is required.","Confidence":0.9,"Rationales":["Candidate checks are required."]},{"Role":"release-engineer","Complexity":4,"ReasoningDepth":4,"ContextDemand":4,"ToolIntensity":5,"TaskTypeTags":["Release"],"Risk":"Medium","RiskReason":"Local candidate packaging is required.","Confidence":0.9,"Rationales":["Commit identity is required."]}]}
            TEAM_TASK_PROFILES_V1_END
            PRE_MORTEM_PLAN_V1_BEGIN
            {"Version":"pre-mortem-plan-v1","AfterRoles":[]}
            PRE_MORTEM_PLAN_V1_END
            OUTCOME_ACCEPTANCE_PLAN_V1_BEGIN
            {"Version":"outcome-acceptance-v1","Criteria":[{"Id":"AC-001","Requirement":"The focused product behavior is implemented.","Verification":"Run the focused test and observe that the focused product behavior passes.","OwnerRoles":["software-engineer"],"EvidenceKinds":["Test"],"CustomerVisible":false}]}
            OUTCOME_ACCEPTANCE_PLAN_V1_END
            """;

        private static string ArchitectOutput() => """
            HANDOFF_STATUS: COMPLETE
            Decision
            Defined the implementation boundary.
            OUTCOME_EVIDENCE_V1_BEGIN
            {"Version":"outcome-evidence-v1","Items":[{"CriterionId":"AC-001","Disposition":"Supports","Kind":"Test","Locator":"dotnet test","ObservedResult":"Architecture integration test passed","ExitCode":0,"ContentDigest":null}]}
            OUTCOME_EVIDENCE_V1_END
            """;

        private static string DataOutput() => """
            HANDOFF_STATUS: COMPLETE
            Decision
            Updated the persisted projection.
            OUTCOME_EVIDENCE_V1_BEGIN
            {"Version":"outcome-evidence-v1","Items":[{"CriterionId":"AC-002","Disposition":"Supports","Kind":"Test","Locator":"dotnet test","ObservedResult":"Stored projection update test passed","ExitCode":0,"ContentDigest":null}]}
            OUTCOME_EVIDENCE_V1_END
            """;

        private static string ReplacementPlanOutput(bool includeDataEngineer = false) => includeDataEngineer ? """
            HANDOFF_STATUS: COMPLETE
            Decision
            Replaced the complete acceptance plan.
            OUTCOME_ACCEPTANCE_PLAN_V1_BEGIN
            {"Version":"outcome-acceptance-v1","Criteria":[{"Id":"AC-001","Requirement":"The architecture and focused behavior work together.","Verification":"Run the focused integration test and observe that the architecture and focused behavior pass together.","OwnerRoles":["architect","software-engineer"],"EvidenceKinds":["Test"],"CustomerVisible":false},{"Id":"AC-002","Requirement":"The stored projection is refreshed.","Verification":"Run the storage update test and observe that the stored projection updates.","OwnerRoles":["data-engineer"],"EvidenceKinds":["Test"],"CustomerVisible":false}]}
            OUTCOME_ACCEPTANCE_PLAN_V1_END
            """
            : """
            HANDOFF_STATUS: COMPLETE
            Decision
            Replaced the complete acceptance plan.
            OUTCOME_ACCEPTANCE_PLAN_V1_BEGIN
            {"Version":"outcome-acceptance-v1","Criteria":[{"Id":"AC-001","Requirement":"The focused product behavior is implemented.","Verification":"Run the focused test and observe that the focused product behavior passes.","OwnerRoles":["software-engineer"],"EvidenceKinds":["Test"],"CustomerVisible":false},{"Id":"AC-002","Requirement":"The missing outcome is implemented.","Verification":"Run the missing outcome test and observe that the missing outcome behavior passes.","OwnerRoles":["software-engineer"],"EvidenceKinds":["Test"],"CustomerVisible":false}]}
            OUTCOME_ACCEPTANCE_PLAN_V1_END
            """;

        private static string SecondReplacementPlanOutput() => """
            HANDOFF_STATUS: COMPLETE
            Decision
            Replaced the complete acceptance plan again.
            OUTCOME_ACCEPTANCE_PLAN_V1_BEGIN
            {"Version":"outcome-acceptance-v1","Criteria":[{"Id":"AC-001","Requirement":"The focused product behavior is implemented.","Verification":"Run the focused test and observe that the focused product behavior passes.","OwnerRoles":["software-engineer"],"EvidenceKinds":["Test"],"CustomerVisible":false},{"Id":"AC-002","Requirement":"The missing outcome is implemented.","Verification":"Run the missing outcome test and observe that the missing outcome behavior passes.","OwnerRoles":["software-engineer"],"EvidenceKinds":["Test"],"CustomerVisible":false},{"Id":"AC-003","Requirement":"The second missing outcome is implemented.","Verification":"Run the second missing outcome test and observe that the second missing outcome behavior passes.","OwnerRoles":["software-engineer"],"EvidenceKinds":["Test"],"CustomerVisible":false}]}
            OUTCOME_ACCEPTANCE_PLAN_V1_END
            """;

        private static void Run(string path, params string[] arguments)
        {
            var output = Capture(path, arguments);
            _ = output;
        }

        private static string Capture(string path, params string[] arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = path,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(error);
            }
            return output;
        }
    }

    private sealed class FixedWorkspaceManager(string path) : IWorkspaceManager
    {
        public Task<WorkspaceInfo> PrepareAsync(
            FlowRun flow,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkspaceInfo(path, "test/outcome", false));

        public Task<WorkspaceCleanupResult> RemoveAsync(
            FlowRun flow,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(WorkspaceCleanupResult.Empty);
    }

    private enum QaObservationMode
    {
        CorrelatedSuccess,
        NonzeroExit,
        Empty,
        LocatorMismatch
    }

    private sealed class StubCandidatePublisher(
        IDbContextFactory<HarnessDbContext> databaseFactory)
        : IVerifiedCandidatePublisher
    {
        public int Calls { get; private set; }

        public async Task<string> PublishAsync(
            FlowRun flow,
            Guid publicationStepId,
            CancellationToken cancellationToken = default)
        {
            await using var database =
                await databaseFactory.CreateDbContextAsync(cancellationToken);
            var stored = await database.Flows.SingleAsync(
                item => item.Id == flow.Id,
                cancellationToken);
            var step = await database.FlowSteps.SingleAsync(
                item => item.Id == publicationStepId,
                cancellationToken);
            var state = OutcomeVerificationRules.DeserializeAggregate(
                stored.OutcomeVerificationJson);
            var candidate = state.CurrentCandidate!;
            var publicationRootId = step.StableSemanticRootId ?? step.Id;
            if (state.Publication is
                {
                    StepId: var existingRootId,
                    CandidateFingerprint: var fingerprint,
                    Status: OutcomePublicationStatus.Published
                } &&
                existingRootId == publicationRootId &&
                string.Equals(
                    fingerprint,
                    candidate.Fingerprint,
                    StringComparison.Ordinal) &&
                state.Publication.Repositories.All(repository =>
                    repository.Status == OutcomeRepositoryPublicationStatus.Published))
            {
                return "https://github.com/example/repository/pull/42";
            }

            Calls++;
            state.Publication = new OutcomePublicationJournal
            {
                StepId = publicationRootId,
                CandidateFingerprint = candidate.Fingerprint,
                Status = OutcomePublicationStatus.Published,
                Repositories = candidate.Manifest.Repositories.Select(repository =>
                    new OutcomeRepositoryPublication
                    {
                        RelativePath = repository.RelativePath,
                        Head = repository.Head,
                        Tree = repository.Tree,
                        RemoteRepository = "example/repository",
                        PullRequestUrl =
                            "https://github.com/example/repository/pull/42",
                        Status = OutcomeRepositoryPublicationStatus.Published
                    }).ToList()
            };
            stored.OutcomeVerificationJson =
                OutcomeVerificationRules.SerializeAggregate(state);
            await database.SaveChangesAsync(cancellationToken);
            return "https://github.com/example/repository/pull/42";
        }
    }

    private sealed class StubPublishedOutcomeVerifier(
        bool failFirstVerification = false) : IPublishedOutcomeVerifier
    {
        private int _calls;

        public Task<PublishedOutcome> VerifyAsync(
            FlowRun flow,
            string releaseOutput,
            CancellationToken cancellationToken = default)
        {
            _calls++;
            if (failFirstVerification && _calls == 1)
            {
                throw new InvalidOperationException(
                    "forced publication verification failure");
            }

            return Task.FromResult(new PublishedOutcome(
                "https://github.com/example/repository/pull/42",
                "Published pull request #42"));
        }
    }

    internal sealed class OutcomeDbContextFactory(
        DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);

        public Task<HarnessDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HarnessDbContext(options));
    }
}

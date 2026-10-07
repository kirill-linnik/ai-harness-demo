using System.Text.Json;
using AiHarnessDemo.Contracts;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Security;
using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Services;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Tests;

public sealed partial class ReviewWorkflowTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task OperatorScopedRemainingWork_PreservesExpiredBudgetAndBoundsNewScope(int limit, bool unconfirmed)
    {
        await using var harness = await ReviewHarness.CreateAsync(FlowKind.Delivery, maxHandoffRetries: limit);
        Guid exhaustedRoot = default;
        var deadline = DateTimeOffset.FromUnixTimeMilliseconds(
            DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());
        harness.Runner.WorkerOutputOverride = context =>
        {
            if (context.PlanStepKey != "implement")
            {
                return null;
            }
            using var database = harness.Factory.CreateDbContext();
            var source = database.FlowSteps.Single(item => item.Id == context.FlowStepId);
            exhaustedRoot = source.Id;
            source.ExecutionBudgetRootId = source.Id;
            database.AgentExecutionBudgets.Add(new AgentExecutionBudget
            {
                FlowRunId = harness.FlowId, RootStepId = source.Id, AgentId = source.AgentId,
                StartedAt = deadline.AddHours(-4), DeadlineAt = deadline,
                PolicyJson = JsonSerializer.Serialize(AssignmentExecutionBudget.Policy(
                    harness.WorkflowProvider.GetEffective().Config.Copilot, source.WorkflowRevision))
            });
            database.SaveChanges();
            throw new AgentRunException(AssignmentExecutionBudget.ExhaustedReason, AgentRunFailureKind.BudgetExhausted)
            {
                ProcessTerminationUnconfirmed = unconfirmed
            };
        };
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        Assert.Equal(FlowStatus.Failed, (await harness.LoadFlowAsync()).Status);
        const string scope = "Inspect preserved implementation and reports. Complete only missing checks and return the normal handoff.";
        if (limit == 0 || unconfirmed)
        {
            await Assert.ThrowsAsync<FlowLifecycleException>(() =>
                harness.Engine.ContinueRemainingWorkAsync(harness.FlowId, scope, CancellationToken.None));
        }
        else
        {
            harness.Runner.WorkerOutputOverride = null;
            var queued = await harness.Engine.ContinueRemainingWorkAsync(harness.FlowId, scope, CancellationToken.None);
            Assert.Equal(FlowStatus.Queued, queued.Status);
            var next = queued.Steps.Last(item => item.PlanStepKey == "implement");
            Assert.Equal(exhaustedRoot, next.StableSemanticRootId);
            Assert.Equal(next.Id, next.ExecutionBudgetRootId);
            Assert.Contains(scope, next.InputSummary);
            Assert.False(next.RemotePublicationAllowed);
            await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
            var completed = await harness.LoadFlowAsync();
            Assert.True(completed.Status == FlowStatus.WaitingForFeedback, completed.FailureReason);
            Assert.Single(completed.Events, item => item.Type == WorkflowEngine.RemainingWorkAssignmentEventType);
            await using var expired = await harness.Factory.CreateDbContextAsync();
            var sameWorker = await expired.FlowSteps.SingleAsync(item => item.Id == next.Id);
            sameWorker.Status = StepStatus.Failed;
            sameWorker.Phase = AgentRunPhase.BudgetExhausted;
            var flow = await expired.Flows.SingleAsync(item => item.Id == harness.FlowId);
            flow.Status = FlowStatus.Failed;
            var newBudget = await expired.AgentExecutionBudgets.SingleAsync(item => item.RootStepId == next.Id);
            Assert.InRange(newBudget.DeadlineAt - newBudget.StartedAt, TimeSpan.Zero, TimeSpan.FromMinutes(45));
            newBudget.DeadlineAt = deadline;
            await expired.SaveChangesAsync();
            await Assert.ThrowsAsync<FlowLifecycleException>(() =>
                harness.Engine.ContinueRemainingWorkAsync(harness.FlowId, scope, CancellationToken.None));
        }
        await using var check = await harness.Factory.CreateDbContextAsync();
        Assert.Equal(deadline, (await check.AgentExecutionBudgets.SingleAsync(
            item => item.RootStepId == exhaustedRoot)).DeadlineAt);
        Assert.Equal(0, harness.CandidatePublisher.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InvalidQaDefectReport_RepairsWithoutAcceptingClaims(bool missingHeader, bool useDisplayName)
    {
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery, maxHandoffRetries: 2);
        harness.Runner.WorkerOutputOverride = context =>
        {
            if (!context.RequiresDeliveryReadinessQa ||
                context.Attempt > 1 && string.IsNullOrWhiteSpace(context.ResponseCorrectionInstructions))
            {
                return null;
            }
            var qa = DeliveryReadinessFixtures.QaBlockFromPrompt(context.OutcomeContext,
                outcome: AiHarnessDemo.Core.Verification.DeliveryCriterionOutcome.Failed,
                evidenceOverride: ["EV-S999-001"]);
            if (useDisplayName)
            {
                qa = qa.Replace("\"external-delivery\"", "\"Outcome Crafter\"", StringComparison.Ordinal);
            }
            return missingHeader
                ? "A response fragment with no opening handoff.\n" + qa
                : "HANDOFF_STATUS: COMPLETE\nFLOW_OUTCOME_BEGIN\n" +
                  """{"Goal":"Produce a customer-reviewable result.","Summary":"Defects need repair.","ImplementationDetails":["Unverified defects were reported."],"Artifacts":[]}""" +
                  "\nFLOW_OUTCOME_END\n" + qa;
        };

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.True(flow.Status == FlowStatus.WaitingForFeedback, flow.FailureReason);
        var directive = Assert.Single(flow.Events, item => item.Type == WorkflowEngine.HostRepairEventType);
        Assert.Contains("No verification claims were accepted", directive.Message);
        var rejected = flow.Steps.Single(item => item.Id == directive.FlowStepId);
        Assert.Equal(StepStatus.Pushback, rejected.Status);
        Assert.Contains("EV-S999-001", rejected.OutputSummary);
        Assert.Single(flow.Steps, item => item.PlanStepKey == "implement" && item.Attempt == 2);
        var fresh = flow.Steps.Last(item => item.PlanStepKey == "prepare");
        Assert.Equal(fresh.Id, fresh.ExecutionBudgetRootId);
        Assert.Single(flow.Events, item => item.Type == WorkflowEngine.RepairedCandidateAssignmentEventType);
        var permission = JsonSerializer.Deserialize<EffectiveExecutionPermission>(fresh.EffectivePermissionJson)!;
        Assert.Contains("powershell", permission.AllowedTools);
        Assert.DoesNotContain("shell", permission.DeniedTools);
        Assert.DoesNotContain(flow.Events, item => item.Type == "flow.failed");
        Assert.Single(flow.Events, item => item.Type == "agent.contract-correction-scheduled");
        var correctionContexts = harness.Runner.Contexts.Where(item => item.RequiresDeliveryReadinessQa).Take(2).ToArray();
        Assert.Equal(correctionContexts[0].CopilotSessionId, correctionContexts[1].CopilotSessionId);
        Assert.True(correctionContexts[1].ResumeSession);
        Assert.Contains(flow.Events, item => item.Type == DeliveryReadinessService.EvidenceEpochEventType);
        await using var database = await harness.Factory.CreateDbContextAsync();
        var readiness = await database.DeliveryReadinessSnapshots.SingleAsync(item => item.Active);
        Assert.NotEqual(rejected.Id, readiness.QaStepId);
        Assert.Equal(0, harness.CandidatePublisher.Calls);
    }

    [Theory]
    [InlineData("bound", true)]
    [InlineData("display-name", true)]
    [InlineData("long-remediation", true)]
    [InlineData("continued-owner", true)]
    [InlineData("manual-owner", true)]
    [InlineData("wrong-hash", false)]
    [InlineData("old-iteration", false)]
    [InlineData("different-agent", false)]
    [InlineData("different-lineage", false)]
    [InlineData("missing-ledger", false)]
    [InlineData("newer-complete-report", false)]
    public async Task TruncatedQaCorrection_UsesOnlyItsBoundCurrentSourceReport(
        string scenario, bool shouldRepair)
    {
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery, maxHandoffRetries: 2);
        if (scenario is "long-remediation" or "continued-owner" or "manual-owner")
        {
            harness.Runner.PlanningOutputOverride = "HANDOFF_STATUS: COMPLETE\n" +
                ReviewHarness.WrapPlan(ReviewHarness.DeliveryPlan(2));
        }
        string? completeReport = null;
        string? newerReport = null;
        harness.Runner.WorkerOutputOverride = context =>
        {
            if (!context.RequiresDeliveryReadinessQa)
            {
                return null;
            }
            completeReport ??= "HANDOFF_STATUS: COMPLETE\n" +
                DeliveryReadinessFixtures.QaBlockFromPrompt(
                    context.OutcomeContext,
                    outcome: AiHarnessDemo.Core.Verification.DeliveryCriterionOutcome.Failed,
                    evidenceOverride: ["EV-S999-001"]);
            if (scenario is "long-remediation" or "continued-owner" or "manual-owner")
            {
                completeReport = completeReport.Replace("Correct the failing behavior and re-verify.",
                    new string('x', 1_980) + "LATE-REPAIR-END", StringComparison.Ordinal);
            }
            newerReport = DeliveryReadinessFixtures.QaBlockFromPrompt(context.OutcomeContext);
            return context.Attempt == 1
                ? completeReport.Replace(
                    DeliveryReadinessFixtures.PlanHashFromPrompt(context.OutcomeContext),
                    "sha256:" + new string('0', 64), StringComparison.Ordinal)
                : "A trailing JSON fragment without the handoff or opening QA sentinel.\nOUTCOME_QA_END";
        };
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        Assert.Equal(FlowStatus.Failed, (await harness.LoadFlowAsync()).Status);

        await using (var database = await harness.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows.Include(item => item.Steps)
                .Include(item => item.Events).Include(item => item.GateRecords).SingleAsync();
            var correction = flow.Steps.Single(item => item.Status == StepStatus.Failed);
            var ledger = flow.Events.Single(item => item.Type == "agent.contract-correction-scheduled");
            using var binding = JsonDocument.Parse(ledger.DataJson!);
            var source = flow.Steps.Single(item =>
                item.Id == binding.RootElement.GetProperty("SourceStepId").GetGuid());
            source.OutputSummary = completeReport!;
            if (scenario == "display-name")
            {
                completeReport = completeReport!.Replace(
                    "\"external-delivery\"", "\"Outcome Crafter\"", StringComparison.Ordinal);
                source.OutputSummary = completeReport;
            }
            switch (scenario)
            {
                case "wrong-hash":
                    source.OutputSummary = source.OutputSummary.Replace(
                        DeliveryReadinessFixtures.PlanHashFromPrompt(
                            harness.Runner.Contexts.Last(item => item.RequiresDeliveryReadinessQa).OutcomeContext),
                        "sha256:" + new string('0', 64), StringComparison.Ordinal);
                    break;
                case "old-iteration":
                    source.Iteration--;
                    break;
                case "different-agent":
                    source.AgentId = "another-verifier";
                    break;
                case "different-lineage":
                    source.StableSemanticRootId = Guid.NewGuid();
                    break;
                case "missing-ledger":
                    database.FlowEvents.Remove(ledger);
                    break;
                case "newer-complete-report":
                    correction.OutputSummary = newerReport!;
                    break;
            }
            await database.SaveChangesAsync();
            var repair = await WorkflowEngine.TryBuildQaOwnerRepairAsync(
                database, flow, correction, correction.OutputSummary, CancellationToken.None);
            Assert.Equal(shouldRepair, repair is not null);
            if (shouldRepair)
            {
                Assert.Equal(source.Id, repair!.Proposal!.SourceStepId);
            }
            Assert.Equal(FlowStatus.Failed, flow.Status);
            Assert.Empty(await database.DeliveryReadinessSnapshots.ToListAsync());
        }

        if (shouldRepair)
        {
            var interrupted = false;
            string? originalProposal = null;
            harness.Runner.WorkerOutputOverride = context =>
            {
                if (scenario is not ("continued-owner" or "manual-owner") || context.PlanStepKey != "implement")
                {
                    return null;
                }
                var document = Assert.Single(context.ContextDocuments!,
                    item => item.Name == "qa-repair-proposal.json");
                if (!interrupted)
                {
                    interrupted = true;
                    originalProposal = document.Content;
                    throw new AgentRunException("Transient interruption before consuming owner context.",
                        scenario == "continued-owner" ? AgentRunFailureKind.Transient : AgentRunFailureKind.AmbiguousCrash);
                }
                Assert.Equal(originalProposal, document.Content);
                return null;
            };
            var queued = await harness.Engine.RestartFailedFlowAsync(harness.FlowId, CancellationToken.None);
            Assert.Equal(FlowStatus.Queued, queued.Status);
            await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
            if (scenario == "manual-owner")
            {
                Assert.Equal(FlowStatus.Failed, (await harness.LoadFlowAsync()).Status);
                await harness.Engine.RestartFailedFlowAsync(harness.FlowId, CancellationToken.None);
                await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
            }
            var recovered = await harness.LoadFlowAsync();
            Assert.True(recovered.Status == FlowStatus.WaitingForFeedback, recovered.FailureReason);
            Assert.Single(recovered.Events, item => item.Type == WorkflowEngine.HostRepairEventType);
            Assert.Single(recovered.Events, item => item.Type == "flow.qa-owner-repair-queued");
            if (scenario is not ("continued-owner" or "manual-owner"))
            {
                Assert.Single(recovered.Steps, item => item.PlanStepKey == "implement" && item.Attempt == 2);
            }
            else
            {
                if (scenario == "continued-owner")
                {
                    Assert.Single(recovered.Events, item => item.Type == WorkflowEngine.AutomaticContinuationEventType);
                }
                else
                {
                    Assert.DoesNotContain(recovered.Events, item => item.Type == WorkflowEngine.AutomaticContinuationEventType);
                }
                Assert.True(interrupted);
            }
            Assert.Contains(recovered.Steps, item => item.OutputSummary.Contains("trailing JSON fragment"));
            Assert.Contains(recovered.Steps, item => item.OutputSummary == completeReport);
            var ownerContext = harness.Runner.Contexts.Last(item => item.PlanStepKey == "implement");
            var document = Assert.Single(ownerContext.ContextDocuments!,
                item => item.Name == "qa-repair-proposal.json");
            var proposal = JsonSerializer.Deserialize<QaOwnerRepairProposal>(document.Content)!;
            Assert.Equal(scenario is "long-remediation" or "continued-owner" or "manual-owner" ? 2 : 1, proposal.Criteria.Count);
            if (scenario is "long-remediation" or "continued-owner" or "manual-owner")
            {
                Assert.Contains("AC-002", document.Content);
                Assert.All(proposal.Criteria, item => Assert.EndsWith("LATE-REPAIR-END", item.Remediation));
                Assert.True(document.Content.Length > 4_000);
            }
            Assert.Single(recovered.Events, item => item.Type == WorkflowEngine.OwnerRepairProposalEventType);
        }
        Assert.Equal(0, harness.CandidatePublisher.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExhaustedQaOwnerRepair_RestartDoesNotDuplicateDirectiveOrResetLimit(bool exhaustAfterRepair)
    {
        var initialLimit = exhaustAfterRepair ? 1 : 0;
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery, maxHandoffRetries: initialLimit);
        harness.Runner.WorkerOutputOverride = context => context.RequiresDeliveryReadinessQa
            ? "Unaccepted defect report.\n" + DeliveryReadinessFixtures.QaBlockFromPrompt(
                context.OutcomeContext,
                outcome: AiHarnessDemo.Core.Verification.DeliveryCriterionOutcome.Failed,
                evidenceOverride: ["EV-S999-001"])
            : null;
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var failed = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Failed, failed.Status);
        var directives = failed.Events.Where(item => item.Type == WorkflowEngine.HostRepairEventType)
            .ToDictionary(item => item.Id, item => item.DataJson);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsAsync<FlowLifecycleException>(() =>
                harness.Engine.RestartFailedFlowAsync(harness.FlowId, CancellationToken.None));
        }
        var unchanged = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Failed, unchanged.Status);
        Assert.Equal(directives.Count,
            unchanged.Events.Count(item => item.Type == WorkflowEngine.HostRepairEventType));
        Assert.All(unchanged.Events.Where(item => item.Type == WorkflowEngine.HostRepairEventType),
            item => Assert.Equal(directives[item.Id], item.DataJson));

        await using (var database = await harness.Factory.CreateDbContextAsync())
        {
            (await database.Settings.SingleAsync()).MaxHandoffRetries = initialLimit + 1;
            await database.SaveChangesAsync();
        }
        harness.Runner.WorkerOutputOverride = null;
        await harness.Engine.RestartFailedFlowAsync(harness.FlowId, CancellationToken.None);
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var recovered = await harness.LoadFlowAsync();
        Assert.True(recovered.Status == FlowStatus.WaitingForFeedback, recovered.FailureReason);
        Assert.Equal(directives.Count,
            recovered.Events.Count(item => item.Type == WorkflowEngine.HostRepairEventType));
        Assert.All(recovered.Events.Where(item => item.Type == WorkflowEngine.HostRepairEventType),
            item => Assert.Equal(directives[item.Id], item.DataJson));
        Assert.Equal(0, harness.CandidatePublisher.Calls);
    }

    [Fact]
    public async Task LegacyFailedQaDefectReport_RestartsThroughOwnerRepair()
    {
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery, maxHandoffRetries: 2);
        string? scopedReport = null;
        harness.Runner.WorkerOutputOverride = context =>
        {
            if (!context.RequiresDeliveryReadinessQa)
            {
                return null;
            }
            scopedReport = "Response fragment.\n" + DeliveryReadinessFixtures.QaBlockFromPrompt(
                context.OutcomeContext,
                outcome: AiHarnessDemo.Core.Verification.DeliveryCriterionOutcome.Failed,
                evidenceOverride: ["EV-S999-001"]);
            return scopedReport.Replace(DeliveryReadinessFixtures.PlanHashFromPrompt(context.OutcomeContext),
                "sha256:" + new string('0', 64), StringComparison.Ordinal);
        };
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        Assert.Equal(FlowStatus.Failed, (await harness.LoadFlowAsync()).Status);
        await using (var database = await harness.Factory.CreateDbContextAsync())
        {
            var legacy = await database.FlowSteps.SingleAsync(item => item.Status == StepStatus.Failed);
            legacy.OutputSummary = scopedReport!;
            await database.SaveChangesAsync();
        }
        harness.Runner.WorkerOutputOverride = null;

        var queued = await harness.Engine.RestartFailedFlowAsync(harness.FlowId, CancellationToken.None);
        Assert.Equal(FlowStatus.Queued, queued.Status);
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.True(flow.Status == FlowStatus.WaitingForFeedback, flow.FailureReason);
        Assert.Equal(1, flow.Iteration);
        Assert.Single(flow.Events, item => item.Type == "flow.qa-owner-repair-queued");
        Assert.Single(flow.Events, item => item.Type == WorkflowEngine.HostRepairEventType);
        Assert.Single(flow.Steps, item => item.PlanStepKey == "implement" && item.Attempt == 2);
        Assert.Equal(0, harness.CandidatePublisher.Calls);
    }

    [Fact]
    public async Task MixedFailedAndBlockedCriteria_RepairInsteadOfStoppingAtBlocked()
    {
        await using var harness = await ReviewHarness.CreateAsync(FlowKind.Delivery);
        harness.Runner.PlanningOutputOverride = "HANDOFF_STATUS: COMPLETE\n" +
            ReviewHarness.WrapPlan(ReviewHarness.DeliveryPlan(2));
        harness.Runner.QaBlockOverride = context => context.Iteration == 1
            ? DeliveryReadinessFixtures.QaBlockFromPrompt(context.OutcomeContext,
                outcome: AiHarnessDemo.Core.Verification.DeliveryCriterionOutcome.Failed)
                .Replace("\"Verdict\":\"FAIL\"", "\"Verdict\":\"BLOCKED\"", StringComparison.Ordinal)
                .Replace("\"CriterionId\":\"AC-002\",\"Outcome\":\"Failed\"",
                    "\"CriterionId\":\"AC-002\",\"Outcome\":\"Blocked\"", StringComparison.Ordinal)
            : DeliveryReadinessFixtures.QaBlockFromPrompt(context.OutcomeContext);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var repairing = await harness.LoadFlowAsync();
        Assert.True(repairing.Status == FlowStatus.Reworking, repairing.FailureReason);
        Assert.Single(repairing.Events, item => item.Type == "delivery.auto-refinement-scheduled");
        Assert.DoesNotContain(repairing.GateRecords, item => item.ActionType == HandoffActionType.CustomerReview);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var finished = await harness.LoadFlowAsync();
        Assert.True(finished.Status == FlowStatus.WaitingForFeedback, finished.FailureReason);
        Assert.Equal(2, finished.Iteration);
        Assert.Equal(0, harness.CandidatePublisher.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepairedCandidateVerification_GetsOneDistinctBudget(bool alreadyAssigned)
    {
        await using var harness = await ReviewHarness.CreateAsync(FlowKind.Delivery);
        harness.Runner.WorkerOutputOverride = context =>
            context.RequiresDeliveryReadinessQa &&
                (context.Attempt == 1 || !string.IsNullOrWhiteSpace(context.ResponseCorrectionInstructions))
                ? "Response fragment.\n" + DeliveryReadinessFixtures.QaBlockFromPrompt(
                    context.OutcomeContext,
                    outcome: AiHarnessDemo.Core.Verification.DeliveryCriterionOutcome.Failed,
                    evidenceOverride: ["EV-S999-001"])
                : null;
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var expiredDeadline = DateTimeOffset.FromUnixTimeMilliseconds(
            DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());
        Guid oldRoot;
        await using (var database = await harness.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows.Include(item => item.Steps)
                .SingleAsync(item => item.Id == harness.FlowId);
            var qa = flow.Steps.Where(item => item.PlanStepKey == "prepare")
                .OrderBy(item => item.Sequence).ToArray();
            oldRoot = qa[0].Id;
            var failed = qa[^1];
            failed.Status = StepStatus.Failed;
            failed.Phase = AgentRunPhase.BudgetExhausted;
            failed.OutputSummary = string.Empty;
            failed.CopilotSessionId = null;
            failed.ExecutionBudgetRootId = oldRoot;
            flow.Status = FlowStatus.Failed;
            flow.FailureReason = AssignmentExecutionBudget.ExhaustedReason;
            if (!alreadyAssigned)
            {
                database.FlowEvents.RemoveRange(await database.FlowEvents
                    .Where(item => item.FlowRunId == flow.Id &&
                        item.Type == WorkflowEngine.RepairedCandidateAssignmentEventType)
                    .ToListAsync());
            }
            database.AgentExecutionBudgets.Add(new AgentExecutionBudget
            {
                FlowRunId = flow.Id,
                RootStepId = oldRoot,
                AgentId = failed.AgentId,
                StartedAt = expiredDeadline.AddHours(-4),
                DeadlineAt = expiredDeadline,
                PolicyJson = JsonSerializer.Serialize(AssignmentExecutionBudget.Policy(
                    harness.WorkflowProvider.GetEffective().Config.Copilot, failed.WorkflowRevision))
            });
            await database.SaveChangesAsync();
        }

        if (alreadyAssigned)
        {
            await Assert.ThrowsAsync<FlowLifecycleException>(() =>
                harness.Engine.RestartFailedFlowAsync(harness.FlowId, CancellationToken.None));
        }
        else
        {
            await harness.Engine.RestartFailedFlowAsync(harness.FlowId, CancellationToken.None);
            await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
            var recovered = await harness.LoadFlowAsync();
            Assert.True(recovered.Status == FlowStatus.WaitingForFeedback, recovered.FailureReason);
            var verification = recovered.Steps.Last(item => item.PlanStepKey == "prepare");
            Assert.Equal(verification.Id, verification.ExecutionBudgetRootId);
            Assert.NotEqual(oldRoot, verification.ExecutionBudgetRootId);
            Assert.Single(recovered.Events,
                item => item.Type == WorkflowEngine.RepairedCandidateAssignmentEventType);
        }
        await using var check = await harness.Factory.CreateDbContextAsync();
        Assert.Equal(expiredDeadline,
            (await check.AgentExecutionBudgets.SingleAsync(item => item.RootStepId == oldRoot)).DeadlineAt);
        Assert.Equal(0, harness.CandidatePublisher.Calls);
    }

    [Theory]
    [InlineData(AgentRunFailureKind.Transient, false)]
    [InlineData(AgentRunFailureKind.TimedOut, true)]
    [InlineData(AgentRunFailureKind.Stalled, true)]
    public async Task FactoryContinuation_PreservesWorkAndOriginalBudget(
        AgentRunFailureKind kind, bool resume)
    {
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery, maxHandoffRetries: 2);
        var deadline = DateTimeOffset.FromUnixTimeMilliseconds(
            DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds());
        Guid originalId = default;
        var interrupted = false;
        harness.Runner.WorkerOutputOverride = context =>
        {
            if (context.PlanStepKey != "implement")
            {
                return null;
            }
            if (interrupted)
            {
                var instructions = CopilotReasoningHost.RenderAndPersistExecutionInstructionsAsync(
                    context, harness.WorkflowProvider.GetEffective(), "Fixture instructions.",
                    harness.WorkspacePath, null, new WorkflowPromptRenderer(), harness.Factory,
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert.False(instructions.Recovered);
                Assert.Contains("Continue from the preserved workspace", instructions.Prompt);
                return null;
            }
            interrupted = true;
            originalId = context.FlowStepId;
            File.WriteAllText(Path.Combine(harness.WorkspacePath, "preserved.txt"), "existing work");
            using var database = harness.Factory.CreateDbContext();
            var source = database.FlowSteps.Single(item => item.Id == originalId);
            source.ExecutionBudgetRootId = originalId;
            database.AgentExecutionBudgets.Add(new AgentExecutionBudget
            {
                RootStepId = originalId,
                FlowRunId = harness.FlowId,
                AgentId = source.AgentId,
                StartedAt = deadline.AddHours(-1),
                DeadlineAt = deadline,
                PolicyJson = JsonSerializer.Serialize(AssignmentExecutionBudget.Policy(
                    harness.WorkflowProvider.GetEffective().Config.Copilot, source.WorkflowRevision))
            });
            database.SaveChanges();
            throw new AgentRunException("Recoverable execution interruption.", kind, canResumeSession: resume);
        };

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.True(flow.Status == FlowStatus.WaitingForFeedback, flow.FailureReason);
        Assert.DoesNotContain(flow.Events, item => item.Type == "flow.failed");
        var scheduled = Assert.Single(flow.Events,
            item => item.Type == WorkflowEngine.AutomaticContinuationEventType);
        var continuation = JsonSerializer.Deserialize<WorkflowEngine.AutomaticContinuation>(scheduled.DataJson!)!;
        var original = Assert.Single(flow.Steps, item => item.Id == originalId);
        var retry = Assert.Single(flow.Steps, item => item.Id == continuation.RetryStepId);
        Assert.Equal(originalId, retry.StableSemanticRootId);
        Assert.Equal(originalId, retry.ExecutionBudgetRootId);
        Assert.Equal(original.EffectivePermissionJson, retry.EffectivePermissionJson);
        Assert.False(retry.RemotePublicationAllowed);
        Assert.Equal(StepStatus.Completed, retry.Status);
        Assert.Equal("existing work", await File.ReadAllTextAsync(
            Path.Combine(harness.WorkspacePath, "preserved.txt")));
        var execution = Assert.Single(harness.Runner.Contexts,
            item => item.FlowStepId == retry.Id);
        Assert.False(execution.RecoverInterruptedSession);
        Assert.Equal(resume, execution.ResumeSession);
        if (resume)
        {
            Assert.Equal(original.CopilotSessionId, execution.CopilotSessionId);
        }
        Assert.NotEmpty(retry.ExecutionPrompt);
        await using var persisted = await harness.Factory.CreateDbContextAsync();
        Assert.Equal(deadline, (await persisted.AgentExecutionBudgets.SingleAsync()).DeadlineAt);
        Assert.Equal(0, harness.CandidatePublisher.Calls);
    }

    [Fact]
    public async Task FactoryContinuation_StopsRepeatedFailureWithoutObservedProgress()
    {
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery, maxHandoffRetries: 2);
        harness.Runner.WorkerOutputOverride = context => context.PlanStepKey == "implement"
            ? throw new AgentRunException("Same transient failure.", AgentRunFailureKind.Transient)
            : null;

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Failed, flow.Status);
        Assert.Single(flow.Events, item => item.Type == WorkflowEngine.AutomaticContinuationEventType);
        Assert.Contains("without new host-observed evidence",
            Assert.Single(flow.Events, item => item.Type == WorkflowEngine.RecoveryExhaustedEventType).Message);
        Assert.Equal(2, harness.Runner.Contexts.Count(item => item.PlanStepKey == "implement"));
        Assert.Contains("preserved", flow.CustomerBlockerMessage);
        Assert.DoesNotContain(flow.GateRecords, item => item.ActionType == HandoffActionType.CustomerReview);
    }

    [Fact]
    public async Task FactoryContinuation_StopsAtLimitEvenWhenFailureChanges()
    {
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery, maxHandoffRetries: 2);
        var failures = 0;
        harness.Runner.WorkerOutputOverride = context => context.PlanStepKey == "implement"
            ? throw new AgentRunException($"Distinct transient failure {++failures}.", AgentRunFailureKind.Transient)
            : null;

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Failed, flow.Status);
        Assert.Equal(3, failures);
        Assert.Equal(2, flow.Events.Count(item => item.Type == WorkflowEngine.AutomaticContinuationEventType));
        Assert.Contains("limit is exhausted",
            Assert.Single(flow.Events, item => item.Type == WorkflowEngine.RecoveryExhaustedEventType).Message);
        await using var database = await harness.Factory.CreateDbContextAsync();
        Assert.Single(await database.AgentExecutionBudgets.ToListAsync());
    }

    [Fact]
    public async Task FactoryContinuation_RetainsFailedToolEvidenceAndRecognizesDifferentChecks()
    {
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery, maxHandoffRetries: 2);
        var failures = 0;
        harness.Runner.WorkerOutputOverride = context =>
        {
            if (context.PlanStepKey != "implement" || failures >= 2)
            {
                return null;
            }
            throw new AgentRunException("Same transient interruption.", AgentRunFailureKind.Transient)
            {
                ToolCalls =
                [
                    new ToolCallRecord("powershell", "A focused check.", true, "Execute",
                        NormalizedCommand: $"dotnet test --filter Check{++failures}",
                        WorkingDirectory: harness.WorkspacePath, ExitCode: 0,
                        ResultDigest: "same-result", ResultSummary: "Passed.")
                ]
            };
        };

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.True(flow.Status == FlowStatus.WaitingForFeedback, flow.FailureReason);
        Assert.Equal(2, flow.Events.Count(item => item.Type == WorkflowEngine.AutomaticContinuationEventType));
        await using var database = await harness.Factory.CreateDbContextAsync();
        var failedSteps = await database.FlowSteps.Include(item => item.ToolCalls)
            .Where(item => item.FlowRunId == flow.Id &&
                item.PlanStepKey == "implement" && item.Status == StepStatus.Failed).ToListAsync();
        Assert.Equal(2, failedSteps.Count);
        Assert.All(failedSteps,
            step => Assert.Single(step.ToolCalls));
        Assert.DoesNotContain(flow.Events, item => item.Type == WorkflowEngine.RecoveryExhaustedEventType);
        Assert.All(flow.Events.Where(item => item.Type == WorkflowEngine.AutomaticContinuationEventType),
            item => Assert.Contains(flow.Events, epoch =>
                epoch.FlowStepId == item.FlowStepId &&
                epoch.Type == DeliveryReadinessService.EvidenceEpochEventType));
    }

    [Fact]
    public async Task FactoryContinuation_ContinuesPlanningWithoutChangingTheAcceptedOutcome()
    {
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery, maxHandoffRetries: 2);
        var failed = false;
        harness.Runner.WorkerOutputOverride = context =>
        {
            if (context.InvocationKind != ExecutionInvocationKind.Planning || failed)
            {
                return null;
            }
            failed = true;
            throw new AgentRunException("Planning temporarily interrupted.", AgentRunFailureKind.Transient);
        };

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.True(flow.Status == FlowStatus.WaitingForFeedback, flow.FailureReason);
        var entry = Assert.Single(flow.Events,
            item => item.Type == WorkflowEngine.AutomaticContinuationEventType);
        var retry = flow.Steps.Single(item => item.Id == entry.FlowStepId);
        Assert.Equal(ExecutionInvocationKind.Planning, retry.InvocationKind);
        Assert.Equal(ExecutionPermissionProfile.ReadOnlySource, retry.PermissionProfile);
        Assert.Single(flow.Steps, item => item.PlanStepKey == "implement");
        Assert.Equal(0, harness.CandidatePublisher.Calls);
    }

    [Fact]
    public async Task FactoryContinuation_DisabledLimitStopsWithoutLaunchingAnotherAttempt()
    {
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery, maxHandoffRetries: 0);
        harness.Runner.WorkerOutputOverride = context => context.PlanStepKey == "implement"
            ? throw new AgentRunException("Transient interruption.", AgentRunFailureKind.Transient)
            : null;

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Failed, flow.Status);
        Assert.Single(harness.Runner.Contexts, item => item.PlanStepKey == "implement");
        Assert.DoesNotContain(flow.Events, item => item.Type == WorkflowEngine.AutomaticContinuationEventType);
        Assert.Single(flow.Events, item => item.Type == WorkflowEngine.RecoveryExhaustedEventType);
    }

    [Fact]
    public async Task FactoryContinuation_NeverDispatchesWhileProcessTerminationIsUnconfirmed()
    {
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery, maxHandoffRetries: 2);
        harness.Runner.WorkerOutputOverride = context => context.PlanStepKey == "implement"
            ? throw new AgentRunException("A process may still be active.", AgentRunFailureKind.Transient)
            {
                ProcessTerminationUnconfirmed = true
            }
            : null;

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Failed, flow.Status);
        Assert.Single(harness.Runner.Contexts, item => item.PlanStepKey == "implement");
        Assert.DoesNotContain(flow.Events, item => item.Type == WorkflowEngine.AutomaticContinuationEventType);
    }

    [Fact]
    public async Task FactoryContinuation_ManualRecoveryClearsOnlyTheExhaustedRecoveryBlocker()
    {
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery, maxHandoffRetries: 1);
        harness.Runner.WorkerOutputOverride = context => context.PlanStepKey == "implement"
            ? throw new AgentRunException("Repeated interruption.", AgentRunFailureKind.Transient)
            : null;
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        Assert.Equal("factory.recovery-exhausted", (await harness.LoadFlowAsync()).CurrentBlockerCode);
        harness.Runner.WorkerOutputOverride = null;

        await harness.Engine.RestartFailedFlowAsync(harness.FlowId, CancellationToken.None);
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.True(flow.Status == FlowStatus.WaitingForFeedback, flow.FailureReason);
        Assert.Null(flow.CurrentBlockerCode);
        Assert.Null(flow.CustomerBlockerMessage);
    }

    [Theory]
    [InlineData(AgentRunFailureKind.ModelUnavailable)]
    [InlineData(AgentRunFailureKind.DependencyUnavailable)]
    [InlineData(AgentRunFailureKind.InvalidOutput)]
    [InlineData(AgentRunFailureKind.BudgetExhausted)]
    [InlineData(AgentRunFailureKind.AmbiguousCrash)]
    [InlineData(AgentRunFailureKind.Cancelled)]
    [InlineData(AgentRunFailureKind.TimedOut)]
    [InlineData(AgentRunFailureKind.Stalled)]
    public async Task FactoryContinuation_NeverBlindlyRetriesUnsafeOrUnresumableFailures(
        AgentRunFailureKind kind)
    {
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery, maxHandoffRetries: 2);
        harness.Runner.WorkerOutputOverride = context => context.PlanStepKey == "implement"
            ? throw new AgentRunException("Not safe to continue automatically.", kind)
            : null;

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Failed, flow.Status);
        Assert.DoesNotContain(flow.Events, item => item.Type == WorkflowEngine.AutomaticContinuationEventType);
        Assert.Single(harness.Runner.Contexts, item => item.PlanStepKey == "implement");
    }

    [Fact]
    public async Task FactoryContinuation_CannotExtendAnExpiredBudget()
    {
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery, maxHandoffRetries: 2);
        var deadline = DateTimeOffset.FromUnixTimeMilliseconds(
            DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds());
        harness.Runner.WorkerOutputOverride = context =>
        {
            if (context.PlanStepKey != "implement")
            {
                return null;
            }
            using var database = harness.Factory.CreateDbContext();
            var source = database.FlowSteps.Single(item => item.Id == context.FlowStepId);
            source.ExecutionBudgetRootId = source.Id;
            database.AgentExecutionBudgets.Add(new AgentExecutionBudget
            {
                RootStepId = source.Id,
                FlowRunId = harness.FlowId,
                AgentId = source.AgentId,
                StartedAt = deadline.AddHours(-1),
                DeadlineAt = deadline,
                PolicyJson = JsonSerializer.Serialize(AssignmentExecutionBudget.Policy(
                    harness.WorkflowProvider.GetEffective().Config.Copilot, source.WorkflowRevision))
            });
            database.SaveChanges();
            throw new AgentRunException("Transient failure.", AgentRunFailureKind.Transient);
        };

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Failed, flow.Status);
        Assert.DoesNotContain(flow.Events, item => item.Type == WorkflowEngine.AutomaticContinuationEventType);
        Assert.Contains("absolute execution budget",
            Assert.Single(flow.Events, item => item.Type == WorkflowEngine.RecoveryExhaustedEventType).Message);
        await using var persisted = await harness.Factory.CreateDbContextAsync();
        Assert.Equal(deadline, (await persisted.AgentExecutionBudgets.SingleAsync()).DeadlineAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FactoryContinuation_SurvivesShutdownDuringPersistedBackoff(bool expireBudget)
    {
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery, maxHandoffRetries: 2, recoveryDelayMs: 60_000);
        var failed = false;
        harness.Runner.WorkerOutputOverride = context =>
        {
            if (context.PlanStepKey != "implement" || failed)
            {
                return null;
            }
            failed = true;
            throw new AgentRunException("Transient interruption.", AgentRunFailureKind.Transient);
        };
        using var cancellation = new CancellationTokenSource();
        var running = harness.Engine.RunAsync(harness.FlowId, cancellation.Token);
        WorkflowEngine.AutomaticContinuation? continuation = null;
        for (var attempt = 0; attempt < 250; attempt++)
        {
            var flow = await harness.LoadFlowAsync();
            var entry = flow.Events.SingleOrDefault(
                item => item.Type == WorkflowEngine.AutomaticContinuationEventType);
            if (entry is not null)
            {
                continuation = JsonSerializer.Deserialize<WorkflowEngine.AutomaticContinuation>(entry.DataJson!);
                break;
            }
            await Task.Delay(20);
        }
        Assert.NotNull(continuation);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await harness.Engine.RecoverInterruptedFlowsAsync(CancellationToken.None);
        await harness.Engine.RecoverInterruptedFlowsAsync(CancellationToken.None);
        var recovered = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Queued, recovered.Status);
        Assert.Single(recovered.Events, item => item.Type == WorkflowEngine.AutomaticContinuationEventType);
        Assert.Equal(StepStatus.Pending,
            recovered.Steps.Single(item => item.Id == continuation.RetryStepId).Status);
        var stored = JsonSerializer.Deserialize<WorkflowEngine.AutomaticContinuation>(
            recovered.Events.Single(item => item.Type == WorkflowEngine.AutomaticContinuationEventType).DataJson!)!;
        Assert.Equal(continuation.DueAt, stored.DueAt);
        // Advance only the fixture's scheduled time; the absolute budget is unchanged.
        await using (var database = await harness.Factory.CreateDbContextAsync())
        {
            var entry = await database.FlowEvents.SingleAsync(
                item => item.Type == WorkflowEngine.AutomaticContinuationEventType);
            entry.DataJson = JsonSerializer.Serialize(stored with { DueAt = DateTimeOffset.UtcNow.AddSeconds(-1) });
            if (expireBudget)
            {
                var budget = await database.AgentExecutionBudgets.SingleAsync();
                budget.DeadlineAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            }
            await database.SaveChangesAsync();
        }
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var finished = await harness.LoadFlowAsync();
        if (expireBudget)
        {
            Assert.Equal(FlowStatus.Failed, finished.Status);
            Assert.Single(harness.Runner.Contexts, item => item.PlanStepKey == "implement");
            Assert.Equal(AgentRunPhase.BudgetExhausted,
                finished.Steps.Single(item => item.Id == stored.RetryStepId).Phase);
            return;
        }
        Assert.True(finished.Status == FlowStatus.WaitingForFeedback, finished.FailureReason);
        Assert.Single(finished.Events, item => item.Type == WorkflowEngine.AutomaticContinuationEventType);
        Assert.Equal(2, harness.Runner.Contexts.Count(item => item.PlanStepKey == "implement"));
    }

    [Fact]
    public async Task FactoryContinuation_NeverRepeatsPublicationAutomatically()
    {
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery, maxHandoffRetries: 2);
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var flow = await harness.LoadFlowAsync();
        await harness.ReviewAsync(new DirectReviewRequest
        {
            GateId = flow.GateRecords.Single(item => item.ActionType == HandoffActionType.CustomerReview).Id,
            Intent = ReviewIntent.Accept
        });
        harness.Runner.PublicationOutputFactory = _ =>
            throw new AgentRunException("Remote publication is uncertain.", AgentRunFailureKind.Transient);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var failed = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Failed, failed.Status);
        Assert.DoesNotContain(failed.Events, item => item.Type == WorkflowEngine.AutomaticContinuationEventType);
        Assert.Single(harness.Runner.Contexts, item => item.InvocationKind == ExecutionInvocationKind.Publication);
    }

    [Fact]
    public async Task HostPreflight_RepairsThroughOwnerEvenWhenVerifierIgnoresPushback()
    {
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery, enablePreviewPreparation: true);
        harness.Runner.WorkerOutputOverride = context =>
        {
            if (context.PlanStepKey == "implement" && context.Attempt > 1)
            {
                var preview = Path.Combine(harness.WorkspacePath, ".customer-preview", "browser");
                Directory.CreateDirectory(preview);
                File.WriteAllText(Path.Combine(preview, "index.html"), "repaired by accepted owner");
            }
            return null;
        };

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.True(flow.Status == FlowStatus.WaitingForFeedback, flow.FailureReason);
        var directed = Assert.Single(flow.Events, item => item.Type == WorkflowEngine.HostRepairEventType);
        var blocked = flow.Steps.Single(item => item.Id == directed.FlowStepId);
        Assert.Contains("HANDOFF_STATUS: COMPLETE", blocked.OutputSummary);
        Assert.Equal(StepStatus.Pushback, blocked.Status);
        Assert.Single(flow.Steps, item => item.PlanStepKey == "implement" && item.Attempt == 2);
        Assert.DoesNotContain(flow.Events, item => item.Type == "flow.failed");
        Assert.Equal(0, harness.CandidatePublisher.Calls);
    }

    [Fact]
    public async Task BehaviorAcceptance_CannotUseScreenshotOnlyEvidence()
    {
        await using var harness = await ReviewHarness.CreateAsync(FlowKind.Delivery);
        harness.Runner.PlanningOutputOverride = "HANDOFF_STATUS: COMPLETE" + Environment.NewLine +
            ReviewHarness.WrapPlan(ReviewHarness.DeliveryPlan())
            .Replace("\"Observation\"", "\"Test\"", StringComparison.Ordinal);
        harness.Runner.VerificationToolCallsOverride = _ =>
        [
            new ToolCallRecord("view_image", "A static screenshot.",
                Succeeded: true, ToolType: "Read", ResultSummary: "Viewed image file successfully.")
        ];
        harness.Runner.QaBlockOverride = context =>
            DeliveryReadinessFixtures.QaBlockFromPrompt(context.OutcomeContext,
                evidenceOverride: [DeliveryReadinessFixtures.CurrentEvidenceIdFromPrompt(context.OutcomeContext)]);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Failed, flow.Status);
        Assert.Contains("not allowed", flow.FailureReason);
        Assert.DoesNotContain(flow.GateRecords, item => item.ActionType == HandoffActionType.CustomerReview);
        Assert.Contains("screenshots prove rendering only",
            harness.Runner.Contexts.Last(item => item.RequiresDeliveryReadinessQa).OutcomeContext);
    }
}

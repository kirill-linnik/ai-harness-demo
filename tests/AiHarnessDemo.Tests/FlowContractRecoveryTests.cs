using System.Collections.Immutable;
using System.Text.Json;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Security;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Services;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Tests;

public sealed class FlowContractRecoveryTests
{
    [Fact]
    public void QaCitationLimit_IsRepeatedInInitialCorrectionAndLegacyRestartAssignments()
    {
        var source = new FlowStep
        {
            AgentId = "quality-engineer",
            AgentName = "Quality Engineer",
            AgentRole = "quality-engineer",
            PlanDutiesJson = """["Verify","PrepareOutcome"]""",
            IsOutcomeOwner = true,
            OutputSummary = "HANDOFF_STATUS: COMPLETE\nRetained independent verification."
        };
        var failure =
            $"criterion 'AC-003' evidenceIds must contain at most " +
            $"{DeliveryReadinessPolicy.MaximumEvidenceIdsPerItem} entries";
        var citationContract = WorkflowEngine.DeliveryQaEvidenceCitationContract();
        var assignments = new[]
        {
            WorkflowEngine.DeliveryQaResponseContract("unchanged-acceptance-plan-hash"),
            WorkflowEngine.BuildStudioContractCorrectionAssignment(source, failure),
            WorkflowEngine.BuildVerificationRestartAssignment("Verify the unchanged candidate.", failure)
        };

        Assert.Contains(
            $"at most {DeliveryReadinessPolicy.MaximumEvidenceIdsPerItem} unique host-issued identifiers",
            citationContract);
        Assert.Contains("do not invent aggregate evidence", citationContract);
        Assert.Contains("Failed or Blocked", citationContract);
        Assert.All(assignments, assignment => Assert.Contains(citationContract, assignment));
        Assert.Contains(source.OutputSummary, assignments[1]);
        Assert.Contains("Do not rerun tools or modify", assignments[1]);
        Assert.Contains("permission ceiling", assignments[2]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedQaJournal_QueuesOnlyOneCorrectionAndRetainsExecutionEvidence(
        bool manuallyRecoverFailedFlow)
    {
        var plan = DeliveryReadinessFixtures.Plan();
        var hash = DeliveryReadinessPolicy.HashAcceptancePlan(plan);
        var output = $$"""
            HANDOFF_STATUS: COMPLETE
            FLOW_OUTCOME_BEGIN
            {"Goal":"Deliver the redesigned sites.","Summary":"Verification finished.","ImplementationDetails":["The independent checks were recorded."],"Artifacts":[]}
            FLOW_OUTCOME_END
            OUTCOME_QA_BEGIN
            {"AcceptancePlanHash":"{{hash}}","Verdict":"PASS","Criteria":[{"CriterionId":"AC-001","Outcome":"Verified","EvidenceIds":["EV-S040-001"],"Rationale":"The tool check passed.","Remediation":null,"ResponsibleRoles":[]}],"ResidualRisks":[],"PlanGaps":[]}
            OUTCOME_QA_END
            """;
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: true,
            persistSessionId: true,
            includeToolCall: true,
            completedOutput: output,
            isOutcomeOwner: true);
        Guid sourceId;
        await using (var database = await fixture.DatabaseFactory.CreateDbContextAsync())
        {
            var flow = await database.Flows.Include(item => item.Steps).SingleAsync();
            flow.Kind = FlowKind.Delivery;
            var source = Assert.Single(flow.Steps);
            sourceId = source.Id;
            source.PlanDutiesJson = """["Verify","PrepareOutcome"]""";
            source.InputSummary = "Independently verify the completed candidate.";
            source.PermissionProfile = ExecutionPermissionProfile.WorkspaceWrite;
            source.EffectivePermissionJson = JsonSerializer.Serialize(
                new PermissionProfileResolver().Resolve(
                    new PermissionResolutionRequest(
                        FlowKind.Delivery,
                        ExecutionInvocationKind.Worker,
                        PlanStage.BeforeReview,
                        [PlanDuty.Verify, PlanDuty.PrepareOutcome],
                        DurableReviewDecision: null,
                        DurableApproval: false,
                        IsOnlyPlannedPublishStep: false),
                    new WorkflowPermissionRestrictions(
                        ExecutionPermissionProfile.ReadOnlySource,
                        ExecutionPermissionProfile.WorkspaceWrite,
                        ExecutionPermissionProfile.Publish,
                        ImmutableDictionary<ExecutionPermissionProfile, ImmutableArray<string>>.Empty)));
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                Type = DeliveryReadinessService.AcceptancePlanEventType,
                Message = "Acceptance criteria recorded.",
                DataJson = DeliveryReadinessService.SerializeAcceptancePlan(plan, 1, Guid.NewGuid())
            });
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = source.Id,
                Type = DeliveryReadinessService.EvidenceEventType,
                Message = "Reserved the original evidence prefix.",
                DataJson = DeliveryReadinessService.SerializeEvidence(
                    DeliveryReadinessService.BuildEvidence(source, []))
            });
            source.Sequence = 80;
            if (manuallyRecoverFailedFlow)
            {
                flow.Status = FlowStatus.Failed;
                flow.FailureReason = "The QA evidence kind was invalid.";
                source.Status = StepStatus.Failed;
                source.Phase = AgentRunPhase.Failed;
                source.OutputSummary = output;
            }
            await database.SaveChangesAsync();
        }

        if (manuallyRecoverFailedFlow)
        {
            var recovered = await fixture.Engine.RestartFailedFlowAsync(
                fixture.FlowId, CancellationToken.None);
            Assert.Equal(FlowStatus.Queued, recovered.Status);
        }
        else
        {
            Assert.Contains(
                fixture.FlowId,
                await fixture.Engine.RecoverInterruptedFlowsAsync(CancellationToken.None));
        }
        await fixture.Engine.RecoverInterruptedFlowsAsync(CancellationToken.None);
        await fixture.Engine.RecoverInterruptedFlowsAsync(CancellationToken.None);

        await using var persisted = await fixture.DatabaseFactory.CreateDbContextAsync();
        var stored = await persisted.Flows
            .Include(item => item.Steps).ThenInclude(step => step.ToolCalls)
            .Include(item => item.Events)
            .Include(item => item.GateRecords)
            .SingleAsync();
        Assert.Equal(FlowStatus.Queued, stored.Status);
        Assert.Empty(stored.FailureReason);
        Assert.Equal(fixture.WorkspacePath, stored.WorkspacePath);
        var original = Assert.Single(stored.Steps, step => step.Id == sourceId);
        var correction = Assert.Single(stored.Steps, step => step.RetryOfStepId == sourceId);
        Assert.Equal(StepStatus.Completed, original.Status);
        Assert.Equal(output, original.OutputSummary);
        Assert.Equal(1, original.ExecutionAttempts);
        Assert.Single(original.ToolCalls);
        Assert.Equal(StepStatus.Pending, correction.Status);
        Assert.Equal(original.WorkflowRevision, correction.WorkflowRevision);
        Assert.Contains("not allowed by the acceptance plan", correction.InputSummary);
        Assert.Contains(output, correction.InputSummary);
        var permission = JsonSerializer.Deserialize<EffectiveExecutionPermission>(
            correction.EffectivePermissionJson)!;
        Assert.Contains("write", permission.DeniedTools);
        Assert.Contains("shell", permission.DeniedTools);
        Assert.All(permission.AllowedTools, tool => Assert.Contains(tool, new[] { "view", "grep", "glob" }));
        var continuationSource = await WorkflowEngine.ResolveTaskPermissionSourceAsync(
            persisted, correction, CancellationToken.None);
        Assert.Equal(original.Id, continuationSource.Id);
        Assert.Equal(original.EffectivePermissionJson, continuationSource.EffectivePermissionJson);
        Assert.DoesNotContain(
            "shell",
            JsonSerializer.Deserialize<EffectiveExecutionPermission>(
                continuationSource.EffectivePermissionJson)!.DeniedTools);
        Assert.Equal(
            correction.EffectivePermissionJson,
            (await persisted.FlowSteps.AsNoTracking()
                .SingleAsync(step => step.Id == correction.Id)).EffectivePermissionJson);
        var evidence = DeliveryReadinessService.ReadStepEvidence(stored.Events, 1, sourceId)!;
        Assert.Equal(40, evidence.Sequence);
        Assert.Equal("EV-S040-001", evidence.Items[1].EvidenceId);
        Assert.Equal(OutcomeEvidenceKind.Test, evidence.Items[1].Kind);
        Assert.Single(stored.Events, item => item.Type == "agent.contract-correction-scheduled");
        Assert.DoesNotContain(stored.Events, item => item.Type == DeliveryReadinessService.QaEventType);
        Assert.Empty(stored.GateRecords);
    }
}

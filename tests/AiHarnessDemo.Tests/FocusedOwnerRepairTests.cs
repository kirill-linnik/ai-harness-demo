using System.Text.Json;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Security;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Services;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Tests;

public sealed partial class ReviewWorkflowTests
{
    private static string QaReworkResponse(AgentExecutionContext context, string owner = "implement") =>
        $"HANDOFF_STATUS: PUSHBACK\nPUSHBACK_OWNER_STEP_ID: {owner}\n" +
        "PUSHBACK_REASON: Add the missing executable verification infrastructure.\n" +
        "FLOW_OUTCOME_BEGIN\n" +
        """{"Goal":"Deliver the confirmed result.","Summary":"Implementation needs executable verification infrastructure.","ImplementationDetails":["Existing work is preserved; independent verification has not passed."],"Artifacts":[]}""" +
        "\nFLOW_OUTCOME_END\n" +
        DeliveryReadinessFixtures.QaBlockFromPrompt(context.OutcomeContext, outcome: DeliveryCriterionOutcome.Blocked);

    [Theory]
    [InlineData("nonexistent")]
    [InlineData("publish")]
    public async Task QaRework_InvalidOwnerMustCorrectBeforeNormalizationOrRepair(string owner)
    {
        await using var harness = await ReviewHarness.CreateAsync(FlowKind.Delivery);
        harness.Runner.WorkerOutputOverride = context =>
            context.RequiresDeliveryReadinessQa && context.Attempt == 1 ? QaReworkResponse(context, owner) : null;
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var flow = await harness.LoadFlowAsync();
        Assert.True(flow.Status == FlowStatus.WaitingForFeedback, flow.FailureReason);
        var correctionEvent = Assert.Single(flow.Events, item => item.Type == "agent.contract-correction-scheduled");
        var correction = flow.Steps.Single(item => item.Id == correctionEvent.FlowStepId);
        Assert.Contains("earlier dependency", correction.InputSummary);
        Assert.DoesNotContain(flow.Events, item => item.FlowStepId == correction.RetryOfStepId &&
            item.Type is "handoff.qa-rework-assessment-recorded" or WorkflowEngine.HostRepairEventType);
        var contexts = harness.Runner.Contexts.Where(item => item.RequiresDeliveryReadinessQa).ToArray();
        Assert.Equal(contexts[0].CopilotSessionId, contexts[1].CopilotSessionId);
        Assert.True(contexts[1].ResumeSession);
        Assert.Equal(0, harness.CandidatePublisher.Calls);
    }

    [Theory]
    [InlineData("nonexistent", true)]
    [InlineData("publish", true)]
    [InlineData("nonexistent", false)]
    [InlineData("publish", false)]
    public async Task QaRework_InvalidOwnerCannotBeRescuedFromItsCorrectionSource(
        string owner, bool malformedCorrection)
    {
        await using var harness = await ReviewHarness.CreateAsync(FlowKind.Delivery);
        harness.Runner.WorkerOutputOverride = context => context.RequiresDeliveryReadinessQa
            ? !string.IsNullOrWhiteSpace(context.ResponseCorrectionInstructions) && malformedCorrection
                ? "The correction is malformed and contains no handoff or QA document."
                : QaReworkResponse(context, owner)
            : null;
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var flow = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Failed, flow.Status);
        Assert.Single(flow.Events, item => item.Type == "agent.contract-correction-scheduled");
        Assert.DoesNotContain(flow.Events, item => item.Type is WorkflowEngine.HostRepairEventType or
            "handoff.qa-rework-assessment-recorded");
        await using var database = await harness.Factory.CreateDbContextAsync();
        var failed = flow.Steps.Last(item => item.PlanStepKey == "prepare");
        Assert.Null(await WorkflowEngine.TryBuildQaOwnerRepairAsync(database, flow, failed,
            failed.OutputSummary, CancellationToken.None));
        Assert.Single(flow.Steps, item => item.PlanStepKey == "implement");
        Assert.Equal(0, harness.CandidatePublisher.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task QaRework_CompletedJournalUsesTheSameRepairAndReplanningPath(int limit)
    {
        await using var harness = await ReviewHarness.CreateAsync(FlowKind.Delivery, maxHandoffRetries: limit);
        using var interrupted = new CancellationTokenSource();
        AgentExecutionContext? captured = null;
        harness.Runner.WorkerOutputOverride = context =>
        {
            if (!context.RequiresDeliveryReadinessQa)
            {
                return null;
            }
            captured = context;
            interrupted.Cancel();
            throw new OperationCanceledException(interrupted.Token);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Engine.RunAsync(harness.FlowId, interrupted.Token));
        Assert.NotNull(captured);
        await harness.Engine.RecoverCompletedStepAsync(harness.FlowId, captured.FlowStepId,
            captured.CopilotSessionId, new AgentRunResult
            {
                Success = true, OutputSummary = QaReworkResponse(captured),
                ToolCalls = [new("observe", "Retained interrupted QA observation.", true, ToolType: "Observation")]
            }, DateTimeOffset.UtcNow, CancellationToken.None);
        harness.Runner.WorkerOutputOverride = null;
        harness.Runner.QaBlockOverride = context => DeliveryReadinessFixtures.QaBlockFromPrompt(
            context.OutcomeContext, outcome: context.Iteration == 1 ? DeliveryCriterionOutcome.Blocked : DeliveryCriterionOutcome.Verified);
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var reworking = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Reworking, reworking.Status);
        Assert.Equal(2, reworking.Iteration);
        Assert.Equal(limit, reworking.Events.Count(item => item.Type == WorkflowEngine.HostRepairEventType));
        Assert.Single(reworking.Events, item => item.FlowStepId == captured.FlowStepId &&
            item.Type == "handoff.qa-rework-assessment-recorded");
        Assert.DoesNotContain(reworking.Events, item => item.Type == "flow.failed");
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var final = await harness.LoadFlowAsync();
        Assert.True(final.Status == FlowStatus.WaitingForFeedback, final.FailureReason);
        Assert.Equal(2, harness.Runner.Contexts.Count(item => item.AgentId == "team-lead"));
        Assert.Single(final.GateRecords, item => item.ActionType == HandoffActionType.CustomerReview);
        Assert.Equal(0, harness.CandidatePublisher.Calls);
    }

    [Theory]
    [InlineData(0, 1, 2, DeliveryCriterionOutcome.Failed)]
    [InlineData(1, 2, 2, DeliveryCriterionOutcome.Failed)]
    [InlineData(2, 3, 1, DeliveryCriterionOutcome.Failed)]
    [InlineData(0, 1, 2, DeliveryCriterionOutcome.Blocked)]
    [InlineData(1, 2, 2, DeliveryCriterionOutcome.Blocked)]
    [InlineData(2, 3, 1, DeliveryCriterionOutcome.Blocked)]
    public async Task FocusedQaRepair_UsesRemainingRoundsBeforeReplanning(
        int limit, int expectedQaCalls, int expectedIteration, DeliveryCriterionOutcome unmetOutcome)
    {
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery, maxHandoffRetries: limit);
        var qaCalls = 0;
        harness.Runner.QaBlockOverride = context =>
            DeliveryReadinessFixtures.QaBlockFromPrompt(context.OutcomeContext,
                outcome: ++qaCalls <= 2 ? unmetOutcome : DeliveryCriterionOutcome.Verified);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.Equal(expectedQaCalls, qaCalls);
        Assert.Equal(expectedIteration, flow.Iteration);
        Assert.Single(flow.Steps, item => item.AgentId == "team-lead");
        Assert.Equal(limit, flow.Steps.Count(item =>
            item.PlanStepKey == "implement" && item.Attempt > 1));
        Assert.Equal(limit, flow.Events.Count(item => item.Type == WorkflowEngine.HostRepairEventType));
        Assert.Equal(limit, flow.Events.Count(item => item.Type == WorkflowEngine.RepairedCandidateAssignmentEventType));
        Assert.Equal(0, harness.CandidatePublisher.Calls);
        if (limit == 2)
        {
            Assert.True(flow.Status == FlowStatus.WaitingForFeedback, flow.FailureReason);
            Assert.DoesNotContain(flow.Events, item => item.Type == WorkflowEngine.AutoRefinementScheduledEventType);
            var review = Assert.Single(flow.GateRecords, item => item.ActionType == HandoffActionType.CustomerReview);
            Assert.False(review.Resolved);
            await using var database = await harness.Factory.CreateDbContextAsync();
            var readiness = await database.DeliveryReadinessSnapshots.SingleAsync(item => item.Active);
            Assert.Equal(DeliveryReadinessState.ReadyToApprove, readiness.State);
            Assert.Equal(flow.Steps.Last(item => item.PlanStepKey == "prepare").Id, readiness.QaStepId);
        }
        else
        {
            Assert.Equal(FlowStatus.Reworking, flow.Status);
            Assert.Single(flow.Events, item => item.Type == WorkflowEngine.AutoRefinementScheduledEventType);
            Assert.DoesNotContain(flow.GateRecords, item => item.ActionType == HandoffActionType.CustomerReview);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task QaPushback_AssessmentReplansAfterLocalDisagreementWithoutFailing(int limit)
    {
        await using var harness = await ReviewHarness.CreateAsync(FlowKind.Delivery, maxHandoffRetries: limit);
        harness.Runner.QaBlockOverride = context =>
            DeliveryReadinessFixtures.QaBlockFromPrompt(context.OutcomeContext, outcome: DeliveryCriterionOutcome.Blocked);
        harness.Runner.WorkerOutputOverride = context => context.RequiresDeliveryReadinessQa
            ? QaReworkResponse(context)
            : null;

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Reworking, flow.Status);
        Assert.Equal(2, flow.Iteration);
        Assert.Equal(limit + 1, harness.Runner.Contexts.Count(context => context.RequiresDeliveryReadinessQa));
        Assert.Contains(flow.Events, item => item.Type == "handoff.qa-rework-assessment-recorded");
        Assert.Single(flow.Events, item => item.Type == WorkflowEngine.AutoRefinementScheduledEventType);
        Assert.DoesNotContain(flow.Events, item => item.Type == "flow.failed");
        Assert.DoesNotContain(flow.GateRecords, item => item.ActionType == HandoffActionType.CustomerReview);
        Assert.Equal(0, harness.CandidatePublisher.Calls);
        Assert.Contains("plan-step ID: implement", harness.Runner.Contexts.First(context => context.RequiresDeliveryReadinessQa).QaImplementationOwner);
    }

    [Fact]
    public async Task FocusedQaRepair_ResponseCorrectionDoesNotBecomeEditingCeiling()
    {
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery, maxHandoffRetries: 2);
        harness.Runner.WorkerOutputOverride = context =>
            context.PlanStepKey == "implement" && context.Attempt == 1
                ? "The implementation exists, but this response lacks its handoff marker."
                : null;
        harness.Runner.QaBlockOverride = context =>
            DeliveryReadinessFixtures.QaBlockFromPrompt(context.OutcomeContext,
                outcome: context.Attempt == 1 ? DeliveryCriterionOutcome.Failed : DeliveryCriterionOutcome.Verified);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.True(flow.Status == FlowStatus.WaitingForFeedback, flow.FailureReason);
        Assert.Equal(1, flow.Iteration);
        var original = flow.Steps.Single(item => item.PlanStepKey == "implement" && item.Attempt == 1);
        var correction = flow.Steps.Single(item => item.PlanStepKey == "implement" && item.Attempt == 2);
        var repair = flow.Steps.Single(item => item.PlanStepKey == "implement" && item.Attempt == 3);
        var correctionPermission = JsonSerializer.Deserialize<EffectiveExecutionPermission>(correction.EffectivePermissionJson)!;
        Assert.Equal(["view", "grep", "glob"], correctionPermission.AllowedTools.ToArray());
        Assert.Contains("write", correctionPermission.DeniedTools);
        Assert.Equal(original.EffectivePermissionJson, repair.EffectivePermissionJson);
        var permission = JsonSerializer.Deserialize<EffectiveExecutionPermission>(repair.EffectivePermissionJson)!;
        Assert.Contains("edit", permission.AllowedTools);
        Assert.Contains("powershell", permission.AllowedTools);
        Assert.DoesNotContain("write", permission.DeniedTools);
        Assert.False(repair.RemotePublicationAllowed);
        Assert.Contains("REPAIR_STATUS:", repair.InputSummary);
        Assert.DoesNotContain(flow.Events, item => item.Type == WorkflowEngine.AutoRefinementScheduledEventType);
    }

    [Fact]
    public async Task FocusedQaRepair_CurrentReadOnlyPolicyBlocksBeforeOwnerDispatch()
    {
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery, maxHandoffRetries: 2);
        var changedPolicy = false;
        harness.Runner.QaBlockOverride = context =>
        {
            if (!changedPolicy)
            {
                changedPolicy = true;
                var path = Path.Combine(harness.Root, "WORKFLOW.md");
                File.WriteAllText(path, File.ReadAllText(path).Replace(
                    "pre_review_maximum_permission: WorkspaceWrite",
                    "pre_review_maximum_permission: ReadOnlySource", StringComparison.Ordinal));
                harness.WorkflowProvider.ReloadAsync().GetAwaiter().GetResult();
            }
            return DeliveryReadinessFixtures.QaBlockFromPrompt(context.OutcomeContext,
                outcome: DeliveryCriterionOutcome.Failed);
        };

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Blocked, flow.Status);
        Assert.Equal(WorkflowEngine.OwnerRepairBlockedEventType, flow.CurrentBlockerCode);
        Assert.Single(harness.Runner.Contexts, item => item.PlanStepKey == "implement");
        Assert.Single(harness.Runner.Contexts, item => item.RequiresDeliveryReadinessQa);
        var repair = flow.Steps.Last(item => item.PlanStepKey == "implement");
        Assert.Equal(ExecutionPermissionProfile.ReadOnlySource, repair.PermissionProfile);
        Assert.Contains("no authorized editing capability", flow.CurrentBlockerSummary);
        Assert.DoesNotContain(flow.GateRecords, item => item.ActionType == HandoffActionType.CustomerReview);
        Assert.Equal(0, harness.CandidatePublisher.Calls);
    }

    [Theory]
    [InlineData("write", false)]
    [InlineData("shell", false)]
    [InlineData("create, edit, shell", true)]
    [InlineData("write, powershell, bash", true)]
    [InlineData("write, shell", true)]
    public async Task FocusedQaRepair_EditingCapabilitiesRespectIndependentToolDenials(
        string deniedTools, bool expectedBlocked)
    {
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery, maxHandoffRetries: 2);
        var path = Path.Combine(harness.Root, "WORKFLOW.md");
        var workflow = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        var updated = workflow.Replace(
            "workspace_write:\n      additional_denied_tools: []",
            $"workspace_write:\n      additional_denied_tools: [{deniedTools}]",
            StringComparison.Ordinal);
        Assert.NotEqual(workflow, updated);
        File.WriteAllText(path, updated);
        await harness.WorkflowProvider.ReloadAsync();
        harness.Runner.QaBlockOverride = context =>
            DeliveryReadinessFixtures.QaBlockFromPrompt(context.OutcomeContext,
                outcome: context.Attempt == 1 ? DeliveryCriterionOutcome.Failed : DeliveryCriterionOutcome.Verified);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        var original = flow.Steps.Single(item => item.PlanStepKey == "implement" && item.Attempt == 1);
        var repair = flow.Steps.Single(item => item.PlanStepKey == "implement" && item.Attempt == 2);
        Assert.Equal(original.EffectivePermissionJson, repair.EffectivePermissionJson);
        var permission = JsonSerializer.Deserialize<EffectiveExecutionPermission>(repair.EffectivePermissionJson)!;
        foreach (var denied in deniedTools.Split(", ", StringSplitOptions.None))
        {
            Assert.Contains(denied, permission.DeniedTools);
        }
        Assert.Equal(1, flow.Iteration);
        Assert.Equal(0, harness.CandidatePublisher.Calls);
        if (expectedBlocked)
        {
            Assert.Equal(FlowStatus.Blocked, flow.Status);
            Assert.Single(harness.Runner.Contexts, item => item.PlanStepKey == "implement");
            Assert.Single(harness.Runner.Contexts, item => item.RequiresDeliveryReadinessQa);
            Assert.Single(flow.Events, item => item.Type == WorkflowEngine.OwnerRepairBlockedEventType);
            Assert.DoesNotContain(flow.GateRecords, item => item.ActionType == HandoffActionType.CustomerReview);
        }
        else
        {
            Assert.True(flow.Status == FlowStatus.WaitingForFeedback, flow.FailureReason);
            Assert.Equal(2, harness.Runner.Contexts.Count(item => item.PlanStepKey == "implement"));
            Assert.Equal(2, harness.Runner.Contexts.Count(item => item.RequiresDeliveryReadinessQa));
            Assert.DoesNotContain(flow.Events, item => item.Type == WorkflowEngine.OwnerRepairBlockedEventType);
            Assert.Single(flow.GateRecords, item => item.ActionType == HandoffActionType.CustomerReview);
        }
    }

    [Fact]
    public async Task FocusedQaRepair_BlockedInvestigationDoesNotDispatchFreshQa()
    {
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery, maxHandoffRetries: 2);
        harness.Runner.QaBlockOverride = context =>
            DeliveryReadinessFixtures.QaBlockFromPrompt(context.OutcomeContext,
                outcome: DeliveryCriterionOutcome.Failed);
        harness.Runner.WorkerOutputOverride = context =>
            WorkflowEngine.RequiresOwnerRepairStatus(context)
                ? "HANDOFF_STATUS: COMPLETE\nREPAIR_STATUS: BLOCKED\nInvestigation complete; no repair was performed."
                : null;

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Blocked, flow.Status);
        Assert.Single(harness.Runner.Contexts, item => item.RequiresDeliveryReadinessQa);
        Assert.Single(flow.Events, item => item.Type == WorkflowEngine.OwnerRepairBlockedEventType);
        Assert.DoesNotContain(flow.Events, item => item.Type == WorkflowEngine.AutoRefinementScheduledEventType);
        Assert.DoesNotContain(flow.GateRecords, item => item.ActionType == HandoffActionType.CustomerReview);
        Assert.Contains(flow.Steps, item => item.PlanStepKey == "prepare" && item.Status == StepStatus.Pending);
    }

    [Fact]
    public async Task FocusedQaRepair_OutputCorrectionDoesNotConsumeAnOwnerRepairRound()
    {
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery, maxHandoffRetries: 2);
        harness.Runner.WorkerOutputOverride = context =>
            context.RequiresDeliveryReadinessQa && context.Attempt == 1
                ? "HANDOFF_STATUS: COMPLETE\nFLOW_OUTCOME_BEGIN\n" +
                  """{"Goal":"Produce a customer-reviewable result.","Summary":"Defects need repair.","ImplementationDetails":["Unverified defects were reported."],"Artifacts":[]}""" +
                  "\nFLOW_OUTCOME_END\n" +
                  DeliveryReadinessFixtures.QaBlockFromPrompt(context.OutcomeContext,
                      outcome: DeliveryCriterionOutcome.Failed, evidenceOverride: ["EV-S999-001"])
                : null;
        harness.Runner.QaBlockOverride = context =>
            DeliveryReadinessFixtures.QaBlockFromPrompt(context.OutcomeContext,
                outcome: context.Attempt == 2 ? DeliveryCriterionOutcome.Failed : DeliveryCriterionOutcome.Verified);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.True(flow.Status == FlowStatus.WaitingForFeedback, flow.FailureReason);
        Assert.Equal(1, flow.Iteration);
        Assert.Single(flow.Steps, item => item.PlanStepKey == "implement" && item.Attempt > 1);
        Assert.Equal(3, harness.Runner.Contexts.Count(item => item.RequiresDeliveryReadinessQa));
        var directives = flow.Events.Where(item => item.Type == WorkflowEngine.HostRepairEventType)
            .OrderBy(item => item.CreatedAt).ToArray();
        Assert.Single(directives);
        using var first = JsonDocument.Parse(directives[0].DataJson!);
        Assert.True(first.RootElement.GetProperty("ValidatedReport").GetBoolean());
        Assert.Single(flow.Events, item => item.Type == "agent.contract-correction-scheduled");
        Assert.DoesNotContain(flow.Events, item => item.Type == WorkflowEngine.AutoRefinementScheduledEventType);
    }

    [Fact]
    public async Task FocusedQaRepair_InvestigationMustCorrectItsDispositionBeforeQa()
    {
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery, maxHandoffRetries: 2);
        harness.Runner.QaBlockOverride = context =>
            DeliveryReadinessFixtures.QaBlockFromPrompt(context.OutcomeContext,
                outcome: DeliveryCriterionOutcome.Failed);
        var ownerCalls = 0;
        harness.Runner.WorkerOutputOverride = context =>
        {
            if (!WorkflowEngine.RequiresOwnerRepairStatus(context))
            {
                return null;
            }
            return ++ownerCalls == 1
                ? "HANDOFF_STATUS: COMPLETE\nInvestigation complete, but repair cannot proceed."
                : "HANDOFF_STATUS: COMPLETE\nREPAIR_STATUS: BLOCKED\nRepair remains blocked.";
        };

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Blocked, flow.Status);
        Assert.Equal(2, ownerCalls);
        Assert.Single(harness.Runner.Contexts, item => item.RequiresDeliveryReadinessQa);
        Assert.Single(flow.Events, item => item.Type == "agent.contract-correction-scheduled");
        Assert.Single(flow.Events, item => item.Type == WorkflowEngine.OwnerRepairBlockedEventType);
        Assert.DoesNotContain(flow.GateRecords, item => item.ActionType == HandoffActionType.CustomerReview);
    }

    [Theory]
    [InlineData("REPAIR_STATUS: REPAIRED", "REPAIRED")]
    [InlineData("REPAIR_STATUS: NO_CHANGE_NEEDED", "NO_CHANGE_NEEDED")]
    [InlineData("REPAIR_STATUS: BLOCKED", "BLOCKED")]
    [InlineData("Investigation complete; repair blocked.", null)]
    [InlineData("REPAIR_STATUS: COMPLETE", null)]
    [InlineData(" REPAIR_STATUS: REPAIRED", null)]
    [InlineData("REPAIR_STATUS: REPAIRED\nREPAIR_STATUS: BLOCKED", null)]
    [InlineData("REPAIR_STATUS: REPAIRED\n REPAIR_STATUS: BLOCKED", null)]
    public void FocusedQaRepair_StatusIsExplicitAndUnambiguous(string output, string? expected)
    {
        Assert.Equal(expected, WorkflowEngine.ReadOwnerRepairStatus(output));
    }
}

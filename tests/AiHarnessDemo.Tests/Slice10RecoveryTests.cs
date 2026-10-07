using System.Collections.Immutable;
using System.Text.Json;
using AiHarnessDemo.Api;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Security;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Data;
using AiHarnessDemo.Infrastructure;
using AiHarnessDemo.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiHarnessDemo.Tests;

public sealed class FlowLifecycleCoordinatorTests
{
    [Fact]
    public void MixedBlockedAssessment_CanRepairOnlyWithFailedCriteriaAndExactBinding()
    {
        var coordinator = new FlowLifecycleCoordinator();
        var flow = Flow(FlowStatus.Running);
        flow.Kind = FlowKind.Delivery;
        var fingerprint = "sha256:" + new string('a', 64);

        Assert.Throws<FlowLifecycleException>(() =>
            coordinator.ScheduleAutoRefinement(flow, DeliveryReadinessState.Blocked,
                fingerprint, fingerprint));
        Assert.Throws<FlowLifecycleException>(() =>
            coordinator.ScheduleAutoRefinement(flow, DeliveryReadinessState.Blocked,
                fingerprint, "sha256:" + new string('b', 64), hasFailedCriteria: true));
        Assert.Equal(FlowStatus.Running, flow.Status);

        Assert.True(coordinator.ScheduleAutoRefinement(flow, DeliveryReadinessState.Blocked,
            fingerprint, fingerprint, hasFailedCriteria: true));
        Assert.Equal(FlowStatus.Reworking, flow.Status);
    }

    [Theory]
    [InlineData(DeliveryReadinessState.ReadyToApprove)]
    [InlineData(DeliveryReadinessState.NeedsCustomerWaiver)]
    public void FailedCriteriaFlag_CannotBypassOtherReadinessStates(DeliveryReadinessState state)
    {
        var flow = Flow(FlowStatus.Running);
        flow.Kind = FlowKind.Delivery;
        var fingerprint = "sha256:" + new string('a', 64);

        Assert.Throws<FlowLifecycleException>(() =>
            new FlowLifecycleCoordinator().ScheduleAutoRefinement(
                flow, state, fingerprint, fingerprint, hasFailedCriteria: true));
        Assert.Equal(FlowStatus.Running, flow.Status);
    }

    public static TheoryData<FlowStatus, FlowStatus> AllowedTransitions => new()
    {
        { FlowStatus.Intake, FlowStatus.Queued },
        { FlowStatus.Queued, FlowStatus.Running },
        { FlowStatus.Reworking, FlowStatus.Running },
        { FlowStatus.Running, FlowStatus.WaitingForFeedback },
        { FlowStatus.Running, FlowStatus.Blocked },
        { FlowStatus.Running, FlowStatus.Failed },
        { FlowStatus.WaitingForFeedback, FlowStatus.Queued },
        { FlowStatus.WaitingForFeedback, FlowStatus.Reworking },
        { FlowStatus.Failed, FlowStatus.Queued },
        { FlowStatus.Blocked, FlowStatus.Abandoning },
        { FlowStatus.Abandoning, FlowStatus.Abandoned }
    };

    [Theory]
    [MemberData(nameof(AllowedTransitions))]
    public void Transition_AllowsTheGuardedLifecycle(
        FlowStatus source,
        FlowStatus target)
    {
        var flow = Flow(source);
        var coordinator = new FlowLifecycleCoordinator();

        Assert.True(coordinator.Transition(flow, target));
        Assert.Equal(target, flow.Status);
    }

    [Fact]
    public async Task InvalidTransition_ThrowsTypedConflictMappedToHttp409()
    {
        var flow = Flow(FlowStatus.Blocked);
        var coordinator = new FlowLifecycleCoordinator();
        var exception = Assert.Throws<FlowLifecycleException>(() =>
            coordinator.Transition(flow, FlowStatus.Queued));
        Assert.Equal(flow.Id, exception.FlowId);
        Assert.Equal(FlowStatus.Blocked, exception.SourceStatus);
        Assert.Equal(FlowStatus.Queued, exception.TargetStatus);

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var handled = await new ApiExceptionHandler(
                NullLogger<ApiExceptionHandler>.Instance)
            .TryHandleAsync(
                context,
                exception,
                CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
    }

    [Fact]
    public async Task EnterAsync_SerializesMutationsForOneFlow()
    {
        var coordinator = new FlowLifecycleCoordinator();
        var flowId = Guid.NewGuid();
        var first = await coordinator.EnterAsync(flowId);
        var secondEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var second = Task.Run(async () =>
        {
            await using var lease = await coordinator.EnterAsync(flowId);
            secondEntered.SetResult();
        });

        await Task.Delay(30);
        Assert.False(secondEntered.Task.IsCompleted);
        await first.DisposeAsync();
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(secondEntered.Task.IsCompletedSuccessfully);
    }

    private static FlowRun Flow(FlowStatus status) => new()
    {
        Title = "Lifecycle",
        OriginalRequest = "Exercise lifecycle.",
        Status = status
    };
}

public sealed class Slice10RecoveryTests
{
    [Fact]
    public async Task Recovery_ReconcilesEveryDurableStudioStateIdempotently()
    {
        await using var fixture = await Slice10RecoveryFixture.CreateAsync();
        var acceptedDelivery = fixture.AddReviewFlow(
            FlowKind.Delivery,
            FlowStatus.WaitingForFeedback,
            accepted: true);
        var waitingReview = fixture.AddReviewFlow(
            FlowKind.Advisory,
            FlowStatus.WaitingForFeedback,
            accepted: false);
        var acceptedAdvisory = fixture.AddReviewFlow(
            FlowKind.Advisory,
            FlowStatus.WaitingForFeedback,
            accepted: true);
        var failedPublication = fixture.AddReviewFlow(
            FlowKind.Delivery,
            FlowStatus.Failed,
            accepted: true,
            publicationStatus: StepStatus.Failed);
        var failedBeforePublication = fixture.AddReviewFlow(
            FlowKind.Delivery,
            FlowStatus.Failed,
            accepted: true);
        var blocked = fixture.AddBareFlow(
            FlowStatus.Blocked);
        blocked.CurrentBlockerCode =
            MissingQualificationCoordinator.BlockerCode;
        blocked.CurrentBlockerSummary = "A specialist is unavailable.";
        blocked.CurrentBlockerDataJson =
            """{"Code":"MissingQualification","Summary":"A specialist is unavailable.","Missing":["specialist"],"WhyRequired":"The requested work requires the missing specialty.","SuggestedAgent":null}""";
        var failed = fixture.AddBareFlow(
            FlowStatus.Failed);
        failed.Steps.Add(Step(
            failed,
            "worker",
            "work",
            StepStatus.Failed));
        await fixture.SaveAsync();

        var first = await fixture.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);
        var second = await fixture.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);

        Assert.Contains(acceptedDelivery.Id, first);
        Assert.DoesNotContain(waitingReview.Id, first);
        Assert.DoesNotContain(acceptedAdvisory.Id, first);
        Assert.DoesNotContain(failedPublication.Id, first);
        Assert.Contains(failedBeforePublication.Id, first);
        Assert.DoesNotContain(blocked.Id, first);
        Assert.DoesNotContain(failed.Id, first);
        Assert.Contains(acceptedDelivery.Id, second);

        await using var database = await fixture.Factory.CreateDbContextAsync();
        var delivery = await fixture.LoadAsync(database, acceptedDelivery.Id);
        Assert.Equal(FlowStatus.Queued, delivery.Status);
        Assert.Single(delivery.PlanDocuments);
        Assert.Single(delivery.AgentSnapshots);
        Assert.Single(
            delivery.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);
        Assert.Single(
            delivery.Steps,
            step => ReviewCoordinator.IsPublicationStep(delivery, step));
        Assert.Single(
            delivery.Events,
            item => item.Type == "flow.recovery-publication-materialized");
        Assert.Single(
            delivery.Events,
            item => item.Type == "flow.recovery-publication-queued");

        var open = await fixture.LoadAsync(database, waitingReview.Id);
        Assert.Equal(FlowStatus.WaitingForFeedback, open.Status);
        Assert.Single(
            open.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);
        Assert.Single(
            open.Events,
            item => item.Type == "flow.recovery-review-retained");

        var advisory = await fixture.LoadAsync(database, acceptedAdvisory.Id);
        Assert.Equal(FlowStatus.Approved, advisory.Status);
        Assert.DoesNotContain(
            advisory.Steps,
            step => step.PlanStage == PlanStage.AfterApproval);
        Assert.Single(
            advisory.Events,
            item => item.Type == "flow.recovery-advisory-accepted");

        var publicationFailure =
            await fixture.LoadAsync(database, failedPublication.Id);
        Assert.Equal(FlowStatus.Failed, publicationFailure.Status);
        Assert.True(ReviewCoordinator.HasAcceptedCustomerReview(
            publicationFailure));
        Assert.Single(
            publicationFailure.Steps,
            step => ReviewCoordinator.IsPublicationStep(
                publicationFailure,
                step));
        Assert.Single(
            publicationFailure.Events,
            item =>
                item.Type ==
                "flow.recovery-publication-restart-required");

        var repairedPublication =
            await fixture.LoadAsync(database, failedBeforePublication.Id);
        Assert.Equal(FlowStatus.Queued, repairedPublication.Status);
        Assert.Empty(repairedPublication.FailureReason);
        Assert.Single(
            repairedPublication.Steps,
            step => ReviewCoordinator.IsPublicationStep(
                repairedPublication,
                step));
        Assert.Single(
            repairedPublication.Events,
            item =>
                item.Type ==
                "flow.recovery-publication-materialized");

        var retainedBlocker = await fixture.LoadAsync(database, blocked.Id);
        Assert.Equal(FlowStatus.Blocked, retainedBlocker.Status);
        Assert.Empty(retainedBlocker.Steps);
        Assert.Single(
            retainedBlocker.Events,
            item => item.Type == "flow.recovery-blocker-retained");

        var retainedFailure = await fixture.LoadAsync(database, failed.Id);
        Assert.Equal(FlowStatus.Failed, retainedFailure.Status);
        Assert.Single(retainedFailure.Steps);
        Assert.Single(
            retainedFailure.Events,
            item =>
                item.Type == "flow.recovery-manual-restart-required");

    }

    [Fact]
    public async Task Recovery_FailsClosedForAcceptedDeliveryWithPersistedNone()
    {
        await using var fixture = await Slice10RecoveryFixture.CreateAsync();
        var flow = fixture.AddReviewFlow(
            FlowKind.Delivery,
            FlowStatus.WaitingForFeedback,
            accepted: true);
        flow.Outcome = OutcomeType.None;
        await fixture.SaveAsync();

        var recovered = await fixture.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);

        Assert.DoesNotContain(flow.Id, recovered);
        await using var database = await fixture.Factory.CreateDbContextAsync();
        var failed = await fixture.LoadAsync(database, flow.Id);
        Assert.Equal(FlowStatus.Failed, failed.Status);
        Assert.DoesNotContain(
            failed.Steps,
            step => step.PlanStage == PlanStage.AfterApproval);
        Assert.Contains(
            failed.Events,
            item => item.Type == "flow.recovery-failed");
    }

    [Fact]
    public async Task ManualRetry_RetainsOriginalPolicyAndOnlyAppliesTightening()
    {
        await using var fixture = await Slice10RecoveryFixture.CreateAsync();
        var flow = fixture.AddBareFlow(
            FlowStatus.Failed);
        flow.Kind = FlowKind.Delivery;
        var originalWorkflow = fixture.WorkflowProvider.GetEffective();
        var resolver = new PermissionProfileResolver();
        var originalPermission = resolver.Resolve(
            new PermissionResolutionRequest(
                FlowKind.Delivery,
                ExecutionInvocationKind.Worker,
                PlanStage.BeforeReview,
                ImmutableArray.Create(PlanDuty.Implement),
                DurableReviewDecision: null,
                DurableApproval: false,
                IsOnlyPlannedPublishStep: false),
            PermissionProfileResolver.FromWorkflow(originalWorkflow));
        var failed = Step(
            flow,
            "worker",
            "implement",
            StepStatus.Failed);
        failed.PlanDutiesJson = """["Implement"]""";
        failed.PermissionProfile = originalPermission.Profile;
        failed.EffectivePermissionJson =
            JsonSerializer.Serialize(originalPermission);
        failed.WorkflowRevision = originalWorkflow.Revision;
        failed.Phase = AgentRunPhase.Failed;
        failed.CompletedAt = DateTimeOffset.UtcNow;
        flow.FailureReason = "The attempt failed.";
        flow.Steps.Add(failed);
        flow.TaskProfiles.Add(Profile(flow, failed, "implement", "worker"));
        await fixture.SaveAsync();

        await fixture.WriteWorkflowAsync(preReviewMaximum: "ReadOnlySource");
        var tightenedWorkflow = fixture.WorkflowProvider.GetEffective();
        Assert.NotEqual(originalWorkflow.Revision, tightenedWorkflow.Revision);

        var restarted = await fixture.Engine.RestartFailedFlowAsync(
            flow.Id,
            CancellationToken.None);

        var retry = restarted.Steps.Single(step =>
            step.RetryOfStepId == failed.Id);
        var retryPermission =
            JsonSerializer.Deserialize<EffectiveExecutionPermission>(
                retry.EffectivePermissionJson)!;
        Assert.Equal(
            ExecutionPermissionProfile.WorkspaceWrite,
            failed.PermissionProfile);
        Assert.Equal(
            originalWorkflow.Revision,
            failed.WorkflowRevision);
        Assert.Equal(
            ExecutionPermissionProfile.ReadOnlySource,
            retry.PermissionProfile);
        Assert.Equal(
            ExecutionPermissionProfile.ReadOnlySource,
            retryPermission.Profile);
        Assert.Equal(tightenedWorkflow.Revision, retry.WorkflowRevision);
        Assert.Contains(
            restarted.Events,
            item => item.Type == "step.permission-policy-tightened");

        var persistedAttempt = restarted.Steps.Single(step =>
            step.Id == failed.Id);
        Assert.Equal(originalWorkflow.Revision, persistedAttempt.WorkflowRevision);
        Assert.Equal(
            JsonSerializer.Serialize(originalPermission),
            persistedAttempt.EffectivePermissionJson);
    }

    [Fact]
    public async Task PersistedAttemptPermissionAndRevision_AreNotRelaxedOnResume()
    {
        await using var fixture = await Slice10RecoveryFixture.CreateAsync();
        var flow = fixture.AddBareFlow(
            FlowStatus.Queued);
        var step = Step(
            flow,
            "worker",
            "analysis",
            StepStatus.Pending);
        step.PlanDutiesJson = """["Analyze"]""";
        var persisted = new EffectiveExecutionPermission(
            ExecutionPermissionProfile.ReadOnlySource,
            ["view"],
            ["write", "shell", "shell(git push)"],
            ["github.com"],
            DisableBuiltinMcps: true,
            DisableCustomInstructions: true,
            DisallowTemporaryDirectory: true,
            GuardPublicationCredentials: true,
            AllowRemotePublication: false,
            GovernedGitMetadataIsolation: false);
        step.PermissionProfile = persisted.Profile;
        step.EffectivePermissionJson = JsonSerializer.Serialize(persisted);
        step.WorkflowRevision = "persisted-revision";
        flow.Steps.Add(step);
        await fixture.SaveAsync();

        var current = fixture.WorkflowProvider.GetEffective();
        await fixture.WriteInvalidWorkflowAsync();
        Assert.False(fixture.WorkflowProvider.Status().CurrentFileValid);
        Assert.Equal(
            current.Revision,
            fixture.WorkflowProvider.GetEffective().Revision);
        var context = new AgentExecutionContext(
            flow.Id,
            flow.Iteration,
            step.AgentId,
            step.AgentName,
            step.AgentRole,
            "model",
            "default",
            1,
            "Inspect.",
            "Knowledge.",
            fixture.Root,
            fixture.Root,
            Guid.NewGuid(),
            OutcomeType.PullRequest,
            "plan",
            [],
            [],
            FlowStepId: step.Id);

        var resolved =
            await CopilotReasoningHost.ResolveAndPersistPermissionAsync(
                context,
                current,
                new PermissionProfileResolver(),
                fixture.Factory,
                CancellationToken.None);

        Assert.True(PermissionProfileResolver.Equivalent(persisted, resolved));
        await using var database = await fixture.Factory.CreateDbContextAsync();
        var stored = await database.FlowSteps.SingleAsync(item => item.Id == step.Id);
        Assert.Equal("persisted-revision", stored.WorkflowRevision);
        Assert.Equal(
            JsonSerializer.Serialize(persisted),
            stored.EffectivePermissionJson);
    }

    [Fact]
    public async Task ManualRetry_DoesNotRelaxAnOriginalAdditionalDenial()
    {
        await using var fixture = await Slice10RecoveryFixture.CreateAsync();
        var flow = fixture.AddBareFlow(
            FlowStatus.Failed);
        var resolver = new PermissionProfileResolver();
        var currentWorkflow = fixture.WorkflowProvider.GetEffective();
        var currentPermission = resolver.Resolve(
            new PermissionResolutionRequest(
                FlowKind.Delivery,
                ExecutionInvocationKind.Worker,
                PlanStage.BeforeReview,
                ImmutableArray.Create(PlanDuty.Implement),
                DurableReviewDecision: null,
                DurableApproval: false,
                IsOnlyPlannedPublishStep: false),
            PermissionProfileResolver.FromWorkflow(currentWorkflow));
        var originalPermission = currentPermission with
        {
            DeniedTools = currentPermission.DeniedTools
                .Add("shell(custom-sensitive-command)")
        };
        var failed = Step(
            flow,
            "worker",
            "implement",
            StepStatus.Failed);
        failed.PlanDutiesJson = """["Implement"]""";
        failed.PermissionProfile = originalPermission.Profile;
        failed.EffectivePermissionJson =
            JsonSerializer.Serialize(originalPermission);
        failed.WorkflowRevision = "older-more-restrictive-revision";
        flow.FailureReason = "The attempt failed.";
        flow.Steps.Add(failed);
        flow.TaskProfiles.Add(Profile(flow, failed, "implement", "worker"));
        await fixture.SaveAsync();

        var restarted = await fixture.Engine.RestartFailedFlowAsync(
            flow.Id,
            CancellationToken.None);

        var retry = restarted.Steps.Single(step =>
            step.RetryOfStepId == failed.Id);
        var retained =
            JsonSerializer.Deserialize<EffectiveExecutionPermission>(
                retry.EffectivePermissionJson)!;
        Assert.Equal(
            "older-more-restrictive-revision",
            retry.WorkflowRevision);
        Assert.Contains(
            "shell(custom-sensitive-command)",
            retained.DeniedTools);
        Assert.DoesNotContain(
            restarted.Events,
            item =>
                item.FlowStepId == retry.Id &&
                item.Type == "step.permission-policy-tightened");
    }

    [Fact]
    public async Task ReusedSkippedRetry_RetainsItsOwnPersistedPolicyAndRevision()
    {
        await using var fixture = await Slice10RecoveryFixture.CreateAsync();
        var flow = fixture.AddBareFlow(
            FlowStatus.Failed);
        var resolver = new PermissionProfileResolver();
        var workflow = fixture.WorkflowProvider.GetEffective();
        var failedPermission = resolver.Resolve(
            new PermissionResolutionRequest(
                FlowKind.Delivery,
                ExecutionInvocationKind.Worker,
                PlanStage.BeforeReview,
                ImmutableArray.Create(PlanDuty.Implement),
                null,
                false,
                false),
            PermissionProfileResolver.FromWorkflow(workflow));
        var retainedPermission = failedPermission with
        {
            Profile = ExecutionPermissionProfile.ReadOnlySource,
            AllowedTools = ["view"],
            DeniedTools = failedPermission.DeniedTools
                .Add("write")
                .Add("shell"),
            GuardPublicationCredentials = true,
            AllowRemotePublication = false
        };
        var failed = Step(
            flow,
            "worker",
            "implement",
            StepStatus.Failed);
        failed.PlanDutiesJson = """["Implement"]""";
        failed.StableSemanticRootId = failed.Id;
        failed.PermissionProfile = failedPermission.Profile;
        failed.EffectivePermissionJson =
            JsonSerializer.Serialize(failedPermission);
        failed.WorkflowRevision = "failed-attempt-revision";
        var retry = Step(
            flow,
            "worker",
            "implement",
            StepStatus.Skipped);
        retry.Sequence = 20;
        retry.Phase = AgentRunPhase.Failed;
        retry.RetryOfStepId = failed.Id;
        retry.StableSemanticRootId = failed.Id;
        retry.PermissionProfile = retainedPermission.Profile;
        retry.EffectivePermissionJson =
            JsonSerializer.Serialize(retainedPermission);
        retry.WorkflowRevision = "retry-persisted-revision";
        flow.FailureReason = "Setup failed before the retry ran.";
        flow.Steps.AddRange([failed, retry]);
        flow.TaskProfiles.Add(Profile(flow, failed, "implement", "worker"));
        flow.TaskProfiles.Add(Profile(flow, retry, "implement", "worker"));
        await fixture.SaveAsync();

        var restarted = await fixture.Engine.RestartFailedFlowAsync(
            flow.Id,
            CancellationToken.None);

        var reused = restarted.Steps.Single(step => step.Id == retry.Id);
        Assert.Equal(StepStatus.Pending, reused.Status);
        Assert.Equal(
            ExecutionPermissionProfile.ReadOnlySource,
            reused.PermissionProfile);
        Assert.Equal(
            JsonSerializer.Serialize(retainedPermission),
            reused.EffectivePermissionJson);
        Assert.Equal("retry-persisted-revision", reused.WorkflowRevision);
    }

    [Fact]
    public async Task RecoveredPublication_LoweredCurrentPolicyFailsClosedWithoutMaterialization()
    {
        await using var fixture = await Slice10RecoveryFixture.CreateAsync();
        var flow = fixture.AddReviewFlow(
            FlowKind.Delivery,
            FlowStatus.WaitingForFeedback,
            accepted: true);
        var plannedRevision = fixture.WorkflowProvider.GetEffective().Revision;
        await fixture.SaveAsync();

        await fixture.WriteWorkflowAsync(
            preReviewMaximum: "WorkspaceWrite",
            postApprovalMaximum: "ReadOnlySource");
        var tightenedRevision = fixture.WorkflowProvider.GetEffective().Revision;
        Assert.NotEqual(plannedRevision, tightenedRevision);

        await fixture.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);

        await using var database = await fixture.Factory.CreateDbContextAsync();
        var recovered = await fixture.LoadAsync(database, flow.Id);
        Assert.Equal(FlowStatus.Failed, recovered.Status);
        Assert.DoesNotContain(
            recovered.Steps,
            step => ReviewCoordinator.IsPublicationStep(
                recovered,
                step));
        Assert.Equal(
            ReviewDecision.Accepted,
            Assert.Single(
                recovered.GateRecords,
                gate => gate.ActionType ==
                        HandoffActionType.CustomerReview)
                .ReviewDecision);
        var failure = Assert.Single(
            recovered.Events,
            item => item.Type == "flow.recovery-failed");
        Assert.Contains(
            "disabled by the current workflow post-approval permission ceiling",
            failure.DataJson,
            StringComparison.Ordinal);
        Assert.NotEqual(plannedRevision, tightenedRevision);
    }

    [Fact]
    public async Task AcceptedManualRetry_UsesRootPublicationSnapshotAcrossEquivalentRevision()
    {
        await using var fixture = await Slice10RecoveryFixture.CreateAsync();
        var flow = fixture.AddReviewFlow(
            FlowKind.Delivery,
            FlowStatus.WaitingForFeedback,
            accepted: true);
        var root = flow.Steps.Single(step => step.IsOutcomeOwner);
        var retry = ReplaceReviewedOutcomeOwner(
            flow,
            root,
            manualRetry: true);
        var plannedRevision = fixture.WorkflowProvider.GetEffective().Revision;
        await fixture.SaveAsync();

        await fixture.TouchEquivalentWorkflowAsync();
        Assert.NotEqual(
            plannedRevision,
            fixture.WorkflowProvider.GetEffective().Revision);
        await fixture.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);

        await using var database = await fixture.Factory.CreateDbContextAsync();
        var recovered = await fixture.LoadAsync(database, flow.Id);
        var publication = Assert.Single(
            recovered.Steps,
            step => ReviewCoordinator.IsPublicationStep(
                recovered,
                step));
        Assert.Equal(retry.Id, publication.DependsOnStepId);
        Assert.Equal(plannedRevision, publication.WorkflowRevision);
        Assert.Single(
            recovered.Events,
            item =>
                item.Type ==
                "plan.publication-permission-snapshotted" &&
                item.FlowStepId == root.Id);
    }

    [Fact]
    public async Task AcceptedPreMortemRevision_LoweredPolicyFailsClosedFromRootSnapshot()
    {
        await using var fixture = await Slice10RecoveryFixture.CreateAsync();
        var flow = fixture.AddReviewFlow(
            FlowKind.Delivery,
            FlowStatus.WaitingForFeedback,
            accepted: true);
        var root = flow.Steps.Single(step => step.IsOutcomeOwner);
        var revision = ReplaceReviewedOutcomeOwner(
            flow,
            root,
            manualRetry: false);
        await fixture.SaveAsync();

        await fixture.WriteWorkflowAsync(
            preReviewMaximum: "WorkspaceWrite",
            postApprovalMaximum: "ReadOnlySource");
        var tightenedRevision = fixture.WorkflowProvider.GetEffective().Revision;
        await fixture.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);

        await using var database = await fixture.Factory.CreateDbContextAsync();
        var recovered = await fixture.LoadAsync(database, flow.Id);
        Assert.Equal(FlowStatus.Failed, recovered.Status);
        Assert.DoesNotContain(
            recovered.Steps,
            step => ReviewCoordinator.IsPublicationStep(
                recovered,
                step));
        Assert.Equal(
            ReviewDecision.Accepted,
            Assert.Single(
                recovered.GateRecords,
                gate => gate.ActionType ==
                        HandoffActionType.CustomerReview)
                .ReviewDecision);
        Assert.Contains(
            recovered.Events,
            item =>
                item.Type == "flow.recovery-failed" &&
                item.DataJson != null &&
                item.DataJson.Contains(
                    "disabled by the current workflow post-approval permission ceiling",
                    StringComparison.Ordinal));
        Assert.Single(
            recovered.Events,
            item =>
                item.Type ==
                "plan.publication-permission-snapshotted" &&
                item.FlowStepId == root.Id);
        Assert.NotEqual(
            revision.WorkflowRevision,
            tightenedRevision);
    }

    [Fact]
    public async Task StudioFlowWithoutSnapshot_DoesNotFallBackToCurrentCatalog()
    {
        await using var fixture = await Slice10RecoveryFixture.CreateAsync();
        var flow = fixture.AddBareFlow(
            FlowStatus.Queued);
        await fixture.SaveAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Snapshots.GetAgentsAsync(flow.Id));

        Assert.Contains("will not be substituted", exception.Message);
        await using var database = await fixture.Factory.CreateDbContextAsync();
        Assert.Empty(await database.FlowAgentSnapshots.ToListAsync());
    }

    [Fact]
    public async Task DuplicateRunClaim_DoesNotStartTwoAttemptsForOneFlow()
    {
        var workspace = new BlockingWorkspaceManager();
        await using var fixture =
            await Slice10RecoveryFixture.CreateAsync(workspace);
        var flow = fixture.AddBareFlow(
            FlowStatus.Queued);
        flow.AgentSnapshots.Add(new FlowAgentSnapshot
        {
            FlowRunId = flow.Id,
            AgentId = "team-lead",
            Name = "Team Lead",
            Description = "Plans the work.",
            Role = "team-lead",
            Instructions = "Plan.",
            DefinitionHash = "sha256:team-lead",
            EnabledAtSnapshot = true,
            Required = true,
            Switchable = false,
            SourceFileName = "team-lead.agent.md"
        });
        await fixture.SaveAsync();
        using var cancellation = new CancellationTokenSource();

        var first = fixture.Engine.RunAsync(flow.Id, cancellation.Token);
        await workspace.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Engine.RunAsync(flow.Id, CancellationToken.None);

        Assert.Equal(1, workspace.Calls);
        Assert.Equal(1, fixture.Engine.ActiveFlowCount);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(0, fixture.Engine.ActiveFlowCount);
    }

    private static FlowStep ReplaceReviewedOutcomeOwner(
        FlowRun flow,
        FlowStep root,
        bool manualRetry)
    {
        root.Status = StepStatus.Failed;
        root.Phase = AgentRunPhase.Failed;
        var retry = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Sequence = root.Sequence + 5,
            AgentId = root.AgentId,
            AgentName = root.AgentName,
            AgentRole = root.AgentRole,
            Label = manualRetry
                ? "Manual restart of outcome owner"
                : "Revision after pre-mortem findings",
            PlanStepKey = root.PlanStepKey,
            PlanDutiesJson = root.PlanDutiesJson,
            PlanStage = root.PlanStage,
            InvocationKind = root.InvocationKind,
            IsOutcomeOwner = true,
            PermissionProfile = root.PermissionProfile,
            WorkflowRevision = root.WorkflowRevision,
            Status = StepStatus.Completed,
            Phase = AgentRunPhase.Succeeded,
            RetryOfStepId = manualRetry ? root.Id : null,
            StableSemanticRootId = root.Id,
            PreMortemReviewStepId = manualRetry
                ? null
                : Guid.NewGuid(),
            CompletedAt = DateTimeOffset.UtcNow
        };
        flow.Steps.Add(retry);
        var gate = flow.GateRecords.Single(item =>
            item.ActionType == HandoffActionType.CustomerReview);
        gate.FlowStepId = retry.Id;
        flow.Events.RemoveAll(item =>
            item.Type == ReviewedCandidateLedger.EventType);
        flow.Events.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = retry.Id,
            Type = ReviewedCandidateLedger.EventType,
            Message =
                "Persisted the reviewed retry candidate identity.",
            DataJson = ReviewedCandidateLedger.Serialize(
                new ReviewedCandidateIdentity(
                    flow.Id,
                    flow.Iteration,
                    retry.Id,
                    retry.PlanStepKey,
                    OutcomeVerificationRules.ComputeSha256(
                        flow.OutcomeContractJson),
                    OutcomeVerificationRules.ComputeSha256(
                        $"retry-plan:{flow.Id:D}"),
                    OutcomeVerificationRules.ComputeSha256(
                        $"retry-candidate:{flow.Id:D}"),
                    0,
                    0,
                    0,
                    0,
                    [
                        new ReviewedCandidateRepositoryIdentity(
                            ".",
                            new string('1', 40),
                            new string('2', 40),
                            "example/repository")
                    ],
                    DateTimeOffset.UtcNow))
        });
        return retry;
    }

    private static FlowStep Step(
        FlowRun flow,
        string agentId,
        string planStepKey,
        StepStatus status) =>
        new()
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Sequence = 10,
            AgentId = agentId,
            AgentName = agentId,
            AgentRole = "worker",
            PlanStepKey = planStepKey,
            PlanDutiesJson = """["PrepareOutcome"]""",
            PlanStage = PlanStage.BeforeReview,
            IsOutcomeOwner = true,
            PermissionProfile = ExecutionPermissionProfile.ReadOnlySource,
            WorkflowRevision = "workflow-revision",
            Status = status,
            Phase = status == StepStatus.Failed
                ? AgentRunPhase.Failed
                : AgentRunPhase.Succeeded,
            CompletedAt = status is StepStatus.Completed or StepStatus.Failed
                ? DateTimeOffset.UtcNow
                : null
        };

    private static TaskProfile Profile(
        FlowRun flow,
        FlowStep? step,
        string planStepKey,
        string agentId) =>
        new()
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            FlowStepId = step?.Id,
            PlanStepKey = planStepKey,
            AgentId = agentId,
            Role = "worker",
            Complexity = 3,
            ReasoningDepth = 3,
            ContextDemand = 3,
            ToolIntensity = 3,
            TaskTypeTagsJson = """["Implementation"]""",
            Risk = TaskRisk.Medium,
            RiskReason = "Recovery must preserve the accepted plan.",
            Confidence = 0.9,
            RationalesJson = """["Durable recovery fixture."]"""
        };

    private sealed class Slice10RecoveryFixture : IAsyncDisposable
    {
        private readonly HandoffGateEngine _gate;
        private readonly List<FlowRun> _pending = [];

        private Slice10RecoveryFixture(
            string root,
            RecoveryDbContextFactory factory,
            WorkflowDefinitionProvider workflowProvider,
            FlowAgentSnapshotService snapshots,
            HandoffGateEngine gate,
            WorkflowEngine engine)
        {
            Root = root;
            Factory = factory;
            WorkflowProvider = workflowProvider;
            Snapshots = snapshots;
            _gate = gate;
            Engine = engine;
        }

        public string Root { get; }

        public RecoveryDbContextFactory Factory { get; }

        public WorkflowDefinitionProvider WorkflowProvider { get; }

        public FlowAgentSnapshotService Snapshots { get; }

        public WorkflowEngine Engine { get; }

        public static async Task<Slice10RecoveryFixture> CreateAsync(
            IWorkspaceManager? workspaceManager = null)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"ai-harness-slice10-{Guid.NewGuid():N}");
            var agents = Path.Combine(root, ".github", "agents");
            Directory.CreateDirectory(agents);
            await WriteAgentAsync(
                agents,
                "account-manager",
                "Account Manager",
                "Confirms customer intent.");
            await WriteAgentAsync(
                agents,
                "team-lead",
                "Team Lead",
                "Plans the work.");
            await WriteAgentAsync(
                agents,
                "pre-mortem-sceptic",
                "Pre-mortem Sceptic",
                "Challenges the plan.");
            await WriteAgentAsync(
                agents,
                "worker",
                "Worker",
                "Completes current work.");
            await File.WriteAllTextAsync(
                Path.Combine(root, "WORKFLOW.md"),
                WorkflowText("WorkspaceWrite"));
            var options = new DbContextOptionsBuilder<HarnessDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "harness.db")};Pooling=False")
                .Options;
            var factory = new RecoveryDbContextFactory(options);
            await using (var database = await factory.CreateDbContextAsync())
            {
                await database.Database.EnsureCreatedAsync();
            }
            var paths = new HarnessPaths(
                root,
                agents,
                Path.Combine(root, "harness.db"));
            var catalog = new AgentCatalog(paths, factory);
            await catalog.LoadAsync();
            var snapshots = new FlowAgentSnapshotService(factory, catalog);
            var workflow = new WorkflowDefinitionProvider(
                paths,
                new WorkflowLoader(),
                NullLogger<WorkflowDefinitionProvider>.Instance);
            var gate = new HandoffGateEngine();
            gate.SetTrustLevel(
                HandoffActionType.Advance,
                HandoffTrustLevel.Auto);
            var lifecycle = new FlowLifecycleCoordinator();
            var engine = new WorkflowEngine(
                factory,
                catalog,
                new FixedModelRouter(),
                new BootstrapTaskProfileFactory(),
                TestRoutingSupport.Recorder(factory),
                workspaceManager ?? new NeverWorkspaceManager(),
                new NeverAgentRunner(),
                gate,
                new CopilotSessionJournal(),
                workflow,
                NullLogger<WorkflowEngine>.Instance,
                flowAgentSnapshotService: snapshots,
                lifecycleCoordinator: lifecycle,
                permissionProfileResolver: new PermissionProfileResolver());
            return new Slice10RecoveryFixture(
                root,
                factory,
                workflow,
                snapshots,
                gate,
                engine);
        }

        public FlowRun AddBareFlow(FlowStatus status)
        {
            var flow = new FlowRun
            {
                Title = $"Current flow {status}",
                OriginalRequest = "Recover durable work.",
                ConsolidatedRequest = "Recover durable work.",
                Kind = FlowKind.Delivery,
                Status = status,
                RepositoryPath = Root,
                RepositoryKnowledge = "Recovery fixture.",
                WorkspacePath = Path.Combine(
                    Root,
                    "workspace",
                    Guid.NewGuid().ToString("N"))
            };
            _pending.Add(flow);
            return flow;
        }

        public FlowRun AddReviewFlow(
            FlowKind kind,
            FlowStatus status,
            bool accepted,
            StepStatus? publicationStatus = null)
        {
            var flow = AddBareFlow(status);
            flow.Kind = kind;
            flow.Outcome = kind == FlowKind.Advisory
                ? OutcomeType.None
                : OutcomeType.PullRequest;
            flow.OutcomeOwnerPlanStepKey = "outcome";
            flow.PublicationPlanStepKey = kind == FlowKind.Delivery
                ? "publish"
                : null;
            var owner = Step(
                flow,
                "worker",
                "outcome",
                StepStatus.Completed);
            owner.WorkflowRevision =
                WorkflowProvider.GetEffective().Revision;
            flow.Steps.Add(owner);
            flow.GateRecords.Add(new HandoffGateRecord
            {
                FlowRunId = flow.Id,
                FlowStepId = owner.Id,
                ActionType = HandoffActionType.CustomerReview,
                Decision = accepted
                    ? HandoffGateDecision.AutoApproved
                    : HandoffGateDecision.AwaitingHumanApproval,
                ReviewDecision = accepted
                    ? ReviewDecision.Accepted
                    : null,
                TrustLevelAtDecision = HandoffTrustLevel.Gated,
                Summary = "Review the durable result.",
                Resolved = accepted,
                Approved = accepted ? true : null,
                ResolvedBy = accepted ? "customer" : null,
                ResolvedAt = accepted ? DateTimeOffset.UtcNow : null
            });
            if (kind == FlowKind.Delivery)
            {
                var workflow = WorkflowProvider.GetEffective();
                flow.PlanDocuments.Add(new FlowPlanDocument
                {
                    FlowRunId = flow.Id,
                    Iteration = flow.Iteration,
                    Disposition = TeamPlanDisposition.Planned.ToString(),
                    RawJson = DeliveryPlanJson
                });
                flow.AgentSnapshots.Add(new FlowAgentSnapshot
                {
                    FlowRunId = flow.Id,
                    AgentId = "publisher",
                    Name = "Publisher",
                    Description = "Publishes an approved result.",
                    Role = "arbitrary-publisher",
                    Instructions = "Publish only after approval.",
                    DefinitionHash = "sha256:publisher",
                    EnabledAtSnapshot = true,
                    SourceFileName = "publisher.agent.md"
                });
                var plannedPermission = new PermissionProfileResolver().Resolve(
                    new PermissionResolutionRequest(
                        FlowKind.Delivery,
                        ExecutionInvocationKind.Publication,
                        PlanStage.AfterApproval,
                        ImmutableArray.Create(PlanDuty.Publish),
                        DurableReviewDecision: ReviewDecision.Accepted,
                        DurableApproval: true,
                        IsOnlyPlannedPublishStep: true),
                    PermissionProfileResolver.FromWorkflow(workflow));
                flow.Events.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = owner.Id,
                    Type = "plan.publication-permission-snapshotted",
                    Message =
                        "Persisted planned publication policy for the recovery fixture.",
                    DataJson = JsonSerializer.Serialize(
                        new DeferredPermissionSnapshot(
                            flow.Iteration,
                            "publish",
                            workflow.Revision,
                            plannedPermission))
                });
                flow.Events.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = owner.Id,
                    Type = ReviewedCandidateLedger.EventType,
                    Message =
                        "Persisted the reviewed candidate identity for recovery.",
                    DataJson = ReviewedCandidateLedger.Serialize(
                        new ReviewedCandidateIdentity(
                            flow.Id,
                            flow.Iteration,
                            owner.Id,
                            owner.PlanStepKey,
                            OutcomeVerificationRules.ComputeSha256(
                                flow.OutcomeContractJson),
                            OutcomeVerificationRules.ComputeSha256(
                                $"recovery-plan:{flow.Id:D}"),
                            OutcomeVerificationRules.ComputeSha256(
                                $"recovery-candidate:{flow.Id:D}"),
                            0,
                            0,
                            0,
                            0,
                            [
                                new ReviewedCandidateRepositoryIdentity(
                                    ".",
                                    new string('1', 40),
                                    new string('2', 40),
                                    "example/repository")
                            ],
                            DateTimeOffset.UtcNow))
                });
                var profile = Profile(
                    flow,
                    step: null,
                    planStepKey: "publish",
                    agentId: "publisher");
                flow.TaskProfiles.Add(profile);
                if (publicationStatus is { } attemptStatus)
                {
                    var publication = new FlowStep
                    {
                        FlowRunId = flow.Id,
                        Iteration = flow.Iteration,
                        Sequence = 20,
                        AgentId = "publisher",
                        AgentName = "Publisher",
                        AgentRole = "arbitrary-publisher",
                        Label = "Publish the accepted result.",
                        PlanStepKey = "publish",
                        PlanDutiesJson = """["Publish"]""",
                        PlanStage = PlanStage.AfterApproval,
                        PermissionProfile =
                            ExecutionPermissionProfile.Publish,
                        WorkflowRevision = "workflow-revision",
                        Status = attemptStatus,
                        Phase = attemptStatus == StepStatus.Failed
                            ? AgentRunPhase.Failed
                            : AgentRunPhase.Succeeded,
                        RemotePublicationAllowed = true,
                        DependsOnStepId = owner.Id,
                        CompletedAt =
                            attemptStatus is
                                StepStatus.Completed or StepStatus.Failed
                                ? DateTimeOffset.UtcNow
                                : null
                    };
                    publication.StableSemanticRootId = publication.Id;
                    flow.Steps.Add(publication);
                    profile.FlowStepId = publication.Id;
                }
            }
            return flow;
        }

        public async Task SaveAsync()
        {
            await using var database = await Factory.CreateDbContextAsync();
            database.Flows.AddRange(_pending);
            await database.SaveChangesAsync();
            _pending.Clear();
        }

        public Task WriteWorkflowAsync(
            string preReviewMaximum,
            string postApprovalMaximum = "Publish")
        {
            File.WriteAllText(
                Path.Combine(Root, "WORKFLOW.md"),
                WorkflowText(
                    preReviewMaximum,
                    postApprovalMaximum));
            return WorkflowProvider.ReloadAsync();
        }

        public Task TouchEquivalentWorkflowAsync()
        {
            File.AppendAllText(
                Path.Combine(Root, "WORKFLOW.md"),
                $"{Environment.NewLine}<!-- equivalent policy revision -->{Environment.NewLine}");
            return WorkflowProvider.ReloadAsync();
        }

        public Task WriteInvalidWorkflowAsync()
        {
            File.WriteAllText(
                Path.Combine(Root, "WORKFLOW.md"),
                """
                ---
                studio:
                  invalid_field: true
                ---
                Invalid current workflow.
                """);
            return WorkflowProvider.ReloadAsync();
        }

        public Task<FlowRun> LoadAsync(
            HarnessDbContext database,
            Guid flowId) =>
            database.Flows
                .AsNoTracking()
                .AsSplitQuery()
                .Include(item => item.Steps)
                .Include(item => item.GateRecords)
                .Include(item => item.Events)
                .Include(item => item.AgentSnapshots)
                .Include(item => item.PlanDocuments)
                .Include(item => item.TaskProfiles)
                .SingleAsync(item => item.Id == flowId);

        public ValueTask DisposeAsync()
        {
            _gate.Dispose();
            WorkflowProvider.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
            return ValueTask.CompletedTask;
        }

        private static string WorkflowText(
            string preReviewMaximum,
            string postApprovalMaximum = "Publish") => """
            ---
            tracker:
              kind: voice
              active_states: [Intake, Queued, Running, WaitingForFeedback, Reworking, Abandoning, Blocked]
              terminal_states: [Approved, Abandoned, Failed]
            workspace:
              root: data\worktrees
            agent:
              max_concurrent_agents: 2
              max_attempts: 1
            studio:
              flow_kinds:
                advisory:
                  required_duties: [PrepareOutcome]
                  maximum_permission: ReadOnlySource
                delivery:
                  required_duties: [Implement, Verify, PrepareOutcome, Publish]
                  pre_review_maximum_permission: __PRE_REVIEW_MAXIMUM__
                  post_approval_maximum_permission: __POST_APPROVAL_MAXIMUM__
              permissions:
                read_only_source:
                  additional_denied_tools: []
                workspace_write:
                  additional_denied_tools: []
                publish:
                  additional_denied_tools: []
                pre_mortem_read_only:
                  additional_denied_tools: []
            ---
            {{ task }}
            {{ agent.instructions }}
            {{ workspace }}
            {{ role.context }}
            {{ outcome.context }}
            {{ outcome.contract }}
            {{ response.contract }}
            """.Replace(
                "__PRE_REVIEW_MAXIMUM__",
                preReviewMaximum,
                StringComparison.Ordinal)
            .Replace(
                "__POST_APPROVAL_MAXIMUM__",
                postApprovalMaximum,
                StringComparison.Ordinal);

        private static Task WriteAgentAsync(
            string directory,
            string id,
            string name,
            string description) =>
            File.WriteAllTextAsync(
                Path.Combine(directory, $"{id}.agent.md"),
                $"""
                 ---
                 name: {name}
                 description: {description}
                 role: {id}
                 ---
                 Execute only the assigned work and return the required contract.
                 """);

        private const string DeliveryPlanJson = """
            {"Disposition": "Planned",
              "Steps": [
                {
                  "Id": "outcome",
                  "AgentId": "worker",
                  "Order": 10,
                  "Stage": "BeforeReview",
                  "Assignment": "Prepare the result.",
                  "Justification": "The customer needs a result.",
                  "DependsOn": [],
                  "Duties": ["Implement", "Verify", "PrepareOutcome"],
                  "OutcomeOwner": true,
                  "TaskProfile": {
                    "Complexity": 3,
                    "ReasoningDepth": 3,
                    "ContextDemand": 3,
                    "ToolIntensity": 3,
                    "TaskTypeTags": ["Implementation"],
                    "Risk": "Medium",
                    "RiskReason": "The result changes the project.",
                    "Confidence": 0.9,
                    "Rationales": ["A worker owns the result."]
                  }
                },
                {
                  "Id": "publish",
                  "AgentId": "publisher",
                  "Order": 20,
                  "Stage": "AfterApproval",
                  "Assignment": "Publish the accepted result.",
                  "Justification": "Publication occurs only after review.",
                  "DependsOn": ["outcome"],
                  "Duties": ["Publish"],
                  "OutcomeOwner": false,
                  "TaskProfile": {}
                }
              ],
              "PreMortemCheckpoints": [],
              "MissingQualification": null
            }
            """;

        public sealed class RecoveryDbContextFactory(
            DbContextOptions<HarnessDbContext> options)
            : IDbContextFactory<HarnessDbContext>
        {
            public HarnessDbContext CreateDbContext() => new(options);

            public Task<HarnessDbContext> CreateDbContextAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult(CreateDbContext());
        }

        private sealed class NeverWorkspaceManager : IWorkspaceManager
        {
            public Task<WorkspaceInfo> PrepareAsync(
                FlowRun flow,
                CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException(
                    "Durable-state reconciliation must not prepare a workspace.");
        }

        private sealed class NeverAgentRunner : IAgentRunner
        {
            public Task<AgentExecutionResult> ExecuteAsync(
                AgentExecutionContext context,
                CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException(
                    "Durable-state reconciliation must not run an agent.");
        }
    }

    private sealed class BlockingWorkspaceManager : IWorkspaceManager
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WorkspaceInfo> PrepareAsync(
            FlowRun flow,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocking workspace was unexpectedly released.");
        }
    }
}

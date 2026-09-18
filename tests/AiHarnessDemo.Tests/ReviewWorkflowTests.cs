using System.Text.Json;
using AiHarnessDemo.Api;
using AiHarnessDemo.Contracts;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Security;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Data;
using AiHarnessDemo.Infrastructure;
using AiHarnessDemo.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiHarnessDemo.Tests;

public sealed class ReviewWorkflowTests
{
    [Fact]
    public void DirectReviewRequest_RejectsUnknownProperties()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<DirectReviewRequest>(
                """
                {
                  "GateId": "00000000-0000-0000-0000-000000000001",
                  "Intent": "Accept",
                  "Unknown": true
                }
                """));
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<DirectReviewRequest>(
                """
                {
                  "GateId": "00000000-0000-0000-0000-000000000001",
                  "Intent": "accept"
                }
                """));

        using var gateEngine = new HandoffGateEngine();
        var gate = gateEngine.SubmitProposal(new HandoffProposal
        {
            FlowRunId = Guid.NewGuid(),
            FlowStepId = Guid.NewGuid(),
            ActionType = HandoffActionType.CustomerReview,
            Summary = "Review the result."
        });
        Assert.Equal(
            HandoffGateDecision.AwaitingHumanApproval,
            gate.Decision);
        Assert.Equal(HandoffTrustLevel.Gated, gate.TrustLevelAtDecision);
        Assert.Throws<InvalidOperationException>(() =>
            gateEngine.SetTrustLevel(
                HandoffActionType.CustomerReview,
                HandoffTrustLevel.Auto));
        Assert.Throws<InvalidOperationException>(() =>
            gateEngine.PrepareResolution(
                gate,
                approved: true,
                "customer"));
    }

    [Fact]
    public async Task AdvisoryAcceptance_IsDurableRoleNeutralAndCreatesNoPublication()
    {
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Advisory);
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var before = await harness.LoadFlowAsync();
        Assert.True(
            before.Status == FlowStatus.WaitingForFeedback,
            before.FailureReason);
        var review = Assert.Single(
            before.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);
        Assert.False(review.Resolved);
        Assert.DoesNotContain(
            before.AgentSnapshots,
            snapshot => snapshot.AgentId is "product-manager" or "release-engineer");

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        Assert.Single(
            (await harness.LoadFlowAsync()).GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);

        var restartedGate = new HandoffGateEngine();
        restartedGate.RestoreHistory(before.GateRecords);
        var restartedCoordinator = new ReviewCoordinator(
            harness.Factory,
            restartedGate,
            new FlowQueue(),
            new FlowLifecycleCoordinator(),
            harness.WorkflowProvider);
        var result = await restartedCoordinator.ReviewAsync(
            harness.FlowId,
            new DirectReviewRequest
            {
                GateId = review.Id,
                Intent = ReviewIntent.Accept
            });

        Assert.Equal(FlowStatus.Approved, result.Flow.Status);
        Assert.Equal(ReviewDecision.Accepted, result.Review.Decision);
        Assert.Equal(
            ReviewPublicationStatus.NotApplicable,
            result.Review.PublicationStatus);
        Assert.DoesNotContain(
            result.Flow.Steps,
            step => step.PlanStage == PlanStage.AfterApproval);
        var persistedReview = Assert.Single(
            result.Flow.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);
        Assert.True(persistedReview.Resolved);
        Assert.True(persistedReview.Approved);
        Assert.Equal(ReviewDecision.Accepted, persistedReview.ReviewDecision);
        Assert.Equal(0, harness.PublicationVerifier.Calls);
        restartedGate.Dispose();
    }

    [Fact]
    public async Task DeliveryAcceptance_MaterializesOneArbitraryPublisherAfterApproval()
    {
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Delivery);
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var before = await harness.LoadFlowAsync();
        var reviewedSeal = Assert.Single(
            before.Events,
            item => item.Type == ReviewedCandidateLedger.EventType);
        var reviewedIdentity = ReviewedCandidateLedger.Deserialize(
            reviewedSeal.DataJson!);
        Assert.Equal(before.Iteration, reviewedIdentity.Iteration);
        Assert.Equal(
            before.Steps.Single(step => step.IsOutcomeOwner).Id,
            reviewedIdentity.OutcomeOwnerStepId);
        Assert.Equal(1, harness.ReviewedCandidates.SealCalls);
        var review = Assert.Single(
            before.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);
        Assert.DoesNotContain(
            before.Steps,
            step => step.PlanStage == PlanStage.AfterApproval);
        Assert.DoesNotContain(
            before.AgentSnapshots,
            snapshot => snapshot.AgentId is "product-manager" or "release-engineer");
        var permissionSnapshot = Assert.Single(
            before.Events,
            item =>
                item.Type ==
                "plan.publication-permission-snapshotted");
        Assert.Equal(
            before.Steps.Single(step => step.IsOutcomeOwner).Id,
            permissionSnapshot.FlowStepId);

        var first = await harness.ReviewAsync(
            new DirectReviewRequest
            {
                GateId = review.Id,
                Intent = ReviewIntent.Accept
            });
        var duplicate = await harness.ReviewAsync(
            new DirectReviewRequest
            {
                GateId = review.Id,
                Intent = ReviewIntent.Accept
            });

        Assert.Equal(first.Review.PublicationStepId, duplicate.Review.PublicationStepId);
        var accepted = await harness.LoadFlowAsync();
        var publication = Assert.Single(
            accepted.Steps,
            step => step.PlanStage == PlanStage.AfterApproval);
        Assert.Equal("sky-publisher", publication.AgentId);
        Assert.Equal("Sky Publisher", publication.AgentName);
        Assert.Equal("external-publisher", publication.AgentRole);
        Assert.Equal("publish", publication.PlanStepKey);
        Assert.Equal("[\"Publish\"]", publication.PlanDutiesJson);
        Assert.Equal(
            ExecutionPermissionProfile.Publish,
            publication.PermissionProfile);
        Assert.False(string.IsNullOrWhiteSpace(
            publication.EffectivePermissionJson));
        Assert.True(
            JsonSerializer.Deserialize<EffectiveExecutionPermission>(
                    publication.EffectivePermissionJson)!
                .AllowRemotePublication);
        Assert.True(publication.RemotePublicationAllowed);
        Assert.False(string.IsNullOrWhiteSpace(
            publication.WorkflowRevision));
        Assert.Equal(
            accepted.Steps.Single(step => step.IsOutcomeOwner).Id,
            publication.DependsOnStepId);
        Assert.Equal(FlowStatus.Queued, accepted.Status);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var published = await harness.LoadFlowAsync();
        Assert.True(
            published.Status == FlowStatus.Approved,
            published.FailureReason);
        Assert.Equal(1, harness.PublicationVerifier.Calls);
        Assert.Single(
            published.Steps,
            step => ReviewCoordinator.IsPublicationStep(published, step));
        Assert.Contains(
            harness.Runner.Contexts,
            context =>
                context.AgentId == "sky-publisher" &&
                context.IsHostControlledPublication &&
                !context.AllowRemotePublication);
        Assert.Equal(1, harness.CandidatePublisher.Calls);
        Assert.Contains(
            published.Events,
            item => item.Type ==
                    WorkflowEngine.RepositoryKnowledgeUnchangedEventType);
        Assert.DoesNotContain(
            "correction",
            publication.InputSummary,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ExecutionPermissionProfile.WorkspaceWrite)]
    [InlineData(ExecutionPermissionProfile.ReadOnlySource)]
    public async Task DeliveryAcceptance_LoweredPostApprovalCeilingFailsWithoutResolvingReview(
        ExecutionPermissionProfile maximum)
    {
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Delivery);
        await harness.Engine.RunAsync(
            harness.FlowId,
            CancellationToken.None);
        var review = Assert.Single(
            (await harness.LoadFlowAsync()).GateRecords,
            gate => gate.ActionType ==
                    HandoffActionType.CustomerReview);
        await harness.SetPostApprovalMaximumAsync(maximum);

        var exception =
            await Assert.ThrowsAsync<PublicationPolicyConflictException>(
                () => harness.ReviewAsync(
                    new DirectReviewRequest
                    {
                        GateId = review.Id,
                        Intent = ReviewIntent.Accept
                    }));

        Assert.Equal(maximum, exception.EffectiveProfile);
        Assert.Contains(
            "disabled by the current workflow post-approval permission ceiling",
            exception.Message,
            StringComparison.Ordinal);
        var rejected = await harness.LoadFlowAsync();
        Assert.Equal(
            FlowStatus.WaitingForFeedback,
            rejected.Status);
        Assert.False(Assert.Single(
            rejected.GateRecords,
            gate => gate.Id == review.Id).Resolved);
        Assert.DoesNotContain(
            rejected.Steps,
            step => step.PlanStage ==
                    PlanStage.AfterApproval);
        Assert.Equal(0, harness.CandidatePublisher.Calls);
        Assert.Equal(0, harness.PublicationVerifier.Calls);
    }

    [Fact]
    public async Task PublicationDispatch_InvalidEditCannotBypassLoweredLastKnownGoodCeiling()
    {
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Delivery);
        await harness.Engine.RunAsync(
            harness.FlowId,
            CancellationToken.None);
        var review = Assert.Single(
            (await harness.LoadFlowAsync()).GateRecords,
            gate => gate.ActionType ==
                    HandoffActionType.CustomerReview);
        await harness.ReviewAsync(
            new DirectReviewRequest
            {
                GateId = review.Id,
                Intent = ReviewIntent.Accept
            });
        await harness.SetPostApprovalMaximumAsync(
            ExecutionPermissionProfile.WorkspaceWrite);
        var loweredRevision =
            harness.WorkflowProvider.GetEffective().Revision;
        await harness.InvalidateCurrentWorkflowAsync();
        Assert.False(
            harness.WorkflowProvider.Status().CurrentFileValid);
        Assert.Equal(
            loweredRevision,
            harness.WorkflowProvider.GetEffective().Revision);
        var publicationContextsBefore = harness.Runner.Contexts.Count(
            item => item.InvocationKind ==
                    ExecutionInvocationKind.Publication);

        await harness.Engine.RunAsync(
            harness.FlowId,
            CancellationToken.None);

        var blocked = await harness.LoadFlowAsync();
        var publication = Assert.Single(
            blocked.Steps,
            step => step.PlanStage ==
                    PlanStage.AfterApproval);
        var effective =
            JsonSerializer.Deserialize<EffectiveExecutionPermission>(
                publication.EffectivePermissionJson)!;
        Assert.Equal(FlowStatus.Failed, blocked.Status);
        Assert.Equal(
            ExecutionPermissionProfile.WorkspaceWrite,
            publication.PermissionProfile);
        Assert.False(effective.AllowRemotePublication);
        Assert.False(publication.RemotePublicationAllowed);
        Assert.Contains(
            "disabled by the current workflow post-approval permission ceiling",
            blocked.FailureReason,
            StringComparison.Ordinal);
        Assert.Equal(
            publicationContextsBefore,
            harness.Runner.Contexts.Count(item =>
                item.InvocationKind ==
                ExecutionInvocationKind.Publication));
        Assert.Equal(0, harness.CandidatePublisher.Calls);
        Assert.Equal(0, harness.PublicationVerifier.Calls);
    }

    [Fact]
    public async Task FlowWorker_RetriesFailedDemoRevocationBeforeExecution()
    {
        var queue = new FlowQueue();
        var flowId = Guid.NewGuid();
        var preflightAttempts = 0;
        var executions = 0;
        var executed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task PrepareAsync(Guid _, CancellationToken __)
        {
            if (Interlocked.Increment(ref preflightAttempts) == 1)
            {
                throw new InvalidOperationException(
                    "Synthetic demo revocation failure.");
            }
            return Task.CompletedTask;
        }

        Task RunAsync(Guid _, CancellationToken __)
        {
            Interlocked.Increment(ref executions);
            executed.TrySetResult();
            return Task.CompletedTask;
        }

        using var worker = new FlowWorker(
            queue,
            RunAsync,
            _ => Task.FromResult<IReadOnlyList<Guid>>([]),
            (_, _) => Task.FromResult(true),
            NullLogger<FlowWorker>.Instance,
            PrepareAsync);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(queue.Queue(flowId));
            await executed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, preflightAttempts);
            Assert.Equal(1, executions);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(ReviewIntent.Accept)]
    [InlineData(ReviewIntent.RequestRefinement)]
    public async Task ReviewFollowUp_ConsumedWhileRegisteredRunsOnceWithoutRestart(
        ReviewIntent intent)
    {
        var kind = intent == ReviewIntent.Accept
            ? FlowKind.Delivery
            : FlowKind.Advisory;
        await using var harness =
            await ReviewHarness.CreateAsync(kind);
        await harness.Engine.RunAsync(
            harness.FlowId,
            CancellationToken.None);
        var review = Assert.Single(
            (await harness.LoadFlowAsync()).GateRecords,
            gate => gate.ActionType ==
                    HandoffActionType.CustomerReview);

        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstToUnwind = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var followUpCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runCount = 0;
        async Task RunAsync(Guid flowId, CancellationToken cancellationToken)
        {
            var ordinal = Interlocked.Increment(ref runCount);
            if (ordinal == 1)
            {
                firstStarted.TrySetResult();
                await allowFirstToUnwind.Task.WaitAsync(cancellationToken);
                return;
            }
            if (ordinal != 2)
            {
                throw new InvalidOperationException(
                    "Repeated queue items started more than one follow-up attempt.");
            }

            await harness.Engine.RunAsync(flowId, cancellationToken);
            followUpCompleted.TrySetResult();
        }

        using var worker = new FlowWorker(
            harness.Queue,
            RunAsync,
            _ => Task.FromResult<IReadOnlyList<Guid>>([]),
            harness.Engine.IsRunnableAsync,
            NullLogger<FlowWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(harness.Queue.Queue(harness.FlowId));
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var request = new DirectReviewRequest
            {
                GateId = review.Id,
                Intent = intent,
                Refinement = intent == ReviewIntent.RequestRefinement
                    ? new DirectReviewRefinement
                    {
                        Goal = "Focus on checkout resilience.",
                        RequestedChanges =
                        [
                            "Exclude account services.",
                            "Add operational trade-offs."
                        ]
                    }
                    : null
            };
            await harness.ReviewAsync(request);
            await harness.ReviewAsync(request);
            Assert.True(harness.Queue.Queue(harness.FlowId));
            Assert.True(harness.Queue.Queue(harness.FlowId));

            await WaitUntilAsync(
                () =>
                    harness.Queue.PendingCount == 0 &&
                    worker.HasPendingRerun(harness.FlowId),
                "The worker did not coalesce the queued review follow-up.");
            allowFirstToUnwind.TrySetResult();
            await followUpCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(
                () =>
                    harness.Queue.PendingCount == 0 &&
                    !worker.IsRegistered(harness.FlowId),
                "The worker did not unregister the completed follow-up.");

            var completed = await harness.LoadFlowAsync();
            Assert.Equal(2, Volatile.Read(ref runCount));
            if (intent == ReviewIntent.Accept)
            {
                Assert.Equal(FlowStatus.Approved, completed.Status);
                Assert.Equal(1, harness.PublicationVerifier.Calls);
                Assert.Single(
                    completed.Steps,
                    step =>
                        ReviewCoordinator.IsPublicationStep(
                            completed,
                            step) &&
                        step.Status == StepStatus.Completed);
            }
            else
            {
                Assert.Equal(
                    FlowStatus.WaitingForFeedback,
                    completed.Status);
                Assert.Equal(2, completed.Iteration);
                Assert.Equal(2, completed.PlanDocuments.Count);
                Assert.Equal(
                    2,
                    completed.GateRecords.Count(gate =>
                        gate.ActionType ==
                        HandoffActionType.CustomerReview));
            }
        }
        finally
        {
            allowFirstToUnwind.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ResolvedReviewReplay_RequeuesDurableFollowUpWhenNoRegistrationExists()
    {
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Delivery);
        await harness.Engine.RunAsync(
            harness.FlowId,
            CancellationToken.None);
        var gate = Assert.Single(
            (await harness.LoadFlowAsync()).GateRecords,
            item => item.ActionType ==
                    HandoffActionType.CustomerReview);
        var request = new DirectReviewRequest
        {
            GateId = gate.Id,
            Intent = ReviewIntent.Accept
        };

        await harness.ReviewAsync(request);
        Assert.Equal(
            harness.FlowId,
            await harness.Queue.Reader.ReadAsync());
        harness.Queue.MarkDequeued();

        await harness.ReviewAsync(request);

        Assert.Equal(
            harness.FlowId,
            await harness.Queue.Reader.ReadAsync());
        harness.Queue.MarkDequeued();
        Assert.Equal(0, harness.Queue.PendingCount);
        Assert.Equal(
            FlowStatus.Queued,
            (await harness.LoadFlowAsync()).Status);
    }

    [Fact]
    public async Task DeliveryAcceptance_RejectsPersistedNoneBeforePublicationMaterializes()
    {
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Delivery);
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var review = Assert.Single(
            (await harness.LoadFlowAsync()).GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);
        await using (var database =
                     await harness.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows.SingleAsync(item =>
                item.Id == harness.FlowId);
            flow.Outcome = OutcomeType.None;
            await database.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.ReviewAsync(
                new DirectReviewRequest
                {
                    GateId = review.Id,
                    Intent = ReviewIntent.Accept
                }));
        var rejected = await harness.LoadFlowAsync();

        Assert.Equal(FlowStatus.WaitingForFeedback, rejected.Status);
        Assert.False(Assert.Single(
            rejected.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview).Resolved);
        Assert.DoesNotContain(
            rejected.Steps,
            step => step.PlanStage == PlanStage.AfterApproval);
        Assert.Equal(0, harness.CandidatePublisher.Calls);
        Assert.Equal(0, harness.PublicationVerifier.Calls);
    }

    [Fact]
    public async Task FailedPublication_RetryKeepsDurableAcceptanceAndSemanticStep()
    {
        var verifier = new RecordingPublicationVerifier(failuresBeforeSuccess: 1);
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Delivery, verifier);
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var review = Assert.Single(
            (await harness.LoadFlowAsync()).GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);
        await harness.ReviewAsync(
            new DirectReviewRequest
            {
                GateId = review.Id,
                Intent = ReviewIntent.Accept
            });

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var failed = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Failed, failed.Status);
        Assert.Equal(
            ReviewDecision.Accepted,
            failed.GateRecords.Single(gate =>
                gate.ActionType == HandoffActionType.CustomerReview).ReviewDecision);

        await harness.Engine.RestartFailedFlowAsync(
            harness.FlowId,
            CancellationToken.None);
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var recovered = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Approved, recovered.Status);
        Assert.Equal(2, verifier.Calls);
        var attempts = recovered.Steps
            .Where(step =>
                step.PlanStepKey == "publish" &&
                step.PlanStage == PlanStage.AfterApproval)
            .OrderBy(step => step.Sequence)
            .ToList();
        Assert.Equal(2, attempts.Count);
        Assert.Equal(StepStatus.Failed, attempts[0].Status);
        Assert.Equal(StepStatus.Completed, attempts[1].Status);
        Assert.Equal(
            attempts[0].StableSemanticRootId,
            attempts[1].StableSemanticRootId);
        Assert.Equal(
            ReviewDecision.Accepted,
            recovered.GateRecords.Single(gate =>
                gate.ActionType == HandoffActionType.CustomerReview).ReviewDecision);
        Assert.Single(
            recovered.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);
        Assert.Single(
            recovered.Events,
            item => item.Type == ReviewedCandidateLedger.EventType);
        Assert.Equal(1, harness.ReviewedCandidates.SealCalls);
    }

    [Fact]
    public async Task FailedPublication_RetryUnderLoweredCeilingRemainsBlocked()
    {
        var verifier = new RecordingPublicationVerifier(
            failuresBeforeSuccess: 1);
        await using var harness =
            await ReviewHarness.CreateAsync(
                FlowKind.Delivery,
                verifier);
        await harness.Engine.RunAsync(
            harness.FlowId,
            CancellationToken.None);
        var review = Assert.Single(
            (await harness.LoadFlowAsync()).GateRecords,
            gate => gate.ActionType ==
                    HandoffActionType.CustomerReview);
        await harness.ReviewAsync(
            new DirectReviewRequest
            {
                GateId = review.Id,
                Intent = ReviewIntent.Accept
            });
        await harness.Engine.RunAsync(
            harness.FlowId,
            CancellationToken.None);
        var callsBeforeRetry = harness.CandidatePublisher.Calls;
        Assert.Equal(1, callsBeforeRetry);
        await harness.SetPostApprovalMaximumAsync(
            ExecutionPermissionProfile.ReadOnlySource);
        var loweredRevision =
            harness.WorkflowProvider.GetEffective().Revision;
        await harness.InvalidateCurrentWorkflowAsync();
        Assert.False(
            harness.WorkflowProvider.Status().CurrentFileValid);
        Assert.Equal(
            loweredRevision,
            harness.WorkflowProvider.GetEffective().Revision);

        var exception =
            await Assert.ThrowsAsync<PublicationPolicyConflictException>(
                () => harness.Engine.RestartFailedFlowAsync(
                    harness.FlowId,
                    CancellationToken.None));

        Assert.Equal(
            ExecutionPermissionProfile.ReadOnlySource,
            exception.EffectiveProfile);
        var blocked = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Failed, blocked.Status);
        Assert.Equal(
            ReviewDecision.Accepted,
            Assert.Single(
                blocked.GateRecords,
                gate => gate.ActionType ==
                        HandoffActionType.CustomerReview)
                .ReviewDecision);
        Assert.Single(
            blocked.Steps,
            step => step.PlanStage ==
                    PlanStage.AfterApproval);
        Assert.Equal(
            callsBeforeRetry,
            harness.CandidatePublisher.Calls);
        Assert.Equal(1, verifier.Calls);
    }

    [Fact]
    public async Task PublicationLaunch_RejectsCandidateDriftBeforeAnyHostSideEffect()
    {
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Delivery);
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var review = Assert.Single(
            (await harness.LoadFlowAsync()).GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);
        await harness.ReviewAsync(
            new DirectReviewRequest
            {
                GateId = review.Id,
                Intent = ReviewIntent.Accept
            });

        harness.ReviewedCandidates.FailVerification = true;
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var failed = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Failed, failed.Status);
        Assert.Contains("candidate drift", failed.FailureReason);
        Assert.Equal(0, harness.CandidatePublisher.Calls);
        Assert.Equal(0, harness.PublicationVerifier.Calls);
        Assert.Single(
            failed.Events,
            item => item.Type == ReviewedCandidateLedger.EventType);
    }

    [Theory]
    [InlineData("Missing exact status")]
    [InlineData("HANDOFF_STATUS: complete")]
    [InlineData("HANDOFF_STATUS: COMPLETE\nHANDOFF_STATUS: COMPLETE")]
    [InlineData("HANDOFF_STATUS: COMPLETE\nPUSHBACK_REASON: contradictory")]
    [InlineData("HANDOFF_STATUS: COMPLETE\n\n## Decision\nPublication prepared without a repository knowledge recap.")]
    public async Task PublicationCompletion_RejectsInvalidContractBeforePublisherSideEffects(
        string publicationOutput)
    {
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Delivery);
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var review = Assert.Single(
            (await harness.LoadFlowAsync()).GateRecords,
            gate => gate.ActionType ==
                    HandoffActionType.CustomerReview);
        await harness.ReviewAsync(
            new DirectReviewRequest
            {
                GateId = review.Id,
                Intent = ReviewIntent.Accept
            });
        harness.Runner.PublicationOutputOverride =
            publicationOutput.Replace("\\n", Environment.NewLine);

        await harness.Engine.RunAsync(
            harness.FlowId,
            CancellationToken.None);

        Assert.Equal(0, harness.CandidatePublisher.Calls);
        Assert.Equal(0, harness.PublicationVerifier.Calls);
        var failed = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Failed, failed.Status);
        Assert.DoesNotContain(
            failed.Events,
            item => item.Type.StartsWith(
                "publication.",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublicationCompletion_ValidPushbackRemainsNonPublishing()
    {
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Delivery);
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var review = Assert.Single(
            (await harness.LoadFlowAsync()).GateRecords,
            gate => gate.ActionType ==
                    HandoffActionType.CustomerReview);
        await harness.ReviewAsync(
            new DirectReviewRequest
            {
                GateId = review.Id,
                Intent = ReviewIntent.Accept
            });
        harness.Runner.PublicationOutputOverride = """
            HANDOFF_STATUS: PUSHBACK
            PUSHBACK_OWNER_STEP_ID: prepare
            PUSHBACK_REASON: The reviewed candidate identity needs clarification.
            """;

        await harness.Engine.RunAsync(
            harness.FlowId,
            CancellationToken.None);

        Assert.Equal(0, harness.CandidatePublisher.Calls);
        Assert.Equal(0, harness.PublicationVerifier.Calls);
        var flow = await harness.LoadFlowAsync();
        Assert.Contains(
            flow.Steps,
            step =>
                step.PlanStepKey == "publish" &&
                step.Status == StepStatus.Pushback);
    }

    [Fact]
    public async Task Refinement_ReusesFlowWorkspaceAndSnapshotThenCreatesNewPlanAndReview()
    {
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Advisory);
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var first = await harness.LoadFlowAsync();
        var snapshot = first.AgentSnapshots
            .OrderBy(item => item.AgentId)
            .Select(item => (item.AgentId, item.DefinitionHash, item.CapturedAt))
            .ToArray();
        var firstReview = Assert.Single(
            first.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);

        var refinementRequest = new DirectReviewRequest
        {
            GateId = firstReview.Id,
            Intent = ReviewIntent.RequestRefinement,
            Refinement = new DirectReviewRefinement
            {
                Goal = "  Focus on checkout resilience.  ",
                RequestedChanges =
                [
                    "  Exclude account services.  ",
                    "Exclude account services.",
                    "Add operational trade-offs."
                ]
            }
        };
        var refinement = await harness.ReviewAsync(
            refinementRequest);
        var replay = await harness.ReviewAsync(
            refinementRequest);
        var differentReplay = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.ReviewAsync(
                new DirectReviewRequest
                {
                    GateId = firstReview.Id,
                    Intent = ReviewIntent.RequestRefinement,
                    Refinement = new DirectReviewRefinement
                    {
                        Goal = "Focus on a materially different goal.",
                        RequestedChanges = ["Replace the accepted scope."]
                    }
                }));

        Assert.Equal(harness.FlowId, refinement.Flow.Id);
        Assert.Equal(2, refinement.Flow.Iteration);
        Assert.Equal(2, replay.Flow.Iteration);
        Assert.Equal(
            refinement.Review.GateId,
            replay.Review.GateId);
        Assert.Contains("materially different", differentReplay.Message);
        Assert.Equal(FlowStatus.Reworking, refinement.Flow.Status);
        Assert.Equal(
            harness.FlowId,
            Assert.Single(harness.DemoRevoker.FlowIds));
        Assert.All(
            harness.DemoRevoker.QueuePendingAtRevoke,
            Assert.True);
        Assert.All(
            harness.DemoRevoker.CancellationCanBeCanceled,
            Assert.False);
        Assert.Equal(first.WorkspacePath, refinement.Flow.WorkspacePath);
        Assert.StartsWith(
            "Customer refinement for iteration 1:",
            refinement.Flow.ConsolidatedRequest,
            StringComparison.Ordinal);
        Assert.Contains(
            "Current reviewed flow outcome JSON:",
            refinement.Flow.ConsolidatedRequest,
            StringComparison.Ordinal);
        Assert.Contains(
            "Previous confirmed brief:",
            refinement.Flow.ConsolidatedRequest,
            StringComparison.Ordinal);
        Assert.True(
            refinement.Flow.ConsolidatedRequest.IndexOf(
                "Current reviewed flow outcome JSON:",
                StringComparison.Ordinal) <
            refinement.Flow.ConsolidatedRequest.IndexOf(
                "Previous confirmed brief:",
                StringComparison.Ordinal));
        Assert.Equal(
            ReviewDecision.RefinementRequested,
            refinement.Flow.GateRecords.Single(gate =>
                gate.Id == firstReview.Id).ReviewDecision);
        Assert.Equal(
            snapshot,
            refinement.Flow.AgentSnapshots
                .OrderBy(item => item.AgentId)
                .Select(item =>
                    (item.AgentId, item.DefinitionHash, item.CapturedAt))
                .ToArray());

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var replanned = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.WaitingForFeedback, replanned.Status);
        Assert.Equal(2, replanned.Iteration);
        Assert.Equal(2, replanned.PlanDocuments.Count);
        var reviews = replanned.GateRecords
            .Where(gate => gate.ActionType == HandoffActionType.CustomerReview)
            .OrderBy(gate => gate.DecidedAt)
            .ToList();
        Assert.Equal(2, reviews.Count);
        Assert.True(reviews[0].Resolved);
        Assert.False(reviews[1].Resolved);
        Assert.Contains(
            replanned.Events,
            item =>
                item.Type == "flow.review-refinement-requested" &&
                item.DataJson?.Contains(
                    "Exclude account services.",
                    StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task DeliveryReadiness_FailedCriterionWithCompleteMarkerCreatesNoReviewOrPublication()
    {
        // R10: reproduce the incident. The verification turn reports HANDOFF_STATUS: COMPLETE and
        // confident prose while its typed criterion result is Failed.
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Delivery);
        harness.Runner.QaBlockOverride = context => DeliveryReadinessFixtures.QaBlockFromPrompt(context.OutcomeContext, outcome: DeliveryCriterionOutcome.Failed);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        // The harness now spends its own refinement budget before it ever asks the customer, so a
        // permanently failing criterion first produces a host-owned iteration, not a question.
        var afterFirstPass = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Reworking, afterFirstPass.Status);
        Assert.Equal(2, afterFirstPass.Iteration);
        Assert.Contains(
            afterFirstPass.Events,
            item => item.Type == "delivery.auto-refinement-scheduled");
        Assert.DoesNotContain(
            afterFirstPass.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);

        // The second pass closes nothing, so the no-progress guard escalates instead of looping.
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.WaitingForFeedback, flow.Status);
        Assert.Contains(
            flow.Events,
            item => item.Type == "delivery.auto-refinement-exhausted");
        Assert.DoesNotContain(
            flow.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);
        Assert.DoesNotContain(
            flow.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerWaiver);
        Assert.Equal(0, harness.CandidatePublisher.Calls);
        Assert.Equal(0, harness.PublicationVerifier.Calls);
        Assert.Equal("delivery.readiness-needs-refinement", flow.CurrentBlockerCode);

        await using var database = await harness.Factory.CreateDbContextAsync();
        var readiness = Assert.Single(
            await database.DeliveryReadinessSnapshots
                .Where(item => item.FlowRunId == harness.FlowId && item.Active)
                .ToListAsync());
        Assert.Equal(DeliveryReadinessState.NeedsRefinement, readiness.State);
        var contract = DeliveryReadinessPolicy.DeserializeSnapshot(readiness.ContractJson);
        Assert.Equal(
            DeliveryCriterionOutcome.Failed,
            Assert.Single(contract.Criteria).Outcome);

        // A direct API caller cannot convert the failure into acceptance or a waiver.
        var conflict = await Assert.ThrowsAsync<DeliveryReadinessConflictException>(
            () => harness.Reviews.GrantReadinessWaiverAsync(
                harness.FlowId,
                new ReadinessWaiverRequest
                {
                    GateId = Guid.NewGuid(),
                    ReviewedCandidateId = Guid.NewGuid(),
                    ReadinessRevision = readiness.Revision,
                    ReadinessContractHash = readiness.ContractHash,
                    RiskIds = ["AC-001"],
                    Acknowledgement = "I accept the risk."
                }));
        Assert.Equal(
            DeliveryReadinessConflicts.CandidateStale,
            conflict.Code);
        Assert.NotEqual(FlowStatus.Approved, (await harness.LoadFlowAsync()).Status);
    }

    [Fact]
    public async Task NeedsRefinement_HarnessClosesTheGapItselfWithoutAskingTheCustomer()
    {
        // The verification turn already names the remediation and its owner, so a recoverable
        // Delivery failure must produce another iteration, not a question for the customer.
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Delivery);
        var qaCalls = 0;
        harness.Runner.QaBlockOverride = context =>
            DeliveryReadinessFixtures.QaBlockFromPrompt(
                context.OutcomeContext,
                outcome: ++qaCalls == 1
                    ? DeliveryCriterionOutcome.Failed
                    : DeliveryCriterionOutcome.Verified);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var reworking = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Reworking, reworking.Status);
        Assert.Equal(2, reworking.Iteration);
        Assert.DoesNotContain(
            reworking.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);
        var scheduled = Assert.Single(
            reworking.Events,
            item => item.Type == "delivery.auto-refinement-scheduled");
        Assert.Contains("AC-001", scheduled.DataJson!, StringComparison.Ordinal);

        // The seed is attributed to the harness and carries the QA turn's own remediation text,
        // so nobody has to retype what the Quality Engineer already wrote.
        var seed = Assert.Single(
            reworking.Messages,
            message => message.Role == ConversationRole.Harness);
        Assert.Contains(
            "Correct the failing behavior and re-verify.",
            seed.Content,
            StringComparison.Ordinal);
        Assert.Contains("external-delivery", seed.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(
            reworking.Events,
            item => item.Type == "flow.review-refinement-requested");

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var converged = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.WaitingForFeedback, converged.Status);
        Assert.Equal(2, converged.Iteration);
        var review = Assert.Single(
            converged.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);
        Assert.False(review.Resolved);
        Assert.DoesNotContain(
            converged.Events,
            item => item.Type == "delivery.auto-refinement-exhausted");

        await using var database = await harness.Factory.CreateDbContextAsync();
        var readiness = Assert.Single(
            await database.DeliveryReadinessSnapshots
                .Where(item => item.FlowRunId == harness.FlowId && item.Active)
                .ToListAsync());
        Assert.Equal(DeliveryReadinessState.ReadyToApprove, readiness.State);
    }

    [Fact]
    public async Task NeedsRefinement_StopsWhenTheRefinementBudgetIsSpent()
    {
        // The loop is bounded: unlike the unbounded re-dispatch it is modelled on, a budget of one
        // buys exactly one host-owned attempt before the customer is asked.
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery,
            maxAutoRefinementIterations: 1);
        harness.Runner.QaBlockOverride = context =>
            DeliveryReadinessFixtures.QaBlockFromPrompt(
                context.OutcomeContext,
                outcome: DeliveryCriterionOutcome.Failed);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        Assert.Equal(
            FlowStatus.Reworking,
            (await harness.LoadFlowAsync()).Status);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.WaitingForFeedback, flow.Status);
        Assert.Single(
            flow.Events,
            item => item.Type == "delivery.auto-refinement-scheduled");
        var exhausted = Assert.Single(
            flow.Events,
            item => item.Type == "delivery.auto-refinement-exhausted");
        Assert.Contains("budget of 1", exhausted.Message, StringComparison.Ordinal);
        Assert.Equal("delivery.readiness-needs-refinement", flow.CurrentBlockerCode);
        Assert.Equal(0, harness.CandidatePublisher.Calls);
    }

    [Fact]
    public async Task NeedsRefinement_WithoutABudgetKeepsTheCustomerGateUnchanged()
    {
        // Setting the budget to zero restores the previous always-ask behavior exactly.
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery,
            maxAutoRefinementIterations: 0);
        harness.Runner.QaBlockOverride = context =>
            DeliveryReadinessFixtures.QaBlockFromPrompt(
                context.OutcomeContext,
                outcome: DeliveryCriterionOutcome.Failed);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.WaitingForFeedback, flow.Status);
        Assert.Equal(1, flow.Iteration);
        Assert.Equal("delivery.readiness-needs-refinement", flow.CurrentBlockerCode);
        Assert.DoesNotContain(
            flow.Events,
            item => item.Type == "delivery.auto-refinement-scheduled");
        Assert.Contains(
            flow.Events,
            item => item.Type == "delivery.auto-refinement-exhausted");
    }

    [Fact]
    public async Task DeliveryReadiness_BlockedCriterionBlocksTheFlowWithoutAnyCustomerGate()
    {
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Delivery);
        harness.Runner.QaBlockOverride = context => DeliveryReadinessFixtures.QaBlockFromPrompt(context.OutcomeContext, outcome: DeliveryCriterionOutcome.Blocked);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Blocked, flow.Status);
        Assert.Equal("delivery.readiness-blocked", flow.CurrentBlockerCode);
        Assert.DoesNotContain(flow.GateRecords, gate =>
            gate.ActionType is HandoffActionType.CustomerReview
                or HandoffActionType.CustomerWaiver);
        Assert.Equal(0, harness.CandidatePublisher.Calls);
    }

    [Fact]
    public async Task DeliveryReadiness_WaiverRequiredRiskOpensOnlyTheSeparateWaiverGate()
    {
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Delivery);
        harness.Runner.QaBlockOverride = context => DeliveryReadinessFixtures.QaBlockFromPrompt(context.OutcomeContext, risks: [("RR-001", DeliveryRiskClassification.WaiverRequired)]);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.WaitingForFeedback, flow.Status);
        var waiverGate = Assert.Single(
            flow.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerWaiver);
        Assert.False(waiverGate.Resolved);
        Assert.DoesNotContain(
            flow.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);

        DeliveryReadinessSnapshotRecord readiness;
        await using (var database = await harness.Factory.CreateDbContextAsync())
        {
            readiness = await database.DeliveryReadinessSnapshots
                .SingleAsync(item => item.FlowRunId == harness.FlowId && item.Active);
        }
        Assert.Equal(DeliveryReadinessState.NeedsCustomerWaiver, readiness.State);

        Guid candidateId;
        await using (var database = await harness.Factory.CreateDbContextAsync())
        {
            candidateId = (await database.ReviewedCandidateRecords
                .SingleAsync(item => item.FlowRunId == harness.FlowId && item.Active)).Id;
        }

        // A criterion is a schema-invalid waiver target and a stale revision is a typed conflict.
        var criterionWaiver = await Assert.ThrowsAsync<DeliveryReadinessConflictException>(
            () => harness.Reviews.GrantReadinessWaiverAsync(
                harness.FlowId,
                Waiver(waiverGate.Id, candidateId, readiness, ["AC-001"])));
        Assert.Equal(
            DeliveryReadinessConflicts.WaiverNotApplicable,
            criterionWaiver.Code);
        var staleWaiver = await Assert.ThrowsAsync<DeliveryReadinessConflictException>(
            () => harness.Reviews.GrantReadinessWaiverAsync(
                harness.FlowId,
                new ReadinessWaiverRequest
                {
                    GateId = waiverGate.Id,
                    ReviewedCandidateId = candidateId,
                    ReadinessRevision = readiness.Revision + 7,
                    ReadinessContractHash = readiness.ContractHash,
                    RiskIds = ["RR-001"],
                    Acknowledgement = "I accept the disclosed risk."
                }));
        Assert.Equal(DeliveryReadinessConflicts.ReviewStale, staleWaiver.Code);

        var granted = await harness.Reviews.GrantReadinessWaiverAsync(
            harness.FlowId,
            Waiver(waiverGate.Id, candidateId, readiness, ["RR-001"]));
        Assert.Equal(["RR-001"], granted.WaivedRiskIds);

        var afterWaiver = await harness.LoadFlowAsync();
        var review = Assert.Single(
            afterWaiver.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);
        Assert.False(review.Resolved);
        Assert.True(
            Assert.Single(
                afterWaiver.GateRecords,
                gate => gate.ActionType == HandoffActionType.CustomerWaiver).Resolved);

        await using var verifyDatabase = await harness.Factory.CreateDbContextAsync();
        var refreshed = await verifyDatabase.DeliveryReadinessSnapshots
            .SingleAsync(item => item.FlowRunId == harness.FlowId && item.Active);
        Assert.Equal(DeliveryReadinessState.ReadyToApprove, refreshed.State);
        Assert.Equal(2, refreshed.Revision);
        Assert.Single(await verifyDatabase.ReadinessWaiverRecords
            .Where(item => item.FlowRunId == harness.FlowId)
            .ToListAsync());

        // Replaying the same waiver after readiness advanced is a typed conflict, not a second
        // receipt and not an acceptance.
        var replay = await Assert.ThrowsAsync<DeliveryReadinessConflictException>(
            () => harness.Reviews.GrantReadinessWaiverAsync(
                harness.FlowId,
                Waiver(waiverGate.Id, candidateId, readiness, ["RR-001"])));
        Assert.Equal(DeliveryReadinessConflicts.CandidateStale, replay.Code);
        Assert.Single(await verifyDatabase.ReadinessWaiverRecords
            .Where(item => item.FlowRunId == harness.FlowId)
            .ToListAsync());
    }

    [Fact]
    public async Task DeliveryReadiness_NonBlockingDisclosureStaysReadyAndRestartIsIdempotent()
    {
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Delivery);
        harness.Runner.QaBlockOverride = context => DeliveryReadinessFixtures.QaBlockFromPrompt(context.OutcomeContext, risks: [("RR-001", DeliveryRiskClassification.NonBlockingDisclosure)]);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var first = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.WaitingForFeedback, first.Status);
        Assert.Single(
            first.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);

        string hash;
        await using (var database = await harness.Factory.CreateDbContextAsync())
        {
            var readiness = await database.DeliveryReadinessSnapshots
                .SingleAsync(item => item.FlowRunId == harness.FlowId && item.Active);
            Assert.Equal(DeliveryReadinessState.ReadyToApprove, readiness.State);
            hash = readiness.ContractHash;
        }

        // Restart reconciliation repeats the derivation from durable rows and produces no new
        // revision, gate, or event when the facts are unchanged.
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        await using var afterRestart = await harness.Factory.CreateDbContextAsync();
        var rows = await afterRestart.DeliveryReadinessSnapshots
            .Where(item => item.FlowRunId == harness.FlowId)
            .ToListAsync();
        Assert.Single(rows);
        Assert.Equal(hash, rows[0].ContractHash);
        Assert.Single(
            (await harness.LoadFlowAsync()).GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);
    }

    [Fact]
    public async Task DeliveryReadiness_InvalidQaContractFailsTheVerificationTurnClosed()
    {
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Delivery);
        // Exact enum casing is part of the contract; a lowercase outcome is not "close enough".
        harness.Runner.QaBlockOverride = context => DeliveryReadinessFixtures.QaBlockFromPrompt(context.OutcomeContext).Replace("\"Verified\"", "\"verified\"", StringComparison.Ordinal);

        try
        {
            await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not Xunit.Sdk.XunitException)
        {
            // The verification turn fails closed; the assertions below prove nothing advanced.
        }

        var flow = await harness.LoadFlowAsync();
        Assert.DoesNotContain(
            flow.GateRecords,
            gate => gate.ActionType is HandoffActionType.CustomerReview
                or HandoffActionType.CustomerWaiver);
        Assert.NotEqual(FlowStatus.Approved, flow.Status);
        Assert.Equal(0, harness.CandidatePublisher.Calls);
        await using var database = await harness.Factory.CreateDbContextAsync();
        Assert.Empty(await database.DeliveryReadinessSnapshots
            .Where(item => item.FlowRunId == harness.FlowId)
            .ToListAsync());
        Assert.Equal(2, harness.Runner.Contexts.Count(context => context.RequiresDeliveryReadinessQa));
        Assert.Single(flow.Events, item => item.Type == "agent.contract-correction-scheduled");
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("unsuccessful")]
    [InlineData("kind")]
    [InlineData("hash")]
    [InlineData("casing")]
    public async Task DeliveryReadiness_InvalidCitationsAreCorrectedWithoutRepeatingImplementation(
        string defect)
    {
        await using var harness = await ReviewHarness.CreateAsync(FlowKind.Delivery);
        var verificationTurns = 0;
        harness.Runner.VerificationToolCallsOverride = context =>
            context.Task.Contains("previous Studio response contract was invalid", StringComparison.Ordinal)
                ? []
                :
                [
                    new ToolCallRecord(
                        "observe", "Failed browser check.", false, ToolType: "Observation",
                        ExitCode: 1, ResultSummary: "The first check failed."),
                    new ToolCallRecord(
                        "view", "Read source.", true, ToolType: "Read",
                        ResultSummary: "Source inspection is not a browser observation."),
                    new ToolCallRecord(
                        "observe", "Successful browser check.", true, ToolType: "Observation",
                        ResultDigest: OutcomeVerificationRules.ComputeSha256("verified candidate"),
                        ResultSummary: "The corrected browser check passed.")
                ];
        harness.Runner.QaBlockOverride = context =>
        {
            verificationTurns++;
            if (verificationTurns > 1)
            {
                return DeliveryReadinessFixtures.QaBlockFromPrompt(context.OutcomeContext);
            }
            var prefix = System.Text.RegularExpressions.Regex.Match(
                context.OutcomeContext,
                @"Current verification step evidence prefix: (EV-S[0-9]+-)").Groups[1].Value;
            var evidenceId = prefix + (defect switch
            {
                "unknown" => "999",
                "unsuccessful" => "001",
                "kind" => "002",
                _ => "003"
            });
            var block = DeliveryReadinessFixtures.QaBlockFromPrompt(
                context.OutcomeContext,
                evidenceOverride: [evidenceId],
                planHashOverride: defect == "hash"
                    ? OutcomeVerificationRules.ComputeSha256("wrong plan")
                    : null);
            return defect == "casing"
                ? block.Replace("\"Verified\"", "\"verified\"", StringComparison.Ordinal)
                : block;
        };

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.WaitingForFeedback, flow.Status);
        Assert.Equal(2, verificationTurns);
        var verificationContexts = harness.Runner.Contexts
            .Where(context => context.RequiresDeliveryReadinessQa).ToArray();
        Assert.False(verificationContexts[1].ResumeSession);
        Assert.NotEqual(
            verificationContexts[0].CopilotSessionId,
            verificationContexts[1].CopilotSessionId);
        Assert.Single(harness.Runner.Contexts, context => context.PlanStepKey == "implement");
        Assert.DoesNotContain(flow.Events, item => item.Type is "flow.failed" or "handoff.pushback");
        var correctionEvent = Assert.Single(
            flow.Events, item => item.Type == "agent.contract-correction-scheduled");
        var correction = Assert.Single(flow.Steps, step => step.Id == correctionEvent.FlowStepId);
        var original = Assert.Single(flow.Steps, step => step.Id == correction.RetryOfStepId);
        Assert.Equal(StepStatus.Completed, original.Status);
        Assert.Equal(StepStatus.Completed, correction.Status);
        Assert.Equal(original.WorkflowRevision, correction.WorkflowRevision);
        var originalPermission =
            System.Text.Json.JsonSerializer.Deserialize<AiHarnessDemo.Core.Security.EffectiveExecutionPermission>(
                original.EffectivePermissionJson)!;
        var correctionPermission =
            System.Text.Json.JsonSerializer.Deserialize<AiHarnessDemo.Core.Security.EffectiveExecutionPermission>(
                correction.EffectivePermissionJson)!;
        Assert.All(correctionPermission.AllowedTools, tool => Assert.Contains(tool, originalPermission.AllowedTools));
        Assert.Contains("write", correctionPermission.DeniedTools);
        Assert.Contains("shell", correctionPermission.DeniedTools);
        Assert.DoesNotContain("powershell", correctionPermission.AllowedTools);
        Assert.DoesNotContain("bash", correctionPermission.AllowedTools);
        Assert.Equal(1, original.ExecutionAttempts);
        Assert.Contains("Previous response to correct:", correction.InputSummary);
        Assert.Single(flow.GateRecords, gate => gate.ActionType == HandoffActionType.CustomerReview);
        Assert.Equal(0, harness.CandidatePublisher.Calls);

        await using var database = await harness.Factory.CreateDbContextAsync();
        Assert.Equal(3, await database.AgentToolCalls.CountAsync(call => call.FlowStepId == original.Id));
        var issued = DeliveryReadinessService.ReadStepEvidence(flow.Events, flow.Iteration, original.Id)!;
        Assert.False(issued.Items[1].SupportsVerification);
        Assert.Equal(OutcomeEvidenceKind.SourceInspection, issued.Items[2].Kind);
        Assert.Equal("The corrected browser check passed.", issued.Items[3].Summary);
        Assert.Single(
            flow.Events,
            item => item.FlowStepId == original.Id &&
                item.Type == DeliveryReadinessService.EvidenceEventType &&
                item.DataJson == DeliveryReadinessService.SerializeEvidence(issued));
    }

    private static ReadinessWaiverRequest Waiver(
        Guid gateId,
        Guid candidateId,
        DeliveryReadinessSnapshotRecord readiness,
        IReadOnlyList<string> riskIds) =>
        new()
        {
            GateId = gateId,
            ReviewedCandidateId = candidateId,
            ReadinessRevision = readiness.Revision,
            ReadinessContractHash = readiness.ContractHash,
            RiskIds = riskIds,
            Acknowledgement = "I acknowledge and accept the disclosed residual risk."
        };

    [Fact]
    public void DeliveryReadinessPolicy_DerivesStateFromTypedFactsOnly()
    {
        var verified = Criterion(DeliveryCriterionOutcome.Verified);
        var failed = Criterion(DeliveryCriterionOutcome.Failed);
        var blocked = Criterion(DeliveryCriterionOutcome.Blocked);
        var waiverRisk = Risk(DeliveryRiskClassification.WaiverRequired);
        var blockingRisk = Risk(DeliveryRiskClassification.Blocking);
        var disclosure = Risk(DeliveryRiskClassification.NonBlockingDisclosure);

        Assert.Equal(
            DeliveryReadinessState.ReadyToApprove,
            DeliveryReadinessPolicy.DeriveState([verified], [disclosure], [], []));
        Assert.Equal(
            DeliveryReadinessState.NeedsCustomerWaiver,
            DeliveryReadinessPolicy.DeriveState([verified], [waiverRisk], [], []));
        Assert.Equal(
            DeliveryReadinessState.ReadyToApprove,
            DeliveryReadinessPolicy.DeriveState(
                [verified],
                [waiverRisk],
                [],
                [waiverRisk.RiskId]));
        Assert.Equal(
            DeliveryReadinessState.NeedsRefinement,
            DeliveryReadinessPolicy.DeriveState([failed], [], [], []));
        Assert.Equal(
            DeliveryReadinessState.NeedsRefinement,
            DeliveryReadinessPolicy.DeriveState([], [], [], []));
        Assert.Equal(
            DeliveryReadinessState.NeedsRefinement,
            DeliveryReadinessPolicy.DeriveState([verified], [], ["a plan gap"], []));
        Assert.Equal(
            DeliveryReadinessState.Blocked,
            DeliveryReadinessPolicy.DeriveState([blocked], [], [], []));
        Assert.Equal(
            DeliveryReadinessState.Blocked,
            DeliveryReadinessPolicy.DeriveState([verified], [blockingRisk], [], []));

        // A failed criterion can never be waived into readiness, even if it is also named as a
        // waived risk identifier.
        Assert.Equal(
            DeliveryReadinessState.NeedsRefinement,
            DeliveryReadinessPolicy.DeriveState(
                [failed],
                [waiverRisk],
                [],
                [waiverRisk.RiskId, failed.CriterionId]));

        Assert.Equal(
            [DeliveryReadinessAction.Accept, DeliveryReadinessAction.RequestRefinement],
            DeliveryReadinessPolicy.AllowedActions(
                DeliveryReadinessState.ReadyToApprove));
        Assert.Equal(
            [DeliveryReadinessAction.RequestRefinement],
            DeliveryReadinessPolicy.AllowedActions(
                DeliveryReadinessState.NeedsRefinement));
        Assert.DoesNotContain(
            DeliveryReadinessAction.Accept,
            DeliveryReadinessPolicy.AllowedActions(
                DeliveryReadinessState.NeedsCustomerWaiver));
        Assert.DoesNotContain(
            DeliveryReadinessAction.GrantWaiver,
            DeliveryReadinessPolicy.AllowedActions(DeliveryReadinessState.Blocked));
    }

    [Fact]
    public void DeliveryReadinessQa_VerifiedCriteriaRequireNoResponsibleRoles()
    {
        var plan = DeliveryReadinessFixtures.Plan();
        var planHash = DeliveryReadinessPolicy.HashAcceptancePlan(plan);
        var prompt =
            $"AcceptancePlanHash: {planHash}{Environment.NewLine}" +
            "- AC-001 (Observation): verify the result" + Environment.NewLine +
            "- EV-S010-001 [Observation; supportsVerification=true] host-observed verification";
        DeliveryEvidenceItem[] evidence =
        [
            new(
                "EV-S010-001",
                OutcomeEvidenceKind.Observation,
                "reviewed-preview",
                "host-observed verification",
                SupportsVerification: true,
                ExitCode: null,
                ResultDigest: string.Empty)
        ];

        var parsed = DeliveryReadinessPolicy.ParseQaOutput(
            DeliveryReadinessFixtures.QaBlockFromPrompt(prompt),
            plan,
            planHash,
            evidence);

        Assert.Equal(OutcomeQaVerdict.PASS, parsed.Document.Verdict);
        Assert.Empty(Assert.Single(parsed.Document.Criteria!).ResponsibleRoles!);

        var invalid = DeliveryReadinessFixtures.QaBlockFromPrompt(prompt)
            .Replace(
                "\"ResponsibleRoles\":[]",
                "\"ResponsibleRoles\":[\"external-delivery\"]",
                StringComparison.Ordinal);
        var exception = Assert.Throws<DeliveryReadinessContractException>(() =>
            DeliveryReadinessPolicy.ParseQaOutput(
                invalid,
                plan,
                planHash,
                evidence));
        Assert.Contains(
            "Verified outcome cannot name responsible roles",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DeliveryLifecycle_ApprovedIsUnreachableOutsideTheGuardedCompletion()
    {
        var lifecycle = new FlowLifecycleCoordinator();
        var flow = new FlowRun
        {
            Title = "Guarded delivery",
            OriginalRequest = "Guard the approval path.",
            Kind = FlowKind.Delivery,
            Status = FlowStatus.Running
        };

        Assert.Throws<FlowLifecycleException>(
            () => lifecycle.Transition(flow, FlowStatus.Approved));
        Assert.Throws<FlowLifecycleException>(
            () => lifecycle.OpenCustomerReview(
                flow,
                DeliveryReadinessState.NeedsRefinement,
                "sha256:" + new string('a', 64),
                "sha256:" + new string('a', 64)));
        Assert.Throws<FlowLifecycleException>(
            () => lifecycle.OpenCustomerReview(
                flow,
                DeliveryReadinessState.ReadyToApprove,
                "sha256:" + new string('a', 64),
                "sha256:" + new string('b', 64)));
        Assert.Throws<FlowLifecycleException>(
            () => lifecycle.CompletePublishedDelivery(
                flow,
                DeliveryReadinessState.ReadyToApprove,
                "sha256:" + new string('a', 64),
                "sha256:" + new string('a', 64),
                publicationVerified: false));

        Assert.True(lifecycle.OpenCustomerReview(
            flow,
            DeliveryReadinessState.ReadyToApprove,
            "sha256:" + new string('a', 64),
            "sha256:" + new string('a', 64)));
        Assert.Equal(FlowStatus.WaitingForFeedback, flow.Status);
        Assert.True(lifecycle.CompletePublishedDelivery(
            flow,
            DeliveryReadinessState.ReadyToApprove,
            "sha256:" + new string('a', 64),
            "sha256:" + new string('a', 64),
            publicationVerified: true));
        Assert.Equal(FlowStatus.Approved, flow.Status);
    }

    private static DeliveryReadinessCriterion Criterion(
        DeliveryCriterionOutcome outcome) =>
        new(
            "AC-001",
            "The result satisfies the confirmed brief.",
            outcome,
            ["EV-001"],
            "The host-observed check produced this result.",
            outcome == DeliveryCriterionOutcome.Verified ? null : "Fix and re-verify.",
            ["external-delivery"],
            true);

    private static DeliveryReadinessRisk Risk(
        DeliveryRiskClassification classification) =>
        new(
            "RR-001",
            classification,
            DeliveryRiskSeverity.Medium,
            "A disclosed residual risk remains.",
            "The customer may observe a bounded degradation.",
            ["EV-RISK"],
            [],
            "quality-engineer",
            Guid.NewGuid(),
            null);

    [Fact]
    public async Task DeliveryVerificationPrompt_CarriesHostComputedPlanHashCriteriaAndEvidence()
    {
        // Finding 1 and 5: the production prompt must carry the host-computed acceptance plan
        // hash, the planned criteria, and the host-issued evidence registry. The scripted agent
        // echoes them back, so the flow only reaches review when the host really injected them.
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Delivery);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var verification = Assert.Single(
            harness.Runner.Contexts,
            context => context.RequiresDeliveryReadinessQa);
        var injectedHash = DeliveryReadinessFixtures.PlanHashFromPrompt(
            verification.OutcomeContext);
        Assert.Equal(DeliveryReadinessFixtures.PlanHash(), injectedHash);
        Assert.Equal(
            ["AC-001"],
            DeliveryReadinessFixtures.CriterionIdsFromPrompt(verification.OutcomeContext));
        Assert.NotEmpty(
            DeliveryReadinessFixtures.EvidenceIdsFromPrompt(verification.OutcomeContext));
        Assert.Contains(
            DeliveryReadinessPolicy.QaBeginMarker,
            verification.OutcomeContract,
            StringComparison.Ordinal);
        Assert.Contains(
            injectedHash,
            verification.OutcomeContract,
            StringComparison.Ordinal);

        await using var database = await harness.Factory.CreateDbContextAsync();
        var flow = await harness.LoadFlowAsync();
        var registry = DeliveryReadinessService.KnownEvidenceIds(
            flow.Events,
            flow.Iteration);
        Assert.NotEmpty(registry);
        var readiness = await database.DeliveryReadinessSnapshots
            .SingleAsync(item => item.FlowRunId == harness.FlowId && item.Active);
        Assert.Equal(DeliveryReadinessState.ReadyToApprove, readiness.State);
        Assert.Equal(injectedHash, readiness.AcceptancePlanHash);
        var contract = DeliveryReadinessPolicy.DeserializeSnapshot(readiness.ContractJson);
        Assert.All(
            contract.Criteria,
            criterion => Assert.All(
                criterion.EvidenceIds,
                evidenceId => Assert.Contains(evidenceId, registry)));
    }

    [Fact]
    public async Task FabricatedEvidenceId_CannotReachReadyToApproveThroughTheEngine()
    {
        // Finding 5: an identifier the host never issued fails the verification turn closed, so it
        // can never become a verified criterion or a releasable readiness state.
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Delivery);
        harness.Runner.QaBlockOverride = context =>
            DeliveryReadinessFixtures.QaBlockFromPrompt(
                context.OutcomeContext,
                evidenceOverride: ["EV-S999-999"]);

        try
        {
            await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not Xunit.Sdk.XunitException)
        {
            // The turn fails closed; the assertions below prove nothing advanced.
        }

        var flow = await harness.LoadFlowAsync();
        Assert.DoesNotContain(
            flow.GateRecords,
            gate => gate.ActionType is HandoffActionType.CustomerReview
                or HandoffActionType.CustomerWaiver);
        Assert.NotEqual(FlowStatus.Approved, flow.Status);
        Assert.Equal(0, harness.CandidatePublisher.Calls);
        await using var database = await harness.Factory.CreateDbContextAsync();
        Assert.DoesNotContain(
            await database.DeliveryReadinessSnapshots
                .Where(item => item.FlowRunId == harness.FlowId)
                .ToListAsync(),
            item => item.State == DeliveryReadinessState.ReadyToApprove);
    }

    [Fact]
    public async Task DeliveryReview_WithoutTheImmutableBinding_ReturnsAStaleConflict()
    {
        // Finding 4: an omitted binding value is a stale tab, not permission to skip the check.
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Delivery);
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var flow = await harness.LoadFlowAsync();
        var review = Assert.Single(
            flow.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);

        var conflict = await Assert.ThrowsAsync<DeliveryReadinessConflictException>(
            () => harness.Reviews.ReviewAsync(
                harness.FlowId,
                new DirectReviewRequest
                {
                    GateId = review.Id,
                    Intent = ReviewIntent.Accept
                }));

        Assert.Equal(DeliveryReadinessConflicts.ReviewStale, conflict.Code);
        Assert.Equal(DeliveryReadinessState.ReadyToApprove, conflict.State);
        Assert.NotNull(conflict.Revision);
        Assert.False(
            (await harness.LoadFlowAsync()).GateRecords.Single(gate =>
                gate.ActionType == HandoffActionType.CustomerReview).Resolved);
        Assert.Equal(0, harness.CandidatePublisher.Calls);
    }

    [Fact]
    public async Task NeedsRefinement_ResolvesThroughTheTypedRefinementPathIntoANewIteration()
    {
        // Finding 3: NeedsRefinement is not a dead end, and its resolution never accepts anything.
        // The budget is disabled here so the flow parks on the customer immediately; in production
        // this same path is reached once host-owned refinement is spent.
        await using var harness = await ReviewHarness.CreateAsync(
            FlowKind.Delivery,
            maxAutoRefinementIterations: 0);
        harness.Runner.QaBlockOverride = context =>
            DeliveryReadinessFixtures.QaBlockFromPrompt(
                context.OutcomeContext,
                outcome: DeliveryCriterionOutcome.Failed);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var (candidateId, revision, hash) = await harness.ReadinessBindingAsync();
        var blocked = await Assert.ThrowsAsync<DeliveryReadinessConflictException>(
            () => harness.Reviews.ResolveReadinessAsync(
                harness.FlowId,
                new ReadinessResolutionRequest
                {
                    ReviewedCandidateId = candidateId,
                    ReadinessRevision = revision,
                    ReadinessContractHash = hash,
                    Action = ReadinessResolutionAction.Continue
                }));
        Assert.Equal(DeliveryReadinessConflicts.NotReady, blocked.Code);

        var resolved = await harness.Reviews.ResolveReadinessAsync(
            harness.FlowId,
            new ReadinessResolutionRequest
            {
                ReviewedCandidateId = candidateId,
                ReadinessRevision = revision,
                ReadinessContractHash = hash,
                Action = ReadinessResolutionAction.RequestRefinement,
                Refinement = new DirectReviewRefinement
                {
                    RequestedChanges = ["Make the archive error state distinguishable."]
                }
            });

        Assert.Equal(DeliveryReadinessState.NeedsRefinement, resolved.ResolvedFrom);
        Assert.Equal(2, resolved.Iteration);
        var flow = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Reworking, flow.Status);
        Assert.Equal(2, flow.Iteration);
        Assert.Null(flow.CurrentBlockerCode);
        Assert.DoesNotContain(
            flow.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);
        Assert.Equal(0, harness.CandidatePublisher.Calls);
        await using var database = await harness.Factory.CreateDbContextAsync();
        Assert.Empty(await database.DeliveryReadinessSnapshots
            .Where(item => item.FlowRunId == harness.FlowId && item.Active)
            .ToListAsync());
    }

    [Fact]
    public async Task Blocked_ResolvesThroughContinueWithoutAnyAcceptOrWaiverBypass()
    {
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Delivery);
        harness.Runner.QaBlockOverride = context =>
            DeliveryReadinessFixtures.QaBlockFromPrompt(
                context.OutcomeContext,
                outcome: DeliveryCriterionOutcome.Blocked);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        Assert.Equal(FlowStatus.Blocked, (await harness.LoadFlowAsync()).Status);

        var (candidateId, revision, hash) = await harness.ReadinessBindingAsync();
        var refinementConflict = await Assert.ThrowsAsync<DeliveryReadinessConflictException>(
            () => harness.Reviews.ResolveReadinessAsync(
                harness.FlowId,
                new ReadinessResolutionRequest
                {
                    ReviewedCandidateId = candidateId,
                    ReadinessRevision = revision,
                    ReadinessContractHash = hash,
                    Action = ReadinessResolutionAction.RequestRefinement,
                    Refinement = new DirectReviewRefinement
                    {
                        RequestedChanges = ["Unblock the release."]
                    }
                }));
        Assert.Equal(DeliveryReadinessConflicts.NotReady, refinementConflict.Code);

        var resolved = await harness.Reviews.ResolveReadinessAsync(
            harness.FlowId,
            new ReadinessResolutionRequest
            {
                ReviewedCandidateId = candidateId,
                ReadinessRevision = revision,
                ReadinessContractHash = hash,
                Action = ReadinessResolutionAction.Continue
            });

        Assert.Equal(DeliveryReadinessState.Blocked, resolved.ResolvedFrom);
        var flow = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Queued, flow.Status);
        Assert.Equal(1, flow.Iteration);
        Assert.Null(flow.CurrentBlockerCode);
        Assert.DoesNotContain(
            flow.GateRecords,
            gate => gate.ActionType is HandoffActionType.CustomerReview
                or HandoffActionType.CustomerWaiver);
        Assert.Equal(0, harness.CandidatePublisher.Calls);
        Assert.Contains(
            flow.Events,
            item => item.Type == "delivery.readiness-resolved");
        var continuation = Assert.Single(
            flow.Steps,
            step =>
                step.Status == StepStatus.Pending &&
                step.IsOutcomeOwner &&
                step.PlanStepKey == "prepare");

        harness.Runner.QaBlockOverride = context =>
            DeliveryReadinessFixtures.QaBlockFromPrompt(context.OutcomeContext);
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var completed = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.WaitingForFeedback, completed.Status);
        Assert.Equal(StepStatus.Completed, completed.Steps.Single(
            step => step.Id == continuation.Id).Status);
        Assert.Equal(
            continuation.Id,
            ReviewedCandidateLedger.Read(completed).OutcomeOwnerStepId);
        await using var database = await harness.Factory.CreateDbContextAsync();
        var active = await database.DeliveryReadinessSnapshots.SingleAsync(
            item => item.FlowRunId == harness.FlowId && item.Active);
        Assert.Equal(2, active.Revision);
        Assert.Equal(DeliveryReadinessState.ReadyToApprove, active.State);
    }

    [Fact]
    public async Task CompletedPublicationBeforeApproval_RecoversThroughTheGuardedCompletion()
    {
        // Finding 2: a crash between a completed publication and final approval must reauthorize
        // rather than transition directly, and must deny safely when the binding no longer agrees.
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Delivery);
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var beforeReview = await harness.LoadFlowAsync();
        var review = Assert.Single(
            beforeReview.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);
        await harness.ReviewAsync(new DirectReviewRequest
        {
            GateId = review.Id,
            Intent = ReviewIntent.Accept
        });
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        Assert.Equal(FlowStatus.Approved, (await harness.LoadFlowAsync()).Status);

        // Rewind to the exact crash window: publication completed, flow not yet Approved.
        await using (var database = await harness.Factory.CreateDbContextAsync())
        {
            var stored = await database.Flows.SingleAsync(
                item => item.Id == harness.FlowId);
            stored.Status = FlowStatus.Queued;
            stored.CompletedAt = null;
            await database.SaveChangesAsync();
        }

        var recovered = await harness.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);
        Assert.DoesNotContain(harness.FlowId, recovered);
        var afterRecovery = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Approved, afterRecovery.Status);
        Assert.Contains(
            afterRecovery.Events,
            item => item.Type == "flow.recovery-publication-completed");

        // Now break the binding and rewind again: recovery must refuse to approve.
        await using (var database = await harness.Factory.CreateDbContextAsync())
        {
            var stored = await database.Flows.SingleAsync(
                item => item.Id == harness.FlowId);
            stored.Status = FlowStatus.Queued;
            stored.CompletedAt = null;
            var readiness = await database.DeliveryReadinessSnapshots
                .SingleAsync(item => item.FlowRunId == harness.FlowId && item.Active);
            readiness.State = DeliveryReadinessState.NeedsRefinement;
            await database.SaveChangesAsync();
        }

        _ = await harness.Engine.RecoverInterruptedFlowsAsync(CancellationToken.None);
        var denied = await harness.LoadFlowAsync();
        Assert.NotEqual(FlowStatus.Approved, denied.Status);
        Assert.Contains(
            denied.Events,
            item => item.Type == DeliveryReadinessService.DeniedEventType);
    }

    [Fact]
    public async Task GrantedWaiverReceipts_SurviveReDerivationIntoProjectionsAndPublication()
    {
        // Receipts are written against the pre-waiver assessment and the grant deliberately
        // re-derives a new revision. They must still be enumerated by the projection, the binding,
        // and the publication waiver-set hash rather than being orphaned by the new contract hash.
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Delivery);
        harness.Runner.QaBlockOverride = context =>
            DeliveryReadinessFixtures.QaBlockFromPrompt(
                context.OutcomeContext,
                risks: [("RR-001", DeliveryRiskClassification.WaiverRequired)]);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var beforeWaiver = await harness.LoadFlowAsync();
        var waiverGate = Assert.Single(
            beforeWaiver.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerWaiver);
        var (candidateId, revision, hash) = await harness.ReadinessBindingAsync();
        var emptyWaiverSetHash = DeliveryReadinessPolicy.HashWaiverSet(hash, []);

        await harness.Reviews.GrantReadinessWaiverAsync(
            harness.FlowId,
            new ReadinessWaiverRequest
            {
                GateId = waiverGate.Id,
                ReviewedCandidateId = candidateId,
                ReadinessRevision = revision,
                ReadinessContractHash = hash,
                RiskIds = ["RR-001"],
                Acknowledgement = "I acknowledge and accept the disclosed residual risk."
            });

        var afterWaiver = await harness.LoadFlowAsync();
        await using (var database = await harness.Factory.CreateDbContextAsync())
        {
            var binding = await new DeliveryReadinessService().LoadCurrentAsync(
                database,
                harness.FlowId);
            Assert.NotNull(binding);
            Assert.Equal(DeliveryReadinessState.ReadyToApprove, binding!.State);
            Assert.NotEqual(hash, binding.ContractHash);

            // The receipt is still bound to the re-derived assessment.
            Assert.Equal(["RR-001"], binding.Waivers.Select(item => item.RiskId));
            Assert.NotEqual(emptyWaiverSetHash, binding.WaiverSetHash);
            Assert.Equal(
                DeliveryReadinessPolicy.HashWaiverSet(
                    binding.ContractHash,
                    ["RR-001"]),
                binding.WaiverSetHash);
            Assert.NotEqual(
                DeliveryReadinessPolicy.HashWaiverSet(binding.ContractHash, []),
                binding.WaiverSetHash);

            var dto = ApiMappings.ToDeliveryReadinessDto(binding, afterWaiver);
            Assert.Equal(["RR-001"], dto.GrantedWaiverRiskIds);
            Assert.True(Assert.Single(dto.Risks).Waived);
        }

        // Acceptance and publication carry the same non-empty waiver-set identity.
        var review = Assert.Single(
            afterWaiver.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);
        await harness.ReviewAsync(new DirectReviewRequest
        {
            GateId = review.Id,
            Intent = ReviewIntent.Accept
        });
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        await using var verification = await harness.Factory.CreateDbContextAsync();
        var acceptanceEvent = Assert.Single(
            await verification.FlowEvents
                .Where(item =>
                    item.FlowRunId == harness.FlowId &&
                    item.Type == "flow.approved-publication-queued")
                .ToListAsync());
        Assert.Contains("\"WaiverSetHash\"", acceptanceEvent.DataJson);
        Assert.DoesNotContain(emptyWaiverSetHash, acceptanceEvent.DataJson);
        Assert.Contains(
            DeliveryReadinessPolicy.HashWaiverSet(
                (await verification.DeliveryReadinessSnapshots
                    .SingleAsync(item =>
                        item.FlowRunId == harness.FlowId && item.Active)).ContractHash,
                ["RR-001"]),
            acceptanceEvent.DataJson);
    }

    [Fact]
    public async Task OrdinaryReviewRefinement_SupersedesTheReleasableReadinessAndCandidate()
    {
        // A refined iteration invalidates the reviewed result. Leaving the ReadyToApprove rows
        // active would keep painting a green card, history row, and readiness panel.
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Delivery);
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var beforeRefinement = await harness.LoadFlowAsync();
        var review = Assert.Single(
            beforeRefinement.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);
        await using (var database = await harness.Factory.CreateDbContextAsync())
        {
            Assert.Equal(
                DeliveryReadinessState.ReadyToApprove,
                (await database.DeliveryReadinessSnapshots
                    .SingleAsync(item =>
                        item.FlowRunId == harness.FlowId && item.Active)).State);
        }

        await harness.ReviewAsync(new DirectReviewRequest
        {
            GateId = review.Id,
            Intent = ReviewIntent.RequestRefinement,
            Refinement = new DirectReviewRefinement
            {
                RequestedChanges = ["Narrow the result to checkout resilience."]
            }
        });

        var flow = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Reworking, flow.Status);
        Assert.Equal(2, flow.Iteration);

        await using var verification = await harness.Factory.CreateDbContextAsync();
        Assert.Contains(
            flow.Events,
            item => item.Type == DeliveryReadinessService.SupersededEventType);
        Assert.Empty(await verification.DeliveryReadinessSnapshots
            .Where(item => item.FlowRunId == harness.FlowId && item.Active)
            .ToListAsync());
        Assert.Empty(await verification.ReviewedCandidateRecords
            .Where(item => item.FlowRunId == harness.FlowId && item.Active)
            .ToListAsync());
        var superseded = Assert.Single(
            await verification.DeliveryReadinessSnapshots
                .Where(item => item.FlowRunId == harness.FlowId)
                .ToListAsync());
        Assert.False(superseded.Active);
        Assert.NotNull(superseded.SupersededAt);

        // Every projection stops advertising a releasable result.
        Assert.Null(await DemoApi.LoadReadinessDtoAsync(
            harness.Factory,
            new DeliveryReadinessService(),
            flow,
            CancellationToken.None));
        var labels = await DemoApi.LoadReadinessLabelsAsync(
            verification,
            [harness.FlowId],
            CancellationToken.None);
        Assert.Empty(labels);
        Assert.Equal(0, harness.CandidatePublisher.Calls);
    }

    private sealed class ReviewHarness : IAsyncDisposable
    {
        private ReviewHarness(
            string root,
            string workspacePath,
            Guid flowId,
            ReviewDbContextFactory factory,
            WorkflowDefinitionProvider workflowProvider,
            HandoffGateEngine gate,
            ReviewAgentRunner runner,
            RecordingPublicationVerifier publicationVerifier,
            RecordingCandidatePublisher candidatePublisher,
            RecordingReviewedCandidateService reviewedCandidates,
            WorkflowEngine engine,
            ReviewCoordinator reviews,
            FlowQueue queue,
            RecordingDemoRuntimeRevoker demoRevoker)
        {
            Root = root;
            WorkspacePath = workspacePath;
            FlowId = flowId;
            Factory = factory;
            WorkflowProvider = workflowProvider;
            Gate = gate;
            Runner = runner;
            PublicationVerifier = publicationVerifier;
            CandidatePublisher = candidatePublisher;
            ReviewedCandidates = reviewedCandidates;
            Engine = engine;
            Reviews = reviews;
            Queue = queue;
            DemoRevoker = demoRevoker;
        }

        public string Root { get; }

        public string WorkspacePath { get; }

        public Guid FlowId { get; }

        public ReviewDbContextFactory Factory { get; }

        public WorkflowDefinitionProvider WorkflowProvider { get; }

        public HandoffGateEngine Gate { get; }

        public ReviewAgentRunner Runner { get; }

        public RecordingPublicationVerifier PublicationVerifier { get; }

        public RecordingCandidatePublisher CandidatePublisher { get; }

        public RecordingReviewedCandidateService ReviewedCandidates { get; }

        public WorkflowEngine Engine { get; }

        public ReviewCoordinator Reviews { get; }

        public FlowQueue Queue { get; }

        public RecordingDemoRuntimeRevoker DemoRevoker { get; }

        public static async Task<ReviewHarness> CreateAsync(
            FlowKind kind,
            RecordingPublicationVerifier? publicationVerifier = null,
            int maxAutoRefinementIterations = 3)
        {
            var root = Path.Combine(
                AppContext.BaseDirectory,
                "review-workflow-tests",
                Guid.NewGuid().ToString("N"));
            var agentsDirectory = Path.Combine(root, ".github", "agents");
            var workspacePath = Path.Combine(root, "workspace");
            Directory.CreateDirectory(agentsDirectory);
            Directory.CreateDirectory(workspacePath);
            Directory.CreateDirectory(Path.Combine(workspacePath, ".git"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "WORKFLOW.md"),
                """
                ---
                workspace:
                  root: workspace
                agent:
                  max_concurrent_agents: 1
                  max_attempts: 1
                studio:
                  planning:
                    max_steps: 24
                    max_dependencies_per_step: 8
                    max_assignment_characters: 4000
                  flow_kinds:
                    advisory:
                      required_duties:
                        - PrepareOutcome
                      maximum_permission: ReadOnlySource
                    delivery:
                      required_duties:
                        - Implement
                        - Verify
                        - PrepareOutcome
                        - Publish
                      pre_review_maximum_permission: WorkspaceWrite
                      post_approval_maximum_permission: Publish
                      max_auto_refinement_iterations: {{MAX_AUTO_REFINEMENT}}
                  advisory:
                    artifact_directory: .studio\advisory
                    max_artifact_count: 8
                    max_total_artifact_bytes: 65536
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

                Test {{ agent.name }} on {{ task }}.
                """.Replace(
                    "{{MAX_AUTO_REFINEMENT}}",
                    maxAutoRefinementIterations.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    StringComparison.Ordinal));

            var plan = kind == FlowKind.Advisory
                ? AdvisoryPlan()
                : DeliveryPlan();
            var flow = new FlowRun
            {
                Title = "Generic customer review",
                OriginalRequest = "Produce a customer-reviewable result.",
                ConsolidatedRequest = "Produce a customer-reviewable result.",
                Kind = kind,
                Status = FlowStatus.Queued,
                RepositoryPath = root,
                RepositoryKnowledge = "A configured test repository.",
                Outcome = kind == FlowKind.Advisory
                    ? OutcomeType.None
                    : OutcomeType.PullRequest
            };
            var snapshots = new List<FlowAgentSnapshot>
            {
                Snapshot(flow.Id, "account-manager", "Account Manager", "account-manager"),
                Snapshot(flow.Id, "team-lead", "Team Lead", "team-lead")
            };
            if (kind == FlowKind.Advisory)
            {
                snapshots.Add(Snapshot(
                    flow.Id,
                    "insight-specialist",
                    "Insight Specialist",
                    "external-advisor"));
            }
            else
            {
                snapshots.Add(Snapshot(
                    flow.Id,
                    "outcome-crafter",
                    "Outcome Crafter",
                    "external-delivery"));
                snapshots.Add(Snapshot(
                    flow.Id,
                    "sky-publisher",
                    "Sky Publisher",
                    "external-publisher"));
            }

            var databasePath = Path.Combine(root, "harness.db");
            var options = new DbContextOptionsBuilder<HarnessDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            var factory = new ReviewDbContextFactory(options);
            await using (var database = await factory.CreateDbContextAsync())
            {
                await database.Database.EnsureCreatedAsync();
                database.Settings.Add(new HarnessSettings
                {
                    RepositoryPath = root,
                    RepositoryKnowledge = "A configured test repository.",
                    MaxHandoffRetries = 0
                });
                database.Flows.Add(flow);
                database.FlowAgentSnapshots.AddRange(snapshots);
                await database.SaveChangesAsync();
            }

            var paths = new HarnessPaths(root, agentsDirectory, databasePath);
            var workflowProvider = new WorkflowDefinitionProvider(
                paths,
                new AiHarnessDemo.Core.Workflow.WorkflowLoader(),
                NullLogger<WorkflowDefinitionProvider>.Instance);
            _ = workflowProvider.GetEffective();
            var catalog = new AgentCatalog(paths, factory);
            var snapshotService = new FlowAgentSnapshotService(factory, catalog);
            var runner = new ReviewAgentRunner(WrapPlan(plan));
            var gate = new HandoffGateEngine();
            gate.SetTrustLevel(HandoffActionType.Advance, HandoffTrustLevel.Auto);
            gate.SetTrustLevel(
                HandoffActionType.RequestRevision,
                HandoffTrustLevel.Auto);
            gate.SetTrustLevel(
                HandoffActionType.CustomerReview,
                HandoffTrustLevel.Gated);
            var verifier = publicationVerifier ??
                           new RecordingPublicationVerifier();
            var candidatePublisher = new RecordingCandidatePublisher();
            var reviewedCandidates = new RecordingReviewedCandidateService();
            var flowQueue = new FlowQueue();
            var lifecycle = new FlowLifecycleCoordinator();
            var demoRevoker = new RecordingDemoRuntimeRevoker(flowQueue);
            var engine = new WorkflowEngine(
                factory,
                catalog,
                new FixedModelRouter(),
                new BootstrapTaskProfileFactory(),
                TestRoutingSupport.Recorder(factory),
                new ReviewWorkspaceManager(workspacePath),
                runner,
                gate,
                new CopilotSessionJournal(),
                workflowProvider,
                NullLogger<WorkflowEngine>.Instance,
                publicationVerifier: verifier,
                candidatePublisher: candidatePublisher,
                flowAgentSnapshotService: snapshotService,
                teamPlanValidator: new TeamPlanValidator(),
                reviewedCandidateService: reviewedCandidates,
                flowQueue: flowQueue);
            var reviews = new ReviewCoordinator(
                factory,
                gate,
                flowQueue,
                lifecycle,
                workflowProvider,
                demoRuntimeRevoker: demoRevoker);
            return new ReviewHarness(
                root,
                workspacePath,
                flow.Id,
                factory,
                workflowProvider,
                gate,
                runner,
                verifier,
                candidatePublisher,
                reviewedCandidates,
                engine,
                reviews,
                flowQueue,
                demoRevoker);
        }

        public async Task<FlowRun> LoadFlowAsync()
        {
            await using var database = await Factory.CreateDbContextAsync();
            return await database.Flows
                .AsSplitQuery()
                .AsNoTracking()
                .Include(item => item.Steps)
                .Include(item => item.GateRecords)
                .Include(item => item.Events)
                .Include(item => item.Messages)
                .Include(item => item.AgentSnapshots)
                .Include(item => item.PlanDocuments)
                .Include(item => item.TaskProfiles)
                .SingleAsync(item => item.Id == FlowId);
        }

        public sealed class RecordingDemoRuntimeRevoker(FlowQueue queue)
            : IDemoRuntimeRevoker
        {
            public List<Guid> FlowIds { get; } = [];
            public List<bool> QueuePendingAtRevoke { get; } = [];
            public List<bool> CancellationCanBeCanceled { get; } = [];

            public Task RevokeFlowAsync(
                Guid flowId,
                string reason,
                CancellationToken cancellationToken = default)
            {
                FlowIds.Add(flowId);
                QueuePendingAtRevoke.Add(queue.PendingCount > 0);
                CancellationCanBeCanceled.Add(
                    cancellationToken.CanBeCanceled);
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Submits a review with the current immutable readiness binding attached. A Delivery
        /// readiness flow rejects a request that omits it, so tests must echo the server's values
        /// exactly as a browser does.
        /// </summary>
        public async Task<ReviewCoordinationResult> ReviewAsync(
            DirectReviewRequest request)
        {
            await using var database = await Factory.CreateDbContextAsync();
            var readiness = await database.DeliveryReadinessSnapshots
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.FlowRunId == FlowId && item.Active);
            var candidate = await database.ReviewedCandidateRecords
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.FlowRunId == FlowId && item.Active);
            var bound = readiness is null || candidate is null
                ? request
                : new DirectReviewRequest
                {
                    GateId = request.GateId,
                    Intent = request.Intent,
                    Refinement = request.Refinement,
                    ReviewedCandidateId = candidate.Id,
                    ReadinessRevision = readiness.Revision,
                    ReadinessContractHash = readiness.ContractHash
                };
            return await Reviews.ReviewAsync(FlowId, bound);
        }

        /// <summary>The current immutable readiness binding a client must echo back.</summary>
        public async Task<(Guid CandidateId, int Revision, string ContractHash)>
            ReadinessBindingAsync()
        {
            await using var database = await Factory.CreateDbContextAsync();
            var readiness = await database.DeliveryReadinessSnapshots
                .AsNoTracking()
                .SingleAsync(item => item.FlowRunId == FlowId && item.Active);
            var candidate = await database.ReviewedCandidateRecords
                .AsNoTracking()
                .SingleAsync(item => item.FlowRunId == FlowId && item.Active);
            return (candidate.Id, readiness.Revision, readiness.ContractHash);
        }

        public async Task SetPostApprovalMaximumAsync(
            ExecutionPermissionProfile maximum)
        {
            var path = Path.Combine(Root, "WORKFLOW.md");
            var current = await File.ReadAllTextAsync(path);
            const string marker =
                "post_approval_maximum_permission: Publish";
            if (!current.Contains(marker, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The review fixture has no Publish post-approval ceiling to replace.");
            }
            await File.WriteAllTextAsync(
                path,
                current.Replace(
                    marker,
                    "post_approval_maximum_permission: " +
                    maximum,
                    StringComparison.Ordinal));
            await WorkflowProvider.ReloadAsync();
        }

        public async Task InvalidateCurrentWorkflowAsync()
        {
            await File.WriteAllTextAsync(
                Path.Combine(Root, "WORKFLOW.md"),
                """
                ---
                studio:
                  invalid_field: true
                ---
                Invalid current workflow.
                """);
            await WorkflowProvider.ReloadAsync();
        }

        public ValueTask DisposeAsync()
        {
            WorkflowProvider.Dispose();
            Gate.Dispose();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // SQLite can briefly retain a handle on Windows.
            }
            return ValueTask.CompletedTask;
        }

        private static TeamPlanDocument AdvisoryPlan() =>
            new()
            {
                Disposition = TeamPlanDisposition.Planned,
                Steps =
                [
                    Step(
                        "prepare",
                        "insight-specialist",
                        10,
                        PlanStage.BeforeReview,
                        [PlanDuty.Analyze, PlanDuty.PrepareOutcome],
                        outcomeOwner: true)
                ],
                PreMortemCheckpoints = [],
                MissingQualification = null
            };

        private static TeamPlanDocument DeliveryPlan() =>
            new()
            {
                Disposition = TeamPlanDisposition.Planned,
                Steps =
                [
                    Step(
                        "implement",
                        "outcome-crafter",
                        10,
                        PlanStage.BeforeReview,
                        [PlanDuty.Implement],
                        outcomeOwner: false),
                    Step(
                        "prepare",
                        "outcome-crafter",
                        20,
                        PlanStage.BeforeReview,
                        [
                            PlanDuty.Verify,
                            PlanDuty.PrepareOutcome
                        ],
                        outcomeOwner: true,
                        dependsOn: ["implement"]),
                    Step(
                        "publish",
                        "sky-publisher",
                        30,
                        PlanStage.AfterApproval,
                        [PlanDuty.Publish],
                        outcomeOwner: false,
                        dependsOn: ["prepare"])
                ],
                PreMortemCheckpoints = [],
                AcceptanceCriteria = DeliveryReadinessFixtures.Criteria(),
                MissingQualification = null
            };

        private static TeamPlanStep Step(
            string id,
            string agentId,
            int order,
            PlanStage stage,
            IReadOnlyList<PlanDuty> duties,
            bool outcomeOwner,
            IReadOnlyList<string>? dependsOn = null) =>
            new()
            {
                Id = id,
                AgentId = agentId,
                Order = order,
                Stage = stage,
                Assignment = $"Complete {id}.",
                Justification = $"{agentId} owns {id}.",
                DependsOn = dependsOn ?? [],
                Duties = duties,
                OutcomeOwner = outcomeOwner,
                TaskProfile = stage == PlanStage.AfterApproval
                    ? new TeamPlanTaskProfile()
                    : new TeamPlanTaskProfile
                    {
                        Complexity = 5,
                        ReasoningDepth = 5,
                        ContextDemand = 5,
                        ToolIntensity = 3,
                        TaskTypeTags = [TaskTypeTag.CrossCutting],
                        Risk = TaskRisk.Medium,
                        RiskReason = "The result must be reviewed.",
                        Confidence = 0.8,
                        Rationales = ["A focused fixture profile is sufficient."]
                    }
            };

        private static FlowAgentSnapshot Snapshot(
            Guid flowId,
            string id,
            string name,
            string role) =>
            new()
            {
                FlowRunId = flowId,
                AgentId = id,
                Name = name,
                Description = $"Description for {name}.",
                Role = role,
                Instructions = "Complete the assigned work.",
                DefinitionHash = $"hash-{id}",
                EnabledAtSnapshot = true,
                Required = id is "account-manager" or "team-lead",
                Switchable = id is not ("account-manager" or "team-lead"),
                SourceFileName = $"{id}.agent.md"
            };

        private static string WrapPlan(TeamPlanDocument plan) =>
            $"{TeamPlanParser.BeginSentinel}{Environment.NewLine}" +
            TeamPlanParser.Serialize(plan) +
            $"{Environment.NewLine}{TeamPlanParser.EndSentinel}";
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        string failureMessage)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        while (!condition())
        {
            try
            {
                await Task.Delay(10, timeout.Token);
            }
            catch (OperationCanceledException) when (
                timeout.IsCancellationRequested)
            {
                throw new TimeoutException(failureMessage);
            }
        }
    }

    private sealed class ReviewAgentRunner(string plan) : IAgentRunner
    {
        public List<AgentExecutionContext> Contexts { get; } = [];

        public string? PublicationOutputOverride { get; set; }

        /// <summary>Optional strict outcome-QA block for a scripted readiness case.</summary>
        public Func<AgentExecutionContext, string>? QaBlockOverride { get; set; }

        public Func<AgentExecutionContext, IReadOnlyList<ToolCallRecord>>?
            VerificationToolCallsOverride { get; set; }

        public Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            if (context.AgentId == "sky-publisher" &&
                PublicationOutputOverride is not null)
            {
                return Task.FromResult(new AgentExecutionResult(
                    PublicationOutputOverride,
                    "Fixture publication handoff.",
                    1,
                    []));
            }
            var output = context.AgentId switch
            {
                "team-lead" =>
                    $"HANDOFF_STATUS: COMPLETE{Environment.NewLine}{plan}",
                "account-manager" => $$$"""
                  HANDOFF_STATUS: COMPLETE
                  {{{IntakeParser.BeginSentinel}}}
                  {"Status":"Confirmed","FlowKind":"{{{(context.Outcome == OutcomeType.None ? "Advisory" : "Delivery")}}}","TaskTitle":"Refine customer result","CustomerReply":"I normalized the requested refinement for the team.","Brief":{"Goal":"Focus on checkout resilience.","Details":["Exclude account services.","Add operational trade-offs."],"SuccessCriteria":["The revised result addresses the requested focus."],"Constraints":[],"Assumptions":[]}}
                  {{{IntakeParser.EndSentinel}}}
                  """,
                "product-manager" => """
                  The captured Product Manager reviewed the complete execution ledger.

                  REWORK_TARGET_ROLES: NONE
                  """,
                _ => """
                  HANDOFF_STATUS: COMPLETE

                  ## Decision
                  Complete.

                  ## Deliverable
                  The assigned result is complete.

                  ## Evidence
                  Focused fixture evidence was inspected.

                  ## Next owner
                  Continue the accepted plan.
                  """
            };
            if (context.OutcomeContract.Contains(
                    FlowOutcomeParser.BeginSentinel,
                    StringComparison.Ordinal))
            {
                output +=
                    Environment.NewLine +
                    FlowOutcomeParser.BeginSentinel +
                    Environment.NewLine +
                    """
                    {"Goal":"Produce a customer-reviewable result.","Summary":"The requested result is ready for customer review.","ImplementationDetails":["The assigned evidence was consolidated into this result."],"Artifacts":[]}
                    """ +
                    Environment.NewLine +
                    FlowOutcomeParser.EndSentinel;
            }
            if (context.RequiresDeliveryReadinessQa)
            {
                // The block is built from the host-rendered prompt, so it is only valid when the
                // production path actually injected the plan hash, criteria, and evidence ids.
                output +=
                    Environment.NewLine +
                    (QaBlockOverride?.Invoke(context) ??
                     DeliveryReadinessFixtures.QaBlockFromPrompt(
                         context.OutcomeContext));
            }
            if (context.InvocationKind == ExecutionInvocationKind.Publication)
            {
                output +=
                    Environment.NewLine +
                    RepositoryKnowledgeSynthesizer.RecapBeginSentinel +
                    Environment.NewLine +
                    """{"Changed":false,"Reason":"The fixture publication does not alter durable repository knowledge.","Knowledge":null}""" +
                    Environment.NewLine +
                    RepositoryKnowledgeSynthesizer.RecapEndSentinel;
            }
            IReadOnlyList<ToolCallRecord> toolCalls =
                context.PlanStepKey == "implement"
                    ?
                    [
                        new ToolCallRecord(
                            "observe",
                            "Host-observed fixture verification.",
                            Succeeded: true,
                            ToolType: "Observation",
                            ResultDigest:
                                OutcomeVerificationRules.ComputeSha256(
                                    "review-workflow-observation"),
                            ResultSummary:
                                "The customer-visible behavior was observed.")
                    ]
                    : [];
            if (context.RequiresDeliveryReadinessQa && VerificationToolCallsOverride is not null)
            {
                toolCalls = VerificationToolCallsOverride(context);
            }
            return Task.FromResult(new AgentExecutionResult(
                output,
                "Fixture evidence.",
                1,
                toolCalls));
        }
    }

    private sealed class RecordingPublicationVerifier(int failuresBeforeSuccess = 0)
        : IPublishedOutcomeVerifier
    {
        private int _failuresRemaining = failuresBeforeSuccess;

        public int Calls { get; private set; }

        public Task<PublishedOutcome> VerifyAsync(
            FlowRun flow,
            string releaseOutput,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (_failuresRemaining-- > 0)
            {
                throw new InvalidOperationException(
                    "Fixture publication verification failed.");
            }
            return Task.FromResult(new PublishedOutcome(
                "https://github.com/example/repository/pull/42",
                "Pull request #42"));
        }
    }

    private sealed class RecordingCandidatePublisher
        : IVerifiedCandidatePublisher
    {
        public int Calls { get; private set; }

        public Task<string> PublishAsync(
            FlowRun flow,
            Guid publicationStepId,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(
                "Reviewed candidate: sha256:" + new string('1', 64) +
                Environment.NewLine +
                "https://github.com/example/repository/pull/42");
        }
    }

    private sealed class RecordingReviewedCandidateService
        : IReviewedCandidateService
    {
        public int SealCalls { get; private set; }

        public int VerifyCalls { get; private set; }

        public bool FailVerification { get; set; }

        public Task<ReviewedCandidateIdentity> SealAsync(
            FlowRun flow,
            Guid outcomeOwnerStepId,
            string outcomeOwnerPlanStepKey,
            string outcomeContractJson,
            CancellationToken cancellationToken = default)
        {
            SealCalls++;
            var identity = Identity(
                flow,
                outcomeOwnerStepId,
                outcomeOwnerPlanStepKey,
                outcomeContractJson);
            return Task.FromResult(identity);
        }

        public Task<OutcomeCandidateSnapshot> VerifyAsync(
            FlowRun flow,
            ReviewedCandidateIdentity identity,
            CancellationToken cancellationToken = default)
        {
            VerifyCalls++;
            if (FailVerification)
            {
                throw new CandidateValidationException(
                    "Fixture detected reviewed candidate drift.");
            }
            ReviewedCandidateLedger.ValidateForFlow(flow, identity);
            return Task.FromResult(new OutcomeCandidateSnapshot(
                new CandidateManifest(
                    flow.Iteration,
                    identity.AcceptancePlanHash,
                    identity.Repositories.Select(repository =>
                        new CandidateRepositoryManifest(
                            repository.RelativePath,
                            repository.Head,
                            repository.Tree,
                            repository.RemoteRepository)).ToArray(),
                    [],
                    []),
                identity.Fingerprint,
                identity.OutcomeOwnerStepId,
                identity.SealedAt));
        }

        private static ReviewedCandidateIdentity Identity(
            FlowRun flow,
            Guid outcomeOwnerStepId,
            string outcomeOwnerPlanStepKey,
            string outcomeContractJson) =>
            new(
                flow.Id,
                flow.Iteration,
                outcomeOwnerStepId,
                outcomeOwnerPlanStepKey,
                OutcomeVerificationRules.ComputeSha256(outcomeContractJson),
                OutcomeVerificationRules.ComputeSha256(
                    $"fixture-plan:{flow.Id:D}:{flow.Iteration}:{outcomeOwnerStepId:D}"),
                OutcomeVerificationRules.ComputeSha256(
                    $"fixture-candidate:{flow.Id:D}:{flow.Iteration}:{outcomeOwnerStepId:D}"),
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
                DateTimeOffset.UtcNow);
    }

    private sealed class ReviewWorkspaceManager(string path) : IWorkspaceManager
    {
        public Task<WorkspaceInfo> PrepareAsync(
            FlowRun flow,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new WorkspaceInfo(path, "review-workflow", CreatedNow: false));
    }

    public sealed class ReviewDbContextFactory(
        DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);

        public Task<HarnessDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HarnessDbContext(options));
    }
}

using System.Text.Json;
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
    public void ReviewFeedbackV1_IsExactStrictAndBounded()
    {
        var output = $$"""
            surrounding classification
            {{ReviewFeedbackParser.BeginSentinel}}
            {
              "Version": "review-feedback-v1",
              "Intent": "RequestRefinement",
              "CustomerReply": "I will ask the team to narrow the result.",
              "Refinement": {
                "Goal": "Limit the recommendation to checkout resilience.",
                "RequestedChanges": ["Exclude unrelated services."]
              },
              "ExplicitImplementationAdoption": false
            }
            {{ReviewFeedbackParser.EndSentinel}}
            """;

        var parsed = ReviewFeedbackParser.Parse(output);

        Assert.Equal(ReviewIntent.RequestRefinement, parsed.Document.Intent);
        Assert.Equal(
            "Limit the recommendation to checkout resilience.",
            parsed.Document.Refinement!.Goal);
        Assert.Throws<ReviewFeedbackContractException>(() =>
            ReviewFeedbackParser.ParseJson(
                parsed.RawJson.Replace(
                    "\"ExplicitImplementationAdoption\": false",
                    "\"ExplicitImplementationAdoption\": false, \"Unknown\": true",
                    StringComparison.Ordinal)));
        Assert.Throws<ReviewFeedbackContractException>(() =>
            ReviewFeedbackParser.Parse(
                output.Replace(
                    "\"RequestRefinement\"",
                    "\"requestrefinement\"",
                    StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<DirectReviewRequest>(
                """
                {
                  "GateId": "00000000-0000-0000-0000-000000000001",
                  "Intent": "Accept",
                  "Unknown": true
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
        Assert.DoesNotContain(
            before.GateRecords,
            gate => gate.ActionType == HandoffActionType.Release);
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
            before.GateRecords,
            gate => gate.ActionType == HandoffActionType.Release);
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

        var first = await harness.Reviews.ReviewAsync(
            harness.FlowId,
            new DirectReviewRequest
            {
                GateId = review.Id,
                Intent = ReviewIntent.Accept
            });
        var duplicate = await harness.Reviews.ReviewAsync(
            harness.FlowId,
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
        Assert.Equal(FlowStatus.Approved, published.Status);
        Assert.Equal(1, harness.PublicationVerifier.Calls);
        Assert.Single(
            published.Steps,
            step => ReviewCoordinator.IsStudioPublicationStep(published, step));
        Assert.Contains(
            harness.Runner.Contexts,
            context =>
                context.AgentId == "sky-publisher" &&
                context.IsHostControlledPublication &&
                !context.AllowRemotePublication);
        Assert.Equal(1, harness.CandidatePublisher.Calls);
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
                () => harness.Reviews.ReviewAsync(
                    harness.FlowId,
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
        await harness.Reviews.ReviewAsync(
            harness.FlowId,
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
            await harness.Reviews.ReviewAsync(harness.FlowId, request);
            await harness.Reviews.ReviewAsync(harness.FlowId, request);
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
                        ReviewCoordinator.IsStudioPublicationStep(
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

        await harness.Reviews.ReviewAsync(harness.FlowId, request);
        Assert.Equal(
            harness.FlowId,
            await harness.Queue.Reader.ReadAsync());
        harness.Queue.MarkDequeued();

        await harness.Reviews.ReviewAsync(harness.FlowId, request);

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
            harness.Reviews.ReviewAsync(
                harness.FlowId,
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
        await harness.Reviews.ReviewAsync(
            harness.FlowId,
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
        await harness.Reviews.ReviewAsync(
            harness.FlowId,
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
        await harness.Reviews.ReviewAsync(
            harness.FlowId,
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
    public async Task PublicationCompletion_RejectsMalformedHandoffBeforePublisherSideEffects(
        string publicationOutput)
    {
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Delivery);
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var review = Assert.Single(
            (await harness.LoadFlowAsync()).GateRecords,
            gate => gate.ActionType ==
                    HandoffActionType.CustomerReview);
        await harness.Reviews.ReviewAsync(
            harness.FlowId,
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
        await harness.Reviews.ReviewAsync(
            harness.FlowId,
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
        var refinement = await harness.Reviews.ReviewAsync(
            harness.FlowId,
            refinementRequest);
        var replay = await harness.Reviews.ReviewAsync(
            harness.FlowId,
            refinementRequest);
        var differentReplay = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Reviews.ReviewAsync(
                harness.FlowId,
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
        Assert.Equal(first.WorkspacePath, refinement.Flow.WorkspacePath);
        Assert.StartsWith(
            "Customer refinement for iteration 1:",
            refinement.Flow.ConsolidatedRequest,
            StringComparison.Ordinal);
        Assert.Contains(
            "Current reviewed flow-outcome-v1 JSON:",
            refinement.Flow.ConsolidatedRequest,
            StringComparison.Ordinal);
        Assert.Contains(
            "Previous confirmed brief:",
            refinement.Flow.ConsolidatedRequest,
            StringComparison.Ordinal);
        Assert.True(
            refinement.Flow.ConsolidatedRequest.IndexOf(
                "Current reviewed flow-outcome-v1 JSON:",
                StringComparison.Ordinal) <
            refinement.Flow.ConsolidatedRequest.IndexOf(
                "Previous confirmed brief:",
                StringComparison.Ordinal));
        Assert.Contains(
            FlowOutcomeParser.Version,
            refinement.Flow.ConsolidatedRequest,
            StringComparison.Ordinal);
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
    public async Task LegacyReleaseGate_RemainsReadableAndUsesLegacyDecisionPath()
    {
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Advisory);
        var legacyFlow = new FlowRun
        {
            Title = "Legacy delivery",
            OriginalRequest = "Ship the legacy result.",
            ConsolidatedRequest = "Ship the legacy result.",
            Kind = FlowKind.Delivery,
            ContractVersion = "legacy-v1",
            Status = FlowStatus.WaitingForFeedback,
            RepositoryPath = harness.Root,
            RepositoryKnowledge = "Legacy fixture.",
            WorkspacePath = harness.WorkspacePath,
            BranchName = "legacy-review",
            Outcome = OutcomeType.PullRequest
        };
        var releaseStep = new FlowStep
        {
            FlowRunId = legacyFlow.Id,
            Iteration = 1,
            Sequence = 10,
            AgentId = "legacy-release",
            AgentName = "Legacy Release",
            AgentRole = "release-engineer",
            Status = StepStatus.Completed,
            OutputSummary = "Prepared release candidate."
        };
        legacyFlow.Steps.Add(releaseStep);
        var releaseGate = harness.Gate.SubmitProposal(new HandoffProposal
        {
            FlowRunId = legacyFlow.Id,
            FlowStepId = releaseStep.Id,
            ActionType = HandoffActionType.Release,
            Summary = "Legacy customer release review.",
            BlastRadius = HandoffBlastRadius.High
        });
        legacyFlow.GateRecords.Add(releaseGate);
        await using (var database =
                     await harness.Factory.CreateDbContextAsync())
        {
            database.Flows.Add(legacyFlow);
            await database.SaveChangesAsync();
        }

        var readable = legacyFlow.ToDetailDto();
        var dtoGate = Assert.Single(readable.GateRecords);
        Assert.Equal(HandoffActionType.Release, dtoGate.ActionType);
        Assert.Null(dtoGate.ReviewDecision);

        var coordinator = new FeedbackCoordinator(
            harness.Factory,
            new FixedModelRouter(),
            new BootstrapTaskProfileFactory(),
            TestRoutingSupport.Recorder(harness.Factory),
            harness.Runner,
            harness.Gate,
            new FlowQueue(),
            new FlowLifecycleCoordinator(),
            workflowProvider: harness.WorkflowProvider);
        var decision = await coordinator.DecideAsync(
            legacyFlow.Id,
            approve: true,
            releaseGate.Id,
            candidateFingerprint: string.Empty,
            feedback: string.Empty);

        Assert.Equal(ReleaseDecisionOutcome.Approved, decision.Outcome);
        FlowStep publication;
        await using (var database =
                     await harness.Factory.CreateDbContextAsync())
        {
            publication = await database.FlowSteps
                .AsNoTracking()
                .SingleAsync(step =>
                    step.FlowRunId == legacyFlow.Id &&
                    step.AgentRole == "release-engineer" &&
                    step.Status == StepStatus.Pending);
        }
        Assert.True(publication.RemotePublicationAllowed);
        Assert.Equal(
            ExecutionInvocationKind.Publication,
            publication.InvocationKind);
        Assert.Equal(PlanStage.AfterApproval, publication.PlanStage);
        Assert.Equal("""["Publish"]""", publication.PlanDutiesJson);
        Assert.Equal(
            ExecutionPermissionProfile.Publish,
            publication.PermissionProfile);

        var permission =
            await CopilotReasoningHost.ResolveAndPersistPermissionAsync(
                new AgentExecutionContext(
                    legacyFlow.Id,
                    legacyFlow.Iteration,
                    publication.AgentId,
                    publication.AgentName,
                    publication.AgentRole,
                    "model",
                    "high",
                    publication.Attempt,
                    publication.InputSummary,
                    legacyFlow.RepositoryKnowledge,
                    legacyFlow.RepositoryPath,
                    legacyFlow.WorkspacePath,
                    Guid.NewGuid(),
                    legacyFlow.Outcome,
                    "Publish the approved legacy outcome.",
                    [],
                    [],
                    FlowStepId: publication.Id,
                    ContractVersion: "legacy-v1",
                    InvocationKind:
                        ExecutionInvocationKind.Publication),
                harness.WorkflowProvider.GetEffective(),
                new PermissionProfileResolver(),
                harness.Factory,
                CancellationToken.None);
        Assert.Equal(
            ExecutionPermissionProfile.Publish,
            permission.Profile);
        Assert.True(permission.AllowRemotePublication);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MigratedLegacyFeedback_UsesCapturedProductManager(
        bool keepChangedGlobalDefinition)
    {
        await using var harness =
            await ReviewHarness.CreateAsync(FlowKind.Advisory);
        var legacyFlow = new FlowRun
        {
            Title = "Legacy feedback",
            OriginalRequest = "Review the legacy result.",
            ConsolidatedRequest = "Review the legacy result.",
            Kind = FlowKind.Delivery,
            ContractVersion = "legacy-v1",
            Status = FlowStatus.WaitingForFeedback,
            RepositoryPath = harness.Root,
            RepositoryKnowledge = "Legacy fixture.",
            WorkspacePath = harness.WorkspacePath,
            Outcome = OutcomeType.PullRequest
        };
        legacyFlow.AgentSnapshots.Add(new FlowAgentSnapshot
        {
            FlowRunId = legacyFlow.Id,
            AgentId = "product-manager",
            Name = "Captured Product Manager",
            Description = "Captured feedback specialist.",
            Role = "product-manager",
            Instructions = "Use the captured legacy feedback contract.",
            DefinitionHash = "sha256:captured-product-manager",
            EnabledAtSnapshot = true,
            SourceFileName = "product-manager.agent.md"
        });
        await using (var database =
                     await harness.Factory.CreateDbContextAsync())
        {
            database.Flows.Add(legacyFlow);
            if (keepChangedGlobalDefinition)
            {
                database.Agents.Add(new AgentRecord
                {
                    Id = "product-manager",
                    Name = "Changed Global Product Manager",
                    Description = "This mutable definition must be ignored.",
                    Role = "product-manager",
                    SourcePath = "changed.agent.md",
                    Enabled = false,
                    SortOrder = 50
                });
            }
            await database.SaveChangesAsync();
        }

        var catalog = new AgentCatalog(
            new HarnessPaths(
                harness.Root,
                Path.Combine(harness.Root, ".github", "agents"),
                Path.Combine(harness.Root, "harness.db")),
            harness.Factory);
        var snapshotService =
            new FlowAgentSnapshotService(harness.Factory, catalog);
        var coordinator = new FeedbackCoordinator(
            harness.Factory,
            new FixedModelRouter(),
            new BootstrapTaskProfileFactory(),
            TestRoutingSupport.Recorder(harness.Factory),
            harness.Runner,
            harness.Gate,
            new FlowQueue(),
            new FlowLifecycleCoordinator(),
            workflowProvider: harness.WorkflowProvider,
            flowAgentSnapshotService: snapshotService);

        var response = await coordinator.RespondAsync(
            legacyFlow.Id,
            "Explain the result before I decide.");

        Assert.True(response.ShouldSpeak);
        var context = Assert.Single(
            harness.Runner.Contexts,
            item => item.FlowId == legacyFlow.Id);
        Assert.Equal("product-manager", context.AgentId);
        Assert.Equal("Captured Product Manager", context.AgentName);
        await using var verify =
            await harness.Factory.CreateDbContextAsync();
        var step = await verify.FlowSteps.SingleAsync(item =>
            item.FlowRunId == legacyFlow.Id &&
            item.AgentRole == "product-manager");
        Assert.Equal("Captured Product Manager", step.AgentName);
        Assert.Contains(
            await verify.FlowMessages
                .Where(item => item.FlowRunId == legacyFlow.Id)
                .ToListAsync(),
            item => item.Role == ConversationRole.ProductManager);
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
            FlowQueue queue)
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

        public static async Task<ReviewHarness> CreateAsync(
            FlowKind kind,
            RecordingPublicationVerifier? publicationVerifier = null)
        {
            var root = Path.Combine(
                AppContext.BaseDirectory,
                "review-workflow-tests",
                Guid.NewGuid().ToString("N"));
            var agentsDirectory = Path.Combine(root, ".github", "agents");
            var workspacePath = Path.Combine(root, "workspace");
            Directory.CreateDirectory(agentsDirectory);
            Directory.CreateDirectory(workspacePath);
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
                  version: 1
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
                """);

            var plan = kind == FlowKind.Advisory
                ? AdvisoryPlan()
                : DeliveryPlan();
            var flow = new FlowRun
            {
                Title = "Generic customer review",
                OriginalRequest = "Produce a customer-reviewable result.",
                ConsolidatedRequest = "Produce a customer-reviewable result.",
                Kind = kind,
                ContractVersion = "studio-v2",
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
            gate.SetTrustLevel(HandoffActionType.Release, HandoffTrustLevel.Gated);
            gate.SetTrustLevel(
                HandoffActionType.CustomerReview,
                HandoffTrustLevel.Gated);
            var verifier = publicationVerifier ??
                           new RecordingPublicationVerifier();
            var candidatePublisher = new RecordingCandidatePublisher();
            var reviewedCandidates = new RecordingReviewedCandidateService();
            var flowQueue = new FlowQueue();
            var lifecycle = new FlowLifecycleCoordinator();
            var engine = new WorkflowEngine(
                factory,
                catalog,
                new FlowPlanner(),
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
                reviewedCandidateService: reviewedCandidates);
            var reviews = new ReviewCoordinator(
                factory,
                gate,
                flowQueue,
                lifecycle,
                workflowProvider);
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
                flowQueue);
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
                Version = TeamPlanParser.Version,
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
                Version = TeamPlanParser.Version,
                Disposition = TeamPlanDisposition.Planned,
                Steps =
                [
                    Step(
                        "prepare",
                        "outcome-crafter",
                        10,
                        PlanStage.BeforeReview,
                        [
                            PlanDuty.Implement,
                            PlanDuty.Verify,
                            PlanDuty.PrepareOutcome
                        ],
                        outcomeOwner: true),
                    Step(
                        "publish",
                        "sky-publisher",
                        20,
                        PlanStage.AfterApproval,
                        [PlanDuty.Publish],
                        outcomeOwner: false,
                        dependsOn: ["prepare"])
                ],
                PreMortemCheckpoints = [],
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
                  {{{IntakeV2Parser.BeginSentinel}}}
                  {"Version":"intake-v2","Status":"Confirmed","FlowKind":"{{{(context.Outcome == OutcomeType.None ? "Advisory" : "Delivery")}}}","TaskTitle":"Refine customer result","CustomerReply":"I normalized the requested refinement for the team.","Brief":{"Goal":"Focus on checkout resilience.","Details":["Exclude account services.","Add operational trade-offs."],"SuccessCriteria":["The revised result addresses the requested focus."],"Constraints":[],"Assumptions":[]}}
                  {{{IntakeV2Parser.EndSentinel}}}
                  """,
                "product-manager" => """
                  The captured Product Manager reviewed the full legacy execution ledger.

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
                    {"Version":"flow-outcome-v1","Goal":"Produce a customer-reviewable result.","Summary":"The requested result is ready for customer review.","ImplementationDetails":["The assigned evidence was consolidated into this result."],"Artifacts":[]}
                    """ +
                    Environment.NewLine +
                    FlowOutcomeParser.EndSentinel;
            }
            return Task.FromResult(new AgentExecutionResult(
                output,
                "Fixture evidence.",
                1,
                []));
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
                    OutcomeVerificationRules.CandidateManifestVersion,
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
                ReviewedCandidateIdentity.CurrentVersion,
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

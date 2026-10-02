using System.Text.Json;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Data;
using AiHarnessDemo.Infrastructure;
using AiHarnessDemo.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiHarnessDemo.Tests;

public sealed class BlockerPromotionTests
{
    [Fact]
    public async Task MissingQualification_BlocksSafelyOnceAndRecoveryLeavesItIdle()
    {
        await using var harness = await Slice8Harness.CreateAsync(
            ParentState.QueuedMissingQualification);

        await harness.Engine.RunAsync(harness.ParentId, CancellationToken.None);
        var blocked = await harness.LoadAsync(harness.ParentId);
        var expectedData =
            MissingQualificationCoordinator.SerializeBlocker(
                Slice8Harness.MissingQualification);

        Assert.Equal(FlowStatus.Blocked, blocked.Status);
        Assert.Equal(
            MissingQualificationCoordinator.BlockerCode,
            blocked.CurrentBlockerCode);
        Assert.Equal(
            Slice8Harness.MissingQualification.Summary,
            blocked.CurrentBlockerSummary);
        Assert.Equal(expectedData, blocked.CurrentBlockerDataJson);
        Assert.NotEqual(
            blocked.CurrentBlockerSummary,
            blocked.CustomerBlockerMessage);
        Assert.DoesNotContain(
            Slice8Harness.MissingQualification.Summary,
            blocked.CustomerBlockerMessage!,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            blocked.Steps,
            step => step.Status == StepStatus.Pending);
        Assert.Equal(0, harness.Engine.ActiveFlowCount);
        Assert.Single(
            blocked.Events,
            item => item.Type == "plan.missing-qualification");
        Assert.Single(
            blocked.Events,
            item => item.Type == "flow.blocked");
        var accountManager = Assert.Single(
            blocked.Steps,
            step =>
                step.PlanStepKey ==
                MissingQualificationCoordinator.AccountManagerPlanStepKey);
        Assert.Equal(
            ExecutionPermissionProfile.ReadOnlySource,
            accountManager.PermissionProfile);
        Assert.NotNull(accountManager.CopilotSessionId);
        Assert.Equal(
            1,
            harness.Runner.Contexts.Count(context =>
                context.AgentId == "account-manager"));

        var recovered = await harness.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);
        Assert.DoesNotContain(harness.ParentId, recovered);
        await harness.Engine.RunAsync(harness.ParentId, CancellationToken.None);
        Assert.Equal(
            1,
            harness.Runner.Contexts.Count(context =>
                context.AgentId == "account-manager"));
        Assert.Single(
            (await harness.LoadAsync(harness.ParentId)).Events,
            item => item.Type == "flow.blocked");
    }

    [Fact]
    public async Task BlockerExplanationFailure_UsesSafeFallbackAndCanBeAbandoned()
    {
        await using var harness = await Slice8Harness.CreateAsync(
            ParentState.QueuedMissingQualification,
            failBlockerExplanation: true);

        await harness.Engine.RunAsync(harness.ParentId, CancellationToken.None);
        var blocked = await harness.LoadAsync(harness.ParentId);

        Assert.Equal(FlowStatus.Blocked, blocked.Status);
        Assert.Equal(
            MissingQualificationCoordinator.SafeFallbackMessage,
            blocked.CustomerBlockerMessage);
        Assert.Contains(
            blocked.Events,
            item => item.Type == "qualification.customer-explanation-failed");
        Assert.Equal(string.Empty, blocked.FailureReason);

        var abandonment = new FlowAbandonmentService(
            harness.Factory,
            new NoOpExecutionController(),
            new NoOpProcessCleaner(),
            new NoOpSessionCleaner(),
            harness.Workspace,
            harness.Lifecycle,
            NullLogger<FlowAbandonmentService>.Instance);
        var result = await abandonment.AbandonAsync(harness.ParentId);

        Assert.Equal(FlowStatus.Abandoned, result.Status);
        Assert.Equal(
            FlowStatus.Abandoned,
            (await harness.LoadAsync(harness.ParentId)).Status);
    }

    [Fact]
    public async Task Recovery_InvalidCompletedBlockerExplanationFallsBackToBlockedAndCleansStaging()
    {
        await using var harness = await Slice8Harness.CreateAsync(
            ParentState.QueuedMissingQualification);
        var (stepId, stagedRoot) =
            await harness.SeedRecoveredBlockerExplanationAsync(
                "SENSITIVE_INVALID_BLOCKER_EXPLANATION");

        await harness.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);
        await harness.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);

        var blocked = await harness.LoadAsync(harness.ParentId);
        Assert.Equal(FlowStatus.Blocked, blocked.Status);
        Assert.Equal(
            MissingQualificationCoordinator.SafeFallbackMessage,
            blocked.CustomerBlockerMessage);
        Assert.Empty(blocked.FailureReason);
        var explanation = blocked.Steps.Single(
            step => step.Id == stepId);
        Assert.Equal(StepStatus.Failed, explanation.Status);
        Assert.Single(
            blocked.Events,
            item => item.FlowStepId == stepId &&
                    item.Type ==
                    "qualification.customer-explanation-failed");
        Assert.Single(
            blocked.Events,
            item => item.Type == "flow.blocked");
        Assert.DoesNotContain(
            harness.Runner.Contexts,
            item => item.InvocationKind ==
                    ExecutionInvocationKind.BlockerExplanation);
        Assert.False(Directory.Exists(stagedRoot));
    }

    [Fact]
    public async Task RosterRepair_CreatesOneFreshLinkedFlowFromCurrentCatalog()
    {
        await using var harness = await Slice8Harness.CreateAsync(
            ParentState.Blocked);
        var parent = await harness.LoadAsync(harness.ParentId);
        var oldRevision = parent.AgentCatalogRevision;
        await harness.ChangeCatalogAsync();

        var request = new QualificationResolutionRequest
        {
            Action = QualificationResolutionAction.RosterRepair
        };
        var first = await harness.Qualifications.ResolveAsync(
            harness.ParentId,
            request);
        var duplicate = await harness.Qualifications.ResolveAsync(
            harness.ParentId,
            request);
        var child = await harness.LoadAsync(first.Successor.Id);
        parent = await harness.LoadAsync(harness.ParentId);

        Assert.Equal(first.Successor.Id, duplicate.Successor.Id);
        Assert.False(first.Response.ExistingSuccessor);
        Assert.True(duplicate.Response.ExistingSuccessor);
        Assert.Equal(FlowStatus.Blocked, parent.Status);
        Assert.Equal(FlowLinkKind.QualificationRosterRepair, child.LinkKind);
        Assert.Equal(parent.Id, child.ParentFlowRunId);
        Assert.Equal(parent.Iteration, child.ParentIteration);
        Assert.Equal(parent.RepositoryPath, child.RepositoryPath);
        Assert.Equal(parent.RepositoryKnowledge, child.RepositoryKnowledge);
        Assert.NotEqual(oldRevision, child.AgentCatalogRevision);
        Assert.NotEqual(parent.AgentCatalogRevision, child.AgentCatalogRevision);
        Assert.NotEqual(parent.WorkspacePath, child.WorkspacePath);
        Assert.Single(
            child.Steps,
            step => step.AgentRole == "account-manager");
        Assert.Equal(
            FlowStatus.Intake,
            child.Status);
        Assert.Single(parent.LinkedFlowRuns);
    }

    [Fact]
    public async Task ScopeRevision_ReturnsThroughConfirmationAndAdmissionFailureIsNonMutating()
    {
        await using var harness = await Slice8Harness.CreateAsync(
            ParentState.Blocked);
        harness.Admission.Ready = false;
        var request = new QualificationResolutionRequest
        {
            Action = QualificationResolutionAction.ScopeRevision,
            ScopeRevision = new QualificationScopeRevision
            {
                Goal = "Assess checkout retries only.",
                Scope = ["Checkout retry behavior", "Operational trade-offs"]
            }
        };

        await Assert.ThrowsAsync<NewWorkAdmissionException>(() =>
            harness.Qualifications.ResolveAsync(harness.ParentId, request));
        var unchanged = await harness.LoadAsync(harness.ParentId);
        Assert.Equal(FlowStatus.Blocked, unchanged.Status);
        Assert.Empty(unchanged.LinkedFlowRuns);

        harness.Admission.Ready = true;
        var result = await harness.Qualifications.ResolveAsync(
            harness.ParentId,
            request);
        var replay = await harness.Qualifications.ResolveAsync(
            harness.ParentId,
            request);
        var conflict = new QualificationResolutionRequest
        {
            Action = QualificationResolutionAction.ScopeRevision,
            ScopeRevision = new QualificationScopeRevision
            {
                Goal = "Assess payment recovery only.",
                Scope = ["Payment recovery behavior"]
            }
        };
        await Assert.ThrowsAsync<FlowLifecycleException>(() =>
            harness.Qualifications.ResolveAsync(
                harness.ParentId,
                conflict));
        var child = await harness.LoadAsync(result.Successor.Id);

        Assert.Equal(result.Successor.Id, replay.Successor.Id);
        Assert.True(replay.Response.ExistingSuccessor);
        Assert.Equal(FlowLinkKind.QualificationScopeRevision, child.LinkKind);
        Assert.Equal(FlowStatus.Intake, child.Status);
        Assert.Contains("Assess checkout retries only.", child.OriginalRequest);
        Assert.Contains("Checkout retry behavior", child.OriginalRequest);
        Assert.Contains(
            child.Events,
            item =>
                item.Type == "intake.confirmation_requested");
        var identity = Assert.Single(
            child.Events,
            item =>
                item.Type ==
                "qualification.scope-revision-request");
        Assert.Contains(
            "\"RequestHash\":\"sha256:",
            identity.DataJson,
            StringComparison.Ordinal);
        Assert.NotEmpty(result.Response.AccountManagerReply);
    }

    [Fact]
    public async Task AdvisoryPromotion_IsAtomicCleanFreshAndConcurrentIdempotent()
    {
        await using var harness = await Slice8Harness.CreateAsync(
            ParentState.AdvisoryReview);
        var before = await harness.LoadAsync(harness.ParentId);
        var gate = Assert.Single(
            before.GateRecords,
            item => item.ActionType == HandoffActionType.CustomerReview);
        var parentEntityIds = before.Steps.Select(item => item.Id)
            .Concat(before.Messages.Select(item => item.Id))
            .Concat(before.Events.Select(item => item.Id))
            .Concat(before.GateRecords.Select(item => item.Id))
            .Concat(before.PlanDocuments.Select(item => item.Id))
            .ToHashSet();
        await harness.ChangeCatalogAsync();

        var requests = Enumerable.Range(0, 4)
            .Select(_ => harness.Reviews.ReviewAsync(
                harness.ParentId,
                new DirectReviewRequest
                {
                    GateId = gate.Id,
                    Intent = ReviewIntent.PromoteToDelivery
                }))
            .ToArray();
        var results = await Task.WhenAll(requests);
        var childId = Assert.Single(
            results.Select(item => item.Review.LinkedFlowId).Distinct())!.Value;
        var parent = await harness.LoadAsync(harness.ParentId);
        var child = await harness.LoadAsync(childId);

        Assert.Equal(FlowStatus.Approved, parent.Status);
        var promotedGate = Assert.Single(
            parent.GateRecords,
            item => item.Id == gate.Id);
        Assert.True(promotedGate.Resolved);
        Assert.True(promotedGate.Approved);
        Assert.Equal(
            ReviewDecision.PromotedToDelivery,
            promotedGate.ReviewDecision);
        Assert.Single(
            parent.LinkedFlowRuns,
            item => item.LinkKind == FlowLinkKind.AdvisoryPromotion);
        Assert.Equal(FlowKind.Delivery, child.Kind);
        Assert.Equal(FlowLinkKind.AdvisoryPromotion, child.LinkKind);
        Assert.Equal(parent.Id, child.ParentFlowRunId);
        Assert.Equal(parent.Iteration, child.ParentIteration);
        Assert.Equal(parent.RepositoryPath, child.RepositoryPath);
        Assert.Equal(parent.RepositoryKnowledge, child.RepositoryKnowledge);
        Assert.NotEqual(parent.WorkspacePath, child.WorkspacePath);
        Assert.NotEqual(parent.AgentCatalogRevision, child.AgentCatalogRevision);
        Assert.Equal(FlowStatus.Queued, child.Status);
        var childAccountManager = Assert.Single(
            child.Steps,
            item => item.AgentRole == "account-manager");
        Assert.Equal(
            ExecutionInvocationKind.Intake,
            childAccountManager.InvocationKind);
        Assert.Equal(
            IntakeCoordinator.InitialLinkedIntakePlanStepKey,
            childAccountManager.PlanStepKey);
        Assert.Equal("Customer confirmed brief", childAccountManager.Label);
        Assert.NotNull(childAccountManager.CopilotSessionId);
        Assert.DoesNotContain(
            childAccountManager.CopilotSessionId!.Value,
            before.Steps
                .Select(item => item.CopilotSessionId)
                .OfType<Guid>());
        Assert.Empty(child.PlanDocuments);
        Assert.DoesNotContain(
            child.Steps.Select(item => item.Id)
                .Concat(child.Messages.Select(item => item.Id))
                .Concat(child.Events.Select(item => item.Id))
                .Concat(child.GateRecords.Select(item => item.Id)),
            parentEntityIds.Contains);
        Assert.Empty(child.Steps.SelectMany(item => item.ToolCalls));
        var seed = Assert.Single(
            child.Events,
            item => item.Type == "flow.linked-intake-seed").DataJson!;
        Assert.Contains("Accepted checkout goal", seed);
        Assert.Contains("Implement retry behavior", seed);
        Assert.DoesNotContain("SUMMARY MUST NOT MOVE", seed);
        Assert.DoesNotContain("RAW ADVISORY DIALOGUE SECRET", seed);
        Assert.DoesNotContain(before.OutcomeContractJson, seed);
        Assert.Single(
            child.Events,
            item => item.Type == "intake.confirmed");
        Assert.Single(
            child.Events,
            item => item.Type == "flow.queued");

        var reviewResult = await harness.Reviews.GetReviewResultAsync(parent.Id);
        Assert.Equal(child.Id, reviewResult.LinkedFlowId);
        Assert.Equal(
            ReviewDecision.PromotedToDelivery,
            reviewResult.Decision);
        Assert.Equal(parent.Id, (await harness.LoadAsync(parent.Id)).Id);
        Assert.Equal(child.Id, (await harness.LoadAsync(child.Id)).Id);
    }

    [Fact]
    public async Task AdvisoryPromotion_MaximumValidOutcomeReachesAccountManagerOnceAndQueues()
    {
        await using var harness = await Slice8Harness.CreateAsync(
            ParentState.AdvisoryReview);
        static string Fill(string marker, char fill) =>
            marker + new string(fill, 4_000 - marker.Length);
        var goal = Fill("maximum-promotion-goal:", 'g');
        var details = Enumerable.Range(1, 24)
            .Select(index => Fill(
                $"implementation-detail-{index:D2}:",
                (char)('a' + index % 26)))
            .ToArray();
        var maximumOutcome = JsonSerializer.Serialize(
            new FlowOutcomeDocument
            {
                Goal = goal,
                Summary = "Every accepted implementation detail must be adopted.",
                ImplementationDetails = details,
                Artifacts = []
            });
        _ = FlowOutcomeParser.ParseJson(maximumOutcome);
        await harness.UpdateParentAsync(flow =>
            flow.OutcomeContractJson = maximumOutcome);
        var parent = await harness.LoadAsync(harness.ParentId);
        var gate = Assert.Single(parent.GateRecords);

        var result = await harness.Reviews.ReviewAsync(
            parent.Id,
            new DirectReviewRequest
            {
                GateId = gate.Id,
                Intent = ReviewIntent.PromoteToDelivery
            });
        var childId = result.Review.LinkedFlowId!.Value;
        var child = await harness.LoadAsync(childId);
        var context = Assert.Single(
            harness.Runner.Contexts,
            item =>
                item.FlowId == childId &&
                item.InvocationKind == ExecutionInvocationKind.Intake);
        var promotion = Assert.IsType<AdvisoryPromotionContext>(
            context.PromotionContext);
        var seed = AdvisoryPromotionSeedParser.Parse(
            promotion.CanonicalSeedJson);
        var adoptedDetails =
            Assert.IsAssignableFrom<IReadOnlyList<string>>(
                seed.ImplementationDetails);

        Assert.Equal(100_000, seed.Goal.Length +
            adoptedDetails.Sum(item => item.Length));
        Assert.Equal(goal, seed.Goal);
        Assert.Equal(details, adoptedDetails);
        Assert.Equal(
            AdvisoryPromotionSeedParser.ComputeHash(
                promotion.CanonicalSeedJson),
            promotion.SeedHash);
        Assert.Equal(details[11], adoptedDetails[11]);
        Assert.Equal(details[^1], adoptedDetails[^1]);
        Assert.DoesNotContain(goal, context.Task, StringComparison.Ordinal);
        Assert.DoesNotContain(details[11], context.Task, StringComparison.Ordinal);
        Assert.DoesNotContain(details[^1], context.Task, StringComparison.Ordinal);
        Assert.Equal(FlowStatus.Queued, child.Status);
        Assert.Single(
            child.Events,
            item => item.Type == "intake.confirmed");
        Assert.Single(
            child.Events,
            item => item.Type == "flow.queued");
    }

    [Fact]
    public async Task AdvisoryPromotion_DirectConfirmationRejectsSeedDriftVisibly()
    {
        await using var harness = await Slice8Harness.CreateAsync(
            ParentState.AdvisoryReview);
        harness.Runner.PromotionBehavior =
            PromotionIntakeBehavior.DriftedConfirmed;
        var parent = await harness.LoadAsync(harness.ParentId);
        var gate = Assert.Single(parent.GateRecords);

        var result = await harness.Reviews.ReviewAsync(
            parent.Id,
            new DirectReviewRequest
            {
                GateId = gate.Id,
                Intent = ReviewIntent.PromoteToDelivery
            });
        var child = await harness.LoadAsync(
            result.Review.LinkedFlowId!.Value);

        Assert.Equal(FlowStatus.Intake, child.Status);
        var failed = Assert.Single(
            child.Steps,
            item => item.AgentRole == "account-manager");
        Assert.Equal(StepStatus.Failed, failed.Status);
        Assert.Contains(
            "drifted",
            failed.PushbackReason,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Initial linked Account Manager intake failed",
            child.FailureReason);
        Assert.Single(
            child.Events,
            item => item.Type == "linked.intake-failed");
        Assert.Contains(
            child.Messages,
            item =>
                item.Role == ConversationRole.Harness &&
                item.Content.Contains(
                    "send a new intake message to retry",
                    StringComparison.Ordinal));
        Assert.DoesNotContain(
            child.Events,
            item => item.Type == "flow.queued");
    }

    [Fact]
    public async Task LinkedIntakeRecovery_InvokesNormalAccountManagerOnceAfterCrashWindow()
    {
        await using var harness = await Slice8Harness.CreateAsync(
            ParentState.AdvisoryReview);
        var childId = await harness.CreateSeedOnlyPromotionChildAsync(
            persistSeedMessage: true);
        var beforeContexts = harness.Runner.Contexts.Count;

        await harness.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);
        await harness.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);

        var child = await harness.LoadAsync(childId);
        Assert.Equal(FlowStatus.Queued, child.Status);
        Assert.Equal(
            beforeContexts + 1,
            harness.Runner.Contexts.Count);
        Assert.Single(
            child.Steps,
            item => item.AgentRole == "account-manager");
        Assert.Single(
            child.Messages,
            item => item.Role == ConversationRole.Customer);
        Assert.Single(
            child.Events,
            item => item.Type == "intake.confirmed");
        Assert.Single(
            child.Events,
            item => item.Type == "flow.queued");
    }

    [Fact]
    public async Task LinkedIntakeRecovery_ContinuesPersistedPendingAttemptExactlyOnce()
    {
        await using var harness = await Slice8Harness.CreateAsync(
            ParentState.AdvisoryReview);
        var seeded = await harness.CreatePersistedLinkedIntakeAsync(
            StepStatus.Pending,
            LinkedJournalFixtureState.Missing,
            persistTaskProfile: false);
        var beforeContexts = harness.Runner.Contexts.Count;

        await harness.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);
        await harness.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);
        var child = await harness.LoadAsync(seeded.FlowId);

        Assert.Equal(FlowStatus.Queued, child.Status);
        var step = Assert.Single(
            child.Steps,
            item =>
                item.PlanStepKey ==
                IntakeCoordinator.InitialLinkedIntakePlanStepKey);
        Assert.Equal(seeded.StepId, step.Id);
        Assert.Equal(StepStatus.Completed, step.Status);
        Assert.Single(
            child.TaskProfiles,
            item => item.FlowStepId == step.Id);
        Assert.Equal(
            beforeContexts + 1,
            harness.Runner.Contexts.Count);
        Assert.Single(
            child.Events,
            item => item.Type == "linked.intake-pending-recovered");
        Assert.Single(
            child.Events,
            item => item.Type == "intake.confirmed");
        Assert.Single(
            child.Events,
            item => item.Type == "flow.queued");
    }

    [Fact]
    public async Task LinkedIntakeRecovery_RunningWithoutJournalFailsVisibleOnce()
    {
        await using var harness = await Slice8Harness.CreateAsync(
            ParentState.AdvisoryReview);
        var seeded = await harness.CreatePersistedLinkedIntakeAsync(
            StepStatus.Running,
            LinkedJournalFixtureState.Missing,
            persistTaskProfile: true);
        var beforeContexts = harness.Runner.Contexts.Count;

        await harness.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);
        await harness.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);
        var child = await harness.LoadAsync(seeded.FlowId);

        Assert.Equal(FlowStatus.Intake, child.Status);
        Assert.Equal(
            beforeContexts,
            harness.Runner.Contexts.Count);
        var step = Assert.Single(
            child.Steps,
            item => item.Id == seeded.StepId);
        Assert.Equal(StepStatus.Failed, step.Status);
        Assert.Contains(
            "manual retry",
            step.PushbackReason,
            StringComparison.OrdinalIgnoreCase);
        Assert.Single(
            child.Events,
            item => item.Type == "linked.intake-failed");
        Assert.Single(
            child.Messages,
            item => item.Role == ConversationRole.Harness);
        Assert.DoesNotContain(
            child.Events,
            item => item.Type == "flow.queued");
    }

    [Fact]
    public async Task LinkedIntakeRecovery_AppliesCompletedJournalWithoutRerun()
    {
        await using var harness = await Slice8Harness.CreateAsync(
            ParentState.AdvisoryReview,
            modelRouterOverride:
                new UnavailableModelRouter());
        var seeded = await harness.CreatePersistedLinkedIntakeAsync(
            StepStatus.Running,
            LinkedJournalFixtureState.Completed,
            persistTaskProfile: true);
        var beforeContexts = harness.Runner.Contexts.Count;

        await harness.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);
        await harness.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);
        var child = await harness.LoadAsync(seeded.FlowId);

        Assert.Equal(FlowStatus.Queued, child.Status);
        Assert.Equal(
            beforeContexts,
            harness.Runner.Contexts.Count);
        Assert.Equal(
            StepStatus.Completed,
            Assert.Single(child.Steps, item =>
                item.Id == seeded.StepId).Status);
        Assert.Single(
            child.Events,
            item => item.Type == "linked.intake-journal-completed");
        Assert.Single(
            child.Messages,
            item => item.Role == ConversationRole.AccountManager);
        Assert.Single(
            child.Events,
            item => item.Type == "intake.confirmed");
        Assert.Single(
            child.Events,
            item => item.Type == "flow.queued");
    }

    [Fact]
    public async Task LinkedQualificationIntakeRecovery_ResumesInterruptedSessionOnce()
    {
        await using var harness = await Slice8Harness.CreateAsync(
            ParentState.AdvisoryReview);
        var seeded = await harness.CreatePersistedLinkedIntakeAsync(
            StepStatus.Running,
            LinkedJournalFixtureState.Interrupted,
            persistTaskProfile: true,
            FlowLinkKind.QualificationRosterRepair);
        var beforeContexts = harness.Runner.Contexts.Count;

        await harness.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);
        await harness.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);
        var child = await harness.LoadAsync(seeded.FlowId);
        var context = Assert.Single(
            harness.Runner.Contexts.Skip(beforeContexts));

        Assert.True(context.ResumeSession);
        Assert.True(context.RecoverInterruptedSession);
        Assert.Equal(seeded.FlowId, context.FlowId);
        Assert.Equal(FlowStatus.Intake, child.Status);
        Assert.Equal(
            StepStatus.Completed,
            Assert.Single(child.Steps, item =>
                item.Id == seeded.StepId).Status);
        Assert.Single(
            child.Events,
            item => item.Type == "linked.intake-session-resumed");
        Assert.Single(
            child.Events,
            item => item.Type == "intake.confirmation_requested");
        Assert.DoesNotContain(
            child.Events,
            item => item.Type == "flow.queued");
    }

    [Fact]
    public async Task AdvisoryPromotion_AwaitingConfirmationRemainsIdempotent()
    {
        await using var harness = await Slice8Harness.CreateAsync(
            ParentState.AdvisoryReview);
        harness.Runner.PromotionBehavior =
            PromotionIntakeBehavior.AwaitingConfirmation;
        var parent = await harness.LoadAsync(harness.ParentId);
        var gate = Assert.Single(parent.GateRecords);

        var result = await harness.Reviews.ReviewAsync(
            parent.Id,
            new DirectReviewRequest
            {
                GateId = gate.Id,
                Intent = ReviewIntent.PromoteToDelivery
            });
        var childId = result.Review.LinkedFlowId!.Value;
        var afterPromotionContexts = harness.Runner.Contexts.Count;
        await harness.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);
        await harness.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);
        var child = await harness.LoadAsync(childId);

        Assert.Equal(FlowStatus.Intake, child.Status);
        Assert.Equal(
            afterPromotionContexts,
            harness.Runner.Contexts.Count);
        Assert.Single(
            child.Steps,
            item =>
                item.AgentRole == "account-manager" &&
                item.Status == StepStatus.Completed);
        Assert.Single(
            child.Events,
            item => item.Type == "intake.confirmation_requested");
        Assert.DoesNotContain(
            child.Events,
            item => item.Type == "flow.queued");
    }

    [Fact]
    public async Task Promotion_RequiresValidCurrentAdvisoryReviewAndAdmission()
    {
        await using var harness = await Slice8Harness.CreateAsync(
            ParentState.AdvisoryReview);
        var parent = await harness.LoadAsync(harness.ParentId);
        var gate = Assert.Single(parent.GateRecords);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Reviews.ReviewAsync(
                parent.Id,
                new DirectReviewRequest
                {
                    GateId = gate.Id,
                    Intent = (ReviewIntent)int.MaxValue
                }));
        Assert.False(
            (await harness.LoadAsync(parent.Id)).GateRecords.Single().Resolved);

        harness.Admission.Ready = false;
        await Assert.ThrowsAsync<NewWorkAdmissionException>(() =>
            harness.Reviews.ReviewAsync(
                parent.Id,
                new DirectReviewRequest
                {
                    GateId = gate.Id,
                    Intent = ReviewIntent.PromoteToDelivery
                }));
        parent = await harness.LoadAsync(parent.Id);
        Assert.False(parent.GateRecords.Single().Resolved);
        Assert.Empty(parent.LinkedFlowRuns);

        harness.Admission.Ready = true;
        await harness.UpdateParentAsync(flow =>
            flow.OutcomeContractJson = "{not valid");
        await Assert.ThrowsAsync<FlowOutcomeContractException>(() =>
            harness.Reviews.ReviewAsync(
                parent.Id,
                new DirectReviewRequest
                {
                    GateId = gate.Id,
                    Intent = ReviewIntent.PromoteToDelivery
                }));
        await harness.UpdateParentAsync(flow =>
            flow.OutcomeContractJson = Slice8Harness.ValidOutcomeJson);
        await harness.UpdateParentAsync(flow => flow.Iteration = 2);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Reviews.ReviewAsync(
                parent.Id,
                new DirectReviewRequest
                {
                    GateId = gate.Id,
                    Intent = ReviewIntent.PromoteToDelivery
                }));
        await harness.UpdateParentAsync(flow => flow.Iteration = 1);
        await harness.UpdateParentAsync(flow => flow.Kind = FlowKind.Delivery);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Reviews.ReviewAsync(
                parent.Id,
                new DirectReviewRequest
                {
                    GateId = gate.Id,
                    Intent = ReviewIntent.PromoteToDelivery
                }));
        Assert.Empty((await harness.LoadAsync(parent.Id)).LinkedFlowRuns);
    }

    [Fact]
    public async Task AcceptedAdvisory_CanBePromotedAndRefinementRunsAccountManagerFirst()
    {
        await using var promotionHarness = await Slice8Harness.CreateAsync(
            ParentState.AdvisoryReview);
        var initial = await promotionHarness.LoadAsync(
            promotionHarness.ParentId);
        var gate = Assert.Single(initial.GateRecords);
        await promotionHarness.Reviews.ReviewAsync(
            initial.Id,
            new DirectReviewRequest
            {
                GateId = gate.Id,
                Intent = ReviewIntent.Accept
            });
        var promoted = await promotionHarness.Reviews.ReviewAsync(
            initial.Id,
            new DirectReviewRequest
            {
                GateId = gate.Id,
                Intent = ReviewIntent.PromoteToDelivery
            });
        Assert.NotNull(promoted.Review.LinkedFlowId);
        Assert.Equal(
            ReviewDecision.PromotedToDelivery,
            (await promotionHarness.LoadAsync(initial.Id))
                .GateRecords.Single().ReviewDecision);

        await using var refinementHarness = await Slice8Harness.CreateAsync(
            ParentState.AdvisoryReview);
        var refining = await refinementHarness.LoadAsync(
            refinementHarness.ParentId);
        var refinementGate = Assert.Single(refining.GateRecords);
        var originalWorkspace = refining.WorkspacePath;
        var originalSnapshots = refining.AgentSnapshots
            .OrderBy(item => item.AgentId)
            .Select(item => (item.AgentId, item.DefinitionHash))
            .ToArray();
        await refinementHarness.Reviews.ReviewAsync(
            refining.Id,
            new DirectReviewRequest
            {
                GateId = refinementGate.Id,
                Intent = ReviewIntent.RequestRefinement,
                Refinement = new DirectReviewRefinement
                {
                    Goal = "Narrow the checkout recommendation.",
                    RequestedChanges = ["Cover retry behavior only."]
                }
            });
        var priorRunCount = refinementHarness.Runner.Contexts.Count;
        await refinementHarness.Engine.RunAsync(
            refining.Id,
            CancellationToken.None);
        var iterationTwoContexts = refinementHarness.Runner.Contexts
            .Skip(priorRunCount)
            .ToList();
        var accountManagerIndex = iterationTwoContexts.FindIndex(item =>
            item.AgentId == "account-manager");
        var teamLeadIndex = iterationTwoContexts.FindIndex(item =>
            item.AgentId == "team-lead");
        var refined = await refinementHarness.LoadAsync(refining.Id);

        Assert.True(accountManagerIndex >= 0);
        Assert.True(teamLeadIndex > accountManagerIndex);
        Assert.Equal(originalWorkspace, refined.WorkspacePath);
        Assert.Equal(
            originalSnapshots,
            refined.AgentSnapshots
                .OrderBy(item => item.AgentId)
                .Select(item => (item.AgentId, item.DefinitionHash))
                .ToArray());
        Assert.Contains(
            refined.Steps,
            item =>
                item.Iteration == 2 &&
                item.PlanStepKey == WorkflowEngine.RefinementIntakePlanStepKey);
    }

    [Theory]
    [InlineData(RefinementIntakeFailure.Malformed)]
    [InlineData(RefinementIntakeFailure.WrongKind)]
    [InlineData(RefinementIntakeFailure.FalseConfirmation)]
    public async Task InvalidRefinementIntake_FailsVisibleStepAndCorrectedManualRetrySucceeds(
        RefinementIntakeFailure failure)
    {
        await using var harness = await Slice8Harness.CreateAsync(
            ParentState.AdvisoryReview);
        var flow = await harness.LoadAsync(harness.ParentId);
        var reviewGate = Assert.Single(flow.GateRecords);
        await harness.Reviews.ReviewAsync(
            flow.Id,
            new DirectReviewRequest
            {
                GateId = reviewGate.Id,
                Intent = ReviewIntent.RequestRefinement,
                Refinement = new DirectReviewRefinement
                {
                    Goal = "Narrow the checkout recommendation.",
                    RequestedChanges = ["Cover retry behavior only."]
                }
            });
        var beforeAttempt = await harness.LoadAsync(flow.Id);
        var customerMessagesBefore = beforeAttempt.Messages.Count(item =>
            item.Role == ConversationRole.Customer);
        harness.Runner.QueueRefinementIntakeOutput(
            Slice8Runner.RefinementIntakeOutput(failure));

        await harness.Engine.RunAsync(
            flow.Id,
            CancellationToken.None);

        var failed = await harness.LoadAsync(flow.Id);
        var failedStep = Assert.Single(
            failed.Steps,
            item =>
                item.Iteration == 2 &&
                item.PlanStepKey ==
                WorkflowEngine.RefinementIntakePlanStepKey &&
                item.Status == StepStatus.Failed);
        Assert.Equal(FlowStatus.Failed, failed.Status);
        Assert.Equal(AgentRunPhase.Failed, failedStep.Phase);
        Assert.False(string.IsNullOrWhiteSpace(
            failedStep.OutputSummary));
        Assert.False(string.IsNullOrWhiteSpace(
            failedStep.PushbackReason));
        Assert.Contains(
            failed.Events,
            item =>
                item.FlowStepId == failedStep.Id &&
                item.Type == "step.failed");
        Assert.DoesNotContain(
            failed.Events,
            item => item.Type == "flow.refinement-normalized");
        Assert.Equal(
            customerMessagesBefore,
            failed.Messages.Count(item =>
                item.Role == ConversationRole.Customer));

        var restarted = await harness.Engine.RestartFailedFlowAsync(
            flow.Id,
            CancellationToken.None);
        var retry = Assert.Single(
            restarted.Steps,
            item => item.Label == "Manual restart of Account Manager");
        Assert.Equal(FlowStatus.Queued, restarted.Status);
        Assert.Equal(
            ExecutionInvocationKind.Intake,
            retry.InvocationKind);
        Assert.Equal(
            WorkflowEngine.RefinementIntakePlanStepKey,
            retry.PlanStepKey);
        Assert.Equal(2, retry.Attempt);
        Assert.Equal(
            failedStep.StableSemanticRootId,
            retry.RetryOfStepId);

        harness.Runner.QueueRefinementIntakeOutput(
            Slice8Runner.RefinementIntakeOutput(
                RefinementIntakeFailure.None));
        await harness.Engine.RunAsync(
            flow.Id,
            CancellationToken.None);

        var corrected = await harness.LoadAsync(flow.Id);
        var completedRetry = corrected.Steps.Single(item =>
            item.Id == retry.Id);
        Assert.Equal(
            StepStatus.Completed,
            completedRetry.Status);
        Assert.NotEqual(
            failedStep.CopilotSessionId,
            completedRetry.CopilotSessionId);
        Assert.Equal(
            failed.WorkspacePath,
            corrected.WorkspacePath);
        Assert.Single(
            corrected.Events,
            item => item.Type == "flow.refinement-normalized");
        Assert.Equal(
            customerMessagesBefore,
            corrected.Messages.Count(item =>
                item.Role == ConversationRole.Customer));
        Assert.Equal(FlowStatus.WaitingForFeedback, corrected.Status);
    }

    private static FlowRun BareChild(FlowRun parent, FlowLinkKind linkKind) =>
        new()
        {
            Title = "Duplicate successor",
            OriginalRequest = "seed",
            ConsolidatedRequest = "seed",
            Kind = parent.Kind,
            Status = FlowStatus.Intake,
            ParentFlowRunId = parent.Id,
            ParentIteration = parent.Iteration,
            LinkKind = linkKind,
            RepositoryPath = parent.RepositoryPath,
            RepositoryKnowledge = parent.RepositoryKnowledge
        };

    private enum ParentState
    {
        QueuedMissingQualification,
        Blocked,
        AdvisoryReview,
        DeliveryReview
    }

    private enum PromotionIntakeBehavior
    {
        DirectConfirmed,
        DriftedConfirmed,
        AwaitingConfirmation
    }

    private enum LinkedJournalFixtureState
    {
        Missing,
        Completed,
        Interrupted
    }

    public enum RefinementIntakeFailure
    {
        None,
        Malformed,
        WrongKind,
        FalseConfirmation
    }

    private sealed class Slice8Harness : IAsyncDisposable
    {
        public static MissingQualification MissingQualification { get; } =
            new()
            {
                Summary =
                    "No enabled agent can assess jurisdiction-specific checkout rules.",
                Missing = ["Jurisdiction-specific compliance analysis"],
                WhyRequired =
                    "Incorrect operator guidance could expose the customer to regulatory risk.",
                SuggestedAgent = new SuggestedAgent
                {
                    Id = "compliance-analyst",
                    Name = "Compliance Analyst",
                    Description =
                        "Evaluates jurisdiction-specific product obligations."
                }
            };

        public const string ValidOutcomeJson =
            """{"Goal":"Accepted checkout goal","Summary":"SUMMARY MUST NOT MOVE","ImplementationDetails":["Implement retry behavior","Add bounded failure handling"],"Artifacts":[]}""";

        private readonly string _root;
        private readonly WorkflowDefinitionProvider _workflowProvider;
        private readonly HandoffGateEngine _gate;
        private readonly AgentCatalog _catalog;
        private readonly string _analystPath;

        private Slice8Harness(
            string root,
            Guid parentId,
            TestDbContextFactory factory,
            WorkflowDefinitionProvider workflowProvider,
            HandoffGateEngine gate,
            AgentCatalog catalog,
            string analystPath,
            Slice8Runner runner,
            Slice8WorkspaceManager workspace,
            MutableAdmission admission,
            FlowLifecycleCoordinator lifecycle,
            LinkedFlowCoordinator links,
            WorkflowEngine engine,
            QualificationResolutionCoordinator qualifications,
            ReviewCoordinator reviews)
        {
            _root = root;
            ParentId = parentId;
            Factory = factory;
            _workflowProvider = workflowProvider;
            _gate = gate;
            _catalog = catalog;
            _analystPath = analystPath;
            Runner = runner;
            Workspace = workspace;
            Admission = admission;
            Lifecycle = lifecycle;
            Links = links;
            Engine = engine;
            Qualifications = qualifications;
            Reviews = reviews;
        }

        public Guid ParentId { get; }

        public TestDbContextFactory Factory { get; }

        public Slice8Runner Runner { get; }

        public Slice8WorkspaceManager Workspace { get; }

        public MutableAdmission Admission { get; }

        public FlowLifecycleCoordinator Lifecycle { get; }

        public LinkedFlowCoordinator Links { get; }

        public WorkflowEngine Engine { get; }

        public QualificationResolutionCoordinator Qualifications { get; }

        public ReviewCoordinator Reviews { get; }

        public static async Task<Slice8Harness> CreateAsync(
            ParentState state,
            bool failBlockerExplanation = false,
            IModelRouter? modelRouterOverride = null)
        {
            var root = Path.Combine(
                Environment.CurrentDirectory,
                ".test-slice8",
                Guid.NewGuid().ToString("N")[..10]);
            var agentsPath = Path.Combine(root, ".github", "agents");
            var workspaceRoot = Path.Combine(root, "workspaces");
            Directory.CreateDirectory(agentsPath);
            Directory.CreateDirectory(workspaceRoot);
            var workflowPath = Path.Combine(root, "WORKFLOW.md");
            await File.WriteAllTextAsync(workflowPath, WorkflowText);
            await WriteAgentAsync(
                agentsPath,
                "account-manager",
                "Account Manager",
                "Normalizes customer requests.");
            await WriteAgentAsync(
                agentsPath,
                "team-lead",
                "Team Lead",
                "Plans the smallest suitable team.");
            await WriteAgentAsync(
                agentsPath,
                "pre-mortem-sceptic",
                "Pre-mortem Sceptic",
                "Reviews likely failures.");
            var analystPath = await WriteAgentAsync(
                agentsPath,
                "analyst",
                "Analyst",
                "Investigates repository evidence.");

            var databasePath = Path.Combine(root, "harness.db");
            var options = new DbContextOptionsBuilder<HarnessDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            var factory = new TestDbContextFactory(options);
            await using (var database = await factory.CreateDbContextAsync())
            {
                await database.Database.EnsureCreatedAsync();
                database.Settings.Add(new HarnessSettings
                {
                    RepositoryPath = root,
                    RepositoryKnowledge = "A configured checkout project.",
                    Outcome = OutcomeType.PullRequest,
                    MaxHandoffRetries = 0
                });
                await database.SaveChangesAsync();
            }

            var paths = new HarnessPaths(root, agentsPath, databasePath);
            var workflowProvider = new WorkflowDefinitionProvider(
                paths,
                new AiHarnessDemo.Core.Workflow.WorkflowLoader(),
                NullLogger<WorkflowDefinitionProvider>.Instance);
            _ = workflowProvider.GetEffective();
            var catalog = new AgentCatalog(paths, factory);
            var catalogStatus = await catalog.LoadAsync();
            Assert.True(catalogStatus.Ready, catalogStatus.LastError);
            var snapshots = new FlowAgentSnapshotService(factory, catalog);
            var flow = CreateParent(root, workspaceRoot, state);
            var gate = new HandoffGateEngine();
            gate.SetTrustLevel(HandoffActionType.Advance, HandoffTrustLevel.Auto);
            gate.SetTrustLevel(
                HandoffActionType.RequestRevision,
                HandoffTrustLevel.Auto);
            gate.SetTrustLevel(
                HandoffActionType.CustomerReview,
                HandoffTrustLevel.Gated);
            if (state is
                ParentState.AdvisoryReview or
                ParentState.DeliveryReview)
            {
                AttachReviewState(flow, gate);
            }
            await using (var database = await factory.CreateDbContextAsync())
            {
                database.Flows.Add(flow);
                snapshots.CaptureForNewFlow(database, flow);
                await database.SaveChangesAsync();
            }

            var runner = new Slice8Runner(
                failBlockerExplanation,
                state == ParentState.QueuedMissingQualification);
            var workspace = new Slice8WorkspaceManager(workspaceRoot);
            var admission = new MutableAdmission();
            var lifecycle = new FlowLifecycleCoordinator();
            var profileFactory = new BootstrapTaskProfileFactory();
            var queue = new FlowQueue();
            var modelRouter =
                modelRouterOverride ?? new FixedModelRouter();
            var routingRecorder = TestRoutingSupport.Recorder(factory);
            var intake = new IntakeCoordinator(
                factory,
                modelRouter,
                profileFactory,
                routingRecorder,
                runner,
                workspace,
                gate,
                new RepositoryContextGate(),
                queue,
                workflowProvider,
                admission,
                snapshots,
                lifecycle);
            intake.GitHubCliAvailableOverride = () => true;
            intake.GitHubAuthenticationAvailableOverride =
                _ => Task.FromResult(true);
            var linked = new LinkedFlowCoordinator(
                factory,
                snapshots,
                intake,
                lifecycle);
            var missing = new MissingQualificationCoordinator(
                factory,
                profileFactory,
                lifecycle);
            var qualifications = new QualificationResolutionCoordinator(
                factory,
                admission,
                linked,
                lifecycle);
            var reviews = new ReviewCoordinator(
                factory,
                gate,
                queue,
                lifecycle,
                workflowProvider,
                admission,
                linked);
            var engine = new WorkflowEngine(
                factory,
                catalog,
                modelRouter,
                profileFactory,
                routingRecorder,
                workspace,
                runner,
                gate,
                new CopilotSessionJournal(),
                workflowProvider,
                NullLogger<WorkflowEngine>.Instance,
                flowAgentSnapshotService: snapshots,
                teamPlanValidator: new TeamPlanValidator(),
                missingQualificationCoordinator: missing,
                lifecycleCoordinator: lifecycle,
                linkedFlowCoordinator: linked);
            return new Slice8Harness(
                root,
                flow.Id,
                factory,
                workflowProvider,
                gate,
                catalog,
                analystPath,
                runner,
                workspace,
                admission,
                lifecycle,
                linked,
                engine,
                qualifications,
                reviews);
        }

        public async Task ChangeCatalogAsync()
        {
            await File.AppendAllTextAsync(
                _analystPath,
                $"{Environment.NewLine}Use the newly repaired qualification.");
            var status = await _catalog.ReloadAsync();
            Assert.True(status.Ready, status.LastError);
        }

        public async Task<FlowRun> LoadAsync(Guid flowId)
        {
            await using var database = await Factory.CreateDbContextAsync();
            return await database.Flows
                .AsNoTracking()
                .AsSplitQuery()
                .Include(item => item.Steps)
                .ThenInclude(item => item.ToolCalls)
                .Include(item => item.Messages)
                .Include(item => item.Events)
                .Include(item => item.GateRecords)
                .Include(item => item.AgentSnapshots)
                .Include(item => item.PlanDocuments)
                .Include(item => item.TaskProfiles)
                .Include(item => item.LinkedFlowRuns)
                .SingleAsync(item => item.Id == flowId);
        }

        public async Task UpdateParentAsync(Action<FlowRun> update)
        {
            await using var database = await Factory.CreateDbContextAsync();
            var flow = await database.Flows.SingleAsync(item =>
                item.Id == ParentId);
            update(flow);
            await database.SaveChangesAsync();
        }

        public async Task<Guid> CreateSeedOnlyPromotionChildAsync(
            bool persistSeedMessage = false)
        {
            await using var database = await Factory.CreateDbContextAsync();
            var parent = await database.Flows.SingleAsync(item =>
                item.Id == ParentId);
            var settings = await database.Settings
                .AsNoTracking()
                .SingleAsync();
            var outcome = FlowOutcomeParser.ParseJson(
                ValidOutcomeJson).Document;
            var seed = LinkedFlowCoordinator.SerializePromotionSeed(
                outcome);
            var child = Links.CreateTrackedSuccessor(
                database,
                parent,
                FlowLinkKind.AdvisoryPromotion,
                FlowKind.Delivery,
                settings.Outcome,
                "Implement accepted checkout goal",
                seed,
                settings);
            if (persistSeedMessage)
            {
                child.Messages.Add(new FlowMessage
                {
                    FlowRunId = child.Id,
                    Role = ConversationRole.Customer,
                    Content = seed
                });
            }
            await database.SaveChangesAsync();
            return child.Id;
        }

        public async Task<(Guid FlowId, Guid StepId)>
            CreatePersistedLinkedIntakeAsync(
                StepStatus status,
                LinkedJournalFixtureState journalState,
                bool persistTaskProfile,
                FlowLinkKind linkKind =
                    FlowLinkKind.AdvisoryPromotion)
        {
            if (status is not (StepStatus.Pending or StepStatus.Running))
            {
                throw new ArgumentOutOfRangeException(nameof(status));
            }
            await using var database = await Factory.CreateDbContextAsync();
            var parent = await database.Flows.SingleAsync(item =>
                item.Id == ParentId);
            var settings = await database.Settings
                .AsNoTracking()
                .SingleAsync();
            var seed = linkKind == FlowLinkKind.AdvisoryPromotion
                ? LinkedFlowCoordinator.SerializePromotionSeed(
                    FlowOutcomeParser.ParseJson(
                        ValidOutcomeJson).Document)
                : $"{linkKind}: continue with the repaired linked intake.";
            var child = Links.CreateTrackedSuccessor(
                database,
                parent,
                linkKind,
                linkKind == FlowLinkKind.AdvisoryPromotion
                    ? FlowKind.Delivery
                    : FlowKind.Advisory,
                linkKind == FlowLinkKind.AdvisoryPromotion
                    ? settings.Outcome
                    : OutcomeType.None,
                "Recover linked intake",
                seed,
                settings);
            var workspace = await Workspace.PrepareAsync(child);
            child.WorkspacePath = workspace.Path;
            child.BranchName = workspace.BranchName;
            child.Messages.Add(new FlowMessage
            {
                FlowRunId = child.Id,
                Role = ConversationRole.Customer,
                Content = seed
            });
            var accountManager = child.AgentSnapshots.Single(item =>
                item.Role == "account-manager" &&
                item.EnabledAtSnapshot);
            var sessionId = AgentSessionIdentity.Create(
                child.Id,
                child.Iteration,
                accountManager.AgentId,
                IntakeCoordinator.InitialLinkedIntakePlanStepKey);
            var copilotHome = Path.Combine(
                _root,
                "linked-intake-home-" + child.Id.ToString("N"));
            var startedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
            var step = new FlowStep
            {
                FlowRunId = child.Id,
                Iteration = child.Iteration,
                Sequence = -99,
                AgentId = accountManager.AgentId,
                AgentName = accountManager.Name,
                AgentRole = accountManager.Role,
                Label = "Review customer intake",
                PlanStepKey =
                    IntakeCoordinator.InitialLinkedIntakePlanStepKey,
                PlanDutiesJson = """["Analyze"]""",
                PlanStage = PlanStage.BeforeReview,
                InvocationKind = ExecutionInvocationKind.Intake,
                PermissionProfile =
                    ExecutionPermissionProfile.ReadOnlySource,
                WorkflowRevision =
                    _workflowProvider.GetEffective().Revision,
                Status = status,
                Phase = status == StepStatus.Running
                    ? AgentRunPhase.StreamingTurn
                    : AgentRunPhase.BuildingPrompt,
                Model = status == StepStatus.Running
                    ? "fixture-model"
                    : string.Empty,
                ModelEffort = status == StepStatus.Running
                    ? "medium"
                    : string.Empty,
                Attempt = 1,
                StartedAt = startedAt,
                InputSummary = seed,
                CopilotSessionId = status == StepStatus.Running
                    ? sessionId
                    : null,
                CopilotSessionHome = status == StepStatus.Running
                    ? copilotHome
                    : string.Empty
            };
            step.StableSemanticRootId = step.Id;
            child.Steps.Add(step);
            if (persistTaskProfile)
            {
                database.TaskProfiles.Add(
                    new BootstrapTaskProfileFactory().Create(
                        accountManager.Role,
                        seed,
                        child.Id,
                        child.Iteration,
                        step.Id,
                        step.PlanStepKey,
                        accountManager.AgentId));
            }
            await database.SaveChangesAsync();

            if (status == StepStatus.Running &&
                journalState != LinkedJournalFixtureState.Missing)
            {
                var sessionDirectory = Path.Combine(
                    copilotHome,
                    "session-state",
                    sessionId.ToString("D"));
                Directory.CreateDirectory(sessionDirectory);
                var journalStarted = startedAt.AddSeconds(1);
                var lines = new List<string>
                {
                    RecoveryFixture.Serialize(
                        "session.start",
                        journalStarted,
                        new
                        {
                            sessionId,
                            context = new
                            {
                                cwd = child.WorkspacePath
                            }
                        }),
                    RecoveryFixture.Serialize(
                        "subagent.selected",
                        journalStarted.AddSeconds(1),
                        new
                        {
                            agentName = accountManager.Name,
                            agentDisplayName = accountManager.Name
                        }),
                    RecoveryFixture.Serialize(
                        "assistant.turn_start",
                        journalStarted.AddSeconds(2),
                        new
                        {
                            turnId = "0"
                        })
                };
                if (journalState == LinkedJournalFixtureState.Completed)
                {
                    var output =
                        Slice8Runner.PromotionIntakeOutput(
                            AdvisoryPromotionSeedParser.Parse(seed),
                            PromotionIntakeBehavior.DirectConfirmed);
                    lines.Add(RecoveryFixture.Serialize(
                        "assistant.message",
                        journalStarted.AddSeconds(3),
                        new
                        {
                            turnId = "0",
                            content = output,
                            toolRequests = Array.Empty<object>()
                        }));
                    lines.Add(RecoveryFixture.Serialize(
                        "assistant.turn_end",
                        journalStarted.AddSeconds(4),
                        new
                        {
                            turnId = "0"
                        }));
                    lines.Add(RecoveryFixture.Serialize(
                        "session.shutdown",
                        journalStarted.AddSeconds(5),
                        new
                        {
                            shutdownType = "routine"
                        }));
                }
                else
                {
                    lines.Add(RecoveryFixture.Serialize(
                        "session.shutdown",
                        journalStarted.AddSeconds(3),
                        new
                        {
                            shutdownType = "error"
                        }));
                }
                await File.WriteAllLinesAsync(
                    Path.Combine(sessionDirectory, "events.jsonl"),
                    lines);
            }
            return (child.Id, step.Id);
        }

        public async Task<(Guid StepId, string StagedRoot)>
            SeedRecoveredBlockerExplanationAsync(
                string journalOutput)
        {
            await using var database =
                await Factory.CreateDbContextAsync();
            var flow = await database.Flows
                .Include(item => item.Steps)
                .SingleAsync(item => item.Id == ParentId);
            flow.Status = FlowStatus.Running;
            flow.CurrentBlockerCode =
                MissingQualificationCoordinator.BlockerCode;
            flow.CurrentBlockerSummary =
                MissingQualification.Summary;
            flow.CurrentBlockerDataJson =
                MissingQualificationCoordinator.SerializeBlocker(
                    MissingQualification);
            var step = new FlowStep
            {
                FlowRunId = flow.Id,
                Iteration = flow.Iteration,
                Sequence = flow.Steps
                    .Select(item => item.Sequence)
                    .DefaultIfEmpty(10)
                    .Max() + 10,
                AgentId = "account-manager",
                AgentName = "Account Manager",
                AgentRole = "account-manager",
                Label = "Explain missing qualification",
                PlanStepKey =
                    MissingQualificationCoordinator
                        .AccountManagerPlanStepKey,
                PlanDutiesJson = """["Analyze"]""",
                PlanStage = PlanStage.BeforeReview,
                InvocationKind =
                    ExecutionInvocationKind.BlockerExplanation,
                PermissionProfile =
                    ExecutionPermissionProfile.ReadOnlySource,
                WorkflowRevision =
                    _workflowProvider.GetEffective().Revision,
                Status = StepStatus.Running,
                Phase = AgentRunPhase.StreamingTurn,
                Attempt = 1,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                InputSummary =
                    "Explain the persisted missing qualification."
            };
            step.StableSemanticRootId = step.Id;
            var sessionId = AgentSessionIdentity.Create(
                flow.Id,
                flow.Iteration,
                step.AgentId,
                step.PlanStepKey);
            var copilotHome = Path.Combine(
                _root,
                "blocker-copilot-home");
            step.CopilotSessionId = sessionId;
            step.CopilotSessionHome = copilotHome;
            flow.Steps.Add(step);
            await database.SaveChangesAsync();

            var sessionDirectory = Path.Combine(
                copilotHome,
                "session-state",
                sessionId.ToString("D"));
            Directory.CreateDirectory(sessionDirectory);
            var started = DateTimeOffset.UtcNow.AddMinutes(-4);
            await File.WriteAllLinesAsync(
                Path.Combine(sessionDirectory, "events.jsonl"),
                [
                    RecoveryFixture.Serialize(
                        "session.start",
                        started,
                        new
                        {
                            sessionId,
                            context = new
                            {
                                cwd = flow.WorkspacePath
                            }
                        }),
                    RecoveryFixture.Serialize(
                        "subagent.selected",
                        started.AddSeconds(1),
                        new
                        {
                            agentName = "Account Manager",
                            agentDisplayName = "Account Manager"
                        }),
                    RecoveryFixture.Serialize(
                        "assistant.turn_start",
                        started.AddSeconds(2),
                        new { turnId = "0" }),
                    RecoveryFixture.Serialize(
                        "assistant.message",
                        started.AddSeconds(3),
                        new
                        {
                            turnId = "0",
                            content = journalOutput,
                            toolRequests = Array.Empty<object>()
                        }),
                    RecoveryFixture.Serialize(
                        "assistant.turn_end",
                        started.AddSeconds(4),
                        new { turnId = "0" }),
                    RecoveryFixture.Serialize(
                        "session.shutdown",
                        started.AddSeconds(5),
                        new { shutdownType = "routine" })
                ]);
            var stagedRoot = AgentManifestStager.GetSessionRoot(
                copilotHome,
                sessionId);
            Directory.CreateDirectory(stagedRoot);
            await File.WriteAllTextAsync(
                Path.Combine(stagedRoot, "sensitive-marker.txt"),
                journalOutput);
            return (step.Id, stagedRoot);
        }

        public async Task<Guid> AttachCurrentReviewAsync()
        {
            await using var database = await Factory.CreateDbContextAsync();
            var flow = await database.Flows
                .Include(item => item.Steps)
                .Include(item => item.GateRecords)
                .SingleAsync(item => item.Id == ParentId);
            Assert.Equal(2, flow.Iteration);
            var owner = new FlowStep
            {
                FlowRunId = flow.Id,
                Iteration = flow.Iteration,
                Sequence = flow.Steps
                    .Where(item => item.Iteration == flow.Iteration)
                    .Select(item => item.Sequence)
                    .DefaultIfEmpty(10)
                    .Max() + 10,
                AgentId = "analyst",
                AgentName = "Analyst",
                AgentRole = "analyst",
                Label = "Prepare the refined outcome",
                PlanStepKey = "refined-outcome",
                PlanDutiesJson = """["PrepareOutcome"]""",
                PlanStage = PlanStage.BeforeReview,
                InvocationKind = ExecutionInvocationKind.Worker,
                IsOutcomeOwner = true,
                PermissionProfile =
                    ExecutionPermissionProfile.ReadOnlySource,
                Status = StepStatus.Completed,
                Phase = AgentRunPhase.Succeeded,
                OutputSummary = ValidOutcomeJson,
                CompletedAt = DateTimeOffset.UtcNow
            };
            owner.StableSemanticRootId = owner.Id;
            var gate = new HandoffGateRecord
            {
                FlowRunId = flow.Id,
                FlowStepId = owner.Id,
                ActionType = HandoffActionType.CustomerReview,
                Decision =
                    HandoffGateDecision.AwaitingHumanApproval,
                TrustLevelAtDecision = HandoffTrustLevel.Gated,
                Summary = "Review the refined recommendation.",
                Resolved = false
            };
            flow.Steps.Add(owner);
            flow.GateRecords.Add(gate);
            flow.OutcomeOwnerPlanStepKey = owner.PlanStepKey;
            flow.OutcomeContractJson = ValidOutcomeJson;
            flow.Status = FlowStatus.WaitingForFeedback;
            flow.UpdatedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync();
            return gate.Id;
        }

        public ValueTask DisposeAsync()
        {
            _workflowProvider.Dispose();
            _gate.Dispose();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
            return ValueTask.CompletedTask;
        }

        private static FlowRun CreateParent(
            string root,
            string workspaceRoot,
            ParentState state)
        {
            var flow = new FlowRun
            {
                Title = "Assess checkout resilience",
                OriginalRequest = "RAW ADVISORY DIALOGUE SECRET",
                ConsolidatedRequest =
                    """{"Goal":"Assess checkout resilience.","Details":["Inspect checkout retry behavior."],"SuccessCriteria":["Provide an evidence-based recommendation."],"Constraints":["Do not change source files."],"Assumptions":[]}""",
                Kind = state == ParentState.DeliveryReview
                    ? FlowKind.Delivery
                    : FlowKind.Advisory,
                Status = state switch
                {
                    ParentState.QueuedMissingQualification => FlowStatus.Queued,
                    ParentState.Blocked => FlowStatus.Blocked,
                    ParentState.AdvisoryReview or
                        ParentState.DeliveryReview =>
                        FlowStatus.WaitingForFeedback,
                    _ => throw new ArgumentOutOfRangeException(nameof(state))
                },
                RepositoryPath = root,
                RepositoryKnowledge = "A configured checkout project.",
                WorkspacePath = Path.Combine(
                    workspaceRoot,
                    "parent-" + Guid.NewGuid().ToString("N")),
                Outcome = state == ParentState.DeliveryReview
                    ? OutcomeType.PullRequest
                    : OutcomeType.None
            };
            Directory.CreateDirectory(flow.WorkspacePath);
            if (state == ParentState.Blocked)
            {
                flow.CurrentBlockerCode =
                    MissingQualificationCoordinator.BlockerCode;
                flow.CurrentBlockerSummary = MissingQualification.Summary;
                flow.CurrentBlockerDataJson =
                    MissingQualificationCoordinator.SerializeBlocker(
                        MissingQualification);
                flow.CustomerBlockerMessage =
                    MissingQualificationCoordinator.SafeFallbackMessage;
            }
            return flow;
        }

        private static void AttachReviewState(
            FlowRun flow,
            HandoffGateEngine gateEngine)
        {
            flow.OutcomeOwnerPlanStepKey = "answer";
            flow.OutcomeContractJson = ValidOutcomeJson;
            var owner = new FlowStep
            {
                FlowRunId = flow.Id,
                Iteration = 1,
                Sequence = 20,
                AgentId = "analyst",
                AgentName = "Analyst",
                AgentRole = "analyst",
                Label = "Prepare advisory result",
                PlanStepKey = "answer",
                PlanDutiesJson = """["Analyze","PrepareOutcome"]""",
                IsOutcomeOwner = true,
                PermissionProfile = ExecutionPermissionProfile.ReadOnlySource,
                Status = StepStatus.Completed,
                Phase = AgentRunPhase.Succeeded,
                CopilotSessionId = Guid.NewGuid(),
                OutputSummary = "RAW OWNER OUTPUT",
                ToolCalls =
                [
                    new AgentToolCall
                    {
                        ToolName = "view",
                        ArgumentsSummary = "secret-parent-argument",
                        Succeeded = true
                    }
                ]
            };
            flow.Steps.Add(owner);
            flow.Messages.Add(new FlowMessage
            {
                FlowRunId = flow.Id,
                Role = ConversationRole.Customer,
                Content = "RAW ADVISORY DIALOGUE SECRET"
            });
            flow.Events.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = owner.Id,
                Type = "parent.secret-event",
                Message = "RAW EVENT SECRET",
                DataJson = """{"secret":true}"""
            });
            if (flow.Kind == FlowKind.Delivery)
            {
                flow.Events.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = owner.Id,
                    Type = ReviewedCandidateLedger.EventType,
                    Message = "Sealed the Delivery review fixture.",
                    DataJson = ReviewedCandidateLedger.Serialize(
                        new ReviewedCandidateIdentity(
                            flow.Id,
                            flow.Iteration,
                            owner.Id,
                            owner.PlanStepKey,
                            OutcomeVerificationRules.ComputeSha256(
                                flow.OutcomeContractJson),
                            OutcomeVerificationRules.ComputeSha256(
                                $"plan:{flow.Id:D}"),
                            OutcomeVerificationRules.ComputeSha256(
                                $"candidate:{flow.Id:D}"),
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
            }
            flow.PlanDocuments.Add(new FlowPlanDocument
            {
                FlowRunId = flow.Id,
                Iteration = 1,
                Disposition = TeamPlanDisposition.Planned.ToString(),
                RawJson = TeamPlanParser.Serialize(AdvisoryPlan())
            });
            flow.GateRecords.Add(gateEngine.SubmitProposal(
                new HandoffProposal
                {
                    FlowRunId = flow.Id,
                    FlowStepId = owner.Id,
                    ActionType = HandoffActionType.CustomerReview,
                    Summary = "Review the Advisory result.",
                    Evidence = "Current outcome owner completed.",
                    BlastRadius = HandoffBlastRadius.High
                }));
        }

        private static TeamPlanDocument AdvisoryPlan() =>
            new()
            {
                Disposition = TeamPlanDisposition.Planned,
                Steps =
                [
                    new TeamPlanStep
                    {
                        Id = "answer",
                        AgentId = "analyst",
                        Order = 10,
                        Stage = PlanStage.BeforeReview,
                        Assignment = "Prepare the refined advisory answer.",
                        Justification = "The requested refinement requires evidence.",
                        DependsOn = [],
                        Duties = [PlanDuty.Analyze, PlanDuty.PrepareOutcome],
                        OutcomeOwner = true,
                        TaskProfile = new TeamPlanTaskProfile
                        {
                            Complexity = 4,
                            ReasoningDepth = 5,
                            ContextDemand = 5,
                            ToolIntensity = 2,
                            TaskTypeTags = [TaskTypeTag.Feedback],
                            Risk = TaskRisk.Low,
                            RiskReason = "The work is read-only.",
                            Confidence = 0.9,
                            Rationales = ["Repository evidence is available."]
                        }
                    }
                ],
                PreMortemCheckpoints = [],
                MissingQualification = null
            };

        private static async Task<string> WriteAgentAsync(
            string directory,
            string id,
            string name,
            string description)
        {
            var path = Path.Combine(directory, $"{id}.agent.md");
            await File.WriteAllTextAsync(
                path,
                $"""
                 ---
                 name: {name}
                 description: {description}
                 role: {id}
                 ---

                 Complete the assigned task.
                 """);
            return path;
        }

        private const string WorkflowText = """
            ---
            tracker:
              kind: voice
              active_states: [Intake, Queued, Running, Reworking, WaitingForFeedback, Blocked]
              terminal_states: [Approved, Abandoned, Failed]
            workspace:
              root: workspaces
            hooks:
              timeout_ms: 60000
            agent:
              max_concurrent_agents: 1
              max_turns: 20
              max_attempts: 1
            copilot:
              command: copilot
              turn_timeout_ms: 120000
              stall_timeout_ms: 30000
              maximum_quality_stall_timeout_ms: 30000
            studio:
              planning:
                max_steps: 24
                max_dependencies_per_step: 8
                max_assignment_characters: 4000
              flow_kinds:
                advisory:
                  required_duties: [PrepareOutcome]
                  maximum_permission: ReadOnlySource
                delivery:
                  required_duties: [Implement, Verify, PrepareOutcome, Publish]
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
            """;
    }

    private sealed class Slice8Runner(
        bool failBlockerExplanation,
        bool returnMissingQualification)
        : IAgentRunner
    {
        private readonly Lock _lock = new();
        private readonly Queue<string> _refinementIntakeOutputs = new();

        public List<AgentExecutionContext> Contexts { get; } = [];

        public PromotionIntakeBehavior PromotionBehavior { get; set; } =
            PromotionIntakeBehavior.DirectConfirmed;

        public void QueueRefinementIntakeOutput(string output)
        {
            lock (_lock)
            {
                _refinementIntakeOutputs.Enqueue(output);
            }
        }

        public Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                Contexts.Add(context);
            }
            if (context.AgentId == "team-lead")
            {
                var plan = returnMissingQualification
                    ? new TeamPlanDocument
                    {
                        Disposition =
                            TeamPlanDisposition.MissingQualification,
                        Steps = [],
                        PreMortemCheckpoints = [],
                        MissingQualification =
                            Slice8Harness.MissingQualification
                    }
                    : GetAdvisoryPlan();
                return Success(
                    "HANDOFF_STATUS: COMPLETE" +
                    Environment.NewLine +
                    TeamPlanParser.BeginSentinel +
                    Environment.NewLine +
                    TeamPlanParser.Serialize(plan) +
                    Environment.NewLine +
                    TeamPlanParser.EndSentinel);
            }
            if (context.AgentId == "account-manager" &&
                context.Task.Contains(
                    "DURABLE_ADVISORY_PROMOTION_AUTHORIZATION",
                    StringComparison.Ordinal))
            {
                return Success(PromotionIntakeOutput(
                    context,
                    PromotionBehavior));
            }
            if (context.AgentId == "account-manager" &&
                context.Task.Contains(
                    "Private operator qualification data",
                    StringComparison.Ordinal))
            {
                if (failBlockerExplanation)
                {
                    throw new AgentRunException(
                        "Account Manager fixture failed.",
                        AgentRunFailureKind.InvalidOutput);
                }
                return Success(IntakeOutput(
                    IntakeStatus.NeedsClarification,
                    flowKind: null,
                    "The current team needs a safer path before continuing. Please revise the scope or retry after the available expertise is updated."));
            }
            if (context.AgentId == "account-manager" &&
                context.Task.Contains(
                    "Normalize the customer's explicit refinement",
                    StringComparison.Ordinal))
            {
                lock (_lock)
                {
                    if (_refinementIntakeOutputs.TryDequeue(
                            out var refinement))
                    {
                        return Success(refinement);
                    }
                }
                return Success(IntakeOutput(
                    IntakeStatus.Confirmed,
                    FlowKind.Advisory,
                    "I normalized the requested refinement for the team.",
                    "Narrow the checkout recommendation."));
            }
            if (context.AgentId == "account-manager")
            {
                return Success(IntakeOutput(
                    IntakeStatus.AwaitingConfirmation,
                    context.Task.Contains(
                        "QualificationRosterRepair",
                        StringComparison.Ordinal)
                        ? FlowKind.Advisory
                        : FlowKind.Delivery,
                    "Please confirm that you want the team to continue with this implementation.",
                    "Implement the accepted recommendation."));
            }

            return Success(
                """
                HANDOFF_STATUS: COMPLETE

                ## Decision
                Complete.

                ## Deliverable
                The refined result is ready.

                ## Evidence
                Repository evidence was reviewed.

                ## Next owner
                Return the result for customer review.
                """ +
                Environment.NewLine +
                FlowOutcomeParser.BeginSentinel +
                Environment.NewLine +
                """{"Goal":"Narrow the checkout recommendation.","Summary":"The refined recommendation is ready.","ImplementationDetails":["Cover retry behavior only."],"Artifacts":[]}""" +
                Environment.NewLine +
                FlowOutcomeParser.EndSentinel);
        }

        internal static string RefinementIntakeOutput(
            RefinementIntakeFailure failure) =>
            failure switch
            {
                RefinementIntakeFailure.Malformed =>
                    "MALFORMED_REFINEMENT_OUTPUT",
                RefinementIntakeFailure.WrongKind =>
                    IntakeOutput(
                        IntakeStatus.Confirmed,
                        FlowKind.Delivery,
                        "I changed the flow kind incorrectly.",
                        "Narrow the checkout recommendation."),
                RefinementIntakeFailure.FalseConfirmation =>
                    IntakeOutput(
                        IntakeStatus.AwaitingConfirmation,
                        FlowKind.Advisory,
                        "Please confirm the refinement again.",
                        "Narrow the checkout recommendation."),
                RefinementIntakeFailure.None =>
                    IntakeOutput(
                        IntakeStatus.Confirmed,
                        FlowKind.Advisory,
                        "I normalized the requested refinement for the team.",
                        "Narrow the checkout recommendation."),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(failure),
                    failure,
                    null)
            };

        private static Task<AgentExecutionResult> Success(string output) =>
            Task.FromResult(new AgentExecutionResult(
                output,
                "Fixture evidence.",
                1,
                []));

        private static string IntakeOutput(
            IntakeStatus status,
            FlowKind? flowKind,
            string reply,
            string goal = "") =>
            "HANDOFF_STATUS: COMPLETE" +
            Environment.NewLine +
            IntakeParser.BeginSentinel +
            Environment.NewLine +
            IntakeParser.Serialize(new IntakeDocument
            {
                Status = status,
                FlowKind = flowKind,
                TaskTitle = "Implement accepted recommendation",
                CustomerReply = reply,
                Brief = new IntakeBrief
                {
                    Goal = goal,
                    Details = string.IsNullOrWhiteSpace(goal)
                        ? []
                        : ["Carry forward the bounded requested scope."],
                    SuccessCriteria = string.IsNullOrWhiteSpace(goal)
                        ? []
                        : ["The requested outcome is addressed."],
                    Constraints = [],
                    Assumptions = []
                }
            }) +
            Environment.NewLine +
            IntakeParser.EndSentinel;

        internal static string PromotionIntakeOutput(
            AgentExecutionContext context,
            PromotionIntakeBehavior behavior)
        {
            var seed = AdvisoryPromotionSeedParser.Parse(
                context.PromotionContext?.CanonicalSeedJson ??
                LinkedFlowCoordinator.SerializePromotionSeed(
                    FlowOutcomeParser.ParseJson(
                        Slice8Harness.ValidOutcomeJson).Document));
            return PromotionIntakeOutput(seed, behavior);
        }

        internal static string PromotionIntakeOutput(
            AdvisoryPromotionSeed seed,
            PromotionIntakeBehavior behavior)
        {
            var details = seed.ImplementationDetails!.ToArray();
            if (behavior == PromotionIntakeBehavior.DriftedConfirmed)
            {
                var driftIndex = details.Length / 2;
                details[driftIndex] =
                    details[driftIndex][..^1] +
                    (details[driftIndex][^1] == 'x' ? 'y' : 'x');
            }
            return
            "HANDOFF_STATUS: COMPLETE" +
            Environment.NewLine +
            IntakeParser.BeginSentinel +
            Environment.NewLine +
            IntakeParser.Serialize(new IntakeDocument
            {
                Status = behavior ==
                         PromotionIntakeBehavior.AwaitingConfirmation
                    ? IntakeStatus.AwaitingConfirmation
                    : IntakeStatus.Confirmed,
                FlowKind = FlowKind.Delivery,
                TaskTitle = "Implement accepted checkout goal",
                CustomerReply = behavior ==
                                PromotionIntakeBehavior.AwaitingConfirmation
                    ? "Please confirm the accepted implementation scope."
                    : "The accepted implementation scope is queued.",
                Brief = new IntakeBrief
                {
                    Goal = seed.Goal,
                    Details = details,
                    SuccessCriteria = [],
                    Constraints = [],
                    Assumptions = []
                }
            }) +
            Environment.NewLine +
            IntakeParser.EndSentinel;
        }

        private static TeamPlanDocument GetAdvisoryPlan() =>
            new()
            {
                Disposition = TeamPlanDisposition.Planned,
                Steps =
                [
                    new TeamPlanStep
                    {
                        Id = "answer",
                        AgentId = "analyst",
                        Order = 10,
                        Stage = PlanStage.BeforeReview,
                        Assignment = "Prepare the refined advisory answer.",
                        Justification = "The refinement requires evidence.",
                        DependsOn = [],
                        Duties = [PlanDuty.Analyze, PlanDuty.PrepareOutcome],
                        OutcomeOwner = true,
                        TaskProfile = new TeamPlanTaskProfile
                        {
                            Complexity = 4,
                            ReasoningDepth = 5,
                            ContextDemand = 5,
                            ToolIntensity = 2,
                            TaskTypeTags = [TaskTypeTag.Feedback],
                            Risk = TaskRisk.Low,
                            RiskReason = "Read-only analysis.",
                            Confidence = 0.9,
                            Rationales = ["Repository evidence is available."]
                        }
                    }
                ],
                PreMortemCheckpoints = [],
                MissingQualification = null
            };
    }

    private sealed class Slice8WorkspaceManager(string root) : IWorkspaceManager
    {
        public Task<WorkspaceInfo> PrepareAsync(
            FlowRun flow,
            CancellationToken cancellationToken = default)
        {
            var path = string.IsNullOrWhiteSpace(flow.WorkspacePath)
                ? Path.Combine(root, flow.Id.ToString("N"))
                : flow.WorkspacePath;
            Directory.CreateDirectory(path);
            return Task.FromResult(new WorkspaceInfo(
                path,
                flow.Kind == FlowKind.Delivery
                    ? $"slice8/{flow.Id:N}"
                    : string.Empty,
                CreatedNow: string.IsNullOrWhiteSpace(flow.WorkspacePath),
                Mode: flow.Kind == FlowKind.Delivery
                    ? WorkspaceMode.Delivery
                    : WorkspaceMode.AdvisoryReadOnly));
        }

        public Task<WorkspaceCleanupResult> RemoveAsync(
            FlowRun flow,
            CancellationToken cancellationToken = default)
        {
            if (Directory.Exists(flow.WorkspacePath))
            {
                Directory.Delete(flow.WorkspacePath, recursive: true);
            }
            return Task.FromResult(WorkspaceCleanupResult.Empty);
        }
    }

    private sealed class MutableAdmission : INewWorkAdmissionService
    {
        public bool Ready { get; set; } = true;

        public Task EnsureReadyAsync(
            CancellationToken cancellationToken = default)
        {
            if (!Ready)
            {
                throw new NewWorkAdmissionException(
                    ["The current catalog is not ready."]);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class UnavailableModelRouter : IModelRouter
    {
        public Task<RoutingDecision> SelectAsync(
            RoutingRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The current model catalog is unavailable.");
    }

    private sealed class NoOpExecutionController : IFlowExecutionController
    {
        public Task<bool> CancelAsync(
            Guid flowId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class NoOpProcessCleaner : IWorkspaceProcessCleaner
    {
        public Task<WorkspaceProcessCleanupResult> StopAsync(
            string workspacePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(WorkspaceProcessCleanupResult.Empty);
    }

    private sealed class NoOpSessionCleaner : IFlowSessionCleaner
    {
        public Task<int> DeleteAsync(
            FlowRun flow,
            IReadOnlyCollection<FlowStep> steps,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    public sealed class TestDbContextFactory(
        DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);

        public Task<HarnessDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HarnessDbContext(options));
    }
}

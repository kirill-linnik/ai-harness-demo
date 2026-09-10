using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Security;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Data;
using AiHarnessDemo.Services;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Tests;

/// <summary>
/// Covers the authoritative pre-publication authorization (QA AC-010) and the durable
/// per-repository reviewed publication state machine (QA AC-018). Every external command is served
/// by a scripted runner so the tests can assert exactly which side effects ran - including none.
/// </summary>
public sealed class ReviewedPublicationJournalTests
{
    private const string RemoteRepository = "example/repository";

    private const string BranchName = "ai-harness/reviewed-publication";

    [Fact]
    public async Task PublicationWithoutCurrentApprovedReview_LeavesNoTokenProcessOrJournalTrace()
    {
        using var scenario = await ReviewedPublicationScenario.CreateAsync(
            OutcomeType.PullRequest);
        await scenario.RemoveCustomerReviewGatesAsync();

        // 1. No customer review at all.
        await AssertRejectedWithoutSideEffectsAsync(
            scenario,
            "resolved, approved customer review");

        // 2. An unresolved review never authorizes.
        await scenario.ReplaceCustomerReviewGateAsync(
            scenario.OutcomeOwner.Id,
            resolved: false,
            approved: null,
            decision: null);
        await AssertRejectedWithoutSideEffectsAsync(
            scenario,
            "resolved, approved customer review");

        // 3. A resolved review that did not accept never authorizes.
        await scenario.ReplaceCustomerReviewGateAsync(
            scenario.OutcomeOwner.Id,
            resolved: true,
            approved: false,
            decision: ReviewDecision.RefinementRequested);
        await AssertRejectedWithoutSideEffectsAsync(
            scenario,
            "resolved, approved customer review");

        // 4. An accepted review bound to a superseded iteration never authorizes.
        var staleOwnerId = await scenario.AddStaleIterationOutcomeOwnerAsync();
        await scenario.ReplaceCustomerReviewGateAsync(
            staleOwnerId,
            resolved: true,
            approved: true,
            decision: ReviewDecision.Accepted);
        await AssertRejectedWithoutSideEffectsAsync(
            scenario,
            "resolved, approved customer review");

        // 5. An accepted review bound to an outcome owner that never completed cannot authorize.
        await scenario.ReplaceCustomerReviewGateAsync(
            scenario.OutcomeOwner.Id,
            resolved: true,
            approved: true,
            decision: ReviewDecision.Accepted);
        await scenario.SetOutcomeOwnerStatusAsync(StepStatus.Running);
        await AssertRejectedWithoutSideEffectsAsync(
            scenario,
            "current completed Delivery outcome owner");

        // The scenario is otherwise publishable, so the rejections above are not false positives.
        await scenario.SetOutcomeOwnerStatusAsync(StepStatus.Completed);
        var report = await scenario.PublishAsync();

        Assert.Contains(scenario.Identity.Fingerprint, report);
        Assert.NotEmpty(scenario.Runner.Invocations);
    }

    [Fact]
    public async Task CallerSuppliedFlowAndGates_NeverAuthorizePublication()
    {
        using var scenario = await ReviewedPublicationScenario.CreateAsync(
            OutcomeType.PullRequest);
        await scenario.RemoveCustomerReviewGatesAsync();
        var forged = new FlowRun
        {
            Id = scenario.Flow.Id,
            Title = scenario.Flow.Title,
            OriginalRequest = scenario.Flow.OriginalRequest,
            ContractVersion = "studio-v2",
            Kind = FlowKind.Delivery,
            Outcome = OutcomeType.PullRequest,
            OutcomeOwnerPlanStepKey = scenario.Flow.OutcomeOwnerPlanStepKey,
            PublicationPlanStepKey = scenario.Flow.PublicationPlanStepKey,
            WorkspacePath = scenario.Flow.WorkspacePath,
            BranchName = scenario.Flow.BranchName
        };
        forged.Steps.Add(scenario.OutcomeOwner);
        forged.Steps.Add(scenario.Publication);
        forged.GateRecords.Add(new HandoffGateRecord
        {
            FlowRunId = scenario.Flow.Id,
            FlowStepId = scenario.OutcomeOwner.Id,
            ActionType = HandoffActionType.CustomerReview,
            Decision = HandoffGateDecision.AwaitingHumanApproval,
            ReviewDecision = ReviewDecision.Accepted,
            Summary = "Forged in-memory approval.",
            Resolved = true,
            Approved = true
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scenario.Publisher.PublishAsync(forged, scenario.Publication.Id));

        Assert.Contains("resolved, approved customer review", exception.Message);
        Assert.Empty(scenario.Runner.Invocations);
        Assert.Empty(await scenario.ReadPublicationEventsAsync());
        Assert.Empty(await scenario.ReadRecordsAsync());
    }

    [Fact]
    public async Task SupersededPublicationStep_CannotPublish()
    {
        using var scenario = await ReviewedPublicationScenario.CreateAsync(
            OutcomeType.PullRequest);
        await scenario.AddLaterPublicationAttemptAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scenario.PublishAsync());

        Assert.Contains("exact semantic root", exception.Message);
        Assert.Empty(scenario.Runner.Invocations);
        Assert.Empty(await scenario.ReadPublicationEventsAsync());
        Assert.Empty(await scenario.ReadRecordsAsync());
    }

    [Theory]
    [InlineData(ExecutionPermissionProfile.WorkspaceWrite, false, true)]
    [InlineData(ExecutionPermissionProfile.Publish, false, true)]
    [InlineData(ExecutionPermissionProfile.Publish, true, false)]
    public async Task PublicationWithoutEffectiveAndDurablePublishAuthority_LeavesNoSideEffects(
        ExecutionPermissionProfile profile,
        bool effectiveRemoteAllowed,
        bool durableRemoteAllowed)
    {
        using var scenario = await ReviewedPublicationScenario.CreateAsync(
            OutcomeType.PullRequest);
        await scenario.SetPublicationAuthorityAsync(
            profile,
            effectiveRemoteAllowed,
            durableRemoteAllowed);

        await AssertRejectedWithoutSideEffectsAsync(
            scenario,
            "disabled by the current workflow post-approval permission ceiling");
    }

    [Fact]
    public async Task ReviewedPullRequestPublication_RecordsDurableJournalAndSkipsRepeatWork()
    {
        using var scenario = await ReviewedPublicationScenario.CreateAsync(
            OutcomeType.PullRequest);

        var first = await scenario.PublishAsync();

        Assert.Contains("https://github.com/example/repository/pull/1", first);
        Assert.Equal(1, scenario.Runner.CountOf("git", "push"));
        Assert.Equal(1, scenario.Runner.CountOf("git", "ls-remote"));
        Assert.Equal(1, scenario.Runner.CountOf("gh", "create"));
        var record = Assert.Single(await scenario.ReadRecordsAsync());
        Assert.Equal(ReviewedPublicationStage.Completed, record.Stage);
        Assert.Equal(scenario.Identity.Fingerprint, record.CandidateFingerprint);
        Assert.Equal(scenario.Repositories[0].Path, record.RelativePath);
        Assert.Equal(RemoteRepository, record.RemoteRepository);
        Assert.Equal(scenario.Flow.BranchName, record.BranchName);
        Assert.Equal(scenario.Identity.Repositories[0].Head, record.Head);
        Assert.Equal(scenario.Identity.Repositories[0].Tree, record.Tree);
        Assert.Equal(
            "https://github.com/example/repository/pull/1",
            record.PullRequestUrl);

        var verifyCallsAfterCompletion =
            scenario.ReviewedCandidates.VerifyCalls;
        scenario.Runner.Reset();
        var retry = await scenario.PublishAsync();

        Assert.Equal(first, retry);
        Assert.Empty(scenario.Runner.Invocations);
        Assert.True(
            scenario.ReviewedCandidates.VerifyCalls >
            verifyCallsAfterCompletion);
        Assert.Single(await scenario.ReadRecordsAsync());
        Assert.Single(
            await scenario.ReadPublicationEventsAsync(
                VerifiedCandidatePublisher.ReviewedPublicationCompletedEventType));
    }

    [Fact]
    public async Task CrashAfterPush_ReconcilesRemoteBranchInsteadOfPushingAgain()
    {
        using var scenario = await ReviewedPublicationScenario.CreateAsync(
            OutcomeType.PullRequest);
        scenario.Runner.FailOn = invocation =>
            invocation.Executable == "gh" &&
            invocation.Arguments.Contains("list");

        await Assert.ThrowsAsync<ScriptedProcessRunner.SimulatedCrashException>(
            () => scenario.PublishAsync());

        var afterCrash = Assert.Single(await scenario.ReadRecordsAsync());
        Assert.Equal(ReviewedPublicationStage.BranchPublished, afterCrash.Stage);
        Assert.Equal(1, scenario.Runner.CountOf("git", "push"));

        // The restart observes the reviewed commit already on the remote branch.
        scenario.Runner.FailOn = null;
        scenario.Runner.RemoteBranchHead = scenario.Identity.Repositories[0].Head;
        scenario.Runner.Reset();
        var report = await scenario.PublishAsync();

        Assert.Contains("https://github.com/example/repository/pull/1", report);
        Assert.Equal(0, scenario.Runner.CountOf("git", "push"));
        Assert.Equal(1, scenario.Runner.CountOf("git", "ls-remote"));
        Assert.Equal(1, scenario.Runner.CountOf("gh", "create"));
        var completed = Assert.Single(await scenario.ReadRecordsAsync());
        Assert.Equal(ReviewedPublicationStage.Completed, completed.Stage);
    }

    [Fact]
    public async Task CrashAfterIntent_ResumesFromTheDurableRepositoryIdentity()
    {
        using var scenario = await ReviewedPublicationScenario.CreateAsync(
            OutcomeType.PullRequest);
        scenario.Runner.FailOn = invocation =>
            invocation.Executable == "git" &&
            invocation.Arguments.Contains("ls-remote");

        await Assert.ThrowsAsync<ScriptedProcessRunner.SimulatedCrashException>(
            () => scenario.PublishAsync());

        var intent = Assert.Single(await scenario.ReadRecordsAsync());
        Assert.Equal(ReviewedPublicationStage.Intent, intent.Stage);
        Assert.Equal(scenario.Identity.Fingerprint, intent.CandidateFingerprint);
        Assert.Equal(scenario.Identity.Repositories[0].Head, intent.Head);
        Assert.Equal(scenario.Identity.Repositories[0].Tree, intent.Tree);

        scenario.Runner.FailOn = null;
        scenario.Runner.Reset();
        var report = await scenario.PublishAsync();

        Assert.Contains("https://github.com/example/repository/pull/1", report);
        Assert.Equal(1, scenario.Runner.CountOf("git", "push"));
        Assert.Equal(
            ReviewedPublicationStage.Completed,
            Assert.Single(await scenario.ReadRecordsAsync()).Stage);
    }

    [Fact]
    public async Task CrashImmediatelyAfterPush_ReconcilesWithoutRepeatingPush()
    {
        using var scenario = await ReviewedPublicationScenario.CreateAsync(
            OutcomeType.PullRequest);
        scenario.Runner.FailAfter = invocation =>
            invocation.Executable == "git" &&
            invocation.Arguments.Contains("push");

        await Assert.ThrowsAsync<ScriptedProcessRunner.SimulatedCrashException>(
            () => scenario.PublishAsync());

        Assert.Equal(
            ReviewedPublicationStage.Intent,
            Assert.Single(await scenario.ReadRecordsAsync()).Stage);
        Assert.Equal(1, scenario.Runner.CountOf("git", "push"));

        scenario.Runner.FailAfter = null;
        scenario.Runner.Reset();
        var report = await scenario.PublishAsync();

        Assert.Contains("https://github.com/example/repository/pull/1", report);
        Assert.Equal(0, scenario.Runner.CountOf("git", "push"));
        Assert.Equal(1, scenario.Runner.CountOf("git", "ls-remote"));
        Assert.Equal(1, scenario.Runner.CountOf("gh", "create"));
    }

    [Fact]
    public async Task CrashImmediatelyAfterPullRequestCreate_ReusesRemotePullRequest()
    {
        using var scenario = await ReviewedPublicationScenario.CreateAsync(
            OutcomeType.PullRequest);
        scenario.Runner.FailAfter = invocation =>
            invocation.Executable == "gh" &&
            invocation.Arguments.Contains("create");

        await Assert.ThrowsAsync<ScriptedProcessRunner.SimulatedCrashException>(
            () => scenario.PublishAsync());

        var afterCrash = Assert.Single(await scenario.ReadRecordsAsync());
        Assert.Equal(ReviewedPublicationStage.BranchPublished, afterCrash.Stage);
        Assert.Equal(string.Empty, afterCrash.PullRequestUrl);
        Assert.Single(scenario.Runner.PullRequests);

        scenario.Runner.FailAfter = null;
        scenario.Runner.Reset();
        var report = await scenario.PublishAsync();

        Assert.Contains("https://github.com/example/repository/pull/1", report);
        Assert.Equal(0, scenario.Runner.CountOf("git", "push"));
        Assert.Equal(0, scenario.Runner.CountOf("gh", "create"));
        Assert.Equal(1, scenario.Runner.CountOf("gh", "list"));
        Assert.Equal(
            "https://github.com/example/repository/pull/1",
            Assert.Single(await scenario.ReadRecordsAsync()).PullRequestUrl);
    }

    [Fact]
    public async Task CrashAfterPullRequestWasJournaled_MergedWithDeletedBranchRecoversAndRetriesCleanly()
    {
        using var scenario = await ReviewedPublicationScenario.CreateAsync(
            OutcomeType.PullRequest);
        var head = scenario.Identity.Repositories[0].Head;
        // Reproduce the durable point after PR creation/identity persistence but before the
        // repository publication event and completion marker were committed.
        await scenario.SeedRecordAsync(
            scenario.Repositories[0].Path,
            ReviewedPublicationStage.PullRequestOpened,
            "https://github.com/example/repository/pull/42");
        scenario.Runner.RemoteBranchHead = string.Empty;
        scenario.Runner.PullRequests.Add(new ScriptedProcessRunner.FakePullRequest(
            "https://github.com/example/repository/pull/42",
            42,
            "MERGED",
            scenario.Flow.BranchName,
            head,
            RemoteRepository));

        var report = await scenario.PublishAsync();

        Assert.Contains("https://github.com/example/repository/pull/42", report);
        Assert.Equal(0, scenario.Runner.CountOf("git", "push"));
        Assert.Equal(0, scenario.Runner.CountOf("gh", "create"));
        Assert.Equal(1, scenario.Runner.CountOf("gh", "view"));
        Assert.Equal(0, scenario.Runner.CountOf("git", "ls-remote"));
        var record = Assert.Single(await scenario.ReadRecordsAsync());
        Assert.Equal(ReviewedPublicationStage.Completed, record.Stage);
        Assert.Equal(
            "https://github.com/example/repository/pull/42",
            record.PullRequestUrl);

        scenario.Runner.Reset();
        var verified = await scenario.VerifyAsync(report);

        Assert.Equal(
            "https://github.com/example/repository/pull/42",
            verified.Url);
        Assert.Equal(1, scenario.Runner.CountOf("gh", "view"));
        Assert.Equal(0, scenario.Runner.CountOf("git", "ls-remote"));

        scenario.Runner.Reset();
        var verifiedAgain = await scenario.VerifyAsync(report);

        Assert.Equal(verified, verifiedAgain);
        Assert.Equal(1, scenario.Runner.CountOf("gh", "view"));
        Assert.Equal(0, scenario.Runner.CountOf("gh", "create"));
        Assert.Equal(0, scenario.Runner.CountOf("git", "push"));
        Assert.Equal(0, scenario.Runner.CountOf("git", "ls-remote"));

        scenario.Runner.Reset();
        var retry = await scenario.PublishAsync();

        Assert.Equal(report, retry);
        Assert.Empty(scenario.Runner.Invocations);
        Assert.Single(
            await scenario.ReadPublicationEventsAsync(
                VerifiedCandidatePublisher.ReviewedPublicationCompletedEventType));
    }

    [Theory]
    [InlineData("OPEN")]
    [InlineData("MERGED")]
    public async Task RecordedPullRequestWithConflictingHead_IsRejected(
        string state)
    {
        using var scenario = await ReviewedPublicationScenario.CreateAsync(
            OutcomeType.PullRequest);
        var head = scenario.Identity.Repositories[0].Head;
        await scenario.SeedRecordAsync(
            scenario.Repositories[0].Path,
            ReviewedPublicationStage.PullRequestOpened,
            "https://github.com/example/repository/pull/42");
        scenario.Runner.RemoteBranchHead = head;
        scenario.Runner.PullRequests.Add(new ScriptedProcessRunner.FakePullRequest(
            "https://github.com/example/repository/pull/42",
            42,
            state,
            scenario.Flow.BranchName,
            new string('a', 40),
            RemoteRepository));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scenario.PublishAsync());

        Assert.Contains("no longer publishes reviewed commit", exception.Message);
        Assert.Equal(0, scenario.Runner.CountOf("gh", "create"));
        Assert.Equal(0, scenario.Runner.CountOf("git", "push"));
        Assert.Equal(0, scenario.Runner.CountOf("git", "ls-remote"));
    }

    [Fact]
    public async Task RecordedClosedPullRequest_FailsClosedWithoutCreatingAReplacement()
    {
        using var scenario = await ReviewedPublicationScenario.CreateAsync(
            OutcomeType.PullRequest);
        var head = scenario.Identity.Repositories[0].Head;
        await scenario.SeedRecordAsync(
            scenario.Repositories[0].Path,
            ReviewedPublicationStage.PullRequestOpened,
            "https://github.com/example/repository/pull/42");
        scenario.Runner.PullRequests.Add(new ScriptedProcessRunner.FakePullRequest(
            "https://github.com/example/repository/pull/42",
            42,
            "CLOSED",
            scenario.Flow.BranchName,
            head,
            RemoteRepository));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scenario.PublishAsync());

        Assert.Contains("closed without merge", exception.Message);
        Assert.Equal(1, scenario.Runner.CountOf("gh", "view"));
        Assert.Equal(0, scenario.Runner.CountOf("gh", "create"));
        Assert.Equal(0, scenario.Runner.CountOf("git", "push"));
        Assert.Equal(0, scenario.Runner.CountOf("git", "ls-remote"));
        Assert.Empty(
            await scenario.ReadPublicationEventsAsync(
                VerifiedCandidatePublisher.ReviewedPublicationCompletedEventType));
    }

    [Fact]
    public async Task RecordedOpenPullRequest_ReconcilesAndVerifiesWithoutRepublishing()
    {
        using var scenario = await ReviewedPublicationScenario.CreateAsync(
            OutcomeType.PullRequest);
        var head = scenario.Identity.Repositories[0].Head;
        await scenario.SeedRecordAsync(
            scenario.Repositories[0].Path,
            ReviewedPublicationStage.PullRequestOpened,
            "https://github.com/example/repository/pull/42");
        scenario.Runner.RemoteBranchHead = head;
        scenario.Runner.PullRequests.Add(new ScriptedProcessRunner.FakePullRequest(
            "https://github.com/example/repository/pull/42",
            42,
            "OPEN",
            scenario.Flow.BranchName,
            head,
            RemoteRepository));

        var report = await scenario.PublishAsync();

        Assert.Equal(1, scenario.Runner.CountOf("gh", "view"));
        Assert.Equal(1, scenario.Runner.CountOf("git", "ls-remote"));
        Assert.Equal(0, scenario.Runner.CountOf("gh", "create"));
        Assert.Equal(0, scenario.Runner.CountOf("git", "push"));

        scenario.Runner.Reset();
        var verified = await scenario.VerifyAsync(report);

        Assert.Equal(
            "https://github.com/example/repository/pull/42",
            verified.Url);
        Assert.Equal(1, scenario.Runner.CountOf("gh", "view"));
        Assert.Equal(0, scenario.Runner.CountOf("gh", "create"));
    }

    [Fact]
    public async Task Verifier_RejectsMergedPullRequestWithConflictingHead()
    {
        using var scenario = await ReviewedPublicationScenario.CreateAsync(
            OutcomeType.PullRequest);
        var report = await scenario.PublishAsync();
        var pullRequest = Assert.Single(scenario.Runner.PullRequests);
        scenario.Runner.PullRequests[0] = pullRequest with
        {
            State = "MERGED",
            HeadRefOid = new string('a', 40)
        };
        scenario.Runner.RemoteBranchHead = string.Empty;
        scenario.Runner.Reset();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scenario.VerifyAsync(report));

        Assert.Contains(
            "does not publish the candidate sealed before review",
            exception.Message);
        Assert.Equal(1, scenario.Runner.CountOf("gh", "view"));
        Assert.Equal(0, scenario.Runner.CountOf("gh", "create"));
    }

    [Fact]
    public async Task Verifier_RejectsClosedUnmergedPullRequest()
    {
        using var scenario = await ReviewedPublicationScenario.CreateAsync(
            OutcomeType.PullRequest);
        var report = await scenario.PublishAsync();
        var pullRequest = Assert.Single(scenario.Runner.PullRequests);
        scenario.Runner.PullRequests[0] = pullRequest with
        {
            State = "CLOSED"
        };
        scenario.Runner.Reset();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scenario.VerifyAsync(report));

        Assert.Contains("closed without merge", exception.Message);
        Assert.Equal(1, scenario.Runner.CountOf("gh", "view"));
        Assert.Equal(0, scenario.Runner.CountOf("gh", "create"));
    }

    [Fact]
    public async Task RemoteBranchThatMovedAfterAPublishedPush_IsRejected()
    {
        using var scenario = await ReviewedPublicationScenario.CreateAsync(
            OutcomeType.PullRequest);
        await scenario.SeedRecordAsync(
            scenario.Repositories[0].Path,
            ReviewedPublicationStage.BranchPublished,
            pullRequestUrl: null);
        scenario.Runner.RemoteBranchHead = new string('b', 40);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scenario.PublishAsync());

        Assert.Contains("no longer holds the reviewed commit", exception.Message);
        Assert.Equal(0, scenario.Runner.CountOf("git", "push"));
        Assert.Equal(0, scenario.Runner.CountOf("gh", "create"));
    }

    [Fact]
    public async Task JournalRecordedForADifferentCandidate_IsRejected()
    {
        using var scenario = await ReviewedPublicationScenario.CreateAsync(
            OutcomeType.PullRequest);
        await scenario.SeedRecordAsync(
            scenario.Repositories[0].Path,
            ReviewedPublicationStage.Intent,
            pullRequestUrl: null,
            fingerprint: new string('c', 64));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scenario.PublishAsync());

        Assert.Contains("conflicts with the reviewed candidate", exception.Message);
        Assert.Empty(scenario.Runner.Invocations);
    }

    [Fact]
    public async Task JournalPullRequestForDifferentRemote_IsRejectedWithoutExternalWork()
    {
        using var scenario = await ReviewedPublicationScenario.CreateAsync(
            OutcomeType.PullRequest);
        await scenario.SeedRecordAsync(
            scenario.Repositories[0].Path,
            ReviewedPublicationStage.Completed,
            "https://github.com/example/different/pull/5");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scenario.PublishAsync());

        Assert.Contains("conflicting pull request URL", exception.Message);
        Assert.Empty(scenario.Runner.Invocations);
    }

    [Fact]
    public async Task CrashBetweenRepositories_OnlyFinishesTheUnpublishedRepository()
    {
        using var scenario = await ReviewedPublicationScenario.CreateAsync(
            OutcomeType.PullRequest,
            [("first", RemoteRepository), ("second", "example/second")]);
        var first = scenario.Identity.Repositories
            .Single(item => item.RelativePath == "first");
        await scenario.SeedRecordAsync(
            "first",
            ReviewedPublicationStage.Completed,
            "https://github.com/example/repository/pull/11");

        var report = await scenario.PublishAsync();

        Assert.Contains("https://github.com/example/repository/pull/11", report);
        Assert.Contains("https://github.com/example/second/pull/1", report);
        Assert.Equal(1, scenario.Runner.CountOf("git", "push"));
        Assert.Equal(1, scenario.Runner.CountOf("gh", "create"));
        Assert.DoesNotContain(
            scenario.Runner.Invocations,
            invocation => invocation.Arguments.Contains(
                $"{first.Head}:refs/heads/{scenario.Flow.BranchName}"));
        var records = await scenario.ReadRecordsAsync();
        Assert.Equal(2, records.Count);
        Assert.All(
            records,
            record => Assert.Equal(
                ReviewedPublicationStage.Completed,
                record.Stage));
    }

    [Fact]
    public async Task CrashDuringSecondRepository_ResumesWithoutRepeatingFirstRepository()
    {
        using var scenario = await ReviewedPublicationScenario.CreateAsync(
            OutcomeType.PullRequest,
            [("first", RemoteRepository), ("second", "example/second")]);
        scenario.Runner.FailOn = invocation =>
            invocation.Executable == "git" &&
            invocation.Arguments.Contains("ls-remote") &&
            invocation.Arguments.Any(argument =>
                argument.Contains("example/second", StringComparison.Ordinal));

        await Assert.ThrowsAsync<ScriptedProcessRunner.SimulatedCrashException>(
            () => scenario.PublishAsync());

        var afterCrash = await scenario.ReadRecordsAsync();
        Assert.Equal(
            ReviewedPublicationStage.Completed,
            afterCrash.Single(item => item.RelativePath == "first").Stage);
        Assert.Equal(
            ReviewedPublicationStage.Intent,
            afterCrash.Single(item => item.RelativePath == "second").Stage);

        scenario.Runner.FailOn = null;
        scenario.Runner.RemoteBranchHead = string.Empty;
        scenario.Runner.Reset();
        var report = await scenario.PublishAsync();

        Assert.Contains("https://github.com/example/repository/pull/1", report);
        Assert.Contains("https://github.com/example/second/pull/1", report);
        Assert.Equal(1, scenario.Runner.CountOf("git", "push"));
        Assert.Equal(1, scenario.Runner.CountOf("gh", "create"));
    }

    [Fact]
    public async Task CompletedRepositoriesWithoutOverallEvent_FinalizeWithoutRemoteWork()
    {
        using var scenario = await ReviewedPublicationScenario.CreateAsync(
            OutcomeType.PullRequest);
        await scenario.SeedRecordAsync(
            scenario.Repositories[0].Path,
            ReviewedPublicationStage.Completed,
            "https://github.com/example/repository/pull/17");

        var report = await scenario.PublishAsync();

        Assert.Contains("https://github.com/example/repository/pull/17", report);
        Assert.Empty(scenario.Runner.Invocations);
        Assert.Single(
            await scenario.ReadPublicationEventsAsync(
                VerifiedCandidatePublisher.ReviewedPublicationCompletedEventType));
    }

    [Fact]
    public async Task ConcurrentPublication_PushesOnceAndOpensOnePullRequest()
    {
        using var scenario = await ReviewedPublicationScenario.CreateAsync(
            OutcomeType.PullRequest);

        var reports = await Task.WhenAll(
            Task.Run(() => scenario.PublishAsync()),
            Task.Run(() => scenario.PublishAsync()));

        Assert.Equal(reports[0], reports[1]);
        Assert.Contains("https://github.com/example/repository/pull/1", reports[0]);
        Assert.Equal(1, scenario.Runner.CountOf("git", "push"));
        Assert.Equal(1, scenario.Runner.CountOf("gh", "create"));
        Assert.Single(await scenario.ReadRecordsAsync());
        Assert.Single(
            await scenario.ReadPublicationEventsAsync(
                VerifiedCandidatePublisher.ReviewedPublicationCompletedEventType));
    }

    [Fact]
    public async Task CommitPublicationStaysIdempotentAndFreeOfRemoteWork()
    {
        using var scenario = await ReviewedPublicationScenario.CreateAsync(
            OutcomeType.Commit,
            [("first", string.Empty)]);

        var first = await scenario.PublishAsync();
        var retry = await scenario.PublishAsync();

        Assert.Equal(first, retry);
        Assert.Empty(scenario.Runner.Invocations);
        var record = Assert.Single(await scenario.ReadRecordsAsync());
        Assert.Equal(ReviewedPublicationStage.Completed, record.Stage);
        Assert.Equal(string.Empty, record.PullRequestUrl);
        Assert.Single(
            await scenario.ReadPublicationEventsAsync(
                VerifiedCandidatePublisher.ReviewedPublicationCompletedEventType));
    }

    [Fact]
    public async Task LegacyCompletedPublicationEvents_RebuildTheJournalWithoutRemoteWork()
    {
        using var scenario = await ReviewedPublicationScenario.CreateAsync(
            OutcomeType.PullRequest);
        var head = scenario.Identity.Repositories[0].Head;
        var tree = scenario.Identity.Repositories[0].Tree;
        await scenario.AddLegacyPublicationEventsAsync(
            scenario.Repositories[0].Path,
            RemoteRepository,
            "https://github.com/example/repository/pull/9",
            head,
            tree);

        var report = await scenario.PublishAsync();

        Assert.Contains("https://github.com/example/repository/pull/9", report);
        Assert.Empty(scenario.Runner.Invocations);
        var record = Assert.Single(await scenario.ReadRecordsAsync());
        Assert.Equal(ReviewedPublicationStage.Completed, record.Stage);
        Assert.Equal(
            "https://github.com/example/repository/pull/9",
            record.PullRequestUrl);
    }

    [Fact]
    public async Task ReviewedPublicationSchema_IsAdditiveAndIdempotentOnOlderDatabases()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "reviewed-publication-schema",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DbContextOptionsBuilder<HarnessDbContext>()
                .UseSqlite(
                    $"Data Source={Path.Combine(root, "schema.db")};Pooling=False")
                .Options;
            IDbContextFactory<HarnessDbContext> factory =
                new ScenarioDbContextFactory(options);
            var flowId = Guid.NewGuid();
            await using (var database = await factory.CreateDbContextAsync())
            {
                await database.Database.EnsureCreatedAsync();
                // Reproduce a database created before the durable publication journal existed.
                await database.Database.ExecuteSqlRawAsync(
                    "DROP TABLE ReviewedPublicationRecords;");
                database.Flows.Add(new FlowRun
                {
                    Id = flowId,
                    Title = "Legacy flow",
                    OriginalRequest = "Keep history."
                });
                await database.SaveChangesAsync();
            }

            await using (var database = await factory.CreateDbContextAsync())
            {
                await DatabaseInitializer.EnsureReviewedPublicationSchemaAsync(
                    database);
                await DatabaseInitializer.EnsureReviewedPublicationSchemaAsync(
                    database);
                database.ReviewedPublicationRecords.Add(
                    BuildSchemaProbeRecord(flowId));
                await database.SaveChangesAsync();
            }

            await using (var database = await factory.CreateDbContextAsync())
            {
                var record = await database.ReviewedPublicationRecords
                    .AsNoTracking()
                    .SingleAsync();
                Assert.Equal(flowId, record.FlowRunId);
                Assert.Equal(
                    ReviewedPublicationStage.BranchPublished,
                    record.Stage);
                Assert.Equal("first", record.RelativePath);
                Assert.Single(await database.Flows.AsNoTracking().ToListAsync());

                database.ReviewedPublicationRecords.Add(
                    BuildSchemaProbeRecord(flowId));
                await Assert.ThrowsAsync<DbUpdateException>(
                    () => database.SaveChangesAsync());
            }
        }
        finally
        {
            GitWorkspace.DeleteBestEffort(root);
        }
    }

    private static ReviewedPublicationRecord BuildSchemaProbeRecord(Guid flowId) =>
        new()
        {
            FlowRunId = flowId,
            PublicationRootId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
            Iteration = 1,
            CandidateFingerprint = new string('d', 64),
            RelativePath = "first",
            RemoteRepository = RemoteRepository,
            BranchName = BranchName,
            Head = new string('e', 40),
            Tree = new string('f', 40),
            Stage = ReviewedPublicationStage.BranchPublished
        };

    private static async Task AssertRejectedWithoutSideEffectsAsync(
        ReviewedPublicationScenario scenario,
        string expectedMessageFragment)
    {
        scenario.Runner.Reset();
        var exception =
            await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => scenario.PublishAsync());

        Assert.Contains(expectedMessageFragment, exception.Message);
        Assert.Empty(scenario.Runner.Invocations);
        Assert.Empty(await scenario.ReadPublicationEventsAsync());
        Assert.Empty(await scenario.ReadRecordsAsync());
    }

    private sealed class ReviewedPublicationScenario : IDisposable
    {
        private readonly string root;

        private ReviewedPublicationScenario(
            string root,
            IDbContextFactory<HarnessDbContext> factory,
            FlowRun flow,
            FlowStep outcomeOwner,
            FlowStep publication,
            ReviewedCandidateIdentity identity,
            IReadOnlyList<(string Path, string Remote)> repositories,
            ScriptedProcessRunner runner,
            TrackingReviewedCandidateService reviewedCandidates,
            VerifiedCandidatePublisher publisher)
        {
            this.root = root;
            Factory = factory;
            Flow = flow;
            OutcomeOwner = outcomeOwner;
            Publication = publication;
            Identity = identity;
            Repositories = repositories;
            Runner = runner;
            ReviewedCandidates = reviewedCandidates;
            Publisher = publisher;
        }

        public IDbContextFactory<HarnessDbContext> Factory { get; }

        public FlowRun Flow { get; }

        public FlowStep OutcomeOwner { get; }

        public FlowStep Publication { get; }

        public ReviewedCandidateIdentity Identity { get; }

        public IReadOnlyList<(string Path, string Remote)> Repositories { get; }

        public ScriptedProcessRunner Runner { get; }

        public TrackingReviewedCandidateService ReviewedCandidates { get; }

        public VerifiedCandidatePublisher Publisher { get; }

        public Guid PublicationRootId =>
            Publication.StableSemanticRootId ?? Publication.Id;

        public static async Task<ReviewedPublicationScenario> CreateAsync(
            OutcomeType outcome,
            IReadOnlyList<(string Path, string Remote)>? repositories = null)
        {
            repositories ??= [("first", RemoteRepository)];
            var root = Path.Combine(
                Path.GetTempPath(),
                "reviewed-publication-tests",
                Guid.NewGuid().ToString("N"));
            var workspacePath = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspacePath);
            foreach (var (path, remote) in repositories)
            {
                GitWorkspace.CreateRepository(
                    Path.Combine(workspacePath, path),
                    remote,
                    BranchName);
            }

            var options = new DbContextOptionsBuilder<HarnessDbContext>()
                .UseSqlite(
                    $"Data Source={Path.Combine(root, "publication.db")};Pooling=False")
                .Options;
            IDbContextFactory<HarnessDbContext> factory =
                new ScenarioDbContextFactory(options);
            var flow = new FlowRun
            {
                Title = "Publish the reviewed candidate",
                OriginalRequest = "Publish the reviewed candidate.",
                ConsolidatedRequest = "Publish the reviewed candidate.",
                ContractVersion = "studio-v2",
                Kind = FlowKind.Delivery,
                Status = FlowStatus.Queued,
                RepositoryPath = workspacePath,
                WorkspacePath = workspacePath,
                BranchName = BranchName,
                Outcome = outcome,
                OutcomeOwnerPlanStepKey = "outcome",
                PublicationPlanStepKey = "publish",
                OutcomeContractJson =
                    """{"Version":"flow-outcome-v1","Goal":"Publish.","Summary":"Reviewed.","ImplementationDetails":["Exact bytes."],"Artifacts":[]}"""
            };
            flow.Events.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                Type = StudioWorkspaceRepositoryMapLedger.EventType,
                Message = "Persisted trusted workspace repositories.",
                DataJson = StudioWorkspaceRepositoryMapLedger.Serialize(
                    StudioWorkspaceRepositoryMapLedger.Create(
                        flow,
                        workspacePath,
                        repositories
                            .Select(item => new WorkspaceRepositoryIdentity(
                                item.Path,
                                item.Remote))
                            .ToArray()))
            });
            var outcomeOwner = new FlowStep
            {
                FlowRunId = flow.Id,
                Iteration = flow.Iteration,
                Sequence = 10,
                AgentId = "owner",
                AgentName = "Owner",
                AgentRole = "owner",
                PlanStepKey = "outcome",
                IsOutcomeOwner = true,
                Status = StepStatus.Completed
            };
            var publication = new FlowStep
            {
                FlowRunId = flow.Id,
                Iteration = flow.Iteration,
                Sequence = 20,
                AgentId = "publisher",
                AgentName = "Publisher",
                AgentRole = "publisher",
                PlanStepKey = "publish",
                PlanDutiesJson = """["Publish"]""",
                PlanStage = PlanStage.AfterApproval,
                InvocationKind = ExecutionInvocationKind.Publication,
                PermissionProfile =
                    ExecutionPermissionProfile.Publish,
                EffectivePermissionJson = JsonSerializer.Serialize(
                    PublicationPermission()),
                WorkflowRevision = "publication-workflow-v1",
                RemotePublicationAllowed = true,
                Status = StepStatus.Running,
                DependsOnStepId = outcomeOwner.Id
            };
            publication.StableSemanticRootId = publication.Id;
            flow.Steps.Add(outcomeOwner);
            flow.Steps.Add(publication);
            flow.GateRecords.Add(BuildCustomerReviewGate(
                flow.Id,
                outcomeOwner.Id,
                resolved: true,
                approved: true,
                decision: ReviewDecision.Accepted));

            var fingerprints = new CandidateFingerprintService(
                new ProcessRunner(),
                TimeProvider.System);
            var reviewed = new ReviewedCandidateService(fingerprints);
            var identity = await reviewed.SealAsync(
                flow,
                outcomeOwner.Id,
                outcomeOwner.PlanStepKey,
                flow.OutcomeContractJson);
            flow.Events.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = outcomeOwner.Id,
                Type = ReviewedCandidateLedger.EventType,
                Message = "Sealed the exact reviewed candidate.",
                DataJson = ReviewedCandidateLedger.Serialize(identity)
            });
            await using (var database = await factory.CreateDbContextAsync())
            {
                await database.Database.EnsureCreatedAsync();
                database.Flows.Add(flow);
                await database.SaveChangesAsync();
                await DeliveryReadinessFixtures.SeedReadyToApproveAsync(
                    database,
                    flow,
                    identity,
                    outcomeOwner.Id);
            }

            var runner = new ScriptedProcessRunner();
            foreach (var repository in identity.Repositories)
            {
                runner.RegisterCommit(repository.Head, repository.Tree);
            }
            var trackedReviewedCandidates =
                new TrackingReviewedCandidateService(reviewed);
            var publisher = new VerifiedCandidatePublisher(
                runner,
                fingerprints,
                trackedReviewedCandidates,
                factory);
            return new ReviewedPublicationScenario(
                root,
                factory,
                flow,
                outcomeOwner,
                publication,
                identity,
                repositories,
                runner,
                trackedReviewedCandidates,
                publisher);
        }

        public Task<string> PublishAsync() =>
            Publisher.PublishAsync(Flow, Publication.Id);

        public async Task SetPublicationAuthorityAsync(
            ExecutionPermissionProfile profile,
            bool effectiveRemoteAllowed,
            bool durableRemoteAllowed)
        {
            await using var database = await Factory.CreateDbContextAsync();
            var publication = await database.FlowSteps.SingleAsync(
                item => item.Id == Publication.Id);
            var effective = PublicationPermission() with
            {
                Profile = profile,
                AllowRemotePublication =
                    effectiveRemoteAllowed
            };
            publication.PermissionProfile = profile;
            publication.EffectivePermissionJson =
                JsonSerializer.Serialize(effective);
            publication.RemotePublicationAllowed =
                durableRemoteAllowed;
            await database.SaveChangesAsync();
        }

        public Task<PublishedOutcome> VerifyAsync(string publicationReport) =>
            new PublishedOutcomeVerifier(
                    Runner,
                    databaseFactory: Factory,
                    reviewedCandidateService: ReviewedCandidates)
                .VerifyAsync(Flow, publicationReport);

        public async Task<IReadOnlyList<ReviewedPublicationRecord>> ReadRecordsAsync()
        {
            await using var database = await Factory.CreateDbContextAsync();
            return await database.ReviewedPublicationRecords
                .AsNoTracking()
                .Where(item => item.FlowRunId == Flow.Id)
                .OrderBy(item => item.RelativePath)
                .ToListAsync();
        }

        public async Task<IReadOnlyList<FlowEvent>> ReadPublicationEventsAsync(
            string? type = null)
        {
            await using var database = await Factory.CreateDbContextAsync();
            return await database.FlowEvents
                .AsNoTracking()
                .Where(item =>
                    item.FlowRunId == Flow.Id &&
                    (type == null
                        ? item.Type.StartsWith("delivery.reviewed-publication")
                        : item.Type == type))
                .ToListAsync();
        }

        public async Task RemoveCustomerReviewGatesAsync()
        {
            await using var database = await Factory.CreateDbContextAsync();
            var gates = await database.GateRecords
                .Where(item => item.FlowRunId == Flow.Id)
                .ToListAsync();
            database.GateRecords.RemoveRange(gates);
            await database.SaveChangesAsync();
        }

        public async Task ReplaceCustomerReviewGateAsync(
            Guid flowStepId,
            bool resolved,
            bool? approved,
            ReviewDecision? decision)
        {
            await RemoveCustomerReviewGatesAsync();
            await using var database = await Factory.CreateDbContextAsync();
            database.GateRecords.Add(BuildCustomerReviewGate(
                Flow.Id,
                flowStepId,
                resolved,
                approved,
                decision));
            await database.SaveChangesAsync();
        }

        public async Task<Guid> AddStaleIterationOutcomeOwnerAsync()
        {
            await using var database = await Factory.CreateDbContextAsync();
            var stale = new FlowStep
            {
                FlowRunId = Flow.Id,
                Iteration = Flow.Iteration - 1,
                Sequence = 5,
                AgentId = "owner",
                AgentName = "Owner",
                AgentRole = "owner",
                PlanStepKey = "outcome",
                IsOutcomeOwner = true,
                Status = StepStatus.Completed
            };
            database.FlowSteps.Add(stale);
            await database.SaveChangesAsync();
            return stale.Id;
        }

        public async Task SetOutcomeOwnerStatusAsync(StepStatus status)
        {
            await using var database = await Factory.CreateDbContextAsync();
            var step = await database.FlowSteps.SingleAsync(
                item => item.Id == OutcomeOwner.Id);
            step.Status = status;
            await database.SaveChangesAsync();
        }

        public async Task AddLaterPublicationAttemptAsync()
        {
            await using var database = await Factory.CreateDbContextAsync();
            var later = new FlowStep
            {
                FlowRunId = Flow.Id,
                Iteration = Flow.Iteration,
                Sequence = 30,
                AgentId = "publisher",
                AgentName = "Publisher",
                AgentRole = "publisher",
                PlanStepKey = "publish",
                PlanDutiesJson = """["Publish"]""",
                PlanStage = PlanStage.AfterApproval,
                InvocationKind = ExecutionInvocationKind.Publication,
                RemotePublicationAllowed = true,
                Status = StepStatus.Running
            };
            later.StableSemanticRootId = later.Id;
            database.FlowSteps.Add(later);
            await database.SaveChangesAsync();
        }

        private static EffectiveExecutionPermission
            PublicationPermission() =>
            new PermissionProfileResolver().Resolve(
                new PermissionResolutionRequest(
                    FlowKind.Delivery,
                    ExecutionInvocationKind.Publication,
                    PlanStage.AfterApproval,
                    ImmutableArray.Create(PlanDuty.Publish),
                    DurableReviewDecision:
                        ReviewDecision.Accepted,
                    DurableApproval: true,
                    IsOnlyPlannedPublishStep: true,
                    ContractVersion: "studio-v2",
                    LegacyPublicationAuthorized: false,
                    IsGovernedOutcomeVerification: false),
                new WorkflowPermissionRestrictions(
                    ExecutionPermissionProfile.ReadOnlySource,
                    ExecutionPermissionProfile.WorkspaceWrite,
                    ExecutionPermissionProfile.Publish,
                    ImmutableDictionary<
                        ExecutionPermissionProfile,
                        ImmutableArray<string>>.Empty));

        public async Task SeedRecordAsync(
            string relativePath,
            ReviewedPublicationStage stage,
            string? pullRequestUrl,
            string? fingerprint = null)
        {
            var repository = Identity.Repositories.Single(item =>
                item.RelativePath == relativePath);
            await using var database = await Factory.CreateDbContextAsync();
            database.ReviewedPublicationRecords.Add(new ReviewedPublicationRecord
            {
                FlowRunId = Flow.Id,
                PublicationRootId = PublicationRootId,
                Iteration = Flow.Iteration,
                CandidateFingerprint = fingerprint ?? Identity.Fingerprint,
                RelativePath = relativePath,
                RemoteRepository = repository.RemoteRepository,
                BranchName = Flow.BranchName,
                Head = repository.Head,
                Tree = repository.Tree,
                Stage = stage,
                PullRequestUrl = pullRequestUrl ?? string.Empty
            });
            await database.SaveChangesAsync();
        }

        public async Task AddLegacyPublicationEventsAsync(
            string relativePath,
            string remoteRepository,
            string pullRequestUrl,
            string head,
            string tree)
        {
            await using var database = await Factory.CreateDbContextAsync();
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = Flow.Id,
                FlowStepId = PublicationRootId,
                Type = VerifiedCandidatePublisher
                    .ReviewedPublicationRepositoryEventType,
                Message = "Legacy repository publication.",
                DataJson = SerializeLegacyEvent(
                    Identity.Fingerprint,
                    relativePath,
                    remoteRepository,
                    pullRequestUrl,
                    head,
                    tree)
            });
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = Flow.Id,
                FlowStepId = PublicationRootId,
                Type = VerifiedCandidatePublisher
                    .ReviewedPublicationCompletedEventType,
                Message = "Legacy publication completion.",
                DataJson = SerializeLegacyEvent(
                    Identity.Fingerprint,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty)
            });
            await database.SaveChangesAsync();
        }

        private static string SerializeLegacyEvent(
            string fingerprint,
            string relativePath,
            string remoteRepository,
            string pullRequestUrl,
            string head,
            string tree) =>
            JsonSerializer.Serialize(new
            {
                Version = "reviewed-candidate-publication-v1",
                CandidateFingerprint = fingerprint,
                RelativePath = relativePath,
                RemoteRepository = remoteRepository,
                PullRequestUrl = pullRequestUrl,
                Head = head,
                Tree = tree
            });

        private static HandoffGateRecord BuildCustomerReviewGate(
            Guid flowId,
            Guid flowStepId,
            bool resolved,
            bool? approved,
            ReviewDecision? decision) =>
            new()
            {
                FlowRunId = flowId,
                FlowStepId = flowStepId,
                ActionType = HandoffActionType.CustomerReview,
                Decision = HandoffGateDecision.AwaitingHumanApproval,
                ReviewDecision = decision,
                TrustLevelAtDecision = HandoffTrustLevel.Gated,
                Summary = "Customer review of the Delivery outcome.",
                Resolved = resolved,
                Approved = approved,
                ResolvedBy = resolved ? "customer" : null,
                ResolvedAt = resolved ? DateTimeOffset.UtcNow : null
            };

        public void Dispose() => GitWorkspace.DeleteBestEffort(root);
    }

    private sealed class TrackingReviewedCandidateService(
        IReviewedCandidateService inner) : IReviewedCandidateService
    {
        public int VerifyCalls { get; private set; }

        public Task<ReviewedCandidateIdentity> SealAsync(
            FlowRun flow,
            Guid outcomeOwnerStepId,
            string outcomeOwnerPlanStepKey,
            string outcomeContractJson,
            CancellationToken cancellationToken = default) =>
            inner.SealAsync(
                flow,
                outcomeOwnerStepId,
                outcomeOwnerPlanStepKey,
                outcomeContractJson,
                cancellationToken);

        public Task<OutcomeCandidateSnapshot> VerifyAsync(
            FlowRun flow,
            ReviewedCandidateIdentity identity,
            CancellationToken cancellationToken = default)
        {
            VerifyCalls++;
            return inner.VerifyAsync(flow, identity, cancellationToken);
        }
    }

    private sealed class ScenarioDbContextFactory(
        DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);

        public Task<HarnessDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HarnessDbContext(options));
    }
}

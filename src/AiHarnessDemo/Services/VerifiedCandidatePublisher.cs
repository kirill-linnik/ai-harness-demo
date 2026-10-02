using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Data;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

public interface IVerifiedCandidatePublisher
{
    Task<string> PublishAsync(
        FlowRun flow,
        Guid publicationStepId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The exact readiness identity that authorized one publication. Every value is read from durable
/// rows immediately before the first remote side effect and stamped onto the publication journal.
/// </summary>
public sealed record DeliveryReadinessPublicationAuthorization(
    Guid ReviewedCandidateId,
    Guid ReadinessSnapshotId,
    string ReadinessContractHash,
    Guid CustomerReviewGateId,
    string WaiverSetHash);

/// <summary>
/// Performs remote side effects from the immutable candidate manifest. The Release Engineer still
/// supplies the publication turn, but its Copilot process remains push-guarded for governed flows.
/// </summary>
public sealed partial class VerifiedCandidatePublisher(
    ProcessRunner processRunner,
    CandidateFingerprintService candidateFingerprintService,
    IReviewedCandidateService reviewedCandidateService,
    IDbContextFactory<HarnessDbContext> databaseFactory)
    : IVerifiedCandidatePublisher
{
    internal const string ReviewedPublicationStartedEventType =
        "delivery.reviewed-publication.started";

    internal const string ReviewedPublicationRepositoryEventType =
        "delivery.reviewed-publication.repository-published";

    internal const string ReviewedPublicationAlreadyCurrentEventType =
        "delivery.reviewed-publication.repository-current";

    internal const string ReviewedPublicationNoDiffEventType =
        "delivery.reviewed-publication.repository-no-diff";

    internal const string ReviewedPublicationCompletedEventType =
        "delivery.reviewed-publication.completed";

    /// <summary>
    /// Serializes concurrent publication attempts for the same flow and publication root inside one
    /// process. Crashes and restarts are handled by the durable journal instead, so this lock only
    /// has to stop two in-flight callers from racing the same external side effects.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PublicationGates =
        new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions PublicationEventJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling =
            System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    [GeneratedRegex(
        @"https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/pull/\d+",
        RegexOptions.CultureInvariant)]
    private static partial Regex PullRequestUrlPattern();

    public async Task<string> PublishAsync(
        FlowRun flow,
        Guid publicationStepId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var publicationState = await LoadPublicationStateAsync(
            flow.Id,
            publicationStepId,
            cancellationToken);
        flow = publicationState.Flow;
        if (flow.Kind != FlowKind.Delivery)
        {
            throw new InvalidOperationException(
                "Only Delivery flows can publish a reviewed candidate.");
        }

        var outcome = OutcomeTypeRules.RequireDelivery(
            flow.Outcome,
            nameof(flow.Outcome));
        var publicationStep = publicationState.Step;
        var publicationRootId =
            publicationStep.StableSemanticRootId ?? publicationStep.Id;
        var authorization = AuthorizeReviewedPublication(
            flow,
            publicationStep,
            publicationRootId);
        var readiness = await AuthorizeReadinessAsync(
            flow,
            authorization,
            cancellationToken);
        candidateFingerprintService.ValidateWorkspaceRoot(flow);
        return await PublishReviewedCandidateAsync(
            flow,
            publicationRootId,
            outcome,
            authorization,
            readiness,
            cancellationToken);
    }
    private static ReviewedPublicationAuthorization AuthorizeReviewedPublication(
        FlowRun flow,
        FlowStep publicationStep,
        Guid publicationRootId)
    {
        if (flow.Kind != FlowKind.Delivery ||
            publicationStep.FlowRunId != flow.Id ||
            publicationStep.Iteration != flow.Iteration ||
            publicationRootId == Guid.Empty ||
            publicationStep.Status != StepStatus.Running ||
            !ReviewCoordinator.IsPublicationStep(flow, publicationStep))
        {
            throw new InvalidOperationException(
                "Host-controlled reviewed-candidate publication requires the durable approved Studio Publish step.");
        }
        _ = ReviewCoordinator.RequireRemotePublicationAuthority(
            flow,
            publicationStep);
        var currentPublicationStep = flow.Steps
            .Where(step =>
                step.Iteration == flow.Iteration &&
                ReviewCoordinator.IsPublicationStep(flow, step))
            .OrderBy(step => step.Sequence)
            .ThenBy(step => step.Attempt)
            .ThenBy(step => step.StartedAt)
            .LastOrDefault();
        if (currentPublicationStep is null ||
            currentPublicationStep.Id != publicationStep.Id ||
            (currentPublicationStep.StableSemanticRootId ??
             currentPublicationStep.Id) != publicationRootId)
        {
            throw new InvalidOperationException(
                "Only the current Studio publication step and its exact semantic root may publish.");
        }
        var currentSteps = flow.Steps
            .Where(step => step.Iteration == flow.Iteration)
            .ToDictionary(step => step.Id);
        var reviewedOwnerStepIds = flow.GateRecords
            .Where(gate =>
                gate.FlowRunId == flow.Id &&
                gate.ActionType == HandoffActionType.CustomerReview &&
                gate.Resolved &&
                gate.Approved == true &&
                gate.ReviewDecision == ReviewDecision.Accepted &&
                currentSteps.ContainsKey(gate.FlowStepId))
            .Select(gate => gate.FlowStepId)
            .Distinct()
            .ToArray();
        if (reviewedOwnerStepIds.Length != 1)
        {
            throw new InvalidOperationException(
                reviewedOwnerStepIds.Length == 0
                    ? "Host-controlled publication requires a resolved, approved customer review that accepted the current Delivery outcome."
                    : "The current Delivery iteration has conflicting accepted customer reviews.");
        }
        var outcomeOwnerStep = currentSteps[reviewedOwnerStepIds[0]];
        if (!outcomeOwnerStep.IsOutcomeOwner ||
            outcomeOwnerStep.Status != StepStatus.Completed ||
            string.IsNullOrWhiteSpace(flow.OutcomeOwnerPlanStepKey) ||
            !string.Equals(
                outcomeOwnerStep.PlanStepKey,
                flow.OutcomeOwnerPlanStepKey,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The accepted customer review is not bound to the current completed Delivery outcome owner.");
        }
        return new ReviewedPublicationAuthorization(
            outcomeOwnerStep.Id,
            ReviewedCandidateLedger.Read(flow, outcomeOwnerStep.Id));
    }

    /// <summary>
    /// Re-reads the authoritative readiness, candidate, waiver, and accepted-review rows from the
    /// database and refuses publication unless every binding still matches exactly. A denial is
    /// recorded as a durable event before the exception propagates.
    /// </summary>
    private async Task<DeliveryReadinessPublicationAuthorization> AuthorizeReadinessAsync(
        FlowRun flow,
        ReviewedPublicationAuthorization authorization,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var readinessService = new DeliveryReadinessService();
        DeliveryReadinessBinding? binding = null;
        string? failure = null;
        try
        {
            binding = await readinessService.AuthorizeAsync(
                database,
                flow.Id,
                expectedCandidateId: null,
                expectedContractHash: null,
                expectedRevision: null,
                DeliveryReadinessState.ReadyToApprove,
                DeliveryReadinessConflicts.PublicationNotAuthorized,
                cancellationToken);
        }
        catch (DeliveryReadinessConflictException exception)
        {
            failure = exception.Message;
        }
        if (binding is not null &&
            !string.Equals(
                binding.Record.CandidateFingerprint,
                authorization.Identity.Fingerprint,
                StringComparison.Ordinal))
        {
            failure =
                "The readiness assessment is not bound to the candidate sealed for this publication.";
        }
        var acceptedGate = await database.GateRecords
            .Where(gate =>
                gate.FlowRunId == flow.Id &&
                gate.ActionType == HandoffActionType.CustomerReview &&
                gate.Resolved &&
                gate.Approved == true &&
                gate.ReviewDecision == ReviewDecision.Accepted &&
                gate.FlowStepId == authorization.OutcomeOwnerStepId)
            .OrderByDescending(gate => gate.ResolvedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (binding is not null && acceptedGate is null)
        {
            failure =
                "Publication requires an accepted ordinary customer review; a waiver is not acceptance.";
        }
        if (failure is not null || binding is null || acceptedGate is null)
        {
            database.FlowEvents.Add(DeliveryReadinessService.DenialEvent(
                flow.Id,
                authorization.OutcomeOwnerStepId,
                DeliveryReadinessConflicts.PublicationNotAuthorized,
                "Publication was refused before any remote side effect: " +
                (failure ?? "no current readiness authorization exists."),
                binding));
            await database.SaveChangesAsync(cancellationToken);
            throw new DeliveryReadinessConflictException(
                DeliveryReadinessConflicts.PublicationNotAuthorized,
                failure ?? "No current readiness authorization exists for this publication.",
                binding?.State,
                binding?.Revision,
                binding?.ContractHash);
        }
        return new DeliveryReadinessPublicationAuthorization(
            binding.Candidate.Id,
            binding.Record.Id,
            binding.ContractHash,
            acceptedGate.Id,
            binding.WaiverSetHash);
    }

    private async Task<string> PublishReviewedCandidateAsync(
        FlowRun flow,
        Guid publicationRootId,
        OutcomeType outcome,
        ReviewedPublicationAuthorization authorization,
        DeliveryReadinessPublicationAuthorization readiness,
        CancellationToken cancellationToken)
    {
        var identity = authorization.Identity;
        var gate = PublicationGates.GetOrAdd(
            $"{flow.Id:D}:{publicationRootId:D}",
            _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var journal = await ReconcilePublicationJournalAsync(
                flow,
                publicationRootId,
                identity,
                cancellationToken);
            var alreadyPublished = TryBuildCompletedPublicationReport(
                flow,
                outcome,
                identity,
                journal);
            if (alreadyPublished is not null)
            {
                // Validate that the local reviewed bytes are still current, then
                // skip every remote publication operation. In particular, do not
                // reacquire a token, reconcile a branch, or contact GitHub.
                _ = await reviewedCandidateService.VerifyAsync(
                    flow,
                    identity,
                    cancellationToken);
                return alreadyPublished;
            }
            var pullRequestBody = outcome == OutcomeType.PullRequest
                ? BuildPullRequestBody(flow, identity)
                : string.Empty;
            foreach (var repository in identity.Repositories)
            {
                _ = await OpenRepositoryPublicationIntentAsync(
                    flow,
                    publicationRootId,
                    identity,
                    repository,
                    cancellationToken);
            }
            // The readiness binding is stamped onto every durable publication intent before any
            // remote side effect, so a later reader can prove which assessment authorized it.
            await BindReadinessAuthorizationAsync(
                flow.Id,
                publicationRootId,
                readiness,
                cancellationToken);
            // Every repository intent is durable before validation invokes local
            // Git. Validation still precedes every remote publication side effect.
            var candidate = await reviewedCandidateService.VerifyAsync(
                flow,
                identity,
                cancellationToken);
            await RecordReviewedPublicationEventAsync(
                flow.Id,
                publicationRootId,
                ReviewedPublicationStartedEventType,
                new ReviewedPublicationEventData(
                    identity.Fingerprint,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty),
                "Host-controlled publication started for the candidate sealed before customer review.",
                cancellationToken);

            return outcome switch
            {
                OutcomeType.Commit =>
                    await PublishReviewedCommitsAsync(
                        flow,
                        publicationRootId,
                        identity,
                        candidate,
                        readiness,
                        cancellationToken),
                OutcomeType.PullRequest =>
                    await PublishReviewedPullRequestsAsync(
                        flow,
                        publicationRootId,
                        identity,
                        candidate,
                        readiness,
                        pullRequestBody,
                        cancellationToken),
                _ => throw new UnreachableException()
            };
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Records the exact readiness, reviewed-candidate, accepted-review, and waiver-set identity on
    /// every durable publication row for this publication root.
    /// </summary>
    private async Task BindReadinessAuthorizationAsync(
        Guid flowId,
        Guid publicationRootId,
        DeliveryReadinessPublicationAuthorization readiness,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var records = await database.ReviewedPublicationRecords
            .Where(item =>
                item.FlowRunId == flowId &&
                item.PublicationRootId == publicationRootId)
            .ToListAsync(cancellationToken);
        var changed = false;
        foreach (var record in records)
        {
            if (record.ReviewedCandidateId != Guid.Empty &&
                (record.ReviewedCandidateId != readiness.ReviewedCandidateId ||
                 !string.Equals(
                     record.ReadinessContractHash,
                     readiness.ReadinessContractHash,
                     StringComparison.Ordinal)))
            {
                throw new DeliveryReadinessConflictException(
                    DeliveryReadinessConflicts.PublicationNotAuthorized,
                    "The durable publication intent is bound to a different readiness assessment.");
            }
            if (record.ReviewedCandidateId == readiness.ReviewedCandidateId &&
                record.ReadinessSnapshotId == readiness.ReadinessSnapshotId &&
                record.CustomerReviewGateId == readiness.CustomerReviewGateId &&
                string.Equals(
                    record.WaiverSetHash,
                    readiness.WaiverSetHash,
                    StringComparison.Ordinal))
            {
                continue;
            }
            record.ReviewedCandidateId = readiness.ReviewedCandidateId;
            record.ReadinessSnapshotId = readiness.ReadinessSnapshotId;
            record.ReadinessContractHash = readiness.ReadinessContractHash;
            record.CustomerReviewGateId = readiness.CustomerReviewGateId;
            record.WaiverSetHash = readiness.WaiverSetHash;
            record.UpdatedAt = DateTimeOffset.UtcNow;
            changed = true;
        }
        if (changed)
        {
            await database.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<string> PublishReviewedCommitsAsync(
        FlowRun flow,
        Guid publicationRootId,
        ReviewedCandidateIdentity identity,
        OutcomeCandidateSnapshot candidate,
        DeliveryReadinessPublicationAuthorization readiness,
        CancellationToken cancellationToken)
    {
        foreach (var repository in candidate.Manifest.Repositories)
        {
            var record = await OpenRepositoryPublicationIntentAsync(
                flow,
                publicationRootId,
                identity,
                repository,
                cancellationToken);
            if (record.Stage == ReviewedPublicationStage.Completed)
            {
                continue;
            }
            await RecordReviewedPublicationEventAsync(
                flow.Id,
                publicationRootId,
                ReviewedPublicationRepositoryEventType,
                new ReviewedPublicationEventData(
                    identity.Fingerprint,
                    repository.RelativePath,
                    repository.RemoteRepository,
                    string.Empty,
                    repository.Head,
                    repository.Tree),
                $"Retained reviewed commit {repository.Head} for '{repository.RelativePath}' without a remote side effect.",
                cancellationToken);
            _ = await AdvanceRepositoryPublicationAsync(
                flow.Id,
                publicationRootId,
                repository.RelativePath,
                ReviewedPublicationStage.Completed,
                pullRequestUrl: null,
                cancellationToken);
        }
        await BindReadinessAuthorizationAsync(
            flow.Id,
            publicationRootId,
            readiness,
            cancellationToken);
        return await CompleteReviewedPublicationAsync(
            flow,
            publicationRootId,
            OutcomeType.Commit,
            identity,
            candidate,
            new Dictionary<string, string>(StringComparer.Ordinal),
            "Host-controlled commit publication completed without changing the reviewed candidate.",
            cancellationToken);
    }

    private async Task<string> PublishReviewedPullRequestsAsync(
        FlowRun flow,
        Guid publicationRootId,
        ReviewedCandidateIdentity identity,
        OutcomeCandidateSnapshot candidate,
        DeliveryReadinessPublicationAuthorization readiness,
        string pullRequestBody,
        CancellationToken cancellationToken)
    {
        var pullRequests = new Dictionary<string, string>(StringComparer.Ordinal);
        var noDiffRepositories = new HashSet<string>(StringComparer.Ordinal);
        string? gitHubToken = null;
        IReadOnlyDictionary<string, string?>? ghEnvironment = null;
        foreach (var repository in candidate.Manifest.Repositories)
        {
            // Intent is durable before the first external byte leaves this process, so a crash
            // between repositories reconciles instead of repeating remote work.
            var record = await OpenRepositoryPublicationIntentAsync(
                flow,
                publicationRootId,
                identity,
                repository,
                cancellationToken);
            if (record.Stage == ReviewedPublicationStage.Completed)
            {
                pullRequests[repository.RelativePath] =
                    RequirePublishedPullRequestUrl(record);
                continue;
            }
            var repositoryName = repository.RemoteRepository;
            if (string.IsNullOrWhiteSpace(repositoryName))
            {
                throw new InvalidOperationException(
                    $"Reviewed repository '{repository.RelativePath}' has no trusted GitHub publication target.");
            }
            var workspaceRepository = ResolveWorkspaceRepository(
                flow.WorkspacePath,
                repository.RelativePath);
            var trustedRemoteUrl = BuildTrustedGitHubRemoteUrl(repositoryName);
            if ((record.Stage is
                     ReviewedPublicationStage.Intent or
                     ReviewedPublicationStage.AlreadyCurrent or
                     ReviewedPublicationStage.NoPullRequestDiff) &&
                string.IsNullOrWhiteSpace(record.PullRequestUrl))
            {
                gitHubToken ??= await ResolveGitHubTokenAsync(
                    AppContext.BaseDirectory,
                    cancellationToken);
                ghEnvironment ??= BuildGitHubCliEnvironment(gitHubToken);
                var defaultHead = await ReadRemoteDefaultHeadAsync(
                    workspaceRepository,
                    trustedRemoteUrl,
                    gitHubToken,
                    cancellationToken);
                var matchesReviewedHead = string.Equals(
                    defaultHead,
                    repository.Head,
                    StringComparison.OrdinalIgnoreCase);
                if (record.Stage == ReviewedPublicationStage.AlreadyCurrent &&
                    !matchesReviewedHead)
                {
                    throw new InvalidOperationException(
                        $"The default branch of '{repositoryName}' no longer holds the reviewed unchanged commit {repository.Head}.");
                }
                if (record.Stage == ReviewedPublicationStage.NoPullRequestDiff ||
                    record.Stage == ReviewedPublicationStage.Intent &&
                    !matchesReviewedHead)
                {
                    var noDiff = await HasNoPullRequestDiffAsync(
                        repositoryName,
                        defaultHead,
                        repository.Head,
                        ghEnvironment!,
                        cancellationToken);
                    if (record.Stage == ReviewedPublicationStage.NoPullRequestDiff &&
                        !noDiff)
                    {
                        throw new InvalidOperationException(
                            $"The reviewed repository '{repositoryName}' no longer has an empty pull-request diff against the remote default branch.");
                    }
                    if (noDiff)
                    {
                        if (record.Stage == ReviewedPublicationStage.Intent)
                        {
                            _ = await AdvanceRepositoryPublicationAsync(
                                flow.Id,
                                publicationRootId,
                                repository.RelativePath,
                                ReviewedPublicationStage.NoPullRequestDiff,
                                pullRequestUrl: null,
                                cancellationToken);
                        }
                        noDiffRepositories.Add(repository.RelativePath);
                        await RecordReviewedPublicationEventAsync(
                            flow.Id,
                            publicationRootId,
                            ReviewedPublicationNoDiffEventType,
                            new ReviewedPublicationEventData(
                                identity.Fingerprint,
                                repository.RelativePath,
                                repositoryName,
                                string.Empty,
                                repository.Head,
                                repository.Tree),
                            $"Reviewed commit {repository.Head} for '{repository.RelativePath}' has no changes relative to the remote default branch; no pull request was needed.",
                            cancellationToken);
                        continue;
                    }
                }
                if (matchesReviewedHead)
                {
                    if (record.Stage == ReviewedPublicationStage.Intent)
                    {
                        _ = await AdvanceRepositoryPublicationAsync(
                            flow.Id,
                            publicationRootId,
                            repository.RelativePath,
                            ReviewedPublicationStage.AlreadyCurrent,
                            pullRequestUrl: null,
                            cancellationToken);
                    }
                    await RecordReviewedPublicationEventAsync(
                        flow.Id,
                        publicationRootId,
                        ReviewedPublicationAlreadyCurrentEventType,
                        new ReviewedPublicationEventData(
                            identity.Fingerprint,
                            repository.RelativePath,
                            repositoryName,
                            string.Empty,
                            repository.Head,
                            repository.Tree),
                        $"Reviewed commit {repository.Head} for '{repository.RelativePath}' already matches the remote default branch; no pull request was needed.",
                        cancellationToken);
                    continue;
                }
            }

            // A recorded PR URL is the strongest remote identity in the journal. Reconcile that
            // exact PR before looking at the branch: GitHub commonly deletes a head branch after
            // merge, but the merged PR still retains the reviewed head identity.
            PublishedPullRequestIdentity? reconciled = null;
            if (!string.IsNullOrWhiteSpace(record.PullRequestUrl))
            {
                if (record.Stage is
                    ReviewedPublicationStage.AlreadyCurrent or
                    ReviewedPublicationStage.NoPullRequestDiff)
                {
                    throw new InvalidOperationException(
                        $"The repository without a pull request '{repository.RelativePath}' cannot have a pull request URL.");
                }
                if (gitHubToken is null)
                {
                    // Acquiring a token is itself a side effect, so it waits until durable state
                    // proves that real remote work remains.
                    gitHubToken = await ResolveGitHubTokenAsync(
                        AppContext.BaseDirectory,
                        cancellationToken);
                    ghEnvironment = BuildGitHubCliEnvironment(gitHubToken);
                }
                reconciled = await ReconcilePullRequestAsync(
                    repositoryName,
                    flow.BranchName,
                    repository.Head,
                    record.PullRequestUrl,
                    ghEnvironment!,
                    cancellationToken);
            }

            if (!string.Equals(
                    reconciled?.State,
                    "MERGED",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (gitHubToken is null)
                {
                    gitHubToken = await ResolveGitHubTokenAsync(
                        AppContext.BaseDirectory,
                        cancellationToken);
                    ghEnvironment = BuildGitHubCliEnvironment(gitHubToken);
                }
                var remoteHead = await ReadRemoteBranchHeadAsync(
                    workspaceRepository,
                    trustedRemoteUrl,
                    flow.BranchName,
                    gitHubToken,
                    cancellationToken);
                var branchHoldsReviewedCommit = string.Equals(
                    remoteHead,
                    repository.Head,
                    StringComparison.OrdinalIgnoreCase);
                if ((record.Stage >= ReviewedPublicationStage.BranchPublished ||
                     reconciled is not null) &&
                    !branchHoldsReviewedCommit)
                {
                    throw new InvalidOperationException(
                        $"The remote branch '{flow.BranchName}' in '{repositoryName}' no longer holds the reviewed commit {repository.Head}.");
                }
                if (!branchHoldsReviewedCommit)
                {
                    _ = await PublishVerifiedGitBranchAsync(
                        processRunner,
                        workspaceRepository,
                        repository,
                        trustedRemoteUrl,
                        flow.BranchName,
                        gitHubToken,
                        cancellationToken);
                }
                record = await AdvanceRepositoryPublicationAsync(
                    flow.Id,
                    publicationRootId,
                    repository.RelativePath,
                    ReviewedPublicationStage.BranchPublished,
                    pullRequestUrl: null,
                    cancellationToken);
            }

            if (reconciled is null)
            {
                reconciled = await ReconcilePullRequestAsync(
                    repositoryName,
                    flow.BranchName,
                    repository.Head,
                    recordedPullRequestUrl: null,
                    ghEnvironment!,
                    cancellationToken);
            }
            var pullRequestUrl = reconciled?.Url;
            if (pullRequestUrl is null)
            {
                var create = await RunRequiredAsync(
                    "gh",
                    [
                        "pr", "create",
                        "--repo", repositoryName,
                        "--head", flow.BranchName,
                        "--title", Clip(flow.Title, 200),
                        "--body", pullRequestBody
                    ],
                    AppContext.BaseDirectory,
                    TimeSpan.FromMinutes(2),
                    $"create the pull request for reviewed repository '{repository.RelativePath}'",
                    cancellationToken,
                    ghEnvironment);
                pullRequestUrl = PullRequestUrlPattern()
                    .Match(create.CombinedOutput)
                    .Value;
                if (string.IsNullOrWhiteSpace(pullRequestUrl))
                {
                    throw new InvalidOperationException(
                        $"GitHub did not return a pull request URL for reviewed repository '{repository.RelativePath}'.");
                }
                var createdReference =
                    PublishedOutcomeVerifier.ParsePullRequest(pullRequestUrl);
                if (createdReference is null ||
                    !string.Equals(
                        createdReference.Repository,
                        repositoryName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"GitHub returned a pull request outside reviewed repository '{repositoryName}'.");
                }
            }
            record = await AdvanceRepositoryPublicationAsync(
                flow.Id,
                publicationRootId,
                repository.RelativePath,
                ReviewedPublicationStage.PullRequestOpened,
                pullRequestUrl,
                cancellationToken);
            pullRequests[repository.RelativePath] =
                RequirePublishedPullRequestUrl(record);
            await RecordReviewedPublicationEventAsync(
                flow.Id,
                publicationRootId,
                ReviewedPublicationRepositoryEventType,
                new ReviewedPublicationEventData(
                    identity.Fingerprint,
                    repository.RelativePath,
                    repositoryName,
                    pullRequestUrl,
                    repository.Head,
                    repository.Tree),
                $"Published reviewed commit {repository.Head} for '{repository.RelativePath}' at {pullRequestUrl}.",
                cancellationToken);
            _ = await AdvanceRepositoryPublicationAsync(
                flow.Id,
                publicationRootId,
                repository.RelativePath,
                ReviewedPublicationStage.Completed,
                pullRequestUrl,
                cancellationToken);
        }

        await BindReadinessAuthorizationAsync(
            flow.Id,
            publicationRootId,
            readiness,
            cancellationToken);
        return await CompleteReviewedPublicationAsync(
            flow,
            publicationRootId,
            OutcomeType.PullRequest,
            identity,
            candidate,
            pullRequests,
            "Host-controlled publication completed without changing the candidate sealed before review.",
            cancellationToken,
            noDiffRepositories);
    }

    private async Task<string> CompleteReviewedPublicationAsync(
        FlowRun flow,
        Guid publicationRootId,
        OutcomeType outcome,
        ReviewedCandidateIdentity identity,
        OutcomeCandidateSnapshot candidate,
        IReadOnlyDictionary<string, string> pullRequests,
        string message,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? noDiffRepositories = null)
    {
        _ = await reviewedCandidateService.VerifyAsync(
            flow,
            identity,
            cancellationToken);
        await RecordReviewedPublicationEventAsync(
            flow.Id,
            publicationRootId,
            ReviewedPublicationCompletedEventType,
            new ReviewedPublicationEventData(
                identity.Fingerprint,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty),
            message,
            cancellationToken);
        return BuildReviewedPublicationReport(
            outcome,
            identity.Fingerprint,
            identity.Repositories,
            pullRequests,
            noDiffRepositories);
    }

    /// <summary>
    /// Re-reads and validates the current durable publication journal inside the publication lock.
    /// </summary>
    private async Task<ReviewedPublicationJournalState>
        ReconcilePublicationJournalAsync(
            FlowRun flow,
            Guid publicationRootId,
            ReviewedCandidateIdentity identity,
            CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var records = await database.ReviewedPublicationRecords
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.PublicationRootId == publicationRootId)
            .ToListAsync(cancellationToken);
        var events = await database.FlowEvents
            .AsNoTracking()
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.FlowStepId == publicationRootId &&
                (item.Type == ReviewedPublicationRepositoryEventType ||
                 item.Type == ReviewedPublicationAlreadyCurrentEventType ||
                item.Type == ReviewedPublicationNoDiffEventType ||
                item.Type == ReviewedPublicationCompletedEventType))
            .ToListAsync(cancellationToken);
        foreach (var record in records)
        {
            var sealedRepository = identity.Repositories.SingleOrDefault(
                repository => string.Equals(
                    repository.RelativePath,
                    record.RelativePath,
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"The durable reviewed publication journal refers to unknown repository '{record.RelativePath}'.");
            RequireReconcilableRecord(
                record,
                identity,
                sealedRepository,
                flow.BranchName);
        }
        return new ReviewedPublicationJournalState(events, records);
    }

    private static string? TryBuildCompletedPublicationReport(
        FlowRun flow,
        OutcomeType outcome,
        ReviewedCandidateIdentity identity,
        ReviewedPublicationJournalState journal)
    {
        var completions = journal.Events
            .Where(item => item.Type == ReviewedPublicationCompletedEventType)
            .ToArray();
        if (completions.Length == 0)
        {
            return null;
        }
        if (completions.Length > 1)
        {
            throw new InvalidOperationException(
                "The durable reviewed publication journal has duplicate completion events.");
        }
        if (!string.Equals(
                ReadReviewedPublicationEvent(completions[0].DataJson)
                    .CandidateFingerprint,
                identity.Fingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The durable reviewed publication journal completed a different reviewed candidate.");
        }
        var pullRequests = new Dictionary<string, string>(StringComparer.Ordinal);
        var noDiffRepositories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var repository in identity.Repositories)
        {
            var record = journal.Records.SingleOrDefault(item =>
                             string.Equals(
                                 item.RelativePath,
                                 repository.RelativePath,
                                 StringComparison.Ordinal))
                         ?? throw new InvalidOperationException(
                             $"The completed reviewed publication has no durable record for '{repository.RelativePath}'.");
            RequireReconcilableRecord(
                record,
                identity,
                repository,
                flow.BranchName);
            if (record.Stage is not (
                    ReviewedPublicationStage.Completed or
                    ReviewedPublicationStage.AlreadyCurrent or
                    ReviewedPublicationStage.NoPullRequestDiff) ||
                (record.Stage is
                    ReviewedPublicationStage.AlreadyCurrent or
                    ReviewedPublicationStage.NoPullRequestDiff) &&
                outcome != OutcomeType.PullRequest)
            {
                throw new InvalidOperationException(
                    $"The completed reviewed publication left '{repository.RelativePath}' at stage {record.Stage}.");
            }
            if (outcome == OutcomeType.PullRequest &&
                record.Stage == ReviewedPublicationStage.Completed)
            {
                pullRequests[repository.RelativePath] =
                    RequirePublishedPullRequestUrl(record);
            }
            else if ((record.Stage is
                          ReviewedPublicationStage.AlreadyCurrent or
                          ReviewedPublicationStage.NoPullRequestDiff) &&
                     !string.IsNullOrWhiteSpace(record.PullRequestUrl))
            {
                throw new InvalidOperationException(
                    $"A repository without a pull request '{repository.RelativePath}' cannot have a pull request URL.");
            }
            if (record.Stage == ReviewedPublicationStage.NoPullRequestDiff)
            {
                noDiffRepositories.Add(repository.RelativePath);
            }
        }
        return BuildReviewedPublicationReport(
            outcome,
            identity.Fingerprint,
            identity.Repositories,
            pullRequests,
            noDiffRepositories);
    }

    private Task<ReviewedPublicationRecord>
        OpenRepositoryPublicationIntentAsync(
            FlowRun flow,
            Guid publicationRootId,
            ReviewedCandidateIdentity identity,
            CandidateRepositoryManifest repository,
            CancellationToken cancellationToken) =>
        OpenRepositoryPublicationIntentAsync(
            flow,
            publicationRootId,
            identity,
            new ReviewedCandidateRepositoryIdentity(
                repository.RelativePath,
                repository.Head,
                repository.Tree,
                repository.RemoteRepository),
            cancellationToken);

    private async Task<ReviewedPublicationRecord>
        OpenRepositoryPublicationIntentAsync(
            FlowRun flow,
            Guid publicationRootId,
            ReviewedCandidateIdentity identity,
            ReviewedCandidateRepositoryIdentity repository,
            CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var record = await database.ReviewedPublicationRecords
            .SingleOrDefaultAsync(
                item =>
                    item.FlowRunId == flow.Id &&
                    item.PublicationRootId == publicationRootId &&
                    item.RelativePath == repository.RelativePath,
                cancellationToken);
        if (record is null)
        {
            record = new ReviewedPublicationRecord
            {
                FlowRunId = flow.Id,
                PublicationRootId = publicationRootId,
                Iteration = identity.Iteration,
                CandidateFingerprint = identity.Fingerprint,
                RelativePath = repository.RelativePath,
                RemoteRepository = repository.RemoteRepository,
                BranchName = flow.BranchName,
                Head = repository.Head,
                Tree = repository.Tree,
                Stage = ReviewedPublicationStage.Intent
            };
            database.ReviewedPublicationRecords.Add(record);
            await database.SaveChangesAsync(cancellationToken);
            return record;
        }
        RequireReconcilableRecord(
            record,
            identity,
            repository,
            flow.BranchName);
        return record;
    }

    private async Task<ReviewedPublicationRecord>
        AdvanceRepositoryPublicationAsync(
            Guid flowId,
            Guid publicationRootId,
            string relativePath,
            ReviewedPublicationStage stage,
            string? pullRequestUrl,
            CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var record = await database.ReviewedPublicationRecords
            .SingleAsync(
                item =>
                    item.FlowRunId == flowId &&
                    item.PublicationRootId == publicationRootId &&
                    item.RelativePath == relativePath,
                cancellationToken);
        if (!string.IsNullOrWhiteSpace(pullRequestUrl))
        {
            if (!string.IsNullOrWhiteSpace(record.PullRequestUrl) &&
                !string.Equals(
                    record.PullRequestUrl,
                    pullRequestUrl,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The durable publication journal already published a different pull request for '{relativePath}'.");
            }
            record.PullRequestUrl = pullRequestUrl;
        }
        if ((stage is
                 ReviewedPublicationStage.AlreadyCurrent or
                 ReviewedPublicationStage.NoPullRequestDiff) &&
            (record.Stage != ReviewedPublicationStage.Intent ||
             !string.IsNullOrWhiteSpace(record.PullRequestUrl)) ||
            (record.Stage is
                ReviewedPublicationStage.AlreadyCurrent or
                ReviewedPublicationStage.NoPullRequestDiff) &&
            stage != record.Stage)
        {
            throw new InvalidOperationException(
                $"The publication journal for '{relativePath}' cannot switch between a no-PR state and a pull request.");
        }
        record.Stage = (ReviewedPublicationStage)Math.Max(
            (int)record.Stage,
            (int)stage);
        record.UpdatedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        return record;
    }

    private static void RequireReconcilableRecord(
        ReviewedPublicationRecord record,
        ReviewedCandidateIdentity identity,
        CandidateRepositoryManifest repository,
        string branchName) =>
        RequireReconcilableRecord(
            record,
            identity,
            new ReviewedCandidateRepositoryIdentity(
                repository.RelativePath,
                repository.Head,
                repository.Tree,
                repository.RemoteRepository),
            branchName);

    private static void RequireReconcilableRecord(
        ReviewedPublicationRecord record,
        ReviewedCandidateIdentity identity,
        ReviewedCandidateRepositoryIdentity repository,
        string branchName)
    {
        if (!string.Equals(
                record.CandidateFingerprint,
                identity.Fingerprint,
                StringComparison.Ordinal) ||
            record.Iteration != identity.Iteration ||
            !string.Equals(
                record.Head,
                repository.Head,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                record.Tree,
                repository.Tree,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                record.RemoteRepository,
                repository.RemoteRepository,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                record.BranchName,
                branchName,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The durable publication journal for '{repository.RelativePath}' conflicts with the reviewed candidate.");
        }
        if (!string.IsNullOrWhiteSpace(record.PullRequestUrl))
        {
            var reference = PublishedOutcomeVerifier.ParsePullRequest(
                record.PullRequestUrl);
            if (reference is null ||
                string.IsNullOrWhiteSpace(repository.RemoteRepository) ||
                !string.Equals(
                    reference.Repository,
                    repository.RemoteRepository,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The durable publication journal for '{repository.RelativePath}' contains a conflicting pull request URL.");
            }
        }
    }

    private static string RequirePublishedPullRequestUrl(
        ReviewedPublicationRecord record) =>
        string.IsNullOrWhiteSpace(record.PullRequestUrl)
            ? throw new InvalidOperationException(
                $"The durable publication journal for '{record.RelativePath}' has no pull request URL.")
            : record.PullRequestUrl;

    private async Task<bool> HasNoPullRequestDiffAsync(
        string repositoryName,
        string defaultHead,
        string reviewedHead,
        IReadOnlyDictionary<string, string?> ghEnvironment,
        CancellationToken cancellationToken)
    {
        var compare = await processRunner.RunAsync(
            "gh",
            ["api", $"repos/{repositoryName}/compare/{defaultHead}...{reviewedHead}"],
            AppContext.BaseDirectory,
            TimeSpan.FromSeconds(30),
            cancellationToken,
            environmentVariables: ghEnvironment);
        if (compare.ExitCode != 0)
        {
            if (compare.ExitCode == 1 &&
                compare.CombinedOutput.Contains(
                    "Not Found (HTTP 404)", StringComparison.Ordinal))
            {
                return false;
            }
            throw new InvalidOperationException(
                $"Unable to compare the reviewed head with the remote default branch in '{repositoryName}': {compare.CombinedOutput}");
        }
        return IsVerifiedEmptyCompare(
            compare.StandardOutput, defaultHead, reviewedHead);
    }

    internal static bool IsVerifiedEmptyCompare(
        string response,
        string defaultHead,
        string reviewedHead)
    {
        using var document = JsonDocument.Parse(response);
        var comparison = document.RootElement;
        if (comparison.ValueKind != JsonValueKind.Object ||
            !comparison.TryGetProperty("base_commit", out var baseCommit) ||
            baseCommit.ValueKind != JsonValueKind.Object ||
            !baseCommit.TryGetProperty("sha", out var baseSha) ||
            !string.Equals(
                baseSha.GetString(),
                defaultHead,
                StringComparison.OrdinalIgnoreCase) ||
            !comparison.TryGetProperty("status", out var status) ||
            status.ValueKind != JsonValueKind.String ||
            status.GetString() is not (
                "identical" or "ahead" or "behind" or "diverged") ||
            !comparison.TryGetProperty("ahead_by", out var ahead) ||
            !ahead.TryGetInt32(out var aheadCount) ||
            aheadCount < 0 ||
            !comparison.TryGetProperty("commits", out var commits) ||
            commits.ValueKind != JsonValueKind.Array ||
            !comparison.TryGetProperty("files", out var files) ||
            files.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "GitHub returned an incomplete reviewed-candidate comparison.");
        }
        if (aheadCount > 0)
        {
            if (commits.GetArrayLength() == 0 ||
                !commits[commits.GetArrayLength() - 1]
                    .TryGetProperty("sha", out var lastCommit) ||
                !string.Equals(
                    lastCommit.GetString(),
                    reviewedHead,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "GitHub compared a different reviewed head.");
            }
        }
        else if (!comparison.TryGetProperty(
                     "merge_base_commit", out var mergeBase) ||
                 mergeBase.ValueKind != JsonValueKind.Object ||
                 !mergeBase.TryGetProperty("sha", out var mergeSha) ||
                 !string.Equals(
                     mergeSha.GetString(),
                     reviewedHead,
                     StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "GitHub did not identify the reviewed head as the merge base.");
        }
        return files.GetArrayLength() == 0;
    }

    private async Task<string> ReadRemoteDefaultHeadAsync(
        string workingDirectory,
        string remoteUrl,
        string gitHubToken,
        CancellationToken cancellationToken)
    {
        using var sandbox = TrustedGitSandbox.Create(remoteUrl, gitHubToken);
        var result = await processRunner.RunAsync(
            "git",
            ["ls-remote", "--symref", remoteUrl, "HEAD"],
            workingDirectory,
            TimeSpan.FromMinutes(1),
            cancellationToken,
            environmentVariables: sandbox.EnvironmentVariables);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to verify the default branch at '{remoteUrl}': {result.CombinedOutput}");
        }
        var lines = result.StandardOutput
            .ReplaceLineEndings("\n")
            .Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
        if (lines.Length != 2 ||
            !lines[0].StartsWith("ref: refs/heads/", StringComparison.Ordinal) ||
            !lines[0].EndsWith("\tHEAD", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The default branch at '{remoteUrl}' has no usable symbolic HEAD.");
        }
        var parts = lines[1].Split(
            '\t',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            parts[1] != "HEAD" ||
            parts[0].Length is not (40 or 64) ||
            parts[0].Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new InvalidOperationException(
                $"The default branch at '{remoteUrl}' has no usable commit identity.");
        }
        return parts[0].ToLowerInvariant();
    }

    /// <summary>
    /// Reads the remote branch tip without mutating anything, so a retry can tell "already pushed"
    /// apart from "never pushed" instead of pushing again.
    /// </summary>
    private async Task<string?> ReadRemoteBranchHeadAsync(
        string workingDirectory,
        string remoteUrl,
        string branchName,
        string gitHubToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(branchName))
        {
            throw new InvalidOperationException(
                "Reviewed publication requires a branch name.");
        }
        using var sandbox = TrustedGitSandbox.Create(remoteUrl, gitHubToken);
        var reference = $"refs/heads/{branchName}";
        var result = await processRunner.RunAsync(
            "git",
            ["ls-remote", "--heads", remoteUrl, reference],
            workingDirectory,
            TimeSpan.FromMinutes(1),
            cancellationToken,
            environmentVariables: sandbox.EnvironmentVariables);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to reconcile remote branch '{branchName}' at '{remoteUrl}': {result.CombinedOutput}");
        }
        foreach (var line in result.StandardOutput
                     .ReplaceLineEndings("\n")
                     .Split(
                         '\n',
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(
                '\t',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                !string.Equals(parts[1], reference, StringComparison.Ordinal))
            {
                continue;
            }
            var objectId = parts[0].ToLowerInvariant();
            if (objectId.Length is not (40 or 64) ||
                objectId.Any(character => !char.IsAsciiHexDigit(character)))
            {
                throw new InvalidOperationException(
                    $"Remote branch '{branchName}' at '{remoteUrl}' reported an unusable commit identity.");
            }
            return objectId;
        }
        return null;
    }

    /// <summary>
    /// Resolves the pull request that already publishes the reviewed head, across open, closed, and
    /// merged states, and rejects anything that conflicts with the durable record.
    /// </summary>
    private async Task<PublishedPullRequestIdentity?> ReconcilePullRequestAsync(
        string repositoryName,
        string branchName,
        string expectedHead,
        string? recordedPullRequestUrl,
        IReadOnlyDictionary<string, string?> ghEnvironment,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(recordedPullRequestUrl))
        {
            var recorded = PublishedOutcomeVerifier.ParsePullRequest(
                                recordedPullRequestUrl)
                            ?? throw new InvalidOperationException(
                                $"The durable publication journal holds an unusable pull request URL for '{repositoryName}'.");
            if (!string.Equals(
                    recorded.Repository,
                    repositoryName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The durable publication journal holds a pull request outside '{repositoryName}'.");
            }
            var view = await RunRequiredAsync(
                "gh",
                [
                    "pr", "view",
                    recorded.Number.ToString(CultureInfo.InvariantCulture),
                    "--repo", repositoryName,
                    "--json", "url,number,state,headRefName,headRefOid,headRepository,headRepositoryOwner"
                ],
                AppContext.BaseDirectory,
                TimeSpan.FromSeconds(60),
                $"reconcile the recorded pull request for '{repositoryName}'",
                cancellationToken,
                ghEnvironment);
            using var document = JsonDocument.Parse(view.StandardOutput);
            var match = MatchPullRequest(
                document.RootElement,
                repositoryName,
                branchName,
                expectedHead);
            if (match is null ||
                !string.Equals(
                    match.Url,
                    recordedPullRequestUrl,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The recorded pull request for '{repositoryName}' no longer publishes reviewed commit {expectedHead}.");
            }
            RequirePublishablePullRequestState(match, repositoryName);
            return match;
        }

        var list = await RunRequiredAsync(
            "gh",
            [
                "pr", "list",
                "--repo", repositoryName,
                "--head", branchName,
                "--state", "all",
                "--limit", "100",
                "--json", "url,number,state,headRefName,headRefOid,headRepository,headRepositoryOwner"
            ],
            AppContext.BaseDirectory,
            TimeSpan.FromSeconds(60),
            $"reconcile existing pull requests for '{repositoryName}'",
            cancellationToken,
            ghEnvironment);
        using var listDocument = JsonDocument.Parse(list.StandardOutput);
        if (listDocument.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var matches = listDocument.RootElement.EnumerateArray()
            .Select(item => MatchPullRequest(
                item,
                repositoryName,
                branchName,
                expectedHead))
            .OfType<PublishedPullRequestIdentity>()
            .GroupBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (matches.Length > 1)
        {
            throw new InvalidOperationException(
                $"GitHub reports conflicting pull requests for reviewed commit {expectedHead} in '{repositoryName}'.");
        }
        var matched = matches.SingleOrDefault();
        if (matched is not null)
        {
            RequirePublishablePullRequestState(matched, repositoryName);
        }
        return matched;
    }

    private static void RequirePublishablePullRequestState(
        PublishedPullRequestIdentity pullRequest,
        string repositoryName)
    {
        if (string.Equals(
                pullRequest.State,
                "OPEN",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                pullRequest.State,
                "MERGED",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (string.Equals(
                pullRequest.State,
                "CLOSED",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Pull request '{pullRequest.Url}' in '{repositoryName}' is closed without merge; refusing to create a replacement pull request.");
        }
        throw new InvalidOperationException(
            $"Pull request '{pullRequest.Url}' in '{repositoryName}' has unsupported state '{pullRequest.State}'.");
    }

    private static PublishedPullRequestIdentity? MatchPullRequest(
        JsonElement pullRequest,
        string repositoryName,
        string branchName,
        string expectedHead)
    {
        if (pullRequest.ValueKind != JsonValueKind.Object ||
            !pullRequest.TryGetProperty("url", out var url) ||
            !pullRequest.TryGetProperty("headRefName", out var headRefName) ||
            !pullRequest.TryGetProperty("headRefOid", out var headRefOid) ||
            !string.Equals(
                headRefName.GetString(),
                branchName,
                StringComparison.Ordinal) ||
            !string.Equals(
                headRefOid.GetString(),
                expectedHead,
                StringComparison.OrdinalIgnoreCase) ||
            !PublishedOutcomeVerifier.IsExpectedHeadRepository(
                pullRequest,
                repositoryName))
        {
            return null;
        }
        var value = url.GetString() ?? string.Empty;
        if (!string.Equals(
                PullRequestUrlPattern().Match(value).Value,
                value,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var state = pullRequest.TryGetProperty("state", out var stateElement)
            ? stateElement.GetString() ?? string.Empty
            : string.Empty;
        return new PublishedPullRequestIdentity(value, state);
    }

    private static string BuildReviewedPublicationReport(
            OutcomeType outcome,
            string fingerprint,
            IReadOnlyList<ReviewedCandidateRepositoryIdentity> repositories,
            IReadOnlyDictionary<string, string> pullRequests,
            IReadOnlySet<string>? noDiffRepositories = null)
    {
        var report = new List<string>
            {
                $"Reviewed candidate: {fingerprint}"
            };
        foreach (var repository in repositories)
        {
            var publication = outcome switch
            {
                OutcomeType.Commit =>
                    $"Repository {repository.RelativePath}: commit {repository.Head}, tree {repository.Tree}",
                OutcomeType.PullRequest =>
                    pullRequests.TryGetValue(
                        repository.RelativePath, out var pullRequest)
                        ? $"Repository {repository.RelativePath}: reviewed remote commit {repository.Head}, tree {repository.Tree}, pull request {pullRequest}"
                        : noDiffRepositories?.Contains(repository.RelativePath) == true
                        ? $"Repository {repository.RelativePath}: reviewed remote commit {repository.Head}, tree {repository.Tree}, no changes relative to the default branch (no PR)"
                        : $"Repository {repository.RelativePath}: reviewed remote commit {repository.Head}, tree {repository.Tree}, already current on remote default branch (no PR)",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(outcome),
                    outcome,
                    "A Delivery publication report requires Commit or PullRequest.")
            };
            report.Add(
                publication);
        }
        return string.Join(Environment.NewLine, report);
    }

    private async Task RecordReviewedPublicationEventAsync(
            Guid flowId,
            Guid publicationRootId,
            string type,
            ReviewedPublicationEventData data,
            string message,
            CancellationToken cancellationToken)
    {
        var dataJson = JsonSerializer.Serialize(
            data,
            PublicationEventJsonOptions);
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var existing = await database.FlowEvents
            .Where(item =>
                item.FlowRunId == flowId &&
                item.FlowStepId == publicationRootId &&
                item.Type == type)
            .ToListAsync(cancellationToken);
        var sameKey = existing.Where(item =>
                string.Equals(
                    ReadReviewedPublicationEvent(item.DataJson).RelativePath,
                    data.RelativePath,
                    StringComparison.Ordinal))
            .ToArray();
        if (sameKey.Length > 1 ||
            sameKey.Length == 1 &&
            !string.Equals(
                sameKey[0].DataJson,
                dataJson,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The durable reviewed publication journal conflicts with this sealed candidate.");
        }
        if (sameKey.Length == 0)
        {
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flowId,
                FlowStepId = publicationRootId,
                Type = type,
                Message = message,
                DataJson = dataJson
            });
            await database.SaveChangesAsync(cancellationToken);
        }
    }

    private static ReviewedPublicationEventData ReadReviewedPublicationEvent(
            string? json)
    {
        try
        {
            var data = JsonSerializer.Deserialize<ReviewedPublicationEventData>(
                           json
                           ?? throw new InvalidOperationException(
                               "The reviewed publication event has no structured data."),
                           PublicationEventJsonOptions)
                       ?? throw new InvalidOperationException(
                           "The reviewed publication event has empty structured data.");
            return data;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The reviewed publication event data is invalid.",
                exception);
        }
    }

    private async Task<string?> FindOpenPullRequestAsync(
        string workspaceRepository,
        string repositoryName,
        string branchName,
        string expectedHead,
        IReadOnlyDictionary<string, string?>? environmentVariables,
        CancellationToken cancellationToken)
    {
        var list = await RunRequiredAsync(
            "gh",
            [
                "pr", "list",
                "--repo", repositoryName,
                "--head", branchName,
                "--state", "open",
                "--limit", "100",
                "--json", "url,headRefName,headRefOid,headRepository,headRepositoryOwner"
            ],
            workspaceRepository,
            TimeSpan.FromSeconds(30),
            "inspect existing pull requests",
            cancellationToken,
            environmentVariables);
        using var document = JsonDocument.Parse(list.StandardOutput);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (string.Equals(
                    item.GetProperty("headRefName").GetString(),
                    branchName,
                    StringComparison.Ordinal) &&
                string.Equals(
                    item.GetProperty("headRefOid").GetString(),
                    expectedHead,
                    StringComparison.OrdinalIgnoreCase) &&
                PublishedOutcomeVerifier.IsExpectedHeadRepository(
                    item,
                    repositoryName))
            {
                return item.GetProperty("url").GetString();
            }
        }
        return null;
    }

    internal static async Task<VerifiedRemoteGitState> PublishVerifiedGitBranchAsync(
        ProcessRunner processRunner,
        string sourceRepository,
        CandidateRepositoryManifest repository,
        string remoteUrl,
        string branchName,
        string gitHubToken,
        CancellationToken cancellationToken = default,
        TimeSpan? remoteTimeout = null,
        IReadOnlyDictionary<string, string?>? inheritedEnvironmentVariables = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        if (string.IsNullOrWhiteSpace(sourceRepository))
        {
            throw new InvalidOperationException(
                "Trusted Git publication requires a source repository.");
        }
        if (string.IsNullOrWhiteSpace(branchName))
        {
            throw new InvalidOperationException(
                "Trusted Git publication requires a branch name.");
        }
        if (string.IsNullOrWhiteSpace(repository.Head) ||
            string.IsNullOrWhiteSpace(repository.Tree))
        {
            throw new InvalidOperationException(
                "Trusted Git publication requires verified commit and tree identities.");
        }

        using var sandbox = TrustedGitSandbox.Create(
            remoteUrl,
            gitHubToken,
            inheritedEnvironmentVariables);
        await RunGitRequiredAsync(
            processRunner,
            sandbox,
            sourceRepository,
            [
                "init", "--bare", sandbox.BareRepositoryPath
            ],
            TimeSpan.FromSeconds(20),
            "initialize the isolated publication repository",
            cancellationToken);

        var verifiedRef = $"refs/ai-harness/verified/{repository.Head}";
        await RunGitRequiredAsync(
            processRunner,
            sandbox,
            sourceRepository,
            [
                "--git-dir", sandbox.BareRepositoryPath,
                "fetch", "--no-tags", "--force",
                sourceRepository,
                $"{repository.Head}:{verifiedRef}"
            ],
            TimeSpan.FromSeconds(30),
            $"materialize verified commit {repository.Head}",
            cancellationToken);

        var materializedHead = await ReadGitRevisionAsync(
            processRunner,
            sandbox,
            sourceRepository,
            repository.Head,
            cancellationToken);
        var materializedTree = await ReadGitRevisionAsync(
            processRunner,
            sandbox,
            sourceRepository,
            $"{repository.Head}^{{tree}}",
            cancellationToken);
        if (!string.Equals(
                materializedHead,
                repository.Head,
                StringComparison.Ordinal) ||
            !string.Equals(
                materializedTree,
                repository.Tree,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The isolated publication repository did not reproduce the verified objects for '{repository.RelativePath}'.");
        }

        await RunGitRequiredAsync(
            processRunner,
            sandbox,
            sourceRepository,
            [
                "--git-dir", sandbox.BareRepositoryPath,
                "push", remoteUrl,
                $"{repository.Head}:refs/heads/{branchName}"
            ],
            remoteTimeout ?? TimeSpan.FromMinutes(2),
            $"push verified commit {repository.Head} to '{remoteUrl}'",
            cancellationToken);

        var verificationRef = $"refs/remotes/ai-harness/verified/{SanitizeRefComponent(branchName)}";
        await RunGitRequiredAsync(
            processRunner,
            sandbox,
            sourceRepository,
            [
                "--git-dir", sandbox.BareRepositoryPath,
                "fetch", "--no-tags", "--force",
                remoteUrl,
                $"refs/heads/{branchName}:{verificationRef}"
            ],
            remoteTimeout ?? TimeSpan.FromMinutes(2),
            $"verify published branch {branchName}",
            cancellationToken);

        var publishedHead = await ReadGitRevisionAsync(
            processRunner,
            sandbox,
            sourceRepository,
            verificationRef,
            cancellationToken);
        var publishedTree = await ReadGitRevisionAsync(
            processRunner,
            sandbox,
            sourceRepository,
            $"{verificationRef}^{{tree}}",
            cancellationToken);
        if (!string.Equals(
                publishedHead,
                repository.Head,
                StringComparison.Ordinal) ||
            !string.Equals(
                publishedTree,
                repository.Tree,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Published branch '{branchName}' no longer matches the verified commit/tree for '{repository.RelativePath}'.");
        }

        return new VerifiedRemoteGitState(publishedHead, publishedTree);
    }

    private async Task<string> ResolveGitHubTokenAsync(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        var token = Environment.GetEnvironmentVariable("GH_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        }
        if (!string.IsNullOrWhiteSpace(token))
        {
            return token.Trim();
        }

        var result = await RunRequiredAsync(
            "gh",
            ["auth", "token"],
            workspacePath,
            TimeSpan.FromSeconds(30),
            "acquire a GitHub token for trusted Git publication",
            cancellationToken);
        var value = result.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                "GitHub did not return an authentication token for trusted Git publication.");
        }
        return value;
    }

    private static IReadOnlyDictionary<string, string?> BuildGitHubCliEnvironment(
        string gitHubToken) =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GH_TOKEN"] = gitHubToken,
            ["GITHUB_TOKEN"] = gitHubToken
        };

    internal static string GitHubGitAuthorizationHeader(string gitHubToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gitHubToken);
        var token = gitHubToken.Trim();
        if (token.Any(character => character > 127 || char.IsControl(character)))
        {
            throw new ArgumentException(
                "A GitHub Git token must contain only printable ASCII characters.",
                nameof(gitHubToken));
        }
        return "AUTHORIZATION: Basic " +
            Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"x-access-token:{token}"));
    }

    private static async Task<ProcessResult> RunGitRequiredAsync(
        ProcessRunner processRunner,
        TrustedGitSandbox sandbox,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string action,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            "git",
            arguments,
            workingDirectory,
            timeout,
            cancellationToken,
            environmentVariables: sandbox.EnvironmentVariables);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to {action}: {result.CombinedOutput}");
        }
        return result;
    }

    private static async Task<string> ReadGitRevisionAsync(
        ProcessRunner processRunner,
        TrustedGitSandbox sandbox,
        string workingDirectory,
        string revision,
        CancellationToken cancellationToken)
    {
        var result = await RunGitRequiredAsync(
            processRunner,
            sandbox,
            workingDirectory,
            [
                "--git-dir", sandbox.BareRepositoryPath,
                "rev-parse", "--verify", revision
            ],
            TimeSpan.FromSeconds(20),
            $"read Git revision {revision}",
            cancellationToken);
        var value = result.StandardOutput.Trim().ToLowerInvariant();
        if (value.Length is not (40 or 64) ||
            value.Any(character =>
                !char.IsAsciiHexDigit(character) ||
                char.IsAsciiLetterUpper(character)))
        {
            throw new InvalidOperationException(
                $"Unable to verify Git revision '{revision}' inside the isolated publication repository.");
        }
        return value;
    }

    private static string SanitizeRefComponent(string value)
    {
        var sanitized = new string(value.Select(character =>
                character is '/' or '\\' or ':' or '^' or '~' or '?' or '*' or '[' or ' '
                    ? '-'
                    : character)
            .ToArray())
            .Trim('-');
        return string.IsNullOrWhiteSpace(sanitized)
            ? "branch"
            : sanitized;
    }

    private async Task<PublicationState> LoadPublicationStateAsync(
        Guid flowId,
        Guid publicationStepId,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var flow = await database.Flows
            .Include(item => item.Events)
            .AsNoTracking()
            .SingleAsync(item => item.Id == flowId, cancellationToken);
        // Steps and gate records are loaded separately (rather than as extra collection includes)
        // so the authoritative authorization inputs cannot be distorted by a cartesian join.
        flow.Steps = await database.FlowSteps
            .AsNoTracking()
            .Where(item => item.FlowRunId == flowId)
            .ToListAsync(cancellationToken);
        flow.GateRecords = await database.GateRecords
            .AsNoTracking()
            .Where(item => item.FlowRunId == flowId)
            .ToListAsync(cancellationToken);
        var step = flow.Steps.SingleOrDefault(item => item.Id == publicationStepId)
                   ?? await database.FlowSteps
                       .AsNoTracking()
                       .SingleAsync(
                           item => item.Id == publicationStepId,
                           cancellationToken);
        return new PublicationState(flow, step);
    }

    private sealed record PublicationState(
        FlowRun Flow,
        FlowStep Step);

    /// <summary>
    /// The authoritative, database-backed proof that this exact publication attempt may run.
    /// </summary>
    private sealed record ReviewedPublicationAuthorization(
        Guid OutcomeOwnerStepId,
        ReviewedCandidateIdentity Identity);

    private sealed record PublishedPullRequestIdentity(
        string Url,
        string State);

    private sealed record ReviewedPublicationJournalState(
        IReadOnlyList<FlowEvent> Events,
        IReadOnlyList<ReviewedPublicationRecord> Records);

    private sealed record ReviewedPublicationEventData(
        string CandidateFingerprint,
        string RelativePath,
        string RemoteRepository,
        string PullRequestUrl,
        string Head,
        string Tree);

    internal static string BuildTrustedGitHubRemoteUrl(string repositoryName)
    {
        var parts = (repositoryName ?? string.Empty).Split('/');
        if (string.IsNullOrWhiteSpace(repositoryName) ||
            parts.Length != 2 ||
            parts.Any(string.IsNullOrWhiteSpace) ||
            repositoryName.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '.' or '_' or '-' or '/')))
        {
            throw new InvalidOperationException(
                $"The trusted GitHub repository identity is invalid: '{repositoryName}'.");
        }
        return $"https://github.com/{repositoryName}.git";
    }

    private async Task<ProcessResult> RunRequiredAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        string action,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        var result = await processRunner.RunAsync(
            executable,
            arguments,
            workingDirectory,
            timeout,
            cancellationToken,
            environmentVariables: environmentVariables);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to {action}: {result.CombinedOutput}");
        }
        return result;
    }

    internal static string ResolveWorkspaceRepository(
        string workspacePath,
        string relativePath)
    {
        var root = Path.GetFullPath(workspacePath);
        var combined = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, combined);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Candidate repository path escaped the flow workspace: {relativePath}");
        }
        return WorkspacePathGuard.ValidateExistingContainedDirectory(
            root,
            combined,
            "Candidate publication");
    }

    internal static string BuildPullRequestBody(
        FlowRun flow,
        ReviewedCandidateIdentity candidate)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(candidate);

        string goal;
        IReadOnlyList<string> details;
        IReadOnlyList<string> criteria;
        IReadOnlyList<string> constraints;
        var confirmedRequest = flow.ConsolidatedRequest.TrimStart();
        if (confirmedRequest.StartsWith('{') ||
            confirmedRequest.StartsWith('['))
        {
            var brief = IntakeParser.ParseBriefJson(flow.ConsolidatedRequest);
            goal = brief.Goal;
            details = brief.Details!;
            criteria = brief.SuccessCriteria!;
            constraints = brief.Constraints!;
        }
        else
        {
            // Legacy flows predate structured briefs; use their validated customer-facing outcome.
            var outcome = FlowOutcomeParser.ParseJson(
                flow.OutcomeContractJson).Document;
            goal = outcome.Goal;
            details = outcome.ImplementationDetails!;
            criteria = [];
            constraints = [];
        }

        static string ListItems(IReadOnlyList<string> items)
        {
            var lines = items.Take(8)
                .Select(item =>
                    "- " + Clip(item.ReplaceLineEndings(" ").Trim(), 600))
                .ToList();
            if (items.Count > lines.Count)
            {
                lines.Add(
                    $"- {items.Count - lines.Count} additional confirmed items omitted from this summary.");
            }
            return string.Join(Environment.NewLine, lines);
        }

        var sections = new List<string>
        {
            "## Summary",
            Clip(goal.ReplaceLineEndings(" ").Trim(), 700)
        };
        if (details.Count > 0)
        {
            sections.Add("## Scope");
            sections.Add(ListItems(details));
        }
        if (criteria.Count > 0)
        {
            sections.Add("## Acceptance checks");
            sections.Add(ListItems(criteria));
        }
        if (constraints.Count > 0)
        {
            sections.Add("## Boundaries");
            sections.Add(ListItems(constraints));
        }
        sections.Add("## Review");
        sections.Add(
            "Studio sealed and independently verified the candidate before customer approval. " +
            "This pull request proposes the reviewed commit for this repository.");
        sections.Add(
            "<details>\n<summary>Studio verification identifiers</summary>\n\n" +
            $"- Acceptance plan: `{candidate.AcceptancePlanHash}`\n" +
            $"- Reviewed candidate: `{candidate.Fingerprint}`\n\n" +
            "</details>");
        return string.Join(
            Environment.NewLine + Environment.NewLine,
            sections);
    }

    private static string Clip(string value, int maximum) =>
        value.Length <= maximum
            ? value
            : value[..maximum] + "...";

    internal sealed record VerifiedRemoteGitState(
        string Head,
        string Tree);

    private sealed class TrustedGitSandbox : IDisposable
    {
        private TrustedGitSandbox(
            string rootPath,
            string bareRepositoryPath,
            IReadOnlyDictionary<string, string?> environmentVariables)
        {
            RootPath = rootPath;
            BareRepositoryPath = bareRepositoryPath;
            EnvironmentVariables = environmentVariables;
        }

        public string RootPath { get; }

        public string BareRepositoryPath { get; }

        public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; }

        public static TrustedGitSandbox Create(
            string remoteUrl,
            string gitHubToken,
            IReadOnlyDictionary<string, string?>?
                inheritedEnvironmentVariables = null)
        {
            var rootPath = Path.Combine(
                Path.GetTempPath(),
                $"ai-harness-publication-{Guid.NewGuid():N}");
            Directory.CreateDirectory(rootPath);

            var bareRepositoryPath = Path.Combine(rootPath, "publication.git");
            var homePath = Path.Combine(rootPath, "home");
            var xdgPath = Path.Combine(rootPath, "xdg");
            var hooksPath = Path.Combine(rootPath, "hooks");
            Directory.CreateDirectory(homePath);
            Directory.CreateDirectory(xdgPath);
            Directory.CreateDirectory(hooksPath);

            var globalConfigPath = Path.Combine(rootPath, "global.gitconfig");
            File.WriteAllText(globalConfigPath, string.Empty);
            var askPassPath = CreateDisabledCommand(
                rootPath,
                "askpass");
            var sshPath = CreateDisabledCommand(
                rootPath,
                "forbid-ssh");

            var environmentVariables =
                new Dictionary<string, string?>(
                    inheritedEnvironmentVariables ??
                    new Dictionary<string, string?>(),
                    StringComparer.Ordinal);
            foreach (var (key, value) in
                     new Dictionary<string, string?>(StringComparer.Ordinal)
                     {
                         ["HOME"] = homePath,
                         ["USERPROFILE"] = homePath,
                         ["XDG_CONFIG_HOME"] = xdgPath,
                         ["GIT_CONFIG_NOSYSTEM"] = "1",
                         ["GIT_CONFIG_SYSTEM"] = null,
                         ["GIT_CONFIG_GLOBAL"] = globalConfigPath,
                         ["GIT_DIR"] = null,
                         ["GIT_WORK_TREE"] = null,
                         ["GIT_COMMON_DIR"] = null,
                         ["GIT_INDEX_FILE"] = null,
                         ["GIT_INDEX_VERSION"] = null,
                         ["GIT_OBJECT_DIRECTORY"] = null,
                         ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = null,
                         ["GIT_TERMINAL_PROMPT"] = "0",
                         ["GCM_INTERACTIVE"] = "Never",
                         ["GIT_ASKPASS"] = askPassPath,
                         ["SSH_ASKPASS"] = askPassPath,
                         ["GIT_SSH"] = sshPath,
                         ["GIT_SSH_COMMAND"] = QuoteCommand(sshPath)
                     })
            {
                environmentVariables[key] = value;
            }

            var config = new List<KeyValuePair<string, string>>
            {
                new("core.hooksPath", hooksPath),
                new("credential.helper", string.Empty),
                new("credential.interactive", "never")
            };
            if (!string.IsNullOrWhiteSpace(gitHubToken) &&
                remoteUrl.StartsWith(
                    "https://github.com/",
                    StringComparison.OrdinalIgnoreCase))
            {
                config.Add(new(
                    "http.https://github.com/.extraheader",
                    GitHubGitAuthorizationHeader(gitHubToken)));
            }

            environmentVariables["GIT_CONFIG_COUNT"] = config.Count.ToString();
            for (var index = 0; index < config.Count; index++)
            {
                environmentVariables[$"GIT_CONFIG_KEY_{index}"] = config[index].Key;
                environmentVariables[$"GIT_CONFIG_VALUE_{index}"] = config[index].Value;
            }

            return new TrustedGitSandbox(
                rootPath,
                bareRepositoryPath,
                environmentVariables);
        }

        public void Dispose() => DeleteDirectoryBestEffort(RootPath);

        internal static void DeleteDirectoryBestEffort(string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !Directory.Exists(path))
            {
                return;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(
                             path,
                             "*",
                             SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                foreach (var directory in Directory.EnumerateDirectories(
                             path,
                             "*",
                             SearchOption.AllDirectories))
                {
                    File.SetAttributes(directory, FileAttributes.Directory);
                }
                Directory.Delete(path, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Temporary publication cleanup is best-effort only.
            }
        }

        private static string CreateDisabledCommand(
            string rootPath,
            string name)
        {
            if (OperatingSystem.IsWindows())
            {
                var path = Path.Combine(rootPath, $"{name}.cmd");
                File.WriteAllText(
                    path,
                    "@echo off\r\nexit /b 1\r\n");
                return path;
            }

            var shellPath = Path.Combine(rootPath, name);
            File.WriteAllText(
                shellPath,
                "#!/bin/sh\nexit 1\n");
            try
            {
                File.SetUnixFileMode(
                    shellPath,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
            }
            catch (PlatformNotSupportedException)
            {
                // Best effort for non-Windows tests only.
            }
            return shellPath;
        }

        private static string QuoteCommand(string path) =>
            path.IndexOf(' ') >= 0
                ? $"\"{path}\""
                : path;
    }
}

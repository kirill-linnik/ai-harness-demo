using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Data;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

public sealed record PublishedOutcome(string Url, string Label);

public interface IPublishedOutcomeVerifier
{
    Task<PublishedOutcome> VerifyAsync(
        FlowRun flow,
        string releaseOutput,
        CancellationToken cancellationToken = default);
}

public sealed partial class PublishedOutcomeVerifier(
    ProcessRunner processRunner,
    IDbContextFactory<HarnessDbContext>? databaseFactory = null,
    IReviewedCandidateService? reviewedCandidateService = null) : IPublishedOutcomeVerifier
{
    [GeneratedRegex(
        @"https://github\.com/(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)/pull/(?<number>\d+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PullRequestPattern();

    [GeneratedRegex(
        @"(?<![0-9a-f])(?<sha>[0-9a-f]{7,40})(?![0-9a-f])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CommitPattern();

    public Task<PublishedOutcome> VerifyAsync(
        FlowRun flow,
        string releaseOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        _ = OutcomeTypeRules.RequireDelivery(flow.Outcome, nameof(flow.Outcome));
        if (flow.Kind != FlowKind.Delivery)
        {
            throw new InvalidOperationException(
                "Publication verification is available only for Delivery flows.");
        }
        return VerifyReviewedCandidatePublicationAsync(
            flow.Id,
            releaseOutput,
            cancellationToken);
    }
    private async Task<PublishedOutcome> VerifyReviewedCandidatePublicationAsync(
        Guid flowId,
        string publicationReport,
        CancellationToken cancellationToken)
    {
        var factory = databaseFactory
            ?? throw new InvalidOperationException(
                "Reviewed publication verification has no durable database.");
        var service = reviewedCandidateService
            ?? throw new InvalidOperationException(
                "Reviewed publication verification has no candidate identity service.");
        await using var database =
            await factory.CreateDbContextAsync(cancellationToken);
        var flow = await database.Flows
            .AsNoTracking()
            .AsSplitQuery()
            .Include(item => item.Events)
            .Include(item => item.Steps)
            .Include(item => item.GateRecords)
            .SingleAsync(item => item.Id == flowId, cancellationToken);
        if (flow.Kind != FlowKind.Delivery)
        {
            throw new InvalidOperationException(
                "Reviewed publication verification requires the authoritative Studio Delivery flow.");
        }
        var outcome = OutcomeTypeRules.RequireDelivery(
            flow.Outcome,
            nameof(flow.Outcome));
        var currentSteps = flow.Steps
            .Where(step => step.Iteration == flow.Iteration)
            .ToDictionary(step => step.Id);
        var acceptedOwnerIds = flow.GateRecords
            .Where(gate =>
                gate.ActionType == HandoffActionType.CustomerReview &&
                gate.Resolved &&
                gate.Approved == true &&
                gate.ReviewDecision == ReviewDecision.Accepted &&
                currentSteps.ContainsKey(gate.FlowStepId))
            .Select(gate => gate.FlowStepId)
            .Distinct()
            .ToArray();
        if (acceptedOwnerIds.Length != 1)
        {
            throw new InvalidOperationException(
                "Reviewed publication verification requires one current accepted CustomerReview.");
        }
        var owner = currentSteps[acceptedOwnerIds[0]];
        var acceptedGateId = flow.GateRecords
            .Where(gate =>
                gate.ActionType == HandoffActionType.CustomerReview &&
                gate.Resolved &&
                gate.Approved == true &&
                gate.ReviewDecision == ReviewDecision.Accepted &&
                gate.FlowStepId == owner.Id)
            .OrderByDescending(gate => gate.ResolvedAt)
            .Select(gate => gate.Id)
            .First();
        // Remote verification has already happened; the same durable binding is rechecked here so a
        // post-review mutation or a superseded assessment cannot be recorded as a verified outcome.
        var readinessBinding = await new DeliveryReadinessService().LoadCurrentAsync(
            database,
            flow.Id,
            cancellationToken);
        if (readinessBinding is null ||
            readinessBinding.State != DeliveryReadinessState.ReadyToApprove)
        {
            throw new DeliveryReadinessConflictException(
                DeliveryReadinessConflicts.PublicationNotAuthorized,
                "Reviewed publication verification requires a current ReadyToApprove readiness assessment.",
                readinessBinding?.State,
                readinessBinding?.Revision,
                readinessBinding?.ContractHash);
        }
        var authorizedJournal = await database.ReviewedPublicationRecords
            .AsNoTracking()
            .Where(item => item.FlowRunId == flow.Id)
            .ToListAsync(cancellationToken);
        if (authorizedJournal.Count > 0 &&
            authorizedJournal.Any(item =>
                item.ReviewedCandidateId != readinessBinding.Candidate.Id ||
                item.ReadinessSnapshotId != readinessBinding.Record.Id ||
                item.CustomerReviewGateId != acceptedGateId ||
                !string.Equals(
                    item.ReadinessContractHash,
                    readinessBinding.ContractHash,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    item.WaiverSetHash,
                    readinessBinding.WaiverSetHash,
                    StringComparison.Ordinal)))
        {
            throw new DeliveryReadinessConflictException(
                DeliveryReadinessConflicts.PublicationNotAuthorized,
                "The reviewed publication journal is not bound to the current readiness, review, and waiver identity.",
                readinessBinding.State,
                readinessBinding.Revision,
                readinessBinding.ContractHash);
        }
        if (!owner.IsOutcomeOwner ||
            owner.Status != StepStatus.Completed ||
            string.IsNullOrWhiteSpace(flow.OutcomeOwnerPlanStepKey) ||
            !string.Equals(
                owner.PlanStepKey,
                flow.OutcomeOwnerPlanStepKey,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Reviewed publication verification is not bound to the current completed outcome owner.");
        }
        var publicationStep = flow.Steps
            .Where(step =>
                step.Iteration == flow.Iteration &&
                ReviewCoordinator.IsPublicationStep(flow, step))
            .OrderBy(step => step.Sequence)
            .ThenBy(step => step.Attempt)
            .ThenBy(step => step.StartedAt)
            .LastOrDefault()
            ?? throw new InvalidOperationException(
                "Reviewed publication verification has no current publication step.");
        if (!publicationStep.RemotePublicationAllowed ||
            publicationStep.Status is not (
                StepStatus.Running or StepStatus.Completed))
        {
            throw new InvalidOperationException(
                "Reviewed publication verification requires the current authorized publication step.");
        }
        var publicationRootId =
            publicationStep.StableSemanticRootId ?? publicationStep.Id;
        var identity = ReviewedCandidateLedger.Read(flow, owner.Id);
        var completionEvents = flow.Events
            .Where(item =>
                item.FlowStepId == publicationRootId &&
                item.Type ==
                    VerifiedCandidatePublisher
                        .ReviewedPublicationCompletedEventType)
            .ToArray();
        if (completionEvents.Length != 1 ||
            !string.Equals(
                ReadReviewedPublicationFingerprint(
                    completionEvents.Single().DataJson),
                identity.Fingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Reviewed publication verification requires one matching durable completion event.");
        }
        var publicationRecords = await database.ReviewedPublicationRecords
            .AsNoTracking()
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.PublicationRootId == publicationRootId)
            .ToListAsync(cancellationToken);
        if (publicationRecords.Count != identity.Repositories.Count)
        {
            throw new InvalidOperationException(
                "Reviewed publication verification requires one completed journal row per sealed repository.");
        }
        foreach (var repository in identity.Repositories)
        {
            var record = publicationRecords.SingleOrDefault(item =>
                             string.Equals(
                                 item.RelativePath,
                                 repository.RelativePath,
                                 StringComparison.Ordinal))
                         ?? throw new InvalidOperationException(
                             $"Reviewed publication verification has no journal row for '{repository.RelativePath}'.");
            if (record.Iteration != identity.Iteration ||
                record.Stage != ReviewedPublicationStage.Completed ||
                !string.Equals(
                    record.CandidateFingerprint,
                    identity.Fingerprint,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    record.RemoteRepository,
                    repository.RemoteRepository,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    record.BranchName,
                    flow.BranchName,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    record.Head,
                    repository.Head,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    record.Tree,
                    repository.Tree,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Reviewed publication journal row '{repository.RelativePath}' conflicts with the current seal.");
            }
            if (outcome == OutcomeType.PullRequest)
            {
                var durableReference = ParsePullRequest(record.PullRequestUrl);
                if (durableReference is null ||
                    !string.Equals(
                        durableReference.Repository,
                        repository.RemoteRepository,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Reviewed publication journal row '{repository.RelativePath}' has no trusted pull request.");
                }
            }
            else if (!string.IsNullOrWhiteSpace(record.PullRequestUrl))
            {
                throw new InvalidOperationException(
                    $"Reviewed commit journal row '{repository.RelativePath}' unexpectedly contains a pull request.");
            }
        }
        var candidate = await service.VerifyAsync(
            flow,
            identity,
            cancellationToken);

        if (outcome == OutcomeType.Commit)
        {
            foreach (var repository in candidate.Manifest.Repositories)
            {
                if (!publicationReport.Contains(
                        repository.Head,
                        StringComparison.OrdinalIgnoreCase) ||
                    !publicationReport.Contains(
                        repository.Tree,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Publication report omitted the reviewed commit/tree for '{repository.RelativePath}'.");
                }
            }
            _ = await service.VerifyAsync(flow, identity, cancellationToken);
            var firstCommit = candidate.Manifest.Repositories[0];
            return new PublishedOutcome(
                $"#/preview/{flow.Id}",
                candidate.Manifest.Repositories.Count == 1
                    ? $"Approved commit · {firstCommit.Head[..12]}"
                    : $"Approved commit · {candidate.Manifest.Repositories.Count} repositories");
        }

        if (outcome != OutcomeType.PullRequest)
        {
            throw new UnreachableException();
        }
        var references = PullRequestPattern().Matches(publicationReport)
            .Select(match => new PullRequestReference(
                match.Value,
                $"{match.Groups["owner"].Value}/{match.Groups["repo"].Value}",
                int.Parse(match.Groups["number"].Value)))
            .GroupBy(item => item.Repository, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        var verified = new List<PullRequestReference>();
        foreach (var repository in candidate.Manifest.Repositories)
        {
            var publicationRecord = publicationRecords.Single(item =>
                string.Equals(
                    item.RelativePath,
                    repository.RelativePath,
                    StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(repository.RemoteRepository) ||
                !references.TryGetValue(
                    repository.RemoteRepository,
                    out var reference))
            {
                throw new InvalidOperationException(
                    $"Publication did not report a pull request for reviewed repository '{repository.RelativePath}'.");
            }
            if (!string.Equals(
                    publicationRecord.PullRequestUrl,
                    reference.Url,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Publication report does not match the durable pull request for '{repository.RelativePath}'.");
            }
            var result = await processRunner.RunAsync(
                "gh",
                [
                    "pr", "view", reference.Number.ToString(),
                    "--repo", reference.Repository,
                    "--json", "state,url,headRefName,headRefOid,headRepository,headRepositoryOwner"
                ],
                AppContext.BaseDirectory,
                TimeSpan.FromSeconds(30),
                cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"The reviewed pull request could not be verified: {result.CombinedOutput}");
            }
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            if (!root.TryGetProperty("url", out var url) ||
                !root.TryGetProperty("headRefName", out var headRefName) ||
                !root.TryGetProperty("headRefOid", out var headRefOid) ||
                !string.Equals(
                    url.GetString(),
                    reference.Url,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    headRefName.GetString(),
                    flow.BranchName,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    headRefOid.GetString(),
                    repository.Head,
                    StringComparison.OrdinalIgnoreCase) ||
                !IsExpectedHeadRepository(
                    root,
                    repository.RemoteRepository))
            {
                throw new InvalidOperationException(
                    $"Pull request for '{repository.RemoteRepository}' does not publish the candidate sealed before review.");
            }
            RequirePublishableReviewedPullRequestState(
                root,
                reference.Url,
                repository.RemoteRepository);
            verified.Add(reference);
        }
        _ = await service.VerifyAsync(flow, identity, cancellationToken);
        var first = verified.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No reviewed pull request was published.");
        return new PublishedOutcome(
            first.Url,
            verified.Count == 1
                ? $"Published pull request #{first.Number}"
                : $"Published pull requests · {verified.Count} repositories");
    }

    private static string ReadReviewedPublicationFingerprint(string? json)
    {
        try
        {
            using var document = JsonDocument.Parse(
                json
                ?? throw new InvalidOperationException(
                    "The reviewed publication completion has no structured data."));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.TryGetProperty("Version", out _) ||
                !root.TryGetProperty(
                    "CandidateFingerprint",
                    out var fingerprint) ||
                string.IsNullOrWhiteSpace(fingerprint.GetString()))
            {
                throw new InvalidOperationException(
                    "The reviewed publication completion data is invalid.");
            }
            return fingerprint.GetString()!;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The reviewed publication completion data is invalid.",
                exception);
        }
    }

    private static void RequirePublishableReviewedPullRequestState(
        JsonElement pullRequest,
        string pullRequestUrl,
        string repositoryName)
    {
        var state = pullRequest.TryGetProperty("state", out var stateElement)
            ? stateElement.GetString()
            : null;
        if (string.Equals(state, "OPEN", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, "MERGED", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (string.Equals(state, "CLOSED", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Reviewed pull request '{pullRequestUrl}' in '{repositoryName}' is closed without merge.");
        }
        throw new InvalidOperationException(
            $"Reviewed pull request '{pullRequestUrl}' in '{repositoryName}' has unsupported state '{state ?? "<missing>"}'.");
    }

    internal static PullRequestReference? ParsePullRequest(string output)
    {
        var match = PullRequestPattern().Match(output);
        return match.Success
            ? new PullRequestReference(
                match.Value,
                $"{match.Groups["owner"].Value}/{match.Groups["repo"].Value}",
                int.Parse(match.Groups["number"].Value))
            : null;
    }

    internal static string? NormalizeGitHubRepository(string remote)
    {
        var value = remote.Trim();
        var match = Regex.Match(
            value,
            @"(?:https://github\.com/|git@github\.com:|ssh://git@github\.com/)(?<repo>[^/]+/[^/]+?)(?:\.git)?$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        return match.Success
            ? match.Groups["repo"].Value.TrimEnd('/')
            : null;
    }

    internal static string? NormalizeSingleGitHubRemote(string output)
    {
        var remotes = output.ReplaceLineEndings("\n")
            .Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
        var repositories = remotes
            .Select(NormalizeGitHubRepository)
            .ToArray();
        if (repositories.Length == 0 ||
            repositories.Any(item => item is null))
        {
            return null;
        }
        var distinct = repositories
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return distinct.Length == 1
            ? distinct[0]
            : null;
    }

    internal static bool IsExpectedHeadRepository(
        JsonElement pullRequest,
        string expectedRepository)
    {
        if (!pullRequest.TryGetProperty("headRepository", out var repository))
        {
            return false;
        }
        if (repository.ValueKind == JsonValueKind.String)
        {
            return string.Equals(
                repository.GetString(),
                expectedRepository,
                StringComparison.OrdinalIgnoreCase);
        }
        if (repository.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        if (repository.TryGetProperty("nameWithOwner", out var nameWithOwner))
        {
            return string.Equals(
                nameWithOwner.GetString(),
                expectedRepository,
                StringComparison.OrdinalIgnoreCase);
        }
        if (!repository.TryGetProperty("name", out var name) ||
            !pullRequest.TryGetProperty(
                "headRepositoryOwner",
                out var owner) ||
            owner.ValueKind != JsonValueKind.Object ||
            !owner.TryGetProperty("login", out var login))
        {
            return false;
        }
        return string.Equals(
            $"{login.GetString()}/{name.GetString()}",
            expectedRepository,
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> FindRepositoryAsync(
        IReadOnlyList<string> repositories,
        string expectedRepository,
        CancellationToken cancellationToken)
    {
        foreach (var repository in repositories)
        {
            var remote = await processRunner.RunAsync(
                "git",
                [
                    "-C", repository,
                    "remote", "get-url", "--push", "--all", "origin"
                ],
                repository,
                TimeSpan.FromSeconds(20),
                cancellationToken);
            if (remote.ExitCode == 0 &&
                string.Equals(
                    NormalizeSingleGitHubRemote(remote.StandardOutput),
                    expectedRepository,
                    StringComparison.OrdinalIgnoreCase))
            {
                return repository;
            }
        }
        return null;
    }

    private async Task<string> RunGitAsync(
        string workspaceRepository,
        IReadOnlyList<string> arguments,
        string action,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            "git",
            ["-C", workspaceRepository, .. arguments],
            workspaceRepository,
            TimeSpan.FromSeconds(20),
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to {action}: {result.CombinedOutput}");
        }
        return result.StandardOutput.Trim();
    }

    private static string ResolveManifestRepository(
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
                $"Published repository path escaped the flow workspace: {relativePath}");
        }
        return WorkspacePathGuard.ValidateExistingContainedDirectory(
            root,
            combined,
            "Published candidate verification");
    }

    private async Task<string> ReadTreeAsync(
        string workspaceRepository,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            "git",
            ["-C", workspaceRepository, "rev-parse", "HEAD^{tree}"],
            workspaceRepository,
            TimeSpan.FromSeconds(20),
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to verify the published workspace tree: {result.CombinedOutput}");
        }
        return result.StandardOutput.Trim();
    }

    internal sealed record PullRequestReference(
        string Url,
        string Repository,
        int Number);
}

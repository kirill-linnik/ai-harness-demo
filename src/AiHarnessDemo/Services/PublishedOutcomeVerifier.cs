using System.Text.Json;
using System.Text.RegularExpressions;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Verification;

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
    CandidateFingerprintService? candidateFingerprintService = null) : IPublishedOutcomeVerifier
{
    [GeneratedRegex(
        @"https://github\.com/(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)/pull/(?<number>\d+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PullRequestPattern();

    [GeneratedRegex(
        @"(?<![0-9a-f])(?<sha>[0-9a-f]{7,40})(?![0-9a-f])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CommitPattern();

    public async Task<PublishedOutcome> VerifyAsync(
        FlowRun flow,
        string releaseOutput,
        CancellationToken cancellationToken = default)
    {
        var verifiedCandidate = ReadVerifiedCandidate(flow);
        if (!string.IsNullOrWhiteSpace(flow.WorkspacePath))
        {
            if (candidateFingerprintService is not null)
            {
                candidateFingerprintService.ValidateWorkspaceRoot(flow);
            }
            else
            {
                WorkspacePathGuard.ValidateExistingRoot(
                    flow.WorkspacePath,
                    authorizedWorkspaceRoot: null,
                    "Publication verification");
            }
        }
        var repositories = RepositoryAnalyzer.FindGitRepositories(flow.RepositoryPath);
        if (repositories.Count == 0)
        {
            throw new InvalidOperationException(
                "The approved publication cannot be verified because the source project has no Git repositories.");
        }
        if (verifiedCandidate is not null)
        {
            return flow.Outcome == OutcomeType.PullRequest
                ? await VerifyGovernedPullRequestsAsync(
                    flow,
                    verifiedCandidate,
                    releaseOutput,
                    cancellationToken)
                : await VerifyGovernedCommitsAsync(
                    flow,
                    verifiedCandidate,
                    releaseOutput,
                    cancellationToken);
        }

        if (flow.Outcome == OutcomeType.PullRequest)
        {
            var references = PullRequestPattern().Matches(releaseOutput)
                .Select(match => new PullRequestReference(
                    match.Value,
                    $"{match.Groups["owner"].Value}/{match.Groups["repo"].Value}",
                    int.Parse(match.Groups["number"].Value)))
                .GroupBy(item => item.Repository, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    StringComparer.OrdinalIgnoreCase);
            if (references.Count == 0)
            {
                throw new InvalidOperationException(
                    "The approved Release handoff did not report a GitHub pull request URL.");
            }

            var verified = new List<PullRequestReference>();
            foreach (var sourceRepository in repositories)
            {
                var remote = await processRunner.RunAsync(
                    "git",
                    [
                        "-C", sourceRepository,
                        "remote", "get-url", "--push", "--all", "origin"
                    ],
                    sourceRepository,
                    TimeSpan.FromSeconds(20),
                    cancellationToken);
                var repositoryName = remote.ExitCode == 0
                    ? NormalizeSingleGitHubRemote(remote.StandardOutput)
                    : null;
                if (repositoryName is null ||
                    !references.TryGetValue(repositoryName, out var reference))
                {
                    throw new InvalidOperationException(
                        $"The Release handoff omitted a pull request for project repository '{sourceRepository}'.");
                }
                var result = await processRunner.RunAsync(
                    "gh",
                    [
                        "pr", "view", reference.Number.ToString(),
                        "--repo", reference.Repository,
                        "--json", "state,url,headRefName,headRefOid,headRepository,headRepositoryOwner"
                    ],
                    sourceRepository,
                    TimeSpan.FromSeconds(30),
                    cancellationToken);
                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"The reported pull request could not be verified: {result.CombinedOutput}");
                }
                using var document = JsonDocument.Parse(result.StandardOutput);
                var root = document.RootElement;
                var headRefOid = root.GetProperty("headRefOid").GetString();
                if (!string.Equals(
                        root.GetProperty("state").GetString(),
                        "OPEN",
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        root.GetProperty("url").GetString(),
                        reference.Url,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        root.GetProperty("headRefName").GetString(),
                        flow.BranchName,
                        StringComparison.Ordinal) ||
                    !IsExpectedHeadRepository(root, reference.Repository))
                {
                    throw new InvalidOperationException(
                        "The reported pull request is not an open same-origin PR for this flow branch.");
                }
                var workspaceRepository = ResolveWorkspaceRepository(
                    flow,
                    sourceRepository,
                    repositories.Count);
                var localHead = await ReadHeadAsync(
                    workspaceRepository,
                    cancellationToken);
                if (!string.Equals(
                        localHead,
                        headRefOid,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The reported pull request head does not match the flow workspace commit.");
                }
                verified.Add(reference);
            }
            var first = verified[0];
            return new PublishedOutcome(
                first.Url,
                verified.Count == 1
                    ? $"Published pull request #{first.Number}"
                    : $"Published pull requests · {verified.Count} repositories");
        }

        var reportedShas = CommitPattern().Matches(releaseOutput)
            .Select(match => match.Groups["sha"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        string? firstHead = null;
        foreach (var repository in repositories)
        {
            var workspaceRepository = ResolveWorkspaceRepository(
                flow,
                repository,
                repositories.Count);
            var head = await ReadHeadAsync(workspaceRepository, cancellationToken);
            var reported = reportedShas.FirstOrDefault(sha =>
                head.StartsWith(sha, StringComparison.OrdinalIgnoreCase));
            if (reported is null)
            {
                throw new InvalidOperationException(
                    $"The approved Release handoff omitted the workspace HEAD for '{repository}'.");
            }
            firstHead ??= head;
        }
        return new PublishedOutcome(
            $"#/preview/{flow.Id}",
            repositories.Count == 1
                ? $"Approved commit · {firstHead![..12]}"
                : $"Approved commit · {repositories.Count} repositories");
    }

    private async Task<PublishedOutcome> VerifyGovernedPullRequestsAsync(
        FlowRun flow,
        OutcomeCandidateSnapshot candidate,
        string publicationReport,
        CancellationToken cancellationToken)
    {
        await RequireCurrentCandidateAsync(flow, candidate, cancellationToken);
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
        var publication = OutcomeVerificationRules
            .DeserializeAggregate(flow.OutcomeVerificationJson)
            .Publication
            ?? throw new InvalidOperationException(
                "Governed pull-request verification requires a durable host publication journal.");
        if (publication.Status is not (
                OutcomePublicationStatus.Published or
                OutcomePublicationStatus.Verified))
        {
            throw new InvalidOperationException(
                "Governed pull-request verification requires a completed host publication journal.");
        }
        var verified = new List<PullRequestReference>();
        foreach (var repository in candidate.Manifest.Repositories)
        {
            var repositoryName = repository.RemoteRepository;
            var journalRepository = publication.Repositories.SingleOrDefault(item =>
                string.Equals(
                    item.RelativePath,
                    repository.RelativePath,
                    StringComparison.Ordinal));
            if (journalRepository is null ||
                journalRepository.Status !=
                    OutcomeRepositoryPublicationStatus.Published ||
                string.IsNullOrWhiteSpace(repositoryName) ||
                !string.Equals(
                    journalRepository.RemoteRepository,
                    repositoryName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Verified repository '{repository.RelativePath}' has no matching trusted publication record.");
            }
            if (!references.TryGetValue(repositoryName, out var reference))
            {
                throw new InvalidOperationException(
                    $"Publication did not report a pull request for verified repository '{repositoryName}'.");
            }
            if (!string.Equals(
                    journalRepository.PullRequestUrl,
                    reference.Url,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Publication report does not match the durable pull-request record for '{repositoryName}'.");
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
                    $"The reported pull request could not be verified: {result.CombinedOutput}");
            }
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            if (!string.Equals(
                    root.GetProperty("state").GetString(),
                    "OPEN",
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    root.GetProperty("url").GetString(),
                    reference.Url,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    root.GetProperty("headRefName").GetString(),
                    flow.BranchName,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    root.GetProperty("headRefOid").GetString(),
                    repository.Head,
                    StringComparison.OrdinalIgnoreCase) ||
                !IsExpectedHeadRepository(root, repositoryName))
            {
                throw new InvalidOperationException(
                    $"Pull request for '{repositoryName}' does not publish the verified branch and commit.");
            }
            verified.Add(reference);
        }
        await RequireCurrentCandidateAsync(flow, candidate, cancellationToken);
        var first = verified.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No verified pull request was published.");
        return new PublishedOutcome(
            first.Url,
            verified.Count == 1
                ? $"Published pull request #{first.Number}"
                : $"Published pull requests · {verified.Count} repositories");
    }

    private async Task<PublishedOutcome> VerifyGovernedCommitsAsync(
        FlowRun flow,
        OutcomeCandidateSnapshot candidate,
        string publicationReport,
        CancellationToken cancellationToken)
    {
        await RequireCurrentCandidateAsync(flow, candidate, cancellationToken);
        foreach (var repository in candidate.Manifest.Repositories)
        {
            if (!publicationReport.Contains(
                    repository.Head,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Publication report omitted verified commit for '{repository.RelativePath}'.");
            }
        }
        var first = candidate.Manifest.Repositories[0];
        return new PublishedOutcome(
            $"#/preview/{flow.Id}",
            candidate.Manifest.Repositories.Count == 1
                ? $"Approved commit · {first.Head[..12]}"
                : $"Approved commit · {candidate.Manifest.Repositories.Count} repositories");
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

    private static string ResolveWorkspaceRepository(
        FlowRun flow,
        string sourceRepository,
        int repositoryCount)
    {
        var projectPath = Path.GetFullPath(flow.RepositoryPath);
        return repositoryCount == 1 &&
               string.Equals(
                   Path.TrimEndingDirectorySeparator(projectPath),
                   Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRepository)),
                   OperatingSystem.IsWindows()
                       ? StringComparison.OrdinalIgnoreCase
                       : StringComparison.Ordinal)
            ? WorkspacePathGuard.ValidateExistingRoot(
                flow.WorkspacePath,
                authorizedWorkspaceRoot: null,
                "Publication verification")
            : WorkspacePathGuard.ValidateExistingContainedDirectory(
                flow.WorkspacePath,
                Path.Combine(
                    flow.WorkspacePath,
                    Path.GetRelativePath(projectPath, sourceRepository)),
                "Publication verification");
    }

    private async Task<string> ReadHeadAsync(
        string workspaceRepository,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            "git",
            ["-C", workspaceRepository, "rev-parse", "HEAD"],
            workspaceRepository,
            TimeSpan.FromSeconds(20),
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to verify the published workspace commit: {result.CombinedOutput}");
        }
        return result.StandardOutput.Trim();
    }

    private static OutcomeCandidateSnapshot? ReadVerifiedCandidate(FlowRun flow)
    {
        if (string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson))
        {
            return null;
        }
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        if (state.Status != OutcomeVerificationStatus.Passed ||
            state.CurrentCandidate is null ||
            state.Stale ||
            !string.Equals(
                state.CurrentCandidate.Fingerprint,
                state.VerifiedCandidateFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Publication requires a current authoritative outcome-verification PASS.");
        }
        return state.CurrentCandidate;
    }

    private async Task RequireCurrentCandidateAsync(
        FlowRun flow,
        OutcomeCandidateSnapshot verifiedCandidate,
        CancellationToken cancellationToken)
    {
        var service = candidateFingerprintService
            ?? throw new InvalidOperationException(
                "No candidate fingerprint service is configured.");
        if (!await service.IsCurrentAsync(
                flow,
                verifiedCandidate,
                CandidateFingerprintService.RequiresPreview(
                    OutcomeVerificationRules
                        .DeserializeAggregate(flow.OutcomeVerificationJson)
                        .AcceptancePlan),
                cancellationToken))
        {
            throw new InvalidOperationException(
                "The published workspace no longer matches the verified candidate.");
        }
    }

    private async Task VerifyLocalIdentityAsync(
        string workspaceRepository,
        CandidateRepositoryManifest repository,
        CancellationToken cancellationToken)
    {
        var head = await ReadHeadAsync(workspaceRepository, cancellationToken);
        var tree = await ReadTreeAsync(workspaceRepository, cancellationToken);
        if (!string.Equals(repository.Head, head, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(repository.Tree, tree, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The published commit/tree does not match the verified candidate.");
        }
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

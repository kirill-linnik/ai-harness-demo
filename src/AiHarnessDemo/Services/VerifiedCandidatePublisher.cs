using System.Text.Json;
using System.Text.RegularExpressions;
using AiHarnessDemo.Core.Domain;
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
/// Performs remote side effects from the immutable candidate manifest. The Release Engineer still
/// supplies the publication turn, but its Copilot process remains push-guarded for governed flows.
/// </summary>
public sealed partial class VerifiedCandidatePublisher(
    ProcessRunner processRunner,
    CandidateFingerprintService candidateFingerprintService,
    IDbContextFactory<HarnessDbContext> databaseFactory)
    : IVerifiedCandidatePublisher
{
    [GeneratedRegex(
        @"https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/pull/\d+",
        RegexOptions.CultureInvariant)]
    private static partial Regex PullRequestUrlPattern();

    public async Task<string> PublishAsync(
        FlowRun flow,
        Guid publicationStepId,
        CancellationToken cancellationToken = default)
    {
        var publicationState = await LoadPublicationStateAsync(
            flow.Id,
            publicationStepId,
            cancellationToken);
        flow = publicationState.Flow;
        var publicationStep = publicationState.Step;
        var publicationRootId = publicationStep.StableSemanticRootId ?? publicationStep.Id;
        candidateFingerprintService.ValidateWorkspaceRoot(flow);

        if (string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson))
        {
            throw new InvalidOperationException(
                "Host-controlled publication requires a governed outcome-verification flow.");
        }
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        var candidate = state.CurrentCandidate;
        if (state.Status != OutcomeVerificationStatus.Passed ||
            candidate is null ||
            state.Stale ||
            !string.Equals(
                candidate.Fingerprint,
                state.VerifiedCandidateFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Host-controlled publication requires a current authoritative QA PASS.");
        }
        if (!await candidateFingerprintService.IsCurrentAsync(
                flow,
                candidate,
                CandidateFingerprintService.RequiresPreview(
                    state.AcceptancePlan),
                cancellationToken))
        {
            throw new InvalidOperationException(
                "The local candidate changed before host-controlled publication.");
        }
        if (TryReusePublishedJournal(
                flow,
                state,
                candidate,
                publicationRootId,
                out var existingReport))
        {
            return existingReport;
        }
        await BeginPublicationAsync(
            flow.Id,
            publicationRootId,
            candidate,
            cancellationToken);
        if (flow.Outcome == OutcomeType.Commit)
        {
            foreach (var repository in candidate.Manifest.Repositories)
            {
                var journal = await LoadPublicationJournalAsync(
                    flow.Id,
                    cancellationToken);
                var persistedRepository = journal.Repositories.Single(item =>
                    string.Equals(
                        item.RelativePath,
                        repository.RelativePath,
                        StringComparison.Ordinal));
                if (persistedRepository.Status == OutcomeRepositoryPublicationStatus.Published)
                {
                    continue;
                }
                await MarkRepositoryAsync(
                    flow.Id,
                    publicationRootId,
                    repository,
                    OutcomeRepositoryPublicationStatus.Publishing,
                    remoteRepository: string.Empty,
                    pullRequestUrl: string.Empty,
                    cancellationToken);
                await MarkRepositoryAsync(
                    flow.Id,
                    publicationRootId,
                    repository,
                    OutcomeRepositoryPublicationStatus.Published,
                    remoteRepository: string.Empty,
                    pullRequestUrl: string.Empty,
                    cancellationToken);
            }
            await CompletePublicationAsync(
                flow.Id,
                publicationRootId,
                cancellationToken);
            return BuildPublicationReport(
                flow.Outcome,
                candidate,
                await LoadPublicationJournalAsync(flow.Id, cancellationToken));
        }

        var gitHubToken = await ResolveGitHubTokenAsync(
            AppContext.BaseDirectory,
            cancellationToken);
        var ghEnvironment = BuildGitHubCliEnvironment(gitHubToken);
        foreach (var repository in candidate.Manifest.Repositories)
        {
            var journal = await LoadPublicationJournalAsync(
                flow.Id,
                cancellationToken);
            var persistedRepository = journal.Repositories.Single(item =>
                string.Equals(
                    item.RelativePath,
                    repository.RelativePath,
                    StringComparison.Ordinal));
            if (persistedRepository.Status == OutcomeRepositoryPublicationStatus.Published)
            {
                continue;
            }
            var workspaceRepository = ResolveWorkspaceRepository(
                flow.WorkspacePath,
                repository.RelativePath);
            var repositoryName = repository.RemoteRepository;
            if (string.IsNullOrWhiteSpace(repositoryName))
            {
                throw new InvalidOperationException(
                    $"Repository '{repository.RelativePath}' has no trusted GitHub publication target.");
            }
            var trustedRemoteUrl = BuildTrustedGitHubRemoteUrl(repositoryName);
            await MarkRepositoryAsync(
                flow.Id,
                publicationRootId,
                repository,
                OutcomeRepositoryPublicationStatus.Publishing,
                repositoryName,
                pullRequestUrl: string.Empty,
                cancellationToken);
            var publicationProof = await PublishVerifiedGitBranchAsync(
                processRunner,
                workspaceRepository,
                repository,
                trustedRemoteUrl,
                flow.BranchName,
                gitHubToken,
                cancellationToken);

            var pullRequestUrl = await FindOpenPullRequestAsync(
                AppContext.BaseDirectory,
                repositoryName,
                flow.BranchName,
                repository.Head,
                ghEnvironment,
                cancellationToken);
            if (pullRequestUrl is null)
            {
                var create = await RunRequiredAsync(
                    "gh",
                    [
                        "pr", "create",
                        "--repo", repositoryName,
                        "--head", flow.BranchName,
                        "--title", Clip(flow.Title, 200),
                        "--body", BuildPullRequestBody(flow, candidate)
                    ],
                    AppContext.BaseDirectory,
                    TimeSpan.FromMinutes(2),
                    $"create the pull request for '{repository.RelativePath}'",
                    cancellationToken,
                    ghEnvironment);
                pullRequestUrl = PullRequestUrlPattern()
                    .Match(create.CombinedOutput)
                    .Value;
                if (string.IsNullOrWhiteSpace(pullRequestUrl))
                {
                    throw new InvalidOperationException(
                        $"GitHub did not return a pull request URL for '{repository.RelativePath}'.");
                }
            }

            await MarkRepositoryAsync(
                flow.Id,
                publicationRootId,
                repository,
                OutcomeRepositoryPublicationStatus.Published,
                repositoryName,
                pullRequestUrl,
                cancellationToken);
        }

        if (!await candidateFingerprintService.IsCurrentAsync(
                flow,
                candidate,
                CandidateFingerprintService.RequiresPreview(
                    state.AcceptancePlan),
                cancellationToken))
        {
            throw new InvalidOperationException(
                "The local candidate changed during host-controlled publication.");
        }
        await CompletePublicationAsync(
            flow.Id,
            publicationRootId,
            cancellationToken);
        return BuildPublicationReport(
            flow.Outcome,
            candidate,
            await LoadPublicationJournalAsync(flow.Id, cancellationToken));
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
        TimeSpan? remoteTimeout = null)
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
            gitHubToken);
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
            .AsNoTracking()
            .SingleAsync(item => item.Id == flowId, cancellationToken);
        var step = await database.FlowSteps
            .AsNoTracking()
            .SingleAsync(item => item.Id == publicationStepId, cancellationToken);
        return new PublicationState(flow, step);
    }

    private async Task<OutcomePublicationJournal> LoadPublicationJournalAsync(
        Guid flowId,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var json = await database.Flows
            .AsNoTracking()
            .Where(item => item.Id == flowId)
            .Select(item => item.OutcomeVerificationJson)
            .SingleAsync(cancellationToken);
        var state = OutcomeVerificationRules.DeserializeAggregate(json);
        return state.Publication
            ?? throw new InvalidOperationException(
                "Host-controlled publication has no durable journal.");
    }

    private static bool TryReusePublishedJournal(
        FlowRun flow,
        OutcomeVerificationState state,
        OutcomeCandidateSnapshot candidate,
        Guid publicationRootId,
        out string report)
    {
        report = string.Empty;
        var journal = state.Publication;
        if (journal is null ||
            journal.StepId != publicationRootId ||
            !string.Equals(
                journal.CandidateFingerprint,
                candidate.Fingerprint,
                StringComparison.Ordinal) ||
            journal.Repositories.Any(item =>
                item.Status != OutcomeRepositoryPublicationStatus.Published) ||
            journal.Status != OutcomePublicationStatus.Published)
        {
            return false;
        }

        report = BuildPublicationReport(flow.Outcome, candidate, journal);
        return true;
    }

    private static string BuildPublicationReport(
        OutcomeType outcome,
        OutcomeCandidateSnapshot candidate,
        OutcomePublicationJournal journal)
    {
        var report = new List<string>
        {
            $"Verified candidate: {candidate.Fingerprint}"
        };
        foreach (var repository in journal.Repositories)
        {
            report.Add(
                outcome == OutcomeType.Commit
                    ? $"Repository {repository.RelativePath}: commit {repository.Head}, tree {repository.Tree}"
                    : $"Repository {repository.RelativePath}: verified remote commit {repository.Head}, tree {repository.Tree}, pull request {repository.PullRequestUrl}");
        }
        return string.Join(Environment.NewLine, report);
    }

    private async Task BeginPublicationAsync(
        Guid flowId,
        Guid publicationRootId,
        OutcomeCandidateSnapshot candidate,
        CancellationToken cancellationToken)
    {
        if (publicationRootId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "Host-controlled publication requires its semantic FlowStep ID.");
        }
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var flow = await database.Flows.SingleAsync(
            item => item.Id == flowId,
            cancellationToken);
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        var created = state.Publication is null ||
                      state.Publication.StepId != publicationRootId ||
                      !string.Equals(
                          state.Publication.CandidateFingerprint,
                          candidate.Fingerprint,
                          StringComparison.Ordinal);
        if (created)
        {
            state.Publication = new OutcomePublicationJournal
            {
                StepId = publicationRootId,
                CandidateFingerprint = candidate.Fingerprint,
                Status = OutcomePublicationStatus.Publishing,
                Repositories = candidate.Manifest.Repositories.Select(repository =>
                    new OutcomeRepositoryPublication
                    {
                        RelativePath = repository.RelativePath,
                        Head = repository.Head,
                        Tree = repository.Tree
                    }).ToList()
            };
        }
        else
        {
            state.Publication!.StepId = publicationRootId;
            state.Publication.Status = AdvancePublicationStatus(
                state.Publication.Status,
                OutcomePublicationStatus.Publishing);
            state.Publication.UpdatedAt = DateTimeOffset.UtcNow;
        }
        state.UpdatedAt = DateTimeOffset.UtcNow;
        flow.OutcomeVerificationJson =
            OutcomeVerificationRules.SerializeAggregate(state);
        if (created)
        {
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = publicationRootId,
                Type = "outcome.publication.started",
                Message =
                    $"Host-controlled publication started for candidate {candidate.Fingerprint}."
            });
        }
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkRepositoryAsync(
        Guid flowId,
        Guid publicationRootId,
        CandidateRepositoryManifest repository,
        OutcomeRepositoryPublicationStatus status,
        string remoteRepository,
        string pullRequestUrl,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var flow = await database.Flows.SingleAsync(
            item => item.Id == flowId,
            cancellationToken);
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        var journal = state.Publication
            ?? throw new InvalidOperationException(
                "Publication repository transition has no durable publication journal.");
        if (journal.StepId != publicationRootId)
        {
            throw new InvalidOperationException(
                "Publication repository transition belongs to a different FlowStep.");
        }
        var item = journal.Repositories.Single(repositoryJournal =>
            string.Equals(
                repositoryJournal.RelativePath,
                repository.RelativePath,
                StringComparison.Ordinal));
        var nextStatus = AdvanceRepositoryStatus(item.Status, status);
        var nextRemoteRepository = string.IsNullOrWhiteSpace(remoteRepository)
            ? item.RemoteRepository
            : remoteRepository;
        var nextPullRequestUrl = string.IsNullOrWhiteSpace(pullRequestUrl)
            ? item.PullRequestUrl
            : pullRequestUrl;
        var changed =
            item.Status != nextStatus ||
            !string.Equals(
                item.RemoteRepository,
                nextRemoteRepository,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                item.PullRequestUrl,
                nextPullRequestUrl,
                StringComparison.OrdinalIgnoreCase);
        item.Status = nextStatus;
        item.RemoteRepository = nextRemoteRepository;
        item.PullRequestUrl = nextPullRequestUrl;
        journal.UpdatedAt = DateTimeOffset.UtcNow;
        state.UpdatedAt = journal.UpdatedAt;
        flow.OutcomeVerificationJson =
            OutcomeVerificationRules.SerializeAggregate(state);
        if (changed)
        {
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = publicationRootId,
                Type = nextStatus == OutcomeRepositoryPublicationStatus.Published
                    ? "outcome.publication.repository-published"
                    : "outcome.publication.repository-started",
                Message =
                    $"{repository.RelativePath} publication is {nextStatus} at immutable commit {repository.Head}." +
                    (string.IsNullOrWhiteSpace(nextPullRequestUrl)
                        ? string.Empty
                        : $" Pull request: {nextPullRequestUrl}")
            });
        }
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task CompletePublicationAsync(
        Guid flowId,
        Guid publicationRootId,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var flow = await database.Flows.SingleAsync(
            item => item.Id == flowId,
            cancellationToken);
        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        var journal = state.Publication
            ?? throw new InvalidOperationException(
                "Publication completion has no durable publication journal.");
        if (journal.StepId != publicationRootId ||
            journal.Repositories.Any(item =>
                item.Status != OutcomeRepositoryPublicationStatus.Published))
        {
            throw new InvalidOperationException(
                "Publication cannot complete before every repository is durably recorded.");
        }
        var nextStatus = AdvancePublicationStatus(
            journal.Status,
            OutcomePublicationStatus.Published);
        var changed = journal.Status != nextStatus;
        journal.Status = nextStatus;
        journal.UpdatedAt = DateTimeOffset.UtcNow;
        state.UpdatedAt = journal.UpdatedAt;
        flow.OutcomeVerificationJson =
            OutcomeVerificationRules.SerializeAggregate(state);
        if (changed)
        {
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = publicationRootId,
                Type = "outcome.publication.completed",
                Message =
                    "Every repository publication side effect is durably recorded and ready for verification."
            });
        }
        await database.SaveChangesAsync(cancellationToken);
    }

    private sealed record PublicationState(
        FlowRun Flow,
        FlowStep Step);

    internal static OutcomePublicationStatus AdvancePublicationStatus(
        OutcomePublicationStatus current,
        OutcomePublicationStatus requested) =>
        (OutcomePublicationStatus)Math.Max((int)current, (int)requested);

    internal static OutcomeRepositoryPublicationStatus AdvanceRepositoryStatus(
        OutcomeRepositoryPublicationStatus current,
        OutcomeRepositoryPublicationStatus requested) =>
        (OutcomeRepositoryPublicationStatus)Math.Max(
            (int)current,
            (int)requested);

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

    private static string BuildPullRequestBody(
        FlowRun flow,
        OutcomeCandidateSnapshot candidate) =>
        $"""
         ## Customer outcome

         {Clip(flow.ConsolidatedRequest, 3_000)}

         ## Verification

         - Acceptance plan: `{candidate.Manifest.AcceptancePlanHash}`
         - Candidate fingerprint: `{candidate.Fingerprint}`
         - Every repository commit and tree was published from the verified manifest.
         """;

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
            string gitHubToken)
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
                };

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
                    $"AUTHORIZATION: bearer {gitHubToken.Trim()}"));
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

using System.Text.Json;
using System.Text.RegularExpressions;
using AiHarnessDemo.Core.Domain;

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
    ProcessRunner processRunner) : IPublishedOutcomeVerifier
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
        var repositories = RepositoryAnalyzer.FindGitRepositories(flow.RepositoryPath);
        if (repositories.Count == 0)
        {
            throw new InvalidOperationException(
                "The approved publication cannot be verified because the source project has no Git repositories.");
        }

        if (flow.Outcome == OutcomeType.PullRequest)
        {
            var reference = ParsePullRequest(releaseOutput)
                ?? throw new InvalidOperationException(
                    "The approved Release handoff did not report a GitHub pull request URL.");
            var sourceRepository = await FindRepositoryAsync(
                repositories,
                reference.Repository,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    $"The reported pull request repository '{reference.Repository}' is not part of this flow.");
            var result = await processRunner.RunAsync(
                "gh",
                [
                    "pr", "view", reference.Number.ToString(),
                    "--repo", reference.Repository,
                    "--json", "state,url,headRefName,headRefOid"
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
            var state = root.GetProperty("state").GetString();
            var url = root.GetProperty("url").GetString();
            var headRefName = root.GetProperty("headRefName").GetString();
            var headRefOid = root.GetProperty("headRefOid").GetString();
            if (!string.Equals(state, "OPEN", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(url, reference.Url, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(headRefName, flow.BranchName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The reported pull request is not an open PR for this flow branch.");
            }

            var workspaceRepository = ResolveWorkspaceRepository(
                flow,
                sourceRepository,
                repositories.Count);
            var localHead = await ReadHeadAsync(workspaceRepository, cancellationToken);
            if (!string.Equals(localHead, headRefOid, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The reported pull request head does not match the verified flow workspace commit.");
            }
            return new PublishedOutcome(reference.Url, $"Published pull request #{reference.Number}");
        }

        var reportedShas = CommitPattern().Matches(releaseOutput)
            .Select(match => match.Groups["sha"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var repository in repositories)
        {
            var workspaceRepository = ResolveWorkspaceRepository(
                flow,
                repository,
                repositories.Count);
            var head = await ReadHeadAsync(workspaceRepository, cancellationToken);
            var reported = reportedShas.FirstOrDefault(sha =>
                head.StartsWith(sha, StringComparison.OrdinalIgnoreCase));
            if (reported is not null)
            {
                return new PublishedOutcome(
                    $"#/preview/{flow.Id}",
                    $"Approved commit · {head[..12]}");
            }
        }
        throw new InvalidOperationException(
            "The approved Release handoff did not report a commit at the flow workspace HEAD.");
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
            ? match.Groups["repo"].Value.TrimEnd('/').Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase)
            : null;
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
                ["-C", repository, "remote", "get-url", "origin"],
                repository,
                TimeSpan.FromSeconds(20),
                cancellationToken);
            if (remote.ExitCode == 0 &&
                string.Equals(
                    NormalizeGitHubRepository(remote.StandardOutput),
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
            ? flow.WorkspacePath
            : Path.Combine(
                flow.WorkspacePath,
                Path.GetRelativePath(projectPath, sourceRepository));
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

    internal sealed record PullRequestReference(
        string Url,
        string Repository,
        int Number);
}

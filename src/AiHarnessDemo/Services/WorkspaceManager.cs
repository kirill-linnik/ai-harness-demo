using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Verification;
using System.Text.RegularExpressions;

namespace AiHarnessDemo.Services;

public sealed partial class WorkspaceManager(
    ProcessRunner processRunner,
    WorkflowDefinitionProvider workflowProvider,
    WorkspaceHookRunner hookRunner,
    ILogger<WorkspaceManager> logger) : IWorkspaceManager
{
    [GeneratedRegex("[^A-Za-z0-9._-]")]
    private static partial Regex UnsafeCharacters();

    public async Task<WorkspaceInfo> PrepareAsync(
        FlowRun flow,
        CancellationToken cancellationToken = default)
    {
        if (!ExecutableLocator.Exists("git"))
        {
            throw new InvalidOperationException(
                "Git is required for isolated live Copilot execution.");
        }

        var projectPath = Path.GetFullPath(flow.RepositoryPath);
        if (!Directory.Exists(projectPath))
        {
            throw new DirectoryNotFoundException(
                $"The selected project folder no longer exists: {projectPath}");
        }

        var repositories = RepositoryAnalyzer.FindGitRepositories(projectPath);
        if (repositories.Count == 0)
        {
            throw new InvalidOperationException(
                "Copilot flows require at least one Git repository inside the selected project folder.");
        }
        var trustedRepositories = await ReadTrustedRepositoriesAsync(
            flow,
            projectPath,
            repositories,
            cancellationToken);
        var authorizedWorkspaceRoot = Path.GetFullPath(
            workflowProvider.GetValidated().Config.Workspace.ResolvedRoot);
        Directory.CreateDirectory(authorizedWorkspaceRoot);
        WorkspacePathGuard.ValidateAuthorizedRoot(
            authorizedWorkspaceRoot,
            "Workspace preparation");
        var shortId = flow.Id.ToString("N")[..16];
        var workspacePath = ResolveContained(UnsafeCharacters().Replace(shortId, "_"));
        if (!string.IsNullOrWhiteSpace(flow.WorkspacePath) &&
            !PathsEqual(workspacePath, flow.WorkspacePath))
        {
            throw new InvalidOperationException(
                $"Flow workspace is outside its expected isolated location: {flow.WorkspacePath}");
        }

        if (!string.IsNullOrWhiteSpace(flow.WorkspacePath) &&
            Directory.Exists(flow.WorkspacePath))
        {
            var validatedWorkspace = WorkspacePathGuard.ValidateExistingRoot(
                flow.WorkspacePath,
                authorizedWorkspaceRoot,
                "Workspace recovery");
            CopilotReasoningHost.GovernedGitIsolationScope.RecoverInterrupted(
                validatedWorkspace);
            return new WorkspaceInfo(
                validatedWorkspace,
                flow.BranchName,
                CreatedNow: false,
                trustedRepositories);
        }

        var branchName = $"ai-harness/{Slug(flow.Title)}-{shortId}";

        bool createdNow;
        if (repositories.Count == 1 &&
            PathsEqual(projectPath, repositories[0]))
        {
            createdNow = await EnsureWorktreeAsync(
                repositories[0],
                workspacePath,
                branchName,
                cancellationToken);
        }
        else
        {
            createdNow = await PrepareProjectWorkspaceAsync(
                projectPath,
                repositories,
                workspacePath,
                branchName,
                cancellationToken);
        }
        workspacePath = WorkspacePathGuard.ValidateExistingRoot(
            workspacePath,
            authorizedWorkspaceRoot,
            "Workspace preparation");

        logger.LogInformation(
            "Prepared project workspace {WorkspacePath} with {RepositoryCount} repositories on {BranchName} for flow {FlowId}",
            workspacePath,
            repositories.Count,
            branchName,
            flow.Id);
        if (createdNow)
        {
            await hookRunner.RunAsync(
                WorkspaceHookStage.AfterCreate,
                workspacePath,
                cancellationToken);
        }

        return new WorkspaceInfo(
            workspacePath,
            branchName,
            createdNow,
            trustedRepositories);
    }

    private async Task<IReadOnlyList<WorkspaceRepositoryIdentity>>
        ReadTrustedRepositoriesAsync(
            FlowRun flow,
            string projectPath,
            IReadOnlyList<string> repositories,
            CancellationToken cancellationToken)
    {
        var relativePaths = repositories
            .Select(repository => NormalizeRepositoryPath(projectPath, repository))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson))
        {
            var state = OutcomeVerificationRules.DeserializeAggregate(
                flow.OutcomeVerificationJson);
            if (state.TrustedRepositories.Count > 0)
            {
                var persistedPaths = state.TrustedRepositories
                    .Select(item => item.RelativePath)
                    .ToArray();
                if (!persistedPaths.SequenceEqual(
                        relativePaths,
                        StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The selected source repository set changed after workspace authorization.");
                }
                return state.TrustedRepositories
                    .Select(item => new WorkspaceRepositoryIdentity(
                        item.RelativePath,
                        item.RemoteRepository))
                    .ToArray();
            }
        }

        var result = new List<WorkspaceRepositoryIdentity>(repositories.Count);
        foreach (var repository in repositories)
        {
            var remoteRepository = await ReadConfiguredRemoteRepositoryAsync(
                repository,
                cancellationToken);
            result.Add(new WorkspaceRepositoryIdentity(
                NormalizeRepositoryPath(projectPath, repository),
                remoteRepository));
        }
        return result
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<string> ReadConfiguredRemoteRepositoryAsync(
        string repository,
        CancellationToken cancellationToken)
    {
        var pushUrls = await ReadConfiguredRemoteValuesAsync(
            repository,
            "remote.origin.pushurl",
            cancellationToken);
        if (pushUrls.Count > 0)
        {
            return PublishedOutcomeVerifier.NormalizeSingleGitHubRemote(
                       string.Join(Environment.NewLine, pushUrls))
                   ?? string.Empty;
        }

        var urls = await ReadConfiguredRemoteValuesAsync(
            repository,
            "remote.origin.url",
            cancellationToken);
        return PublishedOutcomeVerifier.NormalizeSingleGitHubRemote(
                   string.Join(Environment.NewLine, urls))
               ?? string.Empty;
    }

    private async Task<IReadOnlyList<string>> ReadConfiguredRemoteValuesAsync(
        string repository,
        string key,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            "git",
            [
                "-C", repository,
                "config", "--local", "--null", "--get-all", "--no-includes", key
            ],
            repository,
            TimeSpan.FromSeconds(20),
            cancellationToken);
        if (result.ExitCode == 1 &&
            string.IsNullOrWhiteSpace(result.StandardOutput) &&
            string.IsNullOrWhiteSpace(result.StandardError))
        {
            return [];
        }
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to read Git configuration '{key}' for '{repository}': {result.CombinedOutput}");
        }

        return result.StandardOutput.Split(
                '\0',
                StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private static string NormalizeRepositoryPath(
        string projectPath,
        string repositoryPath) =>
        PathsEqual(projectPath, repositoryPath)
            ? "."
            : Path.GetRelativePath(projectPath, repositoryPath)
                .Replace('\\', '/');

    public async Task<WorkspaceCleanupResult> RemoveAsync(
        FlowRun flow,
        CancellationToken cancellationToken = default)
    {
        var branchName = string.IsNullOrWhiteSpace(flow.BranchName)
            ? $"ai-harness/{Slug(flow.Title)}-{flow.Id.ToString("N")[..16]}"
            : flow.BranchName;

        var expectedWorkspace = ResolveContained(flow.Id.ToString("N")[..16]);
        if (!string.IsNullOrWhiteSpace(flow.WorkspacePath) &&
            !PathsEqual(expectedWorkspace, flow.WorkspacePath))
        {
            throw new InvalidOperationException(
                $"Flow workspace is outside its expected isolated location: {flow.WorkspacePath}");
        }
        var authorizedWorkspaceRoot = Path.GetFullPath(
            workflowProvider.GetValidated().Config.Workspace.ResolvedRoot);
        if (Directory.Exists(authorizedWorkspaceRoot))
        {
            WorkspacePathGuard.ValidateAuthorizedRoot(
                authorizedWorkspaceRoot,
                "Workspace cleanup");
        }
        if (Directory.Exists(expectedWorkspace))
        {
            WorkspacePathGuard.ValidateExistingRoot(
                expectedWorkspace,
                authorizedWorkspaceRoot,
                "Workspace cleanup");
        }

        var projectPath = Path.GetFullPath(flow.RepositoryPath);
        if (!Directory.Exists(projectPath))
        {
            if (Directory.Exists(expectedWorkspace))
            {
                Directory.Delete(expectedWorkspace, recursive: true);
            }
            return WorkspaceCleanupResult.Empty;
        }
        var repositories = RepositoryAnalyzer.FindGitRepositories(projectPath);
        if (repositories.Count == 0)
        {
            throw new InvalidOperationException(
                "No source Git repositories remain for flow cleanup.");
        }
        var branchValidation = await processRunner.RunAsync(
            "git",
            ["check-ref-format", "--branch", branchName],
            projectPath,
            TimeSpan.FromSeconds(20),
            cancellationToken);
        if (branchValidation.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Flow branch is not a valid Git branch name: {branchName}");
        }

        if (Directory.Exists(expectedWorkspace))
        {
            await hookRunner.RunAsync(
                WorkspaceHookStage.BeforeRemove,
                expectedWorkspace,
                cancellationToken);
        }

        var worktreesRemoved = 0;
        var localBranchesDeleted = 0;
        var remoteBranchesDeleted = 0;
        foreach (var repository in repositories)
        {
            var relativePath = Path.GetRelativePath(projectPath, repository);
            var repositoryWorkspace =
                repositories.Count == 1 && PathsEqual(projectPath, repository)
                    ? expectedWorkspace
                    : ResolveUnderWorkspace(expectedWorkspace, relativePath);
            if (Directory.Exists(repositoryWorkspace))
            {
                var remove = await processRunner.RunAsync(
                    "git",
                    ["-C", repository, "worktree", "remove", "--force", repositoryWorkspace],
                    repository,
                    TimeSpan.FromMinutes(2),
                    cancellationToken);
                if (remove.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Unable to remove flow worktree '{repositoryWorkspace}': {remove.CombinedOutput}");
                }
                worktreesRemoved++;
            }

            var prune = await processRunner.RunAsync(
                "git",
                ["-C", repository, "worktree", "prune"],
                repository,
                TimeSpan.FromSeconds(30),
                cancellationToken);
            if (prune.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Unable to prune flow worktree metadata for '{repository}': {prune.CombinedOutput}");
            }

            var localBranch = await processRunner.RunAsync(
                "git",
                ["-C", repository, "show-ref", "--verify", "--quiet", $"refs/heads/{branchName}"],
                repository,
                TimeSpan.FromSeconds(20),
                cancellationToken);
            if (localBranch.ExitCode == 0)
            {
                var deleteBranch = await processRunner.RunAsync(
                    "git",
                    ["-C", repository, "branch", "-D", branchName],
                    repository,
                    TimeSpan.FromSeconds(30),
                    cancellationToken);
                if (deleteBranch.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Unable to delete local flow branch '{branchName}': {deleteBranch.CombinedOutput}");
                }
                localBranchesDeleted++;
            }

            var remote = await processRunner.RunAsync(
                "git",
                ["-C", repository, "remote", "get-url", "origin"],
                repository,
                TimeSpan.FromSeconds(20),
                cancellationToken);
            if (remote.ExitCode != 0)
            {
                continue;
            }
            var remoteBranch = await processRunner.RunAsync(
                "git",
                [
                    "-C", repository,
                    "ls-remote", "--exit-code", "--heads", "origin",
                    $"refs/heads/{branchName}"
                ],
                repository,
                TimeSpan.FromSeconds(30),
                cancellationToken);
            if (remoteBranch.ExitCode == 0 &&
                !string.IsNullOrWhiteSpace(remoteBranch.StandardOutput))
            {
                var deleteRemote = await processRunner.RunAsync(
                    "git",
                    ["-C", repository, "push", "origin", "--delete", branchName],
                    repository,
                    TimeSpan.FromMinutes(2),
                    cancellationToken);
                if (deleteRemote.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Unable to delete remote flow branch '{branchName}': {deleteRemote.CombinedOutput}");
                }
                remoteBranchesDeleted++;
            }
            else if (remoteBranch.ExitCode is not (0 or 2))
            {
                throw new InvalidOperationException(
                    $"Unable to inspect remote flow branch '{branchName}': {remoteBranch.CombinedOutput}");
            }
        }

        if (Directory.Exists(expectedWorkspace))
        {
            Directory.Delete(expectedWorkspace, recursive: true);
        }
        logger.LogInformation(
            "Removed flow workspace {WorkspacePath}, {WorktreeCount} worktrees, {LocalBranchCount} local branches, and {RemoteBranchCount} remote branches for flow {FlowId}",
            expectedWorkspace,
            worktreesRemoved,
            localBranchesDeleted,
            remoteBranchesDeleted,
            flow.Id);
        return new WorkspaceCleanupResult(
            worktreesRemoved,
            localBranchesDeleted,
            remoteBranchesDeleted);
    }

    private async Task<bool> PrepareProjectWorkspaceAsync(
        string projectPath,
        IReadOnlyList<string> repositories,
        string workspacePath,
        string branchName,
        CancellationToken cancellationToken)
    {
        var createdNow = !Directory.Exists(workspacePath);
        Directory.CreateDirectory(workspacePath);
        CopyProjectScaffold(projectPath, workspacePath, repositories);

        foreach (var repository in repositories)
        {
            var relativePath = Path.GetRelativePath(projectPath, repository);
            var repositoryWorkspace = ResolveUnderWorkspace(workspacePath, relativePath);
            createdNow |= await EnsureWorktreeAsync(
                repository,
                repositoryWorkspace,
                branchName,
                cancellationToken);
        }

        return createdNow;
    }

    private async Task<bool> EnsureWorktreeAsync(
        string repositoryPath,
        string workspacePath,
        string branchName,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(workspacePath))
        {
            var existingBranch = await processRunner.RunAsync(
                "git",
                ["-C", workspacePath, "branch", "--show-current"],
                workspacePath,
                TimeSpan.FromSeconds(20),
                cancellationToken);
            if (existingBranch.ExitCode == 0 &&
                string.Equals(
                    existingBranch.StandardOutput.Trim(),
                    branchName,
                    StringComparison.Ordinal))
            {
                logger.LogInformation(
                    "Recovered existing worktree {WorkspacePath} on {BranchName}",
                    workspacePath,
                    branchName);
                return false;
            }

            throw new IOException(
                $"Worktree path exists but is not the expected flow branch '{branchName}': {workspacePath}");
        }

        Directory.CreateDirectory(
            Path.GetDirectoryName(workspacePath)
            ?? throw new InvalidOperationException(
                $"Workspace path has no parent directory: {workspacePath}"));
        var branchProbe = await processRunner.RunAsync(
            "git",
            ["-C", repositoryPath, "show-ref", "--verify", "--quiet", $"refs/heads/{branchName}"],
            repositoryPath,
            TimeSpan.FromSeconds(20),
            cancellationToken);
        var arguments = branchProbe.ExitCode == 0
            ? new[]
            {
                "-C", repositoryPath,
                "worktree", "add",
                workspacePath,
                branchName
            }
            : [
                "-C", repositoryPath,
                "worktree", "add",
                "-b", branchName,
                workspacePath,
                "HEAD"
            ];
        var create = await processRunner.RunAsync(
            "git",
            arguments,
            repositoryPath,
            TimeSpan.FromMinutes(2),
            cancellationToken);

        if (create.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to create isolated worktree for '{repositoryPath}': {create.CombinedOutput}");
        }

        return true;
    }

    private void CopyProjectScaffold(
        string projectPath,
        string workspacePath,
        IReadOnlyCollection<string> repositories)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var excludedDirectories = repositories
            .Append(workflowProvider.GetValidated().Config.Workspace.ResolvedRoot)
            .Append(workspacePath)
            .Select(Path.GetFullPath)
            .ToHashSet(comparison);
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((projectPath, workspacePath));

        while (pending.Count > 0)
        {
            var (source, destination) = pending.Pop();
            Directory.CreateDirectory(destination);

            foreach (var file in Directory.EnumerateFiles(source))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var target = Path.Combine(destination, Path.GetFileName(file));
                if (!File.Exists(target))
                {
                    File.Copy(file, target);
                }
            }

            foreach (var directory in Directory.EnumerateDirectories(source))
            {
                var fullPath = Path.GetFullPath(directory);
                if (excludedDirectories.Contains(fullPath) ||
                    RepositoryAnalyzer.ShouldIgnoreDirectory(fullPath) ||
                    !RepositoryAnalyzer.CanTraverse(fullPath))
                {
                    continue;
                }

                pending.Push((
                    fullPath,
                    Path.Combine(destination, Path.GetFileName(fullPath))));
            }
        }
    }

    private static string Slug(string value)
    {
        var characters = value
            .ToLowerInvariant()
            .Select(character =>
                char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        var segments = new string(characters)
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Take(6);
        var slug = string.Join('-', segments);
        return string.IsNullOrWhiteSpace(slug) ? "customer-change" : slug;
    }

    private string ResolveContained(string key)
    {
        var workspaceRoot = Path.GetFullPath(
            workflowProvider.GetValidated().Config.Workspace.ResolvedRoot);
        var combined = Path.GetFullPath(Path.Combine(workspaceRoot, key));
        var rootWithSeparator = workspaceRoot.EndsWith(Path.DirectorySeparatorChar)
            ? workspaceRoot
            : workspaceRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (combined != workspaceRoot &&
            !combined.StartsWith(rootWithSeparator, comparison))
        {
            throw new InvalidOperationException(
                $"Resolved workspace escaped the configured root: {key}");
        }

        return combined;
    }

    private static string ResolveUnderWorkspace(string workspacePath, string relativePath)
    {
        var root = Path.GetFullPath(workspacePath);
        var combined = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!combined.StartsWith(rootWithSeparator, comparison))
        {
            throw new InvalidOperationException(
                $"Repository path escaped the flow workspace: {relativePath}");
        }

        return combined;
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            comparison);
    }
}

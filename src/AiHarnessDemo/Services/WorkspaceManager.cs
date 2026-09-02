using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Orchestration;
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

        if (!string.IsNullOrWhiteSpace(flow.WorkspacePath) &&
            Directory.Exists(flow.WorkspacePath))
        {
            return new WorkspaceInfo(flow.WorkspacePath, flow.BranchName, CreatedNow: false);
        }

        var repositories = RepositoryAnalyzer.FindGitRepositories(projectPath);
        if (repositories.Count == 0)
        {
            throw new InvalidOperationException(
                "Copilot flows require at least one Git repository inside the selected project folder.");
        }

        var shortId = flow.Id.ToString("N")[..16];
        var branchName = $"ai-harness/{Slug(flow.Title)}-{shortId}";
        Directory.CreateDirectory(workflowProvider.GetValidated().Config.Workspace.ResolvedRoot);
        var workspacePath = ResolveContained(UnsafeCharacters().Replace(shortId, "_"));

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

        return new WorkspaceInfo(workspacePath, branchName, createdNow);
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

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

        var repositoryPath = Path.GetFullPath(flow.RepositoryPath);
        var probe = await processRunner.RunAsync(
            "git",
            ["-C", repositoryPath, "rev-parse", "--is-inside-work-tree"],
            repositoryPath,
            TimeSpan.FromSeconds(20),
            cancellationToken);
        if (probe.ExitCode != 0 ||
            !probe.StandardOutput.Contains("true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Copilot flows require the selected repository to be a Git work tree.");
        }

        if (!string.IsNullOrWhiteSpace(flow.WorkspacePath) &&
            Directory.Exists(flow.WorkspacePath))
        {
            return new WorkspaceInfo(flow.WorkspacePath, flow.BranchName, CreatedNow: false);
        }

        var shortId = flow.Id.ToString("N")[..16];
        var branchName = $"ai-harness/{Slug(flow.Title)}-{shortId}";
        Directory.CreateDirectory(workflowProvider.GetValidated().Config.Workspace.ResolvedRoot);
        var workspacePath = ResolveContained(UnsafeCharacters().Replace(shortId, "_"));
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
                    "Recovered existing worktree {WorkspacePath} on {BranchName} for flow {FlowId}",
                    workspacePath,
                    branchName,
                    flow.Id);
                return new WorkspaceInfo(
                    workspacePath,
                    branchName,
                    CreatedNow: false);
            }

            throw new IOException(
                $"Worktree path exists but is not the expected flow branch '{branchName}': {workspacePath}");
        }

        var create = await processRunner.RunAsync(
            "git",
            [
                "-C", repositoryPath,
                "worktree", "add",
                "-b", branchName,
                workspacePath,
                "HEAD"
            ],
            repositoryPath,
            TimeSpan.FromMinutes(2),
            cancellationToken);

        if (create.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to create isolated worktree: {create.CombinedOutput}");
        }

        logger.LogInformation(
            "Prepared worktree {WorkspacePath} on {BranchName} for flow {FlowId}",
            workspacePath,
            branchName,
            flow.Id);
        await hookRunner.RunAsync(
            WorkspaceHookStage.AfterCreate,
            workspacePath,
            cancellationToken);
        return new WorkspaceInfo(workspacePath, branchName, CreatedNow: true);
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
}

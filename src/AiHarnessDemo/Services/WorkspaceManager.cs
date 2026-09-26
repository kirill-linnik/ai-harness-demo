using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Verification;
using System.Text.RegularExpressions;

namespace AiHarnessDemo.Services;

public enum GuardedSnapshotTransition
{
    OwnershipJournaled,
    FinalDirectoryMoved
}

public interface IGuardedSnapshotFaultInjector
{
    Task OnTransitionAsync(
        GuardedSnapshotTransition transition,
        string workspacePath,
        CancellationToken cancellationToken);
}

public sealed partial class WorkspaceManager(
    ProcessRunner processRunner,
    WorkflowDefinitionProvider workflowProvider,
    WorkspaceHookRunner hookRunner,
    ILogger<WorkspaceManager> logger,
    AdvisoryArtifactCatalog? advisoryArtifactCatalog = null,
    IGuardedSnapshotFaultInjector? guardedSnapshotFaultInjector = null)
    : IWorkspaceManager
{
    private readonly AdvisoryArtifactCatalog _advisoryArtifacts =
        advisoryArtifactCatalog ?? new AdvisoryArtifactCatalog(workflowProvider);

    [GeneratedRegex("[^A-Za-z0-9._-]")]
    private static partial Regex UnsafeCharacters();

    public Task<WorkspaceInfo> PrepareAsync(
        FlowRun flow,
        CancellationToken cancellationToken = default) =>
        PrepareCoreAsync(
            flow,
            suppressAfterCreateHook: false,
            cancellationToken);

    public Task<WorkspaceInfo> PrepareForInvocationAsync(
        FlowRun flow,
        ExecutionInvocationKind invocationKind,
        CancellationToken cancellationToken = default) =>
        PrepareCoreAsync(
            flow,
            suppressAfterCreateHook:
                !CopilotReasoningHost.ShouldRunWorkspaceHooks(
                    invocationKind),
            cancellationToken);

    private async Task<WorkspaceInfo> PrepareCoreAsync(
        FlowRun flow,
        bool suppressAfterCreateHook,
        CancellationToken cancellationToken)
    {
        var projectPath = Path.GetFullPath(flow.RepositoryPath);
        if (!Directory.Exists(projectPath))
        {
            throw new DirectoryNotFoundException(
                $"The selected project folder no longer exists: {projectPath}");
        }

        var repositories = RepositoryAnalyzer.FindGitRepositories(projectPath);
        var containingRepository = repositories.Count == 0
            ? RepositoryAnalyzer.FindContainingGitRepository(projectPath)
            : null;
        if (containingRepository is not null)
        {
            repositories = [containingRepository];
        }
        var unversioned = repositories.Count == 0;
        if (!ExecutableLocator.Exists("git"))
        {
            throw new InvalidOperationException(
                "Git is required for isolated live Copilot execution.");
        }
        var trustedRepositories = unversioned
            ? new WorkspaceRepositoryIdentity[] { new(".", string.Empty) }
            : await ReadTrustedRepositoriesAsync(
                flow, projectPath, repositories, cancellationToken);
        var baselines = new Dictionary<string, string>();
        foreach (var repository in repositories)
        {
            baselines.Add(
                repository,
                await FetchBaseBranchAsync(repository, cancellationToken));
        }
        var scopePath = containingRepository is null
            ? string.Empty
            : Path.GetRelativePath(containingRepository, projectPath)
                .Replace('\\', '/');
        var scopeCommit = string.Empty;
        if (containingRepository is not null)
        {
            var revision = await processRunner.RunAsync(
                "git",
                ["-C", containingRepository, "rev-parse", "--verify",
                    $"{baselines[containingRepository]}^{{commit}}"],
                containingRepository, TimeSpan.FromSeconds(20),
                cancellationToken);
            if (revision.ExitCode != 0 ||
                !System.Text.RegularExpressions.Regex.IsMatch(
                    revision.StandardOutput.Trim(),
                    @"\A[0-9a-fA-F]{40}([0-9a-fA-F]{24})?\z"))
            {
                throw new InvalidOperationException(
                    $"Unable to resolve the selected project's Git baseline: {revision.CombinedOutput}");
            }
            scopeCommit = revision.StandardOutput.Trim().ToLowerInvariant();
        }
        var authorizedWorkspaceRoot = Path.GetFullPath(
            workflowProvider.GetValidated().Config.Workspace.ResolvedRoot);
        Directory.CreateDirectory(authorizedWorkspaceRoot);
        WorkspacePathGuard.ValidateAuthorizedRoot(
            authorizedWorkspaceRoot,
            "Workspace preparation");
        var shortId = flow.Id.ToString("N")[..16];
        var workspacePath = ResolveContained(UnsafeCharacters().Replace(shortId, "_"));
        var requestedMode = ResolveMode(flow);
        if (!string.IsNullOrWhiteSpace(flow.WorkspacePath) &&
            !PathsEqual(workspacePath, flow.WorkspacePath))
        {
            throw new InvalidOperationException(
                $"Flow workspace is outside its expected isolated location: {flow.WorkspacePath}");
        }

        if (!string.IsNullOrWhiteSpace(flow.WorkspacePath) &&
            Directory.Exists(flow.WorkspacePath))
        {
            var persistedMode = await _advisoryArtifacts.GetWorkspaceModeAsync(
                flow.Id,
                cancellationToken);
            if (requestedMode == WorkspaceMode.Delivery &&
                persistedMode is
                    WorkspaceMode.ProvisionalReadOnly or
                    WorkspaceMode.AdvisoryReadOnly)
            {
                if (persistedMode == WorkspaceMode.AdvisoryReadOnly)
                {
                    throw new InvalidOperationException(
                        "A confirmed Advisory workspace cannot be changed into a Delivery worktree.");
                }
                var ownership =
                    await _advisoryArtifacts.InspectGuardedSnapshotAsync(
                        flow,
                        flow.WorkspacePath,
                        WorkspaceMode.ProvisionalReadOnly,
                        cancellationToken);
                if (ownership.Status !=
                    GuardedSnapshotOwnershipStatus.Matching)
                {
                    throw new IOException(
                        "The provisional workspace cannot be replaced because its guarded ownership metadata, marker, and baseline do not match.");
                }
                DeleteSnapshotSafely(flow.WorkspacePath);
                await _advisoryArtifacts.DeleteMetadataAsync(
                    flow.Id,
                    cancellationToken);
            }
            else
            {
                if (requestedMode != WorkspaceMode.Delivery &&
                    persistedMode == WorkspaceMode.Delivery)
                {
                    throw new InvalidOperationException(
                        "A Delivery worktree cannot be reused as a guarded read-only snapshot.");
                }
                var validatedWorkspace = WorkspacePathGuard.ValidateExistingRoot(
                    flow.WorkspacePath,
                    authorizedWorkspaceRoot,
                    "Workspace recovery");
                if (requestedMode != WorkspaceMode.Delivery)
                {
                    var evidence = await _advisoryArtifacts.EnsureBaselineAsync(
                        flow,
                        validatedWorkspace,
                        requestedMode,
                        cancellationToken);
                    return new WorkspaceInfo(
                        validatedWorkspace,
                        string.Empty,
                        CreatedNow: false,
                        trustedRepositories,
                        requestedMode,
                        evidence.BaselineDigest,
                        evidence.FileCount,
                        evidence.TotalBytes);
                }

                CopilotReasoningHost.GovernedGitIsolationScope.RecoverInterrupted(
                    validatedWorkspace);
                var recoveredBranchName =
                    string.IsNullOrWhiteSpace(flow.BranchName)
                        ? $"ai-harness/{Slug(flow.Title)}-{shortId}"
                        : flow.BranchName;
                await VerifyRecoveredDeliveryWorkspaceAsync(
                    projectPath,
                    repositories,
                    validatedWorkspace,
                    recoveredBranchName,
                    cancellationToken);
                await VerifyDeliveryBaselinesAsync(
                    projectPath, repositories, baselines, validatedWorkspace,
                    cancellationToken);
                return new WorkspaceInfo(
                    validatedWorkspace,
                    recoveredBranchName,
                    CreatedNow: false,
                    trustedRepositories,
                    WorkspaceMode.Delivery,
                    SourceScopeRelativePath: scopePath,
                    SourceBaselineCommit: scopeCommit);
            }
        }

        if (requestedMode != WorkspaceMode.Delivery)
        {
            if (Directory.Exists(workspacePath))
            {
                var ownership =
                    await _advisoryArtifacts.InspectGuardedSnapshotAsync(
                        flow,
                        workspacePath,
                        requestedMode,
                        cancellationToken);
                if (ownership.Status ==
                    GuardedSnapshotOwnershipStatus.NotOwned)
                {
                    throw new IOException(
                        $"Guarded workspace path already exists without matching flow ownership: {workspacePath}");
                }
                if (ownership.Status ==
                    GuardedSnapshotOwnershipStatus.Mismatch)
                {
                    DeleteSnapshotSafely(workspacePath);
                    await _advisoryArtifacts.DeleteMetadataAsync(
                        flow.Id,
                        cancellationToken);
                }
                else
                {
                    var adoptedEvidence = ownership.Evidence
                        ?? throw new InvalidOperationException(
                            "The matching guarded snapshot has no baseline evidence.");
                    logger.LogInformation(
                        "Adopted flow-owned guarded {WorkspaceMode} snapshot {WorkspacePath} for flow {FlowId}",
                        requestedMode,
                        workspacePath,
                        flow.Id);
                    return new WorkspaceInfo(
                        workspacePath,
                        string.Empty,
                        CreatedNow: false,
                        trustedRepositories,
                        requestedMode,
                        adoptedEvidence.BaselineDigest,
                        adoptedEvidence.FileCount,
                        adoptedEvidence.TotalBytes);
                }
            }
            await PrepareGuardedSnapshotAsync(
                flow,
                projectPath,
                repositories,
                baselines,
                workspacePath,
                authorizedWorkspaceRoot,
                requestedMode,
                cancellationToken);
            workspacePath = WorkspacePathGuard.ValidateExistingRoot(
                workspacePath,
                authorizedWorkspaceRoot,
                "Guarded workspace preparation");
            var evidence = await _advisoryArtifacts.EnsureBaselineAsync(
                flow,
                workspacePath,
                requestedMode,
                cancellationToken);
            logger.LogInformation(
                "Prepared guarded {WorkspaceMode} snapshot {WorkspacePath} for flow {FlowId}",
                requestedMode,
                workspacePath,
                flow.Id);
            return new WorkspaceInfo(
                workspacePath,
                string.Empty,
                CreatedNow: true,
                trustedRepositories,
                requestedMode,
                evidence.BaselineDigest,
                evidence.FileCount,
                evidence.TotalBytes);
        }

        var branchName = $"ai-harness/{Slug(flow.Title)}-{shortId}";

        bool createdNow;
        if (unversioned)
        {
            createdNow = await PrepareUnversionedDeliveryAsync(
                projectPath, workspacePath, branchName,
                authorizedWorkspaceRoot, cancellationToken);
        }
        else if (repositories.Count == 1 &&
                 (PathsEqual(projectPath, repositories[0]) ||
                  containingRepository is not null))
        {
            createdNow = await EnsureWorktreeAsync(
                repositories[0],
                workspacePath,
                branchName,
                baselines[repositories[0]],
                cancellationToken);
        }
        else
        {
            createdNow = await PrepareProjectWorkspaceAsync(
                projectPath,
                repositories,
                workspacePath,
                branchName,
                baselines,
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
        if (createdNow && !suppressAfterCreateHook)
        {
            await hookRunner.RunAsync(
                WorkspaceHookStage.AfterCreate,
                workspacePath,
                workflowProvider.GetEffective(),
                flow.Kind,
                provisional: false,
                cancellationToken: cancellationToken);
        }
        await _advisoryArtifacts.RecordDeliveryModeAsync(
            flow,
            workspacePath,
            cancellationToken);

        return new WorkspaceInfo(
            workspacePath,
            branchName,
            createdNow,
            trustedRepositories,
            WorkspaceMode.Delivery,
            SourceScopeRelativePath: scopePath,
            SourceBaselineCommit: scopeCommit);
    }

    private async Task VerifyRecoveredDeliveryWorkspaceAsync(
        string projectPath,
        IReadOnlyList<string> repositories,
        string workspacePath,
        string expectedBranchName,
        CancellationToken cancellationToken)
    {
        foreach (var repository in repositories)
        {
            var relativePath = Path.GetRelativePath(projectPath, repository);
            var repositoryWorkspace =
                repositories.Count == 1 &&
                (PathsEqual(projectPath, repository) ||
                 IsContainedOrEqual(repository, projectPath))
                    ? workspacePath
                    : ResolveUnderWorkspace(
                        workspacePath,
                        relativePath);
            if (!Directory.Exists(repositoryWorkspace))
            {
                throw new InvalidOperationException(
                    $"Recovered Delivery workspace is missing repository worktree '{repositoryWorkspace}'.");
            }
            var branch = await processRunner.RunAsync(
                "git",
                ["-C", repositoryWorkspace, "branch", "--show-current"],
                repositoryWorkspace,
                TimeSpan.FromSeconds(20),
                cancellationToken);
            if (branch.ExitCode != 0 ||
                !string.Equals(
                    branch.StandardOutput.Trim(),
                    expectedBranchName,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Recovered Delivery worktree '{repositoryWorkspace}' is not on the durable flow branch '{expectedBranchName}'.");
            }
        }
        if (repositories.Count == 0)
        {
            var branch = await processRunner.RunAsync(
                "git", ["-C", workspacePath, "branch", "--show-current"],
                workspacePath, TimeSpan.FromSeconds(20), cancellationToken);
            if (branch.ExitCode != 0 ||
                !string.Equals(
                    branch.StandardOutput.Trim(), expectedBranchName,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Recovered local Delivery workspace '{workspacePath}' is not on its durable flow branch.");
            }
        }
    }

    private async Task<string> FetchBaseBranchAsync(
        string repository,
        CancellationToken cancellationToken)
    {
        var remote = await processRunner.RunAsync(
            "git", ["-C", repository, "remote", "get-url", "origin"],
            repository, TimeSpan.FromSeconds(20), cancellationToken);
        if (remote.ExitCode != 0)
        {
            var remotes = await processRunner.RunAsync(
                "git", ["-C", repository, "remote"],
                repository, TimeSpan.FromSeconds(20), cancellationToken);
            if (remotes.ExitCode == 0 &&
                string.IsNullOrWhiteSpace(remotes.StandardOutput))
            {
                var current = await processRunner.RunAsync(
                    "git", ["-C", repository, "branch", "--show-current"],
                    repository, TimeSpan.FromSeconds(20), cancellationToken);
                if (current.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Unable to inspect the local base branch for '{repository}': {current.CombinedOutput}");
                }
                if (current.StandardOutput.Trim() is "main" or "master")
                {
                    return $"refs/heads/{current.StandardOutput.Trim()}";
                }
                foreach (var localBranch in new[] { "main", "master" })
                {
                    var probe = await processRunner.RunAsync(
                        "git",
                        ["-C", repository, "show-ref", "--verify", "--quiet",
                            $"refs/heads/{localBranch}"],
                        repository, TimeSpan.FromSeconds(20), cancellationToken);
                    if (probe.ExitCode == 0)
                    {
                        return $"refs/heads/{localBranch}";
                    }
                    if (probe.ExitCode != 1)
                    {
                        throw new InvalidOperationException(
                            $"Unable to inspect local '{localBranch}' in '{repository}': {probe.CombinedOutput}");
                    }
                }
                throw new InvalidOperationException(
                    $"Local repository '{repository}' has no main or master branch.");
            }
            throw new InvalidOperationException(
                $"Unable to inspect origin for '{repository}': {remote.CombinedOutput}");
        }

        var heads = await processRunner.RunAsync(
            "git",
            ["-C", repository, "ls-remote", "--symref", "origin",
                "HEAD", "refs/heads/main", "refs/heads/master"],
            repository, TimeSpan.FromMinutes(1), cancellationToken);
        if (heads.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to check the latest main/master branch for '{repository}': {heads.CombinedOutput}");
        }
        var lines = heads.StandardOutput.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var defaultBranch = lines
            .Where(line => line.StartsWith("ref: refs/heads/", StringComparison.Ordinal) &&
                           line.EndsWith("\tHEAD", StringComparison.Ordinal))
            .Select(line => line["ref: refs/heads/".Length..^"\tHEAD".Length])
            .FirstOrDefault();
        var branch = defaultBranch is "main" or "master"
            ? defaultBranch
            : lines.Any(line => line.EndsWith("\trefs/heads/main", StringComparison.Ordinal))
                ? "main"
                : lines.Any(line => line.EndsWith("\trefs/heads/master", StringComparison.Ordinal))
                    ? "master"
                    : throw new InvalidOperationException(
                        $"Origin for '{repository}' has neither a main nor a master branch: {heads.CombinedOutput}");
        var baseline = $"refs/remotes/origin/{branch}";
        var fetch = await processRunner.RunAsync(
            "git",
            ["-C", repository, "fetch", "--no-tags", "origin",
                $"+refs/heads/{branch}:{baseline}"],
            repository, TimeSpan.FromMinutes(2), cancellationToken);
        if (fetch.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to fetch the latest origin/{branch} for '{repository}': {fetch.CombinedOutput}");
        }
        return baseline;
    }

    private async Task VerifyDeliveryBaselinesAsync(
        string projectPath,
        IReadOnlyList<string> repositories,
        IReadOnlyDictionary<string, string> baselines,
        string workspacePath,
        CancellationToken cancellationToken)
    {
        foreach (var repository in repositories)
        {
            var worktree = repositories.Count == 1 &&
                           (PathsEqual(projectPath, repository) ||
                            IsContainedOrEqual(repository, projectPath))
                ? workspacePath
                : ResolveUnderWorkspace(
                    workspacePath, Path.GetRelativePath(projectPath, repository));
            await VerifyBaseAncestorAsync(
                repository, worktree, baselines[repository], cancellationToken);
        }
    }

    private async Task VerifyBaseAncestorAsync(
        string repository,
        string worktree,
        string baseline,
        CancellationToken cancellationToken,
        string target = "HEAD")
    {
        var ancestry = await processRunner.RunAsync(
            "git",
            ["-C", worktree, "merge-base", "--is-ancestor", baseline, target],
            worktree, TimeSpan.FromSeconds(20), cancellationToken);
        if (ancestry.ExitCode == 1)
        {
            throw new InvalidOperationException(
                $"Flow worktree '{worktree}' is behind {baseline} in '{repository}'. " +
                "Resolve the upstream changes explicitly before resuming; Studio will not merge or rebase an existing flow.");
        }
        if (ancestry.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to check upstream ancestry for '{worktree}': {ancestry.CombinedOutput}");
        }
    }

    private async Task<IReadOnlyList<WorkspaceRepositoryIdentity>>
        ReadTrustedRepositoriesAsync(
            FlowRun flow,
            string projectPath,
            IReadOnlyList<string> repositories,
            CancellationToken cancellationToken)
    {
        var result = new List<WorkspaceRepositoryIdentity>(repositories.Count);
        foreach (var repository in repositories)
        {
            var remoteRepository = await ReadConfiguredRemoteRepositoryAsync(
                repository,
                cancellationToken);
            result.Add(new WorkspaceRepositoryIdentity(
                IsContainedOrEqual(repository, projectPath)
                    ? "."
                    : NormalizeRepositoryPath(projectPath, repository),
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
        var persistedMode = await _advisoryArtifacts.GetWorkspaceModeAsync(
            flow.Id,
            cancellationToken);
        var guardedSnapshot =
            persistedMode is
                WorkspaceMode.ProvisionalReadOnly or WorkspaceMode.AdvisoryReadOnly ||
            persistedMode != WorkspaceMode.Delivery &&
            (flow.Kind == FlowKind.Advisory ||
             string.IsNullOrWhiteSpace(flow.BranchName));
        if (guardedSnapshot)
        {
            var diagnostics = new List<string>();
            if (Directory.Exists(expectedWorkspace))
            {
                var expectedMode = persistedMode is
                    WorkspaceMode.ProvisionalReadOnly or
                    WorkspaceMode.AdvisoryReadOnly
                        ? persistedMode.Value
                        : flow.Kind == FlowKind.Advisory
                            ? WorkspaceMode.AdvisoryReadOnly
                            : WorkspaceMode.ProvisionalReadOnly;
                var ownership =
                    await _advisoryArtifacts.InspectGuardedSnapshotAsync(
                        flow,
                        expectedWorkspace,
                        expectedMode,
                        cancellationToken);
                if (ownership.Status !=
                    GuardedSnapshotOwnershipStatus.Matching)
                {
                    diagnostics.Add(
                        ownership.Status ==
                        GuardedSnapshotOwnershipStatus.NotOwned
                            ? "Guarded workspace cleanup preserved a deterministic path collision because matching flow metadata and ownership marker evidence were absent."
                            : "Guarded workspace cleanup preserved the flow path because its current baseline no longer matched the guarded ownership journal.");
                    logger.LogWarning(
                        "Preserved guarded workspace path {WorkspacePath} after ownership validation returned {OwnershipStatus} while abandoning flow {FlowId}",
                        expectedWorkspace,
                        ownership.Status,
                        flow.Id);
                }
                else
                {
                    DeleteSnapshotSafely(expectedWorkspace);
                }
            }
            else if (File.Exists(expectedWorkspace))
            {
                diagnostics.Add(
                    "Guarded workspace cleanup preserved a deterministic path collision because the expected workspace path is an unrelated file.");
                logger.LogWarning(
                    "Preserved non-owned guarded workspace file collision {WorkspacePath} while abandoning flow {FlowId}",
                    expectedWorkspace,
                    flow.Id);
            }
            await _advisoryArtifacts.DeleteMetadataAsync(
                flow.Id,
                cancellationToken);
            if (diagnostics.Count == 0)
            {
                logger.LogInformation(
                    "Removed guarded read-only workspace {WorkspacePath} for flow {FlowId} without touching source Git metadata",
                    expectedWorkspace,
                    flow.Id);
            }
            return diagnostics.Count == 0
                ? WorkspaceCleanupResult.Empty
                : new WorkspaceCleanupResult(0, 0, 0, diagnostics);
        }

        var projectPath = Path.GetFullPath(flow.RepositoryPath);
        if (!Directory.Exists(projectPath))
        {
            if (Directory.Exists(expectedWorkspace))
            {
                Directory.Delete(expectedWorkspace, recursive: true);
            }
            await _advisoryArtifacts.DeleteMetadataAsync(
                flow.Id,
                cancellationToken);
            return WorkspaceCleanupResult.Empty;
        }
        var repositories = RepositoryAnalyzer.FindGitRepositories(projectPath);
        var containingRepository = repositories.Count == 0
            ? RepositoryAnalyzer.FindContainingGitRepository(projectPath)
            : null;
        if (containingRepository is not null)
        {
            repositories = [containingRepository];
        }
        if (repositories.Count == 0)
        {
            if (Directory.Exists(expectedWorkspace))
            {
                if (persistedMode != WorkspaceMode.Delivery ||
                    string.IsNullOrWhiteSpace(flow.WorkspacePath))
                {
                    return new WorkspaceCleanupResult(
                        0, 0, 0,
                        ["Preserved local workspace path without a durable Delivery ownership record."]);
                }
                var marker = Path.Combine(expectedWorkspace, ".git");
                if (!RepositoryAnalyzer.IsGitRepository(expectedWorkspace) ||
                    (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
                {
                    return new WorkspaceCleanupResult(
                        0, 0, 0,
                        ["Preserved local workspace path without an owned Git repository."]);
                }
                var branch = await processRunner.RunAsync(
                    "git", ["-C", expectedWorkspace, "branch", "--show-current"],
                    expectedWorkspace, TimeSpan.FromSeconds(20), cancellationToken);
                if (branch.ExitCode != 0 ||
                    !string.Equals(
                        branch.StandardOutput.Trim(), branchName,
                        StringComparison.Ordinal))
                {
                    return new WorkspaceCleanupResult(
                        0, 0, 0,
                        ["Preserved local workspace path not on the durable flow branch."]);
                }
                await hookRunner.RunAsync(
                    WorkspaceHookStage.BeforeRemove,
                    expectedWorkspace,
                    workflowProvider.GetEffective(),
                    flow.Kind,
                    provisional: false,
                    cancellationToken: cancellationToken);
                DeleteSnapshotSafely(expectedWorkspace);
            }
            await _advisoryArtifacts.DeleteMetadataAsync(
                flow.Id, cancellationToken);
            return WorkspaceCleanupResult.Empty;
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
                workflowProvider.GetEffective(),
                flow.Kind,
                provisional: false,
                cancellationToken: cancellationToken);
        }

        var worktreesRemoved = 0;
        var localBranchesDeleted = 0;
        var remoteBranchesDeleted = 0;
        foreach (var repository in repositories)
        {
            var relativePath = Path.GetRelativePath(projectPath, repository);
            var repositoryWorkspace =
                repositories.Count == 1 &&
                (PathsEqual(projectPath, repository) ||
                 IsContainedOrEqual(repository, projectPath))
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
        await _advisoryArtifacts.DeleteMetadataAsync(
            flow.Id,
            cancellationToken);
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

    private static WorkspaceMode ResolveMode(FlowRun flow)
    {
        if (flow.Status == FlowStatus.Intake)
        {
            return WorkspaceMode.ProvisionalReadOnly;
        }
        return flow.Kind == FlowKind.Advisory
            ? WorkspaceMode.AdvisoryReadOnly
            : WorkspaceMode.Delivery;
    }

    private async Task PrepareGuardedSnapshotAsync(
        FlowRun flow,
        string projectPath,
        IReadOnlyList<string> repositories,
        IReadOnlyDictionary<string, string> baselines,
        string workspacePath,
        string authorizedWorkspaceRoot,
        WorkspaceMode mode,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(workspacePath) || File.Exists(workspacePath))
        {
            throw new IOException(
                $"Guarded workspace path already exists: {workspacePath}");
        }

        var stagingPath = Path.Combine(
            authorizedWorkspaceRoot,
            $"{Path.GetFileName(workspacePath)}.snapshot-{Guid.NewGuid():N}");
        try
        {
            logger.LogInformation(
                "Preparing guarded {WorkspaceMode} snapshot {WorkspacePath} for flow {FlowId}",
                mode,
                workspacePath,
                flow.Id);
            if (repositories.Count == 0 ||
                !IsContainedOrEqual(repositories[0], projectPath) ||
                PathsEqual(repositories[0], projectPath))
            {
                CopyGuardedSource(
                    projectPath,
                    stagingPath,
                    authorizedWorkspaceRoot,
                    cancellationToken,
                    repositories);
            }
            foreach (var repository in repositories)
            {
                var relativeProjectPath = IsContainedOrEqual(repository, projectPath)
                    ? Path.GetRelativePath(repository, projectPath)
                    : ".";
                var snapshotSource = relativeProjectPath == "."
                    ? repository
                    : projectPath;
                var snapshotDestination =
                    relativeProjectPath != "." ||
                    repositories.Count == 1 && PathsEqual(projectPath, repository)
                        ? stagingPath
                        : ResolveUnderWorkspace(
                            stagingPath,
                            Path.GetRelativePath(projectPath, repository));
                if (baselines[repository].StartsWith("refs/heads/", StringComparison.Ordinal))
                {
                    var current = await processRunner.RunAsync(
                        "git", ["-C", repository, "branch", "--show-current"],
                        repository, TimeSpan.FromSeconds(20), cancellationToken);
                    if (current.ExitCode != 0)
                    {
                        throw new InvalidOperationException(
                            $"Unable to inspect local branch for '{repository}': {current.CombinedOutput}");
                    }
                    if (baselines[repository] ==
                        $"refs/heads/{current.StandardOutput.Trim()}")
                    {
                        CopyGuardedSource(
                            snapshotSource,
                            snapshotDestination,
                            authorizedWorkspaceRoot,
                            cancellationToken);
                        continue;
                    }
                }
                var temporaryWorktree = Path.Combine(
                    authorizedWorkspaceRoot,
                    $"{Path.GetFileName(workspacePath)}.source-{Guid.NewGuid():N}");
                var add = await processRunner.RunAsync(
                    "git",
                    ["-C", repository, "worktree", "add", "--detach",
                        temporaryWorktree, baselines[repository]],
                    repository, TimeSpan.FromMinutes(2), cancellationToken);
                if (add.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Unable to snapshot latest source for '{repository}': {add.CombinedOutput}");
                }
                try
                {
                    CopyGuardedSource(
                        relativeProjectPath == "."
                            ? temporaryWorktree
                            : Path.Combine(temporaryWorktree, relativeProjectPath),
                        snapshotDestination,
                        authorizedWorkspaceRoot,
                        cancellationToken);
                }
                finally
                {
                    var remove = await processRunner.RunAsync(
                        "git",
                        ["-C", repository, "worktree", "remove", "--force",
                            temporaryWorktree],
                        repository, TimeSpan.FromMinutes(2), CancellationToken.None);
                    if (remove.ExitCode != 0)
                    {
                        throw new InvalidOperationException(
                            $"Unable to remove temporary source worktree '{temporaryWorktree}': {remove.CombinedOutput}");
                    }
                }
            }
            _ = await _advisoryArtifacts.JournalGuardedSnapshotAsync(
                flow,
                stagingPath,
                workspacePath,
                mode,
                cancellationToken);
            if (guardedSnapshotFaultInjector is not null)
            {
                await guardedSnapshotFaultInjector.OnTransitionAsync(
                    GuardedSnapshotTransition.OwnershipJournaled,
                    workspacePath,
                    cancellationToken);
            }
            Directory.Move(stagingPath, workspacePath);
            if (guardedSnapshotFaultInjector is not null)
            {
                await guardedSnapshotFaultInjector.OnTransitionAsync(
                    GuardedSnapshotTransition.FinalDirectoryMoved,
                    workspacePath,
                    cancellationToken);
            }
        }
        finally
        {
            if (Directory.Exists(stagingPath))
            {
                DeleteSnapshotSafely(stagingPath);
            }
        }
    }

    private static void CopyGuardedSource(
        string projectPath,
        string destinationPath,
        string authorizedWorkspaceRoot,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? excludedRepositories = null)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var excludedRoots = new HashSet<string>(comparison)
        {
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(authorizedWorkspaceRoot)),
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(destinationPath))
        };
        if (excludedRepositories is not null)
        {
            foreach (var repository in excludedRepositories)
            {
                excludedRoots.Add(Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(repository)));
            }
        }
        if (excludedRoots.Contains(Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(projectPath))))
        {
            return;
        }
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((projectPath, destinationPath));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (source, destination) = pending.Pop();
            RejectReparse(source, "Advisory source directory");
            Directory.CreateDirectory(destination);

            foreach (var file in Directory.EnumerateFiles(source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectReparse(file, "Advisory source file");
                if (string.Equals(
                        Path.GetFileName(file),
                        ".git",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                File.Copy(
                    file,
                    Path.Combine(destination, Path.GetFileName(file)),
                    overwrite: false);
            }

            foreach (var directory in Directory.EnumerateDirectories(source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(directory));
                if (excludedRoots.Contains(fullPath) ||
                    RepositoryAnalyzer.ShouldIgnoreDirectory(fullPath) ||
                    string.Equals(
                        Path.GetFileName(fullPath),
                        ".git",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                RejectReparse(fullPath, "Advisory source directory");
                pending.Push((
                    fullPath,
                    Path.Combine(destination, Path.GetFileName(fullPath))));
            }
        }
    }

    private static void DeleteSnapshotSafely(string root)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(root));
        if (!directory.Exists)
        {
            return;
        }
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            directory.Delete();
            return;
        }

        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                entry.Delete();
            }
            else if (entry is DirectoryInfo childDirectory)
            {
                DeleteSnapshotSafely(childDirectory.FullName);
            }
            else
            {
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            }
        }
        directory.Attributes = FileAttributes.Directory;
        directory.Delete();
    }

    private static void RejectReparse(string path, string label)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"{label} cannot be a symbolic link, junction, or reparse point: {path}");
        }
    }

    private async Task<bool> PrepareProjectWorkspaceAsync(
        string projectPath,
        IReadOnlyList<string> repositories,
        string workspacePath,
        string branchName,
        IReadOnlyDictionary<string, string> baselines,
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
                baselines[repository],
                cancellationToken);
        }

        return createdNow;
    }

    private async Task<bool> PrepareUnversionedDeliveryAsync(
        string projectPath,
        string workspacePath,
        string branchName,
        string authorizedWorkspaceRoot,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(workspacePath) || File.Exists(workspacePath))
        {
            throw new IOException(
                $"Local Delivery workspace path already exists: {workspacePath}");
        }
        CopyGuardedSource(
            projectPath, workspacePath, authorizedWorkspaceRoot,
            cancellationToken);
        var init = await processRunner.RunAsync(
            "git", ["-C", workspacePath, "init", "-b", branchName],
            workspacePath, TimeSpan.FromSeconds(30), cancellationToken);
        if (init.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to initialize isolated local Delivery repository: {init.CombinedOutput}");
        }
        var add = await processRunner.RunAsync(
            "git", ["-C", workspacePath, "add", "--all"],
            workspacePath, TimeSpan.FromMinutes(2), cancellationToken);
        if (add.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to stage isolated local Delivery baseline: {add.CombinedOutput}");
        }
        var commit = await processRunner.RunAsync(
            "git",
            ["-C", workspacePath, "-c", "user.name=AI Harness Studio",
                "-c", "user.email=studio@example.invalid",
                "-c", "commit.gpgsign=false",
                "commit", "--allow-empty", "-m", "Capture source project baseline"],
            workspacePath, TimeSpan.FromMinutes(2), cancellationToken);
        if (commit.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to commit isolated local Delivery baseline: {commit.CombinedOutput}");
        }
        return true;
    }

    private async Task<bool> EnsureWorktreeAsync(
        string repositoryPath,
        string workspacePath,
        string branchName,
        string baseline,
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
                await VerifyBaseAncestorAsync(
                    repositoryPath, workspacePath, baseline, cancellationToken);
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
                baseline
            ];
        if (branchProbe.ExitCode is not (0 or 1))
        {
            throw new InvalidOperationException(
                $"Unable to inspect flow branch in '{repositoryPath}': {branchProbe.CombinedOutput}");
        }
        if (branchProbe.ExitCode == 0)
        {
            await VerifyBaseAncestorAsync(
                repositoryPath, repositoryPath, baseline, cancellationToken,
                $"refs/heads/{branchName}");
        }
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
        await VerifyBaseAncestorAsync(
            repositoryPath, workspacePath, baseline, cancellationToken);

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

    private static bool IsContainedOrEqual(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." ||
               !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith(
                   ".." + Path.DirectorySeparatorChar,
                   StringComparison.Ordinal) &&
               !relative.StartsWith(
                   ".." + Path.AltDirectorySeparatorChar,
                   StringComparison.Ordinal);
    }
}

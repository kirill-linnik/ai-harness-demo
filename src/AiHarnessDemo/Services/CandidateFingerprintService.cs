using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Verification;

namespace AiHarnessDemo.Services;

public sealed class CandidateValidationException(string message)
    : InvalidOperationException(message);

public sealed record LocalCandidateSealRepositoryResult(
    string RelativePath,
    bool Changed,
    string Head,
    string Tree);

public sealed record LocalCandidateSealResult(
    IReadOnlyList<LocalCandidateSealRepositoryResult> Repositories)
{
    public bool Changed => Repositories.Any(item => item.Changed);
}

public enum CandidateSealTransition
{
    Started,
    TreeWritten,
    CommitWritten,
    RefAdvanced,
    IndexReset
}

public interface ICandidateSealFaultInjector
{
    Task OnTransitionAsync(
        CandidateSealTransition transition,
        string repository,
        CancellationToken cancellationToken);
}

public sealed class CandidateFingerprintService(
    ProcessRunner processRunner,
    TimeProvider timeProvider,
    ICandidateSealFaultInjector? sealFaultInjector = null,
    WorkflowDefinitionProvider? workflowProvider = null)
{
    public const int MaximumPreviewFiles =
        CandidateManifest.MaximumPreviewArtifacts;
    public const long MaximumPreviewBytes = 100L * 1024 * 1024;
    private static readonly JsonSerializerOptions SealJournalJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling =
            System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    private static readonly string[] FingerprintDirectoryExclusions =
    [
        ".git",
        "bin",
        "obj",
        "node_modules",
        "dist",
        "build",
        "coverage",
        "TestResults",
        "test-results",
        "playwright-report",
        ".playwright-browsers",
        ".next",
        ".vite",
        ".vs",
        ".idea",
        ".venv",
        "venv",
        "__pycache__",
        "packages",
        "wwwroot"
    ];

    internal static IReadOnlyList<string> IgnoredTransientDirectories =>
        FingerprintDirectoryExclusions
            .Where(item => !string.Equals(
                item,
                ".git",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public async Task<OutcomeCandidateSnapshot> PrepareAsync(
        FlowRun flow,
        string acceptancePlanHash,
        Guid preparedByStepId,
        bool requiresPreview,
        CancellationToken cancellationToken = default)
    {
        var workspaceContext = ResolveWorkspaceContext(flow);
        if (!OutcomeVerificationRules.IsSha256(acceptancePlanHash))
        {
            throw new CandidateValidationException(
                "The candidate requires a valid acceptance-plan hash.");
        }
        if (preparedByStepId == Guid.Empty)
        {
            throw new CandidateValidationException(
                "The candidate requires its Release Engineer step ID.");
        }

        var (trustedScaffoldFiles, _) = await ValidateNonRepositoryFilesAsync(
            flow,
            workspaceContext.Workspace,
            workspaceContext.Repositories,
            cancellationToken);

        var repositoryEntries = new List<CandidateRepositoryManifest>();
        foreach (var repository in workspaceContext.Repositories)
        {
            using var sandbox = CandidateGitSandbox.Create();
            var relativePath = NormalizeRelativePath(
                workspaceContext.Workspace,
                repository);
            var trustedRepository = workspaceContext.TrustedRepositories.Single(item =>
                string.Equals(
                    item.RelativePath,
                    relativePath,
                    StringComparison.Ordinal));
            var identity = await ValidateRepositoryCleanAsync(
                repository,
                cancellationToken,
                sandbox);
            var head = identity.Head;
            var tree = identity.Tree;
            var liveRemoteRepository = await ReadRemoteRepositoryAsync(
                repository,
                cancellationToken,
                sandbox.EnvironmentVariables);
            if (!string.Equals(
                    liveRemoteRepository,
                    trustedRepository.RemoteRepository,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new CandidateValidationException(
                    $"Candidate repository '{relativePath}' origin does not match its trusted target.");
            }
            if (flow.Outcome == OutcomeType.PullRequest &&
                string.IsNullOrWhiteSpace(trustedRepository.RemoteRepository))
            {
                throw new CandidateValidationException(
                    $"Pull request candidate repository '{relativePath}' has no trusted GitHub target.");
            }
            repositoryEntries.Add(new CandidateRepositoryManifest(
                relativePath,
                head,
                tree,
                trustedRepository.RemoteRepository));
        }

        var previewEntries = await ReadPreviewArtifactsAsync(
            workspaceContext.Workspace,
            cancellationToken);
        if (requiresPreview &&
            new PreviewArtifactCatalog().Discover(flow).Count == 0)
        {
            throw new CandidateValidationException(
                "The accepted customer-visible criteria require a servable " +
                ".customer-preview\\<variant>\\index.html (or browser\\index.html) artifact.");
        }

        var manifest = new CandidateManifest(
            flow.Iteration,
            acceptancePlanHash,
            repositoryEntries
                .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
                .ToArray(),
            trustedScaffoldFiles,
            previewEntries
                .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
                .ToArray());
        return new OutcomeCandidateSnapshot(
            manifest,
            OutcomeVerificationRules.HashCandidateManifest(manifest),
            preparedByStepId,
            timeProvider.GetUtcNow());
    }

    public async Task<bool> IsCurrentAsync(
        FlowRun flow,
        OutcomeCandidateSnapshot expected,
        bool requiresPreview,
        CancellationToken cancellationToken = default)
    {
        var current = await PrepareAsync(
            flow,
            expected.Manifest.AcceptancePlanHash,
            expected.PreparedByStepId,
            requiresPreview,
            cancellationToken);
        return string.Equals(
            current.Fingerprint,
            expected.Fingerprint,
            StringComparison.Ordinal);
    }

    public async Task<LocalCandidateSealResult> SealAsync(
        FlowRun flow,
        CancellationToken cancellationToken = default)
    {
        var workspaceContext = ResolveWorkspaceContext(flow);
        _ = await ValidateNonRepositoryFilesAsync(
            flow,
            workspaceContext.Workspace,
            workspaceContext.Repositories,
            cancellationToken);

        var repositories = new List<LocalCandidateSealRepositoryResult>(
            workspaceContext.Repositories.Count);
        foreach (var repository in workspaceContext.Repositories)
        {
            using var sandbox = CandidateGitSandbox.Create();
            var currentBranch = await ReadCurrentBranchAsync(
                repository,
                cancellationToken,
                sandbox.EnvironmentVariables);
            var targetBranch = string.IsNullOrWhiteSpace(flow.BranchName)
                ? currentBranch
                : flow.BranchName;
            if (!string.Equals(
                    currentBranch,
                    targetBranch,
                    StringComparison.Ordinal))
            {
                throw new CandidateValidationException(
                    $"Candidate repository '{NormalizeRelativePath(workspaceContext.Workspace, repository)}' is not on the expected local flow branch '{targetBranch}'.");
            }
            await ValidateBranchNameAsync(
                repository,
                targetBranch,
                cancellationToken,
                sandbox.EnvironmentVariables);
            var recoveredChange = await RecoverPendingSealAsync(
                repository,
                targetBranch,
                sandbox,
                cancellationToken);
            await ValidateTrackedIndexFlagsAsync(
                repository,
                cancellationToken,
                sandbox.EnvironmentVariables);
            await ValidateIgnoredPathsAsync(
                repository,
                cancellationToken,
                sandbox.EnvironmentVariables);
            var head = await ReadGitIdentityAsync(
                repository,
                "HEAD",
                cancellationToken,
                sandbox.EnvironmentVariables);
            var headTree = await ReadGitIdentityAsync(
                repository,
                "HEAD^{tree}",
                cancellationToken,
                sandbox.EnvironmentVariables);

            var objectFormat = await ReadObjectFormatAsync(
                repository,
                cancellationToken,
                sandbox.EnvironmentVariables);
            var canonicalEnvironmentVariables =
                await BuildCanonicalBlobEnvironmentAsync(
                    repository,
                    objectFormat,
                    sandbox,
                    cancellationToken);
            var headEntries = (await ReadTrackedHeadEntriesAsync(
                    repository,
                    head,
                    cancellationToken,
                    sandbox.EnvironmentVariables))
                .ToDictionary(item => item.Path, StringComparer.Ordinal);
            foreach (var entry in headEntries.Values)
            {
                ValidateTrackedEntryShape(entry);
            }

            var worktreeEntries = await ReadSealedWorktreeEntriesAsync(
                workspaceContext.Repositories,
                repository,
                headEntries,
                objectFormat,
                cancellationToken,
                canonicalEnvironmentVariables);
            var removedPaths = headEntries.Keys
                .Except(worktreeEntries.Keys, StringComparer.Ordinal)
                .OrderByDescending(path => path.Length)
                .ThenByDescending(path => path, StringComparer.Ordinal)
                .ToArray();
            var changedEntries = worktreeEntries.Values
                .Where(entry =>
                    !headEntries.TryGetValue(entry.Path, out var tracked) ||
                    !string.Equals(tracked.Mode, entry.Mode, StringComparison.Ordinal) ||
                    !string.Equals(tracked.ObjectId, entry.ObjectId, StringComparison.Ordinal))
                .OrderBy(entry => entry.Path, StringComparer.Ordinal)
                .ToArray();

            if (removedPaths.Length == 0 &&
                changedEntries.Length == 0)
            {
                await ResetRealIndexAsync(
                    repository,
                    cancellationToken,
                    sandbox.EnvironmentVariables);
                repositories.Add(new LocalCandidateSealRepositoryResult(
                    NormalizeRelativePath(workspaceContext.Workspace, repository),
                    Changed: recoveredChange,
                    head,
                    headTree));
                continue;
            }

            var journal = new CandidateSealJournal
            {
                Repository = Path.GetFullPath(repository),
                BranchName = targetBranch,
                ExpectedHead = head,
                ExpectedTree = headTree,
                Phase = CandidateSealTransition.Started
            };
            await PersistSealJournalAsync(
                journal,
                cancellationToken);
            await NotifySealTransitionAsync(
                CandidateSealTransition.Started,
                repository,
                cancellationToken);
            await InitializeTemporaryIndexAsync(
                repository,
                head,
                cancellationToken,
                sandbox.TemporaryIndexEnvironmentVariables);
            foreach (var removedPath in removedPaths)
            {
                await RemoveFromTemporaryIndexAsync(
                    repository,
                    removedPath,
                    cancellationToken,
                    sandbox.TemporaryIndexEnvironmentVariables);
            }
            foreach (var entry in changedEntries)
            {
                if (!headEntries.TryGetValue(entry.Path, out var tracked) ||
                    !string.Equals(
                        tracked.ObjectId,
                        entry.ObjectId,
                        StringComparison.Ordinal))
                {
                    await PersistWorkingEntryObjectAsync(
                        repository,
                        entry,
                        cancellationToken,
                        sandbox,
                        canonicalEnvironmentVariables);
                }
                await StageWorkingEntryAsync(
                    repository,
                    entry,
                    cancellationToken,
                    sandbox.TemporaryIndexEnvironmentVariables);
            }

            var sealedTree = await WriteTreeAsync(
                repository,
                cancellationToken,
                sandbox.TemporaryIndexEnvironmentVariables);
            journal.SealedTree = sealedTree;
            journal.Phase = CandidateSealTransition.TreeWritten;
            await PersistSealJournalAsync(journal, cancellationToken);
            await NotifySealTransitionAsync(
                CandidateSealTransition.TreeWritten,
                repository,
                cancellationToken);
            if (string.Equals(
                    sealedTree,
                    headTree,
                    StringComparison.Ordinal))
            {
                await ResetRealIndexAsync(
                    repository,
                    cancellationToken,
                    sandbox.EnvironmentVariables);
                DeleteSealJournal(repository);
                repositories.Add(new LocalCandidateSealRepositoryResult(
                    NormalizeRelativePath(workspaceContext.Workspace, repository),
                    Changed: recoveredChange,
                    head,
                    headTree));
                continue;
            }

            var sealedHead = await CreateSealedCommitAsync(
                repository,
                sealedTree,
                head,
                cancellationToken,
                sandbox.CommitEnvironmentVariables);
            journal.SealedHead = sealedHead;
            journal.Phase = CandidateSealTransition.CommitWritten;
            await PersistSealJournalAsync(journal, cancellationToken);
            await NotifySealTransitionAsync(
                CandidateSealTransition.CommitWritten,
                repository,
                cancellationToken);
            await UpdateCurrentBranchAsync(
                repository,
                targetBranch,
                head,
                sealedHead,
                cancellationToken,
                sandbox.EnvironmentVariables);
            journal.Phase = CandidateSealTransition.RefAdvanced;
            await PersistSealJournalAsync(journal, cancellationToken);
            await NotifySealTransitionAsync(
                CandidateSealTransition.RefAdvanced,
                repository,
                cancellationToken);
            await ResetRealIndexAsync(
                repository,
                cancellationToken,
                sandbox.EnvironmentVariables);
            journal.Phase = CandidateSealTransition.IndexReset;
            await PersistSealJournalAsync(journal, cancellationToken);
            await NotifySealTransitionAsync(
                CandidateSealTransition.IndexReset,
                repository,
                cancellationToken);
            DeleteSealJournal(repository);
            repositories.Add(new LocalCandidateSealRepositoryResult(
                NormalizeRelativePath(workspaceContext.Workspace, repository),
                Changed: true,
                sealedHead,
                sealedTree));
        }

        return new LocalCandidateSealResult(repositories);
    }

    internal async Task<bool> RestoreAndValidateTrustedScaffoldAsync(
        FlowRun flow,
        Func<CancellationToken, Task> beforeRestore,
        CancellationToken cancellationToken = default)
    {
        var workspaceContext = ResolveWorkspaceContext(flow);
        var (_, restoredMissingFiles) =
            await ValidateNonRepositoryFilesAsync(
            flow,
            workspaceContext.Workspace,
            workspaceContext.Repositories,
            cancellationToken,
            restoreMissingTrustedFiles: true,
            beforeRestore);
        return restoredMissingFiles;
    }

    private async Task<bool> RecoverPendingSealAsync(
        string repository,
        string branchName,
        CandidateGitSandbox sandbox,
        CancellationToken cancellationToken)
    {
        var journal = await ReadSealJournalAsync(repository, cancellationToken);
        if (journal is null)
        {
            return false;
        }
        if (!PathsEqual(journal.Repository, repository) ||
            !string.Equals(journal.BranchName, branchName, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(journal.ExpectedHead) ||
            string.IsNullOrWhiteSpace(journal.ExpectedTree) ||
            !Enum.IsDefined(journal.Phase))
        {
            throw new CandidateValidationException(
                $"Candidate seal journal for '{repository}' is invalid.");
        }

        if (journal.Phase == CandidateSealTransition.Started)
        {
            DeleteSealJournal(repository);
            return false;
        }
        if (string.IsNullOrWhiteSpace(journal.SealedTree))
        {
            throw new CandidateValidationException(
                $"Candidate seal journal for '{repository}' has no sealed tree.");
        }

        if (journal.Phase == CandidateSealTransition.TreeWritten)
        {
            var currentHead = await ReadGitIdentityAsync(
                repository,
                "HEAD",
                cancellationToken,
                sandbox.EnvironmentVariables);
            if (!string.Equals(
                    currentHead,
                    journal.ExpectedHead,
                    StringComparison.Ordinal))
            {
                throw SealJournalHeadMismatch(repository, journal, currentHead);
            }
            journal.SealedHead = await CreateSealedCommitAsync(
                repository,
                journal.SealedTree,
                journal.ExpectedHead,
                cancellationToken,
                sandbox.CommitEnvironmentVariables);
            journal.Phase = CandidateSealTransition.CommitWritten;
            await PersistSealJournalAsync(journal, cancellationToken);
            await NotifySealTransitionAsync(
                CandidateSealTransition.CommitWritten,
                repository,
                cancellationToken);
        }
        if (string.IsNullOrWhiteSpace(journal.SealedHead))
        {
            throw new CandidateValidationException(
                $"Candidate seal journal for '{repository}' has no sealed commit.");
        }

        if (journal.Phase == CandidateSealTransition.CommitWritten)
        {
            var currentHead = await ReadGitIdentityAsync(
                repository,
                "HEAD",
                cancellationToken,
                sandbox.EnvironmentVariables);
            if (string.Equals(
                    currentHead,
                    journal.ExpectedHead,
                    StringComparison.Ordinal))
            {
                await UpdateCurrentBranchAsync(
                    repository,
                    branchName,
                    journal.ExpectedHead,
                    journal.SealedHead,
                    cancellationToken,
                    sandbox.EnvironmentVariables);
            }
            else if (!string.Equals(
                         currentHead,
                         journal.SealedHead,
                         StringComparison.Ordinal))
            {
                throw SealJournalHeadMismatch(repository, journal, currentHead);
            }
            journal.Phase = CandidateSealTransition.RefAdvanced;
            await PersistSealJournalAsync(journal, cancellationToken);
            await NotifySealTransitionAsync(
                CandidateSealTransition.RefAdvanced,
                repository,
                cancellationToken);
        }

        if (journal.Phase == CandidateSealTransition.RefAdvanced)
        {
            var currentHead = await ReadGitIdentityAsync(
                repository,
                "HEAD",
                cancellationToken,
                sandbox.EnvironmentVariables);
            if (!string.Equals(
                    currentHead,
                    journal.SealedHead,
                    StringComparison.Ordinal))
            {
                throw SealJournalHeadMismatch(repository, journal, currentHead);
            }
            await ResetRealIndexAsync(
                repository,
                cancellationToken,
                sandbox.EnvironmentVariables);
            journal.Phase = CandidateSealTransition.IndexReset;
            await PersistSealJournalAsync(journal, cancellationToken);
            await NotifySealTransitionAsync(
                CandidateSealTransition.IndexReset,
                repository,
                cancellationToken);
        }

        DeleteSealJournal(repository);
        return true;
    }

    private static CandidateValidationException SealJournalHeadMismatch(
        string repository,
        CandidateSealJournal journal,
        string currentHead) =>
        new(
            $"Candidate seal recovery for '{repository}' expected HEAD " +
            $"'{journal.ExpectedHead}' or '{journal.SealedHead}', but found " +
            $"'{currentHead}'. The journal was retained for deterministic recovery.");

    private async Task NotifySealTransitionAsync(
        CandidateSealTransition transition,
        string repository,
        CancellationToken cancellationToken)
    {
        if (sealFaultInjector is not null)
        {
            await sealFaultInjector.OnTransitionAsync(
                transition,
                repository,
                cancellationToken);
        }
    }

    private static async Task<CandidateSealJournal?> ReadSealJournalAsync(
        string repository,
        CancellationToken cancellationToken)
    {
        var path = GetSealJournalPath(repository);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            await using var stream = File.OpenRead(path);
            var journal = await JsonSerializer.DeserializeAsync<CandidateSealJournal>(
                stream,
                SealJournalJsonOptions,
                cancellationToken: cancellationToken);
            if (journal is null)
            {
                throw new CandidateValidationException(
                    $"Candidate seal journal for '{repository}' is empty.");
            }
            return journal;
        }
        catch (JsonException exception)
        {
            throw new CandidateValidationException(
                $"Candidate seal journal for '{repository}' is corrupt: {exception.Message}");
        }
    }

    private static async Task PersistSealJournalAsync(
        CandidateSealJournal journal,
        CancellationToken cancellationToken)
    {
        var path = GetSealJournalPath(journal.Repository);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    journal,
                    SealJournalJsonOptions,
                    cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal static string GetSealJournalPath(string repository)
    {
        return Path.Combine(
            ResolveRepositoryGitDirectory(repository),
            "ai-harness",
            "candidate-seal.json");
    }

    private static void DeleteSealJournal(string repository)
    {
        var path = GetSealJournalPath(repository);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory) &&
            !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }
    }

    internal string ValidateWorkspaceRoot(FlowRun flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (string.IsNullOrWhiteSpace(flow.WorkspacePath))
        {
            throw new CandidateValidationException(
                "The flow has no isolated workspace to fingerprint.");
        }

        try
        {
            return WorkspacePathGuard.ValidateExistingRoot(
                flow.WorkspacePath,
                workflowProvider?.GetValidated().Config.Workspace.ResolvedRoot,
                "Candidate");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                DirectoryNotFoundException or
                UnauthorizedAccessException or
                IOException)
        {
            throw new CandidateValidationException(exception.Message);
        }
    }

    private CandidateWorkspaceContext ResolveWorkspaceContext(FlowRun flow)
    {
        var workspace = ValidateWorkspaceRoot(flow);
        CopilotReasoningHost.GovernedGitIsolationScope.RecoverInterrupted(
            workspace);
        ValidateLinksStayInside(workspace);
        var repositories = DiscoverRepositories(workspace);
        if (repositories.Count == 0)
        {
            throw new CandidateValidationException(
                "The flow workspace contains no Git repositories.");
        }

        var trustedRepositories = ReadTrustedRepositories(flow);
        var discoveredPaths = repositories
            .Select(repository => NormalizeRelativePath(workspace, repository))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var trustedPaths = trustedRepositories
            .Select(repository => repository.RelativePath)
            .ToArray();
        if (!discoveredPaths.SequenceEqual(
                trustedPaths,
                StringComparer.Ordinal))
        {
            throw new CandidateValidationException(
                "The candidate repository set does not match the trusted workspace mapping.");
        }

        return new CandidateWorkspaceContext(
            workspace,
            repositories,
            trustedRepositories);
    }

    internal static IReadOnlyList<string> DiscoverRepositories(string workspacePath)
    {
        var root = Path.GetFullPath(workspacePath);
        var repositories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (RepositoryAnalyzer.IsGitRepository(current))
            {
                repositories.Add(current);
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(current).ToArray();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw new CandidateValidationException(
                    $"Unable to inspect candidate directory '{current}': {exception.Message}");
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (IsLink(child))
                {
                    continue;
                }
                if (string.Equals(
                        name,
                        ".git",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                pending.Push(child);
            }
        }

        return repositories
            .Distinct(PathComparer)
            .OrderBy(
                path => NormalizeRelativePath(root, path),
                StringComparer.Ordinal)
            .ToArray();
    }

    internal static void ValidateLinksStayInside(string workspacePath)
    {
        var root = Path.GetFullPath(workspacePath);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current).ToArray();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw new CandidateValidationException(
                    $"Unable to inspect candidate path '{current}': {exception.Message}");
            }

            foreach (var entry in entries)
            {
                if (IsLink(entry))
                {
                    FileSystemInfo info = Directory.Exists(entry)
                        ? new DirectoryInfo(entry)
                        : new FileInfo(entry);
                    FileSystemInfo? target;
                    try
                    {
                        target = info.ResolveLinkTarget(returnFinalTarget: true);
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException)
                    {
                        throw new CandidateValidationException(
                            $"Unable to resolve candidate link '{entry}': {exception.Message}");
                    }
                    if (target is null || !IsContained(root, target.FullName))
                    {
                        throw new CandidateValidationException(
                            $"Candidate link escapes the flow workspace: {entry}");
                    }
                    continue;
                }
                if (string.Equals(
                        Path.GetFileName(entry),
                        ".git",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private async Task<RepositoryIdentity> ValidateRepositoryCleanAsync(
        string repository,
        CancellationToken cancellationToken,
        CandidateGitSandbox sandbox)
    {
        var environmentVariables = sandbox.EnvironmentVariables;
        var head = await ReadGitIdentityAsync(
            repository,
            "HEAD",
            cancellationToken,
            environmentVariables);
        var tree = await ReadGitIdentityAsync(
            repository,
            "HEAD^{tree}",
            cancellationToken,
            environmentVariables);
        var objectFormat = await ReadObjectFormatAsync(
            repository,
            cancellationToken,
            environmentVariables);
        var canonicalEnvironmentVariables =
            await BuildCanonicalBlobEnvironmentAsync(
                repository,
                objectFormat,
                sandbox,
                cancellationToken);
        await ValidateTrackedWorktreeMatchesHeadAsync(
            repository,
            head,
            tree,
            objectFormat,
            cancellationToken,
            environmentVariables,
            canonicalEnvironmentVariables);
        var trackedEntries = (await ReadTrackedHeadEntriesAsync(
                repository,
                head,
                cancellationToken,
                environmentVariables))
            .ToDictionary(item => item.Path, StringComparer.Ordinal);
        var ignoredPaths = await ReadIgnoredPathsAsync(
            repository,
            cancellationToken,
            environmentVariables);
        var worktreePaths = EnumerateRepositoryWorktreePaths(repository);
        foreach (var path in worktreePaths.Order(StringComparer.Ordinal))
        {
            if (trackedEntries.ContainsKey(path) ||
                IsApprovedGeneratedPath(path))
            {
                continue;
            }

            if (TryMatchIgnoredPath(path, ignoredPaths, out var ignoredPath))
            {
                if (!IsApprovedIgnoredPath(repository, ignoredPath))
                {
                    throw new CandidateValidationException(
                        $"Candidate repository has an ignored product or configuration path: {ignoredPath}");
                }
                continue;
            }

            throw new CandidateValidationException(
                $"Candidate repository has an untracked product file: {path}");
        }

        return new RepositoryIdentity(head, tree);
    }

    private async Task ValidateTrackedWorktreeMatchesHeadAsync(
        string repository,
        string head,
        string tree,
        string objectFormat,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environmentVariables,
        IReadOnlyDictionary<string, string?> canonicalEnvironmentVariables)
    {
        var entries = await ReadTrackedHeadEntriesAsync(
            repository,
            head,
            cancellationToken,
            environmentVariables);
        if (entries.Count == 0)
        {
            return;
        }

        var actualTree = await ReadGitIdentityAsync(
            repository,
            "HEAD^{tree}",
            cancellationToken,
            environmentVariables);
        if (!string.Equals(actualTree, tree, StringComparison.Ordinal))
        {
            throw new CandidateValidationException(
                $"Tracked worktree verification for '{repository}' observed HEAD tree drift.");
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ValidateTrackedEntryAgainstHeadAsync(
                repository,
                entry,
                objectFormat,
                cancellationToken,
                canonicalEnvironmentVariables);
        }
        await ValidateTrackedIndexFlagsAsync(
            repository,
            cancellationToken,
            environmentVariables);
    }

    private async Task ValidateTrackedIndexFlagsAsync(
        string repository,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var debug = await RunGitAsync(
            repository,
            ["ls-files", "--debug", "-z"],
            TimeSpan.FromSeconds(30),
            cancellationToken,
            environmentVariables);
        if (debug.ExitCode != 0)
        {
            throw new CandidateValidationException(
                $"Unable to inspect Git index flags at '{repository}': {debug.CombinedOutput}");
        }

        foreach (var entry in ParseTrackedIndexDebugEntries(debug.StandardOutput))
        {
            if (!string.Equals(entry.Flags, "0", StringComparison.Ordinal))
            {
                throw new CandidateValidationException(
                    $"Candidate repository has non-default Git index flags on tracked path '{entry.Path}': {entry.Flags}");
            }
        }

        var head = await ReadGitIdentityAsync(
            repository,
            "HEAD",
            cancellationToken,
            environmentVariables);
        var headEntries = (await ReadTrackedHeadEntriesAsync(
                repository,
                head,
                cancellationToken,
                environmentVariables))
            .ToDictionary(item => item.Path, StringComparer.Ordinal);
        var indexEntries = (await ReadTrackedIndexStageEntriesAsync(
                repository,
                cancellationToken,
                environmentVariables))
            .ToDictionary(item => item.Path, StringComparer.Ordinal);
        if (!headEntries.Keys.SequenceEqual(
                indexEntries.Keys,
                StringComparer.Ordinal))
        {
            throw new CandidateValidationException(
                $"Candidate repository index paths differ from HEAD at '{repository}'.");
        }
        foreach (var (path, expected) in headEntries)
        {
            var actual = indexEntries[path];
            if (!string.Equals(
                    expected.Mode,
                    actual.Mode,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    expected.ObjectId,
                    actual.ObjectId,
                    StringComparison.Ordinal))
            {
                throw new CandidateValidationException(
                    $"Candidate repository index metadata differs from HEAD for tracked path '{path}'.");
            }
        }
    }

    private async Task<IReadOnlyList<TrackedIndexStageEntry>>
        ReadTrackedIndexStageEntriesAsync(
            string repository,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var result = await RunGitAsync(
            repository,
            ["ls-files", "--stage", "-z"],
            TimeSpan.FromSeconds(30),
            cancellationToken,
            environmentVariables);
        if (result.ExitCode != 0)
        {
            throw new CandidateValidationException(
                $"Unable to inspect staged Git index metadata at '{repository}': {result.CombinedOutput}");
        }

        var entries = new List<TrackedIndexStageEntry>();
        foreach (var rawEntry in result.StandardOutput.Split(
                     '\0',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(rawEntry))
            {
                continue;
            }
            var separatorIndex = rawEntry.IndexOf('\t');
            var metadata = separatorIndex > 0
                ? rawEntry[..separatorIndex].Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries)
                : [];
            if (metadata.Length != 3 ||
                !string.Equals(metadata[2], "0", StringComparison.Ordinal))
            {
                var diagnostic = rawEntry
                    .Replace("\r", "\\r", StringComparison.Ordinal)
                    .Replace("\n", "\\n", StringComparison.Ordinal);
                throw new CandidateValidationException(
                    $"Candidate repository has an unsupported staged index entry at '{repository}': " +
                    diagnostic[..Math.Min(diagnostic.Length, 160)]);
            }
            entries.Add(new TrackedIndexStageEntry(
                rawEntry[(separatorIndex + 1)..]
                    .TrimEnd('\r', '\n')
                    .Replace('\\', '/'),
                metadata[0],
                metadata[1].ToLowerInvariant()));
        }
        return entries
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task ValidateTrackedEntryAgainstHeadAsync(
        string repository,
        TrackedHeadEntry entry,
        string objectFormat,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?> canonicalEnvironmentVariables)
    {
        ValidateTrackedEntryShape(entry);

        var fullPath = Path.GetFullPath(Path.Combine(
            repository,
            entry.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsContainedOrEqual(repository, fullPath))
        {
            throw new CandidateValidationException(
                $"Tracked entry path escaped the repository root: {entry.Path}");
        }
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new CandidateValidationException(
                $"Candidate repository is missing tracked worktree entry '{entry.Path}' from HEAD.");
        }

        if (entry.Mode == "120000")
        {
            ValidateTrackedSymlinkAgainstHead(
                fullPath,
                entry,
                objectFormat);
            return;
        }

        if (IsLink(fullPath) || Directory.Exists(fullPath))
        {
            throw new CandidateValidationException(
                $"Candidate repository changed the tracked entry type for '{entry.Path}'.");
        }

        var actualMode = ReadTrackedRegularFileMode(
            fullPath,
            entry.Mode);
        if (!string.Equals(actualMode, entry.Mode, StringComparison.Ordinal))
        {
            throw new CandidateValidationException(
                $"Candidate repository changed the tracked file mode for '{entry.Path}' (expected {entry.Mode}, found {actualMode}).");
        }

        var actualObjectId = await HashGitBlobFromWorktreeAsync(
            repository,
            entry.Path,
            fullPath,
            cancellationToken,
            canonicalEnvironmentVariables);
        if (!string.Equals(
                actualObjectId,
                entry.ObjectId,
                StringComparison.Ordinal))
        {
            throw new CandidateValidationException(
                $"Candidate repository changed the tracked bytes for '{entry.Path}' without updating HEAD.");
        }
    }

    private void ValidateTrackedSymlinkAgainstHead(
        string path,
        TrackedHeadEntry entry,
        string objectFormat)
    {
        if (!IsLink(path))
        {
            throw new CandidateValidationException(
                $"Candidate repository changed the tracked entry type for '{entry.Path}'.");
        }

        FileSystemInfo info = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        var linkTarget = info.LinkTarget;
        if (string.IsNullOrEmpty(linkTarget))
        {
            throw new CandidateValidationException(
                $"Candidate repository could not read the tracked symlink target for '{entry.Path}'.");
        }

        var actualObjectId = HashGitBlobBytes(
            objectFormat,
            Encoding.UTF8.GetBytes(linkTarget));
        if (!string.Equals(
                actualObjectId,
                entry.ObjectId,
                StringComparison.Ordinal))
        {
            throw new CandidateValidationException(
                $"Candidate repository changed the tracked symlink target for '{entry.Path}' without updating HEAD.");
        }
    }

    private async Task<string> ReadObjectFormatAsync(
        string repository,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var result = await RunGitAsync(
            repository,
            ["rev-parse", "--show-object-format"],
            TimeSpan.FromSeconds(20),
            cancellationToken,
            environmentVariables);
        var value = result.StandardOutput.Trim().ToLowerInvariant();
        if (result.ExitCode != 0 ||
            value is not ("sha1" or "sha256"))
        {
            throw new CandidateValidationException(
                $"Unable to read the Git object format for '{repository}': {result.CombinedOutput}");
        }
        return value;
    }

    private async Task<IReadOnlyList<TrackedHeadEntry>> ReadTrackedHeadEntriesAsync(
        string repository,
        string head,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var result = await RunGitAsync(
            repository,
            ["ls-tree", "-r", "-z", "--full-tree", head],
            TimeSpan.FromSeconds(30),
            cancellationToken,
            environmentVariables);
        if (result.ExitCode != 0)
        {
            throw new CandidateValidationException(
                $"Unable to enumerate tracked HEAD entries for '{repository}': {result.CombinedOutput}");
        }

        var entries = new List<TrackedHeadEntry>();
        foreach (var rawEntry in result.StandardOutput.Split(
                     '\0',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = rawEntry.Trim('\r', '\n');
            if (entry.Length == 0)
            {
                continue;
            }

            var separatorIndex = entry.IndexOf('\t');
            if (separatorIndex <= 0)
            {
                throw new CandidateValidationException(
                    $"Git returned an invalid tracked-tree entry for '{repository}'.");
            }

            var metadata = entry[..separatorIndex].Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries);
            if (metadata.Length != 3)
            {
                throw new CandidateValidationException(
                    $"Git returned an invalid tracked-tree entry for '{repository}'.");
            }

            entries.Add(new TrackedHeadEntry(
                metadata[0],
                metadata[1],
                metadata[2].ToLowerInvariant(),
                entry[(separatorIndex + 1)..].Replace('\\', '/')));
        }
        return entries;
    }

    private static IReadOnlyList<TrackedIndexDebugEntry> ParseTrackedIndexDebugEntries(
        string output)
    {
        var entries = new List<TrackedIndexDebugEntry>();
        var index = 0;
        while (index < output.Length)
        {
            var separatorIndex = output.IndexOf('\0', index);
            if (separatorIndex < 0)
            {
                break;
            }

            var path = output[index..separatorIndex].Replace('\\', '/');
            var debugStart = separatorIndex + 1;
            var lineEnd = debugStart;
            for (var line = 0; line < 5; line++)
            {
                lineEnd = output.IndexOf('\n', lineEnd);
                if (lineEnd < 0)
                {
                    throw new CandidateValidationException(
                        $"Git returned an invalid tracked-index record for '{path}'.");
                }
                lineEnd++;
            }

            var debugBlock = output[debugStart..lineEnd];
            var flagsLine = debugBlock
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.Contains("flags:", StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(flagsLine))
            {
                throw new CandidateValidationException(
                    $"Git returned an invalid tracked-index record for '{path}'.");
            }

            var flags = flagsLine[(flagsLine.IndexOf("flags:", StringComparison.Ordinal) + "flags:".Length)..]
                .Trim();
            entries.Add(new TrackedIndexDebugEntry(path, flags));
            index = lineEnd;
        }
        return entries;
    }

    private static string ReadTrackedRegularFileMode(
        string path,
        string? trackedMode = null)
    {
        if (OperatingSystem.IsWindows())
        {
            return trackedMode is "100644" or "100755"
                ? trackedMode
                : "100644";
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute |
                            UnixFileMode.GroupExecute |
                            UnixFileMode.OtherExecute)) != 0
                ? "100755"
                : "100644";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new CandidateValidationException(
                $"Unable to inspect the tracked file mode for '{path}': {exception.Message}");
        }
    }

    private static string HashGitBlobBytes(
        string objectFormat,
        ReadOnlySpan<byte> bytes)
    {
        using var hasher = CreateGitHasher(objectFormat);
        hasher.AppendData(Encoding.UTF8.GetBytes($"blob {bytes.Length}\0"));
        hasher.AppendData(bytes);
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    private static GitIncrementalHasher CreateGitHasher(string objectFormat) =>
        objectFormat switch
        {
            "sha1" => new GitIncrementalHasher(HashAlgorithmName.SHA1),
            "sha256" => new GitIncrementalHasher(HashAlgorithmName.SHA256),
            _ => throw new CandidateValidationException(
                $"Unsupported Git object format '{objectFormat}'.")
        };

    private async Task<IReadOnlyDictionary<string, WorkingTreeEntry>>
        ReadSealedWorktreeEntriesAsync(
            IReadOnlyCollection<string> workspaceRepositories,
            string repository,
            IReadOnlyDictionary<string, TrackedHeadEntry> headEntries,
            string objectFormat,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string?> canonicalEnvironmentVariables)
    {
        var root = Path.GetFullPath(repository);
        var result = new Dictionary<string, WorkingTreeEntry>(StringComparer.Ordinal);
        var repositoryRoots = workspaceRepositories
            .Select(Path.GetFullPath)
            .Where(path => !PathsEqual(path, root))
            .ToArray();
        var trackedPrefixes = headEntries.Keys
            .Select(path => path + "/")
            .ToArray();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current).ToArray();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw new CandidateValidationException(
                    $"Unable to inspect repository worktree '{current}': {exception.Message}");
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(
                        Path.GetFileName(entry),
                        ".git",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (repositoryRoots.Any(other => IsContainedOrEqual(other, entry)))
                {
                    continue;
                }

                var relativePath = NormalizeRelativePath(root, entry);
                if (IsLink(entry))
                {
                    if (ShouldExcludeWorktreeEntry(
                            repository,
                            relativePath,
                            headEntries))
                    {
                        continue;
                    }

                    result[relativePath] = ReadLinkEntry(
                        entry,
                        relativePath,
                        objectFormat);
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    if (ShouldSkipDirectory(
                            repository,
                            relativePath,
                            trackedPrefixes))
                    {
                        continue;
                    }
                    pending.Push(entry);
                    continue;
                }

                if (ShouldExcludeWorktreeEntry(
                        repository,
                        relativePath,
                        headEntries))
                {
                    continue;
                }

                headEntries.TryGetValue(relativePath, out var trackedEntry);
                result[relativePath] = await ReadRegularFileEntryAsync(
                    repository,
                    entry,
                    relativePath,
                    trackedEntry?.Mode,
                    cancellationToken,
                    canonicalEnvironmentVariables);
            }
        }

        return result;
    }

    private static bool ShouldExcludeWorktreeEntry(
        string repository,
        string relativePath,
        IReadOnlyDictionary<string, TrackedHeadEntry> headEntries)
    {
        if (IsApprovedGeneratedPath(relativePath))
        {
            return true;
        }

        return !headEntries.ContainsKey(relativePath) &&
               IsApprovedIgnoredPath(repository, relativePath);
    }

    private static bool ShouldSkipDirectory(
        string repository,
        string relativePath,
        IReadOnlyList<string> trackedPrefixes)
    {
        if (IsApprovedGeneratedPath(relativePath))
        {
            return true;
        }

        var prefix = relativePath.TrimEnd('/') + "/";
        var hasTrackedChildren = trackedPrefixes.Any(path =>
            path.StartsWith(prefix, StringComparison.Ordinal));
        return !hasTrackedChildren &&
               IsApprovedIgnoredPath(repository, relativePath + "/");
    }

    private static WorkingTreeEntry ReadLinkEntry(
        string fullPath,
        string relativePath,
        string objectFormat)
    {
        FileSystemInfo info = Directory.Exists(fullPath)
            ? new DirectoryInfo(fullPath)
            : new FileInfo(fullPath);
        var linkTarget = info.LinkTarget;
        if (string.IsNullOrEmpty(linkTarget))
        {
            throw new CandidateValidationException(
                $"Unable to read candidate symlink target for '{relativePath}'.");
        }

        return new WorkingTreeEntry(
            relativePath,
            fullPath,
            "120000",
            HashGitBlobBytes(
                objectFormat,
                Encoding.UTF8.GetBytes(linkTarget)),
            linkTarget);
    }

    private async Task<WorkingTreeEntry> ReadRegularFileEntryAsync(
        string repository,
        string fullPath,
        string relativePath,
        string? trackedMode,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?> canonicalEnvironmentVariables) =>
        new(
            relativePath,
            fullPath,
            ReadTrackedRegularFileMode(fullPath, trackedMode),
            await HashGitBlobFromWorktreeAsync(
                repository,
                relativePath,
                fullPath,
                cancellationToken,
                canonicalEnvironmentVariables),
            LinkTarget: null);

    private static IReadOnlySet<string> EnumerateRepositoryWorktreePaths(string repository)
    {
        var root = Path.GetFullPath(repository);
        var nestedRepositoryRoots = DiscoverRepositories(root)
            .Where(path => !PathsEqual(path, root))
            .Select(Path.GetFullPath)
            .ToArray();
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current).ToArray();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw new CandidateValidationException(
                    $"Unable to inspect repository worktree '{current}': {exception.Message}");
            }

            foreach (var entry in entries)
            {
                if (string.Equals(
                        Path.GetFileName(entry),
                        ".git",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (nestedRepositoryRoots.Any(other => IsContainedOrEqual(other, entry)))
                {
                    continue;
                }

                if (IsLink(entry))
                {
                    result.Add(NormalizeRelativePath(root, entry));
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                    continue;
                }

                result.Add(NormalizeRelativePath(root, entry));
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<string>> ReadIgnoredPathsAsync(
        string repository,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var ignored = await RunGitAsync(
            repository,
            [
                "ls-files", "-z", "--others", "--ignored",
                "--exclude-standard", "--directory"
            ],
            TimeSpan.FromSeconds(30),
            cancellationToken,
            environmentVariables);
        if (ignored.ExitCode != 0)
        {
            throw new CandidateValidationException(
                $"Unable to inspect ignored candidate files at '{repository}': {ignored.CombinedOutput}");
        }

        return ignored.StandardOutput.Split(
                '\0',
                StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Trim('\r', '\n').Replace('\\', '/'))
            .Where(path => path.Length > 0)
            .OrderByDescending(path => path.Length)
            .ThenByDescending(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task ValidateIgnoredPathsAsync(
        string repository,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var ignoredPaths = await ReadIgnoredPathsAsync(
            repository,
            cancellationToken,
            environmentVariables);
        foreach (var path in ignoredPaths)
        {
            if (!IsApprovedIgnoredPath(repository, path))
            {
                throw new CandidateValidationException(
                    $"Candidate repository has an ignored product or configuration path: {path}");
            }
        }
    }

    private static bool TryMatchIgnoredPath(
        string path,
        IReadOnlyList<string> ignoredPaths,
        out string ignoredPath)
    {
        ignoredPath = ignoredPaths.FirstOrDefault(candidate =>
            candidate.EndsWith("/", StringComparison.Ordinal)
                ? path.StartsWith(candidate, StringComparison.Ordinal)
                : string.Equals(path, candidate, StringComparison.Ordinal))
            ?? string.Empty;
        return ignoredPath.Length > 0;
    }

    private static void ValidateTrackedEntryShape(TrackedHeadEntry entry)
    {
        if (!string.Equals(entry.Type, "blob", StringComparison.Ordinal))
        {
            throw new CandidateValidationException(
                $"Candidate repository has unsupported tracked entry type '{entry.Type}' at '{entry.Path}'.");
        }
        if (entry.Mode is not ("100644" or "100755" or "120000"))
        {
            throw new CandidateValidationException(
                $"Candidate repository has unsupported tracked entry mode '{entry.Mode}' at '{entry.Path}'.");
        }
    }

    private static async Task<(
        IReadOnlyList<CandidateScaffoldFile> Files,
        bool RestoredMissingFiles)>
        ValidateNonRepositoryFilesAsync(
        FlowRun flow,
        string workspace,
        IReadOnlyCollection<string> workspaceRepositories,
        CancellationToken cancellationToken,
        bool restoreMissingTrustedFiles = false,
        Func<CancellationToken, Task>? beforeRestore = null)
    {
        var workspaceFiles = EnumerateNonRepositoryFiles(
            workspace,
            workspaceRepositories,
            applyWorkspaceCopyExclusions: false);

        var sourceRoot = Path.GetFullPath(flow.RepositoryPath);
        if (PathsEqual(sourceRoot, workspace))
        {
            if (workspaceFiles.Count > 0)
            {
                throw new CandidateValidationException(
                    "Candidate workspace contains product files outside every Git repository: " +
                    string.Join(", ", workspaceFiles.Keys.Order(StringComparer.Ordinal).Take(5)));
            }
            return ([], false);
        }
        if (!Directory.Exists(sourceRoot))
        {
            throw new CandidateValidationException(
                $"The source project used to validate workspace scaffold files is missing: {sourceRoot}");
        }

        var workspaceContainer = Path.GetDirectoryName(
            Path.TrimEndingDirectorySeparator(workspace));
        var excludedWorkspaceRoot =
            workspaceContainer is not null &&
            IsContainedOrEqual(sourceRoot, workspaceContainer)
                ? Path.GetFullPath(workspaceContainer)
                : null;
        var sourceRepositories = DiscoverRepositories(sourceRoot)
            .Where(repository =>
                excludedWorkspaceRoot is null ||
                !IsContainedOrEqual(excludedWorkspaceRoot, repository))
            .ToArray();
        var sourceFiles = EnumerateNonRepositoryFiles(
            sourceRoot,
            sourceRepositories,
            applyWorkspaceCopyExclusions: true,
            excludedWorkspaceRoot);
        var workspacePaths = workspaceFiles.Keys
            .Order(StringComparer.Ordinal)
            .ToArray();
        var sourcePaths = sourceFiles.Keys
            .Order(StringComparer.Ordinal)
            .ToArray();
        var restoredMissingFiles = false;
        if (restoreMissingTrustedFiles)
        {
            var unexpectedWorkspaceFiles = workspacePaths
                .Except(sourcePaths, StringComparer.Ordinal)
                .Take(5)
                .ToArray();
            if (unexpectedWorkspaceFiles.Length > 0)
            {
                throw new CandidateValidationException(
                    "Candidate changed the exact trusted project scaffold path set: " +
                    string.Join(", ", unexpectedWorkspaceFiles));
            }
            foreach (var relativePath in workspacePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var workspaceFile = workspaceFiles[relativePath];
                var sourceFile = sourceFiles[relativePath];
                if (new FileInfo(workspaceFile).Length !=
                        new FileInfo(sourceFile).Length ||
                    !string.Equals(
                        await HashFileAsync(
                            workspaceFile,
                            cancellationToken),
                        await HashFileAsync(
                            sourceFile,
                            cancellationToken),
                        StringComparison.Ordinal))
                {
                    throw new CandidateValidationException(
                        $"Candidate changed an untracked project scaffold file: {relativePath}");
                }
            }
            var missingTrustedFiles = sourcePaths
                .Except(workspacePaths, StringComparer.Ordinal)
                .ToArray();
            restoredMissingFiles = missingTrustedFiles.Length > 0;
            if (restoredMissingFiles)
            {
                var persistRestorationIntent =
                    beforeRestore ??
                    throw new InvalidOperationException(
                        "Trusted scaffold restoration requires a durable pre-restoration callback.");
                await persistRestorationIntent(cancellationToken);
            }
            foreach (var relativePath in missingTrustedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.GetFullPath(
                    Path.Combine(
                        workspace,
                        relativePath.Replace(
                            '/',
                            Path.DirectorySeparatorChar)));
                var workspacePrefix =
                    Path.TrimEndingDirectorySeparator(workspace) +
                    Path.DirectorySeparatorChar;
                if (!destination.StartsWith(
                        workspacePrefix,
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
                {
                    throw new CandidateValidationException(
                        $"Trusted scaffold path escapes the candidate workspace: {relativePath}");
                }
                Directory.CreateDirectory(
                    Path.GetDirectoryName(destination)!);
                File.Copy(sourceFiles[relativePath], destination);
            }
            workspaceFiles = EnumerateNonRepositoryFiles(
                workspace,
                workspaceRepositories,
                applyWorkspaceCopyExclusions: false);
            workspacePaths = workspaceFiles.Keys
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        if (!workspacePaths.SequenceEqual(sourcePaths, StringComparer.Ordinal))
        {
            var mismatch = workspacePaths
                .Concat(sourcePaths)
                .GroupBy(path => path, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .Select(group => group.Key)
                .Order(StringComparer.Ordinal)
                .Take(5);
            throw new CandidateValidationException(
                "Candidate changed the exact trusted project scaffold path set: " +
                string.Join(", ", mismatch));
        }

        var trustedFiles = new List<CandidateScaffoldFile>(sourcePaths.Length);
        foreach (var relativePath in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var workspaceFile = workspaceFiles[relativePath];
            var sourceFile = sourceFiles[relativePath];
            var workspaceInfo = new FileInfo(workspaceFile);
            var sourceInfo = new FileInfo(sourceFile);
            var sourceDigest = await HashFileAsync(
                sourceFile,
                cancellationToken);
            if (workspaceInfo.Length != sourceInfo.Length ||
                !string.Equals(
                    await HashFileAsync(workspaceFile, cancellationToken),
                    sourceDigest,
                    StringComparison.Ordinal))
            {
                throw new CandidateValidationException(
                    $"Candidate changed an untracked project scaffold file: {relativePath}");
            }
            trustedFiles.Add(new CandidateScaffoldFile(
                relativePath,
                sourceInfo.Length,
                "sha256:" + sourceDigest.ToLowerInvariant()));
        }
        return (trustedFiles, restoredMissingFiles);
    }

    private static IReadOnlyDictionary<string, string> EnumerateNonRepositoryFiles(
        string rootPath,
        IReadOnlyCollection<string> repositories,
        bool applyWorkspaceCopyExclusions,
        string? excludedDirectory = null)
    {
        var root = Path.GetFullPath(rootPath);
        var repositoryRoots = repositories
            .Select(Path.GetFullPath)
            .ToArray();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (excludedDirectory is not null &&
                    IsContainedOrEqual(excludedDirectory, entry))
                {
                    continue;
                }
                if (repositoryRoots.Any(repository => IsContainedOrEqual(repository, entry)))
                {
                    continue;
                }
                if (IsLink(entry))
                {
                    if (applyWorkspaceCopyExclusions)
                    {
                        continue;
                    }
                    throw new CandidateValidationException(
                        $"Candidate has an untracked project scaffold link: " +
                        NormalizeRelativePath(root, entry));
                }
                if (Directory.Exists(entry))
                {
                    var name = Path.GetFileName(entry);
                    var relativePath = NormalizeRelativePath(root, entry);
                    if (string.Equals(
                            name,
                            ".customer-preview",
                            StringComparison.OrdinalIgnoreCase) ||
                        applyWorkspaceCopyExclusions &&
                        (RepositoryAnalyzer.ShouldIgnoreDirectory(entry) ||
                         !RepositoryAnalyzer.CanTraverse(entry)))
                    {
                        continue;
                    }
                    pending.Push(entry);
                }
                else
                {
                    result[NormalizeRelativePath(root, entry)] = entry;
                }
            }
        }
        return result;
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static IReadOnlyList<OutcomeTrustedRepository> ReadTrustedRepositories(
        FlowRun flow)
    {
        if (flow.Kind != FlowKind.Delivery)
        {
            throw new CandidateValidationException(
                "Candidate verification is available only for Delivery flows.");
        }

        return StudioWorkspaceRepositoryMapLedger.Read(flow)
            .Repositories
            .Select(item => new OutcomeTrustedRepository(
                item.RelativePath,
                item.RemoteRepository))
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<ProcessResult> RunGitAsync(
        string repository,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var result = await processRunner.RunAsync(
            "git",
            ["-C", repository, .. arguments],
            repository,
            timeout,
            cancellationToken,
            environmentVariables: environmentVariables);
        return result;
    }

    private async Task<IReadOnlyDictionary<string, string?>> BuildCanonicalBlobEnvironmentAsync(
        string repository,
        string objectFormat,
        CandidateGitSandbox sandbox,
        CancellationToken cancellationToken)
    {
        var gitDirectory = ResolveRepositoryGitDirectory(repository);
        var commonDirectory = ResolveCommonGitDirectory(gitDirectory);
        var objectDirectory = ResolveGitObjectDirectory(
            gitDirectory,
            commonDirectory);
        var shadowGitDirectory = Path.Combine(
            sandbox.RootPath,
            "canonical",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(shadowGitDirectory, "objects", "info"));
        Directory.CreateDirectory(Path.Combine(shadowGitDirectory, "refs", "heads"));
        Directory.CreateDirectory(Path.Combine(shadowGitDirectory, "info"));
        File.WriteAllText(
            Path.Combine(shadowGitDirectory, "HEAD"),
            "ref: refs/heads/ai-harness-canonical\n");
        File.WriteAllText(
            Path.Combine(shadowGitDirectory, "config"),
            await BuildCanonicalBlobConfigAsync(
                repository,
                objectFormat,
                cancellationToken));
        CopyCanonicalAttributesFile(
            gitDirectory,
            commonDirectory,
            shadowGitDirectory);

        return new Dictionary<string, string?>(
            sandbox.EnvironmentVariables,
            StringComparer.Ordinal)
        {
            ["GIT_DIR"] = shadowGitDirectory,
            ["GIT_WORK_TREE"] = repository,
            ["GIT_COMMON_DIR"] = null,
            ["GIT_INDEX_FILE"] = null,
            ["GIT_INDEX_VERSION"] = null,
            ["GIT_OBJECT_DIRECTORY"] = objectDirectory,
            ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = null
        };
    }

    private async Task<string> BuildCanonicalBlobConfigAsync(
        string repository,
        string objectFormat,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>
        {
            "[core]",
            $"    repositoryformatversion = {(string.Equals(objectFormat, "sha256", StringComparison.Ordinal) ? "1" : "0")}",
            "    bare = false"
        };
        if (await ReadCanonicalCoreConfigValueAsync(
                repository,
                "core.autocrlf",
                ["true", "false", "input"],
                cancellationToken) is { } autocrlf)
        {
            lines.Add($"    autocrlf = {autocrlf}");
        }
        if (await ReadCanonicalCoreConfigValueAsync(
                repository,
                "core.eol",
                ["lf", "crlf", "native"],
                cancellationToken) is { } eol)
        {
            lines.Add($"    eol = {eol}");
        }
        if (await ReadCanonicalCoreConfigValueAsync(
                repository,
                "core.safecrlf",
                ["true", "false", "warn"],
                cancellationToken) is { } safeCrlf)
        {
            lines.Add($"    safecrlf = {safeCrlf}");
        }
        if (string.Equals(objectFormat, "sha256", StringComparison.Ordinal))
        {
            lines.Add("[extensions]");
            lines.Add("    objectformat = sha256");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private async Task<string?> ReadCanonicalCoreConfigValueAsync(
        string repository,
        string key,
        IReadOnlyCollection<string> allowedValues,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            repository,
            ["config", "--get", "--no-includes", key],
            TimeSpan.FromSeconds(20),
            cancellationToken,
            environmentVariables: null);
        if (result.ExitCode == 1 &&
            string.IsNullOrWhiteSpace(result.StandardOutput) &&
            string.IsNullOrWhiteSpace(result.StandardError))
        {
            return null;
        }
        if (result.ExitCode != 0)
        {
            throw new CandidateValidationException(
                $"Unable to read safe Git configuration '{key}' for '{repository}': {result.CombinedOutput}");
        }

        var value = result.StandardOutput.Trim().ToLowerInvariant();
        if (!allowedValues.Contains(value, StringComparer.Ordinal))
        {
            throw new CandidateValidationException(
                $"Candidate repository '{repository}' has unsupported Git configuration '{key}={value}' for canonical blob sealing.");
        }

        return value;
    }

    private static void CopyCanonicalAttributesFile(
        string gitDirectory,
        string commonDirectory,
        string shadowGitDirectory)
    {
        var source = new[]
            {
                Path.Combine(gitDirectory, "info", "attributes"),
                Path.Combine(commonDirectory, "info", "attributes")
            }
            .Distinct(PathComparer)
            .FirstOrDefault(File.Exists);
        if (source is null)
        {
            return;
        }

        File.Copy(
            source,
            Path.Combine(shadowGitDirectory, "info", "attributes"),
            overwrite: true);
    }

    private static string ResolveRepositoryGitDirectory(string repository)
    {
        var markerPath = Path.Combine(repository, ".git");
        if (Directory.Exists(markerPath))
        {
            return markerPath;
        }
        if (!File.Exists(markerPath))
        {
            throw new CandidateValidationException(
                $"Candidate repository metadata is missing at '{repository}'.");
        }

        var markerLine = File.ReadLines(markerPath).FirstOrDefault()?.Trim();
        if (markerLine is null ||
            !markerLine.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
        {
            throw new CandidateValidationException(
                $"Candidate repository has an invalid .git pointer file at '{repository}'.");
        }

        return ResolveGitPath(
            repository,
            markerLine["gitdir:".Length..].Trim());
    }

    private static string ResolveCommonGitDirectory(string gitDirectory)
    {
        var commonPointer = Path.Combine(gitDirectory, "commondir");
        if (!File.Exists(commonPointer))
        {
            return gitDirectory;
        }

        return ResolveGitPath(
            gitDirectory,
            File.ReadAllText(commonPointer).Trim());
    }

    private static string ResolveGitObjectDirectory(
        string gitDirectory,
        string commonDirectory)
    {
        foreach (var root in new[] { commonDirectory, gitDirectory }.Distinct(PathComparer))
        {
            var objectDirectory = Path.Combine(root, "objects");
            if (Directory.Exists(objectDirectory))
            {
                return objectDirectory;
            }
        }

        throw new CandidateValidationException(
            $"Candidate repository is missing its Git object database under '{gitDirectory}'.");
    }

    private static string ResolveGitPath(
        string baseDirectory,
        string path) =>
        Path.GetFullPath(
            Path.IsPathRooted(path)
                ? path
                : Path.Combine(baseDirectory, path));

    private async Task<string> HashGitBlobFromWorktreeAsync(
        string repository,
        string relativePath,
        string fullPath,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?> canonicalEnvironmentVariables,
        bool writeObject = false)
    {
        if (!File.Exists(fullPath))
        {
            throw new CandidateValidationException(
                $"Tracked worktree file does not exist: {fullPath}");
        }

        var arguments = new List<string> { "hash-object" };
        if (writeObject)
        {
            arguments.Add("-w");
        }
        arguments.Add("--path");
        arguments.Add(relativePath);
        arguments.Add(fullPath);
        var result = await RunGitAsync(
            repository,
            arguments,
            TimeSpan.FromSeconds(30),
            cancellationToken,
            canonicalEnvironmentVariables);
        var value = result.StandardOutput.Trim().ToLowerInvariant();
        if (result.ExitCode != 0 ||
            value.Length is not (40 or 64) ||
            value.Any(character =>
                !char.IsAsciiHexDigit(character) ||
                char.IsAsciiLetterUpper(character)))
        {
            throw new CandidateValidationException(
                $"{(writeObject ? "Unable to persist" : "Unable to hash")} candidate bytes for '{relativePath}': {result.CombinedOutput}");
        }

        return value;
    }

    private async Task<string> ReadCurrentBranchAsync(
        string repository,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var result = await RunGitAsync(
            repository,
            ["symbolic-ref", "--quiet", "--short", "HEAD"],
            TimeSpan.FromSeconds(20),
            cancellationToken,
            environmentVariables);
        var value = result.StandardOutput.Trim();
        if (result.ExitCode != 0 ||
            string.IsNullOrWhiteSpace(value))
        {
            throw new CandidateValidationException(
                $"Candidate repository '{repository}' is not on a local branch: {result.CombinedOutput}");
        }

        return value;
    }

    private async Task ValidateBranchNameAsync(
        string repository,
        string branchName,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var result = await RunGitAsync(
            repository,
            ["check-ref-format", "--branch", branchName],
            TimeSpan.FromSeconds(20),
            cancellationToken,
            environmentVariables);
        if (result.ExitCode != 0)
        {
            throw new CandidateValidationException(
                $"Candidate repository branch is invalid: {branchName}");
        }
    }

    private async Task InitializeTemporaryIndexAsync(
        string repository,
        string head,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var result = await RunGitAsync(
            repository,
            ["read-tree", head],
            TimeSpan.FromSeconds(30),
            cancellationToken,
            environmentVariables);
        if (result.ExitCode != 0)
        {
            throw new CandidateValidationException(
                $"Unable to initialize the isolated candidate index for '{repository}': {result.CombinedOutput}");
        }
    }

    private async Task RemoveFromTemporaryIndexAsync(
        string repository,
        string path,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var result = await RunGitAsync(
            repository,
            ["update-index", "--force-remove", "--", path],
            TimeSpan.FromSeconds(20),
            cancellationToken,
            environmentVariables);
        if (result.ExitCode != 0)
        {
            throw new CandidateValidationException(
                $"Unable to remove '{path}' from the isolated candidate index for '{repository}': {result.CombinedOutput}");
        }
    }

    private async Task PersistWorkingEntryObjectAsync(
        string repository,
        WorkingTreeEntry entry,
        CancellationToken cancellationToken,
        CandidateGitSandbox sandbox,
        IReadOnlyDictionary<string, string?> canonicalEnvironmentVariables)
    {
        if (entry.Mode != "120000")
        {
            var persisted = await HashGitBlobFromWorktreeAsync(
                repository,
                entry.Path,
                entry.FullPath,
                cancellationToken,
                canonicalEnvironmentVariables,
                writeObject: true);
            if (!string.Equals(
                    persisted,
                    entry.ObjectId,
                    StringComparison.Ordinal))
            {
                throw new CandidateValidationException(
                    $"Unable to persist candidate bytes for '{entry.Path}'.");
            }

            return;
        }

        string? temporaryPath = null;
        try
        {
            temporaryPath = Path.Combine(
                sandbox.RootPath,
                $"{Guid.NewGuid():N}.symlink");
            await File.WriteAllBytesAsync(
                temporaryPath,
                Encoding.UTF8.GetBytes(entry.LinkTarget ?? string.Empty),
                cancellationToken);

            var result = await RunGitAsync(
                repository,
                ["hash-object", "-w", "--no-filters", temporaryPath],
                TimeSpan.FromSeconds(30),
                cancellationToken,
                sandbox.EnvironmentVariables);
            var persisted = result.StandardOutput.Trim().ToLowerInvariant();
            if (result.ExitCode != 0 ||
                !string.Equals(
                    persisted,
                    entry.ObjectId,
                    StringComparison.Ordinal))
            {
                throw new CandidateValidationException(
                    $"Unable to persist candidate bytes for '{entry.Path}': {result.CombinedOutput}");
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryPath) &&
                File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    // Best-effort temporary cleanup only.
                }
            }
        }
    }

    private async Task StageWorkingEntryAsync(
        string repository,
        WorkingTreeEntry entry,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var result = await RunGitAsync(
            repository,
            [
                "update-index",
                "--add",
                "--cacheinfo",
                entry.Mode,
                entry.ObjectId,
                entry.Path
            ],
            TimeSpan.FromSeconds(20),
            cancellationToken,
            environmentVariables);
        if (result.ExitCode != 0)
        {
            throw new CandidateValidationException(
                $"Unable to stage '{entry.Path}' in the isolated candidate index for '{repository}': {result.CombinedOutput}");
        }
    }

    private async Task<string> WriteTreeAsync(
        string repository,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var result = await RunGitAsync(
            repository,
            ["write-tree"],
            TimeSpan.FromSeconds(30),
            cancellationToken,
            environmentVariables);
        var value = result.StandardOutput.Trim().ToLowerInvariant();
        if (result.ExitCode != 0 ||
            value.Length is not (40 or 64) ||
            value.Any(character =>
                !char.IsAsciiHexDigit(character) ||
                char.IsAsciiLetterUpper(character)))
        {
            throw new CandidateValidationException(
                $"Unable to write the isolated candidate tree for '{repository}': {result.CombinedOutput}");
        }

        return value;
    }

    private async Task<string> CreateSealedCommitAsync(
        string repository,
        string tree,
        string parent,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var result = await RunGitAsync(
            repository,
            [
                "commit-tree",
                tree,
                "-p",
                parent,
                "-m",
                "ai-harness: seal local candidate"
            ],
            TimeSpan.FromSeconds(30),
            cancellationToken,
            environmentVariables);
        var value = result.StandardOutput.Trim().ToLowerInvariant();
        if (result.ExitCode != 0 ||
            value.Length is not (40 or 64) ||
            value.Any(character =>
                !char.IsAsciiHexDigit(character) ||
                char.IsAsciiLetterUpper(character)))
        {
            throw new CandidateValidationException(
                $"Unable to create the sealed candidate commit for '{repository}': {result.CombinedOutput}");
        }

        return value;
    }

    private async Task UpdateCurrentBranchAsync(
        string repository,
        string branchName,
        string expectedHead,
        string sealedHead,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var result = await RunGitAsync(
            repository,
            [
                "update-ref",
                $"refs/heads/{branchName}",
                sealedHead,
                expectedHead
            ],
            TimeSpan.FromSeconds(20),
            cancellationToken,
            environmentVariables);
        if (result.ExitCode != 0)
        {
            throw new CandidateValidationException(
                $"Unable to advance local branch '{branchName}' for '{repository}': {result.CombinedOutput}");
        }
    }

    private async Task ResetRealIndexAsync(
        string repository,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var result = await RunGitAsync(
            repository,
            ["reset", "--mixed", "--quiet", "--no-refresh", "HEAD"],
            TimeSpan.FromSeconds(30),
            cancellationToken,
            environmentVariables);
        if (result.ExitCode != 0)
        {
            throw new CandidateValidationException(
                $"Unable to reset the real Git index for '{repository}': {result.CombinedOutput}");
        }
    }

    private async Task<string> ReadGitIdentityAsync(
        string repository,
        string revision,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var result = await RunGitAsync(
            repository,
            ["rev-parse", "--verify", revision],
            TimeSpan.FromSeconds(20),
            cancellationToken,
            environmentVariables);
        var value = result.StandardOutput.Trim().ToLowerInvariant();
        if (result.ExitCode != 0 ||
            value.Length is not (40 or 64) ||
            value.Any(character =>
                !char.IsAsciiHexDigit(character) ||
                char.IsAsciiLetterUpper(character)))
        {
            throw new CandidateValidationException(
                $"Unable to read a full Git {revision} identity for '{repository}': {result.CombinedOutput}");
        }
        return value;
    }

    private async Task<string> ReadRemoteRepositoryAsync(
        string repository,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var pushUrls = await ReadConfiguredRemoteValuesAsync(
            repository,
            "remote.origin.pushurl",
            cancellationToken,
            environmentVariables);
        if (pushUrls.Count > 0)
        {
            return PublishedOutcomeVerifier.NormalizeSingleGitHubRemote(
                       string.Join(Environment.NewLine, pushUrls))
                   ?? string.Empty;
        }

        var urls = await ReadConfiguredRemoteValuesAsync(
            repository,
            "remote.origin.url",
            cancellationToken,
            environmentVariables);
        return PublishedOutcomeVerifier.NormalizeSingleGitHubRemote(
                   string.Join(Environment.NewLine, urls))
               ?? string.Empty;
    }

    private async Task<IReadOnlyList<string>> ReadConfiguredRemoteValuesAsync(
        string repository,
        string key,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var result = await RunGitAsync(
            repository,
            ["config", "--local", "--null", "--get-all", "--no-includes", key],
            TimeSpan.FromSeconds(20),
            cancellationToken,
            environmentVariables);
        if (result.ExitCode == 1 &&
            string.IsNullOrWhiteSpace(result.StandardOutput) &&
            string.IsNullOrWhiteSpace(result.StandardError))
        {
            return [];
        }
        if (result.ExitCode != 0)
        {
            throw new CandidateValidationException(
                $"Unable to read Git configuration '{key}' for '{repository}': {result.CombinedOutput}");
        }

        return result.StandardOutput.Split(
                '\0',
                StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private static async Task<IReadOnlyList<CandidatePreviewArtifact>>
        ReadPreviewArtifactsAsync(
            string workspace,
            CancellationToken cancellationToken)
    {
        var previewRoot = Path.Combine(workspace, ".customer-preview");
        if (!Directory.Exists(previewRoot))
        {
            return [];
        }

        var files = EnumeratePreviewFiles(previewRoot)
            .OrderBy(
                path => NormalizeRelativePath(workspace, path),
                StringComparer.Ordinal)
            .ToArray();
        if (files.Length > MaximumPreviewFiles)
        {
            throw new CandidateValidationException(
                $"Candidate preview contains {files.Length} files; the limit is {MaximumPreviewFiles}.");
        }

        long totalLength = 0;
        var entries = new List<CandidatePreviewArtifact>(files.Length);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsLink(file))
            {
                throw new CandidateValidationException(
                    $"Candidate preview file cannot be a link: {file}");
            }
            var info = new FileInfo(file);
            totalLength = checked(totalLength + info.Length);
            if (totalLength > MaximumPreviewBytes)
            {
                throw new CandidateValidationException(
                    $"Candidate preview exceeds the {MaximumPreviewBytes}-byte limit.");
            }
            var relativePath = NormalizeRelativePath(workspace, file);
            if (string.Equals(
                    Path.GetFileName(file),
                    SealedDemoManifestService.ManifestFileName,
                    StringComparison.Ordinal))
            {
                await ValidateDemoManifestAsync(
                    workspace,
                    file,
                    relativePath,
                    cancellationToken);
            }
            await using var stream = File.OpenRead(file);
            var digest = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken))
                .ToLowerInvariant();
            entries.Add(new CandidatePreviewArtifact(
                relativePath,
                info.Length,
                $"sha256:{digest}"));
        }
        return entries;
    }

    private static async Task ValidateDemoManifestAsync(
        string workspace,
        string manifestPath,
        string relativePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var segments = relativePath.Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 3 ||
                !string.Equals(
                    segments[0],
                    ".customer-preview",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    segments[2],
                    SealedDemoManifestService.ManifestFileName,
                    StringComparison.Ordinal))
            {
                throw new CustomerDemoContractException(
                    "customer-demo.json must be directly under .customer-preview\\<variant>.");
            }

            var manifest = CustomerDemoManifestParser.Parse(
                await File.ReadAllBytesAsync(
                    manifestPath,
                    cancellationToken));
            if (!string.Equals(
                    manifest.ArtifactId,
                    segments[1],
                    StringComparison.Ordinal))
            {
                throw new CustomerDemoContractException(
                    "ArtifactId must exactly match its preview variant directory.");
            }
            var workingDirectory =
                CustomerDemoManifestParser.ResolveWorkingDirectory(
                    manifest,
                    workspace);
            _ = CustomerDemoLaunchPolicy.Resolve(
                manifest.LaunchProfile);
            CustomerDemoLaunchPolicy.ValidateResolvedArguments(
                manifest.LaunchProfile,
                manifest.Arguments,
                workingDirectory);
        }
        catch (CustomerDemoContractException exception)
        {
            throw new CandidateValidationException(
                $"Candidate demo manifest '{relativePath}' is invalid: {exception.Message}");
        }
    }

    private static IReadOnlyList<string> EnumeratePreviewFiles(string previewRoot)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(previewRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (IsLink(entry))
                {
                    throw new CandidateValidationException(
                        $"Candidate preview cannot contain links: {entry}");
                }
                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                }
                else
                {
                    files.Add(entry);
                    if (files.Count > MaximumPreviewFiles)
                    {
                        return files;
                    }
                }
            }
        }
        return files;
    }

    private static bool IsApprovedGeneratedPath(string path) =>
        path.Equals(".customer-preview", StringComparison.Ordinal) ||
        path.StartsWith(".customer-preview/", StringComparison.Ordinal) ||
        path.Equals(".playwright-browsers", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(
            ".playwright-browsers/",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsApprovedIgnoredPath(
        string repository,
        string path)
    {
        var normalized = path.TrimEnd('/');
        if (IsApprovedGeneratedPath(normalized))
        {
            return true;
        }
        var segments = normalized.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        var transientIndex = Array.FindIndex(
            segments,
            segment => IgnoredTransientDirectories.Contains(
                segment,
                StringComparer.OrdinalIgnoreCase));
        if (transientIndex >= 0)
        {
            var ownerDirectory = transientIndex == 0
                ? repository
                : Path.Combine(
                    repository,
                    Path.Combine(segments[..transientIndex]));
            var transientDirectory = segments[transientIndex];
            if (string.Equals(
                    transientDirectory,
                    "packages",
                    StringComparison.OrdinalIgnoreCase))
            {
                return IsDocumentedPackageOutput(ownerDirectory);
            }
            if (string.Equals(
                    transientDirectory,
                    "wwwroot",
                    StringComparison.OrdinalIgnoreCase))
            {
                return IsDocumentedGeneratedWwwroot(ownerDirectory);
            }
            return HasProjectManifest(ownerDirectory);
        }
        return IsApprovedTransientFile(
            segments.LastOrDefault() ?? string.Empty);
    }

    private static bool IsDocumentedPackageOutput(string ownerDirectory) =>
        Directory.Exists(ownerDirectory) &&
        (File.Exists(Path.Combine(ownerDirectory, "packages.config")) ||
         File.Exists(Path.Combine(ownerDirectory, "NuGet.Config")) &&
         (Directory.EnumerateFiles(
                  ownerDirectory,
                  "*.sln*",
                  SearchOption.TopDirectoryOnly)
              .Any() ||
          Directory.EnumerateFiles(
                  ownerDirectory,
                  "*.*proj",
                  SearchOption.TopDirectoryOnly)
              .Any()));

    private static bool IsDocumentedGeneratedWwwroot(string ownerDirectory)
    {
        if (!Directory.Exists(ownerDirectory) ||
            !Directory.EnumerateFiles(
                    ownerDirectory,
                    "*.*proj",
                    SearchOption.TopDirectoryOnly)
                .Any())
        {
            return false;
        }

        var configurationFiles = Directory
            .EnumerateFiles(ownerDirectory, "*", SearchOption.TopDirectoryOnly)
            .Concat(
                Directory.EnumerateDirectories(ownerDirectory)
                    .SelectMany(directory =>
                        Directory.EnumerateFiles(
                            directory,
                            "*",
                            SearchOption.TopDirectoryOnly)))
            .Where(path =>
                Path.GetFileName(path).Equals(
                    "package.json",
                    StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).StartsWith(
                    "vite.config.",
                    StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).StartsWith(
                    "webpack.config.",
                    StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path) is
                    ".csproj" or ".fsproj" or ".vbproj")
            .Take(32);
        foreach (var path in configurationFiles)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length <= 256 * 1024 &&
                    File.ReadAllText(path).Contains(
                        "wwwroot",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        return false;
    }

    private static bool HasProjectManifest(string directory) =>
        Directory.Exists(directory) &&
        (File.Exists(Path.Combine(directory, "package.json")) ||
         File.Exists(Path.Combine(directory, "pyproject.toml")) ||
         File.Exists(Path.Combine(directory, "requirements.txt")) ||
         File.Exists(Path.Combine(directory, "setup.py")) ||
         File.Exists(Path.Combine(directory, "go.mod")) ||
         File.Exists(Path.Combine(directory, "Cargo.toml")) ||
         Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
             .Any(path => Path.GetExtension(path) is
                 ".csproj" or ".fsproj" or ".vbproj"));

    private static bool IsApprovedTransientFile(string fileName) =>
        fileName.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".suo", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".user", StringComparison.OrdinalIgnoreCase);

    private static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new CandidateValidationException(
                $"Unable to inspect candidate path attributes for '{path}': {exception.Message}");
        }
    }

    private static bool IsContained(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, Path.GetFullPath(candidate));
        return !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith(
                   $"..{Path.DirectorySeparatorChar}",
                   StringComparison.Ordinal);
    }

    private static bool IsContainedOrEqual(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.GetFullPath(candidate);
        return string.Equals(fullRoot, fullCandidate, comparison) ||
               fullCandidate.StartsWith(
                   fullRoot + Path.DirectorySeparatorChar,
                   comparison);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static string NormalizeRelativePath(string root, string path) =>
        Path.GetRelativePath(root, Path.GetFullPath(path)).Replace('\\', '/');

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed record RepositoryIdentity(
        string Head,
        string Tree);

    private sealed record TrackedHeadEntry(
        string Mode,
        string Type,
        string ObjectId,
        string Path);

    private sealed record TrackedIndexDebugEntry(
        string Path,
        string Flags);

    private sealed record TrackedIndexStageEntry(
        string Path,
        string Mode,
        string ObjectId);

    private sealed record CandidateWorkspaceContext(
        string Workspace,
        IReadOnlyList<string> Repositories,
        IReadOnlyList<OutcomeTrustedRepository> TrustedRepositories);

    private sealed record WorkingTreeEntry(
        string Path,
        string FullPath,
        string Mode,
        string ObjectId,
        string? LinkTarget);

    private sealed class CandidateSealJournal
    {
        public string Repository { get; set; } = string.Empty;

        public string BranchName { get; set; } = string.Empty;

        public string ExpectedHead { get; set; } = string.Empty;

        public string ExpectedTree { get; set; } = string.Empty;

        public string SealedTree { get; set; } = string.Empty;

        public string SealedHead { get; set; } = string.Empty;

        public CandidateSealTransition Phase { get; set; }
    }

    private sealed class CandidateGitSandbox : IDisposable
    {
        private CandidateGitSandbox(
            string rootPath,
            IReadOnlyDictionary<string, string?> environmentVariables,
            IReadOnlyDictionary<string, string?> temporaryIndexEnvironmentVariables,
            IReadOnlyDictionary<string, string?> commitEnvironmentVariables)
        {
            RootPath = rootPath;
            EnvironmentVariables = environmentVariables;
            TemporaryIndexEnvironmentVariables = temporaryIndexEnvironmentVariables;
            CommitEnvironmentVariables = commitEnvironmentVariables;
        }

        public string RootPath { get; }

        public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; }

        public IReadOnlyDictionary<string, string?> TemporaryIndexEnvironmentVariables { get; }

        public IReadOnlyDictionary<string, string?> CommitEnvironmentVariables { get; }

        public static CandidateGitSandbox Create()
        {
            var rootPath = Path.Combine(
                Path.GetTempPath(),
                $"ai-harness-candidate-{Guid.NewGuid():N}");
            Directory.CreateDirectory(rootPath);

            var homePath = Path.Combine(rootPath, "home");
            var xdgPath = Path.Combine(rootPath, "xdg");
            var hooksPath = Path.Combine(rootPath, "hooks");
            Directory.CreateDirectory(homePath);
            Directory.CreateDirectory(xdgPath);
            Directory.CreateDirectory(hooksPath);
            var globalConfigPath = Path.Combine(rootPath, "global.gitconfig");
            File.WriteAllText(globalConfigPath, string.Empty);
            var disabledCommandPath = CreateDisabledCommand(rootPath);
            var indexPath = Path.Combine(rootPath, "candidate.index");

            var environmentVariables = new Dictionary<string, string?>(
                StringComparer.Ordinal)
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
                ["GIT_ASKPASS"] = disabledCommandPath,
                ["SSH_ASKPASS"] = disabledCommandPath,
                ["GIT_SSH"] = disabledCommandPath,
                ["GIT_SSH_COMMAND"] = QuoteCommand(disabledCommandPath)
            };

            var config = new List<KeyValuePair<string, string>>
            {
                new("core.hooksPath", hooksPath),
                new("core.fsmonitor", "false"),
                new("credential.helper", string.Empty),
                new("credential.interactive", "never"),
                new("commit.gpgSign", "false"),
                new("tag.gpgSign", "false"),
                new("gpg.program", disabledCommandPath)
            };

            ApplyEnvironmentConfig(environmentVariables, config);

            var temporaryIndexEnvironmentVariables =
                new Dictionary<string, string?>(environmentVariables, StringComparer.Ordinal)
                {
                    ["GIT_INDEX_FILE"] = indexPath
                };
            var commitEnvironmentVariables =
                new Dictionary<string, string?>(temporaryIndexEnvironmentVariables, StringComparer.Ordinal)
                {
                    ["GIT_AUTHOR_NAME"] = "AI Harness Host",
                    ["GIT_AUTHOR_EMAIL"] = "host@ai-harness.invalid",
                    ["GIT_AUTHOR_DATE"] = "2000-01-01T00:00:00Z",
                    ["GIT_COMMITTER_NAME"] = "AI Harness Host",
                    ["GIT_COMMITTER_EMAIL"] = "host@ai-harness.invalid",
                    ["GIT_COMMITTER_DATE"] = "2000-01-01T00:00:00Z"
                };

            return new CandidateGitSandbox(
                rootPath,
                environmentVariables,
                temporaryIndexEnvironmentVariables,
                commitEnvironmentVariables);
        }

        public void Dispose() => DeleteDirectoryBestEffort(RootPath);

        private static void ApplyEnvironmentConfig(
            IDictionary<string, string?> environmentVariables,
            IReadOnlyList<KeyValuePair<string, string>> config)
        {
            environmentVariables["GIT_CONFIG_COUNT"] = config.Count.ToString();
            for (var index = 0; index < config.Count; index++)
            {
                environmentVariables[$"GIT_CONFIG_KEY_{index}"] = config[index].Key;
                environmentVariables[$"GIT_CONFIG_VALUE_{index}"] = config[index].Value;
            }
        }

        private static string CreateDisabledCommand(string rootPath)
        {
            if (OperatingSystem.IsWindows())
            {
                var path = Path.Combine(rootPath, "disabled.cmd");
                File.WriteAllText(
                    path,
                    "@echo off\r\nexit /b 1\r\n");
                return path;
            }

            var shellPath = Path.Combine(rootPath, "disabled");
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
                // Best-effort permission hardening only.
            }

            return shellPath;
        }

        private static string QuoteCommand(string path) =>
            OperatingSystem.IsWindows()
                ? $"\"{path}\""
                : $"'{path.Replace("'", "'\\''", StringComparison.Ordinal)}'";

        private static void DeleteDirectoryBestEffort(string path)
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
                // Temporary candidate cleanup is best-effort only.
            }
        }
    }

    private sealed class GitIncrementalHasher(HashAlgorithmName algorithmName)
        : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(algorithmName);

        public void AppendData(ReadOnlySpan<byte> data) => _hash.AppendData(data);

        public async Task AppendAsync(
            Stream stream,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[81920];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }
                _hash.AppendData(buffer.AsSpan(0, read));
            }
        }

        public byte[] GetHashAndReset() => _hash.GetHashAndReset();

        public void Dispose() => _hash.Dispose();
    }
}

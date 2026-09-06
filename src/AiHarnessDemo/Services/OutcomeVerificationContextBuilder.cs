using System.Security.Cryptography;
using System.Text;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Data;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

public sealed record OutcomeQaContext(
    string Path,
    string Hash,
    string PromptSummary);

public sealed class OutcomeVerificationContextBuilder(
    IDbContextFactory<HarnessDbContext> databaseFactory,
    WorkflowDefinitionProvider? workflowProvider = null)
{
    public async Task<OutcomeQaContext> BuildAsync(
        Guid flowId,
        int round,
        CancellationToken cancellationToken = default)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var flow = await database.Flows
            .AsNoTracking()
            .AsSplitQuery()
            .Include(item => item.Messages)
            .Include(item => item.Steps)
            .SingleOrDefaultAsync(item => item.Id == flowId, cancellationToken)
            ?? throw new KeyNotFoundException($"Factory flow '{flowId}' was not found.");
        if (string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson))
        {
            throw new InvalidOperationException(
                "Legacy flows do not have an outcome-verification context.");
        }

        var state = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        var plan = state.AcceptancePlan
            ?? throw new InvalidOperationException(
                "QA context cannot be generated before an acceptance plan exists.");
        var candidate = state.CurrentCandidate
            ?? throw new InvalidOperationException(
                "QA context cannot be generated before a candidate exists.");
        if (round < 1 || round > state.MaxRounds)
        {
            throw new InvalidOperationException(
                $"QA round {round} is outside the active budget of {state.MaxRounds}.");
        }
        if (!string.Equals(
                candidate.Manifest.AcceptancePlanHash,
                plan.Hash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "QA context cannot use a candidate from a different acceptance plan.");
        }
        if (string.IsNullOrWhiteSpace(flow.WorkspacePath))
        {
            throw new DirectoryNotFoundException(
                $"The QA workspace does not exist: {flow.WorkspacePath}");
        }

        var workspace = WorkspacePathGuard.ValidateExistingRoot(
            flow.WorkspacePath,
            workflowProvider?.GetValidated().Config.Workspace.ResolvedRoot,
            "QA context");
        var packet = new
        {
            Version = "outcome-qa-context-v1",
            FlowId = flow.Id,
            FlowIteration = flow.Iteration,
            OriginalCustomerRequest = flow.OriginalRequest,
            ConfirmedBrief = flow.ConsolidatedRequest,
            CurrentIterationFeedback = flow.Messages
                .Where(message => message.Role == ConversationRole.Customer)
                .OrderBy(message => message.CreatedAt)
                .Select(message => message.Content)
                .ToArray(),
            AcceptancePlan = plan,
            AcceptancePlanHash = plan.Hash,
            Evidence = state.Evidence
                .OrderBy(item => item.CriterionId, StringComparer.Ordinal)
                .ThenBy(item => item.ProducedAt)
                .ThenBy(item => item.EvidenceId, StringComparer.Ordinal)
                .ToArray(),
            CandidateManifest = candidate.Manifest,
            CandidateFingerprint = candidate.Fingerprint,
            PreviousQaRounds = state.Rounds
                .OrderBy(item => item.Round)
                .ToArray(),
            CorrectionStepReferences = flow.Steps
                .Where(step =>
                    step.Iteration == flow.Iteration &&
                    (step.Kind is
                         FlowStepKind.OutcomeOwnerCorrection or
                         FlowStepKind.OutcomePlanCorrection or
                         FlowStepKind.OutcomeCandidateRefresh or
                         FlowStepKind.OutcomeQa ||
                     step.RetryOfStepId is not null))
                .OrderBy(step => step.Sequence)
                .Select(step => new
                {
                    step.Id,
                    step.AgentRole,
                    step.Label,
                    step.Kind,
                    step.Status,
                    step.DependsOnStepId,
                    step.OutcomeQaRound,
                    step.OutcomePlanHash,
                    step.StableSemanticRootId
                })
                .ToArray(),
            CurrentRound = round,
            MaximumRounds = state.MaxRounds,
            WorkspacePath = workspace,
            Repositories = candidate.Manifest.Repositories.Select(repository => new
            {
                repository.RelativePath,
                FullPath = Path.GetFullPath(
                    Path.Combine(
                        workspace,
                        repository.RelativePath.Replace(
                            '/',
                            Path.DirectorySeparatorChar))),
                repository.Head,
                repository.Tree
            }).ToArray(),
            PreviewArtifacts = candidate.Manifest.PreviewArtifacts
        };
        var json = OutcomeVerificationRules.SerializeCanonical(packet);
        var hash = OutcomeVerificationRules.ComputeSha256(json);
        var directory = ResolveContextDirectory(
            workspace,
            candidate.Fingerprint);
        CreateContextDirectorySafely(workspace, directory);
        var path = Path.Combine(directory, "qa-context.json");
        EnsureExistingPathContained(workspace, path);
        if (IsLink(path))
        {
            throw new InvalidOperationException(
                $"The QA context file path cannot be a link: {path}");
        }
        if (Directory.Exists(path))
        {
            throw new IOException(
                $"The QA context file path is occupied by a directory: {path}");
        }
        if (File.Exists(path))
        {
            File.SetAttributes(
                path,
                File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }
        await File.WriteAllTextAsync(
            path,
            json,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
        EnsureExistingPathContained(workspace, path);
        if (IsLink(path))
        {
            throw new InvalidOperationException(
                $"The persisted QA context file unexpectedly became a link: {path}");
        }
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var persistedHash = "sha256:" + Convert.ToHexString(
            SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(hash, persistedHash, StringComparison.Ordinal))
        {
            throw new IOException(
                "The persisted QA context hash does not match its canonical content.");
        }
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

        var criterionSummary = string.Join(
            Environment.NewLine,
            plan.Criteria.Select(criterion =>
                $"- {criterion.Id}: {criterion.Requirement}"));
        return new OutcomeQaContext(
            path,
            hash,
            $"Outcome QA round {round} of {state.MaxRounds}.{Environment.NewLine}" +
            $"Acceptance plan: {plan.Hash}{Environment.NewLine}" +
            $"Candidate: {candidate.Fingerprint}{Environment.NewLine}" +
            $"Read-only context packet: {path}{Environment.NewLine}" +
            $"Context hash: {hash}{Environment.NewLine}" +
            $"Workspace to inspect: {workspace}{Environment.NewLine}" +
            $"Criteria:{Environment.NewLine}{criterionSummary}");
    }

    internal static async Task<bool> MatchesPersistedHashAsync(
        string path,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        if (!OutcomeVerificationRules.IsSha256(expectedHash) ||
            string.IsNullOrWhiteSpace(path) ||
            !File.Exists(path) ||
            IsLink(path))
        {
            return false;
        }
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var actual = "sha256:" + Convert.ToHexString(
                SHA256.HashData(bytes)).ToLowerInvariant();
            return string.Equals(actual, expectedHash, StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static string ResolveContextDirectory(
        string workspacePath,
        string fingerprint)
    {
        if (!OutcomeVerificationRules.IsSha256(fingerprint))
        {
            throw new InvalidOperationException(
                "A valid candidate fingerprint is required for the QA context path.");
        }
        var root = WorkspacePathGuard.ValidateExistingRoot(
            workspacePath,
            authorizedWorkspaceRoot: null,
            "QA context");
        var relative = Path.Combine(
            ".ai-harness",
            "outcome-verification",
            fingerprint.Replace(':', '-'));
        var resolved = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsContainedOrEqual(root, resolved))
        {
            throw new InvalidOperationException(
                "The QA context path escaped the flow workspace.");
        }
        EnsureExistingPathContained(root, resolved);
        EnsureNoLinkComponents(root, resolved);
        return resolved;
    }

    private static void CreateContextDirectorySafely(
        string workspacePath,
        string directoryPath)
    {
        var root = Path.GetFullPath(workspacePath);
        var relative = Path.GetRelativePath(root, Path.GetFullPath(directoryPath));
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The QA context directory escaped the flow workspace.");
        }

        var current = root;
        if (IsLink(current))
        {
            throw new InvalidOperationException(
                $"The QA workspace root cannot be a link: {current}");
        }
        EnsureExistingPathContained(root, current);
        foreach (var segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            EnsureExistingPathContained(root, current);
            if (IsLink(current))
            {
                throw new InvalidOperationException(
                    $"The QA context directory cannot traverse a link: {current}");
            }
            if (File.Exists(current) && !Directory.Exists(current))
            {
                throw new IOException(
                    $"The QA context directory path is occupied by a file: {current}");
            }
            if (!Directory.Exists(current))
            {
                Directory.CreateDirectory(current);
            }
            EnsureExistingPathContained(root, current);
        }
    }

    internal static void EnsureExistingPathContained(
        string workspacePath,
        string candidatePath)
    {
        var resolvedRoot = ResolveExistingPathComponents(workspacePath);
        var resolvedCandidate = ResolveExistingPathComponents(candidatePath);
        if (!IsContainedOrEqual(resolvedRoot, resolvedCandidate))
        {
            throw new InvalidOperationException(
                $"The QA context path resolves outside the flow workspace: {candidatePath}");
        }
    }

    private static void EnsureNoLinkComponents(
        string workspacePath,
        string candidatePath)
    {
        var root = Path.GetFullPath(workspacePath);
        if (IsLink(root))
        {
            throw new InvalidOperationException(
                $"The QA workspace root cannot be a link: {root}");
        }
        var relative = Path.GetRelativePath(root, Path.GetFullPath(candidatePath));
        var current = root;
        foreach (var segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (IsLink(current))
            {
                throw new InvalidOperationException(
                    $"The QA context path cannot traverse a link: {current}");
            }
        }
    }

    private static string ResolveExistingPathComponents(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException(
                $"The QA context path has no filesystem root: {path}");
        var current = root;
        var remainder = fullPath[root.Length..];
        foreach (var segment in remainder.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, segment);
            FileSystemInfo? info = null;
            try
            {
                var attributes = File.GetAttributes(next);
                info = (attributes & FileAttributes.Directory) != 0
                    ? new DirectoryInfo(next)
                    : new FileInfo(next);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // Remaining non-existent components stay lexically beneath the
                // last physically resolved directory and are checked after creation.
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Unable to resolve QA context path component '{next}'.",
                    exception);
            }

            if (info is not null &&
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                FileSystemInfo? target;
                try
                {
                    target = info.ResolveLinkTarget(returnFinalTarget: true);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException(
                        $"Unable to resolve QA context link '{next}'.",
                        exception);
                }
                current = target?.FullName
                    ?? throw new InvalidOperationException(
                        $"Unable to resolve QA context link '{next}'.");
            }
            else
            {
                current = next;
            }
        }
        return Path.GetFullPath(current);
    }

    private static bool IsContainedOrEqual(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(candidate));
        return string.Equals(fullRoot, fullCandidate, comparison) ||
               fullCandidate.StartsWith(
                   fullRoot + Path.DirectorySeparatorChar,
                   comparison);
    }

    private static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Unable to inspect QA context path attributes for '{path}'.",
                exception);
        }
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Data;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

internal sealed class PreviewPreparationRequiredException(string message)
    : InvalidOperationException(message);

internal static class WorkspacePreviewPreparer
{
    internal const string StartedEventType = "workspace.preview-preparation-started";
    internal const string CompletedEventType = "workspace.preview-prepared";

    internal sealed record PreviewFile(string RelativePath, long Length, string Digest);
    internal sealed record Preparation(
        Guid Id,
        string Variant,
        string SourceRelativePath,
        IReadOnlyList<PreviewFile> Files,
        IReadOnlyList<PreviewFile> PreviousFiles,
        int Revision = 1);

    internal static async Task<bool> PrepareAsync(
        HarnessDbContext database,
        FlowRun flow,
        Guid stepId,
        int verificationSequence,
        CancellationToken cancellationToken)
    {
        if (!ReviewedCandidateService.RequiresCustomerPreview(flow))
        {
            return false;
        }
        var root = WorkspacePathGuard.ValidateExistingRoot(
            flow.WorkspacePath, null, "Verification preview preparation");
        CandidateFingerprintService.ValidateLinksStayInside(root);
        var canonicalRoot = Path.Combine(root, ".customer-preview");
        if (Directory.Exists(canonicalRoot))
        {
            WorkspacePathGuard.ValidateExistingContainedDirectory(
                root, canonicalRoot, "Canonical preview preparation");
        }
        var mapping = StudioWorkspaceRepositoryMapLedger.Read(flow);
        var catalog = new PreviewArtifactCatalog();
        var canonical = catalog.Discover(flow).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var sources = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var repository in mapping.Repositories.Where(item => item.RelativePath != "."))
        {
            var repositoryRoot = ResolveUnder(root, repository.RelativePath);
            WorkspacePathGuard.ValidateExistingContainedDirectory(
                root, repositoryRoot, "Repository preview preparation");
            var sourceFlow = new FlowRun
            {
                Id = flow.Id,
                Title = flow.Title,
                OriginalRequest = flow.OriginalRequest,
                WorkspacePath = repositoryRoot
            };
            foreach (var artifact in catalog.Discover(sourceFlow))
            {
                if (!sources.TryGetValue(artifact.Id, out var paths))
                {
                    paths = [];
                    sources.Add(artifact.Id, paths);
                }
                paths.Add(Path.Combine(repositoryRoot, ".customer-preview", artifact.Id));
            }
        }
        var history = await database.FlowEvents.AsNoTracking()
            .Where(item => item.FlowRunId == flow.Id &&
                           (item.Type == StartedEventType || item.Type == CompletedEventType))
            .OrderBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);
        var preparations = history.Select(item => (
            item.Type,
            Preparation: JsonSerializer.Deserialize<Preparation>(item.DataJson ?? string.Empty)
                         ?? throw new InvalidOperationException("Preview preparation history is invalid.")))
            .ToArray();
        var changed = false;
        foreach (var (variant, paths) in sources.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var previous = preparations
                .Where(item => item.Type == StartedEventType && item.Preparation.Variant == variant)
                .OrderByDescending(item => item.Preparation.Revision)
                .Select(item => item.Preparation)
                .FirstOrDefault();
            if (canonical.Contains(variant) && previous is null)
            {
                continue;
            }
            if (paths.Count != 1)
            {
                throw new PreviewPreparationRequiredException(
                    $"Preview variant '{variant}' has multiple repository-local origins; a single workspace-root preview must be prepared explicitly.");
            }
            var source = paths[0];
            WorkspacePathGuard.ValidateExistingContainedDirectory(
                root, source, "Repository preview preparation");
            var destination = ResolveUnder(root, $".customer-preview/{variant}");
            var desired = await ReadFilesAsync(source, cancellationToken);
            var current = Directory.Exists(destination)
                ? await ReadFilesAsync(destination, cancellationToken)
                : [];
            var pending = previous is not null &&
                !preparations.Any(item => item.Type == CompletedEventType &&
                                          item.Preparation.Id == previous.Id);
            if (pending && (!string.Equals(
                                previous!.SourceRelativePath,
                                Path.GetRelativePath(root, source).Replace('\\', '/'),
                                StringComparison.Ordinal) ||
                            !previous.Files.SequenceEqual(desired)))
            {
                throw new PreviewPreparationRequiredException(
                    $"The source for interrupted preview preparation '{variant}' changed; its persisted intent cannot be replayed.");
            }
            if (current.SequenceEqual(desired))
            {
                if (pending)
                {
                    database.FlowEvents.Add(new FlowEvent
                    {
                        FlowRunId = flow.Id,
                        FlowStepId = stepId,
                        Type = CompletedEventType,
                        Message = $"Completed interrupted canonical preview preparation for '{variant}' without rewriting verified bytes.",
                        DataJson = JsonSerializer.Serialize(previous)
                    });
                    await database.SaveChangesAsync(cancellationToken);
                }
                continue;
            }
            if (canonical.Contains(variant) && previous is not null && !pending &&
                !current.SequenceEqual(previous.Files))
            {
                // A worker explicitly replaced the canonical preview; it owns that new version.
                continue;
            }
            var permitted = previous is null
                ? desired
                : previous.Files.Concat(previous.PreviousFiles).Concat(desired).ToArray();
            if (current.Any(file => !permitted.Contains(file)))
            {
                throw new PreviewPreparationRequiredException(
                    $"Preview preparation would overwrite unrelated workspace-root files for '{variant}'.");
            }
            var preparation = new Preparation(
                Guid.NewGuid(), variant,
                Path.GetRelativePath(root, source).Replace('\\', '/'),
                desired, current, checked((previous?.Revision ?? 0) + 1));
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = stepId,
                Type = StartedEventType,
                Message =
                    $"Preparing repository-local preview '{variant}' at the canonical workspace root before substantive verification.",
                DataJson = JsonSerializer.Serialize(preparation)
            });
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = stepId,
                Type = DeliveryReadinessService.EvidenceEpochEventType,
                Message = "Started a new evidence epoch before materializing the canonical verification preview.",
                DataJson = DeliveryReadinessService.SerializeEvidenceEpoch(
                    new DeliveryEvidenceEpoch(flow.Iteration, stepId, verificationSequence))
            });
            await database.SaveChangesAsync(cancellationToken);
            foreach (var file in desired)
            {
                var sourceFile = ResolveUnder(source, file.RelativePath);
                await using var sourceStream = new FileStream(
                    sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (sourceStream.Length != file.Length)
                {
                    throw new PreviewPreparationRequiredException(
                        $"Preview source changed during preparation: {preparation.SourceRelativePath}/{file.RelativePath}");
                }
                var bytes = new byte[checked((int)file.Length)];
                await sourceStream.ReadExactlyAsync(bytes, cancellationToken);
                if (Digest(bytes) != file.Digest)
                {
                    throw new PreviewPreparationRequiredException(
                        $"Preview source changed during preparation: {preparation.SourceRelativePath}/{file.RelativePath}");
                }
                var target = ResolveUnder(destination, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var stagingRoot = ResolveUnder(root, ".host-preview-staging");
                Directory.CreateDirectory(stagingRoot);
                WorkspacePathGuard.ValidateExistingContainedDirectory(
                    root, stagingRoot, "Preview staging");
                WorkspacePathGuard.ValidateExistingContainedDirectory(
                    root, Path.GetDirectoryName(target)!, "Preview destination");
                var staged = Path.Combine(stagingRoot, $"{Guid.NewGuid():N}.tmp");
                try
                {
                    await File.WriteAllBytesAsync(staged, bytes, cancellationToken);
                    File.Move(staged, target, overwrite: true);
                }
                finally
                {
                    if (File.Exists(staged))
                    {
                        File.Delete(staged);
                    }
                }
            }
            foreach (var removed in current.Where(file =>
                         !desired.Any(item => item.RelativePath == file.RelativePath)))
            {
                File.Delete(ResolveUnder(destination, removed.RelativePath));
            }
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = stepId,
                Type = CompletedEventType,
                Message =
                    $"Prepared canonical preview '{variant}' from '{preparation.SourceRelativePath}'; the next verifier must exercise the real Studio preview endpoint.",
                DataJson = JsonSerializer.Serialize(preparation)
            });
            await database.SaveChangesAsync(cancellationToken);
            changed = true;
        }
        if (catalog.Discover(flow).Count == 0)
        {
            throw new PreviewPreparationRequiredException(
                "Substantive browser verification cannot start without a workspace-root .customer-preview artifact. " +
                "Prepare it in an implementation dependency; source inspection or an independently served copy cannot substitute for the Studio preview.");
        }
        _ = await ReadFilesAsync(canonicalRoot, cancellationToken);
        return changed;
    }

    private static async Task<PreviewFile[]> ReadFilesAsync(
        string root,
        CancellationToken cancellationToken)
    {
        CandidateFingerprintService.ValidateLinksStayInside(root);
        var result = new List<PreviewFile>();
        long totalBytes = 0;
        var paths = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new CandidateValidationException("Preview preparation cannot materialize links.");
                }
                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                }
                else
                {
                    paths.Add(entry);
                    if (paths.Count > CandidateFingerprintService.MaximumPreviewFiles)
                    {
                        throw new PreviewPreparationRequiredException("The preview preparation exceeds the candidate preview file limit.");
                    }
                }
            }
        }
        foreach (var file in paths.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = new FileInfo(file).Length;
            totalBytes += length;
            if (result.Count >= CandidateFingerprintService.MaximumPreviewFiles ||
                totalBytes > CandidateFingerprintService.MaximumPreviewBytes)
            {
                throw new PreviewPreparationRequiredException("The preview preparation exceeds the candidate preview limits.");
            }
            await using var stream = File.OpenRead(file);
            result.Add(new PreviewFile(
                Path.GetRelativePath(root, file).Replace('\\', '/'),
                length,
                "sha256:" + Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant()));
        }
        return result.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static string ResolveUnder(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(
            fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (Path.IsPathRooted(relativePath) ||
            !path.StartsWith(
                Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new CandidateValidationException("Preview preparation escaped the isolated workspace.");
        }
        return path;
    }

    private static string Digest(byte[] content) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}

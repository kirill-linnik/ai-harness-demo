using System.Collections.Concurrent;
using System.Security.Cryptography;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Data;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

public sealed record ReviewedPreviewArtifactMetadata(
    string RelativePath,
    long Length,
    string Digest);

public sealed record ReviewedPreviewSnapshot(
    ReviewedCandidateIdentity Identity,
    IReadOnlyList<ReviewedPreviewArtifactMetadata> Artifacts,
    bool Materialized);

public sealed record ReviewedPreviewFile(
    string RelativePath,
    long Length,
    string Digest,
    byte[] Content);

/// <summary>
/// Persists and serves the exact preview bytes bound to a reviewed candidate. Full workspace
/// validation happens while the candidate is sealed (or once when an older seal is backfilled);
/// request-time reads touch only SQLite and verify the requested BLOB against its stored digest.
/// </summary>
public sealed class ReviewedPreviewStore(
    IDbContextFactory<HarnessDbContext> databaseFactory,
    TimeProvider timeProvider)
{
    private const string PreviewPrefix = ".customer-preview/";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _captureLocks =
        new(StringComparer.Ordinal);

    public async Task PersistAsync(
        FlowRun flow,
        ReviewedCandidateIdentity identity,
        OutcomeCandidateSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(snapshot);
        ReviewedCandidateLedger.ValidateForFlow(flow, identity);
        ValidateSnapshot(identity, snapshot);

        var key = SnapshotKey(identity);
        var captureLock = _captureLocks.GetOrAdd(
            key,
            static _ => new SemaphoreSlim(1, 1));
        await captureLock.WaitAsync(cancellationToken);
        try
        {
            await using var database =
                await databaseFactory.CreateDbContextAsync(cancellationToken);
            var existing = await database.ReviewedPreviewArtifacts
                .AsNoTracking()
                .Where(item =>
                    item.FlowRunId == identity.FlowId &&
                    item.Iteration == identity.Iteration &&
                    item.CandidateFingerprint == identity.Fingerprint)
                .OrderBy(item => item.RelativePath)
                .ToListAsync(cancellationToken);
            if (existing.Count > 0)
            {
                ValidatePersistedSet(identity, snapshot.Manifest.PreviewArtifacts, existing);
                return;
            }
            if (snapshot.Manifest.PreviewArtifacts.Count == 0)
            {
                return;
            }

            var workspace = WorkspacePathGuard.ValidateExistingRoot(
                flow.WorkspacePath,
                authorizedWorkspaceRoot: null,
                "Reviewed preview snapshot");
            CandidateFingerprintService.ValidateLinksStayInside(workspace);
            var records = new List<ReviewedPreviewArtifactRecord>(
                snapshot.Manifest.PreviewArtifacts.Count);
            foreach (var artifact in snapshot.Manifest.PreviewArtifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = NormalizePreviewPath(artifact.RelativePath);
                var fullPath = ResolveWorkspaceFile(workspace, relativePath);
                var content = await File.ReadAllBytesAsync(
                    fullPath,
                    cancellationToken);
                ValidateContent(relativePath, artifact.Length, artifact.Digest, content);
                records.Add(new ReviewedPreviewArtifactRecord
                {
                    FlowRunId = identity.FlowId,
                    Iteration = identity.Iteration,
                    CandidateFingerprint = identity.Fingerprint,
                    RelativePath = relativePath,
                    Length = content.LongLength,
                    Digest = artifact.Digest,
                    Content = content,
                    CreatedAt = timeProvider.GetUtcNow()
                });
            }

            database.ReviewedPreviewArtifacts.AddRange(records);
            await database.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            captureLock.Release();
        }
    }

    public async Task<ReviewedPreviewSnapshot> LoadCurrentAsync(
        FlowRun flow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var candidate = await database.ReviewedCandidateRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.FlowRunId == flow.Id && item.Active,
                cancellationToken)
            ?? throw new CandidateValidationException(
                "The current Delivery flow has no active reviewed candidate binding.");
        var identity = ReviewedCandidateLedger.Deserialize(candidate.IdentityJson);
        ReviewedCandidateLedger.ValidateForFlow(flow, identity);
        ValidateCandidateBinding(candidate, identity);

        var artifacts = await database.ReviewedPreviewArtifacts
            .AsNoTracking()
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.Iteration == identity.Iteration &&
                item.CandidateFingerprint == identity.Fingerprint)
            .OrderBy(item => item.RelativePath)
            .Select(item => new ReviewedPreviewArtifactMetadata(
                item.RelativePath,
                item.Length,
                item.Digest))
            .ToListAsync(cancellationToken);
        if (artifacts.Count == 0 && identity.PreviewFileCount > 0)
        {
            return new ReviewedPreviewSnapshot(identity, artifacts, Materialized: false);
        }
        ValidateMetadataSet(identity, artifacts);
        return new ReviewedPreviewSnapshot(identity, artifacts, Materialized: true);
    }

    public async Task<ReviewedPreviewFile> ReadAsync(
        ReviewedPreviewSnapshot snapshot,
        string artifactId,
        string? path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.Materialized)
        {
            throw new CandidateValidationException(
                "The reviewed preview snapshot has not been materialized.");
        }

        var relativePath = ResolveRequestedPath(snapshot.Artifacts, artifactId, path);
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var record = await database.ReviewedPreviewArtifacts
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item =>
                    item.FlowRunId == snapshot.Identity.FlowId &&
                    item.Iteration == snapshot.Identity.Iteration &&
                    item.CandidateFingerprint == snapshot.Identity.Fingerprint &&
                    item.RelativePath == relativePath,
                cancellationToken)
            ?? throw new FileNotFoundException(
                $"Reviewed preview file '{relativePath}' was not found.");
        ValidateContent(
            record.RelativePath,
            record.Length,
            record.Digest,
            record.Content);
        return new ReviewedPreviewFile(
            record.RelativePath,
            record.Length,
            record.Digest,
            record.Content);
    }

    private static void ValidateSnapshot(
        ReviewedCandidateIdentity identity,
        OutcomeCandidateSnapshot snapshot)
    {
        if (!string.Equals(
                snapshot.Fingerprint,
                identity.Fingerprint,
                StringComparison.Ordinal) ||
            snapshot.Manifest.FlowIteration != identity.Iteration ||
            snapshot.PreparedByStepId != identity.OutcomeOwnerStepId ||
            snapshot.Manifest.PreviewArtifacts.Count != identity.PreviewFileCount ||
            snapshot.Manifest.PreviewArtifacts.Sum(item => item.Length) !=
                identity.PreviewTotalBytes)
        {
            throw new CandidateValidationException(
                "The preview snapshot does not match the reviewed candidate identity.");
        }
        var paths = snapshot.Manifest.PreviewArtifacts
            .Select(item => NormalizePreviewPath(item.RelativePath))
            .ToArray();
        if (paths.Distinct(StringComparer.Ordinal).Count() != paths.Length)
        {
            throw new CandidateValidationException(
                "The reviewed preview snapshot contains duplicate paths.");
        }
    }

    private static void ValidateCandidateBinding(
        ReviewedCandidateRecord candidate,
        ReviewedCandidateIdentity identity)
    {
        if (candidate.FlowRunId != identity.FlowId ||
            candidate.Iteration != identity.Iteration ||
            candidate.OutcomeOwnerStepId != identity.OutcomeOwnerStepId ||
            !string.Equals(
                candidate.CandidateFingerprint,
                identity.Fingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                candidate.OutcomeContractHash,
                identity.OutcomeContractHash,
                StringComparison.Ordinal))
        {
            throw new CandidateValidationException(
                "The active reviewed candidate row does not match its durable identity.");
        }
    }

    private static void ValidateMetadataSet(
        ReviewedCandidateIdentity identity,
        IReadOnlyList<ReviewedPreviewArtifactMetadata> artifacts)
    {
        if (artifacts.Count != identity.PreviewFileCount ||
            artifacts.Sum(item => item.Length) != identity.PreviewTotalBytes ||
            artifacts.Any(item =>
                item.Length < 0 ||
                !OutcomeVerificationRules.IsSha256(item.Digest) ||
                !string.Equals(
                    item.RelativePath,
                    NormalizePreviewPath(item.RelativePath),
                    StringComparison.Ordinal)) ||
            artifacts.Select(item => item.RelativePath)
                .Distinct(StringComparer.Ordinal)
                .Count() != artifacts.Count)
        {
            throw new CandidateValidationException(
                "The durable reviewed preview does not match its candidate identity.");
        }
    }

    private static void ValidatePersistedSet(
        ReviewedCandidateIdentity identity,
        IReadOnlyList<CandidatePreviewArtifact> expected,
        IReadOnlyList<ReviewedPreviewArtifactRecord> actual)
    {
        var expectedByPath = expected.ToDictionary(
            item => NormalizePreviewPath(item.RelativePath),
            StringComparer.Ordinal);
        if (actual.Count != expectedByPath.Count)
        {
            throw new CandidateValidationException(
                "The durable reviewed preview is incomplete.");
        }
        foreach (var record in actual)
        {
            if (!expectedByPath.TryGetValue(record.RelativePath, out var artifact) ||
                record.Length != artifact.Length ||
                !string.Equals(record.Digest, artifact.Digest, StringComparison.Ordinal))
            {
                throw new CandidateValidationException(
                    "The durable reviewed preview metadata differs from its sealed manifest.");
            }
            ValidateContent(
                record.RelativePath,
                record.Length,
                record.Digest,
                record.Content);
        }
        ValidateMetadataSet(
            identity,
            actual.Select(item => new ReviewedPreviewArtifactMetadata(
                    item.RelativePath,
                    item.Length,
                    item.Digest))
                .ToArray());
    }

    private static string ResolveRequestedPath(
        IReadOnlyList<ReviewedPreviewArtifactMetadata> artifacts,
        string artifactId,
        string? path)
    {
        if (string.IsNullOrWhiteSpace(artifactId) ||
            artifactId.Length > 120 ||
            artifactId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '_' and not '-'))
        {
            throw new UnauthorizedAccessException(
                "Customer preview artifact IDs must be simple path segments.");
        }
        if (path?.Contains('\\', StringComparison.Ordinal) == true)
        {
            throw new UnauthorizedAccessException(
                "Customer preview paths must use forward slashes.");
        }

        var segments = (path ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new UnauthorizedAccessException(
                "Customer preview artifact paths must remain inside the sealed preview.");
        }
        var requested = segments.Length == 0
            ? "index.html"
            : string.Join("/", segments);
        var artifactPrefix = $"{PreviewPrefix}{artifactId}/";
        var browserPrefix = artifactPrefix + "browser/";
        var rootPrefix = artifacts.Any(item =>
            string.Equals(
                item.RelativePath,
                browserPrefix + "index.html",
                StringComparison.Ordinal))
            ? browserPrefix
            : artifactPrefix;
        var resolved = rootPrefix + requested;
        if (artifacts.Any(item =>
                string.Equals(item.RelativePath, resolved, StringComparison.Ordinal)))
        {
            return resolved;
        }
        if (string.IsNullOrEmpty(Path.GetExtension(requested)))
        {
            var fallback = rootPrefix + "index.html";
            if (artifacts.Any(item =>
                    string.Equals(item.RelativePath, fallback, StringComparison.Ordinal)))
            {
                return fallback;
            }
        }
        throw new FileNotFoundException(
            $"Customer preview file '{path}' was not found.");
    }

    private static string NormalizePreviewPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        if (!normalized.StartsWith(PreviewPrefix, StringComparison.Ordinal) ||
            segments.Length < 3 ||
            segments.Any(segment => segment is "." or "..") ||
            !string.Equals(
                normalized,
                string.Join("/", segments),
                StringComparison.Ordinal))
        {
            throw new CandidateValidationException(
                $"Reviewed preview path '{relativePath}' is invalid.");
        }
        return normalized;
    }

    private static string ResolveWorkspaceFile(
        string workspace,
        string relativePath)
    {
        var resolved = Path.GetFullPath(
            Path.Combine(
                workspace,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix =
            Path.TrimEndingDirectorySeparator(workspace) +
            Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(
                prefix,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal) ||
            !File.Exists(resolved))
        {
            throw new CandidateValidationException(
                $"Reviewed preview file '{relativePath}' is missing or outside the workspace.");
        }
        return resolved;
    }

    private static void ValidateContent(
        string relativePath,
        long expectedLength,
        string expectedDigest,
        byte[] content)
    {
        var digest =
            "sha256:" +
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        if (content.LongLength != expectedLength ||
            !string.Equals(digest, expectedDigest, StringComparison.Ordinal))
        {
            throw new CandidateValidationException(
                $"Reviewed preview file '{relativePath}' does not match its sealed digest.");
        }
    }

    private static string SnapshotKey(ReviewedCandidateIdentity identity) =>
        $"{identity.FlowId:D}:{identity.Iteration}:{identity.Fingerprint}";
}

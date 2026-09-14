using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Verification;

namespace AiHarnessDemo.Services;

public sealed record AdvisoryWorkspaceEvidence(
    string BaselineDigest,
    int FileCount,
    long TotalBytes,
    DateTimeOffset CapturedAt);

public sealed record AdvisorySourceVerification(
    string BaselineDigest,
    string VerifiedDigest,
    int FileCount,
    long TotalBytes,
    DateTimeOffset VerifiedAt);

public sealed record AdvisoryArtifact(
    string Path,
    string MediaType,
    int ByteLength,
    string FullPath);

public sealed record AdvisoryArtifactIdentity(
    string Path,
    string MediaType,
    int ByteLength,
    string ContentDigest);

public sealed record AdvisoryArtifactMaterializationPolicy(
    Guid FlowId,
    int Iteration,
    string OutcomeHash,
    string ArtifactDirectory,
    string MaterializationDirectory,
    int MaximumArtifactCount,
    int MaximumTotalArtifactBytes,
    IReadOnlyList<AdvisoryArtifactIdentity> Artifacts,
    DateTimeOffset MaterializedAt);

public sealed record AdvisoryOutcomeMaterialization(
    AdvisorySourceVerification Verification,
    IReadOnlyList<AdvisoryArtifact> Artifacts,
    AdvisoryArtifactMaterializationPolicy Policy);

public enum GuardedSnapshotOwnershipStatus
{
    NotOwned,
    Matching,
    Mismatch
}

public sealed record GuardedSnapshotOwnership(
    GuardedSnapshotOwnershipStatus Status,
    AdvisoryWorkspaceEvidence? Evidence);

public sealed class AdvisoryArtifactCatalog(
    WorkflowDefinitionProvider workflowProvider)
{
    public const string MaterializationEventType =
        "advisory.artifacts-materialized";
    private const string MetadataDirectoryName = ".studio-host";
    private const string GuardedSnapshotMarkerFileName =
        ".studio-workspace-owner.json";
    private const int MaximumMaterializationDataBytes = 60 * 1024;
    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(allowIntegerValues: false)
        }
    };
    private static readonly JsonSerializerOptions DurableJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async Task<AdvisoryWorkspaceEvidence> EnsureBaselineAsync(
        FlowRun flow,
        string workspacePath,
        WorkspaceMode mode,
        CancellationToken cancellationToken = default)
    {
        if (mode == WorkspaceMode.Delivery)
        {
            throw new InvalidOperationException(
                "Delivery workspaces do not use an Advisory source baseline.");
        }

        var workspace = ValidateWorkspace(flow, workspacePath, "Advisory baseline");
        var existing = await ReadMetadataAsync(flow.Id, cancellationToken);
        if (existing is not null)
        {
            ValidateMetadata(existing, flow, workspace);
            if (existing.Mode == WorkspaceMode.Delivery)
            {
                throw new InvalidOperationException(
                    "A Delivery workspace cannot be reused as a guarded Advisory snapshot.");
            }
            if (existing.Baseline is null)
            {
                throw new InvalidOperationException(
                    "The guarded Advisory workspace metadata has no source baseline.");
            }
            var current = await CaptureAsync(
                workspace,
                ResolveRegisteredMaterializationRoots(workspace, existing),
                cancellationToken);
            if (!BaselineMatches(existing.Baseline, current))
            {
                throw new InvalidOperationException(
                    "The guarded Advisory workspace no longer matches its flow-owned baseline.");
            }
            if (existing.Mode != mode)
            {
                existing.Mode = mode;
                await WriteMetadataAsync(existing, cancellationToken);
            }
            return ToEvidence(existing);
        }

        var artifactDirectory = NormalizeRelativeArtifactDirectory(
            workflowProvider.GetEffective()
                .Config.Studio.Advisory.ArtifactDirectory);
        var baseline = await CaptureAsync(
            workspace,
            [],
            cancellationToken);
        var metadata = new AdvisoryWorkspaceMetadata
        {
            FlowId = flow.Id,
            WorkspacePath = workspace,
            SourcePath = Path.GetFullPath(flow.RepositoryPath),
            Mode = mode,
            ArtifactDirectory = artifactDirectory,
            Baseline = baseline,
            CapturedAt = DateTimeOffset.UtcNow
        };
        await WriteMetadataAsync(metadata, cancellationToken);
        return ToEvidence(metadata);
    }

    public async Task<AdvisoryWorkspaceEvidence> JournalGuardedSnapshotAsync(
        FlowRun flow,
        string stagingPath,
        string finalPath,
        WorkspaceMode mode,
        CancellationToken cancellationToken = default)
    {
        if (mode == WorkspaceMode.Delivery)
        {
            throw new InvalidOperationException(
                "Only guarded read-only snapshots can be ownership-journaled.");
        }
        var authorizedRoot = AuthorizedWorkspaceRoot();
        var staging = WorkspacePathGuard.ValidateExistingRoot(
            stagingPath,
            authorizedRoot,
            "Guarded snapshot ownership");
        var final = ValidateContainedPath(
            authorizedRoot,
            finalPath,
            "Guarded snapshot ownership");
        var artifactDirectory = NormalizeRelativeArtifactDirectory(
            workflowProvider.GetEffective()
                .Config.Studio.Advisory.ArtifactDirectory);
        var baseline = await CaptureAsync(
            staging,
            [],
            cancellationToken);
        var ownershipNonce = Convert.ToHexString(
                RandomNumberGenerator.GetBytes(32))
            .ToLowerInvariant();
        await WriteOwnershipMarkerAsync(
            staging,
            new GuardedSnapshotMarker
            {
                FlowId = flow.Id,
                SourcePath = Path.GetFullPath(flow.RepositoryPath),
                WorkspacePath = final,
                OwnershipNonce = ownershipNonce,
                BaselineDigest = baseline.Digest
            },
            cancellationToken);
        var metadata = new AdvisoryWorkspaceMetadata
        {
            FlowId = flow.Id,
            WorkspacePath = final,
            SourcePath = Path.GetFullPath(flow.RepositoryPath),
            Mode = mode,
            ArtifactDirectory = artifactDirectory,
            OwnershipNonce = ownershipNonce,
            Baseline = baseline,
            CapturedAt = DateTimeOffset.UtcNow
        };
        await WriteMetadataAsync(metadata, cancellationToken);
        return ToEvidence(metadata);
    }

    public async Task<GuardedSnapshotOwnership> InspectGuardedSnapshotAsync(
        FlowRun flow,
        string workspacePath,
        WorkspaceMode expectedMode,
        CancellationToken cancellationToken = default)
    {
        if (expectedMode == WorkspaceMode.Delivery)
        {
            throw new InvalidOperationException(
                "Delivery worktrees cannot be adopted as guarded snapshots.");
        }
        var workspace = WorkspacePathGuard.ValidateExistingRoot(
            workspacePath,
            AuthorizedWorkspaceRoot(),
            "Guarded snapshot adoption");
        var metadata = await ReadMetadataAsync(flow.Id, cancellationToken);
        if (metadata is null)
        {
            return new GuardedSnapshotOwnership(
                GuardedSnapshotOwnershipStatus.NotOwned,
                null);
        }
        try
        {
            ValidateMetadata(metadata, flow, workspace);
        }
        catch (InvalidOperationException)
        {
            return new GuardedSnapshotOwnership(
                GuardedSnapshotOwnershipStatus.NotOwned,
                null);
        }
        if (metadata.Mode == WorkspaceMode.Delivery ||
            metadata.Baseline is null ||
            !PathsEqual(metadata.SourcePath, flow.RepositoryPath) ||
            !await HasMatchingOwnershipMarkerAsync(
                flow,
                workspace,
                metadata,
                cancellationToken))
        {
            return new GuardedSnapshotOwnership(
                GuardedSnapshotOwnershipStatus.NotOwned,
                null);
        }
        var current = await CaptureAsync(
            workspace,
            ResolveRegisteredMaterializationRoots(workspace, metadata),
            cancellationToken);
        if (!BaselineMatches(metadata.Baseline, current))
        {
            return new GuardedSnapshotOwnership(
                GuardedSnapshotOwnershipStatus.Mismatch,
                ToEvidence(metadata));
        }
        if (metadata.Mode != expectedMode)
        {
            metadata.Mode = expectedMode;
            await WriteMetadataAsync(metadata, cancellationToken);
        }
        return new GuardedSnapshotOwnership(
            GuardedSnapshotOwnershipStatus.Matching,
            ToEvidence(metadata));
    }

    public async Task RecordDeliveryModeAsync(
        FlowRun flow,
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        var workspace = ValidateWorkspace(flow, workspacePath, "Delivery workspace");
        await WriteMetadataAsync(
            new AdvisoryWorkspaceMetadata
            {
                FlowId = flow.Id,
                WorkspacePath = workspace,
                SourcePath = Path.GetFullPath(flow.RepositoryPath),
                Mode = WorkspaceMode.Delivery,
                ArtifactDirectory = NormalizeRelativeArtifactDirectory(
                    workflowProvider.GetEffective()
                        .Config.Studio.Advisory.ArtifactDirectory),
                Baseline = null,
                CapturedAt = DateTimeOffset.UtcNow
            },
            cancellationToken);
    }

    public async Task<WorkspaceMode?> GetWorkspaceModeAsync(
        Guid flowId,
        CancellationToken cancellationToken = default) =>
        (await ReadMetadataAsync(flowId, cancellationToken))?.Mode;

    public Task DeleteMetadataAsync(
        Guid flowId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = MetadataPath(flowId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    public Task<AdvisorySourceVerification> VerifyBaselineAsync(
        FlowRun flow,
        CancellationToken cancellationToken = default) =>
        VerifyBaselineAsync(flow, additionalExcludedRoot: null, cancellationToken);

    private async Task<AdvisorySourceVerification> VerifyBaselineAsync(
        FlowRun flow,
        string? additionalExcludedRoot,
        CancellationToken cancellationToken)
    {
        if (flow.Kind != FlowKind.Advisory)
        {
            throw new InvalidOperationException(
                "Only Studio Advisory flows have a guarded source baseline.");
        }
        var workspace = ValidateWorkspace(
            flow,
            flow.WorkspacePath,
            "Advisory source verification");
        var metadata = await ReadMetadataAsync(flow.Id, cancellationToken)
                       ?? throw new InvalidOperationException(
                           "The Advisory workspace has no durable source baseline metadata.");
        ValidateMetadata(metadata, flow, workspace);
        if (metadata.Mode is not (
                WorkspaceMode.AdvisoryReadOnly or
                WorkspaceMode.ProvisionalReadOnly) ||
            metadata.Baseline is null)
        {
            throw new InvalidOperationException(
                "The flow workspace is not a guarded Advisory snapshot.");
        }

        var exclusions = ResolveRegisteredMaterializationRoots(
                workspace,
                metadata)
            .ToList();
        if (!string.IsNullOrWhiteSpace(additionalExcludedRoot))
        {
            exclusions.Add(ValidateContainedPath(
                workspace,
                additionalExcludedRoot,
                "Advisory materialization exclusion"));
        }
        var current = await CaptureAsync(
            workspace,
            exclusions,
            cancellationToken);
        if (!BaselineMatches(metadata.Baseline, current))
        {
            throw new InvalidOperationException(
                "Advisory source verification failed: the guarded source snapshot changed.");
        }

        return new AdvisorySourceVerification(
            metadata.Baseline.Digest,
            current.Digest,
            current.FileCount,
            current.TotalBytes,
            DateTimeOffset.UtcNow);
    }

    public async Task<AdvisoryOutcomeMaterialization> VerifyAndWriteAsync(
        FlowRun flow,
        ParsedFlowOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var config = workflowProvider.GetEffective().Config.Studio.Advisory;
        var validated = FlowOutcomeParser.ParseJson(
            outcome.RawJson,
            config.MaxArtifactCount,
            config.MaxTotalArtifactBytes);
        var artifactDirectory = NormalizeRelativeArtifactDirectory(
            config.ArtifactDirectory);
        var outcomeHash = OutcomeVerificationRules.ComputeSha256(
            validated.RawJson);
        var materializationDirectory = string.Join(
            "\\",
            artifactDirectory,
            "iterations",
            $"{flow.Iteration:D6}-{outcomeHash["sha256:".Length..]}");
        var artifactIdentities = (validated.Document.Artifacts ?? [])
            .Select(artifact =>
            {
                var bytes = Encoding.UTF8.GetBytes(artifact.Content);
                return new AdvisoryArtifactIdentity(
                    NormalizeArtifactPath(artifact.Path),
                    artifact.MediaType,
                    bytes.Length,
                    "sha256:" + Convert.ToHexString(SHA256.HashData(bytes))
                        .ToLowerInvariant());
            })
            .OrderBy(artifact => artifact.Path, StringComparer.Ordinal)
            .ToArray();
        var policy = new AdvisoryArtifactMaterializationPolicy(
            flow.Id,
            flow.Iteration,
            outcomeHash,
            artifactDirectory,
            materializationDirectory,
            config.MaxArtifactCount,
            config.MaxTotalArtifactBytes,
            artifactIdentities,
            DateTimeOffset.UtcNow);
        ValidateMaterializationPolicy(policy, flow: null);
        if (flow.Kind != FlowKind.Advisory ||
            policy.FlowId != flow.Id ||
            policy.Iteration != flow.Iteration)
        {
            throw new InvalidOperationException(
                "Advisory artifacts can be materialized only for the current Studio Advisory iteration.");
        }
        var materializationRoot = ResolveMaterializationRoot(flow, policy);
        var alreadyRegistered = await IsRegisteredMaterializationAsync(
            flow,
            policy,
            cancellationToken);
        if ((Directory.Exists(materializationRoot) ||
             File.Exists(materializationRoot)) &&
            !alreadyRegistered)
        {
            throw new InvalidOperationException(
                "Advisory materialization collides with a pre-existing, non-host-owned workspace path.");
        }

        // The deterministic final root is excluded before verification so a crash after the
        // atomic move can be recognized and reused rather than treated as source mutation.
        var verification = await VerifyBaselineAsync(
            flow,
            materializationRoot,
            cancellationToken);
        await RegisterMaterializationAsync(
            flow,
            policy,
            cancellationToken);
        var artifacts = await WriteArtifactsAsync(
            flow,
            validated,
            policy,
            cancellationToken);
        return new AdvisoryOutcomeMaterialization(
            verification,
            artifacts,
            policy);
    }

    public string SerializeMaterialization(
        AdvisoryArtifactMaterializationPolicy policy)
    {
        ValidateMaterializationPolicy(policy, flow: null);
        var json = JsonSerializer.Serialize(policy, DurableJsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumMaterializationDataBytes)
        {
            throw new InvalidOperationException(
                "The Advisory materialization policy exceeds the durable event limit.");
        }
        return json;
    }

    public AdvisoryArtifactMaterializationPolicy? TryReadCurrentMaterialization(
        FlowRun flow) =>
        ReadPersistedMaterialization(flow);

    public static AdvisoryArtifactMaterializationPolicy?
        ReadPersistedMaterialization(FlowRun flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var candidates = flow.Events
            .Where(item =>
                item.Type == MaterializationEventType &&
                !string.IsNullOrWhiteSpace(item.DataJson))
            .Select(item => DeserializeMaterialization(item.DataJson!))
            .Where(item => item.Iteration == flow.Iteration)
            .ToArray();
        if (candidates.Length > 1)
        {
            throw new InvalidOperationException(
                "The current Advisory iteration has duplicate materialization identities.");
        }
        if (candidates.Length == 0)
        {
            return null;
        }
        ValidateMaterializationPolicy(candidates[0], flow);
        return candidates[0];
    }

    public IReadOnlyList<AdvisoryArtifact> Discover(FlowRun flow)
    {
        if (flow.Kind != FlowKind.Advisory ||
            string.IsNullOrWhiteSpace(flow.OutcomeContractJson) ||
            string.IsNullOrWhiteSpace(flow.WorkspacePath))
        {
            return [];
        }
        var policy = TryReadCurrentMaterialization(flow)
                     ?? throw new InvalidOperationException(
                         "The current Advisory outcome has no durable artifact materialization policy.");
        var outcome = FlowOutcomeParser.ParseJson(
            flow.OutcomeContractJson,
            policy.MaximumArtifactCount,
            policy.MaximumTotalArtifactBytes);
        if (!string.Equals(
                OutcomeVerificationRules.ComputeSha256(outcome.RawJson),
                policy.OutcomeHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Advisory outcome no longer matches its durable materialization identity.");
        }
        var root = ResolveMaterializationRoot(flow, policy);
        return ValidateMaterializedArtifacts(root, outcome, policy);
    }

    public string ResolveFile(FlowRun flow, string relativePath)
    {
        var normalized = NormalizeArtifactPath(relativePath);
        var artifact = Discover(flow).SingleOrDefault(item =>
            string.Equals(
                item.Path,
                normalized,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            ?? throw new FileNotFoundException(
                $"Advisory artifact '{relativePath}' was not found.");
        return artifact.FullPath;
    }

    private async Task<IReadOnlyList<AdvisoryArtifact>> WriteArtifactsAsync(
        FlowRun flow,
        ParsedFlowOutcome outcome,
        AdvisoryArtifactMaterializationPolicy policy,
        CancellationToken cancellationToken)
    {
        var artifactRoot = ResolveMaterializationRoot(flow, policy);
        if (Directory.Exists(artifactRoot))
        {
            return ValidateMaterializedArtifacts(
                artifactRoot,
                outcome,
                policy);
        }
        if (File.Exists(artifactRoot))
        {
            throw new InvalidOperationException(
                "Advisory materialization collides with an existing file.");
        }
        EnsureNoReparseAncestors(flow.WorkspacePath, artifactRoot);

        var stagingRoot = Path.Combine(
            MetadataRoot(),
            $"{flow.Id:N}.{flow.Iteration:D6}.{policy.OutcomeHash["sha256:".Length..]}.writing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        try
        {
            foreach (var artifact in outcome.Document.Artifacts ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = NormalizeArtifactPath(artifact.Path);
                var stagingFile = ResolveArtifactFile(stagingRoot, normalized);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(stagingFile)
                    ?? throw new InvalidOperationException(
                        "Advisory artifact path has no parent directory."));
                await File.WriteAllTextAsync(
                    stagingFile,
                    artifact.Content,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken);
            }

            Directory.CreateDirectory(
                Path.GetDirectoryName(artifactRoot)
                ?? throw new InvalidOperationException(
                    "Advisory materialization directory has no parent."));
            Directory.Move(stagingRoot, artifactRoot);
            return ValidateMaterializedArtifacts(
                artifactRoot,
                outcome,
                policy);
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
    }

    private static IReadOnlyList<AdvisoryArtifact> ValidateMaterializedArtifacts(
        string root,
        ParsedFlowOutcome outcome,
        AdvisoryArtifactMaterializationPolicy policy)
    {
        if (!Directory.Exists(root))
        {
            throw new FileNotFoundException(
                "The accepted Advisory materialization directory is missing.");
        }
        RejectReparse(root, "Advisory materialization directory");
        var expectedDocuments = (outcome.Document.Artifacts ?? [])
            .ToDictionary(
                artifact => NormalizeArtifactPath(artifact.Path),
                artifact => artifact,
                StringComparer.Ordinal);
        var expectedIdentities = policy.Artifacts.ToDictionary(
            artifact => artifact.Path,
            artifact => artifact,
            StringComparer.Ordinal);
        if (expectedDocuments.Count != expectedIdentities.Count ||
            !expectedDocuments.Keys
                .Order(StringComparer.Ordinal)
                .SequenceEqual(
                    expectedIdentities.Keys.Order(StringComparer.Ordinal),
                    StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The Advisory outcome and durable artifact identity disagree.");
        }

        var actualPaths = EnumerateMaterializedFiles(root)
            .Select(path => NormalizeRelativePath(root, path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expectedPaths = expectedDocuments.Keys
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!actualPaths.SequenceEqual(expectedPaths, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The Advisory materialization contains missing or unexpected files.");
        }

        var result = new List<AdvisoryArtifact>(expectedPaths.Length);
        foreach (var path in expectedPaths)
        {
            var fullPath = ResolveArtifactFile(root, path);
            if (HasReparseComponent(root, fullPath))
            {
                throw new UnauthorizedAccessException(
                    $"Accepted Advisory artifact '{path}' contains a reparse-point escape.");
            }
            var expectedBytes = Encoding.UTF8.GetBytes(
                expectedDocuments[path].Content);
            var identity = expectedIdentities[path];
            var actualBytes = File.ReadAllBytes(fullPath);
            var digest = "sha256:" +
                         Convert.ToHexString(SHA256.HashData(actualBytes))
                             .ToLowerInvariant();
            if (!actualBytes.AsSpan().SequenceEqual(expectedBytes) ||
                identity.ByteLength != expectedBytes.Length ||
                !string.Equals(
                    identity.MediaType,
                    expectedDocuments[path].MediaType,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    identity.ContentDigest,
                    digest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Advisory artifact '{path}' no longer matches its accepted materialization.");
            }
            result.Add(new AdvisoryArtifact(
                path,
                identity.MediaType,
                identity.ByteLength,
                fullPath));
        }
        return result;
    }

    private static IReadOnlyList<string> EnumerateMaterializedFiles(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            RejectReparse(directory, "Advisory materialization directory");
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectReparse(child, "Advisory materialization entry");
                if (Directory.Exists(child))
                {
                    pending.Push(child);
                }
                else
                {
                    files.Add(child);
                }
            }
        }
        return files;
    }

    private async Task RegisterMaterializationAsync(
        FlowRun flow,
        AdvisoryArtifactMaterializationPolicy policy,
        CancellationToken cancellationToken)
    {
        var metadata = await ReadMetadataAsync(flow.Id, cancellationToken)
                       ?? throw new InvalidOperationException(
                           "The Advisory workspace metadata disappeared during materialization.");
        var workspace = ValidateWorkspace(
            flow,
            flow.WorkspacePath,
            "Advisory materialization registration");
        ValidateMetadata(metadata, flow, workspace);
        var existing = metadata.Materializations
            .Where(item =>
                item.Iteration == policy.Iteration &&
                string.Equals(
                    item.OutcomeHash,
                    policy.OutcomeHash,
                    StringComparison.Ordinal))
            .ToArray();
        if (existing.Length > 1 ||
            existing.Length == 1 &&
            (!string.Equals(
                 existing[0].RelativeDirectory,
                 policy.MaterializationDirectory,
                 StringComparison.Ordinal) ||
             !existing[0].Artifacts.SequenceEqual(policy.Artifacts)))
        {
            throw new InvalidOperationException(
                "The guarded workspace metadata conflicts with this Advisory materialization.");
        }
        if (existing.Length == 0)
        {
            metadata.Materializations.Add(new RegisteredMaterialization
            {
                Iteration = policy.Iteration,
                OutcomeHash = policy.OutcomeHash,
                RelativeDirectory = policy.MaterializationDirectory,
                Artifacts = policy.Artifacts.ToArray()
            });
            await WriteMetadataAsync(metadata, cancellationToken);
        }
    }

    private async Task<bool> IsRegisteredMaterializationAsync(
        FlowRun flow,
        AdvisoryArtifactMaterializationPolicy policy,
        CancellationToken cancellationToken)
    {
        var metadata = await ReadMetadataAsync(flow.Id, cancellationToken)
                       ?? throw new InvalidOperationException(
                           "The Advisory workspace has no flow-owned metadata.");
        var workspace = ValidateWorkspace(
            flow,
            flow.WorkspacePath,
            "Advisory materialization ownership");
        ValidateMetadata(metadata, flow, workspace);
        var matches = metadata.Materializations
            .Where(item =>
                item.Iteration == policy.Iteration &&
                string.Equals(
                    item.OutcomeHash,
                    policy.OutcomeHash,
                    StringComparison.Ordinal))
            .ToArray();
        if (matches.Length > 1)
        {
            throw new InvalidOperationException(
                "The guarded workspace metadata contains duplicate Advisory materializations.");
        }
        if (matches.Length == 0)
        {
            return false;
        }
        if (!string.Equals(
                matches[0].RelativeDirectory,
                policy.MaterializationDirectory,
                StringComparison.Ordinal) ||
            !matches[0].Artifacts.SequenceEqual(policy.Artifacts))
        {
            throw new InvalidOperationException(
                "The guarded workspace metadata conflicts with the requested Advisory materialization.");
        }
        ValidateRegisteredMaterialization(
            ResolveRelativeDirectory(
                workspace,
                matches[0].RelativeDirectory),
            matches[0]);
        return true;
    }

    private async Task<AdvisorySourceBaseline> CaptureAsync(
        string workspace,
        IEnumerable<string> excludedRoots,
        CancellationToken cancellationToken)
    {
        var exclusions = excludedRoots
            .Select(Path.GetFullPath)
            .ToList();
        var metadataRoot = MetadataRoot();
        if (IsContained(workspace, metadataRoot))
        {
            exclusions.Add(metadataRoot);
        }
        var distinctExclusions = exclusions
            .Distinct(PathComparer())
            .ToArray();
        var files = new List<AdvisorySourceFile>();
        var pending = new Stack<string>();
        pending.Push(workspace);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            RejectReparse(directory, "Advisory source directory");
            foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            {
                RejectReparse(childDirectory, "Advisory source directory");
                if (distinctExclusions.Any(excluded =>
                        IsContainedOrEqual(excluded, childDirectory)) ||
                    string.Equals(
                        Path.GetFileName(childDirectory),
                        ".git",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                pending.Push(childDirectory);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                RejectReparse(file, "Advisory source file");
                if (string.Equals(
                        Path.GetFileName(file),
                        ".git",
                        StringComparison.OrdinalIgnoreCase) ||
                    PathsEqual(
                        file,
                        Path.Combine(
                            workspace,
                            GuardedSnapshotMarkerFileName)))
                {
                    continue;
                }
                var relative = NormalizeRelativePath(workspace, file);
                var length = new FileInfo(file).Length;
                await using var stream = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 81_920,
                    useAsync: true);
                var digest = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken));
                files.Add(new AdvisorySourceFile(relative, length, digest));
            }
        }

        files.Sort((left, right) =>
            StringComparer.Ordinal.Compare(left.Path, right.Path));
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long totalBytes = 0;
        foreach (var file in files)
        {
            totalBytes = checked(totalBytes + file.Length);
            aggregate.AppendData(Encoding.UTF8.GetBytes(file.Path));
            aggregate.AppendData([0]);
            aggregate.AppendData(Encoding.UTF8.GetBytes(file.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture)));
            aggregate.AppendData([0]);
            aggregate.AppendData(Encoding.ASCII.GetBytes(file.Sha256));
            aggregate.AppendData([0]);
        }
        return new AdvisorySourceBaseline
        {
            Digest = "sha256:" + Convert.ToHexString(aggregate.GetHashAndReset())
                .ToLowerInvariant(),
            FileCount = files.Count,
            TotalBytes = totalBytes,
            Files = files
        };
    }

    private string ValidateWorkspace(
        FlowRun flow,
        string workspacePath,
        string operation)
    {
        if (flow.Id == Guid.Empty)
        {
            throw new InvalidOperationException($"{operation} requires a flow ID.");
        }
        return WorkspacePathGuard.ValidateExistingRoot(
            workspacePath,
            AuthorizedWorkspaceRoot(),
            operation);
    }

    private string AuthorizedWorkspaceRoot() =>
        Path.GetFullPath(
            workflowProvider.GetEffective().Config.Workspace.ResolvedRoot);

    private string ResolveMaterializationRoot(
        FlowRun flow,
        AdvisoryArtifactMaterializationPolicy policy)
    {
        var workspace = ValidateWorkspace(
            flow,
            flow.WorkspacePath,
            "Advisory artifacts");
        return ResolveRelativeDirectory(
            workspace,
            policy.MaterializationDirectory);
    }

    private static string ResolveRelativeDirectory(
        string workspace,
        string relativeDirectory)
    {
        var root = SplitRelative(relativeDirectory)
            .Aggregate(workspace, Path.Combine);
        var resolved = Path.GetFullPath(root);
        if (!IsContained(workspace, resolved) || PathsEqual(workspace, resolved))
        {
            throw new InvalidOperationException(
                "The Advisory artifact directory escaped the flow workspace.");
        }
        return resolved;
    }

    private static string NormalizeRelativeArtifactDirectory(string value) =>
        string.Join("\\", SplitRelative(value));

    private static string ResolveArtifactFile(string root, string relativePath)
    {
        var normalized = NormalizeArtifactPath(relativePath);
        var path = SplitRelative(normalized).Aggregate(root, Path.Combine);
        var resolved = Path.GetFullPath(path);
        if (!IsContained(root, resolved) || PathsEqual(root, resolved))
        {
            throw new InvalidOperationException(
                "Advisory artifact path escaped its materialization directory.");
        }
        return resolved;
    }

    private static string NormalizeArtifactPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Contains(':', StringComparison.Ordinal) ||
            relativePath.StartsWith('\\') ||
            relativePath.StartsWith('/') ||
            relativePath.Contains("\\\\", StringComparison.Ordinal) ||
            relativePath.Contains("//", StringComparison.Ordinal) ||
            relativePath.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                "Advisory artifact paths must be safe relative paths.");
        }
        var rawSegments = relativePath.Split(
            ['\\', '/'],
            StringSplitOptions.None);
        var segments = SplitRelative(relativePath);
        if (segments.Length == 0 ||
            rawSegments.Any(segment => segment.Length == 0) ||
            segments.Any(segment =>
                segment is "." or ".." ||
                !string.Equals(
                    segment,
                    segment.TrimEnd(' ', '.'),
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Advisory artifact paths must not contain empty or traversal segments.");
        }
        return string.Join("/", segments);
    }

    private static string[] SplitRelative(string value) =>
        value.Split(
            ['\\', '/'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private string MetadataRoot()
    {
        var configuredRoot = AuthorizedWorkspaceRoot();
        Directory.CreateDirectory(configuredRoot);
        WorkspacePathGuard.ValidateAuthorizedRoot(
            configuredRoot,
            "Advisory metadata");
        var root = Path.Combine(configuredRoot, MetadataDirectoryName);
        Directory.CreateDirectory(root);
        RejectReparse(root, "Advisory metadata directory");
        return root;
    }

    private string MetadataPath(Guid flowId) =>
        Path.Combine(MetadataRoot(), $"{flowId:N}.json");

    private async Task<AdvisoryWorkspaceMetadata?> ReadMetadataAsync(
        Guid flowId,
        CancellationToken cancellationToken)
    {
        var path = MetadataPath(flowId);
        if (!File.Exists(path))
        {
            return null;
        }
        RejectReparse(path, "Advisory workspace metadata");
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16_384,
                useAsync: true);
            return await JsonSerializer.DeserializeAsync<AdvisoryWorkspaceMetadata>(
                       stream,
                       MetadataJsonOptions,
                       cancellationToken)
                   ?? throw new InvalidOperationException(
                       "Advisory workspace metadata is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Advisory workspace metadata is invalid.",
                exception);
        }
    }

    private AdvisoryWorkspaceMetadata? ReadMetadata(Guid flowId)
    {
        var path = MetadataPath(flowId);
        if (!File.Exists(path))
        {
            return null;
        }
        RejectReparse(path, "Advisory workspace metadata");
        try
        {
            return JsonSerializer.Deserialize<AdvisoryWorkspaceMetadata>(
                       File.ReadAllText(path),
                       MetadataJsonOptions)
                   ?? throw new InvalidOperationException(
                       "Advisory workspace metadata is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Advisory workspace metadata is invalid.",
                exception);
        }
    }

    private async Task WriteMetadataAsync(
        AdvisoryWorkspaceMetadata metadata,
        CancellationToken cancellationToken)
    {
        var target = MetadataPath(metadata.FlowId);
        var temporary = Path.Combine(
            Path.GetDirectoryName(target)!,
            $"{Path.GetFileName(target)}.{Guid.NewGuid():N}.writing");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16_384,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    metadata,
                    MetadataJsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task WriteOwnershipMarkerAsync(
            string stagingWorkspace,
            GuardedSnapshotMarker marker,
            CancellationToken cancellationToken)
    {
        var markerPath = Path.Combine(
            stagingWorkspace,
            GuardedSnapshotMarkerFileName);
        await using var stream = new FileStream(
            markerPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4_096,
            FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(
            stream,
            marker,
            DurableJsonOptions,
            cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<bool> HasMatchingOwnershipMarkerAsync(
            FlowRun flow,
            string workspace,
            AdvisoryWorkspaceMetadata metadata,
            CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(metadata.OwnershipNonce))
        {
            return false;
        }
        var markerPath = Path.Combine(
            workspace,
            GuardedSnapshotMarkerFileName);
        if (!File.Exists(markerPath))
        {
            return false;
        }
        RejectReparse(markerPath, "Guarded snapshot ownership marker");
        try
        {
            await using var stream = new FileStream(
                markerPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4_096,
                useAsync: true);
            var marker = await JsonSerializer.DeserializeAsync<GuardedSnapshotMarker>(
                stream,
                DurableJsonOptions,
                cancellationToken);
            return marker is not null &&
                   marker.FlowId == flow.Id &&
                   PathsEqual(marker.SourcePath, flow.RepositoryPath) &&
                   PathsEqual(marker.WorkspacePath, workspace) &&
                   string.Equals(
                       marker.OwnershipNonce,
                       metadata.OwnershipNonce,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       marker.BaselineDigest,
                       metadata.Baseline?.Digest,
                       StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ValidateMetadata(
        AdvisoryWorkspaceMetadata metadata,
        FlowRun flow,
        string workspace)
    {
        if (metadata.FlowId != flow.Id ||
            !PathsEqual(metadata.WorkspacePath, workspace) ||
            !string.IsNullOrWhiteSpace(metadata.SourcePath) &&
            !PathsEqual(metadata.SourcePath, flow.RepositoryPath) ||
            string.IsNullOrWhiteSpace(metadata.ArtifactDirectory) ||
            metadata.Materializations.Any(item =>
                item.Iteration < 1 ||
                !OutcomeVerificationRules.IsSha256(item.OutcomeHash) ||
                string.IsNullOrWhiteSpace(item.RelativeDirectory) ||
                item.Artifacts is null ||
                item.Artifacts.Any(artifact =>
                    string.IsNullOrWhiteSpace(artifact.Path) ||
                    artifact.ByteLength < 0 ||
                    !OutcomeVerificationRules.IsSha256(
                        artifact.ContentDigest))))
        {
            throw new InvalidOperationException(
                "Advisory workspace metadata does not match this flow.");
        }
        if (string.IsNullOrWhiteSpace(metadata.SourcePath))
        {
            metadata.SourcePath = Path.GetFullPath(flow.RepositoryPath);
        }
    }

    private static AdvisoryWorkspaceEvidence ToEvidence(
        AdvisoryWorkspaceMetadata metadata) =>
        new(
            metadata.Baseline!.Digest,
            metadata.Baseline.FileCount,
            metadata.Baseline.TotalBytes,
            metadata.CapturedAt);

    private static bool BaselineMatches(
        AdvisorySourceBaseline expected,
        AdvisorySourceBaseline actual) =>
        string.Equals(expected.Digest, actual.Digest, StringComparison.Ordinal) &&
        expected.FileCount == actual.FileCount &&
        expected.TotalBytes == actual.TotalBytes;

    private static void EnsureNoReparseAncestors(string workspace, string path)
    {
        var root = Path.GetFullPath(workspace);
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, path).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) || File.Exists(current))
            {
                RejectReparse(current, "Advisory artifact path");
            }
        }
    }

    private static bool HasReparseComponent(string root, string path)
    {
        var current = Path.GetFullPath(root);
        foreach (var segment in Path.GetRelativePath(current, path).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }
        return false;
    }

    private static void RejectReparse(string path, string label)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"{label} cannot be a symbolic link, junction, or reparse point: {path}");
        }
    }

    private static bool IsContained(string root, string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(candidate));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(fullRoot, fullCandidate, comparison) ||
               fullCandidate.StartsWith(
                   fullRoot + Path.DirectorySeparatorChar,
                   comparison);
    }

    private static bool IsContainedOrEqual(string root, string candidate) =>
        IsContained(root, candidate);

    private static string ValidateContainedPath(
        string root,
        string candidate,
        string operation)
    {
        var resolved = Path.GetFullPath(candidate);
        if (!IsContained(root, resolved) || PathsEqual(root, resolved))
        {
            throw new UnauthorizedAccessException(
                $"{operation} escaped the authorized workspace root.");
        }
        return resolved;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static string NormalizeRelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static AdvisoryArtifactMaterializationPolicy
        DeserializeMaterialization(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumMaterializationDataBytes)
        {
            throw new InvalidOperationException(
                "The durable Advisory materialization policy is oversized.");
        }
        try
        {
            var policy =
                JsonSerializer.Deserialize<AdvisoryArtifactMaterializationPolicy>(
                    json,
                    DurableJsonOptions)
                ?? throw new InvalidOperationException(
                    "The durable Advisory materialization policy is empty.");
            ValidateMaterializationPolicy(policy, flow: null);
            return policy;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The durable Advisory materialization policy is invalid.",
                exception);
        }
    }

    private static void ValidateMaterializationPolicy(
        AdvisoryArtifactMaterializationPolicy policy,
        FlowRun? flow)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.FlowId == Guid.Empty ||
            policy.Iteration < 1 ||
            !OutcomeVerificationRules.IsSha256(policy.OutcomeHash) ||
            string.IsNullOrWhiteSpace(policy.ArtifactDirectory) ||
            string.IsNullOrWhiteSpace(policy.MaterializationDirectory) ||
            policy.MaximumArtifactCount is < 0 or > 100 ||
            policy.MaximumTotalArtifactBytes is < 0 or > 100 * 1024 * 1024 ||
            policy.Artifacts is null ||
            policy.Artifacts.Count > policy.MaximumArtifactCount ||
            policy.Artifacts.Sum(item => (long)item.ByteLength) >
                policy.MaximumTotalArtifactBytes ||
            policy.Artifacts.Any(item =>
                string.IsNullOrWhiteSpace(item.Path) ||
                string.IsNullOrWhiteSpace(item.MediaType) ||
                item.ByteLength < 0 ||
                !OutcomeVerificationRules.IsSha256(item.ContentDigest)) ||
            policy.Artifacts
                .Select(item => item.Path)
                .Distinct(StringComparer.Ordinal)
                .Count() != policy.Artifacts.Count)
        {
            throw new InvalidOperationException(
                "The durable Advisory materialization policy is invalid.");
        }
        _ = NormalizeRelativeArtifactDirectory(policy.ArtifactDirectory);
        _ = NormalizeRelativeArtifactDirectory(policy.MaterializationDirectory);
        foreach (var artifact in policy.Artifacts)
        {
            _ = NormalizeArtifactPath(artifact.Path);
        }
        var normalizedArtifactDirectory =
            NormalizeRelativeArtifactDirectory(policy.ArtifactDirectory);
        var normalizedMaterializationDirectory =
            NormalizeRelativeArtifactDirectory(policy.MaterializationDirectory);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!normalizedMaterializationDirectory.StartsWith(
                normalizedArtifactDirectory + "\\",
                comparison))
        {
            throw new InvalidOperationException(
                "The Advisory materialization directory is outside its persisted artifact directory.");
        }
        if (flow is not null &&
            (flow.Kind != FlowKind.Advisory ||
             policy.FlowId != flow.Id ||
             policy.Iteration != flow.Iteration ||
             !string.Equals(
                 policy.OutcomeHash,
                 OutcomeVerificationRules.ComputeSha256(
                     flow.OutcomeContractJson),
                 StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The Advisory materialization policy belongs to a different flow outcome.");
        }
    }

    private static IReadOnlyList<string> ResolveRegisteredMaterializationRoots(
        string workspace,
        AdvisoryWorkspaceMetadata metadata)
    {
        var roots = new List<string>();
        foreach (var materialization in metadata.Materializations)
        {
            var root = ResolveRelativeDirectory(
                workspace,
                materialization.RelativeDirectory);
            ValidateRegisteredMaterialization(root, materialization);
            roots.Add(root);
        }
        return roots.Distinct(PathComparer()).ToArray();
    }

    private static void ValidateRegisteredMaterialization(
        string root,
        RegisteredMaterialization materialization)
    {
        if (File.Exists(root))
        {
            throw new InvalidOperationException(
                "A registered Advisory materialization collides with a file.");
        }
        if (!Directory.Exists(root))
        {
            return;
        }
        RejectReparse(root, "Registered Advisory materialization");
        var expected = materialization.Artifacts
            .ToDictionary(item => item.Path, StringComparer.Ordinal);
        var actualPaths = EnumerateMaterializedFiles(root)
            .Select(path => NormalizeRelativePath(root, path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!actualPaths.SequenceEqual(
                expected.Keys.Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "A registered Advisory materialization contains unexpected content.");
        }
        foreach (var relativePath in actualPaths)
        {
            var file = ResolveArtifactFile(root, relativePath);
            var bytes = File.ReadAllBytes(file);
            var identity = expected[relativePath];
            var digest = "sha256:" +
                         Convert.ToHexString(SHA256.HashData(bytes))
                             .ToLowerInvariant();
            if (bytes.Length != identity.ByteLength ||
                !string.Equals(
                    digest,
                    identity.ContentDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Registered Advisory artifact '{relativePath}' no longer matches its host identity.");
            }
        }
    }

    private sealed class AdvisoryWorkspaceMetadata
    {
        public Guid FlowId { get; set; }

        public string WorkspacePath { get; set; } = string.Empty;

        public string SourcePath { get; set; } = string.Empty;

        public WorkspaceMode Mode { get; set; }

        public string ArtifactDirectory { get; set; } = string.Empty;

        public string OwnershipNonce { get; set; } = string.Empty;

        public AdvisorySourceBaseline? Baseline { get; set; }

        public List<RegisteredMaterialization> Materializations { get; set; } = [];

        public DateTimeOffset CapturedAt { get; set; }
    }

    private sealed class RegisteredMaterialization
    {
        public int Iteration { get; set; }

        public string OutcomeHash { get; set; } = string.Empty;

        public string RelativeDirectory { get; set; } = string.Empty;

        public IReadOnlyList<AdvisoryArtifactIdentity> Artifacts { get; set; } = [];
    }

    private sealed class GuardedSnapshotMarker
    {
        public Guid FlowId { get; set; }

        public string SourcePath { get; set; } = string.Empty;

        public string WorkspacePath { get; set; } = string.Empty;

        public string OwnershipNonce { get; set; } = string.Empty;

        public string BaselineDigest { get; set; } = string.Empty;
    }

    private sealed class AdvisorySourceBaseline
    {
        public string Digest { get; set; } = string.Empty;

        public int FileCount { get; set; }

        public long TotalBytes { get; set; }

        public IReadOnlyList<AdvisorySourceFile> Files { get; set; } = [];
    }

    private sealed record AdvisorySourceFile(
        string Path,
        long Length,
        string Sha256);
}

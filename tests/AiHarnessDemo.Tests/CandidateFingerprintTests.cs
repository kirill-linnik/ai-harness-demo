using System.Diagnostics;
using System.Text;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Data;
using AiHarnessDemo.Infrastructure;
using AiHarnessDemo.Services;
using AiHarnessDemo.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiHarnessDemo.Tests;

public sealed class CandidateFingerprintTests
{
    [Fact]
    public async Task SelectedFolderCandidate_RejectsCommittedChangesOutsideFolder()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory, "candidate-tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var workspace = Path.Combine(root, "workspace");
        CandidateWorkspace.InitializeRepositoryAt(source);
        Directory.CreateDirectory(Path.Combine(source, "application"));
        await File.WriteAllTextAsync(
            Path.Combine(source, "application", "app.txt"), "baseline");
        var git = new ProcessRunner();
        async Task RunAsync(string directory, params string[] args)
        {
            var result = await git.RunAsync(
                "git", ["-C", directory, .. args], directory,
                TimeSpan.FromSeconds(30));
            Assert.True(result.ExitCode == 0, result.CombinedOutput);
        }
        await RunAsync(source, "add", ".");
        await RunAsync(source, "commit", "--quiet", "-m", "Add application");
        await RunAsync(source, "worktree", "add", "-b", "scope-flow", workspace);
        var flow = new FlowRun
        {
            Title = "Scoped candidate",
            OriginalRequest = "Change the application",
            RepositoryPath = Path.Combine(source, "application"),
            WorkspacePath = workspace,
            BranchName = "scope-flow",
            Outcome = OutcomeType.Commit,
            Iteration = 1
        };
        RecordTrustedRepositories(flow, ["."]);
        var baseline = await git.RunAsync(
            "git", ["-C", source, "rev-parse", "HEAD"],
            source, TimeSpan.FromSeconds(30));
        Assert.Equal(0, baseline.ExitCode);
        flow.Events.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            Type = WorkspaceSourceScopeLedger.EventType,
            Message = "Selected folder scope.",
            DataJson = WorkspaceSourceScopeLedger.Serialize(
                new WorkspaceInfo(
                    workspace, "scope-flow", true,
                    SourceScopeRelativePath: "application",
                    SourceBaselineCommit: baseline.StandardOutput.Trim()))
        });
        var service = new CandidateFingerprintService(git, TimeProvider.System);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "application", "app.txt"), "changed");
            await RunAsync(workspace, "add", ".");
            await RunAsync(workspace, "commit", "--quiet", "-m", "In scope");
            var accepted = await service.PrepareAsync(
                flow, Digest('a'), Guid.NewGuid(), requiresPreview: false);
            Assert.Single(accepted.Manifest.Repositories);

            await File.WriteAllTextAsync(
                Path.Combine(workspace, "tracked.txt"), "out of scope");
            var unsealed = await Assert.ThrowsAsync<CandidateValidationException>(
                () => service.SealAsync(flow));
            Assert.Contains("outside the selected project folder", unsealed.Message);
            await RunAsync(workspace, "add", ".");
            await RunAsync(workspace, "commit", "--quiet", "-m", "Outside folder");
            var error = await Assert.ThrowsAsync<CandidateValidationException>(
                () => service.PrepareAsync(
                    flow, Digest('a'), Guid.NewGuid(), requiresPreview: false));
            Assert.Contains("outside the selected project folder", error.Message);
        }
        finally
        {
            await RunAsync(source, "worktree", "remove", "--force", workspace);
            ClearAndDelete(root);
        }
    }

    [Fact]
    public async Task LegacyPreviewReconstruction_UsesSealedRepositoryIdentity()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"reviewed-preview-reconstruction-{Guid.NewGuid():N}");
        var repository = Path.Combine(root, "repo");
        var previewRoot = Path.Combine(
            root,
            ".customer-preview",
            "eu");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        Directory.CreateDirectory(previewRoot);
        var previewPath = Path.Combine(previewRoot, "index.html");
        var previewBytes = Encoding.UTF8.GetBytes(
            "<h1>legacy reviewed preview</h1>");
        await File.WriteAllBytesAsync(previewPath, previewBytes);
        var flow = new FlowRun
        {
            Title = "Legacy preview",
            OriginalRequest = "Preserve the preview.",
            ConsolidatedRequest = "Preserve the preview.",
            Kind = FlowKind.Delivery,
            Status = FlowStatus.WaitingForFeedback,
            Iteration = 1,
            WorkspacePath = root,
            RepositoryPath = root,
            OutcomeOwnerPlanStepKey = "outcome",
            OutcomeContractJson =
                """{"Goal":"Preview","Summary":"Ready","ImplementationDetails":[],"Artifacts":[]}"""
        };
        flow.Events.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            Type = StudioWorkspaceRepositoryMapLedger.EventType,
            Message = "Trusted repository map.",
            DataJson = StudioWorkspaceRepositoryMapLedger.Serialize(
                StudioWorkspaceRepositoryMapLedger.Create(
                    flow,
                    root,
                    [new WorkspaceRepositoryIdentity("repo", "example/repo")]))
        });
        var ownerStepId = Guid.NewGuid();
        var acceptancePlanHash = OutcomeVerificationRules.ComputeSha256(
            $"plan:{flow.Id:D}");
        var previewDigest =
            "sha256:" +
            Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(previewBytes))
                .ToLowerInvariant();
        var repositories =
            new[]
            {
                new CandidateRepositoryManifest(
                    "repo",
                    new string('1', 40),
                    new string('2', 40),
                    "example/repo")
            };
        var previewArtifacts =
            new[]
            {
                new CandidatePreviewArtifact(
                    ".customer-preview/eu/index.html",
                    previewBytes.LongLength,
                    previewDigest)
            };
        var manifest = new CandidateManifest(
            flow.Iteration,
            acceptancePlanHash,
            repositories,
            [],
            previewArtifacts);
        var identity = new ReviewedCandidateIdentity(
            flow.Id,
            flow.Iteration,
            ownerStepId,
            "outcome",
            OutcomeVerificationRules.ComputeSha256(
                flow.OutcomeContractJson),
            acceptancePlanHash,
            OutcomeVerificationRules.HashCandidateManifest(manifest),
            0,
            0,
            previewArtifacts.Length,
            previewArtifacts.Sum(item => item.Length),
            [
                new ReviewedCandidateRepositoryIdentity(
                    "repo",
                    repositories[0].Head,
                    repositories[0].Tree,
                    repositories[0].RemoteRepository)
            ],
            DateTimeOffset.UtcNow);
        var fingerprints = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(repository, "changed-after-review.txt"),
                "This mutable repository byte is not served by the preview.");
            var reconstructed = await fingerprints.ReconstructPreviewAsync(
                flow,
                identity);
            Assert.Equal(identity.Fingerprint, reconstructed.Fingerprint);

            await File.WriteAllTextAsync(
                previewPath,
                "<h1>changed preview</h1>");
            await Assert.ThrowsAsync<CandidateValidationException>(() =>
                fingerprints.ReconstructPreviewAsync(flow, identity));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewedPreviewStore_PersistsImmutablePreviewBytes(bool includeRootMetadata)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"reviewed-preview-store-{Guid.NewGuid():N}");
        var previewRoot = Path.Combine(
            root,
            ".customer-preview",
            "eu");
        Directory.CreateDirectory(previewRoot);
        var previewPath = Path.Combine(previewRoot, "index.html");
        var content = Encoding.UTF8.GetBytes(
            "<h1>sealed durable preview</h1>");
        await File.WriteAllBytesAsync(previewPath, content);
        var metadata = Encoding.UTF8.GetBytes("Preview generation instructions");
        if (includeRootMetadata)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(root, ".customer-preview", "README.md"), metadata);
        }
        var flow = new FlowRun
        {
            Title = "Durable preview",
            OriginalRequest = "Persist the preview.",
            ConsolidatedRequest = "Persist the preview.",
            Kind = FlowKind.Delivery,
            Status = FlowStatus.WaitingForFeedback,
            Iteration = 1,
            WorkspacePath = root,
            RepositoryPath = root,
            OutcomeOwnerPlanStepKey = "outcome",
            OutcomeContractJson =
                """{"Goal":"Preview","Summary":"Ready","ImplementationDetails":[],"Artifacts":[]}"""
        };
        var ownerStepId = Guid.NewGuid();
        var outcomeHash = OutcomeVerificationRules.ComputeSha256(
            flow.OutcomeContractJson);
        var acceptancePlanHash = OutcomeVerificationRules.ComputeSha256(
            $"plan:{flow.Id:D}");
        var fingerprint = OutcomeVerificationRules.ComputeSha256(
            $"candidate:{flow.Id:D}");
        var digest =
            "sha256:" +
            Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(content))
                .ToLowerInvariant();
        var identity = new ReviewedCandidateIdentity(
            flow.Id,
            flow.Iteration,
            ownerStepId,
            "outcome",
            outcomeHash,
            acceptancePlanHash,
            fingerprint,
            0,
            0,
            includeRootMetadata ? 2 : 1,
            content.LongLength + (includeRootMetadata ? metadata.LongLength : 0),
            [
                new ReviewedCandidateRepositoryIdentity(
                    ".",
                    new string('1', 40),
                    new string('2', 40),
                    "example/repository")
            ],
            DateTimeOffset.UtcNow);
        var previewArtifact = new CandidatePreviewArtifact(
            ".customer-preview/eu/index.html",
            content.LongLength,
            digest);
        var previewArtifacts = new List<CandidatePreviewArtifact> { previewArtifact };
        if (includeRootMetadata)
        {
            previewArtifacts.Add(new CandidatePreviewArtifact(
                ".customer-preview/README.md",
                metadata.LongLength,
                "sha256:" + Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(metadata)).ToLowerInvariant()));
        }
        var snapshot = new OutcomeCandidateSnapshot(
            new CandidateManifest(
                flow.Iteration,
                acceptancePlanHash,
                [
                    new CandidateRepositoryManifest(
                        ".",
                        new string('1', 40),
                        new string('2', 40),
                        "example/repository")
                ],
                [],
                previewArtifacts),
            fingerprint,
            ownerStepId,
            DateTimeOffset.UtcNow);

        try
        {
            await using var connection =
                new Microsoft.Data.Sqlite.SqliteConnection(
                    "Data Source=:memory:");
            await connection.OpenAsync();
            var factory = new CandidateDbContextFactory(
                new DbContextOptionsBuilder<HarnessDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await using (var database = await factory.CreateDbContextAsync())
            {
                await database.Database.EnsureCreatedAsync();
                database.Flows.Add(flow);
                await database.SaveChangesAsync();
            }
            var previewStore = new ReviewedPreviewStore(
                factory,
                TimeProvider.System);

            await previewStore.PersistAsync(flow, identity, snapshot);
            await File.WriteAllTextAsync(
                previewPath,
                "<h1>changed workspace preview</h1>");

            await using var verification =
                await factory.CreateDbContextAsync();
            var stored = await verification.ReviewedPreviewArtifacts
                .AsNoTracking()
                .SingleAsync(item => item.RelativePath == previewArtifact.RelativePath);
            Assert.Equal(identity.Fingerprint, stored.CandidateFingerprint);
            Assert.Equal(
                "<h1>sealed durable preview</h1>",
                Encoding.UTF8.GetString(stored.Content));
            var artifacts = await verification.ReviewedPreviewArtifacts
                .AsNoTracking()
                .Select(item => new ReviewedPreviewArtifactMetadata(
                    item.RelativePath, item.Length, item.Digest))
                .ToListAsync();
            Assert.Equal(includeRootMetadata ? 2 : 1, artifacts.Count);
            Assert.Equal("eu", Assert.Single(
                new PreviewArtifactCatalog().DescribeStored(flow.Id, artifacts)).Id);
            if (includeRootMetadata)
            {
                var storedMetadata = await verification.ReviewedPreviewArtifacts
                    .AsNoTracking()
                    .SingleAsync(item => item.RelativePath == ".customer-preview/README.md");
                Assert.Equal(metadata, storedMetadata.Content);
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                    previewStore.ReadAsync(
                        new ReviewedPreviewSnapshot(identity, artifacts, Materialized: true),
                        "eu", "../README.md"));
            }

            stored.Content = Encoding.UTF8.GetBytes(
                "<h1>tampered durable preview</h1>");
            verification.Entry(stored).State = EntityState.Modified;
            await verification.SaveChangesAsync();
            var durableSnapshot = new ReviewedPreviewSnapshot(
                identity,
                [
                    new ReviewedPreviewArtifactMetadata(
                        stored.RelativePath,
                        stored.Length,
                        stored.Digest)
                ],
                Materialized: true);
            await Assert.ThrowsAsync<CandidateValidationException>(() =>
                previewStore.ReadAsync(
                    durableSnapshot,
                    "eu",
                    "index.html"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReviewedCandidate_SealsPersistsAndRejectsPostReviewByteChanges()
    {
        using var workspace = CandidateWorkspace.Create();
        var flow = workspace.Flow;
        ConfigureReviewedFlow(flow);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "tracked.txt"),
            "reviewed bytes");
        var ownerStepId = Guid.NewGuid();
        var service = new ReviewedCandidateService(
            new CandidateFingerprintService(
                new ProcessRunner(),
                TimeProvider.System));

        var identity = await service.SealAsync(
            flow,
            ownerStepId,
            "outcome",
            flow.OutcomeContractJson);
        var eventJson = ReviewedCandidateLedger.Serialize(identity);
        flow.Events.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = ownerStepId,
            Type = ReviewedCandidateLedger.EventType,
            Message = "Fixture reviewed seal.",
            DataJson = eventJson
        });

        var restartedIdentity = ReviewedCandidateLedger.Read(
            flow,
            ownerStepId);
        var verified = await new ReviewedCandidateService(
                new CandidateFingerprintService(
                    new ProcessRunner(),
                    TimeProvider.System))
            .VerifyAsync(flow, restartedIdentity);
        Assert.Equal(identity.Fingerprint, verified.Fingerprint);
        Assert.Equal(
            "reviewed bytes",
            await File.ReadAllTextAsync(
                Path.Combine(workspace.Root, "tracked.txt")));

        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "tracked.txt"),
            "unreviewed bytes");
        await Assert.ThrowsAsync<CandidateValidationException>(
            () => service.VerifyAsync(flow, identity));
    }

    [Fact]
    public async Task ReviewedPreviewVerification_IsSingleFlightAcrossConcurrentRequests()
    {
        using var workspace = CandidateWorkspace.Create();
        var flow = workspace.Flow;
        ConfigureReviewedFlow(flow);
        var fingerprints = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);
        var identity = await new ReviewedCandidateService(fingerprints)
            .SealAsync(
                flow,
                Guid.NewGuid(),
                "outcome",
                flow.OutcomeContractJson);
        var restartedService = new ReviewedCandidateService(fingerprints);

        var verifications = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => restartedService.VerifyPreviewAsync(
                    flow,
                    identity)));

        Assert.All(
            verifications,
            verification => Assert.Same(verifications[0], verification));
    }

    [Fact]
    public async Task ReviewedPreviewVerification_DoesNotCacheFailure()
    {
        using var workspace = CandidateWorkspace.Create();
        var flow = workspace.Flow;
        ConfigureReviewedFlow(flow);
        var fingerprints = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);
        var identity = await new ReviewedCandidateService(fingerprints)
            .SealAsync(
                flow,
                Guid.NewGuid(),
                "outcome",
                flow.OutcomeContractJson);
        var restartedService = new ReviewedCandidateService(fingerprints);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "tracked.txt"),
            "unreviewed bytes");

        await Assert.ThrowsAsync<CandidateValidationException>(
            () => restartedService.VerifyPreviewAsync(flow, identity));

        workspace.Git("restore", "tracked.txt");
        var verified = await restartedService.VerifyPreviewAsync(
            flow,
            identity);
        Assert.Equal(identity.Fingerprint, verified.Fingerprint);
    }

    [Fact]
    public async Task ReviewedPreviewCache_DoesNotBypassPublicationVerification()
    {
        using var workspace = CandidateWorkspace.Create();
        var flow = workspace.Flow;
        ConfigureReviewedFlow(flow);
        var service = new ReviewedCandidateService(
            new CandidateFingerprintService(
                new ProcessRunner(),
                TimeProvider.System));
        var identity = await service.SealAsync(
            flow,
            Guid.NewGuid(),
            "outcome",
            flow.OutcomeContractJson);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "tracked.txt"),
            "unreviewed bytes");

        var preview = await service.VerifyPreviewAsync(flow, identity);

        Assert.Equal(identity.Fingerprint, preview.Fingerprint);
        await Assert.ThrowsAsync<CandidateValidationException>(
            () => service.VerifyAsync(flow, identity));
    }

    [Fact]
    public async Task ReviewedCandidate_RequiresWorkspaceRootPreviewForObservableDelivery()
    {
        using var workspace = CandidateWorkspace.Create();
        var flow = workspace.Flow;
        ConfigureReviewedFlow(flow);
        RecordCustomerObservableAcceptancePlan(flow);
        var service = new ReviewedCandidateService(
            new CandidateFingerprintService(
                new ProcessRunner(),
                TimeProvider.System));

        var exception = await Assert.ThrowsAsync<CandidateValidationException>(
            () => service.SealAsync(
                flow,
                Guid.NewGuid(),
                "outcome",
                flow.OutcomeContractJson));
        Assert.Contains(".customer-preview", exception.Message);

        var preview = Path.Combine(
            workspace.Root,
            ".customer-preview",
            "eu");
        Directory.CreateDirectory(preview);
        await File.WriteAllTextAsync(
            Path.Combine(preview, "index.html"),
            "<h1>Reviewed delivery</h1>");

        var identity = await service.SealAsync(
            flow,
            Guid.NewGuid(),
            "outcome",
            flow.OutcomeContractJson);

        Assert.Equal(1, identity.PreviewFileCount);
        Assert.True(identity.PreviewTotalBytes > 0);
    }

    [Fact]
    public async Task Fingerprint_IsDeterministicAndOrdersMultipleRepositories()
    {
        using var workspace = CandidateWorkspace.Create(multipleRepositories: true);
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);

        var first = await service.PrepareAsync(
            workspace.Flow,
            Digest('a'),
            Guid.NewGuid(),
            requiresPreview: false);
        var second = await service.PrepareAsync(
            workspace.Flow,
            Digest('a'),
            Guid.NewGuid(),
            requiresPreview: false);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(
            ["a", "z"],
            first.Manifest.Repositories.Select(item => item.RelativePath));
    }

    [Fact]
    public async Task Fingerprint_ChangesForCommitAndPreviewContent()
    {
        using var workspace = CandidateWorkspace.Create();
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);
        var preview = Path.Combine(
            workspace.Root,
            ".customer-preview",
            "demo",
            "browser");
        Directory.CreateDirectory(preview);
        var index = Path.Combine(preview, "index.html");
        await File.WriteAllTextAsync(index, "<h1>one</h1>");
        var first = await service.PrepareAsync(
            workspace.Flow,
            Digest('a'),
            Guid.NewGuid(),
            requiresPreview: true);

        await File.WriteAllTextAsync(index, "<h1>two</h1>");
        var previewChanged = await service.PrepareAsync(
            workspace.Flow,
            Digest('a'),
            Guid.NewGuid(),
            requiresPreview: true);
        Assert.NotEqual(first.Fingerprint, previewChanged.Fingerprint);

        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "tracked.txt"),
            "second");
        workspace.Git("add", "-A");
        workspace.Git("commit", "-m", "second");
        var commitChanged = await service.PrepareAsync(
            workspace.Flow,
            Digest('a'),
            Guid.NewGuid(),
            requiresPreview: true);
        Assert.NotEqual(previewChanged.Fingerprint, commitChanged.Fingerprint);
    }

    [Fact]
    public async Task Candidate_RejectsDirtyTrackedAndUntrackedProductFiles()
    {
        using var workspace = CandidateWorkspace.Create();
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "tracked.txt"),
            "dirty");

        var dirty = await Assert.ThrowsAsync<CandidateValidationException>(() =>
            service.PrepareAsync(
                workspace.Flow,
                Digest('a'),
                Guid.NewGuid(),
                requiresPreview: false));
        Assert.Contains("tracked", dirty.Message, StringComparison.OrdinalIgnoreCase);

        workspace.Git("restore", "tracked.txt");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "product.tmp"),
            "untracked");
        var untracked = await Assert.ThrowsAsync<CandidateValidationException>(() =>
            service.PrepareAsync(
                workspace.Flow,
                Digest('a'),
                Guid.NewGuid(),
                requiresPreview: false));
        Assert.Contains("untracked product", untracked.Message);
    }

    [Fact]
    public async Task Candidate_RejectsAssumeUnchangedIndexFlags()
    {
        using var workspace = CandidateWorkspace.Create();
        workspace.Git("update-index", "--assume-unchanged", "tracked.txt");
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);

        var exception = await Assert.ThrowsAsync<CandidateValidationException>(() =>
            service.PrepareAsync(
                workspace.Flow,
                Digest('a'),
                Guid.NewGuid(),
                requiresPreview: false));

        Assert.Contains("non-default Git index flags", exception.Message);
        Assert.Contains("tracked.txt", exception.Message);
    }

    [Fact]
    public async Task Candidate_RejectsSkipWorktreeIndexFlags()
    {
        using var workspace = CandidateWorkspace.Create();
        workspace.Git("update-index", "--skip-worktree", "tracked.txt");
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);

        var exception = await Assert.ThrowsAsync<CandidateValidationException>(() =>
            service.PrepareAsync(
                workspace.Flow,
                Digest('a'),
                Guid.NewGuid(),
                requiresPreview: false));

        Assert.Contains("non-default Git index flags", exception.Message);
        Assert.Contains("tracked.txt", exception.Message);
    }

    [Fact]
    public async Task Candidate_RejectsTrackedByteDriftHiddenByAssumeUnchanged()
    {
        using var workspace = CandidateWorkspace.Create();
        workspace.Git("update-index", "--assume-unchanged", "tracked.txt");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "tracked.txt"),
            "drifted");
        Assert.True(string.IsNullOrWhiteSpace(
            workspace.GitOutput("status", "--porcelain=v1")));
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);

        var exception = await Assert.ThrowsAsync<CandidateValidationException>(() =>
            service.PrepareAsync(
                workspace.Flow,
                Digest('a'),
                Guid.NewGuid(),
                requiresPreview: false));

        Assert.Contains("tracked bytes", exception.Message);
        Assert.Contains("tracked.txt", exception.Message);
    }

    [Fact]
    public async Task Candidate_AllowsAndHashesGeneratedPreviewFiles()
    {
        using var workspace = CandidateWorkspace.Create();
        var preview = Path.Combine(
            workspace.Root,
            ".customer-preview",
            "eu",
            "browser");
        Directory.CreateDirectory(preview);
        await File.WriteAllTextAsync(
            Path.Combine(preview, "index.html"),
            "<h1>Preview</h1>");
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);

        var candidate = await service.PrepareAsync(
            workspace.Flow,
            Digest('a'),
            Guid.NewGuid(),
            requiresPreview: true);

        var artifact = Assert.Single(candidate.Manifest.PreviewArtifacts);
        Assert.Equal(
            ".customer-preview/eu/browser/index.html",
            artifact.RelativePath);
        Assert.True(OutcomeVerificationRules.IsSha256(artifact.Digest));
    }

    [Fact]
    public async Task Candidate_RejectsSymlinkEscapingWorkspace()
    {
        using var workspace = CandidateWorkspace.Create();
        var outside = Path.Combine(
            Path.GetTempPath(),
            $"candidate-outside-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outside, "outside");
        var link = Path.Combine(workspace.Root, "escape.txt");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException)
        {
            File.Delete(outside);
            return;
        }

        try
        {
            var service = new CandidateFingerprintService(
                new ProcessRunner(),
                TimeProvider.System);
            var exception = await Assert.ThrowsAsync<CandidateValidationException>(() =>
                service.PrepareAsync(
                    workspace.Flow,
                    Digest('a'),
                    Guid.NewGuid(),
                    requiresPreview: false));
            Assert.Contains("escapes", exception.Message);
        }
        finally
        {
            File.Delete(link);
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task Candidate_RejectsSymlinkEscapeInsideExcludedHarnessDirectory()
    {
        using var workspace = CandidateWorkspace.Create();
        var outside = Path.Combine(
            Path.GetTempPath(),
            $"candidate-harness-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        var harness = Path.Combine(workspace.Root, ".ai-harness");
        Directory.CreateDirectory(harness);
        var link = Path.Combine(harness, "escape");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException)
        {
            Directory.Delete(outside, recursive: true);
            return;
        }

        try
        {
            var service = new CandidateFingerprintService(
                new ProcessRunner(),
                TimeProvider.System);
            var exception = await Assert.ThrowsAsync<CandidateValidationException>(() =>
                service.PrepareAsync(
                    workspace.Flow,
                    Digest('a'),
                    Guid.NewGuid(),
                    requiresPreview: false));
            Assert.Contains("escapes", exception.Message);
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceRootReparsePoint_IsRejectedByCandidateQaAndPublication()
    {
        using var workspace = CandidateWorkspace.Create();
        var link = workspace.Root + "-root-link";
        if (!TryCreateDirectoryReparsePoint(link, workspace.Root))
        {
            return;
        }

        try
        {
            workspace.Flow.WorkspacePath = link;
            var service = new CandidateFingerprintService(
                new ProcessRunner(),
                TimeProvider.System);

            var candidateError = await Assert.ThrowsAsync<CandidateValidationException>(() =>
                service.PrepareAsync(
                    workspace.Flow,
                    Digest('a'),
                    Guid.NewGuid(),
                    requiresPreview: false));
            var publicationError = Assert.Throws<InvalidOperationException>(() =>
                VerifiedCandidatePublisher.ResolveWorkspaceRepository(link, "."));

            Assert.Contains("reparse point", candidateError.Message);
            Assert.Contains("reparse point", publicationError.Message);
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }
    }

    [Fact]
    public async Task Candidate_RejectsWorkspaceOutsideConfiguredAuthorizedRoot()
    {
        using var workspace = CandidateWorkspace.Create();
        var harnessRoot = Path.Combine(
            AppContext.BaseDirectory,
            "candidate-authority-tests",
            Guid.NewGuid().ToString("N"));
        var authorizedRoot = Path.Combine(harnessRoot, ".worktrees");
        Directory.CreateDirectory(authorizedRoot);
        File.WriteAllText(
            Path.Combine(harnessRoot, "WORKFLOW.md"),
            """
            ---
            workspace:
              root: .worktrees
            ---
            Prompt
            """);
        var paths = new HarnessPaths(
            harnessRoot,
            Path.Combine(harnessRoot, ".github", "agents"),
            Path.Combine(harnessRoot, "harness.db"));
        using var provider = new WorkflowDefinitionProvider(
            paths,
            new WorkflowLoader(),
            NullLogger<WorkflowDefinitionProvider>.Instance);

        try
        {
            await provider.StartAsync(CancellationToken.None);
            var service = new CandidateFingerprintService(
                new ProcessRunner(),
                TimeProvider.System,
                workflowProvider: provider);

            var exception = await Assert.ThrowsAsync<CandidateValidationException>(() =>
                service.PrepareAsync(
                    workspace.Flow,
                    Digest('a'),
                    Guid.NewGuid(),
                    requiresPreview: false));

            Assert.Contains("authorized root", exception.Message);
        }
        finally
        {
            await provider.StopAsync(CancellationToken.None);
            Directory.Delete(harnessRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Candidate_ExcludesIgnoredLocalConfiguration()
    {
        using var workspace = CandidateWorkspace.Create();
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, ".gitignore"),
            "appsettings.Production.json\n");
        workspace.Git("add", ".gitignore");
        workspace.Git("commit", "--quiet", "-m", "ignore product config");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "appsettings.Production.json"),
            """{"FeatureEnabled":true}""");
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);

        var candidate = await service.PrepareAsync(
                workspace.Flow,
                Digest('a'),
                Guid.NewGuid(),
                requiresPreview: false);

        Assert.Single(candidate.Manifest.Repositories);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "appsettings.Production.json"),
            """{"FeatureEnabled":false}""");
        Assert.True(await service.IsCurrentAsync(workspace.Flow, candidate, requiresPreview: false));
    }

    [Fact]
    public async Task Candidate_ExcludesIgnoredNestedOutput()
    {
        using var workspace = CandidateWorkspace.Create();
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, ".gitignore"),
            "config/dist/\n");
        workspace.Git("add", ".gitignore");
        workspace.Git("commit", "--quiet", "-m", "ignore nested product output");
        var ignored = Path.Combine(workspace.Root, "config", "dist");
        Directory.CreateDirectory(ignored);
        await File.WriteAllTextAsync(
            Path.Combine(ignored, "runtime.json"),
            """{"RuntimeBehavior":"changed"}""");
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);

        var candidate = await service.PrepareAsync(
                workspace.Flow,
                Digest('a'),
                Guid.NewGuid(),
                requiresPreview: false);

        Assert.Single(candidate.Manifest.Repositories);
    }

    [Theory]
    [InlineData("bin/Debug/test.dll")]
    [InlineData(".playwright-browsers/chromium/debug.log")]
    [InlineData("test-results/retry/error-context.md")]
    [InlineData("playwright-report/index.html")]
    public async Task Candidate_AllowsDocumentedIgnoredBuildOutput(
        string relativePath)
    {
        using var workspace = CandidateWorkspace.Create();
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "sample.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk" />""");
        var topLevel = relativePath.Split('/')[0];
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, ".gitignore"),
            $"{topLevel}/{Environment.NewLine}");
        workspace.Git("add", ".gitignore", "sample.csproj");
        workspace.Git("commit", "--quiet", "-m", "ignore build output");
        var output = Path.Combine(
            workspace.Root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, "transient");
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);

        var candidate = await service.PrepareAsync(
            workspace.Flow,
            Digest('a'),
            Guid.NewGuid(),
            requiresPreview: false);

        Assert.Single(candidate.Manifest.Repositories);
    }

    [Theory]
    [InlineData("bin/generated.txt")]
    [InlineData("dist/generated.txt")]
    [InlineData("build/generated.txt")]
    [InlineData("custom-release/generated.txt")]
    [InlineData("dist-locale/generated.txt")]
    public async Task Candidate_RespectsIgnoredOutputWithoutGuessingProjectLayout(
        string relativePath)
    {
        using var workspace = CandidateWorkspace.Create();
        workspace.Git("config", "core.autocrlf", "false");
        var topLevel = relativePath.Split('/')[0];
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, ".gitignore"),
            $"{topLevel}/{Environment.NewLine}");
        workspace.Git("add", ".gitignore");
        workspace.Git("commit", "--quiet", "-m", "ignore ambiguous directory");
        var fullPath = Path.Combine(
            workspace.Root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "not proven generated");
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);

        await service.SealAsync(workspace.Flow);
        var candidate = await service.PrepareAsync(
                workspace.Flow,
                Digest('a'),
                Guid.NewGuid(),
                requiresPreview: false);

        Assert.Single(candidate.Manifest.Repositories);
        Assert.DoesNotContain(relativePath, workspace.GitOutput("ls-tree", "-r", "--name-only", "HEAD"));
    }

    [Fact]
    public async Task Candidate_EnforcesPreviewManifestFileLimit()
    {
        using var workspace = CandidateWorkspace.Create();
        var preview = Path.Combine(workspace.Root, ".customer-preview", "demo");
        Directory.CreateDirectory(preview);
        for (var index = 0; index <= CandidateFingerprintService.MaximumPreviewFiles; index++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(preview, $"{index:000}.txt"),
                "preview");
        }
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);

        var exception = await Assert.ThrowsAsync<CandidateValidationException>(() =>
            service.PrepareAsync(
                workspace.Flow,
                Digest('a'),
                Guid.NewGuid(),
                requiresPreview: true));

        Assert.Contains("limit", exception.Message);
    }

    [Fact]
    public async Task Candidate_RequiresPreviewForClickableBrief()
    {
        using var workspace = CandidateWorkspace.Create();
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);

        var exception = await Assert.ThrowsAsync<CandidateValidationException>(() =>
            service.PrepareAsync(
                workspace.Flow,
                Digest('a'),
                Guid.NewGuid(),
                requiresPreview: true));

        Assert.Contains(".customer-preview", exception.Message);
    }

    [Fact]
    public async Task Candidate_RejectsInvalidSealedDemoManifest()
    {
        using var workspace = CandidateWorkspace.Create();
        var preview = Path.Combine(
            workspace.Root,
            ".customer-preview",
            "eu");
        Directory.CreateDirectory(preview);
        await File.WriteAllTextAsync(
            Path.Combine(preview, "index.html"),
            "<h1>Preview</h1>");
        var manifestPath =
            Path.Combine(preview, "customer-demo.json");
        await File.WriteAllTextAsync(
            manifestPath,
            """
            {
              "ArtifactId": "eu",
              "LaunchProfile": "npm",
              "WorkingDirectory": "",
              "Arguments": ["run", "start", "--", "--host", "127.0.0.1", "--port", "{port}"],
              "HealthPath": "/",
              "StartupTimeoutSeconds": 120
            }
            """);
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);

        var timeout = await Assert.ThrowsAsync<CandidateValidationException>(
            () => service.PrepareAsync(
                workspace.Flow,
                Digest('a'),
                Guid.NewGuid(),
                requiresPreview: true));
        Assert.Contains(
            "StartupTimeoutSeconds must be an integer from 1 to 60",
            timeout.Message);

        await File.WriteAllTextAsync(
            manifestPath,
            """
            {
              "ArtifactId": "wrong",
              "LaunchProfile": "npm",
              "WorkingDirectory": "",
              "Arguments": ["run", "start", "--", "--host", "127.0.0.1", "--port", "{port}"],
              "HealthPath": "/",
              "StartupTimeoutSeconds": 60
            }
            """);
        var artifactId =
            await Assert.ThrowsAsync<CandidateValidationException>(
                () => service.PrepareAsync(
                    workspace.Flow,
                    Digest('a'),
                    Guid.NewGuid(),
                    requiresPreview: true));
        Assert.Contains(
            "ArtifactId must exactly match its preview variant directory",
            artifactId.Message);
    }

    [Fact]
    public async Task Candidate_RejectsPreviewFilesThatCustomerApiCannotServe()
    {
        using var workspace = CandidateWorkspace.Create();
        var previewRoot = Path.Combine(workspace.Root, ".customer-preview");
        Directory.CreateDirectory(previewRoot);
        await File.WriteAllTextAsync(
            Path.Combine(previewRoot, "index.html"),
            "<h1>Unscoped preview</h1>");
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);

        var exception = await Assert.ThrowsAsync<CandidateValidationException>(() =>
            service.PrepareAsync(
                workspace.Flow,
                Digest('a'),
                Guid.NewGuid(),
                requiresPreview: true));

        Assert.Contains("servable", exception.Message);
    }

    [Fact]
    public async Task Candidate_RejectsChangedFilesOutsideRepositoriesInProjectWorkspace()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "candidate-project-tests",
            Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workspace);
        try
        {
            CandidateWorkspace.InitializeRepositoryAt(Path.Combine(source, "repo"));
            CandidateWorkspace.InitializeRepositoryAt(Path.Combine(workspace, "repo"));
            await File.WriteAllTextAsync(
                Path.Combine(source, "docker-compose.yml"),
                "services: {}");
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "docker-compose.yml"),
                "services: {}");
            var flow = new FlowRun
            {
                Title = "Multi repository candidate",
                OriginalRequest = "Prepare it",
                RepositoryPath = source,
                WorkspacePath = workspace,
                Iteration = 1,
                Outcome = OutcomeType.Commit
            };
            RecordTrustedRepositories(flow, ["repo"]);
            var service = new CandidateFingerprintService(
                new ProcessRunner(),
                TimeProvider.System);
            _ = await service.PrepareAsync(
                flow,
                Digest('a'),
                Guid.NewGuid(),
                requiresPreview: false);

            await File.WriteAllTextAsync(
                Path.Combine(workspace, "docker-compose.yml"),
                "services:\n  changed: {}");
            var exception = await Assert.ThrowsAsync<CandidateValidationException>(() =>
                service.PrepareAsync(
                    flow,
                    Digest('a'),
                    Guid.NewGuid(),
                    requiresPreview: false));
            Assert.Contains("scaffold", exception.Message);
        }
        finally
        {
            ClearAndDelete(root);
        }
    }

    [Fact]
    public async Task Candidate_RejectsDeletionOfEveryTrustedScaffoldFile()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cand-sc-del-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workspace);
        try
        {
            CandidateWorkspace.InitializeRepositoryAt(Path.Combine(source, "repo-a"));
            CandidateWorkspace.InitializeRepositoryAt(Path.Combine(workspace, "repo-a"));
            CandidateWorkspace.InitializeRepositoryAt(Path.Combine(source, "repo-b"));
            CandidateWorkspace.InitializeRepositoryAt(Path.Combine(workspace, "repo-b"));
            foreach (var file in new[] { "solution.slnx", "Directory.Build.props" })
            {
                await File.WriteAllTextAsync(Path.Combine(source, file), file);
                await File.WriteAllTextAsync(Path.Combine(workspace, file), file);
            }
            var flow = MultiRepositoryFlow(source, workspace, "repo-a", "repo-b");
            var service = new CandidateFingerprintService(
                new ProcessRunner(),
                TimeProvider.System);
            var initial = await service.PrepareAsync(
                flow,
                Digest('a'),
                Guid.NewGuid(),
                requiresPreview: false);
            Assert.Equal(2, initial.Manifest.TrustedScaffoldFiles.Count);

            File.Delete(Path.Combine(workspace, "solution.slnx"));
            File.Delete(Path.Combine(workspace, "Directory.Build.props"));

            var exception = await Assert.ThrowsAsync<CandidateValidationException>(() =>
                service.PrepareAsync(
                    flow,
                    Digest('a'),
                    Guid.NewGuid(),
                    requiresPreview: false));
            Assert.Contains("scaffold path set", exception.Message);
        }
        finally
        {
            ClearAndDelete(root);
        }
    }

    [Fact]
    public async Task ScaffoldRecovery_RestoresMissingHostOwnedFileBeforeVerification()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cand-sc-restore-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workspace);
        try
        {
            CandidateWorkspace.InitializeRepositoryAt(
                Path.Combine(source, "repo"));
            CandidateWorkspace.InitializeRepositoryAt(
                Path.Combine(workspace, "repo"));
            var sourceScaffold =
                Path.Combine(source, "package-lock.json");
            var workspaceScaffold =
                Path.Combine(workspace, "package-lock.json");
            await File.WriteAllTextAsync(
                sourceScaffold,
                """{"lockfileVersion":3}""");
            await File.WriteAllTextAsync(
                workspaceScaffold,
                """{"lockfileVersion":3}""");
            var flow = MultiRepositoryFlow(source, workspace, "repo");
            var service = new CandidateFingerprintService(
                new ProcessRunner(),
                TimeProvider.System);

            File.Delete(workspaceScaffold);
            await Assert.ThrowsAsync<CandidateValidationException>(
                () => service.SealAsync(flow));

            var restorationIntentRecorded = false;
            Assert.True(
                await service.RestoreAndValidateTrustedScaffoldAsync(
                    flow,
                    _ =>
                    {
                        Assert.False(File.Exists(workspaceScaffold));
                        restorationIntentRecorded = true;
                        return Task.CompletedTask;
                    }));
            Assert.True(restorationIntentRecorded);

            Assert.Equal(
                await File.ReadAllTextAsync(sourceScaffold),
                await File.ReadAllTextAsync(workspaceScaffold));
            Assert.False(
                await service.RestoreAndValidateTrustedScaffoldAsync(
                    flow,
                    _ => throw new InvalidOperationException(
                        "A no-op restoration must not persist another intent.")));

            await File.WriteAllTextAsync(
                workspaceScaffold,
                """{"lockfileVersion":2}""");
            var exception =
                await Assert.ThrowsAsync<CandidateValidationException>(
                    () => service.SealAsync(flow));
            Assert.Contains(
                "changed an untracked project scaffold file",
                exception.Message);
        }
        finally
        {
            ClearAndDelete(root);
        }
    }

    [Fact]
    public async Task CandidateFingerprint_IncludesTrustedScaffoldDigest()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cand-sc-fp-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workspace);
        try
        {
            CandidateWorkspace.InitializeRepositoryAt(Path.Combine(source, "repo"));
            CandidateWorkspace.InitializeRepositoryAt(Path.Combine(workspace, "repo"));
            var sourceScaffold = Path.Combine(source, "solution.slnx");
            var workspaceScaffold = Path.Combine(workspace, "solution.slnx");
            await File.WriteAllTextAsync(sourceScaffold, "before");
            await File.WriteAllTextAsync(workspaceScaffold, "before");
            var flow = MultiRepositoryFlow(source, workspace, "repo");
            var service = new CandidateFingerprintService(
                new ProcessRunner(),
                TimeProvider.System);
            var before = await service.PrepareAsync(
                flow,
                Digest('a'),
                Guid.NewGuid(),
                requiresPreview: false);

            await File.WriteAllTextAsync(sourceScaffold, "after");
            await File.WriteAllTextAsync(workspaceScaffold, "after");
            var after = await service.PrepareAsync(
                flow,
                Digest('a'),
                Guid.NewGuid(),
                requiresPreview: false);

            Assert.NotEqual(before.Fingerprint, after.Fingerprint);
            Assert.NotEqual(
                Assert.Single(before.Manifest.TrustedScaffoldFiles).Digest,
                Assert.Single(after.Manifest.TrustedScaffoldFiles).Digest);
        }
        finally
        {
            ClearAndDelete(root);
        }
    }

    [Fact]
    public async Task ScaffoldMutationBetweenQaAndApprovalInvalidatesCandidate()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cand-sc-stale-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workspace);
        try
        {
            CandidateWorkspace.InitializeRepositoryAt(Path.Combine(source, "repo"));
            CandidateWorkspace.InitializeRepositoryAt(Path.Combine(workspace, "repo"));
            await File.WriteAllTextAsync(
                Path.Combine(source, "solution.slnx"),
                "verified");
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "solution.slnx"),
                "verified");
            var flow = MultiRepositoryFlow(source, workspace, "repo");
            var service = new CandidateFingerprintService(
                new ProcessRunner(),
                TimeProvider.System);
            var verified = await service.PrepareAsync(
                flow,
                Digest('a'),
                Guid.NewGuid(),
                requiresPreview: false);

            await File.WriteAllTextAsync(
                Path.Combine(workspace, "solution.slnx"),
                "mutated after QA");

            await Assert.ThrowsAsync<CandidateValidationException>(() =>
                service.IsCurrentAsync(
                    flow,
                    verified,
                    requiresPreview: false));
        }
        finally
        {
            ClearAndDelete(root);
        }
    }

    [Fact]
    public async Task Candidate_IgnoresTrustedSourceDirectoriesWorkspaceCreationOmits()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cand-sc-copy-policy-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workspace);
        try
        {
            CandidateWorkspace.InitializeRepositoryAt(Path.Combine(source, "repo"));
            CandidateWorkspace.InitializeRepositoryAt(Path.Combine(workspace, "repo"));
            var omittedDirectory = Path.Combine(source, "node_modules", "dependency");
            Directory.CreateDirectory(omittedDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(omittedDirectory, "package.json"),
                """{"name":"not-copied"}""");
            var flow = MultiRepositoryFlow(source, workspace, "repo");
            var service = new CandidateFingerprintService(
                new ProcessRunner(),
                TimeProvider.System);

            var candidate = await service.PrepareAsync(
                flow,
                Digest('a'),
                Guid.NewGuid(),
                requiresPreview: false);

            Assert.Empty(candidate.Manifest.TrustedScaffoldFiles);
        }
        finally
        {
            ClearAndDelete(root);
        }
    }

    [Fact]
    public async Task Candidate_ExcludesConfiguredWorkspaceRootNestedInSourceProject()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cand-sc-worktrees-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var workspace = Path.Combine(source, ".worktrees", "flow");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workspace);
        try
        {
            CandidateWorkspace.InitializeRepositoryAt(Path.Combine(source, "repo"));
            CandidateWorkspace.InitializeRepositoryAt(Path.Combine(workspace, "repo"));
            await File.WriteAllTextAsync(
                Path.Combine(source, "solution.slnx"),
                "trusted scaffold");
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "solution.slnx"),
                "trusted scaffold");
            await File.WriteAllTextAsync(
                Path.Combine(source, ".worktrees", "prior-flow.txt"),
                "must not become scaffold");
            var flow = MultiRepositoryFlow(source, workspace, "repo");
            var service = new CandidateFingerprintService(
                new ProcessRunner(),
                TimeProvider.System);

            var candidate = await service.PrepareAsync(
                flow,
                Digest('a'),
                Guid.NewGuid(),
                requiresPreview: false);

            Assert.Equal(
                "solution.slnx",
                Assert.Single(candidate.Manifest.TrustedScaffoldFiles)
                    .RelativePath);
        }
        finally
        {
            ClearAndDelete(root);
        }
    }

    [Fact]
    public async Task Candidate_RejectsWorkspaceOnlyScaffoldFileUnderTransientNamedDirectory()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "candidate-project-transient-tests",
            Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workspace);
        try
        {
            CandidateWorkspace.InitializeRepositoryAt(Path.Combine(source, "repo"));
            CandidateWorkspace.InitializeRepositoryAt(Path.Combine(workspace, "repo"));
            var productConfig = Path.Combine(
                workspace,
                "config",
                "dist",
                "runtime.json");
            Directory.CreateDirectory(Path.GetDirectoryName(productConfig)!);
            await File.WriteAllTextAsync(
                productConfig,
                """{"RuntimeBehavior":"changed"}""");
            var flow = new FlowRun
            {
                Title = "Multi repository candidate",
                OriginalRequest = "Prepare it",
                RepositoryPath = source,
                WorkspacePath = workspace,
                Iteration = 1,
                Outcome = OutcomeType.Commit
            };
            RecordTrustedRepositories(flow, ["repo"]);
            var service = new CandidateFingerprintService(
                new ProcessRunner(),
                TimeProvider.System);

            var exception = await Assert.ThrowsAsync<CandidateValidationException>(() =>
                service.PrepareAsync(
                    flow,
                    Digest('a'),
                    Guid.NewGuid(),
                    requiresPreview: false));

            Assert.Contains("scaffold", exception.Message);
            Assert.Contains("config/dist/runtime.json", exception.Message);
        }
        finally
        {
            ClearAndDelete(root);
        }
    }

    [Theory]
    [InlineData(".playwright-mcp/console-2026-10-03T18-57-40-232Z.log")]
    [InlineData(".playwright-mcp/page-2026-10-03T18-57-40-640Z.yml")]
    [InlineData(".playwright-mcp/screenshot.png")]
    [InlineData(".playwright-browsers/chromium/debug.log")]
    [InlineData("baseline-mobile.png")]
    [InlineData("unknown-mcp/native-output.custom")]
    [InlineData("config/dist/runtime.json")]
    public async Task ExecutionArtifacts_DoNotChangeMultiRepositoryCandidateOrBlockRecovery(
        string relativePath)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cand-browser-diagnostics-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workspace);
        try
        {
            CandidateWorkspace.InitializeRepositoryAt(Path.Combine(source, "repo"));
            CandidateWorkspace.InitializeRepositoryAt(Path.Combine(workspace, "repo"));
            foreach (var directory in new[] { source, workspace })
            {
                await File.WriteAllTextAsync(
                    Path.Combine(directory, "solution.slnx"),
                    "trusted scaffold");
            }
            var flow = MultiRepositoryFlow(source, workspace, "repo");
            var factory = new CandidateDbContextFactory(
                new DbContextOptionsBuilder<HarnessDbContext>()
                    .UseSqlite($"Data Source={Path.Combine(root, "artifacts.db")};Pooling=False")
                    .Options);
            await using (var database = await factory.CreateDbContextAsync())
            {
                await database.Database.EnsureCreatedAsync();
                database.Flows.Add(flow);
                await database.SaveChangesAsync();
            }
            var service = new CandidateFingerprintService(
                new ProcessRunner(),
                TimeProvider.System,
                databaseFactory: factory);
            var preparedBy = Guid.NewGuid();
            var before = await service.PrepareAsync(
                flow, Digest('a'), preparedBy, requiresPreview: false);
            var diagnostic = Path.Combine(
                workspace,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(diagnostic)!);
            await File.WriteAllTextAsync(diagnostic, "generated by browser verification");

            Assert.False(await service.RestoreAndValidateTrustedScaffoldAsync(
                flow,
                _ => throw new InvalidOperationException(
                    "Browser diagnostics must not trigger scaffold restoration.")));
            _ = await service.SealAsync(flow);
            var after = await service.PrepareAsync(
                flow, Digest('a'), preparedBy, requiresPreview: false);
            Assert.Equal(before.Fingerprint, after.Fingerprint);
            Assert.Equal(
                "solution.slnx",
                Assert.Single(after.Manifest.TrustedScaffoldFiles).RelativePath);
            Assert.True(await service.IsCurrentAsync(
                flow, before, requiresPreview: false));

            Assert.True(File.Exists(diagnostic));
            await File.WriteAllTextAsync(diagnostic, "generated by browser verification");
            service = new CandidateFingerprintService(
                new ProcessRunner(), TimeProvider.System, databaseFactory: factory);
            Assert.False(await service.RestoreAndValidateTrustedScaffoldAsync(
                flow,
                _ => throw new InvalidOperationException(
                    "Replayed artifact cleanup must not trigger scaffold restoration.")));
            Assert.True(await service.IsCurrentAsync(
                flow, before, requiresPreview: false));
            await using var verification = await factory.CreateDbContextAsync();
            var artifact = Assert.Single(await verification.ExecutionArtifacts.ToListAsync());
            Assert.Equal(relativePath, artifact.RelativePath);
            Assert.True(artifact.ContentStored);
            Assert.Equal(
                "generated by browser verification",
                Encoding.UTF8.GetString(artifact.Content));
            Assert.Single(await verification.FlowEvents
                .Where(item => item.Type == "workspace.execution-artifact-archived")
                .ToListAsync());
            var download = Assert.IsAssignableFrom<IFileHttpResult>(
                await DemoApi.GetExecutionArtifactAsync(
                    flow.Id, artifact.Id, new DefaultHttpContext(), factory, CancellationToken.None));
            Assert.Equal("application/octet-stream", download.ContentType);
            Assert.Equal(relativePath.Split('/')[^1], download.FileDownloadName);
            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                DemoApi.GetExecutionArtifactAsync(
                    Guid.NewGuid(), artifact.Id, new DefaultHttpContext(), factory, CancellationToken.None));
            await File.WriteAllTextAsync(diagnostic, "later execution artifact version");
            Assert.True(await service.IsCurrentAsync(flow, before, requiresPreview: false));
            Assert.Equal(2, await verification.ExecutionArtifacts.CountAsync());

            await File.WriteAllTextAsync(
                Path.Combine(workspace, "solution.slnx"), "changed product scaffold");
            var exception = await Assert.ThrowsAsync<CandidateValidationException>(() =>
                service.SealAsync(flow));
            Assert.Contains("changed an untracked project scaffold file", exception.Message);

            artifact.Content = Encoding.UTF8.GetBytes("corrupted archive");
            verification.Entry(artifact).State = EntityState.Modified;
            await verification.SaveChangesAsync();
            await File.WriteAllTextAsync(diagnostic, "generated by browser verification");
            await Assert.ThrowsAsync<CandidateValidationException>(() =>
                service.SealAsync(flow));
            Assert.True(File.Exists(diagnostic));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DemoApi.GetExecutionArtifactAsync(
                    flow.Id, artifact.Id, new DefaultHttpContext(), factory, CancellationToken.None));
        }
        finally
        {
            ClearAndDelete(root);
        }
    }

    [Fact]
    public async Task ExecutionArtifactSchema_UpgradesExistingDatabaseIdempotently()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var database = new HarnessDbContext(
            new DbContextOptionsBuilder<HarnessDbContext>().UseSqlite(connection).Options);
        await database.Database.EnsureCreatedAsync();
        await database.Database.ExecuteSqlRawAsync("DROP TABLE ExecutionArtifacts;");
        var flow = new FlowRun { Title = "Existing flow", OriginalRequest = "Keep this flow" };
        database.Flows.Add(flow);
        await database.SaveChangesAsync();

        await DatabaseInitializer.EnsureExecutionArtifactSchemaAsync(database);
        await DatabaseInitializer.EnsureExecutionArtifactSchemaAsync(database);

        Assert.Equal(flow.Id, (await database.Flows.SingleAsync()).Id);
        Assert.Empty(await database.ExecutionArtifacts.ToListAsync());
        await database.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "ExecutionArtifacts" DROP COLUMN "ContentStored";""");
        await DatabaseInitializer.EnsureExecutionArtifactSchemaAsync(database);
        Assert.Empty(await database.ExecutionArtifacts.ToListAsync());
    }

    [Fact]
    public async Task LargeExecutionArtifacts_DoNotBlockCandidateOrAllocateDatabaseBlobs()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cand-large-artifact-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workspace);
        try
        {
            CandidateWorkspace.InitializeRepositoryAt(Path.Combine(source, "repo"));
            CandidateWorkspace.InitializeRepositoryAt(Path.Combine(workspace, "repo"));
            var flow = MultiRepositoryFlow(source, workspace, "repo");
            var factory = new CandidateDbContextFactory(
                new DbContextOptionsBuilder<HarnessDbContext>()
                    .UseSqlite($"Data Source={Path.Combine(root, "artifacts.db")};Pooling=False").Options);
            await using (var database = await factory.CreateDbContextAsync())
            {
                await database.Database.EnsureCreatedAsync();
                database.Flows.Add(flow);
                await database.SaveChangesAsync();
            }
            var service = new CandidateFingerprintService(
                new ProcessRunner(), TimeProvider.System, databaseFactory: factory);
            var before = await service.PrepareAsync(
                flow, Digest('a'), Guid.NewGuid(), requiresPreview: false);
            var output = Path.Combine(workspace, "unknown-tool-output.bin");
            var length = CandidateFingerprintService.MaximumStoredExecutionArtifactBytes + 1L;
            await using (var stream = File.Create(output))
            {
                stream.SetLength(length);
            }
            Assert.True(await service.IsCurrentAsync(flow, before, requiresPreview: false));
            await using var verification = await factory.CreateDbContextAsync();
            var artifact = await verification.ExecutionArtifacts.SingleAsync();
            Assert.False(artifact.ContentStored);
            Assert.Empty(artifact.Content);
            Assert.Equal(length, artifact.Length);
            await using (var download = await ExecutionArtifactStore.OpenWorkspaceContentAsync(
                             workspace, artifact, CancellationToken.None))
            {
                Assert.Equal(length, download.Length);
            }
            var escaping = new ExecutionArtifactRecord
            {
                FlowRunId = flow.Id,
                RelativePath = "../outside.bin",
                Digest = artifact.Digest,
                Content = [],
                ContentStored = false,
                Length = length
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ExecutionArtifactStore.OpenWorkspaceContentAsync(
                    workspace, escaping, CancellationToken.None));
            await File.WriteAllTextAsync(output, "changed after capture");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ExecutionArtifactStore.OpenWorkspaceContentAsync(
                    workspace, artifact, CancellationToken.None));
        }
        finally
        {
            ClearAndDelete(root);
        }
    }

    [Fact]
    public async Task HostSeal_DoesNotPublishGitIgnoredToolArtifacts()
    {
        using var workspace = CandidateWorkspace.Create();
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, ".gitignore"),
            ".unknown-tool-output/\n");
        workspace.Git("add", ".gitignore");
        workspace.Git("commit", "--quiet", "-m", "ignore execution artifacts");
        var diagnostics = Path.Combine(workspace.Root, ".unknown-tool-output");
        Directory.CreateDirectory(diagnostics);
        await File.WriteAllTextAsync(
            Path.Combine(diagnostics, "page.yml"),
            "browser snapshot");
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);

        var seal = await service.SealAsync(workspace.Flow);
        Assert.False(Assert.Single(seal.Repositories).Changed);
        Assert.DoesNotContain(
            ".unknown-tool-output",
            workspace.GitOutput("ls-tree", "-r", "--name-only", "HEAD"));
        var candidate = await service.PrepareAsync(
            workspace.Flow, Digest('a'), Guid.NewGuid(), requiresPreview: false);
        await File.WriteAllTextAsync(
            Path.Combine(diagnostics, "console.log"),
            "another browser observation");
        Assert.True(await service.IsCurrentAsync(
            workspace.Flow, candidate, requiresPreview: false));
    }

    [Fact]
    public async Task Candidate_RejectsOriginChangedAfterTrustedMappingSnapshot()
    {
        using var workspace = CandidateWorkspace.Create();
        workspace.Git(
            "remote",
            "add",
            "origin",
            "https://github.com/example/repository.git");
        workspace.SetTrustedRemote("example/repository");
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);
        var first = await service.PrepareAsync(
            workspace.Flow,
            Digest('a'),
            Guid.NewGuid(),
            requiresPreview: false);

        Assert.Equal(
            "example/repository",
            Assert.Single(first.Manifest.Repositories).RemoteRepository);

        workspace.Git(
            "remote",
            "set-url",
            "--push",
            "origin",
            "https://github.com/example/publication-target.git");
        var exception = await Assert.ThrowsAsync<CandidateValidationException>(() =>
            service.PrepareAsync(
                workspace.Flow,
                Digest('a'),
                Guid.NewGuid(),
                requiresPreview: false));
        Assert.Contains("trusted target", exception.Message);
    }

    [Fact]
    public async Task Candidate_RejectsRepositoryAddedAfterTrustedMappingSnapshot()
    {
        using var workspace = CandidateWorkspace.Create();
        CandidateWorkspace.InitializeRepositoryAt(
            Path.Combine(workspace.Root, "unexpected-repository"));
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);

        var exception = await Assert.ThrowsAsync<CandidateValidationException>(() =>
            service.PrepareAsync(
                workspace.Flow,
                Digest('a'),
                Guid.NewGuid(),
                requiresPreview: false));

        Assert.Contains("repository set", exception.Message);
    }

    [Fact]
    public async Task Candidate_DiscoversRepositoryAddedInsideTransientDirectory()
    {
        using var workspace = CandidateWorkspace.Create();
        CandidateWorkspace.InitializeRepositoryAt(
            Path.Combine(workspace.Root, "dist", "nested"));
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);

        var exception = await Assert.ThrowsAsync<CandidateValidationException>(() =>
            service.PrepareAsync(
                workspace.Flow,
                Digest('a'),
                Guid.NewGuid(),
                requiresPreview: false));

        Assert.Contains("repository set", exception.Message);
    }

    [Fact]
    public async Task HostSeal_CommitsDirtyTrackedBytesBeforeFingerprinting()
    {
        using var workspace = CandidateWorkspace.Create();
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);
        var before = workspace.GitOutput("rev-parse", "HEAD")
            .Trim()
            .ToLowerInvariant();
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "tracked.txt"),
            "sealed");

        var seal = await service.SealAsync(workspace.Flow);
        var after = workspace.GitOutput("rev-parse", "HEAD")
            .Trim()
            .ToLowerInvariant();

        Assert.NotEqual(before, after);
        Assert.True(Assert.Single(seal.Repositories).Changed);
        Assert.True(string.IsNullOrWhiteSpace(
            workspace.GitOutput("status", "--porcelain=v1")));

        var candidate = await service.PrepareAsync(
            workspace.Flow,
            Digest('a'),
            Guid.NewGuid(),
            requiresPreview: false);

        Assert.Equal(
            after,
            Assert.Single(candidate.Manifest.Repositories).Head);
    }

    [Theory]
    [InlineData(CandidateSealTransition.Started)]
    [InlineData(CandidateSealTransition.TreeWritten)]
    [InlineData(CandidateSealTransition.CommitWritten)]
    [InlineData(CandidateSealTransition.RefAdvanced)]
    [InlineData(CandidateSealTransition.IndexReset)]
    public async Task HostSeal_RecoversEveryDurableTransitionExactlyOnce(
        CandidateSealTransition interruptedAfter)
    {
        using var workspace = CandidateWorkspace.Create();
        var journalPath = CandidateFingerprintService.GetSealJournalPath(
            workspace.Root);
        var beforeHead = workspace.GitOutput("rev-parse", "HEAD").Trim();
        var beforeCount = int.Parse(
            workspace.GitOutput("rev-list", "--count", "HEAD").Trim());
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "tracked.txt"),
            $"sealed after {interruptedAfter}");
        var interrupted = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System,
            new ThrowOnceAtSealTransition(interruptedAfter));

        await Assert.ThrowsAsync<SimulatedCandidateSealCrash>(
            () => interrupted.SealAsync(workspace.Flow));
        Assert.True(File.Exists(journalPath));

        var recovered = await new CandidateFingerprintService(
                new ProcessRunner(),
                TimeProvider.System)
            .SealAsync(workspace.Flow);
        var recoveredHead = workspace.GitOutput("rev-parse", "HEAD").Trim();

        Assert.True(recovered.Changed);
        Assert.NotEqual(beforeHead, recoveredHead);
        Assert.Equal(
            beforeCount + 1,
            int.Parse(workspace.GitOutput("rev-list", "--count", "HEAD").Trim()));
        Assert.True(string.IsNullOrWhiteSpace(
            workspace.GitOutput("status", "--porcelain=v1")));
        Assert.False(File.Exists(journalPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(journalPath)!));
        Assert.False(File.Exists(journalPath));

        var idempotent = await new CandidateFingerprintService(
                new ProcessRunner(),
                TimeProvider.System)
            .SealAsync(workspace.Flow);

        Assert.False(idempotent.Changed);
        Assert.Equal(
            recoveredHead,
            workspace.GitOutput("rev-parse", "HEAD").Trim());
        Assert.Equal(
            beforeCount + 1,
            int.Parse(workspace.GitOutput("rev-list", "--count", "HEAD").Trim()));
    }

    [Fact]
    public async Task HostSeal_IncludesUntrackedProductFile()
    {
        using var workspace = CandidateWorkspace.Create();
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "new-product.txt"),
            "new bytes");

        _ = await service.SealAsync(workspace.Flow);
        var candidate = await service.PrepareAsync(
            workspace.Flow,
            Digest('a'),
            Guid.NewGuid(),
            requiresPreview: false);

        Assert.Equal(
            "new bytes",
            workspace.GitOutput("show", "HEAD:new-product.txt")
                .ReplaceLineEndings("\n")
                .TrimEnd('\n'));
        Assert.Equal(
            workspace.GitOutput("rev-parse", "HEAD")
                .Trim()
                .ToLowerInvariant(),
            Assert.Single(candidate.Manifest.Repositories).Head);
    }

    [Fact]
    public async Task HostSeal_RemovesTrackedPlaywrightBrowserCache()
    {
        using var workspace = CandidateWorkspace.Create();
        var browserCache = Path.Combine(
            workspace.Root,
            ".playwright-browsers",
            "chromium",
            "debug.log");
        Directory.CreateDirectory(Path.GetDirectoryName(browserCache)!);
        await File.WriteAllTextAsync(browserCache, "transient browser output");
        workspace.Git("add", ".playwright-browsers");
        workspace.Git("commit", "--quiet", "-m", "accidentally track browser cache");
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);

        _ = await service.SealAsync(workspace.Flow);

        Assert.DoesNotContain(
            ".playwright-browsers/chromium/debug.log",
            workspace.GitOutput("ls-tree", "-r", "--name-only", "HEAD")
                .ReplaceLineEndings("\n")
                .Split(
                    '\n',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries));
    }

    [Fact]
    public async Task HostSeal_IncludesTrackedDeletion()
    {
        using var workspace = CandidateWorkspace.Create();
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);
        File.Delete(Path.Combine(workspace.Root, "tracked.txt"));

        _ = await service.SealAsync(workspace.Flow);
        _ = await service.PrepareAsync(
            workspace.Flow,
            Digest('a'),
            Guid.NewGuid(),
            requiresPreview: false);

        Assert.DoesNotContain(
            "tracked.txt",
            workspace.GitOutput("ls-tree", "-r", "--name-only", "HEAD")
                .ReplaceLineEndings("\n")
                .Split(
                    '\n',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries));
    }

    [Fact]
    public async Task HostSeal_PreservesTrackedExecutableMode()
    {
        using var workspace = CandidateWorkspace.Create();
        var trackedPath = Path.Combine(workspace.Root, "tracked.txt");
        workspace.Git("update-index", "--chmod=+x", "tracked.txt");
        workspace.Git("commit", "--quiet", "-m", "track executable mode");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                trackedPath,
                File.GetUnixFileMode(trackedPath) |
                UnixFileMode.UserExecute |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherExecute);
        }
        await File.WriteAllTextAsync(trackedPath, "updated executable bytes");
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);

        _ = await service.SealAsync(workspace.Flow);
        _ = await service.PrepareAsync(
            workspace.Flow,
            Digest('a'),
            Guid.NewGuid(),
            requiresPreview: false);

        Assert.StartsWith(
            "100755 ",
            workspace.GitOutput("ls-tree", "HEAD", "--", "tracked.txt"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Candidate_RejectsIndexModeMetadataThatDiffersFromHead()
    {
        using var workspace = CandidateWorkspace.Create();
        workspace.Git("update-index", "--chmod=+x", "tracked.txt");
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);

        var prepareException =
            await Assert.ThrowsAsync<CandidateValidationException>(() =>
                service.PrepareAsync(
                    workspace.Flow,
                    Digest('a'),
                    Guid.NewGuid(),
                    requiresPreview: false));
        var sealException =
            await Assert.ThrowsAsync<CandidateValidationException>(() =>
                service.SealAsync(workspace.Flow));

        Assert.Contains("index metadata differs", prepareException.Message);
        Assert.Contains("index metadata differs", sealException.Message);
    }

    [Fact]
    public async Task HostSeal_ExcludesPreviewFromCommitButStillFingerprintsIt()
    {
        using var workspace = CandidateWorkspace.Create();
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "tracked.txt"),
            "sealed");
        var preview = Path.Combine(
            workspace.Root,
            ".customer-preview",
            "demo",
            "browser");
        Directory.CreateDirectory(preview);
        await File.WriteAllTextAsync(
            Path.Combine(preview, "index.html"),
            "<h1>Preview</h1>");
        _ = await service.SealAsync(workspace.Flow);
        var treeEntries = workspace.GitOutput("ls-tree", "-r", "--name-only", "HEAD")
            .ReplaceLineEndings("\n")
            .Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
        var candidate = await service.PrepareAsync(
            workspace.Flow,
            Digest('a'),
            Guid.NewGuid(),
            requiresPreview: true);

        Assert.DoesNotContain(
            ".customer-preview/demo/browser/index.html",
            treeEntries);
        Assert.Contains(
            candidate.Manifest.PreviewArtifacts,
            item => string.Equals(
                item.RelativePath,
                ".customer-preview/demo/browser/index.html",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostSeal_DoesNotExecuteHooksFiltersOrSigningConfig()
    {
        using var workspace = CandidateWorkspace.Create();
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);
        var hooksDirectory = Path.Combine(workspace.Root, "hooks");
        Directory.CreateDirectory(hooksDirectory);
        var hookMarker = Path.Combine(workspace.Root, "hook-fired.txt");
        var filterMarker = Path.Combine(workspace.Root, "filter-fired.txt");
        var signingMarker = Path.Combine(workspace.Root, "signing-fired.txt");
        await WriteHookScriptAsync(
            Path.Combine(hooksDirectory, "pre-commit"),
            hookMarker);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, ".gitattributes"),
            "*.txt filter=malicious\n");
        workspace.Git("add", ".gitattributes");
        workspace.Git("commit", "--quiet", "-m", "add filter attributes");
        workspace.Git("config", "core.hooksPath", ToGitPath(hooksDirectory));
        workspace.Git(
            "config",
            "filter.malicious.clean",
            BuildShellWriteAndFailCommand(filterMarker, "filter"));
        workspace.Git(
            "config",
            "filter.malicious.smudge",
            BuildShellWriteAndFailCommand(filterMarker, "filter"));
        var fakeGpg = await CreateMarkerCommandAsync(
            workspace.Root,
            "fake-gpg",
            signingMarker,
            "signing");
        workspace.Git("config", "commit.gpgSign", "true");
        workspace.Git("config", "user.signingkey", "candidate-test");
        workspace.Git("config", "gpg.program", fakeGpg);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "tracked.txt"),
            "sealed");

        _ = await service.SealAsync(workspace.Flow);
        _ = await service.PrepareAsync(
            workspace.Flow,
            Digest('a'),
            Guid.NewGuid(),
            requiresPreview: false);

        Assert.False(File.Exists(hookMarker));
        Assert.False(File.Exists(filterMarker));
        Assert.False(File.Exists(signingMarker));
    }

    [Fact]
    public async Task Candidate_AllowsCoreAutocrlfCheckoutAndSealsCanonicalBlobWithoutRunningFilters()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = CandidateWorkspace.Create();
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);
        var trackedPath = Path.Combine(workspace.Root, "tracked.txt");
        await File.WriteAllTextAsync(
            trackedPath,
            "line one\nline two\n");
        workspace.Git("add", "tracked.txt");
        workspace.Git("commit", "--quiet", "-m", "normalize tracked text");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, ".gitattributes"),
            "tracked.txt filter=malicious\n");
        workspace.Git("add", ".gitattributes");
        workspace.Git("commit", "--quiet", "-m", "add filter attribute");
        workspace.Git("config", "core.autocrlf", "true");
        File.Delete(trackedPath);
        workspace.Git("checkout", "--quiet", "--", "tracked.txt");
        Assert.Equal(
            Encoding.UTF8.GetBytes("line one\r\nline two\r\n"),
            await File.ReadAllBytesAsync(trackedPath));

        var filterMarker = Path.Combine(workspace.Root, "filter-fired.txt");
        workspace.Git(
            "config",
            "filter.malicious.clean",
            BuildShellWriteAndFailCommand(filterMarker, "filter"));
        workspace.Git(
            "config",
            "filter.malicious.smudge",
            BuildShellWriteAndFailCommand(filterMarker, "filter"));

        var cleanCandidate = await service.PrepareAsync(
            workspace.Flow,
            Digest('a'),
            Guid.NewGuid(),
            requiresPreview: false);
        Assert.Equal(
            workspace.GitOutput("rev-parse", "HEAD")
                .Trim()
                .ToLowerInvariant(),
            Assert.Single(cleanCandidate.Manifest.Repositories).Head);
        Assert.False(File.Exists(filterMarker));

        await File.WriteAllTextAsync(
            trackedPath,
            "sealed change\r\nwith crlf\r\n");
        var expectedBlob = workspace.GitOutput(
                "-c",
                "filter.malicious.clean=",
                "-c",
                "filter.malicious.smudge=",
                "-c",
                "filter.malicious.process=",
                "-c",
                "filter.malicious.required=false",
                "hash-object",
                "--path",
                "tracked.txt",
                "tracked.txt")
            .Trim()
            .ToLowerInvariant();

        var seal = await service.SealAsync(workspace.Flow);
        Assert.True(Assert.Single(seal.Repositories).Changed);
        Assert.Equal(
            expectedBlob,
            workspace.GitOutput("rev-parse", "HEAD:tracked.txt")
                .Trim()
                .ToLowerInvariant());

        var sealedCandidate = await service.PrepareAsync(
            workspace.Flow,
            Digest('a'),
            Guid.NewGuid(),
            requiresPreview: false);
        Assert.Equal(
            workspace.GitOutput("rev-parse", "HEAD")
                .Trim()
                .ToLowerInvariant(),
            Assert.Single(sealedCandidate.Manifest.Repositories).Head);
        Assert.False(File.Exists(filterMarker));
    }

    [Fact]
    public async Task HostSeal_NoOpKeepsHeadUnchanged()
    {
        using var workspace = CandidateWorkspace.Create();
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);
        var before = workspace.GitOutput("rev-parse", "HEAD")
            .Trim()
            .ToLowerInvariant();

        var seal = await service.SealAsync(workspace.Flow);
        var after = workspace.GitOutput("rev-parse", "HEAD")
            .Trim()
            .ToLowerInvariant();
        var candidate = await service.PrepareAsync(
            workspace.Flow,
            Digest('a'),
            Guid.NewGuid(),
            requiresPreview: false);

        Assert.False(seal.Changed);
        Assert.Equal(before, after);
        Assert.Equal(
            after,
            Assert.Single(candidate.Manifest.Repositories).Head);
    }

    [Fact]
    public async Task HostSeal_ExcludesVerifierReportWithoutBlockingOrPublishingIt()
    {
        using var workspace = CandidateWorkspace.Create();
        Directory.CreateDirectory(Path.Combine(workspace.Root, "scripts"));
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "package.json"), """{"name":"test-product"}""");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "scripts", "verify-assets.js"),
            "console.log('verification');");
        await File.AppendAllTextAsync(
            Path.Combine(workspace.Root, ".gitignore"),
            $"{Environment.NewLine}*-report.json{Environment.NewLine}");
        workspace.Git("add", "package.json", ".gitignore", "scripts/verify-assets.js");
        workspace.Git("commit", "--quiet", "-m", "add verifier");
        var report = Path.Combine(workspace.Root, "scripts", "verify-assets-report.json");
        await File.WriteAllTextAsync(report, """{"passed":true}""");
        var service = new CandidateFingerprintService(new ProcessRunner(), TimeProvider.System);

        await service.SealAsync(workspace.Flow);
        var candidate = await service.PrepareAsync(
            workspace.Flow, Digest('a'), Guid.NewGuid(), requiresPreview: false);

        Assert.DoesNotContain(
            "scripts/verify-assets-report.json",
            workspace.GitOutput("ls-tree", "-r", "--name-only", "HEAD"));
        Assert.True(File.Exists(report));
        await File.WriteAllTextAsync(report, """{"passed":true,"timing":123}""");
        Assert.True(await service.IsCurrentAsync(workspace.Flow, candidate, requiresPreview: false));
    }

    [Theory]
    [InlineData("scripts/verify-settings-report.json", false, true)]
    [InlineData("scripts/verify-settings-report.json", true, false)]
    [InlineData("config/verify-settings-report.json", true, true)]
    [InlineData("scripts/runtime-report.json", true, true)]
    public async Task HostSeal_RespectsIgnoredReportsWithoutNamingOrManifestRequirements(
        string relativePath,
        bool matchingScript,
        bool projectManifest)
    {
        using var workspace = CandidateWorkspace.Create();
        var directory = Path.Combine(
            workspace.Root, Path.GetDirectoryName(relativePath)!.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        if (projectManifest)
        {
            await File.WriteAllTextAsync(
                Path.Combine(workspace.Root, "package.json"), """{"name":"test-product"}""");
            workspace.Git("add", "package.json");
        }
        if (matchingScript)
        {
            var stem = Path.GetFileName(relativePath)[..^"-report.json".Length];
            await File.WriteAllTextAsync(Path.Combine(directory, stem + ".js"), "console.log('check');");
            workspace.Git("add", $"{Path.GetDirectoryName(relativePath)!.Replace('\\', '/')}/{stem}.js");
        }
        await File.AppendAllTextAsync(
            Path.Combine(workspace.Root, ".gitignore"),
            $"{Environment.NewLine}*-report.json{Environment.NewLine}");
        workspace.Git("add", ".gitignore");
        workspace.Git("commit", "--quiet", "-m", "ignore report-shaped file");
        await File.WriteAllTextAsync(
            Path.Combine(directory, Path.GetFileName(relativePath)), """{"FeatureEnabled":true}""");
        var service = new CandidateFingerprintService(new ProcessRunner(), TimeProvider.System);

        await service.SealAsync(workspace.Flow);
        Assert.DoesNotContain(relativePath, workspace.GitOutput("ls-tree", "-r", "--name-only", "HEAD"));
    }

    [Fact]
    public async Task Candidate_ExplicitlyTrackedVerifierReportRemainsProductContent()
    {
        using var workspace = CandidateWorkspace.Create();
        Directory.CreateDirectory(Path.Combine(workspace.Root, "scripts"));
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "package.json"), """{"name":"test-product"}""");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "scripts", "check-assets.js"), "console.log('check');");
        var report = Path.Combine(workspace.Root, "scripts", "check-assets-report.json");
        await File.WriteAllTextAsync(report, """{"passed":true}""");
        await File.AppendAllTextAsync(
            Path.Combine(workspace.Root, ".gitignore"), $"{Environment.NewLine}scripts/{Environment.NewLine}");
        workspace.Git("add", "package.json", ".gitignore");
        workspace.Git("add", "--force", "scripts");
        workspace.Git("commit", "--quiet", "-m", "include verification report");
        var service = new CandidateFingerprintService(new ProcessRunner(), TimeProvider.System);
        var candidate = await service.PrepareAsync(
            workspace.Flow, Digest('a'), Guid.NewGuid(), requiresPreview: false);

        await File.WriteAllTextAsync(report, """{"passed":false}""");

        await Assert.ThrowsAsync<CandidateValidationException>(
            () => service.IsCurrentAsync(workspace.Flow, candidate, requiresPreview: false));
    }

    [Fact]
    public async Task HostSeal_ExcludesIgnoredSecretsInsteadOfCommittingThem()
    {
        using var workspace = CandidateWorkspace.Create();
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);
        await File.AppendAllTextAsync(
            Path.Combine(workspace.Root, ".gitignore"),
            $"{Environment.NewLine}.env{Environment.NewLine}");
        workspace.Git("add", ".gitignore");
        workspace.Git("commit", "--quiet", "-m", "ignore local secrets");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, ".env"),
            "SECRET=must-not-be-committed");

        await service.SealAsync(workspace.Flow);

        Assert.DoesNotContain(
            ".env",
            workspace.GitOutput("ls-tree", "-r", "--name-only", "HEAD")
                .ReplaceLineEndings("\n")
                .Split(
                    '\n',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries));
    }

    [Theory]
    [InlineData("packages/private/config.json")]
    [InlineData("wwwroot/private/config.json")]
    [InlineData("scripts/private/config.json")]
    public async Task HostSeal_ExcludesIgnoredDirectoriesFromDelivery(
        string relativePath)
    {
        using var workspace = CandidateWorkspace.Create();
        var directory = Path.GetDirectoryName(
            Path.Combine(
                workspace.Root,
                relativePath.Replace('/', Path.DirectorySeparatorChar)))!;
        Directory.CreateDirectory(directory);
        await File.AppendAllTextAsync(
            Path.Combine(workspace.Root, ".gitignore"),
            $"{Environment.NewLine}{relativePath.Split('/')[0]}/{Environment.NewLine}");
        workspace.Git("add", ".gitignore");
        workspace.Git("commit", "--quiet", "-m", "ignore ambiguous output");
        await File.WriteAllTextAsync(
            Path.Combine(
                workspace.Root,
                relativePath.Replace('/', Path.DirectorySeparatorChar)),
            """{"secret":"must-not-be-hidden"}""");
        var service = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);

        await service.SealAsync(workspace.Flow);

        Assert.DoesNotContain(relativePath, workspace.GitOutput("ls-tree", "-r", "--name-only", "HEAD"));
    }

    private static FlowRun MultiRepositoryFlow(
        string source,
        string workspace,
        params string[] repositoryPaths)
    {
        var flow = new FlowRun
        {
            Title = "Multi repository candidate",
            OriginalRequest = "Prepare it",
            RepositoryPath = source,
            WorkspacePath = workspace,
            Iteration = 1,
            Outcome = OutcomeType.Commit
        };
        RecordTrustedRepositories(flow, repositoryPaths);
        return flow;
    }

    private static void ConfigureReviewedFlow(FlowRun flow)
    {
        flow.Kind = FlowKind.Delivery;
        flow.OutcomeOwnerPlanStepKey = "outcome";
        flow.OutcomeContractJson =
            """{"Goal":"Ship it.","Summary":"Ready.","ImplementationDetails":["Changed tracked product bytes."],"Artifacts":[]}""";
        RecordTrustedRepositories(flow, ["."]);
    }

    private static void RecordCustomerObservableAcceptancePlan(FlowRun flow)
    {
        var plan = new DeliveryAcceptancePlan(
        [
            new DeliveryAcceptanceCriterion
            {
                Id = "AC-001",
                Requirement = "The customer can inspect the delivered browser result.",
                Verification = "Open the isolated customer preview.",
                OwnerRoles = ["quality-engineer"],
                EvidenceKinds =
                [
                    OutcomeEvidenceKind.Artifact,
                    OutcomeEvidenceKind.Observation
                ],
                CustomerVisible = true
            }
        ]);
        flow.Events.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            Type = DeliveryReadinessService.AcceptancePlanEventType,
            Message = "Fixture acceptance plan.",
            DataJson = DeliveryReadinessService.SerializeAcceptancePlan(
                plan,
                flow.Iteration,
                Guid.NewGuid())
        });
    }

    private static string Digest(char value) => $"sha256:{new string(value, 64)}";

    private static void RecordTrustedRepositories(
        FlowRun flow,
        IReadOnlyList<string> relativePaths,
        string remoteRepository = "")
    {
        flow.Events.RemoveAll(
            item => item.Type == StudioWorkspaceRepositoryMapLedger.EventType);
        flow.Events.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            Type = StudioWorkspaceRepositoryMapLedger.EventType,
            Message = "Fixture trusted repository map.",
            DataJson = StudioWorkspaceRepositoryMapLedger.Serialize(
                StudioWorkspaceRepositoryMapLedger.Create(
                    flow,
                    flow.WorkspacePath,
                    relativePaths
                        .Order(StringComparer.Ordinal)
                        .Select(path => new WorkspaceRepositoryIdentity(
                            path,
                            remoteRepository))
                        .ToArray()))
        });
    }

    private static async Task WriteHookScriptAsync(
        string path,
        string markerPath)
    {
        await File.WriteAllTextAsync(
            path,
            $"""
             #!/bin/sh
             printf hook > '{ShellQuote(markerPath)}'
             exit 1
             """.ReplaceLineEndings("\n"));
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
    }

    private static async Task<string> CreateMarkerCommandAsync(
        string root,
        string name,
        string markerPath,
        string content)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(root, $"{name}.cmd");
            await File.WriteAllTextAsync(
                path,
                "@echo off\r\n" +
                $"set TARGET={markerPath}\r\n" +
                $"@echo {content}>\"%TARGET%\"\r\n" +
                "exit /b 1\r\n");
            return path;
        }

        var shellPath = Path.Combine(root, name);
        await File.WriteAllTextAsync(
            shellPath,
            $$"""
              #!/bin/sh
              printf {{content}} > '{{ShellQuote(markerPath)}}'
              exit 1
              """.ReplaceLineEndings("\n"));
        File.SetUnixFileMode(
            shellPath,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
        return shellPath;
    }

    private static string BuildShellWriteAndFailCommand(
        string markerPath,
        string content) =>
        $"printf {content} > '{ShellQuote(markerPath)}'; exit 1";

    private static string ToGitPath(string path) =>
        Path.GetFullPath(path).Replace('\\', '/');

    private static string ShellQuote(string path) =>
        ToGitPath(path).Replace("'", "'\\''", StringComparison.Ordinal);

    private static bool TryCreateDirectoryReparsePoint(
        string link,
        string target)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(link, target);
                return true;
            }

            var command = Environment.GetEnvironmentVariable("ComSpec")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "cmd.exe");
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = command,
                Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null)
            {
                return false;
            }
            process.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                PlatformNotSupportedException)
        {
            return false;
        }
    }

    private sealed class SimulatedCandidateSealCrash(
        CandidateSealTransition transition)
        : Exception($"Simulated process interruption after {transition}.");

    private sealed class ThrowOnceAtSealTransition(
        CandidateSealTransition target) : ICandidateSealFaultInjector
    {
        private bool _thrown;

        public Task OnTransitionAsync(
            CandidateSealTransition transition,
            string repository,
            CancellationToken cancellationToken)
        {
            if (!_thrown && transition == target)
            {
                _thrown = true;
                throw new SimulatedCandidateSealCrash(transition);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class CandidateWorkspace : IDisposable
    {
        private CandidateWorkspace(string root)
        {
            Root = root;
            Flow = new FlowRun
            {
                Title = "Candidate",
                OriginalRequest = "Prepare it",
                RepositoryPath = root,
                WorkspacePath = root,
                Iteration = 1,
                Outcome = OutcomeType.Commit
            };
        }

        public string Root { get; }

        public FlowRun Flow { get; }

        public static CandidateWorkspace Create(bool multipleRepositories = false)
        {
            var root = Path.Combine(
                multipleRepositories ? Path.GetTempPath() : AppContext.BaseDirectory,
                "candidate-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var workspace = new CandidateWorkspace(root);
            if (multipleRepositories)
            {
                workspace.InitializeRepository(Path.Combine(root, "z"));
                workspace.InitializeRepository(Path.Combine(root, "a"));
            }
            else
            {
                workspace.InitializeRepository(root);
            }
            workspace.SetTrustedRepositories(
                multipleRepositories ? ["a", "z"] : ["."]);
            workspace.Flow.BranchName = RunWithOutput(
                    multipleRepositories
                        ? Path.Combine(root, "a")
                        : root,
                    [
                        "git",
                        "-C",
                        multipleRepositories
                            ? Path.Combine(root, "a")
                            : root,
                        "symbolic-ref",
                        "--quiet",
                        "--short",
                        "HEAD"
                    ])
                .Trim();
            return workspace;
        }

        public void Git(params string[] arguments) =>
            Run(Root, ["git", "-C", Root, .. arguments]);

        public string GitOutput(params string[] arguments) =>
            RunWithOutput(Root, ["git", "-C", Root, .. arguments]);

        public void SetTrustedRemote(string remoteRepository) =>
            SetTrustedRepositories(["."], remoteRepository);

        private void SetTrustedRepositories(
            IReadOnlyList<string> relativePaths,
            string remoteRepository = "")
            => RecordTrustedRepositories(
                Flow,
                relativePaths,
                remoteRepository);

        private void InitializeRepository(string path) =>
            InitializeRepositoryAt(path);

        public static void InitializeRepositoryAt(string path)
        {
            Directory.CreateDirectory(path);
            Run(path, ["git", "init", "--quiet"]);
            Run(path, ["git", "config", "user.email", "tests@example.invalid"]);
            Run(path, ["git", "config", "user.name", "Outcome Tests"]);
            File.WriteAllText(Path.Combine(path, "tracked.txt"), "first");
            Run(path, ["git", "add", "-A"]);
            Run(path, ["git", "commit", "--quiet", "-m", "initial"]);
        }

        private static void Run(string workingDirectory, IReadOnlyList<string> command)
        {
            _ = RunWithOutput(workingDirectory, command);
        }

        private static string RunWithOutput(
            string workingDirectory,
            IReadOnlyList<string> command)
        {
            var start = new ProcessStartInfo
            {
                FileName = command[0],
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in command.Skip(1))
            {
                start.ArgumentList.Add(argument);
            }
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start git.");
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(process.StandardError.ReadToEnd());
            }
            return output;
        }

        public void Dispose()
        {
            ClearAndDelete(Root);
        }

    }

    private sealed class CandidateDbContextFactory(
        DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);

        public Task<HarnessDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private static void ClearAndDelete(string root)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(
                         root,
                         "*",
                         SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            foreach (var directory in Directory.EnumerateDirectories(
                         root,
                         "*",
                         SearchOption.AllDirectories))
            {
                File.SetAttributes(directory, FileAttributes.Directory);
            }
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Test cleanup only.
        }
    }
}

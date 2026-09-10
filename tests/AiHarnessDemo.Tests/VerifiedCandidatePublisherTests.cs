using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Security;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Data;
using AiHarnessDemo.Services;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Tests;

public sealed class VerifiedCandidatePublisherTests
{
    [Fact]
    public async Task DeliveryNone_IsRejectedBeforePublisherOrVerifierSideEffects()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            "verified-publication-tests",
            Guid.NewGuid().ToString("N"),
            "publication.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        try
        {
            var options = new DbContextOptionsBuilder<HarnessDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            IDbContextFactory<HarnessDbContext> factory =
                new PublicationDbContextFactory(options);
            var flow = new FlowRun
            {
                Title = "Malformed Delivery",
                OriginalRequest = "Publish.",
                ContractVersion = "studio-v2",
                Kind = FlowKind.Delivery,
                Status = FlowStatus.Queued,
                RepositoryPath = "must-not-be-inspected",
                WorkspacePath = "must-not-be-inspected",
                Outcome = OutcomeType.None
            };
            var publication = new FlowStep
            {
                FlowRun = flow,
                Iteration = 1,
                Sequence = 10,
                AgentId = "publisher",
                AgentName = "Publisher",
                AgentRole = "publisher",
                PlanStepKey = "publish",
                PlanDutiesJson = """["Publish"]""",
                PlanStage = PlanStage.AfterApproval,
                InvocationKind = ExecutionInvocationKind.Publication,
                RemotePublicationAllowed = true,
                Status = StepStatus.Running
            };
            await using (var database = await factory.CreateDbContextAsync())
            {
                await database.Database.EnsureCreatedAsync();
                database.Flows.Add(flow);
                database.FlowSteps.Add(publication);
                await database.SaveChangesAsync();
            }
            var fingerprints = new CandidateFingerprintService(
                new ProcessRunner(),
                TimeProvider.System);
            var publisher = new VerifiedCandidatePublisher(
                new ProcessRunner(),
                fingerprints,
                new ReviewedCandidateService(fingerprints),
                factory);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                publisher.PublishAsync(flow, publication.Id));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                new PublishedOutcomeVerifier(new ProcessRunner())
                    .VerifyAsync(flow, "must not be interpreted"));
            Assert.Throws<ArgumentException>(() =>
                WorkflowEngine.ApprovedPublicationAssignment(
                    OutcomeType.None));

            await using var check = await factory.CreateDbContextAsync();
            Assert.Empty(await check.FlowEvents.ToListAsync());
        }
        finally
        {
            VerifiedCandidatePublisherTestsCleanup.DeleteDirectoryBestEffort(
                Path.GetDirectoryName(databasePath)!);
        }
    }

    [Fact]
    public async Task StudioCommitPublication_ReusesDurableReviewedIdentityAndRejectsDrift()
    {
        using var workspace = PublicationWorkspace.Create();
        var databasePath = Path.Combine(workspace.Root, "publication.db");
        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;
        IDbContextFactory<HarnessDbContext> factory =
            new PublicationDbContextFactory(options);
        var flow = new FlowRun
        {
            Title = "Publish reviewed candidate",
            OriginalRequest = "Publish the reviewed candidate.",
            ConsolidatedRequest = "Publish the reviewed candidate.",
            ContractVersion = "studio-v2",
            Kind = FlowKind.Delivery,
            Status = FlowStatus.Queued,
            RepositoryPath = workspace.SourceRepository,
            WorkspacePath = workspace.SourceRepository,
            BranchName = workspace.GitOutput(
                    workspace.SourceRepository,
                    "branch",
                    "--show-current")
                .Trim(),
            Outcome = OutcomeType.Commit,
            OutcomeOwnerPlanStepKey = "outcome",
            PublicationPlanStepKey = "publish",
            OutcomeContractJson =
                """{"Version":"flow-outcome-v1","Goal":"Publish.","Summary":"Reviewed.","ImplementationDetails":["Exact bytes."],"Artifacts":[]}"""
        };
        flow.Events.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            Type = StudioWorkspaceRepositoryMapLedger.EventType,
            Message = "Persisted trusted workspace repositories.",
            DataJson = StudioWorkspaceRepositoryMapLedger.Serialize(
                StudioWorkspaceRepositoryMapLedger.Create(
                    flow,
                    flow.WorkspacePath,
                    [new WorkspaceRepositoryIdentity(".", string.Empty)]))
        });
        var owner = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Sequence = 10,
            AgentId = "owner",
            AgentName = "Owner",
            AgentRole = "owner",
            PlanStepKey = "outcome",
            IsOutcomeOwner = true,
            Status = StepStatus.Completed
        };
        var publication = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Sequence = 20,
            AgentId = "publisher",
            AgentName = "Publisher",
            AgentRole = "publisher",
            PlanStepKey = "publish",
            PlanDutiesJson = """["Publish"]""",
            PlanStage = PlanStage.AfterApproval,
            InvocationKind = ExecutionInvocationKind.Publication,
            PermissionProfile =
                ExecutionPermissionProfile.Publish,
            EffectivePermissionJson = JsonSerializer.Serialize(
                PublicationPermission()),
            WorkflowRevision = "publication-workflow-v1",
            RemotePublicationAllowed = true,
            Status = StepStatus.Running,
            DependsOnStepId = owner.Id
        };
        publication.StableSemanticRootId = publication.Id;
        flow.Steps.Add(owner);
        flow.Steps.Add(publication);
        flow.GateRecords.Add(new HandoffGateRecord
        {
            FlowRunId = flow.Id,
            FlowStepId = owner.Id,
            ActionType = HandoffActionType.CustomerReview,
            Decision = HandoffGateDecision.AwaitingHumanApproval,
            ReviewDecision = ReviewDecision.Accepted,
            TrustLevelAtDecision = HandoffTrustLevel.Gated,
            Summary = "Customer accepted the reviewed candidate.",
            Resolved = true,
            Approved = true,
            ResolvedBy = "customer",
            ResolvedAt = DateTimeOffset.UtcNow
        });
        var fingerprints = new CandidateFingerprintService(
            new ProcessRunner(),
            TimeProvider.System);
        var reviewed = new ReviewedCandidateService(fingerprints);
        var identity = await reviewed.SealAsync(
            flow,
            owner.Id,
            owner.PlanStepKey,
            flow.OutcomeContractJson);
        flow.Events.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = owner.Id,
            Type = ReviewedCandidateLedger.EventType,
            Message = "Sealed exact reviewed candidate.",
            DataJson = ReviewedCandidateLedger.Serialize(identity)
        });
        await using (var database = await factory.CreateDbContextAsync())
        {
            await database.Database.EnsureCreatedAsync();
            database.Flows.Add(flow);
            await database.SaveChangesAsync();
            await DeliveryReadinessFixtures.SeedReadyToApproveAsync(
                database,
                flow,
                identity,
                owner.Id);
        }
        var publisher = new VerifiedCandidatePublisher(
            new ProcessRunner(),
            fingerprints,
            reviewed,
            factory);

        var first = await publisher.PublishAsync(flow, publication.Id);
        var retry = await publisher.PublishAsync(flow, publication.Id);

        Assert.Equal(first, retry);
        Assert.Contains(identity.Fingerprint, first);
        Assert.Contains(identity.Repositories[0].Head, first);
        await using (var database = await factory.CreateDbContextAsync())
        {
            Assert.Single(database.FlowEvents.Where(item =>
                item.FlowRunId == flow.Id &&
                item.Type == "delivery.reviewed-publication.completed"));
        }

        await File.WriteAllTextAsync(
            Path.Combine(workspace.SourceRepository, "tracked.txt"),
            "unreviewed drift");
        await Assert.ThrowsAsync<CandidateValidationException>(
            () => publisher.PublishAsync(flow, publication.Id));
    }

    private static EffectiveExecutionPermission
        PublicationPermission() =>
        new PermissionProfileResolver().Resolve(
            new PermissionResolutionRequest(
                FlowKind.Delivery,
                ExecutionInvocationKind.Publication,
                PlanStage.AfterApproval,
                ImmutableArray.Create(PlanDuty.Publish),
                DurableReviewDecision: ReviewDecision.Accepted,
                DurableApproval: true,
                IsOnlyPlannedPublishStep: true,
                ContractVersion: "studio-v2",
                LegacyPublicationAuthorized: false,
                IsGovernedOutcomeVerification: false),
            new WorkflowPermissionRestrictions(
                ExecutionPermissionProfile.ReadOnlySource,
                ExecutionPermissionProfile.WorkspaceWrite,
                ExecutionPermissionProfile.Publish,
                ImmutableDictionary<
                    ExecutionPermissionProfile,
                    ImmutableArray<string>>.Empty));

    [Fact]
    public async Task PublishVerifiedGitBranchAsync_IgnoresRepositoryHooksAndPublishesExactObjects()
    {
        using var workspace = PublicationWorkspace.Create();
        var hookPath = Path.Combine(workspace.Root, "hooks", "pre-push");
        Directory.CreateDirectory(Path.GetDirectoryName(hookPath)!);
        await WriteShellScriptAsync(
            hookPath,
            """
            #!/bin/sh
            printf hook > "./hook-fired.txt"
            exit 1
            """);
        workspace.Git("config", "core.hooksPath", ToGitPath(Path.GetDirectoryName(hookPath)!));

        var published = await VerifiedCandidatePublisher.PublishVerifiedGitBranchAsync(
            new ProcessRunner(),
            workspace.SourceRepository,
            workspace.RepositoryManifest,
            workspace.TargetRepository,
            "verified-publication",
            string.Empty);

        Assert.Equal(workspace.RepositoryManifest.Head, published.Head);
        Assert.Equal(workspace.RepositoryManifest.Tree, published.Tree);
        Assert.False(File.Exists(Path.Combine(workspace.SourceRepository, "hook-fired.txt")));
        Assert.Equal(
            workspace.RepositoryManifest.Head,
            workspace.GitOutput(
                workspace.TargetRepository,
                "--git-dir",
                workspace.TargetRepository,
                "rev-parse",
                "refs/heads/verified-publication").Trim().ToLowerInvariant());
        Assert.Equal(
            workspace.RepositoryManifest.Tree,
            workspace.GitOutput(
                workspace.TargetRepository,
                "--git-dir",
                workspace.TargetRepository,
                "rev-parse",
                "refs/heads/verified-publication^{tree}").Trim().ToLowerInvariant());
    }

    [Fact]
    public async Task PublishVerifiedGitBranchAsync_IgnoresGlobalCredentialHelper()
    {
        using var workspace = PublicationWorkspace.Create();
        var markerPath = Path.Combine(workspace.Root, "credential-helper-fired.txt");
        var configPath = Path.Combine(workspace.Root, "helper.gitconfig");
        await File.WriteAllTextAsync(
            configPath,
            "[credential]\n" +
            $"    helper = \"!f() {{ printf helper > '{ToGitPath(markerPath)}'; exit 1; }}; f\"\n");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VerifiedCandidatePublisher.PublishVerifiedGitBranchAsync(
                new ProcessRunner(),
                workspace.SourceRepository,
                workspace.RepositoryManifest,
                "https://127.0.0.1:1/example/repository.git",
                "verified-publication",
                string.Empty,
                remoteTimeout: TimeSpan.FromSeconds(10),
                inheritedEnvironmentVariables:
                    new Dictionary<string, string?>
                    {
                        ["GIT_CONFIG_GLOBAL"] = configPath,
                        ["HOME"] = Path.Combine(
                            workspace.Root,
                            "global-home"),
                        ["USERPROFILE"] = Path.Combine(
                            workspace.Root,
                            "global-home"),
                        ["XDG_CONFIG_HOME"] = Path.Combine(
                            workspace.Root,
                            "global-xdg")
                    }));

        Assert.Contains("push verified commit", exception.Message);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task PublishVerifiedGitBranchAsync_IgnoresSystemUrlRewrite()
    {
        using var workspace = PublicationWorkspace.Create();
        var systemConfigPath = Path.Combine(workspace.Root, "system.gitconfig");
        await File.WriteAllTextAsync(
            systemConfigPath,
            $"""
            [url "{ToGitPath(workspace.TargetRepository)}"]
                insteadOf = blocked-target
            """);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VerifiedCandidatePublisher.PublishVerifiedGitBranchAsync(
                new ProcessRunner(),
                workspace.SourceRepository,
                workspace.RepositoryManifest,
                "blocked-target",
                "verified-publication",
                string.Empty,
                remoteTimeout: TimeSpan.FromSeconds(10),
                inheritedEnvironmentVariables:
                    new Dictionary<string, string?>
                    {
                        ["GIT_CONFIG_SYSTEM"] = systemConfigPath,
                        ["GIT_CONFIG_NOSYSTEM"] = "0"
                    }));

        Assert.Contains("push verified commit", exception.Message);
        Assert.False(HasRef(
            workspace.TargetRepository,
            "refs/heads/verified-publication"));
    }

    private static bool HasRef(string repository, string reference)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--git-dir");
        start.ArgumentList.Add(repository);
        start.ArgumentList.Add("rev-parse");
        start.ArgumentList.Add("--verify");
        start.ArgumentList.Add(reference);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git.");
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private static async Task WriteShellScriptAsync(
        string path,
        string content)
    {
        await File.WriteAllTextAsync(
            path,
            content.ReplaceLineEndings("\n"));
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

    private static string ToGitPath(string path) =>
        Path.GetFullPath(path).Replace('\\', '/');

    private sealed class PublicationWorkspace : IDisposable
    {
        private PublicationWorkspace(
            string root,
            string sourceRepository,
            string targetRepository,
            CandidateRepositoryManifest repositoryManifest)
        {
            Root = root;
            SourceRepository = sourceRepository;
            TargetRepository = targetRepository;
            RepositoryManifest = repositoryManifest;
        }

        public string Root { get; }

        public string SourceRepository { get; }

        public string TargetRepository { get; }

        public CandidateRepositoryManifest RepositoryManifest { get; }

        public static PublicationWorkspace Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "verified-publication-tests",
                Guid.NewGuid().ToString("N"));
            var sourceRepository = Path.Combine(root, "source");
            var targetRepository = Path.Combine(root, "target.git");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(sourceRepository);
            RunGit(sourceRepository, "init", "--quiet");
            RunGit(sourceRepository, "config", "user.email", "tests@example.invalid");
            RunGit(sourceRepository, "config", "user.name", "Verified Candidate Tests");
            File.WriteAllText(Path.Combine(sourceRepository, "tracked.txt"), "verified");
            RunGit(sourceRepository, "add", "-A");
            RunGit(sourceRepository, "commit", "--quiet", "-m", "initial");
            RunGit(root, "init", "--quiet", "--bare", targetRepository);
            var head = GitOutput(sourceRepository, ["rev-parse", "HEAD"])
                .Trim()
                .ToLowerInvariant();
            var tree = GitOutput(sourceRepository, ["rev-parse", "HEAD^{tree}"])
                .Trim()
                .ToLowerInvariant();
            return new PublicationWorkspace(
                root,
                sourceRepository,
                targetRepository,
                new CandidateRepositoryManifest(".", head, tree, string.Empty));
        }

        public void Git(params string[] arguments) =>
            RunGit(SourceRepository, arguments);

        public string GitOutput(string workingDirectory, params string[] arguments) =>
            GitOutput(workingDirectory, arguments.AsEnumerable());

        private static void RunGit(
            string workingDirectory,
            params string[] arguments) =>
            _ = GitOutput(workingDirectory, arguments.AsEnumerable());

        private static string GitOutput(
            string workingDirectory,
            IEnumerable<string> arguments)
        {
            var start = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start git.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(error);
            }
            return output;
        }

        public void Dispose()
        {
            VerifiedCandidatePublisherTestsCleanup.DeleteDirectoryBestEffort(Root);
        }
    }

    private sealed class PublicationDbContextFactory(
        DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);

        public Task<HarnessDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HarnessDbContext(options));
    }

    private static class VerifiedCandidatePublisherTestsCleanup
    {
        public static void DeleteDirectoryBestEffort(string path)
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
                // Test cleanup only.
            }
        }
    }
}

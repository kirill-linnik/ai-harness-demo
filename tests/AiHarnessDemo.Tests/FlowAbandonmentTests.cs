using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Data;
using AiHarnessDemo.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiHarnessDemo.Tests;

public sealed class FlowAbandonmentTests
{
    [Fact]
    public void SessionCleanup_AlwaysIncludesDefaultAndExplicitHomes()
    {
        var homes = FlowSessionCleaner.ResolveSessionHomes(
            [
                new FlowStep
                {
                    FlowRunId = Guid.NewGuid(),
                    AgentId = "one",
                    AgentName = "One",
                    AgentRole = "worker",
                    CopilotSessionHome = @"C:\explicit-home"
                },
                new FlowStep
                {
                    FlowRunId = Guid.NewGuid(),
                    AgentId = "two",
                    AgentName = "Two",
                    AgentRole = "worker",
                    CopilotSessionHome = string.Empty
                }
            ],
            @"C:\default-home");

        Assert.Contains(@"C:\explicit-home", homes);
        Assert.Contains(@"C:\default-home", homes);
        Assert.Equal(2, homes.Count);
    }

    [Fact]
    public async Task AbandonAsync_RemovesOnlyOwnedStagedSessionRootsAndIsIdempotent()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "abandon-staging-tests",
            Guid.NewGuid().ToString("N"));
        var copilotHome = Path.Combine(root, "copilot-home");
        Directory.CreateDirectory(copilotHome);
        await using var connection =
            new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite(connection)
            .Options;
        var databaseFactory = new AbandonDbContextFactory(options);
        Guid flowId;
        var sessionId = Guid.NewGuid();
        var siblingId = Guid.NewGuid();
        await using (var database =
                     await databaseFactory.CreateDbContextAsync())
        {
            await database.Database.EnsureCreatedAsync();
            var flow = new FlowRun
            {
                Title = "Abandon staged prompt",
                OriginalRequest = "Remove retained sensitive prompt.",
                Status = FlowStatus.Running,
                WorkspacePath = Path.Combine(root, "workspace")
            };
            flow.Steps.Add(new FlowStep
            {
                FlowRunId = flow.Id,
                Iteration = 1,
                AgentId = "worker",
                AgentName = "Worker",
                AgentRole = "worker",
                PlanStepKey = "implement",
                Status = StepStatus.Running,
                CopilotSessionId = sessionId,
                CopilotSessionHome = copilotHome
            });
            database.Flows.Add(flow);
            await database.SaveChangesAsync();
            flowId = flow.Id;
        }

        var ownedRoot = AgentManifestStager.GetSessionRoot(
            copilotHome,
            sessionId);
        var siblingRoot = AgentManifestStager.GetSessionRoot(
            copilotHome,
            siblingId);
        Directory.CreateDirectory(ownedRoot);
        Directory.CreateDirectory(siblingRoot);
        await File.WriteAllTextAsync(
            Path.Combine(ownedRoot, "sensitive-marker.txt"),
            "SENSITIVE_ABANDONED_PROMPT");
        await File.WriteAllTextAsync(
            Path.Combine(siblingRoot, "keep.txt"),
            "unrelated");
        var demoRevoker = new RecordingDemoRuntimeRevoker();
        var service = new FlowAbandonmentService(
            databaseFactory,
            new FakeExecutionController(),
            new FakeProcessCleaner(),
            new FlowSessionCleaner(
                new CopilotSessionJournal(),
                new AgentManifestStager()),
            new FakeWorkspaceManager(),
            new FlowLifecycleCoordinator(),
            NullLogger<FlowAbandonmentService>.Instance,
            demoRevoker);

        var first = await service.AbandonAsync(flowId);
        var repeated = await service.AbandonAsync(flowId);

        Assert.Equal(FlowStatus.Abandoned, first.Status);
        Assert.Equal(FlowStatus.Abandoned, repeated.Status);
        Assert.Equal(flowId, Assert.Single(demoRevoker.FlowIds));
        Assert.False(Directory.Exists(ownedRoot));
        Assert.True(Directory.Exists(siblingRoot));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task AbandonAsync_RemovesArtifactsButRetainsLedgerAndLearning()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite(connection)
            .Options;
        var databaseFactory = new AbandonDbContextFactory(options);
        Guid flowId;
        await using (var database = await databaseFactory.CreateDbContextAsync())
        {
            await database.Database.EnsureCreatedAsync();
            var flow = new FlowRun
            {
                Title = "Abandoned experiment",
                OriginalRequest = "Try an idea.",
                Status = FlowStatus.Running,
                WorkspacePath = @"C:\workspaces\flow",
                BranchName = "ai-harness/abandoned-experiment",
                OutcomeUrl = "#/preview/test",
                OutcomeLabel = "Candidate"
            };
            var step = new FlowStep
            {
                FlowRunId = flow.Id,
                Iteration = 1,
                AgentId = "software-engineer",
                AgentName = "Software Engineer",
                AgentRole = "software-engineer",
                Status = StepStatus.Running
            };
            flow.Steps.Add(step);
            database.Flows.Add(flow);
            database.Learnings.Add(new HarnessLearning
            {
                SourceFlowId = flow.Id,
                Category = "Experiment",
                Trigger = "Observed behavior",
                Lesson = "Retain this.",
                PromptRefinement = "Apply retained learning."
            });
            database.RoutingObservations.Add(new RoutingObservation
            {
                RoutingDecisionId = Guid.NewGuid(),
                FlowStepId = step.Id,
                Role = step.AgentRole,
                Accepted = true,
                OutcomeKind = "accepted-handoff"
            });
            await database.SaveChangesAsync();
            flowId = flow.Id;
        }

        var execution = new FakeExecutionController();
        var processCleaner = new FakeProcessCleaner();
        var sessionCleaner = new FakeSessionCleaner();
        var workspace = new FakeWorkspaceManager();
        var service = new FlowAbandonmentService(
            databaseFactory,
            execution,
            processCleaner,
            sessionCleaner,
            workspace,
            new FlowLifecycleCoordinator(),
            NullLogger<FlowAbandonmentService>.Instance);

        var result = await service.AbandonAsync(flowId);

        Assert.Equal(FlowStatus.Abandoned, result.Status);
        Assert.Equal(2, result.ProcessesStopped);
        Assert.Equal([4173, 5284], result.ListeningPortsReleased);
        Assert.Equal(3, result.CopilotSessionsDeleted);
        Assert.Equal(2, result.WorktreesRemoved);
        Assert.True(execution.Cancelled);
        Assert.True(workspace.Removed);
        await using var check = await databaseFactory.CreateDbContextAsync();
        var stored = await check.Flows
            .Include(item => item.Steps)
            .Include(item => item.Events)
            .SingleAsync();
        Assert.Equal(FlowStatus.Abandoned, stored.Status);
        Assert.Equal("Abandoned", stored.OutcomeLabel);
        Assert.Empty(stored.OutcomeUrl);
        Assert.Equal(StepStatus.Skipped, Assert.Single(stored.Steps).Status);
        Assert.Contains(stored.Events, item => item.Type == "flow.abandoned");
        Assert.Equal(1, await check.Learnings.CountAsync());
        Assert.Equal(1, await check.RoutingObservations.CountAsync());
    }

    [Fact]
    public async Task AbandonAsync_RejectsCustomerApprovedPublication()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite(connection)
            .Options;
        var databaseFactory = new AbandonDbContextFactory(options);
        await using (var database = await databaseFactory.CreateDbContextAsync())
        {
            await database.Database.EnsureCreatedAsync();
            var flow = new FlowRun
            {
                Title = "Published",
                OriginalRequest = "Published",
                Status = FlowStatus.Queued
            };
            var releaseStep = new FlowStep
            {
                FlowRunId = flow.Id,
                AgentId = "release-engineer",
                AgentName = "Release Engineer",
                AgentRole = "release-engineer",
                Status = StepStatus.Pending
            };
            flow.Steps.Add(releaseStep);
            database.Flows.Add(flow);
            database.GateRecords.Add(new AiHarnessDemo.Core.Gating.HandoffGateRecord
            {
                FlowRunId = flow.Id,
                FlowStepId = releaseStep.Id,
                ActionType = AiHarnessDemo.Core.Gating.HandoffActionType.Release,
                Decision = AiHarnessDemo.Core.Gating.HandoffGateDecision.AwaitingHumanApproval,
                TrustLevelAtDecision = AiHarnessDemo.Core.Gating.HandoffTrustLevel.Gated,
                Resolved = true,
                Approved = true
            });
            await database.SaveChangesAsync();
        }
        var service = new FlowAbandonmentService(
            databaseFactory,
            new FakeExecutionController(),
            new FakeProcessCleaner(),
            new FakeSessionCleaner(),
            new FakeWorkspaceManager(),
            new FlowLifecycleCoordinator(),
            NullLogger<FlowAbandonmentService>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AbandonAsync(databaseFactory.CreateDbContext().Flows.Single().Id));

        Assert.Contains("approval is already recorded", exception.Message);
    }

    [Fact]
    public async Task AbandonAsync_RejectsAcceptedStudioDeliveryBeforePublication()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite(connection)
            .Options;
        var databaseFactory = new AbandonDbContextFactory(options);
        Guid flowId;
        await using (var database = await databaseFactory.CreateDbContextAsync())
        {
            await database.Database.EnsureCreatedAsync();
            var flow = new FlowRun
            {
                Title = "Accepted Delivery",
                OriginalRequest = "Publish after acceptance.",
                ContractVersion = "studio-v2",
                Kind = FlowKind.Delivery,
                Status = FlowStatus.Queued,
                OutcomeOwnerPlanStepKey = "outcome",
                PublicationPlanStepKey = "publish"
            };
            var owner = new FlowStep
            {
                FlowRunId = flow.Id,
                Iteration = flow.Iteration,
                AgentId = "worker",
                AgentName = "Worker",
                AgentRole = "worker",
                PlanStepKey = "outcome",
                IsOutcomeOwner = true,
                Status = StepStatus.Completed
            };
            flow.Steps.Add(owner);
            flow.GateRecords.Add(new AiHarnessDemo.Core.Gating.HandoffGateRecord
            {
                FlowRunId = flow.Id,
                FlowStepId = owner.Id,
                ActionType =
                    AiHarnessDemo.Core.Gating.HandoffActionType.CustomerReview,
                Decision =
                    AiHarnessDemo.Core.Gating.HandoffGateDecision.AutoApproved,
                ReviewDecision = ReviewDecision.Accepted,
                TrustLevelAtDecision =
                    AiHarnessDemo.Core.Gating.HandoffTrustLevel.Gated,
                Resolved = true,
                Approved = true,
                ResolvedBy = "customer",
                ResolvedAt = DateTimeOffset.UtcNow
            });
            database.Flows.Add(flow);
            await database.SaveChangesAsync();
            flowId = flow.Id;
        }
        var execution = new FakeExecutionController();
        var service = new FlowAbandonmentService(
            databaseFactory,
            execution,
            new FakeProcessCleaner(),
            new FakeSessionCleaner(),
            new FakeWorkspaceManager(),
            new FlowLifecycleCoordinator(),
            NullLogger<FlowAbandonmentService>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AbandonAsync(flowId));

        Assert.Contains("approval is already recorded", exception.Message);
        Assert.False(execution.Cancelled);
    }

    [Fact]
    public async Task ConcurrentAbandonAsync_PerformsCleanupOnce()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite(connection)
            .Options;
        var databaseFactory = new AbandonDbContextFactory(options);
        Guid flowId;
        await using (var database = await databaseFactory.CreateDbContextAsync())
        {
            await database.Database.EnsureCreatedAsync();
            var flow = new FlowRun
            {
                Title = "Concurrent cleanup",
                OriginalRequest = "Abandon once.",
                Status = FlowStatus.Running,
                WorkspacePath = @"C:\workspaces\flow"
            };
            database.Flows.Add(flow);
            await database.SaveChangesAsync();
            flowId = flow.Id;
        }
        var processCleaner = new BlockingProcessCleaner();
        var workspace = new FakeWorkspaceManager();
        var service = new FlowAbandonmentService(
            databaseFactory,
            new FakeExecutionController(),
            processCleaner,
            new FakeSessionCleaner(),
            workspace,
            new FlowLifecycleCoordinator(),
            NullLogger<FlowAbandonmentService>.Instance);

        var first = service.AbandonAsync(flowId);
        await processCleaner.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = service.AbandonAsync(flowId);
        await Task.Delay(30);
        Assert.False(second.IsCompleted);
        processCleaner.Release.TrySetResult();

        var results = await Task.WhenAll(first, second);

        Assert.Equal(FlowStatus.Abandoned, results[0].Status);
        Assert.Equal(FlowStatus.Abandoned, results[1].Status);
        Assert.Equal(1, processCleaner.Calls);
        Assert.Equal(1, workspace.RemoveCalls);
        await using var verification = await databaseFactory.CreateDbContextAsync();
        Assert.Equal(
            1,
            await verification.FlowEvents.CountAsync(item =>
                item.FlowRunId == flowId &&
                item.Type == "flow.abandoned"));
    }

    private sealed class AbandonDbContextFactory(
        DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);

        public Task<HarnessDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FakeExecutionController : IFlowExecutionController
    {
        public bool Cancelled { get; private set; }

        public Task<bool> CancelAsync(
            Guid flowId,
            CancellationToken cancellationToken = default)
        {
            Cancelled = true;
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingDemoRuntimeRevoker : IDemoRuntimeRevoker
    {
        public List<Guid> FlowIds { get; } = [];

        public Task RevokeFlowAsync(
            Guid flowId,
            string reason,
            CancellationToken cancellationToken = default)
        {
            FlowIds.Add(flowId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProcessCleaner : IWorkspaceProcessCleaner
    {
        public Task<WorkspaceProcessCleanupResult> StopAsync(
            string workspacePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkspaceProcessCleanupResult(
                [101, 102],
                [4173, 5284]));
    }

    private sealed class BlockingProcessCleaner : IWorkspaceProcessCleaner
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WorkspaceProcessCleanupResult> StopAsync(
            string workspacePath,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new WorkspaceProcessCleanupResult([], []);
        }
    }

    private sealed class FakeSessionCleaner : IFlowSessionCleaner
    {
        public Task<int> DeleteAsync(
            FlowRun flow,
            IReadOnlyCollection<FlowStep> steps,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(3);
    }

    private sealed class FakeWorkspaceManager : IWorkspaceManager
    {
        public bool Removed { get; private set; }

        public int RemoveCalls { get; private set; }

        public Task<WorkspaceInfo> PrepareAsync(
            FlowRun flow,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not used.");

        public Task<WorkspaceCleanupResult> RemoveAsync(
            FlowRun flow,
            CancellationToken cancellationToken = default)
        {
            Removed = true;
            RemoveCalls++;
            return Task.FromResult(new WorkspaceCleanupResult(2, 2, 1));
        }
    }
}

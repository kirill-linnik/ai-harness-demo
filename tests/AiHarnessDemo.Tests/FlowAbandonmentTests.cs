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

    private sealed class FakeProcessCleaner : IWorkspaceProcessCleaner
    {
        public Task<WorkspaceProcessCleanupResult> StopAsync(
            string workspacePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkspaceProcessCleanupResult(
                [101, 102],
                [4173, 5284]));
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

        public Task<WorkspaceInfo> PrepareAsync(
            FlowRun flow,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not used.");

        public Task<WorkspaceCleanupResult> RemoveAsync(
            FlowRun flow,
            CancellationToken cancellationToken = default)
        {
            Removed = true;
            return Task.FromResult(new WorkspaceCleanupResult(2, 2, 1));
        }
    }
}

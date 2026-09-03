using System.Text.Json;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Data;
using AiHarnessDemo.Infrastructure;
using AiHarnessDemo.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiHarnessDemo.Tests;

public sealed class CopilotSessionJournalTests
{
    [Fact]
    public async Task InspectAsync_RecoversACompletedFinalHandoff()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: true,
            persistSessionId: true);

        var snapshot = await CopilotSessionJournal.InspectDirectoryAsync(
            fixture.CopilotHome,
            fixture.SessionDirectory,
            fixture.SessionId);

        Assert.Equal(CopilotSessionJournalState.Completed, snapshot.State);
        Assert.Equal(fixture.SessionId, snapshot.SessionId);
        Assert.Equal("Software Engineer", snapshot.AgentName);
        Assert.Equal(fixture.WorkspacePath, snapshot.WorkspacePath);
        Assert.Contains("HANDOFF_STATUS: COMPLETE", snapshot.Result!.OutputSummary);
    }

    [Fact]
    public async Task InspectAsync_RecoversACompletedHandoffWhenCliNeverShutsDownCleanly()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: true,
            persistSessionId: true,
            includeShutdown: false);

        var snapshot = await CopilotSessionJournal.InspectDirectoryAsync(
            fixture.CopilotHome,
            fixture.SessionDirectory,
            fixture.SessionId);

        Assert.Equal(CopilotSessionJournalState.Completed, snapshot.State);
        Assert.Contains("HANDOFF_STATUS: COMPLETE", snapshot.Result!.OutputSummary);
    }

    [Fact]
    public async Task InspectAsync_TreatsAnOpenTurnAndMalformedTailAsInterrupted()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: false,
            persistSessionId: true);

        var snapshot = await CopilotSessionJournal.InspectDirectoryAsync(
            fixture.CopilotHome,
            fixture.SessionDirectory,
            fixture.SessionId);

        Assert.Equal(CopilotSessionJournalState.Interrupted, snapshot.State);
        Assert.Null(snapshot.Result);
    }

    [Fact]
    public async Task InspectAsync_DoesNotReuseAnEarlierCompletedTurnAfterResume()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: true,
            persistSessionId: true);
        var resumedAt = DateTimeOffset.UtcNow;
        await File.AppendAllLinesAsync(
            Path.Combine(fixture.SessionDirectory, "events.jsonl"),
            [
                RecoveryFixture.Serialize(
                    "session.resume",
                    resumedAt,
                    new
                    {
                        sessionId = fixture.SessionId,
                        context = new
                        {
                            cwd = fixture.WorkspacePath
                        }
                    }),
                RecoveryFixture.Serialize(
                    "assistant.turn_start",
                    resumedAt.AddSeconds(1),
                    new
                    {
                        turnId = "1"
                    })
            ]);

        var snapshot = await CopilotSessionJournal.InspectDirectoryAsync(
            fixture.CopilotHome,
            fixture.SessionDirectory,
            fixture.SessionId);

        Assert.Equal(CopilotSessionJournalState.Interrupted, snapshot.State);
        Assert.Null(snapshot.Result);
    }

    [Fact]
    public async Task InspectAsync_IgnoresAStaleLockOwnedByAnotherProcess()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: false,
            persistSessionId: true);
        await File.WriteAllTextAsync(
            Path.Combine(
                fixture.SessionDirectory,
                $"inuse.{Environment.ProcessId}.lock"),
            Environment.ProcessId.ToString());

        var snapshot = await CopilotSessionJournal.InspectDirectoryAsync(
            fixture.CopilotHome,
            fixture.SessionDirectory,
            fixture.SessionId);

        Assert.Equal(CopilotSessionJournalState.Interrupted, snapshot.State);
        Assert.Empty(snapshot.ActiveProcessIds);
    }
}

public sealed class WorkflowRestartRecoveryTests
{
    [Fact]
    public async Task RecoveryDiscoversAndCompletesAFinishedCopilotSession()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: true,
            persistSessionId: false);

        var recoveredFlows = await fixture.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);

        await using var database = await fixture.DatabaseFactory.CreateDbContextAsync();
        var step = await database.FlowSteps.SingleAsync();
        var flow = await database.Flows.SingleAsync();
        Assert.Equal(StepStatus.Completed, step.Status);
        Assert.Equal(AgentRunPhase.Succeeded, step.Phase);
        Assert.Equal(fixture.SessionId, step.CopilotSessionId);
        Assert.Contains("HANDOFF_STATUS: COMPLETE", step.OutputSummary);
        Assert.Equal(FlowStatus.Queued, flow.Status);
        Assert.Contains(flow.Id, recoveredFlows);
        Assert.Single(await database.GateRecords.ToListAsync());
        Assert.Contains(
            await database.FlowEvents.ToListAsync(),
            item => item.Type == "step.session-output-recovered");
    }

    [Fact]
    public async Task RecoveryQueuesAnIncompleteCopilotSessionForExplicitResume()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: false,
            persistSessionId: true);

        var recoveredFlows = await fixture.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);

        await using var database = await fixture.DatabaseFactory.CreateDbContextAsync();
        var step = await database.FlowSteps.SingleAsync();
        Assert.Equal(StepStatus.Pending, step.Status);
        Assert.Equal(AgentRunPhase.CanceledByReconciliation, step.Phase);
        Assert.Equal(fixture.SessionId, step.CopilotSessionId);
        Assert.Contains(step.FlowRunId, recoveredFlows);
        Assert.Contains(
            await database.FlowEvents.ToListAsync(),
            item => item.Type == "step.resume-queued");
    }

    [Fact]
    public async Task RecoveryIsolatesABadSessionAndContinuesOtherFlows()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: true,
            persistSessionId: true);
        var badFlow = new FlowRun
        {
            Title = "Bad recovery",
            OriginalRequest = "Bad recovery",
            ConsolidatedRequest = "Bad recovery",
            Status = FlowStatus.Running,
            RepositoryPath = fixture.Root,
            RepositoryKnowledge = "Test repository.",
            WorkspacePath = Path.Combine(fixture.Root, "bad-workspace")
        };
        badFlow.Steps.Add(new FlowStep
        {
            FlowRunId = badFlow.Id,
            Iteration = 1,
            Sequence = 40,
            AgentId = "software-engineer",
            AgentName = "Software Engineer",
            AgentRole = "software-engineer",
            Status = StepStatus.Running,
            Phase = AgentRunPhase.StreamingTurn,
            StartedAt = DateTimeOffset.UtcNow,
            CopilotSessionId = Guid.NewGuid(),
            CopilotSessionHome = "\0"
        });
        await using (var database = await fixture.DatabaseFactory.CreateDbContextAsync())
        {
            database.Flows.Add(badFlow);
            await database.SaveChangesAsync();
        }

        var recoveredFlows = await fixture.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);

        await using var verification = await fixture.DatabaseFactory.CreateDbContextAsync();
        var recoveredStep = await verification.FlowSteps.SingleAsync(
            item => item.FlowRunId == fixture.FlowId);
        var failedFlow = await verification.Flows.SingleAsync(item => item.Id == badFlow.Id);
        Assert.Equal(StepStatus.Completed, recoveredStep.Status);
        Assert.Equal(FlowStatus.Failed, failedFlow.Status);
        Assert.Contains(fixture.FlowId, recoveredFlows);
        Assert.DoesNotContain(badFlow.Id, recoveredFlows);
    }

    [Fact]
    public async Task RecoveryAutomaticallyContinuesAFailedStallWithCompletedOutput()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: true,
            persistSessionId: false,
            includeShutdown: false);
        await using (var database = await fixture.DatabaseFactory.CreateDbContextAsync())
        {
            var flow = await database.Flows.Include(item => item.Steps).SingleAsync();
            var failedStep = Assert.Single(flow.Steps);
            flow.Status = FlowStatus.Failed;
            flow.FailureReason = "Agent process produced no output for 300 seconds.";
            failedStep.Status = StepStatus.Failed;
            failedStep.Phase = AgentRunPhase.Stalled;
            failedStep.CompletedAt = DateTimeOffset.UtcNow;
            flow.Steps.Add(new FlowStep
            {
                FlowRunId = flow.Id,
                Iteration = flow.Iteration,
                Sequence = 50,
                AgentId = "quality-engineer",
                AgentName = "Quality Engineer",
                AgentRole = "quality-engineer",
                Status = StepStatus.Skipped,
                Phase = AgentRunPhase.Failed,
                CompletedAt = DateTimeOffset.UtcNow
            });
            await database.SaveChangesAsync();
        }

        var recoveredFlows = await fixture.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);

        await using var verification =
            await fixture.DatabaseFactory.CreateDbContextAsync();
        var recovered = await verification.Flows
            .Include(item => item.Steps)
            .Include(item => item.Events)
            .SingleAsync();
        Assert.Contains(fixture.FlowId, recoveredFlows);
        Assert.Equal(FlowStatus.Queued, recovered.Status);
        Assert.Equal(
            StepStatus.Completed,
            recovered.Steps.Single(step => step.Sequence == 40).Status);
        Assert.Equal(
            StepStatus.Pending,
            recovered.Steps.Single(step => step.AgentRole == "quality-engineer").Status);
        Assert.Contains(
            recovered.Events,
            item => item.Type == "flow.completed-output-auto-recovered");
    }

    [Fact]
    public async Task ManualRestartPreservesFailureAndQueuesAResumableRetry()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: false,
            persistSessionId: true);
        await using (var database = await fixture.DatabaseFactory.CreateDbContextAsync())
        {
            var flow = await database.Flows.Include(item => item.Steps).SingleAsync();
            var failedStep = Assert.Single(flow.Steps);
            flow.Status = FlowStatus.Failed;
            flow.FailureReason = "Agent process produced no output for 300 seconds.";
            failedStep.Status = StepStatus.Failed;
            failedStep.Phase = AgentRunPhase.Stalled;
            failedStep.CompletedAt = DateTimeOffset.UtcNow;
            flow.Steps.Add(new FlowStep
            {
                FlowRunId = flow.Id,
                Iteration = flow.Iteration,
                Sequence = 50,
                AgentId = "quality-engineer",
                AgentName = "Quality Engineer",
                AgentRole = "quality-engineer",
                Status = StepStatus.Skipped,
                Phase = AgentRunPhase.Failed,
                CompletedAt = DateTimeOffset.UtcNow
            });
            await database.SaveChangesAsync();
        }

        var restarted = await fixture.Engine.RestartFailedFlowAsync(
            fixture.FlowId,
            CancellationToken.None);

        Assert.Equal(FlowStatus.Queued, restarted.Status);
        Assert.Empty(restarted.FailureReason);
        var originalFailure = restarted.Steps.Single(step => step.Sequence == 40);
        Assert.Equal(StepStatus.Failed, originalFailure.Status);
        var retry = restarted.Steps.Single(
            step => step.Label == "Manual restart of Software Engineer");
        Assert.Equal(50, retry.Sequence);
        Assert.Equal(2, retry.Attempt);
        Assert.Equal(StepStatus.Pending, retry.Status);
        Assert.Equal(AgentRunPhase.CanceledByReconciliation, retry.Phase);
        Assert.Equal(fixture.SessionId, retry.CopilotSessionId);
        Assert.DoesNotContain("300 seconds", retry.InputSummary);
        Assert.Contains("role-specific assignment", retry.InputSummary);
        var downstream = restarted.Steps.Single(step => step.AgentRole == "quality-engineer");
        Assert.Equal(60, downstream.Sequence);
        Assert.Equal(StepStatus.Pending, downstream.Status);
        Assert.Contains(
            restarted.Events,
            item => item.Type == "step.manual-retry-scheduled");
        Assert.Contains(
            restarted.Events,
            item =>
                item.Type == "flow.manual-restart" &&
                item.Message.Contains("300 seconds", StringComparison.Ordinal));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Engine.RestartFailedFlowAsync(
                fixture.FlowId,
                CancellationToken.None));
        Assert.Contains("Only a failed flow", exception.Message);
    }

    [Fact]
    public async Task ManualRestartSkipsJournalDiscoveryForPreLaunchFailure()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: true,
            persistSessionId: false);
        await using (var database = await fixture.DatabaseFactory.CreateDbContextAsync())
        {
            var flow = await database.Flows.Include(item => item.Steps).SingleAsync();
            var failedStep = Assert.Single(flow.Steps);
            flow.Status = FlowStatus.Failed;
            flow.FailureReason = "Workflow template failed.";
            failedStep.Status = StepStatus.Failed;
            failedStep.Phase = AgentRunPhase.Failed;
            failedStep.CopilotSessionId = Guid.NewGuid();
            failedStep.CopilotSessionHome = fixture.CopilotHome;
            failedStep.CompletedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync();
        }

        var restarted = await fixture.Engine.RestartFailedFlowAsync(
            fixture.FlowId,
            CancellationToken.None);

        var retry = restarted.Steps.Single(step =>
            step.Label == "Manual restart of Software Engineer");
        Assert.Equal(StepStatus.Pending, retry.Status);
        Assert.Null(retry.CopilotSessionId);
        Assert.DoesNotContain(
            restarted.Events,
            item => item.Type == "agent.session-discovered");
    }

    [Fact]
    public async Task ManualRestartRecoversCompletedOutputInsteadOfRerunningTheAgent()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: true,
            persistSessionId: false,
            includeShutdown: false);
        await using (var database = await fixture.DatabaseFactory.CreateDbContextAsync())
        {
            var flow = await database.Flows.Include(item => item.Steps).SingleAsync();
            var failedStep = Assert.Single(flow.Steps);
            flow.Status = FlowStatus.Failed;
            flow.FailureReason = "Agent process produced no output for 300 seconds.";
            failedStep.Status = StepStatus.Failed;
            failedStep.Phase = AgentRunPhase.Stalled;
            failedStep.CompletedAt = DateTimeOffset.UtcNow;
            flow.Steps.Add(new FlowStep
            {
                FlowRunId = flow.Id,
                Iteration = flow.Iteration,
                Sequence = 50,
                AgentId = "quality-engineer",
                AgentName = "Quality Engineer",
                AgentRole = "quality-engineer",
                Status = StepStatus.Skipped,
                Phase = AgentRunPhase.Failed,
                CompletedAt = DateTimeOffset.UtcNow
            });
            await database.SaveChangesAsync();
        }

        var restarted = await fixture.Engine.RestartFailedFlowAsync(
            fixture.FlowId,
            CancellationToken.None);

        Assert.Equal(FlowStatus.Queued, restarted.Status);
        Assert.Equal(
            StepStatus.Completed,
            restarted.Steps.Single(step => step.Sequence == 40).Status);
        Assert.Equal(
            StepStatus.Pending,
            restarted.Steps.Single(step => step.AgentRole == "quality-engineer").Status);
        Assert.DoesNotContain(
            restarted.Steps,
            step => step.Label.StartsWith("Manual restart of", StringComparison.Ordinal));
        Assert.Contains(
            restarted.Events,
            item => item.Type == "flow.completed-output-recovered");
    }
}

internal sealed class RecoveryFixture : IAsyncDisposable
{
    private readonly WorkflowDefinitionProvider _workflowProvider;
    private readonly HandoffGateEngine _gate;

    private RecoveryFixture(
        string root,
        string copilotHome,
        string workspacePath,
        Guid sessionId,
        Guid flowId,
        TestDbContextFactory databaseFactory,
        WorkflowDefinitionProvider workflowProvider,
        HandoffGateEngine gate,
        WorkflowEngine engine)
    {
        Root = root;
        CopilotHome = copilotHome;
        WorkspacePath = workspacePath;
        SessionId = sessionId;
        FlowId = flowId;
        DatabaseFactory = databaseFactory;
        _workflowProvider = workflowProvider;
        _gate = gate;
        Engine = engine;
    }

    public string Root { get; }

    public string CopilotHome { get; }

    public string WorkspacePath { get; }

    public Guid SessionId { get; }

    public Guid FlowId { get; }

    public string SessionDirectory =>
        Path.Combine(CopilotHome, "session-state", SessionId.ToString("D"));

    public TestDbContextFactory DatabaseFactory { get; }

    public WorkflowEngine Engine { get; }

    public static async Task<RecoveryFixture> CreateAsync(
        bool completed,
        bool persistSessionId,
        bool includeShutdown = true)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-restart-{Guid.NewGuid():N}");
        var agentsDirectory = Path.Combine(root, ".github", "agents");
        var workspacePath = Path.Combine(root, "workspace");
        var copilotHome = Path.Combine(root, "copilot-home");
        var databasePath = Path.Combine(root, "harness.db");
        Directory.CreateDirectory(agentsDirectory);
        Directory.CreateDirectory(workspacePath);
        var sessionId = Guid.NewGuid();
        var sessionDirectory = Path.Combine(
            copilotHome,
            "session-state",
            sessionId.ToString("D"));
        Directory.CreateDirectory(sessionDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(sessionDirectory, "events.jsonl"),
            BuildJournal(sessionId, workspacePath, completed, includeShutdown));

        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;
        var databaseFactory = new TestDbContextFactory(options);
        var flow = new FlowRun
        {
            Title = "Recover implementation",
            OriginalRequest = "Recover implementation",
            ConsolidatedRequest = "Complete the implementation.",
            Status = FlowStatus.Running,
            RepositoryPath = root,
            RepositoryKnowledge = "Test repository.",
            WorkspacePath = workspacePath
        };
        flow.Steps.Add(new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = 1,
            Sequence = 40,
            AgentId = "software-engineer",
            AgentName = "Software Engineer",
            AgentRole = "software-engineer",
            Label = "Execute Software Engineer contract",
            Status = StepStatus.Running,
            Phase = AgentRunPhase.StreamingTurn,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CopilotSessionId = persistSessionId ? sessionId : null,
            CopilotSessionHome = copilotHome
        });
        await using (var database = await databaseFactory.CreateDbContextAsync())
        {
            await database.Database.EnsureCreatedAsync();
            database.Flows.Add(flow);
            await database.SaveChangesAsync();
        }

        var paths = new HarnessPaths(root, agentsDirectory, databasePath);
        var workflowProvider = new WorkflowDefinitionProvider(
            paths,
            new WorkflowLoader(),
            NullLogger<WorkflowDefinitionProvider>.Instance);
        var gate = new HandoffGateEngine();
        gate.SetTrustLevel(HandoffActionType.Advance, HandoffTrustLevel.Auto);
        var journal = new CopilotSessionJournal();
        var engine = new WorkflowEngine(
            databaseFactory,
            new AgentCatalog(paths, databaseFactory),
            new FlowPlanner(),
            new FixedModelRouter(),
            new BootstrapTaskProfileFactory(),
            TestRoutingSupport.Recorder(databaseFactory),
            new NeverWorkspaceManager(),
            new NeverAgentRunner(),
            gate,
            journal,
            workflowProvider,
            NullLogger<WorkflowEngine>.Instance);
        return new RecoveryFixture(
            root,
            copilotHome,
            workspacePath,
            sessionId,
            flow.Id,
            databaseFactory,
            workflowProvider,
            gate,
            engine);
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        _workflowProvider.Dispose();
        Directory.Delete(Root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private static string BuildJournal(
        Guid sessionId,
        string workspacePath,
        bool completed,
        bool includeShutdown)
    {
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-4);
        var events = new List<string>
        {
            Serialize(
                "session.start",
                startedAt,
                new
                {
                    sessionId,
                    context = new
                    {
                        cwd = workspacePath
                    }
                }),
            Serialize(
                "subagent.selected",
                startedAt.AddSeconds(1),
                new
                {
                    agentName = "Software Engineer",
                    agentDisplayName = "Software Engineer"
                }),
            Serialize(
                "assistant.turn_start",
                startedAt.AddSeconds(2),
                new
                {
                    turnId = "0"
                })
        };
        if (completed)
        {
            events.Add(Serialize(
                "assistant.message",
                startedAt.AddSeconds(3),
                new
                {
                    turnId = "0",
                    content =
                        "HANDOFF_STATUS: COMPLETE\n\n## Decision\n\nImplementation is complete.",
                    toolRequests = Array.Empty<object>()
                }));
            events.Add(Serialize(
                "assistant.turn_end",
                startedAt.AddSeconds(4),
                new
                {
                    turnId = "0"
                }));
            if (includeShutdown)
            {
                events.Add(Serialize(
                    "session.shutdown",
                    startedAt.AddSeconds(5),
                    new
                    {
                        shutdownType = "routine"
                    }));
            }
        }
        else
        {
            events.Add("""{"type":"assistant.message","data":{"content":""");
        }

        return string.Join(Environment.NewLine, events) + Environment.NewLine;
    }

    internal static string Serialize(
        string type,
        DateTimeOffset timestamp,
        object data) =>
        JsonSerializer.Serialize(new
        {
            type,
            timestamp,
            data
        });

    internal sealed class TestDbContextFactory(
        DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);

        public Task<HarnessDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class NeverWorkspaceManager : IWorkspaceManager
    {
        public Task<WorkspaceInfo> PrepareAsync(
            FlowRun flow,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Recovery must not prepare a new workspace.");
    }

    private sealed class NeverAgentRunner : IAgentRunner
    {
        public Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionContext context,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Recovery must not start a new agent.");
    }
}

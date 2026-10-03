using System.Diagnostics;
using System.Text.Json;
using AiHarnessDemo.Contracts;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Data;
using AiHarnessDemo.Infrastructure;
using AiHarnessDemo.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Retry;

namespace AiHarnessDemo.Tests;

public sealed class ExecutionPolicyTests
{
    private static readonly AgentExecutionPolicy Policy =
        new(300_000, 1_800_000, 3_600_000, 14_400_000, "original-revision");

    [Fact]
    public void ActiveStructuredWork_CrossesSoftThresholdWithoutTermination()
    {
        var clock = new ExecutionClock();
        var monitor = Monitor(clock);
        for (var minute = 0; minute <= 70; minute++)
        {
            monitor.Observe("""{"type":"assistant.message_delta","data":{"deltaContent":"working"}}""");
            var snapshot = monitor.Tick(force: true)!;
            monitor.ThrowIfTerminated();
            Assert.Null(snapshot.TerminationReason);
            Assert.Equal(minute >= 60, snapshot.SoftWarning);
            clock.Advance(TimeSpan.FromMinutes(1));
        }
    }

    [Fact]
    public void SoftWarning_IsImmediateAndDoesNotBypassSnapshotThrottlingRepeatedly()
    {
        var clock = new ExecutionClock();
        var monitor = Monitor(clock);
        clock.Advance(TimeSpan.FromMinutes(59));
        monitor.Observe("""{"type":"assistant.message","data":{"content":"not a completion"}}""");
        monitor.Tick(force: true);
        clock.Advance(TimeSpan.FromMinutes(1));
        monitor.Observe("""{"type":"assistant.message","data":{"content":"still working"}}""");
        Assert.True(monitor.Tick()!.SoftWarning);
        Assert.Null(monitor.Tick());
        monitor.ThrowIfTerminated();
    }

    [Fact]
    public void RawDiagnosticNoise_DoesNotRenewStructuredInactivity()
    {
        var clock = new ExecutionClock();
        var monitor = Monitor(clock);
        for (var minute = 0; minute < 6; minute++)
        {
            monitor.ObserveRawOutput();
            monitor.Observe("diagnostic output");
            monitor.Observe("""{"type":"session.info","data":{"message":"log noise"}}""");
            monitor.Observe("""{"type":"future.event","data":{}}""");
            clock.Advance(TimeSpan.FromMinutes(1));
        }
        var snapshot = monitor.Tick()!;
        Assert.NotNull(snapshot.LastRawOutputAt);
        Assert.Null(snapshot.LastStructuredEvent);
        Assert.Null(snapshot.LastStructuredActivityAt);
        Assert.Null(snapshot.CompletedToolCount);
        Assert.Equal("Stalled", snapshot.TerminationReason);
        Assert.Throws<ProcessStalledException>(monitor.ThrowIfTerminated);
    }

    [Fact]
    public void SilentToolAllowance_IsBoundedAndDuplicateStartsCannotRenewIt()
    {
        var clock = new ExecutionClock();
        var monitor = Monitor(clock);
        monitor.Observe(ToolStart("one"));
        var startedAt = monitor.Snapshot().ActiveToolStartedAt;
        clock.Advance(TimeSpan.FromMinutes(6));
        monitor.Tick(force: true);
        monitor.ThrowIfTerminated();
        Assert.Equal(360_000, monitor.Snapshot().ActiveToolElapsedMilliseconds);

        clock.Advance(TimeSpan.FromMinutes(18));
        monitor.Observe(ToolStart("one"));
        Assert.Equal(startedAt, monitor.Snapshot().ActiveToolStartedAt);
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal("Stalled", monitor.Tick(force: true)!.TerminationReason);
        Assert.Throws<ProcessStalledException>(monitor.ThrowIfTerminated);
    }

    [Fact]
    public void ToolCompletion_EndsAllowanceAndCountsDuplicatesOnlyOnce()
    {
        var clock = new ExecutionClock();
        var monitor = Monitor(clock);
        monitor.Observe(ToolStart("one"));
        clock.Advance(TimeSpan.FromMinutes(10));
        monitor.Observe(ToolComplete("one"));
        monitor.Observe(ToolComplete("one"));
        Assert.Null(monitor.Snapshot().ActiveTool);
        Assert.Equal(1, monitor.Snapshot().CompletedToolCount);
        clock.Advance(TimeSpan.FromMinutes(5));
        monitor.Tick(force: true);
        Assert.Throws<ProcessStalledException>(monitor.ThrowIfTerminated);
    }

    [Fact]
    public void UncorrelatedOrUnsafeToolSignals_DoNotGrantSilentAllowance()
    {
        var clock = new ExecutionClock();
        var monitor = Monitor(clock);
        monitor.Observe("""{"type":"tool.execution_start","data":{"toolName":"powershell"}}""");
        monitor.Observe("""{"type":"tool.execution_start","data":{"toolCallId":"one","toolName":"password=secret"}}""");
        monitor.Observe("""{"type":"tool.execution_progress","data":{"toolCallId":"unknown"}}""");
        clock.Advance(TimeSpan.FromMinutes(5));
        monitor.Tick();
        Assert.Null(monitor.Snapshot().ActiveTool);
        Assert.Throws<ProcessStalledException>(monitor.ThrowIfTerminated);
    }

    [Fact]
    public void AbsoluteBudget_BoundsEvenContinuouslyActiveWorkAndWinsOverStall()
    {
        var clock = new ExecutionClock();
        var monitor = Monitor(clock);
        clock.Advance(TimeSpan.FromHours(4));
        monitor.Observe(ToolStart("active"));
        monitor.Observe("""{"type":"assistant.message","data":{"content":"active"}}""");
        var snapshot = monitor.Tick()!;
        Assert.Equal("BudgetExhausted", snapshot.TerminationReason);
        Assert.Equal(0, snapshot.RemainingBudgetMilliseconds);
        Assert.Throws<ProcessBudgetExhaustedException>(monitor.ThrowIfTerminated);
    }

    [Fact]
    public async Task BudgetExhaustion_IsTypedAndExcludedFromTheRuntimeRetryPredicate()
    {
        var executions = 0;
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.Zero,
                ShouldHandle = new PredicateBuilder()
                    .Handle<AgentRunException>(AgentRunner.IsRetryableFailure)
            }).Build();
        var exception = await Assert.ThrowsAsync<AgentRunException>(async () =>
            await pipeline.ExecuteAsync(_ =>
            {
                executions++;
                return ValueTask.FromException(new AgentRunException(AssignmentExecutionBudget.ExhaustedReason,
                    AgentRunFailureKind.BudgetExhausted, canResumeSession: true));
            }));
        Assert.Equal(1, executions);
        Assert.Equal(AgentRunFailureKind.BudgetExhausted, exception.FailureKind);
        Assert.False(AgentRunner.ShouldResumeInterruptedSession(exception));
        Assert.Contains("Workspace edits are preserved", exception.Message);
    }

    [Fact]
    public async Task RetryResumeRestartAndReload_KeepDeadlineAndPolicy_NewRootGetsNewBudget()
    {
        await using var fixture = await BudgetFixture.CreateAsync();
        var original = await fixture.ResolveAsync();
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        var retry = fixture.Step("retry");
        retry.RetryOfStepId = fixture.RootStep.Id;
        retry.StableSemanticRootId = fixture.RootStep.Id;
        await fixture.AddAsync(retry);
        var changedWorkflow = fixture.Workflow with
        {
            Revision = "reloaded",
            Config = new WorkflowConfig
            {
                Copilot = new CopilotConfig { ExecutionBudgetMs = 28_800_000 }
            }
        };
        var resumed = await AssignmentExecutionBudget.ResolveAsync(
            fixture.Context(retry) with { ResumeSession = true, RecoverInterruptedSession = true },
            changedWorkflow, new BudgetFactory(fixture.Options), CancellationToken.None, fixture.Clock);
        Assert.Equal(original.DeadlineAt, resumed.DeadlineAt);
        Assert.Equal(original.PolicyJson, resumed.PolicyJson);
        var activity = new AgentExecutionMonitor(resumed, fixture.Clock).Tick(force: true)!;
        Assert.Equal(7_200_000, activity.RemainingBudgetMilliseconds);
        fixture.Clock.Advance(TimeSpan.FromHours(3));
        var afterDowntime = await AssignmentExecutionBudget.ResolveAsync(
            fixture.Context(retry), changedWorkflow, new BudgetFactory(fixture.Options),
            CancellationToken.None, fixture.Clock);
        var monitor = new AgentExecutionMonitor(afterDowntime, fixture.Clock);
        Assert.Equal("BudgetExhausted", monitor.Tick(force: true)!.TerminationReason);

        var newAssignment = fixture.Step("new-assignment");
        await fixture.AddAsync(newAssignment);
        var fresh = await AssignmentExecutionBudget.ResolveAsync(
            fixture.Context(newAssignment), changedWorkflow, fixture.Factory,
            CancellationToken.None, fixture.Clock);
        Assert.Equal(fixture.Clock.GetUtcNow().AddHours(8), fresh.DeadlineAt);
        Assert.NotEqual(original.RootStepId, fresh.RootStepId);
    }

    [Fact]
    public async Task LegacyPersistedAttempts_ChargeFromEarliestKnownStart_NotMigrationTime()
    {
        await using var fixture = await BudgetFixture.CreateAsync();
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var root = await database.FlowSteps.SingleAsync();
            root.StartedAt = fixture.Clock.GetUtcNow().AddHours(-5);
            await database.SaveChangesAsync();
        }
        var migrated = await fixture.ResolveAsync();
        Assert.True(migrated.DeadlineAt < fixture.Clock.GetUtcNow());
        Assert.Equal(fixture.Clock.GetUtcNow().AddHours(-5), migrated.StartedAt);
    }

    [Fact]
    public async Task WarningAndActivity_AreDurableSafeThrottledAndProjected()
    {
        await using var fixture = await BudgetFixture.CreateAsync();
        var budget = await fixture.ResolveAsync();
        var monitor = new AgentExecutionMonitor(budget, fixture.Clock);
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        monitor.Observe("""
            {"type":"tool.execution_start","data":{"toolCallId":"one","toolName":"powershell","arguments":{"command":"secret-command","token":"secret-token"}}}
            """);
        var snapshot = monitor.Tick(force: true)!;
        await AssignmentExecutionBudget.RecordActivityAsync(
            fixture.Context(fixture.RootStep), snapshot, fixture.Factory);
        await AssignmentExecutionBudget.RecordActivityAsync(
            fixture.Context(fixture.RootStep), snapshot, fixture.Factory);

        await using var database = await fixture.Factory.CreateDbContextAsync();
        var persisted = await database.FlowSteps.SingleAsync();
        var storedBudget = await database.AgentExecutionBudgets.SingleAsync();
        Assert.NotNull(storedBudget.SoftWarningAt);
        Assert.Single(await database.FlowEvents.Where(item => item.Type == "agent.soft-limit-warning").ToListAsync());
        Assert.Single(await database.FlowEvents.Where(item => item.Type == "agent.activity").ToListAsync());
        Assert.DoesNotContain("secret-", persisted.RuntimeActivityJson);
        var dto = persisted.ToDto();
        Assert.Equal("powershell", dto.RuntimeActivity!.ActiveTool);
        Assert.True(dto.RuntimeActivity.SoftWarning);
        Assert.Equal(budget.PolicyJson, dto.ExecutionPolicyJson);

        var resumed = new AgentExecutionMonitor(storedBudget, fixture.Clock);
        Assert.True(resumed.Tick(force: true)!.SoftWarning);
        await AssignmentExecutionBudget.RecordActivityAsync(
            fixture.Context(fixture.RootStep), resumed.Snapshot(), fixture.Factory);
        Assert.Equal(1, await database.FlowEvents.CountAsync(item => item.Type == "agent.soft-limit-warning"));
    }

    [Theory]
    [InlineData("turn_timeout_ms: 3600000")]
    [InlineData("stall_timeout_ms: 300000")]
    [InlineData("maximum_quality_stall_timeout_ms: 1800000")]
    [InlineData("soft_warnng_ms: 3600000")]
    [InlineData("inactivity_timeout_ms: 0")]
    [InlineData("silent_tool_timeout_ms: 1")]
    [InlineData("execution_budget_ms: 3600000")]
    [InlineData("soft_warning_ms: -1")]
    public void StrictConfiguration_RejectsLegacyUnknownAndInvalidSettings(string field)
    {
        using var artifact = WorkflowDefinitionTests.WorkflowArtifact.Create(
            $"---\ncopilot:\n  {field}\n---\nExecute {{{{ task }}}}.");
        Assert.Throws<WorkflowConfigurationException>(() => new WorkflowLoader().Load(artifact.Path));
    }

    [Fact]
    public async Task InvalidLegacyReload_BlocksNewWorkButPreservesEffectivePolicy()
    {
        using var artifact = WorkflowDefinitionTests.WorkflowArtifact.Create(
            "---\ncopilot:\n  soft_warning_ms: 3600000\n  execution_budget_ms: 14400000\n---\nExecute {{ task }}.");
        using var provider = new WorkflowDefinitionProvider(
            new HarnessPaths(artifact.Directory, Path.Combine(artifact.Directory, ".github", "agents"),
                Path.Combine(artifact.Directory, "harness.db")),
            new WorkflowLoader(), NullLogger<WorkflowDefinitionProvider>.Instance);
        var original = provider.GetEffective();
        File.WriteAllText(artifact.Path,
            "---\ncopilot:\n  turn_timeout_ms: 3600000\n---\nExecute {{ task }}.");
        await provider.ReloadAsync();
        Assert.False(provider.Status().Ready);
        Assert.Contains("Explicitly migrate", provider.Status().CurrentFileError);
        Assert.Equal(original.Revision, provider.GetEffective().Revision);
    }

    [Fact]
    public async Task SchemaUpgrade_IsIdempotentAndPreservesEarlierFlowSteps()
    {
        await using var fixture = await BudgetFixture.CreateAsync();
        await using var database = await fixture.Factory.CreateDbContextAsync();
        await database.Database.ExecuteSqlRawAsync(
            """
            DROP TABLE "AgentExecutionBudgets";
            ALTER TABLE "FlowSteps" DROP COLUMN "ExecutionBudgetRootId";
            ALTER TABLE "FlowSteps" DROP COLUMN "ExecutionPolicyJson";
            ALTER TABLE "FlowSteps" DROP COLUMN "RuntimeActivityJson";
            """);
        await DatabaseInitializer.EnsureExecutionBudgetSchemaAsync(database);
        await DatabaseInitializer.EnsureExecutionBudgetSchemaAsync(database);
        Assert.Equal(fixture.RootStep.Id, (await database.FlowSteps.SingleAsync()).Id);
        Assert.Empty(await database.AgentExecutionBudgets.ToListAsync());
        Assert.NotNull(await fixture.ResolveAsync());
    }

    [Fact]
    public async Task LiveJournal_TailsOnlyNewBoundEvents_HandlesPartialLinesAndDeduplicatesStdout()
    {
        await using var fixture = await BudgetFixture.CreateAsync();
        var home = Path.Combine(fixture.Root, "copilot-home");
        var sessionId = Guid.NewGuid();
        var directory = Path.Combine(home, "session-state", sessionId.ToString("D"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "events.jsonl");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            type = "session.start",
            data = new { sessionId, context = new { cwd = fixture.Root } }
        }) + "\n" + ToolStart("old") + "\n");
        var monitor = Monitor(fixture.Clock);
        await using var journal = new CopilotActivityJournal(
            home, sessionId, fixture.Root, monitor, NullLogger.Instance);
        await journal.PollAsync(CancellationToken.None);
        Assert.Null(monitor.Snapshot().ActiveTool);
        var start = ToolStart("new");
        await File.AppendAllTextAsync(path, start[..20]);
        await journal.PollAsync(CancellationToken.None);
        Assert.Null(monitor.Snapshot().ActiveTool);
        await File.AppendAllTextAsync(path, start[20..] + "\n");
        await journal.PollAsync(CancellationToken.None);
        Assert.Equal("powershell", monitor.Snapshot().ActiveTool);
        var complete = ToolComplete("new");
        monitor.Observe(complete);
        await File.AppendAllTextAsync(path, complete + "\n");
        await journal.PollAsync(CancellationToken.None);
        Assert.Equal(1, monitor.Snapshot().CompletedToolCount);
        Assert.Null(monitor.Snapshot().LastRawOutputAt);
    }

    [Fact]
    public async Task ProcessRunner_ActiveProcessCrossesSoftThreshold_AndPreservesCancellation()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await using var fixture = await BudgetFixture.CreateAsync();
        var script = Path.Combine(fixture.Root, "active.ps1");
        var pidFile = Path.Combine(fixture.Root, "pid.txt");
        await File.WriteAllTextAsync(script,
            $"[IO.File]::WriteAllText('{pidFile}', [string]$PID)\n" +
            $"Write-Output '{ToolStart("one")}'\nStart-Sleep -Seconds 120\n");
        var monitor = Monitor(fixture.Clock);
        var snapshots = new System.Collections.Concurrent.ConcurrentQueue<AgentRuntimeActivity>();
        monitor.Report = snapshots.Enqueue;
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromSeconds(15));
        var crossed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new ProcessRunner().RunAsync("pwsh", ["-NoProfile", "-File", script],
            fixture.Root, TimeSpan.FromHours(4), cancellation.Token,
            line =>
            {
                if (line.Contains("tool.execution_start", StringComparison.Ordinal))
                {
                    fixture.Clock.Advance(TimeSpan.FromMinutes(61));
                    monitor.Observe("""{"type":"assistant.message","data":{"content":"still active"}}""");
                    monitor.Report(monitor.Tick(force: true)!);
                    crossed.TrySetResult();
                }
            }, executionMonitor: monitor);
        await crossed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var pid = int.Parse(await File.ReadAllTextAsync(pidFile));
        using (var process = Process.GetProcessById(pid)) { Assert.False(process.HasExited); }
        Assert.Contains(snapshots, item => item.SoftWarning && item.TerminationReason is null);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(IsProcessAlive(pid));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessRunner_WatchdogsKillProcessTreeWithoutRollingBackEdits(bool exhaustBudget)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await using var fixture = await BudgetFixture.CreateAsync();
        var script = Path.Combine(fixture.Root, "hung.ps1");
        var pidFile = Path.Combine(fixture.Root, "pid.txt");
        var childPidFile = Path.Combine(fixture.Root, "child-pid.txt");
        var editFile = Path.Combine(fixture.Root, "preserved-edit.txt");
        await File.WriteAllTextAsync(script,
            $"[IO.File]::WriteAllText('{pidFile}', [string]$PID)\n" +
            $"[IO.File]::WriteAllText('{editFile}', 'workspace edit')\n" +
            "$child = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 120' -PassThru\n" +
            $"[IO.File]::WriteAllText('{childPidFile}', [string]$child.Id)\n" +
            $"[Console]::Error.WriteLine('{ToolStart("stderr-is-not-progress")}')\n" +
            "Write-Output 'diagnostic-noise'\nStart-Sleep -Seconds 120\n");
        var monitor = Monitor(fixture.Clock);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = new ProcessRunner().RunAsync("pwsh", ["-NoProfile", "-File", script],
            fixture.Root, TimeSpan.FromHours(4),
            cancellationToken: cancellation.Token,
            standardOutputLineReceived: _ => fixture.Clock.Advance(
                TimeSpan.FromHours(exhaustBudget ? 4 : 0.1)),
            executionMonitor: monitor);
        if (exhaustBudget)
        {
            await Assert.ThrowsAsync<ProcessBudgetExhaustedException>(() => run);
        }
        else
        {
            await Assert.ThrowsAsync<ProcessStalledException>(() => run);
        }
        var pid = int.Parse(await File.ReadAllTextAsync(pidFile));
        Assert.False(IsProcessAlive(pid));
        Assert.False(IsProcessAlive(int.Parse(await File.ReadAllTextAsync(childPidFile))));
        Assert.Equal("workspace edit", await File.ReadAllTextAsync(editFile));
        Assert.NotNull(monitor.Snapshot().LastRawOutputAt);
        Assert.Null(monitor.Snapshot().LastStructuredActivityAt);
    }

    private static bool IsProcessAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static AgentExecutionMonitor Monitor(ExecutionClock clock) =>
        new(Policy, clock.GetUtcNow(), clock.GetUtcNow().AddHours(4), clock: clock);

    private static string ToolStart(string id) =>
        JsonSerializer.Serialize(new { type = "tool.execution_start",
            data = new { toolCallId = id, toolName = "powershell" } });

    private static string ToolComplete(string id) =>
        JsonSerializer.Serialize(new { type = "tool.execution_complete",
            data = new { toolCallId = id, success = true } });

    internal sealed class ExecutionClock : TimeProvider
    {
        private long milliseconds;
        private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-10-03T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Origin.AddMilliseconds(Interlocked.Read(ref milliseconds));
        public override long GetTimestamp() => Interlocked.Read(ref milliseconds);
        public override long TimestampFrequency => 1_000;
        public void Advance(TimeSpan duration) => Interlocked.Add(ref milliseconds, (long)duration.TotalMilliseconds);
    }

    private sealed class BudgetFactory(DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);
        public Task<HarnessDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class BudgetFixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"execution-budget-{Guid.NewGuid():N}");
        public ExecutionClock Clock { get; } = new();
        public DbContextOptions<HarnessDbContext> Options { get; private set; } = null!;
        public BudgetFactory Factory { get; private set; } = null!;
        public FlowRun Flow { get; } = new() { Title = "Execution policy", OriginalRequest = "Implement policy" };
        public FlowStep RootStep { get; private set; } = null!;
        public WorkflowDefinition Workflow { get; private set; } = null!;

        public static async Task<BudgetFixture> CreateAsync()
        {
            var fixture = new BudgetFixture();
            Directory.CreateDirectory(fixture.Root);
            fixture.Options = new DbContextOptionsBuilder<HarnessDbContext>()
                .UseSqlite($"Data Source={Path.Combine(fixture.Root, "harness.db")};Pooling=False").Options;
            fixture.Factory = new BudgetFactory(fixture.Options);
            fixture.Workflow = new(new WorkflowConfig(), "{{ task }}", Path.Combine(fixture.Root, "WORKFLOW.md"),
                fixture.Clock.GetUtcNow(), "original-revision");
            fixture.RootStep = fixture.Step("root");
            await using var database = fixture.Factory.CreateDbContext();
            await database.Database.EnsureCreatedAsync();
            database.Flows.Add(fixture.Flow);
            database.FlowSteps.Add(fixture.RootStep);
            await database.SaveChangesAsync();
            return fixture;
        }

        public FlowStep Step(string key) => new()
        {
            FlowRunId = Flow.Id, Iteration = 1, Sequence = 10,
            AgentId = "software-engineer", AgentName = "Software Engineer", AgentRole = "software-engineer",
            PlanStepKey = key, Status = StepStatus.Pending
        };

        public AgentExecutionContext Context(FlowStep step) =>
            new(Flow.Id, 1, step.AgentId, step.AgentName, step.AgentRole, "model", "high", 1,
                "Implement policy", "", Root, Root, Guid.NewGuid(), AiHarnessDemo.Core.Domain.OutcomeType.Commit, "", [], [],
                FlowStepId: step.Id, PlanStepKey: step.PlanStepKey);

        public Task<AgentExecutionBudget> ResolveAsync() => AssignmentExecutionBudget.ResolveAsync(
            Context(RootStep), Workflow, Factory, CancellationToken.None, Clock);

        public async Task AddAsync(FlowStep step)
        {
            await using var database = Factory.CreateDbContext();
            database.FlowSteps.Add(step);
            await database.SaveChangesAsync();
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}

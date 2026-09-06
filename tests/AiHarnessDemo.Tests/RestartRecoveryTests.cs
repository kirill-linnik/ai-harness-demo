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
    public async Task InspectAsync_PreservesWorkspaceBindingForRecoveredToolEvidence()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: true,
            persistSessionId: true,
            includeToolCall: true);

        var snapshot = await CopilotSessionJournal.InspectDirectoryAsync(
            fixture.CopilotHome,
            fixture.SessionDirectory,
            fixture.SessionId);

        var toolCall = Assert.Single(snapshot.Result!.ToolCalls);
        Assert.Equal(fixture.WorkspacePath, toolCall.WorkingDirectory);
        Assert.Equal("Command", toolCall.ToolType);
        Assert.Equal("dotnet test", toolCall.NormalizedCommand);
        Assert.Equal(0, toolCall.ExitCode);
        Assert.Contains("passed", toolCall.ResultSummary);
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

    [Fact]
    public async Task DeleteWorkspaceSessionsAsync_RemovesOnlyMatchingFlowSessions()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: true,
            persistSessionId: true);
        var unrelatedId = Guid.NewGuid();
        var unrelatedDirectory = Path.Combine(
            fixture.CopilotHome,
            "session-state",
            unrelatedId.ToString("D"));
        Directory.CreateDirectory(unrelatedDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(unrelatedDirectory, "events.jsonl"),
            RecoveryFixture.Serialize(
                "session.start",
                DateTimeOffset.UtcNow,
                new
                {
                    sessionId = unrelatedId,
                    context = new
                    {
                        cwd = Path.Combine(fixture.Root, "unrelated")
                    }
                }));
        var journal = new CopilotSessionJournal();

        var deleted = await journal.DeleteWorkspaceSessionsAsync(
            [fixture.CopilotHome],
            fixture.WorkspacePath,
            [fixture.SessionId]);

        Assert.Equal(1, deleted);
        Assert.False(Directory.Exists(fixture.SessionDirectory));
        Assert.True(Directory.Exists(unrelatedDirectory));
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
    public void PendingManualRestartResolvesOnlyItsFailedStep()
    {
        var flowId = Guid.NewGuid();
        var failed = new FlowStep
        {
            FlowRunId = flowId,
            Iteration = 1,
            Sequence = 40,
            AgentId = "software-engineer",
            AgentName = "Software Engineer",
            AgentRole = "software-engineer",
            Status = StepStatus.Failed,
            Attempt = 1
        };
        var retry = new FlowStep
        {
            FlowRunId = flowId,
            Iteration = 1,
            Sequence = 50,
            AgentId = "software-engineer",
            AgentName = "Software Engineer",
            AgentRole = "software-engineer",
            Label = "Manual restart of Software Engineer",
            Status = StepStatus.Pending,
            Attempt = 2,
            RetryOfStepId = failed.Id
        };

        Assert.Null(WorkflowEngine.FindUnresolvedFailure([failed, retry]));

        retry.RetryOfStepId = Guid.NewGuid();

        Assert.Same(failed, WorkflowEngine.FindUnresolvedFailure([failed, retry]));
    }

    [Fact]
    public void ManualRetry_DoesNotResolveAnotherFailureForTheSameAgent()
    {
        var flowId = Guid.NewGuid();
        var firstFailure = new FlowStep
        {
            FlowRunId = flowId,
            Iteration = 1,
            Sequence = 20,
            AgentId = "software-engineer",
            AgentName = "Software Engineer",
            AgentRole = "software-engineer",
            Status = StepStatus.Failed,
            Attempt = 1
        };
        var secondFailure = new FlowStep
        {
            FlowRunId = flowId,
            Iteration = 1,
            Sequence = 40,
            AgentId = "software-engineer",
            AgentName = "Software Engineer",
            AgentRole = "software-engineer",
            Status = StepStatus.Failed,
            Attempt = 2
        };
        var secondRetry = new FlowStep
        {
            FlowRunId = flowId,
            Iteration = 1,
            Sequence = 50,
            AgentId = "software-engineer",
            AgentName = "Software Engineer",
            AgentRole = "software-engineer",
            Label = "Manual restart of Software Engineer",
            Status = StepStatus.Pending,
            Attempt = 3,
            RetryOfStepId = secondFailure.Id
        };

        Assert.Same(
            firstFailure,
            WorkflowEngine.FindUnresolvedFailure(
                [firstFailure, secondFailure, secondRetry]));
    }

    [Fact]
    public void CausalPushback_KeepsTheRecoveryChainActive()
    {
        var flowId = Guid.NewGuid();
        var failure = new FlowStep
        {
            FlowRunId = flowId,
            Iteration = 1,
            Sequence = 20,
            AgentId = "quality-engineer",
            AgentName = "Quality Engineer",
            AgentRole = "quality-engineer",
            Status = StepStatus.Failed,
            Attempt = 1
        };
        var pushedBackRetry = new FlowStep
        {
            FlowRunId = flowId,
            Iteration = 1,
            Sequence = 30,
            AgentId = "quality-engineer",
            AgentName = "Quality Engineer",
            AgentRole = "quality-engineer",
            Status = StepStatus.Pushback,
            Attempt = 2,
            RetryOfStepId = failure.Id
        };

        Assert.Null(WorkflowEngine.FindUnresolvedFailure(
            [failure, pushedBackRetry]));
        Assert.Same(
            pushedBackRetry,
            WorkflowEngine.FindUnresolvedPushback(
                [failure, pushedBackRetry]));
    }

    [Fact]
    public void OutcomeQaPushbackRetry_PreservesTypedMetadataAndStableRoot()
    {
        var flow = new FlowRun
        {
            Title = "Governed pushback",
            OriginalRequest = "Governed pushback"
        };
        var blocked = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = 1,
            Sequence = 30,
            AgentId = "quality-engineer",
            AgentName = "Quality Engineer",
            AgentRole = "quality-engineer",
            Label = "Unexpected QA label",
            Kind = FlowStepKind.OutcomeQa,
            Status = StepStatus.Pushback,
            Attempt = 2,
            OutcomeQaRound = 2,
            OutcomePlanHash = "sha256:" + new string('a', 64),
            StableSemanticRootId = Guid.NewGuid()
        };
        var upstreamOwner = new AgentRecord
        {
            Id = "software-engineer",
            Name = "Software Engineer",
            Description = "Implements the fix.",
            Role = "software-engineer",
            SourcePath = "software-engineer.agent.md"
        };

        var (_, retry) = WorkflowEngine.CreateRecoverySteps(
            flow,
            blocked,
            upstreamOwner,
            revisionAttempt: 3);

        Assert.Equal(FlowStepKind.OutcomeQa, retry.Kind);
        Assert.Equal(2, retry.OutcomeQaRound);
        Assert.Equal(blocked.OutcomePlanHash, retry.OutcomePlanHash);
        Assert.Equal(blocked.StableSemanticRootId, retry.StableSemanticRootId);
        Assert.Equal(blocked.StableSemanticRootId, retry.RetryOfStepId);
    }

    [Fact]
    public void IntentionallyDisabledPreMortemRetry_ResolvesItsFailure()
    {
        var flowId = Guid.NewGuid();
        var failure = new FlowStep
        {
            FlowRunId = flowId,
            Iteration = 1,
            Sequence = 20,
            AgentId = WorkflowEngine.PreMortemRole,
            AgentName = "Pre-mortem Sceptic",
            AgentRole = WorkflowEngine.PreMortemRole,
            Status = StepStatus.Failed,
            Attempt = 1
        };
        var disabledRetry = new FlowStep
        {
            FlowRunId = flowId,
            Iteration = 1,
            Sequence = 30,
            AgentId = WorkflowEngine.PreMortemRole,
            AgentName = "Pre-mortem Sceptic",
            AgentRole = WorkflowEngine.PreMortemRole,
            Status = StepStatus.Skipped,
            Phase = AgentRunPhase.Succeeded,
            Attempt = 1,
            RetryOfStepId = failure.Id
        };

        Assert.Null(WorkflowEngine.FindUnresolvedFailure(
            [failure, disabledRetry]));
    }

    [Fact]
    public void TeamLeadCorrection_UsesItsCausalManualRetry()
    {
        var flowId = Guid.NewGuid();
        var correction = new FlowStep
        {
            FlowRunId = flowId,
            Iteration = 1,
            Sequence = 15,
            AgentId = "team-lead",
            AgentName = "Team Lead",
            AgentRole = "team-lead",
            Label = "Correct Team Lead task profiles",
            Status = StepStatus.Failed,
            Attempt = 2
        };
        var retry = new FlowStep
        {
            FlowRunId = flowId,
            Iteration = 1,
            Sequence = 25,
            AgentId = "team-lead",
            AgentName = "Team Lead",
            AgentRole = "team-lead",
            Label = "Manual restart of Team Lead",
            Status = StepStatus.Pending,
            Attempt = 3,
            RetryOfStepId = correction.Id
        };

        Assert.Same(
            retry,
            WorkflowEngine.SelectEffectiveManualRetryStep(
                correction,
                [correction, retry]));
    }

    [Fact]
    public void EffectiveTeamLeadStep_IgnoresSupersededSkippedRetry()
    {
        var flowId = Guid.NewGuid();
        var recovered = new FlowStep
        {
            FlowRunId = flowId,
            Iteration = 1,
            Sequence = 15,
            AgentId = "team-lead",
            AgentName = "Team Lead",
            AgentRole = "team-lead",
            Status = StepStatus.Completed,
            Attempt = 2,
            OutputSummary = "TEAM_TASK_PROFILES_V1_BEGIN"
        };
        var superseded = new FlowStep
        {
            FlowRunId = flowId,
            Iteration = 1,
            Sequence = 25,
            AgentId = "team-lead",
            AgentName = "Team Lead",
            AgentRole = "team-lead",
            Status = StepStatus.Skipped,
            Phase = AgentRunPhase.Failed,
            Attempt = 3,
            RetryOfStepId = recovered.Id
        };

        Assert.Same(
            recovered,
            WorkflowEngine.SelectEffectiveManualRetryStep(
                recovered,
                [recovered, superseded]));
    }

    [Fact]
    public async Task ManualPreMortemRestartPreservesItsCheckpointRound()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: false,
            persistSessionId: true);
        var originStepId = Guid.NewGuid();
        await using (var database = await fixture.DatabaseFactory.CreateDbContextAsync())
        {
            var flow = await database.Flows.Include(item => item.Steps).SingleAsync();
            var failedStep = Assert.Single(flow.Steps);
            flow.Status = FlowStatus.Failed;
            flow.FailureReason = "Pre-mortem output was invalid.";
            failedStep.AgentId = WorkflowEngine.PreMortemRole;
            failedStep.AgentName = "Pre-mortem Sceptic";
            failedStep.AgentRole = WorkflowEngine.PreMortemRole;
            failedStep.Label = "Pre-mortem review of Software Engineer (round 2)";
            failedStep.Status = StepStatus.Failed;
            failedStep.Phase = AgentRunPhase.Failed;
            failedStep.Attempt = 2;
            failedStep.PreMortemOriginStepId = originStepId;
            failedStep.CompletedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync();
        }

        var restarted = await fixture.Engine.RestartFailedFlowAsync(
            fixture.FlowId,
            CancellationToken.None);

        var retry = restarted.Steps.Single(step =>
            step.Label == "Manual restart of Pre-mortem Sceptic");
        Assert.Equal(2, retry.Attempt);
        Assert.Equal(originStepId, retry.PreMortemOriginStepId);
        Assert.NotNull(retry.RetryOfStepId);
        Assert.Null(WorkflowEngine.FindUnresolvedFailure(restarted.Steps));
    }

    [Fact]
    public async Task RepeatedRestart_ReusesTheSkippedCausalRetry()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: false,
            persistSessionId: true);
        await using (var database = await fixture.DatabaseFactory.CreateDbContextAsync())
        {
            var flow = await database.Flows.Include(item => item.Steps).SingleAsync();
            var failedStep = Assert.Single(flow.Steps);
            flow.Status = FlowStatus.Failed;
            flow.FailureReason = "Initial failure.";
            failedStep.Status = StepStatus.Failed;
            failedStep.Phase = AgentRunPhase.Failed;
            failedStep.CompletedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync();
        }

        var firstRestart = await fixture.Engine.RestartFailedFlowAsync(
            fixture.FlowId,
            CancellationToken.None);
        var retryId = firstRestart.Steps.Single(step =>
            step.Label == "Manual restart of Software Engineer").Id;
        await using (var database = await fixture.DatabaseFactory.CreateDbContextAsync())
        {
            var flow = await database.Flows.Include(item => item.Steps).SingleAsync();
            flow.Status = FlowStatus.Failed;
            flow.FailureReason = "Setup failed before the retry ran.";
            var retry = flow.Steps.Single(step => step.Id == retryId);
            retry.Status = StepStatus.Skipped;
            retry.Phase = AgentRunPhase.Failed;
            retry.CompletedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync();
        }

        var secondRestart = await fixture.Engine.RestartFailedFlowAsync(
            fixture.FlowId,
            CancellationToken.None);

        Assert.Single(
            secondRestart.Steps,
            step => step.Label == "Manual restart of Software Engineer");
        Assert.Equal(
            StepStatus.Pending,
            secondRestart.Steps.Single(step => step.Id == retryId).Status);
    }

    [Fact]
    public void SkippedRetry_DoesNotHideAnUnresolvedPushback()
    {
        var flowId = Guid.NewGuid();
        var pushback = new FlowStep
        {
            FlowRunId = flowId,
            Iteration = 1,
            Sequence = 10,
            AgentId = "quality-engineer",
            AgentName = "Quality Engineer",
            AgentRole = "quality-engineer",
            Status = StepStatus.Pushback,
            Attempt = 1
        };
        var skippedRetry = new FlowStep
        {
            FlowRunId = flowId,
            Iteration = 1,
            Sequence = 20,
            AgentId = "quality-engineer",
            AgentName = "Quality Engineer",
            AgentRole = "quality-engineer",
            Status = StepStatus.Skipped,
            Attempt = 2
        };

        Assert.Same(
            pushback,
            WorkflowEngine.FindUnresolvedPushback(
                [pushback, skippedRetry]));
    }

    [Fact]
    public void EffectiveRevision_RetargetsBlockedDependents()
    {
        var originalRevisionId = Guid.NewGuid();
        var completed = new FlowStep
        {
            FlowRunId = Guid.NewGuid(),
            Iteration = 1,
            AgentId = "software-engineer",
            AgentName = "Software Engineer",
            AgentRole = "software-engineer",
            Status = StepStatus.Completed,
            RetryOfStepId = originalRevisionId
        };
        var dependent = new FlowStep
        {
            FlowRunId = completed.FlowRunId,
            Iteration = 1,
            AgentId = "quality-engineer",
            AgentName = "Quality Engineer",
            AgentRole = "quality-engineer",
            Status = StepStatus.Pending,
            DependsOnStepId = originalRevisionId
        };

        Assert.Equal(
            1,
            WorkflowEngine.RetargetDependentSteps(completed, [dependent]));
        Assert.Equal(completed.Id, dependent.DependsOnStepId);
    }

    [Fact]
    public void RecoveredStep_ReversesLinksFromSupersededRetries()
    {
        var recovered = new FlowStep
        {
            FlowRunId = Guid.NewGuid(),
            Iteration = 1,
            AgentId = "software-engineer",
            AgentName = "Software Engineer",
            AgentRole = "software-engineer",
            Status = StepStatus.Completed
        };
        var superseded = new FlowStep
        {
            FlowRunId = recovered.FlowRunId,
            Iteration = 1,
            AgentId = "software-engineer",
            AgentName = "Software Engineer",
            AgentRole = "software-engineer",
            Status = StepStatus.Skipped,
            RetryOfStepId = recovered.Id
        };
        var dependent = new FlowStep
        {
            FlowRunId = recovered.FlowRunId,
            Iteration = 1,
            AgentId = "quality-engineer",
            AgentName = "Quality Engineer",
            AgentRole = "quality-engineer",
            Status = StepStatus.Skipped,
            DependsOnStepId = superseded.Id
        };
        var review = new FlowStep
        {
            FlowRunId = recovered.FlowRunId,
            Iteration = 1,
            AgentId = WorkflowEngine.PreMortemRole,
            AgentName = "Pre-mortem Sceptic",
            AgentRole = WorkflowEngine.PreMortemRole,
            Status = StepStatus.Skipped,
            PreMortemTargetStepId = superseded.Id
        };

        Assert.Equal(
            2,
            WorkflowEngine.RetargetSupersededRetryLinks(
                [recovered, superseded, dependent, review],
                recovered));
        Assert.Equal(recovered.Id, dependent.DependsOnStepId);
        Assert.Equal(recovered.Id, review.PreMortemTargetStepId);
    }

    [Fact]
    public void PreMortemRoundCount_IgnoresManualRetryDuplicates()
    {
        Assert.Equal(2, WorkflowEngine.CountPreMortemRounds([1, 1, 2]));
    }

    [Fact]
    public void InvalidTeamLeadCorrection_BecomesRestartable()
    {
        var correction = new FlowStep
        {
            FlowRunId = Guid.NewGuid(),
            Iteration = 1,
            AgentId = "team-lead",
            AgentName = "Team Lead",
            AgentRole = "team-lead",
            Status = StepStatus.Completed,
            Phase = AgentRunPhase.Succeeded
        };

        WorkflowEngine.ApplyContractValidationFailure(correction);

        Assert.Equal(StepStatus.Failed, correction.Status);
        Assert.Equal(AgentRunPhase.Failed, correction.Phase);
        Assert.NotNull(correction.CompletedAt);
    }

    [Fact]
    public void DeliveryStartsAfterTheEffectiveTeamLeadCorrection()
    {
        Assert.Equal(20, WorkflowEngine.FirstDeliverySequence(10));
        Assert.Equal(35, WorkflowEngine.FirstDeliverySequence(25));
        Assert.Equal(15, WorkflowEngine.FirstCorrectionSequence(10));
        Assert.Equal(25, WorkflowEngine.FirstCorrectionSequence(20));
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
        bool includeShutdown = true,
        bool includeToolCall = false)
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
            BuildJournal(
                sessionId,
                workspacePath,
                completed,
                includeShutdown,
                includeToolCall));

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
        bool includeShutdown,
        bool includeToolCall)
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
            if (includeToolCall)
            {
                events.Add(Serialize(
                    "tool.execution_start",
                    startedAt.AddMilliseconds(2500),
                    new
                    {
                        toolCallId = "qa-test",
                        toolName = "powershell",
                        arguments = new
                        {
                            command = "dotnet test"
                        }
                    }));
                events.Add(Serialize(
                    "tool.execution_complete",
                    startedAt.AddMilliseconds(2750),
                    new
                    {
                        toolCallId = "qa-test",
                        success = true,
                        result = new
                        {
                            exitCode = 0,
                            content = "All tests passed."
                        }
                    }));
            }
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

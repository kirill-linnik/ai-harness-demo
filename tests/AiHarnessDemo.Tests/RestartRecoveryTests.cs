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
    public void ProcessOwnership_RejectsPidReuseOutsideSessionStartWindow()
    {
        var sessionStartedAt = DateTimeOffset.UtcNow;

        Assert.True(CopilotSessionJournal.IsProcessStartOwnedBySession(
            sessionStartedAt.AddMinutes(-1),
            sessionStartedAt));
        Assert.True(CopilotSessionJournal.IsProcessStartOwnedBySession(
            sessionStartedAt.AddMinutes(1),
            sessionStartedAt));
        Assert.False(CopilotSessionJournal.IsProcessStartOwnedBySession(
            sessionStartedAt.AddMinutes(-3),
            sessionStartedAt));
        Assert.False(CopilotSessionJournal.IsProcessStartOwnedBySession(
            sessionStartedAt.AddMinutes(3),
            sessionStartedAt));

        var sessionId = Guid.NewGuid();
        Assert.True(CopilotSessionJournal.CommandLineBelongsToSession(
            $"copilot --session-id {sessionId:D}",
            sessionId));
        Assert.True(CopilotSessionJournal.CommandLineBelongsToSession(
            $"copilot --resume={sessionId:D}",
            sessionId));
        Assert.True(CopilotSessionJournal.CommandLineBelongsToSession(
            $"copilot --resume \"{sessionId:D}\"",
            sessionId));
        Assert.False(CopilotSessionJournal.CommandLineBelongsToSession(
            $"copilot --session-id {Guid.NewGuid():D}",
            sessionId));
        Assert.False(CopilotSessionJournal.CommandLineBelongsToSession(
            $"copilot -p \"inspect --resume={sessionId:D}\" --session-id {Guid.NewGuid():D}",
            sessionId));
        Assert.False(CopilotSessionJournal.CommandLineBelongsToSession(
            $"copilot -p \"inspect \\\" --resume={sessionId:D} \\\" now\" --session-id {Guid.NewGuid():D}",
            sessionId));
        Assert.False(CopilotSessionJournal.CommandLineBelongsToSession(
            $"copilot -p --resume={sessionId:D} --session-id {Guid.NewGuid():D}",
            sessionId));
        Assert.False(
            CopilotSessionJournal.CommandLineArgumentsBelongToSession(
                [
                    "copilot",
                    "--session-id",
                    Guid.NewGuid().ToString("D"),
                    "-p",
                    $"inspect --resume={sessionId:D}"
                ],
                sessionId));
        Assert.True(
            CopilotSessionJournal.CommandLineArgumentsBelongToSession(
                [
                    "copilot",
                    "--resume",
                    sessionId.ToString("D"),
                    "-p",
                    "inspect current session"
                ],
                sessionId));
        Assert.False(CopilotSessionJournal.CommandLineBelongsToSession(
            null,
            sessionId));
    }

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
        var binding =
            await CopilotSessionJournal.ReadWorkspaceBindingAsync(
                fixture.SessionDirectory,
                requireStartedAt: true,
                cancellationToken: CancellationToken.None);
        Assert.Equal(fixture.WorkspacePath, binding.WorkspacePath);
        Assert.Equal(resumedAt, binding.StartedAt);
    }

    [Fact]
    public async Task WorkspaceBinding_FullScanOverridesYamlCreationWithLatestResume()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: true,
            persistSessionId: true);
        var createdAt = DateTimeOffset.UtcNow.AddHours(-1);
        var resumedAt = DateTimeOffset.UtcNow;
        var resumedWorkspacePath =
            Path.Combine(fixture.Root, "resumed-workspace");
        await File.WriteAllTextAsync(
            Path.Combine(fixture.SessionDirectory, "workspace.yaml"),
            $"""
            cwd: '{fixture.WorkspacePath.Replace("'", "''")}'
            created_at: '{createdAt:O}'
            """);
        await File.AppendAllTextAsync(
            Path.Combine(fixture.SessionDirectory, "events.jsonl"),
            Environment.NewLine +
            RecoveryFixture.Serialize(
                "session.resume",
                resumedAt,
                new
                {
                    sessionId = fixture.SessionId,
                    context = new
                    {
                        cwd = resumedWorkspacePath
                    }
                }));

        var lightweight =
            await CopilotSessionJournal.ReadWorkspaceBindingAsync(
                fixture.SessionDirectory,
                requireStartedAt: false,
                cancellationToken: CancellationToken.None);
        var complete =
            await CopilotSessionJournal.ReadWorkspaceBindingAsync(
                fixture.SessionDirectory,
                requireStartedAt: true,
                cancellationToken: CancellationToken.None);

        Assert.Equal(createdAt, lightweight.StartedAt);
        Assert.Equal(fixture.WorkspacePath, lightweight.WorkspacePath);
        Assert.Equal(resumedAt, complete.StartedAt);
        Assert.Equal(resumedWorkspacePath, complete.WorkspacePath);
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

    [Fact]
    public async Task DeleteWorkspaceSessionsAsync_DoesNotReadUnrelatedLockedJournal()
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
            Path.Combine(unrelatedDirectory, "workspace.yaml"),
            $"id: {unrelatedId:D}{Environment.NewLine}" +
            $"cwd: {Path.Combine(fixture.Root, "unrelated")}{Environment.NewLine}");
        var unrelatedJournal =
            Path.Combine(unrelatedDirectory, "events.jsonl");
        await File.WriteAllTextAsync(unrelatedJournal, "locked");
        await using var journalLock = new FileStream(
            unrelatedJournal,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var deleted = await new CopilotSessionJournal()
            .DeleteWorkspaceSessionsAsync(
                [fixture.CopilotHome],
                fixture.WorkspacePath,
                [fixture.SessionId]);

        Assert.Equal(1, deleted);
        Assert.False(Directory.Exists(fixture.SessionDirectory));
        Assert.True(Directory.Exists(unrelatedDirectory));
    }

    [Fact]
    public async Task DeleteWorkspaceSessionsAsync_DoesNotDeleteKnownIdBoundElsewhere()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: true,
            persistSessionId: true);
        var journal = new CopilotSessionJournal();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            journal.DeleteWorkspaceSessionsAsync(
                [fixture.CopilotHome],
                Path.Combine(fixture.Root, "different-workspace"),
                [fixture.SessionId]));

        Assert.Contains("not bound to the flow workspace", exception.Message);
        Assert.True(Directory.Exists(fixture.SessionDirectory));
    }
}

public sealed class WorkflowRestartRecoveryTests
{
    [Fact]
    public void CompletedStudioOutcome_AllowsFinalizationRetry()
    {
        var flow = new FlowRun
        {
            Title = "Review-ready delivery",
            OriginalRequest = "Deliver the redesign.",
            ConsolidatedRequest = "Deliver the redesign.",
            RepositoryPath = @"C:\source",
            WorkspacePath = @"C:\workspace",
            BranchName = "ai-harness/review-ready",
            Kind = FlowKind.Delivery,
            Status = FlowStatus.Failed,
            OutcomeOwnerPlanStepKey = "verify-outcome",
            OutcomeContractJson =
                """{"Goal":"Deliver","Summary":"Ready","ImplementationDetails":["Verified"],"Artifacts":[]}"""
        };
        var outcomeOwner = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Sequence = 30,
            AgentId = "quality-engineer",
            AgentName = "Quality Engineer",
            AgentRole = "quality-engineer",
            Label = "Verify outcome",
            PlanStepKey = "verify-outcome",
            IsOutcomeOwner = true,
            Status = StepStatus.Failed,
            OutputSummary =
                "HANDOFF_STATUS: COMPLETE\nFLOW_OUTCOME_BEGIN\n{}\nFLOW_OUTCOME_END"
        };
        flow.Steps.Add(outcomeOwner);
        flow.Events.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = outcomeOwner.Id,
            Type = "outcome.contract-accepted",
            Message = "Accepted the required flow outcome."
        });

        Assert.True(WorkflowEngine.CanRetryStudioFinalization(flow));

        flow.OutcomeContractJson = string.Empty;
        Assert.False(WorkflowEngine.CanRetryStudioFinalization(flow));
    }

    [Fact]
    public void CandidateSealRecovery_IsScopedToCurrentOutcomeAttempt()
    {
        var flow = new FlowRun
        {
            Title = "Current seal recovery",
            OriginalRequest = "Recover only the current failed seal.",
            Iteration = 2
        };
        var priorOwner = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = 1,
            AgentId = "quality-engineer",
            AgentName = "Quality Engineer",
            AgentRole = "quality-engineer",
            IsOutcomeOwner = true
        };
        var currentOwner = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = 2,
            AgentId = "quality-engineer",
            AgentName = "Quality Engineer",
            AgentRole = "quality-engineer",
            IsOutcomeOwner = true
        };
        flow.Events.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = priorOwner.Id,
            Type = "delivery.review-candidate-seal-failed",
            Message = "Prior iteration seal failed."
        });

        Assert.False(
            WorkflowEngine.IsCurrentCandidateSealFailure(
                flow,
                currentOwner));

        var currentFailure = new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = currentOwner.Id,
            Type = "delivery.review-candidate-seal-failed",
            Message = "Current seal failed."
        };
        flow.Events.Add(currentFailure);
        Assert.True(
            WorkflowEngine.IsCurrentCandidateSealFailure(
                flow,
                currentOwner));
        currentFailure.DataJson =
            """{"Error":"Candidate repository has an ignored product or configuration path: custom-build/"}""";
        Assert.True(WorkflowEngine.IsIgnoredPathSealFailure(flow, currentOwner));
        Assert.False(WorkflowEngine.IsIgnoredPathSealFailure(flow, priorOwner));
        currentFailure.DataJson =
            """{"Error":"Host-owned scaffold file changed."}""";
        Assert.False(WorkflowEngine.IsIgnoredPathSealFailure(flow, currentOwner));

        flow.Events.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = currentOwner.Id,
            Type = ReviewedCandidateLedger.EventType,
            Message = "Current candidate sealed.",
            CreatedAt = currentFailure.CreatedAt.AddSeconds(1)
        });
        Assert.False(
            WorkflowEngine.IsCurrentCandidateSealFailure(
                flow,
                currentOwner));
    }

    [Fact]
    public async Task ManualRestartedCorrection_ResolvesOriginalPermissionSource()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: false,
            persistSessionId: true);
        await using var database =
            await fixture.DatabaseFactory.CreateDbContextAsync();
        var flow = await database.Flows
            .Include(item => item.Steps)
            .Include(item => item.Events)
            .SingleAsync();
        var original = Assert.Single(flow.Steps);
        original.PlanStepKey = "verify";
        var correction = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Sequence = original.Sequence + 10,
            AgentId = original.AgentId,
            AgentName = original.AgentName,
            AgentRole = original.AgentRole,
            Label = "Correct invalid response from Software Engineer",
            PlanStepKey = original.PlanStepKey,
            PlanDutiesJson = original.PlanDutiesJson
        };
        var manualRetry = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Sequence = correction.Sequence + 10,
            AgentId = original.AgentId,
            AgentName = original.AgentName,
            AgentRole = original.AgentRole,
            Label = "Manual restart of Software Engineer",
            PlanStepKey = original.PlanStepKey,
            PlanDutiesJson = original.PlanDutiesJson
        };
        flow.Steps.AddRange([correction, manualRetry]);
        flow.Events.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = correction.Id,
            Type = "agent.contract-correction-scheduled",
            Message = "Correction scheduled.",
            DataJson = JsonSerializer.Serialize(new
            {
                SourceStepId = original.Id,
                CorrectionStepId = correction.Id
            })
        });
        var legacyRestartedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        flow.Events.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = correction.Id,
            Type = "flow.manual-restart",
            Message = "Legacy correction restart requested.",
            CreatedAt = legacyRestartedAt
        });
        flow.Events.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = manualRetry.Id,
            Type = "step.manual-retry-scheduled",
            Message = "Manual retry scheduled.",
            DataJson = null,
            CreatedAt = legacyRestartedAt.AddSeconds(1)
        });
        await database.SaveChangesAsync();

        var source = await WorkflowEngine.ResolveTaskPermissionSourceAsync(
            database,
            manualRetry,
            CancellationToken.None);

        Assert.Equal(original.Id, source.Id);
        Assert.True(await WorkflowEngine.IsResponseCorrectionAttemptAsync(
            database,
            manualRetry,
            CancellationToken.None));

        var ordinaryRetry = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Sequence = manualRetry.Sequence + 10,
            AgentId = original.AgentId,
            AgentName = original.AgentName,
            AgentRole = original.AgentRole,
            Label = "Manual restart of Software Engineer",
            PlanStepKey = original.PlanStepKey,
            PlanDutiesJson = original.PlanDutiesJson
        };
        flow.Steps.Add(ordinaryRetry);
        flow.Events.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = ordinaryRetry.Id,
            Type = "step.manual-retry-scheduled",
            Message = "Ordinary retry scheduled.",
            DataJson = JsonSerializer.Serialize(new
            {
                SourceStepId = original.Id,
                RetryStepId = ordinaryRetry.Id
            })
        });
        await database.SaveChangesAsync();

        var ordinarySource =
            await WorkflowEngine.ResolveTaskPermissionSourceAsync(
                database,
                ordinaryRetry,
                CancellationToken.None);
        Assert.Equal(ordinaryRetry.Id, ordinarySource.Id);
        Assert.False(await WorkflowEngine.IsResponseCorrectionAttemptAsync(
            database,
            ordinaryRetry,
            CancellationToken.None));
    }

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
    public async Task CurrentFlowRecovery_CompletesJournalAttemptOnceWithoutRefreshingSnapshot()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: true,
            persistSessionId: false);
        var stagedRoot = AgentManifestStager.GetSessionRoot(
            fixture.CopilotHome,
            fixture.SessionId);
        Directory.CreateDirectory(stagedRoot);
        await File.WriteAllTextAsync(
            Path.Combine(stagedRoot, "sensitive-marker.txt"),
            "SENSITIVE_RECOVERED_PROMPT");

        var first = await fixture.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);
        var second = await fixture.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);

        await using var database = await fixture.DatabaseFactory.CreateDbContextAsync();
        var flow = await database.Flows
            .Include(item => item.Steps)
            .Include(item => item.AgentSnapshots)
            .Include(item => item.GateRecords)
            .Include(item => item.Events)
            .SingleAsync();
        var step = Assert.Single(flow.Steps);
        Assert.Contains(flow.Id, first);
        Assert.Contains(flow.Id, second);
        Assert.Equal(FlowStatus.Queued, flow.Status);
        Assert.Equal(StepStatus.Completed, step.Status);
        Assert.Single(flow.AgentSnapshots);
        Assert.Single(flow.GateRecords);
        Assert.Single(
            flow.Events,
            item => item.Type == "step.session-output-recovered");
        Assert.False(Directory.Exists(stagedRoot));
    }

    public static TheoryData<ExecutionInvocationKind, string>
        StudioInvocationJournalContracts => new()
        {
            {
                ExecutionInvocationKind.Intake,
                IntakeContract(
                    IntakeStatus.AwaitingConfirmation,
                    FlowKind.Delivery)
            },
            {
                ExecutionInvocationKind.BlockerExplanation,
                IntakeContract(
                    IntakeStatus.NeedsClarification,
                    flowKind: null,
                    emptyBrief: true)
            },
            {
                ExecutionInvocationKind.PreMortem,
                """
                PRE_MORTEM_STATUS: CLEAR
                PRE_MORTEM_FINDINGS_BEGIN
                {"Findings":[]}
                PRE_MORTEM_FINDINGS_END
                """
            },
            {
                ExecutionInvocationKind.Planning,
                PlanningContract()
            }
        };

    [Theory]
    [MemberData(nameof(StudioInvocationJournalContracts))]
    public async Task StudioRecovery_AppliesValidInvocationContractOnceWithoutAgentRerun(
        ExecutionInvocationKind invocationKind,
        string output)
    {
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: true,
            persistSessionId: false,
            invocationKind: invocationKind,
            completedOutput: output);

        await fixture.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);
        await fixture.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);

        await using var database =
            await fixture.DatabaseFactory.CreateDbContextAsync();
        var flow = await database.Flows
            .Include(item => item.Steps)
            .Include(item => item.GateRecords)
            .Include(item => item.Events)
            .SingleAsync();
        var step = Assert.Single(flow.Steps);
        Assert.Equal(invocationKind, step.InvocationKind);
        Assert.Equal(StepStatus.Completed, step.Status);
        Assert.Single(flow.GateRecords);
        Assert.Single(
            flow.Events,
            item => item.Type == "step.session-output-recovered");
    }

    [Fact]
    public async Task StudioRecovery_InvalidCompletedIntakeRemainsFailedAndManualRetryKeepsKind()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: true,
            persistSessionId: true,
            invocationKind: ExecutionInvocationKind.Intake,
            completedOutput:
                "HANDOFF_STATUS: COMPLETE without intake");

        await fixture.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);

        await using (var database =
                     await fixture.DatabaseFactory.CreateDbContextAsync())
        {
            var failed = await database.Flows
                .Include(item => item.Steps)
                .Include(item => item.GateRecords)
                .SingleAsync();
            Assert.Equal(FlowStatus.Failed, failed.Status);
            Assert.Equal(
                StepStatus.Failed,
                Assert.Single(failed.Steps).Status);
            Assert.Empty(failed.GateRecords);
        }

        var restarted = await fixture.Engine.RestartFailedFlowAsync(
            fixture.FlowId,
            CancellationToken.None);
        var retry = Assert.Single(
            restarted.Steps,
            item => item.Label.StartsWith(
                "Manual restart of",
                StringComparison.Ordinal));
        Assert.Equal(
            ExecutionInvocationKind.Intake,
            retry.InvocationKind);
        Assert.Null(retry.CopilotSessionId);
        Assert.Equal(FlowStatus.Queued, restarted.Status);
    }

    [Fact]
    public async Task StudioRecovery_OutcomeOwnerRequiresAndAppliesFlowOutcomeContract()
    {
        const string output = """
            HANDOFF_STATUS: COMPLETE
            FLOW_OUTCOME_BEGIN
            {"Goal":"Recover the outcome.","Summary":"The durable outcome is complete.","ImplementationDetails":["Apply the recovered result exactly once."],"Artifacts":[]}
            FLOW_OUTCOME_END
            """;
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: true,
            persistSessionId: false,
            invocationKind: ExecutionInvocationKind.Worker,
            completedOutput: output,
            isOutcomeOwner: true);

        await fixture.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);

        await using var database =
            await fixture.DatabaseFactory.CreateDbContextAsync();
        var flow = await database.Flows
            .Include(item => item.Steps)
            .SingleAsync();
        Assert.True(
            Assert.Single(flow.Steps).Status == StepStatus.Completed,
            flow.FailureReason +
            Environment.NewLine +
            string.Join(
                Environment.NewLine,
                await database.FlowEvents
                    .Where(item => item.FlowRunId == flow.Id)
                    .Select(item => item.Message)
                    .ToListAsync()));
        Assert.Contains(
            "Recover the outcome.",
            flow.OutcomeContractJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentFlowRecovery_QueuesInterruptedAttemptForSameSessionResume()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: false,
            persistSessionId: true);
        var stagedRoot = AgentManifestStager.GetSessionRoot(
            fixture.CopilotHome,
            fixture.SessionId);
        Directory.CreateDirectory(stagedRoot);
        await File.WriteAllTextAsync(
            Path.Combine(stagedRoot, "sensitive-marker.txt"),
            "SENSITIVE_RESUMABLE_PROMPT");

        var recovered = await fixture.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);

        await using var database = await fixture.DatabaseFactory.CreateDbContextAsync();
        var flow = await database.Flows
            .Include(item => item.Steps)
            .Include(item => item.AgentSnapshots)
            .Include(item => item.Events)
            .SingleAsync();
        var step = Assert.Single(flow.Steps);
        Assert.Contains(flow.Id, recovered);
        Assert.Equal(FlowStatus.Queued, flow.Status);
        Assert.Equal(StepStatus.Pending, step.Status);
        Assert.Equal(AgentRunPhase.CanceledByReconciliation, step.Phase);
        Assert.Equal(fixture.SessionId, step.CopilotSessionId);
        Assert.Single(flow.AgentSnapshots);
        Assert.Contains(
            flow.Events,
            item => item.Type == "step.resume-queued");
        Assert.True(Directory.Exists(stagedRoot));
    }

    [Fact]
    public async Task ManualRecovery_PreservesTheInterruptedAttemptContract()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: false,
            persistSessionId: true);
        Guid stepId;
        const string effectivePermission =
            """{"Profile":"ReadOnlySource","AllowedTools":["read"]}""";
        await using (var database =
                     await fixture.DatabaseFactory.CreateDbContextAsync())
        {
            var configuredStep = await database.FlowSteps.SingleAsync();
            stepId = configuredStep.Id;
            configuredStep.PermissionProfile =
                ExecutionPermissionProfile.ReadOnlySource;
            configuredStep.EffectivePermissionJson = effectivePermission;
            await database.SaveChangesAsync();
        }

        await fixture.Engine.RecoverActiveFlowAsync(
            fixture.FlowId,
            CancellationToken.None);

        await using var verification =
            await fixture.DatabaseFactory.CreateDbContextAsync();
        var flow = await verification.Flows
            .Include(item => item.Steps)
            .Include(item => item.Events)
            .SingleAsync();
        var recoveredStep = Assert.Single(flow.Steps);
        Assert.Equal(FlowStatus.Queued, flow.Status);
        Assert.Equal(stepId, recoveredStep.Id);
        Assert.Equal(StepStatus.Pending, recoveredStep.Status);
        Assert.Equal(
            AgentRunPhase.CanceledByReconciliation,
            recoveredStep.Phase);
        Assert.Equal(fixture.SessionId, recoveredStep.CopilotSessionId);
        Assert.Equal(
            "Original durable execution prompt.",
            recoveredStep.ExecutionPrompt);
        Assert.Equal(new string('A', 64), recoveredStep.WorkflowRevision);
        Assert.Equal(
            ExecutionPermissionProfile.ReadOnlySource,
            recoveredStep.PermissionProfile);
        Assert.Equal(
            effectivePermission,
            recoveredStep.EffectivePermissionJson);
        Assert.Contains(
            flow.Events,
            item => item.Type == "flow.manual-recovery-queued");
    }

    [Fact]
    public async Task FlowWorker_ManualRecoveryCancelsAndRequeuesOneExecution()
    {
        var queue = new FlowQueue();
        var flowId = Guid.NewGuid();
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCanceled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runCount = 0;
        var recoveryCount = 0;

        async Task RunAsync(Guid _, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref runCount) == 1)
            {
                firstStarted.TrySetResult();
                try
                {
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    firstCanceled.TrySetResult();
                    throw;
                }
            }
            else
            {
                resumed.TrySetResult();
            }
        }

        Task RecoverAsync(Guid _, CancellationToken __)
        {
            Assert.True(firstCanceled.Task.IsCompleted);
            Interlocked.Increment(ref recoveryCount);
            return Task.CompletedTask;
        }

        using var worker = new FlowWorker(
            queue,
            RunAsync,
            _ => Task.FromResult<IReadOnlyList<Guid>>([]),
            (_, _) => Task.FromResult(true),
            NullLogger<FlowWorker>.Instance,
            recoverFlowAsync: RecoverAsync);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(queue.Queue(flowId));
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await worker.RecoverAsync(flowId, CancellationToken.None);
            await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, Volatile.Read(ref runCount));
            Assert.Equal(1, Volatile.Read(ref recoveryCount));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task CurrentFlowRecovery_MissingPersistedPromptFailsClosedBeforeRerun()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(
            completed: false,
            persistSessionId: true);
        await using (var database =
                     await fixture.DatabaseFactory.CreateDbContextAsync())
        {
            var step = await database.FlowSteps.SingleAsync();
            step.ExecutionPrompt = string.Empty;
            await database.SaveChangesAsync();
        }

        var recovered = await fixture.Engine.RecoverInterruptedFlowsAsync(
            CancellationToken.None);

        Assert.DoesNotContain(fixture.FlowId, recovered);
        await using var verification =
            await fixture.DatabaseFactory.CreateDbContextAsync();
        var flow = await verification.Flows
            .Include(item => item.Steps)
            .Include(item => item.Events)
            .SingleAsync();
        Assert.Equal(FlowStatus.Failed, flow.Status);
        Assert.Equal(
            StepStatus.Failed,
            Assert.Single(flow.Steps).Status);
        Assert.Contains(
            flow.Events,
            item =>
                item.Type == "step.recovery-failed" &&
                item.Message.Contains(
                    "no persisted exact execution prompt",
                    StringComparison.Ordinal));
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
            var flow = await database.Flows
                .Include(item => item.Steps)
                .Include(item => item.AgentSnapshots)
                .SingleAsync();
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
            OutputSummary = "TEAM_TASK_PROFILES_BEGIN"
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
            flow.AgentSnapshots.Add(new FlowAgentSnapshot
            {
                FlowRunId = flow.Id,
                AgentId = WorkflowEngine.PreMortemRole,
                Name = "Pre-mortem Sceptic",
                Description = "Reviews failure modes.",
                Role = WorkflowEngine.PreMortemRole,
                Instructions = "Review the current checkpoint.",
                DefinitionHash = "sha256:pre-mortem-recovery",
                EnabledAtSnapshot = true,
                SourceFileName = "pre-mortem-sceptic.agent.md"
            });
            var profile = await database.TaskProfiles.SingleAsync(
                item => item.FlowStepId == failedStep.Id);
            profile.AgentId = WorkflowEngine.PreMortemRole;
            profile.Role = WorkflowEngine.PreMortemRole;
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
            var flow = await database.Flows
                .Include(item => item.Steps)
                .Include(item => item.Events)
                .SingleAsync();
            flow.Status = FlowStatus.Failed;
            flow.FailureReason = "Setup failed before the retry ran.";
            var retry = flow.Steps.Single(step => step.Id == retryId);
            retry.Status = StepStatus.Skipped;
            retry.Phase = AgentRunPhase.Failed;
            retry.CompletedAt = DateTimeOffset.UtcNow;
            var binding = flow.Events.Single(item =>
                item.FlowStepId == retryId &&
                item.Type == "step.manual-retry-scheduled");
            binding.DataJson = null;
            flow.Events.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = retryId,
                Type = "step.manual-retry-scheduled",
                Message = "Legacy duplicate retry binding."
            });
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
        await using var verify =
            await fixture.DatabaseFactory.CreateDbContextAsync();
        var canonicalBindings = await verify.FlowEvents
            .Where(item =>
                item.FlowStepId == retryId &&
                item.Type == "step.manual-retry-scheduled")
            .ToListAsync();
        Assert.Equal(2, canonicalBindings.Count);
        Assert.All(
            canonicalBindings,
            item => Assert.False(string.IsNullOrWhiteSpace(item.DataJson)));
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
            failedStep.PermissionProfile =
                ExecutionPermissionProfile.ReadOnlySource;
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
        Assert.Contains("Host recovery context:", retry.InputSummary);
        Assert.Contains("persisted permission ceiling", retry.InputSummary);
        Assert.DoesNotContain("before editing", retry.InputSummary);
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

    private static string IntakeContract(
        IntakeStatus status,
        FlowKind? flowKind,
        bool emptyBrief = false) =>
        IntakeParser.BeginSentinel +
        Environment.NewLine +
        IntakeParser.Serialize(new IntakeDocument
        {
            Status = status,
            FlowKind = flowKind,
            TaskTitle = "Recover intake",
            CustomerReply = "The recovered intake result is valid.",
            Brief = new IntakeBrief
            {
                Goal = emptyBrief ? string.Empty : "Recover the intake.",
                Details = emptyBrief ? [] : ["Keep the durable request."],
                SuccessCriteria = emptyBrief ? [] : ["The brief is preserved."],
                Constraints = [],
                Assumptions = []
            }
        }) +
        Environment.NewLine +
        IntakeParser.EndSentinel;

    private static string PlanningContract()
    {
        var document = new TeamPlanDocument
        {
            Disposition = TeamPlanDisposition.MissingQualification,
            Steps = [],
            PreMortemCheckpoints = [],
            MissingQualification = new MissingQualification
            {
                Summary = "A specialist is required.",
                Missing = ["Specialist analysis"],
                WhyRequired = "The work cannot be planned safely.",
                SuggestedAgent = new SuggestedAgent
                {
                    Id = "specialist",
                    Name = "Specialist",
                    Description = "Performs the required analysis."
                }
            }
        };
        return "HANDOFF_STATUS: COMPLETE" +
               Environment.NewLine +
               TeamPlanParser.BeginSentinel +
               Environment.NewLine +
               TeamPlanParser.Serialize(document) +
               Environment.NewLine +
               TeamPlanParser.EndSentinel;
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
        bool includeToolCall = false,
        ExecutionInvocationKind invocationKind =
            ExecutionInvocationKind.Worker,
        string? completedOutput = null,
        bool isOutcomeOwner = false)
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
        await File.WriteAllTextAsync(
            Path.Combine(root, "WORKFLOW.md"),
            """
            ---
            workspace:
              root: workspace
            agent:
              max_concurrent_agents: 1
              max_attempts: 1
            studio:
              planning:
                max_steps: 24
                max_dependencies_per_step: 8
                max_assignment_characters: 4000
              flow_kinds:
                advisory:
                  required_duties: [PrepareOutcome]
                  maximum_permission: ReadOnlySource
                delivery:
                  required_duties: [Implement, Verify, PrepareOutcome, Publish]
                  pre_review_maximum_permission: WorkspaceWrite
                  post_approval_maximum_permission: Publish
              advisory:
                artifact_directory: .studio\advisory
                max_artifact_count: 8
                max_total_artifact_bytes: 65536
            ---

            {{ task }}
            """);
        var flowId = Guid.NewGuid();
        var (agentId, agentName, agentRole, planStepKey) =
            InvocationIdentity(invocationKind);
        var sessionId = AgentSessionIdentity.Create(
            flowId,
            1,
            agentId,
            planStepKey);
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
                includeToolCall,
                agentName,
                completedOutput));

        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;
        var databaseFactory = new TestDbContextFactory(options);
        var flow = new FlowRun
        {
            Id = flowId,
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
            AgentId = agentId,
            AgentName = agentName,
            AgentRole = agentRole,
            PlanStepKey = planStepKey,
            PlanDutiesJson = invocationKind == ExecutionInvocationKind.Planning
                ? """["Analyze","Design"]"""
                : """["Analyze"]""",
            InvocationKind = invocationKind,
            IsOutcomeOwner = isOutcomeOwner,
            Label = "Execute Software Engineer contract",
            Status = StepStatus.Running,
            Phase = AgentRunPhase.StreamingTurn,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CopilotSessionId = persistSessionId ? sessionId : null,
            CopilotSessionHome = copilotHome,
            ExecutionPrompt = "Original durable execution prompt.",
            WorkflowRevision = new string('A', 64)
        });
        flow.AgentSnapshots.Add(new FlowAgentSnapshot
        {
            FlowRunId = flow.Id,
            AgentId = agentId,
            Name = agentName,
            Description = "Performs the recovered work.",
            Role = agentRole,
            Instructions = "Complete the assignment.",
            DefinitionHash = "sha256:recovery",
            EnabledAtSnapshot = true,
            SourceFileName = $"{agentId}.agent.md"
        });
        await using (var database = await databaseFactory.CreateDbContextAsync())
        {
            await database.Database.EnsureCreatedAsync();
            database.Flows.Add(flow);
            database.TaskProfiles.Add(new TaskProfile
            {
                FlowRunId = flow.Id,
                FlowStepId = flow.Steps.Single().Id,
                Iteration = flow.Iteration,
                PlanStepKey = planStepKey,
                AgentId = agentId,
                Role = agentRole,
                Complexity = 4,
                ReasoningDepth = 4,
                ContextDemand = 4,
                ToolIntensity = 2,
                TaskTypeTagsJson = """["CrossCutting"]""",
                Risk = TaskRisk.Low,
                RiskReason = "Recovery fixture.",
                Confidence = 1,
                RationalesJson = """["Recovery fixture."]"""
            });
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
        bool includeToolCall,
        string agentName,
        string? completedOutput)
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
                    agentName,
                    agentDisplayName = agentName
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
                    content = completedOutput ??
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

    private static (
        string AgentId,
        string AgentName,
        string AgentRole,
        string PlanStepKey) InvocationIdentity(
        ExecutionInvocationKind invocationKind) =>
        invocationKind switch
        {
            ExecutionInvocationKind.Intake =>
                ("account-manager", "Account Manager", "account-manager",
                    "account-manager:intake"),
            ExecutionInvocationKind.BlockerExplanation =>
                ("account-manager", "Account Manager", "account-manager",
                    MissingQualificationCoordinator.AccountManagerPlanStepKey),
            ExecutionInvocationKind.PreMortem =>
                (WorkflowEngine.PreMortemRole, "Pre-mortem Sceptic",
                    WorkflowEngine.PreMortemRole, "pre-mortem:recover"),
            ExecutionInvocationKind.Planning =>
                ("team-lead", "Team Lead", "team-lead",
                    WorkflowEngine.TeamLeadPlanStepKey),
            ExecutionInvocationKind.Publication =>
                ("publisher", "Publisher", "publisher", "publish"),
            _ =>
                ("software-engineer", "Software Engineer",
                    "software-engineer", "implement")
        };

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

using System.Collections.Immutable;
using System.Text.Json;
using AiHarnessDemo.Contracts;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Security;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Data;
using AiHarnessDemo.Infrastructure;
using AiHarnessDemo.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiHarnessDemo.Tests;

public sealed class IntakeAdvisoryTests
{
    [Fact]
    public async Task OptionalAgentWithAccountManagerRoleDoesNotShadowCanonicalCoreIdentity()
    {
        await using var harness = await StudioFlowHarness.CreateAsync(
            FlowKind.Advisory);
        await using (var database = await harness.Factory.CreateDbContextAsync())
        {
            database.FlowAgentSnapshots.Add(new FlowAgentSnapshot
            {
                FlowRunId = harness.FlowId,
                AgentId = "customer-liaison",
                Name = "Customer Liaison",
                Description = "Optional customer research specialist.",
                Role = "account-manager",
                Instructions = "Support the assigned work.",
                DefinitionHash = "optional-role-collision",
                EnabledAtSnapshot = true,
                Required = false,
                Switchable = true,
                SourceFileName = "customer-liaison.agent.md"
            });
            await database.SaveChangesAsync();
        }

        var response = await harness.Intake.ContinueAsync(
            new IntakeRequest(
                harness.FlowId,
                "Assess checkout resilience."));

        Assert.False(response.ReadyToStart);
        var flow = await harness.LoadFlowAsync();
        var intake = Assert.Single(
            flow.Steps,
            step =>
                step.InvocationKind == ExecutionInvocationKind.Intake);
        Assert.Equal("account-manager", intake.AgentId);
    }

    [Fact]
    public void IntakeV2_IsStrictCaseSensitiveAndBounded()
    {
        var parsed = IntakeV2Parser.Parse(Intake(
            "AwaitingConfirmation",
            "Advisory"));

        Assert.Equal(IntakeV2Status.AwaitingConfirmation, parsed.Document.Status);
        Assert.Equal(FlowKind.Advisory, parsed.Document.FlowKind);
        Assert.Equal("Assess checkout resilience", parsed.Document.TaskTitle);

        Assert.Throws<IntakeV2ContractException>(() =>
            IntakeV2Parser.Parse(
                Intake("AwaitingConfirmation", "Advisory")
                    .Replace(
                        "\"Version\":",
                        "\"Unknown\":true,\"Version\":",
                        StringComparison.Ordinal)));
        Assert.Throws<IntakeV2ContractException>(() =>
            IntakeV2Parser.Parse(
                Intake("AwaitingConfirmation", "Advisory")
                    .Replace(
                        "\"TaskTitle\":",
                        "\"TaskTitle\":\"duplicate\",\"TaskTitle\":",
                        StringComparison.Ordinal)));
        Assert.Throws<IntakeV2ContractException>(() =>
            IntakeV2Parser.Parse(Intake("awaitingConfirmation", "Advisory")));
        Assert.Throws<IntakeV2ContractException>(() =>
            IntakeV2Parser.Parse(Intake("Confirmed", "null")));
        Assert.Throws<IntakeV2ContractException>(() =>
            IntakeV2Parser.Parse(
                Intake("AwaitingConfirmation", "Advisory")
                    .Replace(
                        "Assess checkout resilience",
                        new string('x', IntakeV2Parser.MaximumTaskTitleCharacters + 1),
                        StringComparison.Ordinal)));
    }

    [Fact]
    public async Task MaximumPromotionSeed_IsCanonicalStagedOnceAndNotInlined()
    {
        var goal = new string('\uE000', 4_000);
        var details = Enumerable.Range(0, 24)
            .Select(index => new string(
                (char)('\uE001' + index),
                4_000))
            .ToArray();
        var canonical = AdvisoryPromotionSeedParser.Serialize(
            goal,
            details);
        var seed = AdvisoryPromotionSeedParser.Parse(canonical);
        var hash = AdvisoryPromotionSeedParser.ComputeHash(canonical);

        Assert.Equal(
            AdvisoryPromotionSeedParser.MaximumDocumentCharacters,
            canonical.Length);
        Assert.Equal(goal, seed.Goal);
        Assert.Equal(details, seed.ImplementationDetails);

        var task = IntakeCoordinator.BuildDialogueTask(
            [
                new FlowMessage
                {
                    Role = ConversationRole.Customer,
                    Content = canonical
                }
            ],
            OutcomeType.PullRequest,
            contractVersion: "studio-v2",
            promotionSeed: seed);
        Assert.Equal(
            1,
            task.Split(
                    "DURABLE_ADVISORY_PROMOTION_AUTHORIZATION",
                    StringSplitOptions.None)
                .Length - 1);
        Assert.DoesNotContain(canonical, task, StringComparison.Ordinal);
        Assert.DoesNotContain(goal, task, StringComparison.Ordinal);
        Assert.DoesNotContain(details[12], task, StringComparison.Ordinal);

        var root = Path.Combine(
            Path.GetTempPath(),
            $"promotion-context-{Guid.NewGuid():N}");
        try
        {
            var stager = new AgentManifestStager();
            var accountManagerSessionId = Guid.NewGuid();
            var flowId = Guid.NewGuid();
            var accountManagerStepId = Guid.NewGuid();
            var stagedAgent = await stager.StageAsync(
                root,
                new AgentManifest(
                    "account-manager",
                    "Account Manager",
                    "Normalizes customer intent.",
                    "account-manager",
                    "violet",
                    10,
                    "ignored.agent.md",
                    "Return the strict intake contract."),
                accountManagerSessionId);
            var stagedSeed = await stager.StagePromotionSeedAsync(
                stagedAgent,
                new AdvisoryPromotionContext(canonical, hash));
            Assert.Equal(
                canonical,
                await File.ReadAllTextAsync(stagedSeed.Path));

            var context = new AgentExecutionContext(
                flowId,
                1,
                "account-manager",
                "Account Manager",
                "account-manager",
                "model",
                "high",
                1,
                task,
                "Repository facts.",
                root,
                root,
                accountManagerSessionId,
                OutcomeType.PullRequest,
                "Normalize the promotion.",
                [],
                [],
                ContractVersion: "studio-v2",
                InvocationKind: ExecutionInvocationKind.Intake,
                FlowStepId: accountManagerStepId,
                PromotionContext:
                    new AdvisoryPromotionContext(canonical, hash));
            var values = CopilotReasoningHost.BuildPromptValues(
                context,
                "Return the strict intake contract.",
                root,
                stagedSeed);
            var roleContext = values["role.context"];

            Assert.Contains(stagedSeed.Path, roleContext);
            Assert.Contains(hash, roleContext);
            Assert.DoesNotContain(canonical, roleContext);
            Assert.DoesNotContain(goal, roleContext);
            Assert.True(values["task"].Length <= 6_000);

            var accountManagerPrompt =
                CopilotReasoningHost.BoundRenderedPrompt(
                    context,
                    new WorkflowPromptRenderer().Render(
                        "{{ task }}\n\n{{ role.context }}\n\n{{ agent.instructions }}",
                        values));
            var stagedAccountManagerPrompt =
                await stager.StagePromptAsync(
                    stagedAgent,
                    accountManagerPrompt,
                    accountManagerSessionId,
                    flowId,
                    accountManagerStepId,
                    attempt: 1);
            var stagedAccountManagerText =
                await File.ReadAllTextAsync(
                    stagedAccountManagerPrompt.Path);
            Assert.Contains(
                stagedSeed.Path,
                stagedAccountManagerText,
                StringComparison.Ordinal);
            Assert.Contains(
                hash,
                stagedAccountManagerText,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                details[12],
                stagedAccountManagerText,
                StringComparison.Ordinal);
            var stagedSeedDocument = AdvisoryPromotionSeedParser.Parse(
                await File.ReadAllTextAsync(stagedSeed.Path));
            Assert.Equal(
                details[12],
                stagedSeedDocument.ImplementationDetails![12]);
            Assert.Equal(
                details[^1],
                stagedSeedDocument.ImplementationDetails[^1]);

            var teamLeadFlow = new FlowRun
            {
                Title = "Implement accepted recommendation",
                OriginalRequest = "Implement accepted recommendation",
                Kind = FlowKind.Delivery,
                ContractVersion = "studio-v2",
                ConsolidatedRequest =
                    IntakeV2Parser.SerializeBrief(
                        new IntakeV2Brief
                        {
                            Goal = goal,
                            Details = details,
                            SuccessCriteria = [],
                            Constraints = [],
                            Assumptions = []
                        })
            };
            var snapshots = new[]
            {
                Snapshot(teamLeadFlow.Id, "team-lead"),
                Snapshot(teamLeadFlow.Id, "delivery-generalist")
            };
            var assignment = WorkflowEngine.BuildStudioTeamLeadAssignment(
                teamLeadFlow,
                snapshots,
                TeamPlanValidationContext.ForPersistedPlan(
                    FlowKind.Delivery,
                    snapshots,
                    preMortemEnabled: false),
                preMortemAvailable: false,
                maximumPreMortemRounds: 0);
            var teamLeadSessionId = Guid.NewGuid();
            var teamLeadStepId = Guid.NewGuid();
            var teamLeadContext = new AgentExecutionContext(
                teamLeadFlow.Id,
                1,
                "team-lead",
                "Team Lead",
                "team-lead",
                "model",
                "high",
                1,
                assignment,
                "Repository facts.",
                root,
                root,
                teamLeadSessionId,
                OutcomeType.PullRequest,
                "Create the dynamic plan.",
                [],
                [],
                FlowStepId: teamLeadStepId,
                ContractVersion: "studio-v2",
                InvocationKind: ExecutionInvocationKind.Planning);
            var teamLeadValues =
                CopilotReasoningHost.BuildPromptValues(
                    teamLeadContext,
                    "Return exactly one team-plan-v1 document.",
                    root);
            var teamLeadPrompt =
                CopilotReasoningHost.BoundRenderedPrompt(
                    teamLeadContext,
                    new WorkflowPromptRenderer().Render(
                        "{{ task }}\n\n{{ agent.instructions }}",
                        teamLeadValues));
            var stagedTeamLead = await stager.StageAsync(
                root,
                new AgentManifest(
                    "team-lead",
                    "Team Lead",
                    "Plans the smallest capable team.",
                    "team-lead",
                    "violet",
                    20,
                    "team-lead.agent.md",
                    "Return exactly one team-plan-v1 document."),
                teamLeadSessionId);
            var stagedTeamLeadPrompt = await stager.StagePromptAsync(
                stagedTeamLead,
                teamLeadPrompt,
                teamLeadSessionId,
                teamLeadFlow.Id,
                teamLeadStepId,
                attempt: 1);
            var stagedTeamLeadText =
                await File.ReadAllTextAsync(
                    stagedTeamLeadPrompt.Path);

            Assert.Equal(assignment, teamLeadValues["task"]);
            Assert.Contains(
                teamLeadFlow.ConsolidatedRequest,
                stagedTeamLeadText,
                StringComparison.Ordinal);
            var recoveredBrief =
                JsonSerializer.Deserialize<IntakeV2Brief>(
                    teamLeadFlow.ConsolidatedRequest);
            Assert.Equal(goal, recoveredBrief!.Goal);
            Assert.Equal(
                details[12],
                recoveredBrief.Details![12]);
            Assert.Equal(details[^1], recoveredBrief.Details[^1]);
            Assert.True(
                stagedTeamLeadText.Length >
                CopilotReasoningHost.MaximumInlinePromptCharacters);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static FlowAgentSnapshot Snapshot(Guid flowId, string id) =>
        new()
        {
            FlowRunId = flowId,
            AgentId = id,
            Name = id,
            Description = $"Description for {id}.",
            Role = id,
            Instructions = "Complete the assigned work.",
            DefinitionHash = $"hash-{id}",
            EnabledAtSnapshot = true,
            SourceFileName = $"{id}.agent.md"
        };

    [Fact]
    public void Confirmation_ReusesReviewedKindTitleAndNormalizedBrief()
    {
        var pending = IntakeV2Parser.Parse(Intake(
            "AwaitingConfirmation",
            "Advisory")).Document;
        var attemptedChange = IntakeCoordinator.ParseResponse(
            Intake(
                    "Confirmed",
                    "Delivery")
                .Replace(
                    "Identify the highest-impact checkout resilience gaps.",
                    "Implement a different request.",
                    StringComparison.Ordinal),
            "studio-v2");

        var confirmed = IntakeCoordinator.ApplyConfirmationGate(
            attemptedChange,
            pending);

        Assert.True(confirmed.Ready);
        Assert.Equal(FlowKind.Advisory, confirmed.FlowKind);
        Assert.Equal(pending.TaskTitle, confirmed.TaskTitle);
        Assert.Equal(
            IntakeV2Parser.SerializeBrief(pending.Brief!),
            confirmed.TaskBrief);
    }

    [Fact]
    public void Correction_IsNotConfirmationAndRequiresAnotherReview()
    {
        var corrected = IntakeCoordinator.ParseResponse(
            Intake("AwaitingConfirmation", "Delivery")
                .Replace(
                    "Assess checkout resilience",
                    "Improve checkout resilience",
                    StringComparison.Ordinal),
            "studio-v2");
        var pending = IntakeV2Parser.Parse(
            Intake("AwaitingConfirmation", "Advisory")).Document;

        var gated = IntakeCoordinator.ApplyConfirmationGate(corrected, pending);
        var task = IntakeCoordinator.BuildDialogueTask(
            [
                new FlowMessage
                {
                    Role = ConversationRole.Customer,
                    Content = "Actually, implement the recommendation."
                }
            ],
            OutcomeType.PullRequest,
            IntakeV2Parser.SerializeBrief(pending.Brief!),
            pending.FlowKind,
            "studio-v2");

        Assert.False(gated.Ready);
        Assert.Equal(FlowKind.Delivery, gated.FlowKind);
        Assert.Contains("A correction is not confirmation", task);
        Assert.Contains("never silently change", task);
    }

    [Fact]
    public async Task MalformedOrdinaryIntake_FailsDurablyAndRetriesWithoutDuplicateCustomerState()
    {
        await using var harness = await StudioFlowHarness.CreateAsync(
            FlowKind.Advisory,
            malformedInitialIntakeRuns: 1);
        const string customerRequest = "Assess checkout resilience.";

        await Assert.ThrowsAsync<IntakeV2ContractException>(() =>
            harness.Intake.ContinueAsync(
                new IntakeRequest(
                    harness.FlowId,
                    customerRequest)));

        var failed = await harness.LoadFlowAsync();
        var failedStep = Assert.Single(failed.Steps);
        Assert.Equal(FlowStatus.Intake, failed.Status);
        Assert.Equal(StepStatus.Failed, failedStep.Status);
        Assert.Equal(AgentRunPhase.Failed, failedStep.Phase);
        Assert.Contains(
            "MALFORMED_INTAKE_OUTPUT",
            failedStep.OutputSummary,
            StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(failedStep.PushbackReason));
        Assert.Single(
            failed.Events,
            item => item.Type == "intake.attempt-failed");
        Assert.Empty(failed.GateRecords);
        Assert.Single(
            failed.Messages,
            item => item.Role == ConversationRole.Customer);
        Assert.DoesNotContain(
            failed.Messages,
            item => item.Role == ConversationRole.AccountManager);
        var corrected = await harness.Intake.ContinueAsync(
            new IntakeRequest(
                harness.FlowId,
                customerRequest));
        Assert.False(corrected.ReadyToStart);
        var awaiting = await harness.LoadFlowAsync();
        var retry = Assert.Single(
            awaiting.Steps,
            item => item.Status == StepStatus.Completed);
        Assert.Equal(2, retry.Attempt);
        Assert.Equal(
            ExecutionInvocationKind.Intake,
            retry.InvocationKind);
        Assert.NotEqual(
            failedStep.CopilotSessionId,
            retry.CopilotSessionId);
        Assert.Equal(failed.WorkspacePath, awaiting.WorkspacePath);
        Assert.Single(
            awaiting.Messages,
            item => item.Role == ConversationRole.Customer);
        Assert.Single(
            awaiting.Messages,
            item => item.Role == ConversationRole.AccountManager);
        Assert.Single(awaiting.GateRecords);
        Assert.Single(
            awaiting.Events,
            item => item.Type == "intake.retry-started");

        var confirmed = await harness.Intake.ContinueAsync(
            new IntakeRequest(
                harness.FlowId,
                "Yes, that is the exact Advisory assessment I want."));
        Assert.True(confirmed.ReadyToStart);
        var queued = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Queued, queued.Status);
        Assert.Single(
            queued.Events,
            item => item.Type == "flow.queued");
    }

    [Fact]
    public async Task OrdinaryIntakeRecovery_ExecutesPendingStepOnceWithoutDuplicateState()
    {
        await using var harness =
            await StudioFlowHarness.CreateAsync(FlowKind.Advisory);
        _ = await SeedOrdinaryIntakeRecoveryAsync(
            harness,
            StepStatus.Pending);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Intake.ContinueAsync(
                new IntakeRequest(
                    harness.FlowId,
                    "Duplicate submission.")));

        var first = await harness.Intake
            .RecoverOrdinaryIntakesAsync(CancellationToken.None);
        var second = await harness.Intake
            .RecoverOrdinaryIntakesAsync(CancellationToken.None);

        Assert.Contains(harness.FlowId, first);
        Assert.Empty(second);
        var flow = await harness.LoadFlowAsync();
        Assert.Single(flow.Steps);
        Assert.Single(
            flow.Messages,
            item => item.Role == ConversationRole.Customer);
        Assert.Single(
            flow.Messages,
            item => item.Role == ConversationRole.AccountManager);
        Assert.Single(flow.GateRecords);
        Assert.Single(
            flow.Events,
            item => item.Type ==
                    "intake.recovery-pending");
        Assert.Single(
            harness.Runner.Contexts,
            context => context.InvocationKind ==
                       ExecutionInvocationKind.Intake);
        await using var database =
            await harness.Factory.CreateDbContextAsync();
        Assert.Single(await database.TaskProfiles
            .Where(profile =>
                profile.FlowRunId == harness.FlowId)
            .ToListAsync());
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Interrupted")]
    public async Task OrdinaryIntakeRecovery_ReconcilesJournalWithoutDuplicateState(
        string journalState)
    {
        await using var harness =
            await StudioFlowHarness.CreateAsync(FlowKind.Advisory);
        var output = Intake(
            "AwaitingConfirmation",
            "Advisory");
        _ = await SeedOrdinaryIntakeRecoveryAsync(
            harness,
            StepStatus.Running,
            journalState == "Completed"
                ? output
                : null,
            interrupted: journalState == "Interrupted");

        _ = await harness.Intake
            .RecoverOrdinaryIntakesAsync(CancellationToken.None);
        _ = await harness.Intake
            .RecoverOrdinaryIntakesAsync(CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        Assert.Equal(StepStatus.Completed, Assert.Single(flow.Steps).Status);
        Assert.Single(flow.GateRecords);
        Assert.Single(
            flow.Messages,
            item => item.Role == ConversationRole.Customer);
        Assert.Single(
            flow.Messages,
            item => item.Role == ConversationRole.AccountManager);
        if (journalState == "Completed")
        {
            Assert.Empty(harness.Runner.Contexts);
        }
        else
        {
            var context = Assert.Single(harness.Runner.Contexts);
            Assert.True(context.ResumeSession);
            Assert.True(context.RecoverInterruptedSession);
        }
    }

    [Fact]
    public async Task OrdinaryIntakeRecovery_ActiveJournalDefersWithoutConcurrentExecution()
    {
        await using var harness =
            await StudioFlowHarness.CreateAsync(FlowKind.Advisory);
        var sessionId = await SeedOrdinaryIntakeRecoveryAsync(
            harness,
            StepStatus.Running);
        var before = await harness.LoadFlowAsync();
        var step = Assert.Single(before.Steps);
        harness.Intake.SessionInspectorOverride =
            (home, observedSessionId, _) => Task.FromResult(
                new CopilotSessionSnapshot(
                    observedSessionId,
                    home,
                    Path.Combine(
                        home,
                        "session-state",
                        observedSessionId.ToString("D")),
                    CopilotSessionJournalState.Active,
                    harness.WorkspacePath,
                    "account-manager",
                    step.StartedAt,
                    CompletedAt: null,
                    Result: null,
                    ActiveProcessIds: [12345],
                    Detail: "Fixture active owner."));
        harness.Intake.ActiveSessionStopperOverride =
            _ => false;

        var first = await harness.Intake
            .RecoverOrdinaryIntakesAsync(CancellationToken.None);
        var second = await harness.Intake
            .RecoverOrdinaryIntakesAsync(CancellationToken.None);

        Assert.Empty(first);
        Assert.Empty(second);
        var deferred = await harness.LoadFlowAsync();
        Assert.Equal(
            sessionId,
            Assert.Single(deferred.Steps).CopilotSessionId);
        Assert.Equal(
            StepStatus.Running,
            Assert.Single(deferred.Steps).Status);
        Assert.Empty(harness.Runner.Contexts);
        Assert.Empty(deferred.GateRecords);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryIntakeRecovery_MissingOrInvalidJournalBecomesRetryableFailure(
        bool invalidCompletedOutput)
    {
        await using var harness =
            await StudioFlowHarness.CreateAsync(FlowKind.Advisory);
        _ = await SeedOrdinaryIntakeRecoveryAsync(
            harness,
            StepStatus.Running,
            invalidCompletedOutput
                ? "MALFORMED_RECOVERED_INTAKE"
                : null,
            writeJournal: invalidCompletedOutput);

        _ = await harness.Intake
            .RecoverOrdinaryIntakesAsync(CancellationToken.None);
        _ = await harness.Intake
            .RecoverOrdinaryIntakesAsync(CancellationToken.None);

        var flow = await harness.LoadFlowAsync();
        var failed = Assert.Single(flow.Steps);
        Assert.Equal(FlowStatus.Intake, flow.Status);
        Assert.Equal(StepStatus.Failed, failed.Status);
        Assert.Single(
            flow.Events,
            item => item.Type == "intake.attempt-failed");
        Assert.Empty(flow.GateRecords);
        Assert.Empty(harness.Runner.Contexts);
    }

    [Fact]
    public async Task UnmaterializedIntakeRecovery_RecoversFlowWithNoDurableStepOrWorkspace()
    {
        await using var harness =
            await StudioFlowHarness.CreateAsync(FlowKind.Advisory);
        const string customerRequest = "Assess checkout resilience.";
        await SeedUnmaterializedIntakeAsync(harness, customerRequest);

        var seeded = await harness.LoadFlowAsync();
        Assert.Empty(seeded.Steps);
        Assert.True(string.IsNullOrEmpty(seeded.WorkspacePath));

        var recovered = await harness.Intake
            .RecoverUnmaterializedIntakesAsync(CancellationToken.None);

        Assert.Contains(harness.FlowId, recovered);
        var flow = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Intake, flow.Status);
        Assert.Equal(harness.WorkspacePath, flow.WorkspacePath);
        var step = Assert.Single(flow.Steps);
        Assert.Equal(StepStatus.Completed, step.Status);
        Assert.Equal(ExecutionInvocationKind.Intake, step.InvocationKind);
        Assert.Equal(string.Empty, step.PlanStepKey);
        Assert.Equal(customerRequest, step.InputSummary);
        Assert.Single(
            flow.Messages,
            item => item.Role == ConversationRole.Customer &&
                    item.Content == customerRequest);
        Assert.Single(
            flow.Messages,
            item => item.Role == ConversationRole.AccountManager);
        Assert.Single(flow.GateRecords);
        Assert.Single(
            flow.Events,
            item => item.Type == "intake.confirmation_requested");
        Assert.Single(
            harness.Runner.Contexts,
            context => context.InvocationKind ==
                       ExecutionInvocationKind.Intake);
        await using var database = await harness.Factory.CreateDbContextAsync();
        Assert.Single(await database.TaskProfiles
            .Where(profile => profile.FlowRunId == harness.FlowId)
            .ToListAsync());
    }

    [Fact]
    public async Task UnmaterializedIntakeRecovery_IsIdempotentAcrossRepeatedRuns()
    {
        await using var harness =
            await StudioFlowHarness.CreateAsync(FlowKind.Advisory);
        const string customerRequest = "Assess checkout resilience.";
        await SeedUnmaterializedIntakeAsync(harness, customerRequest);

        var first = await harness.Intake
            .RecoverUnmaterializedIntakesAsync(CancellationToken.None);
        var second = await harness.Intake
            .RecoverUnmaterializedIntakesAsync(CancellationToken.None);
        var third = await harness.Intake
            .RecoverUnmaterializedIntakesAsync(CancellationToken.None);

        Assert.Contains(harness.FlowId, first);
        Assert.Empty(second);
        Assert.Empty(third);
        var flow = await harness.LoadFlowAsync();
        Assert.Single(flow.Steps);
        Assert.Single(
            flow.Messages,
            item => item.Role == ConversationRole.Customer);
        Assert.Single(
            harness.Runner.Contexts,
            context => context.InvocationKind ==
                       ExecutionInvocationKind.Intake);
    }

    [Fact]
    public async Task UnmaterializedIntakeRecovery_CollidingCustomerMessagesFailClosedWithoutDuplicationOrLoop()
    {
        await using var harness =
            await StudioFlowHarness.CreateAsync(FlowKind.Advisory);
        await SeedUnmaterializedIntakeAsync(
            harness,
            "Assess checkout resilience.",
            "Actually, assess payments resilience instead.");

        var first = await harness.Intake
            .RecoverUnmaterializedIntakesAsync(CancellationToken.None);
        var second = await harness.Intake
            .RecoverUnmaterializedIntakesAsync(CancellationToken.None);

        Assert.Contains(harness.FlowId, first);
        Assert.Empty(second);
        var flow = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Intake, flow.Status);
        Assert.False(string.IsNullOrWhiteSpace(flow.FailureReason));
        var failed = Assert.Single(flow.Steps);
        Assert.Equal(StepStatus.Failed, failed.Status);
        Assert.Single(
            flow.Events,
            item => item.Type == "intake.attempt-failed");
        Assert.Equal(
            2,
            flow.Messages.Count(
                item => item.Role == ConversationRole.Customer));
        Assert.Empty(harness.Runner.Contexts);
        await using var database = await harness.Factory.CreateDbContextAsync();
        Assert.Empty(await database.TaskProfiles
            .Where(profile => profile.FlowRunId == harness.FlowId)
            .ToListAsync());
    }

    [Fact]
    public async Task UnmaterializedIntakeRecovery_PoisonedFlowDoesNotBlockHealthyFlow()
    {
        await using var harness =
            await StudioFlowHarness.CreateAsync(FlowKind.Advisory);
        await SeedUnmaterializedIntakeAsync(
            harness,
            "Assess checkout resilience.");
        Guid poisonedFlowId;
        await using (var database = await harness.Factory.CreateDbContextAsync())
        {
            var settings = await database.Settings.AsNoTracking().SingleAsync();
            var poisoned = IntakeCoordinator.CreateFlow(
                "Poisoned durable intake.",
                settings);
            poisonedFlowId = poisoned.Id;
            database.Flows.Add(poisoned);
            database.FlowMessages.Add(new FlowMessage
            {
                FlowRunId = poisoned.Id,
                Role = ConversationRole.Customer,
                Content = "Poisoned durable intake."
            });
            // Deliberately omit the required immutable Account Manager snapshot.
            await database.SaveChangesAsync();
        }

        var first = await harness.Intake
            .RecoverUnmaterializedIntakesAsync(CancellationToken.None);
        var second = await harness.Intake
            .RecoverUnmaterializedIntakesAsync(CancellationToken.None);

        Assert.Contains(harness.FlowId, first);
        Assert.Empty(second);
        var healthy = await harness.LoadFlowAsync();
        Assert.Single(
            healthy.Steps,
            step => step.InvocationKind == ExecutionInvocationKind.Intake);
        await using var verification =
            await harness.Factory.CreateDbContextAsync();
        Assert.Empty(await verification.FlowSteps
            .Where(step => step.FlowRunId == poisonedFlowId)
            .ToListAsync());
        Assert.Single(
            await verification.FlowEvents
                .Where(item =>
                    item.FlowRunId == poisonedFlowId &&
                    item.Type ==
                        "intake.unmaterialized-recovery-failed")
                .ToListAsync());
    }

    [Fact]
    public async Task ConfirmedRetryAfterOperationalFailure_ValidatesAgainstOriginalProposal()
    {
        await using var harness = await StudioFlowHarness.CreateAsync(
            FlowKind.Advisory,
            failAccountManagerRunAtOrdinal: 2);
        const string customerRequest = "Assess checkout resilience.";
        const string confirmationReply =
            "Yes, that is the exact Advisory assessment I want.";

        var proposed = await harness.Intake.ContinueAsync(
            new IntakeRequest(harness.FlowId, customerRequest));
        Assert.False(proposed.ReadyToStart);
        var awaiting = await harness.LoadFlowAsync();
        var originalProposal = Assert.Single(
            awaiting.Events,
            item => item.Type == "intake.confirmation_requested");
        Assert.False(string.IsNullOrWhiteSpace(originalProposal.DataJson));

        await Assert.ThrowsAsync<IntakeV2ContractException>(() =>
            harness.Intake.ContinueAsync(
                new IntakeRequest(harness.FlowId, confirmationReply)));
        var afterFailure = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Intake, afterFailure.Status);
        Assert.Single(
            afterFailure.Events,
            item => item.Type == "intake.attempt-failed");
        // The original pending proposal must remain durable and untouched by the
        // unrelated operational failure on the confirmation turn.
        var proposalAfterFailure = Assert.Single(
            afterFailure.Events,
            item => item.Type == "intake.confirmation_requested");
        Assert.Equal(originalProposal.DataJson, proposalAfterFailure.DataJson);

        var confirmed = await harness.Intake.ContinueAsync(
            new IntakeRequest(harness.FlowId, confirmationReply));

        Assert.True(confirmed.ReadyToStart);
        var queued = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Queued, queued.Status);
        Assert.Single(
            queued.Events,
            item => item.Type == "intake.retry-started");
        Assert.Single(
            queued.Events,
            item => item.Type == "flow.queued");
        Assert.Single(
            queued.Steps,
            item => item.Status == StepStatus.Completed &&
                    item.Label == "Customer confirmed brief");
    }

    [Fact]
    public async Task GetPendingConfirmation_IgnoresRecoveryAndProgressNoiseEvents()
    {
        await using var harness =
            await StudioFlowHarness.CreateAsync(
                FlowKind.Advisory,
                confirmOnFirstAccountManagerRun: true);
        const string customerRequest = "Assess checkout resilience.";
        var awaitingOutput = Intake("AwaitingConfirmation", "Advisory");
        var parsed = IntakeV2Parser.Parse(awaitingOutput);
        var proposalDataJson = IntakeV2Parser.Serialize(parsed.Document);

        await using (var database =
                     await harness.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows
                .Include(item => item.Messages)
                .Include(item => item.AgentSnapshots)
                .SingleAsync(item => item.Id == harness.FlowId);
            flow.WorkspacePath = harness.WorkspacePath;
            flow.Messages.Add(new FlowMessage
            {
                FlowRunId = flow.Id,
                Role = ConversationRole.Customer,
                Content = customerRequest
            });
            flow.Messages.Add(new FlowMessage
            {
                FlowRunId = flow.Id,
                Role = ConversationRole.AccountManager,
                Content = parsed.Document.CustomerReply,
                IsQuestion = true
            });
            var accountManager = flow.AgentSnapshots.Single(
                item => item.AgentId == "account-manager");
            var step = new FlowStep
            {
                FlowRunId = flow.Id,
                Iteration = flow.Iteration,
                Sequence = -99,
                AgentId = accountManager.AgentId,
                AgentName = accountManager.Name,
                AgentRole = accountManager.Role,
                Label = "Customer confirmation requested",
                PlanDutiesJson = """["Analyze"]""",
                PlanStage = PlanStage.BeforeReview,
                InvocationKind = ExecutionInvocationKind.Intake,
                PermissionProfile =
                    ExecutionPermissionProfile.ReadOnlySource,
                WorkflowRevision = "planning-revision",
                Status = StepStatus.Completed,
                Phase = AgentRunPhase.Succeeded,
                Attempt = 1,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-9),
                InputSummary = customerRequest,
                OutputSummary = awaitingOutput
            };
            step.StableSemanticRootId = step.Id;
            flow.Steps.Add(step);
            flow.Events.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = step.Id,
                Type = "intake.confirmation_requested",
                Message =
                    "Copilot Account Manager presented its understanding for customer confirmation.",
                DataJson = proposalDataJson,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-9)
            });
            // Later reconciliation noise that must never shadow the real proposal above.
            flow.Events.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = step.Id,
                Type = "intake.recovery-pending",
                Message = "Continued the existing pending initial Account Manager attempt.",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
            });
            flow.Events.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = step.Id,
                Type = "intake.recovery-journal-completed",
                Message = "Recovered the completed Copilot session journal.",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-4)
            });
            flow.Events.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = step.Id,
                Type = "intake.recovery-session-resumed",
                Message = "Resumed the interrupted Copilot session.",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-3)
            });
            await database.SaveChangesAsync();
        }

        var confirmed = await harness.Intake.ContinueAsync(
            new IntakeRequest(
                harness.FlowId,
                "Yes, that is the exact Advisory assessment I want."));

        Assert.True(confirmed.ReadyToStart);
        var queued = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Queued, queued.Status);
        Assert.Single(
            queued.Events,
            item => item.Type == "intake.confirmed");
    }

    [Theory]
    [InlineData(FlowKind.Advisory)]
    [InlineData(FlowKind.Delivery)]
    public async Task IntakeV2_PersistsKindThenRunsAccountManagerAndTeamLead(
        FlowKind kind)
    {
        await using var harness = await StudioFlowHarness.CreateAsync(kind);

        var proposed = await harness.Intake.ContinueAsync(
            new IntakeRequest(harness.FlowId, "Assess checkout resilience."));
        Assert.Equal("studio-v2", proposed.Flow.ContractVersion);
        Assert.Equal(kind, proposed.Flow.Kind);
        Assert.False(proposed.ReadyToStart);
        var proposalEvent = Assert.Single(
            proposed.Flow.Events,
            item => item.Type == "intake.confirmation_requested");
        var persistedProposal = IntakeV2Parser.ParseJson(proposalEvent.DataJson!);
        Assert.Equal(kind, persistedProposal.Document.FlowKind);

        var confirmed = await harness.Intake.ContinueAsync(
            new IntakeRequest(harness.FlowId, "Yes, that is correct."));
        Assert.True(confirmed.ReadyToStart);
        Assert.Equal(kind, confirmed.Flow.Kind);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var flow = await harness.LoadFlowAsync();

        Assert.Equal(FlowStatus.WaitingForFeedback, flow.Status);
        Assert.Equal(
            ["account-manager", "account-manager", "team-lead"],
            harness.Runner.Contexts.Take(3).Select(item => item.AgentId));
        Assert.False(string.IsNullOrWhiteSpace(flow.OutcomeContractJson));
        var review = Assert.Single(
            flow.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);
        Assert.Equal("The requested result is ready for review.", review.Summary);
        if (kind == FlowKind.Advisory)
        {
            Assert.Equal(OutcomeType.None, flow.Outcome);
            Assert.DoesNotContain(
                "commit",
                flow.OutcomeLabel,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "pull request",
                flow.OutcomeLabel,
                StringComparison.OrdinalIgnoreCase);
            Assert.Single(
                Directory.EnumerateFiles(
                    Path.Combine(
                        harness.WorkspacePath,
                        ".studio",
                        "advisory"),
                    "recommendation.md",
                    SearchOption.AllDirectories));
        }
    }

    [Fact]
    public async Task Confirmation_CannotSilentlyChangeTheProposedKind()
    {
        await using var harness = await StudioFlowHarness.CreateAsync(
            FlowKind.Advisory,
            oppositeKindOnConfirmation: true);

        await harness.Intake.ContinueAsync(
            new IntakeRequest(harness.FlowId, "Assess checkout resilience."));
        var confirmed = await harness.Intake.ContinueAsync(
            new IntakeRequest(harness.FlowId, "Yes."));

        Assert.Equal(FlowKind.Advisory, confirmed.Flow.Kind);
        Assert.Equal(OutcomeType.None, confirmed.Flow.Outcome);
    }

    [Fact]
    public void NewIntakeFlows_UseStudioV2()
    {
        var flow = IntakeCoordinator.CreateFlow(
            "Assess checkout resilience.",
            new HarnessSettings
            {
                RepositoryPath = @"C:\source",
                RepositoryKnowledge = "Repository facts.",
                Outcome = OutcomeType.PullRequest
            });

        Assert.Equal("studio-v2", flow.ContractVersion);
        Assert.Equal(FlowStatus.Intake, flow.Status);
        Assert.Empty(flow.OutcomeVerificationJson);
    }

    [Fact]
    public void DeliveryIntake_RejectsMalformedPersistedNoneButAdvisoryUsesNoneLocally()
    {
        var malformed = new HarnessSettings
        {
            RepositoryPath = @"C:\source",
            RepositoryKnowledge = "Repository facts.",
            Outcome = OutcomeType.None
        };
        Assert.Throws<ArgumentException>(() =>
            IntakeCoordinator.CreateFlow(
                "Implement checkout resilience.",
                malformed));

        var deliveryFlow = new FlowRun
        {
            Title = "Implement checkout",
            OriginalRequest = "Implement checkout.",
            Kind = FlowKind.Delivery,
            Outcome = OutcomeType.None
        };
        var delivery = IntakeCoordinator.ParseResponse(
            Intake("AwaitingConfirmation", "Delivery"),
            "studio-v2");
        Assert.Throws<ArgumentException>(() =>
            IntakeCoordinator.ApplyIntakeOutcome(
                deliveryFlow,
                delivery,
                configuredDeliveryOutcome: OutcomeType.None));

        var flow = new FlowRun
        {
            Title = "Assess checkout",
            OriginalRequest = "Assess checkout.",
            Kind = FlowKind.Delivery,
            Outcome = OutcomeType.PullRequest
        };
        var advisory = IntakeCoordinator.ParseResponse(
            Intake("AwaitingConfirmation", "Advisory"),
            "studio-v2");

        IntakeCoordinator.ApplyIntakeOutcome(
            flow,
            advisory,
            configuredDeliveryOutcome: OutcomeType.PullRequest);

        Assert.Equal(FlowKind.Advisory, flow.Kind);
        Assert.Equal(OutcomeType.None, flow.Outcome);
    }

    [Fact]
    public async Task ProvisionalAndAdvisorySnapshots_DoNotChangeSourceGitMetadata()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var beforeRefs = await fixture.GitAsync("show-ref");
        var beforeWorktrees = await fixture.GitAsync("worktree", "list", "--porcelain");
        var sourceBytes = await File.ReadAllBytesAsync(
            Path.Combine(fixture.SourcePath, "source.txt"));
        var flow = fixture.Flow(FlowStatus.Intake, FlowKind.Delivery);

        var provisional = await fixture.Manager.PrepareAsync(flow);

        Assert.Equal(WorkspaceMode.ProvisionalReadOnly, provisional.Mode);
        Assert.Empty(provisional.BranchName);
        Assert.False(Directory.EnumerateFileSystemEntries(
            provisional.Path,
            ".git",
            SearchOption.AllDirectories).Any());
        Assert.Equal(beforeRefs.StandardOutput, (await fixture.GitAsync("show-ref")).StandardOutput);
        Assert.Equal(
            beforeWorktrees.StandardOutput,
            (await fixture.GitAsync("worktree", "list", "--porcelain")).StandardOutput);
        Assert.Equal(
            sourceBytes,
            await File.ReadAllBytesAsync(
                Path.Combine(fixture.SourcePath, "source.txt")));

        flow.WorkspacePath = provisional.Path;
        flow.Status = FlowStatus.Queued;
        flow.Kind = FlowKind.Advisory;
        var advisory = await fixture.Manager.PrepareAsync(flow);
        Assert.Equal(WorkspaceMode.AdvisoryReadOnly, advisory.Mode);
        Assert.False(Directory.Exists(Path.Combine(advisory.Path, ".git")));

        var cleanup = await fixture.Manager.RemoveAsync(flow);
        Assert.Equal(WorkspaceCleanupResult.Empty, cleanup);
        Assert.Equal(beforeRefs.StandardOutput, (await fixture.GitAsync("show-ref")).StandardOutput);
        Assert.Equal(
            beforeWorktrees.StandardOutput,
            (await fixture.GitAsync("worktree", "list", "--porcelain")).StandardOutput);
    }

    [Fact]
    public async Task GuardedSnapshotCrashAfterMove_AdoptsOnlyMatchingFlowOwnedContent()
    {
        var fault = new ThrowOnceGuardedSnapshotFaultInjector(
            GuardedSnapshotTransition.FinalDirectoryMoved);
        await using var fixture = await WorkspaceFixture.CreateAsync(
            guardedSnapshotFaultInjector: fault);
        var flow = fixture.Flow(FlowStatus.Intake, FlowKind.Delivery);

        await Assert.ThrowsAsync<SimulatedGuardedSnapshotCrashException>(
            () => fixture.Manager.PrepareAsync(flow));
        Assert.Empty(flow.WorkspacePath);
        var expectedPath = Path.Combine(
            fixture.Root,
            "workspaces",
            flow.Id.ToString("N")[..16]);
        Assert.True(Directory.Exists(expectedPath));

        var recovered = await fixture.Manager.PrepareAsync(flow);

        Assert.False(recovered.CreatedNow);
        Assert.Equal(WorkspaceMode.ProvisionalReadOnly, recovered.Mode);
        Assert.Equal(expectedPath, recovered.Path);
        Assert.Equal(
            "original source",
            await File.ReadAllTextAsync(
                Path.Combine(recovered.Path, "source.txt")));

        var unrelated = fixture.Flow(FlowStatus.Intake, FlowKind.Delivery);
        var unrelatedPath = Path.Combine(
            fixture.Root,
            "workspaces",
            unrelated.Id.ToString("N")[..16]);
        Directory.CreateDirectory(unrelatedPath);
        await File.WriteAllTextAsync(
            Path.Combine(unrelatedPath, "arbitrary.txt"),
            "not flow-owned");
        var collision = await Assert.ThrowsAsync<IOException>(
            () => fixture.Manager.PrepareAsync(unrelated));
        Assert.Contains("without matching flow ownership", collision.Message);
        Assert.True(File.Exists(
            Path.Combine(unrelatedPath, "arbitrary.txt")));
    }

    [Fact]
    public async Task GuardedSnapshotJournalBeforeMove_NeverDeletesARacingDirectory()
    {
        var fault = new ThrowOnceGuardedSnapshotFaultInjector(
            GuardedSnapshotTransition.OwnershipJournaled);
        await using var fixture = await WorkspaceFixture.CreateAsync(
            guardedSnapshotFaultInjector: fault);
        var flow = fixture.Flow(FlowStatus.Intake, FlowKind.Delivery);
        var expectedPath = Path.Combine(
            fixture.Root,
            "workspaces",
            flow.Id.ToString("N")[..16]);

        await Assert.ThrowsAsync<SimulatedGuardedSnapshotCrashException>(
            () => fixture.Manager.PrepareAsync(flow));
        Directory.CreateDirectory(expectedPath);
        var unrelatedFile = Path.Combine(expectedPath, "unrelated.txt");
        await File.WriteAllTextAsync(unrelatedFile, "racing content");

        var collision = await Assert.ThrowsAsync<IOException>(
            () => fixture.Manager.PrepareAsync(flow));

        Assert.Contains("without matching flow ownership", collision.Message);
        Assert.Equal(
            "racing content",
            await File.ReadAllTextAsync(unrelatedFile));
    }

    [Fact]
    public async Task GuardedSnapshotAbandonment_PreservesUnownedDeterministicPath()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var flow = fixture.Flow(FlowStatus.Abandoning, FlowKind.Delivery);
        var expectedPath = Path.Combine(
            fixture.Root,
            "workspaces",
            flow.Id.ToString("N")[..16]);
        Directory.CreateDirectory(expectedPath);
        var unrelatedFile = Path.Combine(expectedPath, "unrelated.txt");
        await File.WriteAllTextAsync(
            unrelatedFile,
            "content owned by another process");

        var cleanup = await fixture.Manager.RemoveAsync(flow);

        Assert.Equal(
            "content owned by another process",
            await File.ReadAllTextAsync(unrelatedFile));
        Assert.Single(cleanup.CleanupDiagnostics);
        Assert.Contains(
            "preserved",
            cleanup.CleanupDiagnostics[0],
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, cleanup.WorktreesRemoved);
    }

    [Fact]
    public async Task ConfirmedDelivery_RematerializesTheSameLocationAsAWorktree()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var flow = fixture.Flow(FlowStatus.Intake, FlowKind.Delivery);
        var provisional = await fixture.Manager.PrepareAsync(flow);
        var beforeWorktrees = await fixture.GitAsync("worktree", "list", "--porcelain");

        flow.WorkspacePath = provisional.Path;
        flow.Status = FlowStatus.Queued;
        var delivery = await fixture.Manager.PrepareAsync(flow);

        Assert.Equal(provisional.Path, delivery.Path);
        Assert.Equal(WorkspaceMode.Delivery, delivery.Mode);
        Assert.False(string.IsNullOrWhiteSpace(delivery.BranchName));
        Assert.NotEqual(
            beforeWorktrees.StandardOutput,
            (await fixture.GitAsync("worktree", "list", "--porcelain")).StandardOutput);

        flow.BranchName = delivery.BranchName;
        var cleanup = await fixture.Manager.RemoveAsync(flow);
        Assert.Equal(1, cleanup.WorktreesRemoved);
        Assert.Equal(1, cleanup.LocalBranchesDeleted);
    }

    [Fact]
    public async Task AdvisoryPolicy_SkipsEveryWorkspaceHook()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync(includeHooks: true);
        var workingDirectory = Path.Combine(fixture.Root, "hook-workspace");
        Directory.CreateDirectory(workingDirectory);
        var workflow = fixture.WorkflowProvider.GetEffective();

        foreach (var stage in Enum.GetValues<WorkspaceHookStage>())
        {
            await fixture.HookRunner.RunAsync(
                stage,
                workingDirectory,
                workflow,
                FlowKind.Advisory,
                provisional: false);
        }

        Assert.False(File.Exists(Path.Combine(workingDirectory, "hook-ran.txt")));
        await fixture.HookRunner.RunAsync(
            WorkspaceHookStage.AfterCreate,
            workingDirectory,
            workflow,
            FlowKind.Delivery,
            provisional: false);
        Assert.True(File.Exists(Path.Combine(workingDirectory, "hook-ran.txt")));
    }

    [Fact]
    public async Task ReviewClassificationPolicy_RunsNoMutatingHooksAndKeepsCandidateCurrent()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync(
            includeHooks: true);
        var workingDirectory = Path.Combine(
            fixture.Root,
            "review-classification-workspace");
        Directory.CreateDirectory(workingDirectory);
        var candidatePath = Path.Combine(
            workingDirectory,
            "candidate.txt");
        await File.WriteAllTextAsync(candidatePath, "sealed");
        var workflow = fixture.WorkflowProvider.GetEffective();

        foreach (var stage in new[]
                 {
                     WorkspaceHookStage.BeforeRun,
                     WorkspaceHookStage.AfterRun
                 })
        {
            if (CopilotReasoningHost.ShouldRunWorkspaceHooks(
                    "studio-v2",
                    ExecutionInvocationKind.ReviewClassification))
            {
                await fixture.HookRunner.RunAsync(
                    stage,
                    workingDirectory,
                    workflow,
                    FlowKind.Delivery,
                    provisional: false);
            }
        }

        Assert.False(File.Exists(
            Path.Combine(workingDirectory, "hook-ran.txt")));
        Assert.Equal("sealed", await File.ReadAllTextAsync(candidatePath));
        Assert.True(CopilotReasoningHost.ShouldRunWorkspaceHooks(
            "studio-v2",
            ExecutionInvocationKind.Worker));
        Assert.False(CopilotReasoningHost.ShouldRunWorkspaceHooks(
            "studio-v2",
            ExecutionInvocationKind.Publication));
    }

    [Fact]
    public void AdvisoryPermission_IsReadOnlyWithoutShellOrPublication()
    {
        var resolver = new PermissionProfileResolver();
        var restrictions = new WorkflowPermissionRestrictions(
            ExecutionPermissionProfile.ReadOnlySource,
            ExecutionPermissionProfile.WorkspaceWrite,
            ExecutionPermissionProfile.Publish,
            Enum.GetValues<ExecutionPermissionProfile>()
                .ToImmutableDictionary(
                    profile => profile,
                    _ => ImmutableArray<string>.Empty));

        var permission = resolver.Resolve(
            new PermissionResolutionRequest(
                FlowKind.Advisory,
                ExecutionInvocationKind.Worker,
                PlanStage.BeforeReview,
                ImmutableArray.Create(
                    PlanDuty.Analyze,
                    PlanDuty.PrepareOutcome),
                DurableReviewDecision: null,
                DurableApproval: false,
                IsOnlyPlannedPublishStep: false,
                ContractVersion: "studio-v2",
                LegacyPublicationAuthorized: false,
                IsGovernedOutcomeVerification: false),
            restrictions);

        Assert.Equal(ExecutionPermissionProfile.ReadOnlySource, permission.Profile);
        Assert.DoesNotContain("write", permission.AllowedTools);
        Assert.DoesNotContain("shell", permission.AllowedTools);
        Assert.Contains("write", permission.DeniedTools);
        Assert.Contains("shell", permission.DeniedTools);
        Assert.False(permission.AllowRemotePublication);
    }

    [Fact]
    public async Task AdvisoryOutcome_VerifiesSourceAndWritesOnlyValidatedArtifacts()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var flow = fixture.Flow(FlowStatus.Queued, FlowKind.Advisory);
        var workspace = await fixture.Manager.PrepareAsync(flow);
        flow.WorkspacePath = workspace.Path;
        flow.Outcome = OutcomeType.None;
        var parsed = FlowOutcomeParser.Parse(Outcome(
            """
            [{"Path":"recommendations\\checkout.md","MediaType":"text/markdown","Content":"# Recommendation\nUse idempotency keys."}]
            """));

        flow.OutcomeContractJson = parsed.RawJson;
        var result = await fixture.Artifacts.VerifyAndWriteAsync(flow, parsed);
        AttachMaterialization(flow, fixture.Artifacts, result);

        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal("recommendations/checkout.md", artifact.Path);
        Assert.Equal(
            "# Recommendation\nUse idempotency keys.",
            await File.ReadAllTextAsync(artifact.FullPath));
        Assert.Equal(artifact.FullPath, fixture.Artifacts.ResolveFile(
            flow,
            artifact.Path));
        Assert.Equal(
            result.Verification.BaselineDigest,
            result.Verification.VerifiedDigest);
    }

    [Fact]
    public async Task AdvisoryMaterialization_IsCrashIdempotentAndRejectsMismatch()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var flow = fixture.Flow(FlowStatus.Queued, FlowKind.Advisory);
        var workspace = await fixture.Manager.PrepareAsync(flow);
        flow.WorkspacePath = workspace.Path;
        flow.Outcome = OutcomeType.None;
        var parsed = FlowOutcomeParser.Parse(Outcome(
            """
            [{"Path":"answer.md","MediaType":"text/markdown","Content":"reviewed answer"}]
            """));
        flow.OutcomeContractJson = parsed.RawJson;

        // The first call represents the host move succeeding before its DB event is saved.
        var first = await fixture.Artifacts.VerifyAndWriteAsync(flow, parsed);
        var recovered = await fixture.Artifacts.VerifyAndWriteAsync(flow, parsed);

        Assert.Equal(
            first.Policy.MaterializationDirectory,
            recovered.Policy.MaterializationDirectory);
        Assert.Equal(
            Assert.Single(first.Artifacts).FullPath,
            Assert.Single(recovered.Artifacts).FullPath);

        await File.WriteAllTextAsync(
            recovered.Artifacts[0].FullPath,
            "different bytes");
        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Artifacts.VerifyAndWriteAsync(flow, parsed));
        Assert.Contains("no longer matches", mismatch.Message);
    }

    [Fact]
    public async Task LegacyAdvisoryV1Artifacts_RemainDiscoverableAfterCatalogReload()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var flow = fixture.Flow(FlowStatus.Approved, FlowKind.Advisory);
        var workspace = await fixture.Manager.PrepareAsync(flow);
        flow.WorkspacePath = workspace.Path;
        flow.Outcome = OutcomeType.None;
        var outcome = FlowOutcomeParser.Parse(Outcome(
            """
            [{"Path":"answer.md","MediaType":"text/markdown","Content":"legacy answer"}]
            """));
        flow.OutcomeContractJson = outcome.RawJson;
        var step = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Sequence = 20,
            AgentId = "advisor",
            AgentName = "Advisor",
            AgentRole = "advisor",
            PlanStepKey = "prepare-answer",
            Status = StepStatus.Completed
        };
        flow.Steps.Add(step);
        flow.Events.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = step.Id,
            Type = AdvisoryArtifactCatalog.MaterializationEventType,
            Message = "Legacy Advisory artifacts were materialized.",
            DataJson = """{"Version":"advisory-artifacts-v1"}"""
        });
        var artifactRoot = Path.Combine(
            workspace.Path,
            ".studio",
            "advisory");
        Directory.CreateDirectory(artifactRoot);
        await File.WriteAllTextAsync(
            Path.Combine(artifactRoot, "answer.md"),
            "legacy answer");

        var reloaded = new AdvisoryArtifactCatalog(
            fixture.WorkflowProvider);
        var artifact = Assert.Single(reloaded.Discover(flow));
        var policy = reloaded.TryReadCurrentMaterialization(flow);

        Assert.Equal("answer.md", artifact.Path);
        Assert.Equal("legacy answer", await File.ReadAllTextAsync(
            artifact.FullPath));
        Assert.NotNull(policy);
        Assert.True(policy!.LegacyMigratedReadOnly);
        Assert.Equal(
            policy.ArtifactDirectory,
            policy.MaterializationDirectory);
        Assert.Throws<InvalidOperationException>(() =>
            reloaded.SerializeMaterialization(policy));
    }

    [Fact]
    public async Task PreExistingArtifactDirectoryFiles_RemainPartOfSourceBaseline()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var sourceArtifactDirectory = Path.Combine(
            fixture.SourcePath,
            ".studio",
            "advisory");
        Directory.CreateDirectory(sourceArtifactDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(sourceArtifactDirectory, "source-note.md"),
            "source-owned");
        var flow = fixture.Flow(FlowStatus.Queued, FlowKind.Advisory);
        var workspace = await fixture.Manager.PrepareAsync(flow);
        flow.WorkspacePath = workspace.Path;

        await File.WriteAllTextAsync(
            Path.Combine(
                workspace.Path,
                ".studio",
                "advisory",
                "source-note.md"),
            "mutated");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Artifacts.VerifyBaselineAsync(flow));
        Assert.Contains("snapshot changed", exception.Message);
    }

    [Fact]
    public async Task AcceptedAdvisoryArtifacts_KeepPersistedPolicyAcrossReloadAndRefinement()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var flow = fixture.Flow(FlowStatus.Queued, FlowKind.Advisory);
        var workspace = await fixture.Manager.PrepareAsync(flow);
        flow.WorkspacePath = workspace.Path;
        flow.Outcome = OutcomeType.None;
        var firstOutcome = FlowOutcomeParser.Parse(Outcome(
            """
            [{"Path":"answer.md","MediaType":"text/markdown","Content":"iteration one"}]
            """));
        flow.OutcomeContractJson = firstOutcome.RawJson;
        var first = await fixture.Artifacts.VerifyAndWriteAsync(
            flow,
            firstOutcome);
        AttachMaterialization(flow, fixture.Artifacts, first);
        var firstPath = Assert.Single(
            fixture.Artifacts.Discover(flow)).FullPath;

        await fixture.WriteAdvisoryPolicyAsync(
            artifactDirectory: @".studio\future-advisory",
            maximumArtifactCount: 1,
            maximumTotalArtifactBytes: 32);

        var historical = Assert.Single(fixture.Artifacts.Discover(flow));
        Assert.Equal(firstPath, historical.FullPath);
        Assert.Equal("iteration one", await File.ReadAllTextAsync(firstPath));

        flow.Iteration = 2;
        var secondOutcome = FlowOutcomeParser.Parse(Outcome(
            """
            [{"Path":"answer.md","MediaType":"text/markdown","Content":"iteration two"}]
            """));
        flow.OutcomeContractJson = secondOutcome.RawJson;
        var second = await fixture.Artifacts.VerifyAndWriteAsync(
            flow,
            secondOutcome);
        AttachMaterialization(flow, fixture.Artifacts, second);
        var current = Assert.Single(fixture.Artifacts.Discover(flow));

        Assert.NotEqual(firstPath, current.FullPath);
        Assert.Contains(
            $"{Path.DirectorySeparatorChar}future-advisory{Path.DirectorySeparatorChar}",
            current.FullPath);
        Assert.Equal("iteration two", await File.ReadAllTextAsync(current.FullPath));
        Assert.True(File.Exists(firstPath));
    }

    [Fact]
    public async Task AdvisorySourceMutation_BlocksArtifactsAndCustomerReview()
    {
        await using var harness = await StudioFlowHarness.CreateAsync(
            FlowKind.Advisory,
            mutateSource: true);
        await harness.Intake.ContinueAsync(
            new IntakeRequest(harness.FlowId, "Assess checkout resilience."));
        await harness.Intake.ContinueAsync(
            new IntakeRequest(harness.FlowId, "Yes."));

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var flow = await harness.LoadFlowAsync();

        Assert.Equal(FlowStatus.Failed, flow.Status);
        Assert.DoesNotContain(
            flow.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);
        Assert.Contains(
            flow.Steps,
            step => step.IsOutcomeOwner && step.Status == StepStatus.Failed);
        Assert.False(Directory.Exists(Path.Combine(
            harness.WorkspacePath,
            ".studio",
            "advisory")));
        Assert.Contains(
            flow.Events,
            item => item.Type == "advisory.source-verification-failed");
    }

    [Fact]
    public async Task MissingFlowOutcome_BlocksCustomerReview()
    {
        await using var harness = await StudioFlowHarness.CreateAsync(
            FlowKind.Advisory,
            omitOutcome: true);
        await harness.Intake.ContinueAsync(
            new IntakeRequest(harness.FlowId, "Assess checkout resilience."));
        await harness.Intake.ContinueAsync(
            new IntakeRequest(harness.FlowId, "Yes."));

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        var flow = await harness.LoadFlowAsync();

        Assert.Equal(FlowStatus.Failed, flow.Status);
        Assert.Contains("FLOW_OUTCOME_V1", flow.FailureReason);
        Assert.DoesNotContain(
            flow.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);
    }

    [Fact]
    public void OutcomeArtifacts_RejectTraversalAbsoluteMimeCountBytesAndCollisions()
    {
        Assert.Throws<FlowOutcomeContractException>(() =>
            FlowOutcomeParser.Parse(Outcome(
                """[{"Path":"..\\secret.md","MediaType":"text/markdown","Content":"x"}]""")));
        Assert.Throws<FlowOutcomeContractException>(() =>
            FlowOutcomeParser.Parse(Outcome(
                """[{"Path":"C:\\secret.md","MediaType":"text/markdown","Content":"x"}]""")));
        Assert.Throws<FlowOutcomeContractException>(() =>
            FlowOutcomeParser.Parse(Outcome(
                """[{"Path":"answer.exe","MediaType":"application/octet-stream","Content":"x"}]""")));
        Assert.Throws<FlowOutcomeContractException>(() =>
            FlowOutcomeParser.Parse(
                Outcome(
                    """[{"Path":"one.md","MediaType":"text/markdown","Content":"x"},{"Path":"two.md","MediaType":"text/markdown","Content":"x"}]"""),
                maximumArtifactCount: 1));
        Assert.Throws<FlowOutcomeContractException>(() =>
            FlowOutcomeParser.Parse(
                Outcome(
                    """[{"Path":"one.md","MediaType":"text/markdown","Content":"éé"}]"""),
                maximumArtifactCount: 1,
                maximumTotalArtifactBytes: 3));
        Assert.Throws<FlowOutcomeContractException>(() =>
            FlowOutcomeParser.Parse(Outcome(
                """[{"Path":"answer.md","MediaType":"text/markdown","Content":"one"},{"Path":"ANSWER.md","MediaType":"text/markdown","Content":"two"}]""")));
        Assert.Throws<FlowOutcomeContractException>(() =>
            FlowOutcomeParser.Parse(Outcome(
                """[{"Path":"answer","MediaType":"text/plain","Content":"one"},{"Path":"answer/details.md","MediaType":"text/markdown","Content":"two"}]""")));
    }

    private static void AttachMaterialization(
        FlowRun flow,
        AdvisoryArtifactCatalog catalog,
        AdvisoryOutcomeMaterialization materialization)
    {
        flow.Events.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = Guid.NewGuid(),
            Type = AdvisoryArtifactCatalog.MaterializationEventType,
            Message = "Fixture persisted the accepted Advisory materialization.",
            DataJson = catalog.SerializeMaterialization(
                materialization.Policy)
        });
    }

    private static async Task<Guid> SeedOrdinaryIntakeRecoveryAsync(
        StudioFlowHarness harness,
        StepStatus status,
        string? completedOutput = null,
        bool interrupted = false,
        bool writeJournal = false)
    {
        const string message = "Assess checkout resilience.";
        var sessionId = AgentSessionIdentity.Create(
            harness.FlowId,
            1,
            "account-manager",
            string.Empty);
        var workflow = harness.WorkflowProvider.GetEffective();
        var persistedPermission =
            new PermissionProfileResolver().Resolve(
                new PermissionResolutionRequest(
                    FlowKind.Delivery,
                    ExecutionInvocationKind.Intake,
                    PlanStage.BeforeReview,
                    ImmutableArray.Create(PlanDuty.Analyze),
                    DurableReviewDecision: null,
                    DurableApproval: false,
                    IsOnlyPlannedPublishStep: false,
                    ContractVersion: "studio-v2",
                    LegacyPublicationAuthorized: false,
                    IsGovernedOutcomeVerification: false),
                PermissionProfileResolver.FromWorkflow(workflow));
        var copilotHome = Path.Combine(
            harness.Root,
            "copilot-home");
        await using (var database =
                     await harness.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows
                .Include(item => item.Messages)
                .Include(item => item.AgentSnapshots)
                .SingleAsync(item => item.Id == harness.FlowId);
            flow.WorkspacePath = harness.WorkspacePath;
            flow.Messages.Add(new FlowMessage
            {
                FlowRunId = flow.Id,
                Role = ConversationRole.Customer,
                Content = message
            });
            var accountManager = flow.AgentSnapshots.Single(item =>
                item.AgentId == "account-manager");
            var step = new FlowStep
            {
                FlowRunId = flow.Id,
                Iteration = flow.Iteration,
                Sequence = -99,
                AgentId = accountManager.AgentId,
                AgentName = accountManager.Name,
                AgentRole = accountManager.Role,
                Label = "Review customer intake",
                PlanDutiesJson = """["Analyze"]""",
                PlanStage = PlanStage.BeforeReview,
                InvocationKind = ExecutionInvocationKind.Intake,
                PermissionProfile =
                    ExecutionPermissionProfile.ReadOnlySource,
                EffectivePermissionJson =
                    status == StepStatus.Running
                        ? JsonSerializer.Serialize(
                            persistedPermission)
                        : string.Empty,
                WorkflowRevision =
                    status == StepStatus.Running
                        ? workflow.Revision
                        : "planning-revision",
                Status = status,
                Phase = status == StepStatus.Running
                    ? AgentRunPhase.StreamingTurn
                    : AgentRunPhase.PreparingWorkspace,
                Model = status == StepStatus.Running
                    ? "fixture-model"
                    : string.Empty,
                ModelEffort = status == StepStatus.Running
                    ? "medium"
                    : string.Empty,
                StartedAt = status == StepStatus.Running
                    ? DateTimeOffset.UtcNow.AddMinutes(-5)
                    : null,
                CopilotSessionId = status == StepStatus.Running
                    ? sessionId
                    : null,
                CopilotSessionHome = status == StepStatus.Running
                    ? copilotHome
                    : string.Empty,
                InputSummary = message
            };
            step.StableSemanticRootId = step.Id;
            flow.Steps.Add(step);
            await database.SaveChangesAsync();
        }

        if (completedOutput is not null ||
            interrupted ||
            writeJournal)
        {
            var sessionDirectory = Path.Combine(
                copilotHome,
                "session-state",
                sessionId.ToString("D"));
            Directory.CreateDirectory(sessionDirectory);
            var started = DateTimeOffset.UtcNow.AddMinutes(-4);
            var events = new List<string>
            {
                SerializeJournalEvent(
                    "session.start",
                    started,
                    new
                    {
                        sessionId,
                        context = new
                        {
                            cwd = harness.WorkspacePath
                        }
                    }),
                SerializeJournalEvent(
                    "subagent.selected",
                    started.AddSeconds(1),
                    new
                    {
                        agentName = "account-manager",
                        agentDisplayName = "account-manager"
                    }),
                SerializeJournalEvent(
                    "assistant.turn_start",
                    started.AddSeconds(2),
                    new { turnId = "0" })
            };
            if (!interrupted)
            {
                events.Add(SerializeJournalEvent(
                    "assistant.message",
                    started.AddSeconds(3),
                    new
                    {
                        turnId = "0",
                        content = completedOutput ?? string.Empty,
                        toolRequests = Array.Empty<object>()
                    }));
                events.Add(SerializeJournalEvent(
                    "assistant.turn_end",
                    started.AddSeconds(4),
                    new { turnId = "0" }));
                events.Add(SerializeJournalEvent(
                    "session.shutdown",
                    started.AddSeconds(5),
                    new { shutdownType = "routine" }));
            }
            await File.WriteAllTextAsync(
                Path.Combine(sessionDirectory, "events.jsonl"),
                string.Join(Environment.NewLine, events) +
                Environment.NewLine);
        }
        return sessionId;
    }

    private static async Task SeedUnmaterializedIntakeAsync(
        StudioFlowHarness harness,
        params string[] customerMessages)
    {
        await using var database =
            await harness.Factory.CreateDbContextAsync();
        var flow = await database.Flows
            .Include(item => item.Messages)
            .SingleAsync(item => item.Id == harness.FlowId);
        foreach (var content in customerMessages)
        {
            flow.Messages.Add(new FlowMessage
            {
                FlowRunId = flow.Id,
                Role = ConversationRole.Customer,
                Content = content
            });
        }
        await database.SaveChangesAsync();
    }

    private static string SerializeJournalEvent(
        string type,
        DateTimeOffset timestamp,
        object data) =>
        JsonSerializer.Serialize(new
        {
            type,
            timestamp,
            data
        });

    private static string Intake(string status, string flowKind) => $$"""
        {{IntakeV2Parser.BeginSentinel}}
        {
          "Version": "intake-v2",
          "Status": "{{status}}",
          "FlowKind": {{(flowKind == "null" ? "null" : $"\"{flowKind}\"")}},
          "TaskTitle": "Assess checkout resilience",
          "CustomerReply": "Do I understand correctly that you want a checkout resilience assessment?",
          "Brief": {
            "Goal": "Identify the highest-impact checkout resilience gaps.",
            "Details": ["Inspect the configured source project."],
            "SuccessCriteria": ["The customer receives an evidence-based recommendation."],
            "Constraints": ["Do not change source files."],
            "Assumptions": []
          }
        }
        {{IntakeV2Parser.EndSentinel}}
        """;

    private static string Outcome(string artifacts) => $$"""
        {{FlowOutcomeParser.BeginSentinel}}
        {"Version":"flow-outcome-v1","Goal":"Assess checkout resilience.","Summary":"The requested result is ready for review.","ImplementationDetails":["Use idempotency keys at the checkout boundary."],"Artifacts":{{artifacts}}}
        {{FlowOutcomeParser.EndSentinel}}
        """;

    private sealed class WorkspaceFixture : IAsyncDisposable
    {
        private WorkspaceFixture(
            string root,
            string sourcePath,
            ProcessRunner processRunner,
            WorkflowDefinitionProvider workflowProvider,
            WorkspaceHookRunner hookRunner,
            WorkspaceManager manager,
            AdvisoryArtifactCatalog artifacts)
        {
            Root = root;
            SourcePath = sourcePath;
            ProcessRunner = processRunner;
            WorkflowProvider = workflowProvider;
            HookRunner = hookRunner;
            Manager = manager;
            Artifacts = artifacts;
        }

        public string Root { get; }

        public string SourcePath { get; }

        public ProcessRunner ProcessRunner { get; }

        public WorkflowDefinitionProvider WorkflowProvider { get; }

        public WorkspaceHookRunner HookRunner { get; }

        public WorkspaceManager Manager { get; }

        public AdvisoryArtifactCatalog Artifacts { get; }

        public static async Task<WorkspaceFixture> CreateAsync(
            bool includeHooks = false,
            IGuardedSnapshotFaultInjector? guardedSnapshotFaultInjector = null)
        {
            var root = Path.Combine(
                Environment.CurrentDirectory,
                ".test-iadv",
                Guid.NewGuid().ToString("N")[..8]);
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(Path.Combine(root, ".github", "agents"));
            var hook = OperatingSystem.IsWindows()
                ? "Add-Content -Path hook-ran.txt -Value ran; if (Test-Path candidate.txt) { Set-Content -Path candidate.txt -Value mutated }"
                : "printf 'ran\\n' >> hook-ran.txt; if [ -f candidate.txt ]; then printf mutated > candidate.txt; fi";
            await File.WriteAllTextAsync(
                Path.Combine(root, "WORKFLOW.md"),
                $$"""
                ---
                workspace:
                  root: workspaces
                hooks:
                  after_create: {{(includeHooks ? hook : string.Empty)}}
                  before_run: {{(includeHooks ? hook : string.Empty)}}
                  after_run: {{(includeHooks ? hook : string.Empty)}}
                  before_remove: {{(includeHooks ? hook : string.Empty)}}
                studio:
                  advisory:
                    artifact_directory: .studio\advisory
                    max_artifact_count: 8
                    max_total_artifact_bytes: 65536
                ---

                Test workflow.
                """);
            await File.WriteAllTextAsync(
                Path.Combine(source, "source.txt"),
                "original source");
            var runner = new ProcessRunner();
            await RequireGitAsync(runner, source, "init");
            await RequireGitAsync(runner, source, "config", "user.name", "Test");
            await RequireGitAsync(
                runner,
                source,
                "config",
                "user.email",
                "test@example.com");
            await RequireGitAsync(runner, source, "add", "source.txt");
            await RequireGitAsync(runner, source, "commit", "-m", "initial");

            var paths = new HarnessPaths(
                root,
                Path.Combine(root, ".github", "agents"),
                Path.Combine(root, "harness.db"));
            var workflowProvider = new WorkflowDefinitionProvider(
                paths,
                new AiHarnessDemo.Core.Workflow.WorkflowLoader(),
                NullLogger<WorkflowDefinitionProvider>.Instance);
            _ = workflowProvider.GetEffective();
            var hookRunner = new WorkspaceHookRunner(
                workflowProvider,
                runner,
                NullLogger<WorkspaceHookRunner>.Instance);
            var artifacts = new AdvisoryArtifactCatalog(workflowProvider);
            var manager = new WorkspaceManager(
                runner,
                workflowProvider,
                hookRunner,
                NullLogger<WorkspaceManager>.Instance,
                artifacts,
                guardedSnapshotFaultInjector);
            return new WorkspaceFixture(
                root,
                source,
                runner,
                workflowProvider,
                hookRunner,
                manager,
                artifacts);
        }

        public FlowRun Flow(FlowStatus status, FlowKind kind) =>
            new()
            {
                Title = "Assess checkout resilience",
                OriginalRequest = "Assess checkout resilience.",
                ConsolidatedRequest = "Assess checkout resilience.",
                ContractVersion = "studio-v2",
                Kind = kind,
                Status = status,
                RepositoryPath = SourcePath,
                RepositoryKnowledge = "A small checkout repository.",
                Outcome = kind == FlowKind.Advisory
                    ? OutcomeType.None
                    : OutcomeType.PullRequest
            };

        public Task<ProcessResult> GitAsync(params string[] arguments) =>
            ProcessRunner.RunAsync(
                "git",
                ["-C", SourcePath, .. arguments],
                SourcePath,
                TimeSpan.FromSeconds(30));

        public async Task WriteAdvisoryPolicyAsync(
            string artifactDirectory,
            int maximumArtifactCount,
            int maximumTotalArtifactBytes)
        {
            await File.WriteAllTextAsync(
                Path.Combine(Root, "WORKFLOW.md"),
                $$"""
                ---
                workspace:
                  root: workspaces
                studio:
                  advisory:
                    artifact_directory: {{artifactDirectory}}
                    max_artifact_count: {{maximumArtifactCount}}
                    max_total_artifact_bytes: {{maximumTotalArtifactBytes}}
                ---

                Test workflow after reload.
                """);
            await WorkflowProvider.ReloadAsync();
            Assert.Equal(
                artifactDirectory,
                WorkflowProvider.GetEffective()
                    .Config.Studio.Advisory.ArtifactDirectory);
        }

        public ValueTask DisposeAsync()
        {
            WorkflowProvider.Dispose();
            try
            {
                DeleteDirectory(Root);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Git and SQLite may briefly retain handles on Windows.
            }
            return ValueTask.CompletedTask;
        }

        private static async Task RequireGitAsync(
            ProcessRunner runner,
            string repository,
            params string[] arguments)
        {
            var result = await runner.RunAsync(
                "git",
                ["-C", repository, .. arguments],
                repository,
                TimeSpan.FromSeconds(30));
            Assert.True(result.ExitCode == 0, result.CombinedOutput);
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
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
                     SearchOption.AllDirectories).OrderByDescending(
                     item => item.Length))
        {
            File.SetAttributes(directory, FileAttributes.Directory);
        }
        Directory.Delete(path, recursive: true);
    }

    private sealed class SimulatedGuardedSnapshotCrashException
        : Exception
    {
    }

    private sealed class ThrowOnceGuardedSnapshotFaultInjector(
        GuardedSnapshotTransition transition) : IGuardedSnapshotFaultInjector
    {
        private int _thrown;

        public Task OnTransitionAsync(
            GuardedSnapshotTransition observed,
            string workspacePath,
            CancellationToken cancellationToken)
        {
            if (observed == transition &&
                Interlocked.Exchange(ref _thrown, 1) == 0)
            {
                throw new SimulatedGuardedSnapshotCrashException();
            }
            return Task.CompletedTask;
        }
    }

    private sealed class StudioFlowHarness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly WorkflowDefinitionProvider _workflowProvider;
        private readonly HandoffGateEngine _gate;

        private StudioFlowHarness(
            string root,
            string workspacePath,
            Guid flowId,
            StudioDbContextFactory factory,
            IntakeCoordinator intake,
            WorkflowEngine engine,
            StudioAgentRunner runner,
            WorkflowDefinitionProvider workflowProvider,
            HandoffGateEngine gate)
        {
            _root = root;
            WorkspacePath = workspacePath;
            FlowId = flowId;
            Factory = factory;
            Intake = intake;
            Engine = engine;
            Runner = runner;
            _workflowProvider = workflowProvider;
            _gate = gate;
        }

        public string WorkspacePath { get; }

        public string Root => _root;

        public Guid FlowId { get; }

        public StudioDbContextFactory Factory { get; }

        public IntakeCoordinator Intake { get; }

        public WorkflowEngine Engine { get; }

        public StudioAgentRunner Runner { get; }

        public WorkflowDefinitionProvider WorkflowProvider =>
            _workflowProvider;

        public static async Task<StudioFlowHarness> CreateAsync(
            FlowKind proposedKind,
            bool oppositeKindOnConfirmation = false,
            bool mutateSource = false,
            bool omitOutcome = false,
            int malformedInitialIntakeRuns = 0,
            int failAccountManagerRunAtOrdinal = 0,
            bool confirmOnFirstAccountManagerRun = false)
        {
            var root = Path.Combine(
                Environment.CurrentDirectory,
                ".test-iflow",
                Guid.NewGuid().ToString("N")[..8]);
            var workspaceRoot = Path.Combine(root, "workspaces");
            var workspacePath = Path.Combine(workspaceRoot, "flow");
            var agentsPath = Path.Combine(root, ".github", "agents");
            Directory.CreateDirectory(workspacePath);
            Directory.CreateDirectory(agentsPath);
            await File.WriteAllTextAsync(
                Path.Combine(workspacePath, "source.txt"),
                "guarded source");
            await File.WriteAllTextAsync(
                Path.Combine(root, "WORKFLOW.md"),
                """
                ---
                workspace:
                  root: workspaces
                agent:
                  max_concurrent_agents: 1
                  max_attempts: 1
                studio:
                  version: 1
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

                Test {{ agent.name }} on {{ task }}.
                """);
            var databasePath = Path.Combine(root, "harness.db");
            var options = new DbContextOptionsBuilder<HarnessDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            var factory = new StudioDbContextFactory(options);
            var settings = new HarnessSettings
            {
                RepositoryPath = root,
                RepositoryKnowledge = "A configured checkout repository.",
                Outcome = OutcomeType.PullRequest,
                MaxHandoffRetries = 0
            };
            var flow = IntakeCoordinator.CreateFlow(
                "Assess checkout resilience.",
                settings);
            var workerId = proposedKind == FlowKind.Advisory
                ? "advisor"
                : "builder";
            var snapshots = new List<FlowAgentSnapshot>
            {
                Snapshot(flow.Id, "account-manager"),
                Snapshot(flow.Id, "team-lead"),
                Snapshot(flow.Id, workerId)
            };
            if (proposedKind == FlowKind.Delivery)
            {
                snapshots.Add(Snapshot(flow.Id, "publisher"));
            }
            await using (var database = await factory.CreateDbContextAsync())
            {
                await database.Database.EnsureCreatedAsync();
                database.Settings.Add(settings);
                database.Flows.Add(flow);
                database.FlowAgentSnapshots.AddRange(snapshots);
                await database.SaveChangesAsync();
            }

            var paths = new HarnessPaths(root, agentsPath, databasePath);
            var workflowProvider = new WorkflowDefinitionProvider(
                paths,
                new AiHarnessDemo.Core.Workflow.WorkflowLoader(),
                NullLogger<WorkflowDefinitionProvider>.Instance);
            _ = workflowProvider.GetEffective();
            var processRunner = new ProcessRunner();
            var catalog = new AgentCatalog(paths, factory);
            var snapshotService = new FlowAgentSnapshotService(factory, catalog);
            var runtime = new CopilotCliRuntime(
                processRunner,
                paths,
                TimeProvider.System,
                NullLogger<CopilotCliRuntime>.Instance);
            var modelCatalog = new ModelCatalogDiscovery(
                runtime,
                factory,
                TimeProvider.System,
                NullLogger<ModelCatalogDiscovery>.Instance);
            var admission = new NewWorkAdmissionService(
                factory,
                workflowProvider,
                catalog,
                runtime,
                modelCatalog);
            var runner = new StudioAgentRunner(
                proposedKind,
                workspacePath,
                oppositeKindOnConfirmation,
                mutateSource,
                omitOutcome,
                malformedInitialIntakeRuns,
                failAccountManagerRunAtOrdinal,
                confirmOnFirstAccountManagerRun);
            var gate = new HandoffGateEngine();
            gate.SetTrustLevel(HandoffActionType.Advance, HandoffTrustLevel.Auto);
            gate.SetTrustLevel(
                HandoffActionType.RequestRevision,
                HandoffTrustLevel.Auto);
            gate.SetTrustLevel(
                HandoffActionType.CustomerReview,
                HandoffTrustLevel.Gated);
            var queue = new FlowQueue();
            var workspace = new StudioWorkspaceManager(workspacePath);
            var artifacts = new AdvisoryArtifactCatalog(workflowProvider);
            var intake = new IntakeCoordinator(
                factory,
                new FixedModelRouter(),
                new BootstrapTaskProfileFactory(),
                TestRoutingSupport.Recorder(factory),
                runner,
                workspace,
                gate,
                new RepositoryContextGate(),
                queue,
                workflowProvider,
                admission,
                snapshotService);
            var engine = new WorkflowEngine(
                factory,
                catalog,
                new FlowPlanner(),
                new FixedModelRouter(),
                new BootstrapTaskProfileFactory(),
                TestRoutingSupport.Recorder(factory),
                workspace,
                runner,
                gate,
                new CopilotSessionJournal(),
                workflowProvider,
                NullLogger<WorkflowEngine>.Instance,
                flowAgentSnapshotService: snapshotService,
                teamPlanValidator: new TeamPlanValidator(),
                advisoryArtifactCatalog: artifacts,
                reviewedCandidateService:
                    new StubReviewedCandidateService());
            return new StudioFlowHarness(
                root,
                workspacePath,
                flow.Id,
                factory,
                intake,
                engine,
                runner,
                workflowProvider,
                gate);
        }

        public async Task<FlowRun> LoadFlowAsync()
        {
            await using var database = await Factory.CreateDbContextAsync();
            return await database.Flows
                .AsNoTracking()
                .AsSplitQuery()
                .Include(item => item.Steps)
                .Include(item => item.Messages)
                .Include(item => item.Events)
                .Include(item => item.GateRecords)
                .Include(item => item.AgentSnapshots)
                .SingleAsync(item => item.Id == FlowId);
        }

        public ValueTask DisposeAsync()
        {
            _workflowProvider.Dispose();
            _gate.Dispose();
            try
            {
                DeleteDirectory(_root);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // SQLite may briefly retain a handle on Windows.
            }
            return ValueTask.CompletedTask;
        }

        private static FlowAgentSnapshot Snapshot(Guid flowId, string id) =>
            new()
            {
                FlowRunId = flowId,
                AgentId = id,
                Name = id,
                Description = $"Description for {id}.",
                Role = id,
                Instructions = "Complete the assigned work.",
                DefinitionHash = $"hash-{id}",
                EnabledAtSnapshot = true,
                Required = id is "account-manager" or "team-lead",
                Switchable = id is not ("account-manager" or "team-lead"),
                SourceFileName = $"{id}.agent.md"
            };
    }

    private sealed class StudioAgentRunner(
        FlowKind proposedKind,
        string workspacePath,
        bool oppositeKindOnConfirmation,
        bool mutateSource,
        bool omitOutcome,
        int malformedInitialIntakeRuns,
        int failAccountManagerRunAtOrdinal = 0,
        bool confirmOnFirstAccountManagerRun = false) : IAgentRunner
    {
        private int _accountManagerRuns;
        private int _successfulAccountManagerRuns;

        public List<AgentExecutionContext> Contexts { get; } = [];

        public Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            if (context.AgentId == "account-manager")
            {
                _accountManagerRuns++;
                if (_accountManagerRuns <= malformedInitialIntakeRuns ||
                    _accountManagerRuns == failAccountManagerRunAtOrdinal)
                {
                    return Result(
                        "MALFORMED_INTAKE_OUTPUT without the required intake-v2 sentinels.");
                }
                _successfulAccountManagerRuns++;
                var status = _successfulAccountManagerRuns == 1 &&
                             !confirmOnFirstAccountManagerRun
                    ? "AwaitingConfirmation"
                    : "Confirmed";
                var kind = _successfulAccountManagerRuns > 1 &&
                           oppositeKindOnConfirmation
                    ? proposedKind == FlowKind.Advisory
                        ? FlowKind.Delivery
                        : FlowKind.Advisory
                    : proposedKind;
                return Result(Intake(status, kind.ToString()));
            }
            if (context.AgentId == "team-lead")
            {
                var plan = proposedKind == FlowKind.Advisory
                    ? AdvisoryPlan()
                    : DeliveryPlan();
                return Result(
                    "HANDOFF_STATUS: COMPLETE" +
                    Environment.NewLine +
                    TeamPlanParser.BeginSentinel +
                    Environment.NewLine +
                    TeamPlanParser.Serialize(plan) +
                    Environment.NewLine +
                    TeamPlanParser.EndSentinel);
            }
            if (mutateSource &&
                context.OutcomeContract.Contains(
                    FlowOutcomeParser.BeginSentinel,
                    StringComparison.Ordinal))
            {
                File.WriteAllText(
                    Path.Combine(workspacePath, "unauthorized-change.txt"),
                    "changed");
            }

            var output = """
                HANDOFF_STATUS: COMPLETE

                ## Decision
                Complete.

                ## Deliverable
                The assigned work is complete.

                ## Evidence
                Repository evidence was inspected.

                ## Next owner
                Continue the plan.
                """;
            if (!omitOutcome &&
                context.OutcomeContract.Contains(
                    FlowOutcomeParser.BeginSentinel,
                    StringComparison.Ordinal))
            {
                var artifacts = proposedKind == FlowKind.Advisory
                    ? """[{"Path":"recommendation.md","MediaType":"text/markdown","Content":"# Recommendation\nUse idempotency keys."}]"""
                    : "[]";
                output += Environment.NewLine + Outcome(artifacts);
            }
            return Result(output);
        }

        private static Task<AgentExecutionResult> Result(string output) =>
            Task.FromResult(new AgentExecutionResult(
                output,
                "Fixture evidence.",
                1,
                []));

        private static TeamPlanDocument AdvisoryPlan() =>
            new()
            {
                Version = TeamPlanParser.Version,
                Disposition = TeamPlanDisposition.Planned,
                Steps =
                [
                    Step(
                        "prepare",
                        "advisor",
                        10,
                        [PlanDuty.Analyze, PlanDuty.PrepareOutcome],
                        owner: true)
                ],
                PreMortemCheckpoints = [],
                MissingQualification = null
            };

        private static TeamPlanDocument DeliveryPlan() =>
            new()
            {
                Version = TeamPlanParser.Version,
                Disposition = TeamPlanDisposition.Planned,
                Steps =
                [
                    Step(
                        "prepare",
                        "builder",
                        10,
                        [
                            PlanDuty.Implement,
                            PlanDuty.Verify,
                            PlanDuty.PrepareOutcome
                        ],
                        owner: true),
                    Step(
                        "publish",
                        "publisher",
                        20,
                        [PlanDuty.Publish],
                        owner: false,
                        stage: PlanStage.AfterApproval,
                        dependencies: ["prepare"],
                        emptyProfile: true)
                ],
                PreMortemCheckpoints = [],
                MissingQualification = null
            };

        private static TeamPlanStep Step(
            string id,
            string agentId,
            int order,
            IReadOnlyList<PlanDuty> duties,
            bool owner,
            PlanStage stage = PlanStage.BeforeReview,
            IReadOnlyList<string>? dependencies = null,
            bool emptyProfile = false) =>
            new()
            {
                Id = id,
                AgentId = agentId,
                Order = order,
                Stage = stage,
                Assignment = $"Complete {id}.",
                Justification = $"{agentId} is the smallest suitable owner.",
                DependsOn = dependencies ?? [],
                Duties = duties,
                OutcomeOwner = owner,
                TaskProfile = emptyProfile
                    ? new TeamPlanTaskProfile()
                    : new TeamPlanTaskProfile
                    {
                        Complexity = 4,
                        ReasoningDepth = 5,
                        ContextDemand = 5,
                        ToolIntensity = 3,
                        TaskTypeTags = [TaskTypeTag.CrossCutting],
                        Risk = TaskRisk.Medium,
                        RiskReason = "The result requires repository evidence.",
                        Confidence = 0.8,
                        Rationales = ["A focused specialist is sufficient."]
                    }
            };
    }

    private sealed class StudioWorkspaceManager(string path) : IWorkspaceManager
    {
        public Task<WorkspaceInfo> PrepareAsync(
            FlowRun flow,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkspaceInfo(
                path,
                string.Empty,
                CreatedNow: false,
                Mode: flow.Status == FlowStatus.Intake
                    ? WorkspaceMode.ProvisionalReadOnly
                    : flow.Kind == FlowKind.Advisory
                        ? WorkspaceMode.AdvisoryReadOnly
                        : WorkspaceMode.Delivery));
    }

    private sealed class StubReviewedCandidateService
        : IReviewedCandidateService
    {
        public Task<ReviewedCandidateIdentity> SealAsync(
            FlowRun flow,
            Guid outcomeOwnerStepId,
            string outcomeOwnerPlanStepKey,
            string outcomeContractJson,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewedCandidateIdentity(
                ReviewedCandidateIdentity.CurrentVersion,
                flow.Id,
                flow.Iteration,
                outcomeOwnerStepId,
                outcomeOwnerPlanStepKey,
                OutcomeVerificationRules.ComputeSha256(outcomeContractJson),
                OutcomeVerificationRules.ComputeSha256(
                    $"intake-plan:{flow.Id:D}:{flow.Iteration}"),
                OutcomeVerificationRules.ComputeSha256(
                    $"intake-candidate:{flow.Id:D}:{flow.Iteration}"),
                0,
                0,
                0,
                0,
                [
                    new ReviewedCandidateRepositoryIdentity(
                        ".",
                        new string('1', 40),
                        new string('2', 40),
                        "example/repository")
                ],
                DateTimeOffset.UtcNow));

        public Task<OutcomeCandidateSnapshot> VerifyAsync(
            FlowRun flow,
            ReviewedCandidateIdentity identity,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(
                "This intake fixture never launches publication.");
    }

    public sealed class StudioDbContextFactory(
        DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);

        public Task<HarnessDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HarnessDbContext(options));
    }
}

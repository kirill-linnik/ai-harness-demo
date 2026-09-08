using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Data;
using AiHarnessDemo.Infrastructure;
using AiHarnessDemo.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiHarnessDemo.Tests;

public sealed class DynamicPlanningTests
{
    private readonly TeamPlanValidator _validator = new();

    [Fact]
    public void Parser_RejectsUnknownFieldsWrongEnumCasingAndConfiguredLimits()
    {
        var valid = TeamPlanParser.Serialize(AdvisoryPlan(
            Step(
                "answer",
                "research-generalist",
                10,
                [PlanDuty.Analyze, PlanDuty.PrepareOutcome],
                owner: true)));
        var unknown = valid.Replace(
            "\"Version\":",
            "\"Unexpected\":true,\"Version\":",
            StringComparison.Ordinal);
        Assert.Throws<TeamPlanContractException>(
            () => TeamPlanParser.Parse(WrapPlan(unknown)));
        Assert.Throws<TeamPlanContractException>(
            () => TeamPlanParser.Parse(
                WrapPlan(valid.Replace(
                    "\"BeforeReview\"",
                    "\"beforeReview\"",
                    StringComparison.Ordinal))));

        var oversized = AdvisoryPlan(
            Step(
                "answer",
                "research-generalist",
                10,
                [PlanDuty.PrepareOutcome],
                owner: true,
                assignment: new string('x', 11)));
        var exception = Assert.Throws<TeamPlanContractException>(() =>
            _validator.Validate(
                oversized,
                Context(
                    FlowKind.Advisory,
                    [Snapshot("research-generalist")],
                    maximumAssignmentCharacters: 10)));
        Assert.Contains(
            exception.Errors,
            error => error.Contains("assignment", StringComparison.Ordinal));
    }

    [Fact]
    public void OutcomeParser_RejectsUnknownFieldsTraversalAndArtifactByteOverflow()
    {
        var unknown = """
            FLOW_OUTCOME_V1_BEGIN
            {"Version":"flow-outcome-v1","Goal":"Assess","Summary":"Done","ImplementationDetails":["Use the result."],"Artifacts":[],"Unknown":true}
            FLOW_OUTCOME_V1_END
            """;
        Assert.Throws<FlowOutcomeContractException>(
            () => FlowOutcomeParser.Parse(unknown));

        var traversal = """
            FLOW_OUTCOME_V1_BEGIN
            {"Version":"flow-outcome-v1","Goal":"Assess","Summary":"Done","ImplementationDetails":["Use the result."],"Artifacts":[{"Path":"..\\secret.md","MediaType":"text/markdown","Content":"text"}]}
            FLOW_OUTCOME_V1_END
            """;
        Assert.Throws<FlowOutcomeContractException>(
            () => FlowOutcomeParser.Parse(traversal));

        var bytes = """
            FLOW_OUTCOME_V1_BEGIN
            {"Version":"flow-outcome-v1","Goal":"Assess","Summary":"Done","ImplementationDetails":["Use the result."],"Artifacts":[{"Path":"answer.md","MediaType":"text/markdown","Content":"éé"}]}
            FLOW_OUTCOME_V1_END
            """;
        Assert.Throws<FlowOutcomeContractException>(
            () => FlowOutcomeParser.Parse(bytes, maximumArtifactCount: 1, maximumTotalArtifactBytes: 3));
    }

    [Fact]
    public void Validator_AllowsArbitraryIdsSubsetAndRepeatedAgents()
    {
        var document = AdvisoryPlan(
            Step(
                "inspect",
                "domain-investigator",
                10,
                [PlanDuty.Analyze]),
            Step(
                "recommend",
                "domain-investigator",
                20,
                [PlanDuty.Design, PlanDuty.PrepareOutcome],
                owner: true,
                dependencies: ["inspect"]));

        var result = _validator.Validate(
            document,
            Context(
                FlowKind.Advisory,
                [
                    Snapshot("domain-investigator"),
                    Snapshot("unused-specialist")
                ]));

        Assert.Equal(["inspect", "recommend"], result.OrderedSteps.Select(step => step.Id));
        Assert.All(result.OrderedSteps, step => Assert.Equal("domain-investigator", step.AgentId));
    }

    [Theory]
    [InlineData("unknown-agent", true)]
    [InlineData("disabled-specialist", false)]
    [InlineData("team-lead", true)]
    [InlineData("account-manager", true)]
    [InlineData("pre-mortem-sceptic", true)]
    public void Validator_RejectsUnknownDisabledAndCoreWorkers(
        string selectedAgent,
        bool enabled)
    {
        var snapshots = new List<FlowAgentSnapshot>
        {
            Snapshot("enabled-specialist"),
            Snapshot("disabled-specialist", enabled: enabled),
            Snapshot("team-lead"),
            Snapshot("account-manager"),
            Snapshot("pre-mortem-sceptic")
        };
        if (selectedAgent == "unknown-agent")
        {
            snapshots.RemoveAll(item => item.AgentId == selectedAgent);
        }

        Assert.Throws<TeamPlanContractException>(() =>
            _validator.Validate(
                AdvisoryPlan(
                    Step(
                        "answer",
                        selectedAgent,
                        10,
                        [PlanDuty.PrepareOutcome],
                        owner: true)),
                Context(FlowKind.Advisory, snapshots)));
    }

    [Theory]
    [InlineData("Account-Manager")]
    [InlineData("TEAM-LEAD")]
    [InlineData("legacy-account-manager-alias")]
    public void Validator_ReservesWorkersByExactCoreAgentIdOnlyNotByCaseOrRoleVariants(
        string agentId)
    {
        // The snapshot's legacy Role metadata equals a core role ('account-manager'),
        // but the AgentId itself is neither an exact-case core ID nor a canonical
        // core ID at all. Only the exact AgentId values ('account-manager',
        // 'team-lead', 'pre-mortem-sceptic') are reserved for workers; arbitrary
        // optional IDs (including case-variants of core IDs and IDs that merely
        // carry a legacy core Role) must remain selectable.
        var snapshots = new List<FlowAgentSnapshot>
        {
            Snapshot(agentId, role: "account-manager"),
            Snapshot("team-lead"),
            Snapshot("account-manager"),
            Snapshot("pre-mortem-sceptic")
        };

        var result = _validator.Validate(
            AdvisoryPlan(
                Step(
                    "answer",
                    agentId,
                    10,
                    [PlanDuty.PrepareOutcome],
                    owner: true)),
            Context(FlowKind.Advisory, snapshots));

        Assert.Equal(agentId, Assert.Single(result.OrderedSteps).AgentId);
    }

    [Theory]
    [InlineData("account-manager")]
    [InlineData("team-lead")]
    [InlineData("pre-mortem-sceptic")]
    public void Validator_RejectsExactCoreAgentIdEvenWithoutMatchingRoleMetadata(
        string coreAgentId)
    {
        // Reservation is keyed strictly on the exact core AgentId value, regardless
        // of what Role metadata a snapshot happens to carry.
        var snapshots = new List<FlowAgentSnapshot>
        {
            Snapshot(coreAgentId, role: "some-unrelated-legacy-role"),
            Snapshot("helper-specialist")
        };

        var exception = Assert.Throws<TeamPlanContractException>(() =>
            _validator.Validate(
                AdvisoryPlan(
                    Step(
                        "answer",
                        coreAgentId,
                        10,
                        [PlanDuty.PrepareOutcome],
                        owner: true)),
                Context(FlowKind.Advisory, snapshots)));

        Assert.Contains(
            exception.Errors,
            error => error.Contains("core agent", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsDuplicateForwardAndCyclicDependencies()
    {
        var snapshots = new[] { Snapshot("generalist") };
        var duplicate = AdvisoryPlan(
            Step("same", "generalist", 10, [PlanDuty.Analyze]),
            Step("same", "generalist", 20, [PlanDuty.PrepareOutcome], owner: true));
        Assert.Throws<TeamPlanContractException>(
            () => _validator.Validate(
                duplicate,
                Context(FlowKind.Advisory, snapshots)));

        var forward = AdvisoryPlan(
            Step(
                "first",
                "generalist",
                10,
                [PlanDuty.Analyze],
                dependencies: ["second"]),
            Step(
                "second",
                "generalist",
                20,
                [PlanDuty.PrepareOutcome],
                owner: true));
        Assert.Throws<TeamPlanContractException>(
            () => _validator.Validate(
                forward,
                Context(FlowKind.Advisory, snapshots)));

        var cycle = AdvisoryPlan(
            Step(
                "first",
                "generalist",
                10,
                [PlanDuty.Analyze],
                dependencies: ["second"]),
            Step(
                "second",
                "generalist",
                20,
                [PlanDuty.PrepareOutcome],
                owner: true,
                dependencies: ["first"]));
        var exception = Assert.Throws<TeamPlanContractException>(
            () => _validator.Validate(
                cycle,
                Context(FlowKind.Advisory, snapshots)));
        Assert.Contains(
            exception.Errors,
            error => error.Contains("acyclic", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("team-plan")]
    [InlineData("account-manager:refinement")]
    [InlineData("account-manager:qualification-blocker")]
    [InlineData("pre-mortem:answer:1")]
    public void Validator_RejectsHostReservedPlanStepIds(string planStepId)
    {
        var exception = Assert.Throws<TeamPlanContractException>(() =>
            _validator.Validate(
                AdvisoryPlan(
                    Step(
                        planStepId,
                        "generalist",
                        10,
                        [PlanDuty.PrepareOutcome],
                        owner: true)),
                Context(
                    FlowKind.Advisory,
                    [Snapshot("generalist")])));

        Assert.Contains(
            exception.Errors,
            error => error.Contains(
                "reserved",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_RejectsNonCanonicalAndCaseFoldedStepIds()
    {
        var exception = Assert.Throws<TeamPlanContractException>(() =>
            _validator.Validate(
                AdvisoryPlan(
                    Step(
                        "prepare-answer",
                        "generalist",
                        10,
                        [PlanDuty.Analyze]),
                    Step(
                        "Prepare-Answer",
                        "generalist",
                        20,
                        [PlanDuty.PrepareOutcome],
                        owner: true)),
                Context(
                    FlowKind.Advisory,
                    [Snapshot("generalist")])));

        Assert.Contains(
            exception.Errors,
            error => error.Contains(
                "lowercase kebab-case",
                StringComparison.Ordinal));
        Assert.Contains(
            exception.Errors,
            error => error.Contains(
                "must be unique",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_EnforcesSingleFinalOutcomeOwner()
    {
        var snapshots = new[] { Snapshot("generalist") };
        var noOwner = AdvisoryPlan(
            Step("answer", "generalist", 10, [PlanDuty.PrepareOutcome]));
        Assert.Throws<TeamPlanContractException>(
            () => _validator.Validate(
                noOwner,
                Context(FlowKind.Advisory, snapshots)));

        var multiple = AdvisoryPlan(
            Step(
                "first",
                "generalist",
                10,
                [PlanDuty.PrepareOutcome],
                owner: true),
            Step(
                "second",
                "generalist",
                20,
                [PlanDuty.PrepareOutcome],
                owner: true));
        Assert.Throws<TeamPlanContractException>(
            () => _validator.Validate(
                multiple,
                Context(FlowKind.Advisory, snapshots)));

        var notFinal = AdvisoryPlan(
            Step(
                "answer",
                "generalist",
                10,
                [PlanDuty.PrepareOutcome],
                owner: true),
            Step("later", "generalist", 20, [PlanDuty.Analyze]));
        Assert.Throws<TeamPlanContractException>(
            () => _validator.Validate(
                notFinal,
                Context(FlowKind.Advisory, snapshots)));
    }

    [Fact]
    public void Validator_EnforcesAdvisoryAndDeliveryDutiesAndStages()
    {
        var snapshots = new[] { Snapshot("generalist"), Snapshot("publisher") };
        var advisoryImplementation = AdvisoryPlan(
            Step(
                "answer",
                "generalist",
                10,
                [PlanDuty.Implement, PlanDuty.PrepareOutcome],
                owner: true));
        Assert.Throws<TeamPlanContractException>(
            () => _validator.Validate(
                advisoryImplementation,
                Context(FlowKind.Advisory, snapshots)));

        var noPublish = new TeamPlanDocument
        {
            Version = TeamPlanParser.Version,
            Disposition = TeamPlanDisposition.Planned,
            Steps =
            [
                Step(
                    "implement",
                    "generalist",
                    10,
                    [PlanDuty.Implement, PlanDuty.Verify, PlanDuty.PrepareOutcome],
                    owner: true)
            ],
            PreMortemCheckpoints = [],
            MissingQualification = null
        };
        Assert.Throws<TeamPlanContractException>(
            () => _validator.Validate(
                noPublish,
                Context(FlowKind.Delivery, snapshots)));

        var valid = DeliveryPlan();
        var result = _validator.Validate(
            valid,
            Context(FlowKind.Delivery, snapshots));
        Assert.Equal("publish", result.PublicationStep?.Id);
        Assert.Equal(PlanStage.AfterApproval, result.PublicationStep?.Stage);

        var stricterContext = Context(FlowKind.Delivery, snapshots) with
        {
            RequiredDuties =
            [
                PlanDuty.Design,
                PlanDuty.Implement,
                PlanDuty.Verify,
                PlanDuty.PrepareOutcome,
                PlanDuty.Publish
            ]
        };
        Assert.Throws<TeamPlanContractException>(
            () => _validator.Validate(valid, stricterContext));
    }

    [Fact]
    public void Validator_UsesPlanStepKeysForPreMortemAndRejectsDisabledSnapshot()
    {
        var document = AdvisoryPlan(
            Step(
                "inspect",
                "generalist",
                10,
                [PlanDuty.Analyze, PlanDuty.PrepareOutcome],
                owner: true)).WithCheckpoints(["inspect"]);

        var enabled = _validator.Validate(
            document,
            Context(
                FlowKind.Advisory,
                [Snapshot("generalist"), Snapshot("pre-mortem-sceptic")],
                preMortemEnabled: true));
        Assert.Equal(["inspect"], enabled.Document.PreMortemCheckpoints);

        Assert.Throws<TeamPlanContractException>(() =>
            _validator.Validate(
                document,
                Context(
                    FlowKind.Advisory,
                    [
                        Snapshot("generalist"),
                        Snapshot("pre-mortem-sceptic", enabled: false)
                    ],
                    preMortemEnabled: false)));
    }

    [Fact]
    public async Task StudioV2_ManualPreMortemRestartKeepsOneCanonicalRoot()
    {
        var plan = AdvisoryPlan(
                    Step(
                        "answer",
                        "generalist",
                        10,
                        [PlanDuty.Analyze, PlanDuty.PrepareOutcome],
                        owner: true))
            .WithCheckpoints(["answer"]);
        await using var harness = await DynamicHarness.CreateAsync(
            FlowKind.Advisory,
            plan,
            [
                    Snapshot("generalist"),
                    Snapshot("pre-mortem-sceptic")
            ],
            modelRouter: new SeparatedModelRouter());
        harness.Runner.FailFirstPreMortem = true;

        await harness.Engine.RunAsync(
            harness.FlowId,
            CancellationToken.None);
        await harness.Engine.RestartFailedFlowAsync(
            harness.FlowId,
            CancellationToken.None);
        await harness.Engine.RunAsync(
            harness.FlowId,
            CancellationToken.None);

        await using var database =
            await harness.Factory.CreateDbContextAsync();
        var attempts = await database.FlowSteps
            .Where(step =>
                    step.FlowRunId == harness.FlowId &&
                    step.PlanStepKey == "pre-mortem:answer:1")
            .OrderBy(step => step.Sequence)
            .ToListAsync();
        Assert.Equal(2, attempts.Count);
        var root = Assert.Single(
            attempts,
            step => step.RetryOfStepId is null);
        var retry = Assert.Single(
            attempts,
            step => step.RetryOfStepId == root.Id);
        Assert.Equal(root.Id, root.StableSemanticRootId);
        Assert.Equal(root.Id, retry.StableSemanticRootId);
        Assert.Equal(root.Attempt, retry.Attempt);
        var flow = await database.Flows
            .Include(item => item.Events)
            .SingleAsync(item => item.Id == harness.FlowId);
        Assert.True(
            retry.Status == StepStatus.Completed,
            flow.FailureReason + Environment.NewLine +
            string.Join(
                Environment.NewLine,
                flow.Events.Select(item => item.Message)));
    }

    [Fact]
    public async Task StudioV2_MaterializesOnlyPreReviewStepsAndUsesDistinctRepeatedAgentSessions()
    {
        var plan = DeliveryPlan();
        await using var harness = await DynamicHarness.CreateAsync(
            FlowKind.Delivery,
            plan,
            [
                Snapshot("generalist", name: "Generalist", description: "Does delivery work."),
                Snapshot("publisher", name: "Publisher", description: "Publishes approved work."),
                Snapshot("unused-specialist", name: "Unused", description: "Not needed."),
                Snapshot("disabled-specialist", enabled: false, name: "Disabled", description: "Disabled.")
            ]);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        await using var database = await harness.Factory.CreateDbContextAsync();
        var flow = await database.Flows.SingleAsync(item => item.Id == harness.FlowId);
        var steps = await database.FlowSteps
            .Where(item => item.FlowRunId == harness.FlowId)
            .OrderBy(item => item.Sequence)
            .ToListAsync();
        var profiles = await database.TaskProfiles
            .Where(item => item.FlowRunId == harness.FlowId)
            .ToListAsync();

        Assert.True(
            flow.Status == FlowStatus.WaitingForFeedback,
            $"Expected WaitingForFeedback, got {flow.Status}: {flow.FailureReason}");
        Assert.Equal("prepare", flow.OutcomeOwnerPlanStepKey);
        Assert.Equal("publish", flow.PublicationPlanStepKey);
        Assert.Single(await database.FlowPlanDocuments.ToListAsync());
        Assert.DoesNotContain(steps, step => step.PlanStepKey == "publish");
        Assert.DoesNotContain(steps, step => step.AgentId == "software-engineer");
        Assert.Equal(
            ["implement", "verify", "prepare"],
            steps
                .Where(step => step.PlanStepKey is "implement" or "verify" or "prepare")
                .Select(step => step.PlanStepKey));
        Assert.All(
            steps.Where(step => step.PlanStepKey is "implement" or "verify" or "prepare"),
            step =>
            {
                Assert.False(string.IsNullOrWhiteSpace(step.PlanDutiesJson));
                Assert.False(string.IsNullOrWhiteSpace(step.WorkflowRevision));
            });
        Assert.All(
            profiles.Where(profile => profile.PlanStepKey is "implement" or "verify" or "prepare"),
            profile => Assert.Equal("generalist", profile.AgentId));
        var publicationProfile = Assert.Single(
            profiles,
            profile => profile.PlanStepKey == "publish");
        Assert.Equal("publisher", publicationProfile.AgentId);
        Assert.Null(publicationProfile.FlowStepId);

        var generalistRuns = harness.Runner.Contexts
            .Where(context => context.AgentId == "generalist")
            .ToList();
        Assert.Equal(3, generalistRuns.Count);
        Assert.Equal(3, generalistRuns.Select(context => context.CopilotSessionId).Distinct().Count());
        var teamLeadTask = Assert.Single(
            harness.Runner.Contexts,
            context => context.AgentId == "team-lead").Task;
        Assert.Contains("\"Id\": \"unused-specialist\"", teamLeadTask);
        Assert.DoesNotContain("\"Id\": \"disabled-specialist\"", teamLeadTask);
    }

    [Fact]
    public async Task StudioV2_ExecutesOptionalAgentWhenLegacyRoleMatchesCoreRole()
    {
        var plan = AdvisoryPlan(
            Step(
                "answer",
                "independent-reviewer",
                10,
                [PlanDuty.Analyze, PlanDuty.PrepareOutcome],
                owner: true));
        await using var harness = await DynamicHarness.CreateAsync(
            FlowKind.Advisory,
            plan,
            [
                Snapshot(
                    "independent-reviewer",
                    role: "pre-mortem-sceptic")
            ]);

        await harness.Engine.RunAsync(
            harness.FlowId,
            CancellationToken.None);

        await using var database = await harness.Factory.CreateDbContextAsync();
        var flow = await database.Flows.SingleAsync(
            item => item.Id == harness.FlowId);
        var worker = await database.FlowSteps.SingleAsync(
            item =>
                item.FlowRunId == harness.FlowId &&
                item.PlanStepKey == "answer");
        Assert.Equal(FlowStatus.WaitingForFeedback, flow.Status);
        Assert.Equal(StepStatus.Completed, worker.Status);
        Assert.Equal(
            ExecutionInvocationKind.Worker,
            worker.InvocationKind);
        Assert.Equal("pre-mortem-sceptic", worker.AgentRole);
        Assert.Contains(
            harness.Runner.Contexts,
            context =>
                context.AgentId == "independent-reviewer" &&
                context.InvocationKind == ExecutionInvocationKind.Worker);
    }

    [Fact]
    public async Task StudioV2_PendingWorkersBindCurrentWorkflowAtTheirFirstLaunch()
    {
        await using var harness = await DynamicHarness.CreateAsync(
            FlowKind.Delivery,
            DeliveryPlan(),
            [
                Snapshot("generalist"),
                Snapshot("publisher")
            ]);
        string? initialRevision = null;
        harness.Runner.AfterResult = async context =>
        {
            if (context.PlanStepKey != "implement")
            {
                return;
            }
            initialRevision = harness.WorkflowProvider
                .GetEffective()
                .Revision;
            await harness.ReloadWorkflowPromptAsync();
        };

        await harness.Engine.RunAsync(
            harness.FlowId,
            CancellationToken.None);

        await using var database =
            await harness.Factory.CreateDbContextAsync();
        var workers = await database.FlowSteps
            .Where(step =>
                step.FlowRunId == harness.FlowId &&
                (step.PlanStepKey == "implement" ||
                 step.PlanStepKey == "verify" ||
                 step.PlanStepKey == "prepare"))
            .OrderBy(step => step.Sequence)
            .ToListAsync();
        Assert.NotNull(initialRevision);
        Assert.Equal(initialRevision, workers[0].WorkflowRevision);
        Assert.NotEqual(
            initialRevision,
            workers[1].WorkflowRevision);
        Assert.Equal(
            workers[1].WorkflowRevision,
            workers[2].WorkflowRevision);
        Assert.All(
            workers,
            step => Assert.False(string.IsNullOrWhiteSpace(
                step.EffectivePermissionJson)));
        Assert.Equal(
            3,
            harness.Runner.Contexts.Count(context =>
                context.AgentId == "generalist"));
    }

    [Fact]
    public async Task StudioV2_InvalidPlanGetsOneSameSessionCorrectionWithoutDuplicates()
    {
        var plan = AdvisoryPlan(
            Step(
                "answer",
                "generalist",
                10,
                [PlanDuty.Analyze, PlanDuty.PrepareOutcome],
                owner: true));
        await using var harness = await DynamicHarness.CreateAsync(
            FlowKind.Advisory,
            plan,
            [Snapshot("generalist")],
            invalidFirstPlan: true);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        await using var database = await harness.Factory.CreateDbContextAsync();
        var leadSteps = await database.FlowSteps
            .Where(step =>
                step.FlowRunId == harness.FlowId &&
                step.PlanStepKey == WorkflowEngine.TeamLeadPlanStepKey)
            .OrderBy(step => step.Attempt)
            .ToListAsync();
        Assert.Equal(2, leadSteps.Count);
        Assert.Equal(
            leadSteps[0].CopilotSessionId,
            leadSteps[1].CopilotSessionId);
        Assert.Single(await database.FlowPlanDocuments.ToListAsync());
        Assert.Single(await database.FlowSteps
            .Where(step =>
                step.FlowRunId == harness.FlowId &&
                step.PlanStepKey == "answer" &&
                step.RetryOfStepId == null)
            .ToListAsync());

        var flow = await database.Flows.SingleAsync(item => item.Id == harness.FlowId);
        flow.Status = FlowStatus.Queued;
        await database.SaveChangesAsync();
        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);
        database.ChangeTracker.Clear();
        Assert.Single(await database.FlowPlanDocuments.ToListAsync());
        Assert.Single(await database.FlowSteps
            .Where(step =>
                step.FlowRunId == harness.FlowId &&
                step.PlanStepKey == "answer" &&
                step.RetryOfStepId == null)
            .ToListAsync());
    }

    [Fact]
    public async Task StudioV2_MissingQualificationBlocksAfterCustomerSafeAccountManagerStep()
    {
        var missing = new TeamPlanDocument
        {
            Version = TeamPlanParser.Version,
            Disposition = TeamPlanDisposition.MissingQualification,
            Steps = [],
            PreMortemCheckpoints = [],
            MissingQualification = new MissingQualification
            {
                Summary = "No enabled agent can assess the required rules safely.",
                Missing = ["Evidence-based jurisdictional analysis"],
                WhyRequired = "Incorrect advice would create customer risk.",
                SuggestedAgent = new SuggestedAgent
                {
                    Id = "compliance-analyst",
                    Name = "Compliance Analyst",
                    Description = "Assesses jurisdiction-specific requirements."
                }
            }
        };
        await using var harness = await DynamicHarness.CreateAsync(
            FlowKind.Advisory,
            missing,
            [Snapshot("generalist")]);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        await using var database = await harness.Factory.CreateDbContextAsync();
        Assert.Single(await database.FlowPlanDocuments.ToListAsync());
        var steps = await database.FlowSteps
            .Where(step => step.FlowRunId == harness.FlowId)
            .ToListAsync();
        Assert.Contains(
            steps,
            step => step.PlanStepKey == WorkflowEngine.TeamLeadPlanStepKey);
        var explanation = Assert.Single(
            steps,
            step =>
                step.PlanStepKey ==
                MissingQualificationCoordinator.AccountManagerPlanStepKey);
        Assert.Equal(
            ExecutionPermissionProfile.ReadOnlySource,
            explanation.PermissionProfile);
        var flow = await database.Flows.SingleAsync(item => item.Id == harness.FlowId);
        Assert.Equal(FlowStatus.Blocked, flow.Status);
        Assert.Equal(
            MissingQualificationCoordinator.BlockerCode,
            flow.CurrentBlockerCode);
        Assert.NotEqual(flow.CurrentBlockerSummary, flow.CustomerBlockerMessage);
        Assert.Contains(
            await database.FlowEvents.ToListAsync(),
            item => item.Type == "plan.missing-qualification");
    }

    [Fact]
    public async Task StudioV2_PreMortemCheckpointUsesPlanStepKey()
    {
        var plan = AdvisoryPlan(
            Step(
                "recommend",
                "generalist",
                10,
                [PlanDuty.Analyze, PlanDuty.PrepareOutcome],
                owner: true)).WithCheckpoints(["recommend"]);
        await using var harness = await DynamicHarness.CreateAsync(
            FlowKind.Advisory,
            plan,
            [Snapshot("generalist"), Snapshot("pre-mortem-sceptic")]);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        await using var database = await harness.Factory.CreateDbContextAsync();
        var review = await database.FlowSteps.SingleAsync(step =>
            step.FlowRunId == harness.FlowId &&
            step.AgentId == "pre-mortem-sceptic");
        Assert.Equal("pre-mortem:recommend:1", review.PlanStepKey);
        Assert.Equal(PlanStage.BeforeReview, review.PlanStage);
        Assert.Equal(
            ExecutionPermissionProfile.PreMortemReadOnly,
            review.PermissionProfile);
        var profile = await database.TaskProfiles.SingleAsync(
            item => item.FlowStepId == review.Id);
        Assert.Equal(review.PlanStepKey, profile.PlanStepKey);
        Assert.Equal("pre-mortem-sceptic", profile.AgentId);
    }

    [Fact]
    public async Task PersistedCheckpointPlan_RemainsValidWhenRoundBudgetBecomesZero()
    {
        var plan = AdvisoryPlan(
            Step(
                "inspect",
                "generalist",
                10,
                [PlanDuty.Analyze]),
            Step(
                "recommend",
                "generalist",
                20,
                [PlanDuty.PrepareOutcome],
                owner: true,
                dependencies: ["inspect"])).WithCheckpoints(["inspect"]);
        await using var harness = await DynamicHarness.CreateAsync(
            FlowKind.Advisory,
            plan,
            [Snapshot("generalist"), Snapshot("pre-mortem-sceptic")]);
        var rawPlan = TeamPlanParser.Serialize(plan);
        await using (var database =
                     await harness.Factory.CreateDbContextAsync())
        {
            database.FlowSteps.Add(new FlowStep
            {
                FlowRunId = harness.FlowId,
                Iteration = 1,
                Sequence = 10,
                AgentId = "team-lead",
                AgentName = "Team Lead",
                AgentRole = "team-lead",
                Label = "Select the downstream team",
                PlanStepKey = WorkflowEngine.TeamLeadPlanStepKey,
                PlanDutiesJson = """["Analyze","Design"]""",
                PlanStage = PlanStage.BeforeReview,
                InvocationKind = ExecutionInvocationKind.Planning,
                PermissionProfile =
                    ExecutionPermissionProfile.ReadOnlySource,
                Status = StepStatus.Completed,
                Phase = AgentRunPhase.Succeeded,
                OutputSummary =
                    $"{TeamPlanParser.BeginSentinel}{Environment.NewLine}" +
                    rawPlan +
                    $"{Environment.NewLine}{TeamPlanParser.EndSentinel}",
                CompletedAt = DateTimeOffset.UtcNow
            });
            database.FlowPlanDocuments.Add(new FlowPlanDocument
            {
                FlowRunId = harness.FlowId,
                Iteration = 1,
                Version = TeamPlanParser.Version,
                Disposition = TeamPlanDisposition.Planned.ToString(),
                RawJson = rawPlan
            });
            var settings = await database.Settings.SingleAsync();
            settings.MaxHandoffRetries = 0;
            await database.SaveChangesAsync();
        }

        await harness.Engine.RunAsync(
            harness.FlowId,
            CancellationToken.None);

        await using var check = await harness.Factory.CreateDbContextAsync();
        var flow = await check.Flows.SingleAsync(item =>
            item.Id == harness.FlowId);
        var storedPlans = await check.FlowPlanDocuments
            .Where(item => item.FlowRunId == harness.FlowId)
            .ToListAsync();
        var checkpoint = await check.FlowSteps.SingleAsync(step =>
            step.FlowRunId == harness.FlowId &&
            step.PlanStepKey == "pre-mortem:inspect:1");
        var inspected = await check.FlowSteps.SingleAsync(step =>
            step.FlowRunId == harness.FlowId &&
            step.PlanStepKey == "inspect");
        var recommendation = await check.FlowSteps.SingleAsync(step =>
            step.FlowRunId == harness.FlowId &&
            step.PlanStepKey == "recommend");
        Assert.Equal(FlowStatus.WaitingForFeedback, flow.Status);
        Assert.Equal(StepStatus.Skipped, checkpoint.Status);
        Assert.Equal(inspected.Id, checkpoint.PreMortemTargetStepId);
        Assert.Equal(inspected.Id, recommendation.DependsOnStepId);
        Assert.Equal(StepStatus.Completed, recommendation.Status);
        Assert.Single(storedPlans);
        Assert.Equal(rawPlan, storedPlans[0].RawJson);
        Assert.DoesNotContain(
            harness.Runner.Contexts,
            context =>
                context.InvocationKind ==
                ExecutionInvocationKind.PreMortem);
        Assert.DoesNotContain(
            harness.Runner.Contexts,
            context => context.AgentId == "team-lead");
        Assert.Single(
            harness.Runner.Contexts,
            context => context.FlowStepId == inspected.Id);
        Assert.Single(
            harness.Runner.Contexts,
            context => context.FlowStepId == recommendation.Id);
        Assert.Single(
            await check.FlowEvents
                .Where(item =>
                    item.FlowRunId == harness.FlowId &&
                    item.Type == "premortem.review-disabled")
                .ToListAsync());
        Assert.DoesNotContain(
            await check.FlowEvents
                .Where(item => item.FlowRunId == harness.FlowId)
                .ToListAsync(),
            item => item.Type.Contains(
                "validation-failed",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task StudioV2_DynamicPushbackTargetsAncestorByPlanStepIdentity()
    {
        var plan = AdvisoryPlan(
            Step("evidence", "generalist", 10, [PlanDuty.Analyze]),
            Step(
                "answer",
                "reviewer",
                20,
                [PlanDuty.PrepareOutcome],
                owner: true,
                dependencies: ["evidence"]));
        await using var harness = await DynamicHarness.CreateAsync(
            FlowKind.Advisory,
            plan,
            [Snapshot("generalist"), Snapshot("reviewer")],
            pushbackOwner: "evidence");

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        await using var database = await harness.Factory.CreateDbContextAsync();
        var flow = await database.Flows.SingleAsync(item => item.Id == harness.FlowId);
        var revisions = await database.FlowSteps
            .Where(step =>
                step.FlowRunId == harness.FlowId &&
                step.PlanStepKey == "evidence")
            .OrderBy(step => step.Sequence)
            .ToListAsync();
        Assert.True(
            flow.Status == FlowStatus.WaitingForFeedback,
            $"Expected WaitingForFeedback, got {flow.Status}: {flow.FailureReason}");
        Assert.Equal(2, revisions.Count);
        Assert.Equal(revisions[0].CopilotSessionId, revisions[1].CopilotSessionId);
    }

    [Fact]
    public async Task StudioV2_OutcomeOwnerReceivesEveryDirectDependencyEffectiveAttempt()
    {
        var plan = AdvisoryPlan(
            Step("research-a", "generalist", 10, [PlanDuty.Analyze]),
            Step("research-b", "generalist", 20, [PlanDuty.Analyze]),
            Step("research-c", "generalist", 30, [PlanDuty.Analyze]),
            Step("unrelated-recent", "generalist", 35, [PlanDuty.Analyze]),
            Step(
                "answer",
                "reviewer",
                40,
                [PlanDuty.PrepareOutcome],
                owner: true,
                dependencies:
                [
                    "research-a",
                    "research-b",
                    "research-c"
                ]));
        await using var harness = await DynamicHarness.CreateAsync(
            FlowKind.Advisory,
            plan,
            [Snapshot("generalist"), Snapshot("reviewer")],
            pushbackOwner: "research-a");

        await harness.Engine.RunAsync(
            harness.FlowId,
            CancellationToken.None);

        var outcomeContext = harness.Runner.Contexts
            .Where(context =>
                context.AgentId == "reviewer" &&
                context.OutcomeContract.Contains(
                    FlowOutcomeParser.BeginSentinel,
                    StringComparison.Ordinal))
            .Last();
        var dependencies =
            Assert.IsAssignableFrom<IReadOnlyList<StudioDependencyOutput>>(
                outcomeContext.StudioDependencyOutputs);
        Assert.Equal(
            ["research-a", "research-b", "research-c"],
            dependencies
                .Where(item => item.Kind == StudioDependencyKind.Direct)
                .Select(item => item.PlanStepKey));
        Assert.All(
            dependencies.Where(item =>
                item.Kind == StudioDependencyKind.Direct),
            item => Assert.False(string.IsNullOrWhiteSpace(item.Output)));
        var retried = Assert.Single(
            dependencies,
            item => item.PlanStepKey == "research-a");
        Assert.Equal(2, retried.Attempt);
        Assert.Contains(
            "attempt 2",
            retried.Output,
            StringComparison.Ordinal);
        Assert.Contains(
            "attempt 1",
            Assert.Single(dependencies, item =>
                item.PlanStepKey == "research-b").Output,
            StringComparison.Ordinal);
        Assert.Contains(
            "attempt 1",
            Assert.Single(dependencies, item =>
                item.PlanStepKey == "research-c").Output,
            StringComparison.Ordinal);
        Assert.Empty(outcomeContext.PreviousOutputs);
        Assert.DoesNotContain(
            dependencies,
            item => item.PlanStepKey == "unrelated-recent");

        await using var database =
            await harness.Factory.CreateDbContextAsync();
        var workerSteps = await database.FlowSteps
            .Where(step =>
                step.FlowRunId == harness.FlowId &&
                step.InvocationKind ==
                ExecutionInvocationKind.Worker)
            .ToListAsync();
        Assert.All(
            workerSteps,
            step => Assert.Equal(
                ExecutionPermissionProfile.ReadOnlySource,
                step.PermissionProfile));
    }

    [Fact]
    public void StudioDependencyContext_IsDeterministicBoundedAndNeverDropsDirectKeys()
    {
        var direct = Enumerable.Range(1, 8)
            .Select(index => new StudioDependencyOutput(
                $"direct-dependency-{index:D2}-" + new string('k', 90),
                $"agent-{index:D2}",
                StudioDependencyKind.Direct,
                Distance: 1,
                Attempt: index,
                Sequence: index * 10,
                Output: $"output-{index:D2}-" + new string('x', 4_000)))
            .ToList();
        direct.Add(new StudioDependencyOutput(
            "ancestor-should-not-fit",
            "ancestor-agent",
            StudioDependencyKind.Ancestor,
            Distance: 2,
            Attempt: 1,
            Sequence: 1,
            Output: "ancestor output"));

        var bounded = CopilotReasoningHost.FormatStudioDependencyContext(
            direct,
            sourceProjectPath: string.Empty);

        Assert.True(
            bounded.Length <=
            CopilotReasoningHost.MaximumStudioDependencyContextCharacters);
        foreach (var dependency in direct.Where(item =>
                     item.Kind == StudioDependencyKind.Direct))
        {
            Assert.Equal(
                1,
                CountOccurrences(
                    bounded,
                    $"`{dependency.PlanStepKey}`"));
        }
        Assert.Contains(
            "dependency output clipped",
            bounded,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ancestor-should-not-fit",
            bounded,
            StringComparison.Ordinal);

        var shortContext = CopilotReasoningHost.FormatStudioDependencyContext(
            [
                new StudioDependencyOutput(
                    "direct-a",
                    "agent-a",
                    StudioDependencyKind.Direct,
                    1,
                    1,
                    10,
                    "short direct output"),
                new StudioDependencyOutput(
                    "ancestor-a",
                    "agent-a",
                    StudioDependencyKind.Ancestor,
                    2,
                    1,
                    1,
                    "short ancestor output")
            ],
            sourceProjectPath: string.Empty);
        Assert.Contains("`direct-a`", shortContext, StringComparison.Ordinal);
        Assert.Contains("`ancestor-a`", shortContext, StringComparison.Ordinal);

        var resumed = CopilotReasoningHost.RestartContinuationPrompt(
            new string('p', 20_000) +
            Environment.NewLine +
            bounded +
            Environment.NewLine +
            new string('s', 20_000));
        Assert.True(resumed.Length <= 16_000);
        Assert.Contains(
            CopilotReasoningHost.StudioPlanContextBegin,
            resumed,
            StringComparison.Ordinal);
        Assert.Contains(
            CopilotReasoningHost.StudioPlanContextEnd,
            resumed,
            StringComparison.Ordinal);
        foreach (var dependency in direct.Where(item =>
                     item.Kind == StudioDependencyKind.Direct))
        {
            Assert.Contains(
                $"`{dependency.PlanStepKey}`",
                resumed,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task StudioV2_RejectsPushbackTargetThatIsNotAnAncestor()
    {
        var plan = AdvisoryPlan(
            Step("evidence", "generalist", 10, [PlanDuty.Analyze]),
            Step(
                "answer",
                "reviewer",
                20,
                [PlanDuty.PrepareOutcome],
                owner: true,
                dependencies: ["evidence"]));
        await using var harness = await DynamicHarness.CreateAsync(
            FlowKind.Advisory,
            plan,
            [Snapshot("generalist"), Snapshot("reviewer")],
            pushbackOwner: "not-a-step");

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        await using var database = await harness.Factory.CreateDbContextAsync();
        var flow = await database.Flows.SingleAsync(item => item.Id == harness.FlowId);
        Assert.Equal(FlowStatus.Failed, flow.Status);
        Assert.Contains("must be an earlier dependency", flow.FailureReason);
        Assert.DoesNotContain(
            await database.FlowSteps
                .Where(step => step.FlowRunId == harness.FlowId)
                .ToListAsync(),
            step => step.Label.StartsWith("Revision after", StringComparison.Ordinal));
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var start = 0;
        while ((start = value.IndexOf(
                   token,
                   start,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += token.Length;
        }
        return count;
    }

    private static TeamPlanDocument AdvisoryPlan(params TeamPlanStep[] steps) =>
        new()
        {
            Version = TeamPlanParser.Version,
            Disposition = TeamPlanDisposition.Planned,
            Steps = steps,
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
                Step("implement", "generalist", 10, [PlanDuty.Implement]),
                Step(
                    "verify",
                    "generalist",
                    20,
                    [PlanDuty.Verify],
                    dependencies: ["implement"]),
                Step(
                    "prepare",
                    "generalist",
                    30,
                    [PlanDuty.PrepareOutcome],
                    owner: true,
                    dependencies: ["verify"]),
                Step(
                    "publish",
                    "publisher",
                    40,
                    [PlanDuty.Publish],
                    dependencies: ["prepare"],
                    stage: PlanStage.AfterApproval,
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
        bool owner = false,
        IReadOnlyList<string>? dependencies = null,
        PlanStage stage = PlanStage.BeforeReview,
        string assignment = "Complete the bounded assignment.",
        bool emptyProfile = false) =>
        new()
        {
            Id = id,
            AgentId = agentId,
            Order = order,
            Stage = stage,
            Assignment = assignment,
            Justification = "This is the smallest suitable assignment.",
            DependsOn = dependencies ?? [],
            Duties = duties,
            OutcomeOwner = owner,
            TaskProfile = emptyProfile
                ? new TeamPlanTaskProfile()
                : new TeamPlanTaskProfile
                {
                    Complexity = 5,
                    ReasoningDepth = 6,
                    ContextDemand = 5,
                    ToolIntensity = 4,
                    TaskTypeTags = [TaskTypeTag.CrossCutting],
                    Risk = TaskRisk.Medium,
                    RiskReason = "The result crosses a bounded handoff.",
                    Confidence = 0.8,
                    Rationales = ["Repository evidence is required."]
                }
        };

    private static TeamPlanValidationContext Context(
        FlowKind kind,
        IReadOnlyCollection<FlowAgentSnapshot> snapshots,
        bool preMortemEnabled = false,
        int maximumAssignmentCharacters = 4_000) =>
        new(
            kind,
            snapshots,
            preMortemEnabled,
            MaximumSteps: 24,
            MaximumDependenciesPerStep: 8,
            MaximumAssignmentCharacters: maximumAssignmentCharacters,
            RequiredDuties: kind == FlowKind.Advisory
                ? [PlanDuty.PrepareOutcome]
                : [
                    PlanDuty.Implement,
                    PlanDuty.Verify,
                    PlanDuty.PrepareOutcome,
                    PlanDuty.Publish
                ]);

    private static FlowAgentSnapshot Snapshot(
        string id,
        bool enabled = true,
        string? name = null,
        string? description = null,
        string? role = null) =>
        new()
        {
            FlowRunId = Guid.Empty,
            AgentId = id,
            Name = name ?? id,
            Description = description ?? $"Description for {id}.",
            Role = role ?? id,
            Instructions = "Complete the assigned work.",
            DefinitionHash = $"hash-{id}",
            EnabledAtSnapshot = enabled,
            Required = id is
                "account-manager" or "team-lead" or "pre-mortem-sceptic",
            Switchable = id is not ("account-manager" or "team-lead"),
            SourceFileName = $"{id}.agent.md"
        };

    private static string WrapPlan(string json) =>
        $"{TeamPlanParser.BeginSentinel}{Environment.NewLine}" +
        $"{json}{Environment.NewLine}" +
        TeamPlanParser.EndSentinel;

    private sealed class DynamicHarness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly WorkflowDefinitionProvider _workflowProvider;
        private readonly HandoffGateEngine _gate;

        private DynamicHarness(
            string root,
            Guid flowId,
            DynamicDbContextFactory factory,
            WorkflowEngine engine,
            DynamicAgentRunner runner,
            WorkflowDefinitionProvider workflowProvider,
            HandoffGateEngine gate)
        {
            _root = root;
            FlowId = flowId;
            Factory = factory;
            Engine = engine;
            Runner = runner;
            _workflowProvider = workflowProvider;
            _gate = gate;
        }

        public Guid FlowId { get; }

        public DynamicDbContextFactory Factory { get; }

        public WorkflowEngine Engine { get; }

        public DynamicAgentRunner Runner { get; }

        public WorkflowDefinitionProvider WorkflowProvider =>
            _workflowProvider;

        public async Task ReloadWorkflowPromptAsync()
        {
            var path = Path.Combine(_root, "WORKFLOW.md");
            var current = await File.ReadAllTextAsync(path);
            await File.WriteAllTextAsync(
                path,
                current.Replace(
                    "Test {{ agent.name }} on {{ task }}.",
                    "Reloaded {{ agent.name }} for {{ task }}.",
                    StringComparison.Ordinal));
            await _workflowProvider.ReloadAsync();
        }

        public static async Task<DynamicHarness> CreateAsync(
            FlowKind kind,
            TeamPlanDocument plan,
            IReadOnlyCollection<FlowAgentSnapshot> optionalSnapshots,
            bool invalidFirstPlan = false,
            string? pushbackOwner = null,
            IModelRouter? modelRouter = null)
        {
            var root = Path.Combine(
                AppContext.BaseDirectory,
                "dynamic-planning-tests",
                Guid.NewGuid().ToString("N"));
            var agentsDirectory = Path.Combine(root, ".github", "agents");
            var workspacePath = Path.Combine(root, "workspace");
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
                  version: 1
                  planning:
                    max_steps: 24
                    max_dependencies_per_step: 8
                    max_assignment_characters: 4000
                  flow_kinds:
                    advisory:
                      required_duties:
                        - PrepareOutcome
                      maximum_permission: ReadOnlySource
                    delivery:
                      required_duties:
                        - Implement
                        - Verify
                        - PrepareOutcome
                        - Publish
                      pre_review_maximum_permission: WorkspaceWrite
                      post_approval_maximum_permission: Publish
                  advisory:
                    artifact_directory: .studio\advisory
                    max_artifact_count: 8
                    max_total_artifact_bytes: 65536
                  permissions:
                    read_only_source:
                      additional_denied_tools: []
                    workspace_write:
                      additional_denied_tools: []
                    publish:
                      additional_denied_tools: []
                    pre_mortem_read_only:
                      additional_denied_tools: []
                ---

                Test {{ agent.name }} on {{ task }}.
                """);

            var databasePath = Path.Combine(root, "harness.db");
            var options = new DbContextOptionsBuilder<HarnessDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            var factory = new DynamicDbContextFactory(options);
            var flow = new FlowRun
            {
                Title = "Dynamic plan",
                OriginalRequest = "Complete the dynamic plan.",
                ConsolidatedRequest = "Complete the dynamic plan.",
                Kind = kind,
                ContractVersion = "studio-v2",
                Status = FlowStatus.Queued,
                RepositoryPath = root,
                RepositoryKnowledge = "A configured test repository.",
                Outcome = kind == FlowKind.Advisory
                    ? OutcomeType.None
                    : OutcomeType.PullRequest
            };
            var snapshots = new[]
                {
                    Snapshot("account-manager"),
                    Snapshot("team-lead")
                }
                .Concat(optionalSnapshots)
                .GroupBy(item => item.AgentId, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToList();
            foreach (var snapshot in snapshots)
            {
                snapshot.FlowRunId = flow.Id;
            }

            await using (var database = await factory.CreateDbContextAsync())
            {
                await database.Database.EnsureCreatedAsync();
                database.Settings.Add(new HarnessSettings
                {
                    RepositoryPath = root,
                    RepositoryKnowledge = "A configured test repository.",
                    MaxHandoffRetries = 2
                });
                database.Flows.Add(flow);
                database.FlowAgentSnapshots.AddRange(snapshots);
                await database.SaveChangesAsync();
            }

            var paths = new HarnessPaths(root, agentsDirectory, databasePath);
            var workflowProvider = new WorkflowDefinitionProvider(
                paths,
                new WorkflowLoader(),
                NullLogger<WorkflowDefinitionProvider>.Instance);
            _ = workflowProvider.GetEffective();
            var catalog = new AgentCatalog(paths, factory);
            var snapshotService = new FlowAgentSnapshotService(factory, catalog);
            var runner = new DynamicAgentRunner(
                WrapPlan(TeamPlanParser.Serialize(plan)),
                invalidFirstPlan,
                pushbackOwner);
            var gate = new HandoffGateEngine();
            gate.SetTrustLevel(HandoffActionType.Advance, HandoffTrustLevel.Auto);
            gate.SetTrustLevel(
                HandoffActionType.RequestRevision,
                HandoffTrustLevel.Auto);
            gate.SetTrustLevel(HandoffActionType.Release, HandoffTrustLevel.Gated);
            var engine = new WorkflowEngine(
                factory,
                catalog,
                new FlowPlanner(),
                modelRouter ?? new FixedModelRouter(),
                new BootstrapTaskProfileFactory(),
                TestRoutingSupport.Recorder(factory),
                new DynamicWorkspaceManager(workspacePath),
                runner,
                gate,
                new CopilotSessionJournal(),
                workflowProvider,
                NullLogger<WorkflowEngine>.Instance,
                flowAgentSnapshotService: snapshotService,
                teamPlanValidator: new TeamPlanValidator(),
                reviewedCandidateService:
                    new DynamicReviewedCandidateService());
            return new DynamicHarness(
                root,
                flow.Id,
                factory,
                engine,
                runner,
                workflowProvider,
                gate);
        }

        public ValueTask DisposeAsync()
        {
            _workflowProvider.Dispose();
            _gate.Dispose();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // SQLite can briefly retain a handle on Windows; test artifacts are isolated.
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DynamicAgentRunner(
        string validPlan,
        bool invalidFirstPlan,
        string? pushbackOwner) : IAgentRunner
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public List<AgentExecutionContext> Contexts { get; } = [];

        public Func<AgentExecutionContext, Task>? AfterResult { get; set; }

        public bool FailFirstPreMortem { get; set; }

        public async Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            var count = _counts.GetValueOrDefault(context.AgentId) + 1;
            _counts[context.AgentId] = count;
            if (FailFirstPreMortem &&
                context.AgentId == "pre-mortem-sceptic" &&
                count == 1)
            {
                throw new AgentRunException(
                    "Fixture pre-mortem failure.",
                    AgentRunFailureKind.Transient);
            }
            string output;
            if (context.AgentId == "team-lead")
            {
                output = invalidFirstPlan && count == 1
                    ? """
                      HANDOFF_STATUS: COMPLETE
                      TEAM_PLAN_V1_BEGIN
                      {"Version":"team-plan-v1","Disposition":"Planned","Steps":[],"PreMortemCheckpoints":[],"MissingQualification":null,"Unknown":true}
                      TEAM_PLAN_V1_END
                      """
                    : $"HANDOFF_STATUS: COMPLETE{Environment.NewLine}{validPlan}";
            }
            else if (context.AgentId == "account-manager")
            {
                output = """
                    HANDOFF_STATUS: COMPLETE
                    INTAKE_V2_BEGIN
                    {"Version":"intake-v2","Status":"NeedsClarification","FlowKind":null,"TaskTitle":"Review the request","CustomerReply":"The current team needs a safer path before continuing. Please revise the scope or retry after the available expertise is updated.","Brief":{"Goal":"","Details":[],"SuccessCriteria":[],"Constraints":[],"Assumptions":[]}}
                    INTAKE_V2_END
                    """;
            }
            else if (context.AgentId == "pre-mortem-sceptic")
            {
                output = $$"""
                    {{PreMortemRules.ClearStatus}}
                    {{PreMortemRules.FindingsBeginSentinel}}
                    {"Version":"pre-mortem-findings-v1","Findings":[]}
                    {{PreMortemRules.FindingsEndSentinel}}
                    """;
            }
            else if (pushbackOwner is not null &&
                     context.AgentId == "reviewer" &&
                     count == 1)
            {
                output = $"""
                    HANDOFF_STATUS: PUSHBACK
                    PUSHBACK_OWNER_STEP_ID: {pushbackOwner}
                    PUSHBACK_REASON: Repository evidence is incomplete.
                    """;
            }
            else
            {
                output = $$"""
                    HANDOFF_STATUS: COMPLETE

                    ## Decision
                    Complete.

                    ## Deliverable
                    The assigned plan step attempt {{context.Attempt}} is complete.

                    ## Evidence
                    Focused evidence was inspected.

                    ## Next owner
                    Continue the accepted plan.
                    """;
                if (context.OutcomeContract.Contains(
                        FlowOutcomeParser.BeginSentinel,
                        StringComparison.Ordinal))
                {
                    output +=
                        Environment.NewLine +
                        FlowOutcomeParser.BeginSentinel +
                        Environment.NewLine +
                        """
                        {"Version":"flow-outcome-v1","Goal":"Complete the dynamic plan.","Summary":"The planned outcome is ready for review.","ImplementationDetails":["The focused plan-step evidence was consolidated."],"Artifacts":[]}
                        """ +
                        Environment.NewLine +
                        FlowOutcomeParser.EndSentinel;
                }
            }
            var result = new AgentExecutionResult(
                output,
                "Fixture evidence.",
                1,
                []);
            if (AfterResult is not null)
            {
                await AfterResult(context);
            }
            return result;
        }
    }

    private sealed class DynamicReviewedCandidateService
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
                    $"dynamic-plan:{flow.Id:D}:{flow.Iteration}"),
                OutcomeVerificationRules.ComputeSha256(
                    $"dynamic-candidate:{flow.Id:D}:{flow.Iteration}"),
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
                "The dynamic planning fixture never publishes.");
    }

    private sealed class SeparatedModelRouter : IModelRouter
    {
        public Task<RoutingDecision> SelectAsync(
            RoutingRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RoutingDecision
            {
                FlowStepId = request.FlowStepId,
                TaskProfileId = Guid.NewGuid(),
                ModelCatalogSnapshotId = Guid.NewGuid(),
                SelectedModel =
                    request.ExcludedModelFamilies is { Count: > 0 }
                        ? "alternate-model"
                        : "fixture-model",
                SelectedEffort = "fixture-effort",
                Strategy = request.Strategy,
                PredictedQuality = 0.9,
                PredictedAcceptedTimeSeconds = 10,
                PredictedPremiumRequests = 1,
                Confidence = 0.5,
                Uncertainty = 0.5,
                Reason = "Deterministic family-separated routing."
            });
    }

    private sealed class DynamicWorkspaceManager(string path) : IWorkspaceManager
    {
        public Task<WorkspaceInfo> PrepareAsync(
            FlowRun flow,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkspaceInfo(path, "dynamic-test", CreatedNow: false));
    }

    public sealed class DynamicDbContextFactory(
        DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);

        public Task<HarnessDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HarnessDbContext(options));
    }
}

file static class TeamPlanTestExtensions
{
    public static TeamPlanDocument WithCheckpoints(
        this TeamPlanDocument document,
        IReadOnlyList<string> checkpoints) =>
        new()
        {
            Version = document.Version,
            Disposition = document.Disposition,
            Steps = document.Steps,
            PreMortemCheckpoints = checkpoints,
            MissingQualification = document.MissingQualification
        };
}

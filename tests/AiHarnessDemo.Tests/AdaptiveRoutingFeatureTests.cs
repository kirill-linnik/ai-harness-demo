using AiHarnessDemo.Contracts;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Data;
using AiHarnessDemo.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Tests;

public sealed class ModelCatalogDiscoveryTests
{
    [Fact]
    public void ParseConfigOptions_UsesEachModelsAdvertisedEfforts()
    {
        var catalog = ModelCatalogDiscovery.ParseConfigOptions(
            """
            {"jsonrpc":"2.0","id":1,"result":{"configOptions":[
              {"id":"model","currentValue":"model-a","options":[
                {"value":"auto","name":"Auto"},
                {"value":"model-a","name":"A","description":"default","_meta":{"copilot":{"premiumRequestMultiplier":1}}},
                {"value":"model-b","name":"B","_meta":{"copilotEnablement":"disabled"}},
                {"value":"model-c","name":"C","_meta":{"premium_multiplier":"0.5"}}
              ]},
              {"id":"thought_level","currentValue":"medium","options":[
                {"value":"low","name":"Low"},
                {"value":"medium","name":"Medium"}
              ]}
            ]}}
            """,
            """
            {"jsonrpc":"2.0","id":2,"result":{"configOptions":[
              {"id":"model","currentValue":"model-c","options":[
                {"value":"auto","name":"Auto"},
                {"value":"model-a","name":"A","_meta":{"copilot":{"premiumRequestMultiplier":1}}},
                {"value":"model-c","name":"C","_meta":{"premium_multiplier":"0.5"}}
              ]},
              {"id":"thought_level","currentValue":"high","options":[
                {"value":"high","name":"High"},
                {"value":"max","name":"Maximum"}
              ]}
            ]}}
            """);

        Assert.Equal(4, catalog.Candidates.Count);
        Assert.DoesNotContain(catalog.Candidates, item => item.Model == "auto");
        Assert.DoesNotContain(catalog.Candidates, item => item.Model == "model-b");
        Assert.Contains(
            catalog.Candidates,
            item =>
                item.Model == "model-a" &&
                item.Effort == "medium" &&
                item.IsDefaultModel &&
                item.IsDefaultEffort &&
                item.PremiumMultiplier == 1);
        Assert.Contains(
            catalog.Candidates,
            item => item.Model == "model-c" && item.PremiumMultiplier == .5);
        Assert.DoesNotContain(
            catalog.Candidates,
            item => item.Model == "model-c" && item.Effort == "low");
    }

    [Fact]
    public void ParseConfigOptions_UsesModelDefaultWhenEffortIsNotConfigurable()
    {
        var catalog = ModelCatalogDiscovery.ParseConfigOptions(
            """
            {"result":{"configOptions":[
              {"id":"model","currentValue":"explicit-model","options":[
                {"value":"explicit-model","_meta":{"copilotUsage":"0.33x"}}
              ]}
            ]}}
            """);

        var candidate = Assert.Single(catalog.Candidates);
        Assert.Equal("default", candidate.Effort);
        Assert.Equal(.33, candidate.PremiumMultiplier);
    }
}

public sealed class TaskProfileTests
{
    [Fact]
    public void TeamLeadProfiles_ParseStrictSentinelContract()
    {
        var flowId = Guid.NewGuid();
        var profiles = TaskProfileRules.ParseTeamLeadOutput(
            """
            HANDOFF_STATUS: COMPLETE
            TEAM_TASK_PROFILES_BEGIN
            {"Profiles":[{"Role":"software-engineer","Complexity":7,"ReasoningDepth":8,"ContextDemand":6,"ToolIntensity":9,"TaskTypeTags":["Implementation"],"Risk":"High","RiskReason":"The implementation changes a public contract.","Confidence":0.9,"Rationales":["Code and focused validation are required."]}]}
            TEAM_TASK_PROFILES_END
            """,
            ["software-engineer"],
            flowId,
            2);

        var profile = Assert.Single(profiles);
        Assert.Equal(flowId, profile.FlowRunId);
        Assert.Equal(2, profile.Iteration);
        Assert.Equal(TaskRisk.High, profile.Risk);
        Assert.Equal([TaskTypeTag.Implementation], TaskProfileRules.ReadTags(profile));
    }

    [Fact]
    public void TeamLeadProfiles_RejectMissingRoleAndOutOfRangeMetric()
    {
        var exception = Assert.Throws<TaskProfileValidationException>(() =>
            TaskProfileRules.ParseTeamLeadOutput(
                """
                TEAM_TASK_PROFILES_BEGIN
                {"Profiles":[{"Role":"quality-engineer","Complexity":11,"ReasoningDepth":5,"ContextDemand":5,"ToolIntensity":5,"TaskTypeTags":["Quality"],"Risk":"Low","RiskReason":"Validation task.","Confidence":0.5,"Rationales":["Validate."]}]}
                TEAM_TASK_PROFILES_END
                """,
                ["software-engineer"],
                Guid.NewGuid(),
                1));

        Assert.Contains(exception.Errors, error => error.Contains("complexity", StringComparison.Ordinal));
        Assert.Contains(exception.Errors, error => error.Contains("exactly these", StringComparison.Ordinal));
    }

    [Fact]
    public void BootstrapFactory_ProducesBoundedProfileWithoutModelCall()
    {
        var profile = new BootstrapTaskProfileFactory().Create(
            "team-lead",
            "Plan authentication and database migration.",
            Guid.NewGuid(),
            1);

        Assert.InRange(profile.Complexity, 1, 10);
        Assert.Equal(TaskRisk.High, profile.Risk);
        Assert.NotEmpty(TaskProfileRules.ReadRationales(profile));
    }
}

public sealed class PreMortemContractTests
{
    [Fact]
    public void TeamLeadPlan_SelectsOnlyPlannedRoles()
    {
        var roles = PreMortemRules.ParsePlan(
            """
            PRE_MORTEM_PLAN_BEGIN
            {"AfterRoles":["architect","software-engineer"]}
            PRE_MORTEM_PLAN_END
            """,
            ["architect", "software-engineer", "quality-engineer"],
            scepticAvailable: true);

        Assert.Equal(
            ["architect", "software-engineer"],
            roles.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TeamLeadPlan_RejectsCheckpointsWhenScepticIsUnavailable()
    {
        var exception = Assert.Throws<PreMortemValidationException>(() =>
            PreMortemRules.ParsePlan(
                """
                PRE_MORTEM_PLAN_BEGIN
                {"AfterRoles":["software-engineer"]}
                PRE_MORTEM_PLAN_END
                """,
                ["software-engineer"],
                scepticAvailable: false));

        Assert.Contains(
            exception.Errors,
            error => error.Contains("must be empty", StringComparison.Ordinal));
    }

    [Fact]
    public void Review_AllowsClearAndCapsEvidenceBackedFindingsAtFive()
    {
        var clear = PreMortemRules.ParseReview(
            """
            PRE_MORTEM_STATUS: CLEAR
            PRE_MORTEM_FINDINGS_BEGIN
            {"Findings":[]}
            PRE_MORTEM_FINDINGS_END
            """);
        var sixFindings = string.Join(
            ",",
            Enumerable.Range(1, 6).Select(index =>
                $$"""{"FailureMode":"failure {{index}}","Evidence":"src\\file{{index}}.cs proves it","MissedSignal":"signal {{index}}","Prevention":"prevention {{index}}"}"""));

        var exception = Assert.Throws<PreMortemValidationException>(() =>
            PreMortemRules.ParseReview(
                $$"""
                 PRE_MORTEM_STATUS: FINDINGS
                 PRE_MORTEM_FINDINGS_BEGIN
                 {"Findings":[{{sixFindings}}]}
                 PRE_MORTEM_FINDINGS_END
                 """));

        Assert.False(clear.HasFindings);
        Assert.Empty(clear.Findings);
        Assert.Contains(
            exception.Errors,
            error => error.Contains("at most 5", StringComparison.Ordinal));
    }

    [Fact]
    public void Review_AllowsBoundedOutputAboveEightThousandCharacters()
    {
        var padding = new string('x', 1_100);
        var findings = string.Join(
            ",",
            Enumerable.Range(1, 2).Select(index =>
                $$"""{"FailureMode":"failure {{index}} {{padding}}","Evidence":"evidence {{index}} {{padding}}","MissedSignal":"signal {{index}} {{padding}}","Prevention":"prevention {{index}} {{padding}}"}"""));
        var review = $$"""
            PRE_MORTEM_STATUS: FINDINGS
            PRE_MORTEM_FINDINGS_BEGIN
            {"Findings":[{{findings}}]}
            PRE_MORTEM_FINDINGS_END
            """;

        Assert.InRange(review.Length, 8_001, PreMortemRules.MaximumReviewOutputCharacters);
        Assert.True(PreMortemRules.ParseReview(review).HasFindings);
    }

    [Fact]
    public void RevisionAssignment_PreservesEveryValidatedFinding()
    {
        var findings = string.Join(
            ",",
            Enumerable.Range(1, 5).Select(index =>
                $$"""{"FailureMode":"failure {{index}}","Evidence":"proof {{index}}","MissedSignal":"signal {{index}}","Prevention":"prevention {{index}}"}"""));
        var review = $$"""
            PRE_MORTEM_STATUS: FINDINGS
            PRE_MORTEM_FINDINGS_BEGIN
            {"Findings":[{{findings}}]}
            PRE_MORTEM_FINDINGS_END
            """;

        _ = PreMortemRules.ParseReview(review);
        var assignment = WorkflowEngine.BuildPreMortemRevisionAssignment(
            review,
            """["Implement"]""");

        Assert.Contains("\"FailureMode\":\"failure 1\"", assignment);
        Assert.Contains("\"FailureMode\":\"failure 5\"", assignment);
        Assert.Contains(PreMortemRules.FindingsEndSentinel, assignment);
    }

    [Fact]
    public void RevisionAssignment_PreservesTheOriginalAgentsRoleBoundary()
    {
        var designer = WorkflowEngine.BuildPreMortemRevisionAssignment(
            "PRE_MORTEM_STATUS: FINDINGS",
            """["Design"]""");
        var engineer = WorkflowEngine.BuildPreMortemRevisionAssignment(
            "PRE_MORTEM_STATUS: FINDINGS",
            """["Implement"]""");

        Assert.Contains("Do not implement downstream product corrections", designer);
        Assert.Contains("make the focused corrections owned by this role", engineer);
    }

    [Fact]
    public void RevisionAssignment_SanitizesNestedMachineContractSentinels()
    {
        var assignment = WorkflowEngine.BuildPreMortemRevisionAssignment(
            $"""
             PRE_MORTEM_STATUS: FINDINGS
             The prior result mentioned {TeamPlanParser.BeginSentinel},
             {TeamPlanParser.EndSentinel}, {FlowOutcomeParser.BeginSentinel}, and
             {FlowOutcomeParser.EndSentinel}.
             """,
            """["Design","PrepareOutcome"]""");

        Assert.DoesNotContain(TeamPlanParser.BeginSentinel, assignment);
        Assert.DoesNotContain(TeamPlanParser.EndSentinel, assignment);
        Assert.DoesNotContain(FlowOutcomeParser.BeginSentinel, assignment);
        Assert.DoesNotContain(FlowOutcomeParser.EndSentinel, assignment);
        Assert.Contains("[team plan begin marker]", assignment);
        Assert.Contains("[flow outcome end marker]", assignment);
        Assert.Contains(
            "exactly 1-24 consolidated ImplementationDetails",
            assignment,
            StringComparison.Ordinal);
        Assert.Contains(
            "merge overlapping old and new findings",
            assignment,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("claude-sonnet-5", "anthropic")]
    [InlineData("gpt-5.6-sol", "openai")]
    [InlineData("o3-mini", "openai")]
    [InlineData("gemini-3.7-flash", "google")]
    [InlineData("grok-4.6", "xai")]
    [InlineData("mai-code-1.1-flash", "microsoft")]
    public void ModelFamily_IsStableAcrossKnownModelLines(
        string model,
        string expectedFamily)
    {
        Assert.Equal(expectedFamily, ModelFamilyClassifier.Classify(model));
    }

    [Fact]
    public void PersistedCheckpoints_AreDisabledWhenTheCurrentCapIsZero()
    {
        var profile = new TaskProfile
        {
            FlowRunId = Guid.NewGuid(),
            Iteration = 1,
            Role = "software-engineer",
            RiskReason = "Test checkpoint.",
            PreMortemAfter = true
        };

        Assert.Empty(WorkflowEngine.SelectEnabledPreMortemCheckpoints(
            [profile],
            preMortemAvailable: false));
    }

    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(1, 1, true)]
    [InlineData(2, 1, false)]
    [InlineData(2, 2, true)]
    public void CurrentRoundCap_GatesPersistedReviews(
        int round,
        int maximumRounds,
        bool expected)
    {
        Assert.Equal(
            expected,
            WorkflowEngine.ShouldRunPreMortemRound(round, maximumRounds));
    }
}

public sealed class RetryProfileRoutingTests
{
    [Fact]
    public void RetryProfile_UsesItsCausalCheckpoint()
    {
        var firstStepId = Guid.NewGuid();
        var secondStepId = Guid.NewGuid();
        var retry = new FlowStep
        {
            FlowRunId = Guid.NewGuid(),
            Iteration = 1,
            AgentId = WorkflowEngine.PreMortemRole,
            AgentName = "Pre-mortem Sceptic",
            AgentRole = WorkflowEngine.PreMortemRole,
            RetryOfStepId = firstStepId
        };
        var first = new TaskProfile
        {
            FlowRunId = retry.FlowRunId,
            Iteration = 1,
            FlowStepId = firstStepId,
            Role = WorkflowEngine.PreMortemRole,
            Complexity = 3,
            RiskReason = "First checkpoint."
        };
        var newerUnrelated = new TaskProfile
        {
            FlowRunId = retry.FlowRunId,
            Iteration = 1,
            FlowStepId = secondStepId,
            Role = WorkflowEngine.PreMortemRole,
            Complexity = 10,
            RiskReason = "Later checkpoint.",
            CreatedAt = first.CreatedAt.AddMinutes(1)
        };

        Assert.Same(
            first,
            AdaptiveModelRouter.SelectRetrySourceProfile(
                retry,
                [newerUnrelated, first]));
    }
}

public sealed class RoutingObservationTests
{
    [Fact]
    public async Task TargetedRework_OverridesProvisionalHandoffAcceptance()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite(connection)
            .Options;
        var databaseFactory = new InlineDbContextFactory(options);
        Guid flowId;
        Guid stepId;
        await using (var database = await databaseFactory.CreateDbContextAsync())
        {
            await database.Database.EnsureCreatedAsync();
            var flow = new FlowRun
            {
                Title = "Observe rework",
                OriginalRequest = "Implement a change.",
                Status = FlowStatus.WaitingForFeedback
            };
            var step = new FlowStep
            {
                FlowRunId = flow.Id,
                Iteration = 1,
                AgentId = "software-engineer",
                AgentName = "Software Engineer",
                AgentRole = "software-engineer",
                Status = StepStatus.Completed
            };
            var profile = new BootstrapTaskProfileFactory().Create(
                "team-lead",
                "Implement a change.",
                flow.Id,
                1);
            profile.Role = step.AgentRole;
            profile.FlowStepId = step.Id;
            var catalog = new ModelCatalogSnapshot
            {
                CatalogVersion = "test-catalog",
                IsCurrent = true
            };
            var candidate = new ModelCatalogCandidate
            {
                ModelCatalogSnapshotId = catalog.Id,
                Model = "test-model",
                Effort = "medium",
                PremiumMultiplier = 1
            };
            catalog.Candidates.Add(candidate);
            var decision = new RoutingDecision
            {
                FlowStepId = step.Id,
                TaskProfileId = profile.Id,
                ModelCatalogSnapshotId = catalog.Id,
                SelectedModel = candidate.Model,
                SelectedEffort = candidate.Effort,
                Strategy = ModelSelectionStrategy.MaximumQuality,
                Reason = "Test decision."
            };
            database.Flows.Add(flow);
            database.FlowSteps.Add(step);
            database.TaskProfiles.Add(profile);
            database.ModelCatalogSnapshots.Add(catalog);
            database.RoutingDecisions.Add(decision);
            await database.SaveChangesAsync();
            flowId = flow.Id;
            stepId = step.Id;
        }

        var recorder = new RoutingObservationRecorder(databaseFactory, TimeProvider.System);
        await recorder.RecordCompletionAsync(
            stepId,
            accepted: true,
            1_000,
            1,
            "accepted-handoff");
        await recorder.RecordTargetedReworkAsync(
            flowId,
            1,
            ["software-engineer"]);

        await using var check = await databaseFactory.CreateDbContextAsync();
        var observation = await check.RoutingObservations.SingleAsync();
        Assert.False(observation.Accepted);
        Assert.Equal(.75, observation.EvidenceWeight);
        Assert.Equal("customer-rework-targeted", observation.OutcomeKind);
    }

    private sealed class InlineDbContextFactory(
        DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);

        public Task<HarnessDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}

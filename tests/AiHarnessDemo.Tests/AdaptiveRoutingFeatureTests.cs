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
            TEAM_TASK_PROFILES_V1_BEGIN
            {"Version":"task-profile-v1","Profiles":[{"Role":"software-engineer","Complexity":7,"ReasoningDepth":8,"ContextDemand":6,"ToolIntensity":9,"TaskTypeTags":["Implementation"],"Risk":"High","RiskReason":"The implementation changes a public contract.","Confidence":0.9,"Rationales":["Code and focused validation are required."]}]}
            TEAM_TASK_PROFILES_V1_END
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
                TEAM_TASK_PROFILES_V1_BEGIN
                {"Version":"task-profile-v1","Profiles":[{"Role":"quality-engineer","Complexity":11,"ReasoningDepth":5,"ContextDemand":5,"ToolIntensity":5,"TaskTypeTags":["Quality"],"Risk":"Low","RiskReason":"Validation task.","Confidence":0.5,"Rationales":["Validate."]}]}
                TEAM_TASK_PROFILES_V1_END
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
            PRE_MORTEM_PLAN_V1_BEGIN
            {"Version":"pre-mortem-plan-v1","AfterRoles":["architect","software-engineer"]}
            PRE_MORTEM_PLAN_V1_END
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
                PRE_MORTEM_PLAN_V1_BEGIN
                {"Version":"pre-mortem-plan-v1","AfterRoles":["software-engineer"]}
                PRE_MORTEM_PLAN_V1_END
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
            PRE_MORTEM_FINDINGS_V1_BEGIN
            {"Version":"pre-mortem-findings-v1","Findings":[]}
            PRE_MORTEM_FINDINGS_V1_END
            """);
        var sixFindings = string.Join(
            ",",
            Enumerable.Range(1, 6).Select(index =>
                $$"""{"FailureMode":"failure {{index}}","Evidence":"src\\file{{index}}.cs proves it","MissedSignal":"signal {{index}}","Prevention":"prevention {{index}}"}"""));

        var exception = Assert.Throws<PreMortemValidationException>(() =>
            PreMortemRules.ParseReview(
                $$"""
                 PRE_MORTEM_STATUS: FINDINGS
                 PRE_MORTEM_FINDINGS_V1_BEGIN
                 {"Version":"pre-mortem-findings-v1","Findings":[{{sixFindings}}]}
                 PRE_MORTEM_FINDINGS_V1_END
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
            PRE_MORTEM_FINDINGS_V1_BEGIN
            {"Version":"pre-mortem-findings-v1","Findings":[{{findings}}]}
            PRE_MORTEM_FINDINGS_V1_END
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
            PRE_MORTEM_FINDINGS_V1_BEGIN
            {"Version":"pre-mortem-findings-v1","Findings":[{{findings}}]}
            PRE_MORTEM_FINDINGS_V1_END
            """;

        _ = PreMortemRules.ParseReview(review);
        var assignment = WorkflowEngine.BuildPreMortemRevisionAssignment(
            review,
            "software-engineer");

        Assert.Contains("\"FailureMode\":\"failure 1\"", assignment);
        Assert.Contains("\"FailureMode\":\"failure 5\"", assignment);
        Assert.Contains(PreMortemRules.FindingsEndSentinel, assignment);
    }

    [Fact]
    public void RevisionAssignment_PreservesTheOriginalAgentsRoleBoundary()
    {
        var designer = WorkflowEngine.BuildPreMortemRevisionAssignment(
            "PRE_MORTEM_STATUS: FINDINGS",
            "product-designer");
        var engineer = WorkflowEngine.BuildPreMortemRevisionAssignment(
            "PRE_MORTEM_STATUS: FINDINGS",
            "software-engineer");

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
            "product-designer",
            """["Design","PrepareOutcome"]""",
            "studio-v2");

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

public sealed class FeedbackRoutingAttributionTests
{
    [Fact]
    public void ReworkTargets_AreStrictlyValidatedAndRemovedFromCustomerReply()
    {
        const string output = """
            The implementation needs another pass.
            REWORK_TARGET_ROLES: software-engineer, quality-engineer
            """;
        var valid = FeedbackCoordinator.TryParseReworkTargets(
            output,
            new HashSet<string>(["software-engineer", "quality-engineer"], StringComparer.Ordinal),
            out var targets);

        Assert.True(valid);
        Assert.Equal(["software-engineer", "quality-engineer"], targets);
        Assert.Equal(
            "The implementation needs another pass.",
            FeedbackCoordinator.StripReworkTargetMarker(output));
    }

    [Fact]
    public void ReworkTargets_RejectUnknownOrMissingAttribution()
    {
        var eligible = new HashSet<string>(["software-engineer"], StringComparer.Ordinal);

        Assert.False(FeedbackCoordinator.TryParseReworkTargets(
            "REWORK_TARGET_ROLES: architect",
            eligible,
            out var unknown));
        Assert.Empty(unknown);
        Assert.False(FeedbackCoordinator.TryParseReworkTargets(
            "No structured attribution.",
            eligible,
            out var missing));
        Assert.Empty(missing);
    }
}

public sealed class RoutingPersistenceTests
{
    [Fact]
    public async Task Settings_DefaultStrategyAndSchemaUpgradeAreStable()
    {
        Assert.Equal(
            ModelSelectionStrategy.MaximumQuality,
            new HarnessSettings().ModelSelectionStrategy);

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE Settings (
                    Id INTEGER NOT NULL PRIMARY KEY,
                    RepositoryPath TEXT NOT NULL,
                    RepositoryKnowledge TEXT NOT NULL,
                    Outcome TEXT NOT NULL,
                    MaxHandoffRetries INTEGER NOT NULL,
                    ExecutionMode TEXT NOT NULL,
                    UpdatedAt INTEGER NOT NULL
                );
                INSERT INTO Settings VALUES
                    (1, '', '', 'PullRequest', 2, 'LiveCopilot', 0);
                """;
            await create.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var database = new HarnessDbContext(options);

        await DatabaseInitializer.EnsureSettingsSchemaAsync(database);
        var settings = await database.Settings.SingleAsync();

        Assert.Equal(ModelSelectionStrategy.MaximumQuality, settings.ModelSelectionStrategy);
    }

    [Fact]
    public async Task RoutingSchema_AllowsOneFreshProfilePerStepForTheSameRole()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var database = new HarnessDbContext(options);
        await database.Database.EnsureCreatedAsync();
        await database.Database.ExecuteSqlRawAsync(
            """
            DROP INDEX IF EXISTS IX_TaskProfiles_Flow_Iteration_Role;
            CREATE UNIQUE INDEX IX_TaskProfiles_Flow_Iteration_Role
                ON TaskProfiles (FlowRunId, Iteration, Role);
            """);

        await DatabaseInitializer.EnsureRoutingSchemaAsync(database);

        var flow = new FlowRun
        {
            Title = "Profile routing",
            OriginalRequest = "Route changing intake tasks."
        };
        var firstStep = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = 1,
            AgentId = "account-manager",
            AgentName = "Account Manager",
            AgentRole = "account-manager"
        };
        var secondStep = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = 1,
            AgentId = "account-manager",
            AgentName = "Account Manager",
            AgentRole = "account-manager"
        };
        database.Flows.Add(flow);
        database.FlowSteps.AddRange(firstStep, secondStep);
        var factory = new BootstrapTaskProfileFactory();
        database.TaskProfiles.AddRange(
            factory.Create(
                "account-manager",
                "Clarify a small text change.",
                flow.Id,
                1,
                firstStep.Id),
            factory.Create(
                "account-manager",
                "Clarify a production authentication migration.",
                flow.Id,
                1,
                secondStep.Id));

        await database.SaveChangesAsync();

        Assert.Equal(
            2,
            await database.TaskProfiles.CountAsync(item =>
                item.FlowRunId == flow.Id &&
                item.Role == "account-manager"));
    }

    [Fact]
    public async Task RoutingSchema_AddsPreMortemCheckpointToExistingProfiles()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE TaskProfiles (
                    Id TEXT NOT NULL CONSTRAINT PK_TaskProfiles PRIMARY KEY,
                    FlowRunId TEXT NOT NULL,
                    Iteration INTEGER NOT NULL,
                    FlowStepId TEXT NULL,
                    Role TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync();
        }
        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var database = new HarnessDbContext(options);

        await DatabaseInitializer.EnsureRoutingSchemaAsync(database);
        await using var probe = connection.CreateCommand();
        probe.CommandText =
            "SELECT COUNT(*) FROM pragma_table_info('TaskProfiles') " +
            "WHERE name IN ('PreMortemAfter', 'PlanStepKey', 'AgentId');";

        Assert.Equal(3L, Convert.ToInt64(await probe.ExecuteScalarAsync()));
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

public sealed class ApprovalGatedReleaseTests
{
    [Fact]
    public async Task Approval_QueuesPublicationInsteadOfClosingTheFlow()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite(connection)
            .Options;
        var databaseFactory = new ApprovalDbContextFactory(options);
        var flow = await SeedWaitingFlowAsync(databaseFactory);
        var queue = new FlowQueue();
        using var gate = new HandoffGateEngine();
        gate.SetTrustLevel(HandoffActionType.Release, HandoffTrustLevel.Gated);
        await RestoreGateAsync(databaseFactory, gate);
        var coordinator = CreateCoordinator(databaseFactory, gate, queue);
        Guid gateId;
        await using (var gateDatabase = await databaseFactory.CreateDbContextAsync())
        {
            gateId = await gateDatabase.GateRecords.Select(item => item.Id).SingleAsync();
        }

        var updated = await coordinator.DecideAsync(
            flow.Id,
            approve: true,
            gateId,
            candidateFingerprint: string.Empty,
            feedback: string.Empty,
            CancellationToken.None);

        Assert.Equal(ReleaseDecisionOutcome.Approved, updated.Outcome);
        Assert.Equal(FlowStatus.Queued, updated.Flow.Status);
        Assert.Null(updated.Flow.CompletedAt);
        Assert.Equal($"#/preview/{flow.Id}", updated.Flow.OutcomeUrl);
        var publication = Assert.Single(
            updated.Flow.Steps,
            step => step.Label == WorkflowEngine.ApprovedPublicationLabel);
        Assert.Equal(StepStatus.Pending, publication.Status);
        Assert.Contains(
            WorkflowEngine.ApprovedPublicationAssignment(OutcomeType.PullRequest),
            publication.InputSummary);
        Assert.True(queue.Reader.TryRead(out var queuedFlowId));
        Assert.Equal(flow.Id, queuedFlowId);
        await using var database = await databaseFactory.CreateDbContextAsync();
        var resolvedGate = await database.GateRecords.SingleAsync();
        Assert.True(resolvedGate.Resolved);
        Assert.True(resolvedGate.Approved);
    }

    [Fact]
    public async Task Rejection_QueuesReworkWithoutPublication()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite(connection)
            .Options;
        var databaseFactory = new ApprovalDbContextFactory(options);
        var flow = await SeedWaitingFlowAsync(databaseFactory);
        await using (var database = await databaseFactory.CreateDbContextAsync())
        {
            database.FlowMessages.Add(new FlowMessage
            {
                FlowRunId = flow.Id,
                Role = ConversationRole.Customer,
                Content = "Increase text contrast."
            });
            await database.SaveChangesAsync();
        }
        var queue = new FlowQueue();
        using var gate = new HandoffGateEngine();
        gate.SetTrustLevel(HandoffActionType.Release, HandoffTrustLevel.Gated);
        await RestoreGateAsync(databaseFactory, gate);
        var coordinator = CreateCoordinator(databaseFactory, gate, queue);
        Guid gateId;
        await using (var gateDatabase = await databaseFactory.CreateDbContextAsync())
        {
            gateId = await gateDatabase.GateRecords.Select(item => item.Id).SingleAsync();
        }

        var updated = await coordinator.DecideAsync(
            flow.Id,
            approve: false,
            gateId,
            candidateFingerprint: string.Empty,
            feedback: string.Empty,
            CancellationToken.None);

        Assert.Equal(ReleaseDecisionOutcome.Rejected, updated.Outcome);
        Assert.Equal(FlowStatus.Queued, updated.Flow.Status);
        Assert.Equal(2, updated.Flow.Iteration);
        Assert.DoesNotContain(
            updated.Flow.Steps,
            step => step.Label == WorkflowEngine.ApprovedPublicationLabel);
        Assert.True(queue.Reader.TryRead(out var queuedFlowId));
        Assert.Equal(flow.Id, queuedFlowId);
    }

    [Fact]
    public void PullRequestReference_IsParsedFromPublishedReleaseOutput()
    {
        var reference = PublishedOutcomeVerifier.ParsePullRequest(
            "Published https://github.com/devclub/site/pull/38 after approval.");

        Assert.NotNull(reference);
        Assert.Equal("devclub/site", reference.Repository);
        Assert.Equal(38, reference.Number);
        Assert.Null(PublishedOutcomeVerifier.ParsePullRequest("Local candidate only."));
        Assert.Equal(
            "devclub/site",
            PublishedOutcomeVerifier.NormalizeGitHubRepository(
                "git@github.com:devclub/site.git"));
        Assert.Equal(
            "devclub/site.gitops",
            PublishedOutcomeVerifier.NormalizeGitHubRepository(
                "git@github.com:devclub/site.gitops.git"));
        Assert.Null(PublishedOutcomeVerifier.NormalizeSingleGitHubRemote(
            "https://github.com/devclub/site.git\n" +
            "https://github.com/other/site.git"));
        using var ownHead = System.Text.Json.JsonDocument.Parse(
            """{"headRepository":{"nameWithOwner":"devclub/site"},"headRepositoryOwner":{"login":"devclub"}}""");
        using var forkHead = System.Text.Json.JsonDocument.Parse(
            """{"headRepository":{"nameWithOwner":"someone/site"},"headRepositoryOwner":{"login":"someone"}}""");
        Assert.True(PublishedOutcomeVerifier.IsExpectedHeadRepository(
            ownHead.RootElement,
            "devclub/site"));
        Assert.False(PublishedOutcomeVerifier.IsExpectedHeadRepository(
            forkHead.RootElement,
            "devclub/site"));
    }

    private static FeedbackCoordinator CreateCoordinator(
        IDbContextFactory<HarnessDbContext> databaseFactory,
        HandoffGateEngine gate,
        FlowQueue queue) =>
        new(
            databaseFactory,
            new FixedModelRouter(),
            new BootstrapTaskProfileFactory(),
            TestRoutingSupport.Recorder(databaseFactory),
            new NeverApprovalAgentRunner(),
            gate,
            queue,
            new FlowLifecycleCoordinator());

    private static async Task<FlowRun> SeedWaitingFlowAsync(
        IDbContextFactory<HarnessDbContext> databaseFactory)
    {
        await using var database = await databaseFactory.CreateDbContextAsync();
        await database.Database.EnsureCreatedAsync();
        var flow = new FlowRun
        {
            Title = "Approval gated release",
            OriginalRequest = "Prepare a release.",
            ConsolidatedRequest = "Prepare a release.",
            Status = FlowStatus.WaitingForFeedback,
            Outcome = OutcomeType.PullRequest,
            RepositoryPath = @"C:\code\demo"
        };
        flow.OutcomeUrl = $"#/preview/{flow.Id}";
        var release = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = 1,
            Sequence = 70,
            AgentId = "release-engineer",
            AgentName = "Release Engineer",
            AgentRole = "release-engineer",
            Label = WorkflowEngine.ReleaseCandidateLabel,
            Status = StepStatus.Completed,
            Attempt = 1,
            OutputSummary = "Local candidate prepared."
        };
        flow.Steps.Add(release);
        database.Flows.Add(flow);
        database.GateRecords.Add(new HandoffGateRecord
        {
            FlowRunId = flow.Id,
            FlowStepId = release.Id,
            ActionType = HandoffActionType.Release,
            Decision = HandoffGateDecision.AwaitingHumanApproval,
            TrustLevelAtDecision = HandoffTrustLevel.Gated,
            Summary = "Candidate ready.",
            Evidence = "Validated.",
            Reason = "Customer approval required."
        });
        await database.SaveChangesAsync();
        return flow;
    }

    private static async Task RestoreGateAsync(
        IDbContextFactory<HarnessDbContext> databaseFactory,
        HandoffGateEngine gate)
    {
        await using var database = await databaseFactory.CreateDbContextAsync();
        gate.RestoreHistory(await database.GateRecords.AsNoTracking().ToListAsync());
    }

    private sealed class ApprovalDbContextFactory(
        DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);

        public Task<HarnessDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class NeverApprovalAgentRunner : IAgentRunner
    {
        public Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionContext context,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Approval scheduling must not execute an agent inline.");
    }
}

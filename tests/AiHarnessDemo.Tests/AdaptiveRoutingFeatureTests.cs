using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
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

        var updated = await coordinator.DecideAsync(
            flow.Id,
            approve: true,
            CancellationToken.None);

        Assert.Equal(FlowStatus.Queued, updated.Status);
        Assert.Null(updated.CompletedAt);
        Assert.Equal($"#/preview/{flow.Id}", updated.OutcomeUrl);
        var publication = Assert.Single(
            updated.Steps,
            step => step.Label == WorkflowEngine.ApprovedPublicationLabel);
        Assert.Equal(StepStatus.Pending, publication.Status);
        Assert.Contains(
            WorkflowEngine.ApprovedPublicationAssignment,
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

        var updated = await coordinator.DecideAsync(
            flow.Id,
            approve: false,
            CancellationToken.None);

        Assert.Equal(FlowStatus.Queued, updated.Status);
        Assert.Equal(2, updated.Iteration);
        Assert.DoesNotContain(
            updated.Steps,
            step => step.Label == WorkflowEngine.ApprovedPublicationLabel);
        Assert.True(queue.Reader.TryRead(out var queuedFlowId));
        Assert.Equal(flow.Id, queuedFlowId);
    }

    [Fact]
    public void PullRequestUrl_IsAcceptedOnlyFromPublishedReleaseOutput()
    {
        Assert.Equal(
            "https://github.com/devclub/site/pull/38",
            WorkflowEngine.ExtractPullRequestUrl(
                "Published https://github.com/devclub/site/pull/38 after approval."));
        Assert.Null(WorkflowEngine.ExtractPullRequestUrl("Local candidate only."));
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
            queue);

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

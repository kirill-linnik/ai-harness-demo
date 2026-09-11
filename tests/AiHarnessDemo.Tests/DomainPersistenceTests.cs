using System.Collections.Immutable;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Security;
using AiHarnessDemo.Data;
using AiHarnessDemo.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AiHarnessDemo.Tests;

public sealed class DomainPersistenceTests
{
    [Fact]
    public async Task FreshSchema_ContainsSliceOneTablesKeysAndBoundedEventData()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var database = CreateDatabase(connection);

        await database.Database.EnsureCreatedAsync();

        Assert.True(await TableExistsAsync(connection, "FlowAgentSnapshots"));
        Assert.True(await TableExistsAsync(connection, "FlowPlanDocuments"));
        Assert.True(await TableExistsAsync(connection, "DemoInstances"));
        Assert.Equal(
            IntakeV2Parser.MaximumJsonCharacters,
            database.Model.FindEntityType(typeof(FlowEvent))!
                .FindProperty(nameof(FlowEvent.DataJson))!
                .GetMaxLength());

        await using var foreignKeyProbe = connection.CreateCommand();
        foreignKeyProbe.CommandText =
            "SELECT COUNT(*) FROM pragma_foreign_key_list('FlowAgentSnapshots') " +
            "WHERE [table] = 'Flows' AND [from] = 'FlowRunId' AND on_delete = 'CASCADE';";
        Assert.Equal(1L, Convert.ToInt64(await foreignKeyProbe.ExecuteScalarAsync()));

        await using var indexProbe = connection.CreateCommand();
        indexProbe.CommandText =
            "SELECT COUNT(*) FROM pragma_index_list('FlowPlanDocuments') " +
            "WHERE name = 'IX_FlowPlanDocuments_FlowRunId_Iteration' AND [unique] = 1;";
        Assert.Equal(1L, Convert.ToInt64(await indexProbe.ExecuteScalarAsync()));

        await using var demoIndexProbe = connection.CreateCommand();
        demoIndexProbe.CommandText =
            "SELECT COUNT(*) FROM pragma_index_list('DemoInstances') " +
            "WHERE name = 'IX_DemoInstances_ActivePort' AND [unique] = 1;";
        Assert.Equal(
            1L,
            Convert.ToInt64(await demoIndexProbe.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task CurrentSchemaUpgrade_IsIdempotentAndPreservesLegacyHistory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE Flows (
                    Id TEXT NOT NULL CONSTRAINT PK_Flows PRIMARY KEY,
                    Title TEXT NOT NULL,
                    OriginalRequest TEXT NOT NULL,
                    ConsolidatedRequest TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    Iteration INTEGER NOT NULL,
                    RepositoryPath TEXT NOT NULL,
                    RepositoryKnowledge TEXT NOT NULL,
                    Outcome TEXT NOT NULL,
                    ModelSelectionStrategy TEXT NOT NULL,
                    ExecutionMode TEXT NOT NULL,
                    WorkspacePath TEXT NOT NULL,
                    BranchName TEXT NOT NULL,
                    OutcomeUrl TEXT NOT NULL,
                    OutcomeLabel TEXT NOT NULL,
                    FailureReason TEXT NOT NULL,
                    OutcomeVerificationJson TEXT NOT NULL,
                    CreatedAt INTEGER NOT NULL,
                    UpdatedAt INTEGER NOT NULL,
                    CompletedAt INTEGER NULL
                );
                CREATE TABLE Agents (
                    Id TEXT NOT NULL CONSTRAINT PK_Agents PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Description TEXT NOT NULL,
                    Role TEXT NOT NULL,
                    SourcePath TEXT NOT NULL,
                    Accent TEXT NOT NULL,
                    Enabled INTEGER NOT NULL,
                    SortOrder INTEGER NOT NULL,
                    UpdatedAt INTEGER NOT NULL
                );
                CREATE TABLE FlowMessages (
                    Id TEXT NOT NULL CONSTRAINT PK_FlowMessages PRIMARY KEY,
                    FlowRunId TEXT NOT NULL,
                    Role TEXT NOT NULL,
                    Content TEXT NOT NULL,
                    IsQuestion INTEGER NOT NULL,
                    CreatedAt INTEGER NOT NULL
                );
                CREATE TABLE FlowEvents (
                    Id TEXT NOT NULL CONSTRAINT PK_FlowEvents PRIMARY KEY,
                    FlowRunId TEXT NOT NULL,
                    FlowStepId TEXT NULL,
                    Type TEXT NOT NULL,
                    Message TEXT NOT NULL,
                    CreatedAt INTEGER NOT NULL
                );
                CREATE TABLE GateRecords (
                    Id TEXT NOT NULL CONSTRAINT PK_GateRecords PRIMARY KEY,
                    FlowRunId TEXT NOT NULL,
                    FlowStepId TEXT NOT NULL,
                    ActionType TEXT NOT NULL,
                    Decision TEXT NOT NULL,
                    TrustLevelAtDecision TEXT NOT NULL,
                    Summary TEXT NOT NULL,
                    Evidence TEXT NOT NULL,
                    Reason TEXT NOT NULL,
                    DecidedAt INTEGER NOT NULL,
                    Resolved INTEGER NOT NULL,
                    Approved INTEGER NULL,
                    ResolvedBy TEXT NULL,
                    ResolutionNote TEXT NULL,
                    ResolvedAt INTEGER NULL
                );
                INSERT INTO Flows VALUES (
                    '11111111-1111-1111-1111-111111111111',
                    'Legacy flow', 'Keep history', 'Keep history',
                    'Approved', 2, 'C:\repo', 'knowledge', 'PullRequest',
                    'MaximumQuality', 'LiveCopilot', 'C:\workspace', 'legacy',
                    'https://example.test/pr/1', 'PR 1', '', '', 1, 2, 3);
                INSERT INTO Agents VALUES (
                    'architect', 'Architect', 'Designs', 'architect',
                    '.github\agents\architect.agent.md', 'violet', 1, 1, 4);
                INSERT INTO FlowMessages VALUES (
                    '22222222-2222-2222-2222-222222222222',
                    '11111111-1111-1111-1111-111111111111',
                    'Customer', 'Original message', 0, 5);
                INSERT INTO FlowEvents VALUES (
                    '33333333-3333-3333-3333-333333333333',
                    '11111111-1111-1111-1111-111111111111',
                    NULL, 'flow.completed', 'Completed', 6);
                INSERT INTO GateRecords VALUES (
                    '44444444-4444-4444-4444-444444444444',
                    '11111111-1111-1111-1111-111111111111',
                    '55555555-5555-5555-5555-555555555555',
                    'Release', 'AutoApproved', 'Auto', 'Release', '', '', 7,
                    1, 1, 'operator', 'approved', 8);
                """;
            await create.ExecuteNonQueryAsync();
        }

        await using var database = CreateDatabase(connection);
        await DatabaseInitializer.EnsureFlowRunSchemaAsync(database);
        await DatabaseInitializer.EnsureSliceOneSchemaAsync(database);
        await DatabaseInitializer.EnsureDemoRuntimeSchemaAsync(database);
        await DatabaseInitializer.EnsureFlowRunSchemaAsync(database);
        await DatabaseInitializer.EnsureSliceOneSchemaAsync(database);
        await DatabaseInitializer.EnsureDemoRuntimeSchemaAsync(database);

        database.ChangeTracker.Clear();
        var flow = await database.Flows
            .AsNoTracking()
            .Include(item => item.Messages)
            .Include(item => item.Events)
            .Include(item => item.GateRecords)
            .SingleAsync();
        var agent = await database.Agents.AsNoTracking().SingleAsync();

        Assert.Equal(FlowKind.Delivery, flow.Kind);
        Assert.Equal("legacy-v1", flow.ContractVersion);
        Assert.Equal("https://example.test/pr/1", flow.OutcomeUrl);
        Assert.Equal("Original message", Assert.Single(flow.Messages).Content);
        Assert.Equal("flow.completed", Assert.Single(flow.Events).Type);
        Assert.Equal(HandoffActionType.Release, Assert.Single(flow.GateRecords).ActionType);
        Assert.Null(flow.GateRecords[0].ReviewDecision);
        Assert.Equal(AgentDefinitionStatus.Valid, agent.DefinitionStatus);
        Assert.True(agent.Switchable);
        Assert.Empty(await database.FlowAgentSnapshots.ToListAsync());
        Assert.True(await TableExistsAsync(connection, "DemoInstances"));
    }

    [Fact]
    public async Task NewEnumsAndEntities_RoundTripAsNamedValues()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var database = CreateDatabase(connection);
        await database.Database.EnsureCreatedAsync();

        var parent = new FlowRun
        {
            Title = "Advisory",
            OriginalRequest = "Assess",
            Kind = FlowKind.Advisory,
            ContractVersion = "studio-v2",
            Status = FlowStatus.Blocked,
            Outcome = OutcomeType.None,
            CurrentBlockerCode = "qualification"
        };
        var child = new FlowRun
        {
            Title = "Delivery",
            OriginalRequest = "Implement",
            ParentFlowRun = parent,
            ParentIteration = 1,
            LinkKind = FlowLinkKind.AdvisoryPromotion,
            OutcomeOwnerPlanStepKey = "implement",
            PublicationPlanStepKey = "publish"
        };
        var step = new FlowStep
        {
            FlowRun = child,
            Iteration = 1,
            Sequence = 1,
            AgentId = "engineer",
            AgentName = "Engineer",
            AgentRole = "implementation",
            PlanStepKey = "implement",
            PlanDutiesJson = """["Implement","Verify"]""",
            PlanStage = PlanStage.AfterApproval,
            IsOutcomeOwner = true,
            PermissionProfile = ExecutionPermissionProfile.Publish,
            EffectivePermissionJson = """{"publish":true}""",
            WorkflowRevision = "workflow-v2"
        };
        child.AgentSnapshots.Add(new FlowAgentSnapshot
        {
            AgentId = "engineer",
            Name = "Engineer",
            Description = "Implements",
            Role = "implementation",
            Instructions = "Implement safely.",
            DefinitionHash = "sha256",
            EnabledAtSnapshot = true,
            Required = true,
            Switchable = false,
            SourceFileName = "engineer.agent.md"
        });
        child.PlanDocuments.Add(new FlowPlanDocument
        {
            Iteration = 1,
            Version = "plan-v2",
            Disposition = "Ready",
            RawJson = """{"Version":"plan-v2"}"""
        });
        child.Events.Add(new FlowEvent
        {
            Type = "flow.blocked",
            Message = "Blocked",
            DataJson = """{"code":"qualification"}"""
        });
        child.GateRecords.Add(new HandoffGateRecord
        {
            FlowStepId = step.Id,
            ActionType = HandoffActionType.CustomerReview,
            Decision = HandoffGateDecision.AwaitingHumanApproval,
            TrustLevelAtDecision = HandoffTrustLevel.Gated,
            ReviewDecision = ReviewDecision.RefinementRequested
        });
        child.TaskProfiles.Add(new TaskProfile
        {
            FlowStepId = step.Id,
            Iteration = 1,
            PlanStepKey = "implement",
            AgentId = "engineer",
            Role = "implementation",
            RiskReason = "Delivery"
        });
        database.Agents.Add(new AgentRecord
        {
            Id = "engineer",
            Name = "Engineer",
            Description = "Implements",
            Role = "implementation",
            SourcePath = ".github\\agents\\engineer.agent.md",
            DefinitionStatus = AgentDefinitionStatus.Invalid,
            ValidationError = "Malformed frontmatter",
            Required = true,
            Switchable = false,
            DefinitionHash = "sha256"
        });
        database.Flows.AddRange(parent, child);
        database.FlowSteps.Add(step);
        await database.SaveChangesAsync();

        database.ChangeTracker.Clear();
        var loaded = await database.Flows
            .AsNoTracking()
            .Include(item => item.ParentFlowRun)
            .Include(item => item.Steps)
            .Include(item => item.AgentSnapshots)
            .Include(item => item.PlanDocuments)
            .Include(item => item.Events)
            .Include(item => item.GateRecords)
            .Include(item => item.TaskProfiles)
            .SingleAsync(item => item.Id == child.Id);

        Assert.Equal(FlowLinkKind.AdvisoryPromotion, loaded.LinkKind);
        Assert.Equal(FlowKind.Advisory, loaded.ParentFlowRun!.Kind);
        Assert.Equal(FlowStatus.Blocked, loaded.ParentFlowRun.Status);
        Assert.Equal(OutcomeType.None, loaded.ParentFlowRun.Outcome);
        Assert.Equal(PlanStage.AfterApproval, Assert.Single(loaded.Steps).PlanStage);
        Assert.Equal(
            ExecutionPermissionProfile.Publish,
            loaded.Steps[0].PermissionProfile);
        Assert.Equal(
            ReviewDecision.RefinementRequested,
            Assert.Single(loaded.GateRecords).ReviewDecision);
        Assert.Equal("engineer", Assert.Single(loaded.AgentSnapshots).AgentId);
        Assert.Equal("plan-v2", Assert.Single(loaded.PlanDocuments).Version);
        Assert.Equal("implement", Assert.Single(loaded.TaskProfiles).PlanStepKey);
        Assert.Equal(
            AgentDefinitionStatus.Invalid,
            (await database.Agents.AsNoTracking().SingleAsync()).DefinitionStatus);
    }

    [Fact]
    public async Task IntakeInvocationKind_RoundTripsAndRemainsAReadOnlyRuntimeBoundary()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var warnings = new List<string>();
        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite(connection)
            .LogTo(warnings.Add, LogLevel.Warning)
            .Options;
        await using var database = new HarnessDbContext(options);
        await database.Database.EnsureCreatedAsync();

        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText =
                "SELECT dflt_value FROM pragma_table_info('FlowSteps') " +
                "WHERE name = 'InvocationKind';";
            Assert.Equal(DBNull.Value, await schema.ExecuteScalarAsync());
        }

        var flow = new FlowRun
        {
            Title = "Classify intake",
            OriginalRequest = "Assess the request",
            Kind = FlowKind.Delivery,
            ContractVersion = "studio-v2"
        };
        var step = new FlowStep
        {
            FlowRun = flow,
            Iteration = 1,
            Sequence = 1,
            AgentId = "account-manager",
            AgentName = "Account Manager",
            AgentRole = "account-manager",
            PlanStepKey = "account-manager:intake",
            PlanDutiesJson = """["Analyze"]""",
            PlanStage = PlanStage.BeforeReview,
            PermissionProfile = ExecutionPermissionProfile.ReadOnlySource,
            InvocationKind = ExecutionInvocationKind.Intake
        };
        database.FlowSteps.Add(step);

        Assert.Equal(ExecutionInvocationKind.Intake, step.InvocationKind);
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();

        var loaded = await database.FlowSteps.AsNoTracking().SingleAsync();
        Assert.Equal(ExecutionInvocationKind.Intake, loaded.InvocationKind);

        var permission = new PermissionProfileResolver().Resolve(
            new PermissionResolutionRequest(
                FlowKind.Delivery,
                loaded.InvocationKind,
                PlanStage.BeforeReview,
                ImmutableArray.Create(PlanDuty.Analyze),
                DurableReviewDecision: null,
                DurableApproval: false,
                IsOnlyPlannedPublishStep: false,
                ContractVersion: "studio-v2",
                LegacyPublicationAuthorized: false,
                IsGovernedOutcomeVerification: false),
            new WorkflowPermissionRestrictions(
                ExecutionPermissionProfile.ReadOnlySource,
                ExecutionPermissionProfile.WorkspaceWrite,
                ExecutionPermissionProfile.Publish,
                Enum.GetValues<ExecutionPermissionProfile>()
                    .ToImmutableDictionary(
                        profile => profile,
                        _ => ImmutableArray<string>.Empty)));

        Assert.Equal(ExecutionPermissionProfile.ReadOnlySource, permission.Profile);
        Assert.DoesNotContain(
            warnings,
            warning => warning.Contains("20601", StringComparison.Ordinal));
    }

    [Fact]
    public void AgentSessionIdentity_DistinguishesRepeatedPlanSteps()
    {
        var flowId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var first = AgentSessionIdentity.Create(flowId, 1, "engineer", "implementation");
        var second = AgentSessionIdentity.Create(flowId, 1, "engineer", "verification");

        Assert.NotEqual(first, second);
        Assert.Equal(
            first,
            AgentSessionIdentity.Create(flowId, 1, "engineer", "implementation"));
    }

    private static HarnessDbContext CreateDatabase(SqliteConnection connection) =>
        new(
            new DbContextOptionsBuilder<HarnessDbContext>()
                .UseSqlite(connection)
                .Options);

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;";
        command.Parameters.AddWithValue("@name", tableName);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }
}

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
            IntakeParser.MaximumJsonCharacters,
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
            WorkflowRevision = "workflow-revision"
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
            Disposition = "Ready",
            RawJson = """{"Disposition":"Planned"}"""
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
        Assert.Equal(
            """{"Disposition":"Planned"}""",
            Assert.Single(loaded.PlanDocuments).RawJson);
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
                IsOnlyPlannedPublishStep: false),
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

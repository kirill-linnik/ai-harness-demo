using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Data;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Tests;

/// <summary>
/// Migration and restart reconciliation for the host-derived readiness rows. These prove the
/// additive schema upgrade and the fail-closed legacy sweep are safe to repeat.
/// </summary>
public sealed class DeliveryReadinessMigrationTests
{
    [Fact]
    public async Task PreChangeDatabase_UpgradesAdditivelyAndRepeatsWithoutDuplicates()
    {
        var root = NewRoot();
        try
        {
            var factory = Factory(root);
            await using (var database = await factory.CreateDbContextAsync())
            {
                // Simulate a database created before the readiness tables existed.
                await database.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS Flows (
                        Id TEXT NOT NULL CONSTRAINT PK_Flows PRIMARY KEY,
                        Title TEXT NOT NULL,
                        OriginalRequest TEXT NOT NULL
                    );
                    """);
                await database.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS ReviewedPublicationRecords (
                        Id TEXT NOT NULL CONSTRAINT PK_ReviewedPublicationRecords PRIMARY KEY,
                        FlowRunId TEXT NOT NULL,
                        PublicationRootId TEXT NOT NULL,
                        Iteration INTEGER NOT NULL,
                        CandidateFingerprint TEXT NOT NULL,
                        RelativePath TEXT NOT NULL,
                        RemoteRepository TEXT NOT NULL DEFAULT '',
                        BranchName TEXT NOT NULL DEFAULT '',
                        Head TEXT NOT NULL,
                        Tree TEXT NOT NULL,
                        Stage TEXT NOT NULL DEFAULT 'Intent',
                        PullRequestUrl TEXT NOT NULL DEFAULT '',
                        CreatedAt INTEGER NOT NULL,
                        UpdatedAt INTEGER NOT NULL
                    );
                    """);

                await DatabaseInitializer.EnsureReviewedPublicationSchemaAsync(database);
                await DatabaseInitializer.EnsureDeliveryReadinessSchemaAsync(database);
                await DatabaseInitializer.EnsureReviewedPublicationSchemaAsync(database);
                await DatabaseInitializer.EnsureDeliveryReadinessSchemaAsync(database);

                Assert.Equal(1, await CountAsync(database, "DeliveryReadinessSnapshots"));
                Assert.Equal(1, await CountAsync(database, "ReviewedCandidateRecords"));
                Assert.Equal(1, await CountAsync(database, "ReadinessWaiverRecords"));
                Assert.Equal(
                    1,
                    await ColumnCountAsync(
                        database,
                        "ReviewedPublicationRecords",
                        "ReadinessContractHash"));
                Assert.Equal(
                    1,
                    await ColumnCountAsync(
                        database,
                        "ReviewedPublicationRecords",
                        "WaiverSetHash"));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }
    [Fact]
    public async Task LegacyStudioDelivery_ReconcilesFailClosedAndIdempotently()
    {
        var root = NewRoot();
        try
        {
            var factory = Factory(root);
            var flow = new FlowRun
            {
                Title = "Legacy delivery",
                OriginalRequest = "Ship the legacy result.",
                Kind = FlowKind.Delivery,
                ContractVersion = "studio-v2",
                Status = FlowStatus.WaitingForFeedback,
                OutcomeContractJson =
                    """{"Version":"flow-outcome-v1","Goal":"g","Summary":"s","ImplementationDetails":["d"],"Artifacts":[]}"""
            };
            var owner = new FlowStep
            {
                FlowRunId = flow.Id,
                Iteration = 1,
                Sequence = 1,
                AgentId = "builder",
                AgentName = "Builder",
                AgentRole = "external-delivery",
                PlanStepKey = "prepare",
                IsOutcomeOwner = true,
                Status = StepStatus.Completed
            };
            flow.Steps.Add(owner);
            flow.GateRecords.Add(new HandoffGateRecord
            {
                FlowRunId = flow.Id,
                FlowStepId = owner.Id,
                ActionType = HandoffActionType.CustomerReview,
                Decision = HandoffGateDecision.AwaitingHumanApproval,
                TrustLevelAtDecision = HandoffTrustLevel.Gated,
                Summary = "Legacy review awaiting a decision."
            });

            await using (var database = await factory.CreateDbContextAsync())
            {
                await database.Database.EnsureCreatedAsync();
                database.Flows.Add(flow);
                await database.SaveChangesAsync();
            }

            var service = new AiHarnessDemo.Services.DeliveryReadinessService();
            using var gateEngine = new HandoffGateEngine();
            await using (var database = await factory.CreateDbContextAsync())
            {
                gateEngine.RestoreHistory(
                    await database.GateRecords.AsNoTracking().ToListAsync());
                Assert.Equal(1, await service.ReconcileLegacyFlowsAsync(database, gateEngine));
            }
            await using (var database = await factory.CreateDbContextAsync())
            {
                // Repeating the sweep produces no additional rows or events.
                Assert.Equal(0, await service.ReconcileLegacyFlowsAsync(database, gateEngine));
            }

            await using var verification = await factory.CreateDbContextAsync();
            var snapshot = Assert.Single(
                await verification.DeliveryReadinessSnapshots
                    .Where(item => item.FlowRunId == flow.Id)
                    .ToListAsync());
            Assert.Equal(DeliveryReadinessState.Blocked, snapshot.State);
            Assert.Equal(
                DeliveryReadinessReconciliation.LegacyUnverified,
                snapshot.Reconciliation);
            Assert.Single(await verification.ReviewedCandidateRecords
                .Where(item => item.FlowRunId == flow.Id)
                .ToListAsync());
            var gate = Assert.Single(
                await verification.GateRecords
                    .Where(item => item.FlowRunId == flow.Id)
                    .ToListAsync());
            Assert.True(gate.Resolved);
            Assert.False(gate.Approved);
            Assert.Single(await verification.FlowEvents
                .Where(item =>
                    item.FlowRunId == flow.Id &&
                    item.Type == AiHarnessDemo.Services.DeliveryReadinessService
                        .ReconciledEventType)
                .ToListAsync());

            // The legacy assessment is never projected as a green approval.
            var binding = await service.LoadCurrentAsync(verification, flow.Id);
            Assert.NotNull(binding);
            var stored = await verification.Flows
                .AsSplitQuery()
                .Include(item => item.Steps)
                .Include(item => item.GateRecords)
                .SingleAsync(item => item.Id == flow.Id);
            var dto = AiHarnessDemo.Contracts.ApiMappings.ToDeliveryReadinessDto(
                binding!,
                stored);
            Assert.Equal(DeliveryReadinessState.Blocked, dto.State);
            Assert.Equal("Published - readiness unverified", dto.Label);
            Assert.DoesNotContain(DeliveryReadinessAction.Accept, dto.AllowedActions);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task<int> CountAsync(HarnessDbContext database, string table) =>
        await database.Database
            .SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM sqlite_master " +
                $"WHERE type = 'table' AND name = '{table}'")
            .SingleAsync();

    private static async Task<int> ColumnCountAsync(
        HarnessDbContext database,
        string table,
        string column) =>
        await database.Database
            .SqlQueryRaw<int>(
                $"SELECT COUNT(*) AS Value FROM pragma_table_info('{table}') " +
                $"WHERE name = '{column}'")
            .SingleAsync();

    private static string NewRoot()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "delivery-readiness-migration",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static ReviewWorkflowTests.ReviewDbContextFactory Factory(string root) =>
        new(new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "harness.db")};Pooling=False")
            .Options);

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // SQLite can briefly retain a handle on Windows.
        }
    }
}

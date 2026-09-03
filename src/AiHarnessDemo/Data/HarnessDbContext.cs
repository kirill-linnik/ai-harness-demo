using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Services;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AiHarnessDemo.Data;

public sealed class HarnessDbContext(DbContextOptions<HarnessDbContext> options)
    : DbContext(options)
{
    public DbSet<HarnessSettings> Settings => Set<HarnessSettings>();

    public DbSet<AgentRecord> Agents => Set<AgentRecord>();

    public DbSet<FlowRun> Flows => Set<FlowRun>();

    public DbSet<FlowStep> FlowSteps => Set<FlowStep>();

    public DbSet<FlowMessage> FlowMessages => Set<FlowMessage>();

    public DbSet<FlowEvent> FlowEvents => Set<FlowEvent>();

    public DbSet<HarnessLearning> Learnings => Set<HarnessLearning>();

    public DbSet<HandoffGateRecord> GateRecords => Set<HandoffGateRecord>();

    public DbSet<AgentToolCall> AgentToolCalls => Set<AgentToolCall>();

    public DbSet<TaskProfile> TaskProfiles => Set<TaskProfile>();

    public DbSet<ModelCatalogSnapshot> ModelCatalogSnapshots => Set<ModelCatalogSnapshot>();

    public DbSet<ModelCatalogCandidate> ModelCatalogCandidates => Set<ModelCatalogCandidate>();

    public DbSet<RoutingDecision> RoutingDecisions => Set<RoutingDecision>();

    public DbSet<RoutingAlternative> RoutingAlternatives => Set<RoutingAlternative>();

    public DbSet<RoutingObservation> RoutingObservations => Set<RoutingObservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HarnessSettings>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Outcome).HasConversion<string>();
            entity.Property(item => item.ModelSelectionStrategy).HasConversion<string>();
            entity.Property(item => item.MaxHandoffRetries).HasDefaultValue(2);
            entity.Property(item => item.RuntimeMarker).HasColumnName("ExecutionMode");
        });

        modelBuilder.Entity<AgentRecord>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasMaxLength(120);
            entity.Property(item => item.Role).HasMaxLength(120);
            entity.HasIndex(item => item.SortOrder);
        });

        modelBuilder.Entity<FlowRun>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Status).HasConversion<string>();
            entity.Property(item => item.Outcome).HasConversion<string>();
            entity.Property(item => item.ModelSelectionStrategy).HasConversion<string>();
            entity.Property(item => item.RuntimeMarker).HasColumnName("ExecutionMode");
            entity.HasIndex(item => item.CreatedAt);
            entity.HasIndex(item => item.Status);
        });

        modelBuilder.Entity<FlowStep>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Status).HasConversion<string>();
            entity.Property(item => item.Phase).HasConversion<string>();
            entity.HasIndex(item => new { item.FlowRunId, item.Iteration, item.Sequence });
            entity.HasOne(item => item.FlowRun)
                .WithMany(flow => flow.Steps)
                .HasForeignKey(item => item.FlowRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FlowMessage>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Role).HasConversion<string>();
            entity.HasIndex(item => new { item.FlowRunId, item.CreatedAt });
            entity.HasOne(item => item.FlowRun)
                .WithMany(flow => flow.Messages)
                .HasForeignKey(item => item.FlowRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FlowEvent>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.HasIndex(item => new { item.FlowRunId, item.CreatedAt });
            entity.HasOne(item => item.FlowRun)
                .WithMany(flow => flow.Events)
                .HasForeignKey(item => item.FlowRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<HarnessLearning>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.HasIndex(item => item.CreatedAt);
            entity.HasIndex(item => item.AgentId);
        });

        modelBuilder.Entity<HandoffGateRecord>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.ActionType).HasConversion<string>();
            entity.Property(item => item.Decision).HasConversion<string>();
            entity.Property(item => item.TrustLevelAtDecision).HasConversion<string>();
            entity.HasIndex(item => new { item.FlowRunId, item.DecidedAt });
            entity.HasOne<FlowRun>()
                .WithMany(flow => flow.GateRecords)
                .HasForeignKey(item => item.FlowRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentToolCall>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.HasIndex(item => item.FlowStepId);
            entity.HasOne(item => item.FlowStep)
                .WithMany(step => step.ToolCalls)
                .HasForeignKey(item => item.FlowStepId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskProfile>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Risk).HasConversion<string>();
            entity.HasIndex(item => new { item.FlowRunId, item.Iteration, item.Role });
            entity.HasIndex(item => item.FlowStepId).IsUnique();
            entity.HasOne<FlowRun>()
                .WithMany(flow => flow.TaskProfiles)
                .HasForeignKey(item => item.FlowRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ModelCatalogSnapshot>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.HasIndex(item => item.DiscoveredAt);
            entity.HasIndex(item => item.IsCurrent);
        });

        modelBuilder.Entity<ModelCatalogCandidate>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.HasIndex(item => new
            {
                item.ModelCatalogSnapshotId,
                item.Model,
                item.Effort
            }).IsUnique();
            entity.HasOne(item => item.Snapshot)
                .WithMany(snapshot => snapshot.Candidates)
                .HasForeignKey(item => item.ModelCatalogSnapshotId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RoutingDecision>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Strategy).HasConversion<string>();
            entity.HasIndex(item => new { item.FlowStepId, item.Superseded });
            entity.HasOne(item => item.FlowStep)
                .WithMany(step => step.RoutingDecisions)
                .HasForeignKey(item => item.FlowStepId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.TaskProfile)
                .WithMany()
                .HasForeignKey(item => item.TaskProfileId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RoutingAlternative>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.HasIndex(item => new { item.RoutingDecisionId, item.Rank });
            entity.HasOne(item => item.RoutingDecision)
                .WithMany(decision => decision.Alternatives)
                .HasForeignKey(item => item.RoutingDecisionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RoutingObservation>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Risk).HasConversion<string>();
            entity.HasIndex(item => item.RoutingDecisionId);
            entity.HasIndex(item => item.ObservedAt);
            entity.HasIndex(item => new { item.Role, item.Risk });
        });

        var dateTimeOffsetConverter = new ValueConverter<DateTimeOffset, long>(
            value => value.ToUnixTimeMilliseconds(),
            value => DateTimeOffset.FromUnixTimeMilliseconds(value));
        var nullableDateTimeOffsetConverter = new ValueConverter<DateTimeOffset?, long?>(
            value => value.HasValue ? value.Value.ToUnixTimeMilliseconds() : null,
            value => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(dateTimeOffsetConverter);
                }
                else if (property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(nullableDateTimeOffsetConverter);
                }
            }
        }
    }
}

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<HarnessDbContext>>();
        await using var database = await factory.CreateDbContextAsync();

        await database.Database.EnsureCreatedAsync();
        await EnsureSettingsSchemaAsync(database);
        await EnsureFlowRunSchemaAsync(database);
        await EnsureFlowStepSchemaAsync(database);
        await EnsureRoutingSchemaAsync(database);
        await database.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

        var settings = await database.Settings.SingleOrDefaultAsync();
        if (settings is null)
        {
            settings = new HarnessSettings();
            database.Settings.Add(settings);
        }
        settings.RuntimeMarker = "LiveCopilot";

        var legacyHostFlows = await database.Flows
            .Where(flow => flow.RuntimeMarker != "LiveCopilot")
            .ToListAsync();
        foreach (var flow in legacyHostFlows)
        {
            flow.RuntimeMarker = "LiveCopilot";
        }
        await database.SaveChangesAsync();

        var gateEngine = scope.ServiceProvider.GetRequiredService<HandoffGateEngine>();
        gateEngine.RestoreHistory(await database.GateRecords
            .AsNoTracking()
            .OrderBy(item => item.DecidedAt)
            .ToListAsync());

        var catalog = scope.ServiceProvider.GetRequiredService<AgentCatalog>();
        await catalog.SyncAsync();
    }

    internal static async Task EnsureSettingsSchemaAsync(
        HarnessDbContext database,
        CancellationToken cancellationToken = default)
    {
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('Settings') " +
            "WHERE name = 'MaxHandoffRetries';",
            "ALTER TABLE Settings ADD COLUMN MaxHandoffRetries " +
            "INTEGER NOT NULL DEFAULT 2;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('Settings') " +
            "WHERE name = 'ModelSelectionStrategy';",
            "ALTER TABLE Settings ADD COLUMN ModelSelectionStrategy " +
            "TEXT NOT NULL DEFAULT 'MaximumQuality';",
            cancellationToken);
    }

    internal static async Task EnsureFlowStepSchemaAsync(
        HarnessDbContext database,
        CancellationToken cancellationToken = default)
    {
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('FlowSteps') " +
            "WHERE name = 'ExecutionPrompt';",
            "ALTER TABLE FlowSteps ADD COLUMN ExecutionPrompt " +
            "TEXT NOT NULL DEFAULT '';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('FlowSteps') " +
            "WHERE name = 'CopilotSessionId';",
            "ALTER TABLE FlowSteps ADD COLUMN CopilotSessionId TEXT NULL;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('FlowSteps') " +
            "WHERE name = 'CopilotSessionHome';",
            "ALTER TABLE FlowSteps ADD COLUMN CopilotSessionHome " +
            "TEXT NOT NULL DEFAULT '';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('FlowSteps') " +
            "WHERE name = 'ModelEffort';",
            "ALTER TABLE FlowSteps ADD COLUMN ModelEffort TEXT NOT NULL DEFAULT '';",
            cancellationToken);
    }

    internal static Task EnsureFlowRunSchemaAsync(
        HarnessDbContext database,
        CancellationToken cancellationToken = default) =>
        EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('Flows') " +
            "WHERE name = 'ModelSelectionStrategy';",
            "ALTER TABLE Flows ADD COLUMN ModelSelectionStrategy " +
            "TEXT NOT NULL DEFAULT 'MaximumQuality';",
            cancellationToken);

    internal static async Task EnsureRoutingSchemaAsync(
        HarnessDbContext database,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS ModelCatalogSnapshots (
                Id TEXT NOT NULL CONSTRAINT PK_ModelCatalogSnapshots PRIMARY KEY,
                CatalogVersion TEXT NOT NULL,
                CliVersion TEXT NOT NULL,
                DiscoveredAt INTEGER NOT NULL,
                IsCurrent INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_ModelCatalogSnapshots_DiscoveredAt
                ON ModelCatalogSnapshots (DiscoveredAt);
            CREATE INDEX IF NOT EXISTS IX_ModelCatalogSnapshots_IsCurrent
                ON ModelCatalogSnapshots (IsCurrent);

            CREATE TABLE IF NOT EXISTS ModelCatalogCandidates (
                Id TEXT NOT NULL CONSTRAINT PK_ModelCatalogCandidates PRIMARY KEY,
                ModelCatalogSnapshotId TEXT NOT NULL,
                Model TEXT NOT NULL,
                Effort TEXT NOT NULL,
                ModelOrder INTEGER NOT NULL,
                EffortOrder INTEGER NOT NULL,
                IsDefaultModel INTEGER NOT NULL,
                IsDefaultEffort INTEGER NOT NULL,
                PremiumMultiplier REAL NULL,
                Description TEXT NOT NULL,
                MetadataConfidence TEXT NOT NULL,
                Enabled INTEGER NOT NULL,
                CONSTRAINT FK_ModelCatalogCandidates_ModelCatalogSnapshots
                    FOREIGN KEY (ModelCatalogSnapshotId)
                    REFERENCES ModelCatalogSnapshots (Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ModelCatalogCandidates_Catalog_Model_Effort
                ON ModelCatalogCandidates (ModelCatalogSnapshotId, Model, Effort);

            CREATE TABLE IF NOT EXISTS TaskProfiles (
                Id TEXT NOT NULL CONSTRAINT PK_TaskProfiles PRIMARY KEY,
                FlowRunId TEXT NOT NULL,
                Iteration INTEGER NOT NULL,
                FlowStepId TEXT NULL,
                Version TEXT NOT NULL,
                Role TEXT NOT NULL,
                Complexity INTEGER NOT NULL,
                ReasoningDepth INTEGER NOT NULL,
                ContextDemand INTEGER NOT NULL,
                ToolIntensity INTEGER NOT NULL,
                TaskTypeTagsJson TEXT NOT NULL,
                Risk TEXT NOT NULL,
                RiskReason TEXT NOT NULL,
                Confidence REAL NOT NULL,
                RationalesJson TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL
            );
            DROP INDEX IF EXISTS IX_TaskProfiles_Flow_Iteration_Role;
            CREATE INDEX IF NOT EXISTS IX_TaskProfiles_Flow_Iteration_Role
                ON TaskProfiles (FlowRunId, Iteration, Role);
            DROP INDEX IF EXISTS IX_TaskProfiles_FlowStepId;
            CREATE UNIQUE INDEX IF NOT EXISTS IX_TaskProfiles_FlowStepId
                ON TaskProfiles (FlowStepId)
                WHERE FlowStepId IS NOT NULL;

            CREATE TABLE IF NOT EXISTS RoutingDecisions (
                Id TEXT NOT NULL CONSTRAINT PK_RoutingDecisions PRIMARY KEY,
                FlowStepId TEXT NOT NULL,
                TaskProfileId TEXT NOT NULL,
                ModelCatalogSnapshotId TEXT NOT NULL,
                SupersedesRoutingDecisionId TEXT NULL,
                Superseded INTEGER NOT NULL,
                RerouteCount INTEGER NOT NULL,
                SelectedModel TEXT NOT NULL,
                SelectedEffort TEXT NOT NULL,
                Strategy TEXT NOT NULL,
                PredictedQuality REAL NOT NULL,
                PredictedAcceptedTimeSeconds REAL NOT NULL,
                PredictedPremiumRequests REAL NOT NULL,
                PremiumUseEstimated INTEGER NOT NULL,
                Confidence REAL NOT NULL,
                Uncertainty REAL NOT NULL,
                Exploration INTEGER NOT NULL,
                Reason TEXT NOT NULL,
                AlgorithmVersion TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_RoutingDecisions_FlowStep_Superseded
                ON RoutingDecisions (FlowStepId, Superseded);

            CREATE TABLE IF NOT EXISTS RoutingAlternatives (
                Id TEXT NOT NULL CONSTRAINT PK_RoutingAlternatives PRIMARY KEY,
                RoutingDecisionId TEXT NOT NULL,
                Model TEXT NOT NULL,
                Effort TEXT NOT NULL,
                Rank INTEGER NOT NULL,
                PredictedQuality REAL NOT NULL,
                PredictedAcceptedTimeSeconds REAL NOT NULL,
                PredictedPremiumRequests REAL NOT NULL,
                Confidence REAL NOT NULL,
                Reason TEXT NOT NULL,
                CONSTRAINT FK_RoutingAlternatives_RoutingDecisions
                    FOREIGN KEY (RoutingDecisionId)
                    REFERENCES RoutingDecisions (Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_RoutingAlternatives_Decision_Rank
                ON RoutingAlternatives (RoutingDecisionId, Rank);

            CREATE TABLE IF NOT EXISTS RoutingObservations (
                Id TEXT NOT NULL CONSTRAINT PK_RoutingObservations PRIMARY KEY,
                RoutingDecisionId TEXT NOT NULL,
                FlowStepId TEXT NOT NULL,
                Role TEXT NOT NULL,
                TaskTypeTagsJson TEXT NOT NULL,
                Complexity INTEGER NOT NULL,
                ReasoningDepth INTEGER NOT NULL,
                ContextDemand INTEGER NOT NULL,
                ToolIntensity INTEGER NOT NULL,
                Risk TEXT NOT NULL,
                Accepted INTEGER NULL,
                AvailabilityFailure INTEGER NOT NULL,
                EvidenceWeight REAL NOT NULL DEFAULT 1,
                DurationMilliseconds INTEGER NOT NULL,
                ExecutionAttempts INTEGER NOT NULL,
                EstimatedPremiumRequests REAL NOT NULL,
                OutcomeKind TEXT NOT NULL,
                ObservedAt INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_RoutingObservations_RoutingDecisionId
                ON RoutingObservations (RoutingDecisionId);
            CREATE INDEX IF NOT EXISTS IX_RoutingObservations_ObservedAt
                ON RoutingObservations (ObservedAt);
            CREATE INDEX IF NOT EXISTS IX_RoutingObservations_Role_Risk
                ON RoutingObservations (Role, Risk);
            """;
        await database.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('RoutingObservations') " +
            "WHERE name = 'EvidenceWeight';",
            "ALTER TABLE RoutingObservations ADD COLUMN EvidenceWeight " +
            "REAL NOT NULL DEFAULT 1;",
            cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        HarnessDbContext database,
        string probeSql,
        string migrationSql,
        CancellationToken cancellationToken)
    {
        var connection = database.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await database.Database.OpenConnectionAsync(cancellationToken);
        }
        try
        {
            await using var probe = connection.CreateCommand();
            probe.CommandText = probeSql;
            var exists = Convert.ToInt64(
                await probe.ExecuteScalarAsync(cancellationToken));
            if (exists > 0)
            {
                return;
            }

            await using var migration = connection.CreateCommand();
            migration.CommandText = migrationSql;
            await migration.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (shouldClose)
            {
                await database.Database.CloseConnectionAsync();
            }
        }
    }
}

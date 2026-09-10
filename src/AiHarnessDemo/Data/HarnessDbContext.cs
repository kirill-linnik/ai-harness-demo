using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Verification;
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

    public DbSet<FlowAgentSnapshot> FlowAgentSnapshots => Set<FlowAgentSnapshot>();

    public DbSet<FlowPlanDocument> FlowPlanDocuments => Set<FlowPlanDocument>();

    public DbSet<ReviewedPublicationRecord> ReviewedPublicationRecords =>
        Set<ReviewedPublicationRecord>();

    public DbSet<DeliveryReadinessSnapshotRecord> DeliveryReadinessSnapshots =>
        Set<DeliveryReadinessSnapshotRecord>();

    public DbSet<ReviewedCandidateRecord> ReviewedCandidateRecords =>
        Set<ReviewedCandidateRecord>();

    public DbSet<ReadinessWaiverRecord> ReadinessWaiverRecords =>
        Set<ReadinessWaiverRecord>();

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
            entity.Property(item => item.DefinitionStatus)
                .HasConversion<string>()
                .HasDefaultValue(AgentDefinitionStatus.Valid);
            entity.Property(item => item.ValidationError).HasDefaultValue(string.Empty);
            entity.Property(item => item.Required).HasDefaultValue(false);
            entity.Property(item => item.Switchable).HasDefaultValue(true);
            entity.Property(item => item.DefinitionHash).HasDefaultValue(string.Empty);
            entity.HasIndex(item => item.SortOrder);
        });

        modelBuilder.Entity<FlowRun>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Kind).HasConversion<string>();
            entity.Property(item => item.LinkKind).HasConversion<string>();
            entity.Property(item => item.ContractVersion).HasDefaultValue("legacy-v1");
            entity.Property(item => item.AgentCatalogRevision).HasDefaultValue(string.Empty);
            entity.Property(item => item.OutcomeContractJson).HasDefaultValue(string.Empty);
            entity.Property(item => item.CurrentBlockerCode).HasMaxLength(120);
            entity.Property(item => item.CurrentBlockerSummary).HasMaxLength(
                TeamPlanValidator.MaximumQualificationTextCharacters);
            entity.Property(item => item.CurrentBlockerDataJson).HasMaxLength(65_536);
            entity.Property(item => item.CustomerBlockerMessage).HasMaxLength(
                IntakeV2Parser.MaximumCustomerReplyCharacters);
            entity.Property(item => item.Status).HasConversion<string>();
            entity.Property(item => item.Outcome).HasConversion<string>();
            entity.Property(item => item.ModelSelectionStrategy).HasConversion<string>();
            entity.Property(item => item.RuntimeMarker).HasColumnName("ExecutionMode");
            entity.Property(item => item.OutcomeVerificationJson).HasDefaultValue(string.Empty);
            entity.HasIndex(item => item.CreatedAt);
            entity.HasIndex(item => item.Status);
            entity.HasIndex(item => item.ParentFlowRunId);
            entity.HasIndex(item => new
                {
                    item.ParentFlowRunId,
                    item.ParentIteration,
                    item.LinkKind
                })
                .HasDatabaseName("IX_Flows_UniqueLinkedSuccessor")
                .IsUnique()
                .HasFilter(
                    "\"ParentFlowRunId\" IS NOT NULL AND \"LinkKind\" IS NOT NULL");
            entity.HasOne(item => item.ParentFlowRun)
                .WithMany(flow => flow.LinkedFlowRuns)
                .HasForeignKey(item => item.ParentFlowRunId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FlowStep>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Status).HasConversion<string>();
            entity.Property(item => item.Phase).HasConversion<string>();
            entity.Property(item => item.Kind).HasConversion<string>();
            entity.Property(item => item.PlanStepKey).HasDefaultValue(string.Empty);
            entity.Property(item => item.PlanDutiesJson).HasDefaultValue("[]");
            entity.Property(item => item.PlanStage)
                .HasConversion<string>()
                .HasDefaultValue(PlanStage.BeforeReview);
            entity.Property(item => item.IsOutcomeOwner).HasDefaultValue(false);
            entity.Property(item => item.PermissionProfile)
                .HasConversion<string>();
            entity.Property(item => item.InvocationKind)
                .HasConversion<string>();
            entity.Property(item => item.EffectivePermissionJson).HasDefaultValue(string.Empty);
            entity.Property(item => item.WorkflowRevision).HasDefaultValue(string.Empty);
            entity.Property(item => item.ReviewClassificationStateJson)
                .HasDefaultValue(string.Empty);
            entity.Property(item => item.OutcomePlanHash).HasDefaultValue(string.Empty);
            entity.Property(item => item.RemotePublicationAllowed).HasDefaultValue(false);
            entity.HasIndex(item => new { item.FlowRunId, item.Iteration, item.Sequence });
            entity.HasIndex(item => item.RetryOfStepId);
            entity.HasIndex(item => item.DependsOnStepId);
            entity.HasIndex(item => item.PushbackRootStepId);
            entity.HasIndex(item => item.PreMortemOriginStepId);
            entity.HasIndex(item => item.PreMortemTargetStepId);
            entity.HasIndex(item => item.PreMortemReviewStepId);
            entity.HasIndex(item => item.StableSemanticRootId);
            entity.HasIndex(item => new { item.FlowRunId, item.Iteration, item.PlanStepKey });
            entity.HasIndex(item => new
            {
                item.FlowRunId,
                item.Iteration,
                item.Kind,
                item.OutcomeQaRound
            });
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
            entity.Property(item => item.DataJson).HasMaxLength(
                IntakeV2Parser.MaximumJsonCharacters);
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
            entity.Property(item => item.ReviewDecision).HasConversion<string>();
            entity.Property(item => item.TrustLevelAtDecision).HasConversion<string>();
            entity.HasIndex(item => new { item.FlowRunId, item.DecidedAt });
            entity.HasIndex(item => new { item.FlowRunId, item.ActionType })
                .HasDatabaseName("IX_GateRecords_UnresolvedCustomerReview")
                .IsUnique()
                .HasFilter(
                    "\"ActionType\" = 'CustomerReview' AND \"Resolved\" = 0");
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
            entity.Property(item => item.PreMortemAfter).HasDefaultValue(false);
            entity.Property(item => item.PlanStepKey).HasDefaultValue(string.Empty);
            entity.Property(item => item.AgentId).HasDefaultValue(string.Empty);
            entity.HasIndex(item => new { item.FlowRunId, item.Iteration, item.Role });
            entity.HasIndex(item => new { item.FlowRunId, item.Iteration, item.PlanStepKey });
            entity.HasIndex(item => item.FlowStepId).IsUnique();
            entity.HasOne<FlowRun>()
                .WithMany(flow => flow.TaskProfiles)
                .HasForeignKey(item => item.FlowRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FlowAgentSnapshot>(entity =>
        {
            entity.HasKey(item => new { item.FlowRunId, item.AgentId });
            entity.Property(item => item.AgentId).HasMaxLength(120);
            entity.HasIndex(item => item.AgentId);
            entity.HasOne(item => item.FlowRun)
                .WithMany(flow => flow.AgentSnapshots)
                .HasForeignKey(item => item.FlowRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FlowPlanDocument>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.HasIndex(item => new { item.FlowRunId, item.Iteration }).IsUnique();
            entity.HasOne(item => item.FlowRun)
                .WithMany(flow => flow.PlanDocuments)
                .HasForeignKey(item => item.FlowRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReviewedPublicationRecord>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Stage).HasConversion<string>();
            entity.Property(item => item.CandidateFingerprint).HasMaxLength(128);
            entity.Property(item => item.RelativePath).HasMaxLength(1_024);
            entity.Property(item => item.RemoteRepository)
                .HasMaxLength(512)
                .HasDefaultValue(string.Empty);
            entity.Property(item => item.BranchName)
                .HasMaxLength(512)
                .HasDefaultValue(string.Empty);
            entity.Property(item => item.Head).HasMaxLength(128);
            entity.Property(item => item.Tree).HasMaxLength(128);
            entity.Property(item => item.PullRequestUrl)
                .HasMaxLength(1_024)
                .HasDefaultValue(string.Empty);
            entity.Property(item => item.ReadinessContractHash)
                .HasMaxLength(128)
                .HasDefaultValue(string.Empty);
            entity.Property(item => item.WaiverSetHash)
                .HasMaxLength(128)
                .HasDefaultValue(string.Empty);
            entity.HasIndex(item => new
                {
                    item.FlowRunId,
                    item.PublicationRootId,
                    item.RelativePath
                })
                .HasDatabaseName("IX_ReviewedPublicationRecords_Repository")
                .IsUnique();
            entity.HasOne<FlowRun>()
                .WithMany()
                .HasForeignKey(item => item.FlowRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeliveryReadinessSnapshotRecord>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.State).HasConversion<string>();
            entity.Property(item => item.Reconciliation).HasConversion<string>();
            entity.Property(item => item.CandidateFingerprint).HasMaxLength(128);
            entity.Property(item => item.AcceptancePlanHash).HasMaxLength(128);
            entity.Property(item => item.OutcomeContractHash).HasMaxLength(128);
            entity.Property(item => item.QaContractHash)
                .HasMaxLength(128)
                .HasDefaultValue(string.Empty);
            entity.Property(item => item.ContractHash).HasMaxLength(128);
            entity.HasIndex(item => new
                {
                    item.FlowRunId,
                    item.Iteration,
                    item.Revision
                })
                .HasDatabaseName("IX_DeliveryReadinessSnapshots_Revision")
                .IsUnique();
            entity.HasIndex(item => item.FlowRunId)
                .HasDatabaseName("IX_DeliveryReadinessSnapshots_Active")
                .IsUnique()
                .HasFilter("\"Active\" = 1");
            entity.HasOne<FlowRun>()
                .WithMany()
                .HasForeignKey(item => item.FlowRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReviewedCandidateRecord>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.CandidateFingerprint).HasMaxLength(128);
            entity.Property(item => item.OutcomeContractHash).HasMaxLength(128);
            entity.Property(item => item.AcceptancePlanHash).HasMaxLength(128);
            entity.Property(item => item.ReadinessContractHash).HasMaxLength(128);
            entity.HasIndex(item => new
                {
                    item.FlowRunId,
                    item.Iteration,
                    item.CandidateFingerprint,
                    item.ReadinessContractHash
                })
                .HasDatabaseName("IX_ReviewedCandidateRecords_Identity")
                .IsUnique();
            entity.HasIndex(item => item.FlowRunId)
                .HasDatabaseName("IX_ReviewedCandidateRecords_Active")
                .IsUnique()
                .HasFilter("\"Active\" = 1");
            entity.HasOne<FlowRun>()
                .WithMany()
                .HasForeignKey(item => item.FlowRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReadinessWaiverRecord>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.RiskId).HasMaxLength(32);
            entity.Property(item => item.Actor).HasMaxLength(200);
            entity.Property(item => item.Acknowledgement).HasMaxLength(4_000);
            entity.Property(item => item.ReadinessContractHash).HasMaxLength(128);
            entity.HasIndex(item => new
                {
                    item.ReviewedCandidateId,
                    item.ReadinessContractHash,
                    item.RiskId
                })
                .HasDatabaseName("IX_ReadinessWaiverRecords_Receipt")
                .IsUnique();
            entity.HasOne<FlowRun>()
                .WithMany()
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
        await EnsureAgentToolCallSchemaAsync(database);
        await EnsureRoutingSchemaAsync(database);
        await EnsureSliceOneSchemaAsync(database);
        await EnsureReviewedPublicationSchemaAsync(database);
        await EnsureDeliveryReadinessSchemaAsync(database);
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
        await catalog.LoadAsync();
        await scope.ServiceProvider
            .GetRequiredService<FlowAgentSnapshotService>()
            .MigrateLegacyNonterminalAsync();

        // Fail-closed reconciliation of pre-readiness studio-v2 Delivery flows. Repeating this on
        // every start is idempotent: unique keys prevent duplicate rows, gates, and events.
        await using (var reconciliationDatabase = await factory.CreateDbContextAsync())
        {
            await using var reconciliationTransaction =
                await reconciliationDatabase.Database.BeginTransactionAsync();
            await scope.ServiceProvider
                .GetRequiredService<DeliveryReadinessService>()
                .ReconcileLegacyFlowsAsync(reconciliationDatabase, gateEngine);
            await reconciliationTransaction.CommitAsync();
        }
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
            "WHERE name = 'PlanStepKey';",
            "ALTER TABLE FlowSteps ADD COLUMN PlanStepKey TEXT NOT NULL DEFAULT '';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('FlowSteps') " +
            "WHERE name = 'PlanDutiesJson';",
            "ALTER TABLE FlowSteps ADD COLUMN PlanDutiesJson TEXT NOT NULL DEFAULT '[]';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('FlowSteps') " +
            "WHERE name = 'PlanStage';",
            "ALTER TABLE FlowSteps ADD COLUMN PlanStage " +
            "TEXT NOT NULL DEFAULT 'BeforeReview';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('FlowSteps') " +
            "WHERE name = 'IsOutcomeOwner';",
            "ALTER TABLE FlowSteps ADD COLUMN IsOutcomeOwner INTEGER NOT NULL DEFAULT 0;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('FlowSteps') " +
            "WHERE name = 'PermissionProfile';",
            "ALTER TABLE FlowSteps ADD COLUMN PermissionProfile " +
            "TEXT NOT NULL DEFAULT 'WorkspaceWrite';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('FlowSteps') " +
            "WHERE name = 'InvocationKind';",
            "ALTER TABLE FlowSteps ADD COLUMN InvocationKind " +
            "TEXT NOT NULL DEFAULT 'Worker';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('FlowSteps') " +
            "WHERE name = 'EffectivePermissionJson';",
            "ALTER TABLE FlowSteps ADD COLUMN EffectivePermissionJson " +
            "TEXT NOT NULL DEFAULT '';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('FlowSteps') " +
            "WHERE name = 'WorkflowRevision';",
            "ALTER TABLE FlowSteps ADD COLUMN WorkflowRevision TEXT NOT NULL DEFAULT '';",
            cancellationToken);
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
            "WHERE name = 'ReviewClassificationStateJson';",
            "ALTER TABLE FlowSteps ADD COLUMN ReviewClassificationStateJson " +
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
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('FlowSteps') " +
            "WHERE name = 'RemotePublicationAllowed';",
            "ALTER TABLE FlowSteps ADD COLUMN RemotePublicationAllowed " +
            "INTEGER NOT NULL DEFAULT 0;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('FlowSteps') " +
            "WHERE name = 'RetryOfStepId';",
            "ALTER TABLE FlowSteps ADD COLUMN RetryOfStepId TEXT NULL;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('FlowSteps') " +
            "WHERE name = 'DependsOnStepId';",
            "ALTER TABLE FlowSteps ADD COLUMN DependsOnStepId TEXT NULL;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('FlowSteps') " +
            "WHERE name = 'PushbackRootStepId';",
            "ALTER TABLE FlowSteps ADD COLUMN PushbackRootStepId TEXT NULL;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('FlowSteps') " +
            "WHERE name = 'PreMortemOriginStepId';",
            "ALTER TABLE FlowSteps ADD COLUMN PreMortemOriginStepId TEXT NULL;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('FlowSteps') " +
            "WHERE name = 'PreMortemTargetStepId';",
            "ALTER TABLE FlowSteps ADD COLUMN PreMortemTargetStepId TEXT NULL;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('FlowSteps') " +
            "WHERE name = 'PreMortemReviewStepId';",
            "ALTER TABLE FlowSteps ADD COLUMN PreMortemReviewStepId TEXT NULL;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('FlowSteps') " +
            "WHERE name = 'Kind';",
            "ALTER TABLE FlowSteps ADD COLUMN Kind " +
            "TEXT NOT NULL DEFAULT 'Standard';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('FlowSteps') " +
            "WHERE name = 'OutcomeQaRound';",
            "ALTER TABLE FlowSteps ADD COLUMN OutcomeQaRound INTEGER NULL;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('FlowSteps') " +
            "WHERE name = 'OutcomePlanHash';",
            "ALTER TABLE FlowSteps ADD COLUMN OutcomePlanHash " +
            "TEXT NOT NULL DEFAULT '';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('FlowSteps') " +
            "WHERE name = 'StableSemanticRootId';",
            "ALTER TABLE FlowSteps ADD COLUMN StableSemanticRootId TEXT NULL;",
            cancellationToken);
        await database.Database.ExecuteSqlRawAsync(
            """
            UPDATE FlowSteps
            SET InvocationKind = CASE
                WHEN PlanStepKey = 'team-plan' THEN 'Planning'
                WHEN PlanStepKey = 'account-manager:refinement' THEN 'Intake'
                WHEN PlanStepKey = 'account-manager:qualification-blocker'
                    THEN 'BlockerExplanation'
                WHEN PlanStepKey LIKE 'account-manager:review-classification:%'
                    THEN 'ReviewClassification'
                WHEN PlanStepKey LIKE 'pre-mortem:%'
                     AND PreMortemTargetStepId IS NOT NULL
                     AND PreMortemReviewStepId IS NULL
                    THEN 'PreMortem'
                ELSE InvocationKind
            END;
            """,
            cancellationToken);
        await database.Database.ExecuteSqlRawAsync(
            """
            UPDATE FlowSteps AS retry
            SET RetryOfStepId = (
                SELECT COALESCE(blocked.RetryOfStepId, blocked.Id)
                FROM FlowSteps AS blocked
                WHERE blocked.FlowRunId = retry.FlowRunId
                  AND blocked.Iteration = retry.Iteration
                  AND blocked.AgentId = retry.AgentId
                  AND blocked.Sequence < retry.Sequence
                  AND blocked.Status = 'Pushback'
                ORDER BY blocked.Sequence DESC, blocked.Attempt DESC
                LIMIT 1
            )
            WHERE retry.RetryOfStepId IS NULL
              AND (
                  retry.Label LIKE 'Retry after % revision'
                  OR retry.Label = 'Re-validate corrected handoff'
              );

            UPDATE FlowSteps AS retry
            SET RetryOfStepId = (
                SELECT COALESCE(failed.RetryOfStepId, failed.Id)
                FROM FlowSteps AS failed
                WHERE failed.FlowRunId = retry.FlowRunId
                  AND failed.Iteration = retry.Iteration
                  AND failed.AgentId = retry.AgentId
                  AND failed.Sequence < retry.Sequence
                  AND failed.Status IN ('Failed', 'Pushback')
                ORDER BY failed.Sequence DESC, failed.Attempt DESC
                LIMIT 1
            )
            WHERE retry.RetryOfStepId IS NULL
              AND retry.Label LIKE 'Manual restart of %'
              AND EXISTS (
                  SELECT 1
                  FROM FlowSteps AS failed
                  WHERE failed.FlowRunId = retry.FlowRunId
                    AND failed.Iteration = retry.Iteration
                    AND failed.AgentId = retry.AgentId
                    AND failed.Sequence < retry.Sequence
                    AND failed.Status IN ('Failed', 'Pushback')
              );

            UPDATE FlowSteps AS retry
            SET DependsOnStepId = COALESCE(
                (
                    SELECT effective.Id
                    FROM FlowSteps AS effective
                    WHERE effective.FlowRunId = retry.FlowRunId
                      AND effective.Iteration = retry.Iteration
                      AND effective.Sequence < retry.Sequence
                      AND effective.Status = 'Completed'
                      AND (
                          effective.Id = (
                              SELECT revision.Id
                              FROM FlowSteps AS revision
                              WHERE revision.FlowRunId = retry.FlowRunId
                                AND revision.Iteration = retry.Iteration
                                AND revision.Sequence < retry.Sequence
                                AND (
                                    (retry.Label LIKE 'Retry after % revision'
                                     AND revision.Label =
                                         'Revision after ' || retry.AgentName || ' pushback')
                                    OR
                                    (retry.Label = 'Re-validate corrected handoff'
                                     AND revision.Label = 'Revision after QA pushback')
                                )
                              ORDER BY revision.Sequence DESC, revision.Attempt DESC
                              LIMIT 1
                          )
                          OR effective.RetryOfStepId = (
                              SELECT revision.Id
                              FROM FlowSteps AS revision
                              WHERE revision.FlowRunId = retry.FlowRunId
                                AND revision.Iteration = retry.Iteration
                                AND revision.Sequence < retry.Sequence
                                AND (
                                    (retry.Label LIKE 'Retry after % revision'
                                     AND revision.Label =
                                         'Revision after ' || retry.AgentName || ' pushback')
                                    OR
                                    (retry.Label = 'Re-validate corrected handoff'
                                     AND revision.Label = 'Revision after QA pushback')
                                )
                              ORDER BY revision.Sequence DESC, revision.Attempt DESC
                              LIMIT 1
                          )
                      )
                    ORDER BY effective.Sequence DESC, effective.Attempt DESC
                    LIMIT 1
                ),
                (
                    SELECT revision.Id
                    FROM FlowSteps AS revision
                    WHERE revision.FlowRunId = retry.FlowRunId
                      AND revision.Iteration = retry.Iteration
                      AND revision.Sequence < retry.Sequence
                      AND (
                          (retry.Label LIKE 'Retry after % revision'
                           AND revision.Label =
                               'Revision after ' || retry.AgentName || ' pushback')
                          OR
                          (retry.Label = 'Re-validate corrected handoff'
                           AND revision.Label = 'Revision after QA pushback')
                      )
                    ORDER BY revision.Sequence DESC, revision.Attempt DESC
                    LIMIT 1
                )
            )
            WHERE retry.DependsOnStepId IS NULL
              AND (
                  retry.Label LIKE 'Retry after % revision'
                  OR retry.Label = 'Re-validate corrected handoff'
              )
              AND EXISTS (
                  SELECT 1
                  FROM FlowSteps AS revision
                  WHERE revision.FlowRunId = retry.FlowRunId
                    AND revision.Iteration = retry.Iteration
                    AND revision.Sequence < retry.Sequence
                    AND (
                        (retry.Label LIKE 'Retry after % revision'
                         AND revision.Label =
                             'Revision after ' || retry.AgentName || ' pushback')
                        OR
                        (retry.Label = 'Re-validate corrected handoff'
                         AND revision.Label = 'Revision after QA pushback')
                    )
              );

            UPDATE FlowSteps AS retry
            SET PushbackRootStepId = (
                SELECT COALESCE(blocked.PushbackRootStepId, blocked.Id)
                FROM FlowSteps AS blocked
                WHERE blocked.FlowRunId = retry.FlowRunId
                  AND blocked.Iteration = retry.Iteration
                  AND blocked.AgentId = retry.AgentId
                  AND blocked.Sequence < retry.Sequence
                  AND blocked.Status = 'Pushback'
                ORDER BY blocked.Sequence DESC, blocked.Attempt DESC
                LIMIT 1
            )
            WHERE retry.PushbackRootStepId IS NULL
              AND (
                  retry.Label LIKE 'Retry after % revision'
                  OR retry.Label = 'Re-validate corrected handoff'
              );

            UPDATE FlowSteps AS retry
            SET PushbackRootStepId = (
                SELECT COALESCE(
                    parent.PushbackRootStepId,
                    CASE
                        WHEN parent.Status = 'Pushback' THEN parent.Id
                        ELSE NULL
                    END)
                FROM FlowSteps AS parent
                WHERE parent.Id = retry.RetryOfStepId
            )
            WHERE retry.PushbackRootStepId IS NULL
              AND retry.Label LIKE 'Manual restart of %'
              AND retry.RetryOfStepId IS NOT NULL;

            CREATE INDEX IF NOT EXISTS IX_FlowSteps_RetryOfStepId
                ON FlowSteps (RetryOfStepId);
            CREATE INDEX IF NOT EXISTS IX_FlowSteps_DependsOnStepId
                ON FlowSteps (DependsOnStepId);
            CREATE INDEX IF NOT EXISTS IX_FlowSteps_PushbackRootStepId
                ON FlowSteps (PushbackRootStepId);
            CREATE INDEX IF NOT EXISTS IX_FlowSteps_PreMortemOriginStepId
                ON FlowSteps (PreMortemOriginStepId);
            CREATE INDEX IF NOT EXISTS IX_FlowSteps_PreMortemTargetStepId
                ON FlowSteps (PreMortemTargetStepId);
            CREATE INDEX IF NOT EXISTS IX_FlowSteps_PreMortemReviewStepId
                ON FlowSteps (PreMortemReviewStepId);
            CREATE INDEX IF NOT EXISTS IX_FlowSteps_StableSemanticRootId
                ON FlowSteps (StableSemanticRootId);
            CREATE INDEX IF NOT EXISTS IX_FlowSteps_Flow_Iteration_PlanStepKey
                ON FlowSteps (FlowRunId, Iteration, PlanStepKey);
            CREATE INDEX IF NOT EXISTS IX_FlowSteps_Flow_Iteration_Kind_QaRound
                ON FlowSteps (FlowRunId, Iteration, Kind, OutcomeQaRound);
            """,
            cancellationToken);
        for (var pass = 0; pass < 10; pass++)
        {
            await database.Database.ExecuteSqlRawAsync(
                """
                UPDATE FlowSteps AS retry
                SET RetryOfStepId = (
                    SELECT parent.RetryOfStepId
                    FROM FlowSteps AS parent
                    WHERE parent.Id = retry.RetryOfStepId
                )
                WHERE EXISTS (
                    SELECT 1
                    FROM FlowSteps AS parent
                    WHERE parent.Id = retry.RetryOfStepId
                      AND parent.RetryOfStepId IS NOT NULL
                      AND parent.RetryOfStepId <> retry.RetryOfStepId
                );

                UPDATE FlowSteps AS retry
                SET PushbackRootStepId = (
                    SELECT parent.PushbackRootStepId
                    FROM FlowSteps AS parent
                    WHERE parent.Id = retry.RetryOfStepId
                )
                WHERE retry.PushbackRootStepId IS NULL
                  AND EXISTS (
                      SELECT 1
                      FROM FlowSteps AS parent
                      WHERE parent.Id = retry.RetryOfStepId
                        AND parent.PushbackRootStepId IS NOT NULL
                  );
                """,
                cancellationToken);
        }

        await database.Database.ExecuteSqlRawAsync(
            """
            UPDATE FlowSteps
            SET Status = 'Failed',
                Phase = 'Failed'
            WHERE Status = 'Completed'
              AND Label = 'Correct Team Lead task profiles'
              AND EXISTS (
                  SELECT 1
                  FROM FlowEvents
                  WHERE FlowEvents.FlowStepId = FlowSteps.Id
                    AND FlowEvents.Type = 'profile.validation-failed'
              );
            """,
            cancellationToken);
        await database.Database.ExecuteSqlRawAsync(
            """
            UPDATE FlowSteps
            SET StableSemanticRootId = COALESCE(RetryOfStepId, Id)
            WHERE StableSemanticRootId IS NULL;
            """,
            cancellationToken);
        await BackfillFlowStepMetadataAsync(database, cancellationToken);
    }

    internal static async Task EnsureAgentToolCallSchemaAsync(
        HarnessDbContext database,
        CancellationToken cancellationToken = default)
    {
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('AgentToolCalls') " +
            "WHERE name = 'ToolType';",
            "ALTER TABLE AgentToolCalls ADD COLUMN ToolType " +
            "TEXT NOT NULL DEFAULT 'Unknown';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('AgentToolCalls') " +
            "WHERE name = 'NormalizedCommand';",
            "ALTER TABLE AgentToolCalls ADD COLUMN NormalizedCommand " +
            "TEXT NOT NULL DEFAULT '';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('AgentToolCalls') " +
            "WHERE name = 'NormalizedArguments';",
            "ALTER TABLE AgentToolCalls ADD COLUMN NormalizedArguments " +
            "TEXT NOT NULL DEFAULT '';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('AgentToolCalls') " +
            "WHERE name = 'WorkingDirectory';",
            "ALTER TABLE AgentToolCalls ADD COLUMN WorkingDirectory " +
            "TEXT NOT NULL DEFAULT '';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('AgentToolCalls') " +
            "WHERE name = 'ExitCode';",
            "ALTER TABLE AgentToolCalls ADD COLUMN ExitCode INTEGER NULL;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('AgentToolCalls') " +
            "WHERE name = 'ResultDigest';",
            "ALTER TABLE AgentToolCalls ADD COLUMN ResultDigest " +
            "TEXT NOT NULL DEFAULT '';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('AgentToolCalls') " +
            "WHERE name = 'ResultSummary';",
            "ALTER TABLE AgentToolCalls ADD COLUMN ResultSummary " +
            "TEXT NOT NULL DEFAULT '';",
            cancellationToken);
    }

    internal static async Task EnsureFlowRunSchemaAsync(
        HarnessDbContext database,
        CancellationToken cancellationToken = default)
    {
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('Flows') WHERE name = 'Kind';",
            "ALTER TABLE Flows ADD COLUMN Kind TEXT NOT NULL DEFAULT 'Delivery';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('Flows') " +
            "WHERE name = 'ContractVersion';",
            "ALTER TABLE Flows ADD COLUMN ContractVersion " +
            "TEXT NOT NULL DEFAULT 'legacy-v1';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('Flows') " +
            "WHERE name = 'ParentFlowRunId';",
            "ALTER TABLE Flows ADD COLUMN ParentFlowRunId TEXT NULL;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('Flows') " +
            "WHERE name = 'ParentIteration';",
            "ALTER TABLE Flows ADD COLUMN ParentIteration INTEGER NULL;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('Flows') WHERE name = 'LinkKind';",
            "ALTER TABLE Flows ADD COLUMN LinkKind TEXT NULL;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('Flows') " +
            "WHERE name = 'AgentCatalogRevision';",
            "ALTER TABLE Flows ADD COLUMN AgentCatalogRevision TEXT NOT NULL DEFAULT '';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('Flows') " +
            "WHERE name = 'OutcomeOwnerPlanStepKey';",
            "ALTER TABLE Flows ADD COLUMN OutcomeOwnerPlanStepKey TEXT NULL;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('Flows') " +
            "WHERE name = 'PublicationPlanStepKey';",
            "ALTER TABLE Flows ADD COLUMN PublicationPlanStepKey TEXT NULL;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('Flows') " +
            "WHERE name = 'OutcomeContractJson';",
            "ALTER TABLE Flows ADD COLUMN OutcomeContractJson TEXT NOT NULL DEFAULT '';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('Flows') " +
            "WHERE name = 'CurrentBlockerCode';",
            "ALTER TABLE Flows ADD COLUMN CurrentBlockerCode TEXT NULL;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('Flows') " +
            "WHERE name = 'CurrentBlockerSummary';",
            "ALTER TABLE Flows ADD COLUMN CurrentBlockerSummary TEXT NULL;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('Flows') " +
            "WHERE name = 'CurrentBlockerDataJson';",
            "ALTER TABLE Flows ADD COLUMN CurrentBlockerDataJson TEXT NULL;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('Flows') " +
            "WHERE name = 'CustomerBlockerMessage';",
            "ALTER TABLE Flows ADD COLUMN CustomerBlockerMessage TEXT NULL;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('Flows') " +
            "WHERE name = 'ModelSelectionStrategy';",
            "ALTER TABLE Flows ADD COLUMN ModelSelectionStrategy " +
            "TEXT NOT NULL DEFAULT 'MaximumQuality';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('Flows') " +
            "WHERE name = 'OutcomeVerificationJson';",
            "ALTER TABLE Flows ADD COLUMN OutcomeVerificationJson " +
            "TEXT NOT NULL DEFAULT '';",
            cancellationToken);
        await database.Database.ExecuteSqlRawAsync(
            """
            UPDATE Flows
            SET Kind = 'Delivery'
            WHERE Kind IS NULL OR Kind = '';
            UPDATE Flows
            SET ContractVersion = 'legacy-v1'
            WHERE ContractVersion IS NULL OR ContractVersion = '';
            CREATE INDEX IF NOT EXISTS IX_Flows_ParentFlowRunId
                ON Flows (ParentFlowRunId);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Flows_UniqueLinkedSuccessor
                ON Flows (ParentFlowRunId, ParentIteration, LinkKind)
                WHERE ParentFlowRunId IS NOT NULL AND LinkKind IS NOT NULL;
            """,
            cancellationToken);
    }

    private static async Task BackfillFlowStepMetadataAsync(
        HarnessDbContext database,
        CancellationToken cancellationToken)
    {
        if (!await ColumnExistsAsync(
                database,
                "FlowSteps",
                "AgentRole",
                cancellationToken))
        {
            return;
        }

        var steps = await database.FlowSteps
            .OrderBy(item => item.FlowRunId)
            .ThenBy(item => item.Iteration)
            .ThenBy(item => item.Sequence)
            .ThenBy(item => item.Attempt)
            .ToListAsync(cancellationToken);
        if (steps.Count == 0)
        {
            return;
        }

        var hasFlowsTable = await TableExistsAsync(database, "Flows", cancellationToken);
        var flowStates = hasFlowsTable
            ? await database.Flows
                .AsNoTracking()
                .Select(flow => new
                {
                    flow.Id,
                    flow.ContractVersion,
                    flow.OutcomeVerificationJson
                })
                .ToDictionaryAsync(
                    item => item.Id,
                    item => new LegacyFlowUpgradeState(
                        item.ContractVersion,
                        item.OutcomeVerificationJson),
                    cancellationToken)
            : new Dictionary<Guid, LegacyFlowUpgradeState>();
        var gateStates = await TableExistsAsync(
                database,
                "GateRecords",
                cancellationToken)
            ? await database.GateRecords
                .AsNoTracking()
                .Select(gate => new LegacyGateUpgradeState(
                    gate.FlowRunId,
                    gate.FlowStepId,
                    gate.ActionType,
                    gate.Resolved,
                    gate.Approved))
                .ToListAsync(cancellationToken)
            : [];
        var publicationEventStepIds = await TableExistsAsync(
                database,
                "FlowEvents",
                cancellationToken)
            ? await database.FlowEvents
                .AsNoTracking()
                .Where(item =>
                    item.FlowStepId != null &&
                    item.Type == "flow.approved-publication-queued")
                .Select(item => item.FlowStepId!.Value)
                .ToListAsync(cancellationToken)
            : [];
        var publicationEventIds = publicationEventStepIds.ToHashSet();

        var changed = false;
        foreach (var flowGroup in steps.GroupBy(item => item.FlowRunId))
        {
            var ordered = flowGroup
                .OrderBy(item => item.Iteration)
                .ThenBy(item => item.Sequence)
                .ThenBy(item => item.Attempt)
                .ToList();
            var byId = ordered.ToDictionary(item => item.Id);
            foreach (var step in ordered)
            {
                var stableRootId = ResolveLegacyStableSemanticRootId(step, byId);
                if (step.StableSemanticRootId != stableRootId)
                {
                    step.StableSemanticRootId = stableRootId;
                    changed = true;
                }
            }

            if (!flowStates.TryGetValue(flowGroup.Key, out var flowState))
            {
                continue;
            }
            if (string.Equals(
                    flowState.ContractVersion,
                    "legacy-v1",
                    StringComparison.Ordinal))
            {
                var approvedReleaseIterations = gateStates
                    .Where(gate =>
                        gate.FlowRunId == flowGroup.Key &&
                        gate.ActionType == HandoffActionType.Release &&
                        gate.Resolved &&
                        gate.Approved == true &&
                        byId.TryGetValue(gate.FlowStepId, out _))
                    .Select(gate => byId[gate.FlowStepId].Iteration)
                    .ToHashSet();
                var publicationJournalStepIds =
                    string.IsNullOrWhiteSpace(
                        flowState.OutcomeVerificationJson)
                        ? []
                        : BuildLegacyPublicationStepIds(
                            flowState.OutcomeVerificationJson);
                foreach (var step in ordered)
                {
                    if (BackfillLegacyExecutionMetadata(
                            step,
                            approvedReleaseIterations,
                            publicationEventIds,
                            publicationJournalStepIds,
                            governed:
                            !string.IsNullOrWhiteSpace(
                                flowState.OutcomeVerificationJson)))
                    {
                        changed = true;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(
                    flowState.OutcomeVerificationJson))
            {
                continue;
            }
            var iterations = BuildLegacyOutcomeIterations(
                flowState.OutcomeVerificationJson);
            foreach (var step in ordered)
            {
                if (!iterations.TryGetValue(step.Iteration, out var iteration))
                {
                    continue;
                }

                if (step.Kind == FlowStepKind.Standard &&
                    TryInferLegacyOutcomeStepKind(step, byId, out var inferredKind))
                {
                    step.Kind = inferredKind;
                    changed = true;
                }

                if (step.OutcomeQaRound is null &&
                    TryInferLegacyOutcomeQaRound(step, byId, iteration, out var qaRound))
                {
                    step.OutcomeQaRound = qaRound;
                    changed = true;
                }

                if (string.IsNullOrWhiteSpace(step.OutcomePlanHash) &&
                    TryInferLegacyOutcomePlanHash(step, byId, iteration, out var planHash))
                {
                    step.OutcomePlanHash = planHash;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            await database.SaveChangesAsync(cancellationToken);
        }
    }

    private static bool BackfillLegacyExecutionMetadata(
        FlowStep step,
        IReadOnlySet<int> approvedReleaseIterations,
        IReadOnlySet<Guid> publicationEventStepIds,
        IReadOnlySet<Guid> publicationJournalStepIds,
        bool governed)
    {
        var preMortemReview =
            step.PreMortemReviewStepId is null &&
            step.PreMortemTargetStepId is not null &&
            (string.Equals(
                 step.AgentRole,
                 WorkflowEngine.PreMortemRole,
                 StringComparison.Ordinal) ||
             step.PreMortemOriginStepId is not null);
        if (preMortemReview)
        {
            return SetLegacyExecutionMetadata(
                step,
                ExecutionInvocationKind.PreMortem,
                """["Analyze","Verify"]""",
                PlanStage.BeforeReview,
                ExecutionPermissionProfile.PreMortemReadOnly);
        }

        var publicationSignal =
            step.RemotePublicationAllowed ||
            step.Kind == FlowStepKind.OutcomeApprovedPublication ||
            string.Equals(
                step.Label,
                WorkflowEngine.ApprovedPublicationLabel,
                StringComparison.Ordinal) ||
            publicationEventStepIds.Contains(step.Id) ||
            publicationJournalStepIds.Contains(step.Id) ||
            step.InvocationKind ==
                ExecutionInvocationKind.Publication ||
            step.PlanStage == PlanStage.AfterApproval ||
            string.Equals(
                step.PlanDutiesJson,
                """["Publish"]""",
                StringComparison.Ordinal) ||
            step.PermissionProfile ==
                ExecutionPermissionProfile.Publish;
        if (publicationSignal &&
            approvedReleaseIterations.Contains(step.Iteration))
        {
            var publicationFlagChanged =
                !step.RemotePublicationAllowed;
            step.RemotePublicationAllowed = true;
            return SetLegacyExecutionMetadata(
                step,
                ExecutionInvocationKind.Publication,
                """["Publish"]""",
                PlanStage.AfterApproval,
                governed
                    ? ExecutionPermissionProfile.WorkspaceWrite
                    : ExecutionPermissionProfile.Publish) ||
                   publicationFlagChanged;
        }
        if (publicationSignal)
        {
            var publicationFlagChanged =
                step.RemotePublicationAllowed;
            step.RemotePublicationAllowed = false;
            return SetLegacyExecutionMetadata(
                step,
                ExecutionInvocationKind.Worker,
                "[]",
                PlanStage.BeforeReview,
                ExecutionPermissionProfile.WorkspaceWrite) ||
                   publicationFlagChanged;
        }

        if (string.Equals(
                step.AgentRole,
                "account-manager",
                StringComparison.Ordinal))
        {
            return SetLegacyExecutionMetadata(
                step,
                ExecutionInvocationKind.Intake,
                """["Analyze"]""",
                PlanStage.BeforeReview,
                ExecutionPermissionProfile.ReadOnlySource);
        }
        if (string.Equals(
                step.AgentRole,
                "team-lead",
                StringComparison.Ordinal))
        {
            return SetLegacyExecutionMetadata(
                step,
                ExecutionInvocationKind.Planning,
                """["Analyze","Design"]""",
                PlanStage.BeforeReview,
                ExecutionPermissionProfile.ReadOnlySource);
        }
        return false;
    }

    private static bool SetLegacyExecutionMetadata(
        FlowStep step,
        ExecutionInvocationKind invocationKind,
        string dutiesJson,
        PlanStage stage,
        ExecutionPermissionProfile permissionProfile)
    {
        var changed =
            step.InvocationKind != invocationKind ||
            !string.Equals(
                step.PlanDutiesJson,
                dutiesJson,
                StringComparison.Ordinal) ||
            step.PlanStage != stage ||
            step.PermissionProfile != permissionProfile;
        step.InvocationKind = invocationKind;
        step.PlanDutiesJson = dutiesJson;
        step.PlanStage = stage;
        step.PermissionProfile = permissionProfile;
        return changed;
    }

    private static HashSet<Guid> BuildLegacyPublicationStepIds(string json)
    {
        var state = OutcomeVerificationRules.DeserializeAggregate(json);
        var result = state.PriorIterations
            .Select(archive => archive.Publication?.StepId)
            .OfType<Guid>()
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        if (state.Publication?.StepId is { } stepId &&
            stepId != Guid.Empty)
        {
            result.Add(stepId);
        }
        return result;
    }

    private static IReadOnlyDictionary<int, LegacyOutcomeIterationState>
        BuildLegacyOutcomeIterations(string json)
    {
        var state = OutcomeVerificationRules.DeserializeAggregate(json);
        var iterations = new Dictionary<int, LegacyOutcomeIterationState>();
        foreach (var archive in state.PriorIterations)
        {
            iterations[archive.Iteration] = new LegacyOutcomeIterationState(
                archive.AcceptancePlan?.Hash,
                archive.EvidenceProcessing.ToDictionary(
                    item => item.ProducerStepId,
                    item => item.AcceptancePlanHash),
                archive.Rounds.ToDictionary(item => item.QaStepId),
                archive.CurrentCandidate?.PreparedByStepId,
                archive.CurrentCandidate?.Manifest.AcceptancePlanHash,
                null,
                null,
                archive.Rounds
                    .Where(round =>
                        !round.Stale &&
                        round.Result?.Verdict == OutcomeQaVerdict.PASS)
                    .Select(round => (int?)round.Round)
                    .Max());
        }

        iterations[state.Iteration] = new LegacyOutcomeIterationState(
            state.AcceptancePlan?.Hash,
            state.EvidenceProcessing.ToDictionary(
                item => item.ProducerStepId,
                item => item.AcceptancePlanHash),
            state.Rounds.ToDictionary(item => item.QaStepId),
            state.CurrentCandidate?.PreparedByStepId,
            state.CurrentCandidate?.Manifest.AcceptancePlanHash,
            state.ActiveQaStepId,
            state.ActiveQaRound,
            state.Rounds
                .Where(round =>
                    !round.Stale &&
                    round.Result?.Verdict == OutcomeQaVerdict.PASS &&
                    string.Equals(
                        round.CandidateFingerprint,
                        state.VerifiedCandidateFingerprint,
                        StringComparison.Ordinal))
                .Select(round => (int?)round.Round)
                .Max());
        return iterations;
    }

    private static bool TryInferLegacyOutcomeStepKind(
        FlowStep step,
        IReadOnlyDictionary<Guid, FlowStep> stepsById,
        out FlowStepKind kind)
    {
        if (step.RetryOfStepId is { } retryOfStepId &&
            stepsById.TryGetValue(retryOfStepId, out var source) &&
            source.Kind != FlowStepKind.Standard)
        {
            kind = source.Kind;
            return true;
        }

        if (step.DependsOnStepId is { } dependencyId &&
            stepsById.TryGetValue(dependencyId, out var dependency) &&
            dependency.Kind == FlowStepKind.OutcomePlanCorrection &&
            string.Equals(step.AgentRole, "team-lead", StringComparison.Ordinal))
        {
            kind = FlowStepKind.OutcomePlanCorrection;
            return true;
        }

        if (string.Equals(
                step.Label,
                WorkflowEngine.ApprovedPublicationLabel,
                StringComparison.Ordinal))
        {
            kind = FlowStepKind.OutcomeApprovedPublication;
            return true;
        }

        if (string.Equals(
                step.Label,
                WorkflowEngine.ReleaseCandidateLabel,
                StringComparison.Ordinal))
        {
            kind = FlowStepKind.OutcomeLocalReleaseCandidate;
            return true;
        }

        if (step.Label.StartsWith(
                WorkflowEngine.OutcomeQaLabelPrefix,
                StringComparison.Ordinal) ||
            string.Equals(
                step.Label,
                "Re-validate corrected handoff",
                StringComparison.Ordinal))
        {
            kind = FlowStepKind.OutcomeQa;
            return true;
        }

        if (step.Label.StartsWith(
                WorkflowEngine.OutcomePlanCorrectionLabelPrefix,
                StringComparison.Ordinal))
        {
            kind = FlowStepKind.OutcomePlanCorrection;
            return true;
        }

        if (step.Label.StartsWith(
                WorkflowEngine.OutcomeCandidateRefreshLabelPrefix,
                StringComparison.Ordinal))
        {
            kind = FlowStepKind.OutcomeCandidateRefresh;
            return true;
        }

        if (step.Label.StartsWith(
                WorkflowEngine.OutcomeCorrectionLabelPrefix,
                StringComparison.Ordinal))
        {
            kind = FlowStepKind.OutcomeOwnerCorrection;
            return true;
        }

        if (string.Equals(step.AgentRole, "team-lead", StringComparison.Ordinal) &&
            step.Label.Contains("acceptance plan", StringComparison.OrdinalIgnoreCase))
        {
            kind = FlowStepKind.OutcomePlan;
            return true;
        }

        if (string.Equals(step.AgentRole, "release-engineer", StringComparison.Ordinal) &&
            !step.RemotePublicationAllowed)
        {
            kind = FlowStepKind.OutcomeLocalReleaseCandidate;
            return true;
        }

        if (string.Equals(step.AgentRole, "quality-engineer", StringComparison.Ordinal) &&
            !step.RemotePublicationAllowed)
        {
            kind = FlowStepKind.OutcomeQa;
            return true;
        }

        if (step.AgentRole is not ("team-lead" or "quality-engineer" or "product-manager" or
            WorkflowEngine.PreMortemRole) &&
            !step.RemotePublicationAllowed)
        {
            kind = FlowStepKind.OutcomeDelivery;
            return true;
        }

        kind = FlowStepKind.Standard;
        return false;
    }

    private static bool TryInferLegacyOutcomeQaRound(
        FlowStep step,
        IReadOnlyDictionary<Guid, FlowStep> stepsById,
        LegacyOutcomeIterationState iteration,
        out int round)
    {
        if (step.RetryOfStepId is { } retryOfStepId &&
            stepsById.TryGetValue(retryOfStepId, out var source) &&
            source.OutcomeQaRound is { } sourceRound)
        {
            round = sourceRound;
            return true;
        }

        if (iteration.RoundsByQaStepId.TryGetValue(step.Id, out var persistedRound))
        {
            round = persistedRound.Round;
            return true;
        }

        if (iteration.ActiveQaStepId == step.Id && iteration.ActiveQaRound is { } activeRound)
        {
            round = activeRound;
            return true;
        }

        if ((step.Kind == FlowStepKind.OutcomeQa &&
             TryParseRoundSuffix(step.Label, WorkflowEngine.OutcomeQaLabelPrefix, ")", out round)) ||
            (step.Kind == FlowStepKind.OutcomePlanCorrection &&
             TryParseRoundSuffix(step.Label, WorkflowEngine.OutcomePlanCorrectionLabelPrefix, null, out round)) ||
            (step.Kind == FlowStepKind.OutcomeCandidateRefresh &&
             TryParseRoundSuffix(step.Label, WorkflowEngine.OutcomeCandidateRefreshLabelPrefix, null, out round)) ||
            (step.Kind == FlowStepKind.OutcomeOwnerCorrection &&
             TryParseRoundSuffix(step.Label, WorkflowEngine.OutcomeCorrectionLabelPrefix, ":", out round)))
        {
            return true;
        }

        if (step.Kind == FlowStepKind.OutcomeApprovedPublication &&
            iteration.LatestPassedRound is { } passedRound)
        {
            round = passedRound;
            return true;
        }

        round = default;
        return false;
    }

    private static bool TryInferLegacyOutcomePlanHash(
        FlowStep step,
        IReadOnlyDictionary<Guid, FlowStep> stepsById,
        LegacyOutcomeIterationState iteration,
        out string planHash)
    {
        if (step.RetryOfStepId is { } retryOfStepId &&
            stepsById.TryGetValue(retryOfStepId, out var source) &&
            !string.IsNullOrWhiteSpace(source.OutcomePlanHash))
        {
            planHash = source.OutcomePlanHash;
            return true;
        }

        if (iteration.EvidencePlanHashesByStepId.TryGetValue(
                step.Id,
                out var evidencePlanHash))
        {
            planHash = evidencePlanHash;
            return true;
        }

        if (iteration.RoundsByQaStepId.TryGetValue(step.Id, out var round))
        {
            planHash = round.AcceptancePlanHash;
            return true;
        }

        if (iteration.ActiveQaStepId == step.Id &&
            !string.IsNullOrWhiteSpace(iteration.AcceptancePlanHash))
        {
            planHash = iteration.AcceptancePlanHash!;
            return true;
        }

        if (iteration.CurrentCandidatePreparedByStepId == step.Id &&
            !string.IsNullOrWhiteSpace(iteration.CurrentCandidatePlanHash))
        {
            planHash = iteration.CurrentCandidatePlanHash!;
            return true;
        }

        if (step.Kind == FlowStepKind.OutcomePlanCorrection &&
            step.OutcomeQaRound is { } correctionRound &&
            iteration.RoundsByQaStepId.Values.FirstOrDefault(item =>
                item.Round == correctionRound) is { } correctionResult)
        {
            planHash = correctionResult.AcceptancePlanHash;
            return true;
        }

        if (step.Kind == FlowStepKind.OutcomePlan &&
            string.IsNullOrWhiteSpace(iteration.AcceptancePlanHash) == false)
        {
            planHash = iteration.AcceptancePlanHash!;
            return true;
        }

        if (step.Kind != FlowStepKind.Standard &&
            !string.IsNullOrWhiteSpace(iteration.AcceptancePlanHash))
        {
            planHash = iteration.AcceptancePlanHash!;
            return true;
        }

        planHash = string.Empty;
        return false;
    }

    private static Guid ResolveLegacyStableSemanticRootId(
        FlowStep step,
        IReadOnlyDictionary<Guid, FlowStep> stepsById)
    {
        if (step.StableSemanticRootId is { } existingRootId)
        {
            return existingRootId;
        }

        if (step.RetryOfStepId is not { } retryOfStepId)
        {
            return step.Id;
        }

        var currentRetryOfStepId = retryOfStepId;
        for (var pass = 0; pass < 20; pass++)
        {
            if (!stepsById.TryGetValue(currentRetryOfStepId, out var current))
            {
                return currentRetryOfStepId;
            }

            if (current.StableSemanticRootId is { } rootId)
            {
                return rootId;
            }

            if (current.RetryOfStepId is not { } parentRetryId ||
                parentRetryId == current.Id)
            {
                return current.Id;
            }

            currentRetryOfStepId = parentRetryId;
        }

        return currentRetryOfStepId;
    }

    private static bool TryParseRoundSuffix(
        string label,
        string prefix,
        string? terminator,
        out int round)
    {
        round = default;
        if (string.IsNullOrWhiteSpace(label) ||
            !label.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = label[prefix.Length..];
        if (terminator is not null)
        {
            var index = remainder.IndexOf(terminator, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }
            remainder = remainder[..index];
        }

        return int.TryParse(
            remainder.Trim(),
            out round);
    }

    private static async Task<bool> TableExistsAsync(
        HarnessDbContext database,
        string tableName,
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
            probe.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;";
            var parameter = probe.CreateParameter();
            parameter.ParameterName = "@name";
            parameter.Value = tableName;
            probe.Parameters.Add(parameter);
            return Convert.ToInt64(
                       await probe.ExecuteScalarAsync(cancellationToken)) > 0;
        }
        finally
        {
            if (shouldClose)
            {
                await database.Database.CloseConnectionAsync();
            }
        }
    }

    private static async Task<bool> ColumnExistsAsync(
        HarnessDbContext database,
        string tableName,
        string columnName,
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
            probe.CommandText =
                $"SELECT COUNT(*) FROM pragma_table_info('{tableName}') WHERE name = @name;";
            var parameter = probe.CreateParameter();
            parameter.ParameterName = "@name";
            parameter.Value = columnName;
            probe.Parameters.Add(parameter);
            return Convert.ToInt64(
                await probe.ExecuteScalarAsync(cancellationToken)) > 0;
        }
        finally
        {
            if (shouldClose)
            {
                await database.Database.CloseConnectionAsync();
            }
        }
    }

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
                PreMortemAfter INTEGER NOT NULL DEFAULT 0,
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
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('TaskProfiles') " +
            "WHERE name = 'PreMortemAfter';",
            "ALTER TABLE TaskProfiles ADD COLUMN PreMortemAfter " +
            "INTEGER NOT NULL DEFAULT 0;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('TaskProfiles') " +
            "WHERE name = 'PlanStepKey';",
            "ALTER TABLE TaskProfiles ADD COLUMN PlanStepKey TEXT NOT NULL DEFAULT '';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('TaskProfiles') " +
            "WHERE name = 'AgentId';",
            "ALTER TABLE TaskProfiles ADD COLUMN AgentId TEXT NOT NULL DEFAULT '';",
            cancellationToken);
        await database.Database.ExecuteSqlRawAsync(
            """
            CREATE INDEX IF NOT EXISTS IX_TaskProfiles_Flow_Iteration_PlanStepKey
                ON TaskProfiles (FlowRunId, Iteration, PlanStepKey);
            """,
            cancellationToken);
    }

    internal static async Task EnsureSliceOneSchemaAsync(
        HarnessDbContext database,
        CancellationToken cancellationToken = default)
    {
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('Agents') " +
            "WHERE name = 'DefinitionStatus';",
            "ALTER TABLE Agents ADD COLUMN DefinitionStatus TEXT NOT NULL DEFAULT 'Valid';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('Agents') " +
            "WHERE name = 'ValidationError';",
            "ALTER TABLE Agents ADD COLUMN ValidationError TEXT NOT NULL DEFAULT '';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('Agents') WHERE name = 'Required';",
            "ALTER TABLE Agents ADD COLUMN Required INTEGER NOT NULL DEFAULT 0;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('Agents') WHERE name = 'Switchable';",
            "ALTER TABLE Agents ADD COLUMN Switchable INTEGER NOT NULL DEFAULT 1;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('Agents') " +
            "WHERE name = 'DefinitionHash';",
            "ALTER TABLE Agents ADD COLUMN DefinitionHash TEXT NOT NULL DEFAULT '';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('Agents') WHERE name = 'LoadedAt';",
            "ALTER TABLE Agents ADD COLUMN LoadedAt INTEGER NOT NULL DEFAULT 0;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('GateRecords') " +
            "WHERE name = 'ReviewDecision';",
            "ALTER TABLE GateRecords ADD COLUMN ReviewDecision TEXT NULL;",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('FlowEvents') " +
            "WHERE name = 'DataJson';",
            "ALTER TABLE FlowEvents ADD COLUMN DataJson TEXT NULL;",
            cancellationToken);
        await database.Database.ExecuteSqlRawAsync(
            """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_GateRecords_UnresolvedCustomerReview
                ON GateRecords (FlowRunId, ActionType)
                WHERE ActionType = 'CustomerReview' AND Resolved = 0;
            """,
            cancellationToken);

        await database.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS FlowAgentSnapshots (
                FlowRunId TEXT NOT NULL,
                AgentId TEXT NOT NULL,
                Name TEXT NOT NULL,
                Description TEXT NOT NULL,
                Role TEXT NOT NULL,
                Instructions TEXT NOT NULL,
                DefinitionHash TEXT NOT NULL,
                EnabledAtSnapshot INTEGER NOT NULL,
                Required INTEGER NOT NULL,
                Switchable INTEGER NOT NULL,
                SourceFileName TEXT NOT NULL,
                CapturedAt INTEGER NOT NULL,
                CONSTRAINT PK_FlowAgentSnapshots PRIMARY KEY (FlowRunId, AgentId),
                CONSTRAINT FK_FlowAgentSnapshots_Flows_FlowRunId
                    FOREIGN KEY (FlowRunId)
                    REFERENCES Flows (Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_FlowAgentSnapshots_AgentId
                ON FlowAgentSnapshots (AgentId);

            CREATE TABLE IF NOT EXISTS FlowPlanDocuments (
                Id TEXT NOT NULL CONSTRAINT PK_FlowPlanDocuments PRIMARY KEY,
                FlowRunId TEXT NOT NULL,
                Iteration INTEGER NOT NULL,
                Version TEXT NOT NULL,
                Disposition TEXT NOT NULL,
                RawJson TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL,
                CONSTRAINT FK_FlowPlanDocuments_Flows_FlowRunId
                    FOREIGN KEY (FlowRunId)
                    REFERENCES Flows (Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_FlowPlanDocuments_FlowRunId_Iteration
                ON FlowPlanDocuments (FlowRunId, Iteration);
            """,
            cancellationToken);
    }

    /// <summary>
    /// Additive, idempotent upgrade for the durable reviewed-publication journal. Databases created
    /// before the journal existed gain the table without losing any prior publication history.
    /// </summary>
    internal static async Task EnsureReviewedPublicationSchemaAsync(
        HarnessDbContext database,
        CancellationToken cancellationToken = default)
    {
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
                UpdatedAt INTEGER NOT NULL,
                CONSTRAINT FK_ReviewedPublicationRecords_Flows_FlowRunId
                    FOREIGN KEY (FlowRunId)
                    REFERENCES Flows (Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ReviewedPublicationRecords_Repository
                ON ReviewedPublicationRecords
                    (FlowRunId, PublicationRootId, RelativePath);
            """,
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('ReviewedPublicationRecords') " +
            "WHERE name = 'ReviewedCandidateId';",
            "ALTER TABLE ReviewedPublicationRecords ADD COLUMN ReviewedCandidateId " +
            "TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('ReviewedPublicationRecords') " +
            "WHERE name = 'ReadinessSnapshotId';",
            "ALTER TABLE ReviewedPublicationRecords ADD COLUMN ReadinessSnapshotId " +
            "TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('ReviewedPublicationRecords') " +
            "WHERE name = 'ReadinessContractHash';",
            "ALTER TABLE ReviewedPublicationRecords ADD COLUMN ReadinessContractHash " +
            "TEXT NOT NULL DEFAULT '';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('ReviewedPublicationRecords') " +
            "WHERE name = 'CustomerReviewGateId';",
            "ALTER TABLE ReviewedPublicationRecords ADD COLUMN CustomerReviewGateId " +
            "TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';",
            cancellationToken);
        await EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('ReviewedPublicationRecords') " +
            "WHERE name = 'WaiverSetHash';",
            "ALTER TABLE ReviewedPublicationRecords ADD COLUMN WaiverSetHash " +
            "TEXT NOT NULL DEFAULT '';",
            cancellationToken);
    }

    /// <summary>
    /// Additive, idempotent upgrade for the host-derived Delivery readiness tables. Repeating this
    /// on an existing database creates no duplicate rows, indexes, or history.
    /// </summary>
    internal static async Task EnsureDeliveryReadinessSchemaAsync(
        HarnessDbContext database,
        CancellationToken cancellationToken = default)
    {
        await database.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS DeliveryReadinessSnapshots (
                Id TEXT NOT NULL CONSTRAINT PK_DeliveryReadinessSnapshots PRIMARY KEY,
                FlowRunId TEXT NOT NULL,
                Iteration INTEGER NOT NULL,
                Revision INTEGER NOT NULL,
                State TEXT NOT NULL,
                Reconciliation TEXT NOT NULL DEFAULT 'Current',
                CandidateFingerprint TEXT NOT NULL,
                AcceptancePlanHash TEXT NOT NULL,
                OutcomeContractHash TEXT NOT NULL,
                QaContractHash TEXT NOT NULL DEFAULT '',
                OutcomeOwnerStepId TEXT NOT NULL,
                QaStepId TEXT NOT NULL,
                ContractJson TEXT NOT NULL,
                ContractHash TEXT NOT NULL,
                Active INTEGER NOT NULL DEFAULT 1,
                CreatedAt INTEGER NOT NULL,
                SupersededAt INTEGER NULL,
                CONSTRAINT FK_DeliveryReadinessSnapshots_Flows_FlowRunId
                    FOREIGN KEY (FlowRunId)
                    REFERENCES Flows (Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_DeliveryReadinessSnapshots_Revision
                ON DeliveryReadinessSnapshots (FlowRunId, Iteration, Revision);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_DeliveryReadinessSnapshots_Active
                ON DeliveryReadinessSnapshots (FlowRunId)
                WHERE Active = 1;

            CREATE TABLE IF NOT EXISTS ReviewedCandidateRecords (
                Id TEXT NOT NULL CONSTRAINT PK_ReviewedCandidateRecords PRIMARY KEY,
                FlowRunId TEXT NOT NULL,
                Iteration INTEGER NOT NULL,
                CandidateFingerprint TEXT NOT NULL,
                OutcomeOwnerStepId TEXT NOT NULL,
                OutcomeContractHash TEXT NOT NULL,
                AcceptancePlanHash TEXT NOT NULL,
                ReadinessSnapshotId TEXT NOT NULL,
                ReadinessContractHash TEXT NOT NULL,
                IdentityJson TEXT NOT NULL,
                Active INTEGER NOT NULL DEFAULT 1,
                CreatedAt INTEGER NOT NULL,
                SupersededAt INTEGER NULL,
                CONSTRAINT FK_ReviewedCandidateRecords_Flows_FlowRunId
                    FOREIGN KEY (FlowRunId)
                    REFERENCES Flows (Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ReviewedCandidateRecords_Identity
                ON ReviewedCandidateRecords
                    (FlowRunId, Iteration, CandidateFingerprint, ReadinessContractHash);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ReviewedCandidateRecords_Active
                ON ReviewedCandidateRecords (FlowRunId)
                WHERE Active = 1;

            CREATE TABLE IF NOT EXISTS ReadinessWaiverRecords (
                Id TEXT NOT NULL CONSTRAINT PK_ReadinessWaiverRecords PRIMARY KEY,
                FlowRunId TEXT NOT NULL,
                ReviewedCandidateId TEXT NOT NULL,
                ReadinessSnapshotId TEXT NOT NULL,
                ReadinessContractHash TEXT NOT NULL,
                GateId TEXT NOT NULL,
                RiskId TEXT NOT NULL,
                Actor TEXT NOT NULL,
                Acknowledgement TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL,
                CONSTRAINT FK_ReadinessWaiverRecords_Flows_FlowRunId
                    FOREIGN KEY (FlowRunId)
                    REFERENCES Flows (Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ReadinessWaiverRecords_Receipt
                ON ReadinessWaiverRecords
                    (ReviewedCandidateId, ReadinessContractHash, RiskId);
            """,
            cancellationToken);
    }

    private sealed record LegacyFlowUpgradeState(
        string ContractVersion,
        string OutcomeVerificationJson);

    private sealed record LegacyGateUpgradeState(
        Guid FlowRunId,
        Guid FlowStepId,
        HandoffActionType ActionType,
        bool Resolved,
        bool? Approved);

    private sealed record LegacyOutcomeIterationState(
        string? AcceptancePlanHash,
        IReadOnlyDictionary<Guid, string> EvidencePlanHashesByStepId,
        IReadOnlyDictionary<Guid, OutcomeQaRound> RoundsByQaStepId,
        Guid? CurrentCandidatePreparedByStepId,
        string? CurrentCandidatePlanHash,
        Guid? ActiveQaStepId,
        int? ActiveQaRound,
        int? LatestPassedRound);

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

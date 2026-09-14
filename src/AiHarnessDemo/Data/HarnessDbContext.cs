using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Services;
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

    public DbSet<DemoInstanceRecord> DemoInstances => Set<DemoInstanceRecord>();

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
            entity.Property(item => item.LinkKind).HasConversion<string>();
            entity.Property(item => item.AgentCatalogRevision).HasDefaultValue(string.Empty);
            entity.Property(item => item.OutcomeContractJson).HasDefaultValue(string.Empty);
            entity.Property(item => item.CurrentBlockerCode).HasMaxLength(120);
            entity.Property(item => item.CurrentBlockerSummary).HasMaxLength(
                TeamPlanValidator.MaximumQualificationTextCharacters);
            entity.Property(item => item.CurrentBlockerDataJson).HasMaxLength(65_536);
            entity.Property(item => item.CustomerBlockerMessage).HasMaxLength(
                IntakeParser.MaximumCustomerReplyCharacters);
            entity.Property(item => item.Status).HasConversion<string>();
            entity.Property(item => item.Outcome).HasConversion<string>();
            entity.Property(item => item.ModelSelectionStrategy).HasConversion<string>();
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
                IntakeParser.MaximumJsonCharacters);
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

        modelBuilder.Entity<DemoInstanceRecord>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.ArtifactId).HasMaxLength(120);
            entity.Property(item => item.CandidateFingerprint).HasMaxLength(128);
            entity.Property(item => item.ManifestHash).HasMaxLength(128);
            entity.Property(item => item.ManifestRelativePath).HasMaxLength(1_024);
            entity.Property(item => item.State).HasConversion<string>();
            entity.Property(item => item.ProcessName).HasMaxLength(200);
            entity.Property(item => item.WorkspacePath).HasMaxLength(2_048);
            entity.Property(item => item.WorkingDirectory).HasMaxLength(2_048);
            entity.Property(item => item.LaunchProfile).HasMaxLength(64);
            entity.Property(item => item.LaunchIdentity).HasMaxLength(128);
            entity.Property(item => item.FailureDetail).HasMaxLength(4_000);
            entity.HasIndex(item => new
            {
                item.FlowRunId,
                item.ArtifactId,
                item.CandidateFingerprint,
                item.ManifestHash
            })
                .HasDatabaseName("IX_DemoInstances_Binding")
                .IsUnique();
            entity.HasIndex(item => item.AssignedPort)
                .HasDatabaseName("IX_DemoInstances_ActivePort")
                .IsUnique()
                .HasFilter(
                    "\"AssignedPort\" IS NOT NULL AND \"State\" IN ('Starting','Running','Unhealthy')");
            entity.HasIndex(item => new { item.FlowRunId, item.ArtifactId });
            entity.HasOne(item => item.FlowRun)
                .WithMany(flow => flow.DemoInstances)
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
        await database.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

        if (!await database.Settings.AnyAsync())
        {
            database.Settings.Add(new HarnessSettings());
            await database.SaveChangesAsync();
        }

        var gateEngine = scope.ServiceProvider.GetRequiredService<HandoffGateEngine>();
        gateEngine.RestoreHistory(await database.GateRecords
            .AsNoTracking()
            .OrderBy(item => item.DecidedAt)
            .ToListAsync());

        await scope.ServiceProvider
            .GetRequiredService<AgentCatalog>()
            .LoadAsync();
    }
}
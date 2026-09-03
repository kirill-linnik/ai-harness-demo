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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HarnessSettings>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Outcome).HasConversion<string>();
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
        await EnsureFlowStepSchemaAsync(database);
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

    internal static Task EnsureSettingsSchemaAsync(
        HarnessDbContext database,
        CancellationToken cancellationToken = default) =>
        EnsureColumnAsync(
            database,
            "SELECT COUNT(*) FROM pragma_table_info('Settings') " +
            "WHERE name = 'MaxHandoffRetries';",
            "ALTER TABLE Settings ADD COLUMN MaxHandoffRetries " +
            "INTEGER NOT NULL DEFAULT 2;",
            cancellationToken);

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

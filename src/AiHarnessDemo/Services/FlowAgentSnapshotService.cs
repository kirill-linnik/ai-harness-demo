using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Data;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

public sealed class FlowAgentSnapshotService(
    IDbContextFactory<HarnessDbContext> databaseFactory,
    AgentCatalog catalog)
{
    public void CaptureForNewFlow(HarnessDbContext database, FlowRun flow)
    {
        var status = catalog.Status();
        if (!status.Ready)
        {
            throw new NewWorkAdmissionException(
                ["Agent catalog is not ready: " +
                 (status.LastError ?? "no valid current catalog is loaded.")]);
        }

        var snapshot = catalog.GetEffectiveSnapshot();
        flow.AgentCatalogRevision = snapshot.Revision;
        foreach (var definition in snapshot.Definitions)
        {
            var manifest = definition.Manifest
                ?? throw new InvalidOperationException(
                    $"Effective catalog definition '{definition.Record.Id}' has no manifest.");
            var row = Create(flow.Id, definition.Record, manifest);
            flow.AgentSnapshots.Add(row);
            database.FlowAgentSnapshots.Add(row);
        }
    }

    public async Task<AgentManifest> GetManifestAsync(
        Guid flowRunId,
        string agentId,
        CancellationToken cancellationToken = default)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var snapshot = await database.FlowAgentSnapshots
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.FlowRunId == flowRunId && item.AgentId == agentId,
                cancellationToken);
        if (snapshot is not null)
        {
            return ToManifest(snapshot);
        }

        throw new InvalidOperationException(
            $"Flow '{flowRunId}' has no immutable snapshot for agent '{agentId}'.");
    }

    public async Task<IReadOnlyList<AgentRecord>> GetAgentsAsync(
        Guid flowRunId,
        CancellationToken cancellationToken = default)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var snapshots = await database.FlowAgentSnapshots
            .AsNoTracking()
            .Where(item => item.FlowRunId == flowRunId)
            .ToListAsync(cancellationToken);
        if (snapshots.Count == 0)
        {
            var contractVersion = await database.Flows
                .AsNoTracking()
                .Where(item => item.Id == flowRunId)
                .Select(item => item.ContractVersion)
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new KeyNotFoundException(
                    $"Flow '{flowRunId}' was not found.");
            if (string.Equals(
                    contractVersion,
                    "studio-v2",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"studio-v2 flow '{flowRunId}' has no immutable agent snapshot; the current catalog will not be substituted.");
            }
            return catalog.GetEffectiveSnapshot().Definitions
                .Select(item => ToRecord(item.Record))
                .ToList();
        }
        return snapshots.Select(item => new AgentRecord
        {
            Id = item.AgentId,
            Name = item.Name,
            Description = item.Description,
            Role = item.Role,
            SourcePath = item.SourceFileName,
            Accent = AccentFor(item.Role),
            Enabled = item.EnabledAtSnapshot,
            DefinitionStatus = AgentDefinitionStatus.Valid,
            Required = item.Required,
            Switchable = item.Switchable,
            DefinitionHash = item.DefinitionHash,
            LoadedAt = item.CapturedAt,
            SortOrder = OrderFor(item.Role),
            UpdatedAt = item.CapturedAt
        }).ToList();
    }

    public async Task<int> MigrateLegacyNonterminalAsync(
        CancellationToken cancellationToken = default)
    {
        if (!catalog.Status().Ready)
        {
            return 0;
        }
        var effective = catalog.GetEffectiveSnapshot();
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var flows = await database.Flows
            .Include(item => item.AgentSnapshots)
            .Where(item =>
                item.ContractVersion == "legacy-v1" &&
                item.Status != FlowStatus.Approved &&
                item.Status != FlowStatus.Abandoned &&
                item.Status != FlowStatus.Failed &&
                item.AgentSnapshots.Count == 0)
            .ToListAsync(cancellationToken);

        foreach (var flow in flows)
        {
            flow.AgentCatalogRevision = effective.Revision;
            foreach (var definition in effective.Definitions)
            {
                var row = Create(
                    flow.Id,
                    definition.Record,
                    definition.Manifest!);
                flow.AgentSnapshots.Add(row);
            }
            flow.Events.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                Type = "flow.agent-snapshot-migrated",
                Message =
                    "Captured the startup effective agent catalog for this nonterminal legacy flow."
            });
        }
        await database.SaveChangesAsync(cancellationToken);
        return flows.Count;
    }

    public bool CaptureForLegacyReactivation(
        HarnessDbContext database,
        FlowRun flow)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(flow);
        if (!string.Equals(
                flow.ContractVersion,
                "legacy-v1",
                StringComparison.Ordinal) ||
            flow.Status != FlowStatus.Failed)
        {
            throw new InvalidOperationException(
                "Only an explicitly restarted failed legacy flow can receive a migration snapshot.");
        }
        if (flow.AgentSnapshots.Count > 0)
        {
            return false;
        }

        var effective = catalog.GetEffectiveSnapshot();
        flow.AgentCatalogRevision = effective.Revision;
        foreach (var definition in effective.Definitions)
        {
            var row = Create(
                flow.Id,
                definition.Record,
                definition.Manifest
                ?? throw new InvalidOperationException(
                    $"Effective catalog definition '{definition.Record.Id}' has no manifest."));
            flow.AgentSnapshots.Add(row);
            database.FlowAgentSnapshots.Add(row);
        }
        flow.Events.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            Type = "flow.agent-snapshot-migrated-on-restart",
            Message =
                "Captured the effective agent catalog when a terminal legacy flow was explicitly reactivated; original historical instructions were unavailable."
        });
        return true;
    }

    private static FlowAgentSnapshot Create(
        Guid flowId,
        AgentRecord record,
        AgentManifest manifest) =>
        new()
        {
            FlowRunId = flowId,
            AgentId = record.Id,
            Name = record.Name,
            Description = record.Description,
            Role = record.Role,
            Instructions = manifest.Instructions,
            DefinitionHash = record.DefinitionHash,
            EnabledAtSnapshot = record.Enabled,
            Required = record.Required,
            Switchable = record.Switchable,
            SourceFileName = Path.GetFileName(record.SourcePath)
        };

    private static AgentManifest ToManifest(FlowAgentSnapshot item) =>
        new(
            item.AgentId,
            item.Name,
            item.Description,
            item.Role,
            AccentFor(item.Role),
            OrderFor(item.Role),
            item.SourceFileName,
            item.Instructions,
            item.DefinitionHash);

    private static AgentRecord ToRecord(AgentRecord item) => new()
    {
        Id = item.Id,
        Name = item.Name,
        Description = item.Description,
        Role = item.Role,
        SourcePath = item.SourcePath,
        Accent = item.Accent,
        Enabled = item.Enabled,
        DefinitionStatus = item.DefinitionStatus,
        ValidationError = item.ValidationError,
        Required = item.Required,
        Switchable = item.Switchable,
        DefinitionHash = item.DefinitionHash,
        LoadedAt = item.LoadedAt,
        SortOrder = item.SortOrder,
        UpdatedAt = item.UpdatedAt
    };

    private static string AccentFor(string role) => role switch
    {
        "account-manager" => "amber",
        "team-lead" => "violet",
        "pre-mortem-sceptic" => "rose",
        "quality-engineer" => "emerald",
        "release-engineer" => "lime",
        "analyst" => "blue",
        _ => "violet"
    };

    private static int OrderFor(string role) => role switch
    {
        "account-manager" => 10,
        "team-lead" => 20,
        "pre-mortem-sceptic" => 85,
        "quality-engineer" => 80,
        "release-engineer" => 100,
        "analyst" => 120,
        _ => 500
    };
}

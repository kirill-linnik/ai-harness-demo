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
            throw new InvalidOperationException(
                $"Flow '{flowRunId}' has no immutable agent snapshot; the current catalog will not be substituted.");
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

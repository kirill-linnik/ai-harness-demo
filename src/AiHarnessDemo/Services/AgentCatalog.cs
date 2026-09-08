using System.Security.Cryptography;
using System.Text;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Data;
using AiHarnessDemo.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

public sealed record AgentManifest(
    string Id,
    string Name,
    string Description,
    string Role,
    string Accent,
    int SortOrder,
    string SourcePath,
    string Instructions,
    string DefinitionHash = "");

public sealed record CatalogAgentDefinition(
    AgentRecord Record,
    AgentManifest? Manifest);

public sealed record AgentCatalogRuntimeStatus(
    bool Ready,
    bool HasEffectiveCatalog,
    string? EffectiveRevision,
    DateTimeOffset? LoadedAt,
    string? LastError,
    int ValidDefinitionCount,
    int InvalidDefinitionCount);

public sealed record AgentCatalogSnapshot(
    string Revision,
    IReadOnlyList<CatalogAgentDefinition> Definitions);

public sealed class AgentCatalog
{
    private readonly HarnessPaths _paths;
    private readonly IDbContextFactory<HarnessDbContext> _databaseFactory;
    private readonly AgentCatalogLoader _loader;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _statusLock = new();
    private IReadOnlyList<CatalogAgentDefinition> _visible = [];
    private AgentCatalogSnapshot? _effective;
    private AgentCatalogRuntimeStatus _status = new(
        false,
        false,
        null,
        null,
        "The agent catalog has not been loaded.",
        0,
        0);

    public AgentCatalog(
        HarnessPaths paths,
        IDbContextFactory<HarnessDbContext> databaseFactory)
        : this(paths, databaseFactory, new AgentCatalogLoader())
    {
    }

    public AgentCatalog(
        HarnessPaths paths,
        IDbContextFactory<HarnessDbContext> databaseFactory,
        AgentCatalogLoader loader)
    {
        _paths = paths;
        _databaseFactory = databaseFactory;
        _loader = loader;
    }

    public AgentCatalogRuntimeStatus Status()
    {
        lock (_statusLock)
        {
            return _status;
        }
    }

    public IReadOnlyList<AgentRecord> List()
    {
        lock (_statusLock)
        {
            return _visible.Select(item => Clone(item.Record)).ToList();
        }
    }

    public AgentCatalogSnapshot GetEffectiveSnapshot()
    {
        lock (_statusLock)
        {
            return _effective is null
                ? throw new InvalidOperationException(
                    "No valid effective agent catalog is available.")
                : Clone(_effective);
        }
    }

    public Task<AgentCatalogRuntimeStatus> LoadAsync(
        CancellationToken cancellationToken = default) =>
        ReloadAsync(cancellationToken);

    public async Task<AgentCatalogRuntimeStatus> ReloadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var loaded = _loader.Load(_paths.AgentsDirectory);
            await using var database =
                await _databaseFactory.CreateDbContextAsync(cancellationToken);
            var stored = await database.Agents.ToDictionaryAsync(
                item => item.Id,
                StringComparer.Ordinal,
                cancellationToken);

            foreach (var definition in loaded.Definitions)
            {
                if (!stored.TryGetValue(definition.Record.Id, out var record))
                {
                    record = Clone(definition.Record);
                    database.Agents.Add(record);
                }
                else
                {
                    ApplyDefinition(record, definition.Record);
                }

                if (!record.Switchable)
                {
                    record.Enabled = true;
                }
                definition.Record.Enabled = record.Enabled;
                definition.Record.UpdatedAt = record.UpdatedAt;
            }

            var currentIds = loaded.Definitions
                .Select(item => item.Record.Id)
                .ToHashSet(StringComparer.Ordinal);
            database.Agents.RemoveRange(
                stored.Values.Where(item => !currentIds.Contains(item.Id)));
            await database.SaveChangesAsync(cancellationToken);

            var visible = loaded.Definitions
                .OrderBy(item => item.Record.SortOrder)
                .ThenBy(item => item.Record.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var valid = visible
                .Where(item =>
                    item.Record.DefinitionStatus == AgentDefinitionStatus.Valid &&
                    item.Manifest is not null)
                .ToList();
            var fatalErrors = visible
                .Where(item =>
                    item.Record.Required &&
                    item.Record.DefinitionStatus == AgentDefinitionStatus.Invalid)
                .Select(item => item.Record.ValidationError)
                .ToList();
            var loadedAt = DateTimeOffset.UtcNow;

            lock (_statusLock)
            {
                _visible = visible;
                if (fatalErrors.Count == 0)
                {
                    var revision = ComputeRevision(valid);
                    _effective = new AgentCatalogSnapshot(revision, valid);
                    _status = new AgentCatalogRuntimeStatus(
                        true,
                        true,
                        revision,
                        loadedAt,
                        null,
                        valid.Count,
                        visible.Count - valid.Count);
                }
                else
                {
                    _status = new AgentCatalogRuntimeStatus(
                        false,
                        _effective is not null,
                        _effective?.Revision,
                        _effective is null ? null : _status.LoadedAt,
                        string.Join(" ", fatalErrors),
                        valid.Count,
                        visible.Count - valid.Count);
                }
                return _status;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AgentRecord> ToggleAsync(
        string agentId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var database =
                await _databaseFactory.CreateDbContextAsync(cancellationToken);
            var record = await database.Agents.SingleOrDefaultAsync(
                item => item.Id == agentId,
                cancellationToken)
                ?? throw new KeyNotFoundException($"Agent '{agentId}' was not found.");
            if (!record.Switchable)
            {
                throw new InvalidOperationException(
                    $"Agent '{agentId}' is required and cannot be disabled.");
            }
            if (record.DefinitionStatus != AgentDefinitionStatus.Valid)
            {
                throw new InvalidOperationException(
                    $"Agent '{agentId}' is invalid and cannot be enabled.");
            }

            record.Enabled = enabled;
            record.UpdatedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync(cancellationToken);

            lock (_statusLock)
            {
                var visible = _visible.ToList();
                var visibleDefinition = visible.Single(item =>
                    string.Equals(item.Record.Id, agentId, StringComparison.Ordinal));
                visibleDefinition.Record.Enabled = enabled;
                visibleDefinition.Record.UpdatedAt = record.UpdatedAt;
                _visible = visible;

                if (_effective is not null)
                {
                    var effective = _effective.Definitions.ToList();
                    var effectiveDefinition = effective.SingleOrDefault(item =>
                        string.Equals(item.Record.Id, agentId, StringComparison.Ordinal));
                    if (effectiveDefinition is not null)
                    {
                        effectiveDefinition.Record.Enabled = enabled;
                        effectiveDefinition.Record.UpdatedAt = record.UpdatedAt;
                        var revision = ComputeRevision(effective);
                        _effective = new AgentCatalogSnapshot(revision, effective);
                        _status = _status with { EffectiveRevision = revision };
                    }
                }
            }
            return Clone(record);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string ComputeRevision(
        IEnumerable<CatalogAgentDefinition> definitions)
    {
        var identity = string.Join(
            "\n",
            definitions
                .OrderBy(item => item.Record.Id, StringComparer.Ordinal)
                .Select(item =>
                    $"{item.Record.Id}\t{item.Record.DefinitionHash}\t" +
                    $"{(item.Record.Enabled ? "enabled" : "disabled")}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static void ApplyDefinition(AgentRecord target, AgentRecord source)
    {
        target.Name = source.Name;
        target.Description = source.Description;
        target.Role = source.Role;
        target.SourcePath = source.SourcePath;
        target.Accent = source.Accent;
        target.SortOrder = source.SortOrder;
        target.DefinitionStatus = source.DefinitionStatus;
        target.ValidationError = source.ValidationError;
        target.Required = source.Required;
        target.Switchable = source.Switchable;
        target.DefinitionHash = source.DefinitionHash;
        target.LoadedAt = source.LoadedAt;
        target.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static AgentRecord Clone(AgentRecord item) => new()
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

    private static AgentCatalogSnapshot Clone(AgentCatalogSnapshot snapshot) =>
        new(
            snapshot.Revision,
            snapshot.Definitions.Select(item =>
                new CatalogAgentDefinition(Clone(item.Record), item.Manifest)).ToList());
}

using AiHarnessDemo.Data;
using AiHarnessDemo.Core.Domain;
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
    string Instructions);

public sealed class AgentCatalog(
    HarnessPaths paths,
    IDbContextFactory<HarnessDbContext> databaseFactory)
{
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    public async Task<IReadOnlyList<AgentRecord>> SyncAsync(CancellationToken cancellationToken = default)
    {
        await _syncGate.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(paths.AgentsDirectory))
            {
                throw new DirectoryNotFoundException(
                    $"Agent directory was not found: {paths.AgentsDirectory}");
            }

            var manifests = Directory
                .EnumerateFiles(paths.AgentsDirectory, "*.agent.md", SearchOption.TopDirectoryOnly)
                .Select(ParseFile)
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
            var stored = await database.Agents.ToDictionaryAsync(
                item => item.Id,
                StringComparer.OrdinalIgnoreCase,
                cancellationToken);

            foreach (var manifest in manifests)
            {
                if (!stored.TryGetValue(manifest.Id, out var agent))
                {
                    database.Agents.Add(new AgentRecord
                    {
                        Id = manifest.Id,
                        Name = manifest.Name,
                        Description = manifest.Description,
                        Role = manifest.Role,
                        SourcePath = manifest.SourcePath,
                        Accent = manifest.Accent,
                        SortOrder = manifest.SortOrder,
                        Enabled = true
                    });
                    continue;
                }

                agent.Name = manifest.Name;
                agent.Description = manifest.Description;
                agent.Role = manifest.Role;
                agent.SourcePath = manifest.SourcePath;
                agent.Accent = manifest.Accent;
                agent.SortOrder = manifest.SortOrder;
                agent.UpdatedAt = DateTimeOffset.UtcNow;
            }

            var currentIds = manifests
                .Select(item => item.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            database.Agents.RemoveRange(
                stored.Values.Where(item => !currentIds.Contains(item.Id)));

            await database.SaveChangesAsync(cancellationToken);
            return await database.Agents
                .AsNoTracking()
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Name)
                .ToListAsync(cancellationToken);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    public async Task<AgentManifest> GetManifestAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(paths.AgentsDirectory, $"{agentId}.agent.md");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Agent definition '{agentId}' was not found.", path);
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken);
        return Parse(agentId, path, content);
    }

    public static AgentManifest Parse(string id, string sourcePath, string content)
    {
        var normalized = content.ReplaceLineEndings("\n");
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var instructions = normalized;

        if (normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            if (end > 0)
            {
                foreach (var line in normalized[4..end].Split('\n'))
                {
                    var separator = line.IndexOf(':');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    metadata[line[..separator].Trim()] =
                        line[(separator + 1)..].Trim().Trim('"', '\'');
                }

                instructions = normalized[(end + 5)..].Trim();
            }
        }

        var role = metadata.GetValueOrDefault("role", id);
        return new AgentManifest(
            id,
            metadata.GetValueOrDefault("name", Humanize(id)),
            metadata.GetValueOrDefault("description", "A member of the AI delivery team."),
            role,
            metadata.GetValueOrDefault("accent", AccentFor(role)),
            int.TryParse(metadata.GetValueOrDefault("order"), out var order) ? order : OrderFor(role),
            sourcePath,
            instructions);
    }

    private static AgentManifest ParseFile(string path)
    {
        var id = Path.GetFileName(path)[..^".agent.md".Length];
        return Parse(id, path, File.ReadAllText(path));
    }

    private static string Humanize(string value) =>
        string.Join(
            ' ',
            value.Split('-', StringSplitOptions.RemoveEmptyEntries)
                .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));

    private static string AccentFor(string role) => role switch
    {
        "account-manager" => "amber",
        "team-lead" => "violet",
        "architect" => "indigo",
        "product-designer" => "pink",
        "software-engineer" => "cyan",
        "data-engineer" => "blue",
        "security-engineer" => "orange",
        "quality-engineer" => "emerald",
        "pre-mortem-sceptic" => "rose",
        "technical-writer" => "teal",
        "release-engineer" => "lime",
        "product-manager" => "rose",
        _ => "violet"
    };

    private static int OrderFor(string role) => role switch
    {
        "account-manager" => 10,
        "team-lead" => 20,
        "architect" => 30,
        "product-designer" => 40,
        "data-engineer" => 50,
        "software-engineer" => 60,
        "security-engineer" => 70,
        "quality-engineer" => 80,
        "pre-mortem-sceptic" => 85,
        "technical-writer" => 90,
        "release-engineer" => 100,
        "product-manager" => 110,
        _ => 500
    };
}

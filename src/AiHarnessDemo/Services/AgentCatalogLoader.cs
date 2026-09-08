using System.Security.Cryptography;
using System.Text;
using AiHarnessDemo.Core.Domain;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace AiHarnessDemo.Services;

public sealed record AgentCatalogLoadResult(
    IReadOnlyList<CatalogAgentDefinition> Definitions);

public sealed class AgentCatalogLoader
{
    // Required core agent IDs must match the canonical lowercase filename exactly.
    // A comparer other than Ordinal here would let a case variant such as
    // 'Account-Manager.agent.md' silently satisfy the 'account-manager' requirement.
    private static readonly Dictionary<string, (bool Required, bool Switchable)> Policies =
        new(StringComparer.Ordinal)
        {
            ["account-manager"] = (true, false),
            ["team-lead"] = (true, false),
            ["pre-mortem-sceptic"] = (true, true)
        };

    public AgentCatalogLoadResult Load(string directory)
    {
        var definitions = new List<CatalogAgentDefinition>();
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(
                         directory,
                         "*.agent.md",
                         SearchOption.TopDirectoryOnly))
            {
                definitions.Add(LoadFile(path));
            }
        }

        foreach (var (id, policy) in Policies)
        {
            if (definitions.Any(item =>
                    string.Equals(item.Record.Id, id, StringComparison.Ordinal)))
            {
                continue;
            }
            var fileName = $"{id}.agent.md";
            definitions.Add(Invalid(
                id,
                Path.Combine(directory, fileName),
                policy.Required,
                policy.Switchable,
                $"Required agent definition '{fileName}' was not found."));
        }
        return new AgentCatalogLoadResult(definitions);
    }

    public static AgentManifest Parse(string id, string sourcePath, string content)
    {
        var normalized = content.ReplaceLineEndings("\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Agent definition must start with parseable YAML frontmatter.");
        }
        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidDataException(
                "Agent definition frontmatter has no closing '---' delimiter.");
        }

        var yaml = new YamlStream();
        try
        {
            yaml.Load(new StringReader(normalized[4..end]));
        }
        catch (YamlException exception)
        {
            throw new InvalidDataException(
                $"Agent definition frontmatter is invalid YAML: {exception.Message}",
                exception);
        }
        if (yaml.Documents.Count != 1 ||
            yaml.Documents[0].RootNode is not YamlMappingNode metadata)
        {
            throw new InvalidDataException(
                "Agent definition frontmatter must be a YAML mapping.");
        }

        var name = Scalar(metadata, "name");
        var description = Scalar(metadata, "description");
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidDataException(
                "Agent definition frontmatter field 'name' is required and cannot be empty.");
        }
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidDataException(
                "Agent definition frontmatter field 'description' is required and cannot be empty.");
        }
        var instructions = normalized[(end + 5)..].Trim();
        if (string.IsNullOrWhiteSpace(instructions))
        {
            throw new InvalidDataException(
                "Agent definition body instructions are required and cannot be empty.");
        }

        var role = Scalar(metadata, "role") ?? id;
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return new AgentManifest(
            id,
            name.Trim(),
            description.Trim(),
            role.Trim(),
            Scalar(metadata, "accent")?.Trim() ?? AccentFor(role),
            int.TryParse(Scalar(metadata, "order"), out var order)
                ? order
                : OrderFor(role),
            sourcePath,
            instructions,
            hash);
    }

    private static CatalogAgentDefinition LoadFile(string path)
    {
        var id = Path.GetFileName(path)[..^".agent.md".Length];
        var hasExactPolicy = Policies.TryGetValue(id, out var exactPolicy);
        if (!hasExactPolicy &&
            Policies.Keys.Any(coreId =>
                string.Equals(coreId, id, StringComparison.OrdinalIgnoreCase)))
        {
            // The filename ID collides with a reserved core agent ID under a
            // different case (e.g. 'Account-Manager' vs 'account-manager').
            // Case variants never satisfy the required core definition; surface
            // this file as an invalid optional conflict diagnostic instead of
            // silently treating it as the required agent.
            return Invalid(
                id,
                path,
                required: false,
                switchable: true,
                $"Agent ID '{id}' conflicts with reserved core agent ID '{id.ToLowerInvariant()}'; " +
                "required core definitions must use the exact canonical lowercase filename.");
        }
        var policy = hasExactPolicy
            ? exactPolicy
            : (Required: false, Switchable: true);
        try
        {
            var manifest = Parse(id, path, File.ReadAllText(path));
            if (policy.Required &&
                !string.Equals(manifest.Role, id, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Required agent definition '{Path.GetFileName(path)}' must use role '{id}'.");
            }
            return new CatalogAgentDefinition(
                new AgentRecord
                {
                    Id = manifest.Id,
                    Name = manifest.Name,
                    Description = manifest.Description,
                    Role = manifest.Role,
                    SourcePath = path,
                    Accent = manifest.Accent,
                    SortOrder = manifest.SortOrder,
                    Enabled = true,
                    DefinitionStatus = AgentDefinitionStatus.Valid,
                    Required = policy.Required,
                    Switchable = policy.Switchable,
                    DefinitionHash = manifest.DefinitionHash
                },
                manifest);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Invalid(
                id,
                path,
                policy.Required,
                policy.Switchable,
                exception.Message);
        }
    }

    private static CatalogAgentDefinition Invalid(
        string id,
        string path,
        bool required,
        bool switchable,
        string error) =>
        new(
            new AgentRecord
            {
                Id = id,
                Name = Humanize(id),
                Description = string.Empty,
                Role = id,
                SourcePath = path,
                Enabled = false,
                DefinitionStatus = AgentDefinitionStatus.Invalid,
                ValidationError = error,
                Required = required,
                Switchable = switchable,
                SortOrder = OrderFor(id)
            },
            null);

    private static string? Scalar(YamlMappingNode mapping, string name)
    {
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is YamlScalarNode key &&
                string.Equals(key.Value, name, StringComparison.OrdinalIgnoreCase))
            {
                return (pair.Value as YamlScalarNode)?.Value;
            }
        }
        return null;
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
        "analyst" => "blue",
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
        "analyst" => 120,
        _ => 500
    };
}

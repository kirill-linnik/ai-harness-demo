using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiHarnessDemo.Core.Security;

public sealed record McpServerDefinition(
    string Name,
    string ConfigurationJson,
    ImmutableArray<string> Tools);

public sealed record McpConfiguration(
    string SourcePath,
    string Content,
    ImmutableArray<McpServerDefinition> Servers)
{
    public static McpConfiguration Empty { get; } = new(string.Empty, string.Empty, []);
}

public static partial class McpConfigurationParser
{
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolPattern();

    public static McpConfiguration Parse(string sourcePath, string content)
    {
        if (content.Length > 65_536)
        {
            throw new InvalidOperationException("MCP configuration exceeds 65,536 characters.");
        }
        using var document = JsonDocument.Parse(content);
        ValidateObject(document.RootElement, "MCP configuration");
        if (!document.RootElement.TryGetProperty("mcpServers", out var servers))
        {
            throw new InvalidOperationException("MCP configuration requires mcpServers.");
        }
        ValidateObject(servers, "mcpServers");
        if (servers.EnumerateObject().Count() > 16)
        {
            throw new InvalidOperationException("MCP configuration permits at most 16 servers.");
        }

        var configured = ImmutableArray.CreateBuilder<McpServerDefinition>();
        foreach (var property in servers.EnumerateObject())
        {
            ValidateObject(property.Value, $"mcpServers.{property.Name}");
            var tools = ReadTools(property.Value);
            var nativeConfiguration = property.Value.EnumerateObject()
                .Where(item => item.Name != "tools")
                .ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal);
            var server = new McpServerDefinition(
                property.Name, JsonSerializer.Serialize(nativeConfiguration), tools);
            ValidateServer(server);
            configured.Add(server);
        }
        return new McpConfiguration(sourcePath, content, configured.ToImmutable());
    }

    public static void ValidateServer(McpServerDefinition server)
    {
        if (server is null ||
            server.Name is null ||
            !NamePattern().IsMatch(server.Name) ||
            string.IsNullOrWhiteSpace(server.ConfigurationJson) ||
            server.ConfigurationJson.Length > 65_536 ||
            server.Tools.IsDefaultOrEmpty || server.Tools.Length > 64 ||
            server.Tools.Any(tool => tool is null || !ToolPattern().IsMatch(tool)) ||
            server.Tools.Distinct(StringComparer.Ordinal).Count() != server.Tools.Length)
        {
            throw new InvalidOperationException("The effective MCP server definition is invalid.");
        }
        using var document = JsonDocument.Parse(server.ConfigurationJson);
        ValidateObject(document.RootElement, $"MCP server {server.Name}");
        if (document.RootElement.TryGetProperty("tools", out _))
        {
            throw new InvalidOperationException("Effective MCP tools must come from the permission document.");
        }
    }

    public static string ToolName(string serverName, string tool) => $"{serverName}-{tool}";

    public static string PermissionPattern(string serverName, string tool) => $"{serverName}({tool})";

    private static ImmutableArray<string> ReadTools(JsonElement server)
    {
        if (!server.TryGetProperty("tools", out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "Managed MCP servers require an explicit native tools list for bounded tool exposure.");
        }
        return value.EnumerateArray().Select(item =>
            item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())
                ? item.GetString()!
                : throw new InvalidOperationException("MCP tools must contain non-empty strings."))
            .ToImmutableArray();
    }

    private static void ValidateObject(JsonElement value, string label)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"{label} must be an object.");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new InvalidOperationException($"{label} contains duplicate property '{property.Name}'.");
            }
        }
    }
}

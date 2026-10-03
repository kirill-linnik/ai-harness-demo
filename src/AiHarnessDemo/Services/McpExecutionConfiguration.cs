using System.Text.Json;
using AiHarnessDemo.Core.Security;

namespace AiHarnessDemo.Services;

public static class McpExecutionConfiguration
{
    public static async Task<StagedContextDocument?> StageAsync(
        EffectiveExecutionPermission permission,
        AgentManifestStager stager,
        StagedAgentManifest manifest,
        Guid flowId,
        Guid stepId,
        int attempt,
        CancellationToken cancellationToken = default)
    {
        PermissionProfileResolver.ValidatePersisted(permission, permission.Profile);
        if (permission.McpServers.IsEmpty)
        {
            return null;
        }

        var json = Serialize(permission);
        return await stager.StageContextDocumentAsync(
            manifest, flowId, stepId, attempt, "mcp-config.json", json, cancellationToken);
    }

    internal static string Serialize(EffectiveExecutionPermission permission)
    {
        PermissionProfileResolver.ValidatePersisted(permission, permission.Profile);
        return JsonSerializer.Serialize(new
        {
            mcpServers = permission.McpServers.ToDictionary(
                server => server.Name,
                NativeServerConfiguration,
                StringComparer.Ordinal)
        });
    }

    private static Dictionary<string, JsonElement> NativeServerConfiguration(McpServerDefinition server)
    {
        using var document = JsonDocument.Parse(server.ConfigurationJson);
        var properties = document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        properties.Add("tools", JsonSerializer.SerializeToElement(server.Tools));
        return properties;
    }
}

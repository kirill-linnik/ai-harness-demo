using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Orchestration;

namespace AiHarnessDemo.Services;

public sealed record WorkspaceSourceScope(string RelativePath, string BaselineCommit);

public static class WorkspaceSourceScopeLedger
{
    public const string EventType = "workspace.source-scope";
    private static readonly JsonSerializerOptions StrictJson = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static string Serialize(WorkspaceInfo workspace)
    {
        var scope = new WorkspaceSourceScope(
            workspace.SourceScopeRelativePath,
            workspace.SourceBaselineCommit);
        Validate(scope);
        return JsonSerializer.Serialize(scope);
    }

    public static WorkspaceSourceScope Read(FlowRun flow)
    {
        var events = flow.Events
            .Where(item => item.Type == EventType)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .ToArray();
        if (events.Length == 0 ||
            events.Any(item => item.FlowRunId != flow.Id))
        {
            throw new CandidateValidationException(
                "The selected-folder flow has no valid durable source scope.");
        }
        var data = events[0].DataJson;
        if (string.IsNullOrWhiteSpace(data))
        {
            throw new CandidateValidationException(
                "The durable selected-folder source scope is empty.");
        }
        WorkspaceSourceScope scope;
        try
        {
            scope = JsonSerializer.Deserialize<WorkspaceSourceScope>(
                data, StrictJson)
                ?? throw new JsonException("Source scope is missing.");
        }
        catch (JsonException exception)
        {
            throw new CandidateValidationException(
                $"The durable source scope is invalid: {exception.Message}");
        }
        Validate(scope);
        return scope;
    }

    private static void Validate(WorkspaceSourceScope scope)
    {
        if (string.IsNullOrWhiteSpace(scope.RelativePath) ||
            Path.IsPathRooted(scope.RelativePath) ||
            scope.RelativePath.Split('/').Any(segment =>
                segment is "" or "." or ".." || segment.Contains('\\')) ||
            string.IsNullOrWhiteSpace(scope.BaselineCommit) ||
            !Regex.IsMatch(
                scope.BaselineCommit,
                @"\A([0-9a-f]{40}|[0-9a-f]{64})\z",
                RegexOptions.CultureInvariant))
        {
            throw new CandidateValidationException(
                "The durable selected-folder path or baseline commit is invalid.");
        }
    }
}

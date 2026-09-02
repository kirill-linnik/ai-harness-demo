using System.Text.Json;
using AiHarnessDemo.Core.Reasoning;

namespace AiHarnessDemo.Services;

/// <summary>
/// Tolerant Copilot CLI JSONL parser. Unknown or malformed lines do not abort the stream; only the
/// final absence of an assistant handoff is considered invalid.
/// </summary>
public static class CopilotJsonlParser
{
    public static AgentRunResult Parse(string standardOutput)
    {
        var toolCalls = new List<ToolCallRecord>();
        var lastAssistantMessage = string.Empty;
        var pendingTools = new Dictionary<string, PendingToolCall>(StringComparer.Ordinal);

        foreach (var line in standardOutput.Split(
                     '\n',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryPayload(line, out var eventType, out var document, out var payload))
            {
                continue;
            }

            using (document)
            {
                switch (eventType)
                {
                    case "assistant.message":
                        lastAssistantMessage =
                            ReadString(payload, "message") ??
                            ReadString(payload, "content") ??
                            lastAssistantMessage;
                        break;

                    case "tool.execution_start":
                        var startCallId = ReadToolCallId(payload);
                        var startToolName = ReadString(payload, "toolName");
                        if (!string.IsNullOrWhiteSpace(startCallId) &&
                            !string.IsNullOrWhiteSpace(startToolName))
                        {
                            pendingTools[startCallId] = new PendingToolCall(
                                startToolName,
                                ScrubArguments(payload));
                        }
                        break;

                    case "tool.execution_complete":
                        var completeCallId = ReadToolCallId(payload);
                        var toolName = ReadString(payload, "toolName");
                        PendingToolCall? pending = null;
                        if (!string.IsNullOrWhiteSpace(completeCallId))
                        {
                            pendingTools.TryGetValue(completeCallId, out pending);
                        }

                        var succeeded =
                            !payload.TryGetProperty("success", out var successElement) ||
                            successElement.ValueKind != JsonValueKind.False;
                        toolCalls.Add(new ToolCallRecord(
                            toolName ?? pending?.ToolName ?? "unknown",
                            pending?.ArgumentsSummary ?? ScrubArguments(payload),
                            succeeded));
                        if (!string.IsNullOrWhiteSpace(completeCallId))
                        {
                            pendingTools.Remove(completeCallId);
                        }
                        break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(lastAssistantMessage))
        {
            return new AgentRunResult
            {
                Success = false,
                OutputSummary = "Copilot CLI completed without an assistant handoff.",
                Error = "No assistant.message event was present in the JSONL stream.",
                FailureKind = AgentRunFailureKind.InvalidOutput,
                ToolCalls = toolCalls
            };
        }

        return new AgentRunResult
        {
            Success = true,
            OutputSummary = lastAssistantMessage,
            ToolCalls = toolCalls
        };
    }

    public static Action<string> CreateProgressReporter(
        Action<AgentRunProgress>? report)
    {
        var pendingToolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        return line =>
        {
            if (report is null ||
                !TryPayload(line, out var eventType, out var document, out var payload))
            {
                return;
            }

            using (document)
            {
                if (eventType == "tool.execution_start")
                {
                    var tool = ReadString(payload, "toolName") ?? "unknown";
                    var id = ReadToolCallId(payload);
                    if (id is not null)
                    {
                        pendingToolNames[id] = tool;
                    }
                    report(new AgentRunProgress(
                        AgentRunPhase.StreamingTurn,
                        $"Using {tool}."));
                    return;
                }

                if (eventType == "tool.execution_complete")
                {
                    var id = ReadToolCallId(payload);
                    var tool = ReadString(payload, "toolName");
                    if (tool is null && id is not null)
                    {
                        pendingToolNames.TryGetValue(id, out tool);
                    }
                    if (id is not null)
                    {
                        pendingToolNames.Remove(id);
                    }
                    report(new AgentRunProgress(
                        AgentRunPhase.StreamingTurn,
                        $"{tool ?? "Tool"} completed."));
                    return;
                }

                if (eventType is "assistant.message" or "result")
                {
                    report(new AgentRunProgress(
                        AgentRunPhase.Finishing,
                        eventType == "result"
                            ? "Agent run completed."
                            : "Response drafted. Validating the handoff."));
                }
            }
        };
    }

    private static bool TryPayload(
        string line,
        out string eventType,
        out JsonDocument document,
        out JsonElement payload)
    {
        eventType = string.Empty;
        document = null!;
        payload = default;

        try
        {
            document = JsonDocument.Parse(line.Trim());
        }
        catch (JsonException)
        {
            return false;
        }

        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var typeElement))
        {
            document.Dispose();
            return false;
        }

        eventType = typeElement.GetString() ?? string.Empty;
        payload =
            root.TryGetProperty("data", out var dataElement) &&
            dataElement.ValueKind == JsonValueKind.Object
                ? dataElement
                : root;
        return true;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadToolCallId(JsonElement element) =>
        ReadString(element, "toolCallId") ??
        ReadString(element, "callId");

    private static string ScrubArguments(JsonElement payload)
    {
        if (!payload.TryGetProperty("arguments", out var arguments) ||
            arguments.ValueKind != JsonValueKind.Object)
        {
            return "No structured arguments recorded.";
        }

        var names = arguments.EnumerateObject()
            .Select(property => property.Name)
            .Take(20)
            .ToList();
        return names.Count == 0
            ? "No structured arguments recorded."
            : $"Argument values redacted; fields: {string.Join(", ", names)}";
    }

    private sealed record PendingToolCall(string ToolName, string ArgumentsSummary);
}

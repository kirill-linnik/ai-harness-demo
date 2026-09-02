using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiHarnessDemo.Core.Reasoning;

namespace AiHarnessDemo.Services;

/// <summary>
/// Tolerant Copilot CLI JSONL parser. Unknown or malformed lines do not abort the stream; terminal
/// session failures and incomplete streams remain explicit failures.
/// </summary>
public static partial class CopilotJsonlParser
{
    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscapePattern();

    public static AgentRunResult Parse(
        string standardOutput,
        string standardError = "")
    {
        var toolCalls = new List<ToolCallRecord>();
        var lastAssistantMessage = string.Empty;
        var messageDeltas = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var pendingTools = new Dictionary<string, PendingToolCall>(StringComparer.Ordinal);
        var observedEventTypes = new HashSet<string>(StringComparer.Ordinal);
        string? lastRootMessageId = null;
        string? lastCompletedMessageId = null;
        string? sessionErrorType = null;
        string? sessionErrorMessage = null;
        int? sessionStatusCode = null;
        var sawAssistantTurnEnd = false;
        var sawResult = false;

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
                observedEventTypes.Add(eventType);
                var isSubAgentEvent = IsSubAgentEvent(document.RootElement);
                switch (eventType)
                {
                    case "assistant.message_start":
                        if (!isSubAgentEvent)
                        {
                            lastRootMessageId =
                                ReadString(payload, "messageId") ??
                                lastRootMessageId;
                        }
                        break;

                    case "assistant.message_delta":
                        if (isSubAgentEvent)
                        {
                            break;
                        }

                        var deltaMessageId =
                            ReadString(payload, "messageId") ??
                            lastRootMessageId ??
                            "unidentified";
                        var deltaContent = ReadString(payload, "deltaContent");
                        if (!string.IsNullOrEmpty(deltaContent))
                        {
                            if (!messageDeltas.TryGetValue(deltaMessageId, out var builder))
                            {
                                builder = new StringBuilder();
                                messageDeltas[deltaMessageId] = builder;
                            }
                            builder.Append(deltaContent);
                        }
                        lastRootMessageId = deltaMessageId;
                        break;

                    case "assistant.message":
                        if (isSubAgentEvent)
                        {
                            break;
                        }

                        var completedMessageId = ReadString(payload, "messageId");
                        lastRootMessageId = completedMessageId ?? lastRootMessageId;
                        lastCompletedMessageId = completedMessageId ?? lastCompletedMessageId;
                        var completedMessage =
                            ReadString(payload, "message") ??
                            ReadString(payload, "content");
                        if (!string.IsNullOrWhiteSpace(completedMessage))
                        {
                            lastAssistantMessage = completedMessage;
                        }
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

                    case "assistant.turn_end":
                        if (!isSubAgentEvent)
                        {
                            sawAssistantTurnEnd = true;
                        }
                        break;

                    case "session.error":
                        sessionErrorType =
                            ReadString(payload, "errorType") ??
                            sessionErrorType;
                        sessionErrorMessage =
                            ReadString(payload, "message") ??
                            sessionErrorMessage;
                        sessionStatusCode =
                            ReadInt32(payload, "statusCode") ??
                            sessionStatusCode;
                        break;

                    case "session.shutdown":
                        if (string.Equals(
                                ReadString(payload, "shutdownType"),
                                "error",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            sessionErrorType ??= "shutdown";
                            sessionErrorMessage ??=
                                ReadString(payload, "errorReason") ??
                                "Copilot CLI shut down unexpectedly.";
                        }
                        break;

                    case "result":
                        sawResult = true;
                        break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(lastAssistantMessage) &&
            (sawAssistantTurnEnd || sawResult) &&
            lastRootMessageId is not null &&
            !string.Equals(
                lastRootMessageId,
                lastCompletedMessageId,
                StringComparison.Ordinal) &&
            messageDeltas.TryGetValue(lastRootMessageId, out var finalDeltas) &&
            !string.IsNullOrWhiteSpace(finalDeltas.ToString()))
        {
            lastAssistantMessage = finalDeltas.ToString();
        }

        if (string.IsNullOrWhiteSpace(lastAssistantMessage))
        {
            if (sessionErrorType is not null || sessionErrorMessage is not null)
            {
                var failureKind = ClassifySessionError(
                    sessionErrorType,
                    sessionStatusCode);
                var errorType = string.IsNullOrWhiteSpace(sessionErrorType)
                    ? "unknown"
                    : sessionErrorType;
                var status = sessionStatusCode is null
                    ? string.Empty
                    : $", HTTP {sessionStatusCode}";
                var message = string.IsNullOrWhiteSpace(sessionErrorMessage)
                    ? "No additional details were provided."
                    : sessionErrorMessage.Trim();
                return new AgentRunResult
                {
                    Success = false,
                    OutputSummary = "Copilot CLI session failed.",
                    Error = $"Copilot CLI session failed ({errorType}{status}): {message}",
                    FailureKind = failureKind,
                    FailedDependency = failureKind == AgentRunFailureKind.DependencyUnavailable
                        ? "copilot-cli"
                        : null,
                    ToolCalls = toolCalls
                };
            }

            if (!string.IsNullOrWhiteSpace(standardError))
            {
                var diagnostic = Tail(
                    SanitizeTerminalOutput(standardError),
                    1_500);
                return new AgentRunResult
                {
                    Success = false,
                    OutputSummary = "Copilot CLI ended without a complete response.",
                    Error =
                        "Copilot CLI ended without a complete assistant response. " +
                        diagnostic,
                    FailureKind = AgentRunFailureKind.AmbiguousCrash,
                    ToolCalls = toolCalls
                };
            }

            var observedEvents = observedEventTypes.Count == 0
                ? "no JSON events"
                : string.Join(", ", observedEventTypes.Order());
            if (!sawAssistantTurnEnd && !sawResult)
            {
                return new AgentRunResult
                {
                    Success = false,
                    OutputSummary = "Copilot CLI ended before completing its response.",
                    Error =
                        "Copilot CLI ended before completing an assistant response " +
                        $"(observed: {observedEvents}).",
                    FailureKind = AgentRunFailureKind.AmbiguousCrash,
                    ToolCalls = toolCalls
                };
            }

            return new AgentRunResult
            {
                Success = false,
                OutputSummary = "Copilot CLI completed without an assistant handoff.",
                Error =
                    "Copilot CLI completed without a usable assistant response " +
                    $"(observed: {observedEvents}).",
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
                    if (eventType == "assistant.message" &&
                        IsSubAgentEvent(document.RootElement))
                    {
                        return;
                    }
                    report(new AgentRunProgress(
                        AgentRunPhase.Finishing,
                        eventType == "result"
                            ? "Agent run completed."
                            : "Response drafted. Validating the handoff."));
                    return;
                }

                if (eventType == "session.error")
                {
                    report(new AgentRunProgress(
                        AgentRunPhase.Failed,
                        ReadString(payload, "message") is { Length: > 0 } message
                            ? $"Copilot CLI session failed: {message}"
                            : "Copilot CLI session failed."));
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

    private static int? ReadInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.TryGetInt32(out var number)
            ? number
            : null;

    private static string? ReadToolCallId(JsonElement element) =>
        ReadString(element, "toolCallId") ??
        ReadString(element, "callId");

    private static bool IsSubAgentEvent(JsonElement root) =>
        root.TryGetProperty("agentId", out var agentId) &&
        agentId.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(agentId.GetString());

    private static AgentRunFailureKind ClassifySessionError(
        string? errorType,
        int? statusCode)
    {
        if (statusCode is 401 or 403)
        {
            return AgentRunFailureKind.DependencyUnavailable;
        }

        if (statusCode is 408 or 425 or 429 || statusCode >= 500)
        {
            return AgentRunFailureKind.Transient;
        }

        var normalized = (errorType ?? string.Empty)
            .Trim()
            .Replace('-', '_')
            .ToLowerInvariant();
        if (normalized.Contains("auth", StringComparison.Ordinal) ||
            normalized.Contains("forbidden", StringComparison.Ordinal) ||
            normalized.Contains("permission", StringComparison.Ordinal) ||
            normalized.Contains("quota", StringComparison.Ordinal) ||
            normalized.Contains("subscription", StringComparison.Ordinal))
        {
            return AgentRunFailureKind.DependencyUnavailable;
        }

        if (normalized.Contains("invalid_request", StringComparison.Ordinal) ||
            normalized.Contains("content_filter", StringComparison.Ordinal) ||
            normalized.Contains("policy", StringComparison.Ordinal))
        {
            return AgentRunFailureKind.InvalidOutput;
        }

        return AgentRunFailureKind.Transient;
    }

    private static string Tail(string value, int maxCharacters) =>
        value.Length <= maxCharacters ? value : value[^maxCharacters..];

    private static string SanitizeTerminalOutput(string value)
    {
        var withoutAnsi = AnsiEscapePattern().Replace(value, string.Empty);
        var sanitized = new StringBuilder(withoutAnsi.Length);
        foreach (var character in withoutAnsi)
        {
            if (!char.IsControl(character) ||
                character is '\r' or '\n' or '\t')
            {
                sanitized.Append(character);
            }
        }
        return sanitized.ToString().Trim();
    }

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

using System.Security.Cryptography;
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
    private const int MaximumResultSummaryCharacters = 1_000;

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscapePattern();

    [GeneratedRegex(
        @"(?i)(?<name>(?:--)?(?:token|password|secret|api[_-]?key|authorization))\s*(?<separator>[:=]|\s)\s*(?:bearer\s+)?(?<value>[""']?[^\s,;""']+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveValuePattern();

    [GeneratedRegex(
        @"(?i)\b(?:github_pat_|gh[pousr]_)[A-Za-z0-9_]+\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex TokenValuePattern();

    public static AgentRunResult Parse(
        string standardOutput,
        string standardError = "",
        string workingDirectory = "")
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
                            pendingTools[startCallId] = CreatePendingToolCall(
                                startToolName,
                                payload,
                                workingDirectory);
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

                        var completionExitCode = ReadToolExitCode(payload);
                        var succeeded = IsSuccessfulToolCompletion(
                            payload,
                            completionExitCode);
                        var resultEvidence = ReadResultEvidence(payload);
                        var completedToolName =
                            toolName ?? pending?.ToolName ?? "unknown";
                        var completionArguments = pending ??
                            CreatePendingToolCall(
                                completedToolName,
                                payload,
                                workingDirectory);
                        toolCalls.Add(new ToolCallRecord(
                            completedToolName,
                            completionArguments.ArgumentsSummary,
                            succeeded,
                            completionArguments.ToolType,
                            completionArguments.NormalizedCommand,
                            completionArguments.NormalizedArguments,
                            completionArguments.WorkingDirectory,
                            completionExitCode,
                            resultEvidence.Digest,
                            resultEvidence.Summary));
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
                    sessionStatusCode,
                    sessionErrorMessage);
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
                    FailedDependency = failureKind switch
                    {
                        AgentRunFailureKind.DependencyUnavailable => "copilot-cli",
                        AgentRunFailureKind.ModelUnavailable => "model-candidate",
                        _ => null
                    },
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
        Action<AgentRunProgress>? report,
        string copilotSessionHome = "")
    {
        var streamingReported = false;
        void ReportStreaming(string activity)
        {
            if (streamingReported)
            {
                return;
            }
            streamingReported = true;
            report?.Invoke(new AgentRunProgress(
                AgentRunPhase.StreamingTurn,
                activity));
        }

        return line =>
        {
            if (report is null ||
                !TryPayload(line, out var eventType, out var document, out var payload))
            {
                return;
            }

            using (document)
            {
                if (eventType == "session.start")
                {
                    var sessionId = ReadString(payload, "sessionId");
                    var hasSessionId = Guid.TryParse(sessionId, out var parsedSessionId);
                    report(new AgentRunProgress(
                        AgentRunPhase.InitializingSession,
                        hasSessionId
                            ? $"Copilot session {parsedSessionId:D} initialized."
                            : "Copilot session initialized without a usable session ID.",
                        CopilotSessionId: hasSessionId ? parsedSessionId : null,
                        CopilotSessionHome: copilotSessionHome));
                    return;
                }

                if (eventType == "tool.execution_start")
                {
                    ReportStreaming("Agent is using tools.");
                    return;
                }

                if (eventType == "tool.execution_complete")
                {
                    return;
                }

                if (eventType == "assistant.message")
                {
                    if (!IsSubAgentEvent(document.RootElement))
                    {
                        // A message can request another tool call. It proves the turn is active,
                        // but only the terminal result event means the CLI is finishing.
                        ReportStreaming("Assistant message observed; execution continues.");
                    }
                    return;
                }

                if (eventType == "result")
                {
                    report(new AgentRunProgress(
                        AgentRunPhase.Finishing,
                        "Agent run completed."));
                    return;
                }

                if (eventType == "session.error")
                {
                    report(new AgentRunProgress(
                        AgentRunPhase.Failed,
                        "Copilot CLI reported a session failure."));
                }
            }
        };
    }

    internal static bool TryPayload(
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
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
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

    internal static string? ReadString(JsonElement element, string propertyName) =>
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

    internal static bool IsSubAgentEvent(JsonElement root) =>
        root.TryGetProperty("agentId", out var agentId) &&
        agentId.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(agentId.GetString());

    private static AgentRunFailureKind ClassifySessionError(
        string? errorType,
        int? statusCode,
        string? errorMessage)
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
        var normalizedMessage = (errorMessage ?? string.Empty).ToLowerInvariant();
        if (normalized.Contains("model_unavailable", StringComparison.Ordinal) ||
            normalized.Contains("unsupported_model", StringComparison.Ordinal) ||
            normalizedMessage.Contains("model is not available", StringComparison.Ordinal) ||
            normalizedMessage.Contains("unsupported model", StringComparison.Ordinal) ||
            normalizedMessage.Contains("unknown model", StringComparison.Ordinal))
        {
            return AgentRunFailureKind.ModelUnavailable;
        }
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
        var scrubbed = names.Count == 0
            ? "No structured arguments recorded."
            : $"Argument values redacted; fields: {string.Join(", ", names)}";
        return HostObservedToolLocator.AppendDigests(scrubbed, arguments);
    }

    private static PendingToolCall CreatePendingToolCall(
        string toolName,
        JsonElement payload,
        string defaultWorkingDirectory)
    {
        var arguments = payload.TryGetProperty("arguments", out var value) &&
                        value.ValueKind == JsonValueKind.Object
            ? value
            : default;
        return new PendingToolCall(
            toolName,
            ScrubArguments(payload),
            ClassifyTool(toolName),
            ReadNormalizedCommand(arguments),
            NormalizeArguments(arguments),
            ResolveWorkingDirectory(arguments, defaultWorkingDirectory));
    }

    private static string ClassifyTool(string toolName)
    {
        var normalized = toolName.Trim().ToLowerInvariant();
        if (normalized.Contains("browser", StringComparison.Ordinal) ||
            normalized.Contains("playwright", StringComparison.Ordinal))
        {
            return "Browser";
        }
        if (normalized is "shell" or "bash" or "powershell" or "pwsh" or
            "command" or "terminal" or "task" ||
            normalized.Contains("shell", StringComparison.Ordinal) ||
            normalized.Contains("powershell", StringComparison.Ordinal))
        {
            return "Command";
        }
        if (normalized is "view" or "read" or "read_file" or "grep" or "rg" or
            "glob" or "find" ||
            normalized.Contains("read", StringComparison.Ordinal) ||
            normalized.Contains("view", StringComparison.Ordinal))
        {
            return "Read";
        }
        return "Other";
    }

    private static string ReadNormalizedCommand(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }
        foreach (var property in arguments.EnumerateObject())
        {
            if (property.Name is not ("command" or "script") ||
                property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            return RedactSensitiveText(
                HostObservedToolLocator.NormalizeCommand(
                    property.Value.GetString() ?? string.Empty));
        }
        return string.Empty;
    }

    private static string NormalizeArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return "{}";
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteNormalizedJson(writer, arguments, propertyName: null);
        }
        var normalized = Encoding.UTF8.GetString(stream.ToArray());
        return normalized;
    }

    private static void WriteNormalizedJson(
        Utf8JsonWriter writer,
        JsonElement value,
        string? propertyName)
    {
        var sensitive = propertyName is not null &&
                        IsSensitiveArgumentName(propertyName);
        if (sensitive)
        {
            writer.WriteStringValue("<redacted>");
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteNormalizedJson(writer, property.Value, property.Name);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteNormalizedJson(writer, item, propertyName);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(
                    RedactSensitiveText(
                        HostObservedToolLocator.Normalize(
                            value.GetString() ?? string.Empty)));
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitiveArgumentName(string name) =>
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("apiKey", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("api_key", StringComparison.OrdinalIgnoreCase);

    private static string ResolveWorkingDirectory(
        JsonElement arguments,
        string defaultWorkingDirectory)
    {
        var supplied = arguments.ValueKind == JsonValueKind.Object
            ? arguments.EnumerateObject()
                .FirstOrDefault(property =>
                    property.Name.Equals("cwd", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals(
                        "workingDirectory",
                        StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals(
                        "workdir",
                        StringComparison.OrdinalIgnoreCase))
                .Value
            : default;
        var candidate = supplied.ValueKind == JsonValueKind.String
            ? supplied.GetString()
            : defaultWorkingDirectory;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return string.Empty;
        }
        try
        {
            return Path.GetFullPath(
                Path.IsPathRooted(candidate)
                    ? candidate
                    : Path.Combine(defaultWorkingDirectory, candidate));
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            return string.Empty;
        }
    }

    private static int? ReadToolExitCode(JsonElement payload)
    {
        if (ReadInt32(payload, "exitCode") is { } exitCode)
        {
            return exitCode;
        }
        return payload.TryGetProperty("result", out var result) &&
               result.ValueKind == JsonValueKind.Object
            ? ReadInt32(result, "exitCode")
            : null;
    }

    private static ToolResultEvidence ReadResultEvidence(JsonElement payload)
    {
        if (!payload.TryGetProperty("result", out var result) ||
            result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new ToolResultEvidence(string.Empty, string.Empty);
        }

        var serialized = result.GetRawText();
        var digest = "sha256:" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(serialized)))
            .ToLowerInvariant();
        var summary = ExtractResultSummary(result);
        return new ToolResultEvidence(digest, summary);
    }

    private static string ExtractResultSummary(JsonElement result)
    {
        string? value = null;
        if (result.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[]
                     {
                         "content",
                         "standardOutput",
                         "stdout",
                         "output",
                         "message",
                         "text"
                     })
            {
                if (result.TryGetProperty(name, out var candidate) &&
                    candidate.ValueKind == JsonValueKind.String)
                {
                    value = candidate.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        break;
                    }
                }
                else if (name == "content" && candidate.ValueKind == JsonValueKind.Array)
                {
                    value = string.Join(" ", candidate.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.Object &&
                                       ReadString(item, "type") == "text")
                        .Select(item => ReadString(item, "text"))
                        .Where(text => !string.IsNullOrWhiteSpace(text)));
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        value = "MCP returned non-text content.";
                    }
                    break;
                }
            }
        }
        else if (result.ValueKind == JsonValueKind.String)
        {
            value = result.GetString();
        }

        value ??= result.GetRawText();
        var normalized = RedactSensitiveText(
            HostObservedToolLocator.Normalize(value));
        return normalized.Length <= MaximumResultSummaryCharacters
            ? normalized
            : normalized[..MaximumResultSummaryCharacters];
    }

    private static string RedactSensitiveText(string value) =>
        TokenValuePattern().Replace(
            SensitiveValuePattern().Replace(
                value,
                match =>
                    $"{match.Groups["name"].Value}" +
                    $"{match.Groups["separator"].Value}<redacted>"),
            "<redacted>");

    private static bool IsSuccessfulToolCompletion(
        JsonElement payload,
        int? exitCode)
    {
        if (payload.TryGetProperty("result", out var result) &&
            result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("isError", out var isError) &&
            isError.ValueKind == JsonValueKind.True)
        {
            return false;
        }
        bool? explicitSuccess = null;
        if (payload.TryGetProperty("success", out var successElement) &&
            successElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            explicitSuccess = successElement.GetBoolean();
        }
        if (exitCode is { } observedExitCode)
        {
            return observedExitCode == 0 && explicitSuccess != false;
        }
        return explicitSuccess == true;
    }

    private sealed record PendingToolCall(
        string ToolName,
        string ArgumentsSummary,
        string ToolType,
        string NormalizedCommand,
        string NormalizedArguments,
        string WorkingDirectory);

    private readonly record struct ToolResultEvidence(
        string Digest,
        string Summary);
}

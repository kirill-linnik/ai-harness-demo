using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AiHarnessDemo.Core.Reasoning;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace AiHarnessDemo.Services;

internal enum CopilotSessionJournalState
{
    Missing,
    Active,
    Completed,
    Interrupted
}

internal sealed record CopilotSessionSnapshot(
    Guid SessionId,
    string CopilotHome,
    string SessionDirectory,
    CopilotSessionJournalState State,
    string WorkspacePath,
    string AgentName,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    AgentRunResult? Result,
    IReadOnlyList<int> ActiveProcessIds,
    string Detail);

public sealed class CopilotSessionJournal
{
    private const int ProcessExitWaitMilliseconds = 5_000;
    private const int MaximumBindingScanLines = 512;

    internal sealed record SessionWorkspaceBinding(
        string WorkspacePath,
        DateTimeOffset? StartedAt);

    internal string ExpectedHome() =>
        CopilotReasoningHost.ResolveCopilotSessionHome();

    internal Task<CopilotSessionSnapshot> InspectAsync(
        string copilotHome,
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        InspectDirectoryAsync(
            Path.GetFullPath(copilotHome),
            Path.Combine(
                Path.GetFullPath(copilotHome),
                "session-state",
                sessionId.ToString("D")),
            sessionId,
            cancellationToken);

    internal async Task<CopilotSessionSnapshot?> DiscoverLatestAsync(
        string copilotHome,
        string workspacePath,
        string agentName,
        DateTimeOffset? startedAt,
        CancellationToken cancellationToken = default)
    {
        var normalizedHome = Path.GetFullPath(copilotHome);
        var sessionRoot = Path.Combine(normalizedHome, "session-state");
        if (!Directory.Exists(sessionRoot))
        {
            return null;
        }

        var notBefore = (startedAt ?? DateTimeOffset.UtcNow.AddDays(-1)).AddMinutes(-2);
        CopilotSessionSnapshot? latest = null;
        foreach (var directory in Directory.EnumerateDirectories(sessionRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var journalPath = Path.Combine(directory, "events.jsonl");
            if (!File.Exists(journalPath) ||
                File.GetLastWriteTimeUtc(journalPath) < notBefore.UtcDateTime ||
                !Guid.TryParse(Path.GetFileName(directory), out var sessionId))
            {
                continue;
            }

            var snapshot = await InspectDirectoryAsync(
                normalizedHome,
                directory,
                sessionId,
                cancellationToken);
            if (snapshot.StartedAt is null ||
                !PathEquals(snapshot.WorkspacePath, workspacePath) ||
                !string.Equals(snapshot.AgentName, agentName, StringComparison.OrdinalIgnoreCase) ||
                snapshot.StartedAt < notBefore)
            {
                continue;
            }

            if (latest is null || snapshot.StartedAt > latest.StartedAt)
            {
                latest = snapshot;
            }
        }

        return latest;
    }

    internal bool TryStopActiveSession(CopilotSessionSnapshot snapshot)
    {
        foreach (var processId in snapshot.ActiveProcessIds)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    continue;
                }

                if (!string.Equals(
                        process.ProcessName,
                        "copilot",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (snapshot.StartedAt is not { } sessionStartedAt ||
                    !IsProcessStartOwnedBySession(
                        new DateTimeOffset(process.StartTime).ToUniversalTime(),
                        sessionStartedAt) ||
                    !CommandLineArgumentsBelongToSession(
                        TryReadProcessArguments(processId),
                        snapshot.SessionId))
                {
                    return false;
                }

                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(ProcessExitWaitMilliseconds))
                {
                    return false;
                }
            }
            catch (ArgumentException)
            {
                // The process exited between journal inspection and reconciliation.
            }
            catch (InvalidOperationException)
            {
                // The process exited between journal inspection and reconciliation.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        return true;
    }

    internal async Task<int> DeleteWorkspaceSessionsAsync(
        IEnumerable<string> copilotHomes,
        string workspacePath,
        IReadOnlyCollection<Guid> knownSessionIds,
        CancellationToken cancellationToken = default)
    {
        var deleted = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        var homes = copilotHomes
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(
                         OperatingSystem.IsWindows()
                             ? StringComparer.OrdinalIgnoreCase
                             : StringComparer.Ordinal)
            .ToList();
        if (homes.Count == 0)
        {
            homes.Add(ExpectedHome());
        }
        foreach (var homeValue in homes)
        {
            var home = Path.GetFullPath(homeValue);
            var sessionRoot = Path.Combine(home, "session-state");
            if (!Directory.Exists(sessionRoot))
            {
                continue;
            }

            foreach (var sessionId in knownSessionIds)
            {
                var knownDirectory = Path.Combine(
                    sessionRoot,
                    sessionId.ToString("D"));
                if (Directory.Exists(knownDirectory))
                {
                    var binding = await ReadWorkspaceBindingAsync(
                        knownDirectory,
                        requireStartedAt: true,
                        cancellationToken: cancellationToken);
                    if (string.IsNullOrWhiteSpace(binding.WorkspacePath) &&
                        (File.Exists(Path.Combine(
                             knownDirectory,
                             "workspace.yaml")) ||
                         File.Exists(Path.Combine(
                             knownDirectory,
                             "events.jsonl"))))
                    {
                        throw new InvalidOperationException(
                            $"Copilot session {sessionId:D} has no verifiable workspace binding and will not be deleted.");
                    }
                    if (!string.IsNullOrWhiteSpace(binding.WorkspacePath) &&
                        !PathEquals(binding.WorkspacePath, workspacePath))
                    {
                        throw new InvalidOperationException(
                            $"Copilot session {sessionId:D} is not bound to the flow workspace and will not be deleted.");
                    }
                    var activeProcessIds = FindActiveProcessIds(
                        knownDirectory);
                    if (activeProcessIds.Count > 0)
                    {
                        if (binding.StartedAt is null)
                        {
                            throw new InvalidOperationException(
                                $"Copilot session {sessionId:D} has an active PID lock but no verifiable start time; process ownership cannot be proven.");
                        }
                        var snapshot = Missing(
                            sessionId,
                            home,
                            knownDirectory,
                            "Copilot session is being deleted.") with
                        {
                            State = CopilotSessionJournalState.Active,
                            WorkspacePath = binding.WorkspacePath,
                            StartedAt = binding.StartedAt,
                            ActiveProcessIds = activeProcessIds
                        };
                        if (!TryStopActiveSession(snapshot))
                        {
                            throw new InvalidOperationException(
                                $"Copilot session {snapshot.SessionId:D} could not be stopped before deletion.");
                        }
                    }
                    DeleteSessionDirectory(knownDirectory);
                    deleted.Add(knownDirectory);
                }
            }

            foreach (var directory in Directory.EnumerateDirectories(sessionRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (deleted.Contains(directory) ||
                    !Guid.TryParse(Path.GetFileName(directory), out var sessionId))
                {
                    continue;
                }
                var binding = await ReadWorkspaceBindingAsync(
                    directory,
                    requireStartedAt: false,
                    cancellationToken: cancellationToken);
                if (!PathEquals(binding.WorkspacePath, workspacePath))
                {
                    continue;
                }
                var activeProcessIds = FindActiveProcessIds(
                    directory);
                if (activeProcessIds.Count > 0)
                {
                    binding = await ReadWorkspaceBindingAsync(
                        directory,
                        requireStartedAt: true,
                        cancellationToken: cancellationToken);
                    if (!PathEquals(binding.WorkspacePath, workspacePath))
                    {
                        continue;
                    }
                    if (binding.StartedAt is null)
                    {
                        throw new InvalidOperationException(
                            $"Copilot session {sessionId:D} has an active PID lock but no verifiable start time; process ownership cannot be proven.");
                    }
                    var snapshot = Missing(
                        sessionId,
                        home,
                        directory,
                        "Copilot session is being deleted.") with
                    {
                        State = CopilotSessionJournalState.Active,
                        WorkspacePath = binding.WorkspacePath,
                        StartedAt = binding.StartedAt,
                        ActiveProcessIds = activeProcessIds
                    };
                    if (!TryStopActiveSession(snapshot))
                    {
                        throw new InvalidOperationException(
                            $"Copilot session {snapshot.SessionId:D} could not be stopped before deletion.");
                    }
                }
                DeleteSessionDirectory(directory);
                deleted.Add(directory);
            }
        }
        return deleted.Count;
    }

    internal static async Task<SessionWorkspaceBinding> ReadWorkspaceBindingAsync(
        string sessionDirectory,
        bool requireStartedAt,
        CancellationToken cancellationToken)
    {
        var workspacePath = string.Empty;
        DateTimeOffset? startedAt = null;
        var workspaceMetadataPath =
            Path.Combine(sessionDirectory, "workspace.yaml");
        if (File.Exists(workspaceMetadataPath))
        {
            try
            {
                var yaml = new YamlStream();
                yaml.Load(new StringReader(
                    await File.ReadAllTextAsync(
                        workspaceMetadataPath,
                        cancellationToken)));
                if (yaml.Documents.Count == 1 &&
                    yaml.Documents[0].RootNode is YamlMappingNode mapping)
                {
                    foreach (var pair in mapping.Children)
                    {
                        if (pair.Key is YamlScalarNode { Value: "cwd" } &&
                            pair.Value is YamlScalarNode { Value: { } cwd })
                        {
                            workspacePath = cwd;
                        }
                        else if (pair.Key is YamlScalarNode
                                 {
                                     Value: "created_at"
                                 } &&
                                 pair.Value is YamlScalarNode
                                 {
                                     Value: { } createdAt
                                 } &&
                                 DateTimeOffset.TryParse(
                                     createdAt,
                                     out var parsedCreatedAt))
                        {
                            startedAt = parsedCreatedAt;
                        }
                    }
                    if (!requireStartedAt &&
                        !string.IsNullOrWhiteSpace(workspacePath))
                    {
                        return new SessionWorkspaceBinding(
                            workspacePath,
                            startedAt);
                    }
                }
            }
            catch (YamlException exception)
            {
                throw new InvalidOperationException(
                    $"Copilot workspace metadata '{workspaceMetadataPath}' is invalid.",
                    exception);
            }
        }

        var journalPath = Path.Combine(sessionDirectory, "events.jsonl");
        if (!File.Exists(journalPath))
        {
            return new SessionWorkspaceBinding(
                workspacePath,
                startedAt);
        }

        await using var stream = new FileStream(
            journalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var scannedLines = 0;
        while ((requireStartedAt ||
                scannedLines++ < MaximumBindingScanLines) &&
               await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!CopilotJsonlParser.TryPayload(
                    line,
                    out var eventType,
                    out var document,
                    out var payload))
            {
                continue;
            }

            using (document)
            {
                if (eventType is "session.start" or "session.resume" &&
                    payload.TryGetProperty("context", out var context) &&
                    context.ValueKind == JsonValueKind.Object)
                {
                    workspacePath =
                        CopilotJsonlParser.ReadString(context, "cwd") ??
                        workspacePath;
                    startedAt =
                        ReadTimestamp(document.RootElement) ??
                        startedAt;
                    if (!requireStartedAt &&
                        eventType == "session.start")
                    {
                        return new SessionWorkspaceBinding(
                            workspacePath,
                            startedAt);
                    }
                }
                else if (eventType == "session.resume")
                {
                    startedAt =
                        ReadTimestamp(document.RootElement) ??
                        startedAt;
                }
            }
        }
        return new SessionWorkspaceBinding(
            workspacePath,
            startedAt);
    }

    internal static async Task<CopilotSessionSnapshot> InspectDirectoryAsync(
        string copilotHome,
        string sessionDirectory,
        Guid expectedSessionId,
        CancellationToken cancellationToken = default)
    {
        var journalPath = Path.Combine(sessionDirectory, "events.jsonl");
        if (!File.Exists(journalPath))
        {
            return Missing(
                expectedSessionId,
                copilotHome,
                sessionDirectory,
                "Copilot session journal was not found.");
        }

        var validJournal = new StringBuilder();
        var observedSessionId = expectedSessionId;
        var workspacePath = string.Empty;
        var agentName = string.Empty;
        DateTimeOffset? startedAt = null;
        DateTimeOffset? completedAt = null;
        var turnOpen = false;
        var currentTurnHasFinalMessage = false;
        var lastTurnCompletedWithFinalMessage = false;
        var routineShutdown = false;
        var shutdownFailed = false;

        await using var stream = new FileStream(
            journalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!CopilotJsonlParser.TryPayload(
                    line,
                    out var eventType,
                    out var document,
                    out var payload))
            {
                continue;
            }

            using (document)
            {
                var timestamp = ReadTimestamp(document.RootElement);
                if (eventType is "session.start" or "session.resume")
                {
                    validJournal.Clear();
                    startedAt = timestamp ?? startedAt;
                    completedAt = null;
                    turnOpen = false;
                    currentTurnHasFinalMessage = false;
                    lastTurnCompletedWithFinalMessage = false;
                    routineShutdown = false;
                    shutdownFailed = false;
                }
                validJournal.AppendLine(line);
                switch (eventType)
                {
                    case "session.start":
                        if (Guid.TryParse(
                                CopilotJsonlParser.ReadString(payload, "sessionId"),
                                out var parsedSessionId))
                        {
                            observedSessionId = parsedSessionId;
                        }
                        startedAt = timestamp;
                        if (payload.TryGetProperty("context", out var context) &&
                            context.ValueKind == JsonValueKind.Object)
                        {
                            workspacePath =
                                CopilotJsonlParser.ReadString(context, "cwd") ??
                                workspacePath;
                        }
                        break;

                    case "session.resume":
                        if (Guid.TryParse(
                                CopilotJsonlParser.ReadString(payload, "sessionId"),
                                out var resumedSessionId))
                        {
                            observedSessionId = resumedSessionId;
                        }
                        if (payload.TryGetProperty(
                                "context",
                                out var resumedContext) &&
                            resumedContext.ValueKind == JsonValueKind.Object)
                        {
                            workspacePath =
                                CopilotJsonlParser.ReadString(
                                    resumedContext,
                                    "cwd") ??
                                workspacePath;
                        }
                        break;

                    case "subagent.selected":
                        agentName =
                            CopilotJsonlParser.ReadString(payload, "agentDisplayName") ??
                            CopilotJsonlParser.ReadString(payload, "agentName") ??
                            agentName;
                        break;

                    case "assistant.turn_start":
                        if (!CopilotJsonlParser.IsSubAgentEvent(document.RootElement))
                        {
                            turnOpen = true;
                            currentTurnHasFinalMessage = false;
                            lastTurnCompletedWithFinalMessage = false;
                        }
                        break;

                    case "assistant.message":
                        if (!CopilotJsonlParser.IsSubAgentEvent(document.RootElement))
                        {
                            var content =
                                CopilotJsonlParser.ReadString(payload, "content") ??
                                CopilotJsonlParser.ReadString(payload, "message");
                            currentTurnHasFinalMessage =
                                !string.IsNullOrWhiteSpace(content) &&
                                !HasToolRequests(payload);
                        }
                        break;

                    case "assistant.turn_end":
                        if (!CopilotJsonlParser.IsSubAgentEvent(document.RootElement))
                        {
                            turnOpen = false;
                            lastTurnCompletedWithFinalMessage = currentTurnHasFinalMessage;
                            if (lastTurnCompletedWithFinalMessage)
                            {
                                completedAt = timestamp ?? completedAt;
                            }
                        }
                        break;

                    case "session.shutdown":
                        routineShutdown = string.Equals(
                            CopilotJsonlParser.ReadString(payload, "shutdownType"),
                            "routine",
                            StringComparison.OrdinalIgnoreCase);
                        shutdownFailed = !routineShutdown;
                        completedAt = timestamp ?? completedAt;
                        break;
                }
            }
        }

        workspacePath ??= string.Empty;
        var activeProcessIds = FindActiveProcessIds(sessionDirectory);
        var parsed = CopilotJsonlParser.Parse(
            validJournal.ToString(),
            workingDirectory: workspacePath);
        var completed =
            parsed.Success &&
            (routineShutdown ||
             (!turnOpen &&
              lastTurnCompletedWithFinalMessage &&
              activeProcessIds.Count == 0));
        if (completed)
        {
            return new CopilotSessionSnapshot(
                observedSessionId,
                Path.GetFullPath(copilotHome),
                sessionDirectory,
                CopilotSessionJournalState.Completed,
                workspacePath,
                agentName,
                startedAt,
                completedAt,
                parsed,
                activeProcessIds,
                "Copilot session completed with a recoverable assistant response.");
        }

        if (activeProcessIds.Count > 0)
        {
            return new CopilotSessionSnapshot(
                observedSessionId,
                Path.GetFullPath(copilotHome),
                sessionDirectory,
                CopilotSessionJournalState.Active,
                workspacePath,
                agentName,
                startedAt,
                completedAt,
                null,
                activeProcessIds,
                "Copilot session is still owned by a live process.");
        }

        return new CopilotSessionSnapshot(
            observedSessionId,
            Path.GetFullPath(copilotHome),
            sessionDirectory,
            CopilotSessionJournalState.Interrupted,
            workspacePath,
            agentName,
            startedAt,
            completedAt,
            null,
            activeProcessIds,
            shutdownFailed
                ? "Copilot session shut down with an error."
                : "Copilot session ended before producing a final handoff.");
    }

    private static CopilotSessionSnapshot Missing(
        Guid sessionId,
        string copilotHome,
        string sessionDirectory,
        string detail) =>
        new(
            sessionId,
            Path.GetFullPath(copilotHome),
            sessionDirectory,
            CopilotSessionJournalState.Missing,
            string.Empty,
            string.Empty,
            null,
            null,
            null,
            [],
            detail);

    private static List<int> FindActiveProcessIds(
        string sessionDirectory)
    {
        var active = new List<int>();
        foreach (var lockPath in Directory.EnumerateFiles(
                     sessionDirectory,
                     "inuse.*.lock",
                     SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(lockPath);
            const string prefix = "inuse.";
            const string suffix = ".lock";
            var processText = fileName[prefix.Length..^suffix.Length];
            if (!int.TryParse(processText, out var processId))
            {
                continue;
            }

            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.HasExited &&
                    string.Equals(
                        process.ProcessName,
                        "copilot",
                        StringComparison.OrdinalIgnoreCase))
                {
                    active.Add(processId);
                }
            }
            catch (ArgumentException)
            {
                // Stale lock files are expected after abrupt termination.
            }
            catch (InvalidOperationException)
            {
                // The process exited while the lock was being inspected.
            }
        }

        return active;
    }

    internal static bool IsProcessStartOwnedBySession(
        DateTimeOffset processStartedAt,
        DateTimeOffset sessionStartedAt) =>
        processStartedAt >= sessionStartedAt.AddMinutes(-2) &&
        processStartedAt <= sessionStartedAt.AddMinutes(2);

    internal static bool CommandLineBelongsToSession(
        string? commandLine,
        Guid sessionId) =>
        !string.IsNullOrWhiteSpace(commandLine) &&
        CommandLineArgumentsBelongToSession(
            TokenizeCommandLine(commandLine),
            sessionId);

    internal static bool CommandLineArgumentsBelongToSession(
        IReadOnlyList<string>? arguments,
        Guid sessionId)
    {
        if (arguments is null)
        {
            return false;
        }
        var formatted = sessionId.ToString("D");
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument.Equals(
                    "-p",
                    StringComparison.OrdinalIgnoreCase) ||
                argument.Equals(
                    "--prompt",
                    StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }
            if (argument.StartsWith(
                    "--prompt=",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (argument.Equals(
                    "--session-id",
                    StringComparison.OrdinalIgnoreCase) ||
                argument.Equals(
                    "--resume",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < arguments.Count &&
                    arguments[index + 1].Equals(
                        formatted,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (argument.StartsWith(
                         "--session-id=",
                         StringComparison.OrdinalIgnoreCase) ||
                     argument.StartsWith(
                         "--resume=",
                         StringComparison.OrdinalIgnoreCase))
            {
                var separator = argument.IndexOf('=');
                if (argument[(separator + 1)..].Equals(
                        formatted,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static IReadOnlyList<string> TokenizeCommandLine(string commandLine)
    {
        var tokens = new List<string>();
        var index = 0;
        while (index < commandLine.Length)
        {
            while (index < commandLine.Length &&
                   char.IsWhiteSpace(commandLine[index]))
            {
                index++;
            }
            if (index >= commandLine.Length)
            {
                break;
            }

            var current = new StringBuilder();
            var inQuotes = false;
            do
            {
                if (commandLine[index] == '\\')
                {
                    var slashStart = index;
                    while (index < commandLine.Length &&
                           commandLine[index] == '\\')
                    {
                        index++;
                    }
                    var slashCount = index - slashStart;
                    if (index < commandLine.Length &&
                        commandLine[index] == '"')
                    {
                        current.Append('\\', slashCount / 2);
                        if (slashCount % 2 == 1)
                        {
                            current.Append('"');
                            index++;
                        }
                        else if (inQuotes &&
                                 index + 1 < commandLine.Length &&
                                 commandLine[index + 1] == '"')
                        {
                            current.Append('"');
                            index += 2;
                        }
                        else
                        {
                            inQuotes = !inQuotes;
                            index++;
                        }
                    }
                    else
                    {
                        current.Append('\\', slashCount);
                    }
                    continue;
                }
                if (commandLine[index] == '"')
                {
                    if (inQuotes &&
                        index + 1 < commandLine.Length &&
                        commandLine[index + 1] == '"')
                    {
                        current.Append('"');
                        index += 2;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                        index++;
                    }
                    continue;
                }
                if (!inQuotes &&
                    char.IsWhiteSpace(commandLine[index]))
                {
                    break;
                }
                current.Append(commandLine[index]);
                index++;
            }
            while (index < commandLine.Length);
            tokens.Add(current.ToString());
        }
        return tokens;
    }

    private static IReadOnlyList<string>? TryReadProcessArguments(
        int processId)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var path = $"/proc/{processId}/cmdline";
                if (!File.Exists(path))
                {
                    return null;
                }
                var values = Encoding.UTF8
                    .GetString(File.ReadAllBytes(path))
                    .Split('\0');
                var count = values.Length > 0 &&
                            values[^1].Length == 0
                    ? values.Length - 1
                    : values.Length;
                return count > 0
                    ? values[..count]
                    : null;
            }
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(
                $"(Get-CimInstance Win32_Process -Filter \"ProcessId = {processId}\").CommandLine");

            using var query = Process.Start(startInfo);
            if (query is null)
            {
                return null;
            }
            var outputTask = query.StandardOutput.ReadToEndAsync();
            var errorTask = query.StandardError.ReadToEndAsync();
            if (!query.WaitForExit(5_000))
            {
                try
                {
                    query.Kill(entireProcessTree: true);
                    query.WaitForExit(1_000);
                }
                catch (InvalidOperationException)
                {
                    // The query exited at the timeout boundary.
                }
                return null;
            }
            var output = outputTask.GetAwaiter().GetResult().Trim();
            _ = errorTask.GetAwaiter().GetResult();
            return query.ExitCode == 0 && output.Length > 0
                ? TokenizeCommandLine(output)
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or
                InvalidOperationException or
                System.ComponentModel.Win32Exception or
                UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool HasToolRequests(JsonElement payload) =>
        payload.TryGetProperty("toolRequests", out var toolRequests) &&
        toolRequests.ValueKind == JsonValueKind.Array &&
        toolRequests.GetArrayLength() > 0;

    private static DateTimeOffset? ReadTimestamp(JsonElement root) =>
        root.TryGetProperty("timestamp", out var timestamp) &&
        timestamp.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(timestamp.GetString(), out var parsed)
            ? parsed
            : null;

    private static bool PathEquals(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            comparison);
    }

    private static void DeleteSessionDirectory(string directory)
    {
        var parent = Directory.GetParent(directory)
            ?? throw new InvalidOperationException(
                $"Copilot session path has no parent: {directory}");
        if (!string.Equals(
                parent.Name,
                "session-state",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to delete a path outside a Copilot session-state directory: {directory}");
        }
        Directory.Delete(directory, recursive: true);
    }
}

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AiHarnessDemo.Core.Reasoning;

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

                if (snapshot.StartedAt is { } sessionStartedAt &&
                    new DateTimeOffset(process.StartTime).ToUniversalTime() <
                    sessionStartedAt.AddMinutes(-2))
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
                var snapshot = await InspectDirectoryAsync(
                    home,
                    directory,
                    sessionId,
                    cancellationToken);
                if (!PathEquals(snapshot.WorkspacePath, workspacePath))
                {
                    continue;
                }
                if (snapshot.ActiveProcessIds.Count > 0 &&
                    !TryStopActiveSession(snapshot))
                {
                    throw new InvalidOperationException(
                        $"Copilot session {snapshot.SessionId:D} could not be stopped before deletion.");
                }
                DeleteSessionDirectory(directory);
                deleted.Add(directory);
            }
        }
        return deleted.Count;
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

        var activeProcessIds = FindActiveProcessIds(sessionDirectory, startedAt);
        var parsed = CopilotJsonlParser.Parse(validJournal.ToString());
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
        string sessionDirectory,
        DateTimeOffset? sessionStartedAt)
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
                        StringComparison.OrdinalIgnoreCase) &&
                    (sessionStartedAt is null ||
                     new DateTimeOffset(process.StartTime).ToUniversalTime() >=
                     sessionStartedAt.Value.AddMinutes(-2)))
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

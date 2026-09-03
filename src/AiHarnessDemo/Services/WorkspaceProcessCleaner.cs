using System.Diagnostics;
using System.Text.Json;

namespace AiHarnessDemo.Services;

public sealed record WorkspaceProcessCleanupResult(
    IReadOnlyList<int> ProcessIds,
    IReadOnlyList<int> ListeningPorts)
{
    public static WorkspaceProcessCleanupResult Empty { get; } = new([], []);
}

public interface IWorkspaceProcessCleaner
{
    Task<WorkspaceProcessCleanupResult> StopAsync(
        string workspacePath,
        CancellationToken cancellationToken = default);
}

public sealed class WorkspaceProcessCleaner(
    ProcessRunner processRunner,
    ILogger<WorkspaceProcessCleaner> logger) : IWorkspaceProcessCleaner
{
    public async Task<WorkspaceProcessCleanupResult> StopAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) ||
            !Directory.Exists(workspacePath))
        {
            return WorkspaceProcessCleanupResult.Empty;
        }

        var candidates = OperatingSystem.IsWindows()
            ? await FindWindowsProcessesAsync(workspacePath, cancellationToken)
            : FindProcProcesses(workspacePath);
        var stopped = new List<int>();
        var ports = new HashSet<int>();
        foreach (var candidate in candidates
                     .Where(item => item.ProcessId != Environment.ProcessId)
                     .OrderByDescending(item => item.ProcessId))
        {
            foreach (var port in candidate.ListeningPorts)
            {
                ports.Add(port);
            }

            try
            {
                using var process = Process.GetProcessById(candidate.ProcessId);
                if (process.HasExited)
                {
                    continue;
                }
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                stopped.Add(candidate.ProcessId);
            }
            catch (ArgumentException)
            {
                // The process exited between discovery and cleanup.
            }
            catch (InvalidOperationException)
            {
                // The process exited between discovery and cleanup.
            }
        }

        logger.LogInformation(
            "Stopped {ProcessCount} workspace-owned process(es) and released ports {Ports} for {WorkspacePath}",
            stopped.Count,
            string.Join(", ", ports.Order()),
            workspacePath);
        return new WorkspaceProcessCleanupResult(
            stopped,
            ports.Order().ToList());
    }

    private async Task<IReadOnlyList<WorkspaceProcess>> FindWindowsProcessesAsync(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        const string script = """
            $needle = [IO.Path]::GetFullPath($env:AI_HARNESS_WORKSPACE_PATH)
            $current = $PID
            $matches = @(
              Get-CimInstance Win32_Process |
                Where-Object {
                  $_.ProcessId -ne $current -and
                  $_.CommandLine -and
                  $_.CommandLine.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0
                } |
                ForEach-Object {
                  $ports = @(
                    Get-NetTCPConnection -State Listen -OwningProcess $_.ProcessId -ErrorAction SilentlyContinue |
                      Select-Object -ExpandProperty LocalPort -Unique
                  )
                  if ($ports.Count -gt 0) {
                    [pscustomobject]@{
                      processId = [int]$_.ProcessId
                      name = [string]$_.Name
                      listeningPorts = $ports
                    }
                  }
                }
            )
            ConvertTo-Json -InputObject $matches -Compress -Depth 4
            """;
        var result = await processRunner.RunAsync(
            "powershell",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
            workspacePath,
            TimeSpan.FromSeconds(30),
            cancellationToken,
            environmentVariables: new Dictionary<string, string>
            {
                ["AI_HARNESS_WORKSPACE_PATH"] = Path.GetFullPath(workspacePath)
            });
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to discover workspace-owned processes: {result.CombinedOutput}");
        }
        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return [];
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var rows = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().Select(item => item.Clone()).ToList()
            : document.RootElement.ValueKind == JsonValueKind.Object
                ? [document.RootElement.Clone()]
                : [];
        return rows.Select(item => new WorkspaceProcess(
                item.GetProperty("processId").GetInt32(),
                item.TryGetProperty("name", out var name)
                    ? name.GetString() ?? string.Empty
                    : string.Empty,
                item.TryGetProperty("listeningPorts", out var portElement)
                    ? ReadPorts(portElement)
                    : []))
            .ToList();
    }

    private static IReadOnlyList<WorkspaceProcess> FindProcProcesses(string workspacePath)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc"))
        {
            return [];
        }

        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        var comparison = StringComparison.Ordinal;
        var result = new List<WorkspaceProcess>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var processId) ||
                processId == Environment.ProcessId)
            {
                continue;
            }
            try
            {
                var cwd = Directory.ResolveLinkTarget(
                    Path.Combine(directory, "cwd"),
                    returnFinalTarget: true)?.FullName ?? string.Empty;
                var commandLine = File.Exists(Path.Combine(directory, "cmdline"))
                    ? File.ReadAllText(Path.Combine(directory, "cmdline")).Replace('\0', ' ')
                    : string.Empty;
                if (!IsWorkspaceOwned(workspace, cwd, commandLine, comparison) ||
                    !LooksLikeServer(commandLine))
                {
                    continue;
                }
                result.Add(new WorkspaceProcess(processId, string.Empty, []));
            }
            catch (IOException)
            {
                // The process exited or denied inspection.
            }
            catch (UnauthorizedAccessException)
            {
                // The process cannot be inspected by this user.
            }
        }
        return result;
    }

    internal static bool IsWorkspaceOwned(
        string workspacePath,
        string processWorkingDirectory,
        string commandLine,
        StringComparison comparison)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        var rootPrefix = root + Path.DirectorySeparatorChar;
        var cwd = string.IsNullOrWhiteSpace(processWorkingDirectory)
            ? string.Empty
            : Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(processWorkingDirectory));
        return string.Equals(cwd, root, comparison) ||
               cwd.StartsWith(rootPrefix, comparison) ||
               CommandLineContainsPath(commandLine, root, comparison);
    }

    private static bool CommandLineContainsPath(
        string commandLine,
        string root,
        StringComparison comparison)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }
        var rootPrefix = root + Path.DirectorySeparatorChar;
        return commandLine.Contains(rootPrefix, comparison) ||
               commandLine.EndsWith(root, comparison) ||
               commandLine.Contains($"\"{root}\"", comparison) ||
               commandLine.Contains($"'{root}'", comparison);
    }

    private static bool LooksLikeServer(string commandLine)
    {
        var normalized = commandLine.ToLowerInvariant();
        return normalized.Contains(" serve", StringComparison.Ordinal) ||
               normalized.Contains("http.server", StringComparison.Ordinal) ||
               normalized.Contains("vite", StringComparison.Ordinal) ||
               normalized.Contains("webpack", StringComparison.Ordinal) ||
               normalized.Contains("next dev", StringComparison.Ordinal) ||
               normalized.Contains("dotnet run", StringComparison.Ordinal) ||
               normalized.Contains("--port", StringComparison.Ordinal);
    }

    private static IReadOnlyList<int> ReadPorts(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray()
                .Where(item => item.TryGetInt32(out _))
                .Select(item => item.GetInt32())
                .Distinct()
                .ToList();
        }
        return element.TryGetInt32(out var port) ? [port] : [];
    }

    private sealed record WorkspaceProcess(
        int ProcessId,
        string Name,
        IReadOnlyList<int> ListeningPorts);
}

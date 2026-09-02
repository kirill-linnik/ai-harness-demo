using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AiHarnessDemo.Services;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput =>
        string.Join(
            Environment.NewLine,
            new[] { StandardOutput.Trim(), StandardError.Trim() }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed class ProcessStalledException(string message) : TimeoutException(message);

public sealed class ProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        Action<string>? standardOutputLineReceived = null,
        TimeSpan? stallTimeout = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new ArgumentException("An executable is required.", nameof(executable));
        }

        var resolvedWorkingDirectory = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(resolvedWorkingDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Process working directory does not exist: {resolvedWorkingDirectory}");
        }

        var resolvedExecutable = ExecutableLocator.Resolve(
            executable,
            resolvedWorkingDirectory);
        if (resolvedExecutable is null)
        {
            throw new Win32Exception(
                2,
                $"Executable '{executable}' was not found on PATH.");
        }

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = resolvedWorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        ConfigureInvocation(
            startInfo,
            resolvedExecutable,
            arguments.ToArray(),
            resolvedWorkingDirectory);
        if (environmentVariables is not null)
        {
            foreach (var (name, value) in environmentVariables)
            {
                startInfo.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start '{executable}'.");
        }

        var lastActivityTimestamp = Stopwatch.GetTimestamp();
        void RecordActivity(string line)
        {
            Interlocked.Exchange(ref lastActivityTimestamp, Stopwatch.GetTimestamp());
            standardOutputLineReceived?.Invoke(line);
        }

        var outputTask = ReadOutputAsync(
            process.StandardOutput,
            RecordActivity,
            cancellationToken);
        var errorTask = ReadOutputAsync(
            process.StandardError,
            _ => Interlocked.Exchange(ref lastActivityTimestamp, Stopwatch.GetTimestamp()),
            cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var monitorSource =
            CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token);
        var waitTask = process.WaitForExitAsync(timeoutSource.Token);
        var stallTask = stallTimeout is { } configuredStall && configuredStall > TimeSpan.Zero
            ? MonitorStallAsync(
                () => Interlocked.Read(ref lastActivityTimestamp),
                configuredStall,
                monitorSource.Token)
            : Task.Delay(Timeout.InfiniteTimeSpan, monitorSource.Token);

        try
        {
            var completed = await Task.WhenAny(waitTask, stallTask);
            if (completed == stallTask)
            {
                await stallTask;
            }
            await waitTask;
            monitorSource.Cancel();
        }
        catch (ProcessStalledException)
        {
            Terminate(process);
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Terminate(process);
            throw new TimeoutException(
                $"'{executable}' did not finish within {timeout.TotalMinutes:0.#} minutes.");
        }
        catch (OperationCanceledException)
        {
            Terminate(process);
            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            await outputTask,
            await errorTask);
    }

    private static async Task<string> ReadOutputAsync(
        StreamReader reader,
        Action<string>? lineReceived,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            output.AppendLine(line);
            lineReceived?.Invoke(line);
        }

        return output.ToString();
    }

    private static async Task MonitorStallAsync(
        Func<long> lastActivityTimestamp,
        TimeSpan stallTimeout,
        CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMilliseconds(
            Math.Clamp(stallTimeout.TotalMilliseconds / 4, 100, 1_000));
        while (true)
        {
            await Task.Delay(interval, cancellationToken);
            var elapsed = Stopwatch.GetElapsedTime(lastActivityTimestamp());
            if (elapsed > stallTimeout)
            {
                throw new ProcessStalledException(
                    $"Agent process produced no output for {stallTimeout.TotalSeconds:0.#} seconds.");
            }
        }
    }

    private static void Terminate(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        process.Kill(entireProcessTree: true);
    }

    private static void ConfigureInvocation(
        ProcessStartInfo startInfo,
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            var extension = Path.GetExtension(executable);
            if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                ConfigurePowerShell(startInfo, executable, arguments, workingDirectory);
                return;
            }

            if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            {
                var powerShellShim = Path.ChangeExtension(executable, ".ps1");
                if (File.Exists(powerShellShim) &&
                    TryResolvePowerShell(workingDirectory) is { } powerShell)
                {
                    ConfigurePowerShellWithHost(
                        startInfo,
                        powerShellShim,
                        arguments,
                        powerShell);
                    return;
                }

                throw new Win32Exception(
                    193,
                    $"Windows batch launcher '{executable}' has no runnable PowerShell companion. " +
                    "Use a native executable or install the complete npm command shim.");
            }
        }

        startInfo.FileName = executable;
        AddArguments(startInfo, arguments);
    }

    private static void ConfigurePowerShell(
        ProcessStartInfo startInfo,
        string script,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        var powerShell = TryResolvePowerShell(workingDirectory)
            ?? throw new Win32Exception(
                2,
                $"PowerShell is required to launch '{script}'.");
        ConfigurePowerShellWithHost(startInfo, script, arguments, powerShell);
    }

    private static void ConfigurePowerShellWithHost(
        ProcessStartInfo startInfo,
        string script,
        IReadOnlyList<string> arguments,
        string powerShell)
    {
        startInfo.FileName = powerShell;
        AddArguments(
            startInfo,
            [
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                script,
                .. arguments
            ]);
    }

    private static string? TryResolvePowerShell(string workingDirectory) =>
        ExecutableLocator.Resolve("pwsh", workingDirectory) ??
        ExecutableLocator.Resolve("powershell", workingDirectory);

    private static void AddArguments(
        ProcessStartInfo startInfo,
        IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }
}

public static class ExecutableLocator
{
    public static bool Exists(
        string executable,
        string? workingDirectory = null) =>
        Resolve(executable, workingDirectory) is not null;

    public static string? Resolve(
        string executable,
        string? workingDirectory = null) =>
        Resolve(
            executable,
            workingDirectory,
            Environment.GetEnvironmentVariable("PATH"),
            ManagedCopilotRoot());

    internal static string? Resolve(
        string executable,
        string? workingDirectory,
        string? searchPath,
        string? managedCopilotRoot)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        if (Path.IsPathRooted(executable) || HasDirectorySeparator(executable))
        {
            var path = Path.IsPathRooted(executable)
                ? executable
                : Path.Combine(
                    workingDirectory ?? Environment.CurrentDirectory,
                    executable);
            return ResolveCandidate(path);
        }

        var isBareWindowsCopilot =
            OperatingSystem.IsWindows() &&
            (string.Equals(executable, "copilot", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(executable, "copilot.exe", StringComparison.OrdinalIgnoreCase));
        foreach (var directory in (searchPath ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var normalizedDirectory = directory.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(normalizedDirectory))
            {
                continue;
            }

            var resolved = ResolveCandidate(Path.Combine(normalizedDirectory, executable));
            if (resolved is not null)
            {
                if (isBareWindowsCopilot &&
                    IsInteractiveCopilotBootstrapper(resolved))
                {
                    continue;
                }
                return resolved;
            }
        }

        if (isBareWindowsCopilot)
        {
            return ResolveManagedCopilot(managedCopilotRoot);
        }

        return null;
    }

    private static string? ResolveCandidate(string path)
    {
        if (OperatingSystem.IsWindows() &&
            string.IsNullOrEmpty(Path.GetExtension(path)))
        {
            foreach (var extension in WindowsExecutableExtensions())
            {
                var candidate = path + extension;
                if (IsExecutableFile(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        if (IsExecutableFile(path))
        {
            return Path.GetFullPath(path);
        }

        return null;
    }

    private static bool IsInteractiveCopilotBootstrapper(string executable)
    {
        var extension = Path.GetExtension(executable);
        if (!extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var powerShellShim = Path.ChangeExtension(executable, ".ps1");
        if (!File.Exists(powerShellShim))
        {
            return true;
        }

        try
        {
            return File.ReadLines(powerShellShim).Any(line =>
                line.Contains("Read-Host", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("PromptForChoice", StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string? ManagedCopilotRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localApplicationData)
            ? null
            : Path.Combine(
                localApplicationData,
                "github-copilot-sdk",
                "cli");
    }

    private static string? ResolveManagedCopilot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateDirectories(root)
                .Select(directory =>
                {
                    var executable = Path.Combine(directory, "copilot.exe");
                    return new
                    {
                        Path = executable,
                        SortKey = ManagedCopilotSortKey(directory, executable)
                    };
                })
                .Where(candidate => IsExecutableFile(candidate.Path))
                .OrderByDescending(candidate => candidate.SortKey)
                .Select(candidate => candidate.Path)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static (Version Version, bool Stable, int Revision, DateTime ModifiedAt)
        ManagedCopilotSortKey(string directory, string executable)
    {
        var directoryName = Path.GetFileName(directory);
        var separator = directoryName.IndexOf('-');
        var versionText = separator < 0
            ? directoryName
            : directoryName[..separator];
        var revisionText = separator < 0
            ? string.Empty
            : directoryName[(separator + 1)..];
        _ = Version.TryParse(versionText, out var version);
        _ = int.TryParse(revisionText, out var revision);
        return (
            version ?? new Version(0, 0),
            separator < 0,
            revision,
            File.GetLastWriteTimeUtc(executable));
    }

    private static IEnumerable<string> WindowsExecutableExtensions() =>
        (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
        .Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries)
        .Select(extension =>
            extension.StartsWith('.') ? extension : $".{extension}")
        .Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool IsExecutableFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            const UnixFileMode execute =
                UnixFileMode.UserExecute |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & execute) != 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasDirectorySeparator(string value) =>
        value.Contains(Path.DirectorySeparatorChar) ||
        value.Contains(Path.AltDirectorySeparatorChar);
}

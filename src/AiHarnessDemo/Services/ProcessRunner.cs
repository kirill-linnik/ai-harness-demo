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
        TimeSpan? stallTimeout = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
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
}

public static class ExecutableLocator
{
    public static bool Exists(string executable)
    {
        if (Path.IsPathRooted(executable))
        {
            return File.Exists(executable);
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions.Prepend(string.Empty))
            {
                var candidate = Path.Combine(directory.Trim('"'), executable + extension);
                if (File.Exists(candidate))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

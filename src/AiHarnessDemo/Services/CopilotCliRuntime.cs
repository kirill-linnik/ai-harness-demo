using System.ComponentModel;
using System.Text.RegularExpressions;
using AiHarnessDemo.Infrastructure;

namespace AiHarnessDemo.Services;

public sealed record CopilotCliRuntimeStatus(
    bool Ready,
    string Command,
    string ResolvedPath,
    string Version,
    string Detail,
    DateTimeOffset CheckedAt,
    string ReasoningEffortOption = "--reasoning-effort");

/// <summary>Validates that the configured Copilot CLI supports the non-interactive contract.</summary>
public sealed partial class CopilotCliRuntime(
    ProcessRunner processRunner,
    HarnessPaths paths,
    TimeProvider timeProvider,
    ILogger<CopilotCliRuntime> logger)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly string[] RequiredOptions =
    [
        "--add-dir",
        "--acp",
        "--agent",
        "--allow-tool",
        "--available-tools",
        "--disable-builtin-mcps",
        "--deny-tool",
        "--deny-url",
        "--disallow-temp-dir",
        "--model",
        "--no-ask-user",
        "--no-custom-instructions",
        "--no-eager-powershell-resolution",
        "--no-remote",
        "--no-remote-export",
        "--output-format",
        "--secret-env-vars",
        "--session-id"
    ];

    private readonly Lock _statusLock = new();
    private readonly SemaphoreSlim _probeLock = new(1, 1);
    private CopilotCliRuntimeStatus _current = new(
        Ready: false,
        Command: string.Empty,
        ResolvedPath: string.Empty,
        Version: string.Empty,
        Detail: "Copilot CLI startup validation has not run.",
        CheckedAt: DateTimeOffset.MinValue);

    [GeneratedRegex(
        @"(?<!\d)(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public CopilotCliRuntimeStatus Current
    {
        get
        {
            lock (_statusLock)
            {
                return _current;
            }
        }
    }

    public async Task<CopilotCliRuntimeStatus> GetAsync(
        string command,
        CancellationToken cancellationToken = default)
    {
        var resolvedPath = ExecutableLocator.Resolve(command, paths.Root) ?? string.Empty;
        var current = Current;
        if (string.Equals(current.Command, command, StringComparison.Ordinal) &&
            string.Equals(
                current.ResolvedPath,
                resolvedPath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal) &&
            timeProvider.GetUtcNow() - current.CheckedAt < CacheLifetime)
        {
            return current;
        }

        return await RefreshAsync(command, cancellationToken);
    }

    public async Task<CopilotCliRuntimeStatus> RefreshAsync(
        string command,
        CancellationToken cancellationToken = default)
    {
        await _probeLock.WaitAsync(cancellationToken);
        try
        {
            var resolvedPath = ExecutableLocator.Resolve(command, paths.Root);
            if (resolvedPath is null)
            {
                return Publish(new CopilotCliRuntimeStatus(
                    Ready: false,
                    Command: command,
                    ResolvedPath: string.Empty,
                    Version: string.Empty,
                    Detail:
                    $"Copilot CLI command '{command}' was not found. Install it with " +
                    "'winget install GitHub.Copilot' or 'npm install -g @github/copilot'.",
                    CheckedAt: timeProvider.GetUtcNow()));
            }

            ProcessResult versionResult;
            ProcessResult helpResult;
            try
            {
                versionResult = await processRunner.RunAsync(
                    resolvedPath,
                    ["--version"],
                    paths.Root,
                    ProbeTimeout,
                    cancellationToken);
                helpResult = await processRunner.RunAsync(
                    resolvedPath,
                    ["help"],
                    paths.Root,
                    ProbeTimeout,
                    cancellationToken);
            }
            catch (Win32Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Copilot CLI readiness probe could not launch {ResolvedPath}.",
                    resolvedPath);
                return Publish(Failed(
                    command,
                    resolvedPath,
                    "The resolved Copilot CLI executable could not be launched."));
            }
            catch (TimeoutException exception)
            {
                logger.LogWarning(
                    exception,
                    "Copilot CLI readiness probe timed out for {ResolvedPath}.",
                    resolvedPath);
                return Publish(Failed(
                    command,
                    resolvedPath,
                    "The Copilot CLI readiness check timed out."));
            }

            if (versionResult.ExitCode != 0)
            {
                logger.LogWarning(
                    "Copilot CLI version probe exited with code {ExitCode}: {Diagnostic}",
                    versionResult.ExitCode,
                    Tail(versionResult.CombinedOutput, 1_000));
                return Publish(Failed(
                    command,
                    resolvedPath,
                    $"Copilot CLI failed its version check (exit {versionResult.ExitCode})."));
            }

            if (helpResult.ExitCode != 0)
            {
                logger.LogWarning(
                    "Copilot CLI help probe exited with code {ExitCode}: {Diagnostic}",
                    helpResult.ExitCode,
                    Tail(helpResult.CombinedOutput, 1_000));
                return Publish(Failed(
                    command,
                    resolvedPath,
                    $"Copilot CLI failed its capability check (exit {helpResult.ExitCode})."));
            }

            var missingOptions = RequiredOptions
                .Where(option =>
                    !helpResult.StandardOutput.Contains(
                        option,
                        StringComparison.Ordinal))
                .ToList();
            var reasoningEffortOption =
                helpResult.StandardOutput.Contains(
                    "--reasoning-effort",
                    StringComparison.Ordinal)
                    ? "--reasoning-effort"
                    : helpResult.StandardOutput.Contains(
                        "--effort",
                        StringComparison.Ordinal)
                        ? "--effort"
                        : string.Empty;
            if (string.IsNullOrEmpty(reasoningEffortOption))
            {
                missingOptions.Add("--reasoning-effort (or legacy --effort)");
            }
            if (missingOptions.Count > 0)
            {
                return Publish(Failed(
                    command,
                    resolvedPath,
                    "The detected Copilot CLI does not support the required options: " +
                    string.Join(", ", missingOptions) +
                    ". Update GitHub Copilot CLI."));
            }

            var version = VersionPattern()
                .Match(versionResult.CombinedOutput)
                .Groups["version"]
                .Value;
            var status = new CopilotCliRuntimeStatus(
                Ready: true,
                Command: command,
                ResolvedPath: resolvedPath,
                Version: version,
                Detail: string.IsNullOrWhiteSpace(version)
                    ? "Copilot CLI is ready for non-interactive JSON execution."
                    : $"GitHub Copilot CLI {version} is ready for non-interactive JSON execution.",
                CheckedAt: timeProvider.GetUtcNow(),
                ReasoningEffortOption: reasoningEffortOption);
            return Publish(status);
        }
        finally
        {
            _probeLock.Release();
        }
    }

    private CopilotCliRuntimeStatus Failed(
        string command,
        string resolvedPath,
        string detail) =>
        new(
            Ready: false,
            Command: command,
            ResolvedPath: resolvedPath,
            Version: string.Empty,
            Detail: detail,
            CheckedAt: timeProvider.GetUtcNow());

    private CopilotCliRuntimeStatus Publish(CopilotCliRuntimeStatus status)
    {
        CopilotCliRuntimeStatus previous;
        lock (_statusLock)
        {
            previous = _current;
            _current = status;
        }

        if (previous.Ready == status.Ready &&
            string.Equals(previous.ResolvedPath, status.ResolvedPath, StringComparison.Ordinal) &&
            string.Equals(previous.Version, status.Version, StringComparison.Ordinal) &&
            string.Equals(previous.Detail, status.Detail, StringComparison.Ordinal) &&
            string.Equals(
                previous.ReasoningEffortOption,
                status.ReasoningEffortOption,
                StringComparison.Ordinal))
        {
            return status;
        }

        if (status.Ready)
        {
            logger.LogInformation(
                "Copilot CLI validation passed: {Detail} Executable: {ResolvedPath}",
                status.Detail,
                status.ResolvedPath);
        }
        else
        {
            logger.LogWarning(
                "Copilot CLI validation failed: {Detail}",
                status.Detail);
        }
        return status;
    }

    private static string Tail(string value, int maxCharacters) =>
        value.Length <= maxCharacters ? value : value[^maxCharacters..];
}

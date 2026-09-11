using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Data;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

public static class DemoConflictCodes
{
    public const string CandidateStale = "demo.candidate-stale";
    public const string ManifestStale = "demo.manifest-stale";
    public const string InvalidTransition = "demo.invalid-transition";
    public const string AlreadyStarting = "demo.already-starting";
    public const string PortExhausted = "demo.port-exhausted";
    public const string StartupFailed = "demo.startup-failed";
    public const string NotOwned = "demo.not-owned";
}

public sealed class DemoRuntimeException(
    string code,
    string message,
    int statusCode = StatusCodes.Status409Conflict)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;

    public int StatusCode { get; } = statusCode;
}

public enum DemoCapability
{
    Available,
    OfflineOnly
}

public sealed record DemoRuntimeStatus(
    DemoCapability Capability,
    Guid? InstanceId,
    DemoInstanceState State,
    string? StableUrl,
    string? FailureDetail,
    string? CandidateFingerprint,
    string? ManifestHash);

public sealed record DemoRuntimeOptions
{
    public int FirstPort { get; init; } = 43100;

    public int LastPort { get; init; } = 43299;

    public int MaximumPortAttempts { get; init; } = 40;

    public int HealthPollMilliseconds { get; init; } = 200;

    public int ProxyHealthCacheMilliseconds { get; init; } = 2_000;

    public int ConsecutiveHealthFailureThreshold { get; init; } = 3;

    public void Validate()
    {
        if (FirstPort is < 1024 or > 65535 ||
            LastPort < FirstPort ||
            LastPort > 65535 ||
            MaximumPortAttempts is < 1 or > 1_000 ||
            HealthPollMilliseconds is < 25 or > 5_000 ||
            ProxyHealthCacheMilliseconds is < 100 or > 60_000 ||
            ConsecutiveHealthFailureThreshold is < 2 or > 20)
        {
            throw new InvalidOperationException(
                "DemoRuntime configuration contains an invalid port range, attempt bound, or health interval.");
        }
    }
}

public sealed record DemoLaunchCommand(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> EnvironmentVariables);

public sealed record DemoOwnedProcess(
    int ProcessId,
    long ProcessStartIdentity,
    string ProcessName);

public enum DemoListenerOwnership
{
    Owned,
    NotOwned,
    Unverifiable
}

public sealed record DemoListenerOwnershipResult(
    DemoListenerOwnership Ownership,
    string Detail);

public interface IDemoProcessLauncher
{
    Task<DemoOwnedProcess> StartAsync(
        DemoLaunchCommand command,
        CancellationToken cancellationToken);

    bool IsOwned(DemoInstanceRecord record);

    Task<DemoListenerOwnershipResult> VerifyLoopbackListenerAsync(
        DemoInstanceRecord record,
        int port,
        CancellationToken cancellationToken);

    Task StopAsync(
        DemoInstanceRecord record,
        CancellationToken cancellationToken);

    string ReadDiagnostics(DemoInstanceRecord record);

    void Cleanup(DemoInstanceRecord record);
}

public interface IDemoHealthProbe
{
    Task<bool> IsHealthyAsync(
        int port,
        string healthPath,
        CancellationToken cancellationToken);
}

public interface IDemoRuntimeRevoker
{
    Task RevokeFlowAsync(
        Guid flowId,
        string reason,
        CancellationToken cancellationToken = default);
}

public sealed class HttpDemoHealthProbe : IDemoHealthProbe, IDisposable
{
    private readonly HttpClient _client = new(
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(2)
        })
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    public async Task<bool> IsHealthyAsync(
        int port,
        string healthPath,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetAsync(
                new Uri($"http://127.0.0.1:{port}{healthPath}"),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            return (int)response.StatusCode is >= 200 and < 400;
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested &&
            (exception is HttpRequestException or TaskCanceledException))
        {
            return false;
        }
    }

    public void Dispose() => _client.Dispose();
}

public sealed class SystemDemoProcessLauncher : IDemoProcessLauncher
{
    private const int MaximumDiagnosticCharacters = 4_000;
    private readonly ConcurrentDictionary<ProcessIdentity, StringBuilder> _diagnostics = new();
    private readonly ConcurrentDictionary<int, Process> _processes = new();

    public Task<DemoOwnedProcess> StartAsync(
        DemoLaunchCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = command.Executable,
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var (name, value) in command.EnvironmentVariables)
        {
            startInfo.Environment[name] = value;
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    $"The approved demo launch profile '{command.Executable}' did not start.");
            }
            var identity = new ProcessIdentity(
                process.Id,
                process.StartTime.ToUniversalTime().Ticks);
            var buffer = _diagnostics.GetOrAdd(identity, _ => new StringBuilder());
            process.OutputDataReceived += (_, eventArgs) =>
                AppendDiagnostic(buffer, eventArgs.Data);
            process.ErrorDataReceived += (_, eventArgs) =>
                AppendDiagnostic(buffer, eventArgs.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var result = new DemoOwnedProcess(
                process.Id,
                identity.StartIdentity,
                process.ProcessName);
            _processes[process.Id] = process;
            process.Exited += (_, _) =>
            {
                if (_processes.TryRemove(process.Id, out var completed))
                {
                    completed.Dispose();
                }
                _diagnostics.TryRemove(identity, out _);
            };
            return Task.FromResult(result);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public bool IsOwned(DemoInstanceRecord record)
    {
        if (record.ProcessId is not { } processId ||
            record.ProcessStartIdentity is not { } startIdentity ||
            string.IsNullOrWhiteSpace(record.ProcessName))
        {
            return false;
        }
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited &&
                   process.StartTime.ToUniversalTime().Ticks == startIdentity &&
                   string.Equals(
                       process.ProcessName,
                       record.ProcessName,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidOperationException or
                System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public Task<DemoListenerOwnershipResult> VerifyLoopbackListenerAsync(
        DemoInstanceRecord record,
        int port,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsOwned(record) || record.ProcessId is not { } processId)
        {
            return Task.FromResult(new DemoListenerOwnershipResult(
                DemoListenerOwnership.NotOwned,
                "The recorded demo process identity is no longer owned."));
        }

        var result = DemoListenerInspector.Verify(
            processId,
            port,
            cancellationToken);
        if (result.Ownership == DemoListenerOwnership.Owned &&
            !IsOwned(record))
        {
            result = new DemoListenerOwnershipResult(
                DemoListenerOwnership.Unverifiable,
                "The recorded demo process changed during listener ownership verification.");
        }
        return Task.FromResult(result);
    }

    public async Task StopAsync(
        DemoInstanceRecord record,
        CancellationToken cancellationToken)
    {
        if (!IsOwned(record) || record.ProcessId is not { } processId)
        {
            return;
        }
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            // It exited between identity verification and the wait.
        }
    }

    public string ReadDiagnostics(DemoInstanceRecord record)
    {
        if (record.ProcessId is not { } processId ||
            record.ProcessStartIdentity is not { } startIdentity ||
            !_diagnostics.TryGetValue(
                new ProcessIdentity(processId, startIdentity),
                out var buffer))
        {
            return string.Empty;
        }
        lock (buffer)
        {
            return Clip(buffer.ToString(), MaximumDiagnosticCharacters);
        }
    }

    public void Cleanup(DemoInstanceRecord record)
    {
        if (record.ProcessId is { } processId &&
            record.ProcessStartIdentity is { } startIdentity)
        {
            _diagnostics.TryRemove(
                new ProcessIdentity(processId, startIdentity),
                out _);
        }
    }

    private static void AppendDiagnostic(StringBuilder buffer, string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }
        lock (buffer)
        {
            if (buffer.Length >= MaximumDiagnosticCharacters)
            {
                return;
            }
            var remaining = MaximumDiagnosticCharacters - buffer.Length;
            buffer.AppendLine(line[..Math.Min(line.Length, remaining)]);
        }
    }

    private static string Clip(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    private readonly record struct ProcessIdentity(
        int ProcessId,
        long StartIdentity);
}

public sealed class DemoRuntimeManager(
    IDbContextFactory<HarnessDbContext> databaseFactory,
    ISealedDemoManifestService manifests,
    IDemoProcessLauncher processLauncher,
    IDemoHealthProbe healthProbe,
    DemoRuntimeOptions options,
    TimeProvider timeProvider,
    ILogger<DemoRuntimeManager>? logger = null) : IDemoRuntimeRevoker
{
    private const int MaximumFailureCharacters = 4_000;
    private readonly object _operationLocksSync = new();
    private readonly Dictionary<string, OperationLock> _operationLocks =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, ProxyLease> _proxyLeases = new();

    internal int OperationLockCount
    {
        get
        {
            lock (_operationLocksSync)
            {
                return _operationLocks.Count;
            }
        }
    }

    internal int ProxyLeaseCount => _proxyLeases.Count;

    public async Task<DemoRuntimeStatus> GetStatusAsync(
        Guid flowId,
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        var flow = await LoadFlowAsync(flowId, cancellationToken);
        RequirePreviewEligible(flow);
        var binding = await manifests.ResolveAsync(flow, artifactId, cancellationToken);
        if (binding is null)
        {
            return new DemoRuntimeStatus(
                DemoCapability.OfflineOnly,
                null,
                DemoInstanceState.Stopped,
                null,
                null,
                null,
                null);
        }

        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var record = await FindBindingAsync(
            database,
            flowId,
            artifactId,
            binding,
            cancellationToken);
        return ToStatus(record, binding);
    }

    public Task<DemoRuntimeStatus> StartAsync(
        Guid flowId,
        string artifactId,
        string candidateFingerprint,
        string manifestHash,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            flowId,
            artifactId,
            candidateFingerprint,
            manifestHash,
            restart: false,
            stop: false,
            cancellationToken);

    public Task<DemoRuntimeStatus> RestartAsync(
        Guid flowId,
        string artifactId,
        string candidateFingerprint,
        string manifestHash,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            flowId,
            artifactId,
            candidateFingerprint,
            manifestHash,
            restart: true,
            stop: false,
            cancellationToken);

    public Task<DemoRuntimeStatus> StopAsync(
        Guid flowId,
        string artifactId,
        string candidateFingerprint,
        string manifestHash,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            flowId,
            artifactId,
            candidateFingerprint,
            manifestHash,
            restart: false,
            stop: true,
            cancellationToken);

    public async Task<(DemoInstanceRecord Record, SealedDemoManifestBinding Binding)>
        GetProxyTargetAsync(
            Guid instanceId,
            CancellationToken cancellationToken = default)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var target = await database.DemoInstances
            .AsNoTracking()
            .Where(item => item.Id == instanceId)
            .Select(item => new
            {
                Record = item,
                FlowStatus = item.FlowRun!.Status
            })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException(
                $"Live demo instance '{instanceId}' was not found.");
        var record = target.Record;
        if (!IsPreviewEligible(target.FlowStatus))
        {
            _proxyLeases.TryRemove(record.Id, out _);
            throw new DemoRuntimeException(
                DemoConflictCodes.InvalidTransition,
                "The live demo URL is unavailable because this flow is no longer review-eligible.");
        }
        if (record.State != DemoInstanceState.Running ||
            record.AssignedPort is not { } port)
        {
            throw new DemoRuntimeException(
                DemoConflictCodes.InvalidTransition,
                "The live demo is not running.");
        }

        if (!_proxyLeases.TryGetValue(record.Id, out var lease) ||
            !BindingMatches(record, lease.Binding) ||
            !processLauncher.IsOwned(record))
        {
            var owned = processLauncher.IsOwned(record);
            if (owned)
            {
                await processLauncher.StopAsync(
                    record,
                    CancellationToken.None);
            }
            await TransitionAsync(
                record.Id,
                DemoInstanceState.Stopped,
                owned
                    ? "Live demo binding changed; the previously owned process was stopped."
                    : "Live demo process ownership could not be verified.",
                clearProcess: true,
                CancellationToken.None);
            CleanupRuntimeState(record);
            throw new DemoRuntimeException(
                DemoConflictCodes.NotOwned,
                "The live demo process is no longer owned by this instance.");
        }
        var listenerOwnership =
            await processLauncher.VerifyLoopbackListenerAsync(
                record,
                port,
                cancellationToken);
        if (listenerOwnership.Ownership != DemoListenerOwnership.Owned)
        {
            await RejectListenerOwnershipAsync(
                record,
                listenerOwnership,
                DemoInstanceState.Stopped);
            throw new DemoRuntimeException(
                DemoConflictCodes.NotOwned,
                listenerOwnership.Detail);
        }
        await lease.HealthGate.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            if (now - lease.LastHealthCheckAt >=
                TimeSpan.FromMilliseconds(options.ProxyHealthCacheMilliseconds))
            {
                var healthy = await healthProbe.IsHealthyAsync(
                    port,
                    lease.Binding.Manifest.HealthPath,
                    cancellationToken);
                lease.LastHealthCheckAt = now;
                lease.ConsecutiveFailures = healthy
                    ? 0
                    : lease.ConsecutiveFailures + 1;
                await PersistHealthCheckAsync(record.Id, now, cancellationToken);
                if (lease.ConsecutiveFailures >=
                    options.ConsecutiveHealthFailureThreshold)
                {
                    await TransitionAsync(
                        record.Id,
                        DemoInstanceState.Unhealthy,
                        "The owned live demo failed consecutive cached health checks.",
                        clearProcess: false,
                        cancellationToken);
                    _proxyLeases.TryRemove(record.Id, out _);
                    throw new DemoRuntimeException(
                        DemoConflictCodes.StartupFailed,
                        "The live demo is unhealthy.",
                        StatusCodes.Status502BadGateway);
                }
            }
        }
        finally
        {
            lease.HealthGate.Release();
        }
        return (record, lease.Binding);
    }

    public async Task RevokeFlowAsync(
        Guid flowId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        Guid[] instanceIds;
        await using (var database =
                     await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            instanceIds = await database.DemoInstances
                .AsNoTracking()
                .Where(item =>
                    item.FlowRunId == flowId &&
                    (item.ProcessId != null ||
                     item.State == DemoInstanceState.Starting ||
                     item.State == DemoInstanceState.Running ||
                     item.State == DemoInstanceState.Unhealthy))
                .Select(item => item.Id)
                .ToArrayAsync(cancellationToken);
        }

        foreach (var instanceId in instanceIds)
        {
            var record = await LoadRecordAsync(instanceId, cancellationToken);
            var key = $"{record.FlowRunId:D}:{record.ArtifactId}";
            var operationLock = RentOperationLock(key);
            await operationLock.Gate.WaitAsync(cancellationToken);
            try
            {
                record = await LoadRecordAsync(instanceId, cancellationToken);
                var owned = processLauncher.IsOwned(record);
                if (owned)
                {
                    await processLauncher.StopAsync(record, cancellationToken);
                }
                await TransitionAsync(
                    record.Id,
                    DemoInstanceState.Stopped,
                    owned
                        ? reason
                        : $"{reason} Process ownership did not match; no process was terminated.",
                    clearProcess: true,
                    cancellationToken);
                CleanupRuntimeState(record);
            }
            finally
            {
                operationLock.Gate.Release();
                ReturnOperationLock(key, operationLock);
            }
        }
    }

    public async Task ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        Guid[] activeIds;
        await using (var database =
                     await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            activeIds = await database.DemoInstances
                .AsNoTracking()
                .Where(item => item.State == DemoInstanceState.Starting ||
                               item.State == DemoInstanceState.Running ||
                               item.State == DemoInstanceState.Unhealthy)
                .Select(item => item.Id)
                .ToArrayAsync(cancellationToken);
        }

        foreach (var instanceId in activeIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ReconcileOneAsync(instanceId, cancellationToken);
            }
            catch (Exception exception)
            {
                logger?.LogWarning(
                    exception,
                    "Live demo {InstanceId} could not be reconciled.",
                    instanceId);
                try
                {
                    var record = await TryLoadRecordAsync(
                        instanceId,
                        cancellationToken);
                    if (record is not null &&
                        processLauncher.IsOwned(record))
                    {
                        await processLauncher.StopAsync(
                            record,
                            cancellationToken);
                    }
                    await TransitionAsync(
                        instanceId,
                        DemoInstanceState.Stopped,
                        "Startup reconciliation could not verify this live demo binding.",
                        clearProcess: true,
                        cancellationToken);
                    if (record is not null)
                    {
                        CleanupRuntimeState(record);
                    }
                }
                catch (Exception persistenceException)
                {
                    logger?.LogError(
                        persistenceException,
                        "Live demo {InstanceId} reconciliation failure could not be persisted.",
                        instanceId);
                }
            }

        }
    }

    public async Task StopOwnedOnShutdownAsync(
        CancellationToken cancellationToken = default)
    {
        Guid[] activeIds;
        await using (var database =
                     await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            activeIds = await database.DemoInstances
                .AsNoTracking()
                .Where(item => item.State == DemoInstanceState.Starting ||
                               item.State == DemoInstanceState.Running ||
                               item.State == DemoInstanceState.Unhealthy)
                .Select(item => item.Id)
                .ToArrayAsync(cancellationToken);
        }

        foreach (var instanceId in activeIds)
        {
            var record = await LoadRecordAsync(instanceId, cancellationToken);
            var owned = processLauncher.IsOwned(record);
            if (owned)
            {
                await processLauncher.StopAsync(record, cancellationToken);
            }
            await TransitionAsync(
                record.Id,
                DemoInstanceState.Stopped,
                owned
                    ? "Live demo stopped during graceful application shutdown."
                    : "Live demo ownership could not be verified during graceful shutdown; no process was terminated.",
                clearProcess: true,
                cancellationToken);
            CleanupRuntimeState(record);
        }
    }

    private async Task<DemoRuntimeStatus> MutateAsync(
        Guid flowId,
        string artifactId,
        string candidateFingerprint,
        string manifestHash,
        bool restart,
        bool stop,
        CancellationToken cancellationToken)
    {
        options.Validate();
        var key = $"{flowId:D}:{artifactId}";
        var operationLock = RentOperationLock(key);
        await operationLock.Gate.WaitAsync(cancellationToken);
        try
        {
            var flow = await LoadFlowAsync(flowId, cancellationToken);
            if (!stop)
            {
                RequirePreviewEligible(flow);
            }
            var binding = await ResolveRequiredBindingAsync(
                flow,
                artifactId,
                cancellationToken)
                ?? throw new DemoRuntimeException(
                    DemoConflictCodes.ManifestStale,
                    "This artifact has no sealed customer-demo-v1 manifest.");
            RequireRequestBinding(
                binding,
                candidateFingerprint,
                manifestHash);
            var record = await GetOrCreateRecordAsync(
                flow,
                artifactId,
                binding,
                cancellationToken);

            if (record.State == DemoInstanceState.Starting)
            {
                throw new DemoRuntimeException(
                    DemoConflictCodes.AlreadyStarting,
                    "A live-demo start operation is already in progress.");
            }
            if (stop)
            {
                return await StopCoreAsync(record, binding, cancellationToken);
            }
            if (!restart &&
                record.State == DemoInstanceState.Running &&
                processLauncher.IsOwned(record) &&
                record.AssignedPort is { } runningPort)
            {
                if (await healthProbe.IsHealthyAsync(
                        runningPort,
                        binding.Manifest.HealthPath,
                        cancellationToken))
                {
                    var listenerOwnership =
                        await processLauncher.VerifyLoopbackListenerAsync(
                            record,
                            runningPort,
                            cancellationToken);
                    if (listenerOwnership.Ownership !=
                        DemoListenerOwnership.Owned)
                    {
                        await RejectListenerOwnershipAsync(
                            record,
                            listenerOwnership,
                            DemoInstanceState.Stopped);
                        throw new DemoRuntimeException(
                            DemoConflictCodes.NotOwned,
                            listenerOwnership.Detail);
                    }
                    _proxyLeases[record.Id] = new ProxyLease(
                        binding,
                        timeProvider.GetUtcNow());
                    return ToStatus(record, binding);
                }
            }

            record = await PrepareForStartAsync(
                record,
                binding,
                cancellationToken);
            return await StartCoreAsync(record, binding, cancellationToken);
        }
        finally
        {
            operationLock.Gate.Release();
            ReturnOperationLock(key, operationLock);
        }
    }

    private async Task<DemoRuntimeStatus> StartCoreAsync(
        DemoInstanceRecord record,
        SealedDemoManifestBinding binding,
        CancellationToken cancellationToken)
    {
        if (record.ProcessId is not null ||
            record.State is DemoInstanceState.Starting or
                DemoInstanceState.Running or
                DemoInstanceState.Unhealthy)
        {
            throw new DemoRuntimeException(
                DemoConflictCodes.NotOwned,
                "A previous live-demo process identity must be safely stopped before a new start.");
        }

        var attempted = 0;
        for (var port = options.FirstPort;
             port <= options.LastPort && attempted < options.MaximumPortAttempts;
             port++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await IsPortAvailableAsync(port, cancellationToken) ||
                await IsClaimedAsync(port, record.Id, cancellationToken))
            {
                continue;
            }
            attempted++;
            if (!await TryClaimStartingAsync(
                    record.Id,
                    port,
                    cancellationToken))
            {
                continue;
            }
            record = await LoadRecordAsync(record.Id, cancellationToken);

            try
            {
                var arguments = binding.LaunchProfile.PrefixArguments
                    .Concat(binding.Manifest.Arguments)
                    .Select(argument => argument.Replace(
                        "{port}",
                        port.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        StringComparison.Ordinal))
                    .ToArray();
                var process = await processLauncher.StartAsync(
                    new DemoLaunchCommand(
                        binding.LaunchProfile.Executable,
                        arguments,
                        binding.WorkingDirectory,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["PORT"] = port.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                            ["HOST"] = "127.0.0.1",
                            ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
                            ["DOTNET_URLS"] = $"http://127.0.0.1:{port}",
                            ["AI_HARNESS_DEMO_BASE_PATH"] =
                                $"/api/demos/{record.Id:D}",
                            ["BROWSER"] = "none",
                            ["npm_config_offline"] = "true",
                            ["npm_config_prefix"] = string.Empty,
                            ["npm_config_script_shell"] = string.Empty,
                            ["NODE_OPTIONS"] = string.Empty
                        }),
                    cancellationToken);
                record.ProcessId = process.ProcessId;
                record.ProcessStartIdentity = process.ProcessStartIdentity;
                record.ProcessName = process.ProcessName;
                await PersistProcessIdentityAsync(
                    record.Id,
                    process,
                    cancellationToken);
                record = await LoadRecordAsync(record.Id, cancellationToken);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                timeout.CancelAfter(
                    TimeSpan.FromSeconds(
                        binding.Manifest.StartupTimeoutSeconds));
                while (!timeout.IsCancellationRequested)
                {
                    if (!processLauncher.IsOwned(record))
                    {
                        break;
                    }
                    if (await healthProbe.IsHealthyAsync(
                            port,
                            binding.Manifest.HealthPath,
                            timeout.Token))
                    {
                        var listenerOwnership =
                            await processLauncher.VerifyLoopbackListenerAsync(
                                record,
                                port,
                                timeout.Token);
                        if (listenerOwnership.Ownership !=
                            DemoListenerOwnership.Owned)
                        {
                            await RejectListenerOwnershipAsync(
                                record,
                                listenerOwnership,
                                DemoInstanceState.Failed);
                            throw new DemoRuntimeException(
                                DemoConflictCodes.NotOwned,
                                listenerOwnership.Detail);
                        }
                        await TransitionAsync(
                            record.Id,
                            DemoInstanceState.Running,
                            "Live demo passed its loopback health check.",
                            clearProcess: false,
                            cancellationToken);
                        var running = await LoadRecordAsync(
                            record.Id,
                            cancellationToken);
                        _proxyLeases[running.Id] = new ProxyLease(
                            binding,
                            timeProvider.GetUtcNow());
                        return ToStatus(running, binding);
                    }
                    await Task.Delay(
                        options.HealthPollMilliseconds,
                        timeout.Token);
                }
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                // The bounded startup timeout is reported below.
            }
            catch (DemoRuntimeException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var detail = Clip(
                    $"Launch failed: {exception.Message}",
                    MaximumFailureCharacters);
                await CleanupFailedAttemptAsync(
                    record,
                    detail,
                    cancellationToken);
                throw new DemoRuntimeException(
                    DemoConflictCodes.StartupFailed,
                    detail,
                    StatusCodes.Status502BadGateway);
            }

            record = await LoadRecordAsync(record.Id, cancellationToken);
            var diagnostics = processLauncher.ReadDiagnostics(record);
            var processExited = !processLauncher.IsOwned(record);
            var failure = Clip(
                "The live demo did not become healthy within " +
                $"{binding.Manifest.StartupTimeoutSeconds} seconds." +
                (string.IsNullOrWhiteSpace(diagnostics)
                    ? string.Empty
                    : $" Diagnostics: {diagnostics}"),
                MaximumFailureCharacters);
            await CleanupFailedAttemptAsync(record, failure, cancellationToken);
            if (processExited &&
                port < options.LastPort &&
                attempted < options.MaximumPortAttempts)
            {
                continue;
            }
            throw new DemoRuntimeException(
                DemoConflictCodes.StartupFailed,
                failure,
                StatusCodes.Status502BadGateway);
        }

        await TransitionAsync(
            record.Id,
            DemoInstanceState.Failed,
            "No free loopback port was available in the configured bounded range.",
            clearProcess: true,
            cancellationToken);
        throw new DemoRuntimeException(
            DemoConflictCodes.PortExhausted,
            "No free live-demo port is available in the configured bounded range.");
    }

    private async Task<DemoRuntimeStatus> StopCoreAsync(
        DemoInstanceRecord record,
        SealedDemoManifestBinding binding,
        CancellationToken cancellationToken)
    {
        if (record.State == DemoInstanceState.Stopped)
        {
            CleanupRuntimeState(record);
            return ToStatus(record, binding);
        }
        if (record.State == DemoInstanceState.Starting)
        {
            throw new DemoRuntimeException(
                DemoConflictCodes.AlreadyStarting,
                "The live demo cannot be stopped while its start operation is in progress.");
        }

        var ownershipMismatch = false;
        if (record.ProcessId is not null)
        {
            if (IsControllable(record, binding) &&
                processLauncher.IsOwned(record))
            {
                await processLauncher.StopAsync(record, cancellationToken);
            }
            else
            {
                ownershipMismatch = true;
            }
        }
        await TransitionAsync(
            record.Id,
            DemoInstanceState.Stopped,
            ownershipMismatch
                ? "The recorded process identity did not match; no process was terminated."
                : "Live demo stopped.",
            clearProcess: true,
            cancellationToken);
        CleanupRuntimeState(record);
        return ToStatus(
            await LoadRecordAsync(record.Id, cancellationToken),
            binding);
    }

    private async Task<DemoInstanceRecord> PrepareForStartAsync(
        DemoInstanceRecord record,
        SealedDemoManifestBinding binding,
        CancellationToken cancellationToken)
    {
        var mayRetainProcess =
            record.ProcessId is not null ||
            record.State is DemoInstanceState.Running or
                DemoInstanceState.Unhealthy;
        if (!mayRetainProcess)
        {
            return record;
        }
        if (!IsControllable(record, binding) ||
            !processLauncher.IsOwned(record))
        {
            throw new DemoRuntimeException(
                DemoConflictCodes.NotOwned,
                "The previous live-demo process identity could not be verified, so a replacement was not started.");
        }

        await processLauncher.StopAsync(record, cancellationToken);
        await TransitionAsync(
            record.Id,
            DemoInstanceState.Stopped,
            "The previous owned live-demo process was stopped before a new start attempt.",
            clearProcess: true,
            cancellationToken);
        CleanupRuntimeState(record);
        return await LoadRecordAsync(record.Id, cancellationToken);
    }

    private async Task ReconcileOneAsync(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var record = await LoadRecordAsync(instanceId, cancellationToken);
        var flow = await LoadFlowAsync(record.FlowRunId, cancellationToken);
        if (!IsPreviewEligible(flow.Status))
        {
            var owned = processLauncher.IsOwned(record);
            if (owned)
            {
                await processLauncher.StopAsync(record, cancellationToken);
            }
            await TransitionAsync(
                record.Id,
                DemoInstanceState.Stopped,
                owned
                    ? "Owned live demo was stopped because its flow is no longer review-eligible."
                    : "The flow is no longer review-eligible and process ownership did not match; no process was terminated.",
                clearProcess: true,
                cancellationToken);
            CleanupRuntimeState(record);
            return;
        }
        var binding = await manifests.ResolveAsync(
            flow,
            record.ArtifactId,
            cancellationToken);
        if (binding is null ||
            !BindingMatches(record, binding) ||
            !IsControllable(record, binding))
        {
            if (processLauncher.IsOwned(record))
            {
                await processLauncher.StopAsync(record, cancellationToken);
            }
            await TransitionAsync(
                record.Id,
                DemoInstanceState.Stopped,
                "Live demo process or sealed binding was stale during startup reconciliation.",
                clearProcess: true,
                cancellationToken);
            CleanupRuntimeState(record);
            return;
        }
        if (!processLauncher.IsOwned(record))
        {
            await TransitionAsync(
                record.Id,
                DemoInstanceState.Stopped,
                "Live demo process ownership was stale during startup reconciliation.",
                clearProcess: true,
                cancellationToken);
            CleanupRuntimeState(record);
            return;
        }
        if (record.AssignedPort is not { } port)
        {
            await processLauncher.StopAsync(record, cancellationToken);
            await TransitionAsync(
                record.Id,
                DemoInstanceState.Stopped,
                "Owned live demo had no persisted loopback port during startup reconciliation.",
                clearProcess: true,
                cancellationToken);
            CleanupRuntimeState(record);
            return;
        }

        var healthy = await healthProbe.IsHealthyAsync(
                port,
                binding.Manifest.HealthPath,
                cancellationToken);
        if (!healthy)
        {
            if (record.State == DemoInstanceState.Starting)
            {
                await processLauncher.StopAsync(record, cancellationToken);
            }
            await TransitionAsync(
                record.Id,
                record.State == DemoInstanceState.Starting
                    ? DemoInstanceState.Stopped
                    : DemoInstanceState.Unhealthy,
                record.State == DemoInstanceState.Starting
                    ? "Interrupted live-demo startup did not pass reconciliation health validation."
                    : "Live demo did not pass restart reconciliation health validation.",
                clearProcess: record.State == DemoInstanceState.Starting,
                cancellationToken);
            _proxyLeases.TryRemove(record.Id, out _);
            if (record.State == DemoInstanceState.Starting)
            {
                CleanupRuntimeState(record);
            }
            return;
        }
        var listenerOwnership =
            await processLauncher.VerifyLoopbackListenerAsync(
                record,
                port,
                cancellationToken);
        if (listenerOwnership.Ownership != DemoListenerOwnership.Owned)
        {
            await RejectListenerOwnershipAsync(
                record,
                listenerOwnership,
                DemoInstanceState.Stopped);
            return;
        }
        _proxyLeases[record.Id] = new ProxyLease(
            binding,
            timeProvider.GetUtcNow(),
            0);
        await TransitionAsync(
            record.Id,
            DemoInstanceState.Running,
            "Owned live demo retained after startup identity, listener, and health reconciliation.",
            clearProcess: false,
            cancellationToken);
    }

    private async Task<FlowRun> LoadFlowAsync(
        Guid flowId,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        return await database.Flows
                   .AsNoTracking()
                   .AsSplitQuery()
                   .Include(item => item.Events)
                   .Include(item => item.Steps)
                   .Include(item => item.GateRecords)
                   .SingleOrDefaultAsync(item => item.Id == flowId, cancellationToken)
               ?? throw new KeyNotFoundException(
                   $"Factory flow '{flowId}' was not found.");
    }

    private async Task<SealedDemoManifestBinding?> ResolveRequiredBindingAsync(
        FlowRun flow,
        string artifactId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await manifests.ResolveAsync(
                flow,
                artifactId,
                cancellationToken);
        }
        catch (CandidateValidationException exception)
        {
            throw new DemoRuntimeException(
                DemoConflictCodes.CandidateStale,
                $"The reviewed candidate binding is stale: {exception.Message}");
        }
        catch (CustomerDemoContractException exception)
        {
            throw new DemoRuntimeException(
                DemoConflictCodes.ManifestStale,
                $"The sealed demo manifest is invalid: {exception.Message}");
        }
    }

    private async Task<DemoInstanceRecord> GetOrCreateRecordAsync(
        FlowRun flow,
        string artifactId,
        SealedDemoManifestBinding binding,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var records = await database.DemoInstances
            .AsNoTracking()
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.ArtifactId == artifactId)
            .ToListAsync(cancellationToken);
        var existing = records.SingleOrDefault(item =>
            BindingMatches(item, binding));
        var reusable = records.SingleOrDefault(item =>
            ContractMatches(item, binding));
        foreach (var superseded in records.Where(item =>
                     item.Id != existing?.Id &&
                     (item.State is DemoInstanceState.Starting or
                         DemoInstanceState.Running or
                         DemoInstanceState.Unhealthy ||
                      item.ProcessId is not null)))
        {
            var owned = processLauncher.IsOwned(superseded);
            if (owned)
            {
                await processLauncher.StopAsync(superseded, cancellationToken);
            }
            await TransitionAsync(
                superseded.Id,
                DemoInstanceState.Stopped,
                owned
                    ? "Superseded live-demo binding was stopped before replacement."
                    : "Superseded live-demo ownership did not match; no process was terminated.",
                clearProcess: true,
                cancellationToken);
            CleanupRuntimeState(superseded);
        }
        if (existing is not null)
        {
            RequireBinding(existing, binding);
            return existing;
        }
        if (reusable is not null)
        {
            var tracked = await database.DemoInstances.SingleAsync(
                item => item.Id == reusable.Id,
                cancellationToken);
            tracked.ManifestRelativePath = binding.ManifestRelativePath;
            tracked.WorkspacePath = binding.WorkspacePath;
            tracked.WorkingDirectory = binding.WorkingDirectory;
            tracked.LaunchProfile = binding.LaunchProfile.Name;
            tracked.LaunchIdentity = binding.LaunchIdentity;
            tracked.State = DemoInstanceState.Stopped;
            tracked.AssignedPort = null;
            tracked.ProcessId = null;
            tracked.ProcessStartIdentity = null;
            tracked.ProcessName = string.Empty;
            tracked.FailureDetail = string.Empty;
            tracked.StoppedAt = timeProvider.GetUtcNow();
            tracked.UpdatedAt = tracked.StoppedAt.Value;
            await database.SaveChangesAsync(cancellationToken);
            return tracked;
        }

        var now = timeProvider.GetUtcNow();
        var record = new DemoInstanceRecord
        {
            FlowRunId = flow.Id,
            ArtifactId = artifactId,
            CandidateFingerprint = binding.CandidateFingerprint,
            ManifestHash = binding.ManifestHash,
            ManifestRelativePath = binding.ManifestRelativePath,
            State = DemoInstanceState.Stopped,
            WorkspacePath = binding.WorkspacePath,
            WorkingDirectory = binding.WorkingDirectory,
            LaunchProfile = binding.LaunchProfile.Name,
            LaunchIdentity = binding.LaunchIdentity,
            CreatedAt = now,
            UpdatedAt = now,
            StoppedAt = now
        };
        database.DemoInstances.Add(record);
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            Type = "demo.instance-created",
            Message =
                $"Live demo instance for artifact '{artifactId}' was recorded in Stopped state."
        });
        await database.SaveChangesAsync(cancellationToken);
        return record;
    }

    private static Task<DemoInstanceRecord?> FindBindingAsync(
        HarnessDbContext database,
        Guid flowId,
        string artifactId,
        SealedDemoManifestBinding binding,
        CancellationToken cancellationToken) =>
        database.DemoInstances
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item =>
                    item.FlowRunId == flowId &&
                    item.ArtifactId == artifactId &&
                    item.CandidateFingerprint == binding.CandidateFingerprint &&
                    item.ManifestHash == binding.ManifestHash,
                cancellationToken);

    private async Task<DemoInstanceRecord> LoadRecordAsync(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        return await database.DemoInstances
                   .AsNoTracking()
                   .SingleOrDefaultAsync(item => item.Id == instanceId, cancellationToken)
               ?? throw new KeyNotFoundException(
                   $"Live demo instance '{instanceId}' was not found.");
    }

    private async Task<DemoInstanceRecord?> TryLoadRecordAsync(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        return await database.DemoInstances
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == instanceId, cancellationToken);
    }

    private async Task PersistHealthCheckAsync(
        Guid instanceId,
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var record = await database.DemoInstances.SingleAsync(
            item => item.Id == instanceId,
            cancellationToken);
        record.LastHealthCheckAt = checkedAt;
        record.UpdatedAt = checkedAt;
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> TryClaimStartingAsync(
        Guid instanceId,
        int port,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var record = await database.DemoInstances.SingleAsync(
            item => item.Id == instanceId,
            cancellationToken);
        var previous = record.State;
        var now = timeProvider.GetUtcNow();
        record.State = DemoInstanceState.Starting;
        record.AssignedPort = port;
        record.FailureDetail = string.Empty;
        record.ProcessId = null;
        record.ProcessStartIdentity = null;
        record.ProcessName = string.Empty;
        record.StartedAt = now;
        record.StoppedAt = null;
        record.UpdatedAt = now;
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = record.FlowRunId,
            Type = "demo.state-changed",
            Message =
                $"Live demo '{record.Id:D}' changed from {previous} to Starting on loopback port {port}."
        });
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    private async Task PersistProcessIdentityAsync(
        Guid instanceId,
        DemoOwnedProcess process,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var record = await database.DemoInstances.SingleAsync(
            item => item.Id == instanceId,
            cancellationToken);
        record.ProcessId = process.ProcessId;
        record.ProcessStartIdentity = process.ProcessStartIdentity;
        record.ProcessName = process.ProcessName;
        record.UpdatedAt = timeProvider.GetUtcNow();
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task TransitionAsync(
        Guid instanceId,
        DemoInstanceState next,
        string detail,
        bool clearProcess,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var record = await database.DemoInstances.SingleAsync(
            item => item.Id == instanceId,
            cancellationToken);
        var previous = record.State;
        var now = timeProvider.GetUtcNow();
        if (previous == next)
        {
            record.UpdatedAt = now;
            if (next is DemoInstanceState.Running or DemoInstanceState.Unhealthy)
            {
                record.LastHealthCheckAt = now;
            }
            if (next == DemoInstanceState.Stopped)
            {
                record.StoppedAt = now;
            }
            if (clearProcess)
            {
                record.ProcessId = null;
                record.ProcessStartIdentity = null;
                record.ProcessName = string.Empty;
            }
            await database.SaveChangesAsync(cancellationToken);
            return;
        }
        record.State = next;
        record.UpdatedAt = now;
        record.LastHealthCheckAt =
            next is DemoInstanceState.Running or DemoInstanceState.Unhealthy
                ? now
                : record.LastHealthCheckAt;
        record.FailureDetail =
            next is DemoInstanceState.Failed or DemoInstanceState.Unhealthy
                ? Clip(detail, MaximumFailureCharacters)
                : string.Empty;
        if (next == DemoInstanceState.Stopped)
        {
            record.StoppedAt = now;
        }
        if (clearProcess)
        {
            record.ProcessId = null;
            record.ProcessStartIdentity = null;
            record.ProcessName = string.Empty;
        }
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = record.FlowRunId,
            Type = "demo.state-changed",
            Message =
                $"Live demo '{record.Id:D}' changed from {previous} to {next}. " +
                Clip(detail, 1_000)
        });
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task CleanupFailedAttemptAsync(
        DemoInstanceRecord record,
        string failure,
        CancellationToken cancellationToken)
    {
        if (processLauncher.IsOwned(record))
        {
            await processLauncher.StopAsync(record, cancellationToken);
        }
        processLauncher.Cleanup(record);
        await TransitionAsync(
            record.Id,
            DemoInstanceState.Failed,
            failure,
            clearProcess: true,
            cancellationToken);
        _proxyLeases.TryRemove(record.Id, out _);
    }

    private async Task RejectListenerOwnershipAsync(
        DemoInstanceRecord record,
        DemoListenerOwnershipResult ownership,
        DemoInstanceState state)
    {
        if (processLauncher.IsOwned(record))
        {
            await processLauncher.StopAsync(
                record,
                CancellationToken.None);
        }
        processLauncher.Cleanup(record);
        await TransitionAsync(
            record.Id,
            state,
            Clip(
                $"Loopback listener ownership was rejected: {ownership.Detail}",
                MaximumFailureCharacters),
            clearProcess: true,
            CancellationToken.None);
        _proxyLeases.TryRemove(record.Id, out _);
    }

    private async Task<bool> IsClaimedAsync(
        int port,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        return await database.DemoInstances.AsNoTracking().AnyAsync(
            item =>
                item.Id != instanceId &&
                item.AssignedPort == port &&
                (item.State == DemoInstanceState.Starting ||
                 item.State == DemoInstanceState.Running ||
                 item.State == DemoInstanceState.Unhealthy),
            cancellationToken);
    }

    internal static async Task<bool> IsPortAvailableAsync(
        int port,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }

    private static bool BindingMatches(
        DemoInstanceRecord record,
        SealedDemoManifestBinding binding) =>
        ContractMatches(record, binding) &&
        string.Equals(
            Path.GetFullPath(record.WorkspacePath),
            Path.GetFullPath(binding.WorkspacePath),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal) &&
        string.Equals(
            Path.GetFullPath(record.WorkingDirectory),
            Path.GetFullPath(binding.WorkingDirectory),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal) &&
        string.Equals(
            record.LaunchProfile,
            binding.LaunchProfile.Name,
            StringComparison.Ordinal) &&
        string.Equals(
            record.LaunchIdentity,
            binding.LaunchIdentity,
            StringComparison.Ordinal);

    private static bool ContractMatches(
        DemoInstanceRecord record,
        SealedDemoManifestBinding binding) =>
        string.Equals(
            record.CandidateFingerprint,
            binding.CandidateFingerprint,
            StringComparison.Ordinal) &&
        string.Equals(
            record.ManifestHash,
            binding.ManifestHash,
            StringComparison.Ordinal);

    private static bool IsControllable(
        DemoInstanceRecord record,
        SealedDemoManifestBinding binding) =>
        BindingMatches(record, binding);

    private static void RequirePreviewEligible(FlowRun flow)
    {
        if (!IsPreviewEligible(flow.Status))
        {
            throw new DemoRuntimeException(
                DemoConflictCodes.InvalidTransition,
                "Live demos are available only for a reviewed preview awaiting feedback or an approved delivery.");
        }
    }

    private static bool IsPreviewEligible(FlowStatus status) =>
        status is FlowStatus.WaitingForFeedback or FlowStatus.Approved;

    private OperationLock RentOperationLock(string key)
    {
        lock (_operationLocksSync)
        {
            if (!_operationLocks.TryGetValue(key, out var operationLock))
            {
                operationLock = new OperationLock();
                _operationLocks.Add(key, operationLock);
            }
            operationLock.ReferenceCount++;
            return operationLock;
        }
    }

    private void ReturnOperationLock(
        string key,
        OperationLock operationLock)
    {
        lock (_operationLocksSync)
        {
            operationLock.ReferenceCount--;
            if (operationLock.ReferenceCount == 0 &&
                _operationLocks.TryGetValue(key, out var current) &&
                ReferenceEquals(current, operationLock))
            {
                _operationLocks.Remove(key);
                operationLock.Gate.Dispose();
            }
        }
    }

    private void CleanupRuntimeState(DemoInstanceRecord record)
    {
        _proxyLeases.TryRemove(record.Id, out _);
        processLauncher.Cleanup(record);
    }

    private static void RequireRequestBinding(
        SealedDemoManifestBinding binding,
        string candidateFingerprint,
        string manifestHash)
    {
        if (!string.Equals(
                candidateFingerprint,
                binding.CandidateFingerprint,
                StringComparison.Ordinal))
        {
            throw new DemoRuntimeException(
                DemoConflictCodes.CandidateStale,
                "The reviewed candidate binding is stale. Refresh the preview.");
        }
        if (!string.Equals(
                manifestHash,
                binding.ManifestHash,
                StringComparison.Ordinal))
        {
            throw new DemoRuntimeException(
                DemoConflictCodes.ManifestStale,
                "The sealed demo manifest binding is stale. Refresh the preview.");
        }
    }

    private static void RequireBinding(
        DemoInstanceRecord record,
        SealedDemoManifestBinding binding)
    {
        if (!string.Equals(
                record.CandidateFingerprint,
                binding.CandidateFingerprint,
                StringComparison.Ordinal))
        {
            throw new DemoRuntimeException(
                DemoConflictCodes.CandidateStale,
                "The live demo belongs to a stale reviewed candidate.");
        }
        if (!string.Equals(
                record.ManifestHash,
                binding.ManifestHash,
                StringComparison.Ordinal))
        {
            throw new DemoRuntimeException(
                DemoConflictCodes.ManifestStale,
                "The live demo belongs to a stale sealed manifest.");
        }
    }

    private static DemoRuntimeStatus ToStatus(
        DemoInstanceRecord? record,
        SealedDemoManifestBinding binding) =>
        new(
            DemoCapability.Available,
            record?.Id,
            record?.State ?? DemoInstanceState.Stopped,
            record is null ? null : $"/api/demos/{record.Id:D}/",
            string.IsNullOrWhiteSpace(record?.FailureDetail)
                ? null
                : record.FailureDetail,
            binding.CandidateFingerprint,
            binding.ManifestHash);

    private static string Clip(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    private sealed class OperationLock
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public int ReferenceCount { get; set; }
    }

    private sealed class ProxyLease(
        SealedDemoManifestBinding binding,
        DateTimeOffset lastHealthCheckAt,
        int consecutiveFailures = 0)
    {
        public SealedDemoManifestBinding Binding { get; } = binding;

        public SemaphoreSlim HealthGate { get; } = new(1, 1);

        public DateTimeOffset LastHealthCheckAt { get; set; } =
            lastHealthCheckAt;

        public int ConsecutiveFailures { get; set; } = consecutiveFailures;
    }
}

public sealed class DemoRuntimeShutdownService(DemoRuntimeManager runtime)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) =>
        runtime.StopOwnedOnShutdownAsync(cancellationToken);
}

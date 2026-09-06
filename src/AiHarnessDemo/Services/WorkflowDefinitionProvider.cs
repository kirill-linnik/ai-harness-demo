using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Infrastructure;

namespace AiHarnessDemo.Services;

public sealed record WorkflowRuntimeStatus(
    bool Ready,
    string SourcePath,
    DateTimeOffset? LoadedAt,
    string? LastError,
    int? MaxConcurrentAgents,
    int? MaxAttempts,
    string? WorkspaceRoot,
    bool? OutcomeVerificationEnabled,
    int? OutcomeVerificationMaxRounds);

/// <summary>
/// Keeps the last-known-good Symphony WORKFLOW.md in memory and hot-reloads changes. Invalid reloads
/// are operator-visible but do not crash active work; dispatch revalidates defensively.
/// </summary>
public sealed class WorkflowDefinitionProvider(
    HarnessPaths paths,
    WorkflowLoader loader,
    ILogger<WorkflowDefinitionProvider> logger)
    : IHostedService, IDisposable
{
    private readonly Lock _lock = new();
    private readonly string _workflowPath = Path.Combine(paths.Root, "WORKFLOW.md");
    private FileSystemWatcher? _watcher;
    private Timer? _reloadTimer;
    private WorkflowDefinition? _current;
    private string? _lastError;
    private DateTime _lastWriteUtc;

    public WorkflowDefinition GetValidated()
    {
        var writeTime = File.Exists(_workflowPath)
            ? File.GetLastWriteTimeUtc(_workflowPath)
            : DateTime.MinValue;
        lock (_lock)
        {
            if (_current is null || writeTime != _lastWriteUtc)
            {
                ReloadLocked(throwOnFailure: _current is null);
            }

            return _current
                ?? throw new WorkflowConfigurationException(
                    _lastError ?? "No valid Symphony workflow is loaded.");
        }
    }

    public WorkflowRuntimeStatus Status()
    {
        lock (_lock)
        {
            return new WorkflowRuntimeStatus(
                _current is not null && _lastError is null,
                _workflowPath,
                _current?.LoadedAt,
                _lastError,
                _current?.Config.Agent.MaxConcurrentAgents,
                _current?.Config.Agent.MaxAttempts,
                _current?.Config.Workspace.ResolvedRoot,
                _current?.Config.OutcomeVerification.Enabled,
                _current?.Config.OutcomeVerification.MaxRounds);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            ReloadLocked(throwOnFailure: true);
        }

        _watcher = new FileSystemWatcher(paths.Root, "WORKFLOW.md")
        {
            NotifyFilter =
                NotifyFilters.LastWrite |
                NotifyFilters.Size |
                NotifyFilters.FileName |
                NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnWorkflowChanged;
        _watcher.Created += OnWorkflowChanged;
        _watcher.Renamed += OnWorkflowChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
        }
        return Task.CompletedTask;
    }

    private void OnWorkflowChanged(object sender, FileSystemEventArgs eventArgs)
    {
        lock (_lock)
        {
            _reloadTimer?.Dispose();
            _reloadTimer = new Timer(
                _ =>
                {
                    lock (_lock)
                    {
                        ReloadLocked(throwOnFailure: false);
                    }
                },
                null,
                dueTime: 180,
                period: Timeout.Infinite);
        }
    }

    private void ReloadLocked(bool throwOnFailure)
    {
        try
        {
            var workflow = loader.Load(_workflowPath);
            _current = workflow;
            _lastError = null;
            _lastWriteUtc = File.GetLastWriteTimeUtc(_workflowPath);
            logger.LogInformation(
                "Loaded Symphony workflow from {WorkflowPath} at {LoadedAt}",
                workflow.SourcePath,
                workflow.LoadedAt);
        }
        catch (WorkflowConfigurationException exception)
        {
            _lastError = exception.Message;
            logger.LogError(
                exception,
                "Rejected invalid Symphony workflow reload; continuing with the last known good definition.");
            if (throwOnFailure)
            {
                throw;
            }
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _reloadTimer?.Dispose();
    }
}

using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Infrastructure;

namespace AiHarnessDemo.Services;

public sealed record WorkflowRuntimeStatus(
    bool CurrentFileValid,
    bool HasEffectiveDefinition,
    string SourcePath,
    DateTimeOffset? EffectiveLoadedAt,
    string? EffectiveRevision,
    string? CurrentFileError,
    int? MaxConcurrentAgents,
    int? MaxAttempts,
    string? WorkspaceRoot)
{
    public bool Ready => CurrentFileValid;

    public DateTimeOffset? LoadedAt => EffectiveLoadedAt;

    public string? LastError => CurrentFileError;
}

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
    private FileSystemWatcher? _mcpWatcher;
    private Timer? _reloadTimer;
    private WorkflowDefinition? _effective;
    private bool _currentFileValid;
    private string? _currentFileError;
    private DateTime _lastWriteUtc;
    private DateTime _lastMcpWriteUtc;

    private bool FilesChanged() =>
        !_currentFileValid ||
        (File.Exists(_workflowPath) ? File.GetLastWriteTimeUtc(_workflowPath) : DateTime.MinValue) != _lastWriteUtc ||
        McpWriteTime() != _lastMcpWriteUtc;

    private DateTime McpWriteTime()
    {
        var path = _effective?.Config.Copilot.Mcp.SourcePath;
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? File.GetLastWriteTimeUtc(path)
            : DateTime.MinValue;
    }

    public WorkflowDefinition GetEffective()
    {
        lock (_lock)
        {
            if (_effective is null || FilesChanged())
            {
                ReloadLocked(throwOnFailure: _effective is null);
            }

            return _effective
                ?? throw new WorkflowConfigurationException(
                    _currentFileError ?? "No valid Symphony workflow is loaded.");
        }
    }

    // Existing attempt code intentionally resolves the effective last-known-good definition.
    public WorkflowDefinition GetValidated() => GetEffective();

    public Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            ReloadLocked(throwOnFailure: false);
        }
        return Task.CompletedTask;
    }

    public WorkflowRuntimeStatus Status()
    {
        lock (_lock)
        {
            if (_effective is null || FilesChanged())
            {
                ReloadLocked(throwOnFailure: false);
            }
            return new WorkflowRuntimeStatus(
                _currentFileValid,
                _effective is not null,
                _workflowPath,
                _effective?.LoadedAt,
                _effective?.Revision,
                _currentFileError,
                _effective?.Config.Agent.MaxConcurrentAgents,
                _effective?.Config.Agent.MaxAttempts,
                _effective?.Config.Workspace.ResolvedRoot);
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
        _watcher.Deleted += OnWorkflowChanged;
        lock (_lock)
        {
            ConfigureMcpWatcher();
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
        }
        if (_mcpWatcher is not null)
        {
            _mcpWatcher.EnableRaisingEvents = false;
        }
        return Task.CompletedTask;
    }

    private void ConfigureMcpWatcher()
    {
        if (_watcher is null)
        {
            return;
        }
        var path = _effective?.Config.Copilot.Mcp.SourcePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            _mcpWatcher?.Dispose();
            _mcpWatcher = null;
            return;
        }
        var directory = Path.GetDirectoryName(path)!;
        var fileName = Path.GetFileName(path);
        if (_mcpWatcher?.Path == directory && _mcpWatcher.Filter == fileName)
        {
            return;
        }
        _mcpWatcher?.Dispose();
        _mcpWatcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _mcpWatcher.Changed += OnWorkflowChanged;
        _mcpWatcher.Created += OnWorkflowChanged;
        _mcpWatcher.Deleted += OnWorkflowChanged;
        _mcpWatcher.Renamed += OnWorkflowChanged;
    }

    private void OnWorkflowChanged(object sender, FileSystemEventArgs eventArgs)
    {
        lock (_lock)
        {
            var mcpPath = _effective?.Config.Copilot.Mcp.SourcePath;
            var comparer = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(eventArgs.FullPath, _workflowPath, comparer) &&
                !string.Equals(eventArgs.FullPath, mcpPath, comparer))
            {
                return;
            }
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
            _effective = workflow;
            _currentFileValid = true;
            _currentFileError = null;
            _lastWriteUtc = File.GetLastWriteTimeUtc(_workflowPath);
            _lastMcpWriteUtc = McpWriteTime();
            ConfigureMcpWatcher();
            logger.LogInformation(
                "Loaded Symphony workflow {Revision} from {WorkflowPath} at {LoadedAt}",
                workflow.Revision,
                workflow.SourcePath,
                workflow.LoadedAt);
        }
        catch (WorkflowConfigurationException exception)
        {
            var changedFailure = _currentFileValid || _currentFileError != exception.Message;
            _currentFileValid = false;
            _currentFileError = exception.Message;
            _lastWriteUtc = File.Exists(_workflowPath)
                ? File.GetLastWriteTimeUtc(_workflowPath)
                : DateTime.MinValue;
            _lastMcpWriteUtc = McpWriteTime();
            if (changedFailure)
            {
                logger.LogError(
                    exception,
                    "Rejected invalid Symphony workflow reload; continuing with the last known good definition.");
            }
            if (throwOnFailure)
            {
                throw;
            }
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _mcpWatcher?.Dispose();
        _reloadTimer?.Dispose();
    }
}

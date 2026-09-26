using AiHarnessDemo.Core.Domain;

namespace AiHarnessDemo.Core.Orchestration;

public sealed record WorkspaceRepositoryIdentity(
    string RelativePath,
    string RemoteRepository);

public enum WorkspaceMode
{
    ProvisionalReadOnly,
    AdvisoryReadOnly,
    Delivery
}

public sealed record WorkspaceInfo(
    string Path,
    string BranchName,
    bool CreatedNow,
    IReadOnlyList<WorkspaceRepositoryIdentity>? TrustedRepositories = null,
    WorkspaceMode Mode = WorkspaceMode.Delivery,
    string BaselineDigest = "",
    int BaselineFileCount = 0,
    long BaselineTotalBytes = 0,
    string SourceScopeRelativePath = "",
    string SourceBaselineCommit = "");

public sealed record WorkspaceCleanupResult(
    int WorktreesRemoved,
    int LocalBranchesDeleted,
    int RemoteBranchesDeleted,
    IReadOnlyList<string>? Diagnostics = null)
{
    public static WorkspaceCleanupResult Empty { get; } =
        new(0, 0, 0, []);

    public IReadOnlyList<string> CleanupDiagnostics => Diagnostics ?? [];
}

/// <summary>
/// Execution-layer seam from Symphony: the orchestrator receives an isolated workspace without
/// depending on Git or any specific repository population strategy.
/// </summary>
public interface IWorkspaceManager
{
    Task<WorkspaceInfo> PrepareAsync(
        FlowRun flow,
        CancellationToken cancellationToken = default);

    Task<WorkspaceInfo> PrepareForInvocationAsync(
        FlowRun flow,
        ExecutionInvocationKind invocationKind,
        CancellationToken cancellationToken = default) =>
        PrepareAsync(flow, cancellationToken);

    Task<WorkspaceCleanupResult> RemoveAsync(
        FlowRun flow,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(WorkspaceCleanupResult.Empty);
}

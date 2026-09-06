using AiHarnessDemo.Core.Domain;

namespace AiHarnessDemo.Core.Orchestration;

public sealed record WorkspaceRepositoryIdentity(
    string RelativePath,
    string RemoteRepository);

public sealed record WorkspaceInfo(
    string Path,
    string BranchName,
    bool CreatedNow,
    IReadOnlyList<WorkspaceRepositoryIdentity>? TrustedRepositories = null);

public sealed record WorkspaceCleanupResult(
    int WorktreesRemoved,
    int LocalBranchesDeleted,
    int RemoteBranchesDeleted)
{
    public static WorkspaceCleanupResult Empty { get; } = new(0, 0, 0);
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

    Task<WorkspaceCleanupResult> RemoveAsync(
        FlowRun flow,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(WorkspaceCleanupResult.Empty);
}

using AiHarnessDemo.Core.Domain;

namespace AiHarnessDemo.Core.Orchestration;

public sealed record WorkspaceInfo(string Path, string BranchName, bool CreatedNow);

/// <summary>
/// Execution-layer seam from Symphony: the orchestrator receives an isolated workspace without
/// depending on Git or any specific repository population strategy.
/// </summary>
public interface IWorkspaceManager
{
    Task<WorkspaceInfo> PrepareAsync(
        FlowRun flow,
        CancellationToken cancellationToken = default);
}

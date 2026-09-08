using System.Collections.Concurrent;
using AiHarnessDemo.Core.Domain;

namespace AiHarnessDemo.Services;

public sealed class FlowLifecycleException(
    Guid flowId,
    FlowStatus source,
    FlowStatus target,
    string? reason = null)
    : InvalidOperationException(
        $"Flow '{flowId}' lifecycle transition {source} -> {target} is not allowed" +
        (string.IsNullOrWhiteSpace(reason) ? "." : $": {reason}"))
{
    public Guid FlowId { get; } = flowId;

    public FlowStatus SourceStatus { get; } = source;

    public FlowStatus TargetStatus { get; } = target;
}

public sealed class FlowLifecycleCoordinator
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async ValueTask<IAsyncDisposable> EnterAsync(
        Guid flowId,
        CancellationToken cancellationToken = default)
    {
        var gate = _locks.GetOrAdd(flowId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Releaser(gate);
    }

    public bool Transition(FlowRun flow, FlowStatus target)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (!Enum.IsDefined(target))
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }
        if (flow.Status == target)
        {
            return false;
        }
        if (!IsAllowed(flow.Status, target))
        {
            throw new FlowLifecycleException(flow.Id, flow.Status, target);
        }

        flow.Status = target;
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    public bool RequeueAfterRecovery(FlowRun flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (flow.Status == FlowStatus.Queued)
        {
            return false;
        }
        if (flow.Status is not (FlowStatus.Running or FlowStatus.Reworking))
        {
            throw new FlowLifecycleException(
                flow.Id,
                flow.Status,
                FlowStatus.Queued,
                "only interrupted Running or Reworking work can be re-queued automatically");
        }

        flow.Status = FlowStatus.Queued;
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    public void InitializeLinkedSuccessor(
        FlowRun parent,
        FlowRun successor,
        FlowLinkKind linkKind)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(successor);
        if (!Enum.IsDefined(linkKind))
        {
            throw new ArgumentOutOfRangeException(nameof(linkKind));
        }
        if (successor.Id == Guid.Empty || successor.Id == parent.Id ||
            successor.ParentFlowRunId is not null ||
            successor.ParentIteration is not null ||
            successor.LinkKind is not null ||
            successor.Status != FlowStatus.Intake)
        {
            throw new InvalidOperationException(
                "A linked successor must be a fresh Intake flow with no existing parent link.");
        }

        successor.ParentFlowRunId = parent.Id;
        successor.ParentIteration = parent.Iteration;
        successor.LinkKind = linkKind;
    }

    internal static bool IsAllowed(FlowStatus source, FlowStatus target) =>
        (source, target) switch
        {
            (FlowStatus.Intake, FlowStatus.Queued or FlowStatus.Abandoning) => true,
            (FlowStatus.Queued, FlowStatus.Running or FlowStatus.Abandoning or
                FlowStatus.Failed or FlowStatus.Approved) => true,
            (FlowStatus.Running, FlowStatus.WaitingForFeedback or FlowStatus.Blocked or
                FlowStatus.Approved or FlowStatus.Abandoning or FlowStatus.Failed) => true,
            (FlowStatus.Reworking, FlowStatus.Running or FlowStatus.WaitingForFeedback or
                FlowStatus.Blocked or FlowStatus.Approved or FlowStatus.Abandoning or
                FlowStatus.Failed) => true,
            (FlowStatus.WaitingForFeedback, FlowStatus.Reworking or FlowStatus.Queued or
                FlowStatus.Approved or FlowStatus.Abandoning or FlowStatus.Failed) => true,
            (FlowStatus.Blocked, FlowStatus.Abandoning) => true,
            (FlowStatus.Abandoning, FlowStatus.Abandoned) => true,
            (FlowStatus.Failed, FlowStatus.Queued or FlowStatus.Abandoning) => true,
            _ => false
        };

    private sealed class Releaser(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}

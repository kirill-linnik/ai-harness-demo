using System.Collections.Concurrent;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Verification;

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
        if (target == FlowStatus.Approved &&
            flow.Kind == FlowKind.Delivery)
        {
            throw new FlowLifecycleException(
                flow.Id,
                flow.Status,
                target,
                "a Studio Delivery flow reaches Approved only through " +
                nameof(CompletePublishedDelivery));
        }
        return TransitionCore(flow, target);
    }

    /// <summary>
    /// Opens the separate informed-consent gate. It requires a current
    /// <see cref="DeliveryReadinessState.NeedsCustomerWaiver"/> assessment bound to the exact sealed
    /// candidate; a waiver gate is never product acceptance.
    /// </summary>
    public bool OpenWaiverReview(
        FlowRun flow,
        DeliveryReadinessState state,
        string candidateFingerprint,
        string boundCandidateFingerprint)
    {
        RequireDeliveryBinding(
            flow,
            state,
            DeliveryReadinessState.NeedsCustomerWaiver,
            candidateFingerprint,
            boundCandidateFingerprint,
            FlowStatus.WaitingForFeedback);
        return TransitionCore(flow, FlowStatus.WaitingForFeedback);
    }

    /// <summary>
    /// Opens the ordinary customer review. It requires a current
    /// <see cref="DeliveryReadinessState.ReadyToApprove"/> assessment with no failed, blocked, or
    /// missing criterion and with every required waiver already granted.
    /// </summary>
    public bool OpenCustomerReview(
        FlowRun flow,
        DeliveryReadinessState state,
        string candidateFingerprint,
        string boundCandidateFingerprint)
    {
        RequireDeliveryBinding(
            flow,
            state,
            DeliveryReadinessState.ReadyToApprove,
            candidateFingerprint,
            boundCandidateFingerprint,
            FlowStatus.WaitingForFeedback);
        return TransitionCore(flow, FlowStatus.WaitingForFeedback);
    }

    /// <summary>
    /// Queues the approved publication. Only an accepted ordinary customer review bound to the same
    /// candidate and readiness hashes can reach this method.
    /// </summary>
    public bool QueueApprovedPublication(
        FlowRun flow,
        DeliveryReadinessState state,
        string candidateFingerprint,
        string boundCandidateFingerprint)
    {
        RequireDeliveryBinding(
            flow,
            state,
            DeliveryReadinessState.ReadyToApprove,
            candidateFingerprint,
            boundCandidateFingerprint,
            FlowStatus.Queued);
        return TransitionCore(flow, FlowStatus.Queued);
    }

    /// <summary>
    /// The only path to <see cref="FlowStatus.Approved"/> for a Studio Delivery flow. The caller
    /// must have re-read the authoritative readiness, waiver, review, and publication rows.
    /// </summary>
    public bool CompletePublishedDelivery(
        FlowRun flow,
        DeliveryReadinessState state,
        string candidateFingerprint,
        string boundCandidateFingerprint,
        bool publicationVerified)
    {
        RequireDeliveryBinding(
            flow,
            state,
            DeliveryReadinessState.ReadyToApprove,
            candidateFingerprint,
            boundCandidateFingerprint,
            FlowStatus.Approved);
        if (!publicationVerified)
        {
            throw new FlowLifecycleException(
                flow.Id,
                flow.Status,
                FlowStatus.Approved,
                "the reviewed publication journal is not verified for this readiness binding");
        }
        return TransitionCore(flow, FlowStatus.Approved);
    }

    /// <summary>
    /// The only path out of a non-releasable readiness state. It requires the exact derived state
    /// and candidate binding and can reach Queued or Reworking, but never Approved. The ordinary
    /// transition table stays closed for <see cref="FlowStatus.Blocked"/>, so this guarded method
    /// is the sole way a blocked Delivery flow can move.
    /// </summary>
    public bool ResolveReadinessState(
        FlowRun flow,
        DeliveryReadinessState state,
        DeliveryReadinessState required,
        string candidateFingerprint,
        string boundCandidateFingerprint,
        FlowStatus target)
    {
        if (target is not (FlowStatus.Queued or FlowStatus.Reworking))
        {
            throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                "A readiness resolution can only queue or rework the flow.");
        }
        RequireDeliveryBinding(
            flow,
            state,
            required,
            candidateFingerprint,
            boundCandidateFingerprint,
            target);
        if (flow.Status is not (FlowStatus.Blocked or FlowStatus.WaitingForFeedback
            or FlowStatus.Queued or FlowStatus.Reworking))
        {
            throw new FlowLifecycleException(
                flow.Id,
                flow.Status,
                target,
                "only a waiting or blocked readiness state can be resolved");
        }
        if (flow.Status == target)
        {
            return false;
        }
        flow.Status = target;
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    /// <summary>
    /// The host-owned counterpart to <see cref="ResolveReadinessState"/>. It lets the harness
    /// schedule its own refinement iteration directly from the verification turn that produced a
    /// <see cref="DeliveryReadinessState.NeedsRefinement"/> assessment, so a recoverable Delivery
    /// failure never has to park on the customer. It is deliberately narrow: only a
    /// <c>NeedsRefinement</c> binding qualifies, and it can only reach
    /// <see cref="FlowStatus.Reworking"/>.
    /// </summary>
    public bool ScheduleAutoRefinement(
        FlowRun flow,
        DeliveryReadinessState state,
        string candidateFingerprint,
        string boundCandidateFingerprint)
    {
        RequireDeliveryBinding(
            flow,
            state,
            DeliveryReadinessState.NeedsRefinement,
            candidateFingerprint,
            boundCandidateFingerprint,
            FlowStatus.Reworking);
        if (flow.Status is not (FlowStatus.Running or FlowStatus.Reworking
            or FlowStatus.WaitingForFeedback))
        {
            throw new FlowLifecycleException(
                flow.Id,
                flow.Status,
                FlowStatus.Reworking,
                "only a running, reworking, or waiting flow can schedule host-owned refinement");
        }
        if (flow.Status == FlowStatus.Reworking)
        {
            return false;
        }
        flow.Status = FlowStatus.Reworking;
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    private static void RequireDeliveryBinding(
        FlowRun flow,
        DeliveryReadinessState state,
        DeliveryReadinessState required,
        string candidateFingerprint,
        string boundCandidateFingerprint,
        FlowStatus target)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (flow.Kind != FlowKind.Delivery)
        {
            throw new FlowLifecycleException(
                flow.Id,
                flow.Status,
                target,
                "guarded readiness lifecycle operations apply only to Studio Delivery flows");
        }
        if (state != required)
        {
            throw new FlowLifecycleException(
                flow.Id,
                flow.Status,
                target,
                $"the host-derived readiness state is '{state}', not '{required}'");
        }
        if (string.IsNullOrWhiteSpace(candidateFingerprint) ||
            !string.Equals(
                candidateFingerprint,
                boundCandidateFingerprint,
                StringComparison.Ordinal))
        {
            throw new FlowLifecycleException(
                flow.Id,
                flow.Status,
                target,
                "the readiness assessment is not bound to the current sealed candidate");
        }
    }

    private static bool TransitionCore(FlowRun flow, FlowStatus target)
    {
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

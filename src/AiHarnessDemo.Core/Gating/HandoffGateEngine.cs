using AiHarnessDemo.Core.Domain;

namespace AiHarnessDemo.Core.Gating;

/// <summary>How much autonomy an agent action currently has.</summary>
public enum HandoffTrustLevel
{
    Shadow,
    Gated,
    Auto
}

public enum HandoffActionType
{
    Advance,
    RequestRevision,
    CustomerReview,

    /// <summary>
    /// The separate informed-consent gate for waiver-required residual risks. It is deliberately
    /// distinct from <see cref="CustomerReview"/>: a granted waiver is never product acceptance.
    /// </summary>
    CustomerWaiver
}

public enum HandoffBlastRadius
{
    Low,
    Medium,
    High
}

public enum HandoffGateDecision
{
    BlockedKillSwitch,
    LoggedShadow,
    AwaitingHumanApproval,
    AutoApproved
}

public sealed class HandoffProposal
{
    public required Guid FlowRunId { get; init; }

    public required Guid FlowStepId { get; init; }

    public required HandoffActionType ActionType { get; init; }

    public required string Summary { get; init; }

    public string Evidence { get; init; } = string.Empty;

    public HandoffBlastRadius BlastRadius { get; init; } = HandoffBlastRadius.Medium;
}

/// <summary>
/// A permanent record of an agent proposal, the gate decision, and any later human resolution.
/// Approval and side-effect execution intentionally remain separate.
/// </summary>
public sealed class HandoffGateRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FlowRunId { get; set; }

    public Guid FlowStepId { get; set; }

    public HandoffActionType ActionType { get; set; }

    public HandoffGateDecision Decision { get; set; }

    public ReviewDecision? ReviewDecision { get; set; }

    public HandoffTrustLevel TrustLevelAtDecision { get; set; }

    public string Summary { get; set; } = string.Empty;

    public string Evidence { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset DecidedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool Resolved { get; set; }

    public bool? Approved { get; set; }

    public string? ResolvedBy { get; set; }

    public string? ResolutionNote { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>
/// Thread-safe trust state machine. Decision precedence is deliberate: kill switch, shadow,
/// high-impact/human gate, then automatic approval.
/// </summary>
public sealed class HandoffGateEngine : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<HandoffActionType, HandoffTrustLevel> _trustLevels = new();
    private readonly List<HandoffGateRecord> _history = [];
    private readonly List<CancellationTokenSource> _retiredKillSwitchSources = [];
    private CancellationTokenSource _killSwitchSource = new();
    private bool _killSwitchEngaged;

    public bool KillSwitchEngaged
    {
        get
        {
            lock (_lock)
            {
                return _killSwitchEngaged;
            }
        }
    }

    public CancellationToken KillSwitchToken
    {
        get
        {
            lock (_lock)
            {
                return _killSwitchSource.Token;
            }
        }
    }

    public void EngageKillSwitch()
    {
        CancellationTokenSource? source;
        lock (_lock)
        {
            source = _killSwitchEngaged ? null : _killSwitchSource;
            _killSwitchEngaged = true;
        }

        source?.Cancel();
    }

    public void DisengageKillSwitch()
    {
        lock (_lock)
        {
            if (!_killSwitchEngaged)
            {
                return;
            }

            _killSwitchEngaged = false;
            _retiredKillSwitchSources.Add(_killSwitchSource);
            _killSwitchSource = new CancellationTokenSource();
        }
    }

    public HandoffTrustLevel GetTrustLevel(HandoffActionType actionType)
    {
        if (actionType == HandoffActionType.CustomerReview)
        {
            return HandoffTrustLevel.Gated;
        }
        if (actionType == HandoffActionType.CustomerWaiver)
        {
            return HandoffTrustLevel.Gated;
        }
        lock (_lock)
        {
            return _trustLevels.GetValueOrDefault(actionType, HandoffTrustLevel.Shadow);
        }
    }

    public void SetTrustLevel(HandoffActionType actionType, HandoffTrustLevel trustLevel)
    {
        if (actionType == HandoffActionType.CustomerReview &&
            trustLevel != HandoffTrustLevel.Gated)
        {
            throw new InvalidOperationException(
                "CustomerReview is human-gated and must remain at Gated trust.");
        }
        if (actionType == HandoffActionType.CustomerWaiver &&
            trustLevel != HandoffTrustLevel.Gated)
        {
            throw new InvalidOperationException(
                "CustomerWaiver is human-gated and must remain at Gated trust.");
        }
        if (trustLevel == HandoffTrustLevel.Auto &&
            actionType is
                HandoffActionType.CustomerReview or
                HandoffActionType.CustomerWaiver)
        {
            throw new InvalidOperationException(
                $"{actionType} is human-gated and can never receive automatic trust.");
        }

        lock (_lock)
        {
            _trustLevels[actionType] = trustLevel;
        }
    }

    public HandoffGateRecord PrepareReviewResolution(
        HandoffGateRecord record,
        ReviewDecision decision,
        string resolvedBy,
        string? note = null,
        DateTimeOffset? resolvedAt = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.ActionType != HandoffActionType.CustomerReview)
        {
            throw new InvalidOperationException(
                $"Gate record {record.Id} is not a customer review.");
        }
        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision));
        }

        var resolved = PrepareResolutionCore(
            record,
            decision is ReviewDecision.Accepted or ReviewDecision.PromotedToDelivery,
            resolvedBy,
            note,
            resolvedAt);
        resolved.ReviewDecision = decision;
        return resolved;
    }

    public HandoffGateRecord SubmitProposal(HandoffProposal proposal)
    {
        lock (_lock)
        {
            var trustLevel = proposal.ActionType is
                HandoffActionType.CustomerReview or HandoffActionType.CustomerWaiver
                ? HandoffTrustLevel.Gated
                : _trustLevels.GetValueOrDefault(
                    proposal.ActionType,
                    HandoffTrustLevel.Shadow);
            var decision = Decide(proposal, trustLevel);
            var record = new HandoffGateRecord
            {
                FlowRunId = proposal.FlowRunId,
                FlowStepId = proposal.FlowStepId,
                ActionType = proposal.ActionType,
                Decision = decision,
                TrustLevelAtDecision = trustLevel,
                Summary = proposal.Summary,
                Evidence = proposal.Evidence,
                Reason = DecisionReason(proposal, decision)
            };
            _history.Add(record);
            return record;
        }
    }

    public HandoffGateRecord ResolveProposal(
        Guid recordId,
        bool approved,
        string resolvedBy,
        string? note = null)
    {
        lock (_lock)
        {
            var record = RequireRecord(recordId);
            var resolved = PrepareResolution(
                record,
                approved,
                resolvedBy,
                note);
            CopyRecord(resolved, record);
            return record;
        }
    }

    public HandoffGateRecord PrepareResolution(
        HandoffGateRecord record,
        bool approved,
        string resolvedBy,
        string? note = null,
        DateTimeOffset? resolvedAt = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.ActionType == HandoffActionType.CustomerReview)
        {
            throw new InvalidOperationException(
                "CustomerReview must be resolved with a typed ReviewDecision.");
        }
        return PrepareResolutionCore(
            record,
            approved,
            resolvedBy,
            note,
            resolvedAt);
    }

    private static HandoffGateRecord PrepareResolutionCore(
        HandoffGateRecord record,
        bool approved,
        string resolvedBy,
        string? note,
        DateTimeOffset? resolvedAt)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Decision != HandoffGateDecision.AwaitingHumanApproval)
        {
            throw new InvalidOperationException(
                $"Gate record {record.Id} does not require human approval.");
        }
        if (record.Resolved)
        {
            throw new InvalidOperationException(
                $"Gate record {record.Id} was already resolved.");
        }
        if (string.IsNullOrWhiteSpace(resolvedBy))
        {
            throw new ArgumentException(
                "A gate resolution requires an actor.",
                nameof(resolvedBy));
        }

        return new HandoffGateRecord
        {
            Id = record.Id,
            FlowRunId = record.FlowRunId,
            FlowStepId = record.FlowStepId,
            ActionType = record.ActionType,
            Decision = record.Decision,
            ReviewDecision = record.ReviewDecision,
            TrustLevelAtDecision = record.TrustLevelAtDecision,
            Summary = record.Summary,
            Evidence = record.Evidence,
            Reason = record.Reason,
            DecidedAt = record.DecidedAt,
            Resolved = true,
            Approved = approved,
            ResolvedBy = resolvedBy,
            ResolutionNote = note,
            ResolvedAt = resolvedAt ?? DateTimeOffset.UtcNow
        };
    }

    public HandoffGateRecord SupersedeProposal(
        Guid recordId,
        string resolvedBy,
        string note)
    {
        lock (_lock)
        {
            var record = RequireRecord(recordId);
            var superseded = PrepareSupersession(
                record,
                resolvedBy,
                note);
            CopyRecord(superseded, record);
            return record;
        }
    }

    public HandoffGateRecord PrepareSupersession(
        HandoffGateRecord record,
        string resolvedBy,
        string note,
        DateTimeOffset? resolvedAt = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Resolved)
        {
            throw new InvalidOperationException(
                $"Gate record {record.Id} was already resolved.");
        }
        if (string.IsNullOrWhiteSpace(resolvedBy))
        {
            throw new ArgumentException(
                "A gate supersession requires an actor.",
                nameof(resolvedBy));
        }
        if (string.IsNullOrWhiteSpace(note))
        {
            throw new ArgumentException(
                "A gate supersession requires a reason.",
                nameof(note));
        }

        return new HandoffGateRecord
        {
            Id = record.Id,
            FlowRunId = record.FlowRunId,
            FlowStepId = record.FlowStepId,
            ActionType = record.ActionType,
            Decision = record.Decision,
            ReviewDecision = record.ReviewDecision,
            TrustLevelAtDecision = record.TrustLevelAtDecision,
            Summary = record.Summary,
            Evidence = record.Evidence,
            Reason = record.Reason,
            DecidedAt = record.DecidedAt,
            Resolved = true,
            Approved = false,
            ResolvedBy = resolvedBy,
            ResolutionNote = note,
            ResolvedAt = resolvedAt ?? DateTimeOffset.UtcNow
        };
    }

    public IReadOnlyList<HandoffGateRecord> History()
    {
        lock (_lock)
        {
            return [.. _history];
        }
    }

    public void RestoreHistory(IEnumerable<HandoffGateRecord> records)
    {
        lock (_lock)
        {
            foreach (var record in records)
            {
                var existing = _history.FirstOrDefault(item => item.Id == record.Id);
                if (existing is null)
                {
                    _history.Add(record);
                }
                else
                {
                    CopyRecord(record, existing);
                }
            }
        }
    }

    private static void CopyRecord(
        HandoffGateRecord source,
        HandoffGateRecord destination)
    {
        destination.Id = source.Id;
        destination.FlowRunId = source.FlowRunId;
        destination.FlowStepId = source.FlowStepId;
        destination.ActionType = source.ActionType;
        destination.Decision = source.Decision;
        destination.ReviewDecision = source.ReviewDecision;
        destination.TrustLevelAtDecision = source.TrustLevelAtDecision;
        destination.Summary = source.Summary;
        destination.Evidence = source.Evidence;
        destination.Reason = source.Reason;
        destination.DecidedAt = source.DecidedAt;
        destination.Resolved = source.Resolved;
        destination.Approved = source.Approved;
        destination.ResolvedBy = source.ResolvedBy;
        destination.ResolutionNote = source.ResolutionNote;
        destination.ResolvedAt = source.ResolvedAt;
    }

    private HandoffGateDecision Decide(
        HandoffProposal proposal,
        HandoffTrustLevel trustLevel)
    {
        if (_killSwitchEngaged)
        {
            return HandoffGateDecision.BlockedKillSwitch;
        }
        if (proposal.ActionType is
            HandoffActionType.CustomerReview or HandoffActionType.CustomerWaiver)
        {
            return HandoffGateDecision.AwaitingHumanApproval;
        }
        if (trustLevel == HandoffTrustLevel.Shadow)
        {
            return HandoffGateDecision.LoggedShadow;
        }
        if (proposal.BlastRadius == HandoffBlastRadius.High ||
            trustLevel == HandoffTrustLevel.Gated)
        {
            return HandoffGateDecision.AwaitingHumanApproval;
        }

        return HandoffGateDecision.AutoApproved;
    }

    private static string DecisionReason(
        HandoffProposal proposal,
        HandoffGateDecision decision) => decision switch
        {
            HandoffGateDecision.BlockedKillSwitch =>
                "The global harness kill switch is engaged.",
            HandoffGateDecision.LoggedShadow =>
                "This action is in shadow mode and was observed without execution.",
            HandoffGateDecision.AwaitingHumanApproval
                when proposal.ActionType == HandoffActionType.CustomerReview =>
                "Customer review requires an explicit durable human decision.",
            HandoffGateDecision.AwaitingHumanApproval
                when proposal.ActionType == HandoffActionType.CustomerWaiver =>
                "Waiver-required residual risks need explicit informed customer consent before review.",
            HandoffGateDecision.AwaitingHumanApproval =>
                "The proposal is gated because its trust level or blast radius requires human approval.",
            HandoffGateDecision.AutoApproved
                when proposal.ActionType == HandoffActionType.RequestRevision =>
                "The revision request was accepted at the configured automatic trust level.",
            HandoffGateDecision.AutoApproved =>
                "The handoff contract passed at the configured automatic trust level.",
            _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, null)
        };

    private HandoffGateRecord RequireRecord(Guid recordId) =>
        _history.FirstOrDefault(item => item.Id == recordId)
        ?? throw new KeyNotFoundException($"Gate record {recordId} was not found.");

    public void Dispose()
    {
        lock (_lock)
        {
            _killSwitchSource.Dispose();
            foreach (var source in _retiredKillSwitchSources)
            {
                source.Dispose();
            }
            _retiredKillSwitchSources.Clear();
        }
    }
}

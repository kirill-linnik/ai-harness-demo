using AiHarnessDemo.Core.Verification;

namespace AiHarnessDemo.Core.Domain;

/// <summary>
/// One immutable, host-derived readiness assessment for a Studio Delivery iteration. Exactly one
/// row per flow may be <see cref="Active"/>; superseding a row is the only way to change readiness,
/// so an authorization can always prove which assessment it used.
/// </summary>
public sealed class DeliveryReadinessSnapshotRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FlowRunId { get; set; }

    public int Iteration { get; set; }

    public int Revision { get; set; } = 1;

    public DeliveryReadinessState State { get; set; }

    public required string CandidateFingerprint { get; set; }

    public required string AcceptancePlanHash { get; set; }

    public required string OutcomeContractHash { get; set; }

    public string QaContractHash { get; set; } = string.Empty;

    public Guid OutcomeOwnerStepId { get; set; }

    public Guid QaStepId { get; set; }

    /// <summary>Canonical readiness JSON. It is never rewritten in place.</summary>
    public required string ContractJson { get; set; }

    /// <summary>SHA-256 of <see cref="ContractJson"/>; the immutable binding token for review,
    /// waiver, publication, and final approval.</summary>
    public required string ContractHash { get; set; }

    /// <summary>Deterministic 1/0 marker so SQLite can enforce one active row per flow.</summary>
    public bool Active { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? SupersededAt { get; set; }
}

/// <summary>
/// The relational identity of one sealed review candidate, bound immutably to exactly one readiness
/// snapshot. Publication and acceptance re-read this row rather than trusting an in-memory gate.
/// </summary>
public sealed class ReviewedCandidateRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FlowRunId { get; set; }

    public int Iteration { get; set; }

    public required string CandidateFingerprint { get; set; }

    public Guid OutcomeOwnerStepId { get; set; }

    public required string OutcomeContractHash { get; set; }

    public required string AcceptancePlanHash { get; set; }

    public Guid ReadinessSnapshotId { get; set; }

    public required string ReadinessContractHash { get; set; }

    /// <summary>The durable sealed candidate identity JSON as sealed before review.</summary>
    public required string IdentityJson { get; set; }

    public bool Active { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? SupersededAt { get; set; }
}

/// <summary>
/// One immutable file from the customer preview that was sealed for review. Preview bytes are
/// copied into host-owned durable storage before the flow enters customer review, so serving the
/// reviewed result never depends on the mutable flow workspace.
/// </summary>
public sealed class ReviewedPreviewArtifactRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FlowRunId { get; set; }

    public int Iteration { get; set; }

    public required string CandidateFingerprint { get; set; }

    public required string RelativePath { get; set; }

    public long Length { get; set; }

    public required string Digest { get; set; }

    public required byte[] Content { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One immutable customer waiver receipt for a single waiver-required residual risk. A waiver can
/// never name an acceptance criterion, a failed or blocked result, or a blocking risk.
/// </summary>
public sealed class ReadinessWaiverRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FlowRunId { get; set; }

    public Guid ReviewedCandidateId { get; set; }

    public Guid ReadinessSnapshotId { get; set; }

    public required string ReadinessContractHash { get; set; }

    public Guid GateId { get; set; }

    public required string RiskId { get; set; }

    public required string Actor { get; set; }

    public required string Acknowledgement { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

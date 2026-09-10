namespace AiHarnessDemo.Core.Verification;

public enum OutcomeVerificationStatus
{
    NotStarted,
    Planning,
    CollectingEvidence,
    PreparingCandidate,
    AwaitingQa,
    Correcting,
    AwaitingCandidateRefresh,
    Passed,
    AwaitingHumanResolution,
    Superseded
}

public enum OutcomeEvidenceKind
{
    Test,
    Command,
    Artifact,
    Observation,
    SourceInspection
}

public enum OutcomeEvidenceDisposition
{
    Supports,
    Contradicts,
    Inconclusive
}

public enum OutcomeCriterionStatus
{
    PASS,
    FAIL,
    BLOCKED
}

public enum OutcomeQaVerdict
{
    PASS,
    FAIL,
    BLOCKED
}

public enum OutcomeResolutionAction
{
    Continue,
    Replan
}

public enum OutcomePublicationStatus
{
    Publishing,
    Published,
    Verified
}

public enum OutcomeRepositoryPublicationStatus
{
    Pending,
    Publishing,
    Published
}

public sealed record OutcomeAcceptanceCriterion(
    string Id,
    string Requirement,
    string Verification,
    IReadOnlyList<string> OwnerRoles,
    IReadOnlyList<OutcomeEvidenceKind> EvidenceKinds,
    bool CustomerVisible);

public sealed record OutcomeAcceptancePlan(
    string Version,
    IReadOnlyList<OutcomeAcceptanceCriterion> Criteria);

public sealed record OutcomeAcceptancePlanSnapshot(
    string Hash,
    Guid SourceStepId,
    IReadOnlyList<OutcomeAcceptanceCriterion> Criteria);

public sealed record OutcomeTrustedRepository(
    string RelativePath,
    string RemoteRepository);

public sealed record OutcomeDeliveryEvidenceInput(
    string CriterionId,
    OutcomeEvidenceDisposition Disposition,
    OutcomeEvidenceKind Kind,
    string Locator,
    string ObservedResult,
    int? ExitCode,
    string? ContentDigest);

public sealed record OutcomeDeliveryEvidenceDocument(
    string Version,
    IReadOnlyList<OutcomeDeliveryEvidenceInput> Items);

public sealed record OutcomeEvidence(
    string EvidenceId,
    string CriterionId,
    OutcomeEvidenceDisposition Disposition,
    OutcomeEvidenceKind Kind,
    string Locator,
    string ObservedResult,
    int? ExitCode,
    string? ContentDigest,
    string ProducerRole,
    Guid ProducerStepId,
    DateTimeOffset ProducedAt);

public sealed record OutcomeEvidenceProcessing(
    Guid ProducerStepId,
    string AcceptancePlanHash,
    string ProducerRole,
    IReadOnlyList<string> CriterionIds,
    DateTimeOffset ProcessedAt);

public sealed record OutcomeQaCheck(
    OutcomeEvidenceKind Kind,
    string Locator,
    string ObservedResult,
    int? ExitCode);

public sealed record OutcomeQaCriterionResult(
    string CriterionId,
    OutcomeCriterionStatus Status,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<OutcomeQaCheck> ChecksPerformed,
    string Rationale,
    IReadOnlyList<string> ResponsibleRoles,
    string? Remediation);

public sealed record OutcomePlanGap(
    string Requirement,
    string Verification,
    IReadOnlyList<string> OwnerRoles,
    string Rationale);

public sealed record OutcomeQaResult(
    string Version,
    string AcceptancePlanHash,
    string CandidateFingerprint,
    OutcomeQaVerdict Verdict,
    IReadOnlyList<OutcomeQaCriterionResult> Criteria,
    IReadOnlyList<OutcomePlanGap> PlanGaps);

public sealed record CandidateRepositoryManifest(
    string RelativePath,
    string Head,
    string Tree,
    string RemoteRepository = "");

public sealed record CandidatePreviewArtifact(
    string RelativePath,
    long Length,
    string Digest);

public sealed record CandidateScaffoldFile(
    string RelativePath,
    long Length,
    string Digest);

public sealed record CandidateManifest(
    string Version,
    int FlowIteration,
    string AcceptancePlanHash,
    IReadOnlyList<CandidateRepositoryManifest> Repositories,
    IReadOnlyList<CandidateScaffoldFile> TrustedScaffoldFiles,
    IReadOnlyList<CandidatePreviewArtifact> PreviewArtifacts)
{
    public const int MaximumPreviewArtifacts = 2_000;
}

public sealed record OutcomeCandidateSnapshot(
    CandidateManifest Manifest,
    string Fingerprint,
    Guid PreparedByStepId,
    DateTimeOffset PreparedAt);

public sealed class OutcomeQaRound
{
    public int Round { get; set; }

    public Guid QaStepId { get; set; }

    public string AcceptancePlanHash { get; set; } = string.Empty;

    public string CandidateFingerprint { get; set; } = string.Empty;

    public string ContextHash { get; set; } = string.Empty;

    public OutcomeQaVerdict? Verdict { get; set; }

    public OutcomeQaResult? Result { get; set; }

    public string ContractError { get; set; } = string.Empty;

    public bool Stale { get; set; }

    public List<Guid> CorrectionStepIds { get; set; } = [];

    public List<OutcomeCorrectionRegistration> CorrectionRegistrations { get; set; } = [];

    public List<Guid> ProcessedCorrectionRootIds { get; set; } = [];

    public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record OutcomeCorrectionRegistration(
    int QaRound,
    string OutcomePlanHash,
    string Role,
    Guid StepId,
    Guid SemanticRootId);

public sealed class OutcomeVerificationState
{
    public string Version { get; set; } = OutcomeVerificationRules.AggregateVersion;

    public int Iteration { get; set; }

    public OutcomeVerificationStatus Status { get; set; } =
        OutcomeVerificationStatus.NotStarted;

    public int MaxRounds { get; set; }

    public int ManualRoundsGranted { get; set; }

    public List<OutcomeVerificationIterationArchive> PriorIterations { get; set; } = [];

    public List<OutcomeTrustedRepository> TrustedRepositories { get; set; } = [];

    public List<string> PlannedRoles { get; set; } = [];

    public Guid? InitialPlanSemanticRootId { get; set; }

    public List<Guid> InitialDeliverySemanticRootIds { get; set; } = [];

    public List<Guid> ProcessedSemanticRootIds { get; set; } = [];

    public OutcomeAcceptancePlanSnapshot? AcceptancePlan { get; set; }

    public List<OutcomeEvidence> Evidence { get; set; } = [];

    public List<OutcomeEvidenceProcessing> EvidenceProcessing { get; set; } = [];

    public List<OutcomeQaRound> Rounds { get; set; } = [];

    public int? ActiveQaRound { get; set; }

    public Guid? ActiveQaStepId { get; set; }

    public string? ActiveQaContextPath { get; set; }

    public string? ActiveQaContextHash { get; set; }

    public OutcomeCandidateSnapshot? CurrentCandidate { get; set; }

    public OutcomePublicationJournal? Publication { get; set; }

    public string? VerifiedCandidateFingerprint { get; set; }

    public DateTimeOffset? VerifiedAt { get; set; }

    public List<string> PendingOwnerRoles { get; set; } = [];

    public bool Stale { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class OutcomePublicationJournal
{
    /// <summary>
    /// Stable publication root identity carried across retries of the same approved publication.
    /// </summary>
    public Guid StepId { get; set; }

    public string CandidateFingerprint { get; set; } = string.Empty;

    public OutcomePublicationStatus Status { get; set; } =
        OutcomePublicationStatus.Publishing;

    public List<OutcomeRepositoryPublication> Repositories { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class OutcomeRepositoryPublication
{
    public string RelativePath { get; set; } = string.Empty;

    public string Head { get; set; } = string.Empty;

    public string Tree { get; set; } = string.Empty;

    public string RemoteRepository { get; set; } = string.Empty;

    public string PullRequestUrl { get; set; } = string.Empty;

    public OutcomeRepositoryPublicationStatus Status { get; set; } =
        OutcomeRepositoryPublicationStatus.Pending;
}

public sealed record OutcomeVerificationIterationArchive(
    int Iteration,
    OutcomeVerificationStatus Status,
    int MaxRounds,
    int ManualRoundsGranted,
    IReadOnlyList<string> PlannedRoles,
    Guid? InitialPlanSemanticRootId,
    IReadOnlyList<Guid> InitialDeliverySemanticRootIds,
    IReadOnlyList<Guid> ProcessedSemanticRootIds,
    OutcomeAcceptancePlanSnapshot? AcceptancePlan,
    IReadOnlyList<OutcomeEvidence> Evidence,
    IReadOnlyList<OutcomeEvidenceProcessing> EvidenceProcessing,
    IReadOnlyList<OutcomeQaRound> Rounds,
    OutcomeCandidateSnapshot? CurrentCandidate,
    OutcomePublicationJournal? Publication,
    string? VerifiedCandidateFingerprint,
    DateTimeOffset ArchivedAt);

public sealed class OutcomeVerificationValidationException(IReadOnlyList<string> errors)
    : InvalidOperationException(
        "Outcome verification validation failed: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

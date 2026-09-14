namespace AiHarnessDemo.Core.Verification;

public enum OutcomeEvidenceKind
{
    Test,
    Command,
    Artifact,
    Observation,
    SourceInspection
}

public enum OutcomeQaVerdict
{
    PASS,
    FAIL,
    BLOCKED
}

public sealed record OutcomeTrustedRepository(
    string RelativePath,
    string RemoteRepository);

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

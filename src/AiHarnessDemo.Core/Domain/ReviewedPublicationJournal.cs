namespace AiHarnessDemo.Core.Domain;

/// <summary>
/// Durable lifecycle of one repository inside a reviewed studio-v2 publication.
/// Every stage is persisted <em>before</em> the external side effect it authorizes so a crash,
/// restart, or concurrent invocation reconciles against the recorded intent instead of pushing
/// twice or opening a duplicate pull request.
/// </summary>
public enum ReviewedPublicationStage
{
    /// <summary>Intent recorded; no external side effect has been attempted yet.</summary>
    Intent = 0,

    /// <summary>The reviewed commit is confirmed present on the remote branch.</summary>
    BranchPublished = 1,

    /// <summary>A pull request exists for the reviewed head and is durably identified.</summary>
    PullRequestOpened = 2,

    /// <summary>Every external side effect for this repository is durably recorded.</summary>
    Completed = 3
}

/// <summary>
/// One durable row per (flow, publication root, repository). The reviewed fingerprint, repository
/// relative path, remote repository, branch, head, and tree are all bound to the row so a retry can
/// prove it is resuming the exact same publication instead of starting a different one.
/// </summary>
public sealed class ReviewedPublicationRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FlowRunId { get; set; }

    /// <summary>The stable semantic root of the publication step that owns this publication.</summary>
    public Guid PublicationRootId { get; set; }

    public int Iteration { get; set; }

    public required string CandidateFingerprint { get; set; }

    public required string RelativePath { get; set; }

    public string RemoteRepository { get; set; } = string.Empty;

    public string BranchName { get; set; } = string.Empty;

    public required string Head { get; set; }

    public required string Tree { get; set; }

    public ReviewedPublicationStage Stage { get; set; } =
        ReviewedPublicationStage.Intent;

    public string PullRequestUrl { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

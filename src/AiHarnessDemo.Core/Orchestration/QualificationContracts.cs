using System.Text.Json.Serialization;
using AiHarnessDemo.Core.Domain;

namespace AiHarnessDemo.Core.Orchestration;

[JsonConverter(typeof(ExactStringEnumConverter<QualificationResolutionAction>))]
public enum QualificationResolutionAction
{
    RosterRepair,
    ScopeRevision
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class QualificationResolutionRequest
{
    [JsonRequired]
    public QualificationResolutionAction? Action { get; init; }

    public QualificationScopeRevision? ScopeRevision { get; init; }

    public string? RevisedGoal { get; init; }

    public IReadOnlyList<string>? RevisedScope { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class QualificationScopeRevision
{
    [JsonRequired]
    public string Goal { get; init; } = string.Empty;

    [JsonRequired]
    public IReadOnlyList<string>? Scope { get; init; }
}

public sealed record QualificationResolutionResponse(
    Guid ParentFlowId,
    int ParentIteration,
    Guid SuccessorFlowId,
    FlowLinkKind LinkKind,
    FlowStatus ParentStatus,
    FlowStatus SuccessorStatus,
    bool ExistingSuccessor,
    string AccountManagerReply);

public sealed record FlowReviewResultResponse(
    Guid FlowId,
    Guid GateId,
    FlowStatus Status,
    int Iteration,
    bool Resolved,
    bool? Approved,
    ReviewDecision? Decision,
    ReviewPublicationStatus PublicationStatus,
    Guid? LinkedFlowId,
    FlowLinkKind? LinkKind);

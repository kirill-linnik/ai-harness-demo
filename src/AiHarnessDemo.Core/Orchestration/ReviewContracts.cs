using System.Text.Json;
using System.Text.Json.Serialization;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Verification;

namespace AiHarnessDemo.Core.Orchestration;

[JsonConverter(typeof(ExactReviewIntentJsonConverter))]
public enum ReviewIntent
{
    Accept,
    RequestRefinement,
    PromoteToDelivery
}

public enum ReviewPublicationStatus
{
    NotApplicable,
    AwaitingApproval,
    Queued,
    Running,
    Failed,
    Published
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DirectReviewRequest
{
    [JsonRequired]
    public Guid GateId { get; init; }

    [JsonRequired]
    public ReviewIntent? Intent { get; init; }

    public DirectReviewRefinement? Refinement { get; init; }

    /// <summary>
    /// The immutable readiness binding a Delivery client must echo back. A stale or absent binding
    /// is rejected with a typed conflict and never resolves the gate.
    /// </summary>
    public Guid? ReviewedCandidateId { get; init; }

    public int? ReadinessRevision { get; init; }

    public string? ReadinessContractHash { get; init; }
}

/// <summary>
/// The separate customer waiver request. It names the exact waiver-required residual risks and can
/// never target an acceptance criterion or a blocking risk.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ReadinessWaiverRequest
{
    [JsonRequired]
    public Guid GateId { get; init; }

    [JsonRequired]
    public Guid ReviewedCandidateId { get; init; }

    [JsonRequired]
    public int? ReadinessRevision { get; init; }

    [JsonRequired]
    public string? ReadinessContractHash { get; init; }

    [JsonRequired]
    public IReadOnlyList<string>? RiskIds { get; init; }

    [JsonRequired]
    public string? Acknowledgement { get; init; }
}

public sealed record ReadinessWaiverResponse(
    Guid FlowId,
    Guid GateId,
    Guid ReviewedCandidateId,
    int ReadinessRevision,
    string ReadinessContractHash,
    IReadOnlyList<string> WaivedRiskIds,
    string Message);

/// <summary>
/// The typed customer resolutions available when Delivery readiness is not releasable. None of
/// them can accept a result or waive a risk: they only route the work back into the factory.
/// </summary>
[JsonConverter(typeof(ExactStringEnumConverter<ReadinessResolutionAction>))]
public enum ReadinessResolutionAction
{
    /// <summary>Queue a new iteration from NeedsRefinement or decline a pending customer waiver.</summary>
    RequestRefinement,

    /// <summary>Re-run the current blocked iteration.</summary>
    Continue,

    /// <summary>Queue a new iteration from a <c>Blocked</c> assessment.</summary>
    Replan,

    /// <summary>Stop the flow through the existing durable abandonment path.</summary>
    Abandon
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ReadinessResolutionRequest
{
    [JsonRequired]
    public Guid ReviewedCandidateId { get; init; }

    [JsonRequired]
    public int? ReadinessRevision { get; init; }

    [JsonRequired]
    public string? ReadinessContractHash { get; init; }

    [JsonRequired]
    public ReadinessResolutionAction? Action { get; init; }

    public DirectReviewRefinement? Refinement { get; init; }
}

public sealed record ReadinessResolutionResponse(
    Guid FlowId,
    ReadinessResolutionAction Action,
    DeliveryReadinessState ResolvedFrom,
    FlowStatus Status,
    int Iteration,
    string Message);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DirectReviewRefinement
{
    public string? Goal { get; init; }

    [JsonRequired]
    public IReadOnlyList<string>? RequestedChanges { get; init; }
}

public sealed record DirectReviewResponse(
    Guid FlowId,
    Guid GateId,
    ReviewIntent Intent,
    ReviewDecision? Decision,
    FlowStatus Status,
    int Iteration,
    Guid? PublicationStepId,
    Guid? LinkedFlowId,
    ReviewPublicationStatus PublicationStatus,
    string Message);

public sealed class ExactReviewIntentJsonConverter : JsonConverter<ReviewIntent>
{
    public override ReviewIntent Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"{nameof(ReviewIntent)} must be an exact case-sensitive string.");
        }
        var value = reader.GetString();
        if (value is null ||
            !Enum.TryParse<ReviewIntent>(value, ignoreCase: false, out var parsed) ||
            !Enum.IsDefined(parsed) ||
            !string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
        {
            throw new JsonException(
                $"'{value}' is not an exact {nameof(ReviewIntent)} value.");
        }
        return parsed;
    }

    public override void Write(
        Utf8JsonWriter writer,
        ReviewIntent value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

using System.Text.Json;
using System.Text.Json.Serialization;
using AiHarnessDemo.Core.Domain;

namespace AiHarnessDemo.Core.Orchestration;

[JsonConverter(typeof(ExactReviewIntentJsonConverter))]
public enum ReviewIntent
{
    Accept,
    RequestRefinement,
    PromoteToDelivery,
    Ambiguous
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
}

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

public sealed class ReviewFeedbackDocument
{
    public string Version { get; init; } = string.Empty;

    public ReviewIntent? Intent { get; init; }

    public string CustomerReply { get; init; } = string.Empty;

    public ReviewFeedbackRefinement? Refinement { get; init; }

    public bool ExplicitImplementationAdoption { get; init; }
}

public sealed class ReviewFeedbackRefinement
{
    public string Goal { get; init; } = string.Empty;

    public IReadOnlyList<string>? RequestedChanges { get; init; }
}

public sealed record ParsedReviewFeedback(
    ReviewFeedbackDocument Document,
    string RawJson);

public sealed class ReviewFeedbackContractException(IReadOnlyList<string> errors)
    : InvalidOperationException(
        "Review feedback contract validation failed: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public static class ReviewFeedbackParser
{
    public const string Version = "review-feedback-v1";
    public const string BeginSentinel = "REVIEW_FEEDBACK_V1_BEGIN";
    public const string EndSentinel = "REVIEW_FEEDBACK_V1_END";
    public const int MaximumDocumentCharacters = 65_536;
    public const int MaximumCustomerReplyCharacters = 4_000;
    public const int MaximumGoalCharacters = 4_000;
    public const int MaximumRequestedChanges = 24;
    public const int MaximumRequestedChangeCharacters = 4_000;

    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    public static ParsedReviewFeedback Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Length > MaximumDocumentCharacters)
        {
            throw new ReviewFeedbackContractException(
                [$"output must contain at most {MaximumDocumentCharacters} characters"]);
        }

        var begin = output.IndexOf(BeginSentinel, StringComparison.Ordinal);
        var end = output.IndexOf(EndSentinel, StringComparison.Ordinal);
        if (begin < 0 || end < 0 || end <= begin)
        {
            throw new ReviewFeedbackContractException(
                [$"output must contain exact {BeginSentinel}/{EndSentinel} sentinels"]);
        }
        if (output.IndexOf(
                BeginSentinel,
                begin + BeginSentinel.Length,
                StringComparison.Ordinal) >= 0 ||
            output.IndexOf(
                EndSentinel,
                end + EndSentinel.Length,
                StringComparison.Ordinal) >= 0)
        {
            throw new ReviewFeedbackContractException(
                ["review feedback sentinels must occur exactly once"]);
        }

        return ParseJson(output[(begin + BeginSentinel.Length)..end].Trim());
    }

    public static ParsedReviewFeedback ParseJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ReviewFeedbackContractException(
                ["review feedback JSON is empty"]);
        }
        if (json.Length > MaximumDocumentCharacters)
        {
            throw new ReviewFeedbackContractException(
                [$"review feedback JSON must contain at most {MaximumDocumentCharacters} characters"]);
        }

        ReviewFeedbackDocument document;
        try
        {
            using var jsonDocument = JsonDocument.Parse(json);
            var shapeErrors = ValidateRequiredShape(jsonDocument.RootElement);
            if (shapeErrors.Count > 0)
            {
                throw new ReviewFeedbackContractException(shapeErrors);
            }
            document = JsonSerializer.Deserialize<ReviewFeedbackDocument>(
                           json,
                           JsonOptions)
                       ?? throw new ReviewFeedbackContractException(
                           ["review feedback document is null"]);
        }
        catch (JsonException exception)
        {
            throw new ReviewFeedbackContractException(
                [$"sentinel content is not strict review-feedback JSON: {exception.Message}"]);
        }

        var errors = new List<string>();
        ValidateText(document.Version, 1, 32, "version", errors);
        if (!string.Equals(document.Version, Version, StringComparison.Ordinal))
        {
            errors.Add($"version must be exactly '{Version}'");
        }
        if (document.Intent is null || !Enum.IsDefined(document.Intent.Value))
        {
            errors.Add("intent must be a supported review intent");
        }
        ValidateText(
            document.CustomerReply,
            1,
            MaximumCustomerReplyCharacters,
            "customerReply",
            errors);

        if (document.Intent == ReviewIntent.RequestRefinement)
        {
            ValidateRefinement(document.Refinement, errors);
        }
        else if (document.Refinement is not null)
        {
            errors.Add("refinement must be null unless intent is RequestRefinement");
        }

        if (document.Intent == ReviewIntent.PromoteToDelivery &&
            !document.ExplicitImplementationAdoption)
        {
            errors.Add(
                "explicitImplementationAdoption must be true for PromoteToDelivery");
        }
        if (document.Intent != ReviewIntent.PromoteToDelivery &&
            document.ExplicitImplementationAdoption)
        {
            errors.Add(
                "explicitImplementationAdoption is valid only for PromoteToDelivery");
        }

        if (errors.Count > 0)
        {
            throw new ReviewFeedbackContractException(errors);
        }
        return new ParsedReviewFeedback(document, json);
    }

    public static string Serialize(ReviewFeedbackDocument document) =>
        JsonSerializer.Serialize(document, JsonOptions);

    private static void ValidateRefinement(
        ReviewFeedbackRefinement? refinement,
        ICollection<string> errors)
    {
        if (refinement is null)
        {
            errors.Add("refinement is required for RequestRefinement");
            return;
        }
        ValidateText(
            refinement.Goal,
            1,
            MaximumGoalCharacters,
            "refinement goal",
            errors);
        if (refinement.RequestedChanges is null ||
            refinement.RequestedChanges.Count is
                < 1 or > MaximumRequestedChanges)
        {
            errors.Add(
                $"requestedChanges must contain 1-{MaximumRequestedChanges} entries");
            return;
        }
        foreach (var (change, index) in refinement.RequestedChanges.Select(
                     (change, index) => (change, index)))
        {
            ValidateText(
                change,
                1,
                MaximumRequestedChangeCharacters,
                $"requestedChanges item {index + 1}",
                errors);
        }
    }

    private static void ValidateText(
        string? value,
        int minimum,
        int maximum,
        string label,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length < minimum ||
            value.Length > maximum ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            errors.Add(
                $"{label} must contain {minimum}-{maximum} trimmed characters");
        }
    }

    private static IReadOnlyList<string> ValidateRequiredShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ["review feedback JSON must be an object"];
        }

        var errors = new List<string>();
        RejectDuplicateProperties(root, "review feedback", errors);
        RequireProperties(
            root,
            "review feedback",
            [
                "Version",
                "Intent",
                "CustomerReply",
                "Refinement",
                "ExplicitImplementationAdoption"
            ],
            errors);
        if (root.TryGetProperty("Refinement", out var refinement) &&
            refinement.ValueKind == JsonValueKind.Object)
        {
            RequireProperties(
                refinement,
                "refinement",
                ["Goal", "RequestedChanges"],
                errors);
        }
        return errors;
    }

    private static void RejectDuplicateProperties(
        JsonElement element,
        string path,
        ICollection<string> errors)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    errors.Add($"{path} contains duplicate property '{property.Name}'");
                }
                RejectDuplicateProperties(
                    property.Value,
                    $"{path}.{property.Name}",
                    errors);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{path}[{index++}]", errors);
            }
        }
    }

    private static void RequireProperties(
        JsonElement element,
        string label,
        IReadOnlyCollection<string> names,
        ICollection<string> errors)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out _))
            {
                errors.Add($"{label} is missing required property '{name}'");
            }
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new ExactReviewIntentJsonConverter());
        return options;
    }
}

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

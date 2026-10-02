using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using AiHarnessDemo.Core.Domain;

namespace AiHarnessDemo.Core.Orchestration;

public enum IntakeStatus
{
    NeedsClarification,
    AwaitingConfirmation,
    Confirmed
}

public sealed class IntakeDocument
{
    public IntakeStatus? Status { get; init; }

    public FlowKind? FlowKind { get; init; }

    public string TaskTitle { get; init; } = string.Empty;

    public string CustomerReply { get; init; } = string.Empty;

    public IntakeBrief? Brief { get; init; }
}

public sealed class IntakeBrief
{
    public string Goal { get; init; } = string.Empty;

    public IReadOnlyList<string>? Details { get; init; }

    public IReadOnlyList<string>? SuccessCriteria { get; init; }

    public IReadOnlyList<string>? Constraints { get; init; }

    public IReadOnlyList<string>? Assumptions { get; init; }
}

public sealed record ParsedIntake(
    IntakeDocument Document,
    string RawJson,
    string NormalizedBriefJson);

public sealed class IntakeContractException(IReadOnlyList<string> errors)
    : InvalidOperationException(
        "Intake contract validation failed: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public sealed class IntakeAttemptException(
    Guid flowId,
    FlowStatus flowStatus,
    string retryMessage,
    Exception innerException)
    : InvalidOperationException(retryMessage, innerException)
{
    public Guid FlowId { get; } = flowId;

    public FlowStatus FlowStatus { get; } = flowStatus;

    public string RetryMessage { get; } = retryMessage;
}

public static class IntakeParser
{
    public const string BeginSentinel = "INTAKE_BEGIN";
    public const string EndSentinel = "INTAKE_END";
    public const int MaximumTaskTitleCharacters = 120;
    public const int MaximumCustomerReplyCharacters = 4_000;
    public const int MaximumGoalCharacters = 4_000;
    public const int MaximumListItems = 24;
    public const int MaximumListItemCharacters = 4_000;
    public const int MaximumJsonCharacters =
        6 * (
            MaximumTaskTitleCharacters +
            MaximumCustomerReplyCharacters +
            MaximumGoalCharacters +
            4 * MaximumListItems * MaximumListItemCharacters) +
        483;
    public const int MaximumDocumentCharacters =
        MaximumJsonCharacters + 4_096;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static ParsedIntake Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Length > MaximumDocumentCharacters)
        {
            throw new IntakeContractException(
                [$"output must contain at most {MaximumDocumentCharacters} characters"]);
        }

        var begins = MachineContractSentinels.FindStandalone(
            output,
            BeginSentinel);
        var ends = MachineContractSentinels.FindStandalone(
            output,
            EndSentinel);
        if (begins.Count == 0 || ends.Count == 0 ||
            ends[0] <= begins[0])
        {
            throw new IntakeContractException(
                [$"output must contain exact {BeginSentinel}/{EndSentinel} sentinels"]);
        }
        if (begins.Count != 1 || ends.Count != 1)
        {
            throw new IntakeContractException(
                ["intake sentinels must occur exactly once"]);
        }
        var begin = begins[0];
        var end = ends[0];

        return ParseJson(output[(begin + BeginSentinel.Length)..end].Trim());
    }

    public static ParsedIntake ParseJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new IntakeContractException(["intake JSON is empty"]);
        }
        if (json.Length > MaximumJsonCharacters)
        {
            throw new IntakeContractException(
                [$"intake JSON must contain at most {MaximumJsonCharacters} characters"]);
        }

        IntakeDocument document;
        try
        {
            using var jsonDocument = JsonDocument.Parse(json);
            var shapeErrors = ValidateShape(jsonDocument.RootElement);
            if (shapeErrors.Count > 0)
            {
                throw new IntakeContractException(shapeErrors);
            }
            document = JsonSerializer.Deserialize<IntakeDocument>(
                           json,
                           JsonOptions)
                       ?? throw new IntakeContractException(
                           ["intake document is null"]);
        }
        catch (JsonException exception)
        {
            throw new IntakeContractException(
                [$"sentinel content is not strict intake JSON: {exception.Message}"]);
        }

        var errors = new List<string>();
        if (document.Status is null || !Enum.IsDefined(document.Status.Value))
        {
            errors.Add("Status must be NeedsClarification, AwaitingConfirmation, or Confirmed");
        }
        ValidateText(
            document.TaskTitle,
            MaximumTaskTitleCharacters,
            "TaskTitle",
            allowEmpty: false,
            errors);
        ValidateText(
            document.CustomerReply,
            MaximumCustomerReplyCharacters,
            "CustomerReply",
            allowEmpty: false,
            errors);

        if (document.Status is
                IntakeStatus.AwaitingConfirmation or IntakeStatus.Confirmed &&
            document.FlowKind is null)
        {
            errors.Add("FlowKind is required for AwaitingConfirmation and Confirmed");
        }
        if (document.FlowKind is { } flowKind && !Enum.IsDefined(flowKind))
        {
            errors.Add("FlowKind must be Advisory, Delivery, or null");
        }
        if (document.Brief is null)
        {
            errors.Add("Brief is required");
        }
        else
        {
            ValidateBrief(
                document.Brief,
                requireGoal: document.Status is
                    IntakeStatus.AwaitingConfirmation or IntakeStatus.Confirmed,
                errors);
        }

        if (errors.Count > 0)
        {
            throw new IntakeContractException(errors);
        }

        var normalized = Normalize(document);
        return new ParsedIntake(
            normalized,
            json,
            SerializeBrief(normalized.Brief!));
    }

    public static string Serialize(IntakeDocument document)
    {
        var json = JsonSerializer.Serialize(document, JsonOptions);
        if (json.Length > MaximumJsonCharacters)
        {
            throw new IntakeContractException(
                [$"normalized intake JSON must contain at most {MaximumJsonCharacters} characters"]);
        }
        return json;
    }

    public static string SerializeBrief(IntakeBrief brief) =>
        JsonSerializer.Serialize(brief, JsonOptions);

    public static IntakeBrief ParseBriefJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (string.IsNullOrWhiteSpace(json) ||
            json.Length > MaximumJsonCharacters)
        {
            throw new IntakeContractException(
                ["confirmed brief JSON is empty or oversized"]);
        }

        IntakeBrief brief;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new IntakeContractException(
                    ["confirmed brief JSON must be an object"]);
            }
            var shapeErrors = new List<string>();
            RejectDuplicateProperties(document.RootElement, "Brief", shapeErrors);
            RequireProperties(
                document.RootElement,
                "Brief",
                ["Goal", "Details", "SuccessCriteria", "Constraints", "Assumptions"],
                shapeErrors);
            if (shapeErrors.Count > 0)
            {
                throw new IntakeContractException(shapeErrors);
            }
            brief = JsonSerializer.Deserialize<IntakeBrief>(json, JsonOptions)
                ?? throw new IntakeContractException(
                    ["confirmed brief JSON is null"]);
        }
        catch (JsonException exception)
        {
            throw new IntakeContractException(
                [$"confirmed brief JSON is invalid: {exception.Message}"]);
        }

        var errors = new List<string>();
        ValidateBrief(brief, requireGoal: true, errors);
        if (errors.Count > 0)
        {
            throw new IntakeContractException(errors);
        }
        return NormalizeBrief(brief);
    }

    private static IntakeDocument Normalize(IntakeDocument document) =>
        new()
        {
            Status = document.Status,
            FlowKind = document.FlowKind,
            TaskTitle = NormalizeText(document.TaskTitle),
            CustomerReply = NormalizeText(document.CustomerReply),
            Brief = NormalizeBrief(document.Brief!)
        };

    private static IntakeBrief NormalizeBrief(IntakeBrief brief) =>
        new()
        {
            Goal = NormalizeText(brief.Goal),
            Details = NormalizeList(brief.Details!),
            SuccessCriteria = NormalizeList(brief.SuccessCriteria!),
            Constraints = NormalizeList(brief.Constraints!),
            Assumptions = NormalizeList(brief.Assumptions!)
        };

    private static IReadOnlyList<string> NormalizeList(IEnumerable<string> values) =>
        values.Select(NormalizeText).ToArray();

    private static string NormalizeText(string value) =>
        value.ReplaceLineEndings("\n").Trim();

    private static void ValidateText(
        string? value,
        int maximum,
        string label,
        bool allowEmpty,
        ICollection<string> errors)
    {
        if (value is null)
        {
            errors.Add($"{label} is required");
            return;
        }
        var normalized = NormalizeText(value);
        if ((!allowEmpty && normalized.Length == 0) ||
            normalized.Length > maximum ||
            normalized.Any(character =>
                char.IsControl(character) &&
                character is not '\n' and not '\t'))
        {
            errors.Add(
                $"{label} must contain {(allowEmpty ? "0" : "1")}-{maximum} safe, trimmed characters");
        }
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            errors.Add($"{label} must be trimmed");
        }
    }

    private static void ValidateList(
        IReadOnlyList<string>? values,
        string label,
        ICollection<string> errors)
    {
        if (values is null)
        {
            errors.Add($"{label} is required");
            return;
        }
        if (values.Count > MaximumListItems)
        {
            errors.Add($"{label} must contain at most {MaximumListItems} entries");
            return;
        }
        foreach (var (value, index) in values.Select(
                     (value, index) => (value, index)))
        {
            ValidateText(
                value,
                MaximumListItemCharacters,
                $"{label}[{index}]",
                allowEmpty: false,
                errors);
        }
    }

    private static void ValidateBrief(
        IntakeBrief brief,
        bool requireGoal,
        ICollection<string> errors)
    {
        ValidateText(
            brief.Goal,
            MaximumGoalCharacters,
            "Brief.Goal",
            allowEmpty: !requireGoal,
            errors);
        ValidateList(brief.Details, "Brief.Details", errors);
        ValidateList(brief.SuccessCriteria, "Brief.SuccessCriteria", errors);
        ValidateList(brief.Constraints, "Brief.Constraints", errors);
        ValidateList(brief.Assumptions, "Brief.Assumptions", errors);
    }

    private static IReadOnlyList<string> ValidateShape(JsonElement root)
    {
        var errors = new List<string>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ["intake JSON must be an object"];
        }
        RejectDuplicateProperties(root, "intake", errors);
        RequireProperties(
            root,
            "intake",
            [
                "Status",
                "FlowKind",
                "TaskTitle",
                "CustomerReply",
                "Brief"
            ],
            errors);
        RequireExactEnumValue(
            root,
            "Status",
            ["NeedsClarification", "AwaitingConfirmation", "Confirmed"],
            allowNull: false,
            errors);
        RequireExactEnumValue(
            root,
            "FlowKind",
            ["Advisory", "Delivery"],
            allowNull: true,
            errors);
        if (root.TryGetProperty("Brief", out var brief) &&
            brief.ValueKind == JsonValueKind.Object)
        {
            RequireProperties(
                brief,
                "Brief",
                [
                    "Goal",
                    "Details",
                    "SuccessCriteria",
                    "Constraints",
                    "Assumptions"
                ],
                errors);
        }
        return errors;
    }

    private static void RequireExactEnumValue(
        JsonElement element,
        string propertyName,
        IReadOnlyCollection<string> accepted,
        bool allowNull,
        ICollection<string> errors)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return;
        }
        if (allowNull && value.ValueKind == JsonValueKind.Null)
        {
            return;
        }
        if (value.ValueKind != JsonValueKind.String ||
            !accepted.Contains(value.GetString() ?? string.Empty, StringComparer.Ordinal))
        {
            errors.Add(
                $"{propertyName} must use exact casing: {string.Join(", ", accepted)}" +
                (allowNull ? ", or null" : string.Empty));
        }
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

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }
}

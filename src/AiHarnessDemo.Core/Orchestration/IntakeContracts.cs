using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using AiHarnessDemo.Core.Domain;

namespace AiHarnessDemo.Core.Orchestration;

public enum IntakeV2Status
{
    NeedsClarification,
    AwaitingConfirmation,
    Confirmed
}

public sealed class IntakeV2Document
{
    public string Version { get; init; } = string.Empty;

    public IntakeV2Status? Status { get; init; }

    public FlowKind? FlowKind { get; init; }

    public string TaskTitle { get; init; } = string.Empty;

    public string CustomerReply { get; init; } = string.Empty;

    public IntakeV2Brief? Brief { get; init; }
}

public sealed class IntakeV2Brief
{
    public string Goal { get; init; } = string.Empty;

    public IReadOnlyList<string>? Details { get; init; }

    public IReadOnlyList<string>? SuccessCriteria { get; init; }

    public IReadOnlyList<string>? Constraints { get; init; }

    public IReadOnlyList<string>? Assumptions { get; init; }
}

public sealed record ParsedIntakeV2(
    IntakeV2Document Document,
    string RawJson,
    string NormalizedBriefJson);

public sealed class IntakeV2ContractException(IReadOnlyList<string> errors)
    : InvalidOperationException(
        "Intake v2 contract validation failed: " + string.Join("; ", errors))
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

public static class IntakeV2Parser
{
    public const string Version = "intake-v2";
    public const string BeginSentinel = "INTAKE_V2_BEGIN";
    public const string EndSentinel = "INTAKE_V2_END";
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

    public static ParsedIntakeV2 Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Length > MaximumDocumentCharacters)
        {
            throw new IntakeV2ContractException(
                [$"output must contain at most {MaximumDocumentCharacters} characters"]);
        }

        var begin = output.IndexOf(BeginSentinel, StringComparison.Ordinal);
        var end = output.IndexOf(EndSentinel, StringComparison.Ordinal);
        if (begin < 0 || end < 0 || end <= begin ||
            !IsStandaloneSentinel(output, begin, BeginSentinel) ||
            !IsStandaloneSentinel(output, end, EndSentinel))
        {
            throw new IntakeV2ContractException(
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
            throw new IntakeV2ContractException(
                ["intake v2 sentinels must occur exactly once"]);
        }

        return ParseJson(output[(begin + BeginSentinel.Length)..end].Trim());
    }

    private static bool IsStandaloneSentinel(
        string output,
        int index,
        string sentinel)
    {
        var beforeLine = index == 0 || output[index - 1] is '\r' or '\n';
        var afterIndex = index + sentinel.Length;
        var afterLine = afterIndex == output.Length ||
                        output[afterIndex] is '\r' or '\n';
        return beforeLine && afterLine;
    }

    public static ParsedIntakeV2 ParseJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new IntakeV2ContractException(["intake v2 JSON is empty"]);
        }
        if (json.Length > MaximumJsonCharacters)
        {
            throw new IntakeV2ContractException(
                [$"intake v2 JSON must contain at most {MaximumJsonCharacters} characters"]);
        }

        IntakeV2Document document;
        try
        {
            using var jsonDocument = JsonDocument.Parse(json);
            var shapeErrors = ValidateShape(jsonDocument.RootElement);
            if (shapeErrors.Count > 0)
            {
                throw new IntakeV2ContractException(shapeErrors);
            }
            document = JsonSerializer.Deserialize<IntakeV2Document>(
                           json,
                           JsonOptions)
                       ?? throw new IntakeV2ContractException(
                           ["intake v2 document is null"]);
        }
        catch (JsonException exception)
        {
            throw new IntakeV2ContractException(
                [$"sentinel content is not strict intake-v2 JSON: {exception.Message}"]);
        }

        var errors = new List<string>();
        if (!string.Equals(document.Version, Version, StringComparison.Ordinal))
        {
            errors.Add($"Version must be exactly '{Version}'");
        }
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
                IntakeV2Status.AwaitingConfirmation or IntakeV2Status.Confirmed &&
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
            var requiresBrief = document.Status is
                IntakeV2Status.AwaitingConfirmation or IntakeV2Status.Confirmed;
            ValidateText(
                document.Brief.Goal,
                MaximumGoalCharacters,
                "Brief.Goal",
                allowEmpty: !requiresBrief,
                errors);
            ValidateList(document.Brief.Details, "Brief.Details", errors);
            ValidateList(
                document.Brief.SuccessCriteria,
                "Brief.SuccessCriteria",
                errors);
            ValidateList(
                document.Brief.Constraints,
                "Brief.Constraints",
                errors);
            ValidateList(
                document.Brief.Assumptions,
                "Brief.Assumptions",
                errors);
        }

        if (errors.Count > 0)
        {
            throw new IntakeV2ContractException(errors);
        }

        var normalized = Normalize(document);
        return new ParsedIntakeV2(
            normalized,
            json,
            SerializeBrief(normalized.Brief!));
    }

    public static string Serialize(IntakeV2Document document)
    {
        var json = JsonSerializer.Serialize(document, JsonOptions);
        if (json.Length > MaximumJsonCharacters)
        {
            throw new IntakeV2ContractException(
                [$"normalized intake v2 JSON must contain at most {MaximumJsonCharacters} characters"]);
        }
        return json;
    }

    public static string SerializeBrief(IntakeV2Brief brief) =>
        JsonSerializer.Serialize(brief, JsonOptions);

    private static IntakeV2Document Normalize(IntakeV2Document document) =>
        new()
        {
            Version = document.Version,
            Status = document.Status,
            FlowKind = document.FlowKind,
            TaskTitle = NormalizeText(document.TaskTitle),
            CustomerReply = NormalizeText(document.CustomerReply),
            Brief = new IntakeV2Brief
            {
                Goal = NormalizeText(document.Brief!.Goal),
                Details = NormalizeList(document.Brief.Details!),
                SuccessCriteria = NormalizeList(document.Brief.SuccessCriteria!),
                Constraints = NormalizeList(document.Brief.Constraints!),
                Assumptions = NormalizeList(document.Brief.Assumptions!)
            }
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

    private static IReadOnlyList<string> ValidateShape(JsonElement root)
    {
        var errors = new List<string>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ["intake v2 JSON must be an object"];
        }
        RejectDuplicateProperties(root, "intake", errors);
        RequireProperties(
            root,
            "intake",
            [
                "Version",
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

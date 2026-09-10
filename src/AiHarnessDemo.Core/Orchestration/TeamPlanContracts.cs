using System.Text.Json;
using System.Text.Json.Serialization;
using AiHarnessDemo.Core.Domain;

namespace AiHarnessDemo.Core.Orchestration;

public enum TeamPlanDisposition
{
    Planned,
    MissingQualification
}

public sealed class TeamPlanDocument
{
    public string Version { get; init; } = string.Empty;

    public TeamPlanDisposition? Disposition { get; init; }

    public IReadOnlyList<TeamPlanStep>? Steps { get; init; }

    public IReadOnlyList<string>? PreMortemCheckpoints { get; init; }

    public MissingQualification? MissingQualification { get; init; }
}

public sealed class TeamPlanStep
{
    public string Id { get; init; } = string.Empty;

    public string AgentId { get; init; } = string.Empty;

    public int Order { get; init; }

    public PlanStage Stage { get; init; }

    public string Assignment { get; init; } = string.Empty;

    public string Justification { get; init; } = string.Empty;

    public IReadOnlyList<string>? DependsOn { get; init; }

    public IReadOnlyList<PlanDuty>? Duties { get; init; }

    public bool OutcomeOwner { get; init; }

    public TeamPlanTaskProfile? TaskProfile { get; init; }
}

public sealed class TeamPlanTaskProfile
{
    public int? Complexity { get; init; }

    public int? ReasoningDepth { get; init; }

    public int? ContextDemand { get; init; }

    public int? ToolIntensity { get; init; }

    public IReadOnlyList<TaskTypeTag>? TaskTypeTags { get; init; }

    public TaskRisk? Risk { get; init; }

    public string? RiskReason { get; init; }

    public double? Confidence { get; init; }

    public IReadOnlyList<string>? Rationales { get; init; }

    [JsonIgnore]
    public bool IsEmpty =>
        Complexity is null &&
        ReasoningDepth is null &&
        ContextDemand is null &&
        ToolIntensity is null &&
        TaskTypeTags is null &&
        Risk is null &&
        RiskReason is null &&
        Confidence is null &&
        Rationales is null;
}

public sealed class MissingQualification
{
    public string Summary { get; init; } = string.Empty;

    public IReadOnlyList<string>? Missing { get; init; }

    public string WhyRequired { get; init; } = string.Empty;

    public SuggestedAgent? SuggestedAgent { get; init; }
}

public sealed class SuggestedAgent
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;
}

public sealed record ParsedTeamPlan(
    TeamPlanDocument Document,
    string RawJson);

public sealed class TeamPlanContractException(IReadOnlyList<string> errors)
    : InvalidOperationException(
        "Team plan contract validation failed: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public static class TeamPlanParser
{
    public const string Version = "team-plan-v1";
    public const string BeginSentinel = "TEAM_PLAN_V1_BEGIN";
    public const string EndSentinel = "TEAM_PLAN_V1_END";
    public const int MaximumDocumentCharacters = 262_144;

    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    public static ParsedTeamPlan Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Length > MaximumDocumentCharacters)
        {
            throw new TeamPlanContractException(
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
            throw new TeamPlanContractException(
                [$"output must contain exact {BeginSentinel}/{EndSentinel} sentinels"]);
        }
        if (begins.Count != 1 || ends.Count != 1)
        {
            throw new TeamPlanContractException(
                ["team plan sentinels must occur exactly once"]);
        }
        var begin = begins[0];
        var end = ends[0];

        return ParseJson(output[(begin + BeginSentinel.Length)..end].Trim());
    }

    public static ParsedTeamPlan ParseJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new TeamPlanContractException(["team plan JSON is empty"]);
        }
        if (json.Length > MaximumDocumentCharacters)
        {
            throw new TeamPlanContractException(
                [$"team plan JSON must contain at most {MaximumDocumentCharacters} characters"]);
        }

        try
        {
            using var jsonDocument = JsonDocument.Parse(json);
            var shapeErrors = ValidateRequiredShape(jsonDocument.RootElement);
            if (shapeErrors.Count > 0)
            {
                throw new TeamPlanContractException(shapeErrors);
            }
            var document = JsonSerializer.Deserialize<TeamPlanDocument>(
                               json,
                               JsonOptions)
                           ?? throw new TeamPlanContractException(
                               ["team plan document is null"]);
            return new ParsedTeamPlan(document, json);
        }
        catch (JsonException exception)
        {
            throw new TeamPlanContractException(
                [$"sentinel content is not strict team-plan JSON: {exception.Message}"]);
        }
    }

    public static string Serialize(TeamPlanDocument document) =>
        JsonSerializer.Serialize(document, JsonOptions);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new ExactStringEnumConverter<TeamPlanDisposition>());
        options.Converters.Add(new ExactStringEnumConverter<PlanStage>());
        options.Converters.Add(new ExactStringEnumConverter<PlanDuty>());
        options.Converters.Add(new ExactStringEnumConverter<TaskTypeTag>());
        options.Converters.Add(new ExactStringEnumConverter<TaskRisk>());
        return options;
    }

    private static IReadOnlyList<string> ValidateRequiredShape(JsonElement root)
    {
        var errors = new List<string>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ["team plan JSON must be an object"];
        }
        RejectDuplicateProperties(root, "team plan", errors);
        RequireProperties(
            root,
            "team plan",
            ["Version", "Disposition", "Steps", "PreMortemCheckpoints", "MissingQualification"],
            errors);

        if (root.TryGetProperty("Steps", out var steps) &&
            steps.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var step in steps.EnumerateArray())
            {
                index++;
                if (step.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                RequireProperties(
                    step,
                    $"step {index}",
                    [
                        "Id",
                        "AgentId",
                        "Order",
                        "Stage",
                        "Assignment",
                        "Justification",
                        "DependsOn",
                        "Duties",
                        "OutcomeOwner",
                        "TaskProfile"
                    ],
                    errors);
                if (step.TryGetProperty("TaskProfile", out var profile) &&
                    profile.ValueKind == JsonValueKind.Object &&
                    profile.EnumerateObject().Any())
                {
                    RequireProperties(
                        profile,
                        $"step {index} taskProfile",
                        [
                            "Complexity",
                            "ReasoningDepth",
                            "ContextDemand",
                            "ToolIntensity",
                            "TaskTypeTags",
                            "Risk",
                            "RiskReason",
                            "Confidence",
                            "Rationales"
                        ],
                        errors);
                }
            }
        }

        if (root.TryGetProperty("MissingQualification", out var qualification) &&
            qualification.ValueKind == JsonValueKind.Object)
        {
            RequireProperties(
                qualification,
                "missingQualification",
                ["Summary", "Missing", "WhyRequired", "SuggestedAgent"],
                errors);
            if (qualification.TryGetProperty("SuggestedAgent", out var suggested) &&
                suggested.ValueKind == JsonValueKind.Object)
            {
                RequireProperties(
                    suggested,
                    "suggestedAgent",
                    ["Id", "Name", "Description"],
                    errors);
            }
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
}

public sealed class ExactStringEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"{typeof(TEnum).Name} must be an exact case-sensitive string.");
        }
        var value = reader.GetString();
        if (value is null ||
            !Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed) ||
            !Enum.IsDefined(parsed) ||
            !string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
        {
            throw new JsonException(
                $"'{value}' is not an exact {typeof(TEnum).Name} value.");
        }
        return parsed;
    }

    public override void Write(
        Utf8JsonWriter writer,
        TEnum value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

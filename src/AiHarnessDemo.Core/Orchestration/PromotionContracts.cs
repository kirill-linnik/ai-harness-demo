using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiHarnessDemo.Core.Orchestration;

public sealed class AdvisoryPromotionSeed
{
    public string Version { get; init; } = string.Empty;

    public string Goal { get; init; } = string.Empty;

    public IReadOnlyList<string>? ImplementationDetails { get; init; }
}

public sealed class AdvisoryPromotionSeedContractException(
    IReadOnlyList<string> errors)
    : InvalidOperationException(
        "Advisory promotion seed validation failed: " +
        string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Strict durable contract for the only Advisory data adopted by a promoted Delivery child.
/// </summary>
public static class AdvisoryPromotionSeedParser
{
    public const string Version = "advisory-promotion-seed-v1";
    public const int MaximumSemanticCharacters =
        FlowOutcomeParser.MaximumGoalCharacters +
        FlowOutcomeParser.MaximumImplementationDetails *
        FlowOutcomeParser.MaximumImplementationDetailCharacters;
    private const int MaximumSerializedStructuralCharacters = 148;
    public const int MaximumDocumentCharacters =
        MaximumSemanticCharacters * 6 +
        MaximumSerializedStructuralCharacters;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static AdvisoryPromotionSeed Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (string.IsNullOrWhiteSpace(json) ||
            json.Length > MaximumDocumentCharacters)
        {
            throw new AdvisoryPromotionSeedContractException(
                [$"seed JSON must contain 1-{MaximumDocumentCharacters} characters"]);
        }

        AdvisoryPromotionSeed seed;
        try
        {
            using var document = JsonDocument.Parse(json);
            var shapeErrors = ValidateShape(document.RootElement);
            if (shapeErrors.Count > 0)
            {
                throw new AdvisoryPromotionSeedContractException(shapeErrors);
            }
            seed = JsonSerializer.Deserialize<AdvisoryPromotionSeed>(
                       json,
                       JsonOptions)
                   ?? throw new AdvisoryPromotionSeedContractException(
                       ["seed document is null"]);
        }
        catch (JsonException exception)
        {
            throw new AdvisoryPromotionSeedContractException(
                [$"seed is not strict JSON: {exception.Message}"]);
        }

        var errors = new List<string>();
        if (!string.Equals(seed.Version, Version, StringComparison.Ordinal))
        {
            errors.Add($"Version must be exactly '{Version}'");
        }
        ValidateText(
            seed.Goal,
            FlowOutcomeParser.MaximumGoalCharacters,
            "Goal",
            errors);
        if (seed.ImplementationDetails is null ||
            seed.ImplementationDetails.Count is < 1 or >
                FlowOutcomeParser.MaximumImplementationDetails)
        {
            errors.Add(
                $"ImplementationDetails must contain 1-{FlowOutcomeParser.MaximumImplementationDetails} entries");
        }
        else
        {
            foreach (var (detail, index) in seed.ImplementationDetails.Select(
                         (detail, index) => (detail, index)))
            {
                ValidateText(
                    detail,
                    FlowOutcomeParser.MaximumImplementationDetailCharacters,
                    $"ImplementationDetails item {index + 1}",
                    errors);
            }
        }
        if (errors.Count > 0)
        {
            throw new AdvisoryPromotionSeedContractException(errors);
        }

        return new AdvisoryPromotionSeed
        {
            Version = Version,
            Goal = NormalizeText(seed.Goal),
            ImplementationDetails = seed.ImplementationDetails!
                .Select(NormalizeText)
                .ToArray()
        };
    }

    public static string Serialize(
        string goal,
        IReadOnlyList<string> implementationDetails)
    {
        ArgumentNullException.ThrowIfNull(implementationDetails);
        var json = JsonSerializer.Serialize(
            new AdvisoryPromotionSeed
            {
                Version = Version,
                Goal = NormalizeText(goal),
                ImplementationDetails = implementationDetails
                    .Select(NormalizeText)
                    .ToArray()
            },
            JsonOptions);
        _ = Parse(json);
        return json;
    }

    public static string Canonicalize(string json)
    {
        var parsed = Parse(json);
        return Serialize(
            parsed.Goal,
            parsed.ImplementationDetails ?? []);
    }

    public static string ComputeHash(string canonicalSeedJson)
    {
        var canonical = Canonicalize(canonicalSeedJson);
        return
            "sha256:" +
            Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant();
    }

    public static string NormalizeText(string value) =>
        (value ?? string.Empty).ReplaceLineEndings("\n").Trim();

    private static void ValidateText(
        string? value,
        int maximum,
        string label,
        ICollection<string> errors)
    {
        if (value is null)
        {
            errors.Add($"{label} is required");
            return;
        }
        var normalized = NormalizeText(value);
        if (normalized.Length is 0 ||
            normalized.Length > maximum ||
            normalized.Any(character =>
                char.IsControl(character) &&
                character is not '\n' and not '\t'))
        {
            errors.Add(
                $"{label} must contain 1-{maximum} safe characters");
        }
    }

    private static IReadOnlyList<string> ValidateShape(JsonElement element)
    {
        var errors = new List<string>();
        if (element.ValueKind != JsonValueKind.Object)
        {
            return ["seed must be a JSON object"];
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Version",
            "Goal",
            "ImplementationDetails"
        };
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                errors.Add($"seed contains duplicate property '{property.Name}'");
            }
            else if (!allowed.Contains(property.Name))
            {
                errors.Add($"seed contains unknown property '{property.Name}'");
            }
        }
        foreach (var required in allowed)
        {
            if (!names.Contains(required))
            {
                errors.Add($"seed property '{required}' is required");
            }
        }
        if (element.TryGetProperty("Version", out var version) &&
            version.ValueKind != JsonValueKind.String)
        {
            errors.Add("Version must be a string");
        }
        if (element.TryGetProperty("Goal", out var goal) &&
            goal.ValueKind != JsonValueKind.String)
        {
            errors.Add("Goal must be a string");
        }
        if (element.TryGetProperty(
                "ImplementationDetails",
                out var details))
        {
            if (details.ValueKind != JsonValueKind.Array)
            {
                errors.Add("ImplementationDetails must be an array");
            }
            else if (details.EnumerateArray().Any(
                         item => item.ValueKind != JsonValueKind.String))
            {
                errors.Add(
                    "ImplementationDetails must contain only strings");
            }
        }
        return errors;
    }
}

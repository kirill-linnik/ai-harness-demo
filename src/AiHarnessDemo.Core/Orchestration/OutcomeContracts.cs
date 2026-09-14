using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiHarnessDemo.Core.Orchestration;

public sealed class FlowOutcomeDocument
{
    public string Goal { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public IReadOnlyList<string>? ImplementationDetails { get; init; }

    public IReadOnlyList<FlowOutcomeArtifact>? Artifacts { get; init; }
}

public sealed class FlowOutcomeArtifact
{
    public string Path { get; init; } = string.Empty;

    public string MediaType { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;
}

public sealed record ParsedFlowOutcome(
    FlowOutcomeDocument Document,
    string RawJson,
    int TotalArtifactBytes);

public sealed class FlowOutcomeContractException(IReadOnlyList<string> errors)
    : InvalidOperationException(
        "Flow outcome contract validation failed: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public static class FlowOutcomeParser
{
    public const string BeginSentinel = "FLOW_OUTCOME_BEGIN";
    public const string EndSentinel = "FLOW_OUTCOME_END";
    public const int HardMaximumArtifactCount = 8;
    public const int HardMaximumTotalArtifactBytes = 65_536;
    public const int MaximumDocumentCharacters = 131_072;
    public const int MaximumGoalCharacters = 4_000;
    public const int MaximumImplementationDetails = 24;
    public const int MaximumImplementationDetailCharacters = 4_000;

    private const int MaximumSummaryCharacters = 12_000;
    private const int MaximumArtifactPathCharacters = 260;
    private static readonly HashSet<string> SupportedMediaTypes =
        new(StringComparer.Ordinal)
        {
            "text/plain",
            "text/markdown",
            "text/csv",
            "application/json"
        };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static ParsedFlowOutcome Parse(
        string output,
        int maximumArtifactCount = HardMaximumArtifactCount,
        int maximumTotalArtifactBytes = HardMaximumTotalArtifactBytes)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Length > MaximumDocumentCharacters)
        {
            throw new FlowOutcomeContractException(
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
            throw new FlowOutcomeContractException(
                [$"output must contain exact {BeginSentinel}/{EndSentinel} sentinels"]);
        }
        if (begins.Count != 1 || ends.Count != 1)
        {
            throw new FlowOutcomeContractException(
                ["flow outcome sentinels must occur exactly once"]);
        }
        var begin = begins[0];
        var end = ends[0];
        return ParseJson(
            output[(begin + BeginSentinel.Length)..end].Trim(),
            maximumArtifactCount,
            maximumTotalArtifactBytes);
    }

    public static ParsedFlowOutcome ParseJson(
        string json,
        int maximumArtifactCount = HardMaximumArtifactCount,
        int maximumTotalArtifactBytes = HardMaximumTotalArtifactBytes)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new FlowOutcomeContractException(["flow outcome JSON is empty"]);
        }
        if (json.Length > MaximumDocumentCharacters)
        {
            throw new FlowOutcomeContractException(
                [$"flow outcome JSON must contain at most {MaximumDocumentCharacters} characters"]);
        }
        if (maximumArtifactCount is < 0 or > HardMaximumArtifactCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumArtifactCount),
                $"Artifact count limit must be from 0 through {HardMaximumArtifactCount}.");
        }
        if (maximumTotalArtifactBytes is < 0 or > HardMaximumTotalArtifactBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumTotalArtifactBytes),
                $"Artifact byte limit must be from 0 through {HardMaximumTotalArtifactBytes}.");
        }

        FlowOutcomeDocument document;
        try
        {
            using var jsonDocument = JsonDocument.Parse(json);
            var shapeErrors = ValidateRequiredShape(jsonDocument.RootElement);
            if (shapeErrors.Count > 0)
            {
                throw new FlowOutcomeContractException(shapeErrors);
            }
            document = JsonSerializer.Deserialize<FlowOutcomeDocument>(json, JsonOptions)
                       ?? throw new FlowOutcomeContractException(
                           ["flow outcome document is null"]);
        }
        catch (JsonException exception)
        {
            throw new FlowOutcomeContractException(
                [$"sentinel content is not strict flow-outcome JSON: {exception.Message}"]);
        }

        var errors = new List<string>();
        ValidateText(
            document.Goal,
            1,
            MaximumGoalCharacters,
            "goal",
            errors,
            requireSafeCharacters: true);
        ValidateText(document.Summary, 1, MaximumSummaryCharacters, "summary", errors);
        if (document.ImplementationDetails is null ||
            document.ImplementationDetails.Count is < 1 or > MaximumImplementationDetails)
        {
            errors.Add(
                $"implementationDetails must contain 1-{MaximumImplementationDetails} entries");
        }
        else
        {
            foreach (var (detail, index) in document.ImplementationDetails.Select(
                         (detail, index) => (detail, index)))
            {
                ValidateText(
                    detail,
                    1,
                    MaximumImplementationDetailCharacters,
                    $"implementationDetails item {index + 1}",
                    errors,
                    requireSafeCharacters: true);
            }
        }

        if (document.Artifacts?.Any(artifact => artifact is null) == true)
        {
            errors.Add("artifacts must not contain null entries");
        }
        var artifacts = document.Artifacts?.OfType<FlowOutcomeArtifact>().ToList() ?? [];
        if (document.Artifacts is null)
        {
            errors.Add("artifacts is required");
        }
        if (artifacts.Count > maximumArtifactCount)
        {
            errors.Add($"artifacts must contain at most {maximumArtifactCount} entries");
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalBytes = 0;
        foreach (var (artifact, index) in artifacts.Select(
                     (artifact, index) => (artifact, index)))
        {
            var prefix = $"artifact {index + 1}";
            ValidateArtifactPath(artifact.Path, prefix, errors);
            if (!string.IsNullOrWhiteSpace(artifact.Path))
            {
                var normalizedPath = artifact.Path.Replace('/', '\\');
                if (paths.Any(existing =>
                        string.Equals(
                            existing,
                            normalizedPath,
                            StringComparison.OrdinalIgnoreCase) ||
                        existing.StartsWith(
                            normalizedPath + "\\",
                            StringComparison.OrdinalIgnoreCase) ||
                        normalizedPath.StartsWith(
                            existing + "\\",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add($"{prefix} path collides with another artifact");
                }
                else
                {
                    paths.Add(normalizedPath);
                }
            }
            if (!SupportedMediaTypes.Contains(artifact.MediaType))
            {
                errors.Add($"{prefix} mediaType is not a supported text media type");
            }
            if (artifact.Content is null)
            {
                errors.Add($"{prefix} content is required");
            }
            else
            {
                totalBytes = checked(totalBytes + Encoding.UTF8.GetByteCount(artifact.Content));
            }
        }
        if (totalBytes > maximumTotalArtifactBytes)
        {
            errors.Add(
                $"artifact content must contain at most {maximumTotalArtifactBytes} UTF-8 bytes in total");
        }

        if (errors.Count > 0)
        {
            throw new FlowOutcomeContractException(errors);
        }
        return new ParsedFlowOutcome(document, json, totalBytes);
    }

    private static void ValidateArtifactPath(
        string? path,
        string prefix,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Length > MaximumArtifactPathCharacters ||
            !string.Equals(path, path.Trim(), StringComparison.Ordinal))
        {
            errors.Add(
                $"{prefix} path must contain 1-{MaximumArtifactPathCharacters} trimmed characters");
            return;
        }
        var segments = path.Split(['\\', '/'], StringSplitOptions.None);
        if (Path.IsPathRooted(path) ||
            path.StartsWith('\\') ||
            path.StartsWith('/') ||
            path.Contains(':', StringComparison.Ordinal) ||
            path.Any(char.IsControl) ||
            segments.Any(segment =>
                segment.Length == 0 ||
                segment is "." or ".." ||
                !string.Equals(
                    segment,
                    segment.TrimEnd(' ', '.'),
                    StringComparison.Ordinal) ||
                IsReservedWindowsName(segment)))
        {
            errors.Add($"{prefix} path must be relative and contain no traversal");
        }
    }

    private static bool IsReservedWindowsName(string segment)
    {
        var name = segment.Split('.', 2)[0];
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               Enumerable.Range(1, 9).Any(index =>
                   name.Equals($"COM{index}", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals($"LPT{index}", StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateText(
        string? value,
        int minimum,
        int maximum,
        string label,
        ICollection<string> errors,
        bool requireSafeCharacters = false)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length < minimum ||
            value.Length > maximum ||
            requireSafeCharacters &&
            value.Any(character =>
                char.IsControl(character) &&
                character is not '\r' and not '\n' and not '\t'))
        {
            errors.Add(
                $"{label} must contain {minimum}-{maximum}" +
                (requireSafeCharacters ? " safe" : string.Empty) +
                " characters");
        }
    }

    private static IReadOnlyList<string> ValidateRequiredShape(JsonElement root)
    {
        var errors = new List<string>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ["flow outcome JSON must be an object"];
        }
        RejectDuplicateProperties(root, "flow outcome", errors);
        RequireProperties(
            root,
            "flow outcome",
            ["Goal", "Summary", "ImplementationDetails", "Artifacts"],
            errors);
        if (root.TryGetProperty("Artifacts", out var artifacts) &&
            artifacts.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var artifact in artifacts.EnumerateArray())
            {
                index++;
                if (artifact.ValueKind == JsonValueKind.Object)
                {
                    RequireProperties(
                        artifact,
                        $"artifact {index}",
                        ["Path", "MediaType", "Content"],
                        errors);
                }
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

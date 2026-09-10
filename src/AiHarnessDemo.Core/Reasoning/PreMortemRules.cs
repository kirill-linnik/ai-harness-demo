using System.Text.Json;
using System.Text.Json.Serialization;
using AiHarnessDemo.Core.Orchestration;

namespace AiHarnessDemo.Core.Reasoning;

public sealed class PreMortemValidationException(IReadOnlyList<string> errors)
    : InvalidOperationException(
        "Pre-mortem contract validation failed: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public sealed record PreMortemPlanDocument(
    string Version,
    IReadOnlyList<string> AfterRoles);

public sealed record PreMortemFinding(
    string FailureMode,
    string Evidence,
    string MissedSignal,
    string Prevention);

public sealed record PreMortemFindingsDocument(
    string Version,
    IReadOnlyList<PreMortemFinding> Findings);

public enum PreMortemDisposition
{
    Adjusted,
    Unchanged
}

public sealed record PreMortemReview(
    bool HasFindings,
    IReadOnlyList<PreMortemFinding> Findings);

public static class PreMortemRules
{
    public const string PlanVersion = "pre-mortem-plan-v1";
    public const string PlanBeginSentinel = "PRE_MORTEM_PLAN_V1_BEGIN";
    public const string PlanEndSentinel = "PRE_MORTEM_PLAN_V1_END";
    public const string FindingsVersion = "pre-mortem-findings-v1";
    public const string FindingsBeginSentinel = "PRE_MORTEM_FINDINGS_V1_BEGIN";
    public const string FindingsEndSentinel = "PRE_MORTEM_FINDINGS_V1_END";
    public const string ClearStatus = "PRE_MORTEM_STATUS: CLEAR";
    public const string FindingsStatus = "PRE_MORTEM_STATUS: FINDINGS";
    public const string AdjustedDisposition = "PRE_MORTEM_DISPOSITION: ADJUSTED";
    public const string UnchangedDisposition = "PRE_MORTEM_DISPOSITION: UNCHANGED";
    public const int MaximumFindings = 5;

    private const int MaximumFindingTextLength = 1_200;
    public const int MaximumReviewOutputCharacters = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static IReadOnlySet<string> ParsePlan(
        string output,
        IReadOnlyCollection<string> expectedRoles,
        bool scepticAvailable)
    {
        var document = ParseDocument<PreMortemPlanDocument>(
            output,
            PlanBeginSentinel,
            PlanEndSentinel,
            "pre-mortem plan");
        var errors = new List<string>();
        if (!string.Equals(document.Version, PlanVersion, StringComparison.Ordinal))
        {
            errors.Add($"version must be exactly '{PlanVersion}'");
        }

        if (document.AfterRoles is null)
        {
            errors.Add("afterRoles is required");
        }
        else
        {
            var expected = expectedRoles.ToHashSet(StringComparer.Ordinal);
            if (document.AfterRoles.Count !=
                document.AfterRoles.Distinct(StringComparer.Ordinal).Count())
            {
                errors.Add("afterRoles must not contain duplicates");
            }
            foreach (var role in document.AfterRoles)
            {
                if (!expected.Contains(role))
                {
                    errors.Add(
                        $"afterRoles contains '{role}', which is not an already-planned downstream role");
                }
            }
            if (!scepticAvailable && document.AfterRoles.Count > 0)
            {
                errors.Add(
                    "afterRoles must be empty because the Pre-mortem Sceptic is disabled or its round limit is zero");
            }
        }

        if (errors.Count > 0)
        {
            throw new PreMortemValidationException(errors);
        }

        return (document.AfterRoles ?? [])
            .ToHashSet(StringComparer.Ordinal);
    }

    public static PreMortemReview ParseReview(string output)
    {
        var normalized = output.ReplaceLineEndings("\n");
        if (normalized.Length > MaximumReviewOutputCharacters)
        {
            throw new PreMortemValidationException(
                [$"review output must contain at most {MaximumReviewOutputCharacters} characters"]);
        }
        var statusLines = normalized
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(
                "PRE_MORTEM_STATUS:",
                StringComparison.Ordinal))
            .ToArray();
        var errors = new List<string>();
        if (statusLines.Length != 1 ||
            statusLines[0] is not (ClearStatus or FindingsStatus))
        {
            errors.Add(
                $"output must contain exactly one '{ClearStatus}' or '{FindingsStatus}' marker");
        }
        var firstLine = normalized
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);
        if (firstLine is not (ClearStatus or FindingsStatus))
        {
            errors.Add("the pre-mortem status must be the first non-empty output line");
        }

        var document = ParseDocument<PreMortemFindingsDocument>(
            normalized,
            FindingsBeginSentinel,
            FindingsEndSentinel,
            "pre-mortem findings");
        if (!string.Equals(document.Version, FindingsVersion, StringComparison.Ordinal))
        {
            errors.Add($"version must be exactly '{FindingsVersion}'");
        }
        if (document.Findings is null)
        {
            errors.Add("findings is required");
        }
        else
        {
            if (document.Findings.Count > MaximumFindings)
            {
                errors.Add($"findings must contain at most {MaximumFindings} entries");
            }
            foreach (var (finding, index) in document.Findings.Select(
                         (finding, index) => (finding, index)))
            {
                ValidateFindingText(finding?.FailureMode, index, "failureMode", errors);
                ValidateFindingText(finding?.Evidence, index, "evidence", errors);
                ValidateFindingText(finding?.MissedSignal, index, "missedSignal", errors);
                ValidateFindingText(finding?.Prevention, index, "prevention", errors);
            }
        }

        var hasFindings =
            statusLines.Length == 1 &&
            statusLines[0] == FindingsStatus;
        if (document.Findings is { Count: 0 } && hasFindings)
        {
            errors.Add("FINDINGS status requires at least one finding");
        }
        if (document.Findings is { Count: > 0 } && !hasFindings)
        {
            errors.Add("CLEAR status requires an empty findings array");
        }
        if (errors.Count > 0)
        {
            throw new PreMortemValidationException(errors);
        }

        return new PreMortemReview(hasFindings, document.Findings ?? []);
    }

    public static PreMortemDisposition ParseDisposition(string output)
    {
        var markers = output
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(
                "PRE_MORTEM_DISPOSITION:",
                StringComparison.Ordinal))
            .ToArray();
        if (markers.Length != 1)
        {
            throw new PreMortemValidationException(
                ["output must contain exactly one pre-mortem disposition marker"]);
        }
        return markers[0] switch
        {
            AdjustedDisposition => PreMortemDisposition.Adjusted,
            UnchangedDisposition => PreMortemDisposition.Unchanged,
            _ => throw new PreMortemValidationException(
                [$"disposition must be exactly '{AdjustedDisposition}' or '{UnchangedDisposition}'"])
        };
    }

    private static T ParseDocument<T>(
        string output,
        string beginSentinel,
        string endSentinel,
        string label)
    {
        var begins = MachineContractSentinels.FindStandalone(
            output,
            beginSentinel);
        var ends = MachineContractSentinels.FindStandalone(
            output,
            endSentinel);
        if (begins.Count == 0 || ends.Count == 0 ||
            ends[0] <= begins[0])
        {
            throw new PreMortemValidationException(
                [$"output must contain exact {beginSentinel}/{endSentinel} sentinels"]);
        }
        if (begins.Count != 1 || ends.Count != 1)
        {
            throw new PreMortemValidationException(
                [$"{label} sentinels must occur exactly once"]);
        }
        var begin = begins[0];
        var end = ends[0];

        try
        {
            return JsonSerializer.Deserialize<T>(
                       output[(begin + beginSentinel.Length)..end].Trim(),
                       JsonOptions)
                   ?? throw new PreMortemValidationException(
                       [$"{label} document is null"]);
        }
        catch (JsonException exception)
        {
            throw new PreMortemValidationException(
                [$"sentinel content is not strict {label} JSON: {exception.Message}"]);
        }
    }

    private static void ValidateFindingText(
        string? value,
        int index,
        string property,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumFindingTextLength)
        {
            errors.Add(
                $"finding {index + 1} {property} must contain 1-{MaximumFindingTextLength} characters");
        }
    }
}

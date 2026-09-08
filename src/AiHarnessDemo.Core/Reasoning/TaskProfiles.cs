using System.Text.Json;
using System.Text.Json.Serialization;
using AiHarnessDemo.Core.Domain;

namespace AiHarnessDemo.Core.Reasoning;

public sealed class TaskProfileValidationException(IReadOnlyList<string> errors)
    : InvalidOperationException(
        "Task profile validation failed: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public sealed record TaskProfileInput(
    string Role,
    int Complexity,
    int ReasoningDepth,
    int ContextDemand,
    int ToolIntensity,
    IReadOnlyList<string> TaskTypeTags,
    string Risk,
    string RiskReason,
    double Confidence,
    IReadOnlyList<string> Rationales);

public sealed record TeamTaskProfilesDocument(
    string Version,
    IReadOnlyList<TaskProfileInput> Profiles);

public static class TaskProfileRules
{
    public const string Version = "task-profile-v1";
    public const string BeginSentinel = "TEAM_TASK_PROFILES_V1_BEGIN";
    public const string EndSentinel = "TEAM_TASK_PROFILES_V1_END";
    private const int MaximumReasonLength = 240;
    private const int MaximumRationales = 5;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static TaskProfile Create(
        TaskProfileInput input,
        Guid flowRunId,
        int iteration,
        Guid? flowStepId = null,
        string planStepKey = "",
        string agentId = "")
    {
        var errors = Validate(input);
        if (errors.Count > 0)
        {
            throw new TaskProfileValidationException(errors);
        }

        var tags = input.TaskTypeTags
            .Select(tag => Enum.Parse<TaskTypeTag>(tag, ignoreCase: false))
            .Distinct()
            .ToArray();
        var rationales = input.Rationales.Select(value => value.Trim()).ToArray();
        return new TaskProfile
        {
            FlowRunId = flowRunId,
            Iteration = iteration,
            FlowStepId = flowStepId,
            PlanStepKey = planStepKey,
            AgentId = agentId,
            Role = input.Role.Trim(),
            Complexity = input.Complexity,
            ReasoningDepth = input.ReasoningDepth,
            ContextDemand = input.ContextDemand,
            ToolIntensity = input.ToolIntensity,
            TaskTypeTagsJson = JsonSerializer.Serialize(
                tags.Select(tag => tag.ToString()).ToArray()),
            Risk = Enum.Parse<TaskRisk>(input.Risk, ignoreCase: false),
            RiskReason = input.RiskReason.Trim(),
            Confidence = input.Confidence,
            RationalesJson = JsonSerializer.Serialize(rationales),
            PreMortemAfter = false
        };
    }

    public static IReadOnlyList<string> Validate(TaskProfileInput? input)
    {
        var errors = new List<string>();
        if (input is null)
        {
            return ["profile is null"];
        }

        if (string.IsNullOrWhiteSpace(input.Role) || input.Role.Length > 120)
        {
            errors.Add("role must contain 1-120 characters");
        }
        ValidateMetric(input.Complexity, "complexity", errors);
        ValidateMetric(input.ReasoningDepth, "reasoningDepth", errors);
        ValidateMetric(input.ContextDemand, "contextDemand", errors);
        ValidateMetric(input.ToolIntensity, "toolIntensity", errors);

        if (input.TaskTypeTags is null || input.TaskTypeTags.Count is < 1 or > 6)
        {
            errors.Add("taskTypeTags must contain 1-6 fixed tags");
        }
        else
        {
            foreach (var tag in input.TaskTypeTags)
            {
                if (!Enum.TryParse<TaskTypeTag>(tag, ignoreCase: false, out _))
                {
                    errors.Add($"taskTypeTags contains unsupported tag '{tag}'");
                }
            }
            if (input.TaskTypeTags.Distinct(StringComparer.Ordinal).Count() !=
                input.TaskTypeTags.Count)
            {
                errors.Add("taskTypeTags must not contain duplicates");
            }
        }

        if (!Enum.TryParse<TaskRisk>(input.Risk, ignoreCase: false, out _))
        {
            errors.Add("risk must be Low, Medium, High, or Critical");
        }
        if (string.IsNullOrWhiteSpace(input.RiskReason) ||
            input.RiskReason.Length > MaximumReasonLength)
        {
            errors.Add($"riskReason must contain 1-{MaximumReasonLength} characters");
        }
        if (!double.IsFinite(input.Confidence) || input.Confidence is < 0 or > 1)
        {
            errors.Add("confidence must be between 0 and 1");
        }
        if (input.Rationales is null || input.Rationales.Count is < 1 or > MaximumRationales)
        {
            errors.Add($"rationales must contain 1-{MaximumRationales} entries");
        }
        else if (input.Rationales.Any(value =>
                     string.IsNullOrWhiteSpace(value) ||
                     value.Length > MaximumReasonLength))
        {
            errors.Add(
                $"each rationale must contain 1-{MaximumReasonLength} characters");
        }
        return errors;
    }

    public static IReadOnlyList<TaskProfile> ParseTeamLeadOutput(
        string output,
        IReadOnlyCollection<string> expectedRoles,
        Guid flowRunId,
        int iteration)
    {
        var errors = new List<string>();
        var begin = output.IndexOf(BeginSentinel, StringComparison.Ordinal);
        var end = output.IndexOf(EndSentinel, StringComparison.Ordinal);
        if (begin < 0 || end < 0 || end <= begin)
        {
            throw new TaskProfileValidationException(
                [$"output must contain exact {BeginSentinel}/{EndSentinel} sentinels"]);
        }
        if (output.IndexOf(BeginSentinel, begin + BeginSentinel.Length, StringComparison.Ordinal) >= 0 ||
            output.IndexOf(EndSentinel, end + EndSentinel.Length, StringComparison.Ordinal) >= 0)
        {
            throw new TaskProfileValidationException(["profile sentinels must occur exactly once"]);
        }

        var json = output[
            (begin + BeginSentinel.Length)..end].Trim();
        TeamTaskProfilesDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<TeamTaskProfilesDocument>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new TaskProfileValidationException(
                [$"sentinel content is not strict task-profile JSON: {exception.Message}"]);
        }

        if (document is null)
        {
            throw new TaskProfileValidationException(["task-profile document is null"]);
        }
        if (!string.Equals(document.Version, Version, StringComparison.Ordinal))
        {
            errors.Add($"version must be exactly '{Version}'");
        }
        if (document.Profiles is null)
        {
            errors.Add("profiles is required");
        }
        else
        {
            foreach (var profile in document.Profiles)
            {
                errors.AddRange(Validate(profile).Select(error =>
                    $"{profile?.Role ?? "<missing role>"}: {error}"));
            }

            var actual = document.Profiles
                .Where(profile => profile is not null)
                .Select(profile => profile.Role)
                .ToList();
            var expected = expectedRoles.Order(StringComparer.Ordinal).ToArray();
            if (actual.Count != actual.Distinct(StringComparer.Ordinal).Count())
            {
                errors.Add("profiles must contain each downstream role exactly once");
            }
            if (!actual.Order(StringComparer.Ordinal).SequenceEqual(expected, StringComparer.Ordinal))
            {
                errors.Add(
                    "profiles must contain exactly these downstream roles: " +
                    string.Join(", ", expected));
            }
        }

        if (errors.Count > 0)
        {
            throw new TaskProfileValidationException(errors);
        }

        var validatedProfiles = document.Profiles ??
            throw new TaskProfileValidationException(["profiles is required"]);
        return validatedProfiles
            .Select(profile => Create(profile, flowRunId, iteration))
            .ToList();
    }

    public static IReadOnlyList<TaskTypeTag> ReadTags(TaskProfile profile) =>
        (JsonSerializer.Deserialize<string[]>(profile.TaskTypeTagsJson) ?? [])
        .Select(tag => Enum.Parse<TaskTypeTag>(tag, ignoreCase: false))
        .ToArray();

    public static IReadOnlyList<string> ReadRationales(TaskProfile profile) =>
        JsonSerializer.Deserialize<string[]>(profile.RationalesJson) ?? [];

    public static TaskProfile CopyForStep(TaskProfile source, Guid flowStepId) =>
        new()
        {
            FlowRunId = source.FlowRunId,
            Iteration = source.Iteration,
            FlowStepId = flowStepId,
            PlanStepKey = source.PlanStepKey,
            AgentId = source.AgentId,
            Version = source.Version,
            Role = source.Role,
            Complexity = source.Complexity,
            ReasoningDepth = source.ReasoningDepth,
            ContextDemand = source.ContextDemand,
            ToolIntensity = source.ToolIntensity,
            TaskTypeTagsJson = source.TaskTypeTagsJson,
            Risk = source.Risk,
            RiskReason = source.RiskReason,
            Confidence = source.Confidence,
            RationalesJson = source.RationalesJson,
            PreMortemAfter = source.PreMortemAfter
        };

    public static TaskProfile CreatePreMortem(
        TaskProfile source,
        Guid flowStepId,
        string planStepKey = "") =>
        Create(
            new TaskProfileInput(
                "pre-mortem-sceptic",
                source.Complexity,
                Math.Max(8, source.ReasoningDepth),
                Math.Max(8, source.ContextDemand),
                Math.Max(6, source.ToolIntensity),
                [TaskTypeTag.Quality.ToString(), TaskTypeTag.CrossCutting.ToString()],
                source.Risk.ToString(),
                "Independent evidence-based failure reconstruction for the selected handoff.",
                source.Confidence,
                [
                    "The review must investigate the evaluated result independently.",
                    "Only verifiable failure evidence can trigger a revision."
                ]),
            source.FlowRunId,
            source.Iteration,
            flowStepId,
            planStepKey,
            "pre-mortem-sceptic");

    private static void ValidateMetric(int value, string name, ICollection<string> errors)
    {
        if (value is < 1 or > 10)
        {
            errors.Add($"{name} must be an integer from 1 through 10");
        }
    }
}

public sealed class BootstrapTaskProfileFactory
{
    private static readonly string[] RiskSignals =
    [
        "auth", "security", "permission", "identity", "payment", "secret",
        "pii", "compliance", "migration", "delete", "production"
    ];

    public TaskProfile Create(
        string role,
        string brief,
        Guid flowRunId,
        int iteration,
        Guid? flowStepId = null,
        string planStepKey = "",
        string agentId = "")
    {
        var longBrief = brief.Length > 600;
        var crossCutting = brief.Contains(" and ", StringComparison.OrdinalIgnoreCase) ||
                           brief.Contains("multiple", StringComparison.OrdinalIgnoreCase);
        var riskSignal = RiskSignals.Any(signal =>
            brief.Contains(signal, StringComparison.OrdinalIgnoreCase));
        var (complexity, reasoning, context, tools, tags) = role switch
        {
            "account-manager" => (3, 4, 5, 1, new[] { TaskTypeTag.CustomerDialogue }),
            "team-lead" => (
                longBrief || crossCutting ? 8 : 6,
                longBrief || crossCutting ? 8 : 7,
                longBrief ? 8 : 6,
                2,
                new[] { TaskTypeTag.Planning, TaskTypeTag.CrossCutting }),
            "product-manager" => (5, 6, 8, 1, new[] { TaskTypeTag.Feedback }),
            _ => throw new ArgumentOutOfRangeException(
                nameof(role),
                role,
                "Bootstrap profiles are limited to Account Manager, Team Lead, and Product Manager.")
        };
        var risk = riskSignal
            ? TaskRisk.High
            : crossCutting
                ? TaskRisk.Medium
                : TaskRisk.Low;
        var reason = riskSignal
            ? "The brief contains a recognized trust, data, or irreversible-change signal."
            : crossCutting
                ? "The brief spans more than one delivery concern."
                : "No elevated normalized risk signal was detected.";

        return TaskProfileRules.Create(
            new TaskProfileInput(
                role,
                Math.Clamp(complexity + (longBrief ? 1 : 0), 1, 10),
                reasoning,
                context,
                tools,
                tags.Select(tag => tag.ToString()).ToArray(),
                risk.ToString(),
                reason,
                0.65,
                [
                    $"Role baseline: {role}.",
                    longBrief
                        ? "The brief exceeds the long-context threshold."
                        : "The brief is within the standard context threshold."
                ]),
            flowRunId,
            iteration,
            flowStepId,
            planStepKey,
            agentId);
    }
}

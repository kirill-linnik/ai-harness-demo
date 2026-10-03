using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Orchestration;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AiHarnessDemo.Core.Workflow;

public sealed record WorkflowDefinition(
    WorkflowConfig Config,
    string PromptTemplate,
    string SourcePath,
    DateTimeOffset LoadedAt,
    string Revision);

public sealed class WorkflowConfig
{
    public TrackerConfig Tracker { get; set; } = new();

    public WorkspaceConfig Workspace { get; set; } = new();

    public HookConfig Hooks { get; set; } = new();

    public AgentConfig Agent { get; set; } = new();

    public CopilotConfig Copilot { get; set; } = new();

    public StudioConfig Studio { get; set; } = new();
}

public sealed class StudioConfig
{
    public StudioPlanningConfig Planning { get; set; } = new();

    public StudioFlowKindsConfig FlowKinds { get; set; } = new();

    public StudioAdvisoryConfig Advisory { get; set; } = new();

    public StudioPermissionsConfig Permissions { get; set; } = new();
}

public sealed class StudioPlanningConfig
{
    public int MaxSteps { get; set; } = 24;

    public int MaxDependenciesPerStep { get; set; } = 8;

    public int MaxAssignmentCharacters { get; set; } = 4_000;
}

public sealed class StudioFlowKindsConfig
{
    public StudioAdvisoryFlowConfig Advisory { get; set; } = new();

    public StudioDeliveryFlowConfig Delivery { get; set; } = new();
}

public sealed class StudioAdvisoryFlowConfig
{
    public List<PlanDuty> RequiredDuties { get; set; } = [PlanDuty.PrepareOutcome];

    public ExecutionPermissionProfile MaximumPermission { get; set; } =
        ExecutionPermissionProfile.ReadOnlySource;
}

public sealed class StudioDeliveryFlowConfig
{
    public List<PlanDuty> RequiredDuties { get; set; } =
    [
        PlanDuty.Implement,
        PlanDuty.Verify,
        PlanDuty.PrepareOutcome,
        PlanDuty.Publish
    ];

    public ExecutionPermissionProfile PreReviewMaximumPermission { get; set; } =
        ExecutionPermissionProfile.WorkspaceWrite;

    public ExecutionPermissionProfile PostApprovalMaximumPermission { get; set; } =
        ExecutionPermissionProfile.Publish;

    /// <summary>
    /// How many consecutive host-scheduled refinement iterations a Delivery flow may take on its
    /// own after a <c>NeedsRefinement</c> assessment before the harness stops and asks the
    /// customer. Zero restores the previous always-ask behavior.
    /// </summary>
    public int MaxAutoRefinementIterations { get; set; } = 3;
}

public sealed class StudioAdvisoryConfig
{
    public string ArtifactDirectory { get; set; } = @".studio\advisory";

    public int MaxArtifactCount { get; set; } = 8;

    public int MaxTotalArtifactBytes { get; set; } = 65_536;
}

public sealed class StudioPermissionsConfig
{
    public StudioPermissionRestrictionConfig ReadOnlySource { get; set; } = new();

    public StudioPermissionRestrictionConfig WorkspaceWrite { get; set; } = new();

    public StudioPermissionRestrictionConfig Publish { get; set; } = new();

    public StudioPermissionRestrictionConfig PreMortemReadOnly { get; set; } = new();
}

public sealed class StudioPermissionRestrictionConfig
{
    public List<string> AdditionalDeniedTools { get; set; } = [];
}

public sealed class TrackerConfig
{
    public string Kind { get; set; } = "voice";

    public List<string> ActiveStates { get; set; } = ["Intake", "Queued", "Running"];

    public List<string> TerminalStates { get; set; } = ["Approved", "Failed"];
}

public sealed class WorkspaceConfig
{
    public string Root { get; set; } = "data/worktrees";

    [YamlIgnore]
    public string ResolvedRoot { get; set; } = string.Empty;
}

public sealed class HookConfig
{
    public string? AfterCreate { get; set; }

    public string? BeforeRun { get; set; }

    public string? AfterRun { get; set; }

    public string? BeforeRemove { get; set; }

    public int TimeoutMs { get; set; } = 60_000;
}

public sealed class AgentConfig
{
    public int MaxConcurrentAgents { get; set; } = 4;

    public int MaxTurns { get; set; } = 20;

    public int MaxAttempts { get; set; } = 3;

    public int RetryBaseDelayMs { get; set; } = 300;

    public int MaxRetryBackoffMs { get; set; } = 3_000;
}

public sealed class CopilotConfig
{
    public string Command { get; set; } = "copilot";

    public int InactivityTimeoutMs { get; set; } = 300_000;

    public int SilentToolTimeoutMs { get; set; } = 1_800_000;

    public int SoftWarningMs { get; set; } = 3_600_000;

    public int ExecutionBudgetMs { get; set; } = 14_400_000;
}

public sealed class WorkflowConfigurationException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Studio WORKFLOW.md loader: Markdown prompt body, typed defaults, strict Copilot and Studio keys,
/// environment/path resolution, and fail-closed validation.
/// </summary>
public sealed class WorkflowLoader
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public WorkflowDefinition Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new WorkflowConfigurationException(
                $"Symphony workflow file was not found: {fullPath}");
        }

        string text;
        try
        {
            text = File.ReadAllText(fullPath).ReplaceLineEndings("\n");
        }
        catch (IOException exception)
        {
            throw new WorkflowConfigurationException(
                $"Symphony workflow file could not be read: {fullPath}",
                exception);
        }

        var (frontMatter, prompt) = Split(text);
        ValidateStudioShape(frontMatter);
        WorkflowConfig config;
        try
        {
            config = string.IsNullOrWhiteSpace(frontMatter)
                ? new WorkflowConfig()
                : _deserializer.Deserialize<WorkflowConfig>(frontMatter)
                  ?? throw new WorkflowConfigurationException(
                      "Symphony workflow front matter must be a YAML mapping, not null.");
        }
        catch (YamlException exception)
        {
            throw new WorkflowConfigurationException(
                $"Symphony workflow front matter is invalid: {exception.Message}",
                exception);
        }

        ValidateRequiredSections(config);
        var workflowDirectory = Path.GetDirectoryName(fullPath)
            ?? throw new WorkflowConfigurationException(
                $"Symphony workflow path has no parent directory: {fullPath}");
        config.Workspace.ResolvedRoot = ResolvePath(config.Workspace.Root, workflowDirectory);
        Validate(config, prompt);
        var revision = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        return new WorkflowDefinition(
            config,
            prompt.Trim(),
            fullPath,
            DateTimeOffset.UtcNow,
            revision);
    }

    private static void ValidateStudioShape(string frontMatter)
    {
        if (string.IsNullOrWhiteSpace(frontMatter))
        {
            return;
        }

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(frontMatter));
        }
        catch (YamlException exception)
        {
            throw new WorkflowConfigurationException(
                $"Symphony workflow front matter is invalid: {exception.Message}",
                exception);
        }

        if (stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new WorkflowConfigurationException(
                "Symphony workflow front matter must be a YAML mapping.");
        }
        if (TryGet(root, "copilot", out var copilotNode))
        {
            var copilot = RequireMapping(copilotNode, "copilot");
            foreach (var legacyKey in new[]
                     {
                         "turn_timeout_ms", "stall_timeout_ms",
                         "maximum_quality_stall_timeout_ms"
                     })
            {
                if (TryGet(copilot, legacyKey, out _))
                {
                    throw new WorkflowConfigurationException(
                        $"copilot.{legacyKey} is a legacy timeout setting. Explicitly migrate to " +
                        "inactivity_timeout_ms, silent_tool_timeout_ms, soft_warning_ms and " +
                        "execution_budget_ms; turn_timeout_ms is not reinterpreted. " +
                        "See docs\\ARCHITECTURE.md.");
                }
            }
            RejectUnknown(copilot, "copilot", "command", "inactivity_timeout_ms",
                "silent_tool_timeout_ms", "soft_warning_ms", "execution_budget_ms");
        }
        if (!TryGet(root, "studio", out var studioNode))
        {
            return;
        }
        var studio = RequireMapping(studioNode, "studio");
        RejectUnknown(studio, "studio", "planning", "flow_kinds", "advisory", "permissions");

        ValidateMapping(studio, "planning", "studio.planning",
            "max_steps", "max_dependencies_per_step", "max_assignment_characters");
        if (TryGet(studio, "flow_kinds", out var flowKindsNode))
        {
            var flowKinds = RequireMapping(flowKindsNode, "studio.flow_kinds");
            RejectUnknown(flowKinds, "studio.flow_kinds", "advisory", "delivery");
            ValidateMapping(flowKinds, "advisory", "studio.flow_kinds.advisory",
                "required_duties", "maximum_permission");
            ValidateMapping(flowKinds, "delivery", "studio.flow_kinds.delivery",
                "required_duties", "pre_review_maximum_permission",
                "post_approval_maximum_permission",
                "max_auto_refinement_iterations");
        }
        ValidateMapping(studio, "advisory", "studio.advisory",
            "artifact_directory", "max_artifact_count", "max_total_artifact_bytes");
        if (TryGet(studio, "permissions", out var permissionsNode))
        {
            var permissions = RequireMapping(permissionsNode, "studio.permissions");
            RejectUnknown(permissions, "studio.permissions",
                "read_only_source", "workspace_write", "publish", "pre_mortem_read_only");
            foreach (var key in new[]
                     {
                         "read_only_source", "workspace_write", "publish",
                         "pre_mortem_read_only"
                     })
            {
                ValidateMapping(
                    permissions,
                    key,
                    $"studio.permissions.{key}",
                    "additional_denied_tools");
            }
        }
    }

    private static void ValidateMapping(
        YamlMappingNode parent,
        string key,
        string path,
        params string[] allowed)
    {
        if (TryGet(parent, key, out var node))
        {
            RejectUnknown(RequireMapping(node, path), path, allowed);
        }
    }

    private static YamlMappingNode RequireMapping(YamlNode node, string path) =>
        node as YamlMappingNode
        ?? throw new WorkflowConfigurationException(
            $"{path} must be a YAML mapping and cannot be null.");

    private static void RejectUnknown(
        YamlMappingNode mapping,
        string path,
        params string[] allowed)
    {
        var accepted = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var keyNode in mapping.Children.Keys)
        {
            var key = (keyNode as YamlScalarNode)?.Value ?? string.Empty;
            if (!accepted.Contains(key))
            {
                throw new WorkflowConfigurationException(
                    $"Unknown WORKFLOW.md field '{path}.{key}'.");
            }
        }
    }

    private static bool TryGet(
        YamlMappingNode mapping,
        string key,
        out YamlNode value)
    {
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                value = pair.Value;
                return true;
            }
        }
        value = null!;
        return false;
    }

    private static (string FrontMatter, string Prompt) Split(string text)
    {
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
        {
            return (string.Empty, text.Trim());
        }

        var end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new WorkflowConfigurationException(
                "Symphony workflow front matter has no closing '---' delimiter.");
        }

        return (text[4..end], text[(end + 5)..].Trim());
    }

    private static string ResolvePath(string value, string workflowDirectory)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new WorkflowConfigurationException("workspace.root cannot be empty.");
        }

        var expanded = value.Trim();
        if (expanded == "~")
        {
            expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        else if (expanded.StartsWith("~/", StringComparison.Ordinal) ||
                 expanded.StartsWith("~\\", StringComparison.Ordinal))
        {
            expanded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                expanded[2..]);
        }
        else if (expanded.StartsWith('$') &&
                 expanded.IndexOfAny(['\\', '/']) < 0)
        {
            var environmentName = expanded[1..];
            expanded = Environment.GetEnvironmentVariable(environmentName)
                ?? throw new WorkflowConfigurationException(
                    $"workspace.root references missing environment variable ${environmentName}.");
        }

        return Path.GetFullPath(
            Path.IsPathRooted(expanded)
                ? expanded
                : Path.Combine(workflowDirectory, expanded));
    }

    private static void ValidateRequiredSections(WorkflowConfig config)
    {
        if (config.Tracker is null)
        {
            throw new WorkflowConfigurationException(
                "tracker must be a YAML mapping and cannot be null.");
        }
        if (config.Workspace is null)
        {
            throw new WorkflowConfigurationException(
                "workspace must be a YAML mapping and cannot be null.");
        }
        if (config.Hooks is null)
        {
            throw new WorkflowConfigurationException(
                "hooks must be a YAML mapping and cannot be null.");
        }
        if (config.Agent is null)
        {
            throw new WorkflowConfigurationException(
                "agent must be a YAML mapping and cannot be null.");
        }
        if (config.Copilot is null)
        {
            throw new WorkflowConfigurationException(
                "copilot must be a YAML mapping and cannot be null.");
        }
        if (config.Studio is null ||
            config.Studio.Planning is null ||
            config.Studio.FlowKinds is null ||
            config.Studio.FlowKinds.Advisory is null ||
            config.Studio.FlowKinds.Delivery is null ||
            config.Studio.Advisory is null ||
            config.Studio.Permissions is null ||
            config.Studio.Permissions.ReadOnlySource is null ||
            config.Studio.Permissions.WorkspaceWrite is null ||
            config.Studio.Permissions.Publish is null ||
            config.Studio.Permissions.PreMortemReadOnly is null)
        {
            throw new WorkflowConfigurationException(
                "studio and all of its declared sections must be YAML mappings and cannot be null.");
        }
        if (config.Tracker.ActiveStates is null)
        {
            throw new WorkflowConfigurationException(
                "tracker.active_states must be a YAML sequence and cannot be null.");
        }
        if (config.Tracker.TerminalStates is null)
        {
            throw new WorkflowConfigurationException(
                "tracker.terminal_states must be a YAML sequence and cannot be null.");
        }
    }

    private static void Validate(WorkflowConfig config, string prompt)
    {
        if (string.IsNullOrWhiteSpace(config.Tracker.Kind))
        {
            throw new WorkflowConfigurationException("tracker.kind is required.");
        }
        if (config.Agent.MaxConcurrentAgents <= 0)
        {
            throw new WorkflowConfigurationException("agent.max_concurrent_agents must be positive.");
        }
        if (config.Agent.MaxTurns <= 0)
        {
            throw new WorkflowConfigurationException("agent.max_turns must be positive.");
        }
        if (config.Agent.MaxAttempts <= 0)
        {
            throw new WorkflowConfigurationException("agent.max_attempts must be positive.");
        }
        if (config.Agent.RetryBaseDelayMs < 0 ||
            config.Agent.MaxRetryBackoffMs < config.Agent.RetryBaseDelayMs)
        {
            throw new WorkflowConfigurationException(
                "Agent retry delays must be non-negative and max_retry_backoff_ms cannot be smaller than retry_base_delay_ms.");
        }
        if (config.Hooks.TimeoutMs <= 0)
        {
            throw new WorkflowConfigurationException("hooks.timeout_ms must be positive.");
        }
        if (string.IsNullOrWhiteSpace(config.Copilot.Command))
        {
            throw new WorkflowConfigurationException("copilot.command is required.");
        }
        if (config.Copilot.InactivityTimeoutMs <= 0 ||
            config.Copilot.SilentToolTimeoutMs < config.Copilot.InactivityTimeoutMs ||
            config.Copilot.SoftWarningMs <= 0 ||
            config.Copilot.ExecutionBudgetMs <= config.Copilot.SoftWarningMs ||
            config.Copilot.SilentToolTimeoutMs > config.Copilot.ExecutionBudgetMs)
        {
            throw new WorkflowConfigurationException(
                "Copilot timeouts must be positive: inactivity_timeout_ms <= silent_tool_timeout_ms " +
                "<= execution_budget_ms, and soft_warning_ms < execution_budget_ms.");
        }
        ValidateStudio(config.Studio);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new WorkflowConfigurationException(
                "WORKFLOW.md must contain a non-empty prompt template.");
        }
    }

    private static void ValidateStudio(StudioConfig studio)
    {
        if (studio.Planning.MaxSteps is < 1 or > 24)
        {
            throw new WorkflowConfigurationException(
                "studio.planning.max_steps must be from 1 through 24.");
        }
        if (studio.Planning.MaxDependenciesPerStep is < 0 or > 8)
        {
            throw new WorkflowConfigurationException(
                "studio.planning.max_dependencies_per_step must be from 0 through 8.");
        }
        if (studio.Planning.MaxAssignmentCharacters is < 1 or > 4_000)
        {
            throw new WorkflowConfigurationException(
                "studio.planning.max_assignment_characters must be from 1 through 4000.");
        }
        RequireMinimumDuties(
            studio.FlowKinds.Advisory.RequiredDuties,
            [PlanDuty.PrepareOutcome],
            "studio.flow_kinds.advisory.required_duties");
        RequireMinimumDuties(
            studio.FlowKinds.Delivery.RequiredDuties,
            [PlanDuty.Implement, PlanDuty.Verify, PlanDuty.PrepareOutcome, PlanDuty.Publish],
            "studio.flow_kinds.delivery.required_duties");
        if (studio.FlowKinds.Advisory.MaximumPermission !=
            ExecutionPermissionProfile.ReadOnlySource)
        {
            throw new WorkflowConfigurationException(
                "studio.flow_kinds.advisory.maximum_permission cannot exceed ReadOnlySource.");
        }
        if (studio.FlowKinds.Delivery.PreReviewMaximumPermission ==
                ExecutionPermissionProfile.PreMortemReadOnly ||
            PermissionRank(studio.FlowKinds.Delivery.PreReviewMaximumPermission) >
                PermissionRank(ExecutionPermissionProfile.WorkspaceWrite))
        {
            throw new WorkflowConfigurationException(
                "studio.flow_kinds.delivery.pre_review_maximum_permission cannot exceed WorkspaceWrite.");
        }
        if (studio.FlowKinds.Delivery.PostApprovalMaximumPermission ==
                ExecutionPermissionProfile.PreMortemReadOnly ||
            PermissionRank(studio.FlowKinds.Delivery.PostApprovalMaximumPermission) >
                PermissionRank(ExecutionPermissionProfile.Publish))
        {
            throw new WorkflowConfigurationException(
                "studio.flow_kinds.delivery.post_approval_maximum_permission cannot exceed Publish.");
        }
        if (studio.FlowKinds.Delivery.MaxAutoRefinementIterations is < 0 or > 10)
        {
            throw new WorkflowConfigurationException(
                "studio.flow_kinds.delivery.max_auto_refinement_iterations must be from 0 through 10.");
        }
        var advisoryArtifactSegments = studio.Advisory.ArtifactDirectory?
            .Split(['\\', '/'], StringSplitOptions.None) ?? [];
        if (string.IsNullOrWhiteSpace(studio.Advisory.ArtifactDirectory) ||
            Path.IsPathRooted(studio.Advisory.ArtifactDirectory) ||
            studio.Advisory.ArtifactDirectory.Contains(':', StringComparison.Ordinal) ||
            advisoryArtifactSegments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." ||
                segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".studio-host", StringComparison.OrdinalIgnoreCase)))
        {
            throw new WorkflowConfigurationException(
                "studio.advisory.artifact_directory must be a relative path without traversal.");
        }
        if (studio.Advisory.MaxArtifactCount is < 1 or >
            FlowOutcomeParser.HardMaximumArtifactCount)
        {
            throw new WorkflowConfigurationException(
                $"studio.advisory.max_artifact_count must be from 1 through {FlowOutcomeParser.HardMaximumArtifactCount}.");
        }
        if (studio.Advisory.MaxTotalArtifactBytes is < 1 or >
            FlowOutcomeParser.HardMaximumTotalArtifactBytes)
        {
            throw new WorkflowConfigurationException(
                $"studio.advisory.max_total_artifact_bytes must be from 1 through {FlowOutcomeParser.HardMaximumTotalArtifactBytes}.");
        }

        foreach (var (path, restriction) in new[]
                 {
                     ("read_only_source", studio.Permissions.ReadOnlySource),
                     ("workspace_write", studio.Permissions.WorkspaceWrite),
                     ("publish", studio.Permissions.Publish),
                     ("pre_mortem_read_only", studio.Permissions.PreMortemReadOnly)
                 })
        {
            if (restriction.AdditionalDeniedTools is null ||
                restriction.AdditionalDeniedTools.Any(string.IsNullOrWhiteSpace))
            {
                throw new WorkflowConfigurationException(
                    $"studio.permissions.{path}.additional_denied_tools must be a sequence of non-empty tool names.");
            }
        }
    }

    private static void RequireMinimumDuties(
        IReadOnlyCollection<PlanDuty>? configured,
        IReadOnlyCollection<PlanDuty> required,
        string path)
    {
        if (configured is null)
        {
            throw new WorkflowConfigurationException($"{path} must be a YAML sequence.");
        }
        var missing = required.Except(configured).ToList();
        if (missing.Count > 0)
        {
            throw new WorkflowConfigurationException(
                $"{path} cannot remove code-required duties: {string.Join(", ", missing)}.");
        }
    }

    private static int PermissionRank(ExecutionPermissionProfile profile) => profile switch
    {
        ExecutionPermissionProfile.ReadOnlySource => 0,
        ExecutionPermissionProfile.PreMortemReadOnly => 0,
        ExecutionPermissionProfile.WorkspaceWrite => 1,
        ExecutionPermissionProfile.Publish => 2,
        _ => int.MaxValue
    };
}

public sealed partial class WorkflowPromptRenderer
{
    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.]+)\s*\}\}")]
    private static partial Regex VariablePattern();

    public string Render(
        string template,
        IReadOnlyDictionary<string, string> values)
    {
        var templateWithoutVariables = VariablePattern().Replace(template, string.Empty);
        if (templateWithoutVariables.Contains("{{", StringComparison.Ordinal) ||
            templateWithoutVariables.Contains("}}", StringComparison.Ordinal))
        {
            throw new WorkflowConfigurationException(
                "Workflow template contains an invalid or unsupported expression.");
        }

        var rendered = VariablePattern().Replace(
            template,
            match =>
            {
                var key = match.Groups[1].Value;
                return values.TryGetValue(key, out var value)
                    ? value
                    : throw new WorkflowConfigurationException(
                        $"Unknown workflow template variable '{{{{ {key} }}}}'.");
            });

        return rendered.Trim();
    }
}

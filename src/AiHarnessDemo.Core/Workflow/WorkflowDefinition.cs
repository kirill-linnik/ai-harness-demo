using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AiHarnessDemo.Core.Workflow;

public sealed record WorkflowDefinition(
    WorkflowConfig Config,
    string PromptTemplate,
    string SourcePath,
    DateTimeOffset LoadedAt);

public sealed class WorkflowConfig
{
    public TrackerConfig Tracker { get; set; } = new();

    public WorkspaceConfig Workspace { get; set; } = new();

    public HookConfig Hooks { get; set; } = new();

    public AgentConfig Agent { get; set; } = new();

    public CopilotConfig Copilot { get; set; } = new();
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

    public int TurnTimeoutMs { get; set; } = 1_200_000;

    public int StallTimeoutMs { get; set; } = 300_000;
}

public sealed class WorkflowConfigurationException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Symphony-compatible WORKFLOW.md loader: Markdown prompt body, YAML front matter, typed defaults,
/// environment/path resolution, forward-compatible unknown keys, and fail-closed validation.
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
        WorkflowConfig config;
        try
        {
            config = string.IsNullOrWhiteSpace(frontMatter)
                ? new WorkflowConfig()
                : _deserializer.Deserialize<WorkflowConfig>(frontMatter) ?? new WorkflowConfig();
        }
        catch (YamlException exception)
        {
            throw new WorkflowConfigurationException(
                $"Symphony workflow front matter is invalid: {exception.Message}",
                exception);
        }

        var workflowDirectory = Path.GetDirectoryName(fullPath)
            ?? throw new WorkflowConfigurationException(
                $"Symphony workflow path has no parent directory: {fullPath}");
        config.Workspace.ResolvedRoot = ResolvePath(config.Workspace.Root, workflowDirectory);
        Validate(config, prompt);
        return new WorkflowDefinition(config, prompt.Trim(), fullPath, DateTimeOffset.UtcNow);
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
        if (config.Copilot.TurnTimeoutMs <= 0)
        {
            throw new WorkflowConfigurationException("copilot.turn_timeout_ms must be positive.");
        }
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new WorkflowConfigurationException(
                "WORKFLOW.md must contain a non-empty prompt template.");
        }
    }
}

public sealed partial class WorkflowPromptRenderer
{
    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.]+)\s*\}\}")]
    private static partial Regex VariablePattern();

    public string Render(
        string template,
        IReadOnlyDictionary<string, string> values)
    {
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

        if (rendered.Contains("{{", StringComparison.Ordinal) ||
            rendered.Contains("}}", StringComparison.Ordinal))
        {
            throw new WorkflowConfigurationException(
                "Workflow template contains an invalid or unsupported expression.");
        }

        return rendered.Trim();
    }
}

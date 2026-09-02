using AiHarnessDemo.Core.Domain;

namespace AiHarnessDemo.Core.Orchestration;

public sealed record PlannedAgent(AgentRecord Agent, string Reason);

public sealed class FlowPlanner
{
    private static readonly string[] ArchitectureSignals =
        ["architecture", "platform", "migration", "refactor", "integration", "multi-service", "redesign"];

    private static readonly string[] DesignSignals =
        ["ui", "ux", "screen", "page", "design", "customer journey", "accessibility", "dashboard"];

    private static readonly string[] DataSignals =
        ["database", "schema", "sql", "analytics", "data pipeline", "reporting", "migration"];

    private static readonly string[] SecuritySignals =
        ["auth", "security", "secure", "permission", "role-based", "access control", "identity", "payment", "secret", "pii", "compliance"];

    private static readonly string[] DocumentationSignals =
        ["api", "sdk", "documentation", "onboarding", "readme", "public contract"];

    public IReadOnlyList<PlannedAgent> Plan(
        string request,
        IReadOnlyCollection<AgentRecord> availableAgents)
    {
        var enabled = availableAgents
            .Where(agent => agent.Enabled)
            .ToDictionary(agent => agent.Role, StringComparer.OrdinalIgnoreCase);
        var result = new List<PlannedAgent>();
        var complexity = CalculateComplexity(request);

        AddIfEnabled(result, enabled, "team-lead", "Own the delivery sequence and handoff contracts.");

        if (complexity >= 3 || ContainsAny(request, ArchitectureSignals))
        {
            AddIfEnabled(result, enabled, "architect", "The change crosses boundaries or needs an explicit technical shape.");
        }

        if (ContainsAny(request, DesignSignals))
        {
            AddIfEnabled(result, enabled, "product-designer", "The request changes a customer-facing experience.");
        }

        if (ContainsAny(request, DataSignals))
        {
            AddIfEnabled(result, enabled, "data-engineer", "The request changes persisted data or analytical flows.");
        }

        AddIfEnabled(result, enabled, "software-engineer", "Implementation is required in the selected repository.");

        if (ContainsAny(request, SecuritySignals))
        {
            AddIfEnabled(result, enabled, "security-engineer", "The task carries an identity, data, or trust-boundary risk.");
        }

        AddIfEnabled(result, enabled, "quality-engineer", "Independent evidence is required before release.");

        if (ContainsAny(request, DocumentationSignals))
        {
            AddIfEnabled(result, enabled, "technical-writer", "Public or developer-facing behavior needs a clear contract.");
        }

        AddIfEnabled(result, enabled, "release-engineer", "Package the verified outcome as the configured delivery artifact.");

        if (!result.Any(item => item.Agent.Role == "software-engineer"))
        {
            throw new InvalidOperationException(
                "No enabled software-engineer agent is available for implementation.");
        }

        return result;
    }

    public int CalculateComplexity(string request)
    {
        var score = 1;
        if (request.Length > 220)
        {
            score++;
        }
        if (request.Length > 600)
        {
            score++;
        }
        if (ContainsAny(request, ArchitectureSignals))
        {
            score++;
        }
        if (ContainsAny(request, SecuritySignals) || ContainsAny(request, DataSignals))
        {
            score++;
        }
        if (request.Contains(" and ", StringComparison.OrdinalIgnoreCase) ||
            request.Contains("multiple", StringComparison.OrdinalIgnoreCase))
        {
            score++;
        }

        return Math.Clamp(score, 1, 5);
    }

    private static bool ContainsAny(string value, IEnumerable<string> signals) =>
        signals.Any(signal => value.Contains(signal, StringComparison.OrdinalIgnoreCase));

    private static void AddIfEnabled(
        ICollection<PlannedAgent> result,
        IReadOnlyDictionary<string, AgentRecord> enabled,
        string role,
        string reason)
    {
        if (enabled.TryGetValue(role, out var agent))
        {
            result.Add(new PlannedAgent(agent, reason));
        }
    }
}

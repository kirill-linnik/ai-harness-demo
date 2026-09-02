namespace AiHarnessDemo.Core.Reasoning;

public sealed record ModelChoice(string Model, string Reason);

public sealed class ModelSelector
{
    public ModelChoice Select(string role, int complexity, double historicalFailureRate = 0)
    {
        if (historicalFailureRate >= 0.25)
        {
            return new ModelChoice(
                "claude-sonnet-5",
                "Escalated because this role has needed retries in prior harness runs.");
        }

        return role switch
        {
            "account-manager" => new ModelChoice(
                "claude-haiku-4.5",
                "Low-latency conversational model for customer intake."),
            "product-manager" => new ModelChoice(
                "gpt-5-mini",
                "Fast conversational model for structured customer dialogue."),
            "team-lead" or "architect" when complexity >= 3 => new ModelChoice(
                "claude-sonnet-5",
                "Higher-reasoning model selected for cross-cutting planning."),
            "software-engineer" when complexity >= 4 => new ModelChoice(
                "gpt-5.4",
                "High-complexity implementation needs stronger code reasoning."),
            "software-engineer" => new ModelChoice(
                "gpt-5.4-mini",
                "Focused implementation fits the efficient coding model."),
            "security-engineer" => new ModelChoice(
                "gpt-5.4",
                "Security review benefits from deeper adversarial reasoning."),
            "quality-engineer" => new ModelChoice(
                "gpt-5.4-mini",
                "Efficient model for deterministic validation and evidence review."),
            _ when complexity >= 4 => new ModelChoice(
                "gpt-5.4",
                "Task complexity crossed the harness escalation threshold."),
            _ => new ModelChoice(
                "gpt-5.4-mini",
                "Balanced default selected for speed and cost.")
        };
    }
}

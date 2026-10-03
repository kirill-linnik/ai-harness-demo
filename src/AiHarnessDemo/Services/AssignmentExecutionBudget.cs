using System.Text.Json;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Data;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

internal static class AssignmentExecutionBudget
{
    internal const string ExhaustedReason =
        "The assignment's absolute execution budget is exhausted. Automatic retries are disabled. " +
        "Workspace edits are preserved. Inspect the handoff and workspace, then scope remaining work " +
        "as a distinct new assignment or flow; restarting this assignment does not extend its budget.";

    public static AgentExecutionPolicy Policy(CopilotConfig config, string revision) =>
        new(config.InactivityTimeoutMs, config.SilentToolTimeoutMs,
            config.SoftWarningMs, config.ExecutionBudgetMs, revision);

    public static async Task<AgentExecutionBudget> ResolveAsync(
        AgentExecutionContext context,
        WorkflowDefinition workflow,
        IDbContextFactory<HarnessDbContext> factory,
        CancellationToken cancellationToken,
        TimeProvider? clock = null)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var step = await database.FlowSteps.SingleAsync(
            item => item.Id == context.FlowStepId && item.FlowRunId == context.FlowId,
            cancellationToken);
        var rootId = step.ExecutionBudgetRootId ??
                     step.StableSemanticRootId ?? step.RetryOfStepId ?? step.Id;
        var budget = await database.AgentExecutionBudgets.SingleOrDefaultAsync(
            item => item.RootStepId == rootId && item.AgentId == step.AgentId,
            cancellationToken);
        if (budget is null)
        {
            var priorSteps = await database.FlowSteps.Where(item =>
                    item.FlowRunId == step.FlowRunId && item.AgentId == step.AgentId &&
                    (item.Id == rootId ||
                     (item.StableSemanticRootId ?? item.RetryOfStepId ?? item.Id) == rootId))
                .ToListAsync(cancellationToken);
            var startedAt = priorSteps.Where(item => item.StartedAt.HasValue)
                .Select(item => item.StartedAt!.Value).Append(now).Min();
            var policy = Policy(workflow.Config.Copilot, workflow.Revision);
            budget = new AgentExecutionBudget
            {
                RootStepId = rootId,
                FlowRunId = step.FlowRunId,
                AgentId = step.AgentId,
                StartedAt = startedAt,
                DeadlineAt = startedAt.AddMilliseconds(policy.ExecutionBudgetMs),
                PolicyJson = JsonSerializer.Serialize(policy)
            };
            database.AgentExecutionBudgets.Add(budget);
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = step.FlowRunId,
                FlowStepId = step.Id,
                Type = "agent.execution-budget-bound",
                Message = $"Assignment execution deadline bound to {budget.DeadlineAt:O}; " +
                          "retry delays and application downtime count against this budget."
            });
        }
        if (budget.FlowRunId != step.FlowRunId)
        {
            throw new InvalidOperationException("Execution budget is bound to another flow.");
        }
        step.ExecutionBudgetRootId = rootId;
        step.ExecutionPolicyJson = budget.PolicyJson;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return budget;
    }

    public static AgentExecutionPolicy ReadPolicy(AgentExecutionBudget budget) =>
        JsonSerializer.Deserialize<AgentExecutionPolicy>(budget.PolicyJson) ??
        throw new InvalidOperationException("The persisted execution policy is unavailable.");

    public static async Task RecordActivityAsync(
        AgentExecutionContext context,
        AgentRuntimeActivity activity,
        IDbContextFactory<HarnessDbContext> factory)
    {
        await using var database = await factory.CreateDbContextAsync();
        await using var transaction = await database.Database.BeginTransactionAsync();
        var step = await database.FlowSteps.SingleAsync(item => item.Id == context.FlowStepId);
        var previous = string.IsNullOrEmpty(step.RuntimeActivityJson)
            ? null
            : JsonSerializer.Deserialize<AgentRuntimeActivity>(step.RuntimeActivityJson);
        step.RuntimeActivityJson = JsonSerializer.Serialize(activity);
        var budget = await database.AgentExecutionBudgets.SingleAsync(item =>
            item.RootStepId == step.ExecutionBudgetRootId && item.AgentId == step.AgentId);
        if (activity.SoftWarning && budget.SoftWarningAt is null)
        {
            budget.SoftWarningAt = activity.ObservedAt;
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = step.FlowRunId,
                FlowStepId = step.Id,
                Type = "agent.soft-limit-warning",
                Message = "The assignment crossed its soft elapsed-time threshold. This warning " +
                          "does not terminate execution; inactivity and the finite absolute budget " +
                          "remain independently enforced."
            });
        }
        if (previous?.ActiveTool != activity.ActiveTool ||
            previous?.ActiveToolStartedAt != activity.ActiveToolStartedAt ||
            previous?.TerminationReason != activity.TerminationReason)
        {
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = step.FlowRunId,
                FlowStepId = step.Id,
                Type = activity.TerminationReason is null ? "agent.activity" : "agent.execution-terminated",
                Message = activity.TerminationReason is { } reason
                    ? $"Execution termination reason: {reason}."
                    : activity.ActiveTool is { } tool
                        ? $"Observed active tool: {tool}."
                        : "Observed tool execution ended; waiting for further structured activity."
            });
        }
        await database.SaveChangesAsync();
        await transaction.CommitAsync();
    }
}

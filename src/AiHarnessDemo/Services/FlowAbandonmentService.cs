using AiHarnessDemo.Contracts;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Data;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

public interface IFlowSessionCleaner
{
    Task<int> DeleteAsync(
        FlowRun flow,
        IReadOnlyCollection<FlowStep> steps,
        CancellationToken cancellationToken = default);
}

public sealed class FlowSessionCleaner(
    CopilotSessionJournal sessionJournal) : IFlowSessionCleaner
{
    public Task<int> DeleteAsync(
        FlowRun flow,
        IReadOnlyCollection<FlowStep> steps,
        CancellationToken cancellationToken = default)
    {
        var homes = steps
            .Select(item => item.CopilotSessionHome)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        var sessionIds = steps
            .Select(item => item.CopilotSessionId)
            .OfType<Guid>()
            .Concat(steps.Select(item =>
                AgentSessionIdentity.Create(
                    flow.Id,
                    item.Iteration,
                    item.AgentId)))
            .Distinct()
            .ToList();
        return sessionJournal.DeleteWorkspaceSessionsAsync(
            homes,
            flow.WorkspacePath,
            sessionIds,
            cancellationToken);
    }
}

public sealed class FlowAbandonmentService(
    IDbContextFactory<HarnessDbContext> databaseFactory,
    IFlowExecutionController executionController,
    IWorkspaceProcessCleaner processCleaner,
    IFlowSessionCleaner sessionCleaner,
    IWorkspaceManager workspaceManager,
    FlowLifecycleCoordinator lifecycle,
    ILogger<FlowAbandonmentService> logger)
{
    public async Task<AbandonFlowResponse> AbandonAsync(
        Guid flowId,
        CancellationToken cancellationToken = default)
    {
        await using var lifecycleLease =
            await lifecycle.EnterAsync(flowId, cancellationToken);
            FlowRun flow;
            List<FlowStep> steps;
            await using (var database =
                         await databaseFactory.CreateDbContextAsync(cancellationToken))
            {
                flow = await database.Flows
                           .Include(item => item.Steps)
                           .Include(item => item.GateRecords)
                           .SingleOrDefaultAsync(item => item.Id == flowId, cancellationToken)
                       ?? throw new KeyNotFoundException(
                           $"Factory flow '{flowId}' was not found.");
                if (flow.Status == FlowStatus.Approved)
                {
                    throw new InvalidOperationException(
                        "An approved and published flow cannot be abandoned.");
                }
                if (flow.GateRecords.Any(item =>
                        item.ActionType == AiHarnessDemo.Core.Gating.HandoffActionType.Release &&
                        item.Resolved &&
                        item.Approved == true))
                {
                    throw new InvalidOperationException(
                        "Customer approval is already recorded, so release publication cannot be abandoned.");
                }
                if (flow.Status == FlowStatus.Abandoned)
                {
                    return new AbandonFlowResponse(
                        flow.Id,
                        flow.Status,
                        ProcessesStopped: 0,
                        ListeningPortsReleased: [],
                        CopilotSessionsDeleted: 0,
                        WorktreesRemoved: 0,
                        LocalBranchesDeleted: 0,
                        RemoteBranchesDeleted: 0);
                }
                flow.Status = FlowStatus.Abandoning;
                flow.UpdatedAt = DateTimeOffset.UtcNow;
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    Type = "flow.abandoning",
                    Message =
                        "Customer abandoned the flow. Runtime and customer artifacts are being removed; learning evidence is retained."
                });
                await database.SaveChangesAsync(cancellationToken);
                steps = flow.Steps.ToList();
            }

            try
            {
                await executionController.CancelAsync(flow.Id, CancellationToken.None);
                var processes = await processCleaner.StopAsync(
                    flow.WorkspacePath,
                    CancellationToken.None);
                var sessionsDeleted = await sessionCleaner.DeleteAsync(
                    flow,
                    steps,
                    CancellationToken.None);
                var workspace = await workspaceManager.RemoveAsync(
                    flow,
                    CancellationToken.None);

                await using var database =
                    await databaseFactory.CreateDbContextAsync(CancellationToken.None);
                var stored = await database.Flows
                    .Include(item => item.Steps)
                    .SingleAsync(item => item.Id == flow.Id);
                var abandonedAt = DateTimeOffset.UtcNow;
                foreach (var step in stored.Steps.Where(item =>
                             item.Status is StepStatus.Pending or StepStatus.Running))
                {
                    step.Status = StepStatus.Skipped;
                    step.Phase = AgentRunPhase.CanceledByReconciliation;
                    step.CompletedAt = abandonedAt;
                }
                stored.Status = FlowStatus.Abandoned;
                stored.OutcomeUrl = string.Empty;
                stored.OutcomeLabel = "Abandoned";
                stored.FailureReason = string.Empty;
                stored.CompletedAt = abandonedAt;
                stored.UpdatedAt = abandonedAt;
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = stored.Id,
                    Type = "flow.abandoned",
                    Message =
                        $"Customer artifacts removed: {processes.ProcessIds.Count} process(es), " +
                        $"{(processes.ListeningPorts.Count == 0 ? "no listening ports" : "ports " + string.Join(", ", processes.ListeningPorts))}, " +
                        $"{sessionsDeleted} Copilot session(s), {workspace.WorktreesRemoved} worktree(s), " +
                        $"{workspace.LocalBranchesDeleted} local branch(es), and " +
                        $"{workspace.RemoteBranchesDeleted} remote branch(es). " +
                        "Execution history and learning evidence were retained."
                });
                await database.SaveChangesAsync(CancellationToken.None);

                return new AbandonFlowResponse(
                    stored.Id,
                    stored.Status,
                    processes.ProcessIds.Count,
                    processes.ListeningPorts,
                    sessionsDeleted,
                    workspace.WorktreesRemoved,
                    workspace.LocalBranchesDeleted,
                    workspace.RemoteBranchesDeleted);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Artifact cleanup failed while abandoning flow {FlowId}.",
                    flow.Id);
                await using var database =
                    await databaseFactory.CreateDbContextAsync(CancellationToken.None);
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    Type = "flow.abandon-failed",
                    Message =
                        $"Artifact cleanup did not complete and can be retried: {exception.GetBaseException().Message}"
                });
                await database.SaveChangesAsync(CancellationToken.None);
                throw;
            }
    }

    public async Task ResumePendingAsync(CancellationToken cancellationToken = default)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var flowIds = await database.Flows
            .AsNoTracking()
            .Where(item => item.Status == FlowStatus.Abandoning)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        foreach (var flowId in flowIds)
        {
            try
            {
                await AbandonAsync(flowId, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Pending abandonment for flow {FlowId} still requires attention.",
                    flowId);
            }
        }
    }
}

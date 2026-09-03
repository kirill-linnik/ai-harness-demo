using AiHarnessDemo.Contracts;
using AiHarnessDemo.Data;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Api;

public static class DemoApi
{
    public static IEndpointRouteBuilder MapDemoApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        api.MapGet("/health", GetHealthAsync);

        api.MapGet("/bootstrap", GetBootstrapAsync);
        api.MapGet("/settings", GetSettingsAsync);
        api.MapPut("/settings", SaveSettingsAsync);
        api.MapGet("/agents", GetAgentsAsync);
        api.MapPut("/agents/{agentId}", ToggleAgentAsync);
        api.MapGet("/directories", ListDirectories);
        api.MapPost("/repositories/analyze", AnalyzeRepositoryAsync);
        api.MapPost("/intake", ContinueIntakeAsync);
        api.MapGet("/flows", GetFlowsAsync);
        api.MapGet("/flows/{flowId:guid}", GetFlowAsync);
        api.MapPost("/flows/{flowId:guid}/start", StartFlowAsync);
        api.MapPost("/flows/{flowId:guid}/restart", RestartFlowAsync);
        api.MapPost("/flows/{flowId:guid}/feedback", AddFeedbackAsync);
        api.MapPost("/flows/{flowId:guid}/decision", DecideFlowAsync);
        api.MapGet("/history", GetHistoryAsync);
        api.MapGet("/learnings", GetLearningsAsync);
        api.MapGet("/previews/{flowId:guid}", GetPreviewAsync);
        api.MapGet(
            "/previews/{flowId:guid}/artifacts/{artifactId}/{**path}",
            GetPreviewArtifactAsync);

        var symphony = endpoints.MapGroup("/api/v1");
        symphony.MapGet("/state", GetBootstrapAsync);
        symphony.MapGet("/{flowId:guid}", GetFlowAsync);
        symphony.MapPost("/refresh", RefreshRuntimeAsync);

        return endpoints;
    }

    private static async Task<IResult> GetHealthAsync(
        WorkflowDefinitionProvider workflowProvider,
        CopilotCliRuntime copilotCliRuntime,
        ModelCatalogDiscovery modelCatalog,
        CancellationToken cancellationToken)
    {
        var workflow = workflowProvider.GetValidated();
        var copilotCli = await copilotCliRuntime.GetAsync(
            workflow.Config.Copilot.Command,
            cancellationToken);
        return Results.Ok(new
        {
            status = copilotCli.Ready && modelCatalog.Current.Ready ? "ready" : "degraded",
            utc = DateTimeOffset.UtcNow,
            copilotCliAvailable = copilotCli.Ready,
            copilotCli = ToDto(copilotCli),
            modelCatalog = ToDto(modelCatalog.Current)
        });
    }

    private static async Task<IResult> GetBootstrapAsync(
        IDbContextFactory<HarnessDbContext> databaseFactory,
        AgentCatalog catalog,
        WorkflowDefinitionProvider workflowProvider,
        CopilotCliRuntime copilotCliRuntime,
        ModelCatalogDiscovery modelCatalog,
        CancellationToken cancellationToken)
    {
        var agents = await catalog.SyncAsync(cancellationToken);
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var settings = await database.Settings.AsNoTracking().SingleAsync(cancellationToken);
        var flows = await database.Flows
            .AsNoTracking()
            .OrderByDescending(item => item.UpdatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);
        var learningCount = await database.Learnings.CountAsync(cancellationToken);
        var totalMilliseconds = await database.FlowSteps
            .Where(item => item.DurationMilliseconds > 0)
            .SumAsync(item => (long?)item.DurationMilliseconds, cancellationToken) ?? 0;

        var stats = new HarnessStatsDto(
            flows.Count,
            flows.Count(item => item.Status is FlowStatus.Queued or FlowStatus.Running or FlowStatus.Reworking),
            flows.Count(item => item.Status == FlowStatus.Approved),
            learningCount,
            totalMilliseconds / 60_000);
        var workflow = workflowProvider.GetValidated();
        var copilotCli = await copilotCliRuntime.GetAsync(
            workflow.Config.Copilot.Command,
            cancellationToken);
        var workflowStatus = workflowProvider.Status();
        var factoryDisabledReason = FactoryDisabledReason(
            settings,
            copilotCli,
            modelCatalog.Current,
            workflowStatus);

        return Results.Ok(new BootstrapDto(
            settings.ToDto(),
            agents.Select(item => item.ToDto()).ToList(),
            flows.Select(item => item.ToSummaryDto()).ToList(),
            stats,
            copilotCli.Ready,
            ToDto(copilotCli),
            ToDto(modelCatalog.Current),
            ToDto(workflowStatus),
            string.IsNullOrEmpty(factoryDisabledReason),
            factoryDisabledReason));
    }

    private static async Task<IResult> GetSettingsAsync(
        IDbContextFactory<HarnessDbContext> databaseFactory,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var settings = await database.Settings.AsNoTracking().SingleAsync(cancellationToken);
        return Results.Ok(settings.ToDto());
    }

    private static async Task<IResult> SaveSettingsAsync(
        SaveSettingsRequest request,
        IDbContextFactory<HarnessDbContext> databaseFactory,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var settings = await database.Settings.SingleAsync(cancellationToken);
        if (request.RepositoryPath is not null)
        {
            settings.RepositoryPath = request.RepositoryPath.Trim();
        }
        if (request.RepositoryKnowledge is not null)
        {
            settings.RepositoryKnowledge = request.RepositoryKnowledge.Trim();
        }

        if (request.MaxHandoffRetries is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.MaxHandoffRetries),
                "Handoff retries must be between 0 and 10.");
        }

        settings.Outcome = request.Outcome;
        settings.ModelSelectionStrategy = request.ModelSelectionStrategy;
        if (request.MaxHandoffRetries is { } maxHandoffRetries)
        {
            settings.MaxHandoffRetries = maxHandoffRetries;
        }
        settings.RuntimeMarker = "LiveCopilot";
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        return Results.Ok(settings.ToDto());
    }

    private static async Task<IResult> GetAgentsAsync(
        AgentCatalog catalog,
        CancellationToken cancellationToken)
    {
        var agents = await catalog.SyncAsync(cancellationToken);
        return Results.Ok(agents.Select(item => item.ToDto()));
    }

    private static async Task<IResult> ToggleAgentAsync(
        string agentId,
        ToggleAgentRequest request,
        AgentCatalog catalog,
        IDbContextFactory<HarnessDbContext> databaseFactory,
        CancellationToken cancellationToken)
    {
        await catalog.SyncAsync(cancellationToken);
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var agent = await database.Agents.SingleOrDefaultAsync(
                        item => item.Id == agentId,
                        cancellationToken)
                    ?? throw new KeyNotFoundException($"Agent '{agentId}' was not found.");
        agent.Enabled = request.Enabled;
        agent.UpdatedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        return Results.Ok(agent.ToDto());
    }

    private static IResult ListDirectories(
        [FromQuery] string? path,
        RepositoryAnalyzer analyzer) =>
        Results.Ok(analyzer.ListDirectories(path));

    private static async Task<IResult> AnalyzeRepositoryAsync(
        AnalyzeRepositoryRequest request,
        RepositoryAnalyzer analyzer,
        CancellationToken cancellationToken)
    {
        var result = await analyzer.AnalyzeAsync(
            request.Path,
            request.RunCopilotInit,
            cancellationToken);
        return Results.Ok(new AnalyzeRepositoryResponse(
            result.Path,
            result.Knowledge,
            result.CopilotInitSucceeded,
            result.CopilotInitMessage));
    }

    private static async Task<IResult> ContinueIntakeAsync(
        IntakeRequest request,
        IntakeCoordinator coordinator,
        CancellationToken cancellationToken) =>
        Results.Ok(await coordinator.ContinueAsync(request, cancellationToken));

    private static async Task<IResult> GetFlowsAsync(
        IDbContextFactory<HarnessDbContext> databaseFactory,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var flows = await database.Flows
            .AsNoTracking()
            .OrderByDescending(item => item.UpdatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);
        return Results.Ok(flows.Select(item => item.ToSummaryDto()));
    }

    private static async Task<IResult> GetFlowAsync(
        Guid flowId,
        IDbContextFactory<HarnessDbContext> databaseFactory,
        CancellationToken cancellationToken)
    {
        var flow = await LoadFlowAsync(databaseFactory, flowId, cancellationToken);
        return Results.Ok(flow.ToDetailDto());
    }

    private static async Task<IResult> StartFlowAsync(
        Guid flowId,
        IDbContextFactory<HarnessDbContext> databaseFactory,
        FlowQueue queue,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var flow = await database.Flows
                       .Include(item => item.Steps)
                       .ThenInclude(step => step.ToolCalls)
                       .Include(item => item.Steps)
                       .ThenInclude(step => step.RoutingDecisions)
                       .ThenInclude(decision => decision.TaskProfile)
                       .Include(item => item.Steps)
                       .ThenInclude(step => step.RoutingDecisions)
                       .ThenInclude(decision => decision.Alternatives)
                       .Include(item => item.Messages)
                       .Include(item => item.Events)
                       .Include(item => item.GateRecords)
                       .SingleOrDefaultAsync(item => item.Id == flowId, cancellationToken)
                   ?? throw new KeyNotFoundException($"Factory flow '{flowId}' was not found.");

        if (flow.Status != FlowStatus.Intake)
        {
            throw new InvalidOperationException("Only an intake flow can be started.");
        }
        var latestAccountManagerMessage = flow.Messages
            .Where(item => item.Role == ConversationRole.AccountManager)
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefault();
        var latestIntakeEvent = flow.Events
            .Where(item => item.Type.StartsWith("intake.", StringComparison.Ordinal))
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefault();
        if (latestAccountManagerMessage is null ||
            latestAccountManagerMessage.IsQuestion ||
            latestIntakeEvent?.Type != "intake.confirmed" ||
            !flow.Steps.Any(item =>
                item.AgentRole == "account-manager" &&
                item.Status == StepStatus.Completed))
        {
            throw new InvalidOperationException(
                "The customer must explicitly confirm the Account Manager brief before the flow starts.");
        }

        var settings = await database.Settings.AsNoTracking().SingleAsync(cancellationToken);
        flow.ModelSelectionStrategy = settings.ModelSelectionStrategy;
        flow.Status = FlowStatus.Queued;
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        var queuedEvent = new FlowEvent
        {
            FlowRunId = flow.Id,
            Type = "flow.queued",
            Message = "Customer brief entered the AI factory queue."
        };
        flow.Events.Add(queuedEvent);
        database.Entry(queuedEvent).State = EntityState.Added;
        await database.SaveChangesAsync(cancellationToken);

        if (!queue.Queue(flowId))
        {
            throw new InvalidOperationException("Unable to queue the factory flow.");
        }

        return Results.Accepted($"/api/flows/{flowId}", flow.ToDetailDto());
    }

    private static async Task<IResult> AddFeedbackAsync(
        Guid flowId,
        FeedbackRequest request,
        FeedbackCoordinator coordinator,
        CancellationToken cancellationToken) =>
        Results.Ok(await coordinator.RespondAsync(flowId, request.Message, cancellationToken));

    private static async Task<IResult> RestartFlowAsync(
        Guid flowId,
        WorkflowEngine engine,
        FlowQueue queue,
        CancellationToken cancellationToken)
    {
        var flow = await engine.RestartFailedFlowAsync(flowId, cancellationToken);
        if (!queue.Queue(flowId))
        {
            throw new InvalidOperationException("Unable to queue the restarted factory flow.");
        }

        return Results.Accepted($"/api/flows/{flowId}", flow.ToDetailDto());
    }

    private static async Task<IResult> DecideFlowAsync(
        Guid flowId,
        FlowDecisionRequest request,
        FeedbackCoordinator coordinator,
        CancellationToken cancellationToken) =>
        Results.Ok(await coordinator.DecideAsync(flowId, request.Approve, cancellationToken));

    private static async Task<IResult> GetHistoryAsync(
        IDbContextFactory<HarnessDbContext> databaseFactory,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var history = await (
                from step in database.FlowSteps.AsNoTracking()
                join flow in database.Flows.AsNoTracking()
                    on step.FlowRunId equals flow.Id
                orderby step.StartedAt descending
                select new HistoryItemDto(
                    flow.Id,
                    flow.Title,
                    step.Iteration,
                    step.AgentName,
                    step.AgentRole,
                    step.Model,
                    step.Status,
                    step.DurationMilliseconds,
                    step.StartedAt,
                    step.PushbackReason))
            .Take(500)
            .ToListAsync(cancellationToken);
        return Results.Ok(history);
    }

    private static async Task<IResult> GetLearningsAsync(
        IDbContextFactory<HarnessDbContext> databaseFactory,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var learnings = await database.Learnings
            .AsNoTracking()
            .OrderByDescending(item => item.CreatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);
        return Results.Ok(learnings.Select(item => item.ToDto()));
    }

    private static async Task<IResult> GetPreviewAsync(
        Guid flowId,
        IDbContextFactory<HarnessDbContext> databaseFactory,
        PreviewArtifactCatalog artifactCatalog,
        CancellationToken cancellationToken)
    {
        var flow = await LoadFlowAsync(databaseFactory, flowId, cancellationToken);
        if (flow.Status is not (FlowStatus.WaitingForFeedback or FlowStatus.Approved))
        {
            throw new InvalidOperationException("This flow does not have a customer preview yet.");
        }

        var deliveredBy = flow.Steps
            .Where(item => item.Status == StepStatus.Completed)
            .OrderBy(item => item.Iteration)
            .ThenBy(item => item.Sequence)
            .Select(item => item.ToDto())
            .ToList();
        return Results.Ok(new PreviewDto(
            flow.Id,
            flow.Title,
            flow.ConsolidatedRequest,
            Path.GetFileName(flow.RepositoryPath),
            flow.Iteration,
            flow.Status,
            flow.OutcomeLabel,
            artifactCatalog.Discover(flow)
                .Select(item => new PreviewArtifactDto(
                    item.Id,
                    item.Label,
                    item.Url))
                .ToList(),
            deliveredBy,
            flow.UpdatedAt));
    }

    private static async Task<IResult> GetPreviewArtifactAsync(
        Guid flowId,
        string artifactId,
        string? path,
        IDbContextFactory<HarnessDbContext> databaseFactory,
        PreviewArtifactCatalog artifactCatalog,
        CancellationToken cancellationToken)
    {
        var flow = await LoadFlowAsync(databaseFactory, flowId, cancellationToken);
        if (flow.Status is not (FlowStatus.WaitingForFeedback or FlowStatus.Approved))
        {
            throw new InvalidOperationException(
                "This flow does not have a customer preview yet.");
        }

        var filePath = artifactCatalog.ResolveFile(flow, artifactId, path);
        var contentTypes = new FileExtensionContentTypeProvider();
        if (!contentTypes.TryGetContentType(filePath, out var contentType))
        {
            contentType = "application/octet-stream";
        }
        return Results.File(
            filePath,
            contentType,
            enableRangeProcessing: true);
    }

    private static async Task<IResult> RefreshRuntimeAsync(
        WorkflowDefinitionProvider workflowProvider,
        AgentCatalog catalog,
        CopilotCliRuntime copilotCliRuntime,
        ModelCatalogDiscovery modelCatalog,
        IDbContextFactory<HarnessDbContext> databaseFactory,
        FlowQueue queue,
        CancellationToken cancellationToken)
    {
        var requestedAt = DateTimeOffset.UtcNow;
        var workflow = workflowProvider.GetValidated();
        await copilotCliRuntime.RefreshAsync(
            workflow.Config.Copilot.Command,
            cancellationToken);
        await modelCatalog.RefreshAsync(
            Directory.GetCurrentDirectory(),
            cancellationToken);
        await catalog.SyncAsync(cancellationToken);

        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var queued = await database.Flows
            .Where(item =>
                item.Status == FlowStatus.Queued ||
                item.Status == FlowStatus.Reworking)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        foreach (var flowId in queued)
        {
            queue.Queue(flowId);
        }

        return Results.Accepted(value: new
        {
            queued = true,
            coalesced = false,
            requestedAt,
            operations = new[]
            {
                "workflow-reload",
                "copilot-cli-readiness",
                "acp-model-catalog",
                "agent-catalog-reconcile",
                "flow-recovery"
            }
        });
    }

    private static async Task<FlowRun> LoadFlowAsync(
        IDbContextFactory<HarnessDbContext> databaseFactory,
        Guid flowId,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        return await database.Flows
                   .AsNoTracking()
                   .AsSplitQuery()
                   .Include(item => item.Steps)
                   .ThenInclude(step => step.ToolCalls)
                   .Include(item => item.Steps)
                   .ThenInclude(step => step.RoutingDecisions)
                   .ThenInclude(decision => decision.TaskProfile)
                   .Include(item => item.Steps)
                   .ThenInclude(step => step.RoutingDecisions)
                   .ThenInclude(decision => decision.Alternatives)
                   .Include(item => item.Messages)
                   .Include(item => item.Events)
                   .Include(item => item.GateRecords)
                   .SingleOrDefaultAsync(item => item.Id == flowId, cancellationToken)
               ?? throw new KeyNotFoundException($"Factory flow '{flowId}' was not found.");
    }

    private static WorkflowStatusDto ToDto(WorkflowRuntimeStatus status) =>
        new(
            status.Ready,
            status.SourcePath,
            status.LoadedAt,
            status.LastError,
            status.MaxConcurrentAgents,
            status.MaxAttempts,
            status.WorkspaceRoot);

    private static CopilotCliStatusDto ToDto(CopilotCliRuntimeStatus status) =>
        new(
            status.Ready,
            status.Command,
            status.ResolvedPath,
            status.Version,
            status.Detail,
            status.CheckedAt);

    private static ModelCatalogStatusDto ToDto(ModelCatalogRuntimeStatus status) =>
        new(
            status.Ready,
            status.CatalogVersion,
            status.CandidateCount,
            status.Detail,
            status.CheckedAt);

    private static string FactoryDisabledReason(
        HarnessSettings settings,
        CopilotCliRuntimeStatus copilotCli,
        ModelCatalogRuntimeStatus modelCatalog,
        WorkflowRuntimeStatus workflow)
    {
        if (!copilotCli.Ready)
        {
            return copilotCli.Detail;
        }
        if (!workflow.Ready)
        {
            return workflow.LastError ?? "WORKFLOW.md is not ready.";
        }
        if (!modelCatalog.Ready)
        {
            return modelCatalog.Detail;
        }
        if (string.IsNullOrWhiteSpace(settings.RepositoryPath) ||
            string.IsNullOrWhiteSpace(settings.RepositoryKnowledge))
        {
            return "Add and study a source project in Settings before starting the factory.";
        }
        if (!Directory.Exists(settings.RepositoryPath))
        {
            return "The selected source project no longer exists. Choose it again in Settings.";
        }
        if (!RepositoryAnalyzer.IsProjectDirectory(settings.RepositoryPath))
        {
            return "The selected project folder contains no Git repositories. Choose a project with source control in Settings.";
        }
        return string.Empty;
    }
}

using AiHarnessDemo.Contracts;
using AiHarnessDemo.Data;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace AiHarnessDemo.Api;

public static class DemoApi
{
    private static readonly TimeSpan ReviewedPreviewProjectionTimeout =
        TimeSpan.FromSeconds(1);

    public static IEndpointRouteBuilder MapDemoApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        api.MapGet("/health", GetHealthAsync);

        api.MapGet("/bootstrap", GetBootstrapAsync);
        api.MapGet("/settings", GetSettingsAsync);
        api.MapPut("/settings", SaveSettingsAsync);
        api.MapGet("/agents", GetAgentsAsync);
        api.MapPut("/agents/{agentId}", ToggleAgentAsync);
        api.MapGet("/agent-catalog", GetAgentCatalog);
        api.MapPost("/agent-catalog/reload", ReloadAgentCatalogAsync);
        api.MapGet("/directories", ListDirectories);
        api.MapPost("/repositories/analyze", AnalyzeRepositoryAsync);
        api.MapPost("/intake", ContinueIntakeAsync);
        api.MapGet("/flows", GetFlowsAsync);
        api.MapGet("/flows/{flowId:guid}", GetFlowAsync);
        api.MapPost("/flows/{flowId:guid}/start", StartFlowAsync);
        api.MapPost("/flows/{flowId:guid}/restart", RestartFlowAsync);
        api.MapPost("/flows/{flowId:guid}/recover", RecoverFlowAsync);
        api.MapPost("/flows/{flowId:guid}/review", ReviewFlowAsync);
        api.MapPost(
            "/flows/{flowId:guid}/readiness-waiver",
            GrantReadinessWaiverAsync);
        api.MapPost(
            "/flows/{flowId:guid}/readiness-resolution",
            ResolveReadinessAsync);
        api.MapGet("/flows/{flowId:guid}/review-result", GetReviewResultAsync);
        api.MapPost(
            "/flows/{flowId:guid}/qualification-resolution",
            ResolveQualificationAsync);
        api.MapGet(
            "/flows/{flowId:guid}/artifacts/{artifactId}/{**path}",
            GetFlowArtifactAsync);
        api.MapPost("/flows/{flowId:guid}/abandon", AbandonFlowAsync);
        api.MapGet("/history", GetHistoryAsync);
        api.MapGet("/learnings", GetLearningsAsync);
        api.MapGet("/previews/{flowId:guid}", GetPreviewAsync);
        api.MapGet(
            "/verification-previews/{flowId:guid}",
            GetVerificationPreviewAsync);
        api.MapGet(
            "/verification-previews/{flowId:guid}/artifacts/{artifactId}/{**path}",
            GetVerificationPreviewArtifactAsync);
        api.MapGet(
            "/previews/{flowId:guid}/artifacts/{artifactId}/demo",
            GetDemoStatusAsync);
        api.MapPost(
            "/previews/{flowId:guid}/artifacts/{artifactId}/demo/start",
            StartDemoAsync);
        api.MapPost(
            "/previews/{flowId:guid}/artifacts/{artifactId}/demo/restart",
            RestartDemoAsync);
        api.MapPost(
            "/previews/{flowId:guid}/artifacts/{artifactId}/demo/stop",
            StopDemoAsync);
        api.MapMethods(
            "/demos/{instanceId:guid}/{**path}",
            ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"],
            ProxyDemoAsync);
        api.MapGet(
            "/previews/{flowId:guid}/artifacts/{artifactId}/view",
            GetIsolatedPreviewView);
        api.MapGet(
            "/previews/{flowId:guid}/artifacts/{artifactId}/{**path}",
            GetPreviewArtifactAsync);

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
        NewWorkAdmissionService admissionService,
        CancellationToken cancellationToken)
    {
        var agents = catalog.List();
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var settings = await database.Settings.AsNoTracking().SingleAsync(cancellationToken);
        var flows = await database.Flows
            .AsNoTracking()
            .AsSplitQuery()
            .Include(item => item.Steps)
            .Include(item => item.GateRecords)
            .Include(item => item.LinkedFlowRuns)
            .ThenInclude(item => item.Steps)
            .Include(item => item.LinkedFlowRuns)
            .ThenInclude(item => item.GateRecords)
            .OrderByDescending(item => item.UpdatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);
        var learningCount = await database.Learnings.CountAsync(cancellationToken);
        var totalMilliseconds = await database.FlowSteps
            .Where(item => item.DurationMilliseconds > 0)
            .SumAsync(item => (long?)item.DurationMilliseconds, cancellationToken) ?? 0;

        var stats = new HarnessStatsDto(
            flows.Count,
            flows.Count(item => item.Status is
                FlowStatus.Intake or
                FlowStatus.Queued or
                FlowStatus.Running or
                FlowStatus.Reworking or
                FlowStatus.Abandoning),
            flows.Count(item => item.Status == FlowStatus.Approved),
            learningCount,
            totalMilliseconds / 60_000);
        var workflow = workflowProvider.GetValidated();
        var copilotCli = await copilotCliRuntime.GetAsync(
            workflow.Config.Copilot.Command,
            cancellationToken);
        var workflowStatus = workflowProvider.Status();
        var admission = await admissionService.GetStatusAsync(cancellationToken);
        var factoryDisabledReason = admission.Ready
            ? string.Empty
            : string.Join(" ", admission.Failures);

        return Results.Ok(new BootstrapDto(
            settings.ToDto(),
            agents.Select(item => item.ToDto()).ToList(),
            flows.Select(item => item.ToSummaryDto()).ToList(),
            stats,
            copilotCli.Ready,
            ToDto(copilotCli),
            ToDto(modelCatalog.Current),
            ToDto(workflowStatus),
            ToDto(catalog.Status()),
            ToDto(admission),
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
        ValidateSaveSettingsRequest(request);
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
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        return Results.Ok(settings.ToDto());
    }

    internal static void ValidateSaveSettingsRequest(
        SaveSettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = OutcomeTypeRules.RequireDelivery(
            request.Outcome,
            nameof(request.Outcome));
    }

    private static IResult GetAgentsAsync(AgentCatalog catalog)
    {
        var agents = catalog.List();
        return Results.Ok(agents.Select(item => item.ToDto()));
    }

    private static IResult GetAgentCatalog(AgentCatalog catalog) =>
        Results.Ok(new AgentCatalogDto(
            ToDto(catalog.Status()),
            catalog.List().Select(item => item.ToDto()).ToList()));

    private static async Task<IResult> ReloadAgentCatalogAsync(
        AgentCatalog catalog,
        CancellationToken cancellationToken)
    {
        var status = await catalog.ReloadAsync(cancellationToken);
        return Results.Ok(new AgentCatalogDto(
            ToDto(status),
            catalog.List().Select(item => item.ToDto()).ToList()));
    }

    private static async Task<IResult> ToggleAgentAsync(
        string agentId,
        ToggleAgentRequest request,
        AgentCatalog catalog,
        CancellationToken cancellationToken)
    {
        var agent = await catalog.ToggleAsync(agentId, request.Enabled, cancellationToken);
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
            .AsSplitQuery()
            .Include(item => item.Steps)
            .Include(item => item.GateRecords)
            .Include(item => item.LinkedFlowRuns)
            .ThenInclude(item => item.Steps)
            .Include(item => item.LinkedFlowRuns)
            .ThenInclude(item => item.GateRecords)
            .OrderByDescending(item => item.UpdatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);
        var readiness = await LoadReadinessLabelsAsync(
            database,
            flows.Select(item => item.Id),
            cancellationToken);
        return Results.Ok(flows.Select(item =>
        {
            var summary = item.ToSummaryDto();
            return readiness.TryGetValue(item.Id, out var state)
                ? summary with
                {
                    ReadinessState = state.State,
                    ReadinessLabel = state.Label
                }
                : summary;
        }));
    }

    /// <summary>
    /// Reads the durable readiness state for list projections. Cards and history rows must never
    /// paint a green result from <see cref="FlowStatus"/> alone.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<
        Guid,
        (DeliveryReadinessState State, string Label)>> LoadReadinessLabelsAsync(
        HarnessDbContext database,
        IEnumerable<Guid> flowIds,
        CancellationToken cancellationToken)
    {
        var ids = flowIds.Distinct().ToArray();
        var rows = await database.DeliveryReadinessSnapshots
            .AsNoTracking()
            .Where(item => item.Active && ids.Contains(item.FlowRunId))
            .Select(item => new
            {
                item.FlowRunId,
                item.State
            })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(
            item => item.FlowRunId,
            item => item.State switch
            {
                DeliveryReadinessState.ReadyToApprove =>
                    (DeliveryReadinessState.ReadyToApprove, "Ready to approve"),
                DeliveryReadinessState.NeedsCustomerWaiver =>
                    (DeliveryReadinessState.NeedsCustomerWaiver, "Needs customer waiver"),
                DeliveryReadinessState.NeedsRefinement =>
                    (DeliveryReadinessState.NeedsRefinement, "Needs refinement"),
                _ => (DeliveryReadinessState.Blocked, "Blocked")
            });
    }

    private static async Task<IResult> GetFlowAsync(
        Guid flowId,
        IDbContextFactory<HarnessDbContext> databaseFactory,
        PreviewArtifactCatalog artifactCatalog,
        IReviewedCandidateService reviewedCandidateService,
        DeliveryReadinessService readinessService,
        CancellationToken cancellationToken)
    {
        var flow = await LoadFlowAsync(databaseFactory, flowId, cancellationToken);
        var reviewedPreviewUrl = await ResolveReviewedPreviewUrlAsync(
            flow,
            artifactCatalog,
            reviewedCandidateService,
            cancellationToken);
        return Results.Ok(flow.ToDetailDto(
            reviewedPreviewUrl,
            await LoadReadinessDtoAsync(
                databaseFactory,
                readinessService,
                flow,
                cancellationToken)));
    }

    /// <summary>
    /// Loads the durable readiness projection for a Studio Delivery flow. A flow with no
    /// assessment returns <c>null</c> so the client shows no readiness claim at all rather than an
    /// optimistic one.
    /// </summary>
    internal static async Task<DeliveryReadinessDto?> LoadReadinessDtoAsync(
        IDbContextFactory<HarnessDbContext> databaseFactory,
        DeliveryReadinessService readinessService,
        FlowRun flow,
        CancellationToken cancellationToken)
    {
        if (!DeliveryReadinessService.AppliesTo(flow))
        {
            return null;
        }
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var binding = await readinessService.LoadCurrentAsync(
            database,
            flow.Id,
            cancellationToken);
        return binding is null
            ? null
            : ApiMappings.ToDeliveryReadinessDto(binding, flow);
    }

    /// <summary>
    /// Records the separate customer waiver for the exact disclosed waiver-required risks. A waiver
    /// is informed consent to named risks; it never accepts the product and never publishes.
    /// </summary>
    private static async Task<IResult> GrantReadinessWaiverAsync(
        Guid flowId,
        ReadinessWaiverRequest request,
        ReviewCoordinator reviews,
        CancellationToken cancellationToken) =>
        Results.Ok(await reviews.GrantReadinessWaiverAsync(
            flowId,
            request,
            cancellationToken));

    /// <summary>
    /// The typed resolution path for a Delivery result that is not releasable. Abandonment is
    /// authorized here and then executed through the existing durable abandonment service.
    /// </summary>
    private static async Task<IResult> ResolveReadinessAsync(
        Guid flowId,
        ReadinessResolutionRequest request,
        ReviewCoordinator reviews,
        FlowAbandonmentService abandonment,
        CancellationToken cancellationToken)
    {
        var result = await reviews.ResolveReadinessAsync(
            flowId,
            request,
            cancellationToken);
        if (result.Action != ReadinessResolutionAction.Abandon)
        {
            return Results.Ok(result);
        }
        var abandoned = await abandonment.AbandonAsync(flowId, cancellationToken);
        return Results.Ok(result with
        {
            Status = abandoned.Status,
            Message = "The flow was abandoned through the durable abandonment path."
        });
    }

    internal static async Task<string?> ResolveReviewedPreviewUrlAsync(
        FlowRun flow,
        PreviewArtifactCatalog artifactCatalog,
        IReviewedCandidateService reviewedCandidateService,
        CancellationToken cancellationToken,
        TimeSpan? verificationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(artifactCatalog);
        ArgumentNullException.ThrowIfNull(reviewedCandidateService);
        if (flow.Kind != FlowKind.Delivery ||
            flow.Status != FlowStatus.WaitingForFeedback)
        {
            return null;
        }

        var currentStepIds = flow.Steps
            .Where(step => step.Iteration == flow.Iteration)
            .Select(step => step.Id)
            .ToHashSet();
        var hasCurrentOpenReview = flow.GateRecords.Any(gate =>
            gate.ActionType == HandoffActionType.CustomerReview &&
            !gate.Resolved &&
            currentStepIds.Contains(gate.FlowStepId));
        if (!hasCurrentOpenReview)
        {
            return null;
        }

        try
        {
            var verification = Task.Run(
                () => EnsureDeliveryPreviewCurrentAsync(
                    flow,
                    reviewedCandidateService,
                    CancellationToken.None),
                CancellationToken.None);
            var reviewed = await verification
                .WaitAsync(
                    verificationTimeout ?? ReviewedPreviewProjectionTimeout,
                    cancellationToken);
            return reviewed is not null &&
                   artifactCatalog.DiscoverVerified(
                           flow,
                           reviewed.Manifest.PreviewArtifacts)
                       .Count > 0
                ? $"#/preview/{flow.Id:D}"
                : null;
        }
        catch (CandidateValidationException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static async Task<IResult> StartFlowAsync(
        Guid flowId,
        IDbContextFactory<HarnessDbContext> databaseFactory,
        FlowQueue queue,
        FlowLifecycleCoordinator lifecycle,
        CancellationToken cancellationToken)
    {
        await using var lifecycleLease =
            await lifecycle.EnterAsync(flowId, cancellationToken);
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
                item.InvocationKind == ExecutionInvocationKind.Intake &&
                item.Status == StepStatus.Completed))
        {
            throw new InvalidOperationException(
                "The customer must explicitly confirm the Account Manager brief before the flow starts.");
        }

        var settings = await database.Settings.AsNoTracking().SingleAsync(cancellationToken);
        flow.ModelSelectionStrategy = settings.ModelSelectionStrategy;
        lifecycle.Transition(flow, FlowStatus.Queued);
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

    private static async Task<IResult> ReviewFlowAsync(
        Guid flowId,
        DirectReviewRequest request,
        ReviewCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        var result = await coordinator.ReviewAsync(
            flowId,
            request,
            cancellationToken);
        return Results.Ok(result.Review);
    }

    private static async Task<IResult> GetReviewResultAsync(
        Guid flowId,
        ReviewCoordinator coordinator,
        CancellationToken cancellationToken) =>
        Results.Ok(await coordinator.GetReviewResultAsync(
            flowId,
            cancellationToken));

    private static async Task<IResult> ResolveQualificationAsync(
        Guid flowId,
        QualificationResolutionRequest request,
        QualificationResolutionCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        var result = await coordinator.ResolveAsync(
            flowId,
            request,
            cancellationToken);
        return Results.Ok(result.Response);
    }

    private static async Task<IResult> GetFlowArtifactAsync(
        Guid flowId,
        string artifactId,
        string? path,
        [FromQuery] bool download,
        HttpContext httpContext,
        IDbContextFactory<HarnessDbContext> databaseFactory,
        AdvisoryArtifactCatalog artifactCatalog,
        CancellationToken cancellationToken)
    {
        var flow = await LoadFlowAsync(
            databaseFactory,
            flowId,
            cancellationToken);
        if (flow.Kind != FlowKind.Advisory)
        {
            throw new InvalidOperationException(
                "Generic flow artifacts are available only for Advisory flows.");
        }
        if (flow.Status is not (
                FlowStatus.WaitingForFeedback or
                FlowStatus.Approved))
        {
            throw new InvalidOperationException(
                "Advisory artifacts are available only after the result enters customer review.");
        }

        return ResolveAdvisoryArtifactResult(
            flow.Id,
            artifactId,
            path,
            download,
            httpContext.Response,
            artifactCatalog.Discover(flow));
    }

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

    private static async Task<IResult> RecoverFlowAsync(
        Guid flowId,
        IFlowRecoveryController recoveryController,
        IDbContextFactory<HarnessDbContext> databaseFactory,
        CancellationToken cancellationToken)
    {
        await recoveryController.RecoverAsync(flowId, cancellationToken);
        var flow = await LoadFlowAsync(
            databaseFactory,
            flowId,
            CancellationToken.None);
        return Results.Accepted($"/api/flows/{flowId}", flow.ToDetailDto());
    }

    private static async Task<IResult> AbandonFlowAsync(
        Guid flowId,
        FlowAbandonmentService abandonment,
        CancellationToken cancellationToken) =>
        Results.Ok(await abandonment.AbandonAsync(flowId, cancellationToken));

    private static async Task<IResult> GetHistoryAsync(
        IDbContextFactory<HarnessDbContext> databaseFactory,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var attempts = await (
                from step in database.FlowSteps.AsNoTracking()
                join flow in database.Flows.AsNoTracking()
                    on step.FlowRunId equals flow.Id
                orderby step.StartedAt descending
                select new
                {
                    FlowId = flow.Id,
                    flow.Title,
                    step.Iteration,
                    step.AgentName,
                    step.AgentRole,
                    step.Model,
                    StepStatus = step.Status,
                    step.DurationMilliseconds,
                    step.StartedAt,
                    step.PushbackReason
                })
            .Take(500)
            .ToListAsync(cancellationToken);
        var flowIds = attempts.Select(item => item.FlowId).Distinct().ToArray();
        var flows = await database.Flows
            .AsNoTracking()
            .AsSplitQuery()
            .Where(item => flowIds.Contains(item.Id))
            .Include(item => item.Steps)
            .Include(item => item.GateRecords)
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var readinessLabels = await LoadReadinessLabelsAsync(
            database,
            flowIds,
            cancellationToken);
        var history = attempts.Select(item =>
        {
            var flow = flows[item.FlowId];
            var readiness = readinessLabels.TryGetValue(flow.Id, out var state)
                ? state
                : ((DeliveryReadinessState?)null, (string?)null);
            return new HistoryItemDto(
                flow.Id,
                flow.Title,
                flow.Kind,
                flow.Status,
                flow.ParentFlowRunId,
                flow.ParentIteration,
                flow.LinkKind,
                flow.CurrentBlockerCode,
                flow.CustomerBlockerMessage,
                flow.OutcomeLabel,
                flow.ToReviewSummaryDto(),
                item.Iteration,
                item.AgentName,
                item.AgentRole,
                item.Model,
                item.StepStatus,
                item.DurationMilliseconds,
                item.StartedAt,
                item.PushbackReason,
                readiness.Item1,
                readiness.Item2);
        }).ToList();
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

    internal static async Task<IResult> GetPreviewAsync(
        Guid flowId,
        IDbContextFactory<HarnessDbContext> databaseFactory,
        PreviewArtifactCatalog artifactCatalog,
        AdvisoryArtifactCatalog advisoryArtifactCatalog,
        IReviewedCandidateService reviewedCandidateService,
        CancellationToken cancellationToken,
        [FromServices] DeliveryReadinessService? readinessService = null,
        [FromServices] DemoRuntimeManager? demoRuntime = null)
    {
        var flow = await LoadFlowAsync(databaseFactory, flowId, cancellationToken);
        if (flow.Status is not (FlowStatus.WaitingForFeedback or FlowStatus.Approved))
        {
            throw new InvalidOperationException("This flow does not have a customer preview yet.");
        }
        var reviewedSnapshot =
            await EnsureDeliveryPreviewCurrentAsync(
            flow,
            reviewedCandidateService,
            cancellationToken);
        var deliveredBy = flow.Steps
            .Where(item => item.Status == StepStatus.Completed)
            .OrderBy(item => item.Iteration)
            .ThenBy(item => item.Sequence)
            .Select(item => item.ToDto())
            .ToList();
        List<PreviewArtifactDto> artifacts;
        if (flow.Kind == FlowKind.Advisory)
        {
            artifacts = advisoryArtifactCatalog.Discover(flow)
                .Select(item =>
                {
                    var artifactId = ApiMappings.AdvisoryArtifactId(item.Path);
                    var url = ApiMappings.AdvisoryArtifactUrl(
                        flow.Id,
                        artifactId,
                        item.Path);
                    return new PreviewArtifactDto(
                        artifactId,
                        item.Path,
                        url,
                        url,
                        item.MediaType,
                        item.ByteLength,
                        ApiMappings.AdvisoryArtifactUrl(
                            flow.Id,
                            artifactId,
                            item.Path,
                            download: true),
                        Interactive: false,
                        DemoCapability.OfflineOnly,
                        null,
                        DemoInstanceState.Stopped,
                        null,
                        null,
                        null,
                        null);
                })
                .ToList();
        }
        else
        {
            var discovered = reviewedSnapshot is null
                ? artifactCatalog.Discover(flow)
                : artifactCatalog.DiscoverVerified(
                    flow,
                    reviewedSnapshot.Manifest.PreviewArtifacts);
            artifacts = [];
            foreach (var item in discovered)
            {
                DemoRuntimeStatus status;
                try
                {
                    status = demoRuntime is null
                        ? new DemoRuntimeStatus(
                            DemoCapability.OfflineOnly,
                            null,
                            DemoInstanceState.Stopped,
                            null,
                            null,
                            null,
                            null)
                        : await demoRuntime.GetStatusAsync(
                            flow.Id,
                            item.Id,
                            cancellationToken);
                }
                catch (CustomerDemoContractException exception)
                {
                    status = new DemoRuntimeStatus(
                        DemoCapability.OfflineOnly,
                        null,
                        DemoInstanceState.Failed,
                        null,
                        "The sealed live-demo manifest is invalid: " +
                        exception.Message,
                        reviewedSnapshot?.Fingerprint,
                        null);
                }
                artifacts.Add(new PreviewArtifactDto(
                    item.Id,
                    item.Label,
                    item.Url,
                    item.OpenUrl,
                    "text/html",
                    null,
                    null,
                    Interactive: true,
                    status.Capability,
                    status.InstanceId,
                    status.State,
                    status.StableUrl,
                    status.FailureDetail,
                    status.CandidateFingerprint,
                    status.ManifestHash));
            }
        }
        return Results.Ok(new PreviewDto(
            flow.Id,
            flow.Title,
            flow.ConsolidatedRequest,
            Path.GetFileName(flow.RepositoryPath),
            flow.Kind,
            flow.Iteration,
            flow.Status,
            flow.OutcomeLabel,
            flow.ToFlowOutcomeDto(),
            artifacts,
            deliveredBy,
            flow.ToReviewSummaryDto(),
            ReviewCoordinator.GetPublicationStatus(flow),
            readinessService is null
                ? null
                : await LoadReadinessDtoAsync(
                    databaseFactory,
                    readinessService,
                    flow,
                    cancellationToken),
            flow.UpdatedAt));
    }

    internal static async Task<IResult> GetPreviewArtifactAsync(
        Guid flowId,
        string artifactId,
        string? path,
        HttpContext httpContext,
        IDbContextFactory<HarnessDbContext> databaseFactory,
        PreviewArtifactCatalog artifactCatalog,
        IReviewedCandidateService reviewedCandidateService,
        CancellationToken cancellationToken)
    {
        var flow = await LoadFlowAsync(databaseFactory, flowId, cancellationToken);
        if (flow.Status is not (FlowStatus.WaitingForFeedback or FlowStatus.Approved))
        {
            throw new InvalidOperationException(
                "This flow does not have a customer preview yet.");
        }
        var reviewedSnapshot =
            await EnsureDeliveryPreviewCurrentAsync(
            flow,
            reviewedCandidateService,
            cancellationToken);
        return await ServePreviewArtifactAsync(
            flow,
            artifactId,
            path,
            httpContext,
            artifactCatalog,
            reviewedSnapshot,
            cancellationToken);
    }

    internal static async Task<IResult> GetVerificationPreviewAsync(
        Guid flowId,
        IDbContextFactory<HarnessDbContext> databaseFactory,
        PreviewArtifactCatalog artifactCatalog,
        CancellationToken cancellationToken)
    {
        var flow = await LoadFlowAsync(databaseFactory, flowId, cancellationToken);
        var verifier = RequireActiveDeliveryVerifier(flow);
        var basePath = PreviewArtifactCatalog.VerificationBasePath(flowId);
        return Results.Ok(new
        {
            flowId,
            flowStepId = verifier.Id,
            reviewed = false,
            artifacts = artifactCatalog.Discover(flow).Select(artifact => new
            {
                id = artifact.Id,
                label = artifact.Label,
                url = $"{basePath}/artifacts/{Uri.EscapeDataString(artifact.Id)}/index.html",
                openUrl = $"{basePath}/artifacts/{Uri.EscapeDataString(artifact.Id)}/view"
            }).ToArray()
        });
    }

    internal static async Task<IResult> GetVerificationPreviewArtifactAsync(
        Guid flowId,
        string artifactId,
        string? path,
        HttpContext httpContext,
        IDbContextFactory<HarnessDbContext> databaseFactory,
        PreviewArtifactCatalog artifactCatalog,
        CancellationToken cancellationToken)
    {
        var flow = await LoadFlowAsync(databaseFactory, flowId, cancellationToken);
        _ = RequireActiveDeliveryVerifier(flow);
        if (string.Equals(path, "view", StringComparison.Ordinal))
        {
            _ = artifactCatalog.ResolveFile(flow, artifactId, "index.html");
            return CreateIsolatedPreviewView(
                $"{PreviewArtifactCatalog.VerificationBasePath(flowId)}/artifacts/{Uri.EscapeDataString(artifactId)}/index.html",
                httpContext,
                "Unreviewed verification preview");
        }
        return await ServePreviewArtifactAsync(
            flow,
            artifactId,
            path,
            httpContext,
            artifactCatalog,
            reviewedSnapshot: null,
            cancellationToken);
    }

    private static FlowStep RequireActiveDeliveryVerifier(FlowRun flow)
    {
        var verifier = flow.Steps.SingleOrDefault(step =>
            step.Iteration == flow.Iteration &&
            step.Status == StepStatus.Running &&
            step.InvocationKind == ExecutionInvocationKind.Worker &&
            step.PlanStage == PlanStage.BeforeReview &&
            step.IsOutcomeOwner &&
            step.PlanStepKey == flow.OutcomeOwnerPlanStepKey &&
            WorkflowEngine.IsDeliveryVerificationStep(step));
        if (flow.Kind != FlowKind.Delivery ||
            flow.Status != FlowStatus.Running ||
            verifier is null)
        {
            throw new InvalidOperationException(
                "An unreviewed verification preview is available only while the current Delivery verifier is running.");
        }
        return verifier;
    }

    private static async Task<IResult> ServePreviewArtifactAsync(
        FlowRun flow,
        string artifactId,
        string? path,
        HttpContext httpContext,
        PreviewArtifactCatalog artifactCatalog,
        OutcomeCandidateSnapshot? reviewedSnapshot,
        CancellationToken cancellationToken)
    {
        var filePath = artifactCatalog.ResolveFile(flow, artifactId, path);
        var contentTypes = new FileExtensionContentTypeProvider();
        if (!contentTypes.TryGetContentType(filePath, out var contentType))
        {
            contentType = "application/octet-stream";
        }
        ApplyPreviewArtifactSecurityHeaders(httpContext.Response);
        if (reviewedSnapshot is not null)
        {
            var bytes = await ReadVerifiedPreviewFileAsync(
                flow,
                filePath,
                reviewedSnapshot,
                cancellationToken);
            bytes = ApplyPreviewSecurityLayer(bytes, contentType);
            return Results.File(
                bytes,
                contentType,
                enableRangeProcessing: true);
        }
        if (contentType.StartsWith(
                "text/html",
                StringComparison.OrdinalIgnoreCase))
        {
            var bytes = await File.ReadAllBytesAsync(
                filePath,
                cancellationToken);
            return Results.File(
                ApplyPreviewSecurityLayer(bytes, contentType),
                contentType,
                enableRangeProcessing: true);
        }
        return Results.File(
            filePath,
            contentType,
            enableRangeProcessing: true);
    }

    private static async Task<IResult> GetDemoStatusAsync(
        Guid flowId,
        string artifactId,
        [FromServices] DemoRuntimeManager runtime,
        CancellationToken cancellationToken) =>
        Results.Ok(await runtime.GetStatusAsync(
            flowId,
            artifactId,
            cancellationToken));

    private static async Task<IResult> StartDemoAsync(
        Guid flowId,
        string artifactId,
        DemoMutationRequest request,
        [FromServices] DemoRuntimeManager runtime,
        CancellationToken cancellationToken) =>
        Results.Ok(await runtime.StartAsync(
            flowId,
            artifactId,
            request.CandidateFingerprint,
            request.ManifestHash,
            cancellationToken));

    private static async Task<IResult> RestartDemoAsync(
        Guid flowId,
        string artifactId,
        DemoMutationRequest request,
        [FromServices] DemoRuntimeManager runtime,
        CancellationToken cancellationToken) =>
        Results.Ok(await runtime.RestartAsync(
            flowId,
            artifactId,
            request.CandidateFingerprint,
            request.ManifestHash,
            cancellationToken));

    private static async Task<IResult> StopDemoAsync(
        Guid flowId,
        string artifactId,
        DemoMutationRequest request,
        [FromServices] DemoRuntimeManager runtime,
        CancellationToken cancellationToken) =>
        Results.Ok(await runtime.StopAsync(
            flowId,
            artifactId,
            request.CandidateFingerprint,
            request.ManifestHash,
            cancellationToken));

    private static Task ProxyDemoAsync(
        Guid instanceId,
        string? path,
        HttpContext context,
        [FromServices] DemoReverseProxy proxy,
        CancellationToken cancellationToken) =>
        proxy.ProxyAsync(instanceId, path, context, cancellationToken);

    internal static async Task<OutcomeCandidateSnapshot?>
        EnsureDeliveryPreviewCurrentAsync(
        FlowRun flow,
        IReviewedCandidateService reviewedCandidateService,
        CancellationToken cancellationToken)
    {
        if (flow.Kind != FlowKind.Delivery)
        {
            return null;
        }

        var identity = ReviewedCandidateLedger.Read(flow);
        return await reviewedCandidateService.VerifyPreviewAsync(
            flow,
            identity,
            cancellationToken);
    }

    private static async Task<byte[]> ReadVerifiedPreviewFileAsync(
        FlowRun flow,
        string filePath,
        OutcomeCandidateSnapshot verified,
        CancellationToken cancellationToken)
    {
        var workspace = Path.GetFullPath(flow.WorkspacePath);
        CandidateFingerprintService.ValidateLinksStayInside(workspace);
        var relativePath = Path.GetRelativePath(workspace, filePath)
            .Replace('\\', '/');
        var entries = verified.Manifest.PreviewArtifacts
            .Where(item => string.Equals(
                item.RelativePath.Replace('\\', '/'),
                relativePath,
                StringComparison.Ordinal))
            .ToArray();
        if (entries.Length != 1)
        {
            throw new CandidateValidationException(
                entries.Length == 0
                    ? "The requested preview file is not part of the reviewed candidate."
                    : "The reviewed candidate contains duplicate preview file identities.");
        }

        var bytes = await File.ReadAllBytesAsync(
            filePath,
            cancellationToken);
        var digest =
            "sha256:" +
            Convert.ToHexString(SHA256.HashData(bytes))
                .ToLowerInvariant();
        if (entries[0].Length != bytes.LongLength ||
            !string.Equals(
                entries[0].Digest,
                digest,
                StringComparison.Ordinal))
        {
            throw new CandidateValidationException(
                "The requested preview file changed after candidate verification.");
        }
        return bytes;
    }

    internal static byte[] ApplyPreviewSecurityLayer(
        byte[] bytes,
        string contentType)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        if (!contentType.StartsWith(
                "text/html",
                StringComparison.OrdinalIgnoreCase))
        {
            return bytes;
        }

        var document = Encoding.UTF8.GetString(bytes);
        const string marker = "data-ai-harness-preview-bootstrap";
        if (document.Contains(marker, StringComparison.Ordinal))
        {
            return bytes;
        }
        const string bootstrap = """
            <script data-ai-harness-preview-bootstrap>
            (() => {
              const createStorage = () => {
                const values = new Map();
                return {
                  get length() { return values.size; },
                  clear() { values.clear(); },
                  getItem(key) {
                    const normalized = String(key);
                    return values.has(normalized) ? values.get(normalized) : null;
                  },
                  key(index) {
                    const keys = Array.from(values.keys());
                    return Number.isInteger(index) && index >= 0 && index < keys.length
                      ? keys[index]
                      : null;
                  },
                  removeItem(key) { values.delete(String(key)); },
                  setItem(key, value) { values.set(String(key), String(value)); }
                };
              };
              for (const name of ["localStorage", "sessionStorage"]) {
                let available = false;
                try {
                  const storage = window[name];
                  const probe = "__ai_harness_preview_probe__";
                  storage.setItem(probe, probe);
                  storage.removeItem(probe);
                  available = true;
                } catch {}
                if (!available) {
                  Object.defineProperty(window, name, {
                    configurable: true,
                    value: createStorage()
                  });
                }
              }
            })();
            </script>
            """;
        var head = document.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
        if (head >= 0)
        {
            var headEnd = document.IndexOf('>', head);
            if (headEnd >= 0)
            {
                document = document.Insert(
                    headEnd + 1,
                    Environment.NewLine + bootstrap);
                return Encoding.UTF8.GetBytes(document);
            }
        }
        return Encoding.UTF8.GetBytes(bootstrap + Environment.NewLine + document);
    }

    internal static IResult GetIsolatedPreviewView(
        Guid flowId,
        string artifactId,
        HttpContext httpContext) =>
        CreateIsolatedPreviewView(
            $"/api/previews/{flowId:D}/artifacts/{Uri.EscapeDataString(artifactId)}/index.html",
            httpContext,
            "Customer preview");

    private static IResult CreateIsolatedPreviewView(
        string artifactUrl,
        HttpContext httpContext,
        string title)
    {
        ApplyIsolatedPreviewViewSecurityHeaders(httpContext.Response);
        var document = $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{{title}}</title>
              <style>
                html,body,iframe{box-sizing:border-box;width:100%;height:100%;margin:0;border:0;background:#fff}
              </style>
            </head>
            <body>
              <iframe
                src="{{artifactUrl}}"
                title="Interactive customer preview"
                sandbox="allow-scripts"
                referrerpolicy="no-referrer"></iframe>
            </body>
            </html>
            """;
        return Results.Content(document, "text/html; charset=utf-8");
    }

    internal static void ApplyPreviewArtifactSecurityHeaders(
        HttpResponse response)
    {
        response.Headers["Content-Security-Policy"] =
            "sandbox allow-scripts; default-src 'self' data: blob:; " +
            "script-src 'self' 'unsafe-inline' data: blob:; " +
            "style-src 'self' 'unsafe-inline' data:; " +
            "img-src 'self' data: blob:; font-src 'self' data:; " +
            "media-src 'self' data: blob:; connect-src 'none'; " +
            "form-action 'none'; object-src 'none'; base-uri 'none'; " +
            "frame-src 'none'; child-src 'none'; worker-src 'none'; " +
            "manifest-src 'none'; frame-ancestors 'self'";
        ApplyCommonPreviewSecurityHeaders(response);
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Cross-Origin-Resource-Policy"] = "cross-origin";
    }

    internal static void ApplyIsolatedPreviewViewSecurityHeaders(
        HttpResponse response)
    {
        response.Headers["Content-Security-Policy"] =
            "default-src 'none'; frame-src 'self'; " +
            "style-src 'unsafe-inline'; connect-src 'none'; form-action 'none'; " +
            "object-src 'none'; base-uri 'none'; " +
            "frame-ancestors 'none'";
        ApplyCommonPreviewSecurityHeaders(response);
    }

    internal static IResult ResolveAdvisoryArtifactResult(
        Guid flowId,
        string artifactId,
        string? path,
        bool download,
        HttpResponse response,
        IReadOnlyList<AdvisoryArtifact> artifacts)
    {
        var normalizedPath = (path ?? string.Empty).Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalizedPath) ||
            normalizedPath.StartsWith('/') ||
            normalizedPath.Split('/', StringSplitOptions.None).Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." ||
                segment.Any(char.IsControl)))
        {
            throw new UnauthorizedAccessException(
                "Advisory artifact paths must remain inside the accepted artifact set.");
        }

        var artifact = artifacts.SingleOrDefault(item =>
            string.Equals(
                ApiMappings.AdvisoryArtifactId(item.Path),
                artifactId,
                StringComparison.Ordinal) &&
            string.Equals(
                item.Path.Replace('\\', '/'),
                normalizedPath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            ?? throw new FileNotFoundException(
                $"Advisory artifact '{artifactId}' was not found.");
        if (!File.Exists(artifact.FullPath))
        {
            throw new FileNotFoundException(
                $"Advisory artifact '{artifact.Path}' is missing.");
        }

        var contentType = artifact.MediaType switch
        {
            "text/plain" => "text/plain; charset=utf-8",
            "text/markdown" => "text/markdown; charset=utf-8",
            "text/csv" => "text/csv; charset=utf-8",
            "application/json" => "application/json; charset=utf-8",
            _ => throw new InvalidOperationException(
                "The accepted Advisory artifact has an unsupported media type.")
        };
        ApplyAdvisoryArtifactSecurityHeaders(response);
        var disposition = new ContentDispositionHeaderValue(
            download ? "attachment" : "inline")
        {
            FileNameStar = artifact.Path.Split('/').Last()
        };
        response.Headers.ContentDisposition = disposition.ToString();
        return Results.File(
            artifact.FullPath,
            contentType,
            enableRangeProcessing: true);
    }

    internal static void ApplyAdvisoryArtifactSecurityHeaders(
        HttpResponse response)
    {
        response.Headers["Content-Security-Policy"] =
            "sandbox; default-src 'none'; form-action 'none'; object-src 'none'; " +
            "base-uri 'none'; frame-ancestors 'none'";
        response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        response.Headers["Permissions-Policy"] =
            "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["X-Download-Options"] = "noopen";
        response.Headers.CacheControl = "no-store";
    }

    private static void ApplyCommonPreviewSecurityHeaders(HttpResponse response)
    {
        response.Headers["Cross-Origin-Opener-Policy"] = "noopener-allow-popups";
        response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        response.Headers["Permissions-Policy"] =
            "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers.CacheControl = "no-store";
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
                   .Include(item => item.TaskProfiles)
                   .Include(item => item.PlanDocuments)
                   .Include(item => item.LinkedFlowRuns)
                   .ThenInclude(item => item.Steps)
                   .Include(item => item.LinkedFlowRuns)
                   .ThenInclude(item => item.GateRecords)
                   .SingleOrDefaultAsync(item => item.Id == flowId, cancellationToken)
               ?? throw new KeyNotFoundException($"Factory flow '{flowId}' was not found.");
    }

    private static WorkflowStatusDto ToDto(WorkflowRuntimeStatus status) =>
        new(
            status.Ready,
            status.CurrentFileValid,
            status.HasEffectiveDefinition,
            status.SourcePath,
            status.EffectiveLoadedAt,
            status.EffectiveRevision,
            status.CurrentFileError,
            status.LoadedAt,
            status.LastError,
            status.MaxConcurrentAgents,
            status.MaxAttempts,
            status.WorkspaceRoot);

    private static AgentCatalogStatusDto ToDto(AgentCatalogRuntimeStatus status) =>
        new(
            status.Ready,
            status.HasEffectiveCatalog,
            status.EffectiveRevision,
            status.LoadedAt,
            status.LastError,
            status.ValidDefinitionCount,
            status.InvalidDefinitionCount);

    private static NewWorkAdmissionStatusDto ToDto(NewWorkAdmissionStatus status) =>
        new(status.Ready, status.Failures, status.CheckedAt);

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

}

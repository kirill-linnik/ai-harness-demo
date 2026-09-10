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
        api.MapPost("/flows/{flowId:guid}/review", ReviewFlowAsync);
        api.MapGet("/flows/{flowId:guid}/review-result", GetReviewResultAsync);
        api.MapPost(
            "/flows/{flowId:guid}/qualification-resolution",
            ResolveQualificationAsync);
        api.MapGet(
            "/flows/{flowId:guid}/artifacts/{artifactId}/{**path}",
            GetFlowArtifactAsync);
        api.MapPost("/flows/{flowId:guid}/feedback", AddFeedbackAsync);
        api.MapPost("/flows/{flowId:guid}/decision", DecideFlowAsync);
        api.MapPost(
            "/flows/{flowId:guid}/outcome-resolution",
            ResolveOutcomeAsync);
        api.MapPost("/flows/{flowId:guid}/abandon", AbandonFlowAsync);
        api.MapGet("/history", GetHistoryAsync);
        api.MapGet("/learnings", GetLearningsAsync);
        api.MapGet("/previews/{flowId:guid}", GetPreviewAsync);
        api.MapGet(
            "/previews/{flowId:guid}/artifacts/{artifactId}/view",
            GetIsolatedPreviewView);
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
        settings.RuntimeMarker = "LiveCopilot";
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
        FlowAgentSnapshotService snapshots,
        CancellationToken cancellationToken)
    {
        var status = await catalog.ReloadAsync(cancellationToken);
        if (status.Ready)
        {
            await snapshots.MigrateLegacyNonterminalAsync(cancellationToken);
        }
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
        return Results.Ok(flows.Select(item => item.ToSummaryDto()));
    }

    private static async Task<IResult> GetFlowAsync(
        Guid flowId,
        IDbContextFactory<HarnessDbContext> databaseFactory,
        PreviewArtifactCatalog artifactCatalog,
        IReviewedCandidateService reviewedCandidateService,
        CancellationToken cancellationToken)
    {
        var flow = await LoadFlowAsync(databaseFactory, flowId, cancellationToken);
        var reviewedPreviewUrl = await ResolveReviewedPreviewUrlAsync(
            flow,
            artifactCatalog,
            reviewedCandidateService,
            cancellationToken);
        return Results.Ok(flow.ToDetailDto(reviewedPreviewUrl));
    }

    internal static async Task<string?> ResolveReviewedPreviewUrlAsync(
        FlowRun flow,
        PreviewArtifactCatalog artifactCatalog,
        IReviewedCandidateService reviewedCandidateService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(artifactCatalog);
        ArgumentNullException.ThrowIfNull(reviewedCandidateService);
        if (!string.Equals(
                flow.ContractVersion,
                "studio-v2",
                StringComparison.Ordinal) ||
            flow.Kind != FlowKind.Delivery ||
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
            var reviewed = await EnsureStudioDeliveryPreviewCurrentAsync(
                flow,
                reviewedCandidateService,
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

    private static async Task<IResult> AddFeedbackAsync(
        Guid flowId,
        FeedbackRequest request,
        FeedbackCoordinator coordinator,
        ReviewCoordinator reviewCoordinator,
        IDbContextFactory<HarnessDbContext> databaseFactory,
        CancellationToken cancellationToken)
    {
        if (!await IsStudioV2Async(
                flowId,
                databaseFactory,
                cancellationToken))
        {
            return Results.Ok(
                await coordinator.RespondAsync(
                    flowId,
                    request.Message,
                    cancellationToken));
        }

        var result = await reviewCoordinator.RespondToFeedbackAsync(
            flowId,
            request.Message,
            cancellationToken);
        return Results.Ok(new FeedbackResponse(
            result.Flow.ToDetailDto(),
            result.Reply,
            result.ShouldSpeak));
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
        if (!string.Equals(
                flow.ContractVersion,
                "studio-v2",
                StringComparison.Ordinal) ||
            flow.Kind != FlowKind.Advisory)
        {
            throw new InvalidOperationException(
                "Generic flow artifacts are available only for studio-v2 Advisory flows.");
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

    private static async Task<IResult> AbandonFlowAsync(
        Guid flowId,
        FlowAbandonmentService abandonment,
        CancellationToken cancellationToken) =>
        Results.Ok(await abandonment.AbandonAsync(flowId, cancellationToken));

    internal static async Task<IResult> DecideFlowAsync(
        Guid flowId,
        FlowDecisionRequest request,
        FeedbackCoordinator coordinator,
        ReviewCoordinator reviewCoordinator,
        IDbContextFactory<HarnessDbContext> databaseFactory,
        CancellationToken cancellationToken)
    {
        if (await IsStudioV2Async(
                flowId,
                databaseFactory,
                cancellationToken))
        {
            var result = await reviewCoordinator.ReviewAsync(
                flowId,
                new DirectReviewRequest
                {
                    GateId = request.GateId,
                    Intent = request.Approve
                        ? ReviewIntent.Accept
                        : ReviewIntent.RequestRefinement,
                    Refinement = request.Approve
                        ? null
                        : new DirectReviewRefinement
                        {
                            RequestedChanges =
                            [
                                string.IsNullOrWhiteSpace(request.Feedback)
                                    ? "Revise the result for another customer review."
                                    : request.Feedback
                            ]
                        }
                },
                cancellationToken);
            return Results.Ok(new FlowDecisionResponse(
                request.Approve
                    ? ReleaseDecisionOutcome.Approved
                    : ReleaseDecisionOutcome.Rejected,
                result.Flow.ToDetailDto(),
                result.Review.Message));
        }

        var decision = await coordinator.DecideAsync(
            flowId,
            request.Approve,
            request.GateId,
            request.CandidateFingerprint,
            request.Feedback,
            cancellationToken);
        return ToFlowDecisionResult(decision);
    }

    private static async Task<bool> IsStudioV2Async(
        Guid flowId,
        IDbContextFactory<HarnessDbContext> databaseFactory,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var contractVersion = await database.Flows
            .AsNoTracking()
            .Where(flow => flow.Id == flowId)
            .Select(flow => flow.ContractVersion)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException(
                $"Factory flow '{flowId}' was not found.");
        return string.Equals(
            contractVersion,
            "studio-v2",
            StringComparison.Ordinal);
    }

    internal static IResult ToFlowDecisionResult(FlowDecisionResponse decision) =>
        decision.Outcome == ReleaseDecisionOutcome.Conflict
            ? Results.Json(
                decision,
                statusCode: StatusCodes.Status409Conflict)
            : Results.Ok(decision);

    private static async Task<IResult> ResolveOutcomeAsync(
        Guid flowId,
        OutcomeResolutionRequest request,
        WorkflowEngine engine,
        FlowQueue queue,
        CancellationToken cancellationToken)
    {
        var flow = await engine.ResolveOutcomeAsync(
            flowId,
            request.GateId,
            request.Action,
            request.Reason,
            cancellationToken);
        if (!queue.Queue(flowId))
        {
            throw new InvalidOperationException(
                "Unable to queue the resolved outcome-verification flow.");
        }
        return Results.Accepted($"/api/flows/{flowId}", flow.ToDetailDto());
    }

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
        var history = attempts.Select(item =>
        {
            var flow = flows[item.FlowId];
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
                item.PushbackReason);
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
        WorkflowEngine workflowEngine,
        FlowQueue flowQueue,
        IReviewedCandidateService reviewedCandidateService,
        CancellationToken cancellationToken)
    {
        var flow = await LoadFlowAsync(databaseFactory, flowId, cancellationToken);
        if (flow.Status is not (FlowStatus.WaitingForFeedback or FlowStatus.Approved))
        {
            throw new InvalidOperationException("This flow does not have a customer preview yet.");
        }
        var reviewedSnapshot =
            await EnsureStudioDeliveryPreviewCurrentAsync(
            flow,
            reviewedCandidateService,
            cancellationToken);
        var legacyFlow = string.Equals(
            flow.ContractVersion,
            "legacy-v1",
            StringComparison.Ordinal);
        if (legacyFlow &&
            !string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson))
        {
            var state = AiHarnessDemo.Core.Verification.OutcomeVerificationRules
                .DeserializeAggregate(flow.OutcomeVerificationJson);
            if (state.Status !=
                AiHarnessDemo.Core.Verification.OutcomeVerificationStatus.Passed)
            {
                throw new InvalidOperationException(
                    "Customer preview is available only after a current authoritative QA PASS.");
            }
            if (!await workflowEngine.EnsureVerifiedCandidateCurrentAsync(
                    flowId,
                    cancellationToken))
            {
                if (flow.Status != FlowStatus.Approved &&
                    !flowQueue.Queue(flowId))
                {
                    throw new InvalidOperationException(
                        "Unable to queue stale-candidate refresh.");
                }
                throw new InvalidOperationException(
                    flow.Status == FlowStatus.Approved
                        ? "The local historical preview no longer matches the approved candidate; the published outcome remains unchanged."
                        : "The candidate changed after QA; preview access is blocked until refresh and re-verification complete.");
            }
        }

        var outcomeState = string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson)
            ? null
            : AiHarnessDemo.Core.Verification.OutcomeVerificationRules
                .DeserializeAggregate(flow.OutcomeVerificationJson);
        var deliveredBy = flow.Steps
            .Where(item => item.Status == StepStatus.Completed)
            .OrderBy(item => item.Iteration)
            .ThenBy(item => item.Sequence)
            .Select(item => item.ToDto(outcomeState))
            .ToList();
        var outcomeVerification = flow.ToOutcomeVerificationDto();
        var artifacts = flow.Kind == FlowKind.Advisory
            ? advisoryArtifactCatalog.Discover(flow)
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
                        Interactive: false);
                })
                .ToList()
            : (reviewedSnapshot is null
                ? artifactCatalog.Discover(flow)
                : artifactCatalog.DiscoverVerified(
                    flow,
                    reviewedSnapshot.Manifest.PreviewArtifacts))
                .Select(item => new PreviewArtifactDto(
                    item.Id,
                    item.Label,
                    item.Url,
                    item.OpenUrl,
                    "text/html",
                    null,
                    null,
                    Interactive: true))
                .ToList();
        if (outcomeVerification.PreviewRequired && artifacts.Count == 0)
        {
            throw new InvalidOperationException(
                "The verified customer-visible outcome has no preview artifact.");
        }
        return Results.Ok(new PreviewDto(
            flow.Id,
            flow.Title,
            flow.ConsolidatedRequest,
            Path.GetFileName(flow.RepositoryPath),
            flow.Kind,
            flow.ContractVersion,
            flow.Iteration,
            flow.Status,
            flow.OutcomeLabel,
            flow.ToFlowOutcomeDto(),
            artifacts,
            deliveredBy,
            flow.ToReviewSummaryDto(),
            ReviewCoordinator.GetPublicationStatus(flow),
            outcomeVerification,
            flow.UpdatedAt));
    }

    internal static async Task<IResult> GetPreviewArtifactAsync(
        Guid flowId,
        string artifactId,
        string? path,
        HttpContext httpContext,
        IDbContextFactory<HarnessDbContext> databaseFactory,
        PreviewArtifactCatalog artifactCatalog,
        WorkflowEngine workflowEngine,
        FlowQueue flowQueue,
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
            await EnsureStudioDeliveryPreviewCurrentAsync(
            flow,
            reviewedCandidateService,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson) &&
            AiHarnessDemo.Core.Verification.OutcomeVerificationRules
                .DeserializeAggregate(flow.OutcomeVerificationJson)
                .Status !=
            AiHarnessDemo.Core.Verification.OutcomeVerificationStatus.Passed)
        {
            throw new InvalidOperationException(
                "Customer preview artifacts are unavailable before a current QA PASS.");
        }
        if (!string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson) &&
            !await workflowEngine.EnsureVerifiedCandidateCurrentAsync(
                flowId,
                cancellationToken))
        {
            if (flow.Status != FlowStatus.Approved &&
                !flowQueue.Queue(flowId))
            {
                throw new InvalidOperationException(
                    "Unable to queue stale-candidate refresh.");
            }
            throw new InvalidOperationException(
                flow.Status == FlowStatus.Approved
                    ? "The local historical preview artifact no longer matches the approved candidate; the published outcome remains unchanged."
                    : "The candidate changed after QA; preview artifacts are blocked until refresh and re-verification complete.");
        }

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
            bytes = ApplyPreviewCompatibilityLayer(bytes, contentType);
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
                ApplyPreviewCompatibilityLayer(bytes, contentType),
                contentType,
                enableRangeProcessing: true);
        }
        return Results.File(
            filePath,
            contentType,
            enableRangeProcessing: true);
    }

    internal static async Task<OutcomeCandidateSnapshot?>
        EnsureStudioDeliveryPreviewCurrentAsync(
        FlowRun flow,
        IReviewedCandidateService reviewedCandidateService,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                flow.ContractVersion,
                "studio-v2",
                StringComparison.Ordinal) ||
            flow.Kind != FlowKind.Delivery)
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

    internal static byte[] ApplyPreviewCompatibilityLayer(
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
        HttpContext httpContext)
    {
        ApplyIsolatedPreviewViewSecurityHeaders(httpContext.Response);
        var artifactUrl =
            $"/api/previews/{flowId:D}/artifacts/{Uri.EscapeDataString(artifactId)}/index.html";
        var document = $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Customer preview</title>
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

    private static async Task<IResult> RefreshRuntimeAsync(
        WorkflowDefinitionProvider workflowProvider,
        AgentCatalog catalog,
        CopilotCliRuntime copilotCliRuntime,
        ModelCatalogDiscovery modelCatalog,
        FlowAgentSnapshotService snapshots,
        IDbContextFactory<HarnessDbContext> databaseFactory,
        FlowQueue queue,
        CancellationToken cancellationToken)
    {
        var requestedAt = DateTimeOffset.UtcNow;
        await workflowProvider.ReloadAsync(cancellationToken);
        var workflow = workflowProvider.GetEffective();
        await copilotCliRuntime.RefreshAsync(
            workflow.Config.Copilot.Command,
            cancellationToken);
        await modelCatalog.RefreshAsync(
            Directory.GetCurrentDirectory(),
            cancellationToken);
        var catalogStatus = await catalog.ReloadAsync(cancellationToken);
        if (catalogStatus.Ready)
        {
            await snapshots.MigrateLegacyNonterminalAsync(cancellationToken);
        }

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
            status.WorkspaceRoot,
            status.OutcomeVerificationEnabled,
            status.OutcomeVerificationMaxRounds);

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

using System.Text;
using System.Text.Json;
using AiHarnessDemo.Api;
using AiHarnessDemo.Contracts;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Data;
using AiHarnessDemo.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiHarnessDemo.Tests;

public sealed class ApiProjectionTests
{
    [Fact]
    public void SaveSettings_RejectsNoneAsPersistedDeliveryOutcome()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            DemoApi.ValidateSaveSettingsRequest(new SaveSettingsRequest(
                RepositoryPath: null,
                RepositoryKnowledge: null,
                Outcome: OutcomeType.None,
                MaxHandoffRetries: 2,
                ModelSelectionStrategy:
                    ModelSelectionStrategy.MaximumQuality)));

        Assert.Contains(
            "Commit or PullRequest",
            exception.Message,
            StringComparison.Ordinal);
        DemoApi.ValidateSaveSettingsRequest(new SaveSettingsRequest(
            RepositoryPath: null,
            RepositoryKnowledge: null,
            Outcome: OutcomeType.Commit,
            MaxHandoffRetries: 2,
            ModelSelectionStrategy:
                ModelSelectionStrategy.MaximumQuality));
    }

    [Fact]
    public async Task DemoApi_RegistersTheCompleteSliceNineSurface()
    {
        var builder = WebApplication.CreateBuilder();
        var serviceTypes = new[]
        {
            typeof(IDbContextFactory<HarnessDbContext>),
            typeof(AgentCatalog),
            typeof(WorkflowDefinitionProvider),
            typeof(CopilotCliRuntime),
            typeof(ModelCatalogDiscovery),
            typeof(NewWorkAdmissionService),
            typeof(FlowAgentSnapshotService),
            typeof(RepositoryAnalyzer),
            typeof(IntakeCoordinator),
            typeof(FlowQueue),
            typeof(ReviewCoordinator),
            typeof(QualificationResolutionCoordinator),
            typeof(FeedbackCoordinator),
            typeof(WorkflowEngine),
            typeof(FlowAbandonmentService),
            typeof(PreviewArtifactCatalog),
            typeof(AdvisoryArtifactCatalog),
            typeof(IReviewedCandidateService),
            typeof(DeliveryReadinessService),
            typeof(DemoRuntimeManager),
            typeof(DemoReverseProxy)
        };
        foreach (var serviceType in serviceTypes)
        {
            builder.Services.Add(ServiceDescriptor.Singleton(
                serviceType,
                _ => throw new NotSupportedException()));
        }
        await using var app = builder.Build();
        app.MapDemoApi();
        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => new
            {
                Pattern = endpoint.RoutePattern.RawText,
                Methods = endpoint.Metadata
                    .GetMetadata<HttpMethodMetadata>()?
                    .HttpMethods ?? []
            })
            .ToList();
        var expected = new[]
        {
            ("GET", "/api/bootstrap"),
            ("GET", "/api/agent-catalog"),
            ("POST", "/api/agent-catalog/reload"),
            ("PUT", "/api/agents/{agentId}"),
            ("POST", "/api/intake"),
            ("GET", "/api/flows"),
            ("GET", "/api/flows/{flowId:guid}"),
            ("POST", "/api/flows/{flowId:guid}/restart"),
            ("POST", "/api/flows/{flowId:guid}/abandon"),
            ("POST", "/api/flows/{flowId:guid}/review"),
            ("POST", "/api/flows/{flowId:guid}/readiness-waiver"),
            ("POST", "/api/flows/{flowId:guid}/readiness-resolution"),
            ("POST", "/api/flows/{flowId:guid}/qualification-resolution"),
            ("GET", "/api/flows/{flowId:guid}/review-result"),
            ("GET", "/api/flows/{flowId:guid}/artifacts/{artifactId}/{**path}"),
            ("GET", "/api/previews/{flowId:guid}"),
            ("GET", "/api/previews/{flowId:guid}/artifacts/{artifactId}/demo"),
            ("POST", "/api/previews/{flowId:guid}/artifacts/{artifactId}/demo/start"),
            ("POST", "/api/previews/{flowId:guid}/artifacts/{artifactId}/demo/restart"),
            ("POST", "/api/previews/{flowId:guid}/artifacts/{artifactId}/demo/stop"),
            ("GET", "/api/demos/{instanceId:guid}/{**path}"),
            ("GET", "/api/previews/{flowId:guid}/artifacts/{artifactId}/{**path}")
        };

        foreach (var (method, pattern) in expected)
        {
            Assert.Contains(
                routes,
                route =>
                    string.Equals(route.Pattern, pattern, StringComparison.Ordinal) &&
                    route.Methods.Contains(method, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void StudioFlowProjection_ExposesPlanReviewLineageAndArtifactMetadata()
    {
        var flow = new FlowRun
        {
            Title = "Assess checkout resilience",
            OriginalRequest = "Assess checkout resilience.",
            ConsolidatedRequest = "{}",
            Kind = FlowKind.Advisory,
            ContractVersion = "studio-v2",
            Status = FlowStatus.WaitingForFeedback,
            Iteration = 2,
            AgentCatalogRevision = "sha256:catalog",
            OutcomeOwnerPlanStepKey = "prepare-recommendation",
            OutcomeContractJson =
                """
                {"Version":"flow-outcome-v1","Goal":"Harden checkout retries.","Summary":"Use durable idempotency.","ImplementationDetails":["Persist a request key before payment."],"Artifacts":[{"Path":"recommendations/checkout.md","MediaType":"text/markdown","Content":"# Checkout\nUse idempotency."}]}
                """
        };
        var analysis = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Sequence = 20,
            AgentId = "analyst",
            AgentName = "Analyst",
            AgentRole = "analyst",
            Label = "Inspect the current system",
            PlanStepKey = "inspect-current-system",
            PlanDutiesJson = """["Analyze"]""",
            PlanStage = PlanStage.BeforeReview,
            PermissionProfile = ExecutionPermissionProfile.ReadOnlySource,
            Status = StepStatus.Completed
        };
        var owner = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Sequence = 30,
            AgentId = "architect",
            AgentName = "Architect",
            AgentRole = "architect",
            Label = "Prepare the recommendation",
            PlanStepKey = "prepare-recommendation",
            PlanDutiesJson = """["Analyze","PrepareOutcome"]""",
            PlanStage = PlanStage.BeforeReview,
            IsOutcomeOwner = true,
            PermissionProfile = ExecutionPermissionProfile.ReadOnlySource,
            EffectivePermissionJson = """{"profile":"ReadOnlySource"}""",
            WorkflowRevision = "sha256:workflow",
            DependsOnStepId = analysis.Id,
            Status = StepStatus.Completed
        };
        flow.Steps.AddRange([analysis, owner]);
        flow.TaskProfiles.Add(new TaskProfile
        {
            FlowRunId = flow.Id,
            FlowStepId = owner.Id,
            Iteration = flow.Iteration,
            PlanStepKey = owner.PlanStepKey,
            AgentId = owner.AgentId,
            Role = owner.AgentRole,
            Complexity = 8,
            ReasoningDepth = 9,
            ContextDemand = 8,
            ToolIntensity = 2,
            TaskTypeTagsJson = """["Architecture"]""",
            Risk = TaskRisk.High,
            RiskReason = "The recommendation affects a critical boundary.",
            Confidence = .8,
            RationalesJson = """["Repository evidence is required."]"""
        });
        var gate = new HandoffGateRecord
        {
            FlowRunId = flow.Id,
            FlowStepId = owner.Id,
            ActionType = HandoffActionType.CustomerReview,
            Decision = HandoffGateDecision.AwaitingHumanApproval,
            TrustLevelAtDecision = HandoffTrustLevel.Gated,
            Summary = "Recommendation ready."
        };
        flow.GateRecords.Add(gate);
        var child = new FlowRun
        {
            Title = "Implement checkout resilience",
            OriginalRequest = "Implement the accepted recommendation.",
            Kind = FlowKind.Delivery,
            ContractVersion = "studio-v2",
            ParentFlowRunId = flow.Id,
            ParentIteration = flow.Iteration,
            LinkKind = FlowLinkKind.AdvisoryPromotion,
            Status = FlowStatus.Intake
        };
        flow.LinkedFlowRuns.Add(child);

        var detail = flow.ToDetailDto();
        var projectedOwner = Assert.Single(
            detail.Steps,
            step => step.PlanStepKey == "prepare-recommendation");

        Assert.Equal(FlowKind.Advisory, detail.Kind);
        Assert.Equal("sha256:catalog", detail.AgentCatalogRevision);
        Assert.Equal("prepare-recommendation", detail.OutcomeOwnerPlanStepKey);
        Assert.True(detail.Review.Available);
        Assert.False(detail.Review.Resolved);
        Assert.Equal(gate.Id, detail.Review.GateId);
        Assert.Equal(
            [PlanDuty.Analyze, PlanDuty.PrepareOutcome],
            projectedOwner.Duties);
        Assert.True(projectedOwner.IsOutcomeOwner);
        Assert.Equal(
            ExecutionPermissionProfile.ReadOnlySource,
            projectedOwner.PermissionProfile);
        Assert.Equal([analysis.Id], projectedOwner.DependencyStepIds);
        Assert.Equal(
            ["inspect-current-system"],
            projectedOwner.DependencyPlanStepKeys);
        Assert.Equal(owner.PlanStepKey, projectedOwner.TaskProfile?.PlanStepKey);
        Assert.Equal(owner.AgentId, projectedOwner.TaskProfile?.AgentId);
        Assert.Equal(
            FlowLinkKind.AdvisoryPromotion,
            Assert.Single(detail.LinkedFlows).LinkKind);

        var outcome = Assert.IsType<FlowOutcomeDto>(detail.OutcomeResult);
        var artifact = Assert.Single(outcome.Artifacts);
        Assert.Equal("text/markdown", artifact.MediaType);
        Assert.Contains(
            $"/api/flows/{flow.Id:D}/artifacts/{artifact.Id}/recommendations/checkout.md",
            artifact.Url,
            StringComparison.Ordinal);
        Assert.EndsWith("?download=true", artifact.DownloadUrl);
    }

    [Fact]
    public void ReviewProjection_DistinguishesRunningPublicationAfterAcceptance()
    {
        var flow = new FlowRun
        {
            Title = "Deliver checkout resilience",
            OriginalRequest = "Implement checkout resilience.",
            Kind = FlowKind.Delivery,
            ContractVersion = "studio-v2",
            Status = FlowStatus.Running,
            PublicationPlanStepKey = "publish-approved-result"
        };
        var owner = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Sequence = 20,
            AgentId = "builder",
            AgentName = "Builder",
            AgentRole = "builder",
            PlanStepKey = "prepare-outcome",
            PlanDutiesJson = """["PrepareOutcome"]""",
            IsOutcomeOwner = true,
            Status = StepStatus.Completed
        };
        var publication = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Sequence = 30,
            AgentId = "publisher",
            AgentName = "Publisher",
            AgentRole = "publisher",
            PlanStepKey = "publish-approved-result",
            PlanDutiesJson = """["Publish"]""",
            PlanStage = PlanStage.AfterApproval,
            PermissionProfile = ExecutionPermissionProfile.Publish,
            Status = StepStatus.Running
        };
        flow.Steps.AddRange([owner, publication]);
        flow.GateRecords.Add(new HandoffGateRecord
        {
            FlowRunId = flow.Id,
            FlowStepId = owner.Id,
            ActionType = HandoffActionType.CustomerReview,
            Decision = HandoffGateDecision.AutoApproved,
            ReviewDecision = ReviewDecision.Accepted,
            TrustLevelAtDecision = HandoffTrustLevel.Gated,
            Resolved = true,
            Approved = true
        });

        var review = flow.ToReviewSummaryDto();

        Assert.True(review.Resolved);
        Assert.Equal(ReviewDecision.Accepted, review.Decision);
        Assert.Equal(ReviewPublicationStatus.Running, review.PublicationStatus);
    }

    [Fact]
    public async Task AdvisoryArtifactResponse_RejectsTraversalAndUsesMetadataMediaType()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-api-artifact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "report.html");
        await File.WriteAllTextAsync(file, "<script>alert('not executable')</script>");
        var artifact = new AdvisoryArtifact(
            "report.html",
            "text/plain",
            Encoding.UTF8.GetByteCount(await File.ReadAllTextAsync(file)),
            file);
        var flowId = Guid.NewGuid();
        var artifactId = ApiMappings.AdvisoryArtifactId(artifact.Path);

        try
        {
            await using var services = new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider();
            var context = new DefaultHttpContext
            {
                RequestServices = services
            };
            context.Response.Body = new MemoryStream();

            var result = DemoApi.ResolveAdvisoryArtifactResult(
                flowId,
                artifactId,
                artifact.Path,
                download: false,
                context.Response,
                [artifact]);
            await result.ExecuteAsync(context);

            Assert.Equal("text/plain; charset=utf-8", context.Response.ContentType);
            Assert.StartsWith(
                "inline;",
                context.Response.Headers.ContentDisposition.ToString(),
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                "nosniff",
                context.Response.Headers["X-Content-Type-Options"]);
            Assert.Contains(
                "default-src 'none'",
                context.Response.Headers["Content-Security-Policy"].ToString());
            var downloadContext = new DefaultHttpContext
            {
                RequestServices = services
            };
            downloadContext.Response.Body = new MemoryStream();
            var downloadResult = DemoApi.ResolveAdvisoryArtifactResult(
                flowId,
                artifactId,
                artifact.Path,
                download: true,
                downloadContext.Response,
                [artifact]);
            await downloadResult.ExecuteAsync(downloadContext);
            Assert.StartsWith(
                "attachment;",
                downloadContext.Response.Headers.ContentDisposition.ToString(),
                StringComparison.OrdinalIgnoreCase);
            Assert.Throws<UnauthorizedAccessException>(() =>
                DemoApi.ResolveAdvisoryArtifactResult(
                    flowId,
                    artifactId,
                    "../report.html",
                    download: false,
                    new DefaultHttpContext().Response,
                    [artifact]));
            Assert.Throws<InvalidOperationException>(() =>
                DemoApi.ResolveAdvisoryArtifactResult(
                    flowId,
                    artifactId,
                    artifact.Path,
                    download: false,
                    new DefaultHttpContext().Response,
                    [artifact with { MediaType = "text/html" }]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StudioDeliveryPreviewEndpoints_ServeOnlyTheCurrentReviewedSeal()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"studio-preview-endpoint-{Guid.NewGuid():N}");
        var browserRoot = Path.Combine(
            root,
            ".customer-preview",
            "eu",
            "browser");
        Directory.CreateDirectory(browserRoot);
        await File.WriteAllTextAsync(
            Path.Combine(browserRoot, "index.html"),
            "<h1>sealed preview</h1>");
        await using var connection =
            new Microsoft.Data.Sqlite.SqliteConnection(
                "Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new PreviewDbContextFactory(
            new DbContextOptionsBuilder<HarnessDbContext>()
                .UseSqlite(connection)
                .Options);
        var flow = CreatePreviewFlow(root, includeSeal: true);
        await using (var database = await factory.CreateDbContextAsync())
        {
            await database.Database.EnsureCreatedAsync();
            database.Flows.Add(flow);
            await database.SaveChangesAsync();
        }
        var verifier = new PreviewReviewedCandidateService();

        try
        {
            var metadata = await DemoApi.GetPreviewAsync(
                flow.Id,
                factory,
                new PreviewArtifactCatalog(),
                null!,
                null!,
                new FlowQueue(),
                verifier,
                CancellationToken.None);
            Assert.IsAssignableFrom<IValueHttpResult>(metadata);

            using var services = new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider();
            var context = NewContext(
                $"/api/previews/{flow.Id:D}/artifacts/eu/index.html");
            context.RequestServices = services;
            var artifact = await DemoApi.GetPreviewArtifactAsync(
                flow.Id,
                "eu",
                "index.html",
                context,
                factory,
                new PreviewArtifactCatalog(),
                null!,
                new FlowQueue(),
                verifier,
                CancellationToken.None);
            await artifact.ExecuteAsync(context);
            context.Response.Body.Position = 0;
            var body = await new StreamReader(
                    context.Response.Body,
                    leaveOpen: true)
                .ReadToEndAsync();

            Assert.Contains(
                "data-ai-harness-preview-bootstrap",
                body,
                StringComparison.Ordinal);
            Assert.Contains(
                "<h1>sealed preview</h1>",
                body,
                StringComparison.Ordinal);
            Assert.Equal(2, verifier.VerifyCalls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StudioDeliveryPreviewEndpoints_RejectMissingOrMutatedSealWithoutBytes()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"studio-preview-rejection-{Guid.NewGuid():N}");
        var browserRoot = Path.Combine(
            root,
            ".customer-preview",
            "eu",
            "browser");
        Directory.CreateDirectory(browserRoot);
        await File.WriteAllTextAsync(
            Path.Combine(browserRoot, "index.html"),
            "unreviewed bytes");
        await using var connection =
            new Microsoft.Data.Sqlite.SqliteConnection(
                "Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new PreviewDbContextFactory(
            new DbContextOptionsBuilder<HarnessDbContext>()
                .UseSqlite(connection)
                .Options);
        var flow = CreatePreviewFlow(root, includeSeal: false);
        await using (var database = await factory.CreateDbContextAsync())
        {
            await database.Database.EnsureCreatedAsync();
            database.Flows.Add(flow);
            await database.SaveChangesAsync();
        }
        var verifier = new PreviewReviewedCandidateService();
        var context = NewContext(
            $"/api/previews/{flow.Id:D}/artifacts/eu/index.html");

        try
        {
            await Assert.ThrowsAsync<CandidateValidationException>(() =>
                DemoApi.GetPreviewAsync(
                    flow.Id,
                    factory,
                    new PreviewArtifactCatalog(),
                    null!,
                    null!,
                    new FlowQueue(),
                    verifier,
                    CancellationToken.None));
            await Assert.ThrowsAsync<CandidateValidationException>(() =>
                DemoApi.GetPreviewArtifactAsync(
                    flow.Id,
                    "eu",
                    "index.html",
                    context,
                    factory,
                    new PreviewArtifactCatalog(),
                    null!,
                    new FlowQueue(),
                    verifier,
                    CancellationToken.None));
            Assert.Equal(0, context.Response.Body.Length);

            await using (var database =
                         await factory.CreateDbContextAsync())
            {
                var stored = await database.Flows
                    .Include(item => item.Events)
                    .Include(item => item.Steps)
                    .SingleAsync(item => item.Id == flow.Id);
                var owner = Assert.Single(stored.Steps);
                stored.Events.Add(CreateReviewedSeal(stored, owner));
                await database.SaveChangesAsync();
            }
            verifier.FailVerification = true;
            await Assert.ThrowsAsync<CandidateValidationException>(() =>
                DemoApi.GetPreviewAsync(
                    flow.Id,
                    factory,
                    new PreviewArtifactCatalog(),
                    null!,
                    null!,
                    new FlowQueue(),
                    verifier,
                    CancellationToken.None));
            await Assert.ThrowsAsync<CandidateValidationException>(() =>
                DemoApi.GetPreviewArtifactAsync(
                    flow.Id,
                    "eu",
                    "index.html",
                    context,
                    factory,
                    new PreviewArtifactCatalog(),
                    null!,
                    new FlowQueue(),
                    verifier,
                    CancellationToken.None));
            Assert.Equal(0, context.Response.Body.Length);

            verifier.FailVerification = false;
            verifier.MutateAfterVerification = true;
            await Assert.ThrowsAsync<CandidateValidationException>(() =>
                DemoApi.GetPreviewArtifactAsync(
                    flow.Id,
                    "eu",
                    "index.html",
                    context,
                    factory,
                    new PreviewArtifactCatalog(),
                    null!,
                    new FlowQueue(),
                    verifier,
                    CancellationToken.None));
            Assert.Equal(0, context.Response.Body.Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StudioDeliveryProjection_ExposesOnlyCurrentReviewedBrowserPreview()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"studio-reviewed-preview-projection-{Guid.NewGuid():N}");
        var browserRoot = Path.Combine(
            root,
            ".customer-preview",
            "eu",
            "browser");
        Directory.CreateDirectory(browserRoot);
        await File.WriteAllTextAsync(
            Path.Combine(browserRoot, "index.html"),
            "<h1>reviewed preview</h1>");
        var catalog = new PreviewArtifactCatalog();
        var verifier = new PreviewReviewedCandidateService();

        try
        {
            var reviewed = CreatePreviewFlow(root, includeSeal: true);
            var url = await DemoApi.ResolveReviewedPreviewUrlAsync(
                reviewed,
                catalog,
                verifier,
                CancellationToken.None);

            Assert.Equal($"#/preview/{reviewed.Id:D}", url);
            Assert.Equal(1, verifier.VerifyCalls);

            reviewed.Status = FlowStatus.Approved;
            Assert.Null(await DemoApi.ResolveReviewedPreviewUrlAsync(
                reviewed,
                catalog,
                verifier,
                CancellationToken.None));
            Assert.Equal(1, verifier.VerifyCalls);

            var missingSeal = CreatePreviewFlow(root, includeSeal: false);
            Assert.Null(await DemoApi.ResolveReviewedPreviewUrlAsync(
                missingSeal,
                catalog,
                verifier,
                CancellationToken.None));

            var noBrowser = CreatePreviewFlow(root, includeSeal: true);
            verifier.NoPreviewArtifacts = true;
            Assert.Null(await DemoApi.ResolveReviewedPreviewUrlAsync(
                noBrowser,
                catalog,
                verifier,
                CancellationToken.None));
            verifier.NoPreviewArtifacts = false;

            var legacy = CreatePreviewFlow(root, includeSeal: true);
            legacy.ContractVersion = "legacy-v1";
            Assert.Null(await DemoApi.ResolveReviewedPreviewUrlAsync(
                legacy,
                catalog,
                verifier,
                CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApiExceptionHandler_MapsLifecycleAndAdmissionFailuresToProblemDetails()
    {
        var handler = new ApiExceptionHandler(
            NullLogger<ApiExceptionHandler>.Instance);
        var lifecycleContext = NewContext("/api/flows/flow/review");
        var admissionContext = NewContext("/api/intake");
        var demoContext = NewContext(
            $"/api/demos/{Guid.NewGuid():D}/missing.js");

        Assert.True(await handler.TryHandleAsync(
            lifecycleContext,
            new InvalidOperationException("The review is stale."),
            CancellationToken.None));
        Assert.Equal(StatusCodes.Status409Conflict, lifecycleContext.Response.StatusCode);
        Assert.StartsWith(
            "application/problem+json",
            lifecycleContext.Response.ContentType,
            StringComparison.OrdinalIgnoreCase);

        Assert.True(await handler.TryHandleAsync(
            admissionContext,
            new NewWorkAdmissionException(["Agent catalog is not ready."]),
            CancellationToken.None));
        Assert.Equal(
            StatusCodes.Status503ServiceUnavailable,
            admissionContext.Response.StatusCode);
        admissionContext.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(
            admissionContext.Response.Body);
        Assert.Contains(
            "Agent catalog is not ready.",
            document.RootElement
                .GetProperty("failures")
                .EnumerateArray()
                .Select(item => item.GetString()));

        Assert.True(await handler.TryHandleAsync(
            demoContext,
            new DemoRuntimeException(
                DemoConflictCodes.InvalidTransition,
                "The demo is stopped."),
            CancellationToken.None));
        Assert.Equal(
            DemoReverseProxy.IsolationPolicy,
            demoContext.Response.Headers.ContentSecurityPolicy);
        Assert.DoesNotContain(
            "allow-same-origin",
            demoContext.Response.Headers.ContentSecurityPolicy.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiExceptionHandler_ReturnsPersistedIntakeIdentityWithoutRawFailure()
    {
        var handler = new ApiExceptionHandler(
            NullLogger<ApiExceptionHandler>.Instance);
        var context = NewContext("/api/intake");
        var flowId = Guid.NewGuid();
        const string sensitiveMarker =
            "SENSITIVE_RAW_INTAKE_PROMPT";

        Assert.True(await handler.TryHandleAsync(
            context,
            new IntakeAttemptException(
                flowId,
                FlowStatus.Intake,
                IntakeCoordinator.SafeIntakeRetryMessage,
                new InvalidOperationException(sensitiveMarker)),
            CancellationToken.None));

        Assert.Equal(
            StatusCodes.Status409Conflict,
            context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(
            context.Response.Body);
        Assert.Equal(
            flowId,
            document.RootElement
                .GetProperty("flowId")
                .GetGuid());
        Assert.Equal(
            "Intake",
            document.RootElement
                .GetProperty("flowStatus")
                .GetString());
        Assert.Equal(
            IntakeCoordinator.SafeIntakeRetryMessage,
            document.RootElement
                .GetProperty("retryMessage")
                .GetString());
        Assert.DoesNotContain(
            sensitiveMarker,
            document.RootElement.GetRawText(),
            StringComparison.Ordinal);
    }

    private static DefaultHttpContext NewContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static FlowRun CreatePreviewFlow(
        string workspacePath,
        bool includeSeal)
    {
        var flow = new FlowRun
        {
            Title = "Reviewed Delivery preview",
            OriginalRequest = "Prepare the reviewed preview.",
            ConsolidatedRequest = "Prepare the reviewed preview.",
            ContractVersion = "studio-v2",
            Kind = FlowKind.Delivery,
            Status = FlowStatus.WaitingForFeedback,
            Iteration = 1,
            WorkspacePath = workspacePath,
            RepositoryPath = workspacePath,
            OutcomeOwnerPlanStepKey = "outcome",
            OutcomeContractJson =
                """{"Version":"flow-outcome-v1","Goal":"Preview","Summary":"Ready","ImplementationDetails":["Serve reviewed bytes."],"Artifacts":[]}"""
        };
        var owner = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = 1,
            Sequence = 10,
            AgentId = "builder",
            AgentName = "Builder",
            AgentRole = "builder",
            PlanStepKey = "outcome",
            PlanDutiesJson = """["PrepareOutcome"]""",
            IsOutcomeOwner = true,
            Status = StepStatus.Completed,
            Phase = AgentRunPhase.Succeeded
        };
        flow.Steps.Add(owner);
        flow.GateRecords.Add(new HandoffGateRecord
        {
            FlowRunId = flow.Id,
            FlowStepId = owner.Id,
            ActionType = HandoffActionType.CustomerReview,
            Decision = HandoffGateDecision.AwaitingHumanApproval,
            TrustLevelAtDecision = HandoffTrustLevel.Gated,
            Summary = "Review the sealed Delivery preview."
        });
        if (includeSeal)
        {
            flow.Events.Add(CreateReviewedSeal(flow, owner));
        }
        return flow;
    }

    private static FlowEvent CreateReviewedSeal(
        FlowRun flow,
        FlowStep owner) =>
        new()
        {
            FlowRunId = flow.Id,
            FlowStepId = owner.Id,
            Type = ReviewedCandidateLedger.EventType,
            Message = "Sealed the current Delivery preview.",
            DataJson = ReviewedCandidateLedger.Serialize(
                new ReviewedCandidateIdentity(
                    ReviewedCandidateIdentity.CurrentVersion,
                    flow.Id,
                    flow.Iteration,
                    owner.Id,
                    owner.PlanStepKey,
                    AiHarnessDemo.Core.Verification.OutcomeVerificationRules
                        .ComputeSha256(flow.OutcomeContractJson),
                    AiHarnessDemo.Core.Verification.OutcomeVerificationRules
                        .ComputeSha256($"plan:{flow.Id:D}"),
                    AiHarnessDemo.Core.Verification.OutcomeVerificationRules
                        .ComputeSha256($"candidate:{flow.Id:D}"),
                    0,
                    0,
                    1,
                    24,
                    [
                        new ReviewedCandidateRepositoryIdentity(
                            ".",
                            new string('1', 40),
                            new string('2', 40),
                            "example/repository")
                    ],
                    DateTimeOffset.UtcNow))
        };

    private sealed class PreviewReviewedCandidateService
        : IReviewedCandidateService
    {
        public int VerifyCalls { get; private set; }

        public bool FailVerification { get; set; }

        public bool MutateAfterVerification { get; set; }

        public bool NoPreviewArtifacts { get; set; }

        public Task<ReviewedCandidateIdentity> SealAsync(
            FlowRun flow,
            Guid outcomeOwnerStepId,
            string outcomeOwnerPlanStepKey,
            string outcomeContractJson,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AiHarnessDemo.Core.Verification.OutcomeCandidateSnapshot>
            VerifyAsync(
                FlowRun flow,
                ReviewedCandidateIdentity identity,
                CancellationToken cancellationToken = default)
        {
            VerifyCalls++;
            if (FailVerification)
            {
                throw new CandidateValidationException(
                    "The reviewed candidate changed after it was sealed.");
            }
            var path = Path.Combine(
                flow.WorkspacePath,
                ".customer-preview",
                "eu",
                "browser",
                "index.html");
            var bytes = File.ReadAllBytes(path);
            var digest =
                "sha256:" +
                Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(bytes))
                    .ToLowerInvariant();
            var manifest =
                new AiHarnessDemo.Core.Verification.CandidateManifest(
                    AiHarnessDemo.Core.Verification
                        .OutcomeVerificationRules
                        .CandidateManifestVersion,
                    flow.Iteration,
                    identity.AcceptancePlanHash,
                    identity.Repositories.Select(item =>
                        new AiHarnessDemo.Core.Verification
                            .CandidateRepositoryManifest(
                                item.RelativePath,
                                item.Head,
                                item.Tree,
                                item.RemoteRepository))
                        .ToArray(),
                    [],
                    NoPreviewArtifacts
                        ? []
                        : [
                            new AiHarnessDemo.Core.Verification
                                .CandidatePreviewArtifact(
                                    ".customer-preview/eu/browser/index.html",
                                    bytes.LongLength,
                                    digest)
                        ]);
            if (MutateAfterVerification)
            {
                File.WriteAllText(path, "changed after verification");
            }
            return Task.FromResult(
                new AiHarnessDemo.Core.Verification
                    .OutcomeCandidateSnapshot(
                        manifest,
                        identity.Fingerprint,
                        identity.OutcomeOwnerStepId,
                        identity.SealedAt));
        }
    }

    private sealed class PreviewDbContextFactory(
        DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);

        public Task<HarnessDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}

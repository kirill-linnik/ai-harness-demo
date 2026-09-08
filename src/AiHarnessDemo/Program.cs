using System.Text.Json.Serialization;
using AiHarnessDemo.Api;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Data;
using AiHarnessDemo.Infrastructure;
using AiHarnessDemo.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var paths = HarnessPaths.Create(builder.Environment, builder.Configuration);

builder.Services.AddSingleton(paths);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

builder.Services.AddDbContextFactory<HarnessDbContext>(options =>
    options.UseSqlite($"Data Source={paths.DatabasePath};Default Timeout=30;Pooling=True"));

builder.Services.AddSingleton<ProcessRunner>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CopilotCliRuntime>();
builder.Services.AddSingleton<RuntimeCircuitBreaker>();
builder.Services.AddSingleton<RepositoryContextGate>();
builder.Services.AddSingleton<WorkflowLoader>();
builder.Services.AddSingleton<WorkflowPromptRenderer>();
builder.Services.AddSingleton<WorkflowDefinitionProvider>();
builder.Services.AddHostedService(
    services => services.GetRequiredService<WorkflowDefinitionProvider>());
builder.Services.AddSingleton<WorkspaceHookRunner>();
builder.Services.AddSingleton<PermissionProfileResolver>();
builder.Services.AddSingleton<AgentManifestStager>();
builder.Services.AddSingleton<AgentCatalogLoader>();
builder.Services.AddSingleton<AgentCatalog>();
builder.Services.AddSingleton<FlowAgentSnapshotService>();
builder.Services.AddSingleton<NewWorkAdmissionService>();
builder.Services.AddSingleton<INewWorkAdmissionService>(
    services => services.GetRequiredService<NewWorkAdmissionService>());
builder.Services.AddSingleton<RepositoryAnalyzer>();
builder.Services.AddSingleton<IntakeCoordinator>();
builder.Services.AddSingleton<FlowPlanner>();
builder.Services.AddSingleton<TeamPlanValidator>();
builder.Services.AddSingleton<BootstrapTaskProfileFactory>();
builder.Services.AddSingleton<ModelCatalogDiscovery>();
builder.Services.AddSingleton<AdaptiveModelRouter>();
builder.Services.AddSingleton<IModelRouter>(
    services => services.GetRequiredService<AdaptiveModelRouter>());
builder.Services.AddSingleton<RoutingObservationRecorder>();
builder.Services.AddSingleton<PreviewArtifactCatalog>();
builder.Services.AddSingleton<AdvisoryArtifactCatalog>();
builder.Services.AddSingleton<CandidateFingerprintService>();
builder.Services.AddSingleton<IReviewedCandidateService, ReviewedCandidateService>();
builder.Services.AddSingleton<OutcomeVerificationContextBuilder>();
builder.Services.AddSingleton<IVerifiedCandidatePublisher, VerifiedCandidatePublisher>();
builder.Services.AddSingleton<IWorkspaceProcessCleaner, WorkspaceProcessCleaner>();
builder.Services.AddSingleton<IFlowSessionCleaner, FlowSessionCleaner>();
builder.Services.AddSingleton<FlowLifecycleCoordinator>();
builder.Services.AddSingleton<MissingQualificationCoordinator>();
builder.Services.AddSingleton<LinkedFlowCoordinator>();
builder.Services.AddSingleton<QualificationResolutionCoordinator>();
builder.Services.AddSingleton<IPublishedOutcomeVerifier, PublishedOutcomeVerifier>();
builder.Services.AddSingleton<CopilotReasoningHost>();
builder.Services.AddSingleton(_ =>
{
    var gate = new HandoffGateEngine();
    gate.SetTrustLevel(HandoffActionType.Advance, HandoffTrustLevel.Auto);
    gate.SetTrustLevel(HandoffActionType.RequestRevision, HandoffTrustLevel.Auto);
    gate.SetTrustLevel(HandoffActionType.Release, HandoffTrustLevel.Gated);
    gate.SetTrustLevel(HandoffActionType.OutcomeResolution, HandoffTrustLevel.Gated);
    gate.SetTrustLevel(HandoffActionType.CustomerReview, HandoffTrustLevel.Gated);
    return gate;
});
builder.Services.AddSingleton<IWorkspaceManager, WorkspaceManager>();
builder.Services.AddSingleton<AgentRunner>();
builder.Services.AddSingleton<IAgentRunner>(
    services => services.GetRequiredService<AgentRunner>());
builder.Services.AddSingleton<CopilotSessionJournal>();
builder.Services.AddSingleton<FlowQueue>();
builder.Services.AddSingleton<WorkflowEngine>();
builder.Services.AddSingleton<FeedbackCoordinator>();
builder.Services.AddSingleton<ReviewCoordinator>();
builder.Services.AddSingleton<FlowWorker>();
builder.Services.AddSingleton<IFlowExecutionController>(
    services => services.GetRequiredService<FlowWorker>());
builder.Services.AddSingleton<FlowAbandonmentService>();
builder.Services.AddHostedService(
    services => services.GetRequiredService<FlowWorker>());

var app = builder.Build();

await DatabaseInitializer.InitializeAsync(app.Services);
var startupWorkflow = app.Services
    .GetRequiredService<WorkflowDefinitionProvider>()
    .GetValidated();
await app.Services
    .GetRequiredService<CopilotCliRuntime>()
    .RefreshAsync(startupWorkflow.Config.Copilot.Command);
await app.Services
    .GetRequiredService<ModelCatalogDiscovery>()
    .RefreshAsync(paths.Root);
await app.Services
    .GetRequiredService<FlowAbandonmentService>()
    .ResumePendingAsync();

app.UseExceptionHandler();
app.Use(LocalRequestGuard.ApplyAsync);
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapDemoApi();
app.MapFallbackToFile("index.html");

await app.RunAsync();

public partial class Program;

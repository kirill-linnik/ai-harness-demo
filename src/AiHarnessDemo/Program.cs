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
builder.Services.AddSingleton<AgentCatalog>();
builder.Services.AddSingleton<RepositoryAnalyzer>();
builder.Services.AddSingleton<IntakeCoordinator>();
builder.Services.AddSingleton<FlowPlanner>();
builder.Services.AddSingleton<ModelSelector>();
builder.Services.AddSingleton<CopilotReasoningHost>();
builder.Services.AddSingleton(_ =>
{
    var gate = new HandoffGateEngine();
    gate.SetTrustLevel(HandoffActionType.Advance, HandoffTrustLevel.Auto);
    gate.SetTrustLevel(HandoffActionType.RequestRevision, HandoffTrustLevel.Auto);
    gate.SetTrustLevel(HandoffActionType.Release, HandoffTrustLevel.Gated);
    return gate;
});
builder.Services.AddSingleton<IWorkspaceManager, WorkspaceManager>();
builder.Services.AddSingleton<AgentRunner>();
builder.Services.AddSingleton<FlowQueue>();
builder.Services.AddSingleton<WorkflowEngine>();
builder.Services.AddSingleton<FeedbackCoordinator>();
builder.Services.AddHostedService<FlowWorker>();

var app = builder.Build();

await DatabaseInitializer.InitializeAsync(app.Services);
var startupWorkflow = app.Services
    .GetRequiredService<WorkflowDefinitionProvider>()
    .GetValidated();
await app.Services
    .GetRequiredService<CopilotCliRuntime>()
    .RefreshAsync(startupWorkflow.Config.Copilot.Command);

app.UseExceptionHandler();
app.Use(LocalRequestGuard.ApplyAsync);
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapDemoApi();
app.MapFallbackToFile("index.html");

await app.RunAsync();

public partial class Program;

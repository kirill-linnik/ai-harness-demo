using AiHarnessDemo.Data;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

public sealed record NewWorkAdmissionStatus(
    bool Ready,
    IReadOnlyList<string> Failures,
    DateTimeOffset CheckedAt);

public sealed class NewWorkAdmissionException(IReadOnlyList<string> failures)
    : Exception("New work is unavailable: " + string.Join(" ", failures))
{
    public IReadOnlyList<string> Failures { get; } = failures;
}

public interface INewWorkAdmissionService
{
    Task EnsureReadyAsync(CancellationToken cancellationToken = default);

    async Task EnsureReadyForContextAsync(
        string repositoryPath,
        string repositoryKnowledge,
        CancellationToken cancellationToken = default) =>
        await EnsureReadyAsync(cancellationToken);
}

public sealed class NewWorkAdmissionService : INewWorkAdmissionService
{
    private readonly IDbContextFactory<HarnessDbContext> _databaseFactory;
    private readonly Func<IReadOnlyList<string>> _sharedReadinessFailures;

    public NewWorkAdmissionService(
        IDbContextFactory<HarnessDbContext> databaseFactory,
        WorkflowDefinitionProvider workflowProvider,
        AgentCatalog agentCatalog,
        CopilotCliRuntime copilotRuntime,
        ModelCatalogDiscovery modelCatalog)
        : this(
            databaseFactory,
            () => GetSharedReadinessFailures(
                workflowProvider,
                agentCatalog,
                copilotRuntime,
                modelCatalog))
    {
    }

    internal NewWorkAdmissionService(
        IDbContextFactory<HarnessDbContext> databaseFactory,
        Func<IReadOnlyList<string>> sharedReadinessFailures)
    {
        _databaseFactory = databaseFactory;
        _sharedReadinessFailures = sharedReadinessFailures;
    }

    public async Task<NewWorkAdmissionStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var failures = _sharedReadinessFailures().ToList();
        await using var database =
            await _databaseFactory.CreateDbContextAsync(cancellationToken);
        var settings = await database.Settings
            .AsNoTracking()
            .SingleAsync(cancellationToken);
        AddRepositoryContextFailures(
            failures,
            settings.RepositoryPath,
            settings.RepositoryKnowledge,
            linkedContext: false);

        return new NewWorkAdmissionStatus(
            failures.Count == 0,
            failures,
            DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<string> GetSharedReadinessFailures(
        WorkflowDefinitionProvider workflowProvider,
        AgentCatalog agentCatalog,
        CopilotCliRuntime copilotRuntime,
        ModelCatalogDiscovery modelCatalog)
    {
        var failures = new List<string>();
        var workflow = workflowProvider.Status();
        if (!workflow.CurrentFileValid)
        {
            failures.Add(
                "WORKFLOW.md is invalid: " +
                (workflow.CurrentFileError ?? "no current valid definition."));
        }
        var catalog = agentCatalog.Status();
        if (!catalog.Ready)
        {
            failures.Add(
                "Agent catalog is not ready: " +
                (catalog.LastError ?? "no current valid catalog."));
        }
        var runtime = copilotRuntime.Current;
        var effectiveCommand = workflow.HasEffectiveDefinition
            ? workflowProvider.GetEffective().Config.Copilot.Command
            : null;
        if (!runtime.Ready ||
            !string.Equals(runtime.Command, effectiveCommand, StringComparison.Ordinal))
        {
            failures.Add(
                "Copilot runtime is not ready for the effective workflow: " +
                runtime.Detail);
        }
        if (!modelCatalog.Current.Ready)
        {
            failures.Add("Model catalog is not ready: " + modelCatalog.Current.Detail);
        }
        return failures;
    }

    public async Task EnsureReadyAsync(
        CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (!status.Ready)
        {
            throw new NewWorkAdmissionException(status.Failures);
        }
    }

    public Task EnsureReadyForContextAsync(
        string repositoryPath,
        string repositoryKnowledge,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var failures = _sharedReadinessFailures().ToList();
        AddRepositoryContextFailures(
            failures,
            repositoryPath,
            repositoryKnowledge,
            linkedContext: true);
        if (failures.Count > 0)
        {
            throw new NewWorkAdmissionException(failures);
        }
        return Task.CompletedTask;
    }

    private static void AddRepositoryContextFailures(
        ICollection<string> failures,
        string repositoryPath,
        string repositoryKnowledge,
        bool linkedContext)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            failures.Add(linkedContext
                ? "The linked source project path is required."
                : "A source project path is required.");
        }
        else if (!Directory.Exists(repositoryPath))
        {
            failures.Add(linkedContext
                ? "The linked source project path does not exist."
                : "The configured source project path does not exist.");
        }
        if (string.IsNullOrWhiteSpace(repositoryKnowledge))
        {
            failures.Add(linkedContext
                ? "Linked source-project knowledge is required; study the source project first."
                : "Repository knowledge is required; study the source project first.");
        }
    }
}

using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Core.Domain;

namespace AiHarnessDemo.Services;

public enum WorkspaceHookStage
{
    AfterCreate,
    BeforeRun,
    AfterRun,
    BeforeRemove
}

public sealed class WorkspaceHookRunner(
    WorkflowDefinitionProvider workflowProvider,
    ProcessRunner processRunner,
    ILogger<WorkspaceHookRunner> logger)
{
    public async Task RunAsync(
        WorkspaceHookStage stage,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        await RunAsync(
            stage,
            workingDirectory,
            workflowProvider.GetValidated(),
            cancellationToken);
    }

    public async Task RunAsync(
        WorkspaceHookStage stage,
        string workingDirectory,
        WorkflowDefinition workflow,
        CancellationToken cancellationToken = default)
    {
        await RunAsync(
            stage,
            workingDirectory,
            workflow,
            FlowKind.Delivery,
            provisional: false,
            cancellationToken);
    }

    public async Task RunAsync(
        WorkspaceHookStage stage,
        string workingDirectory,
        WorkflowDefinition workflow,
        FlowKind flowKind,
        bool provisional,
        CancellationToken cancellationToken = default)
    {
        if (provisional || flowKind == FlowKind.Advisory)
        {
            logger.LogInformation(
                "Skipped Symphony workspace hook {HookStage} for guarded {WorkspacePolicy} execution in {WorkingDirectory}",
                stage,
                provisional ? "provisional intake" : "Advisory",
                workingDirectory);
            return;
        }

        var script = stage switch
        {
            WorkspaceHookStage.AfterCreate => workflow.Config.Hooks.AfterCreate,
            WorkspaceHookStage.BeforeRun => workflow.Config.Hooks.BeforeRun,
            WorkspaceHookStage.AfterRun => workflow.Config.Hooks.AfterRun,
            WorkspaceHookStage.BeforeRemove => workflow.Config.Hooks.BeforeRemove,
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
        };
        if (string.IsNullOrWhiteSpace(script))
        {
            return;
        }

        var fatal = stage is WorkspaceHookStage.AfterCreate or WorkspaceHookStage.BeforeRun;
        var executable = OperatingSystem.IsWindows()
            ? ExecutableLocator.Exists("pwsh") ? "pwsh" : "powershell"
            : "sh";
        var arguments = OperatingSystem.IsWindows()
            ? new[] { "-NoProfile", "-NonInteractive", "-Command", script }
            : new[] { "-lc", script };

        logger.LogInformation(
            "Running Symphony workspace hook {HookStage} in {WorkingDirectory}",
            stage,
            workingDirectory);

        try
        {
            var result = await processRunner.RunAsync(
                executable,
                arguments,
                workingDirectory,
                TimeSpan.FromMilliseconds(workflow.Config.Hooks.TimeoutMs),
                cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Workspace hook {stage} failed with exit code {result.ExitCode}: {result.CombinedOutput}");
            }
        }
        catch (Exception exception) when (!fatal && exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Non-fatal Symphony workspace hook {HookStage} failed.",
                stage);
        }
    }
}

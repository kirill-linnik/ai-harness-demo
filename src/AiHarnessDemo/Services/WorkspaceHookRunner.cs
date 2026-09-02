using AiHarnessDemo.Core.Workflow;

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
        var workflow = workflowProvider.GetValidated();
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

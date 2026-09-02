using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Infrastructure;

namespace AiHarnessDemo.Services;

/// <summary>Reasoning host that runs enabled custom agents through the local Copilot CLI.</summary>
public sealed class CopilotReasoningHost(
    AgentCatalog agentCatalog,
    ProcessRunner processRunner,
    HarnessPaths paths,
    WorkflowDefinitionProvider workflowProvider,
    CopilotCliRuntime copilotCliRuntime,
    WorkflowPromptRenderer promptRenderer,
    WorkspaceHookRunner hookRunner)
    : ReasoningHost(new ReasoningHostConfig("copilot-cli"))
{
    private const int MaximumPromptCharacters = 24_000;

    public override ReasoningHostReadiness CheckReadiness(
        CancellationToken cancellationToken = default)
    {
        var command = workflowProvider.GetValidated().Config.Copilot.Command;
        var status = copilotCliRuntime.Current;
        return status.Ready &&
               string.Equals(status.Command, command, StringComparison.Ordinal)
            ? ReasoningHostReadiness.CreateAvailable(status.Detail)
            : ReasoningHostReadiness.CreateUnavailable(
                string.Equals(status.Command, command, StringComparison.Ordinal)
                    ? status.Detail
                    : $"Copilot CLI command '{command}' has not passed startup validation.");
    }

    public override async Task<AgentRunResult> RunAgentAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default)
    {
        var context = RequireContext(request);
        var manifest = await agentCatalog.GetManifestAsync(context.AgentId, cancellationToken);
        var workflow = workflowProvider.GetValidated();
        var copilotCli = await copilotCliRuntime.GetAsync(
            workflow.Config.Copilot.Command,
            cancellationToken);
        if (!copilotCli.Ready)
        {
            return Failure(
                "Copilot CLI is not ready.",
                copilotCli.Detail,
                AgentRunFailureKind.DependencyUnavailable,
                "copilot-cli");
        }
        request.Progress?.Invoke(new AgentRunProgress(
            AgentRunPhase.BuildingPrompt,
            "Rendering WORKFLOW.md with role and repository context."));
        var prompt = Clip(
            promptRenderer.Render(
            workflow.PromptTemplate,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["agent.name"] = context.AgentName,
                ["agent.instructions"] = Clip(manifest.Instructions, 4_000),
                ["task"] = Clip(context.Task, 6_000),
                ["repository.knowledge"] = Clip(context.RepositoryKnowledge, 6_000),
                ["plan"] = Clip(context.PlanSummary, 2_000),
                ["handoffs"] = context.PreviousOutputs.Count == 0
                    ? "No prior handoff."
                    : string.Join(
                        Environment.NewLine,
                        context.PreviousOutputs.TakeLast(4).Select(item => $"- {Clip(item, 1_500)}")),
                ["learnings"] = context.Learnings.Count == 0
                    ? "No prior prompt refinement applies."
                    : string.Join(
                        Environment.NewLine,
                        context.Learnings.TakeLast(4).Select(item => $"- {Clip(item.PromptRefinement, 750)}")),
                ["feedback"] = string.IsNullOrWhiteSpace(context.CustomerFeedback)
                    ? "No customer feedback for this turn."
                    : Clip(context.CustomerFeedback, 2_000),
                ["response.contract"] = ResponseContract(context.AgentRole)
            }),
            MaximumPromptCharacters);
        ProcessResult result;

        try
        {
            await hookRunner.RunAsync(
                WorkspaceHookStage.BeforeRun,
                request.WorkingDirectory,
                cancellationToken);
            request.Progress?.Invoke(new AgentRunProgress(
                AgentRunPhase.LaunchingAgentProcess,
                $"Launching Copilot CLI {copilotCli.Version} with {request.Model}."));
            result = await processRunner.RunAsync(
                copilotCli.ResolvedPath,
                [
                    "-C", request.WorkingDirectory,
                    "--add-dir", paths.Root,
                    "--agent", request.AgentId,
                    "--model", request.Model,
                    "--output-format", "json",
                    "--no-color",
                    "--no-ask-user",
                    "--allow-all-tools",
                    "-p", prompt
                ],
                request.WorkingDirectory,
                TimeSpan.FromMilliseconds(workflow.Config.Copilot.TurnTimeoutMs),
                cancellationToken,
                CopilotJsonlParser.CreateProgressReporter(request.Progress),
                TimeSpan.FromMilliseconds(workflow.Config.Copilot.StallTimeoutMs));
        }
        catch (ProcessStalledException exception)
        {
            return Failure(
                "Copilot CLI stalled.",
                exception.Message,
                AgentRunFailureKind.Stalled);
        }
        catch (TimeoutException exception)
        {
            return Failure(
                "Copilot CLI timed out.",
                exception.Message,
                AgentRunFailureKind.TimedOut);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return Failure(
                "Copilot CLI failed to launch.",
                exception.Message,
                AgentRunFailureKind.DependencyUnavailable,
                "copilot-cli");
        }
        catch (InvalidOperationException exception)
        {
            return Failure(
                "A required pre-run workspace hook failed.",
                exception.Message,
                AgentRunFailureKind.Transient);
        }
        finally
        {
            await hookRunner.RunAsync(
                WorkspaceHookStage.AfterRun,
                request.WorkingDirectory,
                CancellationToken.None);
        }

        if (result.ExitCode != 0)
        {
            return Failure(
                $"Copilot CLI exited with code {result.ExitCode}.",
                Tail(result.CombinedOutput, 1_500),
                AgentRunFailureKind.Transient);
        }

        return CopilotJsonlParser.Parse(
            result.StandardOutput,
            result.StandardError);
    }

    private static AgentExecutionContext RequireContext(AgentRunRequest request) =>
        request.InputContext.TryGetValue("execution", out var value) &&
        value is AgentExecutionContext context
            ? context
            : throw new AgentRunException(
                "Copilot host received no execution context.",
                AgentRunFailureKind.InvalidOutput);

    private static AgentRunResult Failure(
        string summary,
        string error,
        AgentRunFailureKind failureKind,
        string? dependency = null) =>
        new()
        {
            Success = false,
            OutputSummary = summary,
            Error = error,
            FailureKind = failureKind,
            FailedDependency = dependency
        };

    private static string ResponseContract(string agentRole) =>
        agentRole == "account-manager"
            ? """
              Decide whether the customer request is implementation-ready.
              Return exactly these plain-text markers with no markdown:
              INTAKE_STATUS: READY or NEEDS_CLARIFICATION
              CUSTOMER_REPLY: one concise sentence suitable for spoken playback
              TASK_BRIEF: the complete implementation brief when ready, otherwise NONE
              Ask only one material question when clarification is needed.
              """
            : """
              Complete the assigned role in the isolated workspace; do not merely advise.
              Return concise sections named Decision, Deliverable, Evidence, and Next owner.
              If an upstream handoff is insufficient, stop and state PUSHBACK plus the exact missing detail.
              """;

    private static string Tail(string value, int maxCharacters) =>
        value.Length <= maxCharacters ? value : value[^maxCharacters..];

    private static string Clip(string value, int maxCharacters)
    {
        if (value.Length <= maxCharacters)
        {
            return value;
        }

        const string marker = "\n...[context compacted]...\n";
        var available = maxCharacters - marker.Length;
        var headLength = available * 2 / 3;
        return value[..headLength] + marker + value[^(available - headLength)..];
    }
}

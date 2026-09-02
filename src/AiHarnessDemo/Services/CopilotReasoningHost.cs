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
    private const int AccountManagerRepositoryKnowledgeCharacters = 2_000;

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
        var isAccountManager = IsAccountManager(context.AgentRole);
        var prompt = Clip(
            promptRenderer.Render(
            workflow.PromptTemplate,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["agent.name"] = context.AgentName,
                ["agent.instructions"] = Clip(manifest.Instructions, 4_000),
                ["task"] = Clip(context.Task, 6_000),
                ["repository.knowledge"] = Clip(
                    context.RepositoryKnowledge,
                    isAccountManager ? AccountManagerRepositoryKnowledgeCharacters : 6_000),
                ["plan"] = Clip(context.PlanSummary, 2_000),
                ["handoffs"] = isAccountManager
                    ? "Prior Account Manager replies are already included in the customer dialogue."
                    : context.PreviousOutputs.Count == 0
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
        var arguments = BuildCliArguments(
            request.WorkingDirectory,
            paths.Root,
            context.AgentId,
            context.AgentRole,
            request.Model,
            prompt);
        var environmentVariables = BuildProcessEnvironment(
            context.AgentRole,
            paths.DatabasePath);
        if (environmentVariables?.TryGetValue("COPILOT_HOME", out var copilotHome) == true)
        {
            Directory.CreateDirectory(copilotHome);
        }
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
                arguments,
                request.WorkingDirectory,
                TimeSpan.FromMilliseconds(workflow.Config.Copilot.TurnTimeoutMs),
                cancellationToken,
                CopilotJsonlParser.CreateProgressReporter(request.Progress),
                TimeSpan.FromMilliseconds(workflow.Config.Copilot.StallTimeoutMs),
                environmentVariables);
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

    internal static IReadOnlyList<string> BuildCliArguments(
        string workingDirectory,
        string harnessRoot,
        string agentId,
        string agentRole,
        string model,
        string prompt)
    {
        var arguments = new List<string>
        {
            "-C", workingDirectory,
            "--add-dir", harnessRoot,
            "--agent", agentId,
            "--model", model,
            "--output-format", "json",
            "--no-color",
            "--no-ask-user"
        };

        if (IsAccountManager(agentRole))
        {
            arguments.AddRange(
            [
                "--available-tools",
                "--disable-builtin-mcps",
                "--effort", "low",
                "--no-custom-instructions",
                "--no-eager-powershell-resolution"
            ]);
        }
        else
        {
            arguments.Add("--allow-all-tools");
        }

        arguments.AddRange(["-p", prompt]);
        return arguments;
    }

    internal static IReadOnlyDictionary<string, string>? BuildProcessEnvironment(
        string agentRole,
        string databasePath)
    {
        if (!IsAccountManager(agentRole))
        {
            return null;
        }

        var dataDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath))
            ?? throw new InvalidOperationException(
                $"Harness database path has no parent directory: {databasePath}");
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["COPILOT_HOME"] = Path.Combine(
                dataDirectory,
                "copilot-home",
                "account-manager")
        };
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

    internal static string ResponseContract(string agentRole) =>
        agentRole == "account-manager"
            ? """
              Move a workable customer request into delivery; do not exhaustively specify it.
              Return exactly these plain-text markers with no text before INTAKE_STATUS:
              INTAKE_STATUS: READY or NEEDS_CLARIFICATION
              CUSTOMER_REPLY: when READY, one brief confirmation of what the team will make; when clarification is essential, one short question; always use plain everyday customer language on one line
              TASK_BRIEF: the complete implementation brief when ready, otherwise NONE; a ready brief may continue on following lines
              Default to READY as soon as the delivery team can take a meaningful first action.
              Use NEEDS_CLARIFICATION only when the target product or visible outcome cannot be identified and no safe reversible assumption lets work start.
              Treat every earlier answer as settled. Never repeat, reconfirm, or reframe it as another choice.
              If the dialogue already contains an Account Manager question, or the customer tells you to proceed or shows frustration, you must return READY using reasonable assumptions.
              A request for something the customer can click is actionable and requires an interactive result; do not ask whether it means pictures, a prototype, implementation, or deployment.
              Never ask about technologies, tools, file formats, implementation approaches, deployment, hosting, credentials, live release, pull requests, builds, or who deploys.
              Put reversible assumptions and decisions owned by designers, engineers, or release staff in TASK_BRIEF instead of asking the customer.
              """
            : """
              Complete the assigned role in the isolated workspace; do not merely advise.
              Return concise sections named Decision, Deliverable, Evidence, and Next owner.
              If an upstream handoff is insufficient, stop and state PUSHBACK plus the exact missing detail.
              """;

    private static bool IsAccountManager(string agentRole) =>
        string.Equals(agentRole, "account-manager", StringComparison.Ordinal);

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

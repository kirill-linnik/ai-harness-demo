using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Workflow;
using System.Text.RegularExpressions;

namespace AiHarnessDemo.Services;

/// <summary>Reasoning host that runs enabled custom agents through the local Copilot CLI.</summary>
public sealed partial class CopilotReasoningHost(
    AgentCatalog agentCatalog,
    ProcessRunner processRunner,
    WorkflowDefinitionProvider workflowProvider,
    CopilotCliRuntime copilotCliRuntime,
    WorkflowPromptRenderer promptRenderer,
    WorkspaceHookRunner hookRunner,
    CopilotSessionJournal sessionJournal)
    : ReasoningHost(new ReasoningHostConfig("copilot-cli"))
{
    private const int MaximumPromptCharacters = 16_000;
    private const int AccountManagerRepositoryKnowledgeCharacters = 1_000;
    private const int DeliveryRepositoryKnowledgeCharacters = 2_000;
    private const int ProductManagerLedgerCharacters = 6_000;

    [GeneratedRegex(
        @"(?im)^- \*\*Location:\*\*\s*`[^`\r\n]+`\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryLocationPattern();

    [GeneratedRegex(
        @"(?ms)^## AI initialization\s*\n.*?(?=^## |\z)",
        RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedRepositorySectionPattern();

    [GeneratedRegex(
        @"(?ms)^## README signal\s*\n(?<content>.*?)(?=^## (?:Study warnings|Editable harness notes)[^\S\n]*$|\z)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReadmeSignalPattern();

    [GeneratedRegex(
        @"(?m)^- \*\*(?:Project files|Source files studied|Primary file types):\*\*.*\n?",
        RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedRepositoryDetailPattern();

    [GeneratedRegex(
        @"(?ms)^## Editable harness notes\s*\nAdd domain language, architectural constraints, release rules, and quality expectations here\. Every agent receives this shared context\.\s*\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex EmptyRepositoryNotesPattern();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex ExcessBlankLinesPattern();

    [GeneratedRegex(@"(?m)^# ([^#\n].*)$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryTitlePattern();

    [GeneratedRegex(
        @"(?m)^## (?:Repository profile|Repository facts)\s*\n?",
        RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryProfileHeadingPattern();

    [GeneratedRegex(@"(?m)^## ", RegexOptions.CultureInvariant)]
    private static partial Regex RepositorySubheadingPattern();

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
            "Rendering WORKFLOW.md with role-focused task context."));
        var renderedPrompt = Clip(
            promptRenderer.Render(
                workflow.PromptTemplate,
                BuildPromptValues(
                    context,
                    manifest.Instructions,
                    request.WorkingDirectory)),
            MaximumPromptCharacters);
        var prompt = context.RecoverInterruptedSession
            ? RestartContinuationPrompt(renderedPrompt)
            : renderedPrompt;
        request.Progress?.Invoke(new AgentRunProgress(
            AgentRunPhase.BuildingPrompt,
            "Rendered the exact prompt for the Copilot CLI turn.",
            prompt));
        var copilotSessionHome = ResolveCopilotSessionHome();
        var agentAccess = await PrepareRestrictedAgentRootAsync(
            copilotSessionHome,
            context.AgentId,
            manifest.SourcePath,
            request.CopilotSessionId,
            cancellationToken);
        var environmentVariables = BuildProcessEnvironment(
            context.AgentRole,
            context.AllowRemotePublication);
        var arguments = BuildCliArguments(
            request.WorkingDirectory,
            agentAccess.Root,
            agentAccess.AgentId,
            context.AgentRole,
            request.Model,
            request.Effort,
            request.CopilotSessionId,
            prompt,
            context.ResumeSession || context.RecoverInterruptedSession);
        ProcessResult result;
        var timeouts = ResolveExecutionTimeouts(
            workflow.Config.Copilot,
            context.ModelSelectionStrategy,
            context.ExpectedAcceptedTimeSeconds);

        try
        {
            await hookRunner.RunAsync(
                WorkspaceHookStage.BeforeRun,
                request.WorkingDirectory,
                cancellationToken);
            request.Progress?.Invoke(new AgentRunProgress(
                AgentRunPhase.LaunchingAgentProcess,
                $"Launching Copilot CLI {copilotCli.Version} with {request.Model}/{request.Effort}; " +
                $"{timeouts.StallTimeout.TotalMinutes:0.#}-minute quiet watchdog and " +
                $"{timeouts.TurnTimeout.TotalMinutes:0.#}-minute hard limit."));
            result = await processRunner.RunAsync(
                copilotCli.ResolvedPath,
                arguments,
                request.WorkingDirectory,
                timeouts.TurnTimeout,
                cancellationToken,
                CopilotJsonlParser.CreateProgressReporter(
                    request.Progress,
                    copilotSessionHome),
                timeouts.StallTimeout,
                environmentVariables);
        }
        catch (ProcessStalledException exception)
        {
            return await RecoverInterruptedProcessAsync(
                context,
                request,
                copilotSessionHome,
                "Copilot CLI stalled.",
                exception.Message,
                AgentRunFailureKind.Stalled);
        }
        catch (TimeoutException exception)
        {
            return await RecoverInterruptedProcessAsync(
                context,
                request,
                copilotSessionHome,
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
            var diagnostic = Tail(result.CombinedOutput, 1_500);
            return Failure(
                $"Copilot CLI exited with code {result.ExitCode}.",
                diagnostic,
                IsModelUnavailableDiagnostic(diagnostic)
                    ? AgentRunFailureKind.ModelUnavailable
                    : AgentRunFailureKind.Transient,
                IsModelUnavailableDiagnostic(diagnostic)
                    ? $"{request.Model}/{request.Effort}"
                    : null);
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
        string effort,
        Guid copilotSessionId,
        string prompt,
        bool resumeSession = false)
    {
        var arguments = new List<string>
        {
            "-C", workingDirectory,
            "--add-dir", workingDirectory,
            "--add-dir", harnessRoot,
            "--agent", agentId,
            "--model", model,
            "--output-format", "json",
            "--no-color",
            "--no-ask-user"
        };
        if (!string.Equals(effort, "default", StringComparison.OrdinalIgnoreCase))
        {
            arguments.AddRange(["--effort", effort]);
        }
        arguments.AddRange(
            resumeSession
                ? [$"--resume={copilotSessionId:D}"]
                : ["--session-id", copilotSessionId.ToString("D")]);

        if (IsAccountManager(agentRole))
        {
            arguments.AddRange(
            [
                "--available-tools",
                "--disable-builtin-mcps",
                "--no-custom-instructions",
                "--no-eager-powershell-resolution"
            ]);
        }
        else if (string.Equals(
                     agentRole,
                     "pre-mortem-sceptic",
                     StringComparison.Ordinal))
        {
            arguments.AddRange(
            [
                "--available-tools=view,grep,glob,web_search",
                "--deny-tool=write,shell",
                "--disallow-temp-dir",
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

    internal static async Task<RestrictedAgentAccess>
        PrepareRestrictedAgentRootAsync(
            string copilotHome,
            string agentId,
            string sourcePath,
            Guid sessionId,
            CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(
            Path.GetFullPath(copilotHome),
            "harness-agent-definitions",
            sessionId.ToString("N"));
        var agentsDirectory = Path.Combine(root, ".github", "agents");
        Directory.CreateDirectory(agentsDirectory);
        var restrictedAgentId =
            $"harness-{agentId}-{sessionId:N}";
        var destination = Path.Combine(
            agentsDirectory,
            $"{restrictedAgentId}.agent.md");
        var content = await File.ReadAllTextAsync(
            Path.GetFullPath(sourcePath),
            cancellationToken);
        await File.WriteAllTextAsync(
            destination,
            RemoveAgentModelFrontMatter(content),
            cancellationToken);
        return new RestrictedAgentAccess(root, restrictedAgentId);
    }

    internal static string RemoveAgentModelFrontMatter(string content)
    {
        var normalized = content.ReplaceLineEndings("\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return normalized;
        }
        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            return normalized;
        }

        var frontMatter = normalized[4..end]
            .Split('\n')
            .Where(line =>
                !line.TrimStart().StartsWith(
                    "model:",
                    StringComparison.OrdinalIgnoreCase));
        return
            $"---{Environment.NewLine}" +
            string.Join(Environment.NewLine, frontMatter) +
            $"{Environment.NewLine}---{Environment.NewLine}" +
            normalized[(end + 5)..];
    }

    internal static bool IsModelUnavailableDiagnostic(string diagnostic)
    {
        var value = diagnostic.ToLowerInvariant();
        return value.Contains("model is not available", StringComparison.Ordinal) ||
               value.Contains("model unavailable", StringComparison.Ordinal) ||
               value.Contains("unsupported model", StringComparison.Ordinal) ||
               value.Contains("unknown model", StringComparison.Ordinal) ||
               value.Contains("invalid model", StringComparison.Ordinal);
    }

    internal static string RestartContinuationPrompt(string renderedPrompt)
    {
        const string recoveryInstruction =
            "Resume from the current workspace; inspect existing changes before continuing.";
        return
            recoveryInstruction +
            Environment.NewLine +
            Environment.NewLine +
            Clip(
                renderedPrompt,
                MaximumPromptCharacters -
                recoveryInstruction.Length -
                (Environment.NewLine.Length * 2));
    }

    internal static CopilotExecutionTimeouts ResolveExecutionTimeouts(
        CopilotConfig config,
        ModelSelectionStrategy strategy,
        double expectedAcceptedTimeSeconds = 0)
    {
        var stallTimeoutMs = config.StallTimeoutMs;
        if (strategy == ModelSelectionStrategy.MaximumQuality)
        {
            var predictedQuietWindowMs = double.IsFinite(expectedAcceptedTimeSeconds) &&
                                         expectedAcceptedTimeSeconds > 0
                ? expectedAcceptedTimeSeconds * 1_500
                : config.MaximumQualityStallTimeoutMs;
            stallTimeoutMs = (int)Math.Clamp(
                predictedQuietWindowMs,
                config.StallTimeoutMs,
                config.MaximumQualityStallTimeoutMs);
        }

        return new CopilotExecutionTimeouts(
            TimeSpan.FromMilliseconds(config.TurnTimeoutMs),
            TimeSpan.FromMilliseconds(stallTimeoutMs));
    }

    private async Task<AgentRunResult> RecoverInterruptedProcessAsync(
        AgentExecutionContext context,
        AgentRunRequest request,
        string copilotSessionHome,
        string summary,
        string error,
        AgentRunFailureKind failureKind)
    {
        CopilotSessionSnapshot snapshot;
        try
        {
            snapshot = await sessionJournal.InspectAsync(
                copilotSessionHome,
                request.CopilotSessionId,
                CancellationToken.None);
            for (var retry = 0;
                 retry < 4 && snapshot.State == CopilotSessionJournalState.Active;
                 retry++)
            {
                await Task.Delay(250, CancellationToken.None);
                snapshot = await sessionJournal.InspectAsync(
                    copilotSessionHome,
                    request.CopilotSessionId,
                    CancellationToken.None);
            }
        }
        catch (IOException exception)
        {
            return Failure(
                summary,
                $"{error} Session recovery inspection failed: {exception.Message}",
                failureKind);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure(
                summary,
                $"{error} Session recovery inspection failed: {exception.Message}",
                failureKind);
        }

        if (snapshot.State == CopilotSessionJournalState.Completed &&
            snapshot.Result is { Success: true } recovered &&
            IsRecoverableCompletedOutput(
                context.AgentRole,
                recovered.OutputSummary,
                context.IsPreMortemRevision) &&
            IsRecoveryCurrent(
                context.InvocationStartedAt,
                snapshot.CompletedAt))
        {
            request.Progress?.Invoke(new AgentRunProgress(
                AgentRunPhase.Finishing,
                "Recovered the completed handoff from the Copilot session journal after the CLI stopped responding."));
            return recovered;
        }

        return Failure(
            summary,
            error,
            failureKind,
            canResumeSession:
                snapshot.State is
                    CopilotSessionJournalState.Interrupted or
                    CopilotSessionJournalState.Completed);
    }

    internal static bool IsRecoverableCompletedOutput(
        string agentRole,
        string output,
        bool isPreMortemRevision = false)
    {
        if (isPreMortemRevision)
        {
            try
            {
                if (!AgentHandoffInspector.HasCompleteStatus(output))
                {
                    return false;
                }
                _ = PreMortemRules.ParseDisposition(output);
                return true;
            }
            catch (PreMortemValidationException)
            {
                return false;
            }
        }

        return agentRole switch
        {
            "account-manager" => HasValidIntakeContract(output),
            "product-manager" => FeedbackCoordinator.HasReworkTargetMarker(output),
            "pre-mortem-sceptic" => HasValidPreMortemContract(output),
            _ => AgentHandoffInspector.HasTerminalStatus(output)
        };
    }

    internal static bool IsRecoveryCurrent(
        DateTimeOffset? invocationStartedAt,
        DateTimeOffset? recoveredCompletedAt) =>
        invocationStartedAt is null ||
        recoveredCompletedAt is { } completedAt &&
        completedAt >= invocationStartedAt.Value;

    private static bool HasValidIntakeContract(string output)
    {
        try
        {
            _ = IntakeCoordinator.ParseResponse(output);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasValidPreMortemContract(string output)
    {
        try
        {
            _ = PreMortemRules.ParseReview(output);
            return true;
        }
        catch (PreMortemValidationException)
        {
            return false;
        }
    }

    internal static IReadOnlyDictionary<string, string> BuildPromptValues(
        AgentExecutionContext context,
        string agentInstructions,
        string workingDirectory)
    {
        var isAccountManager = IsAccountManager(context.AgentRole);
        var isProductManager = IsProductManager(context.AgentRole);
        var isPreMortem = string.Equals(
            context.AgentRole,
            "pre-mortem-sceptic",
            StringComparison.Ordinal);
        var usesCompactPreMortemContext =
            isPreMortem || context.IsPreMortemRevision;
        var workspace = PrepareWorkspace(workingDirectory);
        var repositoryFacts = isProductManager
            ? string.Empty
            : context.IsPreMortemRevision
                ? string.Empty
            : Clip(
                PrepareRepositoryFacts(
                    context.RepositoryKnowledge,
                    context.SourceProjectPath),
                isAccountManager
                    ? AccountManagerRepositoryKnowledgeCharacters
                    : DeliveryRepositoryKnowledgeCharacters);
        var handoffs = isAccountManager || usesCompactPreMortemContext
            ? string.Empty
            : isProductManager
                ? CompactExecutionLedger(
                    context.PreviousOutputs,
                    context.SourceProjectPath,
                    ProductManagerLedgerCharacters)
                : string.Join(
                    $"{Environment.NewLine}{Environment.NewLine}",
                    context.PreviousOutputs.TakeLast(2).Select(item =>
                        Clip(
                            RemoveSourceProjectPath(item, context.SourceProjectPath),
                            1_600)));
        var learnings = usesCompactPreMortemContext
            ? string.Empty
            : string.Join(
                Environment.NewLine,
                context.Learnings
                    .TakeLast(2)
                    .Select(item => $"- {Clip(item.PromptRefinement, 500)}"));
        var feedback = string.IsNullOrWhiteSpace(context.CustomerFeedback)
            ? string.Empty
            : Clip(context.CustomerFeedback, 2_000);

        var roleContext = new List<string>();
        if (!string.IsNullOrWhiteSpace(repositoryFacts))
        {
            roleContext.Add($"## Repository facts{Environment.NewLine}{Environment.NewLine}{repositoryFacts}");
        }
        if (!string.IsNullOrWhiteSpace(handoffs))
        {
            roleContext.Add(
                $"## {(isProductManager ? "Execution ledger" : "Relevant upstream handoffs")}" +
                $"{Environment.NewLine}{Environment.NewLine}{handoffs}");
        }
        if (!string.IsNullOrWhiteSpace(learnings))
        {
            roleContext.Add($"## Learned constraints{Environment.NewLine}{Environment.NewLine}{learnings}");
        }
        if (!string.IsNullOrWhiteSpace(feedback))
        {
            roleContext.Add($"## Customer feedback{Environment.NewLine}{Environment.NewLine}{feedback}");
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["agent.name"] = context.AgentName,
            ["agent.instructions"] = Clip(agentInstructions, 3_000),
            ["task"] = Clip(
                context.Task,
                usesCompactPreMortemContext ? 10_000 : 6_000),
            ["workspace"] = workspace,
            ["role.context"] = string.Join(
                $"{Environment.NewLine}{Environment.NewLine}",
                roleContext),
            ["response.contract"] = context.IsPreMortemRevision
                ? PreMortemRevisionResponseContract()
                : ResponseContract(context.AgentRole),
            // Retain legacy variables so a hot-reloaded older WORKFLOW.md remains valid.
            ["repository.knowledge"] = string.IsNullOrWhiteSpace(repositoryFacts)
                ? workspace
                : $"{workspace}{Environment.NewLine}{Environment.NewLine}{repositoryFacts}",
            ["plan"] = Clip(context.PlanSummary, 1_000),
            ["handoffs"] = handoffs,
            ["learnings"] = learnings,
            ["feedback"] = feedback
        };
    }

    internal static string CompactExecutionLedger(
        IReadOnlyList<string> outputs,
        string sourceProjectPath,
        int maxCharacters)
    {
        if (maxCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCharacters),
                "The execution-ledger prompt budget must be positive.");
        }
        if (outputs.Count == 0)
        {
            return string.Empty;
        }

        var separator = $"{Environment.NewLine}{Environment.NewLine}";
        var contentBudget = Math.Max(
            0,
            maxCharacters - (separator.Length * (outputs.Count - 1)));
        var entryBudget = contentBudget / outputs.Count;
        var remainder = contentBudget % outputs.Count;
        var entries = outputs
            .Select((output, index) =>
                CompactExecutionLedgerEntry(
                    RemoveSourceProjectPath(output, sourceProjectPath),
                    entryBudget + (index < remainder ? 1 : 0)))
            .ToList();
        return string.Join(separator, entries);
    }

    private static string CompactExecutionLedgerEntry(string output, int budget)
    {
        if (budget <= 0)
        {
            return string.Empty;
        }

        var normalized = output.ReplaceLineEndings("\n").Trim();
        var firstLineEnd = normalized.IndexOf('\n');
        var identity = firstLineEnd < 0
            ? normalized
            : normalized[..firstLineEnd].Trim();
        var body = firstLineEnd < 0
            ? string.Empty
            : normalized[(firstLineEnd + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(identity))
        {
            identity = "Agent step";
        }

        var identityBudget = Math.Max(0, Math.Min(120, budget - 4));
        var heading = $"### {ClipHead(identity, identityBudget)}";
        if (heading.Length >= budget)
        {
            return ClipHead(heading, budget);
        }
        if (string.IsNullOrWhiteSpace(body) ||
            heading.Length + Environment.NewLine.Length >= budget)
        {
            return heading;
        }

        var bodyBudget = budget - heading.Length - Environment.NewLine.Length;
        return $"{heading}{Environment.NewLine}{ClipToBudget(body, bodyBudget)}";
    }

    private static string ClipToBudget(string value, int maxCharacters) =>
        maxCharacters < 80
            ? ClipHead(value, maxCharacters)
            : Clip(value, maxCharacters);

    private static string ClipHead(string value, int maxCharacters)
    {
        if (maxCharacters <= 0)
        {
            return string.Empty;
        }
        if (value.Length <= maxCharacters)
        {
            return value;
        }
        if (maxCharacters <= 3)
        {
            return value[..maxCharacters];
        }

        return value[..(maxCharacters - 3)] + "...";
    }

    internal static string PrepareWorkspace(string workingDirectory) =>
        $"- **Project root:** `{Path.GetFullPath(workingDirectory)}`{Environment.NewLine}" +
        "- **Boundary:** Work only in this isolated workspace. Treat original source locations " +
        "as metadata and prefer workspace-relative paths.";

    internal static string PrepareRepositoryFacts(
        string knowledge,
        string sourceProjectPath)
    {
        var sanitized = RepositoryLocationPattern().Replace(
            RemoveSourceProjectPath(knowledge, sourceProjectPath),
            "- **Project files:** Materialized in the isolated workspace.");
        var normalized = sanitized.ReplaceLineEndings("\n");
        var readmeMatch = ReadmeSignalPattern().Match(normalized);
        var readmeSummary = readmeMatch.Success
            ? SummarizeReadme(readmeMatch.Groups["content"].Value)
            : string.Empty;
        var focused = GeneratedRepositorySectionPattern().Replace(
            normalized,
            string.Empty);
        focused = ReadmeSignalPattern().Replace(focused, string.Empty);
        focused = GeneratedRepositoryDetailPattern().Replace(focused, string.Empty);
        focused = EmptyRepositoryNotesPattern().Replace(focused, string.Empty);
        focused = RepositoryTitlePattern().Replace(focused, "- **Project:** $1");
        focused = RepositoryProfileHeadingPattern().Replace(focused, string.Empty);
        focused = RepositorySubheadingPattern().Replace(focused, "### ");
        if (!string.IsNullOrWhiteSpace(readmeSummary))
        {
            focused +=
                $"{Environment.NewLine}{Environment.NewLine}" +
                $"### Project summary{Environment.NewLine}{readmeSummary}";
        }
        return ExcessBlankLinesPattern().Replace(focused, "\n\n").Trim();
    }

    internal static string SummarizeReadme(string readme)
    {
        var summaryLines = new List<string>();
        foreach (var line in readme.ReplaceLineEndings("\n").Split('\n'))
        {
            var candidate = line.Trim();
            if (summaryLines.Count == 0 &&
                (
                    candidate.Length == 0 ||
                    candidate.StartsWith('#') ||
                    candidate.StartsWith("![", StringComparison.Ordinal) ||
                    candidate.StartsWith("[![", StringComparison.Ordinal)
                ))
            {
                continue;
            }
            if (candidate.Length == 0 || candidate.StartsWith('#'))
            {
                break;
            }

            summaryLines.Add(candidate);
        }

        return Clip(string.Join(' ', summaryLines), 500);
    }

    internal static string ResolveCopilotSessionHome(string? inheritedHome = null)
    {
        inheritedHome ??= Environment.GetEnvironmentVariable("COPILOT_HOME");
        return string.IsNullOrWhiteSpace(inheritedHome)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".copilot")
            : Path.GetFullPath(inheritedHome);
    }

    internal static IReadOnlyDictionary<string, string?>? BuildProcessEnvironment(
        string agentRole,
        bool allowRemotePublication)
    {
        if (agentRole == "pre-mortem-sceptic")
        {
            var preMortemEnvironment = new Dictionary<string, string?>(
                PushGuard("pre-mortem-read-only"),
                StringComparer.Ordinal)
            {
                ["GH_TOKEN"] = null,
                ["GITHUB_TOKEN"] = null
            };
            return preMortemEnvironment;
        }
        if (agentRole != "release-engineer" ||
            allowRemotePublication)
        {
            return null;
        }

        var environment = new Dictionary<string, string?>(
            PushGuard("customer-approval-required"),
            StringComparer.Ordinal)
        {
            ["GH_TOKEN"] = "customer-approval-required",
            ["GITHUB_TOKEN"] = "customer-approval-required"
        };
        return environment;
    }

    private static IReadOnlyDictionary<string, string?> PushGuard(
        string marker) =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "remote.origin.pushurl",
            ["GIT_CONFIG_VALUE_0"] = $"disabled://{marker}"
        };

    internal static string PrepareRepositoryKnowledge(
        string knowledge,
        string sourceProjectPath,
        string workingDirectory)
    {
        var workspace = PrepareWorkspace(workingDirectory);
        var facts = PrepareRepositoryFacts(knowledge, sourceProjectPath);
        return string.IsNullOrWhiteSpace(facts)
            ? workspace
            : $"{workspace}{Environment.NewLine}{Environment.NewLine}{facts}";
    }

    internal static string RemoveSourceProjectPath(
        string value,
        string sourceProjectPath)
    {
        if (string.IsNullOrWhiteSpace(sourceProjectPath))
        {
            return value;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return value.Replace(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceProjectPath)),
            "the original source folder (intentionally unavailable)",
            comparison);
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
        string? dependency = null,
        bool canResumeSession = false) =>
        new()
        {
            Success = false,
            OutputSummary = summary,
            Error = error,
            FailureKind = failureKind,
            FailedDependency = dependency,
            CanResumeSession = canResumeSession
        };

    internal sealed record CopilotExecutionTimeouts(
        TimeSpan TurnTimeout,
        TimeSpan StallTimeout);

    internal sealed record RestrictedAgentAccess(
        string Root,
        string AgentId);

    internal static string ResponseContract(string agentRole) =>
        agentRole switch
        {
            "account-manager" => """
              Move a workable customer request toward delivery without treating clarity as customer approval.
              Return exactly these plain-text markers with no text before INTAKE_STATUS:
              INTAKE_STATUS: NEEDS_CLARIFICATION, AWAITING_CONFIRMATION, or CONFIRMED
              TASK_TITLE: a 3-8 word action phrase naming the concrete product change, with no conversational framing or trailing punctuation; never copy or truncate the opening message
              CUSTOMER_REPLY: one short plain-language line; ask one focused question when clarification is essential, ask the customer to validate your concise understanding when awaiting confirmation, or state that the confirmed brief is going to the team
              TASK_BRIEF: the complete proposed brief when awaiting confirmation or confirmed, otherwise NONE; the brief may continue on following lines
              Default to AWAITING_CONFIRMATION as soon as the delivery team can take a meaningful first action.
              Use NEEDS_CLARIFICATION only when the target product or visible outcome cannot be identified and no safe reversible assumption lets work start.
              Treat every earlier answer as settled and never ask for the same detail twice. The one allowed recap is the complete understanding presented for final confirmation.
              Use AWAITING_CONFIRMATION to ask "Do I understand correctly that you want ...? If yes, I'll ask the team to implement it."
              Return CONFIRMED only when the latest customer turn explicitly and unambiguously approves the most recent AWAITING_CONFIRMATION brief without changing it.
              When CONFIRMED, copy the approved TASK_BRIEF exactly and tell the customer you are asking the team to implement it now.
              If the customer rejects or corrects the proposed understanding, do not return CONFIRMED. Incorporate the correction, then clarify only a material gap or present a revised AWAITING_CONFIRMATION brief.
              A request for something the customer can click is actionable and requires an interactive result; do not ask whether it means pictures, a prototype, implementation, or deployment.
              Never ask about technologies, tools, file formats, implementation approaches, deployment, hosting, credentials, live release, pull requests, builds, or who deploys.
              Put reversible assumptions and decisions owned by designers, engineers, or release staff in TASK_BRIEF instead of asking the customer.
              """,
            "team-lead" => """
              Complete the assigned role in the isolated workspace; do not merely advise.
              Start with exactly: HANDOFF_STATUS: COMPLETE
              Return concise sections named Decision, Deliverable, Evidence, and Next owner.
              After those sections, output exactly one strict JSON task-profile document between these standalone sentinels:
              TEAM_TASK_PROFILES_V1_BEGIN
              {"Version":"task-profile-v1","Profiles":[{"Role":"software-engineer","Complexity":1,"ReasoningDepth":1,"ContextDemand":1,"ToolIntensity":1,"TaskTypeTags":["Implementation"],"Risk":"Low","RiskReason":"nonempty bounded reason","Confidence":0.8,"Rationales":["nonempty bounded rationale"]}]}
              TEAM_TASK_PROFILES_V1_END
              Property names and enum casing are exact. Integers are 1-10, confidence is 0-1, risk is Low/Medium/High/Critical, reasons are nonempty, and rationales contain 1-5 bounded entries.
              Allowed task tags are CustomerDialogue, Planning, Architecture, Design, Data, Implementation, Security, Quality, Documentation, Release, Feedback, and CrossCutting.
              Include exactly one profile for every already-planned downstream role in the supplied plan, excluding team-lead. Do not add, remove, or select roles; FlowPlanner remains role-selection authority.
              Then output exactly one strict pre-mortem plan between these standalone sentinels:
              PRE_MORTEM_PLAN_V1_BEGIN
              {"Version":"pre-mortem-plan-v1","AfterRoles":[]}
              PRE_MORTEM_PLAN_V1_END
              AfterRoles may contain only exact role IDs from the supplied downstream plan. Keep it empty when the assignment says the sceptic is unavailable. Do not emit any sentinel more than once.
              """,
            "pre-mortem-sceptic" => """
              Investigate the evaluated result independently. Do not modify product files.
              Start with exactly one marker: PRE_MORTEM_STATUS: CLEAR or PRE_MORTEM_STATUS: FINDINGS.
              Then output exactly one strict JSON document between these standalone sentinels:
              PRE_MORTEM_FINDINGS_V1_BEGIN
              {"Version":"pre-mortem-findings-v1","Findings":[{"FailureMode":"specific six-month failure chain","Evidence":"verifiable files, commands, observations, or authoritative URLs","MissedSignal":"current fact the evaluated result missed","Prevention":"precise change that breaks the failure chain"}]}
              PRE_MORTEM_FINDINGS_V1_END
              CLEAR requires an empty Findings array. FINDINGS requires 1-5 entries. Every entry needs concrete evidence; omit speculative, generic, duplicate, stylistic, or already-covered concerns.
              Do not emit HANDOFF_STATUS, PUSHBACK, or PRE_MORTEM_DISPOSITION markers.
              """,
            "product-manager" => """
              Respond directly to the customer in concise plain language after reviewing the original brief and execution ledger.
              End with exactly one standalone marker:
              REWORK_TARGET_ROLES: NONE
              Replace NONE with a comma-separated list of exact delivery role IDs from the ledger only when the customer's requested rework can be attributed to those roles. Do not guess or blame every role.
              """,
            _ => """
              Complete the assigned role in the isolated workspace; do not merely advise.
              Start with exactly one marker: HANDOFF_STATUS: COMPLETE or HANDOFF_STATUS: PUSHBACK.
              When pushing back, follow it with PUSHBACK_REASON: the exact missing detail and responsible upstream owner.
              Return concise sections named Decision, Deliverable, Evidence, and Next owner.
              If an upstream handoff is insufficient, stop this turn and select the PUSHBACK status.
              """
        };

    internal static string PreMortemRevisionResponseContract() => """
        Complete this role's response to the Pre-mortem Sceptic findings.
        Start with exactly: HANDOFF_STATUS: COMPLETE
        Return concise sections named Decision, Deliverable, Evidence, and Next owner.
        Return the complete current deliverable or plan, not a delta.
        End with exactly one marker: PRE_MORTEM_DISPOSITION: ADJUSTED or PRE_MORTEM_DISPOSITION: UNCHANGED.
        Use ADJUSTED only when the complete result materially changed after investigating the findings.
        Do not emit HANDOFF_STATUS: PUSHBACK or PUSHBACK_REASON in this turn.
        """;

    private static bool IsAccountManager(string agentRole) =>
        string.Equals(agentRole, "account-manager", StringComparison.Ordinal);

    private static bool IsProductManager(string agentRole) =>
        string.Equals(agentRole, "product-manager", StringComparison.Ordinal);

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

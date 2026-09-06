using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Core.Workflow;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private const string AccountManagerTools = "view,grep,glob";
    private const string PreMortemTools = "view,grep,glob,web_fetch";
    private const string HostControlledPublicationTools = "view,grep,glob";
    internal static readonly string[] GovernedGitMutationSubcommands =
    [
        "add",
        "am",
        "apply",
        "branch",
        "checkout",
        "checkout-index",
        "cherry-pick",
        "clean",
        "clone",
        "commit",
        "commit-tree",
        "config",
        "daemon",
        "fast-import",
        "fetch",
        "gc",
        "hash-object",
        "index-pack",
        "init",
        "imap-send",
        "http-fetch",
        "http-push",
        "ls-remote",
        "maintenance",
        "merge",
        "mergetool",
        "mktag",
        "mktree",
        "mv",
        "notes",
        "pack-objects",
        "p4",
        "prune",
        "pull",
        "push",
        "read-tree",
        "receive-pack",
        "rebase",
        "reflog",
        "remote",
        "replace",
        "reset",
        "restore",
        "revert",
        "rm",
        "send-pack",
        "svn",
        "sparse-checkout",
        "stash",
        "submodule",
        "switch",
        "symbolic-ref",
        "tag",
        "unpack-objects",
        "upload-archive",
        "upload-pack",
        "update-index",
        "update-ref",
        "update-server-info",
        "worktree",
        "write-tree"
    ];

    private static readonly string[] GovernedLocalMutationDenials =
        GovernedGitMutationSubcommands
            .SelectMany(command => new[]
            {
                $"--deny-tool=shell(git {command})",
                $"--deny-tool=shell(git.exe {command})"
            })
            .ToArray();

    private static readonly string[] GovernedGitIsolationBypassDenials =
    [
        "--deny-tool=shell(git.exe:*)",
        "--deny-tool=shell(git --git-dir)",
        "--deny-tool=shell(git --git-dir:*)",
        "--deny-tool=shell(git --work-tree)",
        "--deny-tool=shell(git --work-tree:*)",
        "--deny-tool=shell(git --namespace)",
        "--deny-tool=shell(git --namespace:*)",
        "--deny-tool=shell(git --config-env)",
        "--deny-tool=shell(git --config-env:*)",
        "--deny-tool=shell(git -c)"
    ];

    private static readonly string[] RemoteMutationDenials =
    [
        "--deny-tool=shell(git push)",
        "--deny-tool=shell(git send-pack)",
        "--deny-tool=shell(gh:*)",
        "--deny-tool=shell(ssh:*)",
        "--deny-tool=shell(scp:*)",
        "--deny-tool=shell(curl:*)",
        "--deny-tool=shell(wget:*)",
        "--deny-tool=shell(Invoke-WebRequest:*)",
        "--deny-tool=shell(Invoke-RestMethod:*)",
        "--deny-url=github.com",
        "--deny-url=api.github.com"
    ];

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
            string.IsNullOrWhiteSpace(context.DirectPrompt)
                ? promptRenderer.Render(
                    workflow.PromptTemplate,
                    BuildPromptValues(
                        context,
                        manifest.Instructions,
                        request.WorkingDirectory))
                : $"{context.DirectPrompt.Trim()}{Environment.NewLine}{Environment.NewLine}" +
                  $"## Quality Engineer role contract{Environment.NewLine}{Environment.NewLine}" +
                  manifest.Instructions.Trim(),
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
        using var governedGitIsolation = context.IsGovernedOutcomeVerification
            ? GovernedGitIsolationScope.Create(
                request.WorkingDirectory,
                context.GovernedRepositoryRelativePaths ??
                throw new InvalidOperationException(
                    "Governed execution has no trusted repository mapping."))
            : null;
        var environmentVariables = MergeProcessEnvironment(
            BuildProcessEnvironment(
                context.AgentRole,
                context.AllowRemotePublication,
                context.IsGovernedOutcomeVerification),
            governedGitIsolation?.EnvironmentVariables);
        var arguments = BuildCliArguments(
            request.WorkingDirectory,
            agentAccess.Root,
            agentAccess.AgentId,
            context.AgentRole,
            request.Model,
            request.Effort,
            request.CopilotSessionId,
            prompt,
            context.ResumeSession || context.RecoverInterruptedSession,
            context.IsHostControlledPublication,
            context.IsGovernedOutcomeVerification,
            (string.Equals(
                 context.AgentRole,
                 "release-engineer",
                 StringComparison.Ordinal) &&
             !context.AllowRemotePublication) ||
            context.IsGovernedOutcomeVerification).ToList();
        if (governedGitIsolation is not null)
        {
            arguments.Insert(
                Math.Max(0, arguments.Count - 2),
                $"--deny-tool=write({Path.GetFullPath(
                    governedGitIsolation.RootPath).Replace('\\', '/')})");
        }
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
            try
            {
                await hookRunner.RunAsync(
                    WorkspaceHookStage.AfterRun,
                    request.WorkingDirectory,
                    CancellationToken.None);
            }
            finally
            {
                governedGitIsolation?.RestoreAndValidate();
            }
        }

        if (governedGitIsolation?.UnauthorizedMetadataMutationDetected == true)
        {
            return Failure(
                "Governed execution modified disposable Git metadata.",
                "The host rejected and cleaned a .git mutation; authoritative Git metadata was restored unchanged.",
                AgentRunFailureKind.InvalidOutput);
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
            result.StandardError,
            request.WorkingDirectory);
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
        bool resumeSession = false,
        bool isHostControlledPublication = false,
        bool isGovernedOutcomeVerification = false,
        bool blockRemotePublication = false)
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
            ApplyToolPolicy(
                arguments,
                $"--available-tools={AccountManagerTools}",
                "--disable-builtin-mcps",
                "--no-custom-instructions",
                "--no-eager-powershell-resolution");
        }
        else if (string.Equals(
                     agentRole,
                     "pre-mortem-sceptic",
                     StringComparison.Ordinal))
        {
            ApplyToolPolicy(
                arguments,
                $"--available-tools={PreMortemTools}",
                "--disable-builtin-mcps",
                "--deny-tool=write,shell",
                "--disallow-temp-dir",
                "--no-custom-instructions",
                "--no-eager-powershell-resolution");
        }
        else if (isHostControlledPublication)
        {
            ApplyToolPolicy(
                arguments,
                $"--available-tools={HostControlledPublicationTools}",
                "--disable-builtin-mcps",
                "--deny-tool=write,shell",
                "--disallow-temp-dir",
                "--no-custom-instructions",
                "--no-eager-powershell-resolution");
        }
        else if (isGovernedOutcomeVerification)
        {
            ApplyToolPolicy(
                arguments,
                $"--available-tools={GovernedNonPublicationTools()}",
                "--disable-builtin-mcps",
                "--allow-tool=write",
                "--allow-tool=shell",
                "--disallow-temp-dir",
                "--secret-env-vars=COPILOT_GITHUB_TOKEN,GH_TOKEN,GITHUB_TOKEN,SSH_AUTH_SOCK,GIT_ASKPASS,SSH_ASKPASS");
            ApplyToolPolicyRange(arguments, GovernedLocalMutationDenials);
            ApplyToolPolicyRange(arguments, GovernedGitIsolationBypassDenials);
            ApplyToolPolicy(
                arguments,
                "--no-eager-powershell-resolution");
        }
        else
        {
            arguments.Add("--allow-all-tools");
        }
        if (isGovernedOutcomeVerification)
        {
            ApplyToolPolicyRange(
                arguments,
                GovernedGitMarkerWriteDenials(workingDirectory));
            ApplyToolPolicy(
                arguments,
                $"--deny-tool=write({Path.GetFullPath(Path.Combine(
                    workingDirectory,
                    ".ai-harness",
                    "outcome-verification")).Replace('\\', '/')})");
            ApplyToolPolicy(
                arguments,
                "--no-remote",
                "--no-remote-export");
        }
        if (blockRemotePublication)
        {
            ApplyToolPolicyRange(arguments, RemoteMutationDenials);
        }

        arguments.AddRange(["-p", prompt]);
        return arguments;
    }

    internal static string GovernedNonPublicationTools() =>
        OperatingSystem.IsWindows()
            ? "view,grep,glob,create,edit,powershell"
            : "view,grep,glob,create,edit,bash";

    private static IReadOnlyList<string> GovernedGitMarkerWriteDenials(
        string workingDirectory)
    {
        if (!Directory.Exists(workingDirectory))
        {
            return [];
        }

        var denials = new List<string>();
        foreach (var repository in CandidateFingerprintService.DiscoverRepositories(
                     workingDirectory))
        {
            var marker = Path.Combine(repository, ".git");
            if (Directory.Exists(marker))
            {
                denials.Add(
                    $"--deny-tool=write({Path.GetFullPath(marker).Replace('\\', '/')})");
                continue;
            }
            if (File.Exists(marker))
            {
                denials.Add(
                    $"--deny-tool=write({Path.GetFullPath(marker).Replace('\\', '/')})");
            }
        }
        return denials;
    }

    private static void ApplyToolPolicy(
        List<string> arguments,
        params string[] additions)
    {
        foreach (var addition in additions)
        {
            if (!arguments.Contains(addition, StringComparer.Ordinal))
            {
                arguments.Add(addition);
            }
        }
    }

    private static void ApplyToolPolicyRange(
        List<string> arguments,
        IEnumerable<string> additions)
    {
        foreach (var addition in additions)
        {
            if (!arguments.Contains(addition, StringComparer.Ordinal))
            {
                arguments.Add(addition);
            }
        }
    }

    private static IReadOnlyDictionary<string, string?>? MergeProcessEnvironment(
            IReadOnlyDictionary<string, string?>? first,
            IReadOnlyDictionary<string, string?>? second)
    {
        if (first is null && second is null)
        {
            return null;
        }

        var merged = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (first is not null)
        {
            foreach (var (key, value) in first)
            {
                merged[key] = value;
            }
        }
        if (second is not null)
        {
            foreach (var (key, value) in second)
            {
                merged[key] = value;
            }
        }
        return merged;
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
                context.IsPreMortemRevision,
                context.IsOutcomeQa) &&
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
        bool isPreMortemRevision = false,
        bool isOutcomeQa = false)
    {
        if (isOutcomeQa)
        {
            return HasOutcomeQaMarkers(output);
        }
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

    private static bool HasOutcomeQaMarkers(string output)
    {
        var lines = output.ReplaceLineEndings("\n")
            .Split('\n')
            .Select(line => line.Trim())
            .ToArray();
        return lines.Count(line => string.Equals(
                   line,
                   OutcomeVerificationRules.QaBeginMarker,
                   StringComparison.Ordinal)) == 1 &&
               lines.Count(line => string.Equals(
                   line,
                   OutcomeVerificationRules.QaEndMarker,
                   StringComparison.Ordinal)) == 1;
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
            ["outcome.context"] = context.OutcomeContext,
            ["outcome.contract"] = context.OutcomeContract,
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
        bool allowRemotePublication,
        bool isGovernedOutcomeVerification = false)
    {
        if (isGovernedOutcomeVerification)
        {
            return BuildGuardedEnvironment("governed-host-publication-only");
        }
        if (agentRole == "pre-mortem-sceptic")
        {
            return BuildGuardedEnvironment("pre-mortem-read-only");
        }
        if (agentRole != "release-engineer" ||
            allowRemotePublication)
        {
            return null;
        }

        return BuildGuardedEnvironment("customer-approval-required");
    }

    private static IReadOnlyDictionary<string, string?> BuildGuardedEnvironment(
        string marker)
    {
        return new Dictionary<string, string?>(
            PushGuard(marker),
            StringComparer.Ordinal)
        {
            ["GH_TOKEN"] = null,
            ["GITHUB_TOKEN"] = null,
            ["SSH_AUTH_SOCK"] = null,
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "Never",
            ["GIT_ASKPASS"] = "echo",
            ["SSH_ASKPASS"] = "echo",
            ["GIT_SSH_COMMAND"] = OperatingSystem.IsWindows()
                ? "cmd /c exit 1"
                : "false"
        };
    }

    private static IReadOnlyDictionary<string, string?> PushGuard(
        string marker) =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_CONFIG_COUNT"] = "5",
            ["GIT_CONFIG_KEY_0"] = "remote.origin.pushurl",
            ["GIT_CONFIG_VALUE_0"] = $"disabled://{marker}",
            ["GIT_CONFIG_KEY_1"] = "credential.helper",
            ["GIT_CONFIG_VALUE_1"] = string.Empty,
            ["GIT_CONFIG_KEY_2"] = "core.sshCommand",
            ["GIT_CONFIG_VALUE_2"] = OperatingSystem.IsWindows()
                ? "cmd /c exit 1"
                : "false",
            ["GIT_CONFIG_KEY_3"] = $"url.disabled://{marker}/.insteadOf",
            ["GIT_CONFIG_VALUE_3"] = "https://github.com/",
            ["GIT_CONFIG_KEY_4"] = $"url.disabled://{marker}/.insteadOf",
            ["GIT_CONFIG_VALUE_4"] = "git@github.com:"
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

    internal sealed class GovernedGitIsolationScope : IDisposable
    {
        private const string RecoveryJournalName = "recovery.json";
        private static readonly Lock ActiveWorkspaceLock = new();
        private static readonly HashSet<string> ActiveWorkspaces =
            new(PathComparer);
        private readonly string _workspace;
        private readonly string _stateRootPath;
        private readonly IReadOnlyList<GovernedGitMarkerState> _markerStates;
        private readonly IReadOnlySet<string> _initialForeignGitEntries;
        private bool _restored;

        private GovernedGitIsolationScope(
            string workspace,
            string rootPath,
            string stateRootPath,
            IReadOnlyDictionary<string, string?> environmentVariables,
            IReadOnlyList<GovernedGitMarkerState> markerStates,
            IReadOnlySet<string> initialForeignGitEntries)
        {
            _workspace = workspace;
            RootPath = rootPath;
            _stateRootPath = stateRootPath;
            EnvironmentVariables = environmentVariables;
            _markerStates = markerStates;
            _initialForeignGitEntries = initialForeignGitEntries;
        }

        public string RootPath { get; }

        public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; }

        public bool UnauthorizedMetadataMutationDetected { get; private set; }

        public static GovernedGitIsolationScope Create(
            string workingDirectory,
            IReadOnlyCollection<string>? trustedRelativePaths = null)
        {
            var worktree = Path.GetFullPath(workingDirectory);
            RecoverInterrupted(worktree);
            var rootPath = Path.Combine(
                Path.GetTempPath(),
                $"ai-harness-governed-git-{Guid.NewGuid():N}");
            var stateRootPath = GetStateRootPath(worktree);
            var shadowStagingRoot = Path.Combine(
                Path.GetDirectoryName(worktree)
                ?? throw new InvalidOperationException(
                    "Governed workspace has no parent directory."),
                $".ai-harness-git-shadow-{Guid.NewGuid():N}");
            RegisterActiveWorkspace(worktree);
            try
            {
                Directory.CreateDirectory(rootPath);
                Directory.CreateDirectory(stateRootPath);
                Directory.CreateDirectory(shadowStagingRoot);
                var repositories = trustedRelativePaths is null
                    ? CandidateFingerprintService.DiscoverRepositories(worktree)
                    : ResolveTrustedRepositories(
                        worktree,
                        trustedRelativePaths);
                if (repositories.Count == 0)
                {
                    throw new InvalidOperationException(
                        "Governed Copilot Git isolation requires a Git repository.");
                }
                var trustedMarkerPaths = repositories
                    .Select(repository =>
                        Path.GetFullPath(Path.Combine(repository, ".git")))
                    .ToHashSet(PathComparer);
                var initialForeignGitEntries = DiscoverGitEntries(worktree)
                    .Where(path => !trustedMarkerPaths.Contains(path))
                    .ToHashSet(PathComparer);
                var mappings = new List<GovernedGitMapping>(repositories.Count);
                var markerStates = new List<GovernedGitMarkerState>(
                    repositories.Count);
                for (var index = 0; index < repositories.Count; index++)
                {
                    var repository = repositories[index];
                    var markerPath = Path.Combine(repository, ".git");
                    if (!File.Exists(markerPath) || Directory.Exists(markerPath))
                    {
                        throw new InvalidOperationException(
                            "Governed Copilot Git isolation requires isolated worktrees whose .git metadata is an external pointer.");
                    }
                    var shadowGitDirectory = Path.Combine(
                        shadowStagingRoot,
                        $"repository-{index:D3}.git");
                    InitializeShadowRepository(
                        repository,
                        markerPath,
                        shadowGitDirectory);
                    var markerState = new GovernedGitMarkerState(
                        Path.GetFullPath(repository),
                        Convert.ToBase64String(File.ReadAllBytes(markerPath)),
                        Path.Combine(
                            stateRootPath,
                            $"repository-{index:D3}.pointer"),
                        CaptureMetadataManifest(shadowGitDirectory),
                        (int)File.GetAttributes(markerPath));
                    markerStates.Add(markerState);
                    mappings.Add(new GovernedGitMapping(
                        Path.GetFullPath(repository),
                        Path.GetFullPath(markerPath)));
                }

                WriteRecoveryJournal(
                    worktree,
                    stateRootPath,
                    markerStates);
                for (var index = 0; index < markerStates.Count; index++)
                {
                    var markerState = markerStates[index];
                    var markerPath = Path.Combine(
                        markerState.Repository,
                        ".git");
                    File.Move(markerPath, markerState.BackupPointerPath);
                    Directory.Move(
                        Path.Combine(
                            shadowStagingRoot,
                            $"repository-{index:D3}.git"),
                        markerPath);
                }
                DeleteDirectoryBestEffort(shadowStagingRoot);

                var globalConfigPath = Path.Combine(rootPath, "global.gitconfig");
                File.WriteAllText(globalConfigPath, string.Empty);
                var realGit = ResolveGitExecutable();
                CreateGitDispatcher(rootPath, realGit, mappings);
                var primary = mappings
                    .OrderBy(mapping =>
                        Path.GetRelativePath(worktree, mapping.Repository).Length)
                    .First();
                var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["PATH"] = rootPath + Path.PathSeparator +
                               Environment.GetEnvironmentVariable("PATH"),
                    ["GIT_DIR"] = primary.GitDirectory,
                    ["GIT_WORK_TREE"] = primary.Repository,
                    ["GIT_INDEX_FILE"] = Path.Combine(
                        primary.GitDirectory,
                        "index"),
                    ["GIT_COMMON_DIR"] = null,
                    ["GIT_OBJECT_DIRECTORY"] = Path.Combine(
                        primary.GitDirectory,
                        "objects"),
                    ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = null,
                    ["GIT_OPTIONAL_LOCKS"] = "0",
                    ["GIT_ALLOW_PROTOCOL"] = "file",
                    ["GIT_PROTOCOL_FROM_USER"] = "0",
                    ["GIT_CONFIG_NOSYSTEM"] = "1",
                    ["GIT_CONFIG_SYSTEM"] = null,
                    ["GIT_CONFIG_GLOBAL"] = globalConfigPath
                };
                return new GovernedGitIsolationScope(
                    worktree,
                    rootPath,
                    stateRootPath,
                    environment,
                    markerStates,
                    initialForeignGitEntries);
            }
            catch
            {
                RecoverFromStateRoot(worktree, stateRootPath);
                DeleteDirectoryBestEffort(shadowStagingRoot);
                DeleteDirectoryBestEffort(rootPath);
                UnregisterActiveWorkspace(worktree);
                throw;
            }
        }

        public void RestoreAndValidate()
        {
            if (_restored)
            {
                return;
            }
            _restored = true;
            var trustedMarkerPaths = _markerStates
                .Select(state =>
                    Path.GetFullPath(Path.Combine(state.Repository, ".git")))
                .ToHashSet(PathComparer);
            foreach (var entry in DiscoverGitEntries(_workspace).Where(path =>
                         !trustedMarkerPaths.Contains(path) &&
                         !_initialForeignGitEntries.Contains(path)))
            {
                UnauthorizedMetadataMutationDetected = true;
                DeleteGitEntry(entry);
            }
            foreach (var markerState in _markerStates)
            {
                var markerPath = Path.Combine(markerState.Repository, ".git");
                if (!Directory.Exists(markerPath) ||
                    !MetadataManifestEquals(
                        markerState.InitialShadowManifest,
                        CaptureMetadataManifest(markerPath)))
                {
                    UnauthorizedMetadataMutationDetected = true;
                }
            }
            RecoverFromStateRoot(
                Path.GetFullPath(
                    JsonSerializer.Deserialize<GovernedGitRecoveryJournal>(
                        File.ReadAllText(
                            Path.Combine(_stateRootPath, RecoveryJournalName)))
                    ?.Workspace ??
                    throw new InvalidOperationException(
                        "Governed Git recovery journal is invalid.")),
                _stateRootPath);
            DeleteDirectoryBestEffort(RootPath);
            UnregisterActiveWorkspace(_workspace);
        }

        public void Dispose()
        {
            try
            {
                RestoreAndValidate();
            }
            finally
            {
                DeleteDirectoryBestEffort(RootPath);
                if (_restored)
                {
                    UnregisterActiveWorkspace(_workspace);
                }
            }
        }

        internal void LeaveInterruptedForRecoveryTest()
        {
            _restored = true;
            DeleteDirectoryBestEffort(RootPath);
            UnregisterActiveWorkspace(_workspace);
        }

        internal static void RecoverInterrupted(string workingDirectory)
        {
            var workspace = Path.GetFullPath(workingDirectory);
            lock (ActiveWorkspaceLock)
            {
                if (ActiveWorkspaces.Contains(workspace))
                {
                    throw new InvalidOperationException(
                        "Governed Git metadata is currently owned by a live execution in this workspace.");
                }
            }
            RecoverFromStateRoot(workspace, GetStateRootPath(workspace));
        }

        private static void RegisterActiveWorkspace(string workspace)
        {
            lock (ActiveWorkspaceLock)
            {
                if (!ActiveWorkspaces.Add(Path.GetFullPath(workspace)))
                {
                    throw new InvalidOperationException(
                        "A governed execution already owns this workspace.");
                }
            }
        }

        private static void UnregisterActiveWorkspace(string workspace)
        {
            lock (ActiveWorkspaceLock)
            {
                ActiveWorkspaces.Remove(Path.GetFullPath(workspace));
            }
        }

        private static string GetStateRootPath(string workspace)
        {
            var normalized = Path.GetFullPath(workspace)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)
                .Normalize(NormalizationForm.FormKC);
            if (OperatingSystem.IsWindows())
            {
                normalized = normalized.ToUpperInvariant();
            }
            var key = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
                .ToLowerInvariant();
            return Path.Combine(
                Path.GetTempPath(),
                "ai-harness-governed-git-state",
                key);
        }

        private static void WriteRecoveryJournal(
            string workspace,
            string stateRootPath,
            IReadOnlyList<GovernedGitMarkerState> markerStates)
        {
            var journal = new GovernedGitRecoveryJournal(
                Path.GetFullPath(workspace),
                markerStates.Select(state =>
                    new GovernedGitRecoveryEntry(
                        state.Repository,
                        state.OriginalPointerBase64,
                        state.BackupPointerPath,
                        state.OriginalAttributes)).ToArray());
            var journalPath = Path.Combine(
                stateRootPath,
                RecoveryJournalName);
            var temporaryPath = journalPath + ".tmp";
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(journal),
                Encoding.UTF8);
            File.Move(temporaryPath, journalPath, overwrite: true);
        }

        private static void RecoverFromStateRoot(
            string workspace,
            string stateRootPath)
        {
            if (!Directory.Exists(stateRootPath))
            {
                return;
            }
            var journalPath = Path.Combine(
                stateRootPath,
                RecoveryJournalName);
            if (!File.Exists(journalPath))
            {
                DeleteDirectoryBestEffort(stateRootPath);
                return;
            }

            GovernedGitRecoveryJournal journal;
            try
            {
                journal = JsonSerializer.Deserialize<GovernedGitRecoveryJournal>(
                              File.ReadAllText(journalPath))
                          ?? throw new InvalidOperationException(
                              "Governed Git recovery journal is empty.");
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    "Governed Git recovery journal is corrupt; authoritative metadata was not touched.",
                    exception);
            }

            if (!PathsEqual(journal.Workspace, workspace))
            {
                throw new InvalidOperationException(
                    "Governed Git recovery journal belongs to a different workspace.");
            }
            foreach (var entry in journal.Repositories)
            {
                var repository = Path.GetFullPath(entry.Repository);
                if (!IsContainedOrEqual(workspace, repository))
                {
                    throw new InvalidOperationException(
                        "Governed Git recovery journal contains an out-of-workspace repository.");
                }
                var markerPath = Path.Combine(repository, ".git");
                var pointerBytes = File.Exists(entry.BackupPointerPath)
                    ? File.ReadAllBytes(entry.BackupPointerPath)
                    : Convert.FromBase64String(entry.OriginalPointerBase64);
                DeleteGitEntry(markerPath);
                Directory.CreateDirectory(repository);
                var temporaryMarker = Path.Combine(
                    repository,
                    $".git.restore-{Guid.NewGuid():N}");
                File.WriteAllBytes(temporaryMarker, pointerBytes);
                File.SetAttributes(
                    temporaryMarker,
                    (FileAttributes)entry.OriginalAttributes);
                File.Move(temporaryMarker, markerPath);
            }
            DeleteDirectoryBestEffort(stateRootPath);
        }

        private static IReadOnlyList<string> CaptureMetadataManifest(
            string gitDirectory)
        {
            if (!Directory.Exists(gitDirectory))
            {
                return [];
            }
            var result = new List<string>();
            foreach (var directory in Directory.EnumerateDirectories(
                         gitDirectory,
                         "*",
                         SearchOption.AllDirectories))
            {
                result.Add(
                    "D:" + Path.GetRelativePath(gitDirectory, directory)
                        .Replace('\\', '/'));
            }
            foreach (var file in Directory.EnumerateFiles(
                         gitDirectory,
                         "*",
                         SearchOption.AllDirectories))
            {
                var bytes = File.ReadAllBytes(file);
                result.Add(
                    "F:" +
                    Path.GetRelativePath(gitDirectory, file)
                        .Replace('\\', '/') +
                    ":" +
                    bytes.LongLength +
                    ":" +
                    Convert.ToHexString(SHA256.HashData(bytes))
                        .ToLowerInvariant());
            }
            return result.Order(StringComparer.Ordinal).ToArray();
        }

        private static IReadOnlyList<string> DiscoverGitEntries(string workspace)
        {
            var result = new List<string>();
            var pending = new Stack<string>();
            pending.Push(Path.GetFullPath(workspace));
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (string.Equals(
                            Path.GetFileName(entry),
                            ".git",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(Path.GetFullPath(entry));
                        continue;
                    }
                    if (Directory.Exists(entry) &&
                        (File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(entry);
                    }
                }
            }
            return result;
        }

        private static bool MetadataManifestEquals(
            IReadOnlyList<string> expected,
            IReadOnlyList<string> actual) =>
            expected.SequenceEqual(actual, StringComparer.Ordinal);

        private static void DeleteGitEntry(string markerPath)
        {
            if (File.Exists(markerPath))
            {
                File.SetAttributes(markerPath, FileAttributes.Normal);
                File.Delete(markerPath);
                return;
            }
            if (!Directory.Exists(markerPath))
            {
                return;
            }
            var attributes = File.GetAttributes(markerPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(markerPath);
                return;
            }
            DeleteDirectoryBestEffort(markerPath);
            if (Directory.Exists(markerPath))
            {
                throw new IOException(
                    $"Could not remove disposable governed Git metadata '{markerPath}'.");
            }
        }

        private static bool IsContainedOrEqual(string root, string path)
        {
            var relative = Path.GetRelativePath(
                Path.GetFullPath(root),
                Path.GetFullPath(path));
            return !Path.IsPathRooted(relative) &&
                   !relative.Equals("..", StringComparison.Ordinal) &&
                   !relative.StartsWith(
                       ".." + Path.DirectorySeparatorChar,
                       StringComparison.Ordinal) &&
                   !relative.StartsWith(
                       ".." + Path.AltDirectorySeparatorChar,
                       StringComparison.Ordinal);
        }

        private static bool PathsEqual(string left, string right) =>
            string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                PathComparison);

        private static IReadOnlyList<string> ResolveTrustedRepositories(
            string workspace,
            IReadOnlyCollection<string> relativePaths)
        {
            if (relativePaths.Count == 0 ||
                relativePaths.Count !=
                relativePaths.Distinct(StringComparer.Ordinal).Count())
            {
                throw new InvalidOperationException(
                    "Governed execution requires a non-empty unique trusted repository mapping.");
            }
            var repositories = new List<string>(relativePaths.Count);
            foreach (var relativePath in relativePaths)
            {
                if (string.IsNullOrWhiteSpace(relativePath) ||
                    Path.IsPathRooted(relativePath))
                {
                    throw new InvalidOperationException(
                        $"Trusted repository path '{relativePath}' is invalid.");
                }
                var repository = Path.GetFullPath(
                    Path.Combine(
                        workspace,
                        relativePath.Replace(
                            '/',
                            Path.DirectorySeparatorChar)));
                var relative = Path.GetRelativePath(workspace, repository);
                if (Path.IsPathRooted(relative) ||
                    relative.Equals("..", StringComparison.Ordinal) ||
                    relative.StartsWith(
                        ".." + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal) ||
                    relative.StartsWith(
                        ".." + Path.AltDirectorySeparatorChar,
                        StringComparison.Ordinal) ||
                    !RepositoryAnalyzer.IsGitRepository(repository))
                {
                    throw new InvalidOperationException(
                        $"Trusted repository '{relativePath}' is outside the governed workspace or is no longer a Git repository.");
                }
                repositories.Add(repository);
            }
            return repositories
                .OrderBy(
                    path => Path.GetRelativePath(workspace, path),
                    StringComparer.Ordinal)
                .ToArray();
        }

        private static void InitializeShadowRepository(
            string repository,
            string markerPath,
            string shadowGitDirectory)
        {
            var gitDirectory = ResolveGitDirectory(repository, markerPath);
            var commonDirectory = ResolveCommonGitDirectory(gitDirectory);
            var objectFormat = ReadObjectFormat(gitDirectory, commonDirectory);
            Directory.CreateDirectory(Path.Combine(shadowGitDirectory, "objects", "info"));
            Directory.CreateDirectory(Path.Combine(shadowGitDirectory, "objects", "pack"));
            Directory.CreateDirectory(Path.Combine(shadowGitDirectory, "refs", "heads"));
            File.WriteAllText(
                Path.Combine(shadowGitDirectory, "config"),
                objectFormat == "sha256"
                    ? """
                      [core]
                          repositoryformatversion = 1
                          bare = false
                          filemode = true
                          logallrefupdates = false
                      [extensions]
                          objectformat = sha256
                      """
                    : """
                [core]
                    repositoryformatversion = 0
                    bare = false
                    filemode = true
                    logallrefupdates = false
                """);
            SeedReadOnlyIdentity(repository, markerPath, shadowGitDirectory);
        }

        private static void CreateGitDispatcher(
            string rootPath,
            string realGit,
            IReadOnlyList<GovernedGitMapping> mappings)
        {
            if (OperatingSystem.IsWindows())
            {
                var scriptPath = Path.Combine(rootPath, "git-dispatch.ps1");
                var mappingEntries = string.Join(
                    "," + Environment.NewLine,
                    mappings
                        .OrderByDescending(item => item.Repository.Length)
                        .Select(item =>
                            "    [pscustomobject]@{ Repository = '" +
                            PowerShellQuote(item.Repository) +
                            "'; GitDirectory = '" +
                            PowerShellQuote(item.GitDirectory) +
                            "' }"));
                var blockedCommands = string.Join(
                    ", ",
                    GovernedGitMutationSubcommands.Select(command =>
                        $"'{PowerShellQuote(command)}'"));
                File.WriteAllText(
                    scriptPath,
                    $$"""
                      $ErrorActionPreference = 'Stop'
                      $target = [IO.Path]::GetFullPath((Get-Location).Path)
                      $expectDirectory = $false
                      $forwarded = @()
                      foreach ($rawArgument in $args) {
                          $argument = $rawArgument.Trim('"')
                          if (
                              $argument -ceq '-c' -or
                              $argument -ceq '--git-dir' -or
                              $argument.StartsWith('--git-dir=') -or
                              $argument -ceq '--work-tree' -or
                              $argument.StartsWith('--work-tree=') -or
                              $argument -ceq '--namespace' -or
                              $argument.StartsWith('--namespace=') -or
                              $argument -ceq '--config-env' -or
                              $argument.StartsWith('--config-env=')
                          ) {
                              [Console]::Error.WriteLine('Git repository-selection and config override flags are disabled.')
                              exit 126
                          }
                          $forwarded += $argument
                          if ($expectDirectory) {
                              $target = if ([IO.Path]::IsPathRooted($argument)) {
                                  [IO.Path]::GetFullPath($argument)
                              } else {
                                  [IO.Path]::GetFullPath([IO.Path]::Combine($target, $argument))
                              }
                              $expectDirectory = $false
                              continue
                          }
                          if ($argument -eq '-C') {
                              $expectDirectory = $true
                          }
                      }
                      $blockedCommands = @({{blockedCommands}})
                      $subcommand = $null
                      for ($index = 0; $index -lt $forwarded.Count; $index++) {
                          if ($forwarded[$index] -ceq '-C') {
                              $index++
                              continue
                          }
                          if ($forwarded[$index].StartsWith('-')) {
                              continue
                          }
                          $subcommand = $forwarded[$index]
                          break
                      }
                      if ($null -eq $subcommand -or $blockedCommands -contains $subcommand) {
                          [Console]::Error.WriteLine('This Git subcommand is not permitted in a governed turn.')
                          exit 126
                      }
                      $mappings = @(
                      {{mappingEntries}}
                      )
                      $mapping = $mappings | Where-Object {
                          $target -eq $_.Repository -or
                          $target.StartsWith(
                              $_.Repository + [IO.Path]::DirectorySeparatorChar,
                              [StringComparison]::OrdinalIgnoreCase)
                      } | Select-Object -First 1
                      if ($null -eq $mapping) {
                          [Console]::Error.WriteLine('Git access is outside the governed repository set.')
                          exit 128
                      }
                      $env:GIT_DIR = $mapping.GitDirectory
                      $env:GIT_WORK_TREE = $mapping.Repository
                      $env:GIT_INDEX_FILE = [IO.Path]::Combine($mapping.GitDirectory, 'index')
                      $env:GIT_OBJECT_DIRECTORY = [IO.Path]::Combine($mapping.GitDirectory, 'objects')
                      Remove-Item Env:GIT_COMMON_DIR -ErrorAction SilentlyContinue
                      Remove-Item Env:GIT_ALTERNATE_OBJECT_DIRECTORIES -ErrorAction SilentlyContinue
                      & '{{PowerShellQuote(realGit)}}' @forwarded
                      exit $LASTEXITCODE
                      """.ReplaceLineEndings(Environment.NewLine));
                File.WriteAllText(
                    Path.Combine(rootPath, "git.cmd"),
                    "@echo off\r\n" +
                    "powershell.exe -NoLogo -NoProfile -NonInteractive " +
                    "-ExecutionPolicy Bypass -File \"%~dp0git-dispatch.ps1\" %*\r\n" +
                    "exit /b %ERRORLEVEL%\r\n");
                return;
            }

            var cases = string.Join(
                Environment.NewLine,
                mappings
                    .OrderByDescending(item => item.Repository.Length)
                    .Select(item =>
                        $"  {ShellQuote(item.Repository)}|{ShellQuote(item.Repository)}/*) " +
                        $"git_dir={ShellQuote(item.GitDirectory)}; " +
                        $"work_tree={ShellQuote(item.Repository)} ;;"));
            var blockedCase = string.Join(
                "|",
                GovernedGitMutationSubcommands);
            var dispatcher = Path.Combine(rootPath, "git");
            File.WriteAllText(
                dispatcher,
                $$"""
                  #!/bin/sh
                  target=$PWD
                  expect_directory=0
                  subcommand=
                  for argument in "$@"; do
                    case "$argument" in
                      -c|--git-dir|--git-dir=*|--work-tree|--work-tree=*|--namespace|--namespace=*|--config-env|--config-env=*)
                        echo "Git repository-selection and config override flags are disabled." >&2
                        exit 126
                        ;;
                    esac
                    if [ "$expect_directory" -eq 1 ]; then
                      case "$argument" in
                        /*) target=$argument ;;
                        *) target=$target/$argument ;;
                      esac
                      target=$(cd "$target" 2>/dev/null && pwd -P) || exit 128
                      expect_directory=0
                    elif [ "$argument" = "-C" ]; then
                      expect_directory=1
                    elif [ -z "$subcommand" ]; then
                      case "$argument" in
                        -*) ;;
                        *) subcommand=$argument ;;
                      esac
                    fi
                  done
                  case "$subcommand" in
                    ""|{{blockedCase}})
                      echo "This Git subcommand is not permitted in a governed turn." >&2
                      exit 126
                      ;;
                  esac
                  case "$target" in
                  {{cases}}
                    *) echo "Git access is outside the governed repository set." >&2; exit 128 ;;
                  esac
                  export GIT_DIR="$git_dir"
                  export GIT_WORK_TREE="$work_tree"
                  export GIT_INDEX_FILE="$git_dir/index"
                  export GIT_OBJECT_DIRECTORY="$git_dir/objects"
                  unset GIT_COMMON_DIR GIT_ALTERNATE_OBJECT_DIRECTORIES
                  exec {{ShellQuote(realGit)}} "$@"
                  """.ReplaceLineEndings("\n"));
            File.SetUnixFileMode(
                dispatcher,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }

        private static string ResolveGitExecutable()
        {
            var fileName = OperatingSystem.IsWindows() ? "git.exe" : "git";
            foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                         .Split(
                             Path.PathSeparator,
                             StringSplitOptions.RemoveEmptyEntries |
                             StringSplitOptions.TrimEntries))
            {
                var candidate = Path.Combine(entry.Trim('"'), fileName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            throw new InvalidOperationException(
                "Governed Git isolation could not resolve the host Git executable.");
        }

        private static string PowerShellQuote(string value) =>
            value.Replace("'", "''", StringComparison.Ordinal);

        private static string ShellQuote(string value) =>
            "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

        private static string ResolveGitDirectory(
            string repository,
            string markerPath)
        {
            var markerLine = File.ReadLines(markerPath).FirstOrDefault()?.Trim();
            if (markerLine is null ||
                !markerLine.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The governed workspace has an invalid .git pointer file.");
            }
            return ResolveGitPath(
                repository,
                markerLine["gitdir:".Length..].Trim());
        }

        private static string ReadObjectFormat(
            string gitDirectory,
            string commonDirectory)
        {
            foreach (var configPath in new[]
                     {
                         Path.Combine(gitDirectory, "config.worktree"),
                         Path.Combine(gitDirectory, "config"),
                         Path.Combine(commonDirectory, "config")
                     }.Distinct(PathComparer))
            {
                if (!File.Exists(configPath))
                {
                    continue;
                }
                var inExtensions = false;
                foreach (var rawLine in File.ReadLines(configPath))
                {
                    var line = rawLine.Trim();
                    if (line.StartsWith("[", StringComparison.Ordinal))
                    {
                        inExtensions = string.Equals(
                            line,
                            "[extensions]",
                            StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!inExtensions)
                    {
                        continue;
                    }
                    var separator = line.IndexOf('=');
                    if (separator > 0 &&
                        string.Equals(
                            line[..separator].Trim(),
                            "objectformat",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        var value = line[(separator + 1)..].Trim();
                        if (value is "sha1" or "sha256")
                        {
                            return value;
                        }
                    }
                }
            }
            return "sha1";
        }

        private static void DeleteDirectoryBestEffort(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }
            try
            {
                foreach (var file in Directory.EnumerateFiles(
                             path,
                             "*",
                             SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                foreach (var directory in Directory.EnumerateDirectories(
                             path,
                             "*",
                             SearchOption.AllDirectories))
                {
                    File.SetAttributes(directory, FileAttributes.Directory);
                }
                Directory.Delete(path, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // The isolated Git metadata is non-authoritative and cleanup is best effort.
            }
        }

        private static void SeedReadOnlyIdentity(
            string repository,
            string markerPath,
            string shadowGitDirectory)
        {
            var markerLine = File.ReadLines(markerPath).FirstOrDefault()?.Trim();
            if (markerLine is null ||
                !markerLine.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The governed workspace has an invalid .git pointer file.");
            }
            var gitDirectory = ResolveGitPath(
                repository,
                markerLine["gitdir:".Length..].Trim());
            var commonDirectory = ResolveCommonGitDirectory(gitDirectory);
            var headText = File.ReadAllText(Path.Combine(gitDirectory, "HEAD"))
                .ReplaceLineEndings("\n");
            File.WriteAllText(
                Path.Combine(shadowGitDirectory, "HEAD"),
                headText);

            if (headText.Trim().StartsWith("ref:", StringComparison.Ordinal))
            {
                var reference = headText.Trim()["ref:".Length..].Trim();
                var objectId = ReadReference(gitDirectory, commonDirectory, reference);
                if (!string.IsNullOrWhiteSpace(objectId))
                {
                    var shadowReference = Path.Combine(
                        shadowGitDirectory,
                        reference.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(shadowReference)!);
                    File.WriteAllText(shadowReference, objectId + "\n");
                }
            }

            var sourceIndex = Path.Combine(gitDirectory, "index");
            if (File.Exists(sourceIndex))
            {
                File.Copy(
                    sourceIndex,
                    Path.Combine(shadowGitDirectory, "index"),
                    overwrite: true);
            }

            CopyReferenceSnapshot(
                gitDirectory,
                commonDirectory,
                shadowGitDirectory);
            CopyImmutableObjectDatabase(
                gitDirectory,
                commonDirectory,
                shadowGitDirectory);
        }

        private static void CopyReferenceSnapshot(
            string gitDirectory,
            string commonDirectory,
            string shadowGitDirectory)
        {
            foreach (var root in new[] { commonDirectory, gitDirectory }.Distinct(PathComparer))
            {
                CopyDirectorySnapshot(
                    Path.Combine(root, "refs"),
                    Path.Combine(shadowGitDirectory, "refs"));
                CopyFileIfExists(
                    Path.Combine(root, "packed-refs"),
                    Path.Combine(shadowGitDirectory, "packed-refs"));
                CopyFileIfExists(
                    Path.Combine(root, "shallow"),
                    Path.Combine(shadowGitDirectory, "shallow"));
            }
        }

        private static void CopyImmutableObjectDatabase(
            string gitDirectory,
            string commonDirectory,
            string shadowGitDirectory)
        {
            foreach (var objectDirectory in DiscoverObjectDatabaseRoots(
                         gitDirectory,
                         commonDirectory))
            {
                CopyDirectorySnapshot(
                    objectDirectory,
                    Path.Combine(shadowGitDirectory, "objects"),
                    ShouldSkipShadowObjectEntry);
            }
        }

        private static IReadOnlyList<string> DiscoverObjectDatabaseRoots(
            string gitDirectory,
            string commonDirectory)
        {
            var roots = new List<string>();
            var visited = new HashSet<string>(PathComparer);
            var pending = new Stack<string>(
                new[] { commonDirectory, gitDirectory }
                    .Distinct(PathComparer)
                    .Select(root => Path.Combine(root, "objects")));
            while (pending.Count > 0)
            {
                var objectDirectory = Path.GetFullPath(pending.Pop());
                if (!visited.Add(objectDirectory) ||
                    !Directory.Exists(objectDirectory))
                {
                    continue;
                }

                roots.Add(objectDirectory);
                var alternatesPath = Path.Combine(
                    objectDirectory,
                    "info",
                    "alternates");
                if (!File.Exists(alternatesPath))
                {
                    continue;
                }

                foreach (var alternate in File.ReadLines(alternatesPath)
                             .Select(line => line.Trim())
                             .Where(line => line.Length > 0))
                {
                    var alternateDirectory = ResolveGitPath(
                        objectDirectory,
                        alternate);
                    if (!Directory.Exists(alternateDirectory))
                    {
                        throw new InvalidOperationException(
                            $"The governed workspace references a missing alternate object directory '{alternateDirectory}'.");
                    }

                    pending.Push(alternateDirectory);
                }
            }

            return roots;
        }

        private static void CopyDirectorySnapshot(
            string sourceDirectory,
            string destinationDirectory,
            Func<string, bool>? skipRelativePath = null)
        {
            if (!Directory.Exists(sourceDirectory))
            {
                return;
            }

            Directory.CreateDirectory(destinationDirectory);
            foreach (var directory in Directory.EnumerateDirectories(
                         sourceDirectory,
                         "*",
                         SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDirectory, directory)
                    .Replace('\\', '/');
                if (skipRelativePath?.Invoke(relativePath) == true)
                {
                    continue;
                }

                Directory.CreateDirectory(Path.Combine(
                    destinationDirectory,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
            }

            foreach (var file in Directory.EnumerateFiles(
                         sourceDirectory,
                         "*",
                         SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDirectory, file)
                    .Replace('\\', '/');
                if (skipRelativePath?.Invoke(relativePath) == true)
                {
                    continue;
                }

                var destinationPath = Path.Combine(
                    destinationDirectory,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Copy(file, destinationPath, overwrite: true);
            }
        }

        private static bool ShouldSkipShadowObjectEntry(string relativePath) =>
            relativePath.Equals("info/alternates", PathComparison) ||
            relativePath.Equals("info/http-alternates", PathComparison);

        private static void CopyFileIfExists(
            string sourcePath,
            string destinationPath)
        {
            if (!File.Exists(sourcePath))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }

        private static string ResolveCommonGitDirectory(string gitDirectory)
        {
            var commonPointer = Path.Combine(gitDirectory, "commondir");
            if (!File.Exists(commonPointer))
            {
                return gitDirectory;
            }
            return ResolveGitPath(
                gitDirectory,
                File.ReadAllText(commonPointer).Trim());
        }

        private static string ResolveGitPath(
            string baseDirectory,
            string path) =>
            Path.GetFullPath(
                Path.IsPathRooted(path)
                    ? path
                    : Path.Combine(baseDirectory, path));

        private static StringComparer PathComparer =>
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        private static StringComparison PathComparison =>
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        private static string? ReadReference(
            string gitDirectory,
            string commonDirectory,
            string reference)
        {
            foreach (var root in new[] { gitDirectory, commonDirectory })
            {
                var loosePath = Path.Combine(
                    root,
                    reference.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(loosePath))
                {
                    return File.ReadAllText(loosePath).Trim();
                }
            }

            var packedRefs = Path.Combine(commonDirectory, "packed-refs");
            if (!File.Exists(packedRefs))
            {
                return null;
            }
            foreach (var line in File.ReadLines(packedRefs))
            {
                if (line.Length == 0 ||
                    line[0] is '#' or '^')
                {
                    continue;
                }
                var separator = line.IndexOf(' ');
                if (separator > 0 &&
                    string.Equals(
                        line[(separator + 1)..].Trim(),
                        reference,
                        StringComparison.Ordinal))
                {
                    return line[..separator].Trim();
                }
            }
            return null;
        }

        private sealed record GovernedGitMapping(
            string Repository,
            string GitDirectory);

        private sealed record GovernedGitMarkerState(
            string Repository,
            string OriginalPointerBase64,
            string BackupPointerPath,
            IReadOnlyList<string> InitialShadowManifest,
            int OriginalAttributes);

        private sealed record GovernedGitRecoveryJournal(
            string Workspace,
            IReadOnlyList<GovernedGitRecoveryEntry> Repositories);

        private sealed record GovernedGitRecoveryEntry(
            string Repository,
            string OriginalPointerBase64,
            string BackupPointerPath,
            int OriginalAttributes);
    }
}

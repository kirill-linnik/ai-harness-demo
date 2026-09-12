using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Security;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Data;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiHarnessDemo.Services;

internal sealed record DurableExecutionInstructions(
    string Prompt,
    string WorkflowRevision,
    bool Recovered);

/// <summary>Reasoning host that runs enabled custom agents through the local Copilot CLI.</summary>
public sealed partial class CopilotReasoningHost(
    FlowAgentSnapshotService snapshotService,
    ProcessRunner processRunner,
    WorkflowDefinitionProvider workflowProvider,
    CopilotCliRuntime copilotCliRuntime,
    WorkflowPromptRenderer promptRenderer,
    WorkspaceHookRunner hookRunner,
    CopilotSessionJournal sessionJournal,
    PermissionProfileResolver permissionResolver,
    AgentManifestStager manifestStager,
    IDbContextFactory<HarnessDbContext> databaseFactory,
    ILogger<CopilotReasoningHost> logger)
    : ReasoningHost(new ReasoningHostConfig("copilot-cli"))
{
    private const int MaximumPromptCharacters = 16_000;
    internal const int MaximumInlinePromptCharacters = 8_192;
    internal const int MaximumProcessCommandLineCharacters = 30_000;
    internal const int MaximumPlanningRosterCharacters =
        TeamPlanParser.MaximumDocumentCharacters;
    internal const int MaximumPlanningBriefCharacters =
        IntakeV2Parser.MaximumJsonCharacters >
        AdvisoryPromotionSeedParser.MaximumDocumentCharacters
            ? IntakeV2Parser.MaximumJsonCharacters
            : AdvisoryPromotionSeedParser.MaximumDocumentCharacters;
    internal const int MaximumPlanningTaskCharacters =
        MaximumPlanningBriefCharacters +
        MaximumPlanningRosterCharacters +
        32_768;
    internal const int MaximumReviewClassificationTaskCharacters =
        FlowOutcomeParser.MaximumDocumentCharacters +
        ReviewFeedbackParser.MaximumRequestedChangeCharacters +
        8_192;
    private const int MaximumPlanningPromptCharacters =
        MaximumPlanningTaskCharacters +
        TeamPlanParser.MaximumDocumentCharacters;
    private const int MaximumReviewClassificationPromptCharacters =
        MaximumReviewClassificationTaskCharacters +
        ReviewFeedbackParser.MaximumDocumentCharacters;
    private const int ProductManagerLedgerCharacters = 6_000;
    internal const int MaximumStudioDependencyContextCharacters = 3_200;
    internal const string RepositoryKnowledgeBegin =
        "REPOSITORY_KNOWLEDGE_V1_BEGIN";
    internal const string RepositoryKnowledgeEnd =
        "REPOSITORY_KNOWLEDGE_V1_END";
    internal const string StudioPlanContextBegin =
        "STUDIO_PLAN_CONTEXT_V1_BEGIN";
    internal const string StudioPlanContextEnd =
        "STUDIO_PLAN_CONTEXT_V1_END";
    private const string PromptCompactionMarker =
        "\n...[prompt context compacted]...\n";
    private const int MinimumDirectDependencyOutputCharacters = 64;
    private const int MaximumDirectDependencyOutputCharacters = 900;
    private const int MinimumAncestorOutputCharacters = 64;
    private const int MaximumAncestorOutputCharacters = 400;
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
        AgentExecutionContext? context = null;
        WorkflowDefinition? workflow = null;
        string? copilotSessionHome = null;
        GovernedGitIsolationScope? governedGitIsolation = null;
        PublicationGuardScope? publicationGuard = null;
        StagedAgentManifest? stagedAgentAccess = null;
        StagedPromotionSeed? stagedPromotion = null;
        StagedPrompt? stagedPrompt = null;
        var retainStagedContextForRecovery = false;
        var runWorkspaceHooks = false;
        // Resolved inside the outer try so that a failure here (or in any other
        // fallible setup that follows it) still reaches the finally block below;
        // a null value here means the initial lookup did not complete and the
        // finally block must best-effort re-resolve it before deciding whether
        // the after_run hook may run.
        WorkspaceHookPolicy? hookPolicy = null;
        try
        {
            context = RequireContext(request);
            workflow = workflowProvider.GetEffective();
            var recoveredInstructions =
                context.RecoverInterruptedSession
                    ? await LoadPersistedExecutionInstructionsAsync(
                        context,
                        databaseFactory,
                        cancellationToken)
                    : null;
            copilotSessionHome = ResolveCopilotSessionHome();
            runWorkspaceHooks = ShouldRunWorkspaceHooks(
                context.ContractVersion,
                context.InvocationKind);
            hookPolicy = await ResolveWorkspaceHookPolicyAsync(
                databaseFactory,
                context.FlowId,
                cancellationToken);
            var permission = await ResolveAndPersistPermissionAsync(
                context,
                workflow,
                permissionResolver,
                databaseFactory,
                cancellationToken);
            var manifest = await snapshotService.GetManifestAsync(
                context.FlowId,
                context.AgentId,
                cancellationToken);
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
            var agentAccess = await manifestStager.StageAsync(
                copilotSessionHome,
                manifest,
                request.CopilotSessionId,
                cancellationToken);
            stagedAgentAccess = agentAccess;
            stagedPromotion = context.PromotionContext is null
                ? null
                : await manifestStager.StagePromotionSeedAsync(
                    agentAccess,
                    context.PromotionContext,
                    cancellationToken);
            request.Progress?.Invoke(new AgentRunProgress(
                AgentRunPhase.BuildingPrompt,
                "Rendering WORKFLOW.md with role-focused task context."));
            var instructions = recoveredInstructions ??
                await RenderAndPersistExecutionInstructionsAsync(
                    context,
                    workflow,
                    manifest.Instructions,
                    request.WorkingDirectory,
                    stagedPromotion,
                    promptRenderer,
                    databaseFactory,
                    cancellationToken);
            var renderedPrompt = instructions.Prompt;
            var inlinePrompt = renderedPrompt;
            request.Progress?.Invoke(new AgentRunProgress(
                AgentRunPhase.BuildingPrompt,
                instructions.Recovered
                    ? "Loaded the exact persisted prompt for the interrupted Copilot CLI turn."
                    : "Rendered and persisted the exact prompt for the Copilot CLI turn.",
                renderedPrompt));

            governedGitIsolation = permission.GovernedGitMetadataIsolation
                ? GovernedGitIsolationScope.Create(
                    request.WorkingDirectory,
                    context.GovernedRepositoryRelativePaths ??
                    throw new InvalidOperationException(
                        "Governed execution has no trusted repository mapping."))
                : null;
            publicationGuard = permission.GuardPublicationCredentials
                ? PublicationGuardScope.Create(
                    copilotSessionHome,
                    request.WorkingDirectory)
                : null;
            var environmentVariables = MergeProcessEnvironment(
                publicationGuard?.EnvironmentVariables,
                governedGitIsolation?.EnvironmentVariables);

            async Task<string> StagePromptReferenceAsync()
            {
                if (!permission.AllowedTools.Contains(
                        "view",
                        StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        "A staged prompt requires view access in the effective permission profile.");
                }
                stagedPrompt ??= await manifestStager.StagePromptAsync(
                    agentAccess,
                    renderedPrompt,
                    request.CopilotSessionId,
                    context.FlowId,
                    context.FlowStepId,
                    context.Attempt,
                    cancellationToken);
                return AgentManifestStager.BuildPromptReferenceInstruction(
                    stagedPrompt,
                    context.RecoverInterruptedSession);
            }

            List<string> BuildArguments(string cliPrompt)
            {
                var built = BuildCliArguments(
                    request.WorkingDirectory,
                    agentAccess.Root,
                    agentAccess.AgentId,
                    request.Model,
                    request.Effort,
                    request.CopilotSessionId,
                    cliPrompt,
                    permission,
                    copilotCli.ReasoningEffortOption,
                    context.ResumeSession ||
                    context.RecoverInterruptedSession).ToList();
                if (governedGitIsolation is not null)
                {
                    built.Insert(
                        Math.Max(0, built.Count - 2),
                        $"--deny-tool=write({Path.GetFullPath(
                            governedGitIsolation.RootPath).Replace('\\', '/')})");
                }
                return built;
            }

            var cliPrompt =
                inlinePrompt.Length > MaximumInlinePromptCharacters
                    ? await StagePromptReferenceAsync()
                    : inlinePrompt;
            var arguments = BuildArguments(cliPrompt);
            if (EstimateCliCommandLineCharacters(
                    copilotCli.ResolvedPath,
                    arguments) >
                MaximumProcessCommandLineCharacters &&
                stagedPrompt is null)
            {
                cliPrompt = await StagePromptReferenceAsync();
                arguments = BuildArguments(cliPrompt);
            }
            var commandLineCharacters = EstimateCliCommandLineCharacters(
                copilotCli.ResolvedPath,
                arguments);
            if (commandLineCharacters >
                MaximumProcessCommandLineCharacters)
            {
                throw new InvalidOperationException(
                    $"The Copilot CLI command line requires {commandLineCharacters} characters, " +
                    $"exceeding the host limit of {MaximumProcessCommandLineCharacters}.");
            }
            var timeouts = ResolveExecutionTimeouts(
                workflow.Config.Copilot,
                context.ModelSelectionStrategy,
                context.ExpectedAcceptedTimeSeconds);

            ProcessResult result;
            try
            {
                if (runWorkspaceHooks)
                {
                    await hookRunner.RunAsync(
                        WorkspaceHookStage.BeforeRun,
                        request.WorkingDirectory,
                        workflow,
                        hookPolicy.FlowKind,
                        hookPolicy.Provisional,
                        cancellationToken);
                }
                request.Progress?.Invoke(new AgentRunProgress(
                    AgentRunPhase.LaunchingAgentProcess,
                    $"Launching Copilot CLI {copilotCli.Version} with {request.Model}/{request.Effort}; " +
                    $"{timeouts.StallTimeout.TotalMinutes:0.#}-minute quiet watchdog and " +
                    $"{timeouts.TurnTimeout.TotalMinutes:0.#}-minute hard limit."));
                // Once a process can observe the staged root, retain the entire session-owned
                // context until the result is conclusive. Interrupted and still-active sessions
                // need the same agent definition and prompt/seed bytes for a safe resume.
                retainStagedContextForRecovery = true;
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
                retainStagedContextForRecovery = false;
            }
            catch (ProcessStalledException exception)
            {
                var recovered = await RecoverInterruptedProcessAsync(
                    context,
                    request,
                    copilotSessionHome,
                    "Copilot CLI stalled.",
                    exception.Message,
                    AgentRunFailureKind.Stalled);
                retainStagedContextForRecovery =
                    !recovered.Success &&
                    (recovered.CanResumeSession ||
                     recovered.ProcessTerminationUnconfirmed);
                return recovered;
            }
            catch (TimeoutException exception)
            {
                var recovered = await RecoverInterruptedProcessAsync(
                    context,
                    request,
                    copilotSessionHome,
                    "Copilot CLI timed out.",
                    exception.Message,
                    AgentRunFailureKind.TimedOut);
                retainStagedContextForRecovery =
                    !recovered.Success &&
                    (recovered.CanResumeSession ||
                     recovered.ProcessTerminationUnconfirmed);
                return recovered;
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                retainStagedContextForRecovery = false;
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

            governedGitIsolation?.RestoreAndValidate();
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
        finally
        {
            try
            {
                if (context is not null && workflow is not null)
                {
                    await RunAfterRunWorkspaceHookAsync(
                        runWorkspaceHooks,
                        hookPolicy,
                        databaseFactory,
                        hookRunner,
                        context.FlowId,
                        request.WorkingDirectory,
                        workflow,
                        logger,
                        CancellationToken.None);
                }
            }
            finally
            {
                try
                {
                    governedGitIsolation?.RestoreAndValidate();
                }
                finally
                {
                    try
                    {
                        publicationGuard?.Dispose();
                    }
                    finally
                    {
                        if (stagedAgentAccess is not null &&
                            copilotSessionHome is not null &&
                            !retainStagedContextForRecovery)
                        {
                            manifestStager.CleanupSessionRoot(
                                copilotSessionHome,
                                request.CopilotSessionId,
                                stagedAgentAccess.Root);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Runs the after_run workspace hook using an already-resolved policy when
    /// available, or best-effort re-resolves it when the initial lookup (made
    /// earlier in the same run, inside the same outer try/finally) failed to
    /// complete. If both the initial lookup and this cleanup re-resolution fail,
    /// the flow's kind is genuinely unknown and the hook must never run -- an
    /// unrecognized flow could be Advisory, or the invocation could be a
    /// sensitive ReviewClassification/Publication step, either of which must
    /// never execute the after_run script. The lookup failure is logged, not
    /// thrown, so the remaining cleanup (Git isolation restore, publication
    /// guard disposal, staged-context cleanup) always still runs.
    /// </summary>
    internal static async Task RunAfterRunWorkspaceHookAsync(
        bool runWorkspaceHooks,
        WorkspaceHookPolicy? hookPolicy,
        IDbContextFactory<HarnessDbContext> databaseFactory,
        WorkspaceHookRunner hookRunner,
        Guid flowId,
        string workingDirectory,
        WorkflowDefinition workflow,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!runWorkspaceHooks)
        {
            return;
        }

        if (hookPolicy is null)
        {
            try
            {
                hookPolicy = await ResolveWorkspaceHookPolicyAsync(
                    databaseFactory,
                    flowId,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "Best-effort workspace hook policy re-resolution failed for flow {FlowId}; " +
                    "skipping the after_run workspace hook because its flow kind is unknown.",
                    flowId);
                return;
            }
        }

        await hookRunner.RunAsync(
            WorkspaceHookStage.AfterRun,
            workingDirectory,
            workflow,
            hookPolicy.FlowKind,
            hookPolicy.Provisional,
            cancellationToken);
    }

    internal static async Task<WorkspaceHookPolicy> ResolveWorkspaceHookPolicyAsync(
        IDbContextFactory<HarnessDbContext> databaseFactory,
        Guid flowId,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var flow = await database.Flows
            .AsNoTracking()
            .Where(item => item.Id == flowId)
            .Select(item => new
            {
                item.Kind,
                item.Status,
                item.ContractVersion
            })
            .SingleAsync(cancellationToken);
        var studioV2 = string.Equals(
            flow.ContractVersion,
            "studio-v2",
            StringComparison.Ordinal);
        return new WorkspaceHookPolicy(
            studioV2 ? flow.Kind : FlowKind.Delivery,
            studioV2 && flow.Status == FlowStatus.Intake);
    }

    internal sealed record WorkspaceHookPolicy(
        FlowKind FlowKind,
        bool Provisional);

    internal static bool ShouldRunWorkspaceHooks(
        string contractVersion,
        ExecutionInvocationKind invocationKind) =>
        !string.Equals(
            contractVersion,
            "studio-v2",
            StringComparison.Ordinal) ||
        invocationKind is not (
            ExecutionInvocationKind.ReviewClassification or
            ExecutionInvocationKind.Publication);

    internal static IReadOnlyList<string> BuildCliArguments(
        string workingDirectory,
        string harnessRoot,
        string agentId,
        string model,
        string effort,
        Guid copilotSessionId,
        string prompt,
        EffectiveExecutionPermission permission,
        string reasoningEffortOption = "--reasoning-effort",
        bool resumeSession = false)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (prompt.Length > MaximumInlinePromptCharacters)
        {
            throw new InvalidOperationException(
                $"The Copilot CLI prompt argument exceeds the {MaximumInlinePromptCharacters}-character inline limit. Stage the complete prompt before building arguments.");
        }
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
            if (reasoningEffortOption is not (
                    "--reasoning-effort" or "--effort"))
            {
                throw new InvalidOperationException(
                    "The Copilot CLI reasoning-effort option is invalid.");
            }
            arguments.AddRange([reasoningEffortOption, effort]);
        }
        arguments.AddRange(
            resumeSession
                ? [$"--resume={copilotSessionId:D}"]
                : ["--session-id", copilotSessionId.ToString("D")]);

        ApplyToolPolicy(
            arguments,
            $"--available-tools={string.Join(',', permission.AllowedTools)}");
        if (permission.DisableBuiltinMcps)
        {
            ApplyToolPolicy(arguments, "--disable-builtin-mcps");
        }
        if (permission.DisableCustomInstructions)
        {
            ApplyToolPolicy(
                arguments,
                "--no-custom-instructions",
                "--no-eager-powershell-resolution");
        }
        if (permission.DisallowTemporaryDirectory)
        {
            ApplyToolPolicy(arguments, "--disallow-temp-dir");
        }
        if (permission.Profile == ExecutionPermissionProfile.WorkspaceWrite)
        {
            if (!permission.DeniedTools.Contains("write"))
            {
                ApplyToolPolicy(arguments, "--allow-tool=write");
            }
            if (!permission.DeniedTools.Contains("shell"))
            {
                ApplyToolPolicy(arguments, "--allow-tool=shell");
            }
        }
        else if (permission.Profile == ExecutionPermissionProfile.Publish)
        {
            ApplyToolPolicy(arguments, "--allow-tool=shell");
        }
        if (permission.DeniedTools.Contains("write") &&
            permission.DeniedTools.Contains("shell"))
        {
            ApplyToolPolicy(arguments, "--deny-tool=write,shell");
        }
        else
        {
            if (permission.DeniedTools.Contains("write"))
            {
                ApplyToolPolicy(arguments, "--deny-tool=write");
            }
            if (permission.DeniedTools.Contains("shell"))
            {
                ApplyToolPolicy(arguments, "--deny-tool=shell");
            }
        }
        foreach (var deniedTool in permission.DeniedTools
                     .Where(value => value is not "write" and not "shell"))
        {
            ApplyToolPolicy(arguments, $"--deny-tool={deniedTool}");
        }
        foreach (var deniedUrl in permission.DeniedUrls)
        {
            ApplyToolPolicy(arguments, $"--deny-url={deniedUrl}");
        }
        if (permission.GuardPublicationCredentials)
        {
            ApplyToolPolicy(
                arguments,
                "--secret-env-vars=COPILOT_GITHUB_TOKEN,GH_TOKEN,GITHUB_TOKEN,GH_ENTERPRISE_TOKEN,GITHUB_ENTERPRISE_TOKEN,GITHUB_TOKEN_REQUEST_URL,GITHUB_TOKEN_REQUEST_TOKEN,SSH_AUTH_SOCK,GIT_ASKPASS,SSH_ASKPASS",
                "--no-remote",
                "--no-remote-export");
        }
        if (permission.GovernedGitMetadataIsolation)
        {
            ApplyToolPolicyRange(arguments, GovernedLocalMutationDenials);
            ApplyToolPolicyRange(arguments, GovernedGitIsolationBypassDenials);
            ApplyToolPolicyRange(
                arguments,
                GovernedGitMarkerWriteDenials(workingDirectory));
            ApplyToolPolicy(
                arguments,
                $"--deny-tool=write({Path.GetFullPath(Path.Combine(
                    workingDirectory,
                    ".ai-harness",
                    "outcome-verification")).Replace('\\', '/')})");
        }

        ApplyToolPolicy(
            arguments,
            $"--deny-tool=write({Path.GetFullPath(harnessRoot).Replace('\\', '/')})");
        arguments.AddRange(["-p", prompt]);
        return arguments;
    }

    internal static IReadOnlyList<string> BuildCliArguments(
        string workingDirectory,
        string harnessRoot,
        string agentId,
        ExecutionInvocationKind invocationKind,
        string model,
        string effort,
        Guid copilotSessionId,
        string prompt,
        bool resumeSession = false,
        bool isHostControlledPublication = false,
        bool isGovernedOutcomeVerification = false,
        bool blockRemotePublication = false)
    {
        if (!Enum.IsDefined(invocationKind))
        {
            throw new InvalidOperationException(
                "The test invocation kind is invalid.");
        }
        var preMortem =
            invocationKind == ExecutionInvocationKind.PreMortem;
        var readOnly =
            invocationKind is not ExecutionInvocationKind.Worker ||
            isHostControlledPublication;
        var profile = readOnly
            ? preMortem
                ? ExecutionPermissionProfile.PreMortemReadOnly
                : ExecutionPermissionProfile.ReadOnlySource
            : ExecutionPermissionProfile.WorkspaceWrite;
        var permission = new EffectiveExecutionPermission(
            profile,
            readOnly
                ? isHostControlledPublication
                    ? HostControlledPublicationTools.Split(',').ToImmutableArray()
                    : preMortem
                        ? PreMortemTools.Split(',').ToImmutableArray()
                        : AccountManagerTools.Split(',').ToImmutableArray()
                : GovernedNonPublicationTools().Split(',').ToImmutableArray(),
            (readOnly
                    ? new[] { "write", "shell" }
                    : Array.Empty<string>())
                .Concat(blockRemotePublication ? RemoteMutationDenials
                    .Where(item => item.StartsWith("--deny-tool=", StringComparison.Ordinal))
                    .Select(item => item["--deny-tool=".Length..]) : [])
                .ToImmutableArray(),
            blockRemotePublication
                ? ["github.com", "api.github.com"]
                : [],
            true,
            true,
            readOnly || isGovernedOutcomeVerification,
            blockRemotePublication || isGovernedOutcomeVerification,
            false,
            isGovernedOutcomeVerification);
        return BuildCliArguments(
            workingDirectory,
            harnessRoot,
            agentId,
            model,
            effort,
            copilotSessionId,
            prompt,
            permission,
            reasoningEffortOption: "--reasoning-effort",
            resumeSession);
    }

    internal static int EstimateCliCommandLineCharacters(
        string executable,
        IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        var total = QuotedArgumentCharacters(executable);
        foreach (var argument in arguments)
        {
            total = checked(
                total + 1 + QuotedArgumentCharacters(argument));
        }
        return total;
    }

    private static int QuotedArgumentCharacters(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Length > 0 &&
            !argument.Any(character =>
                char.IsWhiteSpace(character) || character == '"'))
        {
            return argument.Length;
        }

        // ProcessStartInfo.ArgumentList uses the standard Windows argv quoting
        // rules. This is also a conservative bound on platforms using execve.
        var length = 1;
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                length = checked(length + (backslashes * 2) + 2);
                backslashes = 0;
                continue;
            }

            length = checked(length + backslashes + 1);
            backslashes = 0;
        }
        return checked(length + (backslashes * 2) + 1);
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

    internal static async Task<EffectiveExecutionPermission>
        ResolveAndPersistPermissionAsync(
            AgentExecutionContext context,
            WorkflowDefinition workflow,
            PermissionProfileResolver permissionResolver,
            IDbContextFactory<HarnessDbContext> databaseFactory,
            CancellationToken cancellationToken)
    {
        if (context.FlowStepId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "A durable FlowStep ID is required before agent execution.");
        }

        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var step = await database.FlowSteps.SingleOrDefaultAsync(
            item =>
                item.Id == context.FlowStepId &&
                item.FlowRunId == context.FlowId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "The durable execution step could not be found.");
        var flow = await database.Flows
            .AsNoTracking()
            .SingleAsync(item => item.Id == context.FlowId, cancellationToken);
        if (step.InvocationKind != context.InvocationKind)
        {
            throw new InvalidOperationException(
                "The execution context does not match the durable lifecycle invocation.");
        }
        ImmutableArray<PlanDuty> duties;
        try
        {
            duties = (JsonSerializer.Deserialize<PlanDuty[]>(
                    step.PlanDutiesJson,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = false,
                        Converters =
                        {
                            new System.Text.Json.Serialization.JsonStringEnumConverter(
                                allowIntegerValues: false)
                        }
                    }) ?? [])
                .ToImmutableArray();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The durable plan duties are invalid; permission resolution failed closed.",
                exception);
        }

        ReviewDecision? reviewDecision;
        bool accepted;
        if (flow.ContractVersion == "studio-v2")
        {
            reviewDecision = await (
                    from gate in database.GateRecords.AsNoTracking()
                    join reviewedStep in database.FlowSteps.AsNoTracking()
                        on gate.FlowStepId equals reviewedStep.Id
                    where gate.FlowRunId == flow.Id &&
                          gate.ActionType == HandoffActionType.CustomerReview &&
                          gate.Resolved &&
                          gate.Approved == true &&
                          gate.ReviewDecision == ReviewDecision.Accepted &&
                          reviewedStep.FlowRunId == flow.Id &&
                          reviewedStep.Iteration == step.Iteration &&
                          reviewedStep.IsOutcomeOwner &&
                          reviewedStep.PlanStepKey == flow.OutcomeOwnerPlanStepKey
                    orderby gate.ResolvedAt descending
                    select gate.ReviewDecision)
                .FirstOrDefaultAsync(cancellationToken);
            accepted = reviewDecision == ReviewDecision.Accepted;
        }
        else
        {
            accepted = await database.GateRecords
                .AsNoTracking()
                .AnyAsync(
                    gate =>
                        gate.FlowRunId == flow.Id &&
                        gate.Resolved &&
                        gate.Approved == true &&
                        gate.ActionType == HandoffActionType.Release,
                    cancellationToken);
            reviewDecision = null;
        }
        var request = new PermissionResolutionRequest(
            flow.Kind,
            step.InvocationKind,
            step.PlanStage,
            duties,
            reviewDecision,
            accepted,
            !string.IsNullOrWhiteSpace(flow.PublicationPlanStepKey) &&
            string.Equals(
                flow.PublicationPlanStepKey,
                step.PlanStepKey,
                StringComparison.Ordinal),
            flow.ContractVersion,
            flow.ContractVersion == "legacy-v1" &&
            step.RemotePublicationAllowed &&
            accepted,
            context.IsGovernedOutcomeVerification);

        if (flow.ContractVersion == "studio-v2" &&
            !string.IsNullOrWhiteSpace(step.EffectivePermissionJson))
        {
            EffectiveExecutionPermission persisted;
            try
            {
                persisted =
                    JsonSerializer.Deserialize<EffectiveExecutionPermission>(
                        step.EffectivePermissionJson)
                    ?? throw new InvalidOperationException(
                        "The persisted effective permission document is empty.");
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    "The persisted effective permission document is invalid; execution failed closed.",
                    exception);
            }
            PermissionProfileResolver.ValidatePersisted(
                persisted,
                step.PermissionProfile);
            if (string.IsNullOrWhiteSpace(step.WorkflowRevision))
            {
                throw new InvalidOperationException(
                    "The persisted studio-v2 permission has no workflow revision.");
            }
            if (context.RecoverInterruptedSession)
            {
                ValidateWorkflowRevision(step.WorkflowRevision);
                PermissionProfileResolver.ValidatePersisted(
                    persisted,
                    persisted.Profile,
                    request);
                return persisted;
            }

            var current = permissionResolver.Resolve(
                request,
                PermissionProfileResolver.FromWorkflow(workflow));
            var permission = step.PlanStage == PlanStage.AfterApproval &&
                             duties.SequenceEqual([PlanDuty.Publish])
                ? PermissionProfileResolver.Tighten(
                    persisted,
                    current)
                : persisted;
            PermissionProfileResolver.ValidatePersisted(
                permission,
                permission.Profile,
                request);
            var effectiveRemotePublicationAllowed =
                step.RemotePublicationAllowed &&
                PermissionProfileResolver.GrantsRemotePublication(
                    permission);
            if (!PermissionProfileResolver.Equivalent(
                    persisted,
                    permission) ||
                step.RemotePublicationAllowed !=
                effectiveRemotePublicationAllowed)
            {
                step.PermissionProfile = permission.Profile;
                step.EffectivePermissionJson =
                    JsonSerializer.Serialize(permission);
                step.WorkflowRevision = workflow.Revision;
                step.RemotePublicationAllowed =
                    effectiveRemotePublicationAllowed;
                await database.SaveChangesAsync(cancellationToken);
            }
            return permission;
        }

        var resolvedPermission = permissionResolver.Resolve(
            request,
            PermissionProfileResolver.FromWorkflow(workflow));
        var unboundPendingAttempt =
            flow.ContractVersion == "studio-v2" &&
            step.Status == StepStatus.Pending &&
            step.StartedAt is null &&
            string.IsNullOrWhiteSpace(step.EffectivePermissionJson);
        if (flow.ContractVersion == "studio-v2" &&
            !string.IsNullOrWhiteSpace(step.WorkflowRevision) &&
            !string.Equals(
                step.WorkflowRevision,
                workflow.Revision,
                StringComparison.Ordinal) &&
            !unboundPendingAttempt)
        {
            throw new InvalidOperationException(
                "A started studio-v2 attempt cannot be rebound to a different workflow revision; execution failed closed.");
        }

        step.PermissionProfile = resolvedPermission.Profile;
        step.EffectivePermissionJson =
            JsonSerializer.Serialize(resolvedPermission);
        if (flow.ContractVersion == "studio-v2")
        {
            step.RemotePublicationAllowed =
                step.RemotePublicationAllowed &&
                PermissionProfileResolver.GrantsRemotePublication(
                    resolvedPermission);
        }
        if (unboundPendingAttempt ||
            string.IsNullOrWhiteSpace(step.WorkflowRevision) ||
            flow.ContractVersion == "legacy-v1")
        {
            step.WorkflowRevision = workflow.Revision;
        }
        await database.SaveChangesAsync(cancellationToken);
        return resolvedPermission;
    }

    internal static async Task<DurableExecutionInstructions>
        LoadPersistedExecutionInstructionsAsync(
            AgentExecutionContext context,
            IDbContextFactory<HarnessDbContext> databaseFactory,
            CancellationToken cancellationToken)
    {
        if (!context.RecoverInterruptedSession)
        {
            throw new InvalidOperationException(
                "Persisted execution instructions are reserved for recovery of the same durable attempt.");
        }
        if (context.FlowStepId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "A durable FlowStep ID is required to recover exact execution instructions.");
        }

        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var step = await database.FlowSteps
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item =>
                    item.Id == context.FlowStepId &&
                    item.FlowRunId == context.FlowId,
                cancellationToken)
            ?? throw new InvalidOperationException(
                "The durable execution step could not be found while recovering its instructions.");
        ValidateAttemptIdentity(step, context);
        if (step.Status != StepStatus.Running)
        {
            throw new InvalidOperationException(
                "Exact execution instructions can be recovered only for a running durable attempt.");
        }
        ValidatePersistedExecutionInstructions(
            step.ExecutionPrompt,
            step.WorkflowRevision);
        if (step.ExecutionPrompt.Length >
            ResolveMaximumRenderedPromptCharacters(context))
        {
            throw new InvalidOperationException(
                "The persisted execution prompt exceeds the durable attempt's prompt limit; recovery failed closed.");
        }

        return new DurableExecutionInstructions(
            step.ExecutionPrompt,
            step.WorkflowRevision,
            Recovered: true);
    }

    internal static async Task<DurableExecutionInstructions>
        RenderAndPersistExecutionInstructionsAsync(
            AgentExecutionContext context,
            WorkflowDefinition workflow,
            string agentInstructions,
            string workingDirectory,
            StagedPromotionSeed? stagedPromotion,
            WorkflowPromptRenderer promptRenderer,
            IDbContextFactory<HarnessDbContext> databaseFactory,
            CancellationToken cancellationToken)
    {
        if (context.RecoverInterruptedSession)
        {
            return await LoadPersistedExecutionInstructionsAsync(
                context,
                databaseFactory,
                cancellationToken);
        }

        ValidateWorkflowRevision(workflow.Revision);
        var renderedPrompt = BoundRenderedPrompt(
            context,
            string.IsNullOrWhiteSpace(context.DirectPrompt)
                ? promptRenderer.Render(
                    workflow.PromptTemplate,
                    BuildPromptValues(
                        context,
                        agentInstructions,
                        workingDirectory,
                        stagedPromotion))
                : BuildDirectPrompt(
                    context,
                    agentInstructions,
                    workingDirectory));

        if (context.FlowStepId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "A durable FlowStep ID is required before the execution prompt can be persisted.");
        }
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var step = await database.FlowSteps.SingleOrDefaultAsync(
            item =>
                item.Id == context.FlowStepId &&
                item.FlowRunId == context.FlowId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "The durable execution step could not be found while persisting its instructions.");
        ValidateAttemptIdentity(step, context);
        if (step.Status != StepStatus.Running)
        {
            throw new InvalidOperationException(
                "The execution prompt can be bound only to a running durable attempt.");
        }

        step.ExecutionPrompt = renderedPrompt;
        step.WorkflowRevision = workflow.Revision;
        await database.SaveChangesAsync(cancellationToken);
        return new DurableExecutionInstructions(
            renderedPrompt,
            workflow.Revision,
            Recovered: false);
    }

    internal static void ValidatePersistedExecutionInstructions(
        string executionPrompt,
        string workflowRevision)
    {
        if (string.IsNullOrWhiteSpace(executionPrompt))
        {
            throw new InvalidOperationException(
                "The interrupted durable attempt has no persisted exact execution prompt; recovery failed closed.");
        }
        ValidateWorkflowRevision(workflowRevision);
    }

    internal static void ValidateWorkflowRevision(string workflowRevision)
    {
        if (workflowRevision.Length != 64 ||
            workflowRevision.Any(character =>
                !((character >= '0' && character <= '9') ||
                  (character >= 'a' && character <= 'f') ||
                  (character >= 'A' && character <= 'F'))))
        {
            throw new InvalidOperationException(
                "The interrupted durable attempt has an invalid persisted workflow revision; recovery failed closed.");
        }
    }

    private static void ValidateAttemptIdentity(
        FlowStep step,
        AgentExecutionContext context)
    {
        if (step.Iteration != context.Iteration ||
            step.Attempt != context.Attempt ||
            step.InvocationKind != context.InvocationKind ||
            step.CopilotSessionId != context.CopilotSessionId)
        {
            throw new InvalidOperationException(
                "The execution context does not match the durable flow-step attempt; execution failed closed.");
        }
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

    internal static string RestartContinuationPrompt(
        string renderedPrompt,
        int maximumPromptCharacters = MaximumPromptCharacters)
    {
        const string recoveryInstruction =
            "Resume from the current workspace; inspect existing changes before continuing.";
        return
            recoveryInstruction +
            Environment.NewLine +
            Environment.NewLine +
            ClipPrompt(
                renderedPrompt,
                maximumPromptCharacters -
                recoveryInstruction.Length -
                (Environment.NewLine.Length * 2));
    }

    internal static string BoundRenderedPrompt(
        AgentExecutionContext context,
        string renderedPrompt)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(renderedPrompt);
        var maximum = ResolveMaximumRenderedPromptCharacters(context);
        var repositoryKnowledgeBlock = BuildRepositoryKnowledgeBlock(
            context.RepositoryKnowledge,
            context.SourceProjectPath);
        if (IsStructuredLargePrompt(context))
        {
            if (renderedPrompt.Length > maximum)
            {
                throw new InvalidOperationException(
                    $"The rendered {context.InvocationKind} prompt contains {renderedPrompt.Length} characters, exceeding its contract-derived hard limit of {maximum}. The host will not truncate confirmed structured context.");
            }
            return renderedPrompt;
        }
        var bounded = ClipPrompt(
            renderedPrompt,
            maximum,
            repositoryKnowledgeBlock);
        if (!string.IsNullOrWhiteSpace(repositoryKnowledgeBlock) &&
            renderedPrompt.Contains(
                repositoryKnowledgeBlock,
                StringComparison.Ordinal) &&
            !bounded.Contains(
                repositoryKnowledgeBlock,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The rendered prompt cannot fit without truncating the complete reviewed repository knowledge.");
        }
        return bounded;
    }

    internal static int ResolveMaximumRenderedPromptCharacters(
        AgentExecutionContext context)
    {
        var baseMaximum = context.ContractVersion == "studio-v2"
            ? context.InvocationKind switch
            {
                ExecutionInvocationKind.Planning =>
                    MaximumPlanningPromptCharacters,
                ExecutionInvocationKind.ReviewClassification =>
                    MaximumReviewClassificationPromptCharacters,
                _ => MaximumPromptCharacters
            }
            : context.InvocationKind ==
              ExecutionInvocationKind.ReviewClassification
                ? 131_072
                : MaximumPromptCharacters;
        var repositoryKnowledgeBlock = BuildRepositoryKnowledgeBlock(
            context.RepositoryKnowledge,
            context.SourceProjectPath);
        return checked(baseMaximum + repositoryKnowledgeBlock.Length);
    }

    private static bool IsStructuredLargePrompt(
        AgentExecutionContext context) =>
        context.ContractVersion == "studio-v2" &&
        context.InvocationKind is
            ExecutionInvocationKind.Planning or
            ExecutionInvocationKind.ReviewClassification;

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
                context.IsOutcomeQa,
                context.ContractVersion,
                context.InvocationKind,
                context.IsOutcomeOwner,
                context.PlanStepKey,
                context.FlowKind) &&
            IsRecoveryCurrent(
                context.InvocationStartedAt,
                snapshot.CompletedAt))
        {
            request.Progress?.Invoke(new AgentRunProgress(
                AgentRunPhase.Finishing,
                "Recovered the completed handoff from the Copilot session journal after the CLI stopped responding."));
            return recovered;
        }
        if (snapshot.State == CopilotSessionJournalState.Completed)
        {
            return Failure(
                "The completed Copilot session output was rejected.",
                $"The journal output is stale or does not satisfy the persisted {context.InvocationKind} contract. It requires manual restart.",
                AgentRunFailureKind.InvalidOutput);
        }

        return Failure(
            summary,
            error,
            failureKind,
            canResumeSession:
                snapshot.State ==
                CopilotSessionJournalState.Interrupted,
            processTerminationUnconfirmed:
                snapshot.State ==
                CopilotSessionJournalState.Active);
    }

    internal static bool IsRecoverableCompletedOutput(
        string agentRole,
        string output,
        bool isPreMortemRevision = false,
        bool isOutcomeQa = false,
        string contractVersion = "legacy-v1",
        ExecutionInvocationKind invocationKind =
            ExecutionInvocationKind.Worker,
        bool isOutcomeOwner = false,
        string planStepKey = "",
        FlowKind? expectedFlowKind = null)
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

        if (contractVersion == "studio-v2" &&
            invocationKind ==
            ExecutionInvocationKind.ReviewClassification)
        {
            try
            {
                _ = ReviewFeedbackParser.Parse(output);
                return true;
            }
            catch (ReviewFeedbackContractException)
            {
                return false;
            }
        }
        if (contractVersion == "studio-v2" &&
            invocationKind == ExecutionInvocationKind.Intake)
        {
            return HasValidIntakeContract(
                output,
                contractVersion,
                planStepKey,
                expectedFlowKind);
        }
        if (contractVersion == "studio-v2" &&
            invocationKind ==
            ExecutionInvocationKind.BlockerExplanation)
        {
            return HasValidBlockerExplanationContract(output);
        }
        if (contractVersion == "studio-v2" &&
            invocationKind == ExecutionInvocationKind.PreMortem)
        {
            return HasValidPreMortemContract(output);
        }
        if (contractVersion == "studio-v2" &&
            invocationKind is
                ExecutionInvocationKind.Worker or
                ExecutionInvocationKind.Publication or
                ExecutionInvocationKind.Planning)
        {
            try
            {
                var status = AgentHandoffInspector.ParseDynamic(output);
                if (invocationKind ==
                    ExecutionInvocationKind.Planning)
                {
                    if (status.IsPushback)
                    {
                        return false;
                    }
                    _ = TeamPlanParser.Parse(output);
                }
                if (invocationKind == ExecutionInvocationKind.Worker &&
                    isOutcomeOwner &&
                    !status.IsPushback)
                {
                    _ = FlowOutcomeParser.Parse(output);
                }
                if (invocationKind == ExecutionInvocationKind.Publication &&
                    !status.IsPushback)
                {
                    _ = RepositoryKnowledgeSynthesizer.ParseRecapEnvelope(output);
                }
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        return agentRole switch
        {
            "account-manager" => HasValidIntakeContract(output, contractVersion),
            "product-manager" => FeedbackCoordinator.HasReworkTargetMarker(output),
            "pre-mortem-sceptic" => HasValidPreMortemContract(output),
            _ => AgentHandoffInspector.HasTerminalStatus(output)
        };
    }

    private static bool HasValidBlockerExplanationContract(string output)
    {
        try
        {
            var parsed = IntakeV2Parser.Parse(output);
            var brief = parsed.Document.Brief;
            return parsed.Document.Status ==
                   IntakeV2Status.NeedsClarification &&
                   parsed.Document.FlowKind is null &&
                   brief is not null &&
                   string.IsNullOrEmpty(brief.Goal) &&
                   brief.Details is { Count: 0 } &&
                   brief.SuccessCriteria is { Count: 0 } &&
                   brief.Constraints is { Count: 0 } &&
                   brief.Assumptions is { Count: 0 };
        }
        catch (IntakeV2ContractException)
        {
            return false;
        }
    }

    internal static bool IsRecoveryCurrent(
        DateTimeOffset? invocationStartedAt,
        DateTimeOffset? recoveredCompletedAt) =>
        invocationStartedAt is null ||
        recoveredCompletedAt is { } completedAt &&
        completedAt >= invocationStartedAt.Value;

    private static bool HasValidIntakeContract(
        string output,
        string contractVersion,
        string planStepKey = "",
        FlowKind? expectedFlowKind = null)
    {
        try
        {
            var response =
                IntakeCoordinator.ParseResponse(output, contractVersion);
            if (contractVersion == "studio-v2" &&
                string.Equals(
                    planStepKey,
                    WorkflowEngine.RefinementIntakePlanStepKey,
                    StringComparison.Ordinal) &&
                (response.Status !=
                     AccountManagerIntakeStatus.Confirmed ||
                 response.FlowKind != expectedFlowKind))
            {
                return false;
            }
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
        string workingDirectory,
        StagedPromotionSeed? stagedPromotion = null)
    {
        var isAccountManager = context.InvocationKind is
            ExecutionInvocationKind.Intake or
            ExecutionInvocationKind.ReviewClassification or
            ExecutionInvocationKind.BlockerExplanation;
        var isProductManager =
            !string.Equals(
                context.ContractVersion,
                "studio-v2",
                StringComparison.Ordinal) &&
            IsProductManager(context.AgentRole);
        var isPreMortem =
            context.InvocationKind == ExecutionInvocationKind.PreMortem;
        var usesCompactPreMortemContext =
            isPreMortem || context.IsPreMortemRevision;
        var workspace = PrepareWorkspace(workingDirectory);
        var repositoryKnowledge = PrepareRepositoryKnowledgeContent(
            context.RepositoryKnowledge,
            context.SourceProjectPath);
        var studioDependencyContext =
            string.Equals(
                context.ContractVersion,
                "studio-v2",
                StringComparison.Ordinal) &&
            context.InvocationKind == ExecutionInvocationKind.Worker &&
            context.StudioDependencyOutputs is { Count: > 0 }
                ? FormatStudioDependencyContext(
                    context.StudioDependencyOutputs,
                    context.SourceProjectPath)
                : string.Empty;
        var handoffs = !string.IsNullOrWhiteSpace(studioDependencyContext)
            ? studioDependencyContext
            : isAccountManager || usesCompactPreMortemContext
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
        var repositoryKnowledgeBlock = BuildRepositoryKnowledgeBlock(
            repositoryKnowledge);
        if (!string.IsNullOrWhiteSpace(repositoryKnowledgeBlock))
        {
            roleContext.Add(repositoryKnowledgeBlock);
        }
        if (!string.IsNullOrWhiteSpace(handoffs))
        {
            roleContext.Add(string.IsNullOrWhiteSpace(studioDependencyContext)
                ? $"## {(isProductManager ? "Execution ledger" : "Relevant upstream handoffs")}" +
                  $"{Environment.NewLine}{Environment.NewLine}{handoffs}"
                : handoffs);
        }
        if (string.Equals(
                context.ContractVersion,
                "studio-v2",
                StringComparison.Ordinal) &&
            context.InvocationKind is
                ExecutionInvocationKind.Worker or
                ExecutionInvocationKind.Publication)
        {
            var pushbackOwners = context.StudioDependencyOutputs?
                .Select(item => item.PlanStepKey)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray() ?? [];
            roleContext.Add(
                "## Pushback boundary" +
                Environment.NewLine +
                Environment.NewLine +
                (pushbackOwners.Length == 0
                    ? "This step has no valid earlier dependency or ancestor. Do not emit HANDOFF_STATUS: PUSHBACK; complete the assigned work from the confirmed brief."
                    : "PUSHBACK_OWNER_STEP_ID may name only one of these exact current-iteration plan-step IDs: " +
                      string.Join(", ", pushbackOwners) +
                      ". Never name a prior-iteration or inferred step."));
        }
        if (string.Equals(
                context.ContractVersion,
                "studio-v2",
                StringComparison.Ordinal) &&
            context.InvocationKind == ExecutionInvocationKind.Worker &&
            string.Equals(
                context.AgentRole,
                "quality-engineer",
                StringComparison.Ordinal))
        {
            roleContext.Add(
                "## Quality verdict handoff" +
                Environment.NewLine +
                Environment.NewLine +
                "HANDOFF_STATUS: COMPLETE is allowed only when every required check is release-ready. " +
                "If any required check fails and the pushback boundary lists an owner, return " +
                "HANDOFF_STATUS: PUSHBACK with one exact allowed PUSHBACK_OWNER_STEP_ID and a bounded " +
                "PUSHBACK_REASON. Never pair COMPLETE with FAIL, NOT release-ready, or an informal " +
                "Next owner instruction.");
        }
        if (stagedPromotion is not null)
        {
            roleContext.Add(
                "## Durable Advisory promotion context" +
                Environment.NewLine +
                Environment.NewLine +
                "The complete normalized accepted Advisory seed is stored once in the " +
                "host-controlled read-only context below. Read the file before responding; " +
                "do not infer or omit any detail." +
                Environment.NewLine +
                $"PROMOTION_SEED_FILE: {stagedPromotion.Path}" +
                Environment.NewLine +
                $"PROMOTION_SEED_SHA256: {stagedPromotion.SeedHash}");
        }
        if (!string.IsNullOrWhiteSpace(learnings))
        {
            roleContext.Add($"## Learned constraints{Environment.NewLine}{Environment.NewLine}{learnings}");
        }
        if (!string.IsNullOrWhiteSpace(feedback))
        {
            roleContext.Add($"## Customer feedback{Environment.NewLine}{Environment.NewLine}{feedback}");
        }
        var responseContract = context.IsPreMortemRevision
            ? PreMortemRevisionResponseContract()
            : ResponseContract(
                context.InvocationKind,
                context.AgentRole,
                context.ContractVersion);
        if (string.Equals(
                context.ContractVersion,
                "studio-v2",
                StringComparison.Ordinal) &&
            context.InvocationKind == ExecutionInvocationKind.Publication)
        {
            responseContract +=
                $"{Environment.NewLine}{Environment.NewLine}" +
                RepositoryKnowledgeSynthesizer.PublicationRecapInstructions(
                    workingDirectory,
                    context.RepositoryKnowledge,
                    context.SourceProjectPath);
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["agent.name"] = context.AgentName,
            ["agent.instructions"] = Clip(agentInstructions, 3_000),
            ["task"] = BoundTaskContext(
                context,
                usesCompactPreMortemContext),
            ["workspace"] = workspace,
            ["role.context"] = string.Join(
                $"{Environment.NewLine}{Environment.NewLine}",
                roleContext),
            ["response.contract"] = responseContract,
            ["outcome.context"] = context.OutcomeContext,
            ["outcome.contract"] = context.OutcomeContract,
            // Retain legacy variables so a hot-reloaded older WORKFLOW.md remains valid.
            ["repository.knowledge"] = string.IsNullOrWhiteSpace(repositoryKnowledgeBlock)
                ? workspace
                : $"{workspace}{Environment.NewLine}{Environment.NewLine}{repositoryKnowledgeBlock}",
            ["plan"] = Clip(context.PlanSummary, 1_000),
            ["handoffs"] = handoffs,
            ["learnings"] = learnings,
            ["feedback"] = feedback
        };
    }

    private static string BoundTaskContext(
        AgentExecutionContext context,
        bool usesCompactPreMortemContext)
    {
        if (context.ContractVersion == "studio-v2")
        {
            var maximum = context.InvocationKind switch
            {
                ExecutionInvocationKind.Planning =>
                    MaximumPlanningTaskCharacters,
                ExecutionInvocationKind.ReviewClassification =>
                    MaximumReviewClassificationTaskCharacters,
                _ => 0
            };
            if (maximum > 0)
            {
                if (context.Task.Length > maximum)
                {
                    throw new InvalidOperationException(
                        $"The {context.InvocationKind} task contains {context.Task.Length} characters, exceeding its contract-derived hard limit of {maximum}. The host will not truncate confirmed structured context.");
                }
                return context.Task;
            }
        }

        return Clip(
            context.Task,
            usesCompactPreMortemContext
                ? 10_000
                : context.InvocationKind is
                    ExecutionInvocationKind.Planning or
                    ExecutionInvocationKind.ReviewClassification
                    ? 131_072
                    : 6_000);
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

    internal static string FormatStudioDependencyContext(
        IReadOnlyList<StudioDependencyOutput> outputs,
        string sourceProjectPath,
        int maximumCharacters = MaximumStudioDependencyContextCharacters)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        if (maximumCharacters <= 0 ||
            maximumCharacters > MaximumStudioDependencyContextCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCharacters),
                $"Studio dependency context must be from 1 through {MaximumStudioDependencyContextCharacters} characters.");
        }

        var normalized = outputs
            .Select(item => item with
            {
                Output = RemoveSourceProjectPath(
                        item.Output,
                        sourceProjectPath)
                    .ReplaceLineEndings("\n")
                    .Trim()
            })
            .ToList();
        var direct = normalized
            .Where(item => item.Kind == StudioDependencyKind.Direct)
            .ToList();
        if (direct.Count == 0)
        {
            return string.Empty;
        }
        if (direct.Any(item =>
                string.IsNullOrWhiteSpace(item.PlanStepKey) ||
                string.IsNullOrWhiteSpace(item.Output)) ||
            direct.Select(item => item.PlanStepKey)
                .Distinct(StringComparer.Ordinal)
                .Count() != direct.Count)
        {
            throw new InvalidOperationException(
                "Studio direct dependency context must contain one nonempty effective output per plan-step key.");
        }

        var budgets = direct
            .Select(item => Math.Min(
                item.Output.Length,
                MinimumDirectDependencyOutputCharacters))
            .ToArray();
        if (StudioDependencySectionLength(direct, budgets) >
            maximumCharacters)
        {
            throw new InvalidOperationException(
                "Studio direct dependency identifiers exceed the bounded context envelope.");
        }

        var targets = direct
            .Select(item => Math.Min(
                item.Output.Length,
                MaximumDirectDependencyOutputCharacters))
            .ToArray();
        while (true)
        {
            var advanced = false;
            for (var index = 0; index < budgets.Length; index++)
            {
                if (budgets[index] >= targets[index])
                {
                    continue;
                }
                budgets[index]++;
                if (StudioDependencySectionLength(direct, budgets) >
                    maximumCharacters)
                {
                    budgets[index]--;
                    continue;
                }
                advanced = true;
            }
            if (!advanced)
            {
                break;
            }
        }

        var entries = direct
            .Select((item, index) =>
                RenderStudioDependencyEntry(item, budgets[index]))
            .ToList();
        var allDirectComplete = direct
            .Select((item, index) => budgets[index] >= item.Output.Length)
            .All(value => value);
        if (allDirectComplete)
        {
            foreach (var ancestor in normalized
                         .Where(item =>
                             item.Kind == StudioDependencyKind.Ancestor &&
                             !string.IsNullOrWhiteSpace(item.Output))
                         .OrderBy(item => item.Distance)
                         .ThenBy(item => item.Sequence)
                         .ThenBy(item => item.PlanStepKey, StringComparer.Ordinal))
            {
                var minimumBudget = Math.Min(
                    ancestor.Output.Length,
                    MinimumAncestorOutputCharacters);
                var maximumBudget = Math.Min(
                    ancestor.Output.Length,
                    MaximumAncestorOutputCharacters);
                var selectedBudget = -1;
                for (var budget = maximumBudget;
                     budget >= minimumBudget;
                     budget--)
                {
                    var candidateEntries = entries
                        .Append(RenderStudioDependencyEntry(ancestor, budget))
                        .ToList();
                    if (RenderStudioDependencySection(candidateEntries).Length <=
                        maximumCharacters)
                    {
                        selectedBudget = budget;
                        break;
                    }
                }
                if (selectedBudget < 0)
                {
                    continue;
                }
                entries.Add(
                    RenderStudioDependencyEntry(
                        ancestor,
                        selectedBudget));
            }
        }

        var result = RenderStudioDependencySection(entries);
        if (result.Length > maximumCharacters)
        {
            throw new InvalidOperationException(
                "Studio dependency context exceeded its hard bound.");
        }
        return result;
    }

    private static int StudioDependencySectionLength(
        IReadOnlyList<StudioDependencyOutput> items,
        IReadOnlyList<int> budgets) =>
        RenderStudioDependencySection(
                items.Select((item, index) =>
                    RenderStudioDependencyEntry(item, budgets[index]))
                    .ToList())
            .Length;

    private static string RenderStudioDependencyEntry(
        StudioDependencyOutput item,
        int outputBudget)
    {
        var relationship = item.Kind == StudioDependencyKind.Direct
            ? "DIRECT DEPENDENCY"
            : $"ANCESTOR (distance {item.Distance})";
        var output = item.Output.Length <= outputBudget
            ? item.Output
            : item.Output[..outputBudget] +
              $"\n...[dependency output clipped: kept {outputBudget} of {item.Output.Length} characters]...";
        return
            $"### {relationship} `{item.PlanStepKey}` (effective attempt {item.Attempt})\n" +
            output;
    }

    private static string RenderStudioDependencySection(
        IReadOnlyList<string> entries) =>
        StudioPlanContextBegin +
        "\n" +
        string.Join("\n\n", entries) +
        "\n" +
        StudioPlanContextEnd;

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

    internal static string PrepareRepositoryKnowledgeContent(
        string knowledge,
        string sourceProjectPath)
    {
        var sanitized = RepositoryLocationPattern().Replace(
            RemoveSourceProjectPath(knowledge, sourceProjectPath),
            "- **Project files:** Materialized in the isolated workspace.");
        return sanitized.ReplaceLineEndings("\n").Trim();
    }

    internal static string BuildRepositoryKnowledgeBlock(
        string knowledge,
        string sourceProjectPath)
    {
        var prepared = PrepareRepositoryKnowledgeContent(
            knowledge,
            sourceProjectPath);
        return BuildRepositoryKnowledgeBlock(prepared);
    }

    private static string BuildRepositoryKnowledgeBlock(string prepared)
    {
        if (string.IsNullOrWhiteSpace(prepared))
        {
            return string.Empty;
        }

        return
            $"## Repository knowledge{Environment.NewLine}{Environment.NewLine}" +
            $"{RepositoryKnowledgeBegin}{Environment.NewLine}" +
            $"{prepared}{Environment.NewLine}" +
            RepositoryKnowledgeEnd;
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
        ExecutionInvocationKind invocationKind,
        bool allowRemotePublication,
        bool isGovernedOutcomeVerification = false)
    {
        if (allowRemotePublication && !isGovernedOutcomeVerification)
        {
            return null;
        }

        return BuildGuardedEnvironment(
            isGovernedOutcomeVerification
                ? "governed-host-publication-only"
                : invocationKind == ExecutionInvocationKind.PreMortem
                    ? "pre-mortem-read-only"
                    : "publication-not-authorized");
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
            ["GH_ENTERPRISE_TOKEN"] = null,
            ["GITHUB_ENTERPRISE_TOKEN"] = null,
            ["GITHUB_TOKEN_REQUEST_URL"] = null,
            ["GITHUB_TOKEN_REQUEST_TOKEN"] = null,
            ["SSH_AUTH_SOCK"] = null,
            ["SSH_AGENT_PID"] = null,
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "Never",
            ["GIT_ASKPASS"] = null,
            ["SSH_ASKPASS"] = null,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
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
        var repositoryKnowledgeBlock = BuildRepositoryKnowledgeBlock(
            knowledge,
            sourceProjectPath);
        return string.IsNullOrWhiteSpace(repositoryKnowledgeBlock)
            ? workspace
            : $"{workspace}{Environment.NewLine}{Environment.NewLine}{repositoryKnowledgeBlock}";
    }

    internal static string BuildDirectPrompt(
        AgentExecutionContext context,
        string agentInstructions,
        string workingDirectory)
    {
        var sections = new List<string>
        {
            context.DirectPrompt.Trim(),
            $"## Workspace{Environment.NewLine}{Environment.NewLine}" +
            PrepareWorkspace(workingDirectory)
        };
        var repositoryKnowledgeBlock = BuildRepositoryKnowledgeBlock(
            context.RepositoryKnowledge,
            context.SourceProjectPath);
        if (!string.IsNullOrWhiteSpace(repositoryKnowledgeBlock))
        {
            sections.Add(repositoryKnowledgeBlock);
        }
        sections.Add(
            $"## Quality Engineer role contract{Environment.NewLine}{Environment.NewLine}" +
            agentInstructions.Trim());
        return string.Join(
            $"{Environment.NewLine}{Environment.NewLine}",
            sections);
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
        bool canResumeSession = false,
        bool processTerminationUnconfirmed = false) =>
        new()
        {
            Success = false,
            OutputSummary = summary,
            Error = error,
            FailureKind = failureKind,
            FailedDependency = dependency,
            CanResumeSession = canResumeSession,
            ProcessTerminationUnconfirmed =
                processTerminationUnconfirmed
        };

    internal sealed record CopilotExecutionTimeouts(
        TimeSpan TurnTimeout,
        TimeSpan StallTimeout);

    internal sealed record RestrictedAgentAccess(
        string Root,
        string AgentId);

    internal static string ResponseContract(
        string agentRole,
        string contractVersion = "legacy-v1") =>
        ResponseContract(
            contractVersion == "studio-v2" &&
            string.Equals(agentRole, "team-lead", StringComparison.Ordinal)
                ? ExecutionInvocationKind.Planning
                : contractVersion == "studio-v2" &&
                  string.Equals(
                      agentRole,
                      "account-manager",
                      StringComparison.Ordinal)
                    ? ExecutionInvocationKind.Intake
                    : contractVersion == "studio-v2" &&
                      string.Equals(
                          agentRole,
                          "pre-mortem-sceptic",
                          StringComparison.Ordinal)
                        ? ExecutionInvocationKind.PreMortem
                        : ExecutionInvocationKind.Worker,
            agentRole,
            contractVersion);

    internal static string ResponseContract(
        ExecutionInvocationKind invocationKind,
        string agentRole,
        string contractVersion = "legacy-v1")
    {
        if (contractVersion == "studio-v2" &&
            invocationKind == ExecutionInvocationKind.Planning)
        {
            return """
              Select the smallest suitable downstream team from the exact enabled snapshot roster in the assignment.
              Start with exactly: HANDOFF_STATUS: COMPLETE
              Return exactly one strict team-plan-v1 JSON document between TEAM_PLAN_V1_BEGIN and TEAM_PLAN_V1_END.
              Use only exact roster Id values. Account Manager, Team Lead, and Pre-mortem Sceptic are never workers.
              Return either Planned or MissingQualification and follow every supplied property, enum, bound, duty, stage, dependency, outcome-owner, publication, and checkpoint rule exactly.
              Do not emit legacy TEAM_TASK_PROFILES or PRE_MORTEM_PLAN documents, Markdown fences, or duplicate sentinels.
              """;
        }
        if (contractVersion == "studio-v2" &&
            invocationKind is
                ExecutionInvocationKind.Intake or
                ExecutionInvocationKind.BlockerExplanation)
        {
            return """
              Classify the repository-grounded request as Advisory (inspect, recommend, or explain without source changes/publication) or Delivery (implement or change the product).
              Discuss customer outcomes, never workspace, branch, hook, tooling, or publication mechanics.
              Return exactly one strict JSON object between standalone INTAKE_V2_BEGIN and INTAKE_V2_END sentinels, with no Markdown fence and no duplicate sentinel:
              INTAKE_V2_BEGIN
              {"Version":"intake-v2","Status":"AwaitingConfirmation","FlowKind":"Advisory","TaskTitle":"Assess checkout resilience","CustomerReply":"Do I understand correctly that you want an assessment of checkout risks and a recommended approach?","Brief":{"Goal":"Identify the highest-impact checkout resilience gaps.","Details":["Inspect the configured source project."],"SuccessCriteria":["The customer receives an evidence-based recommendation."],"Constraints":["Do not change source files."],"Assumptions":[]}}
              INTAKE_V2_END
              Property names and enum casing are exact. Status is NeedsClarification, AwaitingConfirmation, or Confirmed. FlowKind is Advisory, Delivery, or null, and is mandatory for AwaitingConfirmation and Confirmed. Every Brief list is required, even when empty.
              Default to AwaitingConfirmation once meaningful work can begin. NeedsClarification asks at most one material customer-outcome question.
              Confirmed is valid only when the latest customer turn explicitly approves the immediately preceding proposal without a correction, unless the host assignment contains DURABLE_ADVISORY_PROMOTION_AUTHORIZATION for a linked Delivery first turn.
              A host-authorized promotion may confirm only after reading the host-controlled promotion seed file, and only for its exact durable Delivery goal and ordered implementation details; it may not add scope. A correction is not confirmation.
              On ordinary confirmation, reproduce the pending FlowKind, TaskTitle, and complete normalized Brief exactly. Never silently change the kind or brief.
              """;
        }
        if (contractVersion == "studio-v2" &&
            invocationKind == ExecutionInvocationKind.ReviewClassification)
        {
            return """
              Classify only the customer's current free-text review message against the supplied normalized result.
              Return exactly one strict JSON object between standalone REVIEW_FEEDBACK_V1_BEGIN and REVIEW_FEEDBACK_V1_END sentinels, with no Markdown fence and no duplicate sentinel:
              REVIEW_FEEDBACK_V1_BEGIN
              {"Version":"review-feedback-v1","Intent":"RequestRefinement","CustomerReply":"I’ll ask the team to narrow the result.","Refinement":{"Goal":"Limit the recommendation to checkout resilience.","RequestedChanges":["Exclude catalog and account services."]},"ExplicitImplementationAdoption":false}
              REVIEW_FEEDBACK_V1_END
              Property names and enum casing are exact. Intent is Accept, RequestRefinement, PromoteToDelivery, or Ambiguous.
              Use Accept only for unambiguous approval of the current result. Use PromoteToDelivery only when an Advisory customer explicitly asks to implement, build, ship, or otherwise adopt the result; set ExplicitImplementationAdoption to true. Use RequestRefinement only for requested changes and include a complete non-empty Goal plus 1-24 concrete RequestedChanges. Use Ambiguous when intent is not safe to infer and return one short customer-safe clarification.
              Refinement must be null unless Intent is RequestRefinement. ExplicitImplementationAdoption must be false unless Intent is PromoteToDelivery.
              Never expose tool logs, internal prompts, agent IDs, or operator diagnostics in CustomerReply.
              """;
        }
        if (contractVersion == "studio-v2" &&
            invocationKind is
                ExecutionInvocationKind.Worker or
                ExecutionInvocationKind.Publication)
        {
            return """
              Complete only the assigned plan step in the isolated workspace.
              Start with exactly one standalone line: HANDOFF_STATUS: COMPLETE or HANDOFF_STATUS: PUSHBACK.
              For PUSHBACK, immediately include exactly one PUSHBACK_OWNER_STEP_ID naming an earlier dependency or ancestor plan-step ID and exactly one bounded PUSHBACK_REASON.
              Do not use legacy role ownership markers. Return concise Decision, Deliverable, Evidence, and Next owner sections.
              When the supplied outcome contract requires flow-outcome-v1, emit that complete strict document after the handoff. It is mandatory for the final outcome owner.
              """;
        }

        return agentRole switch
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
              Keep the entire response under 9,000 characters and every finding field under 800 characters.
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
    }

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

    private readonly record struct RequiredPromptRange(int Start, int End)
    {
        public int Length => End - Start;
    }

    private static string ClipPrompt(
        string value,
        int maxCharacters,
        string repositoryKnowledgeBlock = "")
    {
        if (value.Length <= maxCharacters)
        {
            return value;
        }

        var requiredRanges = new List<RequiredPromptRange>(2);
        if (!string.IsNullOrWhiteSpace(repositoryKnowledgeBlock))
        {
            var knowledgeStart = value.IndexOf(
                repositoryKnowledgeBlock,
                StringComparison.Ordinal);
            if (knowledgeStart >= 0)
            {
                requiredRanges.Add(
                    new RequiredPromptRange(
                        knowledgeStart,
                        knowledgeStart + repositoryKnowledgeBlock.Length));
            }
        }

        var studioRange = FindDelimitedPromptRange(
            value,
            StudioPlanContextBegin,
            StudioPlanContextEnd,
            "The rendered Studio dependency context markers are invalid.");
        if (studioRange is not null)
        {
            requiredRanges.Add(studioRange.Value);
        }

        if (requiredRanges.Count == 0)
        {
            return Clip(value, maxCharacters);
        }

        var mergedRanges = MergeRequiredPromptRanges(requiredRanges);
        return mergedRanges.Count == 1
            ? ClipPromptAroundRequiredRange(
                value,
                maxCharacters,
                mergedRanges[0],
                preferPrefix: studioRange is null)
            : ClipPromptAroundRequiredRanges(
                value,
                maxCharacters,
                mergedRanges);
    }

    private static RequiredPromptRange? FindDelimitedPromptRange(
        string value,
        string beginMarker,
        string endMarker,
        string invalidMarkerMessage)
    {
        var begin = value.IndexOf(beginMarker, StringComparison.Ordinal);
        if (begin < 0)
        {
            return null;
        }

        var secondBegin = value.IndexOf(
            beginMarker,
            begin + beginMarker.Length,
            StringComparison.Ordinal);
        var endStart = value.IndexOf(
            endMarker,
            begin + beginMarker.Length,
            StringComparison.Ordinal);
        if (secondBegin >= 0 ||
            endStart < 0 ||
            value.IndexOf(
                endMarker,
                endStart + endMarker.Length,
                StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException(invalidMarkerMessage);
        }

        return new RequiredPromptRange(
            begin,
            endStart + endMarker.Length);
    }

    private static IReadOnlyList<RequiredPromptRange>
        MergeRequiredPromptRanges(
            IReadOnlyList<RequiredPromptRange> ranges)
    {
        var ordered = ranges
            .OrderBy(range => range.Start)
            .ThenBy(range => range.End)
            .ToList();
        var merged = new List<RequiredPromptRange>(ordered.Count);
        foreach (var range in ordered)
        {
            if (merged.Count == 0 || range.Start > merged[^1].End)
            {
                merged.Add(range);
                continue;
            }

            var previous = merged[^1];
            merged[^1] = new RequiredPromptRange(
                previous.Start,
                Math.Max(previous.End, range.End));
        }

        return merged;
    }

    private static string ClipPromptAroundRequiredRange(
        string value,
        int maxCharacters,
        RequiredPromptRange requiredRange,
        bool preferPrefix)
    {
        var available = maxCharacters - requiredRange.Length -
                        (PromptCompactionMarker.Length * 2);
        if (available < 0)
        {
            throw new InvalidOperationException(
                "The required prompt context cannot fit in the Copilot prompt envelope.");
        }

        var prefix = value[..requiredRange.Start];
        var suffix = value[requiredRange.End..];
        var prefixBudget = Math.Min(
            prefix.Length,
            preferPrefix ? available * 2 / 3 : available * 2 / 5);
        var suffixBudget = Math.Min(suffix.Length, available - prefixBudget);
        var unused = available - prefixBudget - suffixBudget;
        if (unused > 0)
        {
            var additionalPrefix = Math.Min(
                prefix.Length - prefixBudget,
                unused);
            prefixBudget += additionalPrefix;
            unused -= additionalPrefix;
            suffixBudget += Math.Min(
                suffix.Length - suffixBudget,
                unused);
        }

        return prefix[..prefixBudget] +
               PromptCompactionMarker +
               value[requiredRange.Start..requiredRange.End] +
               PromptCompactionMarker +
               suffix[^suffixBudget..];
    }

    private static string ClipPromptAroundRequiredRanges(
        string value,
        int maxCharacters,
        IReadOnlyList<RequiredPromptRange> requiredRanges)
    {
        var gaps = new List<RequiredPromptRange>(
            requiredRanges.Count + 1);
        var cursor = 0;
        foreach (var range in requiredRanges)
        {
            gaps.Add(new RequiredPromptRange(cursor, range.Start));
            cursor = range.End;
        }
        gaps.Add(new RequiredPromptRange(cursor, value.Length));

        var requiredCharacters = requiredRanges.Sum(range => range.Length);
        var available = maxCharacters - requiredCharacters;
        var gapBudgets = gaps
            .Select(gap => gap.Length == 0
                ? 0
                : Math.Min(gap.Length, PromptCompactionMarker.Length))
            .ToArray();
        var remaining = available - gapBudgets.Sum();
        if (remaining < 0)
        {
            throw new InvalidOperationException(
                "The required prompt contexts cannot fit in the Copilot prompt envelope.");
        }

        while (remaining > 0)
        {
            var expandable = Enumerable.Range(0, gaps.Count)
                .Where(index => gapBudgets[index] < gaps[index].Length)
                .ToList();
            if (expandable.Count == 0)
            {
                break;
            }

            var share = Math.Max(1, remaining / expandable.Count);
            foreach (var index in expandable)
            {
                var added = Math.Min(
                    gaps[index].Length - gapBudgets[index],
                    share);
                gapBudgets[index] += added;
                remaining -= added;
                if (remaining == 0)
                {
                    break;
                }
            }
        }

        var builder = new StringBuilder(maxCharacters);
        for (var index = 0; index < requiredRanges.Count; index++)
        {
            AppendBoundedPromptGap(
                builder,
                value,
                gaps[index],
                gapBudgets[index]);
            var required = requiredRanges[index];
            builder.Append(
                value.AsSpan(required.Start, required.Length));
        }
        AppendBoundedPromptGap(
            builder,
            value,
            gaps[^1],
            gapBudgets[^1]);
        return builder.ToString();
    }

    private static void AppendBoundedPromptGap(
        StringBuilder builder,
        string value,
        RequiredPromptRange gap,
        int budget)
    {
        if (gap.Length == 0 || budget == 0)
        {
            return;
        }
        if (gap.Length <= budget)
        {
            builder.Append(value.AsSpan(gap.Start, gap.Length));
            return;
        }

        var available = budget - PromptCompactionMarker.Length;
        var headLength = available * 2 / 3;
        builder.Append(value.AsSpan(gap.Start, headLength));
        builder.Append(PromptCompactionMarker);
        var tailLength = available - headLength;
        builder.Append(
            value.AsSpan(gap.End - tailLength, tailLength));
    }

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

    internal sealed class PublicationGuardScope : IDisposable
    {
        private readonly string _root;

        private PublicationGuardScope(string root)
        {
            _root = root;
            EnvironmentVariables = new Dictionary<string, string?>(
                BuildGuardedEnvironment("publication-not-authorized"),
                StringComparer.Ordinal)
            {
                ["GH_CONFIG_DIR"] = root
            };
        }

        public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; }

        public static PublicationGuardScope Create(
            string copilotHome,
            string workingDirectory)
        {
            var root = Path.Combine(
                Path.GetFullPath(copilotHome),
                "harness-runtime",
                "gh-config",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var scope = new PublicationGuardScope(root);
            var environment =
                (Dictionary<string, string?>)scope.EnvironmentVariables;
            var remoteNames = DiscoverRemoteNames(workingDirectory)
                .Where(name => !string.Equals(
                    name,
                    "origin",
                    StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var count = int.Parse(
                environment["GIT_CONFIG_COUNT"]!,
                System.Globalization.CultureInfo.InvariantCulture);
            foreach (var remoteName in remoteNames)
            {
                environment[$"GIT_CONFIG_KEY_{count}"] =
                    $"remote.{remoteName}.pushurl";
                environment[$"GIT_CONFIG_VALUE_{count}"] =
                    "disabled://publication-not-authorized";
                count++;
            }
            environment["GIT_CONFIG_COUNT"] =
                count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return scope;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup; a later run uses a different empty directory.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup; permissions must not turn execution success into failure.
            }
        }

        private static IEnumerable<string> DiscoverRemoteNames(
            string workingDirectory)
        {
            foreach (var repository in
                     CandidateFingerprintService.DiscoverRepositories(
                         workingDirectory))
            {
                var start = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = repository,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                start.ArgumentList.Add("remote");
                using var process = System.Diagnostics.Process.Start(start);
                if (process is null ||
                    !process.WaitForExit((int)TimeSpan.FromSeconds(3).TotalMilliseconds) ||
                    process.ExitCode != 0)
                {
                    continue;
                }
                foreach (var name in process.StandardOutput.ReadToEnd()
                             .Split(
                                 ['\r', '\n'],
                                 StringSplitOptions.RemoveEmptyEntries |
                                 StringSplitOptions.TrimEntries)
                             .Where(name =>
                                 name.All(character =>
                                     char.IsAsciiLetterOrDigit(character) ||
                                     character is '-' or '_' or '.')))
                {
                    yield return name;
                }
            }
        }
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

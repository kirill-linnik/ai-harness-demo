using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using AiHarnessDemo.Api;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Infrastructure;
using AiHarnessDemo.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiHarnessDemo.Tests;

public sealed class CopilotJsonlParserTests
{
    [Fact]
    public void Parse_ToleratesUnknownLinesAndPersistsOnlyScrubbedToolArguments()
    {
        const string jsonl = """
            not-json
            {"type":"tool.execution_start","data":{"toolCallId":"one","toolName":"write","arguments":{"path":"secret.txt","token":"do-not-store"}}}
            {"type":"future.event","data":{"anything":true}}
            {"type":"tool.execution_complete","data":{"toolCallId":"one","success":true,"result":{"content":"done"}}}
            {"type":"assistant.message","data":{"content":"Decision\nDeliverable\nEvidence\nNext owner"}}
            {"type":"result","exitCode":0}
            """;

        var result = CopilotJsonlParser.Parse(jsonl);

        Assert.True(result.Success);
        Assert.Equal("Decision\nDeliverable\nEvidence\nNext owner", result.OutputSummary);
        var toolCall = Assert.Single(result.ToolCalls);
        Assert.Equal("write", toolCall.ToolName);
        Assert.Contains("path", toolCall.ArgumentsSummary);
        Assert.DoesNotContain("secret.txt", toolCall.ArgumentsSummary);
        Assert.DoesNotContain("do-not-store", toolCall.ArgumentsSummary);
        Assert.Contains("secret.txt", toolCall.NormalizedArguments);
        Assert.DoesNotContain("do-not-store", toolCall.NormalizedArguments);
        Assert.Contains("redacted", toolCall.NormalizedArguments);
        Assert.StartsWith("sha256:", toolCall.ResultDigest);
        Assert.Equal("done", toolCall.ResultSummary);
    }

    [Fact]
    public void Parse_PersistsOnlyNormalizedCommandDigestForQaCorrelation()
    {
        const string jsonl = """
            {"type":"tool.execution_start","data":{"toolCallId":"one","toolName":"powershell","arguments":{"command":"  dotnet   test .\\AiHarnessDemo.slnx  ","description":"Run tests"}}}
            {"type":"tool.execution_complete","data":{"toolCallId":"one","success":true}}
            {"type":"assistant.message","data":{"content":"HANDOFF_STATUS: COMPLETE"}}
            {"type":"result","exitCode":0}
            """;

        var workspace = Path.GetFullPath(Path.GetTempPath());
        var result = CopilotJsonlParser.Parse(
            jsonl,
            workingDirectory: workspace);

        var toolCall = Assert.Single(result.ToolCalls);
        Assert.True(toolCall.Succeeded);
        Assert.Equal("Command", toolCall.ToolType);
        Assert.Equal(
            "dotnet test ./AiHarnessDemo.slnx",
            toolCall.NormalizedCommand);
        Assert.Equal(workspace, toolCall.WorkingDirectory);
        Assert.True(HostObservedToolLocator.MatchesCommand(
            toolCall.ArgumentsSummary,
            "dotnet test ./AiHarnessDemo.slnx"));
        Assert.DoesNotContain("AiHarnessDemo.slnx", toolCall.ArgumentsSummary);
    }

    [Fact]
    public void Parse_TreatsNonzeroToolExitAsUnsuccessfulWithoutSuccessFlag()
    {
        const string jsonl = """
            {"type":"tool.execution_start","data":{"toolCallId":"one","toolName":"powershell","arguments":{"command":"dotnet test"}}}
            {"type":"tool.execution_complete","data":{"toolCallId":"one","result":{"exitCode":1}}}
            {"type":"assistant.message","data":{"content":"HANDOFF_STATUS: COMPLETE"}}
            {"type":"result","exitCode":0}
            """;

        var result = CopilotJsonlParser.Parse(jsonl);

        Assert.False(Assert.Single(result.ToolCalls).Succeeded);
    }

    [Fact]
    public void Parse_FailsClosedWhenNoAssistantHandoffExists()
    {
        var result = CopilotJsonlParser.Parse(
            """{"type":"result","exitCode":0}""");

        Assert.False(result.Success);
        Assert.Equal(AgentRunFailureKind.InvalidOutput, result.FailureKind);
    }

    [Fact]
    public void Parse_UsesCompletedRootDeltasWhenFinalMessageEventIsMissing()
    {
        const string jsonl = """
            {"type":"assistant.message_start","data":{"messageId":"root"}}
            {"type":"assistant.message_delta","data":{"messageId":"root","deltaContent":"INTAKE_STATUS: AWAITING_CONFIRMATION\n"}}
            {"type":"assistant.message_delta","data":{"messageId":"root","deltaContent":"CUSTOMER_REPLY: Is this the refresh you want? If yes, I'll send it to the team.\nTASK_BRIEF: Refresh the site."}}
            {"type":"assistant.message","agentId":"child","data":{"messageId":"child","content":"Ignore child response."}}
            {"type":"assistant.turn_end","data":{"turnId":"0"}}
            {"type":"result"}
            """;

        var result = CopilotJsonlParser.Parse(jsonl);

        Assert.True(result.Success);
        Assert.Equal(
            "INTAKE_STATUS: AWAITING_CONFIRMATION\nCUSTOMER_REPLY: Is this the refresh you want? If yes, I'll send it to the team.\nTASK_BRIEF: Refresh the site.",
            result.OutputSummary);
    }

    [Fact]
    public void Parse_ReportsSessionErrorsAndMarksRateLimitsTransient()
    {
        const string jsonl = """
            {"type":"session.error","data":{"errorType":"rate_limit","message":"Please retry shortly.","statusCode":429}}
            """;

        var result = CopilotJsonlParser.Parse(jsonl);

        Assert.False(result.Success);
        Assert.Equal(AgentRunFailureKind.Transient, result.FailureKind);
        Assert.Contains("rate_limit", result.Error);
        Assert.Contains("Please retry shortly.", result.Error);
    }

    [Fact]
    public void Parse_ClassifiesUnsupportedModelsSeparatelyFromQualityFailures()
    {
        var result = CopilotJsonlParser.Parse(
            """
            {"type":"session.error","data":{"errorType":"unsupported_model","message":"Unknown model."}}
            """);

        Assert.False(result.Success);
        Assert.Equal(AgentRunFailureKind.ModelUnavailable, result.FailureKind);
        Assert.Equal("model-candidate", result.FailedDependency);
    }

    [Fact]
    public void ProgressReporter_CapturesTheActualCopilotSession()
    {
        var progress = new List<AgentRunProgress>();
        var reporter = CopilotJsonlParser.CreateProgressReporter(
            progress.Add,
            @"C:\copilot-home");
        var sessionId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");

        reporter(JsonSerializer.Serialize(new
        {
            type = "session.start",
            data = new
            {
                sessionId
            }
        }));

        var observed = Assert.Single(progress);
        Assert.Equal(AgentRunPhase.InitializingSession, observed.Phase);
        Assert.Equal(sessionId, observed.CopilotSessionId);
        Assert.Equal(@"C:\copilot-home", observed.CopilotSessionHome);
    }

    [Fact]
    public void Parse_TreatsAnEarlyExitWithStandardErrorAsAmbiguous()
    {
        var result = CopilotJsonlParser.Parse(
            string.Empty,
            "\u001b[31;1mThe transport closed unexpectedly.\u001b[0m");

        Assert.False(result.Success);
        Assert.Equal(AgentRunFailureKind.AmbiguousCrash, result.FailureKind);
        Assert.Equal(
            "Copilot CLI ended without a complete assistant response. " +
            "The transport closed unexpectedly.",
            result.Error);
    }
}

public sealed class AgentHandoffInspectorTests
{
    [Fact]
    public void GetPushbackReason_UsesStructuredHandoffStatus()
    {
        const string output = """
            HANDOFF_STATUS: PUSHBACK
            PUSHBACK_REASON: Software Engineer omitted the acceptance-to-test mapping.

            ## Decision
            Revision required.
            """;

        Assert.Equal(
            "Software Engineer omitted the acceptance-to-test mapping.",
            AgentHandoffInspector.GetPushbackReason(output));
    }

    [Fact]
    public void GetPushbackReason_CompleteStatusOverridesIncidentalPushbackText()
    {
        const string output = """
            HANDOFF_STATUS: COMPLETE

            ## Decision
            PUSHBACK: none; the work is complete.
            """;

        Assert.Null(AgentHandoffInspector.GetPushbackReason(output));
    }

    [Theory]
    [InlineData(
        "## PUSHBACK\n\n**Missing detail:** Workspace access is unavailable.",
        "Missing detail: Workspace access is unavailable.")]
    [InlineData(
        "## Decision\n\n**PUSHBACK** - I cannot implement without repository access.",
        "I cannot implement without repository access.")]
    [InlineData(
        "- **PUSHBACK** \u2014 Required files are unavailable.",
        "Required files are unavailable.")]
    public void GetPushbackReason_RecognizesExplicitContractMarkers(
        string output,
        string expectedReason)
    {
        var reason = AgentHandoffInspector.GetPushbackReason(output);

        Assert.Equal(expectedReason, reason);
    }

    [Fact]
    public void GetPushbackReason_DoesNotRejectACompletedHandoffThatMentionsPushback()
    {
        const string output = """
            ## Decision
            The implementation is complete; no pushback is required.

            ## Deliverable
            Working code.
            """;

        Assert.Null(AgentHandoffInspector.GetPushbackReason(output));
    }

    [Theory]
    [InlineData("PRE_MORTEM_DISPOSITION: UNCHANGED", false)]
    [InlineData("HANDOFF_STATUS: PUSHBACK\nPRE_MORTEM_DISPOSITION: UNCHANGED", false)]
    [InlineData("HANDOFF_STATUS: COMPLETE\nPRE_MORTEM_DISPOSITION: UNCHANGED", true)]
    [InlineData("HANDOFF_STATUS: COMPLETE\nHANDOFF_STATUS: COMPLETE", false)]
    public void CompleteStatus_RequiresOneExplicitCompleteMarker(
        string output,
        bool expected)
    {
        Assert.Equal(expected, AgentHandoffInspector.HasCompleteStatus(output));
    }

    [Fact]
    public void PreMortemRevisionContract_RejectsPushbackEvenWithDisposition()
    {
        const string output = """
            HANDOFF_STATUS: PUSHBACK
            PUSHBACK_REASON: More upstream evidence is required.
            PRE_MORTEM_DISPOSITION: UNCHANGED
            """;

        Assert.Throws<PreMortemValidationException>(
            () => WorkflowEngine.ValidatePreMortemRevisionOutput(output));
    }

    [Fact]
    public void ApplyFlowFailure_SkipsEveryPendingDownstreamStep()
    {
        var flow = new FlowRun
        {
            Title = "Refresh the site",
            OriginalRequest = "Refresh the site",
            Status = FlowStatus.Running
        };
        var pending = new[]
        {
            new FlowStep
            {
                FlowRunId = flow.Id,
                Iteration = 1,
                Sequence = 30,
                AgentId = "software-engineer",
                AgentName = "Software Engineer",
                AgentRole = "software-engineer"
            },
            new FlowStep
            {
                FlowRunId = flow.Id,
                Iteration = 1,
                Sequence = 40,
                AgentId = "quality-engineer",
                AgentName = "Quality Engineer",
                AgentRole = "quality-engineer"
            }
        };

        var events = WorkflowEngine.ApplyFlowFailure(
            flow,
            pending,
            "Architect pushed back and stopped the flow.");

        Assert.Equal(FlowStatus.Failed, flow.Status);
        Assert.All(pending, step => Assert.Equal(StepStatus.Skipped, step.Status));
        Assert.Equal(2, events.Count(item => item.Type == "step.skipped"));
        Assert.Contains(events, item => item.Type == "flow.failed");
    }
}

public sealed class GovernedPublicationBoundaryTests
{
    [Theory]
    [InlineData("team-lead")]
    [InlineData("architect")]
    [InlineData("software-engineer")]
    [InlineData("quality-engineer")]
    [InlineData("release-engineer")]
    [InlineData("product-manager")]
    public void EveryGovernedNonPublicationCopilotTurn_IsRemotePublicationGuarded(
        string agentRole)
    {
        var environment =
            Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(
                CopilotReasoningHost.BuildProcessEnvironment(
                    agentRole,
                    allowRemotePublication: true,
                    isGovernedOutcomeVerification: true));
        var arguments = CopilotReasoningHost.BuildCliArguments(
            @"C:\governed-worktree",
            @"C:\harness",
            agentRole,
            agentRole,
            "model",
            "high",
            Guid.NewGuid(),
            "Governed turn",
            isGovernedOutcomeVerification: true,
            blockRemotePublication: true);

        Assert.Null(environment["GH_TOKEN"]);
        Assert.Null(environment["GITHUB_TOKEN"]);
        Assert.Null(environment["SSH_AUTH_SOCK"]);
        Assert.Equal("Never", environment["GCM_INTERACTIVE"]);
        Assert.Contains(
            $"--available-tools={CopilotReasoningHost.GovernedNonPublicationTools()}",
            arguments);
        Assert.Contains("--allow-tool=write", arguments);
        Assert.Contains("--allow-tool=shell", arguments);
        Assert.Contains(
            "--secret-env-vars=COPILOT_GITHUB_TOKEN,GH_TOKEN,GITHUB_TOKEN,SSH_AUTH_SOCK,GIT_ASKPASS,SSH_ASKPASS",
            arguments);
        Assert.Contains("--disable-builtin-mcps", arguments);
        Assert.DoesNotContain("--allow-all-tools", arguments);
        Assert.DoesNotContain("--deny-tool=shell(git:*)", arguments);
        Assert.Contains("--deny-tool=shell(git.exe:*)", arguments);
        Assert.Contains("--deny-tool=shell(git commit)", arguments);
        Assert.Contains("--deny-tool=shell(git.exe commit)", arguments);
        Assert.Contains("--deny-tool=shell(git branch)", arguments);
        Assert.Contains("--deny-tool=shell(git checkout)", arguments);
        Assert.Contains("--deny-tool=shell(git switch)", arguments);
        Assert.Contains("--deny-tool=shell(git tag)", arguments);
        Assert.Contains("--deny-tool=shell(git push)", arguments);
        Assert.Contains("--deny-tool=shell(git send-pack)", arguments);
        Assert.Contains("--deny-tool=shell(gh:*)", arguments);
        Assert.Contains("--deny-tool=shell(ssh:*)", arguments);
        Assert.Contains("--deny-url=github.com", arguments);
        Assert.Contains("--deny-url=api.github.com", arguments);
        Assert.Contains("--no-remote", arguments);
        Assert.Contains("--no-remote-export", arguments);
    }

    [Theory]
    [InlineData("git status --short")]
    [InlineData("git -C repo diff --stat")]
    [InlineData("git show HEAD:file.txt")]
    [InlineData("git log -1 --format=%H")]
    [InlineData("git rev-parse HEAD")]
    [InlineData("git ls-files")]
    [InlineData("git cat-file -p HEAD")]
    public void GovernedGitArgumentPolicy_AllowsReadOnlyCommands(string command)
    {
        Assert.True(HostObservedToolLocator.IsReadOnlyGitCommand(command));
    }

    [Theory]
    [InlineData("git add -A")]
    [InlineData("git -C repo commit -m sealed")]
    [InlineData("git update-ref refs/heads/test HEAD")]
    [InlineData("git push origin HEAD")]
    [InlineData("git -c core.hooksPath=NUL reset --hard HEAD")]
    [InlineData("git --git-dir=C:/outside/.git show HEAD:file.txt")]
    [InlineData("git.exe status --short")]
    public void GovernedGitArgumentPolicy_DeniesMutatingOrNetworkingCommands(
        string command)
    {
        Assert.False(HostObservedToolLocator.IsReadOnlyGitCommand(command));
    }

    [Fact]
    public void HostControlledPublicationTurn_KeepsOnlyReadOnlyLocalTools()
    {
        var arguments = CopilotReasoningHost.BuildCliArguments(
            @"C:\governed-worktree",
            @"C:\harness",
            "release-engineer",
            "release-engineer",
            "model",
            "high",
            Guid.NewGuid(),
            "Prepare the host-controlled publication narrative.",
            isHostControlledPublication: true,
            isGovernedOutcomeVerification: true,
            blockRemotePublication: true);

        Assert.Contains("--available-tools=view,grep,glob", arguments);
        Assert.Contains("--disable-builtin-mcps", arguments);
        Assert.Contains("--deny-tool=write,shell", arguments);
        Assert.DoesNotContain(
            $"--available-tools={CopilotReasoningHost.GovernedNonPublicationTools()}",
            arguments);
        Assert.DoesNotContain("--allow-all-tools", arguments);
        Assert.Contains("--deny-tool=shell(git push)", arguments);
        Assert.Contains("--deny-tool=shell(gh:*)", arguments);
        Assert.Contains("--deny-url=github.com", arguments);
        Assert.Contains("--no-remote", arguments);
        Assert.Contains("--no-remote-export", arguments);
    }

    [Fact]
    public async Task GovernedGitIsolation_HidesAuthoritativePointerAndRejectsShadowMetadataMutation()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"governed-git-isolation-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var worktree = Path.Combine(root, "worktree");
        Directory.CreateDirectory(source);
        try
        {
            RunGit(source, "init", "--quiet");
            RunGit(source, "config", "user.email", "tests@example.invalid");
            RunGit(source, "config", "user.name", "Governed Isolation Tests");
            await File.WriteAllTextAsync(
                Path.Combine(source, "tracked.txt"),
                "initial");
            RunGit(source, "add", "tracked.txt");
            RunGit(source, "commit", "--quiet", "-m", "initial");
            RunGit(
                source,
                "worktree",
                "add",
                "--quiet",
                "-b",
                "test/governed",
                worktree);
            var markerPath = Path.Combine(worktree, ".git");
            var originalMarkerBytes = File.ReadAllBytes(markerPath);
            var realGitDirectory = RunGitOutput(
                    worktree,
                    "rev-parse",
                    "--absolute-git-dir")
                .Trim();
            var realHead = RunGitOutput(worktree, "rev-parse", "HEAD").Trim();
            var realTree = RunGitOutput(worktree, "rev-parse", "HEAD^{tree}").Trim();
            const string directShadowRef = "refs/heads/agent-created";
            const string childShadowRef = "refs/heads/child-created";

            string isolationRoot;
            using (var isolation =
                   CopilotReasoningHost.GovernedGitIsolationScope.Create(worktree))
            {
                isolationRoot = isolation.RootPath;
                Assert.False(File.Exists(markerPath));
                Assert.True(Directory.Exists(markerPath));
                Assert.Throws<InvalidOperationException>(() =>
                    CopilotReasoningHost.GovernedGitIsolationScope
                        .RecoverInterrupted(worktree));
                var shadowGitDirectory = await RunGitProcessAsync(
                    worktree,
                    isolation.EnvironmentVariables,
                    "rev-parse",
                    "--absolute-git-dir");
                var tree = await RunGitProcessAsync(
                    worktree,
                    isolation.EnvironmentVariables,
                    "rev-parse",
                    "HEAD^{tree}");
                var show = await RunGitProcessAsync(
                    worktree,
                    isolation.EnvironmentVariables,
                    "show",
                    "HEAD:tracked.txt");
                var log = await RunGitProcessAsync(
                    worktree,
                    isolation.EnvironmentVariables,
                    "log",
                    "-1",
                    "--format=%H");
                var diff = await RunGitProcessAsync(
                    worktree,
                    isolation.EnvironmentVariables,
                    "diff",
                    "--",
                    "tracked.txt");
                var status = await RunGitProcessAsync(
                    worktree,
                    isolation.EnvironmentVariables,
                    "status",
                    "--porcelain=v1");
                var directMutation = await RunGitProcessAsync(
                    worktree,
                    isolation.EnvironmentVariables,
                    "update-ref",
                    directShadowRef,
                    realHead);
                var directFileMutation = await RunShellCommandAsync(
                    worktree,
                    OperatingSystem.IsWindows()
                        ? "echo unauthorized>.git\\direct-write"
                        : "printf unauthorized > .git/direct-write",
                    isolation.EnvironmentVariables);
                var nestedGitMutation = await RunShellCommandAsync(
                    worktree,
                    OperatingSystem.IsWindows()
                        ? "mkdir rogue\\.git && echo unauthorized>rogue\\.git\\config"
                        : "mkdir -p rogue/.git && printf unauthorized > rogue/.git/config",
                    isolation.EnvironmentVariables);
                var childMutation = await RunShellCommandAsync(
                    worktree,
                    $"git update-ref {childShadowRef} {realHead}",
                    isolation.EnvironmentVariables);
                var repositoryOverride = await RunShellCommandAsync(
                    worktree,
                    $"git --git-dir=\"{realGitDirectory}\" rev-parse HEAD",
                    isolation.EnvironmentVariables);
                var remoteRead = await RunShellCommandAsync(
                    worktree,
                    "git ls-remote https://example.invalid/repository.git",
                    isolation.EnvironmentVariables);

                Assert.Equal(0, shadowGitDirectory.ExitCode);
                Assert.Equal(0, tree.ExitCode);
                Assert.Equal(0, show.ExitCode);
                Assert.Equal(0, log.ExitCode);
                Assert.Equal(0, diff.ExitCode);
                Assert.Equal(0, status.ExitCode);
                Assert.Equal(0, directMutation.ExitCode);
                Assert.Equal(0, directFileMutation.ExitCode);
                Assert.Equal(0, nestedGitMutation.ExitCode);
                Assert.Equal(126, childMutation.ExitCode);
                Assert.Equal(126, repositoryOverride.ExitCode);
                Assert.Equal(126, remoteRead.ExitCode);
                Assert.Equal(
                    Path.GetFullPath(markerPath),
                    Path.GetFullPath(shadowGitDirectory.StandardOutput.Trim()),
                    OperatingSystem.IsWindows()
                        ? StringComparer.OrdinalIgnoreCase
                        : StringComparer.Ordinal);
                Assert.Equal(realTree, tree.StandardOutput.Trim());
                Assert.Equal("initial", show.StandardOutput.Trim());
                Assert.Equal(realHead, log.StandardOutput.Trim());
                Assert.True(string.IsNullOrWhiteSpace(diff.StandardOutput));
                Assert.True(string.IsNullOrWhiteSpace(status.StandardOutput));
                var directShadowLookup = await RunGitProcessAsync(
                    worktree,
                    isolation.EnvironmentVariables,
                    "show-ref",
                    "--hash",
                    directShadowRef);
                var childShadowLookup = await RunGitProcessAsync(
                    worktree,
                    isolation.EnvironmentVariables,
                    "show-ref",
                    "--hash",
                    childShadowRef);
                Assert.Equal(realHead, directShadowLookup.StandardOutput.Trim());
                Assert.NotEqual(0, childShadowLookup.ExitCode);
                isolation.RestoreAndValidate();
                Assert.True(isolation.UnauthorizedMetadataMutationDetected);
            }

            Assert.False(Directory.Exists(isolationRoot));
            Assert.False(Directory.Exists(Path.Combine(worktree, "rogue", ".git")));
            Assert.Equal(originalMarkerBytes, File.ReadAllBytes(markerPath));
            Assert.Equal(
                realGitDirectory,
                RunGitOutput(worktree, "rev-parse", "--absolute-git-dir").Trim());
            Assert.Equal(realHead, RunGitOutput(worktree, "rev-parse", "HEAD").Trim());
            Assert.NotEqual(
                0,
                TryRunGit(
                    worktree,
                    "show-ref",
                    "--verify",
                    directShadowRef));
            Assert.NotEqual(
                0,
                TryRunGit(
                    worktree,
                    "show-ref",
                    "--verify",
                    childShadowRef));
        }
        finally
        {
            if (Directory.Exists(source))
            {
                _ = TryRunGit(
                    source,
                    "worktree",
                    "remove",
                    "--force",
                    worktree);
            }
            if (Directory.Exists(root))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(
                                 root,
                                 "*",
                                 SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    Directory.Delete(root, recursive: true);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    // Test cleanup only.
                }
            }
        }
    }

    [Fact]
    public async Task GovernedGitIsolation_RecoversPointerAfterInjectedHostInterruption()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            $"gitiso-rec-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var worktree = Path.Combine(root, "worktree");
        Directory.CreateDirectory(source);
        try
        {
            RunGit(source, "init", "--quiet");
            RunGit(source, "config", "user.email", "tests@example.invalid");
            RunGit(source, "config", "user.name", "Governed Isolation Tests");
            await File.WriteAllTextAsync(Path.Combine(source, "tracked.txt"), "initial");
            RunGit(source, "add", "tracked.txt");
            RunGit(source, "commit", "--quiet", "-m", "initial");
            RunGit(
                source,
                "worktree",
                "add",
                "--quiet",
                "-b",
                "test/governed-recovery",
                worktree);
            var markerPath = Path.Combine(worktree, ".git");
            var originalMarkerBytes = File.ReadAllBytes(markerPath);
            var originalHead = RunGitOutput(worktree, "rev-parse", "HEAD").Trim();

            using (var interrupted =
                   CopilotReasoningHost.GovernedGitIsolationScope.Create(worktree))
            {
                Assert.True(Directory.Exists(markerPath));
                await File.WriteAllTextAsync(
                    Path.Combine(worktree, "tracked.txt"),
                    "product edit survives");
                await File.WriteAllTextAsync(
                    Path.Combine(markerPath, "unauthorized"),
                    "discard me");
                interrupted.LeaveInterruptedForRecoveryTest();
            }

            Assert.True(Directory.Exists(markerPath));
            CopilotReasoningHost.GovernedGitIsolationScope.RecoverInterrupted(worktree);

            Assert.True(File.Exists(markerPath));
            Assert.False(Directory.Exists(markerPath));
            Assert.Equal(originalMarkerBytes, File.ReadAllBytes(markerPath));
            Assert.Equal(originalHead, RunGitOutput(worktree, "rev-parse", "HEAD").Trim());
            Assert.Equal(
                "product edit survives",
                await File.ReadAllTextAsync(Path.Combine(worktree, "tracked.txt")));
            Assert.Contains(
                "tracked.txt",
                RunGitOutput(worktree, "status", "--short"));
        }
        finally
        {
            if (Directory.Exists(source))
            {
                _ = TryRunGit(
                    source,
                    "worktree",
                    "remove",
                    "--force",
                    worktree);
            }
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(
                             root,
                             "*",
                             SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GovernedGitIsolation_DispatchesReadOnlyGitPerRepository()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"governed-git-multi-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var sources = new[]
        {
            Path.Combine(root, "source-a"),
            Path.Combine(root, "source-b")
        };
        var worktrees = new[]
        {
            Path.Combine(workspace, "a"),
            Path.Combine(workspace, "b")
        };
        Directory.CreateDirectory(workspace);
        try
        {
            for (var index = 0; index < sources.Length; index++)
            {
                Directory.CreateDirectory(sources[index]);
                RunGit(sources[index], "init", "--quiet");
                RunGit(sources[index], "config", "user.email", "tests@example.invalid");
                RunGit(sources[index], "config", "user.name", "Governed Isolation Tests");
                await File.WriteAllTextAsync(
                    Path.Combine(sources[index], "tracked.txt"),
                    $"repository-{index}");
                RunGit(sources[index], "add", "tracked.txt");
                RunGit(sources[index], "commit", "--quiet", "-m", "initial");
                RunGit(
                    sources[index],
                    "worktree",
                    "add",
                    "--quiet",
                    "-b",
                    $"test/governed-{index}",
                    worktrees[index]);
            }
            var markerBytes = worktrees.ToDictionary(
                path => path,
                path => File.ReadAllBytes(Path.Combine(path, ".git")));
            var planted = Path.Combine(workspace, "planted");
            Directory.CreateDirectory(planted);
            await File.WriteAllTextAsync(
                Path.Combine(planted, ".git"),
                $"gitdir: {Path.Combine(sources[0], ".git").Replace('\\', '/')}\n");

            using (var isolation =
                   CopilotReasoningHost.GovernedGitIsolationScope.Create(
                       workspace,
                       ["a", "b"]))
            {
                for (var index = 0; index < worktrees.Length; index++)
                {
                    var read = await RunShellCommandAsync(
                        workspace,
                        $"git -C \"{worktrees[index]}\" show HEAD:tracked.txt",
                        isolation.EnvironmentVariables);
                    Assert.True(read.ExitCode == 0, read.CombinedOutput);
                    Assert.Equal(
                        $"repository-{index}",
                        read.StandardOutput.Trim());
                    Assert.False(File.Exists(Path.Combine(worktrees[index], ".git")));
                    Assert.True(Directory.Exists(Path.Combine(worktrees[index], ".git")));
                }
                var plantedRead = await RunShellCommandAsync(
                    workspace,
                    $"git -C \"{planted}\" rev-parse HEAD",
                    isolation.EnvironmentVariables);
                Assert.Equal(128, plantedRead.ExitCode);
                isolation.RestoreAndValidate();
                Assert.False(isolation.UnauthorizedMetadataMutationDetected);
            }

            foreach (var worktree in worktrees)
            {
                Assert.Equal(
                    markerBytes[worktree],
                    File.ReadAllBytes(Path.Combine(worktree, ".git")));
            }
        }
        finally
        {
            for (var index = 0; index < sources.Length; index++)
            {
                if (Directory.Exists(sources[index]))
                {
                    _ = TryRunGit(
                        sources[index],
                        "worktree",
                        "remove",
                        "--force",
                        worktrees[index]);
                }
            }
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(
                             root,
                             "*",
                             SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GovernedGitIsolation_PreservesSha256ObjectFormat()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"governed-git-sha256-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var worktree = Path.Combine(root, "worktree");
        Directory.CreateDirectory(source);
        try
        {
            if (TryRunGit(source, "init", "--quiet", "--object-format=sha256") != 0)
            {
                return;
            }
            RunGit(source, "config", "user.email", "tests@example.invalid");
            RunGit(source, "config", "user.name", "Governed Isolation Tests");
            await File.WriteAllTextAsync(
                Path.Combine(source, "tracked.txt"),
                "sha256");
            RunGit(source, "add", "tracked.txt");
            RunGit(source, "commit", "--quiet", "-m", "initial");
            RunGit(
                source,
                "worktree",
                "add",
                "--quiet",
                "-b",
                "test/governed-sha256",
                worktree);
            var expectedHead = RunGitOutput(worktree, "rev-parse", "HEAD").Trim();

            using var isolation =
                CopilotReasoningHost.GovernedGitIsolationScope.Create(worktree);
            var actual = await RunGitProcessAsync(
                worktree,
                isolation.EnvironmentVariables,
                "rev-parse",
                "HEAD");

            Assert.True(actual.ExitCode == 0, actual.CombinedOutput);
            Assert.Equal(expectedHead, actual.StandardOutput.Trim());
            Assert.Equal(64, expectedHead.Length);
        }
        finally
        {
            if (Directory.Exists(source))
            {
                _ = TryRunGit(
                    source,
                    "worktree",
                    "remove",
                    "--force",
                    worktree);
            }
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(
                             root,
                             "*",
                             SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void RunGit(
        string workingDirectory,
        params string[] arguments)
    {
        var exitCode = TryRunGit(workingDirectory, arguments);
        Assert.Equal(0, exitCode);
    }

    private static string RunGitOutput(
        string workingDirectory,
        params string[] arguments)
    {
        using var process = StartGit(workingDirectory, arguments);
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    private static int TryRunGit(
        string workingDirectory,
        params string[] arguments)
    {
        using var process = StartGit(workingDirectory, arguments);
        _ = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    private static Task<ProcessResult> RunGitProcessAsync(
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environmentVariables,
        params string[] arguments) =>
        new ProcessRunner().RunAsync(
            "git",
            arguments,
            workingDirectory,
            TimeSpan.FromSeconds(20),
            environmentVariables: environmentVariables);

    private static Task<ProcessResult> RunShellCommandAsync(
        string workingDirectory,
        string command,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        if (OperatingSystem.IsWindows())
        {
            return new ProcessRunner().RunAsync(
                Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                ["/d", "/c", command],
                workingDirectory,
                TimeSpan.FromSeconds(20),
                environmentVariables: environmentVariables);
        }

        return new ProcessRunner().RunAsync(
            "/bin/sh",
            ["-lc", command],
            workingDirectory,
            TimeSpan.FromSeconds(20),
            environmentVariables: environmentVariables);
    }

    private static Process StartGit(
        string workingDirectory,
        IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        return Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git.");
    }
}

public sealed class CopilotCliRuntimeTests
{
    [Fact]
    public async Task RefreshAsync_ValidatesTheProgrammaticCliContract()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-copilot-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var command = CreateCliShim(
                root,
                "--add-dir --acp --agent --allow-all-tools --allow-tool --available-tools --disable-builtin-mcps --deny-tool --deny-url --disallow-temp-dir " +
                "--effort --model --no-ask-user --no-custom-instructions " +
                "--no-eager-powershell-resolution --output-format --secret-env-vars --session-id");
            var status = await CreateRuntime(root).RefreshAsync(command);

            Assert.True(status.Ready);
            Assert.Equal("9.8.7", status.Version);
            Assert.True(
                string.Equals(
                    command,
                    status.ResolvedPath,
                    StringComparison.OrdinalIgnoreCase),
                $"Expected '{command}', resolved '{status.ResolvedPath}'.");
            Assert.Contains("non-interactive JSON execution", status.Detail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshAsync_RejectsACliWithoutJsonOutputSupport()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-copilot-capabilities-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var command = CreateCliShim(
                root,
                "--agent --allow-all-tools --model --no-ask-user");
            var status = await CreateRuntime(root).RefreshAsync(command);

            Assert.False(status.Ready);
            Assert.Contains("--output-format", status.Detail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshAsync_RejectsACliWithoutExplicitDirectoryGrants()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-copilot-path-capability-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var command = CreateCliShim(
                root,
                "--agent --allow-all-tools --allow-tool --available-tools --disable-builtin-mcps --deny-tool --deny-url --disallow-temp-dir " +
                "--effort --model --no-ask-user --no-custom-instructions " +
                "--no-eager-powershell-resolution --output-format --secret-env-vars --session-id");
            var status = await CreateRuntime(root).RefreshAsync(command);

            Assert.False(status.Ready);
            Assert.Contains("--add-dir", status.Detail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshAsync_ReportsInstallationGuidanceWhenCliIsMissing()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-copilot-missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var status = await CreateRuntime(root).RefreshAsync(
                Path.Combine(root, "missing-copilot"));

            Assert.False(status.Ready);
            Assert.Empty(status.ResolvedPath);
            Assert.Contains("winget install GitHub.Copilot", status.Detail);
            Assert.Contains("npm install -g @github/copilot", status.Detail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CopilotCliRuntime CreateRuntime(string root) =>
        new(
            new ProcessRunner(),
            new HarnessPaths(
                root,
                Path.Combine(root, ".github", "agents"),
                Path.Combine(root, "harness.db")),
            TimeProvider.System,
            NullLogger<CopilotCliRuntime>.Instance);

    private static string CreateCliShim(string root, string helpOutput)
    {
        var command = Path.Combine(root, "copilot.cmd");
        File.WriteAllText(command, "@echo off\r\nexit /b 1\r\n");
        File.WriteAllText(
            Path.Combine(root, "copilot.ps1"),
            $$"""
              if ($args[0] -eq '--version') {
                  Write-Output 'GitHub Copilot CLI 9.8.7'
                  exit 0
              }
              if ($args[0] -eq 'help') {
                  Write-Output '{{helpOutput}}'
                  exit 0
              }
              exit 7
              """);
        return command;
    }
}

public sealed class LocalRequestGuardTests
{
    [Fact]
    public void IsValid_RequiresTheExactNonSimpleHeader()
    {
        var context = new DefaultHttpContext();

        Assert.False(LocalRequestGuard.IsValid(context.Request));

        context.Request.Headers[LocalRequestGuard.HeaderName] =
            LocalRequestGuard.HeaderValue;

        Assert.True(LocalRequestGuard.IsValid(context.Request));
    }
}

public sealed class RepositoryContextGateTests
{
    [Fact]
    public async Task Writer_WaitsForExistingReaderAndThenAcquiresExclusively()
    {
        var gate = new RepositoryContextGate();
        using var reader = await gate.EnterReadAsync();
        var writerTask = gate.EnterWriteAsync();

        await Task.Delay(40);
        Assert.False(writerTask.IsCompleted);

        reader.Dispose();
        using var writer = await writerTask.WaitAsync(TimeSpan.FromSeconds(2));
        var secondReaderTask = gate.EnterReadAsync();
        await Task.Delay(40);
        Assert.False(secondReaderTask.IsCompleted);

        writer.Dispose();
        using var secondReader = await secondReaderTask.WaitAsync(TimeSpan.FromSeconds(2));
    }
}

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task WorkspaceProcessCleaner_StopsAWorkspaceServerAndReportsItsPort()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var workspace = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-process-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        var portFile = Path.Combine(workspace, "port.txt");
        var scriptFile = Path.Combine(workspace, "server.ps1");
        await File.WriteAllTextAsync(
            scriptFile,
            """
            param([string]$PortFile)
            Set-Location -LiteralPath $PSScriptRoot
            $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $listener.Start()
            [IO.File]::WriteAllText($PortFile, [string]$listener.LocalEndpoint.Port)
            while ($true) { Start-Sleep -Seconds 1 }
            """);
        var executable = ExecutableLocator.Resolve("powershell", workspace)
            ?? throw new InvalidOperationException("PowerShell is required for this test.");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(scriptFile);
        process.StartInfo.ArgumentList.Add(portFile);

        try
        {
            Assert.True(process.Start());
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (!File.Exists(portFile) && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
            Assert.True(File.Exists(portFile));
            var port = int.Parse(await File.ReadAllTextAsync(portFile));
            var cleaner = new WorkspaceProcessCleaner(
                new ProcessRunner(),
                NullLogger<WorkspaceProcessCleaner>.Instance);

            var result = await cleaner.StopAsync(workspace);

            Assert.Contains(process.Id, result.ProcessIds);
            Assert.Contains(port, result.ListeningPorts);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void WorkspaceOwnership_MatchesWorkingDirectoryOrCommandLine()
    {
        var workspace = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "flow-workspace"));

        Assert.True(WorkspaceProcessCleaner.IsWorkspaceOwned(
            workspace,
            Path.Combine(workspace, "site"),
            string.Empty,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal));
        Assert.True(WorkspaceProcessCleaner.IsWorkspaceOwned(
            workspace,
            Path.GetTempPath(),
            $"node {Path.Combine(workspace, "site", "server.js")}",
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal));
        Assert.False(WorkspaceProcessCleaner.IsWorkspaceOwned(
            workspace,
            Path.GetTempPath(),
            "node unrelated.js",
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_SkipsInteractiveCopilotBootstrapperForLatestManagedCli()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-copilot-locator-{Guid.NewGuid():N}");
        var shimDirectory = Path.Combine(root, "interactive-shim");
        var managedRoot = Path.Combine(root, "managed-cli");
        var olderExecutable = Path.Combine(managedRoot, "1.0.79-9", "copilot.exe");
        var latestExecutable = Path.Combine(managedRoot, "1.0.80", "copilot.exe");
        Directory.CreateDirectory(shimDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(olderExecutable)!);
        Directory.CreateDirectory(Path.GetDirectoryName(latestExecutable)!);

        try
        {
            File.WriteAllText(
                Path.Combine(shimDirectory, "copilot.bat"),
                "@echo off\r\npwsh -File copilot.ps1 %*\r\n");
            File.WriteAllText(
                Path.Combine(shimDirectory, "copilot.ps1"),
                "$answer = Read-Host \"Install GitHub Copilot CLI? (y/N)\"");
            File.WriteAllText(olderExecutable, string.Empty);
            File.WriteAllText(latestExecutable, string.Empty);

            var resolved = ExecutableLocator.Resolve(
                "copilot",
                root,
                shimDirectory,
                managedRoot);

            Assert.Equal(latestExecutable, resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_PreservesNonInteractiveNpmCopilotShim()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-copilot-shim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var command = Path.Combine(root, "copilot.cmd");
        var powerShellShim = Path.Combine(root, "copilot.ps1");

        try
        {
            File.WriteAllText(command, "@echo off\r\nnode copilot.js %*\r\n");
            File.WriteAllText(powerShellShim, "& node copilot.js @args\r\n");

            var resolved = ExecutableLocator.Resolve(
                "copilot",
                root,
                root,
                managedCopilotRoot: null);

            Assert.True(
                string.Equals(command, resolved, StringComparison.OrdinalIgnoreCase),
                $"Expected '{command}', resolved '{resolved}'.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_LaunchesThePlatformCommandWithoutLosingArguments()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var command = Path.Combine(directory, "portable-command");
            if (OperatingSystem.IsWindows())
            {
                await File.WriteAllTextAsync(
                    command,
                    "#!/bin/sh\nexit 92\n");
                await File.WriteAllTextAsync(
                    $"{command}.cmd",
                    "@echo off\r\nexit /b 91\r\n");
                await File.WriteAllTextAsync(
                    $"{command}.ps1",
                    "Write-Output ($args[0] + '|' + $args[1])\r\n");
            }
            else
            {
                await File.WriteAllTextAsync(
                    command,
                    "#!/bin/sh\nprintf '%s|%s\\n' \"$1\" \"$2\"\n");
                File.SetUnixFileMode(
                    command,
                    File.GetUnixFileMode(command) |
                    UnixFileMode.UserExecute);
            }

            var longArgument = new string('x', 9_000) + "&%";
            var result = await new ProcessRunner().RunAsync(
                command,
                [longArgument, "two words"],
                directory,
                TimeSpan.FromSeconds(20));

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(
                $"{longArgument}|two words",
                result.StandardOutput.Trim());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_PassesEnvironmentOverridesToTheChildProcess()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-process-environment-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var command = Path.Combine(directory, "environment-command");
            if (OperatingSystem.IsWindows())
            {
                await File.WriteAllTextAsync(
                    $"{command}.cmd",
                    "@echo off\r\nexit /b 1\r\n");
                await File.WriteAllTextAsync(
                    $"{command}.ps1",
                    "Write-Output $env:AI_HARNESS_PROCESS_TEST\r\n");
            }
            else
            {
                await File.WriteAllTextAsync(
                    command,
                    "#!/bin/sh\nprintf '%s\\n' \"$AI_HARNESS_PROCESS_TEST\"\n");
                File.SetUnixFileMode(
                    command,
                    File.GetUnixFileMode(command) |
                    UnixFileMode.UserExecute);
            }

            var result = await new ProcessRunner().RunAsync(
                command,
                [],
                directory,
                TimeSpan.FromSeconds(20),
                environmentVariables: new Dictionary<string, string?>
                {
                    ["AI_HARNESS_PROCESS_TEST"] = "isolated"
                });

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("isolated", result.StandardOutput.Trim());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_RemovesInheritedEnvironmentVariables()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-process-environment-remove-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var variableName = $"AI_HARNESS_REMOVE_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variableName, "sensitive");

        try
        {
            var command = Path.Combine(directory, "environment-command");
            if (OperatingSystem.IsWindows())
            {
                await File.WriteAllTextAsync(
                    $"{command}.cmd",
                    "@echo off\r\nexit /b 1\r\n");
                await File.WriteAllTextAsync(
                    $"{command}.ps1",
                    $"Write-Output $env:{variableName}\r\n");
            }
            else
            {
                await File.WriteAllTextAsync(
                    command,
                    $"#!/bin/sh\nprintf '%s\\n' \"${variableName}\"\n");
                File.SetUnixFileMode(
                    command,
                    File.GetUnixFileMode(command) |
                    UnixFileMode.UserExecute);
            }

            var result = await new ProcessRunner().RunAsync(
                command,
                [],
                directory,
                TimeSpan.FromSeconds(20),
                environmentVariables: new Dictionary<string, string?>
                {
                    [variableName] = null
                });

            Assert.Equal(0, result.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(result.StandardOutput));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_RejectsAnUnpairedWindowsBatchLauncher()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var command = Path.Combine(directory, "unsafe-command");
        await File.WriteAllTextAsync(
            $"{command}.cmd",
            "@echo off\r\necho %1\r\n");

        try
        {
            var exception = await Assert.ThrowsAsync<Win32Exception>(
                () => new ProcessRunner().RunAsync(
                    command,
                    ["%PATH%"],
                    directory,
                    TimeSpan.FromSeconds(20)));

            Assert.Contains("no runnable PowerShell companion", exception.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed class RepositoryBrowserTests
{
    [Fact]
    public void ListLocations_ReturnsExistingPortableEntryPoints()
    {
        var locations = RepositoryAnalyzer.ListLocations();
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var root = Path.GetPathRoot(Environment.CurrentDirectory)
                   ?? Path.DirectorySeparatorChar.ToString();

        Assert.NotEmpty(locations);
        Assert.All(locations, item => Assert.True(Directory.Exists(item.Path)));
        Assert.Contains(
            locations,
            item => string.Equals(item.Path, root, comparison));

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Directory.Exists(home))
        {
            Assert.Contains(
                locations,
                item => string.Equals(item.Path, home, comparison));
        }
    }

    [Fact]
    public void FindGitRepositories_TreatsNestedRepositoriesAsOneProject()
    {
        var project = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-project-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(Path.Combine(project, "site", ".git"));
            Directory.CreateDirectory(Path.Combine(project, "services", "data", ".git"));
            Directory.CreateDirectory(Path.Combine(project, "node_modules", "ignored", ".git"));

            var repositories = RepositoryAnalyzer.FindGitRepositories(project);

            Assert.Equal(
                ["services\\data", "site"],
                repositories
                    .Select(path => Path.GetRelativePath(project, path).Replace('/', '\\'))
                    .ToArray());
            Assert.True(RepositoryAnalyzer.IsProjectDirectory(project));
            Assert.False(RepositoryAnalyzer.IsGitRepository(project));
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }
}

public sealed class WorkspaceManagerTests
{
    [Fact]
    public async Task PrepareAsync_CreatesAFlowWorktreeForEveryProjectRepository()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-workspace-{Guid.NewGuid():N}");
        var project = Path.Combine(root, "project");
        var workspaceRoot = Path.Combine(root, "workspaces");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(
            Path.Combine(root, "WORKFLOW.md"),
            """
            ---
            workspace:
              root: workspaces
            ---

            Test workflow.
            """);
        await File.WriteAllTextAsync(
            Path.Combine(project, "project-notes.txt"),
            "Shared project context.");

        var processRunner = new ProcessRunner();
        await CreateRepositoryAsync(
            processRunner,
            Path.Combine(project, "site"),
            "site.txt");
        await CreateRepositoryAsync(
            processRunner,
            Path.Combine(project, "data"),
            "data.txt");

        var paths = new HarnessPaths(
            root,
            Path.Combine(root, ".github", "agents"),
            Path.Combine(root, "harness.db"));
        var workflowProvider = new WorkflowDefinitionProvider(
            paths,
            new WorkflowLoader(),
            NullLogger<WorkflowDefinitionProvider>.Instance);
        await workflowProvider.StartAsync(CancellationToken.None);
        var hookRunner = new WorkspaceHookRunner(
            workflowProvider,
            processRunner,
            NullLogger<WorkspaceHookRunner>.Instance);
        var manager = new WorkspaceManager(
            processRunner,
            workflowProvider,
            hookRunner,
            NullLogger<WorkspaceManager>.Instance);
        var flow = new FlowRun
        {
            Title = "Update the project",
            OriginalRequest = "Update the project",
            RepositoryPath = project
        };

        try
        {
            var workspace = await manager.PrepareAsync(flow);

            Assert.True(workspace.CreatedNow);
            Assert.Equal(
                Path.Combine(workspaceRoot, flow.Id.ToString("N")[..16]),
                workspace.Path);
            Assert.True(File.Exists(Path.Combine(workspace.Path, "project-notes.txt")));
            Assert.True(File.Exists(Path.Combine(workspace.Path, "site", "site.txt")));
            Assert.True(File.Exists(Path.Combine(workspace.Path, "data", "data.txt")));
            Assert.Equal(
                workspace.BranchName,
                await CurrentBranchAsync(processRunner, Path.Combine(workspace.Path, "site")));
            Assert.Equal(
                workspace.BranchName,
                await CurrentBranchAsync(processRunner, Path.Combine(workspace.Path, "data")));

            var recovered = await manager.PrepareAsync(flow);
            Assert.False(recovered.CreatedNow);
            Assert.Equal(workspace.Path, recovered.Path);

            flow.WorkspacePath = workspace.Path;
            flow.BranchName = workspace.BranchName;
            var cleanup = await manager.RemoveAsync(flow);

            Assert.Equal(2, cleanup.WorktreesRemoved);
            Assert.Equal(2, cleanup.LocalBranchesDeleted);
            Assert.Equal(0, cleanup.RemoteBranchesDeleted);
            Assert.False(Directory.Exists(workspace.Path));
            foreach (var repositoryName in new[] { "site", "data" })
            {
                var repositoryPath = Path.Combine(project, repositoryName);
                var branch = await RunGitAsync(
                    processRunner,
                    repositoryPath,
                    ["branch", "--list", workspace.BranchName]);
                Assert.True(string.IsNullOrWhiteSpace(branch.StandardOutput));
            }
        }
        finally
        {
            workflowProvider.Dispose();
            foreach (var repositoryName in new[] { "site", "data" })
            {
                var repositoryPath = Path.Combine(project, repositoryName);
                var repositoryWorkspace = Path.Combine(
                    workspaceRoot,
                    flow.Id.ToString("N")[..16],
                    repositoryName);
                if (Directory.Exists(repositoryPath) &&
                    Directory.Exists(repositoryWorkspace))
                {
                    await RunGitAsync(
                        processRunner,
                        repositoryPath,
                        ["worktree", "remove", "--force", repositoryWorkspace]);
                }
            }
            DeleteDirectory(root);
        }
    }

    private static async Task CreateRepositoryAsync(
        ProcessRunner processRunner,
        string repositoryPath,
        string fileName)
    {
        Directory.CreateDirectory(repositoryPath);
        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, fileName),
            "initial");
        await RunGitAsync(
            processRunner,
            repositoryPath,
            ["init", "--quiet"]);
        await RunGitAsync(
            processRunner,
            repositoryPath,
            ["add", "."]);
        await RunGitAsync(
            processRunner,
            repositoryPath,
            [
                "-c", "user.name=AI Harness Tests",
                "-c", "user.email=ai-harness@example.invalid",
                "commit", "--quiet", "-m", "Initial commit"
            ]);
    }

    private static async Task<string> CurrentBranchAsync(
        ProcessRunner processRunner,
        string repositoryPath)
    {
        var result = await RunGitAsync(
            processRunner,
            repositoryPath,
            ["branch", "--show-current"]);
        return result.StandardOutput.Trim();
    }

    private static async Task<ProcessResult> RunGitAsync(
        ProcessRunner processRunner,
        string repositoryPath,
        IReadOnlyList<string> arguments)
    {
        var result = await processRunner.RunAsync(
            "git",
            ["-C", repositoryPath, .. arguments],
            repositoryPath,
            TimeSpan.FromSeconds(30));
        Assert.True(result.ExitCode == 0, result.CombinedOutput);
        return result;
    }

    private static void DeleteDirectory(string path)
    {
        foreach (var file in Directory.EnumerateFiles(
                     path,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(path, recursive: true);
    }
}

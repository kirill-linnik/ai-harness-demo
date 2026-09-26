using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using AiHarnessDemo.Api;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Security;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Data;
using AiHarnessDemo.Infrastructure;
using AiHarnessDemo.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Immutable;

namespace AiHarnessDemo.Tests;

public sealed class PermissionProfileResolverTests
{
    private readonly PermissionProfileResolver _resolver = new();

    [Fact]
    public void UnknownWorkflowProfile_FailsClosed()
    {
        var restrictions = Restrictions() with
        {
            DeliveryPreReviewMaximum = (ExecutionPermissionProfile)999
        };

        Assert.Throws<InvalidOperationException>(() => _resolver.Resolve(
            Request(PlanDuty.Implement),
            restrictions));
    }

    [Theory]
    [InlineData("architect")]
    [InlineData("anything-at-all")]
    [InlineData("release-engineer")]
    public void EveryNonPublishAgentId_GetsPublicationGuards(string agentId)
    {
        var permission = _resolver.Resolve(
            Request(PlanDuty.Implement),
            Restrictions());
        var arguments = CopilotReasoningHost.BuildCliArguments(
            @"C:\workspace",
            @"C:\agent",
            agentId,
            "model",
            "high",
            Guid.NewGuid(),
            "prompt",
            permission);

        Assert.True(permission.GuardPublicationCredentials);
        Assert.Contains("shell(git push)", permission.DeniedTools);
        Assert.Contains("--deny-url=github.com", arguments);
        Assert.DoesNotContain("--allow-all-tools", arguments);
    }

    [Fact]
    public void PermissionResolution_HasNoAgentIdentityAuthorizationInput()
    {
        Assert.DoesNotContain(
            typeof(PermissionResolutionRequest).GetProperties(),
            property => property.Name.Contains(
                "Agent",
                StringComparison.OrdinalIgnoreCase));

        var intake = _resolver.Resolve(
            Request(PlanDuty.Analyze) with
            {
                InvocationKind = ExecutionInvocationKind.Intake
            },
            Restrictions());
        var planning = _resolver.Resolve(
            Request(PlanDuty.Analyze, PlanDuty.Design) with
            {
                InvocationKind = ExecutionInvocationKind.Planning
            },
            Restrictions());

        Assert.Equal(
            ExecutionPermissionProfile.ReadOnlySource,
            intake.Profile);
        Assert.Equal(
            ExecutionPermissionProfile.ReadOnlySource,
            planning.Profile);
        Assert.True(PermissionProfileResolver.Equivalent(
            intake,
            _resolver.Resolve(
                Request(PlanDuty.Analyze) with
                {
                    InvocationKind = ExecutionInvocationKind.Intake
                },
                Restrictions())));
    }

    [Fact]
    public void AdvisoryAndPreMortem_DenyWriteAndShell()
    {
        var advisory = _resolver.Resolve(
            Request(PlanDuty.Analyze) with { FlowKind = FlowKind.Advisory },
            Restrictions());
        var preMortem = _resolver.Resolve(
            Request(PlanDuty.Analyze) with
            {
                InvocationKind = ExecutionInvocationKind.PreMortem
            },
            Restrictions());

        Assert.Equal(ExecutionPermissionProfile.ReadOnlySource, advisory.Profile);
        Assert.Equal(
            ExecutionPermissionProfile.PreMortemReadOnly,
            preMortem.Profile);
        Assert.All(
            new[] { advisory, preMortem },
            permission =>
            {
                Assert.Contains("write", permission.DeniedTools);
                Assert.Contains("shell", permission.DeniedTools);
            });
    }

    [Fact]
    public void PreMortemFindingRevision_RemainsWorkerWriteWhileScepticIsReadOnly()
    {
        var implementationRevision = _resolver.Resolve(
            Request(PlanDuty.Implement) with
            {
                InvocationKind = ExecutionInvocationKind.Worker
            },
            Restrictions());
        var sceptic = _resolver.Resolve(
            Request(PlanDuty.Analyze, PlanDuty.Verify) with
            {
                InvocationKind = ExecutionInvocationKind.PreMortem
            },
            Restrictions());

        Assert.Equal(
            ExecutionPermissionProfile.WorkspaceWrite,
            implementationRevision.Profile);
        Assert.Contains("edit", implementationRevision.AllowedTools);
        Assert.Equal(
            ExecutionPermissionProfile.PreMortemReadOnly,
            sceptic.Profile);
        Assert.DoesNotContain("write", sceptic.AllowedTools);
        Assert.Contains("write", sceptic.DeniedTools);
        Assert.Contains("shell", sceptic.DeniedTools);
    }

    [Fact]
    public void WorkflowRestrictions_CanOnlyLowerAndAddDenials()
    {
        var restrictions = Restrictions() with
        {
            DeliveryPostApprovalMaximum =
                ExecutionPermissionProfile.WorkspaceWrite,
            AdditionalDeniedTools = Restrictions().AdditionalDeniedTools.SetItem(
                ExecutionPermissionProfile.WorkspaceWrite,
                ["shell(dotnet publish:*)"])
        };
        var permission = _resolver.Resolve(
            Request(PlanDuty.Publish) with
            {
                InvocationKind = ExecutionInvocationKind.Publication,
                PlanStage = PlanStage.AfterApproval,
                DurableApproval = true,
                DurableReviewDecision = ReviewDecision.Accepted,
                IsOnlyPlannedPublishStep = true,
            },
            restrictions);

        Assert.Equal(
            ExecutionPermissionProfile.WorkspaceWrite,
            permission.Profile);
        Assert.False(permission.AllowRemotePublication);
        Assert.Contains("shell(dotnet publish:*)", permission.DeniedTools);
        Assert.Contains("shell(git push)", permission.DeniedTools);
        Assert.Contains("write", permission.DeniedTools);
        Assert.DoesNotContain("create", permission.AllowedTools);
        Assert.DoesNotContain("edit", permission.AllowedTools);
        Assert.False(CopilotReasoningHost.ShouldRunWorkspaceHooks(
            ExecutionInvocationKind.Publication));
    }

    [Fact]
    public void Publish_RequiresDurableAcceptanceAndSolePublishDuty()
    {
        var candidate = Request(PlanDuty.Publish) with
        {
            InvocationKind = ExecutionInvocationKind.Publication,
            PlanStage = PlanStage.AfterApproval,
            IsOnlyPlannedPublishStep = true,
        };
        Assert.Throws<InvalidOperationException>(() =>
            _resolver.Resolve(candidate, Restrictions()));
        Assert.Throws<InvalidOperationException>(() =>
            _resolver.Resolve(
                candidate with
                {
                    DurableApproval = true,
                    DurableReviewDecision = ReviewDecision.Accepted,
                    PlanDuties = [PlanDuty.Publish, PlanDuty.Verify]
                },
                Restrictions()));

        var permission = _resolver.Resolve(
            candidate with
            {
                DurableApproval = true,
                DurableReviewDecision = ReviewDecision.Accepted
            },
            Restrictions());

        Assert.Equal(ExecutionPermissionProfile.Publish, permission.Profile);
        Assert.True(permission.AllowRemotePublication);
        Assert.True(permission.GuardPublicationCredentials);
        Assert.DoesNotContain("create", permission.AllowedTools);
        Assert.DoesNotContain("edit", permission.AllowedTools);
        Assert.Contains(
            OperatingSystem.IsWindows() ? "powershell" : "bash",
            permission.AllowedTools);
        Assert.Contains("write", permission.DeniedTools);
        Assert.Contains("shell(git commit)", permission.DeniedTools);
        Assert.Contains("shell(git push)", permission.DeniedTools);

        var arguments = CopilotReasoningHost.BuildCliArguments(
            @"C:\workspace",
            @"C:\agent",
            "publisher",
            "model",
            "high",
            Guid.NewGuid(),
            "Publish the sealed candidate.",
            permission);
        Assert.Contains("--allow-tool=shell", arguments);
        Assert.DoesNotContain("--allow-tool=write", arguments);
        Assert.Contains("--deny-tool=write", arguments);
        Assert.Contains("--deny-tool=shell(git commit)", arguments);
        Assert.Contains("--deny-tool=shell(git push)", arguments);
        Assert.Contains("--no-remote", arguments);
        Assert.False(CopilotReasoningHost.ShouldRunWorkspaceHooks(
            ExecutionInvocationKind.Publication));
    }

    [Fact]
    public void NonPublishEnvironment_UsesAnEmptyPerAttemptGhDirectory()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"permission-guard-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "copilot");
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        string configDirectory;
        try
        {
            using (var scope =
                   CopilotReasoningHost.PublicationGuardScope.Create(
                       home,
                       workspace))
            {
                configDirectory = Assert.IsType<string>(
                    scope.EnvironmentVariables["GH_CONFIG_DIR"]);
                Assert.True(Directory.Exists(configDirectory));
                Assert.Empty(Directory.GetFileSystemEntries(configDirectory));
                Assert.Null(scope.EnvironmentVariables["GH_TOKEN"]);
                Assert.Null(scope.EnvironmentVariables["GITHUB_TOKEN"]);
                Assert.Null(scope.EnvironmentVariables["SSH_AUTH_SOCK"]);
                Assert.Null(scope.EnvironmentVariables["GIT_ASKPASS"]);
                Assert.Equal(
                    string.Empty,
                    scope.EnvironmentVariables["GIT_CONFIG_VALUE_1"]);
            }

            Assert.False(Directory.Exists(configDirectory));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WorkerPreMortemRevision_PersistsWorkspaceWriteAtRuntimeBoundary()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite(connection)
            .Options;
        IDbContextFactory<HarnessDbContext> factory =
            new PermissionDbContextFactory(options);
        var flow = new FlowRun
        {
            Title = "Permission persistence",
            OriginalRequest = "Persist policy",
            Kind = FlowKind.Delivery
        };
        var step = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = 1,
            Sequence = 10,
            AgentId = "implementation-worker",
            AgentName = "Implementation Worker",
            AgentRole = "worker",
            PlanStepKey = "implement",
            PlanDutiesJson = """["Implement"]""",
            InvocationKind = ExecutionInvocationKind.Worker,
            PreMortemReviewStepId = Guid.NewGuid()
        };
        await using (var database = await factory.CreateDbContextAsync())
        {
            await database.Database.EnsureCreatedAsync();
            database.Flows.Add(flow);
            database.FlowSteps.Add(step);
            await database.SaveChangesAsync();
        }
        var workflow = new WorkflowDefinition(
            new WorkflowConfig(),
            "{{ task }}",
            "WORKFLOW.md",
            DateTimeOffset.UtcNow,
            "revision-under-test");
        var context = new AgentExecutionContext(
            flow.Id,
            1,
            step.AgentId,
            step.AgentName,
            step.AgentRole,
            "model",
            "high",
            1,
            "task",
            "knowledge",
            @"C:\source",
            @"C:\workspace",
            Guid.NewGuid(),
            OutcomeType.PullRequest,
            "plan",
            [],
            [],
            FlowStepId: step.Id,
            InvocationKind: ExecutionInvocationKind.Worker);

        var permission =
            await CopilotReasoningHost.ResolveAndPersistPermissionAsync(
                context,
                workflow,
                _resolver,
                factory,
                CancellationToken.None);

        await using var verification = await factory.CreateDbContextAsync();
        var persisted = await verification.FlowSteps.SingleAsync();
        Assert.Equal(ExecutionPermissionProfile.WorkspaceWrite, permission.Profile);
        Assert.Equal(permission.Profile, persisted.PermissionProfile);
        Assert.Equal("revision-under-test", persisted.WorkflowRevision);
        Assert.Contains("\"Profile\":1", persisted.EffectivePermissionJson);
    }

    private static PermissionResolutionRequest Request(params PlanDuty[] duties) =>
        new(
            FlowKind.Delivery,
            ExecutionInvocationKind.Worker,
            PlanStage.BeforeReview,
            duties.ToImmutableArray(),
            null,
            false,
            false);

    private static WorkflowPermissionRestrictions Restrictions() =>
        new(
            ExecutionPermissionProfile.ReadOnlySource,
            ExecutionPermissionProfile.WorkspaceWrite,
            ExecutionPermissionProfile.Publish,
            Enum.GetValues<ExecutionPermissionProfile>()
                .ToImmutableDictionary(
                    profile => profile,
                    _ => ImmutableArray<string>.Empty));

    private sealed class PermissionDbContextFactory(
        DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);
    }
}

public sealed class PromptStagingTests
{
    [Fact]
    public async Task StagedManifest_PublishesAndCleansTheSessionScopedCliAgent()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"agent-manifest-staging-{Guid.NewGuid():N}");
        var sessionId = Guid.NewGuid();
        try
        {
            var staged = await new AgentManifestStager().StageAsync(
                root,
                Manifest(),
                sessionId);
            var manifestPath = Assert.Single(
                Directory.GetFiles(
                    Path.Combine(staged.Root, ".github", "agents"),
                    "*.agent.md"));
            var publishedPath = Path.Combine(
                root,
                "agents",
                $"{staged.AgentId}.agent.md");
            var content = await File.ReadAllTextAsync(manifestPath);

            Assert.True(File.Exists(publishedPath));
            Assert.Equal(
                content,
                await File.ReadAllTextAsync(publishedPath));
            Assert.Contains(
                $"name: \"Account Manager\"{Environment.NewLine}",
                content,
                StringComparison.Ordinal);

            Assert.True(
                new AgentManifestStager().CleanupSessionRoot(
                    root,
                    sessionId,
                    staged.Root));
            Assert.False(File.Exists(publishedPath));
            Assert.False(Directory.Exists(staged.Root));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LargePrompt_IsStagedAndCliContainsOnlyBoundedReference()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"prompt-staging-{Guid.NewGuid():N}");
        var sessionId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var prompt =
            "prompt-start:" +
            new string('a', 20_000) +
            ":prompt-middle:" +
            new string('b', 20_000) +
            ":prompt-last";
        try
        {
            var stager = new AgentManifestStager();
            var agent = await stager.StageAsync(
                root,
                Manifest(),
                sessionId);
            var staged = await stager.StagePromptAsync(
                agent,
                prompt,
                sessionId,
                flowId,
                stepId,
                attempt: 1);
            var instruction =
                AgentManifestStager.BuildPromptReferenceInstruction(
                    staged,
                    recoveringInterruptedSession: false);
            var arguments = CopilotReasoningHost.BuildCliArguments(
                root,
                agent.Root,
                agent.AgentId,
                ExecutionInvocationKind.Planning,
                "model",
                "high",
                sessionId,
                instruction);

            Assert.Equal(prompt, await File.ReadAllTextAsync(staged.Path));
            Assert.Equal(
                OutcomeVerificationRules.ComputeSha256(prompt),
                staged.Sha256);
            Assert.Equal(
                System.Text.Encoding.UTF8.GetByteCount(prompt),
                staged.ByteCount);
            Assert.Equal("-p", arguments[^2]);
            Assert.Equal(instruction, arguments[^1]);
            Assert.DoesNotContain(
                arguments,
                value => value.Contains(
                    "prompt-middle",
                    StringComparison.Ordinal));
            Assert.DoesNotContain(
                arguments,
                value => value.Length >
                         CopilotReasoningHost.MaximumInlinePromptCharacters);
            Assert.Contains(
                arguments,
                value => value.StartsWith(
                    "--deny-tool=write(",
                    StringComparison.Ordinal) &&
                         value.Contains(
                             Path.GetFullPath(agent.Root)
                                 .Replace('\\', '/'),
                             StringComparison.Ordinal));
            Assert.True(
                CopilotReasoningHost.EstimateCliCommandLineCharacters(
                    "copilot",
                    arguments) <
                CopilotReasoningHost.MaximumProcessCommandLineCharacters);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StagedPrompt_TamperingFailsClosed()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"prompt-tamper-{Guid.NewGuid():N}");
        var sessionId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        const string prompt = "complete authoritative prompt";
        try
        {
            var stager = new AgentManifestStager();
            var agent = await stager.StageAsync(
                root,
                Manifest(),
                sessionId);
            var staged = await stager.StagePromptAsync(
                agent,
                prompt,
                sessionId,
                flowId,
                stepId,
                attempt: 1);
            await File.WriteAllTextAsync(
                staged.Path,
                "tampered prompt");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => stager.StagePromptAsync(
                    agent,
                    prompt,
                    sessionId,
                    flowId,
                    stepId,
                    attempt: 1));

            Assert.Contains(
                "SHA-256 validation",
                exception.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StagedPrompt_ResumeValidatesAndReusesDeterministicBundle()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"prompt-resume-{Guid.NewGuid():N}");
        var sessionId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var prompt = new string('r', 40_000);
        try
        {
            var stager = new AgentManifestStager();
            var agent = await stager.StageAsync(
                root,
                Manifest(),
                sessionId);
            var first = await stager.StagePromptAsync(
                agent,
                prompt,
                sessionId,
                flowId,
                stepId,
                attempt: 3);
            var resumed = await stager.StagePromptAsync(
                agent,
                prompt,
                sessionId,
                flowId,
                stepId,
                attempt: 3);
            var instruction =
                AgentManifestStager.BuildPromptReferenceInstruction(
                    resumed,
                    recoveringInterruptedSession: true);

            Assert.False(first.Reused);
            Assert.True(resumed.Reused);
            Assert.Equal(first.Path, resumed.Path);
            Assert.Equal(first.ManifestPath, resumed.ManifestPath);
            Assert.Equal(first.Sha256, resumed.Sha256);
            Assert.Contains(
                "Resume from the current workspace",
                instruction,
                StringComparison.Ordinal);
            Assert.Contains(resumed.Path, instruction, StringComparison.Ordinal);
            Assert.Contains(resumed.Sha256, instruction, StringComparison.Ordinal);

            stager.CleanupPrompt(resumed);
            Assert.False(Directory.Exists(resumed.BundleRoot));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void CliArguments_RejectAProductPayloadAboveInlineLimit()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CopilotReasoningHost.BuildCliArguments(
                @"C:\workspace",
                @"C:\agent",
                "account-manager",
                ExecutionInvocationKind.Worker,
                "model",
                "high",
                Guid.NewGuid(),
                new string('x',
                    CopilotReasoningHost.MaximumInlinePromptCharacters + 1)));

        Assert.Contains(
            "Stage the complete prompt",
            exception.Message,
            StringComparison.Ordinal);
    }

    private static AgentManifest Manifest() =>
        new(
            "account-manager",
            "Account Manager",
            "Normalizes customer intent.",
            "account-manager",
            "violet",
            10,
            "account-manager.agent.md",
            "Read the supplied prompt and return the strict contract.");
}

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
    public void ProgressReporter_FinishesOnlyOnTheTerminalResult()
    {
        var progress = new List<AgentRunProgress>();
        var reporter = CopilotJsonlParser.CreateProgressReporter(progress.Add);

        reporter(
            """{"type":"assistant.message","data":{"content":"I will inspect another file."}}""");
        reporter(
            """{"type":"tool.execution_start","data":{"toolCallId":"one","toolName":"view"}}""");
        reporter(
            """{"type":"tool.execution_complete","data":{"toolCallId":"one","success":true}}""");
        reporter("""{"type":"result","exitCode":0}""");

        Assert.Collection(
            progress,
            item =>
            {
                Assert.Equal(AgentRunPhase.StreamingTurn, item.Phase);
                Assert.Equal("Agent response received.", item.Activity);
            },
            item =>
            {
                Assert.Equal(AgentRunPhase.Finishing, item.Phase);
                Assert.Equal("Agent run completed.", item.Activity);
            });
    }

    [Fact]
    public void ProgressReporter_CollapsesToolChatterIntoOneStreamingTransition()
    {
        var progress = new List<AgentRunProgress>();
        var reporter = CopilotJsonlParser.CreateProgressReporter(progress.Add);

        reporter(
            """{"type":"tool.execution_start","data":{"toolCallId":"one","toolName":"grep"}}""");
        reporter(
            """{"type":"tool.execution_complete","data":{"toolCallId":"one","success":true}}""");
        reporter(
            """{"type":"tool.execution_start","data":{"toolCallId":"two","toolName":"powershell"}}""");
        reporter(
            """{"type":"tool.execution_complete","data":{"toolCallId":"two","success":true}}""");
        reporter("""{"type":"result","exitCode":0}""");

        Assert.Collection(
            progress,
            item =>
            {
                Assert.Equal(AgentRunPhase.StreamingTurn, item.Phase);
                Assert.Equal("Agent is using tools.", item.Activity);
            },
            item =>
            {
                Assert.Equal(AgentRunPhase.Finishing, item.Phase);
                Assert.Equal("Agent run completed.", item.Activity);
            });
    }

    [Fact]
    public void ProgressPersistence_RecordsOnlyPhaseTransitions()
    {
        var step = new FlowStep
        {
            FlowRunId = Guid.NewGuid(),
            AgentId = "quality-engineer",
            AgentName = "Quality Engineer",
            AgentRole = "quality-engineer",
            Phase = AgentRunPhase.StreamingTurn
        };

        var repeatedPhase = AgentProgressPersistence.Apply(
            step,
            new AgentRunProgress(
                AgentRunPhase.StreamingTurn,
                "Using powershell."));
        Assert.False(repeatedPhase.PhaseChanged);
        Assert.False(repeatedPhase.StateChanged);

        var finishing = AgentProgressPersistence.Apply(
            step,
            new AgentRunProgress(
                AgentRunPhase.Finishing,
                "Agent run completed."));
        Assert.True(finishing.PhaseChanged);
        Assert.True(finishing.StateChanged);
        Assert.Equal(AgentRunPhase.Finishing, step.Phase);
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
    [InlineData("**HANDOFF_STATUS: COMPLETE**\nDecision\nDone.", true)]
    [InlineData("`HANDOFF_STATUS: COMPLETE`\nDecision\nDone.", true)]
    [InlineData("## HANDOFF_STATUS: COMPLETE\nDecision\nDone.", true)]
    [InlineData("HANDOFF_STATUS: COMPLETE\nHANDOFF_STATUS: COMPLETE", false)]
    public void CompleteStatus_RequiresOneExplicitCompleteMarker(
        string output,
        bool expected)
    {
        Assert.Equal(expected, AgentHandoffInspector.HasCompleteStatus(output));
    }

    [Fact]
    public void DynamicStatus_AllowsPreambleWhenTheMarkerIsUniqueAndExact()
    {
        var status = AgentHandoffInspector.ParseDynamic(
            """
            Repository context inspected before planning.

            **HANDOFF_STATUS: COMPLETE**

            TEAM_PLAN_BEGIN
            {}
            TEAM_PLAN_END
            """);

        Assert.False(status.IsPushback);
        Assert.Null(status.OwnerPlanStepKey);
        Assert.Null(status.Reason);
    }

    [Fact]
    public void DynamicPushback_AllowsDetailedBoundedReason()
    {
        var reason = new string('r', 1_200);
        var status = AgentHandoffInspector.ParseDynamic(
            $"HANDOFF_STATUS: PUSHBACK{Environment.NewLine}" +
            $"PUSHBACK_OWNER_STEP_ID: build-preview{Environment.NewLine}" +
            $"PUSHBACK_REASON: {reason}");

        Assert.True(status.IsPushback);
        Assert.Equal("build-preview", status.OwnerPlanStepKey);
        Assert.Equal(reason, status.Reason);
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
                    ExecutionInvocationKind.Worker,
                    allowRemotePublication: false));
        var arguments = CopilotReasoningHost.BuildCliArguments(
            @"C:\governed-worktree",
            @"C:\harness",
            agentRole,
            ExecutionInvocationKind.Worker,
            "model",
            "high",
            Guid.NewGuid(),
            "Governed turn",
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
            "--secret-env-vars=COPILOT_GITHUB_TOKEN,GH_TOKEN,GITHUB_TOKEN,GH_ENTERPRISE_TOKEN,GITHUB_ENTERPRISE_TOKEN,GITHUB_TOKEN_REQUEST_URL,GITHUB_TOKEN_REQUEST_TOKEN,SSH_AUTH_SOCK,GIT_ASKPASS,SSH_ASKPASS",
            arguments);
        Assert.Contains("--disable-builtin-mcps", arguments);
        Assert.DoesNotContain("--allow-all-tools", arguments);
        Assert.DoesNotContain("--deny-tool=shell(git:*)", arguments);
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
            ExecutionInvocationKind.Worker,
            "model",
            "high",
            Guid.NewGuid(),
            "Prepare the host-controlled publication narrative.",
            isHostControlledPublication: true,
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
                Assert.True(isolation.GitBoundaryViolationDetected);
                Assert.NotEmpty(isolation.UnauthorizedMetadataMutationDetails);
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
    public async Task GovernedGitIsolation_PublicationInspectionCommandsRemainReadOnly()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"governed-git-publication-inspection-{Guid.NewGuid():N}");
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
                "test/publication-inspection",
                worktree);

            string[][] commands =
            [
                ["status", "--porcelain=v1", "--branch"],
                ["log", "--oneline", "-10"],
                ["diff", "--stat"],
                ["diff", "--cached", "--stat"],
                ["show", "--stat", "HEAD"],
                ["show", "HEAD"],
                ["remote", "-v"],
                ["branch", "--show-current"],
                ["rev-parse", "HEAD"],
                ["rev-parse", "HEAD^{tree}"],
                ["show", "-s", "--format=commit=%H%ntree=%T%nparent=%P", "HEAD"],
                ["status", "--porcelain", "--ignored"]
            ];
            var mutations = new List<string>();
            foreach (var command in commands)
            {
                using var isolation =
                    CopilotReasoningHost.GovernedGitIsolationScope.Create(
                        worktree);
                var result = await RunGitProcessAsync(
                    worktree,
                    isolation.EnvironmentVariables,
                    command);
                Assert.Equal(0, result.ExitCode);
                isolation.RestoreAndValidate();
                if (isolation.UnauthorizedMetadataMutationDetected)
                {
                    mutations.Add(string.Join(' ', command));
                }
            }

            Assert.Empty(mutations);

            using (var isolation =
                   CopilotReasoningHost.GovernedGitIsolationScope.Create(
                       worktree))
            {
                foreach (var command in commands)
                {
                    var result = await RunGitProcessAsync(
                        worktree,
                        isolation.EnvironmentVariables,
                        command);
                    Assert.Equal(0, result.ExitCode);
                }
                isolation.RestoreAndValidate();
                Assert.False(isolation.UnauthorizedMetadataMutationDetected);
            }
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
    public async Task GovernedGitIsolation_DiscardsShadowOnlyMutationWithoutBoundaryViolation()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"governed-git-shadow-only-{Guid.NewGuid():N}");
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
                "test/shadow-only",
                worktree);
            var realHead = RunGitOutput(worktree, "rev-parse", "HEAD").Trim();

            using (var isolation =
                   CopilotReasoningHost.GovernedGitIsolationScope.Create(
                       worktree))
            {
                var mutation = await RunGitProcessAsync(
                    worktree,
                    isolation.EnvironmentVariables,
                    "update-ref",
                    "refs/heads/disposable-shadow",
                    realHead);
                Assert.Equal(0, mutation.ExitCode);
                isolation.RestoreAndValidate();
                Assert.True(isolation.UnauthorizedMetadataMutationDetected);
                Assert.False(isolation.GitBoundaryViolationDetected);
                Assert.NotEmpty(isolation.UnauthorizedMetadataMutationDetails);
            }

            Assert.NotEqual(
                0,
                TryRunGit(
                    worktree,
                    "show-ref",
                    "--verify",
                    "refs/heads/disposable-shadow"));
            Assert.Equal(realHead, RunGitOutput(worktree, "rev-parse", "HEAD").Trim());
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
                "--add-dir --acp --agent --allow-tool --available-tools --disable-builtin-mcps --deny-tool --deny-url --disallow-temp-dir " +
                "--reasoning-effort --model --no-ask-user --no-custom-instructions " +
                "--no-eager-powershell-resolution --no-remote --no-remote-export --output-format --secret-env-vars --session-id");
            var status = await CreateRuntime(root).RefreshAsync(command);

            Assert.True(status.Ready);
            Assert.Equal("9.8.7", status.Version);
            Assert.Equal("--reasoning-effort", status.ReasoningEffortOption);
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
                "--agent --allow-tool --available-tools --disable-builtin-mcps --deny-tool --deny-url --disallow-temp-dir " +
                "--reasoning-effort --model --no-ask-user --no-custom-instructions " +
                "--no-eager-powershell-resolution --no-remote --no-remote-export --output-format --secret-env-vars --session-id");
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

    [Fact]
    public async Task OpaqueCandidateMutation_CannotReachPrivilegedLifecycleEndpoint()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path =
            $"/api/previews/{Guid.NewGuid():D}/artifacts/demo/demo/start";
        context.Request.Headers.Origin = "null";
        context.Response.Body = new MemoryStream();
        var reached = false;

        await LocalRequestGuard.ApplyAsync(
            context,
            _ =>
            {
                reached = true;
                return Task.CompletedTask;
            });

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task ExactDemoProxyRoute_AllowsOpaqueApplicationTraffic(
        string method)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path =
            $"/api/demos/{Guid.NewGuid():D}/api/items";
        context.Request.Headers.Origin = "null";
        var reached = false;

        await LocalRequestGuard.ApplyAsync(
            context,
            _ =>
            {
                reached = true;
                return Task.CompletedTask;
            });

        Assert.True(reached);
    }

    [Theory]
    [InlineData("/api/demos/not-a-guid/api/items")]
    [InlineData("/api/demos/00000000-0000-0000-0000-000000000000x/api/items")]
    [InlineData("/api/previews/00000000-0000-0000-0000-000000000000/artifacts/demo/demo/stop")]
    public async Task LookalikeAndLifecycleRoutes_RemainGuarded(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Delete;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        var reached = false;

        await LocalRequestGuard.ApplyAsync(
            context,
            _ =>
            {
                reached = true;
                return Task.CompletedTask;
            });

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
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
    public async Task BuildStudyInventoryAsync_IndexesReadmesWithoutCopyingTheirBodies()
    {
        var project = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-knowledge-{Guid.NewGuid():N}");
        var site = Path.Combine(project, "site");
        var data = Path.Combine(project, "data");
        var siteReadme = $"# Site{Environment.NewLine}{new string('s', 900)}SITE-README-END";
        var dataReadme = $"# Data{Environment.NewLine}{new string('d', 900)}DATA-README-END";

        try
        {
            Directory.CreateDirectory(Path.Combine(site, ".git"));
            Directory.CreateDirectory(Path.Combine(data, ".git"));
            await File.WriteAllTextAsync(
                Path.Combine(site, "README.md"),
                siteReadme);
            await File.WriteAllTextAsync(
                Path.Combine(data, "README.md"),
                dataReadme);
            await File.WriteAllTextAsync(
                Path.Combine(site, "package.json"),
                """{"dependencies":{"@angular/core":"22.0.0"}}""");

            var repositories = RepositoryAnalyzer.FindGitRepositories(project);
            var inventory = await RepositoryAnalyzer.BuildStudyInventoryAsync(
                project,
                repositories,
                CancellationToken.None);

            Assert.Contains(Path.Combine("site", "README.md"), inventory.Files);
            Assert.Contains(Path.Combine("data", "README.md"), inventory.Files);
            Assert.Contains(Path.Combine("site", "README.md"), inventory.Summary);
            Assert.Contains(Path.Combine("data", "README.md"), inventory.Summary);
            Assert.DoesNotContain("SITE-README-END", inventory.Summary);
            Assert.DoesNotContain("DATA-README-END", inventory.Summary);
            Assert.Contains("Angular", inventory.Summary);
            Assert.Equal(
                [Path.Combine("data"), Path.Combine("site")],
                inventory.RepositoryPaths);
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }

    [Fact]
    public void RepositoryStudyArguments_AreReadOnlyAndIgnoreRepositoryInstructions()
    {
        var sessionId = Guid.NewGuid();
        var arguments = RepositoryKnowledgeSynthesizer.BuildStudyArguments(
            Environment.CurrentDirectory,
            "Study the repository.",
            sessionId);

        Assert.Contains("--available-tools=view,grep,glob", arguments);
        Assert.Contains("--allow-tool=view,grep,glob", arguments);
        Assert.Contains("--deny-tool=write,shell", arguments);
        Assert.Contains("--no-custom-instructions", arguments);
        Assert.Contains("--disable-builtin-mcps", arguments);
        Assert.Contains("--no-remote", arguments);
        Assert.Contains("--no-remote-export", arguments);
        Assert.Contains("--session-id", arguments);
        Assert.Contains(sessionId.ToString("D"), arguments);
        Assert.DoesNotContain("--allow-all", arguments);
        Assert.DoesNotContain("--allow-all-tools", arguments);
    }

    [Fact]
    public async Task RepositoryStudy_SynthesizesEvidenceGroundedEditableKnowledge()
    {
        var fixture = await RepositoryStudyFixture.CreateAsync();
        try
        {
            var contract = fixture.ValidContract();
            var runner = new RepositoryStudyProcessRunner(contract);
            var synthesizer = new RepositoryKnowledgeSynthesizer(
                runner,
                NullLogger<RepositoryKnowledgeSynthesizer>.Instance);

            var knowledge = await synthesizer.SynthesizeAsync(
                "copilot",
                new AiHarnessDemo.Core.Workflow.CopilotConfig
                {
                    TurnTimeoutMs = 20_000,
                    MaximumQualityStallTimeoutMs = 10_000
                },
                fixture.Root,
                fixture.Inventory,
                CancellationToken.None);

            Assert.Contains($"# {Path.GetFileName(fixture.Root)}", knowledge);
            Assert.Contains("## Product and scope", knowledge);
            Assert.Contains("## Repository map", knowledge);
            Assert.Contains("serves the community sites", knowledge);
            Assert.Contains($"`{fixture.SiteSource}`", knowledge);
            Assert.DoesNotContain(fixture.ReadmeBody, knowledge);
            Assert.Contains("--no-custom-instructions", runner.Arguments);
            Assert.Contains("--deny-tool=write,shell", runner.Arguments);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task RepositoryStudy_CorrectsFindingThatUsesRepositoryOnlyPathFields()
    {
        var fixture = await RepositoryStudyFixture.CreateAsync();
        try
        {
            var invalid = fixture.FindingWithRepositoryFieldsContract();
            var shapeError =
                Assert.Throws<RepositoryKnowledgeContractException>(() =>
                    RepositoryKnowledgeSynthesizer.ParseAndRender(
                        invalid,
                        fixture.Root,
                        fixture.Inventory));
            Assert.Contains(
                shapeError.Errors,
                error =>
                    error.Contains(
                        "knowledge.ProductAndScope[0] property 'Path' is not allowed",
                        StringComparison.Ordinal) &&
                    error.Contains(
                        "Summary, Basis, Evidence",
                        StringComparison.Ordinal));

            var runner = new RepositoryStudyProcessRunner(
                invalid,
                fixture.ValidContract());
            var synthesizer = new RepositoryKnowledgeSynthesizer(
                runner,
                NullLogger<RepositoryKnowledgeSynthesizer>.Instance);

            var knowledge = await synthesizer.SynthesizeAsync(
                "copilot",
                new AiHarnessDemo.Core.Workflow.CopilotConfig
                {
                    TurnTimeoutMs = 20_000,
                    MaximumQualityStallTimeoutMs = 10_000
                },
                fixture.Root,
                fixture.Inventory,
                CancellationToken.None);

            Assert.Contains("serves the community sites", knowledge);
            Assert.Equal(2, runner.Prompts.Count);
            Assert.Contains(
                "Path and Purpose are valid only for objects in Repositories",
                runner.Prompts[1],
                StringComparison.Ordinal);
            Assert.Contains(
                "\"ProductAndScope\":[{\"Summary\"",
                runner.Prompts[1],
                StringComparison.Ordinal);
            Assert.Contains(
                shapeError.Errors[0],
                runner.Prompts[1],
                StringComparison.Ordinal);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task RepositoryStudy_RejectsReadmeOnlySynthesisWhenSourceAndTestsExist()
    {
        var fixture = await RepositoryStudyFixture.CreateAsync();
        try
        {
            var output = fixture.Contract(
                productEvidence: [fixture.SiteReadme],
                architectureEvidence: [fixture.SiteReadme],
                technologyEvidence: [fixture.SiteReadme],
                constraintEvidence: [fixture.SiteReadme],
                siteRepositoryEvidence: [fixture.SiteReadme],
                riskEvidence: [fixture.SiteReadme]);

            var exception = Assert.Throws<RepositoryKnowledgeContractException>(
                () => RepositoryKnowledgeSynthesizer.ParseAndRender(
                    output,
                    fixture.Root,
                    fixture.Inventory));

            Assert.Contains(
                exception.Errors,
                error => error.Contains("source file", StringComparison.Ordinal));
            Assert.Contains(
                exception.Errors,
                error => error.Contains("test file", StringComparison.Ordinal));
            Assert.Contains(
                exception.Errors,
                error => error.Contains("manifest or configuration", StringComparison.Ordinal));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task RepositoryKnowledgeRecap_ReplacesTheBaselineOnlyForDurableImpact()
    {
        var fixture = await RepositoryStudyFixture.CreateAsync();
        try
        {
            var changed = RepositoryKnowledgeSynthesizer.ParseAndRenderRecap(
                fixture.ChangedRecap(),
                fixture.Root,
                fixture.Inventory,
                Path.GetFileName(fixture.Root));
            var unchanged = RepositoryKnowledgeSynthesizer.ParseAndRenderRecap(
                fixture.UnchangedRecap(),
                fixture.Root,
                fixture.Inventory,
                Path.GetFileName(fixture.Root));

            Assert.True(changed.Changed);
            Assert.Contains("serves the community sites", changed.Knowledge);
            Assert.False(unchanged.Changed);
            Assert.Null(unchanged.Knowledge);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task RepositoryKnowledge_PreservesUserProvidedFactsWithoutInventedEvidence()
    {
        var fixture = await RepositoryStudyFixture.CreateAsync();
        try
        {
            var output = fixture.Contract(
                productEvidence: [fixture.SiteReadme, fixture.SiteSource],
                architectureEvidence: [fixture.SiteSource, fixture.DataReadme],
                technologyEvidence: [fixture.SiteManifest, fixture.SiteTest],
                constraintEvidence: [],
                constraintBasis:
                    RepositoryKnowledgeSynthesizer.UserProvidedBasis);

            var knowledge = RepositoryKnowledgeSynthesizer.ParseAndRender(
                output,
                fixture.Root,
                fixture.Inventory);

            Assert.Contains(
                "Changes must preserve the shared community-site behavior. _(User-provided)_",
                knowledge);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task AcceptedKnowledgeRecap_StaysFlowScopedUntilSourceReanalysis()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"repository-recap-db-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite(
                $"Data Source={Path.Combine(root, "recap.db")};Pooling=False")
            .Options;
        const string baseline = "Original reviewed knowledge.";
        const string refreshed = "Updated accepted knowledge.";

        try
        {
            await using var database = new HarnessDbContext(options);
            await database.Database.EnsureCreatedAsync();
            database.Settings.Add(new HarnessSettings
            {
                Id = 1,
                RepositoryPath = root,
                RepositoryKnowledge = baseline
            });
            var firstFlow = RepositoryStudyFixture.KnowledgeFlow(root, baseline);
            var firstStep =
                RepositoryStudyFixture.KnowledgePublicationStep(firstFlow.Id);
            database.Flows.Add(firstFlow);
            database.FlowSteps.Add(firstStep);
            await database.SaveChangesAsync();

            await WorkflowEngine.ApplyRepositoryKnowledgeRecapAsync(
                database,
                firstFlow,
                firstStep,
                new RepositoryKnowledgeRecap(
                    Changed: true,
                    Reason: "Architecture changed.",
                    Knowledge: refreshed),
                CancellationToken.None);
            await database.SaveChangesAsync();
            database.ChangeTracker.Clear();

            var settings = await database.Settings.SingleAsync();
            Assert.Equal(baseline, settings.RepositoryKnowledge);
            Assert.Contains(
                await database.FlowEvents.ToListAsync(),
                item => item.Type ==
                        WorkflowEngine.RepositoryKnowledgeRefreshSkippedEventType);

            settings.RepositoryKnowledge = "User-authored newer knowledge.";
            await database.SaveChangesAsync();
            var secondFlow = RepositoryStudyFixture.KnowledgeFlow(root, baseline);
            var secondStep =
                RepositoryStudyFixture.KnowledgePublicationStep(secondFlow.Id);
            database.Flows.Add(secondFlow);
            database.FlowSteps.Add(secondStep);
            await database.SaveChangesAsync();

            await WorkflowEngine.ApplyRepositoryKnowledgeRecapAsync(
                database,
                secondFlow,
                secondStep,
                new RepositoryKnowledgeRecap(
                    Changed: true,
                    Reason: "A later architecture change.",
                    Knowledge: "Agent-generated replacement."),
                CancellationToken.None);
            await database.SaveChangesAsync();
            database.ChangeTracker.Clear();

            Assert.Equal(
                "User-authored newer knowledge.",
                (await database.Settings.SingleAsync()).RepositoryKnowledge);
            Assert.Contains(
                await database.FlowEvents.ToListAsync(),
                item =>
                    item.FlowRunId == secondFlow.Id &&
                    item.Type ==
                    WorkflowEngine.RepositoryKnowledgeRefreshSkippedEventType);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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

    private sealed class RepositoryStudyProcessRunner : ProcessRunner
    {
        private readonly IReadOnlyList<string> _contracts;
        private int _invocationIndex;

        public RepositoryStudyProcessRunner(params string[] contracts)
        {
            if (contracts.Length == 0)
            {
                throw new ArgumentException(
                    "At least one scripted repository contract is required.",
                    nameof(contracts));
            }
            _contracts = contracts;
        }

        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public List<string> Prompts { get; } = [];

        public override Task<ProcessResult> RunAsync(
            string executable,
            IEnumerable<string> arguments,
            string workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken = default,
            Action<string>? standardOutputLineReceived = null,
            TimeSpan? stallTimeout = null,
            IReadOnlyDictionary<string, string?>? environmentVariables = null)
        {
            var argumentArray = arguments.ToArray();
            Arguments = argumentArray;
            var promptIndex = Array.IndexOf(argumentArray, "-p");
            Prompts.Add(argumentArray[promptIndex + 1]);
            var contract = _contracts[
                Math.Min(_invocationIndex, _contracts.Count - 1)];
            _invocationIndex++;
            var output = string.Join(
                Environment.NewLine,
                JsonSerializer.Serialize(new
                {
                    type = "tool.execution_start",
                    data = new
                    {
                        toolCallId = "study-one",
                        toolName = "glob",
                        arguments = new { pattern = "**/*" }
                    }
                }),
                JsonSerializer.Serialize(new
                {
                    type = "tool.execution_complete",
                    data = new
                    {
                        toolCallId = "study-one",
                        toolName = "glob",
                        success = true
                    }
                }),
                JsonSerializer.Serialize(new
                {
                    type = "assistant.message",
                    data = new { content = contract }
                }),
                """{"type":"result","exitCode":0}""");
            return Task.FromResult(new ProcessResult(0, output, string.Empty));
        }
    }

    private sealed class RepositoryStudyFixture : IDisposable
    {
        private RepositoryStudyFixture(
            string root,
            string readmeBody,
            RepositoryStudyInventory inventory)
        {
            Root = root;
            ReadmeBody = readmeBody;
            Inventory = inventory;
        }

        public string Root { get; }

        public string ReadmeBody { get; }

        public RepositoryStudyInventory Inventory { get; }

        public string SiteReadme => Path.Combine("site", "README.md");

        public string SiteManifest => Path.Combine("site", "package.json");

        public string SiteSource => Path.Combine("site", "src", "app.ts");

        public string SiteTest => Path.Combine("site", "tests", "app.test.ts");

        public string DataReadme => Path.Combine("data", "README.md");

        public static async Task<RepositoryStudyFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"repository-study-{Guid.NewGuid():N}");
            var site = Path.Combine(root, "site");
            var data = Path.Combine(root, "data");
            var readmeBody =
                "OUTDATED README BODY THAT MUST NOT BE COPIED INTO KNOWLEDGE";
            Directory.CreateDirectory(Path.Combine(site, ".git"));
            Directory.CreateDirectory(Path.Combine(site, "src"));
            Directory.CreateDirectory(Path.Combine(site, "tests"));
            Directory.CreateDirectory(Path.Combine(data, ".git"));
            await File.WriteAllTextAsync(
                Path.Combine(site, "README.md"),
                readmeBody);
            await File.WriteAllTextAsync(
                Path.Combine(site, "package.json"),
                """{"scripts":{"test":"vitest run"},"dependencies":{"@angular/core":"22.0.0"}}""");
            await File.WriteAllTextAsync(
                Path.Combine(site, "src", "app.ts"),
                "export const site = 'community';");
            await File.WriteAllTextAsync(
                Path.Combine(site, "tests", "app.test.ts"),
                "test('site', () => expect(true).toBe(true));");
            await File.WriteAllTextAsync(
                Path.Combine(data, "README.md"),
                "# Data API");

            var repositories = RepositoryAnalyzer.FindGitRepositories(root);
            var inventory = await RepositoryAnalyzer.BuildStudyInventoryAsync(
                root,
                repositories,
                CancellationToken.None);
            return new RepositoryStudyFixture(root, readmeBody, inventory);
        }

        public string ValidContract() =>
            Contract(
                productEvidence: [SiteReadme, SiteSource],
                architectureEvidence: [SiteSource, DataReadme],
                technologyEvidence: [SiteManifest, SiteTest],
                constraintEvidence: [SiteSource, SiteTest]);

        public string FindingWithRepositoryFieldsContract() =>
            ValidContract().Replace(
                "\"Summary\":\"Study project serves the community sites.\"",
                "\"Path\":\"site\",\"Purpose\":\"Incorrect finding shape.\"",
                StringComparison.Ordinal);

        public string ChangedRecap()
        {
            var contract = ValidContract().ReplaceLineEndings("\n");
            var begin = contract.IndexOf(
                RepositoryKnowledgeSynthesizer.BeginSentinel,
                StringComparison.Ordinal);
            var end = contract.IndexOf(
                RepositoryKnowledgeSynthesizer.EndSentinel,
                StringComparison.Ordinal);
            var knowledgeJson = contract[
                (begin + RepositoryKnowledgeSynthesizer.BeginSentinel.Length)..end]
                .Trim();
            return
                RepositoryKnowledgeSynthesizer.RecapBeginSentinel +
                Environment.NewLine +
                $$"""{"Changed":true,"Reason":"The accepted implementation changed durable architecture.","Knowledge":{{knowledgeJson}}}""" +
                Environment.NewLine +
                RepositoryKnowledgeSynthesizer.RecapEndSentinel;
        }

        public string UnchangedRecap() =>
            RepositoryKnowledgeSynthesizer.RecapBeginSentinel +
            Environment.NewLine +
            """{"Changed":false,"Reason":"The accepted implementation does not change durable repository knowledge.","Knowledge":null}""" +
            Environment.NewLine +
            RepositoryKnowledgeSynthesizer.RecapEndSentinel;

        public string Contract(
            IReadOnlyList<string> productEvidence,
            IReadOnlyList<string> architectureEvidence,
            IReadOnlyList<string> technologyEvidence,
            IReadOnlyList<string> constraintEvidence,
            IReadOnlyList<string>? siteRepositoryEvidence = null,
            IReadOnlyList<string>? riskEvidence = null,
            string? constraintBasis = null)
        {
            var document = new
            {
                Project = Path.GetFileName(Root),
                ProductAndScope = new[]
                {
                    new
                    {
                        Summary = "Study project serves the community sites.",
                        Basis =
                            RepositoryKnowledgeSynthesizer.RepositoryEvidenceBasis,
                        Evidence = productEvidence
                    }
                },
                Repositories = new[]
                {
                    new
                    {
                        Path = Path.Combine("data"),
                        Purpose = "Provides the runtime content consumed by the site.",
                        Evidence = (IReadOnlyList<string>)[DataReadme]
                    },
                    new
                    {
                        Path = Path.Combine("site"),
                        Purpose = "Implements and verifies the user-facing application.",
                        Evidence = siteRepositoryEvidence ??
                                   new[] { SiteManifest, SiteSource }
                    }
                },
                ArchitectureAndDataFlow = new[]
                {
                    new
                    {
                        Summary = "The site application consumes content maintained by the data repository.",
                        Basis =
                            RepositoryKnowledgeSynthesizer.RepositoryEvidenceBasis,
                        Evidence = architectureEvidence
                    }
                },
                TechnologyAndWorkflow = new[]
                {
                    new
                    {
                        Summary = "The Angular application is verified with its configured test script.",
                        Basis =
                            RepositoryKnowledgeSynthesizer.RepositoryEvidenceBasis,
                        Evidence = technologyEvidence
                    }
                },
                ConstraintsAndConventions = new[]
                {
                    new
                    {
                        Summary = "Changes must preserve the shared community-site behavior.",
                        Basis = constraintBasis ??
                                RepositoryKnowledgeSynthesizer
                                    .RepositoryEvidenceBasis,
                        Evidence = constraintEvidence
                    }
                },
                RisksAndUnknowns = new[]
                {
                    new
                    {
                        Summary = "The README claim may be stale and needs confirmation against current behavior.",
                        Basis =
                            RepositoryKnowledgeSynthesizer.UnresolvedBasis,
                        Evidence = riskEvidence ??
                                   new[] { SiteReadme, SiteSource }
                    }
                }
            };
            return
                RepositoryKnowledgeSynthesizer.BeginSentinel +
                Environment.NewLine +
                JsonSerializer.Serialize(document) +
                Environment.NewLine +
                RepositoryKnowledgeSynthesizer.EndSentinel;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        public static FlowRun KnowledgeFlow(
            string repositoryPath,
            string repositoryKnowledge) =>
            new()
            {
                Title = "Knowledge recap",
                OriginalRequest = "Update durable behavior.",
                ConsolidatedRequest = "Update durable behavior.",
                RepositoryPath = repositoryPath,
                RepositoryKnowledge = repositoryKnowledge,
                WorkspacePath = repositoryPath
            };

        public static FlowStep KnowledgePublicationStep(Guid flowId) =>
            new()
            {
                FlowRunId = flowId,
                Iteration = 1,
                Sequence = 10,
                AgentId = "release-engineer",
                AgentName = "Release Engineer",
                AgentRole = "release-engineer",
                InvocationKind = ExecutionInvocationKind.Publication,
                PlanStage = PlanStage.AfterApproval
            };
    }
}

public sealed class WorkspaceManagerTests
{
    [Fact]
    public void ReconciledFolderScope_UsesLatestDurableBaseline()
    {
        var flow = new FlowRun
        {
            Title = "Folder flow",
            OriginalRequest = "Update folder",
            RepositoryPath = @"C:\project\application"
        };
        var first = WorkspaceSourceScopeLedger.Serialize(
            new WorkspaceInfo(@"C:\workspace", "branch", true,
                SourceScopeRelativePath: "application",
                SourceBaselineCommit: new string('a', 40)));
        var second = WorkspaceSourceScopeLedger.Serialize(
            new WorkspaceInfo(@"C:\workspace", "branch", false,
                SourceScopeRelativePath: "application",
                SourceBaselineCommit: new string('b', 40)));
        flow.Events.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            Type = WorkspaceSourceScopeLedger.EventType,
            Message = "Initial baseline",
            DataJson = first,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        flow.Events.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            Type = WorkspaceSourceScopeLedger.EventType,
            Message = "Reconciled baseline",
            DataJson = second,
            CreatedAt = DateTimeOffset.UtcNow
        });

        Assert.Equal(new string('b', 40),
            WorkspaceSourceScopeLedger.Read(flow).BaselineCommit);
    }

    [Fact]
    public async Task UnversionedProject_UsesLocalFilesAndOnlyInitializesWorkspaceGit()
    {
        var root = Path.Combine(
            Path.GetTempPath(), $"ai-harness-local-source-{Guid.NewGuid():N}");
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(
            Path.Combine(project, "source.txt"), "local source");
        await File.WriteAllTextAsync(
            Path.Combine(root, "WORKFLOW.md"),
            "---\nworkspace:\n  root: workspaces\n---\n\nTest workflow.\n");
        var git = new ProcessRunner();
        var provider = new WorkflowDefinitionProvider(
            new HarnessPaths(root, Path.Combine(root, ".github", "agents"),
                Path.Combine(root, "harness.db")),
            new WorkflowLoader(),
            NullLogger<WorkflowDefinitionProvider>.Instance);
        await provider.StartAsync(CancellationToken.None);
        var manager = new WorkspaceManager(
            git, provider,
            new WorkspaceHookRunner(
                provider, git, NullLogger<WorkspaceHookRunner>.Instance),
            NullLogger<WorkspaceManager>.Instance);
        var flow = new FlowRun
        {
            Title = "Local change", OriginalRequest = "Local change",
            RepositoryPath = project, Status = FlowStatus.Intake,
            Outcome = OutcomeType.Commit
        };
        try
        {
            Assert.True(RepositoryAnalyzer.IsProjectDirectory(project));
            var snapshot = await manager.PrepareAsync(flow);
            Assert.Equal("local source", await File.ReadAllTextAsync(
                Path.Combine(snapshot.Path, "source.txt")));
            flow.WorkspacePath = snapshot.Path;
            flow.Status = FlowStatus.Queued;
            var delivery = await manager.PrepareAsync(flow);
            flow.WorkspacePath = delivery.Path;
            flow.BranchName = delivery.BranchName;
            Assert.True(Directory.Exists(Path.Combine(delivery.Path, ".git")));
            Assert.Equal("local source", await File.ReadAllTextAsync(
                Path.Combine(delivery.Path, "source.txt")));
            Assert.Single(delivery.TrustedRepositories!);
            Assert.Equal(".", delivery.TrustedRepositories![0].RelativePath);
            Assert.Equal(string.Empty, delivery.TrustedRepositories[0].RemoteRepository);
            Assert.False(Directory.Exists(Path.Combine(project, ".git")));
            flow.Events.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                Type = StudioWorkspaceRepositoryMapLedger.EventType,
                Message = "Trusted local workspace.",
                DataJson = StudioWorkspaceRepositoryMapLedger.Serialize(
                    StudioWorkspaceRepositoryMapLedger.Create(
                        flow, delivery.Path, delivery.TrustedRepositories))
            });
            var candidate = await new CandidateFingerprintService(
                    git, TimeProvider.System)
                .PrepareAsync(
                    flow, $"sha256:{new string('a', 64)}",
                    Guid.NewGuid(), requiresPreview: false);
            Assert.Single(candidate.Manifest.Repositories);
            Assert.Equal(WorkspaceCleanupResult.Empty, await manager.RemoveAsync(flow));
            Assert.False(Directory.Exists(delivery.Path));
            Assert.True(File.Exists(Path.Combine(project, "source.txt")));
        }
        finally
        {
            provider.Dispose();
            if (Directory.Exists(root))
            {
                DeleteDirectory(root);
            }
        }
    }

    [Fact]
    public async Task SelectedFolder_UsesLatestParentRepositoryWithoutPullingSource()
    {
        var root = Path.Combine(
            Path.GetTempPath(), $"ai-harness-subfolder-{Guid.NewGuid():N}");
        var repository = Path.Combine(root, "source");
        var project = Path.Combine(repository, "application");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(
            Path.Combine(project, "app.txt"), "initial");
        await File.WriteAllTextAsync(
            Path.Combine(root, "WORKFLOW.md"),
            "---\nworkspace:\n  root: workspaces\n---\n\nTest workflow.\n");
        var git = new ProcessRunner();
        await RunGitAsync(git, repository, ["init", "--quiet"]);
        await RunGitAsync(git, repository, ["add", "."]);
        await RunGitAsync(git, repository,
            ["-c", "user.name=AI Harness Tests",
             "-c", "user.email=ai-harness@example.invalid",
             "commit", "--quiet", "-m", "Initial commit"]);
        await RunGitAsync(git, repository, ["branch", "-M", "main"]);
        var remote = Path.Combine(root, "origin.git");
        await RunGitAsync(git, repository, ["init", "--bare", "--quiet", remote]);
        await RunGitAsync(git, repository, ["remote", "add", "origin", remote]);
        await RunGitAsync(git, repository, ["push", "--quiet", "origin", "main"]);
        await RunGitAsync(git, repository,
            ["--git-dir", remote, "symbolic-ref", "HEAD", "refs/heads/main"]);
        var contributor = Path.Combine(root, "contributor");
        await RunGitAsync(git, repository, ["clone", "--quiet", remote, contributor]);
        Directory.CreateDirectory(Path.Combine(contributor, "application"));
        await CommitAndPushAsync(
            git, Path.Combine(contributor, "application"), "app.txt",
            "latest upstream", "main");
        var provider = new WorkflowDefinitionProvider(
            new HarnessPaths(root, Path.Combine(root, ".github", "agents"),
                Path.Combine(root, "harness.db")),
            new WorkflowLoader(),
            NullLogger<WorkflowDefinitionProvider>.Instance);
        await provider.StartAsync(CancellationToken.None);
        var manager = new WorkspaceManager(
            git, provider,
            new WorkspaceHookRunner(
                provider, git, NullLogger<WorkspaceHookRunner>.Instance),
            NullLogger<WorkspaceManager>.Instance);
        var flow = new FlowRun
        {
            Title = "Scoped work", OriginalRequest = "Scoped work",
            RepositoryPath = project, Status = FlowStatus.Intake
        };
        try
        {
            Assert.Equal(repository,
                RepositoryAnalyzer.FindContainingGitRepository(project));
            var snapshot = await manager.PrepareAsync(flow);
            Assert.Equal("latest upstream", await File.ReadAllTextAsync(
                Path.Combine(snapshot.Path, "app.txt")));
            flow.WorkspacePath = snapshot.Path;
            flow.Status = FlowStatus.Queued;
            var delivery = await manager.PrepareAsync(flow);
            flow.WorkspacePath = delivery.Path;
            flow.BranchName = delivery.BranchName;
            Assert.Equal("latest upstream", await File.ReadAllTextAsync(
                Path.Combine(delivery.Path, "application", "app.txt")));
            Assert.Equal("initial", await File.ReadAllTextAsync(
                Path.Combine(project, "app.txt")));
            Assert.True(Directory.Exists(Path.Combine(delivery.Path, ".git")) ||
                        File.Exists(Path.Combine(delivery.Path, ".git")));
            Assert.Single(delivery.TrustedRepositories!);
            Assert.Equal(".", delivery.TrustedRepositories![0].RelativePath);
            Assert.Equal("application", delivery.SourceScopeRelativePath);
            Assert.Matches("^[0-9a-f]{40}([0-9a-f]{24})?$",
                delivery.SourceBaselineCommit);
            flow.Events.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                Type = WorkspaceSourceScopeLedger.EventType,
                Message = "Selected folder scope.",
                DataJson = WorkspaceSourceScopeLedger.Serialize(delivery)
            });
            Assert.Equal("application",
                WorkspaceSourceScopeLedger.Read(flow).RelativePath);
            Assert.Equal(1, (await manager.RemoveAsync(flow)).WorktreesRemoved);
        }
        finally
        {
            provider.Dispose();
            if (Directory.Exists(root))
            {
                DeleteDirectory(root);
            }
        }
    }

    [Theory]
    [InlineData("main")]
    [InlineData("master")]
    public async Task RemoteBase_IsUsedForEveryRepositoryWithoutUpdatingSourceCheckouts(
        string baseBranch)
    {
        var root = Path.Combine(
            Path.GetTempPath(), $"ai-harness-current-source-{Guid.NewGuid():N}");
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(
            Path.Combine(root, "WORKFLOW.md"),
            "---\nworkspace:\n  root: workspaces\n---\n\nTest workflow.\n");
        var git = new ProcessRunner();
        var repositories = new[] { "site", "data" };
        foreach (var name in repositories)
        {
            var source = Path.Combine(project, name);
            var remote = Path.Combine(root, $"{name}.git");
            var contributor = Path.Combine(root, $"{name}-contributor");
            await CreateRepositoryAsync(git, source, $"{name}.txt");
            await RunGitAsync(git, source, ["branch", "-M", baseBranch]);
            await RunGitAsync(git, source, ["init", "--bare", "--quiet", remote]);
            await RunGitAsync(git, source, ["remote", "add", "origin", remote]);
            await RunGitAsync(git, source, ["push", "--quiet", "origin", baseBranch]);
            await RunGitAsync(git, source,
                ["--git-dir", remote, "symbolic-ref", "HEAD",
                    $"refs/heads/{baseBranch}"]);
            await RunGitAsync(git, source, ["clone", "--quiet", remote, contributor]);
            await CommitAndPushAsync(
                git, contributor, $"{name}.txt", "new upstream", baseBranch);
        }
        var paths = new HarnessPaths(
            root, Path.Combine(root, ".github", "agents"),
            Path.Combine(root, "harness.db"));
        var provider = new WorkflowDefinitionProvider(
            paths, new WorkflowLoader(),
            NullLogger<WorkflowDefinitionProvider>.Instance);
        await provider.StartAsync(CancellationToken.None);
        var manager = new WorkspaceManager(
            git, provider,
            new WorkspaceHookRunner(
                provider, git, NullLogger<WorkspaceHookRunner>.Instance),
            NullLogger<WorkspaceManager>.Instance);
        var intake = new FlowRun
        {
            Title = "Read latest", OriginalRequest = "Read latest",
            RepositoryPath = project, Status = FlowStatus.Intake
        };
        var delivery = new FlowRun
        {
            Title = "Change latest", OriginalRequest = "Change latest",
            RepositoryPath = project, Status = FlowStatus.Queued
        };
        try
        {
            var snapshot = await manager.PrepareAsync(intake);
            intake.WorkspacePath = snapshot.Path;
            foreach (var name in repositories)
            {
                Assert.Equal("new upstream", await File.ReadAllTextAsync(
                    Path.Combine(snapshot.Path, name, $"{name}.txt")));
                Assert.Equal("initial", await File.ReadAllTextAsync(
                    Path.Combine(project, name, $"{name}.txt")));
            }
            var workspace = await manager.PrepareAsync(delivery);
            delivery.WorkspacePath = workspace.Path;
            delivery.BranchName = workspace.BranchName;
            foreach (var name in repositories)
            {
                Assert.Equal("new upstream", await File.ReadAllTextAsync(
                    Path.Combine(workspace.Path, name, $"{name}.txt")));
            }

            await CommitAndPushAsync(
                git, Path.Combine(root, "site-contributor"), "site.txt",
                "newer upstream", baseBranch);
            var stale = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.PrepareAsync(delivery));
            Assert.Contains("behind", stale.Message);
            Assert.Equal("new upstream", await File.ReadAllTextAsync(
                Path.Combine(workspace.Path, "site", "site.txt")));
        }
        finally
        {
            if (Directory.Exists(delivery.WorkspacePath))
            {
                await manager.RemoveAsync(delivery);
            }
            if (Directory.Exists(intake.WorkspacePath))
            {
                await manager.RemoveAsync(intake);
            }
            provider.Dispose();
            DeleteDirectory(root);
        }
    }

    private static async Task CommitAndPushAsync(
        ProcessRunner git, string repository, string fileName, string contents,
        string baseBranch)
    {
        await File.WriteAllTextAsync(Path.Combine(repository, fileName), contents);
        await RunGitAsync(git, repository, ["add", fileName]);
        await RunGitAsync(git, repository,
            ["-c", "user.name=AI Harness Tests",
             "-c", "user.email=ai-harness@example.invalid",
             "commit", "--quiet", "-m", contents]);
        await RunGitAsync(git, repository, ["push", "--quiet", "origin", baseBranch]);
    }

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
            RepositoryPath = project,
            Status = FlowStatus.Queued
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

            flow.WorkspacePath = workspace.Path;
            flow.BranchName = workspace.BranchName;
            var recovered = await manager.PrepareAsync(flow);
            Assert.False(recovered.CreatedNow);
            Assert.Equal(workspace.Path, recovered.Path);

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
                    var worktrees = await RunGitAsync(
                        processRunner,
                        repositoryPath,
                        ["worktree", "list", "--porcelain"]);
                    if (worktrees.StandardOutput.Contains(
                            Path.GetFullPath(repositoryWorkspace),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        await RunGitAsync(
                            processRunner,
                            repositoryPath,
                            ["worktree", "remove", "--force", repositoryWorkspace]);
                    }
                }
            }
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PublicationInvocation_SuppressesAfterCreateHook()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-sensitive-workspace-{Guid.NewGuid():N}");
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        var hook = OperatingSystem.IsWindows()
            ? "Set-Content -Path after-create-hook.txt -Value ran"
            : "printf ran > after-create-hook.txt";
        await File.WriteAllTextAsync(
            Path.Combine(root, "WORKFLOW.md"),
            $$"""
              ---
              workspace:
                root: workspaces
              hooks:
                after_create: {{hook}}
              ---

              Test workflow.
              """);
        var processRunner = new ProcessRunner();
        await CreateRepositoryAsync(
            processRunner,
            project,
            "project.txt");
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
        var created = new List<FlowRun>();

        try
        {
            var publication = new FlowRun
            {
                Title = "Sensitive Publication",
                OriginalRequest = "Use the sealed candidate.",
                Kind = FlowKind.Delivery,
                Status = FlowStatus.Queued,
                RepositoryPath = project
            };
            created.Add(publication);
            var publicationWorkspace = await manager.PrepareForInvocationAsync(
                publication,
                ExecutionInvocationKind.Publication);
            publication.WorkspacePath = publicationWorkspace.Path;
            publication.BranchName = publicationWorkspace.BranchName;
            Assert.False(File.Exists(Path.Combine(
                publicationWorkspace.Path,
                "after-create-hook.txt")));

            var worker = new FlowRun
            {
                Title = "Normal worker",
                OriginalRequest = "Run normal Delivery work.",
                Kind = FlowKind.Delivery,
                Status = FlowStatus.Running,
                RepositoryPath = project
            };
            created.Add(worker);
            var workerWorkspace = await manager.PrepareForInvocationAsync(
                worker,
                ExecutionInvocationKind.Worker);
            worker.WorkspacePath = workerWorkspace.Path;
            worker.BranchName = workerWorkspace.BranchName;
            Assert.True(File.Exists(Path.Combine(
                workerWorkspace.Path,
                "after-create-hook.txt")));
        }
        finally
        {
            foreach (var flow in created)
            {
                if (!string.IsNullOrWhiteSpace(flow.WorkspacePath) &&
                    Directory.Exists(flow.WorkspacePath))
                {
                    await manager.RemoveAsync(flow);
                }
            }
            workflowProvider.Dispose();
            if (Directory.Exists(root))
            {
                DeleteDirectory(root);
            }
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

public sealed class CopilotReasoningHostAfterRunHookPolicyFallbackTests
{
    [Fact]
    public async Task ResolveWorkspaceHookPolicyAsync_ThrowsWhenTheFlowRowDoesNotExist()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite(connection)
            .Options;
        IDbContextFactory<HarnessDbContext> factory =
            new HookPolicyDbContextFactory(options);
        await using (var database = await factory.CreateDbContextAsync())
        {
            await database.Database.EnsureCreatedAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CopilotReasoningHost.ResolveWorkspaceHookPolicyAsync(
                factory,
                Guid.NewGuid(),
                CancellationToken.None));
    }

    [Fact]
    public async Task AfterRunHook_InitialLookupFailsThenCleanupLookupSucceeds_DeliveryHookIsAttempted()
    {
        var root = CreateTempWorkspace();
        try
        {
            var factory = await CreateFactoryAsync();
            var flowId = Guid.NewGuid();

            // Reproduces the initial workspace-hook-policy lookup (made earlier in the
            // same RunAgentAsync call, inside the outer try) failing because the flow
            // row was not yet visible to that query.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CopilotReasoningHost.ResolveWorkspaceHookPolicyAsync(
                    factory,
                    flowId,
                    CancellationToken.None));

            // The flow becomes visible before the finally block's best-effort cleanup
            // re-resolution runs.
            await SeedFlowAsync(
                factory,
                flowId,
                FlowKind.Delivery,
                FlowStatus.Running);

            var marker = Path.Combine(root, "after-run.marker");
            var hookRunner = CreateHookRunner(root);
            var workflow = WorkflowWithAfterRunHook(MarkerScript(marker));

            await CopilotReasoningHost.RunAfterRunWorkspaceHookAsync(
                runWorkspaceHooks: true,
                hookPolicy: null,
                factory,
                hookRunner,
                flowId,
                root,
                workflow,
                NullLogger.Instance,
                CancellationToken.None);

            Assert.True(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AfterRunHook_InitialLookupFailsThenCleanupLookupSucceeds_AdvisoryHookRemainsSkipped()
    {
        var root = CreateTempWorkspace();
        try
        {
            var factory = await CreateFactoryAsync();
            var flowId = Guid.NewGuid();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CopilotReasoningHost.ResolveWorkspaceHookPolicyAsync(
                    factory,
                    flowId,
                    CancellationToken.None));

            await SeedFlowAsync(
                factory,
                flowId,
                FlowKind.Advisory,
                FlowStatus.Running);

            var marker = Path.Combine(root, "after-run.marker");
            var hookRunner = CreateHookRunner(root);
            var workflow = WorkflowWithAfterRunHook(MarkerScript(marker));

            // The cleanup re-resolution succeeds (it finds the flow row), but the
            // resolved policy is Advisory, so the hook must still never execute.
            await CopilotReasoningHost.RunAfterRunWorkspaceHookAsync(
                runWorkspaceHooks: true,
                hookPolicy: null,
                factory,
                hookRunner,
                flowId,
                root,
                workflow,
                NullLogger.Instance,
                CancellationToken.None);

            Assert.False(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AfterRunHook_SensitiveInvocationNeverAttemptsCleanupLookupOrHook(
        bool flowRowExists)
    {
        var root = CreateTempWorkspace();
        try
        {
            var factory = await CreateFactoryAsync();
            var flowId = Guid.NewGuid();
            if (flowRowExists)
            {
                // Even when a lookup would have succeeded, a sensitive invocation
                // Publication invocations must never attempt the after_run hook:
                // runWorkspaceHooks already gates this before policy resolution.
                await SeedFlowAsync(
                    factory,
                    flowId,
                    FlowKind.Delivery,
                    FlowStatus.Running);
            }

            var marker = Path.Combine(root, "after-run.marker");
            var hookRunner = CreateHookRunner(root);
            var workflow = WorkflowWithAfterRunHook(MarkerScript(marker));

            await CopilotReasoningHost.RunAfterRunWorkspaceHookAsync(
                runWorkspaceHooks: false,
                hookPolicy: null,
                factory,
                hookRunner,
                flowId,
                root,
                workflow,
                NullLogger.Instance,
                CancellationToken.None);

            Assert.False(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AfterRunHook_BothLookupsFailIsSwallowedSafelyAndSkipsHook()
    {
        var root = CreateTempWorkspace();
        try
        {
            var factory = await CreateFactoryAsync();
            var flowId = Guid.NewGuid();
            var marker = Path.Combine(root, "after-run.marker");
            var hookRunner = CreateHookRunner(root);
            var workflow = WorkflowWithAfterRunHook(MarkerScript(marker));

            // Neither the initial lookup nor the cleanup lookup can find the flow
            // (it never existed for this test). The unknown policy must never run
            // the hook, and the failure must be swallowed rather than thrown so the
            // rest of the run's cleanup still completes.
            await CopilotReasoningHost.RunAfterRunWorkspaceHookAsync(
                runWorkspaceHooks: true,
                hookPolicy: null,
                factory,
                hookRunner,
                flowId,
                root,
                workflow,
                NullLogger.Instance,
                CancellationToken.None);

            Assert.False(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AfterRunHook_AlreadyResolvedDeliveryPolicySkipsCleanupLookupAndRunsHook()
    {
        var root = CreateTempWorkspace();
        try
        {
            // A databaseFactory that throws on any use proves that when the policy
            // was already resolved by the initial (successful) lookup, no cleanup
            // re-resolution query is attempted at all.
            IDbContextFactory<HarnessDbContext> throwingFactory =
                new ThrowingDbContextFactory();
            var marker = Path.Combine(root, "after-run.marker");
            var hookRunner = CreateHookRunner(root);
            var workflow = WorkflowWithAfterRunHook(MarkerScript(marker));

            await CopilotReasoningHost.RunAfterRunWorkspaceHookAsync(
                runWorkspaceHooks: true,
                hookPolicy: new CopilotReasoningHost.WorkspaceHookPolicy(
                    FlowKind.Delivery,
                    Provisional: false),
                throwingFactory,
                hookRunner,
                Guid.NewGuid(),
                root,
                workflow,
                NullLogger.Instance,
                CancellationToken.None);

            Assert.True(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempWorkspace()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-after-run-hook-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<IDbContextFactory<HarnessDbContext>> CreateFactoryAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite(connection)
            .Options;
        IDbContextFactory<HarnessDbContext> factory =
            new HookPolicyDbContextFactory(options);
        await using (var database = await factory.CreateDbContextAsync())
        {
            await database.Database.EnsureCreatedAsync();
        }
        return factory;
    }

    private static async Task SeedFlowAsync(
        IDbContextFactory<HarnessDbContext> factory,
        Guid flowId,
        FlowKind kind,
        FlowStatus status)
    {
        await using var database = await factory.CreateDbContextAsync();
        database.Flows.Add(new FlowRun
        {
            Id = flowId,
            Title = "After-run hook policy fallback",
            OriginalRequest = "Exercise the after_run hook fallback path.",
            Kind = kind,
            Status = status
        });
        await database.SaveChangesAsync();
    }

    private static WorkspaceHookRunner CreateHookRunner(string root)
    {
        var paths = new HarnessPaths(
            root,
            Path.Combine(root, ".github", "agents"),
            Path.Combine(root, "harness.db"));
        var workflowProvider = new WorkflowDefinitionProvider(
            paths,
            new WorkflowLoader(),
            NullLogger<WorkflowDefinitionProvider>.Instance);
        return new WorkspaceHookRunner(
            workflowProvider,
            new ProcessRunner(),
            NullLogger<WorkspaceHookRunner>.Instance);
    }

    private static WorkflowDefinition WorkflowWithAfterRunHook(string script) =>
        new(
            new WorkflowConfig
            {
                Hooks = new HookConfig
                {
                    AfterRun = script,
                    TimeoutMs = 15_000
                }
            },
            "{{ task }}",
            "WORKFLOW.md",
            DateTimeOffset.UtcNow,
            "revision-under-test");

    private static string MarkerScript(string markerPath) =>
        OperatingSystem.IsWindows()
            ? $"New-Item -ItemType File -Path \"{markerPath}\" -Force | Out-Null"
            : $"touch \"{markerPath}\"";

    private sealed class HookPolicyDbContextFactory(
        DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);
    }

    private sealed class ThrowingDbContextFactory : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() =>
            throw new InvalidOperationException(
                "The database must not be accessed when the policy was already resolved.");
    }
}

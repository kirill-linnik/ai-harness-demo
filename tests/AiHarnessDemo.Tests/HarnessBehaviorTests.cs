using AiHarnessDemo.Data;
using AiHarnessDemo.Api;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using System.Text;

namespace AiHarnessDemo.Tests;

public sealed class AdaptiveModelRouterTests
{
    [Theory]
    [InlineData(ModelSelectionStrategy.MaximumQuality, "quality")]
    [InlineData(ModelSelectionStrategy.FastestResponse, "fast")]
    [InlineData(ModelSelectionStrategy.LowestCost, "cheap")]
    public void Rank_UsesConfiguredLexicographicObjective(
        ModelSelectionStrategy strategy,
        string expectedModel)
    {
        var scores = new[]
        {
            Score("quality", quality: .95, time: 20, premium: 2),
            Score("fast", quality: .91, time: 5, premium: 1.5),
            Score("cheap", quality: .90, time: 15, premium: .5)
        };

        var ranked = AdaptiveModelRouter.Rank(scores, strategy, TaskRisk.Medium);

        Assert.Equal(expectedModel, ranked[0].Model);
    }

    [Fact]
    public void Rank_EnforcesCriticalQualityFloor()
    {
        var ranked = AdaptiveModelRouter.Rank(
            [
                Score("best", quality: .95, time: 50, premium: 4),
                Score("faster", quality: .949, time: 1, premium: .1)
            ],
            ModelSelectionStrategy.FastestResponse,
            TaskRisk.Critical);

        Assert.Single(ranked);
        Assert.Equal("best", ranked[0].Model);
    }

    [Fact]
    public void Exploration_IsDeterministicAndNeverUsedForHighRisk()
    {
        var scores = new[]
        {
            Score("best", quality: .95, time: 10, premium: 1, uncertainty: .1),
            Score("uncertain", quality: .94, time: 11, premium: 1.1, uncertainty: .9)
        };
        var exploringId = Enumerable.Range(1, 10_000)
            .Select(value => new Guid(value, 0, 0, new byte[8]))
            .First(id => AdaptiveModelRouter.SelectCandidate(
                scores,
                ModelSelectionStrategy.MaximumQuality,
                TaskRisk.Low,
                id).Exploration);

        var first = AdaptiveModelRouter.SelectCandidate(
            scores,
            ModelSelectionStrategy.MaximumQuality,
            TaskRisk.Low,
            exploringId);
        var second = AdaptiveModelRouter.SelectCandidate(
            scores,
            ModelSelectionStrategy.MaximumQuality,
            TaskRisk.Low,
            exploringId);
        var highRisk = AdaptiveModelRouter.SelectCandidate(
            scores,
            ModelSelectionStrategy.MaximumQuality,
            TaskRisk.High,
            exploringId);

        Assert.True(first.Exploration);
        Assert.Equal(first, second);
        Assert.False(highRisk.Exploration);
    }

    private static AdaptiveModelRouter.CandidateScore Score(
        string model,
        double quality,
        double time,
        double premium,
        double uncertainty = .2) =>
        new(model, "high", quality, time, premium, 1 - uncertainty, uncertainty);
}

public sealed class AgentCatalogTests
{
    [Fact]
    public void Parse_LoadsCopilotAgentFrontMatterAndRoleContract()
    {
        const string content = """
            ---
            name: Software Engineer
            description: Implements verified changes.
            ---

            # Contract
            Produce working code.
            """;

        var manifest = AgentCatalogLoader.Parse(
            "software-engineer",
            "software-engineer.agent.md",
            content);

        Assert.Equal("Software Engineer", manifest.Name);
        Assert.Equal("software-engineer", manifest.Role);
        Assert.Equal(60, manifest.SortOrder);
        Assert.Contains("Produce working code", manifest.Instructions);
    }

    [Fact]
    public void BrowserDeliveryAgents_RequireMobileOverflowProofBeforeHandoff()
    {
        var agents = Path.Combine(RepositoryRoot(), ".github", "agents");
        var teamLead = AgentCatalogLoader.Parse(
            "team-lead",
            "team-lead.agent.md",
            File.ReadAllText(Path.Combine(agents, "team-lead.agent.md")));
        var engineer = AgentCatalogLoader.Parse(
            "software-engineer",
            "software-engineer.agent.md",
            File.ReadAllText(Path.Combine(agents, "software-engineer.agent.md")));

        Assert.Contains("responsive", teamLead.Instructions);
        Assert.Contains("independent", teamLead.Instructions);
        Assert.Contains("current assignment's", teamLead.Instructions);
        Assert.Contains("horizontal overflow", engineer.Instructions);
        Assert.Contains("trusted project scaffold", engineer.Instructions);
        Assert.Contains("account for all changed paths", engineer.Instructions);
        Assert.Contains(
            "production-visible",
            engineer.Instructions);
        Assert.Contains(
            "including skip links",
            engineer.Instructions);

        var qualityEngineer = AgentCatalogLoader.Parse(
            "quality-engineer",
            "quality-engineer.agent.md",
            File.ReadAllText(Path.Combine(agents, "quality-engineer.agent.md")));
        Assert.Contains(
            "repository's supported runtime",
            qualityEngineer.Instructions);
        Assert.Contains(
            "a static preview, template inspection, or data record cannot substitute",
            qualityEngineer.Instructions,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Read each cited observation's actual result",
            qualityEngineer.Instructions);

        var sceptic = AgentCatalogLoader.Parse(
            "pre-mortem-sceptic",
            "pre-mortem-sceptic.agent.md",
            File.ReadAllText(Path.Combine(agents, "pre-mortem-sceptic.agent.md")));
        Assert.Contains("Counterfactual case file", sceptic.Instructions);
        Assert.Contains("Do not inspect implementation source", sceptic.Instructions);
        Assert.Contains("finding no gap is a valid outcome", sceptic.Instructions);
        Assert.Contains("Independent final verification separately validates", sceptic.Instructions);
    }

    [Fact]
    public void CheckedInAgentCatalog_IsValidAndHasNoIncidentOrProjectDetails()
    {
        var root = RepositoryRoot();
        var agents = Path.Combine(root, ".github", "agents");
        var files = Directory.GetFiles(agents, "*.agent.md");
        Assert.NotEmpty(files);

        var definitions = new AgentCatalogLoader().Load(agents).Definitions;
        Assert.Equal(files.Length, definitions.Count);
        foreach (var definition in definitions)
        {
            Assert.Equal(AgentDefinitionStatus.Valid, definition.Record.DefinitionStatus);
            Assert.IsType<AgentManifest>(definition.Manifest);
            var content = File.ReadAllText(definition.Record.SourcePath);
            foreach (var projectDetail in new[]
                     {
                         "devclub",
                         "ee_meetings",
                         "eu_meetings",
                         "*_conf.json",
                         "site\\.customer-preview",
                         "speaker submission"
                     })
            {
                Assert.DoesNotContain(
                    projectDetail,
                    content,
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        var authoring = File.ReadAllText(
            Path.Combine(root, ".github", "copilot-instructions.md"));
        var workflow = File.ReadAllText(Path.Combine(root, "WORKFLOW.md"));
        Assert.Contains(
            "Agent definitions are always project-agnostic role contracts",
            authoring,
            StringComparison.Ordinal);
        Assert.Contains(
            "only facts necessary for the confirmed outcome",
            workflow,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("account-manager", "without prescribing an unverified solution")]
    [InlineData("analyst", "counterexample")]
    [InlineData("team-lead", "cheap-but-wrong implementation")]
    [InlineData("team-lead", "content-uniqueness and reachable-language regression criteria")]
    [InlineData("team-lead", "independently understandable")]
    [InlineData("team-lead", "faithful source-backed media")]
    [InlineData("product-designer", "Keep, Move, Merge, Replace, or Remove")]
    [InlineData("product-designer", "one visible owner")]
    [InlineData("pre-mortem-sceptic", "cheapest faithful implementation")]
    [InlineData("software-engineer", "Zero overflow is necessary, not proof of good design")]
    [InlineData("software-engineer", "baseline evidence before labeling a defect pre-existing")]
    [InlineData("software-engineer", "focused composition regressions")]
    [InlineData("software-engineer", "never hand-transcribe binary encodings")]
    [InlineData("software-engineer", "negative corruption fixture")]
    [InlineData("quality-engineer", "technical correctness, design fidelity, and customer-outcome adequacy")]
    [InlineData("quality-engineer", "waivable risk")]
    [InlineData("quality-engineer", "inspect meaningful visible pixels")]
    [InlineData("quality-engineer", "negative corruption fixture")]
    [InlineData("product-manager", "not automatically")]
    [InlineData("architect", "complete customer outcome")]
    [InlineData("data-engineer", "old-to-new preservation")]
    [InlineData("security-engineer", "preconditions, reachability, customer impact")]
    [InlineData("release-engineer", "not an independently beautified mockup")]
    [InlineData("technical-writer", "consolidate")]
    public void CheckedInRoleContracts_RetainOutcomeQualityObligations(
        string agentId,
        string obligation)
    {
        var fileName = $"{agentId}.agent.md";
        var path = Path.Combine(RepositoryRoot(), ".github", "agents", fileName);
        var manifest = AgentCatalogLoader.Parse(agentId, fileName, File.ReadAllText(path));

        Assert.Contains(obligation, manifest.Instructions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VisualQualityContracts_RejectProxyPassesAndPreserveRoleBoundaries()
    {
        var agents = Path.Combine(RepositoryRoot(), ".github", "agents");
        var designer = File.ReadAllText(Path.Combine(agents, "product-designer.agent.md"));
        var quality = File.ReadAllText(Path.Combine(agents, "quality-engineer.agent.md"));
        var sceptic = File.ReadAllText(Path.Combine(agents, "pre-mortem-sceptic.agent.md"));
        var release = File.ReadAllText(Path.Combine(agents, "release-engineer.agent.md"));

        Assert.Contains("Do not implement production code", designer);
        Assert.Contains("baseline and candidate at matched desktop/mobile viewports", quality);
        Assert.Contains("duplicate explanations", quality);
        Assert.Contains("unavailable coverage, never as tests passed", quality);
        Assert.Contains("acceptance-plan gaps", quality);
        Assert.Contains("Never modify the", quality);
        Assert.Contains("Do not inspect implementation source", sceptic);
        Assert.Contains("not \"all good\", design approval", sceptic);
        Assert.Contains("renewed verification", release);
        Assert.Contains("customer approval has been recorded", release);
    }

    [Fact]
    public void RoleDefinitions_DoNotOwnHarnessWireProtocolsOrStorageContracts()
    {
        var agents = Path.Combine(RepositoryRoot(), ".github", "agents");
        var protocols = new[]
        {
            "TEAM_PLAN_BEGIN", "TEAM_PLAN_END", "HANDOFF_STATUS", "INTAKE_BEGIN",
            "INTAKE_END", "OUTCOME_QA_BEGIN", "FLOW_OUTCOME_BEGIN", "PUSHBACK_OWNER_STEP_ID",
            "BeforeReview", "AfterApproval", "TaskProfile", "EvidenceKinds",
            ".customer-preview", "customer-demo.json", "DURABLE_ADVISORY_PROMOTION_AUTHORIZATION",
            "host-owned", "the harness", "Studio"
        };
        foreach (var path in Directory.GetFiles(agents, "*.agent.md"))
        {
            var instructions = File.ReadAllText(path);
            foreach (var protocol in protocols)
            {
                Assert.DoesNotContain(protocol, instructions, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null &&
               !File.Exists(Path.Combine(root.FullName, "AiHarnessDemo.slnx")))
        {
            root = root.Parent;
        }
        return root?.FullName
            ?? throw new DirectoryNotFoundException(
                "The agent catalog tests must run from this repository.");
    }
}

public sealed class PreviewArtifactCatalogTests
{
    [Fact]
    public void PreviewResponses_IsolateOnlyTheArtifactAndBlockControlChannels()
    {
        var artifactContext = new DefaultHttpContext();
        var viewContext = new DefaultHttpContext();

        DemoApi.ApplyPreviewArtifactSecurityHeaders(artifactContext.Response);
        DemoApi.ApplyIsolatedPreviewViewSecurityHeaders(viewContext.Response);

        var artifactPolicy =
            artifactContext.Response.Headers["Content-Security-Policy"].ToString();
        var viewPolicy =
            viewContext.Response.Headers["Content-Security-Policy"].ToString();
        Assert.Contains("sandbox allow-scripts", artifactPolicy);
        Assert.DoesNotContain("allow-same-origin", artifactPolicy);
        Assert.Contains("connect-src 'none'", artifactPolicy);
        Assert.Contains(
            "script-src 'self' 'unsafe-inline' data: blob:",
            artifactPolicy);
        Assert.Contains("form-action 'none'", artifactPolicy);
        Assert.Contains("frame-src 'none'", artifactPolicy);
        Assert.DoesNotContain("navigate-to", artifactPolicy);
        Assert.DoesNotContain("sandbox", viewPolicy);
        Assert.Contains("default-src 'none'", viewPolicy);
        Assert.Contains("style-src 'unsafe-inline'", viewPolicy);
        Assert.Contains("frame-src 'self'", viewPolicy);
        Assert.Contains("connect-src 'none'", viewPolicy);
        Assert.Contains("form-action 'none'", viewPolicy);
        Assert.Contains("object-src 'none'", viewPolicy);
        Assert.Contains("base-uri 'none'", viewPolicy);
        Assert.DoesNotContain("navigate-to", viewPolicy);
        Assert.Contains("frame-ancestors 'none'", viewPolicy);
        Assert.Equal(
            "*",
            artifactContext.Response.Headers["Access-Control-Allow-Origin"]);
        Assert.Equal(
            "cross-origin",
            artifactContext.Response.Headers["Cross-Origin-Resource-Policy"]);
        Assert.Equal(
            "noopener-allow-popups",
            artifactContext.Response.Headers["Cross-Origin-Opener-Policy"]);
        Assert.Equal(
            "noopener-allow-popups",
            viewContext.Response.Headers["Cross-Origin-Opener-Policy"]);
        Assert.Equal(
            "no-referrer",
            artifactContext.Response.Headers["Referrer-Policy"]);
        Assert.Equal(
            "no-referrer",
            viewContext.Response.Headers["Referrer-Policy"]);
        Assert.Equal(
            "nosniff",
            viewContext.Response.Headers["X-Content-Type-Options"]);
        Assert.Equal("no-store", viewContext.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task IsolatedPreviewView_RendersAnOpaqueScriptOnlyArtifactFrame()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services
        };
        context.Response.Body = new MemoryStream();
        var flowId = Guid.Parse("af12aca2-0275-4aea-8684-f21375dc3b15");

        var result = DemoApi.GetIsolatedPreviewView(
            flowId,
            "eu\"><script>alert(1)</script>",
            context);
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var document = await new StreamReader(context.Response.Body)
            .ReadToEndAsync();

        Assert.Equal("text/html; charset=utf-8", context.Response.ContentType);
        Assert.Contains(
            $"src=\"/api/previews/{flowId:D}/artifacts/eu%22%3E%3Cscript%3Ealert%281%29%3C%2Fscript%3E/index.html\"",
            document);
        Assert.Contains("sandbox=\"allow-scripts\"", document);
        Assert.Contains("referrerpolicy=\"no-referrer\"", document);
        Assert.DoesNotContain("allow-same-origin", document);
        Assert.DoesNotContain("allow-forms", document);
        Assert.DoesNotContain("allow-popups", document);
        Assert.DoesNotContain("allow-top-navigation", document);
        Assert.DoesNotContain("<script", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" onload=", document, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiscoverAndResolve_StayInsideGeneratedCustomerPreview()
    {
        var workspace = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-preview-{Guid.NewGuid():N}");
        var browserRoot = Path.Combine(workspace, ".customer-preview", "eu", "browser");
        Directory.CreateDirectory(Path.Combine(browserRoot, "assets"));
        File.WriteAllText(Path.Combine(browserRoot, "index.html"), "<h1>Devclub</h1>");
        File.WriteAllText(Path.Combine(browserRoot, "assets", "app.js"), "console.log('ok')");
        File.WriteAllText(Path.Combine(workspace, "secret.txt"), "not public");
        var flow = new FlowRun
        {
            Title = "Preview",
            OriginalRequest = "Preview",
            WorkspacePath = workspace
        };

        try
        {
            var catalog = new PreviewArtifactCatalog();
            var artifact = Assert.Single(catalog.Discover(flow));

            Assert.Equal("devclub.eu", artifact.Label);
            Assert.Equal(
                Path.Combine(browserRoot, "assets", "app.js"),
                catalog.ResolveFile(flow, "eu", "assets/app.js"));
            Assert.Throws<UnauthorizedAccessException>(() =>
                catalog.ResolveFile(flow, "eu", "../../secret.txt"));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}

public sealed class CopilotReasoningHostTests
{
    [Fact]
    public void ExecutionTimeouts_GiveMaximumQualityALongerQuietWindow()
    {
        var config = new CopilotConfig
        {
            TurnTimeoutMs = 1_200_000,
            StallTimeoutMs = 300_000,
            MaximumQualityStallTimeoutMs = 900_000
        };

        var quality = CopilotReasoningHost.ResolveExecutionTimeouts(
            config,
            ModelSelectionStrategy.MaximumQuality,
            expectedAcceptedTimeSeconds: 400);
        var fastest = CopilotReasoningHost.ResolveExecutionTimeouts(
            config,
            ModelSelectionStrategy.FastestResponse);

        Assert.Equal(TimeSpan.FromMinutes(10), quality.StallTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), fastest.StallTimeout);
        Assert.Equal(TimeSpan.FromMinutes(20), quality.TurnTimeout);
    }

    [Theory]
    [InlineData("software-engineer", "HANDOFF_STATUS: COMPLETE", true)]
    [InlineData("software-engineer", "Still working.", false)]
    public void RecoverableOutput_RequiresTheRolesTerminalContract(
        string role,
        string output,
        bool expected)
    {
        Assert.Equal(
            expected,
            CopilotReasoningHost.IsRecoverableCompletedOutput(role, output));
    }

    [Fact]
    public void StudioRecoverableOutput_DispatchesByInvocationKindNotAgentRole()
    {
        var confirmedIntake =
            IntakeParser.BeginSentinel +
            Environment.NewLine +
            IntakeParser.Serialize(new IntakeDocument
            {
                Status = IntakeStatus.Confirmed,
                FlowKind = FlowKind.Delivery,
                TaskTitle = "Implement recovery",
                CustomerReply = "The implementation is confirmed.",
                Brief = new IntakeBrief
                {
                    Goal = "Implement recovery.",
                    Details = ["Preserve invocation kind."],
                    SuccessCriteria = ["Recovery is deterministic."],
                    Constraints = [],
                    Assumptions = []
                }
            }) +
            Environment.NewLine +
            IntakeParser.EndSentinel;

        Assert.True(CopilotReasoningHost.IsRecoverableCompletedOutput(
            "account-manager",
            confirmedIntake,
            invocationKind: ExecutionInvocationKind.Intake));
        Assert.False(CopilotReasoningHost.IsRecoverableCompletedOutput(
            "account-manager",
            confirmedIntake,
            invocationKind:
                ExecutionInvocationKind.BlockerExplanation));
        Assert.False(CopilotReasoningHost.IsRecoverableCompletedOutput(
            "team-lead",
            "HANDOFF_STATUS: COMPLETE",
            invocationKind: ExecutionInvocationKind.Planning));
        Assert.False(CopilotReasoningHost.IsRecoverableCompletedOutput(
            "outcome-writer",
            "HANDOFF_STATUS: COMPLETE",
            invocationKind: ExecutionInvocationKind.Worker,
            isOutcomeOwner: true));
    }

    [Fact]
    public void AccountManagerInvocation_IsReadOnlyAndPreservesUserConfiguration()
    {
        var arguments = CopilotReasoningHost.BuildCliArguments(
            @"C:\worktree",
            @"C:\harness",
            "account-manager",
            ExecutionInvocationKind.Intake,
            "claude-sonnet-5",
            "low",
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "Prompt");

        Assert.Contains("--available-tools=view,grep,glob", arguments);
        Assert.Contains("--disable-builtin-mcps", arguments);
        Assert.Equal(
            ["--reasoning-effort", "low"],
            arguments
                .SkipWhile(argument => argument != "--reasoning-effort")
                .Take(2));
        Assert.Contains("--no-custom-instructions", arguments);
        Assert.Contains("--no-eager-powershell-resolution", arguments);
        Assert.DoesNotContain("--allow-all-tools", arguments);
        Assert.Contains(
            Enumerable.Range(0, arguments.Count - 1),
            index =>
                arguments[index] == "--add-dir" &&
                arguments[index + 1] == @"C:\worktree");
    }

    [Fact]
    public void PreMortemInvocation_EnforcesReadOnlyResearchTools()
    {
        var arguments = CopilotReasoningHost.BuildCliArguments(
            @"C:\worktree",
            @"C:\harness",
            "pre-mortem-sceptic",
            ExecutionInvocationKind.PreMortem,
            "gpt-5.6-sol",
            "max",
            Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"),
            "Investigate.");

        Assert.Contains(
            "--available-tools=view,grep,glob,web_fetch",
            arguments);
        Assert.Contains("--disable-builtin-mcps", arguments);
        Assert.Contains("--deny-tool=write,shell", arguments);
        Assert.Contains("--disallow-temp-dir", arguments);
        Assert.Contains("--no-custom-instructions", arguments);
        Assert.Contains("--no-eager-powershell-resolution", arguments);
        Assert.DoesNotContain("--allow-all-tools", arguments);
    }

    [Fact]
    public void SessionHome_UsesTheInheritedAuthenticatedCopilotHome()
    {
        Assert.Equal(
            Path.GetFullPath(@"C:\authenticated-copilot-home"),
            CopilotReasoningHost.ResolveCopilotSessionHome(
                @"C:\authenticated-copilot-home"));
    }

    [Fact]
    public void DeliveryAgentInvocation_UsesExplicitToolsAndDisablesExtensions()
    {
        var arguments = CopilotReasoningHost.BuildCliArguments(
            @"C:\worktree",
            @"C:\harness",
            "software-engineer",
            ExecutionInvocationKind.Worker,
            "gpt-5.4-mini",
            "medium",
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            "Prompt");

        Assert.DoesNotContain("--allow-all-tools", arguments);
        Assert.Contains(
            $"--available-tools={CopilotReasoningHost.GovernedNonPublicationTools()}",
            arguments);
        Assert.Contains(
            Enumerable.Range(0, arguments.Count - 1),
            index =>
                arguments[index] == "--add-dir" &&
                arguments[index + 1] == @"C:\worktree");
        Assert.DoesNotContain("--allow-all-paths", arguments);
        Assert.DoesNotContain("--allow-all", arguments);
        Assert.Contains(
            Enumerable.Range(0, arguments.Count - 1),
            index =>
                arguments[index] == "--session-id" &&
                arguments[index + 1] == "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        Assert.Contains("--disable-builtin-mcps", arguments);
        Assert.Contains("--no-custom-instructions", arguments);
    }

    [Fact]
    public void ModelWithoutConfigurableEffort_OmitsTheEffortArgument()
    {
        var arguments = CopilotReasoningHost.BuildCliArguments(
            @"C:\worktree",
            @"C:\harness",
            "software-engineer",
            ExecutionInvocationKind.Worker,
            "model-with-default-effort",
            "default",
            Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"),
            "Prompt");

        Assert.DoesNotContain("--reasoning-effort", arguments);
    }

    [Fact]
    public void PreApprovalRelease_BlocksRemotePublicationCredentials()
    {
        var guarded = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(
            CopilotReasoningHost.BuildProcessEnvironment(
                ExecutionInvocationKind.Worker,
                allowRemotePublication: false));

        Assert.Null(guarded["GH_TOKEN"]);
        Assert.Null(guarded["GITHUB_TOKEN"]);
        Assert.Equal("0", guarded["GIT_TERMINAL_PROMPT"]);
        Assert.Equal(string.Empty, guarded["GIT_CONFIG_VALUE_1"]);
        Assert.Equal("remote.origin.pushurl", guarded["GIT_CONFIG_KEY_0"]);
        Assert.Null(CopilotReasoningHost.BuildProcessEnvironment(
            ExecutionInvocationKind.Publication,
            allowRemotePublication: true));
        Assert.NotNull(CopilotReasoningHost.BuildProcessEnvironment(
            ExecutionInvocationKind.Worker,
            allowRemotePublication: false));
    }

    [Theory]
    [InlineData("team-lead")]
    [InlineData("architect")]
    [InlineData("software-engineer")]
    [InlineData("quality-engineer")]
    [InlineData("product-manager")]
    public void GovernedNonPublisherTurns_UseExplicitSafeToolPolicyAndCredentials(
        string role)
    {
        var environment =
            Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(
                CopilotReasoningHost.BuildProcessEnvironment(
                    ExecutionInvocationKind.Worker,
                    allowRemotePublication: false));
        var arguments = CopilotReasoningHost.BuildCliArguments(
            @"C:\worktree",
            @"C:\harness",
            role,
            ExecutionInvocationKind.Worker,
            "model",
            "high",
            Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"),
            "Execute the governed turn.",
            blockRemotePublication: true);

        Assert.Null(environment["GH_TOKEN"]);
        Assert.Null(environment["GITHUB_TOKEN"]);
        Assert.Null(environment["SSH_AUTH_SOCK"]);
        Assert.Equal("0", environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal(
            "disabled://publication-not-authorized",
            environment["GIT_CONFIG_VALUE_0"]);
        AssertGovernedNonPublicationToolPolicy(arguments);
    }

    [Fact]
    public void GovernedCorrectionTurn_UsesExplicitSafeToolPolicy()
    {
        var arguments = BuildGovernedArguments(
            "software-engineer",
            "Revise this role's output after QA feedback.");

        AssertGovernedNonPublicationToolPolicy(arguments);
    }

    [Fact]
    public void GovernedCandidatePreparation_UsesExplicitSafeToolPolicy()
    {
        var arguments = BuildGovernedArguments(
            "release-engineer",
            "Prepare the local candidate.");

        AssertGovernedNonPublicationToolPolicy(arguments);
    }

    [Fact]
    public void GovernedCandidateRefresh_UsesExplicitSafeToolPolicy()
    {
        var arguments = BuildGovernedArguments(
            "release-engineer",
            "Refresh the complete local candidate after corrections.");

        AssertGovernedNonPublicationToolPolicy(arguments);
    }

    [Fact]
    public void GovernedQaTurn_UsesExplicitSafeToolPolicy()
    {
        var arguments = BuildGovernedArguments(
            "quality-engineer",
            "Verify the current candidate.");

        AssertGovernedNonPublicationToolPolicy(arguments);
    }

    [Fact]
    public void GovernedProductManagerFeedback_UsesExplicitSafeToolPolicy()
    {
        var arguments = BuildGovernedArguments(
            "product-manager",
            "Interpret customer feedback against the verified candidate.");

        AssertGovernedNonPublicationToolPolicy(arguments);
    }

    [Fact]
    public void HostControlledPublication_DisablesShellAndWriteTools()
    {
        var arguments = CopilotReasoningHost.BuildCliArguments(
            @"C:\worktree",
            @"C:\harness",
            "release-engineer",
            ExecutionInvocationKind.Worker,
            "model",
            "high",
            Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"),
            "Prepare the publication narrative.",
            isHostControlledPublication: true,
            blockRemotePublication: true);

        Assert.Contains("--available-tools=view,grep,glob", arguments);
        Assert.Contains("--disable-builtin-mcps", arguments);
        Assert.Contains("--deny-tool=write,shell", arguments);
        Assert.Contains("--deny-tool=shell(git push)", arguments);
        Assert.Contains("--deny-tool=shell(gh:*)", arguments);
        Assert.DoesNotContain("--allow-all-tools", arguments);
    }

    [Fact]
    public void CandidatePreparation_DeniesEveryGitPushTarget()
    {
        var arguments = CopilotReasoningHost.BuildCliArguments(
            @"C:\worktree",
            @"C:\harness",
            "release-engineer",
            ExecutionInvocationKind.Worker,
            "model",
            "high",
            Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"),
            "Prepare the local candidate.",
            blockRemotePublication: true);

        Assert.DoesNotContain("--allow-all-tools", arguments);
        Assert.Contains(
            $"--available-tools={CopilotReasoningHost.GovernedNonPublicationTools()}",
            arguments);
        Assert.Contains("--deny-tool=shell(git push)", arguments);
        Assert.Contains("--deny-tool=shell(git send-pack)", arguments);
        Assert.Contains("--deny-tool=shell(gh:*)", arguments);
        Assert.Contains("--deny-url=github.com", arguments);
    }

    [Fact]
    public void PreMortemEnvironment_RemovesPublicationCredentials()
    {
        var guarded = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(
            CopilotReasoningHost.BuildProcessEnvironment(
                ExecutionInvocationKind.PreMortem,
                allowRemotePublication: false));

        Assert.Null(guarded["GH_TOKEN"]);
        Assert.Null(guarded["GITHUB_TOKEN"]);
        Assert.Equal(
            "disabled://pre-mortem-read-only",
            guarded["GIT_CONFIG_VALUE_0"]);
    }

    private static IReadOnlyList<string> BuildGovernedArguments(
        string role,
        string prompt) =>
        CopilotReasoningHost.BuildCliArguments(
            @"C:\worktree",
            @"C:\harness",
            role,
            ExecutionInvocationKind.Worker,
            "model",
            "high",
            Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff"),
            prompt,
            blockRemotePublication: true);

    private static void AssertGovernedNonPublicationToolPolicy(
        IReadOnlyList<string> arguments)
    {
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
        Assert.Contains("--deny-tool=shell(scp:*)", arguments);
        Assert.Contains("--deny-tool=shell(curl:*)", arguments);
        Assert.Contains("--deny-tool=shell(wget:*)", arguments);
        Assert.Contains("--deny-tool=shell(Invoke-WebRequest:*)", arguments);
        Assert.Contains("--deny-tool=shell(Invoke-RestMethod:*)", arguments);
        Assert.Contains("--deny-url=github.com", arguments);
        Assert.Contains("--deny-url=api.github.com", arguments);
        Assert.Contains("--no-remote", arguments);
        Assert.Contains("--no-remote-export", arguments);
        foreach (var readOnlyCommand in new[]
                 {
                     "status",
                     "diff",
                     "show",
                     "log",
                     "rev-parse",
                     "ls-files",
                     "cat-file"
                 })
        {
            Assert.DoesNotContain(
                $"--deny-tool=shell(git {readOnlyCommand})",
                arguments);
            Assert.DoesNotContain(
                $"--deny-tool=shell(git.exe {readOnlyCommand})",
                arguments);
        }
    }

    [Fact]
    public async Task PreMortemAgentDefinition_IsStagedOutsideTheHarnessRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"restricted-agent-{Guid.NewGuid():N}");
        var harnessRoot = Path.Combine(root, "harness");
        var copilotHome = Path.Combine(root, "copilot");
        Directory.CreateDirectory(harnessRoot);

        try
        {
            var access = await new AgentManifestStager().StageAsync(
                copilotHome,
                new AgentManifest(
                    "pre-mortem-sceptic",
                    "Pre-mortem Sceptic",
                    "Independent review.",
                    "pre-mortem-sceptic",
                    "rose",
                    85,
                    "ignored.agent.md",
                    "# Pre-mortem Sceptic"),
                Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"));
            var files = Directory.GetFiles(
                access.Root,
                "*",
                SearchOption.AllDirectories);

            Assert.StartsWith(Path.GetFullPath(copilotHome), access.Root);
            Assert.DoesNotContain(Path.GetFullPath(harnessRoot), access.Root);
            Assert.Single(files);
            Assert.EndsWith(
                $"{access.AgentId}.agent.md",
                files[0],
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StagedAgentDefinition_RebuildsSafeFrontmatter()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"safe-agent-{Guid.NewGuid():N}");
        try
        {
            var access = await new AgentManifestStager().StageAsync(
                root,
                new AgentManifest(
                    "crafted",
                    "Crafted",
                    "Safe description",
                    "worker",
                    "blue",
                    10,
                    "ignored.agent.md",
                    "tools: ['shell']\nmcps: ['publisher']\n# Contract"),
                Guid.NewGuid());
            var content = await File.ReadAllTextAsync(
                Directory.GetFiles(
                    access.Root,
                    "*.agent.md",
                    SearchOption.AllDirectories).Single());

            Assert.DoesNotContain("model:", content);
            Assert.Equal(2, content.Split("---").Length - 1);
            Assert.StartsWith("---\nname: \"Crafted\"\ndescription: \"Safe description\"\n---", content.ReplaceLineEndings("\n"));
            Assert.Contains("# Contract", content);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RevisionJournalRecovery_RequiresTheRevisionContract()
    {
        const string priorHandoff = "HANDOFF_STATUS: COMPLETE";
        const string revision = """
            HANDOFF_STATUS: COMPLETE
            PRE_MORTEM_DISPOSITION: ADJUSTED
            """;

        Assert.False(CopilotReasoningHost.IsRecoverableCompletedOutput(
            "software-engineer",
            priorHandoff,
            isPreMortemRevision: true));
        Assert.True(CopilotReasoningHost.IsRecoverableCompletedOutput(
            "software-engineer",
            revision,
            isPreMortemRevision: true));
    }

    [Fact]
    public void ResumedJournalRecovery_RequiresOutputFromTheCurrentInvocation()
    {
        var invocationStartedAt = DateTimeOffset.UtcNow;

        Assert.False(CopilotReasoningHost.IsRecoveryCurrent(
            invocationStartedAt,
            invocationStartedAt.AddSeconds(-1)));
        Assert.True(CopilotReasoningHost.IsRecoveryCurrent(
            invocationStartedAt,
            invocationStartedAt.AddSeconds(1)));
    }

    [Fact]
    public void InterruptedInvocation_ExplicitlyResumesTheExistingSession()
    {
        var sessionId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
        var prompt = CopilotReasoningHost.RestartContinuationPrompt("Original prompt.");

        var arguments = CopilotReasoningHost.BuildCliArguments(
            @"C:\worktree",
            @"C:\harness",
            "software-engineer",
            ExecutionInvocationKind.Worker,
            "gpt-5.4-mini",
            "high",
            sessionId,
            prompt,
            resumeSession: true);

        Assert.Contains($"--resume={sessionId:D}", arguments);
        Assert.DoesNotContain("--session-id", arguments);
        Assert.Contains("Original prompt.", prompt);
        Assert.StartsWith(
            "Resume from the current workspace; inspect existing changes before continuing.",
            prompt);
        Assert.DoesNotContain("harness restarted", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepositoryKnowledge_DoesNotRedirectAgentsToTheOriginalSourcePath()
    {
        var knowledge = """
            # demo

            ## Repository profile
            - **Location:** `E:\source-project`
            - **Detected stack:** Angular
            """;

        var prepared = CopilotReasoningHost.PrepareRepositoryKnowledge(
            knowledge,
            @"E:\source-project",
            @"E:\worktrees\flow-123");

        Assert.DoesNotContain(@"E:\source-project", prepared);
        Assert.Contains(@"E:\worktrees\flow-123", prepared);
        Assert.Contains("isolated workspace", prepared);
        Assert.Contains("Angular", prepared);
    }

    [Fact]
    public void PromptValues_PreserveCompleteReviewedRepositoryKnowledge()
    {
        var context = new AgentExecutionContext(
            Guid.NewGuid(),
            1,
            "software-engineer",
            "Software Engineer",
            "software-engineer",
            "gpt-5.4-mini",
            "medium",
            1,
            "Implement the approved visual refresh.",
            """
            # demo

            ## Repository profile
            - **Project files:** Materialized into a per-flow isolated workspace before agent execution.
            - **Source files studied:** 1,018
            - **Primary file types:** .jpg (515), .ts (83)
            - **Detected stack:** Angular, Node.js

            ## Likely developer commands
            - `npm test`

            ## AI initialization
            Copilot CLI initialized repository instructions successfully.

            ## README signal
            Long README details that agents can inspect from the workspace.

            ## Installation
            This embedded README section must not leak into the prompt.

            ## Operations
            Neither should this one.

            ## Editable harness notes
            Add domain language, architectural constraints, release rules, and quality expectations here. Every agent receives this shared context.
            """,
            @"E:\source-project",
            @"E:\worktrees\flow-123",
            Guid.NewGuid(),
            AiHarnessDemo.Core.Domain.OutcomeType.PullRequest,
            "Team Lead -> Architect -> Product Designer -> Software Engineer",
            [],
            [],
            Progress: null,
            StudioDependencyOutputs:
            [
                new StudioDependencyOutput(
                    "architecture",
                    "architect",
                    StudioDependencyKind.Ancestor,
                    2,
                    1,
                    20,
                    "Architect handoff"),
                new StudioDependencyOutput(
                    "design",
                    "product-designer",
                    StudioDependencyKind.Direct,
                    1,
                    1,
                    30,
                    "Product Designer handoff")
            ]);

        var values = CopilotReasoningHost.BuildPromptValues(
            context,
            "Deliver working code.",
            context.WorkspacePath);
        var prompt = new WorkflowPromptRenderer().Render(
            """
            You are {{ agent.name }}.
            ## Assignment
            {{ task }}
            ## Role contract
            {{ agent.instructions }}
            ## Workspace
            {{ workspace }}
            {{ role.context }}
            ## Completion contract
            {{ response.contract }}
            """,
            values);

        Assert.Contains("Implement the approved visual refresh", prompt);
        Assert.Contains(@"E:\worktrees\flow-123", prompt);
        Assert.Contains("Angular, Node.js", prompt);
        Assert.Contains(CopilotReasoningHost.RepositoryKnowledgeBegin, prompt);
        Assert.Contains(CopilotReasoningHost.RepositoryKnowledgeEnd, prompt);
        Assert.Contains("Long README details", prompt);
        Assert.Contains("Source files studied", prompt);
        Assert.Contains("AI initialization", prompt);
        Assert.Contains("README signal", prompt);
        Assert.Contains("Installation", prompt);
        Assert.Contains("Operations", prompt);
        Assert.Contains("Editable harness notes", prompt);
        Assert.Contains("Architect handoff", prompt);
        Assert.Contains("Product Designer handoff", prompt);
        Assert.DoesNotContain("Team Lead handoff", prompt);
        Assert.DoesNotContain("Team plan", prompt);
        Assert.DoesNotContain("No prior", prompt);
        Assert.DoesNotContain(@"E:\source-project", prompt);
    }

    [Fact]
    public void PromptValues_DoNotClipRepositoryKnowledgeByRole()
    {
        var requiredTail = "REPOSITORY-KNOWLEDGE-TAIL";
        var knowledge = $"{new string('k', 20_000)}{requiredTail}";
        var context = new AgentExecutionContext(
            Guid.NewGuid(),
            1,
            "account-manager",
            "Account Manager",
            "account-manager",
            "gpt-5.4-mini",
            "medium",
            1,
            "Clarify the request.",
            knowledge,
            @"E:\source-project",
            @"E:\worktrees\flow-123",
            Guid.NewGuid(),
            AiHarnessDemo.Core.Domain.OutcomeType.PullRequest,
            "Confirm the request.",
            [],
            [],
            Progress: null);

        var values = CopilotReasoningHost.BuildPromptValues(
            context,
            "Return the intake contract.",
            context.WorkspacePath);
        var rendered = new WorkflowPromptRenderer().Render(
            "{{ task }}\n\n{{ role.context }}\n\n{{ agent.instructions }}",
            values);
        var bounded = CopilotReasoningHost.BoundRenderedPrompt(
            context,
            rendered);

        Assert.Contains(requiredTail, values["role.context"]);
        Assert.Contains(requiredTail, bounded);
        Assert.Contains(knowledge, bounded);
    }

    [Fact]
    public void PreMortemPrompt_PreservesRepositoryKnowledgeDuringCompaction()
    {
        const string requiredTail = "REPOSITORY-KNOWLEDGE-TAIL";
        var knowledge = $"{new string('k', 6_300)}{requiredTail}";
        var context = new AgentExecutionContext(
            Guid.NewGuid(),
            1,
            "pre-mortem-sceptic",
            "Pre-mortem Sceptic",
            "pre-mortem-sceptic",
            "gpt-5.6-sol",
            "max",
            1,
            new string('t', 8_525),
            knowledge,
            @"E:\source-project",
            @"E:\worktrees\flow-123",
            Guid.NewGuid(),
            AiHarnessDemo.Core.Domain.OutcomeType.PullRequest,
            "Review the architecture.",
            [],
            [],
            InvocationKind: ExecutionInvocationKind.PreMortem);
        var values = CopilotReasoningHost.BuildPromptValues(
            context,
            "Investigate the evaluated result independently.",
            context.WorkspacePath);
        var rendered = new WorkflowPromptRenderer().Render(
            "{{ task }}\n\n{{ agent.instructions }}\n\n{{ workspace }}\n\n" +
            "{{ role.context }}\n\n" +
            new string('p', 12_000) +
            "\n\n{{ response.contract }}",
            values);

        var bounded = CopilotReasoningHost.BoundRenderedPrompt(
            context,
            rendered);
        var repositoryKnowledgeBlock =
            CopilotReasoningHost.BuildRepositoryKnowledgeBlock(
                knowledge,
                context.SourceProjectPath);

        Assert.Equal(
            CopilotReasoningHost.ResolveMaximumRenderedPromptCharacters(
                context),
            bounded.Length);
        Assert.Contains(repositoryKnowledgeBlock, bounded);
        Assert.Contains(requiredTail, bounded);
        Assert.Contains(context.Task, bounded);
        Assert.Contains(
            "...[prompt context compacted]...",
            bounded,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StudioWorkerPrompt_PreservesKnowledgeAndDependenciesDuringCompaction()
    {
        var knowledge = $"{new string('k', 6_000)}knowledge-tail";
        var context = new AgentExecutionContext(
            Guid.NewGuid(),
            1,
            "software-engineer",
            "Software Engineer",
            "software-engineer",
            "gpt-5.6-sol",
            "high",
            1,
            new string('t', 6_000),
            knowledge,
            @"E:\source-project",
            @"E:\worktrees\flow-123",
            Guid.NewGuid(),
            AiHarnessDemo.Core.Domain.OutcomeType.PullRequest,
            "Implement the redesign.",
            [],
            [],
            InvocationKind: ExecutionInvocationKind.Worker,
            StudioDependencyOutputs:
            [
                new StudioDependencyOutput(
                    "approved-architecture",
                    "architect",
                    StudioDependencyKind.Direct,
                    1,
                    1,
                    1,
                    new string('d', 900))
            ]);
        var values = CopilotReasoningHost.BuildPromptValues(
            context,
            "Implement the approved architecture.",
            context.WorkspacePath);
        var rendered = new WorkflowPromptRenderer().Render(
            "{{ task }}\n\n{{ role.context }}\n\n" +
            new string('p', 20_000) +
            "\n\n{{ response.contract }}",
            values);

        var bounded = CopilotReasoningHost.BoundRenderedPrompt(
            context,
            rendered);
        var repositoryKnowledgeBlock =
            CopilotReasoningHost.BuildRepositoryKnowledgeBlock(
                knowledge,
                context.SourceProjectPath);

        Assert.Equal(
            CopilotReasoningHost.ResolveMaximumRenderedPromptCharacters(
                context),
            bounded.Length);
        Assert.Contains(repositoryKnowledgeBlock, bounded);
        Assert.Contains(
            CopilotReasoningHost.StudioPlanContextBegin,
            bounded,
            StringComparison.Ordinal);
        Assert.Contains(
            CopilotReasoningHost.StudioPlanContextEnd,
            bounded,
            StringComparison.Ordinal);
        Assert.Contains(
            "approved-architecture",
            bounded,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StudioDeliveryVerificationPrompt_PreservesTheCompleteStructuredContract()
    {
        var task =
            "Confirmed Delivery brief:" +
            Environment.NewLine +
            new string('t', 20_000) +
            Environment.NewLine +
            "Host recovery context: inspect the preserved workspace state before continuing.";
        var outcomeContext = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, 6).Select(index =>
                $"- AC-{index:000}: complete requirement {index} | verification: complete check {index}"));
        const string outcomeContract =
            "OUTCOME_QA_BEGIN\nReturn all AC-001 through AC-006.\nOUTCOME_QA_END";
        var context = new AgentExecutionContext(
            Guid.NewGuid(),
            1,
            "quality-engineer",
            "Quality Engineer",
            "quality-engineer",
            "claude-sonnet-5",
            "max",
            4,
            task,
            new string('k', 6_000),
            @"E:\source-project",
            @"E:\worktrees\flow-123",
            Guid.NewGuid(),
            AiHarnessDemo.Core.Domain.OutcomeType.PullRequest,
            "Verify the recovered candidate.",
            [],
            [],
            OutcomeContext: outcomeContext,
            OutcomeContract: outcomeContract,
            InvocationKind: ExecutionInvocationKind.Worker,
            RequiresDeliveryReadinessQa: true);
        var values = CopilotReasoningHost.BuildPromptValues(
            context,
            "Verify without modifying the workspace.",
            context.WorkspacePath);
        var rendered = new WorkflowPromptRenderer().Render(
            "{{ task }}\n\n{{ agent.instructions }}\n\n{{ role.context }}\n\n" +
            new string('p', 20_000) +
            "\n\n{{ outcome.context }}\n\n{{ outcome.contract }}\n\n" +
            "{{ response.contract }}",
            values);

        var bounded = CopilotReasoningHost.BoundRenderedPrompt(
            context,
            rendered);

        Assert.True(rendered.Length > 16_000);
        Assert.Equal(task, values["task"]);
        Assert.Equal(rendered, bounded);
        Assert.Contains("AC-006: complete requirement 6", bounded);
        Assert.Contains(outcomeContract, bounded);
    }

    [Theory]
    [InlineData("product-manager", ExecutionInvocationKind.Worker, false)]
    [InlineData("pre-mortem-sceptic", ExecutionInvocationKind.PreMortem, false)]
    [InlineData("software-engineer", ExecutionInvocationKind.Worker, true)]
    public void PromptValues_IncludeRepositoryKnowledgeForSpecialRoles(
        string role,
        ExecutionInvocationKind invocationKind,
        bool isPreMortemRevision)
    {
        var context = new AgentExecutionContext(
            Guid.NewGuid(),
            1,
            role,
            role,
            role,
            "gpt-5.4-mini",
            "medium",
            1,
            "Complete the assignment.",
            "Reviewed repository constraint.",
            @"E:\source-project",
            @"E:\worktrees\flow-123",
            Guid.NewGuid(),
            AiHarnessDemo.Core.Domain.OutcomeType.PullRequest,
            "Complete the assignment.",
            [],
            [],
            IsPreMortemRevision: isPreMortemRevision,
            InvocationKind: invocationKind);

        var values = CopilotReasoningHost.BuildPromptValues(
            context,
            "Complete the role contract.",
            context.WorkspacePath);

        Assert.Contains(
            "Reviewed repository constraint.",
            values["role.context"]);
    }

    [Fact]
    public void DirectPrompt_IncludesCompleteReviewedRepositoryKnowledge()
    {
        var knowledge = $"Architecture decisions.{Environment.NewLine}Required final detail.";
        var context = new AgentExecutionContext(
            Guid.NewGuid(),
            1,
            "quality-engineer",
            "Quality Engineer",
            "quality-engineer",
            "gpt-5.4-mini",
            "medium",
            1,
            "Verify the result.",
            knowledge,
            @"E:\source-project",
            @"E:\worktrees\flow-123",
            Guid.NewGuid(),
            AiHarnessDemo.Core.Domain.OutcomeType.PullRequest,
            "Verify the result.",
            [],
            [],
            DirectPrompt: "Run the governed verification.");

        var prompt = CopilotReasoningHost.BuildDirectPrompt(
            context,
            "Return the QA contract.",
            context.WorkspacePath);

        Assert.Contains(CopilotReasoningHost.RepositoryKnowledgeBegin, prompt);
        Assert.Contains("Architecture decisions.", prompt);
        Assert.Contains("Required final detail.", prompt);
        Assert.Contains(CopilotReasoningHost.RepositoryKnowledgeEnd, prompt);
    }

    [Fact]
    public void StudioPublication_RequiresPostImplementationKnowledgeRecap()
    {
        var context = new AgentExecutionContext(
            Guid.NewGuid(),
            1,
            "release-engineer",
            "Release Engineer",
            "release-engineer",
            "gpt-5.4-mini",
            "medium",
            1,
            WorkflowEngine.HostControlledPublicationAssignment,
            "Reviewed repository knowledge.",
            @"E:\source-project",
            @"E:\worktrees\flow-123",
            Guid.NewGuid(),
            AiHarnessDemo.Core.Domain.OutcomeType.PullRequest,
            "Publish the accepted result.",
            [],
            [],
            InvocationKind: ExecutionInvocationKind.Publication);
        var promptValues = CopilotReasoningHost.BuildPromptValues(
            context,
            "Publish the accepted result.",
            context.WorkspacePath);
        const string missingRecap = """
            HANDOFF_STATUS: COMPLETE

            ## Decision
            Publication prepared.
            """;
        var validRecap =
            "HANDOFF_STATUS: COMPLETE" +
            Environment.NewLine +
            RepositoryKnowledgeSynthesizer.RecapBeginSentinel +
            Environment.NewLine +
            """{"Changed":false,"Reason":"The accepted change does not alter durable repository knowledge.","Knowledge":null}""" +
            Environment.NewLine +
            RepositoryKnowledgeSynthesizer.RecapEndSentinel;

        var error = WorkflowEngine.GetStudioContractCorrectionReason(
            context,
            missingRecap);

        Assert.Contains(
            RepositoryKnowledgeSynthesizer.RecapBeginSentinel,
            error);
        Assert.Contains(
            RepositoryKnowledgeSynthesizer.RecapBeginSentinel,
            promptValues["response.contract"]);
        Assert.Contains(
            "Reason must be nonempty and at most 500 characters",
            promptValues["response.contract"]);
        Assert.Null(
            WorkflowEngine.GetStudioContractCorrectionReason(
                context,
                validRecap));
        Assert.True(
            CopilotReasoningHost.IsRecoverableCompletedOutput(
                context.AgentRole,
                validRecap,
                invocationKind: context.InvocationKind));
    }

    [Fact]
    public void ProductManagerLedger_PreservesEveryStepWithinItsPromptBudget()
    {
        var outputs = Enumerable.Range(1, 9)
            .Select(index =>
                $"Agent {index} (role-{index}){Environment.NewLine}" +
                $"Evidence {index}: {new string((char)('a' + index), 1_200)}")
            .ToList();

        var ledger = CopilotReasoningHost.CompactExecutionLedger(
            outputs,
            string.Empty,
            6_000);

        Assert.InRange(ledger.Length, 1, 6_000);
        foreach (var index in Enumerable.Range(1, 9))
        {
            Assert.Contains($"Agent {index} (role-{index})", ledger);
            Assert.Contains($"Evidence {index}", ledger);
        }
    }

    [Fact]
    public void PriorHandoffs_CannotRedirectLaterAgentsToTheSourceFolder()
    {
        const string handoff =
            @"Inspect E:\source-project first, then make changes in E:\SOURCE-PROJECT\site.";

        var prepared = CopilotReasoningHost.RemoveSourceProjectPath(
            handoff,
            @"E:\source-project");

        Assert.DoesNotContain(@"E:\source-project", prepared, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, prepared.Split("intentionally unavailable").Length - 1);
    }

    [Fact]
    public void DeliveryContract_RequiresAnUnambiguousHandoffStatus()
    {
        var contract = CopilotReasoningHost.ResponseContract("quality-engineer");

        Assert.Contains("HANDOFF_STATUS: COMPLETE", contract);
        Assert.Contains("HANDOFF_STATUS: PUSHBACK", contract);
        Assert.Contains("PUSHBACK_REASON", contract);
        Assert.DoesNotContain("harness will resume", contract, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreMortemRevisionContract_RequiresCompleteAndDisposition()
    {
        var contract = CopilotReasoningHost.PreMortemRevisionResponseContract();

        Assert.Contains("HANDOFF_STATUS: COMPLETE", contract);
        Assert.Contains("PRE_MORTEM_DISPOSITION: ADJUSTED", contract);
        Assert.Contains("PRE_MORTEM_DISPOSITION: UNCHANGED", contract);
        Assert.Contains("Do not emit HANDOFF_STATUS: PUSHBACK", contract);
    }
}

public sealed class AgentRunnerRecoveryTests
{
    [Theory]
    [InlineData(AgentRunFailureKind.Stalled, true, true)]
    [InlineData(AgentRunFailureKind.TimedOut, true, true)]
    [InlineData(AgentRunFailureKind.Stalled, false, false)]
    [InlineData(AgentRunFailureKind.Transient, true, false)]
    public void ResumeRequiresAnInterruptedMaterializedSession(
        AgentRunFailureKind failureKind,
        bool canResume,
        bool expected)
    {
        var exception = new AgentRunException(
            "test",
            failureKind,
            canResumeSession: canResume);

        Assert.Equal(
            expected,
            AgentRunner.ShouldResumeInterruptedSession(exception));
    }
}

public sealed class PushbackRecoveryTests
{
    [Fact]
    public void AgentSessionIdentity_IsStablePerFlowIterationAndAgent()
    {
        var flowId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var first = AgentSessionIdentity.Create(flowId, 2, "software-engineer");
        var resumed = AgentSessionIdentity.Create(flowId, 2, "software-engineer");

        Assert.Equal(first, resumed);
        Assert.NotEqual(first, AgentSessionIdentity.Create(flowId, 2, "quality-engineer"));
        Assert.NotEqual(first, AgentSessionIdentity.Create(flowId, 3, "software-engineer"));
        Assert.Equal('5', first.ToString("D")[14]);
        Assert.Contains(first.ToString("D")[19], "89ab");
    }

    [Theory]
    [InlineData(1, 2, true)]
    [InlineData(2, 2, true)]
    [InlineData(3, 2, false)]
    [InlineData(1, 0, false)]
    public void HandoffRetryLimit_IsBounded(
        int observedPushbacks,
        int configuredRetries,
        bool expected)
    {
        Assert.Equal(
            expected,
            WorkflowEngine.HasHandoffRetryAvailable(
                observedPushbacks,
                configuredRetries));
    }

    [Fact]
    public void RecoverySteps_ResumeUpstreamThenRetryBlockedAgent()
    {
        var flow = new FlowRun
        {
            Title = "Refresh site",
            OriginalRequest = "Refresh site",
            Iteration = 1
        };
        var blocked = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = 1,
            Sequence = 50,
            AgentId = "quality-engineer",
            AgentName = "Quality Engineer",
            AgentRole = "quality-engineer",
            Status = StepStatus.Pushback,
            Attempt = 1,
            PushbackReason = "The implementation handoff has no test mapping.",
            OutputSummary = "PUSHBACK: Add acceptance-to-test evidence."
        };
        var upstream = new AgentRecord
        {
            Id = "software-engineer",
            Name = "Software Engineer",
            Description = "Implements changes.",
            Role = "software-engineer",
            SourcePath = "software-engineer.agent.md"
        };
        var upstreamStep = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Sequence = 40,
            AgentId = upstream.Id,
            AgentName = upstream.Name,
            AgentRole = upstream.Role,
            PlanStepKey = "implement",
            PlanDutiesJson = """["Implement"]""",
            Status = StepStatus.Completed
        };

        var (revision, retry) = WorkflowEngine.CreateRecoverySteps(
            flow,
            blocked,
            upstream,
            revisionAttempt: 2,
            upstreamStep);

        Assert.Equal("software-engineer", revision.AgentId);
        Assert.Equal(60, revision.Sequence);
        Assert.Equal(2, revision.Attempt);
        Assert.Contains(blocked.PushbackReason, revision.InputSummary);
        Assert.Equal("quality-engineer", retry.AgentId);
        Assert.Equal(70, retry.Sequence);
        Assert.Equal(2, retry.Attempt);
        Assert.Equal(blocked.Id, retry.RetryOfStepId);
        Assert.Equal(revision.Id, retry.DependsOnStepId);
        Assert.Equal(blocked.Id, retry.PushbackRootStepId);
    }

    [Fact]
    public void PushbackRetry_PreservesManualRetryLineage()
    {
        var rootFailureId = Guid.NewGuid();
        var flow = new FlowRun
        {
            Title = "Recover feature",
            OriginalRequest = "Recover feature",
            Iteration = 1
        };
        var blocked = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = 1,
            Sequence = 50,
            AgentId = "quality-engineer",
            AgentName = "Quality Engineer",
            AgentRole = "quality-engineer",
            Status = StepStatus.Pushback,
            Attempt = 2,
            RetryOfStepId = rootFailureId,
            PushbackReason = "Evidence is incomplete."
        };
        var upstream = new AgentRecord
        {
            Id = "software-engineer",
            Name = "Software Engineer",
            Description = "Implements changes.",
            Role = "software-engineer",
            SourcePath = "software-engineer.agent.md"
        };
        var upstreamStep = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Sequence = 40,
            AgentId = upstream.Id,
            AgentName = upstream.Name,
            AgentRole = upstream.Role,
            PlanStepKey = "implement",
            PlanDutiesJson = """["Implement"]""",
            Status = StepStatus.Completed
        };

        var (_, retry) = WorkflowEngine.CreateRecoverySteps(
            flow,
            blocked,
            upstream,
            revisionAttempt: 3,
            upstreamStep);

        Assert.Equal(rootFailureId, retry.RetryOfStepId);
    }

    [Fact]
    public void PushbackLearning_TargetsTheResponsibleUpstreamAgent()
    {
        var flow = new FlowRun
        {
            Title = "Refresh site",
            OriginalRequest = "Refresh site"
        };
        var blocked = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = 1,
            Sequence = 50,
            AgentId = "quality-engineer",
            AgentName = "Quality Engineer",
            AgentRole = "quality-engineer",
            PushbackReason = "No acceptance-to-test mapping."
        };
        var upstream = new AgentRecord
        {
            Id = "software-engineer",
            Name = "Software Engineer",
            Description = "Implements changes.",
            Role = "software-engineer",
            SourcePath = "software-engineer.agent.md"
        };

        var learning = WorkflowEngine.CreatePushbackLearning(
            flow,
            blocked,
            upstream);

        Assert.Equal("software-engineer", learning.AgentId);
        Assert.Equal("Handoff pushback", learning.Category);
        Assert.Contains("No acceptance-to-test mapping", learning.PromptRefinement);
        Assert.Contains("Quality Engineer", learning.PromptRefinement);
    }

    [Fact]
    public void StepTask_IncludesTheCorrectiveTurnMessage()
    {
        var task = WorkflowEngine.BuildStepTask(
            "Implement the approved change.",
            "Quality Engineer cannot continue; add exact validation evidence.");

        Assert.Contains("Implement the approved change", task);
        Assert.Contains("Role-specific assignment", task);
        Assert.Contains("Quality Engineer cannot continue", task);
    }
}

public sealed class WorkflowPushbackLoopTests
{
    [Fact]
    public async Task RunAsync_ResumesUpstreamAndBlockedSessionsUntilHandoffSucceeds()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-pushback-loop-{Guid.NewGuid():N}");
        var agentsDirectory = Path.Combine(root, ".github", "agents");
        var workspacePath = Path.Combine(root, "workspace");
        var databasePath = Path.Combine(root, "harness.db");
        Directory.CreateDirectory(agentsDirectory);
        Directory.CreateDirectory(workspacePath);
        foreach (var (id, name) in new[]
                 {
                     ("account-manager", "Account Manager"),
                     ("team-lead", "Team Lead"),
                     ("software-engineer", "Software Engineer"),
                     ("pre-mortem-sceptic", "Pre-mortem Sceptic"),
                     ("quality-engineer", "Quality Engineer"),
                     ("release-engineer", "Release Engineer")
                 })
        {
            await File.WriteAllTextAsync(
                Path.Combine(agentsDirectory, $"{id}.agent.md"),
                $"""
                 ---
                 name: {name}
                 description: Test agent.
                 ---

                 Complete the assigned role.
                 """);
        }
        await File.WriteAllTextAsync(
            Path.Combine(root, "WORKFLOW.md"),
            """
            ---
            workspace:
              root: workspace
            agent:
              max_concurrent_agents: 1
              max_attempts: 1
            ---

            Test workflow for {{ agent.name }} on {{ task }}.
            """);

        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;
        var databaseFactory = new TestDbContextFactory(options);
        var flow = new FlowRun
        {
            Title = "Implement feature",
            OriginalRequest = "Implement feature",
            ConsolidatedRequest = "Implement a focused product feature.",
            Kind = FlowKind.Advisory,
            Status = FlowStatus.Queued,
            RepositoryPath = root,
            RepositoryKnowledge = "Test repository.",
            Outcome = OutcomeType.None
        };
        await using (var database = await databaseFactory.CreateDbContextAsync())
        {
            await database.Database.EnsureCreatedAsync();
            database.Settings.Add(new HarnessSettings
            {
                RepositoryPath = root,
                RepositoryKnowledge = "Test repository.",
                MaxHandoffRetries = 2
            });
            database.Flows.Add(flow);
            await database.SaveChangesAsync();
        }

        var paths = new AiHarnessDemo.Infrastructure.HarnessPaths(
            root,
            agentsDirectory,
            databasePath);
        var workflowProvider = new WorkflowDefinitionProvider(
            paths,
            new WorkflowLoader(),
            NullLogger<WorkflowDefinitionProvider>.Instance);
        await workflowProvider.StartAsync(CancellationToken.None);
        var catalog = new AgentCatalog(paths, databaseFactory);
        var catalogStatus = await catalog.LoadAsync();
        Assert.True(catalogStatus.Ready, catalogStatus.LastError);
        var snapshots = new FlowAgentSnapshotService(databaseFactory, catalog);
        await using (var database = await databaseFactory.CreateDbContextAsync())
        {
            var stored = await database.Flows.SingleAsync(item => item.Id == flow.Id);
            snapshots.CaptureForNewFlow(database, stored);
            await database.SaveChangesAsync();
        }
        var runner = new PushbackLoopAgentRunner();
        using var handoffGate = new HandoffGateEngine();
        handoffGate.SetTrustLevel(HandoffActionType.Advance, HandoffTrustLevel.Auto);
        handoffGate.SetTrustLevel(HandoffActionType.RequestRevision, HandoffTrustLevel.Auto);
        var engine = new WorkflowEngine(
            databaseFactory,
            catalog,
            new FixedModelRouter(),
            new BootstrapTaskProfileFactory(),
            TestRoutingSupport.Recorder(databaseFactory),
            new FixedWorkspaceManager(workspacePath),
            runner,
            handoffGate,
            new CopilotSessionJournal(),
            workflowProvider,
            NullLogger<WorkflowEngine>.Instance,
            flowAgentSnapshotService: snapshots);

        try
        {
            await engine.RunAsync(flow.Id, CancellationToken.None);

            await using var database = await databaseFactory.CreateDbContextAsync();
            var stored = await database.Flows
                .Include(item => item.Steps)
                .Include(item => item.Events)
                .SingleAsync(item => item.Id == flow.Id);
            var learning = await database.Learnings.SingleAsync();
            var profiles = await database.TaskProfiles
                .OrderBy(item => item.Role)
                .ToListAsync();
            var leadRuns = runner.Contexts
                .Where(item => item.AgentRole == "team-lead")
                .ToList();
            var engineerRuns = runner.Contexts
                .Where(item => item.AgentRole == "software-engineer")
                .ToList();
            var qualityRuns = runner.Contexts
                .Where(item => item.AgentRole == "quality-engineer")
                .ToList();
            var releaseRuns = runner.Contexts
                .Where(item => item.AgentRole == "release-engineer")
                .ToList();

            Assert.Equal(FlowStatus.WaitingForFeedback, stored.Status);
            Assert.Equal(7, profiles.Count);
            Assert.Single(leadRuns);
            Assert.False(leadRuns[0].ResumeSession);
            Assert.All(
                runner.Contexts,
                context => Assert.Equal("fixture-effort", context.ModelEffort));
            Assert.Equal(
                [StepStatus.Completed, StepStatus.Completed, StepStatus.Completed],
                stored.Steps
                    .Where(item => item.AgentRole == "software-engineer")
                    .OrderBy(item => item.Attempt)
                    .Select(item => item.Status)
                    .ToArray());
            Assert.Equal(
                [StepStatus.Pushback, StepStatus.Pushback, StepStatus.Completed],
                stored.Steps
                    .Where(item => item.AgentRole == "quality-engineer")
                    .OrderBy(item => item.Attempt)
                    .Select(item => item.Status)
                    .ToArray());
            Assert.Equal(3, engineerRuns.Count);
            Assert.Single(engineerRuns.Select(item => item.CopilotSessionId).Distinct());
            Assert.False(engineerRuns[0].ResumeSession);
            Assert.All(engineerRuns.Skip(1), context => Assert.True(context.ResumeSession));
            Assert.Empty(releaseRuns);
            Assert.Equal(3, qualityRuns.Count);
            Assert.Single(qualityRuns.Select(item => item.CopilotSessionId).Distinct());
            Assert.False(qualityRuns[0].ResumeSession);
            Assert.All(qualityRuns.Skip(1), context => Assert.True(context.ResumeSession));
            Assert.Contains(
                "Quality Engineer cannot continue",
                engineerRuns[^1].Task);
            Assert.Contains(
                engineerRuns[^1].Learnings,
                item => item.Category == "Handoff pushback");
            Assert.Contains(
                "Software Engineer responded to your pushback",
                qualityRuns[^1].Task);
            Assert.Equal("software-engineer", learning.AgentId);
            Assert.Equal(2, learning.TimesObserved);
            Assert.True(learning.TimesApplied >= 2);
            Assert.Contains(
                stored.Events,
                item =>
                    item.Type == "agent.session-resumed" &&
                    item.Message.StartsWith("Software Engineer", StringComparison.Ordinal));
            Assert.All(
                stored.Steps,
                step => Assert.Contains(
                    $"Exact prompt for {step.AgentRole}",
                    step.ExecutionPrompt));
            Assert.DoesNotContain(
                stored.Steps,
                item => item.Status is StepStatus.Failed or StepStatus.Skipped);
        }
        finally
        {
            workflowProvider.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class PushbackLoopAgentRunner : IAgentRunner
    {
        public List<AgentExecutionContext> Contexts { get; } = [];

        public Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            context.Progress?.Invoke(new AgentRunProgress(
                AgentRunPhase.InitializingSession,
                $"Copilot session {context.CopilotSessionId:D} initialized.",
                CopilotSessionId: context.CopilotSessionId,
                CopilotSessionHome: Path.Combine(Path.GetTempPath(), "copilot-test-home")));
            context.Progress?.Invoke(new AgentRunProgress(
                AgentRunPhase.BuildingPrompt,
                "Rendered the exact prompt for the Copilot CLI turn.",
                $"# Exact prompt for {context.AgentRole}{Environment.NewLine}{Environment.NewLine}{context.Task}"));
            var output =
                context.AgentRole == "quality-engineer" && context.Attempt <= 2
                    ? """
                      HANDOFF_STATUS: PUSHBACK
                      PUSHBACK_OWNER_STEP_ID: implement
                      PUSHBACK_REASON: Software Engineer omitted the acceptance-to-test mapping.

                      ## PUSHBACK

                      Missing detail: The engineering handoff has no acceptance-to-test mapping.

                      ## Next owner

                      Software Engineer
                      """
                    : """
                      HANDOFF_STATUS: COMPLETE

                      ## Decision

                      The assigned role is complete.

                      ## Deliverable

                      The downstream handoff is unblocked.

                      ## Evidence

                      Focused validation passed.

                      ## Next owner

                      Continue the planned flow.
                      """;
            if (context.AgentRole == "team-lead")
            {
                output +=
                    Environment.NewLine +
                    TeamPlanParser.BeginSentinel +
                    Environment.NewLine +
                    """
                    {"Disposition":"Planned","Steps":[{"Id":"implement","AgentId":"software-engineer","Order":10,"Stage":"BeforeReview","Assignment":"Implement the focused change.","Justification":"The engineer owns implementation.","DependsOn":[],"Duties":["Analyze"],"OutcomeOwner":false,"TaskProfile":{"Complexity":5,"ReasoningDepth":6,"ContextDemand":5,"ToolIntensity":8,"TaskTypeTags":["Implementation"],"Risk":"Medium","RiskReason":"Implementation changes product behavior.","Confidence":0.8,"Rationales":["Code and tests are required."]}},{"Id":"verify","AgentId":"quality-engineer","Order":20,"Stage":"BeforeReview","Assignment":"Verify the result and prepare the Advisory outcome.","Justification":"Independent verification closes the Advisory.","DependsOn":["implement"],"Duties":["PrepareOutcome"],"OutcomeOwner":true,"TaskProfile":{"Complexity":5,"ReasoningDepth":6,"ContextDemand":6,"ToolIntensity":7,"TaskTypeTags":["Quality"],"Risk":"Medium","RiskReason":"Independent validation is required.","Confidence":0.8,"Rationales":["Acceptance evidence must be checked."]}}],"PreMortemCheckpoints":[],"AcceptanceCriteria":null,"MissingQualification":null}
                    """ +
                    Environment.NewLine +
                    TeamPlanParser.EndSentinel;
            }
            else if (context.IsOutcomeOwner &&
                     !output.Contains(
                         "HANDOFF_STATUS: PUSHBACK",
                         StringComparison.Ordinal))
            {
                output +=
                    Environment.NewLine +
                    FlowOutcomeParser.BeginSentinel +
                    Environment.NewLine +
                    """
                    {"Goal":"Verify the focused result.","Summary":"The result is ready for review.","ImplementationDetails":["The implementation and validation handoff completed."],"Artifacts":[]}
                    """ +
                    Environment.NewLine +
                    FlowOutcomeParser.EndSentinel;
            }
            return Task.FromResult(new AgentExecutionResult(
                output,
                "Fake runner evidence.",
                1,
                []));
        }
    }

    private sealed class FixedWorkspaceManager(string path) : IWorkspaceManager
    {
        public Task<WorkspaceInfo> PrepareAsync(
            FlowRun flow,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkspaceInfo(path, "ai-harness/test", CreatedNow: false));
    }

    private sealed class TestDbContextFactory(
        DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);

        public Task<HarnessDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HarnessDbContext(options));
    }
}

public sealed class PersistenceTests
{
    [Fact]
    public async Task Sqlite_RoundTripsSettingsFlowHistoryAndLearning()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var database = new HarnessDbContext(options))
        {
            await database.Database.EnsureCreatedAsync();
            database.Settings.Add(new HarnessSettings
            {
                RepositoryPath = @"C:\code\demo",
                RepositoryKnowledge = "A .NET 10 web application.",
                Outcome = OutcomeType.PullRequest,
                MaxHandoffRetries = 4
            });
            var flow = new FlowRun
            {
                Title = "Add onboarding",
                OriginalRequest = "Add onboarding",
                ConsolidatedRequest = "Add persistent onboarding with keyboard support.",
                Status = FlowStatus.WaitingForFeedback,
                RepositoryPath = @"C:\code\demo"
            };
            flow.Steps.Add(new FlowStep
            {
                Iteration = 1,
                Sequence = 10,
                AgentId = "software-engineer",
                AgentName = "Software Engineer",
                AgentRole = "software-engineer",
                Model = "gpt-5.4-mini",
                Status = StepStatus.Completed,
                DurationMilliseconds = 900
            });
            database.Flows.Add(flow);
            database.Learnings.Add(new HarnessLearning
            {
                SourceFlowId = flow.Id,
                AgentId = "software-engineer",
                Category = "Handoff quality",
                Trigger = "QA pushback",
                Lesson = "Evidence must be traceable.",
                PromptRefinement = "Include exact commands and results."
            });
            await database.SaveChangesAsync();
        }

        await using (var database = new HarnessDbContext(options))
        {
            var settings = await database.Settings.SingleAsync();
            var flow = await database.Flows.Include(item => item.Steps).SingleAsync();
            var learning = await database.Learnings.SingleAsync();

            Assert.Equal(OutcomeType.PullRequest, settings.Outcome);
            Assert.Equal(4, settings.MaxHandoffRetries);
            Assert.Equal(FlowStatus.WaitingForFeedback, flow.Status);
            Assert.Equal("gpt-5.4-mini", Assert.Single(flow.Steps).Model);
            Assert.Equal("Evidence must be traceable.", learning.Lesson);
        }
    }

}

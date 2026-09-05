using AiHarnessDemo.Data;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiHarnessDemo.Tests;

public sealed class FlowPlannerTests
{
    [Fact]
    public void Plan_SelectsOnlyEnabledRelevantSpecialistsInDeliveryOrder()
    {
        var planner = new FlowPlanner();
        var agents = new[]
        {
            Agent("lead", "team-lead"),
            Agent("architect", "architect"),
            Agent("designer", "product-designer"),
            Agent("data", "data-engineer"),
            Agent("engineer", "software-engineer"),
            Agent("security", "security-engineer"),
            Agent("qa", "quality-engineer"),
            Agent("writer", "technical-writer", enabled: false),
            Agent("release", "release-engineer")
        };

        var plan = planner.Plan(
            "Redesign the admin dashboard and add secure role-based access backed by a database.",
            agents);

        Assert.Equal(
            [
                "team-lead",
                "architect",
                "product-designer",
                "data-engineer",
                "software-engineer",
                "security-engineer",
                "quality-engineer",
                "release-engineer"
            ],
            plan.Select(item => item.Agent.Role));
        Assert.DoesNotContain(plan, item => item.Agent.Role == "technical-writer");
    }

    [Fact]
    public void Plan_RejectsAFlowWithoutAnEnabledImplementationAgent()
    {
        var planner = new FlowPlanner();
        var agents = new[]
        {
            Agent("lead", "team-lead"),
            Agent("engineer", "software-engineer", enabled: false),
            Agent("qa", "quality-engineer")
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => planner.Plan("Add a small button.", agents));

        Assert.Contains("software-engineer", exception.Message);
    }

    private static AgentRecord Agent(string id, string role, bool enabled = true) =>
        new()
        {
            Id = id,
            Name = role,
            Description = role,
            Role = role,
            SourcePath = $"{id}.agent.md",
            Enabled = enabled
        };
}

public sealed class IntakeCoordinatorTests
{
    [Fact]
    public void ParseResponse_LoadsClarifyingQuestion()
    {
        var result = IntakeCoordinator.ParseResponse("""
            INTAKE_STATUS: NEEDS_CLARIFICATION
            TASK_TITLE: Clarify onboarding outcome
            CUSTOMER_REPLY: What observable result defines success?
            TASK_BRIEF: NONE
            """);

        Assert.False(result.Ready);
        Assert.Equal(AccountManagerIntakeStatus.NeedsClarification, result.Status);
        Assert.Equal("Clarify onboarding outcome", result.TaskTitle);
        Assert.Contains("observable result", result.Reply);
        Assert.Empty(result.TaskBrief);
    }

    [Fact]
    public void ParseResponse_LoadsBriefAwaitingCustomerConfirmation()
    {
        var result = IntakeCoordinator.ParseResponse("""
            INTAKE_STATUS: AWAITING_CONFIRMATION
            TASK_TITLE: Add persistent onboarding checklist
            CUSTOMER_REPLY: Do I understand correctly that you want a persistent onboarding checklist? If yes, I'll ask the team to implement it.
            TASK_BRIEF:
            Outcome: Add a persistent onboarding checklist.
            Acceptance:
            - Supports keyboard navigation.
            - Includes automated tests.
            """);

        Assert.False(result.Ready);
        Assert.True(result.AwaitingConfirmation);
        Assert.Equal("Add persistent onboarding checklist", result.TaskTitle);
        Assert.Contains("persistent onboarding", result.TaskBrief);
        Assert.Contains("keyboard navigation", result.TaskBrief);
        Assert.Contains("automated tests", result.TaskBrief);
    }

    [Fact]
    public void ParseResponse_LoadsCustomerConfirmedBrief()
    {
        var result = IntakeCoordinator.ParseResponse("""
            INTAKE_STATUS: CONFIRMED
            TASK_TITLE: Add persistent onboarding checklist
            CUSTOMER_REPLY: Thanks - I'll ask the team to implement it now.
            TASK_BRIEF:
            Outcome: Add a persistent onboarding checklist.
            """);

        Assert.True(result.Ready);
        Assert.Equal(AccountManagerIntakeStatus.Confirmed, result.Status);
        Assert.Equal("Add persistent onboarding checklist", result.TaskTitle);
    }

    [Fact]
    public void ParseResponse_FailsClosedWhenCopilotOmitsContractMarkers()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => IntakeCoordinator.ParseResponse("Looks clear to me."));

        Assert.Contains("invalid intake contract", exception.Message);
    }

    [Fact]
    public void ParseResponse_FailsClosedWhenTaskTitleIsMissing()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => IntakeCoordinator.ParseResponse("""
                INTAKE_STATUS: AWAITING_CONFIRMATION
                CUSTOMER_REPLY: Do I understand correctly that you want a refreshed public site?
                TASK_BRIEF: Outcome: Refresh the public site.
                """));

        Assert.Contains("TASK_TITLE", exception.Message);
    }

    [Fact]
    public void AccountManagerContract_RequiresExplicitConfirmationWithoutAnInterview()
    {
        var contract = CopilotReasoningHost.ResponseContract("account-manager");

        Assert.Contains("Default to AWAITING_CONFIRMATION", contract);
        Assert.Contains("explicitly and unambiguously approves", contract);
        Assert.Contains("most recent AWAITING_CONFIRMATION brief", contract);
        Assert.Contains("TASK_TITLE", contract);
        Assert.Contains("never copy or truncate the opening message", contract);
        Assert.Contains("something the customer can click is actionable", contract);
        Assert.Contains("Never ask about technologies", contract);
        Assert.Contains("deployment, hosting, credentials", contract);
    }

    [Fact]
    public void DialogueTask_RequiresConfirmationAndKeepsDeliveryChoicesOutOfIntake()
    {
        var messages = new[]
        {
            new FlowMessage
            {
                Role = ConversationRole.Customer,
                Content = "Give me a new design I can click."
            },
            new FlowMessage
            {
                Role = ConversationRole.AccountManager,
                Content = "Which site should the redesign cover?",
                IsQuestion = true
            },
            new FlowMessage
            {
                Role = ConversationRole.Customer,
                Content = "Both."
            }
        };

        var task = IntakeCoordinator.BuildDialogueTask(messages, OutcomeType.PullRequest);

        Assert.Contains("must not return CONFIRMED", task);
        Assert.Contains("return AWAITING_CONFIRMATION", task);
        Assert.Contains("Ask at most one focused clarification question in this turn", task);
        Assert.Contains("design the customer can click is actionable", task);
        Assert.Contains("configured delivery outcome is PullRequest", task);
        Assert.Contains("do not ask the customer", task);
    }

    [Fact]
    public void DialogueTask_PassesTheProposedBriefIntoTheApprovalTurn()
    {
        const string proposedBrief = "Outcome: Refresh the public pages with an interactive design.";
        var messages = new[]
        {
            new FlowMessage
            {
                Role = ConversationRole.AccountManager,
                Content = "Do I understand correctly that you want a fresh public-site design?",
                IsQuestion = true
            },
            new FlowMessage
            {
                Role = ConversationRole.Customer,
                Content = "Yes."
            }
        };

        var task = IntakeCoordinator.BuildDialogueTask(
            messages,
            OutcomeType.PullRequest,
            proposedBrief);

        Assert.Contains("Return CONFIRMED only if", task);
        Assert.Contains("UNCONFIRMED_TASK_BRIEF", task);
        Assert.Contains(proposedBrief, task);
    }

    [Fact]
    public void ConfirmationGate_RejectsConfirmationWithoutAProposedBrief()
    {
        var response = new AccountManagerResponse(
            AccountManagerIntakeStatus.Confirmed,
            "I'll ask the team to implement it now.",
            "Add persistent onboarding checklist",
            "Unreviewed brief");

        var exception = Assert.Throws<InvalidOperationException>(
            () => IntakeCoordinator.ApplyConfirmationGate(response, pendingConfirmationBrief: null));

        Assert.Contains("has not reviewed", exception.Message);
    }

    [Fact]
    public void ConfirmationGate_PreservesTheBriefTheCustomerReviewed()
    {
        const string approvedBrief = "Outcome: Refresh the public pages.";
        var response = new AccountManagerResponse(
            AccountManagerIntakeStatus.Confirmed,
            "I'll ask the team to implement it now.",
            "Refresh public pages",
            "A changed brief");

        var confirmed = IntakeCoordinator.ApplyConfirmationGate(response, approvedBrief);

        Assert.Equal(approvedBrief, confirmed.TaskBrief);
        Assert.Equal("Refresh public pages", confirmed.TaskTitle);
    }

    [Fact]
    public void IntakeOutcome_AppliesGeneratedTitleAndQueuesConfirmedBrief()
    {
        var flow = new FlowRun
        {
            Title = "I want new fresh design for our site because old one",
            OriginalRequest = "Give the public pages a fresh design."
        };
        var response = new AccountManagerResponse(
            AccountManagerIntakeStatus.Confirmed,
            "I'll ask the team to implement it now.",
            "Refresh public site design",
            "Outcome: Refresh the public pages.");

        var queuedEvent = IntakeCoordinator.ApplyIntakeOutcome(flow, response);

        Assert.NotNull(queuedEvent);
        Assert.Equal("Refresh public site design", flow.Title);
        Assert.Equal(FlowStatus.Queued, flow.Status);
        Assert.Equal("flow.queued", queuedEvent.Type);
        Assert.Contains(queuedEvent, flow.Events);
    }
}

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

        var manifest = AgentCatalog.Parse(
            "software-engineer",
            "software-engineer.agent.md",
            content);

        Assert.Equal("Software Engineer", manifest.Name);
        Assert.Equal("software-engineer", manifest.Role);
        Assert.Equal(60, manifest.SortOrder);
        Assert.Contains("Produce working code", manifest.Instructions);
    }
}

public sealed class PreviewArtifactCatalogTests
{
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
    [InlineData(
        "account-manager",
        "INTAKE_STATUS: CONFIRMED\nTASK_TITLE: Refresh site\nCUSTOMER_REPLY: Confirmed.\nTASK_BRIEF: Refresh the site.",
        true)]
    [InlineData("account-manager", "INTAKE_STATUS: INVALID", false)]
    [InlineData("product-manager", "REWORK_TARGET_ROLES: NONE", true)]
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
    public void AccountManagerInvocation_IsToolFreeAndPreservesUserConfiguration()
    {
        var arguments = CopilotReasoningHost.BuildCliArguments(
            @"C:\worktree",
            @"C:\harness",
            "account-manager",
            "account-manager",
            "claude-sonnet-5",
            "low",
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "Prompt");

        Assert.Contains("--available-tools", arguments);
        Assert.Contains("--disable-builtin-mcps", arguments);
        Assert.Equal(
            ["--effort", "low"],
            arguments
                .SkipWhile(argument => argument != "--effort")
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
            "pre-mortem-sceptic",
            "gpt-5.6-sol",
            "max",
            Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"),
            "Investigate.");

        Assert.Contains(
            "--available-tools=view,grep,glob,web_search",
            arguments);
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
    public void DeliveryAgentInvocation_PreservesCliToolsAndUserConfiguration()
    {
        var arguments = CopilotReasoningHost.BuildCliArguments(
            @"C:\worktree",
            @"C:\harness",
            "software-engineer",
            "software-engineer",
            "gpt-5.4-mini",
            "medium",
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            "Prompt");

        Assert.Contains("--allow-all-tools", arguments);
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
        Assert.DoesNotContain("--available-tools", arguments);
        Assert.DoesNotContain("--disable-builtin-mcps", arguments);
    }

    [Fact]
    public void ModelWithoutConfigurableEffort_OmitsTheEffortArgument()
    {
        var arguments = CopilotReasoningHost.BuildCliArguments(
            @"C:\worktree",
            @"C:\harness",
            "software-engineer",
            "software-engineer",
            "model-with-default-effort",
            "default",
            Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"),
            "Prompt");

        Assert.DoesNotContain("--effort", arguments);
    }

    [Fact]
    public void PreApprovalRelease_BlocksRemotePublicationCredentials()
    {
        var guarded = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(
            CopilotReasoningHost.BuildProcessEnvironment(
                "release-engineer",
                allowRemotePublication: false));

        Assert.Equal("customer-approval-required", guarded["GH_TOKEN"]);
        Assert.Equal("remote.origin.pushurl", guarded["GIT_CONFIG_KEY_0"]);
        Assert.Null(CopilotReasoningHost.BuildProcessEnvironment(
            "release-engineer",
            allowRemotePublication: true));
        Assert.Null(CopilotReasoningHost.BuildProcessEnvironment(
            "software-engineer",
            allowRemotePublication: false));
    }

    [Fact]
    public void PreMortemEnvironment_RemovesPublicationCredentials()
    {
        var guarded = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(
            CopilotReasoningHost.BuildProcessEnvironment(
                "pre-mortem-sceptic",
                allowRemotePublication: false));

        Assert.Null(guarded["GH_TOKEN"]);
        Assert.Null(guarded["GITHUB_TOKEN"]);
        Assert.Equal(
            "disabled://pre-mortem-read-only",
            guarded["GIT_CONFIG_VALUE_0"]);
    }

    [Fact]
    public async Task PreMortemAgentDefinition_IsStagedOutsideTheHarnessRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"restricted-agent-{Guid.NewGuid():N}");
        var harnessRoot = Path.Combine(root, "harness");
        var copilotHome = Path.Combine(root, "copilot");
        var sourceDirectory = Path.Combine(harnessRoot, ".github", "agents");
        Directory.CreateDirectory(sourceDirectory);
        var source = Path.Combine(
            sourceDirectory,
            "pre-mortem-sceptic.agent.md");
        await File.WriteAllTextAsync(source, "# Pre-mortem Sceptic");

        try
        {
            var access = await CopilotReasoningHost.PrepareRestrictedAgentRootAsync(
                copilotHome,
                "pre-mortem-sceptic",
                source,
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
    public void StagedAgentDefinition_RemovesModelOverride()
    {
        var sanitized = CopilotReasoningHost.RemoveAgentModelFrontMatter(
            """
            ---
            name: Pre-mortem Sceptic
            model: claude-opus-5
            description: Independent review.
            ---

            # Contract
            """);

        Assert.DoesNotContain("model:", sanitized);
        Assert.Contains("name: Pre-mortem Sceptic", sanitized);
        Assert.Contains("# Contract", sanitized);
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
            "software-engineer",
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
    public void PromptValues_KeepOnlyFocusedRoleContext()
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
            [
                "Team Lead handoff",
                "Architect handoff",
                "Product Designer handoff"
            ],
            [],
            Progress: null);

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
        Assert.Contains("### Project summary", prompt);
        Assert.Contains("Long README details", prompt);
        Assert.Contains("Architect handoff", prompt);
        Assert.Contains("Product Designer handoff", prompt);
        Assert.DoesNotContain("Team Lead handoff", prompt);
        Assert.DoesNotContain("Source files studied", prompt);
        Assert.DoesNotContain("AI initialization", prompt);
        Assert.DoesNotContain("README signal", prompt);
        Assert.DoesNotContain("Installation", prompt);
        Assert.DoesNotContain("Operations", prompt);
        Assert.DoesNotContain("Editable harness notes", prompt);
        Assert.DoesNotContain("Team plan", prompt);
        Assert.DoesNotContain("No prior", prompt);
        Assert.DoesNotContain(@"E:\source-project", prompt);
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

        var (revision, retry) = WorkflowEngine.CreateRecoverySteps(
            flow,
            blocked,
            upstream,
            revisionAttempt: 2);

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

        var (_, retry) = WorkflowEngine.CreateRecoverySteps(
            flow,
            blocked,
            upstream,
            revisionAttempt: 3);

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
                     ("team-lead", "Team Lead"),
                     ("software-engineer", "Software Engineer"),
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
            Status = FlowStatus.Queued,
            RepositoryPath = root,
            RepositoryKnowledge = "Test repository."
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
        var runner = new PushbackLoopAgentRunner();
        using var handoffGate = new HandoffGateEngine();
        handoffGate.SetTrustLevel(HandoffActionType.Advance, HandoffTrustLevel.Auto);
        handoffGate.SetTrustLevel(HandoffActionType.RequestRevision, HandoffTrustLevel.Auto);
        handoffGate.SetTrustLevel(HandoffActionType.Release, HandoffTrustLevel.Gated);
        var engine = new WorkflowEngine(
            databaseFactory,
            new AgentCatalog(paths, databaseFactory),
            new FlowPlanner(),
            new FixedModelRouter(),
            new BootstrapTaskProfileFactory(),
            TestRoutingSupport.Recorder(databaseFactory),
            new FixedWorkspaceManager(workspacePath),
            runner,
            handoffGate,
            new CopilotSessionJournal(),
            workflowProvider,
            NullLogger<WorkflowEngine>.Instance);

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
            var releaseRun = runner.Contexts.Single(item =>
                item.AgentRole == "release-engineer");

            Assert.Equal(FlowStatus.WaitingForFeedback, stored.Status);
            Assert.Equal(4, profiles.Count);
            Assert.Equal(2, leadRuns.Count);
            Assert.False(leadRuns[0].ResumeSession);
            Assert.True(leadRuns[1].ResumeSession);
            Assert.All(
                runner.Contexts,
                context => Assert.Equal("fixture-effort", context.ModelEffort));
            Assert.Contains(
                "do not push a branch or create a pull request",
                releaseRun.Task,
                StringComparison.OrdinalIgnoreCase);
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
            Assert.Contains(
                stored.Events,
                item => item.Type == "profile.validation-correction");
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
                var teamLeadAttempt = Contexts.Count(item =>
                    item.AgentRole == "team-lead");
                if (teamLeadAttempt == 1)
                {
                    return Task.FromResult(new AgentExecutionResult(
                        output,
                        "Fake runner evidence.",
                        1,
                        []));
                }
                output += """

                    TEAM_TASK_PROFILES_V1_BEGIN
                    {"Version":"task-profile-v1","Profiles":[{"Role":"software-engineer","Complexity":5,"ReasoningDepth":6,"ContextDemand":5,"ToolIntensity":8,"TaskTypeTags":["Implementation"],"Risk":"Medium","RiskReason":"Implementation changes product behavior.","Confidence":0.8,"Rationales":["Code and tests are required."]},{"Role":"quality-engineer","Complexity":5,"ReasoningDepth":6,"ContextDemand":6,"ToolIntensity":7,"TaskTypeTags":["Quality"],"Risk":"Medium","RiskReason":"Independent validation is required.","Confidence":0.8,"Rationales":["Acceptance evidence must be checked."]},{"Role":"release-engineer","Complexity":4,"ReasoningDepth":4,"ContextDemand":6,"ToolIntensity":6,"TaskTypeTags":["Release"],"Risk":"High","RiskReason":"Packaging changes repository state.","Confidence":0.8,"Rationales":["Verified work must be packaged."]}]}
                    TEAM_TASK_PROFILES_V1_END
                    PRE_MORTEM_PLAN_V1_BEGIN
                    {"Version":"pre-mortem-plan-v1","AfterRoles":[]}
                    PRE_MORTEM_PLAN_V1_END
                    """;
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

    [Fact]
    public async Task SettingsSchema_AddsHandoffRetryLimitToExistingDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE Settings (
                    Id INTEGER NOT NULL CONSTRAINT PK_Settings PRIMARY KEY,
                    RepositoryPath TEXT NOT NULL,
                    RepositoryKnowledge TEXT NOT NULL,
                    Outcome TEXT NOT NULL,
                    ExecutionMode TEXT NOT NULL,
                    UpdatedAt INTEGER NOT NULL
                );
                INSERT INTO Settings
                    (Id, RepositoryPath, RepositoryKnowledge, Outcome, ExecutionMode, UpdatedAt)
                VALUES
                    (1, '', '', 'PullRequest', 'LiveCopilot', 0);
                """;
            await command.ExecuteNonQueryAsync();
        }
        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var database = new HarnessDbContext(options);

        await DatabaseInitializer.EnsureSettingsSchemaAsync(database);
        var settings = await database.Settings.SingleAsync();

        Assert.Equal(2, settings.MaxHandoffRetries);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task FlowStepSchema_AddsExecutionPromptToExistingDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE FlowSteps (
                    Id TEXT NOT NULL CONSTRAINT PK_FlowSteps PRIMARY KEY,
                    FlowRunId TEXT NOT NULL,
                    Iteration INTEGER NOT NULL,
                    Sequence INTEGER NOT NULL,
                    AgentId TEXT NOT NULL,
                    AgentName TEXT NOT NULL,
                    Attempt INTEGER NOT NULL,
                    Label TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    Phase TEXT NOT NULL DEFAULT 'PreparingWorkspace'
                );
                CREATE TABLE FlowEvents (
                    Id TEXT NOT NULL CONSTRAINT PK_FlowEvents PRIMARY KEY,
                    FlowRunId TEXT NOT NULL,
                    FlowStepId TEXT NULL,
                    Type TEXT NOT NULL
                );
                INSERT INTO FlowSteps
                    (Id, FlowRunId, Iteration, Sequence, AgentId, AgentName, Attempt, Label, Status)
                VALUES
                    ('failed-step', 'flow', 1, 10, 'software-engineer', 'Software Engineer', 1, 'Execute Software Engineer contract', 'Failed'),
                    ('retry-step', 'flow', 1, 20, 'software-engineer', 'Software Engineer', 2, 'Manual restart of Software Engineer', 'Pending'),
                    ('pushback-step', 'flow', 1, 30, 'quality-engineer', 'Quality Engineer', 1, 'Execute Quality Engineer contract', 'Pushback'),
                    ('revision-step', 'flow', 1, 40, 'software-engineer', 'Software Engineer', 3, 'Revision after Quality Engineer pushback', 'Pushback'),
                    ('effective-revision', 'flow', 1, 45, 'software-engineer', 'Software Engineer', 4, 'Retry after Architect revision', 'Completed'),
                    ('handoff-retry', 'flow', 1, 50, 'quality-engineer', 'Quality Engineer', 2, 'Retry after Software Engineer revision', 'Failed'),
                    ('manual-handoff-retry', 'flow', 1, 60, 'quality-engineer', 'Quality Engineer', 3, 'Manual restart of Quality Engineer', 'Pending'),
                    ('legacy-qa-pushback', 'flow', 1, 70, 'quality-engineer', 'Quality Engineer', 4, 'Execute Quality Engineer contract', 'Pushback'),
                    ('legacy-qa-revision', 'flow', 1, 80, 'software-engineer', 'Software Engineer', 5, 'Revision after QA pushback', 'Completed'),
                    ('legacy-revalidate', 'flow', 1, 90, 'quality-engineer', 'Quality Engineer', 5, 'Re-validate corrected handoff', 'Pending'),
                    ('old-pushback', 'flow', 1, 100, 'quality-engineer', 'Quality Engineer', 6, 'Execute Quality Engineer contract', 'Pushback'),
                    ('old-revision', 'flow', 1, 110, 'software-engineer', 'Software Engineer', 6, 'Revision after Quality Engineer pushback', 'Completed'),
                    ('latest-pushback', 'flow', 1, 120, 'quality-engineer', 'Quality Engineer', 7, 'Execute Quality Engineer contract', 'Pushback'),
                    ('latest-revision', 'flow', 1, 130, 'software-engineer', 'Software Engineer', 7, 'Revision after Quality Engineer pushback', 'Running'),
                    ('latest-retry', 'flow', 1, 140, 'quality-engineer', 'Quality Engineer', 8, 'Retry after Software Engineer revision', 'Pending'),
                    ('legacy-invalid-correction', 'flow', 1, 150, 'team-lead', 'Team Lead', 2, 'Correct Team Lead task profiles', 'Completed');
                INSERT INTO FlowEvents (Id, FlowRunId, FlowStepId, Type)
                VALUES ('invalid-event', 'flow', 'legacy-invalid-correction', 'profile.validation-failed');
                """;
            await command.ExecuteNonQueryAsync();
        }
        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var database = new HarnessDbContext(options);

        await DatabaseInitializer.EnsureFlowStepSchemaAsync(database);
        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT name FROM pragma_table_info('FlowSteps');";
        var columns = new List<string>();
        await using (var reader = await probe.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(0));
            }
        }

        Assert.Contains("ExecutionPrompt", columns);
        Assert.Contains("CopilotSessionId", columns);
        Assert.Contains("CopilotSessionHome", columns);
        Assert.Contains("RemotePublicationAllowed", columns);
        Assert.Contains("RetryOfStepId", columns);
        Assert.Contains("DependsOnStepId", columns);
        Assert.Contains("PushbackRootStepId", columns);
        Assert.Contains("PreMortemOriginStepId", columns);
        Assert.Contains("PreMortemTargetStepId", columns);
        Assert.Contains("PreMortemReviewStepId", columns);
        await using var backfillProbe = connection.CreateCommand();
        backfillProbe.CommandText =
            "SELECT RetryOfStepId FROM FlowSteps WHERE Id = 'retry-step';";
        Assert.Equal("failed-step", await backfillProbe.ExecuteScalarAsync());
        await using var dependencyProbe = connection.CreateCommand();
        dependencyProbe.CommandText =
            "SELECT DependsOnStepId || '|' || PushbackRootStepId " +
            "FROM FlowSteps WHERE Id = 'handoff-retry';";
        Assert.Equal(
            "effective-revision|pushback-step",
            await dependencyProbe.ExecuteScalarAsync());
        await using var lineageProbe = connection.CreateCommand();
        lineageProbe.CommandText =
            "SELECT RetryOfStepId || '|' || PushbackRootStepId " +
            "FROM FlowSteps WHERE Id = 'manual-handoff-retry';";
        Assert.Equal(
            "pushback-step|pushback-step",
            await lineageProbe.ExecuteScalarAsync());
        await using var historicalProbe = connection.CreateCommand();
        historicalProbe.CommandText =
            "SELECT RetryOfStepId || '|' || DependsOnStepId || '|' || PushbackRootStepId " +
            "FROM FlowSteps WHERE Id = 'legacy-revalidate';";
        Assert.Equal(
            "legacy-qa-pushback|legacy-qa-revision|legacy-qa-pushback",
            await historicalProbe.ExecuteScalarAsync());
        await using var nearestProbe = connection.CreateCommand();
        nearestProbe.CommandText =
            "SELECT DependsOnStepId FROM FlowSteps WHERE Id = 'latest-retry';";
        Assert.Equal(
            "latest-revision",
            await nearestProbe.ExecuteScalarAsync());
        await using var correctionProbe = connection.CreateCommand();
        correctionProbe.CommandText =
            "SELECT Status || '|' || Phase " +
            "FROM FlowSteps WHERE Id = 'legacy-invalid-correction';";
        Assert.Equal(
            "Failed|Failed",
            await correctionProbe.ExecuteScalarAsync());
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }
}

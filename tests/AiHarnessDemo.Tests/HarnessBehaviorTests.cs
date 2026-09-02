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

public sealed class ModelSelectorTests
{
    [Theory]
    [InlineData("account-manager", 1, "claude-sonnet-5")]
    [InlineData("software-engineer", 2, "gpt-5.4-mini")]
    [InlineData("software-engineer", 5, "gpt-5.4")]
    [InlineData("architect", 4, "claude-sonnet-5")]
    public void Select_MatchesRoleAndComplexity(
        string role,
        int complexity,
        string expectedModel)
    {
        var choice = new ModelSelector().Select(role, complexity);

        Assert.Equal(expectedModel, choice.Model);
        Assert.False(string.IsNullOrWhiteSpace(choice.Reason));
    }

    [Fact]
    public void Select_EscalatesWhenHistoryShowsRepeatedCorrections()
    {
        var choice = new ModelSelector().Select(
            "quality-engineer",
            complexity: 2,
            historicalFailureRate: 0.4);

        Assert.Equal("claude-sonnet-5", choice.Model);
        Assert.Contains("prior", choice.Reason);
    }
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

public sealed class CopilotReasoningHostTests
{
    [Fact]
    public void AccountManagerInvocation_IsToolFreeAndUsesAnIsolatedCopilotHome()
    {
        var arguments = CopilotReasoningHost.BuildCliArguments(
            @"C:\worktree",
            @"C:\harness",
            "account-manager",
            "account-manager",
            "claude-sonnet-5",
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "Prompt");
        var environment = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            CopilotReasoningHost.BuildProcessEnvironment(
                "account-manager",
                Path.Combine(Path.GetTempPath(), "harness", "ai-harness.db")));

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
        Assert.EndsWith(
            Path.Combine("copilot-home", "account-manager"),
            environment["COPILOT_HOME"]);
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
        Assert.Null(
            CopilotReasoningHost.BuildProcessEnvironment(
                "software-engineer",
                Path.Combine(Path.GetTempPath(), "harness", "ai-harness.db")));
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
        Assert.Contains("resume", contract, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("Assignment for this turn", task);
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
            new ModelSelector(),
            new FixedWorkspaceManager(workspacePath),
            runner,
            handoffGate,
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
            var engineerRuns = runner.Contexts
                .Where(item => item.AgentRole == "software-engineer")
                .ToList();
            var qualityRuns = runner.Contexts
                .Where(item => item.AgentRole == "quality-engineer")
                .ToList();

            Assert.Equal(FlowStatus.WaitingForFeedback, stored.Status);
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
            Assert.Equal(3, qualityRuns.Count);
            Assert.Single(qualityRuns.Select(item => item.CopilotSessionId).Distinct());
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
}

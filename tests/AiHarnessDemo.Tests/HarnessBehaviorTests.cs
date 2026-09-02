using AiHarnessDemo.Data;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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
            "Prompt");

        Assert.Contains("--allow-all-tools", arguments);
        Assert.DoesNotContain("--available-tools", arguments);
        Assert.DoesNotContain("--disable-builtin-mcps", arguments);
        Assert.Null(
            CopilotReasoningHost.BuildProcessEnvironment(
                "software-engineer",
                Path.Combine(Path.GetTempPath(), "harness", "ai-harness.db")));
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
                Outcome = OutcomeType.PullRequest
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
            Assert.Equal(FlowStatus.WaitingForFeedback, flow.Status);
            Assert.Equal("gpt-5.4-mini", Assert.Single(flow.Steps).Model);
            Assert.Equal("Evidence must be traceable.", learning.Lesson);
        }
    }
}

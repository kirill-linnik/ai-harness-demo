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
            CUSTOMER_REPLY: What observable result defines success?
            TASK_BRIEF: NONE
            """);

        Assert.False(result.Ready);
        Assert.Contains("observable result", result.Reply);
        Assert.Empty(result.TaskBrief);
    }

    [Fact]
    public void ParseResponse_LoadsTaskReadyBrief()
    {
        var result = IntakeCoordinator.ParseResponse("""
            INTAKE_STATUS: READY
            CUSTOMER_REPLY: The implementation brief is ready for Team Lead.
            TASK_BRIEF:
            Outcome: Add a persistent onboarding checklist.
            Acceptance:
            - Supports keyboard navigation.
            - Includes automated tests.
            """);

        Assert.True(result.Ready);
        Assert.Contains("persistent onboarding", result.TaskBrief);
        Assert.Contains("keyboard navigation", result.TaskBrief);
        Assert.Contains("automated tests", result.TaskBrief);
    }

    [Fact]
    public void ParseResponse_FailsClosedWhenCopilotOmitsContractMarkers()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => IntakeCoordinator.ParseResponse("Looks clear to me."));

        Assert.Contains("invalid intake contract", exception.Message);
    }

    [Fact]
    public void AccountManagerContract_DefaultsToActionInsteadOfAnInterview()
    {
        var contract = CopilotReasoningHost.ResponseContract("account-manager");

        Assert.Contains("Default to READY", contract);
        Assert.Contains("every earlier answer as settled", contract);
        Assert.Contains("something the customer can click is actionable", contract);
        Assert.Contains("Never ask about technologies", contract);
        Assert.Contains("deployment, hosting, credentials", contract);
        Assert.Contains("you must return READY using reasonable assumptions", contract);
    }

    [Fact]
    public void DialogueTask_CapsClarificationAndKeepsDeliveryChoicesOutOfIntake()
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

        Assert.Contains("this turn must return READY", task);
        Assert.Contains("design the customer can click is actionable", task);
        Assert.Contains("configured delivery outcome is PullRequest", task);
        Assert.Contains("do not ask the customer", task);
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

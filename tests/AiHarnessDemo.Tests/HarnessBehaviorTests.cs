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
            TASK_BRIEF: Add a persistent onboarding checklist with keyboard support and automated tests.
            """);

        Assert.True(result.Ready);
        Assert.Contains("persistent onboarding", result.TaskBrief);
    }

    [Fact]
    public void ParseResponse_FailsClosedWhenCopilotOmitsContractMarkers()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => IntakeCoordinator.ParseResponse("Looks clear to me."));

        Assert.Contains("invalid intake contract", exception.Message);
    }
}

public sealed class ModelSelectorTests
{
    [Theory]
    [InlineData("account-manager", 1, "gpt-5-mini")]
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

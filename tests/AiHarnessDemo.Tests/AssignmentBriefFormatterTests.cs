using System.Text.Json;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Services;

namespace AiHarnessDemo.Tests;

public sealed class AssignmentBriefFormatterTests
{
    private const string BriefJson =
        """{"Goal":"Give both sites a fresh look.","Details":["Redesign the visual style.","Apply the design to both languages."],"SuccessCriteria":["Both sites use the new design."],"Constraints":["Keep the existing logo unchanged."],"Assumptions":[]}""";

    private const string ExpectedMarkdown = """
        ### Goal

        Give both sites a fresh look.

        ### Details

        - Redesign the visual style.
        - Apply the design to both languages.

        ### Success criteria

        - Both sites use the new design.

        ### Constraints

        - Keep the existing logo unchanged.

        ### Assumptions

        None specified.
        """;

    [Fact]
    public void NormalizedBrief_UsesReadableSectionsWithoutChangingStoredJson()
    {
        var flow = new FlowRun
        {
            Title = "Refresh both sites",
            OriginalRequest = "Give both sites a fresh look.",
            ConsolidatedRequest = BriefJson
        };

        var formatted = AssignmentBriefFormatter.Format(flow.ConsolidatedRequest);

        Assert.Equal(ExpectedMarkdown.ReplaceLineEndings("\n"), formatted);
        Assert.Equal(BriefJson, flow.ConsolidatedRequest);
        var stored = JsonSerializer.Deserialize<IntakeBrief>(flow.ConsolidatedRequest);
        Assert.Equal("Give both sites a fresh look.", stored!.Goal);
        Assert.Equal(2, stored.Details!.Count);
    }

    [Fact]
    public void NormalizedBrief_PreservesLiteralMarkdownAndMultilineListItems()
    {
        var brief = IntakeParser.SerializeBrief(new IntakeBrief
        {
            Goal = "Keep <input> & **literal** text.",
            Details =
            [
                @"Use C:\work\logo_v2.svg and [saved](file).",
                "First line\r\n## Not a section\n- Not another item\n1. Not numbered\n---"
            ],
            SuccessCriteria = ["Retain `code`, ~text~, and a | b."],
            Constraints = [],
            Assumptions = []
        });

        var formatted = AssignmentBriefFormatter.Format(brief);

        Assert.Contains(
            @"Keep \<input\> \& \*\*literal\*\* text.",
            formatted,
            StringComparison.Ordinal);
        Assert.Contains(
            @"- Use C:\\work\\logo\_v2.svg and \[saved\](file).",
            formatted,
            StringComparison.Ordinal);
        Assert.Contains(
            """
            - First line
              \#\# Not a section
              \- Not another item
              1\. Not numbered
              --\-
            """.ReplaceLineEndings("\n"),
            formatted,
            StringComparison.Ordinal);
        Assert.Contains(
            @"- Retain \`code\`, \~text\~, and a \| b.",
            formatted,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  Keep the original request exactly as written.\r\n")]
    [InlineData("## Customer refinement\n\n- Preserve `existing Markdown`.")]
    public void UnstructuredBrief_IsNotRewritten(string brief)
    {
        Assert.Equal(brief, AssignmentBriefFormatter.Format(brief));
    }

    [Fact]
    public void OtherJsonShapes_AreFencedWithoutDiscardingFields()
    {
        const string brief =
            """{"Goal":"Preserve all fields.","Details":[],"SuccessCriteria":[],"Constraints":[],"Assumptions":[],"AdditionalContext":{"Keep":true}}""";

        var formatted = AssignmentBriefFormatter.Format(brief);

        Assert.StartsWith("```json\n", formatted, StringComparison.Ordinal);
        Assert.EndsWith("\n```", formatted, StringComparison.Ordinal);
        using var original = JsonDocument.Parse(brief);
        using var rendered = JsonDocument.Parse(formatted[8..^4]);
        Assert.Equal(
            JsonSerializer.Serialize(original.RootElement),
            JsonSerializer.Serialize(rendered.RootElement));
    }

    [Fact]
    public void MalformedJson_FailsExplicitly()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AssignmentBriefFormatter.Format("""{"Goal":"""));

        Assert.Contains("invalid JSON", exception.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<JsonException>(exception.InnerException);
    }

    [Theory]
    [InlineData("\"Goal\":\"Goal.\"", "\"Goal\":null")]
    [InlineData("\"Details\":[]", "\"Details\":null")]
    [InlineData("\"Constraints\":[]", "\"Constraints\":[42]")]
    public void NormalizedBriefWithInvalidFieldTypes_FailsExplicitly(
        string original,
        string replacement)
    {
        const string brief =
            """{"Goal":"Goal.","Details":[],"SuccessCriteria":[],"Constraints":[],"Assumptions":[]}""";

        Assert.Throws<InvalidOperationException>(() =>
            AssignmentBriefFormatter.Format(
                brief.Replace(original, replacement, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Implement only the approved visual changes.")]
    public void WorkerAssignment_FormatsTheBriefAndRetainsRoleInstructions(string assignment)
    {
        var task = WorkflowEngine.BuildStepTask(BriefJson, assignment);
        var expected = ExpectedMarkdown.ReplaceLineEndings("\n");

        Assert.StartsWith(expected, task, StringComparison.Ordinal);
        Assert.DoesNotContain(BriefJson, task, StringComparison.Ordinal);
        if (assignment.Length == 0)
        {
            Assert.Equal(expected, task);
        }
        else
        {
            Assert.EndsWith(
                $"## Role-specific assignment{Environment.NewLine}{assignment}",
                task,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PlanningAssignment_FormatsTheBriefAndRetainsMachineContracts()
    {
        var flow = new FlowRun
        {
            Title = "Plan the site refresh",
            OriginalRequest = "Give both sites a fresh look.",
            Kind = FlowKind.Advisory,
            ConsolidatedRequest = BriefJson
        };

        var assignment = BuildPlanningAssignment(flow);

        Assert.Contains(
            ExpectedMarkdown.ReplaceLineEndings("\n"),
            assignment,
            StringComparison.Ordinal);
        Assert.DoesNotContain(BriefJson, assignment, StringComparison.Ordinal);
        Assert.Contains(
            """{"Disposition":"Planned","Steps":[""",
            assignment,
            StringComparison.Ordinal);
        Assert.Contains(
            "actual product delivers the requested",
            assignment,
            StringComparison.Ordinal);
        Assert.Contains(
            "existing asset conventions",
            assignment,
            StringComparison.Ordinal);
        Assert.Contains(
            "Incidental fields in supplied source material",
            assignment,
            StringComparison.Ordinal);
        Assert.Contains(
            "does not authorize its publication",
            assignment,
            StringComparison.Ordinal);
        Assert.Contains(
            "do not expand planning into a second implementation pass",
            assignment,
            StringComparison.Ordinal);
        Assert.Contains(
            "Shell, Node, and PowerShell checks (including browser scripts launched from",
            assignment,
            StringComparison.Ordinal);
        Assert.Contains(
            "Where the verifier may use either",
            assignment,
            StringComparison.Ordinal);
        Assert.Equal(BriefJson, flow.ConsolidatedRequest);
    }

    [Fact]
    public void PlanningAssignment_PlacesRiskyRequirementsReviewBeforeDownstreamWorkers()
    {
        var flow = new FlowRun
        {
            Title = "Plan a risky change",
            OriginalRequest = "Deliver an observable change.",
            Kind = FlowKind.Delivery,
            ConsolidatedRequest = BriefJson
        };

        var assignment = WorkflowEngine.BuildStudioTeamLeadAssignment(
            flow,
            [],
            TeamPlanValidationContext.ForPersistedPlan(
                FlowKind.Delivery, [], preMortemEnabled: true),
            preMortemAvailable: true,
            maximumPreMortemRounds: 2);

        Assert.Contains("requires an Analyze-duty requirements-authoring step", assignment);
        Assert.Contains("before the first Design or Implement step", assignment);
        Assert.Contains("returns findings to the requirements author", assignment);
        Assert.Contains("Never checkpoint an implemented result or QA", assignment);
        Assert.Contains("counterfactual premise", assignment);
        Assert.Contains("requirements gaps rather than code defects", assignment);
    }

    [Fact]
    public void PreMortemAssignment_PreservesTheCompleteReadableCustomerContext()
    {
        var brief = IntakeParser.SerializeBrief(new IntakeBrief
        {
            Goal = "Preserve the goal while reviewing.",
            Details = [new string('x', 1_000)],
            SuccessCriteria = [],
            Constraints = [],
            Assumptions = []
        });
        const string roleAssignment = "Independently review the proposed design.";

        var task = WorkflowEngine.BuildPreMortemContextTask(brief, roleAssignment);

        Assert.StartsWith(
            $"## Role-specific assignment{Environment.NewLine}{roleAssignment}",
            task,
            StringComparison.Ordinal);
        var context = task.Split(
            $"## Original customer outcome{Environment.NewLine}",
            StringSplitOptions.None)[1];
        Assert.StartsWith(
            "### Goal\n\nPreserve the goal while reviewing.\n\n### Details\n\n- ",
            context,
            StringComparison.Ordinal);
        Assert.Equal(AssignmentBriefFormatter.Format(brief), context);
        Assert.Contains(new string('x', 1_000), context, StringComparison.Ordinal);
    }

    [Fact]
    public void MaximumBrief_StaysWithinPlanningBudgetAfterMarkdownEscaping()
    {
        var goal = new string('_', IntakeParser.MaximumGoalCharacters);
        var details = Enumerable.Repeat(
            new string('_', IntakeParser.MaximumListItemCharacters),
            IntakeParser.MaximumListItems).ToArray();
        var flow = new FlowRun
        {
            Title = "Preserve the maximum brief",
            OriginalRequest = "Keep every detail of the confirmed brief.",
            Kind = FlowKind.Advisory,
            ConsolidatedRequest = IntakeParser.SerializeBrief(new IntakeBrief
            {
                Goal = goal,
                Details = details,
                SuccessCriteria = details,
                Constraints = details,
                Assumptions = details
            })
        };

        var formatted = AssignmentBriefFormatter.Format(flow.ConsolidatedRequest);
        var assignment = BuildPlanningAssignment(flow);
        var task = WorkflowEngine.BuildStepTask(flow.ConsolidatedRequest, assignment);

        Assert.Contains(formatted, assignment, StringComparison.Ordinal);
        Assert.Equal(
            4 * IntakeParser.MaximumListItems,
            formatted.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal)));
        Assert.EndsWith(
            string.Concat(Enumerable.Repeat(@"\_", IntakeParser.MaximumListItemCharacters)),
            formatted,
            StringComparison.Ordinal);
        Assert.True(task.Length <= CopilotReasoningHost.MaximumPlanningTaskCharacters);
    }

    private static string BuildPlanningAssignment(FlowRun flow) =>
        WorkflowEngine.BuildStudioTeamLeadAssignment(
            flow,
            [],
            TeamPlanValidationContext.ForPersistedPlan(
                flow.Kind,
                [],
                preMortemEnabled: false),
            preMortemAvailable: false,
            maximumPreMortemRounds: 0);
}

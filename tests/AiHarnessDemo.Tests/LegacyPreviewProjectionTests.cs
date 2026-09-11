using AiHarnessDemo.Contracts;
using AiHarnessDemo.Core.Domain;

namespace AiHarnessDemo.Tests;

public sealed class LegacyPreviewProjectionTests
{
    [Fact]
    public void DffcLegacyShape_ProjectsLatestCompletedHistoricalEvidenceOnly()
    {
        var flow = new FlowRun
        {
            Id = Guid.Parse("dffc6813-6600-433b-acb0-2da5a5165113"),
            Title = "Historical DevClub delivery",
            OriginalRequest = "Refresh the browser-visible product.",
            ContractVersion = "legacy-v1",
            Kind = FlowKind.Delivery,
            OutcomeContractJson = string.Empty
        };
        AddStep(
            flow,
            "software-engineer",
            "Software Engineer",
            "Initial implementation",
            "Old implementation.",
            sequence: 10,
            completedAt: DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        AddStep(
            flow,
            "software-engineer",
            "Software Engineer",
            "Correct implementation",
            "## Changed\n\nThe self-contained preview was corrected.",
            sequence: 20,
            completedAt: DateTimeOffset.Parse("2026-01-02T00:00:00Z"));
        AddStep(
            flow,
            "quality-engineer",
            "Quality Engineer",
            "Verify",
            "**Observed:** focused checks passed.",
            sequence: 30,
            completedAt: DateTimeOffset.Parse("2026-01-03T00:00:00Z"));
        AddStep(
            flow,
            "release-engineer",
            "Release Engineer",
            "Package",
            "- Packaged immutable browser artifacts",
            sequence: 40,
            completedAt: DateTimeOffset.Parse("2026-01-04T00:00:00Z"));
        flow.Steps.Add(new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = 1,
            Sequence = 50,
            AgentId = "quality-failed",
            AgentName = "Quality Engineer",
            AgentRole = "quality-engineer",
            Status = StepStatus.Failed,
            OutputSummary = "This failed output must not be projected."
        });

        var projection = flow.ToHistoricalDeliveryEvidenceDto();

        Assert.NotNull(projection);
        Assert.True(projection.NonAuthoritative);
        Assert.Equal(3, projection.Items.Count);
        Assert.DoesNotContain(
            projection.Items,
            item => item.Markdown.Contains("Old implementation", StringComparison.Ordinal));
        Assert.Contains(
            projection.Items,
            item => item.Markdown.Contains("self-contained preview", StringComparison.Ordinal));
        Assert.Contains(
            projection.Items,
            item => item.Kind == "Quality verification");
        Assert.Contains(
            projection.Items,
            item => item.Kind == "Release package");
    }

    private static void AddStep(
        FlowRun flow,
        string role,
        string name,
        string label,
        string output,
        int sequence,
        DateTimeOffset completedAt)
    {
        flow.Steps.Add(new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = 1,
            Sequence = sequence,
            AgentId = role,
            AgentName = name,
            AgentRole = role,
            Label = label,
            Status = StepStatus.Completed,
            OutputSummary = output,
            CompletedAt = completedAt
        });
    }
}

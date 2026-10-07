using System.Text.Json;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Services;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Tests;

public sealed partial class ReviewWorkflowTests
{
    private const string PrefacedFindings = """
        Findings are ready.

        PRE_MORTEM_STATUS: FINDINGS
        PRE_MORTEM_FINDINGS_BEGIN
        {"Findings":[{"FailureMode":"Missing environment preflight.","Evidence":"Read-only requirements reference site\\package.json.","MissedSignal":"Required execution was not checked.","Prevention":"Confirm the authorized environment before implementation."}]}
        PRE_MORTEM_FINDINGS_END
        """;

    private const string EmbeddedStatusFindings = """
        Preface
        PRE_MORTEM_FINDINGS_BEGIN
        {"Findings": []
        PRE_MORTEM_STATUS: CLEAR
        }
        PRE_MORTEM_FINDINGS_END
        """;

    [Theory]
    [InlineData("valid")]
    [InlineData("duplicate-status")]
    [InlineData("duplicate-block")]
    [InlineData("invalid-escape")]
    [InlineData("unknown-field")]
    [InlineData("too-long")]
    [InlineData("mismatched-status")]
    [InlineData("truncated")]
    [InlineData("embedded-status")]
    public void PreMortemFraming_OnlyMovesStatusAndStillEnforcesTheStrictContract(string scenario)
    {
        var output = scenario switch
        {
            "duplicate-status" => PreMortemRules.ClearStatus + "\n" + PrefacedFindings,
            "duplicate-block" => PrefacedFindings + "\n" + PrefacedFindings,
            "invalid-escape" => PrefacedFindings.Replace(@"site\\package", @"site\package"),
            "unknown-field" => PrefacedFindings.Replace(
                "\"Findings\":", "\"Unexpected\":true,\"Findings\":"),
            "too-long" => PrefacedFindings.Replace(
                "Missing environment preflight.", new string('x', 1_201)),
            "mismatched-status" => PrefacedFindings.Replace(
                PreMortemRules.FindingsStatus, PreMortemRules.ClearStatus),
            "truncated" => PrefacedFindings.Replace(PreMortemRules.FindingsEndSentinel, ""),
            "embedded-status" => EmbeddedStatusFindings,
            _ => PrefacedFindings
        };
        var framed = WorkflowEngine.TryFramePreMortemResponse(output);
        if (scenario != "valid")
        {
            Assert.Null(framed);
            return;
        }
        Assert.NotNull(framed);
        Assert.StartsWith(PreMortemRules.FindingsStatus + "\n", framed);
        Assert.Contains("Findings are ready.", framed);
        var review = PreMortemRules.ParseReview(framed);
        Assert.Equal(@"Read-only requirements reference site\package.json.",
            Assert.Single(review.Findings).Evidence);
        Assert.Equal(output.Split(PreMortemRules.FindingsBeginSentinel)[1],
            framed.Split(PreMortemRules.FindingsBeginSentinel)[1]);
    }

    [Theory]
    [InlineData("bound")]
    [InlineData("missing-ledger")]
    [InlineData("wrong-flow")]
    [InlineData("old-iteration")]
    [InlineData("different-agent")]
    [InlineData("different-lineage")]
    [InlineData("different-target")]
    [InlineData("newer-valid-clear")]
    [InlineData("newer-prefaced-clear")]
    public async Task PreMortemFraming_UsesOnlyItsBoundCurrentSource(string scenario)
    {
        await using var harness = await ReviewHarness.CreateAsync(FlowKind.Delivery);
        await using var database = await harness.Factory.CreateDbContextAsync();
        var flow = await database.Flows.Include(item => item.Events)
            .SingleAsync(item => item.Id == harness.FlowId);
        var target = new FlowStep
        {
            FlowRunId = flow.Id, Iteration = flow.Iteration, Sequence = 10,
            AgentId = "author", AgentName = "Author", AgentRole = "analyst",
            Status = StepStatus.Completed
        };
        var source = new FlowStep
        {
            FlowRunId = flow.Id, Iteration = flow.Iteration, Sequence = 20,
            AgentId = "sceptic", AgentName = "Sceptic", AgentRole = "sceptic",
            PlanStepKey = "requirements",
            InvocationKind = ExecutionInvocationKind.PreMortem,
            PlanStage = PlanStage.BeforeReview, Status = StepStatus.Completed,
            PreMortemTargetStepId = target.Id, PreMortemOriginStepId = target.Id,
            OutputSummary = PrefacedFindings
        };
        var correction = new FlowStep
        {
            FlowRunId = flow.Id, Iteration = flow.Iteration, Sequence = 30,
            AgentId = source.AgentId, AgentName = source.AgentName, AgentRole = source.AgentRole,
            PlanStepKey = source.PlanStepKey,
            InvocationKind = source.InvocationKind, PlanStage = source.PlanStage,
            Status = StepStatus.Failed, StableSemanticRootId = source.Id,
            PreMortemTargetStepId = target.Id, PreMortemOriginStepId = target.Id,
            OutputSummary = PrefacedFindings.Replace(@"site\\package", @"site\package")
        };
        switch (scenario)
        {
            case "wrong-flow":
                var other = new FlowRun { Title = "Other flow", OriginalRequest = "Other request" };
                database.Flows.Add(other);
                source.FlowRunId = other.Id;
                break;
            case "old-iteration": source.Iteration--; break;
            case "different-agent": source.AgentId = "other"; break;
            case "different-lineage": correction.StableSemanticRootId = Guid.NewGuid(); break;
            case "different-target": source.PreMortemTargetStepId = Guid.NewGuid(); break;
            case "newer-valid-clear":
            case "newer-prefaced-clear":
                correction.OutputSummary =
                    (scenario == "newer-prefaced-clear" ? "Newer result.\n" : "") +
                    "PRE_MORTEM_STATUS: CLEAR\nPRE_MORTEM_FINDINGS_BEGIN\n" +
                    "{\"Findings\":[]}\nPRE_MORTEM_FINDINGS_END";
                break;
        }
        database.FlowSteps.AddRange(target, source, correction);
        if (scenario != "missing-ledger")
        {
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id, FlowStepId = correction.Id,
                Type = "agent.contract-correction-scheduled",
                Message = "Bound correction to its source.",
                DataJson = JsonSerializer.Serialize(new
                {
                    SourceStepId = source.Id, CorrectionStepId = correction.Id
                })
            });
        }
        await database.SaveChangesAsync();
        var recovered = await WorkflowEngine.TryRecoverPreMortemResponseAsync(
            database, flow, correction, correction.OutputSummary, CancellationToken.None);
        if (scenario == "newer-prefaced-clear")
        {
            Assert.NotNull(recovered);
            Assert.Equal(correction.Id, recovered.SourceStepId);
            Assert.False(PreMortemRules.ParseReview(recovered.Output).HasFindings);
        }
        else if (scenario == "bound")
        {
            Assert.NotNull(recovered);
            Assert.Equal(source.Id, recovered.SourceStepId);
            Assert.Single(PreMortemRules.ParseReview(recovered.Output).Findings);
        }
        else
        {
            Assert.Null(recovered);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreMortemFraming_LegacyRestartPreservesOutputAndBudgetWithoutAnotherAgentTurn(
        bool embeddedStatus)
    {
        await using var harness = await ReviewHarness.CreateAsync(FlowKind.Delivery);
        Guid correctionId;
        var deadline = DateTimeOffset.FromUnixTimeMilliseconds(
            DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds());
        await using (var database = await harness.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows.SingleAsync(item => item.Id == harness.FlowId);
            flow.Status = FlowStatus.Failed;
            flow.FailureReason = "The bounded Studio response-contract correction remained invalid.";
            database.FlowAgentSnapshots.Add(new FlowAgentSnapshot
            {
                FlowRunId = flow.Id, AgentId = "sceptic", Name = "Sceptic",
                Role = WorkflowEngine.PreMortemRole, Description = "Requirements review.",
                Instructions = "Review evidence-backed requirements gaps.",
                DefinitionHash = "test-sceptic-definition", SourceFileName = "sceptic.agent.md",
                EnabledAtSnapshot = true
            });
            var target = new FlowStep
            {
                FlowRunId = flow.Id, Iteration = flow.Iteration, Sequence = 10,
                AgentId = "author", AgentName = "Author", AgentRole = "analyst",
                Status = StepStatus.Completed
            };
            var source = new FlowStep
            {
                FlowRunId = flow.Id, Iteration = flow.Iteration, Sequence = 20,
                AgentId = "sceptic", AgentName = "Sceptic", AgentRole = "sceptic",
                PlanStepKey = "requirements", InvocationKind = ExecutionInvocationKind.PreMortem,
                PlanStage = PlanStage.BeforeReview, Status = StepStatus.Completed,
                PreMortemTargetStepId = target.Id, PreMortemOriginStepId = target.Id,
                OutputSummary = embeddedStatus ? EmbeddedStatusFindings : PrefacedFindings
            };
            var correction = new FlowStep
            {
                FlowRunId = flow.Id, Iteration = flow.Iteration, Sequence = 30,
                AgentId = source.AgentId, AgentName = source.AgentName, AgentRole = source.AgentRole,
                PlanStepKey = source.PlanStepKey, InvocationKind = source.InvocationKind,
                PlanStage = source.PlanStage, Status = StepStatus.Failed,
                StableSemanticRootId = source.Id, ExecutionBudgetRootId = source.Id,
                PreMortemTargetStepId = target.Id, PreMortemOriginStepId = target.Id,
                OutputSummary = embeddedStatus ? EmbeddedStatusFindings :
                    PrefacedFindings.Replace(@"site\\package", @"site\package")
            };
            correctionId = correction.Id;
            database.FlowSteps.AddRange(target, source, correction, new FlowStep
            {
                FlowRunId = flow.Id, Iteration = flow.Iteration, Sequence = 40,
                AgentId = "author", AgentName = "Author", AgentRole = "analyst",
                Status = StepStatus.Skipped, DependsOnStepId = correction.Id
            });
            database.AgentExecutionBudgets.Add(new AgentExecutionBudget
            {
                FlowRunId = flow.Id, RootStepId = source.Id, AgentId = source.AgentId,
                StartedAt = deadline.AddHours(-4), DeadlineAt = deadline,
                PolicyJson = JsonSerializer.Serialize(AssignmentExecutionBudget.Policy(
                    harness.WorkflowProvider.GetEffective().Config.Copilot, source.WorkflowRevision))
            });
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id, FlowStepId = correction.Id,
                Type = "agent.contract-correction-scheduled",
                Message = "Bound correction to its source.",
                DataJson = JsonSerializer.Serialize(new
                {
                    SourceStepId = source.Id, CorrectionStepId = correction.Id
                })
            });
            await database.SaveChangesAsync();
        }
        if (embeddedStatus)
        {
            await Assert.ThrowsAsync<FlowLifecycleException>(() =>
                harness.Engine.RestartFailedFlowAsync(harness.FlowId, CancellationToken.None));
            var rejected = await harness.LoadFlowAsync();
            Assert.Equal(FlowStatus.Failed, rejected.Status);
            Assert.Equal(EmbeddedStatusFindings,
                rejected.Steps.Single(item => item.Id == correctionId).OutputSummary);
            Assert.DoesNotContain(rejected.Events,
                item => item.Type == "agent.pre-mortem-framing-recovered");
            Assert.Empty(harness.Runner.Contexts);
            return;
        }
        await harness.Engine.RestartFailedFlowAsync(harness.FlowId, CancellationToken.None);
        var recovered = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Queued, recovered.Status);
        var completed = recovered.Steps.Single(item => item.Id == correctionId);
        Assert.Equal(StepStatus.Completed, completed.Status);
        Assert.Single(PreMortemRules.ParseReview(completed.OutputSummary).Findings);
        var ledger = Assert.Single(recovered.Events,
            item => item.Type == "agent.pre-mortem-framing-recovered");
        using var data = JsonDocument.Parse(ledger.DataJson!);
        Assert.Contains(@"site\package",
            data.RootElement.GetProperty("OriginalOutput").GetString());
        Assert.Contains(recovered.Steps, item => item.Sequence == 40 && item.Status == StepStatus.Pending);
        Assert.Empty(harness.Runner.Contexts);
        Assert.DoesNotContain(recovered.GateRecords,
            item => item.ActionType == AiHarnessDemo.Core.Gating.HandoffActionType.CustomerReview);
        await using var check = await harness.Factory.CreateDbContextAsync();
        Assert.Equal(deadline, (await check.AgentExecutionBudgets.SingleAsync()).DeadlineAt);
    }
}

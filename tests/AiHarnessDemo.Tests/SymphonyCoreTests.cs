using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Workflow;

namespace AiHarnessDemo.Tests;

public sealed class HandoffGateEngineTests
{
    [Fact]
    public void SubmitProposal_DefaultsUnknownActionsToShadow()
    {
        using var gate = new HandoffGateEngine();

        var record = gate.SubmitProposal(Proposal(HandoffActionType.Advance));

        Assert.Equal(HandoffGateDecision.LoggedShadow, record.Decision);
        Assert.Equal(HandoffTrustLevel.Shadow, record.TrustLevelAtDecision);
    }

    [Fact]
    public void SubmitProposal_HighBlastRadiusDowngradesAutoToHumanApproval()
    {
        using var gate = new HandoffGateEngine();
        gate.SetTrustLevel(HandoffActionType.Advance, HandoffTrustLevel.Auto);

        var record = gate.SubmitProposal(Proposal(
            HandoffActionType.Advance,
            HandoffBlastRadius.High));

        Assert.Equal(HandoffGateDecision.AwaitingHumanApproval, record.Decision);
    }

    [Fact]
    public void EngageKillSwitch_DominatesTrust()
    {
        using var gate = new HandoffGateEngine();
        gate.SetTrustLevel(HandoffActionType.Advance, HandoffTrustLevel.Auto);
        gate.EngageKillSwitch();

        var record = gate.SubmitProposal(Proposal(HandoffActionType.Advance));

        Assert.Equal(HandoffGateDecision.BlockedKillSwitch, record.Decision);
        Assert.True(gate.KillSwitchToken.IsCancellationRequested);
    }

    [Fact]
    public void ResolveProposal_RecordsCustomerDecisionOnce()
    {
        using var gate = new HandoffGateEngine();
        gate.SetTrustLevel(HandoffActionType.Release, HandoffTrustLevel.Gated);
        var record = gate.SubmitProposal(Proposal(HandoffActionType.Release));

        var resolved = gate.ResolveProposal(
            record.Id,
            approved: true,
            resolvedBy: "customer",
            note: "Accepted");

        Assert.True(resolved.Resolved);
        Assert.True(resolved.Approved);
        Assert.Throws<InvalidOperationException>(() =>
            gate.ResolveProposal(record.Id, true, "customer"));
    }

    [Fact]
    public void SetTrustLevel_RejectsAutomaticRelease()
    {
        using var gate = new HandoffGateEngine();

        Assert.Throws<InvalidOperationException>(() =>
            gate.SetTrustLevel(HandoffActionType.Release, HandoffTrustLevel.Auto));
    }

    private static HandoffProposal Proposal(
        HandoffActionType actionType,
        HandoffBlastRadius blastRadius = HandoffBlastRadius.Medium) =>
        new()
        {
            FlowRunId = Guid.NewGuid(),
            FlowStepId = Guid.NewGuid(),
            ActionType = actionType,
            Summary = "Deliverable",
            Evidence = "Observed checks",
            BlastRadius = blastRadius
        };
}

public sealed class WorkflowDefinitionTests
{
    [Fact]
    public void Load_ParsesFrontMatterAndResolvesWorkspaceRelativeToWorkflow()
    {
        using var artifact = WorkflowArtifact.Create("""
            ---
            tracker:
              kind: voice
            workspace:
              root: .worktrees
            agent:
              max_concurrent_agents: 7
              max_attempts: 2
            copilot:
              command: copilot
            ---
            Work on {{ task }} as {{ agent.name }}.
            """);

        var workflow = new WorkflowLoader().Load(artifact.Path);

        Assert.Equal("voice", workflow.Config.Tracker.Kind);
        Assert.Equal(7, workflow.Config.Agent.MaxConcurrentAgents);
        Assert.Equal(2, workflow.Config.Agent.MaxAttempts);
        Assert.Equal(
            Path.Combine(artifact.Directory, ".worktrees"),
            workflow.Config.Workspace.ResolvedRoot);
    }

    [Fact]
    public void Render_FailsClosedForUnknownTemplateVariables()
    {
        var renderer = new WorkflowPromptRenderer();

        var exception = Assert.Throws<WorkflowConfigurationException>(() =>
            renderer.Render("Build {{ missing.value }}", new Dictionary<string, string>()));

        Assert.Contains("missing.value", exception.Message);
    }

    [Fact]
    public void Load_RejectsInvalidRuntimeValues()
    {
        using var artifact = WorkflowArtifact.Create("""
            ---
            tracker:
              kind: voice
            agent:
              max_concurrent_agents: 0
            ---
            Prompt
            """);

        var exception = Assert.Throws<WorkflowConfigurationException>(() =>
            new WorkflowLoader().Load(artifact.Path));

        Assert.Contains("max_concurrent_agents", exception.Message);
    }

    private sealed class WorkflowArtifact : IDisposable
    {
        private WorkflowArtifact(string directory, string path)
        {
            Directory = directory;
            Path = path;
        }

        public string Directory { get; }

        public string Path { get; }

        public static WorkflowArtifact Create(string content)
        {
            var directory = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "test-artifacts",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "WORKFLOW.md");
            File.WriteAllText(path, content);
            return new WorkflowArtifact(directory, path);
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }
}

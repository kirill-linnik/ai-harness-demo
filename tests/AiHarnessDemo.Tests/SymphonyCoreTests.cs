using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Services;
using AiHarnessDemo.Infrastructure;
using AiHarnessDemo.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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
        gate.SetTrustLevel(HandoffActionType.Advance, HandoffTrustLevel.Gated);
        var record = gate.SubmitProposal(Proposal(HandoffActionType.Advance));

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
    public void SupersedeProposal_ResolvesAKillSwitchBlockedGate()
    {
        using var gate = new HandoffGateEngine();
        gate.SetTrustLevel(HandoffActionType.CustomerReview, HandoffTrustLevel.Gated);
        gate.EngageKillSwitch();
        var record = gate.SubmitProposal(Proposal(HandoffActionType.CustomerReview));

        var superseded = gate.SupersedeProposal(
            record.Id,
            "harness",
            "Revised candidate.");

        Assert.Equal(HandoffGateDecision.BlockedKillSwitch, superseded.Decision);
        Assert.True(superseded.Resolved);
        Assert.False(superseded.Approved);
    }

    [Theory]
    [InlineData(HandoffActionType.CustomerReview)]
    [InlineData(HandoffActionType.CustomerWaiver)]
    public void SetTrustLevel_RejectsAutomaticHumanGate(
        HandoffActionType actionType)
    {
        using var gate = new HandoffGateEngine();

        Assert.Throws<InvalidOperationException>(() =>
            gate.SetTrustLevel(actionType, HandoffTrustLevel.Auto));
    }

    [Fact]
    public void SubmitProposal_DescribesPushbackAsARevisionRequest()
    {
        using var gate = new HandoffGateEngine();
        gate.SetTrustLevel(HandoffActionType.RequestRevision, HandoffTrustLevel.Auto);

        var record = gate.SubmitProposal(Proposal(HandoffActionType.RequestRevision));

        Assert.Equal(HandoffGateDecision.AutoApproved, record.Decision);
        Assert.Contains("revision request was accepted", record.Reason);
        Assert.DoesNotContain("handoff contract passed", record.Reason);
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
        Assert.Equal(1_800_000, workflow.Config.Copilot.SilentToolTimeoutMs);
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
    public void Render_PreservesTemplateSyntaxInsideRuntimeValues()
    {
        var rendered = new WorkflowPromptRenderer().Render(
            "Prior handoff:\n{{ handoffs }}",
            new Dictionary<string, string>
            {
                ["handoffs"] = "Angular binding: {{ meeting.title }}"
            });

        Assert.Contains("{{ meeting.title }}", rendered);
    }

    [Fact]
    public void Render_RejectsUnsupportedExpressionsInTheTemplateItself()
    {
        var exception = Assert.Throws<WorkflowConfigurationException>(() =>
            new WorkflowPromptRenderer().Render(
                "{{ task | upper }}",
                new Dictionary<string, string> { ["task"] = "Build it." }));

        Assert.Contains("invalid or unsupported", exception.Message);
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

    [Fact]
    public void Load_AcceptsUnknownTopLevelFieldButRejectsUnknownStudioField()
    {
        using var accepted = WorkflowArtifact.Create("""
            ---
            future_symphony_extension:
              value: accepted
            ---
            Prompt
            """);
        Assert.NotNull(new WorkflowLoader().Load(accepted.Path));

        using var rejected = WorkflowArtifact.Create("""
            ---
            studio:
              version: 1
            ---
            Prompt
            """);
        var exception = Assert.Throws<WorkflowConfigurationException>(() =>
            new WorkflowLoader().Load(rejected.Path));
        Assert.Contains("studio.version", exception.Message);
    }

    [Fact]
    public void Load_RejectsPermissionCeilingRelaxationAndMissingMinimumDuty()
    {
        using var unsafePermission = WorkflowArtifact.Create("""
            ---
            studio:
              flow_kinds:
                advisory:
                  required_duties: [PrepareOutcome]
                  maximum_permission: Publish
            ---
            Prompt
            """);
        Assert.Contains(
            "cannot exceed ReadOnlySource",
            Assert.Throws<WorkflowConfigurationException>(() =>
                new WorkflowLoader().Load(unsafePermission.Path)).Message);

        using var missingDuty = WorkflowArtifact.Create("""
            ---
            studio:
              flow_kinds:
                delivery:
                  required_duties: [Implement, Verify, PrepareOutcome]
            ---
            Prompt
            """);
        Assert.Contains(
            "Publish",
            Assert.Throws<WorkflowConfigurationException>(() =>
                new WorkflowLoader().Load(missingDuty.Path)).Message);
    }

    [Fact]
    public async Task Provider_InvalidReloadPreservesEffectiveRevisionAndMarksCurrentInvalid()
    {
        using var artifact = WorkflowArtifact.Create("""
            ---
            agent:
              max_concurrent_agents: 3
            ---
            Work on {{ task }}.
            """);
        var paths = new HarnessPaths(
            artifact.Directory,
            Path.Combine(artifact.Directory, ".github", "agents"),
            Path.Combine(artifact.Directory, "harness.db"));
        using var provider = new WorkflowDefinitionProvider(
            paths,
            new WorkflowLoader(),
            NullLogger<WorkflowDefinitionProvider>.Instance);
        await provider.StartAsync(CancellationToken.None);
        var revision = provider.GetEffective().Revision;

        File.WriteAllText(artifact.Path, """
            ---
            studio:
              invalid_field: true
            ---
            Invalid current file
            """);
        await provider.ReloadAsync();

        Assert.False(provider.Status().CurrentFileValid);
        Assert.True(provider.Status().HasEffectiveDefinition);
        Assert.Equal(revision, provider.GetEffective().Revision);
        Assert.Contains("studio.invalid_field", provider.Status().CurrentFileError);
    }

    public sealed class WorkflowArtifact : IDisposable
    {
        private WorkflowArtifact(string directory, string path)
        {
            Directory = directory;
            Path = path;
        }

        public sealed class AgentCatalogSliceTests
        {
            [Fact]
            public void Loader_RequiredFailureIsFatalButOptionalFailureIsVisible()
            {
                using var artifact = CatalogArtifact.Create();
                File.Delete(System.IO.Path.Combine(artifact.Agents, "team-lead.agent.md"));
                File.WriteAllText(
                    System.IO.Path.Combine(artifact.Agents, "optional.agent.md"),
                    "---\nname: Optional\n---\nInstructions");

                var result = new AgentCatalogLoader().Load(artifact.Agents);
                var required = Assert.Single(result.Definitions, item =>
                    item.Record.Id == "team-lead");
                var optional = Assert.Single(result.Definitions, item =>
                    item.Record.Id == "optional");

                Assert.True(required.Record.Required);
                Assert.Equal(AgentDefinitionStatus.Invalid, required.Record.DefinitionStatus);
                Assert.Contains("was not found", required.Record.ValidationError);
                Assert.False(optional.Record.Required);
                Assert.Equal(AgentDefinitionStatus.Invalid, optional.Record.DefinitionStatus);
                Assert.Contains("'description'", optional.Record.ValidationError);
            }

            [Theory]
            [InlineData("Account-Manager", "account-manager")]
            [InlineData("TEAM-LEAD", "team-lead")]
            [InlineData("Pre-Mortem-Sceptic", "pre-mortem-sceptic")]
            public void Loader_CaseVariantCoreFilenameDoesNotSatisfyRequiredAndIsExposedAsInvalidConflict(
                string variantId,
                string canonicalId)
            {
                using var artifact = CatalogArtifact.Create();
                File.Delete(System.IO.Path.Combine(artifact.Agents, $"{canonicalId}.agent.md"));
                File.WriteAllText(
                    System.IO.Path.Combine(artifact.Agents, $"{variantId}.agent.md"),
                    $"---\nname: {variantId}\ndescription: A case-variant definition.\nrole: {canonicalId}\n---\nInstructions");

                var result = new AgentCatalogLoader().Load(artifact.Agents);

                var missingRequired = Assert.Single(result.Definitions, item =>
                    item.Record.Id == canonicalId);
                Assert.True(missingRequired.Record.Required);
                Assert.Equal(
                    AgentDefinitionStatus.Invalid,
                    missingRequired.Record.DefinitionStatus);
                Assert.Contains("was not found", missingRequired.Record.ValidationError);

                var conflict = Assert.Single(result.Definitions, item =>
                    item.Record.Id == variantId);
                Assert.False(conflict.Record.Required);
                Assert.Equal(AgentDefinitionStatus.Invalid, conflict.Record.DefinitionStatus);
                Assert.Contains("conflicts with reserved core agent", conflict.Record.ValidationError);
                Assert.Contains(canonicalId, conflict.Record.ValidationError);

                // Exactly these two entries represent this core slot: the missing
                // canonical placeholder and the invalid case-variant conflict.
                Assert.Equal(
                    2,
                    result.Definitions.Count(item =>
                        string.Equals(item.Record.Id, canonicalId, StringComparison.OrdinalIgnoreCase)));
            }

            [Fact]
            public void Loader_CaseVariantWithMatchingRoleStillDoesNotSatisfyRequiredDefinition()
            {
                using var artifact = CatalogArtifact.Create();
                File.Delete(System.IO.Path.Combine(artifact.Agents, "account-manager.agent.md"));
                // Even though 'role' matches the canonical id exactly, the filename
                // (and thus the agent ID) does not, so the required definition must
                // still be reported as missing rather than silently satisfied.
                File.WriteAllText(
                    System.IO.Path.Combine(artifact.Agents, "ACCOUNT-MANAGER.agent.md"),
                    "---\nname: Account Manager\ndescription: Handles intake.\nrole: account-manager\n---\nInstructions");

                var result = new AgentCatalogLoader().Load(artifact.Agents);

                var missingRequired = Assert.Single(result.Definitions, item =>
                    item.Record.Id == "account-manager");
                Assert.True(missingRequired.Record.Required);
                Assert.Equal(
                    AgentDefinitionStatus.Invalid,
                    missingRequired.Record.DefinitionStatus);

                var conflict = Assert.Single(result.Definitions, item =>
                    item.Record.Id == "ACCOUNT-MANAGER");
                Assert.False(conflict.Record.Required);
                Assert.Equal(AgentDefinitionStatus.Invalid, conflict.Record.DefinitionStatus);
            }

            [Fact]
            public async Task Catalog_CaseVariantConflictRemainsVisibleAcrossRepeatedReloads()
            {
                await using var artifact = await CatalogArtifact.CreateWithDatabaseAsync();
                File.Delete(System.IO.Path.Combine(
                    artifact.Agents,
                    "account-manager.agent.md"));
                File.WriteAllText(
                    System.IO.Path.Combine(
                        artifact.Agents,
                        "ACCOUNT-MANAGER.agent.md"),
                    "---\nname: Account Manager Variant\ndescription: Invalid case variant.\nrole: account-manager\n---\nInstructions");

                var first = await artifact.Catalog.ReloadAsync();
                var second = await artifact.Catalog.ReloadAsync();

                Assert.False(first.Ready);
                Assert.False(second.Ready);
                Assert.Contains(
                    artifact.Catalog.List(),
                    item =>
                        item.Id == "account-manager" &&
                        item.Required &&
                        item.DefinitionStatus == AgentDefinitionStatus.Invalid);
                Assert.Contains(
                    artifact.Catalog.List(),
                    item =>
                        item.Id == "ACCOUNT-MANAGER" &&
                        !item.Required &&
                        item.DefinitionStatus == AgentDefinitionStatus.Invalid);
            }

            [Fact]
            public async Task Catalog_TogglesAndReloadsAtomicallyWithLastKnownGood()
            {
                await using var artifact = await CatalogArtifact.CreateWithDatabaseAsync();
                var catalog = artifact.Catalog;
                var initial = await catalog.LoadAsync();
                Assert.True(initial.Ready);

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    catalog.ToggleAsync("account-manager", false));
                var toggled = await catalog.ToggleAsync("pre-mortem-sceptic", false);
                Assert.False(toggled.Enabled);
                var toggledRevision = catalog.Status().EffectiveRevision;
                Assert.NotEqual(initial.EffectiveRevision, toggledRevision);
                var snapshots = new FlowAgentSnapshotService(artifact.Factory, catalog);
                Guid existingFlowId;
                await using (var database = await artifact.Factory.CreateDbContextAsync())
                {
                    var existing = Flow("existing", FlowStatus.Intake);
                    existingFlowId = existing.Id;
                    database.Flows.Add(existing);
                    snapshots.CaptureForNewFlow(database, existing);
                    await database.SaveChangesAsync();
                }

                File.WriteAllText(
                    System.IO.Path.Combine(artifact.Agents, "team-lead.agent.md"),
                    "---\nname: Team Lead\n---\nInstructions");
                var failed = await catalog.ReloadAsync();
                Assert.False(failed.Ready);
                Assert.True(failed.HasEffectiveCatalog);
                Assert.Equal(toggledRevision, failed.EffectiveRevision);
                Assert.Equal(
                    "Team Lead",
                    (await snapshots.GetManifestAsync(existingFlowId, "team-lead")).Name);
                await using (var database = await artifact.Factory.CreateDbContextAsync())
                {
                    var blockedNew = Flow("blocked", FlowStatus.Intake);
                    Assert.Throws<NewWorkAdmissionException>(() =>
                        snapshots.CaptureForNewFlow(database, blockedNew));
                }

                CatalogArtifact.WriteAgent(
                    artifact.Agents,
                    "team-lead",
                    "Team Lead",
                    "Plans work.");
                var recovered = await catalog.ReloadAsync();
                Assert.True(recovered.Ready);
            }

            [Fact]
            public async Task SnapshotsRemainImmutableForNewFlows()
            {
                await using var artifact = await CatalogArtifact.CreateWithDatabaseAsync();
                await artifact.Catalog.LoadAsync();
                var snapshots = new FlowAgentSnapshotService(
                    artifact.Factory,
                    artifact.Catalog);
                Guid activeId;
                await using (var database = await artifact.Factory.CreateDbContextAsync())
                {
                    var active = Flow("active", FlowStatus.Intake);
                    activeId = active.Id;
                    database.Flows.Add(active);
                    snapshots.CaptureForNewFlow(database, active);
                    await database.SaveChangesAsync();
                }

                var original = await snapshots.GetManifestAsync(activeId, "account-manager");
                var originalAnalyst = await snapshots.GetManifestAsync(activeId, "analyst");
                var originalPreMortem = await snapshots.GetAgentsAsync(activeId);
                Assert.True(originalPreMortem.Single(item =>
                    item.Id == "pre-mortem-sceptic").Enabled);
                CatalogArtifact.WriteAgent(
                    artifact.Agents,
                    "account-manager",
                    "Changed Account Manager",
                    "Changed description.");
                await artifact.Catalog.ReloadAsync();
                await artifact.Catalog.ToggleAsync("pre-mortem-sceptic", false);
                File.Delete(System.IO.Path.Combine(
                    artifact.Agents,
                    "analyst.agent.md"));
                await artifact.Catalog.ReloadAsync();

                var retained = await snapshots.GetManifestAsync(activeId, "account-manager");
                Assert.Equal(original.DefinitionHash, retained.DefinitionHash);
                Assert.Equal(original.Instructions, retained.Instructions);
                Assert.Equal(
                    originalAnalyst.DefinitionHash,
                    (await snapshots.GetManifestAsync(activeId, "analyst")).DefinitionHash);
                Assert.True((await snapshots.GetAgentsAsync(activeId)).Single(item =>
                    item.Id == "pre-mortem-sceptic").Enabled);
                await using var verify = await artifact.Factory.CreateDbContextAsync();

                var newFlow = Flow("new", FlowStatus.Intake);
                verify.Flows.Add(newFlow);
                snapshots.CaptureForNewFlow(verify, newFlow);
                await verify.SaveChangesAsync();
                Assert.NotEqual(
                    (await verify.Flows.SingleAsync(item => item.Id == activeId))
                        .AgentCatalogRevision,
                    newFlow.AgentCatalogRevision);
                Assert.False(newFlow.AgentSnapshots.Single(item =>
                    item.AgentId == "pre-mortem-sceptic").EnabledAtSnapshot);
                Assert.DoesNotContain(
                    newFlow.AgentSnapshots,
                    item => item.AgentId == "analyst");
            }

            private static FlowRun Flow(string title, FlowStatus status) => new()
            {
                Title = title,
                OriginalRequest = title,
                Status = status,
            };

            private sealed class CatalogArtifact : IAsyncDisposable, IDisposable
            {
                private CatalogArtifact(string root)
                {
                    Root = root;
                    Agents = System.IO.Path.Combine(root, ".github", "agents");
                    System.IO.Directory.CreateDirectory(Agents);
                    WriteAgent(Agents, "account-manager", "Account Manager", "Handles intake.");
                    WriteAgent(Agents, "team-lead", "Team Lead", "Plans work.");
                    WriteAgent(
                        Agents,
                        "pre-mortem-sceptic",
                        "Pre-mortem Sceptic",
                        "Challenges assumptions.");
                    WriteAgent(Agents, "analyst", "Analyst", "Studies evidence.");
                }

                public string Root { get; }
                public string Agents { get; }
                public TestDbFactory Factory { get; private set; } = null!;
                public AgentCatalog Catalog { get; private set; } = null!;

                public static CatalogArtifact Create() =>
                    new(System.IO.Path.Combine(
                        AppContext.BaseDirectory,
                        "catalog-artifacts",
                        Guid.NewGuid().ToString("N")));

                public static async Task<CatalogArtifact> CreateWithDatabaseAsync()
                {
                    var artifact = Create();
                    artifact.Factory = new TestDbFactory(
                        System.IO.Path.Combine(artifact.Root, "catalog.db"));
                    await using var database = await artifact.Factory.CreateDbContextAsync();
                    await database.Database.EnsureCreatedAsync();
                    artifact.Catalog = new AgentCatalog(
                        new HarnessPaths(
                            artifact.Root,
                            artifact.Agents,
                            System.IO.Path.Combine(artifact.Root, "catalog.db")),
                        artifact.Factory);
                    return artifact;
                }

                public static void WriteAgent(
                    string directory,
                    string id,
                    string name,
                    string description) =>
                    File.WriteAllText(
                        System.IO.Path.Combine(directory, $"{id}.agent.md"),
                        $"---\nname: {name}\ndescription: {description}\n---\n\n# Instructions\n\nDo the assigned work.");

                public void Dispose()
                {
                    if (System.IO.Directory.Exists(Root))
                    {
                        System.IO.Directory.Delete(Root, recursive: true);
                    }
                }

                public ValueTask DisposeAsync()
                {
                    Dispose();
                    return ValueTask.CompletedTask;
                }
            }

            private sealed class TestDbFactory(string databasePath)
                : IDbContextFactory<HarnessDbContext>
            {
                private readonly DbContextOptions<HarnessDbContext> _options =
                    new DbContextOptionsBuilder<HarnessDbContext>()
                        .UseSqlite($"Data Source={databasePath};Pooling=False")
                        .Options;

                public HarnessDbContext CreateDbContext() => new(_options);

                public Task<HarnessDbContext> CreateDbContextAsync(
                    CancellationToken cancellationToken = default) =>
                    Task.FromResult(CreateDbContext());
            }
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

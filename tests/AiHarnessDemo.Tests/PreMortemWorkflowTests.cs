using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Data;
using AiHarnessDemo.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiHarnessDemo.Tests;

public sealed class PreMortemWorkflowTests
{
    [Fact]
    public void PreMortemAssignment_TreatsFailureAsCounterfactualRequirementsGapNotQa()
    {
        var author = new FlowStep
        {
            AgentId = "analyst",
            AgentName = "Analyst",
            AgentRole = "analyst",
            PlanDutiesJson = """["Analyze"]""",
            OutputSummary = "HANDOFF_STATUS: COMPLETE\nProposed requirements."
        };

        var assignment = WorkflowEngine.BuildPreMortemAssignment(author);

        Assert.Contains("implemented them exactly", assignment);
        Assert.Contains("not historical facts", assignment);
        Assert.Contains("why faithful implementation could still fail", assignment);
        Assert.Contains("do not inspect implementation code", assignment);
        Assert.Contains("Return CLEAR if no requirements-level gap is substantiated", assignment);
        Assert.Contains(author.OutputSummary, assignment);
        Assert.DoesNotContain("QA assessment is a valid handoff", assignment);
    }

    [Fact]
    public async Task ModelFamilyViolation_PersistsARestartableFailedReview()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-premortem-route-{Guid.NewGuid():N}");
        var agentsDirectory = Path.Combine(root, ".github", "agents");
        var workspacePath = Path.Combine(root, "workspace");
        var databasePath = Path.Combine(root, "harness.db");
        Directory.CreateDirectory(agentsDirectory);
        Directory.CreateDirectory(workspacePath);
        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;
        var databaseFactory = new PreMortemDbContextFactory(options);
        var flow = new FlowRun
        {
            Title = "Route review",
            OriginalRequest = "Route review",
            ConsolidatedRequest = "Review the implementation.",
            Status = FlowStatus.Running,
            RepositoryPath = root,
            RepositoryKnowledge = "Test repository."
        };
        var target = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = 1,
            Sequence = 10,
            AgentId = "software-engineer",
            AgentName = "Software Engineer",
            AgentRole = "software-engineer",
            Status = StepStatus.Completed,
            Model = "claude-opus-5",
            OutputSummary = "HANDOFF_STATUS: COMPLETE"
        };
        var review = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = 1,
            Sequence = 20,
            AgentId = WorkflowEngine.PreMortemRole,
            AgentName = "Pre-mortem Sceptic",
            AgentRole = WorkflowEngine.PreMortemRole,
            Status = StepStatus.Pending,
            Attempt = 1,
            PreMortemOriginStepId = target.Id,
            PreMortemTargetStepId = target.Id
        };
        await using (var database = await databaseFactory.CreateDbContextAsync())
        {
            await database.Database.EnsureCreatedAsync();
            database.Flows.Add(flow);
            database.FlowSteps.AddRange(target, review);
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
        using var handoffGate = new HandoffGateEngine();
        var engine = new WorkflowEngine(
            databaseFactory,
            new AgentCatalog(paths, databaseFactory),
            new WrongFamilyModelRouter(),
            new BootstrapTaskProfileFactory(),
            TestRoutingSupport.Recorder(databaseFactory),
            new PreMortemWorkspaceManager(workspacePath),
            new PreMortemAgentRunner(),
            handoffGate,
            new CopilotSessionJournal(),
            workflowProvider,
            NullLogger<WorkflowEngine>.Instance);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.ExecuteStepAsync(
                    flow.Id,
                    review.Id,
                    workspacePath,
                    "Software Engineer -> Pre-mortem Sceptic",
                    3,
                    CancellationToken.None));

            await using var database = await databaseFactory.CreateDbContextAsync();
            var stored = await database.FlowSteps.SingleAsync(item => item.Id == review.Id);
            Assert.Contains("different family", exception.Message);
            Assert.Equal(StepStatus.Failed, stored.Status);
            Assert.Equal(AgentRunPhase.Failed, stored.Phase);
            Assert.Contains(
                await database.FlowEvents.ToListAsync(),
                item =>
                    item.FlowStepId == review.Id &&
                    item.Type == "step.failed");
        }
        finally
        {
            workflowProvider.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshottedRun_UsesCapturedScepticAcrossAllRounds(
        bool correctInvalidReview)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-premortem-{Guid.NewGuid():N}");
        var agentsDirectory = Path.Combine(root, ".github", "agents");
        var workspacePath = Path.Combine(root, "workspace");
        var databasePath = Path.Combine(root, "harness.db");
        Directory.CreateDirectory(agentsDirectory);
        Directory.CreateDirectory(workspacePath);
        foreach (var (id, name) in new[]
                 {
                     ("account-manager", "Account Manager"),
                     ("team-lead", "Team Lead"),
                     ("software-engineer", "Software Engineer"),
                     ("pre-mortem-sceptic", "Pre-mortem Sceptic"),
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
        var databaseFactory = new PreMortemDbContextFactory(options);
        var flow = new FlowRun
        {
            Title = "Implement feature",
            OriginalRequest = "Implement feature",
            ConsolidatedRequest =
                "Implement a focused product feature. " +
                new string('x', 900) + " Complete customer requirement.",
            Kind = FlowKind.Advisory,
            Status = FlowStatus.Queued,
            RepositoryPath = root,
            RepositoryKnowledge = "Test repository.",
            Outcome = OutcomeType.None,
            ModelSelectionStrategy = ModelSelectionStrategy.MaximumQuality
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
        var catalog = new AgentCatalog(paths, databaseFactory);
        var catalogStatus = await catalog.LoadAsync();
        Assert.True(catalogStatus.Ready, catalogStatus.LastError);
        var snapshots =
            new FlowAgentSnapshotService(databaseFactory, catalog);
        await using (var database =
                     await databaseFactory.CreateDbContextAsync())
        {
            var stored = await database.Flows.SingleAsync(
                item => item.Id == flow.Id);
            snapshots.CaptureForNewFlow(database, stored);
            await database.SaveChangesAsync();
        }
        _ = await catalog.ToggleAsync(
            WorkflowEngine.PreMortemRole,
            enabled: false);
        var runner = new PreMortemAgentRunner(correctInvalidReview);
        var router = new FamilyAwareModelRouter(databaseFactory);
        using var handoffGate = new HandoffGateEngine();
        handoffGate.SetTrustLevel(HandoffActionType.Advance, HandoffTrustLevel.Auto);
        handoffGate.SetTrustLevel(HandoffActionType.RequestRevision, HandoffTrustLevel.Auto);
        var engine = new WorkflowEngine(
            databaseFactory,
            catalog,
            router,
            new BootstrapTaskProfileFactory(),
            TestRoutingSupport.Recorder(databaseFactory),
            new PreMortemWorkspaceManager(workspacePath),
            runner,
            handoffGate,
            new CopilotSessionJournal(),
            workflowProvider,
            NullLogger<WorkflowEngine>.Instance,
            flowAgentSnapshotService: snapshots);

        try
        {
            await engine.RunAsync(flow.Id, CancellationToken.None);

            await using var database = await databaseFactory.CreateDbContextAsync();
            var stored = await database.Flows
                .Include(item => item.Steps)
                .Include(item => item.Events)
                .SingleAsync(item => item.Id == flow.Id);
            var softwareSteps = stored.Steps
                .Where(item => item.AgentRole == "software-engineer")
                .OrderBy(item => item.Sequence)
                .ToList();
            var reviews = stored.Steps
                .Where(item => item.AgentRole == WorkflowEngine.PreMortemRole)
                .OrderBy(item => item.Sequence)
                .ToList();
            var softwareRuns = runner.Contexts
                .Where(item => item.AgentRole == "software-engineer")
                .ToList();
            var reviewRuns = runner.Contexts
                .Where(item => item.AgentRole == WorkflowEngine.PreMortemRole)
                .ToList();

            Assert.Equal(FlowStatus.WaitingForFeedback, stored.Status);
            Assert.Equal(3, softwareSteps.Count);
            var expectedReviews = 2;
            Assert.Equal(expectedReviews, reviews.Count);
            Assert.All(softwareSteps, step => Assert.Equal("claude-sonnet-5", step.Model));
            Assert.All(reviews, step => Assert.Equal("gpt-5.6-sol", step.Model));
            Assert.Equal(3, softwareRuns.Count);
            Assert.Single(softwareRuns.Select(item => item.CopilotSessionId).Distinct());
            Assert.False(softwareRuns[0].ResumeSession);
            Assert.All(softwareRuns.Skip(1), context => Assert.True(context.ResumeSession));
            Assert.Equal(expectedReviews, reviewRuns.Count);
            Assert.Equal(
                expectedReviews,
                reviewRuns.Select(item => item.CopilotSessionId).Distinct().Count());
            Assert.All(reviewRuns, context => Assert.False(context.ResumeSession));
            Assert.All(
                router.PreMortemRequests,
                request => Assert.Contains(
                    "anthropic",
                    request.ExcludedModelFamilies ?? []));
            Assert.Contains(
                PreMortemRules.FindingsBeginSentinel,
                softwareRuns[1].Task);
            Assert.Contains(
                "Adjusted deliverable 2",
                reviewRuns[^1].Task);
            Assert.Contains(
                softwareSteps[0].OutputSummary,
                reviewRuns[0].Task,
                StringComparison.Ordinal);
            Assert.All(reviewRuns, context =>
                Assert.Contains("Complete customer requirement.", context.Task, StringComparison.Ordinal));
            if (correctInvalidReview)
            {
                Assert.Equal(1, reviewRuns[0].Attempt);
                var framing = Assert.Single(stored.Events, item =>
                    item.Type == "agent.pre-mortem-framing-recovered");
                Assert.Contains("Investigation complete.", framing.DataJson);
                Assert.StartsWith(PreMortemRules.FindingsStatus, reviews[0].OutputSummary);
                Assert.Single(PreMortemRules.ParseReview(reviews[0].OutputSummary).Findings);
                Assert.DoesNotContain(stored.Events, item =>
                    item.Type == "agent.contract-correction-scheduled");
            }
            Assert.Contains(
                stored.Events,
                item => item.Type == "premortem.round-limit-exhausted");
            Assert.Equal(
                2,
                stored.Events.Count(item => item.Type == "premortem.findings"));
            Assert.Equal(
                expectedReviews,
                stored.Events.Count(item =>
                    item.Type == "premortem.model-family-separated"));
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

    private sealed class PreMortemAgentRunner(bool correctInvalidReview = false) : IAgentRunner
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

            var output = context.AgentRole switch
            {
                "team-lead" => TeamLeadOutput,
                "software-engineer" when context.Attempt == 1 => """
                    HANDOFF_STATUS: COMPLETE

                    ## Decision
                    Initial implementation is complete.

                    ## Deliverable
                    Initial deliverable.

                    ## Evidence
                    Focused tests passed.

                    ## Next owner
                    Continue the planned flow.
                    """,
                "software-engineer" => $"""
                    HANDOFF_STATUS: COMPLETE

                    ## Decision
                    The evidence was accepted and the result was adjusted.

                    ## Deliverable
                    Adjusted deliverable {context.Attempt}.

                    ## Evidence
                    The cited failure chain is now prevented.

                    ## Next owner
                    Continue the planned flow.

                    {PreMortemRules.AdjustedDisposition}
                    """,
                "pre-mortem-sceptic" => $$"""
                    {{PreMortemRules.FindingsStatus}}
                    {{PreMortemRules.FindingsBeginSentinel}}
                    {"Findings":[{"FailureMode":"The handoff fails after six months because its compatibility boundary is incomplete.","Evidence":"src\\contract.cs and the evaluated output show no compatibility guarantee.","MissedSignal":"The public boundary has no stated compatibility behavior.","Prevention":"State and verify the compatibility behavior before the next handoff."}]}
                    {{PreMortemRules.FindingsEndSentinel}}
                    """,
                _ => """
                    HANDOFF_STATUS: COMPLETE

                    ## Decision
                    The assigned role is complete.

                    ## Deliverable
                    The downstream handoff is unblocked.

                    ## Evidence
                    Focused validation passed.

                    ## Next owner
                    Continue the planned flow.
                    """
            };
            if (context.AgentRole == "software-engineer" && context.Attempt == 1)
            {
                output += Environment.NewLine + new string('x', 8_500) +
                          Environment.NewLine + "Complete handoff tail.";
            }
            if (correctInvalidReview &&
                context.AgentRole == WorkflowEngine.PreMortemRole &&
                Contexts.Count(item => item.AgentRole == WorkflowEngine.PreMortemRole) == 1)
            {
                output = "Investigation complete." + Environment.NewLine + output;
            }
            if (context.IsOutcomeOwner)
            {
                output +=
                    Environment.NewLine +
                    FlowOutcomeParser.BeginSentinel +
                    Environment.NewLine +
                    """
                    {"Goal":"Assess the feature.","Summary":"The assessment is complete.","ImplementationDetails":["The requested pre-mortem findings were incorporated."],"Artifacts":[]}
                    """ +
                    Environment.NewLine +
                    FlowOutcomeParser.EndSentinel;
            }
            return Task.FromResult(new AgentExecutionResult(
                output,
                "Fake runner evidence.",
                1,
                []));
        }

        private const string TeamLeadOutput = """
            HANDOFF_STATUS: COMPLETE

            ## Decision
            Use one pre-mortem checkpoint.

            ## Deliverable
            The fixed team sequence is profiled.

            ## Evidence
            The implementation boundary carries the material risk.

            ## Next owner
            Software Engineer.

            TEAM_PLAN_BEGIN
            {"Disposition":"Planned","Steps":[{"Id":"assess","AgentId":"software-engineer","Order":10,"Stage":"BeforeReview","Assignment":"Assess the requested feature and prepare the outcome.","Justification":"The captured engineer owns the assessment.","DependsOn":[],"Duties":["Analyze","PrepareOutcome"],"OutcomeOwner":true,"TaskProfile":{"Complexity":7,"ReasoningDepth":8,"ContextDemand":7,"ToolIntensity":8,"TaskTypeTags":["Implementation"],"Risk":"Critical","RiskReason":"The assessment covers a compatibility boundary.","Confidence":0.9,"Rationales":["Focused validation is required."]}}],"PreMortemCheckpoints":["assess"],"AcceptanceCriteria":null,"MissingQualification":null}
            TEAM_PLAN_END
            """;
    }

    private sealed class FamilyAwareModelRouter(
        IDbContextFactory<HarnessDbContext> databaseFactory) : IModelRouter
    {
        public List<RoutingRequest> PreMortemRequests { get; } = [];

        public async Task<RoutingDecision> SelectAsync(
            RoutingRequest request,
            CancellationToken cancellationToken = default)
        {
            await using var database =
                await databaseFactory.CreateDbContextAsync(cancellationToken);
            var role = await database.FlowSteps
                .Where(item => item.Id == request.FlowStepId)
                .Select(item => item.AgentRole)
                .SingleAsync(cancellationToken);
            var isPreMortem = role == WorkflowEngine.PreMortemRole;
            if (isPreMortem)
            {
                PreMortemRequests.Add(request);
            }

            return new RoutingDecision
            {
                FlowStepId = request.FlowStepId,
                TaskProfileId = Guid.NewGuid(),
                ModelCatalogSnapshotId = Guid.NewGuid(),
                SelectedModel = isPreMortem ? "gpt-5.6-sol" : "claude-sonnet-5",
                SelectedEffort = "high",
                Strategy = request.Strategy,
                PredictedQuality = 0.95,
                PredictedAcceptedTimeSeconds = 10,
                PredictedPremiumRequests = 1,
                Confidence = 0.8,
                Uncertainty = 0.2,
                Reason = "Deterministic family-aware test route."
            };
        }
    }

    private sealed class WrongFamilyModelRouter : IModelRouter
    {
        public Task<RoutingDecision> SelectAsync(
            RoutingRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RoutingDecision
            {
                FlowStepId = request.FlowStepId,
                TaskProfileId = Guid.NewGuid(),
                ModelCatalogSnapshotId = Guid.NewGuid(),
                SelectedModel = "claude-sonnet-5",
                SelectedEffort = "high",
                Strategy = request.Strategy,
                Reason = "Invalid same-family route."
            });
    }

    private sealed class PreMortemWorkspaceManager(string path) : IWorkspaceManager
    {
        public Task<WorkspaceInfo> PrepareAsync(
            FlowRun flow,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkspaceInfo(path, "ai-harness/test", CreatedNow: false));
    }

    private sealed class PreMortemDbContextFactory(
        DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);

        public Task<HarnessDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HarnessDbContext(options));
    }
}

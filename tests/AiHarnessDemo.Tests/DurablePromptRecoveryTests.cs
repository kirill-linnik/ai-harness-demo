using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Data;
using AiHarnessDemo.Infrastructure;
using AiHarnessDemo.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiHarnessDemo.Tests;

public sealed class DurablePromptRecoveryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("## Assignment\n\n{\"Goal\":\"Original brief.\",\"Details\":[],\"SuccessCriteria\":[],\"Constraints\":[],\"Assumptions\":[]}")]
    public async Task InterruptedAttempt_UsesExactPromptAndRevisionAfterWorkflowReload(
        string? capturedPrompt)
    {
        await using var fixture =
            await DurablePromptFixture.CreateAsync(executionPrompt: capturedPrompt);
        var originalPrompt = fixture.OriginalPrompt;
        var originalRevision = fixture.OriginalRevision;
        var current = await fixture.ReloadWorkflowAsync(
            "MATERIALLY CHANGED {{ task }} :: {{ agent.instructions }}");

        var recovered =
            await CopilotReasoningHost
                .RenderAndPersistExecutionInstructionsAsync(
                    fixture.Context with
                    {
                        Task = "A task that must not replace the original.",
                        RecoverInterruptedSession = true,
                        ResumeSession = true
                    },
                    current,
                    "Changed role instructions that must not be rendered.",
                    fixture.WorkspacePath,
                    stagedPromotion: null,
                    new WorkflowPromptRenderer(),
                    fixture.Factory,
                    CancellationToken.None);

        Assert.True(recovered.Recovered);
        Assert.Equal(originalPrompt, recovered.Prompt);
        Assert.Equal(originalRevision, recovered.WorkflowRevision);
        Assert.DoesNotContain(
            "MATERIALLY CHANGED",
            recovered.Prompt,
            StringComparison.Ordinal);

        var stored = await fixture.LoadStepAsync();
        Assert.Equal(originalPrompt, stored.ExecutionPrompt);
        Assert.Equal(originalRevision, stored.WorkflowRevision);
    }

    [Fact]
    public async Task FreshAttempt_PersistsAndStagesReadableBriefWithoutRewritingStoredJson()
    {
        const string brief =
            """{"Goal":"Refresh both sites.","Details":["Use the same design."],"SuccessCriteria":["Both sites look modern."],"Constraints":["Keep the logo unchanged."],"Assumptions":[]}""";
        await using var fixture =
            await DurablePromptFixture.CreateAsync(executionPrompt: string.Empty);
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows.SingleAsync(item => item.Id == fixture.FlowId);
            flow.ConsolidatedRequest = brief;
            await database.SaveChangesAsync();
        }
        var workflow = await fixture.ReloadWorkflowAsync(
            "## Assignment\n\n{{ task }}\n\n## Role contract\n\n{{ agent.instructions }}");
        var context = fixture.Context with
        {
            Task = WorkflowEngine.BuildStepTask(brief, "Implement the approved design.")
        };

        var instructions = await CopilotReasoningHost.RenderAndPersistExecutionInstructionsAsync(
            context,
            workflow,
            "Preserve the agreed scope.",
            fixture.WorkspacePath,
            stagedPromotion: null,
            new WorkflowPromptRenderer(),
            fixture.Factory,
            CancellationToken.None);
        var stager = new AgentManifestStager();
        var stagedAgent = await stager.StageAsync(
            fixture.CopilotHome,
            DurablePromptFixture.Manifest(),
            fixture.SessionId);
        var stagedPrompt = await stager.StagePromptAsync(
            stagedAgent,
            instructions.Prompt,
            fixture.SessionId,
            fixture.FlowId,
            fixture.StepId,
            attempt: 1);

        Assert.Contains("### Goal\n\nRefresh both sites.", instructions.Prompt, StringComparison.Ordinal);
        Assert.Contains(
            "### Constraints\n\n- Keep the logo unchanged.",
            instructions.Prompt,
            StringComparison.Ordinal);
        Assert.DoesNotContain(brief, instructions.Prompt, StringComparison.Ordinal);
        Assert.Equal(instructions.Prompt, await File.ReadAllTextAsync(stagedPrompt.Path));
        var stored = await fixture.LoadStepAsync();
        Assert.Equal(instructions.Prompt, stored.ExecutionPrompt);
        Assert.Equal(workflow.Revision, stored.WorkflowRevision);
        await using var persisted = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(
            brief,
            await persisted.Flows
                .Where(flow => flow.Id == fixture.FlowId)
                .Select(flow => flow.ConsolidatedRequest)
                .SingleAsync());
    }

    [Fact]
    public async Task InterruptedLongPrompt_ReusesOrRecreatesOriginalDigest()
    {
        var originalPrompt =
            "original-long-prompt:" +
            new string('p', 40_000) +
            ":original-end";
        await using var fixture =
            await DurablePromptFixture.CreateAsync(
                originalPrompt,
                ExecutionInvocationKind.Worker);
        var stager = new AgentManifestStager();
        var stagedAgent = await stager.StageAsync(
            fixture.CopilotHome,
            DurablePromptFixture.Manifest(),
            fixture.SessionId);
        var initial = await stager.StagePromptAsync(
            stagedAgent,
            originalPrompt,
            fixture.SessionId,
            fixture.FlowId,
            fixture.StepId,
            attempt: 1);
        var current = await fixture.ReloadWorkflowAsync(
            "CHANGED REVIEW TEMPLATE {{ task }}");
        var recovered =
            await CopilotReasoningHost
                .RenderAndPersistExecutionInstructionsAsync(
                    fixture.Context with
                    {
                        RecoverInterruptedSession = true,
                        ResumeSession = true,
                        RequiresDeliveryReadinessQa = true
                    },
                    current,
                    "Changed review instructions.",
                    fixture.WorkspacePath,
                    stagedPromotion: null,
                    new WorkflowPromptRenderer(),
                    fixture.Factory,
                    CancellationToken.None);
        var reused = await stager.StagePromptAsync(
            stagedAgent,
            recovered.Prompt,
            fixture.SessionId,
            fixture.FlowId,
            fixture.StepId,
            attempt: 1);

        Assert.True(reused.Reused);
        Assert.Equal(initial.Sha256, reused.Sha256);
        Assert.Equal(
            OutcomeVerificationRules.ComputeSha256(originalPrompt),
            reused.Sha256);

        stager.CleanupPrompt(reused);
        var recreated = await stager.StagePromptAsync(
            stagedAgent,
            recovered.Prompt,
            fixture.SessionId,
            fixture.FlowId,
            fixture.StepId,
            attempt: 1);
        Assert.False(recreated.Reused);
        Assert.Equal(initial.Sha256, recreated.Sha256);
        Assert.Equal(
            originalPrompt,
            await File.ReadAllTextAsync(recreated.Path));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("Persisted prompt.", "not-a-workflow-revision")]
    public async Task InterruptedAttempt_InvalidPersistedInstructionsFailBeforeLaunch(
        string executionPrompt,
        string? invalidRevision)
    {
        await using var fixture =
            await DurablePromptFixture.CreateAsync(
                executionPrompt,
                ExecutionInvocationKind.Worker,
                invalidRevision);
        var current = await fixture.ReloadWorkflowAsync(
            "CHANGED TEMPLATE THAT MUST NOT LAUNCH {{ task }}");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CopilotReasoningHost
                .RenderAndPersistExecutionInstructionsAsync(
                    fixture.Context with
                    {
                        RecoverInterruptedSession = true,
                        ResumeSession = true
                    },
                    current,
                    "Changed instructions.",
                    fixture.WorkspacePath,
                    stagedPromotion: null,
                    new WorkflowPromptRenderer(),
                    fixture.Factory,
                    CancellationToken.None));

        Assert.Contains(
            invalidRevision is null
                ? "no persisted exact execution prompt"
                : "invalid persisted workflow revision",
            exception.Message,
            StringComparison.Ordinal);
        var stored = await fixture.LoadStepAsync();
        Assert.Equal(executionPrompt, stored.ExecutionPrompt);
        Assert.Equal(
            invalidRevision ?? fixture.OriginalRevision,
            stored.WorkflowRevision);
    }

    [Fact]
    public async Task ExplicitRetry_CanBindCurrentWorkflowAndPersistsFullPrompt()
    {
        await using var fixture =
            await DurablePromptFixture.CreateAsync(
                executionPrompt: string.Empty,
                invocationKind: ExecutionInvocationKind.Worker);
        var current = await fixture.ReloadWorkflowAsync(
            "CURRENT RETRY {{ task }} :: {{ agent.instructions }}");
        var retryContext = fixture.Context with
        {
            Task = "Retry the durable assignment.",
            ResumeSession = true,
            RecoverInterruptedSession = false
        };

        var retry =
            await CopilotReasoningHost
                .RenderAndPersistExecutionInstructionsAsync(
                    retryContext,
                    current,
                    "Use the current retry contract.",
                    fixture.WorkspacePath,
                    stagedPromotion: null,
                    new WorkflowPromptRenderer(),
                    fixture.Factory,
                    CancellationToken.None);

        Assert.False(retry.Recovered);
        Assert.Equal(current.Revision, retry.WorkflowRevision);
        Assert.Contains("CURRENT RETRY", retry.Prompt, StringComparison.Ordinal);
        Assert.Contains(
            "Retry the durable assignment.",
            retry.Prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Use the current retry contract.",
            retry.Prompt,
            StringComparison.Ordinal);
        Assert.NotEqual(fixture.OriginalRevision, retry.WorkflowRevision);

        var stored = await fixture.LoadStepAsync();
        Assert.Equal(retry.Prompt, stored.ExecutionPrompt);
        Assert.Equal(current.Revision, stored.WorkflowRevision);
        Assert.NotEqual(retryContext.Task, stored.ExecutionPrompt);
    }

    private sealed class DurablePromptFixture : IAsyncDisposable
    {
        private DurablePromptFixture(
            string root,
            string workspacePath,
            string copilotHome,
            Guid flowId,
            Guid stepId,
            Guid sessionId,
            DurablePromptDbContextFactory factory,
            WorkflowDefinitionProvider workflowProvider,
            AgentExecutionContext context,
            string originalPrompt,
            string originalRevision)
        {
            Root = root;
            WorkspacePath = workspacePath;
            CopilotHome = copilotHome;
            FlowId = flowId;
            StepId = stepId;
            SessionId = sessionId;
            Factory = factory;
            WorkflowProvider = workflowProvider;
            Context = context;
            OriginalPrompt = originalPrompt;
            OriginalRevision = originalRevision;
        }

        public string Root { get; }

        public string WorkspacePath { get; }

        public string CopilotHome { get; }

        public Guid FlowId { get; }

        public Guid StepId { get; }

        public Guid SessionId { get; }

        public DurablePromptDbContextFactory Factory { get; }

        public WorkflowDefinitionProvider WorkflowProvider { get; }

        public AgentExecutionContext Context { get; }

        public string OriginalPrompt { get; }

        public string OriginalRevision { get; }

        public static async Task<DurablePromptFixture> CreateAsync(
            string? executionPrompt = null,
            ExecutionInvocationKind invocationKind =
                ExecutionInvocationKind.Worker,
            string? workflowRevisionOverride = null)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"ai-harness-durable-prompt-{Guid.NewGuid():N}");
            var workspace = Path.Combine(root, "workspace");
            var copilotHome = Path.Combine(root, "copilot-home");
            var agents = Path.Combine(root, ".github", "agents");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(agents);
            await WriteWorkflowAsync(
                root,
                "ORIGINAL WORKFLOW {{ task }} :: {{ agent.instructions }}");
            var paths = new HarnessPaths(
                root,
                agents,
                Path.Combine(root, "harness.db"));
            var provider = new WorkflowDefinitionProvider(
                paths,
                new WorkflowLoader(),
                NullLogger<WorkflowDefinitionProvider>.Instance);
            var originalWorkflow = provider.GetEffective();
            var flow = new FlowRun
            {
                Title = "Durable prompt recovery",
                OriginalRequest = "Preserve the original execution instructions.",
                ConsolidatedRequest =
                    "Preserve the original execution instructions.",
                Kind = FlowKind.Delivery,
                Status = FlowStatus.Running,
                RepositoryPath = root,
                RepositoryKnowledge = "A prompt recovery fixture.",
                WorkspacePath = workspace
            };
            var sessionId = Guid.NewGuid();
            var step = new FlowStep
            {
                FlowRunId = flow.Id,
                Iteration = 1,
                Sequence = 10,
                AgentId = "software-engineer",
                AgentName = "Software Engineer",
                AgentRole = "software-engineer",
                Label = "Execute the durable assignment",
                PlanStepKey = "implement",
                PlanDutiesJson = """["Implement"]""",
                InvocationKind = invocationKind,
                Status = StepStatus.Running,
                Phase = AgentRunPhase.StreamingTurn,
                Attempt = 1,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                CopilotSessionId = sessionId,
                CopilotSessionHome = copilotHome,
                WorkflowRevision =
                    workflowRevisionOverride ?? originalWorkflow.Revision
            };
            flow.Steps.Add(step);
            var context = new AgentExecutionContext(
                flow.Id,
                flow.Iteration,
                step.AgentId,
                step.AgentName,
                step.AgentRole,
                "model",
                "high",
                step.Attempt,
                "Original durable task.",
                flow.RepositoryKnowledge,
                flow.RepositoryPath,
                flow.WorkspacePath,
                sessionId,
                OutcomeType.PullRequest,
                "Original plan.",
                [],
                [],
                FlowStepId: step.Id,
                InvocationKind: invocationKind,
                PlanStepKey: step.PlanStepKey,
                FlowKind: flow.Kind);
            var originalPrompt = executionPrompt ??
                CopilotReasoningHost.BoundRenderedPrompt(
                    context,
                    new WorkflowPromptRenderer().Render(
                        originalWorkflow.PromptTemplate,
                        CopilotReasoningHost.BuildPromptValues(
                            context,
                            "Original role instructions.",
                            workspace)));
            step.ExecutionPrompt = originalPrompt;

            var options = new DbContextOptionsBuilder<HarnessDbContext>()
                .UseSqlite(
                    $"Data Source={paths.DatabasePath};Pooling=False")
                .Options;
            var factory = new DurablePromptDbContextFactory(options);
            await using (var database =
                         await factory.CreateDbContextAsync())
            {
                await database.Database.EnsureCreatedAsync();
                database.Flows.Add(flow);
                await database.SaveChangesAsync();
            }
            return new DurablePromptFixture(
                root,
                workspace,
                copilotHome,
                flow.Id,
                step.Id,
                sessionId,
                factory,
                provider,
                context,
                originalPrompt,
                originalWorkflow.Revision);
        }

        public async Task<WorkflowDefinition> ReloadWorkflowAsync(
            string promptTemplate)
        {
            await WriteWorkflowAsync(Root, promptTemplate);
            await WorkflowProvider.ReloadAsync();
            var current = WorkflowProvider.GetEffective();
            Assert.NotEqual(OriginalRevision, current.Revision);
            return current;
        }

        public async Task<FlowStep> LoadStepAsync()
        {
            await using var database =
                await Factory.CreateDbContextAsync();
            return await database.FlowSteps
                .AsNoTracking()
                .SingleAsync(step => step.Id == StepId);
        }

        public ValueTask DisposeAsync()
        {
            WorkflowProvider.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
            return ValueTask.CompletedTask;
        }

        public static AgentManifest Manifest() =>
            new(
                "software-engineer",
                "Software Engineer",
                "Implements the requested change.",
                "software-engineer",
                "blue",
                10,
                "software-engineer.agent.md",
                "Complete the assigned work.");

        private static Task WriteWorkflowAsync(
            string root,
            string promptTemplate) =>
            File.WriteAllTextAsync(
                Path.Combine(root, "WORKFLOW.md"),
                $$"""
                  ---
                  workspace:
                    root: workspace
                  agent:
                    max_concurrent_agents: 1
                    max_attempts: 1
                  studio:
                    flow_kinds:
                      advisory:
                        required_duties: [PrepareOutcome]
                        maximum_permission: ReadOnlySource
                      delivery:
                        required_duties: [Implement, Verify, PrepareOutcome, Publish]
                        pre_review_maximum_permission: WorkspaceWrite
                        post_approval_maximum_permission: Publish
                  ---

                  {{promptTemplate}}
                  """);
    }

    private sealed class DurablePromptDbContextFactory(
        DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);

        public Task<HarnessDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}

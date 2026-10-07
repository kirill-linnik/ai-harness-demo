using AiHarnessDemo.Contracts;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Data;
using AiHarnessDemo.Infrastructure;
using AiHarnessDemo.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiHarnessDemo.Tests;

/// <summary>
/// Deterministic replay of the regression scenario derived from Delivery flow
/// <c>36aecd50-3007-4eeb-87a2-e3cd02576dcb</c>.
///
/// Inputs come from the checked-in current-contract fixture. The recorded result was
/// <c>Status = Approved</c>, <c>PublicationStatus = Published</c>, and an accepted
/// <c>CustomerReview</c> — even though the outcome Summary says the result is "not yet ready for
/// unconditional customer sign-off" and lists four confirmed gaps.
///
/// The comparison is on machine-checkable facts only: host-derived readiness, gate creation,
/// acceptance authorization, publisher call count, and flow status.
/// </summary>
public sealed class IncidentReplayTests
{
    [Fact]
    public void CapturedReferenceFlow_IsTheContradictoryApprovedIncident()
    {
        var reference = ReferenceFlowArtifact.Current;

        // The captured baseline: the pre-change host approved and published this flow.
        Assert.Equal(
            Guid.Parse("36aecd50-3007-4eeb-87a2-e3cd02576dcb"),
            reference.Id);
        Assert.Equal("Delivery", reference.Kind);
        Assert.Equal("Approved", reference.Status);
        Assert.Equal("Published", reference.PublicationStatus);
        Assert.True(reference.Review.Resolved);
        Assert.True(reference.Review.Approved);
        Assert.Equal("Accepted", reference.Review.Decision);
        Assert.NotNull(reference.Review.GateId);

        // The very same result openly contradicts that approval.
        Assert.Contains(
            "not yet ready for unconditional customer sign-off",
            reference.Outcome.Summary,
            StringComparison.Ordinal);
        Assert.Equal(
            4,
            reference.Outcome.ImplementationDetails.Count(detail =>
                detail.StartsWith("CONFIRMED GAP", StringComparison.Ordinal)));
        // The pre-change phrase heuristic had nothing to fire on in that outcome text.
        Assert.False(
            WorkflowEngine.ContainsContradictoryQualityProse(reference.Outcome.Summary));
        Assert.False(
            WorkflowEngine.ContainsContradictoryQualityProse(
                string.Join("\n", reference.Outcome.ImplementationDetails)));

        // The recorded worker plan shape the replay reproduces.
        Assert.Equal(
            ["analyst", "product-designer", "software-engineer", "quality-engineer",
             "release-engineer"],
            reference.StepRoles
                .Distinct(StringComparer.Ordinal)
                .Where(role => role is not ("account-manager" or "team-lead"
                    or "pre-mortem-sceptic")));
    }
    [Fact]
    public async Task IncidentReplay_ContradictoryOutcomeCannotReachReviewAcceptanceOrPublication()
    {
        var reference = ReferenceFlowArtifact.Current;
        await using var harness = await IncidentHarness.CreateAsync();
        harness.Runner.QaBlock = context => ReferenceFlowContracts.Block(ReferenceFlowContracts.ContradictoryQaJson(context.OutcomeContext));

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var flow = await harness.LoadFlowAsync();

        Assert.True(flow.Status != FlowStatus.Failed, flow.FailureReason);
        // The replay really did use the captured request and the captured contradictory outcome.
        Assert.Equal(reference.OriginalRequest, flow.OriginalRequest);
        Assert.Equal(reference.OutcomeContractJson, flow.OutcomeContractJson);
        Assert.Contains(
            "not yet ready for unconditional customer sign-off",
            flow.OutcomeContractJson,
            StringComparison.Ordinal);

        await using var database = await harness.Factory.CreateDbContextAsync();
        var readiness = Assert.Single(
            await database.DeliveryReadinessSnapshots
                .Where(item => item.FlowRunId == harness.FlowId && item.Active)
                .ToListAsync());

        // 1. Host-derived readiness is not releasable.
        Assert.Contains(
            readiness.State,
            new[]
            {
                DeliveryReadinessState.NeedsRefinement,
                DeliveryReadinessState.Blocked
            });
        Assert.Equal(DeliveryReadinessState.NeedsRefinement, readiness.State);
        var contract = DeliveryReadinessPolicy.DeserializeSnapshot(readiness.ContractJson);
        Assert.Equal(reference.SuccessCriteria.Count, contract.Criteria.Count);
        Assert.Equal(
            new[]
            {
                $"AC-{ReferenceFlowContracts.FailedCriterionIndex("error states") + 1:000}",
                $"AC-{ReferenceFlowContracts.FailedCriterionIndex("offline preview") + 1:000}"
            }.Order(StringComparer.Ordinal),
            contract.Criteria
                .Where(item => item.Outcome == DeliveryCriterionOutcome.Failed)
                .Select(item => item.CriterionId)
                .Order(StringComparer.Ordinal));

        // 2. No ordinary customer review gate exists, and no waiver gate either: consent cannot
        //    rescue a failed acceptance criterion.
        Assert.DoesNotContain(
            flow.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);
        Assert.DoesNotContain(
            flow.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerWaiver);

        // 3. Acceptance conflicts for every client, including one replaying the recorded gate id.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => harness.Reviews.ReviewAsync(
                harness.FlowId,
                new DirectReviewRequest
                {
                    GateId = reference.Review.GateId!.Value,
                    Intent = ReviewIntent.Accept
                }));
        var candidate = await database.ReviewedCandidateRecords
            .SingleAsync(item => item.FlowRunId == harness.FlowId && item.Active);
        var waiverConflict = await Assert.ThrowsAsync<DeliveryReadinessConflictException>(
            () => harness.Reviews.GrantReadinessWaiverAsync(
                harness.FlowId,
                new ReadinessWaiverRequest
                {
                    GateId = reference.Review.GateId!.Value,
                    ReviewedCandidateId = candidate.Id,
                    ReadinessRevision = readiness.Revision,
                    ReadinessContractHash = readiness.ContractHash,
                    RiskIds = ["RR-001"],
                    Acknowledgement = "I accept the disclosed risk."
                }));
        Assert.Equal(
            DeliveryReadinessConflicts.WaiverNotApplicable,
            waiverConflict.Code);

        // 4. Publisher call count is zero and nothing was journaled.
        Assert.Equal(0, harness.CandidatePublisher.Calls);
        Assert.Equal(0, harness.PublicationVerifier.Calls);
        Assert.DoesNotContain(
            flow.Steps,
            step => step.PlanStage == PlanStage.AfterApproval);
        Assert.Empty(await database.ReviewedPublicationRecords
            .Where(item => item.FlowRunId == harness.FlowId)
            .ToListAsync());

        // 5. Never Approved and never Published, unlike the captured baseline.
        Assert.Equal("Approved", reference.Status);
        Assert.Equal("Published", reference.PublicationStatus);
        Assert.NotEqual(FlowStatus.Approved, flow.Status);
        Assert.Equal(FlowStatus.WaitingForFeedback, flow.Status);
        Assert.Null(flow.CompletedAt);
        Assert.Equal("delivery.readiness-needs-refinement", flow.CurrentBlockerCode);
        Assert.DoesNotContain(flow.Events, item => item.Type == "flow.approved");
        Assert.DoesNotContain(
            flow.Events,
            item => item.Type == "delivery.reviewed-publication.completed");
        Assert.Contains(flow.Events, item => item.Type == "flow.readiness-not-ready");
        Assert.NotEqual(
            ReviewPublicationStatus.Published,
            ReviewCoordinator.GetPublicationStatus(flow));

        // 6. The customer projection is honest instead of green.
        var binding = await new DeliveryReadinessService().LoadCurrentAsync(
            database,
            harness.FlowId);
        var dto = ApiMappings.ToDeliveryReadinessDto(binding!, flow);
        Assert.Equal(DeliveryReadinessState.NeedsRefinement, dto.State);
        Assert.Equal("Needs refinement", dto.Label);
        Assert.Equal("Publication is not authorized", dto.PublicationAssurance);
        Assert.Equal([DeliveryReadinessAction.RequestRefinement], dto.AllowedActions);
        Assert.Null(dto.ReviewGateId);
        Assert.Null(dto.WaiverGateId);
    }

    [Fact]
    public async Task IncidentReplay_SameRequestReachesApprovalOnlyWhenEveryCriterionIsVerified()
    {
        // The control case: identical captured request, brief, plan, and contradictory outcome
        // document, but honest all-Verified typed results. Acceptance and publication proceed,
        // which proves the new gate is semantic rather than a blanket refusal.
        await using var harness = await IncidentHarness.CreateAsync();
        harness.Runner.QaBlock = context => ReferenceFlowContracts.Block(ReferenceFlowContracts.ResolvedQaJson(context.OutcomeContext));

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var beforeReview = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.WaitingForFeedback, beforeReview.Status);
        var review = Assert.Single(
            beforeReview.GateRecords,
            gate => gate.ActionType == HandoffActionType.CustomerReview);

        Guid candidateId;
        int revision;
        string hash;
        await using (var database = await harness.Factory.CreateDbContextAsync())
        {
            var readiness = await database.DeliveryReadinessSnapshots
                .SingleAsync(item => item.FlowRunId == harness.FlowId && item.Active);
            Assert.Equal(DeliveryReadinessState.ReadyToApprove, readiness.State);
            revision = readiness.Revision;
            hash = readiness.ContractHash;
            candidateId = (await database.ReviewedCandidateRecords
                .SingleAsync(item => item.FlowRunId == harness.FlowId && item.Active)).Id;
        }

        var accepted = await harness.Reviews.ReviewAsync(
            harness.FlowId,
            new DirectReviewRequest
            {
                GateId = review.Id,
                Intent = ReviewIntent.Accept,
                ReviewedCandidateId = candidateId,
                ReadinessRevision = revision,
                ReadinessContractHash = hash
            });
        Assert.Equal(ReviewDecision.Accepted, accepted.Review.Decision);

        await harness.Engine.RunAsync(harness.FlowId, CancellationToken.None);

        var published = await harness.LoadFlowAsync();
        Assert.Equal(FlowStatus.Approved, published.Status);
        Assert.Equal(1, harness.CandidatePublisher.Calls);
        Assert.Contains(published.Events, item => item.Type == "flow.approved");
    }

    [Fact]
    public void IncidentReplay_AuthorizationIsIndependentOfProseAndOfAgentVerdict()
    {
        var plan = ReferenceFlowContracts.AcceptancePlan();
        var planHash = ReferenceFlowContracts.AcceptancePlanHash();
        var context = VerificationContext();

        // Without the typed contract the verification turn is not accepted at all, no matter how
        // the recorded outcome text is worded.
        var missingContract = WorkflowEngine.GetStudioContractCorrectionReason(
            context,
            "HANDOFF_STATUS: COMPLETE\n" +
            ReferenceFlowArtifact.Current.Outcome.Summary);
        Assert.Contains(
            DeliveryReadinessPolicy.QaBeginMarker,
            missingContract,
            StringComparison.Ordinal);

        // With the typed contract present, the derived readiness and its immutable binding hash are
        // identical for the recorded hedged wording and for confident release wording.
        var hedged = Derive(ReferenceFlowContracts.ContradictoryQaJson());
        Assert.Null(WorkflowEngine.GetStudioContractCorrectionReason(
            context,
            "HANDOFF_STATUS: COMPLETE\nEverything shipped perfectly and is fully release-ready.\n" +
            ReferenceFlowContracts.Block(
                ReferenceFlowContracts.ContradictoryQaJson())));
        var confident = Derive(ReferenceFlowContracts.ContradictoryQaJson());
        Assert.Equal(DeliveryReadinessState.NeedsRefinement, hedged.State);
        Assert.Equal(
            DeliveryReadinessPolicy.HashSnapshot(hedged),
            DeliveryReadinessPolicy.HashSnapshot(confident));
        Assert.DoesNotContain(
            DeliveryReadinessAction.Accept,
            DeliveryReadinessPolicy.AllowedActions(hedged.State));

        // An agent-supplied PASS verdict contradicting its own criterion results is a contract
        // error, so a confident verdict cannot be laundered into readiness either.
        var forged = ReferenceFlowContracts.ContradictoryQaJson().Replace(
            "\"Verdict\":\"FAIL\"",
            "\"Verdict\":\"PASS\"",
            StringComparison.Ordinal);
        var errors = Assert.Throws<DeliveryReadinessContractException>(
            () => DeliveryReadinessPolicy.ParseQaJson(forged, plan, planHash));
        Assert.Contains(
            errors.Errors,
            error => error.Contains(
                "does not equal the host-derived verdict",
                StringComparison.Ordinal));
    }

    private static DeliveryReadinessSnapshot Derive(string qaJson)
    {
        var parsed = DeliveryReadinessPolicy.ParseQaJson(
            qaJson,
            ReferenceFlowContracts.AcceptancePlan(),
            ReferenceFlowContracts.AcceptancePlanHash());
        return DeliveryReadinessPolicy.Derive(
            new DeliveryReadinessDerivationInput(
                ReferenceFlowArtifact.Current.Id,
                ReferenceFlowArtifact.Current.Iteration,
                1,
                ReferenceFlowContracts.AcceptancePlan(),
                ReferenceFlowContracts.AcceptancePlanHash(),
                parsed.Document,
                parsed.ContractHash,
                Guid.Parse("11111111-1111-4111-8111-111111111111"),
                "quality-engineer",
                Guid.Parse("22222222-2222-4222-8222-222222222222"),
                OutcomeVerificationRules.ComputeSha256("outcome"),
                OutcomeVerificationRules.ComputeSha256("candidate"),
                [],
                [],
                [],
                [],
                DateTimeOffset.UnixEpoch,
                Guid.Empty));
    }

    private static AgentExecutionContext VerificationContext() =>
        new(
            Guid.NewGuid(),
            1,
            "quality-engineer",
            "Quality Engineer",
            "quality-engineer",
            "model",
            "high",
            1,
            "Verify the redesign.",
            "Repository knowledge.",
            "E:\\source",
            "E:\\workspace",
            Guid.NewGuid(),
            OutcomeType.Commit,
            "Plan summary.",
            [],
            [],
            InvocationKind: ExecutionInvocationKind.Worker,
            RequiresDeliveryReadinessQa: true);

    /// <summary>
    /// A current dynamic Delivery harness shaped like the captured incident: analyst, product designer,
    /// implementing engineer, verifying outcome owner, and one AfterApproval publisher, driving a
    /// Commit outcome from the captured customer request and confirmed brief.
    /// </summary>
    private sealed class IncidentHarness : IAsyncDisposable
    {
        private IncidentHarness(
            string root,
            Guid flowId,
            ReviewWorkflowTests.ReviewDbContextFactory factory,
            WorkflowDefinitionProvider workflowProvider,
            HandoffGateEngine gate,
            IncidentAgentRunner runner,
            CountingPublicationVerifier publicationVerifier,
            CountingCandidatePublisher candidatePublisher,
            WorkflowEngine engine,
            ReviewCoordinator reviews)
        {
            Root = root;
            FlowId = flowId;
            Factory = factory;
            WorkflowProvider = workflowProvider;
            Gate = gate;
            Runner = runner;
            PublicationVerifier = publicationVerifier;
            CandidatePublisher = candidatePublisher;
            Engine = engine;
            Reviews = reviews;
        }

        public string Root { get; }

        public Guid FlowId { get; }

        public ReviewWorkflowTests.ReviewDbContextFactory Factory { get; }

        public WorkflowDefinitionProvider WorkflowProvider { get; }

        public HandoffGateEngine Gate { get; }

        public IncidentAgentRunner Runner { get; }

        public CountingPublicationVerifier PublicationVerifier { get; }

        public CountingCandidatePublisher CandidatePublisher { get; }

        public WorkflowEngine Engine { get; }

        public ReviewCoordinator Reviews { get; }

        public static async Task<IncidentHarness> CreateAsync()
        {
            var reference = ReferenceFlowArtifact.Current;
            var root = Path.Combine(
                AppContext.BaseDirectory,
                "incident-replay-tests",
                Guid.NewGuid().ToString("N"));
            var agentsDirectory = Path.Combine(root, ".github", "agents");
            var workspacePath = Path.Combine(root, "workspace");
            Directory.CreateDirectory(agentsDirectory);
            Directory.CreateDirectory(workspacePath);
            Directory.CreateDirectory(Path.Combine(workspacePath, ".git"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "WORKFLOW.md"),
                """
                ---
                workspace:
                  root: workspace
                agent:
                  max_concurrent_agents: 1
                  max_attempts: 1
                studio:
                  planning:
                    max_steps: 24
                    max_dependencies_per_step: 8
                    max_assignment_characters: 4000
                  flow_kinds:
                    advisory:
                      required_duties:
                        - PrepareOutcome
                      maximum_permission: ReadOnlySource
                    delivery:
                      required_duties:
                        - Implement
                        - Verify
                        - PrepareOutcome
                        - Publish
                      pre_review_maximum_permission: WorkspaceWrite
                      post_approval_maximum_permission: Publish
                  advisory:
                    artifact_directory: .studio\advisory
                    max_artifact_count: 8
                    max_total_artifact_bytes: 65536
                  permissions:
                    read_only_source:
                      additional_denied_tools: []
                    workspace_write:
                      additional_denied_tools: []
                    publish:
                      additional_denied_tools: []
                    pre_mortem_read_only:
                      additional_denied_tools: []
                ---

                Test {{ agent.name }} on {{ task }}.
                """);

            var flow = new FlowRun
            {
                Title = "Redesign devclub.eu and devclub.ee with Nordic Tech Minimal",
                OriginalRequest = reference.OriginalRequest,
                ConsolidatedRequest = reference.ConsolidatedRequest,
                Kind = FlowKind.Delivery,
                Status = FlowStatus.Queued,
                RepositoryPath = root,
                RepositoryKnowledge = "The shared Angular site for both devclub brands.",
                Outcome = OutcomeType.Commit
            };
            var snapshots = new[]
            {
                Snapshot(flow.Id, "account-manager", "Account Manager", "account-manager"),
                Snapshot(flow.Id, "team-lead", "Team Lead", "team-lead"),
                Snapshot(flow.Id, "analyst", "Analyst", "analyst"),
                Snapshot(flow.Id, "product-designer", "Product Designer", "product-designer"),
                Snapshot(flow.Id, "software-engineer", "Software Engineer", "software-engineer"),
                Snapshot(flow.Id, "quality-engineer", "Quality Engineer", "quality-engineer"),
                Snapshot(flow.Id, "release-engineer", "Release Engineer", "release-engineer")
            };

            var databasePath = Path.Combine(root, "harness.db");
            var factory = new ReviewWorkflowTests.ReviewDbContextFactory(
                new DbContextOptionsBuilder<HarnessDbContext>()
                    .UseSqlite($"Data Source={databasePath};Pooling=False")
                    .Options);
            await using (var database = await factory.CreateDbContextAsync())
            {
                await database.Database.EnsureCreatedAsync();
                database.Settings.Add(new HarnessSettings
                {
                    RepositoryPath = root,
                    RepositoryKnowledge = "The shared Angular site for both devclub brands.",
                    Outcome = OutcomeType.Commit,
                    MaxHandoffRetries = 0
                });
                database.Flows.Add(flow);
                database.FlowAgentSnapshots.AddRange(snapshots);
                await database.SaveChangesAsync();
            }

            var paths = new HarnessPaths(root, agentsDirectory, databasePath);
            var workflowProvider = new WorkflowDefinitionProvider(
                paths,
                new AiHarnessDemo.Core.Workflow.WorkflowLoader(),
                NullLogger<WorkflowDefinitionProvider>.Instance);
            _ = workflowProvider.GetEffective();
            var catalog = new AgentCatalog(paths, factory);
            var gate = new HandoffGateEngine();
            gate.SetTrustLevel(HandoffActionType.Advance, HandoffTrustLevel.Auto);
            gate.SetTrustLevel(HandoffActionType.RequestRevision, HandoffTrustLevel.Auto);
            gate.SetTrustLevel(HandoffActionType.CustomerReview, HandoffTrustLevel.Gated);
            gate.SetTrustLevel(HandoffActionType.CustomerWaiver, HandoffTrustLevel.Gated);
            var runner = new IncidentAgentRunner(IncidentPlanJson());
            var verifier = new CountingPublicationVerifier();
            var candidatePublisher = new CountingCandidatePublisher();
            var lifecycle = new FlowLifecycleCoordinator();
            var flowQueue = new FlowQueue();
            var engine = new WorkflowEngine(
                factory,
                catalog,
                new FixedModelRouter(),
                new BootstrapTaskProfileFactory(),
                TestRoutingSupport.Recorder(factory),
                new IncidentWorkspaceManager(workspacePath),
                runner,
                gate,
                new CopilotSessionJournal(),
                workflowProvider,
                NullLogger<WorkflowEngine>.Instance,
                publicationVerifier: verifier,
                candidatePublisher: candidatePublisher,
                flowAgentSnapshotService: new FlowAgentSnapshotService(factory, catalog),
                teamPlanValidator: new TeamPlanValidator(),
                lifecycleCoordinator: lifecycle,
                reviewedCandidateService: new IncidentReviewedCandidateService());
            var reviews = new ReviewCoordinator(
                factory,
                gate,
                flowQueue,
                lifecycle,
                workflowProvider);
            return new IncidentHarness(
                root,
                flow.Id,
                factory,
                workflowProvider,
                gate,
                runner,
                verifier,
                candidatePublisher,
                engine,
                reviews);
        }

        public async Task<FlowRun> LoadFlowAsync()
        {
            await using var database = await Factory.CreateDbContextAsync();
            return await database.Flows
                .AsSplitQuery()
                .AsNoTracking()
                .Include(item => item.Steps)
                .Include(item => item.GateRecords)
                .Include(item => item.Events)
                .Include(item => item.Messages)
                .Include(item => item.AgentSnapshots)
                .Include(item => item.PlanDocuments)
                .Include(item => item.TaskProfiles)
                .SingleAsync(item => item.Id == FlowId);
        }

        public ValueTask DisposeAsync()
        {
            WorkflowProvider.Dispose();
            Gate.Dispose();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // SQLite can briefly retain a handle on Windows.
            }
            return ValueTask.CompletedTask;
        }

        /// <summary>The recorded plan shape, now carrying the mandatory acceptance criteria.</summary>
        private static string IncidentPlanJson()
        {
            var plan = new TeamPlanDocument
            {
                Disposition = TeamPlanDisposition.Planned,
                Steps =
                [
                    Step("inspect-current-implementation", "analyst", 20, PlanStage.BeforeReview,
                        [PlanDuty.Analyze], outcomeOwner: false),
                    Step("design-nordic-tech-minimal-system", "product-designer", 30,
                        PlanStage.BeforeReview, [PlanDuty.Design], outcomeOwner: false,
                        dependsOn: ["inspect-current-implementation"]),
                    Step("implement-redesign", "software-engineer", 40, PlanStage.BeforeReview,
                        [PlanDuty.Implement], outcomeOwner: false,
                        dependsOn: ["design-nordic-tech-minimal-system"]),
                    Step("verify-and-prepare-outcome", "quality-engineer", 50,
                        PlanStage.BeforeReview,
                        [PlanDuty.Verify, PlanDuty.PrepareOutcome], outcomeOwner: true,
                        dependsOn: ["implement-redesign"]),
                    Step("publish-redesign", "release-engineer", 60, PlanStage.AfterApproval,
                        [PlanDuty.Publish], outcomeOwner: false,
                        dependsOn: ["verify-and-prepare-outcome"])
                ],
                PreMortemCheckpoints = [],
                AcceptanceCriteria = ReferenceFlowContracts.AcceptancePlan().Criteria,
                MissingQualification = null
            };
            return
                $"{TeamPlanParser.BeginSentinel}{Environment.NewLine}" +
                TeamPlanParser.Serialize(plan) +
                $"{Environment.NewLine}{TeamPlanParser.EndSentinel}";
        }

        private static TeamPlanStep Step(
            string id,
            string agentId,
            int order,
            PlanStage stage,
            IReadOnlyList<PlanDuty> duties,
            bool outcomeOwner,
            IReadOnlyList<string>? dependsOn = null) =>
            new()
            {
                Id = id,
                AgentId = agentId,
                Order = order,
                Stage = stage,
                Assignment = $"Complete {id} for the Nordic Tech Minimal redesign.",
                Justification = $"{agentId} owns {id}.",
                DependsOn = dependsOn ?? [],
                Duties = duties,
                OutcomeOwner = outcomeOwner,
                TaskProfile = stage == PlanStage.AfterApproval
                    ? new TeamPlanTaskProfile()
                    : new TeamPlanTaskProfile
                    {
                        Complexity = 6,
                        ReasoningDepth = 6,
                        ContextDemand = 6,
                        ToolIntensity = 4,
                        TaskTypeTags = [TaskTypeTag.CrossCutting],
                        Risk = TaskRisk.Medium,
                        RiskReason = "A cross-brand visual redesign must not regress behavior.",
                        Confidence = 0.8,
                        Rationales = ["The redesign spans every route on both brands."]
                    }
            };

        private static FlowAgentSnapshot Snapshot(
            Guid flowId,
            string id,
            string name,
            string role) =>
            new()
            {
                FlowRunId = flowId,
                AgentId = id,
                Name = name,
                Description = $"Description for {name}.",
                Role = role,
                Instructions = "Complete the assigned work.",
                DefinitionHash = $"hash-{id}",
                EnabledAtSnapshot = true,
                Required = id is "account-manager" or "team-lead",
                Switchable = id is not ("account-manager" or "team-lead"),
                SourceFileName = $"{id}.agent.md"
            };
    }

    /// <summary>
    /// Scripted agents. The outcome owner returns the captured incident's exact
    /// <c>flow outcome</c> document, so the replay carries the original contradiction verbatim.
    /// </summary>
    private sealed class IncidentAgentRunner(string plan) : IAgentRunner
    {
        public Func<AgentExecutionContext, string>? QaBlock { get; set; }

        public Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            var reference = ReferenceFlowArtifact.Current;
            var output = context.AgentId switch
            {
                "team-lead" => $"HANDOFF_STATUS: COMPLETE{Environment.NewLine}{plan}",
                "account-manager" =>
                    "HANDOFF_STATUS: COMPLETE" + Environment.NewLine +
                    IntakeParser.BeginSentinel + Environment.NewLine +
                    """{"Status":"Confirmed","FlowKind":"Delivery","TaskTitle":"Redesign devclub.eu and devclub.ee","CustomerReply":"I captured the Nordic Tech Minimal redesign for both brands.","Brief":""" +
                    reference.ConsolidatedRequest + "}" + Environment.NewLine +
                    IntakeParser.EndSentinel,
                _ => """
                  HANDOFF_STATUS: COMPLETE

                  ## Decision
                  Complete.

                  ## Deliverable
                  The assigned redesign work is complete.

                  ## Evidence
                  The shared Angular site was inspected in the isolated workspace.

                  ## Next owner
                  Continue the accepted plan.
                  """
            };
            if (context.OutcomeContract.Contains(
                    FlowOutcomeParser.BeginSentinel,
                    StringComparison.Ordinal))
            {
                output +=
                    Environment.NewLine +
                    FlowOutcomeParser.BeginSentinel +
                    Environment.NewLine +
                    reference.OutcomeContractJson +
                    Environment.NewLine +
                    FlowOutcomeParser.EndSentinel;
            }
            if (context.RequiresDeliveryReadinessQa && QaBlock is not null)
            {
                output += Environment.NewLine + QaBlock(context);
            }
            if (WorkflowEngine.RequiresOwnerRepairStatus(context))
            {
                output += Environment.NewLine + "REPAIR_STATUS: NO_CHANGE_NEEDED";
            }
            if (context.InvocationKind == ExecutionInvocationKind.Publication)
            {
                output +=
                    Environment.NewLine +
                    RepositoryKnowledgeSynthesizer.RecapBeginSentinel +
                    Environment.NewLine +
                    """{"Changed":false,"Reason":"The fixture publication does not alter durable repository knowledge.","Knowledge":null}""" +
                    Environment.NewLine +
                    RepositoryKnowledgeSynthesizer.RecapEndSentinel;
            }
            return Task.FromResult(new AgentExecutionResult(
                output,
                "Fixture evidence.",
                1,
                context.InvocationKind == ExecutionInvocationKind.Worker
                    ? [
                        new ToolCallRecord(
                            "observe",
                            "Inspect the redesigned sites.",
                            Succeeded: true,
                            ToolType: "Observation",
                            ResultDigest:
                                OutcomeVerificationRules.ComputeSha256(
                                    context.PlanStepKey),
                            ResultSummary:
                                "The customer-visible behavior was observed.")
                    ]
                    : []));
        }
    }

    private sealed class CountingPublicationVerifier : IPublishedOutcomeVerifier
    {
        public int Calls { get; private set; }

        public Task<PublishedOutcome> VerifyAsync(
            FlowRun flow,
            string releaseOutput,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new PublishedOutcome(
                "#/preview/" + flow.Id.ToString("D"),
                ReferenceFlowArtifact.Current.OutcomeLabel));
        }
    }

    private sealed class CountingCandidatePublisher : IVerifiedCandidatePublisher
    {
        public int Calls { get; private set; }

        public Task<string> PublishAsync(
            FlowRun flow,
            Guid publicationStepId,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(
                "Reviewed candidate: sha256:" + new string('3', 64));
        }
    }

    private sealed class IncidentReviewedCandidateService : IReviewedCandidateService
    {
        public Task<ReviewedCandidateIdentity> SealAsync(
            FlowRun flow,
            Guid outcomeOwnerStepId,
            string outcomeOwnerPlanStepKey,
            string outcomeContractJson,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Identity(
                flow,
                outcomeOwnerStepId,
                outcomeOwnerPlanStepKey,
                outcomeContractJson));

        public Task<OutcomeCandidateSnapshot> VerifyAsync(
            FlowRun flow,
            ReviewedCandidateIdentity identity,
            CancellationToken cancellationToken = default)
        {
            ReviewedCandidateLedger.ValidateForFlow(flow, identity);
            return Task.FromResult(new OutcomeCandidateSnapshot(
                new CandidateManifest(
                    flow.Iteration,
                    identity.AcceptancePlanHash,
                    [.. identity.Repositories.Select(repository =>
                        new CandidateRepositoryManifest(
                            repository.RelativePath,
                            repository.Head,
                            repository.Tree,
                            repository.RemoteRepository))],
                    [],
                    []),
                identity.Fingerprint,
                identity.OutcomeOwnerStepId,
                identity.SealedAt));
        }

        private static ReviewedCandidateIdentity Identity(
            FlowRun flow,
            Guid outcomeOwnerStepId,
            string outcomeOwnerPlanStepKey,
            string outcomeContractJson) =>
            new(
                flow.Id,
                flow.Iteration,
                outcomeOwnerStepId,
                outcomeOwnerPlanStepKey,
                OutcomeVerificationRules.ComputeSha256(outcomeContractJson),
                OutcomeVerificationRules.ComputeSha256(
                    $"incident-plan:{flow.Id:D}:{flow.Iteration}"),
                OutcomeVerificationRules.ComputeSha256(
                    $"incident-candidate:{flow.Id:D}:{flow.Iteration}"),
                0,
                0,
                0,
                0,
                [
                    new ReviewedCandidateRepositoryIdentity(
                        "site",
                        new string('5', 40),
                        new string('6', 40),
                        "devclub/site")
                ],
                DateTimeOffset.UtcNow);
    }

    private sealed class IncidentWorkspaceManager(string path) : IWorkspaceManager
    {
        public Task<WorkspaceInfo> PrepareAsync(
            FlowRun flow,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkspaceInfo(path, "incident-replay", CreatedNow: false));
    }
}

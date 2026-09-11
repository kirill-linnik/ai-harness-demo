using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Security;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Data;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

public sealed record ReviewCoordinationResult(
    DirectReviewResponse Review,
    FlowRun Flow);

public sealed record ReviewFeedbackCoordinationResult(
    FlowRun Flow,
    string Reply,
    bool ShouldSpeak,
    ReviewIntent Intent,
    DirectReviewResponse? Review);

public sealed class PublicationPolicyConflictException(
    Guid flowId,
    Guid publicationStepId,
    ExecutionPermissionProfile effectiveProfile)
    : InvalidOperationException(
        "Remote publication is disabled by the current workflow post-approval permission ceiling. No host publication is authorized.")
{
    public Guid FlowId { get; } = flowId;

    public Guid PublicationStepId { get; } = publicationStepId;

    public ExecutionPermissionProfile EffectiveProfile { get; } =
        effectiveProfile;
}

public sealed class ReviewCoordinator(
    IDbContextFactory<HarnessDbContext> databaseFactory,
    HandoffGateEngine gateEngine,
    FlowQueue flowQueue,
    FlowLifecycleCoordinator lifecycle,
    WorkflowDefinitionProvider workflowProvider,
    INewWorkAdmissionService? admissionService = null,
    LinkedFlowCoordinator? linkedFlows = null,
    PermissionProfileResolver? permissionProfileResolver = null,
    IModelRouter? modelRouter = null,
    BootstrapTaskProfileFactory? profileFactory = null,
    RoutingObservationRecorder? observationRecorder = null,
    IAgentRunner? agentRunner = null,
    IWorkspaceManager? workspaceManager = null,
    CopilotSessionJournal? sessionJournal = null,
    IReviewedCandidateService? reviewedCandidateService = null,
    AgentManifestStager? manifestStager = null,
    DeliveryReadinessService? deliveryReadinessService = null,
    ILogger<ReviewCoordinator>? logger = null,
    IDemoRuntimeRevoker? demoRuntimeRevoker = null)
{
    private const int MaximumTotalRequestedChangeCharacters = 16_000;
    internal const string FeedbackClassificationPlanStepPrefix =
        "account-manager:review-classification:";
    internal const string FeedbackRequestEventType =
        "review.feedback-classification-requested";
    private const string FeedbackCompletedEventType =
        "review.feedback-classified";
    private const string FeedbackAppliedEventType =
        "review.feedback-classification-applied";
    private const string FeedbackSupersededEventType =
        "review.feedback-classification-superseded";
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim>
        _feedbackClassificationLocks = new();
    private readonly PermissionProfileResolver _permissionResolver =
        permissionProfileResolver ?? new PermissionProfileResolver();
    private readonly CopilotSessionJournal _sessionJournal =
        sessionJournal ?? new CopilotSessionJournal();
    private readonly AgentManifestStager _manifestStager =
        manifestStager ?? new AgentManifestStager();
    private readonly DeliveryReadinessService _readiness =
        deliveryReadinessService ?? new DeliveryReadinessService();
    private readonly ILogger<ReviewCoordinator>? _logger =
        logger;
    internal Func<string, Guid, CancellationToken, Task<CopilotSessionSnapshot>>?
        SessionInspectorOverride { get; set; }
    internal Func<CopilotSessionSnapshot, bool>?
        ActiveSessionStopperOverride { get; set; }

    public async Task<ReviewCoordinationResult> ReviewAsync(
        Guid flowId,
        DirectReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.GateId == Guid.Empty)
        {
            throw new ArgumentException("A customer-review gate ID is required.");
        }
        var intent = request.Intent
            ?? throw new ArgumentException("A review intent is required.");
        if (!Enum.IsDefined(intent))
        {
            throw new ArgumentException("The review intent is not supported.");
        }
        if (intent == ReviewIntent.Ambiguous)
        {
            throw new InvalidOperationException(
                "The review remains unresolved until the customer chooses Accept, RequestRefinement, or PromoteToDelivery.");
        }
        if (intent == ReviewIntent.PromoteToDelivery)
        {
            if (request.Refinement is not null)
            {
                throw new ArgumentException(
                    "Advisory promotion cannot include a refinement payload.");
            }
            return await PromoteToDeliveryAsync(
                flowId,
                request.GateId,
                cancellationToken);
        }

        var refinement = intent == ReviewIntent.RequestRefinement
            ? NormalizeRefinement(request.Refinement)
            : null;
        if (intent == ReviewIntent.Accept && request.Refinement is not null)
        {
            throw new ArgumentException(
                "Acceptance cannot include a refinement payload.");
        }

        await using var lifecycleLease =
            await lifecycle.EnterAsync(flowId, cancellationToken);
        HandoffGateRecord? resolvedForHistory = null;
        var queueFlow = false;
        var revokeDemos = false;
        Guid? publicationStepId = null;

        await using (var database =
                     await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction =
                await database.Database.BeginTransactionAsync(cancellationToken);
            var flow = await database.Flows
                           .AsSplitQuery()
                           .Include(item => item.Steps)
                           .Include(item => item.GateRecords)
                           .Include(item => item.Events)
                           .Include(item => item.AgentSnapshots)
                           .Include(item => item.PlanDocuments)
                           .Include(item => item.TaskProfiles)
                           .SingleOrDefaultAsync(
                               item => item.Id == flowId,
                               cancellationToken)
                       ?? throw new KeyNotFoundException(
                           $"Factory flow '{flowId}' was not found.");
            EnsureStudioReviewFlow(flow);
            var gate = flow.GateRecords.SingleOrDefault(
                           item => item.Id == request.GateId)
                       ?? throw new InvalidOperationException(
                           "The customer-review gate is stale or does not belong to this flow.");
            if (gate.ActionType != HandoffActionType.CustomerReview)
            {
                throw new InvalidOperationException(
                    "The selected gate is not a generic customer review.");
            }

            var reviewedStep = flow.Steps.SingleOrDefault(
                                   step => step.Id == gate.FlowStepId)
                               ?? throw new InvalidOperationException(
                                   "The customer-review gate has no durable source step.");
            if (gate.Resolved)
            {
                EnsureIdempotentDecision(
                    flow,
                    gate,
                    intent,
                    refinement);
                publicationStepId = FindPublicationRoot(flow)?.Id;
                await transaction.CommitAsync(cancellationToken);
                if ((flow.Status is
                         FlowStatus.Queued or
                         FlowStatus.Reworking) &&
                    !flowQueue.Queue(flowId))
                {
                    throw new InvalidOperationException(
                        "The durable review decision was already recorded, but its queued follow-up could not be scheduled.");
                }
                return await BuildResultAsync(
                    flowId,
                    gate.Id,
                    intent,
                    gate.ReviewDecision,
                    publicationStepId,
                    linkedFlowId: null,
                    message: "This review decision was already recorded.",
                    cancellationToken: cancellationToken);
            }
            if (!reviewedStep.IsOutcomeOwner ||
                reviewedStep.Status != StepStatus.Completed ||
                string.IsNullOrWhiteSpace(flow.OutcomeOwnerPlanStepKey) ||
                !string.Equals(
                    reviewedStep.PlanStepKey,
                    flow.OutcomeOwnerPlanStepKey,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The customer-review gate is not attached to the completed planned outcome owner.");
            }
            if (flow.Status != FlowStatus.WaitingForFeedback ||
                reviewedStep.Iteration != flow.Iteration)
            {
                throw new InvalidOperationException(
                    "This flow is not waiting for the selected customer review.");
            }

            DeliveryReadinessBinding? readiness = null;
            if (DeliveryReadinessService.AppliesTo(flow))
            {
                // A Delivery review request must carry the complete immutable binding. An omitted
                // value is treated as a stale tab rather than as permission to skip the check.
                if (request.ReviewedCandidateId is null ||
                    request.ReadinessRevision is null ||
                    string.IsNullOrWhiteSpace(request.ReadinessContractHash))
                {
                    var current = await _readiness.LoadCurrentAsync(
                        database,
                        flow.Id,
                        cancellationToken);
                    throw new DeliveryReadinessConflictException(
                        DeliveryReadinessConflicts.ReviewStale,
                        "A Delivery review requires the current reviewed-candidate id, readiness revision, and readiness contract hash.",
                        current?.State,
                        current?.Revision,
                        current?.ContractHash);
                }
                // Acceptance and refinement both re-read the authoritative readiness rows so a
                // stale tab, a replayed request, or a superseded assessment cannot resolve a gate.
                readiness = await _readiness.AuthorizeAsync(
                    database,
                    flow.Id,
                    request.ReviewedCandidateId,
                    request.ReadinessContractHash,
                    request.ReadinessRevision,
                    DeliveryReadinessState.ReadyToApprove,
                    DeliveryReadinessConflicts.NotReady,
                    cancellationToken);
            }

            var now = DateTimeOffset.UtcNow;
            if (intent == ReviewIntent.Accept)
            {
                resolvedForHistory = gateEngine.PrepareReviewResolution(
                    gate,
                    ReviewDecision.Accepted,
                    "customer",
                    "Customer accepted the reviewed outcome.",
                    now);
                ApplyPreparedGate(resolvedForHistory, gate);
                if (flow.Kind == FlowKind.Advisory)
                {
                    lifecycle.Transition(flow, FlowStatus.Approved);
                    flow.OutcomeLabel = "Advisory result accepted";
                    flow.CompletedAt = now;
                    database.FlowEvents.Add(new FlowEvent
                    {
                        FlowRunId = flow.Id,
                        FlowStepId = reviewedStep.Id,
                        Type = "flow.review-accepted",
                        Message =
                            "Customer accepted the Advisory result; no publication was scheduled."
                    });
                }
                else
                {
                    var workflow = workflowProvider.GetEffective();
                    var publication = MaterializePublicationStep(
                        flow,
                        reviewedStep,
                        workflow,
                        _permissionResolver);
                    publicationStepId = publication.Id;
                    if (readiness is not null)
                    {
                        lifecycle.QueueApprovedPublication(
                            flow,
                            readiness.State,
                            readiness.Record.CandidateFingerprint,
                            readiness.Candidate.CandidateFingerprint);
                    }
                    else
                    {
                        lifecycle.Transition(flow, FlowStatus.Queued);
                    }
                    flow.CompletedAt = null;
                    database.FlowEvents.Add(new FlowEvent
                    {
                        FlowRunId = flow.Id,
                        FlowStepId = publication.Id,
                        Type = "flow.approved-publication-queued",
                        Message =
                            "Customer accepted the Delivery result; the sole planned post-approval publication step was queued.",
                        DataJson = readiness is null
                            ? null
                            : JsonSerializer.Serialize(new
                            {
                                Version = "delivery-readiness-acceptance-v1",
                                SnapshotId = readiness.Record.Id,
                                readiness.Revision,
                                readiness.ContractHash,
                                ReviewedCandidateId = readiness.Candidate.Id,
                                readiness.Record.CandidateFingerprint,
                                readiness.WaiverSetHash,
                                GateId = gate.Id
                            })
                    });
                    queueFlow = true;
                }
            }
            else
            {
                resolvedForHistory = gateEngine.PrepareReviewResolution(
                    gate,
                    ReviewDecision.RefinementRequested,
                    "customer",
                    "Customer requested a refined iteration.",
                    now);
                ApplyPreparedGate(resolvedForHistory, gate);
                if (readiness is not null)
                {
                    // A refined iteration invalidates the reviewed result, so the releasable
                    // assessment and its candidate binding are superseded in the same transaction.
                    // Leaving them active would keep painting a green card, history row, and
                    // readiness panel for work that is being redone.
                    await _readiness.SupersedeCurrentAsync(
                        database,
                        flow.Id,
                        reviewedStep.Id,
                        "Superseded because the customer requested a refined iteration from the ordinary customer review.",
                        cancellationToken);
                }
                ApplyRefinement(
                    flow,
                    reviewedStep,
                    refinement!,
                    now,
                    database,
                    lifecycle);
                queueFlow = true;
            }

            flow.UpdatedAt = now;
            revokeDemos = flow.Status is not (
                FlowStatus.WaitingForFeedback or FlowStatus.Approved);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        if (resolvedForHistory is not null)
        {
            gateEngine.RestoreHistory([resolvedForHistory]);
        }
        if (queueFlow && !flowQueue.Queue(flowId))
        {
            throw new InvalidOperationException(
                "The durable review decision was recorded, but the flow could not be queued.");
        }
        if (revokeDemos && demoRuntimeRevoker is not null)
        {
            await demoRuntimeRevoker.RevokeFlowAsync(
                flowId,
                "Live demos were revoked because the reviewed flow moved to a new lifecycle phase.",
                CancellationToken.None);
        }

        var message = intent == ReviewIntent.Accept
            ? publicationStepId is null
                ? "Customer acceptance was recorded; the Advisory flow is complete."
                : "Customer acceptance was recorded and the planned publication step was queued."
            : "Customer refinement was recorded and a new planning iteration was queued.";
        return await BuildResultAsync(
            flowId,
            request.GateId,
            intent,
            intent == ReviewIntent.Accept
                ? ReviewDecision.Accepted
                : ReviewDecision.RefinementRequested,
            publicationStepId,
            linkedFlowId: null,
            message: message,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Resolves the separate customer waiver gate. Granting a waiver is informed consent to named
    /// disclosed risks; it is never product acceptance and never opens publication by itself. On
    /// success the same QA facts are re-derived and the ordinary customer review is opened.
    /// </summary>
    public async Task<ReadinessWaiverResponse> GrantReadinessWaiverAsync(
        Guid flowId,
        ReadinessWaiverRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.GateId == Guid.Empty || request.ReviewedCandidateId == Guid.Empty)
        {
            throw new ArgumentException(
                "A waiver requires the current waiver gate and reviewed candidate identifiers.");
        }
        if (request.RiskIds is null || request.RiskIds.Count == 0)
        {
            throw new ArgumentException("A waiver must name at least one risk.");
        }
        if (string.IsNullOrWhiteSpace(request.Acknowledgement))
        {
            throw new ArgumentException("A waiver requires a non-empty acknowledgement.");
        }

        await using var lifecycleLease =
            await lifecycle.EnterAsync(flowId, cancellationToken);
        HandoffGateRecord? resolvedForHistory = null;
        HandoffGateRecord? reviewForHistory = null;
        DeliveryReadinessBinding refreshed;
        await using (var database =
                     await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction =
                await database.Database.BeginTransactionAsync(cancellationToken);
            var flow = await database.Flows
                           .AsSplitQuery()
                           .Include(item => item.Steps)
                           .Include(item => item.GateRecords)
                           .Include(item => item.Events)
                           .SingleOrDefaultAsync(
                               item => item.Id == flowId,
                               cancellationToken)
                       ?? throw new KeyNotFoundException(
                           $"Factory flow '{flowId}' was not found.");
            if (!DeliveryReadinessService.AppliesTo(flow))
            {
                throw new InvalidOperationException(
                    "Readiness waivers apply only to studio-v2 Delivery flows.");
            }
            var binding = await _readiness.AuthorizeAsync(
                database,
                flow.Id,
                request.ReviewedCandidateId,
                request.ReadinessContractHash,
                request.ReadinessRevision,
                DeliveryReadinessState.NeedsCustomerWaiver,
                DeliveryReadinessConflicts.WaiverNotApplicable,
                cancellationToken);
            var gate = flow.GateRecords.SingleOrDefault(item => item.Id == request.GateId)
                       ?? throw new DeliveryReadinessConflictException(
                           DeliveryReadinessConflicts.ReviewStale,
                           "The waiver gate is stale or does not belong to this flow.",
                           binding.State,
                           binding.Revision,
                           binding.ContractHash);
            if (gate.ActionType != HandoffActionType.CustomerWaiver)
            {
                throw new DeliveryReadinessConflictException(
                    DeliveryReadinessConflicts.WaiverNotApplicable,
                    "The selected gate is not the separate customer waiver gate.",
                    binding.State,
                    binding.Revision,
                    binding.ContractHash);
            }

            await _readiness.RecordWaiversAsync(
                database,
                binding,
                gate.Id,
                request.RiskIds,
                request.Acknowledgement!,
                "customer",
                cancellationToken);
            var now = DateTimeOffset.UtcNow;
            if (!gate.Resolved)
            {
                resolvedForHistory = gateEngine.PrepareResolution(
                    gate,
                    approved: true,
                    "customer",
                    "Customer acknowledged and waived the disclosed residual risks.",
                    now);
                ApplyPreparedGate(resolvedForHistory, gate);
            }

            var identity = ReviewedCandidateLedger.Read(flow);
            refreshed = await _readiness.DeriveAndPersistAsync(
                database,
                flow,
                identity,
                cancellationToken);
            if (refreshed.State == DeliveryReadinessState.ReadyToApprove)
            {
                var reviewedStep = flow.Steps.Single(
                    step => step.Id == refreshed.Record.OutcomeOwnerStepId);
                var existingReview = flow.GateRecords.SingleOrDefault(item =>
                    !item.Resolved &&
                    item.ActionType == HandoffActionType.CustomerReview);
                if (existingReview is null)
                {
                    var review = gateEngine.SubmitProposal(new HandoffProposal
                    {
                        FlowRunId = flow.Id,
                        FlowStepId = reviewedStep.Id,
                        ActionType = HandoffActionType.CustomerReview,
                        Summary =
                            "Every acceptance criterion is verified and all required waivers are granted.",
                        Evidence = string.Join(
                            Environment.NewLine,
                            refreshed.Contract.Criteria.Select(item =>
                                $"{item.CriterionId} {item.Outcome}: {item.Rationale}")),
                        BlastRadius = HandoffBlastRadius.High
                    });
                    flow.GateRecords.Add(review);
                    database.Entry(review).State = EntityState.Added;
                    reviewForHistory = review;
                    database.FlowEvents.Add(new FlowEvent
                    {
                        FlowRunId = flow.Id,
                        FlowStepId = reviewedStep.Id,
                        Type = "gate.customer-review-created",
                        Message =
                            $"Harness opened the ordinary customer review after waiver receipts at readiness revision {refreshed.Revision}.",
                        DataJson = JsonSerializer.Serialize(new
                        {
                            Version = "delivery-readiness-review-opened-v1",
                            SnapshotId = refreshed.Record.Id,
                            refreshed.Revision,
                            refreshed.ContractHash,
                            ReviewedCandidateId = refreshed.Candidate.Id,
                            refreshed.WaiverSetHash
                        })
                    });
                }
                lifecycle.OpenCustomerReview(
                    flow,
                    refreshed.State,
                    refreshed.Record.CandidateFingerprint,
                    refreshed.Candidate.CandidateFingerprint);
                flow.OutcomeLabel = "Customer review ready";
            }
            flow.UpdatedAt = now;
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        if (resolvedForHistory is not null)
        {
            gateEngine.RestoreHistory([resolvedForHistory]);
        }
        if (reviewForHistory is not null)
        {
            gateEngine.RestoreHistory([reviewForHistory]);
        }
        return new ReadinessWaiverResponse(
            flowId,
            request.GateId,
            request.ReviewedCandidateId,
            refreshed.Revision,
            refreshed.ContractHash,
            [.. request.RiskIds.OrderBy(id => id, StringComparer.Ordinal)],
            refreshed.State == DeliveryReadinessState.ReadyToApprove
                ? "The waived risks were recorded and the ordinary customer review is now open."
                : $"The waived risks were recorded; readiness is now '{refreshed.State}'.");
    }

    /// <summary>
    /// Resolves a Delivery flow whose host-derived readiness is not releasable. The action set is
    /// bounded by the derived state: <c>NeedsRefinement</c> allows only a refinement request, and
    /// <c>Blocked</c> allows only Continue, Replan, or Abandon. No path here can accept a result,
    /// grant a waiver, resolve a customer-review gate, or authorize publication.
    /// </summary>
    public async Task<ReadinessResolutionResponse> ResolveReadinessAsync(
        Guid flowId,
        ReadinessResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var action = request.Action
            ?? throw new ArgumentException("A readiness resolution action is required.");
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentException("The readiness resolution action is not supported.");
        }
        if (request.ReviewedCandidateId == Guid.Empty ||
            request.ReadinessRevision is null ||
            string.IsNullOrWhiteSpace(request.ReadinessContractHash))
        {
            throw new DeliveryReadinessConflictException(
                DeliveryReadinessConflicts.ReviewStale,
                "A readiness resolution requires the current reviewed-candidate id, readiness revision, and readiness contract hash.");
        }
        var refinement = action is ReadinessResolutionAction.RequestRefinement
            or ReadinessResolutionAction.Replan
            ? NormalizeRefinement(request.Refinement)
            : null;
        if (action is ReadinessResolutionAction.Continue or ReadinessResolutionAction.Abandon &&
            request.Refinement is not null)
        {
            throw new ArgumentException(
                "Continue and Abandon cannot include a refinement payload.");
        }

        await using var lifecycleLease =
            await lifecycle.EnterAsync(flowId, cancellationToken);
        DeliveryReadinessState resolvedFrom;
        FlowStatus status;
        int iteration;
        var queueFlow = false;
        await using (var database =
                     await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction =
                await database.Database.BeginTransactionAsync(cancellationToken);
            var flow = await database.Flows
                           .AsSplitQuery()
                           .Include(item => item.Steps)
                           .Include(item => item.GateRecords)
                           .Include(item => item.Events)
                           .Include(item => item.PlanDocuments)
                           .SingleOrDefaultAsync(
                               item => item.Id == flowId,
                               cancellationToken)
                       ?? throw new KeyNotFoundException(
                           $"Factory flow '{flowId}' was not found.");
            if (!DeliveryReadinessService.AppliesTo(flow))
            {
                throw new InvalidOperationException(
                    "Readiness resolution applies only to studio-v2 Delivery flows.");
            }
            var required = action switch
            {
                ReadinessResolutionAction.RequestRefinement =>
                    DeliveryReadinessState.NeedsRefinement,
                _ => DeliveryReadinessState.Blocked
            };
            var binding = action == ReadinessResolutionAction.Abandon
                ? await _readiness.LoadCurrentAsync(database, flowId, cancellationToken)
                  ?? throw new DeliveryReadinessConflictException(
                      DeliveryReadinessConflicts.ReconciliationRequired,
                      "This Delivery flow has no current host-derived readiness assessment.")
                : await _readiness.AuthorizeAsync(
                    database,
                    flowId,
                    request.ReviewedCandidateId,
                    request.ReadinessContractHash,
                    request.ReadinessRevision,
                    required,
                    DeliveryReadinessConflicts.NotReady,
                    cancellationToken);
            resolvedFrom = binding.State;
            if (action == ReadinessResolutionAction.Abandon)
            {
                // Abandonment is executed by the durable abandonment path; this endpoint only
                // proves the caller held the current binding.
                await transaction.CommitAsync(cancellationToken);
                return new ReadinessResolutionResponse(
                    flowId,
                    action,
                    resolvedFrom,
                    flow.Status,
                    flow.Iteration,
                    "Abandonment is authorized for the current readiness assessment.");
            }
            if (binding.State == DeliveryReadinessState.ReadyToApprove ||
                binding.State == DeliveryReadinessState.NeedsCustomerWaiver)
            {
                throw new DeliveryReadinessConflictException(
                    DeliveryReadinessConflicts.NotReady,
                    "A releasable or waiver-pending assessment is resolved through review, not through readiness resolution.",
                    binding.State,
                    binding.Revision,
                    binding.ContractHash);
            }
            if (flow.GateRecords.Any(gate =>
                    !gate.Resolved &&
                    gate.ActionType is HandoffActionType.CustomerReview
                        or HandoffActionType.CustomerWaiver))
            {
                throw new DeliveryReadinessConflictException(
                    DeliveryReadinessConflicts.ReviewStale,
                    "An unresolved customer gate exists; resolve it instead of using readiness resolution.",
                    binding.State,
                    binding.Revision,
                    binding.ContractHash);
            }

            var now = DateTimeOffset.UtcNow;
            var reviewedStep = flow.Steps.SingleOrDefault(
                                   step => step.Id == binding.Record.OutcomeOwnerStepId)
                               ?? throw new DeliveryReadinessConflictException(
                                   DeliveryReadinessConflicts.ReconciliationRequired,
                                   "The readiness assessment has no durable outcome-owner step.",
                                   binding.State,
                                   binding.Revision,
                                   binding.ContractHash);
            await _readiness.SupersedeCurrentAsync(
                database,
                flowId,
                reviewedStep.Id,
                $"Superseded because the customer resolved '{binding.State}' with '{action}'.",
                cancellationToken);
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = reviewedStep.Id,
                Type = "delivery.readiness-resolved",
                Message =
                    $"Customer resolved readiness '{binding.State}' with '{action}'.",
                DataJson = JsonSerializer.Serialize(new
                {
                    Version = "delivery-readiness-resolution-v1",
                    Action = action.ToString(),
                    State = binding.State.ToString(),
                    binding.Revision,
                    binding.ContractHash,
                    ReviewedCandidateId = binding.Candidate.Id
                })
            });

            if (action == ReadinessResolutionAction.Continue)
            {
                lifecycle.ResolveReadinessState(
                    flow,
                    binding.State,
                    DeliveryReadinessState.Blocked,
                    binding.Record.CandidateFingerprint,
                    binding.Candidate.CandidateFingerprint,
                    FlowStatus.Queued);
            }
            else
            {
                // Move through the guarded readiness path first so a Blocked flow can be replanned
                // without widening the ordinary transition table.
                lifecycle.ResolveReadinessState(
                    flow,
                    binding.State,
                    required,
                    binding.Record.CandidateFingerprint,
                    binding.Candidate.CandidateFingerprint,
                    FlowStatus.Reworking);
                ApplyRefinement(
                    flow,
                    reviewedStep,
                    refinement!,
                    now,
                    database,
                    lifecycle);
            }
            flow.CurrentBlockerCode = null;
            flow.CurrentBlockerSummary = null;
            flow.CurrentBlockerDataJson = null;
            flow.CustomerBlockerMessage = null;
            flow.OutcomeLabel = string.Empty;
            flow.UpdatedAt = now;
            status = flow.Status;
            iteration = flow.Iteration;
            queueFlow = true;
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        if (queueFlow && !flowQueue.Queue(flowId))
        {
            throw new InvalidOperationException(
                "The durable readiness resolution was recorded, but the flow could not be queued.");
        }
        if (demoRuntimeRevoker is not null &&
            status is not (FlowStatus.WaitingForFeedback or FlowStatus.Approved))
        {
            await demoRuntimeRevoker.RevokeFlowAsync(
                flowId,
                "Live demos were revoked because readiness resolution invalidated the reviewed preview.",
                CancellationToken.None);
        }
        return new ReadinessResolutionResponse(
            flowId,
            action,
            resolvedFrom,
            status,
            iteration,
            action == ReadinessResolutionAction.Continue
                ? "The blocked iteration was re-queued for another attempt."
                : $"A new iteration {iteration} was queued from the customer's requested changes.");
    }

    private async Task<ReviewCoordinationResult> PromoteToDeliveryAsync(
        Guid flowId,
        Guid gateId,
        CancellationToken cancellationToken)
    {
        var admission = admissionService
            ?? throw new InvalidOperationException(
                "Advisory promotion is not configured.");
        var links = linkedFlows
            ?? throw new InvalidOperationException(
                "Advisory promotion is not configured.");

        await using var lifecycleLease =
            await lifecycle.EnterAsync(flowId, cancellationToken);
        FlowRun parent;
        FlowRun? child;
        HandoffGateRecord gate;
        FlowOutcomeDocument outcome;
        await using (var database =
                     await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            parent = await LoadPromotionParentAsync(
                database,
                flowId,
                cancellationToken);
            (gate, outcome) = ValidatePromotion(parent, gateId);
            child = parent.LinkedFlowRuns.SingleOrDefault(item =>
                item.ParentIteration == parent.Iteration &&
                item.LinkKind == FlowLinkKind.AdvisoryPromotion);
            if (child is not null)
            {
                EnsurePromotionChild(parent, child);
            }
        }

        if (child is null)
        {
            await admission.EnsureReadyForContextAsync(
                parent.RepositoryPath,
                parent.RepositoryKnowledge,
                cancellationToken);
        }

        HandoffGateRecord? resolvedForHistory = null;
        var existingChild = child is not null;
        try
        {
            await using var database =
                await databaseFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction =
                await database.Database.BeginTransactionAsync(cancellationToken);
            var trackedParent = await LoadPromotionParentAsync(
                database,
                flowId,
                cancellationToken);
            var validated = ValidatePromotion(trackedParent, gateId);
            gate = validated.Gate;
            outcome = validated.Outcome;
            child = trackedParent.LinkedFlowRuns.SingleOrDefault(item =>
                item.ParentIteration == trackedParent.Iteration &&
                item.LinkKind == FlowLinkKind.AdvisoryPromotion);
            if (child is null)
            {
                var settings = await database.Settings
                    .AsNoTracking()
                    .SingleAsync(cancellationToken);
                var seed = LinkedFlowCoordinator.SerializePromotionSeed(outcome);
                child = links.CreateTrackedSuccessor(
                    database,
                    trackedParent,
                    FlowLinkKind.AdvisoryPromotion,
                    FlowKind.Delivery,
                    OutcomeTypeRules.RequireDelivery(
                        settings.Outcome,
                        nameof(settings.Outcome)),
                    BuildPromotionTitle(outcome.Goal),
                    seed,
                    settings);
            }
            else
            {
                EnsurePromotionChild(trackedParent, child);
                existingChild = true;
            }

            var now = DateTimeOffset.UtcNow;
            if (!gate.Resolved)
            {
                resolvedForHistory = gateEngine.PrepareReviewResolution(
                    gate,
                    ReviewDecision.PromotedToDelivery,
                    "customer",
                    "Customer accepted the Advisory result and promoted it to Delivery.",
                    now);
                ApplyPreparedGate(resolvedForHistory, gate);
            }
            else if (gate.ReviewDecision == ReviewDecision.Accepted)
            {
                gate.ReviewDecision = ReviewDecision.PromotedToDelivery;
                gate.ResolutionNote =
                    "Customer accepted the Advisory result and promoted it to Delivery.";
                gate.ResolvedAt = now;
                resolvedForHistory = CloneGate(gate);
            }
            else
            {
                resolvedForHistory = CloneGate(gate);
            }

            if (trackedParent.Status != FlowStatus.Approved)
            {
                lifecycle.Transition(trackedParent, FlowStatus.Approved);
            }
            trackedParent.OutcomeLabel =
                "Advisory result accepted and promoted to Delivery";
            trackedParent.CompletedAt ??= now;
            trackedParent.FailureReason = string.Empty;
            trackedParent.UpdatedAt = now;
            if (!trackedParent.Events.Any(item =>
                    item.Type == "flow.advisory-promoted" &&
                    item.DataJson != null &&
                    item.DataJson.Contains(
                        child.Id.ToString("D"),
                        StringComparison.OrdinalIgnoreCase)))
            {
                trackedParent.Events.Add(new FlowEvent
                {
                    FlowRunId = trackedParent.Id,
                    FlowStepId = gate.FlowStepId,
                    Type = "flow.advisory-promoted",
                    Message =
                        "Customer accepted the Advisory result and created one fresh linked Delivery flow.",
                    DataJson = JsonSerializer.Serialize(new
                    {
                        Version = "advisory-promotion-v1",
                        ChildFlowRunId = child.Id,
                        ParentIteration = trackedParent.Iteration,
                        LinkKind = FlowLinkKind.AdvisoryPromotion.ToString()
                    })
                });
            }
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            parent = trackedParent;
        }
        catch (DbUpdateException exception) when (
            LinkedFlowCoordinator.IsUniqueSuccessorConflict(exception))
        {
            existingChild = true;
            child = await links.FindSuccessorAsync(
                parent.Id,
                parent.Iteration,
                FlowLinkKind.AdvisoryPromotion,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    "A concurrent Advisory promotion was created but could not be loaded.",
                    exception);
            await using var database =
                await databaseFactory.CreateDbContextAsync(cancellationToken);
            parent = await LoadPromotionParentAsync(
                database,
                flowId,
                cancellationToken);
            gate = parent.GateRecords.Single(item => item.Id == gateId);
            EnsurePromotionChild(parent, child);
            resolvedForHistory = CloneGate(gate);
        }

        if (resolvedForHistory is not null)
        {
            gateEngine.RestoreHistory([resolvedForHistory]);
        }
        var intake = await links.EnsureInitialIntakeAsync(
            child.Id,
            cancellationToken);
        return await BuildResultAsync(
            flowId,
            gateId,
            ReviewIntent.PromoteToDelivery,
            ReviewDecision.PromotedToDelivery,
            publicationStepId: null,
            linkedFlowId: intake.Flow.Id,
            message: existingChild
                ? "This Advisory promotion already exists; the existing linked Delivery flow was returned."
                : "The Advisory result was accepted and a fresh linked Delivery flow entered Account Manager intake.",
            cancellationToken: cancellationToken);
    }

    public async Task<FlowReviewResultResponse> GetReviewResultAsync(
        Guid flowId,
        CancellationToken cancellationToken = default)
    {
        var flow = await LoadDetailedFlowAsync(flowId, cancellationToken);
        EnsureStudioReviewFlow(flow);
        var currentStepIds = flow.Steps
            .Where(step => step.Iteration == flow.Iteration)
            .Select(step => step.Id)
            .ToHashSet();
        var gate = flow.GateRecords
            .Where(item =>
                item.ActionType == HandoffActionType.CustomerReview &&
                currentStepIds.Contains(item.FlowStepId))
            .OrderByDescending(item => item.DecidedAt)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "This flow has no customer-review result for its current iteration.");
        var linked = flow.LinkedFlowRuns.SingleOrDefault(item =>
            item.ParentIteration == flow.Iteration &&
            item.LinkKind == FlowLinkKind.AdvisoryPromotion);
        return new FlowReviewResultResponse(
            flow.Id,
            gate.Id,
            flow.Status,
            flow.Iteration,
            gate.Resolved,
            gate.Approved,
            gate.ReviewDecision,
            GetPublicationStatus(flow),
            linked?.Id,
            linked?.LinkKind);
    }

    public async Task<ReviewFeedbackCoordinationResult> RespondToFeedbackAsync(
        Guid flowId,
        string feedback,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeText(
            feedback,
            ReviewFeedbackParser.MaximumRequestedChangeCharacters,
            "Customer feedback");
        var semaphore = _feedbackClassificationLocks.GetOrAdd(
            flowId,
            static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            return await RespondToFeedbackCoreAsync(
                flowId,
                normalized,
                cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }

    internal async Task<IReadOnlyList<Guid>>
        RecoverFeedbackClassificationsAsync(
            CancellationToken cancellationToken)
    {
        List<FeedbackRecoveryCandidate> candidates;
        await using (var database =
                     await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            candidates = await (
                    from step in database.FlowSteps.AsNoTracking()
                    join flow in database.Flows.AsNoTracking()
                        on step.FlowRunId equals flow.Id
                    where flow.ContractVersion == "studio-v2" &&
                          flow.Status != FlowStatus.Abandoned &&
                          flow.Status != FlowStatus.Abandoning &&
                          step.InvocationKind ==
                          ExecutionInvocationKind.ReviewClassification &&
                          step.PlanStepKey.StartsWith(
                              FeedbackClassificationPlanStepPrefix) &&
                          database.FlowEvents.Any(flowEvent =>
                              flowEvent.FlowStepId == step.Id &&
                              flowEvent.Type ==
                              FeedbackRequestEventType) &&
                          !database.FlowEvents.Any(flowEvent =>
                              flowEvent.FlowStepId == step.Id &&
                              (flowEvent.Type ==
                                   FeedbackAppliedEventType ||
                               flowEvent.Type ==
                                   FeedbackSupersededEventType)) &&
                          (step.Status == StepStatus.Completed ||
                           step.Status == StepStatus.Pending ||
                           step.Status == StepStatus.Running)
                    orderby step.FlowRunId, step.Sequence, step.Attempt
                    select new FeedbackRecoveryCandidate(
                        step.FlowRunId,
                        step.Id,
                        step.InvocationKind))
                .ToListAsync(cancellationToken);
        }

        var recovered = new HashSet<Guid>();
        foreach (var candidate in candidates)
        {
            if (candidate.InvocationKind !=
                ExecutionInvocationKind.ReviewClassification)
            {
                throw new InvalidOperationException(
                    "A review-classification recovery candidate lost its invocation kind.");
            }
            var semaphore = _feedbackClassificationLocks.GetOrAdd(
                candidate.FlowId,
                static _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var prepared =
                    await PrepareRecoveredFeedbackClassificationAsync(
                        candidate,
                        cancellationToken);
                if (prepared is null)
                {
                    continue;
                }
                var result = await RespondToPreparedFeedbackAsync(
                    prepared,
                    prepared.Step.InputSummary,
                    cancellationToken);
                recovered.Add(candidate.FlowId);
                if (result.Intent != ReviewIntent.Ambiguous)
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (FeedbackClassificationDeferredException)
            {
                // A verified live Copilot owner still exists. Leave the durable turn Running
                // and let a later reconciliation pass inspect it again.
            }
            catch (Exception exception)
            {
                await RecordFeedbackRecoveryFailureAsync(
                    candidate,
                    exception,
                    CancellationToken.None);
            }
            finally
            {
                semaphore.Release();
            }
        }
        return recovered.ToArray();
    }

    private async Task<ReviewFeedbackCoordinationResult>
        RespondToFeedbackCoreAsync(
            Guid flowId,
            string normalizedFeedback,
            CancellationToken cancellationToken)
    {
        var prepared = await PrepareFeedbackClassificationAsync(
            flowId,
            normalizedFeedback,
            cancellationToken);
        return await RespondToPreparedFeedbackAsync(
            prepared,
            normalizedFeedback,
            cancellationToken);
    }

    private async Task<ReviewFeedbackCoordinationResult>
        RespondToPreparedFeedbackAsync(
            PreparedFeedbackClassification prepared,
            string normalizedFeedback,
            CancellationToken cancellationToken)
    {
        if (prepared.ResolvedReplay)
        {
            var replayed = ParseStoredFeedback(prepared.Step);
            return new ReviewFeedbackCoordinationResult(
                await LoadDetailedFlowAsync(
                    prepared.Flow.Id,
                    cancellationToken),
                replayed.Document.CustomerReply,
                ShouldSpeak: true,
                replayed.Document.Intent!.Value,
                Review: null);
        }

        ParsedReviewFeedback parsed;
        if (prepared.Step.Status == StepStatus.Completed)
        {
            parsed = ParseStoredFeedback(prepared.Step);
            await ValidateStoredFeedbackClassificationAsync(
                prepared,
                parsed,
                cancellationToken);
        }
        else
        {
            parsed = await ExecuteFeedbackClassificationAsync(
                prepared,
                normalizedFeedback,
                cancellationToken);
        }

        return await ApplyFeedbackClassificationAsync(
            prepared,
            parsed,
            cancellationToken);
    }

    private async Task<PreparedFeedbackClassification?>
        PrepareRecoveredFeedbackClassificationAsync(
            FeedbackRecoveryCandidate candidate,
            CancellationToken cancellationToken)
    {
        await using var lifecycleLease =
            await lifecycle.EnterAsync(candidate.FlowId, cancellationToken);
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);
        var flow = await database.Flows
                       .AsSplitQuery()
                       .Include(item => item.Steps)
                       .Include(item => item.Events)
                       .Include(item => item.Messages)
                       .Include(item => item.GateRecords)
                       .Include(item => item.AgentSnapshots)
                       .Include(item => item.TaskProfiles)
                       .SingleOrDefaultAsync(
                           item => item.Id == candidate.FlowId,
                           cancellationToken)
                   ?? throw new KeyNotFoundException(
                       $"Factory flow '{candidate.FlowId}' was not found.");
        EnsureStudioReviewFlow(flow);
        var step = flow.Steps.SingleOrDefault(item =>
                       item.Id == candidate.StepId)
                   ?? throw new InvalidOperationException(
                       "The durable review classification step is missing.");
        if (step.FlowRunId != flow.Id ||
            step.InvocationKind !=
            ExecutionInvocationKind.ReviewClassification)
        {
            throw new InvalidOperationException(
                "The durable review classification step does not belong to the recovered flow.");
        }
        if (flow.Events.Any(item =>
                item.FlowStepId == step.Id &&
                item.Type is
                    FeedbackAppliedEventType or
                    FeedbackSupersededEventType))
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var requestEvents = flow.Events
            .Where(item =>
                item.FlowStepId == step.Id &&
                item.Type == FeedbackRequestEventType)
            .ToArray();
        if (requestEvents.Length != 1 ||
            requestEvents[0].FlowRunId != flow.Id ||
            requestEvents[0].FlowStepId != step.Id)
        {
            throw new InvalidOperationException(
                "The durable review classification has no unique exact request identity.");
        }
        var request = TryReadFeedbackRequest(requestEvents[0])
                      ?? throw new InvalidOperationException(
                          "The durable review classification request identity is missing.");
        if (request.FlowStepId != step.Id ||
            request.ReviewIteration != step.Iteration)
        {
            throw new InvalidOperationException(
                "The durable review classification request does not match its exact step and iteration.");
        }

        var normalizedFeedback = NormalizeText(
            step.InputSummary,
            ReviewFeedbackParser.MaximumRequestedChangeCharacters,
            "Customer feedback");
        var feedbackHash = ComputeFeedbackHash(normalizedFeedback);
        var requestHash = ComputeFeedbackRequestHash(
            flow.Id,
            request.ReviewIteration,
            request.GateId,
            normalizedFeedback);
        if (!string.Equals(
                request.FeedbackHash,
                feedbackHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                request.RequestHash,
                requestHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The durable review classification message or request hash does not match its exact persisted identity.");
        }
        EnsureFeedbackClassificationStep(step, request);

        var gate = flow.GateRecords.SingleOrDefault(item =>
            item.Id == request.GateId);
        var owner = gate is null
            ? null
            : flow.Steps.SingleOrDefault(item =>
                item.Id == gate.FlowStepId);
        var staleReason =
            flow.Iteration != request.ReviewIteration
                ? "FlowAdvanced"
                : gate is null
                    ? "GateMissing"
                    : gate.FlowRunId != flow.Id ||
                      gate.ActionType !=
                      HandoffActionType.CustomerReview ||
                      owner is null ||
                      owner.Iteration != request.ReviewIteration ||
                      !owner.IsOutcomeOwner ||
                      owner.Status != StepStatus.Completed ||
                      string.IsNullOrWhiteSpace(
                          flow.OutcomeOwnerPlanStepKey) ||
                      !string.Equals(
                          owner.PlanStepKey,
                          flow.OutcomeOwnerPlanStepKey,
                          StringComparison.Ordinal)
                        ? "GateStale"
                        : !gate.Resolved &&
                          (flow.Status !=
                               FlowStatus.WaitingForFeedback ||
                           flow.GateRecords.Count(item =>
                               item.ActionType ==
                                   HandoffActionType.CustomerReview &&
                               !item.Resolved &&
                               flow.Steps.Any(candidateStep =>
                                   candidateStep.Id ==
                                       item.FlowStepId &&
                                   candidateStep.Iteration ==
                                       request.ReviewIteration)) != 1)
                            ? "GateNoLongerCurrent"
                            : null;
        if (staleReason is not null ||
            gate is { Resolved: true } &&
            step.Status != StepStatus.Completed)
        {
            MarkFeedbackClassificationSuperseded(
                flow,
                step,
                request,
                staleReason ?? "GateAlreadyResolved");
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var accountManager = flow.AgentSnapshots.SingleOrDefault(item =>
                item.AgentId == step.AgentId &&
                item.EnabledAtSnapshot)
            ?? throw new InvalidOperationException(
                "The review classifier is absent from the immutable flow snapshot.");
        var prepared = new PreparedFeedbackClassification(
            flow,
            step,
            request.GateId,
            request.RequestHash,
            request.FeedbackHash,
            accountManager,
            ResolvedReplay: false);
        if (gate!.Resolved)
        {
            var parsed = ParseStoredFeedback(step);
            try
            {
                ValidateFeedbackClassification(
                    flow,
                    prepared,
                    parsed);
            }
            catch (ReviewFeedbackFlowValidationException)
            {
                MarkFeedbackClassificationSuperseded(
                    flow,
                    step,
                    request,
                    "GateResolvedDifferently");
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return prepared;
    }

    private async Task<PreparedFeedbackClassification>
        PrepareFeedbackClassificationAsync(
            Guid flowId,
            string normalizedFeedback,
            CancellationToken cancellationToken)
    {
        var feedbackHash = ComputeFeedbackHash(normalizedFeedback);
        await using var lifecycleLease =
            await lifecycle.EnterAsync(flowId, cancellationToken);
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);
        var flow = await database.Flows
                       .AsSplitQuery()
                       .Include(item => item.Steps)
                       .Include(item => item.Events)
                       .Include(item => item.Messages)
                       .Include(item => item.GateRecords)
                       .Include(item => item.AgentSnapshots)
                       .Include(item => item.TaskProfiles)
                       .SingleOrDefaultAsync(
                           item => item.Id == flowId,
                           cancellationToken)
                   ?? throw new KeyNotFoundException(
                       $"Factory flow '{flowId}' was not found.");
        EnsureStudioReviewFlow(flow);
        var currentStepIds = flow.Steps
            .Where(step => step.Iteration == flow.Iteration)
            .Select(step => step.Id)
            .ToHashSet();
        var gates = flow.GateRecords
            .Where(item =>
                item.ActionType == HandoffActionType.CustomerReview &&
                !item.Resolved &&
                currentStepIds.Contains(item.FlowStepId))
            .OrderByDescending(item => item.DecidedAt)
            .ToList();
        if (gates.Count > 1)
        {
            throw new InvalidOperationException(
                "The flow has duplicate unresolved customer reviews for its current iteration.");
        }
        var gate = gates.SingleOrDefault();
        if (gate is null)
        {
            var replay = FindFeedbackRequestByMessageHash(
                flow,
                feedbackHash);
            if (replay is null)
            {
                throw new InvalidOperationException(
                    "The flow has no unresolved customer review for this feedback.");
            }
            var replayStep = flow.Steps.SingleOrDefault(
                    item => item.Id == replay.FlowStepId)
                ?? throw new InvalidOperationException(
                    "The durable review classification has no visible FlowStep.");
            if (replayStep.Status != StepStatus.Completed)
            {
                throw new InvalidOperationException(
                    "The prior review classification did not reach a durable result.");
            }
            EnsureFeedbackClassificationStep(
                replayStep,
                replay);
            var alreadyApplied = flow.Events.Any(item =>
                item.FlowStepId == replayStep.Id &&
                item.Type == FeedbackAppliedEventType);
            await transaction.CommitAsync(cancellationToken);
            return new PreparedFeedbackClassification(
                flow,
                replayStep,
                replay.GateId,
                replay.RequestHash,
                replay.FeedbackHash,
                flow.AgentSnapshots.Single(item =>
                    item.AgentId == replayStep.AgentId),
                ResolvedReplay: alreadyApplied);
        }
        if (flow.Status != FlowStatus.WaitingForFeedback)
        {
            throw new InvalidOperationException(
                "Customer feedback is accepted only while the current result awaits review.");
        }
        if (string.IsNullOrWhiteSpace(flow.OutcomeContractJson))
        {
            throw new InvalidOperationException(
                "Review feedback classification requires the current normalized result.");
        }

        var requestHash = ComputeFeedbackRequestHash(
            flow.Id,
            flow.Iteration,
            gate.Id,
            normalizedFeedback);
        var existingRequest = flow.Events
            .Where(item => item.Type == FeedbackRequestEventType)
            .Select(TryReadFeedbackRequest)
            .OfType<FeedbackRequestIdentity>()
            .SingleOrDefault(item =>
                string.Equals(
                    item.RequestHash,
                    requestHash,
                    StringComparison.Ordinal));
        FlowStep step;
        FlowAgentSnapshot accountManager;
        if (existingRequest is not null)
        {
            if (existingRequest.GateId != gate.Id ||
                existingRequest.ReviewIteration != flow.Iteration)
            {
                throw new InvalidOperationException(
                    "The durable review feedback request belongs to another review.");
            }
            step = flow.Steps.SingleOrDefault(
                    item => item.Id == existingRequest.FlowStepId)
                ?? throw new InvalidOperationException(
                    "The durable review feedback request has no visible FlowStep.");
            EnsureFeedbackClassificationStep(
                step,
                existingRequest);
            accountManager = flow.AgentSnapshots.SingleOrDefault(item =>
                    item.AgentId == step.AgentId &&
                    item.EnabledAtSnapshot)
                ?? throw new InvalidOperationException(
                    "The review classifier is absent from the immutable flow snapshot.");
        }
        else
        {
            var activeOtherRequest = flow.Events
                .Where(item => item.Type == FeedbackRequestEventType)
                .Select(TryReadFeedbackRequest)
                .OfType<FeedbackRequestIdentity>()
                .Where(item =>
                    item.GateId == gate.Id &&
                    !string.Equals(
                        item.RequestHash,
                        requestHash,
                        StringComparison.Ordinal))
                .Join(
                    flow.Steps,
                    item => item.FlowStepId,
                    item => item.Id,
                    (_, candidate) => candidate)
                .Any(candidate =>
                    candidate.Status is StepStatus.Pending or StepStatus.Running);
            if (activeOtherRequest)
            {
                throw new InvalidOperationException(
                    "A different free-text review classification is already in progress for this gate.");
            }

            accountManager = flow.AgentSnapshots.SingleOrDefault(item =>
                    item.EnabledAtSnapshot &&
                    string.Equals(
                        item.AgentId,
                        "account-manager",
                        StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    "This flow has no enabled immutable Account Manager snapshot.");
            var planStepKey =
                FeedbackClassificationPlanStepPrefix +
                requestHash["sha256:".Length..];
            step = new FlowStep
            {
                FlowRunId = flow.Id,
                Iteration = flow.Iteration,
                Sequence = flow.Steps
                    .Where(item => item.Iteration == flow.Iteration)
                    .Select(item => item.Sequence)
                    .DefaultIfEmpty(10)
                    .Max() + 10,
                AgentId = accountManager.AgentId,
                AgentName = accountManager.Name,
                AgentRole = accountManager.Role,
                Label = "Classify customer review feedback",
                PlanStepKey = planStepKey,
                PlanDutiesJson = JsonSerializer.Serialize(
                    new[] { PlanDuty.Analyze.ToString() }),
                PlanStage = PlanStage.BeforeReview,
                InvocationKind =
                    ExecutionInvocationKind.ReviewClassification,
                PermissionProfile =
                    ExecutionPermissionProfile.ReadOnlySource,
                WorkflowRevision =
                    workflowProvider.GetEffective().Revision,
                Status = StepStatus.Pending,
                Phase = AgentRunPhase.PreparingWorkspace,
                Attempt = 1,
                InputSummary = normalizedFeedback
            };
            step.StableSemanticRootId = step.Id;
            flow.Steps.Add(step);
            flow.Messages.Add(new FlowMessage
            {
                FlowRunId = flow.Id,
                Role = ConversationRole.Customer,
                Content = normalizedFeedback
            });
            flow.Events.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = step.Id,
                Type = FeedbackRequestEventType,
                Message =
                    "Account Manager will classify the current free-text customer review against the normalized result.",
                DataJson = SerializeFeedbackRequestIdentity(
                    step.Id,
                    gate.Id,
                    flow.Iteration,
                    requestHash,
                    feedbackHash)
            });
            database.TaskProfiles.Add(
                RequireFeedbackProfileFactory().Create(
                    accountManager.Role,
                    normalizedFeedback,
                    flow.Id,
                    flow.Iteration,
                    step.Id,
                    planStepKey,
                    accountManager.AgentId));
            flow.UpdatedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new PreparedFeedbackClassification(
            flow,
            step,
            gate.Id,
            requestHash,
            feedbackHash,
            accountManager,
            ResolvedReplay: false);
    }

    private async Task<ParsedReviewFeedback>
        ExecuteFeedbackClassificationAsync(
            PreparedFeedbackClassification prepared,
            string normalizedFeedback,
            CancellationToken cancellationToken)
    {
        var step = prepared.Step;
        if (step.Status == StepStatus.Failed)
        {
            throw new InvalidOperationException(
                "This review feedback classification already exhausted its bounded correction.");
        }
        var workspace = await RequireFeedbackWorkspaceManager()
            .PrepareForInvocationAsync(
            prepared.Flow,
            ExecutionInvocationKind.ReviewClassification,
            cancellationToken);
        var outcome = FlowOutcomeParser.ParseJson(
            prepared.Flow.OutcomeContractJson);
        var initialTask = BuildFeedbackClassificationTask(
            prepared.Flow.Kind,
            outcome.RawJson,
            normalizedFeedback);
        var preparedTurn = await PrepareFeedbackTurnAsync(
            prepared,
            step,
            initialTask,
            cancellationToken);
        step = preparedTurn.Step;
        var turn = preparedTurn.State;

        var stopwatch = Stopwatch.StartNew();
        var results = new List<AgentExecutionResult>();
        CopilotSessionSnapshot? recoveredSnapshot = null;
        async Task VerifyReviewedDeliveryAsync()
        {
            if (prepared.Flow.Kind != FlowKind.Delivery)
            {
                return;
            }
            var identity = ReviewedCandidateLedger.Read(prepared.Flow);
            _ = await RequireFeedbackReviewedCandidateService().VerifyAsync(
                prepared.Flow,
                identity,
                cancellationToken);
        }
        try
        {
            await VerifyReviewedDeliveryAsync();
            var execution = await ExecuteOrRecoverFeedbackTurnAsync(
                prepared,
                step,
                turn,
                workspace.Path,
                preparedTurn.WasRunning,
                preparedTurn.ExpectedAcceptedTimeSeconds,
                cancellationToken);
            step = execution.Step;
            turn = execution.State;
            recoveredSnapshot = execution.RecoveredSnapshot;
            results.Add(execution.Result);
            await VerifyReviewedDeliveryAsync();
            IReadOnlyList<string> correctionErrors;
            try
            {
                var parsed = ReviewFeedbackParser.Parse(
                    execution.Result.Output);
                stopwatch.Stop();
                await CompleteFeedbackClassificationAsync(
                    prepared,
                    parsed,
                    results,
                    stopwatch.ElapsedMilliseconds,
                    cancellationToken);
                await CleanupRecoveredFeedbackSessionAsync(
                    prepared.Flow.Id,
                    step.Id,
                    recoveredSnapshot,
                    CancellationToken.None);
                return parsed;
            }
            catch (ReviewFeedbackContractException firstFailure)
            {
                correctionErrors = firstFailure.Errors;
            }
            catch (ReviewFeedbackFlowValidationException firstFailure)
            {
                correctionErrors = firstFailure.Errors;
            }

            if (turn.Attempt >= 2)
            {
                throw new ReviewFeedbackContractException(
                    correctionErrors);
            }
            var correctionTurn = await PersistFeedbackCorrectionTurnAsync(
                prepared,
                step,
                turn,
                execution.Result,
                recoveredSnapshot,
                correctionErrors,
                cancellationToken);
            step = correctionTurn.Step;
            turn = correctionTurn.State;
            var correction = await RequireFeedbackAgentRunner().ExecuteAsync(
                BuildFeedbackExecutionContext(
                    prepared,
                    step,
                    turn,
                    workspace.Path,
                    correctionTurn.ExpectedAcceptedTimeSeconds,
                    resumeSession:
                        correctionTurn.WasRunning,
                    recoverInterruptedSession: false),
                cancellationToken);
            results.Add(correction);
            await VerifyReviewedDeliveryAsync();
            var corrected = ReviewFeedbackParser.Parse(
                correction.Output);
            stopwatch.Stop();
            await CompleteFeedbackClassificationAsync(
                prepared,
                corrected,
                results,
                stopwatch.ElapsedMilliseconds,
                cancellationToken);
            await CleanupRecoveredFeedbackSessionAsync(
                prepared.Flow.Id,
                step.Id,
                recoveredSnapshot,
                CancellationToken.None);
            return corrected;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FeedbackClassificationDeferredException)
        {
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            await FailFeedbackClassificationAsync(
                prepared,
                exception,
                results.Sum(item =>
                    Math.Max(1, item.ExecutionAttempts)),
                stopwatch.ElapsedMilliseconds,
                CancellationToken.None);
            if (exception is not AgentRunException
                {
                    CanResumeSession: true
                })
            {
                await CleanupRecoveredFeedbackSessionAsync(
                    prepared.Flow.Id,
                    step.Id,
                    recoveredSnapshot,
                    CancellationToken.None);
            }
            if (exception is
                ReviewFeedbackContractException or
                ReviewFeedbackFlowValidationException)
            {
                throw new InvalidOperationException(
                    "The Account Manager could not classify this review safely after one bounded correction. The review remains unresolved.",
                    exception);
            }
            throw;
        }

        throw new UnreachableException();
    }

    private async Task<PreparedFeedbackTurn> PrepareFeedbackTurnAsync(
        PreparedFeedbackClassification prepared,
        FlowStep step,
        string initialTask,
        CancellationToken cancellationToken)
    {
        RoutingDecision? routing = null;
        if (string.IsNullOrWhiteSpace(step.Model))
        {
            routing = await RequireFeedbackModelRouter().SelectAsync(
                new RoutingRequest(
                    step.Id,
                    prepared.Flow.ModelSelectionStrategy),
                cancellationToken);
        }

        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var durable = await database.FlowSteps.SingleAsync(
            item => item.Id == step.Id,
            cancellationToken);
        var wasRunning = durable.Status == StepStatus.Running;
        if (durable.Status is not (
                StepStatus.Pending or StepStatus.Running))
        {
            throw new InvalidOperationException(
                $"The review classifier cannot launch from {durable.Status}.");
        }
        if (routing is not null)
        {
            durable.Model = routing.SelectedModel;
            durable.ModelEffort = routing.SelectedEffort;
            durable.ModelReason = routing.Reason;
        }
        if (string.IsNullOrWhiteSpace(durable.Model) ||
            string.IsNullOrWhiteSpace(durable.ModelEffort))
        {
            throw new InvalidOperationException(
                "The review classifier has no durable routing identity.");
        }

        FeedbackClassificationTurnState turn;
        if (string.IsNullOrWhiteSpace(
                durable.ReviewClassificationStateJson))
        {
            if (durable.Attempt > 1 &&
                string.IsNullOrWhiteSpace(durable.ExecutionPrompt))
            {
                throw new InvalidOperationException(
                    "The persisted correction turn has no durable correction input.");
            }
            var task = durable.Attempt > 1
                ? durable.ExecutionPrompt
                : initialTask;
            var sessionId = durable.CopilotSessionId ??
                AgentSessionIdentity.Create(
                    prepared.Flow.Id,
                    prepared.Flow.Iteration,
                    prepared.AccountManager.AgentId,
                    durable.PlanStepKey);
            turn = new FeedbackClassificationTurnState(
                FeedbackClassificationTurnState.CurrentVersion,
                Math.Clamp(durable.Attempt, 1, 2),
                durable.Attempt > 1 ? "Correction" : "Initial",
                task,
                [],
                sessionId,
                SessionGeneration: 1,
                PriorOutputSha256: null,
                PriorJournalCompletedAt: null);
        }
        else
        {
            turn = DeserializeFeedbackTurn(
                durable.ReviewClassificationStateJson);
        }
        ValidateFeedbackTurn(durable, turn);
        BindFeedbackPermissionAtFirstLaunch(
            prepared.Flow,
            durable,
            wasRunning);

        durable.Attempt = turn.Attempt;
        durable.ExecutionPrompt = turn.Task;
        durable.ReviewClassificationStateJson =
            SerializeFeedbackTurn(turn);
        durable.Status = StepStatus.Running;
        durable.Phase = AgentRunPhase.BuildingPrompt;
        durable.StartedAt ??= DateTimeOffset.UtcNow;
        durable.CopilotSessionId = turn.SessionId;
        durable.CopilotSessionHome =
            string.IsNullOrWhiteSpace(durable.CopilotSessionHome)
                ? CopilotReasoningHost.ResolveCopilotSessionHome()
                : durable.CopilotSessionHome;
        await database.SaveChangesAsync(cancellationToken);
        return new PreparedFeedbackTurn(
            durable,
            turn,
            wasRunning,
            routing?.PredictedAcceptedTimeSeconds ?? 0);
    }

    private void BindFeedbackPermissionAtFirstLaunch(
        FlowRun flow,
        FlowStep step,
        bool wasRunning)
    {
        if (!string.IsNullOrWhiteSpace(
                step.EffectivePermissionJson))
        {
            return;
        }
        var workflow = workflowProvider.GetEffective();
        if (wasRunning &&
            !string.IsNullOrWhiteSpace(step.WorkflowRevision) &&
            !string.Equals(
                step.WorkflowRevision,
                workflow.Revision,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A started review-classification turn has no persisted permission policy for its workflow revision.");
        }
        var request = new PermissionResolutionRequest(
            flow.Kind,
            ExecutionInvocationKind.ReviewClassification,
            PlanStage.BeforeReview,
            ImmutableArray.Create(PlanDuty.Analyze),
            DurableReviewDecision: null,
            DurableApproval: false,
            IsOnlyPlannedPublishStep: false,
            ContractVersion: flow.ContractVersion,
            LegacyPublicationAuthorized: false,
            IsGovernedOutcomeVerification: false);
        var permission = _permissionResolver.Resolve(
            request,
            PermissionProfileResolver.FromWorkflow(workflow));
        PermissionProfileResolver.ValidatePersisted(
            permission,
            permission.Profile,
            request);
        step.PermissionProfile = permission.Profile;
        step.EffectivePermissionJson =
            JsonSerializer.Serialize(permission);
        step.WorkflowRevision = workflow.Revision;
    }

    private async Task<ExecutedFeedbackTurn>
        ExecuteOrRecoverFeedbackTurnAsync(
            PreparedFeedbackClassification prepared,
            FlowStep step,
            FeedbackClassificationTurnState turn,
            string workspacePath,
            bool wasRunning,
            double expectedAcceptedTimeSeconds,
            CancellationToken cancellationToken)
    {
        if (!wasRunning)
        {
            var result = await RequireFeedbackAgentRunner().ExecuteAsync(
                BuildFeedbackExecutionContext(
                    prepared,
                    step,
                    turn,
                    workspacePath,
                    expectedAcceptedTimeSeconds,
                    resumeSession: false,
                    recoverInterruptedSession: false),
                cancellationToken);
            return new ExecutedFeedbackTurn(
                step,
                turn,
                result,
                RecoveredSnapshot: null);
        }

        var sessionHome = string.IsNullOrWhiteSpace(
            step.CopilotSessionHome)
            ? CopilotReasoningHost.ResolveCopilotSessionHome()
            : step.CopilotSessionHome;
        var snapshot = await InspectSessionAsync(
            sessionHome,
            turn.SessionId,
            cancellationToken);
        while (true)
        {
            switch (snapshot.State)
            {
                case CopilotSessionJournalState.Completed
                    when snapshot.Result is { Success: true } recovered &&
                         CopilotReasoningHost.IsRecoveryCurrent(
                             step.StartedAt,
                             snapshot.CompletedAt):
                    if (IsPriorClassifierCompletion(
                            turn,
                            snapshot,
                            recovered.OutputSummary))
                    {
                        var correction = await RequireFeedbackAgentRunner()
                            .ExecuteAsync(
                                BuildFeedbackExecutionContext(
                                    prepared,
                                    step,
                                    turn,
                                    workspacePath,
                                    expectedAcceptedTimeSeconds,
                                    resumeSession: true,
                                    recoverInterruptedSession: false),
                                cancellationToken);
                        return new ExecutedFeedbackTurn(
                            step,
                            turn,
                            correction,
                            RecoveredSnapshot: snapshot);
                    }
                    return new ExecutedFeedbackTurn(
                        step,
                        turn,
                        new AgentExecutionResult(
                            recovered.OutputSummary,
                            "Recovered from the durable Copilot session journal.",
                            1,
                            recovered.ToolCalls),
                        snapshot);

                case CopilotSessionJournalState.Completed:
                    throw new InvalidOperationException(
                        "The completed review-classification journal is stale or unsuccessful and requires a fresh customer retry.");

                case CopilotSessionJournalState.Interrupted:
                {
                    var resumed = await RequireFeedbackAgentRunner()
                        .ExecuteAsync(
                            BuildFeedbackExecutionContext(
                                prepared,
                                step,
                                turn,
                                workspacePath,
                                expectedAcceptedTimeSeconds,
                                resumeSession: true,
                                recoverInterruptedSession: true),
                            cancellationToken);
                    return new ExecutedFeedbackTurn(
                        step,
                        turn,
                        resumed,
                        RecoveredSnapshot: null);
                }

                case CopilotSessionJournalState.Active:
                    if (!TryStopActiveSession(snapshot))
                    {
                        throw new FeedbackClassificationDeferredException(
                            "The review classifier is still owned by an active Copilot process; recovery was deferred.");
                    }
                    snapshot = await InspectSessionAsync(
                        sessionHome,
                        turn.SessionId,
                        cancellationToken);
                    if (snapshot.State ==
                        CopilotSessionJournalState.Active)
                    {
                        throw new FeedbackClassificationDeferredException(
                            "The review classifier remained active after safe reconciliation; recovery was deferred.");
                    }
                    continue;

                case CopilotSessionJournalState.Missing:
                {
                    var fresh = await StartFreshFeedbackSessionAsync(
                        prepared,
                        step,
                        turn,
                        cancellationToken);
                    var restarted = await RequireFeedbackAgentRunner()
                        .ExecuteAsync(
                            BuildFeedbackExecutionContext(
                                prepared,
                                fresh.Step,
                                fresh.State,
                                workspacePath,
                                expectedAcceptedTimeSeconds,
                                resumeSession: false,
                                recoverInterruptedSession: false),
                            cancellationToken);
                    return new ExecutedFeedbackTurn(
                        fresh.Step,
                        fresh.State,
                        restarted,
                        RecoveredSnapshot: null);
                }

                default:
                    throw new InvalidOperationException(
                        "The review-classification journal state is unsupported.");
            }
        }
    }

    private async Task<PreparedFeedbackTurn>
        StartFreshFeedbackSessionAsync(
            PreparedFeedbackClassification prepared,
            FlowStep step,
            FeedbackClassificationTurnState turn,
            CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var durable = await database.FlowSteps.SingleAsync(
            item => item.Id == step.Id,
            cancellationToken);
        var current = DeserializeFeedbackTurn(
            durable.ReviewClassificationStateJson);
        if (!EquivalentFeedbackTurn(current, turn))
        {
            throw new InvalidOperationException(
                "The durable review-classification turn changed during recovery.");
        }
        var generation = checked(turn.SessionGeneration + 1);
        var abandonedSessionHome =
            string.IsNullOrWhiteSpace(durable.CopilotSessionHome)
                ? CopilotReasoningHost.ResolveCopilotSessionHome()
                : durable.CopilotSessionHome;
        var sessionId = AgentSessionIdentity.Create(
            prepared.Flow.Id,
            prepared.Flow.Iteration,
            $"{prepared.AccountManager.AgentId}:review-classification:" +
            $"{durable.Id:N}:session:{generation}",
            durable.PlanStepKey);
        var restarted = turn with
        {
            SessionId = sessionId,
            SessionGeneration = generation
        };
        durable.ReviewClassificationStateJson =
            SerializeFeedbackTurn(restarted);
        durable.CopilotSessionId = sessionId;
        durable.CopilotSessionHome =
            CopilotReasoningHost.ResolveCopilotSessionHome();
        durable.StartedAt = DateTimeOffset.UtcNow;
        durable.Phase = AgentRunPhase.BuildingPrompt;
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = prepared.Flow.Id,
            FlowStepId = durable.Id,
            Type =
                "review.feedback-classification-session-restarted",
            Message =
                "The persisted classifier turn had no journal and started a fresh deterministic session without resume.",
            DataJson = JsonSerializer.Serialize(new
            {
                Version =
                    "review-feedback-session-restart-v1",
                Attempt = restarted.Attempt,
                restarted.SessionGeneration,
                restarted.SessionId
            })
        });
        _manifestStager.CleanupSessionRoot(
            abandonedSessionHome,
            turn.SessionId);
        await database.SaveChangesAsync(cancellationToken);
        return new PreparedFeedbackTurn(
            durable,
            restarted,
            WasRunning: false,
            ExpectedAcceptedTimeSeconds: 0);
    }

    private async Task<PreparedFeedbackTurn>
        PersistFeedbackCorrectionTurnAsync(
            PreparedFeedbackClassification prepared,
            FlowStep step,
            FeedbackClassificationTurnState prior,
            AgentExecutionResult priorResult,
            CopilotSessionSnapshot? priorSnapshot,
            IReadOnlyList<string> errors,
            CancellationToken cancellationToken)
    {
        var boundedErrors = errors
            .Take(24)
            .Select(error => Clip(error, 500))
            .ToArray();
        var correctionTask = BuildFeedbackCorrectionTask(
            boundedErrors);
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var durable = await database.FlowSteps.SingleAsync(
            item => item.Id == step.Id,
            cancellationToken);
        var current = DeserializeFeedbackTurn(
            durable.ReviewClassificationStateJson);
        if (current.Attempt == 2)
        {
            ValidateFeedbackTurn(durable, current);
            return new PreparedFeedbackTurn(
                durable,
                current,
                WasRunning: true,
                ExpectedAcceptedTimeSeconds: 0);
        }
        if (!EquivalentFeedbackTurn(current, prior) ||
            current.Attempt != 1)
        {
            throw new InvalidOperationException(
                "The durable classifier turn changed before correction could be persisted.");
        }

        var priorOutputHash =
            OutcomeVerificationRules.ComputeSha256(
                priorResult.Output);
        var priorCompletedAt = priorSnapshot?.CompletedAt;
        var priorHome =
            string.IsNullOrWhiteSpace(durable.CopilotSessionHome)
                ? CopilotReasoningHost.ResolveCopilotSessionHome()
                : durable.CopilotSessionHome;
        if (priorCompletedAt is null)
        {
            var journal = await InspectSessionAsync(
                priorHome,
                prior.SessionId,
                cancellationToken);
            if (journal.State ==
                    CopilotSessionJournalState.Completed &&
                journal.Result is { Success: true } completed &&
                string.Equals(
                    OutcomeVerificationRules.ComputeSha256(
                        completed.OutputSummary),
                    priorOutputHash,
                    StringComparison.Ordinal))
            {
                priorCompletedAt = journal.CompletedAt;
            }
        }
        var resumeCorrection = priorCompletedAt is not null;
        var correctionSessionId = prior.SessionId;
        var correctionGeneration = prior.SessionGeneration;
        if (!resumeCorrection)
        {
            _manifestStager.CleanupSessionRoot(
                priorHome,
                prior.SessionId);
            correctionGeneration =
                checked(prior.SessionGeneration + 1);
            correctionSessionId = AgentSessionIdentity.Create(
                prepared.Flow.Id,
                prepared.Flow.Iteration,
                $"{prepared.AccountManager.AgentId}:review-classification:" +
                $"{durable.Id:N}:correction:{correctionGeneration}",
                durable.PlanStepKey);
        }
        var correction = prior with
        {
            Attempt = 2,
            Kind = "Correction",
            Task = correctionTask,
            ValidationErrors = boundedErrors,
            SessionId = correctionSessionId,
            SessionGeneration = correctionGeneration,
            PriorOutputSha256 =
                resumeCorrection ? priorOutputHash : null,
            PriorJournalCompletedAt = priorCompletedAt
        };
        durable.Attempt = correction.Attempt;
        durable.ExecutionPrompt = correction.Task;
        durable.ReviewClassificationStateJson =
            SerializeFeedbackTurn(correction);
        durable.Status = StepStatus.Running;
        durable.Phase = AgentRunPhase.BuildingPrompt;
        durable.CopilotSessionId = correction.SessionId;
        if (!resumeCorrection)
        {
            durable.CopilotSessionHome =
                CopilotReasoningHost.ResolveCopilotSessionHome();
            durable.StartedAt = DateTimeOffset.UtcNow;
        }
        if (!await database.FlowEvents.AnyAsync(
                item =>
                    item.FlowStepId == durable.Id &&
                    item.Type ==
                    "review.feedback-classification-correction",
                cancellationToken))
        {
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = prepared.Flow.Id,
                FlowStepId = durable.Id,
                Type =
                    "review.feedback-classification-correction",
                Message =
                    "The first classifier result was malformed or invalid for the current flow; one bounded correction turn was requested.",
                DataJson = JsonSerializer.Serialize(new
                {
                    Version = "review-feedback-correction-v1",
                    prepared.RequestHash,
                    Errors = boundedErrors
                })
            });
        }
        await database.SaveChangesAsync(cancellationToken);
        return new PreparedFeedbackTurn(
            durable,
            correction,
            WasRunning: resumeCorrection,
            ExpectedAcceptedTimeSeconds: 0);
    }

    private AgentExecutionContext BuildFeedbackExecutionContext(
        PreparedFeedbackClassification prepared,
        FlowStep step,
        FeedbackClassificationTurnState turn,
        string workspacePath,
        double expectedAcceptedTimeSeconds,
        bool resumeSession,
        bool recoverInterruptedSession) =>
        new(
            prepared.Flow.Id,
            prepared.Flow.Iteration,
            prepared.AccountManager.AgentId,
            prepared.AccountManager.Name,
            prepared.AccountManager.Role,
            step.Model,
            step.ModelEffort,
            turn.Attempt,
            turn.Task,
            prepared.Flow.RepositoryKnowledge,
            prepared.Flow.RepositoryPath,
            workspacePath,
            turn.SessionId,
            prepared.Flow.Outcome,
            "Classify only the current customer review.",
            [],
            [],
            ModelSelectionStrategy:
                prepared.Flow.ModelSelectionStrategy,
            ExpectedAcceptedTimeSeconds:
                expectedAcceptedTimeSeconds,
            ResumeSession: resumeSession,
            RecoverInterruptedSession:
                recoverInterruptedSession,
            Progress: progress =>
                RecordFeedbackProgressAsync(
                        step.Id,
                        progress,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult(),
            InvocationStartedAt: step.StartedAt,
            FlowStepId: step.Id,
            ContractVersion: prepared.Flow.ContractVersion,
            InvocationKind:
                ExecutionInvocationKind.ReviewClassification,
            PlanStepKey: step.PlanStepKey,
            FlowKind: prepared.Flow.Kind);

    private static string SerializeFeedbackTurn(
        FeedbackClassificationTurnState state) =>
        JsonSerializer.Serialize(state);

    private static FeedbackClassificationTurnState
        DeserializeFeedbackTurn(string json)
    {
        try
        {
            return JsonSerializer
                       .Deserialize<FeedbackClassificationTurnState>(
                           json,
                           new JsonSerializerOptions
                           {
                               PropertyNameCaseInsensitive = false,
                               UnmappedMemberHandling =
                                   System.Text.Json.Serialization
                                       .JsonUnmappedMemberHandling
                                       .Disallow
                           })
                   ?? throw new InvalidOperationException(
                       "The persisted review-classification turn state is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The persisted review-classification turn state is invalid.",
                exception);
        }
    }

    private static void ValidateFeedbackTurn(
        FlowStep step,
        FeedbackClassificationTurnState state)
    {
        if (state.Version !=
                FeedbackClassificationTurnState.CurrentVersion ||
            state.Attempt is < 1 or > 2 ||
            state.Kind != (state.Attempt == 1
                ? "Initial"
                : "Correction") ||
            string.IsNullOrWhiteSpace(state.Task) ||
            state.Task.Length > 1_000_000 ||
            state.ValidationErrors is null ||
            state.ValidationErrors.Length > 24 ||
            state.ValidationErrors.Any(error =>
                string.IsNullOrWhiteSpace(error) ||
                error.Length > 500) ||
            state.SessionId == Guid.Empty ||
            state.SessionGeneration < 1 ||
            step.CopilotSessionId is { } persistedSessionId &&
            persistedSessionId != state.SessionId)
        {
            throw new InvalidOperationException(
                "The persisted review-classification turn state failed validation.");
        }
    }

    private static bool EquivalentFeedbackTurn(
        FeedbackClassificationTurnState left,
        FeedbackClassificationTurnState right) =>
        left.Version == right.Version &&
        left.Attempt == right.Attempt &&
        left.Kind == right.Kind &&
        left.Task == right.Task &&
        left.ValidationErrors.SequenceEqual(
            right.ValidationErrors,
            StringComparer.Ordinal) &&
        left.SessionId == right.SessionId &&
        left.SessionGeneration == right.SessionGeneration &&
        left.PriorOutputSha256 == right.PriorOutputSha256 &&
        left.PriorJournalCompletedAt ==
        right.PriorJournalCompletedAt;

    private static bool IsPriorClassifierCompletion(
        FeedbackClassificationTurnState turn,
        CopilotSessionSnapshot snapshot,
        string output)
    {
        if (turn.Attempt != 2 ||
            string.IsNullOrWhiteSpace(
                turn.PriorOutputSha256) ||
            !string.Equals(
                turn.PriorOutputSha256,
                OutcomeVerificationRules.ComputeSha256(output),
                StringComparison.Ordinal))
        {
            return false;
        }
        return turn.PriorJournalCompletedAt is null ||
               snapshot.CompletedAt is null ||
               snapshot.CompletedAt <=
               turn.PriorJournalCompletedAt;
    }

    private Task<CopilotSessionSnapshot> InspectSessionAsync(
        string copilotHome,
        Guid sessionId,
        CancellationToken cancellationToken) =>
        SessionInspectorOverride is null
            ? _sessionJournal.InspectAsync(
                copilotHome,
                sessionId,
                cancellationToken)
            : SessionInspectorOverride(
                copilotHome,
                sessionId,
                cancellationToken);

    private bool TryStopActiveSession(
        CopilotSessionSnapshot snapshot) =>
        ActiveSessionStopperOverride?.Invoke(snapshot) ??
        _sessionJournal.TryStopActiveSession(snapshot);

    private async Task CleanupRecoveredFeedbackSessionAsync(
        Guid flowId,
        Guid stepId,
        CopilotSessionSnapshot? snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot is null)
        {
            return;
        }
        try
        {
            _manifestStager.CleanupSessionRoot(
                snapshot.CopilotHome,
                snapshot.SessionId);
        }
        catch (Exception exception)
        {
            await using var database =
                await databaseFactory.CreateDbContextAsync(
                    cancellationToken);
            if (!await database.FlowEvents.AnyAsync(
                    item =>
                        item.FlowRunId == flowId &&
                        item.FlowStepId == stepId &&
                        item.Type ==
                        "agent.staged-context-cleanup-failed",
                    cancellationToken))
            {
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flowId,
                    FlowStepId = stepId,
                    Type =
                        "agent.staged-context-cleanup-failed",
                    Message =
                        "The review classification is durable, but its exact session-owned staged context still requires cleanup.",
                    DataJson = JsonSerializer.Serialize(new
                    {
                        Version =
                            "staged-context-cleanup-failure-v1",
                        Error = Clip(
                            exception.GetBaseException().Message,
                            1_000)
                    })
                });
                await database.SaveChangesAsync(
                    cancellationToken);
            }
        }
    }

    private async Task<ReviewFeedbackCoordinationResult>
        ApplyFeedbackClassificationAsync(
            PreparedFeedbackClassification prepared,
            ParsedReviewFeedback parsed,
            CancellationToken cancellationToken)
    {
        var intent = parsed.Document.Intent!.Value;
        if (intent == ReviewIntent.Ambiguous)
        {
            await RecordFeedbackAppliedAsync(
                prepared,
                intent,
                reviewDecision: null,
                cancellationToken);
            return new ReviewFeedbackCoordinationResult(
                await LoadDetailedFlowAsync(
                    prepared.Flow.Id,
                    cancellationToken),
                parsed.Document.CustomerReply,
                ShouldSpeak: true,
                intent,
                Review: null);
        }

        var direct = new DirectReviewRequest
        {
            GateId = prepared.GateId,
            Intent = intent,
            Refinement = intent == ReviewIntent.RequestRefinement
                ? new DirectReviewRefinement
                {
                    Goal = parsed.Document.Refinement!.Goal,
                    RequestedChanges =
                        parsed.Document.Refinement.RequestedChanges
                }
                : null
        };
        var reviewed = await ReviewAsync(
            prepared.Flow.Id,
            direct,
            cancellationToken);
        await RecordFeedbackAppliedAsync(
            prepared,
            intent,
            reviewed.Review.Decision,
            cancellationToken);
        return new ReviewFeedbackCoordinationResult(
            reviewed.Flow,
            parsed.Document.CustomerReply,
            ShouldSpeak: true,
            intent,
            reviewed.Review);
    }

    internal static string BuildFeedbackClassificationTask(
        FlowKind flowKind,
        string normalizedOutcomeJson,
        string normalizedFeedback) => $$"""
        Classify the customer's current free-text review of the exact normalized result below.
        Use only the current result and current message. Do not use or request prior dialogue,
        execution logs, tool output, or mutable catalog state.

        Flow kind: {{flowKind}}

        Current normalized flow-outcome-v1 JSON:
        {{normalizedOutcomeJson}}

        Current customer review message:
        {{normalizedFeedback}}

        "Looks good" and equivalent unambiguous approval means Accept. For Advisory only, an
        explicit request such as "let's implement this" means PromoteToDelivery and requires
        ExplicitImplementationAdoption=true. A requested change means RequestRefinement with a
        complete goal and concrete requested changes. If intent is not safe to infer, return
        Ambiguous with one short customer-safe clarification and do not manufacture a decision.
        """;

    private static string BuildFeedbackCorrectionTask(
        IReadOnlyList<string> errors) =>
        "Your previous review-feedback-v1 result was malformed or invalid for the current flow and customer-review gate. " +
        "Return one complete replacement document using the exact sentinels and schema. " +
        "Do not return a patch or quote the earlier output. Validation errors:" +
        Environment.NewLine +
        string.Join(
            Environment.NewLine,
            errors
                .Take(24)
                .Select(error => $"- {Clip(error, 500)}"));

    private async Task CompleteFeedbackClassificationAsync(
        PreparedFeedbackClassification prepared,
        ParsedReviewFeedback parsed,
        IReadOnlyList<AgentExecutionResult> results,
        long durationMilliseconds,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);
        var step = await database.FlowSteps
            .Include(item => item.ToolCalls)
            .SingleAsync(item => item.Id == prepared.Step.Id, cancellationToken);
        var flow = await database.Flows
            .AsSplitQuery()
            .Include(item => item.Events)
            .Include(item => item.Messages)
            .Include(item => item.Steps)
            .Include(item => item.GateRecords)
            .SingleAsync(
                item => item.Id == prepared.Flow.Id,
                cancellationToken);
        ValidateFeedbackClassification(
            flow,
            prepared,
            parsed);
        var existing = flow.Events.SingleOrDefault(item =>
            item.FlowStepId == step.Id &&
            item.Type == FeedbackCompletedEventType);
        if (existing is null)
        {
            step.Status = StepStatus.Completed;
            step.Phase = AgentRunPhase.Succeeded;
            step.OutputSummary = parsed.RawJson;
            step.PushbackReason = string.Empty;
            step.ExecutionAttempts = results.Sum(item =>
                Math.Max(1, item.ExecutionAttempts));
            step.CompletedAt = DateTimeOffset.UtcNow;
            step.DurationMilliseconds = Math.Max(
                1,
                durationMilliseconds);
            AddFeedbackToolCalls(step, results);
            flow.Messages.Add(new FlowMessage
            {
                FlowRunId = flow.Id,
                Role = ConversationRole.AccountManager,
                Content = parsed.Document.CustomerReply,
                IsQuestion =
                    parsed.Document.Intent == ReviewIntent.Ambiguous
            });
            flow.Events.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = step.Id,
                Type = FeedbackCompletedEventType,
                Message =
                    "Account Manager returned a valid bounded review-feedback-v1 classification.",
                DataJson = JsonSerializer.Serialize(new
                {
                    Version = "review-feedback-classification-v1",
                    prepared.GateId,
                    prepared.RequestHash,
                    Intent = parsed.Document.Intent!.Value.ToString(),
                    ContractHash =
                        OutcomeVerificationRules.ComputeSha256(
                            parsed.RawJson)
                })
            });
            flow.UpdatedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync(cancellationToken);
        }
        else if (step.Status != StepStatus.Completed ||
                 !string.Equals(
                     step.OutputSummary,
                     parsed.RawJson,
                     StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The durable review classification completion conflicts with the recovered result.");
        }
        await transaction.CommitAsync(cancellationToken);
        if (observationRecorder is not null)
        {
            try
            {
                await observationRecorder.RecordCompletionAsync(
                    step.Id,
                    accepted: true,
                    Math.Max(1, durationMilliseconds),
                    Math.Max(1, step.ExecutionAttempts),
                    "review-feedback-classified",
                    cancellationToken);
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(
                    exception,
                    "Review-classification telemetry failed after durable completion for step {StepId}.",
                    step.Id);
            }
        }
    }

    private async Task RecordFeedbackCorrectionAsync(
        PreparedFeedbackClassification prepared,
        IReadOnlyList<string> errors,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        if (await database.FlowEvents.AnyAsync(
                item =>
                    item.FlowStepId == prepared.Step.Id &&
                    item.Type ==
                    "review.feedback-classification-correction",
                cancellationToken))
        {
            return;
        }
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = prepared.Flow.Id,
            FlowStepId = prepared.Step.Id,
            Type = "review.feedback-classification-correction",
            Message =
                "The first classifier result was malformed or invalid for the current flow; one bounded correction turn was requested.",
            DataJson = JsonSerializer.Serialize(new
            {
                Version = "review-feedback-correction-v1",
                prepared.RequestHash,
                Errors = errors
                    .Take(24)
                    .Select(error => Clip(error, 500))
                    .ToArray()
            })
        });
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task FailFeedbackClassificationAsync(
        PreparedFeedbackClassification prepared,
        Exception exception,
        int executionAttempts,
        long durationMilliseconds,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var step = await database.FlowSteps
            .Include(item => item.ToolCalls)
            .SingleAsync(item => item.Id == prepared.Step.Id, cancellationToken);
        if (step.Status == StepStatus.Completed &&
            await database.FlowEvents.AnyAsync(
                item =>
                    item.FlowStepId == step.Id &&
                    item.Type == FeedbackCompletedEventType,
                cancellationToken))
        {
            // Completion is already durable. Telemetry or staged-context cleanup failures
            // after that commit must never downgrade the authoritative classifier result.
            return;
        }
        step.Status = StepStatus.Failed;
        step.Phase = exception is AgentRunException
        {
            FailureKind: AgentRunFailureKind.TimedOut
        }
            ? AgentRunPhase.TimedOut
            : exception is AgentRunException
            {
                FailureKind: AgentRunFailureKind.Stalled
            }
                ? AgentRunPhase.Stalled
                : AgentRunPhase.Failed;
        step.PushbackReason =
            "The review feedback could not be classified safely. The customer-review gate remains unresolved.";
        step.ExecutionAttempts = Math.Max(
            step.ExecutionAttempts,
            executionAttempts);
        step.CompletedAt = DateTimeOffset.UtcNow;
        step.DurationMilliseconds = Math.Max(1, durationMilliseconds);
        if (!await database.FlowEvents.AnyAsync(
                item =>
                    item.FlowStepId == step.Id &&
                    item.Type ==
                    "review.feedback-classification-failed",
                cancellationToken))
        {
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = prepared.Flow.Id,
                FlowStepId = step.Id,
                Type = "review.feedback-classification-failed",
                Message =
                    "The bounded Account Manager classifier failed closed; no review decision was applied.",
                DataJson = JsonSerializer.Serialize(new
                {
                    Version = "review-feedback-failure-v1",
                    prepared.RequestHash,
                    FailureKind = exception is
                        ReviewFeedbackContractException
                            ? "MalformedContract"
                            : exception is
                                ReviewFeedbackFlowValidationException
                                ? "InvalidForCurrentFlow"
                            : exception is AgentRunException runException
                                ? runException.FailureKind.ToString()
                                : "ExecutionFailure"
                })
            });
        }
        await database.SaveChangesAsync(cancellationToken);
        if (observationRecorder is not null)
        {
            await observationRecorder.RecordFailureAsync(
                step.Id,
                exception is AgentRunException runException
                    ? runException.FailureKind
                    : AgentRunFailureKind.InvalidOutput,
                step.DurationMilliseconds,
                Math.Max(1, step.ExecutionAttempts),
                cancellationToken);
        }
    }

    private async Task RecordFeedbackAppliedAsync(
        PreparedFeedbackClassification prepared,
        ReviewIntent intent,
        ReviewDecision? reviewDecision,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        if (await database.FlowEvents.AnyAsync(
                item =>
                    item.FlowStepId == prepared.Step.Id &&
                    item.Type == FeedbackAppliedEventType,
                cancellationToken))
        {
            return;
        }
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = prepared.Flow.Id,
            FlowStepId = prepared.Step.Id,
            Type = FeedbackAppliedEventType,
            Message = intent == ReviewIntent.Ambiguous
                ? "The Account Manager requested clarification; the customer-review gate, iteration, and flow status were left unchanged."
                : "The typed Account Manager classification was routed through the generic ReviewCoordinator.",
            DataJson = JsonSerializer.Serialize(new
            {
                Version = "review-feedback-applied-v1",
                prepared.GateId,
                prepared.RequestHash,
                Intent = intent.ToString(),
                ReviewDecision = reviewDecision?.ToString()
            })
        });
        await database.SaveChangesAsync(cancellationToken);
    }

    private static void ValidateFeedbackClassification(
        FlowRun flow,
        PreparedFeedbackClassification prepared,
        ParsedReviewFeedback parsed)
    {
        var errors = new List<string>();
        if (!string.Equals(
                flow.ContractVersion,
                "studio-v2",
                StringComparison.Ordinal))
        {
            errors.Add("Review classification is valid only for a studio-v2 flow.");
        }

        var gate = flow.GateRecords.SingleOrDefault(item =>
            item.Id == prepared.GateId);
        if (gate is null ||
            gate.ActionType != HandoffActionType.CustomerReview)
        {
            errors.Add(
                "The selected customer-review gate is stale or does not belong to this flow.");
        }

        var intent = parsed.Document.Intent!.Value;
        var refinement = intent == ReviewIntent.RequestRefinement
            ? new DirectReviewRefinement
            {
                Goal = parsed.Document.Refinement!.Goal,
                RequestedChanges =
                    parsed.Document.Refinement.RequestedChanges
            }
            : null;
        if (gate is { Resolved: true })
        {
            try
            {
                EnsureIdempotentDecision(
                    flow,
                    gate,
                    intent,
                    refinement is null
                        ? null
                        : NormalizeRefinement(refinement));
            }
            catch (InvalidOperationException exception)
            {
                errors.Add(exception.Message);
            }
            catch (ArgumentException exception)
            {
                errors.Add(exception.Message);
            }
        }
        else
        {
            if (flow.Status != FlowStatus.WaitingForFeedback)
            {
                errors.Add(
                    "The flow is no longer waiting for the selected customer review.");
            }
            if (flow.Iteration != prepared.Step.Iteration)
            {
                errors.Add(
                    "The review classification does not belong to the current flow iteration.");
            }

            var currentStepIds = flow.Steps
                .Where(step => step.Iteration == flow.Iteration)
                .Select(step => step.Id)
                .ToHashSet();
            var openReviews = flow.GateRecords
                .Where(item =>
                    item.ActionType == HandoffActionType.CustomerReview &&
                    !item.Resolved &&
                    currentStepIds.Contains(item.FlowStepId))
                .ToArray();
            if (openReviews.Length != 1)
            {
                errors.Add(
                    "The current iteration must have exactly one unresolved customer-review gate.");
            }
            if (openReviews.SingleOrDefault()?.Id != prepared.GateId)
            {
                errors.Add(
                    "The selected customer-review gate is stale or no longer open.");
            }
            if (gate is not null)
            {
                var owner = flow.Steps.SingleOrDefault(step =>
                    step.Id == gate.FlowStepId);
                if (owner is null ||
                    !owner.IsOutcomeOwner ||
                    owner.Status != StepStatus.Completed ||
                    owner.Iteration != flow.Iteration ||
                    !string.Equals(
                        owner.PlanStepKey,
                        flow.OutcomeOwnerPlanStepKey,
                        StringComparison.Ordinal))
                {
                    errors.Add(
                        "The customer-review gate is not attached to the current completed outcome owner.");
                }
            }
        }

        switch (intent)
        {
            case ReviewIntent.Accept:
                if (flow.Kind == FlowKind.Delivery)
                {
                    try
                    {
                        _ = OutcomeTypeRules.RequireDelivery(
                            flow.Outcome,
                            nameof(flow.Outcome));
                    }
                    catch (ArgumentException exception)
                    {
                        errors.Add(exception.Message);
                    }
                }
                break;
            case ReviewIntent.RequestRefinement:
                try
                {
                    _ = NormalizeRefinement(refinement);
                }
                catch (ArgumentException exception)
                {
                    errors.Add(exception.Message);
                }
                break;
            case ReviewIntent.PromoteToDelivery:
                if (flow.Kind != FlowKind.Advisory)
                {
                    errors.Add(
                        "PromoteToDelivery is valid only for an Advisory flow.");
                }
                else if (gate is { Resolved: false })
                {
                    try
                    {
                        _ = ValidatePromotion(flow, prepared.GateId);
                    }
                    catch (InvalidOperationException exception)
                    {
                        errors.Add(exception.Message);
                    }
                }
                break;
            case ReviewIntent.Ambiguous:
                break;
            default:
                errors.Add("The review intent is not supported.");
                break;
        }

        if (errors.Count > 0)
        {
            throw new ReviewFeedbackFlowValidationException(
                errors.Distinct(StringComparer.Ordinal).ToArray());
        }
    }

    private async Task ValidateStoredFeedbackClassificationAsync(
        PreparedFeedbackClassification prepared,
        ParsedReviewFeedback parsed,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var flow = await database.Flows
            .AsNoTracking()
            .AsSplitQuery()
            .Include(item => item.Steps)
            .Include(item => item.Events)
            .Include(item => item.GateRecords)
            .SingleAsync(
                item => item.Id == prepared.Flow.Id,
                cancellationToken);
        ValidateFeedbackClassification(
            flow,
            prepared,
            parsed);
    }

    private async Task RecordFeedbackRecoveryFailureAsync(
        FeedbackRecoveryCandidate candidate,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        if (await database.FlowEvents.AnyAsync(
                item =>
                    item.FlowStepId == candidate.StepId &&
                    item.Type ==
                    "review.feedback-classification-recovery-failed",
                cancellationToken))
        {
            return;
        }
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = candidate.FlowId,
            FlowStepId = candidate.StepId,
            Type = "review.feedback-classification-recovery-failed",
            Message =
                "Restart reconciliation could not safely resume the durable review classification; the customer-review gate remains unresolved.",
            DataJson = JsonSerializer.Serialize(new
            {
                Version = "review-feedback-recovery-v1",
                Error = Clip(
                    exception.GetBaseException().Message,
                    4_000)
            })
        });
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordFeedbackProgressAsync(
        Guid stepId,
        AgentRunProgress progress,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var step = await database.FlowSteps.SingleOrDefaultAsync(
            item => item.Id == stepId,
            cancellationToken);
        if (step is null)
        {
            return;
        }
        step.Phase = progress.Phase;
        if (progress.ExecutionPrompt is not null)
        {
            step.ExecutionPrompt = progress.ExecutionPrompt;
        }
        if (progress.CopilotSessionId is not null)
        {
            step.CopilotSessionId = progress.CopilotSessionId;
            step.CopilotSessionHome =
                progress.CopilotSessionHome ?? string.Empty;
        }
        await database.SaveChangesAsync(cancellationToken);
    }

    private static void AddFeedbackToolCalls(
        FlowStep step,
        IEnumerable<AgentExecutionResult> results)
    {
        foreach (var toolCall in results.SelectMany(item => item.ToolCalls))
        {
            step.ToolCalls.Add(new AgentToolCall
            {
                FlowStepId = step.Id,
                ToolName = toolCall.ToolName,
                ArgumentsSummary = toolCall.ArgumentsSummary,
                Succeeded = toolCall.Succeeded,
                ToolType = toolCall.ToolType,
                NormalizedCommand = toolCall.NormalizedCommand,
                NormalizedArguments = toolCall.NormalizedArguments,
                WorkingDirectory = toolCall.WorkingDirectory,
                ExitCode = toolCall.ExitCode,
                ResultDigest = toolCall.ResultDigest,
                ResultSummary = toolCall.ResultSummary
            });
        }
    }

    private static ParsedReviewFeedback ParseStoredFeedback(
        FlowStep step)
    {
        if (step.Status != StepStatus.Completed ||
            string.IsNullOrWhiteSpace(step.OutputSummary))
        {
            throw new InvalidOperationException(
                "The durable review classifier has no completed result.");
        }
        return ReviewFeedbackParser.ParseJson(step.OutputSummary);
    }

    private static void EnsureFeedbackClassificationStep(
        FlowStep step,
        FeedbackRequestIdentity request)
    {
        var expectedKey =
            FeedbackClassificationPlanStepPrefix +
            request.RequestHash["sha256:".Length..];
        if (step.FlowRunId == Guid.Empty ||
            step.Iteration != request.ReviewIteration ||
            step.InvocationKind !=
            ExecutionInvocationKind.ReviewClassification ||
            step.PlanStage != PlanStage.BeforeReview ||
            !string.Equals(
                step.PlanStepKey,
                expectedKey,
                StringComparison.Ordinal) ||
            step.PermissionProfile !=
            ExecutionPermissionProfile.ReadOnlySource)
        {
            throw new InvalidOperationException(
                "The durable review classifier step does not match its host-owned lifecycle identity.");
        }
    }

    private static string ComputeFeedbackRequestHash(
        Guid flowId,
        int iteration,
        Guid gateId,
        string feedback) =>
        OutcomeVerificationRules.ComputeSha256(
            JsonSerializer.Serialize(new
            {
                Version = "review-feedback-request-v1",
                FlowId = flowId,
                Iteration = iteration,
                GateId = gateId,
                Message = feedback
            }));

    private static string ComputeFeedbackHash(string feedback) =>
        OutcomeVerificationRules.ComputeSha256(
            JsonSerializer.Serialize(new
            {
                Version = "review-feedback-message-v1",
                Message = feedback
            }));

    private static void MarkFeedbackClassificationSuperseded(
        FlowRun flow,
        FlowStep step,
        FeedbackRequestIdentity request,
        string reason)
    {
        if (flow.Events.Any(item =>
                item.FlowStepId == step.Id &&
                item.Type == FeedbackSupersededEventType))
        {
            return;
        }
        if (step.Status is StepStatus.Pending or StepStatus.Running)
        {
            step.Status = StepStatus.Skipped;
            step.Phase = AgentRunPhase.CanceledByReconciliation;
            step.CompletedAt ??= DateTimeOffset.UtcNow;
            step.PushbackReason =
                "The exact customer-review request is no longer current; stale feedback was not replayed.";
        }
        flow.Events.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = step.Id,
            Type = FeedbackSupersededEventType,
            Message =
                "Restart reconciliation completed the stale review classification without applying it to another customer-review gate.",
            DataJson = JsonSerializer.Serialize(new
            {
                Version = "review-feedback-superseded-v1",
                request.GateId,
                request.ReviewIteration,
                request.RequestHash,
                request.FeedbackHash,
                CurrentIteration = flow.Iteration,
                Reason = reason
            })
        });
        flow.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string SerializeFeedbackRequestIdentity(
        Guid flowStepId,
        Guid gateId,
        int reviewIteration,
        string requestHash,
        string feedbackHash) =>
        JsonSerializer.Serialize(new
        {
            Version = "review-feedback-request-v1",
            FlowStepId = flowStepId,
            GateId = gateId,
            ReviewIteration = reviewIteration,
            RequestHash = requestHash,
            FeedbackHash = feedbackHash
        });

    private static FeedbackRequestIdentity? TryReadFeedbackRequest(
        FlowEvent flowEvent)
    {
        if (flowEvent.Type != FeedbackRequestEventType ||
            string.IsNullOrWhiteSpace(flowEvent.DataJson))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(flowEvent.DataJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("Version", out var version) ||
                !string.Equals(
                    version.GetString(),
                    "review-feedback-request-v1",
                    StringComparison.Ordinal) ||
                !root.TryGetProperty("FlowStepId", out var flowStepId) ||
                !flowStepId.TryGetGuid(out var parsedFlowStepId) ||
                !root.TryGetProperty("GateId", out var gateId) ||
                !gateId.TryGetGuid(out var parsedGateId) ||
                !root.TryGetProperty(
                    "ReviewIteration",
                    out var reviewIteration) ||
                !reviewIteration.TryGetInt32(
                    out var parsedReviewIteration) ||
                parsedReviewIteration < 1 ||
                !root.TryGetProperty("RequestHash", out var requestHash) ||
                !root.TryGetProperty("FeedbackHash", out var feedbackHash) ||
                requestHash.ValueKind != JsonValueKind.String ||
                feedbackHash.ValueKind != JsonValueKind.String ||
                !OutcomeVerificationRules.IsSha256(
                    requestHash.GetString() ?? string.Empty) ||
                !OutcomeVerificationRules.IsSha256(
                    feedbackHash.GetString() ?? string.Empty))
            {
                throw new InvalidOperationException(
                    "The durable review feedback request identity is invalid.");
            }
            return new FeedbackRequestIdentity(
                parsedFlowStepId,
                parsedGateId,
                parsedReviewIteration,
                requestHash.GetString()!,
                feedbackHash.GetString()!);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The durable review feedback request identity is invalid.",
                exception);
        }
    }

    private static FeedbackRequestIdentity? FindFeedbackRequestByMessageHash(
        FlowRun flow,
        string feedbackHash) =>
        flow.Events
            .Where(item => item.Type == FeedbackRequestEventType)
            .OrderByDescending(item => item.CreatedAt)
            .Select(TryReadFeedbackRequest)
            .OfType<FeedbackRequestIdentity>()
            .FirstOrDefault(item =>
                string.Equals(
                    item.FeedbackHash,
                    feedbackHash,
                    StringComparison.Ordinal));

    private BootstrapTaskProfileFactory RequireFeedbackProfileFactory() =>
        profileFactory ?? throw new InvalidOperationException(
            "Review feedback classification is not configured.");

    private IModelRouter RequireFeedbackModelRouter() =>
        modelRouter ?? throw new InvalidOperationException(
            "Review feedback classification is not configured.");

    private IAgentRunner RequireFeedbackAgentRunner() =>
        agentRunner ?? throw new InvalidOperationException(
            "Review feedback classification is not configured.");

    private IWorkspaceManager RequireFeedbackWorkspaceManager() =>
        workspaceManager ?? throw new InvalidOperationException(
            "Review feedback classification is not configured.");

    private IReviewedCandidateService
        RequireFeedbackReviewedCandidateService() =>
        reviewedCandidateService ?? throw new InvalidOperationException(
            "Delivery review candidate verification is not configured.");

    internal static bool IsStudioPublicationStep(FlowRun flow, FlowStep step) =>
        flow.ContractVersion == "studio-v2" &&
        flow.Kind == FlowKind.Delivery &&
        step.PlanStage == PlanStage.AfterApproval &&
        !string.IsNullOrWhiteSpace(flow.PublicationPlanStepKey) &&
        string.Equals(
            flow.PublicationPlanStepKey,
            step.PlanStepKey,
            StringComparison.Ordinal) &&
        HasPublishOnlyDuty(step.PlanDutiesJson);

    internal static bool HasAcceptedCustomerReview(FlowRun flow)
    {
        var currentStepIds = flow.Steps
            .Where(step => step.Iteration == flow.Iteration)
            .Select(step => step.Id)
            .ToHashSet();
        return flow.GateRecords.Any(gate =>
            gate.ActionType == HandoffActionType.CustomerReview &&
            gate.Resolved &&
            gate.Approved == true &&
            gate.ReviewDecision == ReviewDecision.Accepted &&
            currentStepIds.Contains(gate.FlowStepId));
    }

    internal static ReviewPublicationStatus GetPublicationStatus(FlowRun flow)
    {
        if (flow.ContractVersion != "studio-v2" ||
            flow.Kind != FlowKind.Delivery)
        {
            return ReviewPublicationStatus.NotApplicable;
        }
        if (!HasAcceptedCustomerReview(flow))
        {
            return ReviewPublicationStatus.AwaitingApproval;
        }

        var latestAttempt = flow.Steps
            .Where(step =>
                step.Iteration == flow.Iteration &&
                IsStudioPublicationStep(flow, step))
            .OrderBy(step => step.Sequence)
            .ThenBy(step => step.Attempt)
            .ThenBy(step => step.StartedAt)
            .LastOrDefault();
        return latestAttempt?.Status switch
        {
            StepStatus.Completed => ReviewPublicationStatus.Published,
            StepStatus.Running => ReviewPublicationStatus.Running,
            StepStatus.Failed => ReviewPublicationStatus.Failed,
            _ => ReviewPublicationStatus.Queued
        };
    }

    private static void EnsureStudioReviewFlow(FlowRun flow)
    {
        if (!string.Equals(
                flow.ContractVersion,
                "studio-v2",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Generic customer review is available only for studio-v2 flows.");
        }
    }

    private static (HandoffGateRecord Gate, FlowOutcomeDocument Outcome)
        ValidatePromotion(FlowRun flow, Guid gateId)
    {
        EnsureStudioReviewFlow(flow);
        if (flow.Kind != FlowKind.Advisory)
        {
            throw new InvalidOperationException(
                "Only an Advisory flow can be promoted to Delivery.");
        }
        var gate = flow.GateRecords.SingleOrDefault(item => item.Id == gateId)
            ?? throw new InvalidOperationException(
                "The customer-review gate is stale or does not belong to this flow.");
        if (gate.ActionType != HandoffActionType.CustomerReview)
        {
            throw new InvalidOperationException(
                "The selected gate is not a generic customer review.");
        }
        var owner = flow.Steps.SingleOrDefault(step =>
            step.Id == gate.FlowStepId &&
            step.Iteration == flow.Iteration &&
            step.IsOutcomeOwner &&
            step.Status == StepStatus.Completed &&
            string.Equals(
                step.PlanStepKey,
                flow.OutcomeOwnerPlanStepKey,
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "Advisory promotion requires the current completed outcome-owner review.");
        var currentSteps = flow.Steps
            .Where(step => step.Iteration == flow.Iteration)
            .ToArray();
        if (WorkflowEngine.FindUnresolvedFailure(currentSteps) is not null ||
            WorkflowEngine.FindUnresolvedPushback(currentSteps) is not null)
        {
            throw new InvalidOperationException(
                "Advisory promotion is unavailable while the reviewed iteration has an unresolved failed or pushback step.");
        }
        if (flow.GateRecords.Any(item =>
                item.Id != gate.Id &&
                item.ActionType == HandoffActionType.CustomerReview &&
                !item.Resolved &&
                flow.Steps.Any(step =>
                    step.Id == item.FlowStepId &&
                    step.Iteration == flow.Iteration)))
        {
            throw new InvalidOperationException(
                "Advisory promotion cannot bypass another unresolved current review.");
        }

        if (!gate.Resolved)
        {
            if (flow.Status != FlowStatus.WaitingForFeedback)
            {
                throw new InvalidOperationException(
                    "The Advisory flow is not waiting for the selected customer review.");
            }
        }
        else if (gate.Approved != true ||
                 gate.ReviewDecision is not (
                     ReviewDecision.Accepted or
                     ReviewDecision.PromotedToDelivery) ||
                 flow.Status != FlowStatus.Approved)
        {
            throw new InvalidOperationException(
                "The resolved Advisory review is not compatible with promotion.");
        }

        _ = owner;
        if (string.IsNullOrWhiteSpace(flow.OutcomeContractJson))
        {
            throw new InvalidOperationException(
                $"Advisory promotion requires a valid {FlowOutcomeParser.Version} result.");
        }
        return (
            gate,
            FlowOutcomeParser.ParseJson(flow.OutcomeContractJson).Document);
    }

    private static void EnsureIdempotentDecision(
        FlowRun flow,
        HandoffGateRecord gate,
        ReviewIntent intent,
        DirectReviewRefinement? refinement)
    {
        var expected = intent switch
        {
            ReviewIntent.Accept => ReviewDecision.Accepted,
            ReviewIntent.RequestRefinement => ReviewDecision.RefinementRequested,
            ReviewIntent.PromoteToDelivery => ReviewDecision.PromotedToDelivery,
            _ => (ReviewDecision?)null
        };
        if (expected is null || gate.ReviewDecision != expected)
        {
            throw new InvalidOperationException(
                "The customer review was already resolved with a different decision.");
        }
        if (expected != ReviewDecision.RefinementRequested)
        {
            return;
        }
        var normalized = refinement
            ?? throw new InvalidOperationException(
                "The replayed refinement decision has no normalized request.");
        var events = flow.Events
            .Where(item =>
                item.FlowStepId == gate.FlowStepId &&
                item.Type == "flow.review-refinement-requested")
            .ToArray();
        if (events.Length != 1 ||
            string.IsNullOrWhiteSpace(events[0].DataJson))
        {
            throw new InvalidOperationException(
                "The recorded refinement decision has no unique durable request identity.");
        }

        string recordedHash;
        try
        {
            using var document = JsonDocument.Parse(events[0].DataJson!);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("Version", out var version) ||
                !string.Equals(
                    version.GetString(),
                    "direct-review-v1",
                    StringComparison.Ordinal) ||
                !root.TryGetProperty("RequestedChanges", out var changes) ||
                changes.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    "The durable refinement request is invalid.");
            }
            if (root.TryGetProperty("RequestHash", out var requestHash) &&
                requestHash.ValueKind == JsonValueKind.String)
            {
                recordedHash = requestHash.GetString() ?? string.Empty;
            }
            else
            {
                var recorded = new DirectReviewRefinement
                {
                    Goal = root.TryGetProperty("Goal", out var goal) &&
                           goal.ValueKind == JsonValueKind.String
                        ? goal.GetString()
                        : null,
                    RequestedChanges = changes
                        .EnumerateArray()
                        .Select(item => item.GetString() ?? string.Empty)
                        .ToArray()
                };
                recordedHash = ComputeRefinementRequestHash(
                    NormalizeRefinement(recorded));
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The durable refinement request is invalid.",
                exception);
        }

        if (!string.Equals(
                recordedHash,
                ComputeRefinementRequestHash(normalized),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The customer review was already resolved with a materially different refinement request.");
        }
    }

    private static string ComputeRefinementRequestHash(
        DirectReviewRefinement refinement) =>
        AiHarnessDemo.Core.Verification.OutcomeVerificationRules.ComputeSha256(
            JsonSerializer.Serialize(new
            {
                Version = "direct-review-request-v1",
                Goal = refinement.Goal,
                RequestedChanges = refinement.RequestedChanges
            }));

    private static DirectReviewRefinement NormalizeRefinement(
        DirectReviewRefinement? refinement)
    {
        if (refinement?.RequestedChanges is null)
        {
            throw new ArgumentException(
                "RequestRefinement requires requested changes.");
        }
        var goal = string.IsNullOrWhiteSpace(refinement.Goal)
            ? null
            : NormalizeText(
                refinement.Goal,
                ReviewFeedbackParser.MaximumGoalCharacters,
                "Refinement goal");
        if (refinement.RequestedChanges.Count is
            < 1 or > ReviewFeedbackParser.MaximumRequestedChanges)
        {
            throw new ArgumentException(
                $"Requested changes must contain 1-{ReviewFeedbackParser.MaximumRequestedChanges} entries.");
        }
        var changes = refinement.RequestedChanges
            .Select(change => NormalizeText(
                change,
                ReviewFeedbackParser.MaximumRequestedChangeCharacters,
                "Requested change"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (changes.Sum(change => change.Length) >
            MaximumTotalRequestedChangeCharacters)
        {
            throw new ArgumentException(
                $"Requested changes must contain at most {MaximumTotalRequestedChangeCharacters} characters in total.");
        }
        return new DirectReviewRefinement
        {
            Goal = goal,
            RequestedChanges = changes
        };
    }

    private static string NormalizeText(
        string? value,
        int maximumCharacters,
        string label)
    {
        var normalized = value?
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim() ?? string.Empty;
        if (normalized.Length is 0 || normalized.Length > maximumCharacters ||
            normalized.Any(character =>
                char.IsControl(character) &&
                character is not '\n' and not '\t'))
        {
            throw new ArgumentException(
                $"{label} must contain 1-{maximumCharacters} safe, trimmed characters.");
        }
        return normalized;
    }

    private static void ApplyRefinement(
        FlowRun flow,
        FlowStep reviewedStep,
        DirectReviewRefinement refinement,
        DateTimeOffset now,
        HarnessDbContext database,
        FlowLifecycleCoordinator lifecycle)
    {
        var reviewedIteration = flow.Iteration;
        var requestedChanges = refinement.RequestedChanges
            ?? throw new InvalidOperationException(
                "Normalized requested changes are missing.");
        var seedLines = new List<string>
        {
            $"Customer refinement for iteration {reviewedIteration}:"
        };
        if (!string.IsNullOrWhiteSpace(refinement.Goal))
        {
            seedLines.Add($"Goal: {refinement.Goal}");
        }
        seedLines.Add("Requested changes:");
        seedLines.AddRange(requestedChanges.Select(change => $"- {change}"));
        var seed = string.Join(Environment.NewLine, seedLines);
        var reviewedOutcome = FlowOutcomeParser.ParseJson(
            flow.OutcomeContractJson).RawJson;

        database.FlowMessages.Add(new FlowMessage
        {
            FlowRunId = flow.Id,
            Role = ConversationRole.Customer,
            Content = seed
        });
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = reviewedStep.Id,
            Type = "flow.review-refinement-requested",
            Message =
                $"Customer requested refinement; iteration {reviewedIteration + 1} will be replanned from the retained snapshot.",
            DataJson = JsonSerializer.Serialize(new
            {
                Version = "direct-review-v1",
                Goal = refinement.Goal,
                RequestedChanges = requestedChanges,
                RequestHash = ComputeRefinementRequestHash(refinement)
            })
        });

        flow.ConsolidatedRequest =
            $"{seed}{Environment.NewLine}{Environment.NewLine}" +
            $"Current reviewed flow-outcome-v1 JSON:{Environment.NewLine}{reviewedOutcome}" +
            $"{Environment.NewLine}{Environment.NewLine}" +
            $"Previous confirmed brief:{Environment.NewLine}" +
            flow.ConsolidatedRequest.TrimEnd();
        flow.Iteration++;
        lifecycle.Transition(flow, FlowStatus.Reworking);
        flow.OutcomeOwnerPlanStepKey = null;
        flow.PublicationPlanStepKey = null;
        flow.OutcomeContractJson = string.Empty;
        flow.OutcomeUrl = string.Empty;
        flow.OutcomeLabel = string.Empty;
        flow.FailureReason = string.Empty;
        flow.CompletedAt = null;
        flow.UpdatedAt = now;
    }

    internal static FlowStep MaterializePublicationStep(
        FlowRun flow,
        FlowStep reviewedStep,
        WorkflowDefinition effectiveWorkflow,
        PermissionProfileResolver permissionResolver)
    {
        ArgumentNullException.ThrowIfNull(effectiveWorkflow);
        ArgumentNullException.ThrowIfNull(permissionResolver);
        if (flow.Kind != FlowKind.Delivery)
        {
            throw new InvalidOperationException(
                "Only Delivery acceptance can materialize publication.");
        }
        _ = OutcomeTypeRules.RequireDelivery(
            flow.Outcome,
            nameof(flow.Outcome));
        var document = flow.PlanDocuments.SingleOrDefault(
                           item => item.Iteration == flow.Iteration)
                       ?? throw new InvalidOperationException(
                           "Delivery acceptance requires the stored validated flow plan.");
        if (!string.Equals(
                document.Version,
                TeamPlanParser.Version,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The stored Delivery plan version '{document.Version}' is unsupported.");
        }
        var parsed = TeamPlanParser.ParseJson(document.RawJson).Document;
        if (!string.Equals(
                parsed.Version,
                TeamPlanParser.Version,
                StringComparison.Ordinal) ||
            parsed.Disposition != TeamPlanDisposition.Planned ||
            parsed.Steps is null)
        {
            throw new InvalidOperationException(
                "Delivery acceptance requires a planned team-plan-v1 document.");
        }
        var publicationSteps = parsed.Steps.Where(step =>
                step.Stage == PlanStage.AfterApproval &&
                step.Duties?.Count == 1 &&
                step.Duties[0] == PlanDuty.Publish)
            .ToList();
        if (publicationSteps.Count != 1 ||
            parsed.Steps.Count(step =>
                step.Duties?.Contains(PlanDuty.Publish) == true) != 1)
        {
            throw new InvalidOperationException(
                "The stored Delivery plan must contain exactly one AfterApproval Publish-only step.");
        }
        var planned = publicationSteps[0];
        if (string.IsNullOrWhiteSpace(flow.PublicationPlanStepKey) ||
            !string.Equals(
                flow.PublicationPlanStepKey,
                planned.Id,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The durable publication plan-step identity does not match the stored plan.");
        }
        var snapshot = flow.AgentSnapshots.SingleOrDefault(item =>
                           string.Equals(
                               item.AgentId,
                               planned.AgentId,
                               StringComparison.Ordinal) &&
                           item.EnabledAtSnapshot)
                       ?? throw new InvalidOperationException(
                           $"Publication agent '{planned.AgentId}' is absent from the immutable enabled snapshot.");
        if (reviewedStep.Iteration != flow.Iteration ||
            !reviewedStep.IsOutcomeOwner ||
            reviewedStep.Status != StepStatus.Completed ||
            !string.Equals(
                reviewedStep.PlanStepKey,
                flow.OutcomeOwnerPlanStepKey,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Delivery acceptance requires the completed planned outcome owner.");
        }
        var owner = reviewedStep;
        _ = ReviewedCandidateLedger.Read(flow, owner.Id);

        var sameKey = flow.Steps
            .Where(step =>
                step.Iteration == flow.Iteration &&
                string.Equals(
                    step.PlanStepKey,
                    planned.Id,
                    StringComparison.Ordinal))
            .ToList();
        if (sameKey.Any(step => !IsStudioPublicationStep(flow, step)))
        {
            throw new InvalidOperationException(
                "A durable step already uses the publication plan key with a different authority shape.");
        }
        var roots = sameKey
            .Where(step => step.RetryOfStepId is null)
            .OrderBy(step => step.Sequence)
            .ToList();
        if (roots.Count > 1)
        {
            throw new InvalidOperationException(
                "The durable publication plan has duplicate semantic roots.");
        }
        var existing = roots.SingleOrDefault();
        if (existing is not null)
        {
            EnsurePublicationPermission(
                flow,
                reviewedStep,
                planned,
                existing,
                effectiveWorkflow,
                permissionResolver);
            _ = RequireRemotePublicationAuthority(
                flow,
                existing);
            return existing;
        }

        var publication = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Sequence = flow.Steps
                .Where(step => step.Iteration == flow.Iteration)
                .Select(step => step.Sequence)
                .DefaultIfEmpty()
                .Max() + 10,
            AgentId = snapshot.AgentId,
            AgentName = snapshot.Name,
            AgentRole = snapshot.Role,
            Label = planned.Assignment,
            PlanStepKey = planned.Id,
            PlanDutiesJson = JsonSerializer.Serialize(
                new[] { PlanDuty.Publish.ToString() }),
            PlanStage = PlanStage.AfterApproval,
            InvocationKind = ExecutionInvocationKind.Publication,
            IsOutcomeOwner = false,
            PermissionProfile = ExecutionPermissionProfile.Publish,
            WorkflowRevision = effectiveWorkflow.Revision,
            Status = StepStatus.Pending,
            Phase = AgentRunPhase.PreparingWorkspace,
            InputSummary =
                $"{planned.Assignment}{Environment.NewLine}{Environment.NewLine}" +
                $"Selection rationale: {planned.Justification}{Environment.NewLine}{Environment.NewLine}" +
                WorkflowEngine.HostControlledPublicationAssignment,
            DependsOnStepId = owner.Id
        };
        publication.StableSemanticRootId = publication.Id;
        EnsurePublicationPermission(
            flow,
            reviewedStep,
            planned,
            publication,
            effectiveWorkflow,
            permissionResolver);
        _ = RequireRemotePublicationAuthority(
            flow,
            publication);
        flow.Steps.Add(publication);

        var profile = flow.TaskProfiles.SingleOrDefault(item =>
            item.Iteration == flow.Iteration &&
            string.Equals(item.PlanStepKey, planned.Id, StringComparison.Ordinal) &&
            string.Equals(item.AgentId, planned.AgentId, StringComparison.Ordinal));
        if (profile is null)
        {
            throw new InvalidOperationException(
                "The accepted publication plan has no durable task profile.");
        }
        if (profile.FlowStepId is not null &&
            profile.FlowStepId != publication.Id)
        {
            throw new InvalidOperationException(
                "The publication task profile is already bound to another step.");
        }
        profile.FlowStepId = publication.Id;
        return publication;
    }

    internal static bool EnsurePublicationPermission(
        FlowRun flow,
        FlowStep reviewedStep,
        TeamPlanStep planned,
        FlowStep publication,
        WorkflowDefinition effectiveWorkflow,
        PermissionProfileResolver permissionResolver)
    {
        publication.InvocationKind =
            ExecutionInvocationKind.Publication;
        var request = new PermissionResolutionRequest(
            flow.Kind,
            ExecutionInvocationKind.Publication,
            PlanStage.AfterApproval,
            System.Collections.Immutable.ImmutableArray.Create(
                PlanDuty.Publish),
            DurableReviewDecision: ReviewDecision.Accepted,
            DurableApproval: true,
            IsOnlyPlannedPublishStep: true,
            ContractVersion: flow.ContractVersion,
            LegacyPublicationAuthorized: false,
            IsGovernedOutcomeVerification: false);
        var current = permissionResolver.Resolve(
            request,
            PermissionProfileResolver.FromWorkflow(
                effectiveWorkflow));
        if (!string.IsNullOrWhiteSpace(publication.EffectivePermissionJson))
        {
            EffectiveExecutionPermission persisted;
            try
            {
                persisted =
                    JsonSerializer.Deserialize<EffectiveExecutionPermission>(
                        publication.EffectivePermissionJson)
                    ?? throw new InvalidOperationException(
                        "The publication attempt has an empty effective permission document.");
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    "The publication attempt has an invalid effective permission document.",
                    exception);
            }
            PermissionProfileResolver.ValidatePersisted(
                persisted,
                publication.PermissionProfile);
            var hardened = PermissionProfileResolver.Tighten(
                persisted,
                current);
            PermissionProfileResolver.ValidatePersisted(
                hardened,
                hardened.Profile,
                request);
            var changed = !PermissionProfileResolver.Equivalent(
                persisted,
                hardened);
            var remotePublicationAllowed =
                publication.RemotePublicationAllowed &&
                PermissionProfileResolver.GrantsRemotePublication(
                    hardened);
            changed |= publication.RemotePublicationAllowed !=
                       remotePublicationAllowed;
            publication.RemotePublicationAllowed =
                remotePublicationAllowed;
            if (changed)
            {
                publication.PermissionProfile = hardened.Profile;
                publication.EffectivePermissionJson =
                    JsonSerializer.Serialize(hardened);
                publication.WorkflowRevision =
                    effectiveWorkflow.Revision;
            }
            return changed;
        }

        var outcomeOwnerRoot = ResolveOutcomeOwnerSemanticRoot(
            flow,
            reviewedStep);
        var snapshots = flow.Events
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.FlowStepId == outcomeOwnerRoot.Id &&
                item.Type == "plan.publication-permission-snapshotted")
            .ToList();
        if (snapshots.Count > 1)
        {
            throw new InvalidOperationException(
                "The accepted plan has duplicate publication permission snapshots.");
        }

        DeferredPermissionSnapshot baseline;
        if (snapshots.SingleOrDefault()?.DataJson is { } dataJson)
        {
            try
            {
                baseline =
                    JsonSerializer.Deserialize<DeferredPermissionSnapshot>(
                        dataJson)
                    ?? throw new InvalidOperationException(
                        "The planned publication permission snapshot is empty.");
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    "The planned publication permission snapshot is invalid.",
                    exception);
            }
            if (!string.Equals(
                    baseline.Version,
                    DeferredPermissionSnapshot.CurrentVersion,
                    StringComparison.Ordinal) ||
                baseline.Iteration != flow.Iteration ||
                !string.Equals(
                    baseline.PlanStepKey,
                    planned.Id,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    baseline.PlanStepKey,
                    flow.PublicationPlanStepKey,
                    StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(baseline.WorkflowRevision))
            {
                throw new InvalidOperationException(
                    "The planned publication permission snapshot does not match the accepted plan.");
            }
            PermissionProfileResolver.ValidatePersisted(
                baseline.Permission,
                baseline.Permission.Profile);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(outcomeOwnerRoot.WorkflowRevision) &&
                !string.Equals(
                    outcomeOwnerRoot.WorkflowRevision,
                    effectiveWorkflow.Revision,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The historical publication permission ceiling is unavailable; materialization failed closed.");
            }
            baseline = new DeferredPermissionSnapshot(
                DeferredPermissionSnapshot.CurrentVersion,
                flow.Iteration,
                planned.Id,
                effectiveWorkflow.Revision,
                permissionResolver.Resolve(
                    request,
                    PermissionProfileResolver.FromWorkflow(
                        effectiveWorkflow)));
        }

        var effective = PermissionProfileResolver.Tighten(
            baseline.Permission,
            current);
        PermissionProfileResolver.ValidatePersisted(
            effective,
            effective.Profile,
            request);
        var revision = baseline.WorkflowRevision;
        if (!PermissionProfileResolver.Equivalent(
                baseline.Permission,
                effective))
        {
            revision = effectiveWorkflow.Revision;
        }

        publication.PermissionProfile = effective.Profile;
        publication.EffectivePermissionJson =
            JsonSerializer.Serialize(effective);
        publication.WorkflowRevision = revision;
        publication.RemotePublicationAllowed =
            PermissionProfileResolver.GrantsRemotePublication(
                effective);
        return true;
    }

    internal static EffectiveExecutionPermission
        RequireRemotePublicationAuthority(
            FlowRun flow,
            FlowStep publication)
    {
        if (string.IsNullOrWhiteSpace(
                publication.EffectivePermissionJson))
        {
            throw new InvalidOperationException(
                "The publication attempt has no persisted effective permission document.");
        }

        EffectiveExecutionPermission effective;
        try
        {
            effective =
                JsonSerializer.Deserialize<EffectiveExecutionPermission>(
                    publication.EffectivePermissionJson)
                ?? throw new InvalidOperationException(
                    "The publication attempt has an empty effective permission document.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The publication attempt has an invalid effective permission document.",
                exception);
        }
        PermissionProfileResolver.ValidatePersisted(
            effective,
            publication.PermissionProfile);
        if (publication.PermissionProfile !=
                ExecutionPermissionProfile.Publish ||
            !PermissionProfileResolver.GrantsRemotePublication(
                effective) ||
            !publication.RemotePublicationAllowed)
        {
            throw new PublicationPolicyConflictException(
                flow.Id,
                publication.Id,
                effective.Profile);
        }
        return effective;
    }

    private static FlowStep ResolveOutcomeOwnerSemanticRoot(
        FlowRun flow,
        FlowStep reviewedStep)
    {
        var rootId = reviewedStep.StableSemanticRootId ??
                     reviewedStep.RetryOfStepId ??
                     reviewedStep.Id;
        var root = flow.Steps.SingleOrDefault(step =>
                       step.Id == rootId)
                   ?? throw new InvalidOperationException(
                       "The reviewed outcome owner has no durable semantic root.");
        if (reviewedStep.FlowRunId != flow.Id ||
            root.FlowRunId != flow.Id ||
            reviewedStep.Iteration != flow.Iteration ||
            root.Iteration != flow.Iteration ||
            !reviewedStep.IsOutcomeOwner ||
            !root.IsOutcomeOwner ||
            string.IsNullOrWhiteSpace(flow.OutcomeOwnerPlanStepKey) ||
            !string.Equals(
                reviewedStep.PlanStepKey,
                flow.OutcomeOwnerPlanStepKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                root.PlanStepKey,
                flow.OutcomeOwnerPlanStepKey,
                StringComparison.Ordinal) ||
            root.RetryOfStepId is not null ||
            root.StableSemanticRootId is { } persistedRoot &&
            persistedRoot != root.Id)
        {
            throw new InvalidOperationException(
                "The reviewed outcome owner does not belong to the current planned semantic lineage.");
        }
        if (reviewedStep.StableSemanticRootId is { } stableRoot &&
            stableRoot != root.Id ||
            reviewedStep.StableSemanticRootId is null &&
            reviewedStep.RetryOfStepId is { } retryRoot &&
            retryRoot != root.Id ||
            reviewedStep.RetryOfStepId is { } explicitRetryRoot &&
            explicitRetryRoot != root.Id)
        {
            throw new InvalidOperationException(
                "The reviewed outcome-owner retry does not reference its canonical semantic root.");
        }
        return root;
    }

    private async Task<ReviewCoordinationResult> BuildResultAsync(
        Guid flowId,
        Guid gateId,
        ReviewIntent intent,
        ReviewDecision? decision,
        Guid? publicationStepId,
        Guid? linkedFlowId,
        string message,
        CancellationToken cancellationToken)
    {
        var flow = await LoadDetailedFlowAsync(flowId, cancellationToken);
        var response = new DirectReviewResponse(
            flow.Id,
            gateId,
            intent,
            decision,
            flow.Status,
            flow.Iteration,
            publicationStepId ?? FindPublicationRoot(flow)?.Id,
            linkedFlowId,
            GetPublicationStatus(flow),
            message);
        return new ReviewCoordinationResult(response, flow);
    }

    private async Task<FlowRun> LoadDetailedFlowAsync(
        Guid flowId,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        return await database.Flows
                   .AsSplitQuery()
                   .AsNoTracking()
                   .Include(item => item.Steps)
                   .ThenInclude(step => step.ToolCalls)
                   .Include(item => item.Steps)
                   .ThenInclude(step => step.RoutingDecisions)
                   .ThenInclude(decision => decision.TaskProfile)
                   .Include(item => item.Steps)
                   .ThenInclude(step => step.RoutingDecisions)
                   .ThenInclude(decision => decision.Alternatives)
                   .Include(item => item.Messages)
                   .Include(item => item.Events)
                   .Include(item => item.GateRecords)
                   .Include(item => item.AgentSnapshots)
                   .Include(item => item.PlanDocuments)
                   .Include(item => item.TaskProfiles)
                   .Include(item => item.LinkedFlowRuns)
                   .ThenInclude(item => item.Steps)
                   .Include(item => item.LinkedFlowRuns)
                   .ThenInclude(item => item.GateRecords)
                   .SingleAsync(item => item.Id == flowId, cancellationToken);
    }

    private static async Task<FlowRun> LoadPromotionParentAsync(
        HarnessDbContext database,
        Guid flowId,
        CancellationToken cancellationToken) =>
        await database.Flows
                  .AsSplitQuery()
                  .Include(item => item.Steps)
                  .Include(item => item.GateRecords)
                  .Include(item => item.Events)
                  .Include(item => item.LinkedFlowRuns)
                  .SingleOrDefaultAsync(item => item.Id == flowId, cancellationToken)
              ?? throw new KeyNotFoundException(
                  $"Factory flow '{flowId}' was not found.");

    private static string BuildPromotionTitle(string goal)
    {
        var title = $"Implement {goal.Trim().TrimEnd('.', '!', '?')}";
        return title.Length <= 120 ? title : $"{title[..117]}...";
    }

    private static string Clip(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    private static void EnsurePromotionChild(FlowRun parent, FlowRun child)
    {
        if (child.ParentFlowRunId != parent.Id ||
            child.ParentIteration != parent.Iteration ||
            child.LinkKind != FlowLinkKind.AdvisoryPromotion ||
            child.Kind != FlowKind.Delivery ||
            !string.Equals(
                child.ContractVersion,
                "studio-v2",
                StringComparison.Ordinal) ||
            !string.Equals(
                child.RepositoryPath,
                parent.RepositoryPath,
                StringComparison.Ordinal) ||
            !string.Equals(
                child.RepositoryKnowledge,
                parent.RepositoryKnowledge,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The existing Advisory-promotion successor does not match its immutable parent link.");
        }
    }

    private static FlowStep? FindPublicationRoot(FlowRun flow) =>
        flow.Steps
            .Where(step =>
                step.Iteration == flow.Iteration &&
                step.RetryOfStepId is null &&
                IsStudioPublicationStep(flow, step))
            .OrderBy(step => step.Sequence)
            .FirstOrDefault();

    private static bool HasPublishOnlyDuty(string dutiesJson)
    {
        try
        {
            var duties = JsonSerializer.Deserialize<string[]>(dutiesJson);
            return duties is { Length: 1 } &&
                   string.Equals(
                       duties[0],
                       PlanDuty.Publish.ToString(),
                       StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ApplyPreparedGate(
        HandoffGateRecord prepared,
        HandoffGateRecord tracked)
    {
        tracked.Decision = prepared.Decision;
        tracked.ReviewDecision = prepared.ReviewDecision;
        tracked.TrustLevelAtDecision = prepared.TrustLevelAtDecision;
        tracked.Summary = prepared.Summary;
        tracked.Evidence = prepared.Evidence;
        tracked.Reason = prepared.Reason;
        tracked.DecidedAt = prepared.DecidedAt;
        tracked.Resolved = prepared.Resolved;
        tracked.Approved = prepared.Approved;
        tracked.ResolvedBy = prepared.ResolvedBy;
        tracked.ResolutionNote = prepared.ResolutionNote;
        tracked.ResolvedAt = prepared.ResolvedAt;
    }

    private static HandoffGateRecord CloneGate(HandoffGateRecord gate) =>
        new()
        {
            Id = gate.Id,
            FlowRunId = gate.FlowRunId,
            FlowStepId = gate.FlowStepId,
            ActionType = gate.ActionType,
            Decision = gate.Decision,
            ReviewDecision = gate.ReviewDecision,
            TrustLevelAtDecision = gate.TrustLevelAtDecision,
            Summary = gate.Summary,
            Evidence = gate.Evidence,
            Reason = gate.Reason,
            DecidedAt = gate.DecidedAt,
            Resolved = gate.Resolved,
            Approved = gate.Approved,
            ResolvedBy = gate.ResolvedBy,
            ResolutionNote = gate.ResolutionNote,
            ResolvedAt = gate.ResolvedAt
        };

    private sealed record PreparedFeedbackClassification(
        FlowRun Flow,
        FlowStep Step,
        Guid GateId,
        string RequestHash,
        string FeedbackHash,
        FlowAgentSnapshot AccountManager,
        bool ResolvedReplay);

    private sealed record FeedbackRequestIdentity(
        Guid FlowStepId,
        Guid GateId,
        int ReviewIteration,
        string RequestHash,
        string FeedbackHash);

    private sealed record FeedbackRecoveryCandidate(
        Guid FlowId,
        Guid StepId,
        ExecutionInvocationKind InvocationKind);

    private sealed record PreparedFeedbackTurn(
        FlowStep Step,
        FeedbackClassificationTurnState State,
        bool WasRunning,
        double ExpectedAcceptedTimeSeconds);

    private sealed record ExecutedFeedbackTurn(
        FlowStep Step,
        FeedbackClassificationTurnState State,
        AgentExecutionResult Result,
        CopilotSessionSnapshot? RecoveredSnapshot);

    private sealed record FeedbackClassificationTurnState(
        string Version,
        int Attempt,
        string Kind,
        string Task,
        string[] ValidationErrors,
        Guid SessionId,
        int SessionGeneration,
        string? PriorOutputSha256,
        DateTimeOffset? PriorJournalCompletedAt)
    {
        public const string CurrentVersion =
            "review-classification-turn-v1";
    }

    private sealed class FeedbackClassificationDeferredException(
        string message)
        : InvalidOperationException(message);

    private sealed class ReviewFeedbackFlowValidationException(
        IReadOnlyList<string> errors)
        : InvalidOperationException(
            "Review feedback is invalid for the current flow: " +
            string.Join("; ", errors))
    {
        public IReadOnlyList<string> Errors { get; } = errors;
    }
}

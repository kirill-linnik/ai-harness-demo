using System.Collections.Immutable;
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
    DeliveryReadinessService? deliveryReadinessService = null,
    IDemoRuntimeRevoker? demoRuntimeRevoker = null,
    Func<bool>? githubCliAvailable = null,
    Func<CancellationToken, Task<bool>>? githubAuthenticationAvailable = null)
{
    private const int MaximumRefinementGoalCharacters = 4_000;
    private const int MaximumRequestedChanges = 24;
    private const int MaximumRequestedChangeCharacters = 4_000;
    private const int MaximumTotalRequestedChangeCharacters = 16_000;
    private readonly PermissionProfileResolver _permissionResolver =
        permissionProfileResolver ?? new PermissionProfileResolver();
    private readonly DeliveryReadinessService _readiness =
        deliveryReadinessService ?? new DeliveryReadinessService();
    private readonly Func<bool> _githubCliAvailable =
        githubCliAvailable ?? GitHubPublicationPrerequisites.IsAvailable;
    private readonly Func<CancellationToken, Task<bool>> _githubAuthenticationAvailable =
        githubAuthenticationAvailable ??
        GitHubPublicationPrerequisites.HasAuthenticationAsync;

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
            EnsureReviewFlow(flow);
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
                if (flow.Kind == FlowKind.Delivery &&
                    flow.Outcome == OutcomeType.PullRequest)
                {
                    var prerequisite = await GitHubPublicationPrerequisites.CheckAsync(
                        _githubCliAvailable,
                        _githubAuthenticationAvailable,
                        cancellationToken);
                    if (prerequisite.Error is { } error)
                    {
                        throw new DeliveryReadinessConflictException(
                            prerequisite.CliAvailable
                                ? DeliveryReadinessConflicts.PublicationAuthenticationUnavailable
                                : DeliveryReadinessConflicts.PublicationToolUnavailable,
                            error,
                            readiness?.State,
                            readiness?.Revision,
                            readiness?.ContractHash);
                    }
                }
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
                    "Readiness waivers apply only to Delivery flows.");
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
                           .Include(item => item.TaskProfiles)
                           .SingleOrDefaultAsync(
                               item => item.Id == flowId,
                               cancellationToken)
                       ?? throw new KeyNotFoundException(
                           $"Factory flow '{flowId}' was not found.");
            if (!DeliveryReadinessService.AppliesTo(flow))
            {
                throw new InvalidOperationException(
                    "Readiness resolution applies only to Delivery flows.");
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
                      DeliveryReadinessConflicts.BindingInvalid,
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
                                   DeliveryReadinessConflicts.BindingInvalid,
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
                    Action = action.ToString(),
                    State = binding.State.ToString(),
                    binding.Revision,
                    binding.ContractHash,
                    ReviewedCandidateId = binding.Candidate.Id
                })
            });

            if (action == ReadinessResolutionAction.Continue)
            {
                var taskAttempt = flow.Steps
                    .Where(step =>
                        step.Iteration == flow.Iteration &&
                        step.AgentId == reviewedStep.AgentId &&
                        string.Equals(
                            step.PlanStepKey,
                            reviewedStep.PlanStepKey,
                            StringComparison.Ordinal) &&
                        !step.Label.StartsWith(
                            "Correct invalid response from ",
                            StringComparison.Ordinal))
                    .OrderByDescending(step => step.Sequence)
                    .First();
                var permissionSource =
                    await WorkflowEngine.ResolveTaskPermissionSourceAsync(
                        database,
                        taskAttempt,
                        cancellationToken);
                var persistedPermission =
                    JsonSerializer.Deserialize<EffectiveExecutionPermission>(
                        permissionSource.EffectivePermissionJson)
                    ?? throw new InvalidOperationException(
                        "The blocked verification attempt has no persisted permission document.");
                PermissionProfileResolver.ValidatePersisted(
                    persistedPermission,
                    permissionSource.PermissionProfile);
                var workflow = workflowProvider.GetEffective();
                var duties = WorkflowEngine.ReadPlanDuties(
                    permissionSource.PlanDutiesJson);
                var currentPermission = _permissionResolver.Resolve(
                    new PermissionResolutionRequest(
                        flow.Kind,
                        permissionSource.InvocationKind,
                        permissionSource.PlanStage,
                        duties.ToImmutableArray(),
                        DurableReviewDecision: null,
                        DurableApproval: false,
                        IsOnlyPlannedPublishStep: false),
                    PermissionProfileResolver.FromWorkflow(workflow));
                var effectivePermission = PermissionProfileResolver.Tighten(
                    persistedPermission,
                    currentPermission);
                var continuation = new FlowStep
                {
                    FlowRunId = flow.Id,
                    Iteration = flow.Iteration,
                    Sequence = flow.Steps
                        .Where(step => step.Iteration == flow.Iteration)
                        .Select(step => step.Sequence)
                        .DefaultIfEmpty()
                        .Max() + 10,
                    AgentId = reviewedStep.AgentId,
                    AgentName = reviewedStep.AgentName,
                    AgentRole = reviewedStep.AgentRole,
                    Label = $"Continue blocked verification with {reviewedStep.AgentName}",
                    PlanStepKey = reviewedStep.PlanStepKey,
                    PlanDutiesJson = reviewedStep.PlanDutiesJson,
                    PlanStage = reviewedStep.PlanStage,
                    InvocationKind = reviewedStep.InvocationKind,
                    IsOutcomeOwner = true,
                    PermissionProfile = effectivePermission.Profile,
                    EffectivePermissionJson =
                        JsonSerializer.Serialize(effectivePermission),
                    WorkflowRevision = workflow.Revision,
                    RemotePublicationAllowed = false,
                    Status = StepStatus.Pending,
                    Phase = AgentRunPhase.PreparingWorkspace,
                    Attempt = flow.Steps
                        .Where(step =>
                            step.Iteration == flow.Iteration &&
                            step.AgentId == reviewedStep.AgentId)
                        .Select(step => step.Attempt)
                        .DefaultIfEmpty()
                        .Max() + 1,
                    InputSummary =
                        $"{permissionSource.InputSummary.Trim()}{Environment.NewLine}{Environment.NewLine}" +
                        "Customer requested another attempt after the blocked readiness result. " +
                        "Re-check the current candidate, address the blocker if it is now resolvable, " +
                        "and return a complete replacement verification and outcome. " +
                        "Unchanged host-owned scaffold files validated by candidate sealing are allowed " +
                        "and are not stray workspace output. For missing browser evidence, run one bounded " +
                        "browser-automation command per variant and viewport and print a compact result " +
                        "that names that variant, viewport, scrollWidth, and clientWidth.",
                    RetryOfStepId = reviewedStep.RetryOfStepId ?? reviewedStep.Id,
                    DependsOnStepId = reviewedStep.DependsOnStepId
                };
                continuation.StableSemanticRootId = continuation.Id;
                var sourceProfile = flow.TaskProfiles
                    .Where(profile =>
                        profile.Iteration == flow.Iteration &&
                        profile.AgentId == reviewedStep.AgentId &&
                        profile.PlanStepKey == reviewedStep.PlanStepKey)
                    .OrderByDescending(profile =>
                        profile.FlowStepId == reviewedStep.Id)
                    .ThenByDescending(profile => profile.CreatedAt)
                    .FirstOrDefault()
                    ?? throw new InvalidOperationException(
                        "The blocked outcome owner has no durable task profile.");
                flow.Steps.Add(continuation);
                database.TaskProfiles.Add(
                    TaskProfileRules.CopyForStep(sourceProfile, continuation.Id));
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = continuation.Id,
                    Type = "delivery.verification-retry-scheduled",
                    Message =
                        $"Customer continuation scheduled attempt {continuation.Attempt} for the final verification and outcome owner."
                });
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = continuation.Id,
                    Type = DeliveryReadinessService.EvidenceEpochEventType,
                    Message =
                        "Started a new evidence epoch for the customer-requested substantive verification continuation.",
                    DataJson =
                        DeliveryReadinessService.SerializeEvidenceEpoch(
                            new DeliveryEvidenceEpoch(
                                flow.Iteration,
                                continuation.Id,
                                continuation.Sequence))
                });
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
        EnsureReviewFlow(flow);
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

    internal static bool IsPublicationStep(FlowRun flow, FlowStep step) =>
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
        if (flow.Kind != FlowKind.Delivery)
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
                IsPublicationStep(flow, step))
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

    private static void EnsureReviewFlow(FlowRun flow) =>
        ArgumentNullException.ThrowIfNull(flow);

    private static (HandoffGateRecord Gate, FlowOutcomeDocument Outcome)
        ValidatePromotion(FlowRun flow, Guid gateId)
    {
        EnsureReviewFlow(flow);
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
                "Advisory promotion requires a valid flow outcome.");
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
                !root.TryGetProperty("RequestedChanges", out var changes) ||
                changes.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("RequestHash", out var requestHash) ||
                requestHash.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException(
                    "The durable refinement request is invalid.");
            }
            recordedHash = requestHash.GetString() ?? string.Empty;
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
                Goal = refinement.Goal,
                RequestedChanges = refinement.RequestedChanges
            }));

    internal static DirectReviewRefinement NormalizeRefinement(
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
                MaximumRefinementGoalCharacters,
                "Refinement goal");
        if (refinement.RequestedChanges.Count is
            < 1 or > MaximumRequestedChanges)
        {
            throw new ArgumentException(
                $"Requested changes must contain 1-{MaximumRequestedChanges} entries.");
        }
        var changes = refinement.RequestedChanges
            .Select(change => NormalizeText(
                change,
                MaximumRequestedChangeCharacters,
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

    /// <summary>
    /// Describes who asked for a refinement iteration so the durable seed, conversation message,
    /// and event stream stay honest about its origin. The customer path keeps the original
    /// wording; the harness path records host-owned auto-remediation instead.
    /// </summary>
    internal sealed record RefinementOrigin(
        string SeedHeading,
        ConversationRole MessageRole,
        string EventType,
        string EventMessage)
    {
        internal static RefinementOrigin Customer(int reviewedIteration) =>
            new(
                $"Customer refinement for iteration {reviewedIteration}:",
                ConversationRole.Customer,
                "flow.review-refinement-requested",
                $"Customer requested refinement; iteration {reviewedIteration + 1} will be replanned from the retained snapshot.");
    }

    internal static void ApplyRefinement(
        FlowRun flow,
        FlowStep reviewedStep,
        DirectReviewRefinement refinement,
        DateTimeOffset now,
        HarnessDbContext database,
        FlowLifecycleCoordinator lifecycle,
        RefinementOrigin? origin = null)
    {
        var reviewedIteration = flow.Iteration;
        var resolvedOrigin = origin ?? RefinementOrigin.Customer(reviewedIteration);
        var requestedChanges = refinement.RequestedChanges
            ?? throw new InvalidOperationException(
                "Normalized requested changes are missing.");
        var seedLines = new List<string>
        {
            resolvedOrigin.SeedHeading
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
            Role = resolvedOrigin.MessageRole,
            Content = seed
        });
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = reviewedStep.Id,
            Type = resolvedOrigin.EventType,
            Message = resolvedOrigin.EventMessage,
            DataJson = JsonSerializer.Serialize(new
            {
                Goal = refinement.Goal,
                RequestedChanges = requestedChanges,
                RequestHash = ComputeRefinementRequestHash(refinement)
            })
        });

        flow.ConsolidatedRequest =
            $"{seed}{Environment.NewLine}{Environment.NewLine}" +
            $"Current reviewed flow outcome JSON:{Environment.NewLine}{reviewedOutcome}" +
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
        var parsed = TeamPlanParser.ParseJson(document.RawJson).Document;
        if (parsed.Disposition != TeamPlanDisposition.Planned ||
            parsed.Steps is null)
        {
            throw new InvalidOperationException(
                "Delivery acceptance requires a planned team-plan document.");
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
        if (sameKey.Any(step => !IsPublicationStep(flow, step)))
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
            IsOnlyPlannedPublishStep: true);
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

        if (snapshots.SingleOrDefault()?.DataJson is not { } dataJson)
        {
            throw new InvalidOperationException(
                "The accepted plan has no publication permission snapshot.");
        }

        DeferredPermissionSnapshot baseline;
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
        if (baseline.Iteration != flow.Iteration ||
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
                IsPublicationStep(flow, step))
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

}

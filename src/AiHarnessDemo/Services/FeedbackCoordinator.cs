using AiHarnessDemo.Contracts;
using AiHarnessDemo.Data;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Verification;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace AiHarnessDemo.Services;

public sealed partial class FeedbackCoordinator(
    IDbContextFactory<HarnessDbContext> databaseFactory,
    IModelRouter modelRouter,
    BootstrapTaskProfileFactory profileFactory,
    RoutingObservationRecorder observationRecorder,
    IAgentRunner agentRunner,
    HandoffGateEngine gateEngine,
    FlowQueue flowQueue,
    FlowLifecycleCoordinator lifecycle,
    CandidateFingerprintService? candidateFingerprintService = null,
    WorkflowDefinitionProvider? workflowProvider = null,
    FlowAgentSnapshotService? flowAgentSnapshotService = null)
{
    [GeneratedRegex(
        @"(?im)^\s*REWORK_TARGET_ROLES\s*:\s*(?<roles>NONE|[a-z0-9-]+(?:\s*,\s*[a-z0-9-]+)*)\s*$")]
    private static partial Regex ReworkTargetRolesPattern();

    public async Task<FeedbackResponse> RespondAsync(
        Guid flowId,
        string feedback,
        CancellationToken cancellationToken = default)
    {
        await using var lifecycleLease =
            await lifecycle.EnterAsync(flowId, cancellationToken);
        var message = feedback.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Customer feedback cannot be empty.");
        }

        FlowRun flow;
        AgentRecord? productManager;
        FlowStep? step = null;
        var expectedAcceptedTimeSeconds = 0.0;
        await using (var database = await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            flow = await database.Flows
                       .Include(item => item.Steps)
                       .ThenInclude(step => step.ToolCalls)
                       .Include(item => item.Messages)
                       .Include(item => item.Events)
                       .Include(item => item.GateRecords)
                       .SingleOrDefaultAsync(item => item.Id == flowId, cancellationToken)
                   ?? throw new KeyNotFoundException($"Factory flow '{flowId}' was not found.");
            if (flow.ContractVersion != "legacy-v1")
            {
                throw new InvalidOperationException(
                    "studio-v2 feedback must be handled by the generic ReviewCoordinator.");
            }
            if (flow.Status != FlowStatus.WaitingForFeedback)
            {
                throw new InvalidOperationException(
                    "Customer feedback is accepted only when a preview is ready.");
            }
            if (!string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson) &&
                OutcomeVerificationRules
                    .DeserializeAggregate(flow.OutcomeVerificationJson)
                    .Status == OutcomeVerificationStatus.AwaitingHumanResolution)
            {
                throw new InvalidOperationException(
                    "Resolve outcome verification before collecting customer release feedback.");
            }

            var customerMessage = new FlowMessage
            {
                FlowRunId = flow.Id,
                Role = ConversationRole.Customer,
                Content = message
            };
            flow.Messages.Add(customerMessage);
            database.Entry(customerMessage).State = EntityState.Added;
            productManager = flowAgentSnapshotService is not null
                ? (await flowAgentSnapshotService.GetAgentsAsync(
                        flow.Id,
                        cancellationToken))
                    .SingleOrDefault(item =>
                        item.Role == "product-manager" &&
                        item.Enabled)
                : await ResolveLegacyProductManagerAsync(
                    database,
                    flow.Id,
                    cancellationToken);

            if (productManager is not null)
            {
                step = new FlowStep
                {
                    FlowRunId = flow.Id,
                    WorkflowRevision =
                        workflowProvider?.GetEffective().Revision ?? string.Empty,
                    Iteration = flow.Iteration,
                    Sequence = flow.Steps
                        .Where(item => item.Iteration == flow.Iteration)
                        .Select(item => item.Sequence)
                        .DefaultIfEmpty()
                        .Max() + 10,
                    AgentId = productManager.Id,
                    AgentName = productManager.Name,
                    AgentRole = productManager.Role,
                    Label = "Interpret customer feedback",
                    Status = StepStatus.Pending,
                    InputSummary = message
                };
                database.FlowSteps.Add(step);
            }

            flow.UpdatedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync(cancellationToken);
            if (step is not null)
            {
                database.TaskProfiles.Add(profileFactory.Create(
                    productManager!.Role,
                    $"{flow.ConsolidatedRequest}{Environment.NewLine}Customer feedback: {message}",
                    flow.Id,
                    flow.Iteration,
                    step.Id));
                await database.SaveChangesAsync(cancellationToken);
                var decision = await modelRouter.SelectAsync(
                    new RoutingRequest(step.Id, flow.ModelSelectionStrategy),
                    cancellationToken);
                expectedAcceptedTimeSeconds = decision.PredictedAcceptedTimeSeconds;
                step.Model = decision.SelectedModel;
                step.ModelEffort = decision.SelectedEffort;
                step.ModelReason = decision.Reason;
                step.Status = StepStatus.Running;
                step.StartedAt = DateTimeOffset.UtcNow;
                step.CopilotSessionId =
                    AgentSessionIdentity.Create(
                        flow.Id,
                        flow.Iteration,
                        productManager.Id,
                        step.PlanStepKey);
                step.CopilotSessionHome = CopilotReasoningHost.ResolveCopilotSessionHome();
                await database.SaveChangesAsync(cancellationToken);
            }
        }

        string reply;
        if (step is null || productManager is null)
        {
            reply =
                "Feedback is recorded in the execution ledger. Enable Product Manager for a contextual spoken response; " +
                "you can approve this result or start a revised iteration.";
        }
        else
        {
            var previousOutputs = flow.Steps
                .Where(item => item.Status is StepStatus.Completed or StepStatus.Pushback)
                .OrderBy(item => item.Iteration)
                .ThenBy(item => item.Sequence)
                .ThenBy(item => item.Attempt)
                .Where(item => !string.IsNullOrWhiteSpace(item.OutputSummary))
                .Select(item =>
                    $"{item.AgentName} ({item.AgentRole}){Environment.NewLine}" +
                    item.OutputSummary)
                .ToList();
            AgentExecutionResult result;
            try
            {
                result = await agentRunner.ExecuteAsync(
                    new AgentExecutionContext(
                        flow.Id,
                        flow.Iteration,
                        productManager.Id,
                        productManager.Name,
                        productManager.Role,
                        step.Model,
                        step.ModelEffort,
                        step.Attempt,
                        flow.ConsolidatedRequest,
                        flow.RepositoryKnowledge,
                        flow.RepositoryPath,
                        string.IsNullOrWhiteSpace(flow.WorkspacePath)
                            ? flow.RepositoryPath
                            : flow.WorkspacePath,
                        step.CopilotSessionId!.Value,
                        flow.Outcome,
                        "Review the execution ledger and help the customer decide.",
                        previousOutputs,
                        [],
                        message,
                        ModelSelectionStrategy: flow.ModelSelectionStrategy,
                        ExpectedAcceptedTimeSeconds: expectedAcceptedTimeSeconds,
                        IsGovernedOutcomeVerification:
                            !string.IsNullOrWhiteSpace(
                                flow.OutcomeVerificationJson),
                        GovernedRepositoryRelativePaths:
                            string.IsNullOrWhiteSpace(
                                flow.OutcomeVerificationJson)
                                ? null
                                : OutcomeVerificationRules.DeserializeAggregate(
                                        flow.OutcomeVerificationJson)
                                    .TrustedRepositories
                                    .Select(repository => repository.RelativePath)
                                    .ToArray(),
                        Progress: progress =>
                            RecordProgressAsync(
                                    flow.Id,
                                    step.Id,
                                    progress,
                                    CancellationToken.None)
                                .GetAwaiter()
                                .GetResult(),
                        InvocationStartedAt: step.StartedAt,
                        FlowStepId: step.Id),
                    cancellationToken);
            }
            catch (Exception exception)
            {
                await PersistFeedbackFailureAsync(
                    flow.Id,
                    step.Id,
                    exception,
                    CancellationToken.None);
                throw;
            }
            reply = StripReworkTargetMarker(result.Output);

            await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
            var storedStep = await database.FlowSteps.SingleAsync(
                item => item.Id == step.Id,
                cancellationToken);
            var gateRecord = gateEngine.SubmitProposal(new HandoffProposal
            {
                FlowRunId = flow.Id,
                FlowStepId = storedStep.Id,
                ActionType = HandoffActionType.Advance,
                Summary = result.Output,
                Evidence = result.Evidence,
                BlastRadius = HandoffBlastRadius.Low
            });
            database.GateRecords.Add(gateRecord);
            foreach (var toolCall in result.ToolCalls)
            {
                database.AgentToolCalls.Add(new AgentToolCall
                {
                    FlowStepId = storedStep.Id,
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
            storedStep.Status = StepStatus.Completed;
            storedStep.OutputSummary = result.Output;
            storedStep.ExecutionAttempts = result.ExecutionAttempts;
            storedStep.CompletedAt = DateTimeOffset.UtcNow;
            storedStep.DurationMilliseconds = Math.Max(
                1,
                (long)(storedStep.CompletedAt.Value - storedStep.StartedAt!.Value).TotalMilliseconds);
            await database.SaveChangesAsync(cancellationToken);
            await observationRecorder.RecordCompletionAsync(
                storedStep.Id,
                accepted: true,
                storedStep.DurationMilliseconds,
                result.ExecutionAttempts,
                "accepted-feedback-handoff",
                cancellationToken);
        }

        await using (var database = await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            var storedFlow = await database.Flows
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
                .SingleAsync(item => item.Id == flowId, cancellationToken);
            var responseMessage = new FlowMessage
            {
                FlowRunId = storedFlow.Id,
                Role = productManager is null
                    ? ConversationRole.Harness
                    : ConversationRole.ProductManager,
                Content = reply
            };
            storedFlow.Messages.Add(responseMessage);
            database.Entry(responseMessage).State = EntityState.Added;
            var feedbackEvent = new FlowEvent
            {
                FlowRunId = storedFlow.Id,
                Type = "feedback.reviewed",
                Message = "Customer feedback was grounded in the full execution ledger."
            };
            storedFlow.Events.Add(feedbackEvent);
            database.Entry(feedbackEvent).State = EntityState.Added;
            storedFlow.UpdatedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync(cancellationToken);
            return new FeedbackResponse(storedFlow.ToDetailDto(), reply, ShouldSpeak: true);
        }
    }

    private static async Task<AgentRecord?> ResolveLegacyProductManagerAsync(
        HarnessDbContext database,
        Guid flowId,
        CancellationToken cancellationToken)
    {
        var snapshots = await database.FlowAgentSnapshots
            .AsNoTracking()
            .Where(item => item.FlowRunId == flowId)
            .ToListAsync(cancellationToken);
        if (snapshots.Count > 0)
        {
            var snapshot = snapshots.SingleOrDefault(item =>
                item.Role == "product-manager" &&
                item.EnabledAtSnapshot);
            return snapshot is null
                ? null
                : new AgentRecord
                {
                    Id = snapshot.AgentId,
                    Name = snapshot.Name,
                    Description = snapshot.Description,
                    Role = snapshot.Role,
                    SourcePath = snapshot.SourceFileName,
                    Enabled = true,
                    Required = snapshot.Required,
                    Switchable = snapshot.Switchable,
                    DefinitionHash = snapshot.DefinitionHash,
                    LoadedAt = snapshot.CapturedAt,
                    UpdatedAt = snapshot.CapturedAt
                };
        }
        return await database.Agents
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item =>
                    item.Role == "product-manager" &&
                    item.Enabled,
                cancellationToken);
    }

    public async Task<FlowDecisionResponse> DecideAsync(
        Guid flowId,
        bool approve,
        Guid gateId,
        string candidateFingerprint,
        string feedback,
        CancellationToken cancellationToken = default)
    {
        await using var lifecycleLease =
            await lifecycle.EnterAsync(flowId, cancellationToken);
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var flow = await database.Flows
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
                       .SingleOrDefaultAsync(item => item.Id == flowId, cancellationToken)
                   ?? throw new KeyNotFoundException($"Factory flow '{flowId}' was not found.");

        if (flow.ContractVersion != "legacy-v1")
        {
            throw new InvalidOperationException(
                "studio-v2 decisions must be handled by the generic ReviewCoordinator.");
        }
        if (flow.Status != FlowStatus.WaitingForFeedback)
        {
            throw new InvalidOperationException("This flow is not waiting for a customer decision.");
        }
        if (!string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson) &&
            OutcomeVerificationRules
            .DeserializeAggregate(flow.OutcomeVerificationJson)
            .Status == OutcomeVerificationStatus.AwaitingHumanResolution)
        {
            throw new InvalidOperationException(
            "This flow is awaiting outcome resolution, not customer release approval.");
        }

        var currentStepIds = flow.Steps
            .Where(step => step.Iteration == flow.Iteration)
            .Select(step => step.Id)
            .ToHashSet();
        var pendingReleaseGate = flow.GateRecords
            .Where(item =>
                item.ActionType == HandoffActionType.Release &&
                !item.Resolved &&
                currentStepIds.Contains(item.FlowStepId))
            .OrderByDescending(item => item.DecidedAt)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The flow has no unresolved release gate for the customer to decide.");
        if (gateId != pendingReleaseGate.Id)
        {
            return new FlowDecisionResponse(
                ReleaseDecisionOutcome.Conflict,
                flow.ToDetailDto(),
                "The reviewed release gate is stale. Refresh before making a decision.");
        }

        OutcomeVerificationState? governedState = null;
        if (!string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson))
        {
            governedState = OutcomeVerificationRules.DeserializeAggregate(
                flow.OutcomeVerificationJson);
            if (governedState.Status != OutcomeVerificationStatus.Passed ||
                governedState.CurrentCandidate is null ||
                governedState.Stale ||
                !string.Equals(
                    governedState.CurrentCandidate.Fingerprint,
                    governedState.VerifiedCandidateFingerprint,
                    StringComparison.Ordinal))
            {
                return new FlowDecisionResponse(
                    ReleaseDecisionOutcome.Conflict,
                    flow.ToDetailDto(),
                    "The reviewed candidate no longer has a current authoritative QA PASS.");
            }
            if (!string.Equals(
                    candidateFingerprint,
                    governedState.VerifiedCandidateFingerprint,
                    StringComparison.Ordinal))
            {
                return new FlowDecisionResponse(
                    ReleaseDecisionOutcome.Conflict,
                    flow.ToDetailDto(),
                    "The reviewed candidate fingerprint is stale. Refresh before making a decision.");
            }
        }
        else if (!string.IsNullOrEmpty(candidateFingerprint))
        {
            return new FlowDecisionResponse(
                ReleaseDecisionOutcome.Conflict,
                flow.ToDetailDto(),
                "Legacy release decisions do not accept a candidate fingerprint.");
        }

        var rejectionFeedback = feedback?.Trim() ?? string.Empty;

        if (approve && governedState is not null)
        {
            var state = governedState;
            var candidate = state.CurrentCandidate
                ?? throw new InvalidOperationException(
                    "A current governed approval has no candidate.");
            var candidateCurrent = false;
            string? staleReason = null;
            try
            {
                candidateCurrent = await (candidateFingerprintService
                    ?? throw new InvalidOperationException(
                        "No candidate fingerprint service is configured."))
                    .IsCurrentAsync(
                    flow,
                        candidate,
                    CandidateFingerprintService.RequiresPreview(
                        state.AcceptancePlan),
                    cancellationToken);
            }
            catch (CandidateValidationException exception)
            {
                staleReason = exception.Message;
            }
            if (!candidateCurrent)
            {
                var superseded = gateEngine.PrepareSupersession(
                    pendingReleaseGate,
                    "harness",
                    "Candidate changed before customer approval.");
                state.Status = OutcomeVerificationStatus.AwaitingCandidateRefresh;
                state.Stale = true;
                state.VerifiedCandidateFingerprint = null;
                state.VerifiedAt = null;
                foreach (var round in state.Rounds.Where(round =>
                             string.Equals(
                                 round.CandidateFingerprint,
                                 candidate.Fingerprint,
                                 StringComparison.Ordinal)))
                {
                    round.Stale = true;
                }
                state.UpdatedAt = DateTimeOffset.UtcNow;
                var nextOutcomeJson =
                    OutcomeVerificationRules.SerializeAggregate(state);
                await using var staleTransaction =
                    await database.Database.BeginTransactionAsync(
                        cancellationToken);
                ApplyPreparedGate(superseded, pendingReleaseGate);
                flow.OutcomeVerificationJson = nextOutcomeJson;
                lifecycle.Transition(flow, FlowStatus.Queued);
                flow.OutcomeUrl = string.Empty;
                flow.OutcomeLabel = string.Empty;
                flow.UpdatedAt = DateTimeOffset.UtcNow;
                database.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flow.Id,
                    FlowStepId = pendingReleaseGate.FlowStepId,
                    Type = "outcome.candidate.stale",
                    Message =
                        "Candidate changed before customer approval; publication was not queued. " +
                        (staleReason ?? string.Empty)
                });
                await database.SaveChangesAsync(cancellationToken);
                await staleTransaction.CommitAsync(cancellationToken);
                gateEngine.RestoreHistory([superseded]);
                if (!flowQueue.Queue(flow.Id))
                {
                    throw new InvalidOperationException(
                        "Unable to queue stale-candidate refresh.");
                }
                return new FlowDecisionResponse(
                    ReleaseDecisionOutcome.RefreshQueued,
                    flow.ToDetailDto(),
                    "The candidate changed after review. Refresh and re-verification were queued; approval was not recorded.");
            }
        }
        var resolvedGate = gateEngine.PrepareResolution(
            pendingReleaseGate,
            approve,
            "customer",
            approve ? "Customer accepted the outcome." : "Customer requested another iteration.");

        var rejectedIteration = flow.Iteration;
        IReadOnlyList<string> targetedReworkRoles = [];
        if (!approve)
        {
            var eligibleRoles = flow.Steps
                .Where(item => item.Iteration == rejectedIteration)
                .Select(item => item.AgentRole)
                .Where(role => role is not "account-manager" and not "product-manager")
                .ToHashSet(StringComparer.Ordinal);
            var productManagerReview = flow.Steps
                .Where(item =>
                    item.Iteration == rejectedIteration &&
                    item.AgentRole == "product-manager" &&
                    item.Status == StepStatus.Completed)
                .OrderByDescending(item => item.Sequence)
                .ThenByDescending(item => item.Attempt)
                .FirstOrDefault();
            if (productManagerReview is not null)
            {
                _ = TryParseReworkTargets(
                    productManagerReview.OutputSummary,
                    eligibleRoles,
                    out targetedReworkRoles);
            }
        }

        string? nextIterationOutcomeJson = null;
        if (!approve)
        {
            if (governedState is not null)
            {
                nextIterationOutcomeJson =
                    OutcomeVerificationRules.SerializeAggregate(
                        OutcomeVerificationRules.StartNextIteration(
                            governedState,
                            flow.Iteration + 1,
                            (workflowProvider
                                ?? throw new InvalidOperationException(
                                    "No workflow provider is configured."))
                            .GetValidated()
                            .Config.OutcomeVerification.MaxRounds));
            }
            else if (workflowProvider?.GetValidated().Config.OutcomeVerification is
                     { Enabled: true } outcomeConfig)
            {
                var state = OutcomeVerificationRules.CreateInitialState(
                    flow.Iteration + 1,
                    outcomeConfig.MaxRounds);
                state.Status = OutcomeVerificationStatus.Planning;
                nextIterationOutcomeJson =
                    OutcomeVerificationRules.SerializeAggregate(state);
            }
        }
        else if (governedState is not null)
        {
            flow.OutcomeVerificationJson =
                OutcomeVerificationRules.SerializeAggregate(governedState);
        }

        await using var decisionTransaction =
            await database.Database.BeginTransactionAsync(cancellationToken);
        ApplyPreparedGate(resolvedGate, pendingReleaseGate);
        if (!approve && rejectionFeedback.Length > 0)
        {
            var customerMessage = new FlowMessage
            {
                FlowRunId = flow.Id,
                Role = ConversationRole.Customer,
                Content = rejectionFeedback
            };
            flow.Messages.Add(customerMessage);
            database.Entry(customerMessage).State = EntityState.Added;
        }

        if (approve)
        {
            var governed = !string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson);
            var publicationRound = default(int?);
            var publicationPlanHash = string.Empty;
            if (governed)
            {
                var state = OutcomeVerificationRules.DeserializeAggregate(
                    flow.OutcomeVerificationJson);
                publicationRound = state.Rounds
                    .Where(round =>
                        !round.Stale &&
                        round.Result?.Verdict == OutcomeQaVerdict.PASS &&
                        string.Equals(
                            round.CandidateFingerprint,
                            state.VerifiedCandidateFingerprint,
                            StringComparison.Ordinal))
                    .OrderByDescending(round => round.Round)
                    .Select(round => (int?)round.Round)
                    .FirstOrDefault();
                publicationPlanHash = state.AcceptancePlan?.Hash ?? string.Empty;
            }
            var releaseStep = flow.Steps
                .Where(item =>
                    item.Iteration == flow.Iteration &&
                    item.AgentRole == "release-engineer" &&
                    (!governed
                        ? !item.RemotePublicationAllowed
                        : item.Kind is
                            FlowStepKind.OutcomeLocalReleaseCandidate or
                            FlowStepKind.OutcomeCandidateRefresh))
                .OrderByDescending(item => item.Sequence)
                .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "The flow has no prepared Release Engineer handoff to publish.");
            var publicationStep = new FlowStep
            {
                FlowRunId = flow.Id,
                WorkflowRevision =
                    workflowProvider?.GetEffective().Revision ??
                    releaseStep.WorkflowRevision,
                Iteration = flow.Iteration,
                Sequence = flow.Steps
                    .Where(item => item.Iteration == flow.Iteration)
                    .Select(item => item.Sequence)
                    .DefaultIfEmpty()
                    .Max() + 10,
                AgentId = releaseStep.AgentId,
                AgentName = releaseStep.AgentName,
                AgentRole = releaseStep.AgentRole,
                Label = WorkflowEngine.ApprovedPublicationLabel,
                PlanDutiesJson = """["Publish"]""",
                PlanStage = PlanStage.AfterApproval,
                InvocationKind = ExecutionInvocationKind.Publication,
                PermissionProfile = governed
                    ? ExecutionPermissionProfile.WorkspaceWrite
                    : ExecutionPermissionProfile.Publish,
                Kind = governed
                    ? FlowStepKind.OutcomeApprovedPublication
                    : FlowStepKind.Standard,
                RemotePublicationAllowed = true,
                Status = StepStatus.Pending,
                Phase = AgentRunPhase.PreparingWorkspace,
                Attempt = flow.Steps
                    .Where(item =>
                        item.Iteration == flow.Iteration &&
                        item.AgentRole == "release-engineer")
                    .Select(item => item.Attempt)
                    .DefaultIfEmpty()
                    .Max() + 1,
                OutcomeQaRound = publicationRound,
                OutcomePlanHash = publicationPlanHash,
                InputSummary = string.IsNullOrWhiteSpace(flow.OutcomeVerificationJson)
                    ? WorkflowEngine.ApprovedPublicationAssignment(flow.Outcome)
                    : WorkflowEngine.HostControlledPublicationAssignment
            };
            publicationStep.StableSemanticRootId = publicationStep.Id;
            flow.Steps.Add(publicationStep);
            database.Entry(publicationStep).State = EntityState.Added;
            lifecycle.Transition(flow, FlowStatus.Queued);
            flow.CompletedAt = null;
            var approvedEvent = new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = publicationStep.Id,
                Type = "flow.approved-publication-queued",
                Message =
                    "Customer approved the reviewed candidate. Release publication is queued."
            };
            flow.Events.Add(approvedEvent);
            database.Entry(approvedEvent).State = EntityState.Added;
        }
        else
        {
            var latestFeedback = rejectionFeedback.Length > 0
                ? rejectionFeedback
                : flow.Messages
                .Where(item => item.Role == ConversationRole.Customer)
                .OrderByDescending(item => item.CreatedAt)
                .FirstOrDefault()?.Content
                  ?? "Customer requested another iteration.";
            flow.ConsolidatedRequest +=
                $"{Environment.NewLine}Customer feedback after iteration {flow.Iteration}: {latestFeedback}";
            if (nextIterationOutcomeJson is not null)
            {
                flow.OutcomeVerificationJson = nextIterationOutcomeJson;
            }
            flow.Iteration++;
            lifecycle.Transition(flow, FlowStatus.Queued);
            flow.OutcomeUrl = string.Empty;
            flow.OutcomeLabel = string.Empty;
            var reworkEvent = new FlowEvent
            {
                FlowRunId = flow.Id,
                Type = "flow.rework-queued",
                Message = $"Customer requested iteration {flow.Iteration}; all prior context is retained."
            };
            flow.Events.Add(reworkEvent);
            database.Entry(reworkEvent).State = EntityState.Added;
        }

        flow.UpdatedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        await decisionTransaction.CommitAsync(cancellationToken);
        gateEngine.RestoreHistory([resolvedGate]);

        if (!flowQueue.Queue(flow.Id))
        {
            throw new InvalidOperationException(
                approve
                    ? "Unable to queue the customer-approved release publication."
                    : "Unable to queue the revised factory flow.");
        }
        if (approve)
        {
            await observationRecorder.RecordFinalApprovalAsync(
                flow.Id,
                CancellationToken.None);
        }
        else if (targetedReworkRoles.Count > 0)
        {
            await observationRecorder.RecordTargetedReworkAsync(
                flow.Id,
                rejectedIteration,
                targetedReworkRoles,
                CancellationToken.None);
        }

        return new FlowDecisionResponse(
            approve
                ? ReleaseDecisionOutcome.Approved
                : ReleaseDecisionOutcome.Rejected,
            flow.ToDetailDto(),
            approve
                ? "Customer approval was recorded and publication was queued."
                : "Customer feedback was retained and a revised iteration was queued.");
    }

    internal static bool TryParseReworkTargets(
        string output,
        IReadOnlySet<string> eligibleRoles,
        out IReadOnlyList<string> targetRoles)
    {
        var matches = ReworkTargetRolesPattern().Matches(output);
        if (matches.Count != 1)
        {
            targetRoles = [];
            return false;
        }

        var value = matches[0].Groups["roles"].Value;
        if (string.Equals(value, "NONE", StringComparison.Ordinal))
        {
            targetRoles = [];
            return true;
        }

        var parsed = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (parsed.Length == 0 || parsed.Any(role => !eligibleRoles.Contains(role)))
        {
            targetRoles = [];
            return false;
        }

        targetRoles = parsed;
        return true;
    }

    internal static string StripReworkTargetMarker(string output) =>
        ReworkTargetRolesPattern().Replace(output, string.Empty).Trim();

    internal static bool HasReworkTargetMarker(string output) =>
        ReworkTargetRolesPattern().Matches(output).Count == 1;

    private static void ApplyPreparedGate(
        HandoffGateRecord prepared,
        HandoffGateRecord tracked)
    {
        if (prepared.Id != tracked.Id ||
            prepared.FlowRunId != tracked.FlowRunId ||
            prepared.FlowStepId != tracked.FlowStepId ||
            prepared.ActionType != tracked.ActionType)
        {
            throw new InvalidOperationException(
                "The prepared gate decision does not match the persisted release gate.");
        }
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

    private async Task RecordProgressAsync(
        Guid flowId,
        Guid stepId,
        AgentRunProgress progress,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var storedStep = await database.FlowSteps.SingleAsync(
            item => item.Id == stepId,
            cancellationToken);
        storedStep.Phase = progress.Phase;
        if (progress.ExecutionPrompt is not null)
        {
            storedStep.ExecutionPrompt = progress.ExecutionPrompt;
        }
        if (progress.CopilotSessionId is not null)
        {
            storedStep.CopilotSessionId = progress.CopilotSessionId;
            storedStep.CopilotSessionHome = progress.CopilotSessionHome ?? string.Empty;
        }
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flowId,
            FlowStepId = stepId,
            Type = $"agent.{progress.Phase}",
            Message = progress.Activity
        });
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task PersistFeedbackFailureAsync(
        Guid flowId,
        Guid stepId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var storedStep = await database.FlowSteps.SingleAsync(
            item => item.Id == stepId,
            cancellationToken);
        storedStep.Status = StepStatus.Failed;
        storedStep.Phase = exception is AgentRunException
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
        storedStep.CompletedAt = DateTimeOffset.UtcNow;
        if (exception is AgentRunException failedRun)
        {
            storedStep.ExecutionAttempts = Math.Max(
                storedStep.ExecutionAttempts,
                failedRun.ExecutionAttempts);
        }
        storedStep.DurationMilliseconds = Math.Max(
            1,
            (long)(storedStep.CompletedAt.Value - storedStep.StartedAt!.Value).TotalMilliseconds);
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flowId,
            FlowStepId = stepId,
            Type = "feedback.failed",
            Message = $"Product Manager could not process feedback: {exception.GetBaseException().Message}"
        });
        await database.SaveChangesAsync(cancellationToken);
        await observationRecorder.RecordFailureAsync(
            storedStep.Id,
            exception is AgentRunException runException
                ? runException.FailureKind
                : AgentRunFailureKind.InvalidOutput,
            storedStep.DurationMilliseconds,
            Math.Max(1, storedStep.ExecutionAttempts),
            cancellationToken);
    }
}

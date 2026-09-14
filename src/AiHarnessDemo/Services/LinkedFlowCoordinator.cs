using System.Text.Json;
using AiHarnessDemo.Contracts;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

public sealed record LinkedFlowIntakeResult(
    FlowRun Flow,
    string AccountManagerReply,
    bool AccountManagerExecuted);

public sealed class LinkedFlowCoordinator(
    IDbContextFactory<HarnessDbContext> databaseFactory,
    FlowAgentSnapshotService snapshotService,
    IntakeCoordinator intakeCoordinator,
    FlowLifecycleCoordinator lifecycle)
{
    public FlowRun CreateTrackedSuccessor(
        HarnessDbContext database,
        FlowRun parent,
        FlowLinkKind linkKind,
        FlowKind kind,
        OutcomeType outcome,
        string title,
        string seed,
        HarnessSettings settings)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(seed);
        var maximumSeedCharacters =
            linkKind == FlowLinkKind.AdvisoryPromotion
                ? AdvisoryPromotionSeedParser.MaximumDocumentCharacters
                : 65_536;
        if (seed.Length > maximumSeedCharacters)
        {
            throw new ArgumentException(
                $"A linked-flow intake seed must contain at most {maximumSeedCharacters} characters.",
                nameof(seed));
        }
        string seedHash;
        if (linkKind == FlowLinkKind.AdvisoryPromotion)
        {
            var canonical =
                AdvisoryPromotionSeedParser.Canonicalize(seed);
            if (!string.Equals(canonical, seed, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "An Advisory-promotion intake seed must use canonical normalized JSON.",
                    nameof(seed));
            }
            seedHash =
                AdvisoryPromotionSeedParser.ComputeHash(seed);
        }
        else
        {
            seedHash =
                AiHarnessDemo.Core.Verification.OutcomeVerificationRules
                    .ComputeSha256(seed);
        }
        if (kind == FlowKind.Delivery)
        {
            outcome = OutcomeTypeRules.RequireDelivery(
                outcome,
                nameof(outcome));
        }

        var successor = new FlowRun
        {
            Id = Guid.NewGuid(),
            Title = Clip(title.Trim(), 120),
            OriginalRequest = seed,
            ConsolidatedRequest = seed,
            Kind = kind,
            RepositoryPath = parent.RepositoryPath,
            RepositoryKnowledge = parent.RepositoryKnowledge,
            Outcome = outcome,
            ModelSelectionStrategy = settings.ModelSelectionStrategy
        };
        lifecycle.InitializeLinkedSuccessor(parent, successor, linkKind);
        database.Flows.Add(successor);
        snapshotService.CaptureForNewFlow(database, successor);
        successor.Events.Add(new FlowEvent
        {
            FlowRunId = successor.Id,
            Type = "flow.linked-successor-created",
            Message =
                $"Created a fresh {linkKind} successor from flow {parent.Id:D}, iteration {parent.Iteration}.",
            DataJson = JsonSerializer.Serialize(new
            {
                ParentFlowRunId = parent.Id,
                ParentIteration = parent.Iteration,
                LinkKind = linkKind.ToString(),
                SeedHash = seedHash
            })
        });
        successor.Events.Add(new FlowEvent
        {
            FlowRunId = successor.Id,
            Type = "flow.linked-intake-seed",
            Message =
                "Stored the bounded typed seed for normal Account Manager intake.",
            DataJson = seed
        });
        return successor;
    }

    public async Task<LinkedFlowIntakeResult> EnsureInitialIntakeAsync(
        Guid flowId,
        CancellationToken cancellationToken = default,
        bool queueReadyFlow = true)
    {
        string reply = string.Empty;
        var executed = false;
        try
        {
            string seed;
            await using (var database =
                         await databaseFactory.CreateDbContextAsync(
                             cancellationToken))
            {
                var existing = await LoadDetailedAsync(
                    database,
                    flowId,
                    cancellationToken);
                seed = ReadLinkedIntakeSeed(existing);
            }
            executed = true;
            var response = await intakeCoordinator.ContinueInitialLinkedAsync(
                flowId,
                seed,
                cancellationToken,
                queueReadyFlow);
            if (response is null)
            {
                executed = false;
            }
            else
            {
                reply = response.Reply;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await using var failureDatabase =
                await databaseFactory.CreateDbContextAsync(CancellationToken.None);
            var failedFlow = await failureDatabase.Flows.SingleAsync(
                item => item.Id == flowId,
                CancellationToken.None);
            var attempts = await failureDatabase.FlowSteps
                .Where(step =>
                    step.FlowRunId == flowId &&
                    step.Iteration == failedFlow.Iteration &&
                    step.InvocationKind ==
                        ExecutionInvocationKind.Intake)
                .OrderByDescending(step => step.StartedAt)
                .ThenByDescending(step => step.Sequence)
                .ToListAsync(CancellationToken.None);
            var canonicalAttempts = attempts
                .Where(step => string.Equals(
                    step.PlanStepKey,
                    IntakeCoordinator.InitialLinkedIntakePlanStepKey,
                    StringComparison.Ordinal))
                .ToList();
            var failedStep = attempts.Count == 1 &&
                             canonicalAttempts.Count == 1
                ? canonicalAttempts[0]
                : null;
            var failure = Clip(
                exception.GetBaseException().Message,
                4_000);
            if (failedStep is null && attempts.Count == 0)
            {
                var accountManager = await failureDatabase
                    .FlowAgentSnapshots
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        item =>
                            item.FlowRunId == flowId &&
                            item.AgentId == "account-manager" &&
                            item.EnabledAtSnapshot,
                        CancellationToken.None);
                failedStep = new FlowStep
                {
                    FlowRunId = flowId,
                    Iteration = failedFlow.Iteration,
                    Sequence = -99,
                    AgentId = accountManager?.AgentId ??
                              "account-manager",
                    AgentName = accountManager?.Name ??
                                "Account Manager",
                    AgentRole = accountManager?.Role ??
                                "account-manager",
                    Label = "Initial linked intake failed",
                    PlanStepKey =
                        IntakeCoordinator.InitialLinkedIntakePlanStepKey,
                    PlanDutiesJson = """["Analyze"]""",
                    PlanStage = PlanStage.BeforeReview,
                    InvocationKind = ExecutionInvocationKind.Intake,
                    PermissionProfile =
                        ExecutionPermissionProfile.ReadOnlySource,
                    WorkflowRevision = string.Empty,
                    Status = StepStatus.Failed,
                    Phase = AgentRunPhase.Failed,
                    Attempt = 1,
                    StartedAt = DateTimeOffset.UtcNow,
                    CompletedAt = DateTimeOffset.UtcNow,
                    InputSummary =
                        "Recover the durable linked intake seed.",
                    PushbackReason = failure
                };
                failedStep.StableSemanticRootId = failedStep.Id;
                failureDatabase.FlowSteps.Add(failedStep);
            }
            if (failedStep is not null)
            {
                failedStep.Status = StepStatus.Failed;
                failedStep.Phase = AgentRunPhase.Failed;
                failedStep.PushbackReason = failure;
                failedStep.CompletedAt = DateTimeOffset.UtcNow;
            }
            failedFlow.FailureReason =
                "Initial linked Account Manager intake failed: " + failure;
            failedFlow.UpdatedAt = DateTimeOffset.UtcNow;
            if (!await failureDatabase.FlowEvents.AnyAsync(
                    item =>
                        item.FlowRunId == flowId &&
                        item.Type == "linked.intake-failed",
                    CancellationToken.None))
            {
                failureDatabase.FlowEvents.Add(new FlowEvent
                {
                    FlowRunId = flowId,
                    FlowStepId = failedStep?.Id,
                    Type = "linked.intake-failed",
                    Message =
                        "The linked flow was created, but its initial Account Manager turn did not complete.",
                    DataJson = JsonSerializer.Serialize(new
                    {
                        Error = failure
                    })
                });
            }
            const string customerSafeFailure =
                "The initial Account Manager turn failed. Review the failed step and send a new intake message to retry.";
            if (!await failureDatabase.FlowMessages.AnyAsync(
                    item =>
                        item.FlowRunId == flowId &&
                        item.Role == ConversationRole.Harness &&
                        item.Content == customerSafeFailure,
                    CancellationToken.None))
            {
                failureDatabase.FlowMessages.Add(new FlowMessage
                {
                    FlowRunId = flowId,
                    Role = ConversationRole.Harness,
                    Content = customerSafeFailure
                });
            }
            await failureDatabase.SaveChangesAsync(CancellationToken.None);
        }

        await using var resultDatabase =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var result = await LoadDetailedAsync(
            resultDatabase,
            flowId,
            cancellationToken);
        return new LinkedFlowIntakeResult(
            result,
            string.IsNullOrWhiteSpace(reply)
                ? LatestAccountManagerReply(result)
                : reply,
            AccountManagerExecuted: executed);
    }

    public async Task<IReadOnlyList<Guid>> RecoverUnstartedIntakesAsync(
        CancellationToken cancellationToken = default)
    {
        List<Guid> candidates;
        await using (var database =
                     await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            candidates = await database.Flows
                .AsNoTracking()
                .Where(flow =>
                    flow.Status == FlowStatus.Intake &&
                    flow.ParentFlowRunId != null &&
                    flow.LinkKind != null &&
                    database.FlowEvents.Any(item =>
                        item.FlowRunId == flow.Id &&
                        item.Type == "flow.linked-intake-seed"))
                .Select(flow => flow.Id)
                .ToListAsync(cancellationToken);
        }

        var recovered = new List<Guid>();
        foreach (var flowId in candidates)
        {
            try
            {
                var result = await EnsureInitialIntakeAsync(
                    flowId,
                    cancellationToken,
                    queueReadyFlow: false);
                if (result.AccountManagerExecuted)
                {
                    recovered.Add(flowId);
                }
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await RecordLinkedRecoveryFailureBestEffortAsync(
                    flowId,
                    exception,
                    CancellationToken.None);
            }
        }
        recovered.AddRange(
            await intakeCoordinator.RecoverOrdinaryIntakesAsync(
                cancellationToken));
        recovered.AddRange(
            await intakeCoordinator.RecoverUnmaterializedIntakesAsync(
                cancellationToken));
        return recovered;
    }

    private async Task RecordLinkedRecoveryFailureBestEffortAsync(
        Guid flowId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var database =
                await databaseFactory.CreateDbContextAsync(cancellationToken);
            if (!await database.Flows
                    .AsNoTracking()
                    .AnyAsync(item => item.Id == flowId, cancellationToken) ||
                await database.FlowEvents.AnyAsync(
                    item =>
                        item.FlowRunId == flowId &&
                        item.Type == "intake.linked-recovery-failed",
                    cancellationToken))
            {
                return;
            }
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flowId,
                Type = "intake.linked-recovery-failed",
                Message =
                    "Startup linked-intake reconciliation skipped this flow after a durable-state error: " +
                    Clip(exception.GetBaseException().Message, 4_000)
            });
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Recovery diagnostics are best-effort. One malformed linked flow
            // must never block reconciliation of unrelated flows.
        }
    }

    public async Task<FlowRun?> FindSuccessorAsync(
        Guid parentFlowId,
        int parentIteration,
        FlowLinkKind linkKind,
        CancellationToken cancellationToken = default)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        return await database.Flows
            .AsNoTracking()
            .SingleOrDefaultAsync(
                flow =>
                    flow.ParentFlowRunId == parentFlowId &&
                    flow.ParentIteration == parentIteration &&
                    flow.LinkKind == linkKind,
                cancellationToken);
    }

    public async Task<FlowRun> LoadDetailedAsync(
        Guid flowId,
        CancellationToken cancellationToken = default)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        return await LoadDetailedAsync(database, flowId, cancellationToken);
    }

    public static bool IsUniqueSuccessorConflict(DbUpdateException exception) =>
        exception.GetBaseException() is SqliteException
        {
            SqliteErrorCode: 19
        } sqlite &&
        sqlite.Message.Contains(
            "Flows.ParentFlowRunId",
            StringComparison.OrdinalIgnoreCase);

    public static string SerializePromotionSeed(FlowOutcomeDocument outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return AdvisoryPromotionSeedParser.Serialize(
            outcome.Goal,
            outcome.ImplementationDetails ??
            throw new AdvisoryPromotionSeedContractException(
                ["ImplementationDetails is required"]));
    }

    internal static string ReadLinkedIntakeSeed(FlowRun flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var events = flow.Events
            .Where(item =>
                item.FlowRunId == flow.Id &&
                item.Type == "flow.linked-intake-seed")
            .ToArray();
        var maximumSeedCharacters =
            flow.LinkKind == FlowLinkKind.AdvisoryPromotion
                ? AdvisoryPromotionSeedParser.MaximumDocumentCharacters
                : 65_536;
        if (events.Length != 1 ||
            string.IsNullOrWhiteSpace(events[0].DataJson) ||
            events[0].DataJson!.Length >
            maximumSeedCharacters)
        {
            throw new InvalidOperationException(
                "The linked flow must have exactly one bounded durable intake seed.");
        }
        var seed = events[0].DataJson!;
        if (!string.Equals(
                flow.OriginalRequest,
                seed,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The linked flow request does not match its durable intake seed.");
        }
        if (flow.LinkKind == FlowLinkKind.AdvisoryPromotion)
        {
            var canonical = AdvisoryPromotionSeedParser.Canonicalize(seed);
            if (!string.Equals(canonical, seed, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The durable Advisory-promotion seed is not canonical.");
            }
        }
        var linkEvent = flow.Events.SingleOrDefault(item =>
            item.Type == "flow.linked-successor-created");
        if (!string.IsNullOrWhiteSpace(linkEvent?.DataJson))
        {
            using var document = JsonDocument.Parse(linkEvent.DataJson);
            if (document.RootElement.TryGetProperty(
                    "SeedHash",
                    out var storedHash) &&
                storedHash.ValueKind == JsonValueKind.String)
            {
                var expectedHash =
                    flow.LinkKind ==
                    FlowLinkKind.AdvisoryPromotion
                        ? AdvisoryPromotionSeedParser.ComputeHash(seed)
                        : AiHarnessDemo.Core.Verification
                            .OutcomeVerificationRules.ComputeSha256(seed);
                if (!string.Equals(
                        storedHash.GetString(),
                        expectedHash,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The durable linked intake seed hash does not match its content.");
                }
            }
        }
        return seed;
    }

    private static Task<FlowRun> LoadDetailedAsync(
        HarnessDbContext database,
        Guid flowId,
        CancellationToken cancellationToken) =>
        database.Flows
            .AsNoTracking()
            .AsSplitQuery()
            .Include(item => item.Steps)
            .ThenInclude(step => step.ToolCalls)
            .Include(item => item.Messages)
            .Include(item => item.Events)
            .Include(item => item.GateRecords)
            .Include(item => item.AgentSnapshots)
            .Include(item => item.PlanDocuments)
            .Include(item => item.TaskProfiles)
            .Include(item => item.LinkedFlowRuns)
            .SingleAsync(item => item.Id == flowId, cancellationToken);

    private static string LatestAccountManagerReply(FlowRun flow) =>
        flow.Messages
            .Where(message =>
                message.Role == ConversationRole.AccountManager)
            .OrderByDescending(message => message.CreatedAt)
            .Select(message => message.Content)
            .FirstOrDefault() ?? string.Empty;

    private static string Clip(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

}

using System.Text.Json;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Data;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

public sealed record QualificationResolutionResult(
    QualificationResolutionResponse Response,
    FlowRun Parent,
    FlowRun Successor);

public sealed class QualificationResolutionCoordinator(
    IDbContextFactory<HarnessDbContext> databaseFactory,
    INewWorkAdmissionService admissionService,
    LinkedFlowCoordinator linkedFlows,
    FlowLifecycleCoordinator lifecycle)
{
    private const int MaximumScopeItems = 24;
    private const int MaximumTextCharacters = 4_000;

    public async Task<QualificationResolutionResult> ResolveAsync(
        Guid flowId,
        QualificationResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var action = request.Action
            ?? throw new ArgumentException(
                "A qualification resolution action is required.");
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentException(
                "The qualification resolution action is not supported.");
        }
        var revision = NormalizeRevision(
            action,
            request.ScopeRevision,
            request.RevisedGoal,
            request.RevisedScope);
        var linkKind = action switch
        {
            QualificationResolutionAction.RosterRepair =>
                FlowLinkKind.QualificationRosterRepair,
            QualificationResolutionAction.ScopeRevision =>
                FlowLinkKind.QualificationScopeRevision,
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
        var requestHash =
            action == QualificationResolutionAction.ScopeRevision
                ? ComputeScopeRevisionRequestHash(revision!)
                : null;

        await using var lifecycleLease =
            await lifecycle.EnterAsync(flowId, cancellationToken);
        FlowRun parent;
        FlowRun? successor;
        string seed;
        await using (var database =
                     await databaseFactory.CreateDbContextAsync(cancellationToken))
        {
            parent = await database.Flows
                         .AsNoTracking()
                         .SingleOrDefaultAsync(
                             item => item.Id == flowId,
                             cancellationToken)
                     ?? throw new KeyNotFoundException(
                         $"Factory flow '{flowId}' was not found.");
            EnsureResolvableParent(parent);
            var original = ReadNormalizedBrief(parent.ConsolidatedRequest);
            seed = action == QualificationResolutionAction.RosterRepair
                ? SerializeSeed(action, original)
                : SerializeScopeRevisionSeed(revision!, requestHash!);
            successor = await database.Flows
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item =>
                        item.ParentFlowRunId == parent.Id &&
                        item.ParentIteration == parent.Iteration &&
                        item.LinkKind == linkKind,
                    cancellationToken);
            if (successor is not null)
            {
                EnsureMatchingSuccessor(
                    parent,
                    successor,
                    linkKind,
                    requestHash);
            }
        }

        var existingSuccessor = successor is not null;
        if (successor is null)
        {
            await admissionService.EnsureReadyForContextAsync(
                parent.RepositoryPath,
                parent.RepositoryKnowledge,
                cancellationToken);
            try
            {
                await using var database =
                    await databaseFactory.CreateDbContextAsync(cancellationToken);
                await using var transaction =
                    await database.Database.BeginTransactionAsync(cancellationToken);
                var trackedParent = await database.Flows.SingleAsync(
                    item => item.Id == flowId,
                    cancellationToken);
                EnsureResolvableParent(trackedParent);
                successor = await database.Flows.SingleOrDefaultAsync(
                    item =>
                        item.ParentFlowRunId == trackedParent.Id &&
                        item.ParentIteration == trackedParent.Iteration &&
                        item.LinkKind == linkKind,
                    cancellationToken);
                if (successor is null)
                {
                    var settings = await database.Settings
                        .AsNoTracking()
                        .SingleAsync(cancellationToken);
                    successor = linkedFlows.CreateTrackedSuccessor(
                        database,
                        trackedParent,
                        linkKind,
                        trackedParent.Kind,
                        trackedParent.Kind == FlowKind.Advisory
                            ? OutcomeType.None
                            : OutcomeTypeRules.RequireDelivery(
                                settings.Outcome,
                                nameof(settings.Outcome)),
                        action == QualificationResolutionAction.RosterRepair
                            ? trackedParent.Title
                            : BuildTitle(revision!.Goal),
                        seed,
                        settings);
                    if (action ==
                        QualificationResolutionAction.ScopeRevision)
                    {
                        successor.Events.Add(new FlowEvent
                        {
                            FlowRunId = successor.Id,
                            Type = "qualification.scope-revision-request",
                            Message =
                                "Stored the normalized scope-revision request identity for idempotent successor recovery.",
                            DataJson = SerializeScopeRequestIdentity(
                                revision!,
                                requestHash!)
                        });
                    }
                    await database.SaveChangesAsync(cancellationToken);
                }
                else
                {
                    EnsureMatchingSuccessor(
                        trackedParent,
                        successor,
                        linkKind,
                        requestHash);
                    existingSuccessor = true;
                }
                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (
                LinkedFlowCoordinator.IsUniqueSuccessorConflict(exception))
            {
                existingSuccessor = true;
                successor = await linkedFlows.FindSuccessorAsync(
                    parent.Id,
                    parent.Iteration,
                    linkKind,
                    cancellationToken)
                    ?? throw new InvalidOperationException(
                        "A concurrent successor was created but could not be loaded.",
                        exception);
                EnsureMatchingSuccessor(
                    parent,
                    successor,
                    linkKind,
                    requestHash);
            }
        }

        var intake = await linkedFlows.EnsureInitialIntakeAsync(
            successor.Id,
            cancellationToken);
        var currentParent = await LoadParentAsync(flowId, cancellationToken);
        var response = new QualificationResolutionResponse(
            currentParent.Id,
            currentParent.Iteration,
            intake.Flow.Id,
            linkKind,
            currentParent.Status,
            intake.Flow.Status,
            existingSuccessor,
            intake.AccountManagerReply);
        return new QualificationResolutionResult(
            response,
            currentParent,
            intake.Flow);
    }

    private static QualificationScopeRevision? NormalizeRevision(
        QualificationResolutionAction action,
        QualificationScopeRevision? revision,
        string? revisedGoal,
        IReadOnlyList<string>? revisedScope)
    {
        if (action == QualificationResolutionAction.RosterRepair)
        {
            if (revision is not null ||
                revisedGoal is not null ||
                revisedScope is not null)
            {
                throw new ArgumentException(
                    "RosterRepair must not include a scope revision.");
            }
            return null;
        }
        if (revision is not null &&
            (revisedGoal is not null || revisedScope is not null))
        {
            throw new ArgumentException(
                "ScopeRevision must use either ScopeRevision or RevisedGoal/RevisedScope, not both.");
        }
        revision ??= new QualificationScopeRevision
        {
            Goal = revisedGoal ?? string.Empty,
            Scope = revisedScope
        };
        if (revision?.Scope is null)
        {
            throw new ArgumentException(
                "ScopeRevision requires a revised goal and scope.");
        }
        var goal = NormalizeText(revision.Goal, "Revised goal");
        if (revision.Scope.Count is < 1 or > MaximumScopeItems)
        {
            throw new ArgumentException(
                $"Revised scope must contain 1-{MaximumScopeItems} entries.");
        }
        var scope = revision.Scope
            .Select(item => NormalizeText(item, "Revised scope item"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (scope.Sum(item => item.Length) > 16_000)
        {
            throw new ArgumentException(
                "Revised scope must contain at most 16000 characters in total.");
        }
        return new QualificationScopeRevision
        {
            Goal = goal,
            Scope = scope
        };
    }

    private static void EnsureResolvableParent(FlowRun parent)
    {
        if (parent.Status != FlowStatus.Blocked ||
            !string.Equals(
                parent.CurrentBlockerCode,
                MissingQualificationCoordinator.BlockerCode,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(parent.CurrentBlockerDataJson))
        {
            throw new InvalidOperationException(
                "Qualification resolution requires a blocked missing-qualification flow.");
        }
    }

    private static void EnsureMatchingSuccessor(
        FlowRun parent,
        FlowRun successor,
        FlowLinkKind linkKind,
        string? expectedRequestHash)
    {
        if (successor.ParentFlowRunId != parent.Id ||
            successor.ParentIteration != parent.Iteration ||
            successor.LinkKind != linkKind ||
            !string.Equals(
                successor.RepositoryPath,
                parent.RepositoryPath,
                StringComparison.Ordinal) ||
            !string.Equals(
                successor.RepositoryKnowledge,
                parent.RepositoryKnowledge,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The existing qualification successor does not match its immutable parent link.");
        }
        if (linkKind != FlowLinkKind.QualificationScopeRevision)
        {
            return;
        }
        var persistedRequestHash =
            ReadScopeRevisionRequestHash(successor.OriginalRequest);
        if (!string.Equals(
                persistedRequestHash,
                expectedRequestHash,
                StringComparison.Ordinal))
        {
            throw new FlowLifecycleException(
                parent.Id,
                parent.Status,
                parent.Status,
                "A materially different scope revision successor already exists for this blocked iteration.");
        }
    }

    private static NormalizedBrief ReadNormalizedBrief(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "The blocked flow has no normalized original brief.");
            }
            return new NormalizedBrief(
                ReadText(root, "Goal"),
                ReadArray(root, "Details"),
                ReadArray(root, "SuccessCriteria"),
                ReadArray(root, "Constraints"),
                ReadArray(root, "Assumptions"));
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The blocked flow has no valid normalized original brief.",
                exception);
        }
    }

    private static string SerializeSeed(
        QualificationResolutionAction action,
        NormalizedBrief brief) =>
        JsonSerializer.Serialize(new
        {
            Action = action.ToString(),
            brief.Goal,
            brief.Details,
            brief.SuccessCriteria,
            brief.Constraints,
            brief.Assumptions
        });

    private static string SerializeScopeRevisionSeed(
        QualificationScopeRevision revision,
        string requestHash) =>
        JsonSerializer.Serialize(new
        {
            Action = QualificationResolutionAction.ScopeRevision.ToString(),
            RequestHash = requestHash,
            Goal = revision.Goal,
            Details = revision.Scope,
            SuccessCriteria = Array.Empty<string>(),
            Constraints = Array.Empty<string>(),
            Assumptions = Array.Empty<string>()
        });

    private static string SerializeScopeRequestIdentity(
        QualificationScopeRevision revision,
        string requestHash) =>
        JsonSerializer.Serialize(new
        {
            RequestHash = requestHash,
            Goal = revision.Goal,
            Scope = revision.Scope
        });

    private static string ComputeScopeRevisionRequestHash(
        QualificationScopeRevision revision) =>
        OutcomeVerificationRules.ComputeSha256(
            JsonSerializer.Serialize(new
            {
                Goal = revision.Goal,
                Scope = revision.Scope
            }));

    private static string ReadScopeRevisionRequestHash(string seed)
    {
        try
        {
            using var document = JsonDocument.Parse(seed);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("Action", out var action) ||
                !string.Equals(
                    action.GetString(),
                    QualificationResolutionAction.ScopeRevision.ToString(),
                    StringComparison.Ordinal) ||
                !root.TryGetProperty("RequestHash", out var requestHash) ||
                requestHash.ValueKind != JsonValueKind.String ||
                !OutcomeVerificationRules.IsSha256(
                    requestHash.GetString() ?? string.Empty) ||
                !root.TryGetProperty("Goal", out var goal) ||
                goal.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("Details", out var details) ||
                details.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    "The existing scope-revision successor has no valid durable request identity.");
            }
            var normalized = NormalizeRevision(
                QualificationResolutionAction.ScopeRevision,
                new QualificationScopeRevision
                {
                    Goal = goal.GetString() ?? string.Empty,
                    Scope = details.EnumerateArray()
                        .Select(item =>
                            item.ValueKind == JsonValueKind.String
                                ? item.GetString() ?? string.Empty
                                : throw new InvalidOperationException(
                                    "The existing scope-revision successor contains a non-text scope item."))
                        .ToArray()
                },
                revisedGoal: null,
                revisedScope: null)!;
            var persisted = requestHash.GetString()!;
            if (!string.Equals(
                    persisted,
                    ComputeScopeRevisionRequestHash(normalized),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The existing scope-revision successor request identity does not match its normalized seed.");
            }
            return persisted;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The existing scope-revision successor has an invalid durable request identity.",
                exception);
        }
    }

    private static string ReadText(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                $"The blocked flow brief is missing {name}.");
        }
        return NormalizeText(value.GetString(), name);
    }

    private static IReadOnlyList<string> ReadArray(
        JsonElement root,
        string name)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"The blocked flow brief is missing {name}.");
        }
        var values = value.EnumerateArray()
            .Select(item =>
                item.ValueKind == JsonValueKind.String
                    ? NormalizeText(item.GetString(), name)
                    : throw new InvalidOperationException(
                        $"The blocked flow brief contains an invalid {name} item."))
            .ToArray();
        if (values.Length > MaximumScopeItems)
        {
            throw new InvalidOperationException(
                $"The blocked flow brief contains too many {name} items.");
        }
        return values;
    }

    private static string NormalizeText(string? value, string label)
    {
        var normalized = value?
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim() ?? string.Empty;
        if (normalized.Length is 0 or > MaximumTextCharacters ||
            normalized.Any(character =>
                char.IsControl(character) &&
                character is not '\n' and not '\t'))
        {
            throw new ArgumentException(
                $"{label} must contain 1-{MaximumTextCharacters} safe, trimmed characters.");
        }
        return normalized;
    }

    private async Task<FlowRun> LoadParentAsync(
        Guid flowId,
        CancellationToken cancellationToken)
    {
        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        return await database.Flows
            .AsNoTracking()
            .Include(item => item.LinkedFlowRuns)
            .SingleAsync(item => item.Id == flowId, cancellationToken);
    }

    private static string BuildTitle(string goal) =>
        goal.Length <= 120 ? goal : $"{goal[..117]}...";

    private sealed record NormalizedBrief(
        string Goal,
        IReadOnlyList<string> Details,
        IReadOnlyList<string> SuccessCriteria,
        IReadOnlyList<string> Constraints,
        IReadOnlyList<string> Assumptions);
}

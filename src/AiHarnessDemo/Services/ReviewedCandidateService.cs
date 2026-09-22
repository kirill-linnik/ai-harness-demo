using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Verification;

namespace AiHarnessDemo.Services;

public sealed record ReviewedCandidateRepositoryIdentity(
    string RelativePath,
    string Head,
    string Tree,
    string RemoteRepository);

public sealed record ReviewedCandidateIdentity(
    Guid FlowId,
    int Iteration,
    Guid OutcomeOwnerStepId,
    string OutcomeOwnerPlanStepKey,
    string OutcomeContractHash,
    string AcceptancePlanHash,
    string Fingerprint,
    int TrustedScaffoldFileCount,
    long TrustedScaffoldTotalBytes,
    int PreviewFileCount,
    long PreviewTotalBytes,
    IReadOnlyList<ReviewedCandidateRepositoryIdentity> Repositories,
    DateTimeOffset SealedAt);

public sealed record StudioWorkspaceRepositoryMap(
    Guid FlowId,
    string WorkspacePath,
    IReadOnlyList<WorkspaceRepositoryIdentity> Repositories);

public interface IReviewedCandidateService
{
    Task<ReviewedCandidateIdentity> SealAsync(
        FlowRun flow,
        Guid outcomeOwnerStepId,
        string outcomeOwnerPlanStepKey,
        string outcomeContractJson,
        CancellationToken cancellationToken = default);

    Task<OutcomeCandidateSnapshot> VerifyAsync(
        FlowRun flow,
        ReviewedCandidateIdentity identity,
        CancellationToken cancellationToken = default);

    Task<OutcomeCandidateSnapshot> VerifyPreviewAsync(
        FlowRun flow,
        ReviewedCandidateIdentity identity,
        CancellationToken cancellationToken = default) =>
        VerifyAsync(flow, identity, cancellationToken);
}

public sealed class ReviewedCandidateService(
    CandidateFingerprintService fingerprints) : IReviewedCandidateService
{
    private readonly ConcurrentDictionary<
        string,
        Lazy<Task<OutcomeCandidateSnapshot>>> _previewVerifications =
            new(StringComparer.Ordinal);

    public async Task<ReviewedCandidateIdentity> SealAsync(
        FlowRun flow,
        Guid outcomeOwnerStepId,
        string outcomeOwnerPlanStepKey,
        string outcomeContractJson,
        CancellationToken cancellationToken = default)
    {
        ReviewedCandidateLedger.ValidateContext(
            flow,
            outcomeOwnerStepId,
            outcomeOwnerPlanStepKey,
            outcomeContractJson);
        _ = StudioWorkspaceRepositoryMapLedger.Read(flow);

        await fingerprints.SealAsync(flow, cancellationToken);
        var outcomeHash = OutcomeVerificationRules.ComputeSha256(
            outcomeContractJson);
        var acceptancePlanHash = OutcomeVerificationRules.ComputeSha256(
            string.Join(
                "\n",
                flow.Id.ToString("D"),
                flow.Iteration.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                outcomeOwnerStepId.ToString("D"),
                outcomeOwnerPlanStepKey,
                outcomeHash));
        var requiresPreview = RequiresCustomerPreview(flow);
        var snapshot = await fingerprints.PrepareAsync(
            flow,
            acceptancePlanHash,
            outcomeOwnerStepId,
            requiresPreview,
            cancellationToken);
        var identity = new ReviewedCandidateIdentity(
            flow.Id,
            flow.Iteration,
            outcomeOwnerStepId,
            outcomeOwnerPlanStepKey,
            outcomeHash,
            acceptancePlanHash,
            snapshot.Fingerprint,
            snapshot.Manifest.TrustedScaffoldFiles.Count,
            snapshot.Manifest.TrustedScaffoldFiles.Sum(item => item.Length),
            snapshot.Manifest.PreviewArtifacts.Count,
            snapshot.Manifest.PreviewArtifacts.Sum(item => item.Length),
            snapshot.Manifest.Repositories
                .Select(repository =>
                    new ReviewedCandidateRepositoryIdentity(
                        repository.RelativePath,
                        repository.Head,
                        repository.Tree,
                        repository.RemoteRepository))
                .OrderBy(repository => repository.RelativePath, StringComparer.Ordinal)
                .ToArray(),
            snapshot.PreparedAt);
        ReviewedCandidateLedger.Validate(identity);
        CachePreviewVerification(identity, snapshot);
        return identity;
    }

    public async Task<OutcomeCandidateSnapshot> VerifyPreviewAsync(
        FlowRun flow,
        ReviewedCandidateIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ReviewedCandidateLedger.ValidateForFlow(flow, identity);
        _ = StudioWorkspaceRepositoryMapLedger.Read(flow);

        var key = PreviewVerificationKey(identity);
        var verification = _previewVerifications.GetOrAdd(
            key,
            _ => new Lazy<Task<OutcomeCandidateSnapshot>>(
                () => VerifyAsync(
                    flow,
                    identity,
                    CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await verification.Value.WaitAsync(cancellationToken);
        }
        catch
        {
            if (verification.IsValueCreated &&
                verification.Value.IsCompleted &&
                !verification.Value.IsCompletedSuccessfully)
            {
                _previewVerifications.TryRemove(
                    new KeyValuePair<
                        string,
                        Lazy<Task<OutcomeCandidateSnapshot>>>(
                        key,
                        verification));
            }
            throw;
        }
    }

    public async Task<OutcomeCandidateSnapshot> VerifyAsync(
        FlowRun flow,
        ReviewedCandidateIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ReviewedCandidateLedger.Validate(identity);
        ReviewedCandidateLedger.ValidateForFlow(flow, identity);
        ReviewedCandidateLedger.ValidateContext(
            flow,
            identity.OutcomeOwnerStepId,
            identity.OutcomeOwnerPlanStepKey,
            flow.OutcomeContractJson);
        if (!string.Equals(
                identity.OutcomeContractHash,
                OutcomeVerificationRules.ComputeSha256(
                    flow.OutcomeContractJson),
                StringComparison.Ordinal))
        {
            throw new CandidateValidationException(
                "The reviewed outcome contract no longer matches its sealed candidate.");
        }
        _ = StudioWorkspaceRepositoryMapLedger.Read(flow);

        var current = await fingerprints.PrepareAsync(
            flow,
            identity.AcceptancePlanHash,
            identity.OutcomeOwnerStepId,
            RequiresCustomerPreview(flow),
            cancellationToken);
        var repositories = current.Manifest.Repositories
            .Select(repository =>
                new ReviewedCandidateRepositoryIdentity(
                    repository.RelativePath,
                    repository.Head,
                    repository.Tree,
                    repository.RemoteRepository))
            .OrderBy(repository => repository.RelativePath, StringComparer.Ordinal)
            .ToArray();
        if (!string.Equals(
                current.Fingerprint,
                identity.Fingerprint,
                StringComparison.Ordinal) ||
            !repositories.SequenceEqual(identity.Repositories) ||
            current.Manifest.TrustedScaffoldFiles.Count !=
                identity.TrustedScaffoldFileCount ||
            current.Manifest.TrustedScaffoldFiles.Sum(item => item.Length) !=
                identity.TrustedScaffoldTotalBytes ||
            current.Manifest.PreviewArtifacts.Count != identity.PreviewFileCount ||
            current.Manifest.PreviewArtifacts.Sum(item => item.Length) !=
                identity.PreviewTotalBytes)
        {
            throw new CandidateValidationException(
                "The Delivery workspace no longer matches the exact candidate sealed for customer review.");
        }
        return current;
    }

    internal static bool RequiresCustomerPreview(FlowRun flow)
    {
        if (flow.Kind != FlowKind.Delivery)
        {
            return false;
        }

        var (plan, _, errors) =
            DeliveryReadinessService.TryReadAcceptancePlan(flow);
        return errors.Count == 0 &&
               plan!.Criteria.Any(criterion =>
                   criterion.CustomerVisible &&
                   criterion.EvidenceKinds?.Any(kind =>
                       kind is OutcomeEvidenceKind.Artifact or
                           OutcomeEvidenceKind.Observation) == true);
    }

    private void CachePreviewVerification(
        ReviewedCandidateIdentity identity,
        OutcomeCandidateSnapshot snapshot)
    {
        _previewVerifications[PreviewVerificationKey(identity)] =
            new Lazy<Task<OutcomeCandidateSnapshot>>(
                () => Task.FromResult(snapshot),
                LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private static string PreviewVerificationKey(
        ReviewedCandidateIdentity identity) =>
        $"{identity.FlowId:D}:{identity.Iteration}:{identity.Fingerprint}";
}

public static class ReviewedCandidateLedger
{
    public const string EventType = "delivery.review-candidate-sealed";
    public const int MaximumDataBytes = 60 * 1024;
    private const int MaximumRepositories = 128;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling =
            System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static string Serialize(ReviewedCandidateIdentity identity)
    {
        Validate(identity);
        var json = JsonSerializer.Serialize(identity, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumDataBytes)
        {
            throw new CandidateValidationException(
                "The reviewed candidate identity exceeds the durable event limit.");
        }
        return json;
    }

    public static ReviewedCandidateIdentity Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json) ||
            Encoding.UTF8.GetByteCount(json) > MaximumDataBytes)
        {
            throw new CandidateValidationException(
                "The durable reviewed candidate identity is empty or oversized.");
        }
        try
        {
            var identity = JsonSerializer.Deserialize<ReviewedCandidateIdentity>(
                               json,
                               JsonOptions)
                           ?? throw new CandidateValidationException(
                               "The durable reviewed candidate identity is empty.");
            Validate(identity);
            return identity;
        }
        catch (JsonException exception)
        {
            throw new CandidateValidationException(
                $"The durable reviewed candidate identity is invalid: {exception.Message}");
        }
    }

    public static ReviewedCandidateIdentity Read(
        FlowRun flow,
        Guid? expectedOutcomeOwnerStepId = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        expectedOutcomeOwnerStepId ??= flow.Steps
            .Where(step =>
                step.Iteration == flow.Iteration &&
                step.IsOutcomeOwner &&
                step.Status == StepStatus.Completed &&
                string.Equals(
                    step.PlanStepKey,
                    flow.OutcomeOwnerPlanStepKey,
                    StringComparison.Ordinal))
            .OrderByDescending(step => step.Sequence)
            .ThenByDescending(step => step.Attempt)
            .Select(step => (Guid?)step.Id)
            .FirstOrDefault();
        var candidates = flow.Events
            .Where(item =>
                item.Type == EventType &&
                item.FlowStepId is not null)
            .Select(item => new
            {
                Event = item,
                Identity = Deserialize(
                    item.DataJson
                    ?? throw new CandidateValidationException(
                        "The reviewed candidate event has no structured identity."))
            })
            .Where(item =>
                item.Identity.Iteration == flow.Iteration)
            .ToArray();
        var events = expectedOutcomeOwnerStepId is { } ownerStepId
            ? candidates
                .Where(item => item.Event.FlowStepId == ownerStepId)
                .ToArray()
            : candidates;
        if (events.Length != 1)
        {
            throw new CandidateValidationException(
                events.Length == 0
                    ? "The current Delivery iteration has no durable reviewed candidate seal."
                    : "The current Delivery iteration has duplicate reviewed candidate seals.");
        }
        var selected = events[0];
        if (selected.Event.FlowStepId !=
            selected.Identity.OutcomeOwnerStepId)
        {
            throw new CandidateValidationException(
                "The reviewed candidate seal is attached to a different outcome owner.");
        }
        ValidateForFlow(flow, selected.Identity);
        return selected.Identity;
    }

    public static ReviewedCandidateIdentity? TryRead(
        FlowRun flow,
        Guid expectedOutcomeOwnerStepId)
    {
        var matching = flow.Events
            .Where(item =>
                item.Type == EventType &&
                item.FlowStepId == expectedOutcomeOwnerStepId)
            .ToArray();
        if (matching.Length == 0)
        {
            return null;
        }
        if (matching.Length > 1)
        {
            throw new CandidateValidationException(
                "The current outcome owner has duplicate reviewed candidate seals.");
        }
        var identity = Deserialize(
            matching[0].DataJson
            ?? throw new CandidateValidationException(
                "The reviewed candidate event has no structured identity."));
        ValidateForFlow(flow, identity);
        return identity;
    }

    public static void ValidateForFlow(
        FlowRun flow,
        ReviewedCandidateIdentity identity)
    {
        Validate(identity);
        if (flow.Kind != FlowKind.Delivery ||
            identity.FlowId != flow.Id ||
            identity.Iteration != flow.Iteration ||
            string.IsNullOrWhiteSpace(flow.OutcomeOwnerPlanStepKey) ||
            !string.Equals(
                identity.OutcomeOwnerPlanStepKey,
                flow.OutcomeOwnerPlanStepKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                identity.OutcomeContractHash,
                OutcomeVerificationRules.ComputeSha256(
                    flow.OutcomeContractJson),
                StringComparison.Ordinal))
        {
            throw new CandidateValidationException(
                "The reviewed candidate identity does not belong to the current Delivery outcome.");
        }
    }

    public static void ValidateContext(
        FlowRun flow,
        Guid outcomeOwnerStepId,
        string outcomeOwnerPlanStepKey,
        string outcomeContractJson)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (flow.Kind != FlowKind.Delivery ||
            flow.Id == Guid.Empty ||
            flow.Iteration < 1 ||
            outcomeOwnerStepId == Guid.Empty ||
            string.IsNullOrWhiteSpace(outcomeOwnerPlanStepKey) ||
            !string.Equals(
                flow.OutcomeOwnerPlanStepKey,
                outcomeOwnerPlanStepKey,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(outcomeContractJson))
        {
            throw new CandidateValidationException(
                "A reviewed candidate can be sealed only for the current completed Delivery outcome owner.");
        }
    }

    public static void Validate(ReviewedCandidateIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.FlowId == Guid.Empty ||
            identity.Iteration < 1 ||
            identity.OutcomeOwnerStepId == Guid.Empty ||
            string.IsNullOrWhiteSpace(identity.OutcomeOwnerPlanStepKey) ||
            !OutcomeVerificationRules.IsSha256(identity.OutcomeContractHash) ||
            !OutcomeVerificationRules.IsSha256(identity.AcceptancePlanHash) ||
            !OutcomeVerificationRules.IsSha256(identity.Fingerprint) ||
            identity.TrustedScaffoldFileCount < 0 ||
            identity.TrustedScaffoldTotalBytes < 0 ||
            identity.PreviewFileCount < 0 ||
            identity.PreviewTotalBytes < 0 ||
            identity.Repositories is null ||
            identity.Repositories.Count is < 1 or > MaximumRepositories ||
            identity.Repositories.Any(repository =>
                string.IsNullOrWhiteSpace(repository.RelativePath) ||
                repository.RelativePath.Length > 1_024 ||
                string.IsNullOrWhiteSpace(repository.Head) ||
                string.IsNullOrWhiteSpace(repository.Tree) ||
                !IsGitObjectId(repository.Head) ||
                !IsGitObjectId(repository.Tree) ||
                repository.RemoteRepository.Length > 512) ||
            identity.Repositories
                .Select(repository => repository.RelativePath)
                .Distinct(StringComparer.Ordinal)
                .Count() != identity.Repositories.Count)
        {
            throw new CandidateValidationException(
                "The durable reviewed candidate identity is invalid.");
        }
    }

    private static bool IsGitObjectId(string value) =>
        value.Length is 40 or 64 &&
        value.All(character =>
            char.IsAsciiHexDigit(character) &&
            !char.IsAsciiLetterUpper(character));
}

public static class StudioWorkspaceRepositoryMapLedger
{
    public const string EventType = "workspace.studio-repositories-recorded";
    private const int MaximumDataBytes = 48 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling =
            System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static StudioWorkspaceRepositoryMap Create(
        FlowRun flow,
        string workspacePath,
        IReadOnlyList<WorkspaceRepositoryIdentity> repositories)
    {
        var map = new StudioWorkspaceRepositoryMap(
            flow.Id,
            Path.GetFullPath(workspacePath),
            repositories
                .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
                .ToArray());
        Validate(map, flow);
        return map;
    }

    public static string Serialize(StudioWorkspaceRepositoryMap map)
    {
        Validate(map, flow: null);
        var json = JsonSerializer.Serialize(map, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumDataBytes)
        {
            throw new CandidateValidationException(
                "The trusted studio workspace repository map is too large.");
        }
        return json;
    }

    public static StudioWorkspaceRepositoryMap Read(FlowRun flow)
    {
        var events = flow.Events
            .Where(item => item.Type == EventType)
            .ToArray();
        if (events.Length != 1)
        {
            throw new CandidateValidationException(
                events.Length == 0
                    ? "The studio Delivery workspace has no durable trusted repository map."
                    : "The studio Delivery workspace has duplicate trusted repository maps.");
        }
        try
        {
            var data = events[0].DataJson;
            if (string.IsNullOrWhiteSpace(data) ||
                Encoding.UTF8.GetByteCount(data) > MaximumDataBytes)
            {
                throw new CandidateValidationException(
                    "The trusted studio workspace repository map is empty or oversized.");
            }
            var map = JsonSerializer.Deserialize<StudioWorkspaceRepositoryMap>(
                          data,
                          JsonOptions)
                      ?? throw new CandidateValidationException(
                          "The trusted studio workspace repository map is empty.");
            Validate(map, flow);
            return map;
        }
        catch (JsonException exception)
        {
            throw new CandidateValidationException(
                $"The trusted studio workspace repository map is invalid: {exception.Message}");
        }
    }

    private static void Validate(
        StudioWorkspaceRepositoryMap map,
        FlowRun? flow)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.FlowId == Guid.Empty ||
            string.IsNullOrWhiteSpace(map.WorkspacePath) ||
            !Path.IsPathFullyQualified(map.WorkspacePath) ||
            map.Repositories is null ||
            map.Repositories.Count is < 1 or > 128 ||
            map.Repositories.Any(item =>
                string.IsNullOrWhiteSpace(item.RelativePath) ||
                item.RelativePath.Length > 1_024 ||
                item.RemoteRepository.Length > 512) ||
            map.Repositories
                .Select(item => item.RelativePath)
                .Distinct(StringComparer.Ordinal)
                .Count() != map.Repositories.Count)
        {
            throw new CandidateValidationException(
                "The trusted studio workspace repository map is invalid.");
        }
        if (flow is not null &&
            (map.FlowId != flow.Id ||
             !string.Equals(
                 Path.TrimEndingDirectorySeparator(
                     Path.GetFullPath(map.WorkspacePath)),
                 Path.TrimEndingDirectorySeparator(
                     Path.GetFullPath(flow.WorkspacePath)),
                 OperatingSystem.IsWindows()
                     ? StringComparison.OrdinalIgnoreCase
                     : StringComparison.Ordinal)))
        {
            throw new CandidateValidationException(
                "The trusted repository map belongs to a different flow workspace.");
        }
    }
}

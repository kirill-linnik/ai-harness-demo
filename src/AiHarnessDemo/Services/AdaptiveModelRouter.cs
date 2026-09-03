using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Data;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

public sealed record ModelCandidateKey(string Model, string Effort);

public sealed record RoutingRequest(
    Guid FlowStepId,
    ModelSelectionStrategy Strategy,
    IReadOnlyCollection<ModelCandidateKey>? ExcludedCandidates = null,
    bool SupersedeExisting = false);

public interface IModelRouter
{
    Task<RoutingDecision> SelectAsync(
        RoutingRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// router-v1: hierarchical recency-weighted Beta quality estimation with deterministic,
/// bounded exploration and lexicographic strategy objectives.
/// </summary>
public sealed class AdaptiveModelRouter(
    IDbContextFactory<HarnessDbContext> databaseFactory,
    ModelCatalogDiscovery catalogDiscovery,
    TimeProvider timeProvider) : IModelRouter
{
    public const string AlgorithmVersion = "router-v1";
    private const double HalfLifeDays = 90;
    private static readonly TimeSpan ModelAvailabilityCooldown = TimeSpan.FromMinutes(30);

    public async Task<RoutingDecision> SelectAsync(
        RoutingRequest request,
        CancellationToken cancellationToken = default)
    {
        var catalogStatus = catalogDiscovery.Current;
        if (!catalogStatus.Ready || catalogStatus.SnapshotId is null)
        {
            throw new InvalidOperationException(catalogStatus.Detail);
        }

        await using var database =
            await databaseFactory.CreateDbContextAsync(cancellationToken);
        var existing = await database.RoutingDecisions
            .Include(item => item.Alternatives)
            .Where(item =>
                item.FlowStepId == request.FlowStepId &&
                !item.Superseded)
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null && !request.SupersedeExisting)
        {
            var stillAvailable = await database.ModelCatalogCandidates
                .AsNoTracking()
                .AnyAsync(
                    item =>
                        item.ModelCatalogSnapshotId == catalogStatus.SnapshotId.Value &&
                        item.Enabled &&
                        item.Model == existing.SelectedModel &&
                        item.Effort == existing.SelectedEffort,
                    cancellationToken);
            if (!stillAvailable)
            {
                throw new InvalidOperationException(
                    $"The persisted route {existing.SelectedModel}/{existing.SelectedEffort} " +
                    "is not available in the current Copilot ACP catalog. The interrupted " +
                    "execution cannot be rerouted safely.");
            }
            return existing;
        }

        var step = await database.FlowSteps.SingleOrDefaultAsync(
                       item => item.Id == request.FlowStepId,
                       cancellationToken)
                   ?? throw new KeyNotFoundException(
                       $"Flow step '{request.FlowStepId}' was not found.");
        var profile = await database.TaskProfiles.SingleOrDefaultAsync(
            item => item.FlowStepId == step.Id,
            cancellationToken);
        if (profile is null)
        {
            profile = await database.TaskProfiles
                .Where(item =>
                    item.FlowRunId == step.FlowRunId &&
                    item.Iteration == step.Iteration &&
                    item.Role == step.AgentRole &&
                    item.FlowStepId == null)
                .OrderBy(item => item.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (profile is not null)
            {
                profile.FlowStepId = step.Id;
            }
            else
            {
                var source = await database.TaskProfiles
                    .AsNoTracking()
                    .Where(item =>
                        item.FlowRunId == step.FlowRunId &&
                        item.Iteration == step.Iteration &&
                        item.Role == step.AgentRole)
                    .OrderByDescending(item => item.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"No validated task profile exists for role '{step.AgentRole}'.");
                profile = TaskProfileRules.CopyForStep(source, step.Id);
                database.TaskProfiles.Add(profile);
            }
            await database.SaveChangesAsync(cancellationToken);
        }

        var candidates = await database.ModelCatalogCandidates
            .AsNoTracking()
            .Where(item =>
                item.ModelCatalogSnapshotId == catalogStatus.SnapshotId &&
                item.Enabled)
            .OrderBy(item => item.ModelOrder)
            .ThenBy(item => item.EffortOrder)
            .ToListAsync(cancellationToken);
        var excluded = request.ExcludedCandidates?.ToHashSet() ?? [];
        var availabilityCutoff = timeProvider.GetUtcNow() - ModelAvailabilityCooldown;
        var unavailableRows = await (
                from observation in database.RoutingObservations.AsNoTracking()
                join unavailableDecision in database.RoutingDecisions.AsNoTracking()
                    on observation.RoutingDecisionId equals unavailableDecision.Id
                where observation.AvailabilityFailure &&
                      observation.OutcomeKind == "model-unavailable" &&
                      observation.ObservedAt >= availabilityCutoff
                select new
                {
                    unavailableDecision.SelectedModel,
                    unavailableDecision.SelectedEffort
                })
            .Distinct()
            .ToListAsync(cancellationToken);
        excluded.UnionWith(unavailableRows.Select(item =>
            new ModelCandidateKey(item.SelectedModel, item.SelectedEffort)));
        candidates = candidates
            .Where(candidate =>
                !excluded.Contains(new ModelCandidateKey(candidate.Model, candidate.Effort)))
            .ToList();
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "No enabled discovered model/effort candidate remains after availability exclusions.");
        }

        var observations = await (
                from observation in database.RoutingObservations.AsNoTracking()
                join observedDecision in database.RoutingDecisions.AsNoTracking()
                    on observation.RoutingDecisionId equals observedDecision.Id
                join observedStep in database.FlowSteps.AsNoTracking()
                    on observation.FlowStepId equals observedStep.Id
                where !observation.AvailabilityFailure
                select new Evidence(
                    observedDecision.SelectedModel,
                    observedDecision.SelectedEffort,
                    observedStep.FlowRunId,
                    observation.Role,
                    observation.TaskTypeTagsJson,
                    observation.Complexity,
                    observation.ReasoningDepth,
                    observation.ContextDemand,
                    observation.ToolIntensity,
                    observation.Risk,
                    observation.Accepted,
                    observation.EvidenceWeight,
                    observation.DurationMilliseconds,
                    observation.ExecutionAttempts,
                    observation.EstimatedPremiumRequests,
                    observation.OutcomeKind,
                    observation.ObservedAt))
            .ToListAsync(cancellationToken);
        var scored = candidates
            .Select(candidate => Score(candidate, profile, observations, timeProvider.GetUtcNow()))
            .ToList();
        var selected = SelectCandidate(
            scored,
            request.Strategy,
            profile.Risk,
            request.FlowStepId);

        if (existing is not null)
        {
            existing.Superseded = true;
        }
        var decision = new RoutingDecision
        {
            FlowStepId = step.Id,
            TaskProfileId = profile.Id,
            ModelCatalogSnapshotId = catalogStatus.SnapshotId.Value,
            SupersedesRoutingDecisionId = existing?.Id,
            RerouteCount = (existing?.RerouteCount ?? -1) + 1,
            SelectedModel = selected.Score.Model,
            SelectedEffort = selected.Score.Effort,
            Strategy = request.Strategy,
            PredictedQuality = selected.Score.ConservativeQuality,
            PredictedAcceptedTimeSeconds = selected.Score.ExpectedAcceptedTimeSeconds,
            PredictedPremiumRequests = selected.Score.ExpectedPremiumRequests,
            Confidence = selected.Score.Confidence,
            Uncertainty = selected.Score.Uncertainty,
            Exploration = selected.Exploration,
            Reason = selected.Reason,
            AlgorithmVersion = AlgorithmVersion,
            CreatedAt = timeProvider.GetUtcNow()
        };
        var eligibleRank = Rank(scored, request.Strategy, profile.Risk);
        var bestQuality = scored.Max(item => item.ConservativeQuality);
        var qualityFloor = bestQuality * QualityFloor(profile.Risk);
        var rankedAlternatives = eligibleRank
            .Where(item =>
                item.Model != selected.Score.Model ||
                item.Effort != selected.Score.Effort)
            .Concat(scored
                .Where(item => item.ConservativeQuality + 1e-9 < qualityFloor)
                .OrderByDescending(item => item.ConservativeQuality))
            .Take(3)
            .Select((item, index) => new RoutingAlternative
            {
                Model = item.Model,
                Effort = item.Effort,
                Rank = index + 1,
                PredictedQuality = item.ConservativeQuality,
                PredictedAcceptedTimeSeconds = item.ExpectedAcceptedTimeSeconds,
                PredictedPremiumRequests = item.ExpectedPremiumRequests,
                Confidence = item.Confidence,
                Reason = AlternativeReason(item, qualityFloor)
            });
        decision.Alternatives.AddRange(rankedAlternatives);
        database.RoutingDecisions.Add(decision);
        step.Model = decision.SelectedModel;
        step.ModelEffort = decision.SelectedEffort;
        step.ModelReason = decision.Reason;
        await database.SaveChangesAsync(cancellationToken);
        return decision;
    }

    internal static Selection SelectCandidate(
        IReadOnlyList<CandidateScore> scores,
        ModelSelectionStrategy strategy,
        TaskRisk risk,
        Guid stepId)
    {
        var ranked = Rank(scores, strategy, risk);
        var selected = ranked[0];
        var allowsExploration = risk is TaskRisk.Low or TaskRisk.Medium;
        var bucket = DeterministicBucket(stepId);
        if (!allowsExploration || bucket >= 0.05 || ranked.Count < 2)
        {
            return new Selection(
                selected,
                false,
                $"Selected by {strategy} after the {risk} risk quality floor.");
        }

        var uncertain = ranked
            .Skip(1)
            .OrderByDescending(item => item.Uncertainty)
            .ThenBy(item => item.Model, StringComparer.Ordinal)
            .ThenBy(item => item.Effort, StringComparer.Ordinal)
            .First();
        if (uncertain.Uncertainty <= selected.Uncertainty)
        {
            return new Selection(
                selected,
                false,
                $"Selected by {strategy}; no eligible alternative had greater uncertainty.");
        }
        return new Selection(
            uncertain,
            true,
            $"Deterministic router-v1 exploration selected an uncertain quality-floor-eligible alternative ({bucket:P2} bucket).");
    }

    internal static List<CandidateScore> Rank(
        IReadOnlyList<CandidateScore> scores,
        ModelSelectionStrategy strategy,
        TaskRisk risk)
    {
        if (scores.Count == 0)
        {
            throw new InvalidOperationException("The router received no candidate scores.");
        }
        var bestQuality = scores.Max(item => item.ConservativeQuality);
        var qualityFloor = bestQuality * QualityFloor(risk);
        var eligible = scores
            .Where(item => item.ConservativeQuality + 1e-9 >= qualityFloor)
            .ToList();
        IOrderedEnumerable<CandidateScore> ordered = strategy switch
        {
            ModelSelectionStrategy.MaximumQuality => eligible
                .OrderByDescending(item => item.ConservativeQuality)
                .ThenBy(item => item.ExpectedAcceptedTimeSeconds)
                .ThenBy(item => item.ExpectedPremiumRequests),
            ModelSelectionStrategy.FastestResponse => eligible
                .OrderBy(item => item.ExpectedAcceptedTimeSeconds)
                .ThenByDescending(item => item.ConservativeQuality)
                .ThenBy(item => item.ExpectedPremiumRequests),
            ModelSelectionStrategy.LowestCost => eligible
                .OrderBy(item => item.ExpectedPremiumRequests)
                .ThenByDescending(item => item.ConservativeQuality)
                .ThenBy(item => item.ExpectedAcceptedTimeSeconds),
            _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, null)
        };
        return ordered
            .ThenBy(item => item.Model, StringComparer.Ordinal)
            .ThenBy(item => item.Effort, StringComparer.Ordinal)
            .ToList();
    }

    internal static double QualityFloor(TaskRisk risk) => risk switch
    {
        TaskRisk.Low => 0.85,
        TaskRisk.Medium => 0.90,
        TaskRisk.High => 0.95,
        TaskRisk.Critical => 1.00,
        _ => throw new ArgumentOutOfRangeException(nameof(risk), risk, null)
    };

    private static CandidateScore Score(
        ModelCatalogCandidate candidate,
        TaskProfile profile,
        IReadOnlyList<Evidence> evidence,
        DateTimeOffset now)
    {
        var priorMean = Math.Clamp(
            0.70 +
            (candidate.IsDefaultModel ? 0.025 : 0) +
            (candidate.IsDefaultEffort ? 0.015 : 0) -
            (candidate.ModelOrder * 0.004) +
            (candidate.EffortOrder * 0.006),
            0.55,
            0.88);
        const double priorStrength = 2;
        var alpha = priorMean * priorStrength;
        var beta = (1 - priorMean) * priorStrength;
        var durationWeight = 0.0;
        var durationTotal = 0.0;
        var premiumWeight = 0.0;
        var premiumTotal = 0.0;
        var weightedEvidence = evidence
            .Where(item => item.Model == candidate.Model)
            .Select(item =>
            {
                var similarity = Similarity(profile, item);
                var effortSimilarity = string.Equals(
                    item.Effort,
                    candidate.Effort,
                    StringComparison.Ordinal)
                    ? 1.0
                    : 0.25;
                var ageDays = Math.Max(0, (now - item.ObservedAt).TotalDays);
                var weight =
                    similarity *
                    effortSimilarity *
                    Math.Pow(0.5, ageDays / HalfLifeDays) *
                    item.EvidenceWeight;
                return new WeightedEvidence(item, weight);
            })
            .Where(item => item.Weight > 0)
            .ToList();
        foreach (var flowEvidence in weightedEvidence.GroupBy(item => item.Item.FlowRunId))
        {
            var flowWeight = flowEvidence.Sum(item => item.Weight);
            var flowScale = flowWeight <= 2 ? 1 : 2 / flowWeight;
            foreach (var weighted in flowEvidence)
            {
                var item = weighted.Item;
                var weight = weighted.Weight * flowScale;
                if (item.Accepted is true)
                {
                    alpha += weight;
                }
                else if (item.Accepted is false)
                {
                    beta += weight;
                }
                if (item.DurationMilliseconds > 0)
                {
                    durationTotal +=
                        (item.DurationMilliseconds / 1000.0) *
                        weight;
                    durationWeight += weight;
                }
                if (item.EstimatedPremiumRequests >= 0)
                {
                    premiumTotal += item.EstimatedPremiumRequests * weight;
                    premiumWeight += weight;
                }
            }
        }

        var total = alpha + beta;
        var mean = alpha / total;
        var standardDeviation = Math.Sqrt(alpha * beta / (total * total * (total + 1)));
        var conservative = Math.Clamp(mean - (1.28 * standardDeviation), 0.01, 0.999);
        var confidence = Math.Clamp((total - priorStrength) / ((total - priorStrength) + 6), 0, 1);
        var uncertainty = 1 - confidence;
        var duration = durationWeight > 0
            ? durationTotal / durationWeight
            : 45 * (1 + candidate.EffortOrder * 0.18 + candidate.ModelOrder * 0.03);
        var premium = premiumWeight > 0
            ? premiumTotal / premiumWeight
            : candidate.PremiumMultiplier ?? (1 + candidate.ModelOrder * 0.05);
        return new CandidateScore(
            candidate.Model,
            candidate.Effort,
            conservative,
            duration / Math.Max(mean, 0.05),
            premium / Math.Max(mean, 0.05),
            confidence,
            uncertainty);
    }

    private static double Similarity(TaskProfile profile, Evidence evidence)
    {
        var roleSimilarity = string.Equals(profile.Role, evidence.Role, StringComparison.Ordinal)
            ? 1.0
            : 0;
        var profileTags = TaskProfileRules.ReadTags(profile)
            .Select(tag => tag.ToString())
            .ToHashSet(StringComparer.Ordinal);
        var evidenceTags = JsonSerializer.Deserialize<string[]>(evidence.TagsJson) ?? [];
        var overlap = evidenceTags.Count(tag => profileTags.Contains(tag));
        var union = profileTags.Union(evidenceTags, StringComparer.Ordinal).Count();
        var tagSimilarity = union == 0 ? 0 : (double)overlap / union;
        var numericDistance = (
            Math.Abs(profile.Complexity - evidence.Complexity) +
            Math.Abs(profile.ReasoningDepth - evidence.ReasoningDepth) +
            Math.Abs(profile.ContextDemand - evidence.ContextDemand) +
            Math.Abs(profile.ToolIntensity - evidence.ToolIntensity)) / 36.0;
        var numericSimilarity = 1 - numericDistance;
        var riskDistance = Math.Abs((int)profile.Risk - (int)evidence.Risk);
        var riskSimilarity = 1 - riskDistance / 3.0;
        return
            (0.40 * roleSimilarity) +
            (0.25 * tagSimilarity) +
            (0.25 * numericSimilarity) +
            (0.10 * riskSimilarity);
    }

    private static double DeterministicBucket(Guid stepId)
    {
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{stepId:D}:{AlgorithmVersion}"));
        var value = BitConverter.ToUInt64(bytes, 0);
        return value / ((double)ulong.MaxValue + 1);
    }

    private static string AlternativeReason(
        CandidateScore alternative,
        double qualityFloor) =>
        alternative.ConservativeQuality + 1e-9 < qualityFloor
            ? "Rejected below the risk-adjusted conservative quality floor."
            : "Eligible but ranked behind the selected candidate under the configured lexicographic objective.";

    internal sealed record CandidateScore(
        string Model,
        string Effort,
        double ConservativeQuality,
        double ExpectedAcceptedTimeSeconds,
        double ExpectedPremiumRequests,
        double Confidence,
        double Uncertainty);

    internal sealed record Selection(CandidateScore Score, bool Exploration, string Reason);

    private sealed record Evidence(
        string Model,
        string Effort,
        Guid FlowRunId,
        string Role,
        string TagsJson,
        int Complexity,
        int ReasoningDepth,
        int ContextDemand,
        int ToolIntensity,
        TaskRisk Risk,
        bool? Accepted,
        double EvidenceWeight,
        long DurationMilliseconds,
        int ExecutionAttempts,
        double EstimatedPremiumRequests,
        string OutcomeKind,
        DateTimeOffset ObservedAt);

    private sealed record WeightedEvidence(Evidence Item, double Weight);
}

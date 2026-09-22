using System.Text.RegularExpressions;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Data;
using AiHarnessDemo.Services;

namespace AiHarnessDemo.Tests;

/// <summary>
/// Shared deterministic fixtures for the host-derived Delivery readiness contracts.
///
/// The scripted verification agents build their strict outcome-QA document by reading the
/// acceptance plan hash, the criterion identifiers, and the host-issued evidence identifiers back
/// out of the prompt the host actually rendered. Nothing is pre-shared with the runner, so a test
/// only passes when the production prompt really carries those host-computed values.
/// </summary>
internal static class DeliveryReadinessFixtures
{
    public const string DefaultRequirement =
        "The customer-visible result satisfies the confirmed brief.";

    private static readonly Regex PlanHashPattern = new(
        "sha256:[0-9a-f]{64}",
        RegexOptions.CultureInvariant);
    private static readonly Regex CriterionPattern = new(
        @"(?m)^- (AC-[0-9]{3}) \(",
        RegexOptions.CultureInvariant);
    private static readonly Regex EvidencePattern = new(
        @"(?m)^- (EV-S[0-9]{3}-[0-9]{3}) \[[^\]]*supportsVerification=true",
        RegexOptions.CultureInvariant);
    private static readonly Regex CurrentEvidencePrefixPattern = new(
        @"(?m)^Current verification step evidence prefix: (EV-S[0-9]{3}-)\r?$",
        RegexOptions.CultureInvariant);

    public static IReadOnlyList<DeliveryAcceptanceCriterion> Criteria(
        int count = 1,
        params string[] requirements) =>
        [.. Enumerable.Range(1, count).Select(index =>
            new DeliveryAcceptanceCriterion
            {
                Id = $"AC-{index:000}",
                Requirement = requirements.Length >= index
                    ? requirements[index - 1]
                    : $"{DefaultRequirement} ({index})",
                Verification =
                    "Inspect the reviewed candidate and confirm the observable behavior.",
                OwnerRoles = ["external-delivery"],
                EvidenceKinds = [OutcomeEvidenceKind.Observation],
                CustomerVisible = true
            })];

    public static DeliveryAcceptancePlan Plan(int count = 1) =>
        new(Criteria(count));

    public static string PlanHash(int count = 1) =>
        DeliveryReadinessPolicy.HashAcceptancePlan(Plan(count));

    /// <summary>The acceptance plan hash the host rendered into this turn's prompt.</summary>
    public static string PlanHashFromPrompt(string? prompt)
    {
        var match = PlanHashPattern.Match(prompt ?? string.Empty);
        return match.Success ? match.Value : string.Empty;
    }

    /// <summary>The criterion identifiers the host rendered into this turn's prompt.</summary>
    public static IReadOnlyList<string> CriterionIdsFromPrompt(string? prompt) =>
        [.. CriterionPattern.Matches(prompt ?? string.Empty)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)];

    /// <summary>The host-issued evidence identifiers the host rendered into this turn's prompt.</summary>
    public static IReadOnlyList<string> EvidenceIdsFromPrompt(string? prompt)
    {
        var evidence = EvidencePattern.Matches(prompt ?? string.Empty)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (evidence.Length > 0)
        {
            return evidence;
        }
        var currentPrefix = CurrentEvidencePrefixPattern.Match(
            prompt ?? string.Empty);
        return currentPrefix.Success
            ? [$"{currentPrefix.Groups[1].Value}001"]
            : [];
    }

    public static string CurrentEvidenceIdFromPrompt(string? prompt)
    {
        var currentPrefix = CurrentEvidencePrefixPattern.Match(
            prompt ?? string.Empty);
        return currentPrefix.Success
            ? $"{currentPrefix.Groups[1].Value}001"
            : "EV-MISSING";
    }

    /// <summary>
    /// Builds a strict outcome-QA block purely from what the host injected. When the host
    /// injected nothing, the block is deliberately unusable so the turn fails closed.
    /// </summary>
    public static string QaBlockFromPrompt(
        string? prompt,
        DeliveryCriterionOutcome outcome = DeliveryCriterionOutcome.Verified,
        IReadOnlyList<(string RiskId, DeliveryRiskClassification Classification)>? risks = null,
        string? verdictOverride = null,
        IReadOnlyList<string>? evidenceOverride = null,
        string? planHashOverride = null)
    {
        var planHash = planHashOverride ?? PlanHashFromPrompt(prompt);
        var criterionIds = CriterionIdsFromPrompt(prompt);
        var evidence = evidenceOverride ?? EvidenceIdsFromPrompt(prompt);
        var evidenceReference = evidence.Count > 0 ? evidence[0] : "EV-MISSING";
        var criteria = string.Join(
            ",",
            criterionIds.Select(criterionId =>
                $$"""
                {"CriterionId":"{{criterionId}}","Outcome":"{{outcome}}","EvidenceIds":["{{evidenceReference}}"],"Rationale":"The host-observed check produced the expected result.","Remediation":{{(outcome == DeliveryCriterionOutcome.Verified ? "null" : "\"Correct the failing behavior and re-verify.\"")}},"ResponsibleRoles":{{(outcome == DeliveryCriterionOutcome.Verified ? "[]" : "[\"external-delivery\"]")}}}
                """));
        var riskItems = string.Join(
            ",",
            (risks ?? []).Select(risk =>
                $$"""
                {"RiskId":"{{risk.RiskId}}","Classification":"{{risk.Classification}}","Severity":"Medium","Statement":"A disclosed residual risk remains after verification.","Impact":"The customer may observe a bounded degradation.","EvidenceIds":["{{evidenceReference}}"],"CriterionIds":[],"PreMortemFindingId":null}
                """));
        var derived = DerivedVerdict(criterionIds.Count, outcome, risks);
        return
            $"{DeliveryReadinessPolicy.QaBeginMarker}{Environment.NewLine}" +
            $$"""
            {"AcceptancePlanHash":"{{planHash}}","Verdict":"{{verdictOverride ?? derived}}","Criteria":[{{criteria}}],"ResidualRisks":[{{riskItems}}],"PlanGaps":[]}
            """ +
            $"{Environment.NewLine}{DeliveryReadinessPolicy.QaEndMarker}";
    }

    private static string DerivedVerdict(
        int criterionCount,
        DeliveryCriterionOutcome outcome,
        IReadOnlyList<(string RiskId, DeliveryRiskClassification Classification)>? risks)
    {
        if (outcome == DeliveryCriterionOutcome.Blocked ||
            (risks ?? []).Any(risk =>
                risk.Classification == DeliveryRiskClassification.Blocking))
        {
            return "BLOCKED";
        }
        return criterionCount > 0 && outcome == DeliveryCriterionOutcome.Verified
            ? "PASS"
            : "FAIL";
    }

    /// <summary>
    /// Seeds the durable readiness rows a publication-authorization test needs. The snapshot is a
    /// real derivation, so the persisted hash is the same value production would compute.
    /// </summary>
    public static async Task<DeliveryReadinessSnapshotRecord> SeedReadyToApproveAsync(
        HarnessDbContext database,
        FlowRun flow,
        ReviewedCandidateIdentity identity,
        Guid qaStepId,
        CancellationToken cancellationToken = default)
    {
        var planHash = PlanHash();
        var contract = DeliveryReadinessPolicy.Derive(
            new DeliveryReadinessDerivationInput(
                flow.Id,
                flow.Iteration,
                1,
                Plan(),
                planHash,
                QaDocument(),
                OutcomeVerificationRules.ComputeSha256($"qa:{flow.Id:D}:{flow.Iteration}"),
                qaStepId,
                "quality-engineer",
                identity.OutcomeOwnerStepId,
                identity.OutcomeContractHash,
                identity.Fingerprint,
                [],
                KnownEvidence:
                [
                    new DeliveryEvidenceItem(
                        "EV-S010-001",
                        OutcomeEvidenceKind.Observation,
                        "reviewed-preview",
                        "The host observed the expected result.",
                        SupportsVerification: true,
                        ExitCode: null,
                        ResultDigest: string.Empty)
                ],
                [],
                [],
                DateTimeOffset.UtcNow,
                Guid.NewGuid()));
        if (contract.State != DeliveryReadinessState.ReadyToApprove)
        {
            throw new InvalidOperationException(
                "The readiness fixture must derive ReadyToApprove: " +
                string.Join("; ", contract.Diagnostics));
        }
        var hash = DeliveryReadinessPolicy.HashSnapshot(contract);
        var record = new DeliveryReadinessSnapshotRecord
        {
            Id = contract.Id,
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Revision = 1,
            State = contract.State,
            CandidateFingerprint = identity.Fingerprint,
            AcceptancePlanHash = planHash,
            OutcomeContractHash = identity.OutcomeContractHash,
            QaContractHash = contract.QaContractHash,
            OutcomeOwnerStepId = identity.OutcomeOwnerStepId,
            QaStepId = qaStepId,
            ContractJson = DeliveryReadinessPolicy.SerializeSnapshot(contract),
            ContractHash = hash
        };
        database.DeliveryReadinessSnapshots.Add(record);
        database.ReviewedCandidateRecords.Add(new ReviewedCandidateRecord
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            CandidateFingerprint = identity.Fingerprint,
            OutcomeOwnerStepId = identity.OutcomeOwnerStepId,
            OutcomeContractHash = identity.OutcomeContractHash,
            AcceptancePlanHash = planHash,
            ReadinessSnapshotId = record.Id,
            ReadinessContractHash = hash,
            IdentityJson = ReviewedCandidateLedger.Serialize(identity)
        });
        await database.SaveChangesAsync(cancellationToken);
        return record;
    }

    private static DeliveryQaDocument QaDocument() =>
        new()
        {
            AcceptancePlanHash = PlanHash(),
            Verdict = OutcomeQaVerdict.PASS,
            Criteria =
            [
                new DeliveryQaCriterionDocument
                {
                    CriterionId = "AC-001",
                    Outcome = DeliveryCriterionOutcome.Verified,
                    EvidenceIds = ["EV-S010-001"],
                    Rationale = "The host-observed check produced the expected result.",
                    Remediation = null,
                    ResponsibleRoles = []
                }
            ],
            ResidualRisks = [],
            PlanGaps = []
        };
}

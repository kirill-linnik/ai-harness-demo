using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AiHarnessDemo.Core.Verification;

/// <summary>
/// Host-derived outcome of one acceptance criterion. The value is derived from the strict
/// strict verification contract and never from prose, a handoff marker, or a claim.
/// </summary>
public enum DeliveryCriterionOutcome
{
    Verified,
    Failed,
    Blocked
}

/// <summary>
/// Typed disposition of one residual risk. Failed or blocked acceptance criteria are never
/// reclassified as residual risk, so this enum can never be used to escape a criterion result.
/// </summary>
public enum DeliveryRiskClassification
{
    NonBlockingDisclosure,
    WaiverRequired,
    Blocking
}

public enum DeliveryRiskSeverity
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>
/// The four customer-visible readiness states. Only <see cref="ReadyToApprove"/> permits an
/// ordinary customer review, acceptance, or publication authorization.
/// </summary>
public enum DeliveryReadinessState
{
    ReadyToApprove,
    NeedsCustomerWaiver,
    NeedsRefinement,
    Blocked
}

/// <summary>Server-allowed customer actions for a derived readiness state.</summary>
public enum DeliveryReadinessAction
{
    None,
    Accept,
    RequestRefinement,
    GrantWaiver,
    Continue,
    Replan,
    Abandon
}

/// <summary>Stable machine-readable conflict codes returned to clients as RFC 9457 problems.</summary>
public static class DeliveryReadinessConflicts
{
    public const string NotReady = "readiness.not-ready";
    public const string WaiverRequired = "readiness.waiver-required";
    public const string WaiverNotApplicable = "readiness.waiver-not-applicable";
    public const string CandidateStale = "readiness.candidate-stale";
    public const string ReviewStale = "readiness.review-stale";
    public const string BindingInvalid = "readiness.binding-invalid";
    public const string PublicationNotAuthorized = "readiness.publication-not-authorized";
    public const string ContractInvalid = "readiness.contract-invalid";
}

/// <summary>
/// A typed readiness conflict. It carries the stable code plus the current authoritative state and
/// revision so a stale browser tab can refresh instead of resolving a gate it can no longer see.
/// </summary>
public sealed class DeliveryReadinessConflictException(
    string code,
    string message,
    DeliveryReadinessState? state = null,
    int? revision = null,
    string? contractHash = null)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;

    public DeliveryReadinessState? State { get; } = state;

    public int? Revision { get; } = revision;

    public string? ContractHash { get; } = contractHash;
}

public sealed class DeliveryReadinessContractException(IReadOnlyList<string> errors)
    : InvalidOperationException(
        "Delivery readiness contract validation failed: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>One planned, customer-visible acceptance criterion with a stable identity.</summary>
public sealed class DeliveryAcceptanceCriterion
{
    public string Id { get; init; } = string.Empty;

    public string Requirement { get; init; } = string.Empty;

    public string Verification { get; init; } = string.Empty;

    public IReadOnlyList<string>? OwnerRoles { get; init; }

    public IReadOnlyList<OutcomeEvidenceKind>? EvidenceKinds { get; init; }

    public bool CustomerVisible { get; init; }
}

/// <summary>
/// The mandatory Delivery acceptance plan. It is authored during planning, hashed by the
/// host, and is the only permitted criterion namespace for later verification.
/// </summary>
public sealed record DeliveryAcceptancePlan(
    IReadOnlyList<DeliveryAcceptanceCriterion> Criteria);

public sealed class DeliveryQaCriterionDocument
{
    public string CriterionId { get; init; } = string.Empty;

    public DeliveryCriterionOutcome? Outcome { get; init; }

    public IReadOnlyList<string>? EvidenceIds { get; init; }

    public string Rationale { get; init; } = string.Empty;

    public string? Remediation { get; init; }

    public IReadOnlyList<string>? ResponsibleRoles { get; init; }
}

public sealed class DeliveryResidualRiskDocument
{
    public string RiskId { get; init; } = string.Empty;

    public DeliveryRiskClassification? Classification { get; init; }

    public DeliveryRiskSeverity? Severity { get; init; }

    public string Statement { get; init; } = string.Empty;

    public string Impact { get; init; } = string.Empty;

    public IReadOnlyList<string>? EvidenceIds { get; init; }

    public IReadOnlyList<string>? CriterionIds { get; init; }

    public string? PreMortemFindingId { get; init; }
}

public sealed class DeliveryPlanGapDocument
{
    public string Requirement { get; init; } = string.Empty;

    public string Verification { get; init; } = string.Empty;

    public IReadOnlyList<string>? OwnerRoles { get; init; }

    public string Rationale { get; init; } = string.Empty;
}

/// <summary>
/// The strict verification document. It must contain exactly one result for every planned
/// acceptance criterion. The agent-supplied verdict is compared against the host derivation and is
/// never itself an authorization.
/// </summary>
public sealed class DeliveryQaDocument
{
    public string AcceptancePlanHash { get; init; } = string.Empty;

    public OutcomeQaVerdict? Verdict { get; init; }

    public IReadOnlyList<DeliveryQaCriterionDocument>? Criteria { get; init; }

    public IReadOnlyList<DeliveryResidualRiskDocument>? ResidualRisks { get; init; }

    public IReadOnlyList<DeliveryPlanGapDocument>? PlanGaps { get; init; }
}

public sealed record ParsedDeliveryQaDocument(
    DeliveryQaDocument Document,
    string RawJson,
    string ContractHash);

/// <summary>One host-observed fact that a verification result may cite.</summary>
public sealed record DeliveryEvidenceItem(
    string EvidenceId,
    OutcomeEvidenceKind Kind,
    string Locator,
    string Summary,
    bool SupportsVerification,
    int? ExitCode,
    string ResultDigest);

/// <summary>One persisted, host-derived criterion row inside a readiness snapshot.</summary>
public sealed record DeliveryReadinessCriterion(
    string CriterionId,
    string Requirement,
    DeliveryCriterionOutcome Outcome,
    IReadOnlyList<string> EvidenceIds,
    string Rationale,
    string? Remediation,
    IReadOnlyList<string> ResponsibleRoles,
    bool CustomerVisible);

/// <summary>One persisted, host-derived residual risk row inside a readiness snapshot.</summary>
public sealed record DeliveryReadinessRisk(
    string RiskId,
    DeliveryRiskClassification Classification,
    DeliveryRiskSeverity Severity,
    string Statement,
    string Impact,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<string> CriterionIds,
    string SourceRole,
    Guid SourceStepId,
    string? PreMortemFindingId);

/// <summary>
/// The canonical, immutable readiness aggregate. Everything that can authorize a review, a waiver,
/// an acceptance, or a publication is bound to the identifiers and hashes carried here.
/// </summary>
public sealed record DeliveryReadinessSnapshot(
    Guid Id,
    Guid FlowRunId,
    int Iteration,
    int Revision,
    DeliveryReadinessState State,
    string CandidateFingerprint,
    string AcceptancePlanHash,
    string OutcomeContractHash,
    string QaContractHash,
    Guid OutcomeOwnerStepId,
    Guid QaStepId,
    IReadOnlyList<Guid> PreMortemStepIds,
    IReadOnlyList<DeliveryReadinessCriterion> Criteria,
    IReadOnlyList<DeliveryReadinessRisk> Risks,
    IReadOnlyList<string> RequiredWaiverRiskIds,
    IReadOnlyList<string> Diagnostics,
    DateTimeOffset CreatedAt);

/// <summary>Everything the pure policy needs to derive one readiness assessment.</summary>
public sealed record DeliveryReadinessDerivationInput(
    Guid FlowRunId,
    int Iteration,
    int Revision,
    DeliveryAcceptancePlan? AcceptancePlan,
    string AcceptancePlanHash,
    DeliveryQaDocument? Qa,
    string QaContractHash,
    Guid QaStepId,
    string QaRole,
    Guid OutcomeOwnerStepId,
    string OutcomeContractHash,
    string CandidateFingerprint,
    IReadOnlyCollection<string> GrantedWaiverRiskIds,
    IReadOnlyCollection<DeliveryEvidenceItem>? KnownEvidence,
    IReadOnlyList<Guid> PreMortemStepIds,
    IReadOnlyList<string> HostDiagnostics,
    DateTimeOffset CreatedAt,
    Guid SnapshotId);

/// <summary>
/// The single pure derivation used by orchestration, review, waiver, and publication authorization.
/// It is deliberately free of database, workspace, and agent dependencies so restart reconciliation
/// can repeat it byte-for-byte from durable rows.
/// </summary>
public static class DeliveryReadinessPolicy
{
    public const string QaBeginMarker = "OUTCOME_QA_BEGIN";
    public const string QaEndMarker = "OUTCOME_QA_END";

    public const int MaximumQaBytes = 64 * 1024;
    public const int MaximumCriteria = 24;
    public const int MaximumRisks = 24;
    public const int MaximumEvidenceIdsPerItem = 12;
    public const int MaximumTextCharacters = 2_000;
    public const int MaximumSnapshotBytes = 96 * 1024;

    private static readonly Regex CriterionIdPattern = new(
        "^AC-[0-9]{3}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex RiskIdPattern = new(
        "^RR-[0-9]{3}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex FindingIdPattern = new(
        "^PM-[0-9]{3}$",
        RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions StrictJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new ExactEnumConverter<DeliveryCriterionOutcome>(),
            new ExactEnumConverter<DeliveryRiskClassification>(),
            new ExactEnumConverter<DeliveryRiskSeverity>(),
            new ExactEnumConverter<OutcomeQaVerdict>(),
            new ExactEnumConverter<OutcomeEvidenceKind>()
        }
    };

    /// <summary>Validates a planned acceptance plan and returns its canonical hash.</summary>
    public static string HashAcceptancePlan(DeliveryAcceptancePlan plan)
    {
        var errors = ValidateAcceptancePlan(plan);
        if (errors.Count > 0)
        {
            throw new DeliveryReadinessContractException(errors);
        }
        return OutcomeVerificationRules.ComputeSha256(
            JsonSerializer.Serialize(Normalize(plan), StrictJsonOptions));
    }

    public static IReadOnlyList<string> ValidateAcceptancePlan(
        DeliveryAcceptancePlan? plan)
    {
        var errors = new List<string>();
        if (plan is null)
        {
            return ["acceptance plan is required for a Delivery flow"];
        }
        if (plan.Criteria is null || plan.Criteria.Count is < 1 or > MaximumCriteria)
        {
            errors.Add($"acceptance plan must contain 1-{MaximumCriteria} criteria");
            return errors;
        }
        var index = 0;
        foreach (var criterion in plan.Criteria)
        {
            index++;
            if (criterion is null)
            {
                errors.Add($"acceptance criterion {index} is null");
                continue;
            }
            var expectedId = $"AC-{index:000}";
            if (!string.Equals(criterion.Id, expectedId, StringComparison.Ordinal))
            {
                errors.Add($"acceptance criterion {index} id must be exactly '{expectedId}'");
            }
            RequireText(criterion.Requirement, $"criterion {expectedId} requirement", errors);
            RequireText(criterion.Verification, $"criterion {expectedId} verification", errors);
            if (criterion.OwnerRoles is null ||
                criterion.OwnerRoles.Count is < 1 or > 4 ||
                criterion.OwnerRoles.Any(string.IsNullOrWhiteSpace) ||
                criterion.OwnerRoles.Distinct(StringComparer.Ordinal).Count() !=
                    criterion.OwnerRoles.Count)
            {
                errors.Add($"criterion {expectedId} ownerRoles must contain 1-4 unique roles");
            }
            if (criterion.EvidenceKinds is null ||
                criterion.EvidenceKinds.Count is < 1 or > 5 ||
                criterion.EvidenceKinds.Distinct().Count() != criterion.EvidenceKinds.Count)
            {
                errors.Add(
                    $"criterion {expectedId} evidenceKinds must contain 1-5 unique kinds");
            }
        }
        return errors;
    }

    /// <summary>Extracts and strictly validates a verification document from agent output.</summary>
    public static ParsedDeliveryQaDocument ParseQaOutput(
        string output,
        DeliveryAcceptancePlan plan,
        string acceptancePlanHash,
        IReadOnlyCollection<DeliveryEvidenceItem>? knownEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        var begins = FindStandalone(output, QaBeginMarker);
        var ends = FindStandalone(output, QaEndMarker);
        if (begins.Count != 1 || ends.Count != 1 || ends[0] <= begins[0])
        {
            throw new DeliveryReadinessContractException(
                [$"output must contain exactly one {QaBeginMarker}/{QaEndMarker} pair"]);
        }
        return ParseQaJson(
            output[(begins[0] + QaBeginMarker.Length)..ends[0]].Trim(),
            plan,
            acceptancePlanHash,
            knownEvidence);
    }

    public static bool ContainsQaContract(string? output) =>
        output is not null &&
        FindStandalone(output, QaBeginMarker).Count > 0;

    public static ParsedDeliveryQaDocument ParseQaJson(
        string json,
        DeliveryAcceptancePlan plan,
        string acceptancePlanHash,
        IReadOnlyCollection<DeliveryEvidenceItem>? knownEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new DeliveryReadinessContractException(["QA result JSON is empty"]);
        }
        if (Encoding.UTF8.GetByteCount(json) > MaximumQaBytes)
        {
            throw new DeliveryReadinessContractException(
                [$"QA result must contain at most {MaximumQaBytes} UTF-8 bytes"]);
        }

        DeliveryQaDocument document;
        try
        {
            using var parsed = JsonDocument.Parse(json);
            var shapeErrors = ValidateShape(parsed.RootElement);
            if (shapeErrors.Count > 0)
            {
                throw new DeliveryReadinessContractException(shapeErrors);
            }
            document = JsonSerializer.Deserialize<DeliveryQaDocument>(
                           json,
                           StrictJsonOptions)
                       ?? throw new DeliveryReadinessContractException(
                           ["QA result document is null"]);
        }
        catch (JsonException exception)
        {
            throw new DeliveryReadinessContractException(
                [$"QA result is not strict JSON: {exception.Message}"]);
        }

        var errors = ValidateQaDocument(
            document,
            plan,
            acceptancePlanHash,
            knownEvidence);
        if (errors.Count > 0)
        {
            throw new DeliveryReadinessContractException(errors);
        }
        return new ParsedDeliveryQaDocument(
            document,
            json,
            OutcomeVerificationRules.ComputeSha256(json));
    }

    public static IReadOnlyList<string> ValidateQaDocument(
        DeliveryQaDocument? document,
        DeliveryAcceptancePlan plan,
        string acceptancePlanHash,
        IReadOnlyCollection<DeliveryEvidenceItem>? knownEvidence = null)
    {
        var errors = new List<string>();
        if (document is null)
        {
            return ["QA result document is null"];
        }
        if (!OutcomeVerificationRules.IsSha256(document.AcceptancePlanHash) ||
            !string.Equals(
                document.AcceptancePlanHash,
                acceptancePlanHash,
                StringComparison.Ordinal))
        {
            errors.Add("acceptancePlanHash must equal the current planned acceptance plan hash");
        }
        if (document.Verdict is null)
        {
            errors.Add("verdict is required");
        }
        if (document.Criteria is null)
        {
            errors.Add("criteria is required");
            return errors;
        }
        if (document.ResidualRisks is null)
        {
            errors.Add("residualRisks is required");
            return errors;
        }
        if (document.PlanGaps is null)
        {
            errors.Add("planGaps is required");
            return errors;
        }

        var plannedById = (plan.Criteria ?? [])
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        var plannedIds = plannedById.Keys.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var criterionIndex = 0;
        foreach (var result in document.Criteria)
        {
            criterionIndex++;
            if (result is null)
            {
                errors.Add($"criterion result {criterionIndex} is null");
                continue;
            }
            var criterionId = result.CriterionId ?? string.Empty;
            if (!CriterionIdPattern.IsMatch(criterionId))
            {
                errors.Add($"criterion result {criterionIndex} criterionId must match 'AC-000'");
                continue;
            }
            if (!plannedIds.Contains(criterionId))
            {
                errors.Add(
                    $"criterion result '{criterionId}' is not in the planned acceptance plan");
            }
            if (!seen.Add(criterionId))
            {
                errors.Add($"criterion result '{criterionId}' is duplicated");
            }
            if (result.Outcome is null)
            {
                errors.Add($"criterion '{result.CriterionId}' outcome is required");
            }
            RequireText(result.Rationale, $"criterion '{result.CriterionId}' rationale", errors);
            ValidateEvidenceIds(
                result.EvidenceIds,
                $"criterion '{result.CriterionId}'",
                required: result.Outcome == DeliveryCriterionOutcome.Verified,
                knownEvidence,
                result.Outcome == DeliveryCriterionOutcome.Verified &&
                plannedById.TryGetValue(criterionId, out var planned)
                    ? planned.EvidenceKinds
                    : null,
                errors);
            if ((result.Outcome is
                     DeliveryCriterionOutcome.Failed or
                     DeliveryCriterionOutcome.Blocked) &&
                string.IsNullOrWhiteSpace(result.Remediation))
            {
                errors.Add(
                    $"criterion '{result.CriterionId}' must state remediation when it is not verified");
            }
            if (result.Remediation is { Length: > MaximumTextCharacters })
            {
                errors.Add($"criterion '{result.CriterionId}' remediation is too long");
            }
            if (result.ResponsibleRoles is null)
            {
                errors.Add(
                    $"criterion '{result.CriterionId}' responsibleRoles is required");
            }
            else if (result.ResponsibleRoles.Count > 4 ||
                     result.ResponsibleRoles.Any(string.IsNullOrWhiteSpace) ||
                     result.ResponsibleRoles.Distinct(StringComparer.Ordinal).Count() !=
                         result.ResponsibleRoles.Count)
            {
                errors.Add(
                    $"criterion '{result.CriterionId}' responsibleRoles must contain at most 4 unique roles");
            }
            else if (result.Outcome == DeliveryCriterionOutcome.Verified &&
                     result.ResponsibleRoles.Count != 0)
            {
                errors.Add(
                    $"criterion '{result.CriterionId}' Verified outcome cannot name responsible roles");
            }
            else if (result.Outcome == DeliveryCriterionOutcome.Failed &&
                     result.ResponsibleRoles.Count == 0)
            {
                errors.Add(
                    $"criterion '{result.CriterionId}' Failed outcome must name at least one responsible role");
            }
        }
        foreach (var missing in plannedIds.Except(seen, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            errors.Add($"planned criterion '{missing}' has no QA result");
        }
        if (document.Criteria.Count > MaximumCriteria)
        {
            errors.Add($"criteria must contain at most {MaximumCriteria} entries");
        }

        if (document.ResidualRisks.Count > MaximumRisks)
        {
            errors.Add($"residualRisks must contain at most {MaximumRisks} entries");
        }
        var riskIds = new HashSet<string>(StringComparer.Ordinal);
        var riskIndex = 0;
        foreach (var risk in document.ResidualRisks)
        {
            riskIndex++;
            if (risk is null)
            {
                errors.Add($"residual risk {riskIndex} is null");
                continue;
            }
            var riskId = risk.RiskId ?? string.Empty;
            if (!RiskIdPattern.IsMatch(riskId))
            {
                errors.Add($"residual risk {riskIndex} riskId must match 'RR-000'");
                continue;
            }
            if (!riskIds.Add(riskId))
            {
                errors.Add($"residual risk '{riskId}' is duplicated");
            }
            if (risk.Classification is null)
            {
                errors.Add($"residual risk '{risk.RiskId}' classification is required");
            }
            if (risk.Severity is null)
            {
                errors.Add($"residual risk '{risk.RiskId}' severity is required");
            }
            RequireText(risk.Statement, $"residual risk '{risk.RiskId}' statement", errors);
            RequireText(risk.Impact, $"residual risk '{risk.RiskId}' impact", errors);
            ValidateEvidenceIds(
                risk.EvidenceIds,
                $"residual risk '{risk.RiskId}'",
                required: false,
                knownEvidence,
                allowedKinds: null,
                errors);
            if (risk.CriterionIds is not null)
            {
                if (risk.CriterionIds.Count > MaximumCriteria ||
                    risk.CriterionIds.Distinct(StringComparer.Ordinal).Count() !=
                        risk.CriterionIds.Count)
                {
                    errors.Add(
                        $"residual risk '{risk.RiskId}' criterionIds must be unique and bounded");
                }
                foreach (var criterionId in risk.CriterionIds)
                {
                    if (!plannedIds.Contains(criterionId))
                    {
                        errors.Add(
                            $"residual risk '{risk.RiskId}' references unplanned criterion '{criterionId}'");
                    }
                }
            }
            if (risk.PreMortemFindingId is not null &&
                !FindingIdPattern.IsMatch(risk.PreMortemFindingId))
            {
                errors.Add(
                    $"residual risk '{risk.RiskId}' preMortemFindingId must match 'PM-000'");
            }
        }

        if (document.PlanGaps.Count > MaximumCriteria)
        {
            errors.Add($"planGaps must contain at most {MaximumCriteria} entries");
        }
        foreach (var (gap, index) in document.PlanGaps.Select(
                     (gap, index) => (gap, index)))
        {
            if (gap is null)
            {
                errors.Add($"plan gap {index + 1} is null");
                continue;
            }
            RequireText(
                gap.Requirement,
                $"plan gap {index + 1} requirement",
                errors);
            RequireText(
                gap.Verification,
                $"plan gap {index + 1} verification",
                errors);
            RequireText(
                gap.Rationale,
                $"plan gap {index + 1} rationale",
                errors);
            if (gap.OwnerRoles is null ||
                gap.OwnerRoles.Count is < 1 or > 4 ||
                gap.OwnerRoles.Any(string.IsNullOrWhiteSpace) ||
                gap.OwnerRoles.Distinct(StringComparer.Ordinal).Count() !=
                    gap.OwnerRoles.Count)
            {
                errors.Add(
                    $"plan gap {index + 1} ownerRoles must contain 1-4 unique roles");
            }
        }

        if (errors.Count == 0)
        {
            var derived = DeriveVerdict(document);
            if (document.Verdict != derived)
            {
                errors.Add(
                    $"verdict '{document.Verdict}' does not equal the host-derived verdict '{derived}'");
            }
        }
        return errors;
    }

    /// <summary>
    /// Host derivation of the QA verdict. A supplied verdict must equal this value, but even an
    /// equal verdict grants no authority: readiness state is derived separately.
    /// </summary>
    public static OutcomeQaVerdict DeriveVerdict(DeliveryQaDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var criteria = document.Criteria ?? [];
        var risks = document.ResidualRisks ?? [];
        var planGaps = document.PlanGaps ?? [];
        if (criteria.Any(item => item?.Outcome == DeliveryCriterionOutcome.Blocked) ||
            risks.Any(item => item?.Classification == DeliveryRiskClassification.Blocking))
        {
            return OutcomeQaVerdict.BLOCKED;
        }
        return planGaps.Count == 0 &&
               criteria.Count > 0 &&
               criteria.All(item => item?.Outcome == DeliveryCriterionOutcome.Verified)
            ? OutcomeQaVerdict.PASS
            : OutcomeQaVerdict.FAIL;
    }

    /// <summary>
    /// Derives one immutable readiness snapshot. Any missing, invalid, or unbound input fails
    /// closed to <see cref="DeliveryReadinessState.NeedsRefinement"/> or
    /// <see cref="DeliveryReadinessState.Blocked"/>; it never produces a ready result by default.
    /// </summary>
    public static DeliveryReadinessSnapshot Derive(DeliveryReadinessDerivationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var diagnostics = new List<string>(input.HostDiagnostics ?? []);
        var criteria = new List<DeliveryReadinessCriterion>();
        var risks = new List<DeliveryReadinessRisk>();

        var planErrors = ValidateAcceptancePlan(input.AcceptancePlan);
        if (planErrors.Count > 0)
        {
            diagnostics.AddRange(planErrors);
        }
        else
        {
            var qaErrors = ValidateQaDocument(
                input.Qa,
                input.AcceptancePlan!,
                input.AcceptancePlanHash,
                input.KnownEvidence);
            if (qaErrors.Count > 0)
            {
                diagnostics.AddRange(qaErrors);
            }
            else
            {
                var plannedById = input.AcceptancePlan!.Criteria.ToDictionary(
                    item => item.Id,
                    StringComparer.Ordinal);
                foreach (var result in input.Qa!.Criteria!
                             .OrderBy(item => item.CriterionId, StringComparer.Ordinal))
                {
                    var planned = plannedById[result.CriterionId];
                    criteria.Add(new DeliveryReadinessCriterion(
                        result.CriterionId,
                        planned.Requirement.Trim(),
                        result.Outcome!.Value,
                        Normalize(result.EvidenceIds),
                        result.Rationale.Trim(),
                        string.IsNullOrWhiteSpace(result.Remediation)
                            ? null
                            : result.Remediation.Trim(),
                        Normalize(result.ResponsibleRoles),
                        planned.CustomerVisible));
                }
                foreach (var risk in input.Qa.ResidualRisks!
                             .OrderBy(item => item.RiskId, StringComparer.Ordinal))
                {
                    risks.Add(new DeliveryReadinessRisk(
                        risk.RiskId,
                        risk.Classification!.Value,
                        risk.Severity!.Value,
                        risk.Statement.Trim(),
                        risk.Impact.Trim(),
                        Normalize(risk.EvidenceIds),
                        Normalize(risk.CriterionIds),
                        input.QaRole,
                        input.QaStepId,
                        risk.PreMortemFindingId));
                }
                diagnostics.AddRange(input.Qa.PlanGaps!
                    .Select(gap =>
                        $"Verification found an acceptance-plan gap: {gap.Requirement.Trim()}"));
            }
        }

        if (!OutcomeVerificationRules.IsSha256(input.CandidateFingerprint))
        {
            diagnostics.Add("the reviewed candidate fingerprint is missing or malformed");
        }
        if (!OutcomeVerificationRules.IsSha256(input.OutcomeContractHash))
        {
            diagnostics.Add("the reviewed outcome contract hash is missing or malformed");
        }
        if (input.QaStepId == Guid.Empty)
        {
            diagnostics.Add("the readiness assessment has no durable QA source step");
        }
        if (input.OutcomeOwnerStepId == Guid.Empty)
        {
            diagnostics.Add("the readiness assessment has no durable outcome-owner source step");
        }

        var granted = (input.GrantedWaiverRiskIds ?? [])
            .ToHashSet(StringComparer.Ordinal);
        var requiredWaivers = risks
            .Where(risk => risk.Classification == DeliveryRiskClassification.WaiverRequired)
            .Select(risk => risk.RiskId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var state = DeriveState(criteria, risks, diagnostics, granted);

        return new DeliveryReadinessSnapshot(
            input.SnapshotId,
            input.FlowRunId,
            input.Iteration,
            input.Revision,
            state,
            input.CandidateFingerprint,
            input.AcceptancePlanHash,
            input.OutcomeContractHash,
            input.QaContractHash,
            input.OutcomeOwnerStepId,
            input.QaStepId,
            [.. (input.PreMortemStepIds ?? []).Distinct().OrderBy(item => item)],
            criteria,
            risks,
            requiredWaivers,
            [.. diagnostics.Distinct(StringComparer.Ordinal).Take(64)],
            input.CreatedAt);
    }

    /// <summary>
    /// The ordered state derivation. Blocking beats failure, failure beats waiver, and waiver beats
    /// ready. A ready result therefore requires every criterion verified and every waiver granted.
    /// </summary>
    public static DeliveryReadinessState DeriveState(
        IReadOnlyCollection<DeliveryReadinessCriterion> criteria,
        IReadOnlyCollection<DeliveryReadinessRisk> risks,
        IReadOnlyCollection<string> diagnostics,
        IReadOnlyCollection<string> grantedWaiverRiskIds)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(risks);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(grantedWaiverRiskIds);
        if (criteria.Any(item => item.Outcome == DeliveryCriterionOutcome.Blocked) ||
            risks.Any(item => item.Classification == DeliveryRiskClassification.Blocking))
        {
            return DeliveryReadinessState.Blocked;
        }
        if (diagnostics.Count > 0 ||
            criteria.Count == 0 ||
            criteria.Any(item => item.Outcome == DeliveryCriterionOutcome.Failed))
        {
            return DeliveryReadinessState.NeedsRefinement;
        }
        var granted = grantedWaiverRiskIds.ToHashSet(StringComparer.Ordinal);
        return risks.Any(risk =>
            risk.Classification == DeliveryRiskClassification.WaiverRequired &&
            !granted.Contains(risk.RiskId))
            ? DeliveryReadinessState.NeedsCustomerWaiver
            : DeliveryReadinessState.ReadyToApprove;
    }

    /// <summary>The exact set of customer actions the server will accept for a state.</summary>
    public static IReadOnlyList<DeliveryReadinessAction> AllowedActions(
        DeliveryReadinessState state) => state switch
        {
            DeliveryReadinessState.ReadyToApprove =>
                [DeliveryReadinessAction.Accept, DeliveryReadinessAction.RequestRefinement],
            DeliveryReadinessState.NeedsCustomerWaiver =>
                [DeliveryReadinessAction.GrantWaiver, DeliveryReadinessAction.RequestRefinement],
            DeliveryReadinessState.NeedsRefinement =>
                [DeliveryReadinessAction.RequestRefinement],
            DeliveryReadinessState.Blocked =>
            [
                DeliveryReadinessAction.Continue,
            DeliveryReadinessAction.Replan,
            DeliveryReadinessAction.Abandon
            ],
            _ => [DeliveryReadinessAction.None]
        };

    public static string SerializeSnapshot(DeliveryReadinessSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // Property order follows the record declaration and every collection is already ordered by
        // the derivation, so this serialization is deterministic and safe to hash.
        var json = JsonSerializer.Serialize(snapshot, StrictJsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumSnapshotBytes)
        {
            throw new DeliveryReadinessContractException(
                ["the readiness snapshot exceeds the durable size limit"]);
        }
        return json;
    }

    public static DeliveryReadinessSnapshot DeserializeSnapshot(string json)
    {
        if (string.IsNullOrWhiteSpace(json) ||
            Encoding.UTF8.GetByteCount(json) > MaximumSnapshotBytes)
        {
            throw new DeliveryReadinessContractException(
                ["the durable readiness snapshot is empty or oversized"]);
        }
        try
        {
            return JsonSerializer.Deserialize<DeliveryReadinessSnapshot>(
                       json,
                       StrictJsonOptions)
                   ?? throw new DeliveryReadinessContractException(
                       ["the durable readiness snapshot is empty"]);
        }
        catch (JsonException exception)
        {
            throw new DeliveryReadinessContractException(
                [$"the durable readiness snapshot is invalid: {exception.Message}"]);
        }
    }

    public static string HashSnapshot(DeliveryReadinessSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // Identity, revision, and timestamp are deliberately normalized away so the binding token
        // depends only on the derived facts. Repeating a derivation after a restart therefore
        // produces the same hash and reuses the same active rows instead of duplicating them.
        return OutcomeVerificationRules.ComputeSha256(
            SerializeSnapshot(snapshot with
            {
                Id = Guid.Empty,
                Revision = 0,
                CreatedAt = DateTimeOffset.UnixEpoch
            }));
    }

    /// <summary>Deterministic hash of the granted waiver set bound to one readiness assessment.</summary>
    public static string HashWaiverSet(
        string readinessContractHash,
        IEnumerable<string> grantedRiskIds)
    {
        ArgumentNullException.ThrowIfNull(readinessContractHash);
        ArgumentNullException.ThrowIfNull(grantedRiskIds);
        var ordered = grantedRiskIds
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal);
        return OutcomeVerificationRules.ComputeSha256(
            string.Join("\n", [readinessContractHash, .. ordered]));
    }

    private static DeliveryAcceptancePlan Normalize(DeliveryAcceptancePlan plan) =>
        new(
            plan.Criteria
                .Select(criterion => new DeliveryAcceptanceCriterion
                {
                    Id = criterion.Id,
                    Requirement = criterion.Requirement.Trim(),
                    Verification = criterion.Verification.Trim(),
                    OwnerRoles = [.. criterion.OwnerRoles!],
                    EvidenceKinds = [.. criterion.EvidenceKinds!.OrderBy(kind => (int)kind)],
                    CustomerVisible = criterion.CustomerVisible
                })
                .ToArray());

    private static IReadOnlyList<string> Normalize(IReadOnlyList<string>? values) =>
        values is null
            ? []
            : [.. values.Select(value => value.Trim()).Distinct(StringComparer.Ordinal)];

    private static void ValidateEvidenceIds(
        IReadOnlyList<string>? evidenceIds,
        string label,
        bool required,
        IReadOnlyCollection<DeliveryEvidenceItem>? knownEvidence,
        IReadOnlyCollection<OutcomeEvidenceKind>? allowedKinds,
        ICollection<string> errors)
    {
        if (evidenceIds is null)
        {
            errors.Add($"{label} evidenceIds is required");
            return;
        }
        if (evidenceIds.Count > MaximumEvidenceIdsPerItem)
        {
            errors.Add($"{label} evidenceIds must contain at most {MaximumEvidenceIdsPerItem} entries");
        }
        if (required && evidenceIds.Count == 0)
        {
            errors.Add($"{label} must reference at least one evidence id");
        }
        if (evidenceIds.Distinct(StringComparer.Ordinal).Count() != evidenceIds.Count)
        {
            errors.Add($"{label} evidenceIds must be unique");
        }
        foreach (var evidenceId in evidenceIds)
        {
            if (string.IsNullOrWhiteSpace(evidenceId) || evidenceId.Length > 200)
            {
                errors.Add($"{label} contains an empty or oversized evidence id");
                continue;
            }
            if (knownEvidence is null)
            {
                continue;
            }
            var evidence = knownEvidence.SingleOrDefault(item =>
                string.Equals(
                    item.EvidenceId,
                    evidenceId,
                    StringComparison.Ordinal));
            if (evidence is null)
            {
                errors.Add($"{label} references unknown evidence id '{evidenceId}'");
                continue;
            }
            if (required && !evidence.SupportsVerification)
            {
                errors.Add(
                    $"{label} references unsuccessful evidence id '{evidenceId}'");
            }
            if (required &&
                allowedKinds is not null &&
                !allowedKinds.Contains(evidence.Kind))
            {
                errors.Add(
                    $"{label} evidence id '{evidenceId}' has kind '{evidence.Kind}', which is not allowed by the acceptance plan");
            }
        }
    }

    private static void RequireText(
        string? value,
        string label,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumTextCharacters)
        {
            errors.Add($"{label} must contain 1-{MaximumTextCharacters} characters");
        }
    }

    private static IReadOnlyList<string> ValidateShape(JsonElement root)
    {
        var errors = new List<string>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ["QA result JSON must be an object"];
        }
        RejectDuplicates(root, "outcome QA", errors);
        foreach (var name in new[]
                 {
                     "AcceptancePlanHash", "Verdict", "Criteria", "ResidualRisks",
                     "PlanGaps"
                 })
        {
            if (!root.TryGetProperty(name, out _))
            {
                errors.Add($"verification result is missing required property '{name}'");
            }
        }
        return errors;
    }

    private static void RejectDuplicates(
        JsonElement element,
        string path,
        ICollection<string> errors)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    errors.Add($"{path} contains duplicate property '{property.Name}'");
                }
                RejectDuplicates(property.Value, $"{path}.{property.Name}", errors);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicates(item, $"{path}[{index++}]", errors);
            }
        }
    }

    private static IReadOnlyList<int> FindStandalone(string output, string sentinel)
    {
        var matches = new List<int>();
        var searchIndex = 0;
        while (searchIndex <= output.Length - sentinel.Length)
        {
            var index = output.IndexOf(sentinel, searchIndex, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }
            var beforeLine = index == 0 || output[index - 1] is '\r' or '\n';
            var afterIndex = index + sentinel.Length;
            if (beforeLine &&
                (afterIndex == output.Length || output[afterIndex] is '\r' or '\n'))
            {
                matches.Add(index);
            }
            searchIndex = index + sentinel.Length;
        }
        return matches;
    }
}

/// <summary>Exact, case-sensitive enum conversion so contract casing cannot drift silently.</summary>
public sealed class ExactEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"{typeof(TEnum).Name} must be an exact case-sensitive string.");
        }
        var value = reader.GetString();
        if (value is null ||
            !Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed) ||
            !Enum.IsDefined(parsed))
        {
            throw new JsonException($"'{value}' is not an exact {typeof(TEnum).Name} value.");
        }
        return parsed;
    }

    public override void Write(
        Utf8JsonWriter writer,
        TEnum value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

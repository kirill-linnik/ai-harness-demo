using System.Text.Json;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Gating;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Data;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

/// <summary>
/// The authoritative readiness binding loaded straight from durable rows. Every release-sensitive
/// decision re-reads this instead of trusting an in-memory gate, a status, or agent prose.
/// </summary>
public sealed record DeliveryReadinessBinding(
    DeliveryReadinessSnapshotRecord Record,
    DeliveryReadinessSnapshot Contract,
    ReviewedCandidateRecord Candidate,
    IReadOnlyList<ReadinessWaiverRecord> Waivers)
{
    public DeliveryReadinessState State => Contract.State;

    public string ContractHash => Record.ContractHash;

    public int Revision => Record.Revision;

    public string WaiverSetHash => DeliveryReadinessPolicy.HashWaiverSet(
        Record.ContractHash,
        Waivers.Select(item => item.RiskId));
}

/// <summary>
/// Durable envelope for one recorded <c>outcome-qa-v2</c> contract. The raw JSON is preserved so a
/// restart re-derives readiness from the exact bytes the host validated.
/// </summary>
public sealed record DeliveryQaLedgerEntry(
    string Version,
    int Iteration,
    Guid StepId,
    string Role,
    string ContractHash,
    string QaJson)
{
    public const string CurrentVersion = "delivery-readiness-qa-v1";
}

/// <summary>Durable envelope for the planned, hashed acceptance criteria of one iteration.</summary>
public sealed record DeliveryAcceptancePlanLedgerEntry(
    string Version,
    int Iteration,
    Guid StepId,
    string PlanHash,
    string PlanJson)
{
    public const string CurrentVersion = "delivery-acceptance-plan-v1";
}

/// <summary>
/// One host-issued evidence identifier. The host mints these from its own execution records before
/// the verification turn is dispatched, so a verification result can only cite evidence the host
/// itself observed.
/// </summary>
public sealed record DeliveryEvidenceItem(
    string EvidenceId,
    string Kind,
    string Locator,
    string Summary);

/// <summary>Durable envelope for the host-owned evidence registry of one plan step.</summary>
public sealed record DeliveryEvidenceLedgerEntry(
    string Version,
    int Iteration,
    Guid StepId,
    int Sequence,
    string Role,
    IReadOnlyList<DeliveryEvidenceItem> Items)
{
    public const string CurrentVersion = "delivery-readiness-evidence-v1";
}

/// <summary>
/// Derives, persists, and re-reads the host-owned Delivery readiness assessment. The service owns
/// the only path that may bind a readiness snapshot to a sealed candidate, so review, waiver,
/// publication, and approval authorization all resolve through the same durable rows.
/// </summary>
public sealed class DeliveryReadinessService
{
    public const string AcceptancePlanEventType = "delivery.acceptance-plan-recorded";
    public const string EvidenceEventType = "delivery.readiness-evidence-recorded";
    public const string QaEventType = "delivery.readiness-qa-recorded";
    public const string DerivedEventType = "delivery.readiness-derived";
    public const string SupersededEventType = "delivery.readiness-superseded";
    public const string WaiverGrantedEventType = "delivery.readiness-waiver-granted";
    public const string DeniedEventType = "delivery.readiness-authorization-denied";
    public const string ReconciledEventType = "delivery.readiness-reconciled";

    private static readonly JsonSerializerOptions LedgerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        Converters =
        {
            new ExactEnumConverter<OutcomeEvidenceKind>()
        }
    };

    public static bool AppliesTo(FlowRun flow) =>
        flow is
        {
            ContractVersion: "studio-v2",
            Kind: FlowKind.Delivery
        };

    public static string SerializeAcceptancePlan(
        DeliveryAcceptancePlan plan,
        int iteration,
        Guid stepId)
    {
        var hash = DeliveryReadinessPolicy.HashAcceptancePlan(plan);
        return JsonSerializer.Serialize(
            new DeliveryAcceptancePlanLedgerEntry(
                DeliveryAcceptancePlanLedgerEntry.CurrentVersion,
                iteration,
                stepId,
                hash,
                JsonSerializer.Serialize(plan, LedgerOptions)),
            LedgerOptions);
    }

    public static string SerializeQa(
        ParsedDeliveryQaDocument parsed,
        int iteration,
        Guid stepId,
        string role) =>
        JsonSerializer.Serialize(
            new DeliveryQaLedgerEntry(
                DeliveryQaLedgerEntry.CurrentVersion,
                iteration,
                stepId,
                role,
                parsed.ContractHash,
                parsed.RawJson),
            LedgerOptions);

    /// <summary>
    /// Reads the planned acceptance criteria for the current iteration. A missing or duplicated
    /// plan is not repaired from prose; the caller derives <c>NeedsRefinement</c> instead.
    /// </summary>
    public static (DeliveryAcceptancePlan? Plan, string Hash, IReadOnlyList<string> Errors)
        TryReadAcceptancePlan(FlowRun flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return TryReadAcceptancePlan(flow.Events, flow.Iteration);
    }

    public static (DeliveryAcceptancePlan? Plan, string Hash, IReadOnlyList<string> Errors)
        TryReadAcceptancePlan(IEnumerable<FlowEvent> events, int iteration)
    {
        ArgumentNullException.ThrowIfNull(events);
        var entries = ReadLedger<DeliveryAcceptancePlanLedgerEntry>(
            events,
            AcceptancePlanEventType,
            entry => entry.Iteration == iteration &&
                     string.Equals(
                         entry.Version,
                         DeliveryAcceptancePlanLedgerEntry.CurrentVersion,
                         StringComparison.Ordinal));
        if (entries.Count == 0)
        {
            return (
                null,
                string.Empty,
                ["the current Delivery iteration has no planned acceptance criteria"]);
        }
        if (entries.Count > 1)
        {
            return (
                null,
                string.Empty,
                ["the current Delivery iteration has duplicate acceptance plans"]);
        }
        try
        {
            var plan = JsonSerializer.Deserialize<DeliveryAcceptancePlan>(
                           entries[0].PlanJson,
                           LedgerOptions)
                       ?? throw new DeliveryReadinessContractException(
                           ["the durable acceptance plan is empty"]);
            var hash = DeliveryReadinessPolicy.HashAcceptancePlan(plan);
            return !string.Equals(hash, entries[0].PlanHash, StringComparison.Ordinal)
                ? (null, string.Empty, ["the durable acceptance plan hash does not match its content"])
                : (plan, hash, []);
        }
        catch (Exception exception) when (
            exception is JsonException or DeliveryReadinessContractException)
        {
            return (
                null,
                string.Empty,
                [$"the durable acceptance plan is invalid: {exception.Message}"]);
        }
    }

    /// <summary>Reads the newest recorded <c>outcome-qa-v2</c> contract of the current iteration.</summary>
    public static (DeliveryQaLedgerEntry? Entry, IReadOnlyList<string> Errors) TryReadQa(
        FlowRun flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var entries = ReadLedger<DeliveryQaLedgerEntry>(
            flow.Events,
            QaEventType,
            entry => entry.Iteration == flow.Iteration &&
                     string.Equals(
                         entry.Version,
                         DeliveryQaLedgerEntry.CurrentVersion,
                         StringComparison.Ordinal));
        return entries.Count == 0
            ? (null, ["the current Delivery iteration has no strict outcome-qa-v2 result"])
            : (entries[^1], []);
    }

    /// <summary>
    /// Mints the deterministic host-issued evidence identifiers for one completed or about-to-run
    /// plan step. Identifier <c>EV-Snnn-000</c> is the step's own host execution record; the
    /// remaining identifiers are the host-observed tool calls in their recorded order.
    /// </summary>
    public static DeliveryEvidenceLedgerEntry BuildEvidence(
        FlowStep step,
        IReadOnlyList<AgentToolCall>? toolCalls = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        var items = new List<DeliveryEvidenceItem>
        {
            new(
                EvidenceId(step.Sequence, 0),
                "Observation",
                string.IsNullOrWhiteSpace(step.PlanStepKey)
                    ? step.AgentRole
                    : step.PlanStepKey,
                $"Host execution record for plan step '{step.PlanStepKey}' run by {step.AgentRole}.")
        };
        var index = 0;
        foreach (var call in (toolCalls ?? [.. step.ToolCalls]).OrderBy(call => call.Id))
        {
            index++;
            items.Add(new DeliveryEvidenceItem(
                EvidenceId(step.Sequence, index),
                string.Equals(call.ToolType, "Shell", StringComparison.OrdinalIgnoreCase)
                    ? "Command"
                    : "Observation",
                Clip(
                    string.IsNullOrWhiteSpace(call.NormalizedCommand)
                        ? call.ToolName
                        : call.NormalizedCommand,
                    400),
                Clip(call.ResultSummary, 400)));
        }
        return new DeliveryEvidenceLedgerEntry(
            DeliveryEvidenceLedgerEntry.CurrentVersion,
            step.Iteration,
            step.Id,
            step.Sequence,
            step.AgentRole,
            items);
    }

    public static string SerializeEvidence(DeliveryEvidenceLedgerEntry entry) =>
        JsonSerializer.Serialize(entry, LedgerOptions);

    /// <summary>The complete host-owned evidence registry for one iteration.</summary>
    public static IReadOnlyList<DeliveryEvidenceItem> ReadEvidence(
        IEnumerable<FlowEvent> events,
        int iteration) =>
        [.. ReadLedger<DeliveryEvidenceLedgerEntry>(
                events,
                EvidenceEventType,
                entry => entry.Iteration == iteration &&
                         string.Equals(
                             entry.Version,
                             DeliveryEvidenceLedgerEntry.CurrentVersion,
                             StringComparison.Ordinal))
            .OrderBy(entry => entry.Sequence)
            .SelectMany(entry => entry.Items)
            .DistinctBy(item => item.EvidenceId, StringComparer.Ordinal)];

    /// <summary>
    /// The identifiers a verification result may cite. The set is always authoritative, so an empty
    /// registry means no evidence reference can validate and readiness fails closed.
    /// </summary>
    public static IReadOnlyList<string> KnownEvidenceIds(
        IEnumerable<FlowEvent> events,
        int iteration) =>
        [.. ReadEvidence(events, iteration).Select(item => item.EvidenceId)];

    private static string EvidenceId(int sequence, int index) =>
        $"EV-S{Math.Max(sequence, 0):000}-{index:000}";

    private static string Clip(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Length <= maximum
                ? value
                : value[..maximum];

    /// <summary>
    /// Derives and persists the readiness assessment for a sealed candidate. Repeating the call with
    /// unchanged facts returns the same active rows and emits no duplicate events.
    /// </summary>
    public async Task<DeliveryReadinessBinding> DeriveAndPersistAsync(
        HarnessDbContext database,
        FlowRun flow,
        ReviewedCandidateIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(identity);
        if (!AppliesTo(flow))
        {
            throw new InvalidOperationException(
                "Delivery readiness applies only to studio-v2 Delivery flows.");
        }

        var diagnostics = new List<string>();
        var (plan, planHash, planErrors) = TryReadAcceptancePlan(flow);
        diagnostics.AddRange(planErrors);
        var (qaEntry, qaErrors) = TryReadQa(flow);
        diagnostics.AddRange(qaErrors);

        DeliveryQaDocument? qa = null;
        var qaHash = string.Empty;
        if (plan is not null && qaEntry is not null)
        {
            try
            {
                var parsed = DeliveryReadinessPolicy.ParseQaJson(
                    qaEntry.QaJson,
                    plan,
                    planHash,
                    KnownEvidenceIds(flow));
                qa = parsed.Document;
                qaHash = parsed.ContractHash;
            }
            catch (DeliveryReadinessContractException exception)
            {
                diagnostics.AddRange(exception.Errors);
            }
        }
        var existing = await LoadCurrentAsync(database, flow.Id, cancellationToken);
        var grantedRiskIds = await LoadApplicableWaiverRiskIdsAsync(
            database,
            flow.Id,
            identity.Fingerprint,
            qaHash,
            cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var candidate = new DeliveryReadinessDerivationInput(
            flow.Id,
            flow.Iteration,
            (existing?.Record.Revision ?? 0) + 1,
            plan,
            planHash,
            qa,
            qaHash,
            qaEntry?.StepId ?? Guid.Empty,
            qaEntry?.Role ?? string.Empty,
            identity.OutcomeOwnerStepId,
            identity.OutcomeContractHash,
            identity.Fingerprint,
            grantedRiskIds,
            KnownEvidenceIds(flow),
            PreMortemStepIds(flow),
            diagnostics,
            now,
            Guid.NewGuid());
        var contract = DeliveryReadinessPolicy.Derive(candidate);
        var contractHash = DeliveryReadinessPolicy.HashSnapshot(contract);

        if (existing is not null &&
            string.Equals(existing.Record.ContractHash, contractHash, StringComparison.Ordinal) &&
            string.Equals(
                existing.Record.CandidateFingerprint,
                identity.Fingerprint,
                StringComparison.Ordinal) &&
            existing.Record.Iteration == flow.Iteration)
        {
            return existing;
        }

        if (existing is not null)
        {
            existing.Record.Active = false;
            existing.Record.SupersededAt = now;
            existing.Candidate.Active = false;
            existing.Candidate.SupersededAt = now;
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                FlowStepId = identity.OutcomeOwnerStepId,
                Type = SupersededEventType,
                Message =
                    $"Superseded readiness revision {existing.Record.Revision} ({existing.Record.State}).",
                DataJson = JsonSerializer.Serialize(new
                {
                    Version = "delivery-readiness-superseded-v1",
                    SnapshotId = existing.Record.Id,
                    existing.Record.Revision,
                    State = existing.Record.State.ToString(),
                    existing.Record.ContractHash
                })
            });
            await database.SaveChangesAsync(cancellationToken);
        }

        var record = new DeliveryReadinessSnapshotRecord
        {
            Id = contract.Id,
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            Revision = contract.Revision,
            State = contract.State,
            Reconciliation = DeliveryReadinessReconciliation.Current,
            CandidateFingerprint = identity.Fingerprint,
            AcceptancePlanHash = string.IsNullOrEmpty(planHash)
                ? OutcomeVerificationRules.ComputeSha256(
                    $"missing-acceptance-plan:{flow.Id:D}:{flow.Iteration}")
                : planHash,
            OutcomeContractHash = identity.OutcomeContractHash,
            QaContractHash = qaHash,
            OutcomeOwnerStepId = identity.OutcomeOwnerStepId,
            QaStepId = contract.QaStepId,
            ContractJson = DeliveryReadinessPolicy.SerializeSnapshot(contract),
            ContractHash = contractHash,
            Active = true,
            CreatedAt = now
        };
        var candidateRecord = new ReviewedCandidateRecord
        {
            FlowRunId = flow.Id,
            Iteration = flow.Iteration,
            CandidateFingerprint = identity.Fingerprint,
            OutcomeOwnerStepId = identity.OutcomeOwnerStepId,
            OutcomeContractHash = identity.OutcomeContractHash,
            AcceptancePlanHash = record.AcceptancePlanHash,
            ReadinessSnapshotId = record.Id,
            ReadinessContractHash = contractHash,
            IdentityJson = ReviewedCandidateLedger.Serialize(identity),
            Active = true,
            CreatedAt = now
        };
        database.DeliveryReadinessSnapshots.Add(record);
        database.ReviewedCandidateRecords.Add(candidateRecord);
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = identity.OutcomeOwnerStepId,
            Type = DerivedEventType,
            Message =
                $"Derived host-owned Delivery readiness '{contract.State}' at revision {contract.Revision}.",
            DataJson = JsonSerializer.Serialize(new
            {
                Version = DeliveryReadinessSnapshot.CurrentVersion,
                SnapshotId = record.Id,
                record.Revision,
                State = record.State.ToString(),
                record.ContractHash,
                record.CandidateFingerprint,
                record.AcceptancePlanHash,
                record.QaContractHash,
                VerifiedCriteria = contract.Criteria.Count(item =>
                    item.Outcome == DeliveryCriterionOutcome.Verified),
                FailedCriteria = contract.Criteria
                    .Where(item => item.Outcome == DeliveryCriterionOutcome.Failed)
                    .Select(item => item.CriterionId)
                    .ToArray(),
                BlockedCriteria = contract.Criteria
                    .Where(item => item.Outcome == DeliveryCriterionOutcome.Blocked)
                    .Select(item => item.CriterionId)
                    .ToArray(),
                RequiredWaivers = contract.RequiredWaiverRiskIds,
                contract.Diagnostics
            })
        });
        await database.SaveChangesAsync(cancellationToken);
        return new DeliveryReadinessBinding(
            record,
            contract,
            candidateRecord,
            await LoadWaiversAsync(
                database,
                flow.Id,
                record.CandidateFingerprint,
                record.QaContractHash,
                cancellationToken));
    }

    /// <summary>
    /// Supersedes the active readiness assessment and its reviewed-candidate binding. Any later
    /// authorization must derive a fresh assessment, so a resolved non-ready state can never leave
    /// a stale releasable row behind.
    /// </summary>
    public async Task<bool> SupersedeCurrentAsync(
        HarnessDbContext database,
        Guid flowId,
        Guid? stepId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        var binding = await LoadCurrentAsync(database, flowId, cancellationToken);
        if (binding is null)
        {
            return false;
        }
        var now = DateTimeOffset.UtcNow;
        binding.Record.Active = false;
        binding.Record.SupersededAt = now;
        binding.Candidate.Active = false;
        binding.Candidate.SupersededAt = now;
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = flowId,
            FlowStepId = stepId,
            Type = SupersededEventType,
            Message = reason,
            DataJson = JsonSerializer.Serialize(new
            {
                Version = "delivery-readiness-superseded-v1",
                SnapshotId = binding.Record.Id,
                binding.Record.Revision,
                State = binding.Record.State.ToString(),
                binding.Record.ContractHash
            })
        });
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Loads the single active readiness binding, or <c>null</c> when none exists.</summary>
    public async Task<DeliveryReadinessBinding?> LoadCurrentAsync(
        HarnessDbContext database,
        Guid flowId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        var record = await database.DeliveryReadinessSnapshots
            .SingleOrDefaultAsync(
                item => item.FlowRunId == flowId && item.Active,
                cancellationToken);
        if (record is null)
        {
            return null;
        }
        var candidate = await database.ReviewedCandidateRecords
            .SingleOrDefaultAsync(
                item => item.FlowRunId == flowId && item.Active,
                cancellationToken);
        if (candidate is null || candidate.ReadinessSnapshotId != record.Id)
        {
            throw new DeliveryReadinessConflictException(
                DeliveryReadinessConflicts.ReconciliationRequired,
                "The active readiness assessment is not bound to a current reviewed candidate.",
                record.State,
                record.Revision,
                record.ContractHash);
        }
        var contract = DeliveryReadinessPolicy.DeserializeSnapshot(record.ContractJson);
        // The row and its canonical contract must agree. A divergence means the durable state was
        // edited outside the derivation path, so authorization fails closed instead of trusting it.
        if (contract.State != record.State ||
            !string.Equals(
                DeliveryReadinessPolicy.HashSnapshot(contract),
                record.ContractHash,
                StringComparison.Ordinal))
        {
            throw new DeliveryReadinessConflictException(
                DeliveryReadinessConflicts.ReconciliationRequired,
                "The active readiness row does not match its canonical contract.",
                record.State,
                record.Revision,
                record.ContractHash);
        }
        return new DeliveryReadinessBinding(
            record,
            contract,
            candidate,
            await LoadWaiversAsync(
                database,
                flowId,
                record.CandidateFingerprint,
                record.QaContractHash,
                cancellationToken));
    }

    /// <summary>
    /// Re-reads the authoritative rows and rejects any binding that is not exactly the one the
    /// caller claims. This is the single authorization used by review, waiver, and publication.
    /// </summary>
    public async Task<DeliveryReadinessBinding> AuthorizeAsync(
        HarnessDbContext database,
        Guid flowId,
        Guid? expectedCandidateId,
        string? expectedContractHash,
        int? expectedRevision,
        DeliveryReadinessState requiredState,
        string conflictCode,
        CancellationToken cancellationToken = default)
    {
        var binding = await LoadCurrentAsync(database, flowId, cancellationToken)
                      ?? throw new DeliveryReadinessConflictException(
                          DeliveryReadinessConflicts.ReconciliationRequired,
                          "This Delivery flow has no current host-derived readiness assessment.");
        if (expectedCandidateId is { } candidateId &&
            candidateId != binding.Candidate.Id)
        {
            throw new DeliveryReadinessConflictException(
                DeliveryReadinessConflicts.CandidateStale,
                "The submitted reviewed candidate is not the current reviewed candidate.",
                binding.State,
                binding.Revision,
                binding.ContractHash);
        }
        if (!string.IsNullOrWhiteSpace(expectedContractHash) &&
            !string.Equals(expectedContractHash, binding.ContractHash, StringComparison.Ordinal))
        {
            throw new DeliveryReadinessConflictException(
                DeliveryReadinessConflicts.ReviewStale,
                "The submitted readiness assessment has been superseded.",
                binding.State,
                binding.Revision,
                binding.ContractHash);
        }
        if (expectedRevision is { } revision && revision != binding.Revision)
        {
            throw new DeliveryReadinessConflictException(
                DeliveryReadinessConflicts.ReviewStale,
                "The submitted readiness revision has been superseded.",
                binding.State,
                binding.Revision,
                binding.ContractHash);
        }
        if (binding.State != requiredState)
        {
            throw new DeliveryReadinessConflictException(
                conflictCode,
                $"The current Delivery readiness state is '{binding.State}', not '{requiredState}'.",
                binding.State,
                binding.Revision,
                binding.ContractHash);
        }
        return binding;
    }

    /// <summary>
    /// Records immutable waiver receipts for the exact enumerated waiver-required risks. Criteria,
    /// blocking risks, and unknown identifiers are schema-invalid targets and are rejected.
    /// </summary>
    public async Task<IReadOnlyList<ReadinessWaiverRecord>> RecordWaiversAsync(
        HarnessDbContext database,
        DeliveryReadinessBinding binding,
        Guid gateId,
        IReadOnlyList<string> riskIds,
        string acknowledgement,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(riskIds);
        if (string.IsNullOrWhiteSpace(acknowledgement) || acknowledgement.Length > 4_000)
        {
            throw new ArgumentException(
                "A waiver requires a non-empty bounded acknowledgement.",
                nameof(acknowledgement));
        }
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new ArgumentException("A waiver requires an actor.", nameof(actor));
        }

        var required = binding.Contract.RequiredWaiverRiskIds
            .ToHashSet(StringComparer.Ordinal);
        var requested = riskIds.ToHashSet(StringComparer.Ordinal);
        if (requested.Count != riskIds.Count)
        {
            throw new DeliveryReadinessConflictException(
                DeliveryReadinessConflicts.WaiverNotApplicable,
                "A waiver must name each risk at most once.",
                binding.State,
                binding.Revision,
                binding.ContractHash);
        }
        var criterionIds = binding.Contract.Criteria
            .Select(item => item.CriterionId)
            .ToHashSet(StringComparer.Ordinal);
        if (requested.Overlaps(criterionIds))
        {
            throw new DeliveryReadinessConflictException(
                DeliveryReadinessConflicts.WaiverNotApplicable,
                "An acceptance criterion can never be waived.",
                binding.State,
                binding.Revision,
                binding.ContractHash);
        }
        var blocking = binding.Contract.Risks
            .Where(risk => risk.Classification == DeliveryRiskClassification.Blocking)
            .Select(risk => risk.RiskId)
            .ToHashSet(StringComparer.Ordinal);
        if (requested.Overlaps(blocking))
        {
            throw new DeliveryReadinessConflictException(
                DeliveryReadinessConflicts.WaiverNotApplicable,
                "A blocking residual risk can never be waived.",
                binding.State,
                binding.Revision,
                binding.ContractHash);
        }
        if (!requested.SetEquals(required))
        {
            throw new DeliveryReadinessConflictException(
                DeliveryReadinessConflicts.WaiverRequired,
                "A waiver must name exactly the outstanding waiver-required risks.",
                binding.State,
                binding.Revision,
                binding.ContractHash);
        }

        var existing = await database.ReadinessWaiverRecords
            .Where(item =>
                item.ReviewedCandidateId == binding.Candidate.Id &&
                item.ReadinessContractHash == binding.ContractHash)
            .ToListAsync(cancellationToken);
        var created = new List<ReadinessWaiverRecord>(existing);
        foreach (var riskId in requested.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (existing.Any(item =>
                    string.Equals(item.RiskId, riskId, StringComparison.Ordinal)))
            {
                continue;
            }
            var receipt = new ReadinessWaiverRecord
            {
                FlowRunId = binding.Record.FlowRunId,
                ReviewedCandidateId = binding.Candidate.Id,
                ReadinessSnapshotId = binding.Record.Id,
                ReadinessContractHash = binding.ContractHash,
                GateId = gateId,
                RiskId = riskId,
                Actor = actor,
                Acknowledgement = acknowledgement.Trim()
            };
            database.ReadinessWaiverRecords.Add(receipt);
            created.Add(receipt);
        }
        database.FlowEvents.Add(new FlowEvent
        {
            FlowRunId = binding.Record.FlowRunId,
            FlowStepId = binding.Record.OutcomeOwnerStepId,
            Type = WaiverGrantedEventType,
            Message =
                $"Customer waived {requested.Count} disclosed waiver-required risk(s) for readiness revision {binding.Revision}.",
            DataJson = JsonSerializer.Serialize(new
            {
                Version = "delivery-readiness-waiver-v1",
                SnapshotId = binding.Record.Id,
                ReviewedCandidateId = binding.Candidate.Id,
                binding.Record.ContractHash,
                binding.Record.CandidateFingerprint,
                RiskIds = requested.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                GateId = gateId,
                Actor = actor
            })
        });
        await database.SaveChangesAsync(cancellationToken);
        return created;
    }

    /// <summary>
    /// Records a typed authorization denial. Denials are auditable first-class events so an
    /// operator can prove a publication or acceptance was refused before any side effect.
    /// </summary>
    public static FlowEvent DenialEvent(
        Guid flowId,
        Guid? stepId,
        string code,
        string message,
        DeliveryReadinessBinding? binding) =>
        new()
        {
            FlowRunId = flowId,
            FlowStepId = stepId,
            Type = DeniedEventType,
            Message = message,
            DataJson = JsonSerializer.Serialize(new
            {
                Version = "delivery-readiness-denied-v1",
                Code = code,
                State = binding?.State.ToString(),
                Revision = binding?.Revision,
                ContractHash = binding?.ContractHash,
                CandidateFingerprint = binding?.Record.CandidateFingerprint
            })
        };

    /// <summary>
    /// Fail-closed reconciliation for databases written before readiness existed. It never invents
    /// criteria, evidence, or waivers, and never unpublishes or rewrites history.
    /// </summary>
    public async Task<int> ReconcileLegacyFlowsAsync(
        HarnessDbContext database,
        HandoffGateEngine gateEngine,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(gateEngine);
        var flows = await database.Flows
            .AsSplitQuery()
            .Include(item => item.Steps)
            .Include(item => item.GateRecords)
            .Include(item => item.Events)
            .Where(item =>
                item.ContractVersion == "studio-v2" &&
                item.Kind == FlowKind.Delivery)
            .ToListAsync(cancellationToken);
        var reconciled = 0;
        foreach (var flow in flows)
        {
            if (await database.DeliveryReadinessSnapshots.AnyAsync(
                    item => item.FlowRunId == flow.Id,
                    cancellationToken))
            {
                continue;
            }
            if (flow.Status is FlowStatus.Abandoned or FlowStatus.Abandoning)
            {
                continue;
            }
            var hasReviewHistory = flow.GateRecords.Any(gate =>
                gate.ActionType == HandoffActionType.CustomerReview);
            if (!hasReviewHistory && flow.Status is not FlowStatus.Approved)
            {
                // The flow never reached review, so the new contracts apply from its next
                // readiness assessment with no legacy row required.
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var unresolved in flow.GateRecords.Where(gate =>
                         !gate.Resolved &&
                         gate.ActionType == HandoffActionType.CustomerReview))
            {
                var superseded = gateEngine.PrepareSupersession(
                    unresolved,
                    "harness",
                    "Superseded because this review predates host-derived readiness verification.",
                    now);
                unresolved.Resolved = superseded.Resolved;
                unresolved.Approved = superseded.Approved;
                unresolved.ResolvedBy = superseded.ResolvedBy;
                unresolved.ResolutionNote = superseded.ResolutionNote;
                unresolved.ResolvedAt = superseded.ResolvedAt;
                gateEngine.RestoreHistory([superseded]);
            }

            var record = new DeliveryReadinessSnapshotRecord
            {
                FlowRunId = flow.Id,
                Iteration = flow.Iteration,
                Revision = 1,
                State = DeliveryReadinessState.Blocked,
                Reconciliation = DeliveryReadinessReconciliation.LegacyUnverified,
                CandidateFingerprint = OutcomeVerificationRules.ComputeSha256(
                    $"legacy-unverified:{flow.Id:D}:{flow.Iteration}"),
                AcceptancePlanHash = OutcomeVerificationRules.ComputeSha256(
                    $"legacy-unverified-plan:{flow.Id:D}:{flow.Iteration}"),
                OutcomeContractHash = OutcomeVerificationRules.ComputeSha256(
                    flow.OutcomeContractJson ?? string.Empty),
                QaContractHash = string.Empty,
                OutcomeOwnerStepId = Guid.Empty,
                QaStepId = Guid.Empty,
                ContractJson = "{}",
                ContractHash = OutcomeVerificationRules.ComputeSha256(
                    $"legacy-unverified-readiness:{flow.Id:D}:{flow.Iteration}"),
                Active = true,
                CreatedAt = now
            };
            var legacyContract = new DeliveryReadinessSnapshot(
                DeliveryReadinessSnapshot.CurrentVersion,
                record.Id,
                flow.Id,
                flow.Iteration,
                1,
                DeliveryReadinessState.Blocked,
                DeliveryReadinessReconciliation.LegacyUnverified,
                record.CandidateFingerprint,
                record.AcceptancePlanHash,
                record.OutcomeContractHash,
                string.Empty,
                Guid.Empty,
                Guid.Empty,
                [],
                [],
                [],
                [],
                [
                    "this Delivery result predates host-derived readiness verification and was " +
                    "never proven against typed acceptance criteria"
                ],
                now);
            record.ContractJson = DeliveryReadinessPolicy.SerializeSnapshot(legacyContract);
            record.ContractHash = DeliveryReadinessPolicy.HashSnapshot(legacyContract);
            var candidateRecord = new ReviewedCandidateRecord
            {
                FlowRunId = flow.Id,
                Iteration = flow.Iteration,
                CandidateFingerprint = record.CandidateFingerprint,
                OutcomeOwnerStepId = Guid.Empty,
                OutcomeContractHash = record.OutcomeContractHash,
                AcceptancePlanHash = record.AcceptancePlanHash,
                ReadinessSnapshotId = record.Id,
                ReadinessContractHash = record.ContractHash,
                IdentityJson = "{}",
                Active = true,
                CreatedAt = now
            };
            database.DeliveryReadinessSnapshots.Add(record);
            database.ReviewedCandidateRecords.Add(candidateRecord);
            database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                Type = ReconciledEventType,
                Message = flow.Status == FlowStatus.Approved
                    ? "Published without host-derived readiness verification; publication authority is withheld until reconciliation."
                    : "Fail-closed reconciliation: this flow needs a new host-derived readiness assessment before review.",
                DataJson = JsonSerializer.Serialize(new
                {
                    Version = "delivery-readiness-reconciliation-v1",
                    SnapshotId = record.Id,
                    Reconciliation = record.Reconciliation.ToString(),
                    PreviousStatus = flow.Status.ToString(),
                    record.ContractHash
                })
            });
            reconciled++;
        }
        if (reconciled > 0)
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        return reconciled;
    }

    /// <summary>
    /// Loads the waiver receipts that apply to one readiness assessment.
    ///
    /// Receipts are written against the pre-waiver assessment, and granting a waiver deliberately
    /// re-derives a new revision with a new contract hash. Filtering by that new hash would orphan
    /// every receipt, so the durable binding used here is the pair that actually survives the
    /// re-derivation: the sealed candidate fingerprint and the exact QA contract the receipts were
    /// granted against. New QA facts change that pair and therefore invalidate prior consent.
    /// </summary>
    private static async Task<IReadOnlyList<ReadinessWaiverRecord>> LoadWaiversAsync(
        HarnessDbContext database,
        Guid flowId,
        string candidateFingerprint,
        string qaContractHash,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(qaContractHash))
        {
            return [];
        }
        return await database.ReadinessWaiverRecords
            .Join(
                database.DeliveryReadinessSnapshots,
                waiver => waiver.ReadinessSnapshotId,
                snapshot => snapshot.Id,
                (waiver, snapshot) => new { waiver, snapshot })
            .Where(item =>
                item.waiver.FlowRunId == flowId &&
                item.snapshot.CandidateFingerprint == candidateFingerprint &&
                item.snapshot.QaContractHash == qaContractHash)
            .Select(item => item.waiver)
            .OrderBy(item => item.RiskId)
            .ToListAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> LoadApplicableWaiverRiskIdsAsync(
        HarnessDbContext database,
        Guid flowId,
        string candidateFingerprint,
        string qaContractHash,
        CancellationToken cancellationToken) =>
        [.. (await LoadWaiversAsync(
                database,
                flowId,
                candidateFingerprint,
                qaContractHash,
                cancellationToken))
            .Select(item => item.RiskId)
            .Distinct(StringComparer.Ordinal)];

    private static IReadOnlyList<T> ReadLedger<T>(
        IEnumerable<FlowEvent> events,
        string eventType,
        Func<T, bool> predicate)
        where T : class
    {
        var results = new List<T>();
        foreach (var item in events
                     .Where(item => item.Type == eventType)
                     .OrderBy(item => item.CreatedAt))
        {
            if (string.IsNullOrWhiteSpace(item.DataJson))
            {
                continue;
            }
            try
            {
                var entry = JsonSerializer.Deserialize<T>(item.DataJson, LedgerOptions);
                if (entry is not null && predicate(entry))
                {
                    results.Add(entry);
                }
            }
            catch (JsonException)
            {
                // A malformed durable envelope is treated as absent so derivation fails closed.
            }
        }
        return results;
    }

    private static IReadOnlyList<Guid> PreMortemStepIds(FlowRun flow) =>
        [.. flow.Steps
            .Where(step =>
                step.Iteration == flow.Iteration &&
                step.InvocationKind == ExecutionInvocationKind.PreMortem)
            .Select(step => step.Id)];

    /// <summary>
    /// Evidence identifiers the host itself issued for this iteration, read from the durable
    /// registry. The set is always authoritative, so a fabricated identifier becomes a contract
    /// error and an empty registry fails closed instead of authorizing anything.
    /// </summary>
    private static IReadOnlyList<string> KnownEvidenceIds(FlowRun flow) =>
        KnownEvidenceIds(flow.Events, flow.Iteration);
}

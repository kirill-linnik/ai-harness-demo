using System.Text.Encodings.Web;
using System.Text.Json;
using AiHarnessDemo.Core.Verification;

namespace AiHarnessDemo.Tests;

/// <summary>
/// A compact regression fixture derived from Delivery flow
/// <c>36aecd50-3007-4eeb-87a2-e3cd02576dcb</c>. It preserves the contradictory customer-visible
/// facts while expressing them only through current, unversioned contracts.
/// </summary>
internal sealed class ReferenceFlowArtifact
{
    private const string FileName = "reference-flow-36aecd50.json";

    private static readonly Lazy<ReferenceFlowArtifact> Instance =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    private ReferenceFlowArtifact(
        Guid id,
        string status,
        string kind,
        int iteration,
        string originalRequest,
        string consolidatedRequest,
        string outcomeLabel,
        string publicationStatus,
        RecordedReview review,
        RecordedOutcome outcome,
        string outcomeJson,
        IReadOnlyList<string> stepRoles)
    {
        Id = id;
        Status = status;
        Kind = kind;
        Iteration = iteration;
        OriginalRequest = originalRequest;
        ConsolidatedRequest = consolidatedRequest;
        OutcomeLabel = outcomeLabel;
        PublicationStatus = publicationStatus;
        Review = review;
        Outcome = outcome;
        OutcomeJson = outcomeJson;
        StepRoles = stepRoles;
    }

    public static ReferenceFlowArtifact Current => Instance.Value;

    public Guid Id { get; }

    /// <summary>The recorded terminal flow status. The incident value is <c>Approved</c>.</summary>
    public string Status { get; }

    public string Kind { get; }

    public int Iteration { get; }

    /// <summary>The exact customer request the incident flow was created from.</summary>
    public string OriginalRequest { get; }

    /// <summary>The exact confirmed <c>intake</c> brief JSON persisted for the flow.</summary>
    public string ConsolidatedRequest { get; }

    public string OutcomeLabel { get; }

    /// <summary>The recorded publication status. The incident value is <c>Published</c>.</summary>
    public string PublicationStatus { get; }

    public RecordedReview Review { get; }

    public RecordedOutcome Outcome { get; }

    /// <summary>The exact <c>flow outcome</c> projection the API returned for the flow.</summary>
    public string OutcomeJson { get; }

    /// <summary>
    /// The captured outcome rebuilt into the strict <c>flow outcome</c> envelope the host parser
    /// requires. Only the contract property names are supplied by this helper; the Goal, Summary,
    /// and ImplementationDetails text is byte-identical to the captured incident, so the replayed
    /// contradiction is the original one.
    /// </summary>
    public string OutcomeContractJson =>
        JsonSerializer.Serialize(
            new
            {
                Outcome.Goal,
                Outcome.Summary,
                Outcome.ImplementationDetails,
                Artifacts = Array.Empty<object>()
            },
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    public IReadOnlyList<string> StepRoles { get; }

    /// <summary>
    /// The confirmed success criteria of the persisted brief, in order. They are the customer's own
    /// acceptance statements and become the mandatory acceptance plan in the replay.
    /// </summary>
    public IReadOnlyList<string> SuccessCriteria =>
        JsonSerializer.Deserialize<RecordedBrief>(
            ConsolidatedRequest,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!
            .SuccessCriteria;

    private static ReferenceFlowArtifact Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", FileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The captured reference flow artifact '{FileName}' was not copied to the test output.",
                path);
        }
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        string Text(string name) => root.GetProperty(name).GetString()!;

        var outcomeJson = Text("Outcome");
        return new ReferenceFlowArtifact(
            Guid.Parse(Text("Id")),
            Text("Status"),
            Text("Kind"),
            root.GetProperty("Iteration").GetInt32(),
            Text("OriginalRequest"),
            Text("ConsolidatedRequest"),
            Text("OutcomeLabel"),
            Text("PublicationStatus"),
            JsonSerializer.Deserialize<RecordedReview>(Text("Review"), options)!,
            JsonSerializer.Deserialize<RecordedOutcome>(outcomeJson, options)!,
            outcomeJson,
            [.. JsonSerializer.Deserialize<List<RecordedStep>>(Text("Steps"), options)!
                .Select(step => step.AgentRole)]);
    }

    internal sealed record RecordedReview(
        Guid? GateId,
        bool Available,
        bool Resolved,
        bool? Approved,
        string? Decision,
        string PublicationStatus);

    internal sealed record RecordedOutcome(
        string Goal,
        string Summary,
        IReadOnlyList<string> ImplementationDetails,
        IReadOnlyList<JsonElement> Artifacts);

    internal sealed record RecordedStep(string AgentRole, string Status);

    private sealed record RecordedBrief(
        string Goal,
        IReadOnlyList<string> Details,
        IReadOnlyList<string> SuccessCriteria,
        IReadOnlyList<string> Constraints,
        IReadOnlyList<string> Assumptions);
}

/// <summary>
/// Builds the mandatory acceptance plan and strict outcome-QA contracts for the
/// captured incident. The criteria come from the customer's own confirmed success criteria, and the
/// failing results mirror the four gaps the recorded outcome document itself confirmed.
/// </summary>
internal static class ReferenceFlowContracts
{
    public static DeliveryAcceptancePlan AcceptancePlan() =>
        new(
            [.. ReferenceFlowArtifact.Current.SuccessCriteria.Select((requirement, index) =>
                new DeliveryAcceptanceCriterion
                {
                    Id = $"AC-{index + 1:000}",
                    Requirement = requirement,
                    Verification =
                        "Open both brands in the offline preview and confirm the observable behavior.",
                    OwnerRoles = ["software-engineer"],
                    EvidenceKinds = [OutcomeEvidenceKind.Observation],
                    CustomerVisible = true
                })]);

    public static string AcceptancePlanHash() =>
        DeliveryReadinessPolicy.HashAcceptancePlan(AcceptancePlan());

    /// <summary>
    /// The recorded contradiction expressed as typed facts, built from what the host actually
    /// injected into the verification prompt. Gap 1 (non-reproducible previews) and gap 2 (masked
    /// archive error state) map onto the customer's own criteria; gap 3 (unreviewed populated
    /// YouTube layout) is a waiver-required residual risk, present to prove that consent can never
    /// rescue a failed criterion.
    /// </summary>
    public static string ContradictoryQaJson(string? prompt = null)
    {
        var criteria = ReferenceFlowArtifact.Current.SuccessCriteria;
        var previewCriterion = IndexOfContaining(criteria, "offline preview");
        var stateCriterion = IndexOfContaining(criteria, "error states");
        var results = criteria
            .Select((_, index) => index == previewCriterion || index == stateCriterion
                ? (
                    Id: $"AC-{index + 1:000}",
                    Outcome: DeliveryCriterionOutcome.Failed,
                    Rationale: index == previewCriterion
                        ? "No documented command builds either brand into the folder the preview generator requires, so the delivered previews cannot be reproduced."
                        : "The archive and seminar guards swallow every load failure and mark the page initialized, so an outage renders as an ordinary empty result.",
                    Remediation: (string?)(index == previewCriterion
                        ? "Add a tracked command that builds each brand into the generator input path and fails loudly when it is missing."
                        : "Track per-source loading, partial, and error state and render a distinct error notice with retry."))
                : (
                    Id: $"AC-{index + 1:000}",
                    Outcome: DeliveryCriterionOutcome.Verified,
                    Rationale: "The host-observed check produced the expected result on both brands.",
                    Remediation: (string?)null))
            .ToArray();
        return BuildQaJson(
            results,
            [
                ("RR-001", DeliveryRiskClassification.WaiverRequired,
                    "The YouTube section was only ever reviewed with an empty playlist shim.",
                    "A populated video layout could regress at some viewports without being seen.")
            ],
            prompt);
    }

    /// <summary>The same request and plan with every criterion honestly verified.</summary>
    public static string ResolvedQaJson(string? prompt = null) =>
        BuildQaJson(
            [.. ReferenceFlowArtifact.Current.SuccessCriteria.Select((_, index) => (
                Id: $"AC-{index + 1:000}",
                Outcome: DeliveryCriterionOutcome.Verified,
                Rationale: "The host-observed check produced the expected result on both brands.",
                Remediation: (string?)null))],
            [],
            prompt);

    public static string Block(string qaJson) =>
        $"{DeliveryReadinessPolicy.QaBeginMarker}{Environment.NewLine}" +
        qaJson +
        $"{Environment.NewLine}{DeliveryReadinessPolicy.QaEndMarker}";

    public static int FailedCriterionIndex(string fragment) =>
        IndexOfContaining(ReferenceFlowArtifact.Current.SuccessCriteria, fragment);

    private static int IndexOfContaining(
        IReadOnlyList<string> values,
        string fragment)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index].Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }
        throw new InvalidOperationException(
            $"The captured brief has no success criterion containing '{fragment}'.");
    }

    private static string BuildQaJson(
        IReadOnlyList<(string Id, DeliveryCriterionOutcome Outcome, string Rationale, string? Remediation)> results,
        IReadOnlyList<(string RiskId, DeliveryRiskClassification Classification, string Statement, string Impact)> risks,
        string? prompt)
    {
        // When a prompt is supplied the document is built from what the host injected, so the
        // regression only passes if the production path really carried the hash and evidence ids.
        var planHash = prompt is null
            ? AcceptancePlanHash()
            : DeliveryReadinessFixtures.PlanHashFromPrompt(prompt);
        var evidence = prompt is null
            ? null
            : DeliveryReadinessFixtures.EvidenceIdsFromPrompt(prompt);
        string EvidenceFor(string id) =>
            evidence is null
                ? $"EV-{id}"
                : evidence.Count > 0
                    ? evidence[0]
                    : "EV-MISSING";
        var criteria = string.Join(
            ",",
            results.Select(result =>
                $$"""
                {"CriterionId":"{{result.Id}}","Outcome":"{{result.Outcome}}","EvidenceIds":["{{EvidenceFor(result.Id)}}"],"Rationale":{{JsonSerializer.Serialize(result.Rationale)}},"Remediation":{{(result.Remediation is null ? "null" : JsonSerializer.Serialize(result.Remediation))}},"ResponsibleRoles":{{(result.Outcome == DeliveryCriterionOutcome.Verified ? "[]" : "[\"software-engineer\"]")}}}
                """));
        var riskItems = string.Join(
            ",",
            risks.Select(risk =>
                $$"""
                {"RiskId":"{{risk.RiskId}}","Classification":"{{risk.Classification}}","Severity":"Medium","Statement":{{JsonSerializer.Serialize(risk.Statement)}},"Impact":{{JsonSerializer.Serialize(risk.Impact)}},"EvidenceIds":["{{EvidenceFor(risk.RiskId)}}"],"CriterionIds":[],"PreMortemFindingId":"PM-003"}
                """));
        var blocked = results.Any(item => item.Outcome == DeliveryCriterionOutcome.Blocked) ||
                      risks.Any(item =>
                          item.Classification == DeliveryRiskClassification.Blocking);
        var verdict = blocked
            ? "BLOCKED"
            : results.All(item => item.Outcome == DeliveryCriterionOutcome.Verified)
                ? "PASS"
                : "FAIL";
        return
            $$"""
            {"AcceptancePlanHash":"{{planHash}}","Verdict":"{{verdict}}","Criteria":[{{criteria}}],"ResidualRisks":[{{riskItems}}],"PlanGaps":[]}
            """;
    }
}

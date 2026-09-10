using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AiHarnessDemo.Core.Verification;

public static class OutcomeVerificationRules
{
    public const string AggregateVersion = "outcome-verification-state-v1";
    public const string AcceptanceVersion = "outcome-acceptance-v1";
    public const string EvidenceVersion = "outcome-evidence-v1";
    public const string QaVersion = "outcome-qa-v1";
    public const string CandidateManifestVersion = "candidate-manifest-v1";

    public const string AcceptanceBeginMarker = "OUTCOME_ACCEPTANCE_PLAN_V1_BEGIN";
    public const string AcceptanceEndMarker = "OUTCOME_ACCEPTANCE_PLAN_V1_END";
    public const string EvidenceBeginMarker = "OUTCOME_EVIDENCE_V1_BEGIN";
    public const string EvidenceEndMarker = "OUTCOME_EVIDENCE_V1_END";
    public const string QaBeginMarker = "OUTCOME_QA_RESULT_V1_BEGIN";
    public const string QaEndMarker = "OUTCOME_QA_RESULT_V1_END";

    public const int MaximumAggregateBytes = 128 * 1024;
    public const int MaximumAcceptanceBytes = 24 * 1024;
    public const int MaximumEvidenceBytes = 32 * 1024;
    public const int MaximumQaBytes = 64 * 1024;
    public const int MaximumPriorIterations = 100;
    public const int MaximumJsonDepth = 32;

    private static readonly Regex CriterionIdPattern = new(
        "^AC-[0-9]{3}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex DigestPattern = new(
        "^sha256:[0-9a-f]{64}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex GitObjectIdPattern = new(
        "^(?:[0-9a-f]{40}|[0-9a-f]{64})$",
        RegexOptions.CultureInvariant);
    private static readonly Regex RepositoryIdentityPattern = new(
        "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$",
        RegexOptions.CultureInvariant);
    private static readonly Regex ObservableActionPattern = new(
        @"\b(?:assert|capture|cat|check|compare|confirm|curl|inspect|invoke-webrequest|load|open|parse|query|read|render|request|review|run|select-string|test|type|validate|verify)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ObservableOutcomePattern = new(
        @"\b(?:assert|check|confirm|ensure|observe|verify)\s+that\b|\b(?:contain(?:s|ed)?|create(?:s|d)?|display(?:s|ed)?|emit(?:s|ted)?|equal(?:s|ed)?|fail(?:s|ed)?|include(?:s|d)?|match(?:es|ed)?|omit(?:s|ted)?|parse(?:s|d)?|pass(?:es|ed)?|persist(?:s|ed)?|reject(?:s|ed)?|render(?:s|ed)?|respond(?:s|ed)?|return(?:s|ed)?|show(?:s|ed)?|update(?:s|d)?|write(?:s|n)?)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ObservableSubjectPattern = new(
        @"(?<![\w./-])(?:assertion|behavior|body|button|case|content|copy|cta|digest|error|field|gate|heading|link|message|outcome|output|page|payload|preview|release(?:\s+gate)?|response|row|scenario|schema|state|status|test(?:s)?|text|title|value|warning)(?![\w./-])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex StructuredObservationPattern = new(
        "\"[^\"]+\"|'[^']+'|<[^>]+>|\\{[^}]+\\}|\\[[^\\]]+\\]|\\b[0-9]+\\b\\s*(?:bytes?|checks?|items?|messages?|ms|rows?|status|tests?)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex SubjectValuePattern = new(
        @"(?<![\w./-])(?:behavior|body|button|content|copy|cta|digest|error|field|heading|link|message|output|page|payload|preview|release(?:\s+gate)?|response|row|schema|state|status|text|title|value|warning)(?![\w./-]).{0,96}\b(?:is|was|were|:=|=>|equal(?:s|ed)?)\b.{0,96}(?:""[^""]+""|'[^']+'|<[^>]+>|[0-9]+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ContentInspectionCommandPattern = new(
        @"(?:^|\s)(?:cat|curl|findstr|get-content|grep|invoke-webrequest|jq|select-string|type)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ListingOrExistenceProbePattern = new(
        @"(?:^|\s)(?:dir|find|get-childitem|get-item|gci|ls|stat|test-path)\b|(?:^|\s)test\s+-(?:a|d|e|f|L)\b|(?:^|\s)\[\s+-(?:a|d|e|f|L)\s+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ExistenceOnlyStatementPattern = new(
        @"\b(?:available|exists?|existing|found|listed|located|open(?:ed|able)?|present|readable)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex BareRelativePathPattern = new(
        @"^(?:\.{0,2}/)?[\w.-]+(?:/[\w.-]+)+$",
        RegexOptions.CultureInvariant);
    private static readonly string[] ReleaseTerms =
    [
        "package", "packaging", "preview", "publish", "publication", "release"
    ];
    private static readonly JsonSerializerOptions StrictJsonOptions = CreateJsonOptions();

    public static OutcomeVerificationState CreateInitialState(
        int iteration,
        int maxRounds,
        DateTimeOffset? now = null)
    {
        if (iteration < 1)
        {
            throw Invalid("iteration must be positive");
        }
        if (maxRounds is < 1 or > 10)
        {
            throw Invalid("maxRounds must be an integer from 1 through 10");
        }

        return new OutcomeVerificationState
        {
            Iteration = iteration,
            MaxRounds = maxRounds,
            UpdatedAt = now ?? DateTimeOffset.UtcNow
        };
    }

    public static OutcomeVerificationState StartNextIteration(
        OutcomeVerificationState current,
        int nextIteration,
        int maxRounds,
        DateTimeOffset? now = null)
    {
        ValidateAggregate(current);
        if (nextIteration != current.Iteration + 1)
        {
            throw Invalid("the next outcome-verification iteration must be sequential");
        }
        if (maxRounds is < 1 or > 10)
        {
            throw Invalid("maxRounds must be an integer from 1 through 10");
        }
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var next = CreateInitialState(nextIteration, maxRounds, timestamp);
        next.Status = OutcomeVerificationStatus.Planning;
        next.TrustedRepositories = [.. current.TrustedRepositories];
        next.PriorIterations = [.. current.PriorIterations];
        next.PriorIterations.Add(new OutcomeVerificationIterationArchive(
            current.Iteration,
            OutcomeVerificationStatus.Superseded,
            current.MaxRounds,
            current.ManualRoundsGranted,
            current.PlannedRoles.ToArray(),
            current.InitialPlanSemanticRootId,
            current.InitialDeliverySemanticRootIds.ToArray(),
            current.ProcessedSemanticRootIds.ToArray(),
            current.AcceptancePlan,
            current.Evidence.ToArray(),
            current.EvidenceProcessing.ToArray(),
            current.Rounds.ToArray(),
            current.CurrentCandidate,
            current.Publication,
            current.VerifiedCandidateFingerprint,
            timestamp));
        return next;
    }

    public static OutcomeAcceptancePlan ParseAcceptancePlan(
        string output,
        IReadOnlyList<string> downstreamRoles)
    {
        ArgumentNullException.ThrowIfNull(output);
        ValidatePlannedRoles(downstreamRoles);
        var json = ExtractMarkedJson(
            output,
            AcceptanceBeginMarker,
            AcceptanceEndMarker,
            "acceptance plan");
        EnsureSize(json, MaximumAcceptanceBytes, "acceptance plan");
        var document = DeserializeStrict<OutcomeAcceptancePlan>(json, "acceptance plan");
        var errors = ValidateAcceptancePlan(document, downstreamRoles);
        if (errors.Count > 0)
        {
            throw new OutcomeVerificationValidationException(errors);
        }

        var roleOrder = downstreamRoles
            .Select((role, index) => (role, index))
            .ToDictionary(item => item.role, item => item.index, StringComparer.Ordinal);
        return new OutcomeAcceptancePlan(
            AcceptanceVersion,
            document.Criteria.Select(criterion => new OutcomeAcceptanceCriterion(
                criterion.Id,
                criterion.Requirement.Trim(),
                criterion.Verification.Trim(),
                criterion.OwnerRoles
                    .OrderBy(role => roleOrder[role])
                    .ToArray(),
                criterion.EvidenceKinds
                    .OrderBy(kind => (int)kind)
                    .ToArray(),
                criterion.CustomerVisible))
            .ToArray());
    }

    public static OutcomeAcceptancePlanSnapshot CreateAcceptanceSnapshot(
        OutcomeAcceptancePlan plan,
        Guid sourceStepId) =>
        new(
            HashAcceptancePlan(plan),
            sourceStepId,
            plan.Criteria);

    public static string HashAcceptancePlan(OutcomeAcceptancePlan plan)
    {
        var errors = ValidateAcceptancePlan(plan, allowedRoles: null);
        if (errors.Count > 0)
        {
            throw new OutcomeVerificationValidationException(errors);
        }

        return ComputeSha256(SerializeCanonical(plan));
    }

    public static IReadOnlyList<OutcomeEvidence> ParseDeliveryEvidence(
        string output,
        OutcomeAcceptancePlanSnapshot activePlan,
        string producerRole,
        Guid producerStepId,
        DateTimeOffset? producedAt = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(activePlan);
        var json = ExtractMarkedJson(
            output,
            EvidenceBeginMarker,
            EvidenceEndMarker,
            "delivery evidence");
        EnsureSize(json, MaximumEvidenceBytes, "delivery evidence");
        var document = DeserializeStrict<OutcomeDeliveryEvidenceDocument>(
            json,
            "delivery evidence");
        var errors = new List<string>();
        if (!string.Equals(document.Version, EvidenceVersion, StringComparison.Ordinal))
        {
            errors.Add($"version must be exactly '{EvidenceVersion}'");
        }
        if (document.Items is null)
        {
            errors.Add("items is required");
        }
        else
        {
            if (document.Items.Count > 24)
            {
                errors.Add("items must contain at most 24 entries");
            }

            var criteria = activePlan.Criteria.ToDictionary(
                criterion => criterion.Id,
                StringComparer.Ordinal);
            foreach (var (item, index) in document.Items.Select(
                         (item, index) => (item, index)))
            {
                ValidateEvidenceInput(item, index, criteria, producerRole, errors);
            }
            foreach (var group in document.Items
                         .Where(item => item is not null)
                         .GroupBy(item => item.CriterionId, StringComparer.Ordinal))
            {
                if (group.Count() > 4)
                {
                    errors.Add(
                        $"criterion '{group.Key}' has more than four evidence items from one step");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new OutcomeVerificationValidationException(errors);
        }

        var timestamp = producedAt ?? DateTimeOffset.UtcNow;
        return (document.Items ?? [])
            .Select(item => new OutcomeEvidence(
                $"E-{Guid.NewGuid():D}",
                item.CriterionId,
                item.Disposition,
                item.Kind,
                item.Locator.Trim(),
                item.ObservedResult.Trim(),
                item.ExitCode,
                item.ContentDigest,
                producerRole,
                producerStepId,
                timestamp))
            .ToArray();
    }

    public static void MergeEvidence(
        OutcomeVerificationState state,
        IEnumerable<OutcomeEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(evidence);
        var incoming = evidence.ToArray();
        var knownIds = state.Evidence
            .Select(item => item.EvidenceId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var item in incoming)
        {
            if (knownIds.Add(item.EvidenceId))
            {
                state.Evidence.Add(item);
            }
        }

        state.Evidence = state.Evidence
            .GroupBy(item => item.CriterionId, StringComparer.Ordinal)
            .SelectMany(group => group
                .OrderByDescending(item => item.ProducedAt)
                .ThenByDescending(item => item.EvidenceId, StringComparer.Ordinal)
                .Take(8))
            .OrderBy(item => item.CriterionId, StringComparer.Ordinal)
            .ThenBy(item => item.ProducedAt)
            .ThenBy(item => item.EvidenceId, StringComparer.Ordinal)
            .ToList();
        state.UpdatedAt = DateTimeOffset.UtcNow;
        ValidateAggregate(state);
    }

    public static OutcomeQaResult ParseQaResult(
        string output,
        OutcomeAcceptancePlanSnapshot activePlan,
        string candidateFingerprint,
        IReadOnlyCollection<string>? knownEvidenceIds = null,
        IReadOnlyCollection<string>? allowedOwnerRoles = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(activePlan);
        var json = ExtractMarkedJson(
            output,
            QaBeginMarker,
            QaEndMarker,
            "QA result");
        EnsureSize(json, MaximumQaBytes, "QA result");
        var document = DeserializeStrict<OutcomeQaResult>(json, "QA result");
        var errors = ValidateQaResult(
            document,
            activePlan,
            candidateFingerprint,
            knownEvidenceIds,
            allowedOwnerRoles);
        if (errors.Count > 0)
        {
            throw new OutcomeVerificationValidationException(errors);
        }

        var normalizedCriteria = document.Criteria.Select(result =>
            new OutcomeQaCriterionResult(
                result.CriterionId,
                result.Status,
                result.EvidenceIds.Distinct(StringComparer.Ordinal).ToArray(),
                result.ChecksPerformed.Select(check => new OutcomeQaCheck(
                    check.Kind,
                    check.Locator.Trim(),
                    check.ObservedResult.Trim(),
                    check.ExitCode)).ToArray(),
                result.Rationale.Trim(),
                result.ResponsibleRoles.ToArray(),
                string.IsNullOrWhiteSpace(result.Remediation)
                    ? null
                    : result.Remediation.Trim()))
            .ToArray();
        var normalizedGaps = (document.PlanGaps ?? []).Select(gap => new OutcomePlanGap(
            gap.Requirement.Trim(),
            gap.Verification.Trim(),
            gap.OwnerRoles.ToArray(),
            gap.Rationale.Trim())).ToArray();
        return new OutcomeQaResult(
            QaVersion,
            activePlan.Hash,
            candidateFingerprint,
            DeriveGlobalVerdict(normalizedCriteria, normalizedGaps),
            normalizedCriteria,
            normalizedGaps);
    }

    public static OutcomeQaVerdict DeriveGlobalVerdict(
        IReadOnlyCollection<OutcomeQaCriterionResult> criteria,
        IReadOnlyCollection<OutcomePlanGap> planGaps)
    {
        if (criteria.Any(item => item.Status == OutcomeCriterionStatus.BLOCKED))
        {
            return OutcomeQaVerdict.BLOCKED;
        }

        return criteria.Count > 0 &&
               criteria.All(item => item.Status == OutcomeCriterionStatus.PASS) &&
               planGaps.Count == 0
            ? OutcomeQaVerdict.PASS
            : OutcomeQaVerdict.FAIL;
    }

    public static string SerializeAggregate(OutcomeVerificationState state)
    {
        ValidateAggregate(state);
        var json = SerializeCanonical(state);
        EnsureSize(json, MaximumAggregateBytes, "outcome verification aggregate");
        return json;
    }

    public static OutcomeVerificationState DeserializeAggregate(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw Invalid("outcome verification aggregate is empty");
        }
        EnsureSize(json, MaximumAggregateBytes, "outcome verification aggregate");
        var state = DeserializeStrict<OutcomeVerificationState>(
            json,
            "outcome verification aggregate");
        ValidateAggregate(state);
        return state;
    }

    public static void ValidateAggregate(OutcomeVerificationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var errors = new List<string>();
        if (!string.Equals(state.Version, AggregateVersion, StringComparison.Ordinal))
        {
            errors.Add($"aggregate version must be exactly '{AggregateVersion}'");
        }
        if (state.Iteration < 1)
        {
            errors.Add("aggregate iteration must be positive");
        }
        if (state.ManualRoundsGranted is < 0 or > 10)
        {
            errors.Add("manualRoundsGranted must be between zero and ten");
        }
        if (state.MaxRounds is < 1 or > 20 ||
            state.MaxRounds - state.ManualRoundsGranted is < 1 or > 10)
        {
            errors.Add(
                "maxRounds must contain a 1-10 policy snapshot plus at most ten manually granted rounds");
        }
        if (!Enum.IsDefined(state.Status))
        {
            errors.Add("aggregate status is unsupported");
        }
        if (state.Evidence is null)
        {
            errors.Add("evidence is required");
        }
        if (state.Rounds is null)
        {
            errors.Add("rounds is required");
        }
        if (state.PendingOwnerRoles is null)
        {
            errors.Add("pendingOwnerRoles is required");
        }
        if (state.PlannedRoles is null)
        {
            errors.Add("plannedRoles is required");
        }
        else if (state.PlannedRoles.Count !=
                 state.PlannedRoles.Distinct(StringComparer.Ordinal).Count() ||
                 state.PlannedRoles.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("plannedRoles must contain unique, non-empty role IDs");
        }
        ValidateSemanticRoots(
            state.InitialPlanSemanticRootId,
            state.InitialDeliverySemanticRootIds,
            state.ProcessedSemanticRootIds,
            "aggregate",
            errors);
        if (state.PriorIterations is null)
        {
            errors.Add("priorIterations is required");
        }
        else
        {
            if (state.PriorIterations.Count > MaximumPriorIterations)
            {
                errors.Add(
                    $"priorIterations must contain at most {MaximumPriorIterations} archived iterations");
            }
            if (state.PriorIterations
                .Where(item => item is not null)
                .Select(item => item.Iteration)
                .Distinct().Count() != state.PriorIterations.Count)
            {
                errors.Add("priorIterations must not contain duplicate iteration numbers");
            }
            var previousIteration = 0;
            foreach (var (archive, index) in state.PriorIterations.Select(
                         (archive, index) => (archive, index)))
            {
                if (archive is null)
                {
                    errors.Add($"prior iteration entry {index + 1} cannot be null");
                    continue;
                }
                if (archive.Iteration < 1 ||
                    archive.Iteration >= state.Iteration ||
                    archive.Status != OutcomeVerificationStatus.Superseded)
                {
                    errors.Add("each prior iteration must be earlier and marked Superseded");
                }
                if (archive.Iteration <= previousIteration)
                {
                    errors.Add(
                        "priorIterations must be ordered by strictly increasing iteration");
                }
                previousIteration = archive.Iteration;
                ValidateArchivedIteration(
                    archive,
                    state.TrustedRepositories,
                    errors);
            }
        }
        if (state.TrustedRepositories is null)
        {
            errors.Add("trustedRepositories is required");
        }
        else
        {
            ValidateManifestOrdering(
                state.TrustedRepositories
                    .Where(item => item is not null)
                    .Select(item => item.RelativePath),
                "trusted repository",
                errors);
            foreach (var repository in state.TrustedRepositories)
            {
                if (repository is null)
                {
                    errors.Add("trusted repository entries cannot be null");
                    continue;
                }
                if (!IsSafeRelativePath(repository.RelativePath) ||
                    (repository.RemoteRepository.Length > 0 &&
                     !RepositoryIdentityPattern.IsMatch(repository.RemoteRepository)))
                {
                    errors.Add(
                        $"trusted repository '{repository.RelativePath}' has an invalid path or remote identity");
                }
            }
        }
        if (state.EvidenceProcessing is null)
        {
            errors.Add("evidenceProcessing is required");
        }
        ValidateProofLedger(
            state,
            errors,
            labelPrefix: null,
            allowEmptySuperseded: false,
            allowVerifiedSupersededPublication: false);
        if (state.PendingOwnerRoles is not null &&
            state.PendingOwnerRoles.Count !=
            state.PendingOwnerRoles.Distinct(StringComparer.Ordinal).Count())
        {
            errors.Add("pendingOwnerRoles must not contain duplicates");
        }
        if (state.PendingOwnerRoles is not null &&
            state.PendingOwnerRoles.Any(role =>
                string.IsNullOrWhiteSpace(role) ||
                state.PlannedRoles is null ||
                !state.PlannedRoles.Contains(role, StringComparer.Ordinal)))
        {
            errors.Add(
                "pendingOwnerRoles must contain only non-empty roles from plannedRoles");
        }
        var activeQaFields = new object?[]
        {
            state.ActiveQaRound,
            state.ActiveQaStepId,
            state.ActiveQaContextPath,
            state.ActiveQaContextHash
        };
        if (activeQaFields.Any(item => item is not null) &&
            activeQaFields.Any(item => item is null))
        {
            errors.Add("active QA round, step, context path, and context hash must be set together");
        }
        if (state.ActiveQaRound is { } activeRound &&
            (activeRound != (state.Rounds?.Count ?? 0) + 1 ||
             activeRound > state.MaxRounds ||
             state.ActiveQaStepId == Guid.Empty ||
             !IsSha256(state.ActiveQaContextHash)))
        {
            errors.Add("active QA metadata does not identify the next valid round");
        }
        if (state.ActiveQaRound is not null &&
            (state.Status != OutcomeVerificationStatus.AwaitingQa ||
             state.AcceptancePlan is null ||
             state.CurrentCandidate is null))
        {
            errors.Add(
                "active QA metadata requires AwaitingQa status with a current plan and candidate");
        }
        if (state.Status == OutcomeVerificationStatus.Passed)
        {
            if (state.CurrentCandidate is null ||
                string.IsNullOrWhiteSpace(state.VerifiedCandidateFingerprint) ||
                !string.Equals(
                    state.CurrentCandidate.Fingerprint,
                    state.VerifiedCandidateFingerprint,
                    StringComparison.Ordinal) ||
                state.Stale)
            {
                errors.Add(
                    "Passed requires a current, non-stale candidate matching verifiedCandidateFingerprint");
            }
            if (state.VerifiedAt is null)
            {
                errors.Add("Passed requires a verification timestamp");
            }
            if (state.ActiveQaRound is not null ||
                state.PendingOwnerRoles is { Count: > 0 })
            {
                errors.Add(
                    "Passed cannot retain an active QA cursor or pending correction owners");
            }
            ValidateAuthoritativePass(state, errors, "Passed");
        }
        else if (state.VerifiedCandidateFingerprint is not null)
        {
            errors.Add("verifiedCandidateFingerprint is valid only while status is Passed");
        }
        if (state.Status != OutcomeVerificationStatus.Passed &&
            state.VerifiedAt is not null)
        {
            errors.Add("verifiedAt is valid only while status is Passed");
        }
        if (state.Status == OutcomeVerificationStatus.Superseded)
        {
            errors.Add("the current aggregate cannot have Superseded status");
        }

        if (errors.Count > 0)
        {
            throw new OutcomeVerificationValidationException(errors);
        }
    }

    public static string SerializeCanonical<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var document = JsonDocument.Parse(
            JsonSerializer.SerializeToUtf8Bytes(value, StrictJsonOptions),
            new JsonDocumentOptions { MaxDepth = MaximumJsonDepth });
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(document.RootElement, writer);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string HashCandidateManifest(CandidateManifest manifest) =>
        ComputeSha256(SerializeCanonical(manifest));

    public static string ComputeSha256(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    public static bool IsSha256(string? value) =>
        value is not null && DigestPattern.IsMatch(value);

    private static IReadOnlyList<string> ValidateAcceptancePlan(
        OutcomeAcceptancePlan? document,
        IReadOnlyList<string>? allowedRoles)
    {
        var errors = new List<string>();
        if (document is null)
        {
            return ["acceptance plan document is null"];
        }
        if (!string.Equals(document.Version, AcceptanceVersion, StringComparison.Ordinal))
        {
            errors.Add($"version must be exactly '{AcceptanceVersion}'");
        }
        if (document.Criteria is null || document.Criteria.Count is < 1 or > 12)
        {
            errors.Add("criteria must contain 1-12 entries");
            return errors;
        }

        var roleSet = allowedRoles?.ToHashSet(StringComparer.Ordinal);
        foreach (var (criterion, index) in document.Criteria.Select(
                     (criterion, index) => (criterion, index)))
        {
            if (criterion is null)
            {
                errors.Add($"criterion {index + 1} is null");
                continue;
            }
            var expectedId = $"AC-{index + 1:000}";
            if (!CriterionIdPattern.IsMatch(criterion.Id ?? string.Empty) ||
                !string.Equals(criterion.Id, expectedId, StringComparison.Ordinal))
            {
                errors.Add($"criterion {index + 1} id must be exactly '{expectedId}'");
            }
            ValidateText(
                criterion.Requirement,
                320,
                $"criterion {expectedId} requirement",
                errors);
            ValidateText(
                criterion.Verification,
                320,
                $"criterion {expectedId} verification",
                errors);
            if (!string.IsNullOrWhiteSpace(criterion.Verification) &&
                !DescribesObservableCheck(criterion.Verification))
            {
                errors.Add(
                    $"criterion {expectedId} verification must describe an independently observable substantive content or behavior check");
            }
            if (criterion.OwnerRoles is null ||
                criterion.OwnerRoles.Count is < 1 or > 3)
            {
                errors.Add($"criterion {expectedId} ownerRoles must contain 1-3 roles");
            }
            else
            {
                if (criterion.OwnerRoles.Count !=
                    criterion.OwnerRoles.Distinct(StringComparer.Ordinal).Count())
                {
                    errors.Add($"criterion {expectedId} ownerRoles must be unique");
                }
                foreach (var role in criterion.OwnerRoles)
                {
                    if (string.IsNullOrWhiteSpace(role) ||
                        (roleSet is not null && !roleSet.Contains(role)))
                    {
                        errors.Add(
                            $"criterion {expectedId} owner role '{role}' is not in the fixed downstream plan");
                    }
                    if (string.Equals(role, "quality-engineer", StringComparison.Ordinal))
                    {
                        errors.Add(
                            $"criterion {expectedId} cannot be owned by quality-engineer");
                    }
                    if (string.Equals(role, "release-engineer", StringComparison.Ordinal) &&
                        !IsReleaseCriterion(criterion))
                    {
                        errors.Add(
                            $"criterion {expectedId} may assign release-engineer only to packaging, preview, publication, or release outcomes");
                    }
                }
            }
            if (criterion.EvidenceKinds is null ||
                criterion.EvidenceKinds.Count is < 1 or > 4)
            {
                errors.Add(
                    $"criterion {expectedId} evidenceKinds must contain 1-4 values");
            }
            else if (criterion.EvidenceKinds.Count != criterion.EvidenceKinds.Distinct().Count())
            {
                errors.Add($"criterion {expectedId} evidenceKinds must be unique");
            }
        }
        return errors;
    }

    private static void ValidateEvidenceInput(
        OutcomeDeliveryEvidenceInput? item,
        int index,
        IReadOnlyDictionary<string, OutcomeAcceptanceCriterion> criteria,
        string producerRole,
        ICollection<string> errors)
    {
        if (item is null)
        {
            errors.Add($"evidence item {index + 1} is null");
            return;
        }
        if (!criteria.TryGetValue(item.CriterionId ?? string.Empty, out var criterion))
        {
            errors.Add(
                $"evidence item {index + 1} references unknown criterion '{item.CriterionId}'");
        }
        else
        {
            if (!criterion.OwnerRoles.Contains(producerRole, StringComparer.Ordinal))
            {
                errors.Add(
                    $"criterion '{item.CriterionId}' is not assigned to role '{producerRole}'");
            }
            if (!criterion.EvidenceKinds.Contains(item.Kind))
            {
                errors.Add(
                    $"evidence item {index + 1} kind '{item.Kind}' is not allowed for criterion '{item.CriterionId}'");
            }
        }
        ValidateText(item.Locator, 512, $"evidence item {index + 1} locator", errors);
        ValidateText(
            item.ObservedResult,
            500,
            $"evidence item {index + 1} observedResult",
            errors);
        if (item.ContentDigest is not null && !DigestPattern.IsMatch(item.ContentDigest))
        {
            errors.Add(
                $"evidence item {index + 1} contentDigest must be null or a lowercase sha256 digest");
        }
    }

    private static IReadOnlyList<string> ValidateQaResult(
        OutcomeQaResult? document,
        OutcomeAcceptancePlanSnapshot activePlan,
        string candidateFingerprint,
        IReadOnlyCollection<string>? knownEvidenceIds,
        IReadOnlyCollection<string>? allowedOwnerRoles)
    {
        var errors = new List<string>();
        var canDeriveVerdict = true;
        if (document is null)
        {
            return ["QA result document is null"];
        }
        if (!string.Equals(document.Version, QaVersion, StringComparison.Ordinal))
        {
            errors.Add($"version must be exactly '{QaVersion}'");
        }
        if (!string.Equals(
                document.AcceptancePlanHash,
                activePlan.Hash,
                StringComparison.Ordinal))
        {
            errors.Add("acceptancePlanHash does not match the active plan");
        }
        if (!string.Equals(
                document.CandidateFingerprint,
                candidateFingerprint,
                StringComparison.Ordinal))
        {
            errors.Add("candidateFingerprint does not match the active candidate");
        }
        if (document.Criteria is null)
        {
            errors.Add("criteria is required");
            canDeriveVerdict = false;
        }
        else
        {
            if (document.Criteria.Count != activePlan.Criteria.Count)
            {
                errors.Add("criteria must contain exactly one result for every criterion");
            }
            var actualIds = document.Criteria
                .Where(result => result is not null)
                .Select(result => result.CriterionId)
                .ToArray();
            var expectedIds = activePlan.Criteria.Select(item => item.Id).ToArray();
            if (actualIds.Length != actualIds.Distinct(StringComparer.Ordinal).Count())
            {
                errors.Add("criteria must not contain duplicate criterion IDs");
            }
            if (!actualIds.SequenceEqual(expectedIds, StringComparer.Ordinal))
            {
                errors.Add("criteria must appear exactly once in acceptance-plan order");
            }
            var criterionLookup = activePlan.Criteria.ToDictionary(
                item => item.Id,
                StringComparer.Ordinal);
            foreach (var (result, index) in document.Criteria.Select(
                         (result, index) => (result, index)))
            {
                if (result is null)
                {
                    canDeriveVerdict = false;
                    errors.Add($"criterion result {index + 1} is null");
                    continue;
                }
                criterionLookup.TryGetValue(result.CriterionId ?? string.Empty, out var criterion);
                ValidateQaCriterion(
                    result,
                    index,
                    criterion,
                    knownEvidenceIds,
                    errors);
            }
        }

        var planGaps = document.PlanGaps ?? [];
        if (planGaps.Count > 3)
        {
            errors.Add("planGaps must contain at most three entries");
        }
        var ownerRoles = allowedOwnerRoles?.ToHashSet(StringComparer.Ordinal) ??
                         activePlan.Criteria
                             .SelectMany(item => item.OwnerRoles)
                             .ToHashSet(StringComparer.Ordinal);
        foreach (var (gap, index) in planGaps.Select(
                     (gap, index) => (gap, index)))
        {
            if (gap is null)
            {
                canDeriveVerdict = false;
                errors.Add($"plan gap {index + 1} is null");
                continue;
            }
            ValidateText(
                gap.Requirement,
                320,
                $"plan gap {index + 1} requirement",
                errors);
            ValidateText(
                gap.Verification,
                320,
                $"plan gap {index + 1} verification",
                errors);
            ValidateText(
                gap.Rationale,
                500,
                $"plan gap {index + 1} rationale",
                errors);
            if (!string.IsNullOrWhiteSpace(gap.Verification) &&
                !DescribesObservableCheck(gap.Verification))
            {
                errors.Add(
                    $"plan gap {index + 1} verification must describe an independently observable substantive content or behavior check");
            }
            if (gap.OwnerRoles is null || gap.OwnerRoles.Count is < 1 or > 3)
            {
                errors.Add($"plan gap {index + 1} ownerRoles must contain 1-3 roles");
            }
            else
            {
                foreach (var role in gap.OwnerRoles)
                {
                    if (!ownerRoles.Contains(role) ||
                        string.Equals(role, "quality-engineer", StringComparison.Ordinal))
                    {
                        errors.Add(
                            $"plan gap {index + 1} owner role '{role}' is invalid");
                    }
                }
                if (gap.OwnerRoles.Count !=
                    gap.OwnerRoles.Distinct(StringComparer.Ordinal).Count())
                {
                    errors.Add(
                        $"plan gap {index + 1} ownerRoles must be unique");
                }
            }
        }

        if (canDeriveVerdict)
        {
            var derived = DeriveGlobalVerdict(document.Criteria!, planGaps);
            if (document.Verdict != derived)
            {
                errors.Add(
                    $"verdict '{document.Verdict}' contradicts runtime-derived verdict '{derived}'");
            }
        }
        return errors;
    }

    private static void ValidateQaCriterion(
        OutcomeQaCriterionResult result,
        int index,
        OutcomeAcceptanceCriterion? criterion,
        IReadOnlyCollection<string>? knownEvidenceIds,
        ICollection<string> errors)
    {
        var label = criterion?.Id ?? result.CriterionId ?? $"#{index + 1}";
        if (result.EvidenceIds is null)
        {
            errors.Add($"criterion {label} evidenceIds is required");
        }
        else
        {
            if (result.EvidenceIds.Count !=
                result.EvidenceIds.Distinct(StringComparer.Ordinal).Count())
            {
                errors.Add($"criterion {label} evidenceIds must be unique");
            }
            if (knownEvidenceIds is not null)
            {
                var known = knownEvidenceIds.ToHashSet(StringComparer.Ordinal);
                foreach (var evidenceId in result.EvidenceIds.Where(id => !known.Contains(id)))
                {
                    errors.Add($"criterion {label} references unknown evidence '{evidenceId}'");
                }
            }
        }
        if (result.ChecksPerformed is null ||
            result.ChecksPerformed.Count is < 1 or > 6)
        {
            errors.Add($"criterion {label} checksPerformed must contain 1-6 checks");
        }
        else
        {
            foreach (var (check, checkIndex) in result.ChecksPerformed.Select(
                         (check, checkIndex) => (check, checkIndex)))
            {
                if (check is null)
                {
                    errors.Add($"criterion {label} check {checkIndex + 1} is null");
                    continue;
                }
                ValidateText(
                    check.Locator,
                    512,
                    $"criterion {label} check {checkIndex + 1} locator",
                    errors);
                ValidateText(
                    check.ObservedResult,
                    500,
                    $"criterion {label} check {checkIndex + 1} observedResult",
                    errors);
                if (criterion is not null &&
                    !criterion.EvidenceKinds.Contains(check.Kind))
                {
                    errors.Add(
                        $"criterion {label} check {checkIndex + 1} kind '{check.Kind}' " +
                        "is not an allowed verification kind");
                }
            }
        }
        ValidateText(result.Rationale, 500, $"criterion {label} rationale", errors);
        if (result.ResponsibleRoles is null)
        {
            errors.Add($"criterion {label} responsibleRoles is required");
            return;
        }
        if (result.ResponsibleRoles.Count !=
            result.ResponsibleRoles.Distinct(StringComparer.Ordinal).Count())
        {
            errors.Add($"criterion {label} responsibleRoles must be unique");
        }

        var owners = criterion?.OwnerRoles.ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var role in result.ResponsibleRoles)
        {
            if (!owners.Contains(role))
            {
                errors.Add(
                    $"criterion {label} responsible role '{role}' is not an owner");
            }
        }

        switch (result.Status)
        {
            case OutcomeCriterionStatus.PASS:
                if (result.ChecksPerformed is { Count: > 0 } &&
                    !result.ChecksPerformed.Any(
                        check => check is not null &&
                                 DemonstratesOutcomeBeyondExistence(check)))
                {
                    errors.Add(
                        $"criterion {label} PASS requires at least one independently observable substantive content or behavior check");
                }
                if (result.ResponsibleRoles.Count > 0)
                {
                    errors.Add($"criterion {label} PASS cannot name responsible roles");
                }
                if (!string.IsNullOrWhiteSpace(result.Remediation))
                {
                    errors.Add($"criterion {label} PASS cannot include remediation");
                }
                break;
            case OutcomeCriterionStatus.FAIL:
                if (result.ResponsibleRoles.Count is < 1 or > 3)
                {
                    errors.Add($"criterion {label} FAIL requires 1-3 responsible roles");
                }
                ValidateText(
                    result.Remediation,
                    500,
                    $"criterion {label} remediation",
                    errors);
                break;
            case OutcomeCriterionStatus.BLOCKED:
                if (result.ResponsibleRoles.Count > 3)
                {
                    errors.Add(
                        $"criterion {label} BLOCKED accepts at most three responsible roles");
                }
                if (result.ResponsibleRoles.Count > 0)
                {
                    ValidateText(
                        result.Remediation,
                        500,
                        $"criterion {label} remediation",
                        errors);
                }
                if (result.ResponsibleRoles.Count == 0 &&
                    !DescribesExternalBlocker(result.Rationale))
                {
                    errors.Add(
                        $"criterion {label} BLOCKED without an owner must clearly identify an external blocker");
                }
                break;
            default:
                errors.Add($"criterion {label} status is unsupported");
                break;
        }
    }

    private static void ValidateArchivedIteration(
        OutcomeVerificationIterationArchive archive,
        IReadOnlyList<OutcomeTrustedRepository>? trustedRepositories,
        ICollection<string> errors)
    {
        var label = $"prior iteration {archive.Iteration}";
        var archiveErrors = new List<string>();
        if (archive.ManualRoundsGranted is < 0 or > 10)
        {
            archiveErrors.Add("manualRoundsGranted must be between zero and ten");
        }
        if (archive.MaxRounds is < 1 or > 20 ||
            archive.MaxRounds - archive.ManualRoundsGranted is < 1 or > 10)
        {
            archiveErrors.Add(
                "maxRounds must contain a 1-10 policy snapshot plus at most ten manually granted rounds");
        }
        if (archive.Status != OutcomeVerificationStatus.Superseded)
        {
            archiveErrors.Add("status must be Superseded");
        }
        if (archive.PlannedRoles is null)
        {
            archiveErrors.Add("plannedRoles is required");
        }
        else if (archive.PlannedRoles.Count !=
                 archive.PlannedRoles.Distinct(StringComparer.Ordinal).Count() ||
                 archive.PlannedRoles.Any(string.IsNullOrWhiteSpace))
        {
            archiveErrors.Add(
                "plannedRoles must contain unique, non-empty role IDs");
        }
        if (archive.InitialDeliverySemanticRootIds is null)
        {
            archiveErrors.Add("initialDeliverySemanticRootIds is required");
        }
        if (archive.ProcessedSemanticRootIds is null)
        {
            archiveErrors.Add("processedSemanticRootIds is required");
        }
        if (archive.Evidence is null)
        {
            archiveErrors.Add("evidence is required");
        }
        if (archive.EvidenceProcessing is null)
        {
            archiveErrors.Add("evidenceProcessing is required");
        }
        if (archive.Rounds is null)
        {
            archiveErrors.Add("rounds is required");
        }
        ValidateSemanticRoots(
            archive.InitialPlanSemanticRootId,
            archive.InitialDeliverySemanticRootIds,
            archive.ProcessedSemanticRootIds,
            "archive",
            archiveErrors);

        var archivedState = new OutcomeVerificationState
        {
            Iteration = archive.Iteration,
            Status = archive.Status,
            MaxRounds = archive.MaxRounds,
            ManualRoundsGranted = archive.ManualRoundsGranted,
            TrustedRepositories = trustedRepositories?.ToList() ?? [],
            PlannedRoles = archive.PlannedRoles?.ToList() ?? [],
            InitialPlanSemanticRootId = archive.InitialPlanSemanticRootId,
            InitialDeliverySemanticRootIds =
                archive.InitialDeliverySemanticRootIds?.ToList() ?? [],
            ProcessedSemanticRootIds =
                archive.ProcessedSemanticRootIds?.ToList() ?? [],
            AcceptancePlan = archive.AcceptancePlan,
            Evidence = archive.Evidence?.ToList() ?? [],
            EvidenceProcessing = archive.EvidenceProcessing?.ToList() ?? [],
            Rounds = archive.Rounds?.ToList() ?? [],
            CurrentCandidate = archive.CurrentCandidate,
            Publication = archive.Publication,
            VerifiedCandidateFingerprint = archive.VerifiedCandidateFingerprint,
            PendingOwnerRoles = []
        };
        ValidateProofLedger(
            archivedState,
            archiveErrors,
            labelPrefix: null,
            allowEmptySuperseded: true,
            allowVerifiedSupersededPublication: true);

        if (archive.VerifiedCandidateFingerprint is not null)
        {
            if (archive.CurrentCandidate is null ||
                !string.Equals(
                    archive.CurrentCandidate.Fingerprint,
                    archive.VerifiedCandidateFingerprint,
                    StringComparison.Ordinal))
            {
                archiveErrors.Add(
                    "verifiedCandidateFingerprint must match the archived candidate");
            }
            ValidateAuthoritativePass(
                archivedState,
                archiveErrors,
                "archived verified outcome");
        }

        foreach (var error in archiveErrors)
        {
            errors.Add($"{label}: {error}");
        }
    }

    private static void ValidateProofLedger(
        OutcomeVerificationState state,
        ICollection<string> errors,
        string? labelPrefix,
        bool allowEmptySuperseded,
        bool allowVerifiedSupersededPublication)
    {
        var ledgerErrors = new List<string>();
        if (state.AcceptancePlan is null)
        {
            if (!allowEmptySuperseded &&
                state.Status is not (
                    OutcomeVerificationStatus.NotStarted or
                    OutcomeVerificationStatus.Planning))
            {
                ledgerErrors.Add("acceptancePlan is required after planning");
            }
            if ((state.Evidence?.Count ?? 0) > 0 ||
                (state.EvidenceProcessing?.Count ?? 0) > 0 ||
                (state.Rounds?.Count ?? 0) > 0 ||
                state.InitialPlanSemanticRootId is not null ||
                (state.InitialDeliverySemanticRootIds?.Count ?? 0) > 0 ||
                (state.ProcessedSemanticRootIds?.Count ?? 0) > 0 ||
                state.CurrentCandidate is not null ||
                state.Publication is not null ||
                state.VerifiedCandidateFingerprint is not null)
            {
                ledgerErrors.Add(
                    "proof data cannot exist without an acceptance plan");
            }
        }
        else
        {
            var plan = new OutcomeAcceptancePlan(
                AcceptanceVersion,
                state.AcceptancePlan.Criteria);
            var planErrors = ValidateAcceptancePlan(
                plan,
                state.PlannedRoles is { Count: > 0 }
                    ? state.PlannedRoles
                    : null);
            ledgerErrors.AddRange(planErrors.Select(error =>
                $"acceptancePlan: {error}"));
            if (state.PlannedRoles is not { Count: > 0 })
            {
                ledgerErrors.Add("plannedRoles is required after planning");
            }
            if (!string.Equals(
                    state.AcceptancePlan.Hash,
                    SafeHashAcceptancePlan(plan, ledgerErrors),
                    StringComparison.Ordinal))
            {
                ledgerErrors.Add(
                    "acceptancePlan hash does not match its canonical criteria");
            }
            if (state.AcceptancePlan.SourceStepId == Guid.Empty)
            {
                ledgerErrors.Add("acceptancePlan sourceStepId is required");
            }

            // Avoid dereferencing a malformed plan after reporting its contract
            // errors. Strict JSON loading reports the same corruption without
            // allowing a NullReferenceException to escape.
            if (planErrors.Count == 0)
            {
                if (state.Evidence is not null)
                {
                    ValidateAggregateEvidence(state, ledgerErrors);
                }
                if (state.EvidenceProcessing is not null)
                {
                    ValidateEvidenceProcessing(state, ledgerErrors);
                }
                if (state.Rounds is not null)
                {
                    ValidateAggregateRounds(state, ledgerErrors);
                }
                ValidateCandidate(state, ledgerErrors);
                ValidatePublication(
                    state,
                    ledgerErrors,
                    allowVerifiedSupersededPublication);
            }
        }

        foreach (var error in ledgerErrors)
        {
            errors.Add(labelPrefix is null ? error : $"{labelPrefix}: {error}");
        }
    }

    private static void ValidateAuthoritativePass(
        OutcomeVerificationState state,
        ICollection<string> errors,
        string label)
    {
        if (state.AcceptancePlan is null ||
            state.AcceptancePlan.Criteria is null ||
            state.CurrentCandidate is null ||
            state.Rounds is not { Count: > 0 })
        {
            errors.Add(
                $"{label} requires a completed QA round for the current acceptance plan and candidate");
            return;
        }

        var latest = state.Rounds[^1];
        var result = latest.Result;
        if (latest.Stale ||
            latest.Verdict != OutcomeQaVerdict.PASS ||
            result is null ||
            !string.IsNullOrWhiteSpace(latest.ContractError) ||
            result.Verdict != OutcomeQaVerdict.PASS ||
            result.PlanGaps is null ||
            result.PlanGaps.Count != 0 ||
            result.Criteria is null ||
            result.Criteria.Count != state.AcceptancePlan.Criteria.Count ||
            result.Criteria.Any(item =>
                item is null || item.Status != OutcomeCriterionStatus.PASS))
        {
            errors.Add(
                $"{label} requires the latest QA round to be a non-stale terminal all-PASS result with no plan gaps");
        }
        if (!string.Equals(
                latest.AcceptancePlanHash,
                state.AcceptancePlan.Hash,
                StringComparison.Ordinal) ||
            !string.Equals(
                result?.AcceptancePlanHash,
                state.AcceptancePlan.Hash,
                StringComparison.Ordinal))
        {
            errors.Add(
                $"{label} latest QA round must match the current acceptance plan hash");
        }
        if (!string.Equals(
                latest.CandidateFingerprint,
                state.CurrentCandidate.Fingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                result?.CandidateFingerprint,
                state.CurrentCandidate.Fingerprint,
                StringComparison.Ordinal))
        {
            errors.Add(
                $"{label} latest QA round must match the current candidate fingerprint");
        }
        if (latest.QaStepId == Guid.Empty ||
            latest.QaStepId == state.CurrentCandidate.PreparedByStepId ||
            !IsSha256(latest.ContextHash))
        {
            errors.Add(
                $"{label} latest QA round must identify a distinct QA step and valid context hash");
        }
    }

    private static void ValidateAggregateEvidence(
        OutcomeVerificationState state,
        ICollection<string> errors)
    {
        var criteria = state.AcceptancePlan!.Criteria.ToDictionary(
            item => item.Id,
            StringComparer.Ordinal);
        var evidenceItems = state.Evidence
            .Where(item => item is not null)
            .ToArray();
        if (evidenceItems.Length != state.Evidence.Count)
        {
            errors.Add("evidence items cannot be null");
        }
        if (evidenceItems.Select(item => item.EvidenceId)
            .Distinct(StringComparer.Ordinal).Count() != evidenceItems.Length)
        {
            errors.Add("evidence IDs must be unique");
        }
        foreach (var group in evidenceItems.GroupBy(
                     item => item.CriterionId,
                     StringComparer.Ordinal))
        {
            if (group.Count() > 8)
            {
                errors.Add($"criterion '{group.Key}' retains more than eight evidence items");
            }
        }

        foreach (var item in evidenceItems)
        {
            if (!criteria.TryGetValue(item.CriterionId, out var criterion))
            {
                errors.Add($"evidence '{item.EvidenceId}' references an unknown criterion");
                continue;
            }
            if (!criterion.OwnerRoles.Contains(item.ProducerRole, StringComparer.Ordinal))
            {
                errors.Add(
                    $"evidence '{item.EvidenceId}' was produced by an unassigned role");
            }
            if (!criterion.EvidenceKinds.Contains(item.Kind))
            {
                errors.Add($"evidence '{item.EvidenceId}' uses a disallowed kind");
            }
            if (!item.EvidenceId.StartsWith("E-", StringComparison.Ordinal) ||
                item.ProducerStepId == Guid.Empty)
            {
                errors.Add($"evidence '{item.EvidenceId}' has invalid runtime metadata");
            }
            ValidateText(item.Locator, 512, $"evidence '{item.EvidenceId}' locator", errors);
            ValidateText(
                item.ObservedResult,
                500,
                $"evidence '{item.EvidenceId}' observedResult",
                errors);
            if (item.ContentDigest is not null && !DigestPattern.IsMatch(item.ContentDigest))
            {
                errors.Add($"evidence '{item.EvidenceId}' has an invalid contentDigest");
            }
        }
    }

    private static void ValidateEvidenceProcessing(
        OutcomeVerificationState state,
        ICollection<string> errors)
    {
        var processingItems = state.EvidenceProcessing
            .Where(item => item is not null)
            .ToArray();
        if (processingItems.Length != state.EvidenceProcessing.Count)
        {
            errors.Add("evidence processing records cannot be null");
        }
        if (processingItems.Select(item => item.ProducerStepId)
            .Distinct().Count() != processingItems.Length)
        {
            errors.Add("evidence processing records must be unique by producer step ID");
        }

        var criteria = state.AcceptancePlan!.Criteria
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var processing in processingItems)
        {
            if (processing.ProducerStepId == Guid.Empty ||
                string.IsNullOrWhiteSpace(processing.ProducerRole) ||
                !IsSha256(processing.AcceptancePlanHash))
            {
                errors.Add("evidence processing metadata is invalid");
            }
            if (processing.CriterionIds is null ||
                processing.CriterionIds.Count == 0 ||
                processing.CriterionIds.Count !=
                processing.CriterionIds.Distinct(StringComparer.Ordinal).Count() ||
                processing.CriterionIds.Any(id => !criteria.Contains(id)))
            {
                errors.Add(
                    $"evidence processing record '{processing.ProducerStepId}' has invalid criterion IDs");
            }
        }
        foreach (var evidence in state.Evidence.Where(item => item is not null))
        {
            var processing = processingItems.SingleOrDefault(item =>
                item.ProducerStepId == evidence.ProducerStepId);
            if (processing?.CriterionIds is null ||
                !processing.CriterionIds.Contains(
                    evidence.CriterionId,
                    StringComparer.Ordinal))
            {
                errors.Add(
                    $"evidence '{evidence.EvidenceId}' has no matching plan-bound processing record");
            }
        }
    }

    private static void ValidateSemanticRoots(
        Guid? initialPlanRoot,
        IReadOnlyList<Guid>? initialDeliveryRoots,
        IReadOnlyList<Guid>? processedRoots,
        string label,
        ICollection<string> errors)
    {
        if (initialPlanRoot == Guid.Empty)
        {
            errors.Add($"{label} initialPlanSemanticRootId cannot be empty");
        }
        if (initialDeliveryRoots is null)
        {
            errors.Add($"{label} initialDeliverySemanticRootIds is required");
        }
        else if (initialDeliveryRoots.Any(id => id == Guid.Empty) ||
                 initialDeliveryRoots.Distinct().Count() !=
                 initialDeliveryRoots.Count)
        {
            errors.Add(
                $"{label} initialDeliverySemanticRootIds must contain unique non-empty IDs");
        }
        if (processedRoots is null)
        {
            errors.Add($"{label} processedSemanticRootIds is required");
        }
        else if (processedRoots.Any(id => id == Guid.Empty) ||
                 processedRoots.Distinct().Count() != processedRoots.Count)
        {
            errors.Add(
                $"{label} processedSemanticRootIds must contain unique non-empty IDs");
        }
        if ((initialDeliveryRoots?.Count ?? 0) > 0 &&
            initialPlanRoot is null)
        {
            errors.Add(
                $"{label} initial delivery roots require an initial plan root");
        }
    }

    private static void ValidateAggregateRounds(
        OutcomeVerificationState state,
        ICollection<string> errors)
    {
        if (state.Rounds.Count > state.MaxRounds)
        {
            errors.Add("QA round count exceeds maxRounds");
        }
        var qaStepIds = new HashSet<Guid>();
        var expected = 1;
        foreach (var (round, index) in state.Rounds.Select(
                     (round, index) => (round, index)))
        {
            if (round is null)
            {
                errors.Add($"QA round entry {index + 1} cannot be null");
                expected++;
                continue;
            }
            if (round.Round != expected++)
            {
                errors.Add("QA rounds must be sequential starting at one");
            }
            if (round.Round > state.MaxRounds)
            {
                errors.Add($"QA round {round.Round} exceeds maxRounds");
            }
            if (round.QaStepId == Guid.Empty)
            {
                errors.Add($"QA round {round.Round} has no step ID");
            }
            else if (!qaStepIds.Add(round.QaStepId))
            {
                errors.Add("QA round step IDs must be unique");
            }
            if (!IsSha256(round.CandidateFingerprint))
            {
                errors.Add(
                    $"QA round {round.Round} has an invalid candidate fingerprint");
            }
            if (!IsSha256(round.ContextHash))
            {
                errors.Add($"QA round {round.Round} has an invalid context hash");
            }
            if (round.CompletedAt == default)
            {
                errors.Add($"QA round {round.Round} has no completion timestamp");
            }
            if (round.Verdict is null || !Enum.IsDefined(round.Verdict.Value))
            {
                errors.Add($"QA round {round.Round} has no valid terminal verdict");
            }
            if (!string.Equals(
                    round.AcceptancePlanHash,
                    state.AcceptancePlan!.Hash,
                    StringComparison.Ordinal) &&
                !round.Stale)
            {
                errors.Add(
                    $"QA round {round.Round} uses a non-current plan and must be stale");
            }
            if ((round.Result is null) == string.IsNullOrWhiteSpace(round.ContractError))
            {
                errors.Add(
                    $"QA round {round.Round} must contain either a result or a contract error");
            }
            if ((round.ContractError?.Length ?? 0) > 4_000)
            {
                errors.Add($"QA round {round.Round} contract error is too large");
            }
            if (round.Result is not null && round.Verdict != round.Result.Verdict)
            {
                errors.Add($"QA round {round.Round} verdict does not match its result");
            }
            if (round.CorrectionStepIds is null)
            {
                errors.Add(
                    $"QA round {round.Round} correctionStepIds is required");
            }
            else if (round.CorrectionStepIds.Any(id => id == Guid.Empty) ||
                     round.CorrectionStepIds.Distinct().Count() !=
                     round.CorrectionStepIds.Count)
            {
                errors.Add(
                    $"QA round {round.Round} correction step IDs must be unique and non-empty");
            }
            if (round.CorrectionRegistrations is null)
            {
                errors.Add(
                    $"QA round {round.Round} correctionRegistrations is required");
            }
            else
            {
                var registrations = round.CorrectionRegistrations
                    .Where(item => item is not null)
                    .ToArray();
                var duplicateRoles = registrations
                    .GroupBy(item => item.Role, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToArray();
                var duplicateRoots = registrations
                    .GroupBy(item => item.SemanticRootId)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToArray();
                if (duplicateRoles.Length > 0 || duplicateRoots.Length > 0)
                {
                    errors.Add(
                        $"QA round {round.Round} correction registrations must have unique roles and roots");
                }
                foreach (var registration in round.CorrectionRegistrations)
                {
                    if (registration is null)
                    {
                        errors.Add(
                            $"QA round {round.Round} has a null correction registration");
                        continue;
                    }
                    if (registration.QaRound != round.Round ||
                        registration.StepId == Guid.Empty ||
                        registration.SemanticRootId == Guid.Empty ||
                        string.IsNullOrWhiteSpace(registration.Role) ||
                        !state.PlannedRoles.Contains(
                            registration.Role,
                            StringComparer.Ordinal) ||
                        !IsSha256(registration.OutcomePlanHash))
                    {
                        errors.Add(
                            $"QA round {round.Round} has an invalid correction registration");
                    }
                }
            }
            if (round.ProcessedCorrectionRootIds is null)
            {
                errors.Add(
                    $"QA round {round.Round} processedCorrectionRootIds is required");
            }
            else if (round.ProcessedCorrectionRootIds.Any(id => id == Guid.Empty) ||
                     round.ProcessedCorrectionRootIds.Distinct().Count() !=
                     round.ProcessedCorrectionRootIds.Count ||
                     round.CorrectionRegistrations is not null &&
                     round.ProcessedCorrectionRootIds.Any(id =>
                         !round.CorrectionRegistrations.Any(registration =>
                             registration is not null &&
                             registration.SemanticRootId == id)))
            {
                errors.Add(
                    $"QA round {round.Round} processed correction roots must be unique registered roots");
            }
            if (round.Result is not null &&
                string.Equals(
                    round.AcceptancePlanHash,
                    state.AcceptancePlan!.Hash,
                    StringComparison.Ordinal))
            {
                foreach (var error in ValidateQaResult(
                             round.Result,
                             state.AcceptancePlan,
                             round.CandidateFingerprint,
                             state.Evidence
                                 .Where(item => item is not null)
                                 .Select(item => item.EvidenceId)
                                 .ToArray(),
                             allowedOwnerRoles: state.PlannedRoles))
                {
                    errors.Add($"QA round {round.Round}: {error}");
                }
            }
        }
    }

    private static void ValidateCandidate(
        OutcomeVerificationState state,
        ICollection<string> errors)
    {
        if (state.CurrentCandidate is null)
        {
            return;
        }
        var candidate = state.CurrentCandidate;
        if (candidate.Manifest is null)
        {
            errors.Add("candidate manifest is required");
            return;
        }
        if (!string.Equals(
                candidate.Manifest.Version,
                CandidateManifestVersion,
                StringComparison.Ordinal))
        {
            errors.Add(
                $"candidate manifest version must be exactly '{CandidateManifestVersion}'");
        }
        if (candidate.Manifest.FlowIteration != state.Iteration)
        {
            errors.Add("candidate manifest iteration does not match the aggregate");
        }
        if (!string.Equals(
                candidate.Manifest.AcceptancePlanHash,
                state.AcceptancePlan!.Hash,
                StringComparison.Ordinal))
        {
            errors.Add("candidate manifest acceptance plan hash is stale");
        }
        if (candidate.Manifest.Repositories is null ||
            candidate.Manifest.Repositories.Count == 0)
        {
            errors.Add("candidate manifest must contain at least one repository");
        }
        else
        {
            var repositories = candidate.Manifest.Repositories
                .Where(item => item is not null)
                .ToArray();
            if (repositories.Length != candidate.Manifest.Repositories.Count)
            {
                errors.Add("candidate repository entries cannot be null");
            }
            ValidateManifestOrdering(
                repositories.Select(item => item.RelativePath),
                "candidate repository",
                errors);
            foreach (var repository in repositories)
            {
                if (!IsSafeRelativePath(repository.RelativePath))
                {
                    errors.Add(
                        $"candidate repository path '{repository.RelativePath}' is invalid");
                }
                if (!GitObjectIdPattern.IsMatch(repository.Head) ||
                    !GitObjectIdPattern.IsMatch(repository.Tree))
                {
                    errors.Add(
                        $"candidate repository '{repository.RelativePath}' has an invalid commit or tree identity");
                }
                if (repository.RemoteRepository.Length > 0 &&
                    !RepositoryIdentityPattern.IsMatch(
                        repository.RemoteRepository))
                {
                    errors.Add(
                        $"candidate repository '{repository.RelativePath}' has an invalid remote identity");
                }
            }
            if (state.TrustedRepositories is null ||
                state.TrustedRepositories.Count !=
                candidate.Manifest.Repositories.Count)
            {
                errors.Add(
                    "candidate repositories do not match the trusted workspace mapping");
            }
            else
            {
                for (var index = 0;
                     index < repositories.Length;
                     index++)
                {
                    var actual = repositories[index];
                    var trusted = state.TrustedRepositories[index];
                    if (trusted is null)
                    {
                        errors.Add(
                            $"candidate repository '{actual.RelativePath}' has no valid trusted workspace mapping");
                        continue;
                    }
                    if (!string.Equals(
                            actual.RelativePath,
                            trusted.RelativePath,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            actual.RemoteRepository,
                            trusted.RemoteRepository,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(
                            $"candidate repository '{actual.RelativePath}' does not match the trusted workspace mapping");
                    }
                }
            }
        }
        if (candidate.Manifest.TrustedScaffoldFiles is null)
        {
            errors.Add("candidate trustedScaffoldFiles is required");
        }
        else
        {
            var scaffoldFiles = candidate.Manifest.TrustedScaffoldFiles
                .Where(item => item is not null)
                .ToArray();
            if (scaffoldFiles.Length !=
                candidate.Manifest.TrustedScaffoldFiles.Count)
            {
                errors.Add("candidate trusted scaffold entries cannot be null");
            }
            ValidateManifestOrdering(
                scaffoldFiles.Select(
                    item => item.RelativePath),
                "candidate trusted scaffold file",
                errors);
            foreach (var scaffoldFile in
                     scaffoldFiles)
            {
                if (!IsSafeRelativePath(scaffoldFile.RelativePath) ||
                    scaffoldFile.RelativePath.StartsWith(
                        ".customer-preview/",
                        StringComparison.OrdinalIgnoreCase) ||
                    scaffoldFile.RelativePath.StartsWith(
                        ".ai-harness/",
                        StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(
                        $"candidate trusted scaffold path '{scaffoldFile.RelativePath}' is invalid");
                }
                if (scaffoldFile.Length < 0 ||
                    !IsSha256(scaffoldFile.Digest))
                {
                    errors.Add(
                        $"candidate trusted scaffold file '{scaffoldFile.RelativePath}' has invalid metadata");
                }
            }
        }
        if (candidate.Manifest.PreviewArtifacts is null)
        {
            errors.Add("candidate previewArtifacts is required");
        }
        else
        {
            if (candidate.Manifest.PreviewArtifacts.Count >
                CandidateManifest.MaximumPreviewArtifacts)
            {
                errors.Add(
                    $"candidate previewArtifacts exceeds {CandidateManifest.MaximumPreviewArtifacts} files");
            }
            var previewArtifacts = candidate.Manifest.PreviewArtifacts
                .Where(item => item is not null)
                .ToArray();
            if (previewArtifacts.Length !=
                candidate.Manifest.PreviewArtifacts.Count)
            {
                errors.Add("candidate preview artifact entries cannot be null");
            }
            ValidateManifestOrdering(
                previewArtifacts.Select(item => item.RelativePath),
                "candidate preview artifact",
                errors);
            long totalLength = 0;
            foreach (var artifact in previewArtifacts)
            {
                if (!IsSafeRelativePath(artifact.RelativePath) ||
                    !artifact.RelativePath.StartsWith(
                        ".customer-preview/",
                        StringComparison.Ordinal))
                {
                    errors.Add(
                        $"candidate preview path '{artifact.RelativePath}' is invalid");
                }
                if (artifact.Length < 0)
                {
                    errors.Add(
                        $"candidate preview '{artifact.RelativePath}' has a negative length");
                }
                else
                {
                    totalLength += artifact.Length;
                }
                if (!IsSha256(artifact.Digest))
                {
                    errors.Add(
                        $"candidate preview '{artifact.RelativePath}' has an invalid digest");
                }
            }
            if (totalLength > 100L * 1024 * 1024)
            {
                errors.Add("candidate preview exceeds the 100 MiB limit");
            }
        }
        if (!string.Equals(
                candidate.Fingerprint,
                HashCandidateManifest(candidate.Manifest),
                StringComparison.Ordinal))
        {
            errors.Add("candidate fingerprint does not match the canonical manifest");
        }
        if (candidate.PreparedByStepId == Guid.Empty)
        {
            errors.Add("candidate preparedByStepId is required");
        }
    }

    private static void ValidatePublication(
        OutcomeVerificationState state,
        ICollection<string> errors,
        bool allowVerifiedSupersededPublication)
    {
        if (state.Publication is null)
        {
            return;
        }
        if (state.CurrentCandidate is null ||
            !string.Equals(
                state.Publication.CandidateFingerprint,
                state.CurrentCandidate.Fingerprint,
                StringComparison.Ordinal))
        {
            errors.Add("publication journal does not match the current candidate");
            return;
        }
        if (state.Publication.StepId == Guid.Empty)
        {
            errors.Add("publication journal stepId is required");
        }
        if (state.Publication.Repositories is null)
        {
            errors.Add("publication journal repositories is required");
            return;
        }
        var manifestRepositories = state.CurrentCandidate.Manifest.Repositories;
        if (state.Publication.Repositories.Count != manifestRepositories.Count)
        {
            errors.Add(
                "publication journal must contain every candidate repository exactly once");
            return;
        }
        for (var index = 0; index < manifestRepositories.Count; index++)
        {
            var expected = manifestRepositories[index];
            var actual = state.Publication.Repositories[index];
            if (expected is null || actual is null)
            {
                errors.Add(
                    $"publication repository {index + 1} cannot be null");
                continue;
            }
            if (!string.Equals(
                    actual.RelativePath,
                    expected.RelativePath,
                    StringComparison.Ordinal) ||
                !string.Equals(actual.Head, expected.Head, StringComparison.Ordinal) ||
                !string.Equals(actual.Tree, expected.Tree, StringComparison.Ordinal))
            {
                errors.Add(
                    $"publication repository {index + 1} does not match the candidate manifest");
            }
        }
        if ((state.Publication.Status is
                 OutcomePublicationStatus.Published or
                 OutcomePublicationStatus.Verified) &&
            state.Publication.Repositories.Any(item =>
                item is null ||
                item.Status != OutcomeRepositoryPublicationStatus.Published))
        {
            errors.Add(
                "a completed publication journal requires every repository to be published");
        }
        if (state.Publication.Status == OutcomePublicationStatus.Verified &&
            state.Status != OutcomeVerificationStatus.Passed &&
            !(allowVerifiedSupersededPublication &&
              state.Status == OutcomeVerificationStatus.Superseded &&
              state.VerifiedCandidateFingerprint is not null))
        {
            errors.Add("verified publication requires a Passed outcome state");
        }
    }

    private static string SafeHashAcceptancePlan(
        OutcomeAcceptancePlan plan,
        ICollection<string> errors)
    {
        try
        {
            return HashAcceptancePlan(plan);
        }
        catch (OutcomeVerificationValidationException exception)
        {
            foreach (var error in exception.Errors)
            {
                errors.Add($"acceptancePlan: {error}");
            }
            return string.Empty;
        }
    }

    private static string ExtractMarkedJson(
        string output,
        string beginMarker,
        string endMarker,
        string label)
    {
        var normalized = output.ReplaceLineEndings("\n");
        var beginLocations = new List<(int Start, int End)>();
        var endLocations = new List<(int Start, int End)>();
        var markerErrors = new List<string>();
        if (CountOccurrences(normalized, beginMarker) != 1 ||
            CountOccurrences(normalized, endMarker) != 1)
        {
            markerErrors.Add(
                $"{label} must contain each exact marker exactly once");
        }
        MarkdownFence? openFence = null;
        var offset = 0;
        foreach (var line in normalized.Split('\n'))
        {
            var trimmed = line.Trim();
            if (openFence is { } fence)
            {
                if (IsClosingFence(line, fence))
                {
                    openFence = null;
                }
            }
            else if (TryReadOpeningFence(line, out var openingFence))
            {
                openFence = openingFence;
            }
            else
            {
                CollectMarker(
                    line,
                    trimmed,
                    beginMarker,
                    offset,
                    beginLocations,
                    markerErrors);
                CollectMarker(
                    line,
                    trimmed,
                    endMarker,
                    offset,
                    endLocations,
                    markerErrors);
            }
            offset += line.Length + 1;
        }

        if (beginLocations.Count != 1 || endLocations.Count != 1)
        {
            markerErrors.Add(
                $"{label} must contain each exact marker once, on its own line, outside Markdown fences");
        }
        if (markerErrors.Count > 0)
        {
            throw new OutcomeVerificationValidationException(markerErrors);
        }

        var begin = beginLocations[0];
        var end = endLocations[0];
        if (end.Start <= begin.End)
        {
            throw Invalid($"{label} end marker must follow its begin marker");
        }
        return normalized[begin.End..end.Start].Trim();
    }

    private static bool TryReadOpeningFence(
        string line,
        out MarkdownFence fence)
    {
        fence = default;
        if (!TryReadFenceRun(line, out var character, out var length, out var remainder))
        {
            return false;
        }
        if (character == '`' && remainder.Contains('`'))
        {
            return false;
        }
        fence = new MarkdownFence(character, length);
        return true;
    }

    private static bool IsClosingFence(string line, MarkdownFence opening)
    {
        if (!TryReadFenceRun(
                line,
                out var character,
                out var length,
                out var remainder))
        {
            return false;
        }
        return character == opening.Character &&
               length >= opening.Length &&
               string.IsNullOrWhiteSpace(remainder);
    }

    private static bool TryReadFenceRun(
        string line,
        out char character,
        out int length,
        out string remainder)
    {
        character = default;
        length = 0;
        remainder = string.Empty;
        var offset = 0;
        while (offset < line.Length && offset < 3 && line[offset] == ' ')
        {
            offset++;
        }
        if (offset < line.Length && line[offset] == ' ')
        {
            return false;
        }
        if (offset >= line.Length || line[offset] is not ('`' or '~'))
        {
            return false;
        }
        character = line[offset];
        var end = offset;
        while (end < line.Length && line[end] == character)
        {
            end++;
        }
        length = end - offset;
        if (length < 3)
        {
            return false;
        }
        remainder = line[end..];
        return true;
    }

    private readonly record struct MarkdownFence(char Character, int Length);

    private static void CollectMarker(
        string line,
        string trimmed,
        string marker,
        int offset,
        ICollection<(int Start, int End)> locations,
        ICollection<string> errors)
    {
        if (!line.Contains(marker, StringComparison.Ordinal))
        {
            return;
        }
        if (!string.Equals(trimmed, marker, StringComparison.Ordinal))
        {
            errors.Add($"marker '{marker}' must be on a standalone line");
            return;
        }

        var start = offset + line.IndexOf(marker, StringComparison.Ordinal);
        locations.Add((start, offset + line.Length + 1));
    }

    private static int CountOccurrences(string value, string marker)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(
                   marker,
                   offset,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += marker.Length;
        }
        return count;
    }

    private static T DeserializeStrict<T>(string json, string label)
    {
        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions { MaxDepth = MaximumJsonDepth });
            var duplicates = new List<string>();
            FindDuplicateProperties(document.RootElement, "$", duplicates);
            if (duplicates.Count > 0)
            {
                throw new OutcomeVerificationValidationException(duplicates);
            }
            ValidateRequiredJsonProperties<T>(document.RootElement);
            return JsonSerializer.Deserialize<T>(json, StrictJsonOptions)
                   ?? throw Invalid($"{label} document is null");
        }
        catch (OutcomeVerificationValidationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw Invalid(
                $"sentinel content is not strict {label} JSON: {exception.Message}");
        }
    }

    private static void ValidateRequiredJsonProperties<T>(JsonElement root)
    {
        var errors = new List<string>();
        if (typeof(T) == typeof(OutcomeAcceptancePlan))
        {
            ValidateAcceptancePlanJson(root, "$", errors);
        }
        else if (typeof(T) == typeof(OutcomeDeliveryEvidenceDocument))
        {
            ValidateDeliveryEvidenceJson(root, "$", errors);
        }
        else if (typeof(T) == typeof(OutcomeQaResult))
        {
            ValidateQaResultJson(root, "$", errors);
        }
        else if (typeof(T) == typeof(OutcomeVerificationState))
        {
            ValidateAggregateJson(root, "$", errors);
        }

        if (errors.Count > 0)
        {
            throw new OutcomeVerificationValidationException(errors);
        }
    }

    private static void ValidateAcceptancePlanJson(
        JsonElement element,
        string path,
        ICollection<string> errors)
    {
        RequireProperties(element, path, errors, "Version", "Criteria");
        VisitArrayProperty(
            element,
            "Criteria",
            path,
            errors,
            (criterion, criterionPath) => RequireProperties(
                criterion,
                criterionPath,
                errors,
                "Id",
                "Requirement",
                "Verification",
                "OwnerRoles",
                "EvidenceKinds",
                "CustomerVisible"));
    }

    private static void ValidateDeliveryEvidenceJson(
        JsonElement element,
        string path,
        ICollection<string> errors)
    {
        RequireProperties(element, path, errors, "Version", "Items");
        VisitArrayProperty(
            element,
            "Items",
            path,
            errors,
            (item, itemPath) => RequireProperties(
                item,
                itemPath,
                errors,
                "CriterionId",
                "Disposition",
                "Kind",
                "Locator",
                "ObservedResult",
                "ContentDigest"));
    }

    private static void ValidateQaResultJson(
        JsonElement element,
        string path,
        ICollection<string> errors)
    {
        RequireProperties(
            element,
            path,
            errors,
            "Version",
            "AcceptancePlanHash",
            "CandidateFingerprint",
            "Verdict",
            "Criteria");
        VisitArrayProperty(
            element,
            "Criteria",
            path,
            errors,
            (criterion, criterionPath) =>
            {
                RequireProperties(
                    criterion,
                    criterionPath,
                    errors,
                    "CriterionId",
                    "Status",
                    "EvidenceIds",
                    "ChecksPerformed",
                    "Rationale",
                    "ResponsibleRoles");
                VisitArrayProperty(
                    criterion,
                    "ChecksPerformed",
                    criterionPath,
                    errors,
                    (check, checkPath) => RequireProperties(
                        check,
                        checkPath,
                        errors,
                        "Kind",
                        "Locator",
                        "ObservedResult"));
            });
        VisitArrayProperty(
            element,
            "PlanGaps",
            path,
            errors,
            (gap, gapPath) => RequireProperties(
                gap,
                gapPath,
                errors,
                "Requirement",
                "Verification",
                "OwnerRoles",
                "Rationale"),
            required: false);
    }

    private static void ValidateAggregateJson(
        JsonElement element,
        string path,
        ICollection<string> errors)
    {
        RequireProperties(
            element,
            path,
            errors,
            "Version",
            "Iteration",
            "Status",
            "MaxRounds",
            "ManualRoundsGranted",
            "PriorIterations",
            "TrustedRepositories",
            "PlannedRoles",
            "InitialPlanSemanticRootId",
            "InitialDeliverySemanticRootIds",
            "ProcessedSemanticRootIds",
            "AcceptancePlan",
            "Evidence",
            "EvidenceProcessing",
            "Rounds",
            "ActiveQaRound",
            "ActiveQaStepId",
            "ActiveQaContextPath",
            "ActiveQaContextHash",
            "CurrentCandidate",
            "Publication",
            "VerifiedCandidateFingerprint",
            "VerifiedAt",
            "PendingOwnerRoles",
            "Stale",
            "UpdatedAt");
        VisitArrayProperty(
            element,
            "PriorIterations",
            path,
            errors,
            (archive, archivePath) =>
                ValidateIterationArchiveJson(archive, archivePath, errors));
        VisitArrayProperty(
            element,
            "TrustedRepositories",
            path,
            errors,
            (repository, repositoryPath) => RequireProperties(
                repository,
                repositoryPath,
                errors,
                "RelativePath",
                "RemoteRepository"));
        VisitOptionalObjectProperty(
            element,
            "AcceptancePlan",
            path,
            errors,
            (plan, planPath) =>
                ValidateAcceptanceSnapshotJson(plan, planPath, errors));
        VisitArrayProperty(
            element,
            "Evidence",
            path,
            errors,
            (evidence, evidencePath) =>
                ValidateAggregateEvidenceJson(evidence, evidencePath, errors));
        VisitArrayProperty(
            element,
            "EvidenceProcessing",
            path,
            errors,
            (processing, processingPath) => RequireProperties(
                processing,
                processingPath,
                errors,
                "ProducerStepId",
                "AcceptancePlanHash",
                "ProducerRole",
                "CriterionIds",
                "ProcessedAt"));
        VisitArrayProperty(
            element,
            "Rounds",
            path,
            errors,
            (round, roundPath) =>
                ValidateQaRoundJson(round, roundPath, errors));
        VisitOptionalObjectProperty(
            element,
            "CurrentCandidate",
            path,
            errors,
            (candidate, candidatePath) =>
                ValidateCandidateSnapshotJson(candidate, candidatePath, errors));
        VisitOptionalObjectProperty(
            element,
            "Publication",
            path,
            errors,
            (publication, publicationPath) =>
                ValidatePublicationJson(publication, publicationPath, errors));
    }

    private static void ValidateIterationArchiveJson(
        JsonElement element,
        string path,
        ICollection<string> errors)
    {
        RequireProperties(
            element,
            path,
            errors,
            "Iteration",
            "Status",
            "MaxRounds",
            "ManualRoundsGranted",
            "PlannedRoles",
            "InitialPlanSemanticRootId",
            "InitialDeliverySemanticRootIds",
            "ProcessedSemanticRootIds",
            "AcceptancePlan",
            "Evidence",
            "EvidenceProcessing",
            "Rounds",
            "CurrentCandidate",
            "Publication",
            "VerifiedCandidateFingerprint",
            "ArchivedAt");
        VisitOptionalObjectProperty(
            element,
            "AcceptancePlan",
            path,
            errors,
            (plan, planPath) =>
                ValidateAcceptanceSnapshotJson(plan, planPath, errors));
        VisitArrayProperty(
            element,
            "Evidence",
            path,
            errors,
            (evidence, evidencePath) =>
                ValidateAggregateEvidenceJson(evidence, evidencePath, errors));
        VisitArrayProperty(
            element,
            "EvidenceProcessing",
            path,
            errors,
            (processing, processingPath) => RequireProperties(
                processing,
                processingPath,
                errors,
                "ProducerStepId",
                "AcceptancePlanHash",
                "ProducerRole",
                "CriterionIds",
                "ProcessedAt"));
        VisitArrayProperty(
            element,
            "Rounds",
            path,
            errors,
            (round, roundPath) =>
                ValidateQaRoundJson(round, roundPath, errors));
        VisitOptionalObjectProperty(
            element,
            "CurrentCandidate",
            path,
            errors,
            (candidate, candidatePath) =>
                ValidateCandidateSnapshotJson(candidate, candidatePath, errors));
        VisitOptionalObjectProperty(
            element,
            "Publication",
            path,
            errors,
            (publication, publicationPath) =>
                ValidatePublicationJson(publication, publicationPath, errors));
    }

    private static void ValidateAcceptanceSnapshotJson(
        JsonElement element,
        string path,
        ICollection<string> errors)
    {
        RequireProperties(element, path, errors, "Hash", "SourceStepId", "Criteria");
        VisitArrayProperty(
            element,
            "Criteria",
            path,
            errors,
            (criterion, criterionPath) => RequireProperties(
                criterion,
                criterionPath,
                errors,
                "Id",
                "Requirement",
                "Verification",
                "OwnerRoles",
                "EvidenceKinds",
                "CustomerVisible"));
    }

    private static void ValidateAggregateEvidenceJson(
        JsonElement element,
        string path,
        ICollection<string> errors)
    {
        RequireProperties(
            element,
            path,
            errors,
            "EvidenceId",
            "CriterionId",
            "Disposition",
            "Kind",
            "Locator",
            "ObservedResult",
            "ExitCode",
            "ContentDigest",
            "ProducerRole",
            "ProducerStepId",
            "ProducedAt");
    }

    private static void ValidateQaRoundJson(
        JsonElement element,
        string path,
        ICollection<string> errors)
    {
        RequireProperties(
            element,
            path,
            errors,
            "Round",
            "QaStepId",
            "AcceptancePlanHash",
            "CandidateFingerprint",
            "ContextHash",
            "Verdict",
            "Result",
            "ContractError",
            "Stale",
            "CorrectionStepIds",
            "CorrectionRegistrations",
            "ProcessedCorrectionRootIds",
            "CompletedAt");
        VisitArrayProperty(
            element,
            "CorrectionRegistrations",
            path,
            errors,
            (registration, registrationPath) => RequireProperties(
                registration,
                registrationPath,
                errors,
                "QaRound",
                "OutcomePlanHash",
                "Role",
                "StepId",
                "SemanticRootId"));
        VisitOptionalObjectProperty(
            element,
            "Result",
            path,
            errors,
            (result, resultPath) => ValidateQaResultJson(result, resultPath, errors));
    }

    private static void ValidateCandidateSnapshotJson(
        JsonElement element,
        string path,
        ICollection<string> errors)
    {
        RequireProperties(
            element,
            path,
            errors,
            "Manifest",
            "Fingerprint",
            "PreparedByStepId",
            "PreparedAt");
        VisitOptionalObjectProperty(
            element,
            "Manifest",
            path,
            errors,
            (manifest, manifestPath) =>
                ValidateCandidateManifestJson(manifest, manifestPath, errors),
            required: true);
    }

    private static void ValidateCandidateManifestJson(
        JsonElement element,
        string path,
        ICollection<string> errors)
    {
        RequireProperties(
            element,
            path,
            errors,
            "Version",
            "FlowIteration",
            "AcceptancePlanHash",
            "Repositories",
            "TrustedScaffoldFiles",
            "PreviewArtifacts");
        VisitArrayProperty(
            element,
            "Repositories",
            path,
            errors,
            (repository, repositoryPath) => RequireProperties(
                repository,
                repositoryPath,
                errors,
                "RelativePath",
                "Head",
                "Tree",
                "RemoteRepository"));
        VisitArrayProperty(
            element,
            "TrustedScaffoldFiles",
            path,
            errors,
            (scaffoldFile, scaffoldFilePath) => RequireProperties(
                scaffoldFile,
                scaffoldFilePath,
                errors,
                "RelativePath",
                "Length",
                "Digest"));
        VisitArrayProperty(
            element,
            "PreviewArtifacts",
            path,
            errors,
            (artifact, artifactPath) => RequireProperties(
                artifact,
                artifactPath,
                errors,
                "RelativePath",
                "Length",
                "Digest"));
    }

    private static void ValidatePublicationJson(
        JsonElement element,
        string path,
        ICollection<string> errors)
    {
        RequireProperties(
            element,
            path,
            errors,
            "StepId",
            "CandidateFingerprint",
            "Status",
            "Repositories",
            "UpdatedAt");
        VisitArrayProperty(
            element,
            "Repositories",
            path,
            errors,
            (repository, repositoryPath) => RequireProperties(
                repository,
                repositoryPath,
                errors,
                "RelativePath",
                "Head",
                "Tree",
                "RemoteRepository",
                "PullRequestUrl",
                "Status"));
    }

    private static void VisitArrayProperty(
        JsonElement parent,
        string propertyName,
        string path,
        ICollection<string> errors,
        Action<JsonElement, string> visit,
        bool required = true)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(propertyName, out var array))
        {
            if (required && parent.ValueKind == JsonValueKind.Object)
            {
                errors.Add($"{path}.{propertyName} is required");
            }
            return;
        }
        if (array.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{path}.{propertyName} must be an array");
            return;
        }

        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            visit(item, $"{path}.{propertyName}[{index++}]");
        }
    }

    private static void VisitOptionalObjectProperty(
        JsonElement parent,
        string propertyName,
        string path,
        ICollection<string> errors,
        Action<JsonElement, string> visit,
        bool required = false)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(propertyName, out var child))
        {
            if (required && parent.ValueKind == JsonValueKind.Object)
            {
                errors.Add($"{path}.{propertyName} is required");
            }
            return;
        }
        if (child.ValueKind != JsonValueKind.Object)
        {
            if (required)
            {
                errors.Add($"{path}.{propertyName} must be an object");
            }
            return;
        }

        visit(child, $"{path}.{propertyName}");
    }

    private static void RequireProperties(
        JsonElement element,
        string path,
        ICollection<string> errors,
        params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out _))
            {
                errors.Add($"{path}.{propertyName} is required");
            }
        }
    }

    private static void FindDuplicateProperties(
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
                    errors.Add($"duplicate JSON property '{path}.{property.Name}'");
                }
                FindDuplicateProperties(property.Value, $"{path}.{property.Name}", errors);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                FindDuplicateProperties(item, $"{path}[{index++}]", errors);
            }
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
            MaxDepth = MaximumJsonDepth
        };
        options.Converters.Add(new ExactEnumJsonConverter<OutcomeVerificationStatus>());
        options.Converters.Add(new ExactEnumJsonConverter<OutcomeEvidenceKind>());
        options.Converters.Add(new ExactEnumJsonConverter<OutcomeEvidenceDisposition>());
        options.Converters.Add(new ExactEnumJsonConverter<OutcomeCriterionStatus>());
        options.Converters.Add(new ExactEnumJsonConverter<OutcomeQaVerdict>());
        options.Converters.Add(new ExactEnumJsonConverter<OutcomePublicationStatus>());
        options.Converters.Add(
            new ExactEnumJsonConverter<OutcomeRepositoryPublicationStatus>());
        return options;
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw Invalid($"unsupported JSON token '{element.ValueKind}'");
        }
    }

    private static bool DescribesObservableCheck(string value)
    {
        var assessment = AnalyzeObservableCheck(value, observedResult: null);
        return assessment.HasInspectionAction &&
               assessment.HasSubstantiveOutcome &&
               !assessment.IsExistenceOnly;
    }

    private static bool DemonstratesOutcomeBeyondExistence(OutcomeQaCheck check)
    {
        var assessment = AnalyzeObservableCheck(check.Locator, check.ObservedResult);
        return assessment.HasInspectionAction &&
               assessment.HasSubstantiveOutcome &&
               !assessment.IsExistenceOnly;
    }

    private static ObservableCheckAssessment AnalyzeObservableCheck(
        string instructionOrLocator,
        string? observedResult)
    {
        var normalizedInstruction = NormalizeObservableText(instructionOrLocator);
        var normalizedObserved = NormalizeObservableText(observedResult);
        var combined = string.IsNullOrWhiteSpace(normalizedObserved)
            ? normalizedInstruction
            : $"{normalizedInstruction} {normalizedObserved}";
        var hasInspectionAction =
            ObservableActionPattern.IsMatch(normalizedInstruction) ||
            ObservableActionPattern.IsMatch(combined);
        var hasObservableSubject = ObservableSubjectPattern.IsMatch(combined);
        var hasObservableOutcomeVerb = ObservableOutcomePattern.IsMatch(combined);
        var hasStructuredObservation =
            StructuredObservationPattern.IsMatch(observedResult ?? string.Empty) ||
            StructuredObservationPattern.IsMatch(instructionOrLocator);
        var hasSubjectValueObservation = SubjectValuePattern.IsMatch(combined);
        var usesContentInspection =
            ContentInspectionCommandPattern.IsMatch(normalizedInstruction);
        var hasSubstantiveOutcome =
            (hasObservableSubject &&
             (hasObservableOutcomeVerb ||
              hasSubjectValueObservation ||
              (!string.IsNullOrWhiteSpace(normalizedObserved) &&
               hasStructuredObservation))) ||
            (usesContentInspection &&
             (hasObservableOutcomeVerb ||
              hasSubjectValueObservation ||
              hasStructuredObservation));
        var usesExistenceProbe =
            ListingOrExistenceProbePattern.IsMatch(normalizedInstruction) ||
            (BareRelativePathPattern.IsMatch(normalizedInstruction) &&
             !normalizedInstruction.Contains(' '));
        var reportsOnlyExistence =
            ExistenceOnlyStatementPattern.IsMatch(combined) &&
            !hasObservableOutcomeVerb &&
            !hasSubjectValueObservation &&
            !hasStructuredObservation;
        return new ObservableCheckAssessment(
            hasInspectionAction,
            hasSubstantiveOutcome,
            usesExistenceProbe && !usesContentInspection ||
            reportsOnlyExistence);
    }

    private static string NormalizeObservableText(string? value) =>
        string.Join(
            " ",
            (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();

    private static bool DescribesExternalBlocker(string value)
    {
        var normalized = value?.ToLowerInvariant() ?? string.Empty;
        return normalized.Contains("external", StringComparison.Ordinal) ||
               normalized.Contains("third-party", StringComparison.Ordinal) ||
               normalized.Contains("customer", StringComparison.Ordinal) ||
               normalized.Contains("vendor", StringComparison.Ordinal);
    }

    private static bool IsReleaseCriterion(OutcomeAcceptanceCriterion criterion)
    {
        var value = $"{criterion.Requirement} {criterion.Verification}";
        return ReleaseTerms.Any(term =>
            value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateManifestOrdering(
        IEnumerable<string> paths,
        string label,
        ICollection<string> errors)
    {
        var values = paths.ToArray();
        if (values.Length != values.Distinct(StringComparer.Ordinal).Count())
        {
            errors.Add($"{label} paths must be unique");
        }
        if (!values.SequenceEqual(
                values.OrderBy(item => item, StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            errors.Add($"{label} paths must be sorted ordinally");
        }
    }

    private static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return false;
        }
        if (path == ".")
        {
            return true;
        }
        return !path.Contains('\\') &&
               path.Split('/').All(segment =>
                   segment.Length > 0 &&
                   segment is not "." and not "..");
    }

    private static void ValidateText(
        string? value,
        int maximumLength,
        string label,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            errors.Add($"{label} must contain 1-{maximumLength} characters");
        }
    }

    private static void ValidatePlannedRoles(IReadOnlyList<string> downstreamRoles)
    {
        ArgumentNullException.ThrowIfNull(downstreamRoles);
        if (downstreamRoles.Count == 0 ||
            downstreamRoles.Any(string.IsNullOrWhiteSpace) ||
            downstreamRoles.Count != downstreamRoles.Distinct(StringComparer.Ordinal).Count())
        {
            throw Invalid(
                "the fixed downstream plan must contain unique, non-empty role IDs");
        }
    }

    private static void EnsureSize(string json, int maximumBytes, string label)
    {
        var size = Encoding.UTF8.GetByteCount(json);
        if (size > maximumBytes)
        {
            throw Invalid($"{label} exceeds the {maximumBytes}-byte limit");
        }
    }

    private static OutcomeVerificationValidationException Invalid(string error) =>
        new([error]);

    private readonly record struct ObservableCheckAssessment(
        bool HasInspectionAction,
        bool HasSubstantiveOutcome,
        bool IsExistenceOnly);

    private sealed class ExactEnumJsonConverter<TEnum> : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        public override TEnum Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException($"{typeof(TEnum).Name} must be a string.");
            }
            var value = reader.GetString();
            if (value is null ||
                !Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed) ||
                !Enum.IsDefined(parsed))
            {
                throw new JsonException(
                    $"'{value}' is not an exact {typeof(TEnum).Name} value.");
            }
            return parsed;
        }

        public override void Write(
            Utf8JsonWriter writer,
            TEnum value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}

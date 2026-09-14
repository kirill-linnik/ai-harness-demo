using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Services;

namespace AiHarnessDemo.Tests;

public sealed class DeliveryReadinessPolicyTests
{
    [Fact]
    public void VerifiedCriterion_RequiresSuccessfulEvidenceOfPlannedKind()
    {
        var plan = DeliveryReadinessFixtures.Plan();
        var planHash = DeliveryReadinessPolicy.HashAcceptancePlan(plan);
        var output = QaOutput(planHash, "EV-S010-001");

        var failedEvidence = new DeliveryEvidenceItem(
            "EV-S010-001",
            OutcomeEvidenceKind.Observation,
            "browser",
            "The browser check failed.",
            SupportsVerification: false,
            ExitCode: 1,
            ResultDigest: string.Empty);
        var failed = Assert.Throws<DeliveryReadinessContractException>(() =>
            DeliveryReadinessPolicy.ParseQaOutput(
                output,
                plan,
                planHash,
                [failedEvidence]));
        Assert.Contains("unsuccessful evidence", failed.Message);

        var wrongKind = failedEvidence with
        {
            Kind = OutcomeEvidenceKind.Command,
            SupportsVerification = true,
            ExitCode = 0
        };
        var mismatched = Assert.Throws<DeliveryReadinessContractException>(() =>
            DeliveryReadinessPolicy.ParseQaOutput(
                output,
                plan,
                planHash,
                [wrongKind]));
        Assert.Contains("not allowed by the acceptance plan", mismatched.Message);

        var valid = wrongKind with
        {
            Kind = OutcomeEvidenceKind.Observation
        };
        var parsed = DeliveryReadinessPolicy.ParseQaOutput(
            output,
            plan,
            planHash,
            [valid]);
        Assert.Equal(OutcomeQaVerdict.PASS, parsed.Document.Verdict);
    }

    [Fact]
    public void PlanGap_PreventsPassAndProducesNeedsRefinement()
    {
        var plan = DeliveryReadinessFixtures.Plan();
        var planHash = DeliveryReadinessPolicy.HashAcceptancePlan(plan);
        var output = QaOutput(
            planHash,
            "EV-S010-001",
            verdict: "FAIL",
            planGaps:
                """
                [{"Requirement":"The confirmed brief also requires audit export.","Verification":"Export and inspect the audit file.","OwnerRoles":["team-lead"],"Rationale":"No acceptance criterion covers this confirmed requirement."}]
                """);
        DeliveryEvidenceItem[] evidence =
        [
            new(
                "EV-S010-001",
                OutcomeEvidenceKind.Observation,
                "browser",
                "The planned result was observed.",
                SupportsVerification: true,
                ExitCode: null,
                ResultDigest: string.Empty)
        ];
        var qa = DeliveryReadinessPolicy.ParseQaOutput(
            output,
            plan,
            planHash,
            evidence).Document;

        var snapshot = DeliveryReadinessPolicy.Derive(
            new DeliveryReadinessDerivationInput(
                Guid.NewGuid(),
                1,
                1,
                plan,
                planHash,
                qa,
                OutcomeVerificationRules.ComputeSha256("qa"),
                Guid.NewGuid(),
                "quality-engineer",
                Guid.NewGuid(),
                OutcomeVerificationRules.ComputeSha256("outcome"),
                OutcomeVerificationRules.ComputeSha256("candidate"),
                [],
                evidence,
                [],
                [],
                DateTimeOffset.UtcNow,
                Guid.NewGuid()));

        Assert.Equal(DeliveryReadinessState.NeedsRefinement, snapshot.State);
        Assert.Contains(
            snapshot.Diagnostics,
            diagnostic => diagnostic.Contains(
                "acceptance-plan gap",
                StringComparison.Ordinal));
    }

    [Fact]
    public void VersionedQaPayload_IsRejected()
    {
        var plan = DeliveryReadinessFixtures.Plan();
        var planHash = DeliveryReadinessPolicy.HashAcceptancePlan(plan);
        var versioned = QaOutput(planHash, "EV-S010-001")
            .Replace(
                """{"AcceptancePlanHash":""",
                """{"Version":"outcome-qa-v2","AcceptancePlanHash":""",
                StringComparison.Ordinal);

        var exception = Assert.Throws<DeliveryReadinessContractException>(() =>
            DeliveryReadinessPolicy.ParseQaOutput(
                versioned,
                plan,
                planHash,
                [
                    new DeliveryEvidenceItem(
                        "EV-S010-001",
                        OutcomeEvidenceKind.Observation,
                        "browser",
                        "Observed.",
                        SupportsVerification: true,
                        ExitCode: null,
                        ResultDigest: string.Empty)
                ]));

        Assert.Contains("Version", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HostEvidence_RecordsToolOutcomeAndKind()
    {
        var step = new FlowStep
        {
            Sequence = 20,
            AgentId = "generalist",
            AgentName = "Generalist",
            PlanStepKey = "verify",
            AgentRole = "generalist"
        };
        AgentToolCall[] toolCalls =
        [
            new()
            {
                FlowStepId = step.Id,
                ToolName = "powershell",
                ArgumentsSummary = "dotnet test",
                ToolType = "Command",
                NormalizedCommand = "dotnet",
                NormalizedArguments = "test",
                Succeeded = true,
                ExitCode = 0,
                ResultDigest = OutcomeVerificationRules.ComputeSha256("passed"),
                ResultSummary = "All tests passed."
            }
        ];

        var evidence = DeliveryReadinessService.BuildEvidence(step, toolCalls);

        Assert.False(evidence.Items[0].SupportsVerification);
        Assert.Equal(OutcomeEvidenceKind.Test, evidence.Items[1].Kind);
        Assert.True(evidence.Items[1].SupportsVerification);
        Assert.Equal(0, evidence.Items[1].ExitCode);
    }

    [Fact]
    public void HostEvidence_DoesNotTreatTestFileNamesAsTestCommands()
    {
        var step = new FlowStep
        {
            Sequence = 20,
            AgentId = "software-engineer",
            AgentName = "Software Engineer",
            PlanStepKey = "implement",
            AgentRole = "software-engineer"
        };
        AgentToolCall[] toolCalls =
        [
            new()
            {
                FlowStepId = step.Id,
                ToolName = "powershell",
                ArgumentsSummary = "git diff",
                ToolType = "Command",
                NormalizedCommand = "git",
                NormalizedArguments =
                    "--no-pager diff -- server.js server.test.js README.md",
                Succeeded = true,
                ExitCode = 0,
                ResultDigest = OutcomeVerificationRules.ComputeSha256("diff"),
                ResultSummary = "README and source diff."
            }
        ];

        var evidence = DeliveryReadinessService.BuildEvidence(step, toolCalls);

        Assert.Equal(
            OutcomeEvidenceKind.SourceInspection,
            evidence.Items[1].Kind);
    }

    private static string QaOutput(
        string planHash,
        string evidenceId,
        string verdict = "PASS",
        string planGaps = "[]") =>
        $$"""
        {{DeliveryReadinessPolicy.QaBeginMarker}}
        {"AcceptancePlanHash":"{{planHash}}","Verdict":"{{verdict}}","Criteria":[{"CriterionId":"AC-001","Outcome":"Verified","EvidenceIds":["{{evidenceId}}"],"Rationale":"The successful observed evidence satisfies the criterion.","Remediation":null,"ResponsibleRoles":[]}],"ResidualRisks":[],"PlanGaps":{{planGaps}}}
        {{DeliveryReadinessPolicy.QaEndMarker}}
        """;
}

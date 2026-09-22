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

    [Fact]
    public void HostEvidence_IdentifiesAnActuallyViewedImageAsAnObservation()
    {
        var step = new FlowStep
        {
            Sequence = 100,
            AgentId = "verifier",
            AgentName = "Verifier",
            AgentRole = "verifier"
        };
        var image = new AgentToolCall
        {
            FlowStepId = step.Id,
            ToolName = "view",
            ArgumentsSummary = @"C:\workspace\preview.png",
            NormalizedArguments = """{"path":"C:\\workspace\\preview.png"}""",
            ToolType = "Read",
            Succeeded = true,
            ResultSummary = "Viewed image file successfully."
        };

        var observed = DeliveryReadinessService.BuildEvidence(step, [image]).Items[1];
        Assert.Equal(OutcomeEvidenceKind.Observation, observed.Kind);
        Assert.True(observed.SupportsVerification);
        Assert.Contains("preview.png", observed.Locator);

        image.ResultSummary = "The source file contains the text preview.png.";
        var source = DeliveryReadinessService.BuildEvidence(step, [image]).Items[1];
        Assert.Equal(OutcomeEvidenceKind.SourceInspection, source.Kind);
    }

    [Fact]
    public void HostEvidence_ClassifiesShellBrowserAutomationAsCommand()
    {
        var step = new FlowStep
        {
            Sequence = 80,
            AgentId = "quality-engineer",
            AgentName = "Quality Engineer",
            AgentRole = "quality-engineer"
        };
        var browserCheck = new AgentToolCall
        {
            FlowStepId = step.Id,
            ToolName = "powershell",
            ToolType = "Command",
            ArgumentsSummary = "Run a Playwright viewport check.",
            NormalizedCommand =
                """node -e "const { chromium } = require('playwright'); page.goto(url); return document.documentElement.scrollWidth;" """,
            Succeeded = true,
            ExitCode = 0,
            ResultSummary = "scrollWidth=390 clientWidth=390"
        };

        var observed = DeliveryReadinessService
            .BuildEvidence(step, [browserCheck])
            .Items[1];

        Assert.Equal(OutcomeEvidenceKind.Command, observed.Kind);
        Assert.True(observed.SupportsVerification);
    }

    [Theory]
    [InlineData("npx", "playwright test", OutcomeEvidenceKind.Test)]
    [InlineData("npm", "exec playwright test", OutcomeEvidenceKind.Test)]
    [InlineData("node", "-e \"const { chromium } = require('playwright'); chromium.launch(); page.goto(url);\"", OutcomeEvidenceKind.Command)]
    [InlineData("node", "build.js", OutcomeEvidenceKind.Command)]
    public void HostEvidence_DistinguishesBrowserTestsObservationsAndMentions(
        string command,
        string arguments,
        OutcomeEvidenceKind expected)
    {
        var step = new FlowStep
        {
            Sequence = 80,
            AgentId = "quality-engineer",
            AgentName = "Quality Engineer",
            AgentRole = "quality-engineer"
        };
        var call = new AgentToolCall
        {
            FlowStepId = step.Id,
            ToolName = "powershell",
            ToolType = "Command",
            ArgumentsSummary = $"{command} {arguments}",
            NormalizedCommand = $"{command} {arguments}",
            NormalizedArguments = arguments,
            Succeeded = true,
            ExitCode = 0,
            ResultSummary = "The output mentions playwright overflow."
        };

        Assert.Equal(
            expected,
            DeliveryReadinessService.BuildEvidence(step, [call]).Items[1].Kind);
    }

    [Theory]
    [InlineData(
        "$env:PLAYWRIGHT_BROWSERS_PATH='cache'\nnpx playwright test",
        OutcomeEvidenceKind.Test)]
    [InlineData(
        "$env:NODE_PATH='node_modules'\nnode -e \"const { chromium } = require('playwright'); chromium.launch(); page.goto(url);\"",
        OutcomeEvidenceKind.Command)]
    public void HostEvidence_RecognizesNewlineSeparatedBrowserCommands(
        string rawCommand,
        OutcomeEvidenceKind expected)
    {
        var step = new FlowStep
        {
            Sequence = 80,
            AgentId = "quality-engineer",
            AgentName = "Quality Engineer",
            AgentRole = "quality-engineer"
        };
        var call = new AgentToolCall
        {
            FlowStepId = step.Id,
            ToolName = "powershell",
            ToolType = "Command",
            ArgumentsSummary = rawCommand,
            NormalizedCommand =
                HostObservedToolLocator.NormalizeCommand(rawCommand),
            Succeeded = true,
            ExitCode = 0,
            ResultSummary = "Browser command completed."
        };

        Assert.Equal(
            expected,
            DeliveryReadinessService.BuildEvidence(step, [call]).Items[1].Kind);
    }

    [Fact]
    public void EvidenceEpoch_ExcludesPreRestorationEvidence()
    {
        var oldStepId = Guid.NewGuid();
        var retryStepId = Guid.NewGuid();
        var events = new[]
        {
            new FlowEvent
            {
                Type = DeliveryReadinessService.EvidenceEventType,
                Message = "Old evidence.",
                DataJson = DeliveryReadinessService.SerializeEvidence(
                    new DeliveryEvidenceLedgerEntry(
                        1,
                        oldStepId,
                        80,
                        "quality-engineer",
                        [
                            new DeliveryEvidenceItem(
                                "EV-S080-001",
                                OutcomeEvidenceKind.Command,
                                "old-check",
                                "Old candidate passed.",
                                true,
                                0,
                                OutcomeVerificationRules.ComputeSha256("old"))
                        ]))
            },
            new FlowEvent
            {
                Type = DeliveryReadinessService.EvidenceEpochEventType,
                Message = "Scaffold restoration started a new epoch.",
                DataJson = DeliveryReadinessService.SerializeEvidenceEpoch(
                    new DeliveryEvidenceEpoch(
                        1,
                        retryStepId,
                        120))
            },
            new FlowEvent
            {
                Type = DeliveryReadinessService.EvidenceEventType,
                Message = "Fresh evidence.",
                DataJson = DeliveryReadinessService.SerializeEvidence(
                    new DeliveryEvidenceLedgerEntry(
                        1,
                        retryStepId,
                        120,
                        "quality-engineer",
                        [
                            new DeliveryEvidenceItem(
                                "EV-S120-001",
                                OutcomeEvidenceKind.Command,
                                "fresh-check",
                                "Restored candidate passed.",
                                true,
                                0,
                                OutcomeVerificationRules.ComputeSha256("fresh"))
                        ]))
            }
        };

        var evidence = DeliveryReadinessService.ReadEvidence(events, 1);

        Assert.Single(evidence);
        Assert.Equal("EV-S120-001", evidence[0].EvidenceId);
    }

    [Fact]
    public void EvidencePrompt_KeepsTheCompleteTypedIndexWithoutRepeatingLargeTranscripts()
    {
        var evidence = Enumerable.Range(1, 1_200)
            .Select(index => new DeliveryEvidenceItem(
                $"EV-S{index / 100 + 10:000}-{index % 100 + 1:000}",
                index % 2 == 0 ? OutcomeEvidenceKind.Observation : OutcomeEvidenceKind.Command,
                new string('c', 400),
                new string('r', 400),
                SupportsVerification: index % 3 != 0,
                ExitCode: index % 3 == 0 ? 1 : 0,
                ResultDigest: OutcomeVerificationRules.ComputeSha256(index.ToString())))
            .ToArray();

        var prompt = WorkflowEngine.BuildDeliveryEvidenceContext(evidence);

        Assert.True(prompt.Length < 64_000, $"Evidence context was {prompt.Length} characters.");
        Assert.All(evidence, item => Assert.Contains(item.EvidenceId, prompt));
        Assert.Contains("supportsVerification=false; exitCode=1", prompt);
        Assert.Contains("Observation; supportsVerification=true; exitCode=0", prompt);
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

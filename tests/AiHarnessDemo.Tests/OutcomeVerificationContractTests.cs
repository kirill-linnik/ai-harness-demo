using System.Text.Json;
using AiHarnessDemo.Contracts;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Data;
using AiHarnessDemo.Infrastructure;
using AiHarnessDemo.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiHarnessDemo.Tests;

public sealed class OutcomeVerificationContractTests
{
    private static readonly string[] PlannedRoles =
    [
        "software-engineer",
        "technical-writer",
        "release-engineer",
        "quality-engineer"
    ];

    [Fact]
    public void QaPass_NonCommandObservationRequiresConcreteHostSupport()
    {
        var result = PassResult(
            new OutcomeQaCheck(
                OutcomeEvidenceKind.Artifact,
                ".customer-preview/demo/index.html",
                "Rendered content matched.",
                null));

        var errors = HostObservedQaEvidence.ValidatePassChecks(
            result,
            [],
            @"C:\workspace");

        Assert.Contains(
            errors,
            error => error.Contains(
                "no concrete host-observed verification",
                StringComparison.Ordinal));
    }

    [Fact]
    public void QaPass_NonCommandObservationAcceptsMatchingSuccessfulHostRead()
    {
        var workspace = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            $"qa-candidate-{Guid.NewGuid():N}"));
        const string locator = ".customer-preview/demo/index.html";
        var resolved = Path.GetFullPath(Path.Combine(workspace, locator));
        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        File.WriteAllText(resolved, "<h1>Quarterly report heading rendered.</h1>");
        var result = PassResult(
            new OutcomeQaCheck(
                OutcomeEvidenceKind.Artifact,
                locator,
                "Quarterly report heading rendered.",
                null));

        var errors = HostObservedQaEvidence.ValidatePassChecks(
            result,
            [
                new ToolCallRecord(
                    "view",
                    HostObservedToolLocator.CreateLocatorSummary(
                        resolved),
                    Succeeded: true,
                    ToolType: "Read",
                    NormalizedArguments:
                        $$"""{"path":"{{resolved.Replace("\\", "\\\\")}}"}""",
                    WorkingDirectory: workspace,
                    ResultDigest: Digest('c'),
                    ResultSummary:
                        "<h1>Quarterly report heading rendered.</h1>")
            ],
            workspace);

        try
        {
            Assert.Empty(errors);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void QaPass_RejectsUnrelatedToolCallWithClaimedCommandDigest()
    {
        var workspace = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            $"qa-candidate-{Guid.NewGuid():N}"));
        const string command = "dotnet test .\\AiHarnessDemo.slnx";
        var errors = HostObservedQaEvidence.ValidatePassChecks(
            PassResult(new OutcomeQaCheck(
                OutcomeEvidenceKind.Test,
                command,
                "171 tests passed.",
                0)),
            [
                new ToolCallRecord(
                    "view",
                    HostObservedToolLocator.CreateCommandSummary(command),
                    Succeeded: true,
                    ToolType: "Read",
                    NormalizedCommand: command,
                    NormalizedArguments: """{"path":"README.md"}""",
                    WorkingDirectory: workspace,
                    ExitCode: 0,
                    ResultDigest: Digest('d'),
                    ResultSummary: "171 tests passed.")
            ],
            workspace);

        Assert.Contains(
            errors,
            error => error.Contains(
                "assertion, test, or inspection command",
                StringComparison.Ordinal));
    }

    [Fact]
    public void QaPass_RejectsEchoThatOnlyMentionsATestCommand()
    {
        var workspace = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            $"qa-candidate-{Guid.NewGuid():N}"));
        const string command = "echo 171 tests passed dotnet test";
        var errors = HostObservedQaEvidence.ValidatePassChecks(
            PassResult(new OutcomeQaCheck(
                OutcomeEvidenceKind.Test,
                command,
                "171 tests passed.",
                0)),
            [
                ObservedCommand(
                    workspace,
                    command,
                    "171 tests passed.",
                    Digest('5'))
            ],
            workspace,
            ["."]);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void QaPass_RejectsSuccessfulCommandWithoutObservedExitOrResult()
    {
        var workspace = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            $"qa-candidate-{Guid.NewGuid():N}"));
        const string command = "dotnet test";
        var errors = HostObservedQaEvidence.ValidatePassChecks(
            PassResult(new OutcomeQaCheck(
                OutcomeEvidenceKind.Test,
                command,
                "All tests passed.",
                0)),
            [
                new ToolCallRecord(
                    "powershell",
                    HostObservedToolLocator.CreateCommandSummary(command),
                    Succeeded: true,
                    ToolType: "Command",
                    NormalizedCommand: command,
                    NormalizedArguments: """{"command":"dotnet test"}""",
                    WorkingDirectory: workspace)
            ],
            workspace,
            ["."]);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void QaPass_RejectsCompoundCommandThatMasksTestFailure()
    {
        var workspace = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            $"qa-candidate-{Guid.NewGuid():N}"));
        const string command =
            "dotnet test; Write-Output '171 tests passed'; exit 0";
        var errors = HostObservedQaEvidence.ValidatePassChecks(
            PassResult(new OutcomeQaCheck(
                OutcomeEvidenceKind.Test,
                command,
                "171 tests passed.",
                0)),
            [
                ObservedCommand(
                    workspace,
                    command,
                    "171 tests passed.",
                    Digest('6'))
            ],
            workspace,
            ["."]);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void QaPass_RejectsInspectionCommandAgainstTransientOutput()
    {
        var workspace = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            $"qa-candidate-{Guid.NewGuid():N}"));
        const string command =
            "Select-String -Path bin/result.txt -Pattern approved";
        var errors = HostObservedQaEvidence.ValidatePassChecks(
            PassResult(new OutcomeQaCheck(
                OutcomeEvidenceKind.Command,
                command,
                "approved matched.",
                0)),
            [
                ObservedCommand(
                    workspace,
                    command,
                    "approved matched.",
                    Digest('7'))
            ],
            workspace,
            ["."]);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void QaPass_RejectsTurnThatAlsoUsedCandidateWriteTool()
    {
        var workspace = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            $"qa-candidate-{Guid.NewGuid():N}"));
        const string command = "dotnet test";
        var errors = HostObservedQaEvidence.ValidatePassChecks(
            PassResult(new OutcomeQaCheck(
                OutcomeEvidenceKind.Test,
                command,
                "All tests passed.",
                0)),
            [
                new ToolCallRecord(
                    "apply_patch",
                    "Argument values redacted; fields: patch.",
                    Succeeded: true,
                    ToolType: "Other",
                    NormalizedArguments: """{"patch":"<redacted>"}""",
                    WorkingDirectory: workspace,
                    ResultDigest: Digest('8'),
                    ResultSummary: "Patch applied."),
                ObservedCommand(
                    workspace,
                    command,
                    "All tests passed.",
                    Digest('9'))
            ],
            workspace,
            ["."]);

        Assert.Contains(
            errors,
            error => error.Contains("apply_patch", StringComparison.Ordinal));
    }

    [Fact]
    public void QaPass_RejectsOutcomeContextReadAsCandidateInspection()
    {
        var workspace = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            $"qa-candidate-{Guid.NewGuid():N}"));
        const string locator =
            ".ai-harness/outcome-verification/sha256-a/qa-context.json";
        var resolved = Path.GetFullPath(Path.Combine(workspace, locator));
        var errors = HostObservedQaEvidence.ValidatePassChecks(
            PassResult(new OutcomeQaCheck(
                OutcomeEvidenceKind.SourceInspection,
                locator,
                "The context says the criterion passed.",
                null)),
            [
                new ToolCallRecord(
                    "view",
                    HostObservedToolLocator.CreateLocatorSummary(resolved),
                    Succeeded: true,
                    ToolType: "Read",
                    NormalizedArguments:
                        $$"""{"path":"{{resolved.Replace("\\", "\\\\")}}"}""",
                    WorkingDirectory: workspace,
                    ResultDigest: Digest('e'),
                    ResultSummary:
                        """{"Status":"PASS","Rationale":"The context says the criterion passed."}""")
            ],
            workspace,
            ["."]);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void QaPass_RejectsClaimThatDoesNotMatchCapturedResult()
    {
        var workspace = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            $"qa-candidate-{Guid.NewGuid():N}"));
        const string command = "dotnet test .\\AiHarnessDemo.slnx";
        var errors = HostObservedQaEvidence.ValidatePassChecks(
            PassResult(new OutcomeQaCheck(
                OutcomeEvidenceKind.Test,
                command,
                "171 tests passed.",
                0)),
            [
                ObservedCommand(
                    workspace,
                    command,
                    "1 test passed.",
                    Digest('f'))
            ],
            workspace,
            ["."]);

        Assert.Contains(
            errors,
            error => error.Contains(
                "ObservedResult does not match",
                StringComparison.Ordinal));
    }

    [Fact]
    public void QaPass_AllowsOneLegitimateFullSuiteToCoverSeveralCriteria()
    {
        var workspace = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            $"qa-candidate-{Guid.NewGuid():N}"));
        const string command = "dotnet test .\\AiHarnessDemo.slnx";
        var check = new OutcomeQaCheck(
            OutcomeEvidenceKind.Test,
            command,
            "171 tests passed.",
            0);
        var result = new OutcomeQaResult(
            OutcomeVerificationRules.QaVersion,
            Digest('a'),
            Digest('b'),
            OutcomeQaVerdict.PASS,
            [
                PassingCriterion("AC-001", check),
                PassingCriterion("AC-002", check)
            ],
            []);

        var errors = HostObservedQaEvidence.ValidatePassChecks(
            result,
            [
                ObservedCommand(
                    workspace,
                    command,
                    "Test run complete: 171 tests passed.",
                    Digest('1'))
            ],
            workspace,
            ["."]);

        Assert.Empty(errors);
    }

    [Fact]
    public void QaPass_AllowsResultBoundSourceInspectionInsideCandidateRepository()
    {
        var workspace = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            $"qa-candidate-{Guid.NewGuid():N}"));
        const string locator = "src/App.tsx";
        var resolved = Path.GetFullPath(Path.Combine(workspace, locator));
        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        File.WriteAllText(
            resolved,
            """<button aria-label="Save">Save</button>""");
        var errors = HostObservedQaEvidence.ValidatePassChecks(
            PassResult(new OutcomeQaCheck(
                OutcomeEvidenceKind.SourceInspection,
                locator,
                """The source contains aria-label="Save".""",
                null)),
            [
                new ToolCallRecord(
                    "view",
                    HostObservedToolLocator.CreateLocatorSummary(resolved),
                    Succeeded: true,
                    ToolType: "Read",
                    NormalizedArguments:
                        $$"""{"path":"{{resolved.Replace("\\", "\\\\")}}"}""",
                    WorkingDirectory: workspace,
                    ResultDigest: Digest('2'),
                    ResultSummary:
                        """<button aria-label="Save">Save</button>""")
            ],
            workspace,
            ["."]);

        try
        {
            Assert.Empty(errors);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void QaPass_RejectsPatternMatchWhoseActualSearchPathIsOutsideCandidate()
    {
        var workspace = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            $"qa-candidate-{Guid.NewGuid():N}"));
        var outside = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            $"outside-{Guid.NewGuid():N}",
            "report.txt"));
        const string locator = "approved";
        var errors = HostObservedQaEvidence.ValidatePassChecks(
            PassResult(new OutcomeQaCheck(
                OutcomeEvidenceKind.SourceInspection,
                locator,
                "The candidate contains the approved state.",
                null)),
            [
                new ToolCallRecord(
                    "rg",
                    HostObservedToolLocator.CreateLocatorSummary(locator),
                    Succeeded: true,
                    ToolType: "Read",
                    NormalizedArguments: JsonSerializer.Serialize(new
                    {
                        pattern = locator,
                        path = outside
                    }),
                    WorkingDirectory: workspace,
                    ResultDigest: Digest('3'),
                    ResultSummary: "approved")
            ],
            workspace,
            ["."]);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void QaPass_RejectsContradictoryCapturedInspectionResult()
    {
        var workspace = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            $"qa-candidate-{Guid.NewGuid():N}"));
        const string locator = "src/App.tsx";
        var resolved = Path.GetFullPath(Path.Combine(workspace, locator));
        var errors = HostObservedQaEvidence.ValidatePassChecks(
            PassResult(new OutcomeQaCheck(
                OutcomeEvidenceKind.SourceInspection,
                locator,
                "Button text is Disabled.",
                null)),
            [
                new ToolCallRecord(
                    "view",
                    HostObservedToolLocator.CreateLocatorSummary(resolved),
                    Succeeded: true,
                    ToolType: "Read",
                    NormalizedArguments: JsonSerializer.Serialize(new
                    {
                        path = resolved
                    }),
                    WorkingDirectory: workspace,
                    ResultDigest: Digest('4'),
                    ResultSummary: "<button>Enabled</button>")
            ],
            workspace,
            ["."]);

        Assert.NotEmpty(errors);
    }

    [Theory]
    [InlineData("../outside/report.html")]
    [InlineData("C:/outside/report.html")]
    public void QaPass_RejectsHostReadsOutsideCandidateWorkspace(string locator)
    {
        var workspace = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "qa-candidate-workspace"));
        var resolved = Path.GetFullPath(
            Path.IsPathRooted(locator)
                ? locator
                : Path.Combine(workspace, locator));
        var result = PassResult(
            new OutcomeQaCheck(
                OutcomeEvidenceKind.SourceInspection,
                locator,
                "The unrelated file contained matching text.",
                null));

        var errors = HostObservedQaEvidence.ValidatePassChecks(
            result,
            [
                new ToolCallRecord(
                    "view",
                    HostObservedToolLocator.CreateLocatorSummary(resolved),
                    Succeeded: true)
            ],
            workspace);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void AcceptancePlan_ParsesStrictContractAndProducesStableHash()
    {
        var first = OutcomeVerificationRules.ParseAcceptancePlan(
            Acceptance("""
                {
                  "Version": "outcome-acceptance-v1",
                  "Criteria": [{
                    "Id": "AC-001",
                    "Requirement": "The release gate is PASS-only.",
                    "Verification": "Run the gate test and observe that failed QA creates no release gate.",
                    "OwnerRoles": ["software-engineer"],
                    "EvidenceKinds": ["Command", "Test"],
                    "CustomerVisible": true
                  }]
                }
                """),
            PlannedRoles);
        var second = OutcomeVerificationRules.ParseAcceptancePlan(
            Acceptance("""
                {"Criteria":[{"CustomerVisible":true,"EvidenceKinds":["Test","Command"],"OwnerRoles":["software-engineer"],"Verification":"Run the gate test and observe that failed QA creates no release gate.","Requirement":"The release gate is PASS-only.","Id":"AC-001"}],"Version":"outcome-acceptance-v1"}
                """),
            PlannedRoles);

        Assert.Equal(
            OutcomeVerificationRules.HashAcceptancePlan(first),
            OutcomeVerificationRules.HashAcceptancePlan(second));
        Assert.Equal(
            [OutcomeEvidenceKind.Test, OutcomeEvidenceKind.Command],
            first.Criteria[0].EvidenceKinds);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("fenced")]
    public void AcceptancePlan_RejectsInvalidMarkerPlacement(string caseName)
    {
        const string json = """
            {"Version":"outcome-acceptance-v1","Criteria":[{"Id":"AC-001","Requirement":"The behavior works.","Verification":"Run the focused test and observe a passing result.","OwnerRoles":["software-engineer"],"EvidenceKinds":["Test"],"CustomerVisible":true}]}
            """;
        var output = caseName switch
        {
            "missing" => json,
            "duplicate" =>
                $"{OutcomeVerificationRules.AcceptanceBeginMarker}\n" +
                $"{OutcomeVerificationRules.AcceptanceBeginMarker}\n{json}\n" +
                OutcomeVerificationRules.AcceptanceEndMarker,
            "fenced" =>
                $"```json\n{OutcomeVerificationRules.AcceptanceBeginMarker}\n{json}\n" +
                $"{OutcomeVerificationRules.AcceptanceEndMarker}\n```",
            _ => throw new ArgumentOutOfRangeException(nameof(caseName))
        };

        Assert.Throws<OutcomeVerificationValidationException>(() =>
            OutcomeVerificationRules.ParseAcceptancePlan(output, PlannedRoles));
    }

    [Fact]
    public void AcceptancePlan_FourCharacterFenceIsNotClosedByThreeCharacters()
    {
        var output = $$"""
            ````text
            ```
            {{OutcomeVerificationRules.AcceptanceBeginMarker}}
            {"Version":"outcome-acceptance-v1","Criteria":[{"Id":"AC-001","Requirement":"The behavior works.","Verification":"Run the focused test and observe a passing result.","OwnerRoles":["software-engineer"],"EvidenceKinds":["Test"],"CustomerVisible":true}]}
            {{OutcomeVerificationRules.AcceptanceEndMarker}}
            ```
            ````
            """;

        var exception = Assert.Throws<OutcomeVerificationValidationException>(() =>
            OutcomeVerificationRules.ParseAcceptancePlan(output, PlannedRoles));

        Assert.Contains("outside Markdown fences", exception.Message);
    }

    [Fact]
    public void AcceptancePlan_RejectsMissingRequiredBoolean()
    {
        var exception = Assert.Throws<OutcomeVerificationValidationException>(() =>
            OutcomeVerificationRules.ParseAcceptancePlan(
                Acceptance("""
                    {"Version":"outcome-acceptance-v1","Criteria":[{"Id":"AC-001","Requirement":"The behavior works.","Verification":"Run the behavior test and observe its result.","OwnerRoles":["software-engineer"],"EvidenceKinds":["Test"]}]}
                    """),
                PlannedRoles));

        Assert.Contains("$.Criteria[0].CustomerVisible is required", exception.Message);
    }

    [Theory]
    [InlineData(
        """{"Version":"outcome-acceptance-v1","Criteria":[],"Unexpected":true}""",
        "strict")]
    [InlineData(
        """{"Version":"Outcome-Acceptance-V1","Criteria":[]}""",
        "version")]
    [InlineData(
        """{"Version":"outcome-acceptance-v1","Criteria":[{"Id":"AC-002","Requirement":"Works.","Verification":"Run the behavior test.","OwnerRoles":["software-engineer"],"EvidenceKinds":["Test"],"CustomerVisible":true}]}""",
        "AC-001")]
    [InlineData(
        """{"Version":"outcome-acceptance-v1","Criteria":[{"Id":"AC-001","Requirement":"Works.","Verification":"Run the behavior test.","OwnerRoles":["quality-engineer"],"EvidenceKinds":["Test"],"CustomerVisible":true}]}""",
        "quality-engineer")]
    [InlineData(
        """{"Version":"outcome-acceptance-v1","Criteria":[{"Id":"AC-001","Requirement":"Business behavior works.","Verification":"Run the behavior test.","OwnerRoles":["release-engineer"],"EvidenceKinds":["Test"],"CustomerVisible":true}]}""",
        "release-engineer")]
    [InlineData(
        """{"Version":"outcome-acceptance-v1","Criteria":[{"Id":"AC-001","Requirement":"The file is produced.","Verification":"Check that the file exists.","OwnerRoles":["software-engineer"],"EvidenceKinds":["Artifact"],"CustomerVisible":true}]}""",
        "observable")]
    public void AcceptancePlan_RejectsInvalidDocuments(string json, string message)
    {
        var exception = Assert.Throws<OutcomeVerificationValidationException>(() =>
            OutcomeVerificationRules.ParseAcceptancePlan(
                Acceptance(json),
                PlannedRoles));

        Assert.Contains(
            message,
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ls dist/report.html")]
    [InlineData("Open report.html")]
    [InlineData("Get-Item dist/report.html")]
    [InlineData("Check that report.html exists.")]
    public void AcceptancePlan_RejectsListingOpeningAndExistenceOnlyVerification(
        string verification)
    {
        var exception = Assert.Throws<OutcomeVerificationValidationException>(() =>
            OutcomeVerificationRules.ParseAcceptancePlan(
                Acceptance($$"""
                    {"Version":"outcome-acceptance-v1","Criteria":[{"Id":"AC-001","Requirement":"The report is customer-ready.","Verification":"{{verification}}","OwnerRoles":["software-engineer"],"EvidenceKinds":["Command"],"CustomerVisible":true}]}
                    """),
                PlannedRoles));

        Assert.Contains("substantive content or behavior", exception.Message);
    }

    [Fact]
    public void AcceptancePlan_AcceptsVerificationThatNamesConcreteContent()
    {
        var plan = OutcomeVerificationRules.ParseAcceptancePlan(
            Acceptance("""
                {"Version":"outcome-acceptance-v1","Criteria":[{"Id":"AC-001","Requirement":"The report page is customer-ready.","Verification":"Open report.html and confirm the page heading is \"Quarterly report\" and the CTA text shows \"Start verification\".","OwnerRoles":["software-engineer"],"EvidenceKinds":["Command"],"CustomerVisible":true}]}
                """),
            PlannedRoles);

        Assert.Equal(
            "Open report.html and confirm the page heading is \"Quarterly report\" and the CTA text shows \"Start verification\".",
            Assert.Single(plan.Criteria).Verification);
    }

    [Fact]
    public void AcceptancePlan_RejectsOversizedJson()
    {
        var requirement = new string('x', OutcomeVerificationRules.MaximumAcceptanceBytes);
        var output = Acceptance(
            $$"""
              {"Version":"outcome-acceptance-v1","Criteria":[{"Id":"AC-001","Requirement":"{{requirement}}","Verification":"Run a test.","OwnerRoles":["software-engineer"],"EvidenceKinds":["Test"],"CustomerVisible":true}]}
              """);

        var exception = Assert.Throws<OutcomeVerificationValidationException>(() =>
            OutcomeVerificationRules.ParseAcceptancePlan(output, PlannedRoles));

        Assert.Contains("byte limit", exception.Message);
    }

    [Fact]
    public void DeliveryEvidence_InjectsRuntimeMetadataAndRejectsUnownedCriteria()
    {
        var plan = Snapshot();
        var stepId = Guid.NewGuid();
        var valid = OutcomeVerificationRules.ParseDeliveryEvidence(
            Evidence("""
                {"Version":"outcome-evidence-v1","Items":[{"CriterionId":"AC-001","Disposition":"Supports","Kind":"Test","Locator":"dotnet test .\\AiHarnessDemo.slnx","ObservedResult":"171 tests passed","ExitCode":0,"ContentDigest":null}]}
                """),
            plan,
            "software-engineer",
            stepId,
            DateTimeOffset.UnixEpoch);

        var item = Assert.Single(valid);
        Assert.StartsWith("E-", item.EvidenceId, StringComparison.Ordinal);
        Assert.Equal(stepId, item.ProducerStepId);
        Assert.Equal(DateTimeOffset.UnixEpoch, item.ProducedAt);

        var exception = Assert.Throws<OutcomeVerificationValidationException>(() =>
            OutcomeVerificationRules.ParseDeliveryEvidence(
                Evidence("""
                    {"Version":"outcome-evidence-v1","Items":[{"CriterionId":"AC-001","Disposition":"Supports","Kind":"Test","Locator":"dotnet test","ObservedResult":"Passed","ExitCode":0,"ContentDigest":null}]}
                    """),
                plan,
                "technical-writer",
                Guid.NewGuid()));
        Assert.Contains("not assigned", exception.Message);
    }

    [Fact]
    public void DeliveryEvidence_RejectsUnknownCriterionAndBadDigestCasing()
    {
        var exception = Assert.Throws<OutcomeVerificationValidationException>(() =>
            OutcomeVerificationRules.ParseDeliveryEvidence(
                Evidence("""
                    {"Version":"outcome-evidence-v1","Items":[{"CriterionId":"AC-999","Disposition":"Supports","Kind":"Test","Locator":"dotnet test","ObservedResult":"Passed","ExitCode":0,"ContentDigest":"sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}]}
                    """),
                Snapshot(),
                "software-engineer",
                Guid.NewGuid()));

        Assert.Contains("unknown criterion", exception.Message);
        Assert.Contains("lowercase sha256", exception.Message);
    }

    [Theory]
    [InlineData(
        """{"Version":"outcome-evidence-v1","Items":[{"CriterionId":"AC-001","Kind":"Test","Locator":"dotnet test","ObservedResult":"Passed","ContentDigest":null}]}""",
        "Disposition")]
    [InlineData(
        """{"Version":"outcome-evidence-v1","Items":[{"CriterionId":"AC-001","Disposition":"Supports","Locator":"dotnet test","ObservedResult":"Passed","ContentDigest":null}]}""",
        "Kind")]
    [InlineData(
        """{"Version":"outcome-evidence-v1","Items":[{"CriterionId":"AC-001","Disposition":"Supports","Kind":"Test","Locator":"dotnet test","ObservedResult":"Passed"}]}""",
        "ContentDigest")]
    public void DeliveryEvidence_RejectsMissingRequiredProperties(
        string json,
        string property)
    {
        var exception = Assert.Throws<OutcomeVerificationValidationException>(() =>
            OutcomeVerificationRules.ParseDeliveryEvidence(
                Evidence(json),
                Snapshot(),
                "software-engineer",
                Guid.NewGuid()));

        Assert.Contains($"{property} is required", exception.Message);
    }

    [Fact]
    public void QaResult_RequiresEveryCriterionAndRuntimeDerivedVerdict()
    {
        var plan = Snapshot();
        var fingerprint = Digest('b');
        var missing = Assert.Throws<OutcomeVerificationValidationException>(() =>
            OutcomeVerificationRules.ParseQaResult(
                Qa($$"""
                    {"Version":"outcome-qa-v1","AcceptancePlanHash":"{{plan.Hash}}","CandidateFingerprint":"{{fingerprint}}","Verdict":"PASS","Criteria":[],"PlanGaps":[]}
                    """),
                plan,
                fingerprint));
        Assert.Contains("every criterion", missing.Message);

        var contradictory = Assert.Throws<OutcomeVerificationValidationException>(() =>
            OutcomeVerificationRules.ParseQaResult(
                Qa($$"""
                    {"Version":"outcome-qa-v1","AcceptancePlanHash":"{{plan.Hash}}","CandidateFingerprint":"{{fingerprint}}","Verdict":"PASS","Criteria":[{"CriterionId":"AC-001","Status":"FAIL","EvidenceIds":[],"ChecksPerformed":[{"Kind":"Command","Locator":"dotnet test","ObservedResult":"One test failed","ExitCode":1}],"Rationale":"The gate is incorrect.","ResponsibleRoles":["software-engineer"],"Remediation":"Correct the gate."}],"PlanGaps":[]}
                    """),
                plan,
                fingerprint));
        Assert.Contains("contradicts", contradictory.Message);
    }

    [Fact]
    public void QaResult_RejectsMismatchedHashesAndPlanGapForcesFail()
    {
        var plan = Snapshot();
        var fingerprint = Digest('b');
        var mismatch = Assert.Throws<OutcomeVerificationValidationException>(() =>
            OutcomeVerificationRules.ParseQaResult(
                Qa($$"""
                    {"Version":"outcome-qa-v1","AcceptancePlanHash":"{{Digest('c')}}","CandidateFingerprint":"{{fingerprint}}","Verdict":"PASS","Criteria":[{"CriterionId":"AC-001","Status":"PASS","EvidenceIds":[],"ChecksPerformed":[{"Kind":"Test","Locator":"dotnet test","ObservedResult":"All tests passed","ExitCode":0}],"Rationale":"The observed test proves the outcome.","ResponsibleRoles":[],"Remediation":null}],"PlanGaps":[]}
                    """),
                plan,
                fingerprint));
        Assert.Contains("acceptancePlanHash", mismatch.Message);

        var result = OutcomeVerificationRules.ParseQaResult(
            Qa($$"""
                {"Version":"outcome-qa-v1","AcceptancePlanHash":"{{plan.Hash}}","CandidateFingerprint":"{{fingerprint}}","Verdict":"FAIL","Criteria":[{"CriterionId":"AC-001","Status":"PASS","EvidenceIds":[],"ChecksPerformed":[{"Kind":"Test","Locator":"dotnet test --filter ReleaseGate_WhenQaFails_DoesNotCreateCustomerGate","ObservedResult":"ReleaseGate_WhenQaFails_DoesNotCreateCustomerGate passed and asserted that failed QA creates no release gate.","ExitCode":0}],"Rationale":"The focused test proved the release-gate behavior.","ResponsibleRoles":[],"Remediation":null}],"PlanGaps":[{"Requirement":"A second outcome is missing.","Verification":"Run its focused behavior test and observe that the missing outcome renders the required customer state.","OwnerRoles":["software-engineer"],"Rationale":"The confirmed brief requires this outcome."}]}
                """),
            plan,
            fingerprint,
            allowedOwnerRoles: PlannedRoles);

        Assert.Equal(OutcomeQaVerdict.FAIL, result.Verdict);
        Assert.Single(result.PlanGaps);
    }

    [Fact]
    public void QaResult_RejectsMissingRequiredEnumsAtEveryObjectLevel()
    {
        var plan = Snapshot();
        var fingerprint = Digest('b');
        var documents = new[]
        {
            $$"""
              {"Version":"outcome-qa-v1","AcceptancePlanHash":"{{plan.Hash}}","CandidateFingerprint":"{{fingerprint}}","Criteria":[{"CriterionId":"AC-001","Status":"PASS","EvidenceIds":[],"ChecksPerformed":[{"Kind":"Test","Locator":"dotnet test","ObservedResult":"Passed"}],"Rationale":"Observed.","ResponsibleRoles":[]}],"PlanGaps":[]}
              """,
            $$"""
              {"Version":"outcome-qa-v1","AcceptancePlanHash":"{{plan.Hash}}","CandidateFingerprint":"{{fingerprint}}","Verdict":"PASS","Criteria":[{"CriterionId":"AC-001","EvidenceIds":[],"ChecksPerformed":[{"Kind":"Test","Locator":"dotnet test","ObservedResult":"Passed"}],"Rationale":"Observed.","ResponsibleRoles":[]}],"PlanGaps":[]}
              """,
            $$"""
              {"Version":"outcome-qa-v1","AcceptancePlanHash":"{{plan.Hash}}","CandidateFingerprint":"{{fingerprint}}","Verdict":"PASS","Criteria":[{"CriterionId":"AC-001","Status":"PASS","EvidenceIds":[],"ChecksPerformed":[{"Locator":"dotnet test","ObservedResult":"Passed"}],"Rationale":"Observed.","ResponsibleRoles":[]}],"PlanGaps":[]}
              """
        };

        Assert.All(documents, json =>
        {
            var exception = Assert.Throws<OutcomeVerificationValidationException>(() =>
                OutcomeVerificationRules.ParseQaResult(
                    Qa(json),
                    plan,
                    fingerprint));
            Assert.Contains("is required", exception.Message);
        });
    }

    [Fact]
    public void OptionalContractProperties_RemainOptional()
    {
        var plan = Snapshot();
        var fingerprint = Digest('b');
        var evidence = OutcomeVerificationRules.ParseDeliveryEvidence(
            Evidence("""
                {"Version":"outcome-evidence-v1","Items":[{"CriterionId":"AC-001","Disposition":"Supports","Kind":"Test","Locator":"dotnet test","ObservedResult":"Passed","ContentDigest":null}]}
                """),
            plan,
            "software-engineer",
            Guid.NewGuid());
        var qa = OutcomeVerificationRules.ParseQaResult(
            Qa($$"""
                {"Version":"outcome-qa-v1","AcceptancePlanHash":"{{plan.Hash}}","CandidateFingerprint":"{{fingerprint}}","Verdict":"PASS","Criteria":[{"CriterionId":"AC-001","Status":"PASS","EvidenceIds":[],"ChecksPerformed":[{"Kind":"Test","Locator":"dotnet test","ObservedResult":"All tests passed"}],"Rationale":"The focused behavior was observed.","ResponsibleRoles":[]}]}
                """),
            plan,
            fingerprint);

        Assert.Null(Assert.Single(evidence).ExitCode);
        Assert.Empty(qa.PlanGaps);
        Assert.Null(Assert.Single(qa.Criteria).Remediation);
        Assert.Null(Assert.Single(qa.Criteria[0].ChecksPerformed).ExitCode);
    }

    [Theory]
    [InlineData("Artifact", "dist/app.zip", "The artifact is present.")]
    [InlineData("Artifact", "dist/report.html", "Located report.html")]
    [InlineData("Command", "Test-Path dist/app.zip", "The file exists.")]
    [InlineData("Command", "test -f dist/app.zip", "The file existence result was true.")]
    [InlineData("Command", "Get-Item dist/report.html", "Found report.html")]
    [InlineData("Command", "ls dist/report.html", "Located report.html")]
    [InlineData("Command", "dir dist", "Listed report.html")]
    [InlineData("Command", "stat dist/report.html", "report.html exists")]
    [InlineData("Command", "Open report.html", "Opened report.html")]
    [InlineData("Artifact", "dist/app.zip", "The output file exists.")]
    public void QaResult_RejectsPassBasedOnlyOnFileExistence(
        string kind,
        string locator,
        string observedResult)
    {
        var plan = Snapshot();
        var fingerprint = Digest('b');
        var exception = Assert.Throws<OutcomeVerificationValidationException>(() =>
            OutcomeVerificationRules.ParseQaResult(
                Qa($$"""
                    {"Version":"outcome-qa-v1","AcceptancePlanHash":"{{plan.Hash}}","CandidateFingerprint":"{{fingerprint}}","Verdict":"PASS","Criteria":[{"CriterionId":"AC-001","Status":"PASS","EvidenceIds":[],"ChecksPerformed":[{"Kind":"{{kind}}","Locator":"{{locator}}","ObservedResult":"{{observedResult}}"}],"Rationale":"The artifact was located.","ResponsibleRoles":[]}],"PlanGaps":[]}
                    """),
                plan,
                fingerprint));

        Assert.Contains("substantive content or behavior", exception.Message);
    }

    [Fact]
    public void QaResult_AcceptsPassWhenCheckInspectsConcreteContent()
    {
        var plan = Snapshot();
        var fingerprint = Digest('b');

        var result = OutcomeVerificationRules.ParseQaResult(
            Qa($$"""
                {"Version":"outcome-qa-v1","AcceptancePlanHash":"{{plan.Hash}}","CandidateFingerprint":"{{fingerprint}}","Verdict":"PASS","Criteria":[{"CriterionId":"AC-001","Status":"PASS","EvidenceIds":[],"ChecksPerformed":[{"Kind":"Command","Locator":"Select-String -Path dist/report.html -Pattern '<title>Quarterly report</title>'","ObservedResult":"Matched exactly one title element with Quarterly report.","ExitCode":0}],"Rationale":"The QA check inspected the generated HTML content directly.","ResponsibleRoles":[]}],"PlanGaps":[]}
                """),
            plan,
            fingerprint);

        Assert.Equal(OutcomeQaVerdict.PASS, result.Verdict);
    }

    [Fact]
    public void QaResult_AcceptsExistencePrecheckFollowedByConcreteContentInspection()
    {
        var plan = Snapshot();
        var fingerprint = Digest('b');

        var result = OutcomeVerificationRules.ParseQaResult(
            Qa($$"""
                {"Version":"outcome-qa-v1","AcceptancePlanHash":"{{plan.Hash}}","CandidateFingerprint":"{{fingerprint}}","Verdict":"PASS","Criteria":[{"CriterionId":"AC-001","Status":"PASS","EvidenceIds":[],"ChecksPerformed":[{"Kind":"Command","Locator":"Test-Path dist/report.html; Get-Content dist/report.html | Select-String '<title>Quarterly report</title>'","ObservedResult":"Matched exactly one title element with Quarterly report.","ExitCode":0}],"Rationale":"The command first located the artifact and then inspected its concrete HTML content.","ResponsibleRoles":[]}],"PlanGaps":[]}
                """),
            plan,
            fingerprint);

        Assert.Equal(OutcomeQaVerdict.PASS, result.Verdict);
    }

    [Theory]
    [InlineData(
        """{"Version":"outcome-qa-v1","AcceptancePlanHash":"__PLAN_HASH__","CandidateFingerprint":"__FINGERPRINT__","Verdict":"PASS","Criteria":[null],"PlanGaps":[]}""",
        "criterion result 1 is null")]
    [InlineData(
        """{"Version":"outcome-qa-v1","AcceptancePlanHash":"__PLAN_HASH__","CandidateFingerprint":"__FINGERPRINT__","Verdict":"FAIL","Criteria":[{"CriterionId":"AC-001","Status":"PASS","EvidenceIds":[],"ChecksPerformed":[null],"Rationale":"The document is malformed.","ResponsibleRoles":[]}],"PlanGaps":[null]}""",
        "check 1 is null")]
    public void QaResult_NestedNullElementsYieldValidationException(
        string jsonTemplate,
        string expectedMessage)
    {
        var plan = Snapshot();
        var fingerprint = Digest('b');
        var json = jsonTemplate
            .Replace("__PLAN_HASH__", plan.Hash, StringComparison.Ordinal)
            .Replace("__FINGERPRINT__", fingerprint, StringComparison.Ordinal);

        var exception = Assert.Throws<OutcomeVerificationValidationException>(() =>
            OutcomeVerificationRules.ParseQaResult(
                Qa(json),
                plan,
                fingerprint));

        Assert.Contains(expectedMessage, exception.Message);
        if (json.Contains("\"PlanGaps\":[null]", StringComparison.Ordinal))
        {
            Assert.Contains("plan gap 1 is null", exception.Message);
        }
    }

    [Fact]
    public void Aggregate_RoundTripsCanonicallyAndRejectsCorruptOrOversizedState()
    {
        var state = OutcomeVerificationRules.CreateInitialState(1, 3);
        state.Status = OutcomeVerificationStatus.CollectingEvidence;
        state.PlannedRoles = PlannedRoles.ToList();
        state.AcceptancePlan = Snapshot();
        var json = OutcomeVerificationRules.SerializeAggregate(state);

        var restored = OutcomeVerificationRules.DeserializeAggregate(json);

        Assert.Equal(state.AcceptancePlan.Hash, restored.AcceptancePlan?.Hash);
        Assert.Equal(json, OutcomeVerificationRules.SerializeAggregate(restored));
        Assert.Throws<OutcomeVerificationValidationException>(() =>
            OutcomeVerificationRules.DeserializeAggregate(
                json.Replace(
                    OutcomeVerificationRules.AggregateVersion,
                    "outcome-verification-state-v2",
                    StringComparison.Ordinal)));

        state.Rounds.Add(new OutcomeQaRound
        {
            Round = 1,
            QaStepId = Guid.NewGuid(),
            AcceptancePlanHash = state.AcceptancePlan.Hash,
            CandidateFingerprint = Digest('d'),
            ContractError = new string('x', OutcomeVerificationRules.MaximumAggregateBytes)
        });
        Assert.Throws<OutcomeVerificationValidationException>(() =>
            OutcomeVerificationRules.SerializeAggregate(state));
    }

    [Fact]
    public void Aggregate_PassedRequiresCurrentTerminalQaProof()
    {
        var state = ValidPassedState(iteration: 1, maxRounds: 3);
        state.Rounds.Clear();

        var exception = Assert.Throws<OutcomeVerificationValidationException>(() =>
            OutcomeVerificationRules.SerializeAggregate(state));

        Assert.Contains(
            "completed QA round",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Aggregate_RecursivelyRejectsCorruptArchivedIteration()
    {
        var next = OutcomeVerificationRules.StartNextIteration(
            ValidPassedState(iteration: 1, maxRounds: 3),
            nextIteration: 2,
            maxRounds: 4);
        next.PriorIterations[0] = next.PriorIterations[0] with
        {
            MaxRounds = 999
        };

        var exception = Assert.Throws<OutcomeVerificationValidationException>(() =>
            OutcomeVerificationRules.SerializeAggregate(next));

        Assert.Contains("prior iteration 1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("maxRounds", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Aggregate_RoundTripsLegitimateMultiIterationProofHistory()
    {
        var second = OutcomeVerificationRules.StartNextIteration(
            ValidPassedState(iteration: 1, maxRounds: 3),
            nextIteration: 2,
            maxRounds: 4);
        var secondPassed = ValidPassedState(iteration: 2, maxRounds: 4);
        secondPassed.PriorIterations = second.PriorIterations;
        var third = OutcomeVerificationRules.StartNextIteration(
            secondPassed,
            nextIteration: 3,
            maxRounds: 5);

        var json = OutcomeVerificationRules.SerializeAggregate(third);
        var restored = OutcomeVerificationRules.DeserializeAggregate(json);

        Assert.Equal(3, restored.Iteration);
        Assert.Equal(
            [1, 2],
            restored.PriorIterations.Select(item => item.Iteration));
        Assert.All(
            restored.PriorIterations,
            archive => Assert.Equal(
                OutcomeVerificationStatus.Superseded,
                archive.Status));
    }

    [Fact]
    public void FlowDto_LabelsEmptyStateAsLegacyAndProjectsCurrentState()
    {
        var legacy = new FlowRun
        {
            Title = "Legacy",
            OriginalRequest = "Old request"
        };
        Assert.True(legacy.ToOutcomeVerificationDto().LegacyUnverified);

        var state = OutcomeVerificationRules.CreateInitialState(1, 3);
        state.Status = OutcomeVerificationStatus.CollectingEvidence;
        state.PlannedRoles = PlannedRoles.ToList();
        state.AcceptancePlan = Snapshot();
        legacy.OutcomeVerificationJson =
            OutcomeVerificationRules.SerializeAggregate(state);

        var dto = legacy.ToOutcomeVerificationDto();

        Assert.False(dto.LegacyUnverified);
        Assert.Equal("CollectingEvidence", dto.Status);
        Assert.Equal("AC-001", Assert.Single(dto.Criteria).Id);
    }

    [Fact]
    public void Aggregate_RejectsUnknownPropertiesAndUnknownVersionWithoutReset()
    {
        var state = OutcomeVerificationRules.CreateInitialState(1, 3);
        var json = OutcomeVerificationRules.SerializeAggregate(state);

        Assert.Throws<OutcomeVerificationValidationException>(() =>
            OutcomeVerificationRules.DeserializeAggregate(
                json[..^1] + ",\"Unknown\":true}"));
        Assert.Throws<OutcomeVerificationValidationException>(() =>
            OutcomeVerificationRules.DeserializeAggregate(
                json.Replace(
                    OutcomeVerificationRules.AggregateVersion,
                    "outcome-verification-state-v999",
                    StringComparison.Ordinal)));
    }

    [Fact]
    public void Aggregate_RejectsMissingRequiredRootAndNestedProperties()
    {
        var state = OutcomeVerificationRules.CreateInitialState(1, 3);
        state.Status = OutcomeVerificationStatus.CollectingEvidence;
        state.PlannedRoles = PlannedRoles.ToList();
        state.AcceptancePlan = Snapshot();
        var json = OutcomeVerificationRules.SerializeAggregate(state);

        var missingStatus = json.Replace(
            "\"Status\":\"CollectingEvidence\",",
            string.Empty,
            StringComparison.Ordinal);
        var missingCustomerVisible = json.Replace(
            "\"CustomerVisible\":true,",
            string.Empty,
            StringComparison.Ordinal);

        Assert.Contains(
            "Status is required",
            Assert.Throws<OutcomeVerificationValidationException>(() =>
                OutcomeVerificationRules.DeserializeAggregate(missingStatus)).Message);
        Assert.Contains(
            "CustomerVisible is required",
            Assert.Throws<OutcomeVerificationValidationException>(() =>
                OutcomeVerificationRules.DeserializeAggregate(
                    missingCustomerVisible)).Message);
    }

    [Fact]
    public void PublicationJournalTransitions_AreMonotonicAcrossRecovery()
    {
        Assert.Equal(
            OutcomePublicationStatus.Published,
            VerifiedCandidatePublisher.AdvancePublicationStatus(
                OutcomePublicationStatus.Published,
                OutcomePublicationStatus.Publishing));
        Assert.Equal(
            OutcomePublicationStatus.Verified,
            VerifiedCandidatePublisher.AdvancePublicationStatus(
                OutcomePublicationStatus.Verified,
                OutcomePublicationStatus.Published));
        Assert.Equal(
            OutcomeRepositoryPublicationStatus.Published,
            VerifiedCandidatePublisher.AdvanceRepositoryStatus(
                OutcomeRepositoryPublicationStatus.Published,
                OutcomeRepositoryPublicationStatus.Publishing));
        Assert.Equal(
            "https://github.com/example/repository.git",
            VerifiedCandidatePublisher.BuildTrustedGitHubRemoteUrl(
                "example/repository"));
    }

    [Theory]
    [InlineData("Published pull request #42")]
    [InlineData("Published pull requests · 2 repositories")]
    public void PublishedPullRequestLabels_IncludeSingleAndMultiRepositoryForms(
        string label)
    {
        Assert.True(WorkflowEngine.IsPublishedPullRequestLabel(label));
        Assert.False(WorkflowEngine.IsPublishedPullRequestLabel("Pull request candidate"));
    }

    [Fact]
    public void TeamLeadContract_ValidatesProfilesPremortemAndAcceptanceTogether()
    {
        var output = """
            HANDOFF_STATUS: COMPLETE
            TEAM_TASK_PROFILES_V1_BEGIN
            {"Version":"task-profile-v1","Profiles":[{"Role":"software-engineer","Complexity":7,"ReasoningDepth":8,"ContextDemand":7,"ToolIntensity":8,"TaskTypeTags":["Implementation"],"Risk":"High","RiskReason":"The implementation changes a durable workflow.","Confidence":0.9,"Rationales":["Code and focused validation are required."]}]}
            TEAM_TASK_PROFILES_V1_END
            PRE_MORTEM_PLAN_V1_BEGIN
            {"Version":"pre-mortem-plan-v1","AfterRoles":[]}
            PRE_MORTEM_PLAN_V1_END
            OUTCOME_ACCEPTANCE_PLAN_V1_BEGIN
            {"Version":"outcome-acceptance-v1","Criteria":[{"Id":"AC-001","Requirement":"Failed QA cannot open customer review.","Verification":"Run the orchestration test and observe that no release gate is created.","OwnerRoles":["software-engineer"],"EvidenceKinds":["Test"],"CustomerVisible":true}]}
            OUTCOME_ACCEPTANCE_PLAN_V1_END
            """;

        var contract = WorkflowEngine.ParseTeamLeadContract(
            output,
            ["software-engineer"],
            Guid.NewGuid(),
            1,
            preMortemAvailable: false,
            outcomeVerificationEnabled: true);

        Assert.Single(contract.Profiles);
        Assert.NotNull(contract.AcceptancePlan);

        var exception = Assert.Throws<AiHarnessDemo.Core.Reasoning.TaskProfileValidationException>(
            () => WorkflowEngine.ParseTeamLeadContract(
                "invalid",
                ["software-engineer"],
                Guid.NewGuid(),
                1,
                preMortemAvailable: false,
                outcomeVerificationEnabled: true));
        Assert.Contains(exception.Errors, error => error.StartsWith("task profiles:", StringComparison.Ordinal));
        Assert.Contains(exception.Errors, error => error.StartsWith("pre-mortem plan:", StringComparison.Ordinal));
        Assert.Contains(exception.Errors, error => error.StartsWith("acceptance plan:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeliveryCompletion_RequiresEvidenceForEveryAssignedCriterion()
    {
        var state = OutcomeVerificationRules.CreateInitialState(1, 3);
        state.Status = OutcomeVerificationStatus.CollectingEvidence;
        state.PlannedRoles = PlannedRoles.ToList();
        state.AcceptancePlan = Snapshot();
        var flow = new FlowRun
        {
            Title = "Outcome",
            OriginalRequest = "Verify it",
            OutcomeVerificationJson =
                OutcomeVerificationRules.SerializeAggregate(state)
        };
        var step = new FlowStep
        {
            FlowRunId = flow.Id,
            Iteration = 1,
            Sequence = 20,
            AgentId = "software-engineer",
            AgentName = "Software Engineer",
            AgentRole = "software-engineer"
        };
        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        await using var database = new HarnessDbContext(options);

        Assert.Throws<OutcomeVerificationValidationException>(() =>
            WorkflowEngine.CollectOutcomeEvidence(
                database,
                flow,
                step,
                Evidence("""{"Version":"outcome-evidence-v1","Items":[]}"""),
                DateTimeOffset.UnixEpoch));

        WorkflowEngine.CollectOutcomeEvidence(
            database,
            flow,
            step,
            Evidence("""
                {"Version":"outcome-evidence-v1","Items":[{"CriterionId":"AC-001","Disposition":"Supports","Kind":"Test","Locator":"dotnet test","ObservedResult":"Focused test passed","ExitCode":0,"ContentDigest":null}]}
                """),
            DateTimeOffset.UnixEpoch);

        var stored = OutcomeVerificationRules.DeserializeAggregate(
            flow.OutcomeVerificationJson);
        Assert.Equal("AC-001", Assert.Single(stored.Evidence).CriterionId);
        Assert.Contains(
            database.ChangeTracker.Entries<FlowEvent>(),
            entry => entry.Entity.Type == "outcome.evidence.collected");
    }

    private static OutcomeQaResult PassResult(OutcomeQaCheck check) =>
        new(
            OutcomeVerificationRules.QaVersion,
            Digest('a'),
            Digest('b'),
            OutcomeQaVerdict.PASS,
            [
                new OutcomeQaCriterionResult(
                    "AC-001",
                    OutcomeCriterionStatus.PASS,
                    [],
                    [check],
                    "The host-observed check proves the criterion.",
                    [],
                    null)
            ],
            []);

    private static OutcomeQaCriterionResult PassingCriterion(
        string id,
        OutcomeQaCheck check) =>
        new(
            id,
            OutcomeCriterionStatus.PASS,
            [],
            [check],
            "The host-observed check proves the criterion.",
            [],
            null);

    private static ToolCallRecord ObservedCommand(
        string workspace,
        string command,
        string result,
        string digest) =>
        new(
            "powershell",
            HostObservedToolLocator.CreateCommandSummary(command),
            Succeeded: true,
            ToolType: "Command",
            NormalizedCommand: HostObservedToolLocator.Normalize(command),
            NormalizedArguments:
                JsonSerializer.Serialize(new { command }),
            WorkingDirectory: workspace,
            ExitCode: 0,
            ResultDigest: digest,
            ResultSummary: result);

    private static OutcomeAcceptancePlanSnapshot Snapshot()
    {
        var plan = OutcomeVerificationRules.ParseAcceptancePlan(
            Acceptance("""
                {"Version":"outcome-acceptance-v1","Criteria":[{"Id":"AC-001","Requirement":"The release gate is PASS-only.","Verification":"Run the gate test and observe that failed QA creates no release gate.","OwnerRoles":["software-engineer"],"EvidenceKinds":["Test","Command"],"CustomerVisible":true}]}
                """),
            PlannedRoles);
        return OutcomeVerificationRules.CreateAcceptanceSnapshot(plan, Guid.NewGuid());
    }

    private static OutcomeVerificationState ValidPassedState(
        int iteration,
        int maxRounds)
    {
        var plan = Snapshot();
        var manifest = new CandidateManifest(
            OutcomeVerificationRules.CandidateManifestVersion,
            iteration,
            plan.Hash,
            [
                new CandidateRepositoryManifest(
                    ".",
                    new string('1', 40),
                    new string('2', 40))
            ],
            [],
            []);
        var fingerprint = OutcomeVerificationRules.HashCandidateManifest(manifest);
        var preparedBy = Guid.NewGuid();
        var qaStep = Guid.NewGuid();
        var completedAt = DateTimeOffset.UtcNow;
        var result = new OutcomeQaResult(
            OutcomeVerificationRules.QaVersion,
            plan.Hash,
            fingerprint,
            OutcomeQaVerdict.PASS,
            [
                new OutcomeQaCriterionResult(
                    "AC-001",
                    OutcomeCriterionStatus.PASS,
                    [],
                    [
                        new OutcomeQaCheck(
                            OutcomeEvidenceKind.Test,
                            "dotnet test",
                            "The focused regression passed.",
                            0)
                    ],
                    "The focused regression proved the outcome.",
                    [],
                    null)
            ],
            []);
        var state = OutcomeVerificationRules.CreateInitialState(
            iteration,
            maxRounds,
            completedAt);
        state.Status = OutcomeVerificationStatus.Passed;
        state.TrustedRepositories = [new OutcomeTrustedRepository(".", string.Empty)];
        state.PlannedRoles = PlannedRoles.ToList();
        state.AcceptancePlan = plan;
        state.CurrentCandidate = new OutcomeCandidateSnapshot(
            manifest,
            fingerprint,
            preparedBy,
            completedAt.AddMinutes(-1));
        state.Rounds.Add(new OutcomeQaRound
        {
            Round = 1,
            QaStepId = qaStep,
            AcceptancePlanHash = plan.Hash,
            CandidateFingerprint = fingerprint,
            ContextHash = Digest('c'),
            Verdict = OutcomeQaVerdict.PASS,
            Result = result,
            CompletedAt = completedAt
        });
        state.VerifiedCandidateFingerprint = fingerprint;
        state.VerifiedAt = completedAt;
        return state;
    }

    private static string Acceptance(string json) =>
        $"{OutcomeVerificationRules.AcceptanceBeginMarker}\n{json}\n" +
        OutcomeVerificationRules.AcceptanceEndMarker;

    private static string Evidence(string json) =>
        $"{OutcomeVerificationRules.EvidenceBeginMarker}\n{json}\n" +
        OutcomeVerificationRules.EvidenceEndMarker;

    private static string Qa(string json) =>
        $"{OutcomeVerificationRules.QaBeginMarker}\n{json}\n" +
        OutcomeVerificationRules.QaEndMarker;

    private static string Digest(char value) => $"sha256:{new string(value, 64)}";
}

public sealed class OutcomeVerificationWorkflowTests
{
    [Theory]
    [InlineData("tracker")]
    [InlineData("workspace")]
    [InlineData("hooks")]
    [InlineData("agent")]
    [InlineData("copilot")]
    [InlineData("outcome_verification")]
    public void WorkflowConfig_RejectsExplicitlyNullTypedSections(string section)
    {
        using var invalid = WorkflowArtifact.Create($$"""
            ---
            {{section}}: null
            ---
            Prompt
            """);

        var exception = Assert.Throws<AiHarnessDemo.Core.Workflow.WorkflowConfigurationException>(
            () => new AiHarnessDemo.Core.Workflow.WorkflowLoader().Load(invalid.Path));

        Assert.Contains(section, exception.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be null", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkflowConfig_DefaultsAndValidatesOutcomeRoundLimit()
    {
        using var valid = WorkflowArtifact.Create("""
            ---
            workspace:
              root: .worktrees
            ---
            {{ outcome.context }}
            {{ outcome.contract }}
            """);
        var workflow = new AiHarnessDemo.Core.Workflow.WorkflowLoader().Load(valid.Path);
        Assert.True(workflow.Config.OutcomeVerification.Enabled);
        Assert.Equal(3, workflow.Config.OutcomeVerification.MaxRounds);

        using var invalid = WorkflowArtifact.Create("""
            ---
            outcome_verification:
              enabled: true
              max_rounds: 11
            ---
            Prompt
            """);
        var exception = Assert.Throws<AiHarnessDemo.Core.Workflow.WorkflowConfigurationException>(
            () => new AiHarnessDemo.Core.Workflow.WorkflowLoader().Load(invalid.Path));
        Assert.Contains("outcome_verification.max_rounds", exception.Message);
    }

    [Fact]
    public async Task FlowSchema_AddsOutcomeVerificationJsonWithoutFabricatingState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE Flows (
                    Id TEXT NOT NULL CONSTRAINT PK_Flows PRIMARY KEY,
                    ModelSelectionStrategy TEXT NOT NULL DEFAULT 'MaximumQuality'
                );
                INSERT INTO Flows (Id) VALUES ('legacy-flow');
                """;
            await command.ExecuteNonQueryAsync();
        }
        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var database = new HarnessDbContext(options);

        await DatabaseInitializer.EnsureFlowRunSchemaAsync(database);

        await using var probe = connection.CreateCommand();
        probe.CommandText =
            "SELECT OutcomeVerificationJson FROM Flows WHERE Id = 'legacy-flow';";
        Assert.Equal(string.Empty, await probe.ExecuteScalarAsync());
    }

    [Fact]
    public async Task InvalidHotReloadRetainsPolicyAndValidReloadAffectsOnlyNewCycles()
    {
        using var artifact = WorkflowArtifact.Create(Workflow(3));
        var paths = new HarnessPaths(
            artifact.Directory,
            Path.Combine(artifact.Directory, ".github", "agents"),
            Path.Combine(artifact.Directory, "harness.db"));
        using var provider = new WorkflowDefinitionProvider(
            paths,
            new AiHarnessDemo.Core.Workflow.WorkflowLoader(),
            NullLogger<WorkflowDefinitionProvider>.Instance);
        await provider.StartAsync(CancellationToken.None);
        var active = OutcomeVerificationRules.CreateInitialState(
            1,
            provider.GetValidated().Config.OutcomeVerification.MaxRounds);

        File.WriteAllText(artifact.Path, Workflow(11));
        File.SetLastWriteTimeUtc(artifact.Path, DateTime.UtcNow.AddSeconds(2));
        Assert.Equal(
            3,
            provider.GetValidated().Config.OutcomeVerification.MaxRounds);
        Assert.NotNull(provider.Status().LastError);

        File.WriteAllText(artifact.Path, Workflow(5));
        File.SetLastWriteTimeUtc(artifact.Path, DateTime.UtcNow.AddSeconds(4));
        var reloaded = provider.GetValidated();

        Assert.Equal(5, reloaded.Config.OutcomeVerification.MaxRounds);
        Assert.Equal(3, active.MaxRounds);
    }

    [Fact]
    public async Task NullOutcomeVerificationReloadRetainsLastKnownGoodDefinition()
    {
        using var artifact = WorkflowArtifact.Create(Workflow(4));
        var paths = new HarnessPaths(
            artifact.Directory,
            Path.Combine(artifact.Directory, ".github", "agents"),
            Path.Combine(artifact.Directory, "harness.db"));
        using var provider = new WorkflowDefinitionProvider(
            paths,
            new AiHarnessDemo.Core.Workflow.WorkflowLoader(),
            NullLogger<WorkflowDefinitionProvider>.Instance);
        await provider.StartAsync(CancellationToken.None);
        var lastKnownGood = provider.GetValidated();

        File.WriteAllText(
            artifact.Path,
            """
            ---
            outcome_verification: null
            ---
            Prompt
            """);
        File.SetLastWriteTimeUtc(artifact.Path, DateTime.UtcNow.AddSeconds(2));

        var retained = provider.GetValidated();

        Assert.Same(lastKnownGood, retained);
        Assert.Equal(4, retained.Config.OutcomeVerification.MaxRounds);
        Assert.Contains(
            "outcome_verification",
            provider.Status().LastError,
            StringComparison.Ordinal);
    }

    private static string Workflow(int maxRounds) => $$"""
        ---
        workspace:
          root: .worktrees
        outcome_verification:
          enabled: true
          max_rounds: {{maxRounds}}
        ---
        Prompt
        """;

    private sealed class WorkflowArtifact : IDisposable
    {
        private WorkflowArtifact(string directory, string path)
        {
            Directory = directory;
            Path = path;
        }

        public string Directory { get; }

        public string Path { get; }

        public static WorkflowArtifact Create(string content)
        {
            var directory = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "outcome-workflow-tests",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "WORKFLOW.md");
            File.WriteAllText(path, content.ReplaceLineEndings(Environment.NewLine));
            return new WorkflowArtifact(directory, path);
        }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
                // Test cleanup only.
            }
        }
    }
}

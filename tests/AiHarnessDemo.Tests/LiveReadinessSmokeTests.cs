using System.Diagnostics;
using System.Text;
using AiHarnessDemo.Core.Verification;
using Xunit.Abstractions;

namespace AiHarnessDemo.Tests;

/// <summary>
/// A single opt-in, hard-bounded live Copilot CLI smoke for the Delivery readiness gate.
///
/// It runs at most one model call in an empty scratch directory with no repository, no Git, no
/// GitHub access, and no publication path, then feeds the raw agent output through the real host
/// parser and <see cref="DeliveryReadinessPolicy"/>. The assertion is that a live agent describing
/// the recorded incident's unresolved gaps cannot produce a releasable readiness state.
///
/// The test is skipped unless <c>AIHARNESS_LIVE_SMOKE=1</c> is set, so the deterministic suite
/// never launches a model.
/// </summary>
public sealed class LiveReadinessSmokeTests(ITestOutputHelper testOutput)
{
    private const string OptInVariable = "AIHARNESS_LIVE_SMOKE";
    private const int MaximumModelCalls = 1;
    private static readonly TimeSpan WallClockBudget = TimeSpan.FromMinutes(10);

    [Fact]
    public async Task LiveVerificationTurn_WithUnresolvedGaps_CannotDeriveAReleasableState()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(OptInVariable),
                "1",
                StringComparison.Ordinal))
        {
            // Deterministic runs never launch Copilot CLI.
            return;
        }

        var plan = IncidentAcceptancePlan();
        var planHash = DeliveryReadinessPolicy.HashAcceptancePlan(plan);
        var scratch = Path.Combine(
            Path.GetTempPath(),
            "aiharness-live-smoke",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        var calls = 0;
        string output;
        try
        {
            Assert.True(calls < MaximumModelCalls, "The live smoke budget allows one model call.");
            calls++;
            output = await RunCopilotAsync(scratch, Prompt(plan, planHash));
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (IOException)
            {
                // A transient handle must not fail the smoke.
            }
        }

        Assert.Equal(MaximumModelCalls, calls);
        Assert.False(
            string.IsNullOrWhiteSpace(output),
            "The live Copilot CLI turn produced no output.");

        // The host parses the live output with the same strict parser production uses.
        var parsed = DeliveryReadinessPolicy.ParseQaOutput(output, plan, planHash);
        var snapshot = DeliveryReadinessPolicy.Derive(
            new DeliveryReadinessDerivationInput(
                Guid.NewGuid(),
                1,
                1,
                plan,
                planHash,
                parsed.Document,
                parsed.ContractHash,
                Guid.NewGuid(),
                "quality-engineer",
                Guid.NewGuid(),
                OutcomeVerificationRules.ComputeSha256("live-outcome"),
                OutcomeVerificationRules.ComputeSha256("live-candidate"),
                [],
                [],
                [],
                [],
                DateTimeOffset.UtcNow,
                Guid.NewGuid()));

        // Whatever wording the live model chose, an unresolved gap can never be ReadyToApprove and
        // can therefore never open an ordinary customer review or authorize publication.
        testOutput.WriteLine(
            $"live readiness state = {snapshot.State}; " +
            $"verdict = {parsed.Document.Verdict}; " +
            "failed/blocked criteria = " +
            string.Join(
                ",",
                snapshot.Criteria
                    .Where(item => item.Outcome != DeliveryCriterionOutcome.Verified)
                    .Select(item => $"{item.CriterionId}:{item.Outcome}")) +
            "; risks = " +
            string.Join(
                ",",
                snapshot.Risks.Select(item => $"{item.RiskId}:{item.Classification}")));
        Assert.NotEqual(DeliveryReadinessState.ReadyToApprove, snapshot.State);
        Assert.Contains(
            snapshot.Criteria,
            criterion => criterion.Outcome is DeliveryCriterionOutcome.Failed
                or DeliveryCriterionOutcome.Blocked);
        Assert.DoesNotContain(
            DeliveryReadinessAction.Accept,
            DeliveryReadinessPolicy.AllowedActions(snapshot.State));
    }

    private static async Task<string> RunCopilotAsync(string workingDirectory, string prompt)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "copilot",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-C", workingDirectory,
                     "--add-dir", workingDirectory,
                     "--no-color",
                     "--no-ask-user",
                     "--disallow-temp-dir",
                     "--deny-tool=shell",
                     "--deny-tool=write",
                     "--deny-url=github.com",
                     "--deny-url=api.github.com",
                     "-p", prompt
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        using var budget = new CancellationTokenSource(WallClockBudget);
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                standardOutput.AppendLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                standardError.AppendLine(args.Data);
            }
        };
        Assert.True(process.Start(), "Copilot CLI could not be started.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(budget.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process already exited.
            }
            throw new TimeoutException(
                $"The live smoke exceeded its {WallClockBudget.TotalMinutes:0} minute budget.");
        }
        return standardOutput.Length > 0
            ? standardOutput.ToString()
            : standardError.ToString();
    }

    private static string Prompt(DeliveryAcceptancePlan plan, string planHash)
    {
        var criteria = string.Join(
            Environment.NewLine,
            plan.Criteria.Select(criterion =>
                $"- {criterion.Id}: {criterion.Requirement}"));
        return $"""
            You are acting as an independent Quality Engineer for a completed website redesign.
            Do not run any tool, read any file, or access the network. Answer only from the facts
            stated below.

            Planned acceptance criteria:
            {criteria}

            Verification facts established by an independent pre-mortem review, all confirmed as
            real defects in the delivered result:
            1. No documented or scripted command builds either brand into the folder the offline
               preview generator requires, so the delivered previews cannot be reproduced.
            2. The archive and seminar guards swallow every data-load failure, substitute an empty
               list, and mark the page initialized, so an outage renders as an ordinary empty
               result with no error or retry state.
            3. The YouTube section was only ever reviewed with an empty playlist, so the populated
               video layout has never been seen at any viewport.
            4. The bootstrap data-load failure screen is hard-coded in English regardless of brand.

            Every other criterion was independently confirmed to hold.

            Return exactly one strict JSON document and nothing else between a standalone
            {DeliveryReadinessPolicy.QaBeginMarker} line and a standalone
            {DeliveryReadinessPolicy.QaEndMarker} line. The JSON must have exactly these properties:
            "AcceptancePlanHash" (exactly "{planHash}"),
            "Verdict" (exactly "PASS", "FAIL", or "BLOCKED"),
            "Criteria" (one entry per planned criterion, each with "CriterionId",
            "Outcome" exactly "Verified", "Failed", or "Blocked", "EvidenceIds" (non-empty array of
            strings), "Rationale", "Remediation" (a string when the outcome is not Verified,
            otherwise null), and "ResponsibleRoles" (empty for Verified, at least one role for
            Failed, and empty only for an external Blocked outcome)),
            and "ResidualRisks" (an array, each entry with "RiskId" matching RR-000,
            "Classification" exactly "NonBlockingDisclosure", "WaiverRequired", or "Blocking",
            "Severity" exactly "Low", "Medium", "High", or "Critical", "Statement", "Impact",
            "EvidenceIds", "CriterionIds", and "PreMortemFindingId" (null or matching PM-000)),
            and "PlanGaps" (an array of omitted confirmed requirements, each with "Requirement",
            "Verification", "OwnerRoles", and "Rationale").

            Verdict must be BLOCKED when any criterion is Blocked or any risk is Blocking, PASS only
            when every criterion is Verified and PlanGaps is empty, and FAIL otherwise. Enum casing
            is exact.
            """;
    }

    private static DeliveryAcceptancePlan IncidentAcceptancePlan() =>
        new(
            [.. new[]
            {
                "Every page and shared section on both brands reflects the Nordic Tech Minimal look with the correct brand accent color.",
                "Archive/meeting empty, loading, partial, and error states are visually covered on both brands.",
                "All existing content, links, and current site behavior still work exactly as before.",
                "Both sites remain responsive and fully keyboard-usable.",
                "The customer receives self-contained offline preview versions of both sites to review before publishing.",
                "Existing checks and production builds continue to pass."
            }.Select((requirement, index) => new DeliveryAcceptanceCriterion
            {
                Id = $"AC-{index + 1:000}",
                Requirement = requirement,
                Verification =
                    "Open both brands in the offline preview and confirm the observable behavior.",
                OwnerRoles = ["software-engineer"],
                EvidenceKinds = [OutcomeEvidenceKind.Observation],
                CustomerVisible = true
            })]);
}

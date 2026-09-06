using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Data;
using AiHarnessDemo.Services;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Tests;

public sealed class OutcomeVerificationContextTests
{
    [Fact]
    public async Task Builder_WritesCanonicalDatabaseDerivedReadOnlyPacket()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "qa-context-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DbContextOptionsBuilder<HarnessDbContext>()
                .UseSqlite(
                    $"Data Source={Path.Combine(root, "test.db")};Pooling=False")
                .Options;
            await using (var database = new HarnessDbContext(options))
            {
                await database.Database.EnsureCreatedAsync();
                var plan = Plan();
                var manifest = new CandidateManifest(
                    OutcomeVerificationRules.CandidateManifestVersion,
                    1,
                    plan.Hash,
                    [
                        new CandidateRepositoryManifest(
                            ".",
                            new string('a', 40),
                            new string('b', 40))
                    ],
                    [],
                    [
                        new CandidatePreviewArtifact(
                            ".customer-preview/demo/browser/index.html",
                            10,
                            Digest('c'))
                    ]);
                var state = OutcomeVerificationRules.CreateInitialState(1, 3);
                state.Status = OutcomeVerificationStatus.AwaitingQa;
                state.TrustedRepositories =
                    [new OutcomeTrustedRepository(".", string.Empty)];
                state.PlannedRoles =
                    ["software-engineer", "quality-engineer", "release-engineer"];
                state.AcceptancePlan = plan;
                var evidenceStepId = Guid.NewGuid();
                state.Evidence.Add(new OutcomeEvidence(
                    "E-00000000-0000-0000-0000-000000000001",
                    "AC-001",
                    OutcomeEvidenceDisposition.Supports,
                    OutcomeEvidenceKind.Test,
                    "dotnet test",
                    "Tests passed",
                    0,
                    null,
                    "software-engineer",
                    evidenceStepId,
                    DateTimeOffset.UnixEpoch));
                state.EvidenceProcessing.Add(new OutcomeEvidenceProcessing(
                    evidenceStepId,
                    plan.Hash,
                    "software-engineer",
                    ["AC-001"],
                    DateTimeOffset.UnixEpoch));
                state.CurrentCandidate = new OutcomeCandidateSnapshot(
                    manifest,
                    OutcomeVerificationRules.HashCandidateManifest(manifest),
                    Guid.NewGuid(),
                    DateTimeOffset.UnixEpoch);
                var flow = new FlowRun
                {
                    Title = "QA context",
                    OriginalRequest = "Original request",
                    ConsolidatedRequest = "Confirmed brief",
                    WorkspacePath = root,
                    RepositoryPath = root,
                    OutcomeVerificationJson =
                        OutcomeVerificationRules.SerializeAggregate(state)
                };
                flow.Messages.Add(new FlowMessage
                {
                    FlowRunId = flow.Id,
                    Role = ConversationRole.Customer,
                    Content = "Please keep the visible result."
                });
                flow.Steps.Add(new FlowStep
                {
                    FlowRunId = flow.Id,
                    Iteration = 1,
                    Sequence = 20,
                    AgentId = "software-engineer",
                    AgentName = "Software Engineer",
                    AgentRole = "software-engineer",
                    Label = "Execute Software Engineer contract",
                    Status = StepStatus.Completed
                });
                database.Flows.Add(flow);
                await database.SaveChangesAsync();
            }

            var builder = new OutcomeVerificationContextBuilder(
                new ContextFactory(options));
            var context = await builder.BuildAsync(
                await ReadFlowIdAsync(options),
                1);

            Assert.True(File.Exists(context.Path));
            Assert.True(OutcomeVerificationRules.IsSha256(context.Hash));
            Assert.Contains("Confirmed brief", await File.ReadAllTextAsync(context.Path));
            Assert.Contains("AC-001", context.PromptSummary);
            Assert.True(
                (File.GetAttributes(context.Path) & FileAttributes.ReadOnly) != 0);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(
                         root,
                         "*",
                         SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_RejectsSymlinkedContextParentThatEscapesWorkspace()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "qa-context-link-tests",
            Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(
            AppContext.BaseDirectory,
            "qa-context-link-outside",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".ai-harness"));
        Directory.CreateDirectory(outside);
        var link = Path.Combine(root, ".ai-harness", "outcome-verification");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException)
            {
                return;
            }

            var options = new DbContextOptionsBuilder<HarnessDbContext>()
                .UseSqlite(
                    $"Data Source={Path.Combine(root, "test.db")};Pooling=False")
                .Options;
            await using (var database = new HarnessDbContext(options))
            {
                await database.Database.EnsureCreatedAsync();
                var plan = Plan();
                var manifest = new CandidateManifest(
                    OutcomeVerificationRules.CandidateManifestVersion,
                    1,
                    plan.Hash,
                    [
                        new CandidateRepositoryManifest(
                            ".",
                            new string('a', 40),
                            new string('b', 40))
                    ],
                    [],
                    []);
                var state = OutcomeVerificationRules.CreateInitialState(1, 3);
                state.Status = OutcomeVerificationStatus.AwaitingQa;
                state.TrustedRepositories =
                    [new OutcomeTrustedRepository(".", string.Empty)];
                state.PlannedRoles =
                    ["software-engineer", "quality-engineer", "release-engineer"];
                state.AcceptancePlan = plan;
                state.CurrentCandidate = new OutcomeCandidateSnapshot(
                    manifest,
                    OutcomeVerificationRules.HashCandidateManifest(manifest),
                    Guid.NewGuid(),
                    DateTimeOffset.UnixEpoch);
                database.Flows.Add(new FlowRun
                {
                    Title = "Escaping context",
                    OriginalRequest = "Do not escape",
                    ConsolidatedRequest = "Do not escape",
                    WorkspacePath = root,
                    RepositoryPath = root,
                    OutcomeVerificationJson =
                        OutcomeVerificationRules.SerializeAggregate(state)
                });
                await database.SaveChangesAsync();
            }

            var builder = new OutcomeVerificationContextBuilder(
                new ContextFactory(options));
            var flowId = await ReadFlowIdAsync(options);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                builder.BuildAsync(flowId, 1));
            Assert.Empty(Directory.EnumerateFiles(outside));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Builder_RejectsSymlinkedContextFileInsideWorkspace()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "qa-context-file-link-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DbContextOptionsBuilder<HarnessDbContext>()
                .UseSqlite(
                    $"Data Source={Path.Combine(root, "test.db")};Pooling=False")
                .Options;
            string fingerprint;
            await using (var database = new HarnessDbContext(options))
            {
                await database.Database.EnsureCreatedAsync();
                var plan = Plan();
                var manifest = new CandidateManifest(
                    OutcomeVerificationRules.CandidateManifestVersion,
                    1,
                    plan.Hash,
                    [
                        new CandidateRepositoryManifest(
                            ".",
                            new string('a', 40),
                            new string('b', 40))
                    ],
                    [],
                    []);
                fingerprint = OutcomeVerificationRules.HashCandidateManifest(manifest);
                var state = OutcomeVerificationRules.CreateInitialState(1, 3);
                state.Status = OutcomeVerificationStatus.AwaitingQa;
                state.TrustedRepositories =
                    [new OutcomeTrustedRepository(".", string.Empty)];
                state.PlannedRoles =
                    ["software-engineer", "quality-engineer", "release-engineer"];
                state.AcceptancePlan = plan;
                state.CurrentCandidate = new OutcomeCandidateSnapshot(
                    manifest,
                    fingerprint,
                    Guid.NewGuid(),
                    DateTimeOffset.UnixEpoch);
                database.Flows.Add(new FlowRun
                {
                    Title = "Linked context file",
                    OriginalRequest = "Do not overwrite",
                    ConsolidatedRequest = "Do not overwrite",
                    WorkspacePath = root,
                    RepositoryPath = root,
                    OutcomeVerificationJson =
                        OutcomeVerificationRules.SerializeAggregate(state)
                });
                await database.SaveChangesAsync();
            }
            var target = Path.Combine(root, "tracked-candidate.txt");
            await File.WriteAllTextAsync(target, "do not overwrite");
            var directory = OutcomeVerificationContextBuilder.ResolveContextDirectory(
                root,
                fingerprint);
            Directory.CreateDirectory(directory);
            var link = Path.Combine(directory, "qa-context.json");
            try
            {
                File.CreateSymbolicLink(link, target);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException)
            {
                return;
            }

            var builder = new OutcomeVerificationContextBuilder(
                new ContextFactory(options));
            var flowId = await ReadFlowIdAsync(options);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                builder.BuildAsync(flowId, 1));
            Assert.Equal("do not overwrite", await File.ReadAllTextAsync(target));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(
                         root,
                         "*",
                         SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ContextDirectory_RejectsSymlinkedParentEvenWhenTargetStaysInsideWorkspace()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "qa-context-internal-link-tests",
            Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "redirect-target");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        var harness = Path.Combine(root, ".ai-harness");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(harness, target);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException)
            {
                return;
            }

            Assert.Throws<InvalidOperationException>(() =>
                OutcomeVerificationContextBuilder.ResolveContextDirectory(
                    root,
                    Digest('a')));
        }
        finally
        {
            if (Directory.Exists(harness))
            {
                Directory.Delete(harness);
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void PromptValues_AlwaysProvideStrictOutcomeVariables()
    {
        var values = CopilotReasoningHost.BuildPromptValues(
            new AgentExecutionContext(
                Guid.NewGuid(),
                1,
                "software-engineer",
                "Software Engineer",
                "software-engineer",
                "model",
                "high",
                1,
                "Task",
                string.Empty,
                string.Empty,
                Environment.CurrentDirectory,
                Guid.NewGuid(),
                OutcomeType.Commit,
                "Plan",
                [],
                [],
                OutcomeContext: "AC-001",
                OutcomeContract: "strict contract"),
            "instructions",
            Environment.CurrentDirectory);

        Assert.Equal("AC-001", values["outcome.context"]);
        Assert.Equal("strict contract", values["outcome.contract"]);
    }

    private static async Task<Guid> ReadFlowIdAsync(
        DbContextOptions<HarnessDbContext> options)
    {
        await using var database = new HarnessDbContext(options);
        return await database.Flows.Select(item => item.Id).SingleAsync();
    }

    private static OutcomeAcceptancePlanSnapshot Plan()
    {
        var document = new OutcomeAcceptancePlan(
            OutcomeVerificationRules.AcceptanceVersion,
            [
                new OutcomeAcceptanceCriterion(
                    "AC-001",
                    "The result is independently verified.",
                    "Run the focused tests and verify that the test output shows the expected preview content and passing results without errors.",
                    ["software-engineer"],
                    [OutcomeEvidenceKind.Test],
                    true)
            ]);
        return OutcomeVerificationRules.CreateAcceptanceSnapshot(
            document,
            Guid.NewGuid());
    }

    private static string Digest(char value) => $"sha256:{new string(value, 64)}";

    private sealed class ContextFactory(DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);
    }
}

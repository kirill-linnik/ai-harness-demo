using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Data;
using AiHarnessDemo.Infrastructure;
using AiHarnessDemo.Services;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Tests;

public sealed class WorkspacePreviewPreparerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InternalPreviewLinks_AreRejectedBeforeMaterialization(bool linkVariantRoot)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Write("site\\preview-target\\index.html", "linked content");
        var target = Path.Combine(fixture.Root, "site", "preview-target");
        var link = Path.Combine(fixture.Root, "site", ".customer-preview", "eu");
        if (!linkVariantRoot)
        {
            fixture.Write("site\\.customer-preview\\eu\\index.html", "preview");
            link = Path.Combine(link, "assets");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (OperatingSystem.IsWindows())
        {
            var result = await new ProcessRunner().RunAsync(
                Environment.GetEnvironmentVariable("ComSpec")!,
                ["/d", "/c", "mklink", "/J", link, target],
                fixture.Root, TimeSpan.FromSeconds(30));
            Assert.True(result.ExitCode == 0, result.CombinedOutput);
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.PrepareAsync());
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".customer-preview")));
        Assert.Empty(await fixture.Database.FlowEvents.Where(
            item => item.Type == WorkspacePreviewPreparer.StartedEventType).ToListAsync());
        Directory.Delete(link);
        Assert.Equal("linked content", fixture.Read("site\\preview-target\\index.html"));
    }

    [Fact]
    public async Task RepositoryPreviews_ArePreparedBeforeVerificationAndReplayIsIdempotent()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Write("site\\.customer-preview\\eu\\index.html", "Europe");
        fixture.Write("site\\.customer-preview\\ee\\browser\\index.html", "Estonia");

        Assert.True(await fixture.PrepareAsync());
        Assert.Equal("Europe", fixture.Read(".customer-preview\\eu\\index.html"));
        Assert.Equal("Estonia", fixture.Read(".customer-preview\\ee\\browser\\index.html"));
        Assert.Equal("Europe", fixture.Read("site\\.customer-preview\\eu\\index.html"));
        Assert.Equal(2, new PreviewArtifactCatalog().Discover(fixture.Flow).Count);
        var intents = await fixture.Database.FlowEvents
            .Where(item => item.Type == WorkspacePreviewPreparer.StartedEventType).ToListAsync();
        Assert.Equal(2, intents.Count);
        Assert.All(intents, intent =>
        {
            var preparation = JsonSerializer.Deserialize<WorkspacePreviewPreparer.Preparation>(intent.DataJson!)!;
            Assert.StartsWith("site/.customer-preview/", preparation.SourceRelativePath);
            Assert.NotEmpty(preparation.Files);
        });
        Assert.Equal(2, await fixture.Database.FlowEvents.CountAsync(
            item => item.Type == DeliveryReadinessService.EvidenceEpochEventType));
        Assert.False(await fixture.PrepareAsync());
        Assert.Equal(2, await fixture.Database.FlowEvents.CountAsync(
            item => item.Type == WorkspacePreviewPreparer.CompletedEventType));
    }

    [Fact]
    public async Task InterruptedPreparation_CompletesAlreadyCopiedBytesWithoutAnotherEpoch()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Write("site\\.customer-preview\\eu\\index.html", "preview");
        await fixture.PrepareAsync();
        fixture.Database.FlowEvents.RemoveRange(await fixture.Database.FlowEvents
            .Where(item => item.Type == WorkspacePreviewPreparer.CompletedEventType).ToListAsync());
        await fixture.Database.SaveChangesAsync();

        Assert.False(await fixture.PrepareAsync());
        Assert.Single(await fixture.Database.FlowEvents.Where(
            item => item.Type == WorkspacePreviewPreparer.CompletedEventType).ToListAsync());
        Assert.Single(await fixture.Database.FlowEvents.Where(
            item => item.Type == DeliveryReadinessService.EvidenceEpochEventType).ToListAsync());
    }

    [Fact]
    public async Task InterruptedPreparation_ReplaysPartialCopyButRejectsSourceDrift()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Write("site\\.customer-preview\\eu\\index.html", "preview");
        fixture.Write("site\\.customer-preview\\eu\\assets\\style.css", "style");
        await fixture.RecordIntentAsync("preview", "style");
        fixture.Write(".customer-preview\\eu\\assets\\style.css", "style");
        Assert.True(await fixture.PrepareAsync());
        Assert.Equal("preview", fixture.Read(".customer-preview\\eu\\index.html"));

        fixture.Database.FlowEvents.RemoveRange(await fixture.Database.FlowEvents
            .Where(item => item.Type == WorkspacePreviewPreparer.CompletedEventType).ToListAsync());
        await fixture.Database.SaveChangesAsync();
        fixture.Write("site\\.customer-preview\\eu\\index.html", "changed source");
        await Assert.ThrowsAsync<PreviewPreparationRequiredException>(() => fixture.PrepareAsync());
        Assert.Equal("preview", fixture.Read(".customer-preview\\eu\\index.html"));
    }

    [Fact]
    public async Task HostOwnedPreview_UpdatesAndRemovesOnlyPreviouslyOwnedFiles()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Write("site\\.customer-preview\\eu\\index.html", "old");
        fixture.Write("site\\.customer-preview\\eu\\obsolete.css", "old style");
        await fixture.PrepareAsync();
        fixture.Write("site\\.customer-preview\\eu\\index.html", "new");
        File.Delete(Path.Combine(fixture.Root, "site", ".customer-preview", "eu", "obsolete.css"));

        Assert.True(await fixture.PrepareAsync());
        Assert.Equal("new", fixture.Read(".customer-preview\\eu\\index.html"));
        Assert.False(File.Exists(Path.Combine(fixture.Root, ".customer-preview", "eu", "obsolete.css")));
    }

    [Fact]
    public async Task PreparationRevision_DoesNotDependOnEventTimestampOrdering()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Write("site\\.customer-preview\\eu\\index.html", "first");
        await fixture.PrepareAsync();
        fixture.Write("site\\.customer-preview\\eu\\index.html", "second");
        await fixture.PrepareAsync();
        var events = await fixture.Database.FlowEvents.Where(
            item => item.Type == WorkspacePreviewPreparer.StartedEventType).ToListAsync();
        var old = events.Single(item =>
            JsonSerializer.Deserialize<WorkspacePreviewPreparer.Preparation>(item.DataJson!)!.Revision == 1);
        old.CreatedAt = DateTimeOffset.UtcNow.AddHours(1);
        await fixture.Database.SaveChangesAsync();
        fixture.Write("site\\.customer-preview\\eu\\index.html", "third");

        Assert.True(await fixture.PrepareAsync());
        Assert.Equal("third", fixture.Read(".customer-preview\\eu\\index.html"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerAuthoredCanonicalPreview_IsNeverOverwritten(bool previouslyHostOwned)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Write("site\\.customer-preview\\eu\\index.html", "repository copy");
        if (previouslyHostOwned)
        {
            await fixture.PrepareAsync();
        }
        fixture.Write(".customer-preview\\eu\\index.html", "worker's authoritative result");
        Assert.False(await fixture.PrepareAsync());
        Assert.Equal("worker's authoritative result", fixture.Read(".customer-preview\\eu\\index.html"));
    }

    [Fact]
    public async Task AmbiguousOrMissingPreview_RequestsOwnerRepairInsteadOfGuessing()
    {
        await using var fixture = await Fixture.CreateAsync("site", "other");
        await Assert.ThrowsAsync<PreviewPreparationRequiredException>(() => fixture.PrepareAsync());
        fixture.Write("site\\.customer-preview\\eu\\index.html", "one");
        fixture.Write("other\\.customer-preview\\eu\\index.html", "two");
        await Assert.ThrowsAsync<PreviewPreparationRequiredException>(() => fixture.PrepareAsync());
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".customer-preview")));
        Assert.Empty(await fixture.Database.FlowEvents.Where(
            item => item.Type == WorkspacePreviewPreparer.StartedEventType).ToListAsync());
    }

    [Fact]
    public async Task InterruptedPreparation_RejectsUnrelatedDestinationBytes()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Write("site\\.customer-preview\\eu\\index.html", "preview");
        fixture.Write("site\\.customer-preview\\eu\\assets\\style.css", "style");
        await fixture.RecordIntentAsync("preview", "style");
        fixture.Write(".customer-preview\\eu\\index.html", "unrelated");
        await Assert.ThrowsAsync<PreviewPreparationRequiredException>(() => fixture.PrepareAsync());
        Assert.Equal("unrelated", fixture.Read(".customer-preview\\eu\\index.html"));
    }

    private sealed class Fixture(string root, HarnessDbContext database, FlowRun flow) : IAsyncDisposable
    {
        public string Root { get; } = root;
        public HarnessDbContext Database { get; } = database;
        public FlowRun Flow { get; } = flow;

        public static async Task<Fixture> CreateAsync(params string[] repositories)
        {
            if (repositories.Length == 0)
            {
                repositories = ["site"];
            }
            var root = Path.Combine(Path.GetTempPath(), $"preview-preparation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            foreach (var repository in repositories)
            {
                Directory.CreateDirectory(Path.Combine(root, repository, ".git"));
            }
            var database = new HarnessDbContext(new DbContextOptionsBuilder<HarnessDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "test.db")};Pooling=False").Options);
            await database.Database.EnsureCreatedAsync();
            var flow = new FlowRun
            {
                Title = "Customer preview",
                OriginalRequest = "Deliver a browser result.",
                Kind = FlowKind.Delivery,
                Outcome = OutcomeType.Commit,
                WorkspacePath = root,
                RepositoryPath = root,
                Iteration = 1
            };
            flow.Events.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                Type = StudioWorkspaceRepositoryMapLedger.EventType,
                Message = "Trusted fixture repositories.",
                DataJson = StudioWorkspaceRepositoryMapLedger.Serialize(
                    StudioWorkspaceRepositoryMapLedger.Create(flow, root,
                        repositories.Select(path => new WorkspaceRepositoryIdentity(path, "")).ToArray()))
            });
            flow.Events.Add(new FlowEvent
            {
                FlowRunId = flow.Id,
                Type = DeliveryReadinessService.AcceptancePlanEventType,
                Message = "Fixture browser acceptance plan.",
                DataJson = DeliveryReadinessService.SerializeAcceptancePlan(
                    DeliveryReadinessFixtures.Plan(), 1, Guid.NewGuid())
            });
            flow.Steps.Add(new FlowStep
            {
                FlowRunId = flow.Id,
                Sequence = 100,
                AgentId = "quality-engineer",
                AgentName = "Quality Engineer",
                AgentRole = "quality-engineer",
                PlanStepKey = "verify"
            });
            database.Flows.Add(flow);
            await database.SaveChangesAsync();
            return new Fixture(root, database, flow);
        }

        public void Write(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public string Read(string relativePath) =>
            File.ReadAllText(Path.Combine(Root, relativePath.Replace('\\', Path.DirectorySeparatorChar)));

        public Task<bool> PrepareAsync() =>
            WorkspacePreviewPreparer.PrepareAsync(Database, Flow, Flow.Steps.Single().Id, 100, CancellationToken.None);

        public async Task RecordIntentAsync(string index, string style)
        {
            static WorkspacePreviewPreparer.PreviewFile FileIdentity(string path, string content) =>
                new(path, Encoding.UTF8.GetByteCount(content),
                    "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant());
            Database.FlowEvents.Add(new FlowEvent
            {
                FlowRunId = Flow.Id,
                Type = WorkspacePreviewPreparer.StartedEventType,
                Message = "Interrupted fixture preparation.",
                DataJson = JsonSerializer.Serialize(new WorkspacePreviewPreparer.Preparation(
                    Guid.NewGuid(), "eu", "site/.customer-preview/eu",
                    [FileIdentity("assets/style.css", style), FileIdentity("index.html", index)], []))
            });
            await Database.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }
    }
}

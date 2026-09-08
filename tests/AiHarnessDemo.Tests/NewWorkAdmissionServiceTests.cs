using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Data;
using AiHarnessDemo.Services;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Tests;

public sealed class NewWorkAdmissionServiceTests
{
    [Fact]
    public async Task LinkedContext_UsesParentSnapshotInsteadOfInvalidGlobalSettings()
    {
        var root = CreateProjectRoot();
        try
        {
            var factory = await CreateFactoryAsync(
                root,
                Path.Combine(root, "moved-global-project"),
                repositoryKnowledge: string.Empty);
            var admission = new NewWorkAdmissionService(
                factory,
                () => []);

            await admission.EnsureReadyForContextAsync(
                root,
                "The parent captured valid repository knowledge.");
            var ordinaryFailure =
                await Assert.ThrowsAsync<NewWorkAdmissionException>(
                    () => admission.EnsureReadyAsync());

            Assert.Contains(
                ordinaryFailure.Failures,
                failure => failure.Contains(
                    "configured source project path does not exist",
                    StringComparison.Ordinal));
            Assert.Contains(
                ordinaryFailure.Failures,
                failure => failure.Contains(
                    "Repository knowledge is required",
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LinkedContext_FailsWhenParentRepositorySnapshotIsInvalid()
    {
        var root = CreateProjectRoot();
        try
        {
            var factory = await CreateFactoryAsync(
                root,
                root,
                "Valid global repository knowledge.");
            var admission = new NewWorkAdmissionService(
                factory,
                () => []);

            var failure =
                await Assert.ThrowsAsync<NewWorkAdmissionException>(
                    () => admission.EnsureReadyForContextAsync(
                        Path.Combine(root, "missing-parent-project"),
                        string.Empty));

            Assert.Contains(
                failure.Failures,
                item => item.Contains(
                    "linked source project path does not exist",
                    StringComparison.Ordinal));
            Assert.Contains(
                failure.Failures,
                item => item.Contains(
                    "Linked source-project knowledge is required",
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LinkedContext_StillRequiresSharedRuntimeReadiness()
    {
        var root = CreateProjectRoot();
        try
        {
            var factory = await CreateFactoryAsync(
                root,
                root,
                "Valid global repository knowledge.");
            var admission = new NewWorkAdmissionService(
                factory,
                () => ["Model catalog is not ready: unavailable."]);

            var failure =
                await Assert.ThrowsAsync<NewWorkAdmissionException>(
                    () => admission.EnsureReadyForContextAsync(
                        root,
                        "Valid parent repository knowledge."));

            Assert.Equal(
                ["Model catalog is not ready: unavailable."],
                failure.Failures);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateProjectRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-admission-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        return root;
    }

    private static async Task<AdmissionDbContextFactory> CreateFactoryAsync(
        string root,
        string repositoryPath,
        string repositoryKnowledge)
    {
        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseSqlite(
                $"Data Source={Path.Combine(root, "harness.db")};Pooling=False")
            .Options;
        var factory = new AdmissionDbContextFactory(options);
        await using var database = await factory.CreateDbContextAsync();
        await database.Database.EnsureCreatedAsync();
        database.Settings.Add(new HarnessSettings
        {
            RepositoryPath = repositoryPath,
            RepositoryKnowledge = repositoryKnowledge
        });
        await database.SaveChangesAsync();
        return factory;
    }

    private sealed class AdmissionDbContextFactory(
        DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);

        public Task<HarnessDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}

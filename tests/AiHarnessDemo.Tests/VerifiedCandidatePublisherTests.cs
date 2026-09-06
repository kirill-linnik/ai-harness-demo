using System.Diagnostics;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Services;

namespace AiHarnessDemo.Tests;

public sealed class VerifiedCandidatePublisherTests
{
    [Fact]
    public async Task PublishVerifiedGitBranchAsync_IgnoresRepositoryHooksAndPublishesExactObjects()
    {
        using var workspace = PublicationWorkspace.Create();
        var hookPath = Path.Combine(workspace.Root, "hooks", "pre-push");
        Directory.CreateDirectory(Path.GetDirectoryName(hookPath)!);
        await WriteShellScriptAsync(
            hookPath,
            """
            #!/bin/sh
            printf hook > "./hook-fired.txt"
            exit 1
            """);
        workspace.Git("config", "core.hooksPath", ToGitPath(Path.GetDirectoryName(hookPath)!));

        var published = await VerifiedCandidatePublisher.PublishVerifiedGitBranchAsync(
            new ProcessRunner(),
            workspace.SourceRepository,
            workspace.RepositoryManifest,
            workspace.TargetRepository,
            "verified-publication",
            string.Empty);

        Assert.Equal(workspace.RepositoryManifest.Head, published.Head);
        Assert.Equal(workspace.RepositoryManifest.Tree, published.Tree);
        Assert.False(File.Exists(Path.Combine(workspace.SourceRepository, "hook-fired.txt")));
        Assert.Equal(
            workspace.RepositoryManifest.Head,
            workspace.GitOutput(
                workspace.TargetRepository,
                "--git-dir",
                workspace.TargetRepository,
                "rev-parse",
                "refs/heads/verified-publication").Trim().ToLowerInvariant());
        Assert.Equal(
            workspace.RepositoryManifest.Tree,
            workspace.GitOutput(
                workspace.TargetRepository,
                "--git-dir",
                workspace.TargetRepository,
                "rev-parse",
                "refs/heads/verified-publication^{tree}").Trim().ToLowerInvariant());
    }

    [Fact]
    public async Task PublishVerifiedGitBranchAsync_IgnoresGlobalCredentialHelper()
    {
        using var workspace = PublicationWorkspace.Create();
        var markerPath = Path.Combine(workspace.Root, "credential-helper-fired.txt");
        var configPath = Path.Combine(workspace.Root, "helper.gitconfig");
        await File.WriteAllTextAsync(
            configPath,
            "[credential]\n" +
            $"    helper = \"!f() {{ printf helper > '{ToGitPath(markerPath)}'; exit 1; }}; f\"\n");

        using var globalConfig = new EnvironmentVariableScope(
            "GIT_CONFIG_GLOBAL",
            configPath);
        using var home = new EnvironmentVariableScope(
            "HOME",
            Path.Combine(workspace.Root, "global-home"));
        using var userProfile = new EnvironmentVariableScope(
            "USERPROFILE",
            Path.Combine(workspace.Root, "global-home"));
        using var xdg = new EnvironmentVariableScope(
            "XDG_CONFIG_HOME",
            Path.Combine(workspace.Root, "global-xdg"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VerifiedCandidatePublisher.PublishVerifiedGitBranchAsync(
                new ProcessRunner(),
                workspace.SourceRepository,
                workspace.RepositoryManifest,
                "https://127.0.0.1:1/example/repository.git",
                "verified-publication",
                string.Empty,
                remoteTimeout: TimeSpan.FromSeconds(10)));

        Assert.Contains("push verified commit", exception.Message);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task PublishVerifiedGitBranchAsync_IgnoresSystemUrlRewrite()
    {
        using var workspace = PublicationWorkspace.Create();
        var systemConfigPath = Path.Combine(workspace.Root, "system.gitconfig");
        await File.WriteAllTextAsync(
            systemConfigPath,
            $"""
            [url "{ToGitPath(workspace.TargetRepository)}"]
                insteadOf = blocked-target
            """);

        using var systemConfig = new EnvironmentVariableScope(
            "GIT_CONFIG_SYSTEM",
            systemConfigPath);
        using var noSystem = new EnvironmentVariableScope(
            "GIT_CONFIG_NOSYSTEM",
            "0");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VerifiedCandidatePublisher.PublishVerifiedGitBranchAsync(
                new ProcessRunner(),
                workspace.SourceRepository,
                workspace.RepositoryManifest,
                "blocked-target",
                "verified-publication",
                string.Empty,
                remoteTimeout: TimeSpan.FromSeconds(10)));

        Assert.Contains("push verified commit", exception.Message);
        Assert.False(HasRef(
            workspace.TargetRepository,
            "refs/heads/verified-publication"));
    }

    private static bool HasRef(string repository, string reference)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--git-dir");
        start.ArgumentList.Add(repository);
        start.ArgumentList.Add("rev-parse");
        start.ArgumentList.Add("--verify");
        start.ArgumentList.Add(reference);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git.");
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private static async Task WriteShellScriptAsync(
        string path,
        string content)
    {
        await File.WriteAllTextAsync(
            path,
            content.ReplaceLineEndings("\n"));
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
    }

    private static string ToGitPath(string path) =>
        Path.GetFullPath(path).Replace('\\', '/');

    private sealed class PublicationWorkspace : IDisposable
    {
        private PublicationWorkspace(
            string root,
            string sourceRepository,
            string targetRepository,
            CandidateRepositoryManifest repositoryManifest)
        {
            Root = root;
            SourceRepository = sourceRepository;
            TargetRepository = targetRepository;
            RepositoryManifest = repositoryManifest;
        }

        public string Root { get; }

        public string SourceRepository { get; }

        public string TargetRepository { get; }

        public CandidateRepositoryManifest RepositoryManifest { get; }

        public static PublicationWorkspace Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "verified-publication-tests",
                Guid.NewGuid().ToString("N"));
            var sourceRepository = Path.Combine(root, "source");
            var targetRepository = Path.Combine(root, "target.git");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(sourceRepository);
            RunGit(sourceRepository, "init", "--quiet");
            RunGit(sourceRepository, "config", "user.email", "tests@example.invalid");
            RunGit(sourceRepository, "config", "user.name", "Verified Candidate Tests");
            File.WriteAllText(Path.Combine(sourceRepository, "tracked.txt"), "verified");
            RunGit(sourceRepository, "add", "-A");
            RunGit(sourceRepository, "commit", "--quiet", "-m", "initial");
            RunGit(root, "init", "--quiet", "--bare", targetRepository);
            var head = GitOutput(sourceRepository, ["rev-parse", "HEAD"])
                .Trim()
                .ToLowerInvariant();
            var tree = GitOutput(sourceRepository, ["rev-parse", "HEAD^{tree}"])
                .Trim()
                .ToLowerInvariant();
            return new PublicationWorkspace(
                root,
                sourceRepository,
                targetRepository,
                new CandidateRepositoryManifest(".", head, tree, string.Empty));
        }

        public void Git(params string[] arguments) =>
            RunGit(SourceRepository, arguments);

        public string GitOutput(string workingDirectory, params string[] arguments) =>
            GitOutput(workingDirectory, arguments.AsEnumerable());

        private static void RunGit(
            string workingDirectory,
            params string[] arguments) =>
            _ = GitOutput(workingDirectory, arguments.AsEnumerable());

        private static string GitOutput(
            string workingDirectory,
            IEnumerable<string> arguments)
        {
            var start = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start git.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(error);
            }
            return output;
        }

        public void Dispose()
        {
            VerifiedCandidatePublisherTestsCleanup.DeleteDirectoryBestEffort(Root);
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _priorValue;

        public EnvironmentVariableScope(
            string name,
            string? value)
        {
            _name = name;
            _priorValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _priorValue);
    }

    private static class VerifiedCandidatePublisherTestsCleanup
    {
        public static void DeleteDirectoryBestEffort(string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !Directory.Exists(path))
            {
                return;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(
                             path,
                             "*",
                             SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                foreach (var directory in Directory.EnumerateDirectories(
                             path,
                             "*",
                             SearchOption.AllDirectories))
                {
                    File.SetAttributes(directory, FileAttributes.Directory);
                }
                Directory.Delete(path, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Test cleanup only.
            }
        }
    }
}

using System.Text;
using AiHarnessDemo.Contracts;
using AiHarnessDemo.Data;
using AiHarnessDemo.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

public sealed record RepositoryAnalysis(
    string Path,
    string Knowledge,
    bool CopilotInitSucceeded,
    string CopilotInitMessage);

public sealed class RepositoryAnalyzer(
    ProcessRunner processRunner,
    RepositoryContextGate contextGate,
    IDbContextFactory<HarnessDbContext> databaseFactory,
    WorkflowDefinitionProvider workflowProvider,
    ILogger<RepositoryAnalyzer> logger)
{
    private static readonly HashSet<string> IgnoredDirectories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".idea", "bin", "obj", "node_modules", "dist", "build",
            "coverage", ".next", ".venv", "venv", "__pycache__", "packages"
        };

    public static bool IsGitRepository(string path) =>
        Directory.Exists(path) &&
        (
            Directory.Exists(Path.Combine(path, ".git")) ||
            File.Exists(Path.Combine(path, ".git"))
        );

    public async Task<RepositoryAnalysis> AnalyzeAsync(
        string requestedPath,
        bool runCopilotInit,
        CancellationToken cancellationToken = default)
    {
        using var contextLease = await contextGate.EnterWriteAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            throw new ArgumentException("A repository path is required.", nameof(requestedPath));
        }

        var repositoryPath = Path.GetFullPath(requestedPath);
        if (!Directory.Exists(repositoryPath))
        {
            throw new DirectoryNotFoundException(
                $"The selected repository does not exist: {repositoryPath}");
        }

        var initSucceeded = false;
        var initMessage = "Copilot init was not requested.";

        if (runCopilotInit)
        {
            if (!ExecutableLocator.Exists("copilot"))
            {
                initMessage = "Copilot CLI is not available on PATH. Static repository study completed.";
            }
            else
            {
                var result = await processRunner.RunAsync(
                    "copilot",
                    ["init"],
                    repositoryPath,
                    TimeSpan.FromMilliseconds(
                        workflowProvider.GetValidated().Config.Copilot.TurnTimeoutMs),
                    cancellationToken);

                initSucceeded = result.ExitCode == 0;
                initMessage = initSucceeded
                    ? "Copilot CLI initialized repository instructions successfully."
                    : $"Copilot init failed (exit {result.ExitCode}): {Tail(result.CombinedOutput, 700)}";

                if (!initSucceeded)
                {
                    logger.LogWarning(
                        "Copilot init failed for {RepositoryPath}: {Message}",
                        repositoryPath,
                        initMessage);
                }
            }
        }

        var knowledge = await BuildKnowledgeAsync(
            repositoryPath,
            initMessage,
            cancellationToken);

        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        var settings = await database.Settings.SingleAsync(cancellationToken);
        settings.RepositoryPath = repositoryPath;
        settings.RepositoryKnowledge = knowledge;
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);

        return new RepositoryAnalysis(repositoryPath, knowledge, initSucceeded, initMessage);
    }

    public DirectoryListingDto ListDirectories(string? requestedPath)
    {
        var drives = DriveInfo.GetDrives()
            .Where(drive => drive.IsReady)
            .Select(drive => new DirectoryEntryDto(
                string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? drive.Name
                    : $"{drive.Name}  {drive.VolumeLabel}",
                drive.RootDirectory.FullName))
            .ToList();

        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return new DirectoryListingDto(string.Empty, null, [], drives);
        }

        var path = Path.GetFullPath(requestedPath);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Directory does not exist: {path}");
        }

        var directories = Directory.EnumerateDirectories(path)
            .Select(item => new DirectoryInfo(item))
            .Where(item => (item.Attributes & FileAttributes.Hidden) == 0)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(250)
            .Select(item => new DirectoryEntryDto(item.Name, item.FullName))
            .ToList();

        return new DirectoryListingDto(
            path,
            Directory.GetParent(path)?.FullName,
            directories,
            drives);
    }

    private static async Task<string> BuildKnowledgeAsync(
        string repositoryPath,
        string initMessage,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var files = EnumerateSourceFiles(repositoryPath, warnings).Take(6_000).ToList();
        var extensions = files
            .Select(Path.GetExtension)
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(extension => extension!)
            .GroupBy(extension => extension.ToLowerInvariant())
            .OrderByDescending(group => group.Count())
            .Take(8)
            .Select(group => $"{group.Key} ({group.Count():N0})")
            .ToList();

        var topDirectories = Directory.EnumerateDirectories(repositoryPath)
            .Select(Path.GetFileName)
            .Where(name => name is not null && !IgnoredDirectories.Contains(name))
            .OrderBy(name => name)
            .Take(12)
            .ToList();

        var frameworks = await DetectFrameworksAsync(files, cancellationToken);
        var commands = DetectCommands(files);
        var instructionsPath = Path.Combine(
            repositoryPath,
            ".github",
            "copilot-instructions.md");
        var readmePath = files.FirstOrDefault(file =>
            string.Equals(Path.GetFileName(file), "README.md", StringComparison.OrdinalIgnoreCase));

        var builder = new StringBuilder();
        builder.AppendLine($"# {Path.GetFileName(repositoryPath)}");
        builder.AppendLine();
        builder.AppendLine("## Repository profile");
        builder.AppendLine($"- **Location:** `{repositoryPath}`");
        builder.AppendLine($"- **Source files studied:** {files.Count:N0}");
        builder.AppendLine($"- **Primary file types:** {string.Join(", ", extensions.DefaultIfEmpty("No source files detected"))}");
        builder.AppendLine($"- **Top-level areas:** {string.Join(", ", topDirectories.DefaultIfEmpty("No child directories"))}");
        builder.AppendLine($"- **Detected stack:** {string.Join(", ", frameworks.DefaultIfEmpty("No framework manifest detected"))}");
        builder.AppendLine();
        builder.AppendLine("## Likely developer commands");
        foreach (var command in commands.DefaultIfEmpty("Inspect the repository README for project-specific commands."))
        {
            builder.AppendLine($"- `{command}`");
        }

        builder.AppendLine();
        builder.AppendLine("## AI initialization");
        builder.AppendLine(initMessage);

        if (File.Exists(instructionsPath))
        {
            builder.AppendLine();
            builder.AppendLine("## Copilot repository instructions");
            builder.AppendLine(await ReadLimitedAsync(instructionsPath, 5_000, cancellationToken));
        }

        if (readmePath is not null)
        {
            builder.AppendLine();
            builder.AppendLine("## README signal");
            builder.AppendLine(await ReadLimitedAsync(readmePath, 2_500, cancellationToken));
        }

        if (warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Study warnings");
            foreach (var warning in warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Editable harness notes");
        builder.AppendLine(
            "Add domain language, architectural constraints, release rules, and quality expectations here. " +
            "Every agent receives this shared context.");

        return builder.ToString().Trim();
    }

    private static IEnumerable<string> EnumerateSourceFiles(
        string root,
        ICollection<string> warnings)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> directories;
            IEnumerable<string> files;

            try
            {
                directories = Directory.EnumerateDirectories(current).ToList();
                files = Directory.EnumerateFiles(current).ToList();
            }
            catch (UnauthorizedAccessException exception)
            {
                warnings.Add($"Skipped protected directory `{current}`: {exception.Message}");
                continue;
            }
            catch (IOException exception)
            {
                warnings.Add($"Skipped unreadable directory `{current}`: {exception.Message}");
                continue;
            }

            foreach (var directory in directories)
            {
                if (!IgnoredDirectories.Contains(Path.GetFileName(directory)))
                {
                    pending.Push(directory);
                }
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }

    private static async Task<List<string>> DetectFrameworksAsync(
        IReadOnlyCollection<string> files,
        CancellationToken cancellationToken)
    {
        var frameworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = files
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (files.Any(file => file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                              file.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
                              file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            frameworks.Add(".NET");
        }

        if (names.Contains("package.json"))
        {
            frameworks.Add("Node.js");
            var packagePath = files.First(file =>
                string.Equals(Path.GetFileName(file), "package.json", StringComparison.OrdinalIgnoreCase));
            var package = await ReadLimitedAsync(packagePath, 80_000, cancellationToken);
            if (package.Contains("\"react\"", StringComparison.OrdinalIgnoreCase))
            {
                frameworks.Add("React");
            }
            if (package.Contains("\"vue\"", StringComparison.OrdinalIgnoreCase))
            {
                frameworks.Add("Vue");
            }
            if (package.Contains("\"@angular/", StringComparison.OrdinalIgnoreCase))
            {
                frameworks.Add("Angular");
            }
        }

        if (names.Contains("pyproject.toml") || names.Contains("requirements.txt"))
        {
            frameworks.Add("Python");
        }
        if (names.Contains("go.mod"))
        {
            frameworks.Add("Go");
        }
        if (names.Contains("Cargo.toml"))
        {
            frameworks.Add("Rust");
        }
        if (names.Contains("pom.xml") || names.Contains("build.gradle") || names.Contains("build.gradle.kts"))
        {
            frameworks.Add("JVM");
        }
        if (names.Contains("dockerfile") || names.Contains("docker-compose.yml"))
        {
            frameworks.Add("Containers");
        }

        return frameworks.OrderBy(item => item).ToList();
    }

    private static List<string> DetectCommands(IReadOnlyCollection<string> files)
    {
        var names = files
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var commands = new List<string>();

        if (files.Any(file => file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                              file.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
                              file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            commands.Add("dotnet build");
            commands.Add("dotnet test");
        }
        if (names.Contains("package.json"))
        {
            commands.Add(names.Contains("pnpm-lock.yaml") ? "pnpm install" : "npm install");
            commands.Add(names.Contains("pnpm-lock.yaml") ? "pnpm run dev" : "npm run dev");
        }
        if (names.Contains("pyproject.toml"))
        {
            commands.Add("python -m pytest");
        }
        if (names.Contains("go.mod"))
        {
            commands.Add("go test ./...");
        }
        if (names.Contains("Cargo.toml"))
        {
            commands.Add("cargo test");
        }

        return commands;
    }

    private static async Task<string> ReadLimitedAsync(
        string path,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        return text.Length <= maxCharacters
            ? text.Trim()
            : $"{text[..maxCharacters].Trim()}\n\n[truncated by repository study]";
    }

    private static string Tail(string text, int maxCharacters) =>
        text.Length <= maxCharacters ? text : text[^maxCharacters..];
}

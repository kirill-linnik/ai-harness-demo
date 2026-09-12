using System.ComponentModel;
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
    RepositoryKnowledgeSynthesizer knowledgeSynthesizer,
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

    public static bool IsProjectDirectory(string path) =>
        FindGitRepositories(path).Count > 0;

    public static IReadOnlyList<string> FindGitRepositories(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath) ||
            !Directory.Exists(projectPath))
        {
            return [];
        }

        var root = Path.GetFullPath(projectPath);
        var repositories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (IsGitRepository(current))
            {
                repositories.Add(current);
                continue;
            }

            IReadOnlyList<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(current).ToList();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var directory in directories)
            {
                if (!ShouldIgnoreDirectory(directory) &&
                    CanTraverse(directory))
                {
                    pending.Push(directory);
                }
            }
        }

        return repositories
            .OrderBy(
                path => Path.GetRelativePath(root, path),
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal)
            .ToList();
    }

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
                $"The selected project folder does not exist: {repositoryPath}");
        }
        var gitRepositories = FindGitRepositories(repositoryPath);
        if (gitRepositories.Count == 0)
        {
            throw new InvalidOperationException(
                "The selected project folder must contain at least one Git repository.");
        }
        var workflow = workflowProvider.GetValidated();
        var copilotCommand = workflow.Config.Copilot.Command;
        if (!ExecutableLocator.Exists(copilotCommand, repositoryPath))
        {
            throw new InvalidOperationException(
                $"Copilot CLI command '{copilotCommand}' is required to synthesize repository knowledge, but it is not available. No repository knowledge was changed.");
        }

        var initSucceeded = false;
        var initMessage = "Copilot init was not requested.";

        if (runCopilotInit)
        {
            try
            {
                var result = await processRunner.RunAsync(
                    copilotCommand,
                    ["init"],
                    repositoryPath,
                    TimeSpan.FromMilliseconds(workflow.Config.Copilot.TurnTimeoutMs),
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
            catch (Win32Exception exception)
            {
                initMessage =
                    $"Copilot init could not be launched: {exception.Message}";
                logger.LogWarning(
                    exception,
                    "Copilot init could not be launched for {RepositoryPath}.",
                    repositoryPath);
            }
        }

        var inventory = await BuildStudyInventoryAsync(
            repositoryPath,
            gitRepositories,
            cancellationToken);
        var knowledge = await knowledgeSynthesizer.SynthesizeAsync(
            copilotCommand,
            workflow.Config.Copilot,
            repositoryPath,
            inventory,
            cancellationToken);
        initMessage =
            $"Repository knowledge synthesized from documentation, manifests, source, and tests. {initMessage}";

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
        var locations = ListLocations();

        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return new DirectoryListingDto(string.Empty, null, [], locations);
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
            locations);
    }

    public static IReadOnlyList<DirectoryEntryDto> ListLocations()
    {
        var locations = new List<DirectoryEntryDto>();
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        void AddLocation(string name, string path)
        {
            if (Directory.Exists(path) &&
                !locations.Any(item =>
                    string.Equals(item.Path, path, comparison)))
            {
                locations.Add(new DirectoryEntryDto(name, path));
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            AddLocation("Home", home);
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var drive in DriveInfo.GetDrives().Where(item => item.IsReady))
            {
                AddLocation(DriveLabel(drive), drive.RootDirectory.FullName);
            }
        }
        else
        {
            var root = Path.GetPathRoot(Environment.CurrentDirectory) ??
                       Path.DirectorySeparatorChar.ToString();
            AddLocation("File system", root);
        }

        return locations;
    }

    internal static async Task<RepositoryStudyInventory> BuildStudyInventoryAsync(
        string repositoryPath,
        IReadOnlyList<string> gitRepositories,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var files = EnumerateSourceFiles(
                repositoryPath,
                warnings,
                cancellationToken)
            .OrderBy(
                path => Path.GetRelativePath(repositoryPath, path),
                pathComparer)
            .ToList();

        var topDirectories = Directory.EnumerateDirectories(repositoryPath)
            .Select(Path.GetFileName)
            .Where(name => name is not null && !IgnoredDirectories.Contains(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var frameworks = await DetectFrameworksAsync(files, cancellationToken);
        var commands = DetectCommands(files);
        var relativeFiles = files
            .Select(path => Path.GetRelativePath(repositoryPath, path))
            .ToList();
        var repositoryPaths = gitRepositories
            .Select(path => Path.GetRelativePath(repositoryPath, path))
            .ToList();
        var primaryFileTypes = files
            .GroupBy(
                path => string.IsNullOrWhiteSpace(Path.GetExtension(path))
                    ? "[no extension]"
                    : Path.GetExtension(path).ToLowerInvariant(),
                StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .Select(group => $"{group.Key} ({group.Count()})")
            .ToList();
        var entryPoints = relativeFiles
            .Where(IsStudyEntryPoint)
            .OrderBy(path => path, pathComparer)
            .Take(80)
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine("Deterministic inventory (seed evidence, not final knowledge):");
        builder.AppendLine($"- Project: {Path.GetFileName(repositoryPath)}");
        builder.AppendLine(
            $"- Git repositories: {string.Join(", ", repositoryPaths.DefaultIfEmpty("."))}");
        builder.AppendLine(
            $"- Top-level areas: {string.Join(", ", topDirectories.DefaultIfEmpty("No child directories"))}");
        builder.AppendLine(
            $"- Detected stack hints: {string.Join(", ", frameworks.DefaultIfEmpty("No framework manifest detected"))}");
        builder.AppendLine(
            $"- Primary file types: {string.Join(", ", primaryFileTypes.DefaultIfEmpty("No files"))}");
        builder.AppendLine($"- Files inventoried: {files.Count}");
        builder.AppendLine(
            $"- Excluded generated or dependency directories: {string.Join(", ", IgnoredDirectories.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))}");
        builder.AppendLine("- Candidate commands to verify:");
        foreach (var command in commands.DefaultIfEmpty("Inspect the repository README for project-specific commands."))
        {
            builder.AppendLine($"  - {command}");
        }

        if (entryPoints.Count > 0)
        {
            builder.AppendLine("- Selected documentation, manifest, and configuration entry points:");
            foreach (var entryPoint in entryPoints)
            {
                builder.AppendLine($"  - {entryPoint}");
            }
        }

        if (warnings.Count > 0)
        {
            builder.AppendLine("- Inventory warnings:");
            foreach (var warning in warnings)
            {
                builder.AppendLine($"  - {warning}");
            }
        }

        return new RepositoryStudyInventory(
            builder.ToString().Trim(),
            relativeFiles,
            repositoryPaths);
    }

    private static bool IsStudyEntryPoint(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("README", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("copilot-instructions.md", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("WORKFLOW.md", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("package-lock.json", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("pnpm-lock.yaml", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("yarn.lock", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("bun.lock", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("angular.json", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("requirements.txt", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("go.mod", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("pom.xml", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("build.gradle", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("build.gradle.kts", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("docker-compose.yml", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("docker-compose.yaml", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateSourceFiles(
        string root,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                if (!IgnoredDirectories.Contains(Path.GetFileName(directory)) &&
                    CanTraverse(directory))
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
            foreach (var packagePath in files.Where(file =>
                         string.Equals(
                             Path.GetFileName(file),
                             "package.json",
                             StringComparison.OrdinalIgnoreCase)))
            {
                var package = await ReadPrefixAsync(
                    packagePath,
                    80_000,
                    cancellationToken);
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

    private static async Task<string> ReadPrefixAsync(
        string path,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4_096,
            useAsync: true);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        var buffer = new char[maximumCharacters];
        var read = await reader.ReadBlockAsync(
            buffer.AsMemory(),
            cancellationToken);
        return new string(buffer, 0, read);
    }

    private static string Tail(string text, int maxCharacters) =>
        text.Length <= maxCharacters ? text : text[^maxCharacters..];

    internal static bool ShouldIgnoreDirectory(string directory) =>
        IgnoredDirectories.Contains(Path.GetFileName(directory));

    internal static bool CanTraverse(string directory)
    {
        try
        {
            return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string DriveLabel(DriveInfo drive)
    {
        try
        {
            return string.IsNullOrWhiteSpace(drive.VolumeLabel)
                ? drive.Name
                : $"{drive.Name}  {drive.VolumeLabel}";
        }
        catch (IOException)
        {
            return drive.Name;
        }
        catch (UnauthorizedAccessException)
        {
            return drive.Name;
        }
    }
}

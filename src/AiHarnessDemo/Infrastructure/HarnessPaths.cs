namespace AiHarnessDemo.Infrastructure;

public sealed record HarnessPaths(
    string Root,
    string AgentsDirectory,
    string DatabasePath)
{
    public static HarnessPaths Create(IHostEnvironment environment, IConfiguration configuration)
    {
        var configuredRoot = configuration["Harness:Root"];
        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", ".."))
            : Path.GetFullPath(
                Path.IsPathRooted(configuredRoot)
                    ? configuredRoot
                    : Path.Combine(environment.ContentRootPath, configuredRoot));

        var dataDirectory = Path.Combine(root, "data");
        Directory.CreateDirectory(dataDirectory);

        return new HarnessPaths(
            root,
            Path.Combine(root, ".github", "agents"),
            Path.Combine(dataDirectory, "ai-harness.db"));
    }
}

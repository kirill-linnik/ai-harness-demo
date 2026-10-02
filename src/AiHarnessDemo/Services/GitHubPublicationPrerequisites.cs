namespace AiHarnessDemo.Services;

internal static class GitHubPublicationPrerequisites
{
    internal const string MissingCliMessage =
        "Pull-request publication requires GitHub CLI (gh) on the Studio server's PATH. " +
        "Install gh, authenticate it with GitHub, and restart Studio with gh on its PATH " +
        "before confirming or accepting a Delivery flow.";

    internal const string MissingAuthenticationMessage =
        "GitHub CLI (gh) is available, but the Studio process has no GitHub token. " +
        "Run 'gh auth login -h github.com' as the Studio user or launch Studio with GH_TOKEN, " +
        "then retry before confirming or accepting a pull-request Delivery.";

    internal static bool IsAvailable() => ExecutableLocator.Exists("gh");

    internal static async Task<bool> HasAuthenticationAsync(
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("GH_TOKEN")) ||
            !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("GITHUB_TOKEN")))
        {
            return true;
        }

        var result = await new ProcessRunner().RunAsync(
            "gh",
            ["auth", "token", "-h", "github.com"],
            AppContext.BaseDirectory,
            TimeSpan.FromSeconds(15),
            cancellationToken);
        return result.ExitCode == 0 &&
               !string.IsNullOrWhiteSpace(result.StandardOutput);
    }

    internal static async Task<GitHubPublicationPrerequisiteStatus> CheckAsync(
        Func<bool>? cliAvailable = null,
        Func<CancellationToken, Task<bool>>? authenticationAvailable = null,
        CancellationToken cancellationToken = default)
    {
        if (!(cliAvailable?.Invoke() ?? IsAvailable()))
        {
            return new(false, false, MissingCliMessage);
        }
        var authenticated = await (
            authenticationAvailable?.Invoke(cancellationToken) ??
            HasAuthenticationAsync(cancellationToken));
        return new(
            true,
            authenticated,
            authenticated ? null : MissingAuthenticationMessage);
    }

    internal static async Task RequireAsync(
        Func<bool>? cliAvailable = null,
        Func<CancellationToken, Task<bool>>? authenticationAvailable = null,
        CancellationToken cancellationToken = default)
    {
        var status = await CheckAsync(
            cliAvailable,
            authenticationAvailable,
            cancellationToken);
        if (status.Error is { } error)
        {
            throw new NewWorkAdmissionException([error]);
        }
    }
}

internal sealed record GitHubPublicationPrerequisiteStatus(
    bool CliAvailable,
    bool Authenticated,
    string? Error);

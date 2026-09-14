using System.Diagnostics;
using System.Text;
using AiHarnessDemo.Services;

namespace AiHarnessDemo.Tests;

/// <summary>
/// A <see cref="ProcessRunner"/> that answers every Git and GitHub CLI call from memory. Tests can
/// therefore assert the exact set of external side effects a publication attempt produced,
/// including that an unauthorized attempt produced none at all.
/// </summary>
internal sealed class ScriptedProcessRunner : ProcessRunner
{
    private readonly object sync = new();
    private readonly List<ProcessInvocation> invocations = [];
    private readonly Dictionary<string, string> treesByCommit =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> pullRequestNumbers =
        new(StringComparer.OrdinalIgnoreCase);
    private string lastPushedHead = string.Empty;

    public sealed record ProcessInvocation(
        string Executable,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory);

    public sealed record FakePullRequest(
        string Url,
        int Number,
        string State,
        string HeadRefName,
        string HeadRefOid,
        string Repository);

    public sealed class SimulatedCrashException(string message)
        : Exception(message);

    public List<FakePullRequest> PullRequests { get; } = [];

    /// <summary>The commit the remote branch currently points at; empty means "no branch".</summary>
    public string RemoteBranchHead { get; set; } = string.Empty;

    public Func<ProcessInvocation, bool>? FailOn { get; set; }

    public Func<ProcessInvocation, bool>? FailAfter { get; set; }

    public IReadOnlyList<ProcessInvocation> Invocations
    {
        get
        {
            lock (sync)
            {
                return [.. invocations];
            }
        }
    }

    public void RegisterCommit(string head, string tree) =>
        treesByCommit[head] = tree;

    public void Reset()
    {
        lock (sync)
        {
            invocations.Clear();
        }
    }

    public int CountOf(string executable, string argument) =>
        Invocations.Count(invocation =>
            string.Equals(
                invocation.Executable,
                executable,
                StringComparison.Ordinal) &&
            invocation.Arguments.Contains(argument, StringComparer.Ordinal));

    public override Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        Action<string>? standardOutputLineReceived = null,
        TimeSpan? stallTimeout = null,
        IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        var argumentList = arguments.ToArray();
        var invocation = new ProcessInvocation(
            executable,
            argumentList,
            Path.GetFullPath(workingDirectory));
        lock (sync)
        {
            invocations.Add(invocation);
        }
        if (FailOn?.Invoke(invocation) == true)
        {
            throw new SimulatedCrashException(
                $"Simulated crash during '{executable} {string.Join(' ', argumentList)}'.");
        }
        var result = executable switch
        {
            "git" => RunGit(argumentList),
            "gh" => RunGitHub(argumentList),
            _ => throw new InvalidOperationException(
                $"The publication tests do not script '{executable}'.")
        };
        if (FailAfter?.Invoke(invocation) == true)
        {
            throw new SimulatedCrashException(
                $"Simulated crash after '{executable} {string.Join(' ', argumentList)}'.");
        }
        return Task.FromResult(result);
    }

    private ProcessResult RunGit(IReadOnlyList<string> arguments)
    {
        if (arguments.Contains("ls-remote", StringComparer.Ordinal))
        {
            var reference = arguments[^1];
            return string.IsNullOrEmpty(RemoteBranchHead)
                ? Success(string.Empty)
                : Success($"{RemoteBranchHead}\t{reference}\n");
        }
        if (arguments.Contains("push", StringComparer.Ordinal))
        {
            var refspec = arguments[^1];
            var separator = refspec.IndexOf(':');
            lock (sync)
            {
                lastPushedHead = separator > 0
                    ? refspec[..separator]
                    : refspec;
                RemoteBranchHead = lastPushedHead;
            }
            return Success(string.Empty);
        }
        if (arguments.Contains("rev-parse", StringComparer.Ordinal))
        {
            return Success(ResolveRevision(arguments[^1]) + "\n");
        }
        return Success(string.Empty);
    }

    private string ResolveRevision(string revision)
    {
        const string treeSuffix = "^{tree}";
        var wantsTree = revision.EndsWith(treeSuffix, StringComparison.Ordinal);
        var target = wantsTree
            ? revision[..^treeSuffix.Length]
            : revision;
        if (!treesByCommit.ContainsKey(target))
        {
            lock (sync)
            {
                target = lastPushedHead;
            }
        }
        if (!treesByCommit.TryGetValue(target, out var tree))
        {
            throw new InvalidOperationException(
                $"The publication tests have no scripted object for '{revision}'.");
        }
        return wantsTree ? tree : target;
    }

    private ProcessResult RunGitHub(IReadOnlyList<string> arguments)
    {
        if (arguments.Contains("auth", StringComparer.Ordinal))
        {
            return Success("scripted-token\n");
        }
        var repository = ReadOption(arguments, "--repo");
        if (arguments.Contains("list", StringComparer.Ordinal))
        {
            var head = ReadOption(arguments, "--head");
            var matches = PullRequests
                .Where(item =>
                    string.Equals(
                        item.Repository,
                        repository,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        item.HeadRefName,
                        head,
                        StringComparison.Ordinal))
                .ToArray();
            return Success(
                $"[{string.Join(",", matches.Select(Serialize))}]");
        }
        if (arguments.Contains("view", StringComparer.Ordinal))
        {
            var number = int.Parse(arguments[2]);
            var match = PullRequests.SingleOrDefault(item =>
                item.Number == number &&
                string.Equals(
                    item.Repository,
                    repository,
                    StringComparison.OrdinalIgnoreCase));
            return match is null
                ? new ProcessResult(
                    1,
                    string.Empty,
                    $"no pull request #{number} in {repository}")
                : Success(Serialize(match));
        }
        if (!arguments.Contains("create", StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"The publication tests do not script 'gh {string.Join(' ', arguments)}'.");
        }

        var branch = ReadOption(arguments, "--head");
        int next;
        lock (sync)
        {
            pullRequestNumbers.TryGetValue(repository, out var current);
            next = current + 1;
            pullRequestNumbers[repository] = next;
        }
        var url = $"https://github.com/{repository}/pull/{next}";
        PullRequests.Add(new FakePullRequest(
            url,
            next,
            "OPEN",
            branch,
            lastPushedHead,
            repository));
        return Success(url + "\n");
    }

    private static string Serialize(FakePullRequest pullRequest) =>
        string.Concat(
            "{\"url\":\"", pullRequest.Url,
            "\",\"number\":", pullRequest.Number.ToString(),
            ",\"state\":\"", pullRequest.State,
            "\",\"headRefName\":\"", pullRequest.HeadRefName,
            "\",\"headRefOid\":\"", pullRequest.HeadRefOid,
            "\",\"headRepository\":{\"nameWithOwner\":\"", pullRequest.Repository,
            "\"}}");

    private static string ReadOption(
        IReadOnlyList<string> arguments,
        string option)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], option, StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }
        throw new InvalidOperationException(
            $"The scripted GitHub CLI call is missing '{option}'.");
    }

    private static ProcessResult Success(string standardOutput) =>
        new(0, standardOutput, string.Empty);
}

/// <summary>Creates disposable real Git repositories for publication tests.</summary>
internal static class GitWorkspace
{
    public static void CreateRepository(
        string path,
        string remoteRepository,
        string branchName)
    {
        Directory.CreateDirectory(path);
        Run(path, "init", "--quiet", "--initial-branch", branchName);
        Run(path, "config", "user.email", "tests@example.invalid");
        Run(path, "config", "user.name", "Reviewed Publication Tests");
        if (!string.IsNullOrWhiteSpace(remoteRepository))
        {
            Run(
                path,
                "config",
                "remote.origin.url",
                $"https://github.com/{remoteRepository}.git");
        }
        File.WriteAllText(
            Path.Combine(path, "tracked.txt"),
            "reviewed content");
        Run(path, "add", "-A");
        Run(path, "commit", "--quiet", "-m", "reviewed");
    }

    public static void DeleteBestEffort(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
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
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Test cleanup only.
        }
    }

    private static void Run(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git.");
        var error = process.StandardError.ReadToEnd();
        _ = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed: {error}");
        }
    }
}

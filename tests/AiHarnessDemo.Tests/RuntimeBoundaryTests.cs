using System.ComponentModel;
using AiHarnessDemo.Api;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Infrastructure;
using AiHarnessDemo.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiHarnessDemo.Tests;

public sealed class CopilotJsonlParserTests
{
    [Fact]
    public void Parse_ToleratesUnknownLinesAndPersistsOnlyScrubbedToolArguments()
    {
        const string jsonl = """
            not-json
            {"type":"tool.execution_start","data":{"toolCallId":"one","toolName":"write","arguments":{"path":"secret.txt","token":"do-not-store"}}}
            {"type":"future.event","data":{"anything":true}}
            {"type":"tool.execution_complete","data":{"toolCallId":"one","success":true,"result":{"content":"done"}}}
            {"type":"assistant.message","data":{"content":"Decision\nDeliverable\nEvidence\nNext owner"}}
            {"type":"result","exitCode":0}
            """;

        var result = CopilotJsonlParser.Parse(jsonl);

        Assert.True(result.Success);
        Assert.Equal("Decision\nDeliverable\nEvidence\nNext owner", result.OutputSummary);
        var toolCall = Assert.Single(result.ToolCalls);
        Assert.Equal("write", toolCall.ToolName);
        Assert.Contains("path", toolCall.ArgumentsSummary);
        Assert.DoesNotContain("secret.txt", toolCall.ArgumentsSummary);
        Assert.DoesNotContain("do-not-store", toolCall.ArgumentsSummary);
    }

    [Fact]
    public void Parse_FailsClosedWhenNoAssistantHandoffExists()
    {
        var result = CopilotJsonlParser.Parse(
            """{"type":"result","exitCode":0}""");

        Assert.False(result.Success);
        Assert.Equal(AgentRunFailureKind.InvalidOutput, result.FailureKind);
    }

    [Fact]
    public void Parse_UsesCompletedRootDeltasWhenFinalMessageEventIsMissing()
    {
        const string jsonl = """
            {"type":"assistant.message_start","data":{"messageId":"root"}}
            {"type":"assistant.message_delta","data":{"messageId":"root","deltaContent":"INTAKE_STATUS: READY\n"}}
            {"type":"assistant.message_delta","data":{"messageId":"root","deltaContent":"CUSTOMER_REPLY: Ready.\nTASK_BRIEF: Refresh the site."}}
            {"type":"assistant.message","agentId":"child","data":{"messageId":"child","content":"Ignore child response."}}
            {"type":"assistant.turn_end","data":{"turnId":"0"}}
            {"type":"result"}
            """;

        var result = CopilotJsonlParser.Parse(jsonl);

        Assert.True(result.Success);
        Assert.Equal(
            "INTAKE_STATUS: READY\nCUSTOMER_REPLY: Ready.\nTASK_BRIEF: Refresh the site.",
            result.OutputSummary);
    }

    [Fact]
    public void Parse_ReportsSessionErrorsAndMarksRateLimitsTransient()
    {
        const string jsonl = """
            {"type":"session.error","data":{"errorType":"rate_limit","message":"Please retry shortly.","statusCode":429}}
            """;

        var result = CopilotJsonlParser.Parse(jsonl);

        Assert.False(result.Success);
        Assert.Equal(AgentRunFailureKind.Transient, result.FailureKind);
        Assert.Contains("rate_limit", result.Error);
        Assert.Contains("Please retry shortly.", result.Error);
    }

    [Fact]
    public void Parse_TreatsAnEarlyExitWithStandardErrorAsAmbiguous()
    {
        var result = CopilotJsonlParser.Parse(
            string.Empty,
            "\u001b[31;1mThe transport closed unexpectedly.\u001b[0m");

        Assert.False(result.Success);
        Assert.Equal(AgentRunFailureKind.AmbiguousCrash, result.FailureKind);
        Assert.Equal(
            "Copilot CLI ended without a complete assistant response. " +
            "The transport closed unexpectedly.",
            result.Error);
    }
}

public sealed class CopilotCliRuntimeTests
{
    [Fact]
    public async Task RefreshAsync_ValidatesTheProgrammaticCliContract()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-copilot-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var command = CreateCliShim(
                root,
                "--agent --allow-all-tools --available-tools --disable-builtin-mcps " +
                "--model --no-ask-user --no-custom-instructions " +
                "--no-eager-powershell-resolution --output-format");
            var status = await CreateRuntime(root).RefreshAsync(command);

            Assert.True(status.Ready);
            Assert.Equal("9.8.7", status.Version);
            Assert.True(
                string.Equals(
                    command,
                    status.ResolvedPath,
                    StringComparison.OrdinalIgnoreCase),
                $"Expected '{command}', resolved '{status.ResolvedPath}'.");
            Assert.Contains("non-interactive JSON execution", status.Detail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshAsync_RejectsACliWithoutJsonOutputSupport()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-copilot-capabilities-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var command = CreateCliShim(
                root,
                "--agent --allow-all-tools --model --no-ask-user");
            var status = await CreateRuntime(root).RefreshAsync(command);

            Assert.False(status.Ready);
            Assert.Contains("--output-format", status.Detail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshAsync_ReportsInstallationGuidanceWhenCliIsMissing()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-copilot-missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var status = await CreateRuntime(root).RefreshAsync(
                Path.Combine(root, "missing-copilot"));

            Assert.False(status.Ready);
            Assert.Empty(status.ResolvedPath);
            Assert.Contains("winget install GitHub.Copilot", status.Detail);
            Assert.Contains("npm install -g @github/copilot", status.Detail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CopilotCliRuntime CreateRuntime(string root) =>
        new(
            new ProcessRunner(),
            new HarnessPaths(
                root,
                Path.Combine(root, ".github", "agents"),
                Path.Combine(root, "harness.db")),
            TimeProvider.System,
            NullLogger<CopilotCliRuntime>.Instance);

    private static string CreateCliShim(string root, string helpOutput)
    {
        var command = Path.Combine(root, "copilot.cmd");
        File.WriteAllText(command, "@echo off\r\nexit /b 1\r\n");
        File.WriteAllText(
            Path.Combine(root, "copilot.ps1"),
            $$"""
              if ($args[0] -eq '--version') {
                  Write-Output 'GitHub Copilot CLI 9.8.7'
                  exit 0
              }
              if ($args[0] -eq 'help') {
                  Write-Output '{{helpOutput}}'
                  exit 0
              }
              exit 7
              """);
        return command;
    }
}

public sealed class LocalRequestGuardTests
{
    [Fact]
    public void IsValid_RequiresTheExactNonSimpleHeader()
    {
        var context = new DefaultHttpContext();

        Assert.False(LocalRequestGuard.IsValid(context.Request));

        context.Request.Headers[LocalRequestGuard.HeaderName] =
            LocalRequestGuard.HeaderValue;

        Assert.True(LocalRequestGuard.IsValid(context.Request));
    }
}

public sealed class RepositoryContextGateTests
{
    [Fact]
    public async Task Writer_WaitsForExistingReaderAndThenAcquiresExclusively()
    {
        var gate = new RepositoryContextGate();
        using var reader = await gate.EnterReadAsync();
        var writerTask = gate.EnterWriteAsync();

        await Task.Delay(40);
        Assert.False(writerTask.IsCompleted);

        reader.Dispose();
        using var writer = await writerTask.WaitAsync(TimeSpan.FromSeconds(2));
        var secondReaderTask = gate.EnterReadAsync();
        await Task.Delay(40);
        Assert.False(secondReaderTask.IsCompleted);

        writer.Dispose();
        using var secondReader = await secondReaderTask.WaitAsync(TimeSpan.FromSeconds(2));
    }
}

public sealed class ProcessRunnerTests
{
    [Fact]
    public void Resolve_SkipsInteractiveCopilotBootstrapperForLatestManagedCli()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-copilot-locator-{Guid.NewGuid():N}");
        var shimDirectory = Path.Combine(root, "interactive-shim");
        var managedRoot = Path.Combine(root, "managed-cli");
        var olderExecutable = Path.Combine(managedRoot, "1.0.79-9", "copilot.exe");
        var latestExecutable = Path.Combine(managedRoot, "1.0.80", "copilot.exe");
        Directory.CreateDirectory(shimDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(olderExecutable)!);
        Directory.CreateDirectory(Path.GetDirectoryName(latestExecutable)!);

        try
        {
            File.WriteAllText(
                Path.Combine(shimDirectory, "copilot.bat"),
                "@echo off\r\npwsh -File copilot.ps1 %*\r\n");
            File.WriteAllText(
                Path.Combine(shimDirectory, "copilot.ps1"),
                "$answer = Read-Host \"Install GitHub Copilot CLI? (y/N)\"");
            File.WriteAllText(olderExecutable, string.Empty);
            File.WriteAllText(latestExecutable, string.Empty);

            var resolved = ExecutableLocator.Resolve(
                "copilot",
                root,
                shimDirectory,
                managedRoot);

            Assert.Equal(latestExecutable, resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_PreservesNonInteractiveNpmCopilotShim()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-copilot-shim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var command = Path.Combine(root, "copilot.cmd");
        var powerShellShim = Path.Combine(root, "copilot.ps1");

        try
        {
            File.WriteAllText(command, "@echo off\r\nnode copilot.js %*\r\n");
            File.WriteAllText(powerShellShim, "& node copilot.js @args\r\n");

            var resolved = ExecutableLocator.Resolve(
                "copilot",
                root,
                root,
                managedCopilotRoot: null);

            Assert.True(
                string.Equals(command, resolved, StringComparison.OrdinalIgnoreCase),
                $"Expected '{command}', resolved '{resolved}'.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_LaunchesThePlatformCommandWithoutLosingArguments()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var command = Path.Combine(directory, "portable-command");
            if (OperatingSystem.IsWindows())
            {
                await File.WriteAllTextAsync(
                    command,
                    "#!/bin/sh\nexit 92\n");
                await File.WriteAllTextAsync(
                    $"{command}.cmd",
                    "@echo off\r\nexit /b 91\r\n");
                await File.WriteAllTextAsync(
                    $"{command}.ps1",
                    "Write-Output ($args[0] + '|' + $args[1])\r\n");
            }
            else
            {
                await File.WriteAllTextAsync(
                    command,
                    "#!/bin/sh\nprintf '%s|%s\\n' \"$1\" \"$2\"\n");
                File.SetUnixFileMode(
                    command,
                    File.GetUnixFileMode(command) |
                    UnixFileMode.UserExecute);
            }

            var longArgument = new string('x', 9_000) + "&%";
            var result = await new ProcessRunner().RunAsync(
                command,
                [longArgument, "two words"],
                directory,
                TimeSpan.FromSeconds(20));

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(
                $"{longArgument}|two words",
                result.StandardOutput.Trim());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_PassesEnvironmentOverridesToTheChildProcess()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-process-environment-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var command = Path.Combine(directory, "environment-command");
            if (OperatingSystem.IsWindows())
            {
                await File.WriteAllTextAsync(
                    $"{command}.cmd",
                    "@echo off\r\nexit /b 1\r\n");
                await File.WriteAllTextAsync(
                    $"{command}.ps1",
                    "Write-Output $env:AI_HARNESS_PROCESS_TEST\r\n");
            }
            else
            {
                await File.WriteAllTextAsync(
                    command,
                    "#!/bin/sh\nprintf '%s\\n' \"$AI_HARNESS_PROCESS_TEST\"\n");
                File.SetUnixFileMode(
                    command,
                    File.GetUnixFileMode(command) |
                    UnixFileMode.UserExecute);
            }

            var result = await new ProcessRunner().RunAsync(
                command,
                [],
                directory,
                TimeSpan.FromSeconds(20),
                environmentVariables: new Dictionary<string, string>
                {
                    ["AI_HARNESS_PROCESS_TEST"] = "isolated"
                });

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("isolated", result.StandardOutput.Trim());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_RejectsAnUnpairedWindowsBatchLauncher()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var command = Path.Combine(directory, "unsafe-command");
        await File.WriteAllTextAsync(
            $"{command}.cmd",
            "@echo off\r\necho %1\r\n");

        try
        {
            var exception = await Assert.ThrowsAsync<Win32Exception>(
                () => new ProcessRunner().RunAsync(
                    command,
                    ["%PATH%"],
                    directory,
                    TimeSpan.FromSeconds(20)));

            Assert.Contains("no runnable PowerShell companion", exception.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed class RepositoryBrowserTests
{
    [Fact]
    public void ListLocations_ReturnsExistingPortableEntryPoints()
    {
        var locations = RepositoryAnalyzer.ListLocations();
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var root = Path.GetPathRoot(Environment.CurrentDirectory)
                   ?? Path.DirectorySeparatorChar.ToString();

        Assert.NotEmpty(locations);
        Assert.All(locations, item => Assert.True(Directory.Exists(item.Path)));
        Assert.Contains(
            locations,
            item => string.Equals(item.Path, root, comparison));

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Directory.Exists(home))
        {
            Assert.Contains(
                locations,
                item => string.Equals(item.Path, home, comparison));
        }
    }

    [Fact]
    public void FindGitRepositories_TreatsNestedRepositoriesAsOneProject()
    {
        var project = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-project-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(Path.Combine(project, "site", ".git"));
            Directory.CreateDirectory(Path.Combine(project, "services", "data", ".git"));
            Directory.CreateDirectory(Path.Combine(project, "node_modules", "ignored", ".git"));

            var repositories = RepositoryAnalyzer.FindGitRepositories(project);

            Assert.Equal(
                ["services\\data", "site"],
                repositories
                    .Select(path => Path.GetRelativePath(project, path).Replace('/', '\\'))
                    .ToArray());
            Assert.True(RepositoryAnalyzer.IsProjectDirectory(project));
            Assert.False(RepositoryAnalyzer.IsGitRepository(project));
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }
}

public sealed class WorkspaceManagerTests
{
    [Fact]
    public async Task PrepareAsync_CreatesAFlowWorktreeForEveryProjectRepository()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ai-harness-workspace-{Guid.NewGuid():N}");
        var project = Path.Combine(root, "project");
        var workspaceRoot = Path.Combine(root, "workspaces");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(
            Path.Combine(root, "WORKFLOW.md"),
            """
            ---
            workspace:
              root: workspaces
            ---

            Test workflow.
            """);
        await File.WriteAllTextAsync(
            Path.Combine(project, "project-notes.txt"),
            "Shared project context.");

        var processRunner = new ProcessRunner();
        await CreateRepositoryAsync(
            processRunner,
            Path.Combine(project, "site"),
            "site.txt");
        await CreateRepositoryAsync(
            processRunner,
            Path.Combine(project, "data"),
            "data.txt");

        var paths = new HarnessPaths(
            root,
            Path.Combine(root, ".github", "agents"),
            Path.Combine(root, "harness.db"));
        var workflowProvider = new WorkflowDefinitionProvider(
            paths,
            new WorkflowLoader(),
            NullLogger<WorkflowDefinitionProvider>.Instance);
        await workflowProvider.StartAsync(CancellationToken.None);
        var hookRunner = new WorkspaceHookRunner(
            workflowProvider,
            processRunner,
            NullLogger<WorkspaceHookRunner>.Instance);
        var manager = new WorkspaceManager(
            processRunner,
            workflowProvider,
            hookRunner,
            NullLogger<WorkspaceManager>.Instance);
        var flow = new FlowRun
        {
            Title = "Update the project",
            OriginalRequest = "Update the project",
            RepositoryPath = project
        };

        try
        {
            var workspace = await manager.PrepareAsync(flow);

            Assert.True(workspace.CreatedNow);
            Assert.Equal(
                Path.Combine(workspaceRoot, flow.Id.ToString("N")[..16]),
                workspace.Path);
            Assert.True(File.Exists(Path.Combine(workspace.Path, "project-notes.txt")));
            Assert.True(File.Exists(Path.Combine(workspace.Path, "site", "site.txt")));
            Assert.True(File.Exists(Path.Combine(workspace.Path, "data", "data.txt")));
            Assert.Equal(
                workspace.BranchName,
                await CurrentBranchAsync(processRunner, Path.Combine(workspace.Path, "site")));
            Assert.Equal(
                workspace.BranchName,
                await CurrentBranchAsync(processRunner, Path.Combine(workspace.Path, "data")));

            var recovered = await manager.PrepareAsync(flow);
            Assert.False(recovered.CreatedNow);
            Assert.Equal(workspace.Path, recovered.Path);
        }
        finally
        {
            workflowProvider.Dispose();
            foreach (var repositoryName in new[] { "site", "data" })
            {
                var repositoryPath = Path.Combine(project, repositoryName);
                var repositoryWorkspace = Path.Combine(
                    workspaceRoot,
                    flow.Id.ToString("N")[..16],
                    repositoryName);
                if (Directory.Exists(repositoryPath) &&
                    Directory.Exists(repositoryWorkspace))
                {
                    await RunGitAsync(
                        processRunner,
                        repositoryPath,
                        ["worktree", "remove", "--force", repositoryWorkspace]);
                }
            }
            DeleteDirectory(root);
        }
    }

    private static async Task CreateRepositoryAsync(
        ProcessRunner processRunner,
        string repositoryPath,
        string fileName)
    {
        Directory.CreateDirectory(repositoryPath);
        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, fileName),
            "initial");
        await RunGitAsync(
            processRunner,
            repositoryPath,
            ["init", "--quiet"]);
        await RunGitAsync(
            processRunner,
            repositoryPath,
            ["add", "."]);
        await RunGitAsync(
            processRunner,
            repositoryPath,
            [
                "-c", "user.name=AI Harness Tests",
                "-c", "user.email=ai-harness@example.invalid",
                "commit", "--quiet", "-m", "Initial commit"
            ]);
    }

    private static async Task<string> CurrentBranchAsync(
        ProcessRunner processRunner,
        string repositoryPath)
    {
        var result = await RunGitAsync(
            processRunner,
            repositoryPath,
            ["branch", "--show-current"]);
        return result.StandardOutput.Trim();
    }

    private static async Task<ProcessResult> RunGitAsync(
        ProcessRunner processRunner,
        string repositoryPath,
        IReadOnlyList<string> arguments)
    {
        var result = await processRunner.RunAsync(
            "git",
            ["-C", repositoryPath, .. arguments],
            repositoryPath,
            TimeSpan.FromSeconds(30));
        Assert.True(result.ExitCode == 0, result.CombinedOutput);
        return result;
    }

    private static void DeleteDirectory(string path)
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
}

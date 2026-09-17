using System.Diagnostics;
using System.Text.Json;
using AiHarnessDemo.Services;

namespace AiHarnessDemo.Tests;

public sealed class CopilotHandoffWatchdogTests
{
    private const string Complete = "HANDOFF_STATUS: COMPLETE\nImplemented the required change.";

    [Fact]
    public async Task CompletedHandoff_BoundsShutdownWithoutWaitingForTheTaskTimeout()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), CopilotHandoffWatchdog.ShutdownGracePeriod);
        using var watchdog = Create(TimeSpan.FromMilliseconds(250));
        CompleteTurn(watchdog);
        Assert.True(watchdog.WaitingForExit);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Task.Delay(Timeout.InfiniteTimeSpan, watchdog.Token)
                .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(watchdog.Expired);
    }

    [Theory]
    [InlineData("assistant.turn_start")]
    [InlineData("assistant.message_start")]
    [InlineData("assistant.message_delta")]
    [InlineData("assistant.message")]
    [InlineData("tool.execution_start")]
    [InlineData("tool.execution_complete")]
    [InlineData("user.message")]
    [InlineData("session.resume")]
    [InlineData("session.error")]
    [InlineData("abort")]
    public void FurtherWorkOrAnError_DisarmsTheShutdownWatchdog(string type)
    {
        using var watchdog = Create();
        CompleteTurn(watchdog);
        watchdog.Observe(Event(type, new { turnId = "2", toolCallId = "tool-1" }));

        Assert.False(watchdog.WaitingForExit);
        Assert.False(watchdog.Token.IsCancellationRequested);
    }

    [Fact]
    public void ToolRequestOrAnOpenTurn_DoesNotCountAsACompletedHandoff()
    {
        using var watchdog = Create();
        watchdog.Observe(Event("assistant.turn_start", new { turnId = "1" }));
        watchdog.Observe(Event("assistant.message", new { content = Complete }));
        Assert.False(watchdog.WaitingForExit);
        watchdog.Observe(Event("assistant.message", new
        {
            content = Complete,
            toolRequests = new[] { new { toolCallId = "tool-1" } }
        }));
        watchdog.Observe(Event("assistant.turn_end", new { turnId = "1" }));

        Assert.False(watchdog.WaitingForExit);
    }

    [Fact]
    public void PendingToolOrWrongTurnEnd_CannotTriggerShutdown()
    {
        using var watchdog = Create();
        watchdog.Observe(Event("tool.execution_start", new { toolCallId = "tool-1" }));
        CompleteTurn(watchdog);
        Assert.False(watchdog.WaitingForExit);
        watchdog.Observe(Event("tool.execution_complete", new { toolCallId = "tool-1" }));
        watchdog.Observe(Event("assistant.turn_start", new { turnId = "2" }));
        watchdog.Observe(Event("assistant.message", new { content = Complete }));
        watchdog.Observe(Event("assistant.turn_end", new { turnId = "1" }));
        Assert.False(watchdog.WaitingForExit);
        watchdog.Observe(Event("assistant.turn_end", new { turnId = "2" }));

        Assert.True(watchdog.WaitingForExit);
    }

    [Theory]
    [InlineData("Working on the implementation.")]
    [InlineData("HANDOFF_STATUS: COMPLETE\nHANDOFF_STATUS: PUSHBACK")]
    [InlineData("")]
    public void IncompleteOrInvalidResponse_DoesNotTriggerShutdown(string content)
    {
        using var watchdog = Create();
        CompleteTurn(watchdog, content);

        Assert.False(watchdog.WaitingForExit);
    }

    [Fact]
    public void SubagentHandoff_DoesNotCloseTheRootTurn()
    {
        using var watchdog = Create();
        watchdog.Observe(Event("assistant.turn_start", new { turnId = "1" }));
        watchdog.Observe(Event("assistant.message", new { content = Complete }, "child"));
        watchdog.Observe(Event("assistant.turn_end", new { turnId = "1" }, "child"));

        Assert.False(watchdog.WaitingForExit);
    }

    [Fact]
    public void SubagentActivityAfterTheRootHandoff_DisarmsShutdown()
    {
        using var watchdog = Create();
        CompleteTurn(watchdog);
        watchdog.Observe(Event("assistant.message", new { content = "Still working." }, "child"));

        Assert.False(watchdog.WaitingForExit);
    }

    [Fact]
    public void UnidentifiedToolCalls_CannotBeAssumedComplete()
    {
        using var watchdog = Create();
        watchdog.Observe(Event("tool.execution_start", new { }));
        watchdog.Observe(Event("tool.execution_start", new { }));
        watchdog.Observe(Event("tool.execution_complete", new { }));
        CompleteTurn(watchdog);

        Assert.False(watchdog.WaitingForExit);
    }

    [Fact]
    public void SessionFailure_PreventsLaterMessagesFromArmingUntilResume()
    {
        using var watchdog = Create();
        watchdog.Observe(Event("session.shutdown", new { shutdownType = "error" }));
        CompleteTurn(watchdog);
        Assert.False(watchdog.WaitingForExit);
        watchdog.Observe(Event("session.resume", new { }));
        CompleteTurn(watchdog);

        Assert.True(watchdog.WaitingForExit);
    }

    [Fact]
    public void UsageCheckpoint_DoesNotExtendTheShutdownGracePeriod()
    {
        using var watchdog = Create();
        CompleteTurn(watchdog);
        watchdog.Observe(Event("session.usage_checkpoint", new { }));

        Assert.True(watchdog.WaitingForExit);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"type\":42}")]
    [InlineData("{\"type\":null}")]
    public void NonEventOutput_CannotStartShutdownOrBreakTheStream(string line)
    {
        using var watchdog = Create();
        watchdog.Observe(line);

        Assert.False(watchdog.WaitingForExit);
        Assert.False(watchdog.Token.IsCancellationRequested);
    }

    [Fact]
    public void CustomerCancellation_RemainsCancellationNotShutdownRecovery()
    {
        using var source = new CancellationTokenSource();
        using var watchdog = new CopilotHandoffWatchdog(IsComplete, source.Token);
        CompleteTurn(watchdog);
        source.Cancel();

        Assert.True(watchdog.Token.IsCancellationRequested);
        Assert.False(watchdog.Expired);
    }

    [Fact]
    public async Task ProcessRunner_StopsAHungProcessAndPreservesItsRecoverableJournal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"handoff-shutdown-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var script = Path.Combine(root, "completed-but-running.ps1");
        var pidFile = Path.Combine(root, "pid.txt");
        var journalFile = Path.Combine(root, "events.jsonl");
        var sessionId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var lines = new[]
        {
            Event("session.start", new { sessionId, context = new { cwd = root } }),
            Event("tool.execution_start", new
            {
                toolCallId = "verification",
                toolName = "powershell",
                arguments = new { command = "Write-Output passed" }
            }),
            Event("tool.execution_complete", new
            {
                toolCallId = "verification",
                success = true,
                result = new { content = "Focused verification passed." },
                exitCode = 0
            }),
            Event("assistant.turn_start", new { turnId = "1" }),
            Event("assistant.message", new { content = Complete, toolRequests = Array.Empty<object>() }),
            Event("assistant.turn_end", new { turnId = "1" })
        };
        await File.WriteAllTextAsync(
            script,
            $"[IO.File]::WriteAllText('{pidFile}', [string]$PID)\n" +
            string.Join("\n", lines.Select(line =>
                $"$line = '{line.Replace("'", "''")}'\n" +
                $"[IO.File]::AppendAllText('{journalFile}', $line + \"`n\")\n" +
                "Write-Output $line")) +
            "\nStart-Sleep -Seconds 120\n");
        try
        {
            using var watchdog = Create(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new ProcessRunner().RunAsync(
                    script, [], root, TimeSpan.FromSeconds(20),
                    watchdog.Token, watchdog.Observe));

            Assert.True(watchdog.Expired);
            var processId = int.Parse(await File.ReadAllTextAsync(pidFile));
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId));
            var snapshot = await CopilotSessionJournal.InspectDirectoryAsync(root, root, sessionId);
            Assert.Equal(CopilotSessionJournalState.Completed, snapshot.State);
            Assert.Equal(root, snapshot.WorkspacePath);
            Assert.Empty(snapshot.ActiveProcessIds);
            Assert.Equal(Complete, snapshot.Result!.OutputSummary);
            Assert.True(CopilotReasoningHost.IsRecoveryCurrent(startedAt, snapshot.CompletedAt));
            var evidence = Assert.Single(snapshot.Result.ToolCalls);
            Assert.True(evidence.Succeeded);
            Assert.Contains("Focused verification passed.", evidence.ResultSummary);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CopilotHandoffWatchdog Create(TimeSpan? gracePeriod = null) =>
        new(IsComplete, CancellationToken.None, gracePeriod);

    private static bool IsComplete(string output) =>
        CopilotReasoningHost.IsRecoverableCompletedOutput("software-engineer", output);

    private static void CompleteTurn(CopilotHandoffWatchdog watchdog, string content = Complete)
    {
        watchdog.Observe(Event("assistant.turn_start", new { turnId = "1" }));
        watchdog.Observe(Event("assistant.message", new { content }));
        watchdog.Observe(Event("assistant.turn_end", new { turnId = "1" }));
    }

    private static string Event(string type, object data, string? agentId = null) =>
        JsonSerializer.Serialize(new { type, data, agentId, timestamp = DateTimeOffset.UtcNow });
}

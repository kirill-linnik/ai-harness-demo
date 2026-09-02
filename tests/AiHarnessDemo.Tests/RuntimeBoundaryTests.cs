using AiHarnessDemo.Api;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Services;
using Microsoft.AspNetCore.Http;

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

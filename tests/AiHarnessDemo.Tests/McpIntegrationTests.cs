using System.Collections.Immutable;
using System.Text.Json;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Orchestration;
using AiHarnessDemo.Core.Security;
using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Infrastructure;
using AiHarnessDemo.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiHarnessDemo.Tests;

public sealed class McpIntegrationTests
{
    private const string Configuration = """
        {"mcpServers":{"inspection":{"type":"local","command":"node",
        "args":["server.js","--output-dir","reports"],
        "tools":["inspect","capture"]}}}
        """;

    [Fact]
    public void NativeConfiguration_IsGenericAndPreservesRuntimeFields()
    {
        using var fixture = new Fixture();
        var parsed = McpConfigurationParser.Parse(fixture.McpPath, Configuration);
        var server = Assert.Single(parsed.Servers);
        Assert.Equal("inspection", server.Name);
        using var native = JsonDocument.Parse(server.ConfigurationJson);
        Assert.Equal("node", native.RootElement.GetProperty("command").GetString());
        Assert.Equal("server.js", native.RootElement.GetProperty("args")[0].GetString());
        Assert.Equal("reports", native.RootElement.GetProperty("args")[2].GetString());
        Assert.Equal(new[] { "inspect", "capture" }, server.Tools);
    }

    [Fact]
    public void RemoteAndProviderSpecificFields_AreForwardedWithoutAnEngineSchema()
    {
        using var fixture = new Fixture("""
            {"mcpServers":{"Documentation":{"type":"http","url":"https://mcp.example.test/tools",
            "headers":{"X-Example":"${EXAMPLE_TOKEN}"},"providerOption":{"enabled":true},
            "tools":["search","read"]}}}
            """);
        var permission = fixture.Resolve(PlanDuty.Design);
        using var json = JsonDocument.Parse(McpExecutionConfiguration.Serialize(permission));
        var server = json.RootElement.GetProperty("mcpServers").GetProperty("Documentation");
        Assert.Equal("http", server.GetProperty("type").GetString());
        Assert.Equal("https://mcp.example.test/tools", server.GetProperty("url").GetString());
        Assert.Equal("${EXAMPLE_TOKEN}", server.GetProperty("headers").GetProperty("X-Example").GetString());
        Assert.True(server.GetProperty("providerOption").GetProperty("enabled").GetBoolean());
        Assert.Equal("search", server.GetProperty("tools")[0].GetString());
    }

    [Theory]
    [InlineData("""{"mcpServers":{"x":{"type":"local","command":"node","args":[],"tools":["*"]}}}""")]
    [InlineData("""{"mcpServers":{"x":{"type":"local","command":"node","args":[],"tools":[]}}}""")]
    [InlineData("""{"mcpServers":{"x":{"type":"local","command":"node","args":[]}}}""")]
    [InlineData("""{"mcpServers":{"x":{"type":"local","command":"node","command":"evil","args":[],"tools":["x"]}}}""")]
    [InlineData("""{"mcpServers":{"-invalid":{"type":"local","command":"node","args":[],"tools":["x"]}}}""")]
    [InlineData("""{"mcpServers":null}""")]
    public void InvalidOrImplicitToolConfiguration_FailsClosed(string json)
    {
        using var fixture = new Fixture(json);
        Assert.Throws<WorkflowConfigurationException>(() => new WorkflowLoader().Load(fixture.WorkflowPath));
    }

    [Fact]
    public async Task McpReload_IsAtomicAndChangesTheWorkflowRevision()
    {
        using var fixture = new Fixture();
        using var provider = fixture.Provider();
        var original = provider.GetEffective();
        File.WriteAllText(fixture.McpPath, Configuration.Replace("capture", "snapshot", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(fixture.McpPath, DateTime.UtcNow.AddSeconds(2));
        var changed = provider.GetEffective();
        Assert.NotEqual(original.Revision, changed.Revision);
        Assert.Contains("snapshot", Assert.Single(changed.Config.Copilot.Mcp.Servers).Tools);

        File.WriteAllText(fixture.McpPath, "{");
        await provider.ReloadAsync();
        Assert.False(provider.Status().Ready);
        Assert.Equal(changed.Revision, provider.GetEffective().Revision);
        Assert.Contains("MCP configuration", provider.Status().CurrentFileError);

        File.WriteAllText(fixture.McpPath, Configuration);
        File.SetLastWriteTimeUtc(fixture.McpPath, DateTime.UtcNow.AddSeconds(4));
        Assert.True(provider.Status().Ready);
        Assert.Equal(original.Revision, provider.GetEffective().Revision);
    }

    [Fact]
    public async Task MissingMcpFile_BlocksNewWorkWithoutLosingEffectiveDefinition()
    {
        using var fixture = new Fixture();
        using var provider = fixture.Provider();
        var original = provider.GetEffective();
        File.Delete(fixture.McpPath);
        await provider.ReloadAsync();
        Assert.False(provider.Status().Ready);
        Assert.Equal(original.Revision, provider.GetEffective().Revision);
    }

    [Fact]
    public void CorrectingANewInvalidConfigPath_RecoversWithoutAnotherWorkflowEdit()
    {
        using var fixture = new Fixture();
        using var provider = fixture.Provider();
        var original = provider.GetEffective();
        var replacement = Path.Combine(fixture.Root, "replacement.json");
        File.WriteAllText(replacement, "{");
        File.WriteAllText(fixture.WorkflowPath, File.ReadAllText(fixture.WorkflowPath)
            .Replace("mcp-config.json", "replacement.json", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(fixture.WorkflowPath, DateTime.UtcNow.AddSeconds(2));
        Assert.False(provider.Status().Ready);
        Assert.Equal(original.Revision, provider.GetEffective().Revision);

        File.WriteAllText(replacement, Configuration.Replace("capture", "snapshot", StringComparison.Ordinal));

        Assert.True(provider.Status().Ready);
        Assert.Equal(replacement, provider.GetEffective().Config.Copilot.Mcp.SourcePath);
        Assert.Contains("snapshot", Assert.Single(provider.GetEffective().Config.Copilot.Mcp.Servers).Tools);
    }

    [Theory]
    [InlineData(PlanDuty.Analyze)]
    [InlineData(PlanDuty.Design)]
    [InlineData(PlanDuty.Implement)]
    [InlineData(PlanDuty.Verify)]
    public void DeliveryWorkers_GetConfiguredToolsWithoutRoleIdentityChecks(PlanDuty duty)
    {
        using var fixture = new Fixture();
        var permission = fixture.Resolve(duty);
        Assert.Single(permission.McpServers);
        Assert.Contains("inspection-inspect", permission.AllowedTools);
        Assert.Contains("inspection-capture", permission.AllowedTools);
        Assert.True(permission.DisableBuiltinMcps);
        Assert.Contains("shell(git push)", permission.DeniedTools);
        if (duty is PlanDuty.Analyze or PlanDuty.Design)
        {
            Assert.Equal(ExecutionPermissionProfile.ReadOnlySource, permission.Profile);
            Assert.Contains("shell", permission.DeniedTools);
            Assert.Contains("write", permission.DeniedTools);
        }
    }

    [Theory]
    [InlineData(ExecutionInvocationKind.Intake)]
    [InlineData(ExecutionInvocationKind.Planning)]
    [InlineData(ExecutionInvocationKind.PreMortem)]
    [InlineData(ExecutionInvocationKind.BlockerExplanation)]
    public void LifecycleInvocations_DoNotGainMcpTools(ExecutionInvocationKind kind)
    {
        using var fixture = new Fixture();
        var permission = new PermissionProfileResolver().Resolve(
            Request(PlanDuty.Design) with { InvocationKind = kind }, fixture.Restrictions());
        Assert.Empty(permission.McpServers);
        Assert.DoesNotContain(permission.AllowedTools, tool => tool.StartsWith("inspection-", StringComparison.Ordinal));
    }

    [Fact]
    public void AdvisoryAndResponseCorrection_DoNotGainMcpTools()
    {
        using var fixture = new Fixture();
        var advisory = new PermissionProfileResolver().Resolve(
            Request(PlanDuty.Design) with { FlowKind = FlowKind.Advisory }, fixture.Restrictions());
        Assert.Empty(advisory.McpServers);
        var correction = PermissionProfileResolver.ForResponseCorrection(fixture.Resolve(PlanDuty.Verify));
        Assert.Empty(correction.McpServers);
        Assert.Equal(new[] { "view", "grep", "glob" }, correction.AllowedTools);
    }

    [Fact]
    public void WorkflowDenials_RemoveToolsFromBothVisibilityAndServerConfig()
    {
        using var fixture = new Fixture();
        var restrictions = fixture.Restrictions();
        restrictions = restrictions with
        {
            AdditionalDeniedTools = restrictions.AdditionalDeniedTools.SetItem(
                ExecutionPermissionProfile.ReadOnlySource, ["inspection(capture)"])
        };
        var permission = new PermissionProfileResolver().Resolve(Request(PlanDuty.Design), restrictions);
        Assert.Equal(new[] { "inspect" }, Assert.Single(permission.McpServers).Tools);
        Assert.DoesNotContain("inspection-capture", permission.AllowedTools);
    }

    [Fact]
    public void PersistedPermission_RoundTripsAndTighteningNeverAddsOrReplacesServers()
    {
        using var fixture = new Fixture();
        var original = fixture.Resolve(PlanDuty.Verify);
        var persisted = JsonSerializer.Deserialize<EffectiveExecutionPermission>(JsonSerializer.Serialize(original))!;
        Assert.True(PermissionProfileResolver.Equivalent(original, persisted));
        File.WriteAllText(fixture.McpPath, Configuration.Replace("node", "different-command", StringComparison.Ordinal));
        var current = fixture.Resolve(PlanDuty.Verify);
        var tightened = PermissionProfileResolver.Tighten(persisted, current);
        Assert.Empty(tightened.McpServers);
        Assert.DoesNotContain(tightened.AllowedTools, tool => tool.StartsWith("inspection-", StringComparison.Ordinal));
        using var native = JsonDocument.Parse(Assert.Single(persisted.McpServers).ConfigurationJson);
        Assert.Equal("node", native.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public async Task StagingAndCliArguments_UseNativeConfigAndExactToolApprovals()
    {
        using var fixture = new Fixture();
        var permission = fixture.Resolve(PlanDuty.Design);
        var stager = new AgentManifestStager();
        var manifest = new StagedAgentManifest(Path.Combine(fixture.Root, "owned-context"), "designer");
        Directory.CreateDirectory(manifest.Root);
        var flow = Guid.NewGuid();
        var step = Guid.NewGuid();
        var staged = await McpExecutionConfiguration.StageAsync(
            permission, stager, manifest, flow, step, 1);
        Assert.NotNull(staged);
        using var json = JsonDocument.Parse(File.ReadAllText(staged.Path));
        var server = json.RootElement.GetProperty("mcpServers").GetProperty("inspection");
        Assert.Equal("local", server.GetProperty("type").GetString());
        Assert.Equal("reports", server.GetProperty("args")[2].GetString());
        Assert.False(server.TryGetProperty("access", out _));

        var args = CopilotReasoningHost.BuildCliArguments(
            fixture.Workspace, manifest.Root, "designer", "model", "high", Guid.NewGuid(),
            "Review the candidate.", permission, mcpConfigPath: staged.Path);
        Assert.Contains($"--additional-mcp-config=@{staged.Path}", args);
        Assert.Contains("--enable-mcp-server=inspection", args);
        Assert.Contains("--allow-tool=inspection(inspect)", args);
        Assert.Contains("--allow-tool=inspection(capture)", args);
        Assert.Contains("--deny-tool=write,shell", args);
        Assert.DoesNotContain("--allow-all-tools", args);
        var repeated = await McpExecutionConfiguration.StageAsync(
            permission, stager, manifest, flow, step, 1);
        Assert.Equal(staged, repeated);
        Assert.False(Directory.Exists(Path.Combine(fixture.Workspace, "mcp-artifacts")));
    }

    [Fact]
    public void AuthorizedMcpToolsWithoutConfiguration_CannotLaunch()
    {
        using var fixture = new Fixture();
        Assert.Throws<InvalidOperationException>(() => CopilotReasoningHost.BuildCliArguments(
            fixture.Workspace, fixture.Root, "agent", "model", "high", Guid.NewGuid(), "Inspect.",
            fixture.Resolve(PlanDuty.Design)));
    }

    [Fact]
    public void McpToolFailures_AreNotPositiveReadinessEvidence()
    {
        var result = CopilotJsonlParser.Parse("""
            {"type":"tool.execution_start","data":{"toolCallId":"mcp-1","toolName":"inspection-inspect","arguments":{"url":"http://127.0.0.1:1234"}}}
            {"type":"tool.execution_complete","data":{"toolCallId":"mcp-1","success":true,"result":{"isError":true,"content":[{"type":"text","text":"Browser launch failed."}]}}}
            {"type":"assistant.message","data":{"content":"Inspection blocked."}}
            {"type":"assistant.turn_end","data":{}}
            """);
        var call = Assert.Single(result.ToolCalls);
        Assert.False(call.Succeeded);
        Assert.Equal("Browser launch failed.", call.ResultSummary);
        Assert.StartsWith("sha256:", call.ResultDigest);
    }

    [Fact]
    public void McpScreenshotEvidence_RetainsDigestWithoutBase64InSummary()
    {
        var result = CopilotJsonlParser.Parse("""
            {"type":"tool.execution_start","data":{"toolCallId":"mcp-2","toolName":"playwright-browser_take_screenshot","arguments":{}}}
            {"type":"tool.execution_complete","data":{"toolCallId":"mcp-2","success":true,"result":{"content":[{"type":"image","data":"sensitive-image-bytes","mimeType":"image/png"},{"type":"text","text":"Captured the candidate at 390px."}]}}}
            {"type":"assistant.message","data":{"content":"Inspected."}}
            {"type":"assistant.turn_end","data":{}}
            """);
        var call = Assert.Single(result.ToolCalls);
        Assert.True(call.Succeeded);
        Assert.Equal("Browser", call.ToolType);
        Assert.Equal("Captured the candidate at 390px.", call.ResultSummary);
        Assert.DoesNotContain("sensitive-image-bytes", call.ResultSummary);
    }

    private static PermissionResolutionRequest Request(PlanDuty duty) =>
        new(FlowKind.Delivery, ExecutionInvocationKind.Worker, PlanStage.BeforeReview,
            [duty], null, false, false);

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"harness-mcp-test-{Guid.NewGuid():N}");
        public string McpPath => Path.Combine(Root, "mcp-config.json");
        public string WorkflowPath => Path.Combine(Root, "WORKFLOW.md");
        public string Workspace => Path.Combine(Root, "workspace");

        public Fixture(string configuration = Configuration)
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Workspace);
            File.WriteAllText(McpPath, configuration);
            File.WriteAllText(WorkflowPath, """
                ---
                copilot:
                  mcp_config_file: mcp-config.json
                ---
                Work on {{ task }}.
                """);
        }

        public WorkflowPermissionRestrictions Restrictions() =>
            PermissionProfileResolver.FromWorkflow(new WorkflowLoader().Load(WorkflowPath));

        public EffectiveExecutionPermission Resolve(PlanDuty duty) =>
            new PermissionProfileResolver().Resolve(Request(duty), Restrictions());

        public WorkflowDefinitionProvider Provider() =>
            new(new HarnessPaths(Root, Path.Combine(Root, ".github", "agents"), Path.Combine(Root, "test.db")),
                new WorkflowLoader(), NullLogger<WorkflowDefinitionProvider>.Instance);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}

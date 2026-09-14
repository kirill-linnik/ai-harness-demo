using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Data;
using AiHarnessDemo.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AiHarnessDemo.Tests;

public sealed class DemoListenerInspectorTests
{
    [Fact]
    public void DescendantListener_IsOwned()
    {
        var result = DemoListenerInspector.Evaluate(
            10,
            [new DemoListenerInspector.ListenerOwner(12, true, false)],
            new Dictionary<int, int>
            {
                [12] = 11,
                [11] = 10
            });

        Assert.Equal(DemoListenerOwnership.Owned, result.Ownership);
    }

    [Fact]
    public void ForeignOrMixedListener_IsNotOwned()
    {
        var foreign = DemoListenerInspector.Evaluate(
            10,
            [new DemoListenerInspector.ListenerOwner(20, true, false)],
            new Dictionary<int, int> { [20] = 1, [1] = 0 });
        var mixed = DemoListenerInspector.Evaluate(
            10,
            [
                new DemoListenerInspector.ListenerOwner(10, true, false),
                new DemoListenerInspector.ListenerOwner(20, true, false)
            ],
            new Dictionary<int, int> { [20] = 1, [1] = 0 });

        Assert.Equal(DemoListenerOwnership.NotOwned, foreign.Ownership);
        Assert.Equal(DemoListenerOwnership.NotOwned, mixed.Ownership);
    }

    [Fact]
    public void WildcardOrMissingAncestry_FailsClosed()
    {
        var wildcard = DemoListenerInspector.Evaluate(
            10,
            [new DemoListenerInspector.ListenerOwner(10, false, true)],
            new Dictionary<int, int>());
        var missing = DemoListenerInspector.Evaluate(
            10,
            [new DemoListenerInspector.ListenerOwner(12, true, false)],
            new Dictionary<int, int>());

        Assert.Equal(DemoListenerOwnership.NotOwned, wildcard.Ownership);
        Assert.Equal(
            DemoListenerOwnership.Unverifiable,
            missing.Ownership);
    }

    [Fact]
    public async Task PlatformInspector_RecognizesCurrentProcessLoopbackListener()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var process = Process.GetCurrentProcess();
        var record = new DemoInstanceRecord
        {
            ProcessId = process.Id,
            ProcessStartIdentity =
                process.StartTime.ToUniversalTime().Ticks,
            ProcessName = process.ProcessName
        };
        var launcher = new SystemDemoProcessLauncher();

        var result = await launcher.VerifyLoopbackListenerAsync(
            record,
            ((IPEndPoint)listener.LocalEndpoint).Port,
            CancellationToken.None);

        Assert.Equal(DemoListenerOwnership.Owned, result.Ownership);
    }

    [Fact]
    public async Task PlatformInspector_IgnoresSeparateIpv6ListenerOnSamePort()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        using var ipv4 = new TcpListener(IPAddress.Loopback, 0);
        ipv4.Server.ExclusiveAddressUse = true;
        ipv4.Start();
        var port = ((IPEndPoint)ipv4.LocalEndpoint).Port;
        using var ipv6 = new TcpListener(IPAddress.IPv6Loopback, port);
        ipv6.Server.SetSocketOption(
            SocketOptionLevel.IPv6,
            SocketOptionName.IPv6Only,
            true);
        ipv6.Server.ExclusiveAddressUse = true;
        ipv6.Start();
        using var process = Process.GetCurrentProcess();
        var record = new DemoInstanceRecord
        {
            ProcessId = process.Id,
            ProcessStartIdentity =
                process.StartTime.ToUniversalTime().Ticks,
            ProcessName = process.ProcessName
        };
        var launcher = new SystemDemoProcessLauncher();

        var result = await launcher.VerifyLoopbackListenerAsync(
            record,
            port,
            CancellationToken.None);

        Assert.Equal(DemoListenerOwnership.Owned, result.Ownership);
    }
}

public sealed class CustomerDemoManifestParserTests
{
    [Fact]
    public void StrictContract_AcceptsStructuredApprovedProfile()
    {
        var manifest = CustomerDemoManifestParser.Parse(
            Encoding.UTF8.GetBytes(ValidJson()));

        Assert.Equal("eu", manifest.ArtifactId);
        Assert.Equal(
            ["run", "preview", "--", "--host", "127.0.0.1", "--port", "{port}"],
            manifest.Arguments);
    }

    [Theory]
    [InlineData(
        """{"ArtifactId":"eu","LaunchProfile":"npm","WorkingDirectory":"","Arguments":["run","preview","{port}"],"HealthPath":"/","StartupTimeoutSeconds":5,"Unknown":true}""",
        "Unknown")]
    [InlineData(
        """{"Version":"customer-demo-v1","ArtifactId":"eu","LaunchProfile":"npm","WorkingDirectory":"","Arguments":["run","preview","{port}"],"HealthPath":"/","StartupTimeoutSeconds":5}""",
        "Version")]
    [InlineData(
        """{"artifactId":"eu","LaunchProfile":"npm","WorkingDirectory":"","Arguments":["run","preview","{port}"],"HealthPath":"/","StartupTimeoutSeconds":5}""",
        "artifactId")]
    [InlineData(
        """{"ArtifactId":"eu","LaunchProfile":"shell","WorkingDirectory":"","Arguments":["serve","{port}"],"HealthPath":"/","StartupTimeoutSeconds":5}""",
        "not approved")]
    [InlineData(
        """{"ArtifactId":"eu","LaunchProfile":"npm","WorkingDirectory":"","Arguments":["run","preview","--","--host","0.0.0.0","--port","{port}"],"HealthPath":"/","StartupTimeoutSeconds":5}""",
        "127.0.0.1")]
    [InlineData(
        """{"ArtifactId":"eu","LaunchProfile":"npm","WorkingDirectory":"../outside","Arguments":["run","preview","{port}"],"HealthPath":"/","StartupTimeoutSeconds":5}""",
        "traversal")]
    [InlineData(
        """{"ArtifactId":"eu","LaunchProfile":"npm","WorkingDirectory":"","Arguments":["run","preview"],"HealthPath":"/","StartupTimeoutSeconds":5}""",
        "exactly one")]
    [InlineData(
        """{"ArtifactId":"eu","LaunchProfile":"npm","WorkingDirectory":"","Arguments":["run","{port}","{port}"],"HealthPath":"/","StartupTimeoutSeconds":5}""",
        "exactly one")]
    public void StrictContract_RejectsUnsafeOrAmbiguousInput(
        string json,
        string expected)
    {
        var error = Assert.Throws<CustomerDemoContractException>(() =>
            CustomerDemoManifestParser.Parse(Encoding.UTF8.GetBytes(json)));

        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StrictContract_RejectsDuplicateProperties()
    {
        var json =
            """{"ArtifactId":"eu","ArtifactId":"eu","LaunchProfile":"npm","WorkingDirectory":"","Arguments":["run","preview","{port}"],"HealthPath":"/","StartupTimeoutSeconds":5}""";

        var error = Assert.Throws<CustomerDemoContractException>(() =>
            CustomerDemoManifestParser.Parse(Encoding.UTF8.GetBytes(json)));

        Assert.Contains("more than once", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LaunchPolicy_RemovesPackageAcquisitionAndAcceptsOnlyStrictProfiles()
    {
        Assert.DoesNotContain("npx", CustomerDemoLaunchPolicy.Names);
        CustomerDemoLaunchPolicy.ValidateArguments(
            "npm",
            ["run", "preview:customer", "--", "--host", "127.0.0.1", "--port", "{port}"]);
        CustomerDemoLaunchPolicy.ValidateArguments(
            "python",
            ["-m", "http.server", "{port}", "--bind", "127.0.0.1"]);
        CustomerDemoLaunchPolicy.ValidateArguments(
            "dotnet",
            ["run", "--", "--urls", "http://127.0.0.1:{port}"]);
    }

    [Theory]
    [MemberData(nameof(UnsafeLaunchArguments))]
    public void LaunchPolicy_RejectsHostSwitchesPathsAndTraversal(
        string profile,
        string[] arguments)
    {
        Assert.Throws<CustomerDemoContractException>(() =>
            CustomerDemoLaunchPolicy.ValidateArguments(profile, arguments));
    }

    public static TheoryData<string, string[]> UnsafeLaunchArguments() => new()
    {
        { "npm", ["--prefix", "outside", "run", "preview", "{port}"] },
        { "npm", ["run", "--prefix", "--", "127.0.0.1", "{port}"] },
        { "npm", ["run", "preview", "--prefix", "outside", "{port}"] },
        { "npm", ["run", "preview", "--", "--script-shell=cmd.exe", "{port}"] },
        { "npm", ["run", "preview", "--", "--node-options=--require=x", "{port}"] },
        { "npm", ["run", "preview", "--", "../outside", "127.0.0.1", "{port}"] },
        { "npm", ["run", "preview", "--", "/tmp/outside", "127.0.0.1", "{port}"] },
        { "python", ["-m", "http.server", "{port}", "--bind", "127.0.0.1", "--directory", "../outside"] },
        { "python", ["-m", "http.server", "{port}", "--bind", "127.0.0.1", "/tmp"] },
        { "dotnet", ["run", "--project", "../outside.csproj", "--", "--urls", "http://127.0.0.1:{port}"] },
        { "dotnet", ["run", "--project=C:/outside.csproj", "--", "--urls", "http://127.0.0.1:{port}"] },
        { "dotnet", ["run", "--launch-profile", "unsafe", "--", "--urls", "http://127.0.0.1:{port}"] },
        { "dotnet", ["run", "--", "--contentRoot", "../outside", "--urls", "http://127.0.0.1:{port}"] }
    };

    [Theory]
    [InlineData("--host=::", "--origin=http://127.0.0.1")]
    [InlineData("--host", "0.0.0.0")]
    [InlineData("--host=[::]", "--port={port}")]
    [InlineData("--origin=http://127.0.0.1", "--port={port}")]
    [InlineData("--host=127.0.0.1", "--host=127.0.0.1")]
    [InlineData("--host=127.0.0.1", "--bind=0.0.0.0")]
    [InlineData("--hostname=127.0.0.1", "--port={port}")]
    [InlineData("--interface=127.0.0.1", "--port={port}")]
    public void NpmProfile_RejectsAmbiguousOrNonLoopbackBinding(
        params string[] applicationArguments)
    {
        var arguments = new[] { "run", "preview", "--" }
            .Concat(applicationArguments)
            .ToArray();
        if (!arguments.Any(argument =>
                argument.Contains("{port}", StringComparison.Ordinal)))
        {
            arguments = [.. arguments, "--port={port}"];
        }

        Assert.Throws<CustomerDemoContractException>(() =>
            CustomerDemoLaunchPolicy.ValidateArguments("npm", arguments));
    }

    [Fact]
    public void NpmProfile_AcceptsEqualsFormExactLoopbackBinding()
    {
        CustomerDemoLaunchPolicy.ValidateArguments(
            "npm",
            ["run", "preview", "--", "--host=127.0.0.1", "--port={port}"]);
    }

    [Fact]
    public void DotNetProject_MustResolveBeneathWorkingDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"demo-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var project = Path.Combine(root, "Demo.csproj");
        File.WriteAllText(project, "<Project />");
        try
        {
            CustomerDemoLaunchPolicy.ValidateResolvedArguments(
                "dotnet",
                ["run", "--project", "Demo.csproj", "--", "--urls", "http://127.0.0.1:{port}"],
                root);
            Assert.Throws<CustomerDemoContractException>(() =>
                CustomerDemoLaunchPolicy.ValidateResolvedArguments(
                    "dotnet",
                    ["run", "--project", "missing.csproj", "--", "--urls", "http://127.0.0.1:{port}"],
                    root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string ValidJson() =>
        """{"ArtifactId":"eu","LaunchProfile":"npm","WorkingDirectory":"","Arguments":["run","preview","--","--host","127.0.0.1","--port","{port}"],"HealthPath":"/health","StartupTimeoutSeconds":5}""";
}

public sealed class SealedDemoManifestServiceTests
{
    [Fact]
    public async Task Resolver_UsesExactCandidateBoundManifestBytesAndHash()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"sealed-demo-{Guid.NewGuid():N}");
        var artifactRoot = Path.Combine(root, ".customer-preview", "eu");
        Directory.CreateDirectory(artifactRoot);
        var bytes = Encoding.UTF8.GetBytes(
            """{"ArtifactId":"eu","LaunchProfile":"npm","WorkingDirectory":"","Arguments":["run","preview","--","--host","127.0.0.1","--port","{port}"],"HealthPath":"/health","StartupTimeoutSeconds":5}""");
        var path = Path.Combine(artifactRoot, SealedDemoManifestService.ManifestFileName);
        await File.WriteAllBytesAsync(path, bytes);
        var digest =
            "sha256:" +
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var ownerId = Guid.NewGuid();
        var outcomeJson = """{}""";
        var flow = new FlowRun
        {
            Title = "Sealed demo",
            OriginalRequest = "Run it",
            ConsolidatedRequest = "Run it",
            Kind = FlowKind.Delivery,
            WorkspacePath = root,
            OutcomeOwnerPlanStepKey = "package",
            OutcomeContractJson = outcomeJson
        };
        var candidateFingerprint = Sha('c');
        var identity = new ReviewedCandidateIdentity(
            flow.Id,
            flow.Iteration,
            ownerId,
            "package",
            OutcomeVerificationRules.ComputeSha256(outcomeJson),
            Sha('a'),
            candidateFingerprint,
            0,
            0,
            1,
            bytes.Length,
            [
                new ReviewedCandidateRepositoryIdentity(
                    ".",
                    new string('1', 40),
                    new string('2', 40),
                    string.Empty)
            ],
            DateTimeOffset.UtcNow);
        flow.Events.Add(new FlowEvent
        {
            FlowRunId = flow.Id,
            FlowStepId = ownerId,
            Type = ReviewedCandidateLedger.EventType,
            DataJson = ReviewedCandidateLedger.Serialize(identity),
            Message = "sealed"
        });
        var snapshot = new OutcomeCandidateSnapshot(
            new CandidateManifest(
                flow.Iteration,
                Sha('a'),
                [],
                [],
                [
                    new CandidatePreviewArtifact(
                        ".customer-preview/eu/customer-demo.json",
                        bytes.Length,
                        digest)
                ]),
            candidateFingerprint,
            ownerId,
            DateTimeOffset.UtcNow);
        var service = new SealedDemoManifestService(
            new FixedReviewedCandidateService(snapshot));
        try
        {
            var binding = await service.ResolveAsync(flow, "eu");

            Assert.NotNull(binding);
            Assert.Equal(candidateFingerprint, binding.CandidateFingerprint);
            Assert.Equal(digest, binding.ManifestHash);

            await File.WriteAllTextAsync(path, "{}");
            await Assert.ThrowsAsync<CandidateValidationException>(() =>
                service.ResolveAsync(flow, "eu"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Sha(char value) => $"sha256:{new string(value, 64)}";

    private sealed class FixedReviewedCandidateService(
        OutcomeCandidateSnapshot snapshot) : IReviewedCandidateService
    {
        public Task<ReviewedCandidateIdentity> SealAsync(
            FlowRun flow,
            Guid outcomeOwnerStepId,
            string outcomeOwnerPlanStepKey,
            string outcomeContractJson,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OutcomeCandidateSnapshot> VerifyAsync(
            FlowRun flow,
            ReviewedCandidateIdentity identity,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);

        public Task<OutcomeCandidateSnapshot> VerifyPreviewAsync(
            FlowRun flow,
            ReviewedCandidateIdentity identity,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
    }
}

public sealed class DemoRuntimeManagerTests
{
    [Fact]
    public async Task StatusLookup_SplitsFlowLedgerCollectionsAcrossQueries()
    {
        var commands = new RecordingCommandInterceptor();
        await using var fixture =
            await RuntimeFixture.CreateAsync(commandInterceptor: commands);
        commands.Clear();

        await fixture.Manager.GetStatusAsync(fixture.FlowId, "eu");

        Assert.Contains(
            commands.Commands,
            command => command.Contains(
                "\"FlowEvents\"",
                StringComparison.Ordinal));
        Assert.Contains(
            commands.Commands,
            command => command.Contains(
                "\"FlowSteps\"",
                StringComparison.Ordinal));
        Assert.Contains(
            commands.Commands,
            command => command.Contains(
                "\"GateRecords\"",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            commands.Commands,
            command =>
                command.Contains("\"FlowEvents\"", StringComparison.Ordinal) &&
                command.Contains("\"FlowSteps\"", StringComparison.Ordinal) &&
                command.Contains("\"GateRecords\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrentDemos_ReceiveDistinctPorts()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();

        var results = await Task.WhenAll(
            fixture.Manager.StartAsync(
                fixture.FlowId,
                "eu",
                "sha256:candidate",
                "sha256:manifest-eu"),
            fixture.Manager.StartAsync(
                fixture.FlowId,
                "ee",
                "sha256:candidate",
                "sha256:manifest-ee"));

        Assert.All(results, item => Assert.Equal(DemoInstanceState.Running, item.State));
        await using var database = await fixture.Factory.CreateDbContextAsync();
        var ports = await database.DemoInstances
            .Select(item => item.AssignedPort)
            .ToListAsync();
        Assert.Equal(2, ports.Distinct().Count());
    }

    [Fact]
    public async Task HealthyForeignListener_FailsClosedAndStopsOwnedRoot()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        fixture.Processes.EnqueueListenerOwnership(
            DemoListenerOwnership.NotOwned,
            "Synthetic foreign listener.");

        var error = await Assert.ThrowsAsync<DemoRuntimeException>(() =>
            fixture.Manager.StartAsync(
                fixture.FlowId,
                "eu",
                "sha256:candidate",
                "sha256:manifest-eu"));

        Assert.Equal(DemoConflictCodes.NotOwned, error.Code);
        Assert.Equal(1, fixture.Processes.StopCount);
        Assert.Equal(0, fixture.Processes.OwnedCount);
        await using var database = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(
            DemoInstanceState.Failed,
            (await database.DemoInstances.SingleAsync()).State);
    }

    [Fact]
    public async Task OccupiedFirstPort_FallsForward()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        using var occupied = new TcpListener(IPAddress.Loopback, fixture.Options.FirstPort);
        occupied.Server.ExclusiveAddressUse = true;
        occupied.Start();

        var status = await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");

        await using var database = await fixture.Factory.CreateDbContextAsync();
        var record = await database.DemoInstances.SingleAsync();
        Assert.Equal(DemoInstanceState.Running, status.State);
        Assert.NotEqual(fixture.Options.FirstPort, record.AssignedPort);
    }

    [Fact]
    public async Task StaleReconciliation_AllowsExplicitRestartWithoutAutoStart()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var running = await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        fixture.Processes.ForgetAll();

        await fixture.Manager.ReconcileAsync();
        Assert.Equal(
            DemoInstanceState.Stopped,
            (await fixture.Manager.GetStatusAsync(fixture.FlowId, "eu")).State);
        Assert.Equal(1, fixture.Processes.StartCount);

        var restarted = await fixture.Manager.RestartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");

        Assert.Equal(running.InstanceId, restarted.InstanceId);
        Assert.Equal(DemoInstanceState.Running, restarted.State);
        Assert.Equal(2, fixture.Processes.StartCount);
    }

    [Fact]
    public async Task IdentityMismatch_IsNeverKilled()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        fixture.Processes.ForgetAll();

        var stopped = await fixture.Manager.StopAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");

        Assert.Equal(DemoInstanceState.Stopped, stopped.State);
        Assert.Equal(0, fixture.Processes.StopCount);
    }

    [Fact]
    public async Task HealthFailure_IsPersistedAndOwnedProcessCleaned()
    {
        await using var fixture = await RuntimeFixture.CreateAsync(healthy: false);

        var error = await Assert.ThrowsAsync<DemoRuntimeException>(() =>
            fixture.Manager.StartAsync(
                fixture.FlowId,
                "eu",
                "sha256:candidate",
                "sha256:manifest-eu"));

        Assert.Equal(DemoConflictCodes.StartupFailed, error.Code);
        await using var database = await fixture.Factory.CreateDbContextAsync();
        var record = await database.DemoInstances.SingleAsync();
        Assert.Equal(DemoInstanceState.Failed, record.State);
        Assert.NotEmpty(record.FailureDetail);
        Assert.Equal(1, fixture.Processes.StopCount);
        Assert.Contains(
            await database.FlowEvents.Select(item => item.Type).ToListAsync(),
            item => item == "demo.state-changed");
    }

    [Fact]
    public async Task LaunchFailure_IsPersistedWithoutSuccessFallback()
    {
        await using var fixture = await RuntimeFixture.CreateAsync(failLaunch: true);

        var error = await Assert.ThrowsAsync<DemoRuntimeException>(() =>
            fixture.Manager.StartAsync(
                fixture.FlowId,
                "eu",
                "sha256:candidate",
                "sha256:manifest-eu"));

        Assert.Equal(DemoConflictCodes.StartupFailed, error.Code);
        var status = await fixture.Manager.GetStatusAsync(fixture.FlowId, "eu");
        Assert.Equal(DemoInstanceState.Failed, status.State);
        Assert.Contains(
            "synthetic launch failure",
            status.FailureDetail,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartAndStop_AreIdempotent()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();

        var first = await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        var second = await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        await fixture.Manager.StopAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        var stopped = await fixture.Manager.StopAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");

        Assert.Equal(first.InstanceId, second.InstanceId);
        Assert.Equal(DemoInstanceState.Stopped, stopped.State);
        Assert.Equal(1, fixture.Processes.StartCount);
        Assert.Equal(1, fixture.Processes.StopCount);
        Assert.True(fixture.Processes.CleanupCount > 0);
        Assert.Equal(0, fixture.Manager.OperationLockCount);
        Assert.Equal(0, fixture.Manager.ProxyLeaseCount);
    }

    [Fact]
    public async Task StaleBindingAndStoppedProxy_FailClosed()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var stale = await Assert.ThrowsAsync<DemoRuntimeException>(() =>
            fixture.Manager.StartAsync(
                fixture.FlowId,
                "eu",
                "sha256:old",
                "sha256:manifest-eu"));
        Assert.Equal(DemoConflictCodes.CandidateStale, stale.Code);
        var staleManifest = await Assert.ThrowsAsync<DemoRuntimeException>(() =>
            fixture.Manager.StartAsync(
                fixture.FlowId,
                "eu",
                "sha256:candidate",
                "sha256:old-manifest"));
        Assert.Equal(DemoConflictCodes.ManifestStale, staleManifest.Code);

        var running = await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        await fixture.Manager.StopAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        var proxy = await Assert.ThrowsAsync<DemoRuntimeException>(() =>
            fixture.Manager.GetProxyTargetAsync(running.InstanceId!.Value));
        Assert.Equal(DemoConflictCodes.InvalidTransition, proxy.Code);
    }

    [Fact]
    public async Task PortExhaustion_IsTypedAndPersisted()
    {
        await using var fixture = await RuntimeFixture.CreateAsync(rangeSize: 0);
        using var occupied = new TcpListener(IPAddress.Loopback, fixture.Options.FirstPort);
        occupied.Server.ExclusiveAddressUse = true;
        occupied.Start();

        var error = await Assert.ThrowsAsync<DemoRuntimeException>(() =>
            fixture.Manager.StartAsync(
                fixture.FlowId,
                "eu",
                "sha256:candidate",
                "sha256:manifest-eu"));

        Assert.Equal(DemoConflictCodes.PortExhausted, error.Code);
        Assert.Equal(
            DemoInstanceState.Failed,
            (await fixture.Manager.GetStatusAsync(fixture.FlowId, "eu")).State);
    }

    [Fact]
    public async Task ProxyRejectsUnownedRunningRecordWithoutKillingPid()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var running = await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        fixture.Processes.ForgetAll();

        var error = await Assert.ThrowsAsync<DemoRuntimeException>(() =>
            fixture.Manager.GetProxyTargetAsync(running.InstanceId!.Value));

        Assert.Equal(DemoConflictCodes.NotOwned, error.Code);
        Assert.Equal(0, fixture.Processes.StopCount);
        Assert.Equal(
            DemoInstanceState.Stopped,
            (await fixture.Manager.GetStatusAsync(fixture.FlowId, "eu")).State);
    }

    [Fact]
    public async Task Proxy_WhenListenerBecomesForeign_StopsOwnedInstance()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var started = await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        fixture.Processes.EnqueueListenerOwnership(
            DemoListenerOwnership.NotOwned,
            "Synthetic listener takeover.");

        var error = await Assert.ThrowsAsync<DemoRuntimeException>(() =>
            fixture.Manager.GetProxyTargetAsync(started.InstanceId!.Value));

        Assert.Equal(DemoConflictCodes.NotOwned, error.Code);
        Assert.Equal(1, fixture.Processes.StopCount);
        Assert.Equal(
            DemoInstanceState.Stopped,
            (await fixture.Manager.GetStatusAsync(fixture.FlowId, "eu")).State);
    }

    [Fact]
    public async Task RepeatedRestart_ReusesStableInstanceAndLeavesOneOwner()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var started = await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");

        var first = await fixture.Manager.RestartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        var second = await fixture.Manager.RestartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");

        Assert.Equal(started.InstanceId, first.InstanceId);
        Assert.Equal(first.InstanceId, second.InstanceId);
        Assert.Equal(DemoInstanceState.Running, second.State);
        Assert.Equal(1, fixture.Processes.OwnedCount);
    }

    [Fact]
    public async Task ChangedLaunchBinding_DoesNotReuseStoppedRecord()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var first = await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        await fixture.Manager.StopAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        fixture.Manifests.LaunchSuffix = "-changed";

        var replacement = await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");

        Assert.Equal(first.InstanceId, replacement.InstanceId);
        Assert.Equal(1, fixture.Processes.OwnedCount);
        await using var database = await fixture.Factory.CreateDbContextAsync();
        var record = await database.DemoInstances.SingleAsync();
        Assert.EndsWith(
            "-changed",
            record.LaunchIdentity,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProxyHotPath_ReusesValidatedBindingAndCachedHealth()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var started = await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        var resolvesAfterStart = fixture.Manifests.ResolveCount;
        var probesAfterStart = fixture.Health.ProbeCount;

        await fixture.Manager.GetProxyTargetAsync(started.InstanceId!.Value);
        await fixture.Manager.GetProxyTargetAsync(started.InstanceId.Value);
        await fixture.Manager.GetProxyTargetAsync(started.InstanceId.Value);

        Assert.Equal(resolvesAfterStart, fixture.Manifests.ResolveCount);
        Assert.Equal(probesAfterStart, fixture.Health.ProbeCount);
    }

    [Fact]
    public async Task ProxyHealth_RequiresConsecutiveFailuresAndDeduplicatesTransition()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var started = await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        fixture.Health.Enqueue(false, false, false);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            fixture.Time.Advance(TimeSpan.FromMilliseconds(101));
            await fixture.Manager.GetProxyTargetAsync(started.InstanceId!.Value);
        }

        Assert.Equal(
            DemoInstanceState.Running,
            (await fixture.Manager.GetStatusAsync(fixture.FlowId, "eu")).State);
        fixture.Time.Advance(TimeSpan.FromMilliseconds(101));
        var failure = await Assert.ThrowsAsync<DemoRuntimeException>(() =>
            fixture.Manager.GetProxyTargetAsync(started.InstanceId!.Value));
        Assert.Equal(DemoConflictCodes.StartupFailed, failure.Code);

        await using var database = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(
            DemoInstanceState.Unhealthy,
            (await database.DemoInstances.SingleAsync()).State);
        Assert.Single(await database.FlowEvents
            .Where(item =>
                item.Type == "demo.state-changed" &&
                item.Message.Contains("Unhealthy"))
            .ToListAsync());
    }

    [Fact]
    public async Task StartFromUnhealthy_StopsOwnedAttemptBeforeTrackingReplacement()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var started = await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        fixture.Health.Enqueue(false, false, false);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            fixture.Time.Advance(TimeSpan.FromMilliseconds(101));
            if (attempt < 2)
            {
                await fixture.Manager.GetProxyTargetAsync(started.InstanceId!.Value);
            }
            else
            {
                await Assert.ThrowsAsync<DemoRuntimeException>(() =>
                    fixture.Manager.GetProxyTargetAsync(started.InstanceId!.Value));
            }
        }

        var retried = await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");

        Assert.Equal(started.InstanceId, retried.InstanceId);
        Assert.Equal(DemoInstanceState.Running, retried.State);
        Assert.Equal(2, fixture.Processes.StartCount);
        Assert.Equal(1, fixture.Processes.StopCount);
        Assert.Equal(1, fixture.Processes.OwnedCount);
        await using var database = await fixture.Factory.CreateDbContextAsync();
        Assert.Contains(
            await database.FlowEvents
                .Where(item => item.Type == "demo.state-changed")
                .Select(item => item.Message)
                .ToListAsync(),
            message => message.Contains(
                "Unhealthy to Stopped",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartFromUnhealthyOwnershipMismatch_FailsWithoutKillingOrReplacing()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var started = await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        fixture.Health.Enqueue(false, false, false);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            fixture.Time.Advance(TimeSpan.FromMilliseconds(101));
            if (attempt < 2)
            {
                await fixture.Manager.GetProxyTargetAsync(started.InstanceId!.Value);
            }
            else
            {
                await Assert.ThrowsAsync<DemoRuntimeException>(() =>
                    fixture.Manager.GetProxyTargetAsync(started.InstanceId!.Value));
            }
        }
        fixture.Processes.ForgetAll();

        var error = await Assert.ThrowsAsync<DemoRuntimeException>(() =>
            fixture.Manager.StartAsync(
                fixture.FlowId,
                "eu",
                "sha256:candidate",
                "sha256:manifest-eu"));

        Assert.Equal(DemoConflictCodes.NotOwned, error.Code);
        Assert.Equal(1, fixture.Processes.StartCount);
        Assert.Equal(0, fixture.Processes.StopCount);
    }

    [Fact]
    public async Task ReworkingFlow_DeniesProxyAndReconciliationStopsOwnedDemo()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var started = await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows.SingleAsync();
            flow.Status = FlowStatus.Reworking;
            await database.SaveChangesAsync();
        }

        var denied = await Assert.ThrowsAsync<DemoRuntimeException>(() =>
            fixture.Manager.GetProxyTargetAsync(started.InstanceId!.Value));

        Assert.Equal(DemoConflictCodes.InvalidTransition, denied.Code);
        Assert.Equal(1, fixture.Processes.OwnedCount);
        await fixture.Manager.ReconcileAsync();
        Assert.Equal(1, fixture.Processes.StopCount);
        Assert.Equal(0, fixture.Processes.OwnedCount);
        await using var verification = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(
            DemoInstanceState.Stopped,
            (await verification.DemoInstances.SingleAsync()).State);
    }

    [Fact]
    public async Task FlowRevocation_DoesNotKillOwnershipMismatch()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var record = await database.DemoInstances.SingleAsync();
            record.ProcessStartIdentity++;
            await database.SaveChangesAsync();
        }

        await fixture.Manager.RevokeFlowAsync(
            fixture.FlowId,
            "Synthetic refinement.");

        Assert.Equal(0, fixture.Processes.StopCount);
        await using var verification = await fixture.Factory.CreateDbContextAsync();
        var stopped = await verification.DemoInstances.SingleAsync();
        Assert.Equal(DemoInstanceState.Stopped, stopped.State);
        Assert.Null(stopped.ProcessId);
    }

    [Fact]
    public async Task NewReviewedBinding_StopsOwnedOldInstanceBeforeReplacement()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var original = await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        fixture.Manifests.CandidateFingerprint = "sha256:candidate-2";
        fixture.Manifests.ManifestSuffix = "-2";

        var replacement = await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate-2",
            "sha256:manifest-eu-2");

        Assert.NotEqual(original.InstanceId, replacement.InstanceId);
        Assert.Equal(1, fixture.Processes.StopCount);
        Assert.Equal(1, fixture.Processes.OwnedCount);
        await using var database = await fixture.Factory.CreateDbContextAsync();
        var records = await database.DemoInstances
            .OrderBy(item => item.CreatedAt)
            .ToListAsync();
        Assert.Equal(2, records.Count);
        Assert.Equal(DemoInstanceState.Stopped, records[0].State);
        Assert.Equal(DemoInstanceState.Running, records[1].State);
        Assert.Single(records, item =>
            item.State is DemoInstanceState.Starting or
                DemoInstanceState.Running or
                DemoInstanceState.Unhealthy);
    }

    [Fact]
    public async Task StatusStartAndRestart_RequirePreviewEligibleFlowState()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows.SingleAsync();
            flow.Status = FlowStatus.Running;
            await database.SaveChangesAsync();
        }

        var statusError = await Assert.ThrowsAsync<DemoRuntimeException>(() =>
            fixture.Manager.GetStatusAsync(fixture.FlowId, "eu"));
        var startError = await Assert.ThrowsAsync<DemoRuntimeException>(() =>
            fixture.Manager.StartAsync(
                fixture.FlowId,
                "eu",
                "sha256:candidate",
                "sha256:manifest-eu"));
        var restartError = await Assert.ThrowsAsync<DemoRuntimeException>(() =>
            fixture.Manager.RestartAsync(
                fixture.FlowId,
                "eu",
                "sha256:candidate",
                "sha256:manifest-eu"));

        Assert.Equal(DemoConflictCodes.InvalidTransition, statusError.Code);
        Assert.Equal(DemoConflictCodes.InvalidTransition, startError.Code);
        Assert.Equal(DemoConflictCodes.InvalidTransition, restartError.Code);
        Assert.Equal(0, fixture.Processes.StartCount);
    }

    [Fact]
    public async Task GracefulShutdown_StopsOnlyVerifiablyOwnedProcesses()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        await fixture.Manager.StartAsync(
            fixture.FlowId,
            "ee",
            "sha256:candidate",
            "sha256:manifest-ee");
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var mismatched = await database.DemoInstances.SingleAsync(
                item => item.ArtifactId == "ee");
            mismatched.ProcessStartIdentity++;
            await database.SaveChangesAsync();
        }

        await fixture.Manager.StopOwnedOnShutdownAsync();

        Assert.Equal(1, fixture.Processes.StopCount);
        await using var verification = await fixture.Factory.CreateDbContextAsync();
        Assert.All(
            await verification.DemoInstances.ToListAsync(),
            item => Assert.Equal(DemoInstanceState.Stopped, item.State));
    }

    [Fact]
    public async Task ReconciliationFailure_StopsOwnedProcessAndIsIdempotent()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        fixture.Manifests.ThrowOnResolve = true;

        await fixture.Manager.ReconcileAsync();
        await fixture.Manager.ReconcileAsync();

        Assert.Equal(1, fixture.Processes.StopCount);
        Assert.Equal(0, fixture.Processes.OwnedCount);
        await using var database = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(
            DemoInstanceState.Stopped,
            (await database.DemoInstances.SingleAsync()).State);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Proxy_ForwardsMutationsWithOpaqueCorsAndTrailingSlash(
        string method)
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var started = await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        await using var database = await fixture.Factory.CreateDbContextAsync();
        var record = await database.DemoInstances.SingleAsync();
        await using var server = new SingleRequestHttpServer(
            record.AssignedPort!.Value,
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{}");
        using var proxy = new DemoReverseProxy(fixture.Manager);
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = $"/api/demos/{started.InstanceId:D}/docs/";
        context.Request.Headers.Origin = "null";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("payload"));
        context.Request.ContentLength = 7;
        context.Response.Body = new MemoryStream();

        await proxy.ProxyAsync(
            started.InstanceId!.Value,
            "docs/",
            context,
            CancellationToken.None);
        var received = await server.Request;

        Assert.Equal($"{method} /docs/ HTTP/1.1", received.RequestLine);
        Assert.Equal(
            $"http://127.0.0.1:{record.AssignedPort.Value}",
            received.Origin);
        Assert.Equal("payload", received.Body);
        Assert.Equal("null", context.Response.Headers.AccessControlAllowOrigin);
        Assert.Equal(
            "cross-origin",
            context.Response.Headers["Cross-Origin-Resource-Policy"]);
        var csp = context.Response.Headers.ContentSecurityPolicy.ToString();
        Assert.Equal(DemoReverseProxy.IsolationPolicy, csp);
        Assert.Contains("sandbox", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("allow-same-origin", csp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Proxy_RewritesRootRedirectOnceAndKeepsIsolationHeaders()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var started = await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        await using var database = await fixture.Factory.CreateDbContextAsync();
        var record = await database.DemoInstances.SingleAsync();
        await using var server = new SingleRequestHttpServer(
            record.AssignedPort!.Value,
            "HTTP/1.1 302 Found\r\nLocation: /docs/\r\nContent-Length: 0\r\n\r\n");
        using var proxy = new DemoReverseProxy(fixture.Manager);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();

        await proxy.ProxyAsync(
            started.InstanceId!.Value,
            "docs",
            context,
            CancellationToken.None);
        await server.Request;

        Assert.Equal(
            $"/api/demos/{started.InstanceId:D}/docs/",
            context.Response.Headers.Location);
        Assert.Equal(
            DemoReverseProxy.IsolationPolicy,
            context.Response.Headers.ContentSecurityPolicy);
    }

    [Fact]
    public async Task Proxy_DoesNotPrefixAnAlreadyStableRedirectAgain()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var started = await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        await using var database = await fixture.Factory.CreateDbContextAsync();
        var record = await database.DemoInstances.SingleAsync();
        var stableLocation = $"/api/demos/{started.InstanceId:D}/docs/";
        await using var server = new SingleRequestHttpServer(
            record.AssignedPort!.Value,
            $"HTTP/1.1 302 Found\r\nLocation: {stableLocation}\r\nContent-Length: 0\r\n\r\n");
        using var proxy = new DemoReverseProxy(fixture.Manager);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();

        await proxy.ProxyAsync(
            started.InstanceId!.Value,
            "docs",
            context,
            CancellationToken.None);
        await server.Request;

        Assert.Equal(stableLocation, context.Response.Headers.Location);
    }

    [Fact]
    public async Task Proxy_ContainsParentRelativeRedirectWithinInstancePrefix()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var started = await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        await using var database = await fixture.Factory.CreateDbContextAsync();
        var record = await database.DemoInstances.SingleAsync();
        await using var server = new SingleRequestHttpServer(
            record.AssignedPort!.Value,
            "HTTP/1.1 302 Found\r\nLocation: ../../../api/settings\r\nContent-Length: 0\r\n\r\n");
        using var proxy = new DemoReverseProxy(fixture.Manager);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();

        await proxy.ProxyAsync(
            started.InstanceId!.Value,
            "docs/deep/page",
            context,
            CancellationToken.None);
        await server.Request;

        Assert.Equal(
            $"/api/demos/{started.InstanceId:D}/api/settings",
            context.Response.Headers.Location);
    }

    [Fact]
    public async Task Proxy_PreflightAndRuntimeErrorsRemainOpaqueOriginIsolated()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var started = await fixture.Manager.StartAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        using var proxy = new DemoReverseProxy(fixture.Manager);
        var preflight = new DefaultHttpContext();
        preflight.Request.Method = "OPTIONS";
        preflight.Request.Headers.Origin = "null";
        preflight.Request.Headers.AccessControlRequestMethod = "PATCH";
        preflight.Request.Headers.AccessControlRequestHeaders = "content-type";

        await proxy.ProxyAsync(
            started.InstanceId!.Value,
            "api/item",
            preflight,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status204NoContent, preflight.Response.StatusCode);
        Assert.Equal("null", preflight.Response.Headers.AccessControlAllowOrigin);
        Assert.Equal("PATCH", preflight.Response.Headers.AccessControlAllowMethods);
        Assert.Equal(
            DemoReverseProxy.IsolationPolicy,
            preflight.Response.Headers.ContentSecurityPolicy);

        await fixture.Manager.StopAsync(
            fixture.FlowId,
            "eu",
            "sha256:candidate",
            "sha256:manifest-eu");
        var failed = new DefaultHttpContext();
        failed.Request.Method = "GET";
        await Assert.ThrowsAsync<DemoRuntimeException>(() =>
            proxy.ProxyAsync(
                started.InstanceId.Value,
                string.Empty,
                failed,
                CancellationToken.None));
        Assert.Equal(
            DemoReverseProxy.IsolationPolicy,
            failed.Response.Headers.ContentSecurityPolicy);
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private readonly string _root;

        private RuntimeFixture(
            string root,
            Guid flowId,
            TestDbContextFactory factory,
            FakeDemoProcessLauncher processes,
            FakeManifestService manifests,
            FakeHealthProbe health,
            DemoRuntimeOptions options,
            ManualTimeProvider time,
            DemoRuntimeManager manager)
        {
            _root = root;
            FlowId = flowId;
            Factory = factory;
            Processes = processes;
            Manifests = manifests;
            Health = health;
            Options = options;
            Time = time;
            Manager = manager;
        }

        public Guid FlowId { get; }
        public TestDbContextFactory Factory { get; }
        public FakeDemoProcessLauncher Processes { get; }
        public FakeManifestService Manifests { get; }
        public FakeHealthProbe Health { get; }
        public DemoRuntimeOptions Options { get; }
        public ManualTimeProvider Time { get; }
        public DemoRuntimeManager Manager { get; }

        public static async Task<RuntimeFixture> CreateAsync(
            bool healthy = true,
            int rangeSize = 20,
            bool failLaunch = false,
            DbCommandInterceptor? commandInterceptor = null)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"demo-runtime-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var optionsBuilder = new DbContextOptionsBuilder<HarnessDbContext>()
                .UseSqlite(
                    $"Data Source={Path.Combine(root, "runtime.db")};Pooling=False");
            if (commandInterceptor is not null)
            {
                optionsBuilder.AddInterceptors(commandInterceptor);
            }
            var options = optionsBuilder.Options;
            var factory = new TestDbContextFactory(options);
            var flow = new FlowRun
            {
                Title = "Live demo",
                OriginalRequest = "Run two demos",
                ConsolidatedRequest = "Run two demos",
                Kind = FlowKind.Delivery,
                WorkspacePath = root,
                Status = FlowStatus.WaitingForFeedback
            };
            await using (var database = await factory.CreateDbContextAsync())
            {
                await database.Database.EnsureCreatedAsync();
                database.Flows.Add(flow);
                await database.SaveChangesAsync();
            }
            var firstPort = FindFreeRangeStart();
            var runtimeOptions = new DemoRuntimeOptions
            {
                FirstPort = firstPort,
                LastPort = firstPort + rangeSize,
                MaximumPortAttempts = 20,
                HealthPollMilliseconds = 25,
                ProxyHealthCacheMilliseconds = 100,
                ConsecutiveHealthFailureThreshold = 3
            };
            var processes = new FakeDemoProcessLauncher();
            processes.FailLaunch = failLaunch;
            var manifests = new FakeManifestService(root);
            var health = new FakeHealthProbe(healthy);
            var time = new ManualTimeProvider();
            var manager = new DemoRuntimeManager(
                factory,
                manifests,
                processes,
                health,
                runtimeOptions,
                time);
            return new RuntimeFixture(
                root,
                flow.Id,
                factory,
                processes,
                manifests,
                health,
                runtimeOptions,
                time,
                manager);
        }

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static int FindFreeRangeStart()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return Math.Clamp(port, 20_000, 60_000);
        }
    }

    private sealed class FakeManifestService(string workspace)
        : ISealedDemoManifestService
    {
        private int _resolveCount;

        public int ResolveCount => _resolveCount;

        public string CandidateFingerprint { get; set; } = "sha256:candidate";

        public string ManifestSuffix { get; set; } = string.Empty;

        public string LaunchSuffix { get; set; } = string.Empty;

        public bool ThrowOnResolve { get; set; }

        public Task<SealedDemoManifestBinding?> ResolveAsync(
            FlowRun flow,
            string artifactId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _resolveCount);
            if (ThrowOnResolve)
            {
                throw new CustomerDemoContractException(
                    "Synthetic invalid manifest.");
            }
            var manifest = new CustomerDemoManifest(
                artifactId,
                "dotnet",
                string.Empty,
                ["run", "--urls", "http://127.0.0.1:{port}"],
                "/health",
                1);
            var profile = CustomerDemoLaunchPolicy.Resolve("dotnet");
            return Task.FromResult<SealedDemoManifestBinding?>(
                new SealedDemoManifestBinding(
                    manifest,
                    CandidateFingerprint,
                    $"sha256:manifest-{artifactId}{ManifestSuffix}",
                    $".customer-preview/{artifactId}/customer-demo.json",
                    workspace,
                    workspace,
                    profile,
                    $"sha256:launch-{artifactId}{LaunchSuffix}"));
        }
    }

    private sealed class FakeHealthProbe(bool healthy) : IDemoHealthProbe
    {
        private readonly ConcurrentQueue<bool> _results = new();
        private int _probeCount;

        public int ProbeCount => _probeCount;

        public void Enqueue(params bool[] results)
        {
            foreach (var result in results)
            {
                _results.Enqueue(result);
            }
        }

        public Task<bool> IsHealthyAsync(
            int port,
            string healthPath,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _probeCount);
            return Task.FromResult(
                _results.TryDequeue(out var result) ? result : healthy);
        }
    }

    private sealed class RecordingCommandInterceptor : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _commands = new();

        public IReadOnlyCollection<string> Commands => _commands.ToArray();

        public void Clear()
        {
            while (_commands.TryDequeue(out _))
            {
            }
        }

        public override ValueTask<InterceptionResult<DbDataReader>>
            ReaderExecutingAsync(
                DbCommand command,
                CommandEventData eventData,
                InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
        {
            _commands.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeDemoProcessLauncher : IDemoProcessLauncher
    {
        private readonly ConcurrentDictionary<int, long> _owned = new();
        private readonly ConcurrentQueue<DemoListenerOwnershipResult>
            _listenerOwnership = new();
        private int _nextProcessId = 20_000;

        private int _startCount;
        private int _stopCount;
        private int _cleanupCount;

        public int StartCount => _startCount;
        public int StopCount => _stopCount;
        public int CleanupCount => _cleanupCount;
        public int OwnedCount => _owned.Count;
        public bool FailLaunch { get; set; }

        public void EnqueueListenerOwnership(
            DemoListenerOwnership ownership,
            string detail)
        {
            _listenerOwnership.Enqueue(
                new DemoListenerOwnershipResult(ownership, detail));
        }

        public Task<DemoOwnedProcess> StartAsync(
            DemoLaunchCommand command,
            CancellationToken cancellationToken)
        {
            if (FailLaunch)
            {
                throw new InvalidOperationException("Synthetic launch failure.");
            }
            var processId = Interlocked.Increment(ref _nextProcessId);
            var identity = DateTime.UtcNow.Ticks + processId;
            _owned[processId] = identity;
            Interlocked.Increment(ref _startCount);
            return Task.FromResult(
                new DemoOwnedProcess(processId, identity, "fake-demo"));
        }

        public bool IsOwned(DemoInstanceRecord record) =>
            record.ProcessId is { } processId &&
            record.ProcessStartIdentity is { } identity &&
            _owned.TryGetValue(processId, out var expected) &&
            expected == identity;

        public Task<DemoListenerOwnershipResult> VerifyLoopbackListenerAsync(
            DemoInstanceRecord record,
            int port,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                _listenerOwnership.TryDequeue(out var result)
                    ? result
                    : new DemoListenerOwnershipResult(
                        DemoListenerOwnership.Owned,
                        "Synthetic owned listener."));
        }

        public Task StopAsync(
            DemoInstanceRecord record,
            CancellationToken cancellationToken)
        {
            if (record.ProcessId is { } processId &&
                _owned.TryRemove(processId, out _))
            {
                Interlocked.Increment(ref _stopCount);
            }
            return Task.CompletedTask;
        }

        public string ReadDiagnostics(DemoInstanceRecord record) =>
            "fake health endpoint unavailable";

        public void Cleanup(DemoInstanceRecord record)
        {
            Interlocked.Increment(ref _cleanupCount);
        }

        public void ForgetAll() => _owned.Clear();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class SingleRequestHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task<ReceivedRequest> _request;

        public SingleRequestHttpServer(int port, string response)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _request = HandleAsync(response);
        }

        public Task<ReceivedRequest> Request => _request;

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _request;
            }
            catch (Exception exception) when (
                exception is SocketException or ObjectDisposedException)
            {
            }
        }

        private async Task<ReceivedRequest> HandleAsync(string response)
        {
            using var client = await _listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(
                stream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            var requestLine = await reader.ReadLineAsync() ?? string.Empty;
            var contentLength = 0;
            string? origin = null;
            while (await reader.ReadLineAsync() is { Length: > 0 } line)
            {
                var separator = line.IndexOf(':');
                if (separator < 0)
                {
                    continue;
                }
                var name = line[..separator];
                var value = line[(separator + 1)..].Trim();
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    contentLength = int.Parse(
                        value,
                        System.Globalization.CultureInfo.InvariantCulture);
                }
                else if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase))
                {
                    origin = value;
                }
            }
            var bodyBuffer = new char[contentLength];
            if (contentLength > 0)
            {
                await reader.ReadBlockAsync(bodyBuffer);
            }
            var bytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
            return new ReceivedRequest(
                requestLine,
                origin,
                new string(bodyBuffer));
        }

        public sealed record ReceivedRequest(
            string RequestLine,
            string? Origin,
            string Body);
    }

    public sealed class TestDbContextFactory(
        DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);

        public Task<HarnessDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HarnessDbContext(options));
    }
}

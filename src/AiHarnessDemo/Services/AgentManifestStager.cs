using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiHarnessDemo.Core.Orchestration;

namespace AiHarnessDemo.Services;

public sealed record StagedAgentManifest(string Root, string AgentId);

public sealed record StagedPromotionSeed(string Path, string SeedHash);

public sealed record StagedPrompt(
    string AgentRoot,
    string BundleRoot,
    string Path,
    string ManifestPath,
    string Sha256,
    long ByteCount,
    Guid SessionId,
    Guid FlowId,
    Guid FlowStepId,
    int Attempt,
    bool Reused);

public sealed class AgentManifestStager
{
    internal const int MaximumStagedPromptBytes = 16 * 1024 * 1024;
    private const string PromptManifestVersion = "host-prompt-v1";
    private const string PromptFileName = "prompt.utf8.txt";
    private const string PromptManifestFileName = "manifest.json";
    private static readonly UTF8Encoding Utf8WithoutBom =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions PromptManifestJsonOptions =
        new()
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false
        };

    public async Task<StagedAgentManifest> StageAsync(
        string copilotHome,
        AgentManifest snapshot,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Description);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Instructions);

        var root = GetSessionRoot(copilotHome, sessionId);
        var agentsDirectory = Path.Combine(root, ".github", "agents");
        Directory.CreateDirectory(agentsDirectory);
        var safeId = new string(snapshot.Id
            .Where(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            .ToArray());
        if (string.IsNullOrWhiteSpace(safeId))
        {
            throw new InvalidOperationException(
                "The snapshot agent ID has no safe filename characters.");
        }

        var stagedId = $"harness-{safeId}-{sessionId:N}";
        var content =
            $"---{Environment.NewLine}" +
            $"name: {YamlScalar(snapshot.Name)}{Environment.NewLine}" +
            $"description: {YamlScalar(snapshot.Description)}{Environment.NewLine}" +
            $"---{Environment.NewLine}{Environment.NewLine}" +
            snapshot.Instructions.Trim() +
            Environment.NewLine;
        await File.WriteAllTextAsync(
            Path.Combine(agentsDirectory, $"{stagedId}.agent.md"),
            content,
            cancellationToken);
        return new StagedAgentManifest(root, stagedId);
    }

    public bool CleanupSessionRoot(
        string copilotHome,
        Guid sessionId,
        string? assertedRoot = null)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException(
                "A staged-agent cleanup requires a nonempty Copilot session ID.",
                nameof(sessionId));
        }

        var expectedRoot = GetSessionRoot(copilotHome, sessionId);
        if (!string.IsNullOrWhiteSpace(assertedRoot) &&
            !PathEquals(expectedRoot, assertedRoot))
        {
            throw new InvalidOperationException(
                "The asserted staged-agent root does not belong to the requested Copilot session.");
        }
        if (File.Exists(expectedRoot))
        {
            throw new InvalidOperationException(
                "The deterministic staged-agent root is not a directory.");
        }
        if (!Directory.Exists(expectedRoot))
        {
            return false;
        }

        var definitionsRoot = Path.GetDirectoryName(expectedRoot)
            ?? throw new InvalidOperationException(
                "The deterministic staged-agent root has no owner directory.");
        EnsureNotReparsePoint(
            definitionsRoot,
            "The staged-agent definitions directory is a reparse point.");
        EnsureOwnedTreeHasNoReparsePoints(expectedRoot);
        Directory.Delete(expectedRoot, recursive: true);
        return true;
    }

    public async Task<StagedPromotionSeed> StagePromotionSeedAsync(
        StagedAgentManifest stagedManifest,
        AdvisoryPromotionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stagedManifest);
        ArgumentNullException.ThrowIfNull(context);
        var canonical =
            AdvisoryPromotionSeedParser.Canonicalize(
                context.CanonicalSeedJson);
        if (!string.Equals(
                canonical,
                context.CanonicalSeedJson,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Advisory promotion context is not canonical.");
        }
        var hash = AdvisoryPromotionSeedParser.ComputeHash(canonical);
        if (!string.Equals(hash, context.SeedHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Advisory promotion context hash does not match its canonical seed.");
        }

        var contextDirectory = Path.Combine(
            stagedManifest.Root,
            "host-context");
        Directory.CreateDirectory(contextDirectory);
        EnsureNoDirectoryReparsePoints(
            stagedManifest.Root,
            contextDirectory);
        var path = Path.Combine(
            contextDirectory,
            "advisory-promotion-seed.json");
        if (File.Exists(path))
        {
            await ValidatePromotionSeedAsync(
                path,
                canonical,
                hash,
                cancellationToken);
            return new StagedPromotionSeed(path, hash);
        }

        var temporaryPath = $"{path}.tmp-{Guid.NewGuid():N}";
        EnsureContained(stagedManifest.Root, temporaryPath);
        try
        {
            await WriteDurablyAsync(
                temporaryPath,
                Utf8WithoutBom.GetBytes(canonical),
                cancellationToken);
            try
            {
                File.Move(temporaryPath, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                // A concurrent retry published the deterministic seed first.
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        await ValidatePromotionSeedAsync(
            path,
            canonical,
            hash,
            cancellationToken);
        return new StagedPromotionSeed(path, hash);
    }

    public void CleanupPromotionSeed(
        StagedAgentManifest stagedManifest,
        StagedPromotionSeed stagedSeed)
    {
        ArgumentNullException.ThrowIfNull(stagedManifest);
        ArgumentNullException.ThrowIfNull(stagedSeed);
        var contextDirectory = Path.Combine(
            Path.GetFullPath(stagedManifest.Root),
            "host-context");
        EnsureContained(contextDirectory, stagedSeed.Path);
        if (File.Exists(stagedSeed.Path))
        {
            EnsureNoDirectoryReparsePoints(
                stagedManifest.Root,
                contextDirectory);
            EnsureNotReparsePoint(
                stagedSeed.Path,
                "The staged promotion seed cannot be cleaned through a reparse point.");
            File.Delete(stagedSeed.Path);
        }
    }

    public async Task<StagedPrompt> StagePromptAsync(
        StagedAgentManifest stagedManifest,
        string prompt,
        Guid sessionId,
        Guid flowId,
        Guid flowStepId,
        int attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stagedManifest);
        ArgumentNullException.ThrowIfNull(prompt);
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException(
                "A staged prompt requires a nonempty Copilot session ID.",
                nameof(sessionId));
        }
        if (flowId == Guid.Empty)
        {
            throw new ArgumentException(
                "A staged prompt requires a nonempty flow ID.",
                nameof(flowId));
        }
        if (flowStepId == Guid.Empty)
        {
            throw new ArgumentException(
                "A staged prompt requires a nonempty flow-step ID.",
                nameof(flowStepId));
        }
        if (attempt < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attempt),
                "A staged prompt attempt must be positive.");
        }

        var agentRoot = Path.GetFullPath(stagedManifest.Root);
        if (!string.Equals(
                Path.GetFileName(agentRoot),
                sessionId.ToString("N"),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The staged agent root does not belong to the requested Copilot session.");
        }

        var promptBytes = Utf8WithoutBom.GetBytes(prompt);
        if (promptBytes.LongLength > MaximumStagedPromptBytes)
        {
            throw new InvalidOperationException(
                $"The bounded prompt exceeds the {MaximumStagedPromptBytes}-byte staging limit.");
        }
        var digest = ComputeSha256(promptBytes);
        var promptsRoot = Path.Combine(
            agentRoot,
            "host-context",
            "prompts");
        var bundleRoot = Path.Combine(
            promptsRoot,
            flowId.ToString("N"),
            flowStepId.ToString("N"),
            $"attempt-{attempt}");
        EnsureContained(agentRoot, bundleRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(bundleRoot)!);
        EnsureNoDirectoryReparsePoints(
            agentRoot,
            Path.GetDirectoryName(bundleRoot)!);

        if (Directory.Exists(bundleRoot))
        {
            return await ValidatePromptBundleAsync(
                agentRoot,
                bundleRoot,
                digest,
                promptBytes.LongLength,
                sessionId,
                flowId,
                flowStepId,
                attempt,
                reused: true,
                cancellationToken);
        }
        if (File.Exists(bundleRoot))
        {
            throw new InvalidOperationException(
                "The deterministic prompt bundle path is not a directory.");
        }

        var temporaryRoot =
            $"{bundleRoot}.tmp-{Guid.NewGuid():N}";
        EnsureContained(agentRoot, temporaryRoot);
        Directory.CreateDirectory(temporaryRoot);
        var reusedPublishedBundle = false;
        try
        {
            var promptPath = Path.Combine(temporaryRoot, PromptFileName);
            var manifestPath = Path.Combine(
                temporaryRoot,
                PromptManifestFileName);
            await WriteDurablyAsync(
                promptPath,
                promptBytes,
                cancellationToken);
            var manifest = new PromptManifest(
                PromptManifestVersion,
                sessionId,
                flowId,
                flowStepId,
                attempt,
                PromptFileName,
                digest,
                promptBytes.LongLength);
            await WriteDurablyAsync(
                manifestPath,
                Utf8WithoutBom.GetBytes(
                    JsonSerializer.Serialize(
                        manifest,
                        PromptManifestJsonOptions)),
                cancellationToken);

            try
            {
                Directory.Move(temporaryRoot, bundleRoot);
            }
            catch (IOException) when (Directory.Exists(bundleRoot))
            {
                // A concurrent retry won the atomic directory publication race.
                // Its complete bundle is validated below before it is reused.
                reusedPublishedBundle = true;
            }
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }

        return await ValidatePromptBundleAsync(
            agentRoot,
            bundleRoot,
            digest,
            promptBytes.LongLength,
            sessionId,
            flowId,
            flowStepId,
            attempt,
            reused: reusedPublishedBundle,
            cancellationToken);
    }

    public void CleanupPrompt(StagedPrompt stagedPrompt)
    {
        ArgumentNullException.ThrowIfNull(stagedPrompt);
        var agentRoot = Path.GetFullPath(stagedPrompt.AgentRoot);
        var expectedPromptsRoot = Path.Combine(
            agentRoot,
            "host-context",
            "prompts");
        EnsureContained(expectedPromptsRoot, stagedPrompt.BundleRoot);
        if (Directory.Exists(stagedPrompt.BundleRoot))
        {
            EnsureNoDirectoryReparsePoints(
                expectedPromptsRoot,
                Path.GetDirectoryName(stagedPrompt.BundleRoot)!);
            EnsureNotReparsePoint(
                stagedPrompt.BundleRoot,
                "The staged prompt bundle cannot be cleaned through a reparse point.");
            Directory.Delete(stagedPrompt.BundleRoot, recursive: true);
        }
    }

    internal static string BuildPromptReferenceInstruction(
        StagedPrompt stagedPrompt,
        bool recoveringInterruptedSession)
    {
        ArgumentNullException.ThrowIfNull(stagedPrompt);
        var recovery = recoveringInterruptedSession
            ? "Resume from the current workspace and inspect existing changes before continuing. "
            : string.Empty;
        return
            recovery +
            "The host staged the complete bounded UTF-8 prompt for this attempt outside the " +
            "process command line. Before acting, use the view tool to read the entire file at " +
            $"'{stagedPrompt.Path}'. Treat that file as the authoritative prompt. " +
            $"Expected SHA-256: {stagedPrompt.Sha256}. " +
            $"Expected UTF-8 bytes: {stagedPrompt.ByteCount}. " +
            "Do not act until you have read the complete file.";
    }

    private static async Task<StagedPrompt> ValidatePromptBundleAsync(
        string agentRoot,
        string bundleRoot,
        string expectedDigest,
        long expectedByteCount,
        Guid sessionId,
        Guid flowId,
        Guid flowStepId,
        int attempt,
        bool reused,
        CancellationToken cancellationToken)
    {
        EnsureContained(agentRoot, bundleRoot);
        EnsureNoDirectoryReparsePoints(
            agentRoot,
            Path.GetDirectoryName(bundleRoot)!);
        EnsureNotReparsePoint(
            bundleRoot,
            "The staged prompt bundle is a reparse point.");
        var promptPath = Path.Combine(bundleRoot, PromptFileName);
        var manifestPath = Path.Combine(
            bundleRoot,
            PromptManifestFileName);
        if (!File.Exists(promptPath) || !File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                "The deterministic staged prompt bundle is incomplete.");
        }
        EnsureNotReparsePoint(
            promptPath,
            "The staged prompt payload is a reparse point.");
        EnsureNotReparsePoint(
            manifestPath,
            "The staged prompt manifest is a reparse point.");

        var manifestInfo = new FileInfo(manifestPath);
        if (manifestInfo.Length is < 1 or > 4_096)
        {
            throw new InvalidOperationException(
                "The staged prompt manifest has an invalid byte count.");
        }

        PromptManifest manifest;
        try
        {
            var manifestJson = await File.ReadAllTextAsync(
                manifestPath,
                Utf8WithoutBom,
                cancellationToken);
            manifest = JsonSerializer.Deserialize<PromptManifest>(
                           manifestJson,
                           PromptManifestJsonOptions)
                       ?? throw new InvalidOperationException(
                           "The staged prompt manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The staged prompt manifest is invalid.",
                exception);
        }

        if (!string.Equals(
                manifest.Version,
                PromptManifestVersion,
                StringComparison.Ordinal) ||
            manifest.SessionId != sessionId ||
            manifest.FlowId != flowId ||
            manifest.FlowStepId != flowStepId ||
            manifest.Attempt != attempt ||
            !string.Equals(
                manifest.PromptFile,
                PromptFileName,
                StringComparison.Ordinal) ||
            manifest.ByteCount != expectedByteCount ||
            !string.Equals(
                manifest.Sha256,
                expectedDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The staged prompt manifest does not match the current session, flow, step, attempt, byte count, and digest.");
        }

        var promptBytes = await File.ReadAllBytesAsync(
            promptPath,
            cancellationToken);
        var actualDigest = ComputeSha256(promptBytes);
        if (promptBytes.LongLength != manifest.ByteCount ||
            !string.Equals(
                actualDigest,
                manifest.Sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The staged prompt payload failed byte-count or SHA-256 validation.");
        }

        return new StagedPrompt(
            agentRoot,
            bundleRoot,
            promptPath,
            manifestPath,
            manifest.Sha256,
            manifest.ByteCount,
            manifest.SessionId,
            manifest.FlowId,
            manifest.FlowStepId,
            manifest.Attempt,
            reused);
    }

    private static async Task ValidatePromotionSeedAsync(
        string path,
        string expectedCanonical,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        EnsureNotReparsePoint(
            path,
            "The staged promotion seed is a reparse point.");
        var actual = await File.ReadAllTextAsync(
            path,
            Utf8WithoutBom,
            cancellationToken);
        string actualHash;
        try
        {
            actualHash =
                AdvisoryPromotionSeedParser.ComputeHash(actual);
        }
        catch (AdvisoryPromotionSeedContractException exception)
        {
            throw new InvalidOperationException(
                "The staged promotion seed failed contract validation.",
                exception);
        }
        if (!string.Equals(
                actual,
                expectedCanonical,
                StringComparison.Ordinal) ||
            !string.Equals(
                actualHash,
                expectedHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The staged promotion seed failed canonical SHA-256 validation.");
        }
    }

    private static async Task WriteDurablyAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            });
        await stream.WriteAsync(content, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static string ComputeSha256(ReadOnlySpan<byte> content) =>
        "sha256:" +
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    internal static string GetSessionRoot(
        string copilotHome,
        Guid sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(copilotHome);
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException(
                "A staged-agent root requires a nonempty Copilot session ID.",
                nameof(sessionId));
        }
        return Path.GetFullPath(Path.Combine(
            Path.GetFullPath(copilotHome),
            "harness-agent-definitions",
            sessionId.ToString("N")));
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static void EnsureOwnedTreeHasNoReparsePoints(string root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "The staged-agent session root cannot be cleaned through a reparse point.");
            }
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "The staged-agent session root contains a reparse point.");
                }
                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static void EnsureContained(string root, string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(root));
        var fullCandidate = Path.GetFullPath(candidate);
        var relative = Path.GetRelativePath(fullRoot, fullCandidate);
        if (relative == "." ||
            relative == ".." ||
            Path.IsPathRooted(relative) ||
            relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal) ||
            relative.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The staged prompt path escapes its host-owned agent root.");
        }
    }

    private static void EnsureNotReparsePoint(string path, string message)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void EnsureNoDirectoryReparsePoints(
        string root,
        string candidateDirectory)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(root));
        var fullCandidate = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(candidateDirectory));
        EnsureContained(
            Path.GetDirectoryName(fullRoot) ??
            throw new InvalidOperationException(
                "The staged prompt root has no parent directory."),
            fullRoot);
        EnsureContained(fullRoot, fullCandidate);
        EnsureNotReparsePoint(
            fullRoot,
            "The staged prompt root is a reparse point.");

        var relative = Path.GetRelativePath(fullRoot, fullCandidate);
        var current = fullRoot;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current))
            {
                EnsureNotReparsePoint(
                    current,
                    "A staged prompt parent directory is a reparse point.");
            }
        }
    }

    private static string YamlScalar(string value) =>
        JsonSerializer.Serialize(value.ReplaceLineEndings(" "));

    private sealed record PromptManifest(
        string Version,
        Guid SessionId,
        Guid FlowId,
        Guid FlowStepId,
        int Attempt,
        string PromptFile,
        string Sha256,
        long ByteCount);
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Verification;

namespace AiHarnessDemo.Services;

public sealed class CustomerDemoContractException(string message)
    : InvalidOperationException(message);

public sealed record CustomerDemoManifest(
    string ArtifactId,
    string LaunchProfile,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    string HealthPath,
    int StartupTimeoutSeconds);

public sealed record DemoLaunchProfile(
    string Name,
    string WindowsExecutable,
    string UnixExecutable,
    IReadOnlyList<string> PrefixArguments)
{
    public string Executable =>
        OperatingSystem.IsWindows() ? WindowsExecutable : UnixExecutable;
}

/// <summary>
/// Central execution policy for sealed demo manifests. Profiles map a short host-owned name to a
/// known executable; manifests can never supply an executable or a shell command. Candidate code
/// still runs with the user's account, so demos remain explicit, local, and non-authoritative.
/// </summary>
public static class CustomerDemoLaunchPolicy
{
    private static readonly Regex SafeNamePattern =
        new(@"^[A-Za-z0-9@._/-]+$", RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<string, DemoLaunchProfile> ApprovedProfiles =
        new Dictionary<string, DemoLaunchProfile>(StringComparer.Ordinal)
        {
            ["npm"] = new("npm", "npm.cmd", "npm", []),
            ["dotnet"] = new("dotnet", "dotnet.exe", "dotnet", []),
            ["python"] = new("python", "python.exe", "python3", [])
        };

    public static DemoLaunchProfile Resolve(string name) =>
        ApprovedProfiles.TryGetValue(name, out var profile)
            ? name switch
            {
                "npm" => ResolveNodePackageProfile(profile, "npm-cli.js"),
                _ => ResolveExecutableProfile(profile)
            }
            : throw new CustomerDemoContractException(
                $"LaunchProfile '{name}' is not approved. Approved profiles: " +
                string.Join(", ", ApprovedProfiles.Keys.OrderBy(item => item, StringComparer.Ordinal)) +
                ".");

    public static IReadOnlyCollection<string> Names =>
        ApprovedProfiles.Keys.ToArray();

    public static void ValidateArguments(
        string profile,
        IReadOnlyList<string> arguments)
    {
        switch (profile)
        {
            case "npm":
                if (arguments.Count < 2 ||
                    !string.Equals(arguments[0], "run", StringComparison.Ordinal) ||
                    !IsSafeScriptName(arguments[1]) ||
                    arguments.Count > 2 &&
                    !string.Equals(arguments[2], "--", StringComparison.Ordinal))
                {
                    throw new CustomerDemoContractException(
                        "The npm profile requires 'run <safe-script>' followed only by an optional '--' and application arguments.");
                }
                ValidateNpmApplicationArguments(arguments.Skip(3).ToArray());
                break;
            case "dotnet":
                ValidateDotNetArguments(arguments);
                break;
            case "python":
                if (arguments.Count != 5 ||
                    !string.Equals(arguments[0], "-m", StringComparison.Ordinal) ||
                    !string.Equals(arguments[1], "http.server", StringComparison.Ordinal) ||
                    !string.Equals(arguments[2], "{port}", StringComparison.Ordinal) ||
                    !string.Equals(arguments[3], "--bind", StringComparison.Ordinal) ||
                    !string.Equals(arguments[4], "127.0.0.1", StringComparison.Ordinal))
                {
                    throw new CustomerDemoContractException(
                        "The python profile supports only '-m http.server {port} --bind 127.0.0.1'; directory and path overrides are forbidden.");
                }
                break;
        }
    }

    public static void ValidateResolvedArguments(
        string profile,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        if (!string.Equals(profile, "dotnet", StringComparison.Ordinal))
        {
            return;
        }

        var projectIndex = arguments
            .Select((argument, index) => (argument, index))
            .SingleOrDefault(item =>
                string.Equals(item.argument, "--project", StringComparison.Ordinal))
            .index;
        if (projectIndex <= 0)
        {
            return;
        }

        var projectArgument = arguments[projectIndex + 1];
        var projectPath = Path.GetFullPath(
            Path.Combine(
                workingDirectory,
                projectArgument.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(workingDirectory));
        if (!projectPath.StartsWith(
                root + Path.DirectorySeparatorChar,
                comparison) ||
            (!File.Exists(projectPath) && !Directory.Exists(projectPath)))
        {
            throw new CustomerDemoContractException(
                "--project must resolve to an existing project beneath the demo working directory.");
        }
    }

    private static void ValidateNpmApplicationArguments(
        IReadOnlyList<string> arguments)
    {
        foreach (var argument in arguments)
        {
            RejectUnsafeApplicationArgument(argument, "npm");
        }

        var bindingOptionCount = 0;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            var separator = argument.IndexOf('=');
            var option = separator >= 0 ? argument[..separator] : argument;
            if (IsNpmBindingOption(option))
            {
                var value = separator >= 0
                    ? argument[(separator + 1)..]
                    : index + 1 < arguments.Count
                        ? arguments[++index]
                        : string.Empty;
                if (!string.Equals(
                        value,
                        "127.0.0.1",
                        StringComparison.Ordinal))
                {
                    throw new CustomerDemoContractException(
                        $"{option} must bind exactly to 127.0.0.1.");
                }
                bindingOptionCount++;
                continue;
            }

            if (IsHostAffectingOption(option))
            {
                throw new CustomerDemoContractException(
                    $"The npm profile does not approve host-affecting option '{option}'.");
            }
        }

        if (bindingOptionCount != 1)
        {
            throw new CustomerDemoContractException(
                "The npm application arguments must contain exactly one --host, --listen, or --bind option with value 127.0.0.1.");
        }
    }

    private static bool IsNpmBindingOption(string option) =>
        option.Equals("--host", StringComparison.Ordinal) ||
        option.Equals("--listen", StringComparison.Ordinal) ||
        option.Equals("--bind", StringComparison.Ordinal);

    private static bool IsHostAffectingOption(string option)
    {
        if (!option.StartsWith("-", StringComparison.Ordinal))
        {
            return false;
        }

        return option.Contains("host", StringComparison.OrdinalIgnoreCase) ||
               option.Contains("listen", StringComparison.OrdinalIgnoreCase) ||
               option.Contains("bind", StringComparison.OrdinalIgnoreCase) ||
               option.Contains("address", StringComparison.OrdinalIgnoreCase) ||
               option.Contains("interface", StringComparison.OrdinalIgnoreCase) ||
               option.Equals("--ip", StringComparison.OrdinalIgnoreCase) ||
               option is "-H" or "-h" or "-l" or "-b" or "-a" or "-i";
    }

    private static void ValidateDotNetArguments(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 4 ||
            !string.Equals(arguments[0], "run", StringComparison.Ordinal))
        {
            throw new CustomerDemoContractException(
                "The dotnet profile supports only a constrained 'dotnet run' command.");
        }

        var index = 1;
        if (string.Equals(arguments[index], "--project", StringComparison.Ordinal))
        {
            if (index + 1 >= arguments.Count ||
                !IsSafeProjectPath(arguments[index + 1]))
            {
                throw new CustomerDemoContractException(
                    "--project must reference a safe relative project beneath the demo working directory.");
            }
            index += 2;
        }
        if (index >= arguments.Count ||
            !string.Equals(arguments[index], "--", StringComparison.Ordinal))
        {
            throw new CustomerDemoContractException(
                "dotnet host options other than an optional '--project <relative-path>' are forbidden; application arguments must follow '--'.");
        }
        index++;
        if (arguments.Count - index != 2 ||
            !string.Equals(arguments[index], "--urls", StringComparison.Ordinal) ||
            !string.Equals(
                arguments[index + 1],
                "http://127.0.0.1:{port}",
                StringComparison.Ordinal))
        {
            throw new CustomerDemoContractException(
                "The dotnet application arguments must be exactly '--urls http://127.0.0.1:{port}'.");
        }
        foreach (var argument in arguments.Skip(index))
        {
            if (argument.Contains("..", StringComparison.Ordinal) ||
                Path.IsPathRooted(argument))
            {
                throw new CustomerDemoContractException(
                    "The dotnet profile forbids absolute and traversal application arguments.");
            }
        }
    }

    private static void RejectUnsafeApplicationArgument(
        string argument,
        string profile)
    {
        var option = argument.Split('=', 2)[0];
        if (new[] { "--prefix", "--script-shell", "--node-options" }
                .Contains(option, StringComparer.OrdinalIgnoreCase) ||
            argument.Contains("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(argument))
        {
            throw new CustomerDemoContractException(
                $"The {profile} profile forbids dangerous, absolute, and traversal application arguments.");
        }
    }

    private static bool IsSafeProjectPath(string value)
    {
        if (!IsSafeRelativeToken(value))
        {
            return false;
        }
        var extension = Path.GetExtension(value);
        return string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase) ||
               string.IsNullOrEmpty(extension);
    }

    private static bool IsSafeScriptName(string value) =>
        value.Length > 0 &&
        !value.StartsWith("-", StringComparison.Ordinal) &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '@' or '.' or '_' or ':' or '-');

    private static DemoLaunchProfile ResolveExecutableProfile(
        DemoLaunchProfile profile)
    {
        var executableName = OperatingSystem.IsWindows()
            ? profile.WindowsExecutable
            : profile.UnixExecutable;
        var executable = FindOnPath(executableName);
        if (executable is null)
        {
            throw new CustomerDemoContractException(
                $"LaunchProfile '{profile.Name}' is approved but '{executableName}' was not found on PATH.");
        }
        return profile with
        {
            WindowsExecutable = executable,
            UnixExecutable = executable
        };
    }

    private static string? FindOnPath(string executableName)
    {
        if (Path.IsPathRooted(executableName))
        {
            return File.Exists(executableName)
                ? Path.GetFullPath(executableName)
                : null;
        }
        foreach (var directory in PathEntries())
        {
            var candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
        return null;
    }

    private static string[] PathEntries() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
        .ToArray();

    private static bool IsSafeRelativeToken(string value) =>
        value.Length > 0 &&
        SafeNamePattern.IsMatch(value) &&
        !Path.IsPathRooted(value) &&
        !value.Contains('\\', StringComparison.Ordinal) &&
        !value.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..") &&
        !value.Contains("://", StringComparison.Ordinal) &&
        !value.Contains(':', StringComparison.Ordinal);

    private static DemoLaunchProfile ResolveNodePackageProfile(
        DemoLaunchProfile profile,
        string cliFileName)
    {
        var pathEntries = PathEntries();
        var nodeFileName = OperatingSystem.IsWindows() ? "node.exe" : "node";
        var nodePath = pathEntries
            .Select(directory => Path.Combine(directory, nodeFileName))
            .FirstOrDefault(File.Exists);
        if (nodePath is null)
        {
            throw new CustomerDemoContractException(
                $"LaunchProfile '{profile.Name}' is approved but unavailable because Node.js is not on PATH.");
        }

        var nodeDirectory = Path.GetDirectoryName(nodePath)!;
        var cliCandidates = new List<string>
        {
            Path.Combine(
                nodeDirectory,
                "node_modules",
                "npm",
                "bin",
                cliFileName),
            Path.GetFullPath(Path.Combine(
                nodeDirectory,
                "..",
                "lib",
                "node_modules",
                "npm",
                "bin",
                cliFileName))
        };
        if (!OperatingSystem.IsWindows())
        {
            foreach (var directory in pathEntries)
            {
                var shim = Path.Combine(directory, "npm");
                if (!File.Exists(shim))
                {
                    continue;
                }
                try
                {
                    var target = File.ResolveLinkTarget(shim, returnFinalTarget: true);
                    if (target is not null)
                    {
                        cliCandidates.Add(target.FullName);
                    }
                }
                catch (IOException)
                {
                    // An invalid host shim is simply not an approved executable mapping.
                }
            }
        }
        var cliPath = cliCandidates.FirstOrDefault(File.Exists);
        if (cliPath is null)
        {
            throw new CustomerDemoContractException(
                $"LaunchProfile '{profile.Name}' is approved but its host-owned {cliFileName} was not found.");
        }

        return profile with
        {
            WindowsExecutable = Path.GetFullPath(nodePath),
            UnixExecutable = Path.GetFullPath(nodePath),
            PrefixArguments = [Path.GetFullPath(cliPath), .. profile.PrefixArguments]
        };
    }
}

public static partial class CustomerDemoManifestParser
{
    public const int MaximumManifestBytes = 16 * 1024;
    public const int MaximumArguments = 32;
    public const int MaximumArgumentCharacters = 512;
    public const int MaximumPathCharacters = 512;
    public const int MaximumHealthPathCharacters = 256;
    public const int MaximumStartupTimeoutSeconds = 60;

    private static readonly string[] RequiredProperties =
    [
        "ArtifactId",
        "LaunchProfile",
        "WorkingDirectory",
        "Arguments",
        "HealthPath",
        "StartupTimeoutSeconds"
    ];

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,120}$")]
    private static partial Regex ArtifactIdPattern();

    public static CustomerDemoManifest Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty || utf8Json.Length > MaximumManifestBytes)
        {
            throw new CustomerDemoContractException(
                $"The demo manifest must contain 1 to {MaximumManifestBytes} UTF-8 bytes.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                utf8Json.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                });
        }
        catch (JsonException exception)
        {
            throw new CustomerDemoContractException(
                $"The demo manifest is not valid strict JSON: {exception.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new CustomerDemoContractException(
                    "The demo manifest root must be an object.");
            }

            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var allowed = RequiredProperties.ToHashSet(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!allowed.Contains(property.Name))
                {
                    throw new CustomerDemoContractException(
                        $"Unknown demo manifest field '{property.Name}'. Property names are case-sensitive.");
                }
                if (!values.TryAdd(property.Name, property.Value))
                {
                    throw new CustomerDemoContractException(
                        $"Demo manifest field '{property.Name}' appears more than once.");
                }
            }

            var missing = RequiredProperties
                .Where(property => !values.ContainsKey(property))
                .ToArray();
            if (missing.Length > 0)
            {
                throw new CustomerDemoContractException(
                    $"The demo manifest is missing required field(s): {string.Join(", ", missing)}.");
            }

            var artifactId = ReadString(values, "ArtifactId", 120);
            if (!ArtifactIdPattern().IsMatch(artifactId))
            {
                throw new CustomerDemoContractException(
                    "ArtifactId must use 1-120 ASCII letters, digits, underscores, or hyphens.");
            }

            var launchProfile = ReadString(values, "LaunchProfile", 64);
            _ = CustomerDemoLaunchPolicy.Resolve(launchProfile);

            var workingDirectory = ReadString(
                values,
                "WorkingDirectory",
                MaximumPathCharacters,
                allowEmpty: true);
            ValidateRelativeWorkingDirectory(workingDirectory);

            var argumentsElement = values["Arguments"];
            if (argumentsElement.ValueKind != JsonValueKind.Array)
            {
                throw new CustomerDemoContractException("Arguments must be a JSON array of strings.");
            }
            var arguments = argumentsElement.EnumerateArray().ToArray();
            if (arguments.Length is < 1 or > MaximumArguments)
            {
                throw new CustomerDemoContractException(
                    $"Arguments must contain 1 to {MaximumArguments} entries.");
            }
            var parsedArguments = arguments
                .Select((argument, index) =>
                {
                    if (argument.ValueKind != JsonValueKind.String)
                    {
                        throw new CustomerDemoContractException(
                            $"Arguments[{index}] must be a string.");
                    }
                    var value = argument.GetString() ?? string.Empty;
                    if (value.Length is < 1 or > MaximumArgumentCharacters ||
                        value.Any(char.IsControl))
                    {
                        throw new CustomerDemoContractException(
                            $"Arguments[{index}] must contain 1 to {MaximumArgumentCharacters} non-control characters.");
                    }
                    return value;
                })
                .ToArray();
            var portTokenCount = parsedArguments.Sum(argument =>
                CountOccurrences(argument, "{port}"));
            if (portTokenCount != 1)
            {
                throw new CustomerDemoContractException(
                    "Arguments must contain exactly one {port} placeholder.");
            }
            CustomerDemoLaunchPolicy.ValidateArguments(
                launchProfile,
                parsedArguments);

            var healthPath = ReadString(
                values,
                "HealthPath",
                MaximumHealthPathCharacters);
            if (!healthPath.StartsWith("/", StringComparison.Ordinal) ||
                healthPath.StartsWith("//", StringComparison.Ordinal) ||
                healthPath.Contains('\\', StringComparison.Ordinal) ||
                healthPath.Contains('#', StringComparison.Ordinal) ||
                healthPath.Any(char.IsControl) ||
                Uri.TryCreate(healthPath, UriKind.Absolute, out _))
            {
                throw new CustomerDemoContractException(
                    "HealthPath must be a local absolute-path reference beginning with one '/'.");
            }

            var timeoutElement = values["StartupTimeoutSeconds"];
            if (!timeoutElement.TryGetInt32(out var startupTimeout) ||
                startupTimeout is < 1 or > MaximumStartupTimeoutSeconds)
            {
                throw new CustomerDemoContractException(
                    $"StartupTimeoutSeconds must be an integer from 1 to {MaximumStartupTimeoutSeconds}.");
            }

            return new CustomerDemoManifest(
                artifactId,
                launchProfile,
                workingDirectory,
                parsedArguments,
                healthPath,
                startupTimeout);
        }
    }

    public static string ResolveWorkingDirectory(
        CustomerDemoManifest manifest,
        string workspacePath)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var workspace = WorkspacePathGuard.ValidateExistingRoot(
            workspacePath,
            authorizedWorkspaceRoot: null,
            "Live demo");
        ValidateRelativeWorkingDirectory(manifest.WorkingDirectory);
        var candidate = string.IsNullOrEmpty(manifest.WorkingDirectory)
            ? workspace
            : Path.GetFullPath(
                Path.Combine(
                    workspace,
                    manifest.WorkingDirectory.Replace(
                        '/',
                        Path.DirectorySeparatorChar)));
        return WorkspacePathGuard.ValidateExistingContainedDirectory(
            workspace,
            candidate,
            "Live demo working directory");
    }

    private static void ValidateRelativeWorkingDirectory(string value)
    {
        if (Path.IsPathRooted(value) ||
            value.Contains('\0') ||
            value.Contains(':', StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new CustomerDemoContractException(
                "WorkingDirectory must be a relative path inside the flow workspace.");
        }
        var normalized = value.Replace('\\', '/');
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or ".."))
        {
            throw new CustomerDemoContractException(
                "WorkingDirectory cannot contain '.' or '..' traversal segments.");
        }
    }

    private static string ReadString(
        IReadOnlyDictionary<string, JsonElement> values,
        string name,
        int maximumCharacters,
        bool allowEmpty = false)
    {
        var element = values[name];
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new CustomerDemoContractException($"{name} must be a string.");
        }
        var value = element.GetString() ?? string.Empty;
        if ((!allowEmpty && value.Length == 0) ||
            value.Length > maximumCharacters ||
            value.Any(char.IsControl))
        {
            throw new CustomerDemoContractException(
                $"{name} must contain {(allowEmpty ? "0" : "1")} to {maximumCharacters} non-control characters.");
        }
        return value;
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        for (var index = 0;
             (index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0;
             index += token.Length)
        {
            count++;
        }
        return count;
    }
}

public sealed record SealedDemoManifestBinding(
    CustomerDemoManifest Manifest,
    string CandidateFingerprint,
    string ManifestHash,
    string ManifestRelativePath,
    string WorkspacePath,
    string WorkingDirectory,
    DemoLaunchProfile LaunchProfile,
    string LaunchIdentity);

public interface ISealedDemoManifestService
{
    Task<SealedDemoManifestBinding?> ResolveAsync(
        FlowRun flow,
        string artifactId,
        CancellationToken cancellationToken = default);
}

public sealed class SealedDemoManifestService(
    IReviewedCandidateService reviewedCandidates) : ISealedDemoManifestService
{
    public const string ManifestFileName = "customer-demo.json";

    public async Task<SealedDemoManifestBinding?> ResolveAsync(
        FlowRun flow,
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (string.IsNullOrWhiteSpace(artifactId) ||
            artifactId.Length > 120 ||
            artifactId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '_' and not '-'))
        {
            throw new CustomerDemoContractException(
                "The requested demo artifact ID is invalid.");
        }
        if (flow.Kind != FlowKind.Delivery)
        {
            return null;
        }

        var identity = ReviewedCandidateLedger.Read(flow);
        var candidate = await reviewedCandidates.VerifyPreviewAsync(
            flow,
            identity,
            cancellationToken);
        var relativePath =
            $".customer-preview/{artifactId}/{ManifestFileName}";
        var entries = candidate.Manifest.PreviewArtifacts
            .Where(item => string.Equals(
                item.RelativePath.Replace('\\', '/'),
                relativePath,
                StringComparison.Ordinal))
            .ToArray();
        if (entries.Length == 0)
        {
            return null;
        }
        if (entries.Length != 1)
        {
            throw new CustomerDemoContractException(
                "The sealed candidate contains duplicate demo manifest identities.");
        }

        var workspace = WorkspacePathGuard.ValidateExistingRoot(
            flow.WorkspacePath,
            authorizedWorkspaceRoot: null,
            "Sealed live demo");
        CandidateFingerprintService.ValidateLinksStayInside(workspace);
        var manifestPath = Path.GetFullPath(
            Path.Combine(
                workspace,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var expectedPrefix =
            Path.TrimEndingDirectorySeparator(workspace) +
            Path.DirectorySeparatorChar;
        if (!manifestPath.StartsWith(
                expectedPrefix,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal) ||
            !File.Exists(manifestPath))
        {
            throw new CustomerDemoContractException(
                "The sealed demo manifest is missing or outside the flow workspace.");
        }
        var bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        var digest =
            "sha256:" +
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (bytes.LongLength != entries[0].Length ||
            !string.Equals(digest, entries[0].Digest, StringComparison.Ordinal))
        {
            throw new CandidateValidationException(
                "The demo manifest bytes no longer match the sealed reviewed candidate.");
        }

        var manifest = CustomerDemoManifestParser.Parse(bytes);
        if (!string.Equals(manifest.ArtifactId, artifactId, StringComparison.Ordinal))
        {
            throw new CustomerDemoContractException(
                "The sealed demo manifest ArtifactId does not match the requested preview artifact.");
        }
        var workingDirectory =
            CustomerDemoManifestParser.ResolveWorkingDirectory(manifest, workspace);
        var launchProfile = CustomerDemoLaunchPolicy.Resolve(manifest.LaunchProfile);
        CustomerDemoLaunchPolicy.ValidateResolvedArguments(
            manifest.LaunchProfile,
            manifest.Arguments,
            workingDirectory);
        var launchIdentity = OutcomeVerificationRules.ComputeSha256(
            string.Join(
                "\n",
                candidate.Fingerprint,
                digest,
                workspace,
                workingDirectory,
                launchProfile.Name,
                launchProfile.Executable,
                string.Join("\0", launchProfile.PrefixArguments.Concat(manifest.Arguments))));
        return new SealedDemoManifestBinding(
            manifest,
            candidate.Fingerprint,
            digest,
            relativePath,
            workspace,
            workingDirectory,
            launchProfile,
            launchIdentity);
    }
}

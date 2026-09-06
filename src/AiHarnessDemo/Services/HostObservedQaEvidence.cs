using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Verification;

namespace AiHarnessDemo.Services;

internal static partial class HostObservedToolLocator
{
    private const string CommandDigestPrefix = "host-command-sha256:";
    private const string LocatorDigestPrefix = "host-locator-sha256:";

    private static readonly string[] CommandArgumentNames =
    [
        "command",
        "script"
    ];

    private static readonly string[] LocatorArgumentNames =
    [
        "path",
        "url",
        "target",
        "selector",
        "pattern"
    ];

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();

    internal static string AppendDigests(
        string scrubbedSummary,
        JsonElement arguments)
    {
        var commandDigests = ReadArgumentValues(arguments, CommandArgumentNames)
            .Select(CreateDigest)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var locatorDigests = ReadArgumentValues(arguments, LocatorArgumentNames)
            .Select(CreateDigest)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var annotations = commandDigests
            .Select(digest => CommandDigestPrefix + digest)
            .Concat(locatorDigests.Select(digest => LocatorDigestPrefix + digest))
            .ToArray();
        return annotations.Length == 0
            ? scrubbedSummary
            : $"{scrubbedSummary} Host-observed locators: {string.Join(", ", annotations)}";
    }

    internal static string CreateCommandSummary(string command) =>
        $"Argument values redacted; fields: command. Host-observed locators: " +
        $"{CommandDigestPrefix}{CreateDigest(command)}";

    internal static string CreateLocatorSummary(string locator) =>
        $"Argument values redacted; fields: path. Host-observed locators: " +
        $"{LocatorDigestPrefix}{CreateDigest(locator)}";

    internal static bool MatchesCommand(string argumentsSummary, string locator) =>
        ContainsDigest(argumentsSummary, CommandDigestPrefix, locator);

    internal static bool MatchesLocator(string argumentsSummary, string locator) =>
        MatchesCommand(argumentsSummary, locator) ||
        ContainsDigest(argumentsSummary, LocatorDigestPrefix, locator);

    internal static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return WhitespacePattern()
            .Replace(
                value.Normalize(NormalizationForm.FormKC)
                    .Replace('\\', '/')
                    .Trim(),
                " ");
    }

    private static bool ContainsDigest(
        string argumentsSummary,
        string prefix,
        string locator)
    {
        var token = prefix + CreateDigest(locator);
        return argumentsSummary.Contains(token, StringComparison.Ordinal);
    }

    private static string CreateDigest(string value) =>
        Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(Normalize(value))))
            .ToLowerInvariant();

    internal static bool IsReadOnlyGitCommand(string command)
    {
        var tokens = Tokenize(command);
        if (tokens.Count == 0)
        {
            return true;
        }
        var executable = Path.GetFileNameWithoutExtension(tokens[0]);
        if (!string.Equals(executable, "git", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (tokens[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var index = 1;
        while (index < tokens.Count)
        {
            var token = tokens[index];
            if (token == "--")
            {
                return false;
            }
            if (token is "-c" or "--git-dir" or "--work-tree" or
                "--namespace" or "--config-env" ||
                token.StartsWith("--git-dir=", StringComparison.Ordinal) ||
                token.StartsWith("--work-tree=", StringComparison.Ordinal) ||
                token.StartsWith("--namespace=", StringComparison.Ordinal) ||
                token.StartsWith("--config-env=", StringComparison.Ordinal) ||
                token.StartsWith("-c", StringComparison.Ordinal) &&
                token.Length > 2)
            {
                return false;
            }
            if (token is "-C" or "--super-prefix")
            {
                index += 2;
                continue;
            }
            if (token.StartsWith("--super-prefix=", StringComparison.Ordinal))
            {
                index++;
                continue;
            }
            if (token.StartsWith("-", StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            return !CopilotReasoningHost.GovernedGitMutationSubcommands.Contains(
                token,
                StringComparer.OrdinalIgnoreCase);
        }
        return false;
    }

    private static IReadOnlyList<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        for (var index = 0; index < command.Length; index++)
        {
            var character = command[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(character);
                }
                continue;
            }
            if (character is '\'' or '"')
            {
                quote = character;
                continue;
            }
            if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(character);
        }
        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }
        return tokens;
    }


    private static IEnumerable<string> ReadArgumentValues(
        JsonElement arguments,
        IReadOnlyCollection<string> names)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in arguments.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            if (property.Value.ValueKind == JsonValueKind.String &&
                property.Value.GetString() is { Length: > 0 } value)
            {
                yield return value;
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                var parts = property.Value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToArray();
                if (parts.Length > 0)
                {
                    yield return string.Join(" ", parts!);
                }
            }
        }
    }
}

internal static class HostObservedQaEvidence
{
    private static readonly string[] TestCommandMarkers =
    [
        "dotnet test",
        "npm test",
        "npm run test",
        "pnpm test",
        "pnpm run test",
        "yarn test",
        "pytest",
        "python -m pytest",
        "go test",
        "cargo test",
        "mvn test",
        "mvn verify",
        "gradle test",
        "gradlew test",
        "ctest",
        "vstest",
        "nunit",
        "jest",
        "vitest",
        "playwright test"
    ];

    private static readonly string[] InspectionCommandMarkers =
    [
        "select-string",
        "test-path",
        "compare-object",
        "get-filehash",
        "rg ",
        "ripgrep ",
        "grep ",
        "findstr ",
        "diff ",
        "git diff",
        "git show",
        "git status",
        "curl ",
        "invoke-webrequest",
        "playwright"
    ];

    private static readonly string[] BuildAssertionCommandMarkers =
    [
        "dotnet build",
        "npm run build",
        "npm run typecheck",
        "pnpm run build",
        "pnpm run typecheck",
        "yarn build",
        "yarn typecheck",
        "cargo check",
        "go vet"
    ];

    private static readonly string[] AuxiliaryReadCommandMarkers =
    [
        "get-content",
        "get-childitem",
        "cat ",
        "type ",
        "ls ",
        "pwd",
        "git log",
        "git rev-parse"
    ];

    private static readonly HashSet<string> NonExecutingCommandPrefixes =
        new(
            [
                "echo",
                "write-output",
                "write-host",
                "printf"
            ],
            StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> GenericResultWords =
        new(
            [
                "a",
                "an",
                "and",
                "all",
                "command",
                "complete",
                "completed",
                "contains",
                "content",
                "file",
                "found",
                "matched",
                "pass",
                "passed",
                "result",
                "source",
                "text",
                "run",
                "success",
                "successful",
                "test",
                "tests",
                "the",
                "was",
                "were"
            ],
            StringComparer.Ordinal);

    internal static IReadOnlyList<string> ValidatePassChecks(
        OutcomeQaResult result,
        IReadOnlyCollection<ToolCallRecord> toolCalls,
        string workspacePath,
        IReadOnlyCollection<string>? candidateRepositoryRelativePaths = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(toolCalls);
        var workspace = string.IsNullOrWhiteSpace(workspacePath)
            ? string.Empty
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        var candidateRoots = BuildCandidateRoots(
            workspace,
            candidateRepositoryRelativePaths);
        var errors = new List<string>();
        if (result.Criteria.Any(item =>
                item.Status == OutcomeCriterionStatus.PASS))
        {
            foreach (var call in toolCalls.Where(call =>
                         !IsAllowedQaToolCall(
                             call,
                             workspace,
                             candidateRoots)))
            {
                errors.Add(
                    $"QA PASS is invalid because host-observed tool '{call.ToolName}' " +
                    "was not a read/browser operation or a single scoped verification command");
            }
        }
        foreach (var criterion in result.Criteria.Where(item =>
                     item.Status == OutcomeCriterionStatus.PASS))
        {
            var hasCorrelatedObservation = false;
            foreach (var (check, index) in criterion.ChecksPerformed.Select(
                         (check, index) => (check, index)))
            {
                if (check.Kind is OutcomeEvidenceKind.Test or
                    OutcomeEvidenceKind.Command)
                {
                    if (check.ExitCode != 0)
                    {
                        errors.Add(
                            $"criterion {criterion.CriterionId} PASS check {index + 1} " +
                            "must report ExitCode 0");
                        continue;
                    }
                    var matchingCommand = toolCalls.FirstOrDefault(call =>
                        IsMatchingVerificationCommand(
                            call,
                            check,
                            workspace,
                            candidateRoots));
                    if (matchingCommand is null)
                    {
                        errors.Add(
                            $"criterion {criterion.CriterionId} PASS check {index + 1} " +
                            "does not match a successful host-observed command from the QA step; " +
                            "the call must be an assertion, test, or inspection command executed " +
                            "from the candidate workspace");
                        continue;
                    }
                    if (!ResultSupportsClaim(
                            matchingCommand,
                            check.ObservedResult))
                    {
                        errors.Add(
                            $"criterion {criterion.CriterionId} PASS check {index + 1} " +
                            "ObservedResult does not match the host-captured command result");
                        continue;
                    }
                    hasCorrelatedObservation = true;
                    continue;
                }

                var matchingInspection = toolCalls.FirstOrDefault(call =>
                    IsMatchingCandidateInspection(
                        call,
                        check.Locator,
                        workspace,
                        candidateRoots));
                if (matchingInspection is not null &&
                    ResultSupportsClaim(
                        matchingInspection,
                        check.ObservedResult))
                {
                    hasCorrelatedObservation = true;
                }
            }

            if (!hasCorrelatedObservation)
            {
                errors.Add(
                    $"criterion {criterion.CriterionId} PASS has no concrete " +
                    "host-observed verification; add a correlated successful command/test check");
            }
        }
        return errors;
    }

    private static bool IsMatchingVerificationCommand(
        ToolCallRecord call,
        OutcomeQaCheck check,
        string workspace,
        IReadOnlyCollection<string> candidateRoots)
    {
        if (!IsSuccessful(call) ||
            call.ExitCode != 0 ||
            !IsCommandTool(call) ||
            string.IsNullOrWhiteSpace(call.ResultDigest) &&
            string.IsNullOrWhiteSpace(call.ResultSummary) ||
            !WorkingDirectoryIsContained(call.WorkingDirectory, workspace) ||
            !WorkingDirectoryIsCandidateSafe(
                call.WorkingDirectory,
                workspace) ||
            HasShellComposition(call.NormalizedCommand) ||
            ContainsExcludedCandidatePath(call.NormalizedCommand) ||
            IsEvidenceOnlyPath(call.NormalizedCommand) ||
            ContainsOutsideWorkspacePath(
                call.NormalizedCommand,
                call.WorkingDirectory,
                workspace) ||
            !CommandArgumentResourcesAreCandidateScoped(
                call.NormalizedArguments,
                call.WorkingDirectory,
                workspace,
                candidateRoots))
        {
            return false;
        }

        var command = call.NormalizedCommand;
        var locatorMatches = !string.IsNullOrWhiteSpace(command)
            ? string.Equals(
                HostObservedToolLocator.Normalize(command),
                HostObservedToolLocator.Normalize(check.Locator),
                StringComparison.Ordinal)
            : HostObservedToolLocator.MatchesCommand(
                call.ArgumentsSummary,
                check.Locator);
        if (!locatorMatches)
        {
            return false;
        }

        var normalized = HostObservedToolLocator.Normalize(
            string.IsNullOrWhiteSpace(command)
                ? check.Locator
                : command).ToLowerInvariant();
        var firstToken = normalized.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries)[0];
        if (NonExecutingCommandPrefixes.Contains(firstToken))
        {
            return false;
        }
        var isTest = TestCommandMarkers.Any(marker =>
            ContainsCommandMarker(normalized, marker));
        if (check.Kind == OutcomeEvidenceKind.Test)
        {
            return isTest;
        }
        return isTest ||
               BuildAssertionCommandMarkers.Any(marker =>
                   ContainsCommandMarker(normalized, marker)) ||
               InspectionCommandMarkers.Any(marker =>
                   ContainsCommandMarker(normalized, marker));
    }

    private static bool IsAllowedQaToolCall(
        ToolCallRecord call,
        string workspace,
        IReadOnlyCollection<string> candidateRoots)
    {
        if (call.ToolType is "Read" or "Browser")
        {
            return true;
        }
        if (!IsCommandTool(call) ||
            string.IsNullOrWhiteSpace(call.NormalizedCommand) ||
            !WorkingDirectoryIsContained(call.WorkingDirectory, workspace) ||
            !WorkingDirectoryIsCandidateSafe(
                call.WorkingDirectory,
                workspace) ||
            HasShellComposition(call.NormalizedCommand) ||
            ContainsExcludedCandidatePath(call.NormalizedCommand) ||
            ContainsOutsideWorkspacePath(
                call.NormalizedCommand,
                call.WorkingDirectory,
                workspace) ||
            !CommandArgumentResourcesAreCandidateScoped(
                call.NormalizedArguments,
                call.WorkingDirectory,
                workspace,
                candidateRoots))
        {
            return false;
        }

        var normalized = HostObservedToolLocator.Normalize(
            call.NormalizedCommand).ToLowerInvariant();
        return TestCommandMarkers
                   .Concat(BuildAssertionCommandMarkers)
                   .Concat(InspectionCommandMarkers)
                   .Concat(AuxiliaryReadCommandMarkers)
                   .Any(marker => ContainsCommandMarker(normalized, marker));
    }

    private static bool WorkingDirectoryIsCandidateSafe(
        string workingDirectory,
        string workspace)
    {
        try
        {
            var relative = Path.GetRelativePath(
                Path.GetFullPath(workspace),
                Path.GetFullPath(workingDirectory));
            return !IsEvidenceOnlyPath(relative) &&
                   !HasTransientOrGitSegment(relative) &&
                   !PathEntryIsLink(workingDirectory, workspace);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            return false;
        }
    }

    private static bool ContainsExcludedCandidatePath(string command)
    {
        if (IsEvidenceOnlyPath(command))
        {
            return true;
        }
        var normalized = command.Replace('\\', '/');
        return CandidateFingerprintService.IgnoredTransientDirectories.Any(
            directory =>
                normalized.Contains(
                    "/" + directory + "/",
                    StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(
                    " " + directory + "/",
                    StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasShellComposition(string command)
    {
        var quote = '\0';
        for (var index = 0; index < command.Length; index++)
        {
            var character = command[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else if (character == '\\' && index + 1 < command.Length)
                {
                    index++;
                }
                continue;
            }
            if (character is '\'' or '"')
            {
                quote = character;
                continue;
            }
            if (character is ';' or '|' or '&' or '>' or '<' or '`' or
                '\r' or '\n')
            {
                return true;
            }
        }
        return quote != '\0';
    }

    private static bool IsMatchingCandidateInspection(
        ToolCallRecord call,
        string locator,
        string workspace,
        IReadOnlyCollection<string> candidateRoots)
    {
        if (!IsSuccessful(call) ||
            call.ToolType is not ("Read" or "Browser") ||
            string.IsNullOrWhiteSpace(call.ResultDigest) &&
            string.IsNullOrWhiteSpace(call.ResultSummary) ||
            !WorkingDirectoryIsContained(call.WorkingDirectory, workspace) ||
            !TryResolveCandidatePath(
                locator,
                call.WorkingDirectory,
                workspace,
                candidateRoots,
                out var resolved))
        {
            return false;
        }

        if ((HostObservedToolLocator.MatchesLocator(
                call.ArgumentsSummary,
                locator) ||
            HostObservedToolLocator.MatchesLocator(
                call.ArgumentsSummary,
                resolved)) &&
            NormalizedArgumentsReferenceCandidateResource(
                call.NormalizedArguments,
                locator,
                resolved,
                call.WorkingDirectory,
                workspace,
                candidateRoots))
        {
            return true;
        }
        return false;
    }

    private static bool IsSuccessful(ToolCallRecord call) =>
        call.Succeeded && call.ExitCode is null or 0;

    private static bool CommandArgumentResourcesAreCandidateScoped(
        string normalizedArguments,
        string workingDirectory,
        string workspace,
        IReadOnlyCollection<string> candidateRoots)
    {
        if (string.IsNullOrWhiteSpace(normalizedArguments))
        {
            return true;
        }
        try
        {
            using var document = JsonDocument.Parse(normalizedArguments);
            foreach (var resource in EnumerateResourceStrings(document.RootElement))
            {
                if (!TryResolveCandidatePath(
                        resource,
                        workingDirectory,
                        workspace,
                        candidateRoots,
                        out _))
                {
                    return false;
                }
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsCommandTool(ToolCallRecord call) =>
        call.ToolType == "Command" ||
        call.ToolType == "Unknown" &&
        !string.IsNullOrWhiteSpace(call.ArgumentsSummary);

    private static IReadOnlyCollection<string> BuildCandidateRoots(
        string workspace,
        IReadOnlyCollection<string>? repositoryRelativePaths)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return [];
        }
        var roots = new HashSet<string>(PathComparer);
        if (repositoryRelativePaths is null)
        {
            roots.Add(workspace);
        }
        else
        {
            foreach (var relativePath in repositoryRelativePaths)
            {
                if (string.IsNullOrWhiteSpace(relativePath) ||
                    Path.IsPathRooted(relativePath))
                {
                    continue;
                }
                var root = Path.GetFullPath(Path.Combine(
                    workspace,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (IsContained(root, workspace))
                {
                    roots.Add(Path.TrimEndingDirectorySeparator(root));
                }
            }
        }
        roots.Add(Path.Combine(workspace, ".customer-preview"));
        return roots;
    }

    private static bool TryResolveCandidatePath(
        string locator,
        string callWorkingDirectory,
        string workspace,
        IReadOnlyCollection<string> candidateRoots,
        out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(workspace) ||
            string.IsNullOrWhiteSpace(locator))
        {
            return false;
        }
        try
        {
            var candidateLocator = locator;
            if (Uri.TryCreate(locator, UriKind.Absolute, out var uri) &&
                uri.Scheme.Length > 1)
            {
                if (!uri.IsFile)
                {
                    return false;
                }
                candidateLocator = uri.LocalPath;
            }
            var candidatePath = Path.GetFullPath(
                Path.IsPathRooted(candidateLocator)
                    ? candidateLocator
                    : Path.Combine(
                        string.IsNullOrWhiteSpace(callWorkingDirectory)
                            ? workspace
                            : callWorkingDirectory,
                        candidateLocator));
            var relativeToCandidate = candidateRoots
                .Where(root => IsContained(candidatePath, root))
                .Select(root => Path.GetRelativePath(root, candidatePath))
                .OrderBy(relative => relative.Length)
                .FirstOrDefault();
            if (!IsContained(candidatePath, workspace) ||
                !File.Exists(candidatePath) &&
                !Directory.Exists(candidatePath) ||
                IsEvidenceOnlyPath(Path.GetRelativePath(workspace, candidatePath)) ||
                relativeToCandidate is null ||
                HasTransientOrGitSegment(relativeToCandidate) ||
                PathEntryIsLink(candidatePath, workspace))
            {
                return false;
            }

            resolved = candidatePath;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            return false;
        }
    }

    private static bool HasTransientOrGitSegment(string relativePath) =>
        relativePath.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment =>
                segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                CandidateFingerprintService.IgnoredTransientDirectories.Contains(
                    segment,
                    StringComparer.OrdinalIgnoreCase));

    private static bool PathEntryIsLink(string path, string workspace)
    {
        var current = Path.GetFullPath(path);
        var root = Path.GetFullPath(workspace);
        while (IsContained(current, root))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
            if (string.Equals(current, root, PathComparison))
            {
                return false;
            }
            current = Path.GetDirectoryName(current) ?? root;
        }
        return false;
    }

    private static bool NormalizedArgumentsReferenceCandidateResource(
        string normalizedArguments,
        string locator,
        string resolved,
        string workingDirectory,
        string workspace,
        IReadOnlyCollection<string> candidateRoots)
    {
        if (string.IsNullOrWhiteSpace(normalizedArguments))
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(normalizedArguments);
            var resources = EnumerateResourceStrings(document.RootElement)
                .ToArray();
            if (resources.Length == 0)
            {
                return false;
            }

            var matched = false;
            foreach (var resource in resources)
            {
                if (!TryResolveCandidatePath(
                        resource,
                        workingDirectory,
                        workspace,
                        candidateRoots,
                        out var resourcePath))
                {
                    return false;
                }
                if (string.Equals(
                        HostObservedToolLocator.Normalize(resource),
                        HostObservedToolLocator.Normalize(locator),
                        StringComparison.Ordinal) ||
                    string.Equals(
                        HostObservedToolLocator.Normalize(resourcePath),
                        HostObservedToolLocator.Normalize(resolved),
                        StringComparison.Ordinal))
                {
                    matched = true;
                }
            }
            return matched;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateResourceStrings(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }
        foreach (var property in value.EnumerateObject())
        {
            var isResource = property.Name is
                "path" or "paths" or "file" or "files" or "filename" or
                "directory" or "root" or "url";
            if (isResource)
            {
                if (property.Value.ValueKind == JsonValueKind.String &&
                    property.Value.GetString() is { Length: > 0 } text)
                {
                    yield return text;
                }
                else if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String &&
                            item.GetString() is { Length: > 0 } arrayText)
                        {
                            yield return arrayText;
                        }
                    }
                }
            }
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var nested in EnumerateResourceStrings(property.Value))
                {
                    yield return nested;
                }
            }
        }
    }

    private static bool ResultSupportsClaim(
        ToolCallRecord call,
        string observedResult)
    {
        if (string.IsNullOrWhiteSpace(call.ResultSummary))
        {
            return !string.IsNullOrWhiteSpace(call.ResultDigest) &&
                   observedResult.Contains(
                       call.ResultDigest,
                       StringComparison.Ordinal);
        }

        var claim = NormalizeResultText(observedResult);
        var actual = NormalizeResultText(call.ResultSummary);
        if (HasPositiveOutcomeWord(claim) &&
            HasNegativeOutcomeWord(actual))
        {
            return false;
        }
        if (actual.Contains(claim, StringComparison.Ordinal) ||
            claim.Contains(actual, StringComparison.Ordinal))
        {
            return true;
        }

        var claimNumbers = TokenizeResult(claim)
            .Where(token => token.All(char.IsDigit))
            .ToArray();
        var actualTokens = TokenizeResult(actual).ToHashSet(StringComparer.Ordinal);
        if (claimNumbers.Any(number => !actualTokens.Contains(number)))
        {
            return false;
        }

        var meaningfulClaimTokens = TokenizeResult(claim)
            .Where(token =>
                token.Length >= 4 &&
                !GenericResultWords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (meaningfulClaimTokens.Length > 0)
        {
            return meaningfulClaimTokens.All(actualTokens.Contains);
        }

        return HasPositiveOutcomeWord(claim) &&
               HasPositiveOutcomeWord(actual) &&
               !HasNegativeOutcomeWord(actual);
    }

    private static string NormalizeResultText(string value) =>
        string.Join(
            " ",
            TokenizeResult(value));

    private static IEnumerable<string> TokenizeResult(string value) =>
        Regex.Split(
                value.Normalize(NormalizationForm.FormKC).ToLowerInvariant(),
                @"[^\p{L}\p{N}_-]+",
                RegexOptions.CultureInvariant)
            .Where(token => token.Length > 0);

    private static bool HasPositiveOutcomeWord(string value) =>
        value.Contains("pass", StringComparison.Ordinal) ||
        value.Contains("success", StringComparison.Ordinal) ||
        value.Contains("matched", StringComparison.Ordinal) ||
        value.Contains("found", StringComparison.Ordinal) ||
        value.Contains("exists", StringComparison.Ordinal);

    private static bool HasNegativeOutcomeWord(string value) =>
        Regex.IsMatch(
            value,
            @"\b(?:[1-9]\d*)\s+(?:failed|errors?)\b|\b(?:failed|failure|exception)\b",
            RegexOptions.CultureInvariant) ||
        value.Contains("not found", StringComparison.Ordinal) ||
        value.Contains("missing", StringComparison.Ordinal);

    private static bool WorkingDirectoryIsContained(
        string workingDirectory,
        string workspace)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) ||
            string.IsNullOrWhiteSpace(workspace))
        {
            return false;
        }
        try
        {
            return IsContained(Path.GetFullPath(workingDirectory), workspace);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            return false;
        }
    }

    private static bool ContainsOutsideWorkspacePath(
        string command,
        string workingDirectory,
        string workspace)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }
        if (command.Contains("../", StringComparison.Ordinal) ||
            command.Contains(@"..\", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (Match match in Regex.Matches(
                     command,
                     @"(?<![\w.])(?:[A-Za-z]:[\\/][^\s;|""']+|/[^\s;|""']+)",
                     RegexOptions.CultureInvariant))
        {
            try
            {
                var path = Path.GetFullPath(
                    Path.IsPathRooted(match.Value)
                        ? match.Value
                        : Path.Combine(workingDirectory, match.Value));
                if (!IsContained(path, workspace))
                {
                    return true;
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                    NotSupportedException or
                    PathTooLongException)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsEvidenceOnlyPath(string value)
    {
        var normalized = value.Replace('\\', '/');
        return normalized.Contains(
                   "/.ai-harness/outcome-verification/",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(
                   ".ai-harness/outcome-verification/",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(
                   "/qa-context.json",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals(
                   "qa-context.json",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsCommandMarker(
        string command,
        string marker)
    {
        if (command.Equals(marker, StringComparison.Ordinal) ||
            command.StartsWith(marker + " ", StringComparison.Ordinal) ||
            command.Contains("| " + marker, StringComparison.Ordinal) ||
            command.Contains("; " + marker, StringComparison.Ordinal) ||
            command.Contains("&& " + marker, StringComparison.Ordinal) ||
            command.Contains("|| " + marker, StringComparison.Ordinal) ||
            command.Contains("\n" + marker, StringComparison.Ordinal))
        {
            return true;
        }
        return marker.EndsWith(' ') &&
               command.StartsWith(marker, StringComparison.Ordinal);
    }

    private static bool IsContained(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        return !Path.IsPathRooted(relative) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith(
                   ".." + Path.DirectorySeparatorChar,
                   StringComparison.Ordinal) &&
               !relative.StartsWith(
                   ".." + Path.AltDirectorySeparatorChar,
                   StringComparison.Ordinal);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}

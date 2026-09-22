using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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

    internal static string NormalizeCommand(string value) =>
        Normalize(value.ReplaceLineEndings("; "));

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

    private static IReadOnlyList<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        foreach (var character in command)
        {
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

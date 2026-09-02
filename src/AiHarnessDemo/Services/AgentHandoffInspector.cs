using System.Globalization;
using System.Text.RegularExpressions;

namespace AiHarnessDemo.Services;

internal static partial class AgentHandoffInspector
{
    private const int MaximumReasonCharacters = 600;

    [GeneratedRegex(
        @"(?im)^\s*HANDOFF_STATUS\s*:\s*(COMPLETE|PUSHBACK)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex HandoffStatusPattern();

    [GeneratedRegex(
        @"(?im)^\s*PUSHBACK_REASON\s*:\s*(?<reason>.+?)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PushbackReasonPattern();

    [GeneratedRegex(
        @"(?im)^\s*(?:(?:#{1,6}|[-*>])\s*)?(?:\*{1,2}|_{1,2})?PUSHBACK(?:\*{1,2}|_{1,2})?(?<reason>(?:\s+.*|:.*|-.*)?)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PushbackPattern();

    public static string? GetPushbackReason(string output)
    {
        var status = HandoffStatusPattern().Match(output);
        if (status.Success)
        {
            if (string.Equals(
                    status.Groups[1].Value,
                    "COMPLETE",
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var structuredReason = PushbackReasonPattern().Match(output);
            return structuredReason.Success
                ? Clip(CleanLine(structuredReason.Groups["reason"].Value))
                : "The agent explicitly rejected the upstream handoff.";
        }

        var match = PushbackPattern().Match(output);
        if (!match.Success)
        {
            return null;
        }

        var inlineReason = CleanLine(match.Groups["reason"].Value);
        if (!string.IsNullOrWhiteSpace(inlineReason))
        {
            return Clip(inlineReason);
        }

        foreach (var line in output[(match.Index + match.Length)..]
                     .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var reason = CleanLine(line);
            if (!string.IsNullOrWhiteSpace(reason))
            {
                return Clip(reason);
            }
        }

        return "The agent explicitly rejected the upstream handoff.";
    }

    private static string CleanLine(string value)
    {
        var cleaned = value
            .Trim()
            .TrimStart('#')
            .Trim()
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Trim()
            .TrimStart(':', '-')
            .Trim();
        if (cleaned.Length > 0 &&
            CharUnicodeInfo.GetUnicodeCategory(cleaned[0]) == UnicodeCategory.DashPunctuation)
        {
            cleaned = cleaned[1..].TrimStart();
        }

        return cleaned;
    }

    private static string Clip(string value) =>
        value.Length <= MaximumReasonCharacters
            ? value
            : value[..MaximumReasonCharacters] + "...";
}

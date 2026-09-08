using System.Globalization;
using System.Text.RegularExpressions;

namespace AiHarnessDemo.Services;

internal sealed record DynamicHandoffStatus(
    bool IsPushback,
    string? OwnerPlanStepKey,
    string? Reason);

internal static partial class AgentHandoffInspector
{
    private const int MaximumReasonCharacters = 600;
    private const int MaximumPlanStepKeyCharacters = 120;

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

    public static bool HasTerminalStatus(string output) =>
        HandoffStatusPattern().IsMatch(output);

    public static bool HasCompleteStatus(string output)
    {
        var matches = HandoffStatusPattern().Matches(output);
        return matches.Count == 1 &&
               string.Equals(
                   matches[0].Groups[1].Value,
                   "COMPLETE",
                   StringComparison.OrdinalIgnoreCase);
    }

    public static DynamicHandoffStatus ParseDynamic(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var lines = output
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Select(line => line.Trim())
            .ToArray();
        var statuses = lines
            .Where(line => line.StartsWith(
                "HANDOFF_STATUS:",
                StringComparison.Ordinal))
            .ToArray();
        if (statuses.Length != 1 ||
            statuses[0] is not ("HANDOFF_STATUS: COMPLETE" or "HANDOFF_STATUS: PUSHBACK"))
        {
            throw new InvalidOperationException(
                "studio-v2 output must contain exactly one exact " +
                "'HANDOFF_STATUS: COMPLETE' or 'HANDOFF_STATUS: PUSHBACK' line.");
        }
        if (!string.Equals(
                lines.FirstOrDefault(line => line.Length > 0),
                statuses[0],
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The studio-v2 handoff status must be the first non-empty output line.");
        }

        var ownerLines = lines
            .Where(line => line.StartsWith(
                "PUSHBACK_OWNER_STEP_ID:",
                StringComparison.Ordinal))
            .ToArray();
        var reasonLines = lines
            .Where(line => line.StartsWith(
                "PUSHBACK_REASON:",
                StringComparison.Ordinal))
            .ToArray();
        if (statuses[0] == "HANDOFF_STATUS: COMPLETE")
        {
            if (ownerLines.Length != 0 || reasonLines.Length != 0)
            {
                throw new InvalidOperationException(
                    "studio-v2 COMPLETE output cannot contain pushback owner or reason markers.");
            }
            return new DynamicHandoffStatus(false, null, null);
        }

        if (ownerLines.Length != 1 || reasonLines.Length != 1)
        {
            throw new InvalidOperationException(
                "studio-v2 PUSHBACK output must contain exactly one " +
                "PUSHBACK_OWNER_STEP_ID and one PUSHBACK_REASON line.");
        }
        var owner = ownerLines[0]["PUSHBACK_OWNER_STEP_ID:".Length..].Trim();
        var reason = reasonLines[0]["PUSHBACK_REASON:".Length..].Trim();
        if (string.IsNullOrWhiteSpace(owner) ||
            owner.Length > MaximumPlanStepKeyCharacters ||
            owner.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"PUSHBACK_OWNER_STEP_ID must contain 1-{MaximumPlanStepKeyCharacters} characters.");
        }
        if (string.IsNullOrWhiteSpace(reason) ||
            reason.Length > MaximumReasonCharacters ||
            reason.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"PUSHBACK_REASON must contain 1-{MaximumReasonCharacters} characters.");
        }
        return new DynamicHandoffStatus(true, owner, reason);
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

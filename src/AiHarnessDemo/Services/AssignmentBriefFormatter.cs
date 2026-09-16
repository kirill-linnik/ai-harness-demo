using System.Text.Json;
using System.Text.RegularExpressions;
using AiHarnessDemo.Core.Orchestration;

namespace AiHarnessDemo.Services;

internal static partial class AssignmentBriefFormatter
{
    private static readonly (string Property, string Heading)[] Sections =
    [
        (nameof(IntakeBrief.Goal), "Goal"),
        (nameof(IntakeBrief.Details), "Details"),
        (nameof(IntakeBrief.SuccessCriteria), "Success criteria"),
        (nameof(IntakeBrief.Constraints), "Constraints"),
        (nameof(IntakeBrief.Assumptions), "Assumptions")
    ];

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true
    };

    public static string Format(string brief)
    {
        ArgumentNullException.ThrowIfNull(brief);
        if (!brief.AsSpan().TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            return brief;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(brief);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The assignment brief contains invalid JSON and cannot be formatted.",
                exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.EnumerateObject().Count() != Sections.Length ||
                Sections.Any(section => !root.TryGetProperty(section.Property, out _)))
            {
                return "```json\n" +
                    JsonSerializer.Serialize(root, IndentedJsonOptions) +
                    "\n```";
            }

            return string.Join(
                "\n\n",
                Sections.Select(section =>
                    $"### {section.Heading}\n\n" +
                    FormatSection(root.GetProperty(section.Property), section.Property)));
        }
    }

    private static string FormatSection(JsonElement value, string property)
    {
        if (property == nameof(IntakeBrief.Goal))
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException(
                    "The assignment brief Goal must be a string.");
            }
            return EscapeMarkdown(value.GetString()!);
        }

        if (value.ValueKind != JsonValueKind.Array ||
            value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
        {
            throw new InvalidOperationException(
                $"The assignment brief {property} must be an array of strings.");
        }
        return value.GetArrayLength() == 0
            ? "None specified."
            : string.Join(
                "\n",
                value.EnumerateArray().Select(item =>
                    "- " + EscapeMarkdown(item.GetString()!).Replace("\n", "\n  ")));
    }

    private static string EscapeMarkdown(string value)
    {
        var escaped = MarkdownPunctuation().Replace(
            value.ReplaceLineEndings("\n"),
            @"\$0");
        return MarkdownBlockMarker().Replace(
            escaped,
            match =>
                match.Groups[1].Value +
                match.Groups[2].Value[..^1] +
                "\\" +
                match.Groups[2].Value[^1]);
    }

    [GeneratedRegex(@"[\\`*_\[\]<>#|~&]")]
    private static partial Regex MarkdownPunctuation();

    [GeneratedRegex(@"(?m)^( {0,3})([-+=]+|[0-9]{1,9}[.)])(?=[ \t]|$)")]
    private static partial Regex MarkdownBlockMarker();
}

using System.Text.RegularExpressions;
using AiHarnessDemo.Core.Domain;

namespace AiHarnessDemo.Services;

internal static partial class CustomerExternalFileGate
{
    [GeneratedRegex(
        @"(?im)^[ \t]*(?<label>photo|picture|image|file|attachment|folder|directory)\b[^:\r\n]{0,64}:[ \t]*(?<path>[^\r\n]+)\r?$")]
    private static partial Regex FileReferencePattern();

    internal static IReadOnlyList<string> MissingFiles(FlowRun flow)
    {
        var provided = flow.Messages
            .Where(message => message.Role == ConversationRole.Customer)
            .SelectMany(message => message.Attachments)
            .Select(attachment => attachment.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var customerInputs = flow.Messages
            .Where(message => message.Role == ConversationRole.Customer)
            .Select(message => message.Content)
            .Prepend(flow.OriginalRequest)
            .Distinct(StringComparer.Ordinal);
        return customerInputs
            .SelectMany(input => FileReferencePattern().Matches(input)
                .Select(match => new
                {
                    Label = match.Groups["label"].Value,
                    Path = match.Groups["path"].Value.Trim().Trim('"', '\'', '`')
                }))
            .Where(item => IsOutsideProject(item.Path, flow.RepositoryPath))
            .Select(item =>
            {
                var name = item.Path.Replace('\\', '/').TrimEnd('/').Split('/').Last();
                if (name.Length == 0)
                {
                    return null;
                }
                var isFolder =
                    string.Equals(item.Label, "folder", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.Label, "directory", StringComparison.OrdinalIgnoreCase);
                return isFolder
                    ? provided.Count == 0
                        ? $"the needed files from folder {name}"
                        : null
                    : provided.Contains(name) ? null : name;
            })
            .Where(name => name is not null)
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool IsOutsideProject(string reference, string projectPath)
    {
        if (reference.Contains("://", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }
        var normalized = reference.Replace('/', '\\');
        if (normalized.StartsWith(@"Downloads\", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(@"~\", StringComparison.Ordinal))
        {
            return !File.Exists(Path.Combine(
                projectPath, reference.Replace('\\', Path.DirectorySeparatorChar)));
        }
        if (normalized.StartsWith(@"\\", StringComparison.Ordinal) ||
            normalized.Length >= 3 &&
            char.IsAsciiLetter(normalized[0]) &&
            normalized[1] == ':' &&
            normalized[2] == '\\')
        {
            if (!Path.IsPathFullyQualified(reference))
            {
                return true;
            }
            return !IsWithinProject(Path.GetFullPath(reference), projectPath);
        }
        if (normalized.StartsWith(@"..\", StringComparison.Ordinal))
        {
            return !IsWithinProject(
                Path.GetFullPath(Path.Combine(
                    projectPath,
                    reference.Replace('\\', Path.DirectorySeparatorChar))),
                projectPath);
        }
        return false;
    }

    private static bool IsWithinProject(string path, string projectPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPath));
        var candidate = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        return string.Equals(candidate, root, comparison) ||
               candidate.StartsWith(prefix, comparison);
    }
}

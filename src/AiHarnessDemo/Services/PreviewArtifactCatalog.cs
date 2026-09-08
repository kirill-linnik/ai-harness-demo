using System.Text.RegularExpressions;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Verification;

namespace AiHarnessDemo.Services;

public sealed record PreviewArtifact(
    string Id,
    string Label,
    string RootPath,
    string Url,
    string OpenUrl);

public sealed partial class PreviewArtifactCatalog(
    WorkflowDefinitionProvider? workflowProvider = null)
{
    private const string ArtifactDirectoryName = ".customer-preview";

    [GeneratedRegex(@"^[A-Za-z0-9_-]+$")]
    private static partial Regex ArtifactIdPattern();

    public IReadOnlyList<PreviewArtifact> Discover(FlowRun flow)
    {
        if (string.IsNullOrWhiteSpace(flow.WorkspacePath))
        {
            return [];
        }

        var workspaceRoot = WorkspacePathGuard.ValidateExistingRoot(
            flow.WorkspacePath,
            workflowProvider?.GetValidated().Config.Workspace.ResolvedRoot,
            "Customer preview");
        CandidateFingerprintService.ValidateLinksStayInside(workspaceRoot);
        var artifactRoot = Path.Combine(
            workspaceRoot,
            ArtifactDirectoryName);
        if (!Directory.Exists(artifactRoot))
        {
            return [];
        }

        return Directory.EnumerateDirectories(artifactRoot)
            .Select(directory => CreateArtifact(flow.Id, directory))
            .Where(artifact => artifact is not null)
            .Cast<PreviewArtifact>()
            .OrderBy(artifact => artifact.Id switch
            {
                "eu" => 0,
                "ee" => 1,
                _ => 2
            })
            .ThenBy(artifact => artifact.Id, StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyList<PreviewArtifact> DiscoverVerified(
        FlowRun flow,
        IReadOnlyList<CandidatePreviewArtifact> previewManifest)
    {
        ArgumentNullException.ThrowIfNull(previewManifest);
        if (string.IsNullOrWhiteSpace(flow.WorkspacePath))
        {
            return [];
        }

        var workspaceRoot = WorkspacePathGuard.ValidateExistingRoot(
            flow.WorkspacePath,
            workflowProvider?.GetValidated().Config.Workspace.ResolvedRoot,
            "Verified customer preview");
        return previewManifest
            .Select(item => item.RelativePath.Replace('\\', '/'))
            .Select(path => path.Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries))
            .Where(segments =>
                segments.Length >= 3 &&
                string.Equals(
                    segments[0],
                    ArtifactDirectoryName,
                    StringComparison.Ordinal) &&
                ArtifactIdPattern().IsMatch(segments[1]) &&
                (segments.Length == 3 &&
                 string.Equals(
                     segments[2],
                     "index.html",
                     StringComparison.Ordinal) ||
                 segments.Length == 4 &&
                 string.Equals(
                     segments[2],
                     "browser",
                     StringComparison.Ordinal) &&
                 string.Equals(
                     segments[3],
                     "index.html",
                     StringComparison.Ordinal)))
            .GroupBy(segments => segments[1], StringComparer.Ordinal)
            .Select(group =>
            {
                var browser = group.Any(segments =>
                    segments.Length == 4);
                var root = Path.Combine(
                    workspaceRoot,
                    ArtifactDirectoryName,
                    group.Key);
                if (browser)
                {
                    root = Path.Combine(root, "browser");
                }
                return CreateArtifact(flow.Id, group.Key, root);
            })
            .OrderBy(artifact => artifact.Id switch
            {
                "eu" => 0,
                "ee" => 1,
                _ => 2
            })
            .ThenBy(artifact => artifact.Id, StringComparer.Ordinal)
            .ToList();
    }

    public string ResolveFile(
        FlowRun flow,
        string artifactId,
        string? relativePath)
    {
        var artifact = Discover(flow).SingleOrDefault(item =>
            string.Equals(item.Id, artifactId, StringComparison.Ordinal))
            ?? throw new FileNotFoundException(
                $"Customer preview artifact '{artifactId}' was not found.");
        var requestedPath = string.IsNullOrWhiteSpace(relativePath)
            ? "index.html"
            : relativePath.Replace('/', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar);
        var resolved = Path.GetFullPath(Path.Combine(artifact.RootPath, requestedPath));
        var rootPrefix =
            Path.TrimEndingDirectorySeparator(artifact.RootPath) +
            Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(
                rootPrefix,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "Customer preview artifact paths must remain inside the generated preview.");
        }

        if (!File.Exists(resolved) && string.IsNullOrEmpty(Path.GetExtension(resolved)))
        {
            resolved = Path.Combine(artifact.RootPath, "index.html");
        }
        if (!File.Exists(resolved))
        {
            throw new FileNotFoundException(
                $"Customer preview file '{relativePath}' was not found.");
        }

        return resolved;
    }

    private static PreviewArtifact? CreateArtifact(Guid flowId, string directory)
    {
        var id = Path.GetFileName(directory);
        if (!ArtifactIdPattern().IsMatch(id))
        {
            return null;
        }

        var browserRoot = Path.Combine(directory, "browser");
        var root = File.Exists(Path.Combine(browserRoot, "index.html"))
            ? browserRoot
            : directory;
        if (!File.Exists(Path.Combine(root, "index.html")))
        {
            return null;
        }

        return CreateArtifact(flowId, id, root);
    }

    private static PreviewArtifact CreateArtifact(
        Guid flowId,
        string id,
        string root) =>
        new(
            id,
            id.ToLowerInvariant() switch
            {
                "eu" => "devclub.eu",
                "ee" => "devclub.ee",
                _ => id.Replace('-', ' ')
            },
            root,
            $"/api/previews/{flowId:D}/artifacts/{Uri.EscapeDataString(id)}/index.html",
            $"/api/previews/{flowId:D}/artifacts/{Uri.EscapeDataString(id)}/view");
}

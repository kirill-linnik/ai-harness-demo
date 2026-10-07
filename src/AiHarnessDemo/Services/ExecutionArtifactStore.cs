using System.Security.Cryptography;
using AiHarnessDemo.Core.Domain;

namespace AiHarnessDemo.Services;

internal static class ExecutionArtifactStore
{
    internal static async Task<FileStream> OpenWorkspaceContentAsync(
        string workspacePath,
        ExecutionArtifactRecord artifact,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) ||
            Path.IsPathRooted(artifact.RelativePath) ||
            artifact.Content.Length != 0)
        {
            throw new InvalidOperationException("The workspace artifact reference is invalid.");
        }
        var root = Path.GetFullPath(workspacePath);
        var path = Path.GetFullPath(Path.Combine(
            root, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(
                Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The execution artifact reference escapes its flow workspace.");
        }
        var current = root;
        foreach (var segment in new[] { string.Empty }
                     .Concat(Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)))
        {
            current = Path.Combine(current, segment);
            if (!Path.Exists(current))
            {
                throw new KeyNotFoundException(
                    "This large execution artifact is no longer available in the flow workspace.");
            }
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Execution artifact references cannot traverse links.");
            }
        }
        var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var handedOff = false;
        try
        {
            var digest = "sha256:" + Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (stream.Length != artifact.Length ||
                !string.Equals(digest, artifact.Digest, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The workspace execution artifact failed its integrity check.");
            }
            stream.Position = 0;
            handedOff = true;
            return stream;
        }
        finally
        {
            if (!handedOff)
            {
                await stream.DisposeAsync();
            }
        }
    }
}

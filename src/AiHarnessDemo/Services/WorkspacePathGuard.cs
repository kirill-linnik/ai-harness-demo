namespace AiHarnessDemo.Services;

/// <summary>
/// Resolves security-sensitive workspace paths without trusting lexical containment alone.
/// Reparse points are rejected at workspace boundaries so recovery, QA, and publication cannot
/// be redirected after a flow path has been persisted.
/// </summary>
internal static class WorkspacePathGuard
{
    public static string ValidateExistingRoot(
        string workspacePath,
        string? authorizedWorkspaceRoot,
        string operation)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            throw new InvalidOperationException(
                $"{operation} requires a flow workspace path.");
        }

        var workspace = Path.GetFullPath(workspacePath);
        if (!Directory.Exists(workspace))
        {
            throw new DirectoryNotFoundException(
                $"{operation} workspace does not exist: {workspace}");
        }
        RejectReparsePoint(workspace, $"{operation} workspace root");

        if (string.IsNullOrWhiteSpace(authorizedWorkspaceRoot))
        {
            return workspace;
        }

        var authorizedRoot = Path.GetFullPath(authorizedWorkspaceRoot);
        if (!Directory.Exists(authorizedRoot))
        {
            throw new DirectoryNotFoundException(
                $"{operation} authorized workspace root does not exist: {authorizedRoot}");
        }
        RejectReparsePoint(authorizedRoot, $"{operation} authorized workspace root");
        if (!IsContainedOrEqual(authorizedRoot, workspace))
        {
            throw new InvalidOperationException(
                $"{operation} workspace is outside its authorized root: {workspace}");
        }

        RejectReparseComponents(authorizedRoot, workspace, operation);
        var physicalAuthorizedRoot = ResolveExistingPath(authorizedRoot, operation);
        var physicalWorkspace = ResolveExistingPath(workspace, operation);
        if (!IsContainedOrEqual(physicalAuthorizedRoot, physicalWorkspace))
        {
            throw new InvalidOperationException(
                $"{operation} workspace physically resolves outside its authorized root: {workspace}");
        }

        return workspace;
    }

    public static string ValidateExistingContainedDirectory(
        string workspacePath,
        string candidatePath,
        string operation)
    {
        var workspace = ValidateExistingRoot(
            workspacePath,
            authorizedWorkspaceRoot: null,
            operation);
        var candidate = Path.GetFullPath(candidatePath);
        if (!IsContainedOrEqual(workspace, candidate))
        {
            throw new InvalidOperationException(
                $"{operation} path escaped the flow workspace: {candidatePath}");
        }
        if (!Directory.Exists(candidate))
        {
            throw new DirectoryNotFoundException(
                $"{operation} directory does not exist: {candidate}");
        }

        RejectReparseComponents(workspace, candidate, operation);
        var physicalWorkspace = ResolveExistingPath(workspace, operation);
        var physicalCandidate = ResolveExistingPath(candidate, operation);
        if (!IsContainedOrEqual(physicalWorkspace, physicalCandidate))
        {
            throw new InvalidOperationException(
                $"{operation} path physically resolves outside the flow workspace: {candidatePath}");
        }
        return candidate;
    }

    public static void ValidateAuthorizedRoot(
        string authorizedWorkspaceRoot,
        string operation)
    {
        if (string.IsNullOrWhiteSpace(authorizedWorkspaceRoot))
        {
            throw new InvalidOperationException(
                $"{operation} requires an authorized workspace root.");
        }
        var root = Path.GetFullPath(authorizedWorkspaceRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                $"{operation} authorized workspace root does not exist: {root}");
        }
        RejectReparsePoint(root, $"{operation} authorized workspace root");
    }

    private static void RejectReparseComponents(
        string root,
        string candidate,
        string operation)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{operation} path escaped the flow workspace: {candidate}");
        }

        var current = Path.GetFullPath(root);
        RejectReparsePoint(current, $"{operation} workspace path");
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) || File.Exists(current))
            {
                RejectReparsePoint(current, $"{operation} workspace path");
            }
        }
    }

    private static string ResolveExistingPath(string path, string operation)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException(
                $"{operation} path has no filesystem root: {path}");
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, segment);
            FileSystemInfo? info = null;
            try
            {
                var attributes = File.GetAttributes(next);
                info = (attributes & FileAttributes.Directory) != 0
                    ? new DirectoryInfo(next)
                    : new FileInfo(next);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                current = next;
                continue;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Unable to resolve {operation} path component '{next}'.",
                    exception);
            }

            if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                current = next;
                continue;
            }

            FileSystemInfo? target;
            try
            {
                target = info.ResolveLinkTarget(returnFinalTarget: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                    PlatformNotSupportedException)
            {
                throw new InvalidOperationException(
                    $"Unable to resolve {operation} reparse point '{next}'.",
                    exception);
            }
            current = target?.FullName
                ?? throw new InvalidOperationException(
                    $"Unable to resolve {operation} reparse point '{next}'.");
        }
        return Path.GetFullPath(current);
    }

    private static void RejectReparsePoint(string path, string label)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"{label} cannot be a symbolic link, junction, or reparse point: {path}");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Unable to inspect {label} '{path}'.",
                exception);
        }
    }

    private static bool IsContainedOrEqual(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(candidate));
        return string.Equals(fullRoot, fullCandidate, comparison) ||
               fullCandidate.StartsWith(
                   fullRoot + Path.DirectorySeparatorChar,
                   comparison);
    }
}

using System.Text;
using AuditionModStudio.Core.Paths;

namespace AuditionModStudio.Infrastructure.Paths;

public sealed class PathSecurity : IPathSecurity
{
    private static readonly char[] Separators = ['\\', '/'];

    public string ResolvePathWithinRoot(string rootDirectory, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));

        if (!Path.IsPathFullyQualified(canonicalRoot))
        {
            throw new ArgumentException("The approved root must be fully qualified.", nameof(rootDirectory));
        }

        if (Path.IsPathRooted(relativePath)
            || relativePath.StartsWith('\\')
            || relativePath.StartsWith('/'))
        {
            throw new ArgumentException("A relative path was required.", nameof(relativePath));
        }

        var segments = relativePath.Split(Separators, StringSplitOptions.None);
        if (segments.Length == 0 || segments.Any(static segment => segment.Length == 0))
        {
            throw new ArgumentException("The relative path contains an empty segment.", nameof(relativePath));
        }

        var normalizedSegments = new string[segments.Length];
        for (var index = 0; index < segments.Length; index++)
        {
            normalizedSegments[index] = ValidateSegment(segments[index], relativePath);
        }

        var candidate = Path.GetFullPath(Path.Combine([canonicalRoot, .. normalizedSegments]));
        var relativeToRoot = Path.GetRelativePath(canonicalRoot, candidate);

        if (Path.IsPathRooted(relativeToRoot)
            || relativeToRoot.Equals("..", StringComparison.Ordinal)
            || relativeToRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativeToRoot.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException("The relative path escapes the approved root.", nameof(relativePath));
        }

        return candidate;
    }

    public void EnsureNoReparsePoints(string rootDirectory, string candidatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);

        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var canonicalCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        var relative = Path.GetRelativePath(canonicalRoot, canonicalCandidate);

        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The candidate path is outside the approved root.");
        }

        CheckExistingPath(canonicalRoot);
        if (relative.Equals(".", StringComparison.Ordinal))
        {
            return;
        }

        var current = canonicalRoot;
        foreach (var segment in relative.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            CheckExistingPath(current);
        }
    }

    private static string ValidateSegment(string segment, string originalPath)
    {
        var normalized = segment.Normalize(NormalizationForm.FormC);
        if (normalized is "." or "..")
        {
            throw new ArgumentException("Relative navigation segments are not allowed.", nameof(originalPath));
        }

        if (normalized.EndsWith(' ') || normalized.EndsWith('.'))
        {
            throw new ArgumentException("Windows path segments cannot end with a dot or space.", nameof(originalPath));
        }

        if (normalized.IndexOfAny(['\0', ':', '"', '<', '>', '|', '*', '?']) >= 0
            || normalized.Any(static character => character < ' '))
        {
            throw new ArgumentException("The relative path contains invalid Windows filename characters.", nameof(originalPath));
        }

        var deviceName = normalized.Split('.', 2)[0];
        if (IsReservedDeviceName(deviceName))
        {
            throw new ArgumentException("The relative path contains a reserved Windows device name.", nameof(originalPath));
        }

        return normalized;
    }

    private static bool IsReservedDeviceName(string value)
    {
        if (value.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || value.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || value.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || value.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return value.Length == 4
            && (value.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && value[3] is >= '1' and <= '9';
    }

    private static void CheckExistingPath(string path)
    {
        if (!Path.Exists(path))
        {
            return;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Reparse points are not allowed in managed application paths.");
        }
    }
}

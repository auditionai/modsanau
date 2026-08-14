namespace AuditionModStudio.Core.Paths;

/// <summary>
/// Canonicalizes untrusted relative paths and confines them to an approved root.
/// </summary>
public interface IPathSecurity
{
    string ResolvePathWithinRoot(string rootDirectory, string relativePath);

    void EnsureNoReparsePoints(string rootDirectory, string candidatePath);
}

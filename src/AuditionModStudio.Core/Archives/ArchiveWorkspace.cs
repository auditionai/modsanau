using AuditionModStudio.Core.Workspaces;

namespace AuditionModStudio.Core.Archives;

public sealed record ArchiveWorkspace(
    ISecureWorkspace SecureWorkspace,
    string WorkingArchiveRelativePath,
    string ExtractDirectoryRelativePath)
{
    public static ArchiveWorkspace Create(
        ISecureWorkspace secureWorkspace,
        AuditionArchiveTemplate archive)
    {
        ArgumentNullException.ThrowIfNull(secureWorkspace);
        ArgumentNullException.ThrowIfNull(archive);
        return new(secureWorkspace, archive.FileName, archive.ExpectedExtractFolderName);
    }
}

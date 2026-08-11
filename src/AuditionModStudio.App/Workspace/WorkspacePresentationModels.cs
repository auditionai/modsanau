using System.Collections.Immutable;
using AuditionModStudio.Core.Projects;

namespace AuditionModStudio.App.Workspace;

public enum WorkspaceMappingFilter
{
    All,
    ManifestMapped,
    Unmapped
}

public sealed record WorkspaceTextureItem(
    string RelativePath,
    string DirectoryRelativePath,
    string FileName,
    string DisplayName,
    string TargetSize,
    string Format,
    TextureState State,
    string Validation,
    bool IsManifestMapped,
    bool IsEditable,
    string RecommendedEditMode)
{
    public string DisplayLabel => string.Equals(DisplayName, FileName, StringComparison.Ordinal)
        ? FileName
        : $"{DisplayName} ({FileName})";
}

public sealed record WorkspaceFolderItem(
    string DirectoryRelativePath,
    ImmutableArray<WorkspaceTextureItem> Textures)
{
    public string DisplayLabel => string.IsNullOrEmpty(DirectoryRelativePath)
        ? "Archive root"
        : DirectoryRelativePath;
}

using System.Collections.Immutable;
using AuditionModStudio.Core.Projects;

namespace AuditionModStudio.App.Workspace;

public enum WorkspaceMappingFilter
{
    All,
    ManifestMapped,
    Unmapped
}

public enum TextureStatusFilter { All, Modified, Original, Invalid, AI }

public enum TextureSizeFilter { All, Small, Medium, Large }

public enum TextureAlphaFilter { All, HasAlpha, NoAlpha }

public sealed record WorkspaceCategoryFilterOption(string? Category, string Label);

public sealed record WorkspaceTextureItem(
    string RelativePath,
    string DirectoryRelativePath,
    string FileName,
    string DisplayName,
    int Width,
    int Height,
    string TargetSize,
    string Format,
    string Category,
    bool HasAlpha,
    TextureState State,
    string Validation,
    bool IsManifestMapped,
    bool IsEditable,
    string RecommendedEditMode)
{
    public const int SmallMaximumDimension = 512;
    public const int MediumMaximumDimension = 2048;

    public string DisplayLabel => string.Equals(DisplayName, FileName, StringComparison.Ordinal)
        ? FileName
        : $"{DisplayName} ({FileName})";

    public int MaximumDimension => Math.Max(Width, Height);
}

public sealed record WorkspaceFolderItem(
    string DirectoryRelativePath,
    ImmutableArray<WorkspaceTextureItem> Textures)
{
    public string DisplayLabel => string.IsNullOrEmpty(DirectoryRelativePath)
        ? "Archive root"
        : DirectoryRelativePath;
}

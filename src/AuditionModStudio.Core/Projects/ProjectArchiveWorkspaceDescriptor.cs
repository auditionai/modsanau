namespace AuditionModStudio.Core.Projects;

public sealed record ProjectArchiveWorkspaceDescriptor(
    int SchemaVersion,
    Guid ProjectId,
    string DisplayName,
    string WorkspaceId,
    ArchiveTemplateReference ArchiveTemplate,
    string WorkingArchiveRelativePath,
    string ExtractedDirectoryRelativePath,
    string BuildOutputDirectoryRelativePath,
    string? WorkingKeydatRelativePath,
    string ManifestRelativePath,
    string WorkingArchiveSha256,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUpdatedAt,
    ProjectArchiveWorkspaceState State);

using System.Text.Json;
using System.Text.Json.Serialization;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Paths;
using AuditionModStudio.Core.Projects;

namespace AuditionModStudio.Projects;

public sealed class ProjectArchiveWorkspaceManifestStore(IPathSecurity pathSecurity)
    : IProjectArchiveWorkspaceManifestStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public async Task WriteAsync(
        string workspaceRoot,
        ProjectArchiveWorkspaceDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var manifestPath = pathSecurity.ResolvePathWithinRoot(workspaceRoot, descriptor.ManifestRelativePath);
        pathSecurity.EnsureNoReparsePoints(workspaceRoot, manifestPath);
        if (File.Exists(manifestPath))
        {
            throw new IOException("The project archive workspace manifest already exists.");
        }

        var temporaryPath = pathSecurity.ResolvePathWithinRoot(
            workspaceRoot,
            $".{Path.GetFileName(descriptor.ManifestRelativePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        WorkspaceManifest.FromDescriptor(descriptor),
                        SerializerOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, manifestPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<ProjectArchiveWorkspaceDescriptor> ReadAsync(
        string workspaceRoot,
        string manifestRelativePath,
        CancellationToken cancellationToken = default)
    {
        var manifestPath = pathSecurity.ResolvePathWithinRoot(workspaceRoot, manifestRelativePath);
        pathSecurity.EnsureNoReparsePoints(workspaceRoot, manifestPath);
        await using var stream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var manifest = await JsonSerializer.DeserializeAsync<WorkspaceManifest>(
                stream,
                SerializerOptions,
                cancellationToken)
            .ConfigureAwait(false);
        return (manifest ?? throw new InvalidDataException("The project archive workspace manifest is empty."))
            .ToDescriptor();
    }

    private sealed record WorkspaceManifest(
        int SchemaVersion,
        Guid ProjectId,
        string DisplayName,
        string WorkspaceId,
        string ArchiveTemplateId,
        string? ArchiveTemplateVersion,
        string SourceSha256,
        string? CompatibleGameBuild,
        string ArchiveEngineId,
        string RegionProfileId,
        string WorkingArchiveRelativePath,
        string ExtractedDirectoryRelativePath,
        string BuildOutputDirectoryRelativePath,
        string? WorkingKeydatRelativePath,
        string ManifestRelativePath,
        string WorkingArchiveSha256,
        DateTimeOffset CreatedAt,
        DateTimeOffset LastUpdatedAt,
        ProjectArchiveWorkspaceState State)
    {
        public static WorkspaceManifest FromDescriptor(ProjectArchiveWorkspaceDescriptor descriptor) => new(
            descriptor.SchemaVersion,
            descriptor.ProjectId,
            descriptor.DisplayName,
            descriptor.WorkspaceId,
            descriptor.ArchiveTemplate.TemplateId.Value,
            descriptor.ArchiveTemplate.TemplateVersion?.Value,
            descriptor.ArchiveTemplate.SourceSha256.Value,
            descriptor.ArchiveTemplate.CompatibleGameBuild?.Value,
            descriptor.ArchiveTemplate.EngineType.Id,
            descriptor.ArchiveTemplate.RegionProfileId,
            descriptor.WorkingArchiveRelativePath,
            descriptor.ExtractedDirectoryRelativePath,
            descriptor.BuildOutputDirectoryRelativePath,
            descriptor.WorkingKeydatRelativePath,
            descriptor.ManifestRelativePath,
            descriptor.WorkingArchiveSha256,
            descriptor.CreatedAt,
            descriptor.LastUpdatedAt,
            descriptor.State);

        public ProjectArchiveWorkspaceDescriptor ToDescriptor() => new(
            SchemaVersion,
            ProjectId,
            DisplayName,
            WorkspaceId,
            new(
                ArchiveTemplateId,
                ArchiveTemplateVersion,
                SourceSha256,
                new ArchiveEngineType(ArchiveEngineId),
                RegionProfileId,
                CompatibleGameBuild),
            WorkingArchiveRelativePath,
            ExtractedDirectoryRelativePath,
            BuildOutputDirectoryRelativePath,
            WorkingKeydatRelativePath,
            ManifestRelativePath,
            WorkingArchiveSha256,
            CreatedAt,
            LastUpdatedAt,
            State);
    }
}

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Paths;
using AuditionModStudio.Core.Projects;

namespace AuditionModStudio.Projects;

public sealed class AuditionProjectStore(IAppPaths appPaths, IPathSecurity pathSecurity) : IAuditionProjectStore
{
    public const string ProjectFileExtension = ".audproj";

    public async Task<AuditionProjectStoreResult> SaveAsync(
        AuditionProject project,
        CancellationToken cancellationToken = default)
    {
        if (project is null || project.ProjectId == Guid.Empty || project.SchemaVersion != AuditionProject.CurrentSchemaVersion)
        {
            return AuditionProjectStoreResult.Failure(
                AuditionProjectStoreFailureReason.InvalidProject,
                "AUDPROJ_SAVE_PROJECT_INVALID");
        }

        try
        {
            var fileName = GetFileName(project.ProjectId);
            await ProjectAtomicJsonWriter.WriteAsync(
                appPaths.ProjectsDirectory,
                fileName,
                AuditionProjectDocument.FromProject(project),
                pathSecurity,
                cancellationToken).ConfigureAwait(false);
            return AuditionProjectStoreResult.Success(fileName);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AuditionProjectStoreResult.Failure(
                AuditionProjectStoreFailureReason.Cancelled,
                "AUDPROJ_SAVE_CANCELLED");
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or ArgumentException
                                          or JsonException)
        {
            return AuditionProjectStoreResult.Failure(
                AuditionProjectStoreFailureReason.IoFailure,
                "AUDPROJ_SAVE_FAILED");
        }
    }

    public Task<AuditionProjectStoreResult> DeleteAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty)
        {
            return Task.FromResult(AuditionProjectStoreResult.Failure(
                AuditionProjectStoreFailureReason.InvalidProject,
                "AUDPROJ_DELETE_PROJECT_INVALID"));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = pathSecurity.ResolvePathWithinRoot(appPaths.ProjectsDirectory, GetFileName(projectId));
            pathSecurity.EnsureNoReparsePoints(appPaths.ProjectsDirectory, path);
            File.Delete(path);
            return Task.FromResult(AuditionProjectStoreResult.Success(GetFileName(projectId)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(AuditionProjectStoreResult.Failure(
                AuditionProjectStoreFailureReason.Cancelled,
                "AUDPROJ_DELETE_CANCELLED"));
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException
                                          or ArgumentException)
        {
            return Task.FromResult(AuditionProjectStoreResult.Failure(
                AuditionProjectStoreFailureReason.IoFailure,
                "AUDPROJ_DELETE_FAILED"));
        }
    }

    public async Task<AuditionProjectLoadResult> LoadAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty)
        {
            return AuditionProjectLoadResult.Failure(
                AuditionProjectLoadFailureReason.InvalidProjectId,
                "AUDPROJ_LOAD_PROJECT_ID_INVALID");
        }

        try
        {
            var path = pathSecurity.ResolvePathWithinRoot(appPaths.ProjectsDirectory, GetFileName(projectId));
            pathSecurity.EnsureNoReparsePoints(appPaths.ProjectsDirectory, path);
            if (!File.Exists(path))
            {
                return AuditionProjectLoadResult.Failure(
                    AuditionProjectLoadFailureReason.Missing,
                    "AUDPROJ_LOAD_MISSING");
            }

            var document = await ProjectAtomicJsonWriter.ReadAsync<AuditionProjectDocument>(
                appPaths.ProjectsDirectory,
                GetFileName(projectId),
                pathSecurity,
                cancellationToken).ConfigureAwait(false);
            if (document is null || document.ProjectId != projectId)
            {
                return AuditionProjectLoadResult.Failure(
                    AuditionProjectLoadFailureReason.InvalidProject,
                    "AUDPROJ_LOAD_ID_MISMATCH");
            }

            if (document.SchemaVersion != AuditionProject.CurrentSchemaVersion)
            {
                return AuditionProjectLoadResult.Failure(
                    AuditionProjectLoadFailureReason.UnsupportedSchema,
                    "AUDPROJ_LOAD_SCHEMA_UNSUPPORTED");
            }

            var model = document.ToProject();
            return model.Succeeded
                ? AuditionProjectLoadResult.Success(model.Project!)
                : AuditionProjectLoadResult.Failure(
                    AuditionProjectLoadFailureReason.InvalidProject,
                    model.Issues.FirstOrDefault()?.DiagnosticCode ?? "AUDPROJ_LOAD_MODEL_INVALID");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AuditionProjectLoadResult.Failure(
                AuditionProjectLoadFailureReason.Cancelled,
                "AUDPROJ_LOAD_CANCELLED");
        }
        catch (JsonException)
        {
            return AuditionProjectLoadResult.Failure(
                AuditionProjectLoadFailureReason.Corrupt,
                "AUDPROJ_LOAD_CORRUPT");
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or ArgumentException)
        {
            return AuditionProjectLoadResult.Failure(
                AuditionProjectLoadFailureReason.IoFailure,
                "AUDPROJ_LOAD_FAILED");
        }
    }

    public static string GetFileName(Guid projectId) => $"{projectId:N}{ProjectFileExtension}";
}

public sealed class ProjectMetadataCache(IAppPaths appPaths, IPathSecurity pathSecurity) : IProjectMetadataCache
{
    private const string CacheDirectoryName = "ProjectMetadata";

    public async Task<ProjectMetadataCacheResult> StoreAsync(
        Guid projectId,
        IEnumerable<ProjectTextureMetadataSnapshot> textures,
        CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty || textures is null)
        {
            return ProjectMetadataCacheResult.Failure(
                ProjectMetadataCacheFailureReason.InvalidData,
                "PROJECT_METADATA_INVALID");
        }

        var materialized = textures.ToImmutableArray();
        if (materialized.Any(item => item is null
                                     || !item.RelativePath.IsValid
                                     || !item.SourceSha256.IsValid
                                     || item.Metadata is null
                                     || item.Metadata.Width <= 0
                                     || item.Metadata.Height <= 0)
            || materialized.Select(item => item.RelativePath.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != materialized.Length)
        {
            return ProjectMetadataCacheResult.Failure(
                ProjectMetadataCacheFailureReason.InvalidData,
                "PROJECT_METADATA_INVALID");
        }

        try
        {
            pathSecurity.EnsureNoReparsePoints(appPaths.CacheDirectory, appPaths.CacheDirectory);
            var directory = pathSecurity.ResolvePathWithinRoot(appPaths.CacheDirectory, CacheDirectoryName);
            var ordered = materialized.OrderBy(item => item.RelativePath.Value, StringComparer.Ordinal).ToImmutableArray();
            await ProjectAtomicJsonWriter.WriteAsync(
                directory,
                GetFileName(projectId),
                new ProjectMetadataDocument(
                    1,
                    projectId,
                    ordered.Select(ProjectTextureMetadataDocument.FromSnapshot).ToImmutableArray()),
                pathSecurity,
                cancellationToken).ConfigureAwait(false);
            return ProjectMetadataCacheResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ProjectMetadataCacheResult.Failure(
                ProjectMetadataCacheFailureReason.Cancelled,
                "PROJECT_METADATA_CANCELLED");
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or ArgumentException
                                          or JsonException)
        {
            return ProjectMetadataCacheResult.Failure(
                ProjectMetadataCacheFailureReason.IoFailure,
                "PROJECT_METADATA_SAVE_FAILED");
        }
    }

    public Task<ProjectMetadataCacheResult> DeleteAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty)
        {
            return Task.FromResult(ProjectMetadataCacheResult.Failure(
                ProjectMetadataCacheFailureReason.InvalidData,
                "PROJECT_METADATA_DELETE_INVALID"));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            pathSecurity.EnsureNoReparsePoints(appPaths.CacheDirectory, appPaths.CacheDirectory);
            var directory = pathSecurity.ResolvePathWithinRoot(appPaths.CacheDirectory, CacheDirectoryName);
            var path = pathSecurity.ResolvePathWithinRoot(directory, GetFileName(projectId));
            pathSecurity.EnsureNoReparsePoints(appPaths.CacheDirectory, path);
            File.Delete(path);
            return Task.FromResult(ProjectMetadataCacheResult.Success());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(ProjectMetadataCacheResult.Failure(
                ProjectMetadataCacheFailureReason.Cancelled,
                "PROJECT_METADATA_DELETE_CANCELLED"));
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException
                                          or ArgumentException)
        {
            return Task.FromResult(ProjectMetadataCacheResult.Failure(
                ProjectMetadataCacheFailureReason.IoFailure,
                "PROJECT_METADATA_DELETE_FAILED"));
        }
    }

    public async Task<ProjectMetadataCacheValidationResult> ValidateAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty)
        {
            return new(ProjectMetadataCacheValidationStatus.InvalidProjectId, "PROJECT_METADATA_PROJECT_ID_INVALID");
        }

        try
        {
            pathSecurity.EnsureNoReparsePoints(appPaths.CacheDirectory, appPaths.CacheDirectory);
            var directory = pathSecurity.ResolvePathWithinRoot(appPaths.CacheDirectory, CacheDirectoryName);
            var path = pathSecurity.ResolvePathWithinRoot(directory, GetFileName(projectId));
            pathSecurity.EnsureNoReparsePoints(appPaths.CacheDirectory, path);
            if (!File.Exists(path))
            {
                return new(ProjectMetadataCacheValidationStatus.Missing, "PROJECT_METADATA_MISSING");
            }

            var document = await ProjectAtomicJsonWriter.ReadAsync<ProjectMetadataDocument>(
                directory,
                GetFileName(projectId),
                pathSecurity,
                cancellationToken).ConfigureAwait(false);
            if (document is null || document.SchemaVersion != 1 || document.ProjectId != projectId
                || document.Textures.IsDefault
                || document.Textures.Any(texture => string.IsNullOrWhiteSpace(texture.RelativePath)
                                                    || string.IsNullOrWhiteSpace(texture.SourceSha256)
                                                    || texture.Metadata is null)
                || document.Textures.Select(texture => texture.RelativePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.Textures.Length)
            {
                return new(ProjectMetadataCacheValidationStatus.Corrupt, "PROJECT_METADATA_INVALID");
            }

            return new(ProjectMetadataCacheValidationStatus.Valid, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(ProjectMetadataCacheValidationStatus.Cancelled, "PROJECT_METADATA_VALIDATE_CANCELLED");
        }
        catch (Exception exception) when (exception is JsonException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or ArgumentException)
        {
            return new(ProjectMetadataCacheValidationStatus.Corrupt, "PROJECT_METADATA_CORRUPT");
        }
    }

    public async Task<ProjectMetadataCacheLoadResult> LoadAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty)
        {
            return ProjectMetadataCacheLoadResult.Failure(
                ProjectMetadataCacheLoadStatus.InvalidProjectId,
                "PROJECT_METADATA_PROJECT_ID_INVALID");
        }

        try
        {
            pathSecurity.EnsureNoReparsePoints(appPaths.CacheDirectory, appPaths.CacheDirectory);
            var directory = pathSecurity.ResolvePathWithinRoot(appPaths.CacheDirectory, CacheDirectoryName);
            var path = pathSecurity.ResolvePathWithinRoot(directory, GetFileName(projectId));
            pathSecurity.EnsureNoReparsePoints(appPaths.CacheDirectory, path);
            if (!File.Exists(path))
            {
                return ProjectMetadataCacheLoadResult.Failure(
                    ProjectMetadataCacheLoadStatus.Missing,
                    "PROJECT_METADATA_MISSING");
            }

            var document = await ProjectAtomicJsonWriter.ReadAsync<ProjectMetadataDocument>(
                directory,
                GetFileName(projectId),
                pathSecurity,
                cancellationToken).ConfigureAwait(false);
            if (document is null || document.SchemaVersion != 1 || document.ProjectId != projectId
                || document.Textures.IsDefault
                || document.Textures.Select(texture => texture.RelativePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.Textures.Length)
            {
                return ProjectMetadataCacheLoadResult.Failure(
                    ProjectMetadataCacheLoadStatus.Corrupt,
                    "PROJECT_METADATA_INVALID");
            }

            var snapshots = document.Textures
                .Select(texture => texture.ToSnapshot())
                .OrderBy(texture => texture.RelativePath.Value, StringComparer.Ordinal)
                .ToImmutableArray();
            return ProjectMetadataCacheLoadResult.Success(snapshots);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ProjectMetadataCacheLoadResult.Failure(
                ProjectMetadataCacheLoadStatus.Cancelled,
                "PROJECT_METADATA_LOAD_CANCELLED");
        }
        catch (Exception exception) when (exception is JsonException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or ArgumentException)
        {
            return ProjectMetadataCacheLoadResult.Failure(
                ProjectMetadataCacheLoadStatus.Corrupt,
                "PROJECT_METADATA_CORRUPT");
        }
    }

    internal static string GetFileName(Guid projectId) => $"{projectId:N}.metadata.json";

    private sealed record ProjectMetadataDocument(
        int SchemaVersion,
        Guid ProjectId,
        ImmutableArray<ProjectTextureMetadataDocument> Textures);

    private sealed record ProjectTextureMetadataDocument(
        string RelativePath,
        string SourceSha256,
        DdsMetadata Metadata,
        string? ManifestSlotId)
    {
        public static ProjectTextureMetadataDocument FromSnapshot(ProjectTextureMetadataSnapshot snapshot) => new(
            snapshot.RelativePath.Value,
            snapshot.SourceSha256.Value,
            snapshot.Metadata,
            snapshot.ManifestSlotId?.Value);

        public ProjectTextureMetadataSnapshot ToSnapshot() => new(
            new(RelativePath),
            new(SourceSha256),
            Metadata,
            ManifestSlotId is null ? null : new TextureSlotId(ManifestSlotId));
    }
}

internal sealed record AuditionProjectDocument(
    int SchemaVersion,
    Guid ProjectId,
    string Name,
    string GameId,
    string ModId,
    AuditionProjectTemplateDocument Template,
    AuditionProjectWorkspaceDocument Workspace,
    ImmutableArray<AuditionProjectEditedTextureDocument> EditedTextures,
    ImmutableArray<AuditionProjectAssetDocument> ImageAssets,
    ImmutableArray<AuditionProjectAssetDocument> AiAssets,
    AuditionProjectEditStateDocument EditState,
    AuditionProjectBuildStateDocument BuildState,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static AuditionProjectDocument FromProject(AuditionProject project) => new(
        project.SchemaVersion,
        project.ProjectId,
        project.Name,
        project.GameId.Value,
        project.ModId.Value,
        new(
            project.TemplateIdentity.TemplateId.Value,
            project.TemplateIdentity.Version.Value,
            project.TemplateIdentity.Sha256.Value,
            project.TemplateIdentity.CompatibleGameBuild.Value),
        new(
            project.Workspace.WorkspaceId,
            project.Workspace.WorkingArchiveRelativePath.Value,
            project.Workspace.ExtractedRootRelativePath.Value),
        project.EditedTextures.Select(AuditionProjectEditedTextureDocument.FromRecord).ToImmutableArray(),
        project.ImageAssets.Select(AuditionProjectAssetDocument.FromRecord).ToImmutableArray(),
        project.AiAssets.Select(AuditionProjectAssetDocument.FromRecord).ToImmutableArray(),
        AuditionProjectEditStateDocument.FromSnapshot(project.EditState),
        AuditionProjectBuildStateDocument.FromSnapshot(project.BuildState),
        project.CreatedAt,
        project.UpdatedAt);

    public AuditionProjectCreateResult ToProject()
    {
        try
        {
            return AuditionProject.Create(
                SchemaVersion,
                ProjectId,
                Name,
                new(GameId),
                new(ModId),
                new(new(Template.TemplateId), new(Template.Version), new(Template.Sha256), new(Template.CompatibleGameBuild)),
                new(
                    Workspace.WorkspaceId,
                    new(Workspace.WorkingArchiveRelativePath),
                    new(Workspace.ExtractedRootRelativePath)),
                EditedTextures.Select(item => new ProjectEditedTextureRecord(
                    new(item.RelativePath),
                    new(item.OriginalSha256),
                    new(item.CurrentSha256),
                    new(item.CurrentImageAssetId),
                    item.Revision)),
                ImageAssets.Select(item => new ProjectAssetRecord(new(item.Id), new(item.RelativePath), new(item.Sha256))),
                AiAssets.Select(item => new ProjectAssetRecord(new(item.Id), new(item.RelativePath), new(item.Sha256))),
                new(
                    EditState.CurrentRevision,
                    EditState.SavedRevision,
                    EditState.ActiveTextureRelativePath is null ? null : new(EditState.ActiveTextureRelativePath),
                    EditState.History.Select(item => new ProjectEditHistoryRecord(
                        item.Revision,
                        new(item.TextureRelativePath),
                        item.Operation,
                        new(item.BeforeImageAssetId),
                        new(item.AfterImageAssetId))).ToImmutableArray()),
                new(
                    BuildState.Status,
                    BuildState.LastBuildAt,
                    BuildState.OutputArchiveRelativePath is null ? null : new(BuildState.OutputArchiveRelativePath),
                    BuildState.OutputSha256 is null ? null : new(BuildState.OutputSha256)),
                CreatedAt,
                UpdatedAt);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidOperationException
                                          or NullReferenceException)
        {
            return AuditionProjectCreateResult.Failure([
                new(AuditionProjectValidationFailureReason.InvalidRecord, "AUDPROJ_DOCUMENT_INVALID")]);
        }
    }
}

internal sealed record AuditionProjectTemplateDocument(
    string TemplateId,
    string Version,
    string Sha256,
    string CompatibleGameBuild);

internal sealed record AuditionProjectWorkspaceDocument(
    string WorkspaceId,
    string WorkingArchiveRelativePath,
    string ExtractedRootRelativePath);

internal sealed record AuditionProjectEditedTextureDocument(
    string RelativePath,
    string OriginalSha256,
    string CurrentSha256,
    string CurrentImageAssetId,
    long Revision)
{
    public static AuditionProjectEditedTextureDocument FromRecord(ProjectEditedTextureRecord record) => new(
        record.RelativePath.Value,
        record.OriginalSha256.Value,
        record.CurrentSha256.Value,
        record.CurrentImageAssetId.Value,
        record.Revision);
}

internal sealed record AuditionProjectAssetDocument(string Id, string RelativePath, string Sha256)
{
    public static AuditionProjectAssetDocument FromRecord(ProjectAssetRecord record) =>
        new(record.Id.Value, record.RelativePath.Value, record.Sha256.Value);
}

internal sealed record AuditionProjectEditHistoryDocument(
    long Revision,
    string TextureRelativePath,
    EditOperationKind Operation,
    string BeforeImageAssetId,
    string AfterImageAssetId)
{
    public static AuditionProjectEditHistoryDocument FromRecord(ProjectEditHistoryRecord record) => new(
        record.Revision,
        record.TextureRelativePath.Value,
        record.Operation,
        record.BeforeImageAssetId.Value,
        record.AfterImageAssetId.Value);
}

internal sealed record AuditionProjectEditStateDocument(
    long CurrentRevision,
    long SavedRevision,
    string? ActiveTextureRelativePath,
    ImmutableArray<AuditionProjectEditHistoryDocument> History)
{
    public static AuditionProjectEditStateDocument FromSnapshot(ProjectEditStateSnapshot snapshot) => new(
        snapshot.CurrentRevision,
        snapshot.SavedRevision,
        snapshot.ActiveTextureRelativePath?.Value,
        snapshot.History.Select(AuditionProjectEditHistoryDocument.FromRecord).ToImmutableArray());
}

internal sealed record AuditionProjectBuildStateDocument(
    ProjectBuildStatus Status,
    DateTimeOffset? LastBuildAt,
    string? OutputArchiveRelativePath,
    string? OutputSha256)
{
    public static AuditionProjectBuildStateDocument FromSnapshot(ProjectBuildStateSnapshot snapshot) => new(
        snapshot.Status,
        snapshot.LastBuildAt,
        snapshot.OutputArchiveRelativePath?.Value,
        snapshot.OutputSha256?.Value);
}

internal static class ProjectAtomicJsonWriter
{
    private const long MaximumDocumentBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static async Task WriteAsync<T>(
        string directory,
        string fileName,
        T value,
        IPathSecurity pathSecurity,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        pathSecurity.EnsureNoReparsePoints(directory, directory);
        var destination = pathSecurity.ResolvePathWithinRoot(directory, fileName);
        pathSecurity.EnsureNoReparsePoints(directory, destination);
        var temporary = pathSecurity.ResolvePathWithinRoot(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static async Task<T?> ReadAsync<T>(
        string directory,
        string fileName,
        IPathSecurity pathSecurity,
        CancellationToken cancellationToken)
    {
        var path = pathSecurity.ResolvePathWithinRoot(directory, fileName);
        pathSecurity.EnsureNoReparsePoints(directory, path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException("Project JSON document exceeds the supported size limit.");
        }

        return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken).ConfigureAwait(false);
    }
}

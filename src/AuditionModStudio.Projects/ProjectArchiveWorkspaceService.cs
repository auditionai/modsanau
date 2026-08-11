using System.Text.Json;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Paths;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Workspaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuditionModStudio.Projects;

public sealed class ProjectArchiveWorkspaceService(
    IAppPaths appPaths,
    IPathSecurity pathSecurity,
    ISecureWorkspaceService secureWorkspaceService,
    IProjectArchiveWorkspaceManifestStore manifestStore,
    TimeProvider? timeProvider = null,
    ILogger<ProjectArchiveWorkspaceService>? logger = null) : IProjectArchiveWorkspaceService,
    IProjectArchiveWorkspaceRetentionService, IProjectArchiveWorkspaceRemovalService
{
    public const int CurrentSchemaVersion = 1;
    public const string ManifestFileName = ".project-archive-workspace.json";

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ILogger<ProjectArchiveWorkspaceService> _logger =
        logger ?? NullLogger<ProjectArchiveWorkspaceService>.Instance;

    public void Retain(IProjectArchiveWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (secureWorkspaceService is not ISecureWorkspaceRetentionService retention)
        {
            throw new InvalidOperationException("The secure workspace provider does not support project retention.");
        }

        retention.Retain(workspace.ArchiveWorkspace.SecureWorkspace);
    }

    public Task<bool> RemoveAsync(
        IProjectArchiveWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return secureWorkspaceService is ISecureWorkspaceRemovalService removal
            ? removal.RemoveAsync(workspace.ArchiveWorkspace.SecureWorkspace, cancellationToken)
            : Task.FromResult(false);
    }

    public async Task<ProjectArchiveWorkspaceCreateResult> CreateAsync(
        ProjectArchiveWorkspaceCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestFailure = ValidateRequest(request);
        if (requestFailure is not null)
        {
            return requestFailure;
        }

        string sourcePath;
        string sourceHash;
        try
        {
            var sourceRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(request.PristineSource.TrustedRootDirectory));
            sourcePath = pathSecurity.ResolvePathWithinRoot(
                sourceRoot,
                request.ArchiveTemplate.SourceRelativePath);
            pathSecurity.EnsureNoReparsePoints(sourceRoot, sourcePath);
            if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length == 0)
            {
                return ProjectArchiveWorkspaceCreateResult.Failure(
                    ProjectArchiveWorkspaceFailureReason.SourceMissing,
                    "project_archive.source_missing");
            }

            sourceHash = await ProjectFileSha256.ComputeAsync(sourcePath, cancellationToken)
                .ConfigureAwait(false);
            if (request.ArchiveTemplate.ExpectedSha256 is not null
                && !ProjectFileSha256.EqualsHex(request.ArchiveTemplate.ExpectedSha256.Value.Value, sourceHash))
            {
                return ProjectArchiveWorkspaceCreateResult.Failure(
                    ProjectArchiveWorkspaceFailureReason.SourceHashMismatch,
                    "project_archive.source_hash_mismatch");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or IOException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Pristine archive source validation failed");
            return ProjectArchiveWorkspaceCreateResult.Failure(
                ProjectArchiveWorkspaceFailureReason.SourceMissing,
                "project_archive.source_invalid");
        }

        ISecureWorkspace? secureWorkspace = null;
        try
        {
            secureWorkspace = await secureWorkspaceService.CreateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }
        catch (Exception exception) when (exception is IOException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or UnauthorizedAccessException
                                          or ArgumentException)
        {
            _logger.LogWarning(exception, "Managed project workspace allocation failed");
            return ProjectArchiveWorkspaceCreateResult.Failure(
                ProjectArchiveWorkspaceFailureReason.WorkspaceCreationFailed,
                "project_archive.workspace_allocation_failed");
        }

        try
        {
            ValidateManagedWorkspace(secureWorkspace);

            var archiveWorkspace = ArchiveWorkspace.Create(secureWorkspace, request.ArchiveTemplate);
            var extractedDirectoryPath = pathSecurity.ResolvePathWithinRoot(
                secureWorkspace.Paths.ExtractedDirectory,
                archiveWorkspace.ExtractDirectoryRelativePath);
            pathSecurity.EnsureNoReparsePoints(
                secureWorkspace.Paths.ExtractedDirectory,
                extractedDirectoryPath);
            Directory.CreateDirectory(extractedDirectoryPath);
            pathSecurity.EnsureNoReparsePoints(
                secureWorkspace.Paths.ExtractedDirectory,
                extractedDirectoryPath);

            var workingArchivePath = pathSecurity.ResolvePathWithinRoot(
                secureWorkspace.Paths.WorkingDirectory,
                archiveWorkspace.WorkingArchiveRelativePath);
            pathSecurity.EnsureNoReparsePoints(secureWorkspace.Paths.WorkingDirectory, workingArchivePath);
            if (File.Exists(workingArchivePath))
            {
                throw new IOException("The new project workspace already contains a working archive.");
            }

            var copyHash = await CopyWorkingArchiveAsync(
                    sourcePath,
                    workingArchivePath,
                    secureWorkspace.Paths.WorkingDirectory,
                    sourceHash,
                    cancellationToken)
                .ConfigureAwait(false);

            var timestamp = _timeProvider.GetUtcNow();
            var descriptor = new ProjectArchiveWorkspaceDescriptor(
                CurrentSchemaVersion,
                request.ProjectId ?? Guid.NewGuid(),
                request.DisplayName.Trim(),
                secureWorkspace.Id,
                new(
                    request.ArchiveTemplate.TemplateId.Value,
                    request.ArchiveTemplate.TemplateVersion?.Value,
                    sourceHash,
                    request.ArchiveTemplate.EngineType,
                    request.ArchiveTemplate.RegionProfileId,
                    request.ArchiveTemplate.CompatibleGameBuild?.Value),
                Path.Combine("Working", request.ArchiveTemplate.FileName),
                Path.Combine("Extracted", request.ArchiveTemplate.ExpectedExtractFolderName),
                "BuildOutput",
                null,
                ManifestFileName,
                copyHash,
                timestamp,
                timestamp,
                ProjectArchiveWorkspaceState.Ready);

            try
            {
                await manifestStore.WriteAsync(
                        secureWorkspace.Paths.RootDirectory,
                        descriptor,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException
                                              or InvalidDataException
                                              or UnauthorizedAccessException
                                              or InvalidOperationException
                                              or ArgumentException)
            {
                throw new ProjectArchiveManifestWriteException(
                    "The project archive workspace manifest could not be committed.",
                    exception);
            }

            var lease = new ProjectArchiveWorkspaceLease(secureWorkspace, descriptor, archiveWorkspace);
            secureWorkspace = null;
            _logger.LogInformation(
                "Project archive workspace {WorkspaceId} is ready for template {TemplateId}",
                descriptor.WorkspaceId,
                descriptor.ArchiveTemplate.TemplateId);
            return ProjectArchiveWorkspaceCreateResult.Success(lease);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }
        catch (Exception exception) when (exception is IOException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or UnauthorizedAccessException
                                          or ArgumentException)
        {
            _logger.LogWarning(exception, "Project archive workspace creation failed");
            var reason = exception is ProjectArchiveManifestWriteException
                ? ProjectArchiveWorkspaceFailureReason.ManifestWriteFailed
                : ProjectArchiveWorkspaceFailureReason.WorkingCopyFailed;
            return ProjectArchiveWorkspaceCreateResult.Failure(reason, "project_archive.creation_failed");
        }
        finally
        {
            if (secureWorkspace is not null)
            {
                await secureWorkspace.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task<ProjectArchiveWorkspaceValidationResult> ValidateAsync(
        IProjectArchiveWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var descriptor = workspace.Descriptor;
        var secureWorkspace = workspace.ArchiveWorkspace.SecureWorkspace;
        try
        {
            ValidateManagedWorkspace(secureWorkspace);
            var manifestPath = pathSecurity.ResolvePathWithinRoot(
                secureWorkspace.Paths.RootDirectory,
                descriptor.ManifestRelativePath);
            pathSecurity.EnsureNoReparsePoints(secureWorkspace.Paths.RootDirectory, manifestPath);
            if (!File.Exists(manifestPath))
            {
                return Invalid(ProjectArchiveWorkspaceValidationStatus.ManifestMissing, "project_archive.manifest_missing");
            }

            ProjectArchiveWorkspaceDescriptor stored;
            try
            {
                stored = await manifestStore.ReadAsync(
                        secureWorkspace.Paths.RootDirectory,
                        descriptor.ManifestRelativePath,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is JsonException
                                              or InvalidDataException
                                              or ArgumentException)
            {
                _logger.LogWarning(exception, "Project archive workspace manifest is corrupt");
                return Invalid(ProjectArchiveWorkspaceValidationStatus.ManifestCorrupt, "project_archive.manifest_corrupt");
            }

            if (!ManifestMatches(descriptor, stored))
            {
                return Invalid(ProjectArchiveWorkspaceValidationStatus.ManifestMismatch, "project_archive.manifest_mismatch");
            }

            var workingArchivePath = pathSecurity.ResolvePathWithinRoot(
                secureWorkspace.Paths.RootDirectory,
                descriptor.WorkingArchiveRelativePath);
            pathSecurity.EnsureNoReparsePoints(secureWorkspace.Paths.RootDirectory, workingArchivePath);
            if (!File.Exists(workingArchivePath))
            {
                return Invalid(ProjectArchiveWorkspaceValidationStatus.WorkingArchiveMissing, "project_archive.working_missing");
            }

            var extractedDirectory = pathSecurity.ResolvePathWithinRoot(
                secureWorkspace.Paths.RootDirectory,
                descriptor.ExtractedDirectoryRelativePath);
            pathSecurity.EnsureNoReparsePoints(secureWorkspace.Paths.RootDirectory, extractedDirectory);
            if (!Directory.Exists(extractedDirectory))
            {
                return Invalid(ProjectArchiveWorkspaceValidationStatus.ExtractedDirectoryMissing, "project_archive.extracted_missing");
            }

            var buildOutputDirectory = pathSecurity.ResolvePathWithinRoot(
                secureWorkspace.Paths.RootDirectory,
                descriptor.BuildOutputDirectoryRelativePath);
            pathSecurity.EnsureNoReparsePoints(secureWorkspace.Paths.RootDirectory, buildOutputDirectory);
            if (!Directory.Exists(buildOutputDirectory))
            {
                return Invalid(ProjectArchiveWorkspaceValidationStatus.BuildOutputDirectoryMissing, "project_archive.build_output_missing");
            }

            var workingHash = await ProjectFileSha256.ComputeAsync(workingArchivePath, cancellationToken)
                .ConfigureAwait(false);
            if (!ProjectFileSha256.EqualsHex(descriptor.WorkingArchiveSha256, workingHash))
            {
                return Invalid(ProjectArchiveWorkspaceValidationStatus.WorkingArchiveHashMismatch, "project_archive.working_hash_mismatch");
            }

            return ProjectArchiveWorkspaceValidationResult.Valid();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or UnauthorizedAccessException
                                          or ArgumentException)
        {
            _logger.LogWarning(exception, "Project archive workspace path validation failed");
            return Invalid(ProjectArchiveWorkspaceValidationStatus.PathInvalid, "project_archive.path_invalid");
        }
    }

    private static ProjectArchiveWorkspaceCreateResult? ValidateRequest(
        ProjectArchiveWorkspaceCreateRequest request)
    {
        if (request.ArchiveTemplate?.Identity is not { IsValid: true }
            || request.PristineSource is null
            || request.ProjectId == Guid.Empty)
        {
            return ProjectArchiveWorkspaceCreateResult.Failure(
                ProjectArchiveWorkspaceFailureReason.InvalidRequest,
                "project_archive.request_invalid");
        }

        var displayName = request.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(displayName)
            || displayName.Length > 120
            || displayName.Contains("..", StringComparison.Ordinal)
            || displayName.IndexOfAny(['\\', '/']) >= 0
            || displayName.Any(char.IsControl))
        {
            return ProjectArchiveWorkspaceCreateResult.Failure(
                ProjectArchiveWorkspaceFailureReason.InvalidRequest,
                "project_archive.display_name_invalid");
        }

        return null;
    }

    private void ValidateManagedWorkspace(ISecureWorkspace workspace)
    {
        var expectedRoot = pathSecurity.ResolvePathWithinRoot(appPaths.WorkspacesDirectory, workspace.Id);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedRoot)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.Paths.RootDirectory)),
                comparison))
        {
            throw new InvalidOperationException("The project archive workspace is outside the managed workspace root.");
        }

        pathSecurity.EnsureNoReparsePoints(appPaths.WorkspacesDirectory, expectedRoot);
    }

    private async Task<string> CopyWorkingArchiveAsync(
        string sourcePath,
        string destinationPath,
        string workingRoot,
        string sourceHash,
        CancellationToken cancellationToken)
    {
        var temporaryPath = pathSecurity.ResolvePathWithinRoot(
            workingRoot,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var source = new FileStream(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            var copyHash = await ProjectFileSha256.ComputeAsync(temporaryPath, cancellationToken)
                .ConfigureAwait(false);
            if (!ProjectFileSha256.EqualsHex(sourceHash, copyHash))
            {
                throw new InvalidDataException("The working archive copy failed SHA-256 verification.");
            }

            File.Move(temporaryPath, destinationPath);
            return copyHash;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool ManifestMatches(
        ProjectArchiveWorkspaceDescriptor expected,
        ProjectArchiveWorkspaceDescriptor actual) =>
        actual.SchemaVersion == CurrentSchemaVersion
        && actual.ProjectId == expected.ProjectId
        && string.Equals(actual.DisplayName, expected.DisplayName, StringComparison.Ordinal)
        && string.Equals(actual.WorkspaceId, expected.WorkspaceId, StringComparison.Ordinal)
        && actual.ArchiveTemplate.TemplateId == expected.ArchiveTemplate.TemplateId
        && actual.ArchiveTemplate.TemplateVersion == expected.ArchiveTemplate.TemplateVersion
        && actual.ArchiveTemplate.SourceSha256 == expected.ArchiveTemplate.SourceSha256
        && actual.ArchiveTemplate.CompatibleGameBuild == expected.ArchiveTemplate.CompatibleGameBuild
        && actual.ArchiveTemplate.EngineType == expected.ArchiveTemplate.EngineType
        && string.Equals(actual.ArchiveTemplate.RegionProfileId, expected.ArchiveTemplate.RegionProfileId, StringComparison.Ordinal)
        && string.Equals(actual.WorkingArchiveRelativePath, expected.WorkingArchiveRelativePath, StringComparison.Ordinal)
        && string.Equals(actual.ExtractedDirectoryRelativePath, expected.ExtractedDirectoryRelativePath, StringComparison.Ordinal)
        && string.Equals(actual.BuildOutputDirectoryRelativePath, expected.BuildOutputDirectoryRelativePath, StringComparison.Ordinal)
        && string.Equals(actual.WorkingKeydatRelativePath, expected.WorkingKeydatRelativePath, StringComparison.Ordinal)
        && string.Equals(actual.ManifestRelativePath, expected.ManifestRelativePath, StringComparison.Ordinal)
        && string.Equals(actual.WorkingArchiveSha256, expected.WorkingArchiveSha256, StringComparison.OrdinalIgnoreCase)
        && actual.CreatedAt == expected.CreatedAt
        && actual.LastUpdatedAt == expected.LastUpdatedAt
        && actual.State == ProjectArchiveWorkspaceState.Ready;

    private static ProjectArchiveWorkspaceValidationResult Invalid(
        ProjectArchiveWorkspaceValidationStatus status,
        string code) => ProjectArchiveWorkspaceValidationResult.Invalid(status, code);

    private static ProjectArchiveWorkspaceCreateResult Cancelled() =>
        ProjectArchiveWorkspaceCreateResult.Failure(
            ProjectArchiveWorkspaceFailureReason.Cancelled,
            "project_archive.cancelled");
}

internal sealed class ProjectArchiveManifestWriteException(string message, Exception? innerException = null)
    : IOException(message, innerException);

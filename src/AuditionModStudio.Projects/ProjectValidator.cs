using System.Collections.Immutable;
using AuditionModStudio.Core.Assets;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;

namespace AuditionModStudio.Projects;

public sealed class ProjectValidator(
    IProjectMetadataCache metadataCache,
    IArchiveAssetScanner assetScanner,
    IDdsMetadataReader ddsMetadataReader,
    IProjectToolIntegrityValidator toolIntegrityValidator) : IProjectValidator
{
    public async Task<ProjectValidationResult> ValidateAsync(
        ProjectValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Project);
        ArgumentNullException.ThrowIfNull(request.Workspace);

        var issues = ImmutableArray.CreateBuilder<ProjectValidationIssue>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var project = request.Project;
            var workspace = request.Workspace;
            var secure = workspace.ArchiveWorkspace.SecureWorkspace;

            ValidateWorkspaceIdentity(project, workspace, issues);
            ValidateRequiredFolders(secure.Paths, issues);
            ValidateArchive(project, workspace, issues);

            if (project.EditState.CurrentRevision != project.EditState.SavedRevision)
            {
                Add(issues, ProjectValidationSeverity.Error, ProjectValidationIssueKind.PendingEdit,
                    "PROJECT_VALIDATE_PENDING_EDIT");
            }

            var baseline = await metadataCache.LoadAsync(project.ProjectId, cancellationToken).ConfigureAwait(false);
            if (baseline.Status == ProjectMetadataCacheLoadStatus.Cancelled)
            {
                return ProjectValidationResult.CancelledResult(issues);
            }

            var scan = await assetScanner.ScanAsync(workspace, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!scan.IsSuccess)
            {
                Add(issues, ProjectValidationSeverity.Error,
                    scan.FailureReason == ArchiveAssetScanFailureReason.ExtractedDirectoryMissing
                        ? ProjectValidationIssueKind.MissingFolder
                        : ProjectValidationIssueKind.InvalidPath,
                    scan.FailureReason == ArchiveAssetScanFailureReason.ExtractedDirectoryMissing
                        ? "PROJECT_VALIDATE_EXTRACT_ROOT_MISSING"
                        : "PROJECT_VALIDATE_ASSET_SCAN_FAILED");
            }
            else if (baseline.Succeeded)
            {
                await ValidateTexturesAsync(workspace, baseline.Textures, scan.Catalog!, issues, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                Add(issues, ProjectValidationSeverity.Error,
                    ProjectValidationIssueKind.MetadataBaselineUnavailable,
                    "PROJECT_VALIDATE_METADATA_BASELINE_UNAVAILABLE");
                await ValidateReadableTexturesAsync(workspace, scan.Catalog!, issues, cancellationToken)
                    .ConfigureAwait(false);
            }

            var tool = await toolIntegrityValidator.ValidateAsync(cancellationToken).ConfigureAwait(false);
            if (tool.Status == ProjectToolIntegrityStatus.Cancelled)
            {
                return ProjectValidationResult.CancelledResult(issues);
            }

            if (!tool.IsValid)
            {
                Add(issues, ProjectValidationSeverity.Error, ProjectValidationIssueKind.ToolIntegrityIssue,
                    "PROJECT_VALIDATE_TOOL_INTEGRITY_FAILED");
            }

            return ProjectValidationResult.Success(issues);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ProjectValidationResult.CancelledResult(issues);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException
                                          or ArgumentException)
        {
            Add(issues, ProjectValidationSeverity.Error, ProjectValidationIssueKind.InvalidPath,
                "PROJECT_VALIDATE_OPERATION_FAILED");
            return ProjectValidationResult.Success(issues);
        }
    }

    private static void ValidateWorkspaceIdentity(
        AuditionProject project,
        IProjectArchiveWorkspace workspace,
        ImmutableArray<ProjectValidationIssue>.Builder issues)
    {
        var descriptor = workspace.Descriptor;
        if (descriptor.ProjectId != project.ProjectId
            || !descriptor.WorkspaceId.Equals(project.Workspace.WorkspaceId, StringComparison.Ordinal)
            || !descriptor.WorkingArchiveRelativePath.Equals(
                project.Workspace.WorkingArchiveRelativePath.Value, StringComparison.Ordinal)
            || !descriptor.ExtractedDirectoryRelativePath.Equals(
                project.Workspace.ExtractedRootRelativePath.Value, StringComparison.Ordinal))
        {
            Add(issues, ProjectValidationSeverity.Error, ProjectValidationIssueKind.InvalidPath,
                "PROJECT_VALIDATE_WORKSPACE_REFERENCE_INVALID");
        }

        if (!Path.GetFileName(descriptor.WorkingArchiveRelativePath).Equals(
                Path.GetFileName(project.Workspace.WorkingArchiveRelativePath.Value),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            Add(issues, ProjectValidationSeverity.Error, ProjectValidationIssueKind.WrongFilename,
                "PROJECT_VALIDATE_ARCHIVE_FILENAME_MISMATCH");
        }
    }

    private static void ValidateRequiredFolders(
        Core.Workspaces.SecureWorkspacePaths paths,
        ImmutableArray<ProjectValidationIssue>.Builder issues)
    {
        if (!Directory.Exists(paths.WorkingDirectory))
        {
            Add(issues, ProjectValidationSeverity.Error, ProjectValidationIssueKind.MissingFolder,
                "PROJECT_VALIDATE_WORKING_FOLDER_MISSING");
        }

        if (!Directory.Exists(paths.ExtractedDirectory))
        {
            Add(issues, ProjectValidationSeverity.Error, ProjectValidationIssueKind.MissingFolder,
                "PROJECT_VALIDATE_EXTRACTED_FOLDER_MISSING");
        }

        if (!Directory.Exists(paths.BuildOutputDirectory))
        {
            Add(issues, ProjectValidationSeverity.Error, ProjectValidationIssueKind.MissingFolder,
                "PROJECT_VALIDATE_OUTPUT_FOLDER_MISSING");
        }
    }

    private static void ValidateArchive(
        AuditionProject project,
        IProjectArchiveWorkspace workspace,
        ImmutableArray<ProjectValidationIssue>.Builder issues)
    {
        try
        {
            var path = workspace.ArchiveWorkspace.SecureWorkspace.ResolveRelativePath(
                project.Workspace.WorkingArchiveRelativePath.Value);
            if (!File.Exists(path))
            {
                Add(issues, ProjectValidationSeverity.Error, ProjectValidationIssueKind.MissingArchive,
                    "PROJECT_VALIDATE_ARCHIVE_MISSING");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            Add(issues, ProjectValidationSeverity.Error, ProjectValidationIssueKind.InvalidPath,
                "PROJECT_VALIDATE_ARCHIVE_PATH_INVALID");
        }
    }

    private async Task ValidateTexturesAsync(
        IProjectArchiveWorkspace workspace,
        ImmutableArray<ProjectTextureMetadataSnapshot> baseline,
        ArchiveAssetCatalog catalog,
        ImmutableArray<ProjectValidationIssue>.Builder issues,
        CancellationToken cancellationToken)
    {
        var actual = catalog.Assets
            .Where(asset => asset.AssetKind == ArchiveAssetKind.Dds)
            .ToDictionary(asset => Normalize(asset.RelativePath), StringComparer.OrdinalIgnoreCase);
        var expected = baseline.Select(item => item.RelativePath.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in baseline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!actual.TryGetValue(item.RelativePath.Value, out var asset))
            {
                Add(issues, ProjectValidationSeverity.Error, ProjectValidationIssueKind.MissingFile,
                    "PROJECT_VALIDATE_DDS_MISSING", item.RelativePath);
                continue;
            }

            await ValidateTextureAsync(workspace, item.RelativePath, item.Metadata, asset, issues, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var asset in actual.Values.Where(asset => !expected.Contains(Normalize(asset.RelativePath))))
        {
            if (ModRelativePath.TryCreate(asset.RelativePath, out var relativePath))
            {
                Add(issues, ProjectValidationSeverity.Error, ProjectValidationIssueKind.WrongFilename,
                    "PROJECT_VALIDATE_DDS_UNEXPECTED", relativePath);
            }
            else
            {
                Add(issues, ProjectValidationSeverity.Error, ProjectValidationIssueKind.InvalidPath,
                    "PROJECT_VALIDATE_DDS_PATH_INVALID");
            }
        }
    }

    private async Task ValidateReadableTexturesAsync(
        IProjectArchiveWorkspace workspace,
        ArchiveAssetCatalog catalog,
        ImmutableArray<ProjectValidationIssue>.Builder issues,
        CancellationToken cancellationToken)
    {
        foreach (var asset in catalog.Assets.Where(asset => asset.AssetKind == ArchiveAssetKind.Dds))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ModRelativePath.TryCreate(asset.RelativePath, out var relativePath))
            {
                Add(issues, ProjectValidationSeverity.Error, ProjectValidationIssueKind.InvalidPath,
                    "PROJECT_VALIDATE_DDS_PATH_INVALID");
                continue;
            }

            await ValidateTextureAsync(workspace, relativePath, null, asset, issues, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task ValidateTextureAsync(
        IProjectArchiveWorkspace workspace,
        ModRelativePath relativePath,
        DdsMetadata? expected,
        ArchiveAsset asset,
        ImmutableArray<ProjectValidationIssue>.Builder issues,
        CancellationToken cancellationToken)
    {
        string path;
        try
        {
            path = workspace.ArchiveWorkspace.SecureWorkspace.ResolveRelativePath(
                Path.Combine(workspace.Descriptor.ExtractedDirectoryRelativePath, asset.RelativePath));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            Add(issues, ProjectValidationSeverity.Error, ProjectValidationIssueKind.InvalidPath,
                "PROJECT_VALIDATE_DDS_PATH_INVALID", relativePath);
            return;
        }

        var read = await ddsMetadataReader.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Metadata is null)
        {
            Add(issues, ProjectValidationSeverity.Error, ProjectValidationIssueKind.MalformedDds,
                "PROJECT_VALIDATE_DDS_MALFORMED", relativePath);
            return;
        }

        if (expected is not null && (read.Metadata.Width != expected.Width || read.Metadata.Height != expected.Height))
        {
            Add(issues, ProjectValidationSeverity.Error, ProjectValidationIssueKind.WrongDimensions,
                "PROJECT_VALIDATE_DDS_DIMENSIONS_MISMATCH", relativePath);
        }

        if (expected is not null && read.Metadata.Format != expected.Format)
        {
            Add(issues, ProjectValidationSeverity.Error, ProjectValidationIssueKind.WrongDdsFormat,
                "PROJECT_VALIDATE_DDS_FORMAT_MISMATCH", relativePath);
        }

        if (expected is null || (read.Metadata.Width == expected.Width
                                 && read.Metadata.Height == expected.Height
                                 && read.Metadata.Format == expected.Format))
        {
            Add(issues, ProjectValidationSeverity.Info, ProjectValidationIssueKind.ValidatedTexture,
                "PROJECT_VALIDATE_DDS_VALID", relativePath);
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static void Add(
        ImmutableArray<ProjectValidationIssue>.Builder issues,
        ProjectValidationSeverity severity,
        ProjectValidationIssueKind kind,
        string code,
        ModRelativePath? relativePath = null) => issues.Add(new(severity, kind, code, relativePath));
}

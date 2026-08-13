using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Assets;
using AuditionModStudio.Core.Catalog;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Paths;
using AuditionModStudio.Core.Projects;

namespace AuditionModStudio.Archives;

public sealed class TemplateAdminWorkflow(
    ITemplateAdminAuthorizer authorizer,
    ITemplateAdminPublisher publisher,
    IProjectArchiveWorkspaceService workspaceService,
    IAuditionArchiveService archiveService,
    IArchiveAssetScanner scanner,
    IDdsMetadataReader ddsMetadataReader,
    IGameRegionProfileResolver regionProfiles,
    IPathSecurity pathSecurity,
    TimeProvider timeProvider) : ITemplateAdminWorkflow
{
    private static readonly TimeSpan ExtractTimeout = TimeSpan.FromMinutes(10);

    public async Task<TemplateAdminResult> ExecuteAsync(TemplateAdminRequest request,
        IProgress<TemplateAdminProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (request is null) return Failed(TemplateAdminStatus.InvalidRequest, "TEMPLATE_ADMIN_REQUEST_INVALID");
        try
        {
            progress?.Report(new(TemplateAdminPhase.Authorizing, 0, 1));
            if (request.Principal is null || request.Principal.SubjectId == Guid.Empty
                || !await authorizer.IsTemplateAdminAsync(request.Principal, cancellationToken).ConfigureAwait(false))
                return Failed(TemplateAdminStatus.Unauthorized, "TEMPLATE_ADMIN_UNAUTHORIZED");
            progress?.Report(new(TemplateAdminPhase.Validating, 0, 1));
            var validation = ValidateRequest(request);
            if (validation is not null) return validation;

            var archiveRoot = Path.GetDirectoryName(Path.GetFullPath(request.ArchivePath))!;
            pathSecurity.EnsureNoReparsePoints(archiveRoot, request.ArchivePath);
            var archiveInfo = new FileInfo(request.ArchivePath);
            if (!archiveInfo.Exists || archiveInfo.Length is <= 0 or > PremiumTemplatePackageManifest.MaximumPackageBytes)
                return Failed(TemplateAdminStatus.ArchiveRejected, "TEMPLATE_ADMIN_ARCHIVE_REJECTED");
            var archiveSha256 = await ComputeSha256Async(request.ArchivePath, cancellationToken).ConfigureAwait(false);
            var archiveTemplate = new AuditionArchiveTemplate(request.TemplateId.Value, archiveInfo.Name,
                archiveInfo.Name, request.EngineType, request.RegionProfileId, request.ExpectedExtractFolderName,
                request.Version.Value, archiveSha256, request.CompatibleGameBuild.Value);
            var source = new PristineArchiveSource(archiveRoot);

            progress?.Report(new(TemplateAdminPhase.PreparingWorkspace, 0, 1));
            var created = await workspaceService.CreateAsync(new($"Template admin {request.TemplateId.Value}",
                archiveTemplate, source), cancellationToken).ConfigureAwait(false);
            if (!created.Succeeded || created.Workspace is null)
                return Failed(TemplateAdminStatus.ArchiveRejected, "TEMPLATE_ADMIN_WORKSPACE_FAILED");
            await using var workspace = created.Workspace;

            progress?.Report(new(TemplateAdminPhase.Extracting, 0, 1));
            var extracted = await archiveService.ExtractAsync(new(archiveTemplate, workspace.ArchiveWorkspace,
                source, ExtractTimeout), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!extracted.Command.Succeeded)
                return Failed(TemplateAdminStatus.ExtractFailed, "TEMPLATE_ADMIN_EXTRACT_FAILED");

            progress?.Report(new(TemplateAdminPhase.Scanning, 0, 1));
            var scanned = await scanner.ScanAsync(workspace, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!scanned.IsSuccess || scanned.Catalog is null)
                return Failed(TemplateAdminStatus.ScanFailed, "TEMPLATE_ADMIN_SCAN_FAILED");
            var textures = scanned.Catalog.Assets.OfType<TextureAsset>()
                .OrderBy(asset => asset.RelativePath, StringComparer.Ordinal).ToArray();
            var records = ImmutableArray.CreateBuilder<TemplateAdminDdsRecord>(textures.Length);
            var extractedRoot = pathSecurity.ResolvePathWithinRoot(
                workspace.ArchiveWorkspace.SecureWorkspace.Paths.ExtractedDirectory,
                workspace.ArchiveWorkspace.ExtractDirectoryRelativePath);
            for (var index = 0; index < textures.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new(TemplateAdminPhase.ReadingDds, index, textures.Length));
                var asset = textures[index];
                var path = pathSecurity.ResolvePathWithinRoot(extractedRoot, asset.RelativePath);
                pathSecurity.EnsureNoReparsePoints(extractedRoot, path);
                var metadata = await ddsMetadataReader.ReadAsync(path, cancellationToken).ConfigureAwait(false);
                if (!metadata.IsSuccess || metadata.Metadata is null || metadata.Metadata.FormatSupport != DdsFormatSupport.Known)
                    return Failed(TemplateAdminStatus.DdsValidationFailed, "TEMPLATE_ADMIN_DDS_INVALID");
                records.Add(new(new(asset.RelativePath), new(asset.Sha256), metadata.Metadata.Width,
                    metadata.Metadata.Height, metadata.Metadata.Format, metadata.Metadata.EffectiveMipLevelCount,
                    metadata.Metadata.HasAlphaChannel, metadata.Metadata.HeaderType));
            }

            var textureManifest = CreateManifest(request, textures);
            if (textureManifest is null)
                return Failed(TemplateAdminStatus.LabelValidationFailed, "TEMPLATE_ADMIN_LABELS_INVALID");
            var identity = archiveTemplate.Identity!;
            var packageManifest = new PremiumTemplatePackageManifest(identity, request.GameId, request.ModId,
                archiveInfo.Length, PremiumTemplatePackageManifest.PackageMediaType);
            var operationId = Guid.NewGuid();
            var audit = new TemplateAdminAuditEvent(operationId, request.Principal.SubjectId, identity,
                request.GameId, request.ModId, records.Count, timeProvider.GetUtcNow(), "template_version_published");
            progress?.Report(new(TemplateAdminPhase.Publishing, 0, 1));
            var published = await publisher.PublishAtomicallyAsync(new(request.ArchivePath, packageManifest,
                textureManifest, records.ToImmutable(), audit, TemplateAdminStorageEncryption.ServerManaged),
                cancellationToken).ConfigureAwait(false);
            if (!published.Succeeded)
                return Failed(published.VersionConflict ? TemplateAdminStatus.VersionConflict
                    : TemplateAdminStatus.PublishFailed, SafeCode(published.DiagnosticCode));
            if (published.PublishedIdentity != identity
                || published.Encryption != TemplateAdminStorageEncryption.ServerManaged
                || published.MetadataSignature is not { IsDefault: false, Length: 64 }
                || published.AuditEventId != operationId)
                return Failed(TemplateAdminStatus.PublishFailed, "TEMPLATE_ADMIN_PUBLISH_RESPONSE_INVALID");
            progress?.Report(new(TemplateAdminPhase.Completed, 1, 1));
            return new(TemplateAdminStatus.Succeeded, "TEMPLATE_ADMIN_PUBLISHED", identity,
                textureManifest, records.ToImmutable(), operationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Failed(TemplateAdminStatus.Cancelled, "TEMPLATE_ADMIN_CANCELLED"); }
        catch (UnauthorizedAccessException) { return Failed(TemplateAdminStatus.ArchiveRejected, "TEMPLATE_ADMIN_ARCHIVE_ACCESS_DENIED"); }
        catch (IOException) { return Failed(TemplateAdminStatus.ArchiveRejected, "TEMPLATE_ADMIN_ARCHIVE_IO_FAILED"); }
        catch (ArgumentException) { return Failed(TemplateAdminStatus.InvalidRequest, "TEMPLATE_ADMIN_REQUEST_INVALID"); }
        catch (InvalidOperationException) { return Failed(TemplateAdminStatus.ArchiveRejected, "TEMPLATE_ADMIN_ARCHIVE_UNSAFE"); }
    }

    private TemplateAdminResult? ValidateRequest(TemplateAdminRequest request)
    {
        if (!request.GameId.IsValid || !request.ModId.IsValid || !request.TemplateId.IsValid
            || !request.Version.IsValid || !request.CompatibleGameBuild.IsValid
            || request.EngineType is null || string.IsNullOrWhiteSpace(request.RegionProfileId)
            || !regionProfiles.TryResolve(request.RegionProfileId, out _)
            || !ModRelativePath.TryCreate(request.ExpectedExtractFolderName, out _)
            || !Path.IsPathFullyQualified(request.ArchivePath)
            || string.IsNullOrWhiteSpace(Path.GetFileName(request.ArchivePath))
            || string.IsNullOrWhiteSpace(Path.GetExtension(request.ArchivePath))
            || request.Labels.IsDefault || request.Labels.Length > ProductCatalogSnapshot.MaximumEntriesPerCollection)
            return Failed(TemplateAdminStatus.InvalidRequest, "TEMPLATE_ADMIN_REQUEST_INVALID");
        return null;
    }

    private static TextureManifest? CreateManifest(TemplateAdminRequest request, TextureAsset[] textures)
    {
        if (request.Labels.Length != textures.Length) return null;
        var observed = textures.Select(texture => texture.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (request.Labels.Any(label => !label.RelativePath.IsValid || !observed.Contains(label.RelativePath.Value))) return null;
        var created = TextureManifest.Create(request.GameId, request.ModId, request.Labels.Select(label =>
            new TextureSlot(label.SlotId, label.RelativePath, label.DisplayName, label.Category,
                label.Description, label.Tags, label.PreviewEnabled, label.Editable, label.RecommendedEditMode)));
        return created.Succeeded ? created.Manifest : null;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static string SafeCode(string? code) => !string.IsNullOrWhiteSpace(code) && code.Length <= 128
        && code.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
            ? code : "TEMPLATE_ADMIN_PUBLISH_FAILED";
    private static TemplateAdminResult Failed(TemplateAdminStatus status, string code) => new(status, code, null, null, [], null);
}

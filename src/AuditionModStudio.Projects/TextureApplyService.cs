using System.Collections.Immutable;
using System.Security.Cryptography;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;

namespace AuditionModStudio.Projects;

public sealed class TextureApplyService(
    IDdsMetadataReader metadataReader,
    IImageResizeService resizeService,
    IDdsMatchOriginalService matchOriginalService,
    IDdsValidationService validationService,
    ITextureStateMachine textureStateMachine,
    IThumbnailCache thumbnailCache,
    IAuditionProjectStore projectStore,
    TimeProvider? timeProvider = null) : ITextureApplyService
{
    private const int TotalSteps = 8;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<TextureApplyResult> ApplyAsync(
        TextureApplyRequest request,
        IProgress<TextureApplyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var requestFailure = ValidateRequest(request);
        if (requestFailure is not null)
        {
            return requestFailure;
        }

        var secureWorkspace = request.Workspace.ArchiveWorkspace.SecureWorkspace;
        var targetRelativePath = Path.Combine(
            "Extracted",
            request.Workspace.ArchiveWorkspace.ExtractDirectoryRelativePath,
            request.TextureRelativePath.Value.Replace('/', Path.DirectorySeparatorChar));
        var transactionId = Guid.NewGuid().ToString("N");
        var transactionRelativeRoot = Path.Combine("BuildOutput", "ApplyTransactions", transactionId);
        var candidateRelativePath = Path.Combine(transactionRelativeRoot, "candidate.dds");
        var backupRelativePath = Path.Combine(transactionRelativeRoot, "target.backup");
        var targetPath = secureWorkspace.ResolveRelativePath(targetRelativePath);
        var candidatePath = secureWorkspace.ResolveRelativePath(candidateRelativePath);
        var backupPath = secureWorkspace.ResolveRelativePath(backupRelativePath);
        var createdAssets = new List<string>();
        var targetReplaced = false;
        var committed = false;

        try
        {
            Report(progress, TextureApplyPhase.ValidatingTarget, 0);
            if (!File.Exists(targetPath) || new FileInfo(targetPath).Length == 0)
            {
                return TextureApplyResult.Failure(
                    TextureApplyFailureReason.TargetInvalid,
                    "TEXTURE_APPLY_TARGET_MISSING");
            }

            var targetRead = await metadataReader.ReadAsync(targetPath, cancellationToken).ConfigureAwait(false);
            if (!targetRead.IsSuccess || targetRead.Metadata is null
                || !matchOriginalService.DeriveProfile(targetRead.Metadata).Succeeded
                || request.ResizeRequest.TargetWidth != targetRead.Metadata.Width
                || request.ResizeRequest.TargetHeight != targetRead.Metadata.Height)
            {
                return TextureApplyResult.Failure(
                    TextureApplyFailureReason.TargetInvalid,
                    "TEXTURE_APPLY_TARGET_INVALID");
            }

            var beforeHash = await HashAsync(targetPath, cancellationToken).ConfigureAwait(false);
            var existingEdit = request.Project.EditedTextures.FirstOrDefault(item => SamePath(
                item.RelativePath.Value,
                request.TextureRelativePath.Value));
            if (existingEdit is not null && existingEdit.CurrentSha256 != beforeHash)
            {
                return TextureApplyResult.Failure(
                    TextureApplyFailureReason.TargetInvalid,
                    "TEXTURE_APPLY_PROJECT_HASH_STALE");
            }

            Report(progress, TextureApplyPhase.Resizing, 1);
            var resized = await resizeService.ResizeAsync(request.ResizeRequest, cancellationToken).ConfigureAwait(false);
            if (!resized.Succeeded || resized.Image is null)
            {
                return TextureApplyResult.Failure(
                    resized.Cancelled ? TextureApplyFailureReason.Cancelled : TextureApplyFailureReason.ResizeFailed,
                    resized.Cancelled ? "TEXTURE_APPLY_CANCELLED" : "TEXTURE_APPLY_RESIZE_FAILED");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(candidatePath)!);
            Report(progress, TextureApplyPhase.Encoding, 2);
            var matched = await matchOriginalService.MatchAsync(new(
                secureWorkspace,
                targetRelativePath,
                DdsRgbaImage.Create(resized.Image),
                candidateRelativePath), cancellationToken).ConfigureAwait(false);
            if (!matched.Succeeded || matched.OutputRelativePath is null)
            {
                return TextureApplyResult.Failure(
                    matched.Cancelled ? TextureApplyFailureReason.Cancelled : TextureApplyFailureReason.EncodeFailed,
                    matched.Cancelled ? "TEXTURE_APPLY_CANCELLED" : "TEXTURE_APPLY_MATCH_ORIGINAL_FAILED");
            }

            Report(progress, TextureApplyPhase.ValidatingOutput, 3);
            var validation = await validationService.ValidateAsync(new(
                secureWorkspace,
                targetRelativePath,
                matched.OutputRelativePath), cancellationToken).ConfigureAwait(false);
            if (!validation.Succeeded || validation.MatchReport is not { OverallMatch: true })
            {
                return TextureApplyResult.Failure(
                    validation.Cancelled ? TextureApplyFailureReason.Cancelled : TextureApplyFailureReason.ValidationFailed,
                    validation.Cancelled ? "TEXTURE_APPLY_CANCELLED" : "TEXTURE_APPLY_VALIDATION_FAILED");
            }

            var outputPath = secureWorkspace.ResolveRelativePath(matched.OutputRelativePath);
            var afterHash = await HashAsync(outputPath, cancellationToken).ConfigureAwait(false);
            var revision = checked(request.Project.EditState.CurrentRevision + 1);
            var operation = request.ResizeRequest.Options.Mode == ImageResizeMode.ManualCrop
                ? EditOperationKind.Crop
                : EditOperationKind.Resize;
            var prepared = await PrepareProjectUpdateAsync(
                request,
                existingEdit,
                beforeHash,
                afterHash,
                revision,
                operation,
                targetPath,
                outputPath,
                createdAssets,
                cancellationToken).ConfigureAwait(false);
            if (!prepared.Succeeded || prepared.Project is null)
            {
                return TextureApplyResult.Failure(
                    TextureApplyFailureReason.ProjectUpdateFailed,
                    "TEXTURE_APPLY_PROJECT_UPDATE_FAILED");
            }

            Report(progress, TextureApplyPhase.Replacing, 4);
            await CopyDurablyAsync(targetPath, backupPath, cancellationToken).ConfigureAwait(false);
            File.Move(outputPath, targetPath, overwrite: true);
            targetReplaced = true;

            Report(progress, TextureApplyPhase.UpdatingHistory, 5);
            var state = textureStateMachine.Evaluate(
                prepared.Project,
                request.TextureRelativePath,
                new(true, true, false));
            if (!state.Succeeded || state.State != TextureState.Modified)
            {
                return TextureApplyResult.Failure(
                    TextureApplyFailureReason.StateUpdateFailed,
                    "TEXTURE_APPLY_STATE_NOT_MODIFIED");
            }

            Report(progress, TextureApplyPhase.RegeneratingThumbnail, 6);
            var thumbnail = await thumbnailCache.GetOrCreateAsync(new(
                request.Workspace,
                request.TextureRelativePath,
                afterHash,
                request.ThumbnailMaximumDimension), cancellationToken).ConfigureAwait(false);
            if (!thumbnail.Succeeded || thumbnail.Image is null)
            {
                return TextureApplyResult.Failure(
                    thumbnail.Cancelled ? TextureApplyFailureReason.Cancelled : TextureApplyFailureReason.ThumbnailFailed,
                    thumbnail.Cancelled ? "TEXTURE_APPLY_CANCELLED" : "TEXTURE_APPLY_THUMBNAIL_FAILED");
            }

            Report(progress, TextureApplyPhase.SavingProject, 7);
            var saved = await projectStore.SaveAsync(prepared.Project, cancellationToken).ConfigureAwait(false);
            if (!saved.Succeeded)
            {
                return TextureApplyResult.Failure(
                    saved.FailureReason == AuditionProjectStoreFailureReason.Cancelled
                        ? TextureApplyFailureReason.Cancelled
                        : TextureApplyFailureReason.SaveFailed,
                    saved.FailureReason == AuditionProjectStoreFailureReason.Cancelled
                        ? "TEXTURE_APPLY_CANCELLED"
                        : "TEXTURE_APPLY_SAVE_FAILED");
            }

            committed = true;
            var cleanupPending = !TryDelete(backupPath) || !TryDeleteDirectory(Path.GetDirectoryName(backupPath)!);
            Report(progress, TextureApplyPhase.SavingProject, TotalSteps);
            return TextureApplyResult.Success(prepared.Project, thumbnail.Image, state.State, cleanupPending);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return TextureApplyResult.Failure(TextureApplyFailureReason.Cancelled, "TEXTURE_APPLY_CANCELLED");
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or ArgumentException
                                          or OverflowException
                                          or CryptographicException)
        {
            return TextureApplyResult.Failure(TextureApplyFailureReason.ReplaceFailed, "TEXTURE_APPLY_IO_FAILED");
        }
        finally
        {
            if (!committed)
            {
                await RollbackAsync(targetPath, backupPath, targetReplaced, createdAssets).ConfigureAwait(false);
            }

            TryDelete(backupPath);
            TryDelete(candidatePath);
            TryDeleteDirectory(Path.GetDirectoryName(candidatePath)!);
        }
    }

    private async Task<AuditionProjectCreateResult> PrepareProjectUpdateAsync(
        TextureApplyRequest request,
        ProjectEditedTextureRecord? existingEdit,
        Sha256Digest beforeHash,
        Sha256Digest afterHash,
        long revision,
        EditOperationKind operation,
        string targetPath,
        string outputPath,
        ICollection<string> createdAssets,
        CancellationToken cancellationToken)
    {
        var images = request.Project.ImageAssets.ToList();
        ProjectAssetId beforeId;
        if (existingEdit is not null)
        {
            beforeId = existingEdit.CurrentImageAssetId;
            if (!images.Any(asset => asset.Id == beforeId && asset.Sha256 == beforeHash))
            {
                return AuditionProjectCreateResult.Failure([
                    new(AuditionProjectValidationFailureReason.DanglingReference, "TEXTURE_APPLY_BEFORE_ASSET_MISSING")]);
            }
        }
        else
        {
            beforeId = CreateAssetId("before", revision, beforeHash);
            var beforeRelativePath = new ModRelativePath($"BuildOutput/EditAssets/{beforeId.Value}.dds");
            var beforePath = request.Workspace.ArchiveWorkspace.SecureWorkspace.ResolveRelativePath(
                beforeRelativePath.Value.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(beforePath)!);
            await CopyDurablyAsync(targetPath, beforePath, cancellationToken).ConfigureAwait(false);
            createdAssets.Add(beforePath);
            images.Add(new(beforeId, beforeRelativePath, beforeHash));
        }

        var afterId = CreateAssetId("after", revision, afterHash);
        var afterRelativePath = new ModRelativePath($"BuildOutput/EditAssets/{afterId.Value}.dds");
        var afterPath = request.Workspace.ArchiveWorkspace.SecureWorkspace.ResolveRelativePath(
            afterRelativePath.Value.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(afterPath)!);
        await CopyDurablyAsync(outputPath, afterPath, cancellationToken).ConfigureAwait(false);
        createdAssets.Add(afterPath);
        images.Add(new(afterId, afterRelativePath, afterHash));

        var edits = request.Project.EditedTextures
            .Where(item => !SamePath(item.RelativePath.Value, request.TextureRelativePath.Value))
            .Append(new ProjectEditedTextureRecord(
                request.TextureRelativePath,
                existingEdit?.OriginalSha256 ?? beforeHash,
                afterHash,
                afterId,
                revision))
            .ToImmutableArray();
        var history = request.Project.EditState.History.Add(new(
            revision,
            request.TextureRelativePath,
            operation,
            beforeId,
            afterId));
        return AuditionProject.Create(
            request.Project.SchemaVersion,
            request.Project.ProjectId,
            request.Project.Name,
            request.Project.GameId,
            request.Project.ModId,
            request.Project.TemplateIdentity,
            request.Project.Workspace,
            edits,
            images,
            request.Project.AiAssets,
            new(revision, revision, request.TextureRelativePath, history),
            new(ProjectBuildStatus.Dirty, null, null, null),
            request.Project.CreatedAt,
            _timeProvider.GetUtcNow());
    }

    private static TextureApplyResult? ValidateRequest(TextureApplyRequest? request)
    {
        if (request is null
            || request.Project is null
            || request.Workspace is null
            || !request.TextureRelativePath.IsValid
            || request.ResizeRequest is null
            || request.ResizeRequest.Source is null
            || request.ThumbnailMaximumDimension is <= 0 or > 1024)
        {
            return TextureApplyResult.Failure(
                TextureApplyFailureReason.InvalidRequest,
                "TEXTURE_APPLY_REQUEST_INVALID");
        }

        var descriptor = request.Workspace.Descriptor;
        return descriptor.ProjectId == request.Project.ProjectId
               && descriptor.ArchiveTemplate.Identity == request.Project.TemplateIdentity
               && descriptor.State == ProjectArchiveWorkspaceState.Ready
               && string.Equals(descriptor.WorkspaceId, request.Project.Workspace.WorkspaceId, StringComparison.Ordinal)
            ? null
            : TextureApplyResult.Failure(
                TextureApplyFailureReason.WorkspaceMismatch,
                "TEXTURE_APPLY_WORKSPACE_MISMATCH");
    }

    private static ProjectAssetId CreateAssetId(string role, long revision, Sha256Digest hash) =>
        new($"{role}_{revision}_{hash.Value[..16].ToLowerInvariant()}");

    private static async Task<Sha256Digest> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)));
    }

    private static async Task CopyDurablyAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var sourceStream = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destinationStream = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        await sourceStream.CopyToAsync(destinationStream, cancellationToken).ConfigureAwait(false);
        await destinationStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        destinationStream.Flush(flushToDisk: true);
    }

    private static async Task RollbackAsync(
        string targetPath,
        string backupPath,
        bool targetReplaced,
        IEnumerable<string> createdAssets)
    {
        if (targetReplaced && File.Exists(backupPath))
        {
            File.Move(backupPath, targetPath, overwrite: true);
        }

        foreach (var asset in createdAssets)
        {
            File.Delete(asset);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static void Report(IProgress<TextureApplyProgress>? progress, TextureApplyPhase phase, int completed) =>
        progress?.Report(new(phase, completed, TotalSteps));
}

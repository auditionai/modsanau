using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Paths;
using AuditionModStudio.Core.Workspaces;

namespace AuditionModStudio.Dds;

public sealed class DdsValidationService(
    IDdsMetadataReader metadataReader,
    IPathSecurity pathSecurity) : IDdsValidationService
{
    public async Task<DdsValidationResult> ValidateAsync(
        DdsValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Workspace);

        var targetRead = await ReadAsync(
            request.Workspace,
            request.TargetRelativePath,
            isTarget: true,
            cancellationToken).ConfigureAwait(false);
        if (targetRead.Failure is not null)
        {
            return targetRead.Failure;
        }

        var target = targetRead.Metadata!;
        if (!IsSupportedTarget(target))
        {
            return DdsValidationResult.Failure(
                DdsValidationFailureReason.UnsupportedTargetProfile,
                "DDS_VALIDATION_UNSUPPORTED_TARGET_PROFILE",
                target);
        }

        var candidateRead = await ReadAsync(
            request.Workspace,
            request.CandidateRelativePath,
            isTarget: false,
            cancellationToken).ConfigureAwait(false);
        if (candidateRead.Failure is not null)
        {
            return candidateRead.Failure with { TargetMetadata = target };
        }

        var candidate = candidateRead.Metadata!;
        var report = CreateReport(target, candidate);
        return report.OverallMatch
            ? DdsValidationResult.Success(target, candidate, report)
            : DdsValidationResult.Failure(
                DdsValidationFailureReason.MetadataMismatch,
                "DDS_VALIDATION_METADATA_MISMATCH",
                target,
                candidate,
                report);
    }

    private async Task<(DdsMetadata? Metadata, DdsValidationResult? Failure)> ReadAsync(
        ISecureWorkspace workspace,
        string relativePath,
        bool isTarget,
        CancellationToken cancellationToken)
    {
        DdsMetadataReadResult read;
        try
        {
            var path = workspace.ResolveRelativePath(relativePath);
            pathSecurity.EnsureNoReparsePoints(workspace.Paths.RootDirectory, path);
            read = await metadataReader.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return (null, Failure(isTarget, missing: false, cancelled: false));
        }

        if (read.IsSuccess && read.Metadata is not null)
        {
            return (read.Metadata, null);
        }

        return (null, Failure(
            isTarget,
            read.FailureReason == DdsMetadataFailureReason.FileMissing,
            read.FailureReason == DdsMetadataFailureReason.Cancelled));
    }

    private static DdsValidationResult Failure(bool isTarget, bool missing, bool cancelled)
    {
        if (cancelled)
        {
            return DdsValidationResult.Failure(
                DdsValidationFailureReason.Cancelled,
                "DDS_VALIDATION_CANCELLED");
        }

        if (isTarget)
        {
            return DdsValidationResult.Failure(
                missing ? DdsValidationFailureReason.TargetMissing : DdsValidationFailureReason.InvalidTargetDds,
                missing ? "DDS_VALIDATION_TARGET_MISSING" : "DDS_VALIDATION_TARGET_INVALID");
        }

        return DdsValidationResult.Failure(
            missing ? DdsValidationFailureReason.CandidateMissing : DdsValidationFailureReason.InvalidCandidateDds,
            missing ? "DDS_VALIDATION_CANDIDATE_MISSING" : "DDS_VALIDATION_CANDIDATE_INVALID");
    }

    private static bool IsSupportedTarget(DdsMetadata metadata) =>
        metadata.FormatSupport == DdsFormatSupport.Known
        && metadata.Format is DdsFormat.BC1 or DdsFormat.BC3 or DdsFormat.Rgba8 or DdsFormat.Bgra8
        && metadata.HeaderType is DdsHeaderType.Legacy or DdsHeaderType.Dx10
        && metadata.ResourceDimension == DdsResourceDimension.Texture2D
        && !metadata.IsCubemap
        && metadata.ArraySize == 1
        && (metadata.HeaderType == DdsHeaderType.Legacy
            || metadata.ColorSpace is DdsColorSpace.Linear or DdsColorSpace.Srgb);

    private static DdsMetadataMatchReport CreateReport(DdsMetadata target, DdsMetadata candidate) => new(
        candidate.Format == target.Format,
        candidate.Width == target.Width && candidate.Height == target.Height,
        candidate.EffectiveMipLevelCount == target.EffectiveMipLevelCount,
        candidate.HeaderType == target.HeaderType,
        target.HeaderType == DdsHeaderType.Legacy || candidate.ColorSpace == target.ColorSpace,
        candidate.ResourceDimension == target.ResourceDimension
            && candidate.IsCubemap == target.IsCubemap
            && candidate.ArraySize == target.ArraySize);
}

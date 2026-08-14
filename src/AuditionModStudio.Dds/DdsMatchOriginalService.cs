using AuditionModStudio.Core.Dds;

namespace AuditionModStudio.Dds;

public sealed class DdsMatchOriginalService(
    IDdsMetadataReader metadataReader,
    IDdsEncoder encoder,
    IDdsValidationService validationService) : IDdsMatchOriginalService
{
    public DdsMatchOriginalProfileResult DeriveProfile(DdsMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.ResourceDimension != DdsResourceDimension.Texture2D
            || metadata.IsCubemap
            || metadata.ArraySize != 1)
        {
            return DdsMatchOriginalProfileResult.Failure(
                DdsMatchOriginalFailureReason.UnsupportedResourceType,
                "DDS_MATCH_UNSUPPORTED_RESOURCE_TYPE");
        }

        if (metadata.FormatSupport != DdsFormatSupport.Known
            || metadata.Format is not (DdsFormat.BC1 or DdsFormat.BC3 or DdsFormat.Rgba8 or DdsFormat.Bgra8))
        {
            return DdsMatchOriginalProfileResult.Failure(
                DdsMatchOriginalFailureReason.UnsupportedTargetFormat,
                "DDS_MATCH_UNSUPPORTED_TARGET_FORMAT");
        }

        if (metadata.HeaderType is not (DdsHeaderType.Legacy or DdsHeaderType.Dx10))
        {
            return DdsMatchOriginalProfileResult.Failure(
                DdsMatchOriginalFailureReason.UnsupportedHeaderType,
                "DDS_MATCH_UNSUPPORTED_HEADER_TYPE");
        }

        if (metadata.EffectiveMipLevelCount == 0
            || metadata.EffectiveMipLevelCount > int.MaxValue
            || metadata.EffectiveMipLevelCount > CalculateMaximumMipLevels(metadata.Width, metadata.Height))
        {
            return DdsMatchOriginalProfileResult.Failure(
                DdsMatchOriginalFailureReason.InvalidMipProfile,
                "DDS_MATCH_INVALID_MIP_PROFILE");
        }

        var colorSpaceIsMeaningful = metadata.HeaderType == DdsHeaderType.Dx10;
        DdsColorSpace encoderColorSpace;
        if (colorSpaceIsMeaningful)
        {
            if (metadata.ColorSpace is not (DdsColorSpace.Linear or DdsColorSpace.Srgb))
            {
                return DdsMatchOriginalProfileResult.Failure(
                    DdsMatchOriginalFailureReason.ColorSpaceIncompatible,
                    "DDS_MATCH_UNKNOWN_DX10_COLOR_SPACE");
            }

            encoderColorSpace = metadata.ColorSpace;
        }
        else
        {
            encoderColorSpace = DdsColorSpace.Linear;
        }

        var alpha = metadata.Format == DdsFormat.BC1
            ? DdsTargetAlphaSemantics.Binary
            : DdsTargetAlphaSemantics.Full;
        var settings = new DdsTargetSettings(
            metadata.Width,
            metadata.Height,
            metadata.Format,
            checked((int)metadata.EffectiveMipLevelCount),
            metadata.HeaderType,
            encoderColorSpace,
            alpha);
        return DdsMatchOriginalProfileResult.Success(new(
            settings,
            metadata.ColorSpace,
            colorSpaceIsMeaningful,
            metadata.DeclaredMipMapCount,
            metadata.ResourceDimension,
            metadata.IsCubemap,
            metadata.ArraySize));
    }

    public async Task<DdsMatchOriginalResult> MatchAsync(
        DdsMatchOriginalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Workspace);
        ArgumentNullException.ThrowIfNull(request.ReplacementImage);

        DdsMetadataReadResult targetRead;
        try
        {
            var targetPath = request.Workspace.ResolveRelativePath(request.TargetRelativePath);
            targetRead = await metadataReader.ReadAsync(targetPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return DdsMatchOriginalResult.Failure(
                DdsMatchOriginalFailureReason.InvalidTargetDds,
                "DDS_MATCH_TARGET_PATH_REJECTED");
        }

        if (!targetRead.IsSuccess)
        {
            return targetRead.FailureReason switch
            {
                DdsMetadataFailureReason.FileMissing => DdsMatchOriginalResult.Failure(
                    DdsMatchOriginalFailureReason.TargetMissing,
                    "DDS_MATCH_TARGET_MISSING"),
                DdsMetadataFailureReason.Cancelled => DdsMatchOriginalResult.Failure(
                    DdsMatchOriginalFailureReason.Cancelled,
                    "DDS_MATCH_CANCELLED"),
                _ => DdsMatchOriginalResult.Failure(
                    DdsMatchOriginalFailureReason.InvalidTargetDds,
                    "DDS_MATCH_INVALID_TARGET_DDS"),
            };
        }

        var profileResult = DeriveProfile(targetRead.Metadata!);
        if (!profileResult.Succeeded)
        {
            return DdsMatchOriginalResult.Failure(
                profileResult.FailureReason,
                profileResult.DiagnosticCode!);
        }

        var profile = profileResult.Profile!;
        var settings = profile.TargetSettings;
        if (request.ReplacementImage.Width != settings.Width
            || request.ReplacementImage.Height != settings.Height)
        {
            return DdsMatchOriginalResult.Failure(
                DdsMatchOriginalFailureReason.DimensionMismatch,
                "DDS_MATCH_DIMENSION_MISMATCH",
                profile);
        }

        if (settings.Format == DdsFormat.BC1 && HasSemiTransparentAlpha(request.ReplacementImage))
        {
            return DdsMatchOriginalResult.Failure(
                DdsMatchOriginalFailureReason.AlphaIncompatible,
                "DDS_MATCH_BC1_ALPHA_INCOMPATIBLE",
                profile);
        }

        var encoded = await encoder.EncodeAsync(new(
            request.Workspace,
            request.ReplacementImage,
            settings,
            request.OutputRelativePath), cancellationToken).ConfigureAwait(false);
        if (!encoded.Succeeded)
        {
            return MapEncodeFailure(encoded, profile);
        }

        var validation = await validationService.ValidateAsync(new(
            request.Workspace,
            request.TargetRelativePath,
            encoded.OutputRelativePath!), cancellationToken).ConfigureAwait(false);
        if (!validation.Succeeded)
        {
            return DdsMatchOriginalResult.Failure(
                validation.FailureReason == DdsValidationFailureReason.Cancelled
                    ? DdsMatchOriginalFailureReason.Cancelled
                    : DdsMatchOriginalFailureReason.PostValidationFailed,
                validation.FailureReason == DdsValidationFailureReason.Cancelled
                    ? "DDS_MATCH_CANCELLED"
                    : validation.FailureReason == DdsValidationFailureReason.MetadataMismatch
                        ? "DDS_MATCH_OUTPUT_PROFILE_MISMATCH"
                        : "DDS_MATCH_OUTPUT_METADATA_INVALID",
                profile,
                validation.CandidateMetadata,
                validation.MatchReport);
        }

        return DdsMatchOriginalResult.Success(
            profile,
            validation.CandidateMetadata!,
            validation.MatchReport!,
            encoded.OutputRelativePath!);
    }

    private static bool HasSemiTransparentAlpha(DdsRgbaImage image)
    {
        for (var y = 0; y < image.Height; y++)
        {
            var row = image.Pixels.AsSpan(checked(y * image.Stride), checked(image.Width * 4));
            for (var index = 3; index < row.Length; index += 4)
            {
                if (row[index] is not (0 or 255))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static uint CalculateMaximumMipLevels(int width, int height)
    {
        uint levels = 1;
        for (var maximum = Math.Max(width, height); maximum > 1; maximum /= 2)
        {
            levels++;
        }

        return levels;
    }

    private static DdsMatchOriginalResult MapEncodeFailure(
        DdsEncodeResult result,
        DdsMatchOriginalProfile profile) => result.FailureReason switch
        {
            DdsEncodeFailureReason.DimensionMismatch => DdsMatchOriginalResult.Failure(
                DdsMatchOriginalFailureReason.DimensionMismatch, "DDS_MATCH_DIMENSION_MISMATCH", profile),
            DdsEncodeFailureReason.UnsupportedTargetFormat => DdsMatchOriginalResult.Failure(
                DdsMatchOriginalFailureReason.UnsupportedTargetFormat, "DDS_MATCH_UNSUPPORTED_TARGET_FORMAT", profile),
            DdsEncodeFailureReason.InvalidMipCount => DdsMatchOriginalResult.Failure(
                DdsMatchOriginalFailureReason.InvalidMipProfile, "DDS_MATCH_INVALID_MIP_PROFILE", profile),
            DdsEncodeFailureReason.ResourceLimitExceeded => DdsMatchOriginalResult.Failure(
                DdsMatchOriginalFailureReason.ResourceLimitExceeded, "DDS_MATCH_RESOURCE_LIMIT_EXCEEDED", profile),
            DdsEncodeFailureReason.Cancelled => DdsMatchOriginalResult.Failure(
                DdsMatchOriginalFailureReason.Cancelled, "DDS_MATCH_CANCELLED", profile),
            DdsEncodeFailureReason.TimedOut => DdsMatchOriginalResult.Failure(
                DdsMatchOriginalFailureReason.TimedOut, "DDS_MATCH_TIMED_OUT", profile),
            _ => DdsMatchOriginalResult.Failure(
                DdsMatchOriginalFailureReason.EncodeFailed, "DDS_MATCH_ENCODE_FAILED", profile),
        };
}

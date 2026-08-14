using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Paths;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuditionModStudio.Dds;

public sealed class DdsEncoder(
    IDdsMetadataReader metadataReader,
    IDirectXTexEvaluationHarness evaluationHarness,
    IPathSecurity pathSecurity,
    DdsEncoderOptions options,
    ILogger<DdsEncoder>? logger = null) : IDdsEncoder
{
    private readonly ILogger<DdsEncoder> _logger = logger ?? NullLogger<DdsEncoder>.Instance;

    public async Task<DdsEncodeResult> EncodeAsync(
        DdsEncodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = ValidateRequest(request, options.ResourcePolicy);
        if (validation is not null)
        {
            return validation;
        }

        string finalPath;
        try
        {
            finalPath = pathSecurity.ResolvePathWithinRoot(
                request.Workspace.Paths.BuildOutputDirectory,
                request.OutputRelativePath);
            pathSecurity.EnsureNoReparsePoints(request.Workspace.Paths.BuildOutputDirectory, finalPath);
            if (!string.Equals(Path.GetExtension(finalPath), ".dds", StringComparison.OrdinalIgnoreCase))
            {
                return DdsEncodeResult.Failure(
                    DdsEncodeFailureReason.InvalidOutputPath,
                    "DDS_ENCODE_OUTPUT_EXTENSION_REJECTED");
            }

            if (File.Exists(finalPath))
            {
                return DdsEncodeResult.Failure(DdsEncodeFailureReason.OutputExists, "DDS_ENCODE_OUTPUT_EXISTS");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return DdsEncodeResult.Failure(
                DdsEncodeFailureReason.InvalidOutputPath,
                "DDS_ENCODE_OUTPUT_PATH_REJECTED");
        }

        var operationId = Guid.NewGuid().ToString("N");
        var inputDirectoryName = $"DdsEncodeInput-{operationId}";
        var outputDirectoryName = $"DdsEncode-{operationId}";
        var inputDirectory = pathSecurity.ResolvePathWithinRoot(
            request.Workspace.Paths.WorkingDirectory,
            inputDirectoryName);
        var outputDirectory = pathSecurity.ResolvePathWithinRoot(
            request.Workspace.Paths.BuildOutputDirectory,
            outputDirectoryName);
        try
        {
            Directory.CreateDirectory(inputDirectory);
            pathSecurity.EnsureNoReparsePoints(request.Workspace.Paths.WorkingDirectory, inputDirectory);
            var pngPath = pathSecurity.ResolvePathWithinRoot(inputDirectory, "input.png");
            await RgbaPngWriter.WriteAsync(pngPath, request.Image, cancellationToken).ConfigureAwait(false);
            var pngRelativePath = Path.GetRelativePath(request.Workspace.Paths.RootDirectory, pngPath);
            var settings = request.Settings;
            var encoded = await evaluationHarness.RunAsync(new(
                DirectXTexEvaluationOperation.EncodeToDds,
                request.Workspace,
                options.ApprovedToolSourcePath,
                pngRelativePath,
                outputDirectoryName,
                settings.Width,
                settings.Height,
                settings.MipLevelCount,
                settings.HeaderType == DdsHeaderType.Legacy,
                options.Timeout,
                options.ResourcePolicy.MaximumInputBytes,
                options.ResourcePolicy.MaximumPixelCount,
                options.ResourcePolicy.MaximumDiagnosticCharacters,
                settings.Format,
                settings.ColorSpace,
                settings.HeaderType,
                settings.AlphaSemantics),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!encoded.Succeeded)
            {
                return MapFailure(encoded);
            }

            if (encoded.OutputRelativePath is null)
            {
                return DdsEncodeResult.Failure(
                    DdsEncodeFailureReason.OutputMissing,
                    "DDS_ENCODE_OUTPUT_MISSING");
            }

            var temporaryOutput = request.Workspace.ResolveRelativePath(encoded.OutputRelativePath);
            var info = new FileInfo(temporaryOutput);
            if (!info.Exists || info.Length <= 128 || info.Length > options.ResourcePolicy.MaximumEncodedDdsBytes)
            {
                return DdsEncodeResult.Failure(
                    DdsEncodeFailureReason.OutputValidationFailed,
                    "DDS_ENCODE_OUTPUT_SIZE_REJECTED");
            }

            var metadataResult = await metadataReader.ReadAsync(temporaryOutput, cancellationToken).ConfigureAwait(false);
            if (!metadataResult.IsSuccess
                || metadataResult.Metadata is null
                || !MetadataMatches(metadataResult.Metadata, settings))
            {
                return DdsEncodeResult.Failure(
                    DdsEncodeFailureReason.OutputValidationFailed,
                    "DDS_ENCODE_METADATA_MISMATCH",
                    metadataResult.Metadata);
            }

            var parent = Path.GetDirectoryName(finalPath)!;
            Directory.CreateDirectory(parent);
            pathSecurity.EnsureNoReparsePoints(request.Workspace.Paths.BuildOutputDirectory, parent);
            File.Move(temporaryOutput, finalPath);
            var relativeOutput = Path.GetRelativePath(request.Workspace.Paths.RootDirectory, finalPath);
            return DdsEncodeResult.Success(relativeOutput, metadataResult.Metadata);
        }
        catch (OperationCanceledException)
        {
            return DdsEncodeResult.Failure(DdsEncodeFailureReason.Cancelled, "DDS_ENCODE_CANCELLED");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError("DDS encode failed during controlled filesystem processing");
            return DdsEncodeResult.Failure(DdsEncodeFailureReason.EncodeFailed, "DDS_ENCODE_CONTROLLED_IO_FAILURE");
        }
        finally
        {
            TryDelete(inputDirectory, "input");
            TryDelete(outputDirectory, "output");
        }
    }

    private static DdsEncodeResult? ValidateRequest(DdsEncodeRequest request, DdsPreviewResourcePolicy policy)
    {
        if (request.Workspace is null || request.Image is null || request.Settings is null)
        {
            return DdsEncodeResult.Failure(DdsEncodeFailureReason.InvalidInputImage, "DDS_ENCODE_INVALID_REQUEST");
        }

        var image = request.Image;
        var settings = request.Settings;
        if (image.PixelFormat != DdsImagePixelFormat.Rgba8
            || image.Width <= 0
            || image.Height <= 0)
        {
            return DdsEncodeResult.Failure(DdsEncodeFailureReason.InvalidInputImage, "DDS_ENCODE_INVALID_RGBA_IMAGE");
        }

        if (image.Width > policy.MaximumDimension || image.Height > policy.MaximumDimension)
        {
            return DdsEncodeResult.Failure(
                DdsEncodeFailureReason.ResourceLimitExceeded,
                "DDS_ENCODE_IMAGE_RESOURCE_LIMIT_EXCEEDED");
        }

        try
        {
            var minimumStride = checked(image.Width * 4);
            var requiredLength = checked((long)image.Stride * image.Height);
            if (image.Stride < minimumStride
                || requiredLength != image.Pixels.Length
                || requiredLength > policy.MaximumInputBytes
                || checked((long)image.Width * image.Height) > policy.MaximumPixelCount)
            {
                return DdsEncodeResult.Failure(
                    DdsEncodeFailureReason.ResourceLimitExceeded,
                    "DDS_ENCODE_IMAGE_RESOURCE_LIMIT_EXCEEDED");
            }
        }
        catch (OverflowException)
        {
            return DdsEncodeResult.Failure(
                DdsEncodeFailureReason.ResourceLimitExceeded,
                "DDS_ENCODE_IMAGE_ARITHMETIC_OVERFLOW");
        }

        if (image.Width != settings.Width || image.Height != settings.Height)
        {
            return DdsEncodeResult.Failure(DdsEncodeFailureReason.DimensionMismatch, "DDS_ENCODE_DIMENSION_MISMATCH");
        }

        if (settings.Format is not (DdsFormat.BC1 or DdsFormat.BC3 or DdsFormat.Rgba8 or DdsFormat.Bgra8)
            || settings.ColorSpace is not (DdsColorSpace.Linear or DdsColorSpace.Srgb)
            || settings.HeaderType == DdsHeaderType.Legacy && settings.ColorSpace == DdsColorSpace.Srgb
            || settings.Format == DdsFormat.BC1 && settings.AlphaSemantics == DdsTargetAlphaSemantics.Full)
        {
            return DdsEncodeResult.Failure(
                DdsEncodeFailureReason.UnsupportedTargetFormat,
                "DDS_ENCODE_UNSUPPORTED_TARGET_SETTINGS");
        }

        if (settings.MipLevelCount <= 0
            || settings.MipLevelCount > CalculateMaximumMipLevels(settings.Width, settings.Height))
        {
            return DdsEncodeResult.Failure(DdsEncodeFailureReason.InvalidMipCount, "DDS_ENCODE_INVALID_MIP_COUNT");
        }

        if (!HasSafeOutputEstimate(settings, policy.MaximumEncodedDdsBytes))
        {
            return DdsEncodeResult.Failure(
                DdsEncodeFailureReason.ResourceLimitExceeded,
                "DDS_ENCODE_OUTPUT_ESTIMATE_EXCEEDED");
        }

        if (!AlphaMatches(image, settings.AlphaSemantics))
        {
            return DdsEncodeResult.Failure(
                DdsEncodeFailureReason.UnsupportedTargetFormat,
                "DDS_ENCODE_ALPHA_SEMANTICS_MISMATCH");
        }

        return null;
    }

    private static bool AlphaMatches(DdsRgbaImage image, DdsTargetAlphaSemantics semantics)
    {
        if (semantics == DdsTargetAlphaSemantics.Full)
        {
            return true;
        }

        for (var y = 0; y < image.Height; y++)
        {
            var row = image.Pixels.AsSpan(checked(y * image.Stride), checked(image.Width * 4));
            for (var alpha = 3; alpha < row.Length; alpha += 4)
            {
                if (semantics == DdsTargetAlphaSemantics.Opaque && row[alpha] != 255
                    || semantics == DdsTargetAlphaSemantics.Binary && row[alpha] is not (0 or 255))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool MetadataMatches(DdsMetadata metadata, DdsTargetSettings settings) =>
        metadata.Width == settings.Width
        && metadata.Height == settings.Height
        && metadata.Format == settings.Format
        && metadata.HeaderType == settings.HeaderType
        && metadata.EffectiveMipLevelCount == settings.MipLevelCount
        && metadata.ResourceDimension == DdsResourceDimension.Texture2D
        && !metadata.IsCubemap
        && metadata.ArraySize == 1
        && (settings.HeaderType == DdsHeaderType.Legacy || metadata.ColorSpace == settings.ColorSpace);

    private static int CalculateMaximumMipLevels(int width, int height)
    {
        var levels = 1;
        for (var maximum = Math.Max(width, height); maximum > 1; maximum /= 2)
        {
            levels++;
        }

        return levels;
    }

    private static bool HasSafeOutputEstimate(DdsTargetSettings settings, long maximumBytes)
    {
        try
        {
            long bytes = settings.HeaderType == DdsHeaderType.Legacy ? 128 : 148;
            var width = settings.Width;
            var height = settings.Height;
            for (var level = 0; level < settings.MipLevelCount; level++)
            {
                bytes = checked(bytes + settings.Format switch
                {
                    DdsFormat.BC1 => checked((long)Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8),
                    DdsFormat.BC3 => checked((long)Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 16),
                    DdsFormat.Rgba8 or DdsFormat.Bgra8 => checked((long)width * height * 4),
                    _ => maximumBytes + 1,
                });
                width = Math.Max(1, width / 2);
                height = Math.Max(1, height / 2);
            }

            return bytes <= maximumBytes;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static DdsEncodeResult MapFailure(DirectXTexEvaluationResult result) => result.FailureReason switch
    {
        DirectXTexEvaluationFailureReason.ToolMissing or DirectXTexEvaluationFailureReason.ProcessStartFailed =>
            DdsEncodeResult.Failure(DdsEncodeFailureReason.EncoderUnavailable, "DDS_ENCODE_ENCODER_UNAVAILABLE"),
        DirectXTexEvaluationFailureReason.ToolIntegrityMismatch =>
            DdsEncodeResult.Failure(DdsEncodeFailureReason.EncoderIntegrityFailed, "DDS_ENCODE_ENCODER_INTEGRITY_FAILED"),
        DirectXTexEvaluationFailureReason.InputTooLarge =>
            DdsEncodeResult.Failure(DdsEncodeFailureReason.ResourceLimitExceeded, "DDS_ENCODE_RESOURCE_LIMIT_EXCEEDED"),
        DirectXTexEvaluationFailureReason.Cancelled =>
            DdsEncodeResult.Failure(DdsEncodeFailureReason.Cancelled, "DDS_ENCODE_CANCELLED"),
        DirectXTexEvaluationFailureReason.TimedOut =>
            DdsEncodeResult.Failure(DdsEncodeFailureReason.TimedOut, "DDS_ENCODE_TIMED_OUT"),
        DirectXTexEvaluationFailureReason.OutputMissing =>
            DdsEncodeResult.Failure(DdsEncodeFailureReason.OutputMissing, "DDS_ENCODE_OUTPUT_MISSING"),
        DirectXTexEvaluationFailureReason.OutputInvalid =>
            DdsEncodeResult.Failure(DdsEncodeFailureReason.OutputValidationFailed, "DDS_ENCODE_OUTPUT_VALIDATION_FAILED"),
        _ => DdsEncodeResult.Failure(DdsEncodeFailureReason.EncodeFailed, "DDS_ENCODE_FAILED"),
    };

    private void TryDelete(string path, string kind)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not clean DDS encode {Kind} directory", kind);
        }
    }
}

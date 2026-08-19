using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Paths;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Buffers.Binary;

namespace AuditionModStudio.Dds;

public sealed class DdsPreviewService(
    IDdsMetadataReader metadataReader,
    IDirectXTexEvaluationHarness evaluationHarness,
    IPathSecurity pathSecurity,
    DdsPreviewServiceOptions options,
    ILogger<DdsPreviewService>? logger = null) : IDdsPreviewService
{
    private readonly ILogger<DdsPreviewService> _logger = logger ?? NullLogger<DdsPreviewService>.Instance;

    public async Task<DdsPreviewResult> CreateAsync(
        DdsPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Workspace);

        string sourcePath;
        try
        {
            sourcePath = request.Workspace.ResolveRelativePath(request.SourceRelativePath);
            pathSecurity.EnsureNoReparsePoints(request.Workspace.Paths.RootDirectory, sourcePath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return DdsPreviewResult.Failure(DdsPreviewFailureReason.InvalidDds, "DDS_PREVIEW_SOURCE_PATH_REJECTED");
        }

        var metadataResult = await metadataReader.ReadAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!metadataResult.IsSuccess)
        {
            return MapMetadataFailure(metadataResult);
        }

        var metadata = metadataResult.Metadata!;
        var policyFailure = ValidateMetadata(metadata, options.ResourcePolicy);
        if (policyFailure is not null)
        {
            return policyFailure;
        }

        var operationDirectoryName = $"DdsPreview-{Guid.NewGuid():N}";
        var operationDirectory = pathSecurity.ResolvePathWithinRoot(
            request.Workspace.Paths.BuildOutputDirectory,
            operationDirectoryName);
        try
        {
            var decode = await evaluationHarness.RunAsync(new(
                DirectXTexEvaluationOperation.DecodeToPng,
                request.Workspace,
                options.ApprovedToolSourcePath,
                request.SourceRelativePath,
                operationDirectoryName,
                null,
                null,
                1,
                false,
                options.Timeout,
                options.ResourcePolicy.MaximumInputBytes,
                options.ResourcePolicy.MaximumPixelCount,
                options.ResourcePolicy.MaximumDiagnosticCharacters),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!decode.Succeeded)
            {
                return MapDecodeFailure(decode, metadata);
            }

            if (decode.OutputRelativePath is null
                || decode.OutputWidth != metadata.Width
                || decode.OutputHeight != metadata.Height)
            {
                return DdsPreviewResult.Failure(
                    DdsPreviewFailureReason.InvalidOutput,
                    "DDS_PREVIEW_DIMENSION_MISMATCH",
                    metadata);
            }

            var outputPath = request.Workspace.ResolveRelativePath(decode.OutputRelativePath);
            pathSecurity.EnsureNoReparsePoints(request.Workspace.Paths.BuildOutputDirectory, outputPath);
            var outputLength = new FileInfo(outputPath).Length;
            if (outputLength <= 0 || outputLength > options.ResourcePolicy.MaximumEncodedPreviewBytes)
            {
                return DdsPreviewResult.Failure(
                    DdsPreviewFailureReason.ResourceLimitExceeded,
                    "DDS_PREVIEW_OUTPUT_SIZE_REJECTED",
                    metadata);
            }

            var encodedPng = NormalizeDecodedPngToSrgb(
                await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false));
            var dimensions = await PngHeaderReader.ReadDimensionsAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (dimensions is null
                || dimensions.Value.Width != metadata.Width
                || dimensions.Value.Height != metadata.Height)
            {
                return DdsPreviewResult.Failure(
                    DdsPreviewFailureReason.InvalidOutput,
                    "DDS_PREVIEW_INVALID_PNG",
                    metadata);
            }

            return DdsPreviewResult.Success(
                metadata,
                new DdsPreviewImage(dimensions.Value.Width, dimensions.Value.Height, encodedPng));
        }
        catch (OperationCanceledException)
        {
            return DdsPreviewResult.Failure(
                DdsPreviewFailureReason.Cancelled,
                "DDS_PREVIEW_CANCELLED",
                metadata);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return DdsPreviewResult.Failure(
                DdsPreviewFailureReason.InvalidOutput,
                "DDS_PREVIEW_CONTROLLED_IO_FAILURE",
                metadata);
        }
        finally
        {
            TryDeleteOperationDirectory(operationDirectory);
        }
    }

    // texconv labels legacy BC1/BC3 decode output as linear PNG (gAMA=1.0).
    // Our editor stores/displayes RGBA artwork as sRGB, so preserve that intent before import.
    private static byte[] NormalizeDecodedPngToSrgb(byte[] png)
    {
        const int signatureLength = 8;
        const uint srgbGamma = 45_455;
        if (png.Length < signatureLength + 12) return png;
        for (var offset = signatureLength; offset + 12 <= png.Length;)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            if (length > int.MaxValue || offset + 12L + length > png.Length) return png;
            var type = png.AsSpan(offset + 4, 4);
            if (type.SequenceEqual("gAMA"u8) && length == 4)
            {
                BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(offset + 8, 4), srgbGamma);
                BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(offset + 12, 4), ComputePngCrc(type, png.AsSpan(offset + 8, 4)));
                return png;
            }
            offset += checked((int)(12 + length));
        }
        return png;
    }

    private static uint ComputePngCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        Update(type);
        Update(data);
        return ~crc;

        void Update(ReadOnlySpan<byte> values)
        {
            foreach (var value in values)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
            }
        }
    }

    private static DdsPreviewResult? ValidateMetadata(
        DdsMetadata metadata,
        DdsPreviewResourcePolicy policy)
    {
        if (metadata.FormatSupport != DdsFormatSupport.Known
            || metadata.Format is DdsFormat.Unknown or DdsFormat.Uncompressed
            || metadata.ResourceDimension != DdsResourceDimension.Texture2D
            || metadata.IsCubemap
            || metadata.ArraySize != 1)
        {
            return DdsPreviewResult.Failure(
                DdsPreviewFailureReason.UnsupportedFormat,
                "DDS_PREVIEW_UNSUPPORTED_RESOURCE",
                metadata);
        }

        if (metadata.FileLength <= 0
            || metadata.FileLength > policy.MaximumInputBytes
            || metadata.Width <= 0
            || metadata.Height <= 0
            || metadata.Width > policy.MaximumDimension
            || metadata.Height > policy.MaximumDimension
            || (long)metadata.Width * metadata.Height > policy.MaximumPixelCount)
        {
            return DdsPreviewResult.Failure(
                DdsPreviewFailureReason.ResourceLimitExceeded,
                "DDS_PREVIEW_RESOURCE_LIMIT_EXCEEDED",
                metadata);
        }

        return null;
    }

    private static DdsPreviewResult MapMetadataFailure(DdsMetadataReadResult result) => result.FailureReason switch
    {
        DdsMetadataFailureReason.FileMissing => DdsPreviewResult.Failure(
            DdsPreviewFailureReason.SourceMissing,
            "DDS_PREVIEW_SOURCE_MISSING"),
        DdsMetadataFailureReason.Cancelled => DdsPreviewResult.Failure(
            DdsPreviewFailureReason.Cancelled,
            "DDS_PREVIEW_CANCELLED"),
        DdsMetadataFailureReason.InvalidDimensions => DdsPreviewResult.Failure(
            DdsPreviewFailureReason.ResourceLimitExceeded,
            "DDS_PREVIEW_RESOURCE_LIMIT_EXCEEDED"),
        _ => DdsPreviewResult.Failure(
            DdsPreviewFailureReason.InvalidDds,
            "DDS_PREVIEW_INVALID_DDS"),
    };

    private static DdsPreviewResult MapDecodeFailure(
        DirectXTexEvaluationResult result,
        DdsMetadata metadata) => result.FailureReason switch
        {
            DirectXTexEvaluationFailureReason.ToolMissing or DirectXTexEvaluationFailureReason.ProcessStartFailed =>
                DdsPreviewResult.Failure(DdsPreviewFailureReason.DecoderUnavailable, "DDS_PREVIEW_DECODER_UNAVAILABLE", metadata),
            DirectXTexEvaluationFailureReason.ToolIntegrityMismatch =>
                DdsPreviewResult.Failure(DdsPreviewFailureReason.DecoderIntegrityFailed, "DDS_PREVIEW_DECODER_INTEGRITY_FAILED", metadata),
            DirectXTexEvaluationFailureReason.InputTooLarge =>
                DdsPreviewResult.Failure(DdsPreviewFailureReason.ResourceLimitExceeded, "DDS_PREVIEW_RESOURCE_LIMIT_EXCEEDED", metadata),
            DirectXTexEvaluationFailureReason.Cancelled =>
                DdsPreviewResult.Failure(DdsPreviewFailureReason.Cancelled, "DDS_PREVIEW_CANCELLED", metadata),
            DirectXTexEvaluationFailureReason.TimedOut =>
                DdsPreviewResult.Failure(DdsPreviewFailureReason.TimedOut, "DDS_PREVIEW_TIMED_OUT", metadata),
            DirectXTexEvaluationFailureReason.OutputMissing or DirectXTexEvaluationFailureReason.OutputInvalid =>
                DdsPreviewResult.Failure(DdsPreviewFailureReason.InvalidOutput, "DDS_PREVIEW_INVALID_OUTPUT", metadata),
            _ => DdsPreviewResult.Failure(DdsPreviewFailureReason.DecodeFailed, "DDS_PREVIEW_DECODE_FAILED", metadata),
        };

    private void TryDeleteOperationDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            _logger.LogWarning("Could not clean a DDS preview operation directory");
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogWarning("Access was denied while cleaning a DDS preview operation directory");
        }
    }
}

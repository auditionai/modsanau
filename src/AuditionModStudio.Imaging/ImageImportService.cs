using AuditionModStudio.Core.Images;
using SkiaSharp;

namespace AuditionModStudio.Imaging;

public sealed class ImageImportService(ImageImportResourcePolicy resourcePolicy) : IImageImportService
{
    public async Task<ImageImportResult> ImportAsync(
        ImageImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return ImageImportResult.Failure(
                ImageImportFailureReason.Cancelled,
                "IMAGE_IMPORT_CANCELLED");
        }

        var pathResult = ValidatePath(request.SourcePath);
        if (pathResult.Failure is not null)
        {
            return pathResult.Failure;
        }

        var sourcePath = pathResult.Path!;
        try
        {
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Decode(source, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (UnauthorizedAccessException)
        {
            return ImageImportResult.Failure(
                ImageImportFailureReason.AccessDenied,
                "IMAGE_IMPORT_ACCESS_DENIED");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or OverflowException)
        {
            return DecodeFailed();
        }
    }

    public Task<ImageImportResult> ImportMemoryAsync(
        ImageImportMemoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Cancelled());
        }

        if (request.EncodedBytes.IsEmpty)
        {
            return Task.FromResult(ImageImportResult.Failure(
                ImageImportFailureReason.InvalidImage,
                "IMAGE_IMPORT_EMPTY_SOURCE"));
        }

        if (request.EncodedBytes.Length > resourcePolicy.MaximumSourceBytes)
        {
            return Task.FromResult(ImageImportResult.Failure(
                ImageImportFailureReason.ResourceLimitExceeded,
                "IMAGE_IMPORT_SOURCE_TOO_LARGE"));
        }

        try
        {
            var bytes = request.EncodedBytes.ToArray();
            using var source = new MemoryStream(bytes, writable: false);
            return Task.FromResult(Decode(source, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(Cancelled());
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or OverflowException)
        {
            return Task.FromResult(DecodeFailed());
        }
    }

    private ImageImportResult Decode(Stream source, CancellationToken cancellationToken)
    {
        var signature = new byte[12];
        var signatureLength = source.Read(signature, 0, signature.Length);
        var sourceFormat = DetectFormat(signature.AsSpan(0, signatureLength));
        if (sourceFormat is null)
        {
            return ImageImportResult.Failure(
                ImageImportFailureReason.UnsupportedFormat,
                "IMAGE_IMPORT_UNSUPPORTED_FORMAT");
        }

        cancellationToken.ThrowIfCancellationRequested();
        source.Position = 0;
        using var managed = new SKManagedStream(source, disposeManagedStream: false);
        using var codec = SKCodec.Create(managed);
        if (codec is null || MapEncodedFormat(codec.EncodedFormat) != sourceFormat)
        {
            return ImageImportResult.Failure(
                ImageImportFailureReason.InvalidImage,
                "IMAGE_IMPORT_INVALID_IMAGE");
        }

        var sourceInfo = codec.Info;
        var resourceFailure = ValidateDimensions(sourceInfo.Width, sourceInfo.Height);
        if (resourceFailure is not null)
        {
            return resourceFailure;
        }

        var orientation = MapOrientation(codec.EncodedOrigin);
        cancellationToken.ThrowIfCancellationRequested();
        using var colorSpace = SKColorSpace.CreateSrgb();
        var decodeInfo = new SKImageInfo(
            sourceInfo.Width,
            sourceInfo.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul,
            colorSpace);
        using var bitmap = new SKBitmap(decodeInfo);
        var decodeResult = codec.GetPixels(decodeInfo, bitmap.GetPixels());
        if (decodeResult != SKCodecResult.Success)
        {
            return ImageImportResult.Failure(
                decodeResult == SKCodecResult.IncompleteInput
                    ? ImageImportFailureReason.InvalidImage
                    : ImageImportFailureReason.DecodeFailed,
                decodeResult == SKCodecResult.IncompleteInput
                    ? "IMAGE_IMPORT_TRUNCATED_IMAGE"
                    : "IMAGE_IMPORT_DECODE_FAILED");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var packedPixels = CopyPackedRgba(bitmap);
        var normalized = NormalizeOrientation(
            packedPixels,
            sourceInfo.Width,
            sourceInfo.Height,
            orientation);
        resourceFailure = ValidateDimensions(normalized.Width, normalized.Height);
        if (resourceFailure is not null)
        {
            return resourceFailure;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var metadata = new ImageSourceMetadata(
            sourceFormat.Value,
            sourceInfo.Width,
            sourceInfo.Height,
            orientation,
            OrientationNormalized: true,
            HasIccProfile: sourceInfo.ColorSpace is not null);
        return ImageImportResult.Success(new InternalImage(
            normalized.Width,
            normalized.Height,
            checked(normalized.Width * 4),
            normalized.Pixels,
            metadata));
    }

    private static ImageImportResult Cancelled() => ImageImportResult.Failure(
        ImageImportFailureReason.Cancelled,
        "IMAGE_IMPORT_CANCELLED");

    private static ImageImportResult DecodeFailed() => ImageImportResult.Failure(
        ImageImportFailureReason.DecodeFailed,
        "IMAGE_IMPORT_DECODE_FAILED");

    private (string? Path, ImageImportResult? Failure) ValidatePath(string sourcePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathFullyQualified(sourcePath))
            {
                return (null, ImageImportResult.Failure(
                    ImageImportFailureReason.InvalidPath,
                    "IMAGE_IMPORT_ABSOLUTE_PATH_REQUIRED"));
            }

            var fullPath = Path.GetFullPath(sourcePath);
            if (!File.Exists(fullPath))
            {
                return (null, ImageImportResult.Failure(
                    ImageImportFailureReason.SourceMissing,
                    "IMAGE_IMPORT_SOURCE_MISSING"));
            }

            if (ContainsReparsePoint(fullPath))
            {
                return (null, ImageImportResult.Failure(
                    ImageImportFailureReason.InvalidPath,
                    "IMAGE_IMPORT_REPARSE_POINT_REJECTED"));
            }

            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length <= 0)
            {
                return (null, ImageImportResult.Failure(
                    ImageImportFailureReason.InvalidImage,
                    "IMAGE_IMPORT_EMPTY_SOURCE"));
            }

            if (fileInfo.Length > resourcePolicy.MaximumSourceBytes)
            {
                return (null, ImageImportResult.Failure(
                    ImageImportFailureReason.ResourceLimitExceeded,
                    "IMAGE_IMPORT_SOURCE_TOO_LARGE"));
            }

            return (fullPath, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, ImageImportResult.Failure(
                ImageImportFailureReason.AccessDenied,
                "IMAGE_IMPORT_ACCESS_DENIED"));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return (null, ImageImportResult.Failure(
                ImageImportFailureReason.InvalidPath,
                "IMAGE_IMPORT_PATH_REJECTED"));
        }
    }

    private ImageImportResult? ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return ImageImportResult.Failure(
                ImageImportFailureReason.InvalidImage,
                "IMAGE_IMPORT_INVALID_DIMENSIONS");
        }

        try
        {
            var pixels = checked((long)width * height);
            var decodedBytes = checked(pixels * 4);
            if (width > resourcePolicy.MaximumDimension
                || height > resourcePolicy.MaximumDimension
                || pixels > resourcePolicy.MaximumPixelCount
                || decodedBytes > resourcePolicy.MaximumDecodedBytes)
            {
                return ImageImportResult.Failure(
                    ImageImportFailureReason.ResourceLimitExceeded,
                    "IMAGE_IMPORT_DIMENSIONS_REJECTED");
            }
        }
        catch (OverflowException)
        {
            return ImageImportResult.Failure(
                ImageImportFailureReason.ResourceLimitExceeded,
                "IMAGE_IMPORT_DIMENSION_OVERFLOW");
        }

        return null;
    }

    private static ImageSourceFormat? DetectFormat(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            return ImageSourceFormat.Png;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)
        {
            return ImageSourceFormat.Jpeg;
        }

        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return ImageSourceFormat.WebP;
        }

        return bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M'
            ? ImageSourceFormat.Bmp
            : null;
    }

    private static ImageSourceFormat? MapEncodedFormat(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Png => ImageSourceFormat.Png,
        SKEncodedImageFormat.Jpeg => ImageSourceFormat.Jpeg,
        SKEncodedImageFormat.Webp => ImageSourceFormat.WebP,
        SKEncodedImageFormat.Bmp => ImageSourceFormat.Bmp,
        _ => null
    };

    private static ImageSourceOrientation MapOrientation(SKEncodedOrigin origin) => origin switch
    {
        SKEncodedOrigin.TopLeft => ImageSourceOrientation.Normal,
        SKEncodedOrigin.TopRight => ImageSourceOrientation.FlipHorizontal,
        SKEncodedOrigin.BottomRight => ImageSourceOrientation.Rotate180,
        SKEncodedOrigin.BottomLeft => ImageSourceOrientation.FlipVertical,
        SKEncodedOrigin.LeftTop => ImageSourceOrientation.Transpose,
        SKEncodedOrigin.RightTop => ImageSourceOrientation.Rotate90,
        SKEncodedOrigin.RightBottom => ImageSourceOrientation.Transverse,
        SKEncodedOrigin.LeftBottom => ImageSourceOrientation.Rotate270,
        _ => ImageSourceOrientation.Unspecified
    };

    private static byte[] CopyPackedRgba(SKBitmap bitmap)
    {
        var widthBytes = checked(bitmap.Width * 4);
        var result = GC.AllocateUninitializedArray<byte>(checked(widthBytes * bitmap.Height));
        var source = bitmap.GetPixelSpan();
        for (var y = 0; y < bitmap.Height; y++)
        {
            source.Slice(checked(y * bitmap.RowBytes), widthBytes)
                .CopyTo(result.AsSpan(checked(y * widthBytes), widthBytes));
        }

        return result;
    }

    private static (byte[] Pixels, int Width, int Height) NormalizeOrientation(
        byte[] source,
        int width,
        int height,
        ImageSourceOrientation orientation)
    {
        if (orientation is ImageSourceOrientation.Unspecified or ImageSourceOrientation.Normal)
        {
            return (source, width, height);
        }

        var swapsDimensions = orientation is ImageSourceOrientation.Transpose
            or ImageSourceOrientation.Rotate90
            or ImageSourceOrientation.Transverse
            or ImageSourceOrientation.Rotate270;
        var targetWidth = swapsDimensions ? height : width;
        var targetHeight = swapsDimensions ? width : height;
        var target = GC.AllocateUninitializedArray<byte>(checked(targetWidth * targetHeight * 4));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (targetX, targetY) = orientation switch
                {
                    ImageSourceOrientation.FlipHorizontal => (width - 1 - x, y),
                    ImageSourceOrientation.Rotate180 => (width - 1 - x, height - 1 - y),
                    ImageSourceOrientation.FlipVertical => (x, height - 1 - y),
                    ImageSourceOrientation.Transpose => (y, x),
                    ImageSourceOrientation.Rotate90 => (height - 1 - y, x),
                    ImageSourceOrientation.Transverse => (height - 1 - y, width - 1 - x),
                    ImageSourceOrientation.Rotate270 => (y, width - 1 - x),
                    _ => (x, y)
                };
                source.AsSpan(checked((y * width + x) * 4), 4)
                    .CopyTo(target.AsSpan(checked((targetY * targetWidth + targetX) * 4), 4));
            }
        }

        return (target, targetWidth, targetHeight);
    }

    private static bool ContainsReparsePoint(string filePath)
    {
        for (FileSystemInfo? item = new FileInfo(filePath);
            item is not null;
            item = item switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null
            })
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }
}

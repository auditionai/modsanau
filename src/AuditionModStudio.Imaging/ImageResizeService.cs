using AuditionModStudio.Core.Images;
using SkiaSharp;

namespace AuditionModStudio.Imaging;

public sealed class ImageResizeService : IImageResizeService
{
    private readonly ImageImportResourcePolicy _resourcePolicy;

    public ImageResizeService(ImageImportResourcePolicy resourcePolicy)
    {
        _resourcePolicy = resourcePolicy ?? throw new ArgumentNullException(nameof(resourcePolicy));
    }

    public Task<ImageResizeResult> ResizeAsync(
        ImageResizeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(ImageResizeResult.Failure(
                ImageResizeFailureReason.Cancelled,
                "IMAGE_RESIZE_CANCELLED"));
        }

        var validation = ValidateRequest(request);
        if (validation is not null)
        {
            return Task.FromResult(validation);
        }

        if (request.TargetWidth == request.Source.Width
            && request.TargetHeight == request.Source.Height
            && request.Options.Mode is ImageResizeMode.Stretch or ImageResizeMode.FreeAspect)
        {
            return Task.FromResult(ImageResizeResult.Success(request.Source));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var geometry = CalculateGeometry(request);
            var outputValidation = ValidateDimensions(geometry.OutputWidth, geometry.OutputHeight);
            if (outputValidation is not null)
            {
                return Task.FromResult(outputValidation);
            }

            using var colorSpace = SKColorSpace.CreateSrgb();
            using var sourceBitmap = CreatePremultipliedBitmap(request.Source, colorSpace);
            cancellationToken.ThrowIfCancellationRequested();
            var targetInfo = new SKImageInfo(
                geometry.OutputWidth,
                geometry.OutputHeight,
                SKColorType.Rgba8888,
                SKAlphaType.Premul,
                colorSpace);
            using var targetBitmap = new SKBitmap(targetInfo);
            using (var canvas = new SKCanvas(targetBitmap))
            {
                canvas.Clear(SKColors.Transparent);
                var sampling = request.Options.Interpolation == ImageInterpolationMode.NearestNeighbor
                    ? new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)
                    : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
                canvas.DrawBitmap(
                    sourceBitmap,
                    geometry.SourceRectangle,
                    geometry.DestinationRectangle,
                    sampling);
                canvas.Flush();
            }

            cancellationToken.ThrowIfCancellationRequested();
            var straightPixels = CopyAndUnpremultiply(targetBitmap);
            cancellationToken.ThrowIfCancellationRequested();
            var image = new InternalImage(
                geometry.OutputWidth,
                geometry.OutputHeight,
                checked(geometry.OutputWidth * 4),
                straightPixels,
                request.Source.SourceMetadata);
            return Task.FromResult(ImageResizeResult.Success(image));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(ImageResizeResult.Failure(
                ImageResizeFailureReason.Cancelled,
                "IMAGE_RESIZE_CANCELLED"));
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return Task.FromResult(ImageResizeResult.Failure(
                ImageResizeFailureReason.ResizeFailed,
                "IMAGE_RESIZE_FAILED"));
        }
    }

    private ImageResizeResult? ValidateRequest(ImageResizeRequest request)
    {
        if (request.Source is null || request.Options is null)
        {
            return ImageResizeResult.Failure(
                ImageResizeFailureReason.InvalidSource,
                "IMAGE_RESIZE_INVALID_REQUEST");
        }

        var targetValidation = ValidateDimensions(request.TargetWidth, request.TargetHeight);
        if (targetValidation is not null)
        {
            return targetValidation;
        }

        if (!Enum.IsDefined(request.Options.Mode))
        {
            return ImageResizeResult.Failure(
                ImageResizeFailureReason.UnsupportedMode,
                "IMAGE_RESIZE_UNSUPPORTED_MODE");
        }

        if (!Enum.IsDefined(request.Options.Interpolation)
            || !Enum.IsDefined(request.Options.Alignment))
        {
            return ImageResizeResult.Failure(
                ImageResizeFailureReason.InvalidOptions,
                "IMAGE_RESIZE_INVALID_OPTIONS");
        }

        if (request.Options.Mode == ImageResizeMode.ManualCrop)
        {
            var crop = request.Options.CropRectangle;
            if (crop is null
                || crop.X < 0
                || crop.Y < 0
                || crop.Width <= 0
                || crop.Height <= 0)
            {
                return ImageResizeResult.Failure(
                    ImageResizeFailureReason.InvalidOptions,
                    "IMAGE_RESIZE_INVALID_CROP");
            }

            try
            {
                if (checked(crop.X + crop.Width) > request.Source.Width
                    || checked(crop.Y + crop.Height) > request.Source.Height)
                {
                    return ImageResizeResult.Failure(
                        ImageResizeFailureReason.InvalidOptions,
                        "IMAGE_RESIZE_CROP_OUT_OF_BOUNDS");
                }
            }
            catch (OverflowException)
            {
                return ImageResizeResult.Failure(
                    ImageResizeFailureReason.InvalidOptions,
                    "IMAGE_RESIZE_CROP_OVERFLOW");
            }
        }
        else if (request.Options.CropRectangle is not null)
        {
            return ImageResizeResult.Failure(
                ImageResizeFailureReason.InvalidOptions,
                "IMAGE_RESIZE_UNEXPECTED_CROP");
        }

        if (request.Options.Mode == ImageResizeMode.TransparentPadding
            && (request.TargetWidth < request.Source.Width || request.TargetHeight < request.Source.Height))
        {
            return ImageResizeResult.Failure(
                ImageResizeFailureReason.InvalidTargetDimensions,
                "IMAGE_RESIZE_PADDING_TARGET_TOO_SMALL");
        }

        return null;
    }

    private ImageResizeResult? ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return ImageResizeResult.Failure(
                ImageResizeFailureReason.InvalidTargetDimensions,
                "IMAGE_RESIZE_INVALID_TARGET_DIMENSIONS");
        }

        try
        {
            var pixels = checked((long)width * height);
            var bytes = checked(pixels * 4);
            if (width > _resourcePolicy.MaximumDimension
                || height > _resourcePolicy.MaximumDimension
                || pixels > _resourcePolicy.MaximumPixelCount
                || bytes > _resourcePolicy.MaximumDecodedBytes)
            {
                return ImageResizeResult.Failure(
                    ImageResizeFailureReason.ResourceLimitExceeded,
                    "IMAGE_RESIZE_TARGET_RESOURCE_LIMIT_EXCEEDED");
            }
        }
        catch (OverflowException)
        {
            return ImageResizeResult.Failure(
                ImageResizeFailureReason.ResourceLimitExceeded,
                "IMAGE_RESIZE_TARGET_OVERFLOW");
        }

        return null;
    }

    private static ResizeGeometry CalculateGeometry(ImageResizeRequest request)
    {
        var sourceWidth = request.Source.Width;
        var sourceHeight = request.Source.Height;
        var targetWidth = request.TargetWidth;
        var targetHeight = request.TargetHeight;
        var fullSource = SKRect.Create(sourceWidth, sourceHeight);
        var fullTarget = SKRect.Create(targetWidth, targetHeight);

        return request.Options.Mode switch
        {
            ImageResizeMode.Stretch or ImageResizeMode.FreeAspect =>
                new(fullSource, fullTarget, targetWidth, targetHeight),
            ImageResizeMode.Fit => CalculateFit(
                sourceWidth,
                sourceHeight,
                targetWidth,
                targetHeight,
                request.Options.Alignment,
                keepAspectOutput: false),
            ImageResizeMode.KeepAspect => CalculateFit(
                sourceWidth,
                sourceHeight,
                targetWidth,
                targetHeight,
                request.Options.Alignment,
                keepAspectOutput: true),
            ImageResizeMode.Fill => CalculateFill(
                sourceWidth,
                sourceHeight,
                targetWidth,
                targetHeight,
                request.Options.Alignment),
            ImageResizeMode.ManualCrop => new(
                SKRect.Create(
                    request.Options.CropRectangle!.X,
                    request.Options.CropRectangle.Y,
                    request.Options.CropRectangle.Width,
                    request.Options.CropRectangle.Height),
                fullTarget,
                targetWidth,
                targetHeight),
            ImageResizeMode.CanvasResize or ImageResizeMode.TransparentPadding => CalculateCanvas(
                sourceWidth,
                sourceHeight,
                targetWidth,
                targetHeight,
                request.Options.Alignment),
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
    }

    private static ResizeGeometry CalculateFit(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        ImageResizeAlignment alignment,
        bool keepAspectOutput)
    {
        var scale = Math.Min((double)targetWidth / sourceWidth, (double)targetHeight / sourceHeight);
        var fittedWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale, MidpointRounding.AwayFromZero));
        var fittedHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale, MidpointRounding.AwayFromZero));
        var outputWidth = keepAspectOutput ? fittedWidth : targetWidth;
        var outputHeight = keepAspectOutput ? fittedHeight : targetHeight;
        var destination = keepAspectOutput
            ? SKRect.Create(fittedWidth, fittedHeight)
            : AlignRectangle(fittedWidth, fittedHeight, targetWidth, targetHeight, alignment);
        return new(
            SKRect.Create(sourceWidth, sourceHeight),
            destination,
            outputWidth,
            outputHeight);
    }

    private static ResizeGeometry CalculateFill(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        ImageResizeAlignment alignment)
    {
        var targetAspect = (double)targetWidth / targetHeight;
        var sourceAspect = (double)sourceWidth / sourceHeight;
        float cropWidth;
        float cropHeight;
        if (sourceAspect > targetAspect)
        {
            cropHeight = sourceHeight;
            cropWidth = (float)(sourceHeight * targetAspect);
        }
        else
        {
            cropWidth = sourceWidth;
            cropHeight = (float)(sourceWidth / targetAspect);
        }

        var sourceRectangle = AlignRectangle(cropWidth, cropHeight, sourceWidth, sourceHeight, alignment);
        return new(sourceRectangle, SKRect.Create(targetWidth, targetHeight), targetWidth, targetHeight);
    }

    private static ResizeGeometry CalculateCanvas(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        ImageResizeAlignment alignment) => new(
            SKRect.Create(sourceWidth, sourceHeight),
            AlignRectangle(sourceWidth, sourceHeight, targetWidth, targetHeight, alignment),
            targetWidth,
            targetHeight);

    private static SKRect AlignRectangle(
        float width,
        float height,
        float containerWidth,
        float containerHeight,
        ImageResizeAlignment alignment)
    {
        var x = alignment switch
        {
            ImageResizeAlignment.Left => 0,
            ImageResizeAlignment.Right => containerWidth - width,
            _ => (containerWidth - width) / 2
        };
        var y = alignment switch
        {
            ImageResizeAlignment.Top => 0,
            ImageResizeAlignment.Bottom => containerHeight - height,
            _ => (containerHeight - height) / 2
        };
        return SKRect.Create(x, y, width, height);
    }

    private static SKBitmap CreatePremultipliedBitmap(InternalImage image, SKColorSpace colorSpace)
    {
        var info = new SKImageInfo(
            image.Width,
            image.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul,
            colorSpace);
        var bitmap = new SKBitmap(info);
        var destination = bitmap.GetPixelSpan();
        var source = image.Pixels.AsSpan();
        var packedStride = checked(image.Width * 4);
        for (var y = 0; y < image.Height; y++)
        {
            var sourceRow = source.Slice(checked(y * image.Stride), packedStride);
            var destinationRow = destination.Slice(checked(y * bitmap.RowBytes), packedStride);
            for (var x = 0; x < packedStride; x += 4)
            {
                var alpha = sourceRow[x + 3];
                destinationRow[x] = Premultiply(sourceRow[x], alpha);
                destinationRow[x + 1] = Premultiply(sourceRow[x + 1], alpha);
                destinationRow[x + 2] = Premultiply(sourceRow[x + 2], alpha);
                destinationRow[x + 3] = alpha;
            }
        }

        return bitmap;
    }

    private static byte[] CopyAndUnpremultiply(SKBitmap bitmap)
    {
        var packedStride = checked(bitmap.Width * 4);
        var result = GC.AllocateUninitializedArray<byte>(checked(packedStride * bitmap.Height));
        var source = bitmap.GetPixelSpan();
        for (var y = 0; y < bitmap.Height; y++)
        {
            var sourceRow = source.Slice(checked(y * bitmap.RowBytes), packedStride);
            var destinationRow = result.AsSpan(checked(y * packedStride), packedStride);
            for (var x = 0; x < packedStride; x += 4)
            {
                var alpha = sourceRow[x + 3];
                destinationRow[x] = Unpremultiply(sourceRow[x], alpha);
                destinationRow[x + 1] = Unpremultiply(sourceRow[x + 1], alpha);
                destinationRow[x + 2] = Unpremultiply(sourceRow[x + 2], alpha);
                destinationRow[x + 3] = alpha;
            }
        }

        return result;
    }

    private static byte Premultiply(byte color, byte alpha) =>
        checked((byte)((color * alpha + 127) / 255));

    private static byte Unpremultiply(byte color, byte alpha) =>
        alpha == 0
            ? (byte)0
            : checked((byte)Math.Min(255, (color * 255 + alpha / 2) / alpha));

    private sealed record ResizeGeometry(
        SKRect SourceRectangle,
        SKRect DestinationRectangle,
        int OutputWidth,
        int OutputHeight);
}

using System.Numerics;
using AuditionModStudio.Core.Images;

namespace AuditionModStudio.Imaging;

public sealed class ImageTransformService : IImageTransformService
{
    private const double Epsilon = 1e-12;

    public ImageTransformResult<InteractiveImageTransformState> Create(
        InternalImage source,
        ImageTransformConstraints? constraints = null)
    {
        if (source is null)
        {
            return Failure<InteractiveImageTransformState>(
                ImageTransformFailureReason.InvalidImage,
                "IMAGE_TRANSFORM_INVALID_SOURCE");
        }

        var effectiveConstraints = constraints ?? ImageTransformConstraints.Default;
        if (!ValidConstraints(effectiveConstraints))
        {
            return Failure<InteractiveImageTransformState>(
                ImageTransformFailureReason.InvalidTransform,
                "IMAGE_TRANSFORM_INVALID_CONSTRAINTS");
        }

        return ImageTransformResult<InteractiveImageTransformState>.Success(
            Identity(source.Width, source.Height, effectiveConstraints));
    }

    public ImageTransformResult<InteractiveImageTransformState> SetCrop(
        InteractiveImageTransformState state,
        NormalizedImageRectangle requestedCrop,
        CropAspectRatio? aspectRatio = null,
        CropAnchor anchor = CropAnchor.Center)
    {
        var stateFailure = ValidateState(state);
        if (stateFailure is not null)
        {
            return stateFailure;
        }

        if (!Finite(requestedCrop.X, requestedCrop.Y, requestedCrop.Width, requestedCrop.Height)
            || requestedCrop.Width <= 0
            || requestedCrop.Height <= 0
            || !Enum.IsDefined(anchor))
        {
            return Failure<InteractiveImageTransformState>(
                ImageTransformFailureReason.InvalidCrop,
                "IMAGE_TRANSFORM_INVALID_CROP");
        }

        if (aspectRatio is not null
            && (!Finite(aspectRatio.Value.Width, aspectRatio.Value.Height)
                || aspectRatio.Value.Width <= 0
                || aspectRatio.Value.Height <= 0))
        {
            return Failure<InteractiveImageTransformState>(
                ImageTransformFailureReason.InvalidAspectRatio,
                "IMAGE_TRANSFORM_INVALID_ASPECT_RATIO");
        }

        var left = Math.Clamp(requestedCrop.X, 0, 1);
        var top = Math.Clamp(requestedCrop.Y, 0, 1);
        var right = Math.Clamp(requestedCrop.Right, 0, 1);
        var bottom = Math.Clamp(requestedCrop.Bottom, 0, 1);
        if (right - left <= Epsilon || bottom - top <= Epsilon)
        {
            return Failure<InteractiveImageTransformState>(
                ImageTransformFailureReason.InvalidCrop,
                "IMAGE_TRANSFORM_CROP_OUTSIDE_IMAGE");
        }

        var crop = new NormalizedImageRectangle(left, top, right - left, bottom - top);
        if (aspectRatio is not null)
        {
            crop = ApplyAspectRatio(crop, state.ImageWidth, state.ImageHeight, aspectRatio.Value, anchor);
        }

        if (crop.Width * state.ImageWidth + Epsilon < state.Constraints.MinimumCropWidthPixels
            || crop.Height * state.ImageHeight + Epsilon < state.Constraints.MinimumCropHeightPixels)
        {
            return Failure<InteractiveImageTransformState>(
                ImageTransformFailureReason.InvalidCrop,
                "IMAGE_TRANSFORM_CROP_BELOW_MINIMUM");
        }

        return ImageTransformResult<InteractiveImageTransformState>.Success(state with { Crop = crop });
    }

    public ImageTransformResult<InteractiveImageTransformState> SetViewportTransform(
        InteractiveImageTransformState state,
        double zoom,
        ViewportVector pan)
    {
        var stateFailure = ValidateState(state);
        if (stateFailure is not null)
        {
            return stateFailure;
        }

        if (!Finite(zoom, pan.X, pan.Y)
            || zoom < state.Constraints.MinimumZoom
            || zoom > state.Constraints.MaximumZoom)
        {
            return Failure<InteractiveImageTransformState>(
                ImageTransformFailureReason.InvalidZoom,
                "IMAGE_TRANSFORM_INVALID_VIEWPORT_TRANSFORM");
        }

        return ImageTransformResult<InteractiveImageTransformState>.Success(
            state with { Zoom = zoom, Pan = pan });
    }

    public ImageTransformResult<InteractiveImageTransformState> SetImageTransform(
        InteractiveImageTransformState state,
        ImageScale scale,
        ImageQuarterTurn rotation,
        bool flipHorizontal,
        bool flipVertical,
        ImagePixelVector translation)
    {
        var stateFailure = ValidateState(state);
        if (stateFailure is not null)
        {
            return stateFailure;
        }

        if (!Finite(scale.X, scale.Y, translation.X, translation.Y)
            || scale.X <= 0
            || scale.Y <= 0
            || !Enum.IsDefined(rotation))
        {
            return Failure<InteractiveImageTransformState>(
                ImageTransformFailureReason.InvalidTransform,
                "IMAGE_TRANSFORM_INVALID_IMAGE_TRANSFORM");
        }

        return ImageTransformResult<InteractiveImageTransformState>.Success(state with
        {
            Scale = scale,
            Rotation = rotation,
            FlipHorizontal = flipHorizontal,
            FlipVertical = flipVertical,
            Translation = translation
        });
    }

    public ImageTransformResult<InteractiveImageTransformState> Reset(
        InteractiveImageTransformState state)
    {
        var stateFailure = ValidateState(state);
        return stateFailure ?? ImageTransformResult<InteractiveImageTransformState>.Success(
            Identity(state.ImageWidth, state.ImageHeight, state.Constraints));
    }

    public ImageTransformResult<ImageCropRectangle> ToPixelCrop(InteractiveImageTransformState state)
    {
        var stateFailure = ValidateState(state);
        if (stateFailure is not null)
        {
            return Failure<ImageCropRectangle>(stateFailure.FailureReason, stateFailure.DiagnosticCode!);
        }

        try
        {
            var left = Math.Clamp((int)Math.Floor(state.Crop.X * state.ImageWidth), 0, state.ImageWidth - 1);
            var top = Math.Clamp((int)Math.Floor(state.Crop.Y * state.ImageHeight), 0, state.ImageHeight - 1);
            var right = Math.Clamp((int)Math.Ceiling(state.Crop.Right * state.ImageWidth), left + 1, state.ImageWidth);
            var bottom = Math.Clamp((int)Math.Ceiling(state.Crop.Bottom * state.ImageHeight), top + 1, state.ImageHeight);
            return ImageTransformResult<ImageCropRectangle>.Success(
                new(left, top, checked(right - left), checked(bottom - top)));
        }
        catch (Exception exception) when (exception is OverflowException or ArgumentException)
        {
            return Failure<ImageCropRectangle>(
                ImageTransformFailureReason.ConversionOverflow,
                "IMAGE_TRANSFORM_PIXEL_CONVERSION_OVERFLOW");
        }
    }

    public ImageTransformResult<ViewportPoint> MapImageToViewport(
        InteractiveImageTransformState state,
        ViewportSize viewport,
        ImagePixelPoint point)
    {
        var matrixResult = CreateProjection(state, viewport);
        if (!matrixResult.Succeeded)
        {
            return Failure<ViewportPoint>(matrixResult.FailureReason, matrixResult.DiagnosticCode!);
        }

        if (!Finite(point.X, point.Y))
        {
            return Failure<ViewportPoint>(
                ImageTransformFailureReason.InvalidCoordinate,
                "IMAGE_TRANSFORM_INVALID_IMAGE_POINT");
        }

        var mapped = Vector2.Transform(new((float)point.X, (float)point.Y), matrixResult.Value!.Value);
        return ImageTransformResult<ViewportPoint>.Success(new(mapped.X, mapped.Y));
    }

    public ImageTransformResult<ImagePixelPoint> MapViewportToImage(
        InteractiveImageTransformState state,
        ViewportSize viewport,
        ViewportPoint point)
    {
        var matrixResult = CreateProjection(state, viewport);
        if (!matrixResult.Succeeded)
        {
            return Failure<ImagePixelPoint>(matrixResult.FailureReason, matrixResult.DiagnosticCode!);
        }

        if (!Finite(point.X, point.Y)
            || !Matrix3x2.Invert(matrixResult.Value!.Value, out var inverse))
        {
            return Failure<ImagePixelPoint>(
                ImageTransformFailureReason.InvalidCoordinate,
                "IMAGE_TRANSFORM_INVERSE_MAPPING_FAILED");
        }

        var mapped = Vector2.Transform(new((float)point.X, (float)point.Y), inverse);
        return ImageTransformResult<ImagePixelPoint>.Success(new(mapped.X, mapped.Y));
    }

    public ImageTransformResult<ImageResizeRequest> ToManualCropResizeRequest(
        InteractiveImageTransformState state,
        InternalImage source,
        int targetWidth,
        int targetHeight,
        ImageInterpolationMode interpolation = ImageInterpolationMode.Linear)
    {
        if (source is null || source.Width != state.ImageWidth || source.Height != state.ImageHeight)
        {
            return Failure<ImageResizeRequest>(
                ImageTransformFailureReason.ImageMismatch,
                "IMAGE_TRANSFORM_SOURCE_MISMATCH");
        }

        var crop = ToPixelCrop(state);
        if (!crop.Succeeded)
        {
            return Failure<ImageResizeRequest>(crop.FailureReason, crop.DiagnosticCode!);
        }

        if (targetWidth <= 0 || targetHeight <= 0 || !Enum.IsDefined(interpolation))
        {
            return Failure<ImageResizeRequest>(
                ImageTransformFailureReason.InvalidTransform,
                "IMAGE_TRANSFORM_INVALID_RESIZE_TARGET");
        }

        return ImageTransformResult<ImageResizeRequest>.Success(new(
            source,
            targetWidth,
            targetHeight,
            new ImageResizeOptions(
                ImageResizeMode.ManualCrop,
                interpolation,
                CropRectangle: crop.Value)));
    }

    private static ImageTransformResult<Matrix3x2?> CreateProjection(
        InteractiveImageTransformState state,
        ViewportSize viewport)
    {
        var stateFailure = ValidateState(state);
        if (stateFailure is not null)
        {
            return Failure<Matrix3x2?>(stateFailure.FailureReason, stateFailure.DiagnosticCode!);
        }

        if (!Finite(viewport.Width, viewport.Height) || viewport.Width <= 0 || viewport.Height <= 0)
        {
            return Failure<Matrix3x2?>(
                ImageTransformFailureReason.InvalidViewport,
                "IMAGE_TRANSFORM_INVALID_VIEWPORT");
        }

        var imageMatrix = CreateImageMatrix(state);
        var corners = new[]
        {
            Vector2.Transform(Vector2.Zero, imageMatrix),
            Vector2.Transform(new(state.ImageWidth, 0), imageMatrix),
            Vector2.Transform(new(0, state.ImageHeight), imageMatrix),
            Vector2.Transform(new(state.ImageWidth, state.ImageHeight), imageMatrix)
        };
        var minX = corners.Min(point => point.X);
        var minY = corners.Min(point => point.Y);
        var maxX = corners.Max(point => point.X);
        var maxY = corners.Max(point => point.Y);
        var boundsWidth = maxX - minX;
        var boundsHeight = maxY - minY;
        if (boundsWidth <= 0 || boundsHeight <= 0)
        {
            return Failure<Matrix3x2?>(
                ImageTransformFailureReason.InvalidTransform,
                "IMAGE_TRANSFORM_DEGENERATE_BOUNDS");
        }

        var fitScale = (float)Math.Min(viewport.Width / boundsWidth, viewport.Height / boundsHeight);
        var fittedWidth = boundsWidth * fitScale;
        var fittedHeight = boundsHeight * fitScale;
        var fitOffset = new Vector2(
            (float)((viewport.Width - fittedWidth) / 2),
            (float)((viewport.Height - fittedHeight) / 2));
        var fitMatrix = Matrix3x2.CreateTranslation(-minX, -minY)
            * Matrix3x2.CreateScale(fitScale)
            * Matrix3x2.CreateTranslation(fitOffset);
        var viewportCenter = new Vector2((float)(viewport.Width / 2), (float)(viewport.Height / 2));
        var zoomPanMatrix = Matrix3x2.CreateTranslation(-viewportCenter)
            * Matrix3x2.CreateScale((float)state.Zoom)
            * Matrix3x2.CreateTranslation(
                viewportCenter + new Vector2((float)state.Pan.X, (float)state.Pan.Y));
        return ImageTransformResult<Matrix3x2?>.Success(imageMatrix * fitMatrix * zoomPanMatrix);
    }

    private static Matrix3x2 CreateImageMatrix(InteractiveImageTransformState state)
    {
        var center = new Vector2(state.ImageWidth / 2f, state.ImageHeight / 2f);
        var scaleX = (float)(state.Scale.X * (state.FlipHorizontal ? -1 : 1));
        var scaleY = (float)(state.Scale.Y * (state.FlipVertical ? -1 : 1));
        var radians = state.Rotation switch
        {
            ImageQuarterTurn.None => 0,
            ImageQuarterTurn.Clockwise90 => MathF.PI / 2,
            ImageQuarterTurn.Clockwise180 => MathF.PI,
            ImageQuarterTurn.Clockwise270 => MathF.PI * 3 / 2,
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
        return Matrix3x2.CreateTranslation(-center)
            * Matrix3x2.CreateScale(scaleX, scaleY)
            * Matrix3x2.CreateRotation(radians)
            * Matrix3x2.CreateTranslation(
                center + new Vector2((float)state.Translation.X, (float)state.Translation.Y));
    }

    private static NormalizedImageRectangle ApplyAspectRatio(
        NormalizedImageRectangle crop,
        int imageWidth,
        int imageHeight,
        CropAspectRatio ratio,
        CropAnchor anchor)
    {
        var pixelWidth = crop.Width * imageWidth;
        var pixelHeight = crop.Height * imageHeight;
        var targetRatio = ratio.Value;
        double width = crop.Width;
        double height = crop.Height;
        if (pixelWidth / pixelHeight > targetRatio)
        {
            width = pixelHeight * targetRatio / imageWidth;
        }
        else
        {
            height = pixelWidth / targetRatio / imageHeight;
        }

        var x = anchor switch
        {
            CropAnchor.TopLeft or CropAnchor.BottomLeft => crop.X,
            CropAnchor.TopRight or CropAnchor.BottomRight => crop.Right - width,
            _ => crop.X + (crop.Width - width) / 2
        };
        var y = anchor switch
        {
            CropAnchor.TopLeft or CropAnchor.TopRight => crop.Y,
            CropAnchor.BottomLeft or CropAnchor.BottomRight => crop.Bottom - height,
            _ => crop.Y + (crop.Height - height) / 2
        };
        return new(x, y, width, height);
    }

    private static ImageTransformResult<InteractiveImageTransformState>? ValidateState(
        InteractiveImageTransformState? state)
    {
        if (state is null
            || state.ImageWidth <= 0
            || state.ImageHeight <= 0
            || !ValidConstraints(state.Constraints)
            || !Finite(
                state.Crop.X,
                state.Crop.Y,
                state.Crop.Width,
                state.Crop.Height,
                state.Zoom,
                state.Pan.X,
                state.Pan.Y,
                state.Scale.X,
                state.Scale.Y,
                state.Translation.X,
                state.Translation.Y)
            || state.Crop.X < 0
            || state.Crop.Y < 0
            || state.Crop.Width <= 0
            || state.Crop.Height <= 0
            || state.Crop.Right > 1 + Epsilon
            || state.Crop.Bottom > 1 + Epsilon
            || state.Zoom < state.Constraints.MinimumZoom
            || state.Zoom > state.Constraints.MaximumZoom
            || state.Scale.X <= 0
            || state.Scale.Y <= 0
            || !Enum.IsDefined(state.Rotation))
        {
            return Failure<InteractiveImageTransformState>(
                ImageTransformFailureReason.InvalidTransform,
                "IMAGE_TRANSFORM_INVALID_STATE");
        }

        return null;
    }

    private static bool ValidConstraints(ImageTransformConstraints? constraints) =>
        constraints is not null
        && Finite(
            constraints.MinimumZoom,
            constraints.MaximumZoom,
            constraints.MinimumCropWidthPixels,
            constraints.MinimumCropHeightPixels)
        && constraints.MinimumZoom > 0
        && constraints.MaximumZoom >= constraints.MinimumZoom
        && constraints.MinimumCropWidthPixels > 0
        && constraints.MinimumCropHeightPixels > 0;

    private static InteractiveImageTransformState Identity(
        int width,
        int height,
        ImageTransformConstraints constraints) => new(
            width,
            height,
            new(0, 0, 1, 1),
            Zoom: 1,
            Pan: new(0, 0),
            Scale: ImageScale.Identity,
            Rotation: ImageQuarterTurn.None,
            FlipHorizontal: false,
            FlipVertical: false,
            Translation: new(0, 0),
            constraints);

    private static bool Finite(params double[] values) => values.All(double.IsFinite);

    private static ImageTransformResult<T> Failure<T>(
        ImageTransformFailureReason reason,
        string diagnosticCode) => ImageTransformResult<T>.Failure(reason, diagnosticCode);
}

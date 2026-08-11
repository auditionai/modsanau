namespace AuditionModStudio.Core.Images;

public interface IImageTransformService
{
    ImageTransformResult<InteractiveImageTransformState> Create(
        InternalImage source,
        ImageTransformConstraints? constraints = null);

    ImageTransformResult<InteractiveImageTransformState> SetCrop(
        InteractiveImageTransformState state,
        NormalizedImageRectangle requestedCrop,
        CropAspectRatio? aspectRatio = null,
        CropAnchor anchor = CropAnchor.Center);

    ImageTransformResult<InteractiveImageTransformState> SetViewportTransform(
        InteractiveImageTransformState state,
        double zoom,
        ViewportVector pan);

    ImageTransformResult<InteractiveImageTransformState> SetImageTransform(
        InteractiveImageTransformState state,
        ImageScale scale,
        ImageQuarterTurn rotation,
        bool flipHorizontal,
        bool flipVertical,
        ImagePixelVector translation);

    ImageTransformResult<InteractiveImageTransformState> Reset(
        InteractiveImageTransformState state);

    ImageTransformResult<ImageCropRectangle> ToPixelCrop(
        InteractiveImageTransformState state);

    ImageTransformResult<ViewportPoint> MapImageToViewport(
        InteractiveImageTransformState state,
        ViewportSize viewport,
        ImagePixelPoint point);

    ImageTransformResult<ImagePixelPoint> MapViewportToImage(
        InteractiveImageTransformState state,
        ViewportSize viewport,
        ViewportPoint point);

    ImageTransformResult<ImageResizeRequest> ToManualCropResizeRequest(
        InteractiveImageTransformState state,
        InternalImage source,
        int targetWidth,
        int targetHeight,
        ImageInterpolationMode interpolation = ImageInterpolationMode.Linear);
}

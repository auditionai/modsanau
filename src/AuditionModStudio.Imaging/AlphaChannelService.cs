using AuditionModStudio.Core.Images;

namespace AuditionModStudio.Imaging;

public sealed class AlphaChannelService : IAlphaChannelService
{
    private readonly ImageImportResourcePolicy _resourcePolicy;

    public AlphaChannelService(ImageImportResourcePolicy resourcePolicy)
    {
        _resourcePolicy = resourcePolicy ?? throw new ArgumentNullException(nameof(resourcePolicy));
    }

    public Task<AlphaChannelResult> ProcessAsync(
        AlphaChannelRequest request,
        CancellationToken cancellationToken = default)
    {
        var validation = Validate(request);
        if (validation is not null)
        {
            return Task.FromResult(validation);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Cancelled());
        }

        return Task.Run(() => Process(request, cancellationToken), CancellationToken.None);
    }

    private static AlphaChannelResult Process(
        AlphaChannelRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return request.Operation switch
            {
                AlphaChannelOperation.View => View(request.Source, cancellationToken),
                AlphaChannelOperation.Extract => Extract(request.Source, cancellationToken),
                AlphaChannelOperation.Replace => Replace(
                    request.Source,
                    request.Replacement!,
                    cancellationToken),
                AlphaChannelOperation.Invert => Transform(
                    request.Source,
                    static alpha => byte.MaxValue - alpha,
                    cancellationToken),
                AlphaChannelOperation.Threshold => Transform(
                    request.Source,
                    alpha => alpha >= request.Threshold!.Value ? byte.MaxValue : byte.MinValue,
                    cancellationToken),
                _ => InvalidOperation()
            };
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or OutOfMemoryException)
        {
            return AlphaChannelResult.Failure(
                AlphaChannelFailureReason.ProcessingFailed,
                "ALPHA_CHANNEL_PROCESSING_FAILED");
        }
    }

    private static AlphaChannelResult View(
        InternalImage source,
        CancellationToken cancellationToken)
    {
        var output = new byte[source.Pixels.Length];
        var sourcePixels = source.Pixels.AsSpan();
        for (var row = 0; row < source.Height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowStart = row * source.Stride;
            for (var offset = rowStart; offset < rowStart + source.Stride; offset += 4)
            {
                var alpha = sourcePixels[offset + 3];
                output[offset] = alpha;
                output[offset + 1] = alpha;
                output[offset + 2] = alpha;
                output[offset + 3] = byte.MaxValue;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return AlphaChannelResult.ImageSuccess(CreateImage(source, output));
    }

    private static AlphaChannelResult Extract(
        InternalImage source,
        CancellationToken cancellationToken)
    {
        var output = new byte[checked(source.Width * source.Height)];
        var sourcePixels = source.Pixels.AsSpan();
        for (var row = 0; row < source.Height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceOffset = row * source.Stride + 3;
            var outputOffset = row * source.Width;
            for (var column = 0; column < source.Width; column++)
            {
                output[outputOffset + column] = sourcePixels[sourceOffset + (column * 4)];
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return AlphaChannelResult.ChannelSuccess(new AlphaChannelData(source.Width, source.Height, output));
    }

    private static AlphaChannelResult Replace(
        InternalImage source,
        AlphaChannelData replacement,
        CancellationToken cancellationToken)
    {
        var output = source.Pixels.ToArray();
        var replacementValues = replacement.Values.AsSpan();
        var changed = false;
        for (var row = 0; row < source.Height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outputOffset = row * source.Stride + 3;
            var replacementOffset = row * replacement.Stride;
            for (var column = 0; column < source.Width; column++)
            {
                var alphaOffset = outputOffset + (column * 4);
                var alpha = replacementValues[replacementOffset + column];
                changed |= output[alphaOffset] != alpha;
                output[alphaOffset] = alpha;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return AlphaChannelResult.ImageSuccess(changed ? CreateImage(source, output) : source);
    }

    private static AlphaChannelResult Transform(
        InternalImage source,
        Func<byte, int> transform,
        CancellationToken cancellationToken)
    {
        var output = source.Pixels.ToArray();
        var changed = false;
        for (var row = 0; row < source.Height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowStart = row * source.Stride;
            for (var offset = rowStart + 3; offset < rowStart + source.Stride; offset += 4)
            {
                var alpha = checked((byte)transform(output[offset]));
                changed |= output[offset] != alpha;
                output[offset] = alpha;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return AlphaChannelResult.ImageSuccess(changed ? CreateImage(source, output) : source);
    }

    private AlphaChannelResult? Validate(AlphaChannelRequest request)
    {
        if (request is null || request.Source is null)
        {
            return AlphaChannelResult.Failure(
                AlphaChannelFailureReason.InvalidRequest,
                "ALPHA_CHANNEL_INVALID_REQUEST");
        }

        if (!Enum.IsDefined(request.Operation))
        {
            return InvalidOperation();
        }

        var hasUnexpectedArguments = request.Operation switch
        {
            AlphaChannelOperation.Replace => request.Threshold is not null,
            AlphaChannelOperation.Threshold => request.Replacement is not null,
            _ => request.Replacement is not null || request.Threshold is not null
        };
        if (hasUnexpectedArguments)
        {
            return AlphaChannelResult.Failure(
                AlphaChannelFailureReason.InvalidRequest,
                "ALPHA_CHANNEL_UNEXPECTED_ARGUMENTS");
        }

        try
        {
            var pixelCount = checked((long)request.Source.Width * request.Source.Height);
            var byteCount = checked(pixelCount * 4);
            if (request.Source.Width > _resourcePolicy.MaximumDimension
                || request.Source.Height > _resourcePolicy.MaximumDimension
                || pixelCount > _resourcePolicy.MaximumPixelCount
                || byteCount > _resourcePolicy.MaximumDecodedBytes)
            {
                return AlphaChannelResult.Failure(
                    AlphaChannelFailureReason.ResourceLimitExceeded,
                    "ALPHA_CHANNEL_RESOURCE_LIMIT_EXCEEDED");
            }
        }
        catch (OverflowException)
        {
            return AlphaChannelResult.Failure(
                AlphaChannelFailureReason.ResourceLimitExceeded,
                "ALPHA_CHANNEL_RESOURCE_OVERFLOW");
        }

        if (request.Operation == AlphaChannelOperation.Replace
            && (request.Replacement is null
                || request.Replacement.Width != request.Source.Width
                || request.Replacement.Height != request.Source.Height))
        {
            return AlphaChannelResult.Failure(
                AlphaChannelFailureReason.ReplacementDimensionMismatch,
                "ALPHA_CHANNEL_REPLACEMENT_DIMENSION_MISMATCH");
        }

        if (request.Operation == AlphaChannelOperation.Threshold
            && (request.Threshold is < byte.MinValue or > byte.MaxValue or null))
        {
            return AlphaChannelResult.Failure(
                AlphaChannelFailureReason.InvalidThreshold,
                "ALPHA_CHANNEL_INVALID_THRESHOLD");
        }

        return null;
    }

    private static InternalImage CreateImage(InternalImage source, byte[] pixels) =>
        new(source.Width, source.Height, source.Stride, pixels, source.SourceMetadata);

    private static AlphaChannelResult InvalidOperation() =>
        AlphaChannelResult.Failure(
            AlphaChannelFailureReason.InvalidOperation,
            "ALPHA_CHANNEL_INVALID_OPERATION");

    private static AlphaChannelResult Cancelled() =>
        AlphaChannelResult.Failure(
            AlphaChannelFailureReason.Cancelled,
            "ALPHA_CHANNEL_CANCELLED");
}

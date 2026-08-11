using System.Collections.Immutable;

namespace AuditionModStudio.Core.Images;

public enum AlphaChannelOperation
{
    View,
    Extract,
    Replace,
    Invert,
    Threshold
}

public sealed class AlphaChannelData
{
    public AlphaChannelData(int width, int height, ReadOnlySpan<byte> values)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var expectedLength = checked(width * height);
        if (values.Length != expectedLength)
        {
            throw new ArgumentException("Alpha channel length does not match its dimensions.", nameof(values));
        }

        Width = width;
        Height = height;
        Values = ImmutableArray.Create(values.ToArray());
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride => Width;

    public ImmutableArray<byte> Values { get; }
}

public sealed record AlphaChannelRequest(
    InternalImage Source,
    AlphaChannelOperation Operation,
    AlphaChannelData? Replacement = null,
    int? Threshold = null);

public enum AlphaChannelFailureReason
{
    None,
    InvalidRequest,
    InvalidOperation,
    InvalidThreshold,
    ReplacementDimensionMismatch,
    ResourceLimitExceeded,
    ProcessingFailed,
    Cancelled
}

public sealed record AlphaChannelResult(
    bool Succeeded,
    bool Cancelled,
    AlphaChannelFailureReason FailureReason,
    string? DiagnosticCode,
    InternalImage? Image,
    AlphaChannelData? Channel)
{
    public static AlphaChannelResult ImageSuccess(InternalImage image) =>
        new(true, false, AlphaChannelFailureReason.None, null, image, null);

    public static AlphaChannelResult ChannelSuccess(AlphaChannelData channel) =>
        new(true, false, AlphaChannelFailureReason.None, null, null, channel);

    public static AlphaChannelResult Failure(
        AlphaChannelFailureReason reason,
        string diagnosticCode) =>
        new(false, reason == AlphaChannelFailureReason.Cancelled, reason, diagnosticCode, null, null);
}

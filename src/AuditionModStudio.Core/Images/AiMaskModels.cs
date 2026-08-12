using System.Collections.Immutable;
using AuditionModStudio.Core.Projects;

namespace AuditionModStudio.Core.Images;

public sealed class AiMask
{
    public const long MaximumPixels = 16L * 1024 * 1024;

    public AiMask(int width, int height, ReadOnlySpan<byte> opacity)
    {
        if (width <= 0 || height <= 0 || (long)width * height > MaximumPixels)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (opacity.Length != checked(width * height))
            throw new ArgumentException("Mask opacity does not match its dimensions.", nameof(opacity));
        Width = width;
        Height = height;
        Opacity = ImmutableArray.Create(opacity.ToArray());
    }

    public int Width { get; }
    public int Height { get; }
    public ImmutableArray<byte> Opacity { get; }
}

public enum AiMaskBrushMode { Paint, Erase }

public sealed record AiMaskBrushSettings(
    AiMaskBrushMode Mode,
    double Size,
    double Hardness,
    double Opacity)
{
    public bool IsValid => Enum.IsDefined(Mode)
        && double.IsFinite(Size) && Size is >= 1 and <= 512
        && double.IsFinite(Hardness) && Hardness is >= 0 and <= 1
        && double.IsFinite(Opacity) && Opacity is > 0 and <= 1;
}

public readonly record struct AiMaskPoint(double X, double Y)
{
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y);
}

public sealed record AiMaskStroke(
    IReadOnlyList<AiMaskPoint> Points,
    AiMaskBrushSettings Brush);

public sealed record AiMaskEditResult(bool Succeeded, string DiagnosticCode, AiMask? Mask);

public interface IAiMaskEditingService
{
    AiMaskEditResult Create(InternalImage source);
    AiMaskEditResult ApplyStroke(AiMask mask, AiMaskStroke stroke);
    AiMaskEditResult Clear(AiMask mask);
    AiMaskEditResult Invert(AiMask mask);
    Task<InternalImage?> ComposeOverlayAsync(AiMask mask, CancellationToken cancellationToken = default);
}

public sealed record AiMaskAssetResult(bool Succeeded, string DiagnosticCode, string? RelativePath);

public interface IAiMaskAssetStore
{
    Task<AiMaskAssetResult> SaveAsync(
        IProjectArchiveWorkspace workspace,
        AiMask mask,
        CancellationToken cancellationToken = default);
}

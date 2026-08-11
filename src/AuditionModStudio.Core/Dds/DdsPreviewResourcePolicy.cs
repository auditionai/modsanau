namespace AuditionModStudio.Core.Dds;

public sealed record DdsPreviewResourcePolicy(
    long MaximumInputBytes,
    long MaximumPixelCount,
    int MaximumDimension,
    long MaximumEncodedPreviewBytes,
    int MaximumDiagnosticCharacters,
    long MaximumEncodedDdsBytes = 536_870_912)
{
    public static DdsPreviewResourcePolicy Default { get; } = new(
        MaximumInputBytes: 536_870_912,
        MaximumPixelCount: 100_000_000,
        MaximumDimension: 16_384,
        MaximumEncodedPreviewBytes: 268_435_456,
        MaximumDiagnosticCharacters: 262_144);
}

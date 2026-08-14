namespace AuditionModStudio.Core.Dds;

public sealed record DdsMetadata(
    int Width,
    int Height,
    int? Depth,
    uint DeclaredMipMapCount,
    uint EffectiveMipLevelCount,
    DdsFormat Format,
    DdsFormatSupport FormatSupport,
    string? FourCC,
    uint? DxgiFormat,
    DdsHeaderType HeaderType,
    bool SupportsAlpha,
    bool HasAlphaChannel,
    DdsAlphaMode AlphaMode,
    DdsColorSpace ColorSpace,
    DdsResourceDimension ResourceDimension,
    bool IsCubemap,
    uint ArraySize,
    long FileLength,
    int PixelDataOffset);

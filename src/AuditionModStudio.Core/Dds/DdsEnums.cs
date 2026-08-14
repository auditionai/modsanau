namespace AuditionModStudio.Core.Dds;

public enum DdsFormat
{
    Unknown,
    BC1,
    BC2,
    BC3,
    BC4,
    BC5,
    BC6H,
    BC7,
    Rgba8,
    Bgra8,
    Uncompressed
}

public enum DdsFormatSupport
{
    Known,
    Unsupported,
    Unknown
}

public enum DdsHeaderType
{
    Legacy,
    Dx10
}

public enum DdsAlphaMode
{
    None,
    PossibleOneBit,
    Explicit,
    Interpolated,
    Channel,
    Unknown
}

public enum DdsColorSpace
{
    Unknown,
    Linear,
    Srgb
}

public enum DdsResourceDimension
{
    Unknown,
    Texture1D,
    Texture2D,
    Texture3D
}

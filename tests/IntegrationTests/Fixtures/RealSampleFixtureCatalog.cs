namespace IntegrationTests.Fixtures;

internal static class RealSampleFixtureCatalog
{
    public static RealSampleFixtureRegistration AcvTool { get; } = new(
        "acv-tool-5",
        "acv.exe",
        "acv.exe",
        RealSampleFixtureKind.Tool);

    public static RealSampleFixtureRegistration Archive015 { get; } = new(
        "archive-015",
        "015.ab",
        "015.ab",
        RealSampleFixtureKind.Archive);

    public static RealSampleFixtureRegistration CobyLogoTexture { get; } = new(
        "tn-coby-logo",
        "tn_coby_logo.dds",
        Path.Combine("samples", "private", "tn_coby_logo.dds"),
        RealSampleFixtureKind.Texture,
        new DdsFixtureMetadata(
            Width: 6000,
            Height: 1801,
            Format: "DXT5/BC3",
            MipLevels: 1));

    public static IReadOnlyList<RealSampleFixtureRegistration> All { get; } =
        Array.AsReadOnly([AcvTool, Archive015, CobyLogoTexture]);
}

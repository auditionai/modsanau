namespace IntegrationTests.Fixtures;

internal sealed record DdsFixtureMetadata(
    int Width,
    int Height,
    string Format,
    int MipLevels);

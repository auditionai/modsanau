namespace IntegrationTests.Fixtures;

internal sealed record RealSampleFixtureRegistration(
    string Id,
    string FileName,
    string RepositoryRelativePath,
    RealSampleFixtureKind Kind,
    DdsFixtureMetadata? DdsMetadata = null);

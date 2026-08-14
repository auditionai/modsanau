namespace IntegrationTests.Fixtures;

internal sealed record FixtureCopyResult(
    RealSampleFixtureRegistration Registration,
    string SourcePath,
    string WorkingCopyPath,
    string Sha256);

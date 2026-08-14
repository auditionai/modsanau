namespace AuditionModStudio.Core.Archives;

public sealed record AuditionArchiveTemplate
{
    public AuditionArchiveTemplate(
        string archiveId,
        string fileName,
        string sourceRelativePath,
        ArchiveEngineType engineType,
        string regionProfileId,
        string expectedExtractFolderName,
        string? templateVersion = null,
        string? sha256 = null,
        string? compatibleGameBuild = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRelativePath);
        ArgumentNullException.ThrowIfNull(engineType);
        ArgumentException.ThrowIfNullOrWhiteSpace(regionProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExtractFolderName);

        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(Path.GetExtension(fileName)))
        {
            throw new ArgumentException("Archive file name must be a simple file name with an extension.", nameof(fileName));
        }

        if (!string.Equals(Path.GetFileName(sourceRelativePath), fileName, StringComparison.Ordinal))
        {
            throw new ArgumentException("Archive source metadata must end with the exact archive file name.", nameof(sourceRelativePath));
        }

        TemplateId = new TemplateId(archiveId);
        FileName = fileName;
        SourceRelativePath = sourceRelativePath;
        EngineType = engineType;
        RegionProfileId = regionProfileId;
        ExpectedExtractFolderName = expectedExtractFolderName;
        TemplateVersion = templateVersion is null ? null : new TemplateVersion(templateVersion);
        ExpectedSha256 = sha256 is null ? null : new TemplateSha256(sha256);
        CompatibleGameBuild = compatibleGameBuild is null ? null : new CompatibleGameBuild(compatibleGameBuild);
    }

    public TemplateId TemplateId { get; }

    public string ArchiveId => TemplateId.Value;

    public string FileName { get; }

    public string SourceRelativePath { get; }

    public string BaseName => Path.GetFileNameWithoutExtension(FileName);

    public string Extension => Path.GetExtension(FileName);

    public ArchiveEngineType EngineType { get; }

    public string RegionProfileId { get; }

    public string ExpectedExtractFolderName { get; }

    public TemplateVersion? TemplateVersion { get; }

    public TemplateSha256? ExpectedSha256 { get; }

    public string? Sha256 => ExpectedSha256?.Value;

    public CompatibleGameBuild? CompatibleGameBuild { get; }

    public TemplateIdentity? Identity =>
        TemplateVersion is { } version
        && ExpectedSha256 is { } sha256
        && CompatibleGameBuild is { } compatibleGameBuild
            ? new(TemplateId, version, sha256, compatibleGameBuild)
            : null;
}

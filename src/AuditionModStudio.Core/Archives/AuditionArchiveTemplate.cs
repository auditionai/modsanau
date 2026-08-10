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
        string? sha256 = null)
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

        if (sha256 is not null
            && (sha256.Length != 64 || sha256.Any(character => !char.IsAsciiHexDigit(character))))
        {
            throw new ArgumentException("Archive SHA-256 must contain exactly 64 hexadecimal characters.", nameof(sha256));
        }

        ArchiveId = archiveId;
        FileName = fileName;
        SourceRelativePath = sourceRelativePath;
        EngineType = engineType;
        RegionProfileId = regionProfileId;
        ExpectedExtractFolderName = expectedExtractFolderName;
        TemplateVersion = templateVersion;
        Sha256 = sha256;
    }

    public string ArchiveId { get; }

    public string FileName { get; }

    public string SourceRelativePath { get; }

    public string BaseName => Path.GetFileNameWithoutExtension(FileName);

    public string Extension => Path.GetExtension(FileName);

    public ArchiveEngineType EngineType { get; }

    public string RegionProfileId { get; }

    public string ExpectedExtractFolderName { get; }

    public string? TemplateVersion { get; }

    public string? Sha256 { get; }
}

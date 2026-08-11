using AuditionModStudio.Core.Archives;

namespace AuditionModStudio.Core.Projects;

public sealed record ArchiveTemplateReference
{
    public ArchiveTemplateReference(
        string templateId,
        string? templateVersion,
        string sourceSha256,
        ArchiveEngineType engineType,
        string regionProfileId,
        string? compatibleGameBuild = null)
    {
        TemplateId = new(templateId);
        TemplateVersion = templateVersion is null ? null : new(templateVersion);
        SourceSha256 = new(sourceSha256);
        EngineType = engineType ?? throw new ArgumentNullException(nameof(engineType));
        ArgumentException.ThrowIfNullOrWhiteSpace(regionProfileId);
        RegionProfileId = regionProfileId;
        CompatibleGameBuild = compatibleGameBuild is null ? null : new(compatibleGameBuild);
    }

    public TemplateId TemplateId { get; }
    public TemplateVersion? TemplateVersion { get; }
    public TemplateSha256 SourceSha256 { get; }
    public ArchiveEngineType EngineType { get; }
    public string RegionProfileId { get; }
    public CompatibleGameBuild? CompatibleGameBuild { get; }

    public TemplateIdentity? Identity =>
        TemplateVersion is { } version && CompatibleGameBuild is { } build
            ? new(TemplateId, version, SourceSha256, build)
            : null;
}

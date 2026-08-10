using AuditionModStudio.Core.Archives;

namespace AuditionModStudio.Core.Projects;

public sealed record ArchiveTemplateReference(
    string TemplateId,
    string? TemplateVersion,
    string SourceSha256,
    ArchiveEngineType EngineType,
    string RegionProfileId);

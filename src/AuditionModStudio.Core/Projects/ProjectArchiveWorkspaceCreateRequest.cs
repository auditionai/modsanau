using AuditionModStudio.Core.Archives;

namespace AuditionModStudio.Core.Projects;

public sealed record ProjectArchiveWorkspaceCreateRequest(
    string DisplayName,
    AuditionArchiveTemplate ArchiveTemplate,
    PristineArchiveSource PristineSource,
    Guid? ProjectId = null);

using AuditionModStudio.Core.Archives;

namespace AuditionModStudio.Core.Projects;

public interface IProjectArchiveWorkspace : IAsyncDisposable
{
    ProjectArchiveWorkspaceDescriptor Descriptor { get; }

    ArchiveWorkspace ArchiveWorkspace { get; }
}

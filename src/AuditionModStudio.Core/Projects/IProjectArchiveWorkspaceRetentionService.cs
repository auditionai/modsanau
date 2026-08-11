namespace AuditionModStudio.Core.Projects;

public interface IProjectArchiveWorkspaceRetentionService
{
    void Retain(IProjectArchiveWorkspace workspace);
}

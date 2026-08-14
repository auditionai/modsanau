namespace AuditionModStudio.Core.Projects;

public interface IProjectArchiveWorkspaceService
{
    Task<ProjectArchiveWorkspaceCreateResult> CreateAsync(
        ProjectArchiveWorkspaceCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<ProjectArchiveWorkspaceValidationResult> ValidateAsync(
        IProjectArchiveWorkspace workspace,
        CancellationToken cancellationToken = default);
}

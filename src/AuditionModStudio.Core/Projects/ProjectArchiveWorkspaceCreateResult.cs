namespace AuditionModStudio.Core.Projects;

public sealed record ProjectArchiveWorkspaceCreateResult(
    bool Succeeded,
    IProjectArchiveWorkspace? Workspace,
    ProjectArchiveWorkspaceFailureReason FailureReason,
    string? DiagnosticCode)
{
    public static ProjectArchiveWorkspaceCreateResult Success(IProjectArchiveWorkspace workspace) =>
        new(true, workspace, ProjectArchiveWorkspaceFailureReason.None, null);

    public static ProjectArchiveWorkspaceCreateResult Failure(
        ProjectArchiveWorkspaceFailureReason reason,
        string diagnosticCode) => new(false, null, reason, diagnosticCode);
}

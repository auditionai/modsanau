namespace AuditionModStudio.Core.Projects;

public sealed record ProjectArchiveWorkspaceValidationResult(
    bool IsValid,
    ProjectArchiveWorkspaceValidationStatus Status,
    string? DiagnosticCode)
{
    public static ProjectArchiveWorkspaceValidationResult Valid() =>
        new(true, ProjectArchiveWorkspaceValidationStatus.Valid, null);

    public static ProjectArchiveWorkspaceValidationResult Invalid(
        ProjectArchiveWorkspaceValidationStatus status,
        string diagnosticCode) => new(false, status, diagnosticCode);
}

using System.Collections.Immutable;
using AuditionModStudio.Core.Mods;

namespace AuditionModStudio.Core.Projects;

public enum ProjectValidationSeverity
{
    Error,
    Warning,
    Info
}

public enum ProjectValidationIssueKind
{
    MissingArchive,
    MissingFolder,
    MissingFile,
    InvalidPath,
    WrongFilename,
    WrongDimensions,
    WrongDdsFormat,
    MalformedDds,
    PendingEdit,
    ToolIntegrityIssue,
    MetadataBaselineUnavailable,
    ValidatedTexture
}

public sealed record ProjectValidationIssue(
    ProjectValidationSeverity Severity,
    ProjectValidationIssueKind Kind,
    string DiagnosticCode,
    ModRelativePath? RelativePath = null);

public sealed record ProjectValidationRequest(
    AuditionProject Project,
    IProjectArchiveWorkspace Workspace);

public sealed record ProjectValidationResult(
    bool Completed,
    bool Cancelled,
    ImmutableArray<ProjectValidationIssue> Issues)
{
    public bool CanBuild => Completed
                            && !Cancelled
                            && Issues.All(issue => issue.Severity != ProjectValidationSeverity.Error);

    public int ErrorCount => Issues.Count(issue => issue.Severity == ProjectValidationSeverity.Error);
    public int WarningCount => Issues.Count(issue => issue.Severity == ProjectValidationSeverity.Warning);
    public int InfoCount => Issues.Count(issue => issue.Severity == ProjectValidationSeverity.Info);

    public static ProjectValidationResult Success(IEnumerable<ProjectValidationIssue> issues) =>
        new(true, false, issues.ToImmutableArray());

    public static ProjectValidationResult CancelledResult(IEnumerable<ProjectValidationIssue> issues) =>
        new(false, true, issues.ToImmutableArray());
}

public interface IProjectValidator
{
    Task<ProjectValidationResult> ValidateAsync(
        ProjectValidationRequest request,
        CancellationToken cancellationToken = default);
}

public enum ProjectToolIntegrityStatus
{
    Valid,
    Missing,
    Invalid,
    Unavailable,
    Cancelled
}

public sealed record ProjectToolIntegrityResult(
    ProjectToolIntegrityStatus Status,
    string DiagnosticCode)
{
    public bool IsValid => Status == ProjectToolIntegrityStatus.Valid;
}

public interface IProjectToolIntegrityValidator
{
    Task<ProjectToolIntegrityResult> ValidateAsync(CancellationToken cancellationToken = default);
}

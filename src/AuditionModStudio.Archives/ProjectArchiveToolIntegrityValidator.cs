using AuditionModStudio.Core.Projects;

namespace AuditionModStudio.Archives;

public sealed record ProjectArchiveToolIntegrityOptions(string TrustedRootDirectory, string ExecutableFileName)
{
    public static ProjectArchiveToolIntegrityOptions CreateProduction(string applicationBaseDirectory) =>
        new(Path.GetFullPath(applicationBaseDirectory), "acv.exe");
}

public sealed class ProjectArchiveToolIntegrityValidator(
    ArchiveToolIntegrityPolicy integrityPolicy,
    ProjectArchiveToolIntegrityOptions options) : IProjectToolIntegrityValidator
{
    public async Task<ProjectToolIntegrityResult> ValidateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var executable = Path.Combine(options.TrustedRootDirectory, options.ExecutableFileName);
            var result = await integrityPolicy.VerifyAsync(
                ArchiveToolIds.AcvTool5,
                executable,
                options.TrustedRootDirectory,
                cancellationToken).ConfigureAwait(false);
            return result.Approved
                ? new(ProjectToolIntegrityStatus.Valid, "PROJECT_TOOL_INTEGRITY_VALID")
                : new(Map(result.FailureReason), "PROJECT_TOOL_INTEGRITY_INVALID");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(ProjectToolIntegrityStatus.Cancelled, "PROJECT_TOOL_INTEGRITY_CANCELLED");
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException
                                          or ArgumentException)
        {
            return new(ProjectToolIntegrityStatus.Unavailable, "PROJECT_TOOL_INTEGRITY_UNAVAILABLE");
        }
    }

    private static ProjectToolIntegrityStatus Map(ArchiveToolIntegrityFailureReason reason) => reason switch
    {
        ArchiveToolIntegrityFailureReason.ExecutableMissing => ProjectToolIntegrityStatus.Missing,
        ArchiveToolIntegrityFailureReason.HashMismatch
            or ArchiveToolIntegrityFailureReason.FilenameMismatch
            or ArchiveToolIntegrityFailureReason.UnapprovedTool
            or ArchiveToolIntegrityFailureReason.InvalidManifest
            or ArchiveToolIntegrityFailureReason.UnexpectedCompanion => ProjectToolIntegrityStatus.Invalid,
        _ => ProjectToolIntegrityStatus.Unavailable,
    };
}

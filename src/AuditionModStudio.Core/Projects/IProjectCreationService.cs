using System.Collections.Immutable;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;

namespace AuditionModStudio.Core.Projects;

public interface IProjectCreationService
{
    Task<ProjectCreationResult> CreateAsync(
        ProjectCreationRequest request,
        IProgress<ProjectCreationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record ProjectCreationRequest(GameId GameId, ModId ModId, string ProjectName);

public enum ProjectCreationPhase
{
    ValidatingSelection,
    CheckingEntitlement,
    AcquiringTemplate,
    CreatingWorkspace,
    PreparingKeydat,
    ExtractingArchive,
    ScanningTextures,
    CachingMetadata,
    SavingProject,
    Completed
}

public sealed record ProjectCreationProgress(ProjectCreationPhase Phase, int CompletedSteps, int TotalSteps);

public enum ProjectCreationFailureReason
{
    None,
    InvalidRequest,
    UnknownGame,
    UnknownMod,
    EntitlementDenied,
    EntitlementUnavailable,
    TemplateAcquisitionFailed,
    RegionProfileMissing,
    WorkspaceCreationFailed,
    KeydatPolicyInvalid,
    ArchiveExtractionFailed,
    TextureScanFailed,
    MetadataCacheFailed,
    ProjectSaveFailed,
    RollbackFailed,
    Cancelled
}

public sealed record ProjectCreationResult(
    bool Succeeded,
    ProjectCreationFailureReason FailureReason,
    string? DiagnosticCode,
    AuditionProject? Project,
    IProjectArchiveWorkspace? Workspace)
{
    public static ProjectCreationResult Success(AuditionProject project, IProjectArchiveWorkspace workspace) =>
        new(true, ProjectCreationFailureReason.None, null, project, workspace);

    public static ProjectCreationResult Failure(ProjectCreationFailureReason reason, string code) =>
        new(false, reason, code, null, null);
}

public interface ITemplateEntitlementService
{
    Task<TemplateEntitlementResult> CheckAsync(
        TemplateIdentity identity,
        CancellationToken cancellationToken = default);
}

public enum TemplateEntitlementStatus
{
    NotRequired,
    Granted,
    Denied,
    Unavailable
}

public sealed record TemplateEntitlementResult(TemplateEntitlementStatus Status, string? DiagnosticCode);

public interface IProjectTemplateAcquisitionService
{
    Task<ProjectTemplateAcquisitionResult> AcquireAsync(
        AuditionArchiveTemplate template,
        CancellationToken cancellationToken = default);
}

public sealed record ProjectTemplateAcquisitionResult(
    bool Succeeded,
    PristineArchiveSource? Source,
    string? DiagnosticCode)
{
    public static ProjectTemplateAcquisitionResult Success(PristineArchiveSource source) => new(true, source, null);
    public static ProjectTemplateAcquisitionResult Failure(string code) => new(false, null, code);
}

public interface IAuditionProjectStore
{
    Task<AuditionProjectStoreResult> SaveAsync(
        AuditionProject project,
        CancellationToken cancellationToken = default);

    Task<AuditionProjectStoreResult> DeleteAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<AuditionProjectLoadResult> LoadAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);
}

public enum AuditionProjectStoreFailureReason
{
    None,
    InvalidProject,
    IoFailure,
    Cancelled
}

public sealed record AuditionProjectStoreResult(
    bool Succeeded,
    AuditionProjectStoreFailureReason FailureReason,
    string? DiagnosticCode,
    string? ProjectFileName)
{
    public static AuditionProjectStoreResult Success(string projectFileName) =>
        new(true, AuditionProjectStoreFailureReason.None, null, projectFileName);

    public static AuditionProjectStoreResult Failure(AuditionProjectStoreFailureReason reason, string code) =>
        new(false, reason, code, null);
}

public enum AuditionProjectLoadFailureReason
{
    None,
    InvalidProjectId,
    Missing,
    Corrupt,
    UnsupportedSchema,
    InvalidProject,
    IoFailure,
    Cancelled
}

public sealed record AuditionProjectLoadResult(
    bool Succeeded,
    AuditionProjectLoadFailureReason FailureReason,
    string? DiagnosticCode,
    AuditionProject? Project)
{
    public static AuditionProjectLoadResult Success(AuditionProject project) =>
        new(true, AuditionProjectLoadFailureReason.None, null, project);

    public static AuditionProjectLoadResult Failure(AuditionProjectLoadFailureReason reason, string code) =>
        new(false, reason, code, null);
}

public sealed record ProjectTextureMetadataSnapshot(
    ModRelativePath RelativePath,
    Sha256Digest SourceSha256,
    Dds.DdsMetadata Metadata,
    TextureSlotId? ManifestSlotId);

public interface IProjectMetadataCache
{
    Task<ProjectMetadataCacheResult> StoreAsync(
        Guid projectId,
        IEnumerable<ProjectTextureMetadataSnapshot> textures,
        CancellationToken cancellationToken = default);

    Task<ProjectMetadataCacheResult> DeleteAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<ProjectMetadataCacheValidationResult> ValidateAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<ProjectMetadataCacheLoadResult> LoadAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ProjectMetadataCacheLoadResult.Failure(
            ProjectMetadataCacheLoadStatus.Missing,
            "PROJECT_METADATA_MISSING"));
}

public enum ProjectMetadataCacheFailureReason
{
    None,
    InvalidData,
    IoFailure,
    Cancelled
}

public sealed record ProjectMetadataCacheResult(
    bool Succeeded,
    ProjectMetadataCacheFailureReason FailureReason,
    string? DiagnosticCode)
{
    public static ProjectMetadataCacheResult Success() => new(true, ProjectMetadataCacheFailureReason.None, null);
    public static ProjectMetadataCacheResult Failure(ProjectMetadataCacheFailureReason reason, string code) =>
        new(false, reason, code);
}

public enum ProjectMetadataCacheValidationStatus
{
    Valid,
    Missing,
    Corrupt,
    InvalidProjectId,
    Cancelled
}

public sealed record ProjectMetadataCacheValidationResult(
    ProjectMetadataCacheValidationStatus Status,
    string? DiagnosticCode)
{
    public bool IsValid => Status == ProjectMetadataCacheValidationStatus.Valid;
}

public enum ProjectMetadataCacheLoadStatus
{
    Success,
    Missing,
    Corrupt,
    InvalidProjectId,
    Cancelled
}

public sealed record ProjectMetadataCacheLoadResult(
    ProjectMetadataCacheLoadStatus Status,
    string? DiagnosticCode,
    ImmutableArray<ProjectTextureMetadataSnapshot> Textures)
{
    public bool Succeeded => Status == ProjectMetadataCacheLoadStatus.Success;

    public static ProjectMetadataCacheLoadResult Success(
        IEnumerable<ProjectTextureMetadataSnapshot> textures) =>
        new(ProjectMetadataCacheLoadStatus.Success, null, textures.ToImmutableArray());

    public static ProjectMetadataCacheLoadResult Failure(
        ProjectMetadataCacheLoadStatus status,
        string code) => new(status, code, []);
}

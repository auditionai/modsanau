using System.Collections.Immutable;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;

namespace AuditionModStudio.Archives;

public sealed record TemplateAdminPrincipal(Guid SubjectId);

public sealed record TemplateAdminTextureLabel(
    TextureSlotId SlotId,
    ModRelativePath RelativePath,
    string DisplayName,
    TextureCategory Category,
    string Description,
    ImmutableArray<string> Tags,
    bool PreviewEnabled,
    bool Editable,
    TextureEditMode RecommendedEditMode);

public sealed record TemplateAdminRequest(
    TemplateAdminPrincipal Principal,
    string ArchivePath,
    GameId GameId,
    ModId ModId,
    TemplateId TemplateId,
    TemplateVersion Version,
    CompatibleGameBuild CompatibleGameBuild,
    ArchiveEngineType EngineType,
    string RegionProfileId,
    string ExpectedExtractFolderName,
    ImmutableArray<TemplateAdminTextureLabel> Labels);

public enum TemplateAdminPhase
{
    Authorizing, Validating, PreparingWorkspace, Extracting, Scanning, ReadingDds,
    Publishing, Completed, Failed, Cancelled
}

public sealed record TemplateAdminProgress(TemplateAdminPhase Phase, int CompletedItems, int TotalItems);

public enum TemplateAdminStatus
{
    Succeeded, Unauthorized, InvalidRequest, ArchiveRejected, ExtractFailed, ScanFailed,
    DdsValidationFailed, LabelValidationFailed, VersionConflict, PublishFailed, Cancelled
}

public sealed record TemplateAdminDdsRecord(ModRelativePath RelativePath, Sha256Digest ContentSha256,
    int Width, int Height, DdsFormat Format, uint MipLevels, bool HasAlphaChannel, DdsHeaderType HeaderType);

public sealed record TemplateAdminAuditEvent(Guid OperationId, Guid AdminSubjectId, TemplateIdentity Identity,
    GameId GameId, ModId ModId, int DdsCount, DateTimeOffset OccurredAt, string Action);

public enum TemplateAdminStorageEncryption { ServerManaged }

public sealed record TemplateAdminPublishRequest(
    string SourceArchivePath,
    PremiumTemplatePackageManifest PackageManifest,
    TextureManifest TextureManifest,
    ImmutableArray<TemplateAdminDdsRecord> DdsRecords,
    TemplateAdminAuditEvent AuditEvent,
    TemplateAdminStorageEncryption RequiredEncryption);

public sealed record TemplateAdminPublishResult(bool Succeeded, string DiagnosticCode,
    TemplateIdentity? PublishedIdentity, TemplateAdminStorageEncryption? Encryption,
    ImmutableArray<byte> MetadataSignature, Guid? AuditEventId, bool VersionConflict = false);

public sealed record TemplateAdminResult(TemplateAdminStatus Status, string DiagnosticCode,
    TemplateIdentity? Identity, TextureManifest? TextureManifest,
    ImmutableArray<TemplateAdminDdsRecord> DdsRecords, Guid? AuditEventId)
{
    public bool Succeeded => Status == TemplateAdminStatus.Succeeded && Identity is not null;
}

public interface ITemplateAdminAuthorizer
{
    Task<bool> IsTemplateAdminAsync(TemplateAdminPrincipal principal,
        CancellationToken cancellationToken = default);
}

public interface ITemplateAdminPublisher
{
    Task<TemplateAdminPublishResult> PublishAtomicallyAsync(TemplateAdminPublishRequest request,
        CancellationToken cancellationToken = default);
}

public interface ITemplateAdminWorkflow
{
    Task<TemplateAdminResult> ExecuteAsync(TemplateAdminRequest request,
        IProgress<TemplateAdminProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableTemplateAdminAuthorizer : ITemplateAdminAuthorizer
{
    public Task<bool> IsTemplateAdminAsync(TemplateAdminPrincipal principal,
        CancellationToken cancellationToken = default) => Task.FromResult(false);
}

public sealed class UnavailableTemplateAdminPublisher : ITemplateAdminPublisher
{
    public Task<TemplateAdminPublishResult> PublishAtomicallyAsync(TemplateAdminPublishRequest request,
        CancellationToken cancellationToken = default) => Task.FromResult(new TemplateAdminPublishResult(false,
        cancellationToken.IsCancellationRequested ? "TEMPLATE_ADMIN_CANCELLED" : "TEMPLATE_ADMIN_PUBLISH_UNAVAILABLE",
        null, null, [], null));
}

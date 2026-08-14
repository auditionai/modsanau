using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Mods;

namespace AuditionModStudio.Core.Projects;

public enum TextureApplyPhase
{
    ValidatingTarget,
    Resizing,
    Encoding,
    ValidatingOutput,
    Replacing,
    UpdatingHistory,
    RegeneratingThumbnail,
    SavingProject
}

public sealed record TextureApplyProgress(TextureApplyPhase Phase, int CompletedSteps, int TotalSteps);

public sealed record TextureApplyRequest(
    AuditionProject Project,
    IProjectArchiveWorkspace Workspace,
    ModRelativePath TextureRelativePath,
    ImageResizeRequest ResizeRequest,
    int ThumbnailMaximumDimension = 192);

public enum TextureApplyFailureReason
{
    None,
    InvalidRequest,
    WorkspaceMismatch,
    TargetInvalid,
    ResizeFailed,
    EncodeFailed,
    ValidationFailed,
    ReplaceFailed,
    ProjectUpdateFailed,
    StateUpdateFailed,
    ThumbnailFailed,
    SaveFailed,
    RollbackFailed,
    Cancelled
}

public sealed record TextureApplyResult(
    bool Succeeded,
    bool Cancelled,
    TextureApplyFailureReason FailureReason,
    string? DiagnosticCode,
    AuditionProject? Project,
    InternalImage? Thumbnail,
    TextureState State,
    bool CleanupPending)
{
    public static TextureApplyResult Success(
        AuditionProject project,
        InternalImage thumbnail,
        TextureState state,
        bool cleanupPending) =>
        new(true, false, TextureApplyFailureReason.None, null, project, thumbnail, state, cleanupPending);

    public static TextureApplyResult Failure(TextureApplyFailureReason reason, string code) =>
        new(false, reason == TextureApplyFailureReason.Cancelled, reason, code, null, null, default, false);
}

public interface ITextureApplyService
{
    Task<TextureApplyResult> ApplyAsync(
        TextureApplyRequest request,
        IProgress<TextureApplyProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

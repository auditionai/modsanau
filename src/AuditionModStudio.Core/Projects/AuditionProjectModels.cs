using System.Collections.Immutable;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Mods;

namespace AuditionModStudio.Core.Projects;

public readonly record struct ProjectAssetId
{
    public const int MaximumLength = 128;

    public ProjectAssetId(string value)
    {
        if (!IsValidValue(value))
        {
            throw new ArgumentException("Project asset IDs must be bounded lowercase ASCII stable identifiers.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }
    public bool IsValid => IsValidValue(Value);
    public override string ToString() => Value ?? string.Empty;

    private static bool IsValidValue(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaximumLength
        && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
}

public sealed record ProjectWorkspaceReference(
    string WorkspaceId,
    ModRelativePath WorkingArchiveRelativePath,
    ModRelativePath ExtractedRootRelativePath);

public sealed record ProjectEditedTextureRecord(
    ModRelativePath RelativePath,
    Sha256Digest OriginalSha256,
    Sha256Digest CurrentSha256,
    ProjectAssetId CurrentImageAssetId,
    long Revision);

public sealed record ProjectAssetRecord(
    ProjectAssetId Id,
    ModRelativePath RelativePath,
    Sha256Digest Sha256);

public sealed record ProjectEditHistoryRecord(
    long Revision,
    ModRelativePath TextureRelativePath,
    EditOperationKind Operation,
    ProjectAssetId BeforeImageAssetId,
    ProjectAssetId AfterImageAssetId);

public sealed record ProjectEditStateSnapshot(
    long CurrentRevision,
    long SavedRevision,
    ModRelativePath? ActiveTextureRelativePath,
    ImmutableArray<ProjectEditHistoryRecord> History);

public enum ProjectBuildStatus
{
    NotBuilt,
    Dirty,
    Succeeded,
    Failed
}

public sealed record ProjectBuildStateSnapshot(
    ProjectBuildStatus Status,
    DateTimeOffset? LastBuildAt,
    ModRelativePath? OutputArchiveRelativePath,
    Sha256Digest? OutputSha256);

public sealed class AuditionProject
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumNameLength = 128;

    private AuditionProject(
        int schemaVersion,
        Guid projectId,
        string name,
        GameId gameId,
        ModId modId,
        TemplateIdentity templateIdentity,
        ProjectWorkspaceReference workspace,
        ImmutableArray<ProjectEditedTextureRecord> editedTextures,
        ImmutableArray<ProjectAssetRecord> imageAssets,
        ImmutableArray<ProjectAssetRecord> aiAssets,
        ProjectEditStateSnapshot editState,
        ProjectBuildStateSnapshot buildState,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        SchemaVersion = schemaVersion;
        ProjectId = projectId;
        Name = name;
        GameId = gameId;
        ModId = modId;
        TemplateIdentity = templateIdentity;
        Workspace = workspace;
        EditedTextures = editedTextures;
        ImageAssets = imageAssets;
        AiAssets = aiAssets;
        EditState = editState;
        BuildState = buildState;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public int SchemaVersion { get; }
    public Guid ProjectId { get; }
    public string Name { get; }
    public GameId GameId { get; }
    public ModId ModId { get; }
    public TemplateIdentity TemplateIdentity { get; }
    public ProjectWorkspaceReference Workspace { get; }
    public ImmutableArray<ProjectEditedTextureRecord> EditedTextures { get; }
    public ImmutableArray<ProjectAssetRecord> ImageAssets { get; }
    public ImmutableArray<ProjectAssetRecord> AiAssets { get; }
    public ProjectEditStateSnapshot EditState { get; }
    public ProjectBuildStateSnapshot BuildState { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; }

    public static AuditionProjectCreateResult Create(
        int schemaVersion,
        Guid projectId,
        string? name,
        GameId gameId,
        ModId modId,
        TemplateIdentity? templateIdentity,
        ProjectWorkspaceReference? workspace,
        IEnumerable<ProjectEditedTextureRecord?>? editedTextures,
        IEnumerable<ProjectAssetRecord?>? imageAssets,
        IEnumerable<ProjectAssetRecord?>? aiAssets,
        ProjectEditStateSnapshot? editState,
        ProjectBuildStateSnapshot? buildState,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        var issues = ImmutableArray.CreateBuilder<AuditionProjectValidationIssue>();
        ValidateHeader(schemaVersion, projectId, name, gameId, modId, templateIdentity, workspace, createdAt, updatedAt, issues);

        var textures = Materialize(editedTextures, "AUDPROJ_EDITED_TEXTURE_NULL", issues);
        var images = Materialize(imageAssets, "AUDPROJ_IMAGE_ASSET_NULL", issues);
        var ai = Materialize(aiAssets, "AUDPROJ_AI_ASSET_NULL", issues);
        ValidateWorkspace(workspace, issues);
        ValidateEditedTextures(textures, issues);
        ValidateAssets(images, ai, issues);
        ValidateEditState(editState, textures, images, ai, issues);
        ValidateBuildState(buildState, issues);

        if (issues.Count > 0)
        {
            return AuditionProjectCreateResult.Failure(issues.ToImmutable());
        }

        return AuditionProjectCreateResult.Success(new AuditionProject(
            schemaVersion,
            projectId,
            name!,
            gameId,
            modId,
            templateIdentity!,
            workspace!,
            textures.OrderBy(item => item.RelativePath.Value, StringComparer.Ordinal).ToImmutableArray(),
            images.OrderBy(item => item.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            ai.OrderBy(item => item.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            editState! with { History = editState!.History.OrderBy(item => item.Revision).ToImmutableArray() },
            buildState!,
            createdAt,
            updatedAt));
    }

    private static void ValidateHeader(
        int schemaVersion,
        Guid projectId,
        string? name,
        GameId gameId,
        ModId modId,
        TemplateIdentity? templateIdentity,
        ProjectWorkspaceReference? workspace,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        ImmutableArray<AuditionProjectValidationIssue>.Builder issues)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            Add(issues, AuditionProjectValidationFailureReason.UnsupportedSchema, "AUDPROJ_SCHEMA_UNSUPPORTED");
        }

        if (projectId == Guid.Empty)
        {
            Add(issues, AuditionProjectValidationFailureReason.InvalidProjectId, "AUDPROJ_PROJECT_ID_INVALID");
        }

        if (string.IsNullOrWhiteSpace(name) || name.Length > MaximumNameLength || name.Any(char.IsControl))
        {
            Add(issues, AuditionProjectValidationFailureReason.InvalidName, "AUDPROJ_NAME_INVALID");
        }

        if (!gameId.IsValid)
        {
            Add(issues, AuditionProjectValidationFailureReason.InvalidGameId, "AUDPROJ_GAME_ID_INVALID");
        }

        if (!modId.IsValid)
        {
            Add(issues, AuditionProjectValidationFailureReason.InvalidModId, "AUDPROJ_MOD_ID_INVALID");
        }

        if (templateIdentity is not { IsValid: true })
        {
            Add(issues, AuditionProjectValidationFailureReason.InvalidTemplateIdentity, "AUDPROJ_TEMPLATE_IDENTITY_INVALID");
        }

        if (workspace is null)
        {
            Add(issues, AuditionProjectValidationFailureReason.InvalidWorkspaceReference, "AUDPROJ_WORKSPACE_MISSING");
        }

        if (createdAt == default || updatedAt < createdAt)
        {
            Add(issues, AuditionProjectValidationFailureReason.InvalidTimestamps, "AUDPROJ_TIMESTAMPS_INVALID");
        }
    }

    private static List<T> Materialize<T>(
        IEnumerable<T?>? source,
        string nullCode,
        ImmutableArray<AuditionProjectValidationIssue>.Builder issues) where T : class
    {
        if (source is null)
        {
            Add(issues, AuditionProjectValidationFailureReason.MissingCollection, "AUDPROJ_COLLECTION_MISSING");
            return [];
        }

        var result = new List<T>();
        foreach (var item in source)
        {
            if (item is null)
            {
                Add(issues, AuditionProjectValidationFailureReason.InvalidRecord, nullCode);
            }
            else
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static void ValidateWorkspace(
        ProjectWorkspaceReference? workspace,
        ImmutableArray<AuditionProjectValidationIssue>.Builder issues)
    {
        if (workspace is null)
        {
            return;
        }

        if (workspace.WorkspaceId.Length != 32 || workspace.WorkspaceId.Any(character => !char.IsAsciiHexDigit(character)))
        {
            Add(issues, AuditionProjectValidationFailureReason.InvalidWorkspaceReference, "AUDPROJ_WORKSPACE_ID_INVALID");
        }

        if (!workspace.WorkingArchiveRelativePath.IsValid || !workspace.ExtractedRootRelativePath.IsValid)
        {
            Add(issues, AuditionProjectValidationFailureReason.InvalidWorkspaceReference, "AUDPROJ_WORKSPACE_PATH_INVALID");
        }
    }

    private static void ValidateEditedTextures(
        IReadOnlyCollection<ProjectEditedTextureRecord> textures,
        ImmutableArray<AuditionProjectValidationIssue>.Builder issues)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var texture in textures)
        {
            if (!texture.RelativePath.IsValid || !texture.OriginalSha256.IsValid || !texture.CurrentSha256.IsValid
                || !texture.CurrentImageAssetId.IsValid || texture.Revision < 0)
            {
                Add(issues, AuditionProjectValidationFailureReason.InvalidRecord, "AUDPROJ_EDITED_TEXTURE_INVALID");
            }

            if (!paths.Add(texture.RelativePath.Value))
            {
                Add(issues, AuditionProjectValidationFailureReason.DuplicateIdentity, "AUDPROJ_TEXTURE_DUPLICATE");
            }
        }
    }

    private static void ValidateAssets(
        IReadOnlyCollection<ProjectAssetRecord> images,
        IReadOnlyCollection<ProjectAssetRecord> ai,
        ImmutableArray<AuditionProjectValidationIssue>.Builder issues)
    {
        var ids = new HashSet<ProjectAssetId>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in images.Concat(ai))
        {
            if (!asset.Id.IsValid || !asset.RelativePath.IsValid || !asset.Sha256.IsValid)
            {
                Add(issues, AuditionProjectValidationFailureReason.InvalidRecord, "AUDPROJ_ASSET_INVALID");
            }

            if (!ids.Add(asset.Id) || !paths.Add(asset.RelativePath.Value))
            {
                Add(issues, AuditionProjectValidationFailureReason.DuplicateIdentity, "AUDPROJ_ASSET_DUPLICATE");
            }
        }
    }

    private static void ValidateEditState(
        ProjectEditStateSnapshot? state,
        IReadOnlyCollection<ProjectEditedTextureRecord> textures,
        IReadOnlyCollection<ProjectAssetRecord> images,
        IReadOnlyCollection<ProjectAssetRecord> ai,
        ImmutableArray<AuditionProjectValidationIssue>.Builder issues)
    {
        if (state is null)
        {
            Add(issues, AuditionProjectValidationFailureReason.InvalidEditState, "AUDPROJ_EDIT_STATE_MISSING");
            return;
        }

        if (state.History.IsDefault || state.CurrentRevision < 0 || state.SavedRevision < 0
            || state.SavedRevision > state.CurrentRevision
            || textures.Any(item => item.Revision > state.CurrentRevision)
            || state.History.Any(item => item.Revision <= 0 || item.Revision > state.CurrentRevision || !Enum.IsDefined(item.Operation)))
        {
            Add(issues, AuditionProjectValidationFailureReason.InvalidEditState, "AUDPROJ_EDIT_STATE_INVALID");
        }

        if (state.History.Select(item => item.Revision).Distinct().Count() != state.History.Length)
        {
            Add(issues, AuditionProjectValidationFailureReason.DuplicateIdentity, "AUDPROJ_HISTORY_REVISION_DUPLICATE");
        }

        var texturePaths = textures.Select(item => item.RelativePath.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (state.ActiveTextureRelativePath is { } active && !texturePaths.Contains(active.Value))
        {
            Add(issues, AuditionProjectValidationFailureReason.DanglingReference, "AUDPROJ_ACTIVE_TEXTURE_MISSING");
        }

        var assetIds = images.Concat(ai).Select(item => item.Id).ToHashSet();
        foreach (var item in state.History)
        {
            if (!texturePaths.Contains(item.TextureRelativePath.Value)
                || !assetIds.Contains(item.BeforeImageAssetId)
                || !assetIds.Contains(item.AfterImageAssetId))
            {
                Add(issues, AuditionProjectValidationFailureReason.DanglingReference, "AUDPROJ_HISTORY_REFERENCE_MISSING");
            }
        }

        foreach (var texture in textures.Where(texture => !assetIds.Contains(texture.CurrentImageAssetId)))
        {
            Add(issues, AuditionProjectValidationFailureReason.DanglingReference, "AUDPROJ_TEXTURE_ASSET_MISSING");
        }
    }

    private static void ValidateBuildState(
        ProjectBuildStateSnapshot? state,
        ImmutableArray<AuditionProjectValidationIssue>.Builder issues)
    {
        if (state is null || !Enum.IsDefined(state.Status))
        {
            Add(issues, AuditionProjectValidationFailureReason.InvalidBuildState, "AUDPROJ_BUILD_STATE_INVALID");
            return;
        }

        var hasArtifact = state.OutputArchiveRelativePath is { IsValid: true } && state.OutputSha256 is { IsValid: true };
        if ((state.Status == ProjectBuildStatus.Succeeded && (state.LastBuildAt is null || !hasArtifact))
            || (state.Status != ProjectBuildStatus.Succeeded
                && (state.OutputArchiveRelativePath is not null || state.OutputSha256 is not null)))
        {
            Add(issues, AuditionProjectValidationFailureReason.InvalidBuildState, "AUDPROJ_BUILD_STATE_INCONSISTENT");
        }
    }

    private static void Add(
        ImmutableArray<AuditionProjectValidationIssue>.Builder issues,
        AuditionProjectValidationFailureReason reason,
        string code) => issues.Add(new(reason, code));
}

public enum AuditionProjectValidationFailureReason
{
    UnsupportedSchema,
    InvalidProjectId,
    InvalidName,
    InvalidGameId,
    InvalidModId,
    InvalidTemplateIdentity,
    InvalidWorkspaceReference,
    MissingCollection,
    InvalidRecord,
    DuplicateIdentity,
    DanglingReference,
    InvalidEditState,
    InvalidBuildState,
    InvalidTimestamps
}

public sealed record AuditionProjectValidationIssue(
    AuditionProjectValidationFailureReason Reason,
    string DiagnosticCode);

public sealed record AuditionProjectCreateResult(
    bool Succeeded,
    ImmutableArray<AuditionProjectValidationIssue> Issues,
    AuditionProject? Project)
{
    public static AuditionProjectCreateResult Success(AuditionProject project) => new(true, [], project);
    public static AuditionProjectCreateResult Failure(ImmutableArray<AuditionProjectValidationIssue> issues) =>
        new(false, issues, null);
}

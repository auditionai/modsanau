using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;

namespace Core.Tests;

public sealed class AuditionProjectModelsTests
{
    [Fact]
    public void Valid_model_stores_complete_plan_31_snapshot()
    {
        var result = Create();

        Assert.True(result.Succeeded);
        var project = Assert.IsType<AuditionProject>(result.Project);
        Assert.Equal(AuditionProject.CurrentSchemaVersion, project.SchemaVersion);
        Assert.Equal(new GameId("audition"), project.GameId);
        Assert.Equal(new ModId("login_mod"), project.ModId);
        Assert.Equal("1", project.TemplateIdentity.Version.Value);
        Assert.Equal("Working/015.ab", project.Workspace.WorkingArchiveRelativePath.Value);
        Assert.Single(project.EditedTextures);
        Assert.Equal(2, project.ImageAssets.Length);
        Assert.Single(project.AiAssets);
        Assert.Single(project.EditState.History);
        Assert.Equal(ProjectBuildStatus.NotBuilt, project.BuildState.Status);
        Assert.True(project.UpdatedAt >= project.CreatedAt);
    }

    [Theory]
    [InlineData(0, AuditionProjectValidationFailureReason.UnsupportedSchema)]
    [InlineData(2, AuditionProjectValidationFailureReason.UnsupportedSchema)]
    public void Unsupported_schema_is_rejected(int schema, AuditionProjectValidationFailureReason reason)
    {
        var result = Create(schemaVersion: schema);
        Assert.Contains(result.Issues, issue => issue.Reason == reason);
    }

    [Fact]
    public void Invalid_identity_and_timestamps_are_structured()
    {
        var result = Create(
            projectId: Guid.Empty,
            gameId: new GameId(),
            modId: new ModId(),
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow.AddDays(-1));

        Assert.False(result.Succeeded);
        Assert.Null(result.Project);
        Assert.Contains(result.Issues, issue => issue.Reason == AuditionProjectValidationFailureReason.InvalidProjectId);
        Assert.Contains(result.Issues, issue => issue.Reason == AuditionProjectValidationFailureReason.InvalidGameId);
        Assert.Contains(result.Issues, issue => issue.Reason == AuditionProjectValidationFailureReason.InvalidModId);
        Assert.Contains(result.Issues, issue => issue.Reason == AuditionProjectValidationFailureReason.InvalidTimestamps);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Invalid_project_name_is_rejected(string name)
    {
        Assert.Contains(Create(name: name).Issues,
            issue => issue.Reason == AuditionProjectValidationFailureReason.InvalidName);
    }

    [Fact]
    public void Unicode_project_name_is_metadata_only()
    {
        var result = Create(name: "Dự án giao diện Audition");
        Assert.Equal("Dự án giao diện Audition", result.Project!.Name);
    }

    [Fact]
    public void Duplicate_texture_identity_uses_windows_collision_semantics()
    {
        var texture = Texture("Texture/A.dds");
        var result = Create(editedTextures: [texture, Texture("texture/a.dds")]);

        Assert.Contains(result.Issues,
            issue => issue.DiagnosticCode == "AUDPROJ_TEXTURE_DUPLICATE");
    }

    [Fact]
    public void Duplicate_asset_id_across_image_and_ai_sets_is_rejected_atomically()
    {
        var duplicate = Asset("before", "Assets/ai.png", 'C');
        var result = Create(aiAssets: [duplicate]);

        Assert.False(result.Succeeded);
        Assert.Null(result.Project);
        Assert.Contains(result.Issues,
            issue => issue.DiagnosticCode == "AUDPROJ_ASSET_DUPLICATE");
    }

    [Fact]
    public void Dangling_texture_and_history_asset_references_are_rejected()
    {
        var texture = Texture("Texture/A.dds") with { CurrentImageAssetId = new("missing") };
        var edit = EditState() with
        {
            History = [new(1, new("Texture/A.dds"), EditOperationKind.Import, new("missing"), new("after"))]
        };

        var result = Create(editedTextures: [texture], editState: edit);

        Assert.Contains(result.Issues,
            issue => issue.Reason == AuditionProjectValidationFailureReason.DanglingReference);
    }

    [Fact]
    public void Invalid_revision_graph_is_rejected()
    {
        var edit = EditState() with { CurrentRevision = 0, SavedRevision = 1 };

        Assert.Contains(Create(editState: edit).Issues,
            issue => issue.Reason == AuditionProjectValidationFailureReason.InvalidEditState);
    }

    [Fact]
    public void Successful_build_requires_complete_output_identity()
    {
        var invalid = new ProjectBuildStateSnapshot(ProjectBuildStatus.Succeeded, DateTimeOffset.UtcNow, null, null);
        var valid = new ProjectBuildStateSnapshot(
            ProjectBuildStatus.Succeeded,
            DateTimeOffset.UtcNow,
            new("Build/015.ab"),
            new(new string('F', 64)));

        Assert.Contains(Create(buildState: invalid).Issues,
            issue => issue.Reason == AuditionProjectValidationFailureReason.InvalidBuildState);
        Assert.True(Create(buildState: valid).Succeeded);
    }

    [Fact]
    public void Collections_are_immutable_and_ordered_deterministically()
    {
        var input = new List<ProjectAssetRecord>
        {
            Asset("z_asset", "Assets/z.png", 'D'),
            Asset("a_asset", "Assets/a.png", 'E')
        };
        var result = Create(
            editedTextures: [],
            imageAssets: input,
            editState: new(0, 0, null, []));
        input.Clear();

        Assert.Equal(["a_asset", "z_asset"], result.Project!.ImageAssets.Select(item => item.Id.Value));
    }

    [Fact]
    public void Project_name_never_becomes_workspace_or_asset_identity()
    {
        var result = Create(name: "../Không dùng làm path");

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Project!.Name, result.Project.Workspace.WorkingArchiveRelativePath.Value);
    }

    private static AuditionProjectCreateResult Create(
        int schemaVersion = AuditionProject.CurrentSchemaVersion,
        Guid? projectId = null,
        string name = "Project 015",
        GameId? gameId = null,
        ModId? modId = null,
        IEnumerable<ProjectEditedTextureRecord?>? editedTextures = null,
        IEnumerable<ProjectAssetRecord?>? imageAssets = null,
        IEnumerable<ProjectAssetRecord?>? aiAssets = null,
        ProjectEditStateSnapshot? editState = null,
        ProjectBuildStateSnapshot? buildState = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null)
    {
        var created = createdAt ?? new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
        return AuditionProject.Create(
            schemaVersion,
            projectId ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
            name,
            gameId ?? new("audition"),
            modId ?? new("login_mod"),
            new(new("archive-015"), new("1"), new(new string('A', 64)), new("audition-vn-2026")),
            new("0123456789abcdef0123456789abcdef", new("Working/015.ab"), new("Extracted/015")),
            editedTextures ?? [Texture("Texture/A.dds")],
            imageAssets ?? [Asset("before", "Assets/before.png", 'B'), Asset("after", "Assets/after.png", 'C')],
            aiAssets ?? [Asset("ai_one", "Assets/ai-one.png", 'D')],
            editState ?? EditState(),
            buildState ?? new(ProjectBuildStatus.NotBuilt, null, null, null),
            created,
            updatedAt ?? created);
    }

    private static ProjectEditedTextureRecord Texture(string path) => new(
        new(path), new(new string('1', 64)), new(new string('2', 64)), new("after"), 1);

    private static ProjectAssetRecord Asset(string id, string path, char hash) => new(
        new(id), new(path), new(new string(hash, 64)));

    private static ProjectEditStateSnapshot EditState() => new(
        1,
        0,
        new ModRelativePath("Texture/A.dds"),
        [new(1, new("Texture/A.dds"), EditOperationKind.Import, new("before"), new("after"))]);
}

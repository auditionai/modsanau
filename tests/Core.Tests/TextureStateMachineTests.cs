using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;

namespace Core.Tests;

public sealed class TextureStateMachineTests
{
    private readonly TextureStateMachine _machine = new();

    [Theory]
    [InlineData(false, false, false, TextureState.Missing)]
    [InlineData(false, true, true, TextureState.Missing)]
    [InlineData(true, false, false, TextureState.Invalid)]
    [InlineData(true, false, true, TextureState.Invalid)]
    [InlineData(true, true, true, TextureState.Pending)]
    [InlineData(true, true, false, TextureState.Original)]
    public void Observation_priority_is_deterministic(
        bool exists,
        bool valid,
        bool pending,
        TextureState expected)
    {
        var result = _machine.Evaluate(
            Project(),
            new("Texture/logo.dds"),
            new(exists, valid, pending));

        Assert.True(result.Succeeded);
        Assert.Equal(expected, result.State);
    }

    [Fact]
    public void Edited_texture_backed_by_user_image_is_modified()
    {
        var result = _machine.Evaluate(
            Project(includeEdit: true, aiGenerated: false),
            new("texture/LOGO.dds"),
            new(true, true, false));

        Assert.Equal(TextureState.Modified, result.State);
    }

    [Fact]
    public void Edited_texture_backed_by_ai_asset_is_ai_generated()
    {
        var result = _machine.Evaluate(
            Project(includeEdit: true, aiGenerated: true),
            new("Texture/logo.dds"),
            new(true, true, false));

        Assert.Equal(TextureState.AiGenerated, result.State);
    }

    [Fact]
    public void Display_or_filename_does_not_imply_ai_generated_state()
    {
        var result = _machine.Evaluate(
            Project(name: "AI Project"),
            new("Texture/ai_generated_logo.dds"),
            new(true, true, false));

        Assert.Equal(TextureState.Original, result.State);
    }

    [Fact]
    public void Previous_state_reports_real_transition_without_mutating_project()
    {
        var project = Project(includeEdit: true, aiGenerated: false);
        var result = _machine.Evaluate(
            project,
            new("Texture/logo.dds"),
            new(true, true, false),
            TextureState.Pending);

        Assert.True(result.Changed);
        Assert.Equal(TextureState.Pending, result.PreviousState);
        Assert.Equal(TextureState.Modified, result.State);
        Assert.Single(project.EditedTextures);
    }

    [Fact]
    public void Invalid_path_and_unknown_previous_state_fail_structurally()
    {
        var invalidPath = _machine.Evaluate(Project(), default, new(true, true, false));
        var invalidPrevious = _machine.Evaluate(
            Project(), new("Texture/logo.dds"), new(true, true, false), (TextureState)999);

        Assert.Equal(TextureStateFailureReason.InvalidTexturePath, invalidPath.FailureReason);
        Assert.Equal(TextureStateFailureReason.InvalidPreviousState, invalidPrevious.FailureReason);
    }

    [Fact]
    public async Task Concurrent_evaluation_is_thread_safe()
    {
        var project = Project(includeEdit: true, aiGenerated: true);
        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
            _machine.Evaluate(project, new("Texture/logo.dds"), new(true, true, false)))));

        Assert.All(results, result => Assert.Equal(TextureState.AiGenerated, result.State));
    }

    private static AuditionProject Project(
        bool includeEdit = false,
        bool aiGenerated = false,
        string name = "Project")
    {
        var now = new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
        var assetId = new ProjectAssetId(aiGenerated ? "ai_asset" : "image_asset");
        var asset = new ProjectAssetRecord(assetId, new("Assets/logo.png"), new(new string('B', 64)));
        var edit = new ProjectEditedTextureRecord(
            new("Texture/logo.dds"),
            new(new string('A', 64)),
            new(new string('B', 64)),
            assetId,
            1);
        return AuditionProject.Create(
            1,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            name,
            new GameId("audition"),
            new ModId("login_mod"),
            new(new("archive-015"), new("1"), new(new string('C', 64)), new("audition-vn-2026")),
            new("0123456789abcdef0123456789abcdef", new("Working/015.ab"), new("Extracted/015")),
            includeEdit ? [edit] : [],
            includeEdit && !aiGenerated ? [asset] : [],
            includeEdit && aiGenerated ? [asset] : [],
            new(includeEdit ? 1 : 0, 0, null, []),
            new(ProjectBuildStatus.NotBuilt, null, null, null),
            now,
            now).Project!;
    }
}

using System.Text;
using AuditionModStudio.Core.AI;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Infrastructure.Paths;
using AuditionModStudio.Projects;

namespace Projects.Tests;

public sealed class LocalPromptPresetStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"prompt-presets-{Guid.NewGuid():N}");

    [Fact]
    public async Task Create_update_delete_and_persistence_roundtrip_are_versioned()
    {
        var store = CreateStore();
        Assert.True((await store.SaveAsync(Preset(1))).Succeeded);
        Assert.False((await store.SaveAsync(Preset(1))).Succeeded);
        Assert.True((await store.SaveAsync(Preset(2))).Succeeded);

        var loaded = await CreateStore().LoadAsync();
        Assert.True(loaded.Succeeded);
        Assert.Equal(2, Assert.Single(loaded.Presets).Version);
        Assert.True((await store.DeleteAsync(new("local-neon"))).Succeeded);
        Assert.Empty((await store.LoadAsync()).Presets);
    }

    [Fact]
    public async Task Export_import_is_bounded_strict_and_does_not_serialize_authority()
    {
        var store = CreateStore();
        await store.SaveAsync(Preset(1));
        await using var stream = new MemoryStream();
        Assert.True((await store.ExportAsync(stream)).Succeeded);
        var json = Encoding.UTF8.GetString(stream.ToArray());
        Assert.DoesNotContain("providerSecret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("creditCost", json, StringComparison.OrdinalIgnoreCase);

        stream.Position = 0;
        Assert.True((await CreateStore().ImportAsync(stream)).Succeeded);
        await using var malformed = new MemoryStream(Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"presets\":[],\"unknown\":true}"));
        Assert.False((await store.ImportAsync(malformed)).Succeeded);
    }

    [Fact]
    public async Task Corrupt_or_unsupported_schema_isolated_without_silent_migration()
    {
        var paths = new AppPaths(_root); paths.EnsureDirectoriesExist();
        var directory = Path.Combine(paths.SettingsDirectory, "PromptPresets"); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "presets.v1.json"), "{\"schemaVersion\":99,\"presets\":[]}");

        var result = await new LocalPromptPresetStore(paths, new PathSecurity()).LoadAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("PROMPT_PRESET_STORAGE_CORRUPT", result.DiagnosticCode);
    }

    [Fact]
    public async Task Cloud_origin_cannot_be_written_to_local_user_store()
    {
        var cloud = new PromptPreset(new("cloud"), 1, "Cloud", "", new("prompt"), null,
            [AiStudioOperation.Generate], new("audition"), new("ui"), new("logo"), [], PromptPresetOrigin.Cloud);
        Assert.False((await CreateStore().SaveAsync(cloud)).Succeeded);
    }

    private LocalPromptPresetStore CreateStore()
    {
        var paths = new AppPaths(_root); paths.EnsureDirectoriesExist();
        return new(paths, new PathSecurity());
    }

    private static PromptPreset Preset(int version) => new(new("local-neon"), version, "Neon", "",
        new("neon logo"), null, [AiStudioOperation.Generate], new GameId("audition"), new ModId("ui"),
        new TextureSlotId("logo"), ["neon"], PromptPresetOrigin.Local);

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

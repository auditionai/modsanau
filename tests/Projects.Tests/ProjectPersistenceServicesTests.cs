using System.Text.Json;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Infrastructure.Paths;
using AuditionModStudio.Projects;

namespace Projects.Tests;

public sealed class ProjectPersistenceServicesTests
{
    [Fact]
    public async Task Save_writes_atomic_audproj_using_project_id_not_display_name()
    {
        await using var context = new Context();
        var project = Project("../Tên dự án không phải path");

        var result = await context.Store.SaveAsync(project);

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.Equal("11111111111111111111111111111111.audproj", result.ProjectFileName);
        var path = Path.Combine(context.Paths.ProjectsDirectory, result.ProjectFileName!);
        Assert.True(File.Exists(path));
        Assert.DoesNotContain(project.Name, path, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(context.Paths.ProjectsDirectory, "*.tmp"));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("audition", json.RootElement.GetProperty("gameId").GetString());
        Assert.Equal("1", json.RootElement.GetProperty("template").GetProperty("version").GetString());
    }

    [Fact]
    public async Task Save_overwrites_same_project_atomically_and_delete_targets_exact_id()
    {
        await using var context = new Context();
        Assert.True((await context.Store.SaveAsync(Project("First"))).Succeeded);
        Assert.True((await context.Store.SaveAsync(Project("Second"))).Succeeded);
        var path = Path.Combine(context.Paths.ProjectsDirectory, "11111111111111111111111111111111.audproj");
        Assert.Contains("Second", await File.ReadAllTextAsync(path), StringComparison.Ordinal);

        Assert.True((await context.Store.DeleteAsync(Guid.Parse("11111111-1111-1111-1111-111111111111"))).Succeeded);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Metadata_cache_rejects_windows_path_collision_without_writing_partial_file()
    {
        await using var context = new Context();
        var first = Metadata("Texture/A.dds", 'A');
        var second = Metadata("texture/a.dds", 'B');

        var result = await context.Cache.StoreAsync(ProjectId, [first, second]);

        Assert.False(result.Succeeded);
        Assert.Equal(ProjectMetadataCacheFailureReason.InvalidData, result.FailureReason);
        Assert.Empty(Directory.EnumerateFiles(context.Paths.CacheDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Metadata_cache_is_deterministic_and_deletable()
    {
        await using var context = new Context();

        var result = await context.Cache.StoreAsync(ProjectId,
            [Metadata("Texture/Z.dds", 'A'), Metadata("Texture/A.dds", 'B')]);

        Assert.True(result.Succeeded, result.DiagnosticCode);
        var path = Path.Combine(context.Paths.CacheDirectory, "ProjectMetadata", $"{ProjectId:N}.metadata.json");
        var json = await File.ReadAllTextAsync(path);
        Assert.True(json.IndexOf("Texture/A.dds", StringComparison.Ordinal) < json.IndexOf("Texture/Z.dds", StringComparison.Ordinal));
        Assert.True((await context.Cache.DeleteAsync(ProjectId)).Succeeded);
        Assert.False(File.Exists(path));
    }

    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static AuditionProject Project(string name)
    {
        var now = new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
        return AuditionProject.Create(
            1,
            ProjectId,
            name,
            new GameId("audition"),
            new ModId("login_mod"),
            new(new("archive-015"), new("1"), new(new string('A', 64)), new("audition-vn-2026")),
            new("0123456789abcdef0123456789abcdef", new("Working/015.ab"), new("Extracted/015")),
            [], [], [],
            new(0, 0, null, []),
            new(ProjectBuildStatus.NotBuilt, null, null, null),
            now,
            now).Project!;
    }

    private static ProjectTextureMetadataSnapshot Metadata(string path, char hash) => new(
        new(path),
        new(new string(hash, 64)),
        new(
            64, 32, null, 1, 1,
            DdsFormat.BC3,
            DdsFormatSupport.Known,
            "DXT5", null,
            DdsHeaderType.Legacy,
            true, true,
            DdsAlphaMode.Interpolated,
            DdsColorSpace.Linear,
            DdsResourceDimension.Texture2D,
            false, 1, 256, 128),
        null);

    private sealed class Context : IAsyncDisposable
    {
        public Context()
        {
            Root = Path.Combine(Path.GetTempPath(), "ProjectPersistenceServicesTests", Guid.NewGuid().ToString("N"));
            Paths = new AppPaths(Root);
            Paths.EnsureDirectoriesExist();
            var pathSecurity = new PathSecurity();
            Store = new(Paths, pathSecurity);
            Cache = new(Paths, pathSecurity);
        }

        public string Root { get; }
        public AppPaths Paths { get; }
        public AuditionProjectStore Store { get; }
        public ProjectMetadataCache Cache { get; }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}

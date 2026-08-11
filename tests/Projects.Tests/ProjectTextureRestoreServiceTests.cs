using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Projects;

namespace Projects.Tests;

public sealed class ProjectTextureRestoreServiceTests
{
    [Fact]
    public async Task Commit_keeps_pristine_texture_and_removes_transaction_artifacts()
    {
        await using var context = new Context(targetExists: true);

        var result = await context.Service.BeginRestoreAsync(
            context.Target, context.Pristine, new("Texture/logo.dds"));
        await using var transaction = Assert.IsAssignableFrom<IProjectTextureRestoreTransaction>(result.Transaction);
        Assert.Equal("original", await File.ReadAllTextAsync(context.TargetPath));

        await transaction.CommitAsync();

        Assert.Equal("original", await File.ReadAllTextAsync(context.TargetPath));
        Assert.Empty(Directory.EnumerateFiles(
            context.Target.ArchiveWorkspace.SecureWorkspace.Paths.BuildOutputDirectory,
            "*", SearchOption.AllDirectories));
        Assert.Equal("original", await File.ReadAllTextAsync(context.PristinePath));
    }

    [Fact]
    public async Task Rollback_restores_previous_target_bytes()
    {
        await using var context = new Context(targetExists: true);
        var result = await context.Service.BeginRestoreAsync(
            context.Target, context.Pristine, new("Texture/logo.dds"));
        await using var transaction = result.Transaction!;

        await transaction.RollbackAsync();

        Assert.Equal("edited", await File.ReadAllTextAsync(context.TargetPath));
        Assert.Equal("original", await File.ReadAllTextAsync(context.PristinePath));
    }

    [Fact]
    public async Task Rollback_removes_restored_file_when_target_was_missing()
    {
        await using var context = new Context(targetExists: false);
        var result = await context.Service.BeginRestoreAsync(
            context.Target, context.Pristine, new("Texture/logo.dds"));
        await using var transaction = result.Transaction!;
        Assert.True(File.Exists(context.TargetPath));

        await transaction.RollbackAsync();

        Assert.False(File.Exists(context.TargetPath));
    }

    [Fact]
    public async Task Invalid_path_and_missing_pristine_source_fail_without_target_mutation()
    {
        await using var context = new Context(targetExists: true);
        var invalid = await context.Service.BeginRestoreAsync(context.Target, context.Pristine, default);
        File.Delete(context.PristinePath);
        var missing = await context.Service.BeginRestoreAsync(
            context.Target, context.Pristine, new("Texture/logo.dds"));

        Assert.False(invalid.Succeeded);
        Assert.False(missing.Succeeded);
        Assert.Equal("edited", await File.ReadAllTextAsync(context.TargetPath));
    }

    private sealed class Context : IAsyncDisposable
    {
        public Context(bool targetExists)
        {
            Root = Path.Combine(Path.GetTempPath(), "ProjectTextureRestoreTests", Guid.NewGuid().ToString("N"));
            var template = new AuditionArchiveTemplate(
                "archive-015", "015.ab", "templates/015.ab", ArchiveEngineType.AcvTool5,
                "audition_vn", "015", "1", new string('A', 64), "audition-vn-2026");
            Target = CreateWorkspace(Path.Combine(Root, "target"), template, new string('1', 32));
            Pristine = CreateWorkspace(Path.Combine(Root, "pristine"), template, new string('2', 32));
            TargetPath = Path.Combine(Target.ArchiveWorkspace.SecureWorkspace.Paths.ExtractedDirectory,
                "015", "Texture", "logo.dds");
            PristinePath = Path.Combine(Pristine.ArchiveWorkspace.SecureWorkspace.Paths.ExtractedDirectory,
                "015", "Texture", "logo.dds");
            Directory.CreateDirectory(Path.GetDirectoryName(TargetPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(PristinePath)!);
            File.WriteAllText(PristinePath, "original");
            if (targetExists)
            {
                File.WriteAllText(TargetPath, "edited");
            }
        }

        public string Root { get; }
        public ProjectTextureRestoreService Service { get; } = new();
        public StubWorkspace Target { get; }
        public StubWorkspace Pristine { get; }
        public string TargetPath { get; }
        public string PristinePath { get; }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
            return ValueTask.CompletedTask;
        }

        private static StubWorkspace CreateWorkspace(
            string root,
            AuditionArchiveTemplate template,
            string id)
        {
            var paths = new SecureWorkspacePaths(
                root,
                Path.Combine(root, "Working"),
                Path.Combine(root, "Extracted"),
                Path.Combine(root, "BuildOutput"));
            Directory.CreateDirectory(paths.WorkingDirectory);
            Directory.CreateDirectory(paths.ExtractedDirectory);
            Directory.CreateDirectory(paths.BuildOutputDirectory);
            return new(new StubSecureWorkspace(id, paths), template);
        }
    }

    private sealed class StubWorkspace : IProjectArchiveWorkspace
    {
        public StubWorkspace(ISecureWorkspace secure, AuditionArchiveTemplate template)
        {
            ArchiveWorkspace = ArchiveWorkspace.Create(secure, template);
            var now = DateTimeOffset.UtcNow;
            Descriptor = new(
                1, Guid.NewGuid(), "Project", secure.Id,
                new(template.TemplateId.Value, template.TemplateVersion!.Value.Value,
                    template.ExpectedSha256!.Value.Value, template.EngineType, template.RegionProfileId,
                    template.CompatibleGameBuild!.Value.Value),
                "Working/015.ab", "Extracted/015", "BuildOutput", null,
                ".project-archive-workspace.json", template.ExpectedSha256.Value.Value,
                now, now, ProjectArchiveWorkspaceState.Ready);
        }
        public ProjectArchiveWorkspaceDescriptor Descriptor { get; }
        public ArchiveWorkspace ArchiveWorkspace { get; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubSecureWorkspace(string id, SecureWorkspacePaths paths) : ISecureWorkspace
    {
        public string Id { get; } = id;
        public SecureWorkspacePaths Paths { get; } = paths;
        public string ResolveRelativePath(string relativePath)
        {
            var path = Path.GetFullPath(Path.Combine(Paths.RootDirectory, relativePath));
            if (!path.StartsWith(Path.TrimEndingDirectorySeparator(Paths.RootDirectory) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Path escaped workspace.", nameof(relativePath));
            }
            return path;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

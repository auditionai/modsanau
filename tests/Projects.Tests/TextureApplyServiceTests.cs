using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Projects;

namespace Projects.Tests;

public sealed class TextureApplyServiceTests
{
    [Fact]
    public async Task Apply_replaces_only_working_texture_and_saves_history_modified_state_and_thumbnail()
    {
        await using var context = new Context();
        var progress = new List<TextureApplyPhase>();

        var result = await context.Service.ApplyAsync(
            context.Request,
            new CallbackProgress<TextureApplyProgress>(value => progress.Add(value.Phase)));

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.Equal("new-dds", await File.ReadAllTextAsync(context.TargetPath));
        Assert.Equal(TextureState.Modified, result.State);
        Assert.True(context.Store.Saved);
        Assert.True(context.Thumbnail.Called);
        Assert.Single(result.Project!.EditedTextures);
        Assert.Single(result.Project.EditState.History);
        Assert.Equal(ProjectBuildStatus.Dirty, result.Project.BuildState.Status);
        Assert.Equal(result.Project.EditState.CurrentRevision, result.Project.EditState.SavedRevision);
        Assert.Equal(2, result.Project.ImageAssets.Length);
        Assert.All(result.Project.ImageAssets, asset => Assert.True(File.Exists(
            context.Workspace.ArchiveWorkspace.SecureWorkspace.ResolveRelativePath(
                asset.RelativePath.Value.Replace('/', Path.DirectorySeparatorChar)))));
        Assert.Contains(TextureApplyPhase.ValidatingTarget, progress);
        Assert.Contains(TextureApplyPhase.Resizing, progress);
        Assert.Contains(TextureApplyPhase.Encoding, progress);
        Assert.Contains(TextureApplyPhase.ValidatingOutput, progress);
        Assert.Contains(TextureApplyPhase.Replacing, progress);
        Assert.Contains(TextureApplyPhase.UpdatingHistory, progress);
        Assert.Contains(TextureApplyPhase.RegeneratingThumbnail, progress);
        Assert.Contains(TextureApplyPhase.SavingProject, progress);
    }

    [Fact]
    public async Task Validation_failure_does_not_replace_target_or_save_project()
    {
        await using var context = new Context(validationSucceeds: false);

        var result = await context.Service.ApplyAsync(context.Request);

        Assert.False(result.Succeeded);
        Assert.Equal(TextureApplyFailureReason.ValidationFailed, result.FailureReason);
        Assert.Equal("old-dds", await File.ReadAllTextAsync(context.TargetPath));
        Assert.False(context.Store.Saved);
        Assert.Empty(Directory.EnumerateFiles(
            context.Workspace.ArchiveWorkspace.SecureWorkspace.Paths.BuildOutputDirectory,
            "*.dds",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Project_save_failure_rolls_back_target_and_new_history_assets()
    {
        await using var context = new Context(saveSucceeds: false);

        var result = await context.Service.ApplyAsync(context.Request);

        Assert.False(result.Succeeded);
        Assert.Equal(TextureApplyFailureReason.SaveFailed, result.FailureReason);
        Assert.Equal("old-dds", await File.ReadAllTextAsync(context.TargetPath));
        Assert.Empty(Directory.EnumerateFiles(
            context.Workspace.ArchiveWorkspace.SecureWorkspace.Paths.BuildOutputDirectory,
            "*.dds",
            SearchOption.AllDirectories));
    }

    private sealed class Context : IAsyncDisposable
    {
        private readonly string _root;

        public Context(bool validationSucceeds = true, bool saveSucceeds = true)
        {
            _root = Path.Combine(Path.GetTempPath(), "TextureApplyServiceTests", Guid.NewGuid().ToString("N"));
            var paths = new SecureWorkspacePaths(
                _root,
                Path.Combine(_root, "Working"),
                Path.Combine(_root, "Extracted"),
                Path.Combine(_root, "BuildOutput"));
            Directory.CreateDirectory(paths.WorkingDirectory);
            Directory.CreateDirectory(paths.ExtractedDirectory);
            Directory.CreateDirectory(paths.BuildOutputDirectory);
            var secure = new StubSecureWorkspace("0123456789abcdef0123456789abcdef", paths);
            var template = new AuditionArchiveTemplate(
                "archive-015", "015.ab", "templates/015.ab", ArchiveEngineType.AcvTool5,
                "audition_vn", "015", "1", new string('A', 64), "audition-vn-2026");
            var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            Workspace = new StubWorkspace(secure, template, projectId);
            TargetPath = Path.Combine(paths.ExtractedDirectory, "015", "Texture", "logo.dds");
            Directory.CreateDirectory(Path.GetDirectoryName(TargetPath)!);
            File.WriteAllText(TargetPath, "old-dds");
            var now = new DateTimeOffset(2026, 8, 12, 8, 0, 0, TimeSpan.Zero);
            Project = AuditionProject.Create(
                1,
                projectId,
                "Project",
                new GameId("audition"),
                new ModId("login_mod"),
                Workspace.Descriptor.ArchiveTemplate.Identity,
                new(Workspace.Descriptor.WorkspaceId, new("Working/015.ab"), new("Extracted/015")),
                [],
                [],
                [],
                new(0, 0, null, []),
                new(ProjectBuildStatus.NotBuilt, null, null, null),
                now,
                now).Project!;
            var image = Image(2, 2);
            Request = new(
                Project,
                Workspace,
                new("Texture/logo.dds"),
                new(image, 2, 2, new(ImageResizeMode.Stretch)));
            Store = new StubStore(saveSucceeds);
            Thumbnail = new StubThumbnail();
            var metadata = Metadata();
            Service = new(
                new StubMetadataReader(metadata),
                new StubResize(),
                new StubMatch(metadata),
                new StubValidation(metadata, validationSucceeds),
                new TextureStateMachine(),
                Thumbnail,
                Store,
                new FixedTimeProvider());
        }

        public StubWorkspace Workspace { get; }
        public string TargetPath { get; }
        public AuditionProject Project { get; }
        public TextureApplyRequest Request { get; }
        public StubStore Store { get; }
        public StubThumbnail Thumbnail { get; }
        public TextureApplyService Service { get; }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubMetadataReader(DdsMetadata metadata) : IDdsMetadataReader
    {
        public Task<DdsMetadataReadResult> ReadAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(DdsMetadataReadResult.Success(metadata));
    }

    private sealed class StubResize : IImageResizeService
    {
        public Task<ImageResizeResult> ResizeAsync(
            ImageResizeRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ImageResizeResult.Success(request.Source));
    }

    private sealed class StubMatch(DdsMetadata metadata) : IDdsMatchOriginalService
    {
        private readonly DdsTargetSettings _settings = new(
            metadata.Width,
            metadata.Height,
            metadata.Format,
            1,
            metadata.HeaderType,
            DdsColorSpace.Linear,
            DdsTargetAlphaSemantics.Full);

        public DdsMatchOriginalProfileResult DeriveProfile(DdsMetadata value) =>
            DdsMatchOriginalProfileResult.Success(new(
                _settings,
                value.ColorSpace,
                false,
                value.DeclaredMipMapCount,
                value.ResourceDimension,
                value.IsCubemap,
                value.ArraySize));

        public async Task<DdsMatchOriginalResult> MatchAsync(
            DdsMatchOriginalRequest request,
            CancellationToken cancellationToken = default)
        {
            var output = request.Workspace.ResolveRelativePath(request.OutputRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, "new-dds", cancellationToken);
            return DdsMatchOriginalResult.Success(
                DeriveProfile(metadata).Profile!,
                metadata,
                MatchReport(),
                request.OutputRelativePath);
        }
    }

    private sealed class StubValidation(DdsMetadata metadata, bool succeeds) : IDdsValidationService
    {
        public Task<DdsValidationResult> ValidateAsync(
            DdsValidationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(succeeds
                ? DdsValidationResult.Success(metadata, metadata, MatchReport())
                : DdsValidationResult.Failure(
                    DdsValidationFailureReason.MetadataMismatch,
                    "TEST_MISMATCH",
                    metadata,
                    metadata,
                    MatchReport() with { FormatMatches = false }));
    }

    private sealed class StubThumbnail : IThumbnailCache
    {
        public bool Called { get; private set; }

        public Task<ThumbnailCacheResult> GetOrCreateAsync(
            ThumbnailCacheRequest request,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(ThumbnailCacheResult.Success(Image(1, 1), ThumbnailCacheSource.Generated));
        }
    }

    private sealed class StubStore(bool succeeds) : IAuditionProjectStore
    {
        public bool Saved { get; private set; }

        public Task<AuditionProjectStoreResult> SaveAsync(
            AuditionProject project,
            CancellationToken cancellationToken = default)
        {
            Saved = succeeds;
            return Task.FromResult(succeeds
                ? AuditionProjectStoreResult.Success("project.audproj")
                : AuditionProjectStoreResult.Failure(AuditionProjectStoreFailureReason.IoFailure, "TEST_SAVE_FAILED"));
        }

        public Task<AuditionProjectStoreResult> DeleteAsync(
            Guid projectId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AuditionProjectLoadResult> LoadAsync(
            Guid projectId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubWorkspace : IProjectArchiveWorkspace
    {
        public StubWorkspace(ISecureWorkspace secure, AuditionArchiveTemplate template, Guid projectId)
        {
            ArchiveWorkspace = ArchiveWorkspace.Create(secure, template);
            var now = DateTimeOffset.UtcNow;
            Descriptor = new(
                1,
                projectId,
                "Project",
                secure.Id,
                new(
                    template.TemplateId.Value,
                    template.TemplateVersion!.Value.Value,
                    template.ExpectedSha256!.Value.Value,
                    template.EngineType,
                    template.RegionProfileId,
                    template.CompatibleGameBuild!.Value.Value),
                "Working/015.ab",
                "Extracted/015",
                "BuildOutput",
                null,
                ".project-archive-workspace.json",
                template.ExpectedSha256.Value.Value,
                now,
                now,
                ProjectArchiveWorkspaceState.Ready);
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
            if (!path.StartsWith(
                    Path.TrimEndingDirectorySeparator(Paths.RootDirectory) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Path escaped workspace.", nameof(relativePath));
            }

            return path;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private static InternalImage Image(int width, int height) => new(
        width,
        height,
        checked(width * 4),
        Enumerable.Repeat((byte)255, checked(width * height * 4)).ToArray(),
        new(ImageSourceFormat.Png, width, height, ImageSourceOrientation.Normal, true, false));

    private static DdsMetadata Metadata() => new(
        2,
        2,
        null,
        1,
        1,
        DdsFormat.BC3,
        DdsFormatSupport.Known,
        "DXT5",
        null,
        DdsHeaderType.Legacy,
        true,
        true,
        DdsAlphaMode.Interpolated,
        DdsColorSpace.Unknown,
        DdsResourceDimension.Texture2D,
        false,
        1,
        128,
        128);

    private static DdsMetadataMatchReport MatchReport() => new(true, true, true, true, true, true);
}

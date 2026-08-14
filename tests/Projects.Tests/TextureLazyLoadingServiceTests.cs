using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Assets;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Projects;

namespace Projects.Tests;

public sealed class TextureLazyLoadingServiceTests
{
    [Fact]
    public async Task Thumbnail_stage_delegates_to_content_cache_without_full_preview_or_import()
    {
        var cache = new StubThumbnailCache();
        var preview = new StubPreview();
        var import = new StubImport();
        var service = new TextureLazyLoadingService(cache, preview, import);

        var result = await service.LoadThumbnailAsync(new(Workspace, Asset(), 64));

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.Equal(ThumbnailCacheSource.Disk, result.CacheSource);
        Assert.Equal(1, cache.CallCount);
        Assert.Equal(0, preview.CallCount);
        Assert.Equal(0, import.CallCount);
    }

    [Fact]
    public async Task Full_texture_is_not_loaded_until_selected_api_is_called()
    {
        var preview = new StubPreview();
        var import = new StubImport();
        var service = new TextureLazyLoadingService(new StubThumbnailCache(), preview, import);

        await service.LoadThumbnailAsync(new(Workspace, Asset(), 64));
        Assert.Equal(0, preview.CallCount);
        Assert.Equal(0, import.CallCount);

        var selected = await service.LoadSelectedTextureAsync(new(Workspace, Asset(), Metadata));

        Assert.True(selected.Succeeded, selected.DiagnosticCode);
        Assert.Equal(1, preview.CallCount);
        Assert.Equal(1, import.CallCount);
        Assert.Equal(128, selected.Image!.Width);
    }

    [Fact]
    public async Task Changed_observed_metadata_rejects_stale_selection_before_import()
    {
        var changed = Metadata with { Width = 256 };
        var preview = new StubPreview(changed);
        var import = new StubImport();
        var service = new TextureLazyLoadingService(new StubThumbnailCache(), preview, import);

        var result = await service.LoadSelectedTextureAsync(new(Workspace, Asset(), Metadata));

        Assert.False(result.Succeeded);
        Assert.Equal(TextureLazyLoadFailureReason.MetadataChanged, result.FailureReason);
        Assert.Equal(0, import.CallCount);
    }

    [Fact]
    public async Task Invalid_asset_identity_is_rejected_before_dependencies_run()
    {
        var cache = new StubThumbnailCache();
        var preview = new StubPreview();
        var service = new TextureLazyLoadingService(cache, preview, new StubImport());
        var invalid = Asset() with { RelativePath = "../escape.dds" };

        var thumbnail = await service.LoadThumbnailAsync(new(Workspace, invalid, 64));
        var selected = await service.LoadSelectedTextureAsync(new(Workspace, invalid, Metadata));

        Assert.Equal(TextureLazyLoadFailureReason.InvalidRequest, thumbnail.FailureReason);
        Assert.Equal(TextureLazyLoadFailureReason.InvalidRequest, selected.FailureReason);
        Assert.Equal(0, cache.CallCount);
        Assert.Equal(0, preview.CallCount);
    }

    [Fact]
    public async Task Thumbnail_and_selected_texture_cancellation_are_structured()
    {
        var cache = new StubThumbnailCache(cancel: true);
        var preview = new StubPreview(cancel: true);
        var service = new TextureLazyLoadingService(cache, preview, new StubImport());

        var thumbnail = await service.LoadThumbnailAsync(new(Workspace, Asset(), 64));
        var selected = await service.LoadSelectedTextureAsync(new(Workspace, Asset(), Metadata));

        Assert.True(thumbnail.Cancelled);
        Assert.True(selected.Cancelled);
        Assert.Equal(TextureLazyLoadFailureReason.Cancelled, thumbnail.FailureReason);
        Assert.Equal(TextureLazyLoadFailureReason.Cancelled, selected.FailureReason);
    }

    [Fact]
    public async Task Concurrent_selected_loads_do_not_share_mutable_full_images()
    {
        var service = new TextureLazyLoadingService(
            new StubThumbnailCache(), new StubPreview(), new StubImport());

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => service.LoadSelectedTextureAsync(new(Workspace, Asset(), Metadata))));

        Assert.All(results, result => Assert.True(result.Succeeded, result.DiagnosticCode));
        Assert.Equal(8, results.Select(result => result.Image).Distinct(ReferenceEqualityComparer.Instance).Count());
    }

    private static TextureAsset Asset() => new(
        "Texture/logo.dds", "logo.dds", ".dds", "Texture", 128, DateTimeOffset.UtcNow,
        new string('A', 64));

    private static InternalImage Image() => new(
        128, 64, 128 * 4, Enumerable.Repeat((byte)255, 128 * 64 * 4).ToArray(),
        new(ImageSourceFormat.Png, 128, 64, ImageSourceOrientation.Normal, true, false));

    private static DdsMetadata Metadata { get; } = new(
        128, 64, null, 1, 1, DdsFormat.BC3, DdsFormatSupport.Known, "DXT5", null,
        DdsHeaderType.Legacy, true, true, DdsAlphaMode.Interpolated, DdsColorSpace.Unknown,
        DdsResourceDimension.Texture2D, false, 1, 128, 128);

    private static IProjectArchiveWorkspace Workspace { get; } = new StubProjectWorkspace();

    private sealed class StubThumbnailCache(bool cancel = false) : IThumbnailCache
    {
        private int _calls;
        public int CallCount => _calls;
        public Task<ThumbnailCacheResult> GetOrCreateAsync(
            ThumbnailCacheRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(cancel
                ? ThumbnailCacheResult.Failure(ThumbnailCacheFailureReason.Cancelled, "CANCELLED")
                : ThumbnailCacheResult.Success(Image(), ThumbnailCacheSource.Disk));
        }
    }

    private sealed class StubPreview(DdsMetadata? metadata = null, bool cancel = false) : IDdsPreviewService
    {
        private int _calls;
        public int CallCount => _calls;
        public Task<DdsPreviewResult> CreateAsync(
            DdsPreviewRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(cancel
                ? DdsPreviewResult.Failure(DdsPreviewFailureReason.Cancelled, "CANCELLED")
                : DdsPreviewResult.Success(metadata ?? Metadata, new(128, 64, [1])));
        }
    }

    private sealed class StubImport : IImageImportService
    {
        private int _calls;
        public int CallCount => _calls;
        public Task<ImageImportResult> ImportAsync(
            ImageImportRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImageImportResult> ImportMemoryAsync(
            ImageImportMemoryRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(ImageImportResult.Success(Image()));
        }
    }

    private sealed class StubProjectWorkspace : IProjectArchiveWorkspace
    {
        public StubProjectWorkspace()
        {
            var secure = new StubSecureWorkspace();
            var template = new AuditionArchiveTemplate(
                "template", "015.ab", "015.ab", ArchiveEngineType.AcvTool5,
                "audition_vn", "015", "1", new string('A', 64), "build");
            ArchiveWorkspace = ArchiveWorkspace.Create(secure, template);
            Descriptor = new(
                1, Guid.NewGuid(), "Project", secure.Id,
                new("template", "1", new string('A', 64), ArchiveEngineType.AcvTool5, "audition_vn", "build"),
                "Working/015.ab", "Extracted/015", "BuildOutput", null, "manifest.json", new string('A', 64),
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ProjectArchiveWorkspaceState.Ready);
        }

        public ProjectArchiveWorkspaceDescriptor Descriptor { get; }
        public ArchiveWorkspace ArchiveWorkspace { get; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubSecureWorkspace : ISecureWorkspace
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public SecureWorkspacePaths Paths { get; } = new("root", "working", "extracted", "build");
        public string ResolveRelativePath(string relativePath) => relativePath;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

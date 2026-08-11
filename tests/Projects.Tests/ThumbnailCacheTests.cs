using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Infrastructure.Paths;
using AuditionModStudio.Projects;

namespace Projects.Tests;

public sealed class ThumbnailCacheTests
{
    [Fact]
    public async Task Miss_generates_once_and_second_request_hits_memory()
    {
        await using var context = new Context();

        var first = await context.Cache.GetOrCreateAsync(context.Request());
        var second = await context.Cache.GetOrCreateAsync(context.Request());

        Assert.Equal(ThumbnailCacheSource.Generated, first.Source);
        Assert.Equal(ThumbnailCacheSource.Memory, second.Source);
        Assert.Equal(1, context.Preview.CallCount);
        Assert.Equal(64, first.Image!.Width);
        Assert.Equal(32, first.Image.Height);
    }

    [Fact]
    public async Task New_cache_instance_hits_valid_disk_entry_without_preview_generation()
    {
        await using var context = new Context();
        Assert.True((await context.Cache.GetOrCreateAsync(context.Request())).Succeeded);
        var otherPreview = new StubPreview();
        var secondCache = context.CreateCache(otherPreview);

        var result = await secondCache.GetOrCreateAsync(context.Request());

        Assert.Equal(ThumbnailCacheSource.Disk, result.Source);
        Assert.Equal(0, otherPreview.CallCount);
    }

    [Fact]
    public async Task Corrupt_entry_is_discarded_and_regenerated()
    {
        await using var context = new Context();
        Assert.True((await context.Cache.GetOrCreateAsync(context.Request())).Succeeded);
        var entry = Assert.Single(Directory.EnumerateFiles(context.CacheDirectory, "*.thumb"));
        await File.WriteAllBytesAsync(entry, [1, 2, 3]);
        var preview = new StubPreview();
        var secondCache = context.CreateCache(preview);

        var result = await secondCache.GetOrCreateAsync(context.Request());

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.True(result.CorruptEntryRecovered);
        Assert.Equal(ThumbnailCacheSource.Generated, result.Source);
        Assert.Equal(1, preview.CallCount);
        Assert.True(new FileInfo(entry).Length > 3);
    }

    [Fact]
    public async Task Changed_source_hash_or_size_never_reuses_stale_thumbnail()
    {
        await using var context = new Context();

        await context.Cache.GetOrCreateAsync(context.Request(hash: 'A', maximumDimension: 64));
        await context.Cache.GetOrCreateAsync(context.Request(hash: 'B', maximumDimension: 64));
        await context.Cache.GetOrCreateAsync(context.Request(hash: 'B', maximumDimension: 32));

        Assert.Equal(3, context.Preview.CallCount);
        Assert.Equal(3, Directory.EnumerateFiles(context.CacheDirectory, "*.thumb").Count());
    }

    [Fact]
    public async Task Concurrent_same_key_is_single_flight()
    {
        await using var context = new Context(previewDelay: TimeSpan.FromMilliseconds(75));

        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => context.Cache.GetOrCreateAsync(context.Request())));

        Assert.All(results, result => Assert.True(result.Succeeded, result.DiagnosticCode));
        Assert.Equal(1, context.Preview.CallCount);
        Assert.Single(results, result => result.Source == ThumbnailCacheSource.Generated);
    }

    [Fact]
    public async Task Cancellation_is_structured_and_does_not_publish_partial_entry()
    {
        await using var context = new Context(previewDelay: TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        var result = await context.Cache.GetOrCreateAsync(context.Request(), cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(ThumbnailCacheFailureReason.Cancelled, result.FailureReason);
        Assert.Empty(Directory.EnumerateFiles(context.CacheDirectory, "*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Disk_and_memory_entry_limits_evict_only_derived_cache()
    {
        await using var context = new Context(options: new(1, 1, 1024 * 1024));
        var sourceSentinel = Path.Combine(context.Workspace.ArchiveWorkspace.SecureWorkspace.Paths.ExtractedDirectory,
            "015", "Texture", "logo.dds");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceSentinel)!);
        await File.WriteAllTextAsync(sourceSentinel, "authoritative");

        await context.Cache.GetOrCreateAsync(context.Request(hash: 'A'));
        await context.Cache.GetOrCreateAsync(context.Request(hash: 'B'));

        Assert.Single(Directory.EnumerateFiles(context.CacheDirectory, "*.thumb"));
        Assert.Equal("authoritative", await File.ReadAllTextAsync(sourceSentinel));
    }

    private sealed class Context : IAsyncDisposable
    {
        private readonly AppPaths _paths;
        private readonly ThumbnailCacheOptions _options;
        private readonly StubImport _import = new();
        private readonly StubResize _resize = new();

        public Context(TimeSpan? previewDelay = null, ThumbnailCacheOptions? options = null)
        {
            Root = Path.Combine(Path.GetTempPath(), "ThumbnailCacheTests", Guid.NewGuid().ToString("N"));
            _paths = new(Root);
            _paths.EnsureDirectoriesExist();
            _options = options ?? new(16, 32, 16 * 1024 * 1024);
            Preview = new(previewDelay ?? TimeSpan.Zero);
            Workspace = new StubWorkspace(Path.Combine(Root, "workspace"));
            Cache = CreateCache(Preview);
        }

        public string Root { get; }
        public StubPreview Preview { get; }
        public StubWorkspace Workspace { get; }
        public ThumbnailCache Cache { get; }
        public string CacheDirectory => Path.Combine(_paths.CacheDirectory, "Thumbnails", "v1");

        public ThumbnailCacheRequest Request(char hash = 'A', int maximumDimension = 64) =>
            new(Workspace, new("Texture/logo.dds"), new(new string(hash, 64)), maximumDimension);

        public ThumbnailCache CreateCache(StubPreview preview) => new(
            _paths, new PathSecurity(), preview, _import, _resize, _options);

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubPreview(TimeSpan? delay = null) : IDdsPreviewService
    {
        private int _calls;
        public int CallCount => _calls;
        public async Task<DdsPreviewResult> CreateAsync(
            DdsPreviewRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay.Value, cancellationToken);
            }
            return DdsPreviewResult.Success(Metadata, new(128, 64, [1]));
        }
    }

    private sealed class StubImport : IImageImportService
    {
        public Task<ImageImportResult> ImportAsync(ImageImportRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ImageImportResult> ImportMemoryAsync(
            ImageImportMemoryRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ImageImportResult.Success(Image(128, 64)));
    }

    private sealed class StubResize : IImageResizeService
    {
        public Task<ImageResizeResult> ResizeAsync(
            ImageResizeRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ImageResizeResult.Success(Image(request.TargetWidth, request.TargetHeight)));
    }

    private static InternalImage Image(int width, int height) => new(
        width, height, width * 4, Enumerable.Repeat((byte)255, width * height * 4).ToArray(),
        new(ImageSourceFormat.Png, 128, 64, ImageSourceOrientation.Normal, true, false));

    private static DdsMetadata Metadata { get; } = new(
        128, 64, null, 1, 1, DdsFormat.BC3, DdsFormatSupport.Known, "DXT5", null,
        DdsHeaderType.Legacy, true, true, DdsAlphaMode.Interpolated, DdsColorSpace.Unknown,
        DdsResourceDimension.Texture2D, false, 1, 128, 128);

    private sealed class StubWorkspace : IProjectArchiveWorkspace
    {
        public StubWorkspace(string root)
        {
            var secure = new StubSecureWorkspace(root);
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
        public StubSecureWorkspace(string root)
        {
            Id = Guid.NewGuid().ToString("N");
            Paths = new(root, Path.Combine(root, "Working"), Path.Combine(root, "Extracted"),
                Path.Combine(root, "BuildOutput"));
            Directory.CreateDirectory(Paths.WorkingDirectory);
            Directory.CreateDirectory(Paths.ExtractedDirectory);
            Directory.CreateDirectory(Paths.BuildOutputDirectory);
        }
        public string Id { get; }
        public SecureWorkspacePaths Paths { get; }
        public string ResolveRelativePath(string relativePath) => Path.GetFullPath(Path.Combine(Paths.RootDirectory, relativePath));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

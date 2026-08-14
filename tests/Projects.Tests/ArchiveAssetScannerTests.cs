using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Assets;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Infrastructure.Paths;
using AuditionModStudio.Projects;

namespace Projects.Tests;

public sealed class ArchiveAssetScannerTests
{
    [Fact]
    public async Task Empty_directory_returns_empty_catalog()
    {
        await using var context = new ScanContext();

        var result = await context.ScanAsync();

        var catalog = AssertCatalog(result);
        Assert.Empty(catalog.Assets);
        Assert.Equal(0, catalog.DirectoryCount);
        Assert.Equal(0, catalog.TotalByteSize);
    }

    [Fact]
    public async Task Nested_assets_are_classified_with_metadata_and_stable_identity()
    {
        await using var context = new ScanContext();
        context.Write("model/pirate/dcn_floor.dds", "dds");
        context.Write("images/a.png", "png-data");
        context.Write("tables/data.slk", "slk");
        context.Write("motion/clip.rgm", "rgm");
        context.Write("misc/read me.xyz", "other");

        var catalog = AssertCatalog(await context.ScanAsync());

        Assert.Equal(5, catalog.TotalFileCount);
        Assert.Equal(6, catalog.DirectoryCount);
        Assert.Equal(1, catalog.Count(ArchiveAssetKind.Dds));
        Assert.Equal(1, catalog.Count(ArchiveAssetKind.Png));
        Assert.Equal(1, catalog.Count(ArchiveAssetKind.Slk));
        Assert.Equal(1, catalog.Count(ArchiveAssetKind.Rgm));
        Assert.Equal(1, catalog.Count(ArchiveAssetKind.Other));
        var texture = Assert.IsType<TextureAsset>(catalog.Assets.Single(asset => asset.AssetKind == ArchiveAssetKind.Dds));
        Assert.Equal(Path.Combine("model", "pirate", "dcn_floor.dds"), texture.RelativePath);
        Assert.Equal(texture.RelativePath, texture.Identity);
        Assert.Equal("dcn_floor.dds", texture.FileName);
        Assert.Equal(".dds", texture.Extension);
        Assert.Equal(Path.Combine("model", "pirate"), texture.DirectoryRelativePath);
        Assert.Equal(3, texture.FileSize);
        Assert.Matches("^[0-9a-f]{64}$", texture.Sha256);
    }

    [Fact]
    public async Task Duplicate_filenames_in_different_directories_remain_distinct()
    {
        await using var context = new ScanContext();
        context.Write("one/same.dds", "1");
        context.Write("two/same.dds", "2");

        var catalog = AssertCatalog(await context.ScanAsync());

        Assert.Equal(2, catalog.Assets.Select(asset => asset.Identity).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Unicode_spaces_and_deep_nesting_are_supported()
    {
        await using var context = new ScanContext("Dự án có khoảng trắng");
        var relativePath = Path.Combine("nhân vật", "tầng 1", "a", "b", "c", "ảnh lạ.DDS");
        context.Write(relativePath, "content");

        var asset = Assert.Single(AssertCatalog(await context.ScanAsync()).Assets);

        Assert.Equal(relativePath, asset.RelativePath);
        Assert.Equal(ArchiveAssetKind.Dds, asset.AssetKind);
    }

    [Fact]
    public async Task Missing_extracted_directory_returns_structured_failure()
    {
        await using var context = new ScanContext();
        Directory.Delete(context.ExtractedRoot, true);

        var result = await context.ScanAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(ArchiveAssetScanFailureReason.ExtractedDirectoryMissing, result.FailureReason);
    }

    [Fact]
    public async Task Cancellation_returns_structured_failure()
    {
        await using var context = new ScanContext();
        context.Write("asset.bin", new string('x', 1024));
        using var source = new CancellationTokenSource();
        source.Cancel();

        var result = await context.ScanAsync(source.Token);

        Assert.Equal(ArchiveAssetScanFailureReason.Cancelled, result.FailureReason);
    }

    [Fact]
    public async Task Ordering_is_ordinal_and_deterministic()
    {
        await using var context = new ScanContext();
        context.Write("z/file.bin", "z");
        context.Write("A/file.bin", "a");
        context.Write("m/file.bin", "m");

        var first = AssertCatalog(await context.ScanAsync()).Assets.Select(asset => asset.RelativePath).ToArray();
        var second = AssertCatalog(await context.ScanAsync()).Assets.Select(asset => asset.RelativePath).ToArray();

        Assert.Equal(first.OrderBy(path => path, StringComparer.Ordinal), first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Inventory_and_progress_are_accurate()
    {
        await using var context = new ScanContext();
        context.Write("a.dds", "123");
        context.Write("nested/b.unknown", "12345");
        var progress = new SynchronousProgress<ArchiveAssetScanProgress>();

        var catalog = AssertCatalog(await context.Scanner.ScanAsync(context.Workspace, progress));

        Assert.Equal(8, catalog.TotalByteSize);
        Assert.Equal(2, catalog.TotalFileCount);
        Assert.Equal(1, catalog.DirectoryCount);
        Assert.Equal(2, progress.Values[^1].FilesDiscovered);
        Assert.Equal(2, progress.Values[^1].ProcessedCount);
    }

    [Fact]
    public async Task Concurrent_scans_have_no_shared_state()
    {
        await using var first = new ScanContext("first");
        await using var second = new ScanContext("second");
        first.Write("a.dds", "a");
        second.Write("b.png", "bb");

        var results = await Task.WhenAll(first.ScanAsync(), second.ScanAsync());

        Assert.Equal("a.dds", AssertCatalog(results[0]).Assets.Single().FileName);
        Assert.Equal("b.png", AssertCatalog(results[1]).Assets.Single().FileName);
    }

    [Fact]
    public async Task Reparse_point_is_rejected_when_platform_allows_creation()
    {
        await using var context = new ScanContext();
        var target = Path.Combine(context.Root, "target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(context.ExtractedRoot, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var result = await context.ScanAsync();

        Assert.Equal(ArchiveAssetScanFailureReason.ReparsePointRejected, result.FailureReason);
    }

    private static ArchiveAssetCatalog AssertCatalog(ArchiveAssetScanResult result)
    {
        Assert.True(result.IsSuccess, result.ErrorCode);
        return Assert.IsType<ArchiveAssetCatalog>(result.Catalog);
    }

    private sealed class ScanContext : IAsyncDisposable
    {
        public ScanContext(string? name = null)
        {
            Root = Path.Combine(Path.GetTempPath(), "AuditionModStudio", nameof(ArchiveAssetScannerTests), name ?? Guid.NewGuid().ToString("N"));
            ExtractedRoot = Path.Combine(Root, "Extracted", "content");
            Directory.CreateDirectory(ExtractedRoot);
            var secureWorkspace = new TestSecureWorkspace(Root);
            Workspace = new TestProjectWorkspace(secureWorkspace);
            Scanner = new ArchiveAssetScanner(new PathSecurity());
        }

        public string Root { get; }

        public string ExtractedRoot { get; }

        public TestProjectWorkspace Workspace { get; }

        public ArchiveAssetScanner Scanner { get; }

        public void Write(string relativePath, string content)
        {
            var path = Path.Combine(ExtractedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public Task<ArchiveAssetScanResult> ScanAsync(CancellationToken cancellationToken = default) =>
            Scanner.ScanAsync(Workspace, cancellationToken: cancellationToken);

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestProjectWorkspace(TestSecureWorkspace secureWorkspace) : IProjectArchiveWorkspace
    {
        public ProjectArchiveWorkspaceDescriptor Descriptor { get; } = new(
            1,
            Guid.NewGuid(),
            "Project",
            "workspace",
            new("template", "1", new string('0', 64), ArchiveEngineType.AcvTool5, "audition_vn"),
            "Working/archive.ab",
            "Extracted",
            "BuildOutput",
            null,
            "project.json",
            new string('0', 64),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            ProjectArchiveWorkspaceState.Ready);

        public ArchiveWorkspace ArchiveWorkspace { get; } = new(secureWorkspace, "archive.ab", "content");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestSecureWorkspace(string root) : ISecureWorkspace
    {
        public string Id => "test";

        public SecureWorkspacePaths Paths { get; } = new(
            root,
            Path.Combine(root, "Working"),
            Path.Combine(root, "Extracted"),
            Path.Combine(root, "BuildOutput"));

        public string ResolveRelativePath(string relativePath) => Path.Combine(root, relativePath);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }
}

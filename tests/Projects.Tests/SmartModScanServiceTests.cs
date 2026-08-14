using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Assets;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Infrastructure.Paths;
using AuditionModStudio.Projects;

namespace Projects.Tests;

public sealed class SmartModScanServiceTests
{
    [Fact]
    public async Task Exact_path_maps_and_propagates_manifest_and_real_dds_metadata_without_pixels()
    {
        await using var context = new ScanContext(CreateManifest(
            Slot("logo_main", "Texture/Login/logo.dds", "Logo đăng nhập")));
        context.Write("Texture/Login/logo.dds", "source");

        var result = await context.ScanAsync();

        Assert.True(result.Succeeded, result.DiagnosticCode);
        var texture = Assert.Single(Assert.Single(result.Groups).Textures);
        Assert.False(texture.UnknownSemantics);
        Assert.False(texture.CanBeLabeled);
        Assert.Equal("logo_main", texture.ManifestResolution.Slot!.Id.Value);
        Assert.Equal("Logo đăng nhập", texture.ManifestResolution.DisplayName);
        Assert.Equal("branding", texture.ManifestResolution.Slot.Category.Value);
        Assert.Equal(["login", "logo"], texture.ManifestResolution.Slot.Tags.ToArray());
        Assert.True(texture.ManifestResolution.Slot.PreviewEnabled);
        Assert.True(texture.ManifestResolution.Slot.Editable);
        Assert.Equal("alpha_aware", texture.ManifestResolution.Slot.RecommendedEditMode.Value);
        Assert.Equal(DdsFormat.BC3, texture.Metadata.Format);
        Assert.DoesNotContain(typeof(SmartTextureAsset).GetProperties(),
            property => property.PropertyType == typeof(InternalImage));
        Assert.Empty(result.MissingManifestSlots);
    }

    [Fact]
    public async Task Filename_only_and_different_folder_do_not_map_or_drop_assets()
    {
        await using var context = new ScanContext(CreateManifest(Slot("logo_main", "expected/logo.dds")));
        context.Write("one/logo.dds", "one");
        context.Write("two/logo.dds", "two");

        var result = await context.ScanAsync();
        var textures = result.Groups.SelectMany(group => group.Textures).ToArray();

        Assert.True(result.Succeeded);
        Assert.Equal(2, textures.Length);
        Assert.All(textures, texture =>
        {
            Assert.True(texture.UnknownSemantics);
            Assert.True(texture.CanBeLabeled);
            Assert.Equal("logo.dds", texture.ManifestResolution.DisplayName);
            Assert.Null(texture.ManifestResolution.Slot);
        });
        Assert.Equal("logo_main", Assert.Single(result.MissingManifestSlots).Id.Value);
    }

    [Fact]
    public async Task Windows_case_semantics_map_unicode_spaces_and_deep_path()
    {
        const string declared = "Nhân vật/Thư mục sâu/Logo A.dds";
        await using var context = new ScanContext(CreateManifest(Slot("deep_logo", declared)));
        context.Write("nhân vật/thư mục sâu/logo a.DDS", "data");

        var texture = Assert.Single(Assert.Single((await context.ScanAsync()).Groups).Textures);

        Assert.Equal("deep_logo", texture.ManifestResolution.Slot!.Id.Value);
    }

    [Fact]
    public async Task No_manifest_and_empty_manifest_use_raw_fallback_without_guesses()
    {
        await using var noManifest = new ScanContext(null);
        noManifest.Write("raw/unknown.dds", "data");
        await using var emptyManifest = new ScanContext(CreateManifest());
        emptyManifest.Write("raw/other.dds", "data");

        var results = await Task.WhenAll(noManifest.ScanAsync(), emptyManifest.ScanAsync());

        Assert.All(results, result =>
        {
            var texture = Assert.Single(Assert.Single(result.Groups).Textures);
            Assert.True(texture.UnknownSemantics);
            Assert.Null(texture.ManifestResolution.Slot);
            Assert.Empty(result.MissingManifestSlots);
        });
    }

    [Fact]
    public async Task Empty_extracted_tree_returns_deterministic_empty_collections()
    {
        await using var context = new ScanContext(CreateManifest(Slot("missing", "missing.dds")));

        var result = await context.ScanAsync();

        Assert.True(result.Succeeded);
        Assert.Empty(result.Groups);
        Assert.Equal(0, result.TextureCount);
        Assert.Equal("missing", Assert.Single(result.MissingManifestSlots).Id.Value);
        Assert.NotNull(result.ObservedCatalog);
    }

    [Theory]
    [InlineData(false, true, SmartModScanFailureReason.UnknownGame)]
    [InlineData(true, false, SmartModScanFailureReason.UnknownMod)]
    public async Task Unknown_game_and_mod_return_structured_failure(
        bool knownGame,
        bool knownMod,
        SmartModScanFailureReason expected)
    {
        await using var context = new ScanContext(null, knownGame, knownMod);

        var result = await context.ScanAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.FailureReason);
        Assert.Null(result.ObservedCatalog);
    }

    [Fact]
    public async Task Cancellation_is_structured_and_does_not_publish_partial_result()
    {
        await using var context = new ScanContext(null);
        context.Write("a.dds", "data");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await context.ScanAsync(cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.True(result.Cancelled);
        Assert.Equal(SmartModScanFailureReason.Cancelled, result.FailureReason);
        Assert.Null(result.ObservedCatalog);
        Assert.Empty(result.Groups);
    }

    [Fact]
    public async Task Metadata_failure_is_structured_per_asset_and_atomic()
    {
        await using var context = new ScanContext(null) { MetadataFailure = true };
        context.Write("bad.dds", "malformed");

        var result = await context.ScanAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(SmartModScanFailureReason.DdsMetadataReadFailed, result.FailureReason);
        Assert.EndsWith("bad.dds", result.FailedRelativePath, StringComparison.Ordinal);
        Assert.Null(result.ObservedCatalog);
    }

    [Fact]
    public async Task Ambiguous_slot_mapping_is_rejected_even_if_dependency_breaks_its_invariant()
    {
        await using var context = new ScanContext(
            CreateManifest(Slot("shared", "a.dds")),
            ambiguousManifestResolver: true);
        context.Write("a.dds", "a");
        context.Write("b.dds", "b");

        var result = await context.ScanAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(SmartModScanFailureReason.AmbiguousMapping, result.FailureReason);
        Assert.Null(result.ObservedCatalog);
    }

    [Fact]
    public async Task Progress_reports_real_phases_and_completed_texture_count()
    {
        await using var context = new ScanContext(null);
        context.Write("a.dds", "data");
        var progress = new SynchronousProgress<SmartModScanProgress>();

        var result = await context.ScanAsync(progress: progress);

        Assert.True(result.Succeeded);
        Assert.Contains(progress.Values, item => item.Phase == SmartModScanPhase.ScanningAssets);
        Assert.Contains(progress.Values, item => item.Phase == SmartModScanPhase.ReadingMetadata);
        Assert.Contains(progress.Values, item => item.Phase == SmartModScanPhase.ResolvingManifest);
        var completed = Assert.Single(progress.Values, item => item.Phase == SmartModScanPhase.Completed);
        Assert.Equal(1, completed.TotalTextures);
        Assert.Equal(1, completed.CompletedTextures);
    }

    [Fact]
    public async Task Output_order_grouping_concurrency_and_source_immutability_are_deterministic()
    {
        await using var context = new ScanContext(null);
        context.Write("z/b.dds", "b");
        context.Write("a/c.dds", "c");
        context.Write("a/a.dds", "a");
        var before = context.Hashes();

        var results = await Task.WhenAll(context.ScanAsync(), context.ScanAsync());

        Assert.All(results, result =>
        {
            Assert.Equal(["a", "z"], result.Groups.Select(group => group.DirectoryRelativePath));
            Assert.Equal(["a/a.dds", "a/c.dds", "z/b.dds"], result.Groups
                .SelectMany(group => group.Textures)
                .Select(texture => texture.Asset.RelativePath.Replace('\\', '/')));
        });
        Assert.Equal(before, context.Hashes());
        Assert.True(context.ScannerCallCount >= 2);
    }

    private static TextureSlot Slot(string id, string path, string displayName = "Texture") => new(
        new(id), new(path), displayName, new("branding"), "Description", ["login", "logo"],
        true, true, new("alpha_aware"));

    private static TextureManifest CreateManifest(params TextureSlot[] slots) =>
        TextureManifest.Create(new("audition"), new("login_mod"), slots).Manifest!;

    private sealed class ScanContext : IAsyncDisposable
    {
        private readonly CountingScanner _scanner;
        private readonly StubMetadataReader _metadata = new();
        private readonly SmartModScanService _service;

        public ScanContext(
            TextureManifest? manifest,
            bool knownGame = true,
            bool knownMod = true,
            bool ambiguousManifestResolver = false)
        {
            Root = Path.Combine(Path.GetTempPath(), "AuditionModStudio", nameof(SmartModScanServiceTests), Guid.NewGuid().ToString("N"));
            ExtractedRoot = Path.Combine(Root, "Extracted", "content");
            Directory.CreateDirectory(ExtractedRoot);
            Directory.CreateDirectory(Path.Combine(Root, "BuildOutput"));
            Workspace = new TestProjectWorkspace(new TestSecureWorkspace(Root));
            _scanner = new CountingScanner(new ArchiveAssetScanner(new PathSecurity()));
            _service = new(
                new StubGameCatalog(knownGame),
                new StubModCatalog(knownMod),
                new StubManifestCatalog(manifest, ambiguousManifestResolver),
                _scanner,
                _metadata,
                new PathSecurity());
        }

        public string Root { get; }
        public string ExtractedRoot { get; }
        public TestProjectWorkspace Workspace { get; }
        public bool MetadataFailure { set => _metadata.Fail = value; }
        public int ScannerCallCount => _scanner.CallCount;

        public void Write(string relativePath, string content)
        {
            var path = Path.Combine(ExtractedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public Task<SmartModScanResult> ScanAsync(
            CancellationToken cancellationToken = default,
            IProgress<SmartModScanProgress>? progress = null) =>
            _service.ScanAsync(new(new("audition"), new("login_mod"), Workspace), progress, cancellationToken);

        public string[] Hashes() => Directory.EnumerateFiles(ExtractedRoot, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
            .ToArray();

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingScanner(IArchiveAssetScanner inner) : IArchiveAssetScanner
    {
        private int _callCount;
        public int CallCount => _callCount;
        public Task<ArchiveAssetScanResult> ScanAsync(IProjectArchiveWorkspace workspace,
            IProgress<ArchiveAssetScanProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return inner.ScanAsync(workspace, progress, cancellationToken);
        }
    }

    private sealed class StubGameCatalog(bool known) : IGameCatalog
    {
        private readonly GameDefinition _game = new(new("audition"), "Audition");
        public ImmutableArray<GameDefinition> GetGames() => known ? [_game] : [];
        public bool TryGetGame(GameId gameId, [NotNullWhen(true)] out GameDefinition? game)
        {
            game = known && gameId == _game.Id ? _game : null;
            return game is not null;
        }
    }

    private sealed class StubModCatalog(bool known) : IModCatalog
    {
        public ImmutableArray<ModDefinition> GetMods(GameId gameId) => [];
        public bool TryGetMod(GameId gameId, ModId modId, [NotNullWhen(true)] out ModDefinition? mod)
        {
            mod = known ? null! : null;
            return known;
        }
    }

    private sealed class StubManifestCatalog(
        TextureManifest? manifest,
        bool ambiguousResolver) : ITextureManifestCatalog
    {
        public bool TryGetManifest(GameId gameId, ModId modId, [NotNullWhen(true)] out TextureManifest? found)
        {
            found = manifest is not null && manifest.GameId == gameId && manifest.ModId == modId ? manifest : null;
            return found is not null;
        }

        public TextureManifestResolution Resolve(GameId gameId, ModId modId, ModRelativePath relativePath)
        {
            var slot = ambiguousResolver
                ? manifest?.Slots.FirstOrDefault()
                : manifest?.Slots.FirstOrDefault(item =>
                    StringComparer.OrdinalIgnoreCase.Equals(item.RelativePath.Value, relativePath.Value));
            return slot is null
                ? new(relativePath, Path.GetFileName(relativePath.Value), true, null)
                : new(relativePath, slot.DisplayName, false, slot);
        }
    }

    private sealed class StubMetadataReader : IDdsMetadataReader
    {
        public bool Fail { get; set; }
        public Task<DdsMetadataReadResult> ReadAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(Fail
                ? DdsMetadataReadResult.Failure(DdsMetadataFailureReason.CorruptHeader, "DDS_BAD")
                : DdsMetadataReadResult.Success(Metadata));
    }

    private static DdsMetadata Metadata { get; } = new(
        128, 64, null, 1, 1, DdsFormat.BC3, DdsFormatSupport.Known, "DXT5", null,
        DdsHeaderType.Legacy, true, true, DdsAlphaMode.Interpolated, DdsColorSpace.Unknown,
        DdsResourceDimension.Texture2D, false, 1, 128, 128);

    private sealed class TestProjectWorkspace(TestSecureWorkspace secureWorkspace) : IProjectArchiveWorkspace
    {
        public ProjectArchiveWorkspaceDescriptor Descriptor { get; } = new(
            1, Guid.NewGuid(), "Project", "workspace",
            new("template", "1", new string('0', 64), ArchiveEngineType.AcvTool5, "audition_vn"),
            "Working/archive.ab", "Extracted", "BuildOutput", null, "project.json", new string('0', 64),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ProjectArchiveWorkspaceState.Ready);
        public ArchiveWorkspace ArchiveWorkspace { get; } = new(secureWorkspace, "archive.ab", "content");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestSecureWorkspace(string root) : ISecureWorkspace
    {
        public string Id => "test";
        public SecureWorkspacePaths Paths { get; } = new(root, Path.Combine(root, "Working"),
            Path.Combine(root, "Extracted"), Path.Combine(root, "BuildOutput"));
        public string ResolveRelativePath(string relativePath) => Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }
}

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Mods;

namespace Core.Tests;

public sealed class TextureManifestTests
{
    [Theory]
    [InlineData("logo_main", true)]
    [InlineData("a", true)]
    [InlineData("Logo", false)]
    [InlineData("", false)]
    [InlineData("white space", false)]
    [InlineData("bad/path", false)]
    public void Texture_slot_id_uses_stable_semantic_grammar(string value, bool expected)
    {
        Assert.Equal(expected, TextureSlotId.TryCreate(value, out _));
    }

    [Fact]
    public void Texture_slot_id_enforces_length_boundary()
    {
        Assert.True(TextureSlotId.TryCreate(new string('a', TextureSlotId.MaximumLength), out _));
        Assert.False(TextureSlotId.TryCreate(new string('a', TextureSlotId.MaximumLength + 1), out _));
    }

    [Fact]
    public void Slot_is_immutable_and_preserves_all_roadmap_metadata()
    {
        var tags = new List<string> { "Logo", "Đăng nhập" };
        var slot = CreateSlot(tags: tags);
        tags[0] = "Changed";

        Assert.Equal("Logo chính", slot.DisplayName);
        Assert.Equal("branding", slot.Category.Value);
        Assert.Equal("Mô tả Unicode", slot.Description);
        Assert.Equal(["Logo", "Đăng nhập"], slot.Tags.ToArray());
        Assert.True(slot.PreviewEnabled);
        Assert.True(slot.Editable);
        Assert.Equal("alpha_aware", slot.RecommendedEditMode.Value);
    }

    [Theory]
    [InlineData("../texture/a.dds")]
    [InlineData("C:\\texture\\a.dds")]
    [InlineData("\\\\server\\share\\a.dds")]
    [InlineData("/absolute/a.dds")]
    [InlineData("texture/a.dds:stream")]
    [InlineData("texture/CON.dds")]
    [InlineData("texture/com1.dds")]
    public void Unsafe_relative_paths_are_rejected(string path)
    {
        Assert.False(ModRelativePath.TryCreate(path, out _));
    }

    [Fact]
    public void Non_dds_asset_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => CreateSlot(path: "texture/a.png"));
    }

    [Fact]
    public void Whitespace_only_description_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new TextureSlot(
            new("logo_main"), new("texture/logo.dds"), "Logo", new("branding"), "   ",
            [], true, true, new("alpha_aware")));
    }

    [Fact]
    public void Manifest_belongs_to_explicit_game_and_mod_and_orders_by_slot_id()
    {
        var result = TextureManifest.Create(
            new("audition"),
            new("login_mod"),
            [CreateSlot("z_slot", "texture/z.dds"), CreateSlot("a_slot", "texture/a.dds")]);

        Assert.True(result.Succeeded);
        Assert.Equal("audition", result.Manifest!.GameId.Value);
        Assert.Equal("login_mod", result.Manifest.ModId.Value);
        Assert.Equal(["a_slot", "z_slot"], result.Manifest.Slots.Select(slot => slot.Id.Value));
    }

    [Fact]
    public void Invalid_game_and_mod_ids_fail_atomically()
    {
        var result = TextureManifest.Create(default, default, [CreateSlot()]);

        Assert.False(result.Succeeded);
        Assert.Null(result.Manifest);
        Assert.Contains(result.Issues, issue => issue.Reason == TextureManifestValidationFailureReason.InvalidGameId);
        Assert.Contains(result.Issues, issue => issue.Reason == TextureManifestValidationFailureReason.InvalidModId);
    }

    [Fact]
    public void Duplicate_slot_id_is_rejected_atomically()
    {
        var result = TextureManifest.Create(
            new("audition"), new("login_mod"),
            [CreateSlot("logo_main", "texture/a.dds"), CreateSlot("logo_main", "texture/b.dds")]);

        Assert.False(result.Succeeded);
        Assert.Null(result.Manifest);
        Assert.Contains(result.Issues, issue => issue.Reason == TextureManifestValidationFailureReason.DuplicateSlotId);
    }

    [Theory]
    [InlineData("texture/a.dds", "texture/a.dds")]
    [InlineData("Texture/A.dds", "texture/a.DDS")]
    public void Duplicate_or_windows_colliding_asset_identity_is_rejected(string first, string second)
    {
        var result = TextureManifest.Create(
            new("audition"), new("login_mod"),
            [CreateSlot("first", first), CreateSlot("second", second)]);

        Assert.False(result.Succeeded);
        Assert.Null(result.Manifest);
        Assert.Contains(result.Issues, issue => issue.Reason == TextureManifestValidationFailureReason.DuplicateAssetIdentity);
    }

    [Fact]
    public async Task Immutable_manifest_supports_concurrent_reads()
    {
        var manifest = TextureManifest.Create(
            new("audition"), new("login_mod"),
            Enumerable.Range(0, 32).Select(index => CreateSlot($"slot_{index:D2}", $"texture/{index:D2}.dds"))).Manifest!;

        var reads = Enumerable.Range(0, 64).Select(_ => Task.Run(() => manifest.Slots.Select(slot => slot.Id.Value).ToArray()));
        var results = await Task.WhenAll(reads);

        Assert.All(results, result => Assert.Equal(results[0], result));
    }

    [Fact]
    public void Catalog_rejects_unknown_mod_and_duplicate_manifest_without_partial_publication()
    {
        var modCatalog = CreateModCatalog();
        var known = CreateManifest();
        var unknown = TextureManifest.Create(new("audition"), new("unknown"), [CreateSlot()]).Manifest!;

        var unknownResult = TextureManifestCatalog.Create([known, unknown], modCatalog);
        var duplicateResult = TextureManifestCatalog.Create([known, known], modCatalog);

        Assert.False(unknownResult.Succeeded);
        Assert.Null(unknownResult.Catalog);
        Assert.Contains(unknownResult.Issues, issue => issue.Reason == TextureManifestValidationFailureReason.UnknownMod);
        Assert.False(duplicateResult.Succeeded);
        Assert.Null(duplicateResult.Catalog);
        Assert.Contains(duplicateResult.Issues, issue => issue.Reason == TextureManifestValidationFailureReason.DuplicateManifest);
    }

    [Fact]
    public void Catalog_resolves_declared_metadata_and_falls_back_to_raw_filename_path()
    {
        var result = TextureManifestCatalog.Create([CreateManifest()], CreateModCatalog());
        var catalog = Assert.IsAssignableFrom<ITextureManifestCatalog>(result.Catalog);

        var matched = catalog.Resolve(new("audition"), new("login_mod"), new("TEXTURE/LOGO.DDS"));
        var fallback = catalog.Resolve(new("audition"), new("login_mod"), new("unknown/raw_name.dds"));

        Assert.False(matched.UsedFallback);
        Assert.Equal("Logo chính", matched.DisplayName);
        Assert.NotNull(matched.Slot);
        Assert.True(fallback.UsedFallback);
        Assert.Equal("raw_name.dds", fallback.DisplayName);
        Assert.Equal("unknown/raw_name.dds", fallback.RelativePath.Value);
        Assert.Null(fallback.Slot);
    }

    [Fact]
    public void Display_name_does_not_change_semantic_or_asset_identity()
    {
        var first = CreateSlot(displayName: "Tên thứ nhất");
        var second = CreateSlot(displayName: "Tên thứ hai");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.RelativePath, second.RelativePath);
        Assert.NotEqual(first.DisplayName, second.DisplayName);
    }

    [Fact]
    public void Manifest_contains_no_dds_runtime_metadata_or_archive_engine_inference()
    {
        var properties = typeof(TextureSlot).GetProperties().Select(property => property.Name).ToImmutableHashSet();

        Assert.DoesNotContain("Format", properties);
        Assert.DoesNotContain("Width", properties);
        Assert.DoesNotContain("Height", properties);
        Assert.DoesNotContain("MipLevelCount", properties);
        Assert.DoesNotContain("ArchiveEngine", properties);
    }

    private static TextureSlot CreateSlot(
        string id = "logo_main",
        string path = "texture/logo.dds",
        string displayName = "Logo chính",
        IEnumerable<string>? tags = null) => new(
            new(id),
            new(path),
            displayName,
            new("branding"),
            "Mô tả Unicode",
            tags ?? ["Logo", "Đăng nhập"],
            true,
            true,
            new("alpha_aware"));

    private static TextureManifest CreateManifest() => TextureManifest.Create(
        new("audition"), new("login_mod"), [CreateSlot()]).Manifest!;

    private static IModCatalog CreateModCatalog()
    {
        var game = new GameDefinition(new("audition"), "Audition");
        var gameCatalog = new StubGameCatalog(game);
        const string region = "audition_vn";
        var mod = new ModDefinition(
            new("login_mod"), game.Id, "Login", new("interface"), new("covers/login.png"), "Description",
            new("archive", "015.ab", "templates/015.ab", ArchiveEngineType.AcvTool5, region, "015", "1"),
            ModKeydatStrategy.ReuseOrGenerate, new("data"), "Compatible");
        return ModCatalog.Create([mod], gameCatalog, new StubRegionResolver(region)).Catalog!;
    }

    private sealed class StubGameCatalog(GameDefinition game) : IGameCatalog
    {
        public ImmutableArray<GameDefinition> GetGames() => [game];
        public bool TryGetGame(GameId gameId, [NotNullWhen(true)] out GameDefinition? definition)
        {
            definition = gameId == game.Id ? game : null;
            return definition is not null;
        }
    }

    private sealed class StubRegionResolver(string id) : IGameRegionProfileResolver
    {
        public bool TryResolve(string regionProfileId, out GameRegionProfile profile)
        {
            if (regionProfileId == id)
            {
                profile = new(id, "AuditionVN", "1");
                return true;
            }

            profile = null!;
            return false;
        }
    }
}

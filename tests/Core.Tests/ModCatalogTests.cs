using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Mods;

namespace Core.Tests;

public sealed class ModCatalogTests
{
    private static readonly GameId AuditionId = new("audition");
    private static readonly IGameCatalog Games = GameCatalog.CreateBuiltIn();
    private static readonly IGameRegionProfileResolver Regions = new TestRegionProfiles();

    [Theory]
    [InlineData("login_screen")]
    [InlineData("interface-2")]
    [InlineData("2d_texture")]
    public void Stable_mod_id_accepts_machine_friendly_values(string value)
    {
        Assert.True(ModId.TryCreate(value, out var id));
        Assert.Equal(value, id.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("LoginScreen")]
    [InlineData("login screen")]
    [InlineData("login/screen")]
    [InlineData("màn_hình")]
    public void Invalid_mod_id_is_rejected(string? value)
    {
        Assert.False(ModId.TryCreate(value, out var id));
        Assert.False(id.IsValid);
    }

    [Fact]
    public void Mod_id_length_boundary_matches_shared_game_id_policy()
    {
        var maximum = new string('a', ModId.MaximumLength);
        var tooLong = maximum + "a";

        Assert.True(ModId.TryCreate(maximum, out _));
        Assert.False(ModId.TryCreate(tooLong, out _));
        Assert.Equal(GameId.MaximumLength, ModId.MaximumLength);
    }

    [Theory]
    [InlineData("Mods/Covers/login.png", "Mods/Covers/login.png")]
    [InlineData("Audition\\015.ab", "Audition/015.ab")]
    public void Relative_metadata_paths_are_normalized(string value, string expected)
    {
        var path = new ModRelativePath(value);

        Assert.Equal(expected, path.Value);
    }

    [Theory]
    [InlineData("../015.ab")]
    [InlineData("C:\\Game\\015.ab")]
    [InlineData("Mods//login.png")]
    [InlineData("Mods/./login.png")]
    [InlineData("Mods/ /login.png")]
    [InlineData("Mods/login.png.")]
    [InlineData("Mods/login?.png")]
    public void Unsafe_relative_metadata_paths_are_rejected(string value)
    {
        Assert.False(ModRelativePath.TryCreate(value, out _));
    }

    [Fact]
    public void Mod_definition_is_immutable_and_preserves_exact_mapping()
    {
        var definition = CreateDefinition();

        Assert.All(
            typeof(ModDefinition).GetProperties(),
            property => Assert.False(property.SetMethod?.IsPublic ?? false));
        Assert.Equal("015.ab", definition.ArchiveTemplate.FileName);
        Assert.Equal("015", definition.ArchiveTemplate.ExpectedExtractFolderName);
        Assert.Equal("1", definition.ArchiveTemplate.TemplateVersion?.Value);
        Assert.Equal(ArchiveEngineType.AcvTool5, definition.ArchiveTemplate.EngineType);
        Assert.Equal("audition_vn", definition.ArchiveTemplate.RegionProfileId);
        Assert.Equal(ModKeydatStrategy.ReuseOrGenerate, definition.KeydatStrategy);
    }

    [Fact]
    public void Unicode_display_metadata_is_supported()
    {
        var definition = CreateDefinition(displayName: "Giao diện đăng nhập Việt Nam");

        Assert.Equal("Giao diện đăng nhập Việt Nam", definition.DisplayName);
    }

    [Fact]
    public void Required_display_metadata_is_rejected_when_empty_or_control_bearing()
    {
        Assert.Throws<ArgumentException>(() => CreateDefinition(displayName: " "));
        Assert.Throws<ArgumentException>(() => CreateDefinition(description: "Injected\nline"));
        Assert.Throws<ArgumentException>(() => CreateDefinition(compatibility: " "));
    }

    [Fact]
    public void Audition_mods_can_be_queried_semantically()
    {
        var catalog = CreateCatalog([CreateDefinition()]);

        var definitions = catalog.GetMods(AuditionId);
        var found = catalog.TryGetMod(AuditionId, new ModId("login_screen"), out var resolved);

        Assert.True(found);
        Assert.Same(Assert.Single(definitions), resolved);
        Assert.Equal("Giao diện đăng nhập", resolved!.DisplayName);
        Assert.NotEqual(resolved.Id.Value, resolved.ArchiveTemplate.FileName);
    }

    [Fact]
    public void Invalid_game_and_mod_queries_are_expected_misses()
    {
        var catalog = CreateCatalog([CreateDefinition()]);

        Assert.Empty(catalog.GetMods(default));
        Assert.False(catalog.TryGetMod(default, new ModId("login_screen"), out _));
        Assert.False(catalog.TryGetMod(AuditionId, default, out _));
        Assert.False(catalog.TryGetMod(new GameId("unknown"), new ModId("login_screen"), out _));
    }

    [Fact]
    public void Cross_game_lookup_does_not_resolve_a_mod_from_another_game()
    {
        var games = CreateGames(AuditionId, new GameId("other_game"));
        var catalog = CreateCatalog([CreateDefinition()], games);

        Assert.False(catalog.TryGetMod(
            new GameId("other_game"),
            new ModId("login_screen"),
            out var resolved));
        Assert.Null(resolved);
    }

    [Fact]
    public void Mod_ids_are_scoped_to_game_and_duplicate_pairs_are_rejected()
    {
        var otherGame = new GameId("other_game");
        var games = CreateGames(AuditionId, otherGame);
        var sameIdDifferentGame = ModCatalog.Create(
            [CreateDefinition(), CreateDefinition(gameId: otherGame)],
            games,
            Regions);
        var duplicatePair = ModCatalog.Create(
            [CreateDefinition(), CreateDefinition(displayName: "Tên khác")],
            games,
            Regions);

        Assert.True(sameIdDifferentGame.Succeeded);
        Assert.False(duplicatePair.Succeeded);
        Assert.Equal(
            ModCatalogValidationFailureReason.DuplicateModId,
            Assert.Single(duplicatePair.Issues).Reason);
    }

    [Fact]
    public void Unknown_game_definition_is_rejected_atomically()
    {
        var result = ModCatalog.Create(
            [CreateDefinition(), CreateDefinition(gameId: new GameId("unknown"), modId: "other")],
            Games,
            Regions);

        Assert.False(result.Succeeded);
        Assert.Null(result.Catalog);
        Assert.Equal(ModCatalogValidationFailureReason.UnknownGameId, Assert.Single(result.Issues).Reason);
    }

    [Fact]
    public void Unknown_region_profile_is_rejected_atomically()
    {
        var result = ModCatalog.Create(
            [CreateDefinition(regionProfileId: "unknown_region")],
            Games,
            Regions);

        Assert.False(result.Succeeded);
        Assert.Null(result.Catalog);
        Assert.Equal(
            ModCatalogValidationFailureReason.UnknownRegionProfile,
            Assert.Single(result.Issues).Reason);
    }

    [Fact]
    public void Catalog_ordering_is_deterministic_by_mod_id()
    {
        var catalog = CreateCatalog(
        [
            CreateDefinition(modId: "zeta"),
            CreateDefinition(modId: "alpha"),
            CreateDefinition(modId: "middle")
        ]);

        Assert.Equal(
            new[] { "alpha", "middle", "zeta" },
            catalog.GetMods(AuditionId).Select(definition => definition.Id.Value));
    }

    [Fact]
    public void Catalog_snapshots_are_immutable()
    {
        var catalog = CreateCatalog([CreateDefinition()]);
        var snapshot = catalog.GetMods(AuditionId);

        var changedCopy = snapshot.Add(CreateDefinition(modId: "other"));

        Assert.Single(snapshot);
        Assert.Equal(2, changedCopy.Length);
        Assert.Single(catalog.GetMods(AuditionId));
    }

    [Fact]
    public async Task Concurrent_catalog_reads_are_consistent()
    {
        var catalog = CreateCatalog([CreateDefinition()]);
        var reads = Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
        {
            var found = catalog.TryGetMod(AuditionId, new ModId("login_screen"), out var definition);
            return (found, definition);
        }));

        var results = await Task.WhenAll(reads);

        Assert.All(results, result => Assert.True(result.found));
        Assert.All(results, result => Assert.Equal("Giao diện đăng nhập", result.definition!.DisplayName));
    }

    [Fact]
    public void Archive_engine_is_explicit_and_never_inferred_from_extension()
    {
        var definition = CreateDefinition(fileName: "custom.acv");

        Assert.Equal(".acv", definition.ArchiveTemplate.Extension);
        Assert.Same(ArchiveEngineType.AcvTool5, definition.ArchiveTemplate.EngineType);
    }

    [Fact]
    public void Country_selection_is_not_exposed_by_mod_definition()
    {
        var propertyNames = typeof(ModDefinition).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(propertyNames, name => name.Contains("Country", StringComparison.Ordinal));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Selection", StringComparison.Ordinal));
        Assert.Equal("audition_vn", CreateDefinition().ArchiveTemplate.RegionProfileId);
    }

    [Fact]
    public void Display_name_does_not_change_identity_or_filesystem_mapping()
    {
        var first = CreateDefinition(displayName: "Tên thứ nhất");
        var second = CreateDefinition(displayName: "Tên thứ hai");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.InstallRelativePath, second.InstallRelativePath);
        Assert.Equal(first.ArchiveTemplate, second.ArchiveTemplate);
    }

    [Fact]
    public void Missing_dependencies_and_definitions_return_structured_failure()
    {
        var missingDefinitions = ModCatalog.Create(null, Games, Regions);
        var missingGames = ModCatalog.Create([], null, Regions);
        var missingRegions = ModCatalog.Create([], Games, null);

        Assert.Equal(
            ModCatalogValidationFailureReason.InvalidDefinition,
            Assert.Single(missingDefinitions.Issues).Reason);
        Assert.Equal(
            ModCatalogValidationFailureReason.InvalidDependency,
            Assert.Single(missingGames.Issues).Reason);
        Assert.Equal(
            ModCatalogValidationFailureReason.InvalidDependency,
            Assert.Single(missingRegions.Issues).Reason);
    }

    [Fact]
    public void Empty_mod_catalog_is_valid_when_roadmap_defines_no_built_in_mod_types()
    {
        var result = ModCatalog.Create([], Games, Regions);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Catalog!.GetMods(AuditionId));
    }

    private static IModCatalog CreateCatalog(
        IEnumerable<ModDefinition?> definitions,
        IGameCatalog? games = null)
    {
        var result = ModCatalog.Create(definitions, games ?? Games, Regions);
        Assert.True(result.Succeeded, string.Join(',', result.Issues.Select(issue => issue.DiagnosticCode)));
        return result.Catalog!;
    }

    private static IGameCatalog CreateGames(params GameId[] ids)
    {
        var result = GameCatalog.Create(ids.Select(id => new GameDefinition(id, id.Value)));
        Assert.True(result.Succeeded);
        return result.Catalog!;
    }

    private static ModDefinition CreateDefinition(
        GameId? gameId = null,
        string modId = "login_screen",
        string displayName = "Giao diện đăng nhập",
        string description = "Tùy chỉnh giao diện đăng nhập Audition.",
        string compatibility = "Tương thích với profile Audition Việt Nam.",
        string fileName = "015.ab",
        string regionProfileId = "audition_vn") => new(
            new ModId(modId),
            gameId ?? AuditionId,
            displayName,
            new ModCategory("interface"),
            new ModRelativePath("Mods/Covers/login-screen.png"),
            description,
            new AuditionArchiveTemplate(
                "audition-login-template",
                fileName,
                $"Templates/{fileName}",
                ArchiveEngineType.AcvTool5,
                regionProfileId,
                "015",
                "1",
                new string('A', 64),
                "audition-vn-current"),
            ModKeydatStrategy.ReuseOrGenerate,
            new ModRelativePath($"Data/{fileName}"),
            compatibility);

    private sealed class TestRegionProfiles : IGameRegionProfileResolver
    {
        public bool TryResolve(string regionProfileId, out GameRegionProfile profile)
        {
            if (string.Equals(regionProfileId, GameRegionProfile.AuditionVietnam.RegionId, StringComparison.Ordinal))
            {
                profile = GameRegionProfile.AuditionVietnam;
                return true;
            }

            profile = null!;
            return false;
        }
    }
}

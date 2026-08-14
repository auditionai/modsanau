using AuditionModStudio.Core.Games;
using AuditionModStudio.Mods;

namespace Core.Tests;

public sealed class GameCatalogTests
{
    [Fact]
    public void Built_in_catalog_contains_audition_with_stable_id()
    {
        var catalog = GameCatalog.CreateBuiltIn();

        var found = catalog.TryGetGame(new GameId("audition"), out var game);

        Assert.True(found);
        var resolved = Assert.IsType<GameDefinition>(game);
        Assert.Equal("audition", resolved.Id.Value);
        Assert.Equal("Audition", resolved.DisplayName);
    }

    [Fact]
    public void Unknown_game_id_returns_false_without_throwing()
    {
        var catalog = GameCatalog.CreateBuiltIn();

        var found = catalog.TryGetGame(new GameId("unknown_game"), out var game);

        Assert.False(found);
        Assert.Null(game);
    }

    [Theory]
    [InlineData("audition")]
    [InlineData("game_2")]
    [InlineData("game-vn")]
    [InlineData("2game")]
    public void Stable_game_id_accepts_machine_friendly_values(string value)
    {
        Assert.True(GameId.TryCreate(value, out var gameId));
        Assert.Equal(value, gameId.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Audition")]
    [InlineData("audition vn")]
    [InlineData("audition/vn")]
    [InlineData("đột_kích")]
    public void Invalid_game_id_is_rejected(string? value)
    {
        Assert.False(GameId.TryCreate(value, out var gameId));
        Assert.False(gameId.IsValid);
    }

    [Fact]
    public void Default_game_id_lookup_is_an_expected_miss()
    {
        var catalog = GameCatalog.CreateBuiltIn();

        var found = catalog.TryGetGame(default, out var game);

        Assert.False(found);
        Assert.Null(game);
    }

    [Fact]
    public void Duplicate_game_id_is_rejected_with_structured_issue()
    {
        var id = new GameId("audition");

        var result = GameCatalog.Create(
        [
            new(id, "Audition"),
            new(id, "Audition renamed")
        ]);

        Assert.False(result.Succeeded);
        Assert.Null(result.Catalog);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(GameCatalogValidationFailureReason.DuplicateGameId, issue.Reason);
        Assert.Equal("GAME_CATALOG_DUPLICATE_GAME_ID", issue.DiagnosticCode);
        Assert.Equal(id, issue.GameId);
    }

    [Fact]
    public void Missing_empty_and_null_definition_catalogs_are_rejected()
    {
        var missing = GameCatalog.Create(null);
        var empty = GameCatalog.Create([]);
        var nullDefinition = GameCatalog.Create([null]);

        Assert.Equal(
            GameCatalogValidationFailureReason.InvalidDefinition,
            Assert.Single(missing.Issues).Reason);
        Assert.Equal(
            GameCatalogValidationFailureReason.EmptyCatalog,
            Assert.Single(empty.Issues).Reason);
        Assert.Equal(
            GameCatalogValidationFailureReason.InvalidDefinition,
            Assert.Single(nullDefinition.Issues).Reason);
    }

    [Fact]
    public void Ordering_is_deterministic_by_stable_id()
    {
        var result = GameCatalog.Create(
        [
            new(new GameId("zeta"), "Zeta"),
            new(new GameId("audition"), "Audition"),
            new(new GameId("beta"), "Beta")
        ]);

        Assert.True(result.Succeeded);
        Assert.Equal(
            new[] { "audition", "beta", "zeta" },
            result.Catalog!.GetGames().Select(game => game.Id.Value));
    }

    [Fact]
    public void Catalog_collection_is_immutable()
    {
        var catalog = GameCatalog.CreateBuiltIn();
        var snapshot = catalog.GetGames();

        var changedCopy = snapshot.Add(new(new GameId("other"), "Other"));

        Assert.Single(snapshot);
        Assert.Equal(2, changedCopy.Length);
        Assert.Single(catalog.GetGames());
    }

    [Theory]
    [InlineData("Sàn Audition Việt Nam")]
    [InlineData("Audition Global Test")]
    public void Unicode_and_spaces_are_valid_display_metadata(string displayName)
    {
        var definition = new GameDefinition(new GameId("audition"), displayName);

        Assert.Equal(displayName, definition.DisplayName);
        Assert.Equal("audition", definition.Id.Value);
    }

    [Fact]
    public void Display_name_does_not_drive_identity_or_filesystem_metadata()
    {
        var result = GameCatalog.Create(
        [
            new(new GameId("game_one"), "Shared display"),
            new(new GameId("game_two"), "Shared display")
        ]);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Catalog!.GetGames().Length);
        Assert.Equal(
            new[] { nameof(GameDefinition.DisplayName), nameof(GameDefinition.Id) },
            typeof(GameDefinition)
                .GetProperties()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Concurrent_reads_are_consistent()
    {
        var catalog = GameCatalog.CreateBuiltIn();
        var reads = Enumerable.Range(0, 100)
            .Select(_ => Task.Run(() =>
            {
                var found = catalog.TryGetGame(new GameId("audition"), out var game);
                return (found, game);
            }));

        var results = await Task.WhenAll(reads);

        Assert.All(results, result => Assert.True(result.found));
        Assert.All(results, result => Assert.Equal("Audition", result.game!.DisplayName));
    }

    [Fact]
    public void Game_definition_rejects_invalid_display_metadata()
    {
        Assert.Throws<ArgumentException>(() =>
            new GameDefinition(new GameId("audition"), " "));
        Assert.Throws<ArgumentException>(() =>
            new GameDefinition(new GameId("audition"), "Audition\nInjected"));
    }
}

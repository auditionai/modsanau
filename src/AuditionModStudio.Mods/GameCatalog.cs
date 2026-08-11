using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using AuditionModStudio.Core.Games;

namespace AuditionModStudio.Mods;

public sealed class GameCatalog : IGameCatalog
{
    private readonly ImmutableDictionary<GameId, GameDefinition> _gamesById;
    private readonly ImmutableArray<GameDefinition> _orderedGames;

    private GameCatalog(ImmutableArray<GameDefinition> games)
    {
        _orderedGames = games;
        _gamesById = games.ToImmutableDictionary(game => game.Id);
    }

    public static IGameCatalog CreateBuiltIn()
    {
        var result = Create(
        [
            new GameDefinition(new GameId("audition"), "Audition")
        ]);

        if (!result.Succeeded)
        {
            var diagnostics = string.Join(",", result.Issues.Select(issue => issue.DiagnosticCode));
            throw new InvalidOperationException($"Built-in game catalog validation failed: {diagnostics}");
        }

        return result.Catalog!;
    }

    public static GameCatalogCreateResult Create(IEnumerable<GameDefinition?>? definitions)
    {
        if (definitions is null)
        {
            return GameCatalogCreateResult.Failure(
            [
                new(
                    GameCatalogValidationFailureReason.InvalidDefinition,
                    "GAME_CATALOG_DEFINITIONS_MISSING",
                    null,
                    null)
            ]);
        }

        var issues = ImmutableArray.CreateBuilder<GameCatalogValidationIssue>();
        var validDefinitions = new List<GameDefinition>();
        var seenIds = new HashSet<GameId>();
        var index = 0;

        foreach (var definition in definitions)
        {
            if (definition is null)
            {
                issues.Add(new(
                    GameCatalogValidationFailureReason.InvalidDefinition,
                    "GAME_CATALOG_DEFINITION_NULL",
                    index,
                    null));
            }
            else if (!seenIds.Add(definition.Id))
            {
                issues.Add(new(
                    GameCatalogValidationFailureReason.DuplicateGameId,
                    "GAME_CATALOG_DUPLICATE_GAME_ID",
                    index,
                    definition.Id));
            }
            else
            {
                validDefinitions.Add(definition);
            }

            index++;
        }

        if (index == 0)
        {
            issues.Add(new(
                GameCatalogValidationFailureReason.EmptyCatalog,
                "GAME_CATALOG_EMPTY",
                null,
                null));
        }

        if (issues.Count > 0)
        {
            return GameCatalogCreateResult.Failure(issues.ToImmutable());
        }

        var ordered = validDefinitions
            .OrderBy(definition => definition.Id.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        return GameCatalogCreateResult.Success(new GameCatalog(ordered));
    }

    public ImmutableArray<GameDefinition> GetGames() => _orderedGames;

    public bool TryGetGame(
        GameId gameId,
        [NotNullWhen(true)] out GameDefinition? game)
    {
        if (!gameId.IsValid)
        {
            game = null;
            return false;
        }

        return _gamesById.TryGetValue(gameId, out game);
    }
}

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace AuditionModStudio.Core.Games;

public interface IGameCatalog
{
    ImmutableArray<GameDefinition> GetGames();

    bool TryGetGame(
        GameId gameId,
        [NotNullWhen(true)] out GameDefinition? game);
}

public enum GameCatalogValidationFailureReason
{
    None,
    EmptyCatalog,
    InvalidDefinition,
    DuplicateGameId
}

public sealed record GameCatalogValidationIssue(
    GameCatalogValidationFailureReason Reason,
    string DiagnosticCode,
    int? DefinitionIndex,
    GameId? GameId);

public sealed record GameCatalogCreateResult(
    bool Succeeded,
    ImmutableArray<GameCatalogValidationIssue> Issues,
    IGameCatalog? Catalog)
{
    public static GameCatalogCreateResult Success(IGameCatalog catalog) =>
        new(true, [], catalog);

    public static GameCatalogCreateResult Failure(
        ImmutableArray<GameCatalogValidationIssue> issues) =>
        new(false, issues, null);
}

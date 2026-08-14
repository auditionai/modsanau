using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using AuditionModStudio.Core.Games;

namespace AuditionModStudio.Core.Mods;

public interface IModCatalog
{
    ImmutableArray<ModDefinition> GetMods(GameId gameId);

    bool TryGetMod(
        GameId gameId,
        ModId modId,
        [NotNullWhen(true)] out ModDefinition? mod);
}

public enum ModCatalogValidationFailureReason
{
    None,
    InvalidDependency,
    InvalidDefinition,
    UnknownGameId,
    UnknownRegionProfile,
    DuplicateModId
}

public sealed record ModCatalogValidationIssue(
    ModCatalogValidationFailureReason Reason,
    string DiagnosticCode,
    int? DefinitionIndex,
    GameId? GameId,
    ModId? ModId);

public sealed record ModCatalogCreateResult(
    bool Succeeded,
    ImmutableArray<ModCatalogValidationIssue> Issues,
    IModCatalog? Catalog)
{
    public static ModCatalogCreateResult Success(IModCatalog catalog) =>
        new(true, [], catalog);

    public static ModCatalogCreateResult Failure(
        ImmutableArray<ModCatalogValidationIssue> issues) =>
        new(false, issues, null);
}

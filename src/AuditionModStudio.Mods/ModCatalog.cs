using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;

namespace AuditionModStudio.Mods;

public sealed class ModCatalog : IModCatalog
{
    private readonly ImmutableDictionary<GameId, ImmutableArray<ModDefinition>> _modsByGame;
    private readonly ImmutableDictionary<(GameId GameId, ModId ModId), ModDefinition> _modsByIdentity;

    private ModCatalog(ImmutableArray<ModDefinition> definitions)
    {
        _modsByGame = definitions
            .GroupBy(definition => definition.GameId)
            .ToImmutableDictionary(
                group => group.Key,
                group => group
                    .OrderBy(definition => definition.Id.Value, StringComparer.Ordinal)
                    .ToImmutableArray());
        _modsByIdentity = definitions.ToImmutableDictionary(
            definition => (definition.GameId, definition.Id));
    }

    public static ModCatalogCreateResult Create(
        IEnumerable<ModDefinition?>? definitions,
        IGameCatalog? gameCatalog,
        IGameRegionProfileResolver? regionProfiles)
    {
        var issues = ImmutableArray.CreateBuilder<ModCatalogValidationIssue>();
        if (definitions is null)
        {
            issues.Add(new(
                ModCatalogValidationFailureReason.InvalidDefinition,
                "MOD_CATALOG_DEFINITIONS_MISSING",
                null,
                null,
                null));
        }

        if (gameCatalog is null)
        {
            issues.Add(new(
                ModCatalogValidationFailureReason.InvalidDependency,
                "MOD_CATALOG_GAME_CATALOG_MISSING",
                null,
                null,
                null));
        }

        if (regionProfiles is null)
        {
            issues.Add(new(
                ModCatalogValidationFailureReason.InvalidDependency,
                "MOD_CATALOG_REGION_RESOLVER_MISSING",
                null,
                null,
                null));
        }

        if (issues.Count > 0)
        {
            return ModCatalogCreateResult.Failure(issues.ToImmutable());
        }

        var validDefinitions = new List<ModDefinition>();
        var seenIds = new HashSet<(GameId GameId, ModId ModId)>();
        var index = 0;
        foreach (var definition in definitions!)
        {
            if (definition is null)
            {
                issues.Add(new(
                    ModCatalogValidationFailureReason.InvalidDefinition,
                    "MOD_CATALOG_DEFINITION_NULL",
                    index,
                    null,
                    null));
            }
            else if (!gameCatalog!.TryGetGame(definition.GameId, out _))
            {
                issues.Add(new(
                    ModCatalogValidationFailureReason.UnknownGameId,
                    "MOD_CATALOG_GAME_UNKNOWN",
                    index,
                    definition.GameId,
                    definition.Id));
            }
            else if (!regionProfiles!.TryResolve(definition.ArchiveTemplate.RegionProfileId, out _))
            {
                issues.Add(new(
                    ModCatalogValidationFailureReason.UnknownRegionProfile,
                    "MOD_CATALOG_REGION_UNKNOWN",
                    index,
                    definition.GameId,
                    definition.Id));
            }
            else if (!seenIds.Add((definition.GameId, definition.Id)))
            {
                issues.Add(new(
                    ModCatalogValidationFailureReason.DuplicateModId,
                    "MOD_CATALOG_DUPLICATE_MOD_ID",
                    index,
                    definition.GameId,
                    definition.Id));
            }
            else
            {
                validDefinitions.Add(definition);
            }

            index++;
        }

        if (issues.Count > 0)
        {
            return ModCatalogCreateResult.Failure(issues.ToImmutable());
        }

        return ModCatalogCreateResult.Success(new ModCatalog(validDefinitions.ToImmutableArray()));
    }

    public ImmutableArray<ModDefinition> GetMods(GameId gameId)
    {
        if (!gameId.IsValid)
        {
            return [];
        }

        return _modsByGame.TryGetValue(gameId, out var definitions)
            ? definitions
            : [];
    }

    public bool TryGetMod(
        GameId gameId,
        ModId modId,
        [NotNullWhen(true)] out ModDefinition? mod)
    {
        if (!gameId.IsValid || !modId.IsValid)
        {
            mod = null;
            return false;
        }

        return _modsByIdentity.TryGetValue((gameId, modId), out mod);
    }
}

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using AuditionModStudio.Core.Games;

namespace AuditionModStudio.Core.Mods;

public interface ITextureManifestCatalog
{
    bool TryGetManifest(
        GameId gameId,
        ModId modId,
        [NotNullWhen(true)] out TextureManifest? manifest);

    TextureManifestResolution Resolve(GameId gameId, ModId modId, ModRelativePath relativePath);
}

public sealed record TextureManifestResolution(
    ModRelativePath RelativePath,
    string DisplayName,
    bool UsedFallback,
    TextureSlot? Slot);

public sealed record TextureManifestCatalogCreateResult(
    bool Succeeded,
    ImmutableArray<TextureManifestValidationIssue> Issues,
    ITextureManifestCatalog? Catalog)
{
    public static TextureManifestCatalogCreateResult Success(ITextureManifestCatalog catalog) => new(true, [], catalog);
    public static TextureManifestCatalogCreateResult Failure(ImmutableArray<TextureManifestValidationIssue> issues) => new(false, issues, null);
}

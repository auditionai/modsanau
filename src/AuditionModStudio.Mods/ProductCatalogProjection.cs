using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Catalog;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;

namespace AuditionModStudio.Mods;

public sealed record ProductCatalogProjectionResult(bool Succeeded, string DiagnosticCode,
    IGameCatalog? Games, IModCatalog? Mods, ITextureManifestCatalog? Manifests);

public static class ProductCatalogProjection
{
    public static ProductCatalogProjectionResult Create(ProductCatalogSnapshot snapshot,
        IGameRegionProfileResolver regionProfiles)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(regionProfiles);
        var gamesResult = GameCatalog.Create(snapshot.Games.Select(entry =>
            new GameDefinition(entry.GameId, entry.DisplayName)));
        if (!gamesResult.Succeeded)
            return Failed("PRODUCT_CATALOG_GAME_PROJECTION_INVALID");
        var templates = snapshot.Templates.Where(entry => entry.IsCurrent)
            .ToDictionary(entry => (entry.GameId, entry.ModId));
        if (snapshot.Mods.Any(entry => !templates.ContainsKey((entry.GameId, entry.ModId))))
            return Failed("PRODUCT_CATALOG_TEMPLATE_MAPPING_MISSING");
        var modsResult = ModCatalog.Create(snapshot.Mods.Select(entry =>
        {
            var template = templates[(entry.GameId, entry.ModId)];
            var archive = new AuditionArchiveTemplate(template.Identity.TemplateId.Value,
                template.ArchiveFileName, template.ArchiveFileName, template.EngineType,
                template.RegionProfileId, template.ExpectedExtractFolderName,
                template.Identity.Version.Value, template.Identity.Sha256.Value,
                template.Identity.CompatibleGameBuild.Value);
            var cover = new ModRelativePath(entry.CoverReference);
            return new ModDefinition(entry.ModId, entry.GameId, entry.DisplayName, entry.Category,
                cover, entry.Description, archive, ModKeydatStrategy.ReuseOrGenerate,
                entry.CompatibilityInformation);
        }), gamesResult.Catalog, regionProfiles);
        if (!modsResult.Succeeded) return Failed("PRODUCT_CATALOG_MOD_PROJECTION_INVALID");
        var manifests = snapshot.Manifests.Select(entry => TextureManifest.Create(entry.GameId, entry.ModId,
            entry.Slots.Select(slot => new TextureSlot(slot.SlotId, slot.RelativePath, slot.DisplayName,
                slot.Category, slot.Description, slot.Tags, slot.PreviewEnabled, slot.Editable,
                slot.RecommendedEditMode))).Manifest).ToArray();
        if (manifests.Any(manifest => manifest is null)) return Failed("PRODUCT_CATALOG_MANIFEST_PROJECTION_INVALID");
        var manifestResult = TextureManifestCatalog.Create(manifests, modsResult.Catalog);
        return manifestResult.Succeeded
            ? new(true, "PRODUCT_CATALOG_PROJECTED", gamesResult.Catalog, modsResult.Catalog, manifestResult.Catalog)
            : Failed("PRODUCT_CATALOG_MANIFEST_PROJECTION_INVALID");
    }

    private static ProductCatalogProjectionResult Failed(string code) => new(false, code, null, null, null);
}

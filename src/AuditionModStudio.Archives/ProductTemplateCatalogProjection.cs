using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Catalog;

namespace AuditionModStudio.Archives;

public static class ProductTemplateCatalogProjection
{
    public static TemplateVersionCatalogCreateResult Create(ProductCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return TemplateVersionCatalog.Create(snapshot.Templates.Select(entry => new TemplateCatalogEntry(
            new AuditionArchiveTemplate(entry.Identity.TemplateId.Value, entry.ArchiveFileName,
                entry.ArchiveFileName, entry.EngineType, entry.RegionProfileId,
                entry.ExpectedExtractFolderName, entry.Identity.Version.Value,
                entry.Identity.Sha256.Value, entry.Identity.CompatibleGameBuild.Value), entry.IsCurrent)));
    }
}

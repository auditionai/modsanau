using System.Collections.Immutable;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;

namespace AuditionModStudio.Core.Catalog;

public readonly record struct ProductCatalogVersion
{
    public ProductCatalogVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64
            || !char.IsAsciiLetterOrDigit(value[0])
            || value.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character is not '.' and not '_' and not '-'))
            throw new ArgumentException("Catalog versions must be bounded opaque ASCII identifiers.", nameof(value));
        Value = value;
    }
    public string Value { get; }
    public bool IsValid => !string.IsNullOrEmpty(Value);
    public override string ToString() => Value ?? string.Empty;
}

public sealed record ProductGameEntry(GameId GameId, string DisplayName);

public sealed record ProductModEntry(
    GameId GameId,
    ModId ModId,
    string DisplayName,
    ModCategory Category,
    string Description,
    string CoverReference,
    string CompatibilityInformation);

public sealed record ProductTemplateEntry(
    GameId GameId,
    ModId ModId,
    TemplateIdentity Identity,
    bool IsCurrent,
    bool RequiresPremiumEntitlement,
    string ArchiveFileName,
    ArchiveEngineType EngineType,
    string RegionProfileId,
    string ExpectedExtractFolderName);

public sealed record ProductTextureSlotEntry(
    TextureSlotId SlotId,
    ModRelativePath RelativePath,
    string DisplayName,
    TextureCategory Category,
    string Description,
    ImmutableArray<string> Tags,
    bool PreviewEnabled,
    bool Editable,
    TextureEditMode RecommendedEditMode);

public sealed record ProductTextureManifestEntry(
    GameId GameId,
    ModId ModId,
    ImmutableArray<ProductTextureSlotEntry> Slots);

public sealed class ProductCatalogSnapshot
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumEntriesPerCollection = 10_000;

    public ProductCatalogSnapshot(int schemaVersion, ProductCatalogVersion catalogVersion, long revision,
        DateTimeOffset issuedAt, ImmutableArray<ProductGameEntry> games, ImmutableArray<ProductModEntry> mods,
        ImmutableArray<ProductTemplateEntry> templates, ImmutableArray<ProductTextureManifestEntry> manifests)
    {
        SchemaVersion = schemaVersion;
        CatalogVersion = catalogVersion;
        Revision = revision;
        IssuedAt = issuedAt;
        Games = games;
        Mods = mods;
        Templates = templates;
        Manifests = manifests;
    }

    public int SchemaVersion { get; }
    public ProductCatalogVersion CatalogVersion { get; }
    public long Revision { get; }
    public DateTimeOffset IssuedAt { get; }
    public ImmutableArray<ProductGameEntry> Games { get; }
    public ImmutableArray<ProductModEntry> Mods { get; }
    public ImmutableArray<ProductTemplateEntry> Templates { get; }
    public ImmutableArray<ProductTextureManifestEntry> Manifests { get; }
}

public enum ProductCatalogStatus
{
    Succeeded, Unavailable, InvalidSignature, InvalidSchema, UnsupportedVersion, Corrupt, Stale,
    RollbackRejected, Cancelled
}

public sealed record ProductCatalogResult(
    ProductCatalogStatus Status,
    string DiagnosticCode,
    ProductCatalogSnapshot? Snapshot,
    bool FromCache = false)
{
    public bool Succeeded => Status == ProductCatalogStatus.Succeeded && Snapshot is not null;
}

public interface IProductCatalogService
{
    Task<ProductCatalogResult> RefreshAsync(CancellationToken cancellationToken = default);
    Task<ProductCatalogResult> LoadOfflineAsync(CancellationToken cancellationToken = default);
}

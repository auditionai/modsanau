using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;

namespace AuditionModStudio.Mods;

public sealed class TextureManifestCatalog : ITextureManifestCatalog
{
    private readonly ImmutableDictionary<(GameId GameId, ModId ModId), TextureManifest> _manifests;
    private readonly ImmutableDictionary<(GameId GameId, ModId ModId, string Path), TextureSlot> _slots;

    private TextureManifestCatalog(IEnumerable<TextureManifest> manifests)
    {
        _manifests = manifests.ToImmutableDictionary(manifest => (manifest.GameId, manifest.ModId));
        _slots = manifests
            .SelectMany(manifest => manifest.Slots.Select(slot => (manifest, slot)))
            .ToImmutableDictionary(
                item => (item.manifest.GameId, item.manifest.ModId, item.slot.RelativePath.Value),
                item => item.slot,
                new AssetIdentityComparer());
    }

    public static TextureManifestCatalogCreateResult Create(
        IEnumerable<TextureManifest?>? manifests,
        IModCatalog? modCatalog)
    {
        var issues = ImmutableArray.CreateBuilder<TextureManifestValidationIssue>();
        if (manifests is null)
        {
            issues.Add(new(TextureManifestValidationFailureReason.InvalidDependency, "TEXTURE_MANIFEST_CATALOG_DEFINITIONS_MISSING", null));
        }

        if (modCatalog is null)
        {
            issues.Add(new(TextureManifestValidationFailureReason.InvalidDependency, "TEXTURE_MANIFEST_CATALOG_MOD_CATALOG_MISSING", null));
        }

        if (issues.Count > 0)
        {
            return TextureManifestCatalogCreateResult.Failure(issues.ToImmutable());
        }

        var valid = new List<TextureManifest>();
        var seen = new HashSet<(GameId, ModId)>();
        var index = 0;
        foreach (var manifest in manifests!)
        {
            if (manifest is null)
            {
                issues.Add(new(TextureManifestValidationFailureReason.InvalidManifest, "TEXTURE_MANIFEST_CATALOG_MANIFEST_NULL", index));
            }
            else if (!modCatalog!.TryGetMod(manifest.GameId, manifest.ModId, out _))
            {
                issues.Add(new(TextureManifestValidationFailureReason.UnknownMod, "TEXTURE_MANIFEST_CATALOG_MOD_UNKNOWN", index));
            }
            else if (!seen.Add((manifest.GameId, manifest.ModId)))
            {
                issues.Add(new(TextureManifestValidationFailureReason.DuplicateManifest, "TEXTURE_MANIFEST_CATALOG_DUPLICATE_MANIFEST", index));
            }
            else
            {
                valid.Add(manifest);
            }

            index++;
        }

        return issues.Count > 0
            ? TextureManifestCatalogCreateResult.Failure(issues.ToImmutable())
            : TextureManifestCatalogCreateResult.Success(new TextureManifestCatalog(valid));
    }

    public bool TryGetManifest(GameId gameId, ModId modId, [NotNullWhen(true)] out TextureManifest? manifest)
    {
        if (!gameId.IsValid || !modId.IsValid)
        {
            manifest = null;
            return false;
        }

        return _manifests.TryGetValue((gameId, modId), out manifest);
    }

    public TextureManifestResolution Resolve(GameId gameId, ModId modId, ModRelativePath relativePath)
    {
        if (!gameId.IsValid || !modId.IsValid || !relativePath.IsValid)
        {
            throw new ArgumentException("Texture manifest resolution requires valid typed identities.");
        }

        if (_slots.TryGetValue((gameId, modId, relativePath.Value), out var slot))
        {
            return new(relativePath, slot.DisplayName, false, slot);
        }

        return new(relativePath, Path.GetFileName(relativePath.Value), true, null);
    }

    private sealed class AssetIdentityComparer : IEqualityComparer<(GameId GameId, ModId ModId, string Path)>
    {
        public bool Equals(
            (GameId GameId, ModId ModId, string Path) x,
            (GameId GameId, ModId ModId, string Path) y) =>
            x.GameId == y.GameId && x.ModId == y.ModId
            && StringComparer.OrdinalIgnoreCase.Equals(x.Path, y.Path);

        public int GetHashCode((GameId GameId, ModId ModId, string Path) value) =>
            HashCode.Combine(value.GameId, value.ModId, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Path));
    }
}

using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Auth;
using AuditionModStudio.Core.Catalog;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Paths;

namespace AuditionModStudio.Cloud;

public sealed class UnavailableProductCatalogService : IProductCatalogService
{
    public Task<ProductCatalogResult> RefreshAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProductCatalogResult(cancellationToken.IsCancellationRequested
            ? ProductCatalogStatus.Cancelled : ProductCatalogStatus.Unavailable,
            cancellationToken.IsCancellationRequested ? "PRODUCT_CATALOG_CANCELLED"
                : "PRODUCT_CATALOG_UNAVAILABLE", null));
    public Task<ProductCatalogResult> LoadOfflineAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(cancellationToken);
}

public sealed record ProductCatalogOptions(Uri GatewayBaseUri, string CacheRootDirectory,
    ImmutableDictionary<string, string> VerificationKeys)
{
    public const int MaximumDocumentBytes = 4 * 1024 * 1024;
    public bool IsValid => GatewayBaseUri is { IsAbsoluteUri: true, Scheme: "https", AbsolutePath: "/" }
        && string.IsNullOrEmpty(GatewayBaseUri.UserInfo) && string.IsNullOrEmpty(GatewayBaseUri.Query)
        && string.IsNullOrEmpty(GatewayBaseUri.Fragment) && Path.IsPathFullyQualified(CacheRootDirectory)
        && VerificationKeys is { Count: > 0 and <= 3 }
        && VerificationKeys.All(pair => pair.Key.Length is > 0 and <= 64
            && pair.Key.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            && pair.Value.Length is > 0 and <= 16_384);
    public string CacheFilePath => Path.Combine(CacheRootDirectory, "ProductCatalog", "catalog.v1.json");
}

public sealed class ProductCatalogService(
    HttpClient httpClient,
    ISecureSessionStore sessions,
    ProductCatalogOptions options,
    IPathSecurity pathSecurity) : IProductCatalogService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16,
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _highestAcceptedRevision = -1;

    public async Task<ProductCatalogResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!options.IsValid) return Failure(ProductCatalogStatus.Unavailable, "PRODUCT_CATALOG_CONFIGURATION_INVALID");
        var entered = false;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            await LoadAcceptedCacheRevisionAsync(cancellationToken).ConfigureAwait(false);
            var session = await sessions.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (session is null || session.ExpiresAt <= DateTimeOffset.UtcNow)
                return Failure(ProductCatalogStatus.Unavailable, "PRODUCT_CATALOG_AUTH_UNAVAILABLE");
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(options.GatewayBaseUri, "v1/product-catalog"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                return Failure(ProductCatalogStatus.Unavailable, "PRODUCT_CATALOG_UNAVAILABLE");
            var bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (bytes is null) return Failure(ProductCatalogStatus.Corrupt, "PRODUCT_CATALOG_RESPONSE_INVALID");
            var parsed = VerifyAndParse(bytes, _highestAcceptedRevision);
            if (!parsed.Succeeded) return parsed;
            await WriteCacheAtomicallyAsync(bytes, cancellationToken).ConfigureAwait(false);
            _highestAcceptedRevision = Math.Max(_highestAcceptedRevision, parsed.Snapshot!.Revision);
            return parsed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Failure(ProductCatalogStatus.Cancelled, "PRODUCT_CATALOG_CANCELLED"); }
        catch (HttpRequestException) { return Failure(ProductCatalogStatus.Unavailable, "PRODUCT_CATALOG_UNAVAILABLE"); }
        catch (IOException) { return Failure(ProductCatalogStatus.Unavailable, "PRODUCT_CATALOG_CACHE_UNAVAILABLE"); }
        catch (UnauthorizedAccessException) { return Failure(ProductCatalogStatus.Unavailable, "PRODUCT_CATALOG_CACHE_UNAVAILABLE"); }
        catch (InvalidOperationException) { return Failure(ProductCatalogStatus.Corrupt, "PRODUCT_CATALOG_CACHE_UNSAFE"); }
        finally { if (entered) _gate.Release(); }
    }

    public async Task<ProductCatalogResult> LoadOfflineAsync(CancellationToken cancellationToken = default)
    {
        if (!options.IsValid) return Failure(ProductCatalogStatus.Unavailable, "PRODUCT_CATALOG_CONFIGURATION_INVALID");
        var entered = false;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            if (!File.Exists(options.CacheFilePath))
                return Failure(ProductCatalogStatus.Unavailable, "PRODUCT_CATALOG_CACHE_MISSING");
            pathSecurity.EnsureNoReparsePoints(options.CacheRootDirectory, options.CacheFilePath);
            var info = new FileInfo(options.CacheFilePath);
            if (info.Length is <= 0 or > ProductCatalogOptions.MaximumDocumentBytes)
                return Failure(ProductCatalogStatus.Corrupt, "PRODUCT_CATALOG_CACHE_CORRUPT");
            var bytes = await File.ReadAllBytesAsync(options.CacheFilePath, cancellationToken).ConfigureAwait(false);
            var parsed = VerifyAndParse(bytes, _highestAcceptedRevision);
            if (!parsed.Succeeded) return parsed with { FromCache = true };
            _highestAcceptedRevision = Math.Max(_highestAcceptedRevision, parsed.Snapshot!.Revision);
            return parsed with { FromCache = true };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Failure(ProductCatalogStatus.Cancelled, "PRODUCT_CATALOG_CANCELLED", true); }
        catch (IOException) { return Failure(ProductCatalogStatus.Corrupt, "PRODUCT_CATALOG_CACHE_CORRUPT", true); }
        catch (UnauthorizedAccessException) { return Failure(ProductCatalogStatus.Unavailable, "PRODUCT_CATALOG_CACHE_UNAVAILABLE", true); }
        catch (InvalidOperationException) { return Failure(ProductCatalogStatus.Corrupt, "PRODUCT_CATALOG_CACHE_UNSAFE", true); }
        finally { if (entered) _gate.Release(); }
    }

    internal ProductCatalogResult VerifyAndParse(ReadOnlySpan<byte> document, long minimumRevision = -1)
    {
        if (document.Length is <= 0 or > ProductCatalogOptions.MaximumDocumentBytes)
            return Failure(ProductCatalogStatus.Corrupt, "PRODUCT_CATALOG_DOCUMENT_INVALID");
        try
        {
            var envelope = JsonSerializer.Deserialize<CatalogEnvelopeDto>(document, JsonOptions);
            if (envelope is null || envelope.SchemaVersion != 1 || string.IsNullOrWhiteSpace(envelope.KeyId)
                || !TryDecode(envelope.Payload, ProductCatalogOptions.MaximumDocumentBytes, out var payload)
                || !TryDecode(envelope.Signature, 64, out var signature) || signature.Length != 64)
                return Failure(ProductCatalogStatus.InvalidSchema, "PRODUCT_CATALOG_ENVELOPE_INVALID");
            if (!options.VerificationKeys.TryGetValue(envelope.KeyId, out var publicKeyPem))
                return Failure(ProductCatalogStatus.InvalidSignature, "PRODUCT_CATALOG_KEY_UNTRUSTED");
            if (!Verify(publicKeyPem, payload, signature))
                return Failure(ProductCatalogStatus.InvalidSignature, "PRODUCT_CATALOG_SIGNATURE_INVALID");
            var dto = JsonSerializer.Deserialize<CatalogPayloadDto>(payload, JsonOptions);
            return Validate(dto, minimumRevision);
        }
        catch (JsonException) { return Failure(ProductCatalogStatus.InvalidSchema, "PRODUCT_CATALOG_SCHEMA_INVALID"); }
        catch (CryptographicException) { return Failure(ProductCatalogStatus.InvalidSignature, "PRODUCT_CATALOG_SIGNATURE_INVALID"); }
        catch (ArgumentException) { return Failure(ProductCatalogStatus.InvalidSchema, "PRODUCT_CATALOG_SCHEMA_INVALID"); }
    }

    private static ProductCatalogResult Validate(CatalogPayloadDto? value, long minimumRevision)
    {
        if (value is null) return Failure(ProductCatalogStatus.InvalidSchema, "PRODUCT_CATALOG_SCHEMA_INVALID");
        if (value.SchemaVersion != ProductCatalogSnapshot.CurrentSchemaVersion)
            return Failure(ProductCatalogStatus.UnsupportedVersion, "PRODUCT_CATALOG_SCHEMA_UNSUPPORTED");
        if (value.Revision < 0 || value.Revision < minimumRevision)
            return Failure(ProductCatalogStatus.RollbackRejected, "PRODUCT_CATALOG_ROLLBACK_REJECTED");
        if (value.Games is null || value.Mods is null || value.Templates is null || value.Manifests is null
            || new[] { value.Games.Count, value.Mods.Count, value.Templates.Count, value.Manifests.Count }
                .Any(count => count > ProductCatalogSnapshot.MaximumEntriesPerCollection))
            return Failure(ProductCatalogStatus.InvalidSchema, "PRODUCT_CATALOG_SCHEMA_INVALID");
        var games = value.Games.Select(item => new ProductGameEntry(new(item.GameId), Text(item.DisplayName, 128))).ToArray();
        if (games.Select(item => item.GameId).Distinct().Count() != games.Length)
            return Failure(ProductCatalogStatus.InvalidSchema, "PRODUCT_CATALOG_GAME_DUPLICATE");
        var gameIds = games.Select(item => item.GameId).ToHashSet();
        var mods = value.Mods.Select(item => new ProductModEntry(new(item.GameId), new(item.ModId),
            Text(item.DisplayName, 128), new(item.Category), Text(item.Description, 2048, true),
            Relative(item.CoverReference), Text(item.CompatibilityInformation, 1024, true))).ToArray();
        if (mods.Any(item => !gameIds.Contains(item.GameId))
            || mods.Select(item => (item.GameId, item.ModId)).Distinct().Count() != mods.Length)
            return Failure(ProductCatalogStatus.InvalidSchema, "PRODUCT_CATALOG_MOD_RELATION_INVALID");
        var modIds = mods.Select(item => (item.GameId, item.ModId)).ToHashSet();
        var templates = value.Templates.Select(item => new ProductTemplateEntry(new(item.GameId), new(item.ModId),
            new(new(item.TemplateId), new(item.TemplateVersion), new(item.Sha256), new(item.CompatibleGameBuild)),
            item.IsCurrent, item.RequiresPremiumEntitlement, SimpleFile(item.ArchiveFileName),
            new ArchiveEngineType(item.EngineType), Text(item.RegionProfileId, 64),
            Relative(item.ExpectedExtractFolderName))).ToArray();
        if (templates.Any(item => !modIds.Contains((item.GameId, item.ModId)))
            || templates.Select(item => (item.Identity.TemplateId, item.Identity.Version)).Distinct().Count() != templates.Length
            || templates.GroupBy(item => item.Identity.TemplateId).Any(group => group.Count(item => item.IsCurrent) != 1)
            || templates.GroupBy(item => (item.GameId, item.ModId)).Any(group => group.Count(item => item.IsCurrent) != 1))
            return Failure(ProductCatalogStatus.InvalidSchema, "PRODUCT_CATALOG_TEMPLATE_RELATION_INVALID");
        var manifests = value.Manifests.Select(item => new ProductTextureManifestEntry(new(item.GameId), new(item.ModId),
            (item.Slots ?? throw new ArgumentException()).Select(slot => new ProductTextureSlotEntry(new(slot.SlotId),
                new(Relative(slot.RelativePath)), Text(slot.DisplayName, 128), new(slot.Category),
                Text(slot.Description, 2_048, true), Tags(slot.Tags), slot.PreviewEnabled, slot.Editable,
                new(slot.RecommendedEditMode)))
                .OrderBy(slot => slot.SlotId.Value, StringComparer.Ordinal).ToImmutableArray())).ToArray();
        if (manifests.Any(item => !modIds.Contains((item.GameId, item.ModId)))
            || manifests.Select(item => (item.GameId, item.ModId)).Distinct().Count() != manifests.Length
            || manifests.Any(item => item.Slots.Select(slot => slot.SlotId).Distinct().Count() != item.Slots.Length
                || item.Slots.Select(slot => slot.RelativePath.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != item.Slots.Length))
            return Failure(ProductCatalogStatus.InvalidSchema, "PRODUCT_CATALOG_MANIFEST_RELATION_INVALID");
        var snapshot = new ProductCatalogSnapshot(value.SchemaVersion, new(value.CatalogVersion), value.Revision,
            value.IssuedAt,
            games.OrderBy(item => item.GameId.Value, StringComparer.Ordinal).ToImmutableArray(),
            mods.OrderBy(item => item.GameId.Value, StringComparer.Ordinal).ThenBy(item => item.ModId.Value, StringComparer.Ordinal).ToImmutableArray(),
            templates.OrderBy(item => item.Identity.TemplateId.Value, StringComparer.Ordinal).ThenBy(item => item.Identity.Version.Value, StringComparer.Ordinal).ToImmutableArray(),
            manifests.OrderBy(item => item.GameId.Value, StringComparer.Ordinal).ThenBy(item => item.ModId.Value, StringComparer.Ordinal).ToImmutableArray());
        return new(ProductCatalogStatus.Succeeded, "PRODUCT_CATALOG_VALID", snapshot);
    }

    private async Task WriteCacheAtomicallyAsync(byte[] bytes, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(options.CacheFilePath)!;
        pathSecurity.EnsureNoReparsePoints(options.CacheRootDirectory, options.CacheRootDirectory);
        Directory.CreateDirectory(directory);
        pathSecurity.EnsureNoReparsePoints(options.CacheRootDirectory, directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(options.CacheFilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(true);
            }
            pathSecurity.EnsureNoReparsePoints(options.CacheRootDirectory, directory);
            File.Move(temporary, options.CacheFilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task LoadAcceptedCacheRevisionAsync(CancellationToken cancellationToken)
    {
        if (_highestAcceptedRevision >= 0 || !File.Exists(options.CacheFilePath)) return;
        pathSecurity.EnsureNoReparsePoints(options.CacheRootDirectory, options.CacheFilePath);
        var info = new FileInfo(options.CacheFilePath);
        if (info.Length is <= 0 or > ProductCatalogOptions.MaximumDocumentBytes) return;
        var cached = await File.ReadAllBytesAsync(options.CacheFilePath, cancellationToken).ConfigureAwait(false);
        var result = VerifyAndParse(cached);
        if (result.Succeeded) _highestAcceptedRevision = result.Snapshot!.Revision;
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength is > ProductCatalogOptions.MaximumDocumentBytes) return null;
        await using var input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, token).ConfigureAwait(false); if (read == 0) break;
            if (output.Length + read > ProductCatalogOptions.MaximumDocumentBytes) return null; output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
    private static bool Verify(string pem, byte[] payload, byte[] signature)
    {
        using var key = ECDsa.Create(); key.ImportFromPem(pem); return key.KeySize == 256 && key.VerifyData(payload, signature,
        HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
    private static bool TryDecode(string? value, int maximum, out byte[] bytes)
    {
        bytes = []; if (string.IsNullOrWhiteSpace(value) || value.Length > maximum * 2) return false;
        try { bytes = Convert.FromBase64String(value); return bytes.Length <= maximum; } catch (FormatException) { return false; }
    }
    private static string Text(string value, int maximum, bool allowEmpty = false)
    { if (value is null || value.Length > maximum || value.Any(char.IsControl) || (!allowEmpty && string.IsNullOrWhiteSpace(value))) throw new ArgumentException(); return value; }
    private static ImmutableArray<string> Tags(List<string>? values)
    {
        if (values is null || values.Count > 32 || values.Any(value => string.IsNullOrWhiteSpace(value)
            || value.Length > 64 || value.Any(char.IsControl))
            || values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Count)
            throw new ArgumentException();
        return values.ToImmutableArray();
    }
    private static string Relative(string value) => ModRelativePath.TryCreate(value, out var path) ? path.Value : throw new ArgumentException();
    private static string SimpleFile(string value) => Path.GetFileName(value) == value && !string.IsNullOrWhiteSpace(Path.GetExtension(value)) ? value : throw new ArgumentException();
    private static ProductCatalogResult Failure(ProductCatalogStatus status, string code, bool cache = false) => new(status, code, null, cache);

    internal sealed record CatalogEnvelopeDto(int SchemaVersion, string KeyId, string Payload, string Signature);
    internal sealed record CatalogPayloadDto(int SchemaVersion, string CatalogVersion, long Revision, DateTimeOffset IssuedAt,
        List<GameDto>? Games, List<ModDto>? Mods, List<TemplateDto>? Templates, List<ManifestDto>? Manifests);
    internal sealed record GameDto(string GameId, string DisplayName);
    internal sealed record ModDto(string GameId, string ModId, string DisplayName, string Category, string Description,
        string CoverReference, string CompatibilityInformation);
    internal sealed record TemplateDto(string GameId, string ModId, string TemplateId, string TemplateVersion, string Sha256,
        string CompatibleGameBuild, bool IsCurrent, bool RequiresPremiumEntitlement, string ArchiveFileName,
        string EngineType, string RegionProfileId, string ExpectedExtractFolderName);
    internal sealed record ManifestDto(string GameId, string ModId, List<SlotDto>? Slots);
    internal sealed record SlotDto(string SlotId, string RelativePath, string DisplayName, string Category,
        string Description, List<string>? Tags, bool PreviewEnabled, bool Editable, string RecommendedEditMode);
}

using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuditionModStudio.Cloud;
using AuditionModStudio.Core.Auth;
using AuditionModStudio.Core.Catalog;
using AuditionModStudio.Archives;
using AuditionModStudio.Mods;
using AuditionModStudio.Infrastructure.Paths;

namespace IntegrationTests;

public sealed class Plan91RemoteProductCatalogTests
{
    [Fact]
    public async Task Signed_catalog_maps_exact_identity_orders_deterministically_and_caches()
    {
        using var fixture = new Fixture();
        var document = fixture.Sign(Payload(revision: 7, premium: true));
        var service = fixture.Service(document);

        var result = await service.RefreshAsync();
        var offline = await service.LoadOfflineAsync();

        Assert.True(result.Succeeded, $"{result.Status}: {result.DiagnosticCode}");
        Assert.True(offline.Succeeded, $"{offline.Status}: {offline.DiagnosticCode}");
        Assert.True(offline.FromCache);
        Assert.Equal(7, result.Snapshot!.Revision);
        Assert.Equal("audition", Assert.Single(result.Snapshot.Games).GameId.Value);
        var template = Assert.Single(result.Snapshot.Templates);
        Assert.Equal("pointer", template.Identity.TemplateId.Value);
        Assert.Equal("v1", template.Identity.Version.Value);
        Assert.Equal(new string('A', 64), template.Identity.Sha256.Value);
        Assert.True(template.RequiresPremiumEntitlement); // Metadata only; no entitlement grant exists here.
        Assert.Equal("texture.dds", Assert.Single(Assert.Single(result.Snapshot.Manifests).Slots).RelativePath.Value);
        var projection = ProductCatalogProjection.Create(result.Snapshot, new GameRegionProfileCatalog());
        Assert.True(projection.Succeeded, projection.DiagnosticCode);
        Assert.True(projection.Games!.TryGetGame(new("audition"), out _));
        Assert.True(projection.Mods!.TryGetMod(new("audition"), new("pointer_mod"), out var projectedMod));
        Assert.Null(projectedMod!.InstallRelativePath);
        var templateProjection = ProductTemplateCatalogProjection.Create(result.Snapshot);
        Assert.True(templateProjection.Succeeded);
        Assert.True(templateProjection.Catalog!.TryGetExact(new("pointer"), new("v1"), out _));
    }

    [Fact]
    public async Task Tamper_unknown_schema_and_duplicate_relationships_fail_closed_without_cache_publish()
    {
        using var fixture = new Fixture();
        var valid = fixture.Sign(Payload());
        using var parsed = JsonDocument.Parse(valid);
        var root = parsed.RootElement;
        var tampered = JsonSerializer.Serialize(new
        {
            schemaVersion = root.GetProperty("schemaVersion").GetInt32(),
            keyId = root.GetProperty("keyId").GetString(),
            payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(Payload().Replace("\"revision\":1", "\"revision\":2"))),
            signature = root.GetProperty("signature").GetString(),
        });
        Assert.Equal(ProductCatalogStatus.InvalidSignature,
            (await fixture.Service(tampered).RefreshAsync()).Status);

        Assert.Equal(ProductCatalogStatus.UnsupportedVersion,
            (await fixture.Service(fixture.Sign(Payload(schemaVersion: 2))).RefreshAsync()).Status);
        Assert.Equal(ProductCatalogStatus.InvalidSchema,
            (await fixture.Service(fixture.Sign(Payload(duplicateGame: true))).RefreshAsync()).Status);
        var executableMetadata = Payload().Replace("\"displayName\":\"Audition\"",
            "\"displayName\":\"Audition\",\"executablePath\":\"game.exe\"");
        Assert.Equal(ProductCatalogStatus.InvalidSchema,
            (await fixture.Service(fixture.Sign(executableMetadata)).RefreshAsync()).Status);
        Assert.False(File.Exists(fixture.CachePath));
    }

    [Fact]
    public async Task Refresh_publishes_new_snapshot_without_mutating_previous_snapshot()
    {
        using var fixture = new Fixture();
        var service = fixture.Service(fixture.Sign(Payload(revision: 1)));
        var original = (await service.RefreshAsync()).Snapshot!;
        fixture.Handler.Document = fixture.Sign(Payload(revision: 2, premium: true));

        var refreshed = (await service.RefreshAsync()).Snapshot!;

        Assert.Equal(1, original.Revision);
        Assert.False(Assert.Single(original.Templates).RequiresPremiumEntitlement);
        Assert.Equal(2, refreshed.Revision);
        Assert.True(Assert.Single(refreshed.Templates).RequiresPremiumEntitlement);
    }

    [Fact]
    public async Task Older_revision_is_rejected_and_corrupt_offline_cache_is_not_authority()
    {
        using var fixture = new Fixture();
        var service = fixture.Service(fixture.Sign(Payload(revision: 9)));
        var initial = await service.RefreshAsync();
        Assert.True(initial.Succeeded, $"{initial.Status}: {initial.DiagnosticCode}");

        fixture.Handler.Document = fixture.Sign(Payload(revision: 8));
        Assert.Equal(ProductCatalogStatus.RollbackRejected, (await service.RefreshAsync()).Status);

        await File.WriteAllTextAsync(fixture.CachePath, "not-json");
        Assert.Equal(ProductCatalogStatus.InvalidSchema, (await service.LoadOfflineAsync()).Status);
    }

    [Fact]
    public async Task Valid_cache_prevents_rollback_after_process_restart()
    {
        using var fixture = new Fixture();
        Assert.True((await fixture.Service(fixture.Sign(Payload(revision: 12))).RefreshAsync()).Succeeded);

        var restartedService = fixture.Service(fixture.Sign(Payload(revision: 11)));
        var result = await restartedService.RefreshAsync();

        Assert.Equal(ProductCatalogStatus.RollbackRejected, result.Status);
        Assert.Equal(12, (await restartedService.LoadOfflineAsync()).Snapshot!.Revision);
    }

    [Fact]
    public async Task Cancellation_is_structured_and_does_not_publish_partial_cache()
    {
        using var fixture = new Fixture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var result = await fixture.Service(fixture.Sign(Payload())).RefreshAsync(cancelled.Token);
        Assert.Equal(ProductCatalogStatus.Cancelled, result.Status);
        Assert.False(File.Exists(fixture.CachePath));
    }

    [Fact]
    public async Task Concurrent_refreshes_are_serialized_and_publish_one_valid_cache()
    {
        using var fixture = new Fixture();
        var service = fixture.Service(fixture.Sign(Payload(revision: 4)));

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => service.RefreshAsync()));

        Assert.All(results, result => Assert.True(result.Succeeded, result.DiagnosticCode));
        var cached = await service.LoadOfflineAsync();
        Assert.True(cached.Succeeded);
        Assert.Equal(4, cached.Snapshot!.Revision);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(fixture.CachePath)!, "*.tmp"));
    }

    private static string Payload(int schemaVersion = 1, long revision = 1, bool premium = false,
        bool duplicateGame = false)
    {
        var games = duplicateGame
            ? "[{\"gameId\":\"audition\",\"displayName\":\"Audition\"},{\"gameId\":\"audition\",\"displayName\":\"Duplicate\"}]"
            : "[{\"gameId\":\"audition\",\"displayName\":\"Audition\"}]";
        return $$"""
        {"schemaVersion":{{schemaVersion}},"catalogVersion":"2026.08","revision":{{revision}},"issuedAt":"2026-08-13T00:00:00+00:00",
        "games":{{games}},
        "mods":[{"gameId":"audition","modId":"pointer_mod","displayName":"Pointer","category":"interface","description":"Pointer textures","coverReference":"covers/pointer.png","compatibilityInformation":"build-1"}],
        "templates":[{"gameId":"audition","modId":"pointer_mod","templateId":"pointer","templateVersion":"v1","sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","compatibleGameBuild":"build-1","isCurrent":true,"requiresPremiumEntitlement":{{premium.ToString().ToLowerInvariant()}},"archiveFileName":"015.ab","engineType":"acv_tool_5","regionProfileId":"audition_vn","expectedExtractFolderName":"015"}],
        "manifests":[{"gameId":"audition","modId":"pointer_mod","slots":[{"slotId":"logo","relativePath":"texture.dds","displayName":"Logo","category":"logo","description":"Main logo","tags":["logo"],"previewEnabled":true,"editable":true,"recommendedEditMode":"fit"}]}]}
        """;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "ams-plan91-" + Guid.NewGuid().ToString("N"));
        public Fixture() { Directory.CreateDirectory(_directory); Handler = new Handler(); }
        public Handler Handler { get; }
        public string CachePath => Path.Combine(_directory, "ProductCatalog", "catalog.v1.json");
        public string Sign(string payload)
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            var signature = _key.SignData(bytes, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                keyId = "test-key",
                payload = Convert.ToBase64String(bytes),
                signature = Convert.ToBase64String(signature)
            });
        }
        public ProductCatalogService Service(string document)
        {
            Handler.Document = document;
            var key = _key.ExportSubjectPublicKeyInfoPem();
            return new(new HttpClient(Handler), new SessionStore(), new(new("https://gateway.example/"), _directory,
                ImmutableDictionary<string, string>.Empty.Add("test-key", key)), new PathSecurity());
        }
        public void Dispose() { _key.Dispose(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string Document { get; set; } = string.Empty;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/v1/product-catalog", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(Document, Encoding.UTF8, "application/json") });
        }
    }
    private sealed class SessionStore : ISecureSessionStore
    {
        public Task<AuthSessionSecrets?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<AuthSessionSecrets?>(
            new("access-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1)));
        public Task SaveAsync(AuthSessionSecrets value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

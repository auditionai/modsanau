using System.Collections.Concurrent;
using System.Security.Cryptography;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Gateway.Services;

namespace Gateway.Tests;

public sealed class EntitlementGrantTests
{
    private static readonly AuthenticatedGatewayUser User = new(Guid.Parse("5b7a6974-cf2e-4629-b2d4-f076078f5a42"));

    [Fact]
    public async Task Entitled_user_receives_signed_scoped_grant_that_is_consumed_once()
    {
        using var context = Create();
        var descriptor = TemplateDescriptor();

        var issued = await context.Service.IssueAsync(User, descriptor);
        var accepted = await context.Service.ValidateAndConsumeAsync(issued.Grant!, User, descriptor);
        var replay = await context.Service.ValidateAndConsumeAsync(issued.Grant!, User, descriptor);

        Assert.Equal(TrustedServiceStatus.Succeeded, issued.Status);
        Assert.Equal(TrustedServiceStatus.Succeeded, accepted.Status);
        Assert.Null(accepted.Grant);
        Assert.Equal("ENTITLEMENT_GRANT_REPLAYED", replay.DiagnosticCode);
        Assert.Equal(User.UserId, context.Records.LastUser!.UserId);
        Assert.Equal("pointer_mod", context.Records.LastDescriptor!.ModId!.Value.Value);
    }

    [Fact]
    public async Task Grant_rejects_wrong_owner_scope_audience_and_tampering()
    {
        using var context = Create();
        var descriptor = TemplateDescriptor();
        var issued = await context.Service.IssueAsync(User, descriptor);
        var wrongUser = await context.Service.ValidateAndConsumeAsync(issued.Grant!, new(Guid.NewGuid()), descriptor);
        var wrongScope = await context.Service.ValidateAndConsumeAsync(issued.Grant!, User, PremiumAiDescriptor());
        var wrongAudienceDescriptor = descriptor with { Audience = EntitlementGrantDescriptor.PremiumAiAudience };
        var wrongAudience = await context.Service.ValidateAndConsumeAsync(issued.Grant!, User, wrongAudienceDescriptor);
        var wrongResource = await context.Service.ValidateAndConsumeAsync(issued.Grant!, User,
            descriptor with { ModId = new ModId("another_mod") });
        var grant = issued.Grant!;
        var characters = grant.ToCharArray();
        characters[^2] = characters[^2] == 'A' ? 'B' : 'A';
        var tampered = new string(characters);
        var tamperResult = await context.Service.ValidateAndConsumeAsync(tampered, User, descriptor);

        Assert.Equal("ENTITLEMENT_GRANT_CLAIMS_INVALID", wrongUser.DiagnosticCode);
        Assert.Equal("ENTITLEMENT_GRANT_CLAIMS_INVALID", wrongScope.DiagnosticCode);
        Assert.Equal("ENTITLEMENT_GRANT_INVALID", wrongAudience.DiagnosticCode);
        Assert.Equal("ENTITLEMENT_GRANT_CLAIMS_INVALID", wrongResource.DiagnosticCode);
        Assert.Equal("ENTITLEMENT_GRANT_SIGNATURE_INVALID", tamperResult.DiagnosticCode);
    }

    [Fact]
    public async Task Expired_grant_and_unavailable_replay_store_fail_closed()
    {
        using var context = Create();
        var descriptor = TemplateDescriptor();
        var issued = await context.Service.IssueAsync(User, descriptor);
        context.Time.Advance(SignedEntitlementGrantService.Lifetime + TimeSpan.FromSeconds(1));

        var expired = await context.Service.ValidateAndConsumeAsync(issued.Grant!, User, descriptor);

        Assert.Equal("ENTITLEMENT_GRANT_CLAIMS_INVALID", expired.DiagnosticCode);

        using var unavailable = Create(new UnavailableEntitlementNonceStore());
        var second = await unavailable.Service.IssueAsync(User, descriptor);
        var result = await unavailable.Service.ValidateAndConsumeAsync(second.Grant!, User, descriptor);
        Assert.Equal(TrustedServiceStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task Denied_or_offline_record_never_issues_premium_grant()
    {
        using var denied = Create(recordStatus: TrustedServiceStatus.Succeeded, granted: false);
        using var offline = Create(recordStatus: TrustedServiceStatus.Unavailable, granted: false);

        var deniedResult = await denied.Service.IssueAsync(User, TemplateDescriptor());
        var offlineResult = await offline.Service.IssueAsync(User, PremiumAiDescriptor());

        Assert.Null(deniedResult.Grant);
        Assert.Equal("ENTITLEMENT_DENIED", deniedResult.DiagnosticCode);
        Assert.Null(offlineResult.Grant);
        Assert.Equal(TrustedServiceStatus.Unavailable, offlineResult.Status);
    }

    [Fact]
    public async Task Premium_ai_grant_is_online_user_scoped_and_has_no_resource_claims()
    {
        using var context = Create();
        var descriptor = PremiumAiDescriptor();

        var issued = await context.Service.IssueAsync(User, descriptor);
        var accepted = await context.Service.ValidateAndConsumeAsync(issued.Grant!, User, descriptor);

        Assert.Equal(TrustedServiceStatus.Succeeded, accepted.Status);
        Assert.Null(context.Records.LastDescriptor!.Template);
        Assert.Null(context.Records.LastDescriptor.GameId);
    }

    private static TestContext Create(IEntitlementNonceStore? nonces = null,
        TrustedServiceStatus recordStatus = TrustedServiceStatus.Succeeded, bool granted = true)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.True(EntitlementSigningKey.TryCreate(ecdsa.ExportPkcs8PrivateKeyPem(), out var key));
        var records = new StubRecords(recordStatus, granted);
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero));
        return new(key!, records, time, new(records, nonces ?? new MemoryNonceStore(), key!, time));
    }

    private static EntitlementGrantDescriptor TemplateDescriptor() => new(
        PremiumEntitlementScope.PremiumTemplate, EntitlementGrantDescriptor.TemplateAudience,
        new(new("pointer"), new("v1"), new(new string('A', 64)), new("build-1")),
        new GameId("audition"), new ModId("pointer_mod"));
    private static EntitlementGrantDescriptor PremiumAiDescriptor() => new(
        PremiumEntitlementScope.PremiumAi, EntitlementGrantDescriptor.PremiumAiAudience, null, null, null);

    private sealed record TestContext(EntitlementSigningKey Key, StubRecords Records,
        ManualTimeProvider Time, SignedEntitlementGrantService Service) : IDisposable
    { public void Dispose() => Key.Dispose(); }

    private sealed class StubRecords(TrustedServiceStatus status, bool granted) : IEntitlementRecordService
    {
        public AuthenticatedGatewayUser? LastUser { get; private set; }
        public EntitlementGrantDescriptor? LastDescriptor { get; private set; }
        public Task<EntitlementRecordResult> CheckAsync(AuthenticatedGatewayUser user,
            EntitlementGrantDescriptor descriptor, CancellationToken cancellationToken = default)
        {
            LastUser = user; LastDescriptor = descriptor;
            return Task.FromResult(new EntitlementRecordResult(status,
                status == TrustedServiceStatus.Succeeded ? "ENTITLEMENT_RECORD_CHECKED" : "ENTITLEMENT_RECORD_UNAVAILABLE",
                granted));
        }
    }

    private sealed class MemoryNonceStore : IEntitlementNonceStore
    {
        private readonly ConcurrentDictionary<(Guid User, Guid Nonce), byte> _used = new();
        public Task<EntitlementNonceConsumeStatus> TryConsumeAsync(Guid userId, Guid nonce,
            DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
            Task.FromResult(_used.TryAdd((userId, nonce), 0)
                ? EntitlementNonceConsumeStatus.Consumed : EntitlementNonceConsumeStatus.Replay);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}

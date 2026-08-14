namespace AuditionModStudio.Core.Subscriptions;

public enum DeviceCommercialStatus { Active, Blocked, Revoked }
public enum SubscriptionStatus { None, Active, Expired, Suspended, Revoked }

public sealed record DeviceCapabilities(
    bool CanUseAi,
    bool CanBuild,
    bool CanExport,
    bool CanUsePremiumTemplates)
{
    public static DeviceCapabilities Denied { get; } = new(false, false, false, false);
}

public sealed record DeviceEntitlementSnapshot(
    string DeviceCode,
    DeviceCommercialStatus DeviceStatus,
    SubscriptionStatus SubscriptionStatus,
    DateTimeOffset? SubscriptionExpiresAt,
    long AvailableCredits,
    DeviceCapabilities Capabilities,
    DateTimeOffset ObservedAt,
    DateTimeOffset GrantExpiresAt,
    string SignedGrant);

public enum DeviceEntitlementResultStatus
{
    Succeeded,
    AuthenticationRequired,
    Rejected,
    Unavailable,
    InvalidResponse,
    Cancelled,
}

public sealed record DeviceEntitlementResult(
    DeviceEntitlementResultStatus Status,
    string DiagnosticCode,
    DeviceEntitlementSnapshot? Snapshot)
{
    public bool Succeeded => Status == DeviceEntitlementResultStatus.Succeeded && Snapshot is not null;
}

public interface IDeviceEntitlementService
{
    Task<DeviceEntitlementResult> RegisterAsync(CancellationToken cancellationToken = default);
    Task<DeviceEntitlementResult> RefreshAsync(CancellationToken cancellationToken = default);
    Task<DeviceEntitlementResult> RedeemGiftCodeAsync(string giftCode,
        CancellationToken cancellationToken = default);
}

public interface IDeviceEntitlementGrantStore
{
    Task<string?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(string signedGrant, CancellationToken cancellationToken = default);
    Task DeleteAsync(CancellationToken cancellationToken = default);
}

public interface ICapabilityAuthorizationService
{
    DeviceCapabilities Current { get; }
    bool HasUsableGrant { get; }
    void Apply(DeviceEntitlementSnapshot snapshot);
    void Clear();
}

public sealed class CapabilityAuthorizationService(TimeProvider timeProvider) : ICapabilityAuthorizationService
{
    private DeviceEntitlementSnapshot? _snapshot;
    public bool HasUsableGrant => _snapshot is { } value
        && value.GrantExpiresAt > timeProvider.GetUtcNow()
        && value.DeviceStatus == DeviceCommercialStatus.Active;
    public DeviceCapabilities Current => HasUsableGrant ? _snapshot!.Capabilities : DeviceCapabilities.Denied;
    public void Apply(DeviceEntitlementSnapshot snapshot) => _snapshot = snapshot
        ?? throw new ArgumentNullException(nameof(snapshot));
    public void Clear() => _snapshot = null;
}

public sealed class UnavailableDeviceEntitlementService : IDeviceEntitlementService
{
    private static DeviceEntitlementResult Result(CancellationToken token) => new(
        token.IsCancellationRequested ? DeviceEntitlementResultStatus.Cancelled : DeviceEntitlementResultStatus.Unavailable,
        token.IsCancellationRequested ? "DEVICE_ENTITLEMENT_CANCELLED" : "DEVICE_ENTITLEMENT_UNAVAILABLE", null);
    public Task<DeviceEntitlementResult> RegisterAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Result(cancellationToken));
    public Task<DeviceEntitlementResult> RefreshAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Result(cancellationToken));
    public Task<DeviceEntitlementResult> RedeemGiftCodeAsync(string giftCode,
        CancellationToken cancellationToken = default) => Task.FromResult(Result(cancellationToken));
}

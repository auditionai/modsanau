using AuditionModStudio.Core.Subscriptions;

namespace Core.Tests;

public sealed class Plan104CapabilityAuthorizationTests
{
    [Fact]
    public void Signed_snapshot_capabilities_fail_closed_after_grant_expiry()
    {
        var now = new DateTimeOffset(2026, 8, 14, 8, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(now);
        var service = new CapabilityAuthorizationService(time);
        service.Apply(new DeviceEntitlementSnapshot("AMS-2345-6789-ABCD", DeviceCommercialStatus.Active,
            SubscriptionStatus.Active, now.AddDays(30), 10, new(true, true, true, true), now,
            now.AddMinutes(5), "signed-grant"));

        Assert.True(service.HasUsableGrant);
        Assert.True(service.Current.CanUseAi);
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.False(service.HasUsableGrant);
        Assert.Equal(DeviceCapabilities.Denied, service.Current);
    }

    [Fact]
    public void Blocked_device_is_denied_even_before_grant_expiry()
    {
        var now = DateTimeOffset.UtcNow;
        var service = new CapabilityAuthorizationService(TimeProvider.System);
        service.Apply(new DeviceEntitlementSnapshot("AMS-2345-6789-ABCD", DeviceCommercialStatus.Blocked,
            SubscriptionStatus.Active, now.AddDays(1), 10, new(true, true, true, true), now,
            now.AddHours(1), "signed-grant"));
        Assert.Equal(DeviceCapabilities.Denied, service.Current);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }
}

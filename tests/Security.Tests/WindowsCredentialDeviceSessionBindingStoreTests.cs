using AuditionModStudio.Core.Auth;
using AuditionModStudio.Security;

namespace Security.Tests;

public sealed class WindowsCredentialDeviceSessionBindingStoreTests
{
    [Fact]
    public async Task Binding_roundtrips_in_separate_credential_without_auth_tokens()
    {
        var backend = new FakeBackend();
        using var store = new WindowsCredentialDeviceSessionBindingStore(backend);
        var binding = new DeviceSessionBinding(Guid.NewGuid(), Guid.NewGuid());

        await store.SaveAsync(binding);
        var loaded = await store.LoadAsync();

        Assert.Equal(binding, loaded);
        Assert.Equal(WindowsCredentialDeviceSessionBindingStore.TargetName, backend.LastTarget);
        Assert.Equal(33, backend.Bytes!.Length);
        Assert.DoesNotContain(typeof(DeviceSessionBinding).GetProperties(), property =>
            property.Name.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Device_identity_without_server_session_roundtrips_and_corruption_fails_closed()
    {
        var backend = new FakeBackend();
        using var store = new WindowsCredentialDeviceSessionBindingStore(backend);
        var binding = new DeviceSessionBinding(Guid.NewGuid(), null);
        await store.SaveAsync(binding);

        Assert.Equal(binding, await store.LoadAsync());
        backend.Bytes![0] = 99;
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
    }

    [Fact]
    public async Task Empty_identifiers_are_rejected_before_credential_write()
    {
        var backend = new FakeBackend();
        using var store = new WindowsCredentialDeviceSessionBindingStore(backend);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveAsync(new DeviceSessionBinding(Guid.Empty, null)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveAsync(new DeviceSessionBinding(Guid.NewGuid(), Guid.Empty)));
        Assert.Null(backend.Bytes);
    }

    private sealed class FakeBackend : IWindowsCredentialBackend
    {
        public byte[]? Bytes { get; set; }
        public string? LastTarget { get; private set; }
        public byte[]? Read(string targetName, int maximumBytes)
        {
            LastTarget = targetName;
            return Bytes?.ToArray();
        }
        public void Write(string targetName, byte[] bytes)
        {
            LastTarget = targetName;
            Bytes = bytes.ToArray();
        }
        public void Delete(string targetName)
        {
            LastTarget = targetName;
            Bytes = null;
        }
    }
}

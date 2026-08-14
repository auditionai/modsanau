using System.Security.Cryptography;
using System.Text;
using AuditionModStudio.Core.Subscriptions;

namespace AuditionModStudio.Security;

public sealed class WindowsCredentialDeviceEntitlementGrantStore : IDeviceEntitlementGrantStore, IDisposable
{
    internal const string TargetName = "AuditionAiModStudio/DeviceEntitlementGrant";
    private const int MaximumBytes = 2560;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly IWindowsCredentialBackend _backend;
    private int _disposed;
    public WindowsCredentialDeviceEntitlementGrantStore() : this(new WindowsCredentialBackend()) { }
    internal WindowsCredentialDeviceEntitlementGrantStore(IWindowsCredentialBackend backend) => _backend = backend;
    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this); EnsurePlatform();
        await Gate.WaitAsync(cancellationToken); byte[]? bytes=null;
        try { bytes=_backend.Read(TargetName,MaximumBytes); return bytes is null ? null : Encoding.UTF8.GetString(bytes); }
        finally { if(bytes is not null) CryptographicOperations.ZeroMemory(bytes); Gate.Release(); }
    }
    public async Task SaveAsync(string signedGrant, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(signedGrant)) throw new ArgumentException("A signed grant is required.",nameof(signedGrant));
        var bytes=Encoding.UTF8.GetBytes(signedGrant); if(bytes.Length>MaximumBytes) { CryptographicOperations.ZeroMemory(bytes); throw new ArgumentException("The signed grant is too large.",nameof(signedGrant)); }
        await Gate.WaitAsync(cancellationToken); try { _backend.Write(TargetName,bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); Gate.Release(); }
    }
    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    { await Gate.WaitAsync(cancellationToken); try { _backend.Delete(TargetName); } finally { Gate.Release(); } }
    private void EnsurePlatform() { if(!OperatingSystem.IsWindows() && _backend is WindowsCredentialBackend) throw new PlatformNotSupportedException(); }
    public void Dispose() => Interlocked.Exchange(ref _disposed,1);
}

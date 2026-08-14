using System.Security.Cryptography;
using AuditionModStudio.Core.Auth;

namespace AuditionModStudio.Security;

public sealed class WindowsCredentialDeviceSessionBindingStore : IDeviceSessionBindingStore, IDisposable
{
    internal const string TargetName = "AuditionAiModStudio/DeviceSession";
    private const int DocumentLength = 33;
    private const byte SchemaVersion = 1;
    private static readonly SemaphoreSlim ProcessGate = new(1, 1);
    private readonly IWindowsCredentialBackend _backend;
    private int _disposeState;

    public WindowsCredentialDeviceSessionBindingStore() : this(new WindowsCredentialBackend()) { }
    internal WindowsCredentialDeviceSessionBindingStore(IWindowsCredentialBackend backend) =>
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));

    public async Task<DeviceSessionBinding?> LoadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        EnsureWindowsOrTestBackend();
        await ProcessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? bytes = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytes = _backend.Read(TargetName, DocumentLength);
            if (bytes is null) return null;
            if (bytes.Length != DocumentLength || bytes[0] != SchemaVersion)
                throw new InvalidDataException("The stored device session binding is malformed.");
            var deviceId = new Guid(bytes.AsSpan(1, 16));
            var sessionId = new Guid(bytes.AsSpan(17, 16));
            var binding = new DeviceSessionBinding(deviceId, sessionId == Guid.Empty ? null : sessionId);
            return binding.IsValid
                ? binding
                : throw new InvalidDataException("The stored device session binding is invalid.");
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            ProcessGate.Release();
        }
    }

    public async Task SaveAsync(DeviceSessionBinding binding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!binding.IsValid) throw new ArgumentException("Device session binding is invalid.", nameof(binding));
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        EnsureWindowsOrTestBackend();
        var bytes = new byte[DocumentLength];
        bytes[0] = SchemaVersion;
        binding.DeviceId.TryWriteBytes(bytes.AsSpan(1, 16));
        binding.SessionId.GetValueOrDefault().TryWriteBytes(bytes.AsSpan(17, 16));
        await ProcessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _backend.Write(TargetName, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            ProcessGate.Release();
        }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        EnsureWindowsOrTestBackend();
        await ProcessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _backend.Delete(TargetName);
        }
        finally
        {
            ProcessGate.Release();
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _disposeState, 1);

    private void EnsureWindowsOrTestBackend()
    {
        if (!OperatingSystem.IsWindows() && _backend is WindowsCredentialBackend)
            throw new PlatformNotSupportedException("Windows Credential Manager is required.");
    }
}

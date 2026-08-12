using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuditionModStudio.Core.Auth;

namespace AuditionModStudio.Security;

public sealed class WindowsCredentialSessionStore : ISecureSessionStore, IDisposable
{
    private const string TargetName = "AuditionAiModStudio/SupabaseSession";
    private const int MaximumCredentialBlobBytes = 2_560;
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposeState;

    public async Task<AuthSessionSecrets?> LoadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        EnsureWindows();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CredRead(TargetName, CredentialTypeGeneric, 0, out var pointer))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorNotFound)
                {
                    return null;
                }
                throw CreateCredentialIOException(error, "read");
            }

            try
            {
                var credential = Marshal.PtrToStructure<Credential>(pointer);
                if (credential.CredentialBlobSize is 0 or > MaximumCredentialBlobBytes
                    || credential.CredentialBlob == IntPtr.Zero)
                {
                    throw new InvalidDataException("The stored authentication session is malformed.");
                }

                var bytes = new byte[checked((int)credential.CredentialBlobSize)];
                try
                {
                    Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                    var document = JsonSerializer.Deserialize<SessionDocument>(bytes, JsonOptions);
                    if (document is null)
                    {
                        throw new InvalidDataException("The stored authentication session is invalid.");
                    }

                    var session = new AuthSessionSecrets(
                        document.AccessToken, document.RefreshToken, document.ExpiresAt);
                    try
                    {
                        Validate(session);
                    }
                    catch (ArgumentException exception)
                    {
                        throw new InvalidDataException("The stored authentication session is invalid.", exception);
                    }
                    return session;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The stored authentication session cannot be decoded.", exception);
            }
            finally
            {
                CredFree(pointer);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AuthSessionSecrets session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        EnsureWindows();
        Validate(session);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new SessionDocument(session.AccessToken, session.RefreshToken, session.ExpiresAt), JsonOptions);
        if (bytes.Length > MaximumCredentialBlobBytes)
        {
            throw new InvalidDataException("The authentication session exceeds the secure credential size limit.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blob = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var credential = new Credential
                {
                    Type = CredentialTypeGeneric,
                    TargetName = TargetName,
                    CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = blob,
                    Persist = CredentialPersistLocalMachine,
                    UserName = "SupabaseSession",
                };
                if (!CredWrite(ref credential, 0))
                {
                    throw CreateCredentialIOException(Marshal.GetLastWin32Error(), "save");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                Marshal.FreeHGlobal(blob);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        EnsureWindows();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CredDelete(TargetName, CredentialTypeGeneric, 0))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorNotFound)
                {
                    throw CreateCredentialIOException(error, "delete");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Validate(AuthSessionSecrets session)
    {
        if (string.IsNullOrWhiteSpace(session.AccessToken)
            || string.IsNullOrWhiteSpace(session.RefreshToken)
            || session.ExpiresAt == default
            || session.AccessToken.Any(char.IsControl)
            || session.RefreshToken.Any(char.IsControl))
        {
            throw new ArgumentException("Authentication session secrets are invalid.", nameof(session));
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager is required.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            _gate.Dispose();
        }
    }

    private static IOException CreateCredentialIOException(int error, string operation) =>
        new($"Windows Credential Manager could not {operation} the session.", new Win32Exception(error));

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    private sealed record SessionDocument(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
}

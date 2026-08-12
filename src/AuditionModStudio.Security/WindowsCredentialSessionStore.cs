using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuditionModStudio.Core.Auth;

namespace AuditionModStudio.Security;

public sealed class WindowsCredentialSessionStore : ISecureSessionStore, IDisposable
{
    internal const string TargetName = "AuditionAiModStudio/SupabaseSession";
    internal const int MaximumCredentialBlobBytes = 2_560;
    private static readonly SemaphoreSlim ProcessGate = new(1, 1);
    private readonly IWindowsCredentialBackend _backend;
    private int _disposeState;

    public WindowsCredentialSessionStore() : this(new WindowsCredentialBackend()) { }
    internal WindowsCredentialSessionStore(IWindowsCredentialBackend backend) =>
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));

    public async Task<AuthSessionSecrets?> LoadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        EnsureWindowsOrTestBackend();
        await ProcessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? bytes = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytes = _backend.Read(TargetName, MaximumCredentialBlobBytes);
            if (bytes is null) return null;
            var decoded = WindowsCredentialSessionCodec.Decode(bytes);
            if (decoded.RequiresMigration)
            {
                var migrated = WindowsCredentialSessionCodec.Encode(decoded.Session, MaximumCredentialBlobBytes);
                try { _backend.Write(TargetName, migrated); }
                finally { CryptographicOperations.ZeroMemory(migrated); }
            }
            return decoded.Session;
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            ProcessGate.Release();
        }
    }

    public async Task SaveAsync(AuthSessionSecrets session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        EnsureWindowsOrTestBackend();
        var bytes = WindowsCredentialSessionCodec.Encode(session, MaximumCredentialBlobBytes);
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
        finally { ProcessGate.Release(); }
    }

    private void EnsureWindowsOrTestBackend()
    {
        if (!OperatingSystem.IsWindows() && _backend is WindowsCredentialBackend)
            throw new PlatformNotSupportedException("Windows Credential Manager is required.");
    }

    public void Dispose() => Interlocked.Exchange(ref _disposeState, 1);
}

internal sealed record SessionDecodeResult(AuthSessionSecrets Session, bool RequiresMigration);

internal static class WindowsCredentialSessionCodec
{
    private const int CurrentSchemaVersion = 1;
    private const int MaximumTokenCharacters = 2_048;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static byte[] Encode(AuthSessionSecrets session, int maximumBytes)
    {
        Validate(session);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new SessionDocument(
            CurrentSchemaVersion, session.AccessToken, session.RefreshToken, session.ExpiresAt), Options);
        if (bytes.Length is 0 || bytes.Length > maximumBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidDataException("The authentication session exceeds the secure credential size limit.");
        }
        return bytes;
    }

    public static SessionDecodeResult Decode(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > WindowsCredentialSessionStore.MaximumCredentialBlobBytes)
            throw new InvalidDataException("The stored authentication session is malformed.");
        try
        {
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The stored authentication session is invalid.");
            var hasSchema = json.RootElement.TryGetProperty("schemaVersion", out var schema);
            if (hasSchema && (schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != CurrentSchemaVersion))
                throw new InvalidDataException("The stored authentication session schema is unsupported.");

            AuthSessionSecrets session;
            if (hasSchema)
            {
                var document = JsonSerializer.Deserialize<SessionDocument>(bytes, Options)
                    ?? throw new InvalidDataException("The stored authentication session is invalid.");
                session = new(document.AccessToken, document.RefreshToken, document.ExpiresAt);
            }
            else
            {
                var legacy = JsonSerializer.Deserialize<LegacySessionDocument>(bytes, Options)
                    ?? throw new InvalidDataException("The legacy authentication session is invalid.");
                session = new(legacy.AccessToken, legacy.RefreshToken, legacy.ExpiresAt);
            }
            Validate(session);
            return new(session, RequiresMigration: !hasSchema);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The stored authentication session cannot be decoded.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The stored authentication session is invalid.", exception);
        }
    }

    private static void Validate(AuthSessionSecrets session)
    {
        if (session.AccessToken.Length > MaximumTokenCharacters || session.RefreshToken.Length > MaximumTokenCharacters)
            throw new InvalidDataException("Authentication session token exceeds the secure credential size limit.");
        if (string.IsNullOrWhiteSpace(session.AccessToken)
            || string.IsNullOrWhiteSpace(session.RefreshToken)
            || session.ExpiresAt == default || session.AccessToken.Any(char.IsControl)
            || session.RefreshToken.Any(char.IsControl))
            throw new ArgumentException("Authentication session secrets are invalid.", nameof(session));
    }

    private sealed record SessionDocument(int SchemaVersion, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
    private sealed record LegacySessionDocument(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
}

internal interface IWindowsCredentialBackend
{
    byte[]? Read(string targetName, int maximumBytes);
    void Write(string targetName, byte[] bytes);
    void Delete(string targetName);
}

internal sealed class WindowsCredentialBackend : IWindowsCredentialBackend
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public byte[]? Read(string targetName, int maximumBytes)
    {
        if (!CredRead(targetName, CredentialTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw CreateCredentialIOException(error, "read");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize is 0 || credential.CredentialBlobSize > maximumBytes
                || credential.CredentialBlob == IntPtr.Zero)
                throw new InvalidDataException("The stored authentication session is malformed.");
            var bytes = new byte[checked((int)credential.CredentialBlobSize)];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { CredFree(pointer); }
    }

    public void Write(string targetName, byte[] bytes)
    {
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = CredentialTypeGeneric,
                TargetName = targetName,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = "SupabaseSession",
            };
            if (!CredWrite(ref credential, 0)) throw CreateCredentialIOException(Marshal.GetLastWin32Error(), "save");
        }
        finally
        {
            for (var index = 0; index < bytes.Length; index++) Marshal.WriteByte(blob, index, 0);
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete(string targetName)
    {
        if (!CredDelete(targetName, CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound) throw CreateCredentialIOException(error, "delete");
        }
    }

    private static IOException CreateCredentialIOException(int error, string operation) =>
        new($"Windows Credential Manager could not {operation} the session.", new Win32Exception(error));

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags; public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize; public IntPtr CredentialBlob; public uint Persist;
        public uint AttributeCount; public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }
}

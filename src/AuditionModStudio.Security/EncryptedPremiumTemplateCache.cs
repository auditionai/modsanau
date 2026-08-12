using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Paths;

namespace AuditionModStudio.Security;

public interface ITemplateCacheKeyProtector
{
    byte[] Protect(ReadOnlySpan<byte> key, ReadOnlySpan<byte> context);
    byte[] Unprotect(ReadOnlySpan<byte> wrappedKey, ReadOnlySpan<byte> context);
}

public sealed class EncryptedPremiumTemplateCache(
    IAppPaths appPaths,
    ITemplateCacheKeyProtector keyProtector) : IPremiumTemplateCache
{
    private const int SchemaVersion = 1;
    private const int ChunkSize = 1024 * 1024;
    private const int TagSize = 16;
    private const int MaximumWrappedKeyBytes = 16 * 1024;
    private static readonly byte[] Magic = "AMSCTC01"u8.ToArray();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<PremiumTemplateCacheResult> StoreAsync(PremiumTemplatePackageManifest manifest, Stream package,
        CancellationToken cancellationToken = default)
    {
        if (manifest is not { IsValid: true } || package is null || !package.CanRead)
            return Result(PremiumTemplateCacheStatus.InvalidRequest, "TEMPLATE_CACHE_REQUEST_INVALID");
        var path = CachePath(manifest);
        var gate = Gates.GetOrAdd(path, _ => new(1, 1));
        try { await gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Cancelled(); }
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[]? key = null;
        byte[]? wrapped = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            key = RandomNumberGenerator.GetBytes(32);
            var context = Context(manifest);
            try { wrapped = keyProtector.Protect(key, context); }
            finally { CryptographicOperations.ZeroMemory(context); }
            if (wrapped is not { Length: > 0 and <= MaximumWrappedKeyBytes })
                return Result(PremiumTemplateCacheStatus.KeyUnavailable, "TEMPLATE_CACHE_KEY_WRAP_FAILED");

            var noncePrefix = RandomNumberGenerator.GetBytes(8);
            var header = CreateHeader(manifest, noncePrefix, wrapped);
            var aad = SHA256.HashData(header);
            try
            {
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    ChunkSize, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                    var status = await EncryptAsync(package, output, manifest.ContentLength,
                        manifest.Identity.Sha256.Value, key, noncePrefix, aad,
                        cancellationToken).ConfigureAwait(false);
                    if (status is not null) return status;
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(true);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(noncePrefix);
                CryptographicOperations.ZeroMemory(header);
                CryptographicOperations.ZeroMemory(aad);
            }
            File.Move(temp, path, true);
            return Result(PremiumTemplateCacheStatus.Succeeded, "TEMPLATE_CACHE_STORED");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Cancelled(); }
        catch (CryptographicException) { return Result(PremiumTemplateCacheStatus.KeyUnavailable, "TEMPLATE_CACHE_KEY_WRAP_FAILED"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        { return Result(PremiumTemplateCacheStatus.IoFailure, "TEMPLATE_CACHE_WRITE_FAILED"); }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (wrapped is not null) CryptographicOperations.ZeroMemory(wrapped);
            TryDelete(temp);
            gate.Release();
        }
    }

    public async Task<PremiumTemplateCacheResult> MaterializeAsync(PremiumTemplatePackageManifest manifest,
        string controlledWorkspaceDirectory, CancellationToken cancellationToken = default)
    {
        if (manifest is not { IsValid: true } || !IsControlledWorkspace(controlledWorkspaceDirectory))
            return Result(PremiumTemplateCacheStatus.InvalidRequest, "TEMPLATE_CACHE_REQUEST_INVALID");
        var path = CachePath(manifest);
        var gate = Gates.GetOrAdd(path, _ => new(1, 1));
        try { await gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Cancelled(); }
        var destination = Path.Combine(Path.GetFullPath(controlledWorkspaceDirectory),
            $"template-{Guid.NewGuid():N}.package");
        var temp = destination + ".tmp";
        byte[]? key = null;
        try
        {
            if (!File.Exists(path)) return Result(PremiumTemplateCacheStatus.Missing, "TEMPLATE_CACHE_MISSING");
            Directory.CreateDirectory(controlledWorkspaceDirectory);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var parsed = await ReadHeaderAsync(input, cancellationToken).ConfigureAwait(false);
            if (parsed is null || parsed.Manifest != manifest) return Corrupt(path, "TEMPLATE_CACHE_METADATA_INVALID");
            var context = Context(manifest);
            try { key = keyProtector.Unprotect(parsed.WrappedKey, context); }
            catch (CryptographicException) { return KeyLost(path); }
            finally
            {
                CryptographicOperations.ZeroMemory(context);
                CryptographicOperations.ZeroMemory(parsed.WrappedKey);
            }
            if (key is not { Length: 32 }) return KeyLost(path);
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                ChunkSize, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var status = await DecryptAsync(input, output, parsed, key, cancellationToken).ConfigureAwait(false);
                if (status is not null) return Corrupt(path, status.DiagnosticCode);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }
            File.Move(temp, destination);
            return new(PremiumTemplateCacheStatus.Succeeded, "TEMPLATE_CACHE_MATERIALIZED", destination);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Cancelled(); }
        catch (CryptographicException) { return Corrupt(path, "TEMPLATE_CACHE_AUTHENTICATION_FAILED"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        { return Result(PremiumTemplateCacheStatus.IoFailure, "TEMPLATE_CACHE_READ_FAILED"); }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            TryDelete(temp);
            gate.Release();
        }
    }

    public async Task<PremiumTemplateCacheResult> DeleteAsync(PremiumTemplatePackageManifest manifest,
        CancellationToken cancellationToken = default)
    {
        if (manifest is not { IsValid: true })
            return Result(PremiumTemplateCacheStatus.InvalidRequest, "TEMPLATE_CACHE_REQUEST_INVALID");
        var path = CachePath(manifest);
        var gate = Gates.GetOrAdd(path, _ => new(1, 1));
        try { await gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Cancelled(); }
        try
        {
            return TryDelete(path)
            ? Result(PremiumTemplateCacheStatus.Succeeded, "TEMPLATE_CACHE_DELETED")
            : Result(PremiumTemplateCacheStatus.IoFailure, "TEMPLATE_CACHE_DELETE_FAILED");
        }
        finally { gate.Release(); }
    }

    private async Task<PremiumTemplateCacheResult?> EncryptAsync(Stream input, Stream output, long expectedLength,
        string expectedSha256, byte[] key, byte[] noncePrefix, byte[] aad, CancellationToken token)
    {
        var plain = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var cipher = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var tag = new byte[TagSize];
        var nonce = new byte[12];
        noncePrefix.CopyTo(nonce, 0);
        long total = 0; uint counter = 0;
        using var aes = new AesGcm(key, TagSize);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            while (total < expectedLength)
            {
                var wanted = (int)Math.Min(ChunkSize, expectedLength - total);
                var read = await ReadUpToAsync(input, plain, wanted, token).ConfigureAwait(false);
                if (read == 0) return Result(PremiumTemplateCacheStatus.Corrupt, "TEMPLATE_CACHE_SOURCE_TRUNCATED");
                WriteCounter(nonce, counter++);
                aes.Encrypt(nonce, plain.AsSpan(0, read), cipher.AsSpan(0, read), tag, aad);
                hash.AppendData(plain, 0, read);
                await output.WriteAsync(BitConverter.GetBytes(read), token).ConfigureAwait(false);
                await output.WriteAsync(cipher.AsMemory(0, read), token).ConfigureAwait(false);
                await output.WriteAsync(tag, token).ConfigureAwait(false);
                total += read;
            }
            if (await input.ReadAsync(plain.AsMemory(0, 1), token).ConfigureAwait(false) != 0)
                return Result(PremiumTemplateCacheStatus.Corrupt, "TEMPLATE_CACHE_SOURCE_OVERSIZED");
            var digest = hash.GetHashAndReset();
            try
            {
                return Convert.ToHexString(digest) == expectedSha256 ? null
                : Result(PremiumTemplateCacheStatus.Corrupt, "TEMPLATE_CACHE_SOURCE_HASH_MISMATCH");
            }
            finally { CryptographicOperations.ZeroMemory(digest); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain.AsSpan()); ArrayPool<byte>.Shared.Return(plain);
            CryptographicOperations.ZeroMemory(cipher.AsSpan()); ArrayPool<byte>.Shared.Return(cipher);
            CryptographicOperations.ZeroMemory(tag); CryptographicOperations.ZeroMemory(nonce);
        }
    }

    private async Task<PremiumTemplateCacheResult?> DecryptAsync(Stream input, Stream output, ParsedHeader parsed,
        byte[] key, CancellationToken token)
    {
        var cipher = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var plain = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var tag = new byte[TagSize]; var nonce = new byte[12]; parsed.NoncePrefix.CopyTo(nonce, 0);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var aes = new AesGcm(key, TagSize);
        long total = 0; uint counter = 0;
        try
        {
            while (total < parsed.Manifest.ContentLength)
            {
                var lengthBytes = new byte[4];
                if (await ReadUpToAsync(input, lengthBytes, 4, token).ConfigureAwait(false) != 4)
                    return Result(PremiumTemplateCacheStatus.Corrupt, "TEMPLATE_CACHE_TRUNCATED");
                var length = BitConverter.ToInt32(lengthBytes);
                if (length is <= 0 or > ChunkSize || total + length > parsed.Manifest.ContentLength)
                    return Result(PremiumTemplateCacheStatus.Corrupt, "TEMPLATE_CACHE_CHUNK_INVALID");
                if (await ReadUpToAsync(input, cipher, length, token).ConfigureAwait(false) != length
                    || await ReadUpToAsync(input, tag, TagSize, token).ConfigureAwait(false) != TagSize)
                    return Result(PremiumTemplateCacheStatus.Corrupt, "TEMPLATE_CACHE_TRUNCATED");
                WriteCounter(nonce, counter++);
                aes.Decrypt(nonce, cipher.AsSpan(0, length), tag, plain.AsSpan(0, length), parsed.HeaderHash);
                await output.WriteAsync(plain.AsMemory(0, length), token).ConfigureAwait(false);
                hash.AppendData(plain, 0, length); total += length;
            }
            if (input.ReadByte() != -1) return Result(PremiumTemplateCacheStatus.Corrupt, "TEMPLATE_CACHE_TRAILING_DATA");
            var digest = hash.GetHashAndReset();
            try
            {
                return Convert.ToHexString(digest) == parsed.Manifest.Identity.Sha256.Value ? null
                : Result(PremiumTemplateCacheStatus.Corrupt, "TEMPLATE_CACHE_HASH_MISMATCH");
            }
            finally { CryptographicOperations.ZeroMemory(digest); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cipher.AsSpan()); ArrayPool<byte>.Shared.Return(cipher);
            CryptographicOperations.ZeroMemory(plain.AsSpan()); ArrayPool<byte>.Shared.Return(plain);
            CryptographicOperations.ZeroMemory(tag); CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(parsed.NoncePrefix); CryptographicOperations.ZeroMemory(parsed.HeaderHash);
        }
    }

    private static byte[] CreateHeader(PremiumTemplatePackageManifest manifest, byte[] nonce, byte[] wrapped)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(Magic); writer.Write(SchemaVersion); WriteString(writer, manifest.Identity.TemplateId.Value);
        WriteString(writer, manifest.Identity.Version.Value); WriteString(writer, manifest.Identity.Sha256.Value);
        WriteString(writer, manifest.Identity.CompatibleGameBuild.Value); WriteString(writer, manifest.GameId.Value);
        WriteString(writer, manifest.ModId.Value); writer.Write(manifest.ContentLength); WriteString(writer, manifest.MediaType);
        writer.Write(nonce.Length); writer.Write(nonce); writer.Write(wrapped.Length); writer.Write(wrapped); writer.Flush();
        return stream.ToArray();
    }

    private static async Task<ParsedHeader?> ReadHeaderAsync(Stream input, CancellationToken token)
    {
        var captured = new MemoryStream();
        try
        {
            var magic = new byte[Magic.Length]; if (await ReadCaptured(input, captured, magic, token) != magic.Length
                || !magic.AsSpan().SequenceEqual(Magic)) return null;
            var schema = await ReadInt32(input, captured, token); if (schema != SchemaVersion) return null;
            var id = await ReadString(input, captured, TemplateId.MaximumLength, token);
            var version = await ReadString(input, captured, TemplateVersion.MaximumLength, token);
            var sha = await ReadString(input, captured, 64, token);
            var build = await ReadString(input, captured, CompatibleGameBuild.MaximumLength, token);
            var game = await ReadString(input, captured, 64, token); var mod = await ReadString(input, captured, 64, token);
            var length = await ReadInt64(input, captured, token); var media = await ReadString(input, captured, 128, token);
            var nonceLength = await ReadInt32(input, captured, token); if (nonceLength != 8) return null;
            var nonce = new byte[8]; if (await ReadCaptured(input, captured, nonce, token) != 8) return null;
            var wrappedLength = await ReadInt32(input, captured, token);
            if (wrappedLength is <= 0 or > MaximumWrappedKeyBytes) return null;
            var wrapped = new byte[wrappedLength!.Value];
            if (await ReadCaptured(input, captured, wrapped, token) != wrapped.Length) return null;
            var manifest = new PremiumTemplatePackageManifest(new(new(id!), new(version!), new(sha!), new(build!)),
                new(game!), new(mod!), length, media!);
            var capturedBytes = captured.ToArray();
            try { return manifest.IsValid ? new(manifest, nonce, wrapped, SHA256.HashData(capturedBytes)) : null; }
            finally { CryptographicOperations.ZeroMemory(capturedBytes); }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or OverflowException) { return null; }
        finally { CryptographicOperations.ZeroMemory(captured.GetBuffer().AsSpan(0, (int)captured.Length)); captured.Dispose(); }
    }

    private string CachePath(PremiumTemplatePackageManifest manifest)
    {
        var identity = Context(manifest);
        try
        {
            return Path.Combine(Path.GetFullPath(appPaths.SecureTemplateCacheDirectory),
            manifest.Identity.TemplateId.Value, Convert.ToHexString(identity) + ".cache");
        }
        finally { CryptographicOperations.ZeroMemory(identity); }
    }
    private bool IsControlledWorkspace(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appPaths.WorkspacesDirectory)) + Path.DirectorySeparatorChar;
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !HasReparsePoint(root, path);
    }
    private static bool HasReparsePoint(string root, string path)
    {
        var current = Path.TrimEndingDirectorySeparator(path);
        while (current.Length >= root.Length)
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            var parent = Path.GetDirectoryName(current); if (parent is null || parent == current) break; current = parent;
        }
        return false;
    }
    private static byte[] Context(PremiumTemplatePackageManifest manifest) => SHA256.HashData(Encoding.UTF8.GetBytes(
        $"AMS-CACHE-V1\n{manifest.Identity.TemplateId.Value}\n{manifest.Identity.Version.Value}\n{manifest.Identity.Sha256.Value}"));
    private PremiumTemplateCacheResult Corrupt(string path, string code) => TryDelete(path)
        ? Result(PremiumTemplateCacheStatus.Corrupt, code)
        : Result(PremiumTemplateCacheStatus.IoFailure, "TEMPLATE_CACHE_CORRUPT_CLEANUP_FAILED");
    private PremiumTemplateCacheResult KeyLost(string path) => TryDelete(path)
        ? Result(PremiumTemplateCacheStatus.KeyUnavailable, "TEMPLATE_CACHE_KEY_UNAVAILABLE")
        : Result(PremiumTemplateCacheStatus.IoFailure, "TEMPLATE_CACHE_KEY_CLEANUP_FAILED");
    private static PremiumTemplateCacheResult Result(PremiumTemplateCacheStatus status, string code) => new(status, code);
    private static PremiumTemplateCacheResult Cancelled() => Result(PremiumTemplateCacheStatus.Cancelled, "TEMPLATE_CACHE_CANCELLED");
    private static bool TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); return true; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Template cache cleanup failed: {0}", exception.GetType().Name);
            return false;
        }
    }
    private static void WriteCounter(byte[] nonce, uint value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), value);
    private static void WriteString(BinaryWriter writer, string value) { var bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
    private static async Task<int> ReadUpToAsync(Stream stream, byte[] buffer, int count, CancellationToken token)
    { var total = 0; while (total < count) { var read = await stream.ReadAsync(buffer.AsMemory(total, count - total), token); if (read == 0) break; total += read; } return total; }
    private static async Task<int> ReadCaptured(Stream input, Stream captured, byte[] value, CancellationToken token)
    { var read = await ReadUpToAsync(input, value, value.Length, token); if (read > 0) await captured.WriteAsync(value.AsMemory(0, read), token); return read; }
    private static async Task<int?> ReadInt32(Stream input, Stream captured, CancellationToken token)
    { var b = new byte[4]; return await ReadCaptured(input, captured, b, token) == 4 ? BitConverter.ToInt32(b) : null; }
    private static async Task<long> ReadInt64(Stream input, Stream captured, CancellationToken token)
    { var b = new byte[8]; if (await ReadCaptured(input, captured, b, token) != 8) throw new IOException(); return BitConverter.ToInt64(b); }
    private static async Task<string?> ReadString(Stream input, Stream captured, int max, CancellationToken token)
    { var length = await ReadInt32(input, captured, token); if (length is null or <= 0 || length > max * 4) return null; var b = new byte[length.Value]; return await ReadCaptured(input, captured, b, token) == b.Length ? new UTF8Encoding(false, true).GetString(b) : null; }
    private sealed record ParsedHeader(PremiumTemplatePackageManifest Manifest, byte[] NoncePrefix, byte[] WrappedKey, byte[] HeaderHash);
}

public sealed class WindowsDpapiTemplateCacheKeyProtector : ITemplateCacheKeyProtector
{
    private const uint UiForbidden = 1;
    public byte[] Protect(ReadOnlySpan<byte> key, ReadOnlySpan<byte> context) => Transform(key, context, true);
    public byte[] Unprotect(ReadOnlySpan<byte> wrappedKey, ReadOnlySpan<byte> context) => Transform(wrappedKey, context, false);

    private static byte[] Transform(ReadOnlySpan<byte> input, ReadOnlySpan<byte> context, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows DPAPI is required.");
        if (input.IsEmpty || context.IsEmpty || input.Length > MaximumWrappedKeyBytes || context.Length > 1024)
            throw new ArgumentException("Template cache key protection input is invalid.");
        var inputBytes = input.ToArray(); var contextBytes = context.ToArray();
        var inputBlob = Blob(inputBytes); var entropyBlob = Blob(contextBytes); DataBlob output = default;
        try
        {
            var succeeded = protect
                ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!succeeded || output.Data == IntPtr.Zero || output.Size is <= 0 or > MaximumWrappedKeyBytes)
                throw new CryptographicException(Marshal.GetLastWin32Error());
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(inputBytes); CryptographicOperations.ZeroMemory(contextBytes);
            Free(inputBlob); Free(entropyBlob); FreeLocal(output);
        }
    }
    private const int MaximumWrappedKeyBytes = 16 * 1024;
    private static DataBlob Blob(byte[] bytes) { var pointer = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, pointer, bytes.Length); return new(bytes.Length, pointer); }
    private static void Free(DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero) return;
        var zeros = new byte[blob.Size];
        Marshal.Copy(zeros, 0, blob.Data, zeros.Length);
        Marshal.FreeHGlobal(blob.Data);
    }
    private static void FreeLocal(DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero) return;
        var zeros = new byte[blob.Size];
        Marshal.Copy(zeros, 0, blob.Data, zeros.Length);
        LocalFree(blob.Data);
    }
    [StructLayout(LayoutKind.Sequential)] private struct DataBlob(int size, IntPtr data) { public int Size = size; public IntPtr Data = data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob optionalEntropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob optionalEntropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}

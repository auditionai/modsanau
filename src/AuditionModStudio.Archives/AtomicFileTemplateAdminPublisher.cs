using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Paths;

namespace AuditionModStudio.Archives;

public sealed class TemplateAdminPublisherKeys : IDisposable
{
    private readonly byte[] _authenticationKey;
    private readonly byte[] _encryptionKey;
    private readonly ECDsa _signingKey;
    private int _disposed;

    public TemplateAdminPublisherKeys(ReadOnlySpan<byte> encryptionKey,
        ReadOnlySpan<byte> authenticationKey, string signingPrivateKeyPem)
    {
        if (encryptionKey.Length != 32 || authenticationKey.Length != 32
            || string.IsNullOrWhiteSpace(signingPrivateKeyPem) || signingPrivateKeyPem.Length > 16_384)
            throw new ArgumentException("Template publisher requires two 256-bit keys and one bounded signing key.");
        _encryptionKey = encryptionKey.ToArray();
        _authenticationKey = authenticationKey.ToArray();
        _signingKey = ECDsa.Create();
        try
        {
            _signingKey.ImportFromPem(signingPrivateKeyPem);
            if (_signingKey.KeySize != 256) throw new CryptographicException("P-256 is required.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal ICryptoTransform CreateEncryptor(byte[] iv)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = _encryptionKey;
        aes.IV = iv;
        return new OwnedCryptoTransform(aes, aes.CreateEncryptor());
    }

    internal IncrementalHash CreateAuthenticator()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, _authenticationKey);
    }

    internal ImmutableArray<byte> Sign(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        lock (_signingKey)
            return ImmutableArray.Create(_signingKey.SignData(data, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    public override string ToString() => "TemplateAdminPublisherKeys { [REDACTED] }";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CryptographicOperations.ZeroMemory(_encryptionKey);
        CryptographicOperations.ZeroMemory(_authenticationKey);
        _signingKey.Dispose();
    }

    private sealed class OwnedCryptoTransform(Aes owner, ICryptoTransform transform) : ICryptoTransform
    {
        public bool CanReuseTransform => transform.CanReuseTransform;
        public bool CanTransformMultipleBlocks => transform.CanTransformMultipleBlocks;
        public int InputBlockSize => transform.InputBlockSize;
        public int OutputBlockSize => transform.OutputBlockSize;
        public int TransformBlock(byte[] inputBuffer, int inputOffset, int inputCount, byte[] outputBuffer,
            int outputOffset) => transform.TransformBlock(inputBuffer, inputOffset, inputCount, outputBuffer, outputOffset);
        public byte[] TransformFinalBlock(byte[] inputBuffer, int inputOffset, int inputCount) =>
            transform.TransformFinalBlock(inputBuffer, inputOffset, inputCount);
        public void Dispose() { transform.Dispose(); owner.Dispose(); }
    }
}

public sealed class AtomicFileTemplateAdminPublisher(
    string publishRoot,
    TemplateAdminPublisherKeys keys,
    IPathSecurity pathSecurity) : ITemplateAdminPublisher
{
    private static readonly byte[] EncryptionHeader = Encoding.ASCII.GetBytes("AMS-TEMPLATE-ENC-V1\n");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public async Task<TemplateAdminPublishResult> PublishAtomicallyAsync(TemplateAdminPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || request.PackageManifest is not { IsValid: true }
            || request.TextureManifest is null || request.AuditEvent is null
            || request.RequiredEncryption != TemplateAdminStorageEncryption.ServerManaged
            || request.TextureManifest.GameId != request.PackageManifest.GameId
            || request.TextureManifest.ModId != request.PackageManifest.ModId
            || request.AuditEvent.Identity != request.PackageManifest.Identity
            || request.AuditEvent.GameId != request.PackageManifest.GameId
            || request.AuditEvent.ModId != request.PackageManifest.ModId
            || request.AuditEvent.OperationId == Guid.Empty
            || request.AuditEvent.AdminSubjectId == Guid.Empty
            || !HasConsistentDdsInventory(request)
            || request.AuditEvent.Action != "template_version_published"
            || !Path.IsPathFullyQualified(publishRoot) || !Path.IsPathFullyQualified(request.SourceArchivePath))
            return Failed("TEMPLATE_ADMIN_PUBLISH_REQUEST_INVALID");
        var stage = string.Empty;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(publishRoot);
            pathSecurity.EnsureNoReparsePoints(publishRoot, publishRoot);
            var templateRoot = pathSecurity.ResolvePathWithinRoot(publishRoot,
                request.PackageManifest.Identity.TemplateId.Value);
            Directory.CreateDirectory(templateRoot);
            pathSecurity.EnsureNoReparsePoints(publishRoot, templateRoot);
            var destination = pathSecurity.ResolvePathWithinRoot(templateRoot,
                request.PackageManifest.Identity.Version.Value);
            if (Directory.Exists(destination)) return Conflict();
            stage = pathSecurity.ResolvePathWithinRoot(publishRoot, $".stage-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stage);
            pathSecurity.EnsureNoReparsePoints(publishRoot, stage);

            var encryptedPackagePath = Path.Combine(stage, "package.amtenc");
            await EncryptAsync(request.SourceArchivePath, encryptedPackagePath,
                request.PackageManifest, cancellationToken).ConfigureAwait(false);
            var audit = new AuditDto(1, request.AuditEvent.OperationId, request.AuditEvent.AdminSubjectId,
                request.AuditEvent.Identity.TemplateId.Value, request.AuditEvent.Identity.Version.Value,
                request.AuditEvent.GameId.Value, request.AuditEvent.ModId.Value,
                request.AuditEvent.DdsCount, request.AuditEvent.OccurredAt, request.AuditEvent.Action);
            var auditBytes = JsonSerializer.SerializeToUtf8Bytes(audit, JsonOptions);
            var unsignedMetadata = CreateUnsignedMetadata(request, Convert.ToHexString(SHA256.HashData(auditBytes)));
            var signingBytes = JsonSerializer.SerializeToUtf8Bytes(unsignedMetadata, JsonOptions);
            ImmutableArray<byte> signature;
            try { signature = keys.Sign(signingBytes); }
            finally { CryptographicOperations.ZeroMemory(signingBytes); }
            await WriteJsonAsync(Path.Combine(stage, "metadata.json"),
                new MetadataDto(unsignedMetadata, Convert.ToBase64String(signature.AsSpan())),
                cancellationToken).ConfigureAwait(false);
            await WriteBytesAsync(Path.Combine(stage, "audit.json"), auditBytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            pathSecurity.EnsureNoReparsePoints(publishRoot, stage);
            if (Directory.Exists(destination)) return Conflict();
            Directory.Move(stage, destination);
            stage = string.Empty;
            return new(true, "TEMPLATE_ADMIN_PUBLISH_COMMITTED", request.PackageManifest.Identity,
                TemplateAdminStorageEncryption.ServerManaged, signature,
                request.AuditEvent.OperationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Failed("TEMPLATE_ADMIN_CANCELLED"); }
        catch (IOException)
        {
            var destination = Path.Combine(publishRoot, request.PackageManifest.Identity.TemplateId.Value,
                request.PackageManifest.Identity.Version.Value);
            return Directory.Exists(destination) ? Conflict() : Failed("TEMPLATE_ADMIN_PUBLISH_IO_FAILED");
        }
        catch (UnauthorizedAccessException) { return Failed("TEMPLATE_ADMIN_PUBLISH_ACCESS_DENIED"); }
        catch (ArgumentException) { return Failed("TEMPLATE_ADMIN_PUBLISH_REQUEST_INVALID"); }
        catch (InvalidOperationException) { return Failed("TEMPLATE_ADMIN_PUBLISH_PATH_UNSAFE"); }
        catch (CryptographicException) { return Failed("TEMPLATE_ADMIN_PUBLISH_CRYPTO_FAILED"); }
        finally
        {
            if (!string.IsNullOrEmpty(stage) && Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }

    private async Task EncryptAsync(string sourcePath, string destinationPath,
        PremiumTemplatePackageManifest manifest,
        CancellationToken cancellationToken)
    {
        var sourceInfo = new FileInfo(sourcePath);
        if (!sourceInfo.Exists)
            throw new ArgumentException("Source package is invalid.", nameof(sourcePath));
        var sourceRoot = sourceInfo.DirectoryName!;
        pathSecurity.EnsureNoReparsePoints(sourceRoot, sourceInfo.FullName);
        var iv = RandomNumberGenerator.GetBytes(16);
        try
        {
            await using var input = new FileStream(sourceInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length is <= 0 or > PremiumTemplatePackageManifest.MaximumPackageBytes
                || input.Length != manifest.ContentLength
                || !string.Equals(await ComputeSha256Async(input, cancellationToken).ConfigureAwait(false),
                    manifest.Identity.Sha256.Value, StringComparison.Ordinal))
                throw new CryptographicException("Source package identity mismatch.");
            input.Position = 0;
            await using (var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(EncryptionHeader, cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(iv, cancellationToken).ConfigureAwait(false);
                using var encryptor = keys.CreateEncryptor(iv);
                await using var crypto = new CryptoStream(output, encryptor, CryptoStreamMode.Write, leaveOpen: true);
                await input.CopyToAsync(crypto, 64 * 1024, cancellationToken).ConfigureAwait(false);
                crypto.FlushFinalBlock();
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }
            var tag = await AuthenticateAsync(destinationPath, cancellationToken).ConfigureAwait(false);
            try
            {
                await using var append = new FileStream(destinationPath, FileMode.Append, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await append.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
                await append.FlushAsync(cancellationToken).ConfigureAwait(false);
                append.Flush(true);
            }
            finally { CryptographicOperations.ZeroMemory(tag); }
        }
        finally { CryptographicOperations.ZeroMemory(iv); }
    }

    private async Task<byte[]> AuthenticateAsync(string path, CancellationToken cancellationToken)
    {
        using var hmac = keys.CreateAuthenticator();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                hmac.AppendData(buffer, 0, read);
            }
            return hmac.GetHashAndReset();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer.AsSpan());
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer.AsSpan());
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        try
        {
            await WriteBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static async Task WriteBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static byte[] CreateMetadataSigningBytes(TemplateAdminPublishRequest request, byte[] auditBytes) =>
        JsonSerializer.SerializeToUtf8Bytes(CreateUnsignedMetadata(request,
            Convert.ToHexString(SHA256.HashData(auditBytes))), JsonOptions);

    private static bool HasConsistentDdsInventory(TemplateAdminPublishRequest request)
    {
        if (request.DdsRecords.IsDefault || request.AuditEvent.DdsCount != request.DdsRecords.Length
            || request.TextureManifest.Slots.Length != request.DdsRecords.Length
            || request.DdsRecords.Any(record => !record.RelativePath.IsValid || !record.ContentSha256.IsValid
                || record.Width <= 0 || record.Height <= 0 || record.MipLevels == 0
                || record.Format == DdsFormat.Unknown
                || !Enum.IsDefined(record.Format) || !Enum.IsDefined(record.HeaderType)))
            return false;
        var texturePaths = request.TextureManifest.Slots.Select(slot => slot.RelativePath.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return texturePaths.Count == request.TextureManifest.Slots.Length
            && request.DdsRecords.Select(record => record.RelativePath.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(texturePaths);
    }

    private static UnsignedMetadataDto CreateUnsignedMetadata(TemplateAdminPublishRequest request, string auditSha256) => new(
        1, request.PackageManifest.Identity.TemplateId.Value,
        request.PackageManifest.Identity.Version.Value, request.PackageManifest.Identity.Sha256.Value,
        request.PackageManifest.Identity.CompatibleGameBuild.Value, request.PackageManifest.GameId.Value,
        request.PackageManifest.ModId.Value, request.PackageManifest.ContentLength, request.PackageManifest.MediaType,
        "AES-256-CBC-HMAC-SHA256", "AMS-TEMPLATE-METADATA-V1", auditSha256,
        request.TextureManifest.Slots.OrderBy(slot => slot.RelativePath.Value, StringComparer.Ordinal).Select(slot =>
            new TextureSlotDto(slot.Id.Value, slot.RelativePath.Value, slot.DisplayName, slot.Category.Value,
                slot.Description, slot.Tags, slot.PreviewEnabled, slot.Editable,
                slot.RecommendedEditMode.Value)).ToImmutableArray(),
        request.DdsRecords.OrderBy(record => record.RelativePath.Value, StringComparer.Ordinal).Select(record =>
            new DdsRecordDto(record.RelativePath.Value, record.ContentSha256.Value, record.Width, record.Height,
                record.Format.ToString(), record.MipLevels, record.HasAlphaChannel,
                record.HeaderType.ToString())).ToImmutableArray());

    private static TemplateAdminPublishResult Conflict() => new(false, "TEMPLATE_ADMIN_VERSION_CONFLICT",
        null, null, [], null, true);
    private static TemplateAdminPublishResult Failed(string code) => new(false, code, null, null, [], null);

    private sealed record MetadataDto(UnsignedMetadataDto Metadata, string MetadataSignature);
    private sealed record UnsignedMetadataDto(int SchemaVersion, string TemplateId, string TemplateVersion,
        string Sha256, string CompatibleGameBuild, string GameId, string ModId, long ContentLength,
        string MediaType, string Encryption, string SignatureScope, string AuditSha256,
        ImmutableArray<TextureSlotDto> TextureSlots, ImmutableArray<DdsRecordDto> DdsRecords);
    private sealed record TextureSlotDto(string SlotId, string RelativePath, string DisplayName, string Category,
        string Description, ImmutableArray<string> Tags, bool PreviewEnabled, bool Editable,
        string RecommendedEditMode);
    private sealed record DdsRecordDto(string RelativePath, string ContentSha256, int Width, int Height,
        string Format, uint MipLevels, bool HasAlphaChannel, string HeaderType);
    private sealed record AuditDto(int SchemaVersion, Guid OperationId, Guid AdminSubjectId,
        string TemplateId, string TemplateVersion, string GameId, string ModId, int DdsCount,
        DateTimeOffset OccurredAt, string Action);
}

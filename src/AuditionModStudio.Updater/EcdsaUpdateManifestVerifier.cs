using System.Security.Cryptography;

namespace AuditionModStudio.Updater;

public sealed class EcdsaUpdateManifestVerifier : IAppUpdateManifestVerifier, IDisposable
{
    private static ReadOnlySpan<byte> Domain => "AUDITION_APP_UPDATE_MANIFEST_V1\0"u8;
    private readonly ECDsa _publicKey;

    public EcdsaUpdateManifestVerifier(string publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem) || publicKeyPem.Length > 16_384)
            throw new ArgumentException("UPDATE_MANIFEST_PUBLIC_KEY_INVALID", nameof(publicKeyPem));
        _publicKey = ECDsa.Create();
        try
        {
            _publicKey.ImportFromPem(publicKeyPem);
            if (_publicKey.KeySize != 256)
                throw new CryptographicException("UPDATE_MANIFEST_PUBLIC_KEY_NOT_P256");
        }
        catch
        {
            _publicKey.Dispose();
            throw;
        }
    }

    public bool Verify(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        if (payload.IsEmpty || payload.Length > UpdateManifestCodec.MaximumPayloadBytes || signature.Length != 64)
            return false;
        var signedBytes = new byte[Domain.Length + payload.Length];
        try
        {
            Domain.CopyTo(signedBytes);
            payload.CopyTo(signedBytes.AsSpan(Domain.Length));
            return _publicKey.VerifyData(signedBytes, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signedBytes);
        }
    }

    public void Dispose() => _publicKey.Dispose();
}

public sealed class AnyTrustedUpdateManifestVerifier : IAppUpdateManifestVerifier
{
    private readonly IAppUpdateManifestVerifier[] _verifiers;

    public AnyTrustedUpdateManifestVerifier(IEnumerable<IAppUpdateManifestVerifier> verifiers)
    {
        ArgumentNullException.ThrowIfNull(verifiers);
        _verifiers = verifiers.ToArray();
        if (_verifiers.Length is < 1 or > 3 || _verifiers.Any(static verifier => verifier is null))
            throw new ArgumentException("UPDATE_MANIFEST_VERIFIER_SET_INVALID", nameof(verifiers));
    }

    public bool Verify(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        var accepted = false;
        foreach (var verifier in _verifiers)
            accepted |= verifier.Verify(payload, signature);
        return accepted;
    }
}

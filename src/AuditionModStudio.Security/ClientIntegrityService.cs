using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AuditionModStudio.Security;

public sealed partial class ClientIntegrityService(
    ClientIntegrityOptions options,
    IClientExecutableTrustVerifier executableTrustVerifier) : IClientIntegrityService
{
    private const long MaximumManifestBytes = 256 * 1024;
    private const int MaximumArtifactCount = 512;

    public async Task<ClientIntegrityResult> VerifyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryValidatePolicy(out var root, out var executablePath, out var manifestPath))
            return Failure(ClientIntegrityFailureReason.InvalidPolicy, "CLIENT_INTEGRITY_POLICY_INVALID");

        try
        {
            if (HasReparsePoint(root!, executablePath!))
                return Failure(ClientIntegrityFailureReason.ArtifactPathRejected, "CLIENT_INTEGRITY_PATH_REJECTED");

            if (options.RequireExecutableSignature)
            {
                var trust = await executableTrustVerifier.VerifyAsync(
                    executablePath!, options.ExpectedPublisherSubject, options.ExpectedPublisherThumbprint,
                    cancellationToken).ConfigureAwait(false);
                if (!trust.Succeeded)
                    return Failure(ClientIntegrityFailureReason.ExecutableSignatureRejected,
                        "CLIENT_EXECUTABLE_SIGNATURE_REJECTED");
            }

            if (!File.Exists(manifestPath))
                return Failure(ClientIntegrityFailureReason.ManifestMissing, "CLIENT_INTEGRITY_MANIFEST_MISSING");
            if (HasReparsePoint(root!, manifestPath!))
                return Failure(ClientIntegrityFailureReason.ArtifactPathRejected, "CLIENT_INTEGRITY_PATH_REJECTED");

            var manifestInfo = new FileInfo(manifestPath!);
            if (manifestInfo.Length is <= 0 or > MaximumManifestBytes)
                return Failure(ClientIntegrityFailureReason.ManifestInvalid, "CLIENT_INTEGRITY_MANIFEST_INVALID");

            var manifestBytes = await File.ReadAllBytesAsync(manifestPath!, cancellationToken).ConfigureAwait(false);
            var actualManifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes));
            if (!FixedHashEquals(options.ExpectedManifestSha256, actualManifestHash))
                return Failure(ClientIntegrityFailureReason.ManifestHashMismatch, "CLIENT_INTEGRITY_MANIFEST_HASH_MISMATCH");

            if (!TryParseManifest(manifestBytes, out var entries))
                return Failure(ClientIntegrityFailureReason.ManifestInvalid, "CLIENT_INTEGRITY_MANIFEST_INVALID");

            foreach (var entry in entries!)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryResolveRelative(root!, entry.RelativePath, out var artifactPath))
                    return Failure(ClientIntegrityFailureReason.ArtifactPathRejected, "CLIENT_INTEGRITY_PATH_REJECTED");
                if (!File.Exists(artifactPath))
                    return Failure(ClientIntegrityFailureReason.ArtifactMissing, ArtifactDiagnostic(entry.Kind, "MISSING"));
                if (HasReparsePoint(root!, artifactPath!))
                    return Failure(ClientIntegrityFailureReason.ArtifactPathRejected, "CLIENT_INTEGRITY_PATH_REJECTED");

                var info = new FileInfo(artifactPath!);
                if (info.Length != entry.ContentLength)
                    return Failure(ClientIntegrityFailureReason.ArtifactLengthMismatch,
                        ArtifactDiagnostic(entry.Kind, "LENGTH_MISMATCH"));

                await using var stream = new FileStream(artifactPath!, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
                    .ConfigureAwait(false));
                if (!FixedHashEquals(entry.Sha256, actualHash))
                    return Failure(ClientIntegrityFailureReason.ArtifactHashMismatch,
                        ArtifactDiagnostic(entry.Kind, "HASH_MISMATCH"));
            }

            return ClientIntegrityResult.Success(options.RequireExecutableSignature);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or CryptographicException)
        {
            return Failure(ClientIntegrityFailureReason.UnexpectedFailure, "CLIENT_INTEGRITY_CHECK_FAILED");
        }
    }

    private bool TryValidatePolicy(out string? root, out string? executablePath, out string? manifestPath)
    {
        root = executablePath = manifestPath = null;
        try
        {
            if (!Path.IsPathFullyQualified(options.ApplicationRoot)
                || !ValidSha256().IsMatch(options.ExpectedManifestSha256)
                || (options.RequireExecutableSignature
                    && (string.IsNullOrWhiteSpace(options.ExpectedPublisherSubject)
                        || !ValidThumbprint().IsMatch(NormalizeThumbprint(options.ExpectedPublisherThumbprint)))))
                return false;

            root = Path.GetFullPath(options.ApplicationRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return TryResolveRelative(root, options.ExecutableRelativePath, out executablePath)
                && TryResolveRelative(root, options.ManifestRelativePath, out manifestPath)
                && File.Exists(executablePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryParseManifest(ReadOnlyMemory<byte> bytes, out IReadOnlyList<IntegrityEntry>? entries)
    {
        entries = null;
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
            || !root.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var schemaVersion)
            || schemaVersion != 1
            || !root.TryGetProperty("artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Array
            || artifacts.GetArrayLength() is <= 0 or > MaximumArtifactCount)
            return false;

        var parsed = new List<IntegrityEntry>(artifacts.GetArrayLength());
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in artifacts.EnumerateArray())
        {
            if (artifact.ValueKind != JsonValueKind.Object || artifact.EnumerateObject().Count() != 4
                || !artifact.TryGetProperty("relativePath", out var relativePath)
                || !artifact.TryGetProperty("kind", out var kind)
                || !artifact.TryGetProperty("contentLength", out var length)
                || !artifact.TryGetProperty("sha256", out var hash)
                || relativePath.ValueKind != JsonValueKind.String || kind.ValueKind != JsonValueKind.String
                || length.ValueKind != JsonValueKind.Number || hash.ValueKind != JsonValueKind.String
                || !length.TryGetInt64(out var contentLength) || contentLength < 0)
                return false;
            var pathValue = relativePath.GetString()!;
            var kindValue = kind.GetString()!;
            var hashValue = hash.GetString()!;
            if (!paths.Add(pathValue) || !ValidSha256().IsMatch(hashValue)
                || kindValue is not ("resource_bundle" or "companion_tool"))
                return false;
            parsed.Add(new(pathValue, kindValue, contentLength, hashValue));
        }
        entries = parsed;
        return true;
    }

    private static bool TryResolveRelative(string root, string relativePath, out string? resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)
            || relativePath.Contains(':', StringComparison.Ordinal))
            return false;
        var normalized = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (normalized.Split(Path.DirectorySeparatorChar).Any(segment => segment is "" or "." or ".."))
            return false;
        var candidate = Path.GetFullPath(Path.Combine(root, normalized));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return false;
        resolved = candidate;
        return true;
    }

    private static bool HasReparsePoint(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
        }
        return false;
    }

    private static bool FixedHashEquals(string expected, string actual)
    {
        if (!ValidSha256().IsMatch(expected) || !ValidSha256().IsMatch(actual)) return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual));
    }

    private static string ArtifactDiagnostic(string kind, string suffix) =>
        kind == "companion_tool" ? $"CLIENT_COMPANION_{suffix}" : $"CLIENT_RESOURCE_{suffix}";
    private static ClientIntegrityResult Failure(ClientIntegrityFailureReason reason, string code) =>
        ClientIntegrityResult.Failure(reason, code);
    private static string NormalizeThumbprint(string value) => string.Concat(value.Where(Uri.IsHexDigit)).ToUpperInvariant();

    private sealed record IntegrityEntry(string RelativePath, string Kind, long ContentLength, string Sha256);

    [GeneratedRegex("\\A[0-9A-Fa-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ValidSha256();
    [GeneratedRegex("\\A[0-9A-F]{40}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ValidThumbprint();
}

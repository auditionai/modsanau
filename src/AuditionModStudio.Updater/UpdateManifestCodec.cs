using System.Text.Json;
using System.Text.RegularExpressions;

namespace AuditionModStudio.Updater;

public static partial class UpdateManifestCodec
{
    public const int MaximumEnvelopeBytes = 96 * 1024;
    public const int MaximumPayloadBytes = 64 * 1024;
    public const long MaximumArtifactBytes = 2L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions StrictJson = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static bool TryReadEnvelope(ReadOnlySpan<byte> json, out SignedUpdateManifestEnvelope? envelope)
    {
        envelope = null;
        if (json.IsEmpty || json.Length > MaximumEnvelopeBytes) return false;
        try
        {
            var dto = JsonSerializer.Deserialize<EnvelopeDto>(json, StrictJson);
            if (dto is not { SchemaVersion: 1 } || string.IsNullOrWhiteSpace(dto.Payload)
                || string.IsNullOrWhiteSpace(dto.Signature)) return false;
            var payload = Convert.FromBase64String(dto.Payload);
            var signature = Convert.FromBase64String(dto.Signature);
            if (payload.Length is 0 or > MaximumPayloadBytes || signature.Length != 64) return false;
            envelope = new(payload, signature);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or NotSupportedException)
        {
            return false;
        }
    }

    public static bool TryReadVerifiedPayload(ReadOnlySpan<byte> json, out AppUpdateManifest? manifest)
    {
        manifest = null;
        try
        {
            var dto = JsonSerializer.Deserialize<ManifestDto>(json, StrictJson);
            if (dto is not { SchemaVersion: 2, Channel: "Stable" }
                || string.IsNullOrWhiteSpace(dto.ProductId) || dto.ProductId.Length > 64
                || dto.RolloutBasisPoints is < 0 or > 10_000
                || !TryVersion(dto.Version, out var version)
                || !ValidFileName(dto.ArtifactFileName)
                || !Uri.TryCreate(dto.ArtifactUri, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Fragment)
                || dto.ContentLength is <= 0 or > MaximumArtifactBytes
                || !Sha256Regex().IsMatch(dto.Sha256 ?? string.Empty)
                || string.IsNullOrWhiteSpace(dto.PublisherSubject) || dto.PublisherSubject.Length > 512
                || !ThumbprintRegex().IsMatch(dto.PublisherThumbprint ?? string.Empty)) return false;
            manifest = new(dto.ProductId!, AppUpdateChannel.Stable, dto.RolloutBasisPoints,
                version!, dto.ArtifactFileName!, uri, dto.ContentLength,
                dto.Sha256!.ToUpperInvariant(), dto.PublisherSubject!,
                dto.PublisherThumbprint!.ToUpperInvariant());
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryVersion(string? value, out Version? version)
    {
        version = null;
        if (value is null || !VersionRegex().IsMatch(value) || !Version.TryParse(value, out var parsed)) return false;
        if (parsed.Major > ushort.MaxValue || parsed.Minor > ushort.MaxValue
            || parsed.Build > ushort.MaxValue || parsed.Revision > ushort.MaxValue) return false;
        version = parsed;
        return true;
    }

    private static bool ValidFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value != Path.GetFileName(value)
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        return Path.GetExtension(value).Equals(".msix", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsValidThumbprint(string? value) =>
        ThumbprintRegex().IsMatch(value ?? string.Empty);

    [GeneratedRegex("^[0-9]{1,5}\\.[0-9]{1,5}\\.[0-9]{1,5}\\.[0-9]{1,5}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();
    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
    [GeneratedRegex("^[A-Fa-f0-9]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex ThumbprintRegex();

    private sealed record EnvelopeDto(int SchemaVersion, string? Payload, string? Signature);
    private sealed record ManifestDto(int SchemaVersion, string? ProductId, string? Channel,
        int RolloutBasisPoints, string? Version, string? ArtifactFileName, string? ArtifactUri,
        long ContentLength, string? Sha256, string? PublisherSubject, string? PublisherThumbprint);
}

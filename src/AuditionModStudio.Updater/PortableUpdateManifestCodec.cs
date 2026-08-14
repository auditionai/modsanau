using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AuditionModStudio.Updater;

public static partial class PortableUpdateManifestCodec
{
    public const int SchemaVersion = 3;
    public const int MaximumReleaseNotes = 12;
    public const int MaximumInventoryEntries = 4096;
    public const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumExpandedBytes = 4L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions StrictJson = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static bool TryReadVerifiedPayload(ReadOnlySpan<byte> json, out PortableUpdateManifest? manifest)
    {
        manifest = null;
        try
        {
            var dto = JsonSerializer.Deserialize<ManifestDto>(json, StrictJson);
            if (dto is not { SchemaVersion: SchemaVersion, Channel: "stable" }
                || dto.Product != PortableUpdateProduct.Identity
                || !TryVersion(dto.Version, out var version)
                || !TryVersion(dto.MinimumSupportedVersion, out var minimum)
                || minimum!.CompareTo(version) > 0
                || !DateTimeOffset.TryParse(dto.PublishedAt, out var publishedAt)
                || !Enum.TryParse<PortableUpdatePolicy>(dto.UpdatePolicy, true, out var updatePolicy)
                || dto.Package is null
                || !TryPackage(dto.Package, out var package)
                || dto.ReleaseNotes is null
                || dto.ReleaseNotes.Length > MaximumReleaseNotes
                || dto.ReleaseNotes.Any(static note => string.IsNullOrWhiteSpace(note) || note.Length > 240))
                return false;

            manifest = new(
                dto.Product,
                AppUpdateChannel.Stable,
                version!,
                minimum!,
                publishedAt.ToUniversalTime(),
                updatePolicy,
                package!,
                dto.ReleaseNotes.ToImmutableArray());
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    private static bool TryPackage(PackageDto dto, out PortableUpdatePackage? package)
    {
        package = null;
        if (!Uri.TryCreate(dto.Url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !ValidPackageName(dto.FileName)
            || dto.Size is <= 0 or > MaximumPackageBytes
            || !Sha256Regex().IsMatch(dto.Sha256 ?? string.Empty)
            || dto.Product != PortableUpdateProduct.Identity
            || dto.Architecture != PortableUpdateProduct.Architecture
            || dto.Distribution != PortableUpdateProduct.Distribution
            || dto.Inventory is null
            || dto.Inventory.Length is < 2 or > MaximumInventoryEntries
            || dto.RemoveOwnedFiles is null
            || dto.RemoveOwnedFiles.Length > MaximumInventoryEntries)
            return false;

        var inventory = ImmutableArray.CreateBuilder<PortablePackageFile>(dto.Inventory.Length);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var file in dto.Inventory)
        {
            if (!ValidRelativePath(file.Path) || file.Length < 0
                || !Sha256Regex().IsMatch(file.Sha256 ?? string.Empty)
                || !paths.Add(file.Path!))
                return false;
            expanded = checked(expanded + file.Length);
            if (expanded > MaximumExpandedBytes) return false;
            inventory.Add(new(file.Path!.Replace('\\', '/'), file.Length, file.Sha256!.ToUpperInvariant()));
        }

        if (!paths.Contains(PortableUpdateProduct.PrimaryExecutable)
            || !paths.Contains(PortableUpdateProduct.UpdaterExecutable))
            return false;

        var removals = ImmutableArray.CreateBuilder<string>(dto.RemoveOwnedFiles.Length);
        var removalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in dto.RemoveOwnedFiles)
        {
            if (!ValidRelativePath(path) || paths.Contains(path!) || !removalPaths.Add(path!)) return false;
            removals.Add(path!.Replace('\\', '/'));
        }

        package = new(uri, dto.FileName!, dto.Size, dto.Sha256!.ToUpperInvariant(), dto.Product!,
            dto.Architecture!, dto.Distribution!, inventory.MoveToImmutable(), removals.MoveToImmutable());
        return true;
    }

    public static bool ValidRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 260 || Path.IsPathFullyQualified(value)
            || value.StartsWith("\\\\", StringComparison.Ordinal) || value.Contains(':')) return false;
        var normalized = value.Replace('\\', '/');
        return !normalized.StartsWith("/", StringComparison.Ordinal)
               && normalized.Split('/').All(static segment =>
                   segment.Length > 0 && segment is not "." and not ".."
                   && segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
    }

    private static bool ValidPackageName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 160
        && value == Path.GetFileName(value)
        && value.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)
        && value.StartsWith("AuditionAI-Mod-Studio-", StringComparison.Ordinal);

    private static bool TryVersion(string? value, out Version? version)
    {
        version = null;
        if (value is null || !VersionRegex().IsMatch(value) || !Version.TryParse(value, out var parsed)) return false;
        version = parsed;
        return true;
    }

    [GeneratedRegex("^[0-9]{1,5}\\.[0-9]{1,5}\\.[0-9]{1,5}(?:\\.[0-9]{1,5})?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    private sealed record ManifestDto(
        int SchemaVersion,
        string? Product,
        string? Channel,
        string? Version,
        string? MinimumSupportedVersion,
        string? PublishedAt,
        string? UpdatePolicy,
        PackageDto? Package,
        string[]? ReleaseNotes);

    private sealed record PackageDto(
        string? Url,
        string? FileName,
        long Size,
        string? Sha256,
        string? Product,
        string? Architecture,
        string? Distribution,
        PackageFileDto[]? Inventory,
        string[]? RemoveOwnedFiles);

    private sealed record PackageFileDto(string? Path, long Length, string? Sha256);
}

using System.Net;

namespace AuditionModStudio.Updater;

public static class UpdateUriPolicy
{
    public static bool IsAllowedArtifactUri(Uri uri, AppUpdatePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(policy);
        return uri.IsAbsoluteUri
               && uri.Scheme == Uri.UriSchemeHttps
               && uri.Port == 443
               && string.IsNullOrEmpty(uri.UserInfo)
               && string.IsNullOrEmpty(uri.Fragment)
               && IsSafeHost(uri.IdnHost)
               && policy.AllowedArtifactHosts.Contains(uri.IdnHost.ToLowerInvariant());
    }

    internal static bool IsSafeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253) return false;
        var normalized = host.TrimEnd('.').ToLowerInvariant();
        if (normalized is "localhost" or "localhost.localdomain" || normalized.EndsWith(".local", StringComparison.Ordinal))
            return false;
        return !IPAddress.TryParse(normalized, out _)
               && Uri.CheckHostName(normalized) == UriHostNameType.Dns;
    }
}

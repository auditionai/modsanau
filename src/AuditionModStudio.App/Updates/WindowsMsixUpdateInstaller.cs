using System.Security.Cryptography;
using AuditionModStudio.Updater;
using Windows.Management.Deployment;

namespace AuditionModStudio.App.Updates;

public sealed class WindowsMsixUpdateInstaller : IVerifiedAppUpdateInstaller
{
    public async Task<bool> InstallAsync(
        VerifiedUpdateCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(candidate.StagedArtifactPath)
            || !Path.GetExtension(candidate.StagedArtifactPath).Equals(".msix", StringComparison.OrdinalIgnoreCase))
            return false;

        var file = new FileInfo(candidate.StagedArtifactPath);
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0
            || file.Length != candidate.Manifest.ContentLength)
            return false;
        await using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                         64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!string.Equals(hash, candidate.VerifiedSha256, StringComparison.Ordinal)) return false;
        }

        var packageManager = new PackageManager();
        var operation = packageManager.AddPackageAsync(new Uri(file.FullName), null, DeploymentOptions.None);
        using var registration = cancellationToken.Register(operation.Cancel);
        var result = await operation;
        if (result.ExtendedErrorCode is { } error && error != default) return false;

        return packageManager.FindPackagesForUser(string.Empty)
            .Any(package => string.Equals(package.Id.Name, candidate.Manifest.ProductId, StringComparison.Ordinal)
                            && string.Equals(package.Id.Publisher, candidate.Manifest.PublisherSubject,
                                StringComparison.Ordinal)
                            && ToVersion(package.Id.Version).CompareTo(candidate.Manifest.Version) == 0);
    }

    private static Version ToVersion(Windows.ApplicationModel.PackageVersion version) =>
        new(version.Major, version.Minor, version.Build, version.Revision);
}

using System.IO.Compression;
using System.Xml;

namespace AuditionModStudio.Updater;

public sealed class MsixPackageIdentityVerifier : IAppUpdatePackageVerifier
{
    private const int MaximumManifestBytes = 256 * 1024;

    public Task<PackageIdentityVerificationResult> VerifyAsync(
        string artifactPath,
        AppUpdateManifest manifest,
        AppUpdatePolicy policy,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(artifactPath)
            || !Path.GetExtension(artifactPath).Equals(".msix", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(PackageIdentityVerificationResult.Failure("MSIX_PATH_INVALID"));

        try
        {
            var file = new FileInfo(artifactPath);
            if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0
                || file.Length != manifest.ContentLength)
                return Task.FromResult(PackageIdentityVerificationResult.Failure("MSIX_FILE_INVALID"));

            using var archive = ZipFile.OpenRead(file.FullName);
            var entries = archive.Entries.Where(static entry =>
                string.Equals(entry.FullName, "AppxManifest.xml", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (entries.Length != 1 || entries[0].Length is <= 0 or > MaximumManifestBytes)
                return Task.FromResult(PackageIdentityVerificationResult.Failure("MSIX_MANIFEST_INVALID"));

            using var stream = entries[0].Open();
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumManifestBytes
            };
            using var reader = XmlReader.Create(stream, settings);
            var document = new XmlDocument { XmlResolver = null };
            document.Load(reader);
            var identities = document.SelectNodes("/*[local-name()='Package']/*[local-name()='Identity']");
            var applications = document.SelectNodes(
                "/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application']");
            if (identities is not { Count: 1 } || identities[0] is not XmlElement identity
                || applications is not { Count: 1 } || applications[0] is not XmlElement application)
                return Task.FromResult(PackageIdentityVerificationResult.Failure("MSIX_MANIFEST_INVALID"));

            if (!string.Equals(identity.GetAttribute("Name"), policy.ExpectedPackageName, StringComparison.Ordinal)
                || !string.Equals(application.GetAttribute("Id"), policy.ExpectedApplicationId, StringComparison.Ordinal)
                || !string.Equals(application.GetAttribute("Executable"), policy.ExpectedExecutable, StringComparison.Ordinal)
                || !string.Equals(application.GetAttribute("EntryPoint"), policy.ExpectedEntryPoint, StringComparison.Ordinal))
                return Task.FromResult(PackageIdentityVerificationResult.Failure("MSIX_PRODUCT_IDENTITY_MISMATCH"));
            if (!string.Equals(identity.GetAttribute("Publisher"), policy.ExpectedPublisherSubject, StringComparison.Ordinal)
                || !string.Equals(identity.GetAttribute("Publisher"), manifest.PublisherSubject, StringComparison.Ordinal))
                return Task.FromResult(PackageIdentityVerificationResult.Failure("MSIX_PUBLISHER_MISMATCH"));
            if (!string.Equals(identity.GetAttribute("ProcessorArchitecture"), policy.ExpectedArchitecture,
                    StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(PackageIdentityVerificationResult.Failure("MSIX_ARCHITECTURE_MISMATCH"));
            if (!Version.TryParse(identity.GetAttribute("Version"), out var packageVersion)
                || packageVersion.CompareTo(manifest.Version) != 0)
                return Task.FromResult(PackageIdentityVerificationResult.Failure("MSIX_VERSION_MISMATCH"));

            return Task.FromResult(PackageIdentityVerificationResult.Success());
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                                   or XmlException or NotSupportedException)
        {
            return Task.FromResult(PackageIdentityVerificationResult.Failure("MSIX_PACKAGE_INVALID"));
        }
    }
}

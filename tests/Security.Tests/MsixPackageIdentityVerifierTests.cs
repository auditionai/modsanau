using System.IO.Compression;
using System.Text;
using AuditionModStudio.Updater;

namespace Security.Tests;

public sealed class MsixPackageIdentityVerifierTests : IDisposable
{
    private const string Publisher = "CN=Audition AI Mod Studio Test";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"audition-msix-identity-{Guid.NewGuid():N}");

    [Fact]
    public async Task Plan95_identity_version_architecture_and_application_are_accepted()
    {
        var path = CreatePackage(AppUpdateProduct.Identity, Publisher, "2.0.0.0", "x64", "App");

        var result = await VerifyAsync(path, new Version(2, 0, 0, 0));

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("DifferentProduct", "CN=Audition AI Mod Studio Test", "2.0.0.0", "x64", "App",
        "MSIX_PRODUCT_IDENTITY_MISMATCH")]
    [InlineData("AuditionAIModStudio", "CN=Different Publisher", "2.0.0.0", "x64", "App",
        "MSIX_PUBLISHER_MISMATCH")]
    [InlineData("AuditionAIModStudio", "CN=Audition AI Mod Studio Test", "2.0.0.0", "arm64", "App",
        "MSIX_ARCHITECTURE_MISMATCH")]
    [InlineData("AuditionAIModStudio", "CN=Audition AI Mod Studio Test", "3.0.0.0", "x64", "App",
        "MSIX_VERSION_MISMATCH")]
    [InlineData("AuditionAIModStudio", "CN=Audition AI Mod Studio Test", "2.0.0.0", "x64", "Other",
        "MSIX_PRODUCT_IDENTITY_MISMATCH")]
    public async Task Mismatched_package_metadata_is_rejected(string name, string publisher, string version,
        string architecture, string applicationId, string diagnostic)
    {
        var path = CreatePackage(name, publisher, version, architecture, applicationId);

        var result = await VerifyAsync(path, new Version(2, 0, 0, 0));

        Assert.False(result.Succeeded);
        Assert.Equal(diagnostic, result.DiagnosticCode);
    }

    [Fact]
    public async Task Tampered_non_msix_payload_is_rejected()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "tampered.msix");
        await File.WriteAllBytesAsync(path, "not-a-package"u8.ToArray());

        var result = await VerifyAsync(path, new Version(2, 0, 0, 0));

        Assert.False(result.Succeeded);
        Assert.Equal("MSIX_PACKAGE_INVALID", result.DiagnosticCode);
    }

    private async Task<PackageIdentityVerificationResult> VerifyAsync(string path, Version version)
    {
        var file = new FileInfo(path);
        var manifest = new AppUpdateManifest(AppUpdateProduct.Identity, AppUpdateChannel.Stable, 10_000,
            version, "release.msix", new Uri("https://release.example.invalid/release.msix"), file.Length,
            new string('A', 64), Publisher, new string('B', 40));
        var policy = AppUpdatePolicy.CreateStable(["release.example.invalid"], Publisher, new string('B', 40));
        return await new MsixPackageIdentityVerifier().VerifyAsync(path, manifest, policy);
    }

    private string CreatePackage(string name, string publisher, string version, string architecture,
        string applicationId)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.msix");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("AppxManifest.xml", CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write($$"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="{{name}}" Publisher="{{publisher}}" Version="{{version}}" ProcessorArchitecture="{{architecture}}" />
              <Applications><Application Id="{{applicationId}}" Executable="AuditionModStudio.App.exe" EntryPoint="Windows.FullTrustApplication" /></Applications>
            </Package>
            """);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

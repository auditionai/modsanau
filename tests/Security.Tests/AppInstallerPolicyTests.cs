using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;

namespace Security.Tests;

public sealed class AppInstallerPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"app-installer-{Guid.NewGuid():N}");

    [Fact]
    public void Manifest_has_stable_product_identity_and_no_mod_shell_association()
    {
        var repository = FindRepositoryRoot();
        var manifest = XDocument.Load(Path.Combine(repository, "src", "AuditionModStudio.App", "Package.appxmanifest"));
        var identity = manifest.Descendants().Single(item => item.Name.LocalName == "Identity");

        Assert.Equal("AuditionAIModStudio", identity.Attribute("Name")?.Value);
        Assert.Equal("CN=Audition AI Mod Studio Development", identity.Attribute("Publisher")?.Value);
        Assert.Equal("Audition AI Mod Studio", manifest.Descendants().Single(item =>
            item.Name.LocalName == "Properties").Elements().Single(item =>
            item.Name.LocalName == "DisplayName").Value);
        Assert.DoesNotContain(manifest.Descendants(), item =>
            item.Attribute("Category")?.Value is "windows.protocol" or "windows.fileTypeAssociation");
    }

    [Fact]
    public void Packaging_is_single_project_msix_and_runtime_remains_as_invoker()
    {
        var repository = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(repository, "src", "AuditionModStudio.App",
            "AuditionModStudio.App.csproj"));
        var runtimeManifest = XDocument.Load(Path.Combine(repository, "src", "AuditionModStudio.App", "app.manifest"));
        var execution = runtimeManifest.Descendants().Single(item => item.Name.LocalName == "requestedExecutionLevel");

        Assert.Contains("<EnableMsixTooling>true</EnableMsixTooling>", project, StringComparison.Ordinal);
        Assert.Contains("PrepareInstallerPackageManifest", project, StringComparison.Ordinal);
        Assert.Contains("BeforeTargets=\"_GenerateAppxPackageFile\"", project, StringComparison.Ordinal);
        Assert.DoesNotContain("requireAdministrator", project, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("asInvoker", execution.Attribute("level")?.Value);
        Assert.Equal("false", execution.Attribute("uiAccess")?.Value);
    }

    [Fact]
    public void Production_workflow_is_fail_closed_and_publishes_only_verified_installer_bytes()
    {
        var workflow = File.ReadAllText(Path.Combine(FindRepositoryRoot(), ".github", "workflows",
            "release-signing.yml"));

        Assert.Contains("New-AppInstallerPackage.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("-Mode Production", workflow, StringComparison.Ordinal);
        Assert.Contains("AUDITION_SIGNING_PUBLISHER_DISPLAY_NAME", workflow, StringComparison.Ordinal);
        Assert.Contains("Test-AppInstallerPackage.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("-RequireSignature", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("AppxPackageSigningEnabled=false", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".pfx", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".p12", workflow, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Installer_scripts_have_no_custom_action_game_or_forced_downgrade_path()
    {
        var repository = FindRepositoryRoot();
        var build = File.ReadAllText(Path.Combine(repository, "scripts", "New-AppInstallerPackage.ps1"));
        var scan = File.ReadAllText(Path.Combine(repository, "scripts", "Test-AppInstallerPackage.ps1"));

        Assert.DoesNotContain("ForceUpdateFromAnyVersion", build, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("runas", build, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("game directory", build, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Add-AppxPackage", build, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'acv.exe', 'texconv.exe'", scan, StringComparison.Ordinal);
        Assert.Contains("'.ab', '.acv'", scan, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.0.0.65536")]
    [InlineData("latest")]
    public async Task Package_builder_rejects_invalid_version_before_build(string version)
    {
        Directory.CreateDirectory(_root);
        var result = await RunPowerShellAsync(
            $"& './scripts/New-AppInstallerPackage.ps1' -Version '{version}' -ArtifactRoot '{Escape(_root)}'");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("INSTALLER_VERSION_INVALID", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Production_builder_rejects_development_publisher()
    {
        Directory.CreateDirectory(_root);
        var result = await RunPowerShellAsync(
            $"& './scripts/New-AppInstallerPackage.ps1' -Version '1.0.0.95' -ArtifactRoot '{Escape(_root)}' -Mode Production");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("PRODUCTION_INSTALLER_PUBLISHER_REQUIRED", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Production_builder_rejects_missing_signing_tool_without_unsigned_fallback()
    {
        Directory.CreateDirectory(_root);
        var result = await RunPowerShellAsync(
            $"& './scripts/New-AppInstallerPackage.ps1' -Version '1.0.0.95' -ArtifactRoot '{Escape(_root)}' " +
            "-Mode Production -PublisherSubject 'CN=Audition AI Mod Studio Production Test' " +
            "-PublisherDisplayName 'Audition AI Mod Studio Production Test' " +
            "-SignToolPath 'C:\\missing\\signtool.exe'");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("SIGNTOOL_ABSOLUTE_PATH_REQUIRED", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scanner_accepts_exact_unsigned_development_package()
    {
        Directory.CreateDirectory(_root);
        var package = CreatePackage();
        var result = await ScanAsync(package);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("\"PolicyStatus\":\"PASS\"", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"SigningStatus\":\"UnsignedDevelopment\"", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("acv.exe")]
    [InlineData("015.ab")]
    [InlineData("private.pfx")]
    [InlineData("debug.pdb")]
    [InlineData("project.audproj")]
    public async Task Scanner_rejects_proprietary_private_or_user_artifact(string entry)
    {
        Directory.CreateDirectory(_root);
        var package = CreatePackage(entry);
        var result = await ScanAsync(package);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("INSTALLER_FORBIDDEN_ARTIFACT_EXPOSED", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Production_policy_rejects_unsigned_package()
    {
        Directory.CreateDirectory(_root);
        var package = CreatePackage();
        var command = $"& './scripts/Test-AppInstallerPackage.ps1' -PackagePath '{Escape(package)}' " +
            "-ExpectedVersion '1.0.0.95' -ExpectedPublisherSubject 'CN=Audition AI Mod Studio Development' " +
            $"-ExpectedPublisherThumbprint '{new string('A', 40)}' -RequireSignature";
        var result = await RunPowerShellAsync(command);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("INSTALLER_SIGNATURE_POLICY_FAILED", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_does_not_own_local_projects_credentials_or_recovery_cleanup()
    {
        var repository = FindRepositoryRoot();
        var build = File.ReadAllText(Path.Combine(repository, "scripts", "New-AppInstallerPackage.ps1"));
        var payload = File.ReadAllText(Path.Combine(repository, "scripts", "Invoke-AppInstallerPayloadSigning.ps1"));

        Assert.DoesNotContain("Credential", build, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecureTemplateCache", build, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Workspaces", build, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Projects", build, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remove-Item", payload, StringComparison.OrdinalIgnoreCase);
    }

    private string CreatePackage(string? extraEntry = null)
    {
        var path = Path.Combine(_root, $"package-{Guid.NewGuid():N}.msix");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(archive, "AppxManifest.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
              <Identity Name="AuditionAIModStudio" Publisher="CN=Audition AI Mod Studio Development"
                        Version="1.0.0.95" ProcessorArchitecture="x64" />
              <Properties><DisplayName>Audition AI Mod Studio</DisplayName></Properties>
              <Dependencies>
                <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" />
                <PackageDependency Name="Microsoft.WindowsAppRuntime.2" MinVersion="2.3.1.0"
                  Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US" />
              </Dependencies>
              <Applications><Application Id="App" Executable="AuditionModStudio.App.exe"
                                         EntryPoint="Windows.FullTrustApplication" /></Applications>
            </Package>
            """);
        Add(archive, "AppxBlockMap.xml", "block-map");
        Add(archive, "[Content_Types].xml", "content-types");
        Add(archive, "AuditionModStudio.App.exe", "MZ");
        Add(archive, "AuditionModStudio.App.dll", "application");
        if (extraEntry is not null) Add(archive, extraEntry, "forbidden");
        return path;
    }

    private static void Add(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private Task<(int ExitCode, string Output)> ScanAsync(string package) => RunPowerShellAsync(
        $"& './scripts/Test-AppInstallerPackage.ps1' -PackagePath '{Escape(package)}' " +
        "-ExpectedVersion '1.0.0.95' -ExpectedPublisherSubject 'CN=Audition AI Mod Studio Development'");

    private static async Task<(int ExitCode, string Output)> RunPowerShellAsync(string command)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh",
            WorkingDirectory = FindRepositoryRoot(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", command }
        })!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "AuditionModStudio.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException();
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

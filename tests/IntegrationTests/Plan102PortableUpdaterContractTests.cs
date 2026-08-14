using System.Xml.Linq;

namespace IntegrationTests;

public sealed class Plan102PortableUpdaterContractTests
{
    [Fact]
    public void Portable_updater_is_unelevated_exact_path_process_without_shell()
    {
        var manifest = XDocument.Load(PathOf("src", "AuditionModStudio.PortableUpdater", "app.manifest"));
        XNamespace asm3 = "urn:schemas-microsoft-com:asm.v3";
        var level = manifest.Descendants(asm3 + "requestedExecutionLevel").Single();
        Assert.Equal("asInvoker", level.Attribute("level")?.Value);
        Assert.Equal("false", level.Attribute("uiAccess")?.Value);

        var coordinator = Read("src", "AuditionModStudio.Updater", "ConfiguredPortableUpdateCoordinator.cs");
        var program = Read("src", "AuditionModStudio.PortableUpdater", "Program.cs");
        Assert.Contains("UseShellExecute = false", coordinator, StringComparison.Ordinal);
        Assert.Contains("ArgumentList.Add", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("cmd.exe", coordinator, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell", coordinator, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("runas", coordinator, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("values[\"--restart-exe\"] != PortableUpdateProduct.PrimaryExecutable", program, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_exposes_real_vietnamese_update_states_and_accessible_actions()
    {
        var xaml = Read("src", "AuditionModStudio.App", "Settings", "SettingsPage.xaml");
        var code = Read("src", "AuditionModStudio.App", "Settings", "SettingsPage.xaml.cs");
        foreach (var text in new[]
                 {
                     "Cập nhật", "Phiên bản hiện tại", "Lần kiểm tra gần nhất", "Kiểm tra cập nhật",
                     "Nội dung cập nhật", "Cập nhật ngay", "Để sau", "Hủy tải", "Khởi động lại để cập nhật"
                 })
            Assert.Contains(text, xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Ứng dụng sẽ cập nhật sau khi tác vụ hiện tại hoàn tất.", code, StringComparison.Ordinal);
        Assert.Contains("thư mục ứng dụng không cho phép ghi", code, StringComparison.Ordinal);
        Assert.Contains("Texture đang chỉnh sửa có thay đổi chưa được Áp dụng", code, StringComparison.Ordinal);
        Assert.Contains("PORTABLE_UPDATE_CANCELLED", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_composition_has_no_fake_feed_or_runtime_downloadable_trust_root()
    {
        var bootstrap = Read("src", "AuditionModStudio.App", "Bootstrap", "ApplicationBootstrapper.cs");
        var settings = Read("src", "AuditionModStudio.App", "Settings", "SettingsPage.xaml.cs");
        Assert.Contains("UnavailablePortableUpdateCoordinator", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("AUDITION_UPDATE_PUBLIC_KEY", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("AUDITION_UPDATE_MANIFEST", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", settings, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Portable_release_includes_dedicated_single_file_updater_and_preserves_artifact_policy()
    {
        var script = Read("scripts", "New-PortableRelease.ps1");
        var project = XDocument.Load(PathOf("src", "AuditionModStudio.PortableUpdater",
            "AuditionModStudio.PortableUpdater.csproj"));
        Assert.Contains("AuditionAI.Updater.exe", script, StringComparison.Ordinal);
        Assert.Contains("Protect-ReleaseArtifactExposure.ps1", script, StringComparison.Ordinal);
        Assert.Equal("true", project.Descendants("PublishSingleFile").Single().Value);
        Assert.Equal("false", project.Descendants("PublishTrimmed").Single().Value);
    }

    [Fact]
    public void File_only_product_boundary_remains_unchanged()
    {
        var files = new[]
        {
            Read("src", "AuditionModStudio.Updater", "PortableUpdateStager.cs"),
            Read("src", "AuditionModStudio.Updater", "PortableUpdateInstallEngine.cs"),
            Read("src", "AuditionModStudio.PortableUpdater", "Program.cs")
        };
        foreach (var source in files)
        {
            Assert.DoesNotContain("Audition installation", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("game process", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("RegistryKey", source, StringComparison.Ordinal);
            Assert.DoesNotContain("acv.exe", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("015.ab", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("015.keydat", source, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Read(params string[] segments) => File.ReadAllText(PathOf(segments));

    private static string PathOf(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
            directory = directory.Parent;
        return Path.Combine([directory?.FullName ?? throw new DirectoryNotFoundException(), .. segments]);
    }
}

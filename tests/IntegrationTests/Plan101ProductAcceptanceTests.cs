using System.Xml.Linq;

namespace IntegrationTests;

public sealed class Plan101ProductAcceptanceTests
{
    [Fact]
    public void Roadmap_v4_defines_productization_order_and_stop_gate()
    {
        var roadmap = Read("Audition_AI_Mod_Studio_MASTER_ROADMAP_V4.md");

        Assert.Contains("PLAN 101 — Visual Product Acceptance", roadmap, StringComparison.Ordinal);
        Assert.Contains("Portable ZIP Packaging", roadmap, StringComparison.Ordinal);
        Assert.Contains("PLAN 102 — Portable Automatic Updater", roadmap, StringComparison.Ordinal);
        Assert.True(roadmap.IndexOf("PLAN 101", StringComparison.Ordinal)
                    < roadmap.IndexOf("PLAN 109", StringComparison.Ordinal));
        Assert.Contains("WAITING_PRODUCT_OWNER", roadmap, StringComparison.Ordinal);
        Assert.Contains("không phải kênh phân phối V1 chính", roadmap, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Portable_publish_is_unpacked_and_fully_self_contained_without_aot_or_trimming()
    {
        var project = XDocument.Load(PathOf("src", "AuditionModStudio.App", "AuditionModStudio.App.csproj"));
        var portable = project.Descendants("PropertyGroup")
            .Single(element => ((string?)element.Attribute("Condition"))?.Contains("PortableRelease", StringComparison.Ordinal) == true);

        Assert.Equal("None", portable.Element("WindowsPackageType")?.Value);
        Assert.Null(portable.Element("AssemblyName"));
        Assert.Equal("true", portable.Element("WindowsAppSDKSelfContained")?.Value);
        Assert.Equal("true", portable.Element("SelfContained")?.Value);
        Assert.Equal("false", portable.Element("PublishSingleFile")?.Value);
        Assert.Equal("false", portable.Element("PublishTrimmed")?.Value);
        Assert.Null(portable.Element("PublishAot"));
    }

    [Fact]
    public void Portable_pipeline_has_exact_name_scan_zip_inventory_and_no_helper_bundle()
    {
        var script = Read("scripts", "New-PortableRelease.ps1");

        Assert.Contains("AuditionModStudio.App.exe", script, StringComparison.Ordinal);
        Assert.Contains("-p:Platform=x64", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Copy-Item -LiteralPath $publishedEntryPoint", script, StringComparison.Ordinal);
        Assert.Contains("AuditionAI-Mod-Studio-$Version-$RuntimeIdentifier.zip", script, StringComparison.Ordinal);
        Assert.Contains("Protect-ReleaseArtifactExposure.ps1", script, StringComparison.Ordinal);
        Assert.Contains("Invoke-SecretScan.ps1", script, StringComparison.Ordinal);
        Assert.Contains("fileCount", script, StringComparison.Ordinal);
        Assert.Contains("zipSha256", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-WebRequest", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Start-Process", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bootstrap_does_not_depend_on_process_working_directory()
    {
        var bootstrap = Read("src", "AuditionModStudio.App", "Bootstrap", "ApplicationBootstrapper.cs");

        Assert.Contains("ContentRootPath = AppContext.BaseDirectory", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.CurrentDirectory", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.GetCurrentDirectory", bootstrap, StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_maximizes_in_work_area_and_only_routes_to_real_v1_surfaces()
    {
        var window = Read("src", "AuditionModStudio.App", "MainWindow.xaml.cs");
        var route = Read("src", "AuditionModStudio.App", "Shell", "AppRoute.cs");
        var mainPage = Read("src", "AuditionModStudio.App", "MainPage.xaml");

        Assert.Contains("OverlappedPresenter", window, StringComparison.Ordinal);
        Assert.Contains("presenter.Maximize()", window, StringComparison.Ordinal);
        Assert.DoesNotContain("EnterFullScreen", window, StringComparison.Ordinal);
        Assert.DoesNotContain("ModLibrary", route, StringComparison.Ordinal);
        Assert.DoesNotContain("Batch", route, StringComparison.Ordinal);
        Assert.DoesNotContain("Cloud", route, StringComparison.Ordinal);
        Assert.Contains("SettingsContent", mainPage, StringComparison.Ordinal);
        Assert.Contains("<Border x:Name=\"PlaceholderContent\"", mainPage, StringComparison.Ordinal);
        Assert.DoesNotContain("<StackPanel x:Name=\"PlaceholderContent\"", mainPage, StringComparison.Ordinal);
    }

    [Fact]
    public void Workspace_exposes_project_asset_stage_change_and_build_readiness_context()
    {
        var xaml = Read("src", "AuditionModStudio.App", "Workspace", "ProjectWorkspacePage.xaml");
        var viewModel = Read("src", "AuditionModStudio.App", "Workspace", "ProjectWorkspaceViewModel.cs");

        foreach (var stage in new[] { "Tệp nguồn", "Đã giải nén", "Đã quét", "Đang chỉnh sửa", "Đã áp dụng", "Đã kiểm tra", "Đã Build", "Đã xuất" })
            Assert.Contains($"Text=\"{stage}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ProjectIdentitySummary", viewModel, StringComparison.Ordinal);
        Assert.Contains("SelectedAssetSummary", viewModel, StringComparison.Ordinal);
        Assert.Contains("BuildReadinessSummary", viewModel, StringComparison.Ordinal);
        Assert.Contains("HorizontalScrollBarVisibility=\"Auto\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_surface_is_accessible_adaptive_and_documents_portable_runtime()
    {
        var xaml = Read("src", "AuditionModStudio.App", "Settings", "SettingsPage.xaml");

        Assert.Contains("AutomationProperties.HeadingLevel=\"Level1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AdaptiveTrigger MinWindowWidth=\"820\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Portable ZIP", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("self-contained", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unpackaged", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("asInvoker", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tương phản cao sẽ theo Windows", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1366×768", xaml, StringComparison.Ordinal);
        Assert.DoesNotMatch("#[0-9A-Fa-f]{6,8}", xaml);
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

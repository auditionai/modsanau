using AuditionModStudio.Core.Settings;
using AuditionModStudio.Infrastructure.Settings;

namespace IntegrationTests;

public sealed class SettingsValidationTests
{
    private readonly SettingsValidator _validator = new();

    [Fact]
    public void Baseline_defaults_are_valid()
    {
        var result = _validator.Validate(new ApplicationSettings());

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Invalid_backend_url_is_an_error()
    {
        var settings = new ApplicationSettings
        {
            Backend = new BackendSettings { ApiBaseUrl = "not-a-url" },
        };

        var result = _validator.Validate(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "InvalidAbsoluteUrl");
    }

    [Fact]
    public void Non_loopback_backend_requires_tls()
    {
        var settings = new ApplicationSettings
        {
            Backend = new BackendSettings { ApiBaseUrl = "http://api.example.test" },
        };

        var result = _validator.Validate(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "TlsRequired");
    }

    [Theory]
    [InlineData(63)]
    [InlineData(1025)]
    public void Invalid_thumbnail_size_is_an_error(int thumbnailSize)
    {
        var settings = new ApplicationSettings
        {
            Appearance = new AppearanceSettings { ThumbnailSize = thumbnailSize },
        };

        var result = _validator.Validate(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "OutOfRange");
    }

    [Fact]
    public void Unset_external_paths_are_distinct_from_invalid_configured_paths()
    {
        var unsetResult = _validator.Validate(new ApplicationSettings());
        var invalidResult = _validator.Validate(new ApplicationSettings
        {
            Game = new GameSettings { InstallationDirectory = " " },
        });

        Assert.True(unsetResult.IsValid);
        Assert.False(invalidResult.IsValid);
        Assert.Contains(invalidResult.Issues, issue => issue.Code == "EmptyConfiguredPath");
    }

    [Fact]
    public void Absolute_paths_with_spaces_and_vietnamese_are_valid()
    {
        var root = Path.Combine(Path.GetTempPath(), "Sàn Aubiz 015", "Công cụ");
        var settings = new ApplicationSettings
        {
            Game = new GameSettings { InstallationDirectory = Path.Combine(root, "Audition Việt Nam") },
            Tooling = new ToolingSettings { AcvExecutablePath = Path.Combine(root, "acv tool.exe") },
            Project = new ProjectSettings { DefaultProjectDirectory = Path.Combine(root, "Dự án") },
        };

        var result = _validator.Validate(settings);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Relative_external_path_is_rejected()
    {
        var result = _validator.Validate(new ApplicationSettings
        {
            Project = new ProjectSettings { DefaultProjectDirectory = @"Projects\Output" },
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "PathNotAbsolute");
    }

    [Theory]
    [InlineData("https://user:password@api.example.test")]
    [InlineData("https://token@api.example.test")]
    public void Backend_url_cannot_embed_credentials(string url)
    {
        var result = _validator.Validate(new ApplicationSettings
        {
            Backend = new BackendSettings { ApiBaseUrl = url },
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "EmbeddedCredentialNotAllowed");
    }

    [Fact]
    public void Malformed_external_path_is_rejected()
    {
        var invalidPath = Path.Combine(Path.GetTempPath(), "invalid*directory");

        var result = _validator.Validate(new ApplicationSettings
        {
            Game = new GameSettings { InstallationDirectory = invalidPath },
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "InvalidPath");
    }
}

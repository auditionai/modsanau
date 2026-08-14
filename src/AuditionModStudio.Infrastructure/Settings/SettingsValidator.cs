using AuditionModStudio.Core.Settings;

namespace AuditionModStudio.Infrastructure.Settings;

public sealed class SettingsValidator : ISettingsValidator
{
    public const int MinimumThumbnailSize = 64;
    public const int MaximumThumbnailSize = 1024;

    public SettingsValidationResult Validate(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var issues = new List<SettingsValidationIssue>();

        if (settings.SchemaVersion != ApplicationSettings.CurrentSchemaVersion)
        {
            AddError(
                issues,
                "schemaVersion",
                "UnsupportedSchemaVersion",
                $"Schema version {settings.SchemaVersion} is not supported.");
        }

        if (settings.Game is null)
        {
            AddError(issues, "game", "RequiredSection", "The game section is required.");
        }
        else
        {
            ValidateDirectoryPath(
                settings.Game.InstallationDirectory,
                "game.installationDirectory",
                issues);
        }

        if (settings.Tooling is null)
        {
            AddError(issues, "tooling", "RequiredSection", "The tooling section is required.");
        }
        else
        {
            ValidateExecutablePath(
                settings.Tooling.AcvExecutablePath,
                "tooling.acvExecutablePath",
                issues);
        }

        if (settings.Project is null)
        {
            AddError(issues, "project", "RequiredSection", "The project section is required.");
        }
        else
        {
            ValidateDirectoryPath(
                settings.Project.DefaultProjectDirectory,
                "project.defaultProjectDirectory",
                issues);

            if (!Enum.IsDefined(settings.Project.DefaultInstallBehavior))
            {
                AddError(
                    issues,
                    "project.defaultInstallBehavior",
                    "InvalidEnumValue",
                    "The default install behavior is not supported.");
            }
        }

        if (settings.Appearance is null)
        {
            AddError(issues, "appearance", "RequiredSection", "The appearance section is required.");
        }
        else
        {
            if (!Enum.IsDefined(settings.Appearance.Language))
            {
                AddError(
                    issues,
                    "appearance.language",
                    "InvalidEnumValue",
                    "The application language is not supported.");
            }

            if (!Enum.IsDefined(settings.Appearance.Theme))
            {
                AddError(
                    issues,
                    "appearance.theme",
                    "InvalidEnumValue",
                    "The application theme is not supported.");
            }

            if (settings.Appearance.ThumbnailSize is < MinimumThumbnailSize or > MaximumThumbnailSize)
            {
                AddError(
                    issues,
                    "appearance.thumbnailSize",
                    "OutOfRange",
                    $"Thumbnail size must be between {MinimumThumbnailSize} and {MaximumThumbnailSize}.");
            }
        }

        if (settings.Backend is null)
        {
            AddError(issues, "backend", "RequiredSection", "The backend section is required.");
        }
        else
        {
            ValidateBackendUrl(settings.Backend.ApiBaseUrl, issues);
        }

        return new SettingsValidationResult(issues);
    }

    private static void ValidateDirectoryPath(
        string? configuredPath,
        string propertyPath,
        ICollection<SettingsValidationIssue> issues)
    {
        if (configuredPath is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            AddError(issues, propertyPath, "EmptyConfiguredPath", "The configured path is empty.");
            return;
        }

        try
        {
            if (!Path.IsPathFullyQualified(configuredPath))
            {
                AddError(issues, propertyPath, "PathNotAbsolute", "The configured path must be absolute.");
                return;
            }

            _ = Path.GetFullPath(configuredPath);
            var root = Path.GetPathRoot(configuredPath) ?? string.Empty;
            var remainder = configuredPath[root.Length..];
            if (remainder.IndexOfAny(['\0', ':', '"', '<', '>', '|', '*', '?']) >= 0
                || remainder.Any(static character => character < ' ')
                || remainder.Split(['\\', '/']).Any(
                    static segment => segment.EndsWith(' ') || segment.EndsWith('.')))
            {
                AddError(issues, propertyPath, "InvalidPath", "The configured path syntax is invalid.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            AddError(issues, propertyPath, "InvalidPath", "The configured path syntax is invalid.");
        }
    }

    private static void ValidateExecutablePath(
        string? configuredPath,
        string propertyPath,
        ICollection<SettingsValidationIssue> issues)
    {
        var issueCount = issues.Count;
        ValidateDirectoryPath(configuredPath, propertyPath, issues);
        if (configuredPath is null || issues.Count != issueCount)
        {
            return;
        }

        if (!Path.GetExtension(configuredPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            AddError(
                issues,
                propertyPath,
                "ExecutableRequired",
                "The configured tool path must identify a Windows executable.");
        }
    }

    private static void ValidateBackendUrl(
        string? configuredUrl,
        ICollection<SettingsValidationIssue> issues)
    {
        if (configuredUrl is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(configuredUrl)
            || !Uri.TryCreate(configuredUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            AddError(
                issues,
                "backend.apiBaseUrl",
                "InvalidAbsoluteUrl",
                "The backend API base URL must be an absolute HTTP or HTTPS URL.");
            return;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            AddError(
                issues,
                "backend.apiBaseUrl",
                "TlsRequired",
                "A non-loopback backend API URL must use HTTPS.");
        }
        else if (uri.Scheme == Uri.UriSchemeHttp)
        {
            issues.Add(new SettingsValidationIssue(
                "backend.apiBaseUrl",
                "LoopbackWithoutTls",
                "Loopback HTTP is intended for local development only.",
                SettingsValidationSeverity.Warning));
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            AddError(
                issues,
                "backend.apiBaseUrl",
                "EmbeddedCredentialNotAllowed",
                "The backend API URL must not contain embedded credentials.");
        }
    }

    private static void AddError(
        ICollection<SettingsValidationIssue> issues,
        string path,
        string code,
        string message)
    {
        issues.Add(new SettingsValidationIssue(
            path,
            code,
            message,
            SettingsValidationSeverity.Error));
    }
}

namespace AuditionModStudio.Core.Settings;

public sealed record SettingsValidationIssue(
    string Path,
    string Code,
    string Message,
    SettingsValidationSeverity Severity);

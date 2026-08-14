namespace AuditionModStudio.Core.Settings;

public sealed class SettingsValidationResult
{
    public SettingsValidationResult(IEnumerable<SettingsValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        Issues = Array.AsReadOnly(issues.ToArray());
    }

    public IReadOnlyList<SettingsValidationIssue> Issues { get; }

    public bool IsValid => Issues.All(
        static issue => issue.Severity != SettingsValidationSeverity.Error);
}

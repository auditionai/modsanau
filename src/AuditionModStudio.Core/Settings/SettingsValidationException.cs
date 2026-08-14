namespace AuditionModStudio.Core.Settings;

public sealed class SettingsValidationException : Exception
{
    public SettingsValidationException(SettingsValidationResult validationResult)
        : base("Application settings contain one or more validation errors.")
    {
        ValidationResult = validationResult
            ?? throw new ArgumentNullException(nameof(validationResult));
    }

    public SettingsValidationResult ValidationResult { get; }
}

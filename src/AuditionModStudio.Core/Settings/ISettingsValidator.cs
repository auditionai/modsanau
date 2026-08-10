namespace AuditionModStudio.Core.Settings;

public interface ISettingsValidator
{
    SettingsValidationResult Validate(ApplicationSettings settings);
}

namespace AuditionModStudio.Infrastructure.Settings;

public sealed class UnsupportedSettingsSchemaException : Exception
{
    public UnsupportedSettingsSchemaException(int? schemaVersion)
        : base(schemaVersion is null
            ? "The settings schema version is missing or invalid."
            : $"Settings schema version {schemaVersion} is not supported.")
    {
        SchemaVersion = schemaVersion;
    }

    public int? SchemaVersion { get; }
}

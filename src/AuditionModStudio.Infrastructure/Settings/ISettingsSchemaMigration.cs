using System.Text.Json.Nodes;

namespace AuditionModStudio.Infrastructure.Settings;

/// <summary>
/// Defines one explicit JSON schema migration step. No legacy migration is registered for schema v1.
/// </summary>
public interface ISettingsSchemaMigration
{
    int SourceVersion { get; }

    int TargetVersion { get; }

    JsonObject Migrate(JsonObject source);
}

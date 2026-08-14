using System.Collections.Immutable;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;

namespace AuditionModStudio.Core.AI;

public readonly record struct PromptPresetId
{
    public const int MaximumLength = 64;

    public PromptPresetId(string value)
    {
        if (!IsValidValue(value)) throw new ArgumentException("Prompt preset IDs use the stable ID grammar.", nameof(value));
        Value = value;
    }

    public string Value { get; }
    public bool IsValid => IsValidValue(Value);
    public override string ToString() => Value ?? string.Empty;

    private static bool IsValidValue(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaximumLength
        && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
}

public enum PromptPresetOrigin { Local, Cloud }

public sealed class PromptPreset
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumNameLength = 128;
    public const int MaximumDescriptionLength = 1_024;
    public const int MaximumTagCount = 32;
    public const int MaximumTagLength = 64;

    public PromptPreset(
        PromptPresetId id,
        int version,
        string name,
        string description,
        AiPrompt prompt,
        AiPrompt? negativePrompt,
        IEnumerable<AiStudioOperation> applicableOperations,
        GameId gameId,
        ModId modId,
        TextureSlotId textureSemanticType,
        IEnumerable<string> tags,
        PromptPresetOrigin origin)
    {
        if (!id.IsValid) throw new ArgumentException("A valid preset ID is required.", nameof(id));
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
        ValidateText(name, MaximumNameLength, false, nameof(name));
        ValidateText(description, MaximumDescriptionLength, true, nameof(description));
        if (!prompt.IsValid || negativePrompt is { IsValid: false }) throw new ArgumentException("Preset prompts are invalid.");
        if (!gameId.IsValid || !modId.IsValid || !textureSemanticType.IsValid) throw new ArgumentException("Preset scope is invalid.");

        ArgumentNullException.ThrowIfNull(applicableOperations);
        var operations = applicableOperations.Distinct().Order().ToImmutableArray();
        if (operations.IsDefaultOrEmpty || operations.Any(value => !Enum.IsDefined(value)))
            throw new ArgumentException("At least one known operation is required.", nameof(applicableOperations));

        ArgumentNullException.ThrowIfNull(tags);
        var normalizedTags = tags.Order(StringComparer.Ordinal).ToImmutableArray();
        if (normalizedTags.Length > MaximumTagCount
            || normalizedTags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > MaximumTagLength || tag.Any(char.IsControl))
            || normalizedTags.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalizedTags.Length)
            throw new ArgumentException("Preset tags are invalid.", nameof(tags));

        Id = id; Version = version; Name = name.Trim(); Description = description.Trim(); Prompt = prompt;
        NegativePrompt = negativePrompt; ApplicableOperations = operations; GameId = gameId; ModId = modId;
        TextureSemanticType = textureSemanticType; Tags = normalizedTags; Origin = origin;
    }

    public PromptPresetId Id { get; }
    public int Version { get; }
    public string Name { get; }
    public string Description { get; }
    public AiPrompt Prompt { get; }
    public AiPrompt? NegativePrompt { get; }
    public ImmutableArray<AiStudioOperation> ApplicableOperations { get; }
    public GameId GameId { get; }
    public ModId ModId { get; }
    public TextureSlotId TextureSemanticType { get; }
    public ImmutableArray<string> Tags { get; }
    public PromptPresetOrigin Origin { get; }

    public bool IsApplicable(AiStudioOperation operation, GameId gameId, ModId modId, TextureSlotId semanticType) =>
        ApplicableOperations.Contains(operation) && GameId == gameId && ModId == modId && TextureSemanticType == semanticType;

    private static void ValidateText(string value, int maximum, bool allowEmpty, string parameter)
    {
        ArgumentNullException.ThrowIfNull(value, parameter);
        if (value.Length > maximum || value.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t')
            || (!allowEmpty && string.IsNullOrWhiteSpace(value)) || (allowEmpty && value.Length > 0 && string.IsNullOrWhiteSpace(value)))
            throw new ArgumentException("Preset text is invalid.", parameter);
    }
}

public sealed record PromptPresetIssue(string DiagnosticCode, PromptPresetId? PresetId = null);
public sealed record PromptPresetCollectionResult(
    bool Succeeded, string DiagnosticCode, ImmutableArray<PromptPreset> Presets, ImmutableArray<PromptPresetIssue> Issues);
public sealed record PromptPresetMutationResult(bool Succeeded, string DiagnosticCode);

public interface ILocalPromptPresetStore
{
    Task<PromptPresetCollectionResult> LoadAsync(CancellationToken cancellationToken = default);
    Task<PromptPresetMutationResult> SaveAsync(PromptPreset preset, CancellationToken cancellationToken = default);
    Task<PromptPresetMutationResult> DeleteAsync(PromptPresetId id, CancellationToken cancellationToken = default);
    Task<PromptPresetCollectionResult> ImportAsync(Stream input, CancellationToken cancellationToken = default);
    Task<PromptPresetMutationResult> ExportAsync(Stream output, CancellationToken cancellationToken = default);
}

public interface ICloudPromptPresetService
{
    Task<PromptPresetCollectionResult> ListOwnedAsync(CancellationToken cancellationToken = default);
}

public static class PromptPresetMerge
{
    public static PromptPresetCollectionResult Merge(IEnumerable<PromptPreset> local, IEnumerable<PromptPreset> cloud)
    {
        ArgumentNullException.ThrowIfNull(local); ArgumentNullException.ThrowIfNull(cloud);
        var issues = ImmutableArray.CreateBuilder<PromptPresetIssue>();
        var merged = ImmutableArray.CreateBuilder<PromptPreset>();
        foreach (var group in local.Concat(cloud).GroupBy(item => item.Id).OrderBy(item => item.Key.Value, StringComparer.Ordinal))
        {
            var latestVersion = group.Max(item => item.Version);
            var candidates = group.Where(item => item.Version == latestVersion).ToArray();
            var distinct = candidates.Select(ContentIdentity).Distinct(StringComparer.Ordinal).ToArray();
            if (distinct.Length != 1)
            {
                issues.Add(new("PROMPT_PRESET_VERSION_CONFLICT", group.Key));
                continue;
            }
            merged.Add(candidates.OrderBy(item => item.Origin).First());
        }
        return new(true, issues.Count == 0 ? "PROMPT_PRESETS_MERGED" : "PROMPT_PRESETS_MERGED_WITH_ISOLATION",
            merged.ToImmutable(), issues.ToImmutable());
    }

    private static string ContentIdentity(PromptPreset value) => string.Join('\u001f',
        value.Name, value.Description, value.Prompt.Value, value.NegativePrompt?.Value ?? string.Empty,
        value.GameId.Value, value.ModId.Value, value.TextureSemanticType.Value,
        string.Join(',', value.ApplicableOperations), string.Join(',', value.Tags));
}

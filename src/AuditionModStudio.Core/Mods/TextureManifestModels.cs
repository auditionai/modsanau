using System.Collections.Immutable;
using AuditionModStudio.Core.Games;

namespace AuditionModStudio.Core.Mods;

public readonly record struct TextureSlotId
{
    public const int MaximumLength = 64;

    public TextureSlotId(string value)
    {
        if (!IsValidValue(value))
        {
            throw new ArgumentException(
                "Texture slot IDs must use 1-64 lowercase ASCII letters, digits, underscores, or hyphens and start with a letter or digit.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public bool IsValid => IsValidValue(Value);

    public static bool TryCreate(string? value, out TextureSlotId id)
    {
        if (!IsValidValue(value))
        {
            id = default;
            return false;
        }

        id = new TextureSlotId(value!);
        return true;
    }

    public override string ToString() => Value ?? string.Empty;

    private static bool IsValidValue(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaximumLength
        && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
}

public readonly record struct TextureCategory
{
    public TextureCategory(string value)
    {
        if (!TextureSlotId.TryCreate(value, out _))
        {
            throw new ArgumentException("Texture categories must use the stable catalog ID grammar.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public bool IsValid => TextureSlotId.TryCreate(Value, out _);

    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct TextureEditMode
{
    public TextureEditMode(string value)
    {
        if (!TextureSlotId.TryCreate(value, out _))
        {
            throw new ArgumentException("Texture edit modes must use the stable catalog ID grammar.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public bool IsValid => TextureSlotId.TryCreate(Value, out _);

    public override string ToString() => Value ?? string.Empty;
}

public sealed class TextureSlot
{
    public const int MaximumDisplayNameLength = 128;
    public const int MaximumDescriptionLength = 2_048;
    public const int MaximumTagCount = 32;
    public const int MaximumTagLength = 64;

    public TextureSlot(
        TextureSlotId id,
        ModRelativePath relativePath,
        string displayName,
        TextureCategory category,
        string description,
        IEnumerable<string> tags,
        bool previewEnabled,
        bool editable,
        TextureEditMode recommendedEditMode)
    {
        if (!id.IsValid)
        {
            throw new ArgumentException("Texture slot requires a valid semantic ID.", nameof(id));
        }

        if (!relativePath.IsValid
            || !string.Equals(Path.GetExtension(relativePath.Value), ".dds", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Texture slot requires a safe relative DDS path.", nameof(relativePath));
        }

        if (!category.IsValid)
        {
            throw new ArgumentException("Texture slot requires a valid category.", nameof(category));
        }

        if (!recommendedEditMode.IsValid)
        {
            throw new ArgumentException("Texture slot requires a valid recommended edit mode.", nameof(recommendedEditMode));
        }

        ValidateDisplayText(displayName, MaximumDisplayNameLength, false, nameof(displayName));
        ValidateDisplayText(description, MaximumDescriptionLength, true, nameof(description));
        ArgumentNullException.ThrowIfNull(tags);
        var immutableTags = tags.ToImmutableArray();
        if (immutableTags.Length > MaximumTagCount
            || immutableTags.Any(tag => string.IsNullOrWhiteSpace(tag)
                || tag.Length > MaximumTagLength
                || tag.Any(char.IsControl))
            || immutableTags.Distinct(StringComparer.OrdinalIgnoreCase).Count() != immutableTags.Length)
        {
            throw new ArgumentException("Texture tags must be bounded, unique display metadata.", nameof(tags));
        }

        Id = id;
        RelativePath = relativePath;
        DisplayName = displayName;
        Category = category;
        Description = description;
        Tags = immutableTags;
        PreviewEnabled = previewEnabled;
        Editable = editable;
        RecommendedEditMode = recommendedEditMode;
    }

    public TextureSlotId Id { get; }
    public ModRelativePath RelativePath { get; }
    public string DisplayName { get; }
    public TextureCategory Category { get; }
    public string Description { get; }
    public ImmutableArray<string> Tags { get; }
    public bool PreviewEnabled { get; }
    public bool Editable { get; }
    public TextureEditMode RecommendedEditMode { get; }

    private static void ValidateDisplayText(string value, int maximumLength, bool allowEmpty, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if ((!allowEmpty && string.IsNullOrWhiteSpace(value))
            || (allowEmpty && value.Length > 0 && string.IsNullOrWhiteSpace(value))
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new ArgumentException("Texture display metadata is invalid or exceeds its limit.", parameterName);
        }
    }
}

public sealed class TextureManifest
{
    private TextureManifest(GameId gameId, ModId modId, ImmutableArray<TextureSlot> slots)
    {
        GameId = gameId;
        ModId = modId;
        Slots = slots;
    }

    public GameId GameId { get; }
    public ModId ModId { get; }
    public ImmutableArray<TextureSlot> Slots { get; }

    public static TextureManifestCreateResult Create(
        GameId gameId,
        ModId modId,
        IEnumerable<TextureSlot?>? slots)
    {
        var issues = ImmutableArray.CreateBuilder<TextureManifestValidationIssue>();
        if (!gameId.IsValid)
        {
            issues.Add(new(TextureManifestValidationFailureReason.InvalidGameId, "TEXTURE_MANIFEST_GAME_ID_INVALID", null));
        }

        if (!modId.IsValid)
        {
            issues.Add(new(TextureManifestValidationFailureReason.InvalidModId, "TEXTURE_MANIFEST_MOD_ID_INVALID", null));
        }

        if (slots is null)
        {
            issues.Add(new(TextureManifestValidationFailureReason.InvalidSlot, "TEXTURE_MANIFEST_SLOTS_MISSING", null));
            return TextureManifestCreateResult.Failure(issues.ToImmutable());
        }

        var validSlots = new List<TextureSlot>();
        var seenIds = new HashSet<TextureSlotId>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var slot in slots)
        {
            if (slot is null)
            {
                issues.Add(new(TextureManifestValidationFailureReason.InvalidSlot, "TEXTURE_MANIFEST_SLOT_NULL", index));
            }
            else
            {
                if (!seenIds.Add(slot.Id))
                {
                    issues.Add(new(TextureManifestValidationFailureReason.DuplicateSlotId, "TEXTURE_MANIFEST_SLOT_ID_DUPLICATE", index));
                }

                if (!seenPaths.Add(slot.RelativePath.Value))
                {
                    issues.Add(new(TextureManifestValidationFailureReason.DuplicateAssetIdentity, "TEXTURE_MANIFEST_ASSET_IDENTITY_DUPLICATE", index));
                }

                validSlots.Add(slot);
            }

            index++;
        }

        if (issues.Count > 0)
        {
            return TextureManifestCreateResult.Failure(issues.ToImmutable());
        }

        var ordered = validSlots.OrderBy(slot => slot.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        return TextureManifestCreateResult.Success(new TextureManifest(gameId, modId, ordered));
    }
}

public enum TextureManifestValidationFailureReason
{
    None,
    InvalidDependency,
    InvalidGameId,
    InvalidModId,
    InvalidManifest,
    InvalidSlot,
    UnknownMod,
    DuplicateManifest,
    DuplicateSlotId,
    DuplicateAssetIdentity
}

public sealed record TextureManifestValidationIssue(
    TextureManifestValidationFailureReason Reason,
    string DiagnosticCode,
    int? Index);

public sealed record TextureManifestCreateResult(
    bool Succeeded,
    ImmutableArray<TextureManifestValidationIssue> Issues,
    TextureManifest? Manifest)
{
    public static TextureManifestCreateResult Success(TextureManifest manifest) => new(true, [], manifest);
    public static TextureManifestCreateResult Failure(ImmutableArray<TextureManifestValidationIssue> issues) => new(false, issues, null);
}

using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Games;

namespace AuditionModStudio.Core.Mods;

public sealed class ModDefinition
{
    public const int MaximumDisplayNameLength = 128;
    public const int MaximumDescriptionLength = 2_048;
    public const int MaximumCompatibilityInformationLength = 1_024;

    public ModDefinition(
        ModId id,
        GameId gameId,
        string displayName,
        ModCategory category,
        ModRelativePath coverImageRelativePath,
        string description,
        AuditionArchiveTemplate archiveTemplate,
        ModKeydatStrategy keydatStrategy,
        string compatibilityInformation)
        : this(id, gameId, displayName, category, coverImageRelativePath, description, archiveTemplate,
            keydatStrategy, null, compatibilityInformation)
    {
    }

    public ModDefinition(
        ModId id,
        GameId gameId,
        string displayName,
        ModCategory category,
        ModRelativePath coverImageRelativePath,
        string description,
        AuditionArchiveTemplate archiveTemplate,
        ModKeydatStrategy keydatStrategy,
        ModRelativePath installRelativePath,
        string compatibilityInformation)
        : this(id, gameId, displayName, category, coverImageRelativePath, description, archiveTemplate,
            keydatStrategy, (ModRelativePath?)installRelativePath, compatibilityInformation)
    {
    }

    private ModDefinition(
        ModId id,
        GameId gameId,
        string displayName,
        ModCategory category,
        ModRelativePath coverImageRelativePath,
        string description,
        AuditionArchiveTemplate archiveTemplate,
        ModKeydatStrategy keydatStrategy,
        ModRelativePath? installRelativePath,
        string compatibilityInformation)
    {
        if (!id.IsValid)
        {
            throw new ArgumentException("Mod definition requires a valid stable ID.", nameof(id));
        }

        if (!gameId.IsValid)
        {
            throw new ArgumentException("Mod definition requires a valid game ID.", nameof(gameId));
        }

        if (!category.IsValid)
        {
            throw new ArgumentException("Mod definition requires a valid category.", nameof(category));
        }

        if (!coverImageRelativePath.IsValid)
        {
            throw new ArgumentException("Mod definition requires a valid cover-image reference.", nameof(coverImageRelativePath));
        }

        if (installRelativePath is { IsValid: false })
        {
            throw new ArgumentException("Mod definition requires a valid install-relative path.", nameof(installRelativePath));
        }

        ValidateDisplayText(displayName, MaximumDisplayNameLength, nameof(displayName));
        ValidateDisplayText(description, MaximumDescriptionLength, nameof(description));
        ValidateDisplayText(
            compatibilityInformation,
            MaximumCompatibilityInformationLength,
            nameof(compatibilityInformation));
        ArgumentNullException.ThrowIfNull(archiveTemplate);

        if (archiveTemplate.ArchiveId.Length > 128
            || archiveTemplate.ArchiveId.Any(char.IsControl)
            || !ModRelativePath.TryCreate(archiveTemplate.SourceRelativePath, out _)
            || !ModRelativePath.TryCreate(archiveTemplate.ExpectedExtractFolderName, out _))
        {
            throw new ArgumentException(
                "Archive template metadata must use bounded IDs and safe relative paths.",
                nameof(archiveTemplate));
        }

        if (archiveTemplate.Identity is not { IsValid: true })
        {
            throw new ArgumentException(
                "Mod archive mappings require a complete template ID, version, SHA-256, and compatible game build.",
                nameof(archiveTemplate));
        }

        if (!Enum.IsDefined(keydatStrategy))
        {
            throw new ArgumentOutOfRangeException(nameof(keydatStrategy));
        }

        Id = id;
        GameId = gameId;
        DisplayName = displayName;
        Category = category;
        CoverImageRelativePath = coverImageRelativePath;
        Description = description;
        ArchiveTemplate = archiveTemplate;
        KeydatStrategy = keydatStrategy;
        InstallRelativePath = installRelativePath;
        CompatibilityInformation = compatibilityInformation;
    }

    public ModId Id { get; }

    public GameId GameId { get; }

    public string DisplayName { get; }

    public ModCategory Category { get; }

    public ModRelativePath CoverImageRelativePath { get; }

    public string Description { get; }

    public AuditionArchiveTemplate ArchiveTemplate { get; }

    public ModKeydatStrategy KeydatStrategy { get; }

    public ModRelativePath? InstallRelativePath { get; }

    public string CompatibilityInformation { get; }

    private static void ValidateDisplayText(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"Display metadata must contain at most {maximumLength} characters and no control characters.",
                parameterName);
        }
    }
}

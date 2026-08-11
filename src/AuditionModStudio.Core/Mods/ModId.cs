using AuditionModStudio.Core.Games;

namespace AuditionModStudio.Core.Mods;

public readonly record struct ModId
{
    public const int MaximumLength = StableCatalogId.MaximumLength;

    public ModId(string value)
    {
        if (!StableCatalogId.IsValid(value))
        {
            throw new ArgumentException(
                "Mod IDs must use 1-64 lowercase ASCII letters, digits, underscores, or hyphens and start with a letter or digit.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public bool IsValid => StableCatalogId.IsValid(Value);

    public static bool TryCreate(string? value, out ModId modId)
    {
        if (!StableCatalogId.IsValid(value))
        {
            modId = default;
            return false;
        }

        modId = new ModId(value!);
        return true;
    }

    public override string ToString() => Value ?? string.Empty;
}

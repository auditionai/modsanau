using AuditionModStudio.Core.Games;

namespace AuditionModStudio.Core.Mods;

public readonly record struct ModCategory
{
    public ModCategory(string value)
    {
        if (!StableCatalogId.IsValid(value))
        {
            throw new ArgumentException(
                "Mod categories must use 1-64 lowercase ASCII letters, digits, underscores, or hyphens and start with a letter or digit.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public bool IsValid => StableCatalogId.IsValid(Value);

    public override string ToString() => Value ?? string.Empty;
}

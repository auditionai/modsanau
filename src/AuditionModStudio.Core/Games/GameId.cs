namespace AuditionModStudio.Core.Games;

public readonly record struct GameId
{
    public const int MaximumLength = StableCatalogId.MaximumLength;

    public GameId(string value)
    {
        if (!StableCatalogId.IsValid(value))
        {
            throw new ArgumentException(
                "Game IDs must use 1-64 lowercase ASCII letters, digits, underscores, or hyphens and start with a letter or digit.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public bool IsValid => StableCatalogId.IsValid(Value);

    public static bool TryCreate(string? value, out GameId gameId)
    {
        if (!StableCatalogId.IsValid(value))
        {
            gameId = default;
            return false;
        }

        gameId = new GameId(value!);
        return true;
    }

    public override string ToString() => Value ?? string.Empty;

}
